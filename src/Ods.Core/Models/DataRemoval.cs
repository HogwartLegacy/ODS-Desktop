using System.Text.Json;

namespace Ods.Core.Models;

/// <summary>
/// Explicit, user-requested data removal. In a models folder the user chose,
/// only files ODS downloaded (recorded in its index) and its partial
/// downloads are deleted; other files there are never touched.
/// </summary>
public static class DataRemoval
{
    public static void RemoveOdsModels(string directory)
    {
        var index = Path.Combine(directory, ".ods-model-index.json");
        if (File.Exists(index))
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(index)) ?? [];
            foreach (var file in entries.Keys.Select(Path.GetFileName).OfType<string>())
            {
                var path = Path.Combine(directory, file);
                if (File.Exists(path)) File.Delete(path);
            }
            File.Delete(index);
        }
        foreach (var partial in Directory.EnumerateFiles(directory, "*.gguf.part")) File.Delete(partial);
    }

    public static long DirectorySize(string directory) =>
        Directory.Exists(directory)
            ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;
}
