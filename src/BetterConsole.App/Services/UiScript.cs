using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;
using BetterConsole.App.Views;

namespace BetterConsole.App.Services;

/// <summary>
/// Developer tool: <c>BetterConsole.exe --ui-script file.txt</c> drives the window from a script and
/// renders screenshots without touching the real mouse or keyboard (used for the README images and
/// for smoke tests). One command per line:
/// <code>
/// wait 2                      seconds
/// waitfor bridge|stats|catalog
/// tab players                 console, players, client-errors, server-errors, stats, lua:&lt;id&gt;
/// size 1360 860
/// theme Light
/// send status                 run a console command
/// input sv_ch                 type into the console input (opens auto-completion)
/// key Down                    Down, Up, Tab, Escape, Enter
/// expand server 0             expand the n-th server error (client: n-th player)
/// expand client 0 1           player 0, his error 1
/// menu players 0              open the context menu of a player row
/// prof on|off
/// shot docs/images/x.png      the window with a drawn title bar, plus open popups
/// quit
/// </code>
/// </summary>
public sealed class UiScriptRunner(MainWindow window, MainViewModel vm, string file)
{
    public async Task RunAsync()
    {
        var lines = await File.ReadAllLinesAsync(file);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', 2);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1] : "";
            try
            {
                await Step(cmd, arg);
            }
            catch (Exception ex)
            {
                Log.Write($"ui-script '{line}': {ex}");
            }
        }
    }

    private async Task Step(string cmd, string arg)
    {
        switch (cmd)
        {
            case "wait":
                await Task.Delay(TimeSpan.FromSeconds(double.Parse(arg, CultureInfo.InvariantCulture)));
                break;
            case "waitfor":
                for (int i = 0; i < 1200; i++)
                {
                    bool ok = arg switch
                    {
                        "bridge" => vm.BridgeConnected,
                        "stats" => vm.Stats.Fps.Count > 3,
                        "catalog" => vm.Catalog.IsLoaded,
                        _ => true,
                    };
                    if (ok) break;
                    await Task.Delay(100);
                }
                break;
            case "tab":
                if (vm.FindTab(arg) is { } tab) vm.SelectedTab = tab;
                await Task.Delay(300);
                break;
            case "size":
                var wh = arg.Split(' ');
                window.WindowState = WindowState.Normal;
                window.Width = double.Parse(wh[0], CultureInfo.InvariantCulture);
                window.Height = double.Parse(wh[1], CultureInfo.InvariantCulture);
                await Task.Delay(300);
                break;
            case "theme":
                if (ThemeManager.Discover(Path.Combine(AppSettings.DataDirectory, "themes")).FirstOrDefault(t => t.Name.Equals(arg, StringComparison.OrdinalIgnoreCase)) is { } theme)
                    ThemeManager.Apply(theme);
                await Task.Delay(300);
                break;
            case "send":
                vm.SendCommand(arg);
                break;
            case "input":
                Console()?.ScriptInput(arg);
                await Task.Delay(400);
                break;
            case "key":
                Console()?.ScriptKey(arg);
                await Task.Delay(250);
                break;
            case "expand":
            {
                var a = arg.Split(' ');
                int n = int.Parse(a[1]);
                if (a[0] == "server" && n < vm.ServerErrors.Items.Count) vm.ServerErrors.Items[n].IsExpanded = true;
                if (a[0] == "client" && n < vm.ClientErrors.Players.Count)
                {
                    var p = vm.ClientErrors.Players[n];
                    p.IsExpanded = true;
                    p.HasUnseen = false;
                    if (a.Length > 2 && int.Parse(a[2]) is var m && m < p.Entries.Count) p.Entries[m].IsExpanded = true;
                }
                await Task.Delay(300);
                break;
            }
            case "menu":
            {
                var a = arg.Split(' ');
                if (a[0] == "players" && vm.FindTab("players")?.Content is PlayersView pv) pv.ScriptOpenMenu(int.Parse(a[1]));
                await Task.Delay(400);
                break;
            }
            case "settings":
                if (arg == "close") { _settings?.Close(); _settings = null; }
                else
                {
                    _settings = new SettingsWindow(vm, window.Plugins, firstRun: false) { Owner = window, Width = 780, Height = 900 };
                    _settings.Show();
                }
                await Task.Delay(500);
                break;
            case "shot-settings":
                if (_settings?.Content is FrameworkElement sc) ShotElement(sc, _settings.Background, arg, _settings.Title);
                break;
            case "closemenus":
                foreach (PresentationSource src in PresentationSource.CurrentSources)
                {
                    if (src.RootVisual is not FrameworkElement root || root.GetType().Name != "PopupRoot") continue;
                    if (FindContextMenu(root) is { } cm) cm.IsOpen = false;
                }
                await Task.Delay(250);
                break;
            case "scroll":
            {
                // scroll console -30  (lines up)   |  scroll client-errors 300  (pixels down)
                var a = arg.Split(' ');
                double amount = double.Parse(a[1], CultureInfo.InvariantCulture);
                if (a[0] == "console") Console()?.ScriptScroll(amount);
                else if (vm.FindTab(a[0])?.Content is FrameworkElement view && FindScroller(view) is { } sv)
                    sv.ScrollToVerticalOffset(sv.VerticalOffset + amount);
                await Task.Delay(300);
                break;
            }
            case "hover":
                // Simulates the pointer over the client error list (freezes the order).
                vm.ClientErrors.IsFrozen = arg == "on";
                await Task.Delay(100);
                break;
            case "prof":
                vm.SetProfiling(arg == "on");
                break;
            case "shot":
                Shot(arg);
                break;
            case "quit":
                window.Close();
                break;
        }
    }

    private static System.Windows.Controls.ContextMenu? FindContextMenu(DependencyObject node)
    {
        if (node is System.Windows.Controls.ContextMenu cm) return cm;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindContextMenu(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private static System.Windows.Controls.ScrollViewer? FindScroller(DependencyObject node)
    {
        if (node is System.Windows.Controls.ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindScroller(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private ConsoleView? Console() => vm.FindTab("console")?.Content as ConsoleView;

    private SettingsWindow? _settings;

    /// <summary>Renders one element (a dialog's content) with a drawn title bar.</summary>
    private static void ShotElement(FrameworkElement content, Brush background, string path, string title)
    {
        content.UpdateLayout();
        const double titleH = 32;
        double w = content.ActualWidth, h = content.ActualHeight, scale = 1.25;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(ThemeManager.GetBrush("Panel"), null, new Rect(0, 0, w, titleH));
            var font = (FontFamily)Application.Current.Resources["Font.Ui"];
            var ft = new FormattedText(title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, ThemeManager.GetBrush("Text"), 1.25);
            dc.DrawText(ft, new Point(14, (titleH - ft.Height) / 2));
            dc.DrawRectangle(background, null, new Rect(0, titleH, w, h));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, titleH, w, h));
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling((h + titleH) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(fs);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    /// <summary>Renders the window content under a drawn title bar, with any open popup windows on top.</summary>
    private void Shot(string path)
    {
        if (window.Content is not FrameworkElement content) return;
        content.UpdateLayout();
        const double titleH = 32;
        double w = content.ActualWidth, h = content.ActualHeight;
        double scale = 1.25;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // Title bar as Windows 11 draws it with the theme's caption colour.
            dc.DrawRectangle(ThemeManager.GetBrush("Panel"), null, new Rect(0, 0, w, titleH));
            var icon = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png"));
            dc.DrawImage(icon, new Rect(12, 8, 16, 16));
            var font = (FontFamily)Application.Current.Resources["Font.Ui"];
            var dip = VisualTreeHelper.GetDpi(window).PixelsPerDip;
            var title = new FormattedText(window.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, ThemeManager.GetBrush("Text"), dip);
            dc.DrawText(title, new Point(36, (titleH - title.Height) / 2));
            var icons = (FontFamily)Application.Current.Resources["Font.Icons"];
            string[] glyphs = ["", "", ""];
            for (int i = 0; i < 3; i++)
            {
                var g = new FormattedText(glyphs[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(icons, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, ThemeManager.GetBrush("Text"), dip);
                dc.DrawText(g, new Point(w - 46 * (3 - i) + (46 - g.Width) / 2, (titleH - g.Height) / 2));
            }
            // The window background lives on the Window, not on its content.
            dc.DrawRectangle(window.Background, null, new Rect(0, titleH, w, h));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, titleH, w, h));

            // Popups (auto-completion, context menus) are separate windows.
            var origin = content.PointToScreen(new Point(0, 0));
            foreach (PresentationSource src in PresentationSource.CurrentSources)
            {
                if (src is not HwndSource hs || hs.RootVisual is not FrameworkElement root || ReferenceEquals(root, window)) continue;
                if (root.GetType().Name != "PopupRoot" || !root.IsVisible) continue;
                if (!GetWindowRect(hs.Handle, out var r)) continue;
                var m = hs.CompositionTarget.TransformFromDevice;
                var tl = m.Transform(new Point(r.L - origin.X, r.T - origin.Y));
                var size = new Size(root.ActualWidth, root.ActualHeight);
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(new Point(tl.X, tl.Y + titleH), size));
            }
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling((h + titleH) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(fs);
        Log.Write($"ui-script: saved {path}");
    }
}
