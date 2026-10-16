using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

// Acceptance-only verification never calls or relaxes the playable-save verifier.
// All signed files and their namespace stay leased through the protected commit
// and receipt. An uncommitted renamed directory is private interrupted evidence.
internal sealed class WorldPeerVerifiedTestCopy : IDisposable
{
    private readonly WorldPeerTestNamespace names;
    private readonly List<FileStream> files = [];
    internal string Root { get; }
    internal string ArtifactHash { get; }
    private bool disposed;

    private WorldPeerVerifiedTestCopy(string dataRoot, string root, LiveSaveTestArtifact artifact)
    {
        Root = Path.GetFullPath(root);
        ArtifactHash = HashArtifact(artifact);
        names = new(dataRoot, root, create: false);
    }

    internal static string HashArtifact(LiveSaveTestArtifact artifact) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(artifact, LiveSaveAcceptanceTrust.Json)));

    internal static WorldPeerVerifiedTestCopy Open(string dataRoot, string root,
        LiveSaveTestArtifact artifact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!LiveSaveAcceptanceTrust.VerifyArtifact(artifact))
            throw new InvalidDataException("The private test artifact is invalid.");
        var lease = new WorldPeerVerifiedTestCopy(dataRoot, root, artifact);
        try
        {
            var metadata = lease.names.Read("artifact.json");
            lease.files.Add(metadata);
            if (metadata.Length is < 1 or > SharedWorldService.MaximumManifestBytes)
                throw new InvalidDataException("The private test descriptor is oversized.");
            using var body = new MemoryStream(); metadata.CopyTo(body);
            var recorded = WorldPeerProtocol.DecodeEnvelope<LiveSaveTestArtifact>(body.ToArray(),
                checked((int)SharedWorldService.MaximumManifestBytes));
            if (!LiveSaveAcceptanceTrust.VerifyArtifact(recorded) || HashArtifact(recorded) != lease.ArtifactHash)
                throw new InvalidDataException("The private test descriptor differs from the selected artifact.");
            foreach (var file in artifact.Version.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = lease.names.Read("payload/" + file.Path);
                lease.files.Add(input);
                if (input.Length != file.Length || !HashFile(input, cancellationToken).Equals(file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The private test payload failed complete verification.");
            }
            lease.names.RequireInventory(artifact.Version.Files, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return lease;
        }
        catch (Win32Exception error)
        { lease.Dispose(); throw new IOException("The private test namespace could not be leased.", error); }
        catch { lease.Dispose(); throw; }
    }

    internal static string HashFile(Stream input, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; int read;
        while ((read = input.Read(buffer)) > 0)
        { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal void Require(string root, LiveSaveTestArtifact artifact)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Root.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || ArtifactHash != HashArtifact(artifact))
            throw new InvalidDataException("The private test completion lease does not match this exact copy.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (var index = files.Count - 1; index >= 0; index--) files[index].Dispose();
        files.Clear(); names.Dispose();
    }
}

internal sealed record WorldPeerTestCompletion(int Schema, SharedWorldPeerRoot Root,
    Guid TestId, string VersionHash, string ArtifactHash, Guid ReceiverDeviceId,
    string ReceiverPublicKey, long RosterEpoch, long RosterRevision,
    SharedWorldReceipt? Receipt, bool ReceiptRegistered = false);

// Completion is a protected exact-copy marker, separate from the directory's
// existence. A stored receipt is reused until the current signed roster changes.
internal sealed class WorldPeerReceivedTestJournal(LocalData data, SharedWorldPeerRoot root)
{
    private string Name(Guid testId) => $"world-peer-live-test-{root.GroupKey}-{testId:N}.protected";
    internal static string StorageRoot(LocalData data, SharedWorldPeerRoot root, Guid testId) =>
        Path.Combine(data.RootPath, "received-live-save-tests", root.GroupKey, testId.ToString("N"));

    internal WorldPeerTestCompletion? Read(LiveSaveTestArtifact artifact)
    {
        var name = Name(artifact.TestId);
        var bytes = data.LoadProtected(name);
        if (bytes is null)
        {
            if (data.HasProtected(name) || data.Recovery.Notices.Any(notice => notice.StateFile == name))
                throw new InvalidDataException("The private test completion marker needs review.");
            return null;
        }
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("The private test marker is oversized.");
        var marker = WorldPeerProtocol.DecodeEnvelope<WorldPeerTestCompletion>(bytes, 16 * 1024);
        Validate(artifact, marker);
        return marker;
    }

    internal void Save(LiveSaveTestArtifact artifact, WorldPeerTestCompletion marker,
        WorldPeerVerifiedTestCopy lease)
    {
        Validate(artifact, marker);
        lease.Require(StorageRoot(data, root, artifact.TestId), artifact);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, LiveSaveAcceptanceTrust.Json);
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("The private test marker is oversized.");
        var previous = data.LoadProtected(Name(artifact.TestId));
        if (previous is not null && previous.AsSpan().SequenceEqual(bytes)) return;
        data.SaveProtected(Name(artifact.TestId), bytes);
    }

    private void Validate(LiveSaveTestArtifact artifact, WorldPeerTestCompletion marker)
    {
        if (!SharedWorldPeerTrust.ValidRoot(root) || !LiveSaveAcceptanceTrust.VerifyArtifact(artifact) ||
            artifact.Version.ProfileId != root.ProfileId || artifact.Version.GroupId != root.GroupId ||
            marker.Schema != 1 || marker.Root != root || marker.TestId != artifact.TestId ||
            marker.VersionHash != artifact.Version.VersionHash ||
            marker.ArtifactHash != WorldPeerVerifiedTestCopy.HashArtifact(artifact) ||
            marker.RosterEpoch < 1 || marker.RosterRevision < 1 ||
            !SharedWorldRosterTrust.ValidKey(marker.ReceiverPublicKey) ||
            marker.Receipt is not { } receipt ||
                receipt.DeviceId != marker.ReceiverDeviceId || receipt.GroupId != root.GroupId ||
                receipt.ProfileId != root.ProfileId || receipt.VersionHash != marker.VersionHash ||
                receipt.RosterEpoch != marker.RosterEpoch || receipt.RosterRevision != marker.RosterRevision ||
            (marker.ReceiverDeviceId == Guid.Empty ? marker.ReceiverPublicKey != root.OwnerPublicKey ||
                !WorldPeerOwnerTestReceiptTrust.Verify(receipt, root) :
                !SharedWorldReceiptTrust.Verify(receipt, marker.ReceiverPublicKey)))
            throw new InvalidDataException("The private test completion marker failed exact-copy verification.");
    }
}

// Parent-relative directory opens reject reparse points. Held ancestors deny
// deletion; resumed file handles reject final links before any write/truncate.
internal sealed class WorldPeerTestNamespace : IDisposable
{
    private readonly List<SafeFileHandle> directories = [];
    private readonly Dictionary<string, SafeFileHandle> children = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;
    private readonly bool create;

    internal WorldPeerTestNamespace(string dataRoot, string root, bool create)
    {
        this.root = Path.GetFullPath(root); this.create = create;
        try
        {
            SharedWorldService.EnsureUnlinkedRoot(dataRoot, root);
            var volume = Path.GetPathRoot(this.root)!;
            var nativeVolume = volume.StartsWith(@"\\?\", StringComparison.Ordinal) ? volume :
                volume.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + volume[2..] : @"\\?\" + volume;
            var parent = OpenFile(nativeVolume, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            directories.Add(parent);
            if (parent.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((File.GetAttributes(parent) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("The private storage volume is invalid.");
            foreach (var part in this.root[volume.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                parent = create ? NativeSnapshotDirectory.CreateDirectory(parent, part, namespaceWrites: true) :
                    NativeSnapshotDirectory.OpenDirectory(parent, part, namespaceWrites: true);
                directories.Add(parent);
            }
            children[""] = parent;
        }
        catch { Dispose(); throw; }
    }

    private SafeFileHandle Parent(string relative)
    {
        _ = FullPath(relative);
        var parts = relative.Split('/'); var path = ""; var parent = children[""];
        for (var index = 0; index < parts.Length - 1; index++)
        {
            path += "/" + parts[index];
            if (!children.TryGetValue(path, out var child))
            {
                child = create ? NativeSnapshotDirectory.CreateDirectory(parent, parts[index], namespaceWrites: true) :
                    NativeSnapshotDirectory.OpenDirectory(parent, parts[index], namespaceWrites: true);
                children[path] = child; directories.Add(child);
            }
            parent = child;
        }
        return parent;
    }

    private string FullPath(string relative)
    {
        if (relative.Length > 4096 || Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Split('/').Any(part => !SharedWorldService.SafePath(part)))
            throw new InvalidDataException("The private test child name is invalid.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The private test child escaped its storage.");
        return full;
    }

    internal FileStream Read(string relative) => new(NativeSnapshotDirectory.OpenReadFile(Parent(relative),
        relative.Split('/')[^1]), FileAccess.Read);

    internal FileStream Resume(string relative)
    {
        if (!create || !relative.StartsWith("payload/", StringComparison.Ordinal)) throw new InvalidDataException();
        _ = Parent(relative);
        var handle = OpenFile(FullPath(relative), 0xC0000000, 0,
            IntPtr.Zero, 4 /* OPEN_ALWAYS */, 0x00200000 /* open final reparse point itself */, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (((FileAttributes)info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                info.NumberOfLinks != 1) throw new InvalidDataException("The private partial file is linked.");
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        catch { handle.Dispose(); throw; }
    }

    internal void WriteArtifact(LiveSaveTestArtifact artifact)
    {
        if (!create || !LiveSaveAcceptanceTrust.VerifyArtifact(artifact)) throw new InvalidDataException();
        var path = Path.Combine(root, "artifact.json");
        if (File.Exists(path))
        {
            using var existing = Read("artifact.json");
            if (existing.Length > SharedWorldService.MaximumManifestBytes) throw new InvalidDataException();
            using var body = new MemoryStream(); existing.CopyTo(body);
            var recorded = WorldPeerProtocol.DecodeEnvelope<LiveSaveTestArtifact>(body.ToArray(), checked((int)SharedWorldService.MaximumManifestBytes));
            if (WorldPeerVerifiedTestCopy.HashArtifact(recorded) != WorldPeerVerifiedTestCopy.HashArtifact(artifact))
                throw new InvalidDataException("The private partial descriptor belongs to a different exact test.");
            return;
        }
        using var output = new FileStream(NativeSnapshotDirectory.CreateNewFile(children[""], "artifact.json"), FileAccess.Write);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, LiveSaveAcceptanceTrust.Json);
        if (bytes.Length > SharedWorldService.MaximumManifestBytes) throw new InvalidDataException();
        output.Write(bytes); output.Flush(true);
    }

    internal void RequireInventory(IReadOnlyList<SharedWorldFile> files, CancellationToken token)
    {
        var expected = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var payload = Path.Combine(root, "payload");
        var pending = new Stack<string>(); pending.Push(payload); var count = 0;
        while (pending.TryPop(out var directory))
        {
            if (++count > 4096) throw new InvalidDataException("The private test has too many directories.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(payload, path).Replace('\\', '/');
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The private test contains a linked child.");
                if (Directory.Exists(path)) { _ = Parent("payload/" + relative + "/.inventory"); pending.Push(path); }
                else if (!expected.Remove(relative)) throw new InvalidDataException("The private test contains an unsigned extra file.");
            }
        }
        if (expected.Count != 0) throw new InvalidDataException("The private test inventory is incomplete.");
    }

    public void Dispose()
    {
        for (var index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
        directories.Clear(); children.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerial, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
