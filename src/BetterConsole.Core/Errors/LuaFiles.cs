using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BetterConsole.Core.Errors;

/// <summary>A path to a Lua file inside some text, with its line.</summary>
public readonly record struct LuaLink(int Start, int Length, string Source, int Line);

/// <summary>
/// Finds the file on disk for a Lua source as GMod names it: "addons/ulib/lua/ulib/shared/hook.lua",
/// "lua/autorun/x.lua" (in garrysmod/lua or in any folder addon), "gamemodes/darkrp/gamemode/init.lua",
/// or a long path Lua shortened to "...lib/shared/hook.lua". Files packed in workshop .gma archives are
/// not on disk and are not found.
/// </summary>
public sealed class LuaFileResolver(Func<string> gameDirectory)
{
    private readonly ConcurrentDictionary<string, (string? Path, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private (string Dir, string[] Addons, DateTime At)? _addons;
    private sealed record Index(string Dir, List<string> Paths, DateTime At);
    private volatile Index? _index;
    private int _indexing;

    /// <summary>The full path of the file, or null. Cheap after the first call for a source.</summary>
    public string? Resolve(string source)
    {
        var src = Normalize(source);
        if (src == null) return null;
        var now = DateTime.UtcNow;
        // By folder too: the server folder can change in the settings.
        var key = gameDirectory() + "|" + src;
        if (_cache.TryGetValue(key, out var c))
        {
            if (c.Path != null && File.Exists(c.Path)) return c.Path;
            if (c.Path == null && (now - c.At).TotalSeconds < 20) return null;
        }
        var found = Find(src);
        // A shortened path whose index is still being built is asked again soon.
        _cache[key] = (found, found == null && src.StartsWith("...") && _index == null ? now.AddSeconds(-19) : now);
        return found;
    }

    private static string? Normalize(string source)
    {
        var s = source.Trim().Trim('"', '\'').Replace('\\', '/');
        if (s.StartsWith('@')) s = s[1..];
        if (s.Length == 0 || s == "[C]" || !s.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return null;
        if (s.Contains("../") || s.Contains(':') && !Path.IsPathRooted(s)) return null;
        return s;
    }

    private string? Find(string src)
    {
        var game = gameDirectory();
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) return null;
        if (Path.IsPathRooted(src))
        {
            // Only files of this server (client errors carry whatever the client sends).
            var full = Path.GetFullPath(src);
            return full.StartsWith(Path.GetFullPath(game).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        if (src.StartsWith("..."))
        {
            var suffix = src[3..].TrimStart('/');
            return suffix.Length < 6 ? null : FindBySuffix(game, suffix);
        }
        string? Try(params string[] parts)
        {
            var p = Path.Combine(parts);
            return File.Exists(p) ? Path.GetFullPath(p) : null;
        }
        if (Try(game, src) is { } direct) return direct;
        var addons = Addons(game);
        bool rooted = src.StartsWith("lua/", StringComparison.OrdinalIgnoreCase) || src.StartsWith("gamemodes/", StringComparison.OrdinalIgnoreCase);
        if (rooted)
        {
            foreach (var a in addons)
                if (Try(a, src) is { } inAddon) return inAddon;
            return null;
        }
        if (src.StartsWith("addons/", StringComparison.OrdinalIgnoreCase)) return null;
        // Relative to lua/ (include paths, some stack frames).
        if (Try(game, "lua", src) is { } inLua) return inLua;
        foreach (var a in addons)
            if (Try(a, "lua", src) is { } inAddonLua) return inAddonLua;
        return null;
    }

    private string[] Addons(string game)
    {
        var now = DateTime.UtcNow;
        if (_addons is { } a && a.Dir == game && (now - a.At).TotalSeconds < 30) return a.Addons;
        string[] list;
        try
        {
            var dir = Path.Combine(game, "addons");
            list = Directory.Exists(dir) ? Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray() : [];
        }
        catch { list = []; }
        _addons = (game, list, now);
        return list;
    }

    /// <summary>A shortened path: the one file (as GMod names it) that ends like it, from an index built in the background.</summary>
    private string? FindBySuffix(string game, string suffix)
    {
        var idx = _index;
        if (idx == null || idx.Dir != game || (DateTime.UtcNow - idx.At).TotalMinutes > 5)
        {
            if (Interlocked.Exchange(ref _indexing, 1) == 0)
            {
                Task.Run(() =>
                {
                    try { _index = new Index(game, BuildIndex(game), DateTime.UtcNow); }
                    finally { _indexing = 0; }
                });
            }
            if (idx == null || idx.Dir != game) return null;
        }
        string? match = null;
        foreach (var p in idx.Paths)
        {
            if (!p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (match != null) return null; // more than one: not sure which
            match = p;
        }
        return match == null ? null : Path.GetFullPath(Path.Combine(game, match));
    }

    private static List<string> BuildIndex(string game)
    {
        var list = new List<string>();
        void Add(string root, string prefix)
        {
            if (!Directory.Exists(root)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.lua", SearchOption.AllDirectories))
                    list.Add(prefix + Path.GetRelativePath(root, f).Replace('\\', '/'));
            }
            catch { }
        }
        Add(Path.Combine(game, "lua"), "lua/");
        Add(Path.Combine(game, "gamemodes"), "gamemodes/");
        var addons = Path.Combine(game, "addons");
        if (Directory.Exists(addons))
        {
            foreach (var a in Directory.GetDirectories(addons))
            {
                var name = Path.GetFileName(a);
                Add(Path.Combine(a, "lua"), $"addons/{name}/lua/");
                Add(Path.Combine(a, "gamemodes"), $"addons/{name}/gamemodes/");
            }
        }
        return list;
    }

    // ------------------------------------------------------------------------------ links in text

    // "1. fn - addons/my addon/lua/x.lua:12" (a stack line: the path may contain spaces).
    private static readonly Regex StackLine = new(@"^\s*\d+\. .*? - (?<src>[^\r\n]+?\.lua)(?::(?<line>-?\d+))?\r?$", RegexOptions.Multiline | RegexOptions.Compiled);
    // A path in free text: "addons/x/lua/y.lua:12:", "[@addons/x/lua/y.lua (line 5)]", "...ib/shared/hook.lua:110".
    private static readonly Regex InText = new(@"(?<![\w/.\-])(?<src>(?:\.\.\.)?(?:[\w\-.+]+/)*[\w\-.+]+\.lua)(?::(?<line>-?\d+)| \(line (?<line2>-?\d+)\))?", RegexOptions.Compiled);

    /// <summary>The Lua paths in a text (not checked against the disk).</summary>
    public static List<LuaLink> Scan(string text)
    {
        var links = new List<LuaLink>();
        if (string.IsNullOrEmpty(text) || !text.Contains(".lua", StringComparison.OrdinalIgnoreCase)) return links;
        // Error texts are short; a huge one (a crafted client error) is only looked at in its beginning.
        if (text.Length > MaxScan) text = text[..MaxScan];
        foreach (Match m in StackLine.Matches(text))
        {
            var g = m.Groups["src"];
            links.Add(new LuaLink(g.Index, m.Groups["line"].Success ? m.Groups["line"].Index + m.Groups["line"].Length - g.Index : g.Length,
                g.Value, m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var l) ? l : 0));
        }
        // Both lists are in text order: a path inside a stack line found again is skipped in one pass.
        int stackCount = links.Count, k = 0;
        foreach (Match m in InText.Matches(text))
        {
            while (k < stackCount && links[k].Start + links[k].Length <= m.Index) k++;
            if (k < stackCount && links[k].Start < m.Index + m.Length) continue;
            var g = m.Groups["src"];
            var lg = m.Groups["line"].Success ? m.Groups["line"] : m.Groups["line2"];
            int line = lg.Success && int.TryParse(lg.Value, out var l) ? l : 0;
            links.Add(new LuaLink(g.Index, m.Length, g.Value, line));
        }
        return links;
    }

    private const int MaxScan = 16 * 1024;
}
