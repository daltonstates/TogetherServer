using System.Security.Cryptography;
using System.Text;

namespace TogetherServer;

// A bounded, signed offer that voters can compare with their own verified
// vault. Transport must be pinned HTTPS to the candidate's reviewed address;
// this type alone never authorizes Start or opens a listener.
public sealed record WorldAuthorityOffer(WorldAuthorityProposal Proposal,
    SharedWorldRoster Roster, SharedWorldVersion Version,
    SharedWorldReceipt CandidateReceipt,
    IReadOnlyList<SharedWorldVersion> Ancestors,
    string CandidateTlsFingerprint, string CandidateTlsProof);

internal static class SharedWorldElection
{
    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);
    internal static WorldAuthorityOffer PrepareResolutionOffer(string receivedRoot,
        SharedWorldRoster roster, SharedRosterFloor floor, Guid candidateId,
        ECDsa candidateKey, string candidateAddress, string candidateTlsFingerprint,
        WorldAuthorityStore authority, string selectedHeadHash, bool ownerOverride = false)
    {
        var heads = WorldAuthorityTrust.EffectiveHeads(authority.Read(roster.ProfileId));
        var selected = heads.SingleOrDefault(head => head.RecordHash == selectedHeadHash);
        var version = FriendLink.ReadReceivedLatest(receivedRoot);
        var candidatePublicKey = Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo());
        if (heads.Length < 2 || selected is null || version?.VersionHash != selected.Version.VersionHash ||
            ownerOverride && !roster.OwnerOverride ||
            !TrustedResolutionRoster(roster, floor, version, authority) ||
            roster.OwnerPublicKey != selected.Roster.OwnerPublicKey ||
            !SharedWorldRosterTrust.HasRole(roster, candidateId, candidatePublicKey,
                grants => grants.EligibleHost && grants.Receive) ||
            !HostIdentity.TryEndpoint(candidateAddress, out var endpoint) ||
            endpoint.GetLeftPart(UriPartial.Authority) != candidateAddress.TrimEnd('/') ||
            !ValidFingerprint(candidateTlsFingerprint))
            throw new InvalidDataException("The candidate, selected verified copy, or exact split is unavailable.");
        var hostingPublicKey = authority.PrepareLocalHostingKey(roster.ProfileId);
        var namedHeads = heads.Select(head => head.RecordHash).Order(StringComparer.Ordinal).ToArray();
        var draft = new WorldAuthorityProposal(3, roster.GroupId, roster.ProfileId,
            heads.Max(head => head.Proposal.Epoch) + 1, selected.RecordHash,
            WorldAuthorityTrust.RosterHash(roster), version.VersionHash, hostingPublicKey,
            candidateAddress, ownerOverride ? "ResolutionOwnerOverride" : "ResolutionQuorum",
            candidateId, candidatePublicKey, "",
            CompetingHeadHashes: namedHeads);
        var unsignedBinding = new WorldSuccessorBinding(candidateId, candidatePublicKey,
            hostingPublicKey, "");
        draft = draft with
        {
            SuccessorBinding = unsignedBinding with
            {
                Signature = Convert.ToBase64String(candidateKey.SignData(
                WorldAuthorityTrust.BindingBasis(draft, unsignedBinding), HashAlgorithmName.SHA256))
            }
        };
        var proposal = draft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            WorldAuthorityTrust.ProposalBasis(draft), HashAlgorithmName.SHA256))
        };
        var receiptDraft = new SharedWorldReceipt(1, roster.GroupId, roster.ProfileId,
            version.VersionHash, candidateId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
        var receipt = receiptDraft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256))
        };
        return new(proposal, roster, version, receipt, [], candidateTlsFingerprint,
            Convert.ToBase64String(candidateKey.SignData(
                TransportBasis(proposal, candidateTlsFingerprint), HashAlgorithmName.SHA256)));
    }
    internal static WorldAuthorityOffer PrepareOffer(SharedWorldHostLoss loss,
        string receivedRoot, SharedWorldRoster roster, SharedRosterFloor floor,
        Guid candidateId, ECDsa candidateKey, string candidateAddress,
        string candidateTlsFingerprint,
        WorldAuthorityStore authority, IReadOnlyList<SharedWorldVersion>? ancestors = null)
    {
        if (!loss.MayPropose)
            throw new InvalidDataException("The pinned Host has not been unreachable for two minutes.");
        var version = FriendLink.ReadReceivedLatest(receivedRoot) ??
            throw new InvalidDataException("This PC has no hash-verified post-Stop file copy to offer.");
        var candidatePublicKey = Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo());
        if (!TrustedRoster(roster, floor, version, authority) ||
            !SharedWorldRosterTrust.HasRole(roster, candidateId, candidatePublicKey,
                grants => grants.EligibleHost && grants.Receive) ||
            !HostIdentity.TryEndpoint(candidateAddress, out var endpoint) ||
            endpoint.GetLeftPart(UriPartial.Authority) != candidateAddress.TrimEnd('/') ||
            !ValidFingerprint(candidateTlsFingerprint))
            throw new InvalidDataException("The candidate, roster, or direct address is not approved.");
        var prior = authority.Read(roster.ProfileId);
        if (prior.Any(record => !WorldAuthorityTrust.Verify(record) ||
                record.Proposal.GroupId != roster.GroupId || record.Proposal.ProfileId != roster.ProfileId))
            throw new InvalidDataException("Prior authority history is invalid.");
        var heads = WorldAuthorityTrust.EffectiveHeads(prior);
        if (heads.Length > 1)
            throw new InvalidDataException("Competing authorities require review before another takeover.");
        var parent = heads.SingleOrDefault();
        if (!VerifiedCandidateLineage(receivedRoot, roster.OwnerPublicKey, version, prior))
            throw new InvalidDataException("The offered save does not extend the verified current authority.");
        var hostingPublicKey = authority.PrepareLocalHostingKey(roster.ProfileId);
        var draft = new WorldAuthorityProposal(2, roster.GroupId, roster.ProfileId,
            parent?.Proposal.Epoch + 1 ?? 1, parent?.RecordHash,
            WorldAuthorityTrust.RosterHash(roster), version.VersionHash,
            hostingPublicKey, candidateAddress, "Quorum", candidateId, candidatePublicKey, "");
        var unsignedBinding = new WorldSuccessorBinding(candidateId, candidatePublicKey,
            hostingPublicKey, "");
        var binding = unsignedBinding with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            WorldAuthorityTrust.BindingBasis(draft, unsignedBinding), HashAlgorithmName.SHA256))
        };
        draft = draft with { SuccessorBinding = binding };
        var proposal = draft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            WorldAuthorityTrust.ProposalBasis(draft), HashAlgorithmName.SHA256))
        };
        var receiptDraft = new SharedWorldReceipt(1, roster.GroupId, roster.ProfileId,
            version.VersionHash, candidateId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
        var receipt = receiptDraft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256))
        };
        var offer = new WorldAuthorityOffer(proposal, roster, version, receipt, ancestors ?? [],
            candidateTlsFingerprint, Convert.ToBase64String(candidateKey.SignData(
                TransportBasis(proposal, candidateTlsFingerprint), HashAlgorithmName.SHA256)));
        return ancestors is null ? offer with
        {
            Ancestors = OfferAncestors(receivedRoot, version, Math.Max(0, 512 * 1024 -
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(offer, Json).Length))
        } : offer;
    }

    internal static WorldAuthorityVote Vote(SharedWorldHostLoss loss, string receivedRoot,
        SharedRosterFloor floor, string pinnedOwnerKey, WorldAuthorityOffer offer,
        Guid voterId, ECDsa voterKey, WorldAuthorityStore store)
    {
        if (!loss.MayPropose && offer.Proposal.Schema != 3)
            throw new InvalidDataException("This PC has not confirmed two minutes without the pinned Host.");
        var local = FriendLink.ReadReceivedLatest(receivedRoot);
        var prior = store.Read(offer.Roster.ProfileId);
        if (!VerifyOffer(offer) || offer.Proposal.Kind == "ResolutionOwnerOverride" ||
            !(offer.Proposal.Schema == 3
                ? TrustedResolutionRoster(offer.Roster, floor, offer.Version, store)
                : local is not null && TrustedRoster(offer.Roster, floor, offer.Version, store)) ||
            offer.Roster.OwnerPublicKey != pinnedOwnerKey ||
            offer.Proposal.Schema != 3 &&
            (!FriendLink.AuthorizedVersionSignerForRecords(pinnedOwnerKey, offer.Version, prior) ||
             !(local!.VersionHash == offer.Version.VersionHash &&
               VerifiedCandidateLineage(receivedRoot, pinnedOwnerKey, local, prior) ||
              local.Number < offer.Version.Number &&
              FriendLink.VerifySharedChain(local, offer.Version,
                  offer.Ancestors.Where(item => item.Number > local.Number &&
                      item.Number < offer.Version.Number).ToArray(), prior))))
            throw new InvalidDataException("The offered save is not a verified descendant of this PC's copy.");
        var voterPublicKey = Convert.ToBase64String(voterKey.ExportSubjectPublicKeyInfo());
        if (!SharedWorldRosterTrust.HasRole(offer.Roster, voterId, voterPublicKey,
                grants => grants.RecoveryVoter))
            throw new InvalidDataException("This PC does not have an active recovery vote.");
        var heads = WorldAuthorityTrust.EffectiveHeads(prior);
        if (offer.Proposal.Schema == 3)
        {
            var draft = new WorldAuthorityRecord(2, offer.Proposal, offer.Roster,
                offer.Version, [], null, "");
            if (!WorldAuthorityTrust.ExactResolutionHeads(draft, heads) ||
                heads.Single(head => head.RecordHash == offer.Proposal.ParentAuthorityHash)
                    .Version.VersionHash != offer.Version.VersionHash)
                throw new InvalidDataException("The resolution does not match this PC's verified authority heads.");
        }
        else if (heads.Length > 1 || heads.Length == 0 &&
                (offer.Proposal.Epoch != 1 || offer.Proposal.ParentAuthorityHash is not null) ||
            heads.Length == 1 &&
                (offer.Proposal.ParentAuthorityHash != heads[0].RecordHash ||
                 offer.Proposal.Epoch != heads[0].Proposal.Epoch + 1))
            throw new InvalidDataException("The proposal does not extend this PC's authority history.");
        return store.SignLocalVote(offer.Roster.ProfileId, offer.Proposal,
            offer.Roster, voterId, voterKey);
    }

    internal static WorldAuthorityRecord ConfirmQuorum(WorldAuthorityOffer offer,
        IReadOnlyList<WorldAuthorityVote> votes, WorldAuthorityStore store,
        string receivedRoot)
    {
        var parent = store.Read(offer.Proposal.ProfileId).SingleOrDefault(record =>
            record.RecordHash == offer.Proposal.ParentAuthorityHash);
        IEnumerable<SharedWorldVersion>? lineage = null;
        if (offer.Proposal.Schema != 3 &&
            (parent is null && offer.Version.Number > 1 ||
             parent is not null && parent.Version.VersionHash != offer.Version.VersionHash))
            lineage = FriendLink.ReadVerifiedReceivedLineage(receivedRoot, offer.Version,
                parent?.Version);
        var draft = new WorldAuthorityRecord(offer.Proposal.Schema == 3 ? 2 : 1,
            offer.Proposal, offer.Roster,
            offer.Version, votes, null, "",
            VersionLineageDigest: lineage is null ? null :
                WorldAuthorityTrust.LineageDigest(lineage));
        var record = draft with
        {
            RecordHash = WorldAuthorityTrust.Hash(
            WorldAuthorityTrust.RecordBasis(draft))
        };
        record = store.PrepareLocalDecision(record);
        if (!WorldAuthorityTrust.Verify(record))
            throw new InvalidDataException("The designated voters did not produce a valid majority.");
        store.Append(record, externalLineage: lineage);
        return record;
    }

    internal static void RecordDecision(WorldAuthorityOffer offer, WorldAuthorityRecord decision,
        WorldAuthorityStore store, string receivedRoot)
    {
        if (!VerifyOffer(offer) || !WorldAuthorityTrust.Verify(decision) ||
            WorldAuthorityTrust.ProposalHash(decision.Proposal) !=
                WorldAuthorityTrust.ProposalHash(offer.Proposal))
            throw new InvalidDataException("The returned decision does not match the reviewed offer.");
        // A voter can be behind the candidate. Preserve signed metadata, never
        // advance its received payload pointer, then verify the decision's full
        // lineage against the existing owner-rooted authority journal.
        foreach (var version in offer.Ancestors) FriendLink.KeepSignedManifest(receivedRoot, version);
        FriendLink.KeepSignedManifest(receivedRoot, offer.Version);
        var parent = store.Read(offer.Proposal.ProfileId).SingleOrDefault(record =>
            record.RecordHash == offer.Proposal.ParentAuthorityHash);
        store.AppendReceived(decision, offer.Proposal.ProfileId, offer.Roster.GroupId,
            offer.Roster.OwnerPublicKey, decision.VersionLineageDigest is null ? null :
                FriendLink.ReadVerifiedReceivedManifestLineage(receivedRoot, decision.Version, parent?.Version));
    }

    private static IReadOnlyList<SharedWorldVersion> OfferAncestors(string root, SharedWorldVersion latest,
        int maximumBytes)
    {
        var versions = new List<SharedWorldVersion>();
        long bytes = 0;
        var hash = latest.ParentHash;
        for (var number = latest.Number - 1; number >= 1 && versions.Count < 64 && hash is not null; number--)
        {
            var version = FriendLink.ReadReceivedManifest(root, number, hash);
            if (version is null || version.GroupId != latest.GroupId || version.ProfileId != latest.ProfileId ||
                version.Game != latest.Game || version.WorldId != latest.WorldId) break;
            bytes += System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(version, Json).LongLength + 1;
            if (bytes > maximumBytes) break;
            versions.Insert(0, version);
            hash = version.ParentHash;
        }
        return versions;
    }

    private static bool TrustedRoster(SharedWorldRoster roster, SharedRosterFloor floor,
        SharedWorldVersion version, WorldAuthorityStore store) =>
        SharedWorldRosterTrust.Verify(roster) && store.TrustsCurrentRoster(roster) &&
        roster.GroupId == floor.GroupId && roster.Epoch >= floor.Epoch &&
        roster.Revision >= floor.Revision &&
        (roster.Epoch != floor.Epoch || roster.Revision != floor.Revision ||
            roster.Signature == floor.Signature) &&
        roster.GroupId == version.GroupId && roster.ProfileId == version.ProfileId &&
        SharedWorldService.VerifySignature(version);

    private static bool VerifiedCandidateLineage(string root, string ownerKey,
        SharedWorldVersion version, IReadOnlyList<WorldAuthorityRecord> records)
    {
        if (!FriendLink.AuthorizedVersionSignerForRecords(ownerKey, version, records)) return false;
        var head = WorldAuthorityTrust.EffectiveHeads(records).SingleOrDefault();
        if (head?.Version.VersionHash == version.VersionHash) return true;
        if (head is null && version.Number == 1) return version.ParentHash is null;
        // Verify the full archived chain before arming an offer or consuming a
        // durable vote. Large histories stay on disk rather than in the offer.
        var expectedSigner = head?.Proposal.CandidatePublicKey ?? ownerKey;
        try
        {
            return FriendLink.ReadVerifiedReceivedLineage(root, version, head?.Version)
                .All(item => item.SigningPublicKey == expectedSigner);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or
            UnauthorizedAccessException or CryptographicException)
        { return false; }
    }

    private static bool TrustedResolutionRoster(SharedWorldRoster roster,
        SharedRosterFloor floor, SharedWorldVersion version, WorldAuthorityStore store) =>
        SharedWorldRosterTrust.Verify(roster) && store.TrustsCurrentRoster(roster) && roster.GroupId == floor.GroupId &&
        roster.Epoch == floor.Epoch && roster.Revision == floor.Revision &&
        roster.Signature == floor.Signature && roster.GroupId == version.GroupId &&
        roster.ProfileId == version.ProfileId && SharedWorldService.VerifySignature(version);

    internal static bool VerifyOffer(WorldAuthorityOffer? offer)
    {
        if (offer?.Proposal is null || offer.Roster is null || offer.Version is null ||
            offer.CandidateReceipt is null || offer.Ancestors is null ||
            !SharedWorldRosterTrust.Verify(offer.Roster) ||
            !WorldAuthorityTrust.VerifyProposal(offer.Proposal, offer.Roster) ||
            offer.Proposal.Kind is not ("Quorum" or "ResolutionQuorum" or
                "ResolutionOwnerOverride") ||
            offer.Proposal.VersionHash != offer.Version.VersionHash ||
            WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal) !=
                offer.CandidateReceipt.DeviceIdKey(offer.Roster) ||
            !SharedWorldRosterTrust.HasRole(offer.Roster, offer.CandidateReceipt.DeviceId,
                WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal),
                grants => grants.EligibleHost && grants.Receive) ||
            offer.CandidateReceipt.GroupId != offer.Roster.GroupId ||
            offer.CandidateReceipt.ProfileId != offer.Roster.ProfileId ||
            offer.CandidateReceipt.VersionHash != offer.Version.VersionHash ||
            offer.CandidateReceipt.RosterEpoch != offer.Roster.Epoch ||
            offer.CandidateReceipt.RosterRevision != offer.Roster.Revision ||
            !SharedWorldReceiptTrust.Verify(offer.CandidateReceipt,
                WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal)) ||
            !VerifyTransportPin(offer) ||
            !HostIdentity.TryEndpoint(offer.Proposal.CandidateAddress, out _) ||
            offer.Ancestors.Count > 64 || !SharedWorldService.VerifySignature(offer.Version))
            return false;
        return offer.Ancestors.All(SharedWorldService.VerifySignature);
    }

    private static bool ValidFingerprint(string? fingerprint) =>
        fingerprint is { Length: 64 } && fingerprint.All(Uri.IsHexDigit);

    private static byte[] TransportBasis(WorldAuthorityProposal proposal, string fingerprint) =>
        Encoding.UTF8.GetBytes("TogetherServer authority candidate TLS pin v1\n" +
            WorldAuthorityTrust.ProposalHash(proposal) + "\n" + fingerprint.ToUpperInvariant());

    private static bool VerifyTransportPin(WorldAuthorityOffer offer)
    {
        try
        {
            if (!ValidFingerprint(offer.CandidateTlsFingerprint)) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(WorldAuthorityTrust.CandidateDevicePublicKey(offer.Proposal)), out _);
            return key.VerifyData(TransportBasis(offer.Proposal, offer.CandidateTlsFingerprint),
                Convert.FromBase64String(offer.CandidateTlsProof), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    private static string? DeviceIdKey(this SharedWorldReceipt receipt, SharedWorldRoster roster) =>
        roster.Members.SingleOrDefault(member => member.DeviceId == receipt.DeviceId)?.PublicKey;
}
