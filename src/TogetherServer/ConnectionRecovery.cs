using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;

namespace TogetherServer;

public static class ConnectionRouteModes
{
    public const string DirectInternet = "DirectInternet";
    public const string PrivateMesh = "PrivateMesh";
    public const string AdvancedAddress = "AdvancedAddress";

    public static bool Valid(string? value) => value is DirectInternet or PrivateMesh or AdvancedAddress;
}

// The address is deliberately owner-selected. Detection only offers candidates;
// TogetherServer never installs or administers a mesh or changes network policy.
public sealed class ConnectionRoute
{
    public string Mode { get; set; } = ConnectionRouteModes.DirectInternet;
    public string Address { get; set; } = "";
}

public sealed record ConnectionRouteCandidate(string Provider, string InterfaceName, string Address);
public sealed record ConnectionRouteDiscovery(IReadOnlyList<ConnectionRouteCandidate> PrivateMeshCandidates,
    IReadOnlyList<ConnectionRouteCandidate> AdvancedCandidates);

public static class ConnectionRoutes
{
    public static ConnectionRoute Normalize(ConnectionRoute? route) => route is null
        ? new ConnectionRoute()
        : new ConnectionRoute { Mode = route.Mode, Address = route.Address?.Trim() ?? "" };

    public static string DisplayName(string? mode) => mode switch
    {
        ConnectionRouteModes.PrivateMesh => "Private mesh",
        ConnectionRouteModes.AdvancedAddress => "Advanced address",
        _ => "Direct Internet"
    };

    public static string? GameAddress(HostSettings settings)
    {
        var route = Normalize(settings.ConnectionRoute);
        if (route.Mode != ConnectionRouteModes.DirectInternet)
            return ValidAddress(route.Address) ? route.Address : null;
        return settings.PublicGameIpCheckedUtc is { } checkedUtc &&
            DateTimeOffset.UtcNow - checkedUtc <= TimeSpan.FromHours(1) &&
            GameConnection.IsPublicIpv4(settings.PublicGameIp)
                ? settings.PublicGameIp : null;
    }

    public static bool ValidAddress(string? value) => IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any);

    public static ConnectionRouteDiscovery Detect()
    {
        var mesh = new List<ConnectionRouteCandidate>();
        var advanced = new List<ConnectionRouteCandidate>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            var identity = $"{adapter.Name} {adapter.Description}";
            var provider = identity.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ? "Tailscale" :
                identity.Contains("zerotier", StringComparison.OrdinalIgnoreCase) ? "ZeroTier" : null;
            foreach (var address in adapter.GetIPProperties().UnicastAddresses
                         .Select(item => item.Address)
                         .Where(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                             !IPAddress.IsLoopback(item) && !item.Equals(IPAddress.Any) &&
                             !item.ToString().StartsWith("169.254.", StringComparison.Ordinal)))
            {
                var candidate = new ConnectionRouteCandidate(provider ?? "Windows network", adapter.Name, address.ToString());
                advanced.Add(candidate);
                if (provider is not null) mesh.Add(candidate);
            }
        }
        return new(mesh.DistinctBy(item => (item.Provider, item.InterfaceName, item.Address)).ToList(),
            advanced.DistinctBy(item => (item.InterfaceName, item.Address)).ToList());
    }
}

public sealed record HostCertificateState(Guid HostId, string ActiveFingerprint,
    DateTimeOffset ActiveExpiresUtc, string? NextFingerprint, DateTimeOffset? NextExpiresUtc,
    string? PreviousFingerprint, DateTimeOffset? PreviousAcceptedUntilUtc);

public sealed record EndpointRecoveryRequest(string Endpoint);
public sealed record EndpointRecoveryProofRequest(Guid DeviceId);
public sealed record EndpointRecoveryProof(Guid HostId, string Endpoint, HostCertificateState Certificates,
    ConnectionRoute Route);
public sealed record CredentialRenewalRequest(Guid DeviceId, Guid RequestId);
public sealed record CredentialRenewal(Guid DeviceId, Guid RequestId, string Credential,
    DateTimeOffset ExpiresUtc, DateTimeOffset PreviousAcceptedUntilUtc);

internal sealed class HostIdentityMetadata
{
    public Guid HostId { get; set; } = Guid.NewGuid();
    public string? PreviousFingerprint { get; set; }
    public DateTimeOffset? PreviousAcceptedUntilUtc { get; set; }
}

internal sealed class CredentialRenewalReceipt
{
    public Guid DeviceId { get; set; }
    public Guid RequestId { get; set; }
    public string Credential { get; set; } = "";
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset PreviousAcceptedUntilUtc { get; set; }
}

public sealed record GameEndpointProbeResult(bool Answered, string Code, string Message,
    DateTimeOffset CheckedUtc, int? OnlinePlayers = null, int? MaxPlayers = null);
