using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ods.Cli;
using Ods.Core;
using Ods.Core.Catalog;
using Ods.Core.Chat;
using Ods.Core.Diagnostics;
using Ods.Core.Downloads;
using Ods.Core.Models;
using Ods.Core.Platform;
using Ods.Core.Runtime;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var options = new Options(args.Skip(1).ToList());
try
{
    return command switch
    {
        "status" => await Commands.StatusAsync(cancel.Token),
        "hardware" => await Commands.HardwareAsync(options, cancel.Token),
        "recommend" => await Commands.RecommendAsync(options, cancel.Token),
        "runtime" => await Commands.RuntimeAsync(options, cancel.Token),
        "models" or "model" => await Commands.ModelsAsync(options, cancel.Token),
        "serve" => await Commands.ServeAsync(options, cancel.Token),
        "smoke" => await Commands.SmokeAsync(options, cancel.Token),
        "diagnostics" => await Commands.DiagnosticsAsync(options, cancel.Token),
        "version" or "--version" => Commands.Version(),
        _ => Commands.Help(),
    };
}
catch (Exception error) when (error is EngineBusyException or DownloadException or RuntimeQualificationException
    or ServerStartException or NoModelFitsException or KeyNotFoundException or ChatException or InvalidOperationException
    or FileNotFoundException)
{
    // The message is for the user; the full exception, with its stack, goes to the log.
    AppLog.Error($"ods-cli {command} failed.", error);
    Console.Error.WriteLine();
    Console.Error.WriteLine($"error: {error.Message}");
    return error is EngineBusyException ? 3 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

/// <summary>Command-line flags: positional values plus --name value / --switch.</summary>
internal sealed class Options(List<string> raw)
{
    public IReadOnlyList<string> Positional => raw.Where((value, index) => !value.StartsWith("--") && (index == 0 || !IsValueFlag(raw[index - 1]))).ToList();

    public bool Has(string name) => raw.Contains("--" + name);

    public string? Value(string name)
    {
        var index = raw.IndexOf("--" + name);
        return index >= 0 && index + 1 < raw.Count ? raw[index + 1] : null;
    }

    public int? Int(string name) => Value(name) is string text ? int.Parse(text, CultureInfo.InvariantCulture) : null;

    private static readonly HashSet<string> ValueFlags =
        ["--context", "--backend", "--memory-type", "--vram-mb", "--ram-gb", "--tier", "--host-arch", "--profile", "--json-out", "--min-context"];

    private static bool IsValueFlag(string flag) => ValueFlags.Contains(flag);
}

internal static class Commands
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Help()
    {
        Console.WriteLine($"""
            ods-cli {OdsEngine.Version} - native Windows ODS, without Docker or WSL

            Usage: ods-cli <command> [options]

              status                         Model server, runtime and model state
              hardware [--json]              Detected CPU, memory and GPUs, and the llama.cpp plan
              recommend [--json]             The model ODS picks for this PC (select-model.py policy)
                  [--backend nvidia|amd|cpu --vram-mb N --ram-gb N --memory-type discrete|unified|none
                   --tier T --host-arch amd64|arm64 --min-context N]   simulate other hardware
              runtime install|verify|repair  Manage the pinned llama.cpp runtime
              models [list]                  Catalog models, install state and fit
              models download|delete <id>    Download (verified) or delete a catalog model
              serve [<id>] [--context N]     Run the model server in the foreground (Ctrl+C stops)
              smoke [<id>] [--json-out F]    End-to-end check: runtime, model, server, generation
              diagnostics [<zip path>]       Write a support bundle
              version                        Print the version

            Data folder: {OdsPaths.Default().Root} (override with ODS_DESKTOP_HOME)
            """);
        return 0;
    }

    public static int Version()
    {
        Console.WriteLine($"ods-cli {OdsEngine.Version}");
        return 0;
    }

    public static async Task<int> StatusAsync(CancellationToken cancellationToken)
    {
        await using var engine = OdsEngine.Create(OdsPaths.Default(), exclusive: false, logStem: "ods-cli");
        var settings = engine.Settings;
        Console.WriteLine($"Data folder     {engine.Paths.Root}");
        Console.WriteLine($"Models folder   {engine.ModelsDirectory}");
        Console.WriteLine($"Setup complete  {(settings.OnboardingCompleted ? "yes" : "no")}");
        Console.WriteLine($"Runtime         {settings.RuntimeFlavor ?? "not installed"} (device {settings.RuntimeDevice ?? "-"})");
        if (settings.RuntimeFlavor is string flavor)
        {
            var check = engine.RuntimeInstaller.Check(engine.Pins.Get(flavor));
            Console.WriteLine($"Runtime pin     {(check.Ok ? "verified" : "problem: " + check.Problem)}");
        }
        Console.WriteLine($"Active model    {settings.ActiveModel?.Id ?? "none"}{(settings.ActiveModel is { } m ? $" at {m.ContextLength} context" : "")}");
        if (settings.PendingModel is { } pending) Console.WriteLine($"Downloading     {pending.Id} (switches over when complete)");

        var state = LlamaServerHost.ReadState(engine.Paths.ServerStateFile);
        if (state is null || !ProcessAlive(state.ProcessId))
        {
            Console.WriteLine("Model server    stopped");
            return 0;
        }
        var baseUri = new Uri($"http://127.0.0.1:{state.Port}");
        var health = await engine.Api.HealthAsync(baseUri, engine.ApiKey, cancellationToken);
        Console.WriteLine($"Model server    pid {state.ProcessId}, {baseUri} - {(health == System.Net.HttpStatusCode.OK ? "ready" : $"not ready ({(int)health})")}");
        Console.WriteLine($"API key file    {engine.Paths.ApiKeyFile}");
        return 0;
    }

    public static async Task<int> HardwareAsync(Options options, CancellationToken cancellationToken)
    {
        await using var engine = OdsEngine.Create(OdsPaths.Default(), exclusive: false, logStem: "ods-cli");
        var hardware = await engine.ProbeAsync(cancellationToken);
        if (options.Has("json"))
        {
            var node = JsonSerializer.SerializeToNode(hardware)!.AsObject();
            node["plan"] = JsonSerializer.SerializeToNode(engine.Plan!.Candidates.Select(c => c.Flavor));
            node["tier"] = engine.Plan.Tier;
            Console.WriteLine(node.ToJsonString(Json));
            return 0;
        }
        Console.WriteLine($"Windows    {hardware.OsDescription} ({hardware.Architecture})");
        Console.WriteLine($"CPU        {hardware.CpuName} - {hardware.LogicalProcessors} threads{(hardware.Avx2 ? ", AVX2" : "")}{(hardware.Avx512 ? ", AVX-512" : "")}");
        Console.WriteLine($"Memory     {hardware.SystemRamGb} GB ({hardware.AvailableRamBytes / 1073741824.0:0.0} GB free)");
        foreach (var adapter in hardware.Adapters.Where(a => !a.IsSoftware))
        {
            Console.WriteLine($"GPU        {adapter.Name} [{adapter.Vendor}] {adapter.DedicatedVideoMemoryMb} MB dedicated, driver {adapter.DriverVersion}");
        }
        if (hardware.Nvidia is { } nvidia)
        {
            Console.WriteLine($"NVIDIA     driver {nvidia.DriverVersion}, compute capability {nvidia.ComputeCapability}, {nvidia.Count} GPU(s)");
        }
        var gpu = hardware.Gpu;
        Console.WriteLine($"ODS sees   backend {gpu.Backend}, {gpu.VramMb} MB, {gpu.MemoryType} memory, tier {engine.Plan!.Tier}");
        Console.WriteLine($"llama.cpp  {string.Join(" -> ", engine.Plan.Candidates.Select(c => $"{c.Flavor} ({c.DownloadBytes / 1048576} MB)"))}");
        foreach (var note in engine.Plan.Notes) Console.WriteLine($"note       {note}");
        return 0;
    }

    public static async Task<int> RecommendAsync(Options options, CancellationToken cancellationToken)
    {
        await using var engine = OdsEngine.Create(OdsPaths.Default(), exclusive: false, logStem: "ods-cli");
        RecommendationRequest request;
        if (options.Value("backend") is string backend)
        {
            request = new RecommendationRequest(
                backend,
                options.Value("memory-type") ?? (backend == "cpu" ? "none" : "discrete"),
                options.Int("vram-mb") ?? 0,
                options.Int("ram-gb") ?? 16,
                options.Value("host-arch") ?? "amd64",
                options.Value("tier") ?? "1",
                options.Value("profile") ?? "qwen",
                MinContext: options.Int("min-context") ?? ModelRecommender.HermesMinContext);
        }
        else
        {
            await engine.ProbeAsync(cancellationToken);
            var e = engine.Envelope;
            request = new RecommendationRequest(e.Backend, e.MemoryType, e.VramMb, e.RamGb, e.HostArch, e.Tier,
                MinContext: options.Int("min-context") ?? ModelRecommender.HermesMinContext);
        }
        var recommendation = ModelRecommender.Recommend(engine.Catalog.SelectorModels, request);
        if (options.Has("json"))
        {
            Console.WriteLine(RecommendationJson(recommendation).ToJsonString(Json));
            return 0;
        }
        var selected = recommendation.Selected;
        Console.WriteLine($"Model      {selected.Model["name"]} ({selected.Id})");
        Console.WriteLine($"Context    {selected.ContextLength:N0} tokens{(selected.RuntimeProfileId is string p ? $", runtime profile {p}" : "")}");
        Console.WriteLine($"Memory     needs about {selected.RequiredGb} GiB of {recommendation.MemoryCapacityGb} GiB {recommendation.MemoryLabel}");
        Console.WriteLine($"Policy     {recommendation.Policy}");
        Console.WriteLine($"Why        {recommendation.Reason}");
        foreach (var alternative in recommendation.Alternatives.Skip(1))
        {
            Console.WriteLine($"Also fits  {alternative.Model["name"]} at {alternative.ContextLength:N0} context ({alternative.RequiredGb} GiB)");
        }
        return 0;
    }

    /// <summary>The select-model.py JSON payload shape, for parity checks.</summary>
    private static JsonObject RecommendationJson(Recommendation recommendation)
    {
        JsonObject Alternative(Candidate candidate) => new()
        {
            ["id"] = candidate.Id,
            ["name"] = (string?)candidate.Model["name"],
            ["gguf"] = (string?)candidate.Model["gguf_file"],
            ["estimated_required_gb"] = candidate.RequiredGb,
            ["context_length"] = candidate.ContextLength,
            ["runtime_profile"] = candidate.RuntimeProfileId,
            ["meets_min_context"] = candidate.MeetsMinContext,
            ["estimate_source"] = candidate.EstimateSource,
        };
        return new JsonObject
        {
            ["policy"] = recommendation.Policy,
            ["source"] = recommendation.Source,
            ["confidence"] = recommendation.Confidence,
            ["profile"] = recommendation.Profile,
            ["host_arch"] = recommendation.HostArch,
            ["memory_capacity_gb"] = recommendation.MemoryCapacityGb,
            ["memory_label"] = recommendation.MemoryLabel,
            ["memory_class"] = recommendation.MemoryClass,
            ["min_context"] = recommendation.MinContext,
            ["meets_min_context"] = recommendation.MeetsMinContext,
            ["selected"] = Alternative(recommendation.Selected),
            ["reason"] = recommendation.Reason,
            ["alternatives"] = new JsonArray(recommendation.Alternatives.Select(a => (JsonNode)Alternative(a)).ToArray()),
        };
    }

    public static async Task<int> RuntimeAsync(Options options, CancellationToken cancellationToken)
    {
        var action = options.Positional.FirstOrDefault() ?? "install";
        await using var engine = OdsEngine.Create(OdsPaths.Default(), logStem: "ods-cli");
        var progress = new ConsoleProgress();
        await engine.ProbeAsync(cancellationToken);
        switch (action)
        {
            case "verify":
                var flavor = engine.Settings.RuntimeFlavor ?? throw new InvalidOperationException("No llama.cpp runtime is installed yet.");
                var check = engine.RuntimeInstaller.Check(engine.Pins.Get(flavor), forceFullHash: true);
                Console.WriteLine(check.Ok ? $"{flavor}: every file matches pin.json" : $"{flavor}: {check.Problem}");
                return check.Ok ? 0 : 1;
            case "repair":
                var repaired = await engine.RepairRuntimeAsync(progress, cancellationToken);
                progress.CloseLine();
                Console.WriteLine($"Reinstalled llama.cpp {repaired.Flavor.Flavor}, device {repaired.Device}.");
                return 0;
            default:
                var runtime = await engine.EnsureRuntimeAsync(progress, cancellationToken, requalify: options.Has("requalify"));
                progress.CloseLine();
                Console.WriteLine($"llama.cpp {runtime.Flavor.Flavor} (build {runtime.Build}) is ready on device {runtime.Device}.");
                foreach (var device in runtime.Devices) Console.WriteLine($"  {device.Name}: {device.Description} ({device.TotalMb} MiB)");
                return 0;
        }
    }

    public static async Task<int> ModelsAsync(Options options, CancellationToken cancellationToken)
    {
        var action = options.Positional.FirstOrDefault() ?? "list";
        var id = options.Positional.Skip(1).FirstOrDefault();
        await using var engine = OdsEngine.Create(OdsPaths.Default(), exclusive: action != "list", logStem: "ods-cli");
        switch (action)
        {
            case "download":
                var entry = engine.FindModel(id ?? throw new InvalidOperationException("Name a model id, e.g. ods-cli models download qwen3.5-2b-q4"));
                var progress = new ConsoleProgress();
                await engine.DownloadModelAsync(entry, progress, cancellationToken);
                progress.CloseLine();
                Console.WriteLine($"{entry.Name} is downloaded and verified in {engine.ModelsDirectory}.");
                return 0;
            case "delete":
                var doomed = engine.FindModel(id ?? throw new InvalidOperationException("Name a model id to delete."));
                if (engine.Settings.ActiveModel?.Id == doomed.Id && LlamaServerHost.ReadState(engine.Paths.ServerStateFile) is not null)
                {
                    throw new InvalidOperationException($"{doomed.Name} is the active model; stop the model server first.");
                }
                engine.Models.Delete(doomed);
                Console.WriteLine($"Deleted {doomed.Name}.");
                return 0;
            default:
                await engine.ProbeAsync(cancellationToken);
                var ranked = engine.RankedModels().ToDictionary(c => c.Id);
                Console.WriteLine($"{"Model",-34} {"State",-12} {"Size",8}  Fit on this PC");
                foreach (var model in engine.Catalog.Entries.Where(e => e.IsCurated && e.IsDownloadable).OrderByDescending(e => ranked.ContainsKey(e.Id)).ThenBy(e => e.DownloadBytes))
                {
                    var fit = ranked.TryGetValue(model.Id, out var candidate) ? $"{candidate.ContextLength / 1024}K context, {candidate.RequiredGb} GiB" : "does not fit";
                    Console.WriteLine($"{model.Id,-34} {engine.Models.StateOf(model),-12} {model.DownloadBytes / 1073741824.0,6:0.0}GB  {fit}");
                }
                foreach (var local in engine.Models.ScanLocal(engine.Catalog).Where(l => l.Entry is null))
                {
                    Console.WriteLine($"{ModelCatalog.LocalPrefix + local.FileName,-34} {"Local file",-12} {local.Size / 1073741824.0,6:0.0}GB  not in the catalog");
                }
                return 0;
        }
    }

    public static async Task<int> ServeAsync(Options options, CancellationToken cancellationToken)
    {
        await using var engine = OdsEngine.Create(OdsPaths.Default(), logStem: "ods-cli");
        var progress = new ConsoleProgress();
        await engine.ProbeAsync(cancellationToken);
        await engine.EnsureRuntimeAsync(progress, cancellationToken);
        var id = options.Positional.FirstOrDefault() ?? engine.Settings.ActiveModel?.Id ?? engine.Recommend().Selected.Id;
        var entry = engine.FindModel(id);
        if (!engine.IsInstalled(entry)) await engine.DownloadModelAsync(entry, progress, cancellationToken);
        engine.Server.OutputReceived += (_, line) => { if (options.Has("verbose")) Console.WriteLine($"    {line}"); };
        await engine.StartServerAsync(entry.Id, options.Int("context"), progress, cancellationToken);
        progress.CloseLine();
        var status = engine.Server.Status;
        Console.WriteLine($"Serving {entry.Name} at {status.BaseUri}v1 (OpenAI-compatible), context {status.ContextSize}.");
        Console.WriteLine($"API key: {engine.Paths.ApiKeyFile}. Press Ctrl+C to stop.");
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopping...");
        }
        await engine.StopServerAsync();
        return 0;
    }

    public static async Task<int> SmokeAsync(Options options, CancellationToken cancellationToken)
    {
        var results = new JsonObject { ["version"] = OdsEngine.Version, ["startedAt"] = DateTimeOffset.Now.ToString("O") };
        var clock = Stopwatch.StartNew();
        var passed = false;
        await using var engine = OdsEngine.Create(OdsPaths.Default(), logStem: "ods-cli");
        try
        {
            var progress = new ConsoleProgress();
            Console.WriteLine("[1/6] Hardware");
            var hardware = await engine.ProbeAsync(cancellationToken);
            results["gpu"] = $"{hardware.Gpu.Name} ({hardware.Gpu.Backend}, {hardware.Gpu.VramMb} MB)";
            results["ramGb"] = hardware.SystemRamGb;

            Console.WriteLine("[2/6] llama.cpp runtime");
            var runtime = await engine.EnsureRuntimeAsync(progress, cancellationToken);
            progress.CloseLine();
            results["runtime"] = runtime.Flavor.Flavor;
            results["device"] = runtime.Device;
            results["runtimeSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);

            Console.WriteLine("[3/6] Model");
            var recommendation = engine.Recommend();
            results["recommended"] = recommendation.Selected.Id;
            var entry = engine.FindModel(options.Positional.FirstOrDefault() ?? OdsEngine.BootstrapModelId);
            if (!engine.IsInstalled(entry)) await engine.DownloadModelAsync(entry, progress, cancellationToken);
            progress.CloseLine();
            results["model"] = entry.Id;

            Console.WriteLine("[4/6] Model server");
            var startClock = Stopwatch.StartNew();
            await engine.StartServerAsync(entry.Id, options.Int("context"), progress, cancellationToken);
            results["startSeconds"] = Math.Round(startClock.Elapsed.TotalSeconds, 1);
            results["contextSize"] = engine.Server.Status.ContextSize;
            Console.WriteLine($"      ready at {engine.Server.Status.BaseUri} in {startClock.Elapsed.TotalSeconds:0.0} s, context {engine.Server.Status.ContextSize}");

            Console.WriteLine("[5/6] Generation proof");
            var probe = await engine.ProbeGenerationAsync(cancellationToken);
            results["probeAnswer"] = probe.Answer;
            results["probePassed"] = probe.Passed;
            Console.WriteLine($"      \"{OdsEngine.ProbeQuestion}\" -> \"{probe.Answer}\" ({probe.Elapsed.TotalSeconds:0.0} s) {(probe.Passed ? "PASS" : "FAIL")}");

            Console.WriteLine("[6/6] Streaming chat");
            var reply = new System.Text.StringBuilder();
            ChatTimings? timings = null;
            var status = engine.Server.Status;
            await foreach (var delta in engine.Chat.StreamAsync(status.BaseUri!, engine.ApiKey, entry.PrimaryFile,
                [new ChatMessage("user", "In one short sentence, what does a local AI server do?")], new ChatRequestOptions(0.2, 64), cancellationToken))
            {
                reply.Append(delta.Content);
                timings = delta.Timings ?? timings;
            }
            results["chatReply"] = reply.ToString().Trim();
            results["tokensPerSecond"] = timings is null ? null : Math.Round(timings.TokensPerSecond, 1);
            Console.WriteLine($"      {reply.ToString().Trim()}");
            if (timings is not null) Console.WriteLine($"      {timings.PredictedTokens} tokens at {timings.TokensPerSecond:0.0} tokens/s");
            passed = probe.Passed && reply.Length > 0;
        }
        finally
        {
            await engine.StopServerAsync();
            results["passed"] = passed;
            results["totalSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            if (options.Value("json-out") is string output) await File.WriteAllTextAsync(output, results.ToJsonString(Json), CancellationToken.None);
        }
        Console.WriteLine(passed ? "SMOKE PASSED" : "SMOKE FAILED");
        return passed ? 0 : 2;
    }

    public static async Task<int> DiagnosticsAsync(Options options, CancellationToken cancellationToken)
    {
        await using var engine = OdsEngine.Create(OdsPaths.Default(), exclusive: false, logStem: "ods-cli");
        var path = options.Positional.FirstOrDefault() ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"ods-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await DiagnosticsBundle.CreateAsync(engine, path, cancellationToken);
        Console.WriteLine($"Wrote {path}");
        return 0;
    }

    private static bool ProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
