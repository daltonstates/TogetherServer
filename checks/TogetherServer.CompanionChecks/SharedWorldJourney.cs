using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using TogetherServer;

// Three isolated copies of the packaged app exercise the public companion route.
// The harness never starts a listener; only TogetherServer.exe owns HTTP ports.
internal static class SharedWorldJourney
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string appPath, string valheimFixturePath)
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "shared-world-journey",
            Guid.NewGuid().ToString("N")));
        var hostData = Path.Combine(root, "host");
        var friendAData = Path.Combine(root, "friend-a");
        var friendBData = Path.Combine(root, "friend-b");
        var ports = AvailablePorts(3);
        var hostPort = ports[0];
        var companionPort = ports[1];
        var friendAPort = ports[2];
        var friendBPort = AvailablePorts(1, ports)[0];
        var gamePort = AvailableGamePort();
        var world = Path.Combine(hostData, "worlds", "disposable-world");
        Directory.CreateDirectory(world);
        var profile = new ServerProfile
        {
            Kind = GameKinds.Valheim,
            Name = "Disposable shared world",
            ServerName = "Fixture \"Valheim\"",
            WorldSource = "New",
            WorldId = "fixture-world",
            WorldDirectory = world,
            GamePort = gamePort,
            ExecutablePath = valheimFixturePath,
            Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        var settings = new HostSettings
        {
            MaxConcurrentServers = 1,
            Profiles = [profile],
            CompanionEndpoint = $"https://127.0.0.1:{companionPort}",
            CompanionPort = companionPort,
            CompanionBindAddress = "127.0.0.1"
        };
        Process? host = null;
        Process? friendA = null;
        Process? friendB = null;
        var passed = 0;
        try
        {
            host = StartApp(appPath, "--host", hostPort, hostData);
            friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
            friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(friendAPort),
                WaitLocalAsync(friendBPort));
            using var owner = LocalClient(hostPort);
            using var aLocal = LocalClient(friendAPort);
            using var bLocal = LocalClient(friendBPort);
            Require((await PutAsync<HostSettings, ActionResult>(owner,
                "/api/local/settings", settings)).Ok, "disposable Host settings failed");
            Require((await PostAsync<ValheimPasswordRequest, ActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/password", new("fixture-pass-123"))).Ok,
                "disposable game password failed");
            var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner,
                $"/api/local/servers/{profile.Id}/invite",
                new(false, true, true, DeviceLimit: 2));
            Require(invite.GetProperty("ok").GetBoolean() &&
                invite.GetProperty("listenerActive").GetBoolean(),
                "packaged Host did not open its deliberate HTTPS listener");
            var code = invite.GetProperty("password").GetString();
            Require(!string.IsNullOrWhiteSpace(code), "invite has no code");
            Require((await PostAsync<FriendPairRequest, FriendActionResult>(aLocal,
                "/api/local/friend/pair", new(code!))).Ok, "Friend A did not pair");
            var deviceA = await OnlyDeviceAsync(owner, profile.Id);
            Require((await PostAsync<FriendPairRequest, FriendActionResult>(bLocal,
                "/api/local/friend/pair", new(code!))).Ok, "Friend B did not pair");
            var devices = await DeviceIdsAsync(owner, profile.Id);
            Require(devices.Count == 2 && devices.Contains(deviceA),
                "two Friends did not receive separate device identities");
            var deviceB = devices.Single(id => id != deviceA);
            Require((await PollAsync(aLocal)).State == "Connected" &&
                (await PollAsync(bLocal)).State == "Connected",
                "both packaged Friends did not authenticate through pinned HTTPS");
            Require((await PutAsync<SharedWorldConsentRequest, SharedWorldResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world", new(true))).Ok,
                "owner could not enable sharing");
            foreach (var id in devices)
                Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                    $"/api/local/devices/{id}/shared-world/{profile.Id}", new(true))).Ok,
                    "owner could not grant Receive separately to both PCs");
            foreach (var client in new[] { aLocal, bLocal })
                Require((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(client,
                    $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok,
                    "a Friend PC could not consent to receiving");
            Require(!(await PostAsync<object, ReceivedSharedWorldResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/pull", new { })).Ok,
                "a save was received before a confirmed graceful Stop");
            var beforeSave = await GetAsync<ReceivedSharedWorldStatus>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(beforeSave.State == "Transfer unavailable" && beforeSave.Error is not null,
                "a missing Host save was misreported as a stalled transfer");

            File.WriteAllText(Path.Combine(world, "world.dat"), "first verified fixture change");
            await StartAndStopAsync(owner, profile.Id);
            var first = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world");
            Require(first.Latest is { Number: 1 }, "the first signed save was not published");
            var firstVersion = first.Latest!;
            await PollAsync(aLocal);
            await PollAsync(bLocal);
            await WaitVersionAsync(aLocal, profile.Id, 1);
            await WaitVersionAsync(bLocal, profile.Id, 1);
            await WaitCopiesAsync(owner, profile.Id, 2);
            var verifiedStatus = await GetAsync<ReceivedSharedWorldStatus>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(verifiedStatus.ThisPcVersion == 1 && verifiedStatus.Error is null &&
                verifiedStatus.State != "Stalled" && verifiedStatus.State != "Low space",
                "verified catch-up left a stale transfer alert");
            var aRoot = ReceiverRoot(friendAData, deviceA, profile.Id);
            var bRoot = ReceiverRoot(friendBData, deviceB, profile.Id);
            Require(File.ReadAllText(Path.Combine(aRoot, firstVersion.VersionHash,
                        "payload", "world.dat")) == "first verified fixture change" &&
                    File.ReadAllText(Path.Combine(bRoot, firstVersion.VersionHash,
                        "payload", "world.dat")) == "first verified fixture change",
                "the two received payloads did not match the completed save");
            Console.WriteLine("PASS two packaged Friend PCs independently receive and attest one signed save");
            passed++;

            StopApp(friendA);
            friendA = null;
            StopApp(friendB);
            friendB = null;
            using (var stream = new FileStream(Path.Combine(world, "world.dat"),
                       FileMode.Create, FileAccess.Write))
                stream.SetLength(4L * SharedWorldService.ChunkBytes);
            await StartAndStopAsync(owner, profile.Id);
            var second = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world");
            Require(second.Latest is { Number: 2 }, "second signed save was not published");
            var secondVersion = second.Latest!;
            Require(File.Exists(Path.Combine(bRoot, firstVersion.VersionHash,
                        "payload", "world.dat")) &&
                    !File.Exists(Path.Combine(bRoot, secondVersion.VersionHash,
                        "payload", "world.dat")),
                "offline Friend B did not remain one save behind");
            Console.WriteLine("PASS an offline PC keeps its verified earlier copy while the Host advances");
            passed++;

            friendA = StartApp(appPath, "--friend", friendAPort, friendAData,
                receiveDelayMs: 3000);
            await WaitLocalAsync(friendAPort);
            await PollAsync(aLocal);
            var partial = Path.Combine(aRoot, ".partial-" + secondVersion.VersionHash,
                "payload", "world.dat");
            await WaitForAsync(() => File.Exists(partial) &&
                new FileInfo(partial).Length >= SharedWorldService.ChunkBytes,
                "Friend A did not retain one bounded chunk before interruption", 15000);
            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceA}/shared-world/{profile.Id}", new(false))).Ok,
                "owner could not revoke Receive while a chunk was pending");
            await Task.Delay(3500);
            var revoked = await GetAsync<ReceivedSharedWorldStatus>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(revoked.ThisPcVersion == 1 && File.Exists(partial),
                "revocation committed the pending save or removed resumable data");
            StopApp(friendA);
            friendA = null;
            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceA}/shared-world/{profile.Id}", new(true))).Ok,
                "owner could not deliberately restore Receive");
            friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
            await WaitLocalAsync(friendAPort);
            await PollAsync(aLocal);
            await WaitVersionAsync(aLocal, profile.Id, 2);
            Require(File.Exists(Path.Combine(aRoot, secondVersion.VersionHash,
                        "payload", "world.dat")),
                "interrupted transfer did not resume into a verified version");
            Console.WriteLine("PASS interrupted chunks survive PC restart; revocation prevents commit until regrant");
            passed++;

            friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
            await WaitLocalAsync(friendBPort);
            await PollAsync(bLocal);
            await WaitVersionAsync(bLocal, profile.Id, 2);
            await WaitCopiesAsync(owner, profile.Id, 2);
            Require(File.Exists(Path.Combine(bRoot, firstVersion.VersionHash,
                        "payload", "world.dat")),
                "catch-up deleted the earlier verified copy");
            Console.WriteLine("PASS offline Friend B catches up and retains its earlier verified version");
            passed++;

            var tamperedFile = Path.Combine(aRoot, secondVersion.VersionHash,
                "payload", "world.dat");
            using (var stream = new FileStream(tamperedFile, FileMode.Open, FileAccess.Write))
                stream.WriteByte(42);
            var tamperedStatus = await GetAsync<ReceivedSharedWorldStatus>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            var tamperedPull = await PostAsync<object, ReceivedSharedWorldResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
            var healthyB = await GetAsync<ReceivedSharedWorldStatus>(bLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(tamperedStatus.State == "Error" && tamperedStatus.ThisPcVersion is null &&
                !tamperedPull.Ok && healthyB.ThisPcVersion == 2 &&
                File.Exists(Path.Combine(aRoot, firstVersion.VersionHash,
                    "payload", "world.dat")),
                "tampered copy was marked ready, or the independent verified copy was lost");
            Console.WriteLine("PASS payload tampering fails verification without erasing other PCs' copies");
            passed++;

            // The packaged route reads the real disk. Filling a machine to test its
            // reserve is unsafe, so the deterministic boundary is exercised here.
            const long reserve = 1024L * 1024 * 1024;
            Require(!FriendLink.HasReceiverReserve(reserve, 1) &&
                !FriendLink.HasReceiverReserve(reserve - 1, 0) &&
                FriendLink.HasReceiverReserve(reserve + 1, 1),
                "1 GiB receiving reserve boundary changed");
            Console.WriteLine("PASS 1 GiB disk reserve boundary (policy check; no disk was filled)");
            passed++;

            StopApp(host);
            host = null;
            var disconnected = await PollAsync(bLocal);
            Require(disconnected.State == "Disconnected/Unknown" &&
                (await GetAsync<ReceivedSharedWorldStatus>(bLocal,
                    $"/api/local/friend/{profile.Id}/shared-world")).ThisPcVersion == 2,
                "Host loss erased the Friend's verified copy or was reported as connected");
            host = StartApp(appPath, "--host", hostPort, hostData);
            await WaitLocalAsync(hostPort);
            var returned = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world");
            Require(returned.Latest?.VersionHash == secondVersion.VersionHash &&
                (await WaitConnectedAsync(bLocal)).State == "Connected",
                "ordinary Host restart did not retain signed history and reauthenticate Friend B");
            Console.WriteLine("PASS ordinary Host restart keeps signed history and saved Friend access");
            passed++;

            Console.WriteLine("SKIP packaged competing-history resolution, owner override, and old-Host-after-takeover return: a signed takeover and resolution journey is pending integration. Focused authority fixtures run in TogetherServer.Checks.");
            Console.WriteLine($"Shared Worlds packaged journey: {passed} groups passed, 0 failed, 3 recovery scenarios skipped. Disposable data: {root}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL Shared Worlds packaged journey after {passed} passing groups: {ex}");
            throw;
        }
        finally
        {
            if (host is { HasExited: false })
            {
                try
                {
                    using var owner = LocalClient(hostPort);
                    var snapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
                    if (snapshot?.Runs.SingleOrDefault(run => run.ProfileId == profile.Id)?.State != "Offline")
                        _ = await PostAsync<object, ActionResult>(owner,
                            $"/api/local/profiles/{profile.Id}/stop", new { });
                }
                catch (Exception cleanupError)
                { Console.Error.WriteLine("Disposable run cleanup: " + cleanupError.Message); }
            }
            StopApp(friendB);
            StopApp(friendA);
            StopApp(host);
        }
    }

    private static string ReceiverRoot(string data, Guid device, Guid profile) =>
        Path.Combine(data, "received-shared-worlds", device.ToString("N"), profile.ToString("N"));

    private static async Task StartAndStopAsync(HttpClient owner, Guid profileId)
    {
        var start = await PostAsync<object, ActionResult>(owner,
            $"/api/local/profiles/{profileId}/start", new { });
        Require(start.Ok, $"fixture Start failed: {start.Code} {start.Message}");
        var ready = false;
        for (var attempt = 0; attempt < 100 && !ready; attempt++)
        {
            var snapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
            ready = snapshot?.Runs.Single(run => run.ProfileId == profileId).State == "Ready";
            if (!ready) await Task.Delay(100);
        }
        Require(ready, "fixture did not reach Ready before Stop");
        var stop = await PostAsync<object, ActionResult>(owner,
            $"/api/local/profiles/{profileId}/stop", new { });
        Require(stop.Ok, $"fixture graceful Stop failed: {stop.Code} {stop.Message}");
    }

    private static async Task<Guid> OnlyDeviceAsync(HttpClient owner, Guid profileId)
    {
        var ids = await DeviceIdsAsync(owner, profileId);
        Require(ids.Count == 1, "first pairing did not issue exactly one device identity");
        return ids.Single();
    }

    private static async Task<IReadOnlyList<Guid>> DeviceIdsAsync(HttpClient owner, Guid profileId)
    {
        var companion = await GetAsync<JsonElement>(owner, "/api/local/companion");
        return companion.GetProperty("devices").EnumerateArray()
            .Where(device => device.GetProperty("profileId").GetGuid() == profileId)
            .Select(device => device.GetProperty("id").GetGuid()).ToArray();
    }

    private static async Task<FriendView> PollAsync(HttpClient friend) =>
        await PostAsync<object, FriendView>(friend, "/api/local/friend/poll", new { });

    private static async Task<FriendView> WaitConnectedAsync(HttpClient friend)
    {
        FriendView? latest = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            latest = await PollAsync(friend);
            if (latest.State == "Connected") return latest;
            await Task.Delay(250);
        }
        throw new Exception($"Friend did not reconnect: {latest?.State} {latest?.ConnectionCode}");
    }

    private static async Task WaitVersionAsync(HttpClient friend, Guid profileId, long expected)
    {
        ReceivedSharedWorldStatus? latest = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            latest = await GetAsync<ReceivedSharedWorldStatus>(friend,
                $"/api/local/friend/{profileId}/shared-world");
            if (latest.ThisPcVersion == expected) return;
            if (attempt % 10 == 9) await PollAsync(friend);
            await Task.Delay(250);
        }
        throw new Exception($"Friend did not verify version {expected}: {latest?.State} {latest?.Error}");
    }

    private static async Task WaitCopiesAsync(HttpClient owner, Guid profileId, int expected)
    {
        SharedWorldStatus? latest = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            latest = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profileId}/shared-world");
            if (latest.ConfirmedCopies == expected) return;
            await Task.Delay(250);
        }
        throw new Exception($"Host counted {latest?.ConfirmedCopies} of {expected} verified PCs");
    }

    private static async Task WaitForAsync(Func<bool> condition, string error, int timeoutMs)
    {
        var until = Stopwatch.StartNew();
        while (until.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new Exception(error);
    }

    private static Process StartApp(string path, string mode, int port, string data,
        int receiveDelayMs = 0)
    {
        Directory.CreateDirectory(data);
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(port.ToString());
        info.Environment["TOGETHERSERVER_DATA_DIR"] = data;
        info.Environment["TOGETHERSERVER_FIXTURE_ROOT"] = data;
        info.Environment[GameServerRegistry.FixtureOptInEnvironmentVariable] = "1";
        info.Environment["Logging__LogLevel__Default"] = "Warning";
        if (receiveDelayMs > 0)
            info.Environment["TOGETHERSERVER_FIXTURE_RECEIVE_DELAY_MS"] = receiveDelayMs.ToString();
        var process = Process.Start(info) ?? throw new Exception("packaged app did not start");
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                Console.Error.WriteLine($"Disposable {mode} TCP {port}: {eventArgs.Data}");
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                Console.Error.WriteLine($"Disposable {mode} TCP {port}: {eventArgs.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void StopApp(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private static int[] AvailablePorts(int count, params int[] excluded)
    {
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Select(endpoint => endpoint.Port).Concat(excluded).ToHashSet();
        var result = new List<int>();
        for (var attempt = 0; attempt < 1000 && result.Count < count; attempt++)
        {
            var port = Random.Shared.Next(51000, 60000);
            if (active.Add(port)) result.Add(port);
        }
        if (result.Count != count) throw new Exception("no disposable app ports available");
        return [.. result];
    }

    private static int AvailableGamePort()
    {
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
            .Select(endpoint => endpoint.Port).ToHashSet();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var port = Random.Shared.Next(36000, 49000);
            if (!active.Contains(port) && !active.Contains(port + 1)) return port;
        }
        throw new Exception("no disposable Valheim game/query port pair available");
    }

    private static async Task WaitLocalAsync(int port)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromMilliseconds(300)
        };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var response = await client.GetAsync("/api/local/snapshot");
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(100);
        }
        throw new Exception($"disposable app on TCP {port} did not start");
    }

    private static HttpClient LocalClient(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<T>(Json) ??
            throw new Exception($"empty local GET {path}: {(int)response.StatusCode}");
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient client,
        string path, TRequest body) => await MutateAsync<TRequest, TResponse>(client,
        HttpMethod.Post, path, body);

    private static async Task<TResponse> PutAsync<TRequest, TResponse>(HttpClient client,
        string path, TRequest body) => await MutateAsync<TRequest, TResponse>(client,
        HttpMethod.Put, path, body);

    private static async Task<TResponse> MutateAsync<TRequest, TResponse>(HttpClient client,
        HttpMethod method, string path, TRequest body)
    {
        using var request = new HttpRequestMessage(method, path)
        { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("Origin", client.BaseAddress!.ToString().TrimEnd('/'));
        request.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<TResponse>(Json) ??
            throw new Exception($"empty local {method} {path}: {(int)response.StatusCode}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
