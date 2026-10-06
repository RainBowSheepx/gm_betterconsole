using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterConsole.App.Themes;
using BetterConsole.Core.Server;

namespace BetterConsole.App.Controls;

/// <summary>
/// Which logical processors the server may use (a check box per processor, grouped by core; performance and
/// efficiency cores, or the L3 caches of the chip, in groups of their own) and the priority of its process.
/// All processors is the default. In a virtual machine (a VPS) it says so and shows no groups. With several
/// servers, the processors other servers are pinned to are named under each box.
/// </summary>
public sealed class AffinityDialog : Window
{
    private readonly List<(int Cpu, CheckBox Box)> _boxes = new();
    private readonly ComboBox _priority = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _summary = new() { FontSize = 12, Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _ok;

    public AffinityDialog(Window? owner, ulong mask, string priority, string? serverName, IReadOnlyList<(string Name, ulong Mask)> others, CpuTopology? cpu = null)
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

        cpu ??= ProcessTuning.Topology();
        var cores = cpu.Cores;
        // What can be told about the cores: their kind on hybrid CPUs, their L3 on chips with several (the two
        // CCDs of a Ryzen 9). Not in a virtual machine: its processors are threads of the host, run on whichever
        // real cores the host picks, and the topology it shows is made up (often a core and a "cache" per vCPU).
        bool vm = cpu.Hypervisor != null;
        bool hybrid = !vm && cpu.IsHybrid;
        bool byCache = !vm && !hybrid && cpu.Caches.Count > 1 && cores.GroupBy(c => c.Cache).All(g => g.Count() > 1);

        var facts = new List<string>
        {
            hybrid ? string.Join(" + ", cores.GroupBy(cpu.Kind).OrderBy(g => KindOrder(g.Key)).Select(g => $"{g.Count()} {g.Key}")) + " cores" : $"{cores.Count} cores",
            $"{cpu.Threads} threads",
        };
        if (!vm && cpu.Caches.Count == 1) facts.Add($"L3 {cpu.Caches[0].SizeText}");
        if (byCache) facts.Add($"{cpu.Caches.Count} L3 caches");
        var name = new TextBlock { Text = cpu.Name.Length > 0 ? cpu.Name : "Processor", FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        root.Children.Add(name);
        root.Children.Add(Muted(string.Join(" · ", facts), 0, 1, vm ? 8 : 12));
        if (vm)
        {
            var note = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 0, 12) };
            note.SetResourceReference(Border.BackgroundProperty, "Brush.WarningSoft");
            var nt = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Text = $"This is a virtual machine ({cpu.Hypervisor}), a VPS for example. Its processors are virtual: the host decides which of its real " +
                       "cores run them, so performance and efficiency cores or caches cannot be seen from here, and a virtual processor is only as fast " +
                       "as the host lets it be. Pinning still keeps the servers inside this machine apart. If a server stutters, ask the provider for " +
                       "dedicated (not shared) processors.",
            };
            nt.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            note.Child = nt;
            root.Children.Add(note);
        }

        // The cores: in groups when they differ (performance / efficiency, or the cache they share).
        var sections = hybrid
            ? cores.GroupBy(c => cpu.Kind(c)!).OrderBy(g => KindOrder(g.Key)).Select(g => (Title: KindTitle(g.Key), Hint: KindHint(g.Key), Cores: g.ToList())).ToList()
            : byCache
                ? cores.GroupBy(c => c.Cache).OrderBy(g => g.Key).Select(g => (Title: $"L3 group {g.Key + 1} · {cpu.Caches[g.Key].SizeText}", Hint: CacheHint(cpu, g.Key), Cores: g.ToList())).ToList()
                : [(Title: (string?)null, Hint: (string?)null, Cores: cores.ToList())];
        var list = new StackPanel();
        foreach (var section in sections)
        {
            if (section.Title != null)
            {
                var t = new TextBlock { FontSize = 12, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 6, 0, 5), TextWrapping = TextWrapping.Wrap };
                t.Inlines.Add(new System.Windows.Documents.Run(section.Title) { FontWeight = FontWeights.SemiBold });
                if (section.Hint != null)
                {
                    var hint = new System.Windows.Documents.Run("   " + section.Hint) { FontSize = 11.5 };
                    hint.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Brush.TextMuted");
                    t.Inlines.Add(hint);
                }
                t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                list.Children.Add(t);
            }
            int n = section.Cores.Count;
            // Sections line up; single-thread cores (E-cores) fit more to a row.
            int columns = sections.Count > 1 ? (n > 8 && section.Cores.All(c => c.Processors.Length == 1) ? 6 : 4) : n > 8 ? 4 : Math.Max(1, Math.Min(4, n));
            var grid = new UniformGrid { Columns = columns };
            foreach (var core in section.Cores) grid.Children.Add(CoreCard(core, hybrid ? cpu.Kind(core) : null, mask, others));
            list.Children.Add(grid);
        }
        // Many cores (a 24-core Intel, a 32-thread Ryzen): the list scrolls instead of growing past the screen.
        root.Children.Add(new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - (vm ? 520 : 440)),
        });

        var tools = new DockPanel { Margin = new Thickness(0, 4, 0, 14) };
        var quick = new WrapPanel();
        quick.Children.Add(QuickButton("All", _ => true));
        quick.Children.Add(QuickButton("None", _ => false));
        if (cores.Any(c => c.Processors.Length > 1))
            quick.Children.Add(QuickButton("One per core", c => cores.Any(k => k.Processors[0] == c)));
        foreach (var section in sections.Where(_ => sections.Count > 1))
        {
            var set = section.Cores.SelectMany(c => c.Processors).ToHashSet();
            quick.Children.Add(QuickButton(hybrid ? $"{cpu.Kind(section.Cores[0])}-cores" : $"L3 group {section.Cores[0].Cache + 1}", set.Contains));
        }
        DockPanel.SetDock(_summary, Dock.Right);
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        tools.Children.Add(_summary);
        tools.Children.Add(quick);
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

    private Border CoreCard(CpuCore core, string? kind, ulong mask, IReadOnlyList<(string Name, ulong Mask)> others)
    {
        var card = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 6, 6) };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        if (kind != null)
        {
            var badge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), VerticalAlignment = VerticalAlignment.Center, ToolTip = KindTitle(kind) };
            var bt = new TextBlock { Text = kind, FontSize = 10, FontWeight = FontWeights.SemiBold };
            var (back, fore) = kind switch { "P" => ("Brush.AccentSoft", "Brush.Accent"), "LP-E" => ("Brush.WarningSoft", "Brush.Warning"), _ => ("Brush.Border", "Brush.TextSecondary") };
            badge.SetResourceReference(Border.BackgroundProperty, back);
            bt.SetResourceReference(TextBlock.ForegroundProperty, fore);
            badge.Child = bt;
            DockPanel.SetDock(badge, Dock.Right);
            head.Children.Add(badge);
        }
        var h = new TextBlock { Text = $"Core {core.Index}", FontSize = 11, FontWeight = FontWeights.SemiBold };
        h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        head.Children.Add(h);
        stack.Children.Add(head);
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
        return card;
    }

    private static int KindOrder(string? kind) => kind switch { "P" => 0, "E" => 1, "LP-E" => 2, _ => 3 };

    private static string KindTitle(string kind) => kind switch
    {
        "P" => "Performance cores (P)",
        "E" => "Efficiency cores (E)",
        "LP-E" => "Low-power efficiency cores (LP-E)",
        _ => kind,
    };

    private static string? KindHint(string kind) => kind switch
    {
        "P" => "the fast ones: give the game server these",
        "E" => "slower: for other programs, or a quiet second server",
        "LP-E" => "the slowest: keep game servers off them",
        _ => null,
    };

    private static string? CacheHint(CpuTopology cpu, int cache)
    {
        long biggest = cpu.Caches.Max(c => c.SizeBytes);
        if (cpu.Caches.Any(c => c.SizeBytes != biggest))
            return cpu.Caches[cache].SizeBytes == biggest
                ? (cpu.Name.Contains("X3D", StringComparison.OrdinalIgnoreCase) ? "3D V-Cache: " : "the larger cache: ") + "usually the faster one for a game server"
                : null;
        return cache == 0 ? "the cores of a group share their cache: keep each server within one group" : null;
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
