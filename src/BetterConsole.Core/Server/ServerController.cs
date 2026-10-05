using System.Text;
using BetterConsole.Core.Bridge;
using BetterConsole.Core.Console;
using BetterConsole.Core.Pty;
using BetterConsole.Sdk;

namespace BetterConsole.Core.Server;

/// <summary>Process-level numbers, sampled once a second while the server runs.</summary>
public sealed record ProcessSnapshot(DateTime Time, double CpuPercent, long PrivateBytes, long WorkingSet, int Threads, int Handles, TimeSpan Uptime);

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
    private bool _stopRequested;
    private DateTime _startedAt;
    private CancellationTokenSource? _restartCts;

    public ServerController(ServerProfile profile)
    {
        Profile = profile;
        Pipeline = new ConsolePipeline();
        Bridge = new BridgeServer();
        Bridge.Start();
        _sampleTimer = new Timer(_ => Sample(), null, 1000, 1000);
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
    public event Action<ProcessSnapshot>? Sampled;
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

    public async Task StartAsync()
    {
        lock (_lock)
        {
            if (State is ServerState.Starting or ServerState.Running or ServerState.Stopping) return;
            _restartCts?.Cancel();
        }
        var exe = Profile.ResolveExecutable();
        if (exe == null)
        {
            Notice?.Invoke(string.IsNullOrWhiteSpace(Profile.ServerDirectory)
                ? "No server folder is set. Open Settings and choose the folder that contains srcds.exe."
                : $"No srcds_console.exe / srcds_console_win64.exe in \"{Profile.ServerDirectory}\".", true);
            return;
        }
        ExecutablePath = exe;
        SetState(ServerState.Starting);
        try
        {
            if (BeforeStart != null) await BeforeStart(Profile, exe).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Notice?.Invoke("Could not prepare the server: " + ex.Message, true);
            SetState(ServerState.Stopped);
            return;
        }

        Pipeline.Reset();
        _sampler.Reset();
        _stopRequested = false;
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
            SetState(ServerState.Running);
        }
        catch (Exception ex)
        {
            Notice?.Invoke("Could not start the server: " + ex.Message, true);
            SetState(ServerState.Stopped);
        }
    }

    private void OnExited(PseudoConsoleProcess p, int code)
    {
        lock (_lock)
        {
            if (_process != p) return;
            _process = null;
        }
        p.Dispose();
        var uptime = DateTime.Now - _startedAt;
        if (_stopRequested)
        {
            Notice?.Invoke($"Server stopped (exit code {code}, up {FormatSpan(uptime)}).", false);
            SetState(ServerState.Stopped, code);
            return;
        }

        Notice?.Invoke($"Server exited unexpectedly with code {FormatExitCode(code)} after {FormatSpan(uptime)}.", true);
        SetState(ServerState.Crashed, code);
        if (!Profile.AutoRestart) return;

        var now = DateTime.Now;
        _crashTimes.Add(now);
        _crashTimes.RemoveAll(t => (now - t).TotalMinutes > 10);
        if (_crashTimes.Count > 5)
        {
            Notice?.Invoke("The server crashed more than 5 times in 10 minutes. Auto-restart is paused; start it by hand.", true);
            return;
        }
        var delay = Math.Clamp(Profile.RestartDelaySeconds, 0, 600);
        Notice?.Invoke($"Restarting in {delay} s...", false);
        var cts = new CancellationTokenSource();
        lock (_lock) _restartCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ConfigureAwait(false);
                if (State == ServerState.Crashed) await StartAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Asks srcds to quit, kills it after the timeout.</summary>
    public async Task StopAsync()
    {
        PseudoConsoleProcess? p;
        lock (_lock)
        {
            _restartCts?.Cancel();
            p = _process;
        }
        if (p == null)
        {
            // Nothing runs; a crashed server waiting for its auto-restart just stays down.
            if (State == ServerState.Crashed) SetState(ServerState.Stopped);
            return;
        }
        _stopRequested = true;
        SetState(ServerState.Stopping);
        Pipeline.ExpectEcho("quit", isInternal: false);
        p.WriteInput("quit\r");
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(Profile.StopTimeoutSeconds, 3, 300));
        while (!p.HasExited && DateTime.UtcNow < deadline) await Task.Delay(100).ConfigureAwait(false);
        if (!p.HasExited)
        {
            Notice?.Invoke("The server did not quit in time; killing it.", true);
            p.Kill();
            var killDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!p.HasExited && DateTime.UtcNow < killDeadline) await Task.Delay(50).ConfigureAwait(false);
        }
        // OnExited switches the state.
        var stateDeadline = DateTime.UtcNow.AddSeconds(2);
        while (State == ServerState.Stopping && DateTime.UtcNow < stateDeadline) await Task.Delay(50).ConfigureAwait(false);
    }

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    /// <summary>Kills the process at once (no "quit").</summary>
    public void Kill()
    {
        PseudoConsoleProcess? p;
        lock (_lock) p = _process;
        if (p == null) return;
        _stopRequested = true;
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
            Pipeline.ExpectEcho(command, isInternal: false);
            p.WriteInput(command + "\r");
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
            Pipeline.ExpectEcho(line, isInternal: true);
            p.WriteInput(line + "\r");
        }
        else
        {
            // Too long for the console input line: through the pipe (runs on the next frame).
            Bridge.Send("exec", new { cmd = command });
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
            _ => null,
        };
        return u >= 0x40000000 ? $"0x{u:X8}{(name != null ? $" ({name})" : "")}" : code.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _sampleTimer.Dispose();
        PseudoConsoleProcess? p;
        lock (_lock) p = _process;
        if (p != null)
        {
            _stopRequested = true;
            p.Kill();
            p.Dispose();
        }
        await Bridge.DisposeAsync().ConfigureAwait(false);
        Pipeline.Dispose();
    }
}
