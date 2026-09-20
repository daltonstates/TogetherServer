using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var appPath = Path.GetFullPath("local-data/publish/TogetherServer.exe");
var fixturePath = Path.GetFullPath("src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe");
if (!File.Exists(appPath) || !File.Exists(fixturePath)) throw new Exception("Run scripts/build.ps1 first.");
var root = Path.GetFullPath("local-data/companion-checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var hostData = Path.Combine(root, "host");
var friendAData = Path.Combine(root, "friend-a");
var friendBData = Path.Combine(root, "friend-b");
var world = Path.Combine(root, "world");
Directory.CreateDirectory(world);
var hostPort = FreeTcpPort();
var companionPort = FreeTcpPort(hostPort);
var friendAPort = FreeTcpPort(hostPort, companionPort);
var friendBPort = FreeTcpPort(hostPort, companionPort, friendAPort);
var gamePort = Random.Shared.Next(36000, 43000);
var endpoint = $"https://127.0.0.1:{companionPort}";
var profile = new ServerProfile { Name = "Fixture world", WorldId = "companion-fixture", WorldDirectory = world,
    GamePort = gamePort, ExecutablePath = fixturePath };
Process? host = null, friendA = null, friendB = null;
var passes = 0;
try
{
    host = StartApp(appPath, "--host", hostPort, hostData);
    await WaitLocal(hostPort);
    using var owner = LocalClient(hostPort);
    var settings = new HostSettings { MaxConcurrentServers = 1, Profiles = [profile], CompanionEndpoint = endpoint,
        CompanionPort = companionPort, CompanionBindAddress = "127.0.0.1", OwnerClientExecutablePath = fixturePath };
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "initial Host settings failed");
    var inviteA = await Invite(owner, "Friend A", true, false);
    var inviteB = await Invite(owner, "Friend B", false, true);
    settings.CompanionListeningEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "listener setting failed");
    StopApp(host);
    host = StartApp(appPath, "--host", hostPort, hostData);
    await WaitLocal(hostPort);
    var listener = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    Require(listener.GetProperty("listenerActive").GetBoolean(), "companion listener did not start");
    Console.WriteLine("PASS deliberate loopback HTTPS listener and one-time invites"); passes++;

    friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
    friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
    await WaitLocal(friendAPort); await WaitLocal(friendBPort);
    using var aLocal = LocalClient(friendAPort);
    using var bLocal = LocalClient(friendBPort);
    var tampered = inviteA with { Fingerprint = new string('0', 64) };
    var wrongPin = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(JsonSerializer.Serialize(tampered, webJson), fixturePath));
    Require(!wrongPin.Ok && wrongPin.Code == "Disconnected", "wrong Host pin was accepted");
    var pairedA = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(JsonSerializer.Serialize(inviteA, webJson), fixturePath));
    var pairedB = await OwnerPost<FriendPairRequest, FriendActionResult>(bLocal, "/api/local/friend/pair",
        new(JsonSerializer.Serialize(inviteB, webJson), fixturePath));
    Require(pairedA.Ok && pairedB.Ok, $"separate Friend processes did not pair: A={pairedA.Code} {pairedA.Message}, B={pairedB.Code} {pairedB.Message}");
    var secondUse = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(JsonSerializer.Serialize(inviteA, webJson), fixturePath));
    Require(!secondUse.Ok, "one-time invite was reused");
    var aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    var bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Disabled" && bView.State == "Disabled", "initial disabled notice missing");
    Console.WriteLine("PASS two Friend processes, wrong pin, one-time invite, disabled notice"); passes++;

    using var publicClient = PinnedClient(endpoint, inviteA.Fingerprint);
    using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
    invalid.Headers.Add("X-Device-Id", inviteB.DeviceId.ToString());
    invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
    using var invalidResponse = await publicClient.SendAsync(invalid);
    Require(invalidResponse.StatusCode == HttpStatusCode.Unauthorized, "invalid token was accepted");
    using var publicGui = await publicClient.GetAsync("/");
    Require(publicGui.StatusCode == HttpStatusCode.Forbidden, "public GUI route was exposed");
    Console.WriteLine("PASS invalid credential and public GUI isolation"); passes++;

    // A third activated device gives this check a credential for exact HTTP retry tests.
    var inviteC = await Invite(owner, "Retry device", true, false);
    var activation = await publicClient.PostAsJsonAsync("/api/companion/pair", new PairingActivation(inviteC.DeviceId, inviteC.Code), webJson);
    Require(activation.IsSuccessStatusCode, "retry device activation failed");
    var credentialC = await activation.Content.ReadFromJsonAsync<PairingCredential>(webJson) ?? throw new Exception("empty activation");
    var heartbeatC = new HeartbeatRequest(credentialC.DeviceId, Guid.NewGuid(), 1, "check", false);
    async Task<HttpStatusCode> SendHeartbeat()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/companion/heartbeat")
            { Content = JsonContent.Create(heartbeatC, options: webJson) };
        request.Headers.Add("X-Device-Id", credentialC.DeviceId.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialC.Credential);
        using var response = await publicClient.SendAsync(request);
        return response.StatusCode;
    }
    Require(await SendHeartbeat() == HttpStatusCode.OK && await SendHeartbeat() == HttpStatusCode.Conflict,
        "replayed heartbeat refreshed a device");
    settings.RemoteControlsEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "remote controls enable failed");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Connected" && bView.State == "Connected", "connected status missing");
    var deniedStart = await OwnerPost<object, FriendActionResult>(bLocal, $"/api/local/friend/{profile.Id}/start", new { });
    Require(deniedStart.Code == "PermissionDenied", "Friend Start permission was ignored");
    using (var injected = new HttpRequestMessage(HttpMethod.Post, "/api/companion/start"))
    {
        injected.Content = new StringContent(JsonSerializer.Serialize(new { deviceId = credentialC.DeviceId,
            profileId = profile.Id, script = "never execute this" }, webJson), Encoding.UTF8, "application/json");
        injected.Headers.Add("X-Device-Id", credentialC.DeviceId.ToString());
        injected.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        injected.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialC.Credential);
        using var rejected = await publicClient.SendAsync(injected);
        Require(rejected.StatusCode == HttpStatusCode.BadRequest, "extra command text was accepted");
    }
    var requestId = Guid.NewGuid();
    var first = await PublicAction(publicClient, credentialC, profile.Id, requestId, "start");
    var runningHost = (await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!;
    var firstPid = runningHost.Runs.Single().ProcessId;
    Require(runningHost.OwnerGameRunning == true, "Host did not perform its local synthetic game-client check");
    var retry = await PublicAction(publicClient, credentialC, profile.Id, requestId, "start");
    using (var conflicting = new HttpRequestMessage(HttpMethod.Post, "/api/companion/stop"))
    {
        conflicting.Content = JsonContent.Create(new RemoteActionRequest(credentialC.DeviceId, profile.Id), options: webJson);
        conflicting.Headers.Add("X-Device-Id", credentialC.DeviceId.ToString());
        conflicting.Headers.Add("Idempotency-Key", requestId.ToString());
        conflicting.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialC.Credential);
        using var rejected = await publicClient.SendAsync(conflicting);
        Require(rejected.StatusCode == HttpStatusCode.Conflict, "reused action key was accepted for a different action");
    }
    var retryPid = (await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs.Single().ProcessId;
    Require(first.Code == "FixtureStarted" && retry.Code == first.Code && firstPid == retryPid, "idempotent retry launched a second process");
    var duplicate = await OwnerPost<object, FriendActionResult>(aLocal, $"/api/local/friend/{profile.Id}/start", new { });
    Require(duplicate.Code == "AlreadyManaged", "another device launched a duplicate");
    var deniedStop = await OwnerPost<object, FriendActionResult>(aLocal, $"/api/local/friend/{profile.Id}/stop", new { });
    var unknownStop = await OwnerPost<object, FriendActionResult>(bLocal, $"/api/local/friend/{profile.Id}/stop", new { });
    Require(deniedStop.Code == "PermissionDenied" && unknownStop.Code == "PlayerStateUnknown", "remote Stop safety or permissions failed");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.LocalGameRunning == true, "synthetic client-running transition was missed");
    Console.WriteLine("PASS permissions, idempotent Start, duplicate guard, Stop denial, synthetic true signal"); passes++;

    StopApp(host);
    host = null;
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Disconnected/Unknown", "Host outage was mistaken for a connected state");
    host = StartApp(appPath, "--host", hostPort, hostData);
    await WaitLocal(hostPort);
    Require((await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs.Single().State == "Process running", "Host restart did not reattach fixture");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Connected", "Friend did not reconnect after Host restart");
    settings.RemoteControlsEnabled = false;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "remote disable failed");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Disabled", "online Friend did not see disable notice");
    var disabledAction = await OwnerPost<object, FriendActionResult>(aLocal, $"/api/local/friend/{profile.Id}/start", new { });
    Require(disabledAction.Code == "RemoteControlsDisabled", "Host accepted remote command after disable");
    StopApp(friendB);
    friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
    await WaitLocal(friendBPort);
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.State == "Disabled", "offline Friend did not see disable on reconnect");
    Console.WriteLine("PASS Host restart, online and reconnect disable notice, immediate server denial"); passes++;

    StopApp(friendA);
    friendA = null;
    var revoked = await OwnerPost<object, PairingDecision>(owner, $"/api/local/devices/{inviteA.DeviceId}/revoke", new { });
    Require(revoked.Ok, "revoke failed");
    friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
    await WaitLocal(friendAPort);
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Revoked" && bView.State == "Disabled", "revoke affected wrong device or was not shown");
    settings.RemoteControlsEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "re-enable failed");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.State == "Connected", "second device lost authorization");
    var localStop = await OwnerPost<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/stop", new { });
    Require(localStop.Ok, "local fixture stop failed");
    Require((await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.OwnerGameRunning == false,
        "Host did not observe its local synthetic client close");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.LocalGameRunning == false, "synthetic client-closed transition was missed");
    Console.WriteLine("PASS isolated revocation and synthetic false signal"); passes++;

    var rotationResult = await OwnerPost<InviteRequest, JsonElement>(owner, "/api/local/devices/invite",
        new("Retry device", true, false, inviteC.DeviceId));
    Require(rotationResult.GetProperty("ok").GetBoolean(), "rotation invite failed");
    var rotationInvite = JsonSerializer.Deserialize<PairingInvite>(rotationResult.GetProperty("invitation").GetString()!, webJson)!;
    using var rotationResponse = await publicClient.PostAsJsonAsync("/api/companion/pair",
        new PairingActivation(rotationInvite.DeviceId, rotationInvite.Code), webJson);
    Require(rotationResponse.IsSuccessStatusCode, "rotation activation failed");
    var rotatedCredential = await rotationResponse.Content.ReadFromJsonAsync<PairingCredential>(webJson) ?? throw new Exception("empty rotation");
    using var oldTokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
    oldTokenRequest.Headers.Add("X-Device-Id", credentialC.DeviceId.ToString());
    oldTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialC.Credential);
    using var oldTokenResponse = await publicClient.SendAsync(oldTokenRequest);
    using var newTokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
    newTokenRequest.Headers.Add("X-Device-Id", rotatedCredential.DeviceId.ToString());
    newTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotatedCredential.Credential);
    using var newTokenResponse = await publicClient.SendAsync(newTokenRequest);
    Require(oldTokenResponse.StatusCode == HttpStatusCode.Unauthorized && newTokenResponse.IsSuccessStatusCode,
        "rotation did not replace the old credential");
    Console.WriteLine("PASS credential rotation invalidates old token"); passes++;

    settings.RemoteControlsEnabled = false;
    settings.CompanionListeningEnabled = false;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "companion disable failed");
    using (var disabledStatus = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status"))
    {
        disabledStatus.Headers.Add("X-Device-Id", rotatedCredential.DeviceId.ToString());
        disabledStatus.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotatedCredential.Credential);
        using var response = await publicClient.SendAsync(disabledStatus);
        Require(response.StatusCode == HttpStatusCode.Forbidden, "companion requests remained available after listener disable");
    }
    settings.CompanionListeningEnabled = true;
    settings.RemoteControlsEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "companion re-enable failed");
    var clearPath = await OwnerPost<ClientPathRequest, FriendActionResult>(bLocal,
        "/api/local/friend/client-path", new(""));
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(clearPath.Ok && bView.LocalGameRunning is null, "cleared Friend client path was not Unknown");
    var restorePath = await OwnerPost<ClientPathRequest, FriendActionResult>(bLocal,
        "/api/local/friend/client-path", new(fixturePath));
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(restorePath.Ok && bView.LocalGameRunning == false, "Friend client path did not recover");
    Console.WriteLine("PASS immediate companion disable and Friend client-path recovery"); passes++;

    StopApp(friendB);
    friendB = null;
    await Task.Delay(TimeSpan.FromSeconds(47));
    var staleInfo = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    var staleDevice = staleInfo.GetProperty("devices").EnumerateArray()
        .Single(device => device.GetProperty("id").GetGuid() == inviteB.DeviceId);
    Require(staleDevice.GetProperty("lastHeartbeatUtc").ValueKind == JsonValueKind.Null &&
        staleDevice.GetProperty("gameRunning").ValueKind == JsonValueKind.Null,
        "missed Friend heartbeat was treated as a fresh false signal");
    friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
    await WaitLocal(friendBPort);
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.State == "Connected", "Friend did not recover after a stale heartbeat");
    Console.WriteLine("PASS stale heartbeat becomes Unknown and fresh reconnect recovers"); passes++;

    var limited = false;
    for (var i = 0; i < 75; i++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
        request.Headers.Add("X-Device-Id", inviteB.DeviceId.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var response = await publicClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) limited = true;
    }
    Require(limited, "companion authentication was not rate limited");
    Console.WriteLine("PASS repeated invalid authentication is rate limited"); passes++;

    Console.WriteLine($"Companion checks: {passes} groups passed, 0 failed. Data: {root}");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL companion check after {passes} passing groups: {ex}");
    Console.WriteLine("Data: " + root);
    return 1;
}
finally
{
    StopApp(friendA); StopApp(friendB);
    if (host is { HasExited: false })
    {
        try
        {
            using var owner = LocalClient(hostPort);
            var snapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
            if (snapshot?.Runs.SingleOrDefault()?.State == "Process running")
                await OwnerPost<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/stop", new { });
        }
        catch { Console.WriteLine("Fixture cleanup via Host failed; inspect the recorded process."); }
    }
    StopApp(host);
}

static int FreeTcpPort(params int[] exclude)
{
    for (var i = 0; i < 200; i++)
    {
        var port = Random.Shared.Next(51000, 60000);
        if (exclude.Contains(port)) continue;
        try
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            listener.Start(); listener.Stop();
            return port;
        }
        catch (System.Net.Sockets.SocketException) { }
    }
    throw new Exception("No local TCP port available.");
}

static Process StartApp(string path, string mode, int port, string data)
{
    Directory.CreateDirectory(data);
    var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(mode); info.ArgumentList.Add("--port"); info.ArgumentList.Add(port.ToString());
    info.Environment["TOGETHERSERVER_DATA_DIR"] = data;
    info.Environment["Logging__LogLevel__Default"] = "Warning";
    var process = Process.Start(info) ?? throw new Exception("App did not start.");
    process.BeginOutputReadLine(); process.BeginErrorReadLine();
    return process;
}

static void StopApp(Process? process)
{
    if (process is null) return;
    try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    catch (InvalidOperationException) { }
    process.Dispose();
}

static async Task WaitLocal(int port)
{
    using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromMilliseconds(300) };
    for (var i = 0; i < 80; i++)
    {
        try { using var response = await client.GetAsync("/api/local/snapshot"); if (response.IsSuccessStatusCode) return; }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        await Task.Delay(100);
    }
    throw new Exception($"Local app on {port} did not start.");
}

static HttpClient LocalClient(int port) => new() { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(8) };

async Task<TResponse> OwnerPost<TRequest, TResponse>(HttpClient client, string path, TRequest body)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: webJson) };
    request.Headers.Add("Origin", client.BaseAddress!.ToString().TrimEnd('/'));
    request.Headers.Add("X-TogetherServer-Local", "1");
    using var response = await client.SendAsync(request);
    return await response.Content.ReadFromJsonAsync<TResponse>(webJson) ?? throw new Exception($"Empty local POST {path}: {(int)response.StatusCode}");
}

async Task<TResponse> OwnerPut<TRequest, TResponse>(HttpClient client, string path, TRequest body)
{
    using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body, options: webJson) };
    request.Headers.Add("Origin", client.BaseAddress!.ToString().TrimEnd('/'));
    request.Headers.Add("X-TogetherServer-Local", "1");
    using var response = await client.SendAsync(request);
    return await response.Content.ReadFromJsonAsync<TResponse>(webJson) ?? throw new Exception($"Empty local PUT {path}: {(int)response.StatusCode}");
}

async Task<PairingInvite> Invite(HttpClient owner, string name, bool start, bool stop)
{
    var result = await OwnerPost<InviteRequest, JsonElement>(owner, "/api/local/devices/invite", new(name, start, stop, null));
    Require(result.GetProperty("ok").GetBoolean(), "invite creation failed: " + result.GetProperty("message").GetString());
    return JsonSerializer.Deserialize<PairingInvite>(result.GetProperty("invitation").GetString()!, webJson)!;
}

static HttpClient PinnedClient(string endpoint, string fingerprint)
{
    var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
        cert is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), Convert.FromHexString(fingerprint)) };
    return new HttpClient(handler) { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(8) };
}

async Task<FriendActionResult> PublicAction(HttpClient client, PairingCredential credential, Guid profileId, Guid key, string action)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/companion/" + action)
        { Content = JsonContent.Create(new RemoteActionRequest(credential.DeviceId, profileId), options: webJson) };
    request.Headers.Add("X-Device-Id", credential.DeviceId.ToString());
    request.Headers.Add("Idempotency-Key", key.ToString());
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
    using var response = await client.SendAsync(request);
    return await response.Content.ReadFromJsonAsync<FriendActionResult>(webJson) ?? throw new Exception("Empty public action response.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
