namespace Ods.Core.Platform;

/// <summary>
/// Other ODS editions on this account. Running two of them at once loads two
/// models and competes for memory, the GPU and port 8080, so the app says so
/// up front. Nothing is changed or migrated automatically, as with ODS's own
/// installers. The markers are the paths those installers create.
/// </summary>
public static class ExistingInstallations
{
    public static IReadOnlyList<string> Detect()
    {
        var notes = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // install.ps1 (WSL2 + Docker Desktop) keeps its Windows lifecycle controller here.
        if (Directory.Exists(Path.Combine(localAppData, "ODS", "wsl")))
        {
            notes.Add("ODS is also installed in Ubuntu on WSL2 for this account. If it is running, stop it before using this app "
                + "(wsl-lifecycle.ps1 -Action stop, described in the Windows Quickstart) so the two do not compete for memory and the GPU.");
        }

        // The WSL setup's AMD path runs llama-server.exe on Windows through the ODSLlamaServerRuntime task.
        if (Directory.Exists(Path.Combine(localAppData, "ODS", "lemonade", "portal-runtime")))
        {
            notes.Add("The WSL edition's Windows model server task (ODSLlamaServerRuntime) is set up on this PC. "
                + "If it is running, unload its model in Portal > Models first; otherwise both load a model.");
        }

        // The legacy Docker-based native installer (installers/windows/install-windows.ps1).
        var legacy = Environment.GetEnvironmentVariable("ODS_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ods");
        if (File.Exists(Path.Combine(legacy, ".env")) && File.Exists(Path.Combine(legacy, "ods.ps1")))
        {
            notes.Add($"A Docker-based ODS installation is in {legacy}. Stop it with its own .\\ods.ps1 stop before using this app.");
        }
        return notes;
    }
}
