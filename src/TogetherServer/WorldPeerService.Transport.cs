using System.Globalization;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record WorldPeerChallengeRequest(Guid DeviceId, string PublicKey,
    IReadOnlyList<SharedWorldRoster>? RosterRevisions = null, string? Nonce = null,
    long KnownRosterRevision = 0, SharedWorldPeerRoot? Root = null,
    string? CertificateSha256 = null, string? Signature = null, DateTimeOffset? IssuedUtc = null);
public sealed record WorldPeerChallengeResponse(int Schema, SharedWorldPeerRoot Root,
    Guid DeviceId, string PublicKey, Guid ResponderDeviceId, string ResponderPublicKey,
    string CertificateSha256, string RequestSha256, long KnownRosterRevision,
    string Code, WorldPeerChallenge? Challenge, string Signature,
    IReadOnlyList<SharedWorldRoster>? RosterRevisions = null);
public sealed record WorldPeerExchangeRequest(WorldPeerRequest Request, byte[] Payload);
public sealed record WorldPeerAdvertisement(IReadOnlyList<SharedWorldPeerRoute> Routes,
    IReadOnlyList<SharedWorldPeerSession> Sessions, IReadOnlyList<SharedWorldPeerBranch> Branches,
    IReadOnlyList<string> AvailableVersionHashes);
public sealed record WorldPeerMetadataResponse(SharedWorldPeerPage Page,
    IReadOnlyList<SharedWorldRoster> RosterRevisions,
    IReadOnlyList<WorldAuthorityRecord> AuthorityRecords, long AuthorityTotal,
    long RosterTotal, int AcceptedPeerOffset = 0,
    IReadOnlyList<WorldPeerDefaultSelection>? DefaultSelections = null);

internal sealed partial class WorldPeerService : IDisposable
{
    internal const int MaximumRosterHandshakeBytes = 1024 * 1024 + 16 * 1024;
    private const int MaximumRosterRequestNonces = 16384;
    private sealed record ProtocolState(string PublicKey, string Fingerprint,
        ECDsa Key, WorldPeerProtocol Protocol);
    private sealed record PeerCursor(int Schema, string GroupKey, string RouteKey,
        int SentOffset = 0, int ReceivedOffset = 0, long AuthorityOffset = 0,
        long KnownRemoteRosterRevision = 0);
    private readonly Dictionary<string, ProtocolState> peerProtocols = new(StringComparer.Ordinal);
    private readonly Dictionary<(string GroupKey, string PublicKey, string Nonce), DateTimeOffset> rosterRequestNonces = [];
    private readonly Dictionary<string, int> routePositions = new(StringComparer.Ordinal);
    private int groupPosition;
    private bool peerDisposed;
    private Task? syncFlight;
    private Task? membershipRefresh;

    // Direct, fixed-origin TLS only: no redirects, system HTTP proxy, bearer
    // credential sharing, DNS search or arbitrary caller-selected path.
    internal static HttpClient MakePinnedClient(string endpoint, IEnumerable<string> fingerprints)
    {
        var pins = fingerprints.Where(SharedWorldPeerTrust.ValidHash).Select(Convert.FromHexString).ToArray();
        if (pins.Length == 0 || !SharedWorldPeerTrust.ValidRoute(new(endpoint, Convert.ToHexString(pins[0]))))
            throw new AuthenticationException("A pinned direct peer origin is required.");
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                DateTimeOffset.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
                DateTimeOffset.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
                pins.Any(pin => CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), pin))
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(6)
        };
    }

    private SharedWorldPeerTrust Trust(Group group) => new(group.Root, (id, key) =>
    {
        var current = Find(group.Root.GroupKey);
        return current is null ? null : Access(current, id, key);
    });

    // Called while stateSync is held whenever the retained private key is used.
    private WorldPeerProtocol ProtocolFor(Group group, string? activeTlsFingerprint)
    {
        ObjectDisposedException.ThrowIf(peerDisposed, this);
        if (KnownRosterRevision(group) < group.Roster.Revision)
            throw new InvalidDataException("The peer signing identity needs its complete roster history.");
        using var identity = new HostIdentity(data).Load();
        var pin = activeTlsFingerprint ?? (identity is null ? null : HostIdentity.Fingerprint(identity));
        if (!SharedWorldPeerTrust.ValidHash(pin)) throw new InvalidDataException("The peer TLS identity is unavailable.");
        using var candidate = LocalKey(group) ?? throw new InvalidDataException("The peer signing identity is unavailable.");
        var publicKey = Convert.ToBase64String(candidate.ExportSubjectPublicKeyInfo());
        var groupKey = group.Root.GroupKey;
        if (peerProtocols.TryGetValue(groupKey, out var existing) &&
            existing.PublicKey == publicKey && existing.Fingerprint == pin) return existing.Protocol;
        var retained = ECDsa.Create();
        retained.ImportPkcs8PrivateKey(candidate.ExportPkcs8PrivateKey(), out _);
        var protocol = new WorldPeerProtocol(group.Root, Trust(group), group.DeviceId, retained, pin!);
        if (existing is not null) existing.Key.Dispose();
        peerProtocols[groupKey] = new(publicKey, pin!, retained, protocol);
        return protocol;
    }

    internal WorldPeerChallenge Challenge(string groupKey, Guid deviceId, string publicKey,
        string? activeTlsFingerprint = null)
    {
        lock (stateSync)
        {
            var group = Find(groupKey);
            if (group is null || !group.SharingEnabled || Access(group, deviceId, publicKey) is null)
                throw new InvalidDataException("The world peer route is not enabled for this member.");
            return ProtocolFor(group, activeTlsFingerprint).Issue(deviceId, publicKey);
        }
    }

    private static byte[] ChallengeRequestBasis(WorldPeerChallengeRequest value) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer peer roster request proof v1",
            value.Root,
            value.DeviceId,
            value.PublicKey,
            value.CertificateSha256,
            value.Nonce,
            value.IssuedUtc,
            value.KnownRosterRevision,
            rosterSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                value.RosterRevisions ?? [], SharedWorldPeerTrust.Json)))
        }, SharedWorldPeerTrust.Json);

    internal static WorldPeerChallengeRequest SignChallengeRequest(SharedWorldPeerRoot root,
        Guid deviceId, ECDsa key, string fingerprint, long knownRosterRevision,
        IReadOnlyList<SharedWorldRoster>? rosters = null)
    {
        var request = new WorldPeerChallengeRequest(deviceId,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), rosters ?? [],
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), knownRosterRevision, root, fingerprint, "",
            DateTimeOffset.UtcNow);
        return request with
        {
            Signature = Convert.ToBase64String(key.SignData(
            ChallengeRequestBasis(request), HashAlgorithmName.SHA256))
        };
    }

    private static bool VerifyChallengeRequestProof(WorldPeerChallengeRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        if (request.Signature is not { Length: >= 80 and <= 192 } || request.IssuedUtc is not { } issued ||
            issued == default || issued.Offset != TimeSpan.Zero || issued > now.AddMinutes(1) ||
            issued <= now.Subtract(WorldPeerProtocol.ChallengeLifetime)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKey), out _);
            return key.VerifyData(ChallengeRequestBasis(request), Convert.FromBase64String(request.Signature),
                HashAlgorithmName.SHA256);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    private bool ConsumeChallengeRequestProof(WorldPeerChallengeRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in rosterRequestNonces.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            rosterRequestNonces.Remove(expired);
        var nonce = (request.Root!.GroupKey, request.PublicKey, request.Nonce!);
        if (rosterRequestNonces.ContainsKey(nonce) || rosterRequestNonces.Count >= MaximumRosterRequestNonces) return false;
        rosterRequestNonces[nonce] = request.IssuedUtc!.Value.Add(WorldPeerProtocol.ChallengeLifetime);
        return true;
    }

    private static void ValidateChallengeRosters(Group group, IReadOnlyList<SharedWorldRoster> rosters)
    {
        if (rosters.Count > 8 || JsonSerializer.SerializeToUtf8Bytes(rosters, SharedWorldPeerTrust.Json).Length > 1024 * 1024 ||
            rosters.Any(value => value is null || value.ProfileId != group.Root.ProfileId ||
                value.GroupId != group.Root.GroupId || value.OwnerPublicKey != group.Root.OwnerPublicKey ||
                !SharedWorldRosterTrust.VerifySignature(value)))
            throw new InvalidDataException("The challenge roster proof is invalid or oversized.");
    }

    // Both directions use the same exact-parent, protected-floor importer. A
    // gap advances no cursor beyond the locally committed signed revisions.
    private Group ImportChallengeRosters(Group group, IReadOnlyList<SharedWorldRoster> rosters)
    {
        ValidateChallengeRosters(group, rosters);
        var chain = new SharedWorldRosterChainStore(data);
        if (!chain.HasState(group.Root.ProfileId) && StandaloneRosterBaseline(group.Roster))
            chain.Append(group.Roster, group.Root.OwnerPublicKey);
        var legacyParents = new Dictionary<string, SharedWorldRoster>(StringComparer.Ordinal);
        foreach (var roster in rosters)
        {
            if (roster.Schema == 2) legacyParents[SharedWorldRosterTrust.Hash(roster)] = roster;
            if (chain.Contains(group.Root.ProfileId, roster)) continue;
            // A returning legacy PC may retain an intermediate owner snapshot
            // omitted by this journal. Ignore only a verified snapshot strictly
            // between retained legacy counters; current membership still decides
            // whether any metadata or operation is authorized below.
            if (chain.SupersededLegacySnapshot(group.Root.ProfileId, roster)) continue;
            if (roster.PreviousRosterHash is { } parentHash &&
                legacyParents.TryGetValue(parentHash, out var parent) &&
                chain.TryAppendLegacyOwnerProjection(roster, parent, group.Root.OwnerPublicKey)) continue;
            var heads = chain.Heads(group.Root.ProfileId);
            if (heads.Count > 1) throw new InvalidDataException("Competing signed roster histories need review.");
            if (heads.Count == 0 && roster.Schema == 3 && (roster.Revision != 1 || roster.PreviousRosterHash is not null) ||
                heads.Count == 1 && heads[0].Revision < long.MaxValue && roster.Revision > heads[0].Revision + 1 &&
                    !SharedWorldRosterChainStore.LegacyOwnerAdvance(roster, heads[0]))
                break;
            chain.Append(roster, group.Root.OwnerPublicKey);
        }
        return ImportRosters(group, []);
    }

    // A returning PC can be missing the responder's enrollment, or vice versa.
    // Only key possession plus current requester membership releases a roster
    // page. All other callers receive only a signed floor acknowledgement.
    internal WorldPeerChallengeResponse PrepareChallenge(string groupKey, WorldPeerChallengeRequest request,
        string? activeTlsFingerprint = null)
    {
        lock (stateSync)
        {
            ObjectDisposedException.ThrowIf(peerDisposed, this);
            var group = Find(groupKey);
            if (group is null || !group.SharingEnabled || !SharedWorldRosterTrust.ValidKey(request.PublicKey) ||
                request.Root != group.Root || request.KnownRosterRevision < 0 ||
                request.Nonce is not { Length: 44 })
                throw new InvalidDataException("The world peer challenge request is invalid.");
            try
            {
                if (Convert.FromBase64String(request.Nonce).Length != 32)
                    throw new InvalidDataException("The peer request nonce is invalid.");
            }
            catch (FormatException ex) { throw new InvalidDataException("The peer request nonce is invalid.", ex); }
            using var identity = new HostIdentity(data).Load();
            var pin = activeTlsFingerprint ?? (identity is null ? null : HostIdentity.Fingerprint(identity));
            if (!SharedWorldPeerTrust.ValidHash(pin) || request.CertificateSha256 != pin)
                throw new InvalidDataException("The peer request TLS identity is invalid.");
            var rosters = request.RosterRevisions ?? [];
            ValidateChallengeRosters(group, rosters);
            var proved = VerifyChallengeRequestProof(request);
            group = ImportChallengeRosters(group, proved ? rosters : []);
            using var key = RetainedLocalKey(group) ?? throw new InvalidDataException("The responder's retained signing key is unavailable.");
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var known = KnownRosterRevision(group);
            var requesterActive = proved && known >= group.Roster.Revision && known >= request.KnownRosterRevision &&
                Access(group, request.DeviceId, request.PublicKey) is not null && ConsumeChallengeRequestProof(request);
            IReadOnlyList<SharedWorldRoster> pages = requesterActive ? ChallengeRosters(group, request.KnownRosterRevision) : [];
            var challenge = requesterActive && known == request.KnownRosterRevision &&
                Access(group, group.DeviceId, publicKey) is not null
                ? ProtocolFor(group, pin).Issue(request.DeviceId, request.PublicKey) : null;
            var response = new WorldPeerChallengeResponse(1, group.Root, request.DeviceId, request.PublicKey,
                group.DeviceId, publicKey, pin!,
                Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, SharedWorldPeerTrust.Json))),
                known, challenge is null ? "PeerRosterCatchUpRequired" : "PeerChallengeReady", challenge, "", pages);
            return response with
            {
                Signature = Convert.ToBase64String(key.SignData(
                ChallengeResponseBasis(response), HashAlgorithmName.SHA256))
            };
        }
    }

    private static byte[] ChallengeResponseBasis(WorldPeerChallengeResponse value) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer peer roster catchup acknowledgement v1",
            value.Schema,
            value.Root,
            value.DeviceId,
            value.PublicKey,
            value.ResponderDeviceId,
            value.ResponderPublicKey,
            value.CertificateSha256,
            value.RequestSha256,
            value.KnownRosterRevision,
            value.Code,
            value.Challenge,
            value.RosterRevisions
        }, SharedWorldPeerTrust.Json);

    private bool VerifyChallengeResponse(Group group, SharedWorldPeerRoute route,
        WorldPeerChallengeRequest request, WorldPeerChallengeResponse response, string? expectedKey)
    {
        if (response.Schema != 1 || response.Root != group.Root || response.DeviceId != request.DeviceId ||
            response.PublicKey != request.PublicKey || response.CertificateSha256 != route.CertificateSha256 ||
            response.RequestSha256 != Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, SharedWorldPeerTrust.Json))) ||
            response.KnownRosterRevision < 0 || response.Signature is not { Length: >= 80 and <= 192 } ||
            expectedKey is not null && response.ResponderPublicKey != expectedKey ||
            !SharedWorldRosterTrust.ValidKey(response.ResponderPublicKey) ||
            response.RosterRevisions is { Count: > 8 } ||
            response.Code is not ("PeerRosterCatchUpRequired" or "PeerChallengeReady") ||
            (response.Code == "PeerChallengeReady") != (response.Challenge is not null)) return false;
        try
        {
            ValidateChallengeRosters(group, response.RosterRevisions ?? []);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(response.ResponderPublicKey), out _);
            return key.VerifyData(ChallengeResponseBasis(response), Convert.FromBase64String(response.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }

    internal async Task<byte[]> ExchangeAsync(string groupKey, WorldPeerRequest request,
        byte[] payload, CancellationToken cancellationToken, string? activeTlsFingerprint = null)
    {
        Group group;
        WorldPeerProofResult proof;
        lock (stateSync)
        {
            group = Find(groupKey) ?? throw new InvalidDataException("The world peer membership is unavailable.");
            if (!group.SharingEnabled || request.Challenge.Root != group.Root)
                throw new InvalidDataException("The world peer route is disabled.");
            proof = ProtocolFor(group, activeTlsFingerprint).Verify(request, payload, request.Operation);
            if (!proof.Ok) throw new InvalidDataException(proof.Message);
        }
        byte[] result;
        switch (request.Operation)
        {
            case WorldPeerOperations.Metadata:
                result = JsonSerializer.SerializeToUtf8Bytes(BuildMetadata(group,
                    WorldPeerProtocol.DecodeMetadata(payload, group.Root), request.Offset), SharedWorldPeerTrust.Json);
                break;
            case WorldPeerOperations.Catalog:
                result = JsonSerializer.SerializeToUtf8Bytes(await LocalCatalogAsync(group, cancellationToken), Json);
                break;
            case WorldPeerOperations.Version:
                var version = await FindManifestAsync(group, request.VersionHash!, request.ChunkOffset, cancellationToken);
                if (version is null) throw new InvalidDataException("The requested signed world version is unavailable.");
                result = JsonSerializer.SerializeToUtf8Bytes(version, Json);
                break;
            case WorldPeerOperations.Chunk:
                result = await ReadChunkAsync(group, request.VersionHash!, request.FileIndex!.Value,
                    request.ChunkOffset!.Value, cancellationToken) ??
                    throw new InvalidDataException("The requested verified world chunk is unavailable.");
                break;
            case WorldPeerOperations.Receipt:
                var receipt = SharedWorldReceiptTrust.Parse(payload);
                if (receipt is null || receipt.DeviceId != request.Challenge.DeviceId ||
                    receipt.VersionHash != request.VersionHash)
                    throw new InvalidDataException("The copy receipt does not match this signed request.");
                result = JsonSerializer.SerializeToUtf8Bytes(
                    await TryAcceptLiveTestReceiptAsync(group, receipt, cancellationToken) ?? AcceptReceipt(group, payload), Json);
                break;
            case WorldPeerOperations.Enrollment:
                var enrollment = await EnrollSessionAsync(group, request, payload, cancellationToken);
                if (enrollment is null) throw new InvalidDataException("The shared world session is unavailable for enrollment.");
                result = JsonSerializer.SerializeToUtf8Bytes(enrollment, Json);
                break;
            case WorldPeerOperations.Seed:
                result = JsonSerializer.SerializeToUtf8Bytes(await AcceptSeedAsync(group, request,
                    WorldPeerProtocol.DecodeEnvelope<WorldPeerSeedRequest>(payload, SharedWorldPeerTrust.MaximumPageBytes),
                    cancellationToken), Json);
                break;
            default:
                throw new InvalidDataException("The fixed world peer operation is invalid.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var current = Find(groupKey);
        var access = current is null ? null : Access(current, request.Challenge.DeviceId, request.Challenge.PublicKey);
        if (current is null || !current.SharingEnabled || access is null || KnownRosterRevision(current) < current.Roster.Revision ||
            Access(current, current.DeviceId, request.Challenge.ResponderPublicKey) is null ||
            request.Operation is WorldPeerOperations.Chunk or WorldPeerOperations.Receipt && !access.Receive ||
            request.Operation == WorldPeerOperations.Seed && access is not { Receive: true, Reshare: true })
            throw new InvalidDataException("World peer access changed during the request.");
        if (result.Length > WorldPeerProtocol.ResponseLimit(request.Operation))
            throw new InvalidDataException("The world peer response is oversized.");
        return result;
    }

    private WorldPeerMetadataResponse BuildMetadata(Group group, WorldPeerMetadataRequest request, int offset)
    {
        var store = Store(group);
        var acceptedOffset = request.Page?.Offset ?? 0;
        if (request.Page is not null)
        {
            var imported = store.ImportPage(request.Page);
            acceptedOffset = imported.Code == "PeerCatchUpRequired" || UnknownMembers(group, request.Page) ? 0 : request.Page.NextOffset;
        }
        ImportDefaultSelections(group, request.DefaultSelections ?? []);
        var page = store.ReadPage(offset);
        var chain = new SharedWorldRosterChainStore(data);
        var first = chain.First(group.Root.ProfileId);
        if (first is not null && (first.GroupId != group.Root.GroupId || first.OwnerPublicKey != group.Root.OwnerPublicKey))
            throw new InvalidDataException("The roster journal belongs to another shared world.");
        IReadOnlyList<SharedWorldRoster> rosters;
        long rosterTotal;
        if (first is null)
        {
            // A signed cached schema-1/2 owner roster may be a chain baseline.
            // A schema-3 delegated head alone can never replace its ancestors.
            rosters = group.Roster.Schema < 3 && request.KnownRosterRevision < group.Roster.Revision
                ? [group.Roster] : [];
            rosterTotal = rosters.Count;
        }
        else
        {
            rosters = chain.ReadAfterRevision(group.Root.ProfileId, request.KnownRosterRevision).Take(8).ToArray();
            rosterTotal = chain.Count(group.Root.ProfileId);
        }
        var records = ScopedAuthority(group);
        if (request.AuthorityOffset > records.Count)
            throw new InvalidDataException("The peer authority cursor is ahead of this journal.");
        var authorities = records.Where((_, index) => index == request.AuthorityOffset).Take(1).ToArray();
        var response = new WorldPeerMetadataResponse(page, rosters, authorities, records.Count, rosterTotal,
            acceptedOffset, ReadDefaultSelections(group));
        // Each record stays whole. Never emit incomplete JSON or remove a
        // retained history to squeeze it into one network response.
        while (response.RosterRevisions.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(response, SharedWorldPeerTrust.Json).Length >
            WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Metadata))
            response = response with { RosterRevisions = response.RosterRevisions.Take(response.RosterRevisions.Count - 1).ToArray() };
        return response;
    }

    private IReadOnlyList<WorldAuthorityRecord> ScopedAuthority(Group group)
    {
        var records = new WorldAuthorityStore(data).Read(group.Root.ProfileId);
        if (records.Any(value => value.Roster.OwnerPublicKey != group.Root.OwnerPublicKey ||
            value.Proposal.GroupId != group.Root.GroupId))
            throw new InvalidDataException("The signed authority journal belongs to another world.");
        return records;
    }

    private static string RouteKey(SharedWorldPeerRoute route) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(route.Address.TrimEnd('/') + "\n" + route.CertificateSha256)));
    private static string CursorName(Group group, SharedWorldPeerRoute route) =>
        $"world-peer-cursor-{group.Root.GroupKey}-{RouteKey(route)}.protected";

    private PeerCursor ReadCursor(Group group, SharedWorldPeerRoute route)
    {
        var name = CursorName(group, route);
        var existed = data.HasProtected(name);
        var bytes = data.LoadProtected(name);
        if (bytes is null)
        {
            if (existed) throw new InvalidDataException("The world peer cursor needs review.");
            if (Directory.EnumerateFiles(data.RootPath, $"world-peer-cursor-{group.Root.GroupKey}-*.protected")
                .Take(SharedWorldPeerStore.MaximumPeers).Count() >= SharedWorldPeerStore.MaximumPeers)
                throw new InvalidDataException("This world's peer cursor capacity is full; signed histories were kept.");
            return new(1, group.Root.GroupKey, RouteKey(route));
        }
        if (bytes.Length > 4096) throw new InvalidDataException("The world peer cursor is oversized.");
        var cursor = JsonSerializer.Deserialize<PeerCursor>(bytes, SharedWorldPeerTrust.Json);
        if (cursor is null || cursor.Schema != 1 || cursor.GroupKey != group.Root.GroupKey ||
            cursor.RouteKey != RouteKey(route) || cursor.SentOffset is < 0 or > SharedWorldPeerStore.MaximumAnnouncements ||
            cursor.ReceivedOffset is < 0 or > SharedWorldPeerStore.MaximumAnnouncements || cursor.AuthorityOffset < 0 ||
            cursor.KnownRemoteRosterRevision < 0)
            throw new InvalidDataException("The world peer cursor is invalid.");
        return cursor;
    }

    private void SaveCursor(Group group, SharedWorldPeerRoute route, PeerCursor cursor) =>
        data.SaveProtected(CursorName(group, route), JsonSerializer.SerializeToUtf8Bytes(cursor, SharedWorldPeerTrust.Json));

    private IReadOnlyList<SharedWorldRoster> ChallengeRosters(Group group, long knownRemoteRevision)
    {
        var chain = new SharedWorldRosterChainStore(data);
        var first = chain.First(group.Root.ProfileId);
        IReadOnlyList<SharedWorldRoster> page;
        if (first is null) page = group.Roster.Schema < 3 || group.Roster.Revision == 1 && group.Roster.PreviousRosterHash is null
            ? [group.Roster] : [];
        else
        {
            if (first.GroupId != group.Root.GroupId || first.OwnerPublicKey != group.Root.OwnerPublicKey)
                throw new InvalidDataException("The challenge roster journal belongs to another world.");
            page = chain.ReadAfterRevision(group.Root.ProfileId, knownRemoteRevision).Take(8).ToArray();
        }
        while (page.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(page, SharedWorldPeerTrust.Json).Length > 1024 * 1024)
            page = page.Take(page.Count - 1).ToArray();
        return page;
    }

    private long KnownRosterRevision(Group group)
    {
        var heads = new SharedWorldRosterChainStore(data).Heads(group.Root.ProfileId);
        if (heads.Count == 0 && StandaloneRosterBaseline(group.Roster)) return group.Roster.Revision;
        return heads.Count == 1 && heads[0].GroupId == group.Root.GroupId &&
            heads[0].OwnerPublicKey == group.Root.OwnerPublicKey ? heads[0].Revision : 0;
    }

    private static bool StandaloneRosterBaseline(SharedWorldRoster roster) => SharedWorldRosterTrust.Verify(roster) &&
        (roster.Schema < 3 || roster.Revision == 1 && roster.PreviousRosterHash is null &&
            roster.SignerDeviceId == Guid.Empty && roster.SignerPublicKey == roster.OwnerPublicKey);

    private Group ImportRosters(Group group, IReadOnlyList<SharedWorldRoster> rosters)
    {
        if (rosters.Count > 8 || rosters.Any(value => value is null))
            throw new InvalidDataException("The peer roster page is oversized or invalid.");
        var chain = new SharedWorldRosterChainStore(data);
        var legacyParents = new Dictionary<string, SharedWorldRoster>(StringComparer.Ordinal);
        foreach (var roster in rosters)
        {
            if (roster.ProfileId != group.Root.ProfileId || roster.GroupId != group.Root.GroupId ||
                roster.OwnerPublicKey != group.Root.OwnerPublicKey || !SharedWorldRosterTrust.VerifySignature(roster))
                throw new InvalidDataException("The signed peer roster crossed a world boundary.");
            if (roster.Schema == 2) legacyParents[SharedWorldRosterTrust.Hash(roster)] = roster;
            if (!chain.Contains(group.Root.ProfileId, roster) &&
                !chain.SupersededLegacySnapshot(group.Root.ProfileId, roster))
            {
                if (roster.PreviousRosterHash is { } parentHash &&
                    legacyParents.TryGetValue(parentHash, out var parent) &&
                    chain.TryAppendLegacyOwnerProjection(roster, parent, group.Root.OwnerPublicKey)) continue;
                chain.Append(roster, group.Root.OwnerPublicKey);
            }
        }
        var heads = chain.Heads(group.Root.ProfileId);
        if (heads.Count > 1) throw new InvalidDataException("Competing signed roster histories need review.");
        if (heads.Count == 0 || heads[0].Revision < group.Roster.Revision) return group;
        var latest = heads[0];
        if (!SharedWorldRosterTrust.Verify(latest) || latest.GroupId != group.Root.GroupId ||
            latest.OwnerPublicKey != group.Root.OwnerPublicKey ||
            latest.Revision == group.Roster.Revision && latest.Signature != group.Roster.Signature &&
                !chain.ProvesLegacyOwnerProjection(group.Root.ProfileId, group.Roster, latest))
            throw new InvalidDataException("The signed roster floor changed.");
        group = group with { Roster = latest };
        SaveGroup(group);
        return group;
    }

    private async Task<bool> ImportAuthorityAsync(Group group, SharedWorldPeerRoute route,
        WorldAuthorityRecord record, CancellationToken cancellationToken)
    {
        var store = new WorldAuthorityStore(data);
        var known = ScopedAuthority(group);
        if (known.Any(value => value.RecordHash == record.RecordHash)) return true;
        if (record.Roster.OwnerPublicKey != group.Root.OwnerPublicKey ||
            record.Proposal.GroupId != group.Root.GroupId || record.Proposal.ProfileId != group.Root.ProfileId ||
            !WorldAuthorityTrust.Verify(record, rosterChainVerified: true))
            throw new InvalidDataException("The signed peer authority record is invalid.");
        if (!new SharedWorldRosterChainStore(data).Contains(group.Root.ProfileId, record.Roster)) return false;
        var parent = known.SingleOrDefault(value => value.RecordHash == record.Proposal.ParentAuthorityHash);
        if (record.Proposal.ParentAuthorityHash is not null && parent is null) return false;
        if (record.VersionLineageDigest is not null)
        {
            const int proofBudget = 12;
            var staged = await FriendLink.StageAuthorityProofBatchAsync(store, record, parent, proofBudget,
                async (number, token) =>
                {
                    var bytes = await SendPeerAsync(group, route, WorldPeerOperations.Version, token,
                        versionHash: record.RecordHash, chunkOffset: number);
                    return JsonSerializer.Deserialize<SharedWorldVersion>(bytes, SharedWorldPeerTrust.Json);
                }, cancellationToken);
            if (!staged.Complete) return false;
            var verified = FriendLink.VerifyStagedAuthorityProofBatch(store, record, parent, proofBudget, cancellationToken);
            if (!verified.Complete) return false;
            store.AppendReceivedStaged(record, parent, group.Root.ProfileId, group.Root.GroupId, group.Root.OwnerPublicKey);
        }
        else store.AppendReceived(record, group.Root.ProfileId, group.Root.GroupId, group.Root.OwnerPublicKey);
        return true;
    }

    private async Task<byte[]> SendPeerAsync(Group group, SharedWorldPeerRoute route, string operation,
        CancellationToken cancellationToken, byte[]? payload = null, int offset = 0,
        string? versionHash = null, int? fileIndex = null, long? chunkOffset = null)
    {
        // A responder retains one challenge per requester/world. Do not let a
        // foreground metadata handshake invalidate a background payload proof.
        var wireGate = ExchangeGatesFor(group.Root).Wire;
        await wireGate.WaitAsync(cancellationToken);
        try
        {
            return await SendPeerCoreAsync(group, route, operation, cancellationToken,
                payload, offset, versionHash, fileIndex, chunkOffset);
        }
        finally { wireGate.Release(); }
    }

    private async Task<byte[]> SendPeerCoreAsync(Group group, SharedWorldPeerRoute route, string operation,
        CancellationToken cancellationToken, byte[]? payload, int offset,
        string? versionHash, int? fileIndex, long? chunkOffset)
    {
        ObjectDisposedException.ThrowIf(peerDisposed, this);
        if (!SharedWorldPeerTrust.ValidRoute(route)) throw new InvalidDataException("The direct peer route is invalid.");
        group = Find(group.Root.GroupKey) ?? throw new InvalidDataException("The world membership changed.");
        using var key = RetainedLocalKey(group) ?? throw new InvalidDataException("The retained member signing key is unavailable.");
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var state = Store(group).Project();
        var announced = state.Peers.Where(value => value.Authorized && !value.HasConflict &&
            value.Routes.Contains(route)).Select(value => value.PublicKey).Distinct(StringComparer.Ordinal).ToArray();
        var bootstrap = group.Bootstrap is not null && RouteKey(group.Bootstrap) == RouteKey(route);
        var manualIdentity = AdditionalRoutePublicKey(group, route);
        if (announced.Length > 1 || announced.Length == 0 && !bootstrap && manualIdentity is null)
            throw new InvalidDataException("The peer route has no unambiguous signed identity.");
        using var client = makeClient(route.Address, [route.CertificateSha256]);
        client.DefaultRequestHeaders.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString(CultureInfo.InvariantCulture));
        client.DefaultRequestHeaders.Add("X-TogetherServer-Capability", CompanionProtocol.SharedWorldPeersCapability);
        var prefix = $"api/companion/world-peers/{group.Root.GroupKey}/";
        var observationContext = transferAttempt.Value;
        var observation = observationContext?.Attempt;
        WorldPeerChallenge? challenge = null;
        var expectedIdentity = announced.Length == 1 ? announced[0] : manualIdentity;
        var maySendRosters = expectedIdentity is not null && CurrentPeerKey(group, expectedIdentity);
        for (var batch = 0; batch < 2 && challenge is null; batch++)
        {
            var cursor = ReadCursor(group, route);
            // An unproved contact receives no membership list. Its response can
            // first relay independently verified owner-rooted enrollment proof.
            var hello = SignChallengeRequest(group.Root, group.DeviceId, key, route.CertificateSha256,
                KnownRosterRevision(group), maySendRosters && KnownRosterRevision(group) >= cursor.KnownRemoteRosterRevision
                    ? ChallengeRosters(group, cursor.KnownRemoteRosterRevision) : []);
            using var challengeRequest = new HttpRequestMessage(HttpMethod.Post, prefix + "challenge")
            { Content = JsonContent.Create(hello, options: SharedWorldPeerTrust.Json) };
            observation?.Request(TransferOperation(operation), WorldPeerTransferHttpStep.Challenge);
            using var challengeResponse = await client.SendAsync(challengeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            observation?.Response((int)challengeResponse.StatusCode);
            var challengeBytes = await FriendLink.ReadBoundedSharedAsync(challengeResponse.Content,
                MaximumRosterHandshakeBytes, cancellationToken);
            if (!challengeResponse.IsSuccessStatusCode)
                throw new HttpRequestException("The pinned peer did not return a bounded challenge.", null,
                    challengeResponse.StatusCode);
            if (challengeBytes is null) throw new InvalidDataException("The pinned peer challenge exceeded its fixed limit.");
            var reply = WorldPeerProtocol.DecodeEnvelope<WorldPeerChallengeResponse>(challengeBytes, MaximumRosterHandshakeBytes);
            if (!VerifyChallengeResponse(group, route, hello, reply, expectedIdentity))
                throw new InvalidDataException("The peer roster acknowledgement did not verify.");
            lock (stateSync)
            {
                group = Find(group.Root.GroupKey) ?? throw new InvalidDataException("World membership changed during roster catchup.");
                var currentCursor = ReadCursor(group, route);
                if (reply.KnownRosterRevision < currentCursor.KnownRemoteRosterRevision ||
                    (reply.RosterRevisions ?? []).Any(value => value.Revision > reply.KnownRosterRevision))
                    throw new InvalidDataException("The peer roster acknowledgement moved its protected cursor backwards.");
                group = ImportChallengeRosters(group, reply.RosterRevisions ?? []);
                var known = KnownRosterRevision(group);
                maySendRosters = known >= group.Roster.Revision && known >= reply.KnownRosterRevision &&
                    Access(group, reply.ResponderDeviceId, reply.ResponderPublicKey) is not null;
                if (reply.Challenge is not null && (!maySendRosters || known != reply.KnownRosterRevision ||
                    Access(group, group.DeviceId, publicKey) is null))
                    throw new InvalidDataException("Both current memberships must be proved before a peer challenge is accepted.");
                SaveCursor(group, route, currentCursor with { KnownRemoteRosterRevision = reply.KnownRosterRevision });
            }
            expectedIdentity ??= reply.ResponderPublicKey;
            challenge = reply.Challenge;
        }
        if (challenge is null) throw new InvalidDataException("The peer's signed membership is still catching up.");
        // A bootstrap origin was pinned during the existing Host pairing. Its
        // authenticated responder must still be an exact active roster member.
        // Learned origins additionally require the announcing signing key.
        var expectedKey = expectedIdentity ?? challenge.ResponderPublicKey;
        if (!WorldPeerProtocol.VerifyChallenge(challenge, group.Root, expectedKey, route.CertificateSha256,
            group.DeviceId, publicKey, Trust(group)))
            throw new InvalidDataException("The peer challenge identity did not match this route and world.");
        observation?.AuthenticatedRole(expectedKey == group.Root.OwnerPublicKey
            ? WorldPeerTransferRecipientRole.OriginalHost : WorldPeerTransferRecipientRole.ApprovedPeer);
        if (observationContext is not null)
        {
            observationContext.ResponderDeviceId = challenge.ResponderDeviceId;
            observationContext.ResponderPublicKey = challenge.ResponderPublicKey;
        }
        var body = payload ?? [];
        var request = WorldPeerProtocol.Sign(challenge, operation, key, offset, versionHash, fileIndex, chunkOffset, body);
        using var exchange = new HttpRequestMessage(HttpMethod.Post, prefix + "exchange")
        { Content = JsonContent.Create(new WorldPeerExchangeRequest(request, body), options: SharedWorldPeerTrust.Json) };
        observation?.Request(TransferOperation(operation), WorldPeerTransferHttpStep.Exchange);
        using var response = await client.SendAsync(exchange, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        observation?.Response((int)response.StatusCode);
        var bytes = await FriendLink.ReadBoundedSharedAsync(response.Content, WorldPeerProtocol.ResponseLimit(operation), cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The pinned peer did not return a bounded world response.", null,
                response.StatusCode);
        if (bytes is null) throw new InvalidDataException("The pinned peer response exceeded its fixed limit.");
        var current = Find(group.Root.GroupKey);
        if (current is null || KnownRosterRevision(current) < current.Roster.Revision ||
            Access(current, group.DeviceId, publicKey) is null ||
            Access(current, challenge.ResponderDeviceId, challenge.ResponderPublicKey) is null)
            throw new InvalidDataException("World membership changed during the peer request.");
        return bytes;
    }

    private bool CurrentPeerKey(Group group, string publicKey)
    {
        if (KnownRosterRevision(group) < group.Roster.Revision) return false;
        if (publicKey == group.Root.OwnerPublicKey) return Access(group, Guid.Empty, publicKey) is not null;
        var member = group.Roster.Members.SingleOrDefault(item => item.PublicKey == publicKey);
        return member is not null && Access(group, member.DeviceId, publicKey) is not null;
    }

    private Task SyncPeerAsync(Group group, SharedWorldPeerRoute route, CancellationToken cancellationToken,
        bool includePayloads = true, bool background = false)
    {
        if (!includePayloads) return SyncPeerMetadataAsync(group, route, cancellationToken);
        var gates = ExchangeGatesFor(group.Root);
        var routeKey = RouteKey(route);
        lock (stateSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // An explicit preparation joins the same useful transfer. It does
            // not queue another pull/seed behind that completed request. While
            // preparing, subsequent background routes yield to that world.
            if (gates.Transfers.TryGetValue(routeKey, out var existing))
                return existing.Completion.WaitAsync(cancellationToken);
            if (background && gates.Preparations > 0) return Task.CompletedTask;
            var flight = new PeerTransferFlight();
            gates.Transfers.Add(routeKey, flight);
            flight.Completion = Task.Run(async () =>
            {
                try
                {
                    await gates.Transfer.WaitAsync(cancellationToken);
                    try
                    {
                        using var observation = transferObservations.Begin(group.Root.GroupKey, routeKey);
                        transferAttempt.Value = new(observation);
                        try
                        {
                            observation.Phase(WorldPeerTransferPhase.Metadata);
                            await SyncPeerMetadataAsync(group, route, cancellationToken);
                            var current = Find(group.Root.GroupKey) ?? throw new InvalidDataException("World membership changed.");
                            observation.Phase(WorldPeerTransferPhase.Pulling);
                            await PullCatalogAsync(current, route, cancellationToken);
                            observation.Phase(WorldPeerTransferPhase.Offering);
                            observation.Complete(await SeedPeerAsync(current, route, cancellationToken));
                        }
                        catch (Exception error)
                        {
                            ObserveTransferFailure(observation, error, cancellationToken);
                            throw;
                        }
                        finally { transferAttempt.Value = null; }
                    }
                    finally { gates.Transfer.Release(); }
                }
                finally
                {
                    lock (stateSync)
                        if (gates.Transfers.GetValueOrDefault(routeKey) == flight) gates.Transfers.Remove(routeKey);
                }
            }, CancellationToken.None);
            return flight.Completion.WaitAsync(cancellationToken);
        }
    }

    private async Task SyncPeerMetadataAsync(Group group, SharedWorldPeerRoute route, CancellationToken cancellationToken)
    {
        var metadataGate = ExchangeGatesFor(group.Root).Metadata;
        await metadataGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(peerDisposed, this);
            group = Find(group.Root.GroupKey) ?? throw new InvalidDataException("World membership changed before metadata exchange.");
            group = await ExchangePeerMetadataAsync(group, route, cancellationToken);
        }
        finally { metadataGate.Release(); }
        // Payload work follows outside this transaction, so a prepared Start
        // can check signed heads between bounded payload requests.
    }

    private async Task<Group> ExchangePeerMetadataAsync(Group group, SharedWorldPeerRoute route,
        CancellationToken cancellationToken)
    {
        var cursor = ReadCursor(group, route);
        var localPage = Store(group).ReadPage(cursor.SentOffset);
        var metadata = new WorldPeerMetadataRequest(localPage, cursor.AuthorityOffset,
            KnownRosterRevision(group), ReadDefaultSelections(group));
        var payload = JsonSerializer.SerializeToUtf8Bytes(metadata, SharedWorldPeerTrust.Json);
        while (payload.Length > SharedWorldPeerTrust.MaximumPageBytes && localPage.Announcements.Count > 0)
        {
            localPage = localPage with { Announcements = localPage.Announcements.Take(localPage.Announcements.Count - 1).ToArray() };
            metadata = metadata with { Page = localPage };
            payload = JsonSerializer.SerializeToUtf8Bytes(metadata, SharedWorldPeerTrust.Json);
        }
        var bytes = await SendPeerAsync(group, route, WorldPeerOperations.Metadata, cancellationToken,
            payload, cursor.ReceivedOffset);
        group = Find(group.Root.GroupKey) ?? throw new InvalidDataException("World membership changed during metadata catchup.");
        var response = WorldPeerProtocol.DecodeEnvelope<WorldPeerMetadataResponse>(bytes,
            WorldPeerProtocol.ResponseLimit(WorldPeerOperations.Metadata));
        if (response.Page is null || response.Page.Root != group.Root || response.Page.Offset != cursor.ReceivedOffset ||
            response.RosterRevisions is null || response.AuthorityRecords is null || response.AuthorityRecords.Count > 1 ||
            response.DefaultSelections is { Count: > 64 } ||
            response.AuthorityTotal < cursor.AuthorityOffset ||
            response.AuthorityRecords.Count > response.AuthorityTotal - cursor.AuthorityOffset || response.RosterTotal < 0 ||
            response.AcceptedPeerOffset < 0 || response.AcceptedPeerOffset > localPage.NextOffset)
            throw new InvalidDataException("The peer metadata response crossed a world or cursor boundary.");
        lock (stateSync)
            group = ImportRosters(Find(group.Root.GroupKey) ??
                throw new InvalidDataException("World membership changed during metadata import."), response.RosterRevisions);
        var imported = Store(group).ImportPage(response.Page);
        var authorityOffset = cursor.AuthorityOffset;
        foreach (var record in response.AuthorityRecords)
            if (await ImportAuthorityAsync(group, route, record, cancellationToken)) authorityOffset++;
            else break;
        ImportDefaultSelections(group, response.DefaultSelections ?? []);
        cursor = cursor with
        {
            SentOffset = response.AcceptedPeerOffset,
            ReceivedOffset = imported.Code == "PeerCatchUpRequired" || UnknownMembers(group, response.Page) ? 0 : response.Page.NextOffset,
            AuthorityOffset = authorityOffset
        };
        lock (stateSync)
        {
            cursor = cursor with { KnownRemoteRosterRevision = ReadCursor(group, route).KnownRemoteRosterRevision };
            SaveCursor(group, route, cursor);
        }
        var peer = Store(group).Project().Peers.SingleOrDefault(value => value.Routes.Contains(route));
        if (peer is not null) Store(group).RecordSync(peer.DeviceId, peer.PublicKey, true, response.Page.Total);
        if (authorityOffset < response.AuthorityTotal || response.Page.NextOffset < response.Page.Total)
            SetPeerWarning(group.Root.GroupKey, "Signed world information is catching up; existing histories remain available.");
        return group;
    }

    private void SetPeerWarning(string groupKey, string? message)
    {
        lock (stateSync)
        {
            if (message is null) warnings.Remove(groupKey);
            else warnings[groupKey] = message;
        }
    }

    internal void ScheduleSync(CancellationToken cancellationToken)
    {
        lock (stateSync)
        {
            if (peerDisposed || cancellationToken.IsCancellationRequested || backgroundSync is { IsCompleted: false }) return;
            backgroundSync = Task.Run(async () =>
            {
                try { await SyncNowAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex) when (PeerFailure(ex)) { /* Per-group detail is projected by SyncNowAsync. */ }
            }, cancellationToken);
        }
    }

    internal Task WaitForSyncAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (stateSync) task = Task.WhenAll(new[] { backgroundSync ?? Task.CompletedTask,
            syncFlight ?? Task.CompletedTask, membershipRefresh ?? Task.CompletedTask }
            .Concat(worldExchangeGates.Values.SelectMany(gates => gates.Transfers.Values)
                .Select(flight => flight.Completion)));
        return task.WaitAsync(cancellationToken);
    }

    // Explicit preparation must check its exact world instead of whichever
    // four groups the background round-robin happens to visit this pass.
    private async Task SyncWorldAsync(SharedWorldPeerRoot root, CancellationToken cancellationToken)
    {
        var group = Find(root.GroupKey);
        if (group is null || group.Root != root) return;
        ObjectDisposedException.ThrowIf(peerDisposed, this);
        // Registration runs at startup and in the background. An exact
        // known world uses its signed membership and current peer roster
        // handshake instead of waiting for every saved Host connection.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        using var key = RetainedLocalKey(group);
        if (key is null) return;
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        if (KnownRosterRevision(group) >= group.Roster.Revision && Access(group, group.DeviceId, publicKey) is not null)
        {
            await PublishLocalAdvertisementAsync(group, bounded.Token);
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        // A first reachable peer can teach this PC another member's route.
        // Recompute after every request while bounding all network work.
        for (var attempt = 0; attempt < SharedWorldPeerStore.MaximumPeers && !bounded.IsCancellationRequested; attempt++)
        {
            group = Find(root.GroupKey) ?? throw new InvalidDataException("World membership changed.");
            var peers = Store(group).Project().Peers.Where(peer => peer.Authorized && !peer.HasConflict && peer.PublicKey != publicKey)
                .SelectMany(peer => peer.Routes);
            var route = (group.Bootstrap is null ? peers : peers.Prepend(group.Bootstrap)).Concat(AdditionalRoutes(group))
                .GroupBy(RouteKey).Select(routes => routes.First()).FirstOrDefault(route => visited.Add(RouteKey(route)));
            if (route is null) break;
            try { await SyncPeerAsync(group, route, bounded.Token); }
            catch (Exception error) when (PeerFailure(error) || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { SetPeerWarning(root.GroupKey, "A known peer could not be synchronized. Cached signed information and copies were kept."); }
        }
    }

    internal Task SyncNowAsync(CancellationToken cancellationToken)
    {
        lock (stateSync)
        {
            ObjectDisposedException.ThrowIf(peerDisposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (syncFlight is not { IsCompleted: false })
                syncFlight = Task.Run(() => SyncNowCoreAsync(cancellationToken), CancellationToken.None);
            // A joining caller can cancel its own wait without canceling the
            // already running pass owned by the original caller/shutdown.
            return syncFlight.WaitAsync(cancellationToken);
        }
    }

    internal async Task<WorldPeerActionResult> SyncWorldNowAsync(string groupKey, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var group = Find(groupKey);
            if (group is null)
                return new(false, "WorldMembershipRequired", "Verify this shared world's membership before checking its peers.");
            using var preparation = BeginHostingPreparation(group.Root);
            await SyncWorldAsync(group.Root, bounded.Token);
            // This exact world may have joined a transfer whose old metadata
            // predates the instruction. Follow it with a fresh metadata-only
            // barrier, without waiting for an unrelated global background pass.
            if (await DiscoverHostingMetadataAsync(group.Root, bounded.Token))
                return new(true, "WorldPeersChecked", "Fresh signed information was checked for this world. Completed copies and game joins remain separate checks.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (PeerFailure(error)) { }
        return new(false, "WorldPeerSyncInconclusive", "Fresh signed information could not be confirmed. Existing signed history and copies were kept; retry when an approved peer is reachable.");
    }

    private async Task SyncNowCoreAsync(CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(45));
        Task registration;
        lock (stateSync)
        {
            if (membershipRefresh is not { IsCompleted: false })
                membershipRefresh = Task.Run(() => RefreshScheduledMembershipsAsync(cancellationToken), CancellationToken.None);
            registration = membershipRefresh;
        }
        // Existing worlds exchange against their signed registry immediately.
        // A slow original-Host poll/roster refresh cannot hold their delivery.
        // A new empty registry still needs its first verified registration.
        if (Groups().Length == 0) await registration.WaitAsync(bounded.Token);
        Group[] selected;
        Group[] known;
        lock (stateSync)
        {
            var groups = Groups();
            if (groups.Length == 0) return;
            known = groups;
            selected = Enumerable.Range(0, Math.Min(4, groups.Length))
                .Select(index => groups[(groupPosition + index) % groups.Length]).ToArray();
            groupPosition = (groupPosition + selected.Length) % groups.Length;
        }
        foreach (var initial in selected)
        {
            if (bounded.IsCancellationRequested) break;
            await SyncScheduledGroupAsync(initial, bounded.Token, cancellationToken);
        }
        await registration;
        // Registration may rebind a retained world to a freshly paired member
        // while its old identity is being refused. Finish that explicit sync
        // with the new signed membership, without replaying unchanged groups.
        // One bounded follow-up also covers newly registered worlds.
        var changed = Groups().Where(current =>
            !known.Any(before => before.Root == current.Root) ||
            selected.Any(before => before.Root == current.Root && !SameSyncIdentity(before, current)))
            .Take(4).ToArray();
        foreach (var current in changed)
        {
            if (bounded.IsCancellationRequested) break;
            await SyncScheduledGroupAsync(current, bounded.Token, cancellationToken);
        }
    }

    private static bool SameSyncIdentity(Group before, Group after) =>
        before.Root == after.Root && before.DeviceId == after.DeviceId &&
        before.Bootstrap == after.Bootstrap;

    private async Task SyncScheduledGroupAsync(Group group, CancellationToken budget, CancellationToken caller)
    {
        try
        {
            using var key = RetainedLocalKey(group);
            if (key is null) return;
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            if (KnownRosterRevision(group) >= group.Roster.Revision && Access(group, group.DeviceId, publicKey) is not null)
            {
                await PublishLocalAdvertisementAsync(group, budget, background: true);
            }
            var peers = Store(group).Project().Peers.Where(value => value.Authorized && !value.HasConflict && value.PublicKey != publicKey)
                .SelectMany(value => value.Routes);
            var routes = (group.Bootstrap is null ? peers : peers.Prepend(group.Bootstrap)).Concat(AdditionalRoutes(group))
                .GroupBy(RouteKey).Select(values => values.First()).Take(SharedWorldPeerStore.MaximumPeers).ToArray();
            if (routes.Length == 0) return;
            var start = routePositions.GetValueOrDefault(group.Root.GroupKey) % routes.Length;
            routePositions[group.Root.GroupKey] = (start + Math.Min(2, routes.Length)) % routes.Length;
            SetPeerWarning(group.Root.GroupKey, null);
            for (var index = 0; index < Math.Min(2, routes.Length); index++)
            {
                if (budget.IsCancellationRequested) break;
                var route = routes[(start + index) % routes.Length];
                try
                {
                    await SyncPeerAsync(group, route, budget, background: true);
                    group = Find(group.Root.GroupKey) ?? group;
                }
                catch (Exception ex) when (PeerFailure(ex) || ex is OperationCanceledException && !caller.IsCancellationRequested)
                {
                    var peer = Store(group).Project().Peers.SingleOrDefault(value => value.Routes.Contains(route));
                    if (peer is not null) Store(group).RecordSync(peer.DeviceId, peer.PublicKey, false);
                    SetPeerWarning(group.Root.GroupKey, "A known peer could not be synchronized. Cached signed information and copies were kept.");
                }
            }
        }
        catch (Exception ex) when (PeerFailure(ex) || ex is OperationCanceledException && !caller.IsCancellationRequested)
        { SetPeerWarning(group.Root.GroupKey, "This world's signed peer information needs review; existing histories were kept."); }
    }

    private async Task RefreshScheduledMembershipsAsync(CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await refreshGate.WaitAsync(bounded.Token);
            try { await RefreshMembershipsAsync(true, bounded.Token); }
            finally { refreshGate.Release(); }
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested) { }
        catch (Exception error) when (PeerFailure(error))
        { /* Retain the verified registry; the next bounded refresh retries. */ }
    }

    public void Dispose()
    {
        lock (stateSync)
        {
            if (peerDisposed) return;
            peerDisposed = true;
            foreach (var state in peerProtocols.Values) state.Key.Dispose();
            peerProtocols.Clear();
            rosterRequestNonces.Clear();
        }
    }

    private static bool UnknownMembers(Group group, SharedWorldPeerPage page) => page.Announcements.Any(value =>
        value.DeviceId == Guid.Empty ? value.PublicKey != group.Root.OwnerPublicKey :
        !group.Roster.Members.Any(member => member.DeviceId == value.DeviceId && member.PublicKey == value.PublicKey));
}
