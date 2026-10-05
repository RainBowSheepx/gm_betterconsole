using System.Reflection;
using System.Security.Cryptography;
using BetterConsole.Core.Server;

namespace BetterConsole.App.Services;

/// <summary>
/// Installs the companion addon (garrysmod/addons/betterconsole) and the native module
/// (garrysmod/lua/bin/gmsv_betterconsole_win32|win64.dll), both embedded in BetterConsole.exe,
/// before the server starts. Files are only written when they differ.
/// </summary>
public static class CompanionInstaller
{
    private const string AddonPrefix = "companion/addon/";
    private const string BinPrefix = "companion/bin/";

    public sealed record Result(int Written, int Unchanged, List<string> Warnings);

    public static bool HasModule(bool is64Bit) =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(BinPrefix + ModuleName(is64Bit)) != null;

    public static string ModuleName(bool is64Bit) => is64Bit ? "gmsv_betterconsole_win64.dll" : "gmsv_betterconsole_win32.dll";

    public static Result Install(ServerProfile profile, string exePath)
    {
        var asm = Assembly.GetExecutingAssembly();
        var game = profile.GameDirectory;
        var warnings = new List<string>();
        int written = 0, unchanged = 0;
        if (!Directory.Exists(game))
        {
            warnings.Add($"\"{game}\" does not exist; the companion addon was not installed.");
            return new Result(0, 0, warnings);
        }

        var addonRoot = Path.Combine(game, "addons", "betterconsole");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(AddonPrefix, StringComparison.Ordinal)) continue;
            var rel = name[AddonPrefix.Length..].Replace('\\', '/');
            var target = Path.GetFullPath(Path.Combine(addonRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(addonRoot, StringComparison.OrdinalIgnoreCase)) continue;
            expected.Add(target);
            using var s = asm.GetManifestResourceStream(name)!;
            try
            {
                if (WriteIfChanged(s, target)) written++; else unchanged++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not write {rel}: {ex.Message}");
            }
        }
        // Files of an older version that no longer exist.
        if (Directory.Exists(addonRoot))
        {
            foreach (var f in Directory.EnumerateFiles(Path.Combine(addonRoot), "*.lua", SearchOption.AllDirectories))
            {
                if (!expected.Contains(Path.GetFullPath(f)))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }

        bool is64 = ServerProfile.Is64Bit(exePath);
        var module = ModuleName(is64);
        using (var ms = asm.GetManifestResourceStream(BinPrefix + module))
        {
            if (ms == null)
            {
                warnings.Add($"This build of BetterConsole has no {module}; Lua errors, statistics and addon tabs will not be available.");
            }
            else
            {
                var bin = Path.Combine(game, "lua", "bin");
                try
                {
                    if (WriteIfChanged(ms, Path.Combine(bin, module))) written++; else unchanged++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not update {module} (is another server using it?): {ex.Message}");
                }
            }
        }
        return new Result(written, unchanged, warnings);
    }

    private static bool WriteIfChanged(Stream source, string target)
    {
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (File.Exists(target))
        {
            var old = File.ReadAllBytes(target);
            if (old.Length == bytes.Length && SHA256.HashData(old).AsSpan().SequenceEqual(SHA256.HashData(bytes))) return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".new";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, target, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return true;
    }
}
