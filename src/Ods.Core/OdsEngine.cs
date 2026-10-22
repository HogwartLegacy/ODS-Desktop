using System.Reflection;
using Ods.Core.Catalog;
using Ods.Core.Chat;
using Ods.Core.Diagnostics;
using Ods.Core.Downloads;
using Ods.Core.Hardware;
using Ods.Core.Models;
using Ods.Core.Platform;
using Ods.Core.Runtime;
using Ods.Core.Settings;

namespace Ods.Core;

/// <summary>A user-visible step of a longer operation (setup, repair, switch).</summary>
public sealed record SetupStep(string Title, string? Detail = null, DownloadProgress? Download = null);

/// <summary>
/// The native Windows ODS engine: hardware detection, llama.cpp runtime
/// management, model storage and the model server, behind one façade that the
/// desktop app and ods-cli both use. It replaces what Docker Compose, the
/// ods CLI and the host agent do on Linux, for the core local-AI stack.
/// </summary>
public sealed partial class OdsEngine : IAsyncDisposable
{
    /// <summary>The generation proof ODS's Windows runtime uses: one right answer, one token.</summary>
    public const string ProbeQuestion = "What is 2+2? Answer with the number only.";

    private readonly ProcessJob _job;
    private readonly HttpClient _downloadHttp;
    private readonly HttpClient _apiHttp;
    private readonly HttpClient _chatHttp;
    private readonly EngineLock? _lock;

    private OdsEngine(OdsPaths paths, SettingsStore store, AppSettings settings, ModelCatalog catalog, RuntimePins pins, string apiKey, EngineLock? engineLock)
    {
        _lock = engineLock;
        Paths = paths;
        SettingsStore = store;
        Settings = settings;
        Catalog = catalog;
        Pins = pins;
        ApiKey = apiKey;
        _job = new ProcessJob();
        _downloadHttp = Downloader.CreateHttpClient();
        _apiHttp = LlamaApiClient.CreateHttpClient();
        _chatHttp = ChatClient.CreateHttpClient();
        Downloader = new Downloader(_downloadHttp);
        RuntimeInstaller = new RuntimeInstaller(paths.Runtimes, paths.Downloads, new Downloader(_downloadHttp)
        {
            AllowedInitialHosts = new HashSet<string>(["github.com"], StringComparer.OrdinalIgnoreCase),
        });
        Api = new LlamaApiClient(_apiHttp);
        Server = new LlamaServerHost(_job, paths.Logs, paths.ServerStateFile, Api);
        Chat = new ChatClient(_chatHttp);
        Conversations = new ConversationStore(paths.Conversations);
        Models = new ModelStore(ModelsDirectory, Downloader);
    }

    public static string Version { get; } =
        typeof(OdsEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public OdsPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; }

    public ModelCatalog Catalog { get; }

    public RuntimePins Pins { get; }

    public string ApiKey { get; }

    public Downloader Downloader { get; }

    public RuntimeInstaller RuntimeInstaller { get; }

    public LlamaApiClient Api { get; }

    public LlamaServerHost Server { get; }

    public ChatClient Chat { get; }

    public ConversationStore Conversations { get; }

    public ModelStore Models { get; private set; }

    public HardwareInfo? Hardware { get; private set; }

    public AccelerationPlan? Plan { get; private set; }

    public QualifiedRuntime? Runtime { get; private set; }

    public string ModelsDirectory => Settings.ModelsDirectory ?? Paths.DefaultModels;

    /// <summary>True when this engine owns the data folder and may run model servers.</summary>
    public bool IsOwner => _lock is not null;

    /// <summary>
    /// Open the engine. An owner (<paramref name="exclusive"/>) takes the data
    /// folder's lock and stops a model server a crashed session left behind; a
    /// read-only engine (ods-cli status) only reads state.
    /// </summary>
    public static OdsEngine Create(OdsPaths paths, bool exclusive = true, string logStem = "ods-desktop")
    {
        paths.EnsureCreated();
        var engineLock = exclusive ? EngineLock.Acquire(paths.State) : null;
        AppLog.Configure(paths.Logs, logStem);
        AppLog.Info($"ODS Desktop {Version} starting ({logStem}{(exclusive ? "" : ", read-only")}); data at {paths.Root}.");
        var store = new SettingsStore(paths.SettingsFile);
        var settings = store.Load();
        var catalog = ModelCatalog.Load(paths.CatalogFile);
        var pins = RuntimePins.Load(paths.PinsFile);
        var apiKey = ApiKeyFile.EnsureKey(paths.ApiKeyFile);
        if (exclusive) LlamaServerHost.KillOrphan(paths.ServerStateFile);
        return new OdsEngine(paths, store, settings, catalog, pins, apiKey, engineLock);
    }

    public void SaveSettings() => SettingsStore.Save(Settings);

    /// <summary>Point ODS at another models folder (models already there are reused).</summary>
    public void SetModelsDirectory(string? directory)
    {
        Settings.ModelsDirectory = string.IsNullOrWhiteSpace(directory) || Path.GetFullPath(directory) == Path.GetFullPath(Paths.DefaultModels)
            ? null
            : Path.GetFullPath(directory);
        SaveSettings();
        Models = new ModelStore(ModelsDirectory, Downloader);
        AppLog.Info($"Models folder set to {ModelsDirectory}.");
    }

    public async Task<HardwareInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        Hardware = await HardwareProbe.ProbeAsync(cancellationToken);
        Plan = AccelerationPlanner.Plan(Hardware, Pins);
        AppLog.Info($"Hardware: {Hardware.CpuName}, {Hardware.SystemRamGb} GB RAM, GPU {Hardware.Gpu.Name} ({Hardware.Gpu.Backend}, {Hardware.Gpu.VramMb} MB, {Hardware.Gpu.MemoryType}); "
            + $"runtime candidates: {string.Join(", ", Plan.Candidates.Select(c => c.Flavor))}.");
        foreach (var note in Plan.Notes) AppLog.Warn(note);
        return Hardware;
    }

    /// <summary>
    /// A verified, qualified llama.cpp runtime. The saved runtime is reused
    /// after a pin check; otherwise each planned build is installed and
    /// qualified in order, and a build that cannot use the GPU says why before
    /// the next is tried.
    /// </summary>
    public async Task<QualifiedRuntime> EnsureRuntimeAsync(IProgress<SetupStep>? progress, CancellationToken cancellationToken, bool requalify = false)
    {
        if (Hardware is null || Plan is null) await ProbeAsync(cancellationToken);
        if (!requalify && Settings.RuntimeFlavor is string savedFlavor && Settings.RuntimeDevice is string savedDevice
            && Plan!.Candidates.Any(c => c.Flavor == savedFlavor))
        {
            var flavor = Pins.Get(savedFlavor);
            progress?.Report(new SetupStep($"Checking llama.cpp {flavor.DisplayName}"));
            var check = await Task.Run(() => RuntimeInstaller.Check(flavor), cancellationToken);
            if (check.Ok)
            {
                Runtime = new QualifiedRuntime(new InstalledRuntime(flavor, RuntimeInstaller.DirectoryFor(flavor)), flavor.Build, [], savedDevice);
                return Runtime;
            }
            AppLog.Warn($"The saved llama.cpp runtime {savedFlavor} needs repair: {check.Problem}.");
        }

        var failures = new List<string>();
        foreach (var flavor in Plan!.Candidates)
        {
            var installProgress = new Progress<RuntimeInstallProgress>(p => progress?.Report(new SetupStep(p.Stage, null, p.Download)));
            var installed = await RuntimeInstaller.InstallAsync(flavor, installProgress, cancellationToken);
            progress?.Report(new SetupStep($"Checking that llama.cpp {flavor.DisplayName} can use this PC"));
            try
            {
                Runtime = await LlamaQualifier.QualifyAsync(installed, Hardware!.Gpu, cancellationToken);
            }
            catch (RuntimeQualificationException error)
            {
                failures.Add($"{flavor.DisplayName}: {error.Message}");
                AppLog.Warn($"llama.cpp {flavor.Flavor} did not qualify: {error.Message}");
                progress?.Report(new SetupStep($"llama.cpp {flavor.DisplayName} cannot run here", error.Message));
                continue;
            }
            Settings.RuntimeFlavor = flavor.Flavor;
            Settings.RuntimeDevice = Runtime.Device;
            SaveSettings();
            if (failures.Count > 0) AppLog.Warn($"Using llama.cpp {flavor.DisplayName} because: {string.Join(" ", failures)}");
            AppLog.Info($"Qualified llama.cpp {flavor.Flavor} (build {Runtime.Build}) on device {Runtime.Device}.");
            return Runtime;
        }
        throw new RuntimeQualificationException("No llama.cpp build could run on this PC. " + string.Join(" ", failures));
    }

    /// <summary>Reinstall the current runtime from its pinned archives.</summary>
    public async Task<QualifiedRuntime> RepairRuntimeAsync(IProgress<SetupStep>? progress, CancellationToken cancellationToken)
    {
        await Server.StopAsync();
        if (Settings.RuntimeFlavor is string flavor) RuntimeInstaller.Remove(Pins.Get(flavor));
        Settings.RuntimeFlavor = null;
        Settings.RuntimeDevice = null;
        return await EnsureRuntimeAsync(progress, cancellationToken, requalify: true);
    }

    public async Task StopServerAsync() => await Server.StopAsync();

    public async ValueTask DisposeAsync()
    {
        if (IsOwner) await Server.StopAsync();
        _job.Dispose();
        _downloadHttp.Dispose();
        _apiHttp.Dispose();
        _chatHttp.Dispose();
        _lock?.Dispose();
    }
}
