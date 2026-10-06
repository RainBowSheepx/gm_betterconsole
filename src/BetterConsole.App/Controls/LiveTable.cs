using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Controls;

/// <summary>
/// A data grid for an addon's table (<see cref="TableWidgetVm"/>): its columns with their widths and alignment, a
/// click on a header sorts the rows in the view model (numbers as numbers), the order holds still while the pointer
/// is over the grid, paths of Lua files open with a double click when the table asks for it.
/// </summary>
public static class LiveTable
{
    public static void Attach(DataGrid grid, TableWidgetVm vm)
    {
        void Build()
        {
            grid.Columns.Clear();
            for (int i = 0; i < vm.Columns.Length; i++)
            {
                var c = vm.Columns[i];
                var col = new DataGridTextColumn { Header = c.Text, Binding = new Binding($"[{i}]"), Width = Width(c.Width) };
                var align = c.Align switch { "right" => TextAlignment.Right, "center" => TextAlignment.Center, _ => TextAlignment.Left };
                var style = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
                style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, align));
                style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                col.ElementStyle = style;
                grid.Columns.Add(col);
            }
            ShowSort();
        }
        void ShowSort()
        {
            for (int i = 0; i < grid.Columns.Count; i++)
                grid.Columns[i].SortDirection = i == vm.SortColumn ? vm.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
        }
        Build();
        LuaLinks.SetIsEnabled(grid, vm.Links);
        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(TableWidgetVm.Columns): Build(); break;
                case nameof(TableWidgetVm.SortColumn) or nameof(TableWidgetVm.SortDescending): ShowSort(); break;
                case nameof(TableWidgetVm.Links): LuaLinks.SetIsEnabled(grid, vm.Links); break;
            }
        };
        // The view model sorts (numbers as numbers, the rows moved, not rebuilt); the grid only shows the arrow.
        grid.Sorting += (_, e) =>
        {
            e.Handled = true;
            int i = grid.Columns.IndexOf(e.Column);
            if (i >= 0) vm.SortBy(i);
        };
        grid.MouseEnter += (_, _) => vm.Hold = true;
        grid.MouseLeave += (_, _) => vm.Hold = false;
    }

    /// <summary>"*", "2*", "auto" or pixels.</summary>
    public static DataGridLength Width(string spec)
    {
        spec = spec.Trim();
        if (spec.Equals("auto", StringComparison.OrdinalIgnoreCase)) return DataGridLength.Auto;
        if (spec.EndsWith('*'))
            return new DataGridLength(double.TryParse(spec[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var stars) && stars > 0 ? stars : 1, DataGridLengthUnitType.Star);
        return double.TryParse(spec, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && px > 0
            ? new DataGridLength(px)
            : new DataGridLength(1, DataGridLengthUnitType.Star);
    }
}
