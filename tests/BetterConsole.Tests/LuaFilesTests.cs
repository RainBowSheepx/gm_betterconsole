using BetterConsole.Core.Errors;
using BetterConsole.Core.Server;
using Xunit;

namespace BetterConsole.Tests;

public class LuaFilesTests : IDisposable
{
    private readonly string _game;

    public LuaFilesTests()
    {
        _game = Path.Combine(Path.GetTempPath(), "bc-luafiles-" + Guid.NewGuid().ToString("N")[..8], "garrysmod");
        Write("lua/includes/modules/hook.lua");
        Write("addons/my addon/lua/autorun/server/sv_shop.lua");
        Write("addons/ulx/lua/ulx/modules/sh/rcon.lua");
        Write("addons/darkrp/gamemodes/darkrp/gamemode/init.lua");
        Write("gamemodes/sandbox/gamemode/init.lua");
        Write("addons/long_named_addon_folder/lua/some/deeply/nested/folder/structure/file_name.lua");
    }

    private void Write(string rel)
    {
        var p = Path.Combine(_game, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "-- test");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_game)!, true); } catch { }
    }

    private string Full(string rel) => Path.GetFullPath(Path.Combine(_game, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void Resolves_the_ways_GMod_names_files()
    {
        var r = new LuaFileResolver(() => _game);
        Assert.Equal(Full("lua/includes/modules/hook.lua"), r.Resolve("lua/includes/modules/hook.lua"));
        Assert.Equal(Full("addons/my addon/lua/autorun/server/sv_shop.lua"), r.Resolve("addons/my addon/lua/autorun/server/sv_shop.lua"));
        // A path relative to lua/ is found in a folder addon too (client errors, workshop-style paths).
        Assert.Equal(Full("addons/ulx/lua/ulx/modules/sh/rcon.lua"), r.Resolve("lua/ulx/modules/sh/rcon.lua"));
        Assert.Equal(Full("addons/ulx/lua/ulx/modules/sh/rcon.lua"), r.Resolve("@addons/ulx/lua/ulx/modules/sh/rcon.lua"));
        Assert.Equal(Full("addons/darkrp/gamemodes/darkrp/gamemode/init.lua"), r.Resolve("gamemodes/darkrp/gamemode/init.lua"));
        Assert.Equal(Full("gamemodes/sandbox/gamemode/init.lua"), r.Resolve("gamemodes/sandbox/gamemode/init.lua"));
        Assert.Equal(Full("lua/includes/modules/hook.lua"), r.Resolve("includes/modules/hook.lua"));
    }

    [Fact]
    public void Does_not_resolve_what_is_not_a_file_of_the_server()
    {
        var r = new LuaFileResolver(() => _game);
        Assert.Null(r.Resolve("[C]"));
        Assert.Null(r.Resolve("lua_run"));
        Assert.Null(r.Resolve("addons/missing/lua/x.lua"));
        Assert.Null(r.Resolve("../../../../Windows/win.lua"));
        Assert.Null(r.Resolve(@"C:\Windows\System32\drivers\etc\hosts.lua"));
    }

    [Fact]
    public void Resolves_a_path_Lua_shortened()
    {
        var r = new LuaFileResolver(() => _game);
        const string shortened = "...folder/lua/some/deeply/nested/folder/structure/file_name.lua";
        // The index is built in the background on first use.
        string? found = null;
        for (int i = 0; i < 100 && found == null; i++)
        {
            found = r.Resolve(shortened);
            if (found == null) Thread.Sleep(50);
        }
        Assert.Equal(Full("addons/long_named_addon_folder/lua/some/deeply/nested/folder/structure/file_name.lua"), found);
    }

    [Fact]
    public void Finds_paths_in_error_texts()
    {
        var links = LuaFileResolver.Scan("addons/my_shop/lua/autorun/sv_shop.lua:22: attempt to index local 'cfg' (a nil value)");
        var l = Assert.Single(links);
        Assert.Equal("addons/my_shop/lua/autorun/sv_shop.lua", l.Source);
        Assert.Equal(22, l.Line);
        Assert.Equal(0, l.Start);

        var timer = LuaFileResolver.Scan("Timer Failed! [demo_hud_sync][@addons/demo/lua/autorun/server/x.lua (line 37)]");
        Assert.Equal("addons/demo/lua/autorun/server/x.lua", Assert.Single(timer).Source);
        Assert.Equal(37, timer[0].Line);

        // Stack lines: the path may contain spaces.
        var stack = LuaFileResolver.Scan("1. ShopThink - addons/my addon/lua/autorun/server/sv_shop.lua:22\n  2. unknown - lua/includes/modules/hook.lua:96\n    3. Call - [C]");
        Assert.Equal(2, stack.Count);
        Assert.Equal("addons/my addon/lua/autorun/server/sv_shop.lua", stack[0].Source);
        Assert.Equal(96, stack[1].Line);

        Assert.Empty(LuaFileResolver.Scan("lua_run:1: attempt to call a nil value"));
        Assert.Equal("...ib/shared/hook.lua", Assert.Single(LuaFileResolver.Scan("...ib/shared/hook.lua:110: oops")).Source);
    }

    [Fact]
    public void Next_scheduled_restart()
    {
        var p = new ServerProfile { RestartTimes = ["05:00", "17:30"] };
        Assert.Equal(new DateTime(2026, 10, 6, 17, 30, 0), p.NextRestart(new DateTime(2026, 10, 6, 12, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 7, 5, 0, 0), p.NextRestart(new DateTime(2026, 10, 6, 17, 30, 0)));
        Assert.Null(new ServerProfile().NextRestart(DateTime.Now));
        Assert.False(ServerProfile.TryParseTime("25:00", out _));
        Assert.True(ServerProfile.TryParseTime("5.07", out var t));
        Assert.Equal(new TimeSpan(5, 7, 0), t);
    }

    [Fact]
    public void Describes_affinity_masks()
    {
        Assert.Equal($"all {ProcessTuning.ProcessorCount}", ProcessTuning.Describe(0));
        if (ProcessTuning.ProcessorCount >= 8)
        {
            Assert.Equal("2-3", ProcessTuning.Describe(0b1100));
            Assert.Equal("0, 2-3, 7", ProcessTuning.Describe(0b1000_1101));
            Assert.Equal(4, ProcessTuning.Count(0b1000_1101));
        }
        Assert.NotEmpty(ProcessTuning.Cores());
    }
}
