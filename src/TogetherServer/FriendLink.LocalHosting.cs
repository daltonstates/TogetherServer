using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record LocalHostingAccess(bool Allowed, bool Consented, string Name,
    string? Game, SharedWorldVersion? Version, bool SaveVerified, string Code, string Message,
    SharedWorldPeerRoot? Root = null, bool PeerWorldConnection = false);

internal sealed partial class FriendLink
{
    internal bool RetainLocalHosting() => TryRetain();
    internal void ReleaseLocalHosting() => ReleaseRetained();
    // Metadata enrollment is independent of permission to keep save files. A
    // cached grant is useful offline, but only after its pinned roster/floor,
    // local signing key and current revocation fence have all been verified.
    internal async Task<LocalHostingAccess> LocalHostingAccessAsync(Guid profileId,
        bool refresh, CancellationToken cancellationToken, bool verifyFiles = false,
        bool permissionOnly = false, bool refreshDisconnected = false)
    {
        if (!TryRetain()) return new(false, false, "Shared server", null, null,
            false, "ConnectionClosed", "This saved Host connection is closing.");
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var refreshUnavailable = false;
                if (refresh && config is { KnownRevoked: false } &&
                    view.ConnectionCode is not ("Revoked" or "AccessExpired" or "ApprovalPending") &&
                    (view.State is "Connected" or "Disabled" ||
                        refreshDisconnected && !HasCurrentCachedHostingPermission(profileId)))
                {
                    try
                    {
                        var current = await SyncSharingRosterAsync(profileId, cancellationToken);
                        if (current.Roster is null)
                            return new(false, HasSharedWorldReceiveConsent(profileId),
                                "Shared server", null, null, false,
                                current.View.Code, current.View.Message);
                    }
                    catch (Exception error) when (error is HttpRequestException ||
                        error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    { refreshUnavailable = true; }
                }
                var access = ReadLocalHostingAccess(profileId, verifyFiles, permissionOnly);
                // Keep current signed offline grants and denials. Without a
                // verified cache, explain that the refresh could not complete.
                return refreshUnavailable && access.Code == "SharingPermissionUnknown"
                    ? access with { Code = "RosterUnavailable", Message = "This PC could not check the Host's sharing permission. Keep the Host app open and retry." }
                    : access;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or
                JsonException or CryptographicException or UnauthorizedAccessException or ArgumentException)
            {
                return new(false, HasSharedWorldReceiveConsent(profileId), "Shared server", null,
                    null, false, "SharingReviewRequired", "The saved sharing permission or world history needs review.");
            }
            finally { gate.Release(); }
        }
        finally { ReleaseRetained(); }
    }

    private bool HasCurrentCachedHostingPermission(Guid profileId)
    {
        try { return ReadLocalHostingAccess(profileId, verifyFiles: false, permissionOnly: true).Allowed; }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    // A projection cannot repair a missing enrolled identity by generating a
    // different key. Initial enrollment keeps its separate key-creation path.
    private ECDsa? LoadExistingSharingKey()
    {
        var owner = config?.PeerWorld is not null && SharedWorldDeviceId == Guid.Empty;
        var bytes = data.LoadProtected(owner ? "shared-world-signing-key.protected" :
            $"shared-world-pc-signing-{SharedWorldDeviceId:N}.protected");
        if (bytes is null) return null;
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes, out var length);
            if (length == bytes.Length) return key;
            key.Dispose();
            return null;
        }
        catch { key.Dispose(); throw; }
    }

    private LocalHostingAccess ReadLocalHostingAccess(Guid profileId, bool verifyFiles, bool permissionOnly = false)
    {
        var profile = view.Profiles.SingleOrDefault(item => item.Id == profileId) ??
            config?.CachedProfiles?.SingleOrDefault(item => item.Id == profileId);
        var name = profile?.Name is { Length: > 0 and <= 80 } value && !value.Any(char.IsControl)
            ? value : "Shared server";
        var consented = HasSharedWorldReceiveConsent(profileId);
        LocalHostingAccess Denied(string code, string message) => new(false, consented,
            name, profile?.GameKind ?? profile?.Kind, null, false, code, message);
        if (config is null)
            return Denied("NotPaired", "Choose a saved Host connection first.");
        var sourceId = SharedWorldSourceProfileId(profileId);
        if (sourceId is null)
            return Denied("UnknownProfile", "This server is not part of the selected shared world session.");
        profileId = sourceId.Value;
        if (config.KnownRevoked || view.ConnectionCode == "Revoked")
            return Denied("Revoked", "The owner removed this PC's saved access. Ask them for a new server code.");
        if (view.ConnectionCode == "AccessExpired")
            return Denied("AccessExpired", "The owner ended this PC's access. Ask them to extend or clear its deadline.");
        if (view.ConnectionCode == "ApprovalPending")
            return Denied("ApprovalPending", "The owner needs to approve this PC before it can host this server.");
        if (config.PendingSharedWorldGroups?.ContainsKey(profileId) == true ||
            config.SharedWorldConflicts?.Contains(profileId) == true)
            return Denied("WorldHistoryReviewRequired", "Choose and review the shared save history before preparing hosting.");
        var floor = config.SharedRosterFloors?.GetValueOrDefault(profileId);
        var owner = config.SharedWorldSigningKeys?.GetValueOrDefault(profileId);
        if (floor is null || owner is null)
            return Denied("SharingPermissionUnknown", "This PC has not verified the current sharing permission. Check again while connected to the Host.");
        if (config.PeerWorld is { } pinnedRoot && (pinnedRoot.ProfileId != profileId ||
            pinnedRoot.GroupId != floor.GroupId || pinnedRoot.OwnerPublicKey != owner))
            return Denied("SharingReviewRequired", "The saved sharing permission or world history needs review. Check again to verify the current signed history.");
        var bytes = data.LoadProtected($"shared-world-roster-{SharedWorldDeviceId:N}-{profileId:N}.protected");
        var roster = bytes is { Length: > 0 } && bytes.LongLength <= SharedWorldService.MaximumManifestBytes
            ? JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json) : null;
        if (roster is null)
            return Denied("SharingPermissionUnknown", "This PC has not verified the current sharing permission. Check again while connected to the Host.");
        var originalOwner = config.PeerWorld is not null && SharedWorldDeviceId == Guid.Empty;
        using var key = LoadExistingSharingKey();
        if (key is null) return Denied("HostingIdentityRequired", "Check this PC's sharing identity with the Host first.");
        if (!SharedWorldRosterTrust.Verify(roster) || roster.ProfileId != profileId ||
            roster.GroupId != floor.GroupId || roster.Epoch != floor.Epoch ||
            roster.Revision != floor.Revision || roster.Signature != floor.Signature ||
            !new WorldAuthorityStore(data).TrustsCurrentRoster(roster))
            return Denied("SharingReviewRequired", "The saved sharing permission or world history needs review. Check again to verify the current signed history.");
        if (roster.OwnerPublicKey != owner)
            return Denied("SigningIdentityChanged", "The Host's world identity changed. Ask the owner to review it.");
        var member = roster.Members.SingleOrDefault(item => item.DeviceId == SharedWorldDeviceId);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        if (originalOwner && publicKey != config.PeerWorld!.OwnerPublicKey ||
            member is not null && member.PublicKey != publicKey)
            return Denied("HostingIdentityRequired", "Check this PC's sharing identity with the Host first.");
        if (originalOwner
            ? SharedWorldPeerTrust.AccessFromRoster(config.PeerWorld!, roster, Guid.Empty, publicKey) is not { Receive: true, EligibleHost: true }
            : !SharedWorldRosterTrust.HasRole(roster, SharedWorldDeviceId, publicKey,
                grants => grants.Receive && grants.EligibleHost))
            return Denied("HostingPermissionRequired", "The owner has not allowed this PC to host this server. Ask them to turn on Allow friends to host this server on the Host PC and check this PC's access.");
        // A verified signed denial is actionable before this PC has followed
        // its first group. An allowed result still needs that local approval.
        if (config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != floor.GroupId)
            return Denied("SharingPermissionUnknown", "This PC has not verified the current sharing permission. Check again while connected to the Host.");
        // The primary action depends only on current signed authorization.
        // Save selection and verification belong to the explicit hosting flow.
        if (permissionOnly)
            return new(true, consented, name, profile?.GameKind ?? profile?.Kind, null, false,
                "HostingAllowed", "The owner allows this PC to host this server.",
                new(roster.OwnerPublicKey, profileId, roster.GroupId), config.PeerWorld is not null);
        SharedWorldVersion? version = null;
        var verified = false;
        if (consented)
        {
            try
            {
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, ReceivedRoot(profileId));
                if (verifyFiles)
                {
                    version = ReadReceivedLatest(ReceivedRoot(profileId));
                    verified = version is not null;
                }
                else
                {
                    var path = Path.Combine(ReceivedRoot(profileId), "latest.json");
                    if (File.Exists(path) && new FileInfo(path).Length <= SharedWorldService.MaximumManifestBytes &&
                        ManagedImportFiles.PlainFile(path))
                    {
                        version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json);
                        if (version is null || !SharedWorldService.VerifySignature(version)) version = null;
                    }
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or
                JsonException or CryptographicException or UnauthorizedAccessException)
            { /* Never treat an incomplete or changed payload as a usable save. */ }
        }
        version ??= config.LastSharedHostManifests?.GetValueOrDefault(profileId);
        if (version is not null && (version.ProfileId != profileId || version.GroupId != roster.GroupId ||
            !AuthorizedVersionSignerForRecords(owner, version, new WorldAuthorityStore(data).Read(profileId))))
        { version = null; verified = false; }
        return new(true, consented, name, version?.Game ?? profile?.GameKind ?? profile?.Kind,
            version, verified, "HostingAllowed", "The owner allows this PC to host this server.",
            new(roster.OwnerPublicKey, profileId, roster.GroupId), config.PeerWorld is not null);
    }
}

public sealed partial class FriendService
{
    internal sealed class LocalHostingConnection(Guid id, FriendLink link) : IDisposable
    {
        internal Guid Id { get; } = id;
        internal FriendLink Link { get; } = link;
        public void Dispose() => Link.ReleaseLocalHosting();
    }

    internal LocalHostingConnection? RetainLocalHostingConnection()
    {
        lock (sync)
        {
            var link = disposed ? null : links.FirstOrDefault(item => item.Id == selectedId).Link;
            return link?.RetainLocalHosting() == true ? new(selectedId, link) : null;
        }
    }

    internal bool CurrentLocalHostingConnection(LocalHostingConnection connection)
    {
        lock (sync) return !disposed && selectedId == connection.Id &&
            links.Any(item => item.Id == connection.Id && ReferenceEquals(item.Link, connection.Link));
    }

    internal Task<LocalHostingAccess> LocalHostingAccessAsync(Guid profileId, bool refresh,
        CancellationToken cancellationToken) => SelectedLink()?.LocalHostingAccessAsync(profileId,
            refresh, cancellationToken) ?? Task.FromResult(new LocalHostingAccess(false, false,
                "Shared server", null, null, false, "NotPaired", "Choose a saved Host connection first."));
}
