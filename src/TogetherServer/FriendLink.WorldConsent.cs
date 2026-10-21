using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    private sealed record WorldConsentChange(SharedWorldPeerRoot Root, Guid DeviceId, long Revision);

    private SharedWorldPeerRoot? ReceiveConsentRoot(Guid profileId)
    {
        var current = config;
        if (current is null || current.SharedWorldSigningKeys?.GetValueOrDefault(profileId) is not { } owner ||
            current.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) is not { } group || group == Guid.Empty)
            return null;
        var root = new SharedWorldPeerRoot(owner, profileId, group);
        return SharedWorldPeerTrust.ValidRoot(root) && (current.PeerWorld is null || current.PeerWorld == root) ? root : null;
    }

    private bool HasSharedWorldReceiveConsent(Guid profileId)
    {
        var current = config;
        var legacy = current?.ConsentedSharedWorldProfiles?.Contains(profileId) == true &&
            !withdrawnSharedConsent.ContainsKey(profileId);
        var root = ReceiveConsentRoot(profileId);
        return root is null ? legacy : new WorldPeerReceiveConsentStore(data).Effective(root,
            current!.PeerWorldMemberDeviceId ?? current.DeviceId,
            legacy || !withdrawnSharedConsent.ContainsKey(profileId) && AutomaticHostingReceiveConsent(root));
    }

    // Shared hosting includes keeping and forwarding the server's completed
    // saves. The owner's verified grant supplies the initial policy; a saved
    // local withdrawal or damaged consent record always takes precedence.
    // This projection opens no listener and writes no consent or world files.
    private bool AutomaticHostingReceiveConsent(SharedWorldPeerRoot root)
    {
        try
        {
            if (config is null || config.KnownRevoked ||
                view.ConnectionCode is "Revoked" or "AccessExpired" or "ApprovalPending" ||
                config.PendingSharedWorldGroups?.ContainsKey(root.ProfileId) == true ||
                config.SharedWorldConflicts?.Contains(root.ProfileId) == true)
                return false;
            var floor = config.SharedRosterFloors?.GetValueOrDefault(root.ProfileId);
            var bytes = data.LoadProtected($"shared-world-roster-{SharedWorldDeviceId:N}-{root.ProfileId:N}.protected");
            var keyBytes = data.LoadProtected($"shared-world-pc-signing-{SharedWorldDeviceId:N}.protected");
            if (floor is null || bytes is null || bytes.Length > SharedWorldService.MaximumManifestBytes || keyBytes is null)
                return false;
            var roster = JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json);
            if (roster is null || !SharedWorldRosterTrust.Verify(roster) ||
                roster.OwnerPublicKey != root.OwnerPublicKey || roster.ProfileId != root.ProfileId ||
                roster.GroupId != root.GroupId || roster.GroupId != floor.GroupId ||
                roster.Epoch != floor.Epoch || roster.Revision != floor.Revision ||
                roster.Signature != floor.Signature || !new WorldAuthorityStore(data).TrustsCurrentRoster(roster))
                return false;
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(keyBytes, out _);
            return SharedWorldRosterTrust.HasRole(roster, SharedWorldDeviceId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                grants => grants.Receive && grants.EligibleHost && grants.Reshare);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    private WorldConsentChange? CaptureWorldConsentChange(Guid profileId)
    {
        var root = ReceiveConsentRoot(profileId);
        if (root is null || config is null) return null;
        var id = config.PeerWorldMemberDeviceId ?? config.DeviceId;
        return new(root, id, new WorldPeerReceiveConsentStore(data).Read(root, id).Revision);
    }

    private bool AllowWorldReceiveConsent(Guid profileId, WorldConsentChange? before)
    {
        lock (sharedReceiptSync)
        {
            var root = ReceiveConsentRoot(profileId);
            if (root is not null && config is { } current)
            {
                var id = current.PeerWorldMemberDeviceId ?? current.DeviceId;
                var expected = before is not null && before.Root == root && before.DeviceId == id ? before.Revision : 0;
                if (!new WorldPeerReceiveConsentStore(data).Set(root, id, true, expected)) return false;
            }
            withdrawnSharedConsent.TryRemove(profileId, out _);
            if (sharedProfileCancellation.TryRemove(profileId, out var previous)) previous.Dispose();
            return true;
        }
    }

    // Called without the service's mutation gate: every path that needs both
    // locks takes sharedReceiptSync first, then SharedWorldMutationGate.
    internal void RefreshWorldReceiveConsent(SharedWorldPeerRoot root, Guid deviceId)
    {
        lock (sharedReceiptSync)
        {
            if (ReceiveConsentRoot(root.ProfileId) != root || config is not { } current ||
                (current.PeerWorldMemberDeviceId ?? current.DeviceId) != deviceId) return;
            if (new WorldPeerReceiveConsentStore(data).Effective(root, deviceId, false))
            {
                withdrawnSharedConsent.TryRemove(root.ProfileId, out _);
                if (sharedProfileCancellation.TryRemove(root.ProfileId, out var previous)) previous.Dispose();
            }
            else
            {
                withdrawnSharedConsent[root.ProfileId] = 1;
                if (sharedProfileCancellation.TryGetValue(root.ProfileId, out var cancellation)) cancellation.Cancel();
            }
        }
        lock (sharedScheduleSync)
        {
            sharedRetryAfter.Remove(root.ProfileId);
            sharedFailures.Remove(root.ProfileId);
        }
        sharedTransferHealth.Reset(root.ProfileId);
    }
}

public sealed partial class FriendService
{
    internal void RefreshWorldReceiveConsent(SharedWorldPeerRoot root, Guid deviceId)
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        foreach (var (_, link) in current) link.RefreshWorldReceiveConsent(root, deviceId);
    }
}
