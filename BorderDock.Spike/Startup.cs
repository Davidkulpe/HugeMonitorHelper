using Microsoft.Win32;

namespace BorderDock.Spike;

/// <summary>
/// Run-at-login via the per-user Run key. Non-elevated (HKCU), so no UAC —
/// matches the eng-review "run as plain user" decision.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BorderDock";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

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
