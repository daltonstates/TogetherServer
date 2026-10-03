using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    internal async Task<SharedWorldRouteCheck> ProbeSeparateCopyRouteAsync(Guid profileId,
        WorldSeparateCopyBranch branch, CancellationToken cancellationToken)
    {
        SharedWorldRouteCheck Fail(string code, string message) =>
            new(false, code, message, DateTimeOffset.UtcNow, branch?.BranchHash);
        if (!TryRetain()) return Fail("ConnectionClosed", "This Friend connection is closing.");
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (config is null || branch is null ||
                    !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                    !SharedWorldSeparateCopyStore.Verify(branch) ||
                    branch.Offer.Proposal.ProfileId != profileId ||
                    config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) !=
                        branch.Offer.Proposal.GroupId ||
                    config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) !=
                        branch.Offer.Roster.OwnerPublicKey ||
                    config.SharedRosterFloors?.GetValueOrDefault(profileId) is not { } floor ||
                    branch.Offer.Roster.Epoch < floor.Epoch ||
                    branch.Offer.Roster.Revision < floor.Revision ||
                    branch.Offer.Roster.Epoch == floor.Epoch &&
                    branch.Offer.Roster.Revision == floor.Revision &&
                    branch.Offer.Roster.Signature != floor.Signature ||
                    config.SharedWorldConflicts?.Contains(profileId) == true)
                    return Fail("SeparateProofRejected",
                        "This signed separate copy does not match this PC's trusted world and current membership.");
                var observer = branch.Offer.Roster.Members.SingleOrDefault(item =>
                    item.DeviceId == config.DeviceId);
                if (observer is not { Revoked: false } ||
                    !(observer.Grants.Receive || observer.Grants.RecoveryVoter) ||
                    observer.AccessExpiresUtc is { } observerExpiry &&
                        observerExpiry <= DateTimeOffset.UtcNow ||
                    config.DeviceId == branch.Offer.Proposal.ProposerDeviceId ||
                    data.HasProtected($"shared-world-pc-signing-{branch.Offer.Proposal.ProposerDeviceId:N}.protected"))
                    return Fail("OtherFriendRequired",
                        "A different approved Friend PC must check this route.");
                var authority = new WorldAuthorityStore(data).Read(profileId);
                if (authority.Any(record => record.Proposal.Epoch >= branch.Offer.Proposal.Epoch))
                    return Fail("HistoryReviewRequired",
                        "A signed majority or handoff now needs review before this separate copy starts.");
                if (!SharedWorldRouteTrust.DirectIpAddress(branch.Offer.Proposal.CandidateAddress))
                    return Fail("DirectIpRequired", "The candidate must use a reviewed direct-IP HTTPS address.");
                using var key = LoadPcSigningKey();
                if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != observer.PublicKey)
                    return Fail("ObserverIdentityChanged", "This PC's enrolled signing identity changed.");
                var challenge = SharedWorldSeparateRoute.SignChallenge(branch, config.DeviceId, key);
                if (!SharedWorldSeparateRoute.VerifyChallenge(challenge, branch, DateTimeOffset.UtcNow))
                    return Fail("ObserverIdentityChanged", "This PC could not sign the route check.");
                using var client = MakeClient(branch.Offer.Proposal.CandidateAddress,
                    [branch.Offer.CandidateTlsFingerprint]);
                var route = $"api/companion/servers/{profileId}/shared-world/separate-route/{branch.BranchHash}";
                using var response = await client.PostAsJsonAsync(route + "/proof", challenge,
                    Json, cancellationToken);
                var bytes = await ReadBoundedSharedAsync(response.Content, 2048, cancellationToken);
                var proof = response.IsSuccessStatusCode && bytes is not null ?
                    JsonSerializer.Deserialize<SeparateCopyRouteProof>(bytes, Json) : null;
                if (!SharedWorldSeparateRoute.VerifyProof(proof, challenge, branch))
                    return Fail("RouteProofInvalid", "The candidate did not return a valid pinned route proof.");
                if (new WorldAuthorityStore(data).Read(profileId).Any(record =>
                    record.Proposal.Epoch >= branch.Offer.Proposal.Epoch))
                    return Fail("HistoryReviewRequired", "Signed authority changed during the route check.");
                using var confirmed = await client.PostAsJsonAsync(route + "/confirm",
                    new SeparateCopyRouteConfirmation(challenge, proof!), Json, cancellationToken);
                return confirmed.IsSuccessStatusCode
                    ? new(true, "SeparateRouteObserved",
                        "This Friend PC reached the separate-copy Host over pinned HTTPS. The game route still needs a real join after Start.",
                        DateTimeOffset.UtcNow, branch.BranchHash)
                    : Fail("RouteConfirmationFailed", "The candidate did not record this second-PC route check.");
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Fail("RouteCheckInterrupted", "The separate-copy route check was interrupted."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or HttpRequestException or
            TaskCanceledException or ArgumentException)
        { return Fail("RouteUnreachable", "This PC could not verify the separate-copy Host's direct-IP route."); }
        finally { ReleaseRetained(); }
    }
}
