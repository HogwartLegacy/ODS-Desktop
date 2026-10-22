using System.Text.Json.Nodes;

namespace Ods.Core.Catalog;

/// <summary>Inputs of scripts/select-model.py.</summary>
public sealed record RecommendationRequest(
    string Backend,
    string MemoryType,
    double VramMb,
    double RamGb,
    string HostArch,
    string Tier = "1",
    string Profile = "qwen",
    double MaxSizeMb = 0,
    bool InstallableOnly = true,
    bool AgentReadyOnly = false,
    int MinContext = ModelRecommender.HermesMinContext,
    bool RequireMinContext = false);

/// <summary>The select-model.py payload: the pick, why, and its alternatives.</summary>
public sealed record Recommendation(
    string Policy,
    string Source,
    string Confidence,
    string Profile,
    string HostArch,
    double MemoryCapacityGb,
    string MemoryLabel,
    string MemoryClass,
    int MinContext,
    Candidate Selected,
    string Reason,
    IReadOnlyList<Candidate> Alternatives)
{
    public bool MeetsMinContext => Selected.MeetsMinContext;
}

public sealed class NoModelFitsException(string message) : Exception(message);

/// <summary>
/// Port of scripts/select-model.py main(): profile resolution, the
/// architecture policies that route around qwen3-coder-next on unified memory
/// and Spark-class arm64 hosts, alternatives and the reason text.
/// </summary>
public static class ModelRecommender
{
    /// <summary>The 64K floor every ODS installer passes (Hermes and agents need it).</summary>
    public const int HermesMinContext = 65536;

    public const string PixelAgentPolicy = "pixel-agent-capability-v1";
    public const string SparkAarch64Policy = "spark-aarch64-nv-ultra-a3b-v1";
    public const string SparkAarch64ModelId = "qwen3.6-35b-a3b-ud-q4";
    public const string UnifiedMemoryPolicy = "unified-memory-coder-next-a3b-v1";

    public static string NormalizeProfile(string? value)
    {
        var key = Py.NormalizeKey(string.IsNullOrEmpty(value) ? "qwen" : value);
        if (key is "gemma" or "gemma4" or "gemma-4") return "gemma4";
        return key == "auto" ? "auto" : "qwen";
    }

    public static string EffectiveProfile(string profile, string backend, string tier)
    {
        if (profile != "auto") return profile;
        if (Py.NormalizeKey(tier) is "cloud" or "0" or "t0") return "qwen";
        return Py.NormalizeKey(backend) is "apple" or "nvidia" or "sycl" ? "gemma4" : "qwen";
    }

    public static Recommendation Recommend(IReadOnlyList<JsonObject> catalog, RecommendationRequest request)
    {
        if (catalog.Count == 0) throw new NoModelFitsException("The model catalog is empty (no usable models).");
        var profile = EffectiveProfile(NormalizeProfile(request.Profile), request.Backend, request.Tier);
        var (capacityGb, memoryLabel) = ModelSelection.UsableMemoryGb(request.Backend, request.MemoryType, request.VramMb, request.RamGb);
        var mclass = ModelSelection.MemoryClass(request.Backend, request.MemoryType, request.VramMb);
        var confidence = request.Backend is not ("unknown" or "none") && capacityGb > 0 ? "high" : "medium";
        var minContext = Math.Max(request.MinContext, 0);

        List<Candidate> Rank(IReadOnlyList<JsonObject> models, double maxSize, bool agentOnly) => ModelSelection.RankCatalogModels(
            models, capacityGb, profile, request.InstallableOnly, request.Backend, request.MemoryType,
            request.VramMb, request.RamGb, request.HostArch, maxSize, agentOnly, minContext, request.RequireMinContext);

        var ranked = Rank(catalog, request.MaxSizeMb, request.AgentReadyOnly);
        if (ranked.Count == 0)
        {
            var message = request.AgentReadyOnly
                ? "No explicitly verified Pixel agent model fits the detected hardware."
                : request.RequireMinContext && minContext > 0
                    ? $"No installable model fits the detected hardware at {minContext} context."
                    : "No installable model fits the detected hardware runtime profiles.";
            throw new NoModelFitsException(message);
        }

        Candidate? archSelected = null;
        string? archPolicyTag = null;
        if (!request.AgentReadyOnly)
        {
            var (archModel, tag) = ArchPolicyModel(catalog, request.Tier, profile, request.HostArch, request.MemoryType,
                request.InstallableOnly, ranked[0].Model, request.Backend);
            if (archModel is not null)
            {
                var archCandidates = Rank([archModel], 0, false);
                if (archCandidates.Count > 0)
                {
                    archSelected = archCandidates[0];
                    archPolicyTag = tag;
                }
            }
        }

        Candidate selected;
        List<Candidate> alternatives;
        string policy, source, reason;
        if (archSelected is not null)
        {
            selected = archSelected;
            alternatives = [selected, .. ranked.Where(c => c.Id != selected.Id && !IsUnifiedExcludedModel(c.Model)).Take(2)];
            policy = $"{ModelSelection.Policy}+{archPolicyTag}";
            source = "catalog_arch_policy_pre_download";
            reason = ArchPolicyReason(selected, capacityGb, memoryLabel, archPolicyTag!);
        }
        else
        {
            selected = ranked[0];
            alternatives = ranked.Take(3).ToList();
            policy = request.AgentReadyOnly ? $"{ModelSelection.Policy}+{PixelAgentPolicy}" : ModelSelection.Policy;
            source = selected.RuntimeProfile is not null ? "catalog_runtime_profile_pre_download" : "catalog_fit_pre_download";
            reason = RecommendationReason(selected, capacityGb, memoryLabel, request.Backend);
            if (request.AgentReadyOnly) reason += " Pixel default selection requires an explicit verified Pixel capability verdict.";
            if (request.MaxSizeMb > 0)
            {
                reason += ModelSelection.SizeWithinCeiling(selected.Model, request.MaxSizeMb)
                    ? $" Bounded by tier {request.Tier}'s model size ceiling ({Py.FormatG(request.MaxSizeMb)}MB)."
                    : $" No installable model fits beneath tier {request.Tier}'s {Py.FormatG(request.MaxSizeMb)}MB model size preference, so it was relaxed.";
            }
        }
        if (minContext > 0 && !selected.MeetsMinContext)
        {
            reason += $" No installable model fits this hardware at the {minContext} context floor; this is the largest context that fits.";
        }

        return new Recommendation(
            policy, source, confidence, profile, ModelSelection.NormalizeHostArch(request.HostArch),
            Py.Round(capacityGb, 1), memoryLabel, mclass, minContext, selected, reason, alternatives);
    }

    /// <summary>select-model.py arch_policy_model: (model, policy tag) or (null, null).</summary>
    internal static (JsonObject? Model, string? Policy) ArchPolicyModel(
        IReadOnlyList<JsonObject> catalog, string tier, string profile, string hostArch, string memoryType,
        bool installableOnly, JsonObject? selectedModel, string backend)
    {
        if (profile != "qwen") return (null, null);
        var isSparkAarch64 = Py.NormalizeKey(tier) == "nv-ultra" && ModelSelection.NormalizeHostArch(hostArch) == "arm64";
        var isUnified = Py.NormalizeKey(memoryType) == "unified";
        var isStrixLarge = isUnified && Py.NormalizeKey(tier) == "sh-large" && ModelSelection.NormalizeBackend(backend) == "amd";
        var isUnifiedCoderNext = isUnified && selectedModel is not null && IsUnifiedExcludedModel(selectedModel);
        if (!(isSparkAarch64 || isStrixLarge || isUnifiedCoderNext)) return (null, null);

        foreach (var model in catalog)
        {
            if (installableOnly && !ModelSelection.InstallRecommendationAllowed(model)) continue;
            if (Py.NormalizeKey(Py.Get(model, "id")) == Py.NormalizeKey(SparkAarch64ModelId))
            {
                return (model, isSparkAarch64 ? SparkAarch64Policy : UnifiedMemoryPolicy);
            }
        }
        return (null, null);
    }

    /// <summary>The coder-next entry ODS routes around on unified-memory backends.</summary>
    internal static bool IsUnifiedExcludedModel(JsonObject model) =>
        Py.NormalizeKey(Py.Get(model, "llm_model_name")) == "qwen3-coder-next";

    public static string RequirementBreakdown(Candidate candidate)
    {
        if (candidate.EstimateSource == "architecture")
        {
            var e = candidate.Estimate;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>
            {
                string.Create(invariant, $"weights {e.WeightsGib:F2}"),
                string.Create(invariant, $"KV {e.KvGib:F2}"),
            };
            if (e.RecurrentStateGib != 0) parts.Add(string.Create(invariant, $"recurrent state {e.RecurrentStateGib:F2}"));
            parts.Add(string.Create(invariant, $"overhead {e.ComputeOverheadGib:F2}"));
            if (candidate.MemoryClass == "cpu" && e.HostCheckpointGib != 0)
            {
                parts.Add(string.Create(invariant, $"context checkpoints {e.HostCheckpointGib:F2}"));
            }
            return string.Join(" + ", parts);
        }
        return candidate.EstimateSource == "runtime-profile"
            ? "measured runtime-profile budget"
            : "catalog estimate including context/KV";
    }

    private static string MarginText(Candidate candidate)
    {
        if (candidate.FitMarginGb < 0) return $"within the {Py.FormatG(Math.Abs(candidate.FitMarginGb))} GiB catalog tolerance";
        if (candidate.FitMarginGb > 0) return $"leaving at least {Py.FormatG(candidate.FitMarginGb)} GiB free";
        return "within budget";
    }

    private static string RecommendationReason(Candidate candidate, double capacityGb, string memoryLabel, string backend)
    {
        var name = Py.Str(Py.Get(candidate.Model, "name"));
        var contextK = candidate.ContextLength / 1024;
        var required = Py.FormatG(candidate.RequiredGb);
        var capacity = ModelSelection.F1(capacityGb);
        if (candidate.RuntimeProfile is JsonObject profile)
        {
            var label = Py.Str(Py.Or(Py.Get(profile, "label"), Py.Get(profile, "id")));
            if (label.Length == 0) label = "advanced runtime profile";
            var runtime = Py.Str(Py.Get(profile, "runtime")) is { Length: > 0 } r ? r : "llama.cpp";
            var ramNote = Py.Get(profile, "system_ram_min_gb") is JsonNode ramMin ? $" plus {Py.StrExact(ramMin)}GB system RAM" : "";
            return $"Curated runtime fit ({ModelSelection.Policy}): {name} is the highest-priority installable model for "
                + $"{candidate.MemoryClass} memory; it uses {label} via {runtime}, needs about {required} GiB "
                + $"({RequirementBreakdown(candidate)}){ramNote}, fits {capacity} GiB {memoryLabel} on {backend} "
                + $"({MarginText(candidate)}), and gives {contextK}K context. Throughput still requires a local benchmark after first launch.";
        }
        return $"Curated fit ({ModelSelection.Policy}): {name} is the highest-priority installable model for "
            + $"{candidate.MemoryClass} memory that fits {capacity} GiB {memoryLabel} on {backend} ({MarginText(candidate)}) "
            + $"at {contextK}K context; it needs about {required} GiB ({RequirementBreakdown(candidate)}). "
            + "Throughput requires a local benchmark after first launch.";
    }

    private static string ArchPolicyReason(Candidate candidate, double capacityGb, string memoryLabel, string policyTag)
    {
        var name = Py.Str(Py.Get(candidate.Model, "name"));
        var rationale = policyTag == UnifiedMemoryPolicy
            ? "is selected for unified-memory hosts (e.g. Strix Halo, future AMD/NV unified-memory tiers) because qwen3-coder-next produces all-`?` tokens on unified-memory backends"
            : "is selected for arm64 NV_ULTRA Spark-class NVIDIA hosts because qwen3-coder-next is excluded on this architecture by the tier map";
        return $"Arch-aware catalog policy ({policyTag}): {name} {rationale}. It needs about {Py.FormatG(candidate.RequiredGb)} GiB "
            + $"({RequirementBreakdown(candidate)}), fits {ModelSelection.F1(capacityGb)} GiB {memoryLabel}, and gives "
            + $"{candidate.ContextLength / 1024}K context. Throughput requires a local benchmark after first launch.";
    }
}
