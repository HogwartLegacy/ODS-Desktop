using Ods.Core.Hardware;

namespace Ods.Core.Tests.Hardware;

/// <summary>The heuristics ported from installers/windows/lib/detection.ps1.</summary>
public sealed class GpuClassifierTests
{
    private static GpuAdapter Amd(string name, long dedicatedMb) =>
        new(name, GpuAdapter.AmdVendor, 0x1586, dedicatedMb * 1024 * 1024, 0, "32.0.21025.10016", false);

    [Theory]
    [InlineData("AMD Radeon(TM) 8060S Graphics", true)]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon 780M", true)]
    [InlineData("AMD Radeon RX Vega 11 Graphics", true)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("AMD Radeon PRO W7900", false)]
    public void Integrated_radeon_names_are_recognized(string name, bool integrated)
    {
        Assert.Equal(integrated, GpuClassifier.IsAmdIntegratedName(name));
        Assert.Equal(!integrated, GpuClassifier.IsAmdDiscreteName(name));
    }

    [Fact]
    public void Strix_halo_with_large_ram_is_unified_memory()
    {
        var gpu = GpuClassifier.Summarize([Amd("AMD Radeon(TM) 8060S Graphics", 512)], null, "AMD RYZEN AI MAX+ 395 w/ Radeon 8060S", 128);

        Assert.Equal("amd", gpu.Backend);
        Assert.Equal("unified", gpu.MemoryType);
        Assert.Equal((int)Math.Floor(128 * 0.75 * 1024), gpu.VramMb);
    }

    [Fact]
    public void A_large_bios_carve_out_keeps_an_apu_unified()
    {
        // WMI caps AdapterRAM at 4 GB, which ODS relies on; a 64 GB carve-out must not read as discrete.
        var gpu = GpuClassifier.Summarize([Amd("AMD Radeon(TM) 8060S Graphics", 65536)], null, "AMD RYZEN AI MAX+ 395", 128);

        Assert.Equal("unified", gpu.MemoryType);
    }

    [Fact]
    public void A_small_apu_is_not_unified_below_32_gb()
    {
        var gpu = GpuClassifier.Summarize([Amd("AMD Radeon(TM) Graphics", 512)], null, "AMD Ryzen 7 7840U", 16);

        Assert.Equal("discrete", gpu.MemoryType);
    }

    [Fact]
    public void Hybrid_laptops_pick_the_discrete_radeon()
    {
        var gpu = GpuClassifier.Summarize(
            [Amd("AMD Radeon(TM) 780M", 512), Amd("AMD Radeon RX 7700S", 8176)], null, "AMD Ryzen 9 7940HS", 32, _ => 8192);

        Assert.Equal("AMD Radeon RX 7700S", gpu.Name);
        Assert.Equal(8192, gpu.VramMb);
        Assert.Equal(1, gpu.Count);
    }

    [Fact]
    public void Nvidia_comes_first_and_intel_is_not_accelerated()
    {
        var nvidia = new NvidiaGpu("NVIDIA GeForce RTX 4070", 12282, "581.15", "8.9", 1);
        Assert.Equal("nvidia", GpuClassifier.Summarize([Amd("AMD Radeon(TM) Graphics", 512)], nvidia, "", 32).Backend);

        var intel = new GpuAdapter("Intel(R) UHD Graphics 770", GpuAdapter.IntelVendor, 0x4680, 128L << 20, 0, "32.0.101.7088", false);
        Assert.Equal("none", GpuClassifier.Summarize([intel], null, "Intel Core i7-12700", 32).Backend);
    }

    [Fact]
    public void Nvidia_smi_output_is_parsed_and_the_arm_npu_is_ignored()
    {
        var gpu = GpuClassifier.ParseNvidiaSmi("NVIDIA GeForce RTX 4090, 24564 MiB, 581.15, 8.9\n");
        Assert.Equal(new NvidiaGpu("NVIDIA GeForce RTX 4090", 24564, "581.15", "8.9", 1), gpu);

        var arm = GpuClassifier.ParseNvidiaSmi("NVIDIA NPU, [N/A], 616.41, [N/A]\nNVIDIA RTX Spark, 122880 MiB, 616.41, 12.1\n");
        Assert.Equal("NVIDIA RTX Spark", arm!.Name);
        Assert.Equal(1, arm.Count);
    }
}
