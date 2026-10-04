using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var appPath = Path.GetFullPath(args.Length > 0 ? args[0] : "local-data/release/TogetherServer.exe");
var fixturePath = Path.GetFullPath("src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe");
var valheimFixturePath = Path.GetFullPath("src/TogetherServer.ValheimFixture/bin/Release/net10.0/valheim_server.exe");
if (!File.Exists(appPath) || !File.Exists(fixturePath) || !File.Exists(valheimFixturePath))
    throw new Exception("Run scripts/build.ps1 first.");
if (args.Skip(1).Contains("--core-remote-journey", StringComparer.OrdinalIgnoreCase))
{
    await CoreRemoteJourney.RunAsync(appPath, valheimFixturePath);
    return 0;
}
if (args.Skip(1).Contains("--shared-world-journey", StringComparer.OrdinalIgnoreCase))
{
    await SharedWorldJourney.RunAsync(appPath, valheimFixturePath);
    return 0;
}
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
var profile = new ServerProfile
{
    Name = "Fixture world",
    WorldId = "companion-fixture",
    WorldDirectory = world,
    GamePort = gamePort,
    ExecutablePath = fixturePath
};
var joinWorld = Path.Combine(root, "join-world");
Directory.CreateDirectory(joinWorld);
var joinProfile = new ServerProfile
{
    Kind = "Valheim",
    Name = "Friend join example",
    ServerName = "Friend join example",
    WorldId = "join-example",
    WorldDirectory = joinWorld,
    GamePort = gamePort + 4,
    ExecutablePath = fixturePath
};
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

    using (var declaredOversize = new ByteArrayContent([1]))
    using (var streamedOversize = new StreamContent(new MemoryStream(
        new byte[FriendLink.MaximumServerLogResponseBytes + 1])))
    using (var boundedPayload = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"ok\":true}")))
    {
        declaredOversize.Headers.ContentLength = FriendLink.MaximumServerLogResponseBytes + 1;
        streamedOversize.Headers.ContentLength = null;
        var declaredRejected = await FriendLink.ReadBoundedLogPayloadAsync(declaredOversize,
            CancellationToken.None);
        var streamedRejected = await FriendLink.ReadBoundedLogPayloadAsync(streamedOversize,
            CancellationToken.None);
        var accepted = await FriendLink.ReadBoundedLogPayloadAsync(boundedPayload,
            CancellationToken.None);
        using var cancellationContent = new StreamContent(new MemoryStream(new byte[32]));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var cancellationObserved = false;
        try
        {
            _ = await FriendLink.ReadBoundedLogPayloadAsync(cancellationContent, canceled.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        Require(declaredRejected is null && streamedRejected is null &&
            accepted is not null && Encoding.UTF8.GetString(accepted) == "{\"ok\":true}" &&
            cancellationObserved,
            "Friend server-log response buffering did not enforce declared/streamed caps or cancellation");
    }
    Console.WriteLine("PASS Friend server-log response buffering is byte-bounded before JSON decoding"); passes++;

    var migrationRoot = Path.Combine(root, "assignment-migration");
    var existingScopedProfile = Guid.NewGuid();
    var existingScopedDevice = Guid.NewGuid();
    var existingLegacyDevice = Guid.NewGuid();
    using (var migrationData = new LocalData(migrationRoot))
    {
        File.WriteAllText(Path.Combine(migrationRoot, "devices.json"), JsonSerializer.Serialize(new[]
        {
            new PairedDevice { Id = existingScopedDevice, ProfileId = existingScopedProfile },
            new PairedDevice { Id = existingLegacyDevice, ProfileId = Guid.Empty }
        }, webJson));
        var legacyInvite = new ServerInviteState
        {
            ProfileId = existingScopedProfile,
            Generation = Guid.NewGuid(),
            Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Endpoint = "https://127.0.0.1:5131",
            Fingerprint = new string('A', 64),
            PairingOpenedUtc = DateTimeOffset.UtcNow,
            PairingExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            DurationMinutes = 30,
            DeviceLimit = 1
        };
        migrationData.SaveProtected("server-invites.protected",
            JsonSerializer.SerializeToUtf8Bytes(new[] { legacyInvite }, webJson));
        var migratedService = new PairingService(migrationData);
        var migrated = migrationData.LoadDevices();
        Require(migrated.Single(device => device.Id == existingScopedDevice).AssignedProfileIds!
                .SequenceEqual([existingScopedProfile]) &&
            migrated.Single(device => device.Id == existingLegacyDevice).AssignedProfileIds!.Count == 0 &&
            migrated.All(device => !device.CanViewLogs && !device.CanViewLogsForProfile(existingScopedProfile)) &&
            migratedService.CurrentServerInvite(existingScopedProfile) is { Open: true } migratedCode &&
            migratedCode.Invitation.ExpiresUtc == PairingService.PersistentServerCodeExpiry &&
            migrationData.HasProtected("pairing-state.protected"),
            "legacy devices/invites did not migrate atomically with fail-closed explicit server access");
    }
    Console.WriteLine("PASS legacy pairing state migrates to one protected fail-closed snapshot"); passes++;

    using (var invalidIndexData = new LocalData(Path.Combine(root, "invalid-friend-index")))
    {
        invalidIndexData.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            Endpoint = "https://127.0.0.1:5131",
            Fingerprint = new string('A', 64),
            DeviceId = Guid.NewGuid(),
            Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
        }, webJson));
        invalidIndexData.SaveProtected("friend-connections.protected", Encoding.UTF8.GetBytes("{not-json"));
        using var service = new FriendService(invalidIndexData);
        var invalidView = service.View();
        Require(invalidView.State == "Not connected" && invalidView.Connections?.Count == 0 &&
            invalidIndexData.Recovery.Notices.Any(notice => notice.StateFile == "friend-connections.protected"),
            "a malformed protected Friend index crashed startup, was not quarantined, or resurrected a legacy credential");
    }
    using (var invalidConfigData = new LocalData(Path.Combine(root, "invalid-friend-config")))
    {
        var invalidConnectionId = Guid.NewGuid();
        invalidConfigData.SaveProtected("friend-connections.protected",
            JsonSerializer.SerializeToUtf8Bytes(new[] { invalidConnectionId }, webJson));
        invalidConfigData.SaveProtected($"friend-{invalidConnectionId:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(new { }, webJson));
        using var service = new FriendService(invalidConfigData);
        var invalidView = service.View();
        Require(invalidView.State == "Not connected" && invalidView.Connections?.Count == 0 &&
            invalidConfigData.Recovery.Notices.Any(notice =>
                notice.StateFile == $"friend-{invalidConnectionId:N}.protected"),
            "a semantically invalid protected Friend config was loaded or silently deleted");
    }
    Console.WriteLine("PASS malformed protected Friend state is quarantined and fails closed"); passes++;

    using (var lifetimeData = new LocalData(Path.Combine(root, "friend-disposal-race")))
    {
        var lifetimeConnectionId = Guid.NewGuid();
        using var pendingResponse = new PendingResponseHandler();
        {
            lifetimeData.SaveProtected("friend-connections.protected",
                JsonSerializer.SerializeToUtf8Bytes(new[] { lifetimeConnectionId }, webJson));
            lifetimeData.SaveProtected($"friend-{lifetimeConnectionId:N}.protected",
                JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
                {
                    Endpoint = "https://127.0.0.1:5131",
                    Fingerprint = new string('A', 64),
                    DeviceId = Guid.NewGuid(),
                    Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
                }, webJson));
            using var lifetimeService = new FriendService(lifetimeData, () =>
                new HttpClient(pendingResponse, disposeHandler: false)
                { BaseAddress = new Uri("https://127.0.0.1:5131/") });
            var lifetimePoll = lifetimeService.PollAsync();
            await pendingResponse.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            lifetimeService.Dispose();
            pendingResponse.Release();
            await lifetimePoll.WaitAsync(TimeSpan.FromSeconds(2));
            Require((await lifetimeService.RequestAsync(Guid.NewGuid(), "start")).Code == "ConnectionClosed",
                "a disposed Friend connection owner accepted more work");
        }
    }
    Console.WriteLine("PASS Friend disposal is safe while a poll owns the link"); passes++;

    var pairingPolicyRoot = Path.Combine(root, "pairing-policy");
    using (var pairingData = new LocalData(pairingPolicyRoot))
    {
        var pairingProfile = Guid.NewGuid();
        var pairingClock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        var pairingService = new PairingService(pairingData, pairingClock);
        var serverCode = pairingService.IssueServer(pairingProfile, true, false,
            "https://127.0.0.1:5131", new string('A', 64), false,
            durationMinutes: 5, deviceLimit: 1, requireApproval: true);
        var policyFirst = pairingService.Activate(new(pairingProfile, serverCode.Code, true));
        var policySecond = pairingService.Activate(new(pairingProfile, serverCode.Code, true));
        Require(policyFirst?.ApprovalPending == true && policySecond?.ApprovalPending == true,
            "persistent server code did not give each PC separate approval-pending access");
        var pending = pairingService.Authenticate(policyFirst!.DeviceId, policyFirst.Credential, out _);
        Require(!pending.Ok && pending.Code == "ApprovalPending" && pairingService.Approve(policyFirst.DeviceId).Ok &&
            pairingService.Authenticate(policyFirst.DeviceId, policyFirst.Credential, out _).Ok,
            "a waiting PC authenticated before local approval or remained blocked afterward");
        Require(pairingService.SetPermissions(policyFirst.DeviceId, true, false, "logs",
            canViewLogs: true).Ok &&
            pairingService.Authenticate(policyFirst.DeviceId, policyFirst.Credential,
                out var loadedLogDevice).Ok && loadedLogDevice is not null &&
            pairingService.AuthorizeViewLogs(loadedLogDevice, pairingProfile, out _).Ok &&
            pairingService.SetPermissions(policyFirst.DeviceId, true, false, "logs",
                canViewLogs: false).Ok &&
            pairingService.AuthorizeViewLogs(loadedLogDevice, pairingProfile, out _).Code == "PermissionDenied",
            "a loaded device object retained View logs after the owner revoked it");
        pairingClock.SetUtcNow(pairingClock.GetUtcNow().AddDays(30));
        var laterDevice = pairingService.Activate(new(pairingProfile, serverCode.Code, true));
        var unchanged = pairingService.IssueServer(pairingProfile, true, false,
            serverCode.Endpoint, serverCode.Fingerprint, false, durationMinutes: 60, deviceLimit: 25,
            requireApproval: false);
        Require(laterDevice?.ApprovalPending == true && unchanged.Code == serverCode.Code &&
            unchanged.ExpiresUtc == PairingService.PersistentServerCodeExpiry &&
            pairingService.CurrentServerInvite(pairingProfile) is { Open: true, RequireApproval: false },
            "server code expired, stopped accepting PCs, or changed while saving its approval setting");
        Require(pairingService.ClosePairing(pairingProfile).Ok &&
            pairingService.Activate(new(pairingProfile, serverCode.Code, true)) is null &&
            pairingService.Authenticate(policyFirst.DeviceId, policyFirst.Credential, out _).Ok,
            "turning off the legacy server-code route revoked an existing PC or left the old code usable");
        var reopened = pairingService.IssueServer(pairingProfile, true, false,
            serverCode.Endpoint, serverCode.Fingerprint, false);
        Require(reopened.Code != serverCode.Code &&
            pairingService.Activate(new(pairingProfile, reopened.Code, true)) is not null,
            "choosing Invite friends did not restore a persistent current server code");
        Require(pairingService.EmergencyRevoke(pairingProfile).Ok &&
            pairingService.Authenticate(policyFirst.DeviceId, policyFirst.Credential, out _).Code == "Revoked" &&
            pairingData.LoadActivity().All(item => !item.Message.Contains(serverCode.Code, StringComparison.Ordinal)),
            "emergency revoke did not invalidate code-issued credentials or activity exposed a code");
    }
    Console.WriteLine("PASS persistent server codes keep per-PC approval and revocation separate"); passes++;

    var accessRoot = Path.Combine(root, "owner-access-expiry");
    var accessProfile = Guid.NewGuid();
    var accessClock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    PairingCredential accessCredential;
    DateTimeOffset extendedAccessDeadline;
    using (var accessData = new LocalData(accessRoot))
    {
        var accessService = new PairingService(accessData, accessClock);
        var accessInvite = accessService.IssueServer(accessProfile, true, false,
            "https://127.0.0.1:5131", new string('C', 64), false,
            durationMinutes: 30, deviceLimit: 2);
        accessCredential = accessService.Activate(new(accessProfile, accessInvite.Code, true))
            ?? throw new Exception("access-expiry test device did not activate");
        Require(accessService.SetPermissions(accessCredential.DeviceId, true, true,
                canExtendTimer: true, canViewLogs: true).Ok,
            "access-expiry test permissions were not saved");
        var authenticated = accessService.Authenticate(accessCredential.DeviceId,
            accessCredential.Credential, out var loadedDevice);
        Require(authenticated.Ok && loadedDevice is not null, "access-expiry test credential did not authenticate");

        var now = accessClock.GetUtcNow();
        Require(accessService.SetAccessExpiry(accessCredential.DeviceId,
                new(Clear: true, Duration: DeviceAccessDurations.OneHour)).Code == "InvalidAccessExpiry" &&
            accessService.SetAccessExpiry(accessCredential.DeviceId,
                new(AccessExpiresUtc: now)).Code == "AccessExpiryInPast" &&
            accessService.SetAccessExpiry(accessCredential.DeviceId,
                new(AccessExpiresUtc: now.AddDays(366))).Code == "AccessExpiryTooDistant" &&
            accessService.SetAccessExpiry(accessCredential.DeviceId,
                new(AccessExpiresUtc: now.ToOffset(TimeSpan.FromHours(2)).AddHours(1))).Code == "InvalidAccessExpiry" &&
            accessService.SetAccessExpiry(accessCredential.DeviceId,
                new(Duration: "Forever")).Code == "InvalidAccessDuration",
            "owner access-expiry validation accepted ambiguous, past, distant, non-UTC, or unknown input");

        var exactDeadline = now.AddMinutes(10);
        var set = accessService.SetAccessExpiry(accessCredential.DeviceId,
            new(AccessExpiresUtc: exactDeadline));
        var beforeView = accessService.Views().Single(item => item.Id == accessCredential.DeviceId);
        Require(set is { Ok: true, Code: "AccessExpirySet" } && set.AccessExpiresUtc == exactDeadline &&
            beforeView.AccessExpiresUtc == exactDeadline && !beforeView.AccessExpired,
            "the owner access deadline was not exposed as active in DeviceView");
        accessClock.SetUtcNow(exactDeadline.AddTicks(-1));
        Require(accessService.Authenticate(accessCredential.DeviceId, accessCredential.Credential, out _).Ok,
            "owner access expired before the exact UTC boundary");
        accessClock.SetUtcNow(exactDeadline);
        var exactDenial = accessService.Authenticate(accessCredential.DeviceId,
            accessCredential.Credential, out _);
        var staleHeartbeat = accessService.RecordHeartbeat(loadedDevice!,
            new(accessCredential.DeviceId, Guid.NewGuid(), 1, "check", CompanionProtocol.Current));
        var staleRenewal = accessService.Renew(loadedDevice!,
            new(accessCredential.DeviceId, Guid.NewGuid()), false);
        var expiredView = accessService.Views().Single(item => item.Id == accessCredential.DeviceId);
        var persistedExpiredDevice = accessData.LoadDevices().Single(item => item.Id == accessCredential.DeviceId);
        Require(exactDenial.Code == "AccessExpired" && staleHeartbeat.Code == "AccessExpired" &&
            staleRenewal is null && !accessService.CanAccess(loadedDevice!, accessProfile) &&
            expiredView.AccessExpired && !expiredView.Revoked && expiredView.AssignedProfileIds.SequenceEqual([accessProfile]) &&
            expiredView.CanStart && expiredView.CanStop && expiredView.CanExtendTimer && expiredView.CanViewLogs &&
            !persistedExpiredDevice.Revoked && persistedExpiredDevice.CredentialHash is not null &&
            persistedExpiredDevice.AssignedProfileIds!.SequenceEqual([accessProfile]),
            "exact expiry failed open, trusted a stale loaded object, or changed credential, assignment, or permissions");

        var extended = accessService.SetAccessExpiry(accessCredential.DeviceId,
            new(Duration: DeviceAccessDurations.OneHour));
        extendedAccessDeadline = accessClock.GetUtcNow().AddHours(1);
        Require(extended is { Ok: true, AccessExpired: false } &&
            extended.AccessExpiresUtc == extendedAccessDeadline &&
            accessService.Authenticate(accessCredential.DeviceId, accessCredential.Credential, out _).Ok,
            "extending expired owner access did not immediately restore the same credential");
        var activity = accessData.LoadActivity();
        var audit = File.ReadAllText(Path.Combine(accessRoot, "audit.log"));
        Require(activity.Any(item => item.Action == "AccessExpirySet") &&
            activity.Any(item => item.Action == "AccessExpired") &&
            activity.All(item => !item.Message.Contains(accessCredential.Credential, StringComparison.Ordinal)) &&
            !audit.Contains(accessCredential.Credential, StringComparison.Ordinal),
            "local access-expiry activity/audit was incomplete or exposed a credential");
    }
    using (var reopenedAccessData = new LocalData(accessRoot))
    {
        var reopenedAccess = new PairingService(reopenedAccessData, accessClock);
        var restartedView = reopenedAccess.Views().Single(item => item.Id == accessCredential.DeviceId);
        Require(restartedView.AccessExpiresUtc == extendedAccessDeadline && !restartedView.AccessExpired &&
            reopenedAccess.Authenticate(accessCredential.DeviceId, accessCredential.Credential, out _).Ok,
            "the owner access extension did not persist across restart");

        var pendingInvite = reopenedAccess.IssueServer(accessProfile, true, false,
            "https://127.0.0.1:5131", new string('C', 64), false,
            durationMinutes: 30, deviceLimit: 2, requireApproval: true);
        var pendingCredential = reopenedAccess.Activate(new(accessProfile, pendingInvite.Code, true))
            ?? throw new Exception("approval-pending access-expiry device did not activate");
        var pendingDeadline = accessClock.GetUtcNow().AddMinutes(5);
        Require(reopenedAccess.SetAccessExpiry(pendingCredential.DeviceId,
            new(AccessExpiresUtc: pendingDeadline)).Ok, "pending-device access deadline was not saved");

        var clearDeadline = accessClock.GetUtcNow().AddMinutes(2);
        Require(reopenedAccess.SetAccessExpiry(accessCredential.DeviceId,
            new(AccessExpiresUtc: clearDeadline)).Ok, "clear test deadline was not saved");
        accessClock.SetUtcNow(clearDeadline);
        Require(reopenedAccess.Authenticate(accessCredential.DeviceId,
                accessCredential.Credential, out _).Code == "AccessExpired" &&
            reopenedAccess.SetAccessExpiry(accessCredential.DeviceId, new(Clear: true)).Code == "AccessExpiryCleared" &&
            reopenedAccess.Authenticate(accessCredential.DeviceId, accessCredential.Credential, out _).Ok &&
            reopenedAccess.Views().Single(item => item.Id == accessCredential.DeviceId) is
            { AccessExpiresUtc: null, AccessExpired: false },
            "clearing an expired owner deadline did not immediately restore the same credential");

        accessClock.SetUtcNow(pendingDeadline);
        Require(reopenedAccess.Authenticate(pendingCredential.DeviceId,
                pendingCredential.Credential, out _).Code == "ApprovalPending" &&
            reopenedAccess.Approve(pendingCredential.DeviceId).Ok &&
            reopenedAccess.Authenticate(pendingCredential.DeviceId,
                pendingCredential.Credential, out _).Code == "AccessExpired",
            "approval-pending and owner-expired states were not independently fail closed");
        Require(reopenedAccess.SetAccessExpiry(pendingCredential.DeviceId,
            new(Duration: DeviceAccessDurations.ThirtyDays)).Ok,
            "credential-expiry independence deadline was not saved");
        accessClock.SetUtcNow(pendingCredential.ExpiresUtc);
        Require(reopenedAccess.Authenticate(pendingCredential.DeviceId,
                pendingCredential.Credential, out _).Code == "Expired",
            "renewable credential expiry was conflated with owner access expiry");

        Require(reopenedAccess.SetAccessExpiry(Guid.NewGuid(), new(Clear: true)).Code == "UnknownDevice" &&
            reopenedAccess.Revoke(accessCredential.DeviceId).Ok &&
            reopenedAccess.SetAccessExpiry(accessCredential.DeviceId,
                new(Duration: DeviceAccessDurations.OneDay)).Code == "Revoked" &&
            reopenedAccess.Authenticate(accessCredential.DeviceId,
                accessCredential.Credential, out _).Code == "Revoked",
            "unknown or revoked devices accepted an owner deadline, or revocation lost precedence");
    }
    Console.WriteLine("PASS owner access expiry is exact, persistent, reversible, and independent of credential state"); passes++;

    var helperRoot = Path.Combine(root, "temporary-helper");
    var helperProfile = Guid.NewGuid();
    var helperClock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    using (var helperData = new LocalData(helperRoot))
    {
        var helperService = new PairingService(helperData, helperClock);
        var helperInvite = helperService.IssueServer(helperProfile, false, false,
            "https://127.0.0.1:5131", new string('D', 64), false);
        var helperCredential = helperService.Activate(new(helperProfile, helperInvite.Code, true))
            ?? throw new Exception("temporary-helper device did not activate");
        Require(helperService.Authenticate(helperCredential.DeviceId, helperCredential.Credential,
            out var helperDevice).Ok && helperDevice is not null,
            "temporary-helper device did not authenticate");
        Require(helperService.SetTemporaryHelper(helperCredential.DeviceId, new(Duration: "Forever")).Code ==
                "InvalidTemporaryHelper" &&
            helperService.SetTemporaryHelper(helperCredential.DeviceId, new(Clear: true,
                Duration: DeviceAccessDurations.OneHour)).Code == "InvalidTemporaryHelper" &&
            helperService.SetTemporaryHelper(Guid.NewGuid(), new(Duration: DeviceAccessDurations.OneHour)).Code ==
                "UnknownDevice", "temporary helper accepted an invalid owner request");
        var grant = helperService.SetTemporaryHelper(helperCredential.DeviceId,
            new(Duration: DeviceAccessDurations.OneHour));
        Require(grant.Ok && grant.TemporaryHelperUntilUtc == helperClock.GetUtcNow().AddHours(1) &&
            helperService.CanStart(helperDevice!, helperProfile) &&
            helperService.CanStop(helperDevice!, helperProfile) &&
            helperService.CanExtendTimer(helperDevice!, helperProfile) &&
            helperService.CanViewLogs(helperDevice!, helperProfile) &&
            !helperService.CanStart(helperDevice!, Guid.NewGuid()),
            "temporary helper did not grant only fixed controls on assigned servers");
        var helperView = helperService.Views().Single(item => item.Id == helperCredential.DeviceId);
        Require(helperView.TemporaryHelperActive && helperView.ServerPermissions!.Single().CanViewLogs,
            "temporary helper was not shown to the owner");
        helperClock.SetUtcNow(grant.TemporaryHelperUntilUtc!.Value);
        Require(!helperService.CanStart(helperDevice!, helperProfile) &&
            !helperService.CanStop(helperDevice!, helperProfile) &&
            !helperService.CanExtendTimer(helperDevice!, helperProfile) &&
            !helperService.CanViewLogs(helperDevice!, helperProfile) &&
            !helperService.Views().Single(item => item.Id == helperCredential.DeviceId).TemporaryHelperActive,
            "temporary helper permissions survived the exact Host deadline");
        var reopenedHelper = new PairingService(helperData, helperClock);
        Require(!reopenedHelper.CanStart(helperDevice!, helperProfile) &&
            reopenedHelper.SetTemporaryHelper(helperCredential.DeviceId,
                new(Duration: DeviceAccessDurations.EightHours)).Ok &&
            reopenedHelper.SetTemporaryHelper(helperCredential.DeviceId, new(Clear: true)).Ok &&
            !reopenedHelper.CanStop(helperDevice!, helperProfile),
            "temporary helper expiry or End now was not durable across Host restart");
    }
    Console.WriteLine("PASS temporary helper grants fixed controls and restores usual permissions at exact expiry"); passes++;

    host = StartApp(appPath, "--host", hostPort, hostData, drainDiagnostics: false);
    DisconnectDiagnosticPipes(host);
    await WaitLocal(hostPort);
    using var owner = LocalClient(hostPort);
    var settings = new HostSettings
    {
        MaxConcurrentServers = 1,
        Profiles = [profile, joinProfile],
        CompanionEndpoint = endpoint,
        PublicGameIp = "1.2.3.4",
        PublicGameIpCheckedUtc = DateTimeOffset.UtcNow.AddHours(-2),
        CompanionPort = companionPort,
        CompanionBindAddress = "127.0.0.1"
    };
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "initial Host settings failed");
    _ = await ServerInvite(owner, profile.Id, true, enableConnections: true);
    var closedInitialWindow = await OwnerPost<object, ActionResult>(owner,
        $"/api/local/servers/{profile.Id}/pairing/close", new { });
    var idleListener = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
    var idlePorts = await owner.GetFromJsonAsync<PortDiagnosticsView>("/api/local/network/ports");
    Require(closedInitialWindow.Ok && !idleListener.GetProperty("listenerActive").GetBoolean() &&
        idleListener.GetProperty("listenerState").GetString() == CompanionListenerStates.Idle &&
        idleListener.GetProperty("listenerWarning").ValueKind == JsonValueKind.Null &&
        idlePorts?.Control.State == "Idle" && idlePorts.Control.RemoteState == "Not needed",
        "an enabled listener with no invite or usable credential was reported as a failure");
    Console.WriteLine("PASS no active invite or paired PC is a normal idle Friend state"); passes++;
    // A disposable packaged app owns the occupied socket, so Windows never
    // attributes a firewall/access prompt to CompanionChecks.exe.
    var portOccupant = StartApp(appPath, "--friend", companionPort,
        Path.Combine(root, "occupied-companion-port"));
    try
    {
        await WaitLocal(companionPort);
        WindowsListenerOwners.RequireTogetherServerOwner(companionPort, portOccupant);
        var blockedInvite = await OwnerPost<ServerInviteRequest, JsonElement>(owner,
            $"/api/local/servers/{profile.Id}/invite",
            new(false, true, true, DurationMinutes: 30, DeviceLimit: 4));
        Require(blockedInvite.GetProperty("ok").GetBoolean() &&
                !blockedInvite.GetProperty("listenerActive").GetBoolean() &&
                blockedInvite.GetProperty("listenerWarning").GetString() is { Length: > 0 },
            "a listener bind failure with closed GUI diagnostic pipes returned HTTP 500 or lost its typed warning: " +
            blockedInvite);
    }
    finally { StopApp(portOccupant); }
    var inviteA = await ServerInvite(owner, profile.Id, true, enableConnections: true);
    WindowsListenerOwners.RequireTogetherServerOwner(hostPort, host!);
    WindowsListenerOwners.RequireTogetherServerOwner(companionPort, host!);
    var inviteB = await ServerInvite(owner, joinProfile.Id, false);
    var passwordA = PairingPassword.Encode(inviteA);
    Require(passwordA.StartsWith("TS3-", StringComparison.Ordinal) &&
        PairingPassword.TryDecode(passwordA, null, out var decoded) &&
        decoded!.ServerScope && decoded.DeviceId == profile.Id && decoded.Code == inviteA.Code &&
        decoded.Endpoint == endpoint &&
        decoded.Fingerprint == inviteA.Fingerprint &&
        !PairingPassword.TryDecode("wrong-password", endpoint, out _) &&
        PairingPassword.TryDecode(PairingPassword.Encode(inviteA with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }), endpoint, out var oldServerCode) &&
        oldServerCode!.ServerScope &&
        !PairingPassword.TryDecode(PairingPassword.Encode(inviteA with { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ServerScope = false }), endpoint, out _),
        "generated server code did not preserve its server scope, secret, full TLS pin, or persistent TS3 compatibility");
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
    Console.WriteLine("PASS closed GUI diagnostic pipes preserve typed listener errors and later HTTPS recovery"); passes++;
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

    using (var missingDiagnosticsHeader = await owner.GetAsync("/api/local/diagnostics"))
        Require(missingDiagnosticsHeader.StatusCode == HttpStatusCode.Forbidden,
            "owner diagnostics accepted a GET without the sensitive local header");
    using (var missingReportHeader = await owner.GetAsync("/api/local/support-report"))
        Require(missingReportHeader.StatusCode == HttpStatusCode.Forbidden,
            "support export accepted a GET without the sensitive local header");
    using (var missingSessionsHeader = await owner.GetAsync(
        $"/api/local/profiles/{profile.Id}/sessions?limit=8"))
        Require(missingSessionsHeader.StatusCode == HttpStatusCode.Forbidden,
            "recent sessions accepted a GET without the sensitive local header");
    using (var arbitraryDiagnosticsInput = await OwnerGet(owner,
        "/api/local/diagnostics?host=example.test&port=1&path=C:%5Cprivate"))
        Require(arbitraryDiagnosticsInput.StatusCode == HttpStatusCode.BadRequest,
            "owner diagnostics accepted arbitrary host, port, or path query input");
    using (var arbitraryReportInput = await OwnerGet(owner,
        "/api/local/support-report?output=C:%5Cprivate%5Creport.json"))
        Require(arbitraryReportInput.StatusCode == HttpStatusCode.BadRequest,
            "support export accepted a caller-supplied output path");
    using (var arbitrarySessionsInput = await OwnerGet(owner,
        $"/api/local/profiles/{profile.Id}/sessions?path=C:%5Cprivate&source=logs"))
        Require(arbitrarySessionsInput.StatusCode == HttpStatusCode.BadRequest,
            "recent sessions accepted a caller-controlled path or source");
    using (var unboundedSessionsInput = await OwnerGet(owner,
        $"/api/local/profiles/{profile.Id}/sessions?limit={HostManager.MaximumRecentSessionLimit + 1}"))
        Require(unboundedSessionsInput.StatusCode == HttpStatusCode.BadRequest,
            "recent sessions accepted an unbounded limit");
    var emptySessions = await OwnerGetJson<RecentServerSessionsResult>(owner,
        $"/api/local/profiles/{profile.Id}/sessions?limit=8");
    Require(emptySessions.Ok && emptySessions.ProfileId == profile.Id && emptySessions.Sessions.Count == 0,
        "the owner-only recent-session endpoint did not return its bounded empty state");
    using (var reportBody = new HttpRequestMessage(HttpMethod.Get, "/api/local/support-report")
    { Content = JsonContent.Create(new { output = "C:\\private\\report.json" }) })
    {
        reportBody.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await owner.SendAsync(reportBody);
        Require(response.StatusCode == HttpStatusCode.BadRequest,
            "support export accepted an arbitrary GET request body");
    }
    using (var wrongReportMethod = new HttpRequestMessage(HttpMethod.Post, "/api/local/support-report"))
    {
        wrongReportMethod.Headers.Add("Origin", owner.BaseAddress!.ToString().TrimEnd('/'));
        wrongReportMethod.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await owner.SendAsync(wrongReportMethod);
        Require(response.StatusCode == HttpStatusCode.MethodNotAllowed,
            "support export exposed a mutation or arbitrary request-body route");
    }
    var recordedRoute = await OwnerPost<object, ExternalPortProbeResult>(owner,
        "/api/local/network/test-friend-route", new { });
    Require(recordedRoute.State == "Unavailable", "loopback route diagnostic did not fail honestly");
    var ownerDiagnostics = await OwnerGetJson<OwnerDiagnosticsView>(owner, "/api/local/diagnostics");
    var fixtureDiagnostics = ownerDiagnostics.Servers.Single(item => item.ProfileId == profile.Id);
    var declaredPorts = fixtureDiagnostics.Checks.Single(item => item.Id == "local-game-ports");
    var routeDiagnostic = ownerDiagnostics.SharedChecks.Single(item => item.Id == "route-diagnostic");
    Require(declaredPorts.Detail.Contains("Seeing a port open on this PC", StringComparison.Ordinal) &&
        declaredPorts.Detail.Contains("does not prove that a Friend can reach it", StringComparison.Ordinal) &&
        declaredPorts.Detail.Contains("join the game", StringComparison.Ordinal) &&
        routeDiagnostic.State == "Unavailable" &&
        routeDiagnostic.Detail.Contains("does not prove that a Friend connected", StringComparison.Ordinal) &&
        routeDiagnostic.Detail.Contains("joined the game", StringComparison.Ordinal),
        "owner diagnostics overstated local listener or outside TCP evidence");
    var supportExport = await OwnerGetJson<SupportReportExport>(owner, "/api/local/support-report");
    Require(supportExport.FileName == SupportReportExporter.FileName &&
        supportExport.SizeBytes == Encoding.UTF8.GetByteCount(supportExport.Content) &&
        supportExport.SizeBytes <= SupportReportExporter.MaximumReportBytes,
        "support endpoint returned an unsafe filename or an unbounded UTF-8 payload");
    Require(!SupportReportRedactor.Redact("Process running. Recorded PID 50628.")
            .Contains("50628", StringComparison.Ordinal) &&
        !supportExport.Content.Contains("Recorded PID", StringComparison.OrdinalIgnoreCase),
        "support redaction retained a managed process identifier");
    Require(!supportExport.Content.Contains(endpoint, StringComparison.OrdinalIgnoreCase) &&
        !supportExport.Content.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
        !supportExport.Content.Contains(world, StringComparison.OrdinalIgnoreCase) &&
        !supportExport.Content.Contains(fixturePath, StringComparison.OrdinalIgnoreCase) &&
        !supportExport.Content.Contains(inviteA.Code, StringComparison.Ordinal),
        "support endpoint leaked an endpoint, address, path, or pairing secret");
    using (var reportDocument = JsonDocument.Parse(supportExport.Content))
    {
        Require(reportDocument.RootElement.GetProperty("reportSchemaVersion").GetInt32() ==
                SupportReportExporter.ReportSchemaVersion &&
            !reportDocument.RootElement.GetProperty("versions").GetProperty("staging").GetBoolean() &&
            reportDocument.RootElement.GetProperty("recentActivity").GetArrayLength() <= 24 &&
            reportDocument.RootElement.GetProperty("recentOperations").GetArrayLength() <= 24,
            "support endpoint omitted schema/instance state or exceeded summary bounds");
    }
    Console.WriteLine("PASS owner diagnostics and support export are fixed-input local-only, honest, bounded, and redacted"); passes++;

    friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
    friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
    await WaitLocal(friendAPort); await WaitLocal(friendBPort);
    using var aLocal = LocalClient(friendAPort);
    using var bLocal = LocalClient(friendBPort);
    var unpairedFriendSnapshot = await aLocal.GetFromJsonAsync<JsonElement>("/api/local/snapshot");
    Require(unpairedFriendSnapshot.TryGetProperty("hostCapabilities", out var unpairedCapabilities) &&
        unpairedCapabilities.ValueKind == JsonValueKind.Array && unpairedCapabilities.GetArrayLength() == 0,
        "an unpaired Friend snapshot did not emit an empty Host capability list");
    Console.WriteLine("PASS unpaired Friend snapshots emit a canonical empty Host capability list"); passes++;
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
        "a rejected server code was mistaken for a network or TLS failure: " + JsonSerializer.Serialize(rejectedCode, webJson));
    var pairedA = await OwnerPost<FriendPairRequest, FriendActionResult>(aLocal, "/api/local/friend/pair",
        new(passwordA));
    Require(pairedA.Ok, "a current invite did not pair without a separate Host IP");
    var newlyPairedSnapshot = await aLocal.GetFromJsonAsync<JsonElement>("/api/local/snapshot");
    var newlyPairedConnections = newlyPairedSnapshot.GetProperty("connections");
    Require(newlyPairedSnapshot.TryGetProperty("hostCapabilities", out var newlyPairedCapabilities) &&
        newlyPairedCapabilities.ValueKind == JsonValueKind.Array && newlyPairedCapabilities.GetArrayLength() == 0 &&
        newlyPairedConnections.ValueKind == JsonValueKind.Array && newlyPairedConnections.GetArrayLength() == 1 &&
        newlyPairedConnections.EnumerateArray().All(connection =>
            connection.TryGetProperty("hostCapabilities", out var nestedCapabilities) &&
            nestedCapabilities.ValueKind == JsonValueKind.Array && nestedCapabilities.GetArrayLength() == 0),
        "a newly paired Friend snapshot did not emit empty Host capability lists at every projection level");
    Console.WriteLine("PASS newly paired Friend snapshots emit canonical capability lists for selected and nested connections"); passes++;
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
    var pausedRefresh = await FriendAction(aLocal, profile.Id, "refresh");
    var unassignedRefresh = await FriendAction(bLocal, joinProfile.Id, "refresh");
    Require(pausedRefresh.Code == "NotManaged" && pausedRefresh.Status?.Profiles.Single().Id == profile.Id &&
        unassignedRefresh.Code == "PermissionDenied",
        "read-only refresh was blocked by paused lifecycle controls or bypassed server assignment");
    Console.WriteLine("PASS assigned Friends can refresh status while lifecycle controls are paused"); passes++;
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

    // Real loopback HTTPS transfer using the same Host/Friend processes and TLS pin
    // as the companion journey. All data and the fixture game stay under local-data.
    settings.Profiles.Single(item => item.Id == profile.Id).Backups =
        new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "shared-world rolling backup setup failed");
    var enabledSharing = await OwnerPut<SharedWorldConsentRequest, SharedWorldResult>(owner,
        $"/api/local/profiles/{profile.Id}/shared-world", new(true));
    Require(enabledSharing.Ok, "owner could not opt in to shared saves");
    var deniedGrant = await OwnerPut<SharedWorldGrantRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceAId}/shared-world/{joinProfile.Id}", new(true));
    Require(!deniedGrant.Ok, "an unassigned profile received a save grant");
    var grantA = await OwnerPut<SharedWorldGrantRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceAId}/shared-world/{profile.Id}", new(true));
    Require(grantA.Ok, "owner save grant failed");
    var consentA = await OwnerPut<SharedWorldConsentRequest, ReceivedSharedWorldResult>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world/consent", new(true));
    Require(consentA.Ok, "Friend PC consent failed");
    var beforeStop = await OwnerPost<object, ReceivedSharedWorldResult>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
    Require(!beforeStop.Ok, "a Friend received a save before a graceful Stop");
    File.WriteAllText(Path.Combine(world, "world.dat"), "verified HTTPS fixture world");
    var collisionVersion = System.Text.Encoding.UTF8.GetBytes("legitimate world version.json payload");
    var collisionMembership = System.Text.Encoding.UTF8.GetBytes("legitimate world membership.json payload");
    File.WriteAllBytes(Path.Combine(world, "version.json"), collisionVersion);
    File.WriteAllBytes(Path.Combine(world, "membership.json"), collisionMembership);
    Require((await OwnerPost<object, ActionResult>(owner,
        $"/api/local/profiles/{profile.Id}/start", new { })).Ok,
        "shared-world fixture start failed");
    Require((await OwnerPost<object, ActionResult>(owner,
        $"/api/local/profiles/{profile.Id}/stop", new { })).Ok,
        "shared-world fixture graceful Stop failed");
    var publishedShared = await OwnerGetJson<SharedWorldStatus>(owner,
        $"/api/local/profiles/{profile.Id}/shared-world");
    Require(publishedShared.Enabled && publishedShared.Latest is { Number: 1 },
        "Host did not publish a signed post-Stop version");
    // The app-owned poll discovers the publication after reconnect; no user pull is sent.
    StopApp(friendA);
    friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
    await WaitLocal(friendAPort);
    await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    ReceivedSharedWorldStatus receivedStatus = await OwnerGetJson<ReceivedSharedWorldStatus>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world");
    for (var attempt = 0; attempt < 20 && receivedStatus.ThisPcVersion != 1; attempt++)
    {
        await Task.Delay(1000);
        receivedStatus = await OwnerGetJson<ReceivedSharedWorldStatus>(aLocal,
            $"/api/local/friend/{profile.Id}/shared-world");
    }
    Require(receivedStatus.ThisPcVersion == 1 && receivedStatus.HostVersion == 1,
        $"pinned HTTPS automatic receipt did not catch up: {receivedStatus.State} {receivedStatus.Error}");
    for (var attempt = 0; attempt < 20 && (await OwnerGetJson<SharedWorldStatus>(owner,
             $"/api/local/profiles/{profile.Id}/shared-world")).ConfirmedCopies != 1; attempt++)
        await Task.Delay(250);
    Require((await OwnerGetJson<SharedWorldStatus>(owner,
        $"/api/local/profiles/{profile.Id}/shared-world")).ConfirmedCopies == 1,
        "Host did not count the signed copy confirmation over pinned HTTPS");
    var receiverRoot = Path.Combine(friendAData, "received-shared-worlds",
        deviceAId.ToString("N"), profile.Id.ToString("N"));
    Require(File.ReadAllText(Path.Combine(receiverRoot,
        publishedShared.Latest!.VersionHash, "payload", "world.dat")) == "verified HTTPS fixture world",
        "Friend vault did not contain the verified file");
    foreach (var (name, expected) in new[] { ("version.json", collisionVersion),
        ("membership.json", collisionMembership) })
    {
        var hostPayload = Path.Combine(hostData, "shared-worlds", profile.Id.ToString("N"),
            publishedShared.Latest.GroupId.ToString("N"), "1", "payload", name);
        var friendPayload = Path.Combine(receiverRoot, publishedShared.Latest.VersionHash, "payload", name);
        Require(File.ReadAllBytes(hostPayload).SequenceEqual(expected) &&
            File.ReadAllBytes(friendPayload).SequenceEqual(expected),
            $"payload named {name} collided with shared save metadata");
    }
    var rereadShared = await OwnerPost<object, ReceivedSharedWorldResult>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
    Require(rereadShared.Ok && rereadShared.Code == "AlreadyReceived",
        "Friend could not re-verify the collision-named payload files");
    StopApp(friendA);
    using (var larger = new FileStream(Path.Combine(world, "world.dat"), FileMode.Create, FileAccess.Write))
        larger.SetLength(3L * SharedWorldService.ChunkBytes);
    Require((await OwnerPost<object, ActionResult>(owner,
        $"/api/local/profiles/{profile.Id}/start", new { })).Ok &&
        (await OwnerPost<object, ActionResult>(owner,
        $"/api/local/profiles/{profile.Id}/stop", new { })).Ok,
        "second disposable completed save was not published");
    var secondShared = await OwnerGetJson<SharedWorldStatus>(owner,
        $"/api/local/profiles/{profile.Id}/shared-world");
    Require(secondShared.Latest is { Number: 2 }, "second signed version was absent");
    friendA = StartApp(appPath, "--friend", friendAPort, friendAData, receiveDelayMs: 3000);
    await WaitLocal(friendAPort);
    var slowView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(slowView.State is "Connected" or "Disabled",
        "Friend did not reconnect before the interrupted transfer fixture: " + slowView.State + " " + slowView.ConnectionCode);
    var partial = Path.Combine(receiverRoot, ".partial-" + secondShared.Latest!.VersionHash,
        "payload", "world.dat");
    for (var attempt = 0; attempt < 150 &&
        (!File.Exists(partial) || new FileInfo(partial).Length < SharedWorldService.ChunkBytes); attempt++)
        await Task.Delay(100);
    Require(File.Exists(partial) && new FileInfo(partial).Length >= SharedWorldService.ChunkBytes,
        "slow transfer did not retain its first completed chunk");
    var responsive = Stopwatch.StartNew();
    Require((await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { })).State is "Connected" or "Disabled",
        "heartbeat failed during a slow shared-save transfer");
    await FriendAction(aLocal, profile.Id, "stop");
    Require(responsive.Elapsed < TimeSpan.FromSeconds(2),
        "heartbeat or remote Stop waited behind the slow shared-save transfer");
    Require((await OwnerPut<SharedWorldGrantRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceAId}/shared-world/{profile.Id}", new(false))).Ok,
        "fixture grant withdrawal failed during transfer");
    await Task.Delay(3500);
    Require((await OwnerGetJson<ReceivedSharedWorldStatus>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world")).ThisPcVersion == 1 &&
        File.Exists(partial) && new FileInfo(partial).Length >= SharedWorldService.ChunkBytes,
        "revocation advanced the receipt or removed resumable chunks");
    Require((await OwnerPut<SharedWorldGrantRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceAId}/shared-world/{profile.Id}", new(true))).Ok,
        "fixture grant could not be restored");
    ReceivedSharedWorldStatus resumed = await OwnerGetJson<ReceivedSharedWorldStatus>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world");
    for (var attempt = 0; attempt < 100 && resumed.ThisPcVersion != 2; attempt++)
    {
        await Task.Delay(500);
        resumed = await OwnerGetJson<ReceivedSharedWorldStatus>(aLocal,
            $"/api/local/friend/{profile.Id}/shared-world");
    }
    Require(resumed.ThisPcVersion == 2 &&
        File.Exists(Path.Combine(receiverRoot, secondShared.Latest.VersionHash, "payload", "world.dat")),
        $"automatic retry did not resume and verify version 2: {resumed.State} {resumed.Error}");
    StopApp(friendA);
    friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
    await WaitLocal(friendAPort);
    Require((await OwnerGetJson<SharedWorldStatus>(owner,
        $"/api/local/profiles/{profile.Id}/shared-world")).ConfirmedCopies == 1,
        "receipt retry counted one PC twice");
    var rotatedWorld = Path.Combine(root, "rotated-world");
    Directory.CreateDirectory(rotatedWorld);
    File.WriteAllText(Path.Combine(rotatedWorld, "world.dat"), "same WorldId, replacement source");
    profile.WorldDirectory = rotatedWorld;
    profile.SharedSavesEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "Host could not select the disposable replacement source");
    var reviewedSource = await OwnerPut<SharedWorldGovernanceRequest, SharedWorldRoster>(owner,
        $"/api/local/profiles/{profile.Id}/shared-world/governance", new(ReviewSourceChange: true));
    Require(reviewedSource.GroupId != publishedShared.Latest!.GroupId,
        "Host source review did not sign a new shared group");
    Require((await OwnerPut<SharedWorldConsentRequest, SharedWorldResult>(owner,
        $"/api/local/profiles/{profile.Id}/shared-world", new(true))).Ok,
        "Host could not re-enable sharing for the replacement source");
    Require((await OwnerPost<object, ActionResult>(owner,
        $"/api/local/profiles/{profile.Id}/start", new { })).Ok &&
        (await OwnerPost<object, ActionResult>(owner,
        $"/api/local/profiles/{profile.Id}/stop", new { })).Ok,
        "replacement source did not complete a disposable Start and Stop");
    var rotatedShared = await OwnerGetJson<SharedWorldStatus>(owner,
        $"/api/local/profiles/{profile.Id}/shared-world");
    Require(rotatedShared.Latest is { Number: 1 } &&
        rotatedShared.Latest.GroupId != publishedShared.Latest!.GroupId &&
        rotatedShared.Latest.WorldId == publishedShared.Latest.WorldId,
        "same-WorldId source rotation did not create a separate signed group: " +
        JsonSerializer.Serialize(rotatedShared, webJson));
    Require(rotatedShared.ConfirmedCopies == 0, "an old source receipt counted for the new group");
    var blockedRotation = await OwnerPost<object, ReceivedSharedWorldResult>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
    Require(!blockedRotation.Ok && blockedRotation.Code == "SourceReviewRequired" &&
        File.Exists(Path.Combine(receiverRoot, publishedShared.Latest.VersionHash, "payload", "world.dat")),
        "Friend replaced an earlier group without renewed consent");
    Require((await OwnerPut<SharedWorldConsentRequest, ReceivedSharedWorldResult>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world/consent", new(false))).Ok &&
        (await OwnerPut<SharedWorldConsentRequest, ReceivedSharedWorldResult>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok,
        "Friend could not renew consent for the signed replacement group");
    var acceptedRotation = await OwnerPost<object, ReceivedSharedWorldResult>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
    Require(acceptedRotation.Ok &&
        File.ReadAllText(Path.Combine(receiverRoot, rotatedShared.Latest!.VersionHash,
            "payload", "world.dat")) == "same WorldId, replacement source" &&
        File.Exists(Path.Combine(receiverRoot, publishedShared.Latest!.VersionHash, "payload", "world.dat")),
        $"reviewed source rotation did not preserve both verified groups: {acceptedRotation.Code}");
    Require((await OwnerGetJson<SharedWorldStatus>(owner,
        $"/api/local/profiles/{profile.Id}/shared-world")).ConfirmedCopies == 1,
        "new source did not receive a fresh copy confirmation");
    var consentB = await OwnerPut<SharedWorldConsentRequest, ReceivedSharedWorldResult>(bLocal,
        $"/api/local/friend/{profile.Id}/shared-world/consent", new(true));
    var deniedB = await OwnerPost<object, ReceivedSharedWorldResult>(bLocal,
        $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
    Require(consentB.Ok && !deniedB.Ok &&
        !Directory.Exists(Path.Combine(friendBData, "received-shared-worlds", deviceBId.ToString("N"))),
        "an ungranted paired PC received a shared save");
    var removedGrant = await OwnerPut<SharedWorldGrantRequest, PairingDecision>(owner,
        $"/api/local/devices/{deviceAId}/shared-world/{profile.Id}", new(false));
    Require(removedGrant.Ok && !(await OwnerPost<object, ReceivedSharedWorldResult>(aLocal,
        $"/api/local/friend/{profile.Id}/shared-world/pull", new { })).Ok,
        "removing a save grant did not deny a new transfer");
    Console.WriteLine("PASS pinned HTTPS automatic receipt, interrupted resume, grant revocation, and slow-transfer controls"); passes++;
    if (args.Skip(1).Contains("--shared-worlds-only", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Shared-world HTTPS checks: {passes} groups passed, 0 failed. Data: {root}");
        return 0;
    }
    settings.Profiles.Single(item => item.Id == profile.Id).SharedSavesEnabled = false;
    settings.Profiles.Single(item => item.Id == profile.Id).Backups = new BackupOptions();
    Require((await OwnerPut<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
        "companion fixture settings could not be restored after shared-world coverage");

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
    async Task<(HttpStatusCode Status, CredentialRenewal? Renewal)> RenewCredential(PairingCredential credential,
        Guid? requestId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/companion/credential/renew")
        { Content = JsonContent.Create(new CredentialRenewalRequest(credential.DeviceId, requestId ?? renewalId), options: webJson) };
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
    var oldTokenNewRenewal = await RenewCredential(priorCredentialC, Guid.NewGuid());
    var priorCredentialStatus = await PublicStatus(publicClient, priorCredentialC);
    var currentCredentialStatus = await PublicStatus(publicClient, credentialC);
    Require(priorCredentialStatus.Protocol?.Compatible == true &&
        currentCredentialStatus.Protocol?.Compatible == true &&
        oldTokenNewRenewal.Status == HttpStatusCode.Conflict && oldTokenNewRenewal.Renewal is null,
        $"credential renewal stranded a token or let the overlap token mint another credential: " +
        $"oldCompatible={priorCredentialStatus.Protocol?.Compatible}, newCompatible={currentCredentialStatus.Protocol?.Compatible}, " +
        $"secondRenewal={(int)oldTokenNewRenewal.Status}");
    Console.WriteLine("PASS credential renewal is idempotent and the overlap token cannot renew itself"); passes++;

    var accessActivation = await publicClient.PostAsJsonAsync("/api/companion/pair",
        new PairingActivation(inviteA.DeviceId, inviteA.Code, true), webJson);
    Require(accessActivation.IsSuccessStatusCode, "HTTP access-expiry device activation failed");
    var accessHttpCredential = await accessActivation.Content.ReadFromJsonAsync<PairingCredential>(webJson)
        ?? throw new Exception("empty HTTP access-expiry activation");
    Require((await OwnerPut<DevicePermissionRequest, PairingDecision>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/permissions",
        new(true, true, CanExtendTimer: true, CanViewLogs: true))).Ok,
        "HTTP access-expiry device permissions were not saved");
    var accessBefore = (await owner.GetFromJsonAsync<JsonElement>("/api/local/companion"))
        .GetProperty("devices").EnumerateArray()
        .Single(device => device.GetProperty("id").GetGuid() == accessHttpCredential.DeviceId);
    var accessAssignments = accessBefore.GetProperty("assignedProfileIds").EnumerateArray()
        .Select(item => item.GetGuid()).ToArray();
    var friendModeExpiry = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(bLocal,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry", new(Clear: true));
    var unknownExpiry = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{Guid.NewGuid()}/access-expiry", new(Clear: true));
    var pastExpiry = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry",
        new(AccessExpiresUtc: DateTimeOffset.UtcNow.AddSeconds(-1)));
    var distantExpiry = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry",
        new(AccessExpiresUtc: DateTimeOffset.UtcNow.AddDays(366)));
    var nonUtcExpiry = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry",
        new(AccessExpiresUtc: DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)).AddHours(1)));
    var unknownDuration = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry", new(Duration: "Forever"));
    using (var malformedRequest = new HttpRequestMessage(HttpMethod.Put,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry"))
    {
        malformedRequest.Content = new StringContent("{\"accessExpiresUtc\":\"not-a-timestamp\"}",
            Encoding.UTF8, "application/json");
        malformedRequest.Headers.Add("Origin", owner.BaseAddress!.ToString().TrimEnd('/'));
        malformedRequest.Headers.Add("X-TogetherServer-Local", "1");
        using var malformedResponse = await owner.SendAsync(malformedRequest);
        Require(malformedResponse.StatusCode == HttpStatusCode.BadRequest,
            "a malformed owner access timestamp was accepted");
    }
    Require(friendModeExpiry.Code == "FriendMode" && unknownExpiry.Code == "UnknownDevice" &&
        pastExpiry.Code == "AccessExpiryInPast" && distantExpiry.Code == "AccessExpiryTooDistant" &&
        nonUtcExpiry.Code == "InvalidAccessExpiry" && unknownDuration.Code == "InvalidAccessDuration",
        "the local owner access endpoint did not reject Friend mode, unknown device, or invalid values");

    var httpDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
    var operationCountBeforeAccessExpiry =
        (await owner.GetFromJsonAsync<List<RemoteOperationView>>("/api/local/operations"))!.Count;
    var setHttpExpiry = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry",
        new(AccessExpiresUtc: httpDeadline));
    Require(setHttpExpiry.Ok && setHttpExpiry.AccessExpiresUtc == httpDeadline,
        "the local owner endpoint did not return the saved UTC deadline");
    var expiryDelay = httpDeadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
    if (expiryDelay > TimeSpan.Zero) await Task.Delay(expiryDelay);

    async Task<(HttpStatusCode Status, string Code, string Body)> ExpiredCompanionRequest(
        HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), webJson),
                Encoding.UTF8, "application/json");
        request.Headers.Add("X-Device-Id", accessHttpCredential.DeviceId.ToString());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessHttpCredential.Credential);
        using var response = await publicClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseBody);
        return (response.StatusCode, document.RootElement.GetProperty("code").GetString() ?? "", responseBody);
    }

    var expiredDenials = new List<(HttpStatusCode Status, string Code, string Body)>
    {
        await ExpiredCompanionRequest(HttpMethod.Get, "/api/companion/status"),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/heartbeat",
            new HeartbeatRequest(accessHttpCredential.DeviceId, Guid.NewGuid(), 1, "expiry-check",
                CompanionProtocol.Current, CompanionProtocol.Capabilities)),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/start",
            new RemoteActionRequest(accessHttpCredential.DeviceId, profile.Id)),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/stop",
            new RemoteActionRequest(accessHttpCredential.DeviceId, profile.Id)),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/restart",
            new RemoteActionRequest(accessHttpCredential.DeviceId, profile.Id)),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/replace",
            new RemoteActionRequest(accessHttpCredential.DeviceId, profile.Id)),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/extend",
            new RemoteActionRequest(accessHttpCredential.DeviceId, profile.Id)),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/refresh",
            new RemoteActionRequest(accessHttpCredential.DeviceId, profile.Id)),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/credential/renew",
            new CredentialRenewalRequest(accessHttpCredential.DeviceId, Guid.NewGuid())),
        await ExpiredCompanionRequest(HttpMethod.Get, $"/api/companion/servers/{profile.Id}/logs"),
        await ExpiredCompanionRequest(HttpMethod.Get, $"/api/companion/operations/{Guid.NewGuid()}"),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/endpoint/recover",
            new EndpointRecoveryProofRequest(accessHttpCredential.DeviceId)),
        await ExpiredCompanionRequest(HttpMethod.Post, "/api/companion/credential/revoke",
            new DeviceSelfRequest(accessHttpCredential.DeviceId))
    };
    var accessAfter = (await owner.GetFromJsonAsync<JsonElement>("/api/local/companion"))
        .GetProperty("devices").EnumerateArray()
        .Single(device => device.GetProperty("id").GetGuid() == accessHttpCredential.DeviceId);
    var operationCountAfterExpiry =
        (await owner.GetFromJsonAsync<List<RemoteOperationView>>("/api/local/operations"))!.Count;
    Require(expiredDenials.All(item => item.Status == HttpStatusCode.Forbidden && item.Code == "AccessExpired" &&
            !item.Body.Contains(accessHttpCredential.Credential, StringComparison.Ordinal)) &&
        accessAfter.GetProperty("accessExpired").GetBoolean() &&
        accessAfter.GetProperty("accessExpiresUtc").GetDateTimeOffset() == httpDeadline &&
        !accessAfter.GetProperty("revoked").GetBoolean() && accessAfter.GetProperty("paired").GetBoolean() &&
        accessAfter.GetProperty("assignedProfileIds").EnumerateArray().Select(item => item.GetGuid())
            .SequenceEqual(accessAssignments) && accessAfter.GetProperty("canStart").GetBoolean() &&
        accessAfter.GetProperty("canStop").GetBoolean() && accessAfter.GetProperty("canExtendTimer").GetBoolean() &&
        accessAfter.GetProperty("canViewLogs").GetBoolean() && operationCountAfterExpiry == operationCountBeforeAccessExpiry,
        "expired owner access leaked a secret, allowed a companion path, journaled lifecycle work, or changed device authority");
    var accessActivity = (await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Activity!;
    Require(accessActivity.Any(item => item.DeviceId == accessHttpCredential.DeviceId && item.Action == "AccessExpirySet") &&
        accessActivity.Any(item => item.DeviceId == accessHttpCredential.DeviceId && item.Action == "AccessExpired") &&
        !File.ReadAllText(Path.Combine(hostData, "audit.log"))
            .Contains(accessHttpCredential.Credential, StringComparison.Ordinal),
        "Host activity/audit omitted access expiry or exposed the device credential");

    var extendedHttpAccess = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry",
        new(Duration: DeviceAccessDurations.OneHour));
    Require(extendedHttpAccess.Ok && !extendedHttpAccess.AccessExpired &&
        (await PublicStatus(publicClient, accessHttpCredential)).Profiles.Single().Id == profile.Id,
        "reviewed-duration extension did not immediately restore companion status");
    var clearHttpDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
    Require((await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry",
        new(AccessExpiresUtc: clearHttpDeadline))).Ok, "clear-path deadline was not saved");
    var clearDelay = clearHttpDeadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
    if (clearDelay > TimeSpan.Zero) await Task.Delay(clearDelay);
    var clearedHttpAccess = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry", new(Clear: true));
    var clearedHttpView = (await owner.GetFromJsonAsync<JsonElement>("/api/local/companion"))
        .GetProperty("devices").EnumerateArray()
        .Single(device => device.GetProperty("id").GetGuid() == accessHttpCredential.DeviceId);
    Require(clearedHttpAccess.Code == "AccessExpiryCleared" &&
        clearedHttpView.GetProperty("accessExpiresUtc").ValueKind == JsonValueKind.Null &&
        !clearedHttpView.GetProperty("accessExpired").GetBoolean() &&
        (await PublicStatus(publicClient, accessHttpCredential)).Profiles.Single().Id == profile.Id,
        "clearing expired owner access did not restore the unchanged companion credential");
    Require((await OwnerPost<object, PairingDecision>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/revoke", new { })).Ok,
        "HTTP access-expiry test device could not be revoked");
    var revokedExpiry = await OwnerPut<DeviceAccessExpiryRequest, DeviceAccessExpiryResult>(owner,
        $"/api/local/devices/{accessHttpCredential.DeviceId}/access-expiry",
        new(Duration: DeviceAccessDurations.OneDay));
    Require(revokedExpiry.Code == "Revoked", "the local owner endpoint changed a revoked device deadline");
    Console.WriteLine("PASS owner access expiry denies every authenticated companion path and restores only by local extension or clear"); passes++;

    var joinActivation = await publicClient.PostAsJsonAsync("/api/companion/pair",
        new PairingActivation(inviteB.DeviceId, inviteB.Code, true), webJson);
    Require(joinActivation.IsSuccessStatusCode, "second server code activation failed");
    var joinCredential = await joinActivation.Content.ReadFromJsonAsync<PairingCredential>(webJson) ?? throw new Exception("empty second-server activation");
    var joinStatus = await PublicStatus(publicClient, joinCredential);
    Require(joinStatus.Profiles.Count == 1 && joinStatus.Profiles.Single().Id == joinProfile.Id &&
        joinStatus.Profiles.Single().JoinAddress == $"1.2.3.4:{joinProfile.GamePort}" &&
        joinStatus.Protocol is { ProtocolVersion: CompanionProtocol.Current, Compatible: true } &&
        joinStatus.Protocol.Capabilities.Contains("durable-operations") &&
        joinStatus.Protocol.Capabilities.Contains(CompanionProtocol.ServerLogsCapability),
        "a credential did not remain scoped to its server and current join address: " +
        JsonSerializer.Serialize(joinStatus, webJson));
    var heartbeatC = new HeartbeatRequest(credentialC.DeviceId, Guid.NewGuid(), 1, "check",
        CompanionProtocol.Current, CompanionProtocol.Capabilities);
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
        injected.Content = new StringContent(JsonSerializer.Serialize(new
        {
            deviceId = credentialC.DeviceId,
            profileId = profile.Id,
            script = "never execute this"
        }, webJson), Encoding.UTF8, "application/json");
        injected.Headers.Add("X-Device-Id", credentialC.DeviceId.ToString());
        injected.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        injected.Headers.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
        injected.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialC.Credential);
        using var rejected = await publicClient.SendAsync(injected);
        Require(rejected.StatusCode == HttpStatusCode.BadRequest, "extra command text was accepted");
    }
    var operationCountBeforeProtocolDenial =
        (await owner.GetFromJsonAsync<List<RemoteOperationView>>("/api/local/operations"))!.Count;
    using (var incompatible = new HttpRequestMessage(HttpMethod.Post, "/api/companion/start"))
    {
        incompatible.Content = JsonContent.Create(new RemoteActionRequest(credentialC.DeviceId, profile.Id), options: webJson);
        incompatible.Headers.Add("X-Device-Id", credentialC.DeviceId.ToString());
        incompatible.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        incompatible.Headers.Add(CompanionProtocol.HeaderName, (CompanionProtocol.Current + 1).ToString());
        incompatible.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialC.Credential);
        using var rejected = await publicClient.SendAsync(incompatible);
        var denial = await rejected.Content.ReadFromJsonAsync<FriendActionResult>(webJson);
        Require(rejected.StatusCode == HttpStatusCode.Conflict && denial?.Code == "ProtocolIncompatible" &&
            (await owner.GetFromJsonAsync<List<RemoteOperationView>>("/api/local/operations"))!.Count ==
                operationCountBeforeProtocolDenial,
            "an incompatible client action was journaled or accepted");
    }
    Console.WriteLine("PASS incompatible action protocol is rejected before journaling"); passes++;
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
        conflicting.Headers.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
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
    var restartedRenewal = await RenewCredential(priorCredentialC);
    Require(restartedRenewal.Status == HttpStatusCode.OK &&
        restartedRenewal.Renewal?.Credential == credentialC.Credential &&
        (await PublicStatus(publicClient, priorCredentialC)).Protocol?.Compatible == true &&
        (await PublicStatus(publicClient, credentialC)).Protocol?.Compatible == true,
        "credential renewal receipt or overlap credentials did not survive Host restart");
    Console.WriteLine("PASS credential renewal and overlap persist atomically across Host restart"); passes++;
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
    var peerProfile = new ServerProfile
    {
        Name = "Peer fixture",
        WorldId = "peer-fixture",
        WorldDirectory = peerWorld,
        GamePort = gamePort + 10,
        ExecutablePath = fixturePath
    };
    peerHost = StartApp(appPath, "--host", peerHostPort, peerData);
    await WaitLocal(peerHostPort);
    using var peerOwner = LocalClient(peerHostPort);
    var peerSettings = new HostSettings
    {
        CompanionEndpoint = peerEndpoint,
        CompanionPort = peerCompanionPort,
        CompanionBindAddress = "127.0.0.1",
        Profiles = [peerProfile]
    };
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
    using (var diagnosticsFromFriendPage = await OwnerGet(owner, "/api/local/diagnostics"))
        Require(diagnosticsFromFriendPage.StatusCode == HttpStatusCode.Conflict,
            "owner diagnostics were exposed through the Friend-mode page");
    using (var reportFromFriendPage = await OwnerGet(owner, "/api/local/support-report"))
        Require(reportFromFriendPage.StatusCode == HttpStatusCode.Conflict,
            "owner support export was exposed through the Friend-mode page");
    using (var sessionsFromFriendPage = await OwnerGet(owner,
        $"/api/local/profiles/{profile.Id}/sessions?limit=8"))
        Require(sessionsFromFriendPage.StatusCode == HttpStatusCode.Conflict,
            "owner session history was exposed through the Friend-mode page");
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
    using (var sessionResponse = await OwnerGet(owner,
        $"/api/local/profiles/{profile.Id}/sessions?limit=8"))
    {
        var sessionJson = await sessionResponse.Content.ReadAsStringAsync();
        var sessions = JsonSerializer.Deserialize<RecentServerSessionsResult>(sessionJson, webJson) ??
            throw new Exception("empty recent-session response after graceful Stop");
        var session = sessions.Sessions.First();
        Require(sessionResponse.IsSuccessStatusCode && session.ProfileId == profile.Id &&
            session.OperationId != Guid.Empty && session.Outcome == ServerSessionOutcome.GracefulStop &&
            session.BackupResult == ServerSessionBackupResult.NotConfigured &&
            session.ReadyEverObserved == false && session.StartedUtc is not null && session.EndedUtc != default &&
            session.DurationSeconds is >= 0 && session.LastTrustedOnlinePlayers is null &&
            !sessionJson.Contains("worldDirectory", StringComparison.OrdinalIgnoreCase) &&
            !sessionJson.Contains("executablePath", StringComparison.OrdinalIgnoreCase) &&
            !sessionJson.Contains("processId", StringComparison.OrdinalIgnoreCase) &&
            !sessionJson.Contains(world, StringComparison.OrdinalIgnoreCase) &&
            !sessionJson.Contains(fixturePath, StringComparison.OrdinalIgnoreCase),
            "recent-session API lost authoritative fields or exposed process/path/world data");
    }
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

    for (var i = 0; i < 25; i++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
        request.Headers.Add("X-Device-Id", joinCredential.DeviceId.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var response = await publicClient.SendAsync(request);
        Require(response.StatusCode == HttpStatusCode.Unauthorized,
            "an unauthenticated spoof was not rejected before device limiting");
    }
    Require((await PublicStatus(publicClient, joinCredential)).Protocol?.Compatible == true,
        "spoofed device headers consumed the victim's authenticated request allowance");
    var limited = false;
    for (var i = 0; i < 220; i++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/companion/status");
        request.Headers.Add("X-Device-Id", rotatedCredential.DeviceId.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotatedCredential.Credential);
        using var response = await publicClient.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) limited = true;
    }
    Require(limited, "an authenticated device did not reach its own request limit");
    Require((await PublicStatus(publicClient, joinCredential)).Protocol?.Compatible == true,
        "one authenticated device's burst throttled another device behind the same source IP");
    Console.WriteLine("PASS authenticated per-device limiting ignores spoofed headers and isolates one source IP"); passes++;

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
    settings.CompanionBindAddress = "127.0.0.2";
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
    var stopProfile = new ServerProfile
    {
        Kind = "Valheim",
        Name = "Restricted synthetic world",
        ServerName = "Fixture \"Valheim\"",
        WorldSource = "New",
        WorldId = "fixture-world",
        ExecutablePath = valheimFixturePath,
        GamePort = gamePort + 20
    };
    var replacementProfile = new ServerProfile
    {
        Kind = "Valheim",
        Name = "Replacement synthetic world",
        ServerName = "Fixture \"Valheim\"",
        WorldSource = "New",
        WorldId = "fixture-world",
        ExecutablePath = valheimFixturePath,
        GamePort = stopProfile.GamePort
    };
    var customLogProfile = new ServerProfile
    {
        Kind = GameKinds.Custom,
        Name = "Local custom logs",
        WorldId = "custom-log-world",
        WorldDirectory = Path.Combine(stopHostData, "custom-log-world"),
        GamePort = gamePort + 30,
        Custom = new CustomGameOptions { GameName = "Local custom logs" }
    };
    Directory.CreateDirectory(customLogProfile.WorldDirectory);
    stopProfileId = stopProfile.Id;
    replacementProfileId = replacementProfile.Id;
    stopProfile.WorldDirectory = Path.Combine(stopHostData, "worlds", stopProfile.Id.ToString("N"));
    replacementProfile.WorldDirectory = Path.Combine(stopHostData, "worlds", replacementProfile.Id.ToString("N"));
    stopHost = StartApp(appPath, "--host", stopHostPort, stopHostData, stopDelayMs: 10000);
    stopFriend = StartApp(appPath, "--friend", stopFriendPort, stopFriendData);
    await WaitLocal(stopHostPort); await WaitLocal(stopFriendPort);
    using var stopOwner = LocalClient(stopHostPort);
    using var stopFriendLocal = LocalClient(stopFriendPort);
    var stopSettings = new HostSettings
    {
        Profiles = [stopProfile, replacementProfile, customLogProfile],
        CompanionEndpoint = stopEndpoint,
        CompanionBindAddress = "127.0.0.1",
        CompanionPort = stopPublicPort,
        AutoShutdownEnabled = true,
        IdleMinutes = 15,
        FriendTimerExtensionMinutes = 5,
        FriendTimerExtensionMaximumMinutes = 10
    };
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
    using var directLogClient = PinnedClient(stopEndpoint, stopInvite.Fingerprint);
    using var directLogPairResponse = await directLogClient.PostAsJsonAsync("/api/companion/pair",
        new PairingActivation(stopProfile.Id, stopInvite.Code, true), webJson);
    var directLogCredential = await directLogPairResponse.Content.ReadFromJsonAsync<PairingCredential>(webJson)
        ?? throw new Exception("direct log credential was empty");
    var defaultLogStatus = await PublicStatus(directLogClient, directLogCredential);
    var deniedWithoutLogPermission = await PublicLogs(directLogClient, directLogCredential, stopProfile.Id);
    var unassignedLogDenied = await PublicLogs(directLogClient, directLogCredential, replacementProfile.Id);
    var directDeviceBeforeGrant = (await stopOwner.GetFromJsonAsync<JsonElement>("/api/local/companion"))
        .GetProperty("devices").EnumerateArray().Single(device => device.GetProperty("id").GetGuid() == directLogCredential.DeviceId);
    Require(!defaultLogStatus.Profiles.Single().CanViewLogs &&
        !directDeviceBeforeGrant.GetProperty("canViewLogs").GetBoolean() &&
        deniedWithoutLogPermission.Status == HttpStatusCode.Forbidden &&
        deniedWithoutLogPermission.Body.GetProperty("code").GetString() == "PermissionDenied" &&
        unassignedLogDenied.Status == HttpStatusCode.Forbidden &&
        unassignedLogDenied.Body.GetProperty("code").GetString() == "PermissionDenied",
        "Start permission granted logs or an unassigned profile bypassed log authorization");
    var logPermissions = new[]
    {
        new DeviceServerPermissionRequest(stopProfile.Id, true, false, CanViewLogs: true),
        new DeviceServerPermissionRequest(customLogProfile.Id, false, false, CanViewLogs: true)
    };
    Require((await OwnerPut<DeviceServerAccessRequest, PairingDecision>(stopOwner,
        $"/api/local/devices/{directLogCredential.DeviceId}/servers",
        new([stopProfile.Id, customLogProfile.Id], logPermissions))).Ok,
        "View logs permission could not be granted independently per server");
    var permittedLogStatus = await PublicStatus(directLogClient, directLogCredential);
    var missingManagedLog = await PublicLogs(directLogClient, directLogCredential, stopProfile.Id);
    var oldPeerLogs = await PublicLogs(directLogClient, directLogCredential, stopProfile.Id,
        includeProtocol: false);
    var customRemoteLogs = await PublicLogs(directLogClient, directLogCredential, customLogProfile.Id);
    Require(permittedLogStatus.Profiles.Single(item => item.Id == stopProfile.Id).CanViewLogs &&
        permittedLogStatus.Profiles.Single(item => item.Id == customLogProfile.Id).CanViewLogs &&
        permittedLogStatus.Protocol?.Capabilities.Contains(CompanionProtocol.ServerLogsCapability) == true &&
        missingManagedLog.Status == HttpStatusCode.OK &&
        missingManagedLog.Body.GetProperty("sourceState").GetString() == ServerLogSourceStates.Missing &&
        oldPeerLogs.Status == HttpStatusCode.Conflict &&
        oldPeerLogs.Body.GetProperty("code").GetString() == "ServerLogsUpdateRequired" &&
        oldPeerLogs.Body.GetProperty("sourceState").GetString() == ServerLogSourceStates.Unsupported &&
        customRemoteLogs.Status == HttpStatusCode.Forbidden &&
        customRemoteLogs.Body.GetProperty("code").GetString() == "CustomRemoteLogsUnavailable" &&
        customRemoteLogs.Body.GetProperty("records").GetArrayLength() == 0,
        "capability advertisement, old-peer gating, missing source state, or Custom remote log denial was incorrect");
    Require((await OwnerPut<DevicePermissionRequest, PairingDecision>(stopOwner,
        $"/api/local/devices/{directLogCredential.DeviceId}/permissions",
        new(true, false, "logs", CanViewLogs: false))).Ok,
        "View logs permission could not be revoked");
    var revokedLogRead = await PublicLogs(directLogClient, directLogCredential, stopProfile.Id);
    Require(revokedLogRead.Status == HttpStatusCode.Forbidden &&
        revokedLogRead.Body.GetProperty("code").GetString() == "PermissionDenied",
        "View logs revocation did not take effect on the next request");
    Require((await OwnerPut<DeviceServerAccessRequest, PairingDecision>(stopOwner,
        $"/api/local/devices/{directLogCredential.DeviceId}/servers",
        new([stopProfile.Id, customLogProfile.Id], logPermissions))).Ok,
        "View logs permission could not be restored for read checks");
    Console.WriteLine("PASS View logs defaults false and enforces assignment, per-server grant, revocation, and no Custom remote logs"); passes++;
    Require((await OwnerPut<DevicePermissionRequest, PairingDecision>(stopOwner,
        $"/api/local/devices/{stopDeviceId}/permissions",
        new(true, true, CanExtendTimer: true, CanViewLogs: true))).Ok,
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
    var maintenanceLogs = await OwnerGetJson<ServerLogResult>(stopFriendLocal,
        $"/api/local/friend/{stopProfile.Id}/logs?limit=1");
    Require(maintenanceView.Profiles.Single().MaintenanceEnabled &&
        maintenanceView.Profiles.Single().MaintenanceMessage == "Owner maintenance check" &&
        maintenanceView.Profiles.Single().CanViewLogs &&
        maintenanceView.HostCapabilities?.Contains(CompanionProtocol.ServerLogsCapability) == true &&
        maintenanceView.Activity?.Any(item => item.Category == "Maintenance" && item.ProfileId == stopProfile.Id) == true &&
        maintenanceLogs.Code == "NoManagedRunLog" &&
        maintenanceLogs.SourceState == ServerLogSourceStates.Missing &&
        !maintenanceStart.Ok && maintenanceStart.Code == "MaintenanceMode",
        "maintenance denied remote lifecycle actions or incorrectly denied independent read-only logs");
    stopProfile.Maintenance = new MaintenanceOptions();
    stopSettings.RemoteControlsEnabled = false;
    Require((await OwnerPut<HostSettings, ActionResult>(stopOwner, "/api/local/settings", stopSettings)).Ok,
        "maintenance mode could not be ended while pausing remote controls");
    var controlsPausedView = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    var controlsPausedLogs = await OwnerGetJson<ServerLogResult>(stopFriendLocal,
        $"/api/local/friend/{stopProfile.Id}/logs?limit=1");
    Require(!controlsPausedView.RemoteControlsEnabled && controlsPausedView.Profiles.Single().CanViewLogs &&
        controlsPausedLogs.Code == "NoManagedRunLog" &&
        controlsPausedLogs.SourceState == ServerLogSourceStates.Missing,
        "pausing remote controls incorrectly revoked independent read-only log access");
    stopSettings.RemoteControlsEnabled = true;
    Require((await OwnerPut<HostSettings, ActionResult>(stopOwner, "/api/local/settings", stopSettings)).Ok,
        "remote controls could not be restored after the log-permission independence check");
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
    var ownedLogPath = Directory.GetFiles(Path.Combine(stopHostData, "logs"), "*.log").Single();
    File.AppendAllText(ownedLogPath,
        "09/28/2026 12:00:01: password=friend-secret bearer token-secret 10.20.30.40 C:\\Users\\Private\\world.db\n" +
        "09/28/2026 12:00:02: Chat player Alice SteamID 123456789 \u001b[31mwarn\rspoof\n");
    var arbitraryPath = Path.Combine(stopHostData, "owner-secret.txt");
    File.WriteAllText(arbitraryPath, "arbitrary-file-secret");
    using var hostWithoutLocalHeader = await stopOwner.GetAsync(
        $"/api/local/profiles/{stopProfile.Id}/logs?limit=1");
    using var friendWithoutLocalHeader = await stopFriendLocal.GetAsync(
        $"/api/local/friend/{stopProfile.Id}/logs?limit=1");
    using var unknownLogField = await OwnerGet(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/logs?path={Uri.EscapeDataString(arbitraryPath)}");
    using var invalidLimit = await OwnerGet(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/logs?limit=201");
    var localLogs = await OwnerGetJson<ServerLogResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/logs?limit=1");
    var friendProxyLogs = await OwnerGetJson<ServerLogResult>(stopFriendLocal,
        $"/api/local/friend/{stopProfile.Id}/logs?limit=200");
    var remoteLogs = await PublicLogs(directLogClient, directLogCredential, stopProfile.Id, "limit=200");
    var remoteLogResult = remoteLogs.Body.Deserialize<ServerLogResult>(webJson)
        ?? throw new Exception("remote log response was empty");
    var remoteText = string.Join(" ", remoteLogResult.Records.Select(item => item.Message));
    var proxyText = string.Join(" ", friendProxyLogs.Records.Select(item => item.Message));
    Require(hostWithoutLocalHeader.StatusCode == HttpStatusCode.Forbidden &&
        friendWithoutLocalHeader.StatusCode == HttpStatusCode.Forbidden &&
        unknownLogField.StatusCode == HttpStatusCode.BadRequest &&
        invalidLimit.StatusCode == HttpStatusCode.BadRequest && localLogs is { Ok: true } &&
        localLogs.Records.Count == 1 && localLogs.HasMore && !string.IsNullOrWhiteSpace(localLogs.Cursor) &&
        friendProxyLogs is { Ok: true, SourceState: ServerLogSourceStates.Active } &&
        friendProxyLogs.Records.Count >= 3 && !proxyText.Contains("friend-secret", StringComparison.Ordinal) &&
        remoteLogs.Status == HttpStatusCode.OK && remoteLogResult.Records.Count >= 3 &&
        !remoteText.Contains("friend-secret", StringComparison.Ordinal) &&
        !remoteText.Contains("token-secret", StringComparison.Ordinal) &&
        !remoteText.Contains("10.20.30.40", StringComparison.Ordinal) &&
        !remoteText.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase) &&
        !remoteText.Contains("Alice", StringComparison.Ordinal) &&
        !remoteText.Contains("123456789", StringComparison.Ordinal) &&
        !remoteText.Contains("arbitrary-file-secret", StringComparison.Ordinal) &&
        remoteLogResult.Records.All(record => record.Message.All(character => !char.IsControl(character))),
        "local GET protection, Friend proxying, query bounds, exact owned path, or log sanitization failed");
    var invalidCursor = await PublicLogs(directLogClient, directLogCredential, stopProfile.Id, "cursor=invalid");
    Require(invalidCursor.Status == HttpStatusCode.OK &&
        invalidCursor.Body.GetProperty("code").GetString() == "InvalidLogCursor",
        "an invalid cursor was accepted or did not return a typed failure");
    Console.WriteLine("PASS Host and Friend Valheim log reads are bounded, exact-run, sanitized, and cursor scoped"); passes++;
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
    Require(friendExtended.Ok && friendExtended.Code == "CountdownExtended",
        "the fixed Friend timer increment was not applied");
    var extendedCountdown = await OwnerPost<CountdownExtensionRequest, ActionResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/countdown/extend", new(37));
    Require(extendedCountdown.Ok, "Host could not extend an active countdown");
    hostDeadline = extendedCountdown.Snapshot.Runs.Single(run => run.ProfileId == stopProfile.Id).AutoShutdownAtUtc;
    Require(hostDeadline is not null && hostDeadline.Value > DateTimeOffset.UtcNow.AddMinutes(51),
        "Host countdown extension did not add the requested 37 minutes");
    var stopView = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    var friendProfile = stopView.Profiles.Single();
    Require(friendProfile.OnlinePlayers == 0 && friendProfile.MaxPlayers == 10 &&
        friendProfile.AutoShutdownAtUtc == hostDeadline && friendProfile.CanStopNow && friendProfile.StopReason is null &&
        friendProfile.CanExtendTimer && friendProfile.TimerExtensionRemainingMinutes == 5,
        "Friend UI did not receive the player count, shared countdown, and available remote Stop state");
    var playerCountPath = Path.Combine(stopProfile.WorldDirectory, "synthetic-online-players.txt");
    File.WriteAllText(playerCountPath, "1");
    var friendRefresh = await FriendAction(stopFriendLocal, stopProfile.Id, "refresh");
    var refreshedFriendView = await stopFriendLocal.GetFromJsonAsync<FriendView>("/api/local/snapshot");
    var refreshedHostView = await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
    var observedOccupied = refreshedFriendView!.Profiles.Single();
    Require(friendRefresh.Ok && friendRefresh.Code == "PlayerCountRefreshed" &&
        friendRefresh.Status?.Profiles.Single().OnlinePlayers == 1 &&
        refreshedHostView!.Runs.Single(run => run.ProfileId == stopProfile.Id).OnlinePlayers == 1 &&
        observedOccupied.OnlinePlayers == 1 && observedOccupied.AutoShutdownAtUtc is null &&
        observedOccupied.CanExtendTimer &&
        observedOccupied.AutoShutdownReason?.Contains("added minutes are saved", StringComparison.Ordinal) == true &&
        !observedOccupied.CanStopNow &&
        observedOccupied.StopReason?.Contains("1 player is online", StringComparison.Ordinal) == true,
        "Friend refresh did not update the Host cache, response status, and local Friend view together");
    var occupiedFriendExtension = await FriendAction(stopFriendLocal, stopProfile.Id, "extend");
    var friendLimit = await FriendAction(stopFriendLocal, stopProfile.Id, "extend");
    Require(occupiedFriendExtension.Ok && occupiedFriendExtension.Code == "CountdownExtended" &&
        !friendLimit.Ok && friendLimit.Code == "ExtensionLimitReached",
        "Friend-added time was not accepted while occupied or its per-run maximum was not enforced");
    Console.WriteLine("PASS Friend player-count refresh returns one canonical Host status to both views"); passes++;
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
    var persistedFriendLimit = await FriendAction(stopFriendLocal, stopProfile.Id, "extend");
    var resumedTimer = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    Require(!persistedFriendLimit.Ok && persistedFriendLimit.Code == "ExtensionLimitReached" &&
        resumedTimer.Profiles.Single().AutoShutdownAtUtc > DateTimeOffset.UtcNow.AddMinutes(60) &&
        resumedTimer.Profiles.Single().TimerExtensionRemainingMinutes == 0,
        "player activity discarded added time or reset the Friend extension allowance for the same server run");
    Require((await OwnerPut<DeviceServerAccessRequest, PairingDecision>(stopOwner,
        $"/api/local/devices/{stopDeviceId}/servers", new([stopProfile.Id, replacementProfile.Id],
        [new DeviceServerPermissionRequest(stopProfile.Id, true, true,
                CanExtendTimer: true, CanViewLogs: true),
            new DeviceServerPermissionRequest(replacementProfile.Id, true, false)]))).Ok,
        "could not assign the queued-start profile for shutdown-race coverage");
    var stopSubmitted = await OwnerPost<object, FriendActionResult>(stopFriendLocal,
        $"/api/local/friend/{stopProfile.Id}/stop", new { });
    var startSubmitted = await OwnerPost<object, FriendActionResult>(stopFriendLocal,
        $"/api/local/friend/{replacementProfile.Id}/start", new { });
    Require(stopSubmitted.Code == "OperationAccepted" && stopSubmitted.OperationId is not null &&
        startSubmitted.Code == "OperationAccepted" && startSubmitted.OperationId is not null,
        "long Stop and queued Start were not accepted as durable operations");
    using var quittingOwner = new HttpClient
    {
        BaseAddress = new Uri($"http://127.0.0.1:{stopHostPort}"),
        Timeout = TimeSpan.FromSeconds(30)
    };
    var quitTask = OwnerPost<object, JsonElement>(quittingOwner, "/api/local/quit", new { });
    StopApp(stopFriend);
    stopFriend = StartApp(appPath, "--friend", stopFriendPort, stopFriendData);
    await WaitLocal(stopFriendPort);
    var operationPollTimer = Stopwatch.StartNew();
    var runningOperations = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
    operationPollTimer.Stop();
    var stopProgress = runningOperations.Profiles.Select(item => item.Operation)
        .Single(operation => operation?.Id == stopSubmitted.OperationId);
    var startProgress = runningOperations.Profiles.Select(item => item.Operation)
        .Single(operation => operation?.Id == startSubmitted.OperationId);
    Require(operationPollTimer.Elapsed < TimeSpan.FromSeconds(4) &&
        stopProgress?.State is RemoteOperationStates.Running or RemoteOperationStates.Succeeded &&
        startProgress?.State == RemoteOperationStates.Pending,
        "a restarted Friend could not observe durable operation progress without waiting on the locked heartbeat");
    var quitDuringQueue = await quitTask;
    Require(quitDuringQueue.GetProperty("ok").GetBoolean() &&
        quitDuringQueue.GetProperty("code").GetString() == "Closing",
        "Host Quit did not take ownership of shutdown after the long Stop completed");
    using (var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        await stopHost.WaitForExitAsync(exitTimeout.Token);
    StopApp(stopHost);
    stopHost = StartApp(appPath, "--host", stopHostPort, stopHostData, stopDelayMs: 1000);
    await WaitLocal(stopHostPort);
    FriendView? recoveredOperations = null;
    for (var i = 0; i < 80; i++)
    {
        recoveredOperations = await OwnerPost<object, FriendView>(stopFriendLocal, "/api/local/friend/poll", new { });
        var states = recoveredOperations.Profiles.Select(item => item.Operation)
            .Where(operation => operation?.Id == stopSubmitted.OperationId || operation?.Id == startSubmitted.OperationId)
            .ToList();
        if (states.Count == 2 && states.All(operation => operation is not null && RemoteOperationStates.Terminal(operation.State))) break;
        await Task.Delay(100);
    }
    var recoveredStop = recoveredOperations!.Profiles.Select(item => item.Operation)
        .Single(operation => operation?.Id == stopSubmitted.OperationId);
    var blockedStart = recoveredOperations.Profiles.Select(item => item.Operation)
        .Single(operation => operation?.Id == startSubmitted.OperationId);
    var afterShutdownRace = await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
    Require(recoveredStop is { State: RemoteOperationStates.Succeeded, Code: "ValheimStopped" } &&
        blockedStart is not null && blockedStart.State is RemoteOperationStates.Failed or RemoteOperationStates.Interrupted &&
        afterShutdownRace!.Runs.All(run => run.State == "Offline"),
        "a queued remote Start launched during shutdown or unfinished work was not recovered as terminal");
    Require(File.ReadAllText(Path.Combine(stopProfile.WorldDirectory, "synthetic-stop.marker")) == "Ctrl+C received",
        "remote Stop did not use the synthetic console's graceful exit");
    Console.WriteLine("PASS long remote Stop survives Friend restart and queued Start cannot race Host shutdown"); passes++;

    var retainedHostLogs = await OwnerGetJson<ServerLogResult>(stopOwner,
        $"/api/local/profiles/{stopProfile.Id}/logs?limit=200");
    var retainedPublicLogs = await PublicLogs(directLogClient, directLogCredential, stopProfile.Id,
        "limit=200");
    var retainedFriendLogs = await OwnerGetJson<ServerLogResult>(stopFriendLocal,
        $"/api/local/friend/{stopProfile.Id}/logs?limit=200");
    var retainedPublicResult = retainedPublicLogs.Body.Deserialize<ServerLogResult>(webJson)
        ?? throw new Exception("retained Friend log response was empty");
    Require(retainedHostLogs.Ok && retainedHostLogs.SourceState == ServerLogSourceStates.Ended &&
        retainedHostLogs.Records.Count > 0 && retainedHostLogs.RunId is not null &&
        retainedPublicLogs.Status == HttpStatusCode.OK &&
        !retainedPublicResult.Ok && retainedPublicResult.Code == "FriendRetainedLogsUnavailable" &&
        retainedPublicResult.SourceState == ServerLogSourceStates.Ended &&
        retainedPublicResult.RunId is null && retainedPublicResult.Records.Count == 0 &&
        !retainedFriendLogs.Ok && retainedFriendLogs.Code == "FriendRetainedLogsUnavailable" &&
        retainedFriendLogs.RunId is null && retainedFriendLogs.Records.Count == 0,
        "Host retained-run access or active-exact-run-only Friend access was not enforced after restart");
    Console.WriteLine("PASS retained ended-run logs remain Host-only across restart"); passes++;

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
        unknownConflict.PortConflicts.Single().BlockReason?.Contains("time added by the Host", StringComparison.OrdinalIgnoreCase) == true,
        "a transient Unknown player count discarded the Host's saved added time");
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
        occupiedConflict.PortConflicts.Single().BlockReason?.Contains("time added by the Host", StringComparison.OrdinalIgnoreCase) == true,
        "an online player discarded the Host's saved added time");
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
    var stillProtectedConflict = await FriendAction(stopFriendLocal, replacementProfile.Id, "start");
    Require(!stillProtectedConflict.Ok && stillProtectedConflict.Code == "PortConflict" &&
        stillProtectedConflict.PortConflicts?.SingleOrDefault() is { CanReplace: false } &&
        stillProtectedConflict.PortConflicts.Single().BlockReason?.Contains("time added by the Host", StringComparison.OrdinalIgnoreCase) == true,
        "saved added time did not protect the next empty-server countdown");
    var resetTimerSettings = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Settings;
    resetTimerSettings.AutoShutdownEnabled = false;
    Require((await OwnerPut<HostSettings, ActionResult>(stopOwner, "/api/local/settings", resetTimerSettings)).Ok,
        "automatic shutdown could not be disabled to clear the saved extension for replacement coverage");
    var replaceableConflict = await FriendAction(stopFriendLocal, replacementProfile.Id, "start");
    Require(!replaceableConflict.Ok && replaceableConflict.Code == "PortConflict" &&
        replaceableConflict.PortConflicts?.SingleOrDefault() is { ProfileId: var conflictId, CanReplace: true } &&
        conflictId == stopProfile.Id,
        "an empty unextended conflicting server was not offered as an explicit replacement");
    var replaced = await FriendAction(stopFriendLocal, replacementProfile.Id, "replace");
    var replacedRun = (await stopOwner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot"))!.Runs
        .Single(run => run.ProfileId == replacementProfile.Id);
    Require(replaced.Ok && replaced.Code == "PortConflictReplaced" && File.Exists(stopMarker) &&
        replacedRun.ProcessId is not null && replacedRun.State is "Unknown" or "Starting" or "Ready",
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
    Console.WriteLine("PASS Host-added time survives Unknown/player transitions and blocks replacement until timer policy resets"); passes++;

    StopApp(host);
    host = null;
    var offlineForgotten = await OwnerPost<object, FriendActionResult>(aLocal,
        $"/api/local/friend/connections/{firstConnection.ConnectionId}/forget", new { });
    aView = await OwnerPost<object, FriendView>(aLocal, "/api/local/friend/poll", new { });
    Require(offlineForgotten.Ok && offlineForgotten.Code == "ConnectionForgottenLocally" &&
        offlineForgotten.Message.Contains("Ask the Host to remove this PC from Friend access", StringComparison.Ordinal) &&
        aView.Connections?.Count == 0,
        "offline Forget did not remove the saved local access with an explicit Host cleanup warning");
    Console.WriteLine("PASS offline Forget removes saved local access and warns about Host cleanup"); passes++;

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
                if (snapshot?.Runs.SingleOrDefault(run => run.ProfileId == profileId)?.State is "Ready" or "Starting" or "Process running" or "Unknown")
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
    var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
        .Select(endpoint => endpoint.Port).Concat(exclude).ToHashSet();
    for (var i = 0; i < 200; i++)
    {
        var port = Random.Shared.Next(51000, 60000);
        if (!active.Contains(port)) return port;
    }
    throw new Exception("No unused TCP port found in the Windows port table.");
}

static Process StartApp(string path, string mode, int port, string data, int stopDelayMs = 0,
    bool drainDiagnostics = true, int receiveDelayMs = 0)
{
    Directory.CreateDirectory(data);
    var info = new ProcessStartInfo(path)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    info.ArgumentList.Add(mode); info.ArgumentList.Add("--port"); info.ArgumentList.Add(port.ToString());
    info.Environment["TOGETHERSERVER_DATA_DIR"] = data;
    info.Environment["TOGETHERSERVER_FIXTURE_ROOT"] = data;
    info.Environment[GameServerRegistry.FixtureOptInEnvironmentVariable] = "1";
    if (stopDelayMs > 0) info.Environment["TOGETHERSERVER_FIXTURE_STOP_DELAY_MS"] = stopDelayMs.ToString();
    if (receiveDelayMs > 0) info.Environment["TOGETHERSERVER_FIXTURE_RECEIVE_DELAY_MS"] = receiveDelayMs.ToString();
    info.Environment["Logging__LogLevel__Default"] = "Warning";
    info.Environment["Logging__EventLog__LogLevel__Default"] = "None";
    var process = Process.Start(info) ?? throw new Exception("App did not start.");
    if (drainDiagnostics)
    {
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }
    return process;
}

static void DisconnectDiagnosticPipes(Process process)
{
    process.StandardOutput.Dispose();
    process.StandardError.Dispose();
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
    var responseBody = await response.Content.ReadAsStringAsync();
    if (string.IsNullOrWhiteSpace(responseBody))
        throw new Exception($"Empty local PUT {path}: {(int)response.StatusCode}");
    return JsonSerializer.Deserialize<TResponse>(responseBody, webJson) ??
        throw new Exception($"Invalid local PUT {path}: {(int)response.StatusCode}");
}

async Task<HttpResponseMessage> OwnerGet(HttpClient client, string path)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, path);
    request.Headers.Add("X-TogetherServer-Local", "1");
    return await client.SendAsync(request);
}

async Task<TResponse> OwnerGetJson<TResponse>(HttpClient client, string path)
{
    using var response = await OwnerGet(client, path);
    return await response.Content.ReadFromJsonAsync<TResponse>(webJson) ??
        throw new Exception($"Empty local GET {path}: {(int)response.StatusCode}");
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

async Task<(HttpStatusCode Status, JsonElement Body)> PublicLogs(HttpClient client,
    PairingCredential credential, Guid profileId, string? query = null, bool includeProtocol = true)
{
    var path = $"/api/companion/servers/{profileId}/logs" +
        (string.IsNullOrWhiteSpace(query) ? "" : "?" + query);
    using var request = new HttpRequestMessage(HttpMethod.Get, path);
    request.Headers.Add("X-Device-Id", credential.DeviceId.ToString());
    if (includeProtocol)
        request.Headers.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
    using var response = await client.SendAsync(request);
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return (response.StatusCode, document.RootElement.Clone());
}

static HttpClient PinnedClient(string endpoint, string fingerprint)
{
    var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
        cert is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), Convert.FromHexString(fingerprint))
    };
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
    request.Headers.Add(CompanionProtocol.HeaderName, CompanionProtocol.Current.ToString());
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

sealed class PendingResponseHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HttpResponseMessage> response =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => entered.Task;
    public void Release() => response.TrySetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        entered.TrySetResult();
        return await response.Task.WaitAsync(cancellationToken);
    }
}

sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    private DateTimeOffset utcNow = initialUtcNow;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void SetUtcNow(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Manual test time must be UTC.", nameof(value));
        utcNow = value;
    }
}
