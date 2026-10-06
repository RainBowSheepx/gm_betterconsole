using System.Text;
using System.Text.Json;
using BetterConsole.App.Services;
using BetterConsole.App.ViewModels;
using Xunit;

namespace BetterConsole.Tests;

/// <summary>What the app makes of the companion's messages for the widgets of 0.4: tables, forms, buttons, profilers, lag spikes.</summary>
public class LuaWidgetTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ------------------------------------------------------------------------------ tables

    private static TableWidgetVm Table(string opts)
    {
        var t = new TableWidgetVm { Id = "t", Kind = "table" };
        t.Configure(J(opts));
        return t;
    }

    [Fact]
    public void Rows_with_a_key_are_updated_in_place_added_and_removed()
    {
        var t = Table("""{"columns":["Class",{"text":"Count","align":"right","width":80}],"key":1}""");
        t.Apply("set", J("""[["prop",10],["npc",2],["door",4]]"""));
        var prop = t.Rows[0];
        int changes = 0;
        prop.PropertyChanged += (_, _) => changes++;
        t.Apply("set", J("""[["npc",3],["prop",11],["light",1]]"""));
        // The same row object (the grid keeps its selection), its cells changed; the door went, the light came.
        Assert.Same(prop, t.Rows.Single(r => r[0] == "prop"));
        Assert.Equal("11", prop[1]);
        Assert.Equal(1, changes);
        Assert.Equal(["npc", "prop", "light"], t.Rows.Select(r => r[0]));
        Assert.Equal(new TableColumn("Count", "right", "80"), t.Columns[1]);
        // The same cells again: nothing changes.
        t.Apply("set", J("""[["npc",3],["prop",11],["light",1]]"""));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Rows_without_a_key_are_matched_by_position()
    {
        var t = Table("""{"columns":["A","B"]}""");
        t.Apply("set", J("""[["a",1],["b",2],["c",3]]"""));
        var first = t.Rows[0];
        t.Apply("set", J("""[["x",9],{"1":"y","2":8}]"""));
        Assert.Same(first, t.Rows[0]);
        Assert.Equal(["x", "y"], t.Rows.Select(r => r[0]));
        Assert.Equal("8", t.Rows[1][1]);
    }

    [Fact]
    public void Sorting_compares_numbers_as_numbers_and_holds_still_under_the_pointer()
    {
        var t = Table("""{"columns":["Name","ms"],"key":1,"sort":{"column":2,"descending":true}}""");
        t.Apply("set", J("""[["a","9 ms"],["b","10 ms"],["c","2.5 ms"],["d","n/a"]]"""));
        Assert.Equal(["b", "a", "c", "d"], t.Rows.Select(r => r[0]));
        t.Hold = true;
        t.Apply("set", J("""[["a","90 ms"],["b","10 ms"],["c","2.5 ms"],["d","n/a"],["e","50 ms"]]"""));
        // Held: a keeps its place, the new row goes to the end.
        Assert.Equal(["b", "a", "c", "d", "e"], t.Rows.Select(r => r[0]));
        t.Hold = false;
        Assert.Equal(["a", "e", "b", "c", "d"], t.Rows.Select(r => r[0]));
        // A click on Name: text from A; again: the other way round.
        t.SortBy(0);
        Assert.Equal(["a", "b", "c", "d", "e"], t.Rows.Select(r => r[0]));
        t.SortBy(0);
        Assert.Equal(["e", "d", "c", "b", "a"], t.Rows.Select(r => r[0]));
        // The same sort option again (a profiler's next report) keeps the user's choice.
        t.Configure(J("""{"columns":["Name","ms"],"key":1,"sort":{"column":2,"descending":true}}"""));
        Assert.Equal(0, t.SortColumn);
        Assert.Equal(41, TableWidgetVm.LeadingNumber("41 %"));
        Assert.Equal(1234567, TableWidgetVm.LeadingNumber("1,234,567"));
        Assert.Equal(1200, TableWidgetVm.LeadingNumber("$1,200"));
        Assert.Equal(-5, TableWidgetVm.LeadingNumber("-$5"));
        Assert.True(double.IsNaN(TableWidgetVm.LeadingNumber("server.dll")));
    }

    [Fact]
    public void A_row_with_a_hole_keeps_its_cells_in_their_columns_and_columns_stay_when_sent_again()
    {
        var t = Table("""{"columns":["Player","Money","Job"]}""");
        var grid = t.Columns;
        // { "Alice", nil, "Mayor" } in Lua.
        t.Apply("set", J("""[{"1":"Alice","3":"Mayor"}]"""));
        Assert.Equal("", t.Rows[0][1]);
        Assert.Equal("Mayor", t.Rows[0][2]);
        t.Configure(J("""{"columns":["Player","Money","Job"]}"""));
        Assert.Same(grid, t.Columns);
    }

    [Fact]
    public void A_widget_made_without_options_appears()
    {
        // Lua sends an empty table of options as [].
        var tab = new LuaTabVm("t");
        tab.Define("log", "log", J("[]"));
        tab.Define("kv", "kv", J("[]"));
        Assert.Equal(2, tab.Widgets.Count);
        Assert.Null(tab.Widgets[0].Title);
    }

    [Fact]
    public void Commas_in_typed_numbers_follow_the_culture()
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Assert.True(LuaJson.TryNumber("3,000", out var a) && a == 3000);
            Assert.True(LuaJson.TryNumber("1,234.5", out var b) && b == 1234.5);
            Assert.False(LuaJson.TryNumber("1,5", out _));
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            Assert.True(LuaJson.TryNumber("1,5", out var c) && c == 1.5);
            Assert.True(LuaJson.TryNumber("2.25", out var d) && d == 2.25);
            Assert.False(LuaJson.TryNumber("1,2,3", out _));
            Assert.False(LuaJson.TryNumber("NaN", out _));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = old;
        }
        // Values from Lua are not rounded.
        Assert.Equal("0.0625", LuaJson.Plain(J("0.0625")));
    }

    // ------------------------------------------------------------------------------ forms

    private static FormWidgetVm Form(string fields, List<(string Type, object Data)>? sent = null)
    {
        var tab = new LuaTabVm("myaddon") { Send = (type, data) => sent?.Add((type, data)) };
        tab.Define("f", "form", J($$"""{"title":"Settings","fields":{{fields}}}"""));
        return (FormWidgetVm)tab.Widgets[0];
    }

    [Fact]
    public void A_form_knows_its_fields_and_what_differs_from_the_default()
    {
        var f = Form("""
            [{"id":"_1","type":"header","text":"Sending"},
             {"id":"dist","type":"number","convar":"myaddon_distance","text":"Distance","default":"3000","min":0,"max":20000,"step":500,"unit":"units"},
             {"id":"on","type":"bool","convar":"myaddon_on","default":"1"},
             {"id":"mode","type":"choice","choices":[["0 · off","0"],["1 · on","1"]],"default":"1"},
             {"id":"secret","type":"text","convar":"rcon_password","protected":true},
             {"id":"part","type":"bool","convar":"other_part","missing":true}]
            """);
        Assert.Equal(6, f.Fields.Count);
        f.Apply("set", J("""{"values":{"dist":"3000.0","on":"true","mode":"0"}}"""));
        var dist = f.Field("dist")!;
        Assert.False(dist.IsModified);
        Assert.False(f.Field("on")!.IsModified);
        Assert.True(f.Field("mode")!.IsModified);
        Assert.Equal("0 · off", f.Field("mode")!.Shown("0"));
        Assert.Equal(500, dist.Step);
        Assert.Contains("Console variable myaddon_distance", dist.Tooltip);
        Assert.Contains("Between 0 and 20000", dist.Tooltip);
        Assert.False(f.Field("secret")!.CanChange);
        Assert.False(f.Field("part")!.CanChange);
        Assert.False(f.Field("_1")!.IsInput);
        // Every value from the server is heard, also one that did not change (a change it refused).
        int heard = 0;
        dist.ServerValue += () => heard++;
        f.Apply("set", J("""{"values":{"dist":"3000.0"}}"""));
        Assert.Equal(1, heard);
    }

    [Fact]
    public void Typed_values_are_checked_before_they_are_sent()
    {
        var sent = new List<(string Type, object Data)>();
        var f = Form("""
            [{"id":"dist","type":"number","convar":"d","min":0,"max":20000},
             {"id":"name","type":"text","convar":"n"},
             {"id":"mode","type":"choice","choices":["a","b"]},
             {"id":"on","type":"bool"}]
            """, sent);
        Assert.Null(f.Field("dist")!.Check("4500.5", out var n));
        Assert.Equal(4500.5, n);
        Assert.NotNull(f.Field("dist")!.Check("99999", out _));
        Assert.NotNull(f.Field("dist")!.Check("NaN", out _));
        Assert.NotNull(f.Field("name")!.Check("a\"b", out _));
        Assert.NotNull(f.Field("mode")!.Check("c", out _));
        Assert.Null(f.Field("on")!.Check("0", out var b));
        Assert.Equal(false, b);
        f.Change(f.Field("dist")!, 4500.0);
        var (type, data) = Assert.Single(sent);
        Assert.Equal("form", type);
        Assert.Equal("""{"tab":"myaddon","widget":"f","field":"dist","value":4500}""", JsonSerializer.Serialize(data));
    }

    [Fact]
    public void A_form_defined_again_keeps_its_values()
    {
        var tab = new LuaTabVm("myaddon");
        tab.Define("f", "form", J("""{"fields":[{"id":"a","type":"text"}]}"""));
        var f = (FormWidgetVm)tab.Widgets[0];
        f.Apply("set", J("""{"values":{"a":"x"}}"""));
        var field = f.Field("a");
        tab.Define("f", "form", J("""{"fields":[{"id":"a","type":"text"}]}"""));
        Assert.Same(field, f.Field("a"));
        tab.Define("f", "form", J("""{"fields":[{"id":"a","type":"text"},{"id":"b","type":"bool"}]}"""));
        Assert.Equal("x", f.Field("a")!.Value);
        Assert.Equal(2, f.Fields.Count);
    }

    [Fact]
    public void Buttons_with_fields_send_their_values()
    {
        var sent = new List<(string Type, object Data)>();
        var tab = new LuaTabVm("t") { Send = (type, data) => sent.Add((type, data)) };
        tab.Define("b", "buttons", J("""{"buttons":[{"id":"spawn","text":"Spawn","fields":[{"id":"count","type":"number","default":5}]},{"id":"plain"}]}"""));
        var w = (ButtonsWidgetVm)tab.Widgets[0];
        Assert.Equal("count", Assert.Single(w.Buttons[0].Fields).Id);
        Assert.Empty(w.Buttons[1].Fields);
        var values = new Dictionary<string, string> { ["count"] = "7" };
        Assert.Null(PlayerActionDef.TypedValues(w.Buttons[0].Fields, values, out var typed));
        w.Press(w.Buttons[0], typed);
        w.Press(w.Buttons[1]);
        Assert.Equal("""{"tab":"t","widget":"b","id":"spawn","values":{"count":7}}""", JsonSerializer.Serialize(sent[0].Data));
        Assert.Equal("""{"tab":"t","widget":"b","id":"plain"}""", JsonSerializer.Serialize(sent[1].Data));
    }

    // ------------------------------------------------------------------------------ profiler, lag spikes

    [Fact]
    public void An_addons_profiler_shows_only_the_columns_it_has_and_its_own_tables()
    {
        var s = new StatsVm();
        s.SetProvider(J("""{"name":"Mine","info":"Costs little","vprof":false,"capture":true}"""));
        Assert.Equal("Mine", s.ProviderName);
        Assert.False(s.ProviderVprof);
        Assert.True(s.ProviderCapture);
        s.Profiling = true;
        s.ApplyProfile(J("""
            {"hooks":[{"k":"Think / x","ms":0.8,"n":33,"kb":4.5,"src":"addons/x/lua/y.lua:12"}],"since":42,
             "extra":[{"id":"modules","title":"By module","columns":["Module","of busy"],"rows":[["server.dll","41 %"],["engine.dll","20 %"]]}]}
            """));
        Assert.True(s.HasField(s.Hooks, "kb"));
        Assert.False(s.HasField(s.Hooks, "max"));
        Assert.Equal(4.5, s.Hooks[0].KbPerSec);
        var table = Assert.Single(s.ExtraTables);
        var row = table.Rows[0];
        // Keyed by the first column: the next report updates the same rows.
        s.ApplyProfile(J("""{"extra":[{"id":"modules","rows":[["engine.dll","25 %"],["server.dll","40 %"]]}]}"""));
        Assert.Same(row, table.Rows.Single(r => r[0] == "server.dll"));
        Assert.Equal("40 %", row[1]);
        // Back to the built-in profiler: the other one's results go.
        s.SetProvider(default);
        Assert.Null(s.ProviderName);
        Assert.True(s.ProviderVprof);
        Assert.Empty(s.Hooks);
        Assert.Empty(s.ExtraTables);
    }

    [Fact]
    public void Addons_add_chips_lua_and_the_engine_part_to_a_long_frame()
    {
        var row = SpikeRow.From(J("""
            {"ms":120,"busy":110,"time":1700000000,
             "causes":[{"text":"server.dll 45%","kind":"accent","tooltip":"From my module"},{"text":"odd","kind":"pink"}],
             "lua":[{"text":"net my_message","ms":8.4,"src":"addons/x/lua/y.lua:30"}],
             "engine":[{"text":"SendClientMessages","ms":20.1}]}
            """));
        Assert.Equal(["server.dll 45%", "odd"], row.Causes.Select(c => c.Text));
        Assert.Equal("accent", row.Causes[1].Kind);
        Assert.True(row.HasLua);
        Assert.Equal("addons/x/lua/y.lua:30", row.Lua[0].Source);
        Assert.Equal($"{8.4:F1} ms", row.Lua[0].MsText);   // in the culture of the app, like its other numbers
        // With an engine part from an addon the row does not wait for vprof's report.
        Assert.True(row.HasEngine);
        Assert.Equal("20 ms", row.Engine[0].MsText);
        // Without anything from addons, as before.
        var plain = SpikeRow.From(J("""{"ms":120,"busy":110}"""));
        Assert.False(plain.HasEngine);
        Assert.Single(plain.Causes);
    }

    // ------------------------------------------------------------------------------ the companion's install

    [Fact]
    public void A_server_folder_written_with_slashes_gets_the_addon_and_keeps_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "bc_install_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var game = Path.Combine(root, "garrysmod");
            var addon = Path.Combine(game, "addons", "betterconsole", "lua");
            Directory.CreateDirectory(addon);
            File.WriteAllText(Path.Combine(addon, "old_file.lua"), "-- from an older version");
            var files = new List<(string, Func<Stream>)>
            {
                ("lua\\autorun\\!betterconsole.lua", () => new MemoryStream(Encoding.UTF8.GetBytes("-- loader"))),
                ("lua/betterconsole/sv_api.lua", () => new MemoryStream(Encoding.UTF8.GetBytes("-- api"))),
            };
            var r = CompanionInstaller.InstallAddon(game.Replace('\\', '/'), files);
            Assert.Equal(2, r.Written);
            Assert.Empty(r.Warnings);
            Assert.True(File.Exists(Path.Combine(addon, "betterconsole", "sv_api.lua")));
            Assert.False(File.Exists(Path.Combine(addon, "old_file.lua")));
            // Again: nothing to write, nothing deleted.
            r = CompanionInstaller.InstallAddon(game.Replace('\\', '/') + "/", files);
            Assert.Equal(0, r.Written);
            Assert.Equal(2, r.Unchanged);
            Assert.True(File.Exists(Path.Combine(addon, "autorun", "!betterconsole.lua")));
            // No file of the addon: the ones there stay, and the user is told.
            r = CompanionInstaller.InstallAddon(game, []);
            Assert.Single(r.Warnings);
            Assert.True(File.Exists(Path.Combine(addon, "betterconsole", "sv_api.lua")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v0.4.1", "0.4.1")]
    [InlineData("0.4.0+aebb997", "0.4.0")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("v1.0.0-beta", "1.0.0")]
    public void Tags_and_build_versions_compare_as_versions(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), BetterConsole.App.Services.UpdateChecker.ParseVersion(text));

    [Fact]
    public void A_release_of_the_api_opens_only_a_page_of_this_repository()
    {
        var r = BetterConsole.App.Services.UpdateChecker.FromJson(System.Text.Json.JsonDocument.Parse(
            """{"tag_name":"v9.9.9","html_url":"https://example.com/x","name":"v9.9.9","published_at":"2026-10-06T10:00:00Z"}""").RootElement);
        Assert.NotNull(r);
        Assert.Equal(new Version(9, 9, 9), r!.Version);
        Assert.Equal(BetterConsole.App.Services.UpdateChecker.ReleasesPage, r.Url);
        Assert.True(new BetterConsole.App.Services.UpdateChecker.Result(r, null).IsNewer);
        Assert.Null(BetterConsole.App.Services.UpdateChecker.FromJson(System.Text.Json.JsonDocument.Parse("""{"tag_name":"latest"}""").RootElement));
    }
}
