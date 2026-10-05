using BetterConsole.Core.Console;
using BetterConsole.Core.Errors;
using BetterConsole.Sdk;
using Xunit;

namespace BetterConsole.Tests;

public class ClassifierTests
{
    private sealed class Harness
    {
        public readonly List<ConsoleEvent> Events = new();
        public readonly PendingCommands Commands = new();
        public DateTime Now = new(2026, 10, 6, 12, 0, 0);
        public readonly OutputClassifier Classifier;
        private long _id;

        public Harness() => Classifier = new OutputClassifier(Events.Add, Commands, () => Now);

        public void Feed(params string[] lines)
        {
            foreach (var l in lines)
            {
                Classifier.Add(++_id, new ConsoleLine { Text = l, Time = Now }, false);
                Now = Now.AddMilliseconds(1);
            }
        }

        /// <summary>Lets time pass so held lines and open errors are released.</summary>
        public void Settle()
        {
            Now = Now.AddSeconds(1);
            Classifier.Tick();
            Now = Now.AddSeconds(1);
            Classifier.Tick();
        }

        public List<string> Shown => Events.OfType<LineAdded>().Select(e => e.Line.Text).ToList();
        public List<LuaError> Errors => Events.OfType<ErrorRecognized>().Select(e => e.Error).ToList();
    }

    [Fact]
    public void ServerErrorWithTimerIsRemovedFromTheConsole()
    {
        var h = new Harness();
        h.Feed("before", "", "[ERROR] lua_run:1: attempt to index local 'x' (a nil value)", "  1. unknown - lua_run:1", "",
            "Timer Failed! [Simple][@lua_run (line 1)]", "", "after");
        h.Settle();
        Assert.Equal(["before", "after"], h.Shown);
        var e = Assert.Single(h.Errors);
        Assert.Equal(LuaRealm.Server, e.Realm);
        Assert.Equal("lua_run:1: attempt to index local 'x' (a nil value)", e.Message);
        Assert.Equal(new StackFrame("unknown", "lua_run", 1), Assert.Single(e.Stack));
        Assert.Equal("Timer Failed! [Simple][@lua_run (line 1)]", e.Context);
    }

    [Fact]
    public void AddonErrorWithDeepStack()
    {
        var h = new Harness();
        h.Feed("",
            "[my-addon] addons/my-addon/lua/autorun/server/a.lua:125: attempt to get length of global 'vehicles' (a nil value)",
            "  1. fn - addons/my-addon/lua/autorun/server/a.lua:125",
            "   2. unknown - lua/includes/modules/hook.lua:96",
            "", "", "next line");
        h.Settle();
        Assert.Equal(["next line"], h.Shown);
        var e = Assert.Single(h.Errors);
        Assert.Equal("my-addon", e.AddonTitle);
        Assert.Equal(2, e.Stack.Count);
        Assert.Equal(new StackFrame("unknown", "lua/includes/modules/hook.lua", 96), e.Stack[1]);
    }

    [Fact]
    public void CompileErrorHasNoStack()
    {
        var h = new Harness();
        h.Feed("[my-addon] addons/my-addon/lua/x/init.lua:179: '<eof>' expected near 'end'", "", "", "ok");
        h.Settle();
        Assert.Equal(["ok"], h.Shown);
        var e = Assert.Single(h.Errors);
        Assert.Empty(e.Stack);
        Assert.Equal("addons/my-addon/lua/x/init.lua:179: '<eof>' expected near 'end'", e.Message);
    }

    [Fact]
    public void ClientErrorIsAttributedToThePlayer()
    {
        var h = new Harness();
        h.Feed("[Ник Игрока|3|STEAM_0:1:2345] Lua Error:", "",
            "[some-addon] addons/some-addon/lua/autorun/client/c.lua:4: attempt to call a nil value",
            "  1. unknown - addons/some-addon/lua/autorun/client/c.lua:4", "", "", "", "server text");
        h.Settle();
        Assert.Equal(["server text"], h.Shown);
        var e = Assert.Single(h.Errors);
        Assert.Equal(LuaRealm.Client, e.Realm);
        Assert.Equal("Ник Игрока", e.Player!.Name);
        Assert.Equal("STEAM_0:1:2345", e.Player.SteamId);
        Assert.Equal(3, e.Player.UserId);
        Assert.Equal("some-addon", e.AddonTitle);
    }

    [Fact]
    public void OrdinaryTaggedPrintsStay()
    {
        var h = new Harness();
        h.Feed("[ULX] Loaded 120 commands", "[MyAddon] config at lua/myaddon/config.lua loaded");
        h.Settle();
        Assert.Equal(2, h.Shown.Count);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public void BlankLinesAreKeptUnlessNextToAnError()
    {
        var h = new Harness();
        h.Feed("a", "", "b", "", "", "c");
        h.Settle();
        Assert.Equal(["a", "", "b", "", "", "c"], h.Shown);
    }

    [Fact]
    public void CommandEchoIsMarkedAndInternalEchoHidden()
    {
        var h = new Harness();
        h.Commands.Add("status", isInternal: false);
        h.Commands.Add("betterconsole_exec 7361", isInternal: true);
        h.Feed("status", "hostname: x", "betterconsole_exec 7361", "Console: hi");
        h.Settle();
        var added = h.Events.OfType<LineAdded>().ToList();
        Assert.Equal(["status", "hostname: x", "Console: hi"], added.Select(a => a.Line.Text));
        Assert.Equal(ConsoleLineKind.Command, added[0].Line.Kind);
        Assert.Equal(ConsoleLineKind.Output, added[1].Line.Kind);
    }

    [Fact]
    public void TransportCommandIsShownAsWhatTheUserTyped()
    {
        var h = new Harness();
        h.Commands.Add("betterconsole_exec 736179", isInternal: true, display: "say Привет");
        h.Feed("betterconsole_exec 736179", "Console: Привет");
        h.Settle();
        var added = h.Events.OfType<LineAdded>().ToList();
        Assert.Equal(["say Привет", "Console: Привет"], added.Select(a => a.Line.Text));
        Assert.Equal(ConsoleLineKind.Command, added[0].Line.Kind);
    }

    [Fact]
    public void ErrorsCanStayVisible()
    {
        var h = new Harness();
        h.Classifier.HideErrors = false;
        h.Feed("[ERROR] x.lua:1: boom", "  1. unknown - x.lua:1", "", "after");
        h.Settle();
        Assert.Contains("[ERROR] x.lua:1: boom", h.Shown);
        Assert.Single(h.Errors);
    }

    [Fact]
    public void MultiLineMessageBeforeTheStack()
    {
        var h = new Harness();
        h.Feed("[ERROR] x.lua:1: first", "second", "  1. unknown - x.lua:1", "", "after");
        h.Settle();
        Assert.Equal(["after"], h.Shown);
        Assert.Equal("x.lua:1: first\nsecond", Assert.Single(h.Errors).Message);
    }

    [Fact]
    public void ErrorPrintWithoutLocationStaysInTheConsole()
    {
        var h = new Harness();
        h.Feed("a", "", "[ERROR] Could not connect to the database", "retrying in 5 s", "b", "", "c");
        h.Settle();
        Assert.Equal(["a", "", "[ERROR] Could not connect to the database", "retrying in 5 s", "b", "", "c"], h.Shown);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public void OutputRightAfterAnErrorWithoutStackIsNotSwallowed()
    {
        var h = new Harness();
        var flood = Enumerable.Range(1, 20).Select(i => "line " + i).ToArray();
        h.Feed(["[my-addon] addons/my-addon/lua/x/init.lua:1: '=' expected near 'y'", .. flood]);
        h.Settle();
        Assert.Equal(flood, h.Shown);
        Assert.Equal("addons/my-addon/lua/x/init.lua:1: '=' expected near 'y'", Assert.Single(h.Errors).Message);
    }

    [Fact]
    public void TimerContextNamesTheRealFunctionNotTheWrapper()
    {
        var h = new Harness();
        h.Feed("[ERROR] addons/a/lua/autorun/server/t.lua:7: boom",
            "  1. fn - addons/a/lua/autorun/server/t.lua:7",
            "   2. unknown - addons/betterconsole/lua/betterconsole/sv_timers.lua:37", "",
            "Timer Failed! [MyTimer][@addons/betterconsole/lua/betterconsole/sv_timers.lua (line 37)]", "", "after");
        h.Settle();
        Assert.Equal(["after"], h.Shown);
        var e = Assert.Single(h.Errors);
        Assert.Equal(new StackFrame("fn", "addons/a/lua/autorun/server/t.lua", 7), Assert.Single(e.Stack));
        Assert.Equal("Timer Failed! [MyTimer][@addons/a/lua/autorun/server/t.lua (line 7)]", e.Context);
    }

    [Fact]
    public void TimerErrorBlamedOnTheCompanionIsGivenBack()
    {
        var h = new Harness();
        h.Feed("", "[betterconsole] lua_run:1: timer boom",
            "  1. error - [C]:-1",
            "   2. fn - lua_run:1",
            "    3. m - addons/betterconsole/lua/betterconsole/sv_profiler.lua:285",
            "     4. unknown - addons/betterconsole/lua/betterconsole/sv_timers.lua:40", "",
            "Timer Failed! [bc_test_timer][@lua_run (line 1)]", "", "after");
        h.Settle();
        Assert.Equal(["after"], h.Shown);
        var e = Assert.Single(h.Errors);
        Assert.Equal("lua_run:1: timer boom", e.Message);
        Assert.Equal(["[C]", "lua_run"], e.Stack.Select(f => f.Source));
        Assert.Null(ErrorAttribution.Fix(e).AddonTitle);

        var other = e with { Stack = [new StackFrame("fn", "addons/my-addon/lua/autorun/server/x.lua", 3)] };
        Assert.Equal("my-addon", ErrorAttribution.Fix(other).AddonTitle);
    }

    [Fact]
    public void TheCompanionsOwnBugsKeepTheirStack()
    {
        var h = new Harness();
        h.Feed("[betterconsole] addons/betterconsole/lua/betterconsole/sv_core.lua:80: oops",
            "  1. Think - addons/betterconsole/lua/betterconsole/sv_core.lua:80",
            "   2. unknown - lua/includes/modules/hook.lua:96", "", "after");
        h.Settle();
        var e = Assert.Single(h.Errors);
        Assert.Equal(2, e.Stack.Count);
        Assert.Equal("betterconsole", ErrorAttribution.Fix(e).AddonTitle);
    }

    [Fact]
    public void FingerprintMergesEntityIndices()
    {
        var a = new LuaError { Realm = LuaRealm.Server, Message = "x.lua:1: Tried to use a NULL entity! Entity [123][prop_physics]" };
        var b = a with { Message = "x.lua:1: Tried to use a NULL entity! Entity [456][prop_physics]" };
        Assert.Equal(ErrorFingerprint.Of(a, true), ErrorFingerprint.Of(b, true));
        Assert.NotEqual(ErrorFingerprint.Of(a, false), ErrorFingerprint.Of(b, false));
    }
}
