using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using TogetherServer;

internal static partial class CoreRemoteJourney
{
    public static async Task RunMixedVersionAsync(string currentApp, string legacyApp, string fixture)
    {
        Require(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
            "Mixed-version checks are restricted to hosted Windows CI.");
        Require(Path.IsPathFullyQualified(currentApp) && Path.IsPathFullyQualified(legacyApp) &&
            File.Exists(currentApp) && File.Exists(legacyApp) && File.Exists(fixture) &&
            !string.Equals(currentApp, legacyApp, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(legacyApp), ".exe", StringComparison.OrdinalIgnoreCase),
            "Supply separate existing current and legacy EXEs and the fixed synthetic Valheim fixture.");
        var currentVersion = MixedAppVersion(currentApp);
        Require(MixedAppVersion(legacyApp) == "0.3.0", "The separately supplied legacy EXE must have file version 0.3.0.");
        Require(currentVersion == "0.3.1", "The release candidate must have file version 0.3.1.");
        await MixedRolesAsync(currentApp, legacyApp, fixture, "0.3.1", "0.3.0", "new-host-old-friend");
        await MixedRolesAsync(legacyApp, currentApp, fixture, "0.3.0", "0.3.1", "old-host-new-friend");
        Console.WriteLine("Mixed-version journey: both 0.3.0/0.3.1 role directions passed. Synthetic loopback only; no real Friend-network or game acceptance.");
    }

    private static string MixedAppVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        Require(version.FilePrivatePart == 0, "Unexpected private file-version component.");
        return $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
    }

    private static async Task MixedRolesAsync(string hostApp, string friendApp, string fixture,
        string hostVersion, string friendVersion, string direction)
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "mixed-version-journey", direction, Guid.NewGuid().ToString("N")));
        var hostData = Path.Combine(root, "host");
        var friendData = Path.Combine(root, "friend");
        var hostPort = FreeTcpPort();
        var controlPort = FreeTcpPort(hostPort);
        var friendPort = FreeTcpPort(hostPort, controlPort);
        var profile = new ServerProfile
        {
            // The fixed fixture rejects any different name/world/password before publishing readiness.
            Kind = GameKinds.Valheim, Name = "Mixed synthetic world", ServerName = "Fixture \"Valheim\"",
            WorldSource = "New", WorldId = "fixture-world", WorldDirectory = Path.Combine(hostData, "worlds", "fixture"),
            PublicListing = false, GamePort = FreeUdpPair(), ExecutablePath = fixture
        };
        Directory.CreateDirectory(profile.WorldDirectory);
        var settings = new HostSettings { Profiles = [profile], MaxConcurrentServers = 1, AutoShutdownEnabled = false,
            CompanionBindAddress = "127.0.0.1", CompanionPort = controlPort, CompanionEndpoint = $"https://127.0.0.1:{controlPort}" };
        Process? host = null, friend = null;
        try
        {
            host = StartApp(hostApp, "--host", hostPort, hostData);
            friend = StartApp(friendApp, "--friend", friendPort, friendData);
            await WaitLocalAsync(hostPort);
            await WaitLocalAsync(friendPort);
            using var owner = LocalClient(hostPort);
            using var client = LocalClient(friendPort);
            Require((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "Mixed Host setup failed.");
            Require((await PostAsync<ValheimPasswordRequest, ActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/password", new("fixture-pass-123"))).Ok, "Synthetic password setup failed.");
            var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner,
                $"/api/local/servers/{profile.Id}/invite", new(false, true, true));
            Require(invite.GetProperty("ok").GetBoolean() && invite.GetProperty("listenerActive").GetBoolean(), "Mixed invite failed.");
            settings.CompanionListeningEnabled = true;
            settings.RemoteControlsEnabled = true;
            Require((await PostAsync<FriendPairRequest, FriendActionResult>(client, "/api/local/friend/pair",
                new(invite.GetProperty("password").GetString()!))).Ok, "Mixed Friend pairing failed.");
            var connected = await WaitForConnectedAsync(client);
            RequireMixedIdentity(connected, profile.Id, hostVersion, friendVersion);
            var companion = await owner.GetFromJsonAsync<JsonElement>("/api/local/companion");
            Require(!string.IsNullOrWhiteSpace(companion.GetProperty("fingerprint").GetString()), "Host pin identity unavailable.");
            var deviceId = companion.GetProperty("devices").EnumerateArray().Single(item => item.GetProperty("paired").GetBoolean()).GetProperty("id").GetGuid();
            Require((await PutAsync<DevicePermissionRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceId}/permissions", new(true, true))).Ok, "Mixed permissions failed.");
            connected = await WaitForConnectedAsync(client);
            Require(connected.Profiles.Single().CanStart && connected.Profiles.Single().CanStop, "Mixed permissions were not propagated.");
            Console.WriteLine($"PASS {direction}: pinned authenticated pairing and exact per-role versions, protocol 3 and assigned status");

            var ready = await MixedStartReadyAsync(client, owner, profile.Id, direction);
            Require(ready.ProcessId is not null && ready.OnlinePlayers == 0, "Synthetic Ready did not report fresh zero.");
            connected = await WaitForConnectedAsync(client);
            Require(connected.Profiles.Single().State == "Ready", "Mixed Friend did not observe Ready.");
            var countFile = Path.Combine(profile.WorldDirectory, "synthetic-online-players.txt");
            File.WriteAllText(countFile, "1");
            var occupied = await FriendActionAsync(client, profile.Id, "refresh");
            Require(occupied.Ok && occupied.Status?.Profiles.Single().OnlinePlayers == 1, "Mixed positive count not observed.");
            var refused = await FriendActionAsync(client, profile.Id, "stop");
            Require(!refused.Ok && refused.Code == "PlayersOnline", "Mixed occupied Stop was not refused.");
            Require((await WaitForRunStateAsync(owner, profile.Id, "Ready")).ProcessId == ready.ProcessId, "Refused Stop changed the exact run.");
            File.WriteAllText(countFile, "0");
            var empty = await FriendActionAsync(client, profile.Id, "refresh");
            Require(empty.Ok && empty.Status?.Profiles.Single().OnlinePlayers == 0 && empty.Status.Profiles.Single().CanStopNow,
                "Mixed fresh zero did not authorize Stop.");
            Require((await FriendActionAsync(client, profile.Id, "stop")).Ok, "Mixed zero Stop failed.");
            await WaitForRunStateAsync(owner, profile.Id, "Offline");
            Require(File.ReadAllText(Path.Combine(profile.WorldDirectory, "synthetic-stop.marker")) == "Ctrl+C received", "Mixed Stop was not graceful.");
            Console.WriteLine($"PASS {direction}: remote Start/Ready, positive-count Stop refusal, fresh-zero graceful Stop");

            settings.RemoteControlsEnabled = false;
            Require((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "Mixed disable failed.");
            Require((await PostAsync<object, FriendView>(client, "/api/local/friend/poll", new { })).State == "Disabled", "Mixed disable notice missing.");
            Require((await FriendActionAsync(client, profile.Id, "start")).Code == "RemoteControlsDisabled", "Mixed disabled Host accepted Start.");
            Require((await FriendActionAsync(client, profile.Id, "refresh")).Ok, "Mixed disabled status refresh failed.");
            settings.RemoteControlsEnabled = true;
            Require((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", settings)).Ok, "Mixed re-enable failed.");
            Require((await PutAsync<DeviceServerAccessRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceId}/servers", new([]))).Ok, "Mixed assignment removal failed.");
            Require((await PostAsync<object, FriendView>(client, "/api/local/friend/poll", new { })).Profiles.Count == 0,
                "Mixed unassigned server remained visible.");
            Require((await FriendActionAsync(client, profile.Id, "start")).Code == "PermissionDenied", "Mixed unassigned Start accepted.");
            Require((await PutAsync<DeviceServerAccessRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceId}/servers", new([profile.Id]))).Ok, "Mixed assignment restoration failed.");

            StopApp(host); host = null;
            var disconnected = await PostAsync<object, FriendView>(client, "/api/local/friend/poll", new { });
            Require(disconnected.State == "Disconnected/Unknown", "Closed mixed Host did not become Unknown.");
            StopApp(friend); friend = null;
            host = StartApp(hostApp, "--host", hostPort, hostData);
            friend = StartApp(friendApp, "--friend", friendPort, friendData);
            await WaitLocalAsync(hostPort);
            await WaitLocalAsync(friendPort);
            connected = await WaitForConnectedAsync(client);
            RequireMixedIdentity(connected, profile.Id, hostVersion, friendVersion);
            Require(connected.Profiles.Single().CanStart && connected.Profiles.Single().CanStop, "Mixed saved permissions lost on restart.");
            _ = await MixedStartReadyAsync(client, owner, profile.Id, direction + " after restart");
            Require((await FriendActionAsync(client, profile.Id, "stop")).Ok, "Mixed saved access could not Stop after restart.");
            await WaitForRunStateAsync(owner, profile.Id, "Offline");
            Require((await PostAsync<object, PairingDecision>(owner, $"/api/local/devices/{deviceId}/revoke", new { })).Ok, "Mixed revoke failed.");
            Require((await PostAsync<object, FriendView>(client, "/api/local/friend/poll", new { })).State == "Revoked", "Mixed revocation not propagated.");
            Require(!(await FriendActionAsync(client, profile.Id, "start")).Ok, "Mixed revoked access accepted Start.");
            Console.WriteLine($"PASS {direction}: disable/status, assignment enforcement, saved pin/access reconnect and restart, revocation");
        }
        finally
        {
            await TryStopManagedRunAsync(hostPort, profile.Id, host);
            StopApp(friend);
            StopApp(host);
        }
    }

    private static async Task<RunView> MixedStartReadyAsync(HttpClient friend, HttpClient owner, Guid profileId, string direction)
    {
        // The common helper waits for a durable operation's terminal result; acceptance alone is not readiness.
        var started = await FriendActionAsync(friend, profileId, "start");
        Require(started.Ok && started.Code == "ValheimStarting",
            $"{direction}: Start did not complete as ValheimStarting: {started.Code} {started.Message}; operation={started.OperationState}");
        try { return await WaitForRunStateAsync(owner, profileId, "Ready"); }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{direction}: terminal Start result {started.Code} did not prove fixture readiness. " +
                "The synthetic executable requires the fixed fixture name, world, password, public=0 and owned save/log roots. " +
                "Automatic shutdown is disabled for this journey. " + error.Message, error);
        }
    }

    private static void RequireMixedIdentity(FriendView view, Guid profileId, string hostVersion, string friendVersion) =>
        Require(view.State == "Connected" && view.LastConnectedUtc is not null && view.ConnectionCode is null &&
            view.ProtocolCompatible && view.HostProtocolVersion == 3 && view.HostVersion == hostVersion &&
            view.FriendVersion == friendVersion && view.Profiles.Single().Id == profileId,
            "Mixed role versions, protocol 3, authenticated status or exact assignment did not match.");
}
