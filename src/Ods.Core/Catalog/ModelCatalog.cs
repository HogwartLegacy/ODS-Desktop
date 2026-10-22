using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ods.Core.Catalog;

/// <summary>One downloadable file of a catalog model (a single GGUF or one split part).</summary>
public sealed record GgufFile(string File, string Url, string Sha256, long? SizeBytes);

/// <summary>A typed view of one config/model-library.json entry for the app.</summary>
public sealed class CatalogEntry
{
    internal CatalogEntry(JsonObject raw, JsonObject? normalized)
    {
        Raw = raw;
        Normalized = normalized;
        Id = Py.Str(Py.Get(raw, "id"));
        Name = Py.Str(Py.Get(raw, "name")) is { Length: > 0 } name ? name : Id;
        Family = Py.Str(Py.Get(raw, "family"));
        Description = Py.Str(Py.Get(raw, "description"));
        Specialty = Py.Str(Py.Get(raw, "specialty")) is { Length: > 0 } specialty ? specialty : "General";
        Quantization = Py.Str(Py.Get(raw, "quantization"));
        LlmModelName = Py.Str(Py.Get(raw, "llm_model_name")) is { Length: > 0 } llm ? llm : Id;
        ContextLength = Py.IntOr(Py.Get(raw, "context_length"));
        MaxContextLength = Py.IntOr(Py.Get(raw, "max_context_length")) is > 0 and var max ? max : ContextLength;
        Source = Py.Str(Py.Get(raw, "source")).Trim().ToLowerInvariant();
        InstallRecommendation = Py.ValueEnabled(raw.ContainsKey("install_recommendation")
            ? Py.Get(raw, "install_recommendation")
            : JsonValue.Create(true));
        Files = ReadFiles(raw);
        PrimaryFile = Files.Count > 0 ? Files[0].File : Py.Str(Py.Get(raw, "gguf_file"));
        var declaredBytes = Py.PositiveNumber(Py.Get(raw, "size_bytes"));
        var partsBytes = Files.All(f => f.SizeBytes is not null) ? Files.Sum(f => f.SizeBytes!.Value) : 0;
        DownloadBytes = partsBytes > 0 ? partsBytes
            : declaredBytes > 0 ? (long)declaredBytes
            : (long)(Py.PositiveNumber(Py.Get(raw, "size_mb")) * ModelMemory.Mib);
    }

    public JsonObject Raw { get; }

    /// <summary>The select-model.py normalized record, or null for an entry the selector skips.</summary>
    public JsonObject? Normalized { get; }

    public string Id { get; }
    public string Name { get; }
    public string Family { get; }
    public string Description { get; }
    public string Specialty { get; }
    public string Quantization { get; }
    public string LlmModelName { get; }
    public int ContextLength { get; }
    public int MaxContextLength { get; }
    public string Source { get; }
    public bool InstallRecommendation { get; }
    public IReadOnlyList<GgufFile> Files { get; }

    /// <summary>The file llama-server loads (the first part of a split GGUF).</summary>
    public string PrimaryFile { get; }

    /// <summary>Total bytes to download; exact when the catalog declares sizes.</summary>
    public long DownloadBytes { get; }

    public bool IsCurated => Source is "" or "curated";

    public bool IsDownloadable => Files.Count > 0 && Files.All(f => f.Url.StartsWith("https://", StringComparison.Ordinal) && f.Sha256.Length == 64);

    private static List<GgufFile> ReadFiles(JsonObject raw)
    {
        if (Py.Get(raw, "gguf_parts") is JsonArray parts && parts.Count > 0)
        {
            return parts.OfType<JsonObject>().Select(part => new GgufFile(
                Py.Str(Py.Get(part, "file")),
                Py.Str(Py.Get(part, "url")),
                Py.Str(Py.Get(part, "sha256")).ToLowerInvariant(),
                Py.PositiveNumber(Py.Get(part, "size_bytes")) is > 0 and var bytes ? (long)bytes : null)).ToList();
        }
        var file = Py.Str(Py.Get(raw, "gguf_file"));
        if (file.Length == 0) return [];
        var size = Py.PositiveNumber(Py.Get(raw, "size_bytes"));
        return
        [
            new GgufFile(
                file,
                Py.Str(Py.Get(raw, "gguf_url")),
                Py.Str(Py.Get(raw, "gguf_sha256")).ToLowerInvariant(),
                size > 0 ? (long)size : null),
        ];
    }
}

/// <summary>
/// config/model-library.json, loaded once. <see cref="SelectorModels"/> is
/// what scripts/select-model.py ranks (curated entries, normalized);
/// <see cref="Entries"/> keeps every entry for the Models page.
/// </summary>
public sealed class ModelCatalog
{
    private ModelCatalog(int version, List<CatalogEntry> entries)
    {
        Version = version;
        Entries = entries;
        SelectorModels = entries.Where(e => e.IsCurated && e.Normalized is not null).Select(e => e.Normalized!).ToList();
    }

    public int Version { get; }
    public IReadOnlyList<CatalogEntry> Entries { get; }
    public IReadOnlyList<JsonObject> SelectorModels { get; }

    public static ModelCatalog Load(string path)
    {
        using var stream = File.OpenRead(path);
        var root = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false }) as JsonObject
            ?? throw new InvalidDataException($"{path} is not a JSON object.");
        var models = Py.Get(root, "models") as JsonArray
            ?? throw new InvalidDataException($"{path} has no models array.");
        var entries = models.OfType<JsonObject>()
            .Select(raw => new CatalogEntry(raw, Normalize(raw)))
            .Where(e => e.Id.Length > 0)
            .ToList();
        if (entries.Count == 0) throw new InvalidDataException($"{path} lists no usable models.");
        return new ModelCatalog(Py.IntOr(Py.Get(root, "version")), entries);
    }

    /// <summary>Prefix of ids for GGUF files found in the models folder that the catalog does not list.</summary>
    public const string LocalPrefix = "local:";

    /// <summary>
    /// An entry for a GGUF the user placed in the models folder. Without
    /// reviewed architecture metadata it plans with the legacy estimate and a
    /// conservative 8K default context, which the user can raise.
    /// </summary>
    public static CatalogEntry LocalEntry(string path, long sizeBytes)
    {
        var file = Path.GetFileName(path);
        var raw = new JsonObject
        {
            ["id"] = LocalPrefix + file,
            ["name"] = Path.GetFileNameWithoutExtension(file),
            ["family"] = "",
            ["gguf_file"] = file,
            ["size_bytes"] = sizeBytes,
            ["size_mb"] = Math.Round(sizeBytes / ModelMemory.Mib),
            ["context_length"] = 8192,
            ["specialty"] = "Your model",
            ["description"] = $"A GGUF file you added to the models folder ({file}). ODS cannot verify it against the catalog.",
            ["source"] = "local",
            ["install_recommendation"] = false,
        };
        return new CatalogEntry(raw, Normalize(raw));
    }

    public CatalogEntry? Find(string key)
    {
        var normalized = Py.NormalizeKey(key);
        return Entries.FirstOrDefault(e =>
            Py.NormalizeKey(e.Id) == normalized
            || Py.NormalizeKey(e.LlmModelName) == normalized
            || Py.NormalizeKey(e.PrimaryFile) == normalized);
    }

    /// <summary>scripts/select-model.py normalize_model.</summary>
    internal static JsonObject? Normalize(JsonObject raw)
    {
        var ggufParts = Py.Get(raw, "gguf_parts") as JsonArray;
        var gguf = Py.Or(Py.Get(raw, "gguf"), Py.Get(raw, "gguf_file"));
        if (!Py.Truthy(gguf) && ggufParts is { Count: > 0 } && ggufParts[0] is JsonObject firstPart)
        {
            gguf = Py.Get(firstPart, "file");
        }
        var modelId = Py.Or(Py.Or(Py.Or(Py.Get(raw, "id"), Py.Get(raw, "llm_model_name")), Py.Get(raw, "name")), gguf);
        if (!Py.Truthy(modelId) || !Py.Truthy(gguf)) return null;

        var id = Py.Str(modelId);
        var compatibility = Py.Get(raw, "app_compatibility") as JsonObject;
        var agentViability = compatibility is null ? null : Py.Get(compatibility, "agent_viability") as JsonObject;
        var pixelAgent = compatibility is null ? null : Py.Get(compatibility, "pixel_agent") as JsonObject;

        var model = new JsonObject();
        foreach (var key in ModelMemory.MemoryMetadataKeys)
        {
            if (raw.TryGetPropertyValue(key, out var value)) model[key] = value?.DeepClone();
        }
        model["id"] = id;
        model["name"] = Py.Truthy(Py.Get(raw, "name")) ? Py.Get(raw, "name")!.DeepClone() : id;
        model["family"] = Py.Truthy(Py.Get(raw, "family")) ? Py.Get(raw, "family")!.DeepClone() : "";
        model["llm_model_name"] = Py.Truthy(Py.Get(raw, "llm_model_name")) ? Py.Get(raw, "llm_model_name")!.DeepClone() : id;
        model["gguf_file"] = Py.Str(gguf);
        model["gguf_url"] = Py.Truthy(Py.Get(raw, "gguf_url")) ? Py.Get(raw, "gguf_url")!.DeepClone() : "";
        model["gguf_sha256"] = Py.Truthy(Py.Get(raw, "gguf_sha256")) ? Py.Get(raw, "gguf_sha256")!.DeepClone() : "";
        model["gguf_parts"] = ggufParts is null ? new JsonArray() : ggufParts.DeepClone();
        model["size_mb"] = Py.Truthy(Py.Get(raw, "size_mb")) ? Py.ToFloat(Py.Get(raw, "size_mb")) ?? 0.0 : 0.0;
        model["vram_required_gb"] = Py.Truthy(Py.Get(raw, "vram_required_gb")) ? Py.ToFloat(Py.Get(raw, "vram_required_gb")) ?? 0.0 : 0.0;
        model["context_length"] = Py.IntOr(Py.Get(raw, "context_length"));
        model["quantization"] = Py.Truthy(Py.Get(raw, "quantization")) ? Py.Get(raw, "quantization")!.DeepClone() : "";
        model["specialty"] = Py.Truthy(Py.Get(raw, "specialty")) ? Py.Get(raw, "specialty")!.DeepClone() : "General";
        model["install_recommendation"] = Py.ValueEnabled(raw.ContainsKey("install_recommendation")
            ? Py.Get(raw, "install_recommendation")
            : JsonValue.Create(true));
        model["selection"] = Py.Get(raw, "selection") is JsonObject selection ? selection.DeepClone() : new JsonObject();
        model["agent_viability_status"] = agentViability is null ? "" : Py.NormalizeKey(Py.Get(agentViability, "status"));
        model["pixel_agent_status"] = pixelAgent is null ? "" : Py.NormalizeKey(Py.Get(pixelAgent, "status"));
        model["runtime_profiles"] = Py.Get(raw, "runtime_profiles") is JsonArray profiles ? profiles.DeepClone() : new JsonArray();
        return model;
    }
}
