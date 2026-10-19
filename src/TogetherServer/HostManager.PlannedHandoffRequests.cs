using System.Text.Json;

namespace TogetherServer;

public sealed record PlannedHandoffRequest(Guid SuccessorDeviceId);
public sealed record PlannedHandoffResponse(Guid RequestId, bool Accept);
public sealed record PlannedHandoffContinueRequest(Guid RequestId);
public sealed record PlannedHandoffSource(string Game, string WorldId,
    SharedWorldPortableSetup PortableSetup, string SetupIdentity);
internal sealed record PlannedHandoffPreparation(Guid RequestId, string? VersionHash,
    WorldPeerContactCard Contact, string SetupIdentity);
internal sealed record PlannedHandoffInvitation(int Schema, Guid RequestId, Guid ProfileId,
    Guid GroupId, string RosterHash, Guid SuccessorDeviceId, string State,
    string? PreparedVersionHash = null, WorldPeerContactCard? PreparedContact = null,
    PlannedHandoffSource? Source = null);

public sealed partial class HostManager
{
    private static string HandoffInvitationName(Guid profileId) => $"hosting-request-{profileId:N}.protected";

    private PlannedHandoffInvitation? ReadHandoffInvitation(Guid profileId)
    {
        var bytes = data.LoadProtected(HandoffInvitationName(profileId));
        if (bytes is null)
        {
            if (data.HasProtected(HandoffInvitationName(profileId)))
                throw new InvalidDataException("The hosting request needs review.");
            return null;
        }
        var request = bytes.Length <= 256 * 1024 ? JsonSerializer.Deserialize<PlannedHandoffInvitation>(bytes) : null;
        if (request is not { Schema: 1 } || request.ProfileId != profileId ||
            request.RequestId == Guid.Empty || request.SuccessorDeviceId == Guid.Empty ||
            request.GroupId == Guid.Empty || !SharedWorldPeerTrust.ValidHash(request.RosterHash) ||
            request.State is not ("Requested" or "Accepted" or "Declined" or "Prepared" or "Completed" or "Canceled") ||
            request.PreparedVersionHash is not null && !SharedWorldPeerTrust.ValidHash(request.PreparedVersionHash))
            throw new InvalidDataException("The hosting request needs review.");
        return request;
    }

    private void SaveHandoffInvitation(PlannedHandoffInvitation request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("The hosting request's portable setup exceeds its limit.");
        data.SaveProtected(HandoffInvitationName(request.ProfileId), bytes);
    }

    private PlannedHandoffSource HandoffSource(ServerProfile profile)
    {
        // Only reviewed server configuration and installed artifact identity are
        // inspected. No save bytes are read while the game is running.
        var snapshot = ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data));
        var portable = SharedWorldPortableSetupReader.Capture(snapshot);
        var identity = FriendLocalHostingService.PortableSetupIdentity(profile.Kind, profile.WorldId, portable);
        return new(profile.Kind, profile.WorldId, portable, identity);
    }

    private bool CurrentHandoffInvitation(PlannedHandoffInvitation request, out SharedWorldRoster? roster)
    {
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == request.ProfileId);
        roster = profile is null ? null : sharedWorlds.ReadRoster(profile);
        return profile?.SharedSavesEnabled == true && roster is not null &&
            roster.GroupId == request.GroupId && WorldAuthorityTrust.RosterHash(roster) == request.RosterHash &&
            pairing.TryCommitPlannedHandoff(request.ProfileId, request.SuccessorDeviceId, roster, () => { });
    }

    private SharedWorldPeerStore HandoffPeerStore(SharedWorldRoster roster)
    {
        var root = new SharedWorldPeerRoot(roster.OwnerPublicKey, roster.ProfileId, roster.GroupId);
        return new(data, root, new(root, (id, key) =>
            SharedWorldPeerTrust.AccessFromRoster(root, roster, id, key)));
    }

    private string? HandoffSuccessorAddress(SharedWorldRoster roster, Guid deviceId)
    {
        var member = roster.Members.SingleOrDefault(item => item.DeviceId == deviceId);
        var peer = HandoffPeerStore(roster).Project().Peers.SingleOrDefault(item =>
            item.DeviceId == deviceId && item.PublicKey == member?.PublicKey);
        if (peer?.HasConflict == true) return null;
        var prepared = ReadHandoffInvitation(roster.ProfileId)?.PreparedContact;
        if (prepared is not null && prepared.DeviceId == deviceId && prepared.PublicKey == member?.PublicKey &&
            prepared.Root == new SharedWorldPeerRoot(roster.OwnerPublicKey, roster.ProfileId, roster.GroupId) &&
            WorldPeerService.VerifyContact(prepared))
            return prepared.Routes.OrderBy(item => item.Address, StringComparer.Ordinal).Select(item => item.Address).FirstOrDefault();
        return peer is { Authorized: true, HasConflict: false } ? peer.Routes
            .Where(SharedWorldPeerTrust.ValidRoute).OrderBy(item => item.Address, StringComparer.Ordinal)
            .Select(item => item.Address).FirstOrDefault() : null;
    }

    private PlannedHandoffStatus InvitationStatus(PlannedHandoffInvitation request)
    {
        var current = CurrentHandoffInvitation(request, out _);
        var code = !current ? "SuccessorAccessChanged" : request.State switch
        {
            "Requested" => "WaitingForRecipient",
            "Accepted" => "RecipientSetupRequired",
            "Prepared" => "RecipientPrepared",
            "Declined" => "RecipientDeclined",
            "Completed" => "OldHostFenced",
            _ => "HandoffCanceled"
        };
        var message = !current ? "The selected PC's access or signed membership changed. Review permissions before moving hosting." : request.State switch
        {
            "Requested" => "Waiting for the selected PC to accept hosting. The game keeps running.",
            "Accepted" => "The selected PC accepted. Its game setup, save and incoming connection must pass preparation before hosting moves.",
            "Prepared" => "The selected PC finished preparation. The guided move can verify the final save and signed receipt.",
            "Declined" => "The selected PC declined hosting. The current Host was kept.",
            "Completed" => "Hosting moved safely. This PC is fenced; the receiving PC must start the server manually.",
            _ => "This hosting request was canceled. Existing saves were kept."
        };
        return new(false, code, message, request.SuccessorDeviceId,
            CanCancel: request.State is "Requested" or "Accepted" or "Prepared" or "Declined",
            RequestId: request.RequestId, RecipientState: request.State,
            PreparedVersionHash: request.PreparedVersionHash, Source: request.Source);
    }

    public async Task<PlannedHandoffResult> RequestPlannedHandoffAsync(Guid profileId, Guid successorDeviceId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            var roster = profile is null ? null : sharedWorlds.ReadRoster(profile);
            if (profile?.SharedSavesEnabled != true || roster is null)
                return new(false, "SharingOff", "Enable shared saves for this world first.");
            if (data.HasProtected(PlannedHandoffName(profileId)) || authority.HasState(profileId) ||
                SharedAuthorityBlocked(profileId, out _) ||
                ReadHandoffInvitation(profileId)?.State is "Requested" or "Accepted" or "Prepared")
                return new(false, "HandoffAlreadyPending", "Review the current hosting request first.");
            if (!pairing.TryCommitPlannedHandoff(profileId, successorDeviceId, roster, () => { }))
                return new(false, "SuccessorNotEligible", "Choose an approved paired PC allowed to receive and host this world.");
            SaveHandoffInvitation(new(1, Guid.NewGuid(), profileId, roster.GroupId,
                WorldAuthorityTrust.RosterHash(roster), successorDeviceId, "Requested", Source: HandoffSource(profile)));
            return new(true, "WaitingForRecipient", "Hosting was requested. The game keeps running while the selected PC decides.");
        }
        finally { gate.Release(); }
    }

    internal async Task<PlannedHandoffStatus> RecipientHandoffRequestAsync(Guid profileId, Guid deviceId)
    {
        var status = await PlannedHandoffStatusAsync(profileId);
        return status.SuccessorDeviceId == deviceId ? status :
            new(false, "NoHostingRequest", "There is no hosting request for this PC.");
    }

    internal async Task<PlannedHandoffResult> RespondPlannedHandoffAsync(Guid profileId, Guid deviceId,
        PlannedHandoffResponse response)
    {
        await gate.WaitAsync();
        try
        {
            var request = ReadHandoffInvitation(profileId);
            if (request is null || request.RequestId != response.RequestId || request.SuccessorDeviceId != deviceId ||
                request.State is not ("Requested" or "Accepted" or "Prepared") ||
                !CurrentHandoffInvitation(request, out _))
                return new(false, "HostingRequestChanged", "The hosting request or this PC's permission changed.");
            if (!response.Accept || request.State != "Prepared")
                SaveHandoffInvitation(request with { State = response.Accept ? "Accepted" : "Declined", PreparedVersionHash = null });
            return new(true, response.Accept ? "HostingAccepted" : "HostingDeclined", response.Accept ?
                "Hosting accepted. Prepare this PC's game setup and connection before the current Host stops." :
                "Hosting declined. The current Host and all saves were kept.");
        }
        finally { gate.Release(); }
    }

    internal async Task<PlannedHandoffResult> ConfirmHandoffPreparationAsync(Guid profileId, Guid deviceId,
        PlannedHandoffPreparation preparation)
    {
        await gate.WaitAsync();
        try
        {
            var request = ReadHandoffInvitation(profileId);
            if (request is null || request.RequestId != preparation.RequestId || request.SuccessorDeviceId != deviceId ||
                request.State is not ("Accepted" or "Prepared") || !CurrentHandoffInvitation(request, out var roster))
                return new(false, "HostingRequestChanged", "The hosting request or this PC's permission changed.");
            var profile = settings.Profiles.Single(item => item.Id == profileId);
            var latest = sharedWorlds.Status(profile).Latest;
            if (request.Source is null || preparation.SetupIdentity != request.Source.SetupIdentity ||
                HandoffSource(profile).SetupIdentity != request.Source.SetupIdentity)
                return new(false, "LocalServerSetupRequired", "The source game setup changed. Review and send a fresh hosting request.");
            if (latest is not null && (latest.VersionHash != preparation.VersionHash ||
                sharedWorlds.VerifiedReceipt(profile, latest, deviceId, roster!) is null) ||
                latest is null && (preparation.VersionHash is not null || data.HasProtected(PlannedHandoffName(profileId))))
                return new(false, "WaitingForSuccessorCopy", "Receive and confirm the exact current signed save before finishing preparation.");
            var contact = preparation.Contact;
            var member = roster!.Members.Single(item => item.DeviceId == deviceId);
            if (contact is null || contact.DeviceId != deviceId || contact.PublicKey != member.PublicKey ||
                contact.Root != new SharedWorldPeerRoot(roster.OwnerPublicKey, profileId, roster.GroupId) ||
                !WorldPeerService.VerifyContact(contact))
                return new(false, "RecipientConnectionRequired", "Configure this PC's incoming connection, then prepare again.");
            if (HandoffPeerStore(roster).Project().Peers.Any(peer => peer.DeviceId == deviceId &&
                peer.PublicKey == member.PublicKey && peer.HasConflict))
                return new(false, "RecipientConnectionRequired", "Conflicting signed connection information needs review before hosting moves.");
            if (data.LoadProtected(PlannedHandoffName(profileId)) is { } pendingBytes)
            {
                var pending = JsonSerializer.Deserialize<PendingPlannedHandoff>(pendingBytes);
                var route = contact.Routes.SingleOrDefault(item => item.Address == pending?.SuccessorAddress);
                if (pending is null || route is null || route.CertificateSha256 != pending.SuccessorTlsFingerprint)
                    return new(false, "RecipientConnectionChanged", "This PC's signed connection changed after the final save. Cancel and review the move before continuing.");
            }
            SaveHandoffInvitation(request with { State = "Prepared", PreparedVersionHash = latest?.VersionHash, PreparedContact = contact });
            return new(true, "RecipientPrepared", "This PC's preparation and exact-copy receipt were confirmed. Start remains manual.", latest);
        }
        finally { gate.Release(); }
    }

    internal async Task<PlannedHandoffResult> BeginHandoffPreparationAsync(Guid profileId, Guid deviceId, Guid requestId)
    {
        await gate.WaitAsync();
        try
        {
            var request = ReadHandoffInvitation(profileId);
            if (request is null || request.RequestId != requestId || request.SuccessorDeviceId != deviceId ||
                request.State is not ("Accepted" or "Prepared") || !CurrentHandoffInvitation(request, out _))
                return new(false, "HostingRequestChanged", "The hosting request or this PC's permission changed.");
            SaveHandoffInvitation(request with { State = "Accepted", PreparedVersionHash = null });
            return new(true, "RecipientSetupRequired", "Current preparation is being checked. Hosting waits for its exact-copy confirmation.");
        }
        finally { gate.Release(); }
    }

    public async Task<PlannedHandoffResult> ContinuePlannedHandoffAsync(Guid profileId, Guid requestId)
    {
        var status = await PlannedHandoffStatusAsync(profileId);
        if (status.RequestId != requestId || requestId == Guid.Empty)
            return new(false, "HostingRequestChanged", "Review the current hosting request.");
        if (status.RecipientState == "Completed") return new(true, status.Code, status.Message);
        if (status.RecipientState != "Prepared") return new(false, status.Code, status.Message);
        if (!status.Pending) return await PreparePlannedHandoffAsync(profileId, status.SuccessorDeviceId!.Value, requestId);
        return status.CanComplete ? await CompletePlannedHandoffAsync(profileId, requestId) : new(false, status.Code, status.Message);
    }
}
