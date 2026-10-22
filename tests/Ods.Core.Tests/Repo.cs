using System.Reflection;
using System.Text.Json.Nodes;
using Ods.Core.Catalog;

namespace Ods.Core.Tests;

/// <summary>Paths into the ODS checkout this test assembly was built from.</summary>
internal static class Repo
{
    private static string Metadata(string key) =>
        typeof(Repo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value
        ?? throw new InvalidOperationException($"Missing assembly metadata {key}.");

    /// <summary>The ods/ directory (the product root inside the repository).</summary>
    public static string OdsRoot => Metadata("OdsRoot");

    /// <summary>ods/desktop/windows.</summary>
    public static string DesktopRoot => Metadata("DesktopRoot");

    public static string OdsVersion => Metadata("OdsVersion");

    public static string Path(params string[] parts) => System.IO.Path.Combine([OdsRoot, .. parts]);

    public static JsonObject Json(params string[] parts) =>
        JsonNode.Parse(File.ReadAllText(Path(parts)))!.AsObject();

    private static readonly Lazy<ModelCatalog> LazyCatalog = new(() => ModelCatalog.Load(Path("config", "model-library.json")));

    public static ModelCatalog Catalog => LazyCatalog.Value;
}
