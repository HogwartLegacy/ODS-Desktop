using System.Text.Json.Nodes;

namespace Ods.Core.Catalog;

/// <summary>One ODS service and how it runs on native Windows.</summary>
public sealed record ServiceInfo(string Id, string Name, string Description, string Category, string NativeStatus, string NativeApproach);

/// <summary>
/// ODS's service catalog (config/extensions-catalog.json) joined with the
/// native-Windows status table (ods/desktop/windows/windows-native-services.json).
/// </summary>
public sealed class ServiceCatalog
{
    private ServiceCatalog(List<ServiceInfo> core, List<ServiceInfo> library)
    {
        Core = core;
        Library = library;
    }

    /// <summary>The core stack, in the order the native-status table lists it.</summary>
    public IReadOnlyList<ServiceInfo> Core { get; }

    /// <summary>Everything else in the ODS extension library (container apps on Linux and macOS).</summary>
    public IReadOnlyList<ServiceInfo> Library { get; }

    public static ServiceCatalog Load(string extensionsCatalogPath, string nativeServicesPath)
    {
        var catalog = JsonNode.Parse(File.ReadAllText(extensionsCatalogPath))!["extensions"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(e => (string)e["id"]!, StringComparer.OrdinalIgnoreCase);
        var native = JsonNode.Parse(File.ReadAllText(nativeServicesPath))!["services"]!.AsArray().OfType<JsonObject>().ToList();

        ServiceInfo Describe(string id, string status, string approach)
        {
            catalog.TryGetValue(id, out var entry);
            return new ServiceInfo(
                id,
                (string?)entry?["name"] ?? id,
                (string?)entry?["description"] ?? "",
                (string?)entry?["category"] ?? "core",
                status,
                approach);
        }

        var core = native.Select(n => Describe((string)n["id"]!, (string)n["status"]!, (string)n["approach"]!)).ToList();
        var coreIds = core.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var library = catalog.Keys
            .Where(id => !coreIds.Contains(id))
            .Select(id => Describe(id, "container", "Runs as a container on Linux and macOS. A native Windows recipe (pinned binaries or an embedded runtime) is needed before it can run here."))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ServiceCatalog(core, library);
    }
}
