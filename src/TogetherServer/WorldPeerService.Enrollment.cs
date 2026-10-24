using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record WorldPeerSessionEnrollmentRequest(Guid SessionId);
public sealed record WorldPeerSessionEnrollment(PairingCredential Credential,
    Guid LocalProfileId, Guid SessionId, string BranchHash, Guid MemberDeviceId);

internal sealed partial class WorldPeerService
{
    // Session credentials authorize one stored world's identity, not a profile
    // GUID shared by unrelated roots. This is deliberately independent of the
    // candidate's current hosting grant: the original owner can still request
    // guarded Stop for its running world after future hosting is withdrawn.
    internal static bool SessionProfileMatchesRoot(LocalData data, ServerProfile profile,
        SharedWorldPeerRoot root, string? branchHash)
    {
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                if (!SharedWorldPeerTrust.ValidRoot(root) || profile.WorldLoadRehearsalId is not null)
                    return false;
                if (branchHash is not null)
                {
                    if (!SharedWorldPeerTrust.ValidHash(branchHash) ||
                        profile.SeparateCopySourceProfileId != root.ProfileId ||
                        profile.SeparateCopyBranchHash != branchHash) return false;
                    var branch = new SharedWorldSeparateCopyStore(data).Read(root.ProfileId)
                        .SingleOrDefault(item => item.BranchHash == branchHash);
                    return branch is not null && SharedWorldSeparateCopyStore.Verify(branch) &&
                        branch.Offer.Roster.OwnerPublicKey == root.OwnerPublicKey &&
                        branch.Offer.Roster.ProfileId == root.ProfileId && branch.Offer.Roster.GroupId == root.GroupId &&
                        profile.Kind == branch.Offer.Version.Game && profile.WorldId == branch.Offer.Version.WorldId;
                }
                if (profile.Id != root.ProfileId || profile.SeparateCopySourceProfileId is not null ||
                    profile.SeparateCopyBranchHash is not null) return false;
                var authority = new WorldAuthorityStore(data).LocalAuthorizedHead(profile.Id);
                // ReadRoster must never generate a local owner identity while
                // authorizing an unrelated unbound profile.
                if (authority is null)
                {
                    var bytes = data.LoadProtected("shared-world-signing-key.protected");
                    if (bytes is null) return false;
                    using var key = ECDsa.Create();
                    key.ImportPkcs8PrivateKey(bytes, out var read);
                    if (read != bytes.Length || Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != root.OwnerPublicKey)
                        return false;
                }
                var roster = new SharedWorldService(data, new SessionRosterReadOnlyCapture()).ReadRoster(profile);
                return roster is not null && roster.OwnerPublicKey == root.OwnerPublicKey &&
                    roster.ProfileId == root.ProfileId && roster.GroupId == root.GroupId;
            }
        }
        catch (Exception error) when (PeerFailure(error) || error is InvalidOperationException or NullReferenceException)
        { return false; }
    }

    private sealed class SessionRosterReadOnlyCapture : ISharedWorldCaptureAdapter
    {
        public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId) =>
            throw new InvalidOperationException("Session identity reads cannot capture world files.");
    }

    internal static PairingDecision CredentialMembershipDecision(LocalData data, PairedDevice device,
        DateTimeOffset now, Func<Guid, Guid, PairingDecision>? originalAccess = null)
    {
        static PairingDecision Review() => new(false, "PeerMembershipReviewRequired",
            "Current signed world membership needs catch-up or local review. Saved access was kept.");
        static PairingDecision Removed() => new(false, "Revoked",
            "This PC's signed world identity or assigned server access was removed.");
        try
        {
            if (device.PeerWorldGroupKey is not { } groupKey || device.PeerWorldSourceProfileId is not { } source ||
                device.PeerWorldSourceDeviceId is not { } sourceMember ||
                !SharedWorldPeerTrust.ValidHash(groupKey) || !SharedWorldRosterTrust.ValidKey(device.SharedWorldPublicKey)) return Review();
            var bytes = data.LoadProtected(RegistryName);
            if (bytes is null || bytes.Length > 8 * 1024 * 1024) return Review();
            var registry = JsonSerializer.Deserialize<Registry>(bytes, Json);
            var group = registry?.Groups.SingleOrDefault(item => item.Root.GroupKey == groupKey && item.Root.ProfileId == source);
            if (registry?.Schema != 1 || group is null || !SharedWorldPeerTrust.ValidRoot(group.Root) ||
                !SharedWorldRosterTrust.Verify(group.Roster) || group.Roster.OwnerPublicKey != group.Root.OwnerPublicKey ||
                group.Roster.ProfileId != group.Root.ProfileId || group.Roster.GroupId != group.Root.GroupId) return Review();
            var chain = new SharedWorldRosterChainStore(data);
            var heads = chain.Heads(source);
            if (heads.Count == 0 && chain.HasState(source) || heads.Count > 1 || heads.Count == 1 &&
                (heads[0].OwnerPublicKey != group.Root.OwnerPublicKey || heads[0].GroupId != group.Root.GroupId ||
                 heads[0].Revision < group.Roster.Revision ||
                 heads[0].Revision == group.Roster.Revision && heads[0].Signature != group.Roster.Signature)) return Review();
            // A complete, unique newer signed roster can explicitly remove a
            // member immediately. Every other unseen revision stays a temporary
            // catch-up denial; it must never become a permanent local revocation.
            var currentRoster = heads.Count == 1 ? heads[0] : group.Roster;
            if (sourceMember == Guid.Empty)
            {
                if (device.SharedWorldPublicKey != group.Root.OwnerPublicKey) return Removed();
            }
            else
            {
                var member = currentRoster.Members.SingleOrDefault(item => item.DeviceId == sourceMember);
                if (member is null || member.Revoked || member.PublicKey != device.SharedWorldPublicKey) return Removed();
            }
            if (heads.Count == 1 && heads[0].Revision > group.Roster.Revision) return Review();
            if (sourceMember != Guid.Empty && currentRoster.Members.Single(item => item.DeviceId == sourceMember)
                    .AccessExpiresUtc is { } expires && expires <= now)
                return new(false, "AccessExpired", "The owner-defined world membership deadline has passed.");
            var ownerBytes = data.LoadProtected("shared-world-signing-key.protected");
            if (ownerBytes is not null && sourceMember != Guid.Empty)
            {
                using var ownerKey = ECDsa.Create();
                ownerKey.ImportPkcs8PrivateKey(ownerBytes, out var read);
                if (read != ownerBytes.Length) return Review();
                if (Convert.ToBase64String(ownerKey.ExportSubjectPublicKeyInfo()) == group.Root.OwnerPublicKey)
                {
                    var original = originalAccess?.Invoke(sourceMember, source) ?? Review();
                    if (!original.Ok) return original;
                }
            }
            var profile = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == device.ProfileId);
            if (data.Recovery.Notices.Any(notice => notice.StateFile == "host.json")) return Review();
            if (profile is null || device.AssignedProfileIds?.Contains(device.ProfileId) != true) return Removed();
            if (!SessionProfileMatchesRoot(data, profile, group.Root, device.PeerWorldBranchHash)) return Review();
            return new(true, "PeerMembershipActive", "This PC remains an approved member of the selected world.");
        }
        catch (Exception error) when (PeerFailure(error) || error is InvalidOperationException or NullReferenceException)
        { return Review(); }
    }

    private async Task<WorldPeerSessionEnrollment?> EnrollSessionAsync(Group group,
        WorldPeerRequest request, byte[] payload, CancellationToken cancellationToken)
    {
        var input = WorldPeerProtocol.DecodeEnvelope<WorldPeerSessionEnrollmentRequest>(payload, 512);
        if (input.SessionId == Guid.Empty || !group.SharingEnabled) return null;
        var snapshot = await manager.SnapshotAsync();
        var run = snapshot.Runs.SingleOrDefault(item => item.RunOperationId == input.SessionId);
        var profile = run is null ? null : snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == run.ProfileId);
        if (profile is null || run!.State is not ("Ready" or "Starting" or "Listening" or "Process running") ||
            !SessionProfileMatchesRoot(data, profile, group.Root, profile.SeparateCopyBranchHash)) return null;
        var own = Access(group, group.DeviceId, Convert.ToBase64String(LocalPublicKey(group)));
        if (own is not { Receive: true, EligibleHost: true }) return null;
        var branch = profile.SeparateCopyBranchHash;
        var credential = pairing.EnrollPeerWorld(group.Root, group.Roster, request.Challenge.DeviceId,
            request.Challenge.PublicKey, profile.Id, branch);
        if (credential is null) return null;
        var branchHash = branch ?? NormalSessionBranchHash(group);
        return new(credential, profile.Id, input.SessionId, branchHash, request.Challenge.DeviceId);
    }

    private byte[] LocalPublicKey(Group group)
    {
        using var key = LocalKey(group) ?? throw new InvalidDataException("World membership is unavailable.");
        return key.ExportSubjectPublicKeyInfo();
    }

    internal async Task<WorldPeerActionResult> ConnectSessionAsync(string groupKey, Guid deviceId,
        Guid sessionId, CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var group = Find(groupKey);
            if (group is null || sessionId == Guid.Empty) return new(false, "UnknownWorldSession", "Choose a known world session.");
            var state = Store(group).Project();
            var session = state.Sessions.SingleOrDefault(item => item.DeviceId == deviceId &&
                item.Session.SessionId == sessionId && item.Authorized && !item.HasConflict);
            var peer = state.Peers.SingleOrDefault(item => item.DeviceId == deviceId && item.Authorized && !item.HasConflict);
            if (session is null || peer is null || session.Session.State is not ("Hosting" or "Preparing") || peer.Routes.Count == 0)
                return new(false, "PeerSessionUnavailable", "This session has no currently trusted peer address. Refresh or ask its Host for a signed contact code.");
            foreach (var route in peer.Routes)
            {
                if (await friend.SelectExistingPeerAsync(route.Address, group.Root, group.DeviceId,
                    session.Session.BranchHash, deviceId == Guid.Empty))
                    return new(true, "PeerSessionSelected", "The saved Host connection was selected. Existing game sessions were left running.");
                try
                {
                    var bytes = await SendPeerAsync(group, route, WorldPeerOperations.Enrollment,
                        cancellationToken, JsonSerializer.SerializeToUtf8Bytes(new WorldPeerSessionEnrollmentRequest(sessionId), Json));
                    var enrollment = WorldPeerProtocol.DecodeEnvelope<WorldPeerSessionEnrollment>(bytes, 8192);
                    if (enrollment.SessionId != sessionId || enrollment.LocalProfileId == Guid.Empty ||
                        enrollment.BranchHash != session.Session.BranchHash ||
                        enrollment.MemberDeviceId != group.DeviceId || enrollment.Credential.DeviceId == Guid.Empty ||
                        enrollment.Credential.ExpiresUtc <= DateTimeOffset.UtcNow ||
                        enrollment.Credential.Credential.Length is < 32 or > 128)
                        throw new InvalidDataException("The Host did not enroll the exact selected world session.");
                    var result = await friend.AdoptPeerConnectionAsync(group.Root, group.Roster,
                        group.DeviceId, group.ReceiveConsented, route, enrollment, cancellationToken);
                    return new(result.Ok, result.Code, result.Message);
                }
                catch (Exception error) when (PeerFailure(error)) { }
            }
            return new(false, "PeerSessionUnreachable", "This PC could not verify the selected Host. Its cached session and both save histories were kept.");
        }
        finally { refreshGate.Release(); }
    }
}

public sealed partial class PairingService
{
    // Guid.Empty denotes the original owner only after AuthorizationDecision
    // verifies this credential's current signed root, owner key and exact local
    // profile binding. Never turn a member's hosting or sharing grant into Stop.
    private static bool IsOriginalOwnerSession(PairedDevice device, Guid profileId) =>
        device.PeerWorldGroupKey is not null && device.PeerWorldSourceDeviceId == Guid.Empty &&
        device.ProfileId == profileId;

    private PairingDecision PeerMembershipDecision(PairedDevice alias, DateTimeOffset now) =>
        WorldPeerService.CredentialMembershipDecision(data, alias, now, (sourceId, profileId) =>
        {
            if (data.Recovery.Notices.Any(notice => notice.StateFile == "pairing-state.protected"))
                return new(false, "PeerMembershipReviewRequired", "The original protected pairing state needs review.");
            var source = devices.SingleOrDefault(item => item.Id == sourceId && item.PeerWorldGroupKey is null);
            if (source is null || source.Revoked || source.CredentialHash is null ||
                source.SharedWorldPublicKey != alias.SharedWorldPublicKey ||
                source.AssignedProfileIds?.Contains(profileId) != true)
                return new(false, "Revoked", "The owner's original pairing identity or server assignment was removed.");
            // An alias never recursively authorizes another alias. Its canonical
            // parent is checked directly under the PairingService's existing lock.
            if (source.SuccessorAuthorityHash is { } authorityHash)
            {
                try
                {
                    if (new WorldAuthorityStore(data).LocalAuthorizedHead(source.ProfileId)?.RecordHash != authorityHash)
                        return new(false, "PeerMembershipReviewRequired", "The source hosting authority needs review.");
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
                    CryptographicException or UnauthorizedAccessException)
                { return new(false, "PeerMembershipReviewRequired", "The source hosting authority is unavailable."); }
            }
            else if (source.ProfileId == Guid.Empty ? serverInvites.Any(invite => invite.Rotated) :
                serverInvites.SingleOrDefault(invite => invite.ProfileId == source.ProfileId)?.Generation != source.InviteGeneration)
                return new(false, "Revoked", "The owner replaced the server code that issued the original pairing.");
            if (source.ApprovalPending)
                return new(false, "ApprovalPending", "The owner must approve the original PC access.");
            if (source.CredentialExpiresUtc <= now)
                return new(false, "Expired", "The original PC credential has expired.");
            if (IsAccessExpired(source, now))
                return new(false, "AccessExpired", "The owner-defined access deadline has passed.");
            if (SharedRosterDirty(profileId))
                return new(false, "PeerMembershipReviewRequired", "The owner's current signed membership is pending review.");
            return new(true, "CanonicalSourceActive", "The original owner-approved pairing remains active.");
        });

    internal PairingCredential? EnrollPeerWorld(SharedWorldPeerRoot root, SharedWorldRoster roster,
        Guid deviceId, string publicKey, Guid localProfileId, string? branchHash)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var access = SharedWorldPeerTrust.AccessFromRoster(root, roster, deviceId, publicKey, clock);
            if (access is null || localProfileId == Guid.Empty) return null;
            var existing = devices.SingleOrDefault(item => item.PeerWorldSourceDeviceId == deviceId &&
                item.PeerWorldGroupKey == root.GroupKey && item.ProfileId == localProfileId);
            if (existing is not null && (existing.Revoked || existing.PeerWorldGroupKey != root.GroupKey ||
                existing.SharedWorldPublicKey != publicKey || existing.ApprovalPending ||
                existing.PeerWorldBranchHash != branchHash ||
                existing.AssignedProfileIds?.Contains(localProfileId) != true ||
                existing.AccessExpiresUtc is { } deadline && deadline <= UtcNow)) return null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var expiry = UtcNow.AddDays(90);
            var enrolled = existing ?? new PairedDevice
            {
                Id = Guid.NewGuid(),
                ProfileId = localProfileId,
                Name = "Shared world member",
                AssignedProfileIds = [localProfileId],
                SharedWorldPublicKey = publicKey,
                ServerPermissionOverrides = [],
                PeerWorldGroupKey = root.GroupKey,
                PeerWorldSourceProfileId = root.ProfileId,
                PeerWorldSourceDeviceId = deviceId
            };
            enrolled.ProfileId = localProfileId;
            enrolled.AssignedProfileIds = [localProfileId];
            enrolled.PeerWorldBranchHash = branchHash;
            if (!PeerMembershipDecision(enrolled, UtcNow).Ok) return null;
            enrolled.CredentialHash = Hash(token); enrolled.CredentialExpiresUtc = expiry;
            enrolled.PreviousCredentialHash = null; enrolled.PreviousCredentialExpiresUtc = null;
            // Current signed membership enforces the owner's deadline on every
            // request. AccessExpiresUtc is an independent local Host restriction;
            // do not freeze an old signed deadline here or relax a local one.
            renewalReceipts.RemoveAll(item => item.DeviceId == enrolled.Id);
            if (existing is null) devices.Add(enrolled);
            SaveState();
            return new(enrolled.Id, token, expiry);
        }
    }
}

public sealed partial class FriendService
{
    internal async Task<bool> SelectExistingPeerAsync(string endpoint, SharedWorldPeerRoot root,
        Guid deviceId, string branchHash, bool allowOriginal)
    {
        (Guid Id, FriendLink Link)[] current;
        lock (sync) current = disposed ? [] : [.. links];
        foreach (var (id, link) in current)
        {
            if (!await link.MatchesPeerAsync(endpoint, root, deviceId, branchHash, allowOriginal)) continue;
            var verified = await link.PollAsync();
            if (verified.ConnectionCode is not null || !verified.ProtocolCompatible ||
                verified.State is not ("Connected" or "Disabled") || verified.LastConnectedUtc is null ||
                !await link.MatchesPeerAsync(endpoint, root, deviceId, branchHash, allowOriginal)) continue;
            return Select(id).Ok;
        }
        return false;
    }

    internal async Task<FriendActionResult> AdoptPeerConnectionAsync(SharedWorldPeerRoot root,
        SharedWorldRoster roster, Guid deviceId, bool consent, SharedWorldPeerRoute route,
        WorldPeerSessionEnrollment enrollment, CancellationToken cancellationToken)
    {
        if (!TryRetain()) return ClosedAction();
        try
        {
            var id = Guid.NewGuid();
            var config = new FriendConfiguration
            {
                DisplayName = "Shared world Host",
                Endpoint = route.Address,
                Fingerprint = route.CertificateSha256,
                AcceptedFingerprints = [route.CertificateSha256],
                DeviceId = enrollment.Credential.DeviceId,
                Credential = enrollment.Credential.Credential,
                CredentialExpiresUtc = enrollment.Credential.ExpiresUtc,
                PeerWorld = root,
                PeerWorldMemberDeviceId = deviceId,
                PeerWorldBranchHash = enrollment.BranchHash,
                PeerWorldLocalProfileId = enrollment.LocalProfileId,
                SharedWorldSigningKeys = new() { [root.ProfileId] = root.OwnerPublicKey },
                SharedRosterFloors = new() { [root.ProfileId] = new(root.GroupId, roster.Epoch, roster.Revision, roster.Signature) },
                ApprovedSharedWorldGroups = new() { [root.ProfileId] = root.GroupId },
                ConsentedSharedWorldProfiles = consent ? [root.ProfileId] : []
            };
            data.SaveProtected(FileName(id), JsonSerializer.SerializeToUtf8Bytes(config, Json));
            data.SaveProtected($"shared-world-roster-{deviceId:N}-{root.ProfileId:N}.protected",
                JsonSerializer.SerializeToUtf8Bytes(roster, Json));
            var link = new FriendLink(data, FileName(id), clientFactory);
            lock (sync) { links.Add((id, link)); selectedId = id; SaveIndex(); }
            var verified = await link.PollAsync();
            var connected = verified.ConnectionCode is null && verified.ProtocolCompatible &&
                verified.State is "Connected" or "Disabled" && verified.LastConnectedUtc is not null;
            return connected
                ? new(true, "PeerSessionConnected", "The selected Host connection was verified and saved with this PC's own credential. No game was launched or stopped.", null)
                : new(true, "PeerSessionSaved", "The selected Host credential was saved, but its current connection could not be verified. Check the connection before joining. No game was launched or stopped.", null);
        }
        finally { ReleaseRetained(); }
    }
}

internal sealed partial class FriendLink
{
    internal async Task<bool> MatchesPeerAsync(string endpoint, SharedWorldPeerRoot root,
        Guid deviceId, string branchHash, bool allowOriginal)
    {
        await gate.WaitAsync();
        try
        {
            return config is not null && !config.KnownRevoked &&
            config.CredentialExpiresUtc > DateTimeOffset.UtcNow && SharedWorldDeviceId == deviceId &&
            config.SharedWorldSigningKeys.GetValueOrDefault(root.ProfileId) == root.OwnerPublicKey &&
            config.ApprovedSharedWorldGroups.GetValueOrDefault(root.ProfileId) == root.GroupId &&
            (config.PeerWorld == root && config.PeerWorldBranchHash == branchHash ||
             allowOriginal && config.PeerWorld is null) &&
            config.Endpoint.TrimEnd('/').Equals(endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }
        finally { gate.Release(); }
    }
}
