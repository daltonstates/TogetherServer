using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

// Leases only the fixed completion marker beneath LocalData's nonempty anchor.
// No caller-selected path, live save, payload mutation, or recursive cleanup.
internal sealed class BackupBookmarkCompletion : IDisposable
{
    private const long MaximumCompletionBytes = 16L * 1024 * 1024;
    private readonly List<SafeFileHandle> directories = [];
    internal FileStream Stream { get; private set; } = null!;

    internal static BackupBookmarkCompletion Open(LocalData data, Guid profileId, Guid backupId)
    {
        var lease = new BackupBookmarkCompletion();
        try
        {
            var root = CreateFile(data.RootPath, 0x80000000, 3 /* read/write, no delete */,
                IntPtr.Zero, 3, 0x02200000 /* directory + open reparse point */, IntPtr.Zero);
            lease.directories.Add(root);
            if (root.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetFileInformationByHandle(root, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (((FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
                FileAttributes.Directory)
                throw new InvalidDataException("The local-data anchor is not a plain directory.");
            var backups = NativeSnapshotDirectory.OpenDirectory(root, "backups");
            lease.directories.Add(backups);
            var profile = NativeSnapshotDirectory.OpenDirectory(backups, profileId.ToString("N"));
            lease.directories.Add(profile);
            var completed = NativeSnapshotDirectory.OpenDirectory(profile, backupId.ToString("N") + ".backup");
            lease.directories.Add(completed);
            // A marker beside an interrupted/missing payload is not a completed
            // backup. Integrity verification remains its separate full hash check.
            lease.directories.Add(NativeSnapshotDirectory.OpenDirectory(completed, "payload"));
            lease.Stream = new FileStream(NativeSnapshotDirectory.OpenReadFile(completed, "complete.json"), FileAccess.Read);
            if (lease.Stream.Length is <= 0 or > MaximumCompletionBytes)
                throw new InvalidDataException("The completion marker is empty or exceeds its reviewed bound.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        Stream?.Dispose();
        for (var index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
        directories.Clear();
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

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
