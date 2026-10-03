using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    internal async Task<PlannedHandoffStageResult> StagePlannedHandoffAsync(Guid profileId,
        CancellationToken cancellationToken)
    {
        if (!TryRetain())
            return new(false, "ConnectionClosed", "This saved Host connection is closing.");
        var singleFlight = sharedProfileGates.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        var enteredFlight = false;
        try
        {
            await singleFlight.WaitAsync(cancellationToken);
            enteredFlight = true;
            string endpoint;
            string credential;
            string[] pins;
            string vault;
            string ownerKey;
            Guid groupId;
            Guid deviceId;
            SharedRosterFloor floor;
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                    !view.Profiles.Any(item => item.Id == profileId) ||
                    config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) is not { } group ||
                    config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) is not { } pinned ||
                    config.SharedRosterFloors?.GetValueOrDefault(profileId) is not { } savedFloor)
                    return new(false, "VerifiedCopyRequired",
                        "Allow shared saves and receive a verified copy from this Host first.");
                endpoint = config.Endpoint;
                credential = config.Credential;
                pins = AcceptedPins().ToArray();
                vault = ReceivedRoot(profileId);
                ownerKey = pinned;
                groupId = group;
                deviceId = config.DeviceId;
                floor = savedFloor;
            }
            finally { gate.Release(); }
            using var client = MakeClient(endpoint, pins);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
            client.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
            client.DefaultRequestHeaders.Add(CompanionProtocol.HeaderName,
                CompanionProtocol.Current.ToString());
            using var response = await client.GetAsync(
                $"api/companion/servers/{profileId}/shared-world/handoff/offer",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var bytes = await ReadBoundedSharedAsync(response.Content, 512 * 1024,
                cancellationToken);
            if (!response.IsSuccessStatusCode || bytes is null)
                return new(false, "HandoffOfferUnavailable",
                    "The old Host has not completed a signed handoff for this PC.");
            var record = JsonSerializer.Deserialize<WorldAuthorityRecord>(bytes, Json);
            if (record is null || !WorldAuthorityTrust.Verify(record) ||
                record.Roster.Epoch < floor.Epoch || record.Roster.Revision < floor.Revision ||
                record.Roster.Epoch == floor.Epoch && record.Roster.Revision == floor.Revision &&
                    record.Roster.Signature != floor.Signature)
                return new(false, "HandoffProofInvalid",
                    "The signed handoff or membership history did not pass verification.");
            using var key = LoadPcSigningKey(deviceId);
            return await PlannedHandoffReceiver.StageAsync(data, vault, record, profileId,
                groupId, ownerKey, deviceId, key, async () =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        return config is not null && config.DeviceId == deviceId &&
                            config.Endpoint == endpoint && config.Credential == credential &&
                            AcceptedPins().SequenceEqual(pins) &&
                            config.ConsentedSharedWorldProfiles.Contains(profileId) &&
                            config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) == groupId &&
                            config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) == ownerKey &&
                            !withdrawnSharedConsent.ContainsKey(profileId);
                    }
                    finally { gate.Release(); }
                }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(false, "HandoffInterrupted", "The staged copy was interrupted and can be retried."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or HttpRequestException)
        { return new(false, "HandoffFailed", "The handoff proof or staged copy failed verification: " + ex.Message); }
        finally
        {
            if (enteredFlight) singleFlight.Release();
            ReleaseRetained();
        }
    }
}
