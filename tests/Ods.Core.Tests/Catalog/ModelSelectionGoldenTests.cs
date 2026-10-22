using System.Globalization;
using System.Text.Json.Nodes;
using Ods.Core.Catalog;

namespace Ods.Core.Tests.Catalog;

/// <summary>
/// Parity with scripts/select-model.py on every unbounded hardware envelope in
/// tests/fixtures/model-selection-envelopes.json. The expected values are the
/// golden file scripts/simulate-model-selection.py writes from the Python
/// selector (profile qwen, --installable-only, --min-context 65536).
/// </summary>
public sealed class ModelSelectionGoldenTests
{
    private static JsonObject Envelopes => Repo.Json("tests", "fixtures", "model-selection-envelopes.json");

    private static JsonObject Golden => Repo.Json("tests", "fixtures", "model-selection-golden.json");

    private static IEnumerable<JsonObject> UnboundedEnvelopes =>
        Envelopes["envelopes"]!.AsArray().OfType<JsonObject>().Where(e => e["ceiling"]!.ToJsonString() is "0" or "\"0\"");

    public static TheoryData<string> EnvelopeIds() => new(UnboundedEnvelopes.Select(e => (string)e["id"]!));

    [Fact]
    public void Covers_every_unbounded_envelope()
    {
        Assert.True(UnboundedEnvelopes.Count() >= 40, "the fixture should keep its broad hardware coverage");
    }

    [Theory]
    [MemberData(nameof(EnvelopeIds))]
    public void Selector_matches_select_model_py(string envelopeId)
    {
        var envelope = UnboundedEnvelopes.Single(e => (string)e["id"]! == envelopeId);
        var expected = Golden[envelopeId]!.AsObject();
        var minContext = (int)Envelopes["min_context"]!;
        var request = new RecommendationRequest(
            Backend: (string)envelope["backend"]!,
            MemoryType: (string)envelope["memory_type"]!,
            VramMb: (double)envelope["vram_mb"]!,
            RamGb: (double)envelope["ram_gb"]!,
            HostArch: (string)envelope["host_arch"]!,
            Tier: envelope["tier"] is JsonValue tier && tier.TryGetValue(out string? text) ? text : envelope["tier"]!.ToJsonString(),
            Profile: "qwen",
            MinContext: minContext);

        if ((string?)expected["pick"] == "ERROR")
        {
            Assert.Throws<NoModelFitsException>(() => ModelRecommender.Recommend(Repo.Catalog.SelectorModels, request));
            return;
        }

        var recommendation = ModelRecommender.Recommend(Repo.Catalog.SelectorModels, request);
        var selected = recommendation.Selected;
        Assert.Equal((string?)expected["pick"], selected.Id);
        Assert.Equal((int)expected["context_length"]!, selected.ContextLength);
        Assert.Equal((string?)expected["runtime_profile"], selected.RuntimeProfileId);
        Assert.Equal((string?)expected["policy"], recommendation.Policy);
        Assert.Equal((double)expected["selector_estimate_gib"]!, selected.RequiredGb);
        Assert.Equal((string?)expected["cache_types"], CacheTypes(selected));
    }

    private static string CacheTypes(Candidate candidate)
    {
        var settings = ModelMemory.RuntimeProfileCacheSettings(candidate.RuntimeProfile);
        return string.Create(CultureInfo.InvariantCulture, $"{settings.CacheTypeK}/{settings.CacheTypeV}");
    }
}
