using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Fake HTTP and synthetic protected files only. No app, listener or desktop.
internal static class FriendRevocationVisibilityChecks
{
    internal static async Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "friend-revoked-" + Guid.NewGuid().ToString("N")));
        const string file = "revoked-friend.protected";
        var configuration = new FriendConfiguration
        {
            Endpoint = "https://127.0.0.1:5131",
            Fingerprint = new string('A', 64),
            Credential = new string('C', 64),
            DeviceId = Guid.NewGuid(),
            HostId = Guid.NewGuid(),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90)
        };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        void SaveFresh() => data.SaveProtected(file, JsonSerializer.SerializeToUtf8Bytes(configuration, json));
        SaveFresh();
        var handler = new DenialHandler();
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint == configuration.Endpoint && pins.Contains(configuration.Fingerprint),
                "revocation was requested without the saved pinned transport");
            return new(handler, disposeHandler: false) { BaseAddress = new Uri(endpoint) };
        }
        using (var link = new FriendLink(data, file, Client))
        {
            foreach (var mode in new[] { "generic403", "401", "offline", "invalid-revoked" })
            {
                handler.Mode = mode;
                var view = await link.PollAsync();
                Require(view.ConnectionCode != "Revoked" &&
                    data.LoadProtectedJson<FriendConfiguration>(file)?.KnownRevoked == false,
                    "an unproven denial or transport failure inferred revocation");
            }
            handler.Mode = "revoked";
            var revoked = await link.PollAsync();
            Require(revoked.State == "Revoked" && revoked.ConnectionCode == "Revoked" &&
                revoked.Profiles.Count == 0 && !revoked.RemoteControlsEnabled &&
                data.LoadProtectedJson<FriendConfiguration>(file)?.KnownRevoked == true,
                "explicit pinned revocation was not learned and saved");
        }
        using (var restarted = new FriendLink(data, file, Client))
        {
            Require(restarted.View().State == "Revoked" && restarted.View().ConnectionCode == "Revoked",
                "restart temporarily exposed a revoked Host as disconnected");
            foreach (var mode in new[] { "401", "generic403", "offline", "success" })
            {
                handler.Mode = mode;
                var calls = handler.Calls;
                var view = await restarted.PollAsync();
                Require(view.State == "Revoked" && view.ConnectionCode == "Revoked" &&
                    view.Profiles.Count == 0 && handler.Calls == calls,
                    "a refresh revived a revoked saved Host or retried its old credential");
            }
        }
        SaveFresh(); // A fresh pairing configuration resets the learned marker.
        using var fresh = new FriendLink(data, file, Client);
        Require(fresh.View().ConnectionCode != "Revoked", "fresh pairing inherited old revocation");
    }

    private sealed class DenialHandler : HttpMessageHandler
    {
        internal string Mode = "generic403";
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Mode == "offline") throw new HttpRequestException("Synthetic offline Host.");
            return Task.FromResult(new HttpResponseMessage(Mode == "401" ? HttpStatusCode.Unauthorized :
                Mode == "success" ? HttpStatusCode.OK : HttpStatusCode.Forbidden)
            {
                Content = Mode == "generic403" ? new StringContent("Forbidden") :
                    JsonContent.Create(new PairingDecision(Mode == "invalid-revoked", "Revoked", "Synthetic denial."))
            });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
