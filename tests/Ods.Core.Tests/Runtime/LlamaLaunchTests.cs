using Ods.Core.Runtime;

namespace Ods.Core.Tests.Runtime;

public sealed class LlamaLaunchTests
{
    private static RuntimePins Pins => RuntimePins.Load(Path.Combine(Repo.DesktopRoot, "runtime-pins.json"));

    private static LlamaLaunchSpec Spec(string flavor, string device, Dictionary<string, string>? env = null) => new(
        new QualifiedRuntime(new InstalledRuntime(Pins.Get(flavor), @"C:\ODS\runtimes\x"), 9014, [], device),
        @"C:\ODS\models\Qwen3.5-9B-Q4_K_M.gguf",
        "Qwen3.5-9B-Q4_K_M.gguf",
        8080,
        65536,
        "all",
        @"C:\ODS\secrets\llama-api-key",
        "off",
        env ?? []);

    private static string? Flag(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    [Fact]
    public void Launch_follows_the_ods_native_contract()
    {
        var (arguments, warnings) = LlamaArguments.Build(Spec("win-vulkan-x64", "Vulkan0"));

        Assert.Equal("127.0.0.1", Flag(arguments, "--host"));
        Assert.Equal("Qwen3.5-9B-Q4_K_M.gguf", Flag(arguments, "--alias"));
        Assert.Equal("1", Flag(arguments, "--parallel"));
        Assert.Equal("Vulkan0", Flag(arguments, "--device"));
        Assert.Equal("all", Flag(arguments, "--n-gpu-layers"));
        Assert.Equal(@"C:\ODS\secrets\llama-api-key", Flag(arguments, "--api-key-file"));
        Assert.Contains("--metrics", arguments);
        Assert.Contains("--no-webui", arguments);
        Assert.DoesNotContain("--api-key", arguments);
        Assert.DoesNotContain("--fit", arguments);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Cuda_turns_fit_off_and_the_cpu_route_offloads_nothing()
    {
        Assert.Equal("off", Flag(LlamaArguments.Build(Spec("win-cuda-12.4-x64", "CUDA0")).Arguments, "--fit"));
        Assert.Equal("0", Flag(LlamaArguments.Build(Spec("win-cpu-x64", "none")).Arguments, "--n-gpu-layers"));
    }

    [Fact]
    public void Runtime_profile_settings_become_flags()
    {
        var env = new Dictionary<string, string>
        {
            ["LLAMA_ARG_FLASH_ATTN"] = "on",
            ["LLAMA_ARG_CACHE_TYPE_K"] = "q8_0",
            ["LLAMA_ARG_CACHE_TYPE_V"] = "q8_0",
            ["LLAMA_ARG_CTX_CHECKPOINTS"] = "4",
            ["LLAMA_ARG_CACHE_RAM"] = "512",
            ["LLAMA_PARALLEL"] = "2",
            ["LLAMA_SERVER_MEMORY_LIMIT"] = "4G",
            ["LLAMA_ARG_N_CPU_MOE"] = "bad value; rm -rf",
        };

        var (arguments, warnings) = LlamaArguments.Build(Spec("win-cpu-x64", "none", env));

        Assert.Equal("on", Flag(arguments, "--flash-attn"));
        Assert.Equal("q8_0", Flag(arguments, "--cache-type-k"));
        Assert.Equal("4", Flag(arguments, "--ctx-checkpoints"));
        Assert.Equal("512", Flag(arguments, "--cache-ram"));
        Assert.Equal("1", Flag(arguments, "--parallel"));
        Assert.DoesNotContain("--n-cpu-moe", arguments);
        Assert.Contains(warnings, w => w.Contains("LLAMA_ARG_N_CPU_MOE"));
        Assert.Contains(warnings, w => w.Contains("LLAMA_PARALLEL"));
    }

    [Fact]
    public void Version_and_devices_are_parsed_from_llama_server_output()
    {
        Assert.Equal(9014, LlamaQualifier.ParseBuild("version: 9014 (3f2a1b7)\nbuilt with MSVC 19.44 for x64"));
        var devices = LlamaQualifier.ParseDevices(
            "Available devices:\n  CUDA0: NVIDIA GeForce RTX 4090 (24563 MiB, 22994 MiB free)\n  Vulkan0: AMD Radeon(TM) Graphics (16384 MiB, 16000 MiB free)\n");

        Assert.Equal(2, devices.Count);
        Assert.Equal("CUDA0", LlamaQualifier.SelectDevice(devices, "CUDA", "NVIDIA GeForce RTX 4090")!.Name);
        Assert.Equal("Vulkan0", LlamaQualifier.SelectDevice(devices, "Vulkan", "something else")!.Name);
        Assert.Null(LlamaQualifier.SelectDevice(LlamaQualifier.ParseDevices("Available devices:\n"), "CUDA", ""));
    }

    [Theory]
    [InlineData("ggml_backend_cuda_buffer_type_alloc_buffer: allocating 9000 MiB on device 0: cudaMalloc failed: out of memory", FailureKind.OutOfMemory, RecoveryAction.ReduceContext)]
    [InlineData("error: couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 8080", FailureKind.PortInUse, RecoveryAction.ChangePort)]
    [InlineData("llama_model_load: error loading model: invalid magic characters", FailureKind.ModelLoad, RecoveryAction.ChooseModel)]
    [InlineData("ggml_vulkan: No devices found.", FailureKind.DeviceUnavailable, RecoveryAction.UpdateDriver)]
    public void Failures_are_classified_into_recovery_actions(string line, FailureKind kind, RecoveryAction action)
    {
        var failure = FailureClassifier.Classify(1, ["loading...", line], Pins.Get("win-cuda-12.4-x64"));

        Assert.Equal(kind, failure.Kind);
        Assert.True(failure.Actions.HasFlag(action));
    }

    [Fact]
    public void Missing_dll_exit_code_offers_a_runtime_repair()
    {
        var failure = FailureClassifier.Classify(unchecked((int)0xC0000135), [], Pins.Get("win-vulkan-x64"));

        Assert.Equal(FailureKind.RuntimeDamaged, failure.Kind);
        Assert.True(failure.Actions.HasFlag(RecoveryAction.RepairRuntime));
    }
}
