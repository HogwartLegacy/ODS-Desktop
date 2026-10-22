using System.Text.Json.Nodes;
using Ods.Core.Catalog;
using Ods.Core.Diagnostics;
using Ods.Core.Downloads;
using Ods.Core.Models;
using Ods.Core.Platform;
using Ods.Core.Runtime;
using Ods.Core.Settings;

namespace Ods.Core;

/// <summary>How one model would run here: the context, runtime profile and whether it fits.</summary>
public sealed record ModelPlan(CatalogEntry Entry, int ContextLength, JsonObject? RuntimeProfile, double? RequiredGb, double CapacityGb, bool Fits)
{
    public string? RuntimeProfileId => RuntimeProfile is null ? null : (string?)RuntimeProfile["id"];
}

public sealed partial class OdsEngine
{
    /// <summary>ODS's fast-start model (installers/lib/bootstrap-model.sh).</summary>
    public const string BootstrapModelId = "qwen3.5-2b-q4";

    public const int BootstrapContext = 65536;

    /// <summary>The selector envelope for the qualified runtime, or the planned one before setup.</summary>
    public SelectorEnvelope Envelope
    {
        get
        {
            var hardware = Hardware ?? throw new InvalidOperationException("Probe the hardware first.");
            var flavor = Runtime?.Flavor ?? Plan!.Preferred;
            return AccelerationPlanner.EnvelopeFor(flavor, hardware, Plan!.Tier);
        }
    }

    /// <summary>The model ODS's installers would pick for this PC (scripts/select-model.py policy).</summary>
    public Recommendation Recommend(SelectorEnvelope? envelope = null)
    {
        var e = envelope ?? Envelope;
        var request = new RecommendationRequest(e.Backend, e.MemoryType, e.VramMb, e.RamGb, e.HostArch, e.Tier);
        return ModelRecommender.Recommend(Catalog.SelectorModels, request);
    }

    /// <summary>Every catalog model ranked for this PC (the Models page list).</summary>
    public IReadOnlyList<Candidate> RankedModels()
    {
        var e = Envelope;
        var (capacity, _) = ModelSelection.UsableMemoryGb(e.Backend, e.MemoryType, e.VramMb, e.RamGb);
        return ModelSelection.RankCatalogModels(Catalog.SelectorModels, capacity, "qwen", installableOnly: false, e.Backend, e.MemoryType,
            e.VramMb, e.RamGb, e.HostArch, minContext: ModelRecommender.HermesMinContext);
    }

    /// <summary>The context and profile to serve a model at (model_selection.plan_model_context).</summary>
    public ModelPlan PlanFor(CatalogEntry entry, int? preferredContext = null)
    {
        var model = entry.Normalized ?? throw new InvalidOperationException($"{entry.Name} has no GGUF file to plan.");
        var e = Envelope;
        var (capacity, _) = ModelSelection.UsableMemoryGb(e.Backend, e.MemoryType, e.VramMb, e.RamGb);
        var plan = ModelSelection.PlanModelContext(model, capacity, e.Backend, e.MemoryType, e.VramMb, e.RamGb, e.HostArch,
            ModelRecommender.HermesMinContext, preferredContext);
        var profile = plan.RuntimeProfile is string id
            ? (model["runtime_profiles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => (string?)p["id"] == id)
            : null;
        return new ModelPlan(entry, plan.ContextLength, profile, plan.RequiredGb, capacity, plan.Fits);
    }

    public CatalogEntry FindModel(string id)
    {
        if (id.StartsWith(ModelCatalog.LocalPrefix, StringComparison.Ordinal))
        {
            var path = Models.PathFor(id[ModelCatalog.LocalPrefix.Length..]);
            if (!File.Exists(path)) throw new FileNotFoundException($"The model file {path} is no longer in the models folder.", path);
            return ModelCatalog.LocalEntry(path, new FileInfo(path).Length);
        }
        return Catalog.Find(id) ?? throw new KeyNotFoundException($"'{id}' is not in the ODS model catalog.");
    }

    public bool IsInstalled(CatalogEntry entry) =>
        entry.Source == "local" ? File.Exists(Models.PrimaryPath(entry)) : Models.StateOf(entry) is ModelInstallState.Installed or ModelInstallState.NeedsVerification;

    public Task DownloadModelAsync(CatalogEntry entry, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
        Models.DownloadAsync(entry, progress, cancellationToken);

    /// <summary>Start the model server for a model (the active one by default) and prove it is ready.</summary>
    public async Task StartServerAsync(string? modelId, int? contextLength, IProgress<SetupStep>? progress, CancellationToken cancellationToken)
    {
        if (!IsOwner) throw new InvalidOperationException("This ODS process is read-only and cannot start a model server.");
        var id = modelId ?? Settings.ActiveModel?.Id ?? throw new InvalidOperationException("No model is selected yet. Choose one on the Models page.");
        var entry = FindModel(id);
        var runtime = Runtime ?? await EnsureRuntimeAsync(progress, cancellationToken);
        if (entry.Source != "local")
        {
            if (Models.StateOf(entry) == ModelInstallState.NeedsVerification)
            {
                progress?.Report(new SetupStep($"Verifying {entry.Name}"));
                await Models.VerifyAsync(entry, cancellationToken);
            }
            if (Models.StateOf(entry) != ModelInstallState.Installed) throw new InvalidOperationException($"{entry.Name} is not downloaded yet.");
        }
        var preferred = contextLength ?? (Settings.ActiveModel?.Id == entry.Id ? Settings.ActiveModel.ContextLength : null);
        var plan = PlanFor(entry, preferred);
        if (!plan.Fits) AppLog.Warn($"{entry.Name} at {plan.ContextLength} context may not fit in {plan.CapacityGb:0.#} GiB; starting it anyway as requested.");

        var spec = new LlamaLaunchSpec(
            runtime,
            Models.PrimaryPath(entry),
            entry.PrimaryFile,
            PortProbe.ChooseLlamaPort(Settings.Server.Port),
            plan.ContextLength,
            Settings.Server.GpuLayers,
            Paths.ApiKeyFile,
            Settings.Server.Reasoning,
            LlamaArguments.ProfileEnv(plan.RuntimeProfile));
        progress?.Report(new SetupStep($"Starting {entry.Name}"));
        await Server.StartAsync(spec, new ModelIdentity(entry.Id, entry.Name), ApiKey,
            TimeSpan.FromSeconds(Settings.Server.StartupTimeoutSeconds), cancellationToken);
        Settings.ActiveModel = new ActiveModel(entry.Id, Server.Status.ContextSize ?? plan.ContextLength, plan.RuntimeProfileId);
        SaveSettings();
    }

    /// <summary>
    /// Switch models. If the new model fails to start, the previous one is
    /// started again (as the Dashboard does) and the failure is reported.
    /// </summary>
    public async Task SwitchModelAsync(string modelId, int? contextLength, IProgress<SetupStep>? progress, CancellationToken cancellationToken)
    {
        var previous = Settings.ActiveModel;
        await Server.StopAsync();
        try
        {
            await StartServerAsync(modelId, contextLength, progress, cancellationToken);
        }
        catch (ServerStartException) when (previous is not null && previous.Id != modelId)
        {
            AppLog.Warn($"Restoring {previous.Id} after {modelId} failed to start.");
            var failed = Server.Status;
            await Server.StopAsync();
            try
            {
                await StartServerAsync(previous.Id, previous.ContextLength, progress, cancellationToken);
            }
            catch (ServerStartException restoreError)
            {
                AppLog.Error($"The previous model {previous.Id} did not start again either: {restoreError.Message}");
            }
            throw new ServerStartException(failed.Failure ?? new ServerFailure(FailureKind.Unknown, "The model did not start", "See the log.", RecoveryAction.OpenLogs, null, []));
        }
    }

    /// <summary>Ask the loaded model ODS's one-token question; true when it answers 4.</summary>
    public async Task<(bool Passed, string Answer, TimeSpan Elapsed)> ProbeGenerationAsync(CancellationToken cancellationToken)
    {
        var status = Server.Status;
        if (status.State != ServerState.Ready || status.BaseUri is null) throw new InvalidOperationException("The model server is not ready.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var answer = await Api.CompleteAsync(status.BaseUri, ApiKey, FindModel(status.Model!.Id).PrimaryFile, ProbeQuestion, 16, cancellationToken);
        var passed = answer.TrimStart().StartsWith('4');
        AppLog.Info($"Generation probe answered '{answer.Trim()}' in {clock.Elapsed.TotalSeconds:0.0} s ({(passed ? "passed" : "failed")}).");
        return (passed, answer.Trim(), clock.Elapsed);
    }

    /// <summary>
    /// Bootstrap mode (installers/lib/bootstrap-model.sh): the small model
    /// serves while the recommended one downloads in the background. True when
    /// ODS should start with the bootstrap model.
    /// </summary>
    public bool BootstrapRecommended(CatalogEntry target) =>
        target.Id != BootstrapModelId && !IsInstalled(target) && Catalog.Find(BootstrapModelId) is not null;
}
