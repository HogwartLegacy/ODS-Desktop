using System.Text.Json;
using System.Text.Json.Serialization;
using Ods.Core.Diagnostics;

namespace Ods.Core.Settings;

/// <summary>The model ODS serves and the context it was planned at.</summary>
public sealed record ActiveModel(string Id, int ContextLength, string? RuntimeProfile);

public sealed class ServerSettings
{
    /// <summary>0 picks the first free of ODS's defaults (8080, 18080, 28080).</summary>
    public int Port { get; set; }

    /// <summary>--n-gpu-layers on GPU routes: all (default), auto, or a layer count.</summary>
    public string GpuLayers { get; set; } = "all";

    /// <summary>--reasoning: off (ODS default), on or auto.</summary>
    public string Reasoning { get; set; } = "off";

    /// <summary>Load the active model whenever ODS starts.</summary>
    public bool LoadOnStartup { get; set; } = true;

    public int StartupTimeoutSeconds { get; set; } = 900;
}

public sealed class UiSettings
{
    public string Theme { get; set; } = "dark";

    public bool CloseToTray { get; set; } = true;

    public bool TrayHintShown { get; set; }
}

public sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;

    /// <summary>Velopack feed: a GitHub repository URL or an HTTPS folder with releases.win.json.</summary>
    public string FeedUrl { get; set; } = "https://github.com/Osmantic/ODS";

    public bool Prerelease { get; set; } = true;
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public bool OnboardingCompleted { get; set; }

    /// <summary>Null means the default models folder under the data root.</summary>
    public string? ModelsDirectory { get; set; }

    /// <summary>The qualified llama.cpp flavor (e.g. win-cuda-12.4-x64) and device (CUDA0, Vulkan0 or none).</summary>
    public string? RuntimeFlavor { get; set; }

    public string? RuntimeDevice { get; set; }

    public ActiveModel? ActiveModel { get; set; }

    /// <summary>The full model a bootstrap install is still downloading.</summary>
    public ActiveModel? PendingModel { get; set; }

    public bool StartWithWindows { get; set; } = true;

    public ServerSettings Server { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();
}

/// <summary>Loads and atomically saves settings.json.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Set when the last load found a damaged file and set it aside; the UI tells the user.</summary>
    public string? RecoveredFrom { get; private set; }

    public AppSettings Load()
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (JsonException error)
        {
            var aside = path + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(path, aside);
            RecoveredFrom = aside;
            AppLog.Error($"settings.json was damaged and was moved to {aside}; ODS starts with default settings.", error);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, path, overwrite: true);
    }
}
