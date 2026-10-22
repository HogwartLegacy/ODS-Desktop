using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Ods.Core;
using Ods.Core.Catalog;
using Ods.Core.Diagnostics;
using Ods.Core.Downloads;
using Ods.Core.Platform;
using Ods.Core.Runtime;
using Ods.Core.Settings;
using Ods.Desktop.Converters;
using Ods.Desktop.Services;
using Ods.Desktop.Views.Pages;

namespace Ods.Desktop.ViewModels;

/// <summary>A model offered during setup.</summary>
public sealed record ModelChoice(CatalogEntry Entry, int ContextLength, double RequiredGb, bool Recommended, string Reason)
{
    public string Name => Entry.Name;

    public string Facts => $"{Entry.Specialty} · {Format.Context(ContextLength)} context · {Format.Bytes(Entry.DownloadBytes)} download · needs about {RequiredGb:0.#} GB";
}

public sealed partial class InstallStep(string title) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>pending, running, done, failed or skipped.</summary>
    [ObservableProperty]
    private string _state = "pending";

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _hasProgress;
}

/// <summary>
/// First-run setup: check the PC, recommend a model with ODS's own selection
/// policy, install the pinned llama.cpp runtime and the model, start the
/// server and prove it answers. Every step can fail visibly and be retried;
/// finished steps are not repeated.
/// </summary>
public sealed partial class OnboardingViewModel(AppHost host) : ObservableObject
{
    private OdsEngine Engine => host.Engine;

    /// <summary>0 welcome, 1 checking, 2 choose, 3 installing, 4 ready.</summary>
    [ObservableProperty]
    private int _step;

    [ObservableProperty]
    private string _cpuText = "";

    [ObservableProperty]
    private string _memoryText = "";

    [ObservableProperty]
    private string _gpuText = "";

    [ObservableProperty]
    private string _accelerationText = "";

    public ObservableCollection<string> Notes { get; } = [];

    public ObservableCollection<ModelChoice> Choices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSummary))]
    private ModelChoice? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSummary))]
    private string _modelsDirectory = "";

    [ObservableProperty]
    private string _freeSpaceText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadSummary))]
    private bool _useBootstrap = true;

    [ObservableProperty]
    private bool _bootstrapAvailable;

    [ObservableProperty]
    private bool _startWithWindows = true;

    public ObservableCollection<InstallStep> Steps { get; } = [];

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string _proofText = "";

    [ObservableProperty]
    private string _readyDetail = "";

    public string DownloadSummary
    {
        get
        {
            if (Selected is null || Engine.Plan is null) return "";
            var runtime = Engine.Plan.Preferred;
            var parts = new List<string> { $"llama.cpp {runtime.DisplayName} ({Format.Bytes(runtime.DownloadBytes)})" };
            var bootstrap = Engine.Catalog.Find(OdsEngine.BootstrapModelId);
            if (UseBootstrap && BootstrapAvailable && bootstrap is not null)
            {
                parts.Add($"{bootstrap.Name} ({Format.Bytes(bootstrap.DownloadBytes)}) to chat right away");
                parts.Add($"{Selected.Name} ({Format.Bytes(Selected.Entry.DownloadBytes)}) in the background");
            }
            else
            {
                parts.Add($"{Selected.Name} ({Format.Bytes(Selected.Entry.DownloadBytes)})");
            }
            return "ODS will download " + string.Join(", ", parts) + ". Everything is verified against pinned checksums.";
        }
    }

    [RelayCommand]
    private async Task BeginAsync()
    {
        Step = 1;
        Error = null;
        try
        {
            var hardware = await Engine.ProbeAsync();
            CpuText = $"{hardware.CpuName} · {hardware.LogicalProcessors} threads";
            MemoryText = $"{hardware.SystemRamGb} GB";
            var adapters = hardware.Adapters.Where(a => !a.IsSoftware).Select(a => $"{a.Name} ({Format.Bytes(a.DedicatedVideoMemoryBytes)})");
            GpuText = string.Join("\n", adapters.DefaultIfEmpty("No graphics adapter reported"));
            AccelerationText = Engine.Plan!.Preferred.IsGpu
                ? $"GPU acceleration with llama.cpp {Engine.Plan.Preferred.DisplayName}"
                : "CPU inference with llama.cpp (no supported GPU found)";
            Notes.Clear();
            foreach (var note in Engine.Plan.Notes) Notes.Add(note);
            LoadChoices();
            ModelsDirectory = Engine.ModelsDirectory;
            UpdateFreeSpace();
            await Task.Delay(400);
            Step = 2;
        }
        catch (NoModelFitsException error)
        {
            Error = error.Message + " ODS needs at least 8 GB of RAM or a supported GPU.";
        }
    }

    private void LoadChoices()
    {
        Choices.Clear();
        var recommendation = Engine.Recommend();
        foreach (var candidate in recommendation.Alternatives.DistinctBy(c => c.Id))
        {
            var entry = Engine.Catalog.Find(candidate.Id)!;
            var recommended = candidate.Id == recommendation.Selected.Id;
            Choices.Add(new ModelChoice(entry, candidate.ContextLength, candidate.RequiredGb, recommended,
                recommended ? "Recommended for this PC by ODS's model selection" : "Also fits this PC"));
        }
        Selected = Choices.FirstOrDefault(c => c.Recommended) ?? Choices.FirstOrDefault();
        BootstrapAvailable = Selected is not null && Engine.BootstrapRecommended(Selected.Entry);
    }

    partial void OnSelectedChanged(ModelChoice? value) =>
        BootstrapAvailable = value is not null && Engine.BootstrapRecommended(value.Entry);

    [RelayCommand]
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where ODS keeps models", InitialDirectory = ModelsDirectory };
        if (dialog.ShowDialog() != true) return;
        ModelsDirectory = dialog.FolderName;
        UpdateFreeSpace();
    }

    private void UpdateFreeSpace()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(ModelsDirectory));
        FreeSpaceText = string.IsNullOrEmpty(root) ? "" : $"{Format.Bytes(new DriveInfo(root).AvailableFreeSpace)} free on {root}";
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Selected is null) return;
        Step = 3;
        Error = null;
        IsInstalling = true;
        var target = Selected;
        var bootstrap = UseBootstrap && BootstrapAvailable ? Engine.Catalog.Find(OdsEngine.BootstrapModelId) : null;
        var first = bootstrap ?? target.Entry;

        Steps.Clear();
        var runtimeStep = Add("Download and verify llama.cpp");
        var qualifyStep = Add("Check that it can use this PC");
        var modelStep = Add($"Download {first.Name}");
        var startStep = Add($"Start {first.Name}");
        var proofStep = Add("Ask a test question");

        try
        {
            if (Engine.ModelsDirectory != ModelsDirectory) Engine.SetModelsDirectory(ModelsDirectory);

            await RunStep(runtimeStep, async () =>
            {
                var progress = new Progress<SetupStep>(s =>
                {
                    if (s.Title.StartsWith("Checking", StringComparison.Ordinal) || s.Title.Contains("cannot run", StringComparison.Ordinal))
                    {
                        runtimeStep.State = "done";
                        qualifyStep.State = "running";
                        qualifyStep.Detail = s.Detail ?? s.Title;
                    }
                    else
                    {
                        Report(runtimeStep, s.Title, s.Download);
                    }
                });
                await Engine.EnsureRuntimeAsync(progress, CancellationToken.None);
            });
            qualifyStep.State = "done";
            qualifyStep.Detail = Engine.Runtime!.Device == "none" ? "Runs on the CPU" : $"Uses {Engine.Runtime.Device} ({Engine.Runtime.Flavor.DisplayName})";

            // If the GPU build could not run here, plan the model for the build that did.
            if (!Engine.Runtime.Flavor.IsGpu && Engine.Plan!.Preferred.IsGpu)
            {
                var fallback = Engine.Recommend();
                target = new ModelChoice(Engine.Catalog.Find(fallback.Selected.Id)!, fallback.Selected.ContextLength, fallback.Selected.RequiredGb, true, "Recommended for CPU inference");
                Notes.Add($"The GPU could not be used, so ODS chose {target.Name} for the CPU instead.");
            }

            await RunStep(modelStep, async () =>
            {
                var progress = new Progress<DownloadProgress>(d => Report(modelStep, null, d));
                await Engine.DownloadModelAsync(first, progress, CancellationToken.None);
            });

            await RunStep(startStep, () => Engine.StartServerAsync(first.Id, bootstrap is null ? null : OdsEngine.BootstrapContext,
                new Progress<SetupStep>(s => startStep.Detail = s.Title), CancellationToken.None));
            startStep.Detail = $"Running at {Engine.Server.Status.BaseUri} with a {Format.Context(Engine.Server.Status.ContextSize ?? 0)} context";

            await RunStep(proofStep, async () =>
            {
                var (passed, answer, elapsed) = await Engine.ProbeGenerationAsync(CancellationToken.None);
                ProofText = $"“{OdsEngine.ProbeQuestion}” → “{answer}” in {elapsed.TotalSeconds:0.0} s";
                proofStep.Detail = ProofText;
                if (!passed) throw new InvalidOperationException($"The model answered “{answer}” instead of 4. It is running, but may be damaged; try another model.");
            });

            Complete(target, bootstrap);
        }
        catch (Exception error) when (error is DownloadException or RuntimeQualificationException or ServerStartException
            or InvalidOperationException or InvalidDataException or IOException or KeyNotFoundException)
        {
            Error = error is ServerStartException start ? $"{start.Failure.Title}. {start.Failure.Detail}" : error.Message;
            AppLog.Error("Setup did not finish.", error);
        }
        finally
        {
            IsInstalling = false;
        }

        InstallStep Add(string title)
        {
            var step = new InstallStep(title);
            Steps.Add(step);
            return step;
        }
    }

    private static async Task RunStep(InstallStep step, Func<Task> work)
    {
        step.State = "running";
        try
        {
            await work();
            step.State = "done";
            step.HasProgress = false;
        }
        catch
        {
            step.State = "failed";
            step.HasProgress = false;
            throw;
        }
    }

    private static void Report(InstallStep step, string? title, DownloadProgress? download)
    {
        if (download is null)
        {
            step.Detail = title;
            return;
        }
        step.HasProgress = download.Fraction is not null;
        step.Progress = (download.Fraction ?? 0) * 100;
        step.Detail = download.Phase switch
        {
            DownloadPhase.Verifying => "Verifying SHA-256…",
            DownloadPhase.CheckingPartial => "Checking the partial download…",
            DownloadPhase.Resuming => $"Connection stalled, resuming (attempt {download.Attempt})…",
            _ => $"{Format.Bytes(download.BytesDone)} of {Format.Bytes(download.TotalBytes ?? 0)} · {Format.Speed(download.BytesPerSecond)} · {Format.Duration(download.Remaining)}",
        };
    }

    private void Complete(ModelChoice target, CatalogEntry? bootstrap)
    {
        var settings = Engine.Settings;
        settings.OnboardingCompleted = true;
        settings.StartWithWindows = StartWithWindows;
        settings.PendingModel = bootstrap is null ? null : new ActiveModel(target.Entry.Id, target.ContextLength, null);
        Engine.SaveSettings();
        if (StartWithWindows) StartupRegistration.Enable(Environment.ProcessPath!);
        else StartupRegistration.Disable();
        ReadyDetail = bootstrap is null
            ? $"{target.Name} runs locally on this PC. Nothing you type leaves it."
            : $"You can chat with {bootstrap.Name} now. ODS downloads {target.Name} in the background and switches to it when it is ready.";
        AppLog.Info($"Setup complete: serving {Engine.Server.Status.Model?.Id}; pending {settings.PendingModel?.Id ?? "none"}.");
        Step = 4;
    }

    [RelayCommand]
    private void Finish()
    {
        host.Shell.IsOnboarding = false;
        host.Home.RefreshHardware();
        host.Models.Refresh();
        host.Navigate(typeof(ChatPage));
        host.Shell.StartPendingDownload();
        if (Engine.Settings.Updates.AutoCheck) _ = host.Updates.CheckAsync(userInitiated: false);
    }
}
