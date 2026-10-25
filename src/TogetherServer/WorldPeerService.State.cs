using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record WorldPeerGroupView(string GroupKey, string Name, Guid ProfileId,
    Guid SelfDeviceId, bool SharingEnabled, bool ReceiveConsented, bool CanReceive,
    bool CanReshare, bool CanHost, SharedWorldPeerGroupState State, string? Warning = null,
    WorldPeerDefaultView? Selection = null);
public sealed record WorldPeerActionResult(bool Ok, string Code, string Message);
public sealed record WorldPeerSharingRequest(bool Enabled);

// One world registry serves Host and Join concurrently. All entries originate
// in an already pinned Host connection or locally owned signed roster.
internal sealed partial class WorldPeerService
{
    private sealed record Group(SharedWorldPeerRoot Root, SharedWorldRoster Roster,
        Guid DeviceId, string Name, bool ReceiveConsented, bool SharingEnabled,
        SharedWorldPeerRoute? Bootstrap);
    private sealed record Registry(int Schema, IReadOnlyList<Group> Groups);
    private const string RegistryName = "world-peer-memberships.protected";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    private readonly LocalData data;
    private readonly HostManager manager;
    private readonly FriendService friend;
    private readonly PairingService pairing;
    private readonly Func<string, IEnumerable<string>, HttpClient> makeClient;
    private readonly Func<TimeSpan, CancellationToken, Task> seedDelay;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object stateSync;
    private readonly Dictionary<string, string> warnings = new(StringComparer.Ordinal);
    private Task? backgroundSync;

    internal WorldPeerService(LocalData data, HostManager manager, FriendService friend,
        PairingService pairing, Func<string, IEnumerable<string>, HttpClient>? makeClient = null,
        Func<TimeSpan, CancellationToken, Task>? seedDelay = null)
    {
        this.data = data; this.manager = manager; this.friend = friend; this.pairing = pairing;
        this.makeClient = makeClient ?? MakePinnedClient;
        this.seedDelay = seedDelay ?? ((duration, token) => Task.Delay(duration, token));
        stateSync = SharedWorldMutationGate.For(data.RootPath);
    }

    private Group[] Groups()
    {
        lock (stateSync)
        {
            var existed = data.HasProtected(RegistryName);
            var bytes = data.LoadProtected(RegistryName);
            if (bytes is null)
            {
                if (existed || data.Recovery.Notices.Any(notice => notice.StateFile == RegistryName))
                    throw new InvalidDataException("World membership needs review.");
                return [];
            }
            if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("World membership is oversized.");
            var registry = JsonSerializer.Deserialize<Registry>(bytes, Json);
            if (registry is not { Schema: 1 } || registry.Groups is null || registry.Groups.Count > 128 ||
                registry.Groups.Any(group => !SharedWorldPeerTrust.ValidRoot(group.Root) ||
                    !SharedWorldRosterTrust.Verify(group.Roster) ||
                    group.Roster.ProfileId != group.Root.ProfileId || group.Roster.GroupId != group.Root.GroupId ||
                    group.Roster.OwnerPublicKey != group.Root.OwnerPublicKey || group.Name.Length > 100 ||
                    group.Bootstrap is not null && !SharedWorldPeerTrust.ValidRoute(group.Bootstrap)) ||
                registry.Groups.Select(group => group.Root.GroupKey).Distinct().Count() != registry.Groups.Count)
                throw new InvalidDataException("World membership failed verification.");
            var consent = new WorldPeerReceiveConsentStore(data);
            return registry.Groups.Select(group => group with
            {
                ReceiveConsented = consent.Effective(group.Root, group.DeviceId, group.ReceiveConsented)
            }).ToArray();
        }
    }

    private void SaveGroup(Group group)
    {
        lock (stateSync)
        {
            var groups = Groups().Where(item => item.Root != group.Root).Append(group).ToArray();
            if (groups.Length > 128) throw new InvalidDataException("The world membership capacity is full.");
            data.SaveProtected(RegistryName, JsonSerializer.SerializeToUtf8Bytes(new Registry(1, groups), Json));
        }
    }

    private Group? Find(string groupKey) => SharedWorldPeerTrust.ValidHash(groupKey)
        ? Groups().SingleOrDefault(group => group.Root.GroupKey == groupKey) : null;

    private SharedWorldPeerAccess? Access(Group group, Guid deviceId, string publicKey)
    {
        var heads = new SharedWorldRosterChainStore(data).Heads(group.Root.ProfileId);
        if (heads.Count > 1 || heads.Count == 1 &&
            (heads[0].OwnerPublicKey != group.Root.OwnerPublicKey || heads[0].GroupId != group.Root.GroupId ||
             heads[0].Revision > group.Roster.Revision ||
             heads[0].Revision == group.Roster.Revision && heads[0].Signature != group.Roster.Signature))
            return null;
        // A current local removal or deadline takes effect before another sync.
        if (group.Root.OwnerPublicKey == LocalOwnerKey() && deviceId != Guid.Empty &&
            (!pairing.AuthorizeActiveDevice(deviceId, out var device).Ok || device is null ||
             !pairing.CanAccess(device, group.Root.ProfileId) || pairing.SharedRosterDirty(group.Root.ProfileId)))
            return null;
        return SharedWorldPeerTrust.AccessFromRoster(group.Root, group.Roster, deviceId, publicKey);
    }

    private SharedWorldPeerStore Store(Group group) => new(data, group.Root,
        new SharedWorldPeerTrust(group.Root, (id, key) =>
        {
            var current = Find(group.Root.GroupKey);
            return current is null ? null : Access(current, id, key);
        }));

    private string? LocalOwnerKey()
    {
        var bytes = data.LoadProtected("shared-world-signing-key.protected");
        if (bytes is null) return null;
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(bytes, out var read);
        return read == bytes.Length ? Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) : null;
    }

    // A retained key may sign only the roster bootstrap handshake while its
    // cached membership is expired or between signed revoke/regrant revisions.
    // It never authorizes a world operation; LocalKey remains that boundary.
    private ECDsa? RetainedLocalKey(Group group)
    {
        var bytes = data.LoadProtected(group.DeviceId == Guid.Empty
            ? "shared-world-signing-key.protected" : $"shared-world-pc-signing-{group.DeviceId:N}.protected");
        if (bytes is null) return null;
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes, out var read);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            if (read == bytes.Length && SharedWorldRosterTrust.ValidKey(publicKey) &&
                (group.DeviceId != Guid.Empty || publicKey == group.Root.OwnerPublicKey)) return key;
            key.Dispose(); return null;
        }
        catch { key.Dispose(); throw; }
    }

    private ECDsa? LocalKey(Group group)
    {
        var key = RetainedLocalKey(group);
        if (key is null) return null;
        try
        {
            if (Access(group, group.DeviceId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) is not null) return key;
        }
        catch { key.Dispose(); throw; }
        key.Dispose();
        return null;
    }

    internal bool HasEnabledGroups
    {
        get
        {
            // The explicitly enabled listener can finish a signed roster page
            // even when an intermediate revision temporarily disables this PC.
            try { return Groups().Any(group => group.SharingEnabled && RetainedLocalKeyAvailable(group)); }
            catch (Exception ex) when (PeerFailure(ex)) { return false; }
        }
    }

    private bool LocalKeyAvailable(Group group)
    { using var key = LocalKey(group); return key is not null; }

    private bool RetainedLocalKeyAvailable(Group group)
    { using var key = RetainedLocalKey(group); return key is not null; }

    internal async Task RefreshMembershipsAsync(bool refreshFriends, CancellationToken cancellationToken)
    {
        foreach (var saved in Groups()) await friend.ApplyPeerRosterAsync(saved.Roster, cancellationToken);
        var memberships = await friend.PeerMembershipsAsync(refreshFriends, cancellationToken);
        foreach (var membership in memberships)
        {
            lock (stateSync)
            {
                var old = Find(membership.Root.GroupKey);
                if (old is not null && (old.Roster.Revision > membership.Roster.Revision ||
                    old.Roster.Revision == membership.Roster.Revision && old.Roster.Signature != membership.Roster.Signature))
                    continue;
                var bootstrap = new SharedWorldPeerRoute(membership.HostEndpoint.TrimEnd('/'),
                    membership.HostFingerprint.ToUpperInvariant());
                var memberKey = membership.DeviceId == Guid.Empty ? membership.Root.OwnerPublicKey :
                    membership.Roster.Members.SingleOrDefault(member => member.DeviceId == membership.DeviceId)?.PublicKey;
                var autoShare = memberKey is not null && SharedWorldPeerTrust.AccessFromRoster(membership.Root, membership.Roster,
                    membership.DeviceId, memberKey) is { Receive: true, Reshare: true };
                SaveGroup(new(membership.Root, membership.Roster, membership.DeviceId,
                    old?.Name ?? "Shared world", new WorldPeerReceiveConsentStore(data).Effective(membership.Root,
                        membership.DeviceId, membership.ReceiveConsented), old?.SharingEnabled == true || autoShare,
                    SharedWorldPeerTrust.ValidRoute(bootstrap) ? bootstrap : old?.Bootstrap));
            }
        }
        var snapshot = await manager.SnapshotAsync();
        foreach (var profile in snapshot.Settings.Profiles.Where(profile => profile.SeparateCopySourceProfileId is null &&
            (profile.SharedSavesEnabled || Groups().Any(group => group.Root.ProfileId == profile.Id && group.DeviceId == Guid.Empty))))
        {
            var roster = await manager.PeerPublishedRosterAsync(profile.Id);
            if (!SharedWorldRosterTrust.Verify(roster)) continue;
            var root = new SharedWorldPeerRoot(roster!.OwnerPublicKey, profile.Id, roster.GroupId);
            lock (stateSync)
            {
                var old = Find(root.GroupKey);
                if (old is not null && (old.Roster.Revision > roster.Revision ||
                    old.Roster.Revision == roster.Revision && old.Roster.Signature != roster.Signature))
                    continue;
                var owner = LocalOwnerKey() == root.OwnerPublicKey;
                if (!owner && old is null) continue;
                var deviceId = owner ? Guid.Empty : old!.DeviceId;
                SaveGroup(new(root, roster, deviceId,
                    profile.Name.Length <= 100 ? profile.Name : profile.Name[..100],
                    new WorldPeerReceiveConsentStore(data).Effective(root, deviceId, old?.ReceiveConsented ?? owner),
                    old?.SharingEnabled ?? snapshot.Settings.CompanionListeningEnabled, old?.Bootstrap));
            }
        }
    }

    internal IReadOnlyList<WorldPeerGroupView> Views()
    {
        var result = new List<WorldPeerGroupView>();
        foreach (var group in Groups())
        {
            using var key = LocalKey(group);
            var access = key is null ? null : Access(group, group.DeviceId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId)
                .Where(record => record.Roster.OwnerPublicKey == group.Root.OwnerPublicKey &&
                    record.Proposal.GroupId == group.Root.GroupId).ToArray();
            var state = Store(group).Project(WorldAuthorityTrust.EffectiveHeads(records));
            result.Add(new(group.Root.GroupKey, group.Name, group.Root.ProfileId, group.DeviceId,
                group.SharingEnabled, group.ReceiveConsented, access?.Receive == true,
                access?.Reshare == true, access is { Receive: true, EligibleHost: true }, state,
                warnings.GetValueOrDefault(group.Root.GroupKey), DefaultView(group, state)));
        }
        return result;
    }

    internal Guid? OwnerRecoveryProfile(string groupKey)
    {
        var group = Find(groupKey);
        if (group is null || group.DeviceId != Guid.Empty) return null;
        using var key = LocalKey(group);
        return key is not null && Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) == group.Root.OwnerPublicKey
            ? group.Root.ProfileId : null;
    }

    internal IReadOnlyList<WorldSeparateCopyBranch> OwnerRecoveryBranches(string groupKey)
    {
        var group = Find(groupKey);
        return group is null || OwnerRecoveryProfile(groupKey) is null ? [] :
            new SharedWorldSeparateCopyStore(data).Read(group.Root.ProfileId).Where(branch =>
                branch.Offer.Roster.OwnerPublicKey == group.Root.OwnerPublicKey &&
                branch.Offer.Roster.GroupId == group.Root.GroupId && branch.CandidatePublicKey == group.Root.OwnerPublicKey).ToArray();
    }

    internal async Task<WorldPeerActionResult> SelectRecoverySourceAsync(string groupKey,
        CancellationToken cancellationToken)
    {
        var group = Find(groupKey);
        if (group is null || !group.ReceiveConsented ||
            !await friend.SelectWorldSourceAsync(group.Root, cancellationToken))
            return new(false, "RecoverySourceUnavailable", "Choose an approved connection with save storage enabled for this world first.");
        return new(true, "RecoverySourceSelected", "The saved source-world connection was selected for manual recovery. No game was changed.");
    }

    internal async Task<WorldPeerActionResult> SetSharingAsync(string groupKey, bool enabled,
        CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var group = Find(groupKey);
            if (group is null || !LocalKeyAvailable(group))
                return new(false, "WorldMembershipRequired", "Connect to this world and verify its membership first.");
            if (enabled)
            {
                var settings = (await manager.SnapshotAsync()).Settings;
                var endpoint = settings.CompanionEndpoint.TrimEnd('/');
                using var certificate = new HostIdentity(data).Ensure(endpoint);
                if (!SharedWorldPeerTrust.ValidRoute(new(endpoint, HostIdentity.Fingerprint(certificate))))
                    return new(false, "PeerRouteRequired", "Configure this PC's HTTPS IP address and port under connection settings first.");
                var result = await manager.UpdateControlPolicyAsync(new(CompanionListeningEnabled: true));
                if (!result.Ok) return new(false, result.Code, result.Message);
            }
            SaveGroup(group with { SharingEnabled = enabled });
            return new(true, "PeerSharingSaved", enabled ?
                "Approved world members may exchange signed information with this PC. Save forwarding also requires permission and local consent." :
                "This PC no longer accepts world peer exchanges. Existing copies and sessions were kept.");
        }
        finally { refreshGate.Release(); }
    }

    private static bool PeerFailure(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or
        HttpRequestException or OverflowException;
}
