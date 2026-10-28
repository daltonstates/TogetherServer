using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class LocalPairingLabChecks
{
    internal static async Task RunAsync(string root)
    {
        LocalPairingLabEvidenceChecks.Run();
        var args = new[] { "--local-pairing-lab", "--staging", "--host", "--port", "59101" };
        LocalPairingLabMode.ValidateArguments(args);
        foreach (var extras in new[]
        {
            new[] { "--desktop" }, new[] { "--demo" }, new[] { "--startup" },
            new[] { "--apply-update", "unused" }, new[] { "--friend" }, new[] { "--staging" },
            new[] { "--port", "59102" }, new[] { "--install-integration" }
        })
            Reject(() => LocalPairingLabMode.ValidateArguments(args.Concat(extras).ToArray()), "mixed native or duplicate arguments");
        Reject(() => LocalPairingLabMode.ValidateArguments(["--local-pairing-lab", "--host", "--port", "59101"]), "missing explicit staging");
        Reject(() => LocalPairingLabMode.ValidateArguments(["--local-pairing-lab", "--staging", "--friend", "--port", "0"]), "invalid local port");
        Require(LocalPairingLabMode.Resolve(["--host"]) is null, "ordinary launches became lab launches");
        Require(LocalPairingLabMode.AllowsControlPolicy(new(CompanionListeningEnabled: true)) &&
            !LocalPairingLabMode.AllowsControlPolicy(new(AutoShutdownEnabled: true)) &&
            !LocalPairingLabMode.AllowsControlPolicy(new(KeepAwakeWhileHosting: true)), "lab control policy admitted automatic Stop or power changes");
        await NetworkPolicyChecksAsync();
        var probed = new List<int>();
        var chosenPort = LocalPairingLabMode.SelectFixtureUdpPair([36000, 36002, 36004, 36006], [36001], candidate =>
        {
            probed.Add(candidate);
            return candidate == 36004;
        });
        Require(chosenPort == 36004 && probed.SequenceEqual([36002, 36004]),
            "Fixture selection treated a table-free but bind-denied UDP pair as usable or ignored an observed query port.");
        var probes = 0;
        try
        {
            LocalPairingLabMode.SelectFixtureUdpPair(Enumerable.Repeat(37000, 201), [], _ => { probes++; return false; });
            throw new InvalidDataException("Unbindable fixture ports were selected.");
        }
        catch (InvalidOperationException) { }
        Require(probes == 200, "Unbindable fixture port selection was unbounded.");
        using (var recoveryData = new LocalData(Path.Combine(root, "lab-no-native-restart")))
        {
            var recovery = new StartupRecoveryService(recoveryData, [], "not-launched.exe", registerWindowsRestart: false);
            Require(!recovery.View([], new()).WindowsRestartRegistered, "lab registered a desktop crash restart");
        }

        var repository = Path.Combine(root, "lab-policy-repo");
        var dataRoot = Path.Combine(repository, "local-data", "local-pairing-lab", "run", "host");
        var fixture = Path.Combine(repository, "src", "TogetherServer.Fixture", "bin", "Release", "net10.0", "TogetherServer.Fixture.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
        foreach (var file in new[] { fixture, Path.ChangeExtension(fixture, ".dll"), Path.ChangeExtension(fixture, ".deps.json"), Path.ChangeExtension(fixture, ".runtimeconfig.json") })
            File.WriteAllBytes(file, Encoding.UTF8.GetBytes("synthetic identity bytes; never executable"));
        var environment = new Dictionary<string, string>
        {
            ["TOGETHERSERVER_STAGING_DATA_DIR"] = dataRoot,
            ["TOGETHERSERVER_DATA_DIR"] = dataRoot + "-unused-production",
            ["TOGETHERSERVER_FIXTURE_ROOT"] = dataRoot
        };
        string? Read(string name) => environment.GetValueOrDefault(name);
        var lab = LocalPairingLabMode.Resolve(args, repository, Read)!;
        var instance = AppInstance.Resolve(args, Read);
        instance.PrepareDataRoot();
        lab.PrepareDataRoot();
        lab.PrepareDataRoot();
        Require(lab.SelectedLanAddress is null && !lab.AllowsBind(IPAddress.Parse("10.20.30.40")), "default lab admitted LAN traffic");
        var lanAddress = IPAddress.Parse("10.20.30.40");
        var secondLanAddress = IPAddress.Parse("10.20.30.41");
        LocalPairingLabInterface[] adapters = [new(OperationalStatus.Up, NetworkInterfaceType.Ethernet, [lanAddress, secondLanAddress])];
        environment[LocalPairingLabNetwork.LanAddressEnvironmentVariable] = lanAddress.ToString();
        var widened = LocalPairingLabMode.Resolve(args, repository, Read, () => adapters)!;
        Reject(widened.PrepareDataRoot, "widened network on loopback lab resume");
        var lanDataRoot = Path.Combine(repository, "local-data", "local-pairing-lab", "lan-run", "host");
        environment["TOGETHERSERVER_STAGING_DATA_DIR"] = lanDataRoot;
        environment["TOGETHERSERVER_DATA_DIR"] = lanDataRoot + "-unused-production";
        environment["TOGETHERSERVER_FIXTURE_ROOT"] = lanDataRoot;
        var lanLab = LocalPairingLabMode.Resolve(args, repository, Read, () => adapters)!;
        AppInstance.Resolve(args, Read).PrepareDataRoot();
        lanLab.PrepareDataRoot();
        LocalPairingLabMode.Resolve(args, repository, Read, () => adapters)!.PrepareDataRoot();
        Require(lanLab.AllowsBind(lanAddress) && !lanLab.AllowsBind(secondLanAddress), "LAN lab admitted an unselected local address");
        var lanSettings = new HostSettings
        {
            CompanionBindAddress = lanAddress.ToString(),
            CompanionEndpoint = $"https://{lanAddress}:59102"
        };
        Require(lanLab.ValidateSettings(lanSettings).Ok && !lab.ValidateSettings(lanSettings).Ok, "selected LAN settings boundary failed");
        lanSettings.CompanionBindAddress = secondLanAddress.ToString();
        Require(!lanLab.ValidateSettings(lanSettings).Ok, "another assigned LAN bind was accepted");
        lanSettings.CompanionBindAddress = lanAddress.ToString();
        lanSettings.CompanionEndpoint = $"https://{secondLanAddress}:59102";
        Require(!lanLab.ValidateSettings(lanSettings).Ok, "another assigned LAN endpoint was accepted");
        environment[LocalPairingLabNetwork.LanAddressEnvironmentVariable] = secondLanAddress.ToString();
        Reject(() => LocalPairingLabMode.Resolve(args, repository, Read, () => adapters)!.PrepareDataRoot(), "changed LAN address on lab resume");
        environment.Remove(LocalPairingLabNetwork.LanAddressEnvironmentVariable);
        Reject(() => LocalPairingLabMode.Resolve(args, repository, Read)!.PrepareDataRoot(), "changed LAN choice on lab resume");
        environment["TOGETHERSERVER_STAGING_DATA_DIR"] = dataRoot;
        environment["TOGETHERSERVER_DATA_DIR"] = dataRoot + "-unused-production";
        environment["TOGETHERSERVER_FIXTURE_ROOT"] = dataRoot;
        lab.PrepareDataRoot();
        var profile = new ServerProfile { Kind = GameKinds.Fixture, WorldDirectory = Path.Combine(dataRoot, "world"), ExecutablePath = fixture };
        Require(lab.ValidateProfile(profile) is null, "canonical disposable fixture rejected");
        var settings = new HostSettings { Profiles = [profile], CompanionEndpoint = "https://127.0.0.1:59102" };
        Require(lab.ValidateSettings(settings).Ok, "loopback fixture settings rejected");
        settings.CompanionBindAddress = "0.0.0.0";
        Require(!lab.ValidateSettings(settings).Ok, "public bind accepted");
        settings.CompanionBindAddress = "127.0.0.1";
        settings.CompanionEndpoint = "https://192.0.2.1:59102";
        Require(!lab.ValidateSettings(settings).Ok, "external route accepted");
        profile.Kind = GameKinds.Valheim;
        Require(lab.ValidateProfile(profile) is not null, "real driver accepted");
        profile.Kind = GameKinds.Fixture;
        profile.ExecutablePath = Path.Combine(dataRoot, "TogetherServer.Fixture.exe");
        Require(lab.ValidateProfile(profile) is not null, "lookalike fixture executable accepted");
        profile.ExecutablePath = fixture;
        profile.WorldDirectory = Path.Combine(root, "outside-lab");
        Require(lab.ValidateProfile(profile) is not null, "outside save path accepted");
        profile.WorldDirectory = Path.Combine(dataRoot, "world");
        var fixtureDll = Path.ChangeExtension(fixture, ".dll");
        var fixtureDllBytes = File.ReadAllBytes(fixtureDll);
        File.AppendAllText(fixtureDll, "changed managed code");
        Require(lab.ValidateProfile(profile) is not null, "changed fixture managed code accepted");
        File.WriteAllBytes(fixtureDll, fixtureDllBytes);
        Require(lab.ValidateProfile(profile) is null, "restored reviewed fixture was rejected");
        File.AppendAllText(fixture, "changed");
        Require(lab.ValidateProfile(profile) is not null, "changed fixture bytes accepted");
        Reject(() => LocalPairingLabMode.Resolve(args, repository, Read)!.PrepareDataRoot(), "changed fixture marker");
        environment["TOGETHERSERVER_STAGING_DATA_DIR"] = Path.Combine(root, "outside-lab");
        Reject(() => LocalPairingLabMode.Resolve(args, repository, Read), "outside disposable root");

        foreach (var endpoint in new[] { "https://localhost:59101", "http://127.0.0.1:59101", "https://192.0.2.1:59101", "https://127.0.0.1:59101/path", "https://user@127.0.0.1:59101", "https://127.0.0.1:59101/?route=x" })
        {
            Require(!LocalPairingLabMode.IsLoopbackEndpoint(endpoint), "unsafe loopback endpoint accepted");
            var rejected = false;
            try { using var client = FriendLink.MakeClient(endpoint, [new string('A', 64)], loopbackOnly: true); }
            catch (HttpRequestException) { rejected = true; }
            Require(rejected, "pinned client admitted non-loopback target");
        }
        using (var offline = LocalPairingLabMode.OfflineClient(TimeSpan.FromSeconds(1)))
        {
            var rejected = false;
            try { await offline.GetAsync("https://example.invalid/"); }
            catch (HttpRequestException) { rejected = true; }
            Require(rejected, "lab Internet client attempted networking");
        }
        var id = Guid.NewGuid();
        Require(LocalPairingLabMode.AllowsRequest("POST", $"/api/local/servers/{id}/invite") &&
            LocalPairingLabMode.AllowsRequest("POST", $"/api/local/friend/{id}/shared-world/hosting/start") &&
            LocalPairingLabMode.AllowsRequest("POST", $"/api/local/profiles/{id}/shared-world/hosting/start") &&
            LocalPairingLabMode.AllowsRequest("POST", $"/api/local/friend/{id}/shared-world/hosting/access/check"), "fixed lab actions blocked");
        foreach (var path in new[]
        {
            $"/api/local/profiles/{id}/shared-world/hosting/access/check",
            $"/api/local/friend/{id}/shared-world/hosting/access/check/extra",
            $"/api/local/friend/{id}/shared-world/hosting/access/check?force=true",
            $"/api/local/friend/{id}/shared-world/hosting/access/check\n",
            $"/api/local/friend/{new string('-', 36)}/shared-world/hosting/access/check",
            $"/api/local/friend/{id}/shared-world/hosting/access",
            $"/api/local/friend/{id}/shared-world/hosting/access/refresh"
        }) Require(!LocalPairingLabMode.AllowsRequest("POST", path), "unreviewed hosting access check route admitted");
        foreach (var method in new[] { "PUT", "DELETE", "PATCH" })
            Require(!LocalPairingLabMode.AllowsRequest(method, $"/api/local/friend/{id}/shared-world/hosting/access/check"),
                "hosting access check admitted an unreviewed method");
        var group = new string('A', 64);
        Require(LocalPairingLabMode.AllowsRequest("GET", "/api/local/background-jobs") &&
            !LocalPairingLabMode.AllowsRequest("POST", "/api/local/background-jobs") &&
            LocalPairingLabMode.AllowsRequest("GET", "/api/local/world-peers") &&
            LocalPairingLabMode.AllowsRequest("GET", "/api/local/world-peers/work") &&
            LocalPairingLabMode.AllowsRequest("PUT", $"/api/local/world-peers/{group}/sharing") &&
            LocalPairingLabMode.AllowsRequest("POST", $"/api/local/world-peers/{group}/connect"), "fixed peer routes blocked");
        Require(!LocalPairingLabMode.AllowsRequest("POST", $"/api/local/world-peers/{group}/sharing") &&
            !LocalPairingLabMode.AllowsRequest("POST", "/api/local/world-peers/work") &&
            !LocalPairingLabMode.AllowsRequest("PUT", $"/api/local/world-peers/{group}/connect") &&
            !LocalPairingLabMode.AllowsRequest("POST", "/api/local/world-peers/not-a-group/connect"), "noncanonical peer route admitted");
        foreach (var suffix in new[] { "", "/restore", "/finish" })
        {
            var recovery = $"/api/local/world-peers/{group}/owner-recovery{suffix}";
            Require(LocalPairingLabMode.AllowsRequest("POST", recovery), "fixed owner continuation preparation blocked");
            foreach (var method in new[] { "GET", "PUT", "DELETE", "PATCH" })
                Require(!LocalPairingLabMode.AllowsRequest(method, recovery), "owner continuation admitted an unreviewed method");
        }
        foreach (var path in new[]
        {
            $"/api/local/world-peers/{group}/owner-recovery/start",
            $"/api/local/world-peers/{group}/owner-recovery/hosting/{group}",
            $"/api/local/world-peers/{group}/owner-recovery/restore/extra",
            $"/api/local/world-peers/{group}/owner-recovery/",
            $"/api/local/world-peers/{group}/owner-recovery?sourceBranchHash={group}",
            $"/api/local/world-peers/{group[..63]}/owner-recovery",
            $"/api/local/world-peers/{group}A/owner-recovery",
            $"/api/local/world-peers/{new string('G', 64)}/owner-recovery",
            $"/api/companion/world-peers/{group}/owner-recovery"
        }) Require(!LocalPairingLabMode.AllowsRequest("POST", path), "unreviewed owner continuation route admitted");
        foreach (var path in new[]
        {
            "/", "/index.html", "/favicon.svg", "/assets/index-review123.js", "/assets/index-review123.css",
            "/api/local/data-recovery", "/api/local/network/ports", "/api/local/network/routes", "/api/local/game-types",
            "/api/local/profile", "/api/local/required-setup", "/api/local/getting-started", "/api/local/desktop/notifications",
            "/api/local/desktop/preferences", "/api/local/update", "/api/local/update/preparation",
            $"/api/local/friend/connections/{id}/servers/{id}/chat/summary"
        }) Require(LocalPairingLabMode.AllowsRequest("GET", path), "reviewed UI bootstrap read blocked");
        foreach (var path in new[] { "/api/local/profile", "/api/local/getting-started", "/api/local/desktop/notifications", "/api/local/ui-drafts" })
            Require(LocalPairingLabMode.AllowsRequest("PUT", path), "reviewed UI preference or draft write blocked");
        foreach (var path in new[]
        {
            "/api/local/required-setup/profile-name/complete", "/api/local/ui-drafts/read", "/api/local/ui-drafts/clear",
            $"/api/local/servers/{id}/invite/current", $"/api/local/friend/connections/{id}/servers/{id}/chat/sync",
            $"/api/local/friend/connections/{id}/servers/{id}/chat/messages"
        }) Require(LocalPairingLabMode.AllowsRequest("POST", path), "reviewed UI setup, invitation or chat action blocked");
        foreach (var path in new[] { "/assets/../private.js", "/src/main.tsx", "/assets/not-a-bundle.exe", "/api/local/network/routes/extra" })
            Require(!LocalPairingLabMode.AllowsRequest("GET", path), "unreviewed asset or UI route admitted");
        foreach (var path in new[] { "/api/local/show", "/api/local/update/check", "/api/local/update/install", "/api/local/browse", "/api/local/network/test-friend-route", $"/api/local/friend/{id}/open-game", $"/api/local/profiles/{id}/files/server/open", $"/api/local/friend/connections/{id}/endpoint", "/api/local/desktop/preferences", "/api/local/required-setup/unreviewed/complete" })
            Require(!LocalPairingLabMode.AllowsRequest("POST", path) && !LocalPairingLabMode.AllowsRequest("PUT", path), "native or arbitrary route admitted");
    }

    private static async Task NetworkPolicyChecksAsync()
    {
        var scans = 0;
        var loopback = LocalPairingLabNetwork.Resolve(null, () =>
        {
            scans++;
            throw new InvalidOperationException("Default loopback mode must not enumerate adapters.");
        });
        Require(loopback.SelectedLanAddress is null && loopback.AllowsBind(IPAddress.Loopback) &&
            loopback.AllowsBind(IPAddress.IPv6Loopback) && !loopback.AllowsBind(IPAddress.Parse("10.20.30.40")) &&
            !loopback.AllowsBind(IPAddress.Parse("::ffff:127.0.0.1")) && scans == 0, "default loopback scope changed");

        foreach (var value in new[] { "10.20.30.40", "172.16.1.2", "172.31.255.254", "192.168.2.3" })
        {
            var address = IPAddress.Parse(value);
            var valid = LocalPairingLabNetwork.Resolve(value, () => [new(OperationalStatus.Up, NetworkInterfaceType.Ethernet, [address])]);
            Require(valid.AllowsBind(address) && valid.IsAllowedEndpoint($"https://{value}:59101"), "assigned RFC1918 address rejected");
        }
        var selected = IPAddress.Parse("10.20.30.40");
        var other = IPAddress.Parse("192.168.2.3");
        LocalPairingLabInterface[] adapters = [new(OperationalStatus.Up, NetworkInterfaceType.Ethernet, [selected, other])];
        var network = LocalPairingLabNetwork.Resolve(selected.ToString(), () => adapters);
        Require(network.SelectedLanAddress == selected.ToString() && network.AllowsBind(IPAddress.Loopback) &&
            network.AllowsBind(selected) && !network.AllowsBind(other), "selected address widened to other local addresses");
        foreach (var value in new[]
        {
            " ", "localhost", "10.1", "010.20.30.40", "169090600", "https://10.20.30.40", "10.20.30.40:59101",
            "0.0.0.0", "127.0.0.1", "172.15.1.2", "172.32.1.2", "192.169.2.3", "100.64.1.2", "169.254.1.2",
            "192.0.2.1", "8.8.8.8", "224.0.0.1", "255.255.255.255", "::1", "::ffff:10.20.30.40", "2001:db8::1"
        })
            Reject(() => LocalPairingLabNetwork.Resolve(value, () => adapters), "invalid or nonprivate LAN selection");
        Reject(() => LocalPairingLabNetwork.Resolve("10.20.30.41", () => adapters), "unassigned LAN selection");
        Reject(() => LocalPairingLabNetwork.Resolve(selected.ToString(), () => []), "missing local adapter");
        foreach (var adapter in new LocalPairingLabInterface[]
        {
            new(OperationalStatus.Down, NetworkInterfaceType.Ethernet, [selected]),
            new(OperationalStatus.Unknown, NetworkInterfaceType.Ethernet, [selected]),
            new(OperationalStatus.Up, NetworkInterfaceType.Loopback, [selected]),
            new(OperationalStatus.Up, NetworkInterfaceType.Ethernet, [selected.MapToIPv6()])
        })
            Reject(() => LocalPairingLabNetwork.Resolve(selected.ToString(), () => [adapter]), "inactive, loopback or mapped adapter selection");
        Reject(() => LocalPairingLabNetwork.Resolve(selected.ToString(), () => throw new NetworkInformationException()), "unavailable adapter inspection");

        foreach (var endpoint in new[]
        {
            "https://192.168.2.3:59101", "https://10.20.30.41:59101", "https://localhost:59101",
            "http://10.20.30.40:59101", "https://user@10.20.30.40:59101", "https://10.20.30.40:59101/path",
            "https://10.20.30.40:59101/?target=x", "https://10.20.30.40:59101/#fragment", "https://[::ffff:10.20.30.40]:59101"
        }) Require(!network.IsAllowedEndpoint(endpoint), "unsafe LAN peer origin admitted");
        var transport = new HttpClientHandler { AllowAutoRedirect = true, UseProxy = true };
        using (network.GuardClient(transport))
            Require(!transport.AllowAutoRedirect && !transport.UseProxy, "lab transport retained redirects or proxy use");

        var recording = new RecordingHandler();
        using var client = new HttpClient(network.GuardClient(recording));
        using var local = await client.GetAsync("https://127.0.0.1:59101/api/companion/status");
        using var lan = await client.GetAsync("https://10.20.30.40:59101/api/companion/status?limit=1");
        Require(recording.Requests == 2, "allowed local request did not reach fake transport");
        foreach (var endpoint in new[]
        {
            "https://192.168.2.3:59101/api/companion/status", "https://10.20.30.41:59101/api/companion/status",
            "https://192.0.2.1:59101/api/companion/status", "https://localhost:59101/api/companion/status",
            "http://127.0.0.1:59101/api/companion/status", "https://user@10.20.30.40:59101/api/companion/status",
            "https://[::ffff:10.20.30.40]:59101/api/companion/status"
        }) await RejectRequestAsync(client, endpoint);
        Require(recording.Requests == 2, "rejected request reached fake transport");
        adapters = [new(OperationalStatus.Down, NetworkInterfaceType.Ethernet, [selected])];
        Require(!network.AllowsBind(selected) && !network.IsAllowedEndpoint("https://10.20.30.40:59101"), "lost LAN assignment stayed authorized");
        await RejectRequestAsync(client, "https://10.20.30.40:59101/api/companion/status");
        Require(recording.Requests == 2, "lost LAN assignment reached fake transport");
        using var loopbackAfterLoss = await client.GetAsync("https://127.0.0.1:59101/api/companion/status");
        Require(recording.Requests == 3, "lost LAN assignment blocked loopback");
    }

    private static async Task RejectRequestAsync(HttpClient client, string endpoint)
    {
        try { using var response = await client.GetAsync(endpoint); }
        catch (HttpRequestException) { return; }
        throw new InvalidOperationException("Lab request guard admitted an unapproved target.");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException) { return; }
        throw new InvalidOperationException("Lab failed to reject " + message);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
