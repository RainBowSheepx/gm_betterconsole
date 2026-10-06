using System.Text.Json;
using System.Windows;

namespace BetterConsole.Sdk;

/// <summary>Everything the host exposes to a plugin.</summary>
public interface IPluginContext
{
    /// <summary>The managed srcds process.</summary>
    IServer Server { get; }

    /// <summary>The console output stream (after Lua errors are taken out of it).</summary>
    IConsoleOutput Console { get; }

    /// <summary>The message channel to the companion Lua addon running inside the server.</summary>
    ILuaBridge Bridge { get; }

    /// <summary>Tabs, status bar, notifications.</summary>
    IUiHost Ui { get; }

    /// <summary>A folder that belongs to this plugin (created on first access): <c>plugins-data\&lt;Id&gt;</c>.</summary>
    string DataDirectory { get; }

    /// <summary>Writes to the application log (<c>logs\betterconsole.log</c>).</summary>
    void Log(string message);
}

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
}

public sealed class ServerStateChangedEventArgs(ServerState oldState, ServerState newState, int? exitCode) : EventArgs
{
    public ServerState OldState { get; } = oldState;
    public ServerState NewState { get; } = newState;

    /// <summary>The exit code when the process has just ended, else null.</summary>
    public int? ExitCode { get; } = exitCode;
}

/// <summary>A snapshot of the numbers shown in the status bar and on the statistics tab.</summary>
public sealed record ServerSnapshot
{
    public DateTime Time { get; init; }
    /// <summary>CPU usage of the srcds process, computed like the engine's <c>stats</c> command (100 = one core).</summary>
    public double CpuPercent { get; init; }
    public long PrivateBytes { get; init; }
    public long WorkingSet { get; init; }
    public int Threads { get; init; }
    public TimeSpan Uptime { get; init; }

    // Values below come from the Lua addon and are NaN / -1 while it is not connected.
    public double ServerFps { get; init; } = double.NaN;
    public double FrameVarMs { get; init; } = double.NaN;
    public double TickRate { get; init; } = double.NaN;
    public double TicksPerSecond { get; init; } = double.NaN;
    public double FrameMs { get; init; } = double.NaN;
    public double FrameMaxMs { get; init; } = double.NaN;
    public double GameThreadLoad { get; init; } = double.NaN;
    public double NetInKBps { get; init; } = double.NaN;
    public double NetOutKBps { get; init; } = double.NaN;
    public int Players { get; init; } = -1;
    public int Bots { get; init; } = -1;
    public int MaxPlayers { get; init; } = -1;
    public int Entities { get; init; } = -1;
    public int Edicts { get; init; } = -1;
    public double LuaMemoryMB { get; init; } = double.NaN;
    public string? Map { get; init; }
    public string? Hostname { get; init; }
}

public interface IServer
{
    ServerState State { get; }
    event EventHandler<ServerStateChangedEventArgs>? StateChanged;

    /// <summary>Process id of srcds while it runs.</summary>
    int? ProcessId { get; }

    /// <summary>The <c>garrysmod</c> folder of the configured server.</summary>
    string GameDirectory { get; }

    /// <summary>Latest numbers (updated about once per second).</summary>
    ServerSnapshot? Latest { get; }
    event EventHandler<ServerSnapshot>? SnapshotUpdated;

    /// <summary>Runs a console command. Non-ASCII commands are routed through the companion addon.</summary>
    void SendCommand(string command);

    Task StartAsync();
    Task StopAsync();
    Task RestartAsync();
}

/// <summary>One finished line of console output.</summary>
public sealed class ConsoleLine
{
    public required string Text { get; init; }
    public required DateTime Time { get; init; }

    /// <summary>Colour runs over <see cref="Text"/>. Empty means the default colour for the whole line.</summary>
    public IReadOnlyList<ColorSpan> Spans { get; init; } = Array.Empty<ColorSpan>();

    public ConsoleLineKind Kind { get; init; }
}

public enum ConsoleLineKind
{
    /// <summary>Normal server output.</summary>
    Output,
    /// <summary>The echo of a command that was typed in BetterConsole.</summary>
    Command,
    /// <summary>A line written by BetterConsole itself (server started, stopped, ...).</summary>
    App,
    /// <summary>An app line that reports a problem.</summary>
    AppError,
}

/// <summary>A run of characters with one colour. <see cref="Argb"/> == 0 means "default colour".</summary>
public readonly record struct ColorSpan(int Start, int Length, uint Argb, bool Bold = false);

public sealed class ConsoleLineEventArgs(ConsoleLine line) : EventArgs
{
    public ConsoleLine Line { get; } = line;
}

public interface IConsoleOutput
{
    /// <summary>Raised for every line that reaches the console tab.</summary>
    event EventHandler<ConsoleLineEventArgs>? LineReceived;

    /// <summary>Writes a line into the console tab (not into the server).</summary>
    void WriteLine(string text, uint argb = 0);
}

/// <summary>A message from the Lua addon: <c>BetterConsole.SendToApp(type, data)</c> on the server.</summary>
public sealed class BridgeMessage(string type, JsonElement data) : EventArgs
{
    public string Type { get; } = type;
    public JsonElement Data { get; } = data;
}

public interface ILuaBridge
{
    bool IsConnected { get; }
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>Raised for every message from the server; built-in types are delivered too.</summary>
    event EventHandler<BridgeMessage>? MessageReceived;

    /// <summary>
    /// Sends a message to the server. In Lua it arrives in
    /// <c>hook.Add("BetterConsoleMessage", id, function(type, data) end)</c>.
    /// </summary>
    void Send(string type, object? data = null);
}

public interface IUiHost
{
    /// <summary>Adds a tab. <paramref name="content"/> is created lazily, the first time the tab is shown.</summary>
    void AddTab(string id, string header, Func<FrameworkElement> content, int order = 1000);

    /// <summary>
    /// Adds a tab with an icon: a glyph of Segoe Fluent Icons (for example "") or a short text. The
    /// icon is what the tab shows when the app shows tab icons only.
    /// </summary>
    void AddTab(string id, string header, string icon, Func<FrameworkElement> content, int order = 1000);

    void RemoveTab(string id);

    /// <summary>Adds a text item to the status bar. Change its properties later through the returned handle.</summary>
    IStatusItem AddStatusItem(string id, string text, string? tooltip = null, int order = 1000);

    /// <summary>Shows a short toast in the corner of the window.</summary>
    void Notify(string message, NotifyKind kind = NotifyKind.Info);

    /// <summary>The current theme name, e.g. "Dark".</summary>
    string ThemeName { get; }
    event EventHandler<string>? ThemeChanged;
}

public enum NotifyKind { Info, Success, Warning, Error }

public interface IStatusItem
{
    string Text { get; set; }
    string? Tooltip { get; set; }
    bool Visible { get; set; }
    /// <summary>Optional colour of the text, 0 = theme default.</summary>
    uint Argb { get; set; }
}
