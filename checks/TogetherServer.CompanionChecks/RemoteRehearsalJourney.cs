using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

internal static partial class CoreRemoteJourney
{
    public static async Task RunRehearsalAsync(string appPath)
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "remote-rehearsal-journey", Guid.NewGuid().ToString("N")));
        var hostData = Path.Combine(root, "host");
        var friendData = Path.Combine(root, "friend");
        var hostPort = FreeTcpPort();
        var companionPort = FreeTcpPort(hostPort);
        var friendPort = FreeTcpPort(hostPort, companionPort);
        Process? host = null;
        Process? friend = null;
        try
        {
            host = StartApp(appPath, "--host", hostPort, hostData, staging: true);
            friend = StartApp(appPath, "--friend", friendPort, friendData, staging: true);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(friendPort));
            WindowsListenerOwners.RequireTogetherServerOwner(hostPort, host);
            WindowsListenerOwners.RequireTogetherServerOwner(friendPort, friend);
            using var owner = LocalClient(hostPort);
            using var test = LocalClient(friendPort);
            var prepared = await PostAsync<object, RemoteRehearsalSetup>(owner, "/api/local/rehearsal/prepare", new { });
            Require(prepared.Ok && prepared.ProfileId is not null, "disposable staging rehearsal was not prepared: " + prepared.Code);
            var profileId = prepared.ProfileId!.Value;
            var repeated = await PostAsync<object, RemoteRehearsalSetup>(owner, "/api/local/rehearsal/prepare", new { });
            Require(repeated.Ok && repeated.ProfileId == profileId, "prepare retry created another profile");
            var snapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot") ?? throw new Exception("no Host snapshot");
            snapshot.Settings.CompanionBindAddress = "127.0.0.1";
            snapshot.Settings.CompanionPort = companionPort;
            snapshot.Settings.CompanionEndpoint = $"https://127.0.0.1:{companionPort}";
            Require((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", snapshot.Settings)).Ok,
                "staging rehearsal settings were rejected");
            Require(!(await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profileId}/start", new { })).Ok,
                "synthetic rehearsal could launch a game executable");
            var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner,
                $"/api/local/servers/{profileId}/invite", new(false, false, true));
            Require(invite.GetProperty("ok").GetBoolean() &&
                (await PostAsync<FriendPairRequest, FriendActionResult>(test, "/api/local/friend/pair",
                    new(invite.GetProperty("password").GetString()!))).Ok, "test PC could not pair normally");
            var connected = await WaitForConnectedAsync(test, allowDisabled: true);
            var devices = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
            var deviceId = devices.GetProperty("devices").EnumerateArray().Single().GetProperty("id").GetGuid();
            var request = new RemoteRehearsalRequest(Guid.NewGuid(), "SeparateNetwork");
            var noConsent = await PostAsync<RemoteRehearsalRequest, RemoteRehearsalReport>(test,
                $"/api/local/friend/{profileId}/rehearsal", request);
            Require(noConsent.Stages.Single(stage => stage.Id == "transfer").State != "Passed", "Receive consent was silently granted");
            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceId}/shared-world/{profileId}", new(true))).Ok, "owner Receive grant failed");
            Require((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(test,
                $"/api/local/friend/{profileId}/shared-world/consent", new(true))).Ok, "local Receive consent failed");
            var report = await PostAsync<RemoteRehearsalRequest, RemoteRehearsalReport>(test,
                $"/api/local/friend/{profileId}/rehearsal", request);
            AssertRehearsal(report);
            var load = await PostAsync<object, WorldLoadRehearsalResult>(test,
                $"/api/local/friend/{profileId}/world-load/prepare", new { });
            Require(load.Ok && load.Rehearsal is { CanLaunch: false, SourceKind: "Received" } &&
                load.Rehearsal.LoadOutcome == "Unobserved", "received copy did not prepare as an isolated manual drill");
            Require((await PostAsync<WorldLoadConfirmationRequest, WorldLoadRehearsalResult>(test,
                $"/api/local/world-load/{load.Rehearsal!.Id}/confirm", new("Load", false))).Rehearsal?.LoadOutcome == "OwnerFailed",
                "failed received-copy load was not retained accurately");
            Require((await PostAsync<WorldLoadCleanupRequest, WorldLoadRehearsalResult>(test,
                $"/api/local/world-load/{load.Rehearsal.Id}/cleanup", new(true))).Ok,
                "received disposable copy cleanup failed");
            var repeat = await PostAsync<RemoteRehearsalRequest, RemoteRehearsalReport>(test,
                $"/api/local/friend/{profileId}/rehearsal", request);
            AssertRehearsal(repeat);
            var room = await RehearsalOwnerGetAsync<ChatRoomView>(owner, $"/api/local/profiles/{profileId}/chat");
            Require(room?.Entries.Count == 2, "rehearsal retry duplicated signed messages");
            var shared = await RehearsalOwnerGetAsync<SharedWorldStatus>(owner, $"/api/local/profiles/{profileId}/shared-world");
            Require(shared is { ConfirmedCopies: 1, Latest.Files.Count: 1 } &&
                shared.Latest.Files[0].Length > SharedWorldService.ChunkBytes * 2, "synthetic multi-chunk receipt was not confirmed");
            var serialized = JsonSerializer.Serialize(report);
            Require(!serialized.Contains(deviceId.ToString()) && !serialized.Contains(profileId.ToString()) &&
                !serialized.Contains(hostData) && !serialized.Contains("127.0.0.1") &&
                !serialized.Contains(shared!.Latest!.VersionHash), "rehearsal report leaked private setup or copy identity");
            Console.WriteLine("PASS packaged staging rehearsal: normal pairing/Receive, two-way signed chat, multi-chunk hashes and exact-copy receipt; retry and redaction");
            var wrong = await PostAsync<RemoteRehearsalRequest, RemoteRehearsalReport>(test,
                $"/api/local/friend/{Guid.NewGuid()}/rehearsal", request);
            Require(wrong.Stages.All(stage => stage.State != "Passed"), "wrong saved profile was accepted");
            Require((await PutAsync<ChatMemberChange, ChatRoomView>(owner,
                $"/api/local/profiles/{profileId}/chat/members/{deviceId}", new(false))).Ok, "room removal failed");
            var removed = await PostAsync<RemoteRehearsalRequest, RemoteRehearsalReport>(test,
                $"/api/local/friend/{profileId}/rehearsal", new(Guid.NewGuid()));
            Require(removed.Stages.Single(stage => stage.Id == "chat").State != "Passed", "removed room access passed");
            _ = await PutAsync<ChatMemberChange, ChatRoomView>(owner,
                $"/api/local/profiles/{profileId}/chat/members/{deviceId}", new(true));
            StopApp(host); host = null;
            StopApp(friend); friend = null;
            host = StartApp(appPath, "--host", hostPort, hostData, staging: true);
            friend = StartApp(appPath, "--friend", friendPort, friendData, staging: true);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(friendPort));
            _ = await WaitForConnectedAsync(test, allowDisabled: true);
            AssertRehearsal(await PostAsync<RemoteRehearsalRequest, RemoteRehearsalReport>(test,
                $"/api/local/friend/{profileId}/rehearsal", request));
            _ = await PostAsync<object, PairingDecision>(owner, $"/api/local/devices/{deviceId}/revoke", new { });
            var revoked = await PostAsync<RemoteRehearsalRequest, RemoteRehearsalReport>(test,
                $"/api/local/friend/{profileId}/rehearsal", new(Guid.NewGuid()));
            Require(revoked.Stages.All(stage => stage.State != "Passed"), "revoked access passed a rehearsal");
            Require(connected.ConnectionId != Guid.Empty && !Directory.Exists(hostData + "-production-unused"),
                "test pairing or staging data isolation failed");
            Console.WriteLine("PASS packaged rehearsal: wrong profile and removed room denied, saved access/receipt survive both app restarts, revocation denies new checks");
            Console.WriteLine("Remote rehearsal: 2 groups passed, 0 failed. Loopback only; outside network and real game/load remain UNVERIFIED.");
        }
        finally { StopApp(friend); StopApp(host); }
    }

    private static void AssertRehearsal(RemoteRehearsalReport report)
    {
        Require(report.Schema == 1 && report.NetworkContext == "Loopback" && report.Stages.Count == 7,
            "loopback was mislabeled as a separate network");
        Require(report.Stages.Where(stage => stage.Id is "listener" or "connection" or "chat" or "transfer")
            .All(stage => stage.State == "Passed"), "rehearsal connection/chat/transfer failed: " + JsonSerializer.Serialize(report));
        Require(report.Stages.Where(stage => stage.Id is "outsideTcp" or "gameEndpoint" or "humanJoinLoad")
            .All(stage => stage.State == "Unverified"), "synthetic/TCP evidence was promoted to an external game claim");
    }

    private static async Task<T> RehearsalOwnerGetAsync<T>(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }
}
