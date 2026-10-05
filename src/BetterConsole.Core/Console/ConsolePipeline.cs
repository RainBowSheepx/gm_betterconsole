using System.Collections.Concurrent;
using System.Text;
using BetterConsole.Core.Terminal;
using BetterConsole.Sdk;

namespace BetterConsole.Core.Console;

/// <summary>
/// Turns the raw ConPTY byte stream into console events on a dedicated thread:
/// bytes → UTF-8 → terminal emulator → logical lines → classifier → <see cref="Events"/>.
/// The UI drains <see cref="Events"/> on its own schedule, so a flood of output never queues
/// thousands of dispatcher calls.
/// </summary>
public sealed class ConsolePipeline : IDisposable
{
    private readonly BlockingCollection<(byte[] Data, DateTime At)> _chunks = new(new ConcurrentQueue<(byte[], DateTime)>(), 4096);
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly PendingCommands _commands = new();
    private readonly OemTextRestorer _restorer = new();
    private readonly char[] _charBuf = new char[64 * 1024 + 16];

    private Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private TerminalEmulator _terminal = null!;
    private LineAssembler _assembler = null!;
    private OutputClassifier _classifier = null!;
    private DateTime _chunkTime;
    private string? _lastPreview;
    private DateTime _lastData = DateTime.MinValue;
    private volatile bool _resetRequested;
    private volatile bool _hideErrors = true;

    public const short Width = 4096;
    public const short Height = 30;

    public ConsolePipeline()
    {
        CreateState();
        _thread = new Thread(Run) { IsBackground = true, Name = "console pipeline" };
        _thread.Start();
    }

    /// <summary>Output for the UI, in order.</summary>
    public ConcurrentQueue<ConsoleEvent> Events { get; } = new();

    /// <summary>Raised (on the pipeline thread) after new events were queued.</summary>
    public event Action? EventsQueued;

    public bool HideErrors
    {
        get => _hideErrors;
        set => _hideErrors = value;
    }

    /// <summary>Feeds raw pty output. Thread safe; copies the data.</summary>
    public void Post(byte[] buffer, int count)
    {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        try { _chunks.Add((copy, DateTime.Now), _cts.Token); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Remembers a command we are about to type so its echo can be recognised.</summary>
    public void ExpectEcho(string command, bool isInternal, string? display = null) => _commands.Add(command, isInternal, display);

    /// <summary>srcds echoed a typed command, i.e. it has read that input line (pipeline thread).</summary>
    public event Action<string>? EchoMatched
    {
        add => _commands.EchoMatched += value;
        remove => _commands.EchoMatched -= value;
    }

    /// <summary>Starts from a clean screen (a new server process).</summary>
    public void Reset() => _resetRequested = true;

    private void CreateState()
    {
        _utf8 = Encoding.UTF8.GetDecoder();
        _assembler = new LineAssembler(_restorer);
        _terminal = new TerminalEmulator(Width, Height, _assembler);
        _assembler.Terminal = _terminal;
        _classifier = new OutputClassifier(Emit, _commands);
        _lastPreview = null;
    }

    private bool _queued;

    private void Emit(ConsoleEvent e)
    {
        Events.Enqueue(e);
        _queued = true;
    }

    private void Run()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            bool got;
            (byte[] Data, DateTime At) chunk = default;
            try
            {
                got = _chunks.TryTake(out chunk, 60, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_resetRequested)
            {
                _resetRequested = false;
                if (_lastPreview != null) Emit(new PreviewChanged(null));
                CreateState();
            }
            _classifier.HideErrors = _hideErrors;

            if (got)
            {
                _chunkTime = chunk.At;
                _lastData = DateTime.Now;
                Feed(chunk.Data);
                // Drain whatever else is already waiting before publishing.
                while (_chunks.TryTake(out chunk))
                {
                    _chunkTime = chunk.At;
                    Feed(chunk.Data);
                }
                // A finished line clears the preview at once; an unfinished one is shown after a pause.
                // A preview already on screen follows the line it shows (or the next one) right away.
                var preview = _assembler.Preview(_terminal);
                if (_lastPreview != null && preview?.Text != _lastPreview)
                {
                    _lastPreview = preview?.Text;
                    Emit(new PreviewChanged(preview == null ? null : new ConsoleLine
                    {
                        Text = preview.Value.Text,
                        Spans = preview.Value.Spans,
                        Time = _chunkTime,
                    }));
                }
            }
            else if ((DateTime.Now - _lastData).TotalMilliseconds > 120)
            {
                var preview = _assembler.Preview(_terminal);
                string? text = preview?.Text;
                if (text != _lastPreview)
                {
                    _lastPreview = text;
                    Emit(new PreviewChanged(preview == null ? null : new ConsoleLine
                    {
                        Text = preview.Value.Text,
                        Spans = preview.Value.Spans,
                        Time = _chunkTime,
                    }));
                }
            }

            _classifier.Tick();
            if (_queued)
            {
                _queued = false;
                EventsQueued?.Invoke();
            }
        }
    }

    private void Feed(byte[] data)
    {
        int chars = _utf8.GetChars(data, 0, data.Length, _charBuf, 0, flush: false);
        _terminal.Feed(_charBuf.AsSpan(0, chars));
        _assembler.CheckAmendments(_terminal);

        if (_assembler.TitleChanged)
        {
            _assembler.TitleChanged = false;
            if (_assembler.Title != null) Emit(new TitleChanged(_assembler.Title));
        }

        var lines = _assembler.TakeLines();
        foreach (var l in lines)
        {
            var line = new ConsoleLine { Text = l.Text, Spans = l.Spans, Time = _chunkTime };
            _classifier.Add(l.Id, line, l.IsAmend);
        }
        lines.Clear();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(1000);
        _chunks.Dispose();
        _cts.Dispose();
    }
}
