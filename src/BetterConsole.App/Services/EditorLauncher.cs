using System.Diagnostics;
using Microsoft.Win32;

namespace BetterConsole.App.Services;

/// <summary>A text editor BetterConsole knows how to open a file at a line with.</summary>
public sealed record EditorInfo(string Id, string Name, string? Exe, string ArgsTemplate)
{
    public bool Installed => Exe != null;
}

/// <summary>Opens Lua files in the editor chosen in the settings, at the line.</summary>
public static class EditorLauncher
{
    private static List<EditorInfo>? _known;

    /// <summary>The editors BetterConsole can use, with where they are installed (null = not found).</summary>
    public static IReadOnlyList<EditorInfo> Known => _known ??= Detect();

    public static void Refresh() => _known = null;

    private static List<EditorInfo> Detect()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string? First(params string?[] paths) => paths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));

        string? codeFromPath = FindOnPath("code.cmd") is { } cmd ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cmd)!, "..", "Code.exe")) : null;
        string? nppDir = null;
        try { nppDir = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Notepad++")?.GetValue(null) as string; } catch { }

        return
        [
            new("vscode", "Visual Studio Code", First(Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe"), Path.Combine(pf, @"Microsoft VS Code\Code.exe"), codeFromPath), "-g \"{file}:{line}\""),
            new("cursor", "Cursor", First(Path.Combine(local, @"Programs\cursor\Cursor.exe")), "-g \"{file}:{line}\""),
            new("vscodium", "VSCodium", First(Path.Combine(local, @"Programs\VSCodium\VSCodium.exe"), Path.Combine(pf, @"VSCodium\VSCodium.exe")), "-g \"{file}:{line}\""),
            new("notepad++", "Notepad++", First(nppDir != null ? Path.Combine(nppDir, "notepad++.exe") : null, Path.Combine(pf, @"Notepad++\notepad++.exe"), Path.Combine(pf86, @"Notepad++\notepad++.exe")), "-n{line} \"{file}\""),
            new("sublime", "Sublime Text", First(Path.Combine(pf, @"Sublime Text\sublime_text.exe"), Path.Combine(pf, @"Sublime Text 3\sublime_text.exe")), "\"{file}:{line}\""),
            new("notepad", "Notepad (no line numbers)", Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"{file}\""),
        ];
    }

    private static string? FindOnPath(string file)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, file))) return Path.Combine(dir, file);
            }
            catch { }
        }
        return null;
    }

    /// <summary>What "auto" picks: the first installed of VS Code, Cursor, VSCodium, Notepad++, Sublime, else Notepad.</summary>
    public static EditorInfo Auto => Known.First(e => e.Installed);

    /// <summary>The name of the editor that is used for <paramref name="setting"/>.</summary>
    public static string Describe(string setting) => setting switch
    {
        "auto" => Auto.Name,
        "system" => "the program Windows opens .lua files with",
        "custom" => "your command",
        _ => Known.FirstOrDefault(e => e.Id == setting)?.Name ?? Auto.Name,
    };

    /// <summary>Opens the file at the line. Returns the problem, or null.</summary>
    public static string? Open(AppSettings settings, string file, int line)
    {
        line = Math.Max(1, line);
        try
        {
            switch (settings.Editor)
            {
                case "system":
                    // "edit" first: where .lua belongs to a Lua interpreter, "open" would run the file.
                    try
                    {
                        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "edit" });
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                    }
                    return null;
                case "custom":
                {
                    var cmd = settings.EditorCommand.Trim();
                    if (cmd.Length == 0) return "No command for the custom editor (Settings → Lua files).";
                    var (exe, args) = SplitCommand(cmd);
                    if (!cmd.Contains("{file}")) args = (args + " \"{file}\"").Trim();
                    Start(exe, Fill(args, file, line));
                    return null;
                }
                default:
                {
                    var editor = Known.FirstOrDefault(e => e.Id == settings.Editor && e.Installed) ?? Auto;
                    Start(editor.Exe!, Fill(editor.ArgsTemplate, file, line));
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string Fill(string template, string file, int line) =>
        template.Replace("{file}", file).Replace("{line}", line.ToString());

    private static void Start(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });

    /// <summary>"\"C:\\Program Files\\X\\x.exe\" -n{line} {file}" → the exe and the rest.</summary>
    public static (string Exe, string Args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        // Unquoted: the longest prefix that is an existing file (paths with spaces).
        var parts = command.Split(' ');
        for (int n = parts.Length; n > 0; n--)
        {
            var candidate = string.Join(' ', parts.Take(n));
            if (File.Exists(candidate)) return (candidate, string.Join(' ', parts.Skip(n)));
        }
        return (parts[0], string.Join(' ', parts.Skip(1)));
    }
}
