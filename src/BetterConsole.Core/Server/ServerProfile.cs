using System.Text.Json;

namespace BetterConsole.Core.Server;

/// <summary>How to start one dedicated server. Saved in settings.json.</summary>
public sealed class ServerProfile
{
    /// <summary>Stable id (journal, window placement, files of this server).</summary>
    public string Id { get; set; } = NewId();

    /// <summary>Shown in the server list; empty = the hostname of the server.</summary>
    public string Name { get; set; } = "";

    /// <summary>The folder that holds srcds.exe / srcds_console.exe.</summary>
    public string ServerDirectory { get; set; } = "";

    /// <summary>"auto", or a file name in <see cref="ServerDirectory"/> such as srcds_console_win64.exe.</summary>
    public string Executable { get; set; } = "auto";

    /// <summary>Command line arguments for srcds (without the executable).</summary>
    public string Arguments { get; set; } = "-console -game garrysmod +maxplayers 16 +gamemode sandbox +map gm_construct";

    /// <summary>Restart the server when it exits without being asked to.</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>
    /// Keep the server running whatever stopped it: a crash, a quit from the game, rcon or an addon,
    /// "quit" typed in the console. Never gives up after repeated crashes (waits longer instead) and
    /// starts with BetterConsole. Only Stop, Kill and closing BetterConsole leave it off.
    /// </summary>
    public bool AlwaysRun { get; set; }

    public int RestartDelaySeconds { get; set; } = 5;

    /// <summary>Copy the companion addon and module into the server before every start.</summary>
    public bool InstallCompanion { get; set; } = true;

    /// <summary>Start the server as soon as BetterConsole opens.</summary>
    public bool StartWithApp { get; set; }

    /// <summary>Seconds to wait for "quit" before the process is killed.</summary>
    public int StopTimeoutSeconds { get; set; } = 20;

    /// <summary>Logical processors the server may run on, bit n = processor n. 0 = all of them.</summary>
    public ulong AffinityMask { get; set; }

    /// <summary>Priority class of the process: Idle, BelowNormal, Normal, AboveNormal or High.</summary>
    public string Priority { get; set; } = "Normal";

    /// <summary>Times of day ("05:00") the server is restarted at.</summary>
    public List<string> RestartTimes { get; set; } = new();

    /// <summary>Minutes before a scheduled restart at which players are warned in the chat.</summary>
    public List<int> RestartWarningMinutes { get; set; } = [5, 1];

    /// <summary>The warning, said in the chat; {time} becomes "5 minutes", "1 minute", "30 seconds".</summary>
    public string RestartWarningText { get; set; } = "The server restarts in {time}.";

    /// <summary>The hostname the server had last time (its name in the list before it reports it again).</summary>
    public string LastHostname { get; set; } = "";

    /// <summary>The server's own window, when it was taken out of the main one.</summary>
    public WindowPlacement? Window { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string GameDirectory => Path.Combine(ServerDirectory, "garrysmod");

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    private static readonly JsonSerializerOptions CloneOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public ServerProfile Clone() => JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(this, CloneOptions), CloneOptions)!;

    /// <summary>Takes over the settings of an edited copy (not the id and the window).</summary>
    public void CopyFrom(ServerProfile o)
    {
        Name = o.Name;
        ServerDirectory = o.ServerDirectory;
        Executable = o.Executable;
        Arguments = o.Arguments;
        AutoRestart = o.AutoRestart;
        AlwaysRun = o.AlwaysRun;
        RestartDelaySeconds = o.RestartDelaySeconds;
        InstallCompanion = o.InstallCompanion;
        StartWithApp = o.StartWithApp;
        StopTimeoutSeconds = o.StopTimeoutSeconds;
        AffinityMask = o.AffinityMask;
        Priority = o.Priority;
        RestartTimes = new List<string>(o.RestartTimes);
        RestartWarningMinutes = new List<int>(o.RestartWarningMinutes);
        RestartWarningText = o.RestartWarningText;
    }

    /// <summary>
    /// The console-subsystem executable to run. srcds.exe / srcds_win64.exe are GUI programs that
    /// would open a console window of their own, so their *_console twins are used.
    /// </summary>
    public string? ResolveExecutable()
    {
        if (string.IsNullOrWhiteSpace(ServerDirectory) || !Directory.Exists(ServerDirectory)) return null;
        string Map(string name) => name.ToLowerInvariant() switch
        {
            "srcds.exe" => "srcds_console.exe",
            "srcds_win64.exe" => "srcds_console_win64.exe",
            _ => name,
        };
        if (!string.IsNullOrWhiteSpace(Executable) && !Executable.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var p = Path.Combine(ServerDirectory, Map(Executable));
            return File.Exists(p) ? p : null;
        }
        foreach (var name in new[] { "srcds_console_win64.exe", "srcds_console.exe" })
        {
            var p = Path.Combine(ServerDirectory, name);
            if (File.Exists(p))
            {
                // A 64-bit console host is pointless without the 64-bit engine.
                if (name.Contains("win64") && !File.Exists(Path.Combine(ServerDirectory, "bin", "win64", "engine.dll"))) continue;
                return p;
            }
        }
        return null;
    }

    public static bool Is64Bit(string exePath) => Path.GetFileName(exePath).Contains("win64", StringComparison.OrdinalIgnoreCase);

    /// <summary>The next scheduled restart after <paramref name="after"/>, if there is a schedule.</summary>
    public DateTime? NextRestart(DateTime after)
    {
        DateTime? best = null;
        foreach (var t in RestartTimes)
        {
            if (!TryParseTime(t, out var tod)) continue;
            var at = after.Date + tod;
            if (at <= after) at = at.AddDays(1);
            if (best == null || at < best) best = at;
        }
        return best;
    }

    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var parts = text.Trim().Split(':', '.');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        if (h is < 0 or > 23 || m is < 0 or > 59) return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }
}

/// <summary>Where a window was, to open it there again.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; }
}
