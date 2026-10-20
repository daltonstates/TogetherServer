using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Synthetic protected files and in-memory requests only. No listener, process,
// app, browser, native dialog, production credential, game or world is opened.
internal static class GamePasswordPermissionChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task RunAsync(string root)
    {
        CheckIndependentPermissions(root);
        CheckCurrentAuthorization(root);
        await CheckCompanionInputAsync(root);
        await CheckLocalInputAsync(root);
    }

    private static void CheckIndependentPermissions(string root)
    {
        var legacy = JsonSerializer.Deserialize<PairedDevice>("{\"canStart\":true,\"canStop\":true,\"canViewLogs\":true}", Json)!;
        Require(!legacy.CanViewPassword && !legacy.CanViewPasswordForProfile(Guid.NewGuid()), "legacy permissions implicitly granted a secret");
        using var test = new Fixture(root, "permissions");
        Require(test.Pairing.Authenticate(test.DeviceId, test.Credential, out var device).Ok && device is not null, "synthetic pairing failed");
        var active = device!;
        Require(!test.Pairing.CanViewPassword(active, test.ProfileId) &&
            !test.Pairing.Views().Single().CanViewPassword && !test.Pairing.Views().Single().ServerPermissions!.Single().CanViewPassword,
            "new pairing or invitation enabled password access");
        Require(test.Pairing.SetTemporaryHelper(test.DeviceId, new(Duration: DeviceAccessDurations.OneHour)).Ok &&
            test.Pairing.Views().Single().TemporaryHelperActive && !test.Pairing.CanViewPassword(active, test.ProfileId),
            "temporary helper access implicitly granted a secret");
        Require(test.Pairing.SetPermissions(test.DeviceId, true, true, canExtendTimer: true, canViewLogs: true).Ok &&
            !test.Pairing.CanViewPassword(active, test.ProfileId), "an action preset implicitly granted a secret");
        Require(!test.Pairing.SetPermissions(test.DeviceId, false, false, canViewPassword: true).Ok &&
            !test.Pairing.SetPermissions(test.DeviceId, false, false, scope: "password").Ok,
            "an unscoped or omitted secret choice was accepted");
        Require(test.Pairing.SetServerAccess(test.DeviceId, [test.ProfileId, test.SiblingId], null, test.Known).Ok &&
            test.Pairing.SetPermissions(test.DeviceId, false, false, scope: "password", canViewPassword: true).Ok &&
            test.Pairing.CanViewPassword(active, test.ProfileId) && test.Pairing.CanViewPassword(active, test.SiblingId),
            "explicit global password access did not cover assigned profiles");
        Require(test.Pairing.SetServerAccess(test.DeviceId, test.Known,
            [new(test.ProfileId, false, false, CanViewPassword: false), new(test.SiblingId, false, false)], test.Known).Ok &&
            !test.Pairing.CanViewPassword(active, test.ProfileId) && test.Pairing.CanViewPassword(active, test.SiblingId),
            "per-server denial did not override the global secret grant");
        Require(test.Pairing.SetPermissions(test.DeviceId, false, false).Ok &&
            !test.Pairing.CanViewPassword(active, test.ProfileId) && test.Pairing.CanViewPassword(active, test.SiblingId),
            "a status-only preset changed independent per-server password grants");
        Require(test.Pairing.SetPermissions(test.DeviceId, false, false, scope: "password", canViewPassword: false).Ok &&
            test.Pairing.SetServerAccess(test.DeviceId, test.Known,
                [new(test.ProfileId, false, false, CanViewPassword: true), new(test.SiblingId, false, false)], test.Known).Ok &&
            test.Pairing.CanViewPassword(active, test.ProfileId) && !test.Pairing.CanViewPassword(active, test.SiblingId),
            "per-server grant did not remain separate from the global default");
        Require(test.Pairing.SetServerAccess(test.DeviceId, test.Known, [new(test.ProfileId, true, false)], test.Known).Ok &&
            test.Pairing.CanViewPassword(active, test.ProfileId) && !test.Pairing.CanViewPassword(active, test.SiblingId),
            "a legacy per-server editor erased or expanded the separate password grant");
        var reloaded = new PairingService(test.Data, test.Clock);
        Require(reloaded.CanViewPassword(active, test.ProfileId) && !reloaded.CanViewPassword(active, test.SiblingId),
            "protected permission persistence lost the owner choice");
        var state = test.Data.LoadPairingState();
        state.Devices.Single().CanViewPassword = true;
        state.Devices.Single().ServerPermissionOverrides =
        [new() { ProfileId = test.ProfileId, CanViewPassword = true }, new() { ProfileId = test.ProfileId, CanViewPassword = false }];
        test.Data.SavePairingState(state);
        Require(!new PairingService(test.Data, test.Clock).CanViewPassword(active, test.ProfileId),
            "duplicate stored permission normalization expanded secret access");
    }

    private static void CheckCurrentAuthorization(string root)
    {
        using (var test = new Fixture(root, "encrypted"))
        {
            var reads = 0;
            Require(test.Pairing.ReadGamePassword(test.DeviceId, test.Credential, test.ProfileId, () =>
            { reads++; return GamePasswords.Read(test.Data, test.ProfileId); }).Password is null && reads == 0,
                "denied password access read protected secret material");
            test.Grant();
            var first = test.Pairing.ReadGamePassword(test.DeviceId, test.Credential, test.ProfileId);
            Require(first.Ok && first.Password == Fixture.Secret && first.GamePasswordChangedUtc is not null,
                "an explicit grant could not retrieve the current password");
            Require(!File.ReadAllText(test.PasswordPath).Contains(Fixture.Secret, StringComparison.Ordinal) &&
                !first.ToString().Contains(Fixture.Secret, StringComparison.Ordinal), "a plaintext password escaped encrypted storage or diagnostic formatting");
            File.SetLastWriteTimeUtc(test.PasswordPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var before = GamePasswords.ChangedUtc(test.Data, test.ProfileId);
            test.Data.SaveValheimPassword(test.ProfileId, Fixture.Replacement);
            var changed = test.Pairing.ReadGamePassword(test.DeviceId, test.Credential, test.ProfileId);
            Require(changed.Password == Fixture.Replacement && changed.GamePasswordChangedUtc != before &&
                !JsonSerializer.Serialize(test.Pairing.Views(), Json).Contains(Fixture.Replacement, StringComparison.Ordinal) &&
                !JsonSerializer.Serialize(test.Data.LoadActivity(), Json).Contains(Fixture.Replacement, StringComparison.Ordinal),
                "password changes were stale or leaked into permissions/activity");
            Require(!test.Pairing.ReadGamePassword(test.DeviceId, "wrong synthetic bearer", test.ProfileId).Ok &&
                !test.Pairing.ReadGamePassword(test.DeviceId, test.Credential, test.SiblingId).Ok,
                "a wrong credential or unassigned profile revealed a password");
        }

        foreach (var change in new[] { "grant", "assignment", "revocation", "owner-expiry", "credential-expiry" })
        {
            using var test = new Fixture(root, change);
            test.Grant();
            if (change == "owner-expiry") Require(test.Pairing.SetAccessExpiry(test.DeviceId, new(Duration: DeviceAccessDurations.OneHour)).Ok,
                "synthetic owner deadline setup failed");
            var result = test.Pairing.ReadGamePassword(test.DeviceId, test.Credential, test.ProfileId, () =>
            {
                var read = GamePasswords.Read(test.Data, test.ProfileId);
                switch (change)
                {
                    case "grant": test.Pairing.SetPermissions(test.DeviceId, false, false, scope: "password", canViewPassword: false); break;
                    case "assignment": test.Pairing.SetServerAccess(test.DeviceId, [], null, test.Known); break;
                    case "revocation": test.Pairing.Revoke(test.DeviceId); break;
                    case "owner-expiry": test.Clock.Now = test.Clock.Now.AddHours(1); break;
                    case "credential-expiry": test.Clock.Now = test.CredentialExpiresUtc; break;
                }
                return read;
            });
            Require(!result.Ok && result.Password is null && result.GamePasswordChangedUtc is null &&
                !JsonSerializer.Serialize(result, Json).Contains("\"password\"", StringComparison.Ordinal),
                "a changed authorization returned a secret or its private change hint");
            if (change == "owner-expiry") Require(result.Code == "AccessExpired", "the exact owner deadline did not deny password access");
            if (change == "credential-expiry") Require(result.Code == "CredentialExpired", "the exact credential deadline did not deny password access");
        }

        foreach (var missing in new[] { true, false })
        {
            using var test = new Fixture(root, missing ? "missing-credential-deadline" : "expired-credential-deadline");
            test.Grant();
            var saved = test.Data.LoadPairingState();
            saved.Devices.Single().CredentialExpiresUtc = missing ? null : test.Clock.Now;
            test.Data.SavePairingState(saved);
            var pairing = new PairingService(test.Data, test.Clock);
            var legacy = pairing.Authenticate(test.DeviceId, test.Credential, out _);
            Require(missing ? legacy.Ok : !legacy.Ok && legacy.Code == "Expired",
                "password-only deadline validation changed shared legacy authentication");
            var reads = 0;
            var result = pairing.ReadGamePassword(test.DeviceId, test.Credential, test.ProfileId, () =>
            { reads++; return GamePasswords.Read(test.Data, test.ProfileId); });
            Require(result.Code == "CredentialExpired" && result.Password is null && reads == 0,
                "a missing or expired credential deadline reached protected password storage");
        }
    }

    private static async Task CheckCompanionInputAsync(string root)
    {
        using var test = new Fixture(root, "wire");
        test.Grant();
        bool Authenticate(HttpContext context, out PairedDevice? device, out PairingDecision decision)
        {
            device = null;
            if (!Guid.TryParse(context.Request.Headers["X-Device-Id"], out var id))
            { decision = new(false, "Unauthorized", "Synthetic denial."); return false; }
            var auth = context.Request.Headers.Authorization.ToString();
            decision = test.Pairing.Authenticate(id, auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : null, out device);
            return decision.Ok;
        }
        static int AuthStatus(PairingDecision decision) => decision.Code is "AccessExpired" or "Revoked" or "ApprovalPending" ? 403 : 401;
        DefaultHttpContext Context()
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Scheme = "https";
            context.Request.Headers.Authorization = "Bearer " + test.Credential;
            context.Request.Headers["X-Device-Id"] = test.DeviceId.ToString();
            context.Request.Headers[CompanionProtocol.HeaderName] = CompanionProtocol.Current.ToString();
            context.Request.Headers["X-TogetherServer-Capability"] = CompanionProtocol.GamePasswordCapability;
            return context;
        }
        var valid = Context();
        var reply = await GamePasswordCompanionRoutes.RevealAsync(valid, test.ProfileId, test.Data, test.Pairing, Authenticate, AuthStatus);
        Require(Status(reply) == 200 && Value(reply)?.Password == Fixture.Secret && valid.Response.Headers.CacheControl == "no-store",
            "a fixed authenticated password reveal failed or allowed caching");
        foreach (Action<DefaultHttpContext> mutate in new Action<DefaultHttpContext>[]
        {
            context => context.Request.Method = "GET",
            context => context.Request.Scheme = "http",
            context => context.Request.QueryString = new QueryString("?password=untrusted"),
            context => context.Request.ContentLength = 1,
            context => context.Request.Body = new MemoryStream([1]),
            context => context.Request.Headers["Transfer-Encoding"] = "chunked",
            context => context.Request.Headers.Remove("X-Device-Id"),
            context => context.Request.Headers.Authorization = "Bearer wrong synthetic token",
            context => context.Request.Headers.Remove(CompanionProtocol.HeaderName),
            context => context.Request.Headers["X-TogetherServer-Capability"] = "unreviewed",
            context => context.Request.Headers["X-TogetherServer-Capability"] = new Microsoft.Extensions.Primitives.StringValues([CompanionProtocol.GamePasswordCapability, CompanionProtocol.GamePasswordCapability])
        })
        {
            var context = Context(); mutate(context);
            var rejected = await GamePasswordCompanionRoutes.RevealAsync(context, test.ProfileId, test.Data, test.Pairing, Authenticate, AuthStatus);
            Require(Status(rejected) is 400 or 401 or 403 or 409 && Value(rejected)?.Password is null,
                "an ambiguous or untrusted companion request revealed a secret");
        }
    }

    private static async Task CheckLocalInputAsync(string root)
    {
        var connectionId = Guid.NewGuid();
        var payload = "{\"connectionId\":\"" + connectionId + "\"}";
        Require(GamePasswordLocalRoutes.Decode(Encoding.UTF8.GetBytes(payload)) == connectionId, "fixed password connection input did not decode");
        foreach (var invalid in new[] { "{}", "null", "[]", "{", payload[..^1] + ",\"path\":\"private\"}",
            payload[..^1] + ",\"connectionId\":\"" + connectionId + "\"}", "{\"connectionId\":null}",
            "{\"connectionId\":\"" + Guid.Empty + "\"}", new string(' ', 129) })
        {
            var rejected = false;
            try { GamePasswordLocalRoutes.Decode(Encoding.UTF8.GetBytes(invalid)); }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { rejected = true; }
            Require(rejected, "password proxy accepted ambiguous or unbounded target input");
        }
        using var data = new LocalData(Path.Combine(root, "password-local-" + Guid.NewGuid().ToString("N")));
        using var friend = new FriendService(data);
        using var gate = new SemaphoreSlim(1);
        foreach (Action<DefaultHttpContext> mutate in new Action<DefaultHttpContext>[]
        {
            context => context.Request.Headers.Remove("X-TogetherServer-Local"),
            context => context.Request.Headers.Origin = "https://example.invalid",
            context => context.Request.QueryString = new QueryString("?host=arbitrary"),
            context => context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1"),
            context => context.Request.Method = "GET"
        })
        {
            var context = LocalContext(payload); mutate(context);
            Require(Status(await GamePasswordLocalRoutes.RevealAsync(context, Guid.NewGuid(), friend, gate, () => true)) == 403,
                "foreign local password request reached the Friend proxy");
        }
        Require(Status(await GamePasswordLocalRoutes.RevealAsync(LocalContext(payload), Guid.NewGuid(), friend, gate, () => false)) == 409,
            "password proxy bypassed current app operation restrictions");
    }

    internal static DefaultHttpContext LocalContext(string payload)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 5130;
        context.Request.Method = "POST";
        context.Request.Host = new HostString("127.0.0.1", 5130);
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        context.Request.Headers.Origin = "http://127.0.0.1:5130";
        context.Request.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length;
        return context;
    }
    private static GamePasswordResult? Value(IResult result) => (result as IValueHttpResult)?.Value as GamePasswordResult;
    private static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        internal const string Secret = "Synthetic secret only 1!";
        internal const string Replacement = "Synthetic replacement only 2!";
        internal LocalData Data { get; }
        internal PairingService Pairing { get; }
        internal TestClock Clock { get; } = new();
        internal Guid ProfileId { get; } = Guid.NewGuid();
        internal Guid SiblingId { get; } = Guid.NewGuid();
        internal Guid[] Known => [ProfileId, SiblingId];
        internal Guid DeviceId { get; }
        internal string Credential { get; }
        internal DateTimeOffset CredentialExpiresUtc { get; }
        internal string PasswordPath => Path.Combine(Data.RootPath, $"valheim-password-{ProfileId:N}.protected");
        internal Fixture(string root, string name)
        {
            Data = new(Path.Combine(root, "password-" + name + "-" + Guid.NewGuid().ToString("N")));
            Data.SaveSettings(new HostSettings
            {
                Profiles =
                [new() { Id = ProfileId, Name = "Synthetic Valheim", Kind = GameKinds.Valheim },
                 new() { Id = SiblingId, Name = "Synthetic sibling", Kind = GameKinds.Valheim }]
            });
            Data.SaveValheimPassword(ProfileId, Secret);
            Pairing = new(Data, Clock);
            var invitation = Pairing.IssueServer(ProfileId, true, false, "https://127.0.0.1:5131", new string('A', 64), false);
            var credential = Pairing.Activate(new(ProfileId, invitation.Code, ServerScope: true)) ?? throw new InvalidOperationException("synthetic pairing failed");
            DeviceId = credential.DeviceId; Credential = credential.Credential; CredentialExpiresUtc = credential.ExpiresUtc;
        }
        internal void Grant() => Require(Pairing.SetPermissions(DeviceId, false, false, scope: "password", canViewPassword: true).Ok,
            "synthetic explicit password grant failed");
        public void Dispose() => Data.Dispose();
    }
    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
