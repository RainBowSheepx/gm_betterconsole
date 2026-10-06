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
        LiveTable.Attach(grid, vm);
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
        var owner = Window.GetWindow(b);
        if (lb.Fields.Count == 0)
        {
            if (!string.IsNullOrEmpty(lb.Confirm) && !PromptDialog.Confirm(owner, lb.Text, lb.Confirm, lb.Text, lb.Style == "danger")) return;
            widget.Press(lb);
            return;
        }
        // The same dialog as the items of the player menu; the question is its description.
        var values = new Dictionary<string, string>();
        foreach (var f in lb.Fields) values[f.Id] = f.Default;
        while (true)
        {
            var d = new PromptDialog(owner, lb.Text, lb.Confirm, lb.Text, lb.Style == "danger");
            foreach (var f in lb.Fields)
            {
                if (f.Choices is { Count: > 0 } choices) d.Choice(f.Id, f.Label, choices, values[f.Id], f.Editable);
                else d.Text(f.Id, f.Label, values[f.Id]);
            }
            if (d.ShowDialog() != true) return;
            foreach (var f in lb.Fields) values[f.Id] = d[f.Id];
            if (PlayerActionDef.TypedValues(lb.Fields, new Dictionary<string, string>(values), out var typed) is not { } problem)
            {
                widget.Press(lb, typed);
                return;
            }
            // Said what is wrong; the dialog again with what was typed.
            if (!PromptDialog.Confirm(owner, lb.Text, problem, "Change it")) return;
        }
    }

    private static ButtonsWidgetVm? FindWidget(DependencyObject d)
    {
        for (var cur = d; cur != null; cur = TreeWalk.Parent(cur))
            if (cur is FrameworkElement { DataContext: ButtonsWidgetVm w }) return w;
        return null;
    }
}
