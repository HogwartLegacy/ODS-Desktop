using System.Text.RegularExpressions;

namespace Ods.Core.Hardware;

/// <summary>
/// Pure GPU classification, ported from installers/windows/lib/detection.ps1
/// so the desktop app and the ODS Windows installer describe the same
/// hardware the same way (AMD integrated vs discrete names, the Strix Halo
/// unified-memory heuristic, the NVIDIA NPU filter on Windows on Arm).
/// </summary>
public static partial class GpuClassifier
{
    [GeneratedRegex(@"\bRadeon(?:\(TM\))?\s+Graphics$", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonGraphics();

    [GeneratedRegex(@"\bRadeon(?:\(TM\))?\s+\d{3,4}[MS](?:\s+Graphics)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonApuNumber();

    [GeneratedRegex(@"\bRadeon(?:\(TM\))?\s+(?:RX\s+)?Vega\s+\d{1,2}\s+Graphics$", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonVega();

    [GeneratedRegex(@"\bStrix\s+Halo\b", RegexOptions.IgnoreCase)]
    private static partial Regex StrixHalo();

    [GeneratedRegex(@"\b(?:AMD\s+)?Radeon\b|\bFirePro\b", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonOrFirePro();

    [GeneratedRegex(@"Strix|AI\s*(?:MAX|300|395)|Radeon\s+80[56]0S", RegexOptions.IgnoreCase)]
    private static partial Regex StrixGpuSignature();

    [GeneratedRegex(@"Ryzen\s+AI\s+MAX|Strix\s+Halo", RegexOptions.IgnoreCase)]
    private static partial Regex StrixCpuSignature();

    [GeneratedRegex("AMD|Radeon", RegexOptions.IgnoreCase)]
    private static partial Regex AmdName();

    public static bool IsAmdIntegratedName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (RadeonGraphics().IsMatch(name) || RadeonApuNumber().IsMatch(name) || RadeonVega().IsMatch(name) || StrixHalo().IsMatch(name));

    /// <summary>Unknown Radeon families count as discrete: claiming unified memory for a discrete card can pick a model that cannot fit.</summary>
    public static bool IsAmdDiscreteName(string? name) =>
        !IsAmdIntegratedName(name) && !string.IsNullOrEmpty(name) && RadeonOrFirePro().IsMatch(name);

    public static bool IsAmdUnifiedMemory(string gpuName, string processorNames, long adapterRamMb, int systemRamGb)
    {
        if (IsAmdDiscreteName(gpuName)) return false;
        var strixSignature = StrixGpuSignature().IsMatch(gpuName) || StrixCpuSignature().IsMatch(processorNames);
        return adapterRamMb <= 4096 && systemRamGb >= 32 && (strixSignature || IsAmdIntegratedName(gpuName));
    }

    public static bool IsAmd(GpuAdapter adapter) =>
        adapter.VendorId is GpuAdapter.AmdVendor or 0x1022 || AmdName().IsMatch(adapter.Name);

    /// <summary>
    /// Get-GpuInfo: NVIDIA first (from nvidia-smi), then AMD, else none.
    /// <paramref name="registryVramMb"/> resolves dedicated VRAM from the
    /// driver's HardwareInformation.qwMemorySize, as the installer does.
    /// </summary>
    public static GpuSummary Summarize(
        IReadOnlyList<GpuAdapter> adapters,
        NvidiaGpu? nvidia,
        string processorNames,
        int systemRamGb,
        Func<string, long>? registryVramMb = null)
    {
        if (nvidia is not null)
        {
            return new GpuSummary("nvidia", nvidia.Name, nvidia.VramMb, nvidia.Count, "discrete", nvidia.DriverVersion, nvidia.ComputeCapability);
        }

        var amd = adapters.Where(a => !a.IsSoftware && IsAmd(a)).ToList();
        if (amd.Count == 0) return GpuSummary.None;

        // Hybrid systems often enumerate the integrated adapter first.
        var primary = amd.FirstOrDefault(a => IsAmdDiscreteName(a.Name)) ?? amd[0];
        var discreteCount = amd.Count(a => IsAmdDiscreteName(a.Name));

        // WMI's AdapterRAM is 32-bit; ODS keeps that capped value for the
        // unified-memory gate, so an APU with a large BIOS carve-out stays unified.
        var adapterRamMb = Math.Min(primary.DedicatedVideoMemoryMb, 4095);
        var unified = IsAmdUnifiedMemory(primary.Name, processorNames, adapterRamMb, systemRamGb);
        int vramMb;
        if (unified)
        {
            // ~75% of system RAM is usable by the GPU on Strix Halo.
            vramMb = (int)Math.Floor(systemRamGb * 0.75 * 1024);
        }
        else
        {
            var registryMb = registryVramMb?.Invoke(primary.Name) ?? 0;
            vramMb = (int)Math.Max(primary.DedicatedVideoMemoryMb, registryMb);
        }
        return new GpuSummary(
            "amd",
            primary.Name,
            vramMb,
            discreteCount > 0 ? discreteCount : amd.Count,
            unified ? "unified" : "discrete",
            primary.DriverVersion.Length > 0 ? primary.DriverVersion : "unknown",
            "");
    }

    /// <summary>
    /// Parse nvidia-smi --query-gpu=name,memory.total,driver_version,compute_cap
    /// --format=csv,noheader. Windows on Arm also lists the NVIDIA NPU with
    /// memory [N/A]; beside a GPU with a size it is neither counted nor first.
    /// </summary>
    public static NvidiaGpu? ParseNvidiaSmi(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Contains(','))
            .ToList();
        if (lines.Count == 0) return null;
        static string Field(string line, int index)
        {
            var fields = line.Split(',');
            return index < fields.Length ? fields[index].Trim() : "";
        }
        if (lines.Any(line => Field(line, 1).Any(char.IsDigit)))
        {
            lines = lines.Where(line => Field(line, 1) is not ("[N/A]" or "N/A")).ToList();
        }
        var first = lines[0];
        var vramDigits = new string(Field(first, 1).Where(char.IsDigit).ToArray());
        if (vramDigits.Length == 0 || !int.TryParse(vramDigits, out var vramMb)) return null;
        return new NvidiaGpu(Field(first, 0), vramMb, Field(first, 2), Field(first, 3), lines.Count);
    }

    /// <summary>Intel Arc discrete cards: detected so the app can explain they run on the CPU for now.</summary>
    public static bool IsIntelArc(GpuAdapter adapter) =>
        adapter.VendorId == GpuAdapter.IntelVendor && adapter.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
}
