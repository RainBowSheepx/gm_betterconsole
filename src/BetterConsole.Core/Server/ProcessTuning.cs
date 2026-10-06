using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterConsole.Core.Server;

/// <summary>One physical core and its logical processors (two with SMT / Hyper-Threading).</summary>
public sealed record CpuCore(int Index, int[] Processors, int EfficiencyClass);

/// <summary>CPU affinity and priority of a server process, and the processors of this machine.</summary>
public static class ProcessTuning
{
    public static readonly string[] Priorities = ["Idle", "BelowNormal", "Normal", "AboveNormal", "High"];

    /// <summary>
    /// The processors of the machine (one processor group: at most 64). Not Environment.ProcessorCount:
    /// that counts only the processors BetterConsole itself may use, which is less when it was started
    /// with an affinity of its own.
    /// </summary>
    public static ulong AllProcessors => _all ??= SystemMask();
    private static ulong? _all;

    /// <summary>How many processor numbers there are (the highest one + 1).</summary>
    public static int ProcessorCount => 64 - System.Numerics.BitOperations.LeadingZeroCount(AllProcessors);

    private static ulong SystemMask()
    {
        try
        {
            if (GetProcessAffinityMask(GetCurrentProcess(), out _, out var system) && system != UIntPtr.Zero) return (ulong)system;
        }
        catch { }
        int n = Math.Min(Environment.ProcessorCount, 64);
        return n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);

    /// <summary>0 or every processor: no restriction.</summary>
    public static bool IsAll(ulong mask) => mask == 0 || (mask & AllProcessors) == AllProcessors;

    public static int Count(ulong mask) => IsAll(mask) ? ProcessorCount : System.Numerics.BitOperations.PopCount(mask & AllProcessors);

    /// <summary>"all 16", "0-3, 8-11".</summary>
    public static string Describe(ulong mask)
    {
        if (IsAll(mask)) return $"all {ProcessorCount}";
        var parts = new List<string>();
        int i = 0;
        while (i < ProcessorCount)
        {
            if ((mask & (1UL << i)) == 0) { i++; continue; }
            int start = i;
            while (i + 1 < ProcessorCount && (mask & (1UL << (i + 1))) != 0) i++;
            parts.Add(start == i ? $"{start}" : $"{start}-{i}");
            i++;
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    public static string PriorityText(string priority) => priority switch
    {
        "BelowNormal" => "Below normal",
        "AboveNormal" => "Above normal",
        _ => priority,
    };

    /// <summary>Sets affinity and priority of a running process. Returns the problem, or null.</summary>
    public static string? Apply(int processId, ulong mask, string priority)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            var want = IsAll(mask) ? AllProcessors : mask & AllProcessors;
            if (want == 0) want = AllProcessors;
            if ((ulong)p.ProcessorAffinity.ToInt64() != want) p.ProcessorAffinity = (IntPtr)(long)want;
            var cls = priority switch
            {
                "Idle" => ProcessPriorityClass.Idle,
                "BelowNormal" => ProcessPriorityClass.BelowNormal,
                "AboveNormal" => ProcessPriorityClass.AboveNormal,
                "High" => ProcessPriorityClass.High,
                _ => ProcessPriorityClass.Normal,
            };
            if (p.PriorityClass != cls) p.PriorityClass = cls;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The cores of this machine with their logical processors and efficiency class (on hybrid CPUs the
    /// performance cores have the higher class). Falls back to one processor per core.
    /// </summary>
    public static IReadOnlyList<CpuCore> Cores()
    {
        var list = new List<CpuCore>();
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len > 0)
            {
                var buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len))
                    {
                        long off = 0;
                        while (off < len)
                        {
                            var p = buf + (nint)off;
                            int size = Marshal.ReadInt32(p, 4);
                            if (size <= 0) break;
                            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship, Size, then PROCESSOR_RELATIONSHIP:
                            // Flags (1), EfficiencyClass (1), Reserved (20), GroupCount (2), GROUP_AFFINITY[] at 32.
                            int eff = Marshal.ReadByte(p, 9);
                            int groups = Marshal.ReadInt16(p, 30);
                            for (int g = 0; g < groups; g++)
                            {
                                ulong m = (ulong)Marshal.ReadInt64(p, 32 + g * 16);
                                int group = Marshal.ReadInt16(p, 32 + g * 16 + 8);
                                if (group != 0) continue;
                                var cpus = Enumerable.Range(0, 64).Where(i => (m & (1UL << i) & AllProcessors) != 0).ToArray();
                                if (cpus.Length > 0) list.Add(new CpuCore(list.Count, cpus, eff));
                            }
                            off += size;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        catch
        {
            list.Clear();
        }
        if (list.Count == 0)
            for (int i = 0; i < ProcessorCount; i++)
                if ((AllProcessors & (1UL << i)) != 0) list.Add(new CpuCore(i, [i], 0));
        return list.OrderBy(c => c.Processors[0]).Select((c, i) => c with { Index = i }).ToList();
    }

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
}
