namespace Ods.Core;

/// <summary>
/// Where the desktop app keeps its data. Everything ODS writes lives under one
/// per-user root (default %LOCALAPPDATA%\ODS\Desktop, override with
/// ODS_DESKTOP_HOME) so updates never touch it and removal is predictable.
/// The application binaries are installed separately by the installer.
/// </summary>
public sealed class OdsPaths
{
    public OdsPaths(string root, string appDirectory)
    {
        Root = Path.GetFullPath(root);
        AppDirectory = appDirectory;
    }

    public static OdsPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable("ODS_DESKTOP_HOME");
        var root = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ODS", "Desktop")
            : overridden;
        return new OdsPaths(root, AppContext.BaseDirectory);
    }

    public string Root { get; }

    /// <summary>The installed application folder (read-only data such as the model catalog).</summary>
    public string AppDirectory { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string Runtimes => Path.Combine(Root, "runtimes");

    public string DefaultModels => Path.Combine(Root, "models");

    public string Logs => Path.Combine(Root, "logs");

    public string Conversations => Path.Combine(Root, "conversations");

    public string Downloads => Path.Combine(Root, "downloads");

    public string Secrets => Path.Combine(Root, "secrets");

    public string State => Path.Combine(Root, "state");

    public string ApiKeyFile => Path.Combine(Secrets, "llama-api-key");

    public string ServerStateFile => Path.Combine(State, "llama-server.json");

    /// <summary>Written by the in-app uninstall flow; the uninstall hook removes data only when it exists.</summary>
    public string RemoveDataMarker => Path.Combine(Root, "remove-data-on-uninstall");

    public string CatalogFile => Path.Combine(AppDirectory, "data", "model-library.json");

    public string PinsFile => Path.Combine(AppDirectory, "data", "runtime-pins.json");

    public string ExtensionsCatalogFile => Path.Combine(AppDirectory, "data", "extensions-catalog.json");

    public string NativeServicesFile => Path.Combine(AppDirectory, "data", "windows-native-services.json");

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Root, Runtimes, Logs, Conversations, Downloads, Secrets, State })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
