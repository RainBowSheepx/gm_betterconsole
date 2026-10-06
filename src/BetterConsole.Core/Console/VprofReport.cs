using System.Globalization;
using System.Text.RegularExpressions;

namespace BetterConsole.Core.Console;

/// <summary>A scope of the engine's profiler (VProf): its time in ms per frame, with and without what it called.</summary>
public sealed record VprofScope(string Name, double Calls, double InclusiveMs, double ExclusiveMs);

/// <summary>
/// One report of the engine profiler, as <c>vprof_dump_spikes</c> prints it for a long frame
/// ("******** BEGIN VPROF REPORT ********" … "******** END VPROF REPORT ********"): the frame time and
/// the scopes by their own time.
/// </summary>
public sealed partial record VprofReport(int Frames, double FrameMs, IReadOnlyList<VprofScope> Scopes)
{
    public const string Begin = "******** BEGIN VPROF REPORT ********";
    public const string End = "******** END VPROF REPORT ********";

    [GeneratedRegex(@"^\s*(?<n>\d+) frames sampled")]
    private static partial Regex FramesLine();

    [GeneratedRegex(@"(?<ms>[\d.]+) ms per frame")]
    private static partial Regex PerFrame();

    // "  CLuaInterface::CallFunctionProtected   7   7.000   150.102  99.73   150.100  99.73   150.102  21.443  21.443  150.002"
    // name, calls, calls/frame, time+child, pct, time, pct, avg/frame, avg/call, avg-nochild, peak
    [GeneratedRegex(@"^\s*(?<name>\S.*?)\s+(?<calls>\d+)\s+[\d.]+\s+(?<incl>[\d.]+)\s+[\d.]+\s+(?<excl>[\d.]+)\s+[\d.]+\s+[\d.]+\s+[\d.]+\s+[\d.]+\s+[\d.]+\s*$")]
    private static partial Regex Row();

    /// <summary>The lines between the markers (the markers themselves may be included). Null when it is not a report.</summary>
    public static VprofReport? Parse(IEnumerable<string> lines)
    {
        int frames = 0;
        double frameMs = double.NaN;
        var scopes = new List<VprofScope>();
        bool inTable = false, done = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (done) break;
            if (FramesLine().Match(line) is { Success: true } f) frames = int.Parse(f.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (double.IsNaN(frameMs) && PerFrame().Match(line) is { Success: true } pf) frameMs = Num(pf.Groups["ms"].Value);
            if (line.StartsWith("-- Profile scopes sorted by time (without children)", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }
            if (!inTable) continue;
            if (line.Length == 0)
            {
                // The table ends with an empty line (after its header and the dashes).
                if (scopes.Count > 0) done = true;
                continue;
            }
            if (line.TrimStart().StartsWith("--", StringComparison.Ordinal) || line.TrimStart().StartsWith("Scope", StringComparison.Ordinal)) continue;
            var m = Row().Match(line);
            if (!m.Success) continue;
            double perFrame = Math.Max(frames, 1);
            scopes.Add(new VprofScope(m.Groups["name"].Value.Trim(), Num(m.Groups["calls"].Value) / perFrame,
                Num(m.Groups["incl"].Value) / perFrame, Num(m.Groups["excl"].Value) / perFrame));
        }
        if (double.IsNaN(frameMs) || scopes.Count == 0) return null;
        return new VprofReport(Math.Max(frames, 1), frameMs, scopes.OrderByDescending(s => s.ExclusiveMs).ToList());
    }

    private static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>What a scope of the engine stands for, in plain words (null: no idea).</summary>
    public static string? Describe(string scope) => scope switch
    {
        "CLuaInterface::CallFunctionProtected" => "Lua code (hooks, timers, net messages)",
        "CLuaGamemode::Call" or "CLuaGamemode::CallReturns" or "CLuaGamemode::CallWithArgs" => "Lua: gamemode hooks",
        "CBaseEntity::PhysicsDispatchThink" => "entity Think",
        "PhysFrame" or "CPhysicsHook::FrameUpdatePostEntityThink" => "physics",
        "SendClientMessages" => "snapshots to the players",
        "NET_ProcessSocket" or "NET_GetPacket" or "NET_ReceiveDatagram" or "recvfrom" => "network input",
        "CBaseServer::RunFrame" => "server frame",
        "_Host_RunFrame_Server" => "game frame (engine)",
        "CEngine::Frame" => "engine frame",
        "Timer::Cycle" => "engine timers",
        "CBaseServer::UpdateMasterServer" => "master server",
        "CCvar::FindCommandBase" => "console commands",
        _ => null,
    };
}

/// <summary>
/// Which lag spike a report of the engine profiler belongs to. The engine prints its report at the end of the
/// frame, the addon notices the frame at the next Think, up to a tick later, and the console brings the report
/// a moment after it was printed: the report comes about the console's delay after the spike's time (with the
/// addon's precise time; an older addon has the whole second only). The engine's frame is the work only; the
/// addon measures from one Think to the next, which also has the wait for the next tick in it: the report is up
/// to a couple of ticks shorter, never longer. The engine reports one long frame a second at most, so the
/// frames around it have no report of their own and are candidates as well.
/// </summary>
public static class VprofMatch
{
    /// <summary>
    /// The row the report goes to, or -1. <paramref name="rows"/>: spikes without a report yet (when the addon
    /// noticed the frame, its length, whether the time is precise); <paramref name="latency"/>: the console's
    /// usual delay in seconds.
    /// </summary>
    public static int Pick(IReadOnlyList<(DateTime Time, double Ms, bool Precise)> rows, DateTime at, double frameMs, double tickMs, double latency)
    {
        int best = -1;
        double bestCost = double.MaxValue, secondCost = double.MaxValue;
        for (int i = 0; i < rows.Count; i++)
        {
            var (time, ms, precise) = rows[i];
            if (frameMs > ms + 10 || frameMs < ms - Math.Max(2.5 * tickMs, ms * 0.3)) continue;
            double dt = (at - time).TotalSeconds, cost;
            if (precise)
            {
                if (dt < -Math.Max(0.1, 2 * tickMs / 1000) || dt > 0.2) continue;
                cost = Math.Abs(dt - latency);
            }
            else
            {
                if (Math.Abs(dt) > 3) continue;
                cost = Math.Abs(dt);
            }
            if (cost < bestCost) (secondCost, bestCost, best) = (bestCost, cost, i);
            else if (cost < secondCost) secondCost = cost;
        }
        // Two frames about as close (a burst of long frames and a late console): rather none than the wrong one.
        if (best >= 0 && rows[best].Precise && secondCost - bestCost < 0.015) return -1;
        return best;
    }
}
