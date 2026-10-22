using System.Text.Json.Nodes;
using Ods.Core.Catalog;

namespace Ods.Core.Tests.Catalog;

/// <summary>
/// The cases ODS's Windows selector suite (tests/test-windows-catalog-selector.ps1)
/// pins, run against the C# port.
/// </summary>
public sealed class ModelMemoryTests
{
    private static JsonObject Normalized(string id) =>
        Repo.Catalog.Entries.Single(e => e.Id == id).Normalized
        ?? throw new InvalidOperationException($"{id} is not a selector model.");

    [Fact]
    public void Phi4_mini_full_context_kv_is_not_underestimated()
    {
        Assert.Equal(15.62, ModelMemory.EstimatedContextKvGb(Normalized("phi4-mini-q4")));
    }

    [Theory]
    [InlineData("nvidia", 4, 8192)]
    [InlineData("nvidia", 8, 32768)]
    [InlineData("nvidia", 16, 65536)]
    [InlineData("nvidia", 24, 128000)]
    [InlineData("amd", 8, 32768)]
    [InlineData("sycl", 16, 65536)]
    public void Context_steps_down_until_the_architecture_estimate_fits(string backend, int vramGib, int expectedContext)
    {
        var phi = (JsonObject)Normalized("phi4-mini-q4").DeepClone();
        phi["install_recommendation"] = true;
        var request = new RecommendationRequest(backend, "discrete", vramGib * 1024, 32, "amd64", MinContext: 0);

        var recommendation = ModelRecommender.Recommend([phi], request);

        Assert.Equal("phi-4-mini", (string?)recommendation.Selected.Model["llm_model_name"]);
        Assert.Equal(expectedContext, recommendation.Selected.ContextLength);
        Assert.Equal(128000, (int)Normalized("phi4-mini-q4")["context_length"]!);
    }

    [Fact]
    public void No_fit_is_refused_instead_of_falling_back()
    {
        var phi = (JsonObject)Normalized("phi4-mini-q4").DeepClone();
        phi["install_recommendation"] = true;
        var request = new RecommendationRequest("nvidia", "discrete", 1024, 32, "amd64", MinContext: 0);

        Assert.Throws<NoModelFitsException>(() => ModelRecommender.Recommend([phi], request));
    }

    [Fact]
    public void Runtime_profile_ram_gates_keep_their_safety_anchor()
    {
        var model = new JsonObject
        {
            ["runtime_profiles"] = new JsonArray(new JsonObject
            {
                ["backend"] = "nvidia",
                ["system_ram_min_gb"] = 16,
                ["system_ram_max_gb"] = 32,
            }),
        };

        Assert.Null(ModelSelection.MatchingRuntimeProfile(model, "nvidia", "discrete", 8192, 8, "amd64"));
        Assert.NotEmpty(ModelSelection.HardwareMatchingProfiles(model, "nvidia", "discrete", 8192, "amd64", 8));
        Assert.Empty(ModelSelection.HardwareMatchingProfiles(model, "nvidia", "discrete", 8192, "amd64", 64));
    }

    [Fact]
    public void Hermes_floor_fit_check_matches_the_windows_installer()
    {
        var qwen27 = Normalized("qwen3.5-27b-q4");
        Assert.False(ModelSelection.CheckFit(qwen27, 65536, 20475 / 1024.0, "discrete").Fits);
        Assert.True(ModelSelection.CheckFit(qwen27, 65536, 32607 / 1024.0, "discrete").Fits);

        var phi4 = Normalized("phi4-q4");
        var aboveNative = ModelSelection.CheckFit(phi4, 65536, 32607 / 1024.0, "discrete");
        Assert.False(aboveNative.Fits);
        Assert.True(aboveNative.AboveNativeMax);
        Assert.True(ModelSelection.CheckFit(phi4, 16384, 32607 / 1024.0, "discrete").Fits);
    }

    [Fact]
    public void Required_floor_reselects_the_measured_q8_kv_profile()
    {
        var request = new RecommendationRequest("nvidia", "discrete", 20475, 64, "amd64", Tier: "3", RequireMinContext: true);

        var recommendation = ModelRecommender.Recommend(Repo.Catalog.SelectorModels, request);

        Assert.Equal("nvidia-20gb-64k-q8-kv", recommendation.Selected.RuntimeProfileId);
        Assert.Equal(65536, recommendation.Selected.ContextLength);
        Assert.Equal("q8_0", ModelMemory.RuntimeProfileCacheSettings(recommendation.Selected.RuntimeProfile).CacheTypeK);
    }

    [Fact]
    public void Required_floor_refuses_when_nothing_fits_at_64k()
    {
        var request = new RecommendationRequest("nvidia", "discrete", 2048, 16, "amd64", Tier: "0", RequireMinContext: true);

        var error = Assert.Throws<NoModelFitsException>(() => ModelRecommender.Recommend(Repo.Catalog.SelectorModels, request));
        Assert.Contains("at 65536 context", error.Message);
    }

    [Theory]
    [InlineData(2.675, 2, 2.67)]
    [InlineData(0.125, 2, 0.12)]
    [InlineData(0.375, 2, 0.38)]
    [InlineData(2.5, 0, 2.0)]
    [InlineData(3.5, 0, 4.0)]
    [InlineData(-0.125, 2, -0.12)]
    public void Rounding_matches_python(double value, int digits, double expected)
    {
        Assert.Equal(expected, Py.Round(value, digits));
    }
}
