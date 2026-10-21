using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record WorldPeerLatestSourceResult(bool Ok, string Code, string Message,
    WorldPeerRecoverySource? Source = null, string? ObservationHash = null);

internal sealed partial class WorldPeerService
{
    // A local Host instruction checks reachable members first. A member with
    // no incoming route can still send its signed history to an enabled peer;
    // unavailable peers never turn a known newer save into permission to start
    // an older copy. This does not select a Friend connection or group default.
    internal async Task<WorldPeerLatestSourceResult> ResolveLatestRecoverySourceAsync(
        SharedWorldPeerRoot root, CancellationToken cancellationToken)
    {
        try
        {
            await SyncWorldAsync(root, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                var result = await ResolveLatestRecoverySourceOnceAsync(root, cancellationToken);
                if (result.Ok || attempt == 3 || !await ExpectIncomingSeedAsync(root, cancellationToken)) return result;
                // A sender whose incoming listener is off may be between its
                // normal five-second outbound polls. One confirmation waits
                // up to fifteen seconds for that automatic transfer to arrive.
                await seedDelay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (Exception error) when (PeerFailure(error))
        { return new(false, "SharedSaveHistoryNeedsReview", "The current shared save or signed membership needs review. Existing saves were kept."); }
    }

    private async Task<bool> ExpectIncomingSeedAsync(SharedWorldPeerRoot root, CancellationToken cancellationToken)
    {
        if (!data.LoadSettings().CompanionListeningEnabled) return false;
        var group = Find(root.GroupKey);
        if (group is null || group.Root != root || !await RecoveryAllowedAsync(group, cancellationToken)) return false;
        var state = Store(group).Project();
        if (state.Peers.Any(peer => peer.HasConflict)) return false;
        var local = (await LocalSavesAsync(group, cancellationToken)).Select(item => item.Descriptor.Version.VersionHash)
            .ToHashSet(StringComparer.Ordinal);
        using var key = LocalKey(group);
        var publicKey = key is null ? null : Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return state.Peers.Any(peer => peer.Authorized && !peer.HasConflict && peer.PublicKey != publicKey &&
            peer.AvailableVersionHashes.Any(hash => !local.Contains(hash)));
    }

    private async Task<WorldPeerLatestSourceResult> ResolveLatestRecoverySourceOnceAsync(
        SharedWorldPeerRoot root, CancellationToken cancellationToken)
    {
        try
        {
            await refreshGate.WaitAsync(cancellationToken);
            try
            {
                var group = Find(root.GroupKey);
                if (group is null || group.Root != root)
                    return new(false, "WorldMembershipRequired", "Reconnect to this shared world before hosting its save.");
                if (!await RecoveryAllowedAsync(group, cancellationToken))
                    return new(false, "HostingPermissionRequired", "This PC needs permission to receive and host this world's saves.");
                var observation = LatestObservationHash(root) ?? throw new InvalidDataException("The shared save observation changed.");
                var selected = await LatestLocalSaveAsync(group, cancellationToken);
                if (selected is null)
                    return new(false, "SharedSaveUnavailable", "No complete verified save is available on this PC yet. Keep the approved PCs online while the save copies.");
                var lane = selected.Descriptor.Branch?.BranchHash ?? Store(group).Project().Branches
                    .FirstOrDefault(item => item.Branch.HeadVersionHash == selected.Descriptor.Version.VersionHash)?.Branch.BranchHash ??
                    SessionBranchHash(group, selected.Descriptor);
                // An explicit exact selection keeps its identity through the
                // immutable retention and the final permission check.
                var source = await ResolveRecoverySourceAsync(root.GroupKey, lane, cancellationToken,
                    selected.Descriptor.Version.VersionHash);
                var current = Find(root.GroupKey) ?? throw new InvalidDataException("World membership changed.");
                if (LatestObservationHash(root) != observation)
                    return new(false, "LatestSaveChanged", "New shared save information arrived. Try Host locally again.");
                var latest = await LatestLocalSaveAsync(current, cancellationToken);
                if (latest?.Descriptor.Version.VersionHash != source.Descriptor.Version.VersionHash ||
                    LatestObservationHash(root) != observation)
                    return new(false, "LatestSaveChanged", "A newer save arrived while preparing. Try Host locally again.", null);
                return new(true, "LatestSharedSaveReady", "The newest verified continuation is ready.", source, observation);
            }
            finally { refreshGate.Release(); }
        }
        catch (Exception error) when (PeerFailure(error))
        {
            return new(false, "SharedSaveHistoryNeedsReview",
                "The newest known save is incomplete, changed, or has competing histories. Existing saves were kept; review sharing before hosting.");
        }
    }

    private static string SaveObservationName(SharedWorldPeerRoot root) => $"world-peer-save-observation-{root.GroupKey}.protected";

    // Peer journals, roster import, consent and completed peer promotion all
    // share stateSync. HostManager may compare this under the same mutation
    // lock immediately before its final process launch without calling back
    // into a manager, network or asynchronous operation.
    internal string? LatestObservationHash(SharedWorldPeerRoot root)
    {
        try
        {
            lock (stateSync)
            {
                var group = Find(root.GroupKey);
                if (group is null || group.Root != root) return null;
                var state = Store(group).Project();
                var received = ReadPeerPointer(ReceivedRoot(data, group));
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    root,
                    group.Roster.Signature,
                    group.DeviceId,
                    group.ReceiveConsented,
                    group.SharingEnabled,
                    Received = received?.VersionHash,
                    CompletedPeerSave = data.LoadProtected(SaveObservationName(root)),
                    Peers = state.Peers.OrderBy(peer => peer.DeviceId).ThenBy(peer => peer.PublicKey, StringComparer.Ordinal)
                        .Select(peer => new
                        {
                            peer.DeviceId,
                            peer.PublicKey,
                            peer.Revision,
                            peer.HasConflict,
                            peer.Authorized,
                            Copies = peer.AvailableVersionHashes.Order(StringComparer.Ordinal).ToArray()
                        }).ToArray(),
                    Branches = state.Branches.OrderBy(item => item.DeviceId).ThenBy(item => item.PublicKey, StringComparer.Ordinal)
                        .ThenBy(item => item.Branch.BranchHash, StringComparer.Ordinal).ThenBy(item => item.Branch.HeadVersionHash, StringComparer.Ordinal)
                        .Select(item => new { item.DeviceId, item.PublicKey, item.Branch, item.HasConflict, item.Authorized }).ToArray(),
                    Sessions = state.Sessions.OrderBy(item => item.DeviceId).ThenBy(item => item.PublicKey, StringComparer.Ordinal)
                        .ThenBy(item => item.Session.SessionId).ThenBy(item => item.Session.Revision)
                        .Select(item => new { item.DeviceId, item.PublicKey, item.Session, item.HasConflict, item.Authorized }).ToArray()
                }, Json);
                return Convert.ToHexString(SHA256.HashData(payload));
            }
        }
        catch (Exception error) when (PeerFailure(error)) { return null; }
    }

    private async Task<LocalSave?> LatestLocalSaveAsync(Group group, CancellationToken cancellationToken)
    {
        var state = Store(group).Project();
        if (state.Peers.Any(peer => peer.HasConflict))
            throw new InvalidDataException("Conflicting signed world information needs review.");
        var local = (await LocalSavesAsync(group, cancellationToken)).Where(item =>
            !item.Descriptor.TestOnly && item.Descriptor.TestArtifact is null && AuthorizedDescriptor(group, item.Descriptor)).ToArray();
        if (local.Length == 0)
        {
            if (state.Branches.Count > 0 || state.Sessions.Any(item => item.Session.HeadVersionHash is not null))
                throw new InvalidDataException("A known signed save has no complete local payload.");
            return null;
        }
        var candidates = local.Select(item => item.Descriptor).ToArray();
        var known = state.Branches.Select(item => item.Branch.HeadVersionHash)
            .Concat(state.Sessions.Select(item => item.Session.HeadVersionHash).OfType<string>()).Distinct(StringComparer.Ordinal).ToArray();
        var chosen = await WorldPeerLatestSelection.SelectAsync(group.Root, candidates, known,
            async (index, token) =>
            {
                var item = local[index];
                using var verified = WorldPeerVerifiedCopy.Open(data.RootPath, RecoverySourceDirectory(item),
                    item.Descriptor.Version, token);
                var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
                var anchor = item.Descriptor.Branch?.Offer.Version ??
                    WorldPeerFileTransfer.TrustedHistoricAnchor(group.Root, item.Descriptor.Version, records);
                if (item.Vault is not null) WorldPeerFileTransfer.VerifyHistory(item.Vault, item.Descriptor, anchor);
                else
                {
                    // Published reads hold their own immutable manifest checks;
                    // the ancestry walker below still checks each exact parent.
                    if (!WorldPeerFileTransfer.TrustedHistoricSigner(group.Root, item.Descriptor.Version, records))
                        throw new InvalidDataException("The published source signer changed.");
                }
            }, async (hash, number, token) => await FindManifestAsync(group, hash, number, token), cancellationToken);
        return local[chosen];
    }

    // A cheap final lifecycle fence, safe while HostManager already owns its
    // gate. Discovery and immutable source preparation happen before this call.
    // A remote Start cannot revive the old original world after a known peer
    // continuation progressed. The owner can use Host locally to prepare it.
    internal WorldPeerActionResult? CanonicalStartBlock(SharedWorldPeerRoot root, string? originalPublishedHash)
    {
        try
        {
            lock (stateSync)
            {
                var group = Find(root.GroupKey);
                if (group is null) return null;
                if (group.Root != root) throw new InvalidDataException("The world membership changed.");
                var state = Store(group).Project();
                if (state.Peers.Any(peer => peer.HasConflict))
                    return new(false, "SharedSaveHistoryNeedsReview", "Competing save histories need review before this world can start.");
                if (state.Branches.Any(item => item.Branch.BranchHash != NormalSessionBranchHash(group) &&
                    item.Branch.HeadVersionHash != originalPublishedHash &&
                    item.Branch.HeadVersionHash != item.Branch.BaseVersionHash))
                    return new(false, "NewerSharedSaveAvailable", "A peer continued this world. Use Host locally to prepare its latest verified save.");
                return null;
            }
        }
        catch (Exception error) when (PeerFailure(error))
        { return new(false, "SharedSaveHistoryNeedsReview", "The saved world history needs review before starting."); }
    }
}

// Compare verified parent identities across hosting lanes. Save numbers and
// timestamps cannot choose between forks, and a returning owner has no priority.
internal static class WorldPeerLatestSelection
{
    internal static async Task<int> SelectAsync(SharedWorldPeerRoot root,
        IReadOnlyList<WorldPeerSaveDescriptor> candidates, IReadOnlyList<string> knownHeads,
        Func<int, CancellationToken, Task> verifyPayload,
        Func<string, long, CancellationToken, Task<SharedWorldVersion?>> readManifest,
        CancellationToken cancellationToken)
    {
        if (!SharedWorldPeerTrust.ValidRoot(root) || candidates.Count is < 1 or > SharedWorldPeerStore.MaximumKnownBranches ||
            knownHeads.Any(hash => !SharedWorldPeerTrust.ValidHash(hash)))
            throw new InvalidDataException("The available histories crossed their world or capacity.");
        var required = candidates.Select(item => item.Version.VersionHash).Concat(knownHeads).ToHashSet(StringComparer.Ordinal);
        var winners = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = candidates[index];
            if (descriptor.TestOnly || descriptor.TestArtifact is not null) continue;
            try
            {
                await verifyPayload(index, cancellationToken);
                var ancestry = await AncestorsAsync(root, descriptor, readManifest, cancellationToken);
                if (required.IsSubsetOf(ancestry)) winners.TryAdd(descriptor.Version.VersionHash, index);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or
                UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or
                System.Text.Json.JsonException or ArgumentException)
            { /* Changed or incomplete copies never authorize an older fallback. */ }
        }
        if (winners.Count != 1)
            throw new InvalidDataException("The newest history is incomplete or divergent. Keep all copies for review.");
        return winners.Values.Single();
    }

    private static async Task<HashSet<string>> AncestorsAsync(SharedWorldPeerRoot root,
        WorldPeerSaveDescriptor descriptor,
        Func<string, long, CancellationToken, Task<SharedWorldVersion?>> readManifest,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var head = descriptor.Version;
        var branch = descriptor.Branch;
        if (branch is not null && (!SharedWorldSeparateCopyStore.Verify(branch) ||
            branch.Offer.Roster.OwnerPublicKey != root.OwnerPublicKey))
            throw new InvalidDataException("The continuation's source proof changed.");
        var current = head;
        var segmentKey = current.SigningPublicKey;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SharedWorldService.VerifySignature(current) || current.ProfileId != root.ProfileId ||
                current.GroupId != root.GroupId || current.WorldId != head.WorldId || current.Game != head.Game ||
                current.CaptureKind == LiveSaveAcceptanceTrust.TestCaptureKind)
                throw new InvalidDataException("The signed save ancestry crossed a world.");
            if (branch is not null)
            {
                if (!SharedWorldSeparateCopyStore.VerifySnapshotVersion(branch, current))
                    throw new InvalidDataException("The continuation ancestry has an unauthorized signer.");
                if (current.VersionHash == branch.Offer.Version.VersionHash)
                {
                    // The signed offer authorizes this exact transition back
                    // into its source history, including repeated recoveries.
                    branch = branch.SourceBranch;
                    segmentKey = current.SigningPublicKey;
                    continue;
                }
            }
            else if (current.SigningPublicKey != segmentKey)
                throw new InvalidDataException("The save ancestry changed signer without a continuation proof.");
            if (!result.Add(current.VersionHash)) throw new InvalidDataException("The save ancestry repeats a version.");
            if (current.Number == 1)
            {
                if (current.ParentHash is not null) throw new InvalidDataException("The first save has a parent.");
                break;
            }
            if (current.ParentHash is not { } parentHash) throw new InvalidDataException("The save parent is missing.");
            var previous = branch?.Offer.Version.VersionHash == parentHash ? branch.Offer.Version :
                await readManifest(parentHash, current.Number - 1, cancellationToken);
            // A signed branch base may outlive older payloads/manifests. It is
            // still an exact anchor; missing older claims cannot be dominated.
            if (previous is null) break;
            if (previous.VersionHash != parentHash || previous.Number != current.Number - 1)
                throw new InvalidDataException("The signed save parent changed.");
            current = previous;
        }
        return result;
    }
}
