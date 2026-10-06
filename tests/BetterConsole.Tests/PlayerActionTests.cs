using System.Text.Json;
using BetterConsole.App.ViewModels;
using Xunit;

namespace BetterConsole.Tests;

/// <summary>Items of addons in the player menu: what the app reads of them and the commands it makes.</summary>
public class PlayerActionTests
{
    private static PlayerActionDef Parse(string json) => PlayerActionDef.FromLua(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void The_message_of_the_addon_becomes_an_item_with_its_dialog()
    {
        var a = Parse("""
            {"t":"pa","id":"slap","text":"Slap","icon":"E945","order":230,"command":"ulx slap {target} {damage}","bots":false,"multi":true,
             "fields":[{"id":"damage","text":"Damage","choices":[["None","0"],["Ten","10"]],"default":"0"},{"id":"n","text":"Times","type":"number","default":3}]}
            """);
        Assert.Equal("lua:slap", a.Id);
        Assert.Equal("slap", a.LuaId);
        Assert.Equal(230, a.Order);
        Assert.Equal("", a.Icon);
        Assert.False(a.Bots);
        Assert.False(a.LuaRun);
        Assert.Equal(2, a.Fields.Count);
        Assert.Equal([("None", "0"), ("Ten", "10")], a.Fields[0].Choices!.ToArray());
        Assert.True(a.Fields[1].Number);
        Assert.Equal("3", a.Fields[1].Default);
        Assert.True(a.PerPlayer);
    }

    [Fact]
    public void Lua_without_holes_in_its_table_still_sends_an_object_of_fields()
    {
        // util.TableToJSON makes an object of an array with a hole: the fields keep their order.
        var a = Parse("""{"id":"x","fields":{"2":{"id":"b"},"1":{"id":"a"},"3":{"id":"a"}}}""");
        // A second field with an id that is taken is left out.
        Assert.Equal(["a", "b"], a.Fields.Select(f => f.Id).ToArray());
        Assert.Equal(300, a.Order);
        Assert.True(a.Bots);
        Assert.True(a.Multi);
    }

    [Fact]
    public void Commands_get_the_player_and_the_values_without_quotes_or_semicolons()
    {
        var p = new PlayerRowVm(7) { Name = "Bad\"; quit", SteamId = "STEAM_0:1:2", SteamId64 = "765", UlxId = "STEAM_0:1:2" };
        var values = new Dictionary<string, string> { ["reason"] = "spam; rcon_password x\nnow" };
        Assert.Equal("ulx kick \"$STEAM_0:1:2\" spam, rcon_password x now", PlayerActionDef.Expand("ulx kick {target} {reason}", p, values));
        Assert.Equal("say Bad', quit 7 STEAM_0:1:2 765 {unknown}", PlayerActionDef.Expand("say {name} {userid} {steamid} {steamid64} {unknown}", p, values));
        var once = Parse("""{"id":"m","command":"ulx map {map}"}""");
        Assert.False(once.PerPlayer);
    }

    [Fact]
    public void Filters_and_bots_decide_who_an_item_is_for()
    {
        var a = Parse("""{"id":"unfreeze","filtered":true,"bots":false}""");
        var frozen = new PlayerRowVm(1);
        frozen.ActionFlags.Add("unfreeze");
        var bot = new PlayerRowVm(2) { IsBot = true };
        bot.ActionFlags.Add("unfreeze");
        Assert.True(a.AppliesTo(frozen));
        Assert.False(a.AppliesTo(new PlayerRowVm(3)));
        Assert.False(a.AppliesTo(bot));
    }
}
