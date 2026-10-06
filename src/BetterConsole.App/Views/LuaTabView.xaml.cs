using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BetterConsole.App.Controls;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Views;

/// <summary>Shows a tab defined by a server addon (BetterConsole.AddTab) with its widgets.</summary>
public partial class LuaTabView : UserControl
{
    public LuaTabView(LuaTabVm tab)
    {
        InitializeComponent();
        DataContext = tab;
        tab.Widgets.CollectionChanged += (_, _) => Update(tab);
        Update(tab);
    }

    // Loaded runs again whenever the view moves to another window (multi-console): subscribe once.
    private readonly HashSet<object> _bound = new(ReferenceEqualityComparer.Instance);

    private void Update(LuaTabVm tab) => Empty.Visibility = tab.Widgets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnTableLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.DataContext is not TableWidgetVm vm || !_bound.Add(grid)) return;
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
        if (sender is not TimeSeriesChart chart || chart.DataContext is not ChartWidgetVm vm || !_bound.Add(chart)) return;
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
            b.SetResourceReference(StyleProperty, lb.Style switch { "danger" => "Btn.Danger", "primary" => "Btn.Primary", _ => "Btn" });
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LuaButton lb } b) return;
        var widget = FindWidget(b);
        if (widget == null) return;
        if (!string.IsNullOrEmpty(lb.Confirm) && !PromptDialog.Confirm(Window.GetWindow(this), lb.Text, lb.Confirm, lb.Text, lb.Style == "danger")) return;
        widget.Press(lb);
    }

    private static ButtonsWidgetVm? FindWidget(DependencyObject d)
    {
        for (var cur = d; cur != null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur is FrameworkElement { DataContext: ButtonsWidgetVm w }) return w;
        return null;
    }
}
