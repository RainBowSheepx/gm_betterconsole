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
/// expand client 0 1           player 0, their error 1
/// menu players 0              open the context menu of a player row
/// prof on|off
/// compact on|off
/// statusmenu [click name | shot path.png | close]
/// menudump path.txt           the items of the open player menu
/// runaction lua:id k=v        an addon item for the selected players
/// statsparts path.txt         built-in parts of the Statistics tab that are shown
/// shot docs/images/x.png      the window with a drawn title bar, plus open popups
/// quit
/// </code>
/// </summary>
public sealed class UiScriptRunner(MainWindow window, AppShell shell, string file)
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
                        "bridge" => Vm.BridgeConnected,
                        "stats" => Vm.Stats.Fps.Count > 3,
                        "catalog" => Vm.Catalog.IsLoaded,
                        _ => true,
                    };
                    if (ok) break;
                    await Task.Delay(100);
                }
                break;
            case "tab":
                if (Vm.FindTab(arg) is { } tab) Vm.SelectedTab = tab;
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
                Vm.SendCommand(arg);
                break;
            case "input":
                Console()?.ScriptInput(arg);
                await Task.Delay(400);
                break;
            case "key":
                Console()?.ScriptKey(arg);
                await Task.Delay(250);
                break;
            case "tabsmenu":
                window.ScriptTabsMenu();
                await Task.Delay(400);
                break;
            case "find":
                Console()?.ScriptFind(arg);
                await Task.Delay(400);
                break;
            case "expand":
            {
                var a = arg.Split(' ');
                int n = int.Parse(a[1]);
                if (a[0] == "server" && n < Vm.ServerErrors.Items.Count) Vm.ServerErrors.Items[n].IsExpanded = true;
                if (a[0] == "client" && n < Vm.ClientErrors.Players.Count)
                {
                    var p = Vm.ClientErrors.Players[n];
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
                if (a[0] == "players" && Vm.FindTab("players")?.Content is PlayersView pv) pv.ScriptOpenMenu(int.Parse(a[1]));
                await Task.Delay(400);
                break;
            }
            case "select":
                // select all | none | 0,2,3  (rows of the Players tab)
                if (Vm.FindTab("players")?.Content is PlayersView spv) spv.ScriptSelect(arg);
                await Task.Delay(300);
                break;
            case "menuclick":
                // menuclick Copy SteamID  (an item of the menu opened with "menu players n")
                if (Vm.FindTab("players")?.Content is PlayersView mpv) mpv.ScriptMenuClick(arg);
                await Task.Delay(500);
                break;
            case "shot-menu":
                // shot-menu path.png  (the menu opened with "menu players n")
                if (Vm.FindTab("players")?.Content is PlayersView smv && smv.ScriptMenu is { } openMenu && openMenu.ActualWidth > 0)
                {
                    using var fs = File.Create(arg);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(Snapshot(openMenu, openMenu.ActualWidth, openMenu.ActualHeight, 1.25)));
                    enc.Save(fs);
                }
                break;
            case "clipboard":
                // clipboard path.txt  (what is on the clipboard now)
                try { File.WriteAllText(arg, Clipboard.ContainsText() ? Clipboard.GetText() : ""); } catch (Exception ex) { Log.Write("ui-script clipboard: " + ex.Message); }
                break;
            case "settings":
                if (arg == "close") { _settings?.Close(); _settings = null; }
                else
                {
                    _settings = new SettingsWindow(shell, Vm, firstRun: false) { Owner = window, Width = 800, Height = 900 };
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
                else if (a[0] == "settings" && _settings != null && FindScroller(_settings) is { } ss) ss.ScrollToVerticalOffset(ss.VerticalOffset + amount);
                else if (Vm.FindTab(a[0])?.Content is FrameworkElement view && FindScroller(view) is { } sv)
                    sv.ScrollToVerticalOffset(sv.VerticalOffset + amount);
                await Task.Delay(300);
                break;
            }
            case "hover":
                // Simulates the pointer over the client error list (freezes the order).
                Vm.ClientErrors.IsFrozen = arg == "on";
                await Task.Delay(100);
                break;
            case "capture":
                Vm.SetCapture(arg == "on");
                await Task.Delay(500);
                break;
            case "prof":
                Vm.SetProfiling(arg == "on");
                break;
            case "shot":
                Shot(arg);
                break;
            // ---- testing without a real game client
            case "inject":
                // inject players {"maxplayers":16,"list":[...]}  (a bridge message as if the addon sent it)
            {
                var a = arg.Split(' ', 2);
                Vm.InjectBridgeMessage(a[0], a[1]);
                await Task.Delay(300);
                break;
            }
            case "resolve":
                Log.Write($"ui-script: resolve {arg} -> {Vm.Files.Resolve(arg) ?? "(not found)"}");
                break;
            case "openlink":
                // openlink addons/x/lua/y.lua 12  (what a double-click on that path does)
            {
                var a = arg.Split(' ');
                if (Vm.Files.Resolve(a[0]) is { } file) Controls.LuaLinks.Open?.Invoke(window, file, a.Length > 1 ? int.Parse(a[1]) : 1);
                else Log.Write($"ui-script: openlink {a[0]}: not found");
                await Task.Delay(500);
                break;
            }
            case "dumpconsole":
                // dumpconsole path.txt  (the console text of the shown server)
                if (Console() is { } cv) File.WriteAllText(arg, cv.ScriptText());
                break;
            case "log":
                Log.Write("ui-script: " + arg);
                break;
            case "probe-textboxes":
                // Where the text starts inside each visible text box (padding checks).
                foreach (var tb in Descendants(window).OfType<System.Windows.Controls.TextBox>().Where(t => t.IsVisible))
                {
                    var view = Descendants(tb).FirstOrDefault(d => d.GetType().Name == "TextBoxView") as UIElement;
                    var at = view?.TranslatePoint(new Point(0, 0), tb);
                    Log.Write($"ui-script: textbox {tb.Name} \"{(tb.Text.Length > 30 ? tb.Text[..30] : tb.Text)}\" width {tb.ActualWidth:F0} padding {tb.Padding} text at {at}");
                }
                break;
            // ---- multi-console
            case "server":
                // server 1  (show the n-th server in the main window)
                if (int.TryParse(arg, out var si) && si < shell.Servers.Count && shell.WindowOf(shell.Servers[si]) == window) window.ShowServer(shell.Servers[si]);
                await Task.Delay(300);
                break;
            case "sidebar":
                if (arg == "off") window.CloseSide();
                else window.OpenSide();
                await Task.Delay(450);
                break;
            case "detach":
                if (int.TryParse(arg, out var di) && di < shell.Servers.Count) shell.Detach(shell.Servers[di]);
                await Task.Delay(800);
                break;
            case "attach":
                if (int.TryParse(arg, out var ai) && ai < shell.Servers.Count) shell.Attach(shell.Servers[ai]);
                else shell.MergeAll();
                await Task.Delay(500);
                break;
            case "button":
                // button start|stop|restart  (the header buttons of the shown server)
                (arg switch { "stop" => Vm.StopCommand, "restart" => Vm.RestartCommand, _ => Vm.StartCommand }).Execute(null);
                await Task.Delay(500);
                break;
            case "closewindow":
                // closewindow 1  (as if the user closed the own window of the n-th server)
                if (int.TryParse(arg, out var ci) && ci < shell.Servers.Count && shell.WindowOf(shell.Servers[ci]) is { IsMain: false } cw) cw.Close();
                await Task.Delay(800);
                break;
            case "removeserver":
                // removeserver 1  (as if it was removed in the settings and they were saved)
                if (int.TryParse(arg, out var ri) && ri < shell.Servers.Count)
                {
                    shell.Settings.Servers.Remove(shell.Servers[ri].Profile);
                    shell.ApplySettings();
                }
                await Task.Delay(800);
                break;
            case "multi":
                // multi off|on  (the multi-console check box, saved)
                shell.Settings.MultiServer = arg != "off";
                shell.ApplySettings();
                await Task.Delay(800);
                break;
            case "start":
                // start 1  (the n-th server; no number: the shown one)
                await (int.TryParse(arg, out var sti) && sti < shell.Servers.Count ? shell.Servers[sti] : Vm).StartAsync("UI script");
                break;
            case "dialog":
                // dialog affinity | journal: shown without blocking the script; "dialog close" closes it
                _dialog?.Close();
                _dialog = arg switch
                {
                    "affinity" => new Controls.AffinityDialog(window, Vm.Profile.AffinityMask, Vm.Profile.Priority, shell.MultiServer ? Vm.DisplayName : null,
                        shell.Servers.Where(s => s != Vm).Select(s => (s.DisplayName, s.Profile.AffinityMask)).ToList()),
                    // dialog affinity-hybrid | affinity-ccd | affinity-vm: as on such a machine
                    _ when arg.StartsWith("affinity-") => new Controls.AffinityDialog(window, 0b1100, "AboveNormal", "Build server", [("Sandbox #1", 0b11UL)], FakeCpu(arg[9..])),
                    "journal" => new JournalWindow(shell, null) { Owner = window },
                    // dialog players kick | ban | jail | group  (for the selected players; nothing is run)
                    _ when arg.StartsWith("players ") && Vm.FindTab("players")?.Content is PlayersView dpv => dpv.ScriptDialog(arg[8..]),
                    _ => null,
                };
                _dialog?.Show();
                await Task.Delay(600);
                break;
            case "shot-dialog":
                // The whole client area, margins included.
                if (_dialog != null && VisualTreeHelper.GetChildrenCount(_dialog) > 0 && VisualTreeHelper.GetChild(_dialog, 0) is FrameworkElement dr) ShotElement(dr, _dialog.Background, arg, _dialog.Title);
                break;
            case "shot-window":
                // shot-window 1 path.png  (the own window of the n-th server)
            {
                var a = arg.Split(' ', 2);
                if (int.TryParse(a[0], out var wi) && wi < shell.Servers.Count && shell.WindowOf(shell.Servers[wi]) is { } w && w.Content is FrameworkElement wc)
                    ShotElement(wc, w.Background, a[1], w.Title);
                break;
            }
            case "compact":
                shell.SetCompact(arg == "on");
                await Task.Delay(500);
                break;
            case "statusmenu":
                // statusmenu  (open the menu of the status bar)  |  statusmenu click <name>  (toggle that item)  |  statusmenu shot path.png
                if (arg.StartsWith("click "))
                {
                    var name = arg[6..];
                    if (_statusMenu?.Items.OfType<System.Windows.Controls.MenuItem>().FirstOrDefault(m => m.Header is string h && h.StartsWith(name, StringComparison.OrdinalIgnoreCase)) is { } mi)
                    {
                        mi.IsChecked = !mi.IsChecked;
                        mi.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    }
                }
                else if (arg.StartsWith("shot ") && _statusMenu is { ActualWidth: > 0 } sm)
                {
                    using var fs = File.Create(arg[5..]);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(Snapshot(sm, sm.ActualWidth, sm.ActualHeight, 1.25)));
                    enc.Save(fs);
                }
                else if (arg == "close" && _statusMenu != null) _statusMenu.IsOpen = false;
                else _statusMenu = window.ScriptStatusMenu();
                await Task.Delay(400);
                break;
            case "menudump":
                // menudump path.txt  (the items of the menu opened with "menu players n")
                if (Vm.FindTab("players")?.Content is PlayersView dmv) File.WriteAllLines(arg, dmv.ScriptMenuItems());
                break;
            case "runaction":
                // runaction lua:slay reason=text amount=5  (an addon's player item for the selection, as if its dialog said OK)
            {
                var a = arg.Split(' ');
                var values = a.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Replace('_', ' '));
                if (Vm.FindTab("players")?.Content is PlayersView rpv) rpv.ScriptRunAction(a[0], values);
                await Task.Delay(500);
                break;
            }
            case "statsparts":
                // statsparts path.txt  (the built-in parts of the Statistics tab that are shown)
                if (Vm.FindTab("stats")?.Content is StatsView stv) File.WriteAllLines(arg, stv.ScriptVisibleParts());
                break;
            case "quit":
                window.Close();
                break;
        }
    }

    private System.Windows.Controls.ContextMenu? _statusMenu;

    private static System.Windows.Controls.ContextMenu? FindContextMenu(DependencyObject node)
    {
        if (node is System.Windows.Controls.ContextMenu cm) return cm;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindContextMenu(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var c = VisualTreeHelper.GetChild(node, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static System.Windows.Controls.ScrollViewer? FindScroller(DependencyObject node)
    {
        if (node is System.Windows.Controls.ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindScroller(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }

    private ServerViewModel Vm => window.Current!;

    private ConsoleView? Console() => Vm.FindTab("console")?.Content as ConsoleView;

    private SettingsWindow? _settings;
    private Window? _dialog;

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
            dc.DrawImage(Snapshot(content, w, h, scale), new Rect(0, titleH, w, h));
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling((h + titleH) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        using var fs = File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(fs);
    }

    /// <summary>Processors of other machines, to look at the affinity dialog on them.</summary>
    private static BetterConsole.Core.Server.CpuTopology FakeCpu(string kind)
    {
        const long MB = 1 << 20;
        switch (kind)
        {
            case "hybrid":
            {
                // 8 performance cores with Hyper-Threading, 8 efficiency cores, one L3.
                var cores = Enumerable.Range(0, 8).Select(i => (3UL << (2 * i), 1)).Concat(Enumerable.Range(0, 8).Select(i => (1UL << (16 + i), 0))).ToList();
                return BetterConsole.Core.Server.ProcessTuning.Build("Intel(R) Core(TM) i7-13700K", cores, [(3, 0, 30 * MB, 0xFFFFFFUL)], 0xFFFFFFUL);
            }
            case "ccd":
            {
                // Two CCDs, the first with the stacked cache.
                var cores = Enumerable.Range(0, 16).Select(i => (3UL << (2 * i), 0)).ToList();
                return BetterConsole.Core.Server.ProcessTuning.Build("AMD Ryzen 9 7950X3D 16-Core Processor", cores, [(3, 0, 96 * MB, 0xFFFFUL), (3, 0, 32 * MB, 0xFFFF0000UL)], 0xFFFFFFFFUL);
            }
            default:
            {
                // A VPS: a core and an L3 per vCPU, as KVM often shows them.
                var cores = Enumerable.Range(0, 4).Select(i => (1UL << i, 0)).ToList();
                var caches = Enumerable.Range(0, 4).Select(i => (3, 0, 16 * MB, 1UL << i)).ToList();
                return BetterConsole.Core.Server.ProcessTuning.Build("Common KVM processor", cores, caches, 0xFUL) with { Hypervisor = "KVM" };
            }
        }
    }

    /// <summary>
    /// The element as it is on screen, in its own coordinates (a visual brush would align the bounds of
    /// everything inside, which a shadow or a panel slid out to the left would shift).
    /// </summary>
    private static BitmapSource Snapshot(FrameworkElement element, double w, double h, double scale)
    {
        // The element is drawn at its offset in the parent (a dialog's margin): render with it, cut it off.
        var off = VisualTreeHelper.GetOffset(element);
        int x = (int)Math.Round(off.X * scale), y = (int)Math.Round(off.Y * scale);
        int pw = (int)Math.Ceiling(w * scale), ph = (int)Math.Ceiling(h * scale);
        var rtb = new RenderTargetBitmap(x + pw, y + ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(element);
        return x == 0 && y == 0 ? rtb : new CroppedBitmap(rtb, new Int32Rect(x, y, pw, ph));
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
            dc.DrawImage(Snapshot(content, w, h, scale), new Rect(0, titleH, w, h));

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
