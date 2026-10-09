using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class GameClientFriendChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task RunAsync(string root)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var profileId = Guid.NewGuid();
        var adapter = new FakeLaunch();
        foreach (var pair in new[] { (GameKinds.Valheim, "steam://run/892970"), (GameKinds.Factorio, "steam://run/427520"), (GameKinds.Terraria, "steam://run/105600") })
        {
            var result = GameClientLaunch.Open(new(profileId, "Synthetic", "Ready", "127.0.0.1:2456", Kind: pair.Item1), adapter);
            Require(result.Ok && adapter.Uris[^1] == pair.Item2, "a launch used an unexpected URI or arguments");
        }
        var count = adapter.Uris.Count;
        Require(GameClientLaunch.Open(new(profileId, "Synthetic", "Ready", "127.0.0.1:25565", Kind: GameKinds.MinecraftJava), adapter).Code == "ManualLaunchRequired" && adapter.Uris.Count == count,
            "Minecraft launched an arbitrary local executable");
        foreach (var address in new[] { "steam://run/123", "127.0.0.1:2456/--exec", "127.0.0.1:2456\ncommand", "127.0.0.1:0", "127.0.0.1:65536", "127.0.0.1:2456;unsafe" })
            Require(!GameClientLaunch.Open(new(profileId, "Synthetic", "Ready", address, Kind: GameKinds.Valheim), adapter).Ok && adapter.Uris.Count == count, "tampered address reached launch adapter");
        adapter.Result = new(false, "SteamHandlerUnavailable", "Synthetic unavailable handler.");
        Require(GameClientLaunch.Open(new(profileId, "Synthetic", "Ready", "127.0.0.1:2456", Kind: GameKinds.Valheim), adapter).Code == "SteamHandlerUnavailable", "missing handler became launch success");
        adapter.Throw = true;
        Require(GameClientLaunch.Open(new(profileId, "Synthetic", "Ready", "127.0.0.1:2456", Kind: GameKinds.Valheim), adapter).Code == "GameLaunchFailed", "adapter failure was not typed");
        adapter.Throw = false; adapter.Result = new(true, "GameOpenRequested", "Synthetic request accepted.");

        using var data = new LocalData(Path.Combine(root, "client-link-" + Guid.NewGuid().ToString("N")));
        const string configFile = "synthetic-friend.protected";
        var config = new FriendConfiguration { HostId = Guid.NewGuid(), DeviceId = Guid.NewGuid(), Credential = new string('C', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90), Endpoint = "https://127.0.0.1:5131", Fingerprint = new string('A', 64) };
        data.SaveProtected(configFile, JsonSerializer.SerializeToUtf8Bytes(config, Json));
        var host = new FakeHost(profileId, config.DeviceId, config.Credential);
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint == config.Endpoint && pins.Contains(config.Fingerprint), "feature bypassed its saved pinned transport");
            return new(host, disposeHandler: false) { BaseAddress = new Uri(endpoint) };
        }
        using var link = new FriendLink(data, configFile, Client);
        count = adapter.Uris.Count;
        await link.PollAsync(); await link.PollAsync();
        var compatibility = await link.ReadGameCompatibilityAsync(profileId, discover: (_, _) => new("0.219.16", "Observed"));
        Require(compatibility.Ok && compatibility.VersionComparison == "Match" && compatibility.ClientVersionSource == "Observed" && adapter.Uris.Count == count,
            "poll/read either lost comparison or launched a game");
        var manual = await link.ReadGameCompatibilityAsync(profileId, new("0.219.16"), discover: (_, _) => new(null, "Unknown"));
        Require(manual.Ok && manual.ClientVersionSource == "Manual" && manual.VersionComparison == "Match", "manual fallback lost its source label");
        Require((await link.OpenGameAsync(profileId, adapter)).Ok && adapter.Uris.Count == count + 1, "explicit authenticated open did not use the fake adapter once");
        count = adapter.Uris.Count;
        adapter.AfterPrepare = () => host.Denial = "Revoked";
        Require((await link.OpenGameAsync(profileId, adapter)).Code == "Revoked" && adapter.Uris.Count == count,
            "revocation during adapter preparation reached native launch");
        adapter.AfterPrepare = null; host.Denial = null;
        using (var cancellation = new CancellationTokenSource())
        {
            adapter.AfterPrepare = cancellation.Cancel;
            try { await link.OpenGameAsync(profileId, adapter, cancellation.Token); throw new Exception("cancelled launch returned success"); }
            catch (OperationCanceledException) { }
            Require(adapter.Uris.Count == count, "cancelled preparation dispatched a game launch");
            adapter.AfterPrepare = null;
        }
        host.MalformedStatusProfile = true;
        Require((await link.OpenGameAsync(profileId, adapter)).Code == "InvalidResponse" && adapter.Uris.Count == count,
            "a null status profile escaped validation or launched a game");
        Require((await link.ReadGameCompatibilityAsync(profileId, discover: (_, _) => new(null, "Unknown"))).Code == "InvalidResponse",
            "a null status profile escaped compatibility validation");
        host.MalformedStatusProfile = false;
        host.Denial = "Revoked";
        Require((await link.OpenGameAsync(profileId, adapter)).Code == "Revoked" && adapter.Uris.Count == count, "revoked link launched a client");
        host.Denial = "AccessExpired";
        Require((await link.OpenGameAsync(profileId, adapter)).Code == "AccessExpired" && adapter.Uris.Count == count, "expired link launched a client");
        host.Denial = null; host.Assigned = false;
        Require((await link.OpenGameAsync(profileId, adapter)).Code == "PermissionDenied" && adapter.Uris.Count == count, "unassigned link launched a client");
        host.Assigned = true; await link.PollAsync();
        host.SavedKind = GameKinds.Custom; await link.PollAsync();
        Require((await link.OpenGameAsync(profileId, adapter)).Code == "ManualLaunchRequired" && adapter.Uris.Count == count,
            "a Custom game using a built-in display name selected a reviewed client mapping");
        host.SavedKind = GameKinds.Valheim; await link.PollAsync();
        host.Address = "steam://run/123"; await link.PollAsync();
        Require((await link.OpenGameAsync(profileId, adapter)).Code == "GameAddressUnavailable" && adapter.Uris.Count == count, "Host address became a launch command");
        host.Address = "127.0.0.1:2456"; await link.PollAsync();
        host.AfterRequirements = () => host.Denial = "Revoked";
        var revokedRead = await link.ReadGameCompatibilityAsync(profileId, discover: (_, _) => new("0.219.16", "Observed"));
        Require(!revokedRead.Ok && revokedRead.Code == "Revoked" && revokedRead.Requirements is null, "read returned requirements after authorization changed");
        host.Denial = null; host.AfterRequirements = null;
        host.HugeRequirements = true;
        Require((await link.ReadGameCompatibilityAsync(profileId, discover: (_, _) => new(null, "Unknown"))).Code == "RequirementsResponseTooLarge", "oversized requirement response accepted");
        host.HugeRequirements = false; host.RequirementsSupported = false; await link.PollAsync();
        var requests = host.RequirementsRequests;
        Require((await link.ReadGameCompatibilityAsync(profileId)).Code == "RequirementsUpdateRequired" && host.RequirementsRequests == requests,
            "old peer received an unsupported requirements request");
        using var tooLarge = new StreamContent(new NonSeekableInput(new byte[GameCompatibility.MaximumWireBytes + 1]));
        Require(await FriendLink.ReadFeaturePayloadAsync(tooLarge, GameCompatibility.MaximumWireBytes, default) is null, "streamed response size cap failed");
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
    private sealed class FakeLaunch : IGameClientLaunchAdapter
    {
        public List<string> Uris { get; } = [];
        public bool Throw { get; set; }
        public Action? AfterPrepare { get; set; }
        public GameClientLaunchResult Prepare(string uri, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); AfterPrepare?.Invoke();
            return new(true, "GameClientAvailable", "Synthetic availability.");
        }
        public GameClientLaunchResult Result { get; set; } = new(true, "GameOpenRequested", "Synthetic request accepted.");
        public GameClientLaunchResult Open(string uri)
        {
            if (Throw) throw new InvalidOperationException("Synthetic failure.");
            Uris.Add(uri); return Result;
        }
    }
    private sealed class FakeHost(Guid profileId, Guid deviceId, string credential) : HttpMessageHandler
    {
        public bool Assigned { get; set; } = true;
        public string SavedKind { get; set; } = GameKinds.Valheim;
        public string? Denial { get; set; }
        public string Address { get; set; } = "127.0.0.1:2456";
        public bool RequirementsSupported { get; set; } = true;
        public bool HugeRequirements { get; set; }
        public int RequirementsRequests { get; private set; }
        public bool MalformedStatusProfile { get; set; }
        public Action? AfterRequirements { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Authorization?.Parameter != credential ||
                !request.Headers.TryGetValues("X-Device-Id", out var ids) || ids.Single() != deviceId.ToString() ||
                !request.Headers.Contains(CompanionProtocol.HeaderName)) throw new Exception("feature request omitted saved authentication/protocol");
            if (Denial is not null) return Task.FromResult(Reply(new PairingDecision(false, Denial, "Synthetic denial."), HttpStatusCode.Forbidden));
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/companion/status" && MalformedStatusProfile)
                return Task.FromResult(Reply(new { remoteControlsEnabled = true, profiles = new object?[] { null },
                    receivedUtc = DateTimeOffset.UtcNow, protocol = CompanionProtocol.Describe() }));
            if (path is "/api/companion/status" or "/api/companion/heartbeat")
                return Task.FromResult(Reply(new CompanionStatus(true, null, Assigned ? [new(profileId, "Synthetic", "Ready", Address, Kind: GameKinds.Valheim, GameKind: SavedKind)] : [],
                    false, false, DateTimeOffset.UtcNow, new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                        RequirementsSupported ? [CompanionProtocol.GameRequirementsCapability] : []))));
            if (path == $"/api/companion/servers/{profileId}/requirements")
            {
                RequirementsRequests++;
                if (!request.Headers.TryGetValues("X-TogetherServer-Capability", out var capabilities) || capabilities.Single() != CompanionProtocol.GameRequirementsCapability)
                    throw new Exception("requirements request omitted feature capability");
                AfterRequirements?.Invoke();
                if (HugeRequirements) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[GameCompatibility.MaximumWireBytes + 1]) });
                return Task.FromResult(Reply(new GameRequirementsResult(true, "RequirementsRead", "Synthetic requirements.",
                    new(profileId, GameKinds.Valheim, "Valheim", "0.219.16", "OwnerReported", "NotReviewed", [], DateTimeOffset.UtcNow,
                        "Use the Host's required game version. Mod loaders for this game are outside reviewed support."))));
            }
            throw new Exception("unreviewed feature request path");
        }
        private static HttpResponseMessage Reply<T>(T value, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = JsonContent.Create(value, options: Json) };
    }
}
