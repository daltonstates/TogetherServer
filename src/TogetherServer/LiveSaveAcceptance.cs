using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

public sealed record LiveSaveTestArtifact(int Schema, bool TestOnly, Guid TestId,
    LiveSaveAcceptanceScope Scope, Guid OperationId, Guid SourceDeviceId,
    DateTimeOffset CompletionUtc, DateTimeOffset SnapshotUtc, string PayloadSha256,
    long PayloadBytes, int ChunkBytes, int ChunkCount, SharedWorldVersion Version,
    string Signature,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSeparateCopyBranch? Branch = null);
public sealed record LiveSaveTestView(Guid TestId, string State, string Code,
    DateTimeOffset RequestedUtc, LiveSaveAcceptanceScope Scope,
    LiveSaveTestArtifact? Artifact, IReadOnlyList<LiveSaveAcceptanceStage> Stages,
    bool Reviewed);
public sealed record LiveSaveAcceptanceView(bool Accepted, string Code, string Message,
    LiveSaveAcceptanceScope? Scope, IReadOnlyList<LiveSaveAcceptanceStage> Stages,
    IReadOnlyList<LiveSaveTestView> Tests);
public sealed record LiveSaveAcceptanceResult(bool Ok, string Code, string Message,
    LiveSaveTestView? Test = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LiveSaveTestConfirmation(
    [property: JsonRequired] string VersionHash, [property: JsonRequired] string PayloadSha256,
    [property: JsonRequired] string Stage, [property: JsonRequired] bool Confirmed);

// None of these records accepts a source path, process identity or completion
// supplied by an HTTP client. Observed stages come only from the fixed adapter.
internal sealed record LiveSaveAcceptanceRecord(Guid TestId, Guid ProfileId,
    Guid OperationId, LiveSaveAcceptanceScope Scope, DateTimeOffset RequestedUtc,
    string State = "Capturing", string Code = "LiveSaveTestCapturing",
    LiveSaveTestArtifact? Artifact = null, SharedWorldReceipt? Receipt = null,
    DateTimeOffset? TransferUtc = null, DateTimeOffset? LoadUtc = null,
    DateTimeOffset? ChangeUtc = null, DateTimeOffset? RestartUtc = null, bool Reviewed = false)
{
    internal bool Pending => State == "Capturing";
    internal bool AllStages => Artifact is not null && Receipt is not null &&
        TransferUtc is not null && LoadUtc is not null && ChangeUtc is not null && RestartUtc is not null;
    internal LiveSaveTestView View() => new(TestId, State, Code, RequestedUtc, Scope,
        Artifact, LiveSaveAcceptanceTrust.Stages(this), Reviewed);
}

internal static class LiveSaveAcceptanceTrust
{
    internal const string TestCaptureKind = "LiveSaveAcceptanceTest";
    internal static bool IsRecoveryProfile(ServerProfile profile) =>
        profile.SeparateCopySourceProfileId is not null || profile.SeparateCopyBranchHash is not null;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32
    };

    internal static string PayloadHash(IReadOnlyList<SharedWorldFile> files) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), Json)));

    internal static byte[] ArtifactBasis(LiveSaveTestArtifact artifact) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Domain = artifact.Schema == 1 ? "TogetherServer private live-save acceptance v1" :
                "TogetherServer private live-save acceptance v2",
            Artifact = artifact with { Signature = "" }
        }, Json);

    internal static bool VerifyArtifact(LiveSaveTestArtifact? artifact)
    {
        try
        {
            if (artifact is null || artifact.Schema is not (1 or 2) || !artifact.TestOnly ||
                artifact.TestId == Guid.Empty || artifact.OperationId == Guid.Empty ||
                !SharedWorldLiveSaveAdapters.SameRealInstallation(artifact.Scope, artifact.Scope) ||
                !SharedWorldService.VerifyAcceptanceTestVersion(artifact.Version) ||
                artifact.Version.BackupId != artifact.TestId || !ArtifactSourceIdentity(artifact) ||
                artifact.Version.Game != artifact.Scope.Game || artifact.Version.CreatedUtc != artifact.CompletionUtc ||
                artifact.CompletionUtc == default || artifact.SnapshotUtc < artifact.CompletionUtc ||
                artifact.PayloadSha256 != PayloadHash(artifact.Version.Files) ||
                artifact.PayloadBytes != TotalBytes(artifact.Version.Files) ||
                artifact.ChunkBytes != SharedWorldService.ChunkBytes ||
                artifact.ChunkCount != ChunkCount(artifact.Version.Files)) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(artifact.Version.SigningPublicKey), out var consumed);
            return consumed == Convert.FromBase64String(artifact.Version.SigningPublicKey).Length && key.KeySize == 256 &&
                key.VerifyData(ArtifactBasis(artifact), Convert.FromBase64String(artifact.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ExpectedFailure(ex)) { return false; }
    }

    private static bool ArtifactSourceIdentity(LiveSaveTestArtifact artifact)
    {
        if (artifact.Schema == 1)
            return artifact.Branch is null && artifact.Version.ProfileId == artifact.Scope.ProfileId;
        if (artifact.Branch is not { } branch || !SharedWorldSeparateCopyStore.Verify(branch)) return false;
        var source = branch.Offer.Version;
        return source.ProfileId == branch.Offer.Roster.ProfileId && source.GroupId == branch.Offer.Roster.GroupId &&
            source.ProfileId == branch.Offer.Proposal.ProfileId && source.GroupId == branch.Offer.Proposal.GroupId &&
            artifact.Scope.ProfileId != source.ProfileId &&
            artifact.Scope.ProfileId == SharedWorldBranchLiveSource.LocalProfileId(branch.BranchHash) &&
            artifact.Version.ProfileId == source.ProfileId && artifact.Version.GroupId == source.GroupId &&
            artifact.Version.Game == source.Game && artifact.Version.WorldId == source.WorldId &&
            artifact.Version.SigningPublicKey == branch.Offer.Proposal.CandidatePublicKey &&
            artifact.SourceDeviceId == branch.Offer.Proposal.ProposerDeviceId;
    }

    // A captured signed branch proves identity, never perpetual access. Both the
    // source and the receipt signer must still belong to the current root roster.
    internal static bool SourceMatchesRoster(LiveSaveTestArtifact artifact,
        SharedWorldRoster roster, DateTimeOffset now)
    {
        if (!VerifyArtifact(artifact) || !SharedWorldRosterTrust.Verify(roster) ||
            roster.ProfileId != artifact.Version.ProfileId || roster.GroupId != artifact.Version.GroupId)
            return false;
        if (artifact.Branch is { } branch &&
            (branch.Offer.Roster.OwnerPublicKey != roster.OwnerPublicKey ||
             roster.Epoch < branch.Offer.Roster.Epoch ||
             roster.Revision < branch.Offer.Roster.Revision ||
             roster.Revision == branch.Offer.Roster.Revision && roster.Signature != branch.Offer.Roster.Signature))
            return false;
        if (artifact.SourceDeviceId == Guid.Empty)
            return artifact.Branch is { } ownerBranch
                ? SharedWorldSeparateCopyStore.IsOwnerOffer(ownerBranch.Offer) && ownerBranch.CandidatePublicKey == roster.OwnerPublicKey
                : artifact.Version.SigningPublicKey == roster.OwnerPublicKey;
        var member = roster.Members.SingleOrDefault(item => item.DeviceId == artifact.SourceDeviceId);
        return member is { Revoked: false, Grants: { Receive: true, EligibleHost: true } } &&
            (member.AccessExpiresUtc is null || now < member.AccessExpiresUtc) &&
            artifact.Version.SigningPublicKey != roster.OwnerPublicKey &&
            (artifact.Branch is null || member.PublicKey == artifact.Branch.CandidatePublicKey);
    }

    internal static bool MatchesSource(LiveSaveTestArtifact artifact, LiveSaveSourceContext source,
        ServerProfile profile, DateTimeOffset now) => VerifyArtifact(artifact) &&
        artifact.Scope.ProfileId == profile.Id && source.LocalProfileId == profile.Id &&
        artifact.Version.ProfileId == source.Root.ProfileId && artifact.Version.GroupId == source.Root.GroupId &&
        artifact.Version.Game == profile.Kind && artifact.Version.WorldId == profile.WorldId &&
        artifact.Version.SigningPublicKey == source.PublishingPublicKey && artifact.SourceDeviceId == source.SourceDeviceId &&
        artifact.Branch?.BranchHash == source.Branch?.BranchHash &&
        source.CurrentRoster.OwnerPublicKey == source.Root.OwnerPublicKey &&
        SourceMatchesRoster(artifact, source.CurrentRoster, now);

    internal static long TotalBytes(IReadOnlyList<SharedWorldFile> files)
    {
        long bytes = 0;
        if (files.Count is < 1 or > SharedWorldService.MaximumFiles) throw new InvalidDataException();
        foreach (var file in files)
        {
            if (file.Length < 0) throw new InvalidDataException();
            bytes = checked(bytes + file.Length);
            if (bytes > SharedWorldService.MaximumSharedWorldBytes) throw new InvalidDataException();
        }
        return bytes;
    }

    // Chunks never straddle files. Empty files are represented in the signed
    // inventory and need no request; every other chunk has one exact length.
    internal static int ChunkCount(IReadOnlyList<SharedWorldFile> files) => checked((int)files.Sum(file =>
        (file.Length + SharedWorldService.ChunkBytes - 1) / SharedWorldService.ChunkBytes));

    internal static (SharedWorldFile File, long Offset, int Length) Chunk(
        LiveSaveTestArtifact artifact, int index)
    {
        if (index < 0 || index >= artifact.ChunkCount) throw new InvalidDataException("Choose an exact test chunk.");
        foreach (var file in artifact.Version.Files)
        {
            var count = checked((int)((file.Length + artifact.ChunkBytes - 1) / artifact.ChunkBytes));
            if (index < count)
            {
                var offset = (long)index * artifact.ChunkBytes;
                return (file, offset, (int)Math.Min(artifact.ChunkBytes, file.Length - offset));
            }
            index -= count;
        }
        throw new InvalidDataException("The test chunk inventory changed.");
    }

    internal static bool ReceiptMatches(LiveSaveTestArtifact artifact, SharedWorldReceipt? receipt,
        SharedWorldRoster roster, DateTimeOffset now)
    {
        if (!SourceMatchesRoster(artifact, roster, now) || receipt is null ||
            receipt.GroupId != roster.GroupId || receipt.ProfileId != roster.ProfileId ||
            receipt.VersionHash != artifact.Version.VersionHash || receipt.RosterEpoch != roster.Epoch ||
            receipt.RosterRevision != roster.Revision || receipt.DeviceId == artifact.SourceDeviceId) return false;
        if (receipt.DeviceId == Guid.Empty)
            return artifact.SourceDeviceId != Guid.Empty && artifact.Version.SigningPublicKey != roster.OwnerPublicKey &&
                WorldPeerOwnerTestReceiptTrust.Verify(receipt, new(roster.OwnerPublicKey, roster.ProfileId, roster.GroupId));
        var member = roster.Members.SingleOrDefault(member => member.DeviceId == receipt.DeviceId);
        return member is { Revoked: false, Grants.Receive: true } &&
            (member.AccessExpiresUtc is null || now < member.AccessExpiresUtc) &&
            member.PublicKey != artifact.Version.SigningPublicKey &&
            SharedWorldReceiptTrust.Verify(receipt, member.PublicKey);
    }

    internal static bool AcceptedForScope(LiveSaveAcceptanceRecord record, LiveSaveAcceptanceScope actual,
        SharedWorldRoster roster, DateTimeOffset now) => record.State == "Accepted" && record.AllStages &&
        record.ProfileId == actual.ProfileId && record.OperationId == record.Artifact?.OperationId &&
        SharedWorldLiveSaveAdapters.SameRealInstallation(record.Scope, actual) && record.Artifact is { } artifact &&
        SharedWorldLiveSaveAdapters.SameRealInstallation(artifact.Scope, actual) && artifact.TestId == record.TestId &&
        ReceiptMatches(artifact, record.Receipt, roster, now);

    internal static bool RunMatchesProfile(ServerProfile profile, ManagedRun run,
        string executable, IReadOnlyList<GamePort> ports)
    {
        static bool SamePath(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
            Path.IsPathFullyQualified(a) && Path.IsPathFullyQualified(b) &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        return SharedWorldLiveSaveAdapters.ForGame(profile.Kind) is not null &&
            run.ProfileId == profile.Id && run.Kind == profile.Kind && run.WorldId == profile.WorldId &&
            run.OperationId != Guid.Empty && run.ProcessId is > 0 && run.StartTimeUtcTicks is > 0 &&
            run.StopRequestedUtc is null && run.GamePort == profile.GamePort &&
            SamePath(run.WorldDirectory, profile.WorldDirectory) && SamePath(run.ExecutablePath, executable) &&
            ports.Count > 0 && run.DeclaredPorts.SequenceEqual(ports) &&
            (profile.Kind != GameKinds.MinecraftJava || SamePath(run.ServerArtifactPath, profile.Minecraft?.ServerJarPath));
    }

    internal static LiveSaveAcceptanceRecord Confirm(LiveSaveAcceptanceRecord record,
        LiveSaveTestConfirmation confirmation, DateTimeOffset now)
    {
        if (record.State is not ("ArtifactReady" or "Accepted") || record.Artifact is not { } artifact ||
            !VerifyArtifact(artifact) || confirmation.Confirmed != true ||
            confirmation.VersionHash != artifact.Version.VersionHash ||
            confirmation.PayloadSha256 != artifact.PayloadSha256 || record.Receipt is null || record.TransferUtc is null)
            throw new InvalidDataException("First verify a separate-device transfer of this exact private test.");
        var changed = confirmation.Stage switch
        {
            "load" => record with { LoadUtc = record.LoadUtc ?? now },
            "change" when record.LoadUtc is not null => record with { ChangeUtc = record.ChangeUtc ?? now },
            "restart" when record.ChangeUtc is not null => record with { RestartUtc = record.RestartUtc ?? now },
            _ => throw new InvalidDataException("Confirm load, recognizable change, then restart for this exact test.")
        };
        return changed.AllStages ? changed with { State = "Accepted", Code = "LiveSaveInstallationAccepted" } : changed;
    }

    internal static IReadOnlyList<LiveSaveAcceptanceStage> Stages(LiveSaveAcceptanceRecord? record) =>
    [
        new("completion", record?.Artifact is null ? "Unverified" : "Passed", "Fixed adapter completion from the exact managed run.", "ObservedExactRun", record?.Artifact?.CompletionUtc),
        new("snapshot", record?.Artifact is null ? "Unverified" : "Passed", "Protected sealed immutable snapshot; no mutable-world scan by the test publisher.", "VerifiedImmutableSnapshot", record?.Artifact?.SnapshotUtc),
        new("transfer", record?.Receipt is null ? "Unverified" : "Passed", "Normal signed exact-copy receipt from another currently authorized device.", "SignedSeparateDeviceReceipt", record?.TransferUtc),
        new("load", record?.LoadUtc is null ? "Unverified" : "Passed", "Owner reports loading this exact private copy in the actual game on the receiving PC.", "OwnerReported", record?.LoadUtc),
        new("change", record?.ChangeUtc is null ? "Unverified" : "Passed", "Owner reports seeing the recognizable change made before this running save.", "OwnerReported", record?.ChangeUtc),
        new("restart", record?.RestartUtc is null ? "Unverified" : "Passed", "Owner reports graceful save/stop/restart of the disposable copy and the change surviving.", "OwnerReported", record?.RestartUtc)
    ];

    internal static bool ExpectedFailure(Exception ex) => ex is IOException or InvalidDataException or
        UnauthorizedAccessException or JsonException or CryptographicException or FormatException or
        ArgumentException or InvalidOperationException or OverflowException or NotSupportedException or NullReferenceException or
        System.ComponentModel.Win32Exception;
}

internal sealed class LiveSaveAcceptanceStore(LocalData data)
{
    private const string StateName = "live-save-installation-acceptance.protected";
    private const int MaximumStateBytes = 2 * 1024 * 1024;
    internal const int MaximumTests = 16;
    internal const long MaximumRetainedBytes = SharedWorldService.MaximumSharedWorldBytes;
    private sealed record State(int Schema, IReadOnlyList<LiveSaveAcceptanceRecord> Tests);
    // A single test's per-chunk digests are bounded by its signed 64-GiB limit
    // (about 8 MiB of digest bytes). Do not rehash a gigabyte file for each
    // 256-KiB request. Every returned chunk still verifies against these digests.
    private string? chunkVersionHash;
    private readonly Dictionary<string, byte[][]> chunkDigests = new(StringComparer.Ordinal);

    // Open children relative to held directory handles. Each ancestor denies
    // deletion; the native helper rejects reparse points and hard-linked files.
    // No API input can become any part of these private generated directories.
    private sealed class DirectoryPins : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        internal SafeFileHandle Handle => handles[^1];
        internal DirectoryPins(string path, bool create = false)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var volume = Path.GetPathRoot(full)!;
                var nativeVolume = volume.StartsWith(@"\\?\", StringComparison.Ordinal) ? volume :
                    volume.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + volume[2..] : @"\\?\" + volume;
                var first = OpenDirectoryRoot(nativeVolume, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                handles.Add(first);
                if (first.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var attributes = File.GetAttributes(first);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                    throw new InvalidDataException();
                foreach (var component in full[volume.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                    handles.Add(create ? NativeSnapshotDirectory.CreateDirectory(Handle, component, namespaceWrites: true) :
                        NativeSnapshotDirectory.OpenDirectory(Handle, component, namespaceWrites: true));
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); handles.Clear(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle OpenDirectoryRoot(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    internal List<LiveSaveAcceptanceRecord> Read()
    {
        var existed = data.HasProtected(StateName);
        var path = Path.Combine(data.RootPath, StateName);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, data.RootPath);
        if (existed && ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length > MaximumStateBytes * 2L + 8192))
            throw new InvalidDataException("The protected acceptance journal is linked or oversized.");
        var bytes = data.LoadProtected(StateName);
        if (bytes is null)
        {
            if (existed || data.Recovery.Notices.Any(notice => notice.StateFile == StateName)) throw new InvalidDataException();
            return [];
        }
        if (bytes.Length > MaximumStateBytes) throw new InvalidDataException();
        var state = JsonSerializer.Deserialize<State>(bytes, LiveSaveAcceptanceTrust.Json);
        if (state is null || state.Schema != 1 || state.Tests is null || state.Tests.Count > MaximumTests ||
            state.Tests.Any(record => record is null || record.TestId == Guid.Empty || record.ProfileId == Guid.Empty ||
                record.OperationId == Guid.Empty || record.Scope.ProfileId != record.ProfileId || record.RequestedUtc == default ||
                !SharedWorldLiveSaveAdapters.SameRealInstallation(record.Scope, record.Scope) ||
                record.State is not ("Capturing" or "ArtifactReady" or "Accepted" or "Failed" or "Interrupted") ||
                record.Code is not { Length: > 0 and <= 100 } ||
                record.Artifact is { } artifact && (!LiveSaveAcceptanceTrust.VerifyArtifact(artifact) ||
                    artifact.TestId != record.TestId || artifact.Scope != record.Scope || artifact.OperationId != record.OperationId) ||
                record.Artifact is null && (record.Receipt is not null || record.TransferUtc is not null || record.LoadUtc is not null ||
                    record.ChangeUtc is not null || record.RestartUtc is not null || record.State is "ArtifactReady" or "Accepted") ||
                (record.Receipt is null) != (record.TransferUtc is null) ||
                record.LoadUtc is not null && record.TransferUtc is null || record.ChangeUtc is not null && record.LoadUtc is null ||
                record.RestartUtc is not null && record.ChangeUtc is null || record.State == "Accepted" && !record.AllStages) ||
            state.Tests.Select(record => record.TestId).Distinct().Count() != state.Tests.Count)
            throw new InvalidDataException("Private live-save acceptance records need review.");
        return state.Tests.ToList();
    }

    internal void Save(IReadOnlyList<LiveSaveAcceptanceRecord> records)
    {
        if (records.Count > MaximumTests) throw new InvalidDataException("Review retained acceptance tests before another test.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(1, records), LiveSaveAcceptanceTrust.Json);
        if (bytes.Length > MaximumStateBytes) throw new InvalidDataException("Private acceptance records are full.");
        data.SaveProtected(StateName, bytes);
    }

    internal string Root(Guid profileId, Guid testId)
    {
        if (profileId == Guid.Empty || testId == Guid.Empty) throw new InvalidDataException();
        var path = Path.Combine(data.RootPath, "live-save-acceptance-tests", profileId.ToString("N"), testId.ToString("N"));
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
        return path;
    }

    internal void Stage(LiveSaveTestArtifact artifact, string sealedPayload,
        Action verifySealAndRun, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!LiveSaveAcceptanceTrust.VerifyArtifact(artifact)) throw new InvalidDataException();
        var descriptor = JsonSerializer.SerializeToUtf8Bytes(artifact, LiveSaveAcceptanceTrust.Json);
        if (descriptor.Length > SharedWorldService.MaximumManifestBytes) throw new InvalidDataException();
        var root = Root(artifact.Scope.ProfileId, artifact.TestId);
        var partial = root + ".partial";
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, partial);
        if (Directory.Exists(root) || File.Exists(root) || Directory.Exists(partial) || File.Exists(partial))
            throw new InvalidDataException("This exact test already has retained private bytes.");
        RequireCapacity(checked(artifact.PayloadBytes + descriptor.Length), token);
        verifySealAndRun();
        using var parent = new DirectoryPins(Path.GetDirectoryName(root)!, create: true);
        using var guard = new FileStream(NativeSnapshotDirectory.OpenGuard(parent.Handle, NativeSnapshotDirectory.GuardName), FileAccess.ReadWrite);
        if (guard.Length != 0) throw new InvalidDataException("The private test namespace guard changed.");
        using var staging = new DirectoryPins(partial, create: true);
        using (var payloadDirectory = new DirectoryPins(Path.Combine(partial, "payload"), create: true)) { }
        // A failed or interrupted copy remains private under its exact test ID.
        // It is never advertised or promoted, and owner review retains its bytes.
        foreach (var file in artifact.Version.Files)
        {
            token.ThrowIfCancellationRequested();
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, partial);
            var source = SharedWorldService.SafeChild(sealedPayload, file.Path);
            var destination = SharedWorldService.SafeChild(Path.Combine(partial, "payload"), file.Path);
            using var sourceParent = new DirectoryPins(Path.GetDirectoryName(source)!);
            using var destinationParent = new DirectoryPins(Path.GetDirectoryName(destination)!, create: true);
            using var input = new FileStream(NativeSnapshotDirectory.OpenReadFile(sourceParent.Handle, Path.GetFileName(source)), FileAccess.Read, 64 * 1024);
            using var output = new FileStream(NativeSnapshotDirectory.CreateNewFile(destinationParent.Handle, Path.GetFileName(destination)), FileAccess.Write, 64 * 1024);
            if (input.Length != file.Length || (File.GetAttributes(input.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024]; long copied = 0; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                copied = checked(copied + count);
                if (copied > file.Length) throw new InvalidDataException();
                hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count);
            }
            if (copied != file.Length || Convert.ToHexString(hash.GetHashAndReset()) != file.Sha256)
                throw new InvalidDataException("A sealed private test file changed during copy.");
            output.Flush(true);
            File.SetAttributes(output.SafeFileHandle, File.GetAttributes(output.SafeFileHandle) | FileAttributes.ReadOnly);
        }
        verifySealAndRun();
        VerifyPayload(partial, artifact, token);
        using (var marker = new FileStream(NativeSnapshotDirectory.CreateNewFile(staging.Handle, "artifact.json"), FileAccess.Write))
        { marker.Write(descriptor); marker.Flush(true); }
        staging.Dispose(); // Release only the finished subtree for atomic rename.
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        Directory.Move(partial, root);
        _ = ReadArtifact(artifact, token);
        verifySealAndRun();
    }

    private void RequireCapacity(long incoming, CancellationToken token)
    {
        var root = Path.Combine(data.RootPath, "live-save-acceptance-tests");
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        long retained = 0; var directories = 0; var files = 0;
        var pending = new Stack<string>();
        if (Directory.Exists(root)) pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if (++directories > 16384) throw new InvalidDataException();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                if (++files > MaximumTests * (SharedWorldService.MaximumFiles + 2)) throw new InvalidDataException();
                retained = checked(retained + new FileInfo(path).Length);
                if (retained > MaximumRetainedBytes) throw new InvalidDataException();
            }
        }
        if (incoming < 0 || incoming > MaximumRetainedBytes - retained ||
            new DriveInfo(Path.GetPathRoot(data.RootPath)!).AvailableFreeSpace < incoming + 1024L * 1024 * 1024)
            throw new IOException("Private acceptance copies reached their storage limit; keep retained copies for review.");
    }

    internal LiveSaveTestArtifact ReadArtifact(LiveSaveTestArtifact expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!LiveSaveAcceptanceTrust.VerifyArtifact(expected)) throw new InvalidDataException();
        var root = Root(expected.Scope.ProfileId, expected.TestId);
        var path = SharedWorldService.SafeChild(root, "artifact.json");
        using var parent = new DirectoryPins(root);
        using var stream = new FileStream(NativeSnapshotDirectory.OpenReadFile(parent.Handle, Path.GetFileName(path)), FileAccess.Read);
        if (stream.Length is < 1 or > SharedWorldService.MaximumManifestBytes ||
            (File.GetAttributes(stream.SafeFileHandle) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        var artifact = JsonSerializer.Deserialize<LiveSaveTestArtifact>(bytes.ToArray(), LiveSaveAcceptanceTrust.Json);
        if (artifact is null || !LiveSaveAcceptanceTrust.VerifyArtifact(artifact) ||
            !LiveSaveAcceptanceTrust.ArtifactBasis(expected).AsSpan().SequenceEqual(LiveSaveAcceptanceTrust.ArtifactBasis(artifact)) ||
            artifact.Signature != expected.Signature) throw new InvalidDataException();
        VerifyPayload(root, artifact, token);
        return artifact;
    }

    private void VerifyPayload(string root, LiveSaveTestArtifact artifact, CancellationToken token)
    {
        var payload = Path.Combine(root, "payload");
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, payload);
        using var payloadPin = new DirectoryPins(payload);
        foreach (var file in artifact.Version.Files) VerifyFile(payload, file, token);
        // Reject unexpected files as well as changed, missing or linked files.
        var pending = new Stack<string>(); pending.Push(payload); var directories = 0; var count = 0;
        var expected = artifact.Version.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        while (pending.TryPop(out var directory))
        {
            if (++directories > 4096) throw new InvalidDataException();
            using var directoryPin = new DirectoryPins(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                if (!expected.Remove(Path.GetRelativePath(payload, path).Replace('\\', '/')) || ++count > artifact.Version.Files.Count)
                    throw new InvalidDataException();
            }
        }
        if (expected.Count != 0) throw new InvalidDataException();
    }

    private static void VerifyFile(string payload, SharedWorldFile file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = SharedWorldService.SafeChild(payload, file.Path);
        using var parent = new DirectoryPins(Path.GetDirectoryName(path)!);
        using var input = new FileStream(NativeSnapshotDirectory.OpenReadFile(parent.Handle, Path.GetFileName(path)), FileAccess.Read);
        if (input.Length != file.Length || (File.GetAttributes(input.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; int count;
        while ((count = input.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        if (Convert.ToHexString(hash.GetHashAndReset()) != file.Sha256) throw new InvalidDataException();
    }

    internal byte[] ReadChunk(LiveSaveTestArtifact artifact, int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var (file, offset, length) = LiveSaveAcceptanceTrust.Chunk(artifact, index);
        var payload = Path.Combine(Root(artifact.Scope.ProfileId, artifact.TestId), "payload");
        // Hold the same handle while hashing and reading, denying write/delete.
        var path = SharedWorldService.SafeChild(payload, file.Path);
        using var parent = new DirectoryPins(Path.GetDirectoryName(path)!);
        using var input = new FileStream(NativeSnapshotDirectory.OpenReadFile(parent.Handle, Path.GetFileName(path)), FileAccess.Read);
        if (input.Length != file.Length || (File.GetAttributes(input.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException();
        if (chunkVersionHash != artifact.Version.VersionHash)
        { chunkDigests.Clear(); chunkVersionHash = artifact.Version.VersionHash; }
        if (!chunkDigests.TryGetValue(file.Path, out var digests))
        {
            using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var hashes = new List<byte[]>(); var buffer = new byte[artifact.ChunkBytes]; int count;
            while ((count = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
            {
                token.ThrowIfCancellationRequested(); whole.AppendData(buffer, 0, count);
                hashes.Add(SHA256.HashData(buffer.AsSpan(0, count)));
            }
            if (Convert.ToHexString(whole.GetHashAndReset()) != file.Sha256) throw new InvalidDataException();
            digests = hashes.ToArray(); chunkDigests.Add(file.Path, digests);
        }
        input.Position = offset; var bytes = new byte[length]; input.ReadExactly(bytes);
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(digests[checked((int)(offset / artifact.ChunkBytes))]))
            throw new InvalidDataException("The exact private test chunk changed during transfer.");
        return bytes;
    }
}
