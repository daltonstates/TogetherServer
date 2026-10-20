using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TogetherServer;

internal sealed record LocalPairingLabInterface(OperationalStatus Status, NetworkInterfaceType Type,
    IReadOnlyList<IPAddress> UnicastAddresses);

// This lab may use one explicitly selected address already assigned to this PC.
// It never discovers a peer, changes an adapter, or admits an entire subnet.
internal sealed class LocalPairingLabNetwork
{
    internal const string LanAddressEnvironmentVariable = "TOGETHERSERVER_LAB_LAN_ADDRESS";
    private readonly IPAddress? lanAddress;
    private readonly Func<IEnumerable<LocalPairingLabInterface>> interfaces;

    internal string? SelectedLanAddress => lanAddress?.ToString();

    private LocalPairingLabNetwork(IPAddress? lanAddress,
        Func<IEnumerable<LocalPairingLabInterface>>? interfaces)
    {
        this.lanAddress = lanAddress;
        this.interfaces = interfaces ?? ReadInterfaces;
    }

    internal static LocalPairingLabNetwork Resolve(string? selectedAddress,
        Func<IEnumerable<LocalPairingLabInterface>>? interfaces = null)
    {
        if (string.IsNullOrEmpty(selectedAddress)) return new(null, interfaces);
        if (!IPAddress.TryParse(selectedAddress, out var address) ||
            !selectedAddress.Equals(address.ToString(), StringComparison.Ordinal) || !PrivateIpv4(address))
            throw new InvalidDataException("The lab LAN address must be one assigned private IPv4 address in dotted decimal form.");
        var network = new LocalPairingLabNetwork(address, interfaces);
        if (!network.AssignedLanAddress())
            throw new InvalidDataException("The lab LAN address must be assigned to an active non-loopback adapter on this PC.");
        return network;
    }

    internal bool AllowsBind(IPAddress address) =>
        !address.IsIPv4MappedToIPv6 && (IPAddress.IsLoopback(address) ||
            lanAddress is not null && address.Equals(lanAddress) && AssignedLanAddress());

    internal bool IsAllowedEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && AllowsAuthority(uri) &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    internal HttpMessageHandler GuardClient(HttpMessageHandler inner)
    {
        if (inner is HttpClientHandler transport)
        {
            transport.AllowAutoRedirect = false;
            transport.UseProxy = false;
        }
        return new AddressGuard(this, inner);
    }

    private bool AllowsAuthority(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && IPAddress.TryParse(uri.Host, out var address) && AllowsBind(address);

    private bool AssignedLanAddress()
    {
        if (lanAddress is null) return false;
        try
        {
            return interfaces().Any(adapter => adapter.Status == OperationalStatus.Up &&
                adapter.Type != NetworkInterfaceType.Loopback &&
                adapter.UnicastAddresses.Any(address => address.AddressFamily == AddressFamily.InterNetwork &&
                    address.Equals(lanAddress)));
        }
        catch (Exception error) when (error is NetworkInformationException or NotSupportedException or
            InvalidOperationException or System.Security.SecurityException)
        { return false; }
    }

    private static bool PrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168;
    }

    private static IEnumerable<LocalPairingLabInterface> ReadInterfaces()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            if (adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                yield return new(adapter.OperationalStatus, adapter.NetworkInterfaceType,
                    adapter.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address).ToArray());
    }

    private sealed class AddressGuard(LocalPairingLabNetwork network, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { IsAbsoluteUri: true } uri || !network.AllowsAuthority(uri) ||
                !string.IsNullOrEmpty(uri.Fragment))
                throw new HttpRequestException("The disposable lab can contact only loopback or its selected local LAN address.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
