using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class LocalPairingLabChecks
{
    internal static async Task RunAsync(string root)
    {
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
            LocalPairingLabMode.AllowsRequest("POST", $"/api/local/profiles/{id}/shared-world/hosting/start"), "fixed lab actions blocked");
        foreach (var path in new[] { "/api/local/show", "/api/local/update/install", "/api/local/browse", "/api/local/network/test-friend-route", $"/api/local/friend/{id}/open-game", $"/api/local/profiles/{id}/files/server/open", $"/api/local/friend/connections/{id}/endpoint" })
            Require(!LocalPairingLabMode.AllowsRequest("POST", path) && !LocalPairingLabMode.AllowsRequest("PUT", path), "native or arbitrary route admitted");
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
