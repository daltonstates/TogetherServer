using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    private async Task<T?> HandoffExchangeAsync<T>(Guid profileId, string action, object? body,
        CancellationToken cancellationToken)
    {
        if (!TryRetain()) return default;
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (config is null || config.KnownRevoked || view.State is not ("Connected" or "Disabled") ||
                    view.ConnectionCode is "Revoked" or "AccessExpired" or "ApprovalPending" ||
                    !view.Profiles.Any(item => item.Id == profileId)) return default;
                using var requestClient = makeClient(config.Endpoint, AcceptedPins());
                requestClient.DefaultRequestHeaders.Authorization = new("Bearer", config.Credential);
                requestClient.DefaultRequestHeaders.Add("X-Device-Id", config.DeviceId.ToString());
                requestClient.DefaultRequestHeaders.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
                var path = $"api/companion/servers/{profileId}/shared-world/handoff/{action}";
                using var response = body is null ? await requestClient.GetAsync(path,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken) :
                    await requestClient.PostAsJsonAsync(path, body, Json, cancellationToken);
                var bytes = await ReadBoundedSharedAsync(response.Content, 256 * 1024, cancellationToken);
                if (bytes is null || (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Conflict)) return default;
                return JsonSerializer.Deserialize<T>(bytes, Json);
            }
            finally { gate.Release(); }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or
            CryptographicException or InvalidDataException or UnauthorizedAccessException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { return default; }
        finally { ReleaseRetained(); }
    }

    internal async Task<PlannedHandoffStatus?> HandoffRequestAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var status = await HandoffExchangeAsync<PlannedHandoffStatus>(profileId, "request", null, cancellationToken);
        return status?.RequestId is null ? null : status;
    }

    internal async Task<bool> MatchesPlannedAuthorityAsync(WorldAuthorityRecord record, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return config is not null && !config.KnownRevoked && config.DeviceId == record.SuccessorReceipt?.DeviceId &&
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(record.Proposal.ProfileId) == record.Proposal.GroupId &&
                config.SharedWorldSigningKeys?.GetValueOrDefault(record.Proposal.ProfileId) == record.Roster.OwnerPublicKey &&
                ReadLocalHostingAccess(record.Proposal.ProfileId, false).Allowed;
        }
        finally { gate.Release(); }
    }

    internal async Task<PlannedHandoffResult> RespondHandoffRequestAsync(Guid profileId,
        PlannedHandoffResponse response, CancellationToken cancellationToken) =>
        await HandoffExchangeAsync<PlannedHandoffResult>(profileId, "respond", response, cancellationToken) ??
        new(false, "HostingRequestUnavailable", "The current Host could not confirm this hosting choice. Retry before preparing.");

    internal async Task<PlannedHandoffResult> RequestOwnerHandoffRouteCheckAsync(Guid profileId,
        Guid requestId, CancellationToken cancellationToken) =>
        await HandoffExchangeAsync<PlannedHandoffResult>(profileId, "check-route", new PlannedHandoffContinueRequest(requestId), cancellationToken) ??
        new(false, "SuccessorRoutePending", "The old Host could not verify this PC's pinned HTTPS connection. Keep it reachable and retry preparation.");

    internal async Task<PlannedHandoffResult> BeginHandoffPreparationAsync(Guid profileId,
        Guid requestId, CancellationToken cancellationToken) =>
        await HandoffExchangeAsync<PlannedHandoffResult>(profileId, "preparing", new PlannedHandoffContinueRequest(requestId), cancellationToken) ??
        new(false, "HostingRequestUnavailable", "The current Host could not confirm preparation. Retry safely.");

    internal async Task<TakeoverReadiness> CheckHandoffLocalSetupAsync(Guid profileId, TakeoverLocalSetup setup,
        bool ownsControlListener, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var access = ReadLocalHostingAccess(profileId, false);
            if (!access.Allowed || !access.Consented || config is null)
                return new(false, [access.Message], null, null);
            // This is explicitly a local preflight. Route, load and join evidence
            // stays with the signed restore/Start gates and is never inferred here.
            return SharedWorldReadiness.Check(ReceivedRoot(profileId),
                Path.Combine(data.RootPath, "shared-world-rehearsals"), setup,
                new(true, true, true, true, true, true),
                config.SharedWorldSigningKeys?.GetValueOrDefault(profileId),
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId),
                ownedControlListener: ownsControlListener, requiredCopies: 3,
                authorityRecords: new WorldAuthorityStore(data).Read(profileId));
        }
        finally { gate.Release(); }
    }

    internal async Task<PlannedHandoffResult> ConfirmHandoffLocalPreparationAsync(Guid profileId,
        Guid requestId, string? versionHash, string setupIdentity, SharedWorldPeerRoute route, CancellationToken cancellationToken)
    {
        WorldPeerContactCard contact;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var access = ReadLocalHostingAccess(profileId, false);
            if (!access.Allowed || !access.Consented || config is null || !SharedWorldPeerTrust.ValidRoute(route))
                return new(false, "HostingPermissionChanged", "Review current hosting permission and this PC's connection.");
            var root = new SharedWorldPeerRoot(config.SharedWorldSigningKeys[profileId], profileId,
                config.ApprovedSharedWorldGroups[profileId]);
            using var key = LoadPcSigningKey(config.DeviceId);
            contact = new(1, root, config.DeviceId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                1, [route], "");
            contact = contact with
            {
                Signature = Convert.ToBase64String(key.SignData(
                WorldPeerService.ContactBasis(contact), HashAlgorithmName.SHA256))
            };
        }
        finally { gate.Release(); }
        return await HandoffExchangeAsync<PlannedHandoffResult>(profileId, "prepared",
            new PlannedHandoffPreparation(requestId, versionHash, contact, setupIdentity), cancellationToken) ??
            new(false, "HostingRequestUnavailable", "This PC's preparation could not be confirmed by the current Host. Retry safely.");
    }
}
