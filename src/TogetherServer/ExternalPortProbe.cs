using System.Net;
using System.Text;

namespace TogetherServer;

public sealed record ExternalPortProbeResult(string State, string Detail, int Port, DateTimeOffset CheckedUtc,
    string? Endpoint = null);

// An owner-requested, read-only TCP check from a service outside the home network.
// The service checks the caller's public IPv4 address; it never receives an invite.
public sealed class ExternalPortProbe(HttpClient client, Uri? serviceRoot = null)
{
    private readonly Uri serviceRoot = serviceRoot ?? new Uri("https://portchecker.io/");

    public async Task<ExternalPortProbeResult> CheckAsync(string advertisedEndpoint, int port)
    {
        ExternalPortProbeResult Result(string state, string detail, string? endpoint = null) =>
            new(state, detail, port, DateTimeOffset.UtcNow, endpoint);

        if (port is < 1024 or > 65535 ||
            !HostIdentity.TryEndpoint(advertisedEndpoint, out var endpoint) ||
            endpoint.Port != port || !GameConnection.IsPublicIpv4(endpoint.Host))
            return Result("Unavailable", "Set a public IPv4 HTTPS Friend address and matching TCP port first.");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var seenAddress = await GetShortTextAsync(new Uri(serviceRoot, "api/me"), timeout.Token);
            if (!IPAddress.TryParse(seenAddress, out var observed) ||
                !observed.Equals(IPAddress.Parse(endpoint.Host)))
                return Result("Unavailable", "The outside checker sees a different public IPv4 address than the Friend invite. Refresh the public address and review the Host endpoint.", advertisedEndpoint);

            var status = await GetShortTextAsync(new Uri(serviceRoot, $"api/me/{port}"), timeout.Token);
            return status switch
            {
                "True" => Result("Reachable", $"An outside TCP checker reached port {port}. A Friend still needs to verify pinned HTTPS pairing.", advertisedEndpoint),
                "False" => Result("Not reachable", $"An outside TCP checker could not reach port {port}. The router forward, Windows Firewall, ISP filtering, or shared-address NAT may block it. Compare the router WAN address with the invite address, then retry.", advertisedEndpoint),
                _ => Result("Inconclusive", "The outside checker returned an unexpected result. Retry with the Host app running.", advertisedEndpoint)
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return Result("Inconclusive", "The outside TCP checker did not complete. Check the Internet connection and retry.", advertisedEndpoint);
        }
    }

    private async Task<string> GetShortTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 64)
            throw new IOException("The checker response was too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[65];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length > 64) throw new IOException("The checker response was too large.");
        return Encoding.ASCII.GetString(bytes, 0, length).Trim();
    }
}
