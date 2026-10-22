using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Ods.Core;
using Ods.Core.Catalog;
using Ods.Core.Diagnostics;
using Ods.Core.Downloads;
using Ods.Core.Models;
using Ods.Desktop.Converters;
using Ods.Desktop.Services;

namespace Ods.Desktop.ViewModels;

/// <summary>One model on the Models page: catalog facts, fit on this PC, install state and actions.</summary>
public sealed partial class ModelItem : ObservableObject
{
    private readonly AppHost _host;
    private CancellationTokenSource? _download;

    public ModelItem(AppHost host, CatalogEntry entry, ModelPlan? plan, bool recommended)
    {
        _host = host;
        Entry = entry;
        Plan = plan;
        IsRecommended = recommended;
        var contexts = new SortedSet<int>(ModelMemory.ContextSteps.Where(c => c <= Math.Max(entry.MaxContextLength, 8192)));
        if (plan is not null) contexts.Add(plan.ContextLength);
        ContextOptions = contexts.Reverse().ToList();
        SelectedContext = plan?.ContextLength ?? Math.Min(entry.ContextLength, 8192);
        RefreshState();
    }

    public CatalogEntry Entry { get; }

    public ModelPlan? Plan { get; }

    public bool IsRecommended { get; }

    public bool IsLocalFile => Entry.Source == "local";

    public string Name => Entry.Name;

    public string Specialty => Entry.Specialty;

    public string Description => Entry.Description;

    public string SizeText => Format.Bytes(Entry.DownloadBytes);

    public bool Fits => Plan?.Fits ?? false;

    public string FitText => Plan is null
        ? "Fit unknown"
        : Plan.Fits
            ? $"Fits · {Format.Context(Plan.ContextLength)} context · about {Plan.RequiredGb:0.#} GB of {Plan.CapacityGb:0.#} GB"
            : $"Too large for this PC at {Format.Context(Plan.ContextLength)} context";

    public IReadOnlyList<int> ContextOptions { get; }

    [ObservableProperty]
    private int _selectedContext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(CanDownload))]
    private ModelInstallState _state;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = "";

    public bool IsInstalled => State is ModelInstallState.Installed or ModelInstallState.NeedsVerification;

    /// <summary>The bootstrap's full model: it downloads in the background (Home shows it), never twice.</summary>
    public bool IsBackgroundDownload => !IsInstalled && _host.Engine.Settings.PendingModel?.Id == Entry.Id;

    public bool CanDownload => !IsInstalled && !IsDownloading && !IsBackgroundDownload && Entry.IsDownloadable;

    public string UseLabel => IsActive ? "Apply" : "Use";

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(UseLabel));

    public void RefreshState()
    {
        State = IsLocalFile ? ModelInstallState.Installed : _host.Engine.Models.StateOf(Entry);
        IsActive = _host.Engine.Settings.ActiveModel?.Id == Entry.Id;
        if (IsBackgroundDownload && !IsDownloading) ProgressText = "Downloading in the background for you; progress is on the Home page.";
        OnPropertyChanged(nameof(IsBackgroundDownload));
        OnPropertyChanged(nameof(CanDownload));
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (!CanDownload) return;
        IsDownloading = true;
        _download = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(d =>
        {
            Progress = (d.Fraction ?? 0) * 100;
            ProgressText = d.Phase switch
            {
                DownloadPhase.Verifying => "Verifying…",
                DownloadPhase.CheckingPartial => "Checking the partial download…",
                DownloadPhase.Resuming => $"Connection stalled, resuming (attempt {d.Attempt})…",
                _ => $"{Format.Bytes(d.BytesDone)} of {Format.Bytes(d.TotalBytes ?? Entry.DownloadBytes)} · {Format.Speed(d.BytesPerSecond)} · {Format.Duration(d.Remaining)}",
            };
        });
        try
        {
            await _host.Engine.DownloadModelAsync(Entry, progress, _download.Token);
            _host.Notify("Download complete", $"{Name} is verified and ready to use.");
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Paused. Download again to continue where it stopped.";
        }
        catch (DownloadException error)
        {
            AppLog.Error($"Downloading {Name} failed.", error);
            _host.ShowError($"Could not download {Name}", error.Message);
        }
        finally
        {
            IsDownloading = false;
            _download = null;
            RefreshState();
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    [RelayCommand]
    private async Task UseAsync()
    {
        var ok = await _host.Shell.RunAsync($"Switching to {Name}",
            (progress, token) => _host.Engine.SwitchModelAsync(Entry.Id, SelectedContext, progress, token));
        _host.Models.Refresh();
        if (ok) _host.Notify("Model switched", $"ODS now runs {Name} with a {Format.Context(_host.Engine.Server.Status.ContextSize ?? SelectedContext)} context.");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (IsActive && _host.Engine.Server.Status.IsActive)
        {
            _host.Notify("This model is in use", "Switch to another model or stop the model server before deleting it.");
            return;
        }
        var dialog = new Wpf.Ui.Controls.ContentDialog
        {
            Title = $"Delete {Name}?",
            Content = $"This frees {SizeText}. You can download it again later.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        if (await _host.Dialogs.ShowAsync(dialog, CancellationToken.None) != Wpf.Ui.Controls.ContentDialogResult.Primary) return;
        if (IsLocalFile) _host.Engine.Models.DeleteLocal(_host.Engine.Models.PrimaryPath(Entry));
        else _host.Engine.Models.Delete(Entry);
        if (IsActive)
        {
            _host.Engine.Settings.ActiveModel = null;
            _host.Engine.SaveSettings();
        }
        _host.Models.Refresh();
    }

    [RelayCommand]
    private void ShowInFolder() => AppHost.ShowInFolder(_host.Engine.Models.PrimaryPath(Entry));
}

/// <summary>The Models page: recommendation, installed models, the catalog and the models folder.</summary>
public sealed partial class ModelsViewModel(AppHost host) : ObservableObject
{
    private OdsEngine Engine => host.Engine;

    public ObservableCollection<ModelItem> Installed { get; } = [];

    public ObservableCollection<ModelItem> Available { get; } = [];

    [ObservableProperty]
    private ModelItem? _recommended;

    [ObservableProperty]
    private string _recommendationReason = "";

    [ObservableProperty]
    private bool _showAll;

    [ObservableProperty]
    private string _modelsFolder = "";

    [ObservableProperty]
    private string _freeSpace = "";

    [ObservableProperty]
    private bool _isReady;

    partial void OnShowAllChanged(bool value) => Refresh();

    public void Refresh()
    {
        if (Engine.Hardware is null || Engine.Plan is null) return;
        var downloading = Available.Concat(Installed).Where(i => i.IsDownloading).ToDictionary(i => i.Entry.Id);
        ModelsFolder = Engine.ModelsDirectory;
        var root = Path.GetPathRoot(Path.GetFullPath(ModelsFolder));
        FreeSpace = string.IsNullOrEmpty(root) ? "" : $"{Format.Bytes(new DriveInfo(root).AvailableFreeSpace)} free on {root}";

        Recommendation? recommendation = null;
        try
        {
            recommendation = Engine.Recommend();
        }
        catch (NoModelFitsException error)
        {
            RecommendationReason = error.Message;
        }
        var ranked = Engine.RankedModels().Select(c => c.Id).ToList();

        ModelItem Item(CatalogEntry entry) => downloading.TryGetValue(entry.Id, out var existing)
            ? existing
            : new ModelItem(host, entry, entry.Normalized is null ? null : Engine.PlanFor(entry), entry.Id == recommendation?.Selected.Id);

        Installed.Clear();
        Available.Clear();
        var catalogModels = Engine.Catalog.Entries.Where(e => e.IsCurated && e.IsDownloadable && e.InstallRecommendation)
            .OrderBy(e => ranked.IndexOf(e.Id) is var index && index < 0 ? int.MaxValue : index)
            .ThenBy(e => e.DownloadBytes);
        foreach (var entry in catalogModels)
        {
            var item = Item(entry);
            if (item.IsInstalled) Installed.Add(item);
            // The recommendation has its own card above the list.
            else if ((ShowAll || item.Fits) && entry.Id != recommendation?.Selected.Id) Available.Add(item);
        }
        foreach (var local in Engine.Models.ScanLocal(Engine.Catalog))
        {
            if (local.Entry is not null)
            {
                if (Installed.All(i => i.Entry.Id != local.Entry.Id) && local.Entry.IsCurated) Installed.Add(Item(local.Entry));
                continue;
            }
            Installed.Add(Item(ModelCatalog.LocalEntry(local.Path, local.Size)));
        }

        if (recommendation is not null)
        {
            Recommended = Installed.Concat(Available).FirstOrDefault(i => i.Entry.Id == recommendation.Selected.Id) ?? Item(Engine.Catalog.Find(recommendation.Selected.Id)!);
            RecommendationReason = recommendation.Reason;
        }
        IsReady = true;
    }

    [RelayCommand]
    private void ChangeFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where ODS keeps models", InitialDirectory = ModelsFolder };
        if (dialog.ShowDialog() != true) return;
        if (!Ods.Core.Platform.WindowsPaths.IsPrintableAscii(dialog.FolderName))
        {
            host.Notify("Folder name note", "llama.cpp opens non-English paths through their short (8.3) names; if loading fails, choose a folder with an English name.");
        }
        Engine.SetModelsDirectory(dialog.FolderName);
        Refresh();
        host.Home.RefreshHardware();
        host.Notify("Models folder changed", $"ODS now keeps models in {Engine.ModelsDirectory}. Existing models there are used as they are.");
    }

    [RelayCommand]
    private void OpenFolder() => AppHost.OpenFolder(Engine.ModelsDirectory);
}
