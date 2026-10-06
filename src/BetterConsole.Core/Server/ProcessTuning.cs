using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace BetterConsole.Core.Server;

/// <summary>
/// One physical core and its logical processors (two with SMT / Hyper-Threading). On hybrid CPUs the
/// performance cores have the higher efficiency class; <paramref name="Cache"/> is the index of its
/// last-level cache in <see cref="CpuTopology.Caches"/>.
/// </summary>
public sealed record CpuCore(int Index, int[] Processors, int EfficiencyClass, int Cache = 0);

/// <summary>A last-level cache (L3) and the processors sharing it: the whole chip, or one CCX of a Ryzen.</summary>
public sealed record CpuCache(int Index, int Level, long SizeBytes, ulong Mask)
{
    public string SizeText => SizeBytes >= 1 << 20 ? $"{SizeBytes / (1 << 20)} MB" : $"{SizeBytes / 1024} KB";
}

/// <summary>The processor of this machine.</summary>
public sealed record CpuTopology(string Name, IReadOnlyList<CpuCore> Cores, IReadOnlyList<CpuCache> Caches)
{
    private readonly int[] _classes = Cores.Select(c => c.EfficiencyClass).Distinct().OrderDescending().ToArray();

    /// <summary>Performance and efficiency cores (Intel since the 12th generation, some AMD mobile chips).</summary>
    public bool IsHybrid => _classes.Length > 1;

    /// <summary>"P", "E" or (a third, lowest class) "LP-E"; null when all cores are alike.</summary>
    public string? Kind(CpuCore core) =>
        !IsHybrid ? null
        : core.EfficiencyClass == _classes[0] ? "P"
        : _classes.Length > 2 && core.EfficiencyClass == _classes[^1] ? "LP-E"
        : "E";

    public int Threads => Cores.Sum(c => c.Processors.Length);

    /// <summary>The hypervisor when this is a virtual machine (a VPS): see <see cref="ProcessTuning.Hypervisor"/>.</summary>
    public string? Hypervisor { get; init; }
}

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

    /// <summary>The cores of this machine (see <see cref="Topology"/>).</summary>
    public static IReadOnlyList<CpuCore> Cores() => Topology().Cores;

    /// <summary>
    /// The processor of this machine: its name, its cores with their logical processors and efficiency
    /// class, and its last-level caches. Falls back to one processor per core.
    /// </summary>
    public static CpuTopology Topology()
    {
        List<(ulong Mask, int EfficiencyClass)> cores = [];
        List<(int Level, int Type, long Size, ulong Mask)> caches = [];
        try
        {
            cores = ParseCores(ProcessorInformation(RelationProcessorCore));
            caches = ParseCaches(ProcessorInformation(RelationCache));
        }
        catch
        {
            cores.Clear();
            caches.Clear();
        }
        return Build(CpuName(), cores, caches, AllProcessors) with { Hypervisor = Hypervisor() };
    }

    /// <summary>The topology from what Windows reported (processor group 0 only, within <paramref name="all"/>).</summary>
    public static CpuTopology Build(string name, IReadOnlyList<(ulong Mask, int EfficiencyClass)> cores,
        IReadOnlyList<(int Level, int Type, long Size, ulong Mask)> caches, ulong all)
    {
        // The last level: L3 (the caches every core of a CCX / of the chip shares), else whatever is the last.
        var unified = caches.Where(c => c.Type is 0 && (c.Mask & all) != 0).ToList();
        int last = unified.Count > 0 ? unified.Max(c => c.Level) : 0;
        var llc = unified.Where(c => c.Level == last).GroupBy(c => c.Mask & all).Select(g => g.First())
            .OrderBy(c => System.Numerics.BitOperations.TrailingZeroCount(c.Mask & all))
            .Select((c, i) => new CpuCache(i, c.Level, c.Size, c.Mask & all)).ToList();

        var list = new List<CpuCore>();
        foreach (var (mask, eff) in cores)
        {
            var cpus = Enumerable.Range(0, 64).Where(i => (mask & (1UL << i) & all) != 0).ToArray();
            if (cpus.Length == 0) continue;
            int cache = llc.FindIndex(c => (c.Mask & (1UL << cpus[0])) != 0);
            list.Add(new CpuCore(0, cpus, eff, Math.Max(cache, 0)));
        }
        if (list.Count == 0)
            for (int i = 0; i < 64; i++)
                if ((all & (1UL << i)) != 0) list.Add(new CpuCore(0, [i], 0));
        var ordered = list.OrderBy(c => c.Processors[0]).Select((c, i) => c with { Index = i }).ToList();
        return new CpuTopology(name, ordered, llc);
    }

    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship (4), Size (4), then the relationship at 8.

    /// <summary>
    /// The cores in what GetLogicalProcessorInformationEx(RelationProcessorCore) returned: PROCESSOR_RELATIONSHIP
    /// is Flags (1), EfficiencyClass (1), Reserved (20), GroupCount (2), GROUP_AFFINITY (16 each: Mask 8, Group 2, Reserved 6).
    /// </summary>
    public static List<(ulong Mask, int EfficiencyClass)> ParseCores(byte[] buf)
    {
        var list = new List<(ulong, int)>();
        foreach (var (at, end) in Entries(buf, RelationProcessorCore))
        {
            int eff = buf[at + 9];
            int groups = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(at + 30));
            for (int g = 0; g < groups && at + 32 + g * 16 + 10 <= end; g++)
            {
                int o = at + 32 + g * 16;
                if (BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o + 8)) != 0) continue;
                list.Add((BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(o)), eff));
            }
        }
        return list;
    }

    /// <summary>
    /// The caches in what GetLogicalProcessorInformationEx(RelationCache) returned: CACHE_RELATIONSHIP is Level (1),
    /// Associativity (1), LineSize (2), CacheSize (4), Type (4), Reserved (18), GroupCount (2; 0 before Windows 11,
    /// meaning one), GROUP_AFFINITY at 32.
    /// </summary>
    public static List<(int Level, int Type, long Size, ulong Mask)> ParseCaches(byte[] buf)
    {
        var list = new List<(int, int, long, ulong)>();
        foreach (var (at, end) in Entries(buf, RelationCache))
        {
            int level = buf[at + 8];
            long size = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(at + 12));
            int type = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(at + 16));
            int groups = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(at + 38)));
            for (int g = 0; g < groups && at + 40 + g * 16 + 10 <= end; g++)
            {
                int o = at + 40 + g * 16;
                if (BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o + 8)) != 0) continue;
                list.Add((level, type, size, BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(o))));
            }
        }
        return list;
    }

    // Each entry of that relationship: where it starts and ends (its groups never read into the next one).
    private static IEnumerable<(int At, int End)> Entries(byte[] buf, int relationship)
    {
        int off = 0;
        while (off + 8 <= buf.Length)
        {
            int rel = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off));
            int size = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(off + 4));
            if (size < 8 || off + size > buf.Length) yield break;
            if (rel == relationship && size >= 40) yield return (off, off + size);
            off += size;
        }
    }

    private static byte[] ProcessorInformation(int relationship)
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref len);
        if (len == 0) return [];
        var mem = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationship, mem, ref len)) return [];
            var buf = new byte[len];
            Marshal.Copy(mem, buf, 0, (int)len);
            return buf;
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    /// <summary>
    /// The hypervisor BetterConsole runs under ("KVM", "Hyper-V", "VMware" …: a VPS or a virtual machine), or null
    /// on real hardware. Windows with virtualization-based security runs on Hyper-V too, but as its root
    /// partition (the one allowed to create partitions): that is real hardware — unless the firmware says
    /// otherwise: a VPS whose Windows runs Hyper-V itself (nested) is that root as well, and some hypervisors
    /// hide from CPUID.
    /// </summary>
    public static string? Hypervisor() => CpuidHypervisor() ?? SmbiosHypervisor();

    private static string? SmbiosHypervisor()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return VmFromSmbios(key?.GetValue("SystemManufacturer") as string, key?.GetValue("SystemProductName") as string);
        }
        catch
        {
            return null;
        }
    }

    // Makers and models only virtual machines report, as whole words ("Xen", not "Xenon"). Not "Amazon EC2": AWS
    // bare-metal instances report it too (its virtual machines show in CPUID anyway).
    private static readonly (string Needle, string Name)[] VmFirmware =
    [
        ("QEMU", "QEMU/KVM"), ("Standard PC (", "QEMU/KVM"), ("KVM", "KVM"), ("VMware", "VMware"), ("VirtualBox", "VirtualBox"),
        ("innotek", "VirtualBox"), ("Xen", "Xen"), ("HVM domU", "Xen"), ("Bochs", "Bochs"), ("Parallels", "Parallels"),
        ("BHYVE", "bhyve"), ("Google Compute Engine", "Google Cloud"), ("OpenStack", "OpenStack"), ("DigitalOcean", "DigitalOcean"),
        ("Droplet", "DigitalOcean"), ("Hetzner", "Hetzner Cloud"), ("Alibaba Cloud ECS", "Alibaba Cloud"), ("Tencent Cloud", "Tencent Cloud"),
        ("Nutanix AHV", "Nutanix AHV"), ("Scaleway", "Scaleway"), ("Vultr", "Vultr"), ("Virtual Machine", "Hyper-V"),
    ];

    /// <summary>A virtual machine by the maker and model its firmware reports (SMBIOS); null for real hardware.</summary>
    public static string? VmFromSmbios(string? manufacturer, string? product)
    {
        var s = $" {manufacturer} {product} ";
        foreach (var (needle, name) in VmFirmware)
        {
            int at = s.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            while (at >= 0)
            {
                int end = at + needle.Length;
                bool whole = !char.IsLetterOrDigit(s[at - 1]) && (!char.IsLetterOrDigit(needle[^1]) || !char.IsLetterOrDigit(s[end]));
                if (whole) return name;
                at = s.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase);
            }
        }
        return null;
    }

    private static string? CpuidHypervisor()
    {
        try
        {
            if (!X86Base.IsSupported || (X86Base.CpuId(1, 0).Ecx & (1 << 31)) == 0) return null;
            var (max, id) = HypervisorLeaf(0x40000000);
            if (id == "Microsoft Hv")
            {
                if (max >= 0x40000003 && (X86Base.CpuId(0x40000003, 0).Ebx & 1) != 0) return null;
                // KVM and others also speak Hyper-V's interface to Windows guests, under their own name further up.
                var (ownMax, own) = HypervisorLeaf(0x40000100);
                if (ownMax is >= 0x40000100 and < 0x40000200 && own.Length > 0 && own != "Microsoft Hv") id = own;
            }
            return id switch
            {
                "KVMKVMKVM" => "KVM",
                "Microsoft Hv" => "Hyper-V",
                "VMwareVMware" => "VMware",
                "XenVMMXenVMM" => "Xen",
                "VBoxVBoxVBox" => "VirtualBox",
                "TCGTCGTCGTCG" => "QEMU",
                " lrpepyh  vr" => "Parallels",
                "bhyve bhyve " => "bhyve",
                _ => "a hypervisor",
            };
        }
        catch
        {
            return null;
        }
    }

    private static (long Max, string Id) HypervisorLeaf(int leaf)
    {
        var (max, b, c, d) = X86Base.CpuId(leaf, 0);
        var bytes = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, b);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), c);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), d);
        var id = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        return ((uint)max, id.All(ch => ch >= ' ' && ch < 127) ? id : "");
    }

    private static string CpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
}
