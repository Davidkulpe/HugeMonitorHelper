using Microsoft.Win32;

namespace WAuraDows;

/// <summary>
/// Run-at-login via the per-user Run key. Non-elevated (HKCU), so no UAC —
/// matches the eng-review "run as plain user" decision.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WAuraDows";
    private const string LegacyValueName = "BorderDock";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>Take over a pre-rename autostart entry. The old value points at
    /// BorderDock.Spike.exe, which no longer exists after the rename — leaving it
    /// would mean a dead entry firing at every login. Preserve the user's intent
    /// (they had it on) and re-point it at this executable under the new name.</summary>
    public static void MigrateLegacyEntry()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(LegacyValueName) is not string) return;
            k.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            k.SetValue(ValueName, $"\"{ExePath}\"");
        }
        catch { /* best-effort */ }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k!.SetValue(ValueName, $"\"{ExePath}\"");
            else k!.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }
}
