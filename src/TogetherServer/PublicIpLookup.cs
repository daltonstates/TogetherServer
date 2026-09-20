using System.Net;
using System.Text;

namespace TogetherServer;

public sealed record PublicIpLookupResult(bool Ok, string? Address, string Message);

public sealed class PublicIpLookup(HttpClient client, Uri? endpoint = null)
{
    private readonly Uri endpoint = endpoint ?? new Uri("https://api.ipify.org");

    public async Task<PublicIpLookupResult> DetectAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 64)
                return new(false, null, "The public IP lookup returned an invalid response.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[65];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > 64)
                return new(false, null, "The public IP lookup returned an invalid response.");
            var body = Encoding.ASCII.GetString(bytes, 0, length).Trim();
            if (!GameConnection.IsPublicIpv4(body))
                return new(false, null, "The public IP lookup did not return a usable IPv4 address.");
            return new(true, IPAddress.Parse(body).ToString(), "Public IPv4 address detected. Incoming game and Friend app connections are not verified.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new(false, null, "Could not reach the public IP lookup. Check this PC's Internet connection and retry.");
        }
    }
}
