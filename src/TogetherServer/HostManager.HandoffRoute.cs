using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed partial class HostManager
{
    // An injectable pinned client keeps source checks entirely in memory.
    internal Func<string, IEnumerable<string>, HttpClient>? HandoffRouteClientForChecks { get; set; }

    internal async Task<PlannedHandoffResult> CheckPlannedSuccessorRouteAsync(Guid profileId, Guid deviceId,
        Guid requestId, CancellationToken cancellationToken)
    {
        WorldAuthorityRecord record;
        WorldPeerContactCard contact;
        SharedWorldRouteChallenge challenge;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var invitation = ReadHandoffInvitation(profileId);
            var head = authority.ReadUniqueHead(profileId);
            if (invitation is not { State: "Completed", PreparedContact: not null } ||
                invitation.RequestId != requestId || invitation.SuccessorDeviceId != deviceId ||
                head is null || head.Proposal.Kind != "Planned" || head.SuccessorReceipt?.DeviceId != deviceId ||
                !CurrentHandoffInvitation(invitation, out var roster) || roster is null ||
                HandoffSuccessorAddress(roster, deviceId) != head.Proposal.CandidateAddress)
                return new(false, "HostingRequestChanged", "The signed move or receiving PC's connection changed.");
            record = head;
            contact = invitation.PreparedContact;
            var bytes = data.LoadProtected("shared-world-signing-key.protected");
            if (bytes is null) return new(false, "OwnerIdentityRequired", "The original Host's signing identity is unavailable.");
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(bytes, out _);
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            challenge = SharedWorldRouteTrust.SignChallenge(record, nonce, Guid.Empty, key);
        }
        finally { gate.Release(); }
        try
        {
            var route = contact.Routes.Single(item => item.Address == record.Proposal.CandidateAddress);
            using var client = (HandoffRouteClientForChecks ?? WorldPeerService.MakePinnedClient)(route.Address, [route.CertificateSha256]);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            using var response = await client.PostAsJsonAsync($"api/companion/servers/{profileId}/shared-world/route-proof/{record.RecordHash}",
                challenge, json, cancellationToken);
            var bytes = await SharedWorldReceiptTrust.ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                response.Content.Headers.ContentLength, 2048, cancellationToken);
            var proof = response.IsSuccessStatusCode && bytes is not null ? JsonSerializer.Deserialize<SharedWorldRouteProof>(bytes, json) : null;
            if (!SharedWorldRouteTrust.Verify(proof, record, challenge.Nonce, route.CertificateSha256))
                return new(false, "SuccessorRoutePending", "The receiving PC did not return the expected signed proof from its pinned connection.");
            await gate.WaitAsync(cancellationToken);
            try
            {
                var current = ReadHandoffInvitation(profileId);
                if (current?.RequestId != requestId || current.State != "Completed" || current.PreparedContact?.Signature != contact.Signature ||
                    authority.ReadUniqueHead(profileId)?.RecordHash != record.RecordHash ||
                    !CurrentHandoffInvitation(current, out _))
                    return new(false, "HostingRequestChanged", "The move or selected PC's access changed during its connection check.");
            }
            finally { gate.Release(); }
            using var confirmed = await client.PostAsJsonAsync($"api/companion/servers/{profileId}/shared-world/route-confirm/{record.RecordHash}",
                new SharedWorldRouteConfirmation(challenge, proof!), json, cancellationToken);
            return confirmed.IsSuccessStatusCode ? new(true, "ControlRouteObserved",
                "The old Host verified this PC's pinned HTTPS round trip. A real game join remains separate.") :
                new(false, "SuccessorRoutePending", "The receiving PC did not retain the signed connection confirmation.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { return new(false, "SuccessorRoutePending", "The old Host could not reach this PC's pinned connection. Review its incoming route and retry."); }
    }
}
