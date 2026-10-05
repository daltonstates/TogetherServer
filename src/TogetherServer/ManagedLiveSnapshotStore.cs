using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

internal interface IManagedLiveSnapshotAdapter
{
    string Game { get; }
    bool LiveCaptureAccepted { get; }
    Task<ImmutableLiveSaveSnapshot> CaptureAsync(ServerProfile profile, ManagedRun run,
        CancellationToken cancellationToken, Guid? snapshotId = null);
}

// Neither paths nor completion evidence in this internal record are API inputs.
// The protected seal, rather than a caller-created record, authorizes consumption.
internal sealed record ImmutableLiveSaveSnapshot(Guid SnapshotId, Guid ProfileId,
    Guid OperationId, int ProcessId, long StartTimeUtcTicks, string Game, string WorldId,
    string SourceDirectory, string SnapshotDirectory, LiveSaveCompletionEvidence Completion,
    IReadOnlyList<SharedWorldFile> Files, string SetupSha256, DateTimeOffset CapturedUtc);

internal sealed record ManagedLiveSnapshotFile(string Path, long Length);

internal sealed class ManagedLiveSnapshotStore(LocalData data, GameServerRegistry games)
{
    private const string SnapshotRoot = "managed-live-snapshots";
    private const int MaximumPending = 8;
    private const int MaximumDirectories = 4096;
    private const int MaximumDepth = 64;
    private const long SpaceReserve = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Seal(int Schema, bool Complete, ImmutableLiveSaveSnapshot Snapshot);
    private sealed record SourceFile(string Path, long Length, long LastWriteTicks);
    private sealed class DirectoryLeases : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = [];
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); Handles.Clear(); }
    }

    internal Func<ManagedRun, bool>? FixtureRunIdentityForChecks { get; set; }
    internal Func<long>? FixtureAvailableBytesForChecks { get; set; }
    internal Action? BeforeSourceLeaseForChecks { get; set; }
    internal Action? AfterSourceLeaseForChecks { get; set; }
    internal Action? BeforeDestinationWriteForChecks { get; set; }
    internal Action<string>? AfterPrivateDirectoryCreatedForChecks { get; set; }
    internal Action? BeforeSnapshotCommitForChecks { get; set; }
    internal Action? AfterPayloadCopyForChecks { get; set; }

    internal ImmutableLiveSaveSnapshot Stage(ServerProfile profile, ManagedRun run,
        LiveSaveCompletionEvidence completion, IReadOnlyList<ManagedLiveSnapshotFile>? reviewedFiles = null,
        CancellationToken cancellationToken = default, Guid? reservedSnapshotId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireExactRun(profile, run);
        RequireCompletion(profile, run, completion);
        var source = ReviewedSource(profile, run);
        var setup = SetupHash(profile);
        var inventory = ReadInventory(source, cancellationToken);
        var selected = SelectFiles(profile, inventory, reviewedFiles);
        var size = TotalSize(selected.Select(file => file.Length));
        var profileRoot = ProfileRoot(profile.Id);
        using var destinationAncestors = new DirectoryLeases();
        HoldAncestors(data.RootPath, destinationAncestors.Handles, cancellationToken);
        CreatePinnedDirectory(Path.Combine(data.RootPath, SnapshotRoot), destinationAncestors.Handles, namespaceWrites: true);
        CreatePinnedDirectory(profileRoot, destinationAncestors.Handles, namespaceWrites: true);
        var pending = Directory.EnumerateFileSystemEntries(profileRoot)
            .Where(path => Path.GetFileName(path) != NativeSnapshotDirectory.GuardName).Take(MaximumPending + 1).Count();
        var intents = Directory.EnumerateFiles(data.RootPath,
            $"managed-live-snapshot-{profile.Id:N}-*.protected").Take(MaximumPending + 1).Count();
        if (pending >= MaximumPending || intents >= MaximumPending)
            throw new InvalidDataException("Discard interrupted or finished private snapshots before another capture.");
        var available = profile.Kind == GameKinds.Fixture && FixtureAvailableBytesForChecks is not null
            ? FixtureAvailableBytesForChecks() : new DriveInfo(Path.GetPathRoot(profileRoot)!).AvailableFreeSpace;
        if (available < checked(size * 2 + SpaceReserve))
            throw new IOException("There is not enough free space for a private save snapshot.");

        var id = reservedSnapshotId ?? Guid.NewGuid();
        var finalRoot = OwnedDirectory(profile.Id, id);
        var partial = finalRoot + ".partial";
        if (Directory.Exists(finalRoot) || Directory.Exists(partial) || File.Exists(finalRoot) || File.Exists(partial) ||
            data.HasProtected(SealName(profile.Id, id)))
            throw new InvalidDataException("This capture request already owns private state; discard it before retrying.");
        var payload = Path.Combine(partial, "payload");
        var provisional = new ImmutableLiveSaveSnapshot(id, profile.Id, run.OperationId,
            run.ProcessId!.Value, run.StartTimeUtcTicks!.Value, profile.Kind, profile.WorldId,
            source, Path.Combine(finalRoot, "payload"), completion,
            Array.AsReadOnly(Array.Empty<SharedWorldFile>()), setup, DateTimeOffset.UtcNow);
        var leases = new List<FileStream>();
        var directories = new List<SafeFileHandle>();
        var destinationDirectories = new List<SafeFileHandle>();
        var sealedSuccessfully = false;
        try
        {
            SaveSeal(new(1, false, provisional));
            if (profile.Kind == GameKinds.Fixture) AfterPrivateDirectoryCreatedForChecks?.Invoke(profileRoot);
            using var guardParent = OpenVerifiedDirectory(profileRoot, namespaceWrites: true);
            using var namespaceGuard = new FileStream(NativeSnapshotDirectory.OpenGuard(guardParent, NativeSnapshotDirectory.GuardName), FileAccess.ReadWrite);
            if (namespaceGuard.Length != 0 || (File.GetAttributes(namespaceGuard.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The private namespace guard changed.");
            // A held child keeps the namespace parent nonempty across promotion;
            // attribute-only reparse writes bypass ordinary share restrictions.
            CreatePinnedDirectory(partial, destinationDirectories);
            CreatePinnedDirectory(payload, destinationDirectories);
            var preparedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { payload };
            foreach (var file in selected)
            {
                var parent = Path.GetDirectoryName(SharedWorldService.SafeChild(payload, file.Path))!;
                var relative = Path.GetRelativePath(payload, parent);
                if (relative == ".") continue;
                var current = payload;
                foreach (var part in relative.Split(Path.DirectorySeparatorChar))
                {
                    current = Path.Combine(current, part);
                    if (preparedParents.Add(current)) CreatePinnedDirectory(current, destinationDirectories);
                }
            }
            if (profile.Kind == GameKinds.Fixture) BeforeSourceLeaseForChecks?.Invoke();
            // Directory handles deny rename/delete while all file handles deny writes
            // and deletes. A held Bedrock save that still owns write handles fails here.
            HoldSourceDirectories(source, directories, cancellationToken);
            foreach (var file in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = SharedWorldService.SafeChild(source, file.Path);
                RequirePlainPath(path);
                using var parent = OpenVerifiedDirectory(Path.GetDirectoryName(path)!);
                var stream = new FileStream(NativeSnapshotDirectory.OpenReadFile(parent, Path.GetFileName(path)), FileAccess.Read, 64 * 1024);
                leases.Add(stream);
                var current = new FileInfo(path);
                if (stream.Length != file.Length || current.LastWriteTimeUtc.Ticks != file.LastWriteTicks)
                    throw new InvalidDataException("A source save changed before its read lease was acquired.");
                RequireSameFile(path, stream);
            }
            if (profile.Kind == GameKinds.Fixture) AfterSourceLeaseForChecks?.Invoke();
            RequireInventoryEqual(inventory, ReadInventory(source, cancellationToken));
            RequireExactRun(profile, run);
            var files = new List<SharedWorldFile>();
            for (var index = 0; index < selected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = selected[index];
                var input = leases[index];
                var target = SharedWorldService.SafeChild(payload, file.Path);
                RequirePlainPath(target);
                if (profile.Kind == GameKinds.Fixture) BeforeDestinationWriteForChecks?.Invoke();
                input.Position = 0;
                string copiedHash;
                using (var parent = OpenVerifiedDirectory(Path.GetDirectoryName(target)!))
                using (var output = new FileStream(NativeSnapshotDirectory.CreateNewFile(parent, Path.GetFileName(target)),
                    FileAccess.Write, 64 * 1024))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[64 * 1024];
                    long copied = 0;
                    int count;
                    while ((count = input.Read(buffer)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        copied = checked(copied + count);
                        if (copied > file.Length) throw new InvalidDataException("A source save grew during capture.");
                        hash.AppendData(buffer, 0, count);
                        output.Write(buffer, 0, count);
                    }
                    if (copied != file.Length) throw new InvalidDataException("A source save was truncated during capture.");
                    output.Flush(true);
                    copiedHash = Convert.ToHexString(hash.GetHashAndReset());
                    var attributes = File.GetAttributes(output.SafeFileHandle);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("A private file became linked during capture.");
                    File.SetAttributes(output.SafeFileHandle, attributes | FileAttributes.ReadOnly);
                }
                input.Position = 0;
                if (Hash(input, cancellationToken) != copiedHash || HashFile(target, cancellationToken) != copiedHash)
                    throw new InvalidDataException("The leased source and immutable copy differ.");
                RequireSameFile(SharedWorldService.SafeChild(source, file.Path), input);
                files.Add(new(file.Path, file.Length, copiedHash));
            }
            if (profile.Kind == GameKinds.Fixture) AfterPayloadCopyForChecks?.Invoke();
            RequireInventoryEqual(inventory, ReadInventory(source, cancellationToken));
            RequireExactRun(profile, run);
            if (SetupHash(profile) != setup)
                throw new InvalidDataException("The reviewed server setup changed during capture.");
            cancellationToken.ThrowIfCancellationRequested();
            RequirePlainPath(partial);
            RequirePlainPath(finalRoot);
            // Release only the partial subtree after private writes finish. The
            // generated parent/ancestors remain pinned across rename, seal, Verify.
            foreach (var handle in destinationDirectories) handle.Dispose();
            destinationDirectories.Clear();
            if (profile.Kind == GameKinds.Fixture) BeforeSnapshotCommitForChecks?.Invoke();
            RequirePlainPath(partial);
            RequirePlainPath(finalRoot);
            Directory.Move(partial, finalRoot);
            var snapshot = provisional with { Files = Array.AsReadOnly(files.ToArray()), CapturedUtc = DateTimeOffset.UtcNow };
            SaveSeal(new(1, true, snapshot));
            if (!Verify(profile, run, snapshot, cancellationToken))
                throw new InvalidDataException("The protected private snapshot did not verify.");
            sealedSuccessfully = true;
            return snapshot;
        }
        finally
        {
            foreach (var stream in leases) stream.Dispose();
            foreach (var handle in directories) handle.Dispose();
            foreach (var handle in destinationDirectories) handle.Dispose();
            if (!sealedSuccessfully)
            {
                // Failure keeps an incomplete protected intent if a substituted path
                // prevents safe cleanup; it can never be consumed as a snapshot.
                try
                {
                    SaveSeal(new(1, false, provisional));
                    DeleteOwnedDirectory(profile.Id, id, partial: true);
                    DeleteOwnedDirectory(profile.Id, id, partial: false);
                    DeleteSeal(profile.Id, id);
                }
                catch (Exception ex) when (ExpectedFailure(ex)) { }
            }
        }
    }

    internal bool Verify(ServerProfile profile, ManagedRun run, ImmutableLiveSaveSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var seal = ReadSeal(snapshot.ProfileId, snapshot.SnapshotId);
            if (seal is not { Schema: 1, Complete: true } || !SameSnapshot(seal.Snapshot, snapshot)) return false;
            RequireSnapshotPaths(snapshot);
            RequireCompletion(profile, run, snapshot.Completion);
            RequireExactRun(profile, run);
            if (snapshot.ProfileId != profile.Id || snapshot.OperationId != run.OperationId ||
                snapshot.ProcessId != run.ProcessId || snapshot.StartTimeUtcTicks != run.StartTimeUtcTicks ||
                snapshot.Game != profile.Kind || snapshot.WorldId != profile.WorldId ||
                !SamePath(snapshot.SourceDirectory, ReviewedSource(profile, run)) ||
                snapshot.SetupSha256 != SetupHash(profile) || snapshot.Files.Count is < 1 or > SharedWorldService.MaximumFiles)
                return false;
            var inventory = ReadInventory(snapshot.SnapshotDirectory, cancellationToken);
            if (inventory.Count != snapshot.Files.Count) return false;
            var expected = snapshot.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            if (expected.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != expected.Length)
                return false;
            TotalSize(expected.Select(file => file.Length));
            for (var index = 0; index < expected.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = expected[index];
                if (inventory[index].Path != file.Path || inventory[index].Length != file.Length ||
                    file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit)) return false;
                var path = SharedWorldService.SafeChild(snapshot.SnapshotDirectory, file.Path);
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0 ||
                    HashFile(path, cancellationToken) != file.Sha256) return false;
            }
            RequireExactRun(profile, run);
            return snapshot.SetupSha256 == SetupHash(profile);
        }
        catch (Exception ex) when (ExpectedFailure(ex)) { return false; }
    }

    internal void Discard(ImmutableLiveSaveSnapshot snapshot)
    {
        var seal = ReadSeal(snapshot.ProfileId, snapshot.SnapshotId);
        if (seal is null) return; // Missing seals never authorize deleting a caller path.
        if (!SameSnapshot(seal.Snapshot, snapshot))
            throw new InvalidDataException("The snapshot does not match its protected discard authority.");
        RequireSnapshotPaths(snapshot);
        DeleteOwnedDirectory(snapshot.ProfileId, snapshot.SnapshotId, partial: true);
        DeleteOwnedDirectory(snapshot.ProfileId, snapshot.SnapshotId, partial: false);
        DeleteSeal(snapshot.ProfileId, snapshot.SnapshotId);
    }

    // Called only when no capture owns the lifecycle gate. An interrupted intent
    // authorizes discard, never completion, promotion, publication, or source reads.
    internal int DiscardInterrupted(Guid profileId)
    {
        var count = 0;
        RequirePlainPath(data.RootPath);
        foreach (var path in Directory.EnumerateFiles(data.RootPath,
                     $"managed-live-snapshot-{profileId:N}-*.protected").Take(MaximumPending + 1).ToArray())
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var suffix = name[$"managed-live-snapshot-{profileId:N}-".Length..];
            if (!Guid.TryParseExact(suffix, "N", out var id)) continue;
            var seal = ReadSeal(profileId, id);
            if (seal is not { Schema: 1, Complete: false } || seal.Snapshot.ProfileId != profileId || seal.Snapshot.SnapshotId != id) continue;
            RequireSnapshotPaths(seal.Snapshot);
            DeleteOwnedDirectory(profileId, id, partial: true);
            DeleteOwnedDirectory(profileId, id, partial: false);
            DeleteSeal(profileId, id);
            count++;
        }
        return count;
    }

    // The Host calls this after this exact request is durably withdrawn/published
    // under its lifecycle gate. Other copies, including the same run's, are preserved.
    internal void DiscardForAttempt(Guid profileId, Guid operationId, Guid snapshotId)
    {
        if (profileId == Guid.Empty || operationId == Guid.Empty || snapshotId == Guid.Empty)
            throw new InvalidDataException("An exact profile, operation, and capture request are required.");
        var seal = ReadSeal(profileId, snapshotId);
        if (seal is null) return;
        if (seal.Schema != 1 || seal.Snapshot.ProfileId != profileId || seal.Snapshot.SnapshotId != snapshotId ||
            seal.Snapshot.OperationId != operationId)
            throw new InvalidDataException("The private snapshot seal belongs to a different capture request.");
        RequireSnapshotPaths(seal.Snapshot);
        DeleteOwnedDirectory(profileId, snapshotId, partial: true);
        DeleteOwnedDirectory(profileId, snapshotId, partial: false);
        DeleteSeal(profileId, snapshotId);
    }

    internal void RequireExactRun(ServerProfile profile, ManagedRun run)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Managed snapshots require Windows file and process identity.");
        if (profile.Id == Guid.Empty || run.OperationId == Guid.Empty || run.ProfileId != profile.Id ||
            run.Kind != profile.Kind || run.WorldId != profile.WorldId || run.StopRequestedUtc is not null ||
            run.ProcessId is not > 0 || run.StartTimeUtcTicks is not > 0 ||
            !SamePath(run.WorldDirectory, profile.WorldDirectory) || run.GamePort != profile.GamePort ||
            !games.TryGet(profile.Kind, out var driver) ||
            !SamePath(driver.ManagedExecutablePath(profile), run.ExecutablePath))
            throw new InvalidDataException("The managed live run or its reviewed identity changed.");
        var saved = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == profile.Id);
        var recorded = data.LoadRuns().SingleOrDefault(item => item.ProfileId == profile.Id && item.OperationId == run.OperationId);
        if (saved is null || recorded is null ||
            JsonSerializer.Serialize(saved, Json) != JsonSerializer.Serialize(profile, Json) ||
            !SameRun(recorded, run))
            throw new InvalidDataException("The saved profile or recorded live run changed.");
        if (profile.Kind == GameKinds.Fixture && FixtureRunIdentityForChecks is not null)
        {
            if (!FixtureRunIdentityForChecks(recorded)) throw new InvalidDataException("The fixture live run is gone.");
            return;
        }
        using var process = Process.GetProcessById(recorded.ProcessId!.Value);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != recorded.StartTimeUtcTicks ||
            process.MainModule?.FileName is not { } executable || !SamePath(executable, recorded.ExecutablePath))
            throw new InvalidDataException("The recorded native live process changed.");
    }

    private static bool SameRun(ManagedRun a, ManagedRun b) =>
        a.ProfileId == b.ProfileId && a.OperationId == b.OperationId && a.Kind == b.Kind &&
        a.WorldId == b.WorldId && SamePath(a.WorldDirectory, b.WorldDirectory) &&
        SamePath(a.ExecutablePath, b.ExecutablePath) && a.ServerArtifactPath == b.ServerArtifactPath &&
        a.GamePort == b.GamePort && a.DeclaredPorts.SequenceEqual(b.DeclaredPorts) &&
        a.ProcessId == b.ProcessId && a.StartTimeUtcTicks == b.StartTimeUtcTicks &&
        a.StopRequestedUtc is null && b.StopRequestedUtc is null &&
        a.LogPath == b.LogPath && a.StopPipeName == b.StopPipeName &&
        a.ConsoleCaptureProcessId == b.ConsoleCaptureProcessId &&
        a.ConsoleCaptureStartTimeUtcTicks == b.ConsoleCaptureStartTimeUtcTicks &&
        a.ConsoleCaptureExecutablePath == b.ConsoleCaptureExecutablePath;

    private string ReviewedSource(ServerProfile profile, ManagedRun run)
    {
        if (!games.TryGet(profile.Kind, out var driver) || driver.ManagedSaveDirectory(profile) is not { } selected ||
            !Path.IsPathFullyQualified(selected)) throw new InvalidDataException("The game has no reviewed save source.");
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        var world = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.WorldDirectory));
        if (!SamePath(run.WorldDirectory, world) || !Inside(world, source))
            throw new InvalidDataException("The reviewed save source escaped the recorded world.");
        var owned = profile.Kind switch
        {
            GameKinds.Valheim => profile.WorldSource == "New" && SamePath(world, data.NewWorldDirectory(profile.Id)) && data.OwnsNewWorld(profile) ||
                profile.WorldSource == "Existing" && ValheimSetup.IsImportedWorld(data, profile.Id, world),
            GameKinds.Terraria or GameKinds.Fixture => SamePath(world, data.NewWorldDirectory(profile.Id)),
            GameKinds.Factorio => FactorioSetup.IsImportedCopy(data, profile),
            GameKinds.MinecraftJava or GameKinds.MinecraftBedrock =>
                SamePath(Path.GetDirectoryName(world)!, data.MinecraftInstallRoot) &&
                Path.GetFileName(world).StartsWith(profile.Kind == GameKinds.MinecraftJava ? "java-" : "bedrock-", StringComparison.OrdinalIgnoreCase) &&
                SamePath(source, profile.Kind == GameKinds.MinecraftJava ? Path.Combine(world, profile.WorldId) : Path.Combine(world, "worlds", profile.WorldId)),
            _ => false
        };
        if (!owned) throw new InvalidDataException("The save source is outside this profile's reviewed managed storage.");
        RequirePlainPath(source);
        if (!Directory.Exists(source)) throw new InvalidDataException("The reviewed save source is missing.");
        return source;
    }

    private static void RequireCompletion(ServerProfile profile, ManagedRun run, LiveSaveCompletionEvidence completion)
    {
        var kind = profile.Kind switch
        {
            GameKinds.MinecraftBedrock => LiveSaveEvidence.FrozenSnapshotQuery,
            GameKinds.Factorio => LiveSaveEvidence.ClosedSaveArchive,
            GameKinds.Valheim or GameKinds.MinecraftJava or GameKinds.Terraria or GameKinds.Fixture => LiveSaveEvidence.RunScopedCompletion,
            _ => LiveSaveEvidence.None
        };
        var matchesKind = completion.Kind == kind || profile.Kind == GameKinds.Fixture &&
            completion.Kind is LiveSaveEvidence.RunScopedCompletion or LiveSaveEvidence.FrozenSnapshotQuery or LiveSaveEvidence.ClosedSaveArchive;
        if (!completion.Complete || kind == LiveSaveEvidence.None || !matchesKind ||
            completion.ProfileId != profile.Id || completion.OperationId != run.OperationId ||
            completion.ProcessId != run.ProcessId || completion.StartTimeUtcTicks != run.StartTimeUtcTicks ||
            completion.CompletedUtc.UtcTicks < run.StartTimeUtcTicks || completion.CompletedUtc > DateTimeOffset.UtcNow.AddSeconds(5) ||
            completion.CompletedUtc < DateTimeOffset.UtcNow.AddMinutes(-5))
            throw new InvalidDataException("A fresh complete save observation for this exact run is required.");
    }

    private string SetupHash(ServerProfile profile)
    {
        var decoded = ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data));
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(decoded, Json)));
    }

    private List<SourceFile> ReadInventory(string root, CancellationToken token)
    {
        RequirePlainPath(root);
        if (!Directory.Exists(root)) throw new InvalidDataException("The snapshot tree is missing.");
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var files = new List<SourceFile>();
        var directories = 0;
        while (pending.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            RequirePlainPath(directory);
            if (++directories > MaximumDirectories || depth > MaximumDepth)
                throw new InvalidDataException("The save directory exceeds its traversal limit.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A save tree contains a link.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push((entry, depth + 1)); continue; }
                if (files.Count >= SharedWorldService.MaximumFiles) throw new InvalidDataException("The save tree has too many files.");
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                SharedWorldService.SafeChild(root, relative);
                var info = new FileInfo(entry);
                files.Add(new(relative, info.Length, info.LastWriteTimeUtc.Ticks));
            }
        }
        TotalSize(files.Select(file => file.Length));
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<SourceFile> SelectFiles(ServerProfile profile, IReadOnlyList<SourceFile> inventory,
        IReadOnlyList<ManagedLiveSnapshotFile>? reviewedFiles)
    {
        if (reviewedFiles is not null)
        {
            if (reviewedFiles.Count is < 1 or > SharedWorldService.MaximumFiles ||
                reviewedFiles.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != reviewedFiles.Count)
                throw new InvalidDataException("The reviewed save query is empty, ambiguous, or too large.");
            var queried = new List<SourceFile>();
            foreach (var file in reviewedFiles)
            {
                var found = inventory.SingleOrDefault(item => item.Path == file.Path);
                if (found is null || file.Length < 0 || found.Length != file.Length)
                    throw new InvalidDataException("The reviewed save query no longer names exact existing bytes.");
                queried.Add(found);
            }
            if (profile.Kind == GameKinds.MinecraftBedrock || profile.Kind == GameKinds.Fixture)
                return queried.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            throw new InvalidDataException("This game does not accept a query-selected save file list.");
        }
        IReadOnlyList<SourceFile> selected = profile.Kind switch
        {
            GameKinds.MinecraftJava => inventory.Where(file => file.Path != "session.lock").ToArray(),
            GameKinds.Terraria => inventory.Where(file => file.Path == profile.WorldId + ".wld").ToArray(),
            GameKinds.Factorio => inventory.Where(file => file.Path == profile.WorldId + ".zip").ToArray(),
            GameKinds.Valheim => inventory.Where(file => file.Path == $"worlds_local/{profile.WorldId}.db" ||
                file.Path == $"worlds_local/{profile.WorldId}.fwl" || file.Path.StartsWith($"worlds_local/{profile.WorldId}/", StringComparison.Ordinal)).ToArray(),
            GameKinds.Fixture => inventory,
            _ => throw new InvalidDataException("This game requires an exact reviewed frozen query.")
        };
        if (selected.Count == 0 || profile.Kind == GameKinds.MinecraftJava && !selected.Any(file => file.Path == "level.dat") ||
            profile.Kind == GameKinds.Valheim && (!selected.Any(file => file.Path == $"worlds_local/{profile.WorldId}.fwl") ||
                !selected.Any(file => file.Path == $"worlds_local/{profile.WorldId}.db" || file.Path.StartsWith($"worlds_local/{profile.WorldId}/", StringComparison.Ordinal))))
            throw new InvalidDataException("The reviewed save layout is incomplete or unknown.");
        return selected;
    }

    private void HoldSourceDirectories(string source, List<SafeFileHandle> handles, CancellationToken token)
    {
        HoldAncestors(source, handles, token);
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((source, 0));
        var count = 0;
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            RequirePlainPath(directory);
            if (++count > MaximumDirectories || depth > MaximumDepth) throw new InvalidDataException("Too many save directories.");
            if (!SamePath(directory, source)) handles.Add(OpenVerifiedDirectory(directory));
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A save directory contains a link.");
                if ((attributes & FileAttributes.Directory) != 0) queue.Enqueue((child, depth + 1));
            }
        }
    }

    private void HoldAncestors(string source, List<SafeFileHandle> handles, CancellationToken token)
    {
        RequirePlainPath(source);
        var chain = new List<string>();
        for (var ancestor = source; ; ancestor = Path.GetDirectoryName(ancestor)!)
        {
            chain.Add(ancestor);
            if (SamePath(ancestor, data.RootPath)) break;
        }
        foreach (var ancestor in chain.AsEnumerable().Reverse())
        {
            token.ThrowIfCancellationRequested();
            RequirePlainPath(ancestor);
            handles.Add(OpenVerifiedDirectory(ancestor,
                namespaceWrites: SamePath(ancestor, data.RootPath) || !SamePath(ancestor, source)));
        }
    }

    private void CreatePinnedDirectory(string path, List<SafeFileHandle> handles, bool namespaceWrites = false)
    {
        RequirePlainPath(path);
        using var parent = OpenVerifiedDirectory(Path.GetDirectoryName(path)!, namespaceWrites: true);
        var created = NativeSnapshotDirectory.CreateDirectory(parent, Path.GetFileName(path), namespaceWrites);
        try
        {
            RequirePlainPath(path);
            using var named = NativeSnapshotDirectory.OpenDirectory(parent, Path.GetFileName(path), namespaceWrites);
            if (ValheimAutosaveObservationCandidate.FileIdentity.Read(created) !=
                ValheimAutosaveObservationCandidate.FileIdentity.Read(named))
                throw new InvalidDataException("A generated private directory changed during creation.");
            handles.Add(created);
        }
        catch { created.Dispose(); throw; }
    }

    private SafeFileHandle OpenVerifiedDirectory(string path, bool namespaceWrites = false)
    {
        RequirePlainPath(path);
        // LocalData holds host.lock without delete sharing, so its root cannot
        // become empty/reparsed. Every child is opened relative to that anchor.
        if (SamePath(path, data.RootPath)) return OpenDirectoryLease(path, namespaceWrites: true);
        using var parent = OpenVerifiedDirectory(Path.GetDirectoryName(path)!, namespaceWrites: true);
        var handle = NativeSnapshotDirectory.OpenDirectory(parent, Path.GetFileName(path), namespaceWrites);
        try
        {
            RequirePlainPath(path);
            using var named = NativeSnapshotDirectory.OpenDirectory(parent, Path.GetFileName(path), namespaceWrites);
            if (ValheimAutosaveObservationCandidate.FileIdentity.Read(named) !=
                ValheimAutosaveObservationCandidate.FileIdentity.Read(handle))
                throw new InvalidDataException("A managed directory changed while its lease was acquired.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenDirectoryLease(string path, bool namespaceWrites)
    {
        var handle = CreateFile(path, 0x80000000 /* generic read participates in share checks */,
            namespaceWrites ? 3u : 1u /* never share delete */, IntPtr.Zero, 3 /* open existing */,
            0x02200000 /* backup semantics + open reparse point */, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }

    private void RequireSameFile(string path, FileStream leased)
    {
        using var parent = OpenVerifiedDirectory(Path.GetDirectoryName(path)!);
        using var named = new FileStream(NativeSnapshotDirectory.OpenReadFile(parent, Path.GetFileName(path)), FileAccess.Read);
        if (ValheimAutosaveObservationCandidate.FileIdentity.Read(named.SafeFileHandle) !=
            ValheimAutosaveObservationCandidate.FileIdentity.Read(leased.SafeFileHandle))
            throw new InvalidDataException("A source save file was replaced while copying.");
    }

    private static void RequireInventoryEqual(IReadOnlyList<SourceFile> before, IReadOnlyList<SourceFile> after)
    {
        if (!before.SequenceEqual(after)) throw new InvalidDataException("The save tree changed during immutable capture.");
    }

    private static long TotalSize(IEnumerable<long> sizes)
    {
        long total = 0;
        foreach (var size in sizes)
        {
            if (size < 0 || size > SharedWorldService.MaximumSharedWorldBytes - total)
                throw new InvalidDataException("The save exceeds the bounded snapshot size.");
            total += size;
        }
        return total;
    }

    private string ProfileRoot(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("A managed profile identity is required.");
        var root = Path.Combine(data.RootPath, SnapshotRoot, id.ToString("N"));
        RequirePlainPath(root);
        return root;
    }
    private string OwnedDirectory(Guid profileId, Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("A snapshot identity is required.");
        return Path.Combine(ProfileRoot(profileId), id.ToString("N"));
    }
    private string SealName(Guid profileId, Guid id) => $"managed-live-snapshot-{profileId:N}-{id:N}.protected";
    private void SaveSeal(Seal seal)
    {
        RequireSnapshotLocation(seal.Snapshot);
        if (seal.Complete) RequireSnapshotPaths(seal.Snapshot);
        RequirePlainPath(Path.Combine(data.RootPath, SealName(seal.Snapshot.ProfileId, seal.Snapshot.SnapshotId)));
        data.SaveProtected(SealName(seal.Snapshot.ProfileId, seal.Snapshot.SnapshotId), JsonSerializer.SerializeToUtf8Bytes(seal, Json));
    }
    private Seal? ReadSeal(Guid profileId, Guid id)
    {
        if (profileId == Guid.Empty || id == Guid.Empty) return null;
        var name = SealName(profileId, id);
        var path = Path.Combine(data.RootPath, name);
        RequirePlainPath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("A snapshot seal is oversized.");
        var bytes = data.LoadProtected(name);
        return bytes is null ? null : JsonSerializer.Deserialize<Seal>(bytes, Json);
    }
    private void DeleteSeal(Guid profileId, Guid id)
    {
        RequirePlainPath(Path.Combine(data.RootPath, SealName(profileId, id)));
        data.DeleteProtected(SealName(profileId, id));
    }
    private void RequireSnapshotPaths(ImmutableLiveSaveSnapshot snapshot)
    {
        RequireSnapshotLocation(snapshot);
        RequirePlainPath(snapshot.SnapshotDirectory);
    }
    private void RequireSnapshotLocation(ImmutableLiveSaveSnapshot snapshot)
    {
        if (snapshot.ProfileId == Guid.Empty || snapshot.SnapshotId == Guid.Empty ||
            !SamePath(snapshot.SnapshotDirectory, Path.Combine(data.RootPath, SnapshotRoot,
                snapshot.ProfileId.ToString("N"), snapshot.SnapshotId.ToString("N"), "payload")))
            throw new InvalidDataException("The snapshot payload is outside its generated private root.");
    }
    private static bool SameSnapshot(ImmutableLiveSaveSnapshot a, ImmutableLiveSaveSnapshot b) =>
        JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json);

    private void DeleteOwnedDirectory(Guid profileId, Guid id, bool partial)
    {
        var root = OwnedDirectory(profileId, id) + (partial ? ".partial" : "");
        RequirePlainPath(root);
        if (!Directory.Exists(root)) return;
        using var parentLeases = new DirectoryLeases();
        HoldAncestors(ProfileRoot(profileId), parentLeases.Handles, CancellationToken.None);
        var inventory = ReadInventory(root, CancellationToken.None);
        // Complete the read-only traversal before changing attributes or deleting.
        // Keep parent paths stationary while deleting files, then remove only empty
        // directories. Recursive deletion must never follow a late substitution.
        var directories = new List<(string Path, SafeFileHandle Handle)>();
        try
        {
            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count != 0)
            {
                var directory = queue.Dequeue();
                RequirePlainPath(directory);
                directories.Add((directory, OpenVerifiedDirectory(directory)));
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("An interrupted snapshot contains a linked entry.");
                    if ((attributes & FileAttributes.Directory) != 0) queue.Enqueue(child);
                }
                if (directories.Count + queue.Count > MaximumDirectories)
                    throw new InvalidDataException("The interrupted snapshot contains too many directories.");
            }
            RequireInventoryEqual(inventory, ReadInventory(root, CancellationToken.None));
            foreach (var file in inventory)
            {
                var path = SharedWorldService.SafeChild(root, file.Path);
                RequirePlainPath(path);
                using var parent = OpenVerifiedDirectory(Path.GetDirectoryName(path)!, namespaceWrites: true);
                NativeSnapshotDirectory.DeleteFile(parent, Path.GetFileName(path));
            }
            foreach (var directory in directories.OrderByDescending(item => item.Path.Length))
            {
                // Release just this empty target; its parent remains pinned until
                // its own later delete. A last-component link is never traversed.
                directory.Handle.Dispose();
                RequirePlainPath(directory.Path);
                using var parent = OpenVerifiedDirectory(Path.GetDirectoryName(directory.Path)!, namespaceWrites: true);
                NativeSnapshotDirectory.DeleteDirectory(parent, Path.GetFileName(directory.Path));
            }
        }
        finally { foreach (var directory in directories) directory.Handle.Dispose(); }
    }

    private void RequirePlainPath(string path)
    {
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
        // EnsureUnlinkedRoot checks directories; include a final linked/broken file.
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A managed snapshot path contains a link.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool Inside(string root, string child) => SamePath(root, child) ||
        Path.GetFullPath(child).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string a, string b) => Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b) &&
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    private string HashFile(string path, CancellationToken token)
    {
        using var parent = OpenVerifiedDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(NativeSnapshotDirectory.OpenReadFile(parent, Path.GetFileName(path)), FileAccess.Read);
        return Hash(stream, token);
    }
    private static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static bool ExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception or
        JsonException or CryptographicException or OverflowException;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}
