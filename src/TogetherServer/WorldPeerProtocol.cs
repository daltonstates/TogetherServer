using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record WorldPeerChallenge(int Schema, SharedWorldPeerRoot Root,
    Guid DeviceId, string PublicKey, Guid ResponderDeviceId, string ResponderPublicKey,
    string CertificateSha256, string Nonce, DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc, string ChallengeHash, string Signature);

// ChunkOffset is a byte offset for Chunk, and an optional positive signed
// manifest/proof version number for Version. It is long in both cases; lifetime
// world history is not restricted to a 32-bit number or a wall-clock window.
public sealed record WorldPeerRequest(int Schema, WorldPeerChallenge Challenge,
    string Operation, int Offset, string? VersionHash, int? FileIndex,
    long? ChunkOffset, string PayloadSha256, string Signature);

public sealed record WorldPeerMetadataRequest(SharedWorldPeerPage? Page = null,
    long AuthorityOffset = 0, long KnownRosterRevision = 0,
    IReadOnlyList<WorldPeerDefaultSelection>? DefaultSelections = null);

internal sealed record WorldPeerProofResult(bool Ok, string Code, string Message,
    SharedWorldPeerAccess? Access = null);

internal static class WorldPeerOperations
{
    internal const string Metadata = "Metadata";
    internal const string Catalog = "Catalog";
    internal const string Version = "Version";
    internal const string Chunk = "Chunk";
    internal const string Receipt = "Receipt";
    internal const string Enrollment = "Enrollment";
}

// A fixed signed request protocol over an already selected/pinned direct peer
// origin. No paired Host bearer token is shared with another PC. Each successful
// proof consumes one exact in-memory nonce. Restart invalidates all old proofs.
internal sealed class WorldPeerProtocol
{
    internal const int MaximumPending = 256;
    internal const int MaximumChallengeBytes = 8192;
    internal const int MaximumRequestBytes = 16 * 1024;
    internal static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private readonly SharedWorldPeerRoot root;
    private readonly SharedWorldPeerTrust trust;
    private readonly Guid responderDeviceId;
    private readonly ECDsa responderKey;
    private readonly string responderPublicKey;
    private readonly string fingerprint;
    private readonly TimeProvider clock;
    private readonly object sync = new();
    private readonly Dictionary<(Guid DeviceId, string PublicKey), WorldPeerChallenge> pending = [];

    internal WorldPeerProtocol(SharedWorldPeerRoot root, SharedWorldPeerTrust trust,
        Guid responderDeviceId, ECDsa responderKey, string tlsFingerprint, TimeProvider? clock = null)
    {
        if (!SharedWorldPeerTrust.ValidRoot(root) || !SharedWorldPeerTrust.ValidHash(tlsFingerprint))
            throw new InvalidDataException("The peer protocol identity is invalid.");
        this.root = root;
        this.trust = trust;
        this.responderDeviceId = responderDeviceId;
        this.responderKey = responderKey;
        responderPublicKey = Convert.ToBase64String(responderKey.ExportSubjectPublicKeyInfo());
        if (!SharedWorldRosterTrust.ValidKey(responderPublicKey))
            throw new InvalidDataException("The peer protocol signing key is invalid.");
        fingerprint = tlsFingerprint;
        this.clock = clock ?? TimeProvider.System;
    }

    internal WorldPeerChallenge Issue(Guid requesterId, string requesterPublicKey)
    {
        lock (sync)
        {
            if (trust.Access(requesterId, requesterPublicKey) is null ||
                trust.Access(responderDeviceId, responderPublicKey) is null)
                throw new InvalidDataException("Peer metadata membership is required.");
            var now = clock.GetUtcNow();
            foreach (var expired in pending.Where(item => item.Value.ExpiresUtc <= now).Select(item => item.Key).ToArray())
                pending.Remove(expired);
            var scope = (requesterId, requesterPublicKey);
            if (!pending.ContainsKey(scope) && pending.Count >= MaximumPending)
                throw new InvalidDataException("Too many peer challenges are pending.");
            var draft = new WorldPeerChallenge(1, root, requesterId, requesterPublicKey,
                responderDeviceId, responderPublicKey, fingerprint,
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), now,
                now.Add(ChallengeLifetime), "", "");
            var basis = ChallengeBasis(draft);
            var challenge = draft with
            {
                ChallengeHash = Convert.ToHexString(SHA256.HashData(basis)),
                Signature = Convert.ToBase64String(responderKey.SignData(basis, HashAlgorithmName.SHA256))
            };
            pending[scope] = challenge;
            return challenge;
        }
    }

    internal static byte[] ChallengeBasis(WorldPeerChallenge value) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer direct world peer challenge v1",
            value.Schema,
            value.Root,
            value.DeviceId,
            value.PublicKey,
            value.ResponderDeviceId,
            value.ResponderPublicKey,
            value.CertificateSha256,
            value.Nonce,
            value.IssuedUtc,
            value.ExpiresUtc
        }, SharedWorldPeerTrust.Json);

    private static bool VerifyChallengeSignature(WorldPeerChallenge? value)
    {
        try
        {
            if (value is null || value.Schema != 1 || !SharedWorldPeerTrust.ValidRoot(value.Root) ||
                !SharedWorldRosterTrust.ValidKey(value.PublicKey) ||
                !SharedWorldRosterTrust.ValidKey(value.ResponderPublicKey) ||
                !SharedWorldPeerTrust.ValidHash(value.CertificateSha256) ||
                !SharedWorldPeerTrust.ValidHash(value.ChallengeHash) ||
                value.Nonce is not { Length: 44 } || Convert.FromBase64String(value.Nonce).Length != 32 ||
                value.IssuedUtc == default || value.IssuedUtc.Offset != TimeSpan.Zero ||
                value.ExpiresUtc.Offset != TimeSpan.Zero || value.ExpiresUtc <= value.IssuedUtc ||
                value.ExpiresUtc - value.IssuedUtc != ChallengeLifetime ||
                value.Signature is not { Length: >= 80 and <= 192 } ||
                JsonSerializer.SerializeToUtf8Bytes(value, SharedWorldPeerTrust.Json).Length > MaximumChallengeBytes)
                return false;
            var basis = ChallengeBasis(value);
            return value.ChallengeHash == Convert.ToHexString(SHA256.HashData(basis)) &&
                VerifySignature(value.ResponderPublicKey, basis, value.Signature);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or
                                   NullReferenceException or JsonException or OverflowException)
        { return false; }
    }

    internal static bool VerifyChallenge(WorldPeerChallenge? challenge, SharedWorldPeerRoot root,
        string expectedResponderKey, string expectedFingerprint, Guid requesterId,
        string requesterPublicKey, SharedWorldPeerTrust trust, TimeProvider? clock = null)
    {
        if (!VerifyChallengeSignature(challenge) || challenge!.Root != root ||
            challenge.ResponderPublicKey != expectedResponderKey ||
            challenge.CertificateSha256 != expectedFingerprint ||
            challenge.DeviceId != requesterId || challenge.PublicKey != requesterPublicKey ||
            trust.Access(challenge.ResponderDeviceId, challenge.ResponderPublicKey) is null ||
            trust.Access(requesterId, requesterPublicKey) is null) return false;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return challenge.IssuedUtc <= now.AddMinutes(1) && challenge.ExpiresUtc > now;
    }

    internal static byte[] RequestBasis(WorldPeerRequest value) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer direct world peer request v1",
            value.Schema,
            value.Challenge.ChallengeHash,
            value.Operation,
            value.Offset,
            value.VersionHash,
            value.FileIndex,
            value.ChunkOffset,
            value.PayloadSha256
        }, SharedWorldPeerTrust.Json);

    internal static WorldPeerRequest Sign(WorldPeerChallenge challenge, string operation,
        ECDsa requesterKey, int offset = 0, string? versionHash = null,
        int? fileIndex = null, long? chunkOffset = null, ReadOnlySpan<byte> payload = default)
    {
        if (Convert.ToBase64String(requesterKey.ExportSubjectPublicKeyInfo()) != challenge.PublicKey)
            throw new InvalidDataException("The peer request signing identity changed.");
        var draft = new WorldPeerRequest(1, challenge, operation, offset, versionHash,
            fileIndex, chunkOffset, Convert.ToHexString(SHA256.HashData(payload)), "");
        if (!ValidRequestShape(draft, requireSignature: false) || !ValidPayload(draft, payload.Length))
            throw new InvalidDataException("The fixed peer request is invalid.");
        return draft with { Signature = Convert.ToBase64String(requesterKey.SignData(RequestBasis(draft), HashAlgorithmName.SHA256)) };
    }

    private static bool ValidRequestShape(WorldPeerRequest? value, bool requireSignature = true)
    {
        if (value is null || value.Schema != 1 || !VerifyChallengeSignature(value.Challenge) ||
            !SharedWorldPeerTrust.ValidHash(value.PayloadSha256) || value.Offset < 0 ||
            requireSignature && value.Signature is not { Length: >= 80 and <= 192 }) return false;
        return value.Operation switch
        {
            WorldPeerOperations.Metadata => value.Offset <= SharedWorldPeerStore.MaximumAnnouncements &&
                value.VersionHash is null && value.FileIndex is null && value.ChunkOffset is null,
            WorldPeerOperations.Catalog => value.VersionHash is null && value.FileIndex is null && value.ChunkOffset is null,
            WorldPeerOperations.Version => value.Offset == 0 && SharedWorldPeerTrust.ValidHash(value.VersionHash) &&
                value.FileIndex is null && (value.ChunkOffset is null or >= 1),
            WorldPeerOperations.Chunk => value.Offset == 0 && SharedWorldPeerTrust.ValidHash(value.VersionHash) &&
                value.FileIndex is >= 0 and < SharedWorldService.MaximumFiles &&
                value.ChunkOffset is >= 0 and <= SharedWorldService.MaximumSharedWorldBytes &&
                value.ChunkOffset % SharedWorldService.ChunkBytes == 0,
            WorldPeerOperations.Receipt => value.Offset == 0 && SharedWorldPeerTrust.ValidHash(value.VersionHash) &&
                value.FileIndex is null && value.ChunkOffset is null,
            WorldPeerOperations.Enrollment => value.Offset == 0 && value.VersionHash is null &&
                value.FileIndex is null && value.ChunkOffset is null,
            _ => false
        };
    }

    private static bool ValidPayload(WorldPeerRequest request, int length) => request.Operation switch
    {
        WorldPeerOperations.Metadata => length is >= 0 and <= SharedWorldPeerTrust.MaximumPageBytes,
        WorldPeerOperations.Receipt => length is > 0 and <= 64 * 1024,
        WorldPeerOperations.Enrollment => length is > 0 and <= 8 * 1024,
        _ => length == 0
    };

    internal WorldPeerProofResult Verify(WorldPeerRequest request, ReadOnlySpan<byte> payload, string expectedOperation)
    {
        lock (sync)
        {
            if (!ValidRequestShape(request) || request.Operation != expectedOperation || !ValidPayload(request, payload.Length) ||
                !VerifyChallenge(request.Challenge, root, responderPublicKey, fingerprint,
                    request.Challenge.DeviceId, request.Challenge.PublicKey, trust, clock) ||
                request.Challenge.ResponderDeviceId != responderDeviceId ||
                request.PayloadSha256 != Convert.ToHexString(SHA256.HashData(payload)) ||
                !VerifySignature(request.Challenge.PublicKey, RequestBasis(request), request.Signature))
                return new(false, "PeerProofRejected", "The signed peer request did not verify.");
            var challenge = request.Challenge;
            var scope = (challenge.DeviceId, challenge.PublicKey);
            if (!pending.TryGetValue(scope, out var issued) || issued.ChallengeHash != challenge.ChallengeHash ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(issued.Nonce), Encoding.ASCII.GetBytes(challenge.Nonce)))
                return new(false, "PeerChallengeChanged", "The peer challenge was replaced or already used.");
            var access = trust.Access(challenge.DeviceId, challenge.PublicKey);
            if (access is null || request.Operation is WorldPeerOperations.Chunk or WorldPeerOperations.Receipt &&
                !access.Receive)
                return new(false, "PeerPermissionDenied", "Save access is not enabled for this peer.");
            // All signature, current permission, exact nonce and payload checks
            // finish before consumption. A forged request cannot burn a nonce.
            pending.Remove(scope);
            return new(true, "PeerProofAccepted", "The signed peer request was accepted.", access);
        }
    }

    internal static int ResponseLimit(string operation) => operation switch
    {
        WorldPeerOperations.Metadata => 4 * 1024 * 1024,
        WorldPeerOperations.Catalog => 2 * 1024 * 1024,
        WorldPeerOperations.Version => 256 * 1024,
        WorldPeerOperations.Chunk => 512 * 1024,
        WorldPeerOperations.Receipt => 64 * 1024,
        WorldPeerOperations.Enrollment => 8 * 1024,
        _ => throw new InvalidDataException("The peer operation is invalid.")
    };

    private static bool VerifySignature(string publicKey, byte[] basis, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(basis, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    internal static WorldPeerRequest DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        var request = Decode<WorldPeerRequest>(bytes, MaximumRequestBytes);
        if (!ValidRequestShape(request)) throw new InvalidDataException("The fixed peer request is invalid.");
        return request;
    }

    internal static WorldPeerChallenge DecodeChallenge(ReadOnlySpan<byte> bytes)
    {
        var challenge = Decode<WorldPeerChallenge>(bytes, MaximumChallengeBytes);
        if (!VerifyChallengeSignature(challenge)) throw new InvalidDataException("The peer challenge is invalid.");
        return challenge;
    }

    internal static WorldPeerMetadataRequest DecodeMetadata(ReadOnlySpan<byte> bytes, SharedWorldPeerRoot root)
    {
        if (bytes.Length == 0) return new();
        var metadata = Decode<WorldPeerMetadataRequest>(bytes, SharedWorldPeerTrust.MaximumPageBytes);
        if (metadata.AuthorityOffset < 0 || metadata.KnownRosterRevision < 0 || metadata.DefaultSelections is { Count: > 64 } ||
            metadata.Page is { } page &&
            SharedWorldPeerTrust.DecodePage(JsonSerializer.SerializeToUtf8Bytes(page, SharedWorldPeerTrust.Json)).Root != root)
            throw new InvalidDataException("The peer metadata catchup request is invalid.");
        return metadata;
    }

    internal static T DecodeEnvelope<T>(ReadOnlySpan<byte> bytes, int maximumBytes) where T : class =>
        maximumBytes is > 0 and <= 4 * 1024 * 1024 ? Decode<T>(bytes, maximumBytes) :
            throw new InvalidDataException("The peer envelope limit is invalid.");

    private static T Decode<T>(ReadOnlySpan<byte> bytes, int maximumBytes) where T : class
    {
        if (bytes.Length > maximumBytes) throw new InvalidDataException("The peer request is oversized.");
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            { MaxDepth = SharedWorldPeerTrust.MaximumJsonDepth });
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, SharedWorldPeerTrust.Json) ??
                throw new InvalidDataException("The peer request is invalid.");
        }
        catch (JsonException ex) { throw new InvalidDataException("The peer request is invalid.", ex); }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The peer request has duplicate fields.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
