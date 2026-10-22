using System.Globalization;
using System.Text.RegularExpressions;
using Ods.Core.Platform;

namespace Ods.Core.Runtime;

/// <summary>Everything needed to launch one llama-server for one model.</summary>
public sealed record LlamaLaunchSpec(
    QualifiedRuntime Runtime,
    string ModelPath,
    string Alias,
    int Port,
    int ContextSize,
    string GpuLayers,
    string ApiKeyFile,
    string Reasoning,
    IReadOnlyDictionary<string, string> ProfileEnv);

/// <summary>
/// Builds llama-server arguments the way ODS's native Windows launcher does
/// (installers/windows/lib/native-llama-runtime.ps1): loopback only, the GGUF
/// name as alias, one slot so /props proves the planned context, the device
/// named explicitly, metrics on, no web UI, and the API key from an
/// owner-only file. Catalog runtime-profile settings become flags.
/// </summary>
public static partial class LlamaArguments
{
    private static readonly (string Env, string Flag)[] TuningFlags =
    [
        ("LLAMA_ARG_FLASH_ATTN", "--flash-attn"),
        ("LLAMA_ARG_CACHE_TYPE_K", "--cache-type-k"),
        ("LLAMA_ARG_CACHE_TYPE_V", "--cache-type-v"),
        ("LLAMA_ARG_N_CPU_MOE", "--n-cpu-moe"),
        ("LLAMA_ARG_CTX_CHECKPOINTS", "--ctx-checkpoints"),
        ("LLAMA_ARG_CACHE_RAM", "--cache-ram"),
        ("LLAMA_ARG_SPEC_TYPE", "--spec-type"),
        ("LLAMA_ARG_SPEC_DRAFT_N_MAX", "--spec-draft-n-max"),
        ("LLAMA_ARG_SPEC_DRAFT_TYPE_K", "--spec-draft-type-k"),
        ("LLAMA_ARG_SPEC_DRAFT_TYPE_V", "--spec-draft-type-v"),
    ];

    // Profile keys with no flag here (LLAMA_SERVER_MEMORY_LIMIT, the Docker
    // container's memory cap) have no meaning for a native process and are
    // left out by design.

    [GeneratedRegex("^-?[A-Za-z0-9._][A-Za-z0-9._-]{0,31}$")]
    private static partial Regex SafeValue();

    public static (IReadOnlyList<string> Arguments, IReadOnlyList<string> Warnings) Build(LlamaLaunchSpec spec)
    {
        if (spec.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(spec), "The llama-server port must be 1 to 65535.");
        if (spec.ContextSize is < 1 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(spec), "The context size is out of range.");
        if (spec.Reasoning is not ("off" or "on" or "auto")) throw new ArgumentException("Reasoning must be off, on or auto.", nameof(spec));
        if (!SafeValue().IsMatch(spec.GpuLayers)) throw new ArgumentException("The GPU layer setting is invalid.", nameof(spec));
        var cpuRoute = spec.Runtime.Device == "none";

        var warnings = new List<string>();
        var arguments = new List<string>
        {
            "--model", WindowsPaths.ToLlamaArgumentPath(spec.ModelPath),
            "--alias", spec.Alias,
            // Loopback only: the authenticated model API never leaves this PC.
            "--host", "127.0.0.1",
            "--port", spec.Port.ToString(CultureInfo.InvariantCulture),
            "--ctx-size", spec.ContextSize.ToString(CultureInfo.InvariantCulture),
            // One slot makes /props n_ctx the whole planned context.
            "--parallel", "1",
            "--n-gpu-layers", cpuRoute ? "0" : spec.GpuLayers,
            // Naming the device fails closed when the GPU is unusable instead
            // of llama.cpp's silent CPU fallback.
            "--device", spec.Runtime.Device,
            "--metrics",
            "--no-webui",
            "--api-key-file", WindowsPaths.ToLlamaArgumentPath(spec.ApiKeyFile),
            "--reasoning", spec.Reasoning,
        };
        if (spec.Runtime.Flavor.Backend == "cuda")
        {
            // llama.cpp defaults to --fit on, which can move layers to the CPU
            // to fit WDDM's budget; the selector already sized the plan.
            arguments.AddRange(["--fit", "off"]);
        }

        foreach (var (env, flag) in TuningFlags)
        {
            if (!spec.ProfileEnv.TryGetValue(env, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            value = value.Trim();
            if (!SafeValue().IsMatch(value))
            {
                warnings.Add($"{env}={value} is not a valid llama-server value; starting without it.");
                continue;
            }
            arguments.AddRange([flag, value]);
        }
        if (spec.ProfileEnv.TryGetValue("LLAMA_ARG_NO_CACHE_PROMPT", out var noCache)
            && noCache.Trim().ToLowerInvariant() is not ("" or "0" or "false" or "off" or "no"))
        {
            arguments.Add("--no-cache-prompt");
        }
        if (spec.ProfileEnv.TryGetValue("LLAMA_PARALLEL", out var parallel) && parallel.Trim() is not ("" or "1"))
        {
            warnings.Add($"LLAMA_PARALLEL={parallel} is ignored: the native llama-server runs one slot so its context can be proven.");
        }
        return (arguments, warnings);
    }

    /// <summary>The runtime profile's env block as a dictionary (string values only).</summary>
    public static IReadOnlyDictionary<string, string> ProfileEnv(System.Text.Json.Nodes.JsonObject? runtimeProfile)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (runtimeProfile?["env"] is not System.Text.Json.Nodes.JsonObject env) return result;
        foreach (var (key, value) in env)
        {
            if (value is null) continue;
            result[key] = value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? text) ? text : value.ToJsonString();
        }
        return result;
    }
}
