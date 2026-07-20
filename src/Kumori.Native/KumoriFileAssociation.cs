using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kumori.Native;

public static class KumoriFileAssociation
{
    public const string Extension = ".kumori";
    public const string ProgId = "Kumori.SharedPlay.1";
    private const string ApplicationName = "Kumori";
    private const string ClassesRoot = @"Software\Classes";
    private const string CapabilitiesPath = @"Software\Kumori\Capabilities";
    private const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
    private const string UserChoicePath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.kumori\UserChoice";

    public static void Register(string? executablePath = null)
    {
        string executable = ResolveExecutablePath(executablePath);
        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(ClassesRoot, writable: true)
            ?? throw new InvalidOperationException("Could not open the per-user Windows file-type registry.");
        using (RegistryKey progId = classes.CreateSubKey(ProgId, writable: true)
                                    ?? throw new InvalidOperationException("Could not register the Kumori shared-play type."))
        {
            progId.SetValue(null, "Kumori shared play", RegistryValueKind.String);
            progId.SetValue("FriendlyTypeName", "Kumori shared play", RegistryValueKind.String);
            using RegistryKey icon = progId.CreateSubKey("DefaultIcon", writable: true)!;
            icon.SetValue(null, $"\"{executable}\",0", RegistryValueKind.String);
            using RegistryKey command = progId.CreateSubKey(@"shell\open\command", writable: true)!;
            command.SetValue(null, BuildOpenCommand(executable), RegistryValueKind.String);
        }
        using (RegistryKey extension = classes.CreateSubKey(Extension, writable: true)!)
        {
            string? currentDefault = extension.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(currentDefault)
                || string.Equals(currentDefault, ProgId, StringComparison.OrdinalIgnoreCase))
            {
                extension.SetValue(null, ProgId, RegistryValueKind.String);
            }
            using RegistryKey openWith = extension.CreateSubKey("OpenWithProgids", writable: true)!;
            openWith.SetValue(ProgId, "", RegistryValueKind.String);
        }
        using (RegistryKey application = classes.CreateSubKey(@"Applications\Kumori.exe", writable: true)!)
        {
            application.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
            using RegistryKey command = application.CreateSubKey(@"shell\open\command", writable: true)!;
            command.SetValue(null, BuildOpenCommand(executable), RegistryValueKind.String);
            using RegistryKey supported = application.CreateSubKey("SupportedTypes", writable: true)!;
            supported.SetValue(Extension, "", RegistryValueKind.String);
        }
        using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath, writable: true)!)
        {
            capabilities.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
            capabilities.SetValue(
                "ApplicationDescription",
                "View portable plays and replays shared by other Kumori users.",
                RegistryValueKind.String);
            using RegistryKey associations = capabilities.CreateSubKey("FileAssociations", writable: true)!;
            associations.SetValue(Extension, ProgId, RegistryValueKind.String);
        }
        using (RegistryKey registered = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsPath, writable: true)!)
        {
            registered.SetValue(ApplicationName, CapabilitiesPath, RegistryValueKind.String);
        }
        NotifyShell();
    }

    public static bool IsRegistered(string? executablePath = null)
    {
        string expected = BuildOpenCommand(ResolveExecutablePath(executablePath));
        using RegistryKey? command = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesRoot}\{ProgId}\shell\open\command",
            writable: false);
        return string.Equals(command?.GetValue(null) as string, expected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCurrentDefault()
    {
        using RegistryKey? userChoice = Registry.CurrentUser.OpenSubKey(UserChoicePath, writable: false);
        if (userChoice?.GetValue("ProgId") is string explicitChoice)
            return string.Equals(explicitChoice, ProgId, StringComparison.OrdinalIgnoreCase);
        using RegistryKey? extension = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{Extension}", writable: false);
        return string.Equals(extension?.GetValue(null) as string, ProgId, StringComparison.OrdinalIgnoreCase);
    }

    public static void Remove()
    {
        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(ClassesRoot, writable: true)
            ?? throw new InvalidOperationException("Could not open the per-user Windows file-type registry.");
        using (RegistryKey? extension = classes.OpenSubKey(Extension, writable: true))
        {
            if (string.Equals(extension?.GetValue(null) as string, ProgId, StringComparison.OrdinalIgnoreCase))
                extension?.DeleteValue(string.Empty, throwOnMissingValue: false);
            using RegistryKey? openWith = extension?.OpenSubKey("OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        classes.DeleteSubKeyTree(@"Applications\Kumori.exe", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);
        using (RegistryKey? registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, writable: true))
            registered?.DeleteValue(ApplicationName, throwOnMissingValue: false);
        NotifyShell();
    }

    public static void OpenWindowsDefaultApps()
    {
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps")
        {
            UseShellExecute = true,
        });
    }

    internal static string BuildOpenCommand(string executable) =>
        $"\"{executable}\" --import \"%1\"";

    private static string ResolveExecutablePath(string? executablePath)
    {
        string path = string.IsNullOrWhiteSpace(executablePath)
            ? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Kumori.exe")
            : executablePath.Trim();
        return Path.GetFullPath(path);
    }

    private static void NotifyShell() =>
        SHChangeNotify(ShellChangeNotifyEventId.AssocChanged, ShellChangeNotifyFlags.IdList | ShellChangeNotifyFlags.Flush, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        ShellChangeNotifyEventId eventId,
        ShellChangeNotifyFlags flags,
        IntPtr item1,
        IntPtr item2);

    private enum ShellChangeNotifyEventId : uint
    {
        AssocChanged = 0x08000000,
    }

    [Flags]
    private enum ShellChangeNotifyFlags : uint
    {
        IdList = 0x0000,
        Flush = 0x1000,
    }
}
