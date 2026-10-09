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
            await AssertRehearsalAsync(report, "initial", owner, test, profileId, deviceId, connected);
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
            await AssertRehearsalAsync(repeat, "repeat", owner, test, profileId, deviceId, connected);
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
            await AssertRehearsalAsync(await PostAsync<RemoteRehearsalRequest, RemoteRehearsalReport>(test,
                $"/api/local/friend/{profileId}/rehearsal", request), "restart", owner, test, profileId, deviceId, connected);
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

    private static async Task AssertRehearsalAsync(RemoteRehearsalReport report, string phase,
        HttpClient owner, HttpClient test, Guid profileId, Guid deviceId, FriendView connected)
    {
        try { AssertRehearsal(report, phase); }
        catch
        {
            // These are current GET-only facts after failure, not the lost
            // preflight result. Never repeat Check/Receive or the rehearsal.
            try
            {
                var selectedTask = ReadRehearsalFailureAsync<FriendView>(test, "/api/local/snapshot");
                var receivedTask = ReadRehearsalFailureAsync<ReceivedSharedWorldStatus>(test,
                    $"/api/local/friend/{profileId}/shared-world");
                var devicesTask = ReadRehearsalFailureAsync<RehearsalCompanionDevices>(owner, "/api/local/companion");
                var rosterTask = ReadRehearsalFailureAsync<SharedWorldRoster>(owner,
                    $"/api/local/profiles/{profileId}/shared-world/governance");
                await Task.WhenAll(selectedTask, receivedTask, devicesTask, rosterTask);
                var selected = selectedTask.Result.Value;
                var received = receivedTask.Result.Value;
                var device = devicesTask.Result.Value?.Devices?.SingleOrDefault(item => item.Id == deviceId);
                var roster = rosterTask.Result.Value;
                var member = roster?.Members?.SingleOrDefault(item => item.DeviceId == deviceId);
                Console.WriteLine("REHEARSAL_FAILURE_CURRENT_STATE " + JsonSerializer.Serialize(new
                {
                    phase,
                    selectedRead = selectedTask.Result.ReadState,
                    selectedHttp = selectedTask.Result.HttpState,
                    selectedCode = selectedTask.Result.Code,
                    selectedConnected = selected?.State == "Connected",
                    selectedDisabled = selected?.State == "Disabled",
                    selectedConnectionMatches = selected?.ConnectionId == connected.ConnectionId,
                    selectedHostMatches = selected?.HostId == connected.HostId,
                    selectedProfileAssigned = selected?.Profiles?.Any(item => item.Id == profileId) == true,
                    sharingCapability = selected?.HostCapabilities?.Contains(CompanionProtocol.SharedWorldsCapability) == true,
                    receivedRead = receivedTask.Result.ReadState,
                    receivedHttp = receivedTask.Result.HttpState,
                    receivedCode = receivedTask.Result.Code,
                    consented = received?.Consented == true,
                    hostVersionPresent = received?.HostVersion is not null,
                    thisPcVersionPresent = received?.ThisPcVersion is not null,
                    versionsMatch = received?.HostVersion is not null && received.HostVersion == received.ThisPcVersion,
                    receiptConfirmed = received?.ReceiptConfirmed == true,
                    receiveErrorPresent = received?.Error is not null,
                    receivePhasePresent = received?.TransferPhase is not null,
                    devicesRead = devicesTask.Result.ReadState,
                    devicesHttp = devicesTask.Result.HttpState,
                    devicesCode = devicesTask.Result.Code,
                    deviceFound = device is not null,
                    devicePaired = device?.Paired == true,
                    deviceRevoked = device?.Revoked == true,
                    deviceApprovalPending = device?.ApprovalPending == true,
                    deviceAccessExpired = device?.AccessExpired == true,
                    deviceProfileAssigned = device?.AssignedProfileIds?.Contains(profileId) == true,
                    deviceReceiveGranted = device?.SharedWorldGrants?.GetValueOrDefault(profileId)?.Receive == true,
                    deviceSigningEnrolled = device?.SharedWorldKeyEnrolled == true,
                    rosterRead = rosterTask.Result.ReadState,
                    rosterHttp = rosterTask.Result.HttpState,
                    rosterCode = rosterTask.Result.Code,
                    rosterProfileMatches = roster?.ProfileId == profileId,
                    rosterSignatureVerified = roster is not null && SharedWorldRosterTrust.VerifySignature(roster),
                    rosterFloorMatches = received?.RosterRevision is not null && received.RosterRevision == roster?.Revision,
                    rosterMemberFound = member is not null,
                    rosterMemberRevoked = member?.Revoked == true,
                    rosterReceiveGranted = member?.Grants?.Receive == true,
                    rosterMemberExpired = member?.AccessExpiresUtc is { } expiry && expiry <= DateTimeOffset.UtcNow
                }));
            }
            catch { Console.WriteLine("REHEARSAL_FAILURE_CURRENT_STATE " + JsonSerializer.Serialize(new { phase, readState = "Unavailable" })); }
            throw;
        }
    }

    private sealed record RehearsalCompanionDevices(IReadOnlyList<DeviceView>? Devices);
    private sealed record RehearsalFailureRead<T>(string ReadState, string HttpState, string Code, T? Value) where T : class;

    private static async Task<RehearsalFailureRead<T>> ReadRehearsalFailureAsync<T>(HttpClient client, string path) where T : class
    {
        // Failure-only diagnostics have one bounded local GET, with no retries.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var httpState = "Unobserved";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-TogetherServer-Local", "1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            httpState = response.StatusCode.ToString();
            const int maximumBytes = 64 * 1024;
            if (response.Content.Headers.ContentLength > maximumBytes) return new("Oversized", httpState, "Unobserved", null);
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + read > maximumBytes) return new("Oversized", httpState, "Unobserved", null);
                output.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var code = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("code", out var field) &&
                field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 and <= 64 } value &&
                value.All(char.IsAsciiLetterOrDigit) ? value : "Absent";
            return response.IsSuccessStatusCode
                ? new("Read", httpState, code, document.RootElement.Deserialize<T>(Json))
                : new("HttpDenied", httpState, code, null);
        }
        catch (OperationCanceledException) { return new("Cancelled", httpState, "Unobserved", null); }
        catch (JsonException) { return new("InvalidJson", httpState, "Unobserved", null); }
        catch { return new("Unavailable", httpState, "Unobserved", null); }
    }

    private static void AssertRehearsal(RemoteRehearsalReport report, string phase)
    {
        Require(report.Schema == 1 && report.NetworkContext == "Loopback" && report.Stages.Count == 7,
            $"{phase}: loopback was mislabeled as a separate network");
        Require(report.Stages.Where(stage => stage.Id is "listener" or "connection" or "chat" or "transfer")
            .All(stage => stage.State == "Passed"), $"{phase}: rehearsal connection/chat/transfer failed: " + JsonSerializer.Serialize(report));
        Require(report.Stages.Where(stage => stage.Id is "outsideTcp" or "gameEndpoint" or "humanJoinLoad")
            .All(stage => stage.State == "Unverified"), $"{phase}: synthetic/TCP evidence was promoted to an external game claim");
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
