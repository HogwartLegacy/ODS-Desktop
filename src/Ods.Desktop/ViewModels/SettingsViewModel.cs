using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ods.Core;
using Ods.Core.Diagnostics;
using Ods.Core.Models;
using Ods.Core.Platform;
using Ods.Desktop.Converters;
using Ods.Desktop.Services;
using Wpf.Ui.Controls;

namespace Ods.Desktop.ViewModels;

/// <summary>A setting's stored value and the label the user sees.</summary>
public sealed record Choice(string Value, string Label);

/// <summary>Settings, storage, runtime maintenance, updates and removal.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _loading;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        Load();
    }

    private OdsEngine Engine => _host.Engine;

    public UpdateService Updates => _host.Updates;

    public IReadOnlyList<Choice> Themes { get; } = [new("dark", "Dark"), new("light", "Light"), new("system", "Use the Windows setting")];

    public IReadOnlyList<Choice> ReasoningModes { get; } = [new("off", "Off"), new("on", "On"), new("auto", "Automatic")];

    public IReadOnlyList<Choice> GpuLayerModes { get; } = [new("all", "All layers on the GPU"), new("auto", "Automatic")];

    public string Version => OdsEngine.Version;

    public string DataFolder => Engine.Paths.Root;

    [ObservableProperty]
    private string _theme = "dark";

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private string _startupNote = "";

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _loadOnStartup;

    [ObservableProperty]
    private string _port = "Automatic";

    [ObservableProperty]
    private string _reasoning = "off";

    [ObservableProperty]
    private string _gpuLayers = "all";

    [ObservableProperty]
    private bool _autoUpdate;

    [ObservableProperty]
    private string _runtimeText = "";

    [ObservableProperty]
    private string _storageText = "";

    private void Load()
    {
        _loading = true;
        var settings = Engine.Settings;
        Theme = settings.Ui.Theme;
        CloseToTray = settings.Ui.CloseToTray;
        LoadOnStartup = settings.Server.LoadOnStartup;
        Port = settings.Server.Port == 0 ? "Automatic" : settings.Server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Reasoning = settings.Server.Reasoning;
        GpuLayers = settings.Server.GpuLayers;
        AutoUpdate = settings.Updates.AutoCheck;
        var state = StartupRegistration.State();
        StartWithWindows = state == StartupState.Enabled;
        StartupNote = state == StartupState.DisabledInTaskManager
            ? "Turned off in Task Manager > Startup apps. Switching it on here turns it back on."
            : "ODS starts in the tray and loads your model in the background.";
        _loading = false;
    }

    public void Refresh()
    {
        Load();
        RuntimeText = Engine.Runtime is { } runtime
            ? $"llama.cpp {runtime.Flavor.ReleaseTag} {runtime.Flavor.Flavor} on {(runtime.Device == "none" ? "the CPU" : runtime.Device)}"
            : "Not installed yet";
        var models = DataRemoval.DirectorySize(Engine.ModelsDirectory);
        var runtimes = DataRemoval.DirectorySize(Engine.Paths.Runtimes);
        StorageText = $"Models {Format.Bytes(models)} · llama.cpp {Format.Bytes(runtimes)} · in {Engine.ModelsDirectory}";
    }

    private void Save()
    {
        if (!_loading) Engine.SaveSettings();
    }

    partial void OnThemeChanged(string value)
    {
        if (_loading) return;
        Engine.Settings.Ui.Theme = value;
        Save();
        _host.ApplyTheme(value);
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        Engine.Settings.StartWithWindows = value;
        Save();
        if (value) StartupRegistration.Enable(Environment.ProcessPath!);
        else StartupRegistration.Disable();
        Load();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        Engine.Settings.Ui.CloseToTray = value;
        Save();
    }

    partial void OnLoadOnStartupChanged(bool value)
    {
        Engine.Settings.Server.LoadOnStartup = value;
        Save();
    }

    partial void OnReasoningChanged(string value)
    {
        if (_loading) return;
        Engine.Settings.Server.Reasoning = value;
        Save();
        _host.Notify("Applies on restart", "Restart the model server to use the new reasoning setting.");
    }

    partial void OnGpuLayersChanged(string value)
    {
        if (_loading) return;
        Engine.Settings.Server.GpuLayers = value;
        Save();
        _host.Notify("Applies on restart", "Restart the model server to use the new GPU setting.");
    }

    partial void OnPortChanged(string value)
    {
        if (_loading) return;
        if (value.Trim().Length == 0 || value.Trim().Equals("automatic", StringComparison.OrdinalIgnoreCase))
        {
            Engine.Settings.Server.Port = 0;
        }
        else if (int.TryParse(value, out var port) && port is >= 1024 and <= 65535)
        {
            Engine.Settings.Server.Port = port;
        }
        else
        {
            return;
        }
        Save();
    }

    partial void OnAutoUpdateChanged(bool value)
    {
        Engine.Settings.Updates.AutoCheck = value;
        Save();
    }

    [RelayCommand]
    private void OpenDataFolder() => AppHost.OpenFolder(Engine.Paths.Root);

    [RelayCommand]
    private Task RepairRuntimeAsync() => _host.Shell.RepairRuntimeCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task RecheckHardwareAsync()
    {
        await _host.Shell.RunAsync("Checking this PC again", async (progress, token) =>
        {
            await Engine.ProbeAsync(token);
            await Engine.EnsureRuntimeAsync(progress, token, requalify: true);
        });
        _host.Home.RefreshHardware();
        _host.Models.Refresh();
        Refresh();
    }

    [RelayCommand]
    private Task CheckUpdatesAsync() => Updates.CheckAsync(userInitiated: true);

    [RelayCommand]
    private Task RestartToUpdateAsync() => Updates.RestartToUpdateAsync();

    [RelayCommand]
    private static void OpenProject() => AppHost.OpenUrl("https://github.com/Osmantic/ODS");

    [RelayCommand]
    private static void OpenDiscord() => AppHost.OpenUrl("https://discord.gg/qGVygYada3");

    /// <summary>Delete models, chats, logs and settings, then quit. The app stays installed.</summary>
    [RelayCommand]
    private async Task RemoveDataAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Remove all ODS data?",
            Content = $"This deletes downloaded models, chats, logs and settings ({StorageText}). ODS quits afterwards and starts setup again next time. This cannot be undone.",
            PrimaryButtonText = "Remove data",
            CloseButtonText = "Cancel",
        };
        if (await _host.Dialogs.ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary) return;
        AppLog.Info("Removing all ODS data at the user's request.");
        await Engine.StopServerAsync();
        if (Engine.Settings.ModelsDirectory is string custom && Directory.Exists(custom)) DataRemoval.RemoveOdsModels(custom);
        var root = Engine.Paths.Root;
        await _host.QuitAsync();
        // The engine lock and log are closed now; remove the folder from a short-lived helper.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{root}\"") { CreateNoWindow = true, UseShellExecute = false });
    }

    /// <summary>Open Windows' uninstaller for ODS, optionally removing data too.</summary>
    [RelayCommand]
    private async Task UninstallAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Uninstall ODS",
            Content = "Keep your downloaded models and chats so a reinstall can reuse them, or remove them too?",
            PrimaryButtonText = "Uninstall, keep data",
            SecondaryButtonText = "Uninstall and remove data",
            CloseButtonText = "Cancel",
        };
        var result = await _host.Dialogs.ShowAsync(dialog, CancellationToken.None);
        if (result == ContentDialogResult.None) return;
        if (result == ContentDialogResult.Secondary) File.WriteAllText(Engine.Paths.RemoveDataMarker, DateTimeOffset.Now.ToString("O"));
        else if (File.Exists(Engine.Paths.RemoveDataMarker)) File.Delete(Engine.Paths.RemoveDataMarker);
        var updateExe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Environment.ProcessPath!)!)!, "Update.exe");
        if (!File.Exists(updateExe))
        {
            _host.Notify("Not installed", "This copy of ODS was not installed with the ODS installer. Delete its folder to remove it.");
            return;
        }
        AppLog.Info($"Uninstalling ODS ({(result == ContentDialogResult.Secondary ? "removing" : "keeping")} data).");
        await Engine.StopServerAsync();
        Process.Start(new ProcessStartInfo(updateExe, "--uninstall") { UseShellExecute = false });
        await _host.QuitAsync();
    }
}
