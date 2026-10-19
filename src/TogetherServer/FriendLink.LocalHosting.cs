using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record LocalHostingAccess(bool Allowed, bool Consented, string Name,
    string? Game, SharedWorldVersion? Version, bool SaveVerified, string Code, string Message);

internal sealed partial class FriendLink
{
    internal bool RetainLocalHosting() => TryRetain();
    internal void ReleaseLocalHosting() => ReleaseRetained();
    // Metadata enrollment is independent of permission to keep save files. A
    // cached grant is useful offline, but only after its pinned roster/floor,
    // local signing key and current revocation fence have all been verified.
    internal async Task<LocalHostingAccess> LocalHostingAccessAsync(Guid profileId,
        bool refresh, CancellationToken cancellationToken, bool verifyFiles = false)
    {
        if (!TryRetain()) return new(false, false, "Shared server", null, null,
            false, "ConnectionClosed", "This saved Host connection is closing.");
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (refresh && config is not null && view.State is "Connected" or "Disabled")
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
                    { /* An unreachable Host does not invalidate a signed offline grant. */ }
                }
                return ReadLocalHostingAccess(profileId, verifyFiles);
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

    private LocalHostingAccess ReadLocalHostingAccess(Guid profileId, bool verifyFiles)
    {
        var profile = view.Profiles.SingleOrDefault(item => item.Id == profileId) ??
            config?.CachedProfiles?.SingleOrDefault(item => item.Id == profileId);
        var name = profile?.Name is { Length: > 0 and <= 80 } value && !value.Any(char.IsControl)
            ? value : "Shared server";
        var consented = HasSharedWorldReceiveConsent(profileId);
        LocalHostingAccess Denied(string code, string message) => new(false, consented,
            name, profile?.GameKind ?? profile?.Kind, null, false, code, message);
        if (config is null || config.KnownRevoked || view.ConnectionCode is "Revoked" or "AccessExpired" or "ApprovalPending")
            return Denied("HostingPermissionRequired", "The owner must grant Can host this save to this PC.");
        if (config.PendingSharedWorldGroups?.ContainsKey(profileId) == true ||
            config.SharedWorldConflicts?.Contains(profileId) == true)
            return Denied("WorldHistoryReviewRequired", "Choose and review the shared save history before preparing hosting.");
        var floor = config.SharedRosterFloors?.GetValueOrDefault(profileId);
        var owner = config.SharedWorldSigningKeys?.GetValueOrDefault(profileId);
        if (floor is null || owner is null ||
            config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != floor.GroupId)
            return Denied("HostingPermissionRequired", "Check the owner's Can host this save permission first.");
        var bytes = data.LoadProtected($"shared-world-roster-{SharedWorldDeviceId:N}-{profileId:N}.protected");
        var roster = bytes is { Length: > 0 } && bytes.LongLength <= SharedWorldService.MaximumManifestBytes
            ? JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json) : null;
        var keyBytes = data.LoadProtected($"shared-world-pc-signing-{SharedWorldDeviceId:N}.protected");
        if (keyBytes is null) return Denied("HostingIdentityRequired", "Check this PC's sharing identity with the Host first.");
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(keyBytes, out _);
        if (roster is null || !SharedWorldRosterTrust.Verify(roster) ||
            roster.OwnerPublicKey != owner || roster.ProfileId != profileId ||
            roster.GroupId != floor.GroupId || roster.Epoch != floor.Epoch ||
            roster.Revision != floor.Revision || roster.Signature != floor.Signature ||
            !new WorldAuthorityStore(data).TrustsCurrentRoster(roster) ||
            !SharedWorldRosterTrust.HasRole(roster, SharedWorldDeviceId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), grants => grants.Receive && grants.EligibleHost))
            return Denied("HostingPermissionRequired", "The owner must grant Can host this save to this PC.");
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
            version, verified, "HostingAllowed", "This PC has the owner's signed hosting permission.");
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
