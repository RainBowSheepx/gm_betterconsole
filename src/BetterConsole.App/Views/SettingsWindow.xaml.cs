using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterConsole.App.Plugins;
using BetterConsole.App.Services;
using BetterConsole.App.Themes;
using BetterConsole.App.ViewModels;
using BetterConsole.Core.Server;
using Microsoft.Win32;

namespace BetterConsole.App.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly PluginManager _plugins;
    private readonly ThemePalette _themeAtOpen;
    private readonly List<ThemePalette> _themes;
    private bool _saved;

    public SettingsWindow(MainViewModel vm, PluginManager plugins, bool firstRun)
    {
        _vm = vm;
        _plugins = plugins;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _themeAtOpen = ThemeManager.Current;
        Welcome.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;

        var s = vm.Settings;
        var p = s.Server;
        ServerDir.Text = p.ServerDirectory;
        Args.Text = p.Arguments;
        AutoRestart.IsChecked = p.AutoRestart;
        RestartDelay.Text = p.RestartDelaySeconds.ToString();
        InstallCompanion.IsChecked = p.InstallCompanion;
        StartWithApp.IsChecked = p.StartWithApp;
        StopTimeout.Text = p.StopTimeoutSeconds.ToString();
        FillExecutables(p.Executable);

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

        _themes = ThemeManager.Discover(Path.Combine(AppSettings.DataDirectory, "themes"));
        foreach (var t in _themes) Theme.Items.Add(t.Name);
        Theme.SelectedItem = ThemeManager.Current.Name;
        TabTitles.SelectedItem = TabTitles.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.TabTitles) ?? TabTitles.Items[2];

        PluginList.ItemsSource = plugins.Plugins;
        PluginsInfo.Text = plugins.Plugins.Count == 0
            ? $"No plugins installed. Put plugin folders into {Path.Combine(AppContext.BaseDirectory, "plugins")}."
            : $"{plugins.Plugins.Count} plugin(s) in {Path.Combine(AppContext.BaseDirectory, "plugins")}.";
        DataDir.Text = "Settings: " + (s.FilePath ?? AppSettings.DataDirectory);
        Closed += (_, _) => { if (!_saved) ThemeManager.Apply(_themeAtOpen); };
    }

    private static IEnumerable<string> MonospaceFonts()
    {
        var wanted = new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "JetBrains Mono", "Fira Code", "Source Code Pro", "Hack", "Lucida Console", "Courier New" };
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wanted.Where(installed.Contains);
    }

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
            _vm.Notify("Could not read the file: " + ex.Message, BetterConsole.Sdk.NotifyKind.Error);
            return;
        }
        var m = Regex.Match(text, @"(?im)(srcds(?:_console)?(?:_win64)?\.exe)""?\s+([^\r\n]*)");
        if (!m.Success)
        {
            _vm.Notify("No srcds command found in that file.", BetterConsole.Sdk.NotifyKind.Warning);
            return;
        }
        var args = m.Groups[2].Value.Trim().TrimEnd('"').Trim();
        // "start" options and environment expansions are not srcds arguments.
        args = Regex.Replace(args, @"\s*%[^%\s]+%\s*", " ").Trim();
        Args.Text = args;
        if (string.IsNullOrWhiteSpace(ServerDir.Text)) ServerDir.Text = Path.GetDirectoryName(dlg.FileName) ?? "";
        var exe = m.Groups[1].Value;
        FillExecutables(exe.Contains("win64", StringComparison.OrdinalIgnoreCase) ? "srcds_console_win64.exe" : "srcds_console.exe");
        _vm.Notify($"Imported the options of {exe}.", BetterConsole.Sdk.NotifyKind.Success);
    }

    private void OnThemePreview(object sender, SelectionChangedEventArgs e)
    {
        if (Theme.SelectedItem is string name && _themes.FirstOrDefault(t => t.Name == name) is { } t && t != ThemeManager.Current)
            ThemeManager.Apply(t);
    }

    private static int ParseInt(string s, int fallback, int min, int max) =>
        int.TryParse(s.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = _vm.Settings;
        var p = s.Server;
        p.ServerDirectory = ServerDir.Text.Trim();
        p.Executable = Exe.SelectedItem as string ?? "auto";
        p.Arguments = Args.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        p.AutoRestart = AutoRestart.IsChecked == true;
        p.RestartDelaySeconds = ParseInt(RestartDelay.Text, 5, 0, 600);
        p.InstallCompanion = InstallCompanion.IsChecked == true;
        p.StartWithApp = StartWithApp.IsChecked == true;
        p.StopTimeoutSeconds = ParseInt(StopTimeout.Text, 20, 3, 300);

        s.ConsoleFont = string.IsNullOrWhiteSpace(Font.Text) ? "Cascadia Mono" : Font.Text.Trim();
        s.ConsoleFontSize = double.TryParse(FontSize2.Text, out var fs) ? Math.Clamp(fs, 8, 32) : 13;
        s.ConsoleMaxLines = ParseInt(MaxLines.Text, 20000, 1000, 1_000_000);
        s.HideErrorsInConsole = HideErrors.IsChecked == true;
        s.CompleteServerCommandsOnly = ServerOnly.IsChecked == true;
        s.ConfirmExitWhileRunning = ConfirmExit.IsChecked == true;
        s.MergeSimilarErrors = MergeSimilar.IsChecked == true;
        s.MaxErrorsPerList = ParseInt(MaxErrors.Text, 500, 50, 10000);
        s.Theme = ThemeManager.Current.Name;
        s.TabTitles = (TabTitles.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        s.DisabledPlugins = _plugins.Plugins.Where(x => !x.Enabled).Select(x => x.Id).ToList();
        s.Save();

        _vm.Controller.Profile = p;
        _vm.Controller.Pipeline.HideErrors = s.HideErrorsInConsole;
        _vm.ServerErrors.MaxItems = s.MaxErrorsPerList;
        _vm.ClientErrors.MaxItemsPerPlayer = Math.Max(50, s.MaxErrorsPerList / 2);
        if (_vm.BridgeConnected) _vm.Request("cmds");
        _vm.RaiseSettingsChanged();
        _saved = true;
        DialogResult = true;
    }
}
