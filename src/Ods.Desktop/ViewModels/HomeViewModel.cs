using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ods.Core;
using Ods.Desktop.Converters;
using Ods.Desktop.Services;
using Ods.Desktop.Views.Pages;

namespace Ods.Desktop.ViewModels;

/// <summary>The Home page: status, this PC, the local API and live resource use.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _metrics;
    private TimeSpan _lastCpu;
    private DateTime _lastSample;
    private int? _sampledPid;

    public HomeViewModel(AppHost host)
    {
        _host = host;
        _metrics = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _metrics.Tick += (_, _) => SampleMetrics();
        _metrics.Start();
    }

    public ShellViewModel Shell => _host.Shell;

    private OdsEngine Engine => _host.Engine;

    [ObservableProperty]
    private string _cpu = "…";

    [ObservableProperty]
    private string _memory = "…";

    [ObservableProperty]
    private string _gpu = "…";

    [ObservableProperty]
    private string _acceleration = "…";

    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    private bool _showKey;

    [ObservableProperty]
    private string _processCpu = "–";

    [ObservableProperty]
    private string _processMemory = "–";

    [ObservableProperty]
    private string _uptime = "–";

    [ObservableProperty]
    private string _lastSpeed = "–";

    [ObservableProperty]
    private string _modelsFolder = "";

    [ObservableProperty]
    private string _diskFree = "";

    public string ApiKeyDisplay => ShowKey ? Engine.ApiKey : new string('•', 24) + Engine.ApiKey[^4..];

    partial void OnShowKeyChanged(bool value) => OnPropertyChanged(nameof(ApiKeyDisplay));

    public void RefreshHardware()
    {
        var hardware = Engine.Hardware;
        if (hardware is null) return;
        Cpu = $"{hardware.CpuName} · {hardware.LogicalProcessors} threads{(hardware.Avx2 ? " · AVX2" : "")}";
        Memory = $"{hardware.SystemRamGb} GB";
        var adapters = hardware.Adapters.Where(a => !a.IsSoftware).Select(a => $"{a.Name} · {Format.Bytes(a.DedicatedVideoMemoryBytes)}");
        Gpu = string.Join("\n", adapters.DefaultIfEmpty("None reported"));
        Acceleration = Engine.Runtime is { } runtime
            ? $"llama.cpp {runtime.Flavor.DisplayName} {runtime.Flavor.ReleaseTag} · {(runtime.Device == "none" ? "CPU" : runtime.Device)}"
            : $"llama.cpp {Engine.Plan?.Preferred.DisplayName} (not installed yet)";
        Notes.Clear();
        foreach (var note in Engine.Plan?.Notes ?? []) Notes.Add(note);
        ModelsFolder = Engine.ModelsDirectory;
        var root = Path.GetPathRoot(Path.GetFullPath(ModelsFolder));
        DiskFree = string.IsNullOrEmpty(root) ? "" : $"{Format.Bytes(new DriveInfo(root).AvailableFreeSpace)} free on {root}";
    }

    public void ReportSpeed(double tokensPerSecond) => LastSpeed = $"{tokensPerSecond:0.0} tokens/s";

    private void SampleMetrics()
    {
        var pid = Engine.Server.ProcessId;
        if (pid is not int id)
        {
            ProcessCpu = ProcessMemory = Uptime = "–";
            _sampledPid = null;
            return;
        }
        try
        {
            using var process = Process.GetProcessById(id);
            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            if (_sampledPid == id)
            {
                var share = (cpu - _lastCpu).TotalMilliseconds / ((now - _lastSample).TotalMilliseconds * Environment.ProcessorCount);
                ProcessCpu = $"{Math.Clamp(share, 0, 1):P0}";
            }
            _sampledPid = id;
            _lastCpu = cpu;
            _lastSample = now;
            ProcessMemory = Format.Bytes(process.WorkingSet64);
            var running = now - process.StartTime.ToUniversalTime();
            Uptime = running.TotalHours >= 1 ? $"{(int)running.TotalHours} h {running.Minutes} min" : $"{running.Minutes} min {running.Seconds} s";
        }
        catch (ArgumentException)
        {
            // The process exited between the id read and the sample.
            ProcessCpu = ProcessMemory = Uptime = "–";
        }
    }

    [RelayCommand]
    private void CopyEndpoint()
    {
        if (Shell.Endpoint is string endpoint) Clipboard.SetText(endpoint);
        _host.Notify("Copied", "The local API address is on the clipboard.");
    }

    [RelayCommand]
    private void CopyKey()
    {
        Clipboard.SetText(Engine.ApiKey);
        _host.Notify("Copied", "The API key is on the clipboard. Keep it private.");
    }

    [RelayCommand]
    private void CopyCurl()
    {
        var model = Engine.Settings.ActiveModel is { } active ? Engine.FindModel(active.Id).PrimaryFile : "model";
        var endpoint = Shell.Endpoint ?? "http://127.0.0.1:8080/v1";
        Clipboard.SetText(
            $"curl {endpoint}/chat/completions -H \"Authorization: Bearer {Engine.ApiKey}\" -H \"Content-Type: application/json\" " +
            $"-d \"{{\\\"model\\\": \\\"{model}\\\", \\\"messages\\\": [{{\\\"role\\\": \\\"user\\\", \\\"content\\\": \\\"Hello\\\"}}]}}\"");
        _host.Notify("Copied", "A sample request is on the clipboard.");
    }

    [RelayCommand]
    private void OpenModels() => _host.Navigate(typeof(ModelsPage));

    [RelayCommand]
    private void OpenModelsFolder() => AppHost.OpenFolder(Engine.ModelsDirectory);
}
