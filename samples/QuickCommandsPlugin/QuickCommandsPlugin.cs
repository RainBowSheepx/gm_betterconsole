using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterConsole.Sdk;

namespace QuickCommands;

/// <summary>One button: a label and the console command it runs.</summary>
public sealed record QuickCommand(string Label, string Command, bool Confirm = false);

/// <summary>
/// Sample plugin: a "Quick commands" tab with buttons for commands you run often, read from
/// plugins-data\example.quickcommands\commands.json (created on first start, edit it freely), the time
/// since the last map change in the status bar and on the Statistics tab, and a "Greet in the chat"
/// item in the right-click menu of the Players tab.
/// </summary>
public sealed class QuickCommandsPlugin : IConsolePlugin
{
    private IPluginContext _ctx = null!;
    private IStatusItem? _mapItem;
    private IStatCard? _mapCard;
    private DateTime _mapStarted = DateTime.Now;
    private string? _lastMap;

    public string Id => "example.quickcommands";
    public string Name => "Quick commands";
    public string Description => "Buttons for the console commands you use most (sample plugin).";

    public void Initialize(IPluginContext context)
    {
        _ctx = context;
        context.Ui.AddTab("quickcommands", "Quick commands", "", BuildView, order: 60);
        _mapItem = context.Ui.AddStatusItem("quickcommands.map", "", "Time on the current map (Quick commands plugin)");
        // Like the built-in items: "On map" dimmed, then the time. The name is what the status bar's menu (right-click) lists.
        _mapItem.Label = "On map";
        _mapItem.Name = "Time on the map (Quick commands)";
        _mapItem.Visible = false;
        // A key number on the Statistics tab, after the built-in ones (they use the order 10-110).
        _mapCard = context.Ui.Stats.AddCard("map", "Time on the map", order: 120);
        _mapCard.Tooltip = "Since the last map change (Quick commands plugin)";
        // An item of the player menu, for any number of selected players.
        context.Ui.AddPlayerAction("greet", "Greet in the chat", Greet, icon: "");
        context.Server.SnapshotUpdated += OnSnapshot;
        context.Server.StateChanged += (_, e) =>
        {
            if (e.NewState != ServerState.Running && _mapItem != null) _mapItem.Visible = false;
        };
    }

    private void Greet(IReadOnlyList<PlayerInfo> players)
    {
        // A name may hold a ';' (it would end the command) or quotes.
        var names = string.Join(", ", players.Select(p => p.Name.Replace(";", ",").Replace("\"", "'")));
        _ctx.Server.SendCommand($"say Hello, {names}!");
    }

    private void OnSnapshot(object? sender, ServerSnapshot s)
    {
        if (_mapItem == null || s.Map == null) return;
        if (s.Map != _lastMap)
        {
            _lastMap = s.Map;
            _mapStarted = DateTime.Now;
        }
        var t = DateTime.Now - _mapStarted;
        _mapItem.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        _mapItem.Visible = true;
        if (_mapCard != null)
        {
            _mapCard.Value = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
            _mapCard.Sub = s.Map;
        }
    }

    private List<QuickCommand> LoadCommands()
    {
        var file = Path.Combine(_ctx.DataDirectory, "commands.json");
        if (!File.Exists(file))
        {
            var defaults = new List<QuickCommand>
            {
                new("Status", "status"),
                new("Players (ULX)", "ulx who"),
                new("Save bans", "writeid"),
                new("Announce restart", "say The server restarts in 5 minutes!"),
                new("Clean up the map", "gmod_admin_cleanup", Confirm: true),
                new("List mounted addons", "lua_run for _, a in ipairs(engine.GetAddons()) do print(a.title, a.wsid) end"),
            };
            File.WriteAllText(file, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            return JsonSerializer.Deserialize<List<QuickCommand>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex)
        {
            _ctx.Log("commands.json is not valid: " + ex.Message);
            _ctx.Ui.Notify("Quick commands: commands.json could not be read.", NotifyKind.Error);
            return new();
        }
    }

    private FrameworkElement BuildView()
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        var title = new TextBlock { Text = "Quick commands", FontSize = 15, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        root.Children.Add(title);
        var hint = new TextBlock
        {
            Text = "Edit " + Path.Combine(_ctx.DataDirectory, "commands.json") + " to change these buttons, then press Reload.",
            Margin = new Thickness(0, 4, 0, 14),
            TextWrapping = TextWrapping.Wrap,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        root.Children.Add(hint);

        var panel = new WrapPanel();
        root.Children.Add(panel);

        void Fill()
        {
            panel.Children.Clear();
            foreach (var qc in LoadCommands())
            {
                var b = new Button { Content = qc.Label, Margin = new Thickness(0, 0, 8, 8), ToolTip = qc.Command };
                // Theme styles of the app are available as resources: Btn, Btn.Primary, Btn.Danger, Btn.Ghost.
                b.SetResourceReference(FrameworkElement.StyleProperty, qc.Confirm ? "Btn.Danger" : "Btn.Primary");
                b.Click += (_, _) =>
                {
                    if (qc.Confirm && MessageBox.Show($"Run \"{qc.Command}\"?", qc.Label, MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                    if (_ctx.Server.State != ServerState.Running)
                    {
                        _ctx.Ui.Notify("The server is not running.", NotifyKind.Warning);
                        return;
                    }
                    _ctx.Server.SendCommand(qc.Command);
                    _ctx.Ui.Notify("Sent: " + qc.Command, NotifyKind.Success);
                };
                panel.Children.Add(b);
            }
        }
        Fill();

        var reload = new Button { Content = "Reload", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reload.SetResourceReference(FrameworkElement.StyleProperty, "Btn");
        reload.Click += (_, _) => Fill();
        root.Children.Add(reload);
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
