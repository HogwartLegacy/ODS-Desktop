using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Ods.Core.Diagnostics;
using Ods.Core.Platform;

namespace Ods.Core.Hardware;

/// <summary>Reads this PC's CPU, memory and GPUs. Each source failing is recorded, never hidden.</summary>
public static class HardwareProbe
{
    private const string VideoClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static async Task<HardwareInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var memory = new NativeMethods.MemoryStatusEx { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref memory))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read this PC's memory size.");
        }

        List<GpuAdapter> adapters;
        try
        {
            adapters = DxgiAdapters.Enumerate();
        }
        catch (COMException error)
        {
            adapters = [];
            notes.Add($"Windows did not list display adapters (DXGI 0x{error.HResult:X8}); GPU acceleration is unavailable.");
        }

        // Like Get-GpuInfo, ask nvidia-smi even when DXGI listed nothing.
        var hasNvidiaAdapter = adapters.Any(a => a.VendorId == GpuAdapter.NvidiaVendor);
        var nvidia = hasNvidiaAdapter || adapters.Count == 0
            ? await QueryNvidiaAsync(notes, hasNvidiaAdapter, cancellationToken)
            : null;

        var cpuName = ReadCpuName();
        var ramGb = (int)(memory.TotalPhys / (1024UL * 1024 * 1024));
        var gpu = GpuClassifier.Summarize(adapters, nvidia, cpuName, ramGb, RegistryVramMb);

        foreach (var arc in adapters.Where(GpuClassifier.IsIntelArc))
        {
            notes.Add($"{arc.Name} is detected. ODS does not accelerate Intel GPUs on Windows yet, so models run on the CPU.");
        }
        notes.AddRange(ExistingInstallations.Detect());

        return new HardwareInfo(
            RuntimeInformation.OSDescription,
            Environment.OSVersion.Version.Build,
            RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64",
            cpuName,
            Environment.ProcessorCount,
            NativeMethods.IsProcessorFeaturePresent(NativeMethods.PfAvx2InstructionsAvailable),
            NativeMethods.IsProcessorFeaturePresent(NativeMethods.PfAvx512FInstructionsAvailable),
            (long)memory.TotalPhys,
            (long)memory.AvailPhys,
            (long)memory.AvailPageFile,
            adapters,
            nvidia,
            gpu,
            notes);
    }

    private static async Task<NvidiaGpu?> QueryNvidiaAsync(List<string> notes, bool adapterListed, CancellationToken cancellationToken)
    {
        var smi = FindNvidiaSmi();
        if (smi is null)
        {
            if (adapterListed)
            {
                notes.Add("An NVIDIA GPU is present but nvidia-smi was not found; install the current NVIDIA driver for CUDA acceleration.");
            }
            return null;
        }
        var result = await ProcessRunner.RunAsync(
            smi,
            ["--query-gpu=name,memory.total,driver_version,compute_cap", "--format=csv,noheader"],
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            notes.Add($"nvidia-smi failed (exit {result.ExitCode}): {result.Output.Trim()}");
            return null;
        }
        var parsed = GpuClassifier.ParseNvidiaSmi(result.Output);
        if (parsed is null) notes.Add($"nvidia-smi output was not understood: {result.Output.Trim()}");
        return parsed;
    }

    private static string? FindNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ReadCpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown processor";
    }

    /// <summary>Dedicated VRAM from the display driver's HardwareInformation.qwMemorySize (0 when absent).</summary>
    private static long RegistryVramMb(string adapterName)
    {
        using var videoClass = Registry.LocalMachine.OpenSubKey(VideoClassKey);
        if (videoClass is null) return 0;
        long best = 0;
        foreach (var name in videoClass.GetSubKeyNames())
        {
            try
            {
                using var key = videoClass.OpenSubKey(name);
                if (key?.GetValue("DriverDesc") as string != adapterName) continue;
                var raw = key.GetValue("HardwareInformation.qwMemorySize");
                var bytes = raw switch
                {
                    byte[] { Length: >= 8 } array => BitConverter.ToInt64(array, 0),
                    long value => value,
                    int value => (long)(uint)value,
                    _ => 0L,
                };
                best = Math.Max(best, bytes / (1024 * 1024));
            }
            catch (System.Security.SecurityException)
            {
                // Some class subkeys (e.g. Properties) are restricted; they hold no adapter data.
            }
        }
        return best;
    }
}

/// <summary>Runs a short-lived helper process and captures its combined output.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output, bool TimedOut);

    public static async Task<Result> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            AppLog.Warn($"{Path.GetFileName(fileName)} {string.Join(' ', arguments)} did not exit within {timeout.TotalSeconds:0} s and was stopped.");
            return new Result(-1, "", TimedOut: true);
        }
        return new Result(process.ExitCode, await stdout + await stderr, TimedOut: false);
    }
}
