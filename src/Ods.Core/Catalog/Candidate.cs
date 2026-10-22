using System.Text.Json.Nodes;

namespace Ods.Core.Catalog;

/// <summary>One catalog model planned for one hardware envelope (model_selection.Candidate).</summary>
public sealed class Candidate
{
    public required JsonObject Model { get; init; }
    public JsonObject? RuntimeProfile { get; init; }
    public required int ContextLength { get; init; }
    public required double RequiredGb { get; init; }
    public required MemoryEstimate Estimate { get; init; }
    public required bool ArchitectureEstimate { get; init; }
    public required bool AuthoredEstimate { get; init; }
    public required bool MeetsMinContext { get; init; }
    public required string MemoryClass { get; init; }
    public required double CapacityGb { get; init; }
    public required double FitMarginGb { get; init; }
    public required int Priority { get; init; }
    public required int Evidence { get; init; }

    public string Id => Py.Str(Py.Get(Model, "id"));

    public string? RuntimeProfileId => RuntimeProfile is null ? null : Py.Str(Py.Get(RuntimeProfile, "id"));

    /// <summary>"runtime-profile" for a hand-measured budget, else the estimator's method.</summary>
    public string EstimateSource => AuthoredEstimate ? "runtime-profile" : Estimate.Method;
}

/// <summary>The context to serve one model at on this hardware (model_selection.plan_model_context).</summary>
public sealed record ContextPlan(
    int ContextLength,
    bool Fits,
    bool MeetsMinContext,
    int MinContext,
    int MaxContextLength,
    string MemoryClass,
    double CapacityGb,
    double? RequiredGb,
    string? RuntimeProfile,
    string? EstimateSource);

/// <summary>Result of model_selection.check_fit.</summary>
public sealed record FitCheck(
    bool Fits,
    string ModelId,
    int ContextLength,
    int? MaxContextLength,
    bool AboveNativeMax,
    string? RuntimeProfile,
    double RequiredGb,
    double CapacityGb,
    string MemoryClass,
    double MarginGb,
    string EstimateSource,
    MemoryEstimate Estimate);
