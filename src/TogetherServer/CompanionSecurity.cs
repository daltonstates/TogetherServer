using System.Buffers.Binary;
using System.Collections.Concurrent;
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
    public bool CanViewLogs { get; set; }
    // Deliberately separate from presets and temporary helper permissions.
    public List<Guid> SaveReceiveProfileIds { get; set; } = [];
    public Dictionary<Guid, SharedWorldGrants> SharedWorldGrants { get; set; } = [];
    public string? SharedWorldPublicKey { get; set; }
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
    // Owner access is a separate authorization deadline. It never rotates,
    // revokes, or deletes the renewable device credential.
    public DateTimeOffset? AccessExpiresUtc { get; set; }
    public DateTimeOffset? AccessExpiryNotifiedForUtc { get; set; }
    // This grant overlays, but never rewrites, the owner's usual permissions.
    // Every action checks the Host clock so expiry does not depend on a poll.
    public DateTimeOffset? TemporaryHelperUntilUtc { get; set; }

    public bool CanStartProfile(Guid profileId) => ServerPermissionOverrides?
        .FirstOrDefault(item => item.ProfileId == profileId)?.CanStart ?? CanStart;
    public bool CanStopProfile(Guid profileId) => ServerPermissionOverrides?
        .FirstOrDefault(item => item.ProfileId == profileId)?.CanStop ?? CanStop;
    public bool CanExtendTimerForProfile(Guid profileId) => ServerPermissionOverrides?
        .FirstOrDefault(item => item.ProfileId == profileId)?.CanExtendTimer ?? CanExtendTimer;
    public bool CanViewLogsForProfile(Guid profileId) => ServerPermissionOverrides?
        .FirstOrDefault(item => item.ProfileId == profileId)?.CanViewLogs ?? CanViewLogs;
}

public sealed class ServerPermissionOverride
{
    public Guid ProfileId { get; set; }
    public bool CanStart { get; set; }
    public bool CanStop { get; set; }
    public bool CanExtendTimer { get; set; }
    public bool CanViewLogs { get; set; }
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
    public bool CanViewLogs { get; set; }
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
    bool ApprovalPending = false, bool CanExtendTimer = false, bool CanViewLogs = false,
    DateTimeOffset? AccessExpiresUtc = null, bool AccessExpired = false,
    DateTimeOffset? TemporaryHelperUntilUtc = null, bool TemporaryHelperActive = false,
    IReadOnlyList<Guid>? SaveReceiveProfileIds = null,
    IReadOnlyDictionary<Guid, SharedWorldGrants>? SharedWorldGrants = null,
    bool SharedWorldKeyEnrolled = false);
public sealed record ServerPermissionView(Guid ProfileId, bool CanStart, bool CanStop,
    bool CanExtendTimer = false, bool CanViewLogs = false);

public sealed record PairingInvite(string Endpoint, string Fingerprint, Guid DeviceId, string Code, DateTimeOffset ExpiresUtc,
    bool ServerScope = false);
public sealed record ServerInviteView(PairingInvite Invitation, bool CanStart,
    bool Open = true, int DeviceLimit = 1, int ActivatedDevices = 0,
    bool RequireApproval = false, DateTimeOffset? OpenedUtc = null,
    int DurationMinutes = 30, bool CanViewLogs = false);
public sealed record PairingActivation(Guid DeviceId, string Code, bool ServerScope = false);
public sealed record PairingCredential(Guid DeviceId, string Credential, DateTimeOffset ExpiresUtc,
    bool ApprovalPending = false);
public sealed record HeartbeatRequest(Guid DeviceId, Guid InstanceId, long Sequence, string Version,
    int ProtocolVersion = 1, IReadOnlyList<string>? Capabilities = null);
public sealed record HeartbeatReceipt(Guid InstanceId, long Sequence, DateTimeOffset ReceivedUtc,
    string AppVersion = "", int ProtocolVersion = 1);
public sealed record PairingDecision(bool Ok, string Code, string Message);
public sealed record ServerInviteRequest(bool Refresh, bool CanStart, bool EnableConnections = false,
    int DurationMinutes = 30, int DeviceLimit = 1, bool RequireApproval = false,
    bool CanViewLogs = false);
public sealed record DevicePermissionRequest(bool CanStart, bool CanStop, string? Scope = null,
    bool CanExtendTimer = false, bool CanViewLogs = false);
public sealed record DeviceServerPermissionRequest(Guid ProfileId, bool CanStart, bool CanStop,
    bool CanExtendTimer = false, bool CanViewLogs = false);
public sealed record DeviceServerAccessRequest(IReadOnlyList<Guid>? ProfileIds,
    IReadOnlyList<DeviceServerPermissionRequest>? Permissions = null);
public sealed record DeviceNameRequest(string? Name);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceAccessExpiryRequest(bool Clear = false,
    DateTimeOffset? AccessExpiresUtc = null, string? Duration = null);
public sealed record DeviceAccessExpiryResult(bool Ok, string Code, string Message,
    DateTimeOffset? AccessExpiresUtc = null, bool AccessExpired = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemporaryHelperRequest(string? Duration = null, bool Clear = false);
public sealed record TemporaryHelperResult(bool Ok, string Code, string Message,
    DateTimeOffset? TemporaryHelperUntilUtc = null, bool Active = false);

public static class DeviceAccessDurations
{
    public const string OneHour = "OneHour";
    public const string EightHours = "EightHours";
    public const string OneDay = "OneDay";
    public const string SevenDays = "SevenDays";
    public const string ThirtyDays = "ThirtyDays";
    public const string NinetyDays = "NinetyDays";

    internal static bool TryGet(string? value, out TimeSpan duration)
    {
        duration = value switch
        {
            OneHour => TimeSpan.FromHours(1),
            EightHours => TimeSpan.FromHours(8),
            OneDay => TimeSpan.FromDays(1),
            SevenDays => TimeSpan.FromDays(7),
            ThirtyDays => TimeSpan.FromDays(30),
            NinetyDays => TimeSpan.FromDays(90),
            _ => default
        };
        return duration > TimeSpan.Zero;
    }
}
public sealed record FriendPairRequest(string Invitation, string? HostAddress = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteActionRequest(Guid DeviceId, Guid ProfileId);

// A TS3 server code carries the Host address, server profile, shared pairing secret,
// and full TLS pin. The timestamp field remains in the format for compatibility,
// but server-code validity is controlled by the Host's saved state. TS1/TS2
// per-device invites still expire normally.
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
            throw new ArgumentException("The server code could not be created from the saved connection details.");
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
        if (id == Guid.Empty || !serverScope && expires <= DateTimeOffset.UtcNow) return false;
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
    private static readonly TimeSpan MaximumOwnerAccess = TimeSpan.FromDays(365);
    internal static readonly DateTimeOffset PersistentServerCodeExpiry = DateTimeOffset.MaxValue;
    private readonly LocalData data;
    private readonly TimeProvider clock;
    private readonly object sync = new();
    private readonly List<PairedDevice> devices;
    private readonly List<ServerInviteState> serverInvites;
    private readonly List<CredentialRenewalReceipt> renewalReceipts;
    private readonly ConcurrentDictionary<Guid, HeartbeatReceipt> heartbeats = new();

    public PairingService(LocalData data, TimeProvider? clock = null)
    {
        this.data = data;
        this.clock = clock ?? TimeProvider.System;
        var persisted = data.LoadPairingState();
        devices = persisted.Devices;
        serverInvites = persisted.ServerInvites;
        renewalReceipts = persisted.CredentialRenewals;
        // A successor cannot re-sign the inherited roster. Its pairing state is
        // kept as loaded; authorization still enforces revocation and expiry.
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
            if (!HasSuccessorSharedProfile())
            {
                var normalized = NormalizeAssignments() | NormalizeServerCodes();
                normalized |= renewalReceipts.RemoveAll(receipt => receipt.PreviousAcceptedUntilUtc <= UtcNow ||
                    devices.All(device => device.Id != receipt.DeviceId)) > 0;
                if (normalized)
                {
                    MarkSharedRostersDirty();
                    SaveState();
                }
            }
        }
    }

    private DateTimeOffset UtcNow => clock.GetUtcNow();
    private bool AuthorityStateOrDamaged(Guid profileId)
    {
        try { return new WorldAuthorityStore(data).HasState(profileId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or CryptographicException)
        { return true; }
    }
    private bool HasSuccessorSharedProfile() => data.LoadSettings().Profiles.Any(profile =>
        AuthorityStateOrDamaged(profile.Id));
    private bool CanChangeSharedRoster(IEnumerable<Guid> affected) =>
        !data.LoadSettings().Profiles.Any(profile =>
            affected.Contains(profile.Id) && AuthorityStateOrDamaged(profile.Id));
    private static IEnumerable<Guid> AffectedProfiles(PairedDevice device) =>
        (device.AssignedProfileIds ?? []).Concat(device.SharedWorldGrants?.Keys.AsEnumerable() ??
            Enumerable.Empty<Guid>()).Append(device.ProfileId).Where(id => id != Guid.Empty);
    // Transport assignment alone is not signed membership. In particular,
    // Activate creates an assigned PC without a shared-world signing key.
    private IReadOnlyCollection<Guid> SignedRosterProfiles(PairedDevice device)
    {
        var assigned = AffectedProfiles(device).ToHashSet();
        var affected = new HashSet<Guid>();
        var authority = new WorldAuthorityStore(data);
        foreach (var profile in data.LoadSettings().Profiles)
        {
            try
            {
                if (!authority.HasState(profile.Id))
                {
                    // On the original Host, an enrolled PC in the local roster
                    // projection is a membership change even before publication.
                    if (device.SharedWorldPublicKey is not null && assigned.Contains(profile.Id))
                        affected.Add(profile.Id);
                    continue;
                }
                var records = authority.Read(profile.Id);
                var heads = records.Where(record => !records.Any(child =>
                    child.Proposal.ParentAuthorityHash == record.RecordHash)).ToArray();
                if (heads.Length != 1 || heads[0].Roster.Members.Any(member =>
                    member.DeviceId == device.Id))
                    affected.Add(profile.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or CryptographicException)
            {
                // Damaged authority cannot establish nonmembership. Keep its
                // existing fail-closed behavior without blocking credential removal.
                affected.Add(profile.Id);
            }
        }
        return affected;
    }
    private static PairingDecision SuccessorRosterDenied() => new(false, "SuccessorRosterReadOnly",
        "Only the original owner can change this shared world's signed membership.");
    private string RosterDirtyPath(Guid profileId) => Path.Combine(data.RootPath,
        "shared-worlds", profileId.ToString("N"), "roster-dirty");

    public bool SharedRosterDirty(Guid profileId) => File.Exists(RosterDirtyPath(profileId));

    public void RequireSharedRosterPublication(Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
        {
        if (!CanChangeSharedRoster([profileId]))
            throw new InvalidDataException("This successor PC cannot publish signed membership.");
        var path = RosterDirtyPath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Publication required");
        }
    }

    private void MarkSharedRostersDirty(IEnumerable<Guid>? affected = null)
    {
        var selected = affected?.ToHashSet();
        foreach (var profile in data.LoadSettings().Profiles.Where(item =>
            (item.SharedSavesEnabled || AuthorityStateOrDamaged(item.Id)) &&
            (selected is null || selected.Contains(item.Id))))
        {
            var path = RosterDirtyPath(profile.Id);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Signed membership review required");
        }
    }

    public void ConfirmSharedRosterPublished(Guid profileId, SharedWorldRoster roster)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var chain = new SharedWorldRosterChainStore(data);
            if (!SharedWorldRosterTrust.VerifySignature(roster) ||
                roster.Schema == 3 && chain.Heads(profileId)
                    .SingleOrDefault()?.Signature != roster.Signature ||
                !(roster.Schema == 3
                    ? roster.OwnerLocalBaselineMembers ?? chain.Read(profileId)[0].Members
                    : roster.Members).SequenceEqual(SharedRosterMembers(profileId).OrderBy(item => item.DeviceId)))
                throw new InvalidDataException("The signed roster no longer matches current access.");
            var path = RosterDirtyPath(profileId);
            if (File.Exists(path)) File.Delete(path);
        }
    }
    public bool IsTemporaryHelperActive(PairedDevice device) =>
        device.TemporaryHelperUntilUtc is { } until && until > UtcNow;

    private void SaveState()
    {
        try
        {
            data.SavePairingState(new PairingPersistentState
            {
                Devices = devices,
                ServerInvites = serverInvites,
                CredentialRenewals = renewalReceipts
            });
        }
        catch
        {
            // Never retain a newly permissive in-memory state when its durable snapshot failed.
            foreach (var invite in serverInvites)
            {
                invite.Closed = true;
                invite.PairingExpiresUtc = UtcNow;
                invite.Generation = Guid.NewGuid();
            }
            foreach (var device in devices)
            {
                device.Revoked = true;
                device.InviteHash = null;
                device.InviteExpiresUtc = null;
                device.CredentialHash = null;
                device.PreviousCredentialHash = null;
                device.PreviousCredentialExpiresUtc = null;
            }
            renewalReceipts.Clear();
            heartbeats.Clear();
            throw;
        }
    }

    private void ForgetRenewalReceipt(Guid deviceId)
    {
        renewalReceipts.RemoveAll(receipt => receipt.DeviceId == deviceId);
        try { data.DeleteProtected($"credential-renewal-{deviceId:N}.protected"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            data.TryAudit($"legacy-renewal-cleanup-failed {deviceId} {ex.GetType().Name} {UtcNow:O}");
        }
    }

    private bool NormalizeServerCodes()
    {
        var changed = false;
        foreach (var invite in serverInvites)
        {
            if (!invite.Closed)
            {
                if (invite.DurationMinutes != 0)
                {
                    invite.DurationMinutes = 0;
                    changed = true;
                }
                if (invite.DeviceLimit != int.MaxValue)
                {
                    invite.DeviceLimit = int.MaxValue;
                    changed = true;
                }
            }
            else if (invite.DurationMinutes is < 5 or > 1440)
            {
                invite.DurationMinutes = 30;
                changed = true;
            }
            if (invite.Closed && invite.DeviceLimit < 1)
            {
                invite.DeviceLimit = 1;
                changed = true;
            }
            if (invite.PairingOpenedUtc is null)
            {
                invite.PairingOpenedUtc = UtcNow;
                changed = true;
            }
            // An invite that merely aged out becomes a persistent server code.
            // Respect an explicitly closed or emergency-revoked code until the
            // owner chooses Invite friends again.
            if (!invite.Closed && invite.PairingExpiresUtc is not null)
            {
                invite.PairingExpiresUtc = null;
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
            device.SharedWorldGrants ??= [];
            var saveGrants = device.SharedWorldGrants
                .Where(item => normalized.Contains(item.Key) && item.Value?.Receive == true)
                .Select(item => item.Key).Distinct().ToList();
            if (device.SaveReceiveProfileIds is null ||
                !saveGrants.SequenceEqual(device.SaveReceiveProfileIds))
            {
                device.SaveReceiveProfileIds = saveGrants;
                changed = true;
            }
            foreach (var id in device.SharedWorldGrants.Keys.Where(id => !normalized.Contains(id)).ToArray())
            {
                device.SharedWorldGrants.Remove(id);
                changed = true;
            }
            // Corrupt duplicate entries collapse to the most restrictive
            // effective permission so normalization never expands access.
            var normalizedPermissions = device.ServerPermissionOverrides
                .Where(item => item.ProfileId != Guid.Empty && normalized.Contains(item.ProfileId))
                .GroupBy(item => item.ProfileId)
                .Select(group => new ServerPermissionOverride
                {
                    ProfileId = group.Key,
                    CanStart = group.All(item => item.CanStart),
                    CanStop = group.All(item => item.CanStop),
                    CanExtendTimer = group.All(item => item.CanExtendTimer),
                    CanViewLogs = group.All(item => item.CanViewLogs)
                })
                .Where(item => item.CanStart != device.CanStart || item.CanStop != device.CanStop ||
                    item.CanExtendTimer != device.CanExtendTimer || item.CanViewLogs != device.CanViewLogs)
                .ToList();
            if (normalizedPermissions.Count != device.ServerPermissionOverrides.Count ||
                normalizedPermissions.Where((item, index) =>
                    index >= device.ServerPermissionOverrides.Count ||
                    item.ProfileId != device.ServerPermissionOverrides[index].ProfileId ||
                    item.CanStart != device.ServerPermissionOverrides[index].CanStart ||
                    item.CanStop != device.ServerPermissionOverrides[index].CanStop ||
                    item.CanExtendTimer != device.ServerPermissionOverrides[index].CanExtendTimer ||
                    item.CanViewLogs != device.ServerPermissionOverrides[index].CanViewLogs).Any())
            {
                device.ServerPermissionOverrides = normalizedPermissions;
                changed = true;
            }
        }
        foreach (var duplicate in devices.Where(item => item.SharedWorldPublicKey is not null)
                     .GroupBy(item => item.SharedWorldPublicKey, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            foreach (var device in duplicate)
            {
                device.SharedWorldPublicKey = null;
                device.SharedWorldGrants?.Clear();
                device.SaveReceiveProfileIds?.Clear();
                changed = true;
            }
        return changed;
    }

    private bool IsRevoked(PairedDevice device) => device.Revoked ||
        (device.ProfileId == Guid.Empty
            ? serverInvites.Any(invite => invite.Rotated)
            : serverInvites.SingleOrDefault(invite => invite.ProfileId == device.ProfileId)?.Generation != device.InviteGeneration);

    private static bool IsAccessExpired(PairedDevice device, DateTimeOffset now) =>
        device.AccessExpiresUtc is { } accessExpiresUtc && accessExpiresUtc <= now;

    private PairingDecision AuthorizationDecision(PairedDevice device, DateTimeOffset now)
    {
        if (IsRevoked(device))
            return new(false, "Revoked", "This server code was replaced or this PC's access was removed.");
        if (device.ApprovalPending)
            return new(false, "ApprovalPending", "The Host must approve this PC locally before it can connect.");
        if (device.CredentialExpiresUtc <= now)
            return new(false, "Expired", "This PC's saved access has expired.");
        if (!IsAccessExpired(device, now))
            return new(true, "Authorized", "This PC has access.");

        if (device.AccessExpiryNotifiedForUtc != device.AccessExpiresUtc)
        {
            device.AccessExpiryNotifiedForUtc = device.AccessExpiresUtc;
            SaveState();
            data.TryAudit($"device-access-expired {device.Id} {device.AccessExpiresUtc:O} observed={now:O}");
            Activity("Access", "AccessExpired", "The Host's access deadline passed for a connected PC.",
                ActivitySeverity.Warning, deviceId: device.Id);
        }
        return new(false, "AccessExpired", "The Host ended access for this PC at the saved time.");
    }

    public ServerInviteView? CurrentServerInvite(Guid profileId, string? endpoint = null, string? fingerprint = null)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            if (state is null) return null;
            if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(fingerprint) &&
                (!string.Equals(state.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal)))
            {
                // A copied code must advertise the Host's current route and active
                // pin, but refreshing connection details must not rotate its secret.
                state.Endpoint = endpoint;
                state.Fingerprint = fingerprint;
                SaveState();
            }
            var open = !state.Closed;
            return new(new PairingInvite(state.Endpoint, state.Fingerprint, profileId,
                state.Code, PersistentServerCodeExpiry, true), state.CanStart, open, state.DeviceLimit,
                state.ActivatedDevices, state.RequireApproval, state.PairingOpenedUtc,
                state.DurationMinutes, state.CanViewLogs);
        }
    }

    public void ReconcileProfiles(IEnumerable<Guid> profileIds)
    {
        var known = profileIds.ToHashSet();
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            if (HasSuccessorSharedProfile()) return;
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
            if (removed > 0 || changedDevices)
            {
                if (changedDevices) MarkSharedRostersDirty();
                SaveState();
            }
        }
    }

    public PairingInvite IssueServer(Guid profileId, bool canStart, bool canStop, string endpoint,
        string fingerprint, bool refresh, int durationMinutes = 30, int deviceLimit = 1,
        bool requireApproval = false, bool canViewLogs = false)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("Choose a saved server.");
        // Keep the old request fields in the local API for compatibility. A server
        // code no longer uses either value and remains available until replacement.
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var affected = new HashSet<Guid>();
            if (refresh)
                affected.UnionWith(devices.Where(device => device.ProfileId == profileId && !device.Revoked)
                    .SelectMany(SignedRosterProfiles));
            if (refresh && !CanChangeSharedRoster(affected.Append(profileId)))
                throw new InvalidOperationException("This successor PC cannot replace a shared world server code.");
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            var now = UtcNow;
            if (state is not null && !refresh)
            {
                var wasAvailable = !state.Closed && state.PairingExpiresUtc is null;
                var changed = !wasAvailable ||
                    !string.Equals(state.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                    state.CanStart != canStart || state.CanStop != canStop ||
                    state.CanViewLogs != canViewLogs || state.RequireApproval != requireApproval ||
                    state.DurationMinutes != 0 || state.DeviceLimit != int.MaxValue;
                state.Endpoint = endpoint;
                state.Fingerprint = fingerprint;
                state.CanStart = canStart;
                state.CanStop = canStop;
                state.CanViewLogs = canViewLogs;
                state.PairingOpenedUtc ??= now;
                state.PairingExpiresUtc = null;
                state.DurationMinutes = 0;
                state.DeviceLimit = int.MaxValue;
                state.RequireApproval = requireApproval;
                state.Closed = false;
                if (changed)
                {
                    SaveState();
                    if (!wasAvailable)
                    {
                        data.TryAudit($"server-code-enable {profileId} approval={requireApproval} {now:O}");
                        Activity("Connections", "CodeAvailable", "The server code is available until the owner replaces it.",
                            ActivitySeverity.Important, profileId);
                    }
                }
                return new(state.Endpoint, state.Fingerprint, profileId, state.Code,
                    PersistentServerCodeExpiry, true);
            }
            var next = new ServerInviteState
            {
                ProfileId = profileId,
                Generation = Guid.NewGuid(),
                Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Endpoint = endpoint,
                Fingerprint = fingerprint,
                CanStart = canStart,
                CanStop = canStop,
                CanViewLogs = canViewLogs,
                Rotated = refresh,
                PairingOpenedUtc = now,
                PairingExpiresUtc = null,
                DurationMinutes = 0,
                DeviceLimit = int.MaxValue,
                ActivatedDevices = 0,
                RequireApproval = requireApproval,
                Closed = false
            };
            if (state is not null) serverInvites.Remove(state);
            serverInvites.Add(next);
            // Generation is the authorization gate. Persist it before updating device views,
            // so even an interrupted rotation cannot leave an old credential usable.
            if (refresh) MarkSharedRostersDirty(affected);
            SaveState();
            if (refresh)
            {
                foreach (var device in devices.Where(device => device.ProfileId == profileId))
                {
                    device.Revoked = true;
                    device.InviteHash = null;
                    device.InviteExpiresUtc = null;
                    heartbeats.TryRemove(device.Id, out _);
                }
                SaveState();
            }
            data.TryAudit($"server-code {(refresh ? "replace" : "create")} {profileId} approval={requireApproval} {now:O}");
            Activity("Connections", refresh ? "CodeReplaced" : "CodeAvailable",
                refresh ? "The server code was replaced and access created through the old code was removed."
                    : "The server code is available until the owner replaces it.",
                ActivitySeverity.Important, profileId);
            return new(endpoint, fingerprint, profileId, next.Code, PersistentServerCodeExpiry, true);
        }
    }

    public PairingDecision ClosePairing(Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            if (state is null) return new(false, "UnknownServerCode", "This server does not have a server code yet.");
            state.Closed = true;
            state.PairingExpiresUtc = UtcNow;
            state.Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            SaveState();
            data.TryAudit($"server-code-disable {profileId} {UtcNow:O}");
            Activity("Connections", "CodeDisabled", "The server code was turned off. PCs that already connected kept their access.",
                ActivitySeverity.Important, profileId);
            return new(true, "ServerCodeDisabled", "The server code is off. PCs that already connected keep their access.");
        }
    }

    public PairingDecision EmergencyRevoke(Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == profileId);
            if (state is null) return new(false, "UnknownServerCode", "This server does not have a server code yet.");
            var previousGeneration = state.Generation;
            state.Generation = Guid.NewGuid();
            state.Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            state.Closed = true;
            state.PairingExpiresUtc = UtcNow;
            state.ActivatedDevices = 0;
            state.Rotated = true;
            var revoked = 0;
            var affected = new HashSet<Guid>();
            foreach (var device in devices.Where(device => device.ProfileId == profileId &&
                device.InviteGeneration == previousGeneration && !device.Revoked))
            {
                affected.UnionWith(SignedRosterProfiles(device));
                device.Revoked = true;
                device.InviteHash = null;
                device.InviteExpiresUtc = null;
                device.PreviousCredentialHash = null;
                device.PreviousCredentialExpiresUtc = null;
                ForgetRenewalReceipt(device.Id);
                heartbeats.TryRemove(device.Id, out _);
                revoked++;
            }
            MarkSharedRostersDirty(affected);
            var successor = !CanChangeSharedRoster(affected);
            SaveState();
            data.TryAudit($"pairing-emergency-revoke {profileId} devices={revoked} {UtcNow:O}");
            Activity("Connections", "CodeAccessRemoved",
                $"The server code was turned off and access was removed from {revoked} PC{(revoked == 1 ? "" : "s")}.",
                ActivitySeverity.Warning, profileId);
            return new(true, "ServerCodeAccessRemoved", successor
                ? "Access was removed. Signed membership needs review; sharing and recovery are paused on this PC."
                : $"The server code is off and access was removed from {revoked} PC{(revoked == 1 ? "" : "s")}.");
        }
    }

    public IReadOnlyList<DeviceView> Views()
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var now = UtcNow;
            var expiryNoticesChanged = false;
            foreach (var device in devices.Where(item => !IsRevoked(item) && item.CredentialExpiresUtc is { } expiry &&
                expiry > now && expiry - now <= TimeSpan.FromDays(14) &&
                item.CredentialExpiryNotifiedForUtc != expiry))
            {
                device.CredentialExpiryNotifiedForUtc = device.CredentialExpiresUtc;
                expiryNoticesChanged = true;
                Activity("Access", "CredentialExpiring", "A connected PC's saved access expires within 14 days.",
                    ActivitySeverity.Warning, device.ProfileId == Guid.Empty ? null : device.ProfileId, device.Id);
                Activity("Access", "CredentialExpiring", "This PC's saved access expires within 14 days and will be renewed while connected.",
                    ActivitySeverity.Warning, device.ProfileId == Guid.Empty ? null : device.ProfileId,
                    device.Id, ActivityVisibility.Device);
            }
            foreach (var device in devices.Where(item => !IsRevoked(item) && IsAccessExpired(item, now) &&
                item.AccessExpiryNotifiedForUtc != item.AccessExpiresUtc))
            {
                device.AccessExpiryNotifiedForUtc = device.AccessExpiresUtc;
                expiryNoticesChanged = true;
                data.TryAudit($"device-access-expired {device.Id} {device.AccessExpiresUtc:O} observed={now:O}");
                Activity("Access", "AccessExpired", "The Host's access deadline passed for a connected PC.",
                    ActivitySeverity.Warning, deviceId: device.Id);
            }
            if (expiryNoticesChanged) SaveState();
            return devices.Select(device =>
            {
                heartbeats.TryGetValue(device.Id, out var heartbeat);
                var fresh = heartbeat is not null && now - heartbeat.ReceivedUtc <= TimeSpan.FromSeconds(45);
                var helper = IsTemporaryHelperActive(device);
                return new DeviceView(device.Id, device.ProfileId, device.AssignedProfileIds!.ToArray(),
                    device.Name, helper || device.CanStart, helper || device.CanStop, IsRevoked(device),
                    device.CredentialHash is not null, device.CredentialExpiresUtc,
                    fresh ? heartbeat!.ReceivedUtc : null,
                    device.AssignedProfileIds.Select(profileId => new ServerPermissionView(profileId,
                        helper || device.CanStartProfile(profileId), helper || device.CanStopProfile(profileId),
                        helper || device.CanExtendTimerForProfile(profileId), helper || device.CanViewLogsForProfile(profileId))).ToList(),
                    fresh ? heartbeat!.AppVersion : null, fresh ? heartbeat!.ProtocolVersion : null,
                    device.ApprovalPending, helper || device.CanExtendTimer, helper || device.CanViewLogs,
                    device.AccessExpiresUtc, IsAccessExpired(device, now),
                    device.TemporaryHelperUntilUtc, helper,
                    device.SaveReceiveProfileIds.Where(device.AssignedProfileIds.Contains).ToArray(),
                    new Dictionary<Guid, SharedWorldGrants>(device.SharedWorldGrants ?? []),
                    device.SharedWorldPublicKey is not null);
            }).ToList();
        }
    }

    public bool HasInviteOrCredential()
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync) return serverInvites.Any(invite => !invite.Closed) ||
            devices.Any(d => !IsRevoked(d) && (d.InviteHash is not null || d.CredentialHash is not null));
    }

    public PairingInvite Issue(string name, bool canStart, bool canStop, string endpoint, string fingerprint, Guid? rotatingId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("Enter a device name up to 80 characters.");
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expires = UtcNow.AddMinutes(30);
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
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
                device = new PairedDevice
                {
                    Id = newDeviceId,
                    AssignedProfileIds = [],
                    ServerPermissionOverrides = [],
                    Name = name == "Friend PC" ? $"Friend PC {newDeviceId.ToString("N")[..6]}" : name,
                    CanStart = canStart,
                    CanStop = canStop
                };
                devices.Add(device);
            }
            device.InviteHash = Hash(code);
            device.InviteExpiresUtc = expires;
            SaveState();
            data.TryAudit($"invite {device.Id} {UtcNow:O}");
            return new PairingInvite(endpoint, fingerprint, device.Id, code, expires);
        }
    }

    public PairingCredential? Activate(PairingActivation request)
    {
        if (request.Code is null || request.Code.Length > 128) return null;
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            if (request.ServerScope)
            {
                var state = serverInvites.SingleOrDefault(invite => invite.ProfileId == request.DeviceId);
                if (state is null || state.Closed || !Matches(request.Code, Hash(state.Code))) return null;
                var id = Guid.NewGuid();
                var serverToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var expires = UtcNow.AddDays(90);
                devices.Add(new PairedDevice
                {
                    Id = id,
                    ProfileId = state.ProfileId,
                    InviteGeneration = state.Generation,
                    AssignedProfileIds = [state.ProfileId],
                    ServerPermissionOverrides = [],
                    Name = $"Friend PC {id.ToString("N")[..6]}",
                    CanStart = state.CanStart,
                    CanStop = state.CanStop,
                    CanViewLogs = state.CanViewLogs,
                    CredentialHash = Hash(serverToken),
                    CredentialExpiresUtc = expires,
                    ApprovalPending = state.RequireApproval
                });
                if (state.ActivatedDevices < int.MaxValue) state.ActivatedDevices++;
                SaveState();
                data.TryAudit($"activate {id} {state.ProfileId} approval-pending={state.RequireApproval} {UtcNow:O}");
                Activity("Connections", state.RequireApproval ? "DeviceAwaitingApproval" : "DevicePaired",
                    state.RequireApproval ? "A new PC connected and is waiting for Host approval." : "A new PC connected.",
                    ActivitySeverity.Important, state.ProfileId, id);
                Activity("Connections", state.RequireApproval ? "ApprovalPending" : "Paired",
                    state.RequireApproval ? "This PC is waiting for Host approval." : "This PC connected.",
                    ActivitySeverity.Important, state.ProfileId, id, ActivityVisibility.Device);
                return new PairingCredential(id, serverToken, expires, state.RequireApproval);
            }
            var device = devices.SingleOrDefault(d => d.Id == request.DeviceId);
            if (device is null || IsRevoked(device) || device.InviteHash is null ||
                device.InviteExpiresUtc <= UtcNow || !Matches(request.Code, device.InviteHash)) return null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            device.CredentialHash = Hash(token);
            device.CredentialExpiresUtc = UtcNow.AddDays(90);
            device.CredentialExpiryNotifiedForUtc = null;
            device.PreviousCredentialHash = null;
            device.PreviousCredentialExpiresUtc = null;
            ForgetRenewalReceipt(device.Id);
            device.InviteHash = null;
            device.InviteExpiresUtc = null;
            heartbeats.TryRemove(device.Id, out _);
            SaveState();
            data.TryAudit($"activate {device.Id} {UtcNow:O}");
            return new PairingCredential(device.Id, token, device.CredentialExpiresUtc.Value);
        }
    }

    public PairingDecision Authenticate(Guid id, string? bearer, out PairedDevice? device)
        => Authenticate(id, bearer, out device, out _);

    public PairingDecision Authenticate(Guid id, string? bearer, out PairedDevice? device,
        out bool usedPreviousCredential)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            usedPreviousCredential = false;
            device = devices.SingleOrDefault(d => d.Id == id);
            var current = device?.CredentialHash is not null && bearer is not null && bearer.Length <= 128 &&
                Matches(bearer, device.CredentialHash);
            var previous = device?.PreviousCredentialHash is not null && bearer is not null && bearer.Length <= 128 &&
                device.PreviousCredentialExpiresUtc > UtcNow && Matches(bearer, device.PreviousCredentialHash);
            if (device is null || !current && !previous)
            {
                device = null;
                return new PairingDecision(false, "Unauthorized", "This PC's saved access was not accepted.");
            }
            var authorization = AuthorizationDecision(device, UtcNow);
            if (!authorization.Ok) return authorization;
            usedPreviousCredential = !current && previous;
            return new PairingDecision(true, "Authenticated", "Device authenticated.");
        }
    }

    public PairingDecision AuthorizeActiveDevice(Guid id, out PairedDevice? device)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            device = devices.SingleOrDefault(item => item.Id == id && item.CredentialHash is not null);
            return device is null
                ? new PairingDecision(false, "Unauthorized", "This PC's saved access was not accepted.")
                : AuthorizationDecision(device, UtcNow);
        }
    }

    public CredentialRenewal? Renew(PairedDevice authenticatedDevice, CredentialRenewalRequest request,
        bool authenticatedWithPreviousCredential)
    {
        if (request.DeviceId != authenticatedDevice.Id || request.RequestId == Guid.Empty) return null;
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(item => item.Id == authenticatedDevice.Id);
            if (device is null || device.CredentialHash is null ||
                !AuthorizationDecision(device, UtcNow).Ok) return null;
            var existing = renewalReceipts.SingleOrDefault(receipt => receipt.DeviceId == device.Id);
            if (existing is not null && existing.PreviousAcceptedUntilUtc > UtcNow)
            {
                if (existing.RequestId == request.RequestId)
                    return new(existing.DeviceId, existing.RequestId, existing.Credential,
                        existing.ExpiresUtc, existing.PreviousAcceptedUntilUtc);
                // While the retry receipt and old credential overlap, no token
                // may start another rotation. This keeps a lost-response retry
                // idempotent and prevents the overlap token from advancing the chain.
                return null;
            }
            if (authenticatedWithPreviousCredential) return null;

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var overlap = UtcNow.AddMinutes(10);
            device.PreviousCredentialHash = device.CredentialHash;
            device.PreviousCredentialExpiresUtc = overlap;
            device.CredentialHash = Hash(token);
            device.CredentialExpiresUtc = UtcNow.AddDays(90);
            var receipt = new CredentialRenewalReceipt
            {
                DeviceId = device.Id,
                RequestId = request.RequestId,
                Credential = token,
                ExpiresUtc = device.CredentialExpiresUtc.Value,
                PreviousAcceptedUntilUtc = overlap
            };
            renewalReceipts.RemoveAll(item => item.DeviceId == device.Id);
            renewalReceipts.Add(receipt);
            SaveState();
            data.TryAudit($"credential-renew {device.Id} {UtcNow:O}");
            Activity("Access", "CredentialRenewed", "This PC's saved access was renewed for 90 days.",
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
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var current = devices.SingleOrDefault(item => item.Id == device.Id && item.CredentialHash is not null);
            if (current is null)
                return new PairingDecision(false, "Unauthorized", "This PC's saved access was not accepted.");
            var authorization = AuthorizationDecision(current, UtcNow);
            if (!authorization.Ok) return authorization;
            if (heartbeats.TryGetValue(device.Id, out var prior) && prior.InstanceId == request.InstanceId &&
                request.Sequence <= prior.Sequence)
                return new PairingDecision(false, "Replay", "Heartbeat sequence did not advance.");
            heartbeats[device.Id] = new HeartbeatReceipt(request.InstanceId, request.Sequence,
                UtcNow, request.Version, request.ProtocolVersion);
            return new PairingDecision(true, "Received", "Heartbeat recorded.");
        }
    }

    public DeviceAccessExpiryResult SetAccessExpiry(Guid id, DeviceAccessExpiryRequest request)
    {
        var selections = (request.Clear ? 1 : 0) + (request.AccessExpiresUtc.HasValue ? 1 : 0) +
            (request.Duration is null ? 0 : 1);
        if (selections != 1)
            return new(false, "InvalidAccessExpiry",
                "Choose exactly one access deadline, reviewed duration, or Clear.");

        var now = UtcNow;
        DateTimeOffset? deadline = null;
        if (!request.Clear)
        {
            if (request.AccessExpiresUtc is { } requestedDeadline)
            {
                if (requestedDeadline.Offset != TimeSpan.Zero)
                    return new(false, "InvalidAccessExpiry", "AccessExpiresUtc must be an explicit UTC timestamp ending in Z.");
                deadline = requestedDeadline;
            }
            else if (DeviceAccessDurations.TryGet(request.Duration, out var duration))
                deadline = now.Add(duration);
            else
                return new(false, "InvalidAccessDuration",
                    "Choose OneHour, EightHours, OneDay, SevenDays, ThirtyDays, or NinetyDays.");

            if (deadline is not { } resolvedDeadline)
                return new(false, "InvalidAccessExpiry", "Choose an access deadline.");
            if (resolvedDeadline <= now)
                return new(false, "AccessExpiryInPast", "Choose an access deadline in the future.");
            if (resolvedDeadline > now.Add(MaximumOwnerAccess))
                return new(false, "AccessExpiryTooDistant", "Choose an access deadline no more than 365 days away.");
        }

        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(item => item.Id == id && item.CredentialHash is not null);
            if (device is null)
                return new(false, "UnknownDevice", "Connect this Friend PC first.");
            if (IsRevoked(device))
                return new(false, "Revoked", "A Friend PC whose access was removed cannot receive an end date.");
            if (!CanChangeSharedRoster(AffectedProfiles(device)))
                return new(false, "SuccessorRosterReadOnly", SuccessorRosterDenied().Message);

            device.AccessExpiresUtc = deadline;
            device.AccessExpiryNotifiedForUtc = null;
            MarkSharedRostersDirty(AffectedProfiles(device));
            SaveState();
            if (deadline is null)
            {
                data.TryAudit($"device-access-expiry-cleared {device.Id} {now:O}");
                Activity("Access", "AccessExpiryCleared", "The Host removed the access deadline for a connected PC.",
                    ActivitySeverity.Important, deviceId: device.Id);
                return new(true, "AccessExpiryCleared", "This Friend PC no longer has an access end date.");
            }

            data.TryAudit($"device-access-expiry-set {device.Id} {deadline:O} changed={now:O}");
            Activity("Access", "AccessExpirySet", "The Host set an access deadline for a connected PC.",
                ActivitySeverity.Important, deviceId: device.Id);
            return new(true, "AccessExpirySet", "This Friend PC's access end date was saved.", deadline, false);
        }
    }

    public PairingDecision Revoke(Guid id)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id);
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Device was not found.");
            var affected = SignedRosterProfiles(device);
            var successor = !CanChangeSharedRoster(affected);
            device.Revoked = true;
            device.InviteHash = null;
            device.InviteExpiresUtc = null;
            device.PreviousCredentialHash = null;
            device.PreviousCredentialExpiresUtc = null;
            ForgetRenewalReceipt(device.Id);
            heartbeats.TryRemove(id, out _);
            MarkSharedRostersDirty(affected);
            SaveState();
            data.TryAudit($"revoke {device.Id} {UtcNow:O}");
            Activity("Access", "DeviceRevoked", "The Host removed access from a connected PC.", ActivitySeverity.Warning,
                device.ProfileId == Guid.Empty ? null : device.ProfileId, device.Id);
            return new PairingDecision(true, "Revoked", successor
                ? "Access removed. Signed membership needs review; sharing and recovery are paused on this PC."
                : "Access removed from this PC.");
        }
    }

    public PairingDecision Approve(Guid id)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d) && d.CredentialHash is not null);
            if (device is null) return new(false, "UnknownDevice", "Connected PC was not found.");
            if (!device.ApprovalPending) return new(true, "AlreadyApproved", "This PC is already approved.");
            if (!CanChangeSharedRoster(AffectedProfiles(device))) return SuccessorRosterDenied();
            device.ApprovalPending = false;
            MarkSharedRostersDirty(AffectedProfiles(device));
            SaveState();
            data.TryAudit($"device-approve {device.Id} {UtcNow:O}");
            Activity("Connections", "DeviceApproved", "A waiting PC was approved locally.",
                ActivitySeverity.Important, device.ProfileId == Guid.Empty ? null : device.ProfileId, device.Id);
            Activity("Connections", "DeviceApproved", "The Host approved this PC.",
                ActivitySeverity.Important, device.ProfileId == Guid.Empty ? null : device.ProfileId,
                device.Id, ActivityVisibility.Device);
            return new(true, "DeviceApproved", "Friend PC approved. Its next authenticated check can connect.");
        }
    }

    public PairingDecision SetPermissions(Guid id, bool canStart, bool canStop, string? scope = null,
        bool canExtendTimer = false, bool canViewLogs = false)
    {
        if (scope is not (null or "start" or "stop" or "extend" or "logs"))
            return new PairingDecision(false, "InvalidPermissionScope", "Choose Start, Stop, Add shutdown time, or View logs permissions.");
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d) && d.CredentialHash is not null);
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Connect this Friend PC first.");
            var effective = device.AssignedProfileIds!.ToDictionary(profileId => profileId, profileId =>
                (CanStart: device.CanStartProfile(profileId), CanStop: device.CanStopProfile(profileId),
                    CanExtendTimer: device.CanExtendTimerForProfile(profileId),
                    CanViewLogs: device.CanViewLogsForProfile(profileId)));
            if (scope is null or "start") device.CanStart = canStart;
            if (scope is null or "stop") device.CanStop = canStop;
            if (scope is null or "extend") device.CanExtendTimer = canExtendTimer;
            if (scope is null or "logs") device.CanViewLogs = canViewLogs;
            device.ServerPermissionOverrides = device.AssignedProfileIds!.Select(profileId =>
            {
                var previous = effective[profileId];
                return new ServerPermissionOverride
                {
                    ProfileId = profileId,
                    CanStart = scope is null or "start" ? device.CanStart : previous.CanStart,
                    CanStop = scope is null or "stop" ? device.CanStop : previous.CanStop,
                    CanExtendTimer = scope is null or "extend" ? device.CanExtendTimer : previous.CanExtendTimer,
                    CanViewLogs = scope is null or "logs" ? device.CanViewLogs : previous.CanViewLogs
                };
            }).Where(permission => permission.CanStart != device.CanStart || permission.CanStop != device.CanStop ||
                permission.CanExtendTimer != device.CanExtendTimer || permission.CanViewLogs != device.CanViewLogs).ToList();
            SaveState();
            data.TryAudit($"permissions-change {device.Id} {UtcNow:O}");
            Activity("Access", "PermissionsChanged", "The Host changed what a connected PC can do.",
                ActivitySeverity.Important, device.ProfileId == Guid.Empty ? null : device.ProfileId, device.Id);
            Activity("Access", "PermissionsChanged", "The Host changed this PC's permissions.",
                ActivitySeverity.Important, device.ProfileId == Guid.Empty ? null : device.ProfileId,
                device.Id, ActivityVisibility.Device);
            return new PairingDecision(true, "PermissionsSaved", "Friend permissions changed immediately.");
        }
    }

    public TemporaryHelperResult SetTemporaryHelper(Guid id, TemporaryHelperRequest request)
    {
        if (request.Clear == (request.Duration is not null) ||
            !request.Clear && request.Duration is not (DeviceAccessDurations.OneHour or DeviceAccessDurations.EightHours))
            return new(false, "InvalidTemporaryHelper", "Choose one hour, eight hours, or End now.");
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(item => item.Id == id);
            if (device is null || device.CredentialHash is null)
                return new(false, "UnknownDevice", "Connect this Friend PC first.");
            if (IsRevoked(device)) return new(false, "Revoked", "This PC's saved access was removed.");
            if (device.ApprovalPending || IsAccessExpired(device, UtcNow))
                return new(false, "AccessUnavailable", "Approve or extend this PC's access first.");
            var until = request.Clear ? (DateTimeOffset?)null :
                UtcNow.Add(request.Duration == DeviceAccessDurations.OneHour ? TimeSpan.FromHours(1) : TimeSpan.FromHours(8));
            device.TemporaryHelperUntilUtc = until;
            SaveState();
            data.TryAudit($"temporary-helper-{(until is null ? "ended" : "granted")} {device.Id} until={until:O} {UtcNow:O}");
            Activity("Access", "PermissionsChanged", until is null ?
                "Temporary helper access ended for a connected PC." :
                "Temporary helper access was granted to a connected PC.",
                ActivitySeverity.Important, deviceId: device.Id);
            Activity("Access", "PermissionsChanged", until is null ?
                "Your temporary helper access ended." :
                "The Host granted temporary helper access to this PC.",
                ActivitySeverity.Important, deviceId: device.Id, visibility: ActivityVisibility.Device);
            return new(true, until is null ? "TemporaryHelperEnded" : "TemporaryHelperGranted",
                until is null ? "This PC returned to its usual permissions." :
                    "This PC can help until the shown deadline, then its usual permissions apply automatically.",
                until, until is not null);
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
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d) && d.CredentialHash is not null);
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Connect this Friend PC first.");
            var affected = AffectedProfiles(device).Concat(profileIds).ToArray();
            if (!CanChangeSharedRoster(affected))
                return SuccessorRosterDenied();
            device.AssignedProfileIds = profileIds.ToList();
            device.SaveReceiveProfileIds = (device.SaveReceiveProfileIds ?? [])
                .Where(device.AssignedProfileIds.Contains).ToList();
            device.SharedWorldGrants = (device.SharedWorldGrants ?? [])
                .Where(item => device.AssignedProfileIds.Contains(item.Key))
                .ToDictionary(item => item.Key, item => item.Value);
            MarkSharedRostersDirty(affected);
            if (permissions is null)
            {
                device.ServerPermissionOverrides = device.ServerPermissionOverrides!
                    .Where(permission => device.AssignedProfileIds.Contains(permission.ProfileId)).ToList();
            }
            else
            {
                device.ServerPermissionOverrides = permissions
                    .Where(permission => permission.CanStart != device.CanStart || permission.CanStop != device.CanStop ||
                        permission.CanExtendTimer != device.CanExtendTimer || permission.CanViewLogs != device.CanViewLogs)
                    .Select(permission => new ServerPermissionOverride
                    {
                        ProfileId = permission.ProfileId,
                        CanStart = permission.CanStart,
                        CanStop = permission.CanStop,
                        CanExtendTimer = permission.CanExtendTimer,
                        CanViewLogs = permission.CanViewLogs
                    }).ToList();
            }
            SaveState();
            data.TryAudit($"server-access-change {device.Id} {device.AssignedProfileIds.Count} {UtcNow:O}");
            Activity("Access", "ServerAssignmentsChanged", "The Host changed which servers a connected PC can use.",
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
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var current = devices.SingleOrDefault(item => item.Id == device.Id && item.CredentialHash is not null);
            return current is not null && AuthorizationDecision(current, UtcNow).Ok &&
                current.AssignedProfileIds!.Contains(profileId);
        }
    }

    public bool TryGetActiveDevice(Guid id, out PairedDevice? device)
    {
        return AuthorizeActiveDevice(id, out device).Ok;
    }

    public bool CanStart(PairedDevice device, Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var current = devices.SingleOrDefault(item => item.Id == device.Id && item.CredentialHash is not null);
            return current is not null && AuthorizationDecision(current, UtcNow).Ok &&
                current.AssignedProfileIds!.Contains(profileId) &&
                (IsTemporaryHelperActive(current) || current.CanStartProfile(profileId));
        }
    }

    public bool CanStop(PairedDevice device, Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var current = devices.SingleOrDefault(item => item.Id == device.Id && item.CredentialHash is not null);
            return current is not null && AuthorizationDecision(current, UtcNow).Ok &&
                current.AssignedProfileIds!.Contains(profileId) &&
                (IsTemporaryHelperActive(current) || current.CanStopProfile(profileId));
        }
    }

    public bool CanExtendTimer(PairedDevice device, Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var current = devices.SingleOrDefault(item => item.Id == device.Id && item.CredentialHash is not null);
            return current is not null && AuthorizationDecision(current, UtcNow).Ok &&
                current.AssignedProfileIds!.Contains(profileId) &&
                (IsTemporaryHelperActive(current) || current.CanExtendTimerForProfile(profileId));
        }
    }

    public bool CanViewLogs(PairedDevice device, Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var current = devices.SingleOrDefault(item => item.Id == device.Id && item.CredentialHash is not null);
            return current is not null && AuthorizationDecision(current, UtcNow).Ok &&
                current.AssignedProfileIds!.Contains(profileId) &&
                (IsTemporaryHelperActive(current) || current.CanViewLogsForProfile(profileId));
        }
    }

    public PairingDecision AuthorizeViewLogs(PairedDevice device, Guid profileId,
        out PairedDevice? current)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            current = devices.SingleOrDefault(item => item.Id == device.Id && item.CredentialHash is not null);
            if (current is null)
                return new(false, "Unauthorized", "This PC's saved access was not accepted.");
            var authorization = AuthorizationDecision(current, UtcNow);
            if (!authorization.Ok) return authorization;
            return current.AssignedProfileIds!.Contains(profileId) &&
                (IsTemporaryHelperActive(current) || current.CanViewLogsForProfile(profileId))
                ? new(true, "ViewLogsAllowed", "Server-log access is allowed.")
                : new(false, "PermissionDenied",
                    "The Host has not assigned this server with View logs permission to this PC.");
        }
    }

    public PairingDecision AuthorizeReceiveSaves(PairedDevice device, Guid profileId,
        out PairedDevice? current)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            current = devices.SingleOrDefault(item => item.Id == device.Id && item.CredentialHash is not null);
            if (current is null) return new(false, "Unauthorized", "This PC's saved access was not accepted.");
            var decision = AuthorizationDecision(current, UtcNow);
            if (!decision.Ok) return decision;
            return current.AssignedProfileIds?.Contains(profileId) == true &&
                current.SharedWorldGrants?.GetValueOrDefault(profileId)?.Receive == true &&
                current.SharedWorldPublicKey is not null
                ? new(true, "ReceiveSavesAllowed", "Shared save access is allowed.")
                : new(false, "PermissionDenied", "Shared save access is not granted to this PC for this server.");
        }
    }

    public PairingDecision SetReceiveSaves(Guid deviceId, Guid profileId, bool enabled)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            if (!CanChangeSharedRoster([profileId])) return SuccessorRosterDenied();
            var device = devices.SingleOrDefault(item => item.Id == deviceId && !IsRevoked(item) &&
                item.CredentialHash is not null);
            if (device is null) return new(false, "UnknownDevice", "Connect and approve this PC first.");
            if (enabled && (device.ApprovalPending || device.AssignedProfileIds?.Contains(profileId) != true))
                return new(false, "PermissionDenied", "Approve and assign this PC to the server first.");
            device.SharedWorldGrants ??= [];
            var previous = device.SharedWorldGrants.GetValueOrDefault(profileId) ?? new();
            device.SharedWorldGrants[profileId] = previous with { Receive = enabled };
            device.SaveReceiveProfileIds ??= [];
            device.SaveReceiveProfileIds.Remove(profileId);
            if (enabled) device.SaveReceiveProfileIds.Add(profileId);
            MarkSharedRostersDirty([profileId]);
            SaveState();
            data.TryAudit($"save-receive-grant {deviceId} {profileId} enabled={enabled} {UtcNow:O}");
            Activity("Access", enabled ? "SaveReceiveGranted" : "SaveReceiveRemoved",
                enabled ? "The owner allowed a PC to receive completed saves." :
                    "The owner removed completed-save access from a PC.",
                ActivitySeverity.Important, profileId, deviceId);
            return new(true, "ReceiveSavesSaved", enabled ? "This PC may receive completed saves." :
                "This PC can no longer start new shared save reads.");
        }
    }

    public PairingDecision SetSharedWorldGrants(Guid deviceId, Guid profileId, SharedWorldGrants grants)
    {
        if (grants is null) return new(false, "InvalidGrants", "Choose the reviewed shared world grants.");
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            if (!CanChangeSharedRoster([profileId])) return SuccessorRosterDenied();
            var device = devices.SingleOrDefault(item => item.Id == deviceId && !IsRevoked(item) &&
                item.CredentialHash is not null && !item.ApprovalPending &&
                item.AssignedProfileIds?.Contains(profileId) == true);
            if (device is null) return new(false, "PermissionDenied", "Approve and assign this PC first.");
            device.SharedWorldGrants ??= [];
            device.SharedWorldGrants[profileId] = grants;
            device.SaveReceiveProfileIds ??= [];
            device.SaveReceiveProfileIds.Remove(profileId);
            if (grants.Receive) device.SaveReceiveProfileIds.Add(profileId);
            MarkSharedRostersDirty([profileId]);
            SaveState();
            return new(true, "SharedGrantsSaved", "Shared world grants saved.");
        }
    }

    public PairingDecision BindSharedWorldKey(Guid deviceId, SharedWorldEnrollmentRequest request)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(item => item.Id == deviceId && !IsRevoked(item) &&
                item.CredentialHash is not null && !item.ApprovalPending);
            if (device is null || !SharedWorldRosterTrust.VerifyEnrollment(deviceId, request))
                return new(false, "EnrollmentRejected", "This PC could not prove its signing identity.");
            if (!CanChangeSharedRoster(AffectedProfiles(device))) return SuccessorRosterDenied();
            if (device.SharedWorldPublicKey is not null && device.SharedWorldPublicKey != request.PublicKey)
                return new(false, "KeyReviewRequired", "The owner must reset this PC's shared world identity first.");
            if (devices.Any(item => item.Id != deviceId && item.SharedWorldPublicKey == request.PublicKey))
                return new(false, "KeyAlreadyBound", "This signing identity is already bound to another PC.");
            if (device.SharedWorldPublicKey == request.PublicKey)
                return new(true, "IdentityAlreadyEnrolled", "This PC's signing identity is already bound.");
            device.SharedWorldPublicKey = request.PublicKey;
            MarkSharedRostersDirty(AffectedProfiles(device));
            SaveState();
            return new(true, "IdentityEnrolled", "This PC's signing identity is bound to its device ID.");
        }
    }

    public PairingDecision ResetSharedWorldKey(Guid deviceId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(item => item.Id == deviceId && !IsRevoked(item));
            if (device is null) return new(false, "UnknownDevice", "This PC was not found.");
            var affected = AffectedProfiles(device).ToArray();
            if (!CanChangeSharedRoster(affected))
                return SuccessorRosterDenied();
            device.SharedWorldPublicKey = null;
            device.SharedWorldGrants?.Clear();
            device.SaveReceiveProfileIds?.Clear();
            MarkSharedRostersDirty(affected);
            SaveState();
            return new(true, "IdentityReset", "Shared world identity reset. Review grants after this PC enrolls again.");
        }
    }

    public IReadOnlyList<SharedWorldRosterMember> SharedRosterMembers(Guid profileId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync) return devices.Where(item => item.SharedWorldPublicKey is not null &&
                (item.AssignedProfileIds?.Contains(profileId) == true ||
                 item.SharedWorldGrants?.ContainsKey(profileId) == true))
            .Select(item => new SharedWorldRosterMember(item.Id, item.SharedWorldPublicKey!,
                item.SharedWorldGrants?.GetValueOrDefault(profileId) ?? new(),
                IsRevoked(item) || item.ApprovalPending || item.AssignedProfileIds?.Contains(profileId) != true,
                item.AccessExpiresUtc))
            .ToArray();
    }

    public PairingDecision SetName(Guid id, string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is < 1 or > 48 || value.Any(char.IsControl))
            return new PairingDecision(false, "InvalidDeviceName", "Use a name between 1 and 48 characters without line breaks.");
        lock (SharedWorldMutationGate.For(data.RootPath)) lock (sync)
        {
            var device = devices.SingleOrDefault(d => d.Id == id && !IsRevoked(d));
            if (device is null) return new PairingDecision(false, "UnknownDevice", "Friend PC was not found.");
            device.Name = value;
            SaveState();
            data.TryAudit($"device-name-change {device.Id} {UtcNow:O}");
            Activity("Access", "DeviceRenamed", "The Host renamed a connected PC.", deviceId: device.Id);
            return new PairingDecision(true, "DeviceNameSaved", "Friend PC name saved locally.");
        }
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private void Activity(string category, string action, string message,
        string severity = ActivitySeverity.Info, Guid? profileId = null,
        Guid? deviceId = null, string visibility = ActivityVisibility.Local)
    {
        try { data.RecordActivity(category, action, message, severity, profileId, deviceId, visibility); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or JsonException or NotSupportedException)
        { data.TryAudit($"activity-write-failed {category} {action} {ex.GetType().Name} {UtcNow:O}"); }
    }
    private static bool Matches(string candidate, string expectedHash)
    {
        try { return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), Convert.FromHexString(expectedHash)); }
        catch (FormatException) { return false; }
    }
}
