using System.Security.Cryptography;
using System.Text.Json;
using Ods.Core.Catalog;
using Ods.Core.Diagnostics;
using Ods.Core.Downloads;

namespace Ods.Core.Models;

public enum ModelInstallState
{
    NotInstalled,
    Partial,
    NeedsVerification,
    Installed,
}

/// <summary>A .gguf in the models folder; <see cref="Entry"/> is null for a file that is not in the catalog.</summary>
public sealed record LocalGguf(string Path, long Size, CatalogEntry? Entry)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The models folder. Catalog files are downloaded with their pinned SHA-256
/// and recorded in an index (size, write time, hash) so a verified 20 GB file
/// is not hashed again at every start; a file whose size or write time
/// changed is verified again before use.
/// </summary>
public sealed class ModelStore(string directory, Downloader downloader)
{
    private const string IndexFileName = ".ods-model-index.json";
    private readonly Lock _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _downloading = new(StringComparer.OrdinalIgnoreCase);

    public string Directory { get; } = Path.GetFullPath(directory);

    public string PathFor(string file) => Path.Combine(Directory, file);

    public string PrimaryPath(CatalogEntry entry) => PathFor(entry.PrimaryFile);

    public ModelInstallState StateOf(CatalogEntry entry)
    {
        if (entry.Files.Count == 0) return ModelInstallState.NotInstalled;
        var index = ReadIndex();
        var states = entry.Files.Select(file => FileState(file, index)).ToList();
        if (states.All(s => s == ModelInstallState.Installed)) return ModelInstallState.Installed;
        if (states.All(s => s is ModelInstallState.Installed or ModelInstallState.NeedsVerification)) return ModelInstallState.NeedsVerification;
        if (states.Any(s => s != ModelInstallState.NotInstalled)) return ModelInstallState.Partial;
        return ModelInstallState.NotInstalled;
    }

    /// <summary>Bytes already on disk for the entry, including partial downloads.</summary>
    public long BytesOnDisk(CatalogEntry entry) => entry.Files.Sum(file =>
    {
        var path = PathFor(file.File);
        if (File.Exists(path)) return new FileInfo(path).Length;
        return File.Exists(path + ".part") ? new FileInfo(path + ".part").Length : 0;
    });

    /// <summary>True while a download of this model is in progress (two downloads would share one .part file).</summary>
    public bool IsDownloading(CatalogEntry entry) => _downloading.ContainsKey(entry.Id);

    public async Task DownloadAsync(CatalogEntry entry, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!entry.IsDownloadable) throw new DownloadException($"{entry.Name} has no pinned download in the ODS catalog.");
        if (!_downloading.TryAdd(entry.Id, 0)) throw new DownloadException($"{entry.Name} is already downloading.");
        try
        {
            await DownloadFilesAsync(entry, progress, cancellationToken);
        }
        finally
        {
            _downloading.TryRemove(entry.Id, out _);
        }
    }

    private async Task DownloadFilesAsync(CatalogEntry entry, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var index = ReadIndex();
        for (var part = 0; part < entry.Files.Count; part++)
        {
            var file = entry.Files[part];
            if (FileState(file, index) == ModelInstallState.Installed) continue;
            var label = entry.Files.Count == 1 ? entry.Name : $"{entry.Name} (part {part + 1} of {entry.Files.Count})";
            var destination = PathFor(file.File);
            if (File.Exists(destination) && await HashAsync(destination, cancellationToken) == file.Sha256)
            {
                Record(file.File, file.Sha256);
                continue;
            }
            await downloader.DownloadAsync(new DownloadRequest(new Uri(file.Url), destination, file.Sha256, file.SizeBytes, label), progress, cancellationToken);
            Record(file.File, file.Sha256);
            AppLog.Info($"Downloaded and verified {file.File} ({new FileInfo(destination).Length:N0} bytes).");
        }
    }

    /// <summary>Hash files whose size or write time changed since they were verified.</summary>
    public async Task VerifyAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        var index = ReadIndex();
        foreach (var file in entry.Files)
        {
            if (FileState(file, index) == ModelInstallState.Installed) continue;
            var path = PathFor(file.File);
            if (!File.Exists(path)) throw new FileNotFoundException($"{entry.Name} is not downloaded ({file.File} is missing).", path);
            var actual = await HashAsync(path, cancellationToken);
            if (actual != file.Sha256)
            {
                throw new InvalidDataException($"{file.File} does not match the ODS catalog SHA-256 (got {actual}, expected {file.Sha256}). Delete it and download it again.");
            }
            Record(file.File, file.Sha256);
        }
    }

    public void Delete(CatalogEntry entry)
    {
        foreach (var file in entry.Files)
        {
            var path = PathFor(file.File);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".part")) File.Delete(path + ".part");
            Forget(file.File);
        }
        AppLog.Info($"Deleted {entry.Name} from {Directory}.");
    }

    public void DeleteLocal(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only files inside the models folder can be deleted from ODS.");
        }
        File.Delete(path);
        Forget(Path.GetFileName(path));
    }

    /// <summary>Every complete .gguf in the folder, matched to catalog entries by file name.</summary>
    public IReadOnlyList<LocalGguf> ScanLocal(ModelCatalog catalog)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var byFile = catalog.Entries
            .SelectMany(entry => entry.Files.Select((file, index) => (file.File, entry, index)))
            .GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<LocalGguf>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.gguf"))
        {
            var name = Path.GetFileName(path);
            if (byFile.TryGetValue(name, out var match))
            {
                // Later parts of a split model belong to their first part.
                if (match.index > 0) continue;
                result.Add(new LocalGguf(path, new FileInfo(path).Length, match.entry));
            }
            else if (!name.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new LocalGguf(path, new FileInfo(path).Length, null));
            }
        }
        return result;
    }

    private ModelInstallState FileState(GgufFile file, Dictionary<string, IndexEntry> index)
    {
        var path = PathFor(file.File);
        if (!File.Exists(path)) return File.Exists(path + ".part") ? ModelInstallState.Partial : ModelInstallState.NotInstalled;
        var info = new FileInfo(path);
        if (file.SizeBytes is long expected && info.Length != expected) return ModelInstallState.Partial;
        return index.TryGetValue(file.File, out var entry)
            && entry.Size == info.Length
            && entry.LastWriteUtc == info.LastWriteTimeUtc
            && entry.Sha256 == file.Sha256
                ? ModelInstallState.Installed
                : ModelInstallState.NeedsVerification;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private void Record(string file, string sha256)
    {
        var info = new FileInfo(PathFor(file));
        lock (_gate)
        {
            var index = ReadIndex();
            index[file] = new IndexEntry(info.Length, info.LastWriteTimeUtc, sha256);
            WriteIndex(index);
        }
    }

    private void Forget(string file)
    {
        lock (_gate)
        {
            var index = ReadIndex();
            if (index.Remove(file)) WriteIndex(index);
        }
    }

    private Dictionary<string, IndexEntry> ReadIndex()
    {
        var path = PathFor(IndexFileName);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(path));
            return new Dictionary<string, IndexEntry>(entries ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException error)
        {
            // The index is a cache of verifications; dropping it only means hashing again.
            AppLog.Warn($"The model index {path} was unreadable ({error.Message}); models will be verified again.");
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void WriteIndex(Dictionary<string, IndexEntry> index)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(IndexFileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private sealed record IndexEntry(long Size, DateTime LastWriteUtc, string Sha256);
}
