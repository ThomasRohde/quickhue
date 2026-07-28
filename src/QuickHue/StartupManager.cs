using Microsoft.Win32;

namespace QuickHue;

internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickHue";

    public static void SetEnabled(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var path = executablePath ?? Environment.ProcessPath
                ?? throw new InvalidOperationException("The QuickHue executable path is unavailable.");
            key.SetValue(ValueName, QuoteExecutable(path), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static string QuoteExecutable(string path) => $"\"{path}\" --startup";
}
