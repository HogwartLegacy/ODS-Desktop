namespace Ods.Core.Hardware;

/// <summary>Port of ConvertTo-TierFromGpu (installers/windows/lib/tier-map.ps1).</summary>
public static class TierMap
{
    public static string TierFor(GpuSummary gpu, int systemRamGb)
    {
        if (gpu.Backend == "none")
        {
            return systemRamGb < 8 ? "0" : "1";
        }
        if (gpu.Backend == "amd" && gpu.MemoryType == "unified")
        {
            if (systemRamGb >= 90) return "SH_LARGE";
            if (systemRamGb >= 64) return "SH_COMPACT";
            if (systemRamGb < 12) return "0";
            return "1";
        }
        var vramGb = gpu.VramMb / 1024;
        if (vramGb >= 90) return "NV_ULTRA";
        if (vramGb >= 40) return "4";
        if (vramGb >= 20) return "3";
        if (vramGb >= 12) return "2";
        if (vramGb < 4 && systemRamGb < 12) return "0";
        return "1";
    }
}
