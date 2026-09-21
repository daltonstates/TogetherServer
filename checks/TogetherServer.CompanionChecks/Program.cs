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
var appPath = Path.GetFullPath(args.Length > 0 ? args[0] : "local-data/release/TogetherServer.exe");
var fixturePath = Path.GetFullPath("src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe");
var valheimFixturePath = Path.GetFullPath("src/TogetherServer.ValheimFixture/bin/Release/net10.0/valheim_server.exe");
if (!File.Exists(appPath) || !File.Exists(fixturePath) || !File.Exists(valheimFixturePath))
    throw new Exception("Run scripts/build.ps1 first.");
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
var peerHostPort = FreeTcpPort(hostPort, companionPort, friendAPort, friendBPort);
var peerCompanionPort = FreeTcpPort(hostPort, companionPort, friendAPort, friendBPort, peerHostPort);
var gamePort = Random.Shared.Next(36000, 43000);
var endpoint = $"https://127.0.0.1:{companionPort}";
var profile = new ServerProfile { Name = "Fixture world", WorldId = "companion-fixture", WorldDirectory = world,
    GamePort = gamePort, ExecutablePath = fixturePath };
var joinWorld = Path.Combine(root, "join-world");
Directory.CreateDirectory(joinWorld);
var joinProfile = new ServerProfile { Kind = "Valheim", Name = "Friend join example", ServerName = "Friend join example",
    WorldId = "join-example", WorldDirectory = joinWorld, GamePort = gamePort + 4, ExecutablePath = fixturePath };
Process? host = null, friendA = null, friendB = null, peerHost = null, stopHost = null, stopFriend = null;
var stopHostPort = 0;
var stopProfileId = Guid.Empty;
var passes = 0;
try
{
    host = StartApp(appPath, "--host", hostPort, hostData);
    await WaitLocal(hostPort);
    using var owner = LocalClient(hostPort);
    var settings = new HostSettings { MaxConcurrentServers = 1, Profiles = [profile, joinProfile], CompanionEndpoint = endpoint,
        PublicGameIp = "1.2.3.4", PublicGameIpCheckedUtc = DateTimeOffset.UtcNow.AddHours(-2),
        CompanionPort = companionPort, CompanionBindAddress = "127.0.0.1", OwnerClientExecutablePath = fixturePath };
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "initial Host settings failed");
    var inviteA = await Invite(owner, "Friend A", true, false);
    var inviteB = await Invite(owner, "Friend B", false, true);
    var passwordA = PairingPassword.Encode(inviteA);
    Require(passwordA.StartsWith("TS2-", StringComparison.Ordinal) &&
        PairingPassword.TryDecode(passwordA, null, out var decoded) &&
        decoded!.DeviceId == inviteA.DeviceId && decoded.Code == inviteA.Code &&
        decoded.Endpoint == endpoint &&
        decoded.Fingerprint == inviteA.Fingerprint &&
        !PairingPassword.TryDecode("wrong-password", endpoint, out _) &&
        !PairingPassword.TryDecode(PairingPassword.Encode(inviteA with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }), endpoint, out _),
        "generated password did not preserve the device secret and full TLS pin or reject invalid values");
    settings.CompanionEndpoint = $"https://127.0.0.2:{companionPort}";
    var changedPinnedAddress = await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings);
    Require(!changedPinnedAddress.Ok && changedPinnedAddress.Code == "HostAddressPinned",
        "the Host app address changed after its TLS identity was pinned");
    settings.CompanionEndpoint = endpoint;
    var enableInvite = await OwnerPost<InviteRequest, JsonElement>(owner, "/api/local/devices/invite",
        new("Immediate connection", true, false, null, true));
    Require(enableInvite.GetProperty("ok").GetBoolean() && enableInvite.GetProperty("listenerActive").GetBoolean(),
        "creating an invite did not start the companion listener immediately");
    settings.CompanionListeningEnabled = true;
    settings.RemoteControlsEnabled = false;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "remote control pause failed");
    var listener = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    Require(listener.GetProperty("listenerActive").GetBoolean(), "companion listener did not start");
    Console.WriteLine("PASS one-step invite starts the loopback HTTPS listener without restart"); passes++;

    friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
    friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
    await WaitLocal(friendAPort); await WaitLocal(friendBPort);
    using var aLocal = LocalClient(friendAPort);
    using var bLocal = LocalClient(friendBPort);
    var tampered = inviteA with { Fingerprint = new string('0', 64) };
    Require(HostIdentity.TryAddress("127.0.0.1", out var defaultAddress) &&
        defaultAddress == "https://127.0.0.1:5131" &&
        HostIdentity.TryAddress($"127.0.0.1:{companionPort}", out var explicitAddress) && explicitAddress == endpoint &&
        !HostIdentity.TryAddress("http://127.0.0.1", out _),
        "Host IP with optional control port was not normalized safely");
    var wrongAddress = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(JsonSerializer.Serialize(inviteA, webJson), fixturePath, "127.0.0.1:9999"));
    Require(wrongAddress.Code == "HostAddressMismatch", "pairing ignored an address that differed from the pinned invite");
    var wrongPin = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(JsonSerializer.Serialize(tampered, webJson), fixturePath));
    Require(!wrongPin.Ok && wrongPin.Code == "Disconnected", "wrong Host pin was accepted");
    var pairedA = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(passwordA, fixturePath));
    Require(pairedA.Ok, "a current invite did not pair without a separate Host IP");
    var wrongPasswordPin = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(PairingPassword.Encode(tampered), fixturePath, $"127.0.0.1:{companionPort}"));
    Require(!wrongPasswordPin.Ok && wrongPasswordPin.Code == "Disconnected", "password pairing accepted the wrong Host TLS pin");
    var pairedB = await OwnerPost<FriendPairRequest, FriendActionResult>(bLocal, "/api/local/friend/pair",
        new(JsonSerializer.Serialize(inviteB, webJson), fixturePath));
    Require(pairedA.Ok && pairedB.Ok, $"separate Friend processes did not pair: A={pairedA.Code} {pairedA.Message}, B={pairedB.Code} {pairedB.Message}");
    var secondUse = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(passwordA, fixturePath, $"127.0.0.1:{companionPort}"));
    Require(!secondUse.Ok, "one-time invite was reused");
    var aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    var bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Disabled" && bView.State == "Disabled", "initial disabled notice missing");
    Require(aView.Profiles.Single(item => item.Id == joinProfile.Id).JoinAddress is null,
        "paired Friend received a stale Valheim join address");
    settings.PublicGameIpCheckedUtc = DateTimeOffset.UtcNow;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "fresh Host address update failed");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.Profiles.Single(item => item.Id == joinProfile.Id).JoinAddress == $"1.2.3.4:{joinProfile.GamePort}" &&
        aView.Profiles.Single(item => item.Id == profile.Id).JoinAddress is null,
        "paired Friend did not receive only the Valheim join address while controls were disabled");
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
    var firstPid = runningHost.Runs.Single(run => run.ProfileId == profile.Id).ProcessId;
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
    var retryPid = (await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs.Single(run => run.ProfileId == profile.Id).ProcessId;
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
    Require((await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs.Single(run => run.ProfileId == profile.Id).State == "Process running", "Host restart did not reattach fixture");
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
    var peerData = Path.Combine(root, "peer-host");
    var peerEndpoint = $"https://127.0.0.1:{peerCompanionPort}";
    peerHost = StartApp(appPath, "--host", peerHostPort, peerData);
    await WaitLocal(peerHostPort);
    using var peerOwner = LocalClient(peerHostPort);
    var peerSettings = new HostSettings { CompanionEndpoint = peerEndpoint, CompanionPort = peerCompanionPort,
        CompanionBindAddress = "127.0.0.1" };
    Require((await OwnerPut<HostSettings, ActionResult>(peerOwner, "/api/local/settings", peerSettings)).Ok,
        "second Host settings failed");
    var peerInvite = await Invite(peerOwner, "Owner PC", false, false);
    peerSettings.CompanionListeningEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(peerOwner, "/api/local/settings", peerSettings)).Ok,
        "second Host listener settings failed");
    StopApp(peerHost);
    peerHost = StartApp(appPath, "--host", peerHostPort, peerData);
    await WaitLocal(peerHostPort);
    var blockedMode = await owner.PostAsync("/api/local/mode/friend", new StringContent("{}", Encoding.UTF8, "application/json"));
    Require(blockedMode.StatusCode == HttpStatusCode.Forbidden,
        "mode change without local owner headers was accepted");
    var runningMode = await OwnerPost<object, JsonElement>(owner, "/api/local/mode/friend", new { });
    var ownFriendLink = await OwnerPost<FriendPairRequest, FriendActionResult>(owner, "/api/local/friend/pair",
        new(PairingPassword.Encode(peerInvite), fixturePath, $"127.0.0.1:{peerCompanionPort}"));
    var joinedPeer = await OwnerPost<object, FriendView>(owner, "/api/local/friend/poll", new { });
    var hostWhileJoining = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(runningMode.GetProperty("ok").GetBoolean() && ownFriendLink.Ok &&
        joinedPeer.State == "Disabled" && joinedPeer.Endpoint == peerEndpoint &&
        hostWhileJoining.GetProperty("listenerActive").GetBoolean() && bView.State == "Connected" &&
        (await owner.GetFromJsonAsync<JsonElement>("/api/local/snapshot")).GetProperty("mode").GetString() == "Friend",
        "linking a second Host interrupted this PC's running server or companion listener");
    var quitWhileJoining = await OwnerPost<object, JsonElement>(owner, "/api/local/quit", new { });
    Require(!quitWhileJoining.GetProperty("ok").GetBoolean() && quitWhileJoining.GetProperty("code").GetString() == "ManagedRunPresent",
        "Friend view let the owner quit while a managed server was running");
    var duplicateWhileJoining = await PublicAction(publicClient, credentialC, profile.Id, Guid.NewGuid(), "start");
    Require(duplicateWhileJoining.Code == "AlreadyManaged", "remote Host actions stopped working while the owner joined another Host");
    Require((await OwnerPost<object, JsonElement>(owner, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(),
        "owner could not return to Host view with a managed server running");
    var localStop = await OwnerPost<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/stop", new { });
    Require(localStop.Ok, "local fixture stop failed");
    Require((await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.OwnerGameRunning == false,
        "Host did not observe its local synthetic client close");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.LocalGameRunning == false, "synthetic client-closed transition was missed");
    Console.WriteLine("PASS isolated revocation and synthetic false signal"); passes++;

    var friendViewAfterStop = await OwnerPost<object, JsonElement>(owner, "/api/local/mode/friend", new { });
    var listenerAfterStop = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    Require(friendViewAfterStop.GetProperty("ok").GetBoolean() && listenerAfterStop.GetProperty("listenerActive").GetBoolean(),
        "opening connected Hosts after Stop disabled this PC's companion listener");
    using (var activeStatus = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status"))
    {
        activeStatus.Headers.Add("X-Device-Id", credentialC.DeviceId.ToString());
        activeStatus.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialC.Credential);
        using var response = await publicClient.SendAsync(activeStatus);
        Require(response.StatusCode == HttpStatusCode.OK, "Friend view interrupted authenticated Host status");
    }
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.State == "Connected", "a Friend lost connection when the owner opened connected Hosts");
    StopApp(host);
    host = StartApp(appPath, "--friend", hostPort, hostData);
    await WaitLocal(hostPort);
    var restartedAsFriend = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(restartedAsFriend.GetProperty("listenerActive").GetBoolean() && bView.State == "Connected" &&
        (await owner.GetFromJsonAsync<JsonElement>("/api/local/snapshot")).GetProperty("mode").GetString() == "Friend",
        "restarting on Friends' servers page failed to restore this PC's Host listener");
    var hostModeResult = await OwnerPost<object, JsonElement>(owner, "/api/local/mode/host", new { });
    Require(hostModeResult.GetProperty("ok").GetBoolean(), "owner could not return to the Host dashboard after Stop");
    Console.WriteLine("PASS two Host PCs link while the first keeps serving its game and paired Friend"); passes++;

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
        var rejected = false;
        try
        {
            using var response = await publicClient.SendAsync(disabledStatus);
            rejected = response.StatusCode == HttpStatusCode.Forbidden;
        }
        catch (HttpRequestException) { rejected = true; }
        Require(rejected, "companion requests remained available after listener disable");
    }
    Require(!(await owner.GetFromJsonAsync<JsonElement>("/api/local/companion")).GetProperty("listenerActive").GetBoolean(),
        "disabled companion listener remained active");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.State == "Disconnected/Unknown", "a disabled listener was falsely reported as credential revocation");
    settings.CompanionListeningEnabled = true;
    settings.RemoteControlsEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "companion re-enable failed");
    var clearPath = await OwnerPost<ClientPathRequest, FriendActionResult>(bLocal,
        "/api/local/friend/client-path", new(""));
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(clearPath.Ok && bView.LocalGameRunning is null && bView.ClientExecutablePath == "",
        "cleared Friend client path was not Unknown");
    var restorePath = await OwnerPost<ClientPathRequest, FriendActionResult>(bLocal,
        "/api/local/friend/client-path", new(fixturePath));
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(restorePath.Ok && bView.LocalGameRunning == false && bView.ClientExecutablePath == fixturePath,
        "Friend client path did not recover");
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

    var stopHostData = Path.Combine(root, "stop-host");
    var stopFriendData = Path.Combine(root, "stop-friend");
    stopHostPort = FreeTcpPort(hostPort, companionPort, friendAPort, friendBPort, peerHostPort, peerCompanionPort);
    var stopPublicPort = FreeTcpPort(stopHostPort, hostPort, companionPort, friendAPort, friendBPort, peerHostPort, peerCompanionPort);
    var stopFriendPort = FreeTcpPort(stopHostPort, stopPublicPort, hostPort, companionPort, friendAPort, friendBPort, peerHostPort, peerCompanionPort);
    var stopEndpoint = $"https://127.0.0.1:{stopPublicPort}";
    var stopProfile = new ServerProfile { Kind = "Valheim", Name = "Restricted synthetic world",
        ServerName = "Fixture \"Valheim\"", WorldSource = "New", WorldId = "fixture-world",
        ExecutablePath = valheimFixturePath, GamePort = gamePort + 20 };
    stopProfileId = stopProfile.Id;
    stopProfile.WorldDirectory = Path.Combine(stopHostData, "worlds", stopProfile.Id.ToString("N"));
    stopHost = StartApp(appPath, "--host", stopHostPort, stopHostData, stopDelayMs: 7000);
    stopFriend = StartApp(appPath, "--friend", stopFriendPort, stopFriendData);
    await WaitLocal(stopHostPort); await WaitLocal(stopFriendPort);
    using var stopOwner = LocalClient(stopHostPort);
    using var stopFriendLocal = LocalClient(stopFriendPort);
    var stopSettings = new HostSettings { Profiles = [stopProfile], CompanionEndpoint = stopEndpoint,
        CompanionBindAddress = "127.0.0.1", CompanionPort = stopPublicPort };
    Require((await OwnerPut<HostSettings, ActionResult>(stopOwner, "/api/local/settings", stopSettings)).Ok,
        "restricted Host settings failed");
    Require((await OwnerPost<ValheimPasswordRequest, ActionResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/password", new("fixture-pass-123"))).Ok,
        "restricted Host password failed");
    var stopInviteResponse = await OwnerPost<InviteRequest, JsonElement>(stopOwner, "/api/local/devices/invite",
        new("Stop Friend", true, true, null, true));
    Require(stopInviteResponse.GetProperty("listenerActive").GetBoolean(), "restricted Host listener did not start");
    var stopInvite = JsonSerializer.Deserialize<PairingInvite>(stopInviteResponse.GetProperty("invitation").GetString()!, webJson)!;
    var stopPair = await OwnerPost<FriendPairRequest, FriendActionResult>(stopFriendLocal, "/api/local/friend/pair",
        new(PairingPassword.Encode(stopInvite), fixturePath));
    Require(stopPair.Ok, "restricted Friend did not pair from one invite");
    var setId = await OwnerPut<DevicePlayerIdRequest, PairingDecision>(stopOwner,
        $"/api/local/devices/{stopInvite.DeviceId}/player-id", new("V_123456789"));
    Require(setId.Ok, "restricted Friend player ID was not saved");
    var listCreated = await OwnerPost<object, StopListResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/permitted-list", new { });
    Require(listCreated.Ok, "restricted permitted-player list was not created");
    Require((await OwnerPost<object, ActionResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/start", new { })).Ok, "restricted synthetic start failed");
    var ready = false;
    for (var i = 0; i < 60; i++)
    {
        var state = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs.Single().State;
        if (state == "Ready") { ready = true; break; }
        await Task.Delay(100);
    }
    Require(ready, "restricted synthetic server never reached Ready");
    var stopView = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    Require(stopView.Profiles.Single().CanStopNow, "Friend UI did not receive available remote Stop");
    var remoteStop = await OwnerPost<object, FriendActionResult>(stopFriendLocal,
        $"/api/local/friend/{stopProfile.Id}/stop", new { });
    Require(remoteStop.Ok && remoteStop.Code == "ValheimStopped", $"remote Stop failed: {remoteStop.Code} {remoteStop.Message}");
    Require(File.ReadAllText(Path.Combine(stopProfile.WorldDirectory, "synthetic-stop.marker")) == "Ctrl+C received",
        "remote Stop did not use the synthetic console's graceful exit");
    Console.WriteLine("PASS paired Friend remotely stops restricted synthetic Valheim through HTTPS and Ctrl+C"); passes++;

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
    StopApp(friendA); StopApp(friendB); StopApp(peerHost); StopApp(stopFriend);
    if (stopHost is { HasExited: false })
    {
        try
        {
            using var stopOwner = LocalClient(stopHostPort);
            var snapshot = await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
            if (snapshot?.Runs.SingleOrDefault(run => run.ProfileId == stopProfileId)?.State is "Ready" or "Starting")
                await OwnerPost<object, ActionResult>(stopOwner, $"/api/local/profiles/{stopProfileId}/stop", new { });
        }
        catch { Console.WriteLine("Restricted fixture cleanup via Host failed; inspect the recorded process."); }
    }
    StopApp(stopHost);
    if (host is { HasExited: false })
    {
        try
        {
            using var owner = LocalClient(hostPort);
            var snapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
            if (snapshot?.Runs.SingleOrDefault(run => run.ProfileId == profile.Id)?.State == "Process running")
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

static Process StartApp(string path, string mode, int port, string data, int stopDelayMs = 0)
{
    Directory.CreateDirectory(data);
    var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(mode); info.ArgumentList.Add("--port"); info.ArgumentList.Add(port.ToString());
    info.Environment["TOGETHERSERVER_DATA_DIR"] = data;
    info.Environment["TOGETHERSERVER_FIXTURE_ROOT"] = data;
    if (stopDelayMs > 0) info.Environment["TOGETHERSERVER_FIXTURE_STOP_DELAY_MS"] = stopDelayMs.ToString();
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
    var invite = JsonSerializer.Deserialize<PairingInvite>(result.GetProperty("invitation").GetString()!, webJson)!;
    Require(result.GetProperty("password").GetString() == PairingPassword.Encode(invite),
        "Host did not return the generated copy/paste password");
    return invite;
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
