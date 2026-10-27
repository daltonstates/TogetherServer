using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

// One explicit Start owns this proof. Full payload hashes are computed once,
// and the exact files and namespace remain leased until the launch boundary.
// It conveys no permission, setup, process, readiness or occupancy authority.
internal sealed class WorkingSaveStartProof : IDisposable
{
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    private readonly List<FileStream> files = [];
    private readonly List<SafeFileHandle> directories = [];
    private readonly Dictionary<string, SafeFileHandle> payloadDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly string dataRoot;
    private readonly string directory;
    private readonly SharedWorldVersion version;
    private Action? releaseReservation;
    private bool verified;
    internal string VersionHash => version.VersionHash;
    // Synthetic namespace-race injection only; relative names grant no access.
    internal Action<string>? BeforeDirectoryOpenForChecks { get; set; }

    private WorkingSaveStartProof(string dataRoot, string directory, SharedWorldVersion version, Action? releaseReservation)
    {
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.directory = Path.GetFullPath(directory);
        this.version = version;
        this.releaseReservation = releaseReservation;
    }

    internal static WorkingSaveStartProof Open(string dataRoot, string directory, SharedWorldVersion version,
        CancellationToken cancellationToken, Action? payloadVerified = null, Action? releaseReservation = null)
    {
        var proof = new WorkingSaveStartProof(dataRoot, directory, version, releaseReservation);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SharedWorldService.VerifySignature(version) || !AppInstance.ContainsPath(dataRoot, directory) ||
                string.Equals(Path.GetFullPath(dataRoot), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The working save needs an exact signed identity inside managed storage.");
            SharedWorldService.EnsureUnlinkedRoot(dataRoot, directory);
            var parent = OpenAnchor(dataRoot, 0x80000000, 3 /* read/write, no delete */, IntPtr.Zero,
                3, 0x02200000 /* directory + open reparse point */, IntPtr.Zero);
            proof.directories.Add(parent);
            if (parent.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((File.GetAttributes(parent) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("The working save anchor is not a plain directory.");
            var parts = Path.GetRelativePath(dataRoot, directory).Split(Path.DirectorySeparatorChar);
            for (var index = 0; index < parts.Length; index++)
            {
                parent = NativeSnapshotDirectory.OpenDirectory(parent, parts[index], namespaceWrites: index < parts.Length - 1);
                proof.directories.Add(parent);
            }
            proof.payloadDirectories.Add("", parent);
            foreach (var file in version.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var names = file.Path.Split('/');
                var relative = "";
                var current = parent;
                for (var index = 0; index < names.Length - 1; index++)
                {
                    relative += "/" + names[index];
                    if (!proof.payloadDirectories.TryGetValue(relative, out var child))
                    {
                        child = NativeSnapshotDirectory.OpenDirectory(current, names[index]);
                        proof.payloadDirectories.Add(relative, child);
                        proof.directories.Add(child);
                    }
                    current = child;
                }
                var stream = new FileStream(NativeSnapshotDirectory.OpenReadFile(current, names[^1]), FileAccess.Read);
                proof.files.Add(stream);
                if (stream.Length != file.Length ||
                    !WorldPeerPayloadHash.Hash(stream, cancellationToken).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The working save differs from its exact signed source.");
            }
            proof.verified = true;
            proof.RequireCurrent(dataRoot, directory, version, cancellationToken);
            payloadVerified?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return proof;
        }
        catch (Win32Exception error)
        {
            proof.Dispose();
            throw new IOException("The working save namespace could not be held for Start.", error);
        }
        catch { proof.Dispose(); throw; }
    }

    internal void RequireCurrent(string expectedRoot, string expectedDirectory, SharedWorldVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!verified || files.Count == 0 || expectedVersion.VersionHash != version.VersionHash ||
            !dataRoot.Equals(Path.GetFullPath(expectedRoot), StringComparison.OrdinalIgnoreCase) ||
            !directory.Equals(Path.GetFullPath(expectedDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The operation's held working save changed or was released.");
        // Held files cannot change or be replaced. Check namespace additions as
        // well: a new unmanifested file must never bypass the existing layout rule.
        SharedWorldService.EnsureUnlinkedRoot(dataRoot, directory);
        var expected = version.Files.Select(file => file.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Relative, SafeFileHandle Handle)>();
        pending.Push(("", payloadDirectories[""]));
        var remaining = checked(SharedWorldService.MaximumFiles * 4);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in ReadDirectory(current.Handle, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (--remaining < 0) throw new InvalidDataException("The working save layout exceeds its bounded inventory.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The working save layout became linked.");
                var relative = current.Relative.Length == 0 ? entry.Name : current.Relative + "/" + entry.Name;
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    BeforeDirectoryOpenForChecks?.Invoke(relative);
                    var key = "/" + relative;
                    if (!payloadDirectories.TryGetValue(key, out var child))
                    {
                        // An unmanifested empty directory is permitted by the
                        // existing layout rule, but traverse it only through a
                        // leased parent-relative open which refuses links.
                        child = NativeSnapshotDirectory.OpenDirectory(current.Handle, entry.Name);
                        payloadDirectories.Add(key, child);
                        directories.Add(child);
                    }
                    else
                    {
                        // Reopen the name even when its original handle is
                        // held: attribute-only reparse changes bypass share
                        // flags on an otherwise empty directory.
                        using var named = NativeSnapshotDirectory.OpenDirectory(current.Handle, entry.Name);
                        if (ValheimAutosaveObservationCandidate.FileIdentity.Read(named) !=
                            ValheimAutosaveObservationCandidate.FileIdentity.Read(child))
                            throw new InvalidDataException("The working save directory identity changed.");
                    }
                    pending.Push((relative, child));
                }
                else if (!expected.Contains(relative))
                    throw new InvalidDataException("The working save layout changed after verification.");
            }
        }
    }

    private static IEnumerable<(string Name, FileAttributes Attributes)> ReadDirectory(SafeFileHandle directory,
        CancellationToken cancellationToken)
    {
        const int capacity = 64 * 1024;
        const int header = 68; // FILE_FULL_DIR_INFO: FileAttributes=56, FileNameLength=60, FileName=68.
        var bytes = new byte[capacity];
        var memory = Marshal.AllocHGlobal(capacity); // Native records require 8-byte alignment.
        var restart = true;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(directory) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new InvalidDataException("The leased working directory became linked.");
                if (!GetFileInformationByHandleEx(directory, restart ? 15u : 14u, memory, capacity))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 18 /* ERROR_NO_MORE_FILES */)
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("The enumerated working directory became linked.");
                        yield break;
                    }
                    throw new Win32Exception(error, "The leased working directory could not be enumerated.");
                }
                restart = false;
                Marshal.Copy(memory, bytes, 0, capacity);
                var offset = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (offset < 0 || offset > capacity - header || (offset & 7) != 0)
                        throw new InvalidDataException("A native directory record has an invalid boundary.");
                    var next = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                    var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 56));
                    var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 60));
                    if (length is 0 or > 510 || (length & 1) != 0 || length > capacity - offset - header)
                        throw new InvalidDataException("A native directory name exceeds its bounded record.");
                    var end = checked(header + (int)length);
                    if (next != 0 && ((next & 7) != 0 || next < end || next > capacity - offset - header))
                        throw new InvalidDataException("A native directory record has an invalid successor.");
                    var name = StrictUnicode.GetString(bytes, offset + header, (int)length);
                    if (name is not ("." or ".."))
                    {
                        if (name.Any(character => char.IsControl(character) || character is '/' or '\\' or ':'))
                            throw new InvalidDataException("A native directory name is not one local basename.");
                        yield return (name, attributes);
                    }
                    if (next == 0) break;
                    offset = checked(offset + (int)next);
                }
                // Enumeration uses the already-open object, never a path. A
                // late reparse cannot redirect these reads to its target.
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The enumerated working directory became linked.");
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public void Dispose()
    {
        verified = false;
        try
        {
            for (var index = files.Count - 1; index >= 0; index--) files[index].Dispose();
            files.Clear();
            for (var index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
            directories.Clear();
            payloadDirectories.Clear();
        }
        finally { Interlocked.Exchange(ref releaseReservation, null)?.Invoke(); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenAnchor(string path, uint access, uint sharing,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, uint informationClass,
        IntPtr information, int size);
}
