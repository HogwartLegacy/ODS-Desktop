using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ods.Core.Models;

namespace Ods.Core.Diagnostics;

/// <summary>
/// A support bundle: system and runtime report, settings, recent desktop and
/// llama-server logs, and model states. It never includes the API key,
/// conversations or model files, and the user chooses where to save it.
/// </summary>
public static class DiagnosticsBundle
{
    private const int DesktopLogDays = 7;
    private const int ServerLogs = 3;

    public static async Task<string> CreateAsync(OdsEngine engine, string zipPath, CancellationToken cancellationToken = default)
    {
        if (engine.Hardware is null) await engine.ProbeAsync(cancellationToken);
        var report = BuildReport(engine);
        var directory = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(directory);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddText(zip, "report.json", report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), engine.ApiKey);
            if (File.Exists(engine.Paths.SettingsFile))
            {
                AddText(zip, "settings.json", await File.ReadAllTextAsync(engine.Paths.SettingsFile, cancellationToken), engine.ApiKey);
            }
            var logs = new DirectoryInfo(engine.Paths.Logs);
            if (logs.Exists)
            {
                var cutoff = DateTime.Now.AddDays(-DesktopLogDays);
                var desktopLogs = logs.GetFiles("ods-*.log").Where(f => f.LastWriteTime >= cutoff);
                var serverLogs = logs.GetFiles("llama-server-*.log").OrderByDescending(f => f.Name).Take(ServerLogs);
                foreach (var file in desktopLogs.Concat(serverLogs))
                {
                    AddText(zip, "logs/" + file.Name, await ReadSharedAsync(file.FullName, cancellationToken), engine.ApiKey);
                }
            }
        }
        AppLog.Info($"Diagnostics bundle written to {zipPath}.");
        return zipPath;
    }

    public static JsonObject BuildReport(OdsEngine engine)
    {
        var hardware = engine.Hardware!;
        var status = engine.Server.Status;
        var runtime = engine.Settings.RuntimeFlavor is string flavor ? engine.RuntimeInstaller.Check(engine.Pins.Get(flavor)) : null;
        var models = new JsonArray();
        foreach (var entry in engine.Catalog.Entries.Where(e => engine.Models.StateOf(e) != ModelInstallState.NotInstalled))
        {
            models.Add(new JsonObject
            {
                ["id"] = entry.Id,
                ["state"] = engine.Models.StateOf(entry).ToString(),
                ["bytesOnDisk"] = engine.Models.BytesOnDisk(entry),
            });
        }
        return new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
            ["app"] = new JsonObject
            {
                ["version"] = OdsEngine.Version,
                ["catalogVersion"] = engine.Catalog.Version,
                ["dataRoot"] = engine.Paths.Root,
                ["modelsDirectory"] = engine.ModelsDirectory,
            },
            ["os"] = new JsonObject
            {
                ["description"] = hardware.OsDescription,
                ["build"] = hardware.OsBuild,
                ["architecture"] = hardware.Architecture,
            },
            ["hardware"] = new JsonObject
            {
                ["cpu"] = hardware.CpuName,
                ["logicalProcessors"] = hardware.LogicalProcessors,
                ["avx2"] = hardware.Avx2,
                ["avx512"] = hardware.Avx512,
                ["ramGb"] = hardware.SystemRamGb,
                ["availableRamBytes"] = hardware.AvailableRamBytes,
                ["gpu"] = JsonSerializer.SerializeToNode(hardware.Gpu),
                ["adapters"] = JsonSerializer.SerializeToNode(hardware.Adapters),
                ["notes"] = JsonSerializer.SerializeToNode(engine.Plan?.Notes ?? []),
            },
            ["runtime"] = new JsonObject
            {
                ["candidates"] = JsonSerializer.SerializeToNode(engine.Plan?.Candidates.Select(c => c.Flavor) ?? []),
                ["flavor"] = engine.Settings.RuntimeFlavor,
                ["device"] = engine.Settings.RuntimeDevice,
                ["verified"] = runtime?.Ok,
                ["problem"] = runtime?.Problem,
            },
            ["server"] = new JsonObject
            {
                ["state"] = status.State.ToString(),
                ["model"] = status.Model?.Id,
                ["contextSize"] = status.ContextSize,
                ["port"] = status.Port,
                ["failure"] = status.Failure is null ? null : JsonSerializer.SerializeToNode(status.Failure),
                ["recentOutput"] = JsonSerializer.SerializeToNode(engine.Server.RecentOutput().TakeLast(80)),
            },
            ["models"] = models,
            ["recentLog"] = JsonSerializer.SerializeToNode(AppLog.Recent().TakeLast(200).Select(e => e.ToString())),
        };
    }

    private static void AddText(ZipArchive zip, string name, string text, string secret)
    {
        if (secret.Length > 0) text = text.Replace(secret, "<redacted-api-key>", StringComparison.Ordinal);
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
