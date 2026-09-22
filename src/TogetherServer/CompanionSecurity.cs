using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
    public bool Revoked { get; set; }
    public string? InviteHash { get; set; }
    public DateTimeOffset? InviteExpiresUtc { get; set; }
    public string? CredentialHash { get; set; }
    public DateTimeOffset? CredentialExpiresUtc { get; set; }
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
}

public sealed record DeviceView(Guid Id, Guid ProfileId, IReadOnlyList<Guid> AssignedProfileIds,
    string Name, bool CanStart, bool CanStop, bool Revoked,
    bool Paired, DateTimeOffset? CredentialExpiresUtc, DateTimeOffset? LastHeartbeatUtc, bool? GameRunning);

public static class AutoShutdownPresence
{
    public static string? FriendBlocker(Guid profileId, IEnumerable<DeviceView> devices)
    {
        var assigned = devices.Where(device => device.Paired && !device.Revoked &&
            device.AssignedProfileIds.Contains(profileId)).ToList();
        if (assigned.Any(device => device.LastHeartbeatUtc is null))
            return "Waiting for a fresh report from every assigned Friend PC.";
        if (assigned.Any(device => device.GameRunning == true))
            return "An assigned Friend's game is running.";
        if (assigned.Any(device => device.GameRunning is null))
            return "Every assigned Friend PC needs a current game check.";
        return null;
    }
}

public sealed record PairingInvite(string Endpoint, string Fingerprint, Guid DeviceId, string Code, DateTimeOffset ExpiresUtc,
    bool ServerScope = false);
public sealed record ServerInviteView(PairingInvite Invitation, bool CanStart);
public sealed record PairingActivation(Guid DeviceId, string Code, bool ServerScope = false);
public sealed record PairingCredential(Guid DeviceId, string Credential, DateTimeOffset ExpiresUtc);
public sealed record HeartbeatRequest(Guid DeviceId, Guid InstanceId, long Sequence, string Version, bool? GameRunning);
public sealed record HeartbeatReceipt(Guid InstanceId, long Sequence, DateTimeOffset ReceivedUtc, bool? GameRunning);
public sealed record PairingDecision(bool Ok, string Code, string Message);
public sealed record ServerInviteRequest(bool Refresh, bool CanStart, bool EnableConnections = false);
public sealed record DevicePermissionRequest(bool CanStart, bool CanStop);
public sealed record DeviceServerAccessRequest(IReadOnlyList<Guid>? ProfileIds);
public sealed record DeviceNameRequest(string? Name);
public sealed record FriendPairRequest(string Invitation, string ClientExecutablePath, string? HostAddress = null);
public sealed record ClientPathRequest(string Path);
public sealed record FriendClientBrowseRequest(string? Kind);
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
        var bytes = data.LoadProtected(FileName);
        // Windows Schannel cannot serve TLS with an ephemeral imported private key.
        return bytes is null ? null : X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.UserKeySet);
    }

    public X509Certificate2 Ensure(string endpoint)
    {
        var existing = Load();
        if (existing is not null)
        {
            if (!string.Equals(data.LoadIdentityEndpoint(), endpoint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Host identity is pinned to a different endpoint. Rotate it deliberately before changing the endpoint.");
            if (!existing.HasPrivateKey || DateTimeOffset.UtcNow < existing.NotBefore.ToUniversalTime() ||
                DateTimeOffset.UtcNow > existing.NotAfter.ToUniversalTime())
                throw new InvalidOperationException("Host TLS identity is not currently usable. Renew it before creating invites.");
            return existing;
        }
        if (!TryEndpoint(endpoint, out var uri)) throw new ArgumentException("Enter an HTTPS endpoint with an IP address and port.");
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=TogetherServer Host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse(uri.Host));
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        data.SaveProtected(FileName, certificate.Export(X509ContentType.Pkcs12));
        data.SaveIdentityEndpoint(endpoint);
        return Load()!;
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
        }
        return changed;
    }

    private bool IsRevoked(PairedDevice device) => device.Revoked ||
        (device.ProfileId == Guid.Empty
            ? serverInvites.Any(invite => invite.Rotated)
            : serverInvites.SingleOrDefault(invite => invite.ProfileId == device.ProfileId)?.Generation != device.InviteGeneration);

    public ServerInviteView? CurrentServerInvite(Guid profileId)
    {
        lock (sync)
        {
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            return state is null ? null : new(new PairingInvite(state.Endpoint, state.Fingerprint, profileId,
                state.Code, DateTimeOffset.MaxValue, true), state.CanStart);
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
        string fingerprint, bool refresh)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Choose a saved server.");
        lock (sync)
        {
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            if (state is not null && !refresh)
                return new(state.Endpoint, state.Fingerprint, profileId, state.Code, DateTimeOffset.MaxValue, true);
            var next = new ServerInviteState
            {
                ProfileId = profileId, Generation = Guid.NewGuid(),
                Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Endpoint = endpoint, Fingerprint = fingerprint,
                CanStart = canStart, CanStop = canStop,
                Rotated = refresh
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
            data.Audit($"server-invite {(refresh ? "refresh" : "create")} {profileId} {DateTimeOffset.UtcNow:O}");
            return new(endpoint, fingerprint, profileId, next.Code, DateTimeOffset.MaxValue, true);
        }
    }

    public IReadOnlyList<DeviceView> Views()
    {
        lock (sync) return devices.Select(device =>
        {
            heartbeats.TryGetValue(device.Id, out var heartbeat);
            var fresh = heartbeat is not null && DateTimeOffset.UtcNow - heartbeat.ReceivedUtc <= TimeSpan.FromSeconds(45);
            return new DeviceView(device.Id, device.ProfileId, device.AssignedProfileIds!.ToArray(),
                device.Name, device.CanStart, device.CanStop, IsRevoked(device),
                device.CredentialHash is not null, device.CredentialExpiresUtc,
                fresh ? heartbeat!.ReceivedUtc : null, fresh ? heartbeat!.GameRunning : null);
        }).ToList();
    }

    public bool HasInviteOrCredential()
    {
        lock (sync) return serverInvites.Count > 0 ||
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
                if (state is null || !Matches(request.Code, Hash(state.Code))) return null;
                var id = Guid.NewGuid();
                var serverToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var expires = DateTimeOffset.UtcNow.AddDays(90);
                devices.Add(new PairedDevice
                {
                    Id = id, ProfileId = state.ProfileId, InviteGeneration = state.Generation,
                    AssignedProfileIds = [state.ProfileId],
                    Name = $"Friend PC {id.ToString("N")[..6]}", CanStart = state.CanStart,
                    CanStop = state.CanStop, CredentialHash = Hash(serverToken), CredentialExpiresUtc = expires
                });
                data.SaveDevices(devices);
                data.Audit($"activate {id} {state.ProfileId} {DateTimeOffset.UtcNow:O}");
                return new PairingCredential(id, serverToken, expires);
            }
            var device = devices.SingleOrDefault(d => d.Id == request.DeviceId);
            if (device is null || IsRevoked(device) || device.InviteHash is null ||
                device.InviteExpiresUtc <= DateTimeOffset.UtcNow || !Matches(request.Code, device.InviteHash)) return null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            device.CredentialHash = Hash(token);
            device.CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90);
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
            if (device is null || device.CredentialHash is null || bearer is null ||
                bearer.Length > 128 || !Matches(bearer, device.CredentialHash))
            {
                device = null;
                return new PairingDecision(false, "Unauthorized", "Device credential was not accepted.");
            }
            if (IsRevoked(device)) return new PairingDecision(false, "Revoked", "This server's invite was refreshed or this device was revoked.");
            if (device.CredentialExpiresUtc <= DateTimeOffset.UtcNow)
                return new PairingDecision(false, "Expired", "This device credential has expired.");
            return new PairingDecision(true, "Authenticated", "Device authenticated.");
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
            heartbeats[device.Id] = new HeartbeatReceipt(request.InstanceId, request.Sequence, DateTimeOffset.UtcNow, request.GameRunning);
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
            heartbeats.TryRemove(id, out _);
            data.SaveDevices(devices);
            data.Audit($"revoke {device.Id} {DateTimeOffset.UtcNow:O}");
            return new PairingDecision(true, "Revoked", "Device revoked.");
        }
    }

    public PairingDecision SetPermissions(Guid id, bool canStart, bool canStop)
    {
        lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d) && d.CredentialHash is not null);
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Pair this Friend PC first.");
            device.CanStart = canStart;
            device.CanStop = canStop;
            data.SaveDevices(devices);
            data.Audit($"permissions-change {device.Id} {DateTimeOffset.UtcNow:O}");
            return new PairingDecision(true, "PermissionsSaved", "Friend permissions changed immediately.");
        }
    }

    public PairingDecision SetServerAccess(Guid id, IReadOnlyList<Guid>? profileIds, IEnumerable<Guid> knownProfileIds)
    {
        if (profileIds is null || profileIds.Any(profileId => profileId == Guid.Empty) ||
            profileIds.Distinct().Count() != profileIds.Count)
            return new PairingDecision(false, "InvalidServerAccess", "Choose each saved server at most once.");
        var known = knownProfileIds.ToHashSet();
        if (profileIds.Any(profileId => !known.Contains(profileId)))
            return new PairingDecision(false, "UnknownServer", "One or more selected servers are no longer saved on this Host.");
        lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d) && d.CredentialHash is not null);
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Pair this Friend PC first.");
            device.AssignedProfileIds = profileIds.ToList();
            data.SaveDevices(devices);
            data.Audit($"server-access-change {device.Id} {device.AssignedProfileIds.Count} {DateTimeOffset.UtcNow:O}");
            return new PairingDecision(true, "ServerAccessSaved", device.AssignedProfileIds.Count == 0
                ? "This Friend PC is not assigned to any servers."
                : $"This Friend PC can now access {device.AssignedProfileIds.Count} server{(device.AssignedProfileIds.Count == 1 ? "" : "s")}.");
        }
    }

    public bool CanAccess(PairedDevice device, Guid profileId)
    {
        lock (sync) return !IsRevoked(device) && device.AssignedProfileIds!.Contains(profileId);
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
            return new PairingDecision(true, "DeviceNameSaved", "Friend PC name saved locally.");
        }
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static bool Matches(string candidate, string expectedHash)
    {
        try { return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), Convert.FromHexString(expectedHash)); }
        catch (FormatException) { return false; }
    }
}
