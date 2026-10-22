using System.Globalization;
using System.Text.Json.Nodes;

namespace Ods.Core.Catalog;

/// <summary>
/// Port of extensions/services/dashboard-api/model_selection.py: curated,
/// context-aware ranking shared by the ODS installers and dashboard. The
/// desktop app must pick the same model the Linux, macOS and Windows/WSL
/// installers pick for the same hardware; the golden-fixture tests check it.
/// </summary>
public static class ModelSelection
{
    public const string Policy = "context-aware-curated-fit-v2";
    public const int DefaultSelectionPriority = 10;
    public const int EvidenceWeight = 5;
    public const double HeadroomRatio = 0.95;
    public const double HeadroomPenalty = 0.25;
    public const int MaxContextCredit = 262144;
    public const int ContextCreditUnit = 65536;

    private static readonly HashSet<string> PositivePixelStatuses = ["verified", "pixel-agent-viable"];
    private static readonly HashSet<string> NegativeEvidenceStatuses = ["not-agent-viable", "unsupported-until-revalidated", "blocked"];
    private static readonly HashSet<string> CpuBackends = ["", "cpu", "none", "unknown"];

    /// <summary>Backend key; none/unknown/empty (Windows no-GPU) mean cpu.</summary>
    public static string NormalizeBackend(string? value)
    {
        var key = Py.NormalizeKey(value);
        return CpuBackends.Contains(key) ? "cpu" : key;
    }

    public static string NormalizeHostArch(string? value)
    {
        var key = Py.NormalizeKey(string.IsNullOrEmpty(value) ? "unknown" : value);
        if (key is "aarch64" or "arm64") return "arm64";
        if (key is "x86-64" or "x86_64" or "amd64" or "x64") return "amd64";
        return key.Length > 0 ? key : "unknown";
    }

    /// <summary>unified (Apple, APUs), cpu (no GPU) or discrete.</summary>
    public static string MemoryClass(string? backend, string? memoryType, double vramMb)
    {
        var backendKey = NormalizeBackend(backend);
        if (backendKey == "apple" || Py.NormalizeKey(memoryType) == "unified") return "unified";
        if (backendKey == "cpu" || vramMb <= 0) return "cpu";
        return "discrete";
    }

    /// <summary>Memory budget for the model, in GiB, and a label for reasons.</summary>
    public static (double CapacityGb, string Label) UsableMemoryGb(string? backend, string? memoryType, double vramMb, double ramGb)
    {
        var mclass = MemoryClass(backend, memoryType, vramMb);
        if (mclass == "unified") return (Math.Max(ramGb * 0.55, 2.0), "unified system memory");
        if (mclass == "cpu") return (Math.Min(Math.Max(ramGb * 0.35, 3.0), 8.0), "system RAM");
        return (vramMb / 1024.0, "GPU VRAM");
    }

    private static IEnumerable<JsonObject> RuntimeProfiles(JsonObject model) =>
        Py.Get(model, "runtime_profiles") is JsonArray profiles ? profiles.OfType<JsonObject>() : [];

    /// <summary>Profiles anchored to this hardware before system-RAM filtering.</summary>
    public static List<JsonObject> HardwareMatchingProfiles(
        JsonObject model, string? backend, string? memoryType, double vramMb, string? hostArch, double? ramGb = null)
    {
        var backendKey = NormalizeBackend(backend);
        var memoryKey = Py.NormalizeKey(memoryType);
        var archKey = NormalizeHostArch(hostArch);
        var vramGb = vramMb / 1024.0;
        var matches = new List<JsonObject>();
        foreach (var profile in RuntimeProfiles(model))
        {
            var profileBackend = Py.Get(profile, "backend");
            if (Py.NormalizeKey(profileBackend).Length > 0 && NormalizeBackend(Py.Str(profileBackend)) != backendKey) continue;
            var allowedArches = Py.ListValue(Py.Get(profile, "host_arch")).Select(NormalizeHostArch).ToHashSet();
            if (allowedArches.Count > 0 && !allowedArches.Contains(archKey)) continue;
            var requiredMemoryType = Py.NormalizeKey(Py.Get(profile, "memory_type"));
            if (requiredMemoryType.Length > 0 && requiredMemoryType != memoryKey) continue;

            var ramMax = Py.Get(profile, "system_ram_max_gb");
            var vramMin = Py.Get(profile, "vram_min_gb");
            var vramMax = Py.Get(profile, "vram_max_gb");
            if (ramGb is double ram && ramMax is not null)
            {
                if (Py.ToFloat(ramMax) is not double max) continue;
                // A RAM ceiling scopes the profile to a class of machines.
                if (ram > max) continue;
            }
            if (vramMin is not null)
            {
                if (Py.ToFloat(vramMin) is not double min) continue;
                if (vramGb < min) continue;
            }
            if (vramMax is not null)
            {
                if (Py.ToFloat(vramMax) is not double max) continue;
                if (vramGb > max) continue;
            }
            matches.Add(profile);
        }
        return matches;
    }

    public static JsonObject? MatchingRuntimeProfile(
        JsonObject model, string? backend, string? memoryType, double vramMb, double ramGb, string? hostArch)
    {
        foreach (var profile in HardwareMatchingProfiles(model, backend, memoryType, vramMb, hostArch, ramGb))
        {
            var ramMin = Py.Get(profile, "system_ram_min_gb");
            var ramMax = Py.Get(profile, "system_ram_max_gb");
            if (ramMin is not null)
            {
                if (Py.ToFloat(ramMin) is not double min) continue;
                if (ramGb < min) continue;
            }
            if (ramMax is not null)
            {
                if (Py.ToFloat(ramMax) is not double max) continue;
                if (ramGb > max) continue;
            }
            return profile;
        }
        return null;
    }

    public static bool FamilyAllowed(JsonObject model, string profile)
    {
        var family = Py.NormalizeKey(Py.Get(model, "family"));
        if (profile == "gemma4")
        {
            // Keep the tiny Qwen bootstrap fallback for the minimum tier.
            return family == "gemma4" || Py.Str(Py.Get(model, "id")) == "qwen3.5-2b-q4";
        }
        return family != "gemma4";
    }

    public static bool InstallRecommendationAllowed(JsonObject model) =>
        Py.Truthy(Py.Get(model, "gguf_url"))
        && Py.ValueEnabled(model.ContainsKey("install_recommendation") ? Py.Get(model, "install_recommendation") : JsonValue.Create(true));

    private static string Status(JsonObject model, string flatKey, string appKey)
    {
        if (model.ContainsKey(flatKey)) return Py.NormalizeKey(Py.Get(model, flatKey));
        var entry = (Py.Get(model, "app_compatibility") as JsonObject) is JsonObject compatibility
            ? Py.Get(compatibility, appKey) as JsonObject
            : null;
        return entry is null ? "" : Py.NormalizeKey(Py.Get(entry, "status"));
    }

    public static string PixelAgentStatus(JsonObject model) => Status(model, "pixel_agent_status", "pixel_agent");

    public static string AgentViabilityStatus(JsonObject model) => Status(model, "agent_viability_status", "agent_viability");

    /// <summary>Require an explicit real-Pixel capability verdict for the Pixel route.</summary>
    public static bool PixelAgentReady(JsonObject model) => PositivePixelStatuses.Contains(PixelAgentStatus(model));

    public static int EvidenceAdjustment(JsonObject model)
    {
        var pixel = PixelAgentStatus(model);
        var agent = AgentViabilityStatus(model);
        var adjustment = PositivePixelStatuses.Contains(pixel) ? EvidenceWeight : 0;
        if (NegativeEvidenceStatuses.Contains(pixel) || NegativeEvidenceStatuses.Contains(agent)) adjustment -= EvidenceWeight;
        return adjustment;
    }

    private static JsonObject? SelectionBlock(JsonObject model) => Py.Get(model, "selection") as JsonObject;

    /// <summary>Curated priority for this memory class; 0 means never auto-selected.</summary>
    public static int SelectionPriority(JsonObject model, string mclass)
    {
        var selection = SelectionBlock(model);
        if (selection is null || selection.Count == 0) return DefaultSelectionPriority;
        if (!selection.ContainsKey(mclass)) return DefaultSelectionPriority;
        return Py.ToInt(Py.Get(selection, mclass)) is long value ? (int)value : DefaultSelectionPriority;
    }

    public static double MinimumCapacityGib(JsonObject model, string mclass)
    {
        if (SelectionBlock(model) is not JsonObject selection || Py.Get(selection, "min_capacity_gib") is not JsonObject floors) return 0.0;
        var value = Py.Get(floors, mclass);
        if (!Py.Truthy(value)) return 0.0;
        return Py.ToFloat(value) ?? 0.0;
    }

    /// <summary>True if the model respects an optional tier size ceiling (0 = none).</summary>
    public static bool SizeWithinCeiling(JsonObject model, double maxSizeMb)
    {
        if (maxSizeMb <= 0) return true;
        var sizeMb = Py.Truthy(Py.Get(model, "size_mb")) ? Py.ToFloat(Py.Get(model, "size_mb")) ?? 0.0 : 0.0;
        return sizeMb <= maxSizeMb + Math.Max(maxSizeMb * 0.02, 64.0);
    }

    public static double WeightsGib(JsonObject model)
    {
        var sizeBytes = Py.Truthy(Py.Get(model, "size_bytes")) ? Py.ToFloat(Py.Get(model, "size_bytes")) ?? 0.0 : 0.0;
        if (sizeBytes > 0) return sizeBytes / (1024.0 * 1024.0 * 1024.0);
        var sizeMb = Py.Truthy(Py.Get(model, "size_mb")) ? Py.ToFloat(Py.Get(model, "size_mb")) ?? 0.0 : 0.0;
        return sizeMb / 1024.0;
    }

    /// <summary>The one fit gate for a planned candidate.</summary>
    public static bool CandidateFits(Candidate candidate) =>
        ModelMemory.MemoryFits(candidate.RequiredGb, candidate.CapacityGb, candidate.MemoryClass, candidate.ArchitectureEstimate);

    /// <summary>Fit the model to this hardware, or null when it cannot run here.</summary>
    public static Candidate? PlanCandidate(
        JsonObject model,
        double capacityGb,
        string mclass,
        string? backend,
        string? memoryType,
        double vramMb,
        double ramGb,
        string? hostArch,
        int minContext = 0,
        int? priority = null)
    {
        var hardwareProfiles = HardwareMatchingProfiles(model, backend, memoryType, vramMb, hostArch, ramGb);
        var runtimeProfile = MatchingRuntimeProfile(model, backend, memoryType, vramMb, ramGb, hostArch);
        if (runtimeProfile is null && hardwareProfiles.Count > 0) return null;
        var includeHost = mclass == "cpu";
        var margin = ModelMemory.FitMarginGib(capacityGb, mclass);
        var basePriority = priority ?? SelectionPriority(model, mclass);
        var evidence = EvidenceAdjustment(model);

        Candidate Build(int context, MemoryEstimate estimate, double required, bool authored)
        {
            var architecture = !authored && estimate.IsArchitecture;
            return new Candidate
            {
                Model = model,
                RuntimeProfile = runtimeProfile,
                ContextLength = context,
                RequiredGb = Py.Round(required, 2),
                Estimate = estimate,
                ArchitectureEstimate = architecture,
                AuthoredEstimate = authored,
                MeetsMinContext = minContext == 0 || context >= minContext,
                MemoryClass = mclass,
                CapacityGb = capacityGb,
                FitMarginGb = architecture ? margin : -ModelMemory.LegacyFitToleranceGib,
                Priority = basePriority,
                Evidence = evidence,
            };
        }

        if (runtimeProfile is not null)
        {
            var contextNode = Py.Or(Py.Or(Py.Get(runtimeProfile, "context_length"), Py.Get(model, "context_length")), JsonValue.Create(0));
            var context = Py.ToInt(contextNode) is long parsed ? (int)parsed : Py.IntOr(Py.Get(model, "context_length"));
            var estimate = ModelMemory.EstimateForRuntime(model, context, runtimeProfile: runtimeProfile);
            var authored = ModelMemory.AuthoredProfileEstimate(runtimeProfile);
            var required = authored > 0 ? authored : (includeHost ? estimate.TotalGib : estimate.DeviceGib);
            var candidate = Build(context, estimate, required, authored > 0);
            return CandidateFits(candidate) ? candidate : null;
        }

        var contexts = ModelMemory.ContextCandidates(model, minContext);
        var ordered = contexts.Where(c => c >= minContext).Concat(contexts.Where(c => c < minContext));
        foreach (var context in ordered)
        {
            var estimate = ModelMemory.EstimateModelMemory(model, context);
            var required = includeHost ? estimate.TotalGib : estimate.DeviceGib;
            var candidate = Build(context, estimate, required, false);
            if (CandidateFits(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The catalog's declared native maximum context, or 0 when undeclared.</summary>
    public static int DeclaredMaxContext(JsonObject model)
    {
        if (Py.Get(model, "native_context_declared") is JsonValue declared
            && declared.GetValueKind() == System.Text.Json.JsonValueKind.False)
        {
            return 0;
        }
        return IntOrZero(Py.Get(model, "max_context_length"));
    }

    private static int IntOrZero(JsonNode? value)
    {
        if (value is JsonValue v && v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) return 0;
        return Math.Max(Py.IntOr(value), 0);
    }

    /// <summary>The context to serve one model at on this hardware (dashboard switch, restore, installer).</summary>
    public static ContextPlan PlanModelContext(
        JsonObject model,
        double capacityGb,
        string? backend,
        string? memoryType,
        double vramMb,
        double ramGb,
        string? hostArch,
        int minContext = 0,
        int? preferredContext = null)
    {
        var mclass = MemoryClass(backend, memoryType, vramMb);
        var defaultContext = IntOrZero(Py.Get(model, "context_length"));
        var declaredMax = DeclaredMaxContext(model);
        var native = declaredMax != 0 ? declaredMax : defaultContext;
        var preferred = Math.Max(preferredContext ?? 0, 0);
        if (declaredMax != 0 && preferred > declaredMax) preferred = declaredMax;
        var planned = model;
        if (preferred != 0 && preferred != defaultContext)
        {
            planned = (JsonObject)model.DeepClone();
            planned["context_length"] = preferred;
            planned["max_context_length"] = Math.Max(native, preferred);
        }
        var floor = Math.Max(minContext, 0);
        var candidate = PlanCandidate(planned, capacityGb, mclass, backend, memoryType, vramMb, ramGb, hostArch, floor);
        if (candidate is null)
        {
            var context = preferred != 0 ? preferred : defaultContext;
            return new ContextPlan(context, false, floor == 0 || context >= floor, floor, native, mclass,
                Py.Round(capacityGb, 2), null, null, null);
        }
        return new ContextPlan(
            candidate.ContextLength,
            true,
            candidate.MeetsMinContext,
            floor,
            Math.Max(native, candidate.ContextLength),
            mclass,
            Py.Round(candidate.CapacityGb, 2),
            candidate.RequiredGb,
            candidate.RuntimeProfileId,
            candidate.EstimateSource);
    }

    /// <summary>Lexicographic rank key, highest first.</summary>
    internal static (int Family, int MeetsMin, int Priority, double Context, double Size) RankKey(
        Candidate candidate, string profile, bool includeSizeTiebreak = true)
    {
        var familyMatch = profile == "gemma4" && Py.NormalizeKey(Py.Get(candidate.Model, "family")) == "gemma4" ? 1 : 0;
        var contextCredit = Math.Min(candidate.ContextLength, MaxContextCredit) / (double)ContextCreditUnit;
        var headroom = candidate.RequiredGb / Math.Max(candidate.CapacityGb, 1.0) > HeadroomRatio;
        return (
            familyMatch,
            candidate.MeetsMinContext ? 1 : 0,
            candidate.Priority + candidate.Evidence,
            Py.Round(contextCredit - (headroom ? HeadroomPenalty : 0.0), 6),
            includeSizeTiebreak ? Py.Round(WeightsGib(candidate.Model), 6) : 0.0);
    }

    /// <summary>Rank every eligible catalog model that fits this hardware.</summary>
    public static List<Candidate> RankCatalogModels(
        IReadOnlyList<JsonObject> catalog,
        double capacityGb,
        string profile,
        bool installableOnly,
        string? backend,
        string? memoryType,
        double vramMb,
        double ramGb,
        string? hostArch,
        double maxSizeMb = 0,
        bool agentReadyOnly = false,
        int minContext = 0,
        bool requireMinContext = false,
        bool includeSizeTiebreak = true)
    {
        var mclass = MemoryClass(backend, memoryType, vramMb);

        List<Candidate> Pool(bool enforceCeiling)
        {
            var pool = new List<Candidate>();
            foreach (var model in catalog)
            {
                if (installableOnly && !InstallRecommendationAllowed(model)) continue;
                if (agentReadyOnly && !PixelAgentReady(model)) continue;
                if (!FamilyAllowed(model, profile)) continue;
                if (enforceCeiling && !SizeWithinCeiling(model, maxSizeMb)) continue;
                var priority = SelectionPriority(model, mclass);
                if (priority <= 0) continue;
                if (capacityGb < MinimumCapacityGib(model, mclass)) continue;
                var candidate = PlanCandidate(model, capacityGb, mclass, backend, memoryType, vramMb, ramGb, hostArch, minContext, priority);
                if (candidate is null) continue;
                if (requireMinContext && !candidate.MeetsMinContext) continue;
                pool.Add(candidate);
            }
            // Python sorts with reverse=True, which keeps equal keys in input order.
            return pool.OrderByDescending(item => RankKey(item, profile, includeSizeTiebreak)).ToList();
        }

        var ranked = Pool(enforceCeiling: true);
        if (ranked.Count == 0 && maxSizeMb > 0) ranked = Pool(enforceCeiling: false);
        return ranked;
    }

    /// <summary>Would the model fit at this context? (model_selection.check_fit)</summary>
    public static FitCheck CheckFit(JsonObject model, int contextLength, double capacityGb, string mclass, JsonObject? runtimeProfile = null)
    {
        var nativeMax = DeclaredMaxContext(model);
        var aboveNativeMax = nativeMax != 0 && contextLength > nativeMax;
        var estimate = ModelMemory.EstimateForRuntime(model, contextLength, runtimeProfile: runtimeProfile);
        var authored = 0.0;
        if (runtimeProfile is not null)
        {
            var profileContext = Py.IntOr(Py.Get(runtimeProfile, "context_length"));
            if (profileContext == contextLength) authored = ModelMemory.AuthoredProfileEstimate(runtimeProfile);
        }
        var required = authored > 0 ? authored : (mclass == "cpu" ? estimate.TotalGib : estimate.DeviceGib);
        var architecture = authored == 0 && estimate.IsArchitecture;
        var margin = architecture ? ModelMemory.FitMarginGib(capacityGb, mclass) : -ModelMemory.LegacyFitToleranceGib;
        var fits = ModelMemory.MemoryFits(Py.Round(required, 2), capacityGb, mclass, architecture);
        return new FitCheck(
            fits && !aboveNativeMax,
            Py.Str(Py.Get(model, "id")),
            contextLength,
            nativeMax != 0 ? nativeMax : null,
            aboveNativeMax,
            runtimeProfile is null ? null : Py.Str(Py.Get(runtimeProfile, "id")),
            Py.Round(required, 2),
            Py.Round(capacityGb, 2),
            mclass,
            margin,
            authored > 0 ? "runtime-profile" : estimate.Method,
            estimate);
    }

    internal static string F1(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
}
