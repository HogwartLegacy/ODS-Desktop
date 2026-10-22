using System.Globalization;
using System.Text.RegularExpressions;
using Ods.Core.Hardware;

namespace Ods.Core.Runtime;

/// <summary>One accelerator llama-server --list-devices reports.</summary>
public sealed record LlamaDevice(string Name, string Description, long TotalMb, long FreeMb);

public sealed record QualifiedRuntime(InstalledRuntime Runtime, int Build, IReadOnlyList<LlamaDevice> Devices, string Device)
{
    public RuntimeFlavor Flavor => Runtime.Flavor;

    /// <summary>The --n-gpu-layers value for this route: every layer on a named GPU, none on the CPU build.</summary>
    public string DefaultGpuLayers => Device == "none" ? "0" : "all";
}

/// <summary>A runtime cannot run here; <see cref="Exception.Message"/> says why and what to do.</summary>
public sealed class RuntimeQualificationException(string message) : Exception(message);

/// <summary>
/// Qualifies an installed llama-server.exe the way ODS's Windows runtime
/// does: --version must report the pinned build and --list-devices must show
/// a device of the flavor's backend. The device is always named at launch, so
/// an unusable GPU fails loudly instead of llama.cpp silently using the CPU.
/// </summary>
public static partial class LlamaQualifier
{
    [GeneratedRegex(@"version:\s*(\d+)")]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z]+\d+)\s*:\s*(?<desc>.+?)\s*\((?<total>\d+)\s*MiB,\s*(?<free>\d+)\s*MiB free\)\s*$", RegexOptions.Multiline)]
    private static partial Regex DeviceLine();

    public static async Task<QualifiedRuntime> QualifyAsync(InstalledRuntime runtime, GpuSummary gpu, CancellationToken cancellationToken)
    {
        var flavor = runtime.Flavor;
        var version = await ProcessRunner.RunAsync(runtime.Executable, ["--version"], TimeSpan.FromSeconds(60), cancellationToken);
        if (version.TimedOut) throw new RuntimeQualificationException($"llama.cpp {flavor.DisplayName} did not answer --version within 60 seconds.");
        if (version.ExitCode != 0) throw new RuntimeQualificationException(DescribeExit(flavor, version.ExitCode, version.Output));
        var build = ParseBuild(version.Output);
        if (build != flavor.Build)
        {
            throw new RuntimeQualificationException($"llama.cpp reports build {build?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, not the pinned {flavor.ReleaseTag}; repair the runtime.");
        }

        var listing = await ProcessRunner.RunAsync(runtime.Executable, ["--list-devices"], TimeSpan.FromSeconds(60), cancellationToken);
        if (listing.TimedOut) throw new RuntimeQualificationException($"llama.cpp {flavor.DisplayName} did not list its devices within 60 seconds.");
        if (listing.ExitCode != 0) throw new RuntimeQualificationException(DescribeExit(flavor, listing.ExitCode, listing.Output));
        var devices = ParseDevices(listing.Output);
        if (!flavor.IsGpu) return new QualifiedRuntime(runtime, flavor.Build, devices, "none");

        var device = SelectDevice(devices, flavor.Backend == "cuda" ? "CUDA" : "Vulkan", gpu.Name)
            ?? throw new RuntimeQualificationException(flavor.Backend == "cuda"
                ? "llama.cpp CUDA found no usable NVIDIA GPU. Update the NVIDIA driver from nvidia.com and restart Windows."
                : "llama.cpp Vulkan found no usable GPU. Update the graphics driver (AMD Adrenalin or NVIDIA) and restart Windows.");
        return new QualifiedRuntime(runtime, flavor.Build, devices, device.Name);
    }

    public static int? ParseBuild(string output)
    {
        var match = VersionLine().Match(output);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    public static IReadOnlyList<LlamaDevice> ParseDevices(string output) =>
        DeviceLine().Matches(output).Select(m => new LlamaDevice(
            m.Groups["name"].Value,
            m.Groups["desc"].Value,
            long.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture),
            long.Parse(m.Groups["free"].Value, CultureInfo.InvariantCulture))).ToList();

    /// <summary>The device for the detected GPU (by name), else the backend's largest device.</summary>
    public static LlamaDevice? SelectDevice(IReadOnlyList<LlamaDevice> devices, string prefix, string gpuName)
    {
        var matching = devices.Where(d => d.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return matching.FirstOrDefault(d => gpuName.Length > 0 && d.Description.Contains(gpuName, StringComparison.OrdinalIgnoreCase))
            ?? matching.OrderByDescending(d => d.TotalMb).FirstOrDefault();
    }

    /// <summary>Explain a llama-server process failure from its exit code.</summary>
    public static string DescribeExit(RuntimeFlavor flavor, int exitCode, string output) => unchecked((uint)exitCode) switch
    {
        0xC0000135 => $"A llama.cpp {flavor.DisplayName} file is missing (a required DLL could not be found). Repair the runtime in Settings.",
        0xC000001D => "This processor lacks an instruction llama.cpp needs. Report it with a diagnostics bundle.",
        0xC0000005 => $"llama.cpp {flavor.DisplayName} crashed while starting. Update your graphics driver, then try again.",
        0xC000007B => $"llama.cpp {flavor.DisplayName} could not load a DLL built for another architecture. Repair the runtime in Settings.",
        _ => $"llama.cpp {flavor.DisplayName} exited with code {exitCode}: {LastLines(output, 3)}",
    };

    internal static string LastLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" / ", lines.TakeLast(count));
    }
}
