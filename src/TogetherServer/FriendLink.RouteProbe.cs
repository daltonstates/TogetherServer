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
                var record = new WorldAuthorityStore(data).Read(profileId)
                    .SingleOrDefault(item => item.RecordHash == recordHash);
                if (record is null || !WorldAuthorityTrust.Verify(record) ||
                    record.Roster.OwnerPublicKey !=
                        config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) ||
                    record.Proposal.GroupId !=
                        config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId))
                    return Fail("HandoffProofInvalid", "This PC has not verified the signed takeover history.");
                var candidate = record.Roster.Members.SingleOrDefault(item =>
                    item.PublicKey == record.Proposal.CandidatePublicKey);
                if (candidate is null) return Fail("HandoffProofInvalid", "The signed successor is not in this group.");
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
                using var client = MakeClient(record.Proposal.CandidateAddress, [tlsFingerprint]);
                using var response = await client.PostAsJsonAsync(
                    $"api/companion/servers/{profileId}/shared-world/route-proof/{recordHash}",
                    challenge, Json, cancellationToken);
                var bytes = await ReadBoundedSharedAsync(response.Content, 2048, cancellationToken);
                var proof = response.IsSuccessStatusCode && bytes is not null ?
                    JsonSerializer.Deserialize<SharedWorldRouteProof>(bytes, Json) : null;
                return SharedWorldRouteTrust.Verify(proof, record, nonce, tlsFingerprint)
                    ? new(true, "ControlRouteObserved",
                        "This Friend connection reached the successor over pinned HTTPS and verified its signed handoff. Confirm this check ran on another PC; the game route and a real join still need testing.",
                        DateTimeOffset.UtcNow, recordHash)
                    : Fail("RouteProofInvalid",
                        "The successor did not return a valid signed route proof from the pinned address.");
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
