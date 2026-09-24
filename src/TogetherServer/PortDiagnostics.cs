using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TogetherServer;

public sealed record GamePortCheck(Guid ProfileId, string Label, IReadOnlyList<int> Ports,
    string Protocol, string State, string Detail, string RouteKind = "Direct", string Kind = "");
public sealed record LanAddressHint(string Address, string InterfaceName, string Gateway);
public sealed record ControlPortCheck(int Port, string State, string Detail,
    string RemoteState, string RemoteDetail, string BindAddress, string BindScope,
    string? Endpoint, string EndpointState, string EndpointDetail,
    IReadOnlyList<LanAddressHint> LanAddresses, string LanForwardDetail);
public sealed record PortDiagnosticsView(DateTimeOffset CheckedUtc,
    IReadOnlyList<GamePortCheck> Games, ControlPortCheck Control);

public static class PortDiagnostics
{
    public static PortDiagnosticsView Read(HostSnapshot snapshot, GameServerRegistry games,
        bool companionActive, IReadOnlyList<DeviceView> devices, string? listenerWarning = null)
    {
        var checkedUtc = DateTimeOffset.UtcNow;
        IPEndPoint[]? udp = null;
        IPEndPoint[]? tcp = null;
        string? inspectionError = null;
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            udp = properties.GetActiveUdpListeners();
            tcp = properties.GetActiveTcpListeners();
        }
        catch (NetworkInformationException ex) { inspectionError = ex.Message; }

        var gameChecks = snapshot.Settings.Profiles.Select(profile =>
        {
            if (!games.TryGet(profile.Kind, out var driver))
                return new GamePortCheck(profile.Id, profile.Name, [], "Unknown", "Unknown",
                    "The saved game driver is unavailable.", "Unknown", profile.Kind);
            var run = snapshot.Runs.Single(item => item.ProfileId == profile.Id);
            var definitions = run.DeclaredPorts is { Count: > 0 } ? run.DeclaredPorts : driver.Ports(profile);
            var ports = definitions.Select(port => port.Port).ToList();
            var protocol = string.Join(" + ", definitions.Select(port => port.Protocol).Distinct(StringComparer.OrdinalIgnoreCase));
            if (!driver.ShowPortDiagnostics)
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Not checked",
                    "The synthetic fixture has no real game network listener.", "Not applicable", profile.Kind);
            if (profile.Kind == GameKinds.Valheim && profile.Crossplay)
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol,
                    run.State == "Ready" ? "Relay ready" : run.State == "Offline" ? "Waiting" : run.State,
                    "Valheim Crossplay uses its relay, so router game-port forwarding is not required.", "Relay", profile.Kind);
            if (run.State == "Offline")
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Waiting",
                    "Start the server to check whether its game sockets open on this PC.", "Direct", profile.Kind);
            if (inspectionError is not null || udp is null || tcp is null)
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Unknown",
                    "Windows could not read its active port table" + (inspectionError is null ? "." : ": " + inspectionError),
                    "Direct", profile.Kind);
            var missing = definitions.Where(port => !IsOpen(port,
                port.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? tcp : udp)).ToList();
            if (missing.Count == 0)
            {
                var localOnly = definitions.Where(port => ListenersFor(port,
                    port.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? tcp : udp)
                    .All(endpoint => IPAddress.IsLoopback(endpoint.Address))).ToList();
                if (localOnly.Count > 0)
                    return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Loopback only",
                        $"Windows sees {string.Join(", ", localOnly.Select(PortName))} listening only on this PC. Check the game's bind settings before trying a Friend join.",
                        "Direct", profile.Kind);
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Open on PC",
                    $"Windows sees {string.Join(", ", definitions.Select(PortName))} listening locally. Public forwarding still needs a Friend join test.",
                    "Direct", profile.Kind);
            }
            if (run.State is "Starting" or "Process running")
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Opening",
                    "The process is starting; waiting for " + string.Join(", ", missing.Select(PortName)) + ".",
                    "Direct", profile.Kind);
            return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Closed on PC",
                "The server reports ready, but Windows does not see " +
                string.Join(", ", missing.Select(PortName)) + " listening.", "Direct", profile.Kind);
        }).ToList();

        var settings = snapshot.Settings;
        var controlPort = settings.CompanionPort;
        var bindAddress = settings.CompanionBindAddress;
        var validBind = IPAddress.TryParse(bindAddress, out var bind);
        var bindScope = !validBind ? "Invalid" : IPAddress.IsLoopback(bind!) ? "Loopback only" :
            bind!.Equals(IPAddress.Any) ? "All IPv4 interfaces" :
            bind.Equals(IPAddress.IPv6Any) ? "All IPv6 interfaces" : "One interface";
        var controlOpen = companionActive && validBind && tcp?.Any(endpoint =>
            endpoint.Port == controlPort && endpoint.Address.Equals(bind)) == true;
        var (controlState, controlDetail) = ControlStatus(settings.CompanionListeningEnabled,
            companionActive, controlOpen, controlPort, bindAddress, bindScope, inspectionError, listenerWarning);
        var lastFriend = controlOpen ? devices.Where(device => !device.Revoked && device.Paired &&
                device.CredentialExpiresUtc > checkedUtc && device.LastHeartbeatUtc is { } receivedUtc &&
                receivedUtc <= checkedUtc && checkedUtc - receivedUtc <= TimeSpan.FromSeconds(45))
            .Select(device => device.LastHeartbeatUtc!.Value).DefaultIfEmpty().Max() : default;
        var remoteState = lastFriend == default ? "Not verified" : "Friend connected";
        var remoteDetail = lastFriend == default
            ? "No paired Friend has a current authenticated heartbeat. Test pairing from a PC outside this network to verify that route."
            : $"A paired Friend sent a heartbeat at {lastFriend.ToLocalTime():t}. Its network location is unknown; an outside-network test is still needed.";
        var (endpointState, endpointDetail) = EndpointStatus(settings, checkedUtc);
        var (lanAddresses, lanForwardDetail) = ReadLanAddresses();

        return new(checkedUtc, gameChecks, new(controlPort, controlState, controlDetail,
            remoteState, remoteDetail, bindAddress, bindScope,
            string.IsNullOrWhiteSpace(settings.CompanionEndpoint) ? null : settings.CompanionEndpoint,
            endpointState, endpointDetail, lanAddresses, lanForwardDetail));
    }

    private static (string State, string Detail) ControlStatus(bool enabled, bool active, bool open,
        int port, string bindAddress, string bindScope, string? inspectionError, string? listenerWarning)
    {
        if (!enabled)
            return ("Off", "Friend connections are off. Create or copy an invite to enable the HTTPS listener.");
        if (!active)
            return ("Not listening", string.IsNullOrWhiteSpace(listenerWarning)
                ? "Friend connections are enabled, but the HTTPS listener is not running. Check the Host connection warning."
                : listenerWarning);
        if (inspectionError is not null)
            return ("Unknown", "Windows could not read its active TCP listeners: " + inspectionError);
        if (!open)
            return ("Closed on PC", $"TogetherServer expected HTTPS TCP {port} on {bindAddress}, but Windows does not report that listener.");
        if (bindScope == "Loopback only")
            return ("Open on PC", $"HTTPS TCP {port} is listening only on this PC ({bindAddress}). A Friend outside this PC cannot reach this bind.");
        return ("Open on PC", $"HTTPS TCP {port} is listening on {bindAddress}. This is a local check; Windows Firewall and router forwarding are not verified.");
    }

    private static (string State, string Detail) EndpointStatus(HostSettings settings, DateTimeOffset checkedUtc)
    {
        if (string.IsNullOrWhiteSpace(settings.CompanionEndpoint))
            return ("Not configured", "Create an invite after checking the public IP address to set the Friend app address.");
        if (!HostIdentity.TryEndpoint(settings.CompanionEndpoint, out var endpoint) ||
            endpoint.Port != settings.CompanionPort)
            return ("Invalid", "The HTTPS endpoint and companion port do not match. Check Settings and safety.");
        if (!GameConnection.IsPublicIpv4(endpoint.Host))
            return ("Local only", "The invite endpoint needs a public IPv4 address for an outside-network Friend.");
        if (settings.PublicGameIpCheckedUtc is not { } addressCheckedUtc ||
            addressCheckedUtc > checkedUtc || checkedUtc - addressCheckedUtc > TimeSpan.FromHours(1) ||
            !GameConnection.IsPublicIpv4(settings.PublicGameIp))
            return ("Address stale", "Check the public IP again. An old address may no longer lead to this Host.");
        if (!string.Equals(endpoint.Host, settings.PublicGameIp, StringComparison.OrdinalIgnoreCase))
            return ("Address differs", "The invite address differs from this PC's recent outbound public IP. Verify the router's WAN address and endpoint.");
        return ("Address hint", "The invite matches a recent outbound public IP lookup. It does not verify Windows Firewall, router forwarding, or the Friend route. Compare the router's WAN address to check for shared-address NAT.");
    }

    private static (IReadOnlyList<LanAddressHint> Addresses, string Detail) ReadLanAddresses()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(adapter => (adapter.Name, Properties: adapter.GetIPProperties()))
                .SelectMany(adapter => adapter.Properties.GatewayAddresses
                    .Select(gateway => gateway.Address)
                    .Where(gateway => gateway.AddressFamily == AddressFamily.InterNetwork &&
                        !gateway.Equals(IPAddress.Any) && !IPAddress.IsLoopback(gateway))
                    .SelectMany(gateway => adapter.Properties.UnicastAddresses
                        .Select(unicast => unicast.Address)
                        .Where(IsPrivateLanIpv4)
                        .Select(address => new LanAddressHint(address.ToString(), adapter.Name, gateway.ToString()))))
                .Distinct()
                .OrderBy(candidate => candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Address, StringComparer.Ordinal)
                .ToList();
            var detail = candidates.Count switch
            {
                0 => "No active private IPv4 address with an IPv4 gateway was found. Check this PC's network adapter and router LAN before forwarding.",
                1 => "This PC's active private IPv4 address is a possible router forwarding target. Point only the approved HTTPS TCP port at it, then run an outside-network check.",
                _ => "Several active private IPv4 addresses could be router forwarding targets. Choose the adapter connected to your router's LAN, then run an outside-network check."
            };
            return (candidates, detail);
        }
        catch (NetworkInformationException)
        {
            return ([], "Windows could not inspect active network adapters. Check the router LAN target manually.");
        }
    }

    private static bool IsPrivateLanIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168;
    }

    private static bool IsOpen(GamePort port, IPEndPoint[] listeners) => ListenersFor(port, listeners).Any();

    private static IEnumerable<IPEndPoint> ListenersFor(GamePort port, IPEndPoint[] listeners) =>
        listeners.Where(listener => listener.Port == port.Port &&
            (port.Family == "Any" || listener.Address.AddressFamily ==
                (port.Family == "IPv6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork)));

    private static string PortName(GamePort port) =>
        $"{port.Protocol} {port.Port}{(port.Family == "Any" ? "" : " " + port.Family)}";
}
