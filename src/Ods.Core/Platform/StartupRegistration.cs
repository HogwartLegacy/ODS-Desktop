using Microsoft.Win32;

namespace Ods.Core.Platform;

public enum StartupState
{
    Disabled,
    Enabled,

    /// <summary>Registered, but switched off by the user in Task Manager > Startup apps.</summary>
    DisabledInTaskManager,
}

/// <summary>
/// "Start ODS when I sign in": a per-user Run entry (no administrator rights),
/// launching ODS minimized to the tray. Task Manager's Startup apps switch is
/// respected and reported, and cleared only when the user turns the setting
/// on again in ODS.
/// </summary>
public static class StartupRegistration
{
    public const string ValueName = "ODS";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static StartupState State()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string) return StartupState.Disabled;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        // Task Manager writes 02 (enabled) or 03 (disabled) as the first byte.
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1
            ? StartupState.DisabledInTaskManager
            : StartupState.Enabled;
    }

    public static void Enable(string executablePath)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            run.SetValue(ValueName, $"\"{executablePath}\" --background", RegistryValueKind.String);
        }
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Re-point an enabled entry at the current executable (the install folder can move on update).</summary>
    public static void Refresh(string executablePath)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(ValueName) is string current && !current.Contains(executablePath, StringComparison.OrdinalIgnoreCase))
        {
            run.SetValue(ValueName, $"\"{executablePath}\" --background", RegistryValueKind.String);
        }
    }
}
