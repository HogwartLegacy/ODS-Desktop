namespace Ods.Core.Runtime;

public enum FailureKind
{
    OutOfMemory,
    ModelLoad,
    PortInUse,
    RuntimeDamaged,
    DeviceUnavailable,
    Crash,
    Timeout,
    ProofFailed,
    Unknown,
}

/// <summary>What the UI can offer to recover from a failure.</summary>
[Flags]
public enum RecoveryAction
{
    None = 0,
    Restart = 1,
    ReduceContext = 2,
    ChooseModel = 4,
    RepairRuntime = 8,
    ChangePort = 16,
    UpdateDriver = 32,
    OpenLogs = 64,
}

/// <summary>Why llama-server stopped or never became ready, in words the user can act on.</summary>
public sealed record ServerFailure(
    FailureKind Kind,
    string Title,
    string Detail,
    RecoveryAction Actions,
    int? ExitCode,
    IReadOnlyList<string> LastLines);

/// <summary>Classifies llama-server failures from its output and Windows exit code.</summary>
public static class FailureClassifier
{
    private static readonly string[] OutOfMemory =
    [
        "out of memory", "cudamalloc failed", "erroroutofdevicememory", "failed to allocate", "unable to allocate",
        "not enough memory", "cannot allocate", "alloc_tensor_range: failed", "allocatememory",
    ];

    private static readonly string[] ModelLoad =
    [
        "failed to load model", "error loading model", "llama_model_load: error", "invalid magic",
        "unknown model architecture", "unsupported model", "gguf_init_from_file", "failed to read",
    ];

    private static readonly string[] PortInUse =
    [
        "couldn't bind", "address already in use", "failed to listen", "only one usage of each socket address",
    ];

    private static readonly string[] DeviceUnavailable =
    [
        "invalid device", "device not found", "no devices found", "no cuda-capable device", "unknown device",
        "cuda driver version is insufficient",
    ];

    public static ServerFailure Classify(int? exitCode, IReadOnlyList<string> recentOutput, RuntimeFlavor flavor, bool timedOut = false)
    {
        var tail = recentOutput.TakeLast(12).ToList();
        var text = string.Join('\n', recentOutput.TakeLast(80)).ToLowerInvariant();
        bool Has(string[] needles) => needles.Any(text.Contains);
        var memoryName = flavor.IsGpu ? "GPU memory" : "system memory";

        if (timedOut)
        {
            return new ServerFailure(FailureKind.Timeout, "The model took too long to load",
                "llama-server did not become ready in time. A slow disk or a model larger than this PC can hold usually causes this.",
                RecoveryAction.Restart | RecoveryAction.ChooseModel | RecoveryAction.OpenLogs, exitCode, tail);
        }
        if (Has(OutOfMemory))
        {
            return new ServerFailure(FailureKind.OutOfMemory, $"Not enough {memoryName} for this model",
                $"The model and its context do not fit in {memoryName}. Use a smaller context or a smaller model. Closing other GPU-heavy apps can also help.",
                RecoveryAction.ReduceContext | RecoveryAction.ChooseModel | RecoveryAction.OpenLogs, exitCode, tail);
        }
        if (Has(PortInUse))
        {
            return new ServerFailure(FailureKind.PortInUse, "The model server's port is taken",
                "Another program is using the model server port. ODS can pick a free port.",
                RecoveryAction.ChangePort | RecoveryAction.OpenLogs, exitCode, tail);
        }
        if (Has(DeviceUnavailable))
        {
            return new ServerFailure(FailureKind.DeviceUnavailable, "The GPU is not available to llama.cpp",
                $"llama.cpp {flavor.DisplayName} could not use the GPU. Update the graphics driver and restart Windows.",
                RecoveryAction.UpdateDriver | RecoveryAction.Restart | RecoveryAction.OpenLogs, exitCode, tail);
        }
        if (Has(ModelLoad))
        {
            return new ServerFailure(FailureKind.ModelLoad, "The model file could not be loaded",
                "The file may be damaged, incomplete, or a format this llama.cpp build does not support. Delete and download it again, or choose another model.",
                RecoveryAction.ChooseModel | RecoveryAction.OpenLogs, exitCode, tail);
        }
        if (exitCode is int code && unchecked((uint)code) is 0xC0000135 or 0xC000007B)
        {
            return new ServerFailure(FailureKind.RuntimeDamaged, "A llama.cpp runtime file is missing",
                LlamaQualifier.DescribeExit(flavor, code, ""), RecoveryAction.RepairRuntime | RecoveryAction.OpenLogs, exitCode, tail);
        }
        if (exitCode is int crash && unchecked((uint)crash) >= 0xC0000000)
        {
            return new ServerFailure(FailureKind.Crash, "llama.cpp crashed",
                LlamaQualifier.DescribeExit(flavor, crash, ""),
                RecoveryAction.Restart | RecoveryAction.UpdateDriver | RecoveryAction.OpenLogs, exitCode, tail);
        }
        var last = tail.LastOrDefault(l => l.Trim().Length > 0) ?? "no output";
        return new ServerFailure(FailureKind.Unknown, "The model server stopped",
            exitCode is int unknown ? $"llama-server exited with code {unknown}. Last message: {last}" : $"Last message: {last}",
            RecoveryAction.Restart | RecoveryAction.OpenLogs, exitCode, tail);
    }
}
