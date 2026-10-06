using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterConsole.App.Controls;
using BetterConsole.App.Services;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;
using BetterConsole.Core.Server;
using BetterConsole.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace BetterConsole.App.Views;

/// <summary>A server in the settings: an edited copy of its profile until Save.</summary>
public sealed partial class ServerItem : ObservableObject
{
    public ServerItem(ServerProfile copy, ServerProfile? original, string? runningName)
    {
        Profile = copy;
        Original = original;
        _runningName = runningName;
        Refresh();
    }

    private readonly string? _runningName;
    public ServerProfile Profile { get; }
    public ServerProfile? Original { get; }
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string folder = "";

    public void Refresh()
    {
        Title = !string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Name.Trim()
            : _runningName ?? (string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? "New server" : Path.GetFileName(Profile.ServerDirectory.TrimEnd('\\', '/')));
        Folder = string.IsNullOrWhiteSpace(Profile.ServerDirectory) ? "no folder chosen yet" : Profile.ServerDirectory;
    }
}

public partial class SettingsWindow : Window
{
    private readonly AppShell _shell;
    private readonly ThemePalette _themeAtOpen;
    private readonly List<ThemePalette> _themes;
    private readonly ObservableCollection<ServerItem> _servers = new();
    private ServerItem? _current;
    private bool _loading;
    private bool _saved;

    public SettingsWindow(AppShell shell, ServerViewModel? current, bool firstRun, bool addServer = false)
    {
        _shell = shell;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _themeAtOpen = ThemeManager.Discover(Path.Combine(AppSettings.DataDirectory, "themes")).FirstOrDefault(t => t.Name == shell.Settings.Theme) ?? ThemeManager.Current;
        Welcome.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;

        var s = shell.Settings;
        foreach (var p in s.Servers)
            _servers.Add(new ServerItem(p.Clone(), p, shell.Servers.FirstOrDefault(v => v.Profile == p)?.Hostname));
        ServerList.ItemsSource = _servers;
        Multi.IsChecked = s.MultiServer;

        foreach (var f in MonospaceFonts()) Font.Items.Add(f);
        Font.Text = s.ConsoleFont;
        foreach (var size in new[] { 11, 12, 13, 14, 15, 16, 18, 20 }) FontSize2.Items.Add(size.ToString());
        FontSize2.Text = s.ConsoleFontSize.ToString("0.#");
        MaxLines.Text = s.ConsoleMaxLines.ToString();
        HideErrors.IsChecked = s.HideErrorsInConsole;
        ServerOnly.IsChecked = s.CompleteServerCommandsOnly;
        ConfirmExit.IsChecked = s.ConfirmExitWhileRunning;
        MergeSimilar.IsChecked = s.MergeSimilarErrors;
        MaxErrors.Text = s.MaxErrorsPerList.ToString();
        ShowAvatars.IsChecked = s.ShowAvatars;

        EditorLauncher.Refresh();
        Editor.Items.Add(new ComboBoxItem { Content = $"Automatic ({EditorLauncher.Auto.Name})", Tag = "auto" });
        foreach (var ed in EditorLauncher.Known)
            Editor.Items.Add(new ComboBoxItem { Content = ed.Installed ? ed.Name : ed.Name + " (not found)", Tag = ed.Id, IsEnabled = ed.Installed });
        Editor.Items.Add(new ComboBoxItem { Content = "The program Windows opens .lua files with", Tag = "system" });
        Editor.Items.Add(new ComboBoxItem { Content = "Custom command…", Tag = "custom" });
        Editor.SelectedItem = Editor.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Editor) ?? Editor.Items[0];
        EditorCommand.Text = s.EditorCommand;
        UpdateEditorPanel();

        _themes = ThemeManager.Discover(Path.Combine(AppSettings.DataDirectory, "themes"));
        foreach (var t in _themes) Theme.Items.Add(t.Name);
        // Compact first: selecting the theme would preview it (and in compact mode replace the palette).
        Compact.IsChecked = s.CompactMode;
        Theme.IsEnabled = !s.CompactMode;
        Theme.SelectedItem = _themes.Any(t => t.Name == s.Theme) ? s.Theme : ThemeManager.Current.Name;
        TabTitles.SelectedItem = TabTitles.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.TabTitles) ?? TabTitles.Items[2];

        PluginList.ItemsSource = shell.Plugins.Plugins;
        PluginsInfo.Text = shell.Plugins.Plugins.Count == 0
            ? $"No plugins installed. Put plugin folders into {Path.Combine(AppContext.BaseDirectory, "plugins")}."
            : $"{shell.Plugins.Plugins.Count} plugin(s) in {Path.Combine(AppContext.BaseDirectory, "plugins")}.";
        DataDir.Text = "Settings: " + (s.FilePath ?? AppSettings.DataDirectory);
        // Cancelled: back to the look of the settings (the theme and compact mode were only previewed).
        Closed += (_, _) => { if (!_saved) Look.Apply(s.CompactMode, _themeAtOpen); };

        var select = _servers.FirstOrDefault(i => i.Original != null && i.Original == current?.Profile) ?? _servers[0];
        ServerList.SelectedItem = select;
        if (!s.MultiServer) ShowServer(_servers[0]);
        UpdateMultiPanel();
        if (addServer)
        {
            Multi.IsChecked = true;
            Loaded += (_, _) => AddServer();
        }
    }

    private static IEnumerable<string> MonospaceFonts()
    {
        var wanted = new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", "Fira Code", "Source Code Pro", "Hack", "Lucida Console", "Courier New" };
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.Where(installed.Contains);
    }

    // ------------------------------------------------------------------------------ servers

    private void OnMultiChanged(object sender, RoutedEventArgs e) => UpdateMultiPanel();

    private void UpdateMultiPanel()
    {
        bool multi = Multi.IsChecked == true;
        ServersPanel.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        NamePanel.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        RemoveServer.IsEnabled = _servers.Count > 1;
        if (!multi && _current != _servers[0]) ServerList.SelectedItem = _servers[0];
        ServerHeading.Text = multi && _current != null ? "Server · " + _current.Title : "Server";
    }

    private void OnServerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ServerList.SelectedItem is ServerItem item) ShowServer(item);
    }

    private void ShowServer(ServerItem item)
    {
        if (_current == item) return;
        if (_current != null) StoreServer(_current);
        _current = item;
        _loading = true;
        var p = item.Profile;
        ServerName.Text = p.Name;
        ServerDir.Text = p.ServerDirectory;
        Args.Text = p.Arguments;
        AutoRestart.IsChecked = p.AutoRestart;
        AlwaysRun.IsChecked = p.AlwaysRun;
        RestartDelay.Text = p.RestartDelaySeconds.ToString();
        InstallCompanion.IsChecked = p.InstallCompanion;
        StartWithApp.IsChecked = p.StartWithApp;
        StopTimeout.Text = p.StopTimeoutSeconds.ToString();
        FillExecutables(p.Executable);
        RestartTimes.Text = string.Join(", ", p.RestartTimes);
        WarnMinutes.Text = string.Join(", ", p.RestartWarningMinutes);
        WarnText.Text = p.RestartWarningText;
        _loading = false;
        OnAlwaysRunChanged(this, new RoutedEventArgs());
        UpdateAffinityText();
        UpdateScheduleInfo();
        UpdateMultiPanel();
    }

    /// <summary>The fields into the edited copy. Returns the problem, or null.</summary>
    private string? StoreServer(ServerItem item)
    {
        var p = item.Profile;
        p.Name = ServerName.Text.Trim();
        p.ServerDirectory = ServerDir.Text.Trim();
        p.Executable = Exe.SelectedItem as string ?? "auto";
        p.Arguments = Args.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        p.AlwaysRun = AlwaysRun.IsChecked == true;
        p.AutoRestart = AutoRestart.IsChecked == true || p.AlwaysRun;
        p.RestartDelaySeconds = ParseInt(RestartDelay.Text, 5, 0, 600);
        p.InstallCompanion = InstallCompanion.IsChecked == true;
        p.StartWithApp = StartWithApp.IsChecked == true;
        p.StopTimeoutSeconds = ParseInt(StopTimeout.Text, 20, 3, 300);
        p.RestartWarningText = WarnText.Text.Trim();
        p.RestartWarningMinutes = WarnMinutes.Text.Split(',', ';', ' ')
            .Select(x => int.TryParse(x.Trim(), out var m) ? m : 0).Where(m => m is > 0 and <= 120).Distinct().OrderByDescending(m => m).ToList();
        item.Refresh();
        var (times, bad) = ParseTimes(RestartTimes.Text);
        if (bad.Count > 0) return $"{item.Title}: \"{string.Join("\", \"", bad)}\" is not a time of day (use 05:00).";
        p.RestartTimes = times;
        return null;
    }

    private static (List<string> Times, List<string> Bad) ParseTimes(string text)
    {
        var times = new List<string>();
        var bad = new List<string>();
        foreach (var part in text.Split(',', ';', ' ').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (ServerProfile.TryParseTime(part, out var t)) times.Add($"{t.Hours:00}:{t.Minutes:00}");
            else bad.Add(part);
        }
        return (times.Distinct().OrderBy(x => x).ToList(), bad);
    }

    private void OnServerNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current == null) return;
        _current.Profile.Name = ServerName.Text.Trim();
        _current.Refresh();
        UpdateMultiPanel();
    }

    private void OnAddServer(object sender, RoutedEventArgs e) => AddServer();

    private void AddServer()
    {
        if (_current != null && StoreServer(_current) is { } problem)
        {
            Notify(problem, NotifyKind.Warning);
            return;
        }
        var p = new ServerProfile();
        // A different port than the servers there are already (they would fight over 27015).
        var ports = _servers.Select(i => Regex.Match(i.Profile.Arguments ?? "", @"-port\s+(\d+)")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        int port = 27015 + 10 * _servers.Count;
        while (ports.Contains(port)) port += 10;
        p.Arguments += $" -port {port}";
        if (_current != null)
        {
            p.ServerDirectory = _current.Profile.ServerDirectory;
            p.Executable = _current.Profile.Executable;
        }
        var item = new ServerItem(p, null, null);
        _servers.Add(item);
        ServerList.SelectedItem = item;
        UpdateMultiPanel();
        ServerName.Focus();
    }

    private void OnRemoveServer(object sender, RoutedEventArgs e)
    {
        if (_current == null || _servers.Count <= 1) return;
        var vm = _shell.Servers.FirstOrDefault(v => v.Profile == _current.Original);
        if (vm is { IsRunning: true })
        {
            Notify($"{_current.Title} is running. Stop it first.", NotifyKind.Warning);
            return;
        }
        if (!PromptDialog.Confirm(this, "Remove server", $"Remove {_current.Title} from BetterConsole? The server's files are not touched.", "Remove", danger: true)) return;
        var gone = _current;
        _current = null;
        int idx = _servers.IndexOf(gone);
        _servers.Remove(gone);
        ServerList.SelectedItem = _servers[Math.Min(idx, _servers.Count - 1)];
        UpdateMultiPanel();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int by)
    {
        if (_current == null) return;
        int i = _servers.IndexOf(_current), j = i + by;
        if (j < 0 || j >= _servers.Count) return;
        _servers.Move(i, j);
        ServerList.SelectedItem = _current;
    }

    // ------------------------------------------------------------------------------ the server's fields

    private void FillExecutables(string selected)
    {
        Exe.Items.Clear();
        Exe.Items.Add("auto");
        var dir = ServerDir.Text.Trim();
        if (Directory.Exists(dir))
        {
            foreach (var name in new[] { "srcds_console_win64.exe", "srcds_console.exe" })
                if (File.Exists(Path.Combine(dir, name))) Exe.Items.Add(name);
        }
        Exe.SelectedItem = Exe.Items.Contains(selected) ? selected : "auto";
    }

    private void OnServerDirChanged(object sender, TextChangedEventArgs e)
    {
        var dir = ServerDir.Text.Trim();
        string? current = Exe.SelectedItem as string;
        FillExecutables(current ?? "auto");
        if (!_loading && _current != null)
        {
            _current.Profile.ServerDirectory = dir;
            _current.Refresh();
        }
        if (dir.Length == 0)
        {
            ServerDirInfo.Text = "";
            return;
        }
        var probe = new ServerProfile { ServerDirectory = dir };
        var exe = probe.ResolveExecutable();
        if (!Directory.Exists(dir)) ServerDirInfo.Text = "The folder does not exist.";
        else if (exe == null) ServerDirInfo.Text = "No srcds_console.exe / srcds_console_win64.exe here. Is this the folder of a Garry's Mod dedicated server (app 4020)?";
        else if (!Directory.Exists(Path.Combine(dir, "garrysmod"))) ServerDirInfo.Text = "Found srcds, but no garrysmod folder next to it.";
        else
        {
            bool is64 = ServerProfile.Is64Bit(exe);
            ServerDirInfo.Text = $"OK: will start {Path.GetFileName(exe)} ({(is64 ? "64-bit" : "32-bit")}).";
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder of the dedicated server (with srcds.exe)" };
        if (Directory.Exists(ServerDir.Text)) dlg.InitialDirectory = ServerDir.Text;
        if (dlg.ShowDialog(this) == true) ServerDir.Text = dlg.FolderName;
    }

    private void OnAddArg(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string arg }) return;
        var key = arg.Split(' ')[0];
        if (Args.Text.Contains(key + " ", StringComparison.OrdinalIgnoreCase)) return;
        Args.Text = (Args.Text.TrimEnd() + " " + arg).Trim();
    }

    /// <summary>Reads the srcds command line out of a start.bat / .cmd.</summary>
    private void OnImportBat(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Batch files (*.bat;*.cmd)|*.bat;*.cmd|All files|*.*", Title = "The .bat that starts your server" };
        if (Directory.Exists(ServerDir.Text)) dlg.InitialDirectory = ServerDir.Text;
        if (dlg.ShowDialog(this) != true) return;
        string text;
        try { text = File.ReadAllText(dlg.FileName); }
        catch (Exception ex)
        {
            Notify("Could not read the file: " + ex.Message, NotifyKind.Error);
            return;
        }
        var m = Regex.Match(text, @"(?im)(srcds(?:_console)?(?:_win64)?\.exe)""?\s+([^\r\n]*)");
        if (!m.Success)
        {
            Notify("No srcds command found in that file.", NotifyKind.Warning);
            return;
        }
        var args = m.Groups[2].Value.Trim().TrimEnd('"').Trim();
        // "start" options and environment expansions are not srcds arguments.
        args = Regex.Replace(args, @"\s*%[^%\s]+%\s*", " ").Trim();
        Args.Text = args;
        if (string.IsNullOrWhiteSpace(ServerDir.Text)) ServerDir.Text = Path.GetDirectoryName(dlg.FileName) ?? "";
        var exe = m.Groups[1].Value;
        FillExecutables(exe.Contains("win64", StringComparison.OrdinalIgnoreCase) ? "srcds_console_win64.exe" : "srcds_console.exe");
        Notify($"Imported the options of {exe}.", NotifyKind.Success);
    }

    /// <summary>Always run includes restarting after crashes and starting with BetterConsole.</summary>
    private void OnAlwaysRunChanged(object sender, RoutedEventArgs e)
    {
        bool always = AlwaysRun.IsChecked == true;
        if (always)
        {
            AutoRestart.IsChecked = true;
            StartWithApp.IsChecked = true;
        }
        AutoRestart.IsEnabled = StartWithApp.IsEnabled = !always;
    }

    private void OnAffinity(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var others = _servers.Where(i => i != _current).Select(i => (i.Title, i.Profile.AffinityMask)).ToList();
        var d = new AffinityDialog(this, _current.Profile.AffinityMask, _current.Profile.Priority, Multi.IsChecked == true ? _current.Title : null, others);
        if (d.ShowDialog() != true) return;
        _current.Profile.AffinityMask = d.Mask;
        _current.Profile.Priority = d.Priority;
        UpdateAffinityText();
    }

    private void UpdateAffinityText()
    {
        if (_current == null) return;
        var p = _current.Profile;
        AffinityText.Text = $"CPU {ProcessTuning.Describe(p.AffinityMask)} · priority {ProcessTuning.PriorityText(p.Priority)}";
    }

    private void OnRestartTimesChanged(object sender, TextChangedEventArgs e) => UpdateScheduleInfo();

    private void UpdateScheduleInfo()
    {
        var (times, bad) = ParseTimes(RestartTimes.Text);
        if (bad.Count > 0)
        {
            ScheduleInfo.Text = $"Not a time of day: {string.Join(", ", bad)} (use 05:00).";
            ScheduleInfo.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
            return;
        }
        ScheduleInfo.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        var next = new ServerProfile { RestartTimes = times }.NextRestart(DateTime.Now);
        ScheduleInfo.Text = next is { } at
            ? $"Next restart {(at.Date == DateTime.Today ? "today" : "tomorrow")} at {at:HH:mm}. Warnings are said with the say command; restarts go to the start / stop journal (… menu)."
            : "Warnings are said with the say command; restarts go to the start / stop journal (… menu).";
    }

    // ------------------------------------------------------------------------------ the rest

    private void OnEditorChanged(object sender, SelectionChangedEventArgs e) => UpdateEditorPanel();

    private void UpdateEditorPanel()
    {
        if (CustomEditorPanel == null) return;
        CustomEditorPanel.Visibility = (Editor.SelectedItem as ComboBoxItem)?.Tag as string == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnThemePreview(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || Compact.IsChecked == true) return;
        if (Theme.SelectedItem is string name && _themes.FirstOrDefault(t => t.Name == name) is { } t && t != ThemeManager.Current)
            ThemeManager.Apply(t);
    }

    /// <summary>Compact mode at once, to see it (kept only with Save).</summary>
    private void OnCompactPreview(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool on = Compact.IsChecked == true;
        Theme.IsEnabled = !on;
        var theme = Theme.SelectedItem is string name ? _themes.FirstOrDefault(t => t.Name == name) ?? _themeAtOpen : _themeAtOpen;
        Look.Apply(on, theme);
    }

    private static int ParseInt(string s, int fallback, int min, int max) =>
        int.TryParse(s.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void Notify(string text, NotifyKind kind)
    {
        if (Owner is MainWindow w) w.ShowToast(null, text, kind);
        else MessageBox.Show(this, text, "BetterConsole");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_current != null && StoreServer(_current) is { } problem)
        {
            MessageBox.Show(this, problem, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var s = _shell.Settings;
        // The servers, in the order of the list: edited copies go into the profiles the servers run with.
        var list = new List<ServerProfile>();
        // Servers whose CPU affinity or priority changed (only those are touched while they run).
        var tuned = new HashSet<ServerProfile>();
        foreach (var item in _servers)
        {
            var p = item.Original ?? item.Profile;
            if (item.Original != null)
            {
                if (item.Original.AffinityMask != item.Profile.AffinityMask || item.Original.Priority != item.Profile.Priority) tuned.Add(item.Original);
                item.Original.CopyFrom(item.Profile);
            }
            list.Add(p);
        }
        s.Servers = list;
        s.MultiServer = Multi.IsChecked == true;

        s.ConsoleFont = string.IsNullOrWhiteSpace(Font.Text) ? "Cascadia Mono" : Font.Text.Trim();
        s.ConsoleFontSize = double.TryParse(FontSize2.Text, out var fs) ? Math.Clamp(fs, 8, 32) : 13;
        s.ConsoleMaxLines = ParseInt(MaxLines.Text, 20000, 1000, 1_000_000);
        s.HideErrorsInConsole = HideErrors.IsChecked == true;
        s.CompleteServerCommandsOnly = ServerOnly.IsChecked == true;
        s.ConfirmExitWhileRunning = ConfirmExit.IsChecked == true;
        s.MergeSimilarErrors = MergeSimilar.IsChecked == true;
        s.MaxErrorsPerList = ParseInt(MaxErrors.Text, 500, 50, 10000);
        s.ShowAvatars = ShowAvatars.IsChecked == true;
        s.Editor = (Editor.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        s.EditorCommand = EditorCommand.Text.Trim();
        s.Theme = Theme.SelectedItem as string ?? s.Theme;
        _shell.SetCompact(Compact.IsChecked == true);
        s.TabTitles = (TabTitles.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        s.DisabledPlugins = _shell.Plugins.Plugins.Where(x => !x.Enabled).Select(x => x.Id).ToList();
        s.Save();

        _shell.ApplySettings();
        foreach (var vm in _shell.Servers)
        {
            // A changed CPU affinity or priority applies to the running server at once.
            if (tuned.Contains(vm.Profile) && vm.Controller.ProcessId != null && vm.Controller.ApplyProcessSettings() is { } err)
                vm.Notify("Could not set CPU affinity / priority: " + err, NotifyKind.Error);
            if (vm.BridgeConnected) vm.Request("cmds");
        }
        _saved = true;
        DialogResult = true;
    }
}
