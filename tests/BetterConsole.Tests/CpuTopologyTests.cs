using System.Buffers.Binary;
using BetterConsole.Core.Server;
using Xunit;

namespace BetterConsole.Tests;

public class CpuTopologyTests
{
    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX entries as GetLogicalProcessorInformationEx returns them.

    private static byte[] CoreEntry(ulong mask, int efficiencyClass, int group = 0)
    {
        var b = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(b, 0);           // RelationProcessorCore
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 48);
        b[8] = 1;                                                 // Flags: SMT
        b[9] = (byte)efficiencyClass;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(30), 1); // GroupCount
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), mask);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(40), (ushort)group);
        return b;
    }

    // groupCount 0: Windows 10 (the field was still reserved), one mask.
    private static byte[] CacheEntry(int level, int type, long size, ulong mask, int groupCount = 1)
    {
        var b = new byte[56];
        BinaryPrimitives.WriteInt32LittleEndian(b, 2);           // RelationCache
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 56);
        b[8] = (byte)level;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)size);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), type);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(38), (ushort)groupCount);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40), mask);
        return b;
    }

    private static byte[] Other()
    {
        var b = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(b, 1);           // RelationNumaNode
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 40);
        return b;
    }

    private const long MB = 1 << 20;

    [Fact]
    public void Hybrid_cores_get_their_kind()
    {
        // 2 performance cores with Hyper-Threading, 2 efficiency cores, a core of processor group 1.
        var buf = new[] { CoreEntry(0b11, 1), Other(), CoreEntry(0b1100, 1), CoreEntry(0b10000, 0), CoreEntry(0b100000, 0), CoreEntry(0b1, 1, group: 1) }
            .SelectMany(x => x).ToArray();
        var cores = ProcessTuning.ParseCores(buf);
        Assert.Equal(4, cores.Count);
        var cpu = ProcessTuning.Build("Test", cores, [], 0b111111);
        Assert.True(cpu.IsHybrid);
        Assert.Equal(["P", "P", "E", "E"], cpu.Cores.Select(c => cpu.Kind(c) ?? "-").ToArray());
        Assert.Equal([0, 1], cpu.Cores[0].Processors);
        Assert.Equal(6, cpu.Threads);
    }

    [Fact]
    public void A_third_class_is_low_power()
    {
        var cpu = ProcessTuning.Build("Test", [(0b1, 2), (0b10, 1), (0b100, 0)], [], 0b111);
        Assert.Equal(["P", "E", "LP-E"], cpu.Cores.Select(c => cpu.Kind(c) ?? "-").ToArray());
    }

    [Fact]
    public void Alike_cores_have_no_kind()
    {
        var cpu = ProcessTuning.Build("Test", [(0b11, 0), (0b1100, 0)], [], 0b1111);
        Assert.False(cpu.IsHybrid);
        Assert.Null(cpu.Kind(cpu.Cores[0]));
    }

    [Fact]
    public void Cores_are_grouped_by_their_L3()
    {
        // Two CCDs of two cores each; L1 and L2 per core are not what groups them.
        var entries = new List<byte[]>();
        for (int i = 0; i < 4; i++)
        {
            ulong m = 3UL << (2 * i);
            entries.Add(CoreEntry(m, 0));
            entries.Add(CacheEntry(1, 2, 32 * 1024, m));
            entries.Add(CacheEntry(1, 1, 32 * 1024, m));
            entries.Add(CacheEntry(2, 0, 1 * MB, m));
        }
        entries.Add(CacheEntry(3, 0, 96 * MB, 0x0F, groupCount: 0));
        entries.Add(CacheEntry(3, 0, 32 * MB, 0xF0, groupCount: 0));
        var buf = entries.SelectMany(x => x).ToArray();

        var cpu = ProcessTuning.Build("Test", ProcessTuning.ParseCores(buf), ProcessTuning.ParseCaches(buf), 0xFF);
        Assert.Equal(2, cpu.Caches.Count);
        Assert.Equal("96 MB", cpu.Caches[0].SizeText);
        Assert.Equal("32 MB", cpu.Caches[1].SizeText);
        Assert.Equal([0, 0, 1, 1], cpu.Cores.Select(c => c.Cache).ToArray());
    }

    [Theory]
    [InlineData("QEMU", "Standard PC (Q35 + ICH9, 2009)", "QEMU/KVM")]
    [InlineData("Microsoft Corporation", "Virtual Machine", "Hyper-V")]
    [InlineData("VMware, Inc.", "VMware7,1", "VMware")]
    [InlineData("Hetzner", "vServer", "Hetzner Cloud")]
    [InlineData("Xen", "HVM domU", "Xen")]
    [InlineData("Alibaba Cloud", "Alibaba Cloud ECS", "Alibaba Cloud")]
    [InlineData("Xenon Systems", "Workstation", null)]
    [InlineData("Amazon EC2", "m5.metal", null)]
    [InlineData("ASUS", "System Product Name", null)]
    [InlineData("Microsoft Corporation", "Surface Pro 9", null)]
    [InlineData("Dell Inc.", "PowerEdge R740", null)]
    [InlineData(null, null, null)]
    public void Virtual_machines_are_known_by_their_firmware(string? maker, string? model, string? expected) =>
        Assert.Equal(expected, ProcessTuning.VmFromSmbios(maker, model));

    [Fact]
    public void Nothing_reported_falls_back_to_a_core_per_processor()
    {
        var cpu = ProcessTuning.Build("", [], [], 0b1011);
        Assert.Equal([0, 1, 3], cpu.Cores.Select(c => c.Processors[0]).ToArray());
        Assert.Empty(cpu.Caches);
    }

    [Fact]
    public void A_truncated_buffer_is_not_read_past_its_end()
    {
        var buf = CoreEntry(0b11, 0).Concat(CoreEntry(0b1100, 0)).ToArray()[..60];
        Assert.Single(ProcessTuning.ParseCores(buf));
        Assert.NotNull(ProcessTuning.Topology());
    }
}
