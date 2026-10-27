using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record WorldPeerMembership(SharedWorldPeerRoot Root,
    SharedWorldRoster Roster, Guid DeviceId, bool ReceiveConsented,
    string HostEndpoint, string HostFingerprint);

internal sealed partial class FriendLink
{
    internal async Task<IReadOnlyList<WorldPeerMembership>> PeerMembershipsAsync(
        bool refresh, CancellationToken cancellationToken, Action<PeerMembershipRefreshPending>? onPending = null,
        Action<PeerMembershipLookupOutcome, bool>? onLookup = null)
    {
        if (!TryRetain()) return [];
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (config is null || config.KnownRevoked) return [];
                if (config.PeerWorld is null && refresh && view.State is ("Connected" or "Disabled") &&
                    view.HostCapabilities.Contains(CompanionProtocol.SharedWorldPeersCapability))
                {
                    foreach (var profile in view.Profiles.Where(item => item.Kind != GameKinds.Custom))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            ObserveMembershipLookup(onLookup, PeerMembershipLookupOutcome.Started, false);
                            var lookup = await SyncSharingRosterAsync(profile.Id, cancellationToken);
                            ObserveMembershipLookup(onLookup, MembershipLookupOutcome(lookup.View.Code), lookup.Roster is not null);
                            if (lookup.View.Code == "RosterCatchUpPending") onPending?.Invoke(PeerMembershipRefreshPending.RosterCatchUpPending);
                            else if (lookup.View.Code == "RosterUnavailable") onPending?.Invoke(PeerMembershipRefreshPending.RosterUnavailable);
                        }
                        catch (OperationCanceledException)
                        {
                            ObserveMembershipLookup(onLookup, PeerMembershipLookupOutcome.Canceled, false);
                            throw;
                        }
                        catch (Exception ex) when (ex is IOException or HttpRequestException or
                            JsonException or CryptographicException or InvalidDataException or
                            UnauthorizedAccessException)
                        {
                            ObserveMembershipLookup(onLookup, ex is HttpRequestException
                                ? PeerMembershipLookupOutcome.TransportFailure : PeerMembershipLookupOutcome.InvalidData, false);
                            /* Keep the last verified membership. */
                        }
                    }
                }
                var result = new List<WorldPeerMembership>();
                foreach (var (profileId, groupId) in config.ApprovedSharedWorldGroups)
                {
                    if (config.PendingSharedWorldGroups.ContainsKey(profileId)) continue;
                    var memberId = SharedWorldDeviceId;
                    var bytes = data.LoadProtected($"shared-world-roster-{memberId:N}-{profileId:N}.protected");
                    if (bytes is null || bytes.Length > 262144) continue;
                    SharedWorldRoster? roster;
                    try { roster = JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json); }
                    catch (JsonException) { continue; }
                    if (roster is null || roster.GroupId != groupId || roster.ProfileId != profileId ||
                        roster.OwnerPublicKey != config.SharedWorldSigningKeys.GetValueOrDefault(profileId)) continue;
                    using var key = LoadPcSigningKey(config.DeviceId);
                    var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
                    if (SharedWorldPeerTrust.AccessFromRoster(new(roster.OwnerPublicKey, profileId, groupId),
                        roster, memberId, publicKey) is null) continue;
                    result.Add(new(new(roster.OwnerPublicKey, profileId, groupId), roster,
                        memberId, HasSharedWorldReceiveConsent(profileId),
                        config.Endpoint, config.Fingerprint));
                }
                return result;
            }
            finally { gate.Release(); }
        }
        finally { ReleaseRetained(); }
    }

    private static void ObserveMembershipLookup(Action<PeerMembershipLookupOutcome, bool>? observer,
        PeerMembershipLookupOutcome outcome, bool hasRoster)
    {
        try { observer?.Invoke(outcome, hasRoster); }
        catch (Exception) { /* A diagnostic observation cannot change enrollment. */ }
    }

    private static PeerMembershipLookupOutcome MembershipLookupOutcome(string code) => code switch
    {
        "NotPaired" => PeerMembershipLookupOutcome.NotPaired,
        "UnknownProfile" => PeerMembershipLookupOutcome.UnknownProfile,
        "UpdateRequired" => PeerMembershipLookupOutcome.UpdateRequired,
        "RosterUnavailable" => PeerMembershipLookupOutcome.RosterUnavailable,
        "EnrollmentDenied" => PeerMembershipLookupOutcome.EnrollmentDenied,
        "InvalidChallenge" => PeerMembershipLookupOutcome.InvalidChallenge,
        "KeyReviewRequired" => PeerMembershipLookupOutcome.KeyReviewRequired,
        "SigningIdentityChanged" => PeerMembershipLookupOutcome.SigningIdentityChanged,
        "RosterCatchUpPending" => PeerMembershipLookupOutcome.RosterCatchUpPending,
        "RosterRollback" => PeerMembershipLookupOutcome.RosterRollback,
        "RosterRejected" => PeerMembershipLookupOutcome.RosterRejected,
        "CanManage" => PeerMembershipLookupOutcome.CanManage,
        "PermissionDenied" => PeerMembershipLookupOutcome.PermissionDenied,
        _ => PeerMembershipLookupOutcome.Other
    };
}

public sealed partial class FriendService
{
    internal async Task ApplyPeerRosterAsync(SharedWorldRoster roster, CancellationToken cancellationToken)
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        foreach (var (_, link) in current) await link.ApplyPeerRosterAsync(roster, cancellationToken);
    }
    public Task<WorldAuthorityOfferResult> PrepareSoloRecoveryOfferAsync(Guid profileId,
        string? sourceBranchHash = null) => SelectedLink()?.PrepareSoloRecoveryOfferAsync(profileId, sourceBranchHash) ??
        Task.FromResult(new WorldAuthorityOfferResult(false, "NotPaired", "Choose a saved world connection."));

    public Task<WorldSeparateCopyResult> DeclareSoloRecoveryAsync(Guid profileId,
        bool acceptSplitWarning) => SelectedLink()?.DeclareSoloRecoveryAsync(profileId, acceptSplitWarning) ??
        Task.FromResult(new WorldSeparateCopyResult(false, "NotPaired", "Choose a saved world connection."));

    internal async Task<IReadOnlyList<WorldPeerMembership>> PeerMembershipsAsync(
        bool refresh, CancellationToken cancellationToken, Action<PeerMembershipRefreshPending>? onPending = null,
        Action<PeerMembershipLookupOutcome, bool>? onLookup = null)
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        var memberships = new List<WorldPeerMembership>();
        foreach (var (_, link) in current)
            memberships.AddRange(await link.PeerMembershipsAsync(refresh, cancellationToken, onPending, onLookup));
        return memberships.GroupBy(item => SharedWorldPeerTrust.RootKey(item.Root))
            .Select(group =>
            {
                var latest = group.OrderByDescending(item => item.Roster.Revision).First();
                return latest with
                {
                    ReceiveConsented = new WorldPeerReceiveConsentStore(data).Effective(
                    latest.Root, latest.DeviceId, group.Any(item => item.DeviceId == latest.DeviceId &&
                        item.Roster.Signature == latest.Roster.Signature && item.ReceiveConsented))
                };
            }).ToArray();
    }

    internal async Task<bool> SelectWorldSourceAsync(SharedWorldPeerRoot root, CancellationToken cancellationToken)
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        foreach (var (id, link) in current)
        {
            var memberships = await link.PeerMembershipsAsync(false, cancellationToken);
            if (memberships.Any(item => item.Root == root && item.ReceiveConsented)) return Select(id).Ok;
        }
        return false;
    }

    internal async Task<bool> CommitPeerReceiveAsync(SharedWorldPeerRoot root, Guid deviceId,
        Action commit, CancellationToken cancellationToken, bool canonicalCopyCompleted = false)
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        foreach (var (_, link) in current)
            if (await link.CommitPeerReceiveAsync(root, deviceId, commit, cancellationToken, canonicalCopyCompleted))
            {
                // Every saved alias for the exact source member sees the same
                // completed vault. Its old legacy failure is no longer current.
                if (canonicalCopyCompleted)
                    foreach (var (_, alias) in current)
                        if (!ReferenceEquals(alias, link)) alias.InvalidatePeerReceiveStatus(root, deviceId);
                return true;
            }
        return false;
    }

    internal async Task RecordPeerOwnerReceiptAcknowledgedAsync(SharedWorldPeerRoot root, SharedWorldReceipt receipt,
        Guid responderDeviceId, string responderPublicKey, CancellationToken cancellationToken)
    {
        if (responderDeviceId != Guid.Empty || responderPublicKey != root.OwnerPublicKey) return;
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        foreach (var (_, link) in current)
            if (await link.RecordPeerOwnerReceiptAcknowledgedAsync(root, receipt, responderDeviceId,
                responderPublicKey, cancellationToken)) return;
    }
}

internal sealed partial class FriendLink
{
    internal async Task ApplyPeerRosterAsync(SharedWorldRoster roster, CancellationToken cancellationToken)
    {
        if (!TryRetain()) return;
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                lock (SharedWorldMutationGate.For(data.RootPath))
                {
                    if (config is null || config.PendingSharedWorldGroups.ContainsKey(roster.ProfileId) ||
                        config.ApprovedSharedWorldGroups.GetValueOrDefault(roster.ProfileId) != roster.GroupId ||
                        config.SharedWorldSigningKeys.GetValueOrDefault(roster.ProfileId) != roster.OwnerPublicKey ||
                        !new WorldAuthorityStore(data).TrustsCurrentRoster(roster)) return;
                    var floor = config.SharedRosterFloors.GetValueOrDefault(roster.ProfileId);
                    if (floor is not null && (floor.GroupId != roster.GroupId || floor.Revision > roster.Revision ||
                        floor.Revision == roster.Revision && floor.Signature != roster.Signature)) return;
                    config.SharedRosterFloors[roster.ProfileId] = new(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature);
                    data.SaveProtected($"shared-world-roster-{SharedWorldDeviceId:N}-{roster.ProfileId:N}.protected",
                        JsonSerializer.SerializeToUtf8Bytes(roster, Json));
                    SaveConfig();
                }
            }
            finally { gate.Release(); }
        }
        finally { ReleaseRetained(); }
    }

    private (SharedWorldRoster? Roster, SharedWorldSharingView View) PeerSharingRoster(Guid profileId)
    {
        var selfId = SharedWorldDeviceId;
        if (!MatchesPeerSessionProfile(profileId) || config?.PeerWorld is not { } root)
            return (null, SharingFailure(selfId, "UnknownProfile", "This server is not part of the selected shared world session."));
        var cached = CachedPeerRoster(root.ProfileId);
        if (cached is null)
            return (null, SharingFailure(selfId, "RosterUnavailable", "Sync approved peers to verify this world's current sharing list."));
        using var key = LoadExistingSharingKey();
        if (key is null)
            return (null, SharingFailure(selfId, "HostingIdentityRequired", "Check this PC's sharing identity with the Host first."));
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        if (SharedWorldPeerTrust.AccessFromRoster(root, cached, selfId, publicKey) is null)
            return (null, SharingFailure(selfId, "PermissionDenied", "This PC no longer has access to this world's sharing list."));
        // The original owner is proved by its pinned key rather than a
        // synthetic roster member. Sharing edits still use its Host workspace.
        return (cached, selfId == Guid.Empty
            ? new(true, false, selfId, cached.Revision, "OriginalOwner",
                "You own this world. Remote Stop remains subject to the current Host controls and a fresh zero-player count.",
                [], DateTimeOffset.UtcNow)
            : SharedWorldSharingProjection.Build(cached, selfId, publicKey, DateTimeOffset.UtcNow));
    }

    private bool MatchesPeerSessionProfile(Guid profileId)
    {
        if (config?.PeerWorld is not { } root || profileId == Guid.Empty) return false;
        if (profileId == root.ProfileId || config.PeerWorldLocalProfileId == profileId) return true;
        if (config.PeerWorldLocalProfileId is not null || view.ConnectionCode is not null ||
            !view.ProtocolCompatible || view.State is not ("Connected" or "Disabled") ||
            view.LastConnectedUtc is null || view.Profiles.Count != 1 || view.Profiles[0].Id != profileId)
            return false;
        // Older saved peer credentials lacked the local profile binding. Only
        // the authenticated, unambiguous session status may supply that ID.
        config.PeerWorldLocalProfileId = profileId;
        SaveConfig();
        return true;
    }

    private SharedWorldRoster? CachedPeerRoster(Guid profileId)
    {
        if (config?.PeerWorld is not { } root || root.ProfileId != profileId || config.KnownRevoked) return null;
        if (config.SharedWorldSigningKeys.GetValueOrDefault(profileId) != root.OwnerPublicKey ||
            config.ApprovedSharedWorldGroups.GetValueOrDefault(profileId) != root.GroupId ||
            config.PendingSharedWorldGroups.ContainsKey(profileId)) return null;
        var bytes = data.LoadProtected($"shared-world-roster-{SharedWorldDeviceId:N}-{profileId:N}.protected");
        if (bytes is null || bytes.Length > SharedWorldService.MaximumManifestBytes) return null;
        var roster = JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json);
        var floor = config.SharedRosterFloors.GetValueOrDefault(profileId);
        return roster is not null && roster.OwnerPublicKey == root.OwnerPublicKey && roster.GroupId == root.GroupId &&
            roster.ProfileId == profileId && SharedWorldRosterTrust.Verify(roster) &&
            floor is not null && floor.GroupId == roster.GroupId && floor.Epoch == roster.Epoch &&
            floor.Revision == roster.Revision && floor.Signature == roster.Signature &&
            new WorldAuthorityStore(data).TrustsCurrentRoster(roster) ? roster : null;
    }

    internal async Task<bool> CommitPeerReceiveAsync(SharedWorldPeerRoot root, Guid deviceId,
        Action commit, CancellationToken cancellationToken, bool canonicalCopyCompleted = false)
    {
        if (!TryRetain()) return false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                lock (sharedReceiptSync)
                    lock (SharedWorldMutationGate.For(data.RootPath))
                    {
                        if (config is null || config.KnownRevoked || SharedWorldDeviceId != deviceId ||
                            !HasSharedWorldReceiveConsent(root.ProfileId) ||
                            config.PendingSharedWorldGroups.ContainsKey(root.ProfileId) ||
                            config.ApprovedSharedWorldGroups.GetValueOrDefault(root.ProfileId) != root.GroupId ||
                            config.SharedWorldSigningKeys.GetValueOrDefault(root.ProfileId) != root.OwnerPublicKey)
                            return false;
                        cancellationToken.ThrowIfCancellationRequested();
                        // All aliases share the consent decision under this lock.
                        // Withdrawal takes receipt -> mutation before waiting for
                        // a link gate, so no fallback alias can revive promotion.
                        commit();
                        if (canonicalCopyCompleted) InvalidatePeerReceiveStatus(root, deviceId);
                        return true;
                    }
            }
            finally { gate.Release(); }
        }
        finally { ReleaseRetained(); }
    }

    // Display invalidation only, after a successful fenced peer commit. It
    // changes no consent, signed history, selected source or receiving policy.
    // No link gate is needed, including when another matching alias is busy.
    internal void InvalidatePeerReceiveStatus(SharedWorldPeerRoot root, Guid deviceId)
    {
        if (!TryRetain()) return;
        try
        {
            lock (sharedReceiptSync)
            {
                if (config is null || SharedWorldDeviceId != deviceId || ReceiveConsentRoot(root.ProfileId) != root) return;
                foreach (var profileId in ReceiveConsentProfileIds(root.ProfileId))
                    InvalidateSharedReceiveStatus(profileId);
            }
        }
        finally { ReleaseRetained(); }
    }
}
