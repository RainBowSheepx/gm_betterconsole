using BetterConsole.Core.Console;
using BetterConsole.Sdk;
using Xunit;

namespace BetterConsole.Tests;

public class VprofTests
{
    // What vprof_dump_spikes printed on a GMod server for a frame stuck 150 ms in Lua (shortened).
    private static readonly string[] Report =
    [
        "******** BEGIN VPROF REPORT ********",
        "-- Summary --",
        "1 frames sampled for 0.15 seconds",
        "Average 6.64 fps, 150.51 ms per frame",
        "Peak 150.51 ms frame",
        "100 pct of time accounted for",
        "",
        "-- Profile scopes sorted by time (including children) --",
        "  Scope                                                      Calls Calls/Frame  Time+Child    Pct        Time    Pct   Avg/Frame    Avg/Call Avg-NoChild        Peak",
        "  ---------------------------------------------------- ----------- ----------- ----------- ------ ----------- ------ ----------- ----------- ----------- -----------",
        "                                        CEngine::Frame           1       1.000     150.478  99.98       0.197   0.13     150.478     150.478       0.197     150.478",
        "                  CLuaInterface::CallFunctionProtected           7       7.000     150.102  99.73     150.100  99.73     150.102      21.443      21.443     150.002",
        "",
        "-- Profile scopes sorted by time (without children) --",
        "  Scope                                                      Calls Calls/Frame  Time+Child    Pct        Time    Pct   Avg/Frame    Avg/Call Avg-NoChild        Peak",
        "  ---------------------------------------------------- ----------- ----------- ----------- ------ ----------- ------ ----------- ----------- ----------- -----------",
        "                  CLuaInterface::CallFunctionProtected           7       7.000     150.102  99.73     150.100  99.73     150.102      21.443      21.443     150.002",
        "                                        CEngine::Frame           1       1.000     150.478  99.98       0.197   0.13     150.478     150.478       0.197     150.478",
        "                                 _Host_RunFrame_Server           3       3.000       0.275   0.18       0.145   0.10       0.275       0.092       0.048       0.275",
        "                                             PhysFrame           3       3.000       0.009   0.01       0.009   0.01       0.009       0.003       0.003       0.009",
        "",
        "-- Profile scopes sorted by average time (including children) --",
        "                                        CEngine::Frame           1       1.000     150.478  99.98       0.197   0.13     150.478     150.478       0.197     150.478",
        "",
        "******** END VPROF REPORT ********",
    ];

    [Fact]
    public void Parses_the_scopes_of_a_long_frame()
    {
        var r = VprofReport.Parse(Report);
        Assert.NotNull(r);
        Assert.Equal(1, r!.Frames);
        Assert.Equal(150.51, r.FrameMs, 2);
        Assert.Equal(4, r.Scopes.Count);
        var top = r.Scopes[0];
        Assert.Equal("CLuaInterface::CallFunctionProtected", top.Name);
        Assert.Equal(150.100, top.ExclusiveMs, 3);
        Assert.Equal(150.102, top.InclusiveMs, 3);
        Assert.Equal(7, top.Calls);
        Assert.Equal("Lua code (hooks, timers, net messages)", VprofReport.Describe(top.Name));
        Assert.Null(VprofReport.Parse(["some", "other", "text"]));
    }

    [Fact]
    public void A_report_is_taken_out_of_the_console_only_while_capturing()
    {
        var events = new List<ConsoleEvent>();
        var now = new DateTime(2026, 10, 6, 12, 0, 0);
        var c = new OutputClassifier(events.Add, new PendingCommands(), () => now);
        long id = 0;
        void Feed(IEnumerable<string> lines)
        {
            foreach (var l in lines)
            {
                c.Add(++id, new ConsoleLine { Text = l, Time = now }, false);
                now = now.AddMilliseconds(1);
            }
        }

        // Typed by the user (not capturing): it stays in the console.
        Feed(Report);
        Assert.Contains(VprofReport.Begin, events.OfType<LineAdded>().Select(e => e.Line.Text));
        Assert.Empty(events.OfType<VprofCaptured>());

        events.Clear();
        c.CaptureVprof = true;
        Feed(["before"]);
        Feed(Report);
        Feed(["Saved report to \"vprof/vprof7.txt\"", "after"]);
        now = now.AddSeconds(1);
        c.Tick();
        Assert.Equal(["before", "after"], events.OfType<LineAdded>().Select(e => e.Line.Text).ToList());
        var cap = Assert.Single(events.OfType<VprofCaptured>());
        Assert.Equal("vprof/vprof7.txt", cap.SavedTo);
        Assert.Equal(150.51, cap.Report!.FrameMs, 2);
    }

    private sealed class Feeder
    {
        public readonly List<ConsoleEvent> Events = new();
        public DateTime Now = new(2026, 10, 6, 12, 0, 0);
        public readonly OutputClassifier C;
        private long _id;

        public Feeder() => C = new OutputClassifier(Events.Add, new PendingCommands(), () => Now) { CaptureVprof = true };

        public void Feed(params string[] lines)
        {
            foreach (var l in lines)
            {
                C.Add(++_id, new ConsoleLine { Text = l, Time = Now }, false);
                Now = Now.AddMilliseconds(1);
            }
        }

        public List<string> Shown() => Events.OfType<LineAdded>().Select(e => e.Line.Text).ToList();
    }

    [Fact]
    public void A_report_right_after_an_error_of_the_frame_is_captured()
    {
        var f = new Feeder();
        // The hook that made the frame long also failed: its error is still being put together.
        f.Feed("[ERROR] addons/demo/lua/autorun/server/x.lua:5: attempt to call a nil value",
            "  1. fn - addons/demo/lua/autorun/server/x.lua:5",
            "   2. unknown - lua/includes/modules/hook.lua:96",
            "");
        f.Feed(Report);
        f.Feed("Saved report to \"vprof/vprof8.txt\"", "after");
        f.Now = f.Now.AddSeconds(1);
        f.C.Tick();
        Assert.Single(f.Events.OfType<ErrorRecognized>());
        Assert.Equal("vprof/vprof8.txt", Assert.Single(f.Events.OfType<VprofCaptured>()).SavedTo);
        Assert.DoesNotContain(VprofReport.Begin, f.Shown());
        Assert.Contains("after", f.Shown());
    }

    [Fact]
    public void A_report_of_many_frames_stays_in_the_console()
    {
        // vprof_generate_report while the capture is on: the user's own report.
        var f = new Feeder();
        f.Feed(Report.Select(l => l.StartsWith("1 frames sampled") ? "330 frames sampled for 10.00 seconds" : l).ToArray());
        f.Feed("Saved report to \"vprof/vprof9.txt\"");
        f.Now = f.Now.AddSeconds(1);
        f.C.Tick();
        Assert.Empty(f.Events.OfType<VprofCaptured>());
        var shown = f.Shown();
        Assert.Equal(VprofReport.Begin, shown[0]);
        Assert.Equal("-- Summary --", shown[1]);
        Assert.Contains("330 frames sampled for 10.00 seconds", shown);
        Assert.Contains(VprofReport.End, shown);
        Assert.Contains("Saved report to \"vprof/vprof9.txt\"", shown);
    }

    [Fact]
    public void A_report_of_a_short_frame_is_someone_elses()
    {
        // vprof_generate_report through rcon while the capture is on: one frame, but not a long one.
        var f = new Feeder();
        f.C.VprofMinFrameMs = 90.9;
        f.Feed(Report.Select(l => l.StartsWith("Average") ? "Average 30.00 fps, 33.33 ms per frame" : l).ToArray());
        f.Feed("Saved report to \"vprof/vprof3.txt\"");
        f.Now = f.Now.AddSeconds(1);
        f.C.Tick();
        Assert.Empty(f.Events.OfType<VprofCaptured>());
        var shown = f.Shown();
        Assert.Equal([VprofReport.Begin, "-- Summary --", "1 frames sampled for 0.15 seconds", "Average 30.00 fps, 33.33 ms per frame"], shown.Take(4).ToArray());
        Assert.Contains("Saved report to \"vprof/vprof3.txt\"", shown);
    }

    [Fact]
    public void A_report_goes_to_its_own_frame_in_a_burst()
    {
        var t = new DateTime(2026, 10, 6, 12, 0, 0);
        // Long frames back to back at 66 tick, 60 ms each.
        var rows = new List<(DateTime, double, bool)> { (t, 60.0, true), (t.AddMilliseconds(60), 60.0, true), (t.AddMilliseconds(120), 60.0, true) };
        // The middle frame's report, the console 35 ms late (usually 20): the next frame is nearer in time, the middle one fits.
        Assert.Equal(1, VprofMatch.Pick(rows, t.AddMilliseconds(95), 55, 15.15, 0.02));
        // About as near to two frames: none rather than the wrong one.
        Assert.Equal(-1, VprofMatch.Pick(rows.Take(2).ToList(), t.AddMilliseconds(50), 55, 15.15, 0.02));
        // Longer than any of them: not theirs.
        Assert.Equal(-1, VprofMatch.Pick(rows, t.AddMilliseconds(80), 120, 15.15, 0.02));
        // An older addon (the whole second only).
        Assert.Equal(0, VprofMatch.Pick([(t, 150.0, false)], t.AddSeconds(1.5), 120, 30, 0.02));
    }

    [Fact]
    public void A_report_without_its_end_does_not_hide_the_console()
    {
        var f = new Feeder();
        f.Feed(Report[..5]);
        f.Now = f.Now.AddSeconds(3);
        f.Feed("hello");
        Assert.Equal(["hello"], f.Shown());
    }
}
