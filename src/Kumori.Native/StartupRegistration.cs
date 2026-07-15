using Microsoft.Win32;
using Kumori.Core;

namespace Kumori.Native;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kumori";
    public const string StartMinimizedArgument = "--start-minimized";

    public static bool IsEnabled()
    {
        return !string.IsNullOrWhiteSpace(GetCommand());
    }

    public static string? GetCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public static string? GetExecutablePath() => ParseExecutablePath(GetCommand());

    public static bool IsConfigured(
        bool enabled,
        bool startMinimized,
        string? executablePath = null)
    {
        var command = GetCommand();
        if (!enabled)
        {
            return string.IsNullOrWhiteSpace(command);
        }

        return string.Equals(
            command,
            BuildCommand(ResolveExecutablePath(executablePath), startMinimized),
            StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(
        bool enabled,
        bool startMinimized = false,
        string? executablePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(
            ValueName,
            BuildCommand(ResolveExecutablePath(executablePath), startMinimized),
            RegistryValueKind.String);
    }

    internal static string BuildCommand(string executable, bool startMinimized) =>
        startMinimized
            ? $"\"{executable}\" {StartMinimizedArgument}"
            : $"\"{executable}\"";

    internal static string? ParseExecutablePath(string? command)
    {
        var value = command?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : null;
        }

        var executableEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (executableEnd >= 0)
        {
            return value[..(executableEnd + 4)].Trim();
        }

        var firstSpace = value.IndexOf(' ');
        return firstSpace > 0 ? value[..firstSpace] : value;
    }

    private static string CurrentExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(processPath)
            ? Path.Combine(AppContext.BaseDirectory, "Kumori.exe")
            : processPath;
    }

    private static string ResolveExecutablePath(string? executablePath) =>
        string.IsNullOrWhiteSpace(executablePath)
            ? CurrentExecutablePath()
            : executablePath.Trim();
}
