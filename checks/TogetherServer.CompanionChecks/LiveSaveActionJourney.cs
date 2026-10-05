using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

internal static partial class CoreRemoteJourney
{
    internal static async Task RunLiveSaveActionAsync(string appPath, string fixturePath)
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "live-save-action-journey", Guid.NewGuid().ToString("N")));
        var hostData = Path.Combine(root, "host"); var friendData = Path.Combine(root, "friend");
        var hostPort = FreeTcpPort(); var companionPort = FreeTcpPort(hostPort); var friendPort = FreeTcpPort(hostPort, companionPort);
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            Name = "Synthetic live sharing",
            WorldId = "synthetic",
            WorldSource = "New",
            ExecutablePath = fixturePath,
            GamePort = FreeUdpPair(),
            Backups = new() { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        profile.WorldDirectory = Path.Combine(hostData, "worlds", profile.Id.ToString("N"));
        Process? host = null; Process? friend = null;
        try
        {
            host = StartApp(appPath, "--host", hostPort, hostData, staging: true);
            friend = StartApp(appPath, "--friend", friendPort, friendData, staging: true);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(friendPort));
            WindowsListenerOwners.RequireTogetherServerOwner(hostPort, host);
            WindowsListenerOwners.RequireTogetherServerOwner(friendPort, friend);
            Directory.CreateDirectory(profile.WorldDirectory);
            var bytes = new byte[3 * SharedWorldService.ChunkBytes + 29]; RandomNumberGenerator.Fill(bytes);
            File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), bytes);
            using var owner = LocalClient(hostPort); using var test = LocalClient(friendPort);
            Require((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", new()
            { Profiles = [profile], CompanionPort = companionPort, CompanionBindAddress = "127.0.0.1", CompanionEndpoint = $"https://127.0.0.1:{companionPort}" })).Ok,
                "live staging settings failed");
            Require((await PutAsync<object, SharedWorldResult>(owner, $"/api/local/profiles/{profile.Id}/shared-world", new { enabled = true })).Ok,
                "normal shared-save opt-in failed");
            var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner, $"/api/local/servers/{profile.Id}/invite", new(false, false, true));
            Require(invite.GetProperty("ok").GetBoolean() && (await PostAsync<FriendPairRequest, FriendActionResult>(test,
                "/api/local/friend/pair", new(invite.GetProperty("password").GetString()!))).Ok, "normal pairing failed");
            await WaitForConnectedAsync(test, allowDisabled: true);
            var devices = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
            var deviceId = devices.GetProperty("devices").EnumerateArray().Single().GetProperty("id").GetGuid();
            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner, $"/api/local/devices/{deviceId}/shared-world/{profile.Id}", new(true))).Ok &&
                (await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(test, $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok,
                "normal Receive grant and consent failed");
            Require((await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/start", new { })).Ok, "managed live fixture Start failed");
            Require(!(await PostAsync<LiveSaveRequest, LiveSaveActionResult>(test,
                $"/api/local/profiles/{profile.Id}/shared-world/live/save", new(Guid.NewGuid()))).Ok, "Friend mode submitted a capture action");
            using (var invalid = new HttpRequestMessage(HttpMethod.Post, $"/api/local/profiles/{profile.Id}/shared-world/live/save")
            { Content = JsonContent.Create(new { requestId = Guid.NewGuid(), command = "caller input forbidden" }) })
            {
                invalid.Headers.Add("Origin", owner.BaseAddress!.ToString().TrimEnd('/'));
                invalid.Headers.Add("X-TogetherServer-Local", "1");
                using var denied = await owner.SendAsync(invalid);
                Require(denied.StatusCode == System.Net.HttpStatusCode.BadRequest, "extra command input was admitted");
            }
            var request = new LiveSaveRequest(Guid.NewGuid());
            var saved = await PostAsync<LiveSaveRequest, LiveSaveActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/live/save", request);
            Require(saved.Ok && saved.Attempt is { State: "Published", VersionHash: not null }, "surfaced fixed live fixture action failed: " + saved.Code);
            var repeated = await PostAsync<LiveSaveRequest, LiveSaveActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/live/save", request);
            Require(repeated.Ok && repeated.Attempt?.VersionHash == saved.Attempt!.VersionHash, "retry repeated signed publication");
            var current = await RehearsalOwnerGetAsync<SharedWorldStatus>(owner, $"/api/local/profiles/{profile.Id}/shared-world");
            Require(current.Latest is { Schema: 5, CaptureKind: SharedWorldCaptureKinds.LiveSave } &&
                SharedWorldService.VerifySignature(current.Latest) && current.LiveSave is { Available: true, Game: GameKinds.Fixture },
                "fixture capability or schema-5 current copy incorrect");
            Require((await PostAsync<object, ReceivedSharedWorldResult>(test,
                $"/api/local/friend/{profile.Id}/shared-world/pull", new { })).Ok, "normal pinned live copy Receive failed");
            for (var retry = 0; retry < 50; retry++)
            {
                current = await RehearsalOwnerGetAsync<SharedWorldStatus>(owner, $"/api/local/profiles/{profile.Id}/shared-world");
                if (current.ConfirmedCopies == 1) break;
                await Task.Delay(100);
            }
            Require(current.ConfirmedCopies == 1, "exact live-copy receipt was not confirmed");
            var received = Path.Combine(friendData, "received-shared-worlds", deviceId.ToString("N"), profile.Id.ToString("N"),
                current.Latest!.VersionHash, "payload", "world.dat");
            Require(File.Exists(received) && SHA256.HashData(File.ReadAllBytes(received)).AsSpan().SequenceEqual(SHA256.HashData(bytes)),
                "pinned multi-chunk live transfer did not match synthetic bytes");
            Console.WriteLine("PASS staging owner fixed live action, exact-process completion, signed immutable version, retry and normal Receive/hash/receipt");
            StopApp(friend); friend = StartApp(appPath, "--friend", friendPort, friendData, staging: true);
            StopApp(host); host = StartApp(appPath, "--host", hostPort, hostData, staging: true);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(friendPort));
            Require((await PostAsync<LiveSaveRequest, LiveSaveActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/live/save", request)).Attempt?.VersionHash == saved.Attempt!.VersionHash,
                "restart replay changed published identity");
            Console.WriteLine("PASS both apps restart with saved pairing and durable live attempt; no replay publication");
            var capture = PostAsync<LiveSaveRequest, LiveSaveActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/live/save", new(Guid.NewGuid()));
            await Task.Delay(40);
            var stopped = await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/stop", new { });
            Require(stopped.Ok && (await capture).Code == "LiveSaveCanceled", "concurrent owner Stop did not cancel and gracefully stop exact fixture");
            Require((await RehearsalOwnerGetAsync<SharedWorldStatus>(owner, $"/api/local/profiles/{profile.Id}/shared-world")).Latest?.CaptureKind == SharedWorldCaptureKinds.PostStopBackup,
                "existing verified post-Stop publication regressed");
            Console.WriteLine("PASS concurrent Stop cancels live capture and keeps post-Stop publication working");
            Console.WriteLine("Staging live action: 3 groups passed. Loopback/synthetic only; separate-PC game load/change/restart UNVERIFIED. Every real game remains disabled.");
        }
        finally { await TryStopManagedRunAsync(hostPort, profile.Id, host); StopApp(friend); StopApp(host); }
    }
}
