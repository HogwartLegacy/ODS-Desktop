namespace Ods.Core.Hardware;

/// <summary>One display adapter as DXGI reports it (64-bit memory sizes, no WMI 4 GB cap).</summary>
public sealed record GpuAdapter(
    string Name,
    uint VendorId,
    uint DeviceId,
    long DedicatedVideoMemoryBytes,
    long SharedSystemMemoryBytes,
    string DriverVersion,
    bool IsSoftware)
{
    public const uint NvidiaVendor = 0x10DE;
    public const uint AmdVendor = 0x1002;
    public const uint IntelVendor = 0x8086;
    public const uint MicrosoftVendor = 0x1414;
    public const uint QualcommVendor = 0x5143;

    public string Vendor => VendorId switch
    {
        NvidiaVendor => "NVIDIA",
        AmdVendor or 0x1022 => "AMD",
        IntelVendor => "Intel",
        QualcommVendor or 0x4D4F4351 => "Qualcomm",
        MicrosoftVendor => "Microsoft",
        _ => $"0x{VendorId:X4}",
    };

    public long DedicatedVideoMemoryMb => DedicatedVideoMemoryBytes / (1024 * 1024);
}

/// <summary>nvidia-smi --query-gpu output for the first GPU.</summary>
public sealed record NvidiaGpu(string Name, int VramMb, string DriverVersion, string ComputeCapability, int Count);

/// <summary>
/// The GPU as ODS's Windows installer describes it (Get-GpuInfo in
/// installers/windows/lib/detection.ps1): backend nvidia, amd or none.
/// </summary>
public sealed record GpuSummary(
    string Backend,
    string Name,
    int VramMb,
    int Count,
    string MemoryType,
    string DriverVersion,
    string ComputeCapability)
{
    public static GpuSummary None { get; } = new("none", "None", 0, 0, "none", "", "");
}

public sealed record HardwareInfo(
    string OsDescription,
    int OsBuild,
    string Architecture,
    string CpuName,
    int LogicalProcessors,
    bool Avx2,
    bool Avx512,
    long TotalRamBytes,
    long AvailableRamBytes,
    long AvailableCommitBytes,
    IReadOnlyList<GpuAdapter> Adapters,
    NvidiaGpu? Nvidia,
    GpuSummary Gpu,
    IReadOnlyList<string> ProbeNotes)
{
    /// <summary>Whole GiB of RAM, rounded down, as the ODS installers report it.</summary>
    public int SystemRamGb => (int)(TotalRamBytes / (1024L * 1024 * 1024));

    public bool IsArm64 => Architecture == "arm64";
}
