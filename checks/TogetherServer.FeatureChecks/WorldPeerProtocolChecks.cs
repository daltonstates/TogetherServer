using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure signing/nonces/JSON only. No HTTP client, listener, process, app, browser,
// production data or native dialog is created by this protocol group.
internal static class WorldPeerProtocolChecks
{
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static void Run()
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static void Invalid(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new Exception(message);
        }
        static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var joiner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var memberId = Guid.NewGuid();
        var joinerId = Guid.NewGuid();
        var fingerprint = Hash("synthetic certificate fingerprint");
        var now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
        var clock = new ManualClock(now);
        var revoked = false;
        var receive = true;
        var trust = new SharedWorldPeerTrust(root, (id, key) =>
        {
            if (id == Guid.Empty && key == Public(owner)) return new(true, true, true, true, true, true);
            if (id == memberId && key == Public(member) && !revoked) return new(true, receive, true, true, false, true);
            if (id == joinerId && key == Public(joiner)) return new(true, false, false, false, false);
            return null;
        });
        var protocol = new WorldPeerProtocol(root, trust, Guid.Empty, owner, fingerprint, clock);
        var challenge = protocol.Issue(memberId, Public(member));
        Require(WorldPeerProtocol.VerifyChallenge(challenge, root, Public(owner), fingerprint,
            memberId, Public(member), trust, clock), "the pinned signed challenge did not verify");
        Require(!WorldPeerProtocol.VerifyChallenge(challenge, root with { GroupId = Guid.NewGuid() }, Public(owner),
            fingerprint, memberId, Public(member), trust, clock) &&
            !WorldPeerProtocol.VerifyChallenge(challenge, root, Public(owner), Hash("other certificate"),
                memberId, Public(member), trust, clock) &&
            !WorldPeerProtocol.VerifyChallenge(challenge, root, Public(joiner), fingerprint,
                memberId, Public(member), trust, clock) &&
            !WorldPeerProtocol.VerifyChallenge(challenge, root, Public(owner), fingerprint,
                joinerId, Public(joiner), trust, clock), "a challenge crossed a root, responder, TLS or requester boundary");

        var metadata = JsonSerializer.SerializeToUtf8Bytes(new WorldPeerMetadataRequest(null,
            (long)int.MaxValue + 9000, (long)int.MaxValue + 9001), SharedWorldPeerTrust.Json);
        var request = WorldPeerProtocol.Sign(challenge, WorldPeerOperations.Metadata, member, payload: metadata);
        Require(!protocol.Verify(request with { Signature = Convert.ToBase64String(new byte[64]) }, metadata,
            WorldPeerOperations.Metadata).Ok, "a forged request verified");
        Require(!protocol.Verify(request, [1, 2, 3], WorldPeerOperations.Metadata).Ok,
            "a changed metadata payload retained its signature");
        Require(!protocol.Verify(request, metadata, WorldPeerOperations.Catalog).Ok,
            "a request was accepted at a different fixed operation");
        Require(protocol.Verify(request, metadata, WorldPeerOperations.Metadata).Ok &&
            !protocol.Verify(request, metadata, WorldPeerOperations.Metadata).Ok,
            "a forged attempt consumed the valid nonce or a valid nonce could be replayed");
        var catchup = WorldPeerProtocol.DecodeMetadata(metadata, root);
        Require(catchup.AuthorityOffset > int.MaxValue && catchup.KnownRosterRevision > int.MaxValue,
            "long authority/roster catchup history was truncated to 32 bits");

        var old = protocol.Issue(memberId, Public(member));
        var current = protocol.Issue(memberId, Public(member));
        Require(!protocol.Verify(WorldPeerProtocol.Sign(old, WorldPeerOperations.Catalog, member), [],
            WorldPeerOperations.Catalog).Ok &&
            protocol.Verify(WorldPeerProtocol.Sign(current, WorldPeerOperations.Catalog, member), [],
                WorldPeerOperations.Catalog).Ok,
            "an overridden nonce verified or burned its newer scoped challenge");
        var memberChallenge = protocol.Issue(memberId, Public(member));
        var joinerChallenge = protocol.Issue(joinerId, Public(joiner));
        Require(protocol.Verify(WorldPeerProtocol.Sign(memberChallenge, WorldPeerOperations.Catalog, member), [],
            WorldPeerOperations.Catalog).Ok &&
            protocol.Verify(WorldPeerProtocol.Sign(joinerChallenge, WorldPeerOperations.Metadata, joiner), [],
                WorldPeerOperations.Metadata).Ok,
            "issuing a challenge to another PC replaced this PC's nonce or denied metadata-only membership");

        var denied = protocol.Issue(joinerId, Public(joiner));
        Require(!protocol.Verify(WorldPeerProtocol.Sign(denied, WorldPeerOperations.Chunk, joiner,
            versionHash: Hash("version"), fileIndex: 0, chunkOffset: 0), [], WorldPeerOperations.Chunk).Ok,
            "metadata-only membership authorized world file bytes");
        var permissionChallenge = protocol.Issue(memberId, Public(member));
        var proofRequest = WorldPeerProtocol.Sign(permissionChallenge, WorldPeerOperations.Chunk, member,
            versionHash: Hash("version"), fileIndex: 0, chunkOffset: 0);
        receive = false;
        Require(!protocol.Verify(proofRequest, [], WorldPeerOperations.Chunk).Ok,
            "a saved old permission snapshot bypassed current Receive");
        receive = true;
        Require(protocol.Verify(proofRequest, [], WorldPeerOperations.Chunk).Ok,
            "a denied request consumed the nonce");
        var longProofChallenge = protocol.Issue(joinerId, Public(joiner));
        Require(protocol.Verify(WorldPeerProtocol.Sign(longProofChallenge, WorldPeerOperations.Version, joiner,
            versionHash: Hash("authority record"), chunkOffset: (long)int.MaxValue + 99), [], WorldPeerOperations.Version).Ok,
            "metadata-only membership could not verify a long signed authority proof");
        var enrollmentChallenge = protocol.Issue(joinerId, Public(joiner));
        var enrollmentPayload = JsonSerializer.SerializeToUtf8Bytes(new { SessionId = Guid.NewGuid() }, SharedWorldPeerTrust.Json);
        var enrollment = WorldPeerProtocol.Sign(enrollmentChallenge, WorldPeerOperations.Enrollment, joiner, payload: enrollmentPayload);
        Require(protocol.Verify(enrollment, enrollmentPayload, WorldPeerOperations.Enrollment).Ok &&
            WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Enrollment) == 8 * 1024,
            "a metadata-only member could not request bounded per-session status enrollment");
        Invalid(() => WorldPeerProtocol.Sign(enrollmentChallenge, WorldPeerOperations.Enrollment, joiner,
            payload: new byte[8 * 1024 + 1]), "session enrollment accepted an oversized payload");
        var revokedChallenge = protocol.Issue(memberId, Public(member));
        revoked = true;
        Require(!protocol.Verify(WorldPeerProtocol.Sign(revokedChallenge, WorldPeerOperations.Metadata, member), [],
            WorldPeerOperations.Metadata).Ok, "revocation after challenge issue was ignored");
        revoked = false;

        var expiry = protocol.Issue(memberId, Public(member));
        var expiryRequest = WorldPeerProtocol.Sign(expiry, WorldPeerOperations.Catalog, member);
        clock.Now = expiry.ExpiresUtc;
        Require(!protocol.Verify(expiryRequest, [], WorldPeerOperations.Catalog).Ok,
            "a challenge was valid at its exact expiry boundary");
        clock.Now = now;
        var restartChallenge = protocol.Issue(memberId, Public(member));
        var restarted = new WorldPeerProtocol(root, trust, Guid.Empty, owner, fingerprint, clock);
        Require(!restarted.Verify(WorldPeerProtocol.Sign(restartChallenge, WorldPeerOperations.Catalog, member), [],
            WorldPeerOperations.Catalog).Ok, "a challenge survived a protocol responder restart");

        var fresh = restarted.Issue(memberId, Public(member));
        var valid = WorldPeerProtocol.Sign(fresh, WorldPeerOperations.Catalog, member);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(valid, SharedWorldPeerTrust.Json);
        Require(WorldPeerProtocol.DecodeRequest(serialized).Challenge.ChallengeHash == fresh.ChallengeHash &&
            WorldPeerProtocol.DecodeChallenge(JsonSerializer.SerializeToUtf8Bytes(fresh, SharedWorldPeerTrust.Json)) == fresh,
            "valid signed protocol JSON did not roundtrip");
        var json = Encoding.UTF8.GetString(serialized);
        Invalid(() => WorldPeerProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json.Replace(
            "\"offset\":0", "\"offset\":0,\"path\":\"C:/private/world\"", StringComparison.Ordinal))),
            "an arbitrary request field was accepted");
        Invalid(() => WorldPeerProtocol.DecodeRequest(Encoding.UTF8.GetBytes(json.Replace(
            "\"offset\":0", "\"offset\":0,\"Offset\":0", StringComparison.Ordinal))),
            "case-insensitive duplicate signed request fields were accepted");
        Invalid(() => WorldPeerProtocol.DecodeRequest(new byte[WorldPeerProtocol.MaximumRequestBytes + 1]),
            "an oversized signed request decoded");
        Invalid(() => WorldPeerProtocol.DecodeMetadata("{\"authorityOffset\":-1}"u8, root),
            "a negative authority history offset was accepted");
        Invalid(() => WorldPeerProtocol.Sign(fresh, "Execute", member), "an arbitrary peer operation was accepted");
        Invalid(() => WorldPeerProtocol.Sign(fresh, WorldPeerOperations.Version, member,
            versionHash: "../../world"), "an arbitrary version selector was accepted");
        Invalid(() => WorldPeerProtocol.Sign(fresh, WorldPeerOperations.Chunk, member,
            versionHash: Hash("version"), fileIndex: 0, chunkOffset: 1), "an unaligned chunk offset was accepted");
        Invalid(() => WorldPeerProtocol.Sign(fresh, WorldPeerOperations.Chunk, member,
            versionHash: Hash("version"), fileIndex: SharedWorldService.MaximumFiles, chunkOffset: 0),
            "an out-of-range manifest file selector was accepted");
        Invalid(() => WorldPeerProtocol.Sign(fresh, WorldPeerOperations.Catalog, member, payload: [1]),
            "a catalog request accepted an arbitrary payload");
        Require(WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Metadata) == 4 * 1024 * 1024 &&
            WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Catalog) == 2 * 1024 * 1024 &&
            WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Version) == 256 * 1024 &&
            WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Chunk) == 512 * 1024,
            "the fixed peer response byte caps drifted");

        var capTrust = new SharedWorldPeerTrust(root, (_, key) => key == Public(owner) || key == Public(member)
            ? new(true, true, false, false, false) : null);
        var capped = new WorldPeerProtocol(root, capTrust, Guid.Empty, owner, fingerprint, clock);
        var ids = Enumerable.Range(0, WorldPeerProtocol.MaximumPending).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) _ = capped.Issue(id, Public(member));
        Invalid(() => capped.Issue(Guid.NewGuid(), Public(member)), "pending nonces grew without the 256-entry bound");
        _ = capped.Issue(ids[0], Public(member));
        clock.Now = now.Add(WorldPeerProtocol.ChallengeLifetime);
        _ = capped.Issue(Guid.NewGuid(), Public(member));
    }
}
