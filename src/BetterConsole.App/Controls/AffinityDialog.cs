using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterConsole.App.Themes;
using BetterConsole.Core.Server;

namespace BetterConsole.App.Controls;

/// <summary>
/// Which logical processors the server may use (a check box per processor, grouped by core) and the
/// priority of its process. All processors is the default. With several servers, the processors other
/// servers are pinned to are named under each box.
/// </summary>
public sealed class AffinityDialog : Window
{
    private readonly List<(int Cpu, CheckBox Box)> _boxes = new();
    private readonly ComboBox _priority = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _summary = new() { FontSize = 12, Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _ok;

    public AffinityDialog(Window? owner, ulong mask, string priority, string? serverName, IReadOnlyList<(string Name, ulong Mask)> others)
    {
        Owner = owner;
        Title = "CPU affinity and priority";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Brush.Panel");
        SetResourceReference(ForegroundProperty, "Brush.Text");
        FontFamily = (FontFamily)Application.Current.Resources["Font.Ui"];
        FontSize = 13;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        root.Children.Add(new TextBlock { Text = serverName == null ? "CPU affinity and priority" : $"CPU affinity and priority · {serverName}", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(Muted("The processors the server may run on. srcds runs its game on one thread, so a server needs one fast core of its own; " +
                                "pinning servers to different cores keeps them from slowing each other down. Applies at once and on every start.", 0, 0, 14));

        var cores = ProcessTuning.Cores();
        bool hybrid = cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
        int topClass = cores.Max(c => c.EfficiencyClass);
        var grid = new UniformGrid { Columns = cores.Count > 8 ? 4 : Math.Max(1, Math.Min(4, cores.Count)) };
        foreach (var core in cores)
        {
            var card = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 6, 6) };
            card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
            var stack = new StackPanel();
            var head = $"Core {core.Index}";
            if (hybrid) head += core.EfficiencyClass == topClass ? " · P" : " · E";
            var h = new TextBlock { Text = head, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
            h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            if (hybrid) h.ToolTip = core.EfficiencyClass == topClass ? "Performance core" : "Efficiency core (slower)";
            stack.Children.Add(h);
            foreach (var cpu in core.Processors)
            {
                var box = new CheckBox { Content = $"CPU {cpu}", Margin = new Thickness(0, 2, 0, 0), IsChecked = ProcessTuning.IsAll(mask) || (mask & (1UL << cpu)) != 0 };
                box.Checked += (_, _) => Update();
                box.Unchecked += (_, _) => Update();
                stack.Children.Add(box);
                var used = others.Where(o => !ProcessTuning.IsAll(o.Mask) && (o.Mask & (1UL << cpu)) != 0).Select(o => o.Name).ToList();
                if (used.Count > 0)
                {
                    var u = Muted(string.Join(", ", used), 20, 0, 0);
                    u.FontSize = 10.5;
                    u.TextTrimming = TextTrimming.CharacterEllipsis;
                    u.TextWrapping = TextWrapping.NoWrap;
                    u.ToolTip = "Also used by " + string.Join(", ", used);
                    stack.Children.Add(u);
                }
                _boxes.Add((cpu, box));
            }
            card.Child = stack;
            grid.Children.Add(card);
        }
        root.Children.Add(grid);

        var tools = new DockPanel { Margin = new Thickness(0, 4, 0, 14) };
        var quick = new StackPanel { Orientation = Orientation.Horizontal };
        quick.Children.Add(QuickButton("All", _ => true));
        quick.Children.Add(QuickButton("None", _ => false));
        if (cores.Any(c => c.Processors.Length > 1))
            quick.Children.Add(QuickButton("One per core", cpu => cores.Any(c => c.Processors[0] == cpu)));
        DockPanel.SetDock(quick, Dock.Left);
        tools.Children.Add(quick);
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        tools.Children.Add(_summary);
        root.Children.Add(tools);

        var pl = new TextBlock { Text = "Priority", FontSize = 12, Margin = new Thickness(0, 0, 0, 5) };
        pl.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        root.Children.Add(pl);
        foreach (var p in ProcessTuning.Priorities) _priority.Items.Add(new ComboBoxItem { Content = ProcessTuning.PriorityText(p), Tag = p });
        _priority.SelectedItem = _priority.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == priority) ?? _priority.Items[2];
        root.Children.Add(_priority);
        root.Children.Add(Muted("Above normal or High lets the server win over other programs on the machine (not over Windows itself). Real-time is not offered: it can freeze the system.", 0, 4, 0));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        _ok = new Button { Content = "Apply", MinWidth = 90, IsDefault = true };
        _ok.SetResourceReference(StyleProperty, "Btn.Primary");
        _ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_ok);
        root.Children.Add(buttons);
        Content = root;
        Update();
    }

    /// <summary>The chosen processors (0 = all).</summary>
    public ulong Mask
    {
        get
        {
            ulong m = 0;
            foreach (var (cpu, box) in _boxes)
                if (box.IsChecked == true) m |= 1UL << cpu;
            return ProcessTuning.IsAll(m) ? 0 : m;
        }
    }

    public string Priority => (_priority.SelectedItem as ComboBoxItem)?.Tag as string ?? "Normal";

    private void Update()
    {
        int n = _boxes.Count(b => b.Box.IsChecked == true);
        _summary.Text = n == 0 ? "Choose at least one processor" : n == _boxes.Count ? $"All {n} processors" : $"{n} of {_boxes.Count} processors";
        if (_ok != null) _ok.IsEnabled = n > 0;
    }

    private Button QuickButton(string text, Func<int, bool> pick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3) };
        b.SetResourceReference(StyleProperty, "Btn.Ghost");
        b.Click += (_, _) =>
        {
            foreach (var (cpu, box) in _boxes) box.IsChecked = pick(cpu);
        };
        return b;
    }

    private static TextBlock Muted(string text, double left, double top, double bottom)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(left, top, 0, bottom) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        return t;
    }
}
