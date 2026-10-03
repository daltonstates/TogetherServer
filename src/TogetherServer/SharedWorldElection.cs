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
        if (!TrustedRoster(roster, floor, version) ||
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
        var heads = prior.Where(record => !prior.Any(child =>
            child.Proposal.ParentAuthorityHash == record.RecordHash)).ToArray();
        if (heads.Length > 1)
            throw new InvalidDataException("Competing authorities require review before another takeover.");
        var parent = heads.SingleOrDefault();
        if (parent is not null && parent.Version.VersionHash != version.VersionHash)
            throw new InvalidDataException("The existing authority and this PC have different save heads.");
        var hostingPublicKey = authority.PrepareLocalHostingKey(roster.ProfileId);
        var draft = new WorldAuthorityProposal(2, roster.GroupId, roster.ProfileId,
            parent?.Proposal.Epoch + 1 ?? 1, parent?.RecordHash,
            WorldAuthorityTrust.RosterHash(roster), version.VersionHash,
            hostingPublicKey, candidateAddress, "Quorum", candidateId, candidatePublicKey, "");
        var unsignedBinding = new WorldSuccessorBinding(candidateId, candidatePublicKey,
            hostingPublicKey, "");
        var binding = unsignedBinding with { Signature = Convert.ToBase64String(candidateKey.SignData(
            WorldAuthorityTrust.BindingBasis(draft, unsignedBinding), HashAlgorithmName.SHA256)) };
        draft = draft with { SuccessorBinding = binding };
        var proposal = draft with { Signature = Convert.ToBase64String(candidateKey.SignData(
            WorldAuthorityTrust.ProposalBasis(draft), HashAlgorithmName.SHA256)) };
        var receiptDraft = new SharedWorldReceipt(1, roster.GroupId, roster.ProfileId,
            version.VersionHash, candidateId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
        var receipt = receiptDraft with { Signature = Convert.ToBase64String(candidateKey.SignData(
            SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256)) };
        return new(proposal, roster, version, receipt, ancestors ?? [],
            candidateTlsFingerprint, Convert.ToBase64String(candidateKey.SignData(
                TransportBasis(proposal, candidateTlsFingerprint), HashAlgorithmName.SHA256)));
    }

    internal static WorldAuthorityVote Vote(SharedWorldHostLoss loss, string receivedRoot,
        SharedRosterFloor floor, string pinnedOwnerKey, WorldAuthorityOffer offer,
        Guid voterId, ECDsa voterKey, WorldAuthorityStore store)
    {
        if (!loss.MayPropose)
            throw new InvalidDataException("This PC has not confirmed two minutes without the pinned Host.");
        var local = FriendLink.ReadReceivedLatest(receivedRoot) ??
            throw new InvalidDataException("This PC has no hash-verified post-Stop file copy history to compare.");
        if (!VerifyOffer(offer) || !TrustedRoster(offer.Roster, floor, offer.Version) ||
            offer.Roster.OwnerPublicKey != pinnedOwnerKey ||
            !(local.VersionHash == offer.Version.VersionHash && offer.Ancestors.Count == 0 ||
              local.Number < offer.Version.Number &&
              FriendLink.VerifySharedChain(local, offer.Version, offer.Ancestors)))
            throw new InvalidDataException("The offered save is not a verified descendant of this PC's copy.");
        var voterPublicKey = Convert.ToBase64String(voterKey.ExportSubjectPublicKeyInfo());
        if (!SharedWorldRosterTrust.HasRole(offer.Roster, voterId, voterPublicKey,
                grants => grants.RecoveryVoter))
            throw new InvalidDataException("This PC does not have an active recovery vote.");
        var prior = store.Read(offer.Roster.ProfileId);
        var heads = prior.Where(record => !prior.Any(child =>
            child.Proposal.ParentAuthorityHash == record.RecordHash)).ToArray();
        if (heads.Length > 1 || heads.Length == 0 &&
                (offer.Proposal.Epoch != 1 || offer.Proposal.ParentAuthorityHash is not null) ||
            heads.Length == 1 &&
                (offer.Proposal.ParentAuthorityHash != heads[0].RecordHash ||
                 offer.Proposal.Epoch != heads[0].Proposal.Epoch + 1))
            throw new InvalidDataException("The proposal does not extend this PC's authority history.");
        return store.SignLocalVote(offer.Roster.ProfileId, offer.Proposal,
            offer.Roster, voterId, voterKey);
    }

    internal static WorldAuthorityRecord ConfirmQuorum(WorldAuthorityOffer offer,
        IReadOnlyList<WorldAuthorityVote> votes, WorldAuthorityStore store)
    {
        var draft = new WorldAuthorityRecord(1, offer.Proposal, offer.Roster,
            offer.Version, votes, null, "");
        var record = draft with { RecordHash = WorldAuthorityTrust.Hash(
            WorldAuthorityTrust.RecordBasis(draft)) };
        if (!WorldAuthorityTrust.Verify(record))
            throw new InvalidDataException("The designated voters did not produce a valid majority.");
        store.Append(record);
        return record;
    }

    private static bool TrustedRoster(SharedWorldRoster roster, SharedRosterFloor floor,
        SharedWorldVersion version) =>
        SharedWorldRosterTrust.Verify(roster) &&
        roster.GroupId == floor.GroupId && roster.Epoch >= floor.Epoch &&
        roster.Revision >= floor.Revision &&
        (roster.Epoch != floor.Epoch || roster.Revision != floor.Revision ||
            roster.Signature == floor.Signature) &&
        roster.GroupId == version.GroupId && roster.ProfileId == version.ProfileId &&
        roster.OwnerPublicKey == version.SigningPublicKey &&
        SharedWorldService.VerifySignature(version);

    internal static bool VerifyOffer(WorldAuthorityOffer? offer)
    {
        if (offer?.Proposal is null || offer.Roster is null || offer.Version is null ||
            offer.CandidateReceipt is null || offer.Ancestors is null ||
            !WorldAuthorityTrust.VerifyProposal(offer.Proposal, offer.Roster) ||
            offer.Proposal.Kind != "Quorum" || offer.Proposal.VersionHash != offer.Version.VersionHash ||
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
