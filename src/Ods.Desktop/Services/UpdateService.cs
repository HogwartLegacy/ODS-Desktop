using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Ods.Core;
using Ods.Core.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace Ods.Desktop.Services;

/// <summary>
/// In-app updates through Velopack: check the release feed, download the
/// delta or full package in the background, and apply it on restart after
/// ODS has stopped its model server. Data under the ODS data folder is never
/// part of an update.
/// </summary>
public sealed partial class UpdateService(OdsEngine engine) : ObservableObject
{
    private UpdateManager? _manager;
    private UpdateInfo? _ready;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _readyToRestart;

    [ObservableProperty]
    private int _downloadPercent;

    public string CurrentVersion => Manager.IsInstalled ? Manager.CurrentVersion?.ToString() ?? OdsEngine.Version : OdsEngine.Version;

    public bool IsInstalled => Manager.IsInstalled;

    private UpdateManager Manager => _manager ??= CreateManager();

    /// <summary>
    /// The release feed: GitHub releases (default), any HTTPS folder with
    /// releases.win.json, or a local or network folder (an IT-managed share).
    /// </summary>
    private UpdateManager CreateManager()
    {
        var feed = engine.Settings.Updates.FeedUrl;
        IUpdateSource source;
        if (feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            source = new GithubSource(feed, accessToken: null, prerelease: engine.Settings.Updates.Prerelease);
        }
        else if (feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            source = new SimpleWebSource(feed);
        }
        else
        {
            source = new SimpleFileSource(new System.IO.DirectoryInfo(feed));
        }
        return new UpdateManager(source);
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (IsChecking || ReadyToRestart) return;
        if (!Manager.IsInstalled)
        {
            Status = "This copy of ODS was not installed with the ODS installer, so it does not update itself.";
            return;
        }
        IsChecking = true;
        Status = "Checking for updates…";
        try
        {
            var update = await Manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Status = $"ODS {CurrentVersion} is up to date.";
                return;
            }
            var version = update.TargetFullRelease.Version;
            Status = $"Downloading ODS {version}…";
            await Manager.DownloadUpdatesAsync(update, percent => App.Current.Dispatcher.BeginInvoke(() => DownloadPercent = percent));
            _ready = update;
            ReadyToRestart = true;
            Status = $"ODS {version} is ready. Restart ODS to finish updating.";
            AppLog.Info($"Update {version} downloaded and ready to apply.");
            if (!userInitiated) App.Host.Notify("Update ready", $"ODS {version} installs the next time you restart ODS.");
        }
        catch (HttpRequestException error)
        {
            Status = $"Could not check for updates: {error.Message}";
            AppLog.Warn(Status);
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Stop the model server, then let Velopack swap versions and relaunch ODS.</summary>
    public async Task RestartToUpdateAsync()
    {
        if (_ready is null) return;
        AppLog.Info($"Restarting to apply ODS {_ready.TargetFullRelease.Version}.");
        await engine.StopServerAsync();
        Manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }
}
