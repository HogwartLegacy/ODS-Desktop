using System.Diagnostics;
using System.IO;
using Ods.Core;
using Ods.Core.Models;
using Ods.Core.Platform;
using Ods.Core.Settings;

namespace Ods.Desktop.Services;

/// <summary>
/// Runs when Windows uninstalls ODS (Velopack's fast callback, before files
/// are removed, with a strict time limit and no UI). It always stops ODS's
/// model servers and removes the sign-in entry. User data (models, chats,
/// settings) is kept unless the user chose "Remove ODS and its data" in the
/// app, which leaves a marker; the same rule ODS's WSL uninstaller follows.
/// </summary>
public static class UninstallHooks
{
    public static void BeforeUninstall()
    {
        StartupRegistration.Disable();
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            using (process)
            {
                if (IsOdsRuntime(process)) process.Kill(entireProcessTree: true);
            }
        }

        var paths = OdsPaths.Default();
        if (!File.Exists(paths.RemoveDataMarker)) return;
        var settings = new SettingsStore(paths.SettingsFile).Load();
        if (settings.ModelsDirectory is string custom && Directory.Exists(custom)) DataRemoval.RemoveOdsModels(custom);
        Directory.Delete(paths.Root, recursive: true);
    }

    private static bool IsOdsRuntime(Process process)
    {
        var runtimes = OdsPaths.Default().Runtimes;
        try
        {
            return process.MainModule?.FileName.StartsWith(runtimes, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Another user's process or an access-denied handle is not ODS's.
            return false;
        }
    }
}
