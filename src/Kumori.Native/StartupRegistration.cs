using Microsoft.Win32;
using Kumori.Core;

namespace Kumori.Native;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kumori";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return !string.IsNullOrWhiteSpace(key?.GetValue(ValueName) as string);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            executable = Path.Combine(AppContext.BaseDirectory, "Kumori.exe");
        }

        key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
    }
}
