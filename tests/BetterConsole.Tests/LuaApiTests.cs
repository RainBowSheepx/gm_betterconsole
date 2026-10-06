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
                isbool = function(v) return type(v) == "boolean" end
                isnumber = function(v) return type(v) == "number" end
                string.Trim = function(s) return (s:gsub("^%s+", ""):gsub("%s+$", "")) end
                bit = { band = bit32.band }
                FCVAR_PROTECTED = 32
                -- Console variables: vars[name] = a ConVar; locked[name]: there, but GMod does not give it to Lua.
                vars, locked, cmds = {}, {}, {}
                function Var(name, value, default, help, min, max, flags)
                    local cv = { v = value }
                    function cv:GetString() return self.v end
                    function cv:GetDefault() return default end
                    function cv:GetHelpText() return help end
                    function cv:GetMin() return min end
                    function cv:GetMax() return max end
                    function cv:GetFlags() return flags or 0 end
                    vars[name] = cv
                    return cv
                end
                ConVarExists = function(n) return vars[n] ~= nil or locked[n] == true end
                GetConVar = function(n) return vars[n] end
                concommand = { GetTable = function() return cmds end }
                timers = {}
                timer = { Create = function(name, delay, reps, fn) timers[name] = fn end }
                hooks = {}
                hook = { Run = function(name, ...) hooks[#hooks + 1] = { name, ... } end }
                -- The engine running a command: "name value" sets the variable.
                ran = {}
                BetterConsole.RunCommand = function(cmd)
                    ran[#ran + 1] = cmd
                    local name, value = cmd:match('^(%S+) "?([^"]*)"?$')
                    if vars[name] then vars[name].v = value end
                    return true
                end
                """);
            ((Table)Lua.Globals["BetterConsole"])["Emit"] = DynValue.NewCallback((_, args) =>
            {
                Sent.Add(args[0].Table);
                return DynValue.True;
            });
            Lua.DoString(File.ReadAllText(Path.Combine(RepoRoot(), "addon", "betterconsole", "lua", "betterconsole", "sv_api.lua")), null, "sv_api.lua");
        }

        public void Run(string code)
        {
            try { Lua.DoString(code); }
            catch (InterpreterException ex) { throw new InvalidOperationException(ex.DecoratedMessage ?? ex.Message, ex); }
        }
        public Table Last(string type) => Sent.Last(m => m.Get("t").String == type);
        public IEnumerable<Table> All(string type) => Sent.Where(m => m.Get("t").String == type);
        public DynValue G(string name) => Lua.Globals.Get(name);
        public List<string> Strings(string global) => G(global).Table.Values.Select(v => v.String).ToList();
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

    // ------------------------------------------------------------------------------ forms

    private const string FormSetup = """
        Var("myaddon_mode", "1", "1", "  How it sends.  ")
        Var("myaddon_distance", "3000", "3000", "How far.", 0, 20000)
        Var("myaddon_name", "a b", "x")
        Var("myaddon_secret", "hunter2", "", "A secret.", nil, nil, 32)
        locked["sv_password"] = true
        tab = BetterConsole.AddTab("myaddon", { title = "My addon" })
        form = tab:Form("settings", { title = "Settings", span = 12, fields = {
            { type = "header", text = "Sending" },
            { convar = "myaddon_mode", type = "choice", choices = { { "0 · off", "0" }, { "1 · on", 1 } } },
            { convar = "myaddon_distance", type = "number", step = 500, unit = "units" },
            { convar = "myaddon_name", text = "Name" },
            { convar = "myaddon_optional", type = "bool" },
            { convar = "myaddon_other_part", type = "bool", missing = "show" },
            { convar = "myaddon_secret", type = "text" },
            { convar = "sv_password", type = "text" },
            { id = "level", text = "Level", type = "number", value = 3, min = 1, max = 6 },
            { id = "on", value = true },
            { id = "level", text = "Again" },
        } })
        """;

    private static Dictionary<string, Table> Fields(Table w) =>
        w.Get("opts").Table.Get("fields").Table.Values.Select(v => v.Table).ToDictionary(f => f.Get("id").String);

    [Fact]
    public void A_form_takes_text_help_default_and_range_from_the_variables_and_leaves_out_missing_ones()
    {
        var api = new Api();
        api.Run(FormSetup);
        var w = api.Last("w");
        Assert.Equal("form", w.Get("kind").String);
        Assert.Equal(12, w.Get("opts").Table.Get("span").Number);
        var f = Fields(w);
        // In their order, the missing variable without missing = "show" left out, the second "level" too.
        Assert.Equal(["_1", "myaddon_mode", "myaddon_distance", "myaddon_name", "myaddon_other_part", "myaddon_secret", "sv_password", "level", "on"],
            w.Get("opts").Table.Get("fields").Table.Values.Select(v => v.Table.Get("id").String).ToList());
        Assert.Equal("header", f["_1"].Get("type").String);
        Assert.Equal("myaddon_mode", f["myaddon_mode"].Get("text").String);
        Assert.Equal("How it sends.", f["myaddon_mode"].Get("help").String);
        Assert.Equal("1", f["myaddon_mode"].Get("default").String);
        Assert.Equal("1", ((Table)f["myaddon_mode"].Get("choices").Table[2]).Get(2).String);
        Assert.Equal(0, f["myaddon_distance"].Get("min").Number);
        Assert.Equal(20000, f["myaddon_distance"].Get("max").Number);
        Assert.Equal("units", f["myaddon_distance"].Get("unit").String);
        Assert.True(f["myaddon_other_part"].Get("missing").Boolean);
        // Protected by its flag, or not given to Lua at all: shown as protected, no default, no value.
        Assert.True(f["myaddon_secret"].Get("protected").Boolean);
        Assert.True(f["myaddon_secret"].Get("default").IsNil());
        Assert.True(f["sv_password"].Get("protected").Boolean);
        Assert.Equal("bool", f["on"].Get("type").String);
        Assert.Single(api.Strings("errors"));
        var values = api.Last("wd").Get("data").Table.Get("values").Table;
        Assert.Equal("1", values.Get("myaddon_mode").String);
        Assert.Equal("3000", values.Get("myaddon_distance").String);
        Assert.Equal(3, values.Get("level").Number);
        Assert.True(values.Get("on").Boolean);
        Assert.True(values.Get("myaddon_secret").IsNil());
        Assert.True(values.Get("sv_password").IsNil());
    }

    [Fact]
    public void A_changed_variable_runs_as_a_command_and_wrong_values_are_refused()
    {
        var api = new Api();
        api.Run(FormSetup);
        api.Sent.Clear();
        api.Run("""
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "myaddon_distance", value = 4500 })
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "myaddon_distance", value = 99999 })
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "myaddon_name", value = "two words; more" })
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "myaddon_name", value = 'quote"d' })
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "myaddon_mode", value = "5" })
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "myaddon_secret", value = "x" })
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "sv_password", value = "x" })
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "myaddon_other_part", value = true })
            """);
        Assert.Equal(["myaddon_distance 4500", "myaddon_name \"two words; more\""], api.Strings("ran"));
        // The app shows each in the console, and gets the variable's value back.
        Assert.Equal(["myaddon_distance 4500", "myaddon_name \"two words; more\""], api.All("form_cmd").Select(m => m.Get("cmd").String).ToList());
        var sets = api.All("wd").Select(m => m.Get("data").Table.Get("values").Table).ToList();
        Assert.Equal("4500", sets[0].Get("myaddon_distance").String);
        // Refused (out of range): its value again, so the app puts the control back.
        Assert.Equal("4500", sets[1].Get("myaddon_distance").String);
        var notes = api.All("notify").Select(m => m.Get("text").String).ToList();
        Assert.Contains(notes, n => n.Contains("between 0 and 20000"));
        Assert.Contains(notes, n => n.Contains("quotes"));
        Assert.Contains(notes, n => n.Contains("not one of its choices"));
        Assert.Contains(notes, n => n.Contains("myaddon_secret is protected"));
        Assert.Contains(notes, n => n.Contains("sv_password is protected"));
        Assert.Contains(notes, n => n.Contains("myaddon_other_part is not on this server"));
    }

    [Fact]
    public void Big_whole_numbers_are_written_whole_and_values_json_cannot_carry_are_dropped()
    {
        var api = new Api();
        api.Run("""
            Var("myaddon_money", "0", "0", "", 0, 1e12)
            tab = BetterConsole.AddTab("m")
            form = tab:Form("f", { fields = { { convar = "myaddon_money", type = "number" }, { id = "ratio", type = "number", value = 0/0 } } })
            BetterConsole.HandleForm({ tab = "m", widget = "f", field = "myaddon_money", value = 5000000000 })
            form:Set({ ratio = 1/0 })
            form:Set({ ratio = { 1 } })
            """);
        Assert.Equal(["myaddon_money 5000000000"], api.Strings("ran"));
        Assert.DoesNotContain(api.All("wd"), m => m.Get("data").Table.Get("values").Table.Get("ratio").IsNotNil());
    }

    [Fact]
    public void An_error_in_a_profiler_hook_of_another_addon_does_not_stop_SetProfiler()
    {
        var api = new Api();
        api.Run(BuiltIn);
        api.Run("""
            hook.Run = function() error("bad hook") end
            BetterConsole.ProfilerRequest("prof", true)
            BetterConsole.SetProfiler({ name = "Mine" })
            """);
        Assert.Equal("Mine", api.Last("prof_provider").Get("name").String);
        Assert.Contains(api.Strings("errors"), e => e.Contains("BetterConsoleProfiler hook failed"));
    }

    [Fact]
    public void Own_values_go_to_onChange_typed_and_errors_name_the_form()
    {
        var api = new Api();
        api.Run(FormSetup);
        api.Run("""
            tab:OnChange(function(w, f, v) got = { w, f, v, type(v) } end)
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "level", value = "5" })
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "on", value = false })
            gotOn = got
            BetterConsole.HandleForm({ tab = "myaddon", widget = "settings", field = "level", value = 9 })
            tab:Form("other", { fields = { { id = "x", value = "a" } }, onChange = function() error("boom") end })
            BetterConsole.HandleForm({ tab = "myaddon", widget = "other", field = "x", value = "b" })
            form:Set({ level = 2, myaddon_mode = "0" })
            """);
        var got = api.G("gotOn").Table;
        Assert.Equal("on", got.Get(2).String);
        Assert.False(got.Get(3).Boolean);
        Assert.Equal("boolean", got.Get(4).String);
        Assert.Contains(api.Strings("errors"), e => e.Contains("onChange of form 'other' (tab 'myaddon') failed") && e.Contains("boom"));
        Assert.Contains(api.All("notify"), m => m.Get("text").String.Contains("between 1 and 6"));
        // form:Set changes the values of the addon's own fields only; the variables are the server's.
        var last = api.Last("wd").Get("data").Table.Get("values").Table;
        Assert.Equal(2, last.Get("level").Number);
        Assert.True(last.Get("myaddon_mode").IsNil());
        Assert.Empty(api.Strings("ran"));
    }

    [Fact]
    public void A_shown_tab_hears_about_it_and_its_forms_follow_the_server()
    {
        var api = new Api();
        api.Run(FormSetup);
        api.Run("""
            BetterConsole.Connected = true
            shown = ""
            tab:OnShow(function(on) shown = shown .. (on and "1" or "0") end)
            BetterConsole.Stats:OnShow(function(on) stats = on end)
            BetterConsole.HandleTabShow({ tab = "myaddon", on = true })
            BetterConsole.HandleTabShow({ tab = "@stats", on = true })
            """);
        api.Sent.Clear();
        // Changed from the console, and a variable another part of the addon creates later.
        api.Run("""
            vars["myaddon_distance"].v = "1234"
            timers["BetterConsole.Forms"]()
            Var("myaddon_optional", "1", "0")
            timers["BetterConsole.Forms"]()
            timers["BetterConsole.Forms"]()
            BetterConsole.HandleTabShow({ tab = "myaddon", on = false })
            vars["myaddon_distance"].v = "1"
            timers["BetterConsole.Forms"]()
            BetterConsole.HideAllTabs()
            """);
        var first = api.Sent[0];
        Assert.Equal("wd", first.Get("t").String);
        Assert.Equal("1234", first.Get("data").Table.Get("values").Table.Get("myaddon_distance").String);
        Assert.Single(first.Get("data").Table.Get("values").Table.Keys);
        // The new variable: the fields again, with every value.
        Assert.Contains("myaddon_optional", Fields(api.Last("w")).Keys);
        Assert.Equal(2, api.All("wd").Count());
        Assert.Equal("10", api.G("shown").String);
        Assert.False(api.G("stats").Boolean);
    }

    [Fact]
    public void Buttons_get_the_values_of_their_dialog()
    {
        var api = new Api();
        api.Run("""
            tab = BetterConsole.AddTab("t")
            tab:Buttons("b", { buttons = { { id = "spawn", text = "Spawn", fields = { { id = "count", type = "number", default = 5 } } }, { id = "plain" } } })
            tab:OnAction(function(w, a, values) calls = (calls or 0) + 1 last = { a, values } end)
            BetterConsole.HandleAction({ tab = "t", widget = "b", id = "spawn", values = { count = 7 } })
            spawn = last
            BetterConsole.HandleAction({ tab = "t", widget = "b", id = "plain" })
            """);
        Assert.Equal(7, api.G("spawn").Table.Get(2).Table.Get("count").Number);
        Assert.True(api.G("last").Table.Get(2).IsNil());
        var button = (Table)api.Last("w").Get("opts").Table.Get("buttons").Table[1];
        Assert.Equal("count", ((Table)button.Get("fields").Table[1]).Get("id").String);
    }

    // ------------------------------------------------------------------------------ profiler

    private const string BuiltIn = """
        BetterConsole.Profiler = { on = false, capture = false }
        local P = BetterConsole.Profiler
        function P.Start() P.on = true end
        function P.Stop() P.on = false end
        function P.StartCapture() P.capture = true end
        function P.StopCapture() P.capture = false end
        """;

    [Fact]
    public void An_addons_profiler_takes_the_requests_and_the_built_in_one_stops_first()
    {
        var api = new Api();
        api.Run(BuiltIn);
        api.Run("""
            BetterConsole.ProfilerRequest("prof", true)
            BetterConsole.ProfilerRequest("capture", true)
            log = {}
            BetterConsole.SetProfiler({ name = "Mine", info = "What it costs", vprof = false,
                start = function() log[#log + 1] = "start" end, stop = function() log[#log + 1] = "stop" end,
                startCapture = function() log[#log + 1] = "cap" end, stopCapture = function() log[#log + 1] = "nocap" end })
            builtinOn, builtinCap = BetterConsole.Profiler.on, BetterConsole.Profiler.capture
            BetterConsole.ProfilerRequest("prof", true)
            BetterConsole.ProfilerRequest("capture", true)
            profiling, capturing = BetterConsole.IsProfiling(), BetterConsole.IsCapturing()
            BetterConsole.ProfilerRequest("capture", false)
            """);
        Assert.False(api.G("builtinOn").Boolean);
        Assert.False(api.G("builtinCap").Boolean);
        Assert.Equal(["start", "cap", "nocap"], api.Strings("log"));
        Assert.True(api.G("profiling").Boolean);
        Assert.True(api.G("capturing").Boolean);
        var p = api.Last("prof_provider");
        Assert.Equal("Mine", p.Get("name").String);
        Assert.Equal("What it costs", p.Get("info").String);
        Assert.False(p.Get("vprof").Boolean);
        Assert.True(p.Get("capture").Boolean);
        Assert.False(api.Last("capture_state").Get("on").Boolean);
        Assert.True(api.Last("prof_state").Get("on").Boolean);
        // The hooks hear about every start and stop (the built-in ones too).
        var hooks = api.G("hooks").Table.Values.Select(v => v.Table.Get(1).String + "=" + v.Table.Get(2).Boolean).ToList();
        Assert.Equal(["BetterConsoleProfiler=True", "BetterConsoleCapture=True", "BetterConsoleProfiler=False", "BetterConsoleCapture=False",
            "BetterConsoleProfiler=True", "BetterConsoleCapture=True", "BetterConsoleCapture=False"], hooks);
        // Back to the built-in one: the addon's stops.
        api.Run("""BetterConsole.SetProfiler(nil) BetterConsole.ProfilerRequest("prof", true) builtinOn = BetterConsole.Profiler.on""");
        Assert.Equal("stop", api.Strings("log").Last());
        Assert.True(api.G("builtinOn").Boolean);
        Assert.True(api.Last("prof_provider").Get("name").IsNil());
    }

    [Fact]
    public void A_profiler_may_refuse_to_start_and_its_errors_are_reported()
    {
        var api = new Api();
        api.Run("""
            BetterConsole.SetProfiler({ name = "Picky", start = function() return false, "Not with nobody on" end,
                startCapture = function() error("no capture") end })
            BetterConsole.ProfilerRequest("prof", true)
            BetterConsole.ProfilerRequest("capture", true)
            profiling, capturing = BetterConsole.IsProfiling(), BetterConsole.IsCapturing()
            """);
        Assert.False(api.G("profiling").Boolean);
        Assert.False(api.G("capturing").Boolean);
        var notes = api.All("notify").Select(m => m.Get("text").String).ToList();
        Assert.Equal("Not with nobody on", notes[0]);
        Assert.Contains("Picky could not start the capture", notes[1]);
        Assert.Contains(api.Strings("errors"), e => e.Contains("startCapture of the profiler 'Picky' failed"));
        Assert.Empty(api.G("hooks").Table.Values);
    }

    [Fact]
    public void A_profilers_report_goes_out_as_the_built_in_one_while_it_runs()
    {
        var api = new Api();
        api.Run("""
            report = {
                hooks = { { k = "Think / x", ms = 0.8, n = 33, src = "addons/x/lua/y.lua:12", kb = 4.5 }, { k = "bad", ms = 0/0 }, { ms = 1 } },
                ents = { { k = "prop_physics", n = 120 } }, entTotal = 1500, since = 42,
                extra = { { id = "modules", title = "By module", columns = { "Module", "of busy" }, rows = { { "server.dll", "41 %" } } } },
            }
            BetterConsole.ProfilerReport(report)
            BetterConsole.SetProfiler({ name = "Mine" })
            BetterConsole.ProfilerReport(report)
            BetterConsole.ProfilerRequest("prof", true)
            BetterConsole.ProfilerReport(report)
            """);
        var prof = Assert.Single(api.All("prof"));
        var rows = prof.Get("hooks").Table.Values.Select(v => v.Table).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(4.5, rows[0].Get("kb").Number);
        Assert.True(rows[0].Get("max").IsNil());
        // NaN cannot go into JSON.
        Assert.True(rows[1].Get("ms").IsNil());
        Assert.Equal(1500, prof.Get("entTotal").Number);
        var extra = (Table)prof.Get("extra").Table[1];
        Assert.Equal("modules", extra.Get("id").String);
        Assert.Equal("41 %", ((Table)extra.Get("rows").Table[1]).Get(2).String);
    }

    // ------------------------------------------------------------------------------ lag spikes

    [Fact]
    public void Several_addons_add_to_a_long_frames_row()
    {
        var api = new Api();
        api.Run("""
            BetterConsole.Stats:OnSpike("a", function(s) s.causes = { { text = "a 12 ms", kind = "danger", tooltip = "t" }, { kind = "muted" } } s.lua = { { text = "net a", ms = 12, src = "x.lua:3" } } end)
            BetterConsole.Stats:OnSpike("b", function(s) table.insert(s.causes, { text = "b", kind = "pink" }) table.insert(s.engine, { text = "SendClientMessages", ms = 20 }) end)
            BetterConsole.Stats:OnSpike("broken", function() error("oops") end)
            BetterConsole.Stats:OnSpike("gone", function(s) s.causes = { { text = "gone" } } end)
            BetterConsole.Stats:OnSpike("gone", nil)
            spikes = { { ms = 120, st = 10 }, { ms = 90, st = 11 } }
            BetterConsole.RunSpikeFns(spikes)
            """);
        var rec = (Table)api.G("spikes").Table[1];
        var causes = rec.Get("causes").Table.Values.Select(v => v.Table).ToList();
        Assert.Equal(["a 12 ms", "b"], causes.Select(c => c.Get("text").String));
        Assert.Equal(["danger", "accent"], causes.Select(c => c.Get("kind").String));
        Assert.Equal(12, ((Table)rec.Get("lua").Table[1]).Get("ms").Number);
        Assert.Equal("x.lua:3", ((Table)rec.Get("lua").Table[1]).Get("src").String);
        Assert.Equal("SendClientMessages", ((Table)rec.Get("engine").Table[1]).Get("text").String);
        // A failing function is reported once for the whole summary.
        Assert.Single(api.Strings("errors"), e => e.Contains("OnSpike 'broken' failed"));
        Assert.NotNull(((Table)api.G("spikes").Table[2]).Get("causes").Table);
    }

    [Fact]
    public void A_reconnect_sends_forms_again_with_their_values()
    {
        var api = new Api();
        api.Run(FormSetup);
        api.Sent.Clear();
        api.Run("vars['myaddon_distance'].v = '42' BetterConsole.ResendRegistry()");
        Assert.Contains(api.All("w"), m => m.Get("kind").String == "form");
        var values = api.All("wd").Select(m => m.Get("data").Table).First(d => d.Get("values").Type == DataType.Table).Get("values").Table;
        Assert.Equal("42", values.Get("myaddon_distance").String);
        Assert.Equal(3, values.Get("level").Number);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BetterConsole.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
