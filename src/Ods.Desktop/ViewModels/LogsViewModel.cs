using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Ods.Core.Diagnostics;
using Ods.Desktop.Services;

namespace Ods.Desktop.ViewModels;

/// <summary>Live ODS and llama-server logs, plus the support bundle export.</summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private const int MaxLines = 3000;
    private readonly AppHost _host;
    private readonly ConcurrentQueue<string> _appPending = new();
    private readonly ConcurrentQueue<string> _serverPending = new();
    private readonly LinkedList<string> _appLines = new();
    private readonly LinkedList<string> _serverLines = new();

    public LogsViewModel(AppHost host)
    {
        _host = host;
        foreach (var entry in AppLog.Recent()) _appPending.Enqueue(entry.ToString());
        foreach (var line in host.Engine.Server.RecentOutput()) _serverPending.Enqueue(line);
        AppLog.EntryWritten += entry => _appPending.Enqueue(entry.ToString());
        host.Engine.Server.OutputReceived += (_, line) => _serverPending.Enqueue(line);
        var flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        flush.Tick += (_, _) => Flush();
        flush.Start();
    }

    [ObservableProperty]
    private string _appLogText = "";

    [ObservableProperty]
    private string _serverLogText = "";

    /// <summary>0 = ODS, 1 = model server.</summary>
    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    private bool _isExporting;

    public event Action? Appended;

    private void Flush()
    {
        var changed = Drain(_appPending, _appLines, text => AppLogText = text) | Drain(_serverPending, _serverLines, text => ServerLogText = text);
        if (changed) Appended?.Invoke();
    }

    private static bool Drain(ConcurrentQueue<string> pending, LinkedList<string> lines, Action<string> publish)
    {
        if (pending.IsEmpty) return false;
        while (pending.TryDequeue(out var line))
        {
            lines.AddLast(line);
            if (lines.Count > MaxLines) lines.RemoveFirst();
        }
        var builder = new StringBuilder();
        foreach (var line in lines) builder.AppendLine(line);
        publish(builder.ToString());
        return true;
    }

    [RelayCommand]
    private void OpenFolder() => AppHost.OpenFolder(_host.Engine.Paths.Logs);

    [RelayCommand]
    private void Copy()
    {
        Clipboard.SetText(SelectedTab == 0 ? AppLogText : ServerLogText);
        _host.Notify("Copied", "The log is on the clipboard.");
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save an ODS support bundle",
            FileName = $"ods-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "Zip archive (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog() != true) return;
        IsExporting = true;
        try
        {
            await DiagnosticsBundle.CreateAsync(_host.Engine, dialog.FileName);
            _host.Notify("Support bundle saved", "It contains logs and system details, never your chats, models or API key.");
            AppHost.ShowInFolder(dialog.FileName);
        }
        finally
        {
            IsExporting = false;
        }
    }
}
