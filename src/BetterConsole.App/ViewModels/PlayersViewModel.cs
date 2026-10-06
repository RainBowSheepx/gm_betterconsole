using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using BetterConsole.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterConsole.App.ViewModels;

/// <summary>One row of the Players tab. Updated in place so selection and sorting survive refreshes.</summary>
public sealed partial class PlayerRowVm : ObservableObject
{
    public PlayerRowVm(int userId) => UserId = userId;

    public int UserId { get; }
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string steamId = "";
    [ObservableProperty] private string steamId64 = "";
    [ObservableProperty] private string group = "user";
    /// <summary>How high the group is in the hierarchy (superadmin above admin above user): what the Group column sorts by.</summary>
    [ObservableProperty] private int groupRank;
    [ObservableProperty] private bool isBot;
    [ObservableProperty] private string ip = "";
    /// <summary>Server CPU spent on this player, ms per tick (only while the tab is open).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LoadText), nameof(LoadLevel))] private double load = double.NaN;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LossText))] private double loss;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PingText))] private double ping;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FpsText))] private double fps = double.NaN;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ChokeText))] private double choke = double.NaN;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(InText))] private double inBytes = double.NaN;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OutText))] private double outBytes = double.NaN;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimeText))] private double timeConnected;
    /// <summary>cl_updaterate and cl_cmdrate of the client (what it asks for; NaN for bots).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UpdateRateText))] private double updateRate = double.NaN;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CmdRateText))] private double cmdRate = double.NaN;
    [ObservableProperty] private string team = "";
    [ObservableProperty] private int frags;
    [ObservableProperty] private int deaths;
    [ObservableProperty] private string? ulxId;
    [ObservableProperty] private bool gagged;
    [ObservableProperty] private bool muted;
    [ObservableProperty] private bool jailed;
    /// <summary>The Steam avatar, once loaded (null: the coloured initial is shown).</summary>
    [ObservableProperty] private ImageSource? avatar;

    /// <summary>The tick budget in ms, to colour the load column (set by the owner).</summary>
    public double TickBudgetMs { get; set; } = 15;

    public string LoadText => double.IsNaN(Load) ? "—" : Load.ToString("F2");
    /// <summary>0 normal, 1 noticeable (2.5 % of the tick), 2 heavy (6 % of the tick).</summary>
    public int LoadLevel => double.IsNaN(Load) ? 0 : Load > TickBudgetMs * 0.06 ? 2 : Load > TickBudgetMs * 0.025 ? 1 : 0;
    public string LossText => $"{Loss:F0}%";
    public string PingText => IsBot ? "BOT" : $"{Ping:F0}";
    public string FpsText => double.IsNaN(Fps) || Fps <= 0 ? "—" : $"{Fps:F0}";
    public string ChokeText => double.IsNaN(Choke) ? "—" : $"{Choke:F0}%";
    public string InText => double.IsNaN(InBytes) ? "—" : ProfileRow.FormatBytes(InBytes) + "/s";
    public string OutText => double.IsNaN(OutBytes) ? "—" : ProfileRow.FormatBytes(OutBytes) + "/s";
    public string UpdateRateText => double.IsNaN(UpdateRate) || UpdateRate <= 0 ? "—" : $"{UpdateRate:0.#}";
    public string CmdRateText => double.IsNaN(CmdRate) || CmdRate <= 0 ? "—" : $"{CmdRate:0.#}";

    /// <summary>The player menu items of addons whose filter accepts this player (their Lua ids).</summary>
    public HashSet<string> ActionFlags { get; } = new();
    public string TimeText
    {
        get
        {
            var t = TimeSpan.FromSeconds(TimeConnected);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }

    /// <summary>How ULX should address this player: "$id" that matches exactly one player.</summary>
    public string UlxTarget => "\"$" + (UlxId ?? (IsBot ? UserId.ToString() : SteamId)) + "\"";

    public string ProfileUrl => string.IsNullOrEmpty(SteamId64) || IsBot ? "" : $"https://steamcommunity.com/profiles/{SteamId64}";

    /// <summary>What plugins see of the player.</summary>
    public BetterConsole.Sdk.PlayerInfo ToInfo() => new()
    {
        UserId = UserId,
        Name = Name,
        SteamId = SteamId,
        SteamId64 = IsBot ? "" : SteamId64,
        IsBot = IsBot,
        Group = Group,
        Ip = Ip,
        Ping = Ping,
        Connected = TimeSpan.FromSeconds(TimeConnected),
    };
}

public sealed partial class PlayersVm : ObservableObject
{
    private readonly Dictionary<int, PlayerRowVm> _byId = new();

    public ObservableCollection<PlayerRowVm> Rows { get; } = new();
    public ObservableCollection<string> Groups { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HeaderText))] private int count;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HeaderText))] private int maxPlayers;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HeaderText))] private int bots;
    [ObservableProperty] private bool hasUlx;
    [ObservableProperty] private bool loadMeasured;
    [ObservableProperty] private bool hasData;

    public double TickBudgetMs { get; set; } = 15;

    public string HeaderText => MaxPlayers > 0 ? $"{Count} / {MaxPlayers}" + (Bots > 0 ? $"  ·  {Bots} bot{(Bots == 1 ? "" : "s")}" : "") : $"{Count}";

    public void Apply(JsonElement m)
    {
        HasData = true;
        if (m.TryGetProperty("maxplayers", out var mp) && mp.ValueKind == JsonValueKind.Number) MaxPlayers = mp.GetInt32();
        HasUlx = m.TryGetProperty("ulx", out var u) && u.ValueKind == JsonValueKind.True;
        LoadMeasured = m.TryGetProperty("load", out var lm) && lm.ValueKind == JsonValueKind.True;
        if (m.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            var list = groups.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.String).Select(g => g.GetString()!).ToList();
            if (!list.SequenceEqual(Groups))
            {
                Groups.Clear();
                foreach (var g in list) Groups.Add(g);
            }
        }
        else if (Groups.Count == 0)
        {
            foreach (var g in new[] { "superadmin", "admin", "user" }) Groups.Add(g);
        }

        _ranks.Clear();
        if (m.TryGetProperty("ranks", out var ranks) && ranks.ValueKind == JsonValueKind.Object)
            foreach (var r in ranks.EnumerateObject())
                if (r.Value.ValueKind == JsonValueKind.Number) _ranks[r.Name] = r.Value.GetInt32();

        var seen = new HashSet<int>();
        int bots = 0;
        if (m.TryGetProperty("list", out var list2) && list2.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in list2.EnumerateArray())
            {
                int uid = (int)StatsVm.Num0(p, "uid");
                seen.Add(uid);
                if (!_byId.TryGetValue(uid, out var row))
                {
                    row = new PlayerRowVm(uid);
                    _byId[uid] = row;
                    Rows.Add(row);
                }
                row.TickBudgetMs = TickBudgetMs;
                row.Name = StatsVm.Str(p, "name") ?? "?";
                row.SteamId = StatsVm.Str(p, "sid") ?? "";
                var sid64 = StatsVm.Str(p, "sid64") ?? "";
                // Another player on the same row: not the old avatar.
                if (sid64 != row.SteamId64) row.Avatar = null;
                row.SteamId64 = sid64;
                row.IsBot = p.TryGetProperty("bot", out var b) && b.ValueKind == JsonValueKind.True;
                if (row.IsBot) bots++;
                row.Ip = StatsVm.Str(p, "ip") ?? "";
                row.Group = StatsVm.Str(p, "group") ?? "user";
                row.GroupRank = RankOf(row.Group);
                row.Ping = StatsVm.Num0(p, "ping");
                row.Loss = StatsVm.Num0(p, "loss");
                row.Choke = StatsVm.Num(p, "choke");
                row.InBytes = StatsVm.Num(p, "in");
                row.OutBytes = StatsVm.Num(p, "out");
                row.Fps = StatsVm.Num(p, "fps");
                row.Load = LoadMeasured ? StatsVm.Num0(p, "load") : double.NaN;
                row.TimeConnected = StatsVm.Num0(p, "time");
                row.UpdateRate = StatsVm.Num(p, "upd");
                row.CmdRate = StatsVm.Num(p, "cmdr");
                row.ActionFlags.Clear();
                if (p.TryGetProperty("act", out var act) && act.ValueKind == JsonValueKind.Object)
                    foreach (var a in act.EnumerateObject())
                        if (a.Value.ValueKind == JsonValueKind.True) row.ActionFlags.Add(a.Name);
                row.Team = StatsVm.Str(p, "team") ?? "";
                row.Frags = (int)StatsVm.Num0(p, "frags");
                row.Deaths = (int)StatsVm.Num0(p, "deaths");
                row.UlxId = StatsVm.Str(p, "ulxid");
                row.Gagged = p.TryGetProperty("gagged", out var g) && g.ValueKind == JsonValueKind.True;
                row.Muted = p.TryGetProperty("muted", out var mu) && mu.ValueKind == JsonValueKind.True;
                row.Jailed = p.TryGetProperty("jailed", out var j) && j.ValueKind == JsonValueKind.True;
                if (ShowAvatars && row.Avatar == null && !row.IsBot) LoadAvatar(row);
            }
        }
        foreach (var gone in _byId.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Rows.Remove(_byId[gone]);
            _byId.Remove(gone);
        }
        Count = Rows.Count;
        Bots = bots;
    }

    public void Clear()
    {
        _byId.Clear();
        Rows.Clear();
        Count = 0;
        Bots = 0;
        HasData = false;
    }

    private readonly Dictionary<string, int> _ranks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rank of a group: from the admin mod's hierarchy (how many groups it inherits from) when the
    /// addon sent it, else GMod's own three groups; groups nobody knows rank with user.
    /// </summary>
    public int RankOf(string group)
    {
        if (_ranks.TryGetValue(group, out var r)) return r;
        return group.ToLowerInvariant() switch
        {
            "superadmin" => 2,
            "admin" => 1,
            _ => 0,
        };
    }

    /// <summary>Steam avatars in the Nick column (Settings → Players). Off: the coloured initials.</summary>
    public bool ShowAvatars
    {
        get => _showAvatars;
        set
        {
            if (_showAvatars == value) return;
            _showAvatars = value;
            foreach (var row in Rows)
            {
                if (!value) row.Avatar = null;
                else if (!row.IsBot) LoadAvatar(row);
            }
        }
    }
    private bool _showAvatars = true;

    private void LoadAvatar(PlayerRowVm row) =>
        row.Avatar = AvatarCache.Get(row.SteamId64, img => { if (_showAvatars && _byId.ContainsValue(row)) row.Avatar = img; });
}

/// <summary>A field of the dialog of a player action: text, a number or a choice.</summary>
public sealed record PlayerActionField(string Id, string Label, string Default, bool Number, IReadOnlyList<(string Text, string Value)>? Choices, bool Editable);

/// <summary>
/// An item of an addon (BetterConsole.AddPlayerAction) or a plugin (IUiHost.AddPlayerAction) in the right-click
/// menu of the Players tab.
/// </summary>
public sealed class PlayerActionDef
{
    /// <summary>"lua:id" or "plugin:id".</summary>
    public required string Id { get; init; }
    public required string Text { get; init; }
    /// <summary>A glyph of Segoe Fluent Icons, or empty.</summary>
    public string Icon { get; init; } = "";
    public int Order { get; init; } = 300;
    /// <summary>A console command; placeholders: see <see cref="Expand"/>.</summary>
    public string? Command { get; init; }
    public string? Confirm { get; init; }
    public bool Danger { get; init; }
    public bool Bots { get; init; } = true;
    public bool Multi { get; init; } = true;
    /// <summary>The addon's filter decides who it is for (the "act" flags of the player list).</summary>
    public bool Filtered { get; init; }
    /// <summary>The addon runs it in Lua (onRun).</summary>
    public bool LuaRun { get; init; }
    public IReadOnlyList<PlayerActionField> Fields { get; init; } = [];
    public Action<IReadOnlyList<BetterConsole.Sdk.PlayerInfo>>? PluginRun { get; init; }
    public Func<BetterConsole.Sdk.PlayerInfo, bool>? PluginFilter { get; init; }

    public string LuaId => Id.StartsWith("lua:", StringComparison.Ordinal) ? Id[4..] : Id;

    public bool AppliesTo(PlayerRowVm p)
    {
        if (!Bots && p.IsBot) return false;
        if (Filtered && !p.ActionFlags.Contains(LuaId)) return false;
        if (PluginFilter == null) return true;
        try { return PluginFilter(p.ToInfo()); }
        catch (Exception ex)
        {
            Services.Log.Write($"player action {Id} filter: {ex.Message}");
            return false;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex PlayerPlaceholder =
        new(@"\{(target|userid|steamid|steamid64|name)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The command names a player: it runs once for each, else once.</summary>
    public bool PerPlayer => Command != null && PlayerPlaceholder.IsMatch(Command);

    /// <summary>
    /// {target} (ULX's "$id", quoted), {userid}, {steamid}, {steamid64}, {name} and {field} (the dialog's values).
    /// Names and values lose quotes, semicolons and line breaks, so they cannot end the command.
    /// </summary>
    public static string Expand(string template, PlayerRowVm? p, IReadOnlyDictionary<string, string> values)
    {
        return System.Text.RegularExpressions.Regex.Replace(template, @"\{([A-Za-z0-9_]+)\}", m =>
        {
            var key = m.Groups[1].Value;
            if (p != null)
            {
                switch (key.ToLowerInvariant())
                {
                    case "target": return p.UlxTarget;
                    case "userid": return p.UserId.ToString();
                    case "steamid": return p.SteamId;
                    case "steamid64": return p.SteamId64;
                    case "name": return Clean(p.Name);
                }
            }
            return values.TryGetValue(key, out var v) ? Clean(v) : m.Value;
        });
    }

    public static string Clean(string s) => s.Replace("\"", "'").Replace(";", ",").Replace("\r", " ").Replace("\n", " ").Trim();

    /// <summary>
    /// The fields of a dialog (player menu items, buttons of addon tabs): an array of { id, text, type, default,
    /// choices, editable }. One field per id (a second one would have no value of its own).
    /// </summary>
    public static List<PlayerActionField> ParseFields(JsonElement owner)
    {
        var fields = new List<PlayerActionField>();
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("fields", out var fs) || fs.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return fields;
        foreach (var f in LuaJson.Items(fs))
        {
            if (f.ValueKind != JsonValueKind.Object || StatsVm.Str(f, "id") is not { Length: > 0 } fid || fields.Any(x => x.Id == fid)) continue;
            fields.Add(new PlayerActionField(fid, StatsVm.Str(f, "text") ?? fid, f.TryGetProperty("default", out var d) ? LuaJson.Plain(d) : "",
                StatsVm.Str(f, "type") == "number", LuaJson.Choices(f, "choices"), LuaJson.Flag(f, "editable", false)));
        }
        return fields;
    }

    /// <summary>
    /// The values of a dialog as Lua gets them: numbers for number fields, else the text. Null when they are all
    /// good, else what is wrong ("Minutes: a number is needed."). <paramref name="values"/> gets the numbers
    /// written plainly ("1.5"), for commands.
    /// </summary>
    public static string? TypedValues(IReadOnlyList<PlayerActionField> fields, Dictionary<string, string> values, out Dictionary<string, object> typed)
    {
        typed = new Dictionary<string, object>();
        foreach (var f in fields)
        {
            var v = values.GetValueOrDefault(f.Id, f.Default);
            if (f.Number)
            {
                // "NaN", "Infinity", "1e400" parse as numbers, but are none (and JSON cannot carry them).
                if (!LuaJson.TryNumber(v, out var n)) return $"{f.Label}: a number is needed.";
                v = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                typed[f.Id] = n;
            }
            else typed[f.Id] = v;
            values[f.Id] = v;
        }
        return null;
    }

    /// <summary>The "pa" message of the addon.</summary>
    public static PlayerActionDef FromLua(JsonElement m)
    {
        var icon = StatsVm.Str(m, "icon");
        var order = StatsVm.Num(m, "order");
        return new PlayerActionDef
        {
            Id = "lua:" + (StatsVm.Str(m, "id") ?? "?"),
            Text = StatsVm.Str(m, "text") ?? StatsVm.Str(m, "id") ?? "?",
            Icon = string.IsNullOrWhiteSpace(icon) ? "" : LuaTabVm.ParseIcon(icon),
            Order = double.IsNaN(order) ? 300 : (int)Math.Clamp(order, -100000, 100000),
            Command = StatsVm.Str(m, "command"),
            Confirm = StatsVm.Str(m, "confirm"),
            Danger = LuaJson.Flag(m, "danger", false),
            Bots = LuaJson.Flag(m, "bots", true),
            Multi = LuaJson.Flag(m, "multi", true),
            Filtered = LuaJson.Flag(m, "filtered", false),
            LuaRun = LuaJson.Flag(m, "run", false),
            Fields = ParseFields(m),
        };
    }
}
