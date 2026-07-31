using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kumori.FarmFinder;

namespace Kumori.App.FarmFinder;

/// <summary>
/// Persists only a DPAPI-protected payload. Decryption is scoped to the
/// current Windows user and the secret is never written as plaintext.
/// </summary>
public sealed class WindowsCredentialsStore(string path) : IOsuCredentialsStore
{
    private static readonly byte[] entropy = Encoding.UTF8.GetBytes("Kumori.FarmFinder.v1");

    public async Task<OsuApiCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return null;
        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var clearBytes = Unprotect(protectedBytes);
        try
        {
            return JsonSerializer.Deserialize<OsuApiCredentials>(clearBytes);
        }
        finally
        {
            Array.Clear(clearBytes);
        }
    }

    public async Task SaveAsync(
        OsuApiCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        if (!credentials.IsConfigured)
            throw new ArgumentException("A positive Client ID and non-empty secret are required.", nameof(credentials));
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var protectedBytes = Protect(clearBytes);
            try
            {
                var temporary = path + ".tmp";
                await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken);
                File.Move(temporary, path, true);
            }
            finally
            {
                Array.Clear(protectedBytes);
            }
        }
        finally
        {
            Array.Clear(clearBytes);
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] clearBytes) =>
        invokeCrypt(clearBytes, entropy, protect: true);

    private static byte[] Unprotect(byte[] protectedBytes) =>
        invokeCrypt(protectedBytes, entropy, protect: false);

    private static byte[] invokeCrypt(byte[] input, byte[] optionalEntropy, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(optionalEntropy, GCHandleType.Pinned);
        try
        {
            var inputBlob = new DataBlob(input.Length, inputHandle.AddrOfPinnedObject());
            var entropyBlob = new DataBlob(optionalEntropy.Length, entropyHandle.AddrOfPinnedObject());
            bool succeeded = protect
                ? CryptProtectData(
                    ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var outputBlob)
                : CryptUnprotectData(
                    ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out outputBlob);
            if (!succeeded)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                return output;
            }
            finally
            {
                if (outputBlob.Data != IntPtr.Zero)
                    LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            entropyHandle.Free();
            inputHandle.Free();
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;
        public readonly IntPtr Data = data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
