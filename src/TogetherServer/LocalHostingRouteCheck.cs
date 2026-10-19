using System.Net;
using System.Security.Cryptography;

namespace TogetherServer;

internal sealed record LocalHostingRouteSnapshot(string Endpoint, int ControlPort,
    string BindAddress, bool Enabled, bool Active, string ListenerState,
    string? CertificateFingerprint, bool ListenerOwned);

internal sealed record LocalHostingRouteEvidence(string Code, string State, string Message,
    DateTimeOffset CheckedUtc, LocalHostingRouteSnapshot Identity)
{
    internal LocalHostingStep Step => new("connection", State, Message);
}

// Only explicit Prepare calls CheckAsync. CurrentAsync projects a bounded,
// unchanged observation without making a network request or enabling a listener.
internal sealed class LocalHostingRouteCheck(
    Func<CancellationToken, Task<LocalHostingRouteSnapshot>> readSnapshot,
    Func<string, int, CancellationToken, Task<ExternalPortProbeResult>> probe,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private LocalHostingRouteEvidence? latest;
    private static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(5);

    internal static LocalHostingRouteCheck Create(LocalData data, HostManager manager,
        CompanionServer companion, ExternalPortProbe externalProbe) => new(async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = (await manager.CompanionSnapshotAsync()).Settings;
            cancellationToken.ThrowIfCancellationRequested();
            string? fingerprint = null;
            try
            {
                using var certificate = new HostIdentity(data).Load();
                if (certificate is { HasPrivateKey: true }) fingerprint = HostIdentity.Fingerprint(certificate);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
            { /* Missing identity cannot become an owned, externally checked listener. */ }
            return new(settings.CompanionEndpoint, settings.CompanionPort, settings.CompanionBindAddress,
                settings.CompanionListeningEnabled, companion.Active, companion.ListenerState, fingerprint,
                fingerprint is not null && companion.OwnsListener(settings.CompanionEndpoint,
                    settings.CompanionPort, fingerprint, settings.CompanionBindAddress));
        }, (endpoint, port, cancellationToken) =>
            externalProbe.CheckAsync(endpoint, port).WaitAsync(cancellationToken));

    internal async Task<LocalHostingRouteEvidence> CurrentAsync(CancellationToken cancellationToken = default)
    {
        var current = await readSnapshot(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var observation = Volatile.Read(ref latest);
        var now = clock.GetUtcNow();
        if (observation is not null && observation.Identity == current && observation.CheckedUtc <= now &&
            now - observation.CheckedUtc <= EvidenceLifetime) return observation;
        return DescribeLocal(current) ?? Evidence(current, "OutsideCheckNotRun", "Unknown",
            "The local HTTPS listener is configured. Prepare this PC to check its public address and outside TCP port. Pinned pairing and game joins remain unverified.");
    }

    internal async Task<LocalHostingRouteEvidence> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captured = await readSnapshot(cancellationToken);
        var local = DescribeLocal(captured);
        LocalHostingRouteEvidence observation;
        if (local is not null) observation = local;
        else
        {
            var startedUtc = clock.GetUtcNow();
            ExternalPortProbeResult? outside = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                outside = await probe(captured.Endpoint, captured.ControlPort, timeout.Token).WaitAsync(timeout.Token);
            }
            catch (Exception error) when (error is HttpRequestException or IOException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { /* A failed optional checker supplies no negative or positive route proof. */ }
            cancellationToken.ThrowIfCancellationRequested();
            observation = DescribeOutside(captured, outside, startedUtc);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var current = await readSnapshot(cancellationToken);
        if (current != captured)
            return Evidence(current, "LocalRouteChanged", "Unknown",
                "This PC's connection address or listener changed during preparation. Retry the checks for the current route; incoming access remains unverified.");
        Volatile.Write(ref latest, observation);
        return observation;
    }

    private LocalHostingRouteEvidence? DescribeLocal(LocalHostingRouteSnapshot route)
    {
        if (string.IsNullOrWhiteSpace(route.Endpoint))
            return Evidence(route, "LocalAddressRequired", "ActionRequired",
                "No Friend connection address is configured on this PC. Local hosting is available; configure an address before inviting others.");
        if (route.ControlPort is < 1024 or > 65535 || !HostIdentity.TryEndpoint(route.Endpoint, out var endpoint) ||
            endpoint.Port != route.ControlPort)
            return Evidence(route, "LocalAddressInvalid", "ActionRequired",
                "The saved HTTPS IP address and Friend control port do not match. Review this PC's connection settings before inviting others.");
        if (!route.Enabled)
            return Evidence(route, "LocalListenerOff", "ActionRequired",
                "Friend connections are off on this PC. Preparation keeps them off; local hosting is available, and inviting others requires enabling this PC's connection.");
        if (!IPAddress.TryParse(route.BindAddress, out var bind))
            return Evidence(route, "LocalBindInvalid", "ActionRequired",
                "This PC's Friend listener has an invalid bind address. Review connection settings; incoming access has not been checked.");
        if (IPAddress.IsLoopback(bind) || IPAddress.IsLoopback(IPAddress.Parse(endpoint.Host)))
            return Evidence(route, "LocalConnectionOnly", "ActionRequired",
                "The saved Friend address or listener is limited to this PC. Local hosting is available; another PC cannot use this loopback route.");
        if (!route.Active || route.ListenerState != CompanionListenerStates.Listening)
            return Evidence(route, "LocalListenerNotRunning", "ActionRequired",
                "Friend connections are enabled, but this PC's HTTPS listener is not running. Incoming access remains unverified; review the connection warning.");
        if (!route.ListenerOwned || route.CertificateFingerprint is not { Length: 64 } fingerprint ||
            !fingerprint.All(Uri.IsHexDigit))
            return Evidence(route, "LocalListenerUnverified", "Unknown",
                "The current HTTPS listener could not be matched to this PC's saved identity. Incoming access has not been checked.");
        if (!GameConnection.IsPublicIpv4(endpoint.Host))
            return Evidence(route, "PrivateRouteOnly", "Unknown",
                "This PC has a local or private-network HTTPS listener. The public IPv4 checker does not test this route; check it from an approved Friend PC on that network. Game joins remain unverified.");
        return null;
    }

    private LocalHostingRouteEvidence DescribeOutside(LocalHostingRouteSnapshot route,
        ExternalPortProbeResult? outside, DateTimeOffset startedUtc)
    {
        var now = clock.GetUtcNow();
        if (outside is null || outside.Endpoint != route.Endpoint || outside.Port != route.ControlPort ||
            outside.CheckedUtc < startedUtc - TimeSpan.FromMinutes(1) || outside.CheckedUtc > now + TimeSpan.FromSeconds(1))
            return Evidence(route, "OutsideCheckInconclusive", "Unknown",
                "The optional outside TCP check did not return current evidence for this PC's saved route. Retry or check from an approved Friend PC; game joins remain unverified.");
        return outside.State switch
        {
            "Reachable" => Evidence(route, "OutsideTcpReachable", "Ready",
                $"The public address matched and an outside checker reached Friend TCP port {route.ControlPort}. This verifies TCP reachability only; pinned HTTPS pairing and the game route still need a Friend check."),
            "Not reachable" => Evidence(route, "OutsideTcpNotReachable", "ActionRequired",
                $"The public address matched, but an outside checker could not reach Friend TCP port {route.ControlPort}. Review this PC's listener, firewall, router forwarding and ISP route; no network settings were changed."),
            "Unavailable" => Evidence(route, "OutsideAddressUnverified", "ActionRequired",
                "The outside checker could not match a valid public IPv4 address to this PC's saved Friend address. Review the address before retrying; this does not prove that the port is closed."),
            _ => Evidence(route, "OutsideCheckInconclusive", "Unknown",
                "The optional outside TCP check was inconclusive. Retry or check from an approved Friend PC; pinned pairing and game joins remain unverified.")
        };
    }

    private LocalHostingRouteEvidence Evidence(LocalHostingRouteSnapshot route, string code,
        string state, string message) => new(code, state, message, clock.GetUtcNow(), route);
}
