using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterConsole.App.ViewModels;

public enum CommandKind { Command, Variable, History, Map }

/// <summary>One entry of the auto-completion list.</summary>
public sealed partial class CompletionItem : ObservableObject
{
    public required string Name { get; init; }
    public CommandKind Kind { get; init; }
    public string? Help { get; init; }
    public string? Default { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public int Flags { get; init; }
    /// <summary>The text that replaces the input when the item is accepted.</summary>
    public string? InsertText { get; init; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasValue))] private string? value;

    // Set per query for the match highlight.
    [ObservableProperty] private string before = "";
    [ObservableProperty] private string match = "";
    [ObservableProperty] private string after = "";

    public bool HasValue => !string.IsNullOrEmpty(Value);
    public string KindText => Kind switch { CommandKind.Variable => "var", CommandKind.Command => "cmd", CommandKind.History => "history", _ => "map" };
    public string? HelpLine => Help?.Replace("\r", " ").Replace("\n", " ");

    public string Tooltip
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Help)) parts.Add(Help!);
            if (Kind == CommandKind.Variable)
            {
                var v = $"Value: \"{Value}\"  Default: \"{Default}\"";
                if (Min != null || Max != null) v += $"  Range: {Min?.ToString() ?? ""} .. {Max?.ToString() ?? ""}";
                parts.Add(v);
            }
            return string.Join("\n", parts);
        }
    }
}

/// <summary>
/// What the console input can complete: every server command and variable the engine knows (sent by
/// the companion addon), map names for map / changelevel, and the command history.
/// </summary>
public sealed class CommandCatalog
{
    private List<CompletionItem> _items = new();
    private readonly Dictionary<string, CompletionItem> _byName = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _maps = new();

    public int Count => _items.Count;
    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }

    // GMod flag bits (Source 2013 convar.h)
    private const int FCVAR_DEVELOPMENTONLY = 1 << 1;
    private const int FCVAR_GAMEDLL = 1 << 2;
    private const int FCVAR_CLIENTDLL = 1 << 3;
    private const int FCVAR_HIDDEN = 1 << 4;
    private const int FCVAR_USERINFO = 1 << 9;
    private const int FCVAR_LUA_CLIENT = 1 << 18;
    private const int FCVAR_ARCHIVE_XBOX = 1 << 24;

    // Engine (not game DLL) commands that only make sense on a game client. The dedicated server
    // shares engine.dll with the client, so they are registered but do nothing useful.
    private static readonly string[] ClientPrefixes =
    [
        "+", "-", "cl_", "mat_", "r_", "snd_", "vgui_", "joy_", "m_", "gl_", "hud_", "cc_", "dsp_", "demo_",
        "gameui", "menu_", "spec_", "key_", "vr_", "voice_", "cam_", "xbox", "x360", "vid_", "zoom_", "texture_", "fps_max_",
    ];

    // Single client-only names of the engine.
    private static readonly HashSet<string> ExactClientNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "play", "playvol", "speak", "bind", "unbind", "connect", "retry", "disconnect", "jpeg", "screenshot", "record", "stop",
        "volume", "rate", "name", "password", "kill", "explode", "noclip", "god", "buddha", "impulse", "use", "slot",
        "setinfo", "escape", "cancelselect", "chooseteam", "lastinv", "invnext", "invprev", "phys_swap", "spawnmenu",
        "snd", "cl", "sensitivity", "showinfo", "net_graph", "gameinstructor", "lookspring", "lookstrafe", "videomode",
        "lua_run_cl", "lua_openscript_cl", "lua_run_menu", "lua_openscript_menu", "toggleconsole", "showconsole", "hideconsole",
        "startmovie", "endmovie", "cache_print", "messagemode", "gmod_language", "gmod_toolmode", "showsmoke",
    };

    public static bool IsClientOnly(string name, int flags)
    {
        if ((flags & (FCVAR_DEVELOPMENTONLY | FCVAR_HIDDEN | FCVAR_CLIENTDLL | FCVAR_USERINFO | FCVAR_LUA_CLIENT | FCVAR_ARCHIVE_XBOX)) != 0) return true;
        if (ExactClientNames.Contains(name)) return true;
        if ((flags & FCVAR_GAMEDLL) != 0) return false;
        foreach (var p in ClientPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void Load(JsonElement m, bool serverOnly)
    {
        var items = new List<CompletionItem>();
        _byName.Clear();
        if (m.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                var name = StatsVm.Str(e, "n");
                if (string.IsNullOrEmpty(name) || name.StartsWith("betterconsole_", StringComparison.Ordinal)) continue;
                int flags = (int)StatsVm.Num0(e, "f");
                if (serverOnly && IsClientOnly(name, flags)) continue;
                bool isVar = e.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.False;
                var mn = StatsVm.Num(e, "mn");
                var mx = StatsVm.Num(e, "mx");
                var item = new CompletionItem
                {
                    Name = name,
                    Kind = isVar ? CommandKind.Variable : CommandKind.Command,
                    Help = StatsVm.Str(e, "h"),
                    Default = StatsVm.Str(e, "d"),
                    Min = double.IsNaN(mn) ? null : mn,
                    Max = double.IsNaN(mx) ? null : mx,
                    Flags = flags,
                    Value = StatsVm.Str(e, "v"),
                };
                if (_byName.TryAdd(name, item)) items.Add(item);
            }
        }
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _items = items;
        _maps = m.TryGetProperty("maps", out var maps) && maps.ValueKind == JsonValueKind.Array
            ? maps.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new();
        IsLoaded = true;
        LoadError = m.TryGetProperty("native", out var n) && n.ValueKind == JsonValueKind.False ? StatsVm.Str(m, "err") : null;
    }

    public void UpdateValues(JsonElement vals)
    {
        if (vals.ValueKind != JsonValueKind.Object) return;
        foreach (var p in vals.EnumerateObject())
            if (_byName.TryGetValue(p.Name, out var item) && p.Value.ValueKind == JsonValueKind.String) item.Value = p.Value.GetString();
    }

    public CompletionItem? Find(string name) => _byName.TryGetValue(name, out var i) ? i : null;

    /// <summary>
    /// Suggestions for <paramref name="input"/>. Completes the command name while the first word is
    /// typed, map names after "map " / "changelevel ". Prefix matches come first, then names that
    /// only contain the text. History entries starting with the text are offered too.
    /// </summary>
    public List<CompletionItem> Suggest(string input, IReadOnlyList<string> history, int limit)
    {
        var result = new List<CompletionItem>();
        if (string.IsNullOrWhiteSpace(input)) return result;
        int space = input.IndexOf(' ');
        if (space > 0)
        {
            var cmd = input[..space];
            var arg = input[(space + 1)..];
            if ((cmd.Equals("map", StringComparison.OrdinalIgnoreCase) || cmd.Equals("changelevel", StringComparison.OrdinalIgnoreCase)) && !arg.Contains(' '))
            {
                foreach (var map in _maps.Where(x => x.StartsWith(arg, StringComparison.OrdinalIgnoreCase)).Take(limit))
                    result.Add(Highlight(new CompletionItem { Name = map, Kind = CommandKind.Map, InsertText = $"{cmd} {map}" }, arg));
            }
            else
            {
                // Past the command name: only history lines that continue what is typed.
                foreach (var h in history.Reverse().Where(h => h.StartsWith(input, StringComparison.OrdinalIgnoreCase) && h.Length > input.Length).Distinct().Take(8))
                    result.Add(Highlight(new CompletionItem { Name = h, Kind = CommandKind.History, InsertText = h }, input));
            }
            return result;
        }

        var text = input.Trim();
        var prefix = new List<CompletionItem>();
        var contains = new List<CompletionItem>();
        foreach (var item in _items)
        {
            int idx = item.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase);
            if (idx == 0) prefix.Add(item);
            else if (idx > 0 && text.Length >= 2) contains.Add(item);
            if (prefix.Count >= limit) break;
        }
        // Exact name first, then shorter names (sv_ -> sv_cheats before sv_cheats_whatever).
        prefix.Sort((a, b) =>
        {
            bool ea = a.Name.Equals(text, StringComparison.OrdinalIgnoreCase), eb = b.Name.Equals(text, StringComparison.OrdinalIgnoreCase);
            if (ea != eb) return ea ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (var i in prefix) result.Add(Highlight(i, text));
        foreach (var i in contains)
        {
            if (result.Count >= limit) break;
            result.Add(Highlight(i, text));
        }
        if (result.Count == 0)
        {
            foreach (var h in history.Reverse().Where(h => h.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Distinct().Take(8))
                result.Add(Highlight(new CompletionItem { Name = h, Kind = CommandKind.History, InsertText = h }, text));
        }
        return result;
    }

    private static CompletionItem Highlight(CompletionItem item, string text)
    {
        int idx = item.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || text.Length == 0)
        {
            item.Before = item.Name;
            item.Match = "";
            item.After = "";
        }
        else
        {
            item.Before = item.Name[..idx];
            item.Match = item.Name.Substring(idx, text.Length);
            item.After = item.Name[(idx + text.Length)..];
        }
        return item;
    }
}
