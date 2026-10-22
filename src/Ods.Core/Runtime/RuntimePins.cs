using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ods.Core.Runtime;

public sealed record RuntimeArchive(string Asset, string Sha256, long Size, Uri Url);

/// <summary>One pinned llama.cpp Windows build (a release tag and flavor).</summary>
public sealed record RuntimeFlavor(
    string Flavor,
    string Backend,
    string Architecture,
    string ReleaseTag,
    Version? MinDriverVersion,
    Version? MinComputeCapability,
    Version? MaxComputeCapability,
    IReadOnlyList<RuntimeArchive> Archives,
    IReadOnlyList<string> RequiredFiles)
{
    public int Build => int.Parse(ReleaseTag.AsSpan(1), CultureInfo.InvariantCulture);

    public long DownloadBytes => Archives.Sum(a => a.Size);

    /// <summary>Install folder name, as ODS uses: b9014-win-vulkan-x64.</summary>
    public string DirectoryName => $"{ReleaseTag}-{Flavor}";

    public bool IsGpu => Backend is "cuda" or "vulkan";

    /// <summary>"CUDA 12.4", "Vulkan" or "CPU" for the UI.</summary>
    public string DisplayName => Backend switch
    {
        "cuda" => "CUDA " + Regex.Match(Flavor, @"cuda-(\d+\.\d+)").Groups[1].Value,
        "vulkan" => "Vulkan",
        _ => "CPU",
    };
}

/// <summary>
/// ods/desktop/windows/runtime-pins.json. Loading validates the same shape
/// Assert-ODSNativeLlamaPin enforces for the installer's pins: a bNNNN tag,
/// official asset names, lowercase SHA-256 and byte sizes.
/// </summary>
public sealed partial class RuntimePins
{
    [GeneratedRegex("^b[0-9]{3,6}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$")]
    private static partial Regex FileNamePattern();

    private RuntimePins(List<RuntimeFlavor> flavors) => Flavors = flavors;

    public IReadOnlyList<RuntimeFlavor> Flavors { get; }

    public RuntimeFlavor Get(string flavor) =>
        Flavors.SingleOrDefault(f => f.Flavor == flavor)
        ?? throw new KeyNotFoundException($"ODS has no pinned llama.cpp build '{flavor}'.");

    public static RuntimePins Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException($"{path} is empty.");
        if ((int?)root["schemaVersion"] != 1) throw new InvalidDataException($"{path} has an unsupported schemaVersion.");
        var flavors = root["flavors"]?.AsArray().Select(node => Parse(node!.AsObject())).ToList()
            ?? throw new InvalidDataException($"{path} lists no flavors.");
        var duplicate = flavors.GroupBy(f => f.Flavor).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"{path} pins {duplicate.Key} twice.");
        return new RuntimePins(flavors);
    }

    private static RuntimeFlavor Parse(JsonObject pin)
    {
        var flavor = (string?)pin["flavor"] ?? "";
        var tag = (string?)pin["release_tag"] ?? "";
        var backend = (string?)pin["backend"] ?? "";
        var architecture = (string?)pin["architecture"] ?? "";
        string Problem(string what) => $"The llama.cpp pin '{flavor}' must name {what}.";

        if (!TagPattern().IsMatch(tag)) throw new InvalidDataException(Problem("a bNNNN release_tag"));
        if (backend is not ("cuda" or "vulkan" or "cpu")) throw new InvalidDataException(Problem("a cuda, vulkan or cpu backend"));
        if (architecture is not ("x64" or "arm64")) throw new InvalidDataException(Problem("an x64 or arm64 architecture"));
        if (!flavor.StartsWith($"win-{backend}", StringComparison.Ordinal) || !flavor.EndsWith($"-{architecture}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(Problem("a win-<backend>-...-<architecture> flavor"));
        }

        var expectedAssets = new[] { $"llama-{tag}-bin-{flavor}.zip", $"cudart-llama-bin-{flavor}.zip" };
        var archives = pin["archives"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        if (archives.Count is < 1 or > 2 || (archives.Count == 2 && backend != "cuda"))
        {
            throw new InvalidDataException(Problem("one release archive (two for CUDA: llama.cpp and cudart)"));
        }
        var parsedArchives = new List<RuntimeArchive>();
        for (var index = 0; index < archives.Count; index++)
        {
            var asset = (string?)archives[index]["asset"];
            var sha256 = (string?)archives[index]["sha256"] ?? "";
            var size = (long?)archives[index]["size"] ?? 0;
            if (asset != expectedAssets[index] || !Sha256Pattern().IsMatch(sha256) || size is < 1048576 or > 536870912)
            {
                throw new InvalidDataException(Problem($"archive {index + 1} as {expectedAssets[index]} with a lowercase SHA-256 and a size in bytes"));
            }
            parsedArchives.Add(new RuntimeArchive(asset, sha256, size,
                new Uri($"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{asset}")));
        }

        var required = pin["required_files"]?.AsArray().Select(n => (string?)n ?? "").ToList() ?? [];
        if (!required.Contains("llama-server.exe") || required.Any(f => !FileNamePattern().IsMatch(f))
            || required.Distinct(StringComparer.OrdinalIgnoreCase).Count() != required.Count)
        {
            throw new InvalidDataException(Problem("distinct ASCII required_files including llama-server.exe"));
        }

        return new RuntimeFlavor(
            flavor,
            backend,
            architecture,
            tag,
            ParseVersion(pin["min_driver_version"]),
            ParseVersion(pin["min_compute_capability"]),
            ParseVersion(pin["max_compute_capability"]),
            parsedArchives,
            required);
    }

    private static Version? ParseVersion(JsonNode? node) =>
        node is null ? null : Version.Parse((string)node!);
}
