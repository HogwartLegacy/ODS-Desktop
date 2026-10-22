using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ods.Core.Diagnostics;
using Ods.Core.Downloads;

namespace Ods.Core.Runtime;

public sealed record InstalledRuntime(RuntimeFlavor Flavor, string Directory)
{
    public string Executable => Path.Combine(Directory, "llama-server.exe");
}

public sealed record RuntimeCheck(bool Ok, string? Problem)
{
    public static RuntimeCheck Good { get; } = new(true, null);

    public static RuntimeCheck Bad(string problem) => new(false, problem);
}

public sealed record RuntimeInstallProgress(string Stage, DownloadProgress? Download = null);

/// <summary>pin.json, the install record ODS writes beside every llama.cpp runtime.</summary>
public sealed record RuntimePinRecord(
    int SchemaVersion,
    string ReleaseTag,
    string Flavor,
    IReadOnlyList<RuntimePinRecord.Archive> Archives,
    string ZipSha256,
    IReadOnlyList<RuntimePinRecord.FileEntry> Files,
    string Source,
    string InstalledBy,
    DateTimeOffset InstalledAt)
{
    public sealed record Archive(string Asset, string Sha256, long Size);

    public sealed record FileEntry(string Path, string Sha256, long Size);
}

/// <summary>
/// Installs pinned llama.cpp builds into &lt;data&gt;\runtimes\&lt;tag&gt;-&lt;flavor&gt;
/// following ODS's native-runtime contract: archives are size- and
/// SHA-256-checked before extraction, extraction is staged and published
/// atomically, and pin.json records a SHA-256 for every file so the runtime
/// is verified again before it is launched.
/// </summary>
public sealed class RuntimeInstaller(string runtimesRoot, string downloadsRoot, Downloader downloader)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly string[] ExecutableExtensions = [".exe", ".dll", ".sys", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".cpl", ".ocx"];
    private readonly HashSet<string> _verifiedThisSession = new(StringComparer.OrdinalIgnoreCase);

    public string DirectoryFor(RuntimeFlavor flavor) => Path.Combine(runtimesRoot, flavor.DirectoryName);

    /// <summary>
    /// Verify an installed runtime against its pin. The full per-file hash runs
    /// once per session (or when forced); later launches check sizes and the
    /// file list only.
    /// </summary>
    public RuntimeCheck Check(RuntimeFlavor flavor, bool forceFullHash = false)
    {
        var directory = DirectoryFor(flavor);
        var pinPath = Path.Combine(directory, "pin.json");
        if (!File.Exists(pinPath)) return RuntimeCheck.Bad("not installed");
        RuntimePinRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<RuntimePinRecord>(File.ReadAllText(pinPath), JsonOptions);
        }
        catch (JsonException error)
        {
            return RuntimeCheck.Bad($"pin.json is unreadable ({error.Message})");
        }
        if (record is null || record.SchemaVersion != 2 || record.ReleaseTag != flavor.ReleaseTag || record.Flavor != flavor.Flavor)
        {
            return RuntimeCheck.Bad("the installed build is not the pinned one");
        }
        var pinned = flavor.Archives.Select(a => $"{a.Asset}|{a.Sha256}");
        if (!pinned.SequenceEqual(record.Archives.Select(a => $"{a.Asset}|{a.Sha256}"))) return RuntimeCheck.Bad("the installed archives are not the pinned ones");

        var listed = record.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var required in flavor.RequiredFiles)
        {
            if (!listed.ContainsKey(required)) return RuntimeCheck.Bad($"{required} is missing from pin.json");
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file);
            if (relative.Equals("pin.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!listed.ContainsKey(relative) && ExecutableExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
            {
                return RuntimeCheck.Bad($"unexpected executable file {relative} (llama-server loads DLLs from its folder)");
            }
        }
        var fullHash = forceFullHash || !_verifiedThisSession.Contains(directory);
        foreach (var entry in record.Files)
        {
            var path = Path.Combine(directory, entry.Path);
            var info = new FileInfo(path);
            if (!info.Exists) return RuntimeCheck.Bad($"{entry.Path} is missing");
            if (info.Length != entry.Size) return RuntimeCheck.Bad($"{entry.Path} changed size");
            if (fullHash && HashFile(path) != entry.Sha256) return RuntimeCheck.Bad($"{entry.Path} does not match its recorded SHA-256");
        }
        _verifiedThisSession.Add(directory);
        return RuntimeCheck.Good;
    }

    public async Task<InstalledRuntime> InstallAsync(RuntimeFlavor flavor, IProgress<RuntimeInstallProgress>? progress, CancellationToken cancellationToken)
    {
        var target = DirectoryFor(flavor);
        if (Check(flavor).Ok) return new InstalledRuntime(flavor, target);

        Directory.CreateDirectory(downloadsRoot);
        var archives = new List<string>();
        foreach (var archive in flavor.Archives)
        {
            var path = Path.Combine(downloadsRoot, archive.Asset);
            var label = $"llama.cpp {flavor.DisplayName} ({archive.Asset})";
            var download = new Progress<DownloadProgress>(p => progress?.Report(new RuntimeInstallProgress($"Downloading {label}", p)));
            if (!File.Exists(path) || new FileInfo(path).Length != archive.Size || HashFile(path) != archive.Sha256)
            {
                await downloader.DownloadAsync(new DownloadRequest(archive.Url, path, archive.Sha256, archive.Size, label), download, cancellationToken);
            }
            archives.Add(path);
        }

        progress?.Report(new RuntimeInstallProgress($"Installing llama.cpp {flavor.DisplayName}"));
        Directory.CreateDirectory(runtimesRoot);
        var staging = Path.Combine(runtimesRoot, $".staging-{flavor.DirectoryName}-{Guid.NewGuid():N}");
        try
        {
            var files = new Dictionary<string, RuntimePinRecord.FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var archive in archives) Extract(archive, staging, files, cancellationToken);
            foreach (var required in flavor.RequiredFiles)
            {
                if (!files.ContainsKey(required)) throw new InvalidDataException($"The pinned llama.cpp archive does not contain {required}.");
            }
            var record = new RuntimePinRecord(
                2,
                flavor.ReleaseTag,
                flavor.Flavor,
                flavor.Archives.Select(a => new RuntimePinRecord.Archive(a.Asset, a.Sha256, a.Size)).ToList(),
                flavor.Archives[0].Sha256,
                files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                $"https://github.com/ggml-org/llama.cpp/releases/tag/{flavor.ReleaseTag}",
                $"ODS Desktop {typeof(RuntimeInstaller).Assembly.GetName().Version}",
                DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(staging, "pin.json"), JsonSerializer.Serialize(record, JsonOptions));

            if (Directory.Exists(target))
            {
                AppLog.Warn($"Replacing the llama.cpp runtime at {target}: {Check(flavor).Problem}.");
                Directory.Delete(target, recursive: true);
            }
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        foreach (var archive in archives) File.Delete(archive);
        _verifiedThisSession.Remove(target);
        var check = Check(flavor, forceFullHash: true);
        if (!check.Ok) throw new InvalidDataException($"The llama.cpp runtime failed verification right after installing: {check.Problem}.");
        AppLog.Info($"Installed llama.cpp {flavor.ReleaseTag} {flavor.Flavor} at {target}.");
        return new InstalledRuntime(flavor, target);
    }

    public void Remove(RuntimeFlavor flavor)
    {
        var target = DirectoryFor(flavor);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        _verifiedThisSession.Remove(target);
    }

    private static void Extract(string archive, string staging, Dictionary<string, RuntimePinRecord.FileEntry> files, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(staging, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) || relative.Contains(':'))
            {
                throw new InvalidDataException($"{Path.GetFileName(archive)} contains an unsafe path: {entry.FullName}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string sha256;
            using (var input = entry.Open())
            {
                if (File.Exists(destination))
                {
                    sha256 = HashStream(input);
                    if (files.TryGetValue(relative, out var previous) && previous.Sha256 == sha256) continue;
                    throw new InvalidDataException($"The llama.cpp archives disagree about {relative}; refusing a mixed runtime.");
                }
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    output.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                }
                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            files[relative] = new RuntimePinRecord.FileEntry(relative, sha256, new FileInfo(destination).Length);
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return HashStream(stream);
    }

    private static string HashStream(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
}
