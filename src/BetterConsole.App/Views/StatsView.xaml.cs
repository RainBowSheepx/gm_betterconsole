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
        vm.StatsExtras.Widgets.CollectionChanged += (_, _) => SyncExtras();
        vm.StatsHiddenChanged += ApplyHidden;
        UpdateBudget();
        UpdateProfiler();
        UpdateNote();
        SyncExtras();
        ApplyHidden();
    }

    private void OnUpdated()
    {
        if (!IsVisible) return;
        foreach (var c in _charts) c.Refresh();
        // The time axis of addon charts moves on too, also between their points.
        foreach (var c in _extraCharts.Values) c.Refresh();
    }

    // ------------------------------------------------------------------------------ addons and plugins

    // The built-in key numbers and charts by their id, in the order they have (10, 20, …): what addons place theirs by.
    private static readonly string[] KpiIds = ["fps", "frame", "load", "tick", "cpu", "memory", "lua", "players", "entities", "network", "uptime"];
    private static readonly string[] ChartIds = ["chart.frame", "chart.load", "chart.rate", "chart.memory", "chart.network", "chart.players", "chart.entities"];

    private readonly Dictionary<LuaWidgetVm, FrameworkElement> _extraViews = new();
    private readonly Dictionary<ChartWidgetVm, TimeSeriesChart> _extraCharts = new();

    /// <summary>
    /// The widgets of BetterConsole.Stats and of plugins: Stat cards with the key numbers, charts with the charts,
    /// the others below them; each by its order among the built-in ones.
    /// </summary>
    private void SyncExtras()
    {
        var widgets = _vm.StatsExtras.Widgets.ToList();
        foreach (var (w, view) in _extraViews.ToList())
        {
            if (widgets.Contains(w)) continue;
            if (view.Parent is Panel p) p.Children.Remove(view);
            _extraViews.Remove(w);
            if (w is ChartWidgetVm cw && _extraCharts.Remove(cw, out var chart)) cw.Updated -= chart.Refresh;
        }
        foreach (var w in widgets)
        {
            if (!_extraViews.ContainsKey(w))
            {
                _extraViews[w] = CreateView(w);
                // Defined again with another order or span: placed again.
                w.Reconfigured += () => { if (_extraViews.ContainsKey(w)) SyncExtras(); };
            }
            StatsLayout.SetSpan(_extraViews[w], SpanOf(w));
        }

        Place(Kpis, KpiIds, widgets.Where(w => w is StatWidgetVm));
        Place(Charts, ChartIds, widgets.Where(w => w is ChartWidgetVm));
        Place(Extras, [], widgets.Where(w => w is not (StatWidgetVm or ChartWidgetVm)));
        Extras.Visibility = Extras.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatsLayout.Arrange(Charts);
        StatsLayout.Arrange(Extras);
    }

    /// <summary>The built-in children (by their Tag) and the widgets' views, sorted by order; built-in ones first on a tie.</summary>
    private void Place(Panel panel, string[] builtInIds, IEnumerable<LuaWidgetVm> widgets)
    {
        var builtIn = panel.Children.OfType<FrameworkElement>().Where(e => e.Tag is string id && builtInIds.Contains(id))
            .Select(e => (Order: (Array.IndexOf(builtInIds, (string)e.Tag) + 1) * 10, Rank: 0, View: e));
        var extra = widgets.Select(w => (Order: w.Order, Rank: 1, View: _extraViews[w]));
        var wanted = builtIn.Concat(extra).OrderBy(x => x.Order).ThenBy(x => x.Rank).Select(x => x.View).ToList();
        if (panel.Children.Cast<FrameworkElement>().SequenceEqual(wanted)) return;
        panel.Children.Clear();
        foreach (var v in wanted)
        {
            if (v.Parent is Panel old) old.Children.Remove(v);
            panel.Children.Add(v);
        }
    }

    /// <summary>Charts are half wide like the built-in ones unless the addon said otherwise.</summary>
    private static int SpanOf(LuaWidgetVm w) => w is ChartWidgetVm && !w.SpanGiven ? 6 : w.Span;

    private FrameworkElement CreateView(LuaWidgetVm w)
    {
        if (w is ChartWidgetVm cw)
        {
            var chart = new TimeSeriesChart { Decimals = 1, WindowSeconds = _vm.Settings.StatsWindowMinutes * 60 };
            chart.SetBinding(TimeSeriesChart.TitleProperty, new System.Windows.Data.Binding(nameof(ChartWidgetVm.Title)) { Source = cw });
            chart.SetBinding(TimeSeriesChart.UnitProperty, new System.Windows.Data.Binding(nameof(ChartWidgetVm.Unit)) { Source = cw });
            void Build()
            {
                chart.ClearSeries();
                foreach (var s in cw.Series) chart.Add(s.Data, s.Name, brush: s.Brush);
            }
            Build();
            cw.Series.CollectionChanged += (_, _) => Build();
            cw.Updated += chart.Refresh;
            _extraCharts[cw] = chart;
            var card = new Border { Child = chart };
            card.SetResourceReference(StyleProperty, "ChartCard");
            StatsLayout.SetSpan(card, SpanOf(cw));
            return card;
        }
        var view = new ContentPresenter { Content = w };
        StatsLayout.SetSpan(view, SpanOf(w));
        return view;
    }

    /// <summary>Built-in parts an addon or a plugin hid.</summary>
    private void ApplyHidden()
    {
        foreach (var e in Kpis.Children.OfType<FrameworkElement>().Concat(Charts.Children.OfType<FrameworkElement>()).Append(SpikesCard).Append(ProfilerCard))
        {
            if (e.Tag is not string id || !(KpiIds.Contains(id) || ChartIds.Contains(id) || id is "spikes" or "profiler")) continue;
            e.Visibility = _vm.IsStatHidden(id) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnChartsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && sender is Panel p) StatsLayout.Arrange(p);
    }

    /// <summary>For the UI script runner: the ids of the built-in parts that are shown.</summary>
    public IEnumerable<string> ScriptVisibleParts() =>
        Kpis.Children.OfType<FrameworkElement>().Concat(Charts.Children.OfType<FrameworkElement>()).Append(SpikesCard).Append(ProfilerCard)
            .Where(e => e.Tag is string && e.Visibility == Visibility.Visible).Select(e => (string)e.Tag)
            .Concat(_extraViews.Select(x => $"extra {x.Key.Id} ({x.Key.Kind}) in {(x.Value.Parent as FrameworkElement)?.Name} {x.Value.ActualWidth:F0}x{x.Value.ActualHeight:F0}"));

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
        foreach (var c in _extraCharts.Values) c.WindowSeconds = minutes * 60;
    }
}
