using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record ReceivedSharedWorldStatus(bool Consented, long? HostVersion, long? ThisPcVersion,
    string State, string? Error = null, long ReceivedBytes = 0, long TotalBytes = 0,
    long? RosterRevision = null, string Trust = "Roster not verified");
public sealed record ReceivedSharedWorldResult(bool Ok, string Code, string Message,
    ReceivedSharedWorldStatus? Status = null);
public sealed record WorldAuthorityOfferResult(bool Ok, string Code, string Message,
    WorldAuthorityOffer? Offer = null);
public sealed record WorldAuthorityVoteAction(bool Ok, string Code, string Message,
    int Votes = 0, int Required = 0, WorldAuthorityRecord? Decision = null);
public sealed record WorldResolutionChoice(string RecordHash, long Version, bool AvailableHere);
public sealed record WorldResolutionInvitationResult(bool Ok, string Code, string Message,
    string? ProposalHash = null);
public sealed record WorldHistoryReviewResult(bool Ok, string Code, string Message,
    int RecordCount = 0, int CompetingHeads = 0,
    Guid? GroupId = null, string? OwnerPublicKey = null);
public sealed record WorldHistoryReviewRequest(Guid? ConfirmGroupId = null,
    string? ConfirmOwnerPublicKey = null);

internal sealed partial class FriendLink
{
    public async Task<WorldHistoryReviewResult> ReviewSharedHistoryAsync(Guid profileId,
        WorldHistoryReviewRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This connection is closing.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (config is null) return new(false, "NotPaired", "Choose a saved Host connection.");
            var group = config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId);
            var owner = config.SharedWorldSigningKeys?.GetValueOrDefault(profileId);
            var floor = config.SharedRosterFloors?.GetValueOrDefault(profileId);
            if (floor is not null && group is not null && floor.GroupId != group)
                return new(false, "TrustRequired", "This PC's saved group review needs repair.");
            using var client = MakeClient(config.Endpoint, AcceptedPins());
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.Credential);
            client.DefaultRequestHeaders.Add("X-Device-Id", config.DeviceId.ToString());
            client.DefaultRequestHeaders.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
            using var key = LoadPcSigningKey(config.DeviceId);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            using var firstRosterResponse = await client.GetAsync(
                $"api/companion/servers/{profileId}/shared-world/roster", cancellationToken);
            if (firstRosterResponse.StatusCode == System.Net.HttpStatusCode.Forbidden &&
                (group is null || owner is null || floor is null))
            {
                using var challengeResponse = await client.GetAsync(
                    $"api/companion/servers/{profileId}/shared-world/enrollment", cancellationToken);
                var challengeBytes = await ReadBoundedSharedAsync(challengeResponse.Content, 512,
                    cancellationToken);
                var challenge = challengeResponse.IsSuccessStatusCode && challengeBytes is not null ?
                    JsonSerializer.Deserialize<SharedWorldEnrollmentChallenge>(challengeBytes, Json) : null;
                if (challenge?.Nonce is null || challenge.Nonce.Length != 44)
                    return new(false, "EnrollmentDenied", "The Host did not allow this PC to enroll for voting.");
                var proof = new SharedWorldEnrollmentRequest(challenge.Nonce, publicKey,
                    Convert.ToBase64String(key.SignData(SharedWorldRosterTrust.EnrollmentBasis(
                        config.DeviceId, challenge.Nonce, publicKey), HashAlgorithmName.SHA256)));
                using var enrollment = await client.PostAsJsonAsync(
                    $"api/companion/servers/{profileId}/shared-world/enrollment", proof, Json,
                    cancellationToken);
                if (!enrollment.IsSuccessStatusCode)
                    return new(false, "EnrollmentDenied", "The Host did not accept this PC's voting identity.");
            }
            else if (!firstRosterResponse.IsSuccessStatusCode)
                return new(false, "RosterUnavailable", "Current signed membership is unavailable.");
            using var rosterResponse = await client.GetAsync(
                $"api/companion/servers/{profileId}/shared-world/roster", cancellationToken);
            var rosterBytes = await ReadBoundedSharedAsync(rosterResponse.Content,
                SharedWorldService.MaximumManifestBytes, cancellationToken);
            var roster = rosterResponse.IsSuccessStatusCode && rosterBytes is not null
                ? JsonSerializer.Deserialize<SharedWorldRoster>(rosterBytes, Json) : null;
            var member = roster?.Members.SingleOrDefault(item => item.DeviceId == config.DeviceId);
            if (!SharedWorldRosterTrust.Verify(roster) || roster!.ProfileId != profileId ||
                owner is not null && roster.OwnerPublicKey != owner ||
                floor is not null && floor.GroupId == roster.GroupId &&
                    (roster.Epoch < floor.Epoch ||
                     roster.Epoch == floor.Epoch && roster.Revision < floor.Revision ||
                     roster.Epoch == floor.Epoch && roster.Revision == floor.Revision &&
                         roster.Signature != floor.Signature) ||
                member is null || member.Revoked || member.PublicKey != publicKey ||
                !(member.Grants.Receive || member.Grants.RecoveryVoter) ||
                member.AccessExpiresUtc is { } expires && expires <= DateTimeOffset.UtcNow)
                return new(false, "RosterRejected", "Current signed membership does not allow history review.");
            var changingGroup = group != roster.GroupId || floor is null ||
                floor.GroupId != roster.GroupId;
            if (changingGroup && (request?.ConfirmGroupId != roster.GroupId ||
                request?.ConfirmOwnerPublicKey != roster.OwnerPublicKey))
                return new(false, "GroupReviewRequired",
                    "Review this signed world group and owner identity, then confirm to read its hosting history.",
                    GroupId: roster.GroupId, OwnerPublicKey: roster.OwnerPublicKey);
            var store = new WorldAuthorityStore(data);
            var accepted = store.Read(profileId);
            if (changingGroup && accepted.Count > 0 && group != roster.GroupId)
                return new(false, "HistoryConflict",
                    "This PC already keeps authority for another group. Preserve it for owner review.");
            var offset = Math.Max(0, accepted.Count - 1);
            using var historyResponse = await client.GetAsync(
                $"api/companion/servers/{profileId}/shared-world/authority?offset={offset}",
                cancellationToken);
            var historyBytes = await ReadBoundedSharedAsync(historyResponse.Content,
                4 * 1024 * 1024, cancellationToken);
            var page = historyResponse.IsSuccessStatusCode && historyBytes is not null
                ? JsonSerializer.Deserialize<List<WorldAuthorityRecord>>(historyBytes, Json) : null;
            if (page is null) return new(false, "HistoryUnavailable", "Signed history is unavailable.");
            var additions = NewAuthorityPage(accepted, page);
            var staged = new HashSet<string>(StringComparer.Ordinal);
            var remaining = WorldAuthorityTrust.ProofVersionsPerCheck;
            foreach (var record in additions.Where(item => item.VersionLineageDigest is not null))
            {
                var parent = additions.Concat(accepted).SingleOrDefault(item =>
                    item.RecordHash == record.Proposal.ParentAuthorityHash);
                var batch = await StageAuthorityProofBatchAsync(store, record, parent,
                    remaining, async (number, token) =>
                    {
                        using var response = await client.GetAsync(
                            $"api/companion/servers/{profileId}/shared-world/authority/" +
                            $"{record.RecordHash}/proof/{number}",
                            HttpCompletionOption.ResponseHeadersRead, token);
                        var bytes = await ReadBoundedSharedAsync(response.Content,
                            SharedWorldService.MaximumManifestBytes, token);
                        return response.IsSuccessStatusCode && bytes is not null
                            ? JsonSerializer.Deserialize<SharedWorldVersion>(bytes, Json) : null;
                    }, cancellationToken);
                remaining -= batch.Used;
                if (!batch.Complete) return new(false, "HistoryCatchUpPending",
                    "Check again to finish verifying signed history.");
                var seal = VerifyStagedAuthorityProofBatch(store, record, parent,
                    remaining, cancellationToken);
                remaining -= seal.Used;
                if (!seal.Complete) return new(false, "HistoryCatchUpPending",
                    "Check again to finish verifying signed history.");
                staged.Add(record.RecordHash);
            }
            foreach (var record in additions)
            {
                if (staged.Contains(record.RecordHash))
                {
                    var parent = store.Read(profileId).SingleOrDefault(item =>
                        item.RecordHash == record.Proposal.ParentAuthorityHash);
                    store.AppendReceivedStaged(record, parent, profileId,
                        roster.GroupId, roster.OwnerPublicKey);
                    store.ClearStagedProof(record, parent);
                }
                else store.AppendReceived(record, profileId,
                    roster.GroupId, roster.OwnerPublicKey);
            }
            accepted = store.Read(profileId);
            config.ApprovedSharedWorldGroups ??= [];
            config.SharedWorldSigningKeys ??= [];
            config.SharedRosterFloors ??= [];
            config.ApprovedSharedWorldGroups[profileId] = roster.GroupId;
            config.SharedWorldSigningKeys[profileId] = roster.OwnerPublicKey;
            config.SharedRosterFloors![profileId] = new(roster.GroupId, roster.Epoch,
                roster.Revision, roster.Signature);
            SaveConfig();
            data.SaveProtected($"shared-world-roster-{config.DeviceId:N}-{profileId:N}.protected", rosterBytes!);
            return new(page.Count == WorldAuthorityTrust.PageSize ? false : true,
                page.Count == WorldAuthorityTrust.PageSize ? "HistoryCatchUpPending" : "HistoryReviewed",
                page.Count == WorldAuthorityTrust.PageSize ? "Check again to finish reviewing signed history." :
                    "Signed hosting history was reviewed.", accepted.Count,
                WorldAuthorityTrust.EffectiveHeads(accepted).Length);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
                                   InvalidDataException or CryptographicException or TaskCanceledException)
        { return new(false, "HistoryRejected", "Signed history could not be verified."); }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public IReadOnlyList<WorldResolutionChoice> ResolutionChoices(Guid profileId)
    {
        gate.Wait();
        try
        {
            if (config?.ConsentedSharedWorldProfiles?.Contains(profileId) != true ||
                config.ApprovedSharedWorldGroups?.ContainsKey(profileId) != true) return [];
            var records = new WorldAuthorityStore(data).Read(profileId);
            var heads = WorldAuthorityTrust.EffectiveHeads(records);
            if (heads.Length < 2) return [];
            var local = ReadReceivedLatest(ReceivedRoot(profileId));
            return heads.Select(head => new WorldResolutionChoice(head.RecordHash,
                head.Version.Number, local?.VersionHash == head.Version.VersionHash)).ToArray();
        }
        finally { gate.Release(); }
    }
    private readonly HashSet<string> deliveredAuthority = new(StringComparer.Ordinal);

    private async Task ReturnAuthorityToOriginalHostAsync(CancellationToken cancellationToken = default)
    {
        if (config is null) return;
        var store = new WorldAuthorityStore(data);
        foreach (var profileId in (config.ApprovedSharedWorldGroups?.Keys.AsEnumerable() ??
            Enumerable.Empty<Guid>())
            .Take(20))
        {
            IReadOnlyList<WorldAuthorityRecord> records;
            try
            {
                if (!store.HasState(profileId)) continue;
                records = store.Read(profileId);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
            { continue; }
            foreach (var record in records.Where(record => !deliveredAuthority.Contains(record.RecordHash)).Take(2))
            {
                if (config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) !=
                    record.Proposal.GroupId ||
                    config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) !=
                    record.Roster.OwnerPublicKey) continue;
                try
                {
                    using var response = await HostClient().PostAsJsonAsync(
                        $"api/companion/servers/{profileId}/shared-world/authority",
                        record, Json, cancellationToken);
                    if (response.IsSuccessStatusCode) deliveredAuthority.Add(record.RecordHash);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
                { return; }
            }
            var offer = new SharedWorldVoteInbox(data).Armed(profileId);
            if (offer?.Proposal.Kind == "ResolutionOwnerOverride" &&
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) == offer.Roster.GroupId)
            {
                try
                {
                    using var response = await HostClient().PostAsJsonAsync(
                        $"api/companion/servers/{profileId}/shared-world/resolution/owner-offer",
                        offer, Json, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
                { return; }
            }
        }
    }
    public async Task<WorldAuthorityOfferResult> PrepareRecoveryOfferAsync(Guid profileId)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This connection is closing.");
        await gate.WaitAsync();
        try
        {
            if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId))
                return new(false, "ConsentRequired", "Allow shared saves on this PC first.");
            if (!sharedHostLoss.MayPropose)
                return new(false, "HostLossNotConfirmed",
                    "Wait for two minutes of failed secure Host checks before proposing takeover.");
            if (config.SharedWorldConflicts?.Contains(profileId) == true ||
                config.PendingSharedWorldGroups?.ContainsKey(profileId) == true)
                return new(false, "HistoryReviewRequired", "Review the competing or changed save history first.");
            var floor = config.SharedRosterFloors?.GetValueOrDefault(profileId);
            var pinnedOwner = config.SharedWorldSigningKeys?.GetValueOrDefault(profileId);
            if (floor is null || pinnedOwner is null ||
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != floor.GroupId)
                return new(false, "RosterUnavailable", "A trusted owner-signed roster is unavailable on this PC.");
            var rosterBytes = data.LoadProtected(
                $"shared-world-roster-{config.DeviceId:N}-{profileId:N}.protected");
            if (rosterBytes is null || rosterBytes.Length > SharedWorldService.MaximumManifestBytes)
                return new(false, "RosterUnavailable", "Check the Host roster again before a future takeover.");
            var roster = JsonSerializer.Deserialize<SharedWorldRoster>(rosterBytes, Json);
            if (roster is null || roster.OwnerPublicKey != pinnedOwner)
                return new(false, "RosterRejected", "The saved roster does not match this world identity.");
            var settings = data.LoadSettings();
            if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out _))
                return new(false, "CandidateRouteUnavailable",
                    "Set this PC's direct HTTPS address before proposing takeover. Open Friend connections only when ready to receive votes.");
            using var certificate = new HostIdentity(data).Ensure(settings.CompanionEndpoint);
            using var key = LoadPcSigningKey();
            var offer = SharedWorldElection.PrepareOffer(sharedHostLoss, ReceivedRoot(profileId),
                roster, floor, config.DeviceId, key, settings.CompanionEndpoint,
                HostIdentity.Fingerprint(certificate), new WorldAuthorityStore(data));
            offer = new SharedWorldVoteInbox(data).Arm(offer, ReceivedRoot(profileId));
            return new(true, "RecoveryOfferArmed",
                "This PC prepared a signed offer. Other approved PCs can vote only through its pinned HTTPS address; no game server has started.",
                offer);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "RecoveryOfferUnavailable", "A safe recovery offer could not be prepared: " + ex.Message); }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<WorldAuthorityOfferResult> PrepareResolutionOfferAsync(Guid profileId,
        string selectedHeadHash, bool ownerOverride = false)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This connection is closing.");
        await gate.WaitAsync();
        try
        {
            if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                selectedHeadHash.Length != 64 || !selectedHeadHash.All(Uri.IsHexDigit))
                return new(false, "ResolutionUnavailable", "Choose a verified saved branch on this PC.");
            var floor = config.SharedRosterFloors?.GetValueOrDefault(profileId);
            var pinned = config.SharedWorldSigningKeys?.GetValueOrDefault(profileId);
            if (floor is null || pinned is null ||
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != floor.GroupId)
                return new(false, "RosterUnavailable", "Check the owner-signed roster first.");
            var rosterBytes = data.LoadProtected(
                $"shared-world-roster-{config.DeviceId:N}-{profileId:N}.protected");
            var roster = rosterBytes is not null && rosterBytes.Length <= SharedWorldService.MaximumManifestBytes
                ? JsonSerializer.Deserialize<SharedWorldRoster>(rosterBytes, Json) : null;
            if (roster is null || roster.OwnerPublicKey != pinned)
                return new(false, "RosterRejected", "The saved roster does not match this world.");
            if (ownerOverride && !roster.OwnerOverride)
                return new(false, "OwnerOverrideDisabled", "The signed roster has owner override off.");
            var settings = data.LoadSettings();
            if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out _))
                return new(false, "CandidateRouteUnavailable", "Set this PC's direct HTTPS address first.");
            using var certificate = new HostIdentity(data).Ensure(settings.CompanionEndpoint);
            using var key = LoadPcSigningKey();
            var offer = SharedWorldElection.PrepareResolutionOffer(ReceivedRoot(profileId), roster,
                floor, config.DeviceId, key, settings.CompanionEndpoint,
                HostIdentity.Fingerprint(certificate), new WorldAuthorityStore(data),
                selectedHeadHash, ownerOverride);
            offer = new SharedWorldVoteInbox(data).Arm(offer, ReceivedRoot(profileId));
            return new(true, "ResolutionOfferArmed",
                "This PC offered its verified copy for a decision by designated voters.", offer);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "ResolutionUnavailable", "The resolution offer could not be prepared: " + ex.Message); }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<WorldAuthorityVoteAction> VoteOnRecoveryOfferAsync(Guid profileId,
        WorldAuthorityOffer offer, CancellationToken cancellationToken = default)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This connection is closing.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId))
                return new(false, "ConsentRequired", "Allow shared saves on this PC first.");
            if (!sharedHostLoss.MayPropose && offer.Proposal.Schema != 3)
                return new(false, "HostLossNotConfirmed",
                    "This PC must also confirm two minutes without the pinned Host before voting.");
            if (!SharedWorldElection.VerifyOffer(offer) || offer.Proposal.ProfileId != profileId ||
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != offer.Roster.GroupId ||
                config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) != offer.Roster.OwnerPublicKey ||
                config.SharedRosterFloors?.GetValueOrDefault(profileId) is not { } floor ||
                offer.Proposal.Schema != 3 && config.SharedWorldConflicts?.Contains(profileId) == true)
                return new(false, "RecoveryOfferRejected", "This offer does not match the trusted shared world on this PC.");
            using var signer = LoadPcSigningKey();
            using var candidate = MakeClient(offer.Proposal.CandidateAddress,
                [offer.CandidateTlsFingerprint]);
            var hash = WorldAuthorityTrust.ProposalHash(offer.Proposal);
            var path = $"api/companion/servers/{profileId}/shared-world/recovery/{hash}";
            using var challengeResponse = await candidate.GetAsync(
                $"{path}/challenge/{config.DeviceId}", cancellationToken);
            var challengeBytes = await ReadBoundedSharedAsync(challengeResponse.Content, 512,
                cancellationToken);
            var challenge = challengeResponse.IsSuccessStatusCode && challengeBytes is not null ?
                JsonSerializer.Deserialize<WorldAuthorityChallenge>(challengeBytes, Json) : null;
            if (challenge?.Nonce is not { Length: 44 })
                return new(false, "CandidateUnavailable", "The candidate did not provide a valid secure challenge.");
            var publicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
            var requestDraft = new WorldAuthorityOfferRequest(1, offer.Roster.GroupId, profileId,
                hash, config.DeviceId, publicKey, challenge.Nonce, "");
            var request = requestDraft with
            {
                Signature = Convert.ToBase64String(signer.SignData(
                SharedWorldVoteInbox.RequestBasis(requestDraft), HashAlgorithmName.SHA256))
            };
            using var offerResponse = await candidate.PostAsJsonAsync(path + "/offer", request, Json,
                cancellationToken);
            var offerBytes = await ReadBoundedSharedAsync(offerResponse.Content, 512 * 1024,
                cancellationToken);
            var received = offerResponse.IsSuccessStatusCode && offerBytes is not null ?
                JsonSerializer.Deserialize<WorldAuthorityOffer>(offerBytes, Json) : null;
            if (!SharedWorldElection.VerifyOffer(received) ||
                WorldAuthorityTrust.ProposalHash(received!.Proposal) != hash ||
                received.CandidateTlsFingerprint != offer.CandidateTlsFingerprint ||
                received.Roster.Signature != offer.Roster.Signature ||
                received.Version.VersionHash != offer.Version.VersionHash)
                return new(false, "CandidateOfferChanged", "The candidate's signed offer changed during review.");
            var store = new WorldAuthorityStore(data);
            var vote = SharedWorldElection.Vote(sharedHostLoss, ReceivedRoot(profileId), floor,
                offer.Roster.OwnerPublicKey, received, config.DeviceId, signer, store);
            using var voteResponse = await candidate.PostAsJsonAsync(path + "/vote", vote, Json,
                cancellationToken);
            var resultBytes = await ReadBoundedSharedAsync(voteResponse.Content, 2 * 1024 * 1024,
                cancellationToken);
            var result = voteResponse.IsSuccessStatusCode && resultBytes is not null ?
                JsonSerializer.Deserialize<WorldAuthorityVoteResult>(resultBytes, Json) : null;
            if (result is not { Ok: true })
                return new(false, "VoteNotConfirmed", "The signed vote is saved on this PC. Retry with the same offer to confirm delivery.");
            if (result.Decision is { } decision)
                store.AppendReceived(decision, profileId, offer.Roster.GroupId,
                    offer.Roster.OwnerPublicKey);
            return new(true, result.Code,
                result.Decision is null ? "Your recovery vote was recorded." :
                    "A majority approved this exact save. The game server has not started.",
                result.Votes, result.Required, result.Decision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException or
                                   HttpRequestException or TaskCanceledException or ArgumentException)
        { return new(false, "RecoveryVoteUnavailable", "The secure recovery vote could not finish: " + ex.Message); }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<WorldResolutionInvitationResult> ImportResolutionInvitationAsync(
        Guid profileId, WorldAuthorityOffer offer)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This connection is closing.");
        await gate.WaitAsync();
        try
        {
            if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                offer.Proposal.Kind != "ResolutionQuorum" ||
                offer.Proposal.ProfileId != profileId || !SharedWorldElection.VerifyOffer(offer) ||
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != offer.Roster.GroupId ||
                config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) != offer.Roster.OwnerPublicKey ||
                config.SharedRosterFloors?.GetValueOrDefault(profileId) is not { } floor ||
                floor.Epoch != offer.Roster.Epoch || floor.Revision != offer.Roster.Revision ||
                floor.Signature != offer.Roster.Signature)
                return new(false, "InvitationRejected", "This invitation does not match the trusted roster.");
            var heads = WorldAuthorityTrust.EffectiveHeads(new WorldAuthorityStore(data).Read(profileId));
            if (!WorldAuthorityTrust.ExactResolutionHeads(new WorldAuthorityRecord(2,
                    offer.Proposal, offer.Roster, offer.Version, [], null, ""), heads) ||
                heads.Single(head => head.RecordHash == offer.Proposal.ParentAuthorityHash)
                    .Version.VersionHash != offer.Version.VersionHash)
                return new(false, "InvitationRejected", "The complete signed branch set changed.");
            var hash = WorldAuthorityTrust.ProposalHash(offer.Proposal);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(offer, Json);
            if (bytes.Length > 512 * 1024)
                return new(false, "InvitationRejected", "The invitation is too large.");
            data.SaveProtected($"resolution-invitation-{config.DeviceId:N}-{profileId:N}-{hash}.protected",
                bytes);
            return new(true, "InvitationReviewed", "This signed invitation matches the verified branches.", hash);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException)
        { return new(false, "InvitationRejected", "The invitation could not be verified."); }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public async Task<WorldAuthorityVoteAction> VoteOnResolutionIdAsync(Guid profileId,
        string proposalHash, CancellationToken cancellationToken = default)
    {
        if (proposalHash.Length != 64 || !proposalHash.All(Uri.IsHexDigit))
            return new(false, "InvalidResolutionId", "Choose a reviewed invitation.");
        WorldAuthorityOffer? offer;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (config is null) return new(false, "NotPaired", "Choose a saved Host connection.");
            var name = $"resolution-invitation-{config.DeviceId:N}-{profileId:N}-{proposalHash}.protected";
            var bytes = data.LoadProtected(name);
            if (bytes is null || bytes.Length > 512 * 1024)
                return new(false, "InvitationUnavailable", "Review the signed invitation again.");
            offer = JsonSerializer.Deserialize<WorldAuthorityOffer>(bytes, Json);
            if (!SharedWorldElection.VerifyOffer(offer) ||
                WorldAuthorityTrust.ProposalHash(offer!.Proposal) != proposalHash)
                return new(false, "InvitationRejected", "The reviewed invitation changed.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        { return new(false, "InvitationRejected", "The reviewed invitation is unavailable."); }
        finally { gate.Release(); }
        return await VoteOnRecoveryOfferAsync(profileId, offer!, cancellationToken);
    }

    public async Task<WorldSeparateCopyResult> DeclareSeparateCopyAsync(Guid profileId,
        bool acceptSplitWarning)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This connection is closing.");
        await gate.WaitAsync();
        try
        {
            if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId))
                return new(false, "ConsentRequired", "Allow shared saves on this PC first.");
            if (!acceptSplitWarning)
                return new(false, "SplitWarningRequired",
                    "Confirm that another game server may still be running and histories will remain separate.");
            var offer = new SharedWorldVoteInbox(data).Armed(profileId);
            if (offer is null ||
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != offer.Roster.GroupId ||
                config.SharedWorldSigningKeys?.GetValueOrDefault(profileId) != offer.Roster.OwnerPublicKey ||
                config.SharedWorldConflicts?.Contains(profileId) == true)
                return new(false, "RecoveryOfferRejected", "Prepare and review this PC's signed offer first.");
            using var key = LoadPcSigningKey();
            var branch = new SharedWorldSeparateCopyStore(data).Declare(sharedHostLoss, offer,
                ReceivedRoot(profileId), key, acceptSplitWarning);
            return new(true, "SeparateCopyRecorded",
                "A warned separate history was recorded. It is not authoritative and has not started a game server. Keep both histories for later group review.", branch);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "SeparateCopyUnavailable", "A separate history could not be recorded: " + ex.Message); }
        finally { gate.Release(); ReleaseRetained(); }
    }
    internal TakeoverReadiness CheckTakeoverReadiness(Guid profileId, TakeoverLocalSetup setup)
    {
        string? vault;
        string? pinned;
        Guid? group;
        Guid deviceId;
        gate.Wait();
        try
        {
            if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId))
                return new(false, ["Allow saves on this PC and receive a verified copy first."], null, null);
            vault = ReceivedRoot(profileId);
            pinned = config.SharedWorldSigningKeys?.GetValueOrDefault(profileId);
            group = config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId);
            deviceId = config.DeviceId;
        }
        finally { gate.Release(); }
        return SharedWorldReadiness.Check(vault,
            Path.Combine(data.RootPath, "shared-world-rehearsals"), setup,
            LocalTakeoverAuthority(profileId, deviceId), pinned, group);
    }

    internal TakeoverReadiness RehearseTakeover(Guid profileId, TakeoverLocalSetup setup)
    {
        string? vault;
        string? pinned;
        Guid? group;
        Guid deviceId;
        gate.Wait();
        try
        {
            if (config is null || !config.ConsentedSharedWorldProfiles.Contains(profileId))
                return new(false, ["Allow saves on this PC and receive a verified copy first."], null, null);
            vault = ReceivedRoot(profileId);
            pinned = config.SharedWorldSigningKeys?.GetValueOrDefault(profileId);
            group = config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId);
            deviceId = config.DeviceId;
        }
        finally { gate.Release(); }
        return SharedWorldReadiness.Rehearse(data.RootPath, vault, setup,
            LocalTakeoverAuthority(profileId, deviceId), pinned, group);
    }

    private TakeoverAuthority LocalTakeoverAuthority(Guid profileId, Guid deviceId)
    {
        try
        {
            var records = new WorldAuthorityStore(data).Read(profileId);
            var heads = WorldAuthorityTrust.EffectiveHeads(records);
            var eligible = heads.Length == 1 && heads[0].Proposal.Kind == "Planned" &&
                heads[0].Roster.Members.Any(member => member.DeviceId == deviceId &&
                    member.PublicKey == heads[0].Proposal.CandidatePublicKey &&
                    member.Grants.EligibleHost && !member.Revoked &&
                    (member.AccessExpiresUtc is null || member.AccessExpiresUtc > DateTimeOffset.UtcNow));
            return new(eligible, false, false,
                eligible && data.HasProtected($"successor-restore-{profileId:N}.protected"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                   System.Text.Json.JsonException or CryptographicException)
        { return new(false, false, false); }
    }

    private const long ReceiverReserveBytes = 1024L * 1024 * 1024;
    private readonly ConcurrentDictionary<Guid, ReceivedSharedWorldStatus> sharedTransfers = new();
    private readonly ConcurrentDictionary<Guid, byte> withdrawnSharedConsent = new();
    private readonly object sharedReceiptSync = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> sharedProfileGates = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> sharedProfileCancellation = new();
    private CancellationTokenSource sharedLinkCancellation = new();
    private readonly object sharedScheduleSync = new();
    private Task? scheduledSharedTransfer;
    private readonly Dictionary<Guid, DateTimeOffset> sharedRetryAfter = [];
    private readonly Dictionary<Guid, int> sharedFailures = [];

    internal void ScheduleSharedCatchUp(CancellationToken shutdown)
    {
        lock (sharedScheduleSync)
        {
            if (shutdown.IsCancellationRequested || scheduledSharedTransfer is { IsCompleted: false } ||
                view.State is not ("Connected" or "Disabled") || !gate.Wait(0)) return;
            Guid next;
            try
            {
                next = config?.ConsentedSharedWorldProfiles.FirstOrDefault(id =>
                    view.Profiles.Any(profile => profile.Id == id && profile.Kind != GameKinds.Custom) &&
                    (!sharedRetryAfter.TryGetValue(id, out var after) || after <= DateTimeOffset.UtcNow)) ?? Guid.Empty;
            }
            finally { gate.Release(); }
            if (next == Guid.Empty) return;
            scheduledSharedTransfer = RunScheduledSharedTransferAsync(next, shutdown);
        }
    }

    internal Task ScheduledSharedCatchUp()
    {
        lock (sharedScheduleSync) return scheduledSharedTransfer ?? Task.CompletedTask;
    }

    private async Task RunScheduledSharedTransferAsync(Guid profileId, CancellationToken shutdown)
    {
        try
        {
            var result = await PullSharedWorldAsync(profileId, shutdown);
            lock (sharedScheduleSync)
            {
                var reviewRequired = result.Code is "SourceReviewRequired" or "ConsentRequired" or
                    "SigningIdentityChanged" or "VersionConflict" or "VersionChainInvalid";
                var failures = result.Ok || reviewRequired ? 0 :
                    Math.Min(6, sharedFailures.GetValueOrDefault(profileId) + 1);
                sharedFailures[profileId] = failures;
                sharedRetryAfter[profileId] = DateTimeOffset.UtcNow.AddSeconds(reviewRequired ? 300 : failures == 0 ? 30 :
                    Math.Min(300, 5 * (1 << failures)));
                if (!result.Ok && result.Code == "InsufficientSpace")
                    sharedTransfers[profileId] = SharedWorldStatus(profileId) with { State = "Low space", Error = result.Message };
                else if (!result.Ok && failures > 0)
                    sharedTransfers[profileId] = SharedWorldStatus(profileId) with { State = "Stalled", Error = result.Message };
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DiagnosticOutput.WriteError("Shared save catch-up failed: " + ex.GetType().Name); }
    }

    internal void CancelSharedTransfers()
    {
        lock (sharedReceiptSync)
        {
            var previous = sharedLinkCancellation;
            sharedLinkCancellation = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
        }
    }

    internal static async Task<(bool Complete, int Used)> StageAuthorityProofBatchAsync(
        WorldAuthorityStore store, WorldAuthorityRecord record, WorldAuthorityRecord? parent,
        int budget, Func<long, CancellationToken, Task<SharedWorldVersion?>> fetch,
        CancellationToken cancellationToken)
    {
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget));
        var (first, prior, digest) = store.ReadStagedProofCursor(record, parent);
        if (prior?.Number == record.Version.Number) return (true, 0);
        var used = 0;
        var expectedSigner = parent?.Proposal.CandidatePublicKey ?? record.Roster.OwnerPublicKey;
        for (var number = first; ; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (used == budget) return (false, used);
            var piece = store.ReadStagedProofVersion(record, number);
            var fetched = piece is null;
            if (fetched)
            {
                piece = number == record.Version.Number ? record.Version :
                    await fetch(number, cancellationToken);
            }
            if (piece is null || piece.Number != number || piece.ParentHash != prior?.VersionHash ||
                piece.SigningPublicKey != expectedSigner ||
                number == record.Version.Number && piece.VersionHash != record.Version.VersionHash)
                throw new InvalidDataException("A signed authority proof piece is missing or changed.");
            if (fetched)
                store.StageReceivedProofVersion(record, piece, cancellationToken);
            used++;
            digest = WorldAuthorityTrust.AdvanceLineage(digest, piece);
            prior = piece;
            if (number == record.Version.Number || used == budget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                store.SaveStagedProofCursor(record, parent, prior, digest);
                if (number == record.Version.Number &&
                    Convert.ToHexString(digest) != record.VersionLineageDigest)
                    throw new InvalidDataException("Authority proof digest does not match the signed record.");
                return (number == record.Version.Number, used);
            }
        }
    }

    internal static WorldAuthorityRecord[] NewAuthorityPage(
        IReadOnlyList<WorldAuthorityRecord> accepted, IReadOnlyList<WorldAuthorityRecord> page)
    {
        if (page.Count > WorldAuthorityTrust.PageSize || page.Any(record => record is null) ||
            page.Select(record => record.RecordHash).Distinct(StringComparer.Ordinal).Count() != page.Count ||
            accepted.Count > 0 &&
                (page.Count == 0 || page[0].RecordHash != accepted[^1].RecordHash))
            throw new InvalidDataException("The authority page changed or omitted a verified decision.");
        return accepted.Count > 0 ? page.Skip(1).ToArray() : page.ToArray();
    }

    internal static (bool Complete, int Used) VerifyStagedAuthorityProofBatch(
        WorldAuthorityStore store, WorldAuthorityRecord record, WorldAuthorityRecord? parent,
        int budget, CancellationToken cancellationToken)
    {
        if (budget < 0) throw new ArgumentOutOfRangeException(nameof(budget));
        var (first, prior, digest) = store.ReadStagedProofSeal(record, parent);
        if (prior?.Number == record.Version.Number) return (true, 0);
        var expectedSigner = parent?.Proposal.CandidatePublicKey ?? record.Roster.OwnerPublicKey;
        var used = 0;
        for (var number = first; ; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (used == budget) return (false, used);
            var piece = store.ReadStagedProofVersion(record, number) ??
                throw new InvalidDataException("A staged authority proof boundary is missing.");
            if (piece.ParentHash != prior?.VersionHash || piece.SigningPublicKey != expectedSigner ||
                number == record.Version.Number && piece.VersionHash != record.Version.VersionHash)
                throw new InvalidDataException("A staged authority proof boundary changed.");
            digest = WorldAuthorityTrust.AdvanceLineage(digest, piece);
            prior = piece;
            used++;
            if (number == record.Version.Number || used == budget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                store.SaveStagedProofSeal(record, parent, prior, digest);
                if (number == record.Version.Number &&
                    Convert.ToHexString(digest) != record.VersionLineageDigest)
                    throw new InvalidDataException("The staged authority proof digest changed.");
                return (number == record.Version.Number, used);
            }
        }
    }

    private async Task<ReceivedSharedWorldResult?> TrustSharedRosterAsync(Guid profileId,
        Guid deviceId, Guid hostId, string endpoint, string[] pins,
        HttpClient client, CancellationToken cancellationToken)
    {
        using var pcKey = LoadPcSigningKey(deviceId);
        var publicKey = Convert.ToBase64String(pcKey.ExportSubjectPublicKeyInfo());
        using var challengeResponse = await client.GetAsync(
            $"api/companion/servers/{profileId}/shared-world/enrollment", cancellationToken);
        if (!challengeResponse.IsSuccessStatusCode)
            return SharedFailure("EnrollmentDenied", "The Host did not allow this PC to enroll for this server.");
        var challengeBytes = await ReadBoundedSharedAsync(challengeResponse.Content, 512, cancellationToken);
        var challenge = challengeBytes is null ? null :
            JsonSerializer.Deserialize<SharedWorldEnrollmentChallenge>(challengeBytes, Json);
        if (challenge?.Nonce is null || challenge.Nonce.Length != 44)
            return SharedFailure("InvalidChallenge", "The Host sent an invalid enrollment challenge.");
        var proof = new SharedWorldEnrollmentRequest(challenge.Nonce, publicKey,
            Convert.ToBase64String(pcKey.SignData(SharedWorldRosterTrust.EnrollmentBasis(
                deviceId, challenge.Nonce, publicKey), HashAlgorithmName.SHA256)));
        using var enrollment = await client.PostAsJsonAsync(
            $"api/companion/servers/{profileId}/shared-world/enrollment", proof, Json, cancellationToken);
        if (!enrollment.IsSuccessStatusCode)
            return SharedFailure("KeyReviewRequired", "The Host did not accept this PC's signing identity. Ask the owner to review it.");
        using var response = await client.GetAsync(
            $"api/companion/servers/{profileId}/shared-world/roster",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await ReadBoundedSharedAsync(response.Content,
            SharedWorldService.MaximumManifestBytes, cancellationToken);
        if (!response.IsSuccessStatusCode || bytes is null)
            return SharedFailure("RosterUnavailable", "The current owner-signed roster is unavailable.");
        var roster = JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json);
        if (!SharedWorldRosterTrust.Verify(roster) || roster!.ProfileId != profileId)
            return SharedFailure("RosterRejected", "The owner-signed roster failed verification.");
        var authorityStoreForStage = new WorldAuthorityStore(data);
        IReadOnlyList<WorldAuthorityRecord> acceptedBeforePage;
        try { acceptedBeforePage = authorityStoreForStage.Read(profileId); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        { return SharedFailure("AuthorityRejected", "The saved authority history failed verification."); }
        var offset = Math.Max(0, acceptedBeforePage.Count - 1);
        using var authorityResponse = await client.GetAsync(
            $"api/companion/servers/{profileId}/shared-world/authority?offset={offset}",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var authorityBytes = await ReadBoundedSharedAsync(authorityResponse.Content,
            4 * 1024 * 1024, cancellationToken);
        if (!authorityResponse.IsSuccessStatusCode || authorityBytes is null)
            return SharedFailure("AuthorityUnavailable", "The signed authority history is unavailable.");
        List<WorldAuthorityRecord>? page;
        try { page = JsonSerializer.Deserialize<List<WorldAuthorityRecord>>(authorityBytes, Json); }
        catch (JsonException) { return SharedFailure("AuthorityRejected", "The authority history is invalid."); }
        if (page is null)
            return SharedFailure("AuthorityRejected", "The authority history changed or omitted a verified decision.");
        WorldAuthorityRecord[] authorityRecords;
        try { authorityRecords = NewAuthorityPage(acceptedBeforePage, page); }
        catch (InvalidDataException)
        { return SharedFailure("AuthorityRejected", "The authority history changed or omitted a verified decision."); }
        var stagedAuthorityProofs = new HashSet<string>(StringComparer.Ordinal);
        var remainingProofWork = WorldAuthorityTrust.ProofVersionsPerCheck;
        foreach (var record in authorityRecords.Where(item => item.VersionLineageDigest is not null))
        {
            var parent = authorityRecords.Concat(acceptedBeforePage).SingleOrDefault(item =>
                item.RecordHash == record.Proposal.ParentAuthorityHash);
            (bool Complete, int Used) staged;
            try
            {
                staged = await StageAuthorityProofBatchAsync(authorityStoreForStage, record, parent,
                    remainingProofWork, async (number, token) =>
                {
                    using var proofResponse = await client.GetAsync(
                        $"api/companion/servers/{profileId}/shared-world/versions/{number}",
                        HttpCompletionOption.ResponseHeadersRead, token);
                    var proofBytes = await ReadBoundedSharedAsync(proofResponse.Content,
                        SharedWorldService.MaximumManifestBytes, token);
                    return !proofResponse.IsSuccessStatusCode || proofBytes is null ? null :
                        JsonSerializer.Deserialize<SharedWorldVersion>(proofBytes, Json);
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or
                JsonException or InvalidDataException)
            { return SharedFailure("AuthorityRejected", "A signed authority boundary is missing or invalid."); }
            remainingProofWork -= staged.Used;
            if (!staged.Complete)
                return SharedFailure("AuthorityCatchUpPending",
                    "The signed save history is still being checked. Check again to continue.");
            (bool Complete, int Used) sealedProof;
            try
            {
                sealedProof = VerifyStagedAuthorityProofBatch(authorityStoreForStage,
                record, parent, remainingProofWork, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            { return SharedFailure("AuthorityRejected", "A staged authority proof boundary is missing or invalid."); }
            remainingProofWork -= sealedProof.Used;
            if (!sealedProof.Complete)
                return SharedFailure("AuthorityCatchUpPending",
                    "The signed save history is still being checked. Check again to continue.");
            stagedAuthorityProofs.Add(record.RecordHash);
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (config is null || config.DeviceId != deviceId || config.HostId != hostId ||
                config.Endpoint != endpoint || !AcceptedPins().SequenceEqual(pins) ||
                config.ConsentedSharedWorldProfiles?.Contains(profileId) != true ||
                withdrawnSharedConsent.ContainsKey(profileId))
                return SharedFailure("ConnectionChanged", "The saved Host connection changed while checking the roster.");
            config.SharedWorldSigningKeys ??= [];
            config.SharedRosterFloors ??= [];
            var pinned = config.SharedWorldSigningKeys.GetValueOrDefault(profileId);
            if (pinned is not null && roster?.OwnerPublicKey != pinned)
                return SharedFailure("SigningIdentityChanged", "The Host's world signing identity changed. Ask the owner to review it.");
            var floor = config.SharedRosterFloors.GetValueOrDefault(profileId);
            if (roster is not null && floor is not null && floor.GroupId != roster.GroupId &&
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != roster.GroupId)
            {
                config.PendingSharedWorldGroups ??= [];
                config.PendingSharedWorldGroups[profileId] = roster.GroupId;
                SaveConfig();
                return SharedFailure("SourceReviewRequired",
                    "The Host changed this save source. Turn Allow saves off, then on to review the new signed group.");
            }
            if (roster is null || !SharedWorldRosterTrust.Accept(roster, profileId, deviceId,
                    publicKey, pinned ?? roster.OwnerPublicKey, floor?.Epoch ?? 0, floor?.Revision ?? 0) ||
                floor is not null && roster.Epoch == floor.Epoch && roster.Revision == floor.Revision &&
                    roster.Signature != floor.Signature)
                return SharedFailure("RosterRejected", "The signed roster is invalid, older, or does not grant this PC Receive access.");
            try
            {
                var authorityStore = new WorldAuthorityStore(data);
                if (page.Count == 0 && authorityStore.HasState(profileId))
                    return SharedFailure("AuthorityRejected", "The Host omitted previously verified authority.");
                foreach (var record in authorityRecords)
                {
                    if (record.Schema == 2 &&
                        WorldAuthorityTrust.EffectiveHeads(authorityRecords)
                            .Any(head => head.RecordHash == record.RecordHash) &&
                        (record.Roster.Epoch != roster.Epoch ||
                        record.Roster.Revision != roster.Revision ||
                        record.Roster.Signature != roster.Signature))
                        return SharedFailure("AuthorityRejected",
                            "The signed resolution uses an older roster than this Host.");
                    var acceptedBefore = authorityStore.Read(profileId);
                    if (acceptedBefore.Any(item => item.RecordHash == record.RecordHash)) continue;
                    if (stagedAuthorityProofs.Contains(record.RecordHash))
                    {
                        var parent = acceptedBefore.SingleOrDefault(item =>
                            item.RecordHash == record.Proposal.ParentAuthorityHash);
                        authorityStore.AppendReceivedStaged(record, parent, profileId,
                            roster.GroupId, pinned ?? roster.OwnerPublicKey);
                        try { authorityStore.ClearStagedProof(record, parent); }
                        catch (IOException) { /* Verified authority is already durable. */ }
                    }
                    else
                        authorityStore.AppendReceived(record, profileId, roster.GroupId,
                            pinned ?? roster.OwnerPublicKey);
                }
                var accepted = authorityStore.Read(profileId);
                if (page.Count == WorldAuthorityTrust.PageSize)
                    return SharedFailure("AuthorityCatchUpPending",
                        "The signed authority history is still being checked. Check again to continue.");
                if (accepted.Count > 0)
                {
                    var heads = WorldAuthorityTrust.EffectiveHeads(accepted);
                    if (heads.Length != 1 || heads[0].Roster.Signature != roster.Signature ||
                        heads[0].Proposal.Schema is not (2 or 3) &&
                        !(heads[0].Proposal.Schema == 1 && heads[0].Proposal.Kind == "Planned" &&
                          heads[0].SuccessorReceipt is not null))
                        return SharedFailure("AuthorityRejected", "The roster and successor authority differ.");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException)
            { return SharedFailure("AuthorityRejected", "The signed authority history failed verification."); }
            // Persist the rollback floor before any manifest or chunks are trusted.
            config.SharedWorldSigningKeys[profileId] = roster.OwnerPublicKey;
            config.SharedRosterFloors[profileId] = new(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature);
            SaveConfig();
            // Retain the signed roster for recovery during a Host outage.
            data.SaveProtected($"shared-world-roster-{deviceId:N}-{profileId:N}.protected", bytes);
            return null;
        }
        finally { gate.Release(); }
    }

    private string ReceivedRoot(Guid profileId)
    {
        var path = Path.Combine(data.RootPath, "received-shared-worlds",
            config!.DeviceId.ToString("N"), profileId.ToString("N"));
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
        return path;
    }

    public async Task<ReceivedSharedWorldResult> SetSharedWorldConsentAsync(Guid profileId, bool enabled)
    {
        if (!TryRetain()) return SharedFailure("ConnectionClosed", "This connection is closing.");
        if (!enabled)
        {
            WithdrawSharedConsent(profileId);
            new SharedWorldVoteInbox(data).Retire(profileId);
        }
        await gate.WaitAsync();
        try
        {
            if (config is null) return SharedFailure("NotPaired", "Connect to a Host first.");
            config.ConsentedSharedWorldProfiles ??= [];
            var previouslyConsented = config.ConsentedSharedWorldProfiles.Contains(profileId);
            Guid? priorApproval = config.ApprovedSharedWorldGroups?.TryGetValue(profileId,
                out var approvedGroup) == true ? approvedGroup : null;
            Guid? priorPending = config.PendingSharedWorldGroups?.TryGetValue(profileId,
                out var pendingBefore) == true ? pendingBefore : null;
            config.ConsentedSharedWorldProfiles.Remove(profileId);
            if (enabled)
            {
                using var signingKey = LoadPcSigningKey();
                config.ConsentedSharedWorldProfiles.Add(profileId);
            }
            if (enabled && !previouslyConsented &&
                config.PendingSharedWorldGroups?.TryGetValue(profileId, out var pendingGroup) == true)
            {
                config.ApprovedSharedWorldGroups ??= [];
                config.ApprovedSharedWorldGroups[profileId] = pendingGroup;
                config.PendingSharedWorldGroups.Remove(profileId);
            }
            try { SaveConfig(); }
            catch
            {
                config.ConsentedSharedWorldProfiles.Remove(profileId);
                if (previouslyConsented) config.ConsentedSharedWorldProfiles.Add(profileId);
                if (priorApproval is Guid approved) config.ApprovedSharedWorldGroups![profileId] = approved;
                else config.ApprovedSharedWorldGroups?.Remove(profileId);
                if (priorPending is Guid pending) config.PendingSharedWorldGroups![profileId] = pending;
                if (previouslyConsented) lock (sharedReceiptSync)
                    withdrawnSharedConsent.TryRemove(profileId, out _);
                throw;
            }
            if (enabled) lock (sharedReceiptSync)
            {
                withdrawnSharedConsent.TryRemove(profileId, out _);
                if (sharedProfileCancellation.TryRemove(profileId, out var previous)) previous.Dispose();
            }
            lock (sharedScheduleSync)
            {
                sharedRetryAfter.Remove(profileId);
                sharedFailures.Remove(profileId);
                sharedTransfers.TryRemove(profileId, out _);
            }
            return new(true, "ConsentSaved", enabled ? "This PC may pull approved completed saves." :
                "This PC will no longer pull shared saves.");
        }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public ReceivedSharedWorldStatus SharedWorldStatus(Guid profileId)
    {
        if (!TryRetain()) return new(false, null, null, "Not paired");
        try
        {
            gate.Wait();
            try
            {
                if (config is null) return new(false, null, null, "Not paired");
                if (sharedTransfers.TryGetValue(profileId, out var active) &&
                    config.ConsentedSharedWorldProfiles?.Contains(profileId) == true) return active;
            }
            finally { gate.Release(); }
            return LocalSharedWorldStatus(profileId);
        }
        finally { ReleaseRetained(); }
    }

    public async Task<ReceivedSharedWorldResult> CheckSharedWorldAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (!TryRetain()) return SharedFailure("ConnectionClosed", "This connection is closing.");
        var entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            entered = true;
            if (config is null) return SharedFailure("NotPaired", "Connect to a Host first.");
            if (config.ConsentedSharedWorldProfiles?.Contains(profileId) != true)
                return SharedFailure("ConsentRequired", "Allow shared saves on this PC first.");
            var profile = view.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return SharedFailure("UnknownProfile", "This server is not assigned to this PC.");
            if (!view.HostCapabilities.Contains(CompanionProtocol.SharedWorldsCapability, StringComparer.Ordinal))
                return SharedFailure("SharedWorldsUpdateRequired", "Update the Host app to receive shared saves.");
            var endpoint = config.Endpoint;
            var hostId = config.HostId;
            var deviceId = config.DeviceId;
            var pins = AcceptedPins().ToArray();
            var root = ReceivedRoot(profileId);
            using var checkClient = MakeClient(endpoint, pins);
            checkClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.Credential);
            checkClient.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
            checkClient.DefaultRequestHeaders.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
            gate.Release();
            entered = false;
            var trustFailure = await TrustSharedRosterAsync(profileId, deviceId, hostId, endpoint,
                pins, checkClient, cancellationToken);
            if (trustFailure is not null) return trustFailure;
            using var response = await checkClient.GetAsync($"api/companion/servers/{profileId}/shared-world",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var bytes = await ReadBoundedSharedAsync(response.Content,
                SharedWorldService.MaximumManifestBytes, cancellationToken);
            if (withdrawnSharedConsent.ContainsKey(profileId))
                return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
            if (!response.IsSuccessStatusCode || bytes is null)
                return RemoteSharedDenial(bytes);
            var version = JsonSerializer.Deserialize<SharedWorldVersion>(bytes, Json);
            if (version is null || !SharedWorldService.VerifySignature(version) ||
                version.ProfileId != profileId || version.Game != profile.Kind)
                return SharedFailure("InvalidManifest", "The Host's shared save failed verification.");
            var old = ReadReceivedLatest(root);
            var resolvedAnchor = ResolvedAuthorityAnchor(profileId, version);
            await gate.WaitAsync(cancellationToken);
            entered = true;
            var chainAnchor = resolvedAnchor ?? (config is null ? null :
                NewestTrustedAnchor(config, profileId, old, version.GroupId));
            gate.Release();
            entered = false;
            var chain = old is not null && old.GroupId != version.GroupId ?
                new SharedChainCheck(true, false) :
                await VerifySharedChainAsync(profileId, chainAnchor, version, checkClient, cancellationToken);
            await gate.WaitAsync(cancellationToken);
            entered = true;
            if (config is null || config.Endpoint != endpoint || config.HostId != hostId ||
                config.DeviceId != deviceId || !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                !AcceptedPins().SequenceEqual(pins) || withdrawnSharedConsent.ContainsKey(profileId))
                return SharedFailure("ConnectionChanged", "The saved Host connection changed while checking.");
            if (config.SharedRosterFloors?.GetValueOrDefault(profileId)?.GroupId != version.GroupId)
                return SharedFailure("GroupMismatch", "The save version does not match the verified shared roster.");
            if ((ResolvedAuthorityAnchor(profileId, version) ?? NewestTrustedAnchor(config, profileId, old,
                    version.GroupId))?.VersionHash !=
                chainAnchor?.VersionHash)
                return SharedFailure("ConnectionChanged", "The checked Host save changed while verifying ancestry. Check again.");
            config.SharedWorldSigningKeys ??= [];
            if (!AuthorizedVersionSigner(profileId, version))
                return SharedFailure("SigningIdentityChanged", "The Host's world signing identity changed.");
            config.LastSharedHostGroups ??= [];
            if (resolvedAnchor is null &&
                config.LastSharedHostGroups.GetValueOrDefault(profileId) == version.GroupId &&
                config.LastSharedHostVersions?.GetValueOrDefault(profileId) > version.Number)
                return SharedFailure("VersionRollback", "The Host returned an older save version for this group.");
            if (chain.Pending)
                return SharedFailure("VersionCatchUpPending",
                    "The signed save history is still being checked. Check again to continue.");
            if (!chain.Valid)
            {
                if (!chain.Conflict && !IsObservedHeadFork(config, profileId, version) &&
                    !IsReceivedHistoryConflict(old, version))
                    return SharedFailure("VersionChainInvalid", "The Host's signed save ancestry could not be verified.");
                ObserveHistory(config, profileId, old, version, ancestryConflict: true);
                SaveConfig();
                return SharedFailure("VersionConflict", "The Host and this PC have competing signed save histories. Review them before receiving another save.");
            }
            RememberSourceReview(profileId, version, old);
            var conflict = resolvedAnchor is null && ObserveHistory(config, profileId, old, version);
            if (resolvedAnchor is not null) AcceptResolvedHistory(config, profileId, version);
            if (!conflict && resolvedAnchor is null)
                config.SharedWorldSigningKeys[profileId] = version.SigningPublicKey;
            SaveConfig();
            gate.Release();
            entered = false;
            if (conflict)
                return SharedFailure("VersionConflict", "The Host and this PC have competing signed save histories. Review them before receiving another save.");
            if (old?.VersionHash == version.VersionHash)
                await SendSharedReceiptAsync(profileId, old, deviceId, checkClient, cancellationToken);
            return new(true, "SharedWorldChecked", "Latest Host version checked securely.",
                LocalSharedWorldStatus(profileId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
            CryptographicException or InvalidDataException or UnauthorizedAccessException)
        { return SharedFailure("CheckFailed", "The latest Host save could not be checked: " + ex.Message); }
        finally { if (entered) gate.Release(); ReleaseRetained(); }
    }

    private ReceivedSharedWorldStatus LocalSharedWorldStatus(Guid profileId)
    {
        bool consent;
        bool review;
        bool conflict;
        long? hostVersion;
        string? hostHash;
        SharedRosterFloor? floor;
        string? root;
        gate.Wait();
        try
        {
            consent = config?.ConsentedSharedWorldProfiles?.Contains(profileId) == true;
            review = config?.PendingSharedWorldGroups?.ContainsKey(profileId) == true;
            conflict = config?.SharedWorldConflicts?.Contains(profileId) == true;
            hostVersion = config?.LastSharedHostVersions?.GetValueOrDefault(profileId);
            hostHash = config?.LastSharedHostHashes?.GetValueOrDefault(profileId);
            floor = config?.SharedRosterFloors?.GetValueOrDefault(profileId);
            root = config is null ? null : ReceivedRoot(profileId);
        }
        finally { gate.Release(); }
        if (root is null) return new(false, null, null, "Not paired");
        long? received = null;
        string? receivedHash = null;
        string? error = null;
        try
        {
            var latest = ReadReceivedLatest(root);
            received = latest?.Number;
            receivedHash = latest?.VersionHash;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or InvalidDataException)
        { error = "The stored save failed verification. The live world was not changed."; }
        return new(consent, hostVersion, received,
            DescribeReceivedHistory(consent, error, review, conflict,
                hostVersion, hostHash, received, receivedHash), error,
            RosterRevision: floor?.Revision,
            Trust: floor is null ? "Roster not verified" : "Owner signature and this PC's Receive grant verified when last checked");
    }

    internal static string DescribeReceivedHistory(bool consent, string? error, bool review,
        bool conflict, long? hostVersion, string? hostHash, long? receivedVersion, string? receivedHash) =>
        !consent ? "Consent off" : error is not null ? "Error" :
        review ? "Host save source changed. Turn Allow saves off, then on to approve the new signed group. Earlier verified copies stay here." :
        conflict || hostVersion == receivedVersion && hostVersion is not null && hostHash is not null &&
            receivedHash is not null && hostHash != receivedHash ? "Competing save histories. Review before receiving another save." :
        hostVersion is null ? "Host version not checked" :
        hostVersion == receivedVersion && hostHash is not null && hostHash == receivedHash ?
            "Up to date when last checked" : "Ready to pull";

    internal static bool IsObservedHeadFork(FriendConfiguration config, Guid profileId,
        SharedWorldVersion version)
    {
        if (config.LastSharedHostGroups?.GetValueOrDefault(profileId) != version.GroupId ||
            config.LastSharedHostHashes?.TryGetValue(profileId, out var priorHash) != true)
            return false;
        var priorNumber = config.LastSharedHostVersions?.GetValueOrDefault(profileId) ?? 0;
        return priorNumber == version.Number && priorHash != version.VersionHash ||
            priorNumber + 1 == version.Number && version.ParentHash != priorHash;
    }

    internal static SharedWorldVersion? NewestTrustedAnchor(FriendConfiguration config,
        Guid profileId, SharedWorldVersion? received, Guid groupId)
    {
        var checkedHead = config.LastSharedHostManifests?.GetValueOrDefault(profileId);
        if (checkedHead?.GroupId != groupId ||
            config.LastSharedHostHashes?.GetValueOrDefault(profileId) != checkedHead.VersionHash ||
            config.LastSharedHostVersions?.GetValueOrDefault(profileId) != checkedHead.Number)
            checkedHead = null;
        if (received?.GroupId != groupId) received = null;
        return checkedHead is not null && (received is null || checkedHead.Number >= received.Number)
            ? checkedHead : received;
    }

    internal static bool ObserveHistory(FriendConfiguration config, Guid profileId,
        SharedWorldVersion? old, SharedWorldVersion version, bool ancestryConflict = false)
    {
        config.SharedWorldConflicts ??= [];
        config.LastSharedHostGroups ??= [];
        config.LastSharedHostVersions ??= [];
        config.LastSharedHostHashes ??= [];
        config.LastSharedHostManifests ??= [];
        config.CompetingSharedHostManifests ??= [];
        var observedFork = IsObservedHeadFork(config, profileId, version);
        var conflict = ancestryConflict || observedFork || IsReceivedHistoryConflict(old, version) ||
            config.SharedWorldConflicts.Contains(profileId);
        if (conflict) config.SharedWorldConflicts.Add(profileId);
        if (ancestryConflict || observedFork)
        {
            if (!config.CompetingSharedHostManifests.TryGetValue(profileId, out var competing))
                config.CompetingSharedHostManifests[profileId] = competing = [];
            if (competing.All(head => head.VersionHash != version.VersionHash) && competing.Count < 8)
                competing.Add(version);
        }
        else if (!conflict)
        {
            config.LastSharedHostGroups[profileId] = version.GroupId;
            config.LastSharedHostVersions[profileId] = version.Number;
            config.LastSharedHostHashes[profileId] = version.VersionHash;
            config.LastSharedHostManifests[profileId] = version;
        }
        return conflict;
    }

    internal static bool CanCommitReceivedVersion(FriendConfiguration config, Guid profileId,
        SharedWorldVersion version) =>
        config.SharedWorldConflicts?.Contains(profileId) != true &&
        config.LastSharedHostGroups?.GetValueOrDefault(profileId) == version.GroupId &&
        config.LastSharedHostVersions?.GetValueOrDefault(profileId) == version.Number &&
        config.LastSharedHostHashes?.GetValueOrDefault(profileId) == version.VersionHash;

    private bool AuthorizedVersionSigner(Guid profileId, SharedWorldVersion version)
    {
        var pinnedOwner = config?.SharedWorldSigningKeys?.GetValueOrDefault(profileId);
        return AuthorizedVersionSignerForRecords(pinnedOwner, version,
            new WorldAuthorityStore(data).Read(profileId));
    }

    internal static bool AuthorizedVersionSignerForRecords(string? pinnedOwner,
        SharedWorldVersion version, IReadOnlyList<WorldAuthorityRecord> records)
    {
        if (pinnedOwner is null || !SharedWorldService.VerifySignature(version)) return false;
        if (records.Count == 0) return version.SigningPublicKey == pinnedOwner;
        var heads = WorldAuthorityTrust.EffectiveHeads(records);
        if (heads.Length != 1 || heads[0].Proposal.Schema is not (2 or 3) ||
            heads[0].Roster.OwnerPublicKey != pinnedOwner ||
            heads[0].Proposal.GroupId != version.GroupId) return false;
        var head = heads[0];
        if (version.VersionHash == head.Version.VersionHash)
            return true;
        if (version.Number <= head.Version.Number ||
            version.SigningPublicKey != head.Proposal.CandidatePublicKey ||
            version.Game != head.Version.Game || version.WorldId != head.Version.WorldId)
            return false;
        return version.Number != head.Version.Number + 1 ||
            version.ParentHash == head.Version.VersionHash;
    }

    internal static bool IsReceivedHistoryConflict(SharedWorldVersion? old, SharedWorldVersion version) =>
        old is not null && old.GroupId == version.GroupId &&
        (old.Number > version.Number ||
         old.Number == version.Number && old.VersionHash != version.VersionHash ||
         old.Number + 1 == version.Number && version.ParentHash != old.VersionHash);

    private SharedWorldVersion? ResolvedAuthorityAnchor(Guid profileId, SharedWorldVersion version)
    {
        return ResolvedAuthorityAnchorForRecords(
            config?.SharedWorldSigningKeys?.GetValueOrDefault(profileId), version,
            new WorldAuthorityStore(data).Read(profileId));
    }

    internal static SharedWorldVersion? ResolvedAuthorityAnchorForRecords(string? pinnedOwner,
        SharedWorldVersion version, IReadOnlyList<WorldAuthorityRecord> records)
    {
        if (!AuthorizedVersionSignerForRecords(pinnedOwner, version, records)) return null;
        var heads = WorldAuthorityTrust.EffectiveHeads(records);
        if (heads.Length != 1) return null;
        for (WorldAuthorityRecord? current = heads[0]; current is not null;
             current = records.SingleOrDefault(record =>
                 record.RecordHash == current.Proposal.ParentAuthorityHash))
        {
            if (current.Schema == 2 && version.Number >= current.Version.Number)
                return current.Version;
        }
        return null;
    }

    private static void AcceptResolvedHistory(FriendConfiguration config, Guid profileId,
        SharedWorldVersion version)
    {
        config.SharedWorldConflicts?.Remove(profileId);
        config.LastSharedHostGroups ??= [];
        config.LastSharedHostVersions ??= [];
        config.LastSharedHostHashes ??= [];
        config.LastSharedHostManifests ??= [];
        config.LastSharedHostGroups[profileId] = version.GroupId;
        config.LastSharedHostVersions[profileId] = version.Number;
        config.LastSharedHostHashes[profileId] = version.VersionHash;
        config.LastSharedHostManifests[profileId] = version;
    }

    private void RememberSourceReview(Guid profileId, SharedWorldVersion version,
        SharedWorldVersion? old)
    {
        config!.PendingSharedWorldGroups ??= [];
        if (old is not null && old.GroupId != version.GroupId &&
            config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != version.GroupId)
            config.PendingSharedWorldGroups[profileId] = version.GroupId;
        else
            config.PendingSharedWorldGroups.Remove(profileId);
    }

    public async Task<ReceivedSharedWorldResult> PullSharedWorldAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (!TryRetain()) return SharedFailure("ConnectionClosed", "This connection is closing.");
        var profileGate = sharedProfileGates.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        var profileEntered = false;
        var entered = false;
        try
        {
            await profileGate.WaitAsync(cancellationToken);
            profileEntered = true;
            await gate.WaitAsync(cancellationToken);
            entered = true;
            if (config is null) return SharedFailure("NotPaired", "Connect to a Host first.");
            if (config.ConsentedSharedWorldProfiles?.Contains(profileId) != true)
                return SharedFailure("ConsentRequired", "Allow shared saves on this PC first.");
            var profile = view.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return SharedFailure("UnknownProfile", "This server is not assigned to this PC.");
            if (profile.Kind == GameKinds.Custom)
                return SharedFailure("SharingUnsupported", "Custom game worlds cannot be shared.");
            if (!view.HostCapabilities.Contains(CompanionProtocol.SharedWorldsCapability, StringComparer.Ordinal))
                return SharedFailure("SharedWorldsUpdateRequired", "Update the Host app to receive shared saves.");
            var endpoint = config.Endpoint;
            var hostId = config.HostId;
            var deviceId = config.DeviceId;
            var pins = AcceptedPins().ToArray();
            var root = ReceivedRoot(profileId);
            using var transferClient = MakeClient(endpoint, pins);
            transferClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.Credential);
            transferClient.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
            transferClient.DefaultRequestHeaders.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
            CancellationTokenSource linked;
            lock (sharedReceiptSync)
            {
                var profileCancellation = sharedProfileCancellation.GetOrAdd(profileId,
                    _ => new CancellationTokenSource());
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                    profileCancellation.Token, sharedLinkCancellation.Token);
            }
            using var linkedLifetime = linked;
            var transferToken = linked.Token;
            gate.Release();
            entered = false;
            var trustFailure = await TrustSharedRosterAsync(profileId, deviceId, hostId, endpoint,
                pins, transferClient, transferToken);
            if (trustFailure is not null) return trustFailure;
            using var response = await transferClient.GetAsync($"api/companion/servers/{profileId}/shared-world",
                HttpCompletionOption.ResponseHeadersRead, transferToken);
            var manifestBytes = await ReadBoundedSharedAsync(response.Content,
                SharedWorldService.MaximumManifestBytes, transferToken);
            if (withdrawnSharedConsent.ContainsKey(profileId))
                return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
            if (manifestBytes is null) return SharedFailure("InvalidManifest", "The Host sent an oversized manifest.");
            if (!response.IsSuccessStatusCode)
                return RemoteSharedDenial(manifestBytes);
            SharedWorldVersion? version;
            try { version = JsonSerializer.Deserialize<SharedWorldVersion>(manifestBytes, Json); }
            catch (JsonException) { return SharedFailure("InvalidManifest", "The Host sent an invalid manifest."); }
            if (version is null || !SharedWorldService.VerifySignature(version) || version.ProfileId != profileId ||
                version.Game != profile.Kind || !SharedWorldSizeAllowed(version.Files))
                return SharedFailure("InvalidManifest", "The published version failed integrity or identity checks.");
            Directory.CreateDirectory(root);
            var old = ReadReceivedLatest(root);
            var resolvedAnchor = ResolvedAuthorityAnchor(profileId, version);
            await gate.WaitAsync(transferToken);
            entered = true;
            var chainAnchor = resolvedAnchor ?? (config is null ? null :
                NewestTrustedAnchor(config, profileId, old, version.GroupId));
            gate.Release();
            entered = false;
            var chain = old is not null && old.GroupId != version.GroupId ?
                new SharedChainCheck(true, false) :
                await VerifySharedChainAsync(profileId, chainAnchor, version, transferClient, transferToken);
            await gate.WaitAsync(transferToken);
            entered = true;
            if (config is null || config.Endpoint != endpoint || config.HostId != hostId ||
                config.DeviceId != deviceId || !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                !AcceptedPins().SequenceEqual(pins))
                return SharedFailure("ConnectionChanged", "The saved Host connection changed during transfer.");
            if (config.SharedRosterFloors?.GetValueOrDefault(profileId)?.GroupId != version.GroupId)
                return SharedFailure("GroupMismatch", "The save version does not match the verified shared roster.");
            if ((ResolvedAuthorityAnchor(profileId, version) ?? NewestTrustedAnchor(config, profileId, old,
                    version.GroupId))?.VersionHash !=
                chainAnchor?.VersionHash)
                return SharedFailure("ConnectionChanged", "The checked Host save changed while verifying ancestry. Try again.");
            config.SharedWorldSigningKeys ??= [];
            if (!AuthorizedVersionSigner(profileId, version))
                return SharedFailure("SigningIdentityChanged", "The Host's world signing identity changed. Ask the owner to review it.");
            config.LastSharedHostGroups ??= [];
            if (resolvedAnchor is null &&
                config.LastSharedHostGroups.GetValueOrDefault(profileId) == version.GroupId &&
                config.LastSharedHostVersions?.GetValueOrDefault(profileId) > version.Number)
                return SharedFailure("VersionRollback", "The Host returned an older save version for this group.");
            if (chain.Pending)
                return SharedFailure("VersionCatchUpPending",
                    "The signed save history is still being checked. Try again to continue.");
            if (!chain.Valid)
            {
                if (!chain.Conflict && !IsObservedHeadFork(config, profileId, version) &&
                    !IsReceivedHistoryConflict(old, version))
                    return SharedFailure("VersionChainInvalid", "This PC could not verify every missed version's parent hash.");
                ObserveHistory(config, profileId, old, version, ancestryConflict: true);
                SaveConfig();
                return SharedFailure("VersionConflict", "The Host and this PC have competing signed save histories. Review them before receiving another save.");
            }
            RememberSourceReview(profileId, version, old);
            var conflict = resolvedAnchor is null && ObserveHistory(config, profileId, old, version);
            if (resolvedAnchor is not null) AcceptResolvedHistory(config, profileId, version);
            if (!conflict && resolvedAnchor is null)
                config.SharedWorldSigningKeys[profileId] = version.SigningPublicKey;
            var approvedGroup = config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId);
            SaveConfig();
            gate.Release();
            entered = false;
            if (conflict)
                return SharedFailure("VersionConflict", "The Host and this PC have competing signed save histories. Review them before receiving another save.");
            PrunePartialStages(root, version.VersionHash);
            var newWorldGroup = old is not null && old.GroupId != version.GroupId &&
                approvedGroup == version.GroupId;
            if (old is not null && old.GroupId != version.GroupId && !newWorldGroup)
                return SharedFailure("SourceReviewRequired",
                    "The Host changed this save source. Turn Allow saves off, then on to approve the new signed group. Earlier verified copies stay here.");
            if (old is not null && !newWorldGroup && old.GroupId != version.GroupId)
                return SharedFailure("VersionConflict", "The published version does not continue this PC's verified world history.");
            if (old?.VersionHash == version.VersionHash)
            {
                await SendSharedReceiptAsync(profileId, old, deviceId, transferClient, transferToken);
                return new(true, "AlreadyReceived", "This PC already has the latest hash-verified post-Stop file copy. Game load has not been checked.",
                    LocalSharedWorldStatus(profileId));
            }
            var stage = Path.Combine(root, ".partial-" + version.VersionHash);
            if (Directory.Exists(stage) && (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                return SharedFailure("LinkedVault", "The receiving vault contains a linked folder.");
            Directory.CreateDirectory(stage);
            var payloadStage = Path.Combine(stage, SharedWorldService.PayloadDirectory);
            var remaining = SharedWorldService.BoundedTotalBytes(version.Files) -
                version.Files.Sum(file => ExistingPartialBytes(payloadStage, file));
            var totalBytes = SharedWorldService.BoundedTotalBytes(version.Files);
            var receivedBytes = totalBytes - remaining;
            sharedTransfers[profileId] = new(true, version.Number, old?.Number,
                "Receiving", null, receivedBytes, totalBytes,
                config.SharedRosterFloors[profileId].Revision,
                "Owner signature and this PC's Receive grant verified when last checked");
            var driveRoot = Path.GetPathRoot(root)!;
            if (!HasReceiverReserve(new DriveInfo(driveRoot).AvailableFreeSpace, remaining))
                return SharedFailure("InsufficientSpace", "Keep at least 1 GiB free after receiving this save. Existing verified copies were kept.");
            for (var index = 0; index < version.Files.Count; index++)
            {
                var file = version.Files[index];
                var path = SharedWorldService.SafeChild(payloadStage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (file.Length == 0)
                {
                    if (!File.Exists(path)) File.WriteAllBytes(path, []);
                    SharedWorldService.VerifyFile(path, file);
                    continue;
                }
                if (File.Exists(path) && new FileInfo(path).Length == file.Length)
                {
                    try { SharedWorldService.VerifyFile(path, file); continue; }
                    catch (InvalidDataException) { File.Delete(path); }
                }
                var offset = ResumeOffset(path, file);
                if (offset < 0)
                {
                    File.Delete(path);
                    offset = 0;
                }
                if (offset == 0 && File.Exists(path)) File.Delete(path);
                await using var output = OpenPartialOutput(path, offset);
                output.Position = offset;
                while (offset < file.Length)
                {
                    if (withdrawnSharedConsent.ContainsKey(profileId))
                        return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
                    using var chunkResponse = await transferClient.GetAsync(
                        $"api/companion/servers/{profileId}/shared-world/{version.VersionHash}/files/{index}/chunks/{offset}",
                        HttpCompletionOption.ResponseHeadersRead, transferToken);
                    var chunk = await ReadBoundedSharedAsync(chunkResponse.Content,
                        SharedWorldService.ChunkBytes, transferToken);
                    if (withdrawnSharedConsent.ContainsKey(profileId))
                        return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
                    var expected = (int)Math.Min(SharedWorldService.ChunkBytes, file.Length - offset);
                    if (!chunkResponse.IsSuccessStatusCode || chunk is null || chunk.Length != expected)
                        return !chunkResponse.IsSuccessStatusCode ? RemoteSharedDenial(chunk) :
                            SharedFailure("TransferInterrupted", "The Host stopped this transfer. Progress was kept for retry.");
                    await output.WriteAsync(chunk, transferToken);
                    offset += chunk.Length;
                    receivedBytes += chunk.Length;
                    sharedTransfers[profileId] = new(true, version.Number, old?.Number,
                        "Receiving", null, receivedBytes, totalBytes,
                        config.SharedRosterFloors[profileId].Revision,
                        "Owner signature and this PC's Receive grant verified when last checked");
                    // Disposable fixture runs can hold a completed chunk open so the
                    // packaged journey exercises cancellation and control concurrency.
                    if (Environment.GetEnvironmentVariable(GameServerRegistry.FixtureOptInEnvironmentVariable) == "1" &&
                        string.Equals(Environment.GetEnvironmentVariable("TOGETHERSERVER_FIXTURE_ROOT"),
                            data.RootPath, StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(Environment.GetEnvironmentVariable("TOGETHERSERVER_FIXTURE_RECEIVE_DELAY_MS"),
                            out var fixtureDelay) && fixtureDelay is > 0 and <= 5000)
                        await Task.Delay(fixtureDelay, transferToken);
                }
                await output.FlushAsync(transferToken);
                await output.DisposeAsync();
                SharedWorldService.VerifyFile(path, file);
            }
            foreach (var file in version.Files)
                SharedWorldService.VerifyFile(SharedWorldService.SafeChild(payloadStage, file.Path), file);
            using var latestResponse = await transferClient.GetAsync(
                $"api/companion/servers/{profileId}/shared-world", HttpCompletionOption.ResponseHeadersRead, transferToken);
            var latestBytes = await ReadBoundedSharedAsync(latestResponse.Content,
                SharedWorldService.MaximumManifestBytes, transferToken);
            if (!latestResponse.IsSuccessStatusCode || latestBytes is null)
                return SharedFailure("LatestCheckFailed", "The latest Host version could not be checked before saving.");
            var latest = JsonSerializer.Deserialize<SharedWorldVersion>(latestBytes, Json);
            if (latest is null || !SharedWorldService.VerifySignature(latest) ||
                latest.ProfileId != profileId || latest.SigningPublicKey != version.SigningPublicKey)
                return SharedFailure("InvalidManifest", "The latest Host version failed verification.");
            if (latest.VersionHash != version.VersionHash)
                return SharedFailure("NewerVersionAvailable", "The Host published another completed save. Receiving the latest version next.");
            // A grant can expire or be revoked while chunks are in flight.
            var currentGrant = await TrustSharedRosterAsync(profileId, deviceId, hostId, endpoint,
                pins, transferClient, transferToken);
            if (currentGrant is not null) return currentGrant;
            if (!AuthorizedVersionSigner(profileId, version))
                return SharedFailure("AuthorityRejected", "Authority changed during save transfer.");
            if (withdrawnSharedConsent.ContainsKey(profileId))
                return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
            File.WriteAllBytes(Path.Combine(stage, "version.json"), manifestBytes);
            var destination = Path.Combine(root, version.VersionHash);
            if (Directory.Exists(destination))
            {
                var retained = ReadVersionForRetention(destination);
                if (retained?.VersionHash != version.VersionHash)
                    throw new InvalidDataException("Existing received payload failed verification.");
                lock (sharedReceiptSync)
                {
                    if (withdrawnSharedConsent.ContainsKey(profileId) || transferToken.IsCancellationRequested)
                        return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
                    var latestStage = Path.Combine(root, "latest.json.new");
                    File.WriteAllBytes(latestStage, manifestBytes);
                    File.Move(latestStage, Path.Combine(root, "latest.json"), true);
                }
                return new(true, "SaveReceived", "The selected verified copy is now current. Earlier copies remain intact.",
                    LocalSharedWorldStatus(profileId));
            }
            await gate.WaitAsync(transferToken);
            entered = true;
            if (config is null || config.Endpoint != endpoint || config.HostId != hostId ||
                config.DeviceId != deviceId || !config.ConsentedSharedWorldProfiles.Contains(profileId) ||
                !AcceptedPins().SequenceEqual(pins) ||
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != approvedGroup)
                return SharedFailure("ConnectionChanged", "The saved Host connection changed during transfer.");
            if (config.SharedWorldConflicts?.Contains(profileId) == true)
                return SharedFailure("VersionConflict", "A competing signed save was observed during transfer. Review the histories before receiving another save.");
            if (!CanCommitReceivedVersion(config, profileId, version))
                return SharedFailure("NewerVersionAvailable", "The checked Host save changed during transfer. Receive the latest version next.");
            transferToken.ThrowIfCancellationRequested();
            if (!CommitSharedReceipt(profileId, stage, destination, root, manifestBytes, transferToken))
                return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
            if (resolvedAnchor is null)
                config.SharedWorldSigningKeys[profileId] = version.SigningPublicKey;
            SaveConfig();
            gate.Release();
            entered = false;
            var copyConfirmed = await SendSharedReceiptAsync(profileId, version, deviceId,
                transferClient, transferToken, verifiedInThisTransfer: true);
            try
            {
                var protectedHashes = new SharedWorldSeparateCopyStore(data).Read(profileId)
                    .Select(branch => branch.Offer.Version.VersionHash)
                    .Concat(new WorldAuthorityStore(data).Read(profileId)
                        .Select(record => record.Version.VersionHash))
                    .ToHashSet(StringComparer.Ordinal);
                if (old is not null) protectedHashes.Add(old.VersionHash);
                // A signed resolution is a permanent branch choice. Keep every
                // verified received payload so losing history cannot be pruned.
                if (!new WorldAuthorityStore(data).Read(profileId).Any(record => record.Schema == 2))
                    PruneReceived(root, version.VersionHash, protectedHashes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { /* A verified receipt is kept even if old-version cleanup fails. */ }
            return new(true, "SaveReceived", copyConfirmed ?
                    "A post-Stop file copy passed its hash checks here and was confirmed to the Host. Game load has not been checked." :
                    "A post-Stop file copy passed its hash checks here. Game load has not been checked. Host confirmation is pending; retry when connected.",
                LocalSharedWorldStatus(profileId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return SharedFailure("TransferInterrupted", "The transfer stopped; progress was kept for retry."); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
            CryptographicException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        { return SharedFailure("TransferFailed", "The transfer stopped; verified copies were kept. " + ex.Message); }
        finally { sharedTransfers.TryRemove(profileId, out _); if (entered) gate.Release(); if (profileEntered) profileGate.Release(); ReleaseRetained(); }
    }

    internal static bool HasReceiverReserve(long freeBytes, long remainingBytes) =>
        freeBytes >= ReceiverReserveBytes && remainingBytes >= 0 &&
        freeBytes - ReceiverReserveBytes >= remainingBytes;

    private static bool SharedWorldSizeAllowed(IReadOnlyList<SharedWorldFile> files)
    {
        try { SharedWorldService.BoundedTotalBytes(files); return true; }
        catch (InvalidDataException) { return false; }
    }

    internal void WithdrawSharedConsent(Guid profileId)
    {
        lock (sharedReceiptSync)
        {
            withdrawnSharedConsent[profileId] = 1;
            if (sharedProfileCancellation.TryGetValue(profileId, out var cancellation)) cancellation.Cancel();
        }
    }

    internal bool CommitSharedReceipt(Guid profileId, string stage, string destination,
        string root, byte[] manifestBytes, CancellationToken cancellationToken = default)
    {
        lock (sharedReceiptSync)
        {
            if (cancellationToken.IsCancellationRequested || withdrawnSharedConsent.ContainsKey(profileId)) return false;
            Directory.Move(stage, destination);
            var latestStage = Path.Combine(root, "latest.json.new");
            File.WriteAllBytes(latestStage, manifestBytes);
            File.Move(latestStage, Path.Combine(root, "latest.json"), true);
            return true;
        }
    }

    internal sealed record SharedChainCheck(bool Valid, bool Conflict, bool Pending = false);
    private sealed record SharedChainProgress(int Schema, Guid GroupId, string AnchorHash,
        string? AuthorityHeadHash, SharedWorldVersion Last);
    private static string ChainProgressName(Guid deviceId, Guid profileId) =>
        $"shared-chain-{deviceId:N}-{profileId:N}.protected";

    private async Task<SharedChainCheck> VerifySharedChainAsync(Guid profileId, SharedWorldVersion? anchor,
        SharedWorldVersion latest, HttpClient transferClient, CancellationToken cancellationToken)
    {
        var records = new WorldAuthorityStore(data).Read(profileId);
        anchor ??= records.OrderBy(record => record.Proposal.Epoch).FirstOrDefault()?.Version;
        if (anchor is null) return new(records.Count == 0 &&
            latest.SigningPublicKey == config?.SharedWorldSigningKeys?.GetValueOrDefault(profileId), false);
        var pending = new Dictionary<long, SharedWorldVersion>();
        return await VerifySharedChainBatchAsync(data, config?.DeviceId ?? Guid.Empty, profileId,
            anchor, latest, records, async (number, token) =>
            {
                if (withdrawnSharedConsent.ContainsKey(profileId)) return null;
                if (!pending.TryGetValue(number, out var item))
                {
                    var count = checked((int)Math.Min(WorldAuthorityTrust.ChainVersionsPerCheck,
                        latest.Number - number));
                    using var response = await transferClient.GetAsync(
                        $"api/companion/servers/{profileId}/shared-world/versions/range/{number}/{count}",
                        HttpCompletionOption.ResponseHeadersRead, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        // An older companion has only the single-version route.
                        // A JSON denial from a current Host must not fall back.
                        if (response.StatusCode != System.Net.HttpStatusCode.NotFound ||
                            response.Content.Headers.ContentType?.MediaType?.Contains("json",
                                StringComparison.OrdinalIgnoreCase) == true)
                            return null;
                        using var older = await transferClient.GetAsync(
                            $"api/companion/servers/{profileId}/shared-world/versions/{number}",
                            HttpCompletionOption.ResponseHeadersRead, token);
                        var olderBytes = await ReadBoundedSharedAsync(older.Content,
                            SharedWorldService.MaximumManifestBytes, token);
                        return !older.IsSuccessStatusCode || olderBytes is null ? null :
                            JsonSerializer.Deserialize<SharedWorldVersion>(olderBytes, Json);
                    }
                    var bytes = await ReadBoundedSharedAsync(response.Content,
                        SharedWorldService.MaximumManifestBytes * count + 4096L, token);
                    var range = bytes is null ? null :
                        JsonSerializer.Deserialize<SharedWorldVersion[]>(bytes, Json);
                    if (range?.Length != count || range.Where((value, index) =>
                            value is null || value.Number != number + index).Any())
                        return null;
                    foreach (var value in range) pending.Add(value.Number, value);
                    item = pending[number];
                }
                pending.Remove(number);
                return item;
            }, cancellationToken);
    }

    internal static async Task<SharedChainCheck> VerifySharedChainBatchAsync(LocalData data,
        Guid deviceId, Guid profileId, SharedWorldVersion anchor, SharedWorldVersion latest,
        IReadOnlyList<WorldAuthorityRecord> records,
        Func<long, CancellationToken, Task<SharedWorldVersion?>> fetch,
        CancellationToken cancellationToken)
    {
        if (latest.Number < anchor.Number) return new(false, true);
        if (!SharedWorldService.VerifySignature(anchor) ||
            records.Any(record => !WorldAuthorityTrust.Verify(record) ||
                record.Proposal.GroupId != anchor.GroupId || record.Version.ProfileId != anchor.ProfileId ||
                record.Version.Game != anchor.Game || record.Version.WorldId != anchor.WorldId) ||
            !ValidAtAuthorityBoundary(anchor, records)) return new(false, false);
        var progressName = ChainProgressName(deviceId, profileId);
        var authorityHeadHash = records.LastOrDefault()?.RecordHash;
        var savedBytes = data.LoadProtected(progressName);
        SharedChainProgress? saved = null;
        if (savedBytes is not null)
        {
            try { saved = JsonSerializer.Deserialize<SharedChainProgress>(savedBytes, Json); }
            catch (JsonException) { /* An unusable cursor only requires rechecking from the anchor. */ }
        }
        var prior = anchor;
        if (saved is { Schema: 1 } && saved.GroupId == anchor.GroupId &&
            saved.AnchorHash == anchor.VersionHash && saved.AuthorityHeadHash == authorityHeadHash &&
            saved.Last.Number >= anchor.Number &&
            saved.Last.GroupId == anchor.GroupId && saved.Last.ProfileId == anchor.ProfileId &&
            saved.Last.Game == anchor.Game && saved.Last.WorldId == anchor.WorldId &&
            SharedWorldService.VerifySignature(saved.Last) &&
            ValidAtAuthorityBoundary(saved.Last, records))
            prior = saved.Last;
        if (prior.Number > latest.Number) return new(false, true);
        var used = 0;
        while (prior.Number < latest.Number && used < WorldAuthorityTrust.ChainVersionsPerCheck)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = checked(prior.Number + 1);
            var item = number == latest.Number ? latest : await fetch(number, cancellationToken);
            if (item is null || !SharedWorldService.VerifySignature(item) || item.Number != number ||
                item.GroupId != anchor.GroupId || item.ProfileId != anchor.ProfileId ||
                item.Game != anchor.Game || item.WorldId != anchor.WorldId)
                return new(false, false);
            if (item.ParentHash != prior.VersionHash ||
                !ValidTransition(prior, item, records) || !ValidAtAuthorityBoundary(item, records))
                return new(false, true);
            prior = item;
            used++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (used > 0)
            data.SaveProtected(progressName, JsonSerializer.SerializeToUtf8Bytes(
                new SharedChainProgress(1, anchor.GroupId, anchor.VersionHash, authorityHeadHash, prior), Json));
        if (prior.Number < latest.Number) return new(false, false, true);
        return new(prior.VersionHash == latest.VersionHash,
            prior.VersionHash != latest.VersionHash);
    }

    internal static bool VerifySharedChain(SharedWorldVersion old, SharedWorldVersion latest,
        IReadOnlyList<SharedWorldVersion> ancestors, WorldAuthorityRecord? authority = null)
        => VerifySharedChain(old, latest, ancestors,
            authority is null ? [] : [authority]);

    internal static bool VerifySharedChain(SharedWorldVersion old, SharedWorldVersion latest,
        IReadOnlyList<SharedWorldVersion> ancestors, IReadOnlyList<WorldAuthorityRecord> authorities)
    {
        if (old.Number >= latest.Number || ancestors.Count != latest.Number - old.Number - 1 ||
            old.GroupId != latest.GroupId || old.ProfileId != latest.ProfileId ||
            old.Game != latest.Game || old.WorldId != latest.WorldId ||
            !SharedWorldService.VerifySignature(old) || !SharedWorldService.VerifySignature(latest)) return false;
        if (authorities.Any(record => !WorldAuthorityTrust.Verify(record) ||
            record.Proposal.GroupId != old.GroupId || record.Version.ProfileId != old.ProfileId ||
            record.Version.Game != old.Game || record.Version.WorldId != old.WorldId) ||
            !ValidAtAuthorityBoundary(old, authorities)) return false;
        var parent = old.VersionHash;
        var prior = old;
        for (var i = 0; i < ancestors.Count; i++)
        {
            var item = ancestors[i];
            if (!SharedWorldService.VerifySignature(item) || item.Number != old.Number + i + 1 ||
                item.GroupId != latest.GroupId || item.ProfileId != latest.ProfileId ||
                item.Game != latest.Game || item.WorldId != latest.WorldId ||
                !ValidTransition(prior, item, authorities) || item.ParentHash != parent ||
                !ValidAtAuthorityBoundary(item, authorities)) return false;
            parent = item.VersionHash;
            prior = item;
        }
        return latest.ParentHash == parent && ValidTransition(prior, latest, authorities) &&
            ValidAtAuthorityBoundary(latest, authorities);
    }

    private static bool ValidAtAuthorityBoundary(SharedWorldVersion version,
        IReadOnlyList<WorldAuthorityRecord> authorities)
    {
        var decision = authorities.LastOrDefault(record => record.Schema == 2);
        if (decision is not null && version.Number >= decision.Version.Number)
            return version.Number != decision.Version.Number ||
                version.VersionHash == decision.Version.VersionHash;
        return authorities.Where(record => record.Version.Number == version.Number)
            .All(record => record.Version.VersionHash == version.VersionHash);
    }

    private static bool ValidTransition(SharedWorldVersion previous, SharedWorldVersion next,
        IReadOnlyList<WorldAuthorityRecord> authorities)
    {
        var boundary = authorities.Where(record => record.Version.Number <= previous.Number)
            .OrderBy(record => record.Proposal.Epoch).LastOrDefault();
        if (boundary is null) return previous.SigningPublicKey == next.SigningPublicKey;
        if (previous.Number == boundary.Version.Number &&
            previous.VersionHash != boundary.Version.VersionHash) return false;
        return next.SigningPublicKey == boundary.Proposal.CandidatePublicKey &&
            (previous.Number == boundary.Version.Number ||
             previous.SigningPublicKey == boundary.Proposal.CandidatePublicKey);
    }

    internal static long ExistingPartialBytes(string root, SharedWorldFile file)
    {
        var path = SharedWorldService.SafeChild(root, file.Path);
        var offset = ResumeOffset(path, file);
        if (offset == file.Length && offset > 0)
        {
            try { SharedWorldService.VerifyFile(path, file); }
            catch (InvalidDataException) { return 0; }
        }
        return offset > 0 ? offset : 0;
    }

    internal static long ResumeOffset(string path, SharedWorldFile file)
    {
        if (!File.Exists(path)) return 0;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return -1;
        var length = new FileInfo(path).Length;
        return length <= file.Length && length % SharedWorldService.ChunkBytes == 0 ? length : -1;
    }

    internal static FileStream OpenPartialOutput(string path, long offset) =>
        new(path, offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write,
            FileShare.None, SharedWorldService.ChunkBytes, FileOptions.WriteThrough);

    private async Task<bool> SendSharedReceiptAsync(Guid profileId, SharedWorldVersion version,
        Guid deviceId, HttpClient client, CancellationToken cancellationToken,
        bool verifiedInThisTransfer = false)
    {
        SharedRosterFloor? floor;
        string root;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (config is null || config.DeviceId != deviceId ||
                config.ConsentedSharedWorldProfiles?.Contains(profileId) != true ||
                withdrawnSharedConsent.ContainsKey(profileId)) return false;
            floor = config.SharedRosterFloors?.GetValueOrDefault(profileId);
            root = ReceivedRoot(profileId);
        }
        finally { gate.Release(); }
        if (floor is null || floor.GroupId != version.GroupId) return false;
        // ReadReceivedLatest verifies every payload hash immediately before attesting.
        if (ReadReceivedLatest(root)?.VersionHash != version.VersionHash) return false;
        if (!AuthorizedVersionSigner(profileId, version)) return false;
        using var key = LoadPcSigningKey(deviceId);
        var receiptFile = Path.Combine(root, version.VersionHash, "receipt.json");
        SharedWorldReceipt? receipt = null;
        if (File.Exists(receiptFile))
        {
            if (new FileInfo(receiptFile).Length > 4096 ||
                (File.GetAttributes(receiptFile) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Stored copy receipt is invalid or linked.");
            receipt = JsonSerializer.Deserialize<SharedWorldReceipt>(File.ReadAllBytes(receiptFile), Json);
            if (receipt is not null && (!SharedWorldReceiptTrust.Verify(receipt,
                    Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) ||
                receipt.DeviceId != deviceId || receipt.ProfileId != profileId ||
                receipt.VersionHash != version.VersionHash || receipt.GroupId != version.GroupId))
                throw new InvalidDataException("Stored copy receipt failed verification.");
        }
        if (receipt is null || receipt.RosterEpoch != floor.Epoch || receipt.RosterRevision != floor.Revision)
        {
            if (!verifiedInThisTransfer) return false;
            var draft = new SharedWorldReceipt(1, version.GroupId, profileId, version.VersionHash,
                deviceId, floor.Epoch, floor.Revision, Guid.NewGuid(), "");
            receipt = draft with
            {
                Signature = Convert.ToBase64String(key.SignData(
                SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256))
            };
            var stage = receiptFile + "." + Guid.NewGuid().ToString("N") + ".new";
            File.WriteAllBytes(stage, JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
            File.Move(stage, receiptFile, true);
        }
        try
        {
            using var response = await client.PostAsJsonAsync(
                $"api/companion/servers/{profileId}/shared-world/receipts", receipt, Json, cancellationToken);
            return response.IsSuccessStatusCode && !withdrawnSharedConsent.ContainsKey(profileId);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        { return false; }
    }

    private ECDsa LoadPcSigningKey() => LoadPcSigningKey(config!.DeviceId);

    private ECDsa LoadPcSigningKey(Guid deviceId)
    {
        var path = $"shared-world-pc-signing-{deviceId:N}.protected";
        var bytes = data.LoadProtected(path);
        if (bytes is null)
        {
            using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            bytes = created.ExportPkcs8PrivateKey();
            data.SaveProtected(path, bytes);
        }
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(bytes, out _);
        return key;
    }

    internal static SharedWorldVersion? ReadReceivedLatest(string root)
    {
        var path = Path.Combine(root, "latest.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > SharedWorldService.MaximumManifestBytes ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The received version metadata is oversized or linked.");
        var version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json);
        if (version is null || !SharedWorldService.VerifySignature(version) ||
            !Directory.Exists(Path.Combine(root, version.VersionHash)))
            throw new InvalidDataException("The prior received version failed verification.");
        foreach (var file in version.Files)
            SharedWorldService.VerifyFile(SharedWorldService.SafeChild(
                Path.Combine(root, version.VersionHash, SharedWorldService.PayloadDirectory), file.Path), file);
        return version;
    }

    internal static void PruneReceived(string root, string newest,
        IReadOnlySet<string>? preservedHistoryHashes = null)
    {
        var versions = Directory.EnumerateDirectories(root)
            .Where(path => Path.GetFileName(path).Length == 64 &&
                Path.GetFileName(path).All(Uri.IsHexDigit))
            .Select(path => (Path: path, Version: ReadVersionForRetention(path)))
            .Where(item => item.Version is not null && SharedWorldService.VerifySignature(item.Version))
            .ToList();
        foreach (var group in versions.GroupBy(item => item.Version!.GroupId))
        {
            var keep = group.OrderByDescending(item => item.Version!.Number)
                .Take(3).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            keep.Add(Path.Combine(root, newest));
            if (preservedHistoryHashes is not null)
                foreach (var hash in preservedHistoryHashes.Where(hash => hash.Length == 64 &&
                    hash.All(Uri.IsHexDigit)))
                    keep.Add(Path.Combine(root, hash));
            foreach (var old in group.Where(item => !keep.Contains(item.Path)))
                if (!ContainsReparsePoint(old.Path)) Directory.Delete(old.Path, true);
        }
    }

    private static void PrunePartialStages(string root, string currentHash)
    {
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(".partial-", StringComparison.Ordinal) ||
                name == ".partial-" + currentHash ||
                name.Length != ".partial-".Length + 64 ||
                !name[".partial-".Length..].All(Uri.IsHexDigit) || ContainsReparsePoint(path)) continue;
            Directory.Delete(path, true);
        }
    }

    private static SharedWorldVersion? ReadVersionForRetention(string path)
    {
        try
        {
            var file = Path.Combine(path, "version.json");
            if (!File.Exists(file) || new FileInfo(file).Length > SharedWorldService.MaximumManifestBytes ||
                ContainsReparsePoint(path)) return null;
            var version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(file), Json);
            if (version is null || !SharedWorldService.VerifySignature(version) ||
                !Path.GetFileName(path).Equals(version.VersionHash, StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var item in version.Files)
                SharedWorldService.VerifyFile(SharedWorldService.SafeChild(
                    Path.Combine(path, SharedWorldService.PayloadDirectory), item.Path), item);
            return version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return null; }
    }

    private static bool ContainsReparsePoint(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) return true;
                if (Directory.Exists(child)) queue.Enqueue(child);
            }
        }
        return false;
    }

    internal static async Task<byte[]?> ReadBoundedSharedAsync(HttpContent content, long maximum,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximum) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximum) return null;
            output.Write(buffer, 0, read);
        }
    }

    private static ReceivedSharedWorldResult SharedFailure(string code, string message) =>
        new(false, code, message);

    private static ReceivedSharedWorldResult RemoteSharedDenial(byte[]? payload)
    {
        if (payload is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("code", out var codeElement) &&
                    codeElement.GetString() is { } code)
                {
                    return code switch
                    {
                        "Revoked" => SharedFailure(code, "The Host removed this PC's access."),
                        "AccessExpired" => SharedFailure(code, "The Host ended this PC's access at its saved deadline."),
                        "ApprovalPending" => SharedFailure(code, "The Host has not approved this PC."),
                        "PermissionDenied" => SharedFailure(code, "The Host has not granted this PC shared save access."),
                        "SharingOff" => SharedFailure(code, "The Host turned off sharing for this server."),
                        "NoPublishedSave" => SharedFailure(code, "The Host has no completed post-Stop save yet."),
                        "SharedVersionUnavailable" => SharedFailure(code,
                            "This save version is no longer available from the Host. Check for the latest save."),
                        "SharedChunkUnavailable" => SharedFailure(code,
                            "This save payload is no longer available from the Host. Check for the latest save."),
                        _ => SharedFailure("HostDenied", "The Host denied this shared save read.")
                    };
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        return SharedFailure("HostDenied", "The Host denied this shared save read.");
    }
}
