using System.Globalization;
using Ods.Core.Hardware;

namespace Ods.Core.Runtime;

/// <summary>
/// How ODS will run models on this PC: the llama.cpp builds to try, in order,
/// and the hardware envelope the model selector sees for each of them.
/// </summary>
public sealed record AccelerationPlan(
    IReadOnlyList<RuntimeFlavor> Candidates,
    string HostArch,
    string Tier,
    IReadOnlyList<string> Notes)
{
    public RuntimeFlavor Preferred => Candidates[0];
}

/// <summary>The hardware envelope scripts/select-model.py would be called with.</summary>
public sealed record SelectorEnvelope(string Backend, string MemoryType, double VramMb, double RamGb, string HostArch, string Tier);

/// <summary>
/// Maps detected hardware to pinned llama.cpp builds. NVIDIA prefers CUDA
/// (13.1 for Turing+ on R580+ drivers, else 12.4), AMD uses Vulkan as ODS's
/// Windows path does, and everything ends with the CPU build. A GPU flavor
/// that fails qualification moves to the next one with an explanation;
/// ODS never falls back to the CPU silently.
/// </summary>
public static class AccelerationPlanner
{
    public static AccelerationPlan Plan(HardwareInfo hardware, RuntimePins pins)
    {
        var notes = new List<string>(hardware.ProbeNotes);
        var candidates = new List<RuntimeFlavor>();
        var arch = hardware.Architecture;
        var tier = TierMap.TierFor(hardware.Gpu, hardware.SystemRamGb);

        if (hardware.Gpu.Backend == "nvidia" && hardware.Nvidia is NvidiaGpu nvidia)
        {
            var driver = ParseVersion(nvidia.DriverVersion);
            var compute = ParseVersion(nvidia.ComputeCapability);
            candidates.AddRange(pins.Flavors.Where(f => f.Backend == "cuda" && f.Architecture == arch && CudaEligible(f, driver, compute)));
            if (candidates.Count == 0 && arch == "x64")
            {
                var newest = pins.Flavors.Where(f => f.Backend == "cuda" && f.Architecture == arch).Max(f => f.MinDriverVersion);
                notes.Add($"No ODS CUDA build supports NVIDIA driver {nvidia.DriverVersion} with compute capability {nvidia.ComputeCapability}. "
                    + $"Updating the driver (to {newest} or newer) from nvidia.com enables the fastest path; ODS uses Vulkan meanwhile.");
            }
            if (arch == "x64") candidates.Add(pins.Get("win-vulkan-x64"));
        }
        else if (hardware.Gpu.Backend == "amd" && arch == "x64")
        {
            candidates.Add(pins.Get("win-vulkan-x64"));
        }

        candidates.Add(pins.Get(arch == "arm64" ? "win-cpu-arm64" : "win-cpu-x64"));
        return new AccelerationPlan(candidates, arch == "arm64" ? "arm64" : "amd64", tier, notes);
    }

    /// <summary>The selector envelope for a qualified flavor: GPU flavors keep the GPU, the CPU build uses system RAM.</summary>
    public static SelectorEnvelope EnvelopeFor(RuntimeFlavor flavor, HardwareInfo hardware, string tier)
    {
        var hostArch = hardware.IsArm64 ? "arm64" : "amd64";
        if (!flavor.IsGpu || hardware.Gpu.Backend == "none")
        {
            var cpuTier = hardware.SystemRamGb < 8 ? "0" : "1";
            return new SelectorEnvelope("cpu", "none", 0, hardware.SystemRamGb, hostArch, cpuTier);
        }
        return new SelectorEnvelope(hardware.Gpu.Backend, hardware.Gpu.MemoryType, hardware.Gpu.VramMb, hardware.SystemRamGb, hostArch, tier);
    }

    internal static bool CudaEligible(RuntimeFlavor flavor, Version? driver, Version? compute)
    {
        if (driver is null || compute is null) return false;
        if (flavor.MinComputeCapability is Version min && compute < min) return false;
        if (flavor.MaxComputeCapability is Version max && compute > max) return false;
        return flavor.MinDriverVersion is not Version minDriver || driver >= minDriver;
    }

    internal static Version? ParseVersion(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        if (!trimmed.Contains('.')) trimmed += ".0";
        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    internal static string Gib(long bytes) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture);
}
