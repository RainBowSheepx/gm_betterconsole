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

public sealed record ProfileRow(string Key, double MsPerSec, double CallsPerSec, double MaxMs, double BytesPerSec, string? Source)
{
    public string MsText => MsPerSec >= 100 ? MsPerSec.ToString("F0") : MsPerSec.ToString("F2");
    public string CallsText => CallsPerSec >= 100 ? CallsPerSec.ToString("F0") : CallsPerSec.ToString("F1");
    public string MaxText => MaxMs.ToString("F2");
    public string BytesText => FormatBytes(BytesPerSec) + "/s";

    public static string FormatBytes(double b) =>
        b >= 1024 * 1024 ? $"{b / (1024 * 1024):F2} MB" : b >= 1024 ? $"{b / 1024:F1} KB" : $"{b:F0} B";
}

public sealed record EntityClassRow(string Class, int Count);

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

    public void ApplyProfile(JsonElement m)
    {
        Fill(Hooks, m, "hooks");
        Fill(Timers, m, "timers");
        Fill(NetReceive, m, "netin");
        Fill(NetSend, m, "netout");
        EntityClasses.Clear();
        if (m.TryGetProperty("ents", out var ents) && ents.ValueKind == JsonValueKind.Array)
            foreach (var e in ents.EnumerateArray())
                EntityClasses.Add(new EntityClassRow(Str(e, "k") ?? "?", (int)Num(e, "n")));
        double since = m.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
        ProfilingInfo = $"Profiling for {TimeSpan.FromSeconds(since):mm\\:ss} · averages per second since the start, refreshed every second";
    }

    private static void Fill(ObservableCollection<ProfileRow> target, JsonElement m, string name)
    {
        target.Clear();
        if (!m.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var e in arr.EnumerateArray())
            target.Add(new ProfileRow(Str(e, "k") ?? "?", Num0(e, "ms"), Num0(e, "n"), Num0(e, "max"), Num0(e, "b"), Str(e, "src")));
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double Num0(JsonElement e, string name) => Num(e, name) is var v && double.IsNaN(v) ? 0 : v;

    internal static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
}
