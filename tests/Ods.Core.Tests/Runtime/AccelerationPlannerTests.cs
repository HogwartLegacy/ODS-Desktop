using Ods.Core.Hardware;
using Ods.Core.Runtime;

namespace Ods.Core.Tests.Runtime;

public sealed class AccelerationPlannerTests
{
    private static RuntimePins Pins => RuntimePins.Load(Path.Combine(Repo.DesktopRoot, "runtime-pins.json"));

    private static HardwareInfo Machine(GpuSummary gpu, NvidiaGpu? nvidia = null, string architecture = "x64", int ramGb = 32) => new(
        "Windows 11", 26100, architecture, "Test CPU", 16, true, false,
        ramGb * 1024L * 1024 * 1024, 8L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024,
        [], nvidia, gpu, []);

    private static HardwareInfo Nvidia(string name, int vramMb, string driver, string compute) =>
        Machine(new GpuSummary("nvidia", name, vramMb, 1, "discrete", driver, compute), new NvidiaGpu(name, vramMb, driver, compute, 1));

    private static string[] Flavors(HardwareInfo hardware) => AccelerationPlanner.Plan(hardware, Pins).Candidates.Select(c => c.Flavor).ToArray();

    [Fact]
    public void Current_nvidia_driver_prefers_cuda_13_then_falls_back()
    {
        Assert.Equal(["win-cuda-13.1-x64", "win-cuda-12.4-x64", "win-vulkan-x64", "win-cpu-x64"],
            Flavors(Nvidia("NVIDIA GeForce RTX 4090", 24564, "581.15", "8.9")));
    }

    [Fact]
    public void Pascal_cards_use_cuda_12_4()
    {
        Assert.Equal(["win-cuda-12.4-x64", "win-vulkan-x64", "win-cpu-x64"],
            Flavors(Nvidia("NVIDIA GeForce GTX 1080", 8192, "566.36", "6.1")));
    }

    [Fact]
    public void Blackwell_on_an_old_driver_explains_and_uses_vulkan()
    {
        var plan = AccelerationPlanner.Plan(Nvidia("NVIDIA GeForce RTX 5090", 32607, "576.02", "12.0"), Pins);

        Assert.Equal(["win-vulkan-x64", "win-cpu-x64"], plan.Candidates.Select(c => c.Flavor));
        Assert.Contains(plan.Notes, note => note.Contains("Updating the driver"));
    }

    [Fact]
    public void Amd_uses_vulkan_like_the_ods_windows_path()
    {
        var plan = AccelerationPlanner.Plan(Machine(new GpuSummary("amd", "AMD Radeon RX 7900 XTX", 24560, 1, "discrete", "32.0", "")), Pins);

        Assert.Equal(["win-vulkan-x64", "win-cpu-x64"], plan.Candidates.Select(c => c.Flavor));
        Assert.Equal("3", plan.Tier);
    }

    [Fact]
    public void No_supported_gpu_runs_on_the_cpu()
    {
        Assert.Equal(["win-cpu-x64"], Flavors(Machine(GpuSummary.None)));
        Assert.Equal(["win-cpu-arm64"], Flavors(Machine(GpuSummary.None, architecture: "arm64")));
    }

    [Fact]
    public void A_cpu_fallback_plans_models_for_system_ram()
    {
        var hardware = Nvidia("NVIDIA GeForce RTX 4090", 24564, "581.15", "8.9");
        var envelope = AccelerationPlanner.EnvelopeFor(Pins.Get("win-cpu-x64"), hardware, "3");

        Assert.Equal("cpu", envelope.Backend);
        Assert.Equal(0, envelope.VramMb);
        Assert.Equal("nvidia", AccelerationPlanner.EnvelopeFor(Pins.Get("win-vulkan-x64"), hardware, "3").Backend);
    }

    [Theory]
    [InlineData("none", "none", 0, 16, "1")]
    [InlineData("none", "none", 0, 6, "0")]
    [InlineData("amd", "unified", 98304, 128, "SH_LARGE")]
    [InlineData("amd", "unified", 49152, 64, "SH_COMPACT")]
    [InlineData("nvidia", "discrete", 97887, 256, "NV_ULTRA")]
    [InlineData("nvidia", "discrete", 12288, 32, "2")]
    [InlineData("nvidia", "discrete", 2048, 8, "0")]
    public void Tier_map_matches_the_windows_installer(string backend, string memory, int vramMb, int ramGb, string tier)
    {
        Assert.Equal(tier, TierMap.TierFor(new GpuSummary(backend, "GPU", vramMb, 1, memory, "", ""), ramGb));
    }
}
