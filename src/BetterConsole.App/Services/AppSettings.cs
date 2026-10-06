using System.Text.Json;
using System.Text.Json.Serialization;
using BetterConsole.Core.Server;

namespace BetterConsole.App.Services;

/// <summary>Everything BetterConsole remembers. Saved as settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>The servers. Without <see cref="MultiServer"/> only the first one is used.</summary>
    public List<ServerProfile> Servers { get; set; } = new();

    /// <summary>Multi-console: several servers in one app, switched in the side bar, in one window or several.</summary>
    public bool MultiServer { get; set; }

    /// <summary>The single server of settings files from before multi-console; moved into <see cref="Servers"/> on load.</summary>
    [JsonPropertyName("Server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerProfile? LegacyServer { get; set; }

    /// <summary>The first server (the only one without multi-console).</summary>
    [JsonIgnore]
    public ServerProfile Server => Servers[0];

    /// <summary>The servers that run: all of them in multi-console, else the first.</summary>
    [JsonIgnore]
    public IEnumerable<ServerProfile> ActiveServers => MultiServer ? Servers : Servers.Take(1);

    /// <summary>Editor for Lua files (double-click a path in errors or the profiler): auto, vscode, notepad++, sublime, notepad, system, custom ...</summary>
    public string Editor { get; set; } = "auto";

    /// <summary>The command line of the "custom" editor; {file} and {line} are replaced.</summary>
    public string EditorCommand { get; set; } = "";

    /// <summary>Steam avatars of players (downloaded from steamcommunity.com and cached).</summary>
    public bool ShowAvatars { get; set; } = true;

    public string Theme { get; set; } = "Dark";
    public string ConsoleFont { get; set; } = "Cascadia Mono";
    public double ConsoleFontSize { get; set; } = 13;
    public int ConsoleMaxLines { get; set; } = 20000;
    public bool ConsoleTimestamps { get; set; } = true;
    public bool ConsoleWordWrap { get; set; }
    /// <summary>Keep Lua errors out of the console tab (they have their own tabs).</summary>
    public bool HideErrorsInConsole { get; set; } = true;
    /// <summary>Treat errors that differ only in entity indices / table addresses as one.</summary>
    public bool MergeSimilarErrors { get; set; } = true;
    /// <summary>Hide client-only commands (bind, cl_*, mat_*...) from auto-completion.</summary>
    public bool CompleteServerCommandsOnly { get; set; } = true;
    public int MaxErrorsPerList { get; set; } = 500;

    public List<string> PlayerColumnsHidden { get; set; } = ["IP", "In", "Out", "Choke", "Team", "Score"];
    /// <summary>Widths of the Players columns the user changed ("150", "2.4*", "Auto"), by header.</summary>
    public Dictionary<string, string> PlayerColumnSizes { get; set; } = new();
    /// <summary>Order of the Players columns (headers), when the user moved them.</summary>
    public List<string> PlayerColumnOrder { get; set; } = new();
    public string PlayerSortColumn { get; set; } = "Time";
    public bool PlayerSortDescending { get; set; } = true;

    public int StatsWindowMinutes { get; set; } = 5;

    /// <summary>Tab headers: "show" (icon and title), "icons" (icon only) or "auto" (icon only when the titles do not fit).</summary>
    public string TabTitles { get; set; } = "auto";

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }

    public bool ConfirmExitWhileRunning { get; set; } = true;
    public List<string> DisabledPlugins { get; set; } = new();

    [JsonIgnore]
    public string? FilePath { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Window position is NaN until the window was moved once.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>
    /// The data folder: next to the exe when it is writable (portable, one folder per server), else
    /// %APPDATA%\BetterConsole. <paramref name="profile"/> selects settings.&lt;profile&gt;.json so one
    /// copy of the app can run several servers.
    /// </summary>
    public static string DataDirectory { get; private set; } = AppContext.BaseDirectory;

    public static AppSettings Load(string? profile)
    {
        DataDirectory = ChooseDataDirectory();
        string file = Path.Combine(DataDirectory, string.IsNullOrWhiteSpace(profile) ? "settings.json" : $"settings.{profile}.json");
        AppSettings s;
        try
        {
            s = File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions) ?? new() : new();
        }
        catch
        {
            // A broken file is kept aside instead of being overwritten.
            try { File.Copy(file, file + ".broken", true); } catch { }
            s = new();
        }
        s.FilePath = file;
        s.Normalize();
        return s;
    }

    /// <summary>At least one server, every one with its own id.</summary>
    private void Normalize()
    {
        if (LegacyServer != null)
        {
            if (Servers.Count == 0) Servers.Add(LegacyServer);
            LegacyServer = null;
        }
        Servers.RemoveAll(p => p == null);
        if (Servers.Count == 0) Servers.Add(new ServerProfile());
        var ids = new HashSet<string>();
        foreach (var p in Servers)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id))
            {
                p.Id = ServerProfile.NewId();
                ids.Add(p.Id);
            }
            p.RestartTimes ??= new();
            p.RestartWarningMinutes ??= new();
            p.Arguments ??= "";
            p.Priority ??= "Normal";
            p.RestartWarningText ??= "";
            p.Name ??= "";
            p.LastHostname ??= "";
        }
        PlayerColumnSizes ??= new();
        PlayerColumnOrder ??= new();
        PlayerColumnsHidden ??= new();
    }

    public void Save()
    {
        if (FilePath == null) return;
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            Log.Write("settings save failed: " + ex.Message);
        }
    }

    private static string ChooseDataDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        try
        {
            var probe = Path.Combine(exeDir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return exeDir;
        }
        catch
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterConsole");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

/// <summary>Plain text log in logs\betterconsole.log (kept small).</summary>
public static class Log
{
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                var dir = Path.Combine(AppSettings.DataDirectory, "logs");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "betterconsole.log");
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 2 * 1024 * 1024) File.Move(file, file + ".1", true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>Command history of the console input, kept across sessions.</summary>
public sealed class CommandHistory
{
    private readonly List<string> _items = new();
    private readonly string _file;
    private const int Max = 500;

    public CommandHistory(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file)) _items.AddRange(File.ReadAllLines(file).Where(l => l.Length > 0).TakeLast(Max));
        }
        catch { }
    }

    public IReadOnlyList<string> Items => _items;

    public void Add(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return;
        _items.Remove(command);
        _items.Add(command);
        if (_items.Count > Max) _items.RemoveRange(0, _items.Count - Max);
        try { File.WriteAllLines(_file, _items); } catch { }
    }
}
