using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kumori.Core;

/// <summary>Measures disk space owned by a cache without counting linked source files.</summary>
public static class CacheStorageUsage
{
    public static long GetAdditionalBytes(string path, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path)) return 0;

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
                if (OperatingSystem.IsWindows() && HasAnotherHardLink(file)) continue;
                total += new FileInfo(file).Length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    private static bool HasAnotherHardLink(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
        return GetFileInformationByHandle(handle, out var info) && info.NumberOfLinks > 1;
    }

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
