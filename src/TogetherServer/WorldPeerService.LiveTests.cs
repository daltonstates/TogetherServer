using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record ReceivedLiveSaveTestView(Guid TestId, Guid ProfileId, string VersionHash,
    string PayloadDirectory, string Message);

internal sealed partial class WorldPeerService
{
    private readonly Dictionary<string, LiveSaveTestArtifact> servedLiveTests = new(StringComparer.Ordinal);

    private bool AuthorizedLiveTest(Group group, WorldPeerSaveDescriptor descriptor)
    {
        var artifact = descriptor.TestArtifact;
        if (!descriptor.TestOnly || descriptor.Branch is not null || descriptor.SessionBranchHash is not null ||
            artifact is null || !LiveSaveAcceptanceTrust.SourceMatchesRoster(artifact, group.Roster, DateTimeOffset.UtcNow) ||
            artifact.Version.VersionHash != descriptor.Version.VersionHash ||
            artifact.Version.Signature != descriptor.Version.Signature ||
            !SharedWorldService.VerifyAcceptanceTestVersion(descriptor.Version) ||
            artifact.Version.ProfileId != group.Root.ProfileId || artifact.Version.GroupId != group.Root.GroupId)
            return false;
        var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
        if (artifact.Branch is { } branch)
            return BranchLiveTestHasTrustedRoot(artifact, group.Root, records) &&
                Access(group, artifact.SourceDeviceId, branch.CandidatePublicKey) is { Receive: true, EligibleHost: true };
        return artifact.Scope.ProfileId == group.Root.ProfileId &&
            (artifact.Version.SigningPublicKey == group.Root.OwnerPublicKey && artifact.SourceDeviceId == Guid.Empty ||
             records.Any(record =>
                record.Roster.OwnerPublicKey == group.Root.OwnerPublicKey && record.Proposal.GroupId == group.Root.GroupId &&
                record.Proposal.CandidatePublicKey == artifact.Version.SigningPublicKey &&
                (record.Proposal.SuccessorBinding?.DeviceId ?? record.SuccessorReceipt?.DeviceId ??
                    record.Roster.Members.SingleOrDefault(member => member.PublicKey == record.Proposal.CandidatePublicKey)?.DeviceId)
                    == artifact.SourceDeviceId && WorldAuthorityTrust.Verify(record) &&
                group.Roster.Members.SingleOrDefault(member => member.DeviceId == artifact.SourceDeviceId) is { } source &&
                Access(group, source.DeviceId, source.PublicKey) is { Receive: true, EligibleHost: true }));
    }

    internal static bool BranchLiveTestHasTrustedRoot(LiveSaveTestArtifact artifact,
        SharedWorldPeerRoot root, IReadOnlyList<WorldAuthorityRecord> records)
    {
        if (!SharedWorldPeerTrust.ValidRoot(root) || !LiveSaveAcceptanceTrust.VerifyArtifact(artifact) ||
            artifact.Branch is not { } branch || branch.Offer.Roster.OwnerPublicKey != root.OwnerPublicKey ||
            artifact.Version.ProfileId != root.ProfileId || artifact.Version.GroupId != root.GroupId) return false;
        while (true)
        {
            if (branch.Offer.Roster.ProfileId != root.ProfileId || branch.Offer.Roster.GroupId != root.GroupId ||
                branch.Offer.Roster.OwnerPublicKey != root.OwnerPublicKey ||
                branch.Offer.Version.ProfileId != root.ProfileId || branch.Offer.Version.GroupId != root.GroupId)
                return false;
            if (branch.SourceBranch is not { } parent) break;
            branch = parent;
        }
        // Reuse the playable-source trust boundary. A signed recovery envelope
        // cannot promote an arbitrary self-signed base into this pinned world.
        return WorldPeerFileTransfer.TrustedHistoricSigner(root, branch.Offer.Version, records);
    }

    internal static IReadOnlyList<Guid> LocalLiveTestProfileIds(SharedWorldPeerRoot root,
        IEnumerable<ServerProfile> profiles) => profiles.Where(profile =>
            !LiveSaveAcceptanceTrust.IsRecoveryProfile(profile) ? profile.Id == root.ProfileId :
            profile.SeparateCopySourceProfileId == root.ProfileId &&
            profile.SeparateCopyBranchHash is { } hash && SharedWorldPeerTrust.ValidHash(hash) &&
            profile.Id == SharedWorldBranchLiveSource.LocalProfileId(hash)).Select(profile => profile.Id).Distinct().ToArray();

    private async Task<IReadOnlyList<WorldPeerSaveDescriptor>> LocalLiveTestDescriptorsAsync(Group group,
        CancellationToken cancellationToken)
    {
        using var key = LocalKey(group);
        if (!group.SharingEnabled || key is null ||
            Access(group, group.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))?.Receive != true) return [];
        var descriptors = new List<WorldPeerSaveDescriptor>();
        foreach (var profileId in LocalLiveTestProfileIds(group.Root, data.LoadSettings().Profiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = await manager.LiveSaveAcceptanceAsync(profileId, cancellationToken);
            descriptors.AddRange(view.Tests.Where(test => test.Artifact is not null && test.State is "ArtifactReady" or "Accepted")
                .Select(test => new WorldPeerSaveDescriptor(test.Artifact!.Version, TestOnly: true, TestArtifact: test.Artifact))
                .Where(descriptor => AuthorizedLiveTest(group, descriptor)).Take(16 - descriptors.Count));
            if (descriptors.Count == 16) break;
        }
        lock (stateSync)
        {
            foreach (var descriptor in descriptors)
            {
                if (servedLiveTests.Count >= 128 && !servedLiveTests.ContainsKey(descriptor.Version.VersionHash)) break;
                servedLiveTests[descriptor.Version.VersionHash] = descriptor.TestArtifact!;
            }
        }
        return descriptors;
    }

    private LiveSaveTestArtifact? KnownLiveTest(Group group, string versionHash)
    {
        lock (stateSync)
            return servedLiveTests.TryGetValue(versionHash, out var artifact) &&
                AuthorizedLiveTest(group, new(artifact.Version, TestOnly: true, TestArtifact: artifact)) ? artifact : null;
    }

    private Task<SharedWorldVersion?> FindLiveTestManifestAsync(Group group, string versionHash,
        CancellationToken cancellationToken) => Task.FromResult(KnownLiveTest(group, versionHash)?.Version);

    private async Task<byte[]?> ReadLiveTestChunkAsync(Group group, string versionHash,
        int fileIndex, long offset, CancellationToken cancellationToken)
    {
        var artifact = KnownLiveTest(group, versionHash);
        if (artifact is null || !await MayServeSaveAsync(group, true, cancellationToken) || fileIndex < 0 ||
            fileIndex >= artifact.Version.Files.Count || offset < 0 || offset % artifact.ChunkBytes != 0 ||
            offset >= artifact.Version.Files[fileIndex].Length) return null;
        var index = checked((int)(artifact.Version.Files.Take(fileIndex).Sum(file =>
            (file.Length + artifact.ChunkBytes - 1) / artifact.ChunkBytes) + offset / artifact.ChunkBytes));
        var result = await manager.LiveSaveTestChunkAsync(artifact.Scope.ProfileId, artifact.TestId, index, cancellationToken);
        return KnownLiveTest(group, versionHash) is not null && await MayServeSaveAsync(group, true, cancellationToken) ? result : null;
    }

    private async Task<WorldPeerActionResult?> TryAcceptLiveTestReceiptAsync(Group group,
        SharedWorldReceipt receipt, CancellationToken cancellationToken)
    {
        var artifact = KnownLiveTest(group, receipt.VersionHash);
        if (artifact is null) return null;
        var result = await manager.RegisterLiveSaveTestReceiptAsync(artifact.Scope.ProfileId,
            artifact.TestId, receipt, cancellationToken);
        return new(result.Ok, result.Code, result.Message);
    }

    private string ReceivedLiveTestRoot(Group group, Guid testId) => Path.Combine(data.RootPath,
        "received-live-save-tests", group.Root.GroupKey, testId.ToString("N"));

    internal IReadOnlyList<ReceivedLiveSaveTestView> ReceivedLiveTests(string groupKey,
        CancellationToken cancellationToken = default)
    {
        var group = Find(groupKey);
        if (group is null) return [];
        var parent = Path.Combine(data.RootPath, "received-live-save-tests", groupKey);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, parent);
        if (!Directory.Exists(parent)) return [];
        var result = new List<ReceivedLiveSaveTestView>();
        foreach (var path in Directory.EnumerateDirectories(parent).Take(128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(path), "N", out var testId)) continue;
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
                var descriptor = Path.Combine(path, "artifact.json");
                if (!File.Exists(descriptor) || new FileInfo(descriptor).Length > SharedWorldService.MaximumManifestBytes ||
                    (File.GetAttributes(descriptor) & FileAttributes.ReparsePoint) != 0) continue;
                var artifact = WorldPeerProtocol.DecodeEnvelope<LiveSaveTestArtifact>(File.ReadAllBytes(descriptor),
                    checked((int)SharedWorldService.MaximumManifestBytes));
                if (artifact.TestId != testId || !AuthorizedLiveTest(group,
                    new(artifact.Version, TestOnly: true, TestArtifact: artifact))) continue;
                if (new WorldPeerReceivedTestJournal(data, group.Root).Read(artifact) is null) continue;
                using var verified = WorldPeerVerifiedTestCopy.Open(data.RootPath, path, artifact, cancellationToken);
                result.Add(new(testId, group.Root.ProfileId, artifact.Version.VersionHash,
                    Path.Combine(path, "payload"), "Private acceptance copy. Use a disposable game setup to check load, the recognizable change, and graceful restart; then report the exact test to its Host."));
                if (result.Count == 32) break;
            }
            catch (Exception error) when (LiveSaveAcceptanceTrust.ExpectedFailure(error))
            { /* Retained damaged or uncommitted evidence stays private for review. */ }
        }
        return result;
    }

    private async Task PullLiveTestAsync(Group group, SharedWorldPeerRoute route,
        WorldPeerSaveDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!AuthorizedLiveTest(group, descriptor) || !await ReceiveAllowedAsync(group, cancellationToken)) return;
        var artifact = descriptor.TestArtifact!;
        var root = ReceivedLiveTestRoot(group, artifact.TestId);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        await FriendLink.ReceiveGateFor(root).WaitAsync(cancellationToken);
        try
        {
            var partial = root + ".partial";
            var retained = Directory.Exists(root);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, partial);
            if (!retained && !SharedWorldReadiness.HasSpaceForCopies(partial, artifact.PayloadBytes, 1))
                throw new IOException("The private test copy needs additional space, including 1 GiB kept free.");
            var parent = Path.GetDirectoryName(root)!;
            if (Directory.Exists(parent) && Directory.EnumerateDirectories(parent).Take(65).Count() >= 64 &&
                !retained && !Directory.Exists(partial)) throw new IOException("Private peer tests reached their retained copy capacity.");
            if (!retained)
            {
                using (var names = new WorldPeerTestNamespace(data.RootPath, partial, create: true))
                {
                    names.WriteArtifact(artifact);
                    long transferred = 0;
                    for (var index = 0; index < descriptor.Version.Files.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var file = descriptor.Version.Files[index];
                        using var output = names.Resume("payload/" + file.Path);
                        if (output.Length > file.Length) throw new InvalidDataException("The partial private test is inconsistent.");
                        if (output.Length < file.Length && output.Length % artifact.ChunkBytes != 0)
                            output.SetLength(output.Length - output.Length % artifact.ChunkBytes);
                        output.Position = output.Length;
                        while (output.Length < file.Length)
                        {
                            if (transferred >= WorldPeerFileTransfer.MaximumPayloadBytesPerPass ||
                                !await ReceiveAllowedAsync(group, cancellationToken)) return;
                            cancellationToken.ThrowIfCancellationRequested();
                            var bytes = await SendPeerAsync(group, route, WorldPeerOperations.Chunk, cancellationToken,
                                versionHash: artifact.Version.VersionHash, fileIndex: index, chunkOffset: output.Length);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!await ReceiveAllowedAsync(group, cancellationToken)) return;
                            if (bytes.Length != Math.Min(artifact.ChunkBytes, file.Length - output.Length))
                                throw new InvalidDataException("The private test chunk length is inconsistent.");
                            await output.WriteAsync(bytes, cancellationToken); transferred += bytes.Length;
                        }
                        await output.FlushAsync(cancellationToken);
                        output.Position = 0;
                        if (WorldPeerVerifiedTestCopy.HashFile(output, cancellationToken) != file.Sha256.ToUpperInvariant())
                        {
                            // Reset only this held disposable partial file. Never
                            // repair or overwrite a completed/uncommitted copy.
                            output.SetLength(0); output.Flush(true);
                            throw new InvalidDataException("The disposable private test partial failed its signed hash.");
                        }
                        output.Flush(true);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!await ReceiveAllowedAsync(group, cancellationToken)) return;
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
                Directory.Move(partial, root); // Still private: no protected completed marker exists.
            }
            using var verified = WorldPeerVerifiedTestCopy.Open(data.RootPath, root, artifact, cancellationToken);
            var journal = new WorldPeerReceivedTestJournal(data, group.Root);
            WorldPeerTestCompletion? completion = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ReceiveAllowedAsync(group, cancellationToken)) return;
            void Commit()
            {
                lock (stateSync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = Find(group.Root.GroupKey);
                    using var key = current is null ? null : LocalKey(current);
                    if (current is null || key is null || !current.ReceiveConsented ||
                        current.DeviceId != group.DeviceId || !AuthorizedLiveTest(current, descriptor) ||
                        Access(current, current.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))?.Receive != true)
                        throw new InvalidDataException("Private test receive permission changed.");
                    var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
                    var prior = journal.Read(artifact);
                    if (prior is not null && prior.ReceiverDeviceId == current.DeviceId &&
                        prior.ReceiverPublicKey == publicKey && prior.RosterEpoch == current.Roster.Epoch &&
                        prior.RosterRevision == current.Roster.Revision)
                        completion = prior;
                    else
                    {
                        var draft = new SharedWorldReceipt(current.DeviceId == Guid.Empty ? 2 : 1,
                            current.Root.GroupId, current.Root.ProfileId, artifact.Version.VersionHash,
                            current.DeviceId, current.Roster.Epoch, current.Roster.Revision, Guid.NewGuid(), "");
                        var receipt = draft with { Signature = Convert.ToBase64String(key.SignData(
                            SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256)) };
                        completion = new(1, current.Root, artifact.TestId, artifact.Version.VersionHash,
                            verified.ArtifactHash, current.DeviceId, publicKey, current.Roster.Epoch,
                            current.Roster.Revision, receipt);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    journal.Save(artifact, completion!, verified);
                }
            }
            if (group.DeviceId == Guid.Empty) Commit();
            else if (!await friend.CommitPeerReceiveAsync(group.Root, group.DeviceId, Commit, cancellationToken)) return;
            if (completion?.Receipt is not { } receiptToSend || completion.ReceiptRegistered ||
                !await ReceiveAllowedAsync(group, cancellationToken)) return;
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await SendPeerAsync(group, route, WorldPeerOperations.Receipt, cancellationToken,
                JsonSerializer.SerializeToUtf8Bytes(receiptToSend, Json), versionHash: receiptToSend.VersionHash);
            var accepted = WorldPeerProtocol.DecodeEnvelope<WorldPeerActionResult>(reply,
                WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Receipt));
            if (!accepted.Ok || accepted.Code is not ("LiveSaveTestTransferVerified" or "LiveSaveTestAlreadyConfirmed" or "LiveSaveTestAdditionalCopyVerified"))
                throw new InvalidDataException("The source did not register the exact private test receipt in its acceptance workflow.");
            lock (stateSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prior = journal.Read(artifact);
                if (prior?.Receipt == receiptToSend) journal.Save(artifact, prior with { ReceiptRegistered = true }, verified);
            }
        }
        catch (System.ComponentModel.Win32Exception error)
        { throw new IOException("The private test namespace could not be held safely.", error); }
        finally { FriendLink.ReceiveGateFor(root).Release(); }
    }
}
