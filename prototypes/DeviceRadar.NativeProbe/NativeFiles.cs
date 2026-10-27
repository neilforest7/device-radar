using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeviceRadar.NativeProbe;

// Locks namespace components before using paths. Attribute checks always inspect the opened object itself.
internal static class NativeFiles
{
    private const uint Read = 0x80000000;
    private const uint Write = 0x40000000;
    private const uint ShareRead = 1;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    public static DirectoryGuard LockDirectories(string path, bool create)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new ProbeException("invalid-data-path");
        var guard = new DirectoryGuard();
        try
        {
            var current = root;
            guard.Add(OpenDirectory(current));
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (create)
                {
                    // The already-open parent cannot be renamed or opened for writing/deletion.
                    // If another actor creates this child first, OpenDirectory checks its own handle.
                    Directory.CreateDirectory(current);
                }
                guard.Add(OpenDirectory(current));
            }
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = Open(path, 0x81 /* FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES */, ShareRead, 3,
            OpenReparsePoint | BackupSemantics, "directory-lock-failed");
        try { CheckAttributes(handle, directory: true); return handle; }
        catch { handle.Dispose(); throw; }
    }

    public static FileStream OpenRead(string path) => Stream(path, Read, ShareRead, 3, FileAccess.Read);
    public static FileStream OpenLock(string path) => Stream(path, Read | Write, 0, 4, FileAccess.ReadWrite);
    public static FileStream CreateNew(string path) => Stream(path, Read | Write, 0, 1,
        FileAccess.ReadWrite, 0x80000000 /* FILE_FLAG_WRITE_THROUGH */);

    private static FileStream Stream(string path, uint access, uint share, uint disposition, FileAccess fileAccess, uint flags = 0)
    {
        var handle = Open(path, access, share, disposition, flags | OpenReparsePoint, "file-open-failed");
        try
        {
            CheckAttributes(handle, directory: false);
            return new FileStream(handle, fileAccess);
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags, string errorCode)
    {
        var full = Path.GetFullPath(path);
        var nativePath = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        var handle = CreateFileW(nativePath, access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new ProbeException(error == 32 ? "operation-busy" : errorCode, error);
        }
        return handle;
    }

    private static void CheckAttributes(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, 8))
            throw new ProbeException("file-attributes-failed", Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400 /* FILE_ATTRIBUTE_REPARSE_POINT */) != 0)
            throw new ProbeException("reparse-point-refused");
        if (((info.Attributes & 0x10 /* FILE_ATTRIBUTE_DIRECTORY */) != 0) != directory)
            throw new ProbeException("unexpected-file-type");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes; public uint ReparseTag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out AttributeTagInfo information, uint bufferSize);

}

internal sealed class DirectoryGuard : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    public void Add(SafeFileHandle handle) => handles.Add(handle);
    public void Dispose()
    {
        for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
        handles.Clear();
    }
}

internal sealed class MutationLock(DirectoryGuard directories, FileStream file) : IDisposable
{
    public void Dispose() { file.Dispose(); directories.Dispose(); }
}
