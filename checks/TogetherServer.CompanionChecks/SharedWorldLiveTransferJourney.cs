using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

// The Host is stopped only to release its exclusive LocalData lock for the
// internal fixture adapter. No HTTP route can request a live capture.
internal static partial class SharedWorldJourney
{
    public static async Task RunLiveTransferAsync(string appPath, string fixturePath)
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "shared-live-transfer-journey",
            Guid.NewGuid().ToString("N")));
        var hostData = Path.Combine(root, "host");
        var friendData = Path.Combine(root, "friend");
        var ports = AvailablePorts(3);
        var hostPort = ports[0];
        var companionPort = ports[1];
        var friendPort = ports[2];
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            Name = "Disposable live transfer",
            WorldSource = "New",
            WorldId = "synthetic-live-transfer",
            GamePort = AvailableGamePort(),
            ExecutablePath = fixturePath,
            Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        profile.WorldDirectory = Path.Combine(hostData, "worlds", profile.Id.ToString("N"));
        Directory.CreateDirectory(profile.WorldDirectory);
        var settings = new HostSettings
        {
            Profiles = [profile],
            CompanionEndpoint = $"https://127.0.0.1:{companionPort}",
            CompanionPort = companionPort,
            CompanionBindAddress = "127.0.0.1"
        };
        Process? host = null;
        Process? friend = null;
        Process? fixture = null;
        ManagedRun? liveRun = null;
        var passed = 0;
        try
        {
            Require(new[] { GameKinds.Valheim, GameKinds.MinecraftJava,
                    GameKinds.MinecraftBedrock, GameKinds.Factorio, GameKinds.Terraria }
                .All(game => !SharedWorldLiveSaveAdapters.Status(game).Available),
                "a real game was marked accepted for live capture");
            host = StartApp(appPath, "--host", hostPort, hostData);
            friend = StartApp(appPath, "--friend", friendPort, friendData);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(friendPort));
            WindowsListenerOwners.RequireTogetherServerOwner(hostPort, host);
            WindowsListenerOwners.RequireTogetherServerOwner(friendPort, friend);
            using var owner = LocalClient(hostPort);
            using var friendLocal = LocalClient(friendPort);
            Require((await PutAsync<HostSettings, ActionResult>(owner,
                "/api/local/settings", settings)).Ok, "fixture Host settings failed");
            var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner,
                $"/api/local/servers/{profile.Id}/invite", new(false, true, true));
            Require(invite.GetProperty("ok").GetBoolean() &&
                invite.GetProperty("listenerActive").GetBoolean(),
                "fixture Host did not open its deliberate HTTPS listener");
            WindowsListenerOwners.RequireTogetherServerOwner(companionPort, host);
            var code = invite.GetProperty("password").GetString();
            Require(!string.IsNullOrWhiteSpace(code) &&
                (await PostAsync<FriendPairRequest, FriendActionResult>(friendLocal,
                    "/api/local/friend/pair", new(code!))).Ok,
                "fixture Friend did not pair");
            var deviceId = await OnlyDeviceAsync(owner, profile.Id);
            Require((await WaitConnectedAsync(friendLocal)).ConnectionId != Guid.Empty,
                "fixture Friend did not authenticate over pinned HTTPS");
            Require((await PutAsync<SharedWorldConsentRequest, SharedWorldResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world", new(true))).Ok,
                "owner could not enable fixture sharing");
            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceId}/shared-world/{profile.Id}", new(true))).Ok,
                "owner could not grant fixture Receive");
            Require((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(friendLocal,
                $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok,
                "fixture Friend could not consent");
            var payload = new byte[4 * SharedWorldService.ChunkBytes + 37];
            RandomNumberGenerator.Fill(payload);
            File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), payload);
            StopApp(friend);
            friend = null;
            StopApp(host);
            host = null;

            SharedWorldVersion version;
            using (var data = new LocalData(hostData, companionPort))
            {
                var savedProfile = data.LoadSettings().Profiles.Single(item => item.Id == profile.Id);
                // This disposable fixture has no readiness signal. The check
                // records synthetic completion for its exact test process only.
                var stopPipe = "TogetherServer.LiveTransferFixture." + Guid.NewGuid().ToString("N");
                fixture = Process.Start(new ProcessStartInfo(fixturePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(fixturePath)!,
                    ArgumentList = { "--stop-pipe", stopPipe }
                }) ?? throw new Exception("synthetic fixture did not start");
                liveRun = new ManagedRun
                {
                    ProfileId = profile.Id,
                    OperationId = Guid.NewGuid(),
                    Kind = profile.Kind,
                    WorldId = profile.WorldId,
                    WorldDirectory = profile.WorldDirectory,
                    GamePort = profile.GamePort,
                    ExecutablePath = fixturePath,
                    StopPipeName = stopPipe,
                    ProcessId = fixture.Id,
                    StartTimeUtcTicks = fixture.StartTime.ToUniversalTime().Ticks,
                    WasReady = true
                };
                data.SaveRuns([liveRun]);
                Require(liveRun.StopRequestedUtc is null &&
                    ExactFixtureProcess(liveRun, fixturePath),
                    "the synthetic recorded run did not match its exact fixture process");
                var games = new GameServerRegistry(data, includeFixture: true);
                var backups = new WorldBackupService(data, TimeProvider.System, games: games);
                var shared = new SharedWorldService(data, backups)
                {
                    LiveGameRegistryForChecks = games,
                    FixtureLiveCaptureAcceptedForChecks = true
                };
                var completion = new LiveSaveCompletionEvidence(profile.Id,
                    liveRun.OperationId, liveRun.ProcessId!.Value,
                    liveRun.StartTimeUtcTicks!.Value, LiveSaveEvidence.RunScopedCompletion,
                    DateTimeOffset.UtcNow, true);
                var captureId = shared.StageLiveCapture(savedProfile, liveRun, completion);
                var published = shared.PublishLiveCapture(savedProfile, captureId,
                    new(captureId, liveRun.OperationId, true));
                Require(published.Ok && published.Version is
                {
                    Schema: 5,
                    CaptureKind: SharedWorldCaptureKinds.LiveSave, Number: 1
                } &&
                    SharedWorldService.VerifySignature(published.Version),
                    $"fixture LiveSave was not signed and published: {published.Code} {published.Message}");
                version = published.Version!;
                Require(version.BackupId == captureId && version.Files.Count == 1 &&
                    version.Files[0].Sha256 == Convert.ToHexString(SHA256.HashData(payload)) &&
                    shared.ReadChunk(version, 0, 0).AsSpan().SequenceEqual(
                        payload.AsSpan(0, SharedWorldService.ChunkBytes)) &&
                    shared.Status(savedProfile).Latest?.VersionHash == version.VersionHash,
                    "the published schema-5 payload or signed Host pointer changed");
            }
            Console.WriteLine("PASS exact fixture run stages and publishes a signed schema-5 LiveSave; real game acceptance stays false");
            passed++;

            host = StartApp(appPath, "--host", hostPort, hostData);
            await WaitLocalAsync(hostPort);
            WindowsListenerOwners.RequireTogetherServerOwner(hostPort, host);
            WindowsListenerOwners.RequireTogetherServerOwner(companionPort, host);
            var served = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world");
            Require(served.Latest?.VersionHash == version.VersionHash &&
                served.Latest.Schema == 5 && served.LiveSave?.Available == false,
                "restarted packaged Host did not serve the signed fixture version");
            friend = StartApp(appPath, "--friend", friendPort, friendData,
                receiveDelayMs: 3000);
            await WaitLocalAsync(friendPort);
            WindowsListenerOwners.RequireTogetherServerOwner(friendPort, friend);
            await WaitConnectedAsync(friendLocal);
            var receivedRoot = ReceiverRoot(friendData, deviceId, profile.Id);
            var partial = Path.Combine(receivedRoot, ".partial-" + version.VersionHash,
                "payload", "world.dat");
            await WaitForAsync(() => File.Exists(partial) &&
                new FileInfo(partial).Length >= SharedWorldService.ChunkBytes,
                "Friend did not persist a complete HTTPS chunk", 15000);
            StopApp(friend);
            friend = null;
            var pendingBytes = new FileInfo(partial).Length;
            Require(pendingBytes >= SharedWorldService.ChunkBytes &&
                pendingBytes < payload.Length &&
                FriendLink.ResumeOffset(partial, version.Files[0]) == pendingBytes &&
                !File.Exists(Path.Combine(receivedRoot, "latest.json")),
                "interruption did not leave a resumable pending chunk without a receipt");
            Console.WriteLine("PASS packaged Friend persists a bounded pending chunk before interruption");
            passed++;

            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceId}/shared-world/{profile.Id}", new(false))).Ok,
                "owner could not revoke Receive during the pending transfer");
            friend = StartApp(appPath, "--friend", friendPort, friendData);
            await WaitLocalAsync(friendPort);
            await WaitConnectedAsync(friendLocal);
            var denied = await PostAsync<object, ReceivedSharedWorldResult>(friendLocal,
                $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
            Require(!denied.Ok && !File.Exists(Path.Combine(receivedRoot, "latest.json")) &&
                new FileInfo(partial).Length == pendingBytes &&
                (await GetAsync<SharedWorldStatus>(owner,
                    $"/api/local/profiles/{profile.Id}/shared-world")).ConfirmedCopies == 0,
                "revoked Receive completed or discarded the pending fixture copy");
            StopApp(friend);
            friend = null;
            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceId}/shared-world/{profile.Id}", new(true))).Ok,
                "owner could not restore fixture Receive");
            Console.WriteLine("PASS revoked Receive denies the pending transfer and leaves its resumable bytes intact");
            passed++;

            friend = StartApp(appPath, "--friend", friendPort, friendData,
                beforeChunkDelayMs: 2000);
            await WaitLocalAsync(friendPort);
            WindowsListenerOwners.RequireTogetherServerOwner(friendPort, friend);
            await WaitConnectedAsync(friendLocal);
            await WaitSharedStateAsync(friendLocal, profile.Id, "Receiving", pendingBytes);
            Require(File.Exists(partial) && new FileInfo(partial).Length >= pendingBytes,
                "resumed transfer restarted from an empty pending file");
            await WaitVersionAsync(friendLocal, profile.Id, version.Number);
            await WaitCopiesAsync(owner, profile.Id, 1);
            var received = Path.Combine(receivedRoot, version.VersionHash, "payload", "world.dat");
            var receiptPath = Path.Combine(receivedRoot, version.VersionHash, "receipt.json");
            var receivedManifest = JsonSerializer.Deserialize<SharedWorldVersion>(
                File.ReadAllBytes(Path.Combine(receivedRoot, "latest.json")), Json);
            var receipt = JsonSerializer.Deserialize<SharedWorldReceipt>(
                File.ReadAllBytes(receiptPath), Json);
            Require(receivedManifest?.VersionHash == version.VersionHash &&
                SharedWorldService.VerifySignature(receivedManifest) &&
                SHA256.HashData(File.ReadAllBytes(received)).AsSpan().SequenceEqual(
                    SHA256.HashData(payload)) &&
                receipt is { DeviceId: var id } && id == deviceId &&
                receipt.VersionHash == version.VersionHash,
                "resumed Friend copy, signed manifest, payload hash, or receipt is invalid");
            Console.WriteLine("PASS actual pending state resumes through pinned authenticated HTTPS; signed receipt confirms the hash-verified copy on Host");
            passed++;

            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceId}/shared-world/{profile.Id}", new(false))).Ok,
                "owner could not revoke the confirmed copy");
            await WaitCopiesAsync(owner, profile.Id, 0);
            Require(File.Exists(received), "revocation removed a previously verified local copy");
            using (var stream = new FileStream(received, FileMode.Open, FileAccess.Write))
                stream.WriteByte((byte)(payload[0] ^ 0xff));
            var tampered = await GetAsync<ReceivedSharedWorldStatus>(friendLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(tampered.State == "Error" && tampered.ThisPcVersion is null,
                "a tampered received LiveSave remained verified");
            StopApp(host);
            host = null;
            using (var data = new LocalData(hostData, companionPort))
            {
                var shared = new SharedWorldService(data,
                    new WorldBackupService(data, TimeProvider.System,
                        games: new GameServerRegistry(data, includeFixture: true)));
                var roster = shared.ReadRoster(data.LoadSettings().Profiles.Single(item => item.Id == profile.Id));
                var member = roster?.Members.SingleOrDefault(item => item.DeviceId == deviceId);
                Require(member is { Grants.Receive: false } && receipt is not null &&
                    SharedWorldReceiptTrust.Verify(receipt, member.PublicKey) &&
                    receipt.RosterRevision < roster!.Revision,
                    "the Friend receipt was not signed by its enrolled device key");
            }
            Console.WriteLine("PASS revocation removes Host confirmation, and tampering invalidates the Friend copy while its receipt remains signed");
            passed++;
            Console.WriteLine($"Shared LiveSave transfer journey: {passed} groups passed, 0 failed. Disposable data: {root}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL Shared LiveSave transfer journey after {passed} passing groups: {ex}");
            throw;
        }
        finally
        {
            if (host is { HasExited: false } && liveRun is not null)
            {
                try
                {
                    using var owner = LocalClient(hostPort);
                    _ = await PostAsync<object, ActionResult>(owner,
                        $"/api/local/profiles/{profile.Id}/stop", new { });
                }
                catch (Exception ex)
                { Console.Error.WriteLine("Disposable fixture Stop: " + ex.Message); }
            }
            StopApp(friend);
            StopApp(host);
            if (liveRun is not null) await StopExactFixtureAsync(liveRun, fixturePath);
            fixture?.Dispose();
        }
    }

    private static bool ExactFixtureProcess(ManagedRun run, string fixturePath)
    {
        if (run.ProcessId is not > 0 || run.StartTimeUtcTicks is not > 0 ||
            !Path.GetFullPath(run.ExecutablePath).Equals(Path.GetFullPath(fixturePath),
                StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var process = Process.GetProcessById(run.ProcessId.Value);
            return !process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                Path.GetFullPath(process.MainModule!.FileName).Equals(Path.GetFullPath(fixturePath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static async Task StopExactFixtureAsync(ManagedRun run, string fixturePath)
    {
        if (!ExactFixtureProcess(run, fixturePath) ||
            string.IsNullOrWhiteSpace(run.StopPipeName)) return;
        try
        {
            using var process = Process.GetProcessById(run.ProcessId!.Value);
            using var pipe = new NamedPipeClientStream(".", run.StopPipeName,
                PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await pipe.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync("stop");
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine("Disposable exact fixture cleanup: " + ex.Message); }
    }
}
