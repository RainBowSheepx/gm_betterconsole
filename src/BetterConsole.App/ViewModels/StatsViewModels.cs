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
    public string? Source { get; } = source;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MsText))] private double msPerSec;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CallsText))] private double callsPerSec;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MaxText))] private double maxMs;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BytesText))] private double bytesPerSec;

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

public sealed record SpikeRow(DateTime Time, double Ms, double? BusyMs)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string MsText => $"{Ms:F0} ms";
    public string BusyText => BusyMs is { } b ? $"{b:F0} ms CPU" : "";
    /// <summary>A long frame with little CPU time means the thread was waiting (disk, console, OS).</summary>
    public string Hint => BusyMs is { } b && b < Ms * 0.4 ? "mostly waiting (disk, console window, other processes?)" : "busy";
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
    [ObservableProperty] private string profilingInfo = "";
    [ObservableProperty] private bool hasLuaStats;

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
        Merge(Hooks, m, "hooks");
        Merge(Timers, m, "timers");
        Merge(NetReceive, m, "netin");
        Merge(NetSend, m, "netout");
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

    /// <summary>Updates the rows in place. Entries the addon did not send this time (out of its top list) keep their last numbers.</summary>
    private void Merge(ObservableCollection<ProfileRow> target, JsonElement m, string name)
    {
        if (!m.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        if (!_rows.TryGetValue(target, out var byKey)) _rows[target] = byKey = new Dictionary<string, ProfileRow>();
        foreach (var e in arr.EnumerateArray())
        {
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
        }
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double Num0(JsonElement e, string name) => Num(e, name) is var v && double.IsNaN(v) ? 0 : v;

    internal static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
}
