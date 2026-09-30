using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using TogetherServer;

internal static class CoreRemoteJourney
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string appPath, string valheimFixturePath)
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "core-remote-journey",
            Guid.NewGuid().ToString("N")));
        var hostData = Path.Combine(root, "host");
        var friendData = Path.Combine(root, "friend");
        var hostPort = FreeTcpPort();
        var companionPort = FreeTcpPort(hostPort);
        var friendPort = FreeTcpPort(hostPort, companionPort);
        var gamePort = FreeUdpPair();
        var endpoint = $"https://127.0.0.1:{companionPort}";
        var profile = new ServerProfile
        {
            Kind = GameKinds.Valheim,
            Name = "Core remote journey",
            ServerName = "Fixture \"Valheim\"",
            WorldSource = "New",
            WorldId = "fixture-world",
            WorldDirectory = Path.Combine(hostData, "worlds", Guid.NewGuid().ToString("N")),
            GamePort = gamePort,
            ExecutablePath = valheimFixturePath
        };
        Directory.CreateDirectory(profile.WorldDirectory);

        Process? host = null;
        Process? friend = null;
        var passes = 0;
        try
        {
            host = StartApp(appPath, "--host", hostPort, hostData);
            friend = StartApp(appPath, "--friend", friendPort, friendData);
            await WaitLocalAsync(hostPort);
            await WaitLocalAsync(friendPort);
            using var owner = LocalClient(hostPort);
            using var friendLocal = LocalClient(friendPort);

            var settings = new HostSettings
            {
                MaxConcurrentServers = 1,
                Profiles = [profile],
                CompanionEndpoint = endpoint,
                CompanionPort = companionPort,
                CompanionBindAddress = "127.0.0.1"
            };
            Require((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok,
                "Host settings could not be saved");
            Require((await PostAsync<ValheimPasswordRequest, ActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/password", new("fixture-pass-123"))).Ok,
                "fixture password could not be saved");

            var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner,
                $"/api/local/servers/{profile.Id}/invite", new(false, true, true));
            var password = invite.GetProperty("password").GetString();
            Require(invite.GetProperty("ok").GetBoolean() &&
                invite.GetProperty("listenerActive").GetBoolean() &&
                !string.IsNullOrWhiteSpace(password),
                "Host did not create a usable server code and HTTPS listener");

            var paired = await PostAsync<FriendPairRequest, FriendActionResult>(friendLocal,
                "/api/local/friend/pair", new(password!));
            Require(paired.Ok, $"Friend could not pair: {paired.Code} {paired.Message}");
            var connected = await PostAsync<object, FriendView>(friendLocal, "/api/local/friend/poll", new { });
            Require(connected.State == "Connected" && connected.LastConnectedUtc is not null &&
                connected.ConnectionCode is null && connected.Profiles.Single().Id == profile.Id,
                $"authenticated connection was not verified: {connected.State} {connected.ConnectionCode} {connected.Detail}");
            var ports = await owner.GetFromJsonAsync<PortDiagnosticsView>("/api/local/network/ports");
            Require(ports?.Control.State == "Open on PC" &&
                ports.Control.RemoteState == "Friend connected",
                "Host did not distinguish its open listener from an authenticated Friend heartbeat");
            Console.WriteLine("PASS authenticated Friend-to-Host connection is verified beyond an open TCP port");
            passes++;

            var companion = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
            var deviceId = companion.GetProperty("devices").EnumerateArray().Single().GetProperty("id").GetGuid();
            Require((await PutAsync<DevicePermissionRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceId}/permissions", new(true, true))).Ok,
                "Host could not grant core Start and Stop permissions");
            connected = await PostAsync<object, FriendView>(friendLocal, "/api/local/friend/poll", new { });
            Require(connected.CanStart && connected.CanStop && connected.Profiles.Single().CanStart &&
                connected.Profiles.Single().CanStop,
                "Friend did not receive the granted core permissions");

            var started = await FriendActionAsync(friendLocal, profile.Id, "start");
            Require(started.Ok && started.Code == "ValheimStarting",
                $"remote Start failed: {started.Code} {started.Message}");
            var ready = await WaitForRunStateAsync(owner, profile.Id, "Ready");
            Require(ready.ProcessId is not null && ready.OnlinePlayers == 0,
                "remote Start did not reach Ready with the disposable server reporting zero players");
            connected = await PostAsync<object, FriendView>(friendLocal, "/api/local/friend/poll", new { });
            Require(connected.State == "Connected" && connected.Profiles.Single().State == "Ready",
                "Friend did not receive the Ready state after remote Start");
            Console.WriteLine("PASS paired Friend remotely starts exactly one server and receives Ready status");
            passes++;

            var playerCountPath = Path.Combine(profile.WorldDirectory, "synthetic-online-players.txt");
            File.WriteAllText(playerCountPath, "1");
            var occupied = await FriendActionAsync(friendLocal, profile.Id, "refresh");
            Require(occupied.Ok && occupied.Status?.Profiles.Single().OnlinePlayers == 1,
                "Friend could not refresh the authoritative occupied-player state");
            var deniedStop = await FriendActionAsync(friendLocal, profile.Id, "stop");
            Require(!deniedStop.Ok && deniedStop.Code == "PlayersOnline",
                $"remote Stop was not denied while occupied: {deniedStop.Code} {deniedStop.Message}");
            Console.WriteLine("PASS authoritative occupied status blocks remote Stop");
            passes++;

            File.WriteAllText(playerCountPath, "0");
            var empty = await FriendActionAsync(friendLocal, profile.Id, "refresh");
            Require(empty.Ok && empty.Status?.Profiles.Single().OnlinePlayers == 0 &&
                empty.Status.Profiles.Single().CanStopNow,
                "Friend could not verify the authoritative empty-player state");
            var stopped = await FriendActionAsync(friendLocal, profile.Id, "stop");
            Require(stopped.Ok && stopped.Code == "ValheimStopped",
                $"remote Stop failed: {stopped.Code} {stopped.Message}");
            _ = await WaitForRunStateAsync(owner, profile.Id, "Offline");
            Require(File.ReadAllText(Path.Combine(profile.WorldDirectory, "synthetic-stop.marker")) == "Ctrl+C received",
                "remote Stop did not gracefully stop the exact disposable process");
            Console.WriteLine("PASS verified zero-player state permits graceful remote Stop of the exact process");
            passes++;

            var beforeDisconnect = await PostAsync<object, FriendView>(friendLocal,
                "/api/local/friend/poll", new { });
            Require(beforeDisconnect.State == "Connected" && beforeDisconnect.LastConnectedUtc is not null,
                "connection was not authenticated immediately before the closed-port check");
            StopApp(host);
            host = null;
            var closed = await PostAsync<object, FriendView>(friendLocal, "/api/local/friend/poll", new { });
            Require(closed.State == "Disconnected/Unknown" && closed.ConnectionCode == "HostPortClosed" &&
                closed.LastConnectedUtc == beforeDisconnect.LastConnectedUtc,
                $"closed Host port was not reported explicitly: {closed.State} {closed.ConnectionCode} {closed.Detail}");
            Console.WriteLine("PASS closed Host listener is detected without erasing the last authenticated connection");
            passes++;

            StopApp(friend);
            friend = null;
            host = StartApp(appPath, "--host", hostPort, hostData);
            friend = StartApp(appPath, "--friend", friendPort, friendData);
            await WaitLocalAsync(hostPort);
            await WaitLocalAsync(friendPort);
            var reconnected = await WaitForConnectedAsync(friendLocal);
            Require(reconnected.LastConnectedUtc is not null && reconnected.Profiles.Single().Id == profile.Id &&
                reconnected.CanStart && reconnected.CanStop,
                "saved pin, credential, assignment, or permissions did not reconnect after both apps restarted");

            var restarted = await FriendActionAsync(friendLocal, profile.Id, "start");
            Require(restarted.Ok && restarted.Code == "ValheimStarting",
                $"remote Start after reconnect failed: {restarted.Code} {restarted.Message}");
            _ = await WaitForRunStateAsync(owner, profile.Id, "Ready");
            var restopped = await FriendActionAsync(friendLocal, profile.Id, "stop");
            Require(restopped.Ok && restopped.Code == "ValheimStopped",
                $"remote Stop after reconnect failed: {restopped.Code} {restopped.Message}");
            _ = await WaitForRunStateAsync(owner, profile.Id, "Offline");
            Console.WriteLine("PASS saved authenticated connection survives Host and Friend restart and still controls lifecycle");
            passes++;

            Console.WriteLine($"Core remote journey: {passes} groups passed, 0 failed. Disposable data: {root}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL core remote journey after {passes} passing groups: {ex}");
            throw;
        }
        finally
        {
            await TryStopManagedRunAsync(hostPort, profile.Id, host);
            StopApp(friend);
            StopApp(host);
        }
    }

    private static async Task<FriendView> WaitForConnectedAsync(HttpClient friend)
    {
        FriendView? latest = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            latest = await PostAsync<object, FriendView>(friend, "/api/local/friend/poll", new { });
            if (latest.State == "Connected") return latest;
            await Task.Delay(100);
        }
        throw new Exception($"saved Friend connection did not recover: {latest?.State} {latest?.ConnectionCode} {latest?.Detail}");
    }

    private static async Task<RunView> WaitForRunStateAsync(HttpClient owner, Guid profileId, string expected)
    {
        RunView? latest = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
            latest = snapshot?.Runs.Single(run => run.ProfileId == profileId);
            if (latest?.State == expected) return latest;
            await Task.Delay(100);
        }
        throw new Exception($"managed server did not reach {expected}; last state was {latest?.State}: {latest?.Detail}");
    }

    private static async Task<FriendActionResult> FriendActionAsync(HttpClient friend, Guid profileId, string action)
    {
        var submitted = await PostAsync<object, FriendActionResult>(friend,
            $"/api/local/friend/{profileId}/{action}", new { });
        if (submitted.Code != "OperationAccepted" || submitted.OperationId is not { } operationId)
            return submitted;

        for (var attempt = 0; attempt < 450; attempt++)
        {
            var status = await PostAsync<object, FriendView>(friend, "/api/local/friend/poll", new { });
            var operation = status.Profiles.Select(profile => profile.Operation)
                .FirstOrDefault(candidate => candidate?.Id == operationId);
            if (operation is not null && RemoteOperationStates.Terminal(operation.State))
                return new(operation.Ok == true, operation.Code, operation.Message, null,
                    operation.PortConflicts, operation.Id, operation.State);
            await Task.Delay(100);
        }
        throw new Exception($"remote {action} operation {operationId} did not finish");
    }

    private static async Task TryStopManagedRunAsync(int hostPort, Guid profileId, Process? host)
    {
        if (host is null) return;
        try
        {
            if (host.HasExited) return;
            using var owner = LocalClient(hostPort);
            var snapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
            if (snapshot?.Runs.Single(run => run.ProfileId == profileId).State != "Offline")
                _ = await PostAsync<object, ActionResult>(owner,
                    $"/api/local/profiles/{profileId}/stop", new { });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Core journey cleanup could not ask the Host to stop its disposable run: {ex.Message}");
        }
    }

    private static Process StartApp(string path, string mode, int port, string data)
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
        var process = Process.Start(info) ?? throw new Exception("TogetherServer did not start");
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

    private static async Task WaitLocalAsync(int port)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromMilliseconds(300)
        };
        for (var attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                using var response = await client.GetAsync("/api/local/snapshot");
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(100);
        }
        throw new Exception($"local app on TCP {port} did not start");
    }

    private static HttpClient LocalClient(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient client,
        string path, TRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.Add("Origin", client.BaseAddress!.ToString().TrimEnd('/'));
        request.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<TResponse>(Json) ??
            throw new Exception($"empty local POST {path}: {(int)response.StatusCode}");
    }

    private static async Task<TResponse> PutAsync<TRequest, TResponse>(HttpClient client,
        string path, TRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.Add("Origin", client.BaseAddress!.ToString().TrimEnd('/'));
        request.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<TResponse>(Json) ??
            throw new Exception($"empty local PUT {path}: {(int)response.StatusCode}");
    }

    private static int FreeTcpPort(params int[] excluded)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = Random.Shared.Next(51000, 60000);
            if (excluded.Contains(port)) continue;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new Exception("no free TCP port was available for the core remote journey");
    }

    private static int FreeUdpPair()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = Random.Shared.Next(36000, 50000);
            try
            {
                using var game = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                { ExclusiveAddressUse = true };
                using var query = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                { ExclusiveAddressUse = true };
                game.Bind(new IPEndPoint(IPAddress.Any, port));
                query.Bind(new IPEndPoint(IPAddress.Any, port + 1));
                return port;
            }
            catch (SocketException) { }
        }
        throw new Exception("no free UDP port pair was available for the disposable Valheim fixture");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
