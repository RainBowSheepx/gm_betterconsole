using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BetterConsole.App.Controls;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Views;

public partial class StatsView : UserControl
{
    private readonly ServerViewModel _vm;
    private readonly TimeSeriesChart[] _charts;

    public StatsView(ServerViewModel vm)
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
        SetupProfileGrid(HooksGrid, nameof(ProfileRow.MsPerSec));
        SetupProfileGrid(TimersGrid, nameof(ProfileRow.MsPerSec));
        SetupProfileGrid(NetInGrid, nameof(ProfileRow.MsPerSec));
        SetupProfileGrid(NetOutGrid, nameof(ProfileRow.BytesPerSec));
        SetupProfileGrid(EntsGrid, nameof(EntityClassRow.Count));
        s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatsVm.Profiling) or nameof(StatsVm.ProfilingInfo) or nameof(StatsVm.HasProfile)) UpdateProfiler();
            if (e.PropertyName is nameof(StatsVm.TickRate)) { UpdateBudget(); UpdateCapture(); }
            if (e.PropertyName is nameof(StatsVm.Capture)) UpdateCapture();
        };
        void UpdateNoSpikes() => NoSpikes.Visibility = s.Spikes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        s.Spikes.CollectionChanged += (_, _) => UpdateNoSpikes();
        UpdateNoSpikes();
        UpdateCapture();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ServerViewModel.BridgeConnected)) UpdateNote(); };
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

    /// <summary>
    /// Biggest first. The order follows the numbers as they change (live sorting) but holds still while
    /// the pointer is over the table, so the row being read does not move away.
    /// </summary>
    private static void SetupProfileGrid(DataGrid grid, string sortBy)
    {
        grid.Items.SortDescriptions.Add(new SortDescription(sortBy, ListSortDirection.Descending));
        foreach (var col in grid.Columns)
            if (col.SortMemberPath == sortBy) col.SortDirection = ListSortDirection.Descending;
        if (grid.Items is ICollectionViewLiveShaping { CanChangeLiveSorting: true } live)
        {
            live.IsLiveSorting = true;
            grid.MouseEnter += (_, _) => live.IsLiveSorting = false;
            grid.MouseLeave += (_, _) => live.IsLiveSorting = true;
        }
    }

    private void UpdateProfiler()
    {
        var s = _vm.Stats;
        bool on = s.Profiling;
        // The last results stay after stopping, until the next start.
        bool show = on || s.HasProfile;
        ProfTables.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProfLabel.Text = on ? "Stop profiling" : "Start profiling";
        ProfIcon.Text = on ? "" : "";
        ProfButton.SetResourceReference(StyleProperty, on ? "Btn.Danger" : "Btn.Primary");
        ProfInfo.Text = show && !string.IsNullOrEmpty(s.ProfilingInfo)
            ? s.ProfilingInfo
            : "Times every hook, timer and net message handler and counts outgoing net messages and entities. Costs 1-3 µs per call while it runs, so switch it off when done.";
    }

    private void OnProfile(object sender, RoutedEventArgs e) => _vm.SetProfiling(!_vm.Stats.Profiling);

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        _vm.SetCapture(CaptureButton.IsChecked == true);
        // Refused (no addon): the button goes back to what the capture is.
        UpdateCapture();
    }

    private void OnClearSpikes(object sender, RoutedEventArgs e) => _vm.Stats.Spikes.Clear();

    private void UpdateCapture()
    {
        var s = _vm.Stats;
        CaptureButton.IsChecked = s.Capture;
        CaptureLabel.Text = s.Capture ? "Detailed capture: on" : "Detailed capture";
        double tick = double.IsNaN(s.TickRate) || s.TickRate <= 0 ? 33 : s.TickRate;
        double limit = Math.Max(3000 / tick, 50);
        var longer = 3000 / tick >= 50 ? $"Frames longer than {limit:F0} ms (3 ticks)" : $"Frames longer than {limit:F0} ms";
        SpikesInfo.Text = s.Capture
            ? longer + ". Detailed capture is on: each long frame names its slowest hooks, timers and net messages (Lua) and what the engine did in it (vprof; the engine reports one long frame a second at most). It costs 1-3 µs per Lua call: switch it off when done."
            : longer + ", newest first, with what they were made of: CPU time, Lua collector, physics, entities, players joining, the slowest timer. Detailed capture names the hooks, timers and net messages of each long frame and adds the engine's profile (vprof). Double-click a Lua name to open its file.";
    }

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
