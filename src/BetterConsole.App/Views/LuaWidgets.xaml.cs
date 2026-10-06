using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BetterConsole.App.Controls;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Views;

/// <summary>The templates of addon widgets and what their controls do (tables get columns, charts series, buttons send actions).</summary>
public partial class LuaWidgets : ResourceDictionary
{
    public LuaWidgets() => InitializeComponent();

    // Loaded runs again whenever a view moves to another window (multi-console): subscribe once. Weak, so
    // the controls of removed widgets are not kept (this dictionary lives as long as its view).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _bound = new();

    private void OnTableLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.DataContext is not TableWidgetVm vm || !_bound.TryAdd(grid, grid)) return;
        void Build()
        {
            grid.Columns.Clear();
            for (int i = 0; i < vm.Columns.Length; i++)
                grid.Columns.Add(new DataGridTextColumn { Header = vm.Columns[i], Binding = new Binding($"[{i}]"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        }
        Build();
        vm.PropertyChanged += (_, pe) => { if (pe.PropertyName == nameof(TableWidgetVm.Columns)) Build(); };
    }

    private void OnChartLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TimeSeriesChart chart || chart.DataContext is not ChartWidgetVm vm || !_bound.TryAdd(chart, chart)) return;
        void Build()
        {
            chart.ClearSeries();
            foreach (var s in vm.Series) chart.Add(s.Data, s.Name, brush: s.Brush);
        }
        Build();
        vm.Series.CollectionChanged += (_, _) => Build();
        vm.Updated += chart.Refresh;
    }

    private void OnButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is LuaButton lb)
            b.SetResourceReference(FrameworkElement.StyleProperty, lb.Style switch { "danger" => "Btn.Danger", "primary" => "Btn.Primary", _ => "Btn" });
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LuaButton lb } b) return;
        var widget = FindWidget(b);
        if (widget == null) return;
        if (!string.IsNullOrEmpty(lb.Confirm) && !PromptDialog.Confirm(Window.GetWindow(b), lb.Text, lb.Confirm, lb.Text, lb.Style == "danger")) return;
        widget.Press(lb);
    }

    private static ButtonsWidgetVm? FindWidget(DependencyObject d)
    {
        for (var cur = d; cur != null; cur = TreeWalk.Parent(cur))
            if (cur is FrameworkElement { DataContext: ButtonsWidgetVm w }) return w;
        return null;
    }
}
