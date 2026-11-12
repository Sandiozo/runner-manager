using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RunnerManager;

/// <summary>
/// Which file a running program was started from, told by the file itself rather than by its path or version. An update
/// or a rollback renames the running exe out of the way and puts another file at its path, so a copy that still runs
/// from the renamed file has the installed path in <see cref="Environment.ProcessPath"/>, and a build of the same version
/// elsewhere has the same version: neither of those tells whether it is the installed file. Windows keeps the name of a
/// running program's file up to date when the file is renamed (<see cref="CurrentPath"/>), and two paths name the same
/// file when they have the same volume and file id (<see cref="IdOf"/>).
/// </summary>
internal static class ProcessImage
{
    /// <summary>
    /// The full path that the file <paramref name="process"/> runs from has now, or null when it cannot be read. Unlike
    /// <see cref="Environment.ProcessPath"/> and <see cref="System.Diagnostics.Process.MainModule"/>, which keep the
    /// path the program was started by, it follows a rename of the file.
    /// </summary>
    internal static string? CurrentPath(SafeHandle process)
    {
        var size = 32768;
        var name = new StringBuilder(size);
        return QueryFullProcessImageNameW(process, 0, name, ref size) ? name.ToString(0, size) : null;
    }

    /// <summary>The volume and file id of the file at <paramref name="path"/> (a link is followed), or null when it cannot be read.</summary>
    internal static FileId? IdOf(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return null;
        using var file = CreateFileW(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (file.IsInvalid) return null;
        if (GetFileInformationByHandleEx(file, FileIdInfoClass, out var info, Marshal.SizeOf<FileIdInfo>()))
            return new FileId(info.VolumeSerialNumber, info.Low, info.High);
        // A file system without 128-bit ids (FAT): the 64-bit index.
        return GetFileInformationByHandle(file, out var basic)
            ? new FileId(basic.VolumeSerialNumber, ((ulong)basic.FileIndexHigh << 32) | basic.FileIndexLow, 0)
            : null;
    }

    /// <summary>Whether <paramref name="process"/> runs from the file that is at <paramref name="path"/> now. False when either cannot be read.</summary>
    internal static bool RunsFrom(SafeHandle process, string path)
        => IdOf(path) is { } file && CurrentPath(process) is { } image && IdOf(image) == file;

    /// <summary>Whether this process runs from the file that is at <paramref name="path"/> now (see <see cref="RunsFrom"/>).</summary>
    internal static bool ThisProcessRunsFrom(string path)
    {
        using var self = new SafeProcessHandle(GetCurrentProcess(), ownsHandle: false);
        return RunsFrom(self, path);
    }

    /// <summary>The identity of a file: the serial number of its volume and its id there.</summary>
    internal readonly record struct FileId(ulong Volume, ulong Low, ulong High);

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong Low;
        public ulong High;
    }

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeHandle process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
