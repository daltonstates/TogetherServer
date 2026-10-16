using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record WorldPeerMembership(SharedWorldPeerRoot Root,
    SharedWorldRoster Roster, Guid DeviceId, bool ReceiveConsented,
    string HostEndpoint, string HostFingerprint);

internal sealed partial class FriendLink
{
    internal async Task<IReadOnlyList<WorldPeerMembership>> PeerMembershipsAsync(
        bool refresh, CancellationToken cancellationToken)
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
                        try { _ = await SyncSharingRosterAsync(profile.Id, cancellationToken); }
                        catch (Exception ex) when (ex is IOException or HttpRequestException or
                            JsonException or CryptographicException or InvalidDataException or
                            UnauthorizedAccessException) { /* Keep the last verified membership. */ }
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
        bool refresh, CancellationToken cancellationToken)
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        var memberships = new List<WorldPeerMembership>();
        foreach (var (_, link) in current)
            memberships.AddRange(await link.PeerMembershipsAsync(refresh, cancellationToken));
        return memberships.GroupBy(item => SharedWorldPeerTrust.RootKey(item.Root))
            .Select(group =>
            {
                var latest = group.OrderByDescending(item => item.Roster.Revision).First();
                return latest with { ReceiveConsented = new WorldPeerReceiveConsentStore(data).Effective(
                    latest.Root, latest.DeviceId, group.Any(item => item.DeviceId == latest.DeviceId &&
                        item.Roster.Signature == latest.Roster.Signature && item.ReceiveConsented)) };
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
        Action commit, CancellationToken cancellationToken)
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        foreach (var (_, link) in current)
            if (await link.CommitPeerReceiveAsync(root, deviceId, commit, cancellationToken)) return true;
        return false;
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

    private SharedWorldRoster? CachedPeerRoster(Guid profileId)
    {
        if (config?.PeerWorld is not { } root || root.ProfileId != profileId || config.KnownRevoked) return null;
        var bytes = data.LoadProtected($"shared-world-roster-{SharedWorldDeviceId:N}-{profileId:N}.protected");
        if (bytes is null || bytes.Length > SharedWorldService.MaximumManifestBytes) return null;
        var roster = JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json);
        return roster is not null && roster.OwnerPublicKey == root.OwnerPublicKey && roster.GroupId == root.GroupId &&
            roster.ProfileId == profileId && SharedWorldRosterTrust.Verify(roster) &&
            new WorldAuthorityStore(data).TrustsCurrentRoster(roster) ? roster : null;
    }

    internal async Task<bool> CommitPeerReceiveAsync(SharedWorldPeerRoot root, Guid deviceId,
        Action commit, CancellationToken cancellationToken)
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
                    return true;
                }
            }
            finally { gate.Release(); }
        }
        finally { ReleaseRetained(); }
    }
}
