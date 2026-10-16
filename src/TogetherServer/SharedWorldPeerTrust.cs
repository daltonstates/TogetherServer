using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

// The resolver is a local authorization boundary. An untrusted peer cannot
// install a resolver, grant itself a role, or replace the pinned source root.
internal sealed class SharedWorldPeerTrust(SharedWorldPeerRoot root,
    Func<Guid, string, SharedWorldPeerAccess?> resolveAccess)
{
    internal const int MaximumAnnouncementBytes = 64 * 1024;
    internal const int MaximumPageBytes = 1024 * 1024;
    internal const int PageSize = 12;
    internal const int MaximumRoutes = 4;
    internal const int MaximumBranches = 32;
    internal const int MaximumSessions = 32;
    internal const int MaximumAvailableVersions = 128;
    // Recovery proofs retain at most sixteen nested source branches. Their
    // fixed envelope and roster fields need additional bounded JSON depth.
    internal const int MaximumJsonDepth = 32;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = MaximumJsonDepth,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static bool ValidHash(string? value) => value is { Length: 64 } &&
        value.All(ch => char.IsAsciiHexDigit(ch) && !char.IsLower(ch));

    internal static bool ValidRoot(SharedWorldPeerRoot? value) => value is not null &&
        value.ProfileId != Guid.Empty && value.GroupId != Guid.Empty &&
        SharedWorldRosterTrust.ValidKey(value.OwnerPublicKey);

    internal static string RootKey(SharedWorldPeerRoot value)
    {
        if (!ValidRoot(value)) throw new InvalidDataException("The shared world identity is invalid.");
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer peer world root v1",
            value.OwnerPublicKey,
            value.ProfileId,
            value.GroupId
        }, Json)));
    }

    internal static string SignerKey(Guid deviceId, string publicKey) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { deviceId, publicKey }, Json)));

    internal SharedWorldPeerAccess? Access(Guid deviceId, string publicKey)
    {
        if (!ValidRoot(root) || !SharedWorldRosterTrust.ValidKey(publicKey)) return null;
        var access = resolveAccess(deviceId, publicKey);
        return access?.ViewMetadata == true ? access : null;
    }

    internal static SharedWorldPeerAccess? AccessFromRoster(SharedWorldPeerRoot root,
        SharedWorldRoster? roster, Guid deviceId, string publicKey, TimeProvider? clock = null)
    {
        if (!ValidRoot(root) || !SharedWorldRosterTrust.Verify(roster) ||
            roster!.OwnerPublicKey != root.OwnerPublicKey || roster.ProfileId != root.ProfileId ||
            roster.GroupId != root.GroupId) return null;
        if (deviceId == Guid.Empty)
            return publicKey == root.OwnerPublicKey ? new(true, true, true, true, true, true) : null;
        var member = WorldGroupAccess.ActiveMember(roster, deviceId, publicKey, clock);
        return member is null ? null : new(true, member.Grants.Receive, member.Grants.EligibleHost,
            member.Grants.RecoveryVoter, member.Grants.ManageSharing, member.Grants.Reshare);
    }

    // The caller supplies a head already accepted with complete signed lineage
    // by WorldAuthorityStore. Merely receiving a self-contained decision is not
    // sufficient to call this helper with that decision as the current head.
    internal static SharedWorldPeerAccess? AccessFromAcceptedHost(SharedWorldPeerRoot root,
        SharedWorldRoster? currentRoster, WorldAuthorityRecord? acceptedHead,
        Guid deviceId, string publicKey, TimeProvider? clock = null)
    {
        if (acceptedHead is null || !WorldAuthorityTrust.Verify(acceptedHead, rosterChainVerified: true) ||
            acceptedHead.Roster.OwnerPublicKey != root.OwnerPublicKey ||
            acceptedHead.Proposal.ProfileId != root.ProfileId ||
            acceptedHead.Proposal.GroupId != root.GroupId ||
            acceptedHead.Proposal.CandidatePublicKey != publicKey) return null;
        var memberKey = WorldAuthorityTrust.CandidateDevicePublicKey(acceptedHead.Proposal);
        var memberId = acceptedHead.Proposal.SuccessorBinding?.DeviceId ??
            acceptedHead.Roster.Members.SingleOrDefault(member => member.PublicKey == memberKey)?.DeviceId;
        return memberId == deviceId ? AccessFromRoster(root, currentRoster, deviceId, memberKey, clock) : null;
    }

    internal static bool ValidRoute(SharedWorldPeerRoute? route)
    {
        if (route is null || route.Address is not { Length: >= 9 and <= 255 } ||
            !ValidHash(route.CertificateSha256) ||
            !Uri.TryCreate(route.Address, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0 ||
            address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0 ||
            address.Port is < 1 or > 65535 || route.Address.Contains('%') ||
            !IPAddress.TryParse(address.Host.Trim('[', ']'), out var ip) ||
            IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            ip.IsIPv4MappedToIPv6 || ip.IsIPv6Multicast || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes[0] is 0 or >= 224 || bytes[0] == 169 && bytes[1] == 254 ||
                address.Host != ip.ToString()) return false;
        }
        return address.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static bool ValidUtc(DateTimeOffset? value) => value is null ||
        value != default(DateTimeOffset) && value.Value.Offset == TimeSpan.Zero;

    private static bool ValidBranch(SharedWorldPeerBranch? branch) => branch is not null &&
        ValidHash(branch.BranchHash) && ValidHash(branch.BaseVersionHash) &&
        ValidHash(branch.HeadVersionHash) && branch.VersionNumber >= 1 &&
        (branch.ParentVersionHash is null || ValidHash(branch.ParentVersionHash) &&
            branch.ParentVersionHash != branch.HeadVersionHash) &&
        (branch.VersionNumber == 1 ? branch.ParentVersionHash is null : branch.ParentVersionHash is not null) &&
        (branch.AuthorityHash is null || ValidHash(branch.AuthorityHash));

    private static bool ValidSession(SharedWorldPeerSession? session) => session is not null &&
        session.SessionId != Guid.Empty && session.Revision >= 1 && ValidHash(session.BranchHash) &&
        ValidHash(session.BaseVersionHash) &&
        (session.HeadVersionHash is null || ValidHash(session.HeadVersionHash)) &&
        session.State is "Preparing" or "Hosting" or "Stopped" or "Interrupted" &&
        ValidUtc(session.StartedUtc) && ValidUtc(session.EndedUtc) &&
        (session.EndedUtc is null || session.StartedUtc is { } started && session.EndedUtc >= started) &&
        (session.State is not ("Preparing" or "Hosting") || session.EndedUtc is null);

    internal static byte[] Basis(SharedWorldPeerAnnouncement value) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "TogetherServer peer world announcement v1",
            value.Schema,
            value.Root,
            value.DeviceId,
            value.PublicKey,
            value.Revision,
            value.PreviousHash,
            value.Routes,
            value.Sessions,
            value.Branches,
            value.AvailableVersionHashes,
            value.AnnouncedUtc
        }, Json);

    internal static SharedWorldPeerAnnouncement Sign(SharedWorldPeerAnnouncement draft, ECDsa key)
    {
        var value = draft with { PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) };
        var basis = Basis(value);
        var signed = value with
        {
            AnnouncementHash = Convert.ToHexString(SHA256.HashData(basis)),
            Signature = Convert.ToBase64String(key.SignData(basis, HashAlgorithmName.SHA256))
        };
        if (!VerifySignature(signed)) throw new InvalidDataException("The peer announcement is invalid.");
        return signed;
    }

    internal static bool VerifySignature(SharedWorldPeerAnnouncement? value)
    {
        try
        {
            if (value is null || value.Schema != 1 || !ValidRoot(value.Root) || value.Revision < 1 ||
                !SharedWorldRosterTrust.ValidKey(value.PublicKey) ||
                value.Revision == 1 && value.PreviousHash is not null ||
                value.Revision > 1 && !ValidHash(value.PreviousHash) ||
                value.Routes is null || value.Routes.Count > MaximumRoutes ||
                value.Routes.Any(route => !ValidRoute(route)) ||
                value.Routes.Select(route => route.Address).Distinct(StringComparer.Ordinal).Count() != value.Routes.Count ||
                value.Sessions is null || value.Sessions.Count > MaximumSessions ||
                value.Sessions.Any(session => !ValidSession(session)) ||
                value.Sessions.Select(session => session.SessionId).Distinct().Count() != value.Sessions.Count ||
                value.Branches is null || value.Branches.Count > MaximumBranches ||
                value.Branches.Any(branch => !ValidBranch(branch)) ||
                value.Branches.Select(branch => branch.BranchHash).Distinct(StringComparer.Ordinal).Count() != value.Branches.Count ||
                value.Sessions.Any(session => !value.Branches.Any(branch => branch.BranchHash == session.BranchHash &&
                    branch.BaseVersionHash == session.BaseVersionHash)) ||
                value.AvailableVersionHashes is null || value.AvailableVersionHashes.Count > MaximumAvailableVersions ||
                value.AvailableVersionHashes.Any(hash => !ValidHash(hash)) ||
                value.AvailableVersionHashes.Distinct(StringComparer.Ordinal).Count() != value.AvailableVersionHashes.Count ||
                !ValidUtc(value.AnnouncedUtc) || value.AnnouncedUtc == default ||
                !ValidHash(value.AnnouncementHash) || value.Signature is not { Length: >= 80 and <= 192 } ||
                JsonSerializer.SerializeToUtf8Bytes(value, Json).Length > MaximumAnnouncementBytes)
                return false;
            var basis = Basis(value);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value.PublicKey), out _);
            return value.AnnouncementHash == Convert.ToHexString(SHA256.HashData(basis)) &&
                key.VerifyData(basis, Convert.FromBase64String(value.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or
                                   NullReferenceException or JsonException or OverflowException)
        { return false; }
    }

    internal bool Verify(SharedWorldPeerAnnouncement? value)
    {
        if (!VerifySignature(value) || value!.Root != root ||
            Access(value.DeviceId, value.PublicKey) is not { ViewMetadata: true } access) return false;
        return (value.Branches.Count == 0 && value.Sessions.Count == 0 ||
                access is { Receive: true, EligibleHost: true }) &&
            (value.AvailableVersionHashes.Count == 0 || access.Receive);
    }

    internal static SharedWorldPeerPage DecodePage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumPageBytes) throw new InvalidDataException("Peer metadata page is oversized.");
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
            RejectDuplicates(document.RootElement);
            var page = JsonSerializer.Deserialize<SharedWorldPeerPage>(bytes, Json);
            if (page is null || !ValidRoot(page.Root) || page.Schema != 1 || page.Offset < 0 ||
                page.Total < page.Offset || page.Total > SharedWorldPeerStore.MaximumAnnouncements ||
                page.Announcements is null || page.Announcements.Count > PageSize ||
                page.Announcements.Count > page.Total - page.Offset ||
                page.Announcements.Any(value => !VerifySignature(value) || value.Root != page.Root))
                throw new InvalidDataException("Peer metadata page is invalid.");
            return page;
        }
        catch (JsonException ex) { throw new InvalidDataException("Peer metadata page is invalid.", ex); }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) throw new InvalidDataException("Peer metadata contains duplicate fields.");
                RejectDuplicates(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
