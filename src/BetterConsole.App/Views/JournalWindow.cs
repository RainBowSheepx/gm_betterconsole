using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BetterConsole.App.Services;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Views;

/// <summary>
/// The start / stop journal: when each server started, stopped, crashed or quit by itself, and why
/// (a button, a scheduled restart, "quit" in the console, a player through ulx rcon, a crash with its
/// exit code ...). Newest first.
/// </summary>
public sealed class JournalWindow : Window
{
    private readonly AppShell _shell;
    private readonly ListCollectionView _view;
    private readonly ComboBox _server = new() { Width = 240, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };

    public JournalWindow(AppShell shell, ServerViewModel? server)
    {
        _shell = shell;
        Title = "Start / stop journal — BetterConsole";
        Width = 1040;
        Height = 560;
        MinWidth = 640;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Brush.Background");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        FontFamily = (FontFamily)Application.Current.Resources["Font.Ui"];
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        _view = new ListCollectionView(shell.Journal.Entries);
        _view.SortDescriptions.Add(new SortDescription(nameof(JournalEntry.Time), ListSortDirection.Descending));
        _view.IsLiveSorting = false;

        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Ghost("", "Copy", OnCopy));
        right.Children.Add(Ghost("", "Open the file", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{shell.Journal.FilePath}\"") { UseShellExecute = true }); } catch { }
        }));
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (shell.Servers.Count > 1 || shell.Journal.Entries.Select(e => e.ServerId).Distinct().Count() > 1)
        {
            _server.Items.Add(new ComboBoxItem { Content = "All servers", Tag = "" });
            foreach (var g in shell.Journal.Entries.GroupBy(e => e.ServerId))
            {
                var name = shell.Servers.FirstOrDefault(s => s.Profile.Id == g.Key)?.DisplayName ?? g.Last().Server;
                _server.Items.Add(new ComboBoxItem { Content = name, Tag = g.Key });
            }
            foreach (var s in shell.Servers)
                if (!_server.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == s.Profile.Id))
                    _server.Items.Add(new ComboBoxItem { Content = s.DisplayName, Tag = s.Profile.Id });
            _server.SelectedItem = _server.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (server?.Profile.Id ?? "")) ?? _server.Items[0];
            _server.SelectionChanged += (_, _) => ApplyFilter();
            left.Children.Add(_server);
        }
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        left.Children.Add(_count);
        bar.Children.Add(left);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(6, 4, 6, 4) };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var grid = new DataGrid
        {
            ItemsSource = _view,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended,
            IsSynchronizedWithCurrentItem = false,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding(nameof(JournalEntry.TimeText)), Width = new DataGridLength(150), ElementStyle = Mono() });
        if (_server.Items.Count > 0)
            grid.Columns.Add(new DataGridTextColumn { Header = "Server", Binding = new Binding(nameof(JournalEntry.Server)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 120 });
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Event", CellTemplate = EventTemplate(), Width = new DataGridLength(130) });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Reason",
            Binding = new Binding(nameof(JournalEntry.Reason)),
            Width = new DataGridLength(2.6, DataGridLengthUnitType.Star),
            ElementStyle = Trimmed(),
        });
        grid.Columns.Add(new DataGridTextColumn { Header = "Details", Binding = new Binding(nameof(JournalEntry.DetailText)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 120, ElementStyle = Mono() });
        grid.Loaded += (_, _) => grid.UnselectAll();
        card.Child = grid;
        root.Children.Add(card);
        Content = root;

        shell.Journal.Entries.CollectionChanged += OnEntriesChanged;
        Closed += (_, _) =>
        {
            shell.Journal.Entries.CollectionChanged -= OnEntriesChanged;
            // The view listens to the journal, which lives as long as the app.
            _view.DetachFromSourceCollection();
        };
        ApplyFilter();
    }

    private void OnEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateCount();

    private void ApplyFilter()
    {
        var id = (_server.SelectedItem as ComboBoxItem)?.Tag as string;
        _view.Filter = string.IsNullOrEmpty(id) ? null : o => o is JournalEntry e && e.ServerId == id;
        UpdateCount();
    }

    private void UpdateCount()
    {
        int n = _view.Count;
        _count.Text = n == 0 ? "Nothing yet: entries appear when a server starts or stops." : $"{n} entr{(n == 1 ? "y" : "ies")}";
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var x in _view.OfType<JournalEntry>())
            sb.AppendLine($"{x.Time:yyyy-MM-dd HH:mm:ss}\t{x.Server}\t{x.EventText}\t{x.Reason}\t{x.DetailText}");
        try { Clipboard.SetText(sb.ToString()); } catch { }
    }

    private static Button Ghost(string icon, string text, RoutedEventHandler click)
    {
        var b = new Button { Margin = new Thickness(6, 0, 0, 0) };
        b.SetResourceReference(StyleProperty, "Btn.Ghost");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var i = new TextBlock { Text = icon, FontFamily = (FontFamily)Application.Current.Resources["Font.Icons"], FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(i);
        sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0) });
        b.Content = sp;
        b.Click += click;
        return b;
    }

    private static Style Mono()
    {
        var s = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
        s.Setters.Add(new Setter(TextBlock.FontFamilyProperty, Application.Current.Resources["Font.Mono"]));
        s.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
        s.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Brush.TextSecondary")));
        return s;
    }

    private static Style Trimmed()
    {
        var s = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
        s.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        s.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(JournalEntry.Reason))));
        return s;
    }

    private static DataTemplate EventTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new Binding(nameof(JournalEntry.Icon)));
        icon.SetValue(TextBlock.FontFamilyProperty, Application.Current.Resources["Font.Icons"]);
        icon.SetValue(TextBlock.FontSizeProperty, 12.0);
        icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 7, 0));
        icon.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(JournalEntry.ColorKey)) { Converter = (IValueConverter)Application.Current.Resources["ResourceBrush"] });
        panel.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(JournalEntry.EventText)));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(JournalEntry.ColorKey)) { Converter = (IValueConverter)Application.Current.Resources["ResourceBrush"] });
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }
}
