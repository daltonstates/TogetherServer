using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Injected HTTP handlers and disposable Windows-protected files only. No real
// connection, app entry point, game, listener, browser or native helper runs.
internal static class GamePasswordFriendChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "password-friend-" + Guid.NewGuid().ToString("N")));
        var configuration = Configuration();
        const string file = "password-friend.protected";
        data.SaveProtected(file, JsonSerializer.SerializeToUtf8Bytes(configuration, Json));
        using var host = new FakeHost(configuration);
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint == configuration.Endpoint && pins.SequenceEqual([configuration.Fingerprint]),
                "password reveal escaped the saved endpoint or certificate pin");
            return new(host, disposeHandler: false) { BaseAddress = new Uri(endpoint) };
        }
        using var link = new FriendLink(data, file, Client);
        var first = await link.RevealGamePasswordAsync(host.ProfileId);
        Require(first.Ok && first.Password == host.Secret && first.GamePasswordChangedUtc == host.ChangedUtc &&
            !first.Message.Contains(host.Secret, StringComparison.Ordinal), "an explicit pinned password read failed or echoed remote text");
        Require(host.Reveals == 1 && host.StatusReads == 2, "password read skipped fresh pre/post authorization or requested more than one secret");
        Require(!JsonSerializer.Serialize(link.View(), Json).Contains(host.Secret, StringComparison.Ordinal) &&
            !JsonSerializer.Serialize(data.LoadProtectedJson<FriendConfiguration>(file), Json).Contains(host.Secret, StringComparison.Ordinal) &&
            !Directory.EnumerateFiles(data.RootPath).Where(path => Path.GetFileName(path) != "host.lock")
                .Any(path => File.ReadAllText(path).Contains(host.Secret, StringComparison.Ordinal)),
            "the Friend cached or persisted a plaintext game password");
        host.Secret = "Synthetic current password 2!"; host.ChangedUtc = host.ChangedUtc.AddMinutes(1);
        Require((await link.RevealGamePasswordAsync(host.ProfileId)).Password == host.Secret && host.Reveals == 2,
            "a later explicit reveal returned an old cached password");
        host.Allowed = false;
        var count = host.Reveals;
        Require((await link.RevealGamePasswordAsync(host.ProfileId)).Code == "PermissionDenied" && host.Reveals == count,
            "a stale card or prior reveal bypassed a removed password grant");
        host.Allowed = true; host.Supported = false;
        Require((await link.RevealGamePasswordAsync(host.ProfileId)).Code == "GamePasswordUpdateRequired" && host.Reveals == count,
            "an old peer received an unsupported secret request");
        host.Supported = true;
        host.AfterReveal = () => host.Allowed = false;
        Require((await link.RevealGamePasswordAsync(host.ProfileId)).Password is null,
            "revoking the separate grant during a read returned the secret");
        host.AfterReveal = null; host.Allowed = true;
        host.AfterReveal = () => host.ChangedUtc = host.ChangedUtc.AddMinutes(1);
        Require((await link.RevealGamePasswordAsync(host.ProfileId)).Code == "GamePasswordChanged",
            "a password change during the read was returned as current");
        host.AfterReveal = null;
        host.WrongScope = true;
        Require((await link.RevealGamePasswordAsync(host.ProfileId)).Code == "GamePasswordResponseRejected",
            "a different server's password response was accepted");
        host.WrongScope = false; host.Huge = true;
        Require((await link.RevealGamePasswordAsync(host.ProfileId)).Code == "GamePasswordResponseRejected",
            "a declared oversized password response was accepted");
        host.Huge = false; host.StreamedHuge = true;
        Require((await link.RevealGamePasswordAsync(host.ProfileId)).Code == "GamePasswordResponseRejected",
            "an oversized response without Content-Length bypassed the streaming cap");
        host.StreamedHuge = false; host.Malformed = true;
        Require(!(await link.RevealGamePasswordAsync(host.ProfileId)).Ok, "malformed remote password data escaped typed failure");
        host.Malformed = false;
        foreach (var mode in new[] { "generic403", "401", "503", "positive-revoked", "offline" })
        {
            host.Denial = "Revoked";
            host.DenialStatus = mode == "401" ? HttpStatusCode.Unauthorized : mode == "503" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden;
            host.DenialOk = mode == "positive-revoked";
            host.GenericDenial = mode == "generic403";
            host.Offline = mode == "offline";
            var unproven = await link.RevealGamePasswordAsync(host.ProfileId);
            Require(!unproven.Ok && unproven.Password is null && unproven.Code != "Revoked" &&
                data.LoadProtectedJson<FriendConfiguration>(file)?.KnownRevoked == false && link.View().ConnectionCode != "Revoked",
                "an unproven password denial or transport failure taught durable revocation");
        }
        host.Denial = null; host.DenialStatus = HttpStatusCode.Forbidden;
        host.DenialOk = false; host.GenericDenial = false; host.Offline = false;
        host.AfterReveal = () => host.Denial = "AccessExpired";
        Require((await link.RevealGamePasswordAsync(host.ProfileId)).Code == "AccessExpired", "owner expiry during a read returned the secret");
        host.AfterReveal = null; host.Denial = null;
        host.AfterReveal = () => host.Denial = "Revoked";
        var revoked = await link.RevealGamePasswordAsync(host.ProfileId);
        Require(revoked.Code == "Revoked" && revoked.Password is null && link.View().ConnectionCode == "Revoked" &&
            data.LoadProtectedJson<FriendConfiguration>(file)?.KnownRevoked == true,
            "explicit pinned revocation during a read returned a secret or lost known revocation");
        host.AfterReveal = null; host.Denial = null;
        using (var restarted = new FriendLink(data, file, Client))
        {
            var calls = host.Requests;
            Require(restarted.View().ConnectionCode == "Revoked" &&
                (await restarted.RevealGamePasswordAsync(host.ProfileId)).Code == "Revoked" && host.Requests == calls,
                "restart retried a revoked credential or revived password access");
        }

        await CheckSelectionAndModeAsync(root);
    }

    private static async Task CheckSelectionAndModeAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "password-connection-" + Guid.NewGuid().ToString("N")));
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        var configuration = Configuration();
        foreach (var id in new[] { firstId, secondId })
            data.SaveProtected($"friend-{id:N}.protected", JsonSerializer.SerializeToUtf8Bytes(configuration, Json));
        data.SaveProtected("friend-connections.protected", JsonSerializer.SerializeToUtf8Bytes(new { ids = new[] { firstId, secondId }, selectedId = firstId }, Json));
        using var host = new FakeHost(configuration);
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint == configuration.Endpoint && pins.Contains(configuration.Fingerprint), "service used an unpinned password target");
            return new(host, disposeHandler: false) { BaseAddress = new Uri(endpoint) };
        }
        using var friend = new FriendService(data, Client);
        Require((await friend.RevealGamePasswordAsync(secondId, host.ProfileId)).Code == "ConnectionChanged" && host.Reveals == 0,
            "the password proxy accepted a saved connection other than the selected one");
        using var modeGate = new SemaphoreSlim(1);
        var allowed = true;
        host.DelayReveal();
        var pending = GamePasswordLocalRoutes.RevealAsync(GamePasswordPermissionChecks.LocalContext(
            "{\"connectionId\":\"" + firstId + "\"}"), host.ProfileId, friend, modeGate, () => allowed);
        await host.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(modeGate.CurrentCount == 1, "a slow password read held the safe Stop/navigation gate");
        allowed = false; host.Continue!.SetResult();
        var rejected = await pending;
        Require((rejected as IStatusCodeHttpResult)?.StatusCode == 409 &&
            ((rejected as IValueHttpResult)?.Value as GamePasswordResult)?.Password is null,
            "a mode/update/shutdown change returned an in-flight password result");
        host.DelayReveal();
        var selection = friend.RevealGamePasswordAsync(firstId, host.ProfileId);
        await host.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(friend.Select(secondId).Ok, "synthetic Host selection failed");
        host.Continue!.SetResult();
        Require((await selection).Code == "ConnectionChanged", "changing the selected Host during a read returned a secret");
    }

    private static FriendConfiguration Configuration() => new()
    {
        HostId = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        Credential = new string('C', 64),
        CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
        Endpoint = "https://127.0.0.1:5131",
        Fingerprint = new string('A', 64)
    };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FakeHost(FriendConfiguration configuration) : HttpMessageHandler
    {
        internal Guid ProfileId { get; } = Guid.NewGuid();
        internal string Secret { get; set; } = "Synthetic current password 1!";
        internal DateTimeOffset ChangedUtc { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        internal bool Allowed { get; set; } = true;
        internal bool Supported { get; set; } = true;
        internal bool WrongScope { get; set; }
        internal bool Huge { get; set; }
        internal bool StreamedHuge { get; set; }
        internal bool Malformed { get; set; }
        internal string? Denial { get; set; }
        internal HttpStatusCode DenialStatus { get; set; } = HttpStatusCode.Forbidden;
        internal bool DenialOk { get; set; }
        internal bool GenericDenial { get; set; }
        internal bool Offline { get; set; }
        internal int Requests { get; private set; }
        internal Action? AfterReveal { get; set; }
        internal int Reveals { get; private set; }
        internal int StatusReads { get; private set; }
        internal TaskCompletionSource? Entered { get; private set; }
        internal TaskCompletionSource? Continue { get; private set; }
        internal void DelayReveal()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Require(request.RequestUri?.Scheme == "https" && request.Headers.Authorization?.Parameter == configuration.Credential &&
                request.Headers.TryGetValues("X-Device-Id", out var ids) && ids.Single() == configuration.DeviceId.ToString() &&
                request.Headers.Contains(CompanionProtocol.HeaderName), "password request lost pinned HTTPS/authentication/protocol");
            if (Offline) throw new HttpRequestException("Synthetic offline Host.");
            if (GenericDenial) return new(DenialStatus) { Content = new StringContent("Forbidden") };
            if (Denial is not null) return Reply(new PairingDecision(DenialOk, Denial, Secret), DenialStatus);
            if (request.RequestUri!.AbsolutePath == "/api/companion/status")
            {
                StatusReads++;
                return Reply(new CompanionStatus(false, "Synthetic remote actions paused.",
                    [new(ProfileId, "Synthetic server", "Offline", null, Kind: GameKinds.Valheim, GameKind: GameKinds.Valheim,
                        CanViewPassword: Allowed, GamePasswordChangedUtc: Allowed ? ChangedUtc : null)], false, false, DateTimeOffset.UtcNow,
                    new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                        Supported ? [CompanionProtocol.GameRequirementsCapability, CompanionProtocol.GamePasswordCapability] : [CompanionProtocol.GameRequirementsCapability])));
            }
            Require(request.RequestUri.AbsolutePath == $"/api/companion/servers/{ProfileId}/game-password/reveal" &&
                request.Method == HttpMethod.Post && request.Content is null && string.IsNullOrEmpty(request.RequestUri.Query) &&
                request.Headers.TryGetValues("X-TogetherServer-Capability", out var capabilities) &&
                capabilities.Single() == CompanionProtocol.GamePasswordCapability, "password request included an unreviewed target, query, body or action");
            Reveals++;
            var current = new GamePasswordResult(true, "GamePasswordRevealed", Secret, WrongScope ? Guid.NewGuid() : ProfileId, Secret, ChangedUtc);
            if (Entered is not null && Continue is not null)
            { Entered.TrySetResult(); await Continue.Task.WaitAsync(ct); }
            AfterReveal?.Invoke();
            if (Huge) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[GamePasswords.MaximumResponseBytes + 1]) };
            if (StreamedHuge) return new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableInput(new byte[GamePasswords.MaximumResponseBytes + 1])) };
            if (Malformed) return new(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") };
            return Reply(current);
        }
        private static HttpResponseMessage Reply<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value, options: Json) };
    }

    private sealed class NonSeekableInput(byte[] bytes) : Stream
    {
        private readonly MemoryStream input = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => input.ReadAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
