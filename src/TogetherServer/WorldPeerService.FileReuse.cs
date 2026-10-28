using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    // One operation-owned prior save supplies bytes only. Neither this source
    // nor its subset lease can acknowledge a publication or authorize Start.
    private sealed record MatchingFile(SharedWorldFile Target, SharedWorldFile Source);
    internal Action<long>? MatchingFileCopyingForChecks { get; set; }

    private static MatchingFile[] MatchingFiles(SharedWorldVersion prior, SharedWorldVersion target)
    {
        if (prior.Files.Count is < 1 or > SharedWorldService.MaximumFiles)
            throw new InvalidDataException("The prior copy's matching-file manifest exceeds its bound.");
        _ = SharedWorldService.BoundedTotalBytes(prior.Files);
        var paths = new Dictionary<string, SharedWorldFile>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in prior.Files)
        {
            if (!SharedWorldService.SafePath(source.Path) || !aliases.Add(source.Path))
                throw new InvalidDataException("The prior copy's matching-file paths are ambiguous.");
            paths.Add(source.Path, source);
        }
        var matches = new List<MatchingFile>();
        foreach (var file in target.Files)
            if (file.Length > 0 && paths.TryGetValue(file.Path, out var source) && source.Length == file.Length &&
                source.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) matches.Add(new(file, source));
        return matches.ToArray();
    }

    private async Task ReuseMatchingFilesAsync(Group group, WorldPeerSaveDescriptor descriptor,
        string vault, string payloadRoot, SharedWorldVersion? anchor, IReadOnlyList<LocalSave> inventory,
        CancellationToken cancellationToken, Func<bool>? additionalAuthorization = null)
    {
        var version = descriptor.Version;
        var candidates = inventory.Take(32).Where(item =>
            !item.Descriptor.TestOnly && item.Descriptor.TestArtifact is null &&
            item.Descriptor.Version.VersionHash != version.VersionHash &&
            item.Descriptor.Version.Game == version.Game && item.Descriptor.Version.WorldId == version.WorldId)
            .Select(item => new
            {
                Copy = item,
                Files = MatchingFiles(item.Descriptor.Version, version)
            })
            .Where(item => item.Files.Length > 0)
            .OrderByDescending(item => item.Files.Sum(match => match.Target.Length))
            .ThenBy(item => item.Copy.Descriptor.Version.VersionHash, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return;
        var selected = candidates[0];
        // Already complete target files need no repeated source inventory/hash.
        // Length is only a staging hint; ordinary completion still hashes all.
        if (selected.Files.All(match =>
        {
            var path = SharedWorldService.SafeChild(payloadRoot, match.Target.Path);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
            return File.Exists(path) && new FileInfo(path).Length == match.Target.Length;
        })) return;

        ExistingCopyScope scope;
        lock (stateSync) scope = ExistingCopyScopeFor(group, descriptor, cancellationToken);
        async Task RequireCurrentAsync()
        {
            if (!await ReceiveAllowedAsync(group, cancellationToken))
                throw new OperationCanceledException("Current receiving consent changed during local file reuse.");
            lock (stateSync)
            {
                RequireExistingCopyScope(scope, descriptor, cancellationToken);
                if (!ChunkSourceIsCurrent(scope.Group, selected.Copy) || additionalAuthorization?.Invoke() == false)
                    throw new InvalidDataException("The prior copy's current source or forwarding permission changed.");
            }
        }
        await RequireCurrentAsync();
        WorldPeerFileTransfer.VerifyHistory(vault, descriptor, anchor);
        VerifyExistingCopyHistory(scope.Group, selected.Copy, selected.Copy.Descriptor, cancellationToken);
        using var lease = MatchingFileLease.Open(data.RootPath, RecoverySourceDirectory(selected.Copy),
            selected.Copy.Descriptor.Version, payloadRoot);
        try { MatchingFileCopyingForChecks?.Invoke(0); }
        catch (Exception) { /* Observations cannot authorize local reuse. */ }
        lease.RequirePlainNamespace();
        var buffer = ArrayPool<byte>.Shared.Rent(512 * 1024);
        var prefix = ArrayPool<byte>.Shared.Rent(512 * 1024);
        try
        {
            foreach (var match in selected.Files)
            {
                await RequireCurrentAsync();
                var output = lease.Target(match.Target.Path);
                var originalLength = output.Length;
                if (originalLength >= match.Target.Length) continue;
                var input = lease.Source(match.Source.Path);
                if (input is null) continue; // A missing file takes the normal network path.
                if (input.Length != match.Source.Length)
                    throw new InvalidDataException("The prior matching file changed its signed length.");
                var complete = false;
                try
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    output.Position = 0;
                    long position = 0;
                    while (position < match.Source.Length)
                    {
                        await RequireCurrentAsync();
                        var count = (int)Math.Min(512 * 1024, match.Source.Length - position);
                        await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                        hash.AppendData(buffer, 0, count);
                        var retained = (int)Math.Min(count, Math.Max(0, originalLength - position));
                        if (retained > 0)
                        {
                            await output.ReadExactlyAsync(prefix.AsMemory(0, retained), cancellationToken);
                            if (!buffer.AsSpan(0, retained).SequenceEqual(prefix.AsSpan(0, retained)))
                                throw new InvalidDataException("The staged matching file prefix changed.");
                        }
                        if (retained < count)
                            await output.WriteAsync(buffer.AsMemory(retained, count - retained), cancellationToken);
                        position += count;
                        try { MatchingFileCopyingForChecks?.Invoke(position); }
                        catch (Exception) { /* Observations cannot authorize local reuse. */ }
                    }
                    if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(match.Target.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The prior matching file failed its signed hash.");
                    await RequireCurrentAsync();
                    await output.FlushAsync(cancellationToken);
                    complete = true;
                }
                finally
                {
                    // Existing staged bytes were compared, never overwritten.
                    // A failed/cancelled local append cannot damage that prefix.
                    if (!complete) output.SetLength(originalLength);
                }
            }
            await RequireCurrentAsync();
            lease.RequirePlainNamespace();
            VerifyExistingCopyHistory(scope.Group, selected.Copy, selected.Copy.Descriptor, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prefix, clearArray: true);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    // A held manifest/namespace and matching read handles only. Deliberately no
    // Covers, receipt, pointer-promotion or complete-payload proof capability.
    private sealed class MatchingFileLease : IDisposable
    {
        private readonly List<SafeFileHandle> directories = [];
        private readonly Dictionary<string, SafeFileHandle> sourceDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SafeFileHandle> targetDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FileStream> sourceFiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FileStream> targetFiles = new(StringComparer.Ordinal);
        private FileStream? metadata;

        internal static MatchingFileLease Open(string dataRoot, string source, SharedWorldVersion version, string target)
        {
            var lease = new MatchingFileLease();
            try
            {
                var sourceRoot = lease.DirectoryPath(dataRoot, source);
                lease.metadata = new(NativeSnapshotDirectory.OpenReadFile(sourceRoot, "version.json"), FileAccess.Read);
                if (lease.metadata.Length is < 1 or > SharedWorldService.MaximumManifestBytes)
                    throw new InvalidDataException("The prior copy's signed manifest is oversized.");
                var recorded = JsonSerializer.Deserialize<SharedWorldVersion>(lease.metadata, Json);
                if (recorded?.VersionHash != version.VersionHash || !SharedWorldService.VerifySignature(recorded))
                    throw new InvalidDataException("The prior copy's signed manifest changed.");
                var payload = NativeSnapshotDirectory.OpenDirectory(sourceRoot, SharedWorldService.PayloadDirectory, namespaceWrites: true);
                lease.directories.Add(payload);
                lease.sourceDirectories.Add("", payload);
                lease.targetDirectories.Add("", lease.DirectoryPath(dataRoot, target));
                return lease;
            }
            catch (Win32Exception error)
            {
                lease.Dispose();
                throw new IOException("The prior matching-file namespace could not be held.", error);
            }
            catch { lease.Dispose(); throw; }
        }

        private SafeFileHandle DirectoryPath(string dataRoot, string path)
        {
            SharedWorldService.EnsureUnlinkedRoot(dataRoot, path);
            var current = OpenReuseAnchor(dataRoot, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            directories.Add(current);
            if (current.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((File.GetAttributes(current) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("The matching-file storage anchor is not a plain directory.");
            foreach (var part in Path.GetRelativePath(dataRoot, path).Split(Path.DirectorySeparatorChar))
            {
                current = NativeSnapshotDirectory.OpenDirectory(current, part, namespaceWrites: true);
                directories.Add(current);
            }
            return current;
        }

        private FileStream OpenRelative(string path, bool target)
        {
            if (!SharedWorldService.SafePath(path)) throw new InvalidDataException("The matching file path is invalid.");
            var cache = target ? targetDirectories : sourceDirectories;
            var current = cache[""];
            var parts = path.Split('/');
            var relative = "";
            for (var index = 0; index < parts.Length - 1; index++)
            {
                RequirePlainDirectory(current);
                relative += "/" + parts[index];
                if (!cache.TryGetValue(relative, out var child))
                {
                    child = target ? NativeSnapshotDirectory.CreateDirectory(current, parts[index], namespaceWrites: true) :
                        NativeSnapshotDirectory.OpenDirectory(current, parts[index], namespaceWrites: true);
                    cache.Add(relative, child); directories.Add(child);
                }
                current = child;
            }
            RequirePlainDirectory(current);
            return target ? new(NativeSnapshotDirectory.ResumeFile(current, parts[^1]), FileAccess.ReadWrite) :
                new(NativeSnapshotDirectory.OpenReadFile(current, parts[^1]), FileAccess.Read);
        }

        internal FileStream Target(string path)
        {
            if (targetFiles.TryGetValue(path, out var known)) return known;
            try
            {
                var stream = OpenRelative(path, target: true);
                targetFiles.Add(path, stream);
                return stream;
            }
            catch (Win32Exception error) { throw new IOException("The matching target file could not be held.", error); }
        }
        internal FileStream? Source(string path)
        {
            if (sourceFiles.TryGetValue(path, out var known)) return known;
            try
            {
                var stream = OpenRelative(path, target: false);
                sourceFiles.Add(path, stream);
                return stream;
            }
            catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return null; }
            catch (Win32Exception error) { throw new IOException("The prior matching file could not be held.", error); }
        }

        internal void RequirePlainNamespace()
        {
            foreach (var directory in directories)
                RequirePlainDirectory(directory);
            if (metadata is not null && (File.GetAttributes(metadata.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The held matching-file manifest changed attributes.");
            foreach (var stream in sourceFiles.Values.Concat(targetFiles.Values))
                if ((File.GetAttributes(stream.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A held matching file changed attributes.");
        }

        private static void RequirePlainDirectory(SafeFileHandle directory)
        {
            if ((File.GetAttributes(directory) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidDataException("The held matching-file directory changed attributes.");
        }

        public void Dispose()
        {
            foreach (var stream in targetFiles.Values) stream.Dispose();
            foreach (var stream in sourceFiles.Values) stream.Dispose();
            metadata?.Dispose();
            for (var index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
            directories.Clear();
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle OpenReuseAnchor(string path, uint access, uint sharing,
            IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
