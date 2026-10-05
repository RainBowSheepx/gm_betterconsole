using System.Text.RegularExpressions;
using BetterConsole.Core.Errors;
using BetterConsole.Sdk;

namespace BetterConsole.Core.Console;

/// <summary>
/// Sits between the terminal and the console tab. Takes Lua errors out of the text stream (they get
/// their own tabs), recognises the echo of commands we typed, and swallows the blank lines srcds
/// prints around an error. Needs one line of look-ahead for blank lines, so it can hold one line.
/// </summary>
/// <remarks>
/// What srcds prints for a server error:
/// <code>
/// (blank)
/// [ERROR] lua_run:1: attempt to index local 'x' (a nil value)
///   1. unknown - lua_run:1
/// (blank)
/// Timer Failed! [Simple][@lua_run (line 1)]     (only for timers)
/// (blank)
/// </code>
/// An error inside an addon starts with the addon's name instead of "[ERROR]":
/// "[my-addon] addons/my-addon/lua/autorun/x.lua:12: attempt to ...". Compile errors have no stack.
/// A client error relayed to the server starts with "[Nick|userid|STEAM_0:1:2] Lua Error:", a blank
/// line, then the same block; it is followed by up to three blank lines.
/// </remarks>
internal sealed partial class OutputClassifier
{
    [GeneratedRegex(@"^\[ERROR\] (?<msg>.*)$")]
    private static partial Regex ErrorHead();

    // "[addon name] path/to/file.lua:123: message" - the path is what tells it from an ordinary "[tag] text" print.
    [GeneratedRegex(@"^\[(?<addon>[^\]|]{1,120})\] (?<msg>[^:]*?\.lua:-?\d+: .*)$")]
    private static partial Regex AddonErrorHead();

    // "  1. unknown - addons/x/lua/autorun/foo.lua:12" (deeper frames are indented further)
    [GeneratedRegex(@"^\s+(?<n>\d+)\.\s+(?<fn>.*?) - (?<src>.*?)(?::(?<line>-?\d+))?\s*$")]
    private static partial Regex StackLine();

    [GeneratedRegex(@"^Timer Failed! \[.*\]\[.*\]\s*$")]
    private static partial Regex TimerFailed();

    [GeneratedRegex(@"^\[(?<name>.*)\|(?<uid>\d+)\|(?<sid>[^\]|]*)\] Lua Error:\s*$")]
    private static partial Regex ClientHead();

    private enum Mode { Normal, InError, AfterError }

    private readonly Action<ConsoleEvent> _emit;
    private readonly Func<DateTime> _clock;
    private readonly PendingCommands _commands;

    private Mode _mode;
    private ErrorBuilder? _error;
    private LuaError? _finished;
    private DateTime _afterErrorSince;
    private int _blanksToEat;
    private PlayerRef? _clientHead;
    private int _clientHeadBlanks;

    private (long Id, ConsoleLine Line, DateTime At)? _heldBlank;
    private readonly HashSet<long> _hidden = new();
    private readonly Queue<long> _hiddenOrder = new();

    public OutputClassifier(Action<ConsoleEvent> emit, PendingCommands commands, Func<DateTime>? clock = null)
    {
        _emit = emit;
        _commands = commands;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>When false, Lua errors stay in the console text (they are still reported).</summary>
    public bool HideErrors { get; set; } = true;

    public void Add(long id, ConsoleLine line, bool isAmend)
    {
        if (isAmend)
        {
            if (_hidden.Contains(id)) return;
            if (_heldBlank is { } hb && hb.Id == id)
            {
                // The held blank line got text after all: treat it as a fresh line.
                _heldBlank = null;
                Process(id, line);
                return;
            }
            _emit(new LineAmended(id, line));
            return;
        }
        Process(id, line);
    }

    /// <summary>Releases held lines and finishes errors once nothing followed them for a while. Call periodically.</summary>
    public void Tick()
    {
        var now = _clock();
        if (_mode == Mode.InError && _error != null && (now - _error.LastLineAt).TotalMilliseconds > 400)
        {
            FinishError();
        }
        if (_mode == Mode.AfterError && (now - _afterErrorSince).TotalMilliseconds > 400)
        {
            EmitFinished();
            _mode = Mode.Normal;
        }
        if (_heldBlank is { } hb && (now - hb.At).TotalMilliseconds > 250 && _mode == Mode.Normal)
        {
            _heldBlank = null;
            Show(hb.Id, hb.Line);
        }
        if (_clientHead != null && (now - _clientHeadAt).TotalMilliseconds > 1000) _clientHead = null;
    }

    private DateTime _clientHeadAt;

    private void Process(long id, ConsoleLine line)
    {
        string text = line.Text;
        bool blank = string.IsNullOrWhiteSpace(text);

        if (_mode == Mode.InError)
        {
            var m = StackLine().Match(text);
            if (m.Success)
            {
                _error!.Frames.Add(new StackFrame(m.Groups["fn"].Value, m.Groups["src"].Value,
                    m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out int ln) ? ln : 0));
                _error.LastLineAt = line.Time;
                HideError(id, line);
                return;
            }
            if (blank)
            {
                FinishError();
                HideError(id, line);
                return;
            }
            if (_error!.Frames.Count == 0 && (line.Time - _error.LastLineAt).TotalMilliseconds < 50 && !ErrorHead().IsMatch(text) && !AddonErrorHead().IsMatch(text) && !ClientHead().IsMatch(text))
            {
                // A multi-line error message: the rest of the message comes before the stack.
                _error.Message += "\n" + text;
                _error.LastLineAt = line.Time;
                HideError(id, line);
                return;
            }
            FinishError();
        }

        if (_mode == Mode.AfterError)
        {
            if (TimerFailed().IsMatch(text) && _finished != null)
            {
                _finished = _finished with { Context = text.Trim() };
                _blanksToEat = 1;
                _afterErrorSince = _clock();
                HideError(id, line);
                return;
            }
            if (blank && _blanksToEat > 0)
            {
                _blanksToEat--;
                HideError(id, line);
                return;
            }
            EmitFinished();
            _mode = Mode.Normal;
        }

        var head = ErrorHead().Match(text);
        var addonHead = head.Success ? Match.Empty : AddonErrorHead().Match(text);
        if (head.Success || addonHead.Success)
        {
            DropHeldBlank();
            var player = _clientHead;
            _clientHead = null;
            _error = head.Success
                ? new ErrorBuilder(head.Groups["msg"].Value, line.Time, player)
                : new ErrorBuilder(addonHead.Groups["msg"].Value, line.Time, player) { Addon = addonHead.Groups["addon"].Value };
            _mode = Mode.InError;
            HideError(id, line);
            return;
        }

        var ch = ClientHead().Match(text);
        if (ch.Success)
        {
            DropHeldBlank();
            _clientHead = new PlayerRef(ch.Groups["name"].Value, ch.Groups["sid"].Value, "",
                int.TryParse(ch.Groups["uid"].Value, out int uid) ? uid : 0);
            _clientHeadAt = _clock();
            _clientHeadBlanks = 0;
            HideError(id, line);
            return;
        }
        if (_clientHead != null)
        {
            // Lines between the client header and its "[ERROR]" line: blanks are eaten, anything else
            // means the error text came without the usual prefix.
            if (blank && _clientHeadBlanks < 2)
            {
                _clientHeadBlanks++;
                HideError(id, line);
                return;
            }
            if (!blank)
            {
                // Not an "[ERROR]" / "[addon] file.lua:1:" line (checked above): take it as the message.
                _error = new ErrorBuilder(text.Trim(), line.Time, _clientHead);
                _clientHead = null;
                _mode = Mode.InError;
                HideError(id, line);
                return;
            }
            _clientHead = null;
        }

        if (_commands.TryMatchEcho(text, out bool internalCommand, out var display))
        {
            ReleaseHeldBlank();
            if (internalCommand)
            {
                if (display != null)
                {
                    // The transport line of a non-ASCII command: show what the user typed instead.
                    Show(id, new ConsoleLine { Text = display, Time = line.Time, Kind = ConsoleLineKind.Command });
                    return;
                }
                HideAlways(id);
                return;
            }
            Show(id, new ConsoleLine { Text = text, Time = line.Time, Spans = line.Spans, Kind = ConsoleLineKind.Command });
            return;
        }

        if (blank)
        {
            ReleaseHeldBlank();
            _heldBlank = (id, line, _clock());
            return;
        }

        ReleaseHeldBlank();
        Show(id, line);
    }

    private void FinishError()
    {
        if (_error == null) return;
        var e = _error;
        _error = null;
        EmitFinished();
        _finished = new LuaError
        {
            Realm = e.Player != null ? LuaRealm.Client : LuaRealm.Server,
            Message = e.Message,
            Stack = e.Frames.ToArray(),
            Time = e.StartedAt,
            Player = e.Player,
            AddonTitle = e.Addon,
            Source = ErrorSource.ConsoleText,
        };
        _mode = Mode.AfterError;
        // srcds prints one to three blank lines after an error (three after a client's).
        _blanksToEat = 3;
        _afterErrorSince = _clock();
    }

    private void EmitFinished()
    {
        if (_finished == null) return;
        _emit(new ErrorRecognized(_finished));
        _finished = null;
    }

    private void Show(long id, ConsoleLine line) => _emit(new LineAdded(id, line));

    /// <summary>A line that belongs to a Lua error: hidden unless the user wants errors in the console.</summary>
    private void HideError(long id, ConsoleLine line)
    {
        if (HideErrors) HideAlways(id);
        else Show(id, line);
    }

    private void HideAlways(long id)
    {
        _hidden.Add(id);
        _hiddenOrder.Enqueue(id);
        while (_hiddenOrder.Count > 512) _hidden.Remove(_hiddenOrder.Dequeue());
    }

    private void DropHeldBlank()
    {
        if (_heldBlank is { } hb)
        {
            if (HideErrors) HideAlways(hb.Id);
            else Show(hb.Id, hb.Line);
        }
        _heldBlank = null;
    }

    private void ReleaseHeldBlank()
    {
        if (_heldBlank is { } hb) Show(hb.Id, hb.Line);
        _heldBlank = null;
    }

    private sealed class ErrorBuilder(string message, DateTime at, PlayerRef? player)
    {
        public string Message = message;
        public readonly List<StackFrame> Frames = new();
        public readonly DateTime StartedAt = at;
        public DateTime LastLineAt = at;
        public readonly PlayerRef? Player = player;
        public string? Addon;
    }
}
