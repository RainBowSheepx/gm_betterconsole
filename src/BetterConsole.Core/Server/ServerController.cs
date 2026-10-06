using System.Text;
using BetterConsole.Core.Bridge;
using BetterConsole.Core.Console;
using BetterConsole.Core.Pty;
using BetterConsole.Sdk;

namespace BetterConsole.Core.Server;

/// <summary>Process-level numbers, sampled once a second while the server runs.</summary>
public sealed record ProcessSnapshot(DateTime Time, double CpuPercent, long PrivateBytes, long WorkingSet, int Threads, int Handles, TimeSpan Uptime);

public enum LifecycleKind { Started, Stopped, Crashed, Exited, StartFailed }

/// <summary>
/// The server started or stopped, and why: "Start button", "Scheduled restart (05:00)", a crash with its
/// exit code, a quit from an addon or rcon. Written to the start / stop journal.
/// </summary>
public sealed record LifecycleEvent(DateTime Time, LifecycleKind Kind, string Reason, int? ExitCode = null, TimeSpan? Uptime = null);

/// <summary>
/// Owns the srcds process: start, stop, restart, crash detection and auto-restart, command input.
/// Console output goes into <see cref="Pipeline"/>; the Lua addon talks through <see cref="Bridge"/>.
/// Events are raised on background threads.
/// </summary>
public sealed class ServerController : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly ProcessSampler _sampler = new();
    private readonly Timer _sampleTimer;
    private readonly List<DateTime> _crashTimes = new();
    private PseudoConsoleProcess? _process;
    private volatile bool _stopRequested;
    // Stop, Kill or closing BetterConsole: not even Always run starts it again ("quit" in the console is not final).
    private volatile bool _stopFinal;
    // After DisposeAsync: nothing starts any more (a restart that was still waiting).
    private volatile bool _disposed;
    private volatile bool _startCancelled;
    private DateTime _startedAt;
    private CancellationTokenSource? _restartCts;
    // Why the process is being stopped (set with _stopRequested), and what was seen to make it quit by
    // itself (a "quit" run by an addon, rcon) shortly before it did.
    private string _stopReason = "Stopped";
    private (string Text, DateTime At)? _shutdownCause;

    // srcds reads all pending console input in a frame, takes the first line and drops the rest:
    // lines are typed one at a time, the next one after srcds echoed the previous one.
    private readonly System.Threading.Channels.Channel<(string Text, bool Internal, string? Display)> _input =
        System.Threading.Channels.Channel.CreateUnbounded<(string, bool, string?)>();
    private readonly object _echoLock = new();
    private readonly LinkedList<(string Text, TaskCompletionSource Done)> _echoWaiters = new();
    // Internal commands queued or waiting for their echo: the same one is not queued twice.
    private readonly HashSet<string> _internalQueued = new();

    /// <summary>
    /// How long a typed line may wait for its echo before the next one is typed. While srcds loads a
    /// map it reads no input at all, and a line typed next to an unread one is lost.
    /// </summary>
    private static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(30);

    public ServerController(ServerProfile profile)
    {
        Profile = profile;
        Pipeline = new ConsolePipeline();
        Bridge = new BridgeServer();
        Bridge.Start();
        _sampleTimer = new Timer(_ => Sample(), null, 1000, 1000);
        Pipeline.EchoMatched += OnEcho;
        _ = Task.Run(InputLoop);
    }

    private void OnEcho(string text)
    {
        lock (_echoLock)
        {
            for (var n = _echoWaiters.First; n != null; n = n.Next)
            {
                if (n.Value.Text != text) continue;
                n.Value.Done.TrySetResult();
                _echoWaiters.Remove(n);
                return;
            }
        }
    }

    private async Task InputLoop()
    {
        var reader = _input.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                var text = item.Text.TrimEnd();
                try
                {
                    PseudoConsoleProcess? p;
                    lock (_lock) p = _process;
                    if (p == null || p.HasExited) continue;
                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_echoLock) _echoWaiters.AddLast((text, done));
                    Pipeline.ExpectEcho(text, item.Internal, item.Display);
                    p.WriteInput(text + "\r");
                    // Normally the echo comes within a frame; during a map load the input waits.
                    var deadline = DateTime.UtcNow + EchoTimeout;
                    while (!done.Task.IsCompleted && !p.HasExited && DateTime.UtcNow < deadline)
                        await Task.WhenAny(done.Task, Task.Delay(250)).ConfigureAwait(false);
                    lock (_echoLock)
                    {
                        for (var n = _echoWaiters.First; n != null; n = n.Next)
                            if (n.Value.Done == done) { _echoWaiters.Remove(n); break; }
                    }
                }
                finally
                {
                    if (item.Internal && item.Display == null) lock (_echoLock) _internalQueued.Remove(text);
                }
            }
        }
    }

    /// <summary>
    /// Types a command whose echo stays hidden (BetterConsole's own helpers). Nothing happens while
    /// the same command still waits in the queue.
    /// </summary>
    public void TypeInternal(string command)
    {
        command = command.TrimEnd();
        lock (_lock) if (_process == null) return;
        lock (_echoLock) if (!_internalQueued.Add(command)) return;
        Type(command, isInternal: true);
    }

    /// <summary>Queues a line for the console input (see <see cref="InputLoop"/>).</summary>
    private void Type(string text, bool isInternal, string? display = null) => _input.Writer.TryWrite((text, isInternal, display));

    /// <summary>A new process: drop what was typed for the old one.</summary>
    private void ClearInput()
    {
        while (_input.Reader.TryRead(out _)) { }
        lock (_echoLock)
        {
            foreach (var w in _echoWaiters) w.Done.TrySetResult();
            _echoWaiters.Clear();
            _internalQueued.Clear();
        }
    }

    public ServerProfile Profile { get; set; }
    public ConsolePipeline Pipeline { get; }
    public BridgeServer Bridge { get; }
    public ServerState State { get; private set; } = ServerState.Stopped;
    public int? ProcessId => _process is { HasExited: false } p ? p.ProcessId : null;
    public DateTime StartedAt => _startedAt;
    public string? ExecutablePath { get; private set; }

    /// <summary>Called right before the process starts (install the companion addon here). Throw to cancel the start.</summary>
    public Func<ServerProfile, string, Task>? BeforeStart { get; set; }

    public event Action<ServerState, ServerState, int?>? StateChanged;
    /// <summary>Started, stopped, crashed, with the reason (for the journal).</summary>
    public event Action<LifecycleEvent>? Lifecycle;
    public event Action<ProcessSnapshot>? Sampled;
    /// <summary>A command that srcds does not echo (it went through the pipe): show it yourself.</summary>
    public event Action<string>? CommandEcho;
    /// <summary>A notice for the console tab (server started, crashed, ...). The bool marks an error.</summary>
    public event Action<string, bool>? Notice;

    private void SetState(ServerState s, int? exitCode = null)
    {
        ServerState old;
        lock (_lock)
        {
            old = State;
            if (old == s) return;
            State = s;
        }
        StateChanged?.Invoke(old, s, exitCode);
    }

    private void Report(LifecycleKind kind, string reason, int? exitCode = null, TimeSpan? uptime = null) =>
        Lifecycle?.Invoke(new LifecycleEvent(DateTime.Now, kind, reason, exitCode, uptime));

    /// <summary>
    /// Something that is about to make the server quit by itself: a "quit" an addon or a player ran, an
    /// rcon command. Used as the reason when the process exits within a short while.
    /// </summary>
    public void NoteShutdownCause(string text) => _shutdownCause = (text, DateTime.Now);

    /// <summary>The "quit" that was noted did not happen (GMod refused it).</summary>
    public void ClearShutdownCause() => _shutdownCause = null;

    /// <summary>Applies the profile's CPU affinity and priority to the running process. Returns the problem, or null.</summary>
    public string? ApplyProcessSettings()
    {
        var pid = ProcessId;
        if (pid == null) return null;
        return ProcessTuning.Apply(pid.Value, Profile.AffinityMask, Profile.Priority);
    }

    public Task StartAsync(string reason = "Started") => StartCoreAsync(reason, automatic: false);

    /// <summary>A start that failed on its own (Always run, an auto-restart): with Always run it is tried again.</summary>
    private void StartFailed(string reason, bool automatic)
    {
        if (automatic && Profile.AlwaysRun && !_disposed) ScheduleRestart(reason, countsAsCrash: true);
    }

    private async Task StartCoreAsync(string reason, bool automatic)
    {
        lock (_lock)
        {
            if (_disposed || State is ServerState.Starting or ServerState.Running or ServerState.Stopping) return;
        }
        var exe = Profile.ResolveExecutable();
        if (exe == null)
        {
            var problem = string.IsNullOrWhiteSpace(Profile.ServerDirectory)
                ? "No server folder is set. Open Settings and choose the folder that contains srcds.exe."
                : $"No srcds_console.exe / srcds_console_win64.exe in \"{Profile.ServerDirectory}\".";
            Notice?.Invoke(problem, true);
            Report(LifecycleKind.StartFailed, $"{reason}: {problem}");
            StartFailed(reason, automatic);
            return;
        }
        // Checked and set under one lock: two quick starts must not run two servers.
        ServerState old;
        lock (_lock)
        {
            if (_disposed || State is ServerState.Starting or ServerState.Running or ServerState.Stopping) return;
            _restartCts?.Cancel();
            old = State;
            State = ServerState.Starting;
            _startCancelled = false;
        }
        StateChanged?.Invoke(old, ServerState.Starting, null);
        ExecutablePath = exe;
        try
        {
            if (BeforeStart != null) await BeforeStart(Profile, exe).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Notice?.Invoke("Could not prepare the server: " + ex.Message, true);
            SetState(ServerState.Stopped);
            Report(LifecycleKind.StartFailed, $"{reason}: could not prepare the server ({ex.Message})");
            StartFailed(reason, automatic);
            return;
        }
        if (_startCancelled)
        {
            Notice?.Invoke("Start cancelled.", false);
            SetState(ServerState.Stopped);
            Report(LifecycleKind.StartFailed, $"{reason}: cancelled ({_stopReason})");
            return;
        }

        Pipeline.Reset();
        _sampler.Reset();
        ClearInput();
        _stopRequested = false;
        _stopFinal = false;
        _shutdownCause = null;
        var args = Profile.Arguments ?? "";
        if (!args.Contains("-console", StringComparison.OrdinalIgnoreCase)) args = "-console " + args;

        try
        {
            var env = new Dictionary<string, string> { [BridgeServer.EnvironmentVariable] = Bridge.PipeName };
            var p = PseudoConsoleProcess.Start(exe, args, Profile.ServerDirectory, env, ConsolePipeline.Width, ConsolePipeline.Height);
            p.OutputReceived += Pipeline.Post;
            p.Exited += code => OnExited(p, code);
            lock (_lock) _process = p;
            _startedAt = DateTime.Now;
            Notice?.Invoke($"Starting {Path.GetFileName(exe)} {args}", false);
            if (!ProcessTuning.IsAll(Profile.AffinityMask) || Profile.Priority != "Normal")
            {
                var problem = ProcessTuning.Apply(p.ProcessId, Profile.AffinityMask, Profile.Priority);
                Notice?.Invoke(problem == null
                    ? $"CPU: {ProcessTuning.Describe(Profile.AffinityMask)} · priority {ProcessTuning.PriorityText(Profile.Priority)}."
                    : "Could not set CPU affinity / priority: " + problem, problem != null);
            }
            SetState(ServerState.Running);
            Report(LifecycleKind.Started, reason);
        }
        catch (Exception ex)
        {
            Notice?.Invoke("Could not start the server: " + ex.Message, true);
            SetState(ServerState.Stopped);
            Report(LifecycleKind.StartFailed, $"{reason}: {ex.Message}");
            StartFailed(reason, automatic);
        }
    }

    private void OnExited(PseudoConsoleProcess p, int code)
    {
        lock (_lock)
        {
            if (_process != p) return;
            _process = null;
        }
        // Closing the pseudo console can take a moment; the new state is reported first.
        try { Exited(code); }
        finally { p.Dispose(); }
    }

    private void Exited(int code)
    {
        var uptime = DateTime.Now - _startedAt;
        bool always = Profile.AlwaysRun;
        if (_stopRequested)
        {
            Notice?.Invoke($"Server stopped (exit code {code}, up {FormatSpan(uptime)}).", false);
            SetState(ServerState.Stopped, code);
            Report(LifecycleKind.Stopped, _stopReason, code, uptime);
            // "quit" typed in the console stops it for good, unless it has to run always (only Stop,
            // Kill and closing BetterConsole are final then).
            if (!_stopFinal && always) ScheduleRestart("Always run: started again after \"quit\" in the console", countsAsCrash: false);
            return;
        }

        // Exit code 0 is a "quit" from somewhere else (an addon, rcon): restarted like a crash, as a
        // restart script would.
        var cause = _shutdownCause is { } sc && (DateTime.Now - sc.At).TotalSeconds < 45 ? sc.Text : null;
        Notice?.Invoke(code == 0
            ? $"The server quit by itself after {FormatSpan(uptime)} (exit code 0{(cause != null ? ": " + cause : "")})."
            : $"Server exited unexpectedly with code {FormatExitCode(code)} after {FormatSpan(uptime)}.", code != 0);
        SetState(ServerState.Crashed, code);
        if (code == 0) Report(LifecycleKind.Exited, cause ?? "Quit by itself (rcon, a module or the engine ran \"quit\")", code, uptime);
        else Report(LifecycleKind.Crashed, "Crash: exit code " + FormatExitCode(code), code, uptime);
        if (!Profile.AutoRestart && !always) return;
        var prefix = always ? "Always run" : "Auto-restart";
        ScheduleRestart(code == 0 ? $"{prefix} after the server quit" : $"{prefix} after the crash", countsAsCrash: true);
    }

    /// <summary>
    /// Starts the server again after the delay of the profile. More than 5 crashes in 10 minutes pause
    /// auto-restart; with Always run the wait grows instead (up to 5 minutes) and it keeps trying.
    /// Start, Stop and Kill cancel a pending restart.
    /// </summary>
    private void ScheduleRestart(string reason, bool countsAsCrash)
    {
        var delay = Math.Clamp(Profile.RestartDelaySeconds, 0, 600);
        if (countsAsCrash)
        {
            var now = DateTime.Now;
            _crashTimes.Add(now);
            _crashTimes.RemoveAll(t => (now - t).TotalMinutes > 10);
            if (_crashTimes.Count > 5)
            {
                if (!Profile.AlwaysRun)
                {
                    Notice?.Invoke("The server crashed more than 5 times in 10 minutes. Auto-restart is paused; start it by hand.", true);
                    return;
                }
                delay = Math.Min(300, Math.Max(delay, 5) * (1 << Math.Min(6, _crashTimes.Count - 5)));
                Notice?.Invoke($"The server crashed {_crashTimes.Count} times in 10 minutes; Always run tries again in {delay} s.", true);
            }
        }
        Notice?.Invoke($"Restarting in {delay} s...", false);
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _restartCts?.Cancel();
            _restartCts = cts;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ConfigureAwait(false);
                if (State is ServerState.Crashed or ServerState.Stopped) await StartCoreAsync(reason, automatic: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Asks srcds to quit, kills it after the timeout.</summary>
    public async Task StopAsync(string reason = "Stopped")
    {
        _stopReason = reason;
        PseudoConsoleProcess? p;
        lock (_lock)
        {
            _restartCts?.Cancel();
            p = _process;
        }
        if (p == null)
        {
            if (State == ServerState.Starting)
            {
                // Still preparing (installing the companion): StartAsync stops there.
                _startCancelled = true;
                var startDeadline = DateTime.UtcNow.AddSeconds(10);
                while (State == ServerState.Starting && DateTime.UtcNow < startDeadline) await Task.Delay(50).ConfigureAwait(false);
                lock (_lock) p = _process;
                if (p == null) return;
            }
            else
            {
                // Nothing runs; a crashed server waiting for its auto-restart just stays down.
                if (State == ServerState.Crashed) SetState(ServerState.Stopped);
                return;
            }
        }
        _stopRequested = true;
        _stopFinal = true;
        SetState(ServerState.Stopping);
        Type("quit", isInternal: false);
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(Profile.StopTimeoutSeconds, 3, 300));
        while (!p.HasExited && DateTime.UtcNow < deadline) await Task.Delay(100).ConfigureAwait(false);
        if (!p.HasExited)
        {
            Notice?.Invoke("The server did not quit in time; killing it.", true);
            _stopReason = reason + " (killed: it did not quit in time)";
            p.Kill();
            var killDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!p.HasExited && DateTime.UtcNow < killDeadline) await Task.Delay(50).ConfigureAwait(false);
        }
        // OnExited switches the state.
        var stateDeadline = DateTime.UtcNow.AddSeconds(2);
        while (State == ServerState.Stopping && DateTime.UtcNow < stateDeadline) await Task.Delay(50).ConfigureAwait(false);
    }

    public async Task RestartAsync(string reason = "Restart")
    {
        await StopAsync(reason).ConfigureAwait(false);
        await StartAsync(reason).ConfigureAwait(false);
    }

    /// <summary>Kills the process at once (no "quit").</summary>
    public void Kill(string reason = "Killed")
    {
        PseudoConsoleProcess? p;
        lock (_lock) p = _process;
        if (p == null) return;
        _stopReason = reason;
        _stopRequested = true;
        _stopFinal = true;
        p.Kill();
    }

    /// <summary>
    /// Runs a console command. srcds' console input only accepts ASCII, so commands with other
    /// characters go through the companion addon (hex-encoded, executed by the engine).
    /// Returns false when the command could not be delivered.
    /// </summary>
    public bool SendCommand(string command, out string? problem)
    {
        problem = null;
        PseudoConsoleProcess? p;
        lock (_lock) p = _process;
        if (p == null)
        {
            problem = "The server is not running.";
            return false;
        }
        command = command.Replace("\r", "").Replace("\n", " ").Trim();
        if (command.Length == 0) return true;

        bool ascii = true;
        foreach (char c in command)
        {
            if (c < 0x20 || c > 0x7E) { ascii = false; break; }
        }
        if (ascii && command.Length < 250)
        {
            // "quit" typed here is a stop, not a crash to restart from.
            var verb = command.Split(' ', 2)[0];
            if (verb.Equals("quit", StringComparison.OrdinalIgnoreCase) || verb.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock) _restartCts?.Cancel();
                _stopReason = $"\"{verb.ToLowerInvariant()}\" typed in the console";
                _stopRequested = true;
                _stopFinal = false;
            }
            Type(command, isInternal: false);
            return true;
        }

        if (!Bridge.IsConnected)
        {
            problem = ascii
                ? "The command is too long for the srcds console (250 characters) and the companion addon is not connected."
                : "srcds' console only accepts ASCII. Commands with other characters need the companion addon, which is not connected.";
            return false;
        }
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(command)).ToLowerInvariant();
        var line = "betterconsole_exec " + hex;
        if (line.Length < 250)
        {
            Type(line, isInternal: true, display: command);
        }
        else
        {
            // Too long for the console input line: through the pipe (runs on the next frame).
            // srcds echoes nothing for it, so the echo is ours.
            Bridge.Send("exec", new { cmd = command });
            CommandEcho?.Invoke(command);
        }
        return true;
    }

    private void Sample()
    {
        PseudoConsoleProcess? p;
        lock (_lock) p = _process;
        if (p == null || p.HasExited) return;
        try
        {
            _sampler.Sample(p.ProcessId, p.GetCpuTime());
            Sampled?.Invoke(new ProcessSnapshot(DateTime.Now, _sampler.CpuPercent, _sampler.PrivateBytes, _sampler.WorkingSet,
                _sampler.Threads, _sampler.Handles, DateTime.Now - _startedAt));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("sample: " + ex);
        }
    }

    public static string FormatSpan(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
        : $"{t.Seconds}s";

    public static string FormatExitCode(int code)
    {
        uint u = unchecked((uint)code);
        string? name = u switch
        {
            0xC0000005 => "access violation",
            0xC00000FD => "stack overflow",
            0xC0000409 => "stack buffer overrun / fail-fast",
            0xC0000374 => "heap corruption",
            0xC000001D => "illegal instruction",
            0x80000003 => "breakpoint",
            0xC0000094 => "integer divide by zero",
            0x40010004 => "killed by the console closing",
            0xFFFFFFFF => "ended by another program, e.g. Task Manager",
            _ => null,
        };
        return u >= 0x40000000 ? $"0x{u:X8}{(name != null ? $" ({name})" : "")}" : code.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _sampleTimer.Dispose();
        _disposed = true;
        lock (_lock) _restartCts?.Cancel();
        PseudoConsoleProcess? p;
        lock (_lock) p = _process;
        if (p != null)
        {
            _stopReason = "BetterConsole was closed";
            _stopRequested = true;
            _stopFinal = true;
            p.Kill();
            p.Dispose();
        }
        await Bridge.DisposeAsync().ConfigureAwait(false);
        Pipeline.Dispose();
    }
}
