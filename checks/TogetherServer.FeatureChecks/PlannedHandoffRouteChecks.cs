using System.Security.Cryptography;
using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure signed messages only. No files, HTTP transport, listeners, processes,
// installed games, browser or native dialogs are used by this group.
internal static class PlannedHandoffRouteChecks
{
    private const string Endpoint = "https://192.0.2.44:5131";
    private static readonly string Pin = new('A', 64);
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    internal static Task RunAsync()
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var candidate = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var hosting = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid();
        var roster = SignRoster(new(2, Guid.NewGuid(), Guid.NewGuid(), 1, 1, true,
            Public(owner), [new(deviceId, Public(candidate), new(Receive: true, EligibleHost: true), false)], ""), owner);
        var version = Version(roster, owner);
        var record = Record(roster, version, deviceId, candidate, owner);
        Require(WorldAuthorityTrust.Verify(record) && roster.Members.Count == 1 && record.Votes.Count == 0,
            "the two-PC planned fixture required a third member or recovery votes");

        var nonce = Nonce(1);
        var challenge = SharedWorldRouteTrust.SignChallenge(record, nonce, Guid.Empty, owner);
        Require(SharedWorldRouteTrust.VerifyChallenge(challenge, record, Now),
            "the distinct original owner could not observe a two-PC planned move");
        var proof = SharedWorldRouteTrust.Sign(roster.ProfileId, record.RecordHash,
            nonce, Endpoint, Pin, candidate);
        Require(SharedWorldRouteTrust.Verify(proof, record, nonce, Pin),
            "the receiving PC's bound signed route proof did not verify");

        Require(!SharedWorldRouteTrust.VerifyChallenge(
                SharedWorldRouteTrust.SignChallenge(record, nonce, deviceId, candidate), record, Now),
            "the receiving PC observed its own route");
        Reject(() => SharedWorldRouteTrust.SignChallenge(record, nonce, Guid.Empty, candidate),
            "a receiving PC signed an owner-observer challenge");
        Reject(() => SharedWorldRouteTrust.SignChallenge(record, nonce, Guid.Empty, stranger),
            "an unrelated key signed an owner-observer challenge");
        Require(!SharedWorldRouteTrust.VerifyChallenge(SignChallenge(record, nonce, Guid.Empty, candidate), record, Now) &&
            !SharedWorldRouteTrust.VerifyChallenge(SignChallenge(record, nonce, Guid.NewGuid(), stranger), record, Now) &&
            !SharedWorldRouteTrust.VerifyChallenge(SignChallenge(record, nonce, deviceId, owner), record, Now),
            "an unapproved observer key or falsely attributed observer was accepted");

        foreach (var changed in new[]
        {
            challenge with { Schema = 2 },
            challenge with { ProfileId = Guid.NewGuid() },
            challenge with { RecordHash = new string('B', 64) },
            challenge with { Nonce = Nonce(2) },
            challenge with { ObserverDeviceId = deviceId },
            challenge with { ObserverPublicKey = Public(stranger) },
            challenge with { Signature = "invalid" }
        })
            Require(!SharedWorldRouteTrust.VerifyChallenge(changed, record, Now),
                "an altered owner challenge verified");
        Require(!SharedWorldRouteTrust.VerifyChallenge(null, record, Now) &&
            !SharedWorldRouteTrust.VerifyChallenge(challenge, record with { OwnerSignature = null }, Now),
            "missing or unsigned planned authority admitted an owner observer");

        foreach (var changed in new[]
        {
            proof with { Schema = 2 },
            proof with { ProfileId = Guid.NewGuid() },
            proof with { RecordHash = new string('B', 64) },
            proof with { Nonce = Nonce(2) },
            proof with { Endpoint = "https://192.0.2.45:5131" },
            proof with { TlsFingerprint = new string('B', 64) },
            proof with { Signature = "invalid" }
        })
            Require(!SharedWorldRouteTrust.Verify(changed, record, nonce, Pin),
                "an altered route proof verified");
        Require(!SharedWorldRouteTrust.Verify(proof, record, Nonce(2), Pin) &&
            !SharedWorldRouteTrust.Verify(proof, record, nonce, new string('B', 64)) &&
            !SharedWorldRouteTrust.Verify(SharedWorldRouteTrust.Sign(roster.ProfileId,
                record.RecordHash, nonce, Endpoint, Pin, owner), record, nonce, Pin) &&
            !SharedWorldRouteTrust.Verify(null, record, nonce, Pin),
            "another nonce, TLS pin, signing PC or missing proof completed route verification");

        // Use valid signed recovery authority, so refusal cannot be explained by
        // a broken record signature or by a missing recovery majority.
        var recoveryRoster = SignRoster(roster with
        {
            Members = [roster.Members[0] with { Grants = new(Receive: true, EligibleHost: true, RecoveryVoter: true) }]
        }, owner);
        var recovery = Record(recoveryRoster, version, deviceId, candidate, owner, quorum: true);
        Require(WorldAuthorityTrust.Verify(recovery), "the signed quorum fixture was invalid");
        Reject(() => SharedWorldRouteTrust.SignChallenge(recovery, nonce, Guid.Empty, owner),
            "recovery used the planned-only owner-observer signer");
        Require(!SharedWorldRouteTrust.VerifyChallenge(SignChallenge(recovery, nonce, Guid.Empty, owner), recovery, Now) &&
            !SharedWorldRouteTrust.VerifyChallenge(challenge, recovery, Now),
            "planned owner observation bypassed the separate recovery route requirements");
        var noMajority = recovery with { Votes = [] };
        noMajority = noMajority with { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(noMajority)) };
        Require(!WorldAuthorityTrust.Verify(noMajority) &&
            !SharedWorldRouteTrust.VerifyChallenge(SignChallenge(noMajority, nonce, Guid.Empty, owner), noMajority, Now),
            "owner route observation replaced a required recovery majority");

        // The enrolled device identity remains the self-observation boundary
        // when its authority record uses a separate successor hosting key.
        var bound = Record(roster, version, deviceId, candidate, owner, hosting: hosting);
        Require(WorldAuthorityTrust.Verify(bound) &&
            SharedWorldRouteTrust.VerifyChallenge(SharedWorldRouteTrust.SignChallenge(bound, nonce, Guid.Empty, owner), bound, Now) &&
            !SharedWorldRouteTrust.VerifyChallenge(SharedWorldRouteTrust.SignChallenge(bound, nonce, deviceId, candidate), bound, Now) &&
            SharedWorldRouteTrust.Verify(SharedWorldRouteTrust.Sign(roster.ProfileId, bound.RecordHash,
                nonce, Endpoint, Pin, hosting), bound, nonce, Pin),
            "successor hosting-key binding lost the distinct observer or proof-signing identity");

        // Even a cryptographically valid legacy record cannot make one key
        // count as both the original owner and the receiving PC.
        var sameKeyRoster = SignRoster(roster with
        {
            Members = [roster.Members[0] with { PublicKey = Public(owner) }]
        }, owner);
        var sameKeyRecord = Record(sameKeyRoster, version, deviceId, owner, owner);
        Require(WorldAuthorityTrust.Verify(sameKeyRecord) &&
            !SharedWorldRouteTrust.VerifyChallenge(SignChallenge(sameKeyRecord, nonce, Guid.Empty, owner), sameKeyRecord, Now),
            "one signing identity counted as two independent PCs");
        return Task.CompletedTask;
    }

    private static SharedWorldVersion Version(SharedWorldRoster roster, ECDsa owner)
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic final post-Stop save");
        return SharedWorldService.SignVersion(new(4, roster.GroupId, 1, null, roster.ProfileId,
            GameKinds.Fixture, "SyntheticPlannedMove", Now, SharedWorldCaptureKinds.PostStopBackup,
            Guid.NewGuid(), new(2456, false, "Unknown", [], []),
            [new("world.dat", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)))], "", "", ""), owner);
    }

    private static WorldAuthorityRecord Record(SharedWorldRoster roster, SharedWorldVersion version,
        Guid deviceId, ECDsa candidate, ECDsa owner, bool quorum = false, ECDsa? hosting = null)
    {
        var draft = new WorldAuthorityProposal(hosting is null ? 1 : 2, roster.GroupId,
            roster.ProfileId, 1, null, WorldAuthorityTrust.RosterHash(roster), version.VersionHash,
            Public(hosting ?? candidate), Endpoint, quorum ? "Quorum" : "Planned",
            quorum ? deviceId : Guid.Empty, Public(quorum ? candidate : owner), "");
        if (hosting is not null)
        {
            var binding = new WorldSuccessorBinding(deviceId, Public(candidate), Public(hosting), "");
            draft = draft with { SuccessorBinding = binding };
            draft = draft with { SuccessorBinding = binding with
            {
                Signature = Sign(candidate, WorldAuthorityTrust.BindingBasis(draft, binding))
            } };
        }
        var proposal = draft with { Signature = Sign(quorum ? candidate : owner, WorldAuthorityTrust.ProposalBasis(draft)) };
        SharedWorldReceipt? receipt = null;
        WorldAuthorityVote[] votes = [];
        if (quorum)
        {
            var vote = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(proposal), deviceId, Public(candidate), "");
            votes = [vote with { Signature = Sign(candidate, WorldAuthorityTrust.VoteBasis(vote)) }];
        }
        else
        {
            var receiptDraft = new SharedWorldReceipt(1, roster.GroupId, roster.ProfileId,
                version.VersionHash, deviceId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
            receipt = receiptDraft with { Signature = Sign(candidate, SharedWorldReceiptTrust.Basis(receiptDraft)) };
        }
        var record = new WorldAuthorityRecord(1, proposal, roster, version, votes,
            quorum ? null : Sign(owner, WorldAuthorityTrust.OwnerBasis(proposal)), "", receipt);
        return record with { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(record)) };
    }

    private static SharedWorldRouteChallenge SignChallenge(WorldAuthorityRecord record, string nonce,
        Guid observerId, ECDsa key)
    {
        var draft = new SharedWorldRouteChallenge(1, record.Proposal.ProfileId, record.RecordHash,
            nonce, observerId, Public(key), "");
        return draft with { Signature = Sign(key, SharedWorldRouteTrust.ChallengeBasis(draft)) };
    }

    private static SharedWorldRoster SignRoster(SharedWorldRoster roster, ECDsa owner) =>
        roster with { Signature = Sign(owner, SharedWorldRosterTrust.Basis(roster)) };
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static string Sign(ECDsa key, byte[] basis) => Convert.ToBase64String(key.SignData(basis, HashAlgorithmName.SHA256));
    private static string Nonce(byte value) => Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray())
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
}
