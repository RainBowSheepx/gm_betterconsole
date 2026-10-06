using System.Globalization;
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
    // Code run from the console is "lua_run:1:" (or "RunString:1:", "LuaCmd:1:").
    [GeneratedRegex(@"^\[(?<addon>[^\]|]{1,120})\] (?<msg>(?:[^:]*?\.lua|lua_run|RunString(?:Ex)?|LuaCmd):-?\d+: .*)$")]
    private static partial Regex AddonErrorHead();

    // "  1. unknown - addons/x/lua/autorun/foo.lua:12" (deeper frames are indented further)
    [GeneratedRegex(@"^\s+(?<n>\d+)\.\s+(?<fn>.*?) - (?<src>.*?)(?::(?<line>-?\d+))?\s*$")]
    private static partial Regex StackLine();

    [GeneratedRegex(@"^Timer Failed! \[.*\]\[.*\]\s*$")]
    private static partial Regex TimerFailed();

    // Lua's own traceback (debug.traceback, luaL_traceback), in a message given to ErrorNoHalt or printed
    // by code that runs Lua outside the game's state:
    //   stack traceback:
    //       [C]: in function 'error'
    //       addons/x/lua/y.lua:12: in function 'name'      ("in main chunk", "in local 'f'", "in function <file:line>")
    [GeneratedRegex(@"^\s*stack traceback:\s*$")]
    private static partial Regex TracebackHead();

    [GeneratedRegex(@"^\s+(?<src>\[C\]|\S[^:]*?)(?::(?<line>-?\d+))?: in (?<what>.+?)\s*$")]
    private static partial Regex TracebackLine();

    [GeneratedRegex(@"^(?:function|local|upvalue|method|field|global) '(?<name>[^']+)'$")]
    private static partial Regex TracebackName();

    [GeneratedRegex(@"^\[(?<name>.*)\|(?<uid>\d+)\|(?<sid>[^\]|]*)\] Lua Error:\s*$")]
    private static partial Regex ClientHead();

    // What the engine names as a timer's function when it is the companion's timer wrapper.
    [GeneratedRegex(@"\[@[^\[\]]*betterconsole/sv_timers\.lua \(line -?\d+\)\]")]
    private static partial Regex WrapperTimerSource();

    // The "chunk:line: " a Lua error message starts with ("lua_run:1: ", "addons/x/lua/y.lua:12: ").
    [GeneratedRegex(@"^\S.*?:-?\d+: ")]
    private static partial Regex LuaLocation();

    /// <summary>Lines of a multi-line error message taken before the stack, at most.</summary>
    private const int MaxMessageLines = 8;

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

    /// <summary>
    /// While on (the detailed capture of lag spikes), the engine profiler's reports are taken out of the
    /// console and handed over as <see cref="VprofCaptured"/>, with the "Saved report to" line after them.
    /// </summary>
    public bool CaptureVprof { get; set; }

    /// <summary>The frame length vprof_dump_spikes reports from (ms); reports of shorter frames are left in the console.</summary>
    public double VprofMinFrameMs { get; set; }

    private List<string>? _vprof;
    // The first lines of a report, until it says what it covers (shown again when it is not a spike's).
    private List<(long Id, ConsoleLine Line)>? _vprofHead;
    private DateTime _vprofSince;
    private (VprofReport? Report, DateTime At)? _vprofDone;

    [GeneratedRegex(@"^Saved report to ""(?<path>[^""]+)""")]
    private static partial Regex VprofSaved();

    [GeneratedRegex(@"^(?<n>\d+) frames sampled")]
    private static partial Regex VprofFrames();

    [GeneratedRegex(@"^Average [\d.]+ fps, (?<ms>[\d.]+) ms per frame")]
    private static partial Regex VprofAverage();

    private bool VprofLine(long id, ConsoleLine line)
    {
        var text = line.Text.TrimEnd();
        // A report comes in one burst: one still open after seconds lost its END line. Stop hiding the console.
        if (_vprof != null && (line.Time - _vprofSince).TotalSeconds > 2) (_vprof, _vprofHead) = (null, null);
        if (_vprof != null)
        {
            if (_vprofHead != null)
            {
                // vprof_dump_spikes reports one frame, longer than its limit. Anything else is a report somebody
                // asked for (vprof_generate_report typed, through rcon, from Lua …): it stays in the console, and
                // so does its file.
                var fm = VprofFrames().Match(text);
                var am = VprofAverage().Match(text);
                bool theirs = fm.Success ? fm.Groups["n"].Value != "1"
                    : am.Success ? !double.TryParse(am.Groups["ms"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms < VprofMinFrameMs * 0.95
                    : _vprofHead.Count >= 8;
                if (theirs)
                {
                    foreach (var (hid, hl) in _vprofHead)
                    {
                        _hidden.Remove(hid);
                        Show(hid, hl);
                    }
                    (_vprof, _vprofHead) = (null, null);
                    return false;
                }
                if (am.Success) _vprofHead = null;
                else _vprofHead.Add((id, line));
            }
            HideAlways(id);
            if (text == VprofReport.End)
            {
                _vprofDone = (VprofReport.Parse(_vprof), line.Time);
                _vprof = null;
            }
            else if (_vprof.Count < 4000) _vprof.Add(text);
            return true;
        }
        if (_vprofDone is { } done)
        {
            _vprofDone = null;
            var m = VprofSaved().Match(text);
            _emit(new VprofCaptured(done.Report, done.At, m.Success ? m.Groups["path"].Value : null));
            if (m.Success)
            {
                HideAlways(id);
                return true;
            }
        }
        if (CaptureVprof && text == VprofReport.Begin)
        {
            // The engine reports at the end of the frame: right after an error of that frame, whose
            // lines are all out by then (the classifier still waits a moment for more of them).
            if (_mode == Mode.InError) FinishError();
            if (_mode == Mode.AfterError)
            {
                EmitFinished();
                _mode = Mode.Normal;
            }
            ReleaseHeldBlank();
            _vprof = new List<string>();
            _vprofHead = [(id, line)];
            _vprofSince = line.Time;
            HideAlways(id);
            return true;
        }
        return false;
    }

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
        // A report whose "Saved report to" line did not follow.
        if (_vprofDone is { } vd && (now - vd.At).TotalMilliseconds > 500)
        {
            _vprofDone = null;
            _emit(new VprofCaptured(vd.Report, vd.At, null));
        }
    }

    private DateTime _clientHeadAt;

    private void Process(long id, ConsoleLine line)
    {
        if (VprofLine(id, line)) return;
        string text = line.Text;
        bool blank = string.IsNullOrWhiteSpace(text);

        if (_mode == Mode.InError)
        {
            if (TracebackHead().IsMatch(text))
            {
                _error!.InTraceback = true;
                _error.SawStack = true;
                _error.LastLineAt = line.Time;
                HideError(id, line);
                return;
            }
            if (_error!.InTraceback)
            {
                var t = TracebackLine().Match(text);
                if (t.Success)
                {
                    var what = t.Groups["what"].Value;
                    var nm = TracebackName().Match(what);
                    var fn = nm.Success ? nm.Groups["name"].Value : what == "main chunk" ? "main chunk" : "unknown";
                    _error.Frames.Add(new StackFrame(fn, t.Groups["src"].Value,
                        t.Groups["line"].Success && int.TryParse(t.Groups["line"].Value, out int tl) ? tl : 0));
                    _error.FromTraceback = true;
                    _error.LastLineAt = line.Time;
                    HideError(id, line);
                    return;
                }
                _error.InTraceback = false;
            }
            var m = StackLine().Match(text);
            if (m.Success)
            {
                _error.SawStack = true;
                // After a Lua traceback in the message GMod's stack only shows where ErrorNoHalt was called.
                if (!_error.FromTraceback)
                    _error.Frames.Add(new StackFrame(m.Groups["fn"].Value, m.Groups["src"].Value,
                        m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out int ln) ? ln : 0));
                _error.LastLineAt = line.Time;
                HideError(id, line);
                return;
            }
            if (blank)
            {
                if (FinishError())
                {
                    HideError(id, line);
                    return;
                }
            }
            else if (!_error!.SawStack && _error.MessageLines.Count < MaxMessageLines
                     && (line.Time - _error.LastLineAt).TotalMilliseconds < 50
                     && !ErrorHead().IsMatch(text) && !AddonErrorHead().IsMatch(text) && !ClientHead().IsMatch(text))
            {
                // Maybe a multi-line error message: the rest of the message comes before the stack.
                // Decided when the error ends (see FinishError).
                _error.MessageLines.Add((id, line));
                _error.LastLineAt = line.Time;
                HideError(id, line);
                return;
            }
            else FinishError();
        }

        if (_mode == Mode.AfterError)
        {
            if (TimerFailed().IsMatch(text) && _finished != null)
            {
                var context = text.Trim();
                // Under the timer detour the engine sees the wrapper; the outermost frame is the real function.
                if (_finished.Stack.Count > 0 && WrapperTimerSource().IsMatch(context))
                {
                    var f = _finished.Stack[^1];
                    context = WrapperTimerSource().Replace(context, $"[@{f.Source} (line {f.Line})]");
                }
                _finished = _finished with { Context = context };
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
            var blankBefore = DropHeldBlank();
            var player = _clientHead;
            _clientHead = null;
            _error = head.Success
                ? new ErrorBuilder(head.Groups["msg"].Value, line.Time, player)
                : new ErrorBuilder(addonHead.Groups["msg"].Value, line.Time, player) { Addon = addonHead.Groups["addon"].Value };
            if (head.Success && player == null) _error.PlainHead = (id, line, blankBefore);
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

    /// <returns>False when it was not an error after all; its lines are back in the console then.</returns>
    private bool FinishError()
    {
        if (_error == null) return false;
        var e = _error;
        _error = null;
        EmitFinished();
        var message = e.Message;
        if (!e.SawStack)
        {
            // Without a stack the lines after the head were just output that followed it quickly, and an
            // "[ERROR] ..." line without a Lua location is somebody's print, not a Lua error.
            bool print = e.PlainHead != null && !LuaLocation().IsMatch(e.Message);
            if (print)
            {
                var ph = e.PlainHead!.Value;
                if (ph.BlankBefore is { } bb) Unhide(bb.Id, bb.Line);
                Unhide(ph.Id, ph.Line);
            }
            foreach (var (lid, l) in e.MessageLines) Unhide(lid, l);
            if (print)
            {
                _mode = Mode.Normal;
                return false;
            }
        }
        else if (e.MessageLines.Count > 0)
        {
            message += "\n" + string.Join("\n", e.MessageLines.Select(l => l.Line.Text));
        }
        _finished = new LuaError
        {
            Realm = e.Player != null ? LuaRealm.Client : LuaRealm.Server,
            Message = message,
            Stack = WithoutWrappers(e.Frames),
            Time = e.StartedAt,
            Player = e.Player,
            AddonTitle = e.Addon,
            Source = ErrorSource.ConsoleText,
        };
        _mode = Mode.AfterError;
        // srcds prints one to three blank lines after an error (three after a client's).
        _blanksToEat = 3;
        _afterErrorSince = _clock();
        return true;
    }

    /// <summary>
    /// The companion's profiler and timer wrappers are not part of anybody's bug: their frames go,
    /// unless the bug is the companion's own (its code is the innermost Lua frame).
    /// </summary>
    private static StackFrame[] WithoutWrappers(List<StackFrame> frames)
    {
        var first = frames.FirstOrDefault(f => f.Source != "[C]");
        if (first == null || first.Source.Contains("betterconsole/", StringComparison.Ordinal)) return frames.ToArray();
        return frames.Where(f => !f.Source.Contains("betterconsole/sv_", StringComparison.Ordinal)).ToArray();
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

    /// <summary>A line taken for part of an error that was not: shown after all, in its place.</summary>
    private void Unhide(long id, ConsoleLine line)
    {
        if (!HideErrors) return; // shown already
        _hidden.Remove(id);
        Show(id, line);
    }

    private (long Id, ConsoleLine Line)? DropHeldBlank()
    {
        (long, ConsoleLine)? dropped = null;
        if (_heldBlank is { } hb)
        {
            if (HideErrors) HideAlways(hb.Id);
            else Show(hb.Id, hb.Line);
            dropped = (hb.Id, hb.Line);
        }
        _heldBlank = null;
        return dropped;
    }

    private void ReleaseHeldBlank()
    {
        if (_heldBlank is { } hb) Show(hb.Id, hb.Line);
        _heldBlank = null;
    }

    private sealed class ErrorBuilder(string message, DateTime at, PlayerRef? player)
    {
        public readonly string Message = message;
        public readonly List<(long Id, ConsoleLine Line)> MessageLines = new();
        /// <summary>Set for an "[ERROR]" head of a server error: it is a print unless a location or a stack follows.</summary>
        public (long Id, ConsoleLine Line, (long Id, ConsoleLine Line)? BlankBefore)? PlainHead;
        public readonly List<StackFrame> Frames = new();
        public bool SawStack;
        /// <summary>Inside a "stack traceback:" block.</summary>
        public bool InTraceback;
        /// <summary>The frames came from a Lua traceback (they win over GMod's stack lines after it).</summary>
        public bool FromTraceback;
        public readonly DateTime StartedAt = at;
        public DateTime LastLineAt = at;
        public readonly PlayerRef? Player = player;
        public string? Addon;
    }
}
