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
var replacementProfileId = Guid.Empty;
var passes = 0;
try
{
    const int probePort = 5131;
    const string publicEndpoint = "https://1.2.3.4:5131";
    var (reachable, reachedPaths) = await ProbeFake(publicEndpoint, probePort, path => path switch
    {
        "/api/me" => ProbeResponse("1.2.3.4"),
        "/api/me/5131" => ProbeResponse("True"),
        _ => ProbeResponse("unexpected", HttpStatusCode.BadRequest)
    });
    Require(reachable.State == "Reachable" && reachable.Port == probePort &&
        reachable.Endpoint == publicEndpoint &&
        reachedPaths.SequenceEqual(["/api/me", "/api/me/5131"]),
        "the external TCP probe did not verify the advertised IP before checking the fixed port");
    var (blocked, blockedPaths) = await ProbeFake(publicEndpoint, probePort, path => path switch
    {
        "/api/me" => ProbeResponse("1.2.3.4"),
        "/api/me/5131" => ProbeResponse("False"),
        _ => ProbeResponse("unexpected", HttpStatusCode.BadRequest)
    });
    Require(blocked.State == "Not reachable" && blocked.Endpoint == publicEndpoint && blockedPaths.Count == 2,
        "a closed outside TCP route was reported reachable");
    Console.WriteLine("PASS outside TCP probe distinguishes reachable and blocked fixed-port results"); passes++;

    var (wrongProbeAddress, wrongAddressPaths) = await ProbeFake(publicEndpoint, probePort, _ => ProbeResponse("5.6.7.8"));
    Require(wrongProbeAddress.State == "Unavailable" && wrongProbeAddress.Endpoint == publicEndpoint &&
        wrongAddressPaths.SequenceEqual(["/api/me"]),
        "the external TCP probe tested a route after the service saw a different public IP");
    var (invalidTarget, invalidTargetPaths) = await ProbeFake("https://127.0.0.1:5131", probePort,
        _ => ProbeResponse("1.2.3.4"));
    Require(invalidTarget.State == "Unavailable" && invalidTarget.Endpoint is null && invalidTargetPaths.Count == 0,
        "the external TCP probe sent a private endpoint to the outside service");
    var (serviceError, serviceErrorPaths) = await ProbeFake(publicEndpoint, probePort,
        _ => ProbeResponse("unavailable", HttpStatusCode.ServiceUnavailable));
    Require(serviceError.State == "Inconclusive" && serviceErrorPaths.SequenceEqual(["/api/me"]),
        "a checker service failure was interpreted as a closed Host port");
    Console.WriteLine("PASS outside TCP probe fails closed on wrong IP, private target, and checker error"); passes++;

    var migrationRoot = Path.Combine(root, "assignment-migration");
    var existingScopedProfile = Guid.NewGuid();
    var existingScopedDevice = Guid.NewGuid();
    var existingLegacyDevice = Guid.NewGuid();
    using (var migrationData = new LocalData(migrationRoot))
    {
        migrationData.SaveDevices([
            new PairedDevice { Id = existingScopedDevice, ProfileId = existingScopedProfile },
            new PairedDevice { Id = existingLegacyDevice, ProfileId = Guid.Empty }
        ]);
        _ = new PairingService(migrationData);
        var migrated = migrationData.LoadDevices();
        Require(migrated.Single(device => device.Id == existingScopedDevice).AssignedProfileIds!
                .SequenceEqual([existingScopedProfile]) &&
            migrated.Single(device => device.Id == existingLegacyDevice).AssignedProfileIds!.Count == 0,
            "existing scoped and legacy credentials did not migrate to fail-closed explicit server access");
    }
    Console.WriteLine("PASS existing scoped access is preserved while legacy unscoped access fails closed"); passes++;

    var pairingPolicyRoot = Path.Combine(root, "pairing-policy");
    using (var pairingData = new LocalData(pairingPolicyRoot))
    {
        var pairingProfile = Guid.NewGuid();
        var pairingService = new PairingService(pairingData);
        var window = pairingService.IssueServer(pairingProfile, true, false,
            "https://127.0.0.1:5131", new string('A', 64), false,
            durationMinutes: 30, deviceLimit: 2, requireApproval: true);
        var policyFirst = pairingService.Activate(new(pairingProfile, window.Code, true));
        var policySecond = pairingService.Activate(new(pairingProfile, window.Code, true));
        var overLimit = pairingService.Activate(new(pairingProfile, window.Code, true));
        Require(policyFirst?.ApprovalPending == true && policySecond?.ApprovalPending == true && overLimit is null,
            "pairing window approval or device limit was not enforced");
        var pending = pairingService.Authenticate(policyFirst!.DeviceId, policyFirst.Credential, out _);
        Require(!pending.Ok && pending.Code == "ApprovalPending" && pairingService.Approve(policyFirst.DeviceId).Ok &&
            pairingService.Authenticate(policyFirst.DeviceId, policyFirst.Credential, out _).Ok,
            "a waiting PC authenticated before local approval or remained blocked afterward");
        var reopened = pairingService.IssueServer(pairingProfile, true, false,
            window.Endpoint, window.Fingerprint, false, durationMinutes: 60, deviceLimit: 1);
        Require(reopened.Code != window.Code && reopened.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(59),
            "changing the pairing duration did not open a new bounded window");
        Require(pairingService.ClosePairing(pairingProfile).Ok &&
            pairingService.Activate(new(pairingProfile, reopened.Code, true)) is null &&
            pairingService.Authenticate(policyFirst.DeviceId, policyFirst.Credential, out _).Ok,
            "closing pairing revoked an existing PC or left the shared code usable");
        Require(pairingService.EmergencyRevoke(pairingProfile).Ok &&
            pairingService.Authenticate(policyFirst.DeviceId, policyFirst.Credential, out _).Code == "Revoked" &&
            pairingData.LoadActivity().All(item => !item.Message.Contains(window.Code, StringComparison.Ordinal)),
            "emergency revoke did not invalidate code-issued credentials or activity exposed a code");
    }
    Console.WriteLine("PASS bounded pairing windows separate approval, close, per-PC credentials, and emergency revoke"); passes++;

    host = StartApp(appPath, "--host", hostPort, hostData);
    await WaitLocal(hostPort);
    using var owner = LocalClient(hostPort);
    var settings = new HostSettings { MaxConcurrentServers = 1, Profiles = [profile, joinProfile], CompanionEndpoint = endpoint,
        PublicGameIp = "1.2.3.4", PublicGameIpCheckedUtc = DateTimeOffset.UtcNow.AddHours(-2),
        CompanionPort = companionPort, CompanionBindAddress = "127.0.0.1" };
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "initial Host settings failed");
    var inviteA = await ServerInvite(owner, profile.Id, true, enableConnections: true);
    var inviteB = await ServerInvite(owner, joinProfile.Id, false);
    var passwordA = PairingPassword.Encode(inviteA);
    Require(passwordA.StartsWith("TS3-", StringComparison.Ordinal) &&
        PairingPassword.TryDecode(passwordA, null, out var decoded) &&
        decoded!.ServerScope && decoded.DeviceId == profile.Id && decoded.Code == inviteA.Code &&
        decoded.Endpoint == endpoint &&
        decoded.Fingerprint == inviteA.Fingerprint &&
        !PairingPassword.TryDecode("wrong-password", endpoint, out _) &&
        !PairingPassword.TryDecode(PairingPassword.Encode(inviteA with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }), endpoint, out _),
        "generated server code did not preserve its server scope, secret, and full TLS pin or reject invalid values");
    var currentInvite = await OwnerPost<object, JsonElement>(owner,
        $"/api/local/servers/{profile.Id}/invite/current", new { });
    var currentJoinInvite = await OwnerPost<object, JsonElement>(owner,
        $"/api/local/servers/{joinProfile.Id}/invite/current", new { });
    Require(currentInvite.GetProperty("exists").GetBoolean() && currentInvite.GetProperty("password").GetString() == passwordA &&
        currentInvite.GetProperty("canStart").GetBoolean() && !currentJoinInvite.GetProperty("canStart").GetBoolean(),
        "the Host did not return the same current code and saved Start default for each server");
    settings.CompanionEndpoint = $"https://127.0.0.2:{companionPort}";
    var changedPinnedAddress = await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings);
    var identityAfterEndpointChange = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    Require(changedPinnedAddress.Ok &&
        identityAfterEndpointChange.GetProperty("fingerprint").GetString() == inviteA.Fingerprint,
        "changing the advertised endpoint replaced or rejected the stable Host identity");
    settings.CompanionEndpoint = endpoint;
    settings.CompanionListeningEnabled = true;
    settings.RemoteControlsEnabled = false;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "remote control pause failed");
    var listener = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    Require(listener.GetProperty("listenerActive").GetBoolean(), "companion listener did not start");
    Console.WriteLine("PASS one current bounded code per server starts the loopback HTTPS listener without restart"); passes++;
    var copiedWhilePaused = await ServerInvite(owner, profile.Id, true, enableConnections: true);
    var pausedSnapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
    Require(copiedWhilePaused.Code == inviteA.Code && pausedSnapshot?.Settings.RemoteControlsEnabled == false &&
        pausedSnapshot.Settings.CompanionListeningEnabled,
        "copying an existing invite silently re-enabled remote Start and Stop after the Host paused them");
    Console.WriteLine("PASS copying an invite keeps the Host's remote controls paused"); passes++;

    var localPorts = await owner.GetFromJsonAsync<PortDiagnosticsView>("/api/local/network/ports");
    Require(localPorts?.Control.State == "Open on PC" && localPorts.Control.BindAddress == "127.0.0.1" &&
        localPorts.Control.BindScope == "Loopback only" && localPorts.Control.EndpointState == "Local only" &&
        localPorts.Control.RemoteState == "Not verified",
        "loopback HTTPS listener was mistaken for a public Friend route");
    Console.WriteLine("PASS companion diagnostics distinguish a live loopback listener from public reachability"); passes++;

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
        new(JsonSerializer.Serialize(inviteA, webJson), "127.0.0.1:9999"));
    Require(wrongAddress.Code == "HostAddressMismatch", "pairing ignored an address that differed from the pinned invite");
    var wrongPin = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(JsonSerializer.Serialize(tampered, webJson)));
    Require(!wrongPin.Ok && wrongPin.Code == "HostIdentityMismatch", "wrong Host pin was accepted");
    var rejectedCode = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(PairingPassword.Encode(inviteA with { Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })));
    Require(!rejectedCode.Ok && rejectedCode.Code == "PairingRejected" && rejectedCode.Message.Contains("current server code"),
        "a rejected server code was mistaken for a network or TLS failure");
    var pairedA = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(passwordA));
    Require(pairedA.Ok, "a current invite did not pair without a separate Host IP");
    var wrongPasswordPin = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(PairingPassword.Encode(tampered), $"127.0.0.1:{companionPort}"));
    Require(!wrongPasswordPin.Ok && wrongPasswordPin.Code == "HostIdentityMismatch", "password pairing accepted the wrong Host TLS pin");
    var pairedB = await OwnerPost<FriendPairRequest, FriendActionResult>(bLocal, "/api/local/friend/pair",
        new(passwordA));
    Require(pairedA.Ok && pairedB.Ok, $"separate Friend processes did not pair: A={pairedA.Code} {pairedA.Message}, B={pairedB.Code} {pairedB.Message}");
    var pairedDevices = (await owner.GetFromJsonAsync<JsonElement>("/api/local/companion")).GetProperty("devices").EnumerateArray()
        .Where(device => device.GetProperty("profileId").GetGuid() == profile.Id).ToArray();
    Require(pairedDevices.Length == 2 && pairedDevices.Select(device => device.GetProperty("id").GetGuid()).Distinct().Count() == 2 &&
        pairedDevices.All(device => device.GetProperty("assignedProfileIds").EnumerateArray()
            .Select(value => value.GetGuid()).SequenceEqual([profile.Id])),
        "the reusable server code did not issue separate device credentials scoped only to that server");
    var deviceAId = pairedDevices[0].GetProperty("id").GetGuid();
    var deviceBId = pairedDevices[1].GetProperty("id").GetGuid();
    const string deviceBName = "Morgan's gaming PC";
    var missingName = await OwnerPut<DeviceNameRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/name", new(null));
    var multilineName = await OwnerPut<DeviceNameRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/name", new("Morgan's\ngaming PC"));
    var longName = await OwnerPut<DeviceNameRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/name", new(new string('x', 49)));
    var renamed = await OwnerPut<DeviceNameRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/name", new($"  {deviceBName}  "));
    var renamedView = (await owner.GetFromJsonAsync<JsonElement>("/api/local/companion")).GetProperty("devices")
        .EnumerateArray().Single(device => device.GetProperty("id").GetGuid() == deviceBId);
    Require(!missingName.Ok && missingName.Code == "InvalidDeviceName" &&
        !multilineName.Ok && multilineName.Code == "InvalidDeviceName" &&
        !longName.Ok && longName.Code == "InvalidDeviceName" && renamed.Ok &&
        renamedView.GetProperty("name").GetString() == deviceBName,
        "Friend PC name validation or trimmed rename failed");
    Require((await OwnerPut<DevicePermissionRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/permissions", new(false, true))).Ok,
        "second Friend permissions were not saved");
    var aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    var bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Disabled" && bView.State == "Disabled", "initial disabled notice missing");
    Require(aView.Profiles.Count == 1 && aView.Profiles.Single().Id == profile.Id,
        "a server code exposed a different server profile");
    var differentServerDenied = await FriendAction(bLocal, joinProfile.Id, "start");
    Require(differentServerDenied.Code == "PermissionDenied",
        "a Friend controlled a server that was not assigned to its code or device");
    var unknownServerAssignment = await OwnerPut<DeviceServerAccessRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/servers", new([profile.Id, Guid.NewGuid()]));
    var duplicateServerAssignment = await OwnerPut<DeviceServerAccessRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/servers", new([profile.Id, profile.Id]));
    Require(!unknownServerAssignment.Ok && unknownServerAssignment.Code == "UnknownServer" &&
        !duplicateServerAssignment.Ok && duplicateServerAssignment.Code == "InvalidServerAccess",
        "the Host assigned a Friend to an unknown or duplicate server ID");
    var assignedBoth = await OwnerPut<DeviceServerAccessRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/servers", new([profile.Id, joinProfile.Id]));
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(assignedBoth.Ok && bView.Profiles.Select(item => item.Id).ToHashSet()
            .SetEquals([profile.Id, joinProfile.Id]),
        "the Host could not assign one Friend PC to multiple saved servers");
    var serverPermissions = new[]
    {
        new DeviceServerPermissionRequest(profile.Id, false, true),
        new DeviceServerPermissionRequest(joinProfile.Id, true, false)
    };
    Require((await OwnerPut<DeviceServerAccessRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/servers", new([profile.Id, joinProfile.Id], serverPermissions))).Ok,
        "per-server Start and Stop exceptions were not saved");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    var profilePermission = bView.Profiles.Single(item => item.Id == profile.Id);
    var joinPermission = bView.Profiles.Single(item => item.Id == joinProfile.Id);
    var mixedDevice = (await owner.GetFromJsonAsync<JsonElement>("/api/local/companion")).GetProperty("devices")
        .EnumerateArray().Single(device => device.GetProperty("id").GetGuid() == deviceBId);
    Require(!profilePermission.CanStart && profilePermission.CanStop && joinPermission.CanStart && !joinPermission.CanStop &&
        !mixedDevice.GetProperty("canStart").GetBoolean() && mixedDevice.GetProperty("canStop").GetBoolean() &&
        mixedDevice.GetProperty("serverPermissions").EnumerateArray().Count() == 2,
        "Friend status or Host mixed-permission data ignored per-server exceptions");
    Require((await OwnerPut<DevicePermissionRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/permissions", new(true, true, "start"))).Ok,
        "the global Start control did not apply to every assigned server");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    profilePermission = bView.Profiles.Single(item => item.Id == profile.Id);
    joinPermission = bView.Profiles.Single(item => item.Id == joinProfile.Id);
    Require(profilePermission.CanStart && joinPermission.CanStart && profilePermission.CanStop && !joinPermission.CanStop,
        "changing global Start erased a per-server Stop exception");
    Require((await OwnerPut<DevicePermissionRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/permissions", new(false, true, "start"))).Ok,
        "the global Start control could not be returned to its prior default");
    Require((await OwnerPut<DeviceServerAccessRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/servers", new([profile.Id, joinProfile.Id], serverPermissions))).Ok,
        "per-server exceptions could not be restored after a global permission change");
    var assignedButPaused = await FriendAction(bLocal, joinProfile.Id, "start");
    Require(assignedButPaused.Code == "RemoteControlsDisabled",
        "an assigned server did not pass server access before the separate global control gate");
    Require((await OwnerPut<DeviceServerAccessRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/servers", new([]))).Ok,
        "the Host could not remove every server assignment");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    var removedServerDenied = await FriendAction(bLocal, profile.Id, "start");
    Require(bView.Profiles.Count == 0 && removedServerDenied.Code == "PermissionDenied",
        "removing assignments did not immediately hide and deny server access");
    Require((await OwnerPut<DeviceServerAccessRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceBId}/servers", new([profile.Id, joinProfile.Id]))).Ok,
        "the Host could not restore multiple server assignments");
    Console.WriteLine("PASS codes grant server access while per-server Start and Stop exceptions remain authoritative"); passes++;
    var pairedSecondGame = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(PairingPassword.Encode(inviteB)));
    Require(pairedSecondGame.Ok, "second server pairing replaced the first or failed");
    var multiple = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(multiple.Connections?.Count == 2 && multiple.Profiles.Single().Kind == GameKinds.Valheim &&
        multiple.Connections.Any(connection => connection.Profiles.SingleOrDefault()?.Id == profile.Id),
        "Friend did not retain both independently scoped saved connections and game kinds");
    var firstConnection = multiple.Connections!.Single(connection => connection.Profiles.Single().Id == profile.Id);
    Require((await OwnerPost<object, FriendActionResult>(aLocal,
        $"/api/local/friend/connections/{firstConnection.ConnectionId}/select", new { })).Ok,
        "Friend could not select the earlier saved connection");
    var renamedConnection = await OwnerPut<DeviceNameRequest, FriendActionResult>(aLocal,
        $"/api/local/friend/connections/{firstConnection.ConnectionId}/name", new("Weekend Host"));
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(renamedConnection.Ok && aView.ConnectionName == "Weekend Host" &&
        aView.Profiles.Count == 1 && aView.Profiles.Single().Id == profile.Id,
        "selecting the earlier server lost its pairing: " + JsonSerializer.Serialize(aView, webJson));
    var secondConnection = multiple.Connections!.Single(connection => connection.ConnectionId != firstConnection.ConnectionId);
    settings.PublicGameIpCheckedUtc = DateTimeOffset.UtcNow;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "fresh Host address update failed");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.Profiles.Single().JoinAddress is null,
        "a fixture server exposed a Valheim join address");
    Console.WriteLine("PASS saved Friend connections can be selected and renamed"); passes++;

    using var publicClient = PinnedClient(endpoint, inviteA.Fingerprint);
    using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
    invalid.Headers.Add("X-Device-Id", deviceBId.ToString());
    invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
    using var invalidResponse = await publicClient.SendAsync(invalid);
    Require(invalidResponse.StatusCode == HttpStatusCode.Unauthorized, "invalid token was accepted");
    using var publicGui = await publicClient.GetAsync("/");
    Require(publicGui.StatusCode == HttpStatusCode.Forbidden, "public GUI route was exposed");
    Console.WriteLine("PASS invalid credential and public GUI isolation"); passes++;

    // The same code creates a third, distinct credential for exact HTTP retry tests.
    var activation = await publicClient.PostAsJsonAsync("/api/companion/pair",
        new PairingActivation(inviteA.DeviceId, inviteA.Code, true), webJson);
    Require(activation.IsSuccessStatusCode, "retry device activation failed");
    var credentialC = await activation.Content.ReadFromJsonAsync<PairingCredential>(webJson) ?? throw new Exception("empty activation");
    var priorCredentialC = credentialC;
    var renewalId = Guid.NewGuid();
    async Task<(HttpStatusCode Status, CredentialRenewal? Renewal)> RenewCredential(PairingCredential credential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/companion/credential/renew")
            { Content = JsonContent.Create(new CredentialRenewalRequest(credential.DeviceId, renewalId), options: webJson) };
        request.Headers.Add("X-Device-Id", credential.DeviceId.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
        using var response = await publicClient.SendAsync(request);
        return (response.StatusCode, response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CredentialRenewal>(webJson) : null);
    }
    var firstRenewal = await RenewCredential(priorCredentialC);
    var retriedRenewal = await RenewCredential(priorCredentialC);
    Require(firstRenewal.Status == HttpStatusCode.OK && retriedRenewal.Status == HttpStatusCode.OK &&
        firstRenewal.Renewal is not null && retriedRenewal.Renewal is not null &&
        firstRenewal.Renewal.Credential == retriedRenewal.Renewal.Credential &&
        firstRenewal.Renewal.RequestId == renewalId &&
        firstRenewal.Renewal.PreviousAcceptedUntilUtc > DateTimeOffset.UtcNow,
        "credential renewal was not idempotent or did not retain the old-token overlap");
    credentialC = new PairingCredential(credentialC.DeviceId, firstRenewal.Renewal!.Credential,
        firstRenewal.Renewal.ExpiresUtc);
    Require((await PublicStatus(publicClient, priorCredentialC)).Protocol?.Compatible == true &&
        (await PublicStatus(publicClient, credentialC)).Protocol?.Compatible == true,
        "credential renewal stranded either the old overlap token or the new token");
    Console.WriteLine("PASS credential renewal is idempotent with a bounded old-token overlap"); passes++;
    var joinActivation = await publicClient.PostAsJsonAsync("/api/companion/pair",
        new PairingActivation(inviteB.DeviceId, inviteB.Code, true), webJson);
    Require(joinActivation.IsSuccessStatusCode, "second server code activation failed");
    var joinCredential = await joinActivation.Content.ReadFromJsonAsync<PairingCredential>(webJson) ?? throw new Exception("empty second-server activation");
    var joinStatus = await PublicStatus(publicClient, joinCredential);
    Require(joinStatus.Profiles.Count == 1 && joinStatus.Profiles.Single().Id == joinProfile.Id &&
        joinStatus.Profiles.Single().JoinAddress == $"1.2.3.4:{joinProfile.GamePort}" &&
        joinStatus.Protocol is { ProtocolVersion: CompanionProtocol.Current, Compatible: true } &&
        joinStatus.Protocol.Capabilities.Contains("durable-operations"),
        "a credential did not remain scoped to its server and current join address: " +
        JsonSerializer.Serialize(joinStatus, webJson));
    var heartbeatC = new HeartbeatRequest(credentialC.DeviceId, Guid.NewGuid(), 1, "check");
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
    localPorts = await owner.GetFromJsonAsync<PortDiagnosticsView>("/api/local/network/ports");
    Require(localPorts?.Control.RemoteState == "Friend connected" &&
        localPorts.Control.RemoteDetail.Contains("network location is unknown", StringComparison.Ordinal),
        "an authenticated same-PC heartbeat was presented as an outside-network connection");
    Console.WriteLine("PASS fresh authenticated heartbeat does not claim outside-network reachability"); passes++;
    settings.RemoteControlsEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "remote controls enable failed");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Connected" && bView.State == "Connected", "connected status missing: A=" +
        JsonSerializer.Serialize(aView, webJson) + " B=" + JsonSerializer.Serialize(bView, webJson));
    var deniedStart = await FriendAction(bLocal, profile.Id, "start");
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
    Require(first.Code == "FixtureStarted" && retry.Code == first.Code && firstPid == retryPid,
        $"idempotent retry changed the result or launched twice: first={first.Code}/{first.OperationState}/{firstPid}, retry={retry.Code}/{retry.OperationState}/{retryPid}");
    var duplicate = await FriendAction(aLocal, profile.Id, "start");
    Require(duplicate.Code == "AlreadyManaged", "another device launched a duplicate");
    var deniedStop = await FriendAction(aLocal, profile.Id, "stop");
    var unknownStop = await FriendAction(bLocal, profile.Id, "stop");
    Require(deniedStop.Code == "PermissionDenied" && unknownStop.Code == "ServerNotReady",
        $"remote Stop safety or permissions failed: denied={deniedStop.Code}, unsupported={unknownStop.Code}");
    Console.WriteLine("PASS permissions, idempotent Start, duplicate guard, and guarded Stop denial"); passes++;

    StopApp(host);
    host = null;
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Disconnected/Unknown", "Host outage was mistaken for a connected state");
    host = StartApp(appPath, "--host", hostPort, hostData);
    await WaitLocal(hostPort);
    Require((await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs.Single(run => run.ProfileId == profile.Id).State == "Process running", "Host restart did not reattach fixture");
    var restoredDevice = (await owner.GetFromJsonAsync<JsonElement>("/api/local/companion")).GetProperty("devices")
        .EnumerateArray().Single(device => device.GetProperty("id").GetGuid() == deviceBId);
    Require(restoredDevice.GetProperty("name").GetString() == deviceBName &&
        restoredDevice.GetProperty("assignedProfileIds").EnumerateArray().Select(value => value.GetGuid()).ToHashSet()
            .SetEquals([profile.Id, joinProfile.Id]),
        "renamed Friend PC name or multiple server assignments did not survive Host restart");
    Console.WriteLine("PASS Friend PC names and multiple server assignments persist after Host restart"); passes++;
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Connected" && bView.Profiles.Select(item => item.Id).ToHashSet()
            .SetEquals([profile.Id, joinProfile.Id]),
        "Friend did not reconnect with its saved server assignments after Host restart");
    settings.RemoteControlsEnabled = false;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "remote disable failed");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Disabled", "online Friend did not see disable notice");
    var disabledAction = await FriendAction(aLocal, profile.Id, "start");
    Require(disabledAction.Code == "RemoteControlsDisabled", "Host accepted remote command after disable");
    StopApp(friendB);
    friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
    await WaitLocal(friendBPort);
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.State == "Disabled", "offline Friend did not see disable on reconnect");
    Console.WriteLine("PASS Host restart, online and reconnect disable notice, immediate server denial"); passes++;

    StopApp(friendA);
    friendA = null;
    var revoked = await OwnerPost<object, PairingDecision>(owner, $"/api/local/devices/{deviceAId}/revoke", new { });
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
    var peerWorld = Path.Combine(root, "peer-world");
    Directory.CreateDirectory(peerWorld);
    var peerProfile = new ServerProfile { Name = "Peer fixture", WorldId = "peer-fixture",
        WorldDirectory = peerWorld, GamePort = gamePort + 10, ExecutablePath = fixturePath };
    peerHost = StartApp(appPath, "--host", peerHostPort, peerData);
    await WaitLocal(peerHostPort);
    using var peerOwner = LocalClient(peerHostPort);
    var peerSettings = new HostSettings { CompanionEndpoint = peerEndpoint, CompanionPort = peerCompanionPort,
        CompanionBindAddress = "127.0.0.1", Profiles = [peerProfile] };
    Require((await OwnerPut<HostSettings, ActionResult>(peerOwner, "/api/local/settings", peerSettings)).Ok,
        "second Host settings failed");
    var peerInvite = await ServerInvite(peerOwner, peerProfile.Id, false);
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
        new(PairingPassword.Encode(peerInvite), $"127.0.0.1:{peerCompanionPort}"));
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
    Console.WriteLine("PASS isolated revocation and concurrent Host/Friend operation"); passes++;

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

    var rotationInvite = await ServerInvite(owner, profile.Id, true, refresh: true);
    using var rotationResponse = await publicClient.PostAsJsonAsync("/api/companion/pair",
        new PairingActivation(rotationInvite.DeviceId, rotationInvite.Code, true), webJson);
    Require(rotationResponse.IsSuccessStatusCode, "rotation activation failed");
    var rotatedCredential = await rotationResponse.Content.ReadFromJsonAsync<PairingCredential>(webJson) ?? throw new Exception("empty rotation");
    using var oldCodeResponse = await publicClient.PostAsJsonAsync("/api/companion/pair",
        new PairingActivation(inviteA.DeviceId, inviteA.Code, true), webJson);
    using var oldTokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
    oldTokenRequest.Headers.Add("X-Device-Id", credentialC.DeviceId.ToString());
    oldTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialC.Credential);
    using var oldTokenResponse = await publicClient.SendAsync(oldTokenRequest);
    using var newTokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
    newTokenRequest.Headers.Add("X-Device-Id", rotatedCredential.DeviceId.ToString());
    newTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotatedCredential.Credential);
    using var newTokenResponse = await publicClient.SendAsync(newTokenRequest);
    using var otherServerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
    otherServerRequest.Headers.Add("X-Device-Id", joinCredential.DeviceId.ToString());
    otherServerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", joinCredential.Credential);
    using var otherServerResponse = await publicClient.SendAsync(otherServerRequest);
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(oldCodeResponse.StatusCode == HttpStatusCode.Unauthorized &&
        oldTokenResponse.StatusCode == HttpStatusCode.Forbidden && newTokenResponse.IsSuccessStatusCode &&
        otherServerResponse.IsSuccessStatusCode && bView.State == "Revoked",
        "refresh did not revoke the old server code and access while preserving the other server");
    var repairedB = await OwnerPost<FriendPairRequest, FriendActionResult>(bLocal, "/api/local/friend/pair",
        new(PairingPassword.Encode(rotationInvite)));
    Require(repairedB.Ok, "Friend could not reconnect with the refreshed server code");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.State == "Connected" && bView.Connections?.Count == 2,
        "new pairing did not keep both saved connections or select the working one");
    var selectedBConnection = bView.ConnectionId;
    deviceBId = (await owner.GetFromJsonAsync<JsonElement>("/api/local/companion")).GetProperty("devices").EnumerateArray()
        .Where(device => device.GetProperty("profileId").GetGuid() == profile.Id && !device.GetProperty("revoked").GetBoolean())
        .Select(device => device.GetProperty("id").GetGuid()).Single(id => id != rotatedCredential.DeviceId);
    Console.WriteLine("PASS refreshing one server code revokes its old code and devices but preserves another server"); passes++;

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
    var closedPortPair = await OwnerPost<FriendPairRequest, FriendActionResult>(bLocal, "/api/local/friend/pair",
        new(PairingPassword.Encode(rotationInvite)));
    Require(!closedPortPair.Ok && closedPortPair.Code == "HostPortClosed" &&
        !closedPortPair.Message.Contains("Test from internet", StringComparison.OrdinalIgnoreCase),
        "a closed Host listener was not reported as a local TCP refusal");
    settings.CompanionListeningEnabled = true;
    settings.RemoteControlsEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "companion re-enable failed");
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.State == "Connected", "Friend did not recover after companion re-enable");
    Console.WriteLine("PASS immediate companion disable and Friend reconnect recovery"); passes++;

    StopApp(friendB);
    friendB = null;
    await Task.Delay(TimeSpan.FromSeconds(47));
    var staleInfo = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    var staleDevice = staleInfo.GetProperty("devices").EnumerateArray()
        .Single(device => device.GetProperty("id").GetGuid() == deviceBId);
    Require(staleDevice.GetProperty("lastHeartbeatUtc").ValueKind == JsonValueKind.Null &&
        !staleDevice.TryGetProperty("gameRunning", out _),
        "missed Friend heartbeat was treated as fresh or retained the removed game-running field");
    friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
    await WaitLocal(friendBPort);
    bView = await OwnerPost<object, FriendView>(bLocal, "/api/local/friend/poll", new { });
    Require(bView.State == "Connected" && bView.ConnectionId == selectedBConnection,
        "Friend did not restore its selected connection after a stale heartbeat and app restart");
    Console.WriteLine("PASS stale heartbeat becomes Unknown and fresh reconnect recovers"); passes++;

    var limited = false;
    for (var i = 0; i < 220; i++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
        request.Headers.Add("X-Device-Id", deviceBId.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var response = await publicClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) limited = true;
    }
    Require(limited, "companion authentication was not rate limited");
    var independentDeviceStatus = await PublicStatus(publicClient, joinCredential);
    Require(independentDeviceStatus.Protocol?.Compatible == true,
        "one device's authentication burst throttled another device behind the same source IP");
    Console.WriteLine("PASS per-device authentication limiting preserves another device behind one source IP"); passes++;

    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    var recoveryConnection = aView.Connections!.Single(connection =>
        connection.Profiles.SingleOrDefault()?.Id == joinProfile.Id);
    Require((await OwnerPost<object, FriendActionResult>(aLocal,
        $"/api/local/friend/connections/{recoveryConnection.ConnectionId}/select", new { })).Ok,
        "could not select the non-throttled connection for recovery checks");
    var staged = await OwnerPost<object, JsonElement>(owner, "/api/local/companion/certificate/stage", new { });
    Require(staged.GetProperty("ok").GetBoolean(), "the Host could not stage its next certificate");
    var stagedCertificates = (await owner.GetFromJsonAsync<JsonElement>("/api/local/companion"))
        .GetProperty("certificates");
    var nextFingerprint = stagedCertificates.GetProperty("nextFingerprint").GetString();
    Require(nextFingerprint is { Length: 64 }, "staged certificate fingerprint was not exposed locally");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Connected" && aView.CertificateExpiresUtc is not null && aView.HostId != Guid.Empty,
        "the Friend did not receive authenticated certificate and Host identity metadata");
    var activated = await OwnerPost<object, JsonElement>(owner, "/api/local/companion/certificate/activate", new { });
    Require(activated.GetProperty("ok").GetBoolean(), "the Host could not activate its staged certificate: " + activated);
    var oldPinRejected = false;
    using var oldOnlyAfterRotation = PinnedClient(endpoint, inviteA.Fingerprint);
    try
    {
        using var oldPinRequest = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
        oldPinRequest.Headers.Add("X-Device-Id", joinCredential.DeviceId.ToString());
        oldPinRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", joinCredential.Credential);
        using var response = await oldOnlyAfterRotation.SendAsync(oldPinRequest);
        oldPinRejected = !response.IsSuccessStatusCode;
    }
    catch (HttpRequestException) { oldPinRejected = true; }
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(oldPinRejected && aView.State == "Connected",
        $"certificate activation did not reject an old-only pin or stranded a Friend that received the staged pin: oldRejected={oldPinRejected}, friend={JsonSerializer.Serialize(aView, webJson)}");

    var recoveredEndpoint = $"https://127.0.0.2:{companionPort}";
    settings.CompanionEndpoint = recoveredEndpoint;
    settings.CompanionBindAddress = "0.0.0.0";
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "the owner could not deliberately change the advertised endpoint while retaining Host identity");
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(aView.State == "Disconnected/Unknown", "a saved Friend endpoint changed silently without recovery proof");
    var recovered = await OwnerPut<EndpointRecoveryRequest, FriendActionResult>(aLocal,
        $"/api/local/friend/connections/{recoveryConnection.ConnectionId}/endpoint", new(recoveredEndpoint));
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(recovered.Ok && recovered.Code == "EndpointRecovered" && aView.State == "Connected" &&
        aView.Endpoint == recoveredEndpoint && aView.RouteMode == ConnectionRouteModes.DirectInternet,
        "endpoint recovery did not require and preserve the saved pin, credential, route, and Host identity");
    var badRecovery = await OwnerPut<EndpointRecoveryRequest, FriendActionResult>(aLocal,
        $"/api/local/friend/connections/{recoveryConnection.ConnectionId}/endpoint",
        new($"https://127.0.0.3:{companionPort}"));
    Require(!badRecovery.Ok, "endpoint recovery trusted an address that could not prove the saved Host");
    var currentAfterRecovery = await OwnerPost<object, JsonElement>(owner,
        $"/api/local/servers/{joinProfile.Id}/invite/current", new { });
    Require(PairingPassword.TryDecode(currentAfterRecovery.GetProperty("password").GetString(), null, out var recoveredInvite) &&
        recoveredInvite!.Endpoint == recoveredEndpoint && recoveredInvite.Fingerprint == nextFingerprint,
        "new invite copies did not advertise the recovered endpoint and active staged pin");
    var retired = await OwnerPost<object, JsonElement>(owner, "/api/local/companion/certificate/retire-previous", new { });
    Require(retired.GetProperty("ok").GetBoolean() &&
        retired.GetProperty("certificates").GetProperty("previousFingerprint").ValueKind == JsonValueKind.Null,
        "the owner could not retire the previous certificate pin after the grace path");
    Console.WriteLine("PASS staged certificate rotation and credential-bound endpoint recovery fail closed"); passes++;

    var forgotten = await OwnerPost<object, FriendActionResult>(aLocal,
        $"/api/local/friend/connections/{secondConnection.ConnectionId}/forget", new { });
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(forgotten.Ok && forgotten.Code == "ConnectionForgottenAndRevoked" && aView.Connections?.Count == 1,
        "reachable Forget did not revoke the device credential before removing the saved connection");
    Console.WriteLine("PASS reachable Forget revokes the credential before removing the saved connection"); passes++;

    var stopHostData = Path.Combine(root, "stop-host");
    var stopFriendData = Path.Combine(root, "stop-friend");
    stopHostPort = FreeTcpPort(hostPort, companionPort, friendAPort, friendBPort, peerHostPort, peerCompanionPort);
    var stopPublicPort = FreeTcpPort(stopHostPort, hostPort, companionPort, friendAPort, friendBPort, peerHostPort, peerCompanionPort);
    var stopFriendPort = FreeTcpPort(stopHostPort, stopPublicPort, hostPort, companionPort, friendAPort, friendBPort, peerHostPort, peerCompanionPort);
    var stopEndpoint = $"https://127.0.0.1:{stopPublicPort}";
    var stopProfile = new ServerProfile { Kind = "Valheim", Name = "Restricted synthetic world",
        ServerName = "Fixture \"Valheim\"", WorldSource = "New", WorldId = "fixture-world",
        ExecutablePath = valheimFixturePath, GamePort = gamePort + 20 };
    var replacementProfile = new ServerProfile { Kind = "Valheim", Name = "Replacement synthetic world",
        ServerName = "Fixture \"Valheim\"", WorldSource = "New", WorldId = "fixture-world",
        ExecutablePath = valheimFixturePath, GamePort = stopProfile.GamePort };
    stopProfileId = stopProfile.Id;
    replacementProfileId = replacementProfile.Id;
    stopProfile.WorldDirectory = Path.Combine(stopHostData, "worlds", stopProfile.Id.ToString("N"));
    replacementProfile.WorldDirectory = Path.Combine(stopHostData, "worlds", replacementProfile.Id.ToString("N"));
    stopHost = StartApp(appPath, "--host", stopHostPort, stopHostData, stopDelayMs: 7000);
    stopFriend = StartApp(appPath, "--friend", stopFriendPort, stopFriendData);
    await WaitLocal(stopHostPort); await WaitLocal(stopFriendPort);
    using var stopOwner = LocalClient(stopHostPort);
    using var stopFriendLocal = LocalClient(stopFriendPort);
    var stopSettings = new HostSettings { Profiles = [stopProfile, replacementProfile], CompanionEndpoint = stopEndpoint,
        CompanionBindAddress = "127.0.0.1", CompanionPort = stopPublicPort,
        AutoShutdownEnabled = true, IdleMinutes = 15,
        FriendTimerExtensionMinutes = 5, FriendTimerExtensionMaximumMinutes = 5 };
    Require((await OwnerPut<HostSettings, ActionResult>(stopOwner, "/api/local/settings", stopSettings)).Ok,
        "restricted Host settings failed");
    Require((await OwnerPost<ValheimPasswordRequest, ActionResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/password", new("fixture-pass-123"))).Ok,
        "restricted Host password failed");
    Require((await OwnerPost<ValheimPasswordRequest, ActionResult>(stopOwner,
        $"/api/local/profiles/{replacementProfile.Id}/password", new("fixture-pass-123"))).Ok,
        "replacement Host password failed");
    var stopInvite = await ServerInvite(stopOwner, stopProfile.Id, true, enableConnections: true);
    var stopPair = await OwnerPost<FriendPairRequest, FriendActionResult>(stopFriendLocal, "/api/local/friend/pair",
        new(PairingPassword.Encode(stopInvite)));
    Require(stopPair.Ok, "restricted Friend did not pair from the server code");
    var stopBeforePermission = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    Require(!stopBeforePermission.CanStop && !stopBeforePermission.Profiles.Single().CanStopNow &&
        !string.IsNullOrWhiteSpace(stopBeforePermission.Profiles.Single().StopReason),
        "new Friend did not receive distinct Stop permission and safety state");
    var stopDeviceId = (await stopOwner.GetFromJsonAsync<JsonElement>("/api/local/companion")).GetProperty("devices").EnumerateArray()
        .Single(device => device.GetProperty("profileId").GetGuid() == stopProfile.Id).GetProperty("id").GetGuid();
    Require((await OwnerPut<DevicePermissionRequest, PairingDecision>(stopOwner,
        $"/api/local/devices/{stopDeviceId}/permissions", new(true, true, CanExtendTimer: true))).Ok,
        "restricted Friend Stop permission was not saved");
    var pairedStopSnapshot = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!;
    stopSettings = pairedStopSnapshot.Settings;
    stopProfile = stopSettings.Profiles.Single(item => item.Id == stopProfileId);
    replacementProfile = stopSettings.Profiles.Single(item => item.Id == replacementProfileId);
    stopProfile.Maintenance = new MaintenanceOptions { Enabled = true, Message = "Owner maintenance check" };
    Require((await OwnerPut<HostSettings, ActionResult>(stopOwner, "/api/local/settings", stopSettings)).Ok,
        "maintenance mode could not be enabled while Offline");
    var maintenanceView = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    var maintenanceStart = await FriendAction(stopFriendLocal, stopProfile.Id, "start");
    Require(maintenanceView.Profiles.Single().MaintenanceEnabled &&
        maintenanceView.Profiles.Single().MaintenanceMessage == "Owner maintenance check" &&
        maintenanceView.Activity?.Any(item => item.Category == "Maintenance" && item.ProfileId == stopProfile.Id) == true &&
        !maintenanceStart.Ok && maintenanceStart.Code == "MaintenanceMode",
        "maintenance did not remain visible while denying remote lifecycle actions");
    stopProfile.Maintenance = new MaintenanceOptions();
    Require((await OwnerPut<HostSettings, ActionResult>(stopOwner, "/api/local/settings", stopSettings)).Ok,
        "maintenance mode could not be ended");
    var stopPreparing = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    Require(stopPreparing.CanStop && !stopPreparing.Profiles.Single().CanStopNow &&
        !string.IsNullOrWhiteSpace(stopPreparing.Profiles.Single().StopReason),
        "early Stop permission bypassed or hid the incomplete safety setup");
    var prematureStop = await FriendAction(stopFriendLocal, stopProfile.Id, "stop");
    Require(!prematureStop.Ok && prematureStop.Code == "ServerNotReady",
        "Host accepted Stop before the server was ready");
    Console.WriteLine("PASS maintenance stays visible while remote actions are denied, then releases cleanly"); passes++;
    Require((await OwnerPost<object, ActionResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/start", new { })).Ok, "restricted synthetic start failed");
    var ready = false;
    for (var i = 0; i < 60; i++)
    {
        var state = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs
            .Single(run => run.ProfileId == stopProfile.Id).State;
        if (state == "Ready") { ready = true; break; }
        await Task.Delay(100);
    }
    Require(ready, "restricted synthetic server never reached Ready");
    var gameEndpointAnswer = GameEndpointProbe.Check(new PublicProfile(stopProfile.Id, stopProfile.Name, "Ready",
        $"127.0.0.1:{stopProfile.GamePort}", Kind: GameKinds.Valheim));
    var unsupportedEndpointAnswer = GameEndpointProbe.Check(new PublicProfile(profile.Id, profile.Name, "Ready",
        $"127.0.0.1:{profile.GamePort}", Kind: GameKinds.Custom));
    Require(gameEndpointAnswer.Answered && gameEndpointAnswer.Code == "GameEndpointAnswered" &&
        gameEndpointAnswer.Message.StartsWith("Game endpoint answered from this PC", StringComparison.Ordinal) &&
        !gameEndpointAnswer.Message.Contains("Join verified", StringComparison.OrdinalIgnoreCase) &&
        !unsupportedEndpointAnswer.Answered && unsupportedEndpointAnswer.Code == "UnsupportedGameProbe",
        "Friend-side built-in game probing did not require a valid protocol reply or overstated a successful join");
    Console.WriteLine("PASS Friend-side game query reports an endpoint answer without claiming a join"); passes++;
    var readySnapshot = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!;
    var hostRun = readySnapshot.Runs.Single(run => run.ProfileId == stopProfile.Id);
    var hostDeadline = hostRun.AutoShutdownAtUtc;
    Require(hostRun.OnlinePlayers == 0 && hostRun.MaxPlayers == 10 &&
        hostDeadline is not null && hostDeadline.Value > DateTimeOffset.UtcNow.AddMinutes(14),
        "Host API did not start a server-count-only empty-server deadline without game-client settings");
    var friendExtended = await FriendAction(stopFriendLocal, stopProfile.Id, "extend");
    var friendLimit = await FriendAction(stopFriendLocal, stopProfile.Id, "extend");
    Require(friendExtended.Ok && friendExtended.Code == "CountdownExtended" &&
        !friendLimit.Ok && friendLimit.Code == "ExtensionLimitReached",
        "fixed Friend timer increment or per-countdown maximum was not enforced");
    var extendedCountdown = await OwnerPost<CountdownExtensionRequest, ActionResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/countdown/extend", new(37));
    Require(extendedCountdown.Ok, "Host could not extend an active countdown");
    hostDeadline = extendedCountdown.Snapshot.Runs.Single(run => run.ProfileId == stopProfile.Id).AutoShutdownAtUtc;
    Require(hostDeadline is not null && hostDeadline.Value > DateTimeOffset.UtcNow.AddMinutes(51),
        "Host countdown extension did not add the requested 37 minutes");
    var stopView = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    var friendProfile = stopView.Profiles.Single();
    Require(friendProfile.OnlinePlayers == 0 && friendProfile.MaxPlayers == 10 &&
        friendProfile.AutoShutdownAtUtc == hostDeadline && friendProfile.CanStopNow && friendProfile.StopReason is null,
        "Friend UI did not receive the player count, shared countdown, and available remote Stop state");
    var playerCountPath = Path.Combine(stopProfile.WorldDirectory, "synthetic-online-players.txt");
    File.WriteAllText(playerCountPath, "1");
    FriendView? occupiedView = null;
    for (var i = 0; i < 80; i++)
    {
        occupiedView = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
        if (occupiedView.Profiles.Single().OnlinePlayers == 1 &&
            occupiedView.Profiles.Single().AutoShutdownAtUtc is null) break;
        await Task.Delay(100);
    }
    var observedOccupied = occupiedView ?? throw new Exception("Friend status was unavailable after player-count change");
    Require(observedOccupied.Profiles.Single().OnlinePlayers == 1 &&
        observedOccupied.Profiles.Single().AutoShutdownAtUtc is null && !observedOccupied.Profiles.Single().CanStopNow &&
        observedOccupied.Profiles.Single().StopReason?.Contains("1 player is online", StringComparison.Ordinal) == true,
        "Friend UI did not cancel the countdown and receive the online-player Stop blocker");
    var occupiedStop = await FriendAction(stopFriendLocal, stopProfile.Id, "stop");
    Require(!occupiedStop.Ok && occupiedStop.Code == "PlayersOnline",
        "Host accepted remote Stop while the server reported an online player");
    File.WriteAllText(playerCountPath, "0");
    for (var i = 0; i < 80; i++)
    {
        var emptyView = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
        if (emptyView.Profiles.Single().OnlinePlayers == 0 && emptyView.Profiles.Single().CanStopNow) break;
        await Task.Delay(100);
    }
    var resetFriendExtension = await FriendAction(stopFriendLocal, stopProfile.Id, "extend");
    Require(resetFriendExtension.Ok,
        "a positive-player cancellation did not reset the Friend extension allowance for the next zero-player countdown");
    var remoteStop = await FriendAction(stopFriendLocal, stopProfile.Id, "stop");
    Require(remoteStop.Ok && remoteStop.Code == "ValheimStopped", $"remote Stop failed: {remoteStop.Code} {remoteStop.Message}");
    Require(File.ReadAllText(Path.Combine(stopProfile.WorldDirectory, "synthetic-stop.marker")) == "Ctrl+C received",
        "remote Stop did not use the synthetic console's graceful exit");
    Console.WriteLine("PASS fixed Friend timer extensions reset on occupancy while Stop still requires fresh zero through HTTPS and Ctrl+C"); passes++;

    var stopMarker = Path.Combine(stopProfile.WorldDirectory, "synthetic-stop.marker");
    File.Delete(stopMarker);
    Require((await OwnerPut<DeviceServerAccessRequest, PairingDecision>(stopOwner,
        $"/api/local/devices/{stopDeviceId}/servers", new([replacementProfile.Id],
            [new DeviceServerPermissionRequest(replacementProfile.Id, true, false)]))).Ok,
        "replacement server access and its Start-only permission were not saved");
    var replacementView = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    Require(replacementView.Profiles.Single().Id == replacementProfile.Id &&
        replacementView.Profiles.Single().CanStart && !replacementView.Profiles.Single().CanStop,
        "the Friend did not receive the replacement server's specific permissions");
    Require((await OwnerPost<object, ActionResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/start", new { })).Ok,
        "conflicting server did not restart");
    ready = false;
    for (var i = 0; i < 60; i++)
    {
        var state = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs
            .Single(run => run.ProfileId == stopProfile.Id).State;
        if (state == "Ready") { ready = true; break; }
        await Task.Delay(100);
    }
    Require(ready, "conflicting server never reached Ready for replacement checks");
    Require((await OwnerPost<CountdownExtensionRequest, ActionResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/countdown/extend", new(37))).Ok,
        "Host keep-alive extension was not applied before the replacement check");
    var protectedConflict = await FriendAction(stopFriendLocal, replacementProfile.Id, "start");
    var protectedServer = protectedConflict.PortConflicts?.SingleOrDefault();
    Require(!protectedConflict.Ok && protectedConflict.Code == "PortConflict" &&
        protectedServer is { ProfileId: var protectedId, CanReplace: false } && protectedId == stopProfile.Id &&
        protectedServer.BlockReason?.Contains("time added by the Host", StringComparison.OrdinalIgnoreCase) == true,
        "a Host keep-alive extension did not block empty-server conflict replacement");

    File.WriteAllText(playerCountPath, "not-a-count");
    var observedUnknown = false;
    for (var i = 0; i < 80; i++)
    {
        var observed = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs
            .Single(run => run.ProfileId == stopProfile.Id);
        if (observed.OnlinePlayers is null || !observed.PlayerCountTrusted) { observedUnknown = true; break; }
        await Task.Delay(100);
    }
    Require(observedUnknown, "observation supervisor did not publish the Unknown player count");
    var unknownConflict = await FriendAction(stopFriendLocal, replacementProfile.Id, "start");
    Require(!unknownConflict.Ok && unknownConflict.Code == "PortConflict" &&
        unknownConflict.PortConflicts?.SingleOrDefault() is
            { ProfileId: var unknownConflictId, CanReplace: false } &&
        unknownConflictId == stopProfile.Id &&
        unknownConflict.PortConflicts.Single().BlockReason?.Contains("authoritative player count", StringComparison.OrdinalIgnoreCase) == true,
        "a conflicting server with an Unknown player count was incorrectly offered for replacement");
    File.WriteAllText(playerCountPath, "1");
    var observedOne = false;
    for (var i = 0; i < 80; i++)
    {
        var observed = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs
            .Single(run => run.ProfileId == stopProfile.Id);
        if (observed.OnlinePlayers == 1) { observedOne = true; break; }
        await Task.Delay(100);
    }
    Require(observedOne, "observation supervisor did not publish the occupied player count");
    var occupiedConflict = await FriendAction(stopFriendLocal, replacementProfile.Id, "start");
    Require(!occupiedConflict.Ok && occupiedConflict.Code == "PortConflict" &&
        occupiedConflict.PortConflicts?.SingleOrDefault() is
            { ProfileId: var occupiedConflictId, CanReplace: false } &&
        occupiedConflictId == stopProfile.Id &&
        occupiedConflict.PortConflicts.Single().BlockReason?.Contains("player", StringComparison.OrdinalIgnoreCase) == true,
        "an occupied conflicting server was incorrectly offered for replacement");
    File.WriteAllText(playerCountPath, "0");
    var observedZero = false;
    for (var i = 0; i < 80; i++)
    {
        var observed = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs
            .Single(run => run.ProfileId == stopProfile.Id);
        if (observed.OnlinePlayers == 0 && observed.PlayerCountTrusted) { observedZero = true; break; }
        await Task.Delay(100);
    }
    Require(observedZero, "observation supervisor did not publish the empty player count");
    var replaceableConflict = await FriendAction(stopFriendLocal, replacementProfile.Id, "start");
    Require(!replaceableConflict.Ok && replaceableConflict.Code == "PortConflict" &&
        replaceableConflict.PortConflicts?.SingleOrDefault() is { ProfileId: var conflictId, CanReplace: true } &&
        conflictId == stopProfile.Id,
        "an empty unextended conflicting server was not offered as an explicit replacement");
    var replaced = await FriendAction(stopFriendLocal, replacementProfile.Id, "replace");
    Require(replaced.Ok && replaced.Code == "PortConflictReplaced" && File.Exists(stopMarker) &&
        (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs
            .Single(run => run.ProfileId == replacementProfile.Id).State is "Starting" or "Ready",
        $"Start-only Friend could not explicitly replace the unassigned empty conflict: {replaced.Code} {replaced.Message}");
    ready = false;
    for (var i = 0; i < 60; i++)
    {
        var state = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs
            .Single(run => run.ProfileId == replacementProfile.Id).State;
        if (state == "Ready") { ready = true; break; }
        await Task.Delay(100);
    }
    Require(ready, "replacement server never reached Ready after the conflicting empty server stopped");
    var replacementCleanup = await OwnerPost<object, ActionResult>(stopOwner,
        $"/api/local/profiles/{replacementProfile.Id}/stop", new { });
    Require(replacementCleanup.Ok,
        $"replacement fixture cleanup failed: {replacementCleanup.Code} {replacementCleanup.Message}");
    Console.WriteLine("PASS Start-only Friend can replace an unassigned empty port conflict, but Host-added time blocks it"); passes++;

    StopApp(host);
    host = null;
    var offlineForgotten = await OwnerPost<object, FriendActionResult>(aLocal,
        $"/api/local/friend/connections/{firstConnection.ConnectionId}/forget", new { });
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(offlineForgotten.Ok && offlineForgotten.Code == "ConnectionForgottenLocally" &&
        offlineForgotten.Message.Contains("Host owner to revoke", StringComparison.Ordinal) &&
        aView.Connections?.Count == 0,
        "offline Forget did not remove the protected local credential with an explicit stale-Host warning");
    Console.WriteLine("PASS offline Forget removes the local credential and warns about Host revocation"); passes++;

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
            foreach (var profileId in new[] { stopProfileId, replacementProfileId })
                if (snapshot?.Runs.SingleOrDefault(run => run.ProfileId == profileId)?.State is "Ready" or "Starting" or "Process running")
                    await OwnerPost<object, ActionResult>(stopOwner, $"/api/local/profiles/{profileId}/stop", new { });
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

async Task<PairingInvite> ServerInvite(HttpClient owner, Guid profileId, bool start, bool refresh = false,
    bool enableConnections = false)
{
    var result = await OwnerPost<ServerInviteRequest, JsonElement>(owner, $"/api/local/servers/{profileId}/invite",
        new(refresh, start, enableConnections, DurationMinutes: 30, DeviceLimit: 4));
    Require(result.GetProperty("ok").GetBoolean(), "server code creation failed: " + result.GetProperty("message").GetString());
    var password = result.GetProperty("password").GetString();
    Require(PairingPassword.TryDecode(password, null, out var invite) && invite!.ServerScope && invite.DeviceId == profileId,
        "Host did not return a valid server-scoped copy/paste code");
    if (enableConnections)
        Require(result.GetProperty("listenerActive").GetBoolean(), "server code did not start the companion listener immediately");
    return invite!;
}

async Task<CompanionStatus> PublicStatus(HttpClient client, PairingCredential credential)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
    request.Headers.Add("X-Device-Id", credential.DeviceId.ToString());
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
    using var response = await client.SendAsync(request);
    return await response.Content.ReadFromJsonAsync<CompanionStatus>(webJson) ?? throw new Exception("Empty public status response.");
}

static HttpClient PinnedClient(string endpoint, string fingerprint)
{
    var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
        cert is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), Convert.FromHexString(fingerprint)) };
    return new HttpClient(handler) { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(8) };
}

async Task<FriendActionResult> FriendAction(HttpClient client, Guid profileId, string action)
{
    var submitted = await OwnerPost<object, FriendActionResult>(client,
        $"/api/local/friend/{profileId}/{action}", new { });
    if (submitted.Code != "OperationAccepted" || submitted.OperationId is not { } operationId)
        return submitted;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
    while (DateTimeOffset.UtcNow < deadline)
    {
        var status = await OwnerPost<object, FriendView>(client, "/api/local/friend/poll", new { });
        var operation = status.Profiles.Select(profile => profile.Operation)
            .FirstOrDefault(candidate => candidate?.Id == operationId);
        if (operation is not null && RemoteOperationStates.Terminal(operation.State))
            return new(operation.Ok == true, operation.Code, operation.Message, null,
                operation.PortConflicts, operation.Id, operation.State);
        await Task.Delay(100);
    }
    throw new Exception($"Friend {action} operation {operationId} did not reach a terminal state.");
}

async Task<FriendActionResult> PublicAction(HttpClient client, PairingCredential credential, Guid profileId, Guid key, string action)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/companion/" + action)
        { Content = JsonContent.Create(new RemoteActionRequest(credential.DeviceId, profileId), options: webJson) };
    request.Headers.Add("X-Device-Id", credential.DeviceId.ToString());
    request.Headers.Add("Idempotency-Key", key.ToString());
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
    using var response = await client.SendAsync(request);
    var submitted = await response.Content.ReadFromJsonAsync<FriendActionResult>(webJson) ??
        throw new Exception("Empty public action response.");
    if (submitted.OperationId is not { } operationId ||
        RemoteOperationStates.Terminal(submitted.OperationState ?? "")) return submitted;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    while (DateTimeOffset.UtcNow < deadline)
    {
        await Task.Delay(100);
        using var poll = new HttpRequestMessage(HttpMethod.Get, "/api/companion/operations/" + operationId);
        poll.Headers.Add("X-Device-Id", credential.DeviceId.ToString());
        poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
        using var pollResponse = await client.SendAsync(poll);
        Require(pollResponse.IsSuccessStatusCode, "accepted operation could not be queried");
        var operation = await pollResponse.Content.ReadFromJsonAsync<RemoteOperationView>(webJson) ??
            throw new Exception("Empty operation response.");
        if (!RemoteOperationStates.Terminal(operation.State)) continue;
        return new(operation.Ok == true, operation.Code, operation.Message, null,
            operation.PortConflicts, operation.Id, operation.State);
    }
    throw new Exception("Remote operation did not reach a terminal state.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static HttpResponseMessage ProbeResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
    new(status) { Content = new StringContent(body) };

static async Task<(ExternalPortProbeResult Result, List<string> Paths)> ProbeFake(
    string endpoint, int port, Func<string, HttpResponseMessage> respond)
{
    var paths = new List<string>();
    using var client = new HttpClient(new ProbeHandler(request =>
    {
        Require(request.RequestUri?.Scheme == Uri.UriSchemeHttps &&
            request.RequestUri.Host == "checker.example", "external probe sent a request to an unexpected service");
        var path = request.RequestUri!.AbsolutePath;
        paths.Add(path);
        return respond(path);
    }));
    var result = await new ExternalPortProbe(client, new Uri("https://checker.example/"))
        .CheckAsync(endpoint, port);
    return (result, paths);
}

sealed class ProbeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
