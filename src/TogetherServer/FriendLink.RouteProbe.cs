using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    internal async Task<SharedWorldRouteCheck> ProbeSuccessorRouteAsync(Guid profileId,
        string recordHash, string tlsFingerprint, CancellationToken cancellationToken)
    {
        SharedWorldRouteCheck Fail(string code, string message) =>
            new(false, code, message, DateTimeOffset.UtcNow, recordHash);
        if (!TryRetain()) return Fail("ConnectionClosed", "This Friend connection is closing.");
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                    recordHash.Length != 64 || !recordHash.All(Uri.IsHexDigit) ||
                    !ValidFingerprint(tlsFingerprint))
                    return Fail("RouteCheckUnavailable", "Choose a trusted shared world and its successor certificate fingerprint.");
                recordHash = recordHash.ToUpperInvariant();
                tlsFingerprint = tlsFingerprint.ToUpperInvariant();
                var authority = new WorldAuthorityStore(data);
                var record = authority.ReadUniqueHead(profileId);
                if (record is null || record.RecordHash != recordHash ||
                    !WorldAuthorityTrust.Verify(record) ||
                    record.Roster.OwnerPublicKey !=
                        config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) ||
                    record.Proposal.GroupId !=
                        config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId))
                    return Fail("HandoffProofInvalid", "This PC has not verified the signed takeover history.");
                var candidate = record.Roster.Members.SingleOrDefault(item =>
                    item.PublicKey == WorldAuthorityTrust.CandidateDevicePublicKey(record.Proposal));
                if (candidate is not { Revoked: false, Grants.EligibleHost: true } ||
                    record.Proposal.Kind == "Planned" && !candidate.Grants.Receive ||
                    candidate.AccessExpiresUtc is { } candidateExpiry && candidateExpiry <= DateTimeOffset.UtcNow)
                    return Fail("HandoffProofInvalid", "The signed successor no longer has current hosting permission.");
                var observer = record.Roster.Members.SingleOrDefault(item => item.DeviceId == config.DeviceId);
                if (observer is not { Revoked: false } ||
                    !(observer.Grants.Receive || observer.Grants.RecoveryVoter) ||
                    observer.AccessExpiresUtc is { } observerExpiry && observerExpiry <= DateTimeOffset.UtcNow)
                    return Fail("HandoffProofInvalid", "This PC no longer has current observer permission.");
                if (candidate.DeviceId == config.DeviceId || data.HasProtected(
                    $"shared-world-pc-signing-{candidate.DeviceId:N}.protected"))
                    return Fail("OtherFriendRequired", "Run this direct-IP route check from another Friend PC.");
                if (!SharedWorldRouteTrust.DirectIpAddress(record.Proposal.CandidateAddress))
                    return Fail("DirectIpRequired", "The successor needs a reviewed direct-IP HTTPS address.");
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                using var observerKey = LoadPcSigningKey();
                var challenge = SharedWorldRouteTrust.SignChallenge(record,
                    nonce, config.DeviceId, observerKey);
                if (!SharedWorldRouteTrust.VerifyChallenge(challenge, record, DateTimeOffset.UtcNow))
                    return Fail("HandoffProofInvalid", "This PC's signing identity does not match the current signed roster.");
                using var client = MakeClient(record.Proposal.CandidateAddress, [tlsFingerprint]);
                using var response = await client.PostAsJsonAsync(
                    $"api/companion/servers/{profileId}/shared-world/route-proof/{recordHash}",
                    challenge, Json, cancellationToken);
                var bytes = await ReadBoundedSharedAsync(response.Content, 2048, cancellationToken);
                var proof = response.IsSuccessStatusCode && bytes is not null ?
                    JsonSerializer.Deserialize<SharedWorldRouteProof>(bytes, Json) : null;
                var current = authority.ReadUniqueHead(profileId);
                if (current?.RecordHash != recordHash)
                    return Fail("HandoffProofInvalid", "The signed handoff was superseded or its history needs review.");
                if (!SharedWorldRouteTrust.VerifyChallenge(challenge, current, DateTimeOffset.UtcNow) ||
                    current.Roster.Members.Single(item => item.PublicKey ==
                        WorldAuthorityTrust.CandidateDevicePublicKey(current.Proposal))
                        .AccessExpiresUtc is { } currentExpiry && currentExpiry <= DateTimeOffset.UtcNow)
                    return Fail("HandoffProofInvalid", "Current signed membership or permission changed.");
                if (!SharedWorldRouteTrust.Verify(proof, current, nonce, tlsFingerprint))
                    return Fail("RouteProofInvalid",
                        "The successor did not return a valid signed route proof from the pinned address.");
                using var confirmed = await client.PostAsJsonAsync(
                    $"api/companion/servers/{profileId}/shared-world/route-confirm/{recordHash}",
                    new SharedWorldRouteConfirmation(challenge, proof!), Json, cancellationToken);
                return confirmed.IsSuccessStatusCode
                    ? new(true, "ControlRouteObserved",
                        "This Friend PC completed a pinned HTTPS round trip with the successor. Test the game route and real join after Start.",
                        DateTimeOffset.UtcNow, recordHash)
                    : Fail("RouteConfirmationFailed",
                        "The successor did not record this verified direct-IP control route check.");
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Fail("RouteCheckInterrupted", "The direct-IP route check was interrupted."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or HttpRequestException or
            TaskCanceledException or ArgumentException)
        { return Fail("RouteUnreachable", "This PC could not verify the successor's direct-IP route."); }
        finally { ReleaseRetained(); }
    }
}
