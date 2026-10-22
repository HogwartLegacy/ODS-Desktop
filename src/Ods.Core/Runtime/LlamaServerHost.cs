using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Ods.Core.Diagnostics;
using Ods.Core.Platform;

namespace Ods.Core.Runtime;

public enum ServerState
{
    Stopped,
    Starting,
    Loading,
    Ready,
    Stopping,
    Failed,
}

public sealed record ModelIdentity(string Id, string Name);

public sealed record ServerStatus(
    ServerState State,
    ModelIdentity? Model,
    int? ContextSize,
    int? Port,
    string? Flavor,
    string? Device,
    DateTimeOffset Since,
    string? Message,
    ServerFailure? Failure)
{
    public static ServerStatus Initial { get; } = new(ServerState.Stopped, null, null, null, null, null, DateTimeOffset.Now, null, null);

    public Uri? BaseUri => Port is int port ? new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}")) : null;

    public bool IsActive => State is ServerState.Starting or ServerState.Loading or ServerState.Ready or ServerState.Stopping;
}

/// <summary>Starting the server failed; <see cref="Failure"/> explains why.</summary>
public sealed class ServerStartException(ServerFailure failure) : Exception(failure.Title + ": " + failure.Detail)
{
    public ServerFailure Failure { get; } = failure;
}

/// <summary>
/// Owns one llama-server.exe process: starts it inside the ODS job object,
/// captures its output to a per-start log, proves readiness and reports
/// every state change. An exit nobody asked for becomes a classified
/// failure, never a silent restart. One exit-watcher task observes the
/// process; everything that disposes it awaits that task first.
/// </summary>
public sealed class LlamaServerHost(ProcessJob job, string logsDirectory, string stateFile, LlamaApiClient api) : IAsyncDisposable
{
    private const int RecentCapacity = 600;
    private const int KeptLogs = 10;
    private static readonly TimeSpan HealthInterval = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Queue<string> _recent = new();
    private Process? _process;
    private Task _exitWatcher = Task.CompletedTask;
    private StreamWriter? _log;
    private string _apiKey = "";
    private RuntimeFlavor? _flavor;

    public event EventHandler<ServerStatus>? StatusChanged;

    public event EventHandler<string>? OutputReceived;

    public ServerStatus Status { get; private set; } = ServerStatus.Initial;

    public string? CurrentLogFile { get; private set; }

    public string ApiKey => _apiKey;

    /// <summary>The running llama-server's process id, for live resource readouts.</summary>
    public int? ProcessId
    {
        get
        {
            var process = _process;
            return process is { HasExited: false } ? process.Id : null;
        }
    }

    public IReadOnlyList<string> RecentOutput()
    {
        lock (_gate) return _recent.ToList();
    }

    public async Task StartAsync(LlamaLaunchSpec spec, ModelIdentity model, string apiKey, TimeSpan startupTimeout, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (Status.IsActive) throw new InvalidOperationException("The model server is already running; stop it first.");
            await ReleaseProcessAsync();
            await StartCoreAsync(spec, model, apiKey, startupTimeout, cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Kill a llama-server a previous ODS session recorded and left behind.</summary>
    public static void KillOrphan(string stateFile)
    {
        if (!File.Exists(stateFile)) return;
        var state = JsonSerializer.Deserialize<ProcessState>(File.ReadAllText(stateFile));
        File.Delete(stateFile);
        if (state is null) return;
        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            var started = process.StartTime.ToUniversalTime();
            if (process.ProcessName.Equals("llama-server", StringComparison.OrdinalIgnoreCase)
                && Math.Abs((started - state.StartedUtc).TotalSeconds) < 2)
            {
                process.Kill(entireProcessTree: true);
                AppLog.Warn($"Stopped a llama-server (pid {state.ProcessId}) left running by an earlier ODS session.");
            }
        }
        catch (ArgumentException)
        {
            // The recorded process has already exited.
        }
    }

    private async Task StartCoreAsync(LlamaLaunchSpec spec, ModelIdentity model, string apiKey, TimeSpan startupTimeout, CancellationToken cancellationToken)
    {
        _apiKey = apiKey;
        _flavor = spec.Runtime.Flavor;
        lock (_gate) _recent.Clear();
        SetStatus(new ServerStatus(ServerState.Starting, model, spec.ContextSize, spec.Port, spec.Runtime.Flavor.Flavor, spec.Runtime.Device,
            DateTimeOffset.Now, "Starting llama.cpp", null));

        if (!PortProbe.IsFree(spec.Port))
        {
            Fail(FailureClassifier.Classify(null, ["couldn't bind: address already in use"], spec.Runtime.Flavor));
        }

        var (arguments, warnings) = LlamaArguments.Build(spec);
        foreach (var warning in warnings) AppLog.Warn(warning);
        OpenLog(model);
        WriteLine($"[ods] {spec.Runtime.Runtime.Executable} {string.Join(' ', arguments)}");

        var start = new ProcessStartInfo(spec.Runtime.Runtime.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.Runtime.Runtime.Directory,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Command-line flags are the whole configuration: drop inherited LLAMA_ARG_* overrides.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("LLAMA_ARG_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) WriteLine(e.Data); };
        process.Start();
        job.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _exitWatcher = WatchExitAsync(process);
        WriteState(process);
        AppLog.Info($"llama-server started (pid {process.Id}) for {model.Name} on port {spec.Port}, context {spec.ContextSize}, device {spec.Runtime.Device}.");

        SetStatus(Status with { State = ServerState.Loading, Since = DateTimeOffset.Now, Message = "Loading the model" });
        await WaitUntilHealthyAsync(process, startupTimeout, cancellationToken);
        await ProveAsync(spec, cancellationToken);
        SetStatus(Status with { State = ServerState.Ready, Since = DateTimeOffset.Now, Message = null });
        AppLog.Info($"llama-server is ready: {model.Name} at {Status.BaseUri} (context {Status.ContextSize}).");
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        var stopping = process is not null && !process.HasExited;
        if (stopping)
        {
            SetStatus(Status with { State = ServerState.Stopping, Since = DateTimeOffset.Now, Message = "Stopping" });
            process!.Kill(entireProcessTree: true);
        }
        await ReleaseProcessAsync();
        if (stopping) AppLog.Info("llama-server stopped.");
        SetStatus(ServerStatus.Initial with { Since = DateTimeOffset.Now });
    }

    private async Task WatchExitAsync(Process process)
    {
        // Completes after the exit and after the redirected output is drained.
        await process.WaitForExitAsync();
        if (File.Exists(stateFile)) File.Delete(stateFile);
        if (Status.State is ServerState.Stopping or ServerState.Stopped or ServerState.Failed) return;
        var failure = FailureClassifier.Classify(process.ExitCode, RecentOutput(), _flavor!);
        AppLog.Error($"llama-server exited unexpectedly (code {failure.ExitCode}): {failure.Title}. {failure.Detail}");
        SetStatus(Status with { State = ServerState.Failed, Since = DateTimeOffset.Now, Message = failure.Title, Failure = failure });
    }

    private async Task ReleaseProcessAsync()
    {
        var process = _process;
        if (process is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            await _exitWatcher;
        }
        lock (_gate)
        {
            _process = null;
            _log?.Dispose();
            _log = null;
        }
        process?.Dispose();
    }

    private async Task WaitUntilHealthyAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var baseUri = Status.BaseUri!;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await StopCoreAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (process.HasExited)
            {
                await _exitWatcher;
                throw new ServerStartException(Status.Failure ?? FailureClassifier.Classify(process.ExitCode, RecentOutput(), _flavor!));
            }
            var health = await api.HealthAsync(baseUri, _apiKey, CancellationToken.None);
            if (health == HttpStatusCode.OK) return;
            if (health == HttpStatusCode.Unauthorized)
            {
                await StopCoreAsync();
                Fail(Proof("llama-server rejected the ODS API key."));
            }
            if (clock.Elapsed > timeout)
            {
                var failure = FailureClassifier.Classify(null, RecentOutput(), _flavor!, timedOut: true);
                await StopCoreAsync();
                Fail(failure);
            }
            await Task.Delay(HealthInterval, CancellationToken.None);
        }
    }

    private async Task ProveAsync(LlamaLaunchSpec spec, CancellationToken cancellationToken)
    {
        var baseUri = Status.BaseUri!;
        var modelId = await api.ModelIdAsync(baseUri, _apiKey, cancellationToken);
        if (modelId != spec.Alias)
        {
            await StopCoreAsync();
            Fail(Proof($"llama-server serves '{modelId}', not '{spec.Alias}'."));
        }
        var props = await api.PropsAsync(baseUri, _apiKey, cancellationToken);
        if (Math.Abs(props.ContextSize - spec.ContextSize) >= 256)
        {
            AppLog.Warn($"llama-server runs a {props.ContextSize}-token context instead of the planned {spec.ContextSize} (the model's own limit applies).");
            SetStatus(Status with { ContextSize = props.ContextSize });
        }
    }

    private ServerFailure Proof(string detail) =>
        new(FailureKind.ProofFailed, "The model server did not prove it is ready", detail,
            RecoveryAction.Restart | RecoveryAction.OpenLogs, null, RecentOutput().TakeLast(12).ToList());

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(ServerFailure failure)
    {
        SetStatus(Status with { State = ServerState.Failed, Since = DateTimeOffset.Now, Message = failure.Title, Failure = failure });
        AppLog.Error($"Model server failure: {failure.Title}. {failure.Detail}");
        throw new ServerStartException(failure);
    }

    private void SetStatus(ServerStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void OpenLog(ModelIdentity model)
    {
        Directory.CreateDirectory(logsDirectory);
        foreach (var old in new DirectoryInfo(logsDirectory).GetFiles("llama-server-*.log").OrderByDescending(f => f.Name).Skip(KeptLogs - 1))
        {
            old.Delete();
        }
        CurrentLogFile = Path.Combine(logsDirectory, string.Create(CultureInfo.InvariantCulture, $"llama-server-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        var stream = new FileStream(CurrentLogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        lock (_gate) _log = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        WriteLine($"[ods] model {model.Id} ({model.Name})");
    }

    private void WriteLine(string line)
    {
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentCapacity) _recent.Dequeue();
            _log?.WriteLine(line);
        }
        OutputReceived?.Invoke(this, line);
    }

    private void WriteState(Process process)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var state = new ProcessState(process.Id, process.StartTime.ToUniversalTime(), Status.Port ?? 0, Status.Model?.Id ?? "");
        File.WriteAllText(stateFile, JsonSerializer.Serialize(state));
    }

    /// <summary>The running server another ODS process recorded, or null.</summary>
    public static ProcessState? ReadState(string stateFile)
    {
        if (!File.Exists(stateFile)) return null;
        using var stream = new FileStream(stateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<ProcessState>(stream);
    }

    public sealed record ProcessState(int ProcessId, DateTime StartedUtc, int Port, string ModelId);
}
