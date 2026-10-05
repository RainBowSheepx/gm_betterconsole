using System.Diagnostics;

namespace BetterConsole.Core.Server;

/// <summary>
/// CPU and memory of the srcds process. The CPU figure uses the formula of the engine's "stats"
/// command (CBaseServer::CalculateCPUUsage): process CPU time over wall time across ~5 seconds,
/// where 100 % is one core.
/// </summary>
internal sealed class ProcessSampler
{
    private readonly Queue<(DateTime Wall, long Cpu)> _window = new();

    public double CpuPercent { get; private set; }
    public long PrivateBytes { get; private set; }
    public long WorkingSet { get; private set; }
    public int Threads { get; private set; }
    public int Handles { get; private set; }

    public void Reset()
    {
        _window.Clear();
        CpuPercent = 0;
    }

    public void Sample(int pid, long cpuTime100ns)
    {
        var now = DateTime.UtcNow;
        if (cpuTime100ns >= 0)
        {
            _window.Enqueue((now, cpuTime100ns));
            while (_window.Count > 2 && (now - _window.Peek().Wall).TotalSeconds > 5.5) _window.Dequeue();
            var first = _window.Peek();
            double wall = (now - first.Wall).TotalMilliseconds * 10_000;
            if (wall > 0 && _window.Count > 1) CpuPercent = 100.0 * (cpuTime100ns - first.Cpu) / wall;
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            PrivateBytes = p.PrivateMemorySize64;
            WorkingSet = p.WorkingSet64;
            Threads = p.Threads.Count;
            Handles = p.HandleCount;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}
