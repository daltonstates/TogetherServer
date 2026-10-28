using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record WorldPeerLatestSourceResult(bool Ok, string Code, string Message,
    WorldPeerRecoverySource? Source = null, string? ObservationHash = null);
internal sealed record WorldPeerLatestReviewDiagnostic(string Phase, string Reason, string ExceptionType,
    int LocalCopies = 0, int CanonicalCopies = 0, int BranchCopies = 0, int KnownHeadCount = 0,
    int VerifiedPayloads = 0, int CoveringCandidates = 0, string CandidateKind = "None");

internal sealed partial class WorldPeerService
{
    private const string KnownProgressPendingMessage = "Announced progress is not complete and verified on this PC yet. Keep an approved PC with that save reachable, then retry hosting.";
    private static bool KnownProgressNotLocal(Exception error) => error is InvalidDataException &&
        error.Data["WorldPeerLatestReason"] is "KnownProgressNotLocal";

    // Original Start checks signed metadata and the same exact newest-save
    // selector before deciding whether recovery retention is needed at all.
    internal async Task<WorldPeerOriginalStartResult> ResolveLatestOriginalStartSourceAsync(
        SharedWorldPeerRoot root, CancellationToken cancellationToken,
        Action<WorldPeerSourceProgress>? progress = null)
    {
        WorldPeerOriginalStartSource? source = null;
        var phase = "OriginalDiscovery";
        try
        {
            using var preparation = BeginHostingPreparation(root);
            await DiscoverHostingMetadataAsync(root, cancellationToken);
            phase = "OriginalMembership";
            var group = Find(root.GroupKey);
            if (group is null || group.Root != root)
                return new(false, "WorldMembershipRequired", "Reconnect to this shared world before hosting its save.");
            phase = "OriginalPermission";
            if (!await RecoveryAllowedAsync(group, cancellationToken))
                return new(false, "HostingPermissionRequired", "Current receiving and hosting permission needs review before Start.");
            phase = "OriginalObservation";
            var observation = LatestObservationHash(root) ?? throw new InvalidDataException("The shared save observation changed.");
            phase = "OriginalLatestSelection";
            var selected = await LatestLocalSaveAsync(group, cancellationToken, progress);
            if (selected is null)
                return new(false, "SharedSaveUnavailable", "No complete shared save is available on this PC yet.");
            if (!selected.ProducedHere || selected.Vault is not null || selected.Descriptor.Branch is not null)
                return new(true, "DifferentSharedSaveReady", "The newest completed save needs the existing recovery preparation.",
                    RecoveryRequired: true);
            phase = "OriginalSource";
            source = WorldPeerOriginalStartSource.Open(data, root, selected.Descriptor.Version, cancellationToken, progress);
            phase = "OriginalFinalSelection";
            if (!await LatestLocalSelectionCurrentAsync(root, observation, source.Descriptor.Version.VersionHash,
                cancellationToken, progress))
                return new(false, "LatestSaveChanged", "A shared save or permission changed. Check the latest save again before Start.");
            phase = "OriginalFinalScope";
            source.RequireCurrent(data, root, cancellationToken);
            var result = new WorldPeerOriginalStartResult(true, "LatestSharedSaveReady",
                "This PC's original publication matches the latest signed history.", source, observation);
            source = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (KnownProgressNotLocal(error))
        {
            ReportLatestReview(new(phase, "KnownProgressNotLocal", error.GetType().Name));
            // This grants no source or Start proof. The existing bounded
            // recovery transaction must receive and verify every required head,
            // and still refuses an unknown remote fork after it is retained.
            try
            {
                var current = Find(root.GroupKey);
                if (current is null || current.Root != root || !await RecoveryAllowedAsync(current, cancellationToken))
                    return new(false, "HostingPermissionRequired", "Current receiving and hosting permission needs review before Start.");
                return new(true, "SharedSaveCopyPending", KnownProgressPendingMessage, RecoveryRequired: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception changed) when (PeerFailure(changed) || SharedWorldBranchLiveSource.ExpectedFailure(changed))
            {
                ReportLatestReview(new("OriginalPendingScope", "ReviewRequired", changed.GetType().Name));
                return new(false, "SharedSaveHistoryNeedsReview", "Current signed hosting permission needs review. Existing saves were kept.");
            }
        }
        catch (Exception error) when (PeerFailure(error) || SharedWorldBranchLiveSource.ExpectedFailure(error))
        {
            ReportLatestReview(new(phase, "ReviewRequired", error.GetType().Name));
            return new(false, "SharedSaveHistoryNeedsReview",
                "The newest known save or current signed permission needs review. Existing saves were kept.");
        }
        finally { source?.Dispose(); }
    }

    private async Task<bool> LatestLocalSelectionCurrentAsync(SharedWorldPeerRoot root,
        string observation, string versionHash, CancellationToken cancellationToken,
        Action<WorldPeerSourceProgress>? progress)
    {
        var current = Find(root.GroupKey) ?? throw new InvalidDataException("World membership changed.");
        if (current.Root != root || !await RecoveryAllowedAsync(current, cancellationToken) ||
            LatestObservationHash(root) != observation) return false;
        var latest = await LatestLocalSaveAsync(current, cancellationToken, progress);
        return latest?.Descriptor.Version.VersionHash == versionHash && LatestObservationHash(root) == observation;
    }

    // Optional fixed metadata for an isolated source/packaged lab. Production
    // has no sink. No save identity, path, key, payload or raw error is emitted,
    // and a diagnostic failure cannot change an authorization decision.
    internal Action<WorldPeerLatestReviewDiagnostic>? LatestReviewDiagnostic { get; set; }

    private void ReportLatestReview(WorldPeerLatestReviewDiagnostic diagnostic)
    {
        try { LatestReviewDiagnostic?.Invoke(diagnostic); }
        catch (Exception) { /* Diagnostic storage never authorizes or blocks hosting. */ }
    }

    // A local Host instruction checks reachable members first. A member with
    // no incoming route can still send its signed history to an enabled peer;
    // unavailable peers never turn a known newer save into permission to start
    // an older copy. This does not select a Friend connection or group default.
    internal Task<WorldPeerLatestSourceResult> ResolveLatestRecoverySourceAsync(
        SharedWorldPeerRoot root, CancellationToken cancellationToken) =>
        ResolveLatestRecoverySourceAsync(root, cancellationToken, null);

    internal async Task<WorldPeerLatestSourceResult> ResolveLatestRecoverySourceAsync(
        SharedWorldPeerRoot root, CancellationToken cancellationToken, Action<WorldPeerSourceProgress>? progress)
    {
        var phase = "PeerSync";
        try
        {
            using var preparation = BeginHostingPreparation(root);
            WorldPeerSourceProgress.Report(progress, new("PeerSync", "Checking approved PCs for completed saves."));
            await SyncWorldAsync(root, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                var result = await ResolveLatestRecoverySourceOnceAsync(root, cancellationToken, progress);
                phase = "IncomingSeedWait";
                if (result.Ok || attempt == 3 || !await ExpectIncomingSeedAsync(root, cancellationToken)) return result;
                // A sender whose incoming listener is off may be between its
                // normal five-second outbound polls. One confirmation waits
                // up to fifteen seconds for that automatic transfer to arrive.
                WorldPeerSourceProgress.Report(progress, new("PeerSync", "Waiting for an approved PC to finish sending its completed save."));
                await seedDelay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (Exception error) when (PeerFailure(error))
        {
            ReportLatestReview(new(phase, "ReviewRequired", error.GetType().Name));
            return new(false, "SharedSaveHistoryNeedsReview", "The current shared save or signed membership needs review. Existing saves were kept.");
        }
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
        SharedWorldPeerRoot root, CancellationToken cancellationToken, Action<WorldPeerSourceProgress>? progress)
    {
        var phase = "Membership";
        try
        {
            // Exact files are immutable and retention owns its receive gate.
            // Compare material signed state before and after selection instead
            // of waiting behind unrelated network transfers in refreshGate.
            {
                var group = Find(root.GroupKey);
                if (group is null || group.Root != root)
                    return new(false, "WorldMembershipRequired", "Reconnect to this shared world before hosting its save.");
                if (!await RecoveryAllowedAsync(group, cancellationToken))
                    return new(false, "HostingPermissionRequired", "This PC needs permission to receive and host this world's saves.");
                phase = "InitialObservation";
                var observation = LatestObservationHash(root) ?? throw new InvalidDataException("The shared save observation changed.");
                phase = "LatestSelection";
                WorldPeerSourceProgress.Report(progress, new("LatestSave", "Checking complete local copies against the signed save history."));
                var selected = await LatestLocalSaveAsync(group, cancellationToken, progress);
                if (selected is null)
                    return new(false, "SharedSaveUnavailable", "No complete verified save is available on this PC yet. Keep the approved PCs online while the save copies.");
                var lane = selected.Descriptor.Branch?.BranchHash ?? Store(group).Project().Branches
                    .FirstOrDefault(item => item.Branch.HeadVersionHash == selected.Descriptor.Version.VersionHash)?.Branch.BranchHash ??
                    SessionBranchHash(group, selected.Descriptor);
                // An explicit exact selection keeps its identity through the
                // immutable retention and the final permission check.
                phase = "RetainExactSource";
                var source = await ResolveRecoverySourceAsync(root.GroupKey, lane, cancellationToken,
                    selected.Descriptor.Version.VersionHash, progress);
                phase = "FinalRecheck";
                WorldPeerSourceProgress.Report(progress, new("LatestSave", "Repeating current permission and newest-save checks before using the prepared copy."));
                if (!await LatestLocalSelectionCurrentAsync(root, observation, source.Descriptor.Version.VersionHash,
                    cancellationToken, progress))
                    return new(false, "LatestSaveChanged", "A newer save arrived while preparing. Try Host locally again.", null);
                return new(true, "LatestSharedSaveReady", "The newest verified continuation is ready.", source, observation);
            }
        }
        catch (Exception error) when (KnownProgressNotLocal(error))
        {
            ReportLatestReview(new(phase, "KnownProgressNotLocal", error.GetType().Name));
            return new(false, "SharedSaveUnavailable", KnownProgressPendingMessage);
        }
        catch (Exception error) when (PeerFailure(error))
        {
            ReportLatestReview(new(phase, "ReviewRequired", error.GetType().Name));
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
                        // A repeated announcement counter alone changes no
                        // save, grant, conflict or ancestry. All material
                        // projected fields below remain part of this fence.
                        .Select(peer => new
                        {
                            peer.DeviceId,
                            peer.PublicKey,
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

    private async Task<LocalSave?> LatestLocalSaveAsync(Group group, CancellationToken cancellationToken,
        Action<WorldPeerSourceProgress>? progress = null)
    {
        var state = Store(group).Project();
        if (state.Peers.Any(peer => peer.HasConflict))
        {
            ReportLatestReview(new("Inventory", "SignerConflict", "InvalidDataException"));
            throw new InvalidDataException("Conflicting signed world information needs review.");
        }
        var local = (await LocalSavesAsync(group, cancellationToken)).Where(item =>
            !item.Descriptor.TestOnly && item.Descriptor.TestArtifact is null && AuthorizedDescriptor(group, item.Descriptor)).ToArray();
        if (local.Length == 0)
        {
            ReportLatestReview(new("Inventory", "NoCompleteCopy", "None", KnownHeadCount:
                state.Branches.Select(item => item.Branch.HeadVersionHash)
                    .Concat(state.Sessions.Select(item => item.Session.HeadVersionHash).OfType<string>()).Distinct().Count()));
            if (state.Branches.Count > 0 || state.Sessions.Any(item => item.Session.HeadVersionHash is not null))
                throw new InvalidDataException("A known signed save has no complete local payload.");
            return null;
        }
        var candidates = local.Select(item => item.Descriptor).ToArray();
        var known = state.Branches.Select(item => item.Branch.HeadVersionHash)
            .Concat(state.Sessions.Select(item => item.Session.HeadVersionHash).OfType<string>()).Distinct(StringComparer.Ordinal).ToArray();
        var knownNumbers = state.Branches.GroupBy(item => item.Branch.HeadVersionHash, StringComparer.Ordinal)
            .Where(items => items.Select(item => item.Branch.VersionNumber).Distinct().Count() == 1)
            .ToDictionary(items => items.Key, items => items.First().Branch.VersionNumber, StringComparer.Ordinal);
        ReportLatestReview(new("Inventory", "CandidatesRead", "None", local.Length,
            candidates.Count(item => item.Branch is null), candidates.Count(item => item.Branch is not null), known.Length));
        var chosen = await WorldPeerLatestSelection.SelectAsync(group.Root, candidates, known,
            async (index, token) =>
            {
                var item = local[index];
                WorldPeerSourceProgress.Report(progress, new("LatestSave", "Checking the next complete local save candidate."));
                using var verified = WorldPeerVerifiedCopy.Open(data.RootPath, RecoverySourceDirectory(item),
                    item.Descriptor.Version, token, (bytes, total, files, count) =>
                        WorldPeerSourceProgress.Report(progress, new("Verifying", "Checking a complete local save against its signed file hashes.",
                            bytes, total, files, count)));
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
            }, async (hash, number, token) => await FindManifestAsync(group, hash, number, token), cancellationToken,
            ReportLatestReview, knownNumbers);
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
    private sealed record Ancestry(HashSet<string> Hashes, bool ReachedOrigin, HashSet<string> VerifiedAnchors);
    internal static async Task<int> SelectAsync(SharedWorldPeerRoot root,
        IReadOnlyList<WorldPeerSaveDescriptor> candidates, IReadOnlyList<string> knownHeads,
        Func<int, CancellationToken, Task> verifyPayload,
        Func<string, long, CancellationToken, Task<SharedWorldVersion?>> readManifest,
        CancellationToken cancellationToken, Action<WorldPeerLatestReviewDiagnostic>? diagnostic = null,
        IReadOnlyDictionary<string, long>? knownHeadNumbers = null)
    {
        if (!SharedWorldPeerTrust.ValidRoot(root) || candidates.Count is < 1 or > SharedWorldPeerStore.MaximumKnownBranches ||
            knownHeads.Any(hash => !SharedWorldPeerTrust.ValidHash(hash)))
            throw new InvalidDataException("The available histories crossed their world or capacity.");
        var required = candidates.Select(item => item.Version.VersionHash).Concat(knownHeads).ToHashSet(StringComparer.Ordinal);
        var localHeads = candidates.Select(item => item.Version.VersionHash).ToHashSet(StringComparer.Ordinal);
        var winners = new Dictionary<string, int>(StringComparer.Ordinal);
        var ancestries = new Dictionary<int, Ancestry>();
        var verifiedPayloads = 0;
        void Report(string phase, string reason, string exceptionType, string kind = "None")
        {
            try
            {
                diagnostic?.Invoke(new(phase, reason, exceptionType, candidates.Count,
                    candidates.Count(item => item.Branch is null), candidates.Count(item => item.Branch is not null),
                    required.Count, verifiedPayloads, winners.Count, kind));
            }
            catch (Exception) { /* Diagnostics cannot change source selection. */ }
        }
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = candidates[index];
            if (descriptor.TestOnly || descriptor.TestArtifact is not null) continue;
            var phase = "CandidatePayload";
            var kind = descriptor.Branch is null ? "Canonical" : "Branch";
            try
            {
                await verifyPayload(index, cancellationToken);
                verifiedPayloads++;
                phase = "CandidateAncestry";
                var ancestry = await AncestorsAsync(root, descriptor, readManifest, cancellationToken);
                ancestries.Add(index, ancestry);
                if (required.IsSubsetOf(ancestry.Hashes)) winners.TryAdd(descriptor.Version.VersionHash, index);
                else Report(phase, "KnownHeadNotAncestor", "None", kind);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or
                UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or
                System.Text.Json.JsonException or ArgumentException)
            {
                var reason = error.Data["WorldPeerLatestReason"] switch
                {
                    "SourceProofChanged" => "SourceProofChanged",
                    "WorldIdentityChanged" => "WorldIdentityChanged",
                    "BranchSignerChanged" => "BranchSignerChanged",
                    "CanonicalSignerChanged" => "CanonicalSignerChanged",
                    "RepeatedVersion" => "RepeatedVersion",
                    "FirstVersionHasParent" => "FirstVersionHasParent",
                    "MissingParentHash" => "MissingParentHash",
                    "ParentIdentityChanged" => "ParentIdentityChanged",
                    _ => "CopyVerificationFailed"
                };
                Report(phase, reason,
                    error.GetType().Name, kind);
                // Changed or incomplete copies never authorize an older fallback.
            }
        }
        if (winners.Count != 1)
        {
            // This is a routing hint, never a source selection. An actual
            // validated branch offer anchors its exact base after the full
            // head-to-base segment verifies; unavailable older history need
            // not disable catch-up. Truncation before that anchor or any
            // supplied damaged manifest still refuses. Equal/older known heads
            // remain review cases. Unknown remote history still has to pass
            // this same all-heads selector after bounded catch-up completes.
            var anchors = ancestries.Values.SelectMany(ancestry => ancestry.VerifiedAnchors).ToHashSet(StringComparer.Ordinal);
            var complete = ancestries.Count == candidates.Count && ancestries.Values.All(ancestry =>
                ancestry.ReachedOrigin || ancestry.Hashes.Overlaps(anchors));
            var localHistories = ancestries.Where(pair => localHeads.IsSubsetOf(pair.Value.Hashes))
                .GroupBy(pair => candidates[pair.Key].Version.VersionHash, StringComparer.Ordinal).ToArray();
            if (winners.Count == 0 && verifiedPayloads == candidates.Count && complete && localHistories.Length == 1)
            {
                var local = localHistories[0].First();
                var missing = knownHeads.Where(hash => !local.Value.Hashes.Contains(hash)).ToArray();
                if (missing.Length > 0 && missing.All(hash => knownHeadNumbers is not null &&
                    knownHeadNumbers.TryGetValue(hash, out var number) ? number > candidates[local.Key].Version.Number :
                        local.Value.ReachedOrigin))
                {
                    Report("LatestSelection", "KnownProgressNotLocal", "InvalidDataException");
                    var pending = new InvalidDataException("Known signed progress has no complete verified local copy.");
                    pending.Data["WorldPeerLatestReason"] = "KnownProgressNotLocal";
                    throw pending;
                }
            }
            Report("LatestSelection", "UniqueDescendantUnavailable", "InvalidDataException");
            throw new InvalidDataException("The newest history is incomplete or divergent. Keep all copies for review.");
        }
        Report("LatestSelection", "UniqueDescendantVerified", "None");
        return winners.Values.Single();
    }

    private static async Task<Ancestry> AncestorsAsync(SharedWorldPeerRoot root,
        WorldPeerSaveDescriptor descriptor,
        Func<string, long, CancellationToken, Task<SharedWorldVersion?>> readManifest,
        CancellationToken cancellationToken)
    {
        static InvalidDataException Invalid(string reason, string message)
        {
            var error = new InvalidDataException(message);
            error.Data["WorldPeerLatestReason"] = reason;
            return error;
        }
        var result = new HashSet<string>(StringComparer.Ordinal);
        var head = descriptor.Version;
        var branch = descriptor.Branch;
        if (branch is not null && (!SharedWorldSeparateCopyStore.Verify(branch) ||
            branch.Offer.Roster.OwnerPublicKey != root.OwnerPublicKey))
            throw Invalid("SourceProofChanged", "The continuation's source proof changed.");
        var current = head;
        var segmentKey = current.SigningPublicKey;
        var complete = false;
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SharedWorldService.VerifySignature(current) || current.ProfileId != root.ProfileId ||
                current.GroupId != root.GroupId || current.WorldId != head.WorldId || current.Game != head.Game ||
                current.CaptureKind == LiveSaveAcceptanceTrust.TestCaptureKind)
                throw Invalid("WorldIdentityChanged", "The signed save ancestry crossed a world.");
            if (branch is not null)
            {
                if (!SharedWorldSeparateCopyStore.VerifySnapshotVersion(branch, current))
                    throw Invalid("BranchSignerChanged", "The continuation ancestry has an unauthorized signer.");
                if (current.VersionHash == branch.Offer.Version.VersionHash)
                {
                    // The signed offer authorizes this exact transition back
                    // into its source history, including repeated recoveries.
                    anchors.Add(current.VersionHash);
                    branch = branch.SourceBranch;
                    segmentKey = current.SigningPublicKey;
                    continue;
                }
            }
            else if (current.SigningPublicKey != segmentKey)
                throw Invalid("CanonicalSignerChanged", "The save ancestry changed signer without a continuation proof.");
            if (!result.Add(current.VersionHash)) throw Invalid("RepeatedVersion", "The save ancestry repeats a version.");
            if (current.Number == 1)
            {
                if (current.ParentHash is not null) throw Invalid("FirstVersionHasParent", "The first save has a parent.");
                complete = true;
                break;
            }
            if (current.ParentHash is not { } parentHash) throw Invalid("MissingParentHash", "The save parent is missing.");
            var previous = branch?.Offer.Version.VersionHash == parentHash ? branch.Offer.Version :
                await readManifest(parentHash, current.Number - 1, cancellationToken);
            // A signed branch base may outlive older payloads/manifests. It is
            // still an exact anchor; missing older claims cannot be dominated.
            if (previous is null) break;
            if (previous.VersionHash != parentHash || previous.Number != current.Number - 1)
                throw Invalid("ParentIdentityChanged", "The signed save parent changed.");
            current = previous;
        }
        return new(result, complete, anchors);
    }
}
