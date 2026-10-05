namespace BetterConsole.Core.Server;

/// <summary>How to start one dedicated server. Saved in settings.json.</summary>
public sealed class ServerProfile
{
    /// <summary>The folder that holds srcds.exe / srcds_console.exe.</summary>
    public string ServerDirectory { get; set; } = "";

    /// <summary>"auto", or a file name in <see cref="ServerDirectory"/> such as srcds_console_win64.exe.</summary>
    public string Executable { get; set; } = "auto";

    /// <summary>Command line arguments for srcds (without the executable).</summary>
    public string Arguments { get; set; } = "-console -game garrysmod +maxplayers 16 +gamemode sandbox +map gm_construct";

    /// <summary>Restart the server when it exits without being asked to.</summary>
    public bool AutoRestart { get; set; } = true;

    public int RestartDelaySeconds { get; set; } = 5;

    /// <summary>Copy the companion addon and module into the server before every start.</summary>
    public bool InstallCompanion { get; set; } = true;

    /// <summary>Start the server as soon as BetterConsole opens.</summary>
    public bool StartWithApp { get; set; }

    /// <summary>Seconds to wait for "quit" before the process is killed.</summary>
    public int StopTimeoutSeconds { get; set; } = 20;

    public string GameDirectory => Path.Combine(ServerDirectory, "garrysmod");

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
}
