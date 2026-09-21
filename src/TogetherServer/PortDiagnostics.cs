using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace TogetherServer;

public sealed record GamePortCheck(Guid ProfileId, string Label, IReadOnlyList<int> Ports,
    string Protocol, string State, string Detail);
public sealed record ControlPortCheck(int Port, string State, string Detail,
    string RemoteState, string RemoteDetail);
public sealed record PortDiagnosticsView(DateTimeOffset CheckedUtc,
    IReadOnlyList<GamePortCheck> Games, ControlPortCheck Control);

public static class PortDiagnostics
{
    public static PortDiagnosticsView Read(HostSnapshot snapshot, GameServerRegistry games,
        bool companionActive, IReadOnlyList<DeviceView> devices)
    {
        HashSet<(AddressFamily Family, int Port)>? udp = null;
        HashSet<(AddressFamily Family, int Port)>? tcp = null;
        string? inspectionError = null;
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            udp = properties.GetActiveUdpListeners().Select(endpoint => (endpoint.Address.AddressFamily, endpoint.Port)).ToHashSet();
            tcp = properties.GetActiveTcpListeners().Select(endpoint => (endpoint.Address.AddressFamily, endpoint.Port)).ToHashSet();
        }
        catch (NetworkInformationException ex) { inspectionError = ex.Message; }

        var gameChecks = snapshot.Settings.Profiles.Select(profile =>
        {
            if (!games.TryGet(profile.Kind, out var driver))
                return new GamePortCheck(profile.Id, profile.Name, [], "Unknown", "Unknown",
                    "The saved game driver is unavailable.");
            var run = snapshot.Runs.Single(item => item.ProfileId == profile.Id);
            var definitions = run.DeclaredPorts is { Count: > 0 } ? run.DeclaredPorts : driver.Ports(profile);
            var ports = definitions.Select(port => port.Port).ToList();
            var protocol = string.Join(" + ", definitions.Select(port => port.Protocol).Distinct(StringComparer.OrdinalIgnoreCase));
            if (!driver.ShowPortDiagnostics)
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Not checked",
                    "The synthetic fixture has no real game network listener.");
            if (profile.Kind == GameKinds.Valheim && profile.Crossplay)
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol,
                    run.State == "Ready" ? "Relay ready" : run.State == "Offline" ? "Waiting" : run.State,
                    "Valheim Crossplay uses its relay, so router game-port forwarding is not required.");
            if (run.State == "Offline")
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Waiting",
                    "Start the server to check whether its game sockets open on this PC.");
            if (inspectionError is not null || udp is null || tcp is null)
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Unknown",
                    "Windows could not read its active port table" + (inspectionError is null ? "." : ": " + inspectionError));
            var missing = definitions.Where(port => !IsOpen(port,
                port.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? tcp : udp)).ToList();
            if (missing.Count == 0)
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Open on PC",
                    $"Windows sees {string.Join(", ", definitions.Select(PortName))} listening locally. Public forwarding still needs a Friend join test.");
            if (run.State is "Starting" or "Process running")
                return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Opening",
                    "The process is starting; waiting for " + string.Join(", ", missing.Select(PortName)) + ".");
            return new GamePortCheck(profile.Id, profile.Name, ports, protocol, "Closed on PC",
                "The server reports ready, but Windows does not see " +
                string.Join(", ", missing.Select(PortName)) + " listening.");
        }).ToList();

        var controlPort = snapshot.Settings.CompanionPort;
        var controlOpen = companionActive && tcp?.Any(endpoint => endpoint.Port == controlPort) == true;
        var controlState = !companionActive ? "Off" : inspectionError is not null ? "Unknown" :
            controlOpen ? "Open on PC" : "Closed on PC";
        var controlDetail = !companionActive
            ? "Create or copy an invite to allow authenticated Friend app connections."
            : inspectionError is not null
                ? "Windows could not read its active TCP listeners: " + inspectionError
                : controlOpen
                    ? $"TogetherServer is listening on TCP {controlPort} on this PC."
                    : $"TogetherServer expected TCP {controlPort}, but Windows does not report a listener.";
        var lastFriend = devices.Where(device => !device.Revoked && device.Paired && device.LastHeartbeatUtc is not null)
            .Select(device => device.LastHeartbeatUtc!.Value).DefaultIfEmpty().Max();
        var remoteState = lastFriend == default ? "Not verified" : "Friend reached";
        var remoteDetail = lastFriend == default
            ? "A connection from a Friend PC is still needed to prove the route beyond this PC."
            : $"An authenticated Friend app reached this Host at {lastFriend.ToLocalTime():t}.";

        return new(DateTimeOffset.UtcNow, gameChecks,
            new(controlPort, controlState, controlDetail, remoteState, remoteDetail));
    }

    private static bool IsOpen(GamePort port, HashSet<(AddressFamily Family, int Port)> listeners) =>
        listeners.Any(listener => listener.Port == port.Port &&
            (port.Family == "Any" || listener.Family ==
                (port.Family == "IPv6" ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork)));

    private static string PortName(GamePort port) =>
        $"{port.Protocol} {port.Port}{(port.Family == "Any" ? "" : " " + port.Family)}";
}
