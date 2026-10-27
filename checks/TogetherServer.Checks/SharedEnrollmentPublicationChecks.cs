using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using TogetherServer;

// Hosted general fixtures only. These are the actual companion routes over
// TestServer and generated protected state; no game or network socket is used.
internal static class SharedEnrollmentPublicationChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent, string fixture)
    {
        using var data = new LocalData(Path.Combine(parent, "enrollment-publication-" + Guid.NewGuid().ToString("N")));
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            WorldId = "EnrollmentPublication",
            WorldDirectory = Path.Combine(data.RootPath, "world"),
            ExecutablePath = fixture,
            SharedSavesEnabled = true,
            Backups = new() { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        Directory.CreateDirectory(profile.WorldDirectory);
        const string endpoint = "https://127.0.0.1:51388";
        var settings = new HostSettings
        {
            Profiles = [profile],
            CompanionEndpoint = endpoint,
            CompanionPort = 51388,
            CompanionBindAddress = "127.0.0.1",
            CompanionListeningEnabled = true
        };
        data.SaveSettings(settings);
        using var certificate = new HostIdentity(data).Ensure(endpoint);
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = Device(profile.Id);
        var second = Device(profile.Id);
        var unassigned = Device();
        var expired = Device(profile.Id);
        expired.Record.AccessExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var revoked = Device(profile.Id);
        revoked.Record.Revoked = true;
        data.SavePairingState(new PairingPersistentState
        { Devices = [first.Record, second.Record, unassigned.Record, expired.Record, revoked.Record] });
        var pairing = new PairingService(data);
        var games = new GameServerRegistry(data, true, PortProbeMode.ObserveOnly);
        var manager = new HostManager(data, games, TimeProvider.System, new NullHostingPowerGuard(), pairing: pairing);
        var initial = await manager.PublishSimpleSharedHostingRosterAsync(profile.Id);
        pairing.ConfirmSharedRosterPublished(profile.Id, initial);
        using var gate = new SemaphoreSlim(1, 1);
        var companion = new CompanionServer(data, manager, pairing, games, new ServerLogService(data, manager),
            gate, 51390, inMemoryTransport: builder => builder.UseTestServer());
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseTestServer();
        await using var app = companion.BuildInMemoryApp(builder, settings, new Uri(endpoint));
        await app.StartAsync();
        HttpClient Client((PairedDevice Record, string Credential) device)
        {
            var client = app.GetTestServer().CreateClient();
            client.BaseAddress = new Uri(endpoint + "/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", device.Credential);
            client.DefaultRequestHeaders.Add("X-Device-Id", device.Record.Id.ToString());
            return client;
        }
        using var firstClient = Client(first);
        using var secondClient = Client(second);
        using var unassignedClient = Client(unassigned);
        using var expiredClient = Client(expired);
        using var revokedClient = Client(revoked);
        var path = $"api/companion/servers/{profile.Id}/shared-world/enrollment";
        var firstChallenge = await firstClient.GetFromJsonAsync<SharedWorldEnrollmentChallenge>(path, Json);
        var secondChallenge = await secondClient.GetFromJsonAsync<SharedWorldEnrollmentChallenge>(path, Json);
        Require(firstChallenge is not null && secondChallenge is not null, "the initial authorized enrollment challenges were unavailable");
        var firstProof = Proof(first.Record.Id, firstKey, firstChallenge!.Nonce);
        var secondProof = Proof(second.Record.Id, secondKey, secondChallenge!.Nonce);

        // This is the exact canonical step performed by PC1's enrollment POST.
        // Hold its signed publication, leaving the real durable dirty fence in
        // place while PC2 enters the actual companion GET and POST routes.
        Require(pairing.BindSharedWorldKey(first.Record.Id, firstProof).Ok && pairing.SharedRosterDirty(profile.Id),
            "the first generated PC did not create the actual pending signed-publication fence");
        var publish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = Task.Run(async () =>
        {
            await publish.Task;
            var roster = await manager.PublishSimpleSharedHostingRosterAsync(profile.Id);
            pairing.ConfirmSharedRosterPublished(profile.Id, roster);
            return roster;
        });
        try
        {
            using var pendingGet = await secondClient.GetAsync(path);
            using var pendingPost = await secondClient.PostAsJsonAsync(path, secondProof, Json);
            Require(pendingGet.StatusCode == HttpStatusCode.ServiceUnavailable &&
                pendingPost.StatusCode == HttpStatusCode.ServiceUnavailable &&
                await FixedUnavailableAsync(pendingGet) && await FixedUnavailableAsync(pendingPost) && !publication.IsCompleted,
                "a current authorized second PC's enrollment was reported as denied while PC1's signed publication was pending");
            Require(data.LoadPairingState().Devices.Single(item => item.Id == second.Record.Id).SharedWorldPublicKey is null,
                "the temporary publication response enrolled the second PC or consumed its proof as success");
            foreach (var denied in new[] { unassignedClient, expiredClient, revokedClient })
            {
                using var response = await denied.GetAsync(path);
                Require(response.StatusCode == HttpStatusCode.Forbidden,
                    "a temporary publication response disclosed the fence before current assignment/expiry/revocation authorization");
            }
        }
        finally { publish.TrySetResult(true); await publication; }
        Require(!pairing.SharedRosterDirty(profile.Id) && SharedWorldRosterTrust.Verify(publication.Result),
            "the first generated signed publication did not finish under the existing verification gate");
        // Neither a refused dirty POST nor a refused dirty GET may consume or
        // replace PC2's previously issued current nonce.
        using var accepted = await secondClient.PostAsJsonAsync(path, secondProof, Json);
        Require(accepted.IsSuccessStatusCode && !pairing.SharedRosterDirty(profile.Id) &&
            data.LoadPairingState().Devices.Single(item => item.Id == second.Record.Id).SharedWorldPublicKey == secondProof.PublicKey,
            "a temporary publication refusal lost the second PC's current nonce or prevented normal verified enrollment");
        using var replay = await secondClient.PostAsJsonAsync(path, secondProof, Json);
        Require(replay.StatusCode == HttpStatusCode.Forbidden, "an accepted enrollment proof was replayable after publication");
        var fresh = await secondClient.GetFromJsonAsync<SharedWorldEnrollmentChallenge>(path, Json);
        Require(fresh is not null, "a fresh current nonce was not issued after enrollment");
        using var wrong = await secondClient.PostAsJsonAsync(path,
            Proof(second.Record.Id, secondKey, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), Json);
        Require(wrong.StatusCode == HttpStatusCode.Forbidden, "a different nonce bypassed the existing current proof check");
        using var current = await secondClient.PostAsJsonAsync(path, Proof(second.Record.Id, secondKey, fresh!.Nonce), Json);
        Require(current.IsSuccessStatusCode, "a wrong nonce consumed another current legitimate challenge");
        Require(data.LoadRuns().Count == 0, "enrollment publication checks created a managed game run");
    }

    private static async Task<bool> FixedUnavailableAsync(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 1 &&
            value.GetProperty("code").GetString() == "RosterUnavailable";
    }
    private static (PairedDevice Record, string Credential) Device(params Guid[] profiles)
    {
        var credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (new()
        {
            Id = Guid.NewGuid(),
            AssignedProfileIds = [.. profiles],
            CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
        }, credential);
    }
    private static SharedWorldEnrollmentRequest Proof(Guid id, ECDsa key, string nonce)
    {
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return new(nonce, publicKey, Convert.ToBase64String(key.SignData(
            SharedWorldRosterTrust.EnrollmentBasis(id, nonce, publicKey), HashAlgorithmName.SHA256)));
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
