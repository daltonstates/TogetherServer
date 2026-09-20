using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;

namespace TogetherServer;

public sealed class PairedDevice
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool CanStart { get; set; }
    public bool CanStop { get; set; }
    public bool Revoked { get; set; }
    public string? InviteHash { get; set; }
    public DateTimeOffset? InviteExpiresUtc { get; set; }
    public string? CredentialHash { get; set; }
    public DateTimeOffset? CredentialExpiresUtc { get; set; }
}

public sealed record DeviceView(Guid Id, string Name, bool CanStart, bool CanStop, bool Revoked,
    bool Paired, DateTimeOffset? CredentialExpiresUtc, DateTimeOffset? LastHeartbeatUtc, bool? GameRunning);
public sealed record PairingInvite(string Endpoint, string Fingerprint, Guid DeviceId, string Code, DateTimeOffset ExpiresUtc);
public sealed record PairingActivation(Guid DeviceId, string Code);
public sealed record PairingCredential(Guid DeviceId, string Credential, DateTimeOffset ExpiresUtc);
public sealed record HeartbeatRequest(Guid DeviceId, Guid InstanceId, long Sequence, string Version, bool? GameRunning);
public sealed record HeartbeatReceipt(Guid InstanceId, long Sequence, DateTimeOffset ReceivedUtc, bool? GameRunning);
public sealed record PairingDecision(bool Ok, string Code, string Message);
public sealed record InviteRequest(string Name, bool CanStart, bool CanStop, Guid? RotateDeviceId);
public sealed record FriendPairRequest(string Invitation, string ClientExecutablePath);
public sealed record ClientPathRequest(string Path);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteActionRequest(Guid DeviceId, Guid ProfileId);

public sealed class HostIdentity(LocalData data)
{
    private const string FileName = "host-certificate.protected";

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

public sealed class PairingService(LocalData data)
{
    private readonly object sync = new();
    private readonly List<PairedDevice> devices = data.LoadDevices();
    private readonly ConcurrentDictionary<Guid, HeartbeatReceipt> heartbeats = new();

    public IReadOnlyList<DeviceView> Views()
    {
        lock (sync) return devices.Select(device =>
        {
            heartbeats.TryGetValue(device.Id, out var heartbeat);
            var fresh = heartbeat is not null && DateTimeOffset.UtcNow - heartbeat.ReceivedUtc <= TimeSpan.FromSeconds(45);
            return new DeviceView(device.Id, device.Name, device.CanStart, device.CanStop, device.Revoked,
                device.CredentialHash is not null, device.CredentialExpiresUtc,
                fresh ? heartbeat!.ReceivedUtc : null, fresh ? heartbeat!.GameRunning : null);
        }).ToList();
    }

    public bool HasInviteOrCredential()
    {
        lock (sync) return devices.Any(d => !d.Revoked && (d.InviteHash is not null || d.CredentialHash is not null));
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
                device = new PairedDevice { Id = Guid.NewGuid(), Name = name, CanStart = canStart, CanStop = canStop };
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
            var device = devices.SingleOrDefault(d => d.Id == request.DeviceId);
            if (device is null || device.Revoked || device.InviteHash is null ||
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
            if (device.Revoked) return new PairingDecision(false, "Revoked", "This device has been revoked.");
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
            if (device.Revoked) return new PairingDecision(false, "Revoked", "This device has been revoked.");
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

    public bool AllKnownNotPlaying()
    {
        lock (sync)
        {
            var active = devices.Where(d => !d.Revoked).ToList();
            return active.Count > 0 && active.All(d => d.CredentialHash is not null &&
                d.CredentialExpiresUtc > DateTimeOffset.UtcNow &&
                heartbeats.TryGetValue(d.Id, out var receipt) &&
                DateTimeOffset.UtcNow - receipt.ReceivedUtc <= TimeSpan.FromSeconds(45) && receipt.GameRunning == false);
        }
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static bool Matches(string candidate, string expectedHash)
    {
        try { return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), Convert.FromHexString(expectedHash)); }
        catch (FormatException) { return false; }
    }
}
