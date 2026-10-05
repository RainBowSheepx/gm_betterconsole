using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BetterConsole.App.Controls;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Views;

public partial class StatsView : UserControl
{
    private readonly MainViewModel _vm;
    private readonly TimeSeriesChart[] _charts;

    public StatsView(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        var s = vm.Stats;

        FrameChart.Add(s.FrameMs, "avg", "Chart1").Add(s.FrameMaxMs, "max", "Chart2");
        LoadChart.Add(s.Load, "game thread", "Chart1").Add(s.Cpu, "process CPU", "Chart4");
        FpsChart.Add(s.Fps, "fps", "Chart3").Add(s.Tps, "ticks", "Chart1");
        MemChart.Add(s.MemoryMB, "process", "Chart5").Add(s.LuaMB, "Lua", "Chart2");
        NetChart.Add(s.NetIn, "in", "Chart3").Add(s.NetOut, "out", "Chart1");
        PlayersChart.Add(s.Players, "players", "Chart1");
        EntsChart.Add(s.Entities, "entities", "Chart4");
        _charts = [FrameChart, LoadChart, FpsChart, MemChart, NetChart, PlayersChart, EntsChart];

        SetWindow(vm.Settings.StatsWindowMinutes);
        s.Updated += OnUpdated;
        s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatsVm.Profiling) or nameof(StatsVm.ProfilingInfo)) UpdateProfiler();
            if (e.PropertyName is nameof(StatsVm.TickRate)) UpdateBudget();
        };
        s.Spikes.CollectionChanged += (_, _) => NoSpikes.Visibility = s.Spikes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MainViewModel.BridgeConnected)) UpdateNote(); };
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) OnUpdated(); };
        UpdateBudget();
        UpdateProfiler();
        UpdateNote();
    }

    private void OnUpdated()
    {
        if (!IsVisible) return;
        foreach (var c in _charts) c.Refresh();
    }

    private void UpdateBudget()
    {
        var tr = _vm.Stats.TickRate;
        if (double.IsNaN(tr) || tr <= 0) return;
        FrameChart.Reference = 1000.0 / tr;
        FrameChart.ReferenceLabel = $"tick {1000.0 / tr:F1} ms";
        FpsChart.Reference = tr;
        FpsChart.ReferenceLabel = $"tick rate {tr:F0}";
    }

    private void UpdateNote()
    {
        Note.Text = _vm.BridgeConnected
            ? "Numbers from the companion addon inside the server, one sample per second."
            : "Only process numbers (CPU, memory) until the companion addon connects.";
    }

    private void UpdateProfiler()
    {
        bool on = _vm.Stats.Profiling;
        ProfTables.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ProfLabel.Text = on ? "Stop profiling" : "Start profiling";
        ProfIcon.Text = on ? "" : "";
        ProfButton.SetResourceReference(StyleProperty, on ? "Btn.Danger" : "Btn.Primary");
        if (on && !string.IsNullOrEmpty(_vm.Stats.ProfilingInfo)) ProfInfo.Text = _vm.Stats.ProfilingInfo;
        else if (!on) ProfInfo.Text = "Times every hook, timer and net message handler and counts outgoing net messages and entities. Costs 1-3 µs per call while it runs, so switch it off when done.";
    }

    private void OnProfile(object sender, RoutedEventArgs e) => _vm.SetProfiling(!_vm.Stats.Profiling);

    private void OnWindow(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } && int.TryParse(tag, out var minutes)) SetWindow(minutes);
    }

    private void SetWindow(int minutes)
    {
        minutes = minutes is 1 or 5 or 15 or 60 ? minutes : 5;
        _vm.Settings.StatsWindowMinutes = minutes;
        W1.IsChecked = minutes == 1;
        W5.IsChecked = minutes == 5;
        W15.IsChecked = minutes == 15;
        W60.IsChecked = minutes == 60;
        foreach (var c in _charts) c.WindowSeconds = minutes * 60;
    }
}
