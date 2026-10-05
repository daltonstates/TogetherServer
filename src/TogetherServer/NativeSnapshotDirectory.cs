using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

// One reviewed basename resolved under a borrowed, already verified directory
// handle. This helper never accepts a root/path and never follows a reparse point.
// The caller owns directory provenance, native attributes/identity, guard length,
// and the lifetime of returned handles; no real-game acceptance is implied.
internal static class NativeSnapshotDirectory
{
    internal const string GuardName = ".snapshot-namespace-guard";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ReadData = 0x00000001;
    private const uint WriteData = 0x00000002;
    private const uint Traverse = 0x00000020;
    private const uint ReadAttributes = 0x00000080;
    private const uint WriteAttributes = 0x00000100;
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint DirectoryFile = 0x00000001;
    private const uint NonDirectoryFile = 0x00000040;
    private const uint SynchronousIoNonAlert = 0x00000020;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint CaseInsensitive = 0x00000040;
    private const uint DontReparse = 0x00001000;

    internal static SafeFileHandle CreateDirectory(SafeFileHandle parent, string name,
        bool namespaceWrites = false) => Open(parent, name, GenericRead | Traverse | Synchronize,
            ShareRead | (namespaceWrites ? ShareWrite : 0), FileOpenIf, DirectoryFile);

    internal static SafeFileHandle OpenDirectory(SafeFileHandle parent, string name,
        bool namespaceWrites = false) => Open(parent, name, GenericRead | Traverse | Synchronize,
            ShareRead | (namespaceWrites ? ShareWrite : 0), FileOpen, DirectoryFile);

    internal static SafeFileHandle CreateNewFile(SafeFileHandle parent, string name) =>
        Open(parent, name, WriteData | ReadAttributes | WriteAttributes | Synchronize, ShareRead, FileCreate, NonDirectoryFile);

    internal static SafeFileHandle OpenReadFile(SafeFileHandle parent, string name) =>
        Open(parent, name, ReadData | ReadAttributes | Synchronize, ShareRead, FileOpen, NonDirectoryFile);

    internal static SafeFileHandle OpenGuard(SafeFileHandle parent, string name)
    {
        if (!string.Equals(name, GuardName, StringComparison.Ordinal))
            throw new InvalidDataException("Only the fixed snapshot namespace guard may be opened.");
        // FILE_OPEN_IF preserves an existing guard: the caller must reject a
        // nonzero/linked guard rather than truncating or repairing it silently.
        return Open(parent, name, GenericRead | GenericWrite | Synchronize, ShareRead, FileOpenIf, NonDirectoryFile);
    }

    internal static void DeleteFile(SafeFileHandle parent, string name) => DeleteChild(parent, name, directory: false);

    internal static void DeleteDirectory(SafeFileHandle parent, string name) => DeleteChild(parent, name, directory: true);

    private static void DeleteChild(SafeFileHandle parent, string name, bool directory)
    {
        using var child = Open(parent, name, Delete | ReadAttributes | WriteAttributes | Synchronize,
            7 /* share read/write/delete; caller closed only this child's lease */, FileOpen,
            directory ? DirectoryFile : NonDirectoryFile);
        if (!GetFileInformationByHandle(child, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The opened private snapshot child could not be verified.");
        var attributes = (FileAttributes)info.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != directory ||
            !directory && info.NumberOfLinks != 1)
            throw new InvalidDataException("A linked or unexpected child cannot authorize private snapshot cleanup.");
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(child, attributes & ~FileAttributes.ReadOnly);
        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(child, 4 /* FileDispositionInfo */, ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The exact private snapshot child could not be deleted.");
        // Closing this exact child completes deletion. Nonempty directories fail;
        // no recursive or by-path operation is performed here.
    }

    private static SafeFileHandle Open(SafeFileHandle parent, string name, uint access,
        uint sharing, uint disposition, uint kind)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Snapshot directory handles require Windows.");
        ArgumentNullException.ThrowIfNull(parent);
        RequireBasename(name);
        if (parent.IsClosed || parent.IsInvalid) throw new InvalidDataException("A leased snapshot parent handle is required.");
        var referenced = false;
        var text = IntPtr.Zero;
        var unicodePointer = IntPtr.Zero;
        SafeFileHandle? result = null;
        var returned = false;
        try
        {
            parent.DangerousAddRef(ref referenced);
            text = Marshal.StringToHGlobalUni(name);
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = text
            };
            unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = CaseInsensitive | DontReparse
            };
            var status = NtCreateFile(out var raw, access, ref attributes, out _, IntPtr.Zero,
                0x80 /* FILE_ATTRIBUTE_NORMAL */, sharing, disposition,
                kind | SynchronousIoNonAlert | OpenReparsePoint, IntPtr.Zero, 0);
            result = new SafeFileHandle(raw, ownsHandle: true);
            if (status != 0)
                throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)),
                    "The parent-relative snapshot open did not complete safely.");
            if (result.IsInvalid) throw new IOException("The parent-relative snapshot open returned an invalid handle.");
            if (!GetFileInformationByHandle(result, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The parent-relative snapshot identity could not be read.");
            if (((FileAttributes)info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (((FileAttributes)info.Attributes & FileAttributes.Directory) != 0) != (kind == DirectoryFile) ||
                kind == NonDirectoryFile && info.NumberOfLinks != 1)
                throw new InvalidDataException("A parent-relative snapshot child is linked or has the wrong type.");
            returned = true;
            return result;
        }
        finally
        {
            if (!returned) result?.Dispose();
            if (unicodePointer != IntPtr.Zero) Marshal.FreeHGlobal(unicodePointer);
            if (text != IntPtr.Zero) Marshal.FreeHGlobal(text);
            if (referenced) parent.DangerousRelease();
        }
    }

    private static void RequireBasename(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(character => char.IsControl(character) || character is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
            throw new InvalidDataException("A snapshot handle open requires one safe reviewed basename.");
        var stem = name.Split('.', 2)[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
            (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
            throw new InvalidDataException("A snapshot basename cannot identify a Windows device.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal IntPtr RootDirectory;
        internal IntPtr ObjectName;
        internal uint Attributes;
        internal IntPtr SecurityDescriptor;
        internal IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal IntPtr Status;
        internal UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)] internal bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerial;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out IntPtr handle, uint desiredAccess,
        ref ObjectAttributes attributes, out IoStatusBlock status, IntPtr allocationSize,
        uint fileAttributes, uint sharing, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, uint informationClass,
        ref FileDispositionInfo information, uint size);
}
