using System.Runtime.InteropServices;
namespace Kumori.ReplayViewer;

/// <summary>
/// Uses the Windows common file dialog directly. Some SDL hosts return null
/// from GameHost.CreateSystemFileSelector(), despite the non-null API shape.
/// </summary>
internal static class WindowsReplayFilePicker
{
    private const int max_path_length = 32768;
    private const int ofn_pathmustexist = 0x00000800;
    private const int ofn_filemustexist = 0x00001000;
    private const int ofn_explorer = 0x00080000;
    private const int ofn_nochangedir = 0x00000008;

    internal static int NativeDialogStructureSize => Marshal.SizeOf<OpenFileName>();

    public static string? SelectOsr()
    {
        IntPtr path = Marshal.AllocHGlobal(max_path_length * sizeof(char));
        IntPtr filter = Marshal.StringToHGlobalUni("osu! replay (*.osr)\0*.osr\0All files (*.*)\0*.*\0\0");
        IntPtr title = Marshal.StringToHGlobalUni("Select a replay to compare");
        IntPtr extension = Marshal.StringToHGlobalUni("osr");

        try
        {
            // OPENFILENAME expects the first character of the caller-owned
            // file buffer to be null when no initial path is supplied.
            Marshal.WriteInt16(path, 0);
            var dialog = new OpenFileName
            {
                StructureSize = NativeDialogStructureSize,
                Filter = filter,
                File = path,
                MaxFile = max_path_length,
                Title = title,
                DefaultExtension = extension,
                FilterIndex = 1,
                Flags = ofn_explorer | ofn_filemustexist | ofn_pathmustexist | ofn_nochangedir,
            };

            if (GetOpenFileNameW(ref dialog))
                return Marshal.PtrToStringUni(path);

            // Extended error 0 means that the user cancelled the dialog.
            int error = CommDlgExtendedError();
            if (error == 0)
                return null;

            throw new InvalidOperationException($"Windows could not open the replay picker (0x{error:X4}).");
        }
        finally
        {
            Marshal.FreeHGlobal(extension);
            Marshal.FreeHGlobal(title);
            Marshal.FreeHGlobal(filter);
            Marshal.FreeHGlobal(path);
        }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileName
    {
        public int StructureSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public IntPtr Filter;
        public IntPtr CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public IntPtr InitialDirectory;
        public IntPtr Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public IntPtr DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr Reserved;
        public int ReservedSize;
        public int ExtendedFlags;
    }
}
