using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ods.Core;
using Ods.Core.Catalog;
using Ods.Core.Diagnostics;
using Ods.Core.Downloads;
using Ods.Core.Runtime;
using Ods.Desktop.Converters;
using Ods.Desktop.Services;
using Ods.Desktop.Views.Pages;

namespace Ods.Desktop.ViewModels;

/// <summary>
/// App-wide state: the model server's status, one operation at a time
/// (start, stop, switch, repair), recovery actions for failures, and the
/// bootstrap model's background download.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private CancellationTokenSource? _pendingDownload;

    public ShellViewModel(AppHost host)
    {
        _host = host;
        host.Engine.Server.StatusChanged += (_, status) => AppHost.OnUi(() => Apply(status));
    }

    private OdsEngine Engine => _host.Engine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsActive), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand))]
    private ServerState _state = ServerState.Stopped;

    [ObservableProperty]
    private string _statusTitle = "Model server is stopped";

    [ObservableProperty]
    private string _statusDetail = "";

    /// <summary>ready, busy, stopped or failed: drives the status colour.</summary>
    [ObservableProperty]
    private string _statusKind = "stopped";

    [ObservableProperty]
    private string _pillText = "Stopped";

    [ObservableProperty]
    private string? _modelName;

    [ObservableProperty]
    private string? _endpoint;

    [ObservableProperty]
    private ServerFailure? _failure;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    private bool _isOnboarding;

    [ObservableProperty]
    private bool _hasPendingDownload;

    [ObservableProperty]
    private double _pendingFraction;

    [ObservableProperty]
    private string _pendingText = "";

    [ObservableProperty]
    private string _pendingModelName = "";

    [ObservableProperty]
    private bool _pendingPaused;

    public bool IsReady => State == ServerState.Ready;

    public bool IsActive => State is ServerState.Starting or ServerState.Loading or ServerState.Ready;

    public bool CanStart => !IsBusy && !IsActive && Engine.Settings.ActiveModel is not null;

    private void Apply(ServerStatus status)
    {
        State = status.State;
        ModelName = status.Model?.Name;
        Endpoint = status.State == ServerState.Ready && status.BaseUri is Uri uri ? new Uri(uri, "/v1").ToString() : null;
        var context = status.ContextSize is int c ? $"{Format.Context(c)} context" : "";
        (StatusKind, PillText, StatusTitle, StatusDetail) = status.State switch
        {
            ServerState.Ready => ("ready", "Ready", $"{status.Model?.Name} is ready", $"{context} · llama.cpp {Flavor(status)} · {Endpoint}"),
            ServerState.Loading => ("busy", "Loading", $"Loading {status.Model?.Name}…", "Large models take a minute to load the first time."),
            ServerState.Starting => ("busy", "Starting", $"Starting {status.Model?.Name}…", ""),
            ServerState.Stopping => ("busy", "Stopping", "Stopping the model server…", ""),
            ServerState.Failed => ("failed", "Needs attention", status.Failure?.Title ?? "The model server stopped", status.Failure?.Detail ?? ""),
            _ => ("stopped", "Stopped", "Model server is stopped", Engine.Settings.ActiveModel is null ? "Choose a model to get started." : "Start it to chat and to use the local API."),
        };
        if (status.State == ServerState.Failed) Failure = status.Failure;
        else if (status.State is ServerState.Starting or ServerState.Ready) Failure = null;
        OnPropertyChanged(nameof(CanStart));
        _host.Window?.UpdateTray();
    }

    private string Flavor(ServerStatus status) =>
        status.Flavor is string flavor ? Engine.Pins.Flavors.FirstOrDefault(f => f.Flavor == flavor)?.DisplayName ?? flavor : "";

    /// <summary>Run one engine operation; failures land in the banner or a notice, never silently.</summary>
    public async Task<bool> RunAsync(string text, Func<IProgress<SetupStep>, CancellationToken, Task> work)
    {
        if (!await _operation.WaitAsync(0))
        {
            _host.Notify("ODS is busy", $"Wait for \"{BusyText}\" to finish.");
            return false;
        }
        IsBusy = true;
        BusyText = text;
        var progress = new Progress<SetupStep>(step => BusyText = step.Download is DownloadProgress d && d.Fraction is double f
            ? $"{step.Title} {f:P0}"
            : step.Title);
        try
        {
            await work(progress, CancellationToken.None);
            return true;
        }
        catch (ServerStartException error)
        {
            Failure = error.Failure;
            return false;
        }
        catch (Exception error) when (error is DownloadException or RuntimeQualificationException or InvalidOperationException
            or KeyNotFoundException or FileNotFoundException or InvalidDataException or NoModelFitsException)
        {
            AppLog.Error($"{text} failed.", error);
            _host.ShowError(text + " failed", error.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            _operation.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync("Starting the model server", (progress, token) => Engine.StartServerAsync(null, null, progress, token));

    private bool CanStop => !IsBusy && IsActive;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunAsync("Stopping the model server", (_, _) => Engine.StopServerAsync());

    private bool CanRestart => !IsBusy && Engine.Settings.ActiveModel is not null;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => RunAsync("Restarting the model server", async (progress, token) =>
    {
        await Engine.StopServerAsync();
        await Engine.StartServerAsync(null, null, progress, token);
    });

    /// <summary>Recovery: serve the same model with half the context.</summary>
    [RelayCommand]
    private Task ReduceContextAsync()
    {
        var active = Engine.Settings.ActiveModel;
        if (active is null) return Task.CompletedTask;
        var smaller = Math.Max(8192, active.ContextLength / 2);
        return RunAsync($"Restarting with a {Format.Context(smaller)} context", async (progress, token) =>
        {
            await Engine.StopServerAsync();
            await Engine.StartServerAsync(active.Id, smaller, progress, token);
        });
    }

    [RelayCommand]
    private void ChooseModel() => _host.Navigate(typeof(ModelsPage));

    [RelayCommand]
    private void OpenLogs() => _host.Navigate(typeof(LogsPage));

    [RelayCommand]
    private Task RepairRuntimeAsync() => RunAsync("Repairing llama.cpp", async (progress, token) =>
    {
        await Engine.RepairRuntimeAsync(progress, token);
        if (Engine.Settings.ActiveModel is not null) await Engine.StartServerAsync(null, null, progress, token);
    });

    [RelayCommand]
    private Task ChangePortAsync()
    {
        Engine.Settings.Server.Port = 0;
        Engine.SaveSettings();
        return RestartAsync();
    }

    [RelayCommand]
    private void UpdateDriver()
    {
        var backend = Engine.Hardware?.Gpu.Backend;
        AppHost.OpenUrl(backend == "amd" ? "https://www.amd.com/en/support/download/drivers.html" : "https://www.nvidia.com/en-us/drivers/");
    }

    [RelayCommand]
    private void DismissFailure() => Failure = null;

    /// <summary>A normal launch (setup already done): verify the runtime, load the model, resume downloads.</summary>
    public async Task StartupAsync()
    {
        var ok = await RunAsync("Checking this PC", async (progress, token) =>
        {
            await Engine.ProbeAsync(token);
            await Engine.EnsureRuntimeAsync(progress, token);
        });
        _host.Home.RefreshHardware();
        _host.Models.Refresh();
        if (ok && Engine.Settings.Server.LoadOnStartup && Engine.Settings.ActiveModel is not null)
        {
            await StartCommand.ExecuteAsync(null);
        }
        Apply(Engine.Server.Status);
        StartPendingDownload();
        if (Engine.Settings.Updates.AutoCheck) await _host.Updates.CheckAsync(userInitiated: false);
    }

    /// <summary>
    /// Bootstrap mode: download the recommended model in the background, wait
    /// until no reply is streaming, then switch to it. A restart resumes the
    /// download from its partial file.
    /// </summary>
    public void StartPendingDownload()
    {
        if (Engine.Settings.PendingModel is not { } pending || _pendingDownload is not null) return;
        CatalogEntry entry;
        try
        {
            entry = Engine.FindModel(pending.Id);
        }
        catch (KeyNotFoundException)
        {
            AppLog.Warn($"The pending model {pending.Id} is no longer in the catalog; dropping it.");
            Engine.Settings.PendingModel = null;
            Engine.SaveSettings();
            return;
        }
        HasPendingDownload = true;
        PendingPaused = false;
        PendingModelName = entry.Name;
        _pendingDownload = new CancellationTokenSource();
        var token = _pendingDownload.Token;
        var progress = new Progress<DownloadProgress>(d =>
        {
            PendingFraction = d.Fraction ?? 0;
            PendingText = d.Phase switch
            {
                DownloadPhase.Resuming => $"Connection stalled, resuming (attempt {d.Attempt})…",
                DownloadPhase.Verifying => "Verifying the download…",
                DownloadPhase.CheckingPartial => "Checking the partial download…",
                _ => $"{d.Fraction ?? 0:P0} · {Format.Bytes(d.BytesDone)} of {Format.Bytes(d.TotalBytes ?? 0)} · {Format.Speed(d.BytesPerSecond)} · {Format.Duration(d.Remaining)}",
            };
        });
        _ = CompletePendingAsync(entry, progress, token);
    }

    private async Task CompletePendingAsync(CatalogEntry entry, IProgress<DownloadProgress> progress, CancellationToken token)
    {
        try
        {
            await Engine.DownloadModelAsync(entry, progress, token);
            PendingText = $"Downloaded. Switching to {entry.Name} when you are not chatting…";
            await _host.Chat.WaitUntilIdleAsync(token);
            var switched = await RunAsync($"Switching to {entry.Name}", (p, t) => Engine.SwitchModelAsync(entry.Id, null, p, t));
            if (switched)
            {
                Engine.Settings.PendingModel = null;
                Engine.SaveSettings();
                HasPendingDownload = false;
                _host.Notify("Your full model is ready", $"ODS now runs {entry.Name}.");
                _host.Models.Refresh();
            }
            else
            {
                PendingText = $"{entry.Name} is downloaded but did not start; ODS kept the previous model. See Home for details.";
            }
        }
        catch (OperationCanceledException)
        {
            PendingPaused = true;
            PendingText = "Paused. Resume to continue where it stopped.";
        }
        catch (DownloadException error)
        {
            PendingPaused = true;
            PendingText = error.Message;
            AppLog.Error($"Background download of {entry.Name} stopped.", error);
        }
        finally
        {
            _pendingDownload = null;
        }
    }

    [RelayCommand]
    private void PausePending() => _pendingDownload?.Cancel();

    [RelayCommand]
    private void ResumePending() => StartPendingDownload();

    [RelayCommand]
    private void OpenChat() => _host.Navigate(typeof(ChatPage));
}
