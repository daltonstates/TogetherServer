using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

public sealed class PairedDevice
{
    public Guid Id { get; set; }
    // ProfileId and InviteGeneration identify the server code that issued this
    // credential. AssignedProfileIds is the separate, owner-managed access set.
    public Guid ProfileId { get; set; }
    public Guid InviteGeneration { get; set; }
    public List<Guid>? AssignedProfileIds { get; set; }
    public string Name { get; set; } = "";
    public bool CanStart { get; set; }
    public bool CanStop { get; set; }
    public bool CanExtendTimer { get; set; }
    // Global permissions remain the default. Entries are stored only when an
    // assigned server differs from that default, so the owner can see and edit
    // explicit per-server exceptions without duplicating the access list.
    public List<ServerPermissionOverride>? ServerPermissionOverrides { get; set; }
    public bool Revoked { get; set; }
    public bool ApprovalPending { get; set; }
    public string? InviteHash { get; set; }
    public DateTimeOffset? InviteExpiresUtc { get; set; }
    public string? CredentialHash { get; set; }
    public DateTimeOffset? CredentialExpiresUtc { get; set; }
    public string? PreviousCredentialHash { get; set; }
    public DateTimeOffset? PreviousCredentialExpiresUtc { get; set; }
    public DateTimeOffset? CredentialExpiryNotifiedForUtc { get; set; }

    public bool CanStartProfile(Guid profileId) => ServerPermissionOverrides?
        .FirstOrDefault(item => item.ProfileId == profileId)?.CanStart ?? CanStart;
    public bool CanStopProfile(Guid profileId) => ServerPermissionOverrides?
        .FirstOrDefault(item => item.ProfileId == profileId)?.CanStop ?? CanStop;
    public bool CanExtendTimerForProfile(Guid profileId) => ServerPermissionOverrides?
        .FirstOrDefault(item => item.ProfileId == profileId)?.CanExtendTimer ?? CanExtendTimer;
}

public sealed class ServerPermissionOverride
{
    public Guid ProfileId { get; set; }
    public bool CanStart { get; set; }
    public bool CanStop { get; set; }
    public bool CanExtendTimer { get; set; }
}

public sealed class ServerInviteState
{
    public Guid ProfileId { get; set; }
    public Guid Generation { get; set; }
    public string Code { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public bool CanStart { get; set; }
    public bool CanStop { get; set; }
    public bool Rotated { get; set; }
    public DateTimeOffset? PairingOpenedUtc { get; set; }
    public DateTimeOffset? PairingExpiresUtc { get; set; }
    public int DurationMinutes { get; set; }
    public int DeviceLimit { get; set; }
    public int ActivatedDevices { get; set; }
    public bool RequireApproval { get; set; }
    public bool Closed { get; set; }
}

public sealed record DeviceView(Guid Id, Guid ProfileId, IReadOnlyList<Guid> AssignedProfileIds,
    string Name, bool CanStart, bool CanStop, bool Revoked,
    bool Paired, DateTimeOffset? CredentialExpiresUtc, DateTimeOffset? LastHeartbeatUtc,
    IReadOnlyList<ServerPermissionView>? ServerPermissions = null,
    string? AppVersion = null, int? ProtocolVersion = null,
    bool ApprovalPending = false, bool CanExtendTimer = false);
public sealed record ServerPermissionView(Guid ProfileId, bool CanStart, bool CanStop,
    bool CanExtendTimer = false);

public sealed record PairingInvite(string Endpoint, string Fingerprint, Guid DeviceId, string Code, DateTimeOffset ExpiresUtc,
    bool ServerScope = false);
public sealed record ServerInviteView(PairingInvite Invitation, bool CanStart,
    bool Open = true, int DeviceLimit = 1, int ActivatedDevices = 0,
    bool RequireApproval = false, DateTimeOffset? OpenedUtc = null,
    int DurationMinutes = 30);
public sealed record PairingActivation(Guid DeviceId, string Code, bool ServerScope = false);
public sealed record PairingCredential(Guid DeviceId, string Credential, DateTimeOffset ExpiresUtc,
    bool ApprovalPending = false);
public sealed record HeartbeatRequest(Guid DeviceId, Guid InstanceId, long Sequence, string Version,
    int ProtocolVersion = 1, IReadOnlyList<string>? Capabilities = null);
public sealed record HeartbeatReceipt(Guid InstanceId, long Sequence, DateTimeOffset ReceivedUtc,
    string AppVersion = "", int ProtocolVersion = 1);
public sealed record PairingDecision(bool Ok, string Code, string Message);
public sealed record ServerInviteRequest(bool Refresh, bool CanStart, bool EnableConnections = false,
    int DurationMinutes = 30, int DeviceLimit = 1, bool RequireApproval = false);
public sealed record DevicePermissionRequest(bool CanStart, bool CanStop, string? Scope = null,
    bool CanExtendTimer = false);
public sealed record DeviceServerPermissionRequest(Guid ProfileId, bool CanStart, bool CanStop,
    bool CanExtendTimer = false);
public sealed record DeviceServerAccessRequest(IReadOnlyList<Guid>? ProfileIds,
    IReadOnlyList<DeviceServerPermissionRequest>? Permissions = null);
public sealed record DeviceNameRequest(string? Name);
public sealed record FriendPairRequest(string Invitation, string? HostAddress = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteActionRequest(Guid DeviceId, Guid ProfileId);

// A TS3 server code carries the Host address, server profile, shared pairing secret,
// and full TLS pin. TS1/TS2 per-device invites remain readable until they expire.
public static class PairingPassword
{
    private const string ServerPrefix = "TS3-";
    private const string Prefix = "TS2-";
    private const string LegacyPrefix = "TS1-";
    private const int PayloadLength = 1 + 4 + 2 + 16 + 32 + 32 + 8;
    private const int LegacyPayloadLength = 1 + 16 + 32 + 32 + 8;

    public static string Encode(PairingInvite invite)
    {
        var fingerprint = Convert.FromHexString(invite.Fingerprint);
        var secret = Convert.FromBase64String(invite.Code);
        if (!HostIdentity.TryEndpoint(invite.Endpoint, out var endpoint) ||
            !IPAddress.TryParse(endpoint.Host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            fingerprint.Length != 32 || secret.Length != 32 || invite.DeviceId == Guid.Empty)
            throw new ArgumentException("Pairing invite fields are invalid.");
        Span<byte> bytes = stackalloc byte[PayloadLength];
        bytes[0] = invite.ServerScope ? (byte)3 : (byte)2;
        address.GetAddressBytes().CopyTo(bytes[1..5]);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[5..7], checked((ushort)endpoint.Port));
        invite.DeviceId.TryWriteBytes(bytes[7..23]);
        fingerprint.CopyTo(bytes[23..55]);
        secret.CopyTo(bytes[55..87]);
        BinaryPrimitives.WriteInt64BigEndian(bytes[87..], invite.ExpiresUtc.ToUnixTimeSeconds());
        return (invite.ServerScope ? ServerPrefix : Prefix) + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? password, string? hostAddress, out PairingInvite? invite)
    {
        invite = null;
        var value = password?.Trim();
        if (value is null || value.Length > 200) return false;
        var legacy = value.StartsWith(LegacyPrefix, StringComparison.Ordinal);
        var serverScope = value.StartsWith(ServerPrefix, StringComparison.Ordinal);
        if (!legacy && !serverScope && !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var encoded = value[(legacy ? LegacyPrefix.Length : Prefix.Length)..].Replace('-', '+').Replace('_', '/');
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')); }
        catch (FormatException) { return false; }
        if (bytes.Length != (legacy ? LegacyPayloadLength : PayloadLength) ||
            bytes[0] != (legacy ? 1 : serverScope ? 3 : 2)) return false;
        string endpoint;
        var offset = 1;
        if (legacy)
        {
            if (!TryEnteredEndpoint(hostAddress, out endpoint)) return false;
        }
        else
        {
            var port = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(5, 2));
            if (port < 1024) return false;
            endpoint = $"https://{new IPAddress(bytes.AsSpan(1, 4))}:{port}";
            offset = 7;
            if (!string.IsNullOrWhiteSpace(hostAddress) &&
                (!TryEnteredEndpoint(hostAddress, out var entered) ||
                 !string.Equals(entered, endpoint, StringComparison.OrdinalIgnoreCase))) return false;
        }
        var id = new Guid(bytes.AsSpan(offset, 16));
        DateTimeOffset expires;
        try { expires = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(offset + 80, 8))); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (id == Guid.Empty || expires <= DateTimeOffset.UtcNow) return false;
        invite = new PairingInvite(endpoint, Convert.ToHexString(bytes.AsSpan(offset + 16, 32)), id,
            Convert.ToBase64String(bytes.AsSpan(offset + 48, 32)), expires, serverScope);
        return true;
    }

    private static bool TryEnteredEndpoint(string? address, out string endpoint)
    {
        if (HostIdentity.TryAddress(address, out endpoint)) return true;
        if (HostIdentity.TryEndpoint(address ?? "", out var uri) &&
            uri.HostNameType == UriHostNameType.IPv4)
        {
            endpoint = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }
        endpoint = "";
        return false;
    }
}

public sealed class HostIdentity(LocalData data)
{
    private const string FileName = "host-certificate.protected";
    private const string NextFileName = "host-certificate-next.protected";
    private const string MetadataFileName = "host-identity.json";

    public static bool TryAddress(string? address, out string endpoint)
    {
        endpoint = "";
        var value = address?.Trim();
        if (string.IsNullOrEmpty(value) || value.Contains('/') || value.Contains(' ')) return false;
        var separator = value.LastIndexOf(':');
        var ipText = separator < 0 ? value : value[..separator];
        var port = 5131;
        if (separator >= 0 && !int.TryParse(value[(separator + 1)..], out port)) return false;
        if (port is < 1024 or > 65535 || !IPAddress.TryParse(ipText, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        endpoint = $"https://{ip}:{port}";
        return true;
    }

    public X509Certificate2? Load()
    {
        return Load(FileName);
    }

    public X509Certificate2 Ensure(string endpoint)
    {
        if (!TryEndpoint(endpoint, out _)) throw new ArgumentException("Enter an HTTPS endpoint with an IP address and port.");
        var existing = Load();
        if (existing is not null)
        {
            if (!existing.HasPrivateKey || DateTimeOffset.UtcNow < existing.NotBefore.ToUniversalTime() ||
                DateTimeOffset.UtcNow > existing.NotAfter.ToUniversalTime())
                throw new InvalidOperationException("Host TLS identity is not currently usable. Renew it before creating invites.");
            _ = Metadata();
            data.SaveIdentityEndpoint(endpoint); // Last advertised endpoint; never an identity trust anchor.
            return existing;
        }
        using var certificate = Create(endpoint, Metadata().HostId);
        data.SaveProtected(FileName, certificate.Export(X509ContentType.Pkcs12));
        data.SaveIdentityEndpoint(endpoint);
        return Load()!;
    }

    public HostCertificateState? State()
    {
        using var current = Load();
        if (current is null) return null;
        using var next = Load(NextFileName);
        var metadata = Metadata();
        if (metadata.PreviousAcceptedUntilUtc <= DateTimeOffset.UtcNow && metadata.PreviousFingerprint is not null)
        {
            metadata.PreviousFingerprint = null;
            metadata.PreviousAcceptedUntilUtc = null;
            data.SaveState(MetadataFileName, metadata);
            return new(metadata.HostId, Fingerprint(current), current.NotAfter.ToUniversalTime(),
                next is null ? null : Fingerprint(next), next?.NotAfter.ToUniversalTime(), null, null);
        }
        return new(metadata.HostId, Fingerprint(current), current.NotAfter.ToUniversalTime(),
            next is null ? null : Fingerprint(next), next?.NotAfter.ToUniversalTime(),
            metadata.PreviousFingerprint, metadata.PreviousAcceptedUntilUtc);
    }

    public HostCertificateState StageNext(string endpoint, bool force = false)
    {
        using var current = Ensure(endpoint);
        using var existingNext = Load(NextFileName);
        if (existingNext is null || force)
        {
            using var generated = Create(endpoint, Metadata().HostId);
            data.SaveProtected(NextFileName, generated.Export(X509ContentType.Pkcs12));
        }
        return State()!;
    }

    public HostCertificateState StageNextIfExpiring(string endpoint, TimeSpan warning)
    {
        using var current = Ensure(endpoint);
        return current.NotAfter.ToUniversalTime() - DateTimeOffset.UtcNow <= warning
            ? StageNext(endpoint)
            : State()!;
    }

    public HostCertificateState ActivateNext()
    {
        using var current = Load() ?? throw new InvalidOperationException("The current Host certificate is unavailable.");
        using var next = Load(NextFileName) ?? throw new InvalidOperationException("Stage the next Host certificate first.");
        if (DateTimeOffset.UtcNow < next.NotBefore.ToUniversalTime() || DateTimeOffset.UtcNow > next.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("The staged Host certificate is not currently usable.");
        var nextBytes = data.LoadProtected(NextFileName)
            ?? throw new InvalidOperationException("The staged Host certificate is unavailable.");
        data.SaveProtected(FileName, nextBytes);
        data.DeleteProtected(NextFileName);
        var metadata = Metadata();
        metadata.PreviousFingerprint = Fingerprint(current);
        metadata.PreviousAcceptedUntilUtc = DateTimeOffset.UtcNow.AddDays(14);
        data.SaveState(MetadataFileName, metadata);
        return State()!;
    }

    public HostCertificateState RetirePrevious()
    {
        var metadata = Metadata();
        metadata.PreviousFingerprint = null;
        metadata.PreviousAcceptedUntilUtc = null;
        data.SaveState(MetadataFileName, metadata);
        return State() ?? throw new InvalidOperationException("The current Host certificate is unavailable.");
    }

    private X509Certificate2? Load(string fileName)
    {
        var bytes = data.LoadProtected(fileName);
        // Windows Schannel cannot serve TLS with an ephemeral imported private key.
        return bytes is null ? null : X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.UserKeySet);
    }

    private HostIdentityMetadata Metadata()
    {
        var metadata = data.LoadState<HostIdentityMetadata?>(MetadataFileName, null);
        if (metadata is not null && metadata.HostId != Guid.Empty) return metadata;
        metadata ??= new HostIdentityMetadata();
        metadata.HostId = Guid.NewGuid();
        data.SaveState(MetadataFileName, metadata);
        return metadata;
    }

    private static X509Certificate2 Create(string endpoint, Guid hostId)
    {
        if (!TryEndpoint(endpoint, out var uri)) throw new ArgumentException("Enter an HTTPS endpoint with an IP address and port.");
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest($"CN=TogetherServer Host {hostId:N}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse(uri.Host));
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public static bool TryEndpoint(string endpoint, out Uri uri)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps &&
            IPAddress.TryParse(parsed.Host, out _) &&
            parsed.AbsolutePath == "/" && string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment) &&
            string.IsNullOrEmpty(parsed.UserInfo))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }
}

public sealed class PairingService
{
    private readonly LocalData data;
    private readonly object sync = new();
    private readonly List<PairedDevice> devices;
    private readonly List<ServerInviteState> serverInvites;
    private readonly ConcurrentDictionary<Guid, HeartbeatReceipt> heartbeats = new();

    public PairingService(LocalData data)
    {
        this.data = data;
        devices = data.LoadDevices();
        serverInvites = data.LoadServerInvites();
        if (NormalizeAssignments()) data.SaveDevices(devices);
        if (NormalizePairingWindows()) data.SaveServerInvites(serverInvites);
    }

    private bool NormalizePairingWindows()
    {
        var changed = false;
        var migrationExpiry = DateTimeOffset.UtcNow.AddMinutes(30);
        foreach (var invite in serverInvites)
        {
            if (invite.DurationMinutes is < 5 or > 1440)
            {
                invite.DurationMinutes = 30;
                changed = true;
            }
            if (invite.DeviceLimit < 1)
            {
                invite.DeviceLimit = 1;
                changed = true;
            }
            if (invite.PairingOpenedUtc is null)
            {
                // Pre-window TS3 codes get one bounded migration window. They
                // remain decodable, but never regain indefinite pairing.
                invite.PairingOpenedUtc = DateTimeOffset.UtcNow;
                invite.PairingExpiresUtc = migrationExpiry;
                invite.ActivatedDevices = 0;
                invite.Closed = false;
                changed = true;
            }
            if (invite.PairingExpiresUtc is null)
            {
                invite.PairingExpiresUtc = migrationExpiry;
                changed = true;
            }
        }
        return changed;
    }

    private bool NormalizeAssignments()
    {
        var changed = false;
        foreach (var device in devices)
        {
            if (device.AssignedProfileIds is null)
            {
                // Existing TS3 credentials were already scoped by ProfileId.
                // TS1/TS2 credentials have no trustworthy server scope and are
                // deliberately migrated with no access until the owner assigns it.
                device.AssignedProfileIds = device.ProfileId == Guid.Empty ? [] : [device.ProfileId];
                changed = true;
            }
            var normalized = device.AssignedProfileIds.Where(id => id != Guid.Empty).Distinct().ToList();
            if (!normalized.SequenceEqual(device.AssignedProfileIds))
            {
                device.AssignedProfileIds = normalized;
                changed = true;
            }
            if (device.ServerPermissionOverrides is null)
            {
                device.ServerPermissionOverrides = [];
                changed = true;
            }
            var normalizedPermissions = device.ServerPermissionOverrides
                .Where(item => item.ProfileId != Guid.Empty && normalized.Contains(item.ProfileId))
                .GroupBy(item => item.ProfileId)
                // Corrupt duplicate entries collapse to the most restrictive
                // effective permission so normalization never expands access.
                .Select(group => new ServerPermissionOverride
                {
                    ProfileId = group.Key,
                    CanStart = group.All(item => item.CanStart),
                    CanStop = group.All(item => item.CanStop),
                    CanExtendTimer = group.All(item => item.CanExtendTimer)
                })
                .Where(item => item.CanStart != device.CanStart || item.CanStop != device.CanStop ||
                    item.CanExtendTimer != device.CanExtendTimer)
                .ToList();
            if (normalizedPermissions.Count != device.ServerPermissionOverrides.Count ||
                normalizedPermissions.Where((item, index) =>
                    index >= device.ServerPermissionOverrides.Count ||
                    item.ProfileId != device.ServerPermissionOverrides[index].ProfileId ||
                    item.CanStart != device.ServerPermissionOverrides[index].CanStart ||
                    item.CanStop != device.ServerPermissionOverrides[index].CanStop ||
                    item.CanExtendTimer != device.ServerPermissionOverrides[index].CanExtendTimer).Any())
            {
                device.ServerPermissionOverrides = normalizedPermissions;
                changed = true;
            }
        }
        return changed;
    }

    private bool IsRevoked(PairedDevice device) => device.Revoked ||
        (device.ProfileId == Guid.Empty
            ? serverInvites.Any(invite => invite.Rotated)
            : serverInvites.SingleOrDefault(invite => invite.ProfileId == device.ProfileId)?.Generation != device.InviteGeneration);

    public ServerInviteView? CurrentServerInvite(Guid profileId, string? endpoint = null, string? fingerprint = null)
    {
        lock (sync)
        {
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            if (state is null) return null;
            if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(fingerprint) &&
                (!string.Equals(state.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal)))
            {
                // A copied invite must advertise the Host's current route and active
                // pin, but refreshing transport metadata must not reopen, extend, or
                // otherwise change the bounded pairing window.
                state.Endpoint = endpoint;
                state.Fingerprint = fingerprint;
                data.SaveServerInvites(serverInvites);
            }
            var expires = state.PairingExpiresUtc ?? DateTimeOffset.UtcNow;
            var open = !state.Closed && expires > DateTimeOffset.UtcNow &&
                state.ActivatedDevices < state.DeviceLimit;
            return new(new PairingInvite(state.Endpoint, state.Fingerprint, profileId,
                state.Code, expires, true), state.CanStart, open, state.DeviceLimit,
                state.ActivatedDevices, state.RequireApproval, state.PairingOpenedUtc,
                state.DurationMinutes);
        }
    }

    public void ReconcileProfiles(IEnumerable<Guid> profileIds)
    {
        var known = profileIds.ToHashSet();
        lock (sync)
        {
            var removed = serverInvites.RemoveAll(invite => !known.Contains(invite.ProfileId));
            var changedDevices = false;
            foreach (var device in devices)
            {
                var assigned = device.AssignedProfileIds!;
                var validAssignments = assigned.Where(known.Contains).ToList();
                if (!validAssignments.SequenceEqual(assigned))
                {
                    device.AssignedProfileIds = validAssignments;
                    device.ServerPermissionOverrides = device.ServerPermissionOverrides!
                        .Where(permission => validAssignments.Contains(permission.ProfileId)).ToList();
                    changedDevices = true;
                }
                if (device.ProfileId == Guid.Empty || known.Contains(device.ProfileId) || device.Revoked) continue;
                device.Revoked = true;
                heartbeats.TryRemove(device.Id, out _);
                changedDevices = true;
            }
            if (removed > 0) data.SaveServerInvites(serverInvites);
            if (changedDevices) data.SaveDevices(devices);
        }
    }

    public PairingInvite IssueServer(Guid profileId, bool canStart, bool canStop, string endpoint,
        string fingerprint, bool refresh, int durationMinutes = 30, int deviceLimit = 1,
        bool requireApproval = false)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Choose a saved server.");
        if (durationMinutes is < 5 or > 1440)
            throw new ArgumentException("Pairing duration must be between 5 minutes and 24 hours.");
        if (deviceLimit is < 1 or > 25)
            throw new ArgumentException("Pairing device limit must be between 1 and 25 PCs.");
        lock (sync)
        {
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            var now = DateTimeOffset.UtcNow;
            if (state is not null && !refresh)
            {
                var currentWindow = !state.Closed && state.PairingExpiresUtc > now &&
                    state.ActivatedDevices < state.DeviceLimit && state.DeviceLimit == deviceLimit &&
                    state.DurationMinutes == durationMinutes &&
                    state.RequireApproval == requireApproval && state.CanStart == canStart && state.CanStop == canStop;
                if (currentWindow)
                {
                    if (!string.Equals(state.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        state.Endpoint = endpoint;
                        state.Fingerprint = fingerprint;
                        data.SaveServerInvites(serverInvites);
                    }
                    return new(state.Endpoint, state.Fingerprint, profileId, state.Code,
                        state.PairingExpiresUtc!.Value, true);
                }
                state.Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                state.Endpoint = endpoint;
                state.Fingerprint = fingerprint;
                state.CanStart = canStart;
                state.CanStop = canStop;
                state.PairingOpenedUtc = now;
                state.PairingExpiresUtc = now.AddMinutes(durationMinutes);
                state.DurationMinutes = durationMinutes;
                state.DeviceLimit = deviceLimit;
                state.ActivatedDevices = 0;
                state.RequireApproval = requireApproval;
                state.Closed = false;
                data.SaveServerInvites(serverInvites);
                data.Audit($"pairing-window-open {profileId} limit={deviceLimit} minutes={durationMinutes} approval={requireApproval} {now:O}");
                Activity("Pairing", "WindowOpened", $"Pairing opened for up to {deviceLimit} PC{(deviceLimit == 1 ? "" : "s")} for {durationMinutes} minutes.",
                    ActivitySeverity.Important, profileId);
                return new(state.Endpoint, state.Fingerprint, profileId, state.Code,
                    state.PairingExpiresUtc.Value, true);
            }
            var next = new ServerInviteState
            {
                ProfileId = profileId, Generation = Guid.NewGuid(),
                Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Endpoint = endpoint, Fingerprint = fingerprint,
                CanStart = canStart, CanStop = canStop,
                Rotated = refresh,
                PairingOpenedUtc = now,
                PairingExpiresUtc = now.AddMinutes(durationMinutes),
                DurationMinutes = durationMinutes,
                DeviceLimit = deviceLimit,
                ActivatedDevices = 0,
                RequireApproval = requireApproval,
                Closed = false
            };
            if (state is not null) serverInvites.Remove(state);
            serverInvites.Add(next);
            // Generation is the authorization gate. Persist it before updating device views,
            // so even an interrupted rotation cannot leave an old credential usable.
            data.SaveServerInvites(serverInvites);
            if (refresh)
            {
                foreach (var device in devices.Where(device => device.ProfileId == profileId))
                {
                    device.Revoked = true;
                    device.InviteHash = null;
                    device.InviteExpiresUtc = null;
                    heartbeats.TryRemove(device.Id, out _);
                }
                data.SaveDevices(devices);
            }
            data.Audit($"pairing-window {(refresh ? "replace" : "create")} {profileId} limit={deviceLimit} minutes={durationMinutes} approval={requireApproval} {now:O}");
            Activity("Pairing", refresh ? "WindowReplaced" : "WindowOpened",
                refresh ? "Earlier credentials were revoked and a new pairing window opened."
                    : $"Pairing opened for up to {deviceLimit} PC{(deviceLimit == 1 ? "" : "s")} for {durationMinutes} minutes.",
                ActivitySeverity.Important, profileId);
            return new(endpoint, fingerprint, profileId, next.Code, next.PairingExpiresUtc.Value, true);
        }
    }

    public PairingDecision ClosePairing(Guid profileId)
    {
        lock (sync)
        {
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            if (state is null) return new(false, "UnknownPairingWindow", "This server has no pairing window.");
            state.Closed = true;
            state.PairingExpiresUtc = DateTimeOffset.UtcNow;
            state.Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            data.SaveServerInvites(serverInvites);
            data.Audit($"pairing-window-close {profileId} {DateTimeOffset.UtcNow:O}");
            Activity("Pairing", "WindowClosed", "Pairing closed; already paired PCs kept their access.",
                ActivitySeverity.Important, profileId);
            return new(true, "PairingClosed", "Pairing is closed. Already paired PCs keep their current access.");
        }
    }

    public PairingDecision EmergencyRevoke(Guid profileId)
    {
        lock (sync)
        {
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            if (state is null) return new(false, "UnknownPairingWindow", "This server has no pairing history.");
            var previousGeneration = state.Generation;
            state.Generation = Guid.NewGuid();
            state.Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            state.Closed = true;
            state.PairingExpiresUtc = DateTimeOffset.UtcNow;
            state.ActivatedDevices = 0;
            state.Rotated = true;
            var revoked = 0;
            foreach (var device in devices.Where(device => device.ProfileId == profileId &&
                device.InviteGeneration == previousGeneration && !device.Revoked))
            {
                device.Revoked = true;
                device.InviteHash = null;
                device.InviteExpiresUtc = null;
                device.PreviousCredentialHash = null;
                device.PreviousCredentialExpiresUtc = null;
                data.DeleteCredentialRenewalReceipt(device.Id);
                heartbeats.TryRemove(device.Id, out _);
                revoked++;
            }
            data.SaveServerInvites(serverInvites);
            if (revoked > 0) data.SaveDevices(devices);
            data.Audit($"pairing-emergency-revoke {profileId} devices={revoked} {DateTimeOffset.UtcNow:O}");
            Activity("Pairing", "EmergencyRevoke",
                $"Pairing closed and {revoked} issued PC credential{(revoked == 1 ? " was" : "s were")} revoked.",
                ActivitySeverity.Warning, profileId);
            return new(true, "PairingCredentialsRevoked",
                $"Pairing was closed and {revoked} PC credential{(revoked == 1 ? " was" : "s were")} revoked.");
        }
    }

    public IReadOnlyList<DeviceView> Views()
    {
        lock (sync)
        {
            var expiryNoticesChanged = false;
            foreach (var device in devices.Where(item => !IsRevoked(item) && item.CredentialExpiresUtc is { } expiry &&
                expiry > DateTimeOffset.UtcNow && expiry - DateTimeOffset.UtcNow <= TimeSpan.FromDays(14) &&
                item.CredentialExpiryNotifiedForUtc != expiry))
            {
                device.CredentialExpiryNotifiedForUtc = device.CredentialExpiresUtc;
                expiryNoticesChanged = true;
                Activity("Access", "CredentialExpiring", "A paired PC credential expires within 14 days.",
                    ActivitySeverity.Warning, device.ProfileId == Guid.Empty ? null : device.ProfileId, device.Id);
                Activity("Access", "CredentialExpiring", "This PC's Host credential expires within 14 days and will be renewed while connected.",
                    ActivitySeverity.Warning, device.ProfileId == Guid.Empty ? null : device.ProfileId,
                    device.Id, ActivityVisibility.Device);
            }
            if (expiryNoticesChanged) data.SaveDevices(devices);
            return devices.Select(device =>
            {
            heartbeats.TryGetValue(device.Id, out var heartbeat);
            var fresh = heartbeat is not null && DateTimeOffset.UtcNow - heartbeat.ReceivedUtc <= TimeSpan.FromSeconds(45);
            return new DeviceView(device.Id, device.ProfileId, device.AssignedProfileIds!.ToArray(),
                device.Name, device.CanStart, device.CanStop, IsRevoked(device),
                device.CredentialHash is not null, device.CredentialExpiresUtc,
                fresh ? heartbeat!.ReceivedUtc : null,
                device.AssignedProfileIds.Select(profileId => new ServerPermissionView(profileId,
                    device.CanStartProfile(profileId), device.CanStopProfile(profileId),
                    device.CanExtendTimerForProfile(profileId))).ToList(),
                fresh ? heartbeat!.AppVersion : null, fresh ? heartbeat!.ProtocolVersion : null,
                device.ApprovalPending, device.CanExtendTimer);
            }).ToList();
        }
    }

    public bool HasInviteOrCredential()
    {
        lock (sync) return serverInvites.Any(invite => !invite.Closed &&
                invite.PairingExpiresUtc > DateTimeOffset.UtcNow && invite.ActivatedDevices < invite.DeviceLimit) ||
            devices.Any(d => !IsRevoked(d) && (d.InviteHash is not null || d.CredentialHash is not null));
    }

    public PairingInvite Issue(string name, bool canStart, bool canStop, string endpoint, string fingerprint, Guid? rotatingId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("Enter a device name up to 80 characters.");
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        lock (sync)
        {
            PairedDevice device;
            if (rotatingId is { } id)
            {
                device = devices.SingleOrDefault(d => d.Id == id && !d.Revoked)
                    ?? throw new ArgumentException("Device is unavailable for rotation.");
                device.Name = name;
                device.CanStart = canStart;
                device.CanStop = canStop;
            }
            else
            {
                var newDeviceId = Guid.NewGuid();
                device = new PairedDevice { Id = newDeviceId,
                    AssignedProfileIds = [],
                    ServerPermissionOverrides = [],
                    Name = name == "Friend PC" ? $"Friend PC {newDeviceId.ToString("N")[..6]}" : name,
                    CanStart = canStart, CanStop = canStop };
                devices.Add(device);
            }
            device.InviteHash = Hash(code);
            device.InviteExpiresUtc = expires;
            data.SaveDevices(devices);
            data.Audit($"invite {device.Id} {DateTimeOffset.UtcNow:O}");
            return new PairingInvite(endpoint, fingerprint, device.Id, code, expires);
        }
    }

    public PairingCredential? Activate(PairingActivation request)
    {
        if (request.Code is null || request.Code.Length > 128) return null;
        lock (sync)
        {
            if (request.ServerScope)
            {
                var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == request.DeviceId);
                if (state is null || state.Closed || state.PairingExpiresUtc <= DateTimeOffset.UtcNow ||
                    state.DeviceLimit < 1 || state.ActivatedDevices >= state.DeviceLimit ||
                    !Matches(request.Code, Hash(state.Code))) return null;
                var id = Guid.NewGuid();
                var serverToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var expires = DateTimeOffset.UtcNow.AddDays(90);
                devices.Add(new PairedDevice
                {
                    Id = id, ProfileId = state.ProfileId, InviteGeneration = state.Generation,
                    AssignedProfileIds = [state.ProfileId],
                    ServerPermissionOverrides = [],
                    Name = $"Friend PC {id.ToString("N")[..6]}", CanStart = state.CanStart,
                    CanStop = state.CanStop, CredentialHash = Hash(serverToken), CredentialExpiresUtc = expires,
                    ApprovalPending = state.RequireApproval
                });
                state.ActivatedDevices++;
                if (state.ActivatedDevices >= state.DeviceLimit) state.Closed = true;
                data.SaveServerInvites(serverInvites);
                data.SaveDevices(devices);
                data.Audit($"activate {id} {state.ProfileId} approval-pending={state.RequireApproval} {DateTimeOffset.UtcNow:O}");
                Activity("Pairing", state.RequireApproval ? "DeviceAwaitingApproval" : "DevicePaired",
                    state.RequireApproval ? "A new PC paired and is waiting for local approval." : "A new PC paired successfully.",
                    ActivitySeverity.Important, state.ProfileId, id);
                Activity("Pairing", state.RequireApproval ? "ApprovalPending" : "Paired",
                    state.RequireApproval ? "This PC is waiting for local Host approval." : "This PC paired successfully.",
                    ActivitySeverity.Important, state.ProfileId, id, ActivityVisibility.Device);
                return new PairingCredential(id, serverToken, expires, state.RequireApproval);
            }
            var device = devices.SingleOrDefault(d => d.Id == request.DeviceId);
            if (device is null || IsRevoked(device) || device.InviteHash is null ||
                device.InviteExpiresUtc <= DateTimeOffset.UtcNow || !Matches(request.Code, device.InviteHash)) return null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            device.CredentialHash = Hash(token);
            device.CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90);
            device.CredentialExpiryNotifiedForUtc = null;
            device.PreviousCredentialHash = null;
            device.PreviousCredentialExpiresUtc = null;
            data.DeleteCredentialRenewalReceipt(device.Id);
            device.InviteHash = null;
            device.InviteExpiresUtc = null;
            heartbeats.TryRemove(device.Id, out _);
            data.SaveDevices(devices);
            data.Audit($"activate {device.Id} {DateTimeOffset.UtcNow:O}");
            return new PairingCredential(device.Id, token, device.CredentialExpiresUtc.Value);
        }
    }

    public PairingDecision Authenticate(Guid id, string? bearer, out PairedDevice? device)
    {
        lock (sync)
        {
            device = devices.SingleOrDefault(d => d.Id == id);
            var current = device?.CredentialHash is not null && bearer is not null && bearer.Length <= 128 &&
                Matches(bearer, device.CredentialHash);
            var previous = device?.PreviousCredentialHash is not null && bearer is not null && bearer.Length <= 128 &&
                device.PreviousCredentialExpiresUtc > DateTimeOffset.UtcNow && Matches(bearer, device.PreviousCredentialHash);
            if (device is null || !current && !previous)
            {
                device = null;
                return new PairingDecision(false, "Unauthorized", "Device credential was not accepted.");
            }
            if (IsRevoked(device)) return new PairingDecision(false, "Revoked", "This server's invite was refreshed or this device was revoked.");
            if (device.ApprovalPending)
                return new PairingDecision(false, "ApprovalPending", "The Host must approve this PC locally before it can connect.");
            if (device.CredentialExpiresUtc <= DateTimeOffset.UtcNow)
                return new PairingDecision(false, "Expired", "This device credential has expired.");
            return new PairingDecision(true, "Authenticated", "Device authenticated.");
        }
    }

    public CredentialRenewal? Renew(PairedDevice authenticatedDevice, CredentialRenewalRequest request)
    {
        if (request.DeviceId != authenticatedDevice.Id || request.RequestId == Guid.Empty) return null;
        lock (sync)
        {
            var device = devices.SingleOrDefault(item => item.Id == authenticatedDevice.Id);
            if (device is null || IsRevoked(device) || device.CredentialHash is null ||
                device.CredentialExpiresUtc <= DateTimeOffset.UtcNow) return null;
            var existing = data.LoadCredentialRenewalReceipt(device.Id);
            if (existing is not null && existing.RequestId == request.RequestId &&
                existing.PreviousAcceptedUntilUtc > DateTimeOffset.UtcNow)
                return new(existing.DeviceId, existing.RequestId, existing.Credential,
                    existing.ExpiresUtc, existing.PreviousAcceptedUntilUtc);

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var overlap = DateTimeOffset.UtcNow.AddMinutes(10);
            device.PreviousCredentialHash = device.CredentialHash;
            device.PreviousCredentialExpiresUtc = overlap;
            device.CredentialHash = Hash(token);
            device.CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90);
            var receipt = new CredentialRenewalReceipt
            {
                DeviceId = device.Id, RequestId = request.RequestId, Credential = token,
                ExpiresUtc = device.CredentialExpiresUtc.Value, PreviousAcceptedUntilUtc = overlap
            };
            data.SaveDevices(devices);
            data.SaveCredentialRenewalReceipt(receipt);
            data.Audit($"credential-renew {device.Id} {DateTimeOffset.UtcNow:O}");
            Activity("Access", "CredentialRenewed", "This PC's Host credential was renewed for 90 days.",
                ActivitySeverity.Info, device.ProfileId == Guid.Empty ? null : device.ProfileId,
                device.Id, ActivityVisibility.Device);
            return new(receipt.DeviceId, receipt.RequestId, receipt.Credential,
                receipt.ExpiresUtc, receipt.PreviousAcceptedUntilUtc);
        }
    }

    public PairingDecision RecordHeartbeat(PairedDevice device, HeartbeatRequest request)
    {
        if (request.InstanceId == Guid.Empty || request.Sequence < 1 || request.Version is null || request.Version.Length > 32)
            return new PairingDecision(false, "InvalidHeartbeat", "Heartbeat fields are invalid.");
        lock (sync)
        {
            if (IsRevoked(device)) return new PairingDecision(false, "Revoked", "This server's invite was refreshed or this device was revoked.");
            if (heartbeats.TryGetValue(device.Id, out var prior) && prior.InstanceId == request.InstanceId &&
                request.Sequence <= prior.Sequence)
                return new PairingDecision(false, "Replay", "Heartbeat sequence did not advance.");
            heartbeats[device.Id] = new HeartbeatReceipt(request.InstanceId, request.Sequence,
                DateTimeOffset.UtcNow, request.Version, request.ProtocolVersion);
            return new PairingDecision(true, "Received", "Heartbeat recorded.");
        }
    }

    public PairingDecision Revoke(Guid id)
    {
        lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id);
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Device was not found.");
            device.Revoked = true;
            device.InviteHash = null;
            device.InviteExpiresUtc = null;
            device.PreviousCredentialHash = null;
            device.PreviousCredentialExpiresUtc = null;
            data.DeleteCredentialRenewalReceipt(device.Id);
            heartbeats.TryRemove(id, out _);
            data.SaveDevices(devices);
            data.Audit($"revoke {device.Id} {DateTimeOffset.UtcNow:O}");
            Activity("Access", "DeviceRevoked", "A paired PC was revoked.", ActivitySeverity.Warning,
                device.ProfileId == Guid.Empty ? null : device.ProfileId, device.Id);
            return new PairingDecision(true, "Revoked", "Device revoked.");
        }
    }

    public PairingDecision Approve(Guid id)
    {
        lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d) && d.CredentialHash is not null);
            if (device is null) return new(false, "UnknownDevice", "Paired PC was not found.");
            if (!device.ApprovalPending) return new(true, "AlreadyApproved", "This PC is already approved.");
            device.ApprovalPending = false;
            data.SaveDevices(devices);
            data.Audit($"device-approve {device.Id} {DateTimeOffset.UtcNow:O}");
            Activity("Pairing", "DeviceApproved", "A waiting PC was approved locally.",
                ActivitySeverity.Important, device.ProfileId == Guid.Empty ? null : device.ProfileId, device.Id);
            Activity("Pairing", "DeviceApproved", "The Host owner approved this PC.",
                ActivitySeverity.Important, device.ProfileId == Guid.Empty ? null : device.ProfileId,
                device.Id, ActivityVisibility.Device);
            return new(true, "DeviceApproved", "Friend PC approved. Its next authenticated check can connect.");
        }
    }

    public PairingDecision SetPermissions(Guid id, bool canStart, bool canStop, string? scope = null,
        bool canExtendTimer = false)
    {
        if (scope is not (null or "start" or "stop" or "extend"))
            return new PairingDecision(false, "InvalidPermissionScope", "Choose Start, Stop, or Extend timer permissions.");
        lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d) && d.CredentialHash is not null);
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Pair this Friend PC first.");
            var effective = device.AssignedProfileIds!.ToDictionary(profileId => profileId, profileId =>
                (CanStart: device.CanStartProfile(profileId), CanStop: device.CanStopProfile(profileId),
                    CanExtendTimer: device.CanExtendTimerForProfile(profileId)));
            if (scope is null or "start") device.CanStart = canStart;
            if (scope is null or "stop") device.CanStop = canStop;
            if (scope is null or "extend") device.CanExtendTimer = canExtendTimer;
            device.ServerPermissionOverrides = device.AssignedProfileIds!.Select(profileId =>
            {
                var previous = effective[profileId];
                return new ServerPermissionOverride
                {
                    ProfileId = profileId,
                    CanStart = scope is null or "start" ? device.CanStart : previous.CanStart,
                    CanStop = scope is null or "stop" ? device.CanStop : previous.CanStop,
                    CanExtendTimer = scope is "start" or "stop" ? previous.CanExtendTimer : device.CanExtendTimer
                };
            }).Where(permission => permission.CanStart != device.CanStart || permission.CanStop != device.CanStop ||
                permission.CanExtendTimer != device.CanExtendTimer).ToList();
            data.SaveDevices(devices);
            data.Audit($"permissions-change {device.Id} {DateTimeOffset.UtcNow:O}");
            Activity("Access", "PermissionsChanged", "Permissions changed for a paired PC.",
                ActivitySeverity.Important, device.ProfileId == Guid.Empty ? null : device.ProfileId, device.Id);
            Activity("Access", "PermissionsChanged", "The Host changed this PC's permissions.",
                ActivitySeverity.Important, device.ProfileId == Guid.Empty ? null : device.ProfileId,
                device.Id, ActivityVisibility.Device);
            return new PairingDecision(true, "PermissionsSaved", "Friend permissions changed immediately.");
        }
    }

    public PairingDecision SetServerAccess(Guid id, IReadOnlyList<Guid>? profileIds,
        IReadOnlyList<DeviceServerPermissionRequest>? permissions, IEnumerable<Guid> knownProfileIds)
    {
        if (profileIds is null || profileIds.Any(profileId => profileId == Guid.Empty) ||
            profileIds.Distinct().Count() != profileIds.Count)
            return new PairingDecision(false, "InvalidServerAccess", "Choose each saved server at most once.");
        var known = knownProfileIds.ToHashSet();
        if (profileIds.Any(profileId => !known.Contains(profileId)))
            return new PairingDecision(false, "UnknownServer", "One or more selected servers are no longer saved on this Host.");
        if (permissions is not null && (permissions.Any(permission => permission.ProfileId == Guid.Empty ||
                !profileIds.Contains(permission.ProfileId)) ||
            permissions.Select(permission => permission.ProfileId).Distinct().Count() != permissions.Count))
            return new PairingDecision(false, "InvalidServerPermissions",
                "Save at most one Start and Stop permission for each selected server.");
        lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d) && d.CredentialHash is not null);
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Pair this Friend PC first.");
            device.AssignedProfileIds = profileIds.ToList();
            if (permissions is null)
            {
                device.ServerPermissionOverrides = device.ServerPermissionOverrides!
                    .Where(permission => device.AssignedProfileIds.Contains(permission.ProfileId)).ToList();
            }
            else
            {
                device.ServerPermissionOverrides = permissions
                    .Where(permission => permission.CanStart != device.CanStart || permission.CanStop != device.CanStop ||
                        permission.CanExtendTimer != device.CanExtendTimer)
                    .Select(permission => new ServerPermissionOverride
                    {
                        ProfileId = permission.ProfileId,
                        CanStart = permission.CanStart,
                        CanStop = permission.CanStop,
                        CanExtendTimer = permission.CanExtendTimer
                    }).ToList();
            }
            data.SaveDevices(devices);
            data.Audit($"server-access-change {device.Id} {device.AssignedProfileIds.Count} {DateTimeOffset.UtcNow:O}");
            Activity("Access", "ServerAssignmentsChanged", "Server assignments changed for a paired PC.",
                ActivitySeverity.Important, deviceId: device.Id);
            Activity("Access", "ServerAssignmentsChanged", "The Host changed which servers this PC can access.",
                ActivitySeverity.Important, deviceId: device.Id, visibility: ActivityVisibility.Device);
            return new PairingDecision(true, "ServerAccessSaved", device.AssignedProfileIds.Count == 0
                ? "This Friend PC is not assigned to any servers."
                : $"This Friend PC can now access {device.AssignedProfileIds.Count} server{(device.AssignedProfileIds.Count == 1 ? "" : "s")}.");
        }
    }

    public bool CanAccess(PairedDevice device, Guid profileId)
    {
        lock (sync) return !IsRevoked(device) && device.AssignedProfileIds!.Contains(profileId);
    }

    public bool TryGetActiveDevice(Guid id, out PairedDevice? device)
    {
        lock (sync)
        {
            device = devices.SingleOrDefault(item => item.Id == id && item.CredentialHash is not null &&
                !item.ApprovalPending && !IsRevoked(item) && item.CredentialExpiresUtc > DateTimeOffset.UtcNow);
            return device is not null;
        }
    }

    public bool CanStart(PairedDevice device, Guid profileId)
    {
        lock (sync) return !IsRevoked(device) && device.AssignedProfileIds!.Contains(profileId) &&
            device.CanStartProfile(profileId);
    }

    public bool CanStop(PairedDevice device, Guid profileId)
    {
        lock (sync) return !IsRevoked(device) && device.AssignedProfileIds!.Contains(profileId) &&
            device.CanStopProfile(profileId);
    }

    public bool CanExtendTimer(PairedDevice device, Guid profileId)
    {
        lock (sync) return !IsRevoked(device) && device.AssignedProfileIds!.Contains(profileId) &&
            device.CanExtendTimerForProfile(profileId);
    }

    public PairingDecision SetName(Guid id, string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is < 1 or > 48 || value.Any(char.IsControl))
            return new PairingDecision(false, "InvalidDeviceName", "Use a name between 1 and 48 characters without line breaks.");
        lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d));
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Friend PC was not found.");
            device.Name = value;
            data.SaveDevices(devices);
            data.Audit($"device-name-change {device.Id} {DateTimeOffset.UtcNow:O}");
            Activity("Access", "DeviceRenamed", "A paired PC was renamed locally.", deviceId: device.Id);
            return new PairingDecision(true, "DeviceNameSaved", "Friend PC name saved locally.");
        }
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private void Activity(string category, string action, string message,
        string severity = ActivitySeverity.Info, Guid? profileId = null,
        Guid? deviceId = null, string visibility = ActivityVisibility.Local)
    {
        try { data.RecordActivity(category, action, message, severity, profileId, deviceId, visibility); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { data.Audit($"activity-write-failed {category} {action} {ex.GetType().Name} {DateTimeOffset.UtcNow:O}"); }
    }
    private static bool Matches(string candidate, string expectedHash)
    {
        try { return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), Convert.FromHexString(expectedHash)); }
        catch (FormatException) { return false; }
    }
}
