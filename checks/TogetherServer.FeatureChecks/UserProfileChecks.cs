using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Synthetic protected state, in-memory HttpContext and injected HTTP only.
// No process, listener, game, browser or desktop interaction.
internal static class UserProfileChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        CheckNamesAndStorage(root);
        await CheckLocalRoutesAsync(root);
        CheckPairing(root);
        await CheckFriendAsync(root);
    }

    private static void CheckNamesAndStorage(string root)
    {
        using var data = Data(root, "profile-store");
        Require(data.UserProfile.View().DisplayName == "", "a fresh profile inferred a personal name");
        Require(UserProfileName.TryNormalize(null, out var absent) && absent is null,
            "older-peer absence was rejected");
        Require(data.UserProfile.Save("  Alice 😀  ").Ok && data.UserProfile.View().DisplayName == "Alice 😀",
            "a valid name was not trimmed");
        Require(new UserProfileStore(data).View().DisplayName == "Alice 😀" && data.HasProtected("user-profile.protected"),
            "the name did not survive protected storage reload");
        foreach (var value in new[] { "a\nb", "a\rb", "a\tb", "a\0b", "a\u200Bb", "a\u202Eb", "a\u2028b", "a\u2029b", "a\uD800", "\uDC00a", new string('a', 81), new string('a', 79) + "😀" })
        {
            Require(!UserProfileName.TryNormalize(value, out _) && !data.UserProfile.Save(value).Ok,
                "an unsafe or oversized name was accepted");
            Require(data.UserProfile.View().DisplayName == "Alice 😀", "invalid input changed the name");
        }
        Require(!data.UserProfile.Save(null).Ok, "local null was confused with an explicit clear");
        Require(data.UserProfile.Save(new string('a', 80)).Ok, "the exact UTF-16 name limit was rejected");
        Require(!data.UserProfile.Save("   ").Ok && new UserProfileStore(data).View().DisplayName == new string('a', 80),
            "a blank bypassed required profile setup or erased the saved name");
    }

    private static async Task CheckLocalRoutesAsync(string root)
    {
        using var data = Data(root, "profile-routes");
        data.UserProfile.Save("Before");
        using var gate = new SemaphoreSlim(1);
        foreach (var json in new[] { "{}", "null", "[]", "{", "{\"displayName\":null}", "{\"displayName\":1}", "{\"DisplayName\":\"A\"}", "{\"displayName\":\"A\",\"displayName\":\"B\"}", "{\"displayName\":\"A\",\"command\":\"start\"}", "{\"displayName\":\"a\\nb\"}", new string(' ', 1025) })
        {
            var rejected = false;
            try { UserProfileLocalRoutes.Decode(Encoding.UTF8.GetBytes(json)); }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException) { rejected = true; }
            Require(rejected, "local decoding accepted ambiguous or unsafe profile input");
        }
        Require(UserProfileLocalRoutes.Decode(Encoding.UTF8.GetBytes("{\"displayName\":\" A \"}")) == " A ",
            "exact profile input could not be decoded");
        var get = Context("GET");
        Require(UserProfileLocalRoutes.Read(get, data.UserProfile) is JsonHttpResult<UserProfileView>, "fixed local GET was rejected");
        get.Request.ContentLength = 1;
        Require(Status(UserProfileLocalRoutes.Read(get, data.UserProfile)) == 400, "GET accepted a request body");
        foreach (Action<DefaultHttpContext> mutate in new Action<DefaultHttpContext>[]
        {
            c => c.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1"),
            c => c.Request.Host = new HostString("localhost", 5130),
            c => c.Request.Host = new HostString("127.0.0.1", 5131),
            c => c.Request.QueryString = new QueryString("?name=A"),
            c => c.Request.Headers.Remove("X-TogetherServer-Local"),
            c => c.Request.Headers["X-TogetherServer-Local"] = new Microsoft.Extensions.Primitives.StringValues(["1", "1"]),
            c => c.Request.Headers.Origin = "https://evil.example"
        })
        {
            var request = Context("PUT", "{\"displayName\":\"After\"}");
            mutate(request);
            Require(Status(await UserProfileLocalRoutes.SaveAsync(request, data.UserProfile, gate, () => true)) == 403,
                "a foreign or ambiguous local request was accepted");
            Require(data.UserProfile.View().DisplayName == "Before", "denied local request mutated state");
        }
        Require(Status(await UserProfileLocalRoutes.SaveAsync(Context("PUT", "{\"displayName\":\"After\"}"), data.UserProfile, gate, () => false)) == 409,
            "profile update bypassed the operation gate");
        var waiting = Context("PUT", "{\"displayName\":\"After\"}");
        await gate.WaitAsync();
        var save = UserProfileLocalRoutes.SaveAsync(waiting, data.UserProfile, gate, () => true);
        Require(!save.IsCompleted && data.UserProfile.View().DisplayName == "Before", "profile update bypassed the shared mode gate");
        gate.Release();
        await save;
        Require(data.UserProfile.View().DisplayName == "After", "valid local update failed");

        // Exercise the registered delegate and JSON response, without an app,
        // listener or transport. A RequestDelegate overload can discard IResult.
        using var services = new ServiceCollection().AddLogging().AddRouting().BuildServiceProvider();
        var routes = new SyntheticRoutes(services);
        UserProfileLocalRoutes.Map(routes, data.UserProfile, gate, () => true);
        var put = routes.DataSources.SelectMany(source => source.Endpoints).Single(endpoint =>
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("PUT") == true);
        var mapped = Context("PUT", "{\"displayName\":\"Mapped name\"}");
        mapped.RequestServices = services;
        mapped.Response.Body = new MemoryStream();
        await put.RequestDelegate!(mapped);
        mapped.Response.Body.Position = 0;
        var response = await JsonSerializer.DeserializeAsync<UserProfileResult>(mapped.Response.Body, Json);
        Require(response is { Ok: true, DisplayName: "Mapped name" } &&
            data.UserProfile.View().DisplayName == "Mapped name", "registered profile route discarded its JSON result");
    }

    private static void CheckPairing(string root)
    {
        using var data = Data(root, "profile-pairing");
        var clock = new Clock();
        var profile = Guid.NewGuid();
        data.SaveSettings(new HostSettings { Profiles = [new ServerProfile { Id = profile }] });
        data.UserProfile.Save("Host person");
        var pairing = new PairingService(data, clock);
        var invite = pairing.IssueServer(profile, false, false, "https://127.0.0.1:5131", new string('A', 64), refresh: false);
        Require(pairing.Activate(new(profile, invite.Code, ServerScope: true, ProfileName: "bad\nname")) is null,
            "malformed profile name consumed a pairing invitation");
        var first = pairing.Activate(new(profile, invite.Code, ServerScope: true, ProfileName: " Friend person "))!;
        var second = pairing.Activate(new(profile, invite.Code, ServerScope: true, ProfileName: "Other person"))!;
        Require(first is not null && second is not null && first.HostProfileName == "Host person", "valid pairing lost the Host profile");
        Require(pairing.Authenticate(first!.DeviceId, first.Credential, out var device).Ok, "synthetic credential failed");
        var initial = pairing.Views().Single(v => v.Id == first.DeviceId);
        Require(initial.ProfileName == "Friend person", "pairing did not normalize the Friend name");
        var instance = Guid.NewGuid();
        HeartbeatRequest Beat(long sequence, string? name) => new(first.DeviceId, instance, sequence, "synthetic", ProfileName: name);
        Require(pairing.RecordHeartbeat(device!, Beat(1, "Changed")).Ok, "authenticated profile heartbeat failed");
        Require(!pairing.RecordHeartbeat(device!, Beat(1, "Replay")).Ok && !pairing.RecordHeartbeat(device!, Beat(2, "Bad\nname")).Ok,
            "replay or malformed heartbeat was accepted");
        Require(pairing.Views().Single(v => v.Id == first.DeviceId).ProfileName == "Changed", "denied heartbeat changed the name");
        Require(pairing.RecordHeartbeat(device!, Beat(2, null)).Ok && pairing.Views().Single(v => v.Id == first.DeviceId).ProfileName == "Changed",
            "an older peer erased a saved Friend name");
        Require(pairing.RecordHeartbeat(device!, Beat(3, "")).Ok && string.IsNullOrEmpty(pairing.Views().Single(v => v.Id == first.DeviceId).ProfileName),
            "explicit blank did not clear the Friend name");
        Require(pairing.RecordHeartbeat(device!, Beat(4, "Final")).Ok, "last synthetic heartbeat failed");
        var restarted = new PairingService(data, clock);
        var view = restarted.Views().Single(v => v.Id == first.DeviceId);
        Require(view.ProfileName == "Final" && view.Name == initial.Name && view.CanStart == initial.CanStart && view.CanStop == initial.CanStop && view.AssignedProfileIds.SequenceEqual(initial.AssignedProfileIds) &&
            restarted.Views().Single(v => v.Id == second!.DeviceId).ProfileName == "Other person" && restarted.Authenticate(first.DeviceId, first.Credential, out _).Ok,
            "profile changes rewrote labels, grants, credentials or another device");
        Require(restarted.Revoke(first.DeviceId).Ok && !restarted.RecordHeartbeat(device!, Beat(5, "Revoked mutation")).Ok && restarted.Views().Single(v => v.Id == first.DeviceId).ProfileName == "Final",
            "revoked device changed its name through a stale loaded object");
        Require(pairing.Authenticate(second!.DeviceId, second.Credential, out var other).Ok, "second device authentication failed");
        clock.Now = second.ExpiresUtc.AddSeconds(1);
        Require(!pairing.RecordHeartbeat(other!, new(second.DeviceId, Guid.NewGuid(), 1, "synthetic", ProfileName: "Expired mutation")).Ok && pairing.Views().Single(v => v.Id == second.DeviceId).ProfileName == "Other person",
            "expired credential changed its profile");
    }

    private static async Task CheckFriendAsync(string root)
    {
        using var data = Data(root, "profile-friend");
        using (var pairingData = Data(root, "profile-friend-activation"))
        {
            pairingData.UserProfile.Save("Activation person");
            using var activationHost = new FakeHost();
            using var pairingLink = new FriendLink(pairingData, "activation.protected",
                (endpoint, _) => new HttpClient(activationHost, false) { BaseAddress = new Uri(endpoint) });
            var invite = new PairingInvite("https://127.0.0.1:5131", new string('A', 64), Guid.NewGuid(), "synthetic", DateTimeOffset.UtcNow.AddDays(1), ServerScope: true);
            Require((await pairingLink.PairAsync(JsonSerializer.Serialize(invite, Json))).Ok && activationHost.ActivationName == "Activation person" && pairingLink.View().HostProfileName == "Host person",
                "activation did not exchange profile names through the pinned transport");
            var savedCredential = pairingData.LoadProtectedJson<FriendConfiguration>("activation.protected")!.Credential;
            activationHost.Name = "Bad\nname";
            Require(!(await pairingLink.PairAsync(JsonSerializer.Serialize(invite, Json))).Ok && pairingData.LoadProtectedJson<FriendConfiguration>("activation.protected")!.Credential == savedCredential,
                "malformed activation Host profile replaced saved access");
        }
        const string file = "profile-friend.protected";
        var config = new FriendConfiguration { HostId = Guid.NewGuid(), DeviceId = Guid.NewGuid(), DisplayName = "Owner's local alias", Credential = new string('C', 64), CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90), Endpoint = "https://127.0.0.1:5131", Fingerprint = new string('A', 64), HostProfileName = "Cached person" };
        data.SaveProtected(file, JsonSerializer.SerializeToUtf8Bytes(config, Json));
        data.UserProfile.Save("Friend person");
        using var handler = new FakeHost();
        HttpClient Client(string endpoint, IEnumerable<string> pins) => new(handler, false) { BaseAddress = new Uri(endpoint) };
        using (var link = new FriendLink(data, file, Client))
        {
            Require(link.View().HostProfileName == "Cached person", "restart lost the protected cached Host name");
            await link.PollAsync();
            Require(handler.LastName == "Friend person" && link.View().HostProfileName == "Host person", "heartbeat did not exchange person names");
            data.UserProfile.Save("New friend");
            handler.Name = "New host";
            await link.PollAsync();
            Require(handler.LastName == "New friend" && link.View().HostProfileName == "New host", "live profile edits were not propagated");
            var assignedProfile = new PublicProfile(Guid.NewGuid(), "Synthetic saved server", "Offline", null);
            handler.Profiles = [assignedProfile];
            await link.PollAsync();
            Require(data.LoadProtectedJson<FriendConfiguration>(file)?.CachedProfiles?.Single().Id == assignedProfile.Id,
                "an unchanged Host name delayed persisting current assigned servers");
            handler.Profiles = [];
            handler.Name = null;
            await link.PollAsync();
            Require(string.IsNullOrEmpty(link.View().HostProfileName) && link.View().ConnectionName == config.DisplayName,
                "older Host absence did not fall back to the unchanged local alias");
            handler.Name = "Bad\nname";
            await link.PollAsync();
            Require(link.View().HostProfileName != "Bad\nname", "malformed Host profile reached cached display state");
            handler.Name = "";
            await link.PollAsync();
            Require(string.IsNullOrEmpty(link.View().HostProfileName), "explicit Host clear was ignored");
            handler.Name = "Remembered host";
            await link.PollAsync();
        }
        handler.Offline = true;
        using var offline = new FriendLink(data, file, Client);
        await offline.PollAsync();
        Require(offline.View().HostProfileName == "Remembered host" && data.LoadProtectedJson<FriendConfiguration>(file)?.Credential == config.Credential,
            "restart/offline lost the Host name or changed its credential");
    }

    private sealed class FakeHost : HttpMessageHandler
    {
        internal string? Name = "Host person";
        internal string? LastName;
        internal string? ActivationName;
        internal IReadOnlyList<PublicProfile> Profiles = [];
        internal bool Offline;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("Synthetic unavailable transport");
            if (request.RequestUri!.AbsolutePath == "/api/companion/pair")
            {
                ActivationName = (await request.Content!.ReadFromJsonAsync<PairingActivation>(Json, cancellationToken))!.ProfileName;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new PairingCredential(Guid.NewGuid(), new string('D', 64), DateTimeOffset.UtcNow.AddDays(90), HostProfileName: Name), options: Json) };
            }
            Require(request.RequestUri!.AbsolutePath == "/api/companion/heartbeat", "unexpected profile HTTP route");
            var beat = await request.Content!.ReadFromJsonAsync<HeartbeatRequest>(Json, cancellationToken);
            LastName = beat!.ProfileName;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new CompanionStatus(true, null, Profiles, false, false, DateTimeOffset.UtcNow, HostProfileName: Name), options: Json) };
        }
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class SyntheticRoutes(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(services);
    }
    private static DefaultHttpContext Context(string method, string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 5130;
        context.Request.Method = method;
        context.Request.Host = new HostString("127.0.0.1", 5130);
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        context.Request.Headers.Origin = "http://127.0.0.1:5130";
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }
        return context;
    }
    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
    private static LocalData Data(string root, string prefix) => new(Path.Combine(root, prefix + "-" + Guid.NewGuid().ToString("N")));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
