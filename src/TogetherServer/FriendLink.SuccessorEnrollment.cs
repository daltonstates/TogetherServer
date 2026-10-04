using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    internal async Task<(FriendActionResult Result, FriendConfiguration? Connection)>
        EnrollWithSuccessorAsync(Guid profileId, string recordHash, string tlsFingerprint,
            CancellationToken cancellationToken = default)
    {
        static (FriendActionResult, FriendConfiguration?) Fail(string code, string message) =>
            (new(false, code, message, null), null);
        if (!TryRetain()) return Fail("ConnectionClosed", "This connection is closing.");
        try
        {
            if (recordHash?.Length != 64 || !recordHash.All(Uri.IsHexDigit) ||
                !ValidFingerprint(tlsFingerprint))
                return Fail("InvalidSuccessor", "Choose the verified takeover and its certificate fingerprint.");
            recordHash = recordHash.ToUpperInvariant();
            tlsFingerprint = tlsFingerprint.ToUpperInvariant();
            var route = await ProbeSuccessorRouteAsync(profileId, recordHash,
                tlsFingerprint, cancellationToken);
            if (!route.ControlRouteObserved)
                return Fail(route.Code, route.Message);
            await gate.WaitAsync(cancellationToken);
            Guid deviceId;
            Guid groupId;
            string ownerKey;
            string oldEndpoint;
            Guid oldHostId;
            try
            {
                if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId))
                    return Fail("ConsentRequired", "Allow shared saves on this PC first.");
                deviceId = config.DeviceId;
                groupId = config.ApprovedSharedWorldGroups.GetValueOrDefault(profileId);
                ownerKey = config.SharedWorldSigningKeys.GetValueOrDefault(profileId) ?? "";
                oldEndpoint = config.Endpoint;
                oldHostId = config.HostId;
            }
            finally { gate.Release(); }
            var record = new WorldAuthorityStore(data).ReadUniqueHead(profileId);
            if (record is null || record.RecordHash != recordHash ||
                !WorldAuthorityTrust.Verify(record) || record.Proposal.GroupId != groupId ||
                record.Roster.OwnerPublicKey != ownerKey ||
                !SharedWorldRouteTrust.DirectIpAddress(record.Proposal.CandidateAddress) ||
                record.Roster.Members.SingleOrDefault(item => item.DeviceId == deviceId) is not
                { Revoked: false, Grants.Receive: true } member ||
                member.AccessExpiresUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
                return Fail("AuthorityRejected", "The verified takeover or this PC's Receive grant changed.");
            using var key = LoadPcSigningKey(deviceId);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            if (member.PublicKey != publicKey)
                return Fail("IdentityChanged", "This PC's signed identity differs from the approved member.");
            using var successor = makeClient(record.Proposal.CandidateAddress, [tlsFingerprint]);
            var path = $"api/companion/servers/{profileId}/shared-world/successor-enrollment";
            using var challengeResponse = await successor.GetAsync(
                $"{path}/{recordHash}/{deviceId}", cancellationToken);
            var challengeBytes = await ReadBoundedSharedAsync(challengeResponse.Content,
                2048, cancellationToken);
            var challenge = challengeResponse.IsSuccessStatusCode && challengeBytes is not null ?
                JsonSerializer.Deserialize<SuccessorEnrollmentChallenge>(challengeBytes, Json) : null;
            if (challenge is null || challenge.Nonce.Length != 44 ||
                challenge.RecordHash != recordHash || challenge.GroupId != groupId ||
                challenge.OwnerPublicKey != ownerKey ||
                challenge.SuccessorAddress != record.Proposal.CandidateAddress ||
                challenge.TlsFingerprint != tlsFingerprint)
                return Fail("ChallengeRejected", "The successor did not confirm the exact trusted takeover.");
            var draft = new SuccessorEnrollmentRequest(deviceId, challenge.Nonce, recordHash,
                groupId, ownerKey, challenge.SuccessorAddress, tlsFingerprint, publicKey, "");
            var request = draft with
            {
                Signature = Convert.ToBase64String(key.SignData(
                SharedWorldSuccessorEnrollment.Basis(draft), HashAlgorithmName.SHA256))
            };
            using var response = await successor.PostAsJsonAsync(path, request, Json, cancellationToken);
            var bytes = await ReadBoundedSharedAsync(response.Content, 2048, cancellationToken);
            var credential = response.IsSuccessStatusCode && bytes is not null ?
                JsonSerializer.Deserialize<PairingCredential>(bytes, Json) : null;
            if (credential is null || credential.DeviceId != deviceId ||
                credential.Credential.Length < 32 || credential.ExpiresUtc <= DateTimeOffset.UtcNow)
                return Fail("EnrollmentDenied", "The successor did not accept this PC's approved identity.");
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (config is null || config.DeviceId != deviceId || config.HostId != oldHostId ||
                    config.Endpoint != oldEndpoint ||
                    config.ApprovedSharedWorldGroups.GetValueOrDefault(profileId) != groupId ||
                    config.SharedWorldSigningKeys.GetValueOrDefault(profileId) != ownerKey ||
                    !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                    new WorldAuthorityStore(data).ReadUniqueHead(profileId)?.RecordHash != recordHash)
                    return Fail("ConnectionChanged", "The trusted Host or takeover changed during enrollment.");
                // Keep the old Host connection and all received vault/history files.
                var next = JsonSerializer.Deserialize<FriendConfiguration>(
                    JsonSerializer.SerializeToUtf8Bytes(config, Json), Json)!;
                next.DisplayName = "Successor " + HostLabel(record.Proposal.CandidateAddress);
                next.Endpoint = record.Proposal.CandidateAddress;
                next.Fingerprint = tlsFingerprint;
                next.AcceptedFingerprints = [tlsFingerprint];
                next.HostId = Guid.Empty;
                next.Credential = credential.Credential;
                next.CredentialExpiresUtc = credential.ExpiresUtc;
                next.PendingRenewalRequestId = null;
                next.PendingOperations = [];
                next.Route = null;
                return (new(true, "SuccessorEnrolled",
                    "This PC can now check and receive this successor's shared saves.", null), next);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Fail("EnrollmentInterrupted", "Successor enrollment was interrupted."); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
            CryptographicException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return Fail("SuccessorUnavailable", "This PC could not verify and join the successor."); }
        finally { ReleaseRetained(); }
    }
}
