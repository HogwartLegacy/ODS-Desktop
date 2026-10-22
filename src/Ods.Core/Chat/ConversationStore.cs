using System.Text.Json;
using System.Text.Json.Serialization;
using Ods.Core.Diagnostics;

namespace Ods.Core.Chat;

public sealed class StoredMessage
{
    public string Role { get; set; } = "user";

    public string Content { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string? ModelName { get; set; }

    public double? TokensPerSecond { get; set; }

    public int? Tokens { get; set; }
}

public sealed class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "New chat";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string? SystemPrompt { get; set; }

    public List<StoredMessage> Messages { get; set; } = [];
}

/// <summary>Chat history as one JSON file per conversation. It stays on this PC.</summary>
public sealed class ConversationStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyList<Conversation> LoadAll()
    {
        if (!Directory.Exists(directory)) return [];
        var conversations = new List<Conversation>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var conversation = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(file), Options);
                if (conversation is not null) conversations.Add(conversation);
            }
            catch (JsonException error)
            {
                var aside = file + ".damaged";
                File.Move(file, aside, overwrite: true);
                AppLog.Error($"Conversation file {file} was damaged and was moved to {aside}.", error);
            }
        }
        return conversations.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    public void Save(Conversation conversation)
    {
        Directory.CreateDirectory(directory);
        var path = PathFor(conversation.Id);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(conversation, Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string id)
    {
        if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Invalid conversation id.", nameof(id));
        return Path.Combine(directory, id + ".json");
    }

    /// <summary>A short title from the first user message.</summary>
    public static string TitleFrom(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "New chat";
        return line.Length <= 48 ? line : line[..47].TrimEnd() + "…";
    }
}
