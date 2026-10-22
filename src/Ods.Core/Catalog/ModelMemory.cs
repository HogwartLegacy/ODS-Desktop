using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ods.Core.Catalog;

/// <summary>One model at one context and cache configuration (model_memory.MemoryEstimate).</summary>
public sealed record MemoryEstimate(
    int ContextLength,
    string CacheTypeK,
    string CacheTypeV,
    int? Layers,
    int? KvLayers,
    double? KvBytesPerToken,
    double WeightsGib,
    double KvGib,
    double RecurrentStateGib,
    double ComputeOverheadGib,
    double HostCheckpointGib,
    double DeviceGib,
    double TotalGib,
    string Method,
    double SwaKvGib = 0.0)
{
    public bool IsArchitecture => Method == "architecture";
}

/// <summary>Cache settings a runtime profile sets (model_memory.runtime_profile_cache_settings).</summary>
public sealed record RuntimeCacheSettings(string CacheTypeK, string CacheTypeV, int? CtxCheckpoints, int Parallel);

/// <summary>
/// Port of extensions/services/dashboard-api/model_memory.py: context-aware
/// memory estimates shared by the installer, dashboard and activation. Keep
/// the constants and arithmetic order identical to the Python module; the
/// golden-fixture tests compare both on every ODS hardware envelope.
/// </summary>
public static partial class ModelMemory
{
    public const double Mib = 1024.0 * 1024.0;
    public const double Gib = 1024.0 * 1024.0 * 1024.0;

    private static readonly Dictionary<string, double> KvCacheBytesPerElement = new()
    {
        ["f32"] = 4.0,
        ["f16"] = 2.0,
        ["bf16"] = 2.0,
        ["q8_0"] = 34.0 / 32,
        ["q5_1"] = 24.0 / 32,
        ["q5_0"] = 22.0 / 32,
        ["q4_1"] = 20.0 / 32,
        ["q4_0"] = 18.0 / 32,
        ["iq4_nl"] = 18.0 / 32,
    };

    public const double OverheadBaseGib = 0.35;
    public const double OverheadPerWeightGib = 0.015;
    public const int LlamaDefaultCtxCheckpoints = 32;
    public const int SwaUbatchCells = 512;
    public const int SwaCellPadding = 256;
    public const double DiscreteFitMarginMinGib = 0.25;
    public const double DiscreteFitMarginFraction = 0.03;
    public const double LegacyFitToleranceGib = 0.25;
    public const int MinimumContext = 8192;
    public static readonly int[] ContextSteps = [8192, 16384, 32768, 65536, 131072, 262144];

    public static readonly string[] MemoryMetadataKeys =
    [
        "total_params_b", "params_b", "block_count", "embedding_length",
        "attention_head_count", "head_count", "attention_head_count_kv",
        "head_count_kv", "attention_head_dimension", "head_dimension",
        "attention_key_length", "attention_value_length", "kv_cache_element_bytes",
        "attention_layer_count", "full_attention_interval", "recurrent_state_bytes",
        "size_bytes", "max_context_length",
        "sliding_window", "sliding_window_layer_count", "sliding_window_head_count_kv",
        "sliding_window_key_length", "sliding_window_value_length",
    ];

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*b", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterScale();

    private static JsonNode? G(JsonObject model, string key) => Py.Get(model, key);

    /// <summary>Best-effort model scale from explicit metadata, name, then file size.</summary>
    public static double EstimatedParamBillions(JsonObject model)
    {
        foreach (var key in new[] { "total_params_b", "params_b" })
        {
            var value = Py.PositiveNumber(G(model, key));
            if (value > 0) return value;
        }

        var numbers = new List<double>();
        foreach (var key in new[] { "id", "name", "llm_model_name", "gguf", "gguf_file" })
        {
            foreach (Match match in ParameterScale().Matches(Py.Str(G(model, key))))
            {
                numbers.Add(double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (numbers.Count > 0) return numbers.Max();

        var sizeMb = Py.PositiveNumber(G(model, "size_mb"));
        if (sizeMb > 0) return Math.Max(sizeMb / 600.0, 1.0);
        return 4.0;
    }

    private static int Context(JsonObject model, int? contextLength)
    {
        int context = contextLength is int explicitContext ? explicitContext : Py.IntOr(G(model, "context_length"));
        return Math.Max(context, MinimumContext);
    }

    private static (double Key, double Value) KvDimensions(JsonObject model)
    {
        var embeddingLength = Py.PositiveNumber(G(model, "embedding_length"));
        var headCount = Py.PositiveNumber(Py.Or(G(model, "attention_head_count"), G(model, "head_count")));
        var headDimension = Py.PositiveNumber(Py.Or(G(model, "attention_head_dimension"), G(model, "head_dimension")));
        var derived = embeddingLength > 0 && headCount > 0 ? embeddingLength / headCount : 0.0;
        var keyDimension = Py.PositiveNumber(G(model, "attention_key_length"));
        var valueDimension = Py.PositiveNumber(G(model, "attention_value_length"));
        return (
            FirstPositive(keyDimension, headDimension, derived),
            FirstPositive(valueDimension, headDimension, derived));
    }

    private static double FirstPositive(params double[] values)
    {
        foreach (var value in values) if (value != 0) return value;
        return values[^1];
    }

    private static JsonNode? RawKvHeads(JsonObject model) => Py.Or(G(model, "attention_head_count_kv"), G(model, "head_count_kv"));

    private static List<double>? CompletePerLayerKvHeads(JsonObject model)
    {
        if (RawKvHeads(model) is not JsonArray raw) return null;
        var blockCount = Py.PositiveNumber(G(model, "block_count"));
        var heads = raw.Select(Py.PositiveNumber).ToList();
        // Per-layer arrays are authoritative only when complete.
        if (blockCount > 0 && heads.Count == (int)blockCount) return heads;
        return null;
    }

    /// <summary>Number of layers that hold a KV cache (hybrid SSM layers hold none).</summary>
    public static int? KvLayerCount(JsonObject model)
    {
        var blockCount = (int)Py.PositiveNumber(G(model, "block_count"));
        if (blockCount == 0) return null;
        var explicitCount = (int)Py.PositiveNumber(G(model, "attention_layer_count"));
        if (explicitCount != 0) return Math.Min(explicitCount, blockCount);
        var perLayer = CompletePerLayerKvHeads(model);
        if (perLayer is not null) return perLayer.Count(heads => heads > 0);
        if (RawKvHeads(model) is JsonArray) return null;
        var interval = (int)Py.PositiveNumber(G(model, "full_attention_interval"));
        if (interval > 1) return blockCount / interval;
        return blockCount;
    }

    private static double KvHeadLayers(JsonObject model)
    {
        var perLayer = CompletePerLayerKvHeads(model);
        if (perLayer is not null) return perLayer.Sum();
        var raw = RawKvHeads(model);
        if (raw is JsonArray) return 0.0;
        var heads = Py.PositiveNumber(raw);
        var layers = KvLayerCount(model);
        return heads > 0 && layers is int l && l != 0 ? heads * l : 0.0;
    }

    private static double CacheElementBytes(string? cacheType)
    {
        var key = (cacheType ?? "f16").Trim().ToLowerInvariant();
        if (key.Length == 0) key = "f16";
        // Unknown cache types are charged as f16, the llama.cpp default.
        return KvCacheBytesPerElement.TryGetValue(key, out var bytes) ? bytes : 2.0;
    }

    /// <summary>KV-cache bytes per context token, or null when metadata is incomplete.</summary>
    public static double? KvBytesPerToken(JsonObject model, string cacheTypeK = "f16", string cacheTypeV = "f16")
    {
        var headLayers = KvHeadLayers(model);
        var (keyDimension, valueDimension) = KvDimensions(model);
        if (headLayers == 0 || keyDimension == 0 || valueDimension == 0) return null;
        return headLayers * (keyDimension * CacheElementBytes(cacheTypeK) + valueDimension * CacheElementBytes(cacheTypeV));
    }

    /// <summary>KV bytes per cached cell summed over the sliding-window layers (0 if none).</summary>
    public static double SlidingWindowKvBytesPerCell(JsonObject model, string cacheTypeK = "f16", string cacheTypeV = "f16")
    {
        var window = Py.PositiveNumber(G(model, "sliding_window"));
        var layers = Py.PositiveNumber(G(model, "sliding_window_layer_count"));
        var heads = Py.PositiveNumber(G(model, "sliding_window_head_count_kv"));
        var keyDimension = Py.PositiveNumber(G(model, "sliding_window_key_length"));
        var valueDimension = Py.PositiveNumber(G(model, "sliding_window_value_length"));
        if (valueDimension == 0) valueDimension = keyDimension;
        if (window == 0 || layers == 0 || heads == 0 || keyDimension == 0) return 0.0;
        return layers * heads * (keyDimension * CacheElementBytes(cacheTypeK) + valueDimension * CacheElementBytes(cacheTypeV));
    }

    /// <summary>Cells llama.cpp allocates for the sliding-window cache at this context.</summary>
    public static int SlidingWindowCells(JsonObject model, int contextLength, int parallel = 1)
    {
        var window = (int)Py.PositiveNumber(G(model, "sliding_window"));
        if (window == 0) return 0;
        var cells = window * Math.Max(parallel == 0 ? 1 : parallel, 1) + SwaUbatchCells;
        var padded = (cells + SwaCellPadding - 1) / SwaCellPadding * SwaCellPadding;
        return Math.Min(contextLength, padded);
    }

    /// <summary>True when the entry carries a reviewed attention layout.</summary>
    public static bool ArchitectureMetadataComplete(JsonObject model) =>
        KvBytesPerToken(model) is not null
        && Py.NonNegativeNumber(G(model, "recurrent_state_bytes")) is not null
        && WeightsBytes(model) > 0;

    private static double WeightsBytes(JsonObject model, double? weightSizeMb = null)
    {
        var explicitSize = weightSizeMb is double w && double.IsFinite(w) && w > 0 ? w : 0.0;
        if (explicitSize > 0) return explicitSize * Mib;
        var sizeBytes = Py.PositiveNumber(G(model, "size_bytes"));
        if (sizeBytes > 0) return sizeBytes;
        return Py.PositiveNumber(G(model, "size_mb")) * Mib;
    }

    /// <summary>Estimate memory for <paramref name="model"/> at <paramref name="contextLength"/>.</summary>
    public static MemoryEstimate EstimateModelMemory(
        JsonObject model,
        int? contextLength = null,
        string cacheTypeK = "f16",
        string cacheTypeV = "f16",
        int parallel = 1,
        int? ctxCheckpoints = null,
        double? weightSizeMb = null)
    {
        var context = Context(model, contextLength);
        var cacheK = string.IsNullOrWhiteSpace(cacheTypeK) ? "f16" : cacheTypeK.Trim().ToLowerInvariant();
        var cacheV = string.IsNullOrWhiteSpace(cacheTypeV) ? "f16" : cacheTypeV.Trim().ToLowerInvariant();
        var blockCount = (int)Py.PositiveNumber(G(model, "block_count"));
        int? layers = blockCount != 0 ? blockCount : null;
        var kvLayers = KvLayerCount(model);

        if (ArchitectureMetadataComplete(model))
        {
            var perToken = KvBytesPerToken(model, cacheK, cacheV) ?? 0.0;
            var weights = WeightsBytes(model, weightSizeMb) / Gib;
            var sequences = Math.Max(parallel == 0 ? 1 : parallel, 1);
            var swaPerCell = SlidingWindowKvBytesPerCell(model, cacheK, cacheV);
            var swaKv = swaPerCell * SlidingWindowCells(model, context, sequences) / Gib;
            var kv = perToken * context / Gib + swaKv;
            var stateBytes = Py.NonNegativeNumber(G(model, "recurrent_state_bytes")) ?? 0.0;
            var recurrent = stateBytes * sequences / Gib;
            var overhead = OverheadBaseGib + OverheadPerWeightGib * weights;
            var checkpoints = ctxCheckpoints is int c ? Math.Max(c, 0) : LlamaDefaultCtxCheckpoints;
            var swaStateBytes = swaPerCell * SlidingWindowCells(model, context, 1);
            var host = checkpoints * (stateBytes + swaStateBytes) * sequences / Gib;
            var device = weights + kv + recurrent + overhead;
            return new MemoryEstimate(
                ContextLength: context,
                CacheTypeK: cacheK,
                CacheTypeV: cacheV,
                Layers: layers,
                KvLayers: kvLayers,
                KvBytesPerToken: perToken,
                WeightsGib: Py.Round(weights, 3),
                KvGib: Py.Round(kv, 3),
                RecurrentStateGib: Py.Round(recurrent, 3),
                ComputeOverheadGib: Py.Round(overhead, 3),
                HostCheckpointGib: Py.Round(host, 3),
                DeviceGib: Py.Round(device, 2),
                TotalGib: Py.Round(device + host, 2),
                Method: "architecture",
                SwaKvGib: Py.Round(swaKv, 3));
        }

        // Legacy path: bit-for-bit the historical estimate (size_mb, not
        // size_bytes; KV rounded to 0.01; the declared catalog value as a floor).
        var legacyKv = EstimatedContextKvGb(model, context);
        var sizeMb = weightSizeMb is double explicitMb
            ? (double.IsFinite(explicitMb) && explicitMb > 0 ? explicitMb : 0.0)
            : Py.PositiveNumber(G(model, "size_mb"));
        var legacyWeights = sizeMb / 1024.0;
        var sizeAndKv = sizeMb > 0 ? legacyWeights + legacyKv : 0.0;
        var legacyDevice = Py.Round(Math.Max(Py.PositiveNumber(G(model, "vram_required_gb")), sizeAndKv), 2);
        return new MemoryEstimate(
            ContextLength: context,
            CacheTypeK: cacheK,
            CacheTypeV: cacheV,
            Layers: layers,
            KvLayers: kvLayers,
            KvBytesPerToken: KvBytesPerToken(model),
            WeightsGib: Py.Round(legacyWeights, 3),
            KvGib: legacyKv,
            RecurrentStateGib: 0.0,
            ComputeOverheadGib: 0.0,
            HostCheckpointGib: 0.0,
            DeviceGib: legacyDevice,
            TotalGib: legacyDevice,
            Method: "legacy-heuristic");
    }

    /// <summary>Standard llama.cpp f16 KV pressure at the selected context.</summary>
    public static double EstimatedContextKvGb(JsonObject model, int? contextLength = null)
    {
        var context = Context(model, contextLength);
        var perToken = KvBytesPerToken(model);
        if (perToken is double bytes)
        {
            var elementBytes = Py.PositiveNumber(G(model, "kv_cache_element_bytes"));
            if (elementBytes == 0) elementBytes = 2.0;
            return Py.Round(bytes * (elementBytes / 2.0) * context / Gib, 2);
        }
        var paramsB = EstimatedParamBillions(model);
        var kvPer32kGb = Math.Min(Math.Max(paramsB * 0.12, 0.35), 3.5);
        return Py.Round(kvPer32kGb * (context / 32768.0), 2);
    }

    public static RuntimeCacheSettings RuntimeProfileCacheSettings(JsonObject? runtimeProfile)
    {
        var env = runtimeProfile is null ? null : Py.Get(runtimeProfile, "env") as JsonObject;
        int? IntSetting(string key)
        {
            // Python: int(str(env.get(key)).strip()), None on failure.
            var text = Py.StrExact(env is null ? null : Py.Get(env, key)).Trim();
            return int.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        string Text(string key)
        {
            var value = env is null ? "" : Py.Str(Py.Get(env, key));
            return value.Length > 0 ? value : "f16";
        }
        var parallel = IntSetting("LLAMA_PARALLEL");
        return new RuntimeCacheSettings(
            CacheTypeK: Text("LLAMA_ARG_CACHE_TYPE_K"),
            CacheTypeV: Text("LLAMA_ARG_CACHE_TYPE_V"),
            CtxCheckpoints: IntSetting("LLAMA_ARG_CTX_CHECKPOINTS"),
            Parallel: parallel is int p && p != 0 ? p : 1);
    }

    /// <summary>A runtime profile's hand-measured estimated_required_gb (0 if none).</summary>
    public static double AuthoredProfileEstimate(JsonObject? runtimeProfile) =>
        runtimeProfile is null ? 0.0 : Py.PositiveNumber(Py.Get(runtimeProfile, "estimated_required_gb"));

    public static MemoryEstimate EstimateForRuntime(
        JsonObject model,
        int? contextLength = null,
        double? weightSizeMb = null,
        JsonObject? runtimeProfile = null)
    {
        if (contextLength is null && runtimeProfile is not null && Py.Truthy(Py.Get(runtimeProfile, "context_length")))
        {
            var profileContext = Py.ToInt(Py.Get(runtimeProfile, "context_length"));
            contextLength = profileContext is long pc ? (int)pc : null;
        }
        var settings = RuntimeProfileCacheSettings(runtimeProfile);
        return EstimateModelMemory(
            model,
            contextLength,
            settings.CacheTypeK,
            settings.CacheTypeV,
            settings.Parallel,
            settings.CtxCheckpoints,
            weightSizeMb);
    }

    /// <summary>The shared selector/activation memory requirement in GiB.</summary>
    public static double RequiredModelMemoryGb(
        JsonObject model,
        int? contextLength = null,
        double? weightSizeMb = null,
        JsonObject? runtimeProfile = null,
        bool includeHostState = false)
    {
        var authored = AuthoredProfileEstimate(runtimeProfile);
        if (authored > 0) return Py.Round(authored, 2);
        var estimate = EstimateForRuntime(model, contextLength, weightSizeMb, runtimeProfile);
        return includeHostState ? estimate.TotalGib : estimate.DeviceGib;
    }

    /// <summary>Free memory an architecture estimate must leave on this memory class.</summary>
    public static double FitMarginGib(double capacityGib, string memoryClass)
    {
        if (memoryClass != "discrete") return 0.0;
        var capacity = double.IsFinite(capacityGib) && capacityGib > 0 ? capacityGib : 0.0;
        return Py.Round(Math.Max(DiscreteFitMarginMinGib, DiscreteFitMarginFraction * capacity), 2);
    }

    public static bool MemoryFits(double requiredGib, double capacityGib, string memoryClass, bool architectureEstimate)
    {
        if (architectureEstimate)
        {
            return requiredGib <= capacityGib - FitMarginGib(capacityGib, memoryClass) + 1e-9;
        }
        return requiredGib <= capacityGib + LegacyFitToleranceGib;
    }

    /// <summary>Contexts to try, largest first, for a model without a runtime profile.</summary>
    public static List<int> ContextCandidates(JsonObject model, int minContext = 0)
    {
        var defaultContext = Py.IntOr(G(model, "context_length"));
        if (Py.PositiveNumber(G(model, "block_count")) == 0) return [Math.Max(defaultContext, 0)];
        var nativeNode = Py.Or(G(model, "max_context_length"), JsonValue.Create(defaultContext));
        var native = Py.ToInt(nativeNode) is long n ? (int)n : defaultContext;
        var target = defaultContext;
        if (minContext != 0 && defaultContext < minContext && minContext <= Math.Max(native, defaultContext))
        {
            target = minContext;
        }
        if (target <= MinimumContext) return [target];
        var choices = new SortedSet<int>(ContextSteps.Where(step => step <= target)) { target };
        return choices.Reverse().ToList();
    }
}
