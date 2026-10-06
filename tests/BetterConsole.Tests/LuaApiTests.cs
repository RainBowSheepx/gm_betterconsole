using MoonSharp.Interpreter;
using Xunit;

namespace BetterConsole.Tests;

/// <summary>
/// The public Lua API (sv_api.lua) run in MoonSharp with the few GMod functions it needs: what each call
/// sends to the app, and what the app's requests do on the server.
/// </summary>
public class LuaApiTests
{
    private sealed class Api
    {
        public readonly Script Lua = new(CoreModules.Preset_Default);
        public readonly List<Table> Sent = new();

        public Api()
        {
            Lua.Globals["BetterConsole"] = new Table(Lua);
            Lua.DoString("""
                isstring = function(v) return type(v) == "string" end
                istable = function(v) return type(v) == "table" end
                isfunction = function(v) return type(v) == "function" end
                math.Clamp = function(v, lo, hi) return math.min(math.max(v, lo), hi) end
                SysTime = function() return 1000.25 end
                errors = {}
                ErrorNoHalt = function(s) errors[#errors + 1] = s end
                players = {}
                player = { GetAll = function() return players end }
                function Bot(uid, frozen) return { UserID = function() return uid end, IsFrozen = function() return frozen end, uid = uid } end
                """);
            ((Table)Lua.Globals["BetterConsole"])["Emit"] = DynValue.NewCallback((_, args) =>
            {
                Sent.Add(args[0].Table);
                return DynValue.True;
            });
            Lua.DoString(File.ReadAllText(Path.Combine(RepoRoot(), "addon", "betterconsole", "lua", "betterconsole", "sv_api.lua")), null, "sv_api.lua");
        }

        public void Run(string code) => Lua.DoString(code);
        public Table Last(string type) => Sent.Last(m => m.Get("t").String == type);
    }

    [Fact]
    public void A_player_action_is_sent_with_its_dialog_and_runs_on_the_players_still_there()
    {
        var api = new Api();
        api.Run("""
            BetterConsole.AddPlayerAction("heal", {
                text = "Heal", icon = "E95E", order = 250, confirm = "Heal {players}?",
                fields = { { id = "hp", text = "Health", type = "number", default = 100 } },
                bots = false,
                onRun = function(plys, values) healed = #plys; hp = values.hp end,
            })
            players = { Bot(2), Bot(3) }
            BetterConsole.HandlePlayerAction({ id = "heal", uids = { 3, 99 }, values = { hp = 55 } })
            """);
        var pa = api.Last("pa");
        Assert.Equal("heal", pa.Get("id").String);
        Assert.Equal(250, pa.Get("order").Number);
        Assert.True(pa.Get("run").Boolean);
        Assert.False(pa.Get("bots").Boolean);
        Assert.True(pa.Get("multi").Boolean);
        Assert.Equal("hp", ((Table)pa.Get("fields").Table[1]).Get("id").String);
        Assert.Equal(1, api.Lua.Globals.Get("healed").Number);
        Assert.Equal(55, api.Lua.Globals.Get("hp").Number);
    }

    [Fact]
    public void Filters_flag_the_players_they_accept_and_a_failing_one_is_reported_once()
    {
        var api = new Api();
        api.Run("""
            BetterConsole.AddPlayerAction("freeze", { text = "Freeze", filter = function(p) return not p:IsFrozen() end, onRun = function() end })
            BetterConsole.AddPlayerAction("unfreeze", { text = "Unfreeze", filter = function(p) return p:IsFrozen() end, onRun = function() end })
            BetterConsole.AddPlayerAction("broken", { text = "Broken", filter = function(p) error("oops") end, onRun = function() end })
            a = BetterConsole.PlayerActionFlags(Bot(1, false))
            b = BetterConsole.PlayerActionFlags(Bot(2, true))
            """);
        var a = api.Lua.Globals.Get("a").Table;
        var b = api.Lua.Globals.Get("b").Table;
        Assert.True(a.Get("freeze").Boolean);
        Assert.True(a.Get("unfreeze").IsNil());
        Assert.True(b.Get("unfreeze").Boolean);
        Assert.Equal(1, api.Lua.Globals.Get("errors").Table.Length);
        Assert.True(api.Last("pa").Get("filtered").Boolean);
    }

    [Fact]
    public void An_item_that_does_nothing_is_reported()
    {
        var api = new Api();
        api.Run("""BetterConsole.AddPlayerAction("nothing", { text = "Nothing" })""");
        Assert.Equal(1, api.Lua.Globals.Get("errors").Table.Length);
    }

    [Fact]
    public void Built_in_items_are_hidden_and_shown_again_as_one_list()
    {
        var api = new Api();
        api.Run("""BetterConsole.HidePlayerAction("jail", "gag") BetterConsole.ShowPlayerAction("jail")""");
        var ids = api.Last("pa_hide").Get("ids").Table.Values.Select(v => v.String).ToList();
        Assert.Equal(["gag"], ids);
        api.Run("""BetterConsole.AddPlayerAction("x", { command = "ulx slay {target}" }) BetterConsole.RemovePlayerAction("x")""");
        Assert.Equal("x", api.Last("pa_rm").Get("id").String);
    }

    [Fact]
    public void The_statistics_tab_takes_stats_charts_and_hides_built_in_parts()
    {
        var api = new Api();
        api.Run("""
            local s = BetterConsole.Stats:Stat("money", { title = "Money", order = 95 })
            s:Set("$1.2M", "40 wallets")
            local c = BetterConsole.Stats:Chart("money_chart", { title = "Money" })
            BetterConsole.Stats:Hide("players", "chart.entities")
            c:Remove()
            """);
        var w = api.Sent.First(m => m.Get("t").String == "w");
        Assert.Equal("@stats", w.Get("tab").String);
        Assert.Equal("stat", w.Get("kind").String);
        var data = api.Last("wd").Get("data").Table;
        Assert.Equal("$1.2M", data.Get("value").String);
        Assert.Equal("40 wallets", data.Get("sub").String);
        Assert.Equal("money_chart", api.Last("w_rm").Get("id").String);
        Assert.Equal(2, api.Last("stats_hide").Get("ids").Table.Length);
        // The built-in tab cannot be removed, nor replaced by a tab of the same id.
        api.Run("""BetterConsole.Stats:Remove() same = BetterConsole.AddTab("@stats") == BetterConsole.Stats""");
        Assert.DoesNotContain(api.Sent, m => m.Get("t").String == "tab_rm");
        Assert.True(api.Lua.Globals.Get("same").Boolean);
    }

    [Fact]
    public void A_status_item_with_options_has_its_label_name_and_order()
    {
        var api = new Api();
        api.Run("""
            BetterConsole.SetStatus("jobs", { label = "Jobs", text = 12, tooltip = "Waiting jobs", name = "Job queue", order = 50 })
            BetterConsole.SetStatus("plain", "Text", "Tip")
            """);
        var st = api.Sent.First(m => m.Get("t").String == "st");
        Assert.Equal("Jobs", st.Get("label").String);
        Assert.Equal("12", st.Get("text").String);
        Assert.Equal("Job queue", st.Get("name").String);
        Assert.Equal(50, st.Get("order").Number);
        Assert.Equal("Tip", api.Last("st").Get("tip").String);
    }

    [Fact]
    public void A_reconnect_sends_everything_again()
    {
        var api = new Api();
        api.Run("""
            BetterConsole.Stats:Stat("money", { title = "Money" }):Set(5)
            BetterConsole.AddPlayerAction("heal", { text = "Heal", onRun = function() end })
            BetterConsole.HidePlayerAction("ban")
            BetterConsole.Stats:Hide("uptime")
            """);
        api.Sent.Clear();
        api.Run("BetterConsole.ResendRegistry()");
        var types = api.Sent.Select(m => m.Get("t").String).ToList();
        Assert.Contains("w", types);
        Assert.Contains("wd", types);
        Assert.Contains("stats_hide", types);
        Assert.Contains("pa", types);
        Assert.Contains("pa_hide", types);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BetterConsole.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
