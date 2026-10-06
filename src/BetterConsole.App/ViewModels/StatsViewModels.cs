using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterConsole.App.ViewModels;

/// <summary>A fixed-size ring of (time, value) samples, one per second, up to an hour.</summary>
public sealed class TimeSeries
{
    private readonly double[] _t;
    private readonly double[] _v;
    private int _start, _count;

    public TimeSeries(string name, int capacity = 3600)
    {
        Name = name;
        _t = new double[capacity];
        _v = new double[capacity];
    }

    public string Name { get; }
    public int Count => _count;
    public double LastValue => _count == 0 ? double.NaN : _v[(_start + _count - 1) % _t.Length];
    public double LastTime => _count == 0 ? double.NaN : _t[(_start + _count - 1) % _t.Length];

    public void Add(double time, double value)
    {
        int i = (_start + _count) % _t.Length;
        _t[i] = time;
        _v[i] = value;
        if (_count < _t.Length) _count++;
        else _start = (_start + 1) % _t.Length;
    }

    /// <summary>Samples with time >= <paramref name="from"/>, oldest first.</summary>
    public IEnumerable<(double T, double V)> Since(double from)
    {
        for (int k = 0; k < _count; k++)
        {
            int i = (_start + k) % _t.Length;
            if (_t[i] >= from) yield return (_t[i], _v[i]);
        }
    }

    public (double Min, double Max) Range(double from)
    {
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        foreach (var (_, v) in Since(from))
        {
            if (double.IsNaN(v)) continue;
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        return (mn, mx);
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }
}

/// <summary>
/// One line of a profiler table. Rows are updated in place (the tables keep their scroll position and
/// selection) and stay when an entry drops out of the addon's top list.
/// </summary>
public sealed partial class ProfileRow(string key, string? source) : ObservableObject
{
    public string Key { get; } = key;
    /// <summary>Where the function is: "addons/x/lua/y.lua:12" (timers and net messages too).</summary>
    public string? Source { get; } = source;
    public string? SourceTip => string.IsNullOrEmpty(Source) ? null : "Defined in " + Source;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MsText))] private double msPerSec;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CallsText))] private double callsPerSec;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MaxText))] private double maxMs;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BytesText))] private double bytesPerSec;
    /// <summary>Lua memory allocated per second, KB (an addon's profiler may measure it).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(KbText))] private double kbPerSec;

    public string KbText => KbPerSec >= 100 ? KbPerSec.ToString("F0") : KbPerSec.ToString("F1");
    public string MsText => MsPerSec >= 100 ? MsPerSec.ToString("F0") : MsPerSec.ToString("F2");
    public string CallsText => CallsPerSec >= 100 ? CallsPerSec.ToString("F0") : CallsPerSec.ToString("F1");
    public string MaxText => MaxMs.ToString("F2");
    public string BytesText => FormatBytes(BytesPerSec) + "/s";

    public static string FormatBytes(double b) =>
        b >= 1024 * 1024 ? $"{b / (1024 * 1024):F2} MB" : b >= 1024 ? $"{b / 1024:F1} KB" : $"{b:F0} B";
}

public sealed partial class EntityClassRow(string @class) : ObservableObject
{
    public string Class { get; } = @class;
    [ObservableProperty] private int count;
}

/// <summary>One reason a long frame was long: a chip on its row ("timer shop_sync 120 ms", "physics 40 ms").</summary>
/// <param name="Kind">"danger", "warning", "accent" or "muted" (the chip's colour).</param>
/// <param name="Source">Where the Lua function is (double-click opens it), if it is one.</param>
public sealed record SpikeCause(string Text, string Kind, string? Tooltip, string? Source = null);

/// <summary>A line of a long frame's breakdown: a Lua callback of that frame, or a scope of the engine profiler.</summary>
public sealed record SpikePart(string Text, string MsText, string? Tooltip, string? Source = null);

/// <summary>
/// A frame longer than three ticks and what it was made of: CPU time, the Lua collector, physics, entities,
/// players, the slowest timer (always), the slowest Lua callbacks and the engine's profile (with the
/// detailed capture). The engine part arrives separately (from the console) and is added later.
/// </summary>
public sealed partial class SpikeRow : ObservableObject
{
    public SpikeRow(DateTime time, double ms, double? busyMs, bool precise = false)
    {
        Time = time;
        Ms = ms;
        BusyMs = busyMs;
        IsPrecise = precise;
    }

    /// <summary>When the addon noticed the frame (the next Think after it).</summary>
    public DateTime Time { get; }
    /// <summary>The time is to the millisecond (the addon's SysTime on this machine's clock), not the whole second.</summary>
    public bool IsPrecise { get; }
    public double Ms { get; }
    public double? BusyMs { get; }
    public string TimeText => Time.ToString("HH:mm:ss");
    public string MsText => $"{Ms:F0} ms";
    public string BusyText => BusyMs is { } b ? $"CPU {b:F0} ms" : "";
    public ObservableCollection<SpikeCause> Causes { get; } = new();
    public ObservableCollection<SpikePart> Lua { get; } = new();
    public ObservableCollection<SpikePart> Engine { get; } = new();
    [ObservableProperty] private bool hasLua;
    [ObservableProperty] private bool hasEngine;
    /// <summary>The engine's part came from an addon (OnSpike): vprof's report of this frame is not shown on it.</summary>
    public bool EngineFromAddon { get; private set; }
    /// <summary>Such a row took the report of its frame (and dropped it): it is no candidate for other reports.</summary>
    public bool VprofTaken { get; set; }

    private static string Kind(string kind) => kind switch { "hooks" => "hook", "timers" => "timer", "netin" => "net", _ => kind };

    /// <summary>
    /// A spike of the addon's "stats" message. <paramref name="clockOffset"/>: Unix time minus the addon's
    /// SysTime (NaN when not known yet).
    /// </summary>
    public static SpikeRow From(JsonElement s, double clockOffset = double.NaN)
    {
        var t = StatsVm.Num(s, "time");
        var sys = StatsVm.Num(s, "st");
        var b = StatsVm.Num(s, "busy");
        bool precise = !double.IsNaN(sys) && !double.IsNaN(clockOffset);
        var time = precise ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round((sys + clockOffset) * 1000)).LocalDateTime
            : double.IsNaN(t) ? DateTime.Now : DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime;
        var row = new SpikeRow(time, StatsVm.Num0(s, "ms"), double.IsNaN(b) ? null : b, precise);
        double ms = row.Ms;

        if (row.BusyMs is { } busy && busy < ms * 0.4)
            row.Causes.Add(new($"no CPU for {ms - busy:F0} ms", "warning",
                "The game thread did not run for most of this frame. It waited: for the disk (a model, a sound or a map file read the first time), " +
                "for the srcds console window (text selected in it stops the server), or other programs had the processor. On a VPS also: the host " +
                "gave the real processor to other virtual machines for a while (steal time, typical of shared vCPUs)."));
        if (s.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.True)
            row.Causes.Add(new("map start", "muted", "The first seconds after a map loaded: files, models and Lua are still being loaded."));

        // The slowest Lua of the frame: the capture's callbacks, else the slowest timer (always known).
        if (s.TryGetProperty("cbs", out var cbs) && cbs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cbs.EnumerateArray())
            {
                double cms = StatsVm.Num0(c, "ms");
                var kind = Kind(StatsVm.Str(c, "kind") ?? "");
                var name = $"{kind} {StatsVm.Str(c, "k") ?? "?"}";
                var n = StatsVm.Num0(c, "n");
                var bytes = StatsVm.Num0(c, "b");
                var who = StatsVm.Str(c, "who");
                var extra = (n > 1 ? $" ×{n:F0}" : "") + (bytes > 0 ? $" · {ProfileRow.FormatBytes(bytes)}" : "") + (who != null ? $" from {who}" : "");
                var src = StatsVm.Str(c, "src");
                row.Lua.Add(new(name + extra, cms >= 10 ? $"{cms:F0} ms" : $"{cms:F1} ms", src != null ? "Defined in " + src : null, src));
            }
            if (row.Lua.FirstOrDefault() is { } top && StatsVm.Num0(cbs[0], "ms") >= ms * 0.05)
                row.Causes.Add(new($"{top.Text} {top.MsText}", "accent", top.Tooltip, top.Source));
        }
        else if (s.TryGetProperty("timer", out var tm) && tm.ValueKind == JsonValueKind.Object && StatsVm.Num0(tm, "ms") >= ms * 0.05)
        {
            var src = StatsVm.Str(tm, "src");
            row.Causes.Add(new($"timer {StatsVm.Str(tm, "k") ?? "?"} {StatsVm.Num0(tm, "ms"):F0} ms", "accent", src != null ? "Defined in " + src : null, src));
        }
        // What addons know about the frame (BetterConsole.Stats:OnSpike): after the companion's own Lua.
        foreach (var p in LuaJson.Items(s.TryGetProperty("lua", out var xl) ? xl : default))
            if (Part(p) is { } part) row.Lua.Add(part);
        row.HasLua = row.Lua.Count > 0;

        var gc = StatsVm.Num(s, "gc");
        if (!double.IsNaN(gc))
        {
            var heap = StatsVm.Num(s, "heap");
            row.Causes.Add(new($"Lua GC freed {gc:F0} MB", "warning",
                $"The Lua garbage collector ran in this frame (Lua memory {heap + gc:F0} → {heap:F0} MB). Addons that make lots of garbage " +
                "(tables and strings built every frame) make these steps long."));
        }
        var phys = StatsVm.Num(s, "phys");
        if (!double.IsNaN(phys) && phys >= Math.Max(5, ms * 0.15))
            row.Causes.Add(new($"physics {phys:F0} ms", "accent", "The physics simulation of the frame's last tick: many props touching each other, a big contraption, ragdolls."));
        var ents = StatsVm.Num(s, "ents");
        if (!double.IsNaN(ents) && Math.Abs(ents) >= 30)
            row.Causes.Add(new($"{ents:+0;-0} entities", "muted", "Entities created or removed in this frame: a dupe pasted, a cleanup, a wave of NPCs."));
        if (s.TryGetProperty("joined", out var j) && j.ValueKind == JsonValueKind.Array && j.GetArrayLength() > 0)
            row.Causes.Add(new("joined: " + string.Join(", ", j.EnumerateArray().Select(x => x.GetString())), "muted", "Players who spawned in this frame."));
        foreach (var c in LuaJson.Items(s.TryGetProperty("causes", out var xc) ? xc : default))
        {
            if (StatsVm.Str(c, "text") is not { Length: > 0 } text) continue;
            var kind = StatsVm.Str(c, "kind") is { } k && k is "danger" or "warning" or "accent" or "muted" ? k : "accent";
            row.Causes.Add(new(text, kind, StatsVm.Str(c, "tooltip"), StatsVm.Str(c, "src")));
        }
        // The engine's part from an addon: in place of vprof's report (the app does not wait for one then).
        foreach (var p in LuaJson.Items(s.TryGetProperty("engine", out var xe) ? xe : default))
            if (Part(p) is { } part) row.Engine.Add(part);
        row.HasEngine = row.EngineFromAddon = row.Engine.Count > 0;
        // Nothing else explains it (a map that just started explains little of a long frame).
        if (row.Causes.All(c => c.Text == "map start"))
            row.Causes.Add(new(row.BusyMs is { } bb ? $"busy {bb:F0} ms" : "busy", "muted",
                "Nothing BetterConsole measures all the time explains it. Detailed capture names the hooks, timers and net messages of each " +
                "long frame and adds the engine's own profile (vprof)."));
        return row;
    }

    private static SpikePart? Part(JsonElement p)
    {
        if (StatsVm.Str(p, "text") is not { Length: > 0 } text) return null;
        var ms = StatsVm.Num(p, "ms");
        var src = StatsVm.Str(p, "src");
        return new SpikePart(text, double.IsNaN(ms) ? "" : ms >= 10 ? $"{ms:F0} ms" : $"{ms:F1} ms", StatsVm.Str(p, "tooltip") ?? (src != null ? "Defined in " + src : null), src);
    }

    /// <summary>The engine profiler's report of this frame: its scopes by their own time.</summary>
    public void AttachEngine(BetterConsole.Core.Console.VprofReport report)
    {
        Engine.Clear();
        foreach (var sc in report.Scopes.Where(x => x.ExclusiveMs >= 0.5).Take(6))
        {
            // The plain name on the row, the engine's in the tooltip.
            var what = BetterConsole.Core.Console.VprofReport.Describe(sc.Name);
            Engine.Add(new(what ?? sc.Name,
                sc.ExclusiveMs >= 10 ? $"{sc.ExclusiveMs:F0} ms" : $"{sc.ExclusiveMs:F1} ms",
                $"{sc.Name}: {sc.ExclusiveMs:F2} ms of its own, {sc.InclusiveMs:F2} ms with what it called, {sc.Calls:0.#} calls"));
        }
        HasEngine = Engine.Count > 0;
    }
}

/// <summary>Everything on the Statistics tab.</summary>
public sealed partial class StatsVm : ObservableObject
{
    public TimeSeries Fps { get; } = new("Server FPS");
    public TimeSeries FrameMs { get; } = new("Frame time");
    public TimeSeries FrameMaxMs { get; } = new("Longest frame");
    public TimeSeries Busy { get; } = new("Game thread CPU per frame");
    public TimeSeries Load { get; } = new("Game thread load");
    public TimeSeries Tps { get; } = new("Ticks per second");
    public TimeSeries Cpu { get; } = new("Process CPU");
    public TimeSeries MemoryMB { get; } = new("Process memory");
    public TimeSeries LuaMB { get; } = new("Lua memory");
    public TimeSeries Players { get; } = new("Players");
    public TimeSeries Entities { get; } = new("Entities");
    public TimeSeries NetIn { get; } = new("Net in");
    public TimeSeries NetOut { get; } = new("Net out");

    [ObservableProperty] private double tickRate = double.NaN;
    [ObservableProperty] private string fpsText = "—";
    [ObservableProperty] private string fpsSub = "";
    [ObservableProperty] private string frameText = "—";
    [ObservableProperty] private string frameSub = "";
    [ObservableProperty] private string loadText = "—";
    [ObservableProperty] private string loadSub = "";
    [ObservableProperty] private string tickText = "—";
    [ObservableProperty] private string tickSub = "";
    [ObservableProperty] private string cpuText = "—";
    [ObservableProperty] private string cpuSub = "";
    [ObservableProperty] private string memoryText = "—";
    [ObservableProperty] private string memorySub = "";
    [ObservableProperty] private string luaText = "—";
    [ObservableProperty] private string luaSub = "";
    [ObservableProperty] private string playersText = "—";
    [ObservableProperty] private string playersSub = "";
    [ObservableProperty] private string entitiesText = "—";
    [ObservableProperty] private string entitiesSub = "";
    [ObservableProperty] private string netText = "—";
    [ObservableProperty] private string netSub = "";
    [ObservableProperty] private string uptimeText = "—";
    [ObservableProperty] private string uptimeSub = "";

    [ObservableProperty] private bool profiling;
    /// <summary>Detailed capture of lag spikes: the Lua callbacks of each long frame and the engine profile (vprof).</summary>
    [ObservableProperty] private bool capture;
    [ObservableProperty] private string profilingInfo = "";
    [ObservableProperty] private bool hasLuaStats;

    /// <summary>An addon's profiler (BetterConsole.SetProfiler) has the profiler card and the capture: its name, or null.</summary>
    [ObservableProperty] private string? providerName;
    /// <summary>What the addon's profiler measures and costs (in place of the built-in description).</summary>
    [ObservableProperty] private string? providerInfo;
    /// <summary>The app may type vprof commands for the capture (an addon's profiler can forbid it: vprof = false).</summary>
    [ObservableProperty] private bool providerVprof = true;
    /// <summary>The addon's profiler has a detailed capture of its own.</summary>
    [ObservableProperty] private bool providerCapture;

    /// <summary>The provider of the hello or prof_provider message (none: the built-in profiler).</summary>
    public void SetProvider(JsonElement p)
    {
        var name = p.ValueKind == JsonValueKind.Object ? Str(p, "name") : null;
        if (name != ProviderName)
        {
            // Another profiler: the last results were the previous one's.
            foreach (var c in new[] { Hooks, Timers, NetReceive, NetSend }) c.Clear();
            _rows.Clear();
            _fields.Clear();
            ExtraTables.Clear();
            EntityClasses.Clear();
            _entityRows.Clear();
            ProfilingInfo = "";
            ColumnsChanged?.Invoke();
            OnPropertyChanged(nameof(HasProfile));
        }
        ProviderName = name;
        ProviderInfo = ProviderName != null ? Str(p, "info") : null;
        ProviderVprof = ProviderName == null || LuaJson.Flag(p, "vprof", true);
        ProviderCapture = ProviderName != null && LuaJson.Flag(p, "capture", false);
    }

    /// <summary>Tables of an addon's profiler under the built-in ones (the "extra" of its reports), updated in place.</summary>
    public ObservableCollection<TableWidgetVm> ExtraTables { get; } = new();

    // Which numbers the rows of each table have had since profiling started: a column without any is not shown
    // (the "max" of a profiler that does not measure it, the "kb" the built-in one does not).
    private readonly Dictionary<ObservableCollection<ProfileRow>, HashSet<string>> _fields = new();

    /// <summary>Some row of that table had this number ("ms", "n", "max", "b", "kb").</summary>
    public bool HasField(ObservableCollection<ProfileRow> table, string field) => _fields.TryGetValue(table, out var f) && f.Contains(field);

    /// <summary>Raised after a report changed which columns have numbers.</summary>
    public event Action? ColumnsChanged;

    public ObservableCollection<SpikeRow> Spikes { get; } = new();
    public ObservableCollection<ProfileRow> Hooks { get; } = new();
    public ObservableCollection<ProfileRow> Timers { get; } = new();
    public ObservableCollection<ProfileRow> NetReceive { get; } = new();
    public ObservableCollection<ProfileRow> NetSend { get; } = new();
    public ObservableCollection<EntityClassRow> EntityClasses { get; } = new();

    /// <summary>Raised after new samples were added (charts redraw).</summary>
    public event Action? Updated;

    public void RaiseUpdated() => Updated?.Invoke();

    public static double Now() => DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;

    public void Clear()
    {
        foreach (var s in new[] { Fps, FrameMs, FrameMaxMs, Busy, Load, Tps, Cpu, MemoryMB, LuaMB, Players, Entities, NetIn, NetOut }) s.Clear();
        Spikes.Clear();
    }

    private readonly Dictionary<ObservableCollection<ProfileRow>, Dictionary<string, ProfileRow>> _rows = new();
    private readonly Dictionary<string, EntityClassRow> _entityRows = new();
    private double _profiledFor;

    /// <summary>True while there are results to show: during profiling and after it stopped.</summary>
    public bool HasProfile => Hooks.Count + Timers.Count + NetReceive.Count + NetSend.Count + EntityClasses.Count > 0;

    partial void OnProfilingChanged(bool value)
    {
        if (value)
        {
            // A new session starts from zero, like the numbers in the addon.
            foreach (var c in new[] { Hooks, Timers, NetReceive, NetSend }) c.Clear();
            _rows.Clear();
            _fields.Clear();
            ExtraTables.Clear();
            ColumnsChanged?.Invoke();
            EntityClasses.Clear();
            _entityRows.Clear();
            _profiledFor = 0;
            ProfilingInfo = "Profiling · waiting for the first numbers…";
        }
        else if (HasProfile)
        {
            ProfilingInfo = $"Stopped after {TimeSpan.FromSeconds(_profiledFor):mm\\:ss}. These are the last results (averages per second over that time); they stay until the next start.";
        }
        OnPropertyChanged(nameof(HasProfile));
    }

    public void ApplyProfile(JsonElement m)
    {
        if (!Profiling) return;
        bool fieldsChanged = false;
        fieldsChanged |= Merge(Hooks, m, "hooks");
        fieldsChanged |= Merge(Timers, m, "timers");
        fieldsChanged |= Merge(NetReceive, m, "netin");
        fieldsChanged |= Merge(NetSend, m, "netout");
        if (fieldsChanged) ColumnsChanged?.Invoke();
        if (m.TryGetProperty("extra", out var extra) && extra.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            // The tables it sends now; one it stopped sending goes.
            var ids = LuaJson.Items(extra).Select(x => Str(x, "id")).Where(id => id != null).ToHashSet();
            for (int i = ExtraTables.Count - 1; i >= 0; i--)
                if (!ids.Contains(ExtraTables[i].Id)) ExtraTables.RemoveAt(i);
            foreach (var x in LuaJson.Items(extra))
            {
                if (Str(x, "id") is not { } id) continue;
                var table = ExtraTables.FirstOrDefault(t => t.Id == id);
                if (table == null)
                {
                    // Keyed by the first column unless it says otherwise: the rows stay in place from report to report.
                    table = new TableWidgetVm { Id = id, Kind = "table", DefaultKey = 0 };
                    ExtraTables.Add(table);
                }
                table.Configure(x);
                if (x.TryGetProperty("rows", out var rows)) table.Apply("set", rows);
            }
        }
        if (m.TryGetProperty("ents", out var ents) && ents.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>();
            foreach (var e in ents.EnumerateArray())
            {
                var cls = Str(e, "k") ?? "?";
                seen.Add(cls);
                if (!_entityRows.TryGetValue(cls, out var row))
                {
                    row = new EntityClassRow(cls);
                    _entityRows[cls] = row;
                    EntityClasses.Add(row);
                }
                row.Count = (int)Num0(e, "n");
            }
            // The addon sends every class: one that is missing has no entities left.
            for (int i = EntityClasses.Count - 1; i >= 0; i--)
            {
                if (seen.Contains(EntityClasses[i].Class)) continue;
                _entityRows.Remove(EntityClasses[i].Class);
                EntityClasses.RemoveAt(i);
            }
        }
        _profiledFor = m.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
        ProfilingInfo = $"Profiling for {TimeSpan.FromSeconds(_profiledFor):mm\\:ss} · averages per second since the start, refreshed every second";
        OnPropertyChanged(nameof(HasProfile));
    }

    /// <summary>
    /// Updates the rows in place. Entries the addon did not send this time (out of its top list) keep their last
    /// numbers. True when a number turned up that no row of this table had before.
    /// </summary>
    private bool Merge(ObservableCollection<ProfileRow> target, JsonElement m, string name)
    {
        if (!m.TryGetProperty(name, out var arr) || arr.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return false;
        if (!_rows.TryGetValue(target, out var byKey)) _rows[target] = byKey = new Dictionary<string, ProfileRow>();
        if (!_fields.TryGetValue(target, out var fields)) _fields[target] = fields = new HashSet<string>();
        bool added = false;
        foreach (var e in LuaJson.Items(arr))
        {
            foreach (var f in ProfileFields)
                if (e.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.Number) added |= fields.Add(f);
            var key = Str(e, "k") ?? "?";
            if (!byKey.TryGetValue(key, out var row))
            {
                row = new ProfileRow(key, Str(e, "src"));
                byKey[key] = row;
                target.Add(row);
            }
            row.MsPerSec = Num0(e, "ms");
            row.CallsPerSec = Num0(e, "n");
            row.MaxMs = Num0(e, "max");
            row.BytesPerSec = Num0(e, "b");
            row.KbPerSec = Num0(e, "kb");
        }
        return added;
    }

    private static readonly string[] ProfileFields = ["ms", "n", "max", "b", "kb"];

    // Lua's util.TableToJSON writes an empty table as [], so a "table of options" is not always an object.
    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double Num0(JsonElement e, string name) => Num(e, name) is var v && double.IsNaN(v) ? 0 : v;

    internal static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
}
