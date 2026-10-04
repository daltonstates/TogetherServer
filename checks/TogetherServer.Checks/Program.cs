using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Win32;
using TogetherServer;

var fixture = Path.GetFullPath("src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe");
if (!File.Exists(fixture)) throw new FileNotFoundException("Build the fixture in Release first.", fixture);
var root = Path.GetFullPath("local-data/checks/" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;

async Task Check(string name, Func<Task> test)
{
    var filter = args.FirstOrDefault(argument => argument.StartsWith("--filter=", StringComparison.Ordinal))?[9..];
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    try { await test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; }
}

HostSettings Settings(params ServerProfile[] profiles) => new() { MaxConcurrentServers = 2, Profiles = [.. profiles] };
ServerProfile Profile(string name, string world, int port, string? directory = null)
{
    var path = directory ?? Path.Combine(root, name);
    Directory.CreateDirectory(path);
    return new ServerProfile { Name = name, WorldId = world, WorldDirectory = path, GamePort = port, ExecutablePath = fixture };
}
void Require(bool value, string message) { if (!value) throw new Exception(message); }
void RequireThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception(message);
}
int FreePort()
{
    for (var i = 0; i < 100; i++)
    {
        var port = Random.Shared.Next(35000, 59000);
        try
        {
            using var one = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            using var two = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            one.Bind(new IPEndPoint(IPAddress.Loopback, port));
            two.Bind(new IPEndPoint(IPAddress.Loopback, port + 1));
            return port;
        }
        catch (SocketException) { }
    }
    throw new Exception("No free UDP pair found.");
}
void CreateJunction(string link, string target)
{
    var shell = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
    var info = new ProcessStartInfo(shell)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new Exception("junction helper did not start");
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new Exception("junction creation failed: " + process.StandardError.ReadToEnd());
}
LocalData Data(string name) => new(Path.Combine(root, name));
GameServerRegistry Games(LocalData data) => new(data, true, PortProbeMode.LoopbackOnly);
HostManager Manager(LocalData data) => new(data, Games(data));

await Check("live save candidates stay closed until a real load test", () =>
{
    using var data = Data("live-save-candidates");
    var expected = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        [GameKinds.Valheim] = null,
        [GameKinds.MinecraftJava] = "save-all flush",
        [GameKinds.MinecraftBedrock] = "save hold",
        [GameKinds.Factorio] = "/server-save",
        [GameKinds.Terraria] = "save"
    };
    foreach (var (game, firstCommand) in expected)
    {
        var candidate = SharedWorldLiveSaveAdapters.ForGame(game);
        Require(candidate is not null && !candidate.LiveCaptureAccepted &&
            candidate.Steps.Count > 0 && candidate.Steps[0].FixedCommand == firstCommand &&
            candidate.MissingProof.Length > 0, game + " live capture was enabled or lost its reviewed plan");
        var profile = Profile("live-" + game, "world", FreePort());
        profile.Kind = game;
        var status = new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System)).Status(profile);
        Require(status.LiveSave is { Available: false } &&
            status.LiveSave.Message.Contains("post-Stop", StringComparison.Ordinal),
            game + " advertised a live copy without game-load acceptance");
    }
    var bedrock = SharedWorldLiveSaveAdapters.ForGame(GameKinds.MinecraftBedrock)!;
    Require(bedrock.Steps.Select(step => step.FixedCommand).SequenceEqual(
        ["save hold", "save query", "save resume"]) &&
        bedrock.Steps[1].RequiredEvidence == LiveSaveEvidence.FrozenSnapshotQuery,
        "Bedrock candidate lost its hold/query/resume boundary");
    Require(SharedWorldLiveSaveAdapters.ForGame(GameKinds.Custom) is null &&
        SharedWorldLiveSaveAdapters.ForGame("invalid") is null,
        "an unreviewed game gained live capture");
    return Task.CompletedTask;
});

await Check("shared Host snapshot fixture matches the backend contract", async () =>
{
    var fixtureJson = await File.ReadAllTextAsync(Path.GetFullPath("contracts/host-snapshot.v1.json"));
    var fixtureSnapshot = JsonSerializer.Deserialize<HostSnapshot>(fixtureJson,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var profileId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    Require(fixtureSnapshot is not null && fixtureSnapshot.Mode == "Host" &&
        fixtureSnapshot.Settings.KeepAwakeWhileHosting &&
        fixtureSnapshot.Runs.Single().AddedShutdownMinutes == 25 &&
        fixtureSnapshot.Runs.Single().PlayerObservationSource == "FixtureReady" &&
        fixtureSnapshot.Backups![profileId].RetainedSizeBytes == 4096 &&
        fixtureSnapshot.HostingPower is { State: "Active", RequestActive: true },
        "the checked-in shared Host snapshot no longer matches backend records");
});

await Check("owner diagnostics reuse canonical state and support export stays bounded and redacted", async () =>
{
    using var data = Data("owner-diagnostics");
    var profile = Profile("Support fixture", "support-world", FreePort());
    profile.Kind = GameKinds.Valheim;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "adminlist.txt"), "Private player Alice\n");
    data.SaveSettings(Settings(profile));
    var games = Games(data);
    var manager = new HostManager(data, games);
    var snapshot = await manager.SnapshotAsync();
    var ports = PortDiagnostics.Read(snapshot, games, false, []);
    var route = new ExternalPortProbeResult("Reachable",
        "An outside TCP checker reached https://203.0.113.8:5131.", 5131,
        DateTimeOffset.UtcNow, "https://203.0.113.8:5131");
    var diagnostics = OwnerDiagnostics.Build(snapshot, games, ports, [], data.Recovery,
        new UpdateView("Current", "0.1.11", "0.1.11", "TogetherServer is up to date."),
        route, isStaging: true);
    var server = diagnostics.Servers.Single();
    Require(server.Checks.Single(item => item.Id == "managed-process").State == "Offline",
        "offline diagnostics invented a managed process state");
    var portCheck = server.Checks.Single(item => item.Id == "local-game-ports");
    Require(portCheck.Detail.Contains("Seeing a port open on this PC", StringComparison.Ordinal) &&
        portCheck.Detail.Contains("does not prove that a Friend can reach it", StringComparison.Ordinal) &&
        portCheck.Detail.Contains("join the game", StringComparison.Ordinal),
        "declared-port diagnostics overstated local listener evidence");
    Require(diagnostics.EvidenceBoundary.Contains("Friend connected", StringComparison.Ordinal) &&
        diagnostics.EvidenceBoundary.Contains("world saved correctly", StringComparison.Ordinal),
        "diagnostic evidence boundary omitted an external acceptance limit");

    var secret = SupportReportRedactor.Redact(
        "password=hunter2 token=abcdefghijklmnopabcdefghijklmnop TS3-super-secret-code");
    var privatePath = SupportReportRedactor.Redact(
        "File C:\\Users\\Alice Smith\\Saved Games\\world, retry later");
    var address = SupportReportRedactor.Redact(
        "Endpoint https://192.168.1.20:5131 and 10.0.0.4");
    var certificate = SupportReportRedactor.Redact(
        "Certificate fingerprint 0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");
    var player = SupportReportRedactor.Redact("Player Alice said in chat: meet at the castle");
    Require(!secret.Contains("hunter2", StringComparison.Ordinal) &&
        !secret.Contains("super-secret", StringComparison.Ordinal) &&
        !privatePath.Contains("Alice Smith", StringComparison.Ordinal) &&
        !address.Contains("192.168.1.20", StringComparison.Ordinal) &&
        !address.Contains("10.0.0.4", StringComparison.Ordinal) &&
        !certificate.Contains("0123456789ABCDEF", StringComparison.Ordinal) &&
        !player.Contains("Alice", StringComparison.Ordinal) &&
        player.Contains("Player activity redacted", StringComparison.Ordinal),
        "support-report redaction did not deterministically remove representative private values");

    var injected = diagnostics with
    {
        SharedChecks = diagnostics.SharedChecks.Concat([
            new OwnerDiagnosticCheck("redaction-fixture", "Support fixture", "Observed",
                "password=hunter2 at C:\\Users\\Owner\\world via https://10.0.0.4:5131",
                "Player Alice wrote chat content.", "Settings > Diagnostics")
        ]).ToList()
    };
    var activity = Enumerable.Range(0, 100).Select(index => new TogetherServer.ActivityEvent(Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddSeconds(-index), "Player chat Alice", "Authorization Bearer secret-token",
        "C:\\Users\\Owner\\world 10.0.0.4 password=hunter2", ProfileId: profile.Id)).ToList();
    var operations = Enumerable.Range(0, 100).Select(index => new RemoteOperationView(Guid.NewGuid(),
        profile.Id, new string('x', 300), RemoteOperationStates.Failed, false,
        new string('y', 600), "password=hunter2 C:\\Users\\Owner\\world 10.0.0.4",
        DateTimeOffset.UtcNow.AddSeconds(-index), null, DateTimeOffset.UtcNow, null)).ToList();
    var productionRoot = Path.Combine(root, "support-instance-production");
    var stagingRoot = Path.Combine(root, "support-instance-staging");
    string? InstanceEnvironment(string name) => name switch
    {
        "TOGETHERSERVER_DATA_DIR" => productionRoot,
        "TOGETHERSERVER_STAGING_DATA_DIR" => stagingRoot,
        _ => null
    };
    var instance = AppInstance.Resolve(["--staging"], InstanceEnvironment, root);
    var export = SupportReportExporter.Create(injected, snapshot, instance,
        new UpdateView("Unavailable", "0.1.11", null,
            "Update failed at https://10.0.0.4:5131 from C:\\Users\\Owner\\app.exe"),
        activity, operations, data.ReadSupportLogMetadata(), data);
    Require(export.FileName == SupportReportExporter.FileName &&
        export.ContentType == SupportReportExporter.ContentType,
        "support export did not use its fixed safe filename and UTF-8 content type");
    Require(export.SizeBytes == Encoding.UTF8.GetByteCount(export.Content) &&
        export.SizeBytes <= SupportReportExporter.MaximumReportBytes,
        "support export exceeded or misreported its UTF-8 size bound");
    Require(!export.Content.Contains("hunter2", StringComparison.OrdinalIgnoreCase) &&
        !export.Content.Contains("Alice", StringComparison.OrdinalIgnoreCase) &&
        !export.Content.Contains("10.0.0.4", StringComparison.OrdinalIgnoreCase) &&
        !export.Content.Contains("C:\\\\Users", StringComparison.OrdinalIgnoreCase) &&
        !export.Content.Contains("secret-token", StringComparison.OrdinalIgnoreCase),
        "support export serialized a representative secret, path, address, or player value");
    using var report = JsonDocument.Parse(export.Content);
    Require(report.RootElement.GetProperty("recentActivity").GetArrayLength() <= 24 &&
        report.RootElement.GetProperty("recentOperations").GetArrayLength() <= 24,
        "support export exceeded its recent-summary count bounds");
    Require(report.RootElement.GetProperty("settings").GetProperty("profiles")[0]
            .GetProperty("setup").GetProperty("availableReviewedConfigFiles").GetInt32() == 1 &&
        !export.Content.Contains("Private player Alice", StringComparison.Ordinal),
        "the support setup summary omitted file availability or included player-list contents");
    ValidateStringBounds(report.RootElement, 600);
});

await Check("borderless window geometry stays on the selected monitor and detects off-screen bounds", () =>
{
    var rightMonitor = new Rectangle(1920, 0, 1920, 1032);
    var relative = DesktopWindow.RelativeMaximizedBounds(new Rectangle(1920, 0, 1920, 1080), rightMonitor);
    Require(relative == new Rectangle(0, 0, 1920, 1032),
        "maximized bounds retained the secondary monitor offset");
    var inset = DesktopWindow.RelativeMaximizedBounds(new Rectangle(1920, 0, 1920, 1080),
        new Rectangle(1960, 40, 1880, 1040));
    Require(inset == new Rectangle(40, 40, 1880, 1040),
        "maximized bounds lost a top or left taskbar inset");
    var workingAreas = new[]
    {
        new Rectangle(-1920, 0, 1920, 1032),
        new Rectangle(0, 0, 1920, 1032),
        rightMonitor
    };
    Require(DesktopWindow.HasUsefulVisibleArea(new Rectangle(2385, 0, 1180, 820), workingAreas),
        "a visible secondary-monitor window was treated as off-screen");
    Require(!DesktopWindow.HasUsefulVisibleArea(new Rectangle(3840, 0, 1920, 1032), workingAreas),
        "a window beyond the rightmost monitor was treated as visible");
    return Task.CompletedTask;
});

await Check("development executable selects isolated staging without a command-line flag", () =>
{
    var productionRoot = Path.Combine(root, "named-instance-production");
    var stagingRoot = Path.Combine(root, "named-instance-staging");
    string? EnvironmentValue(string name) => name switch
    {
        "TOGETHERSERVER_DATA_DIR" => productionRoot,
        "TOGETHERSERVER_STAGING_DATA_DIR" => stagingRoot,
        _ => null
    };
    var developmentPath = Path.Combine(root, AppInstance.DevelopmentExecutableName);
    var development = AppInstance.Resolve([], EnvironmentValue, Path.Combine(root, "unused-local"), developmentPath);
    var production = AppInstance.Resolve([], EnvironmentValue, Path.Combine(root, "unused-local"),
        Path.Combine(root, "TogetherServer.exe"));
    Require(development.IsStaging && development.DataRoot == Path.GetFullPath(stagingRoot) &&
        development.DisplayName == "TogetherServer DEVELOPMENT",
        "the clearly named development executable did not select isolated staging");
    Require(!production.IsStaging && production.DataRoot == Path.GetFullPath(productionRoot),
        "the ordinary executable stopped selecting production");
    var productionPorts = new[] { production.DefaultLocalPort, production.DefaultCompanionPort,
        production.DefaultValheimPort, production.DefaultValheimPort + 1, production.DefaultMinecraftJavaPort,
        production.DefaultMinecraftBedrockPort, production.DefaultMinecraftBedrockPort + 1 };
    var developmentPorts = new[] { development.DefaultLocalPort, development.DefaultCompanionPort,
        development.DefaultValheimPort, development.DefaultValheimPort + 1, development.DefaultMinecraftJavaPort,
        development.DefaultMinecraftBedrockPort, development.DefaultMinecraftBedrockPort + 1 };
    Require(!productionPorts.Intersect(developmentPorts).Any(),
        "the production and development default port plans overlap");
    RequireThrows<ArgumentException>(() => AppInstance.Resolve(["--startup"], EnvironmentValue,
        Path.Combine(root, "unused-local"), developmentPath),
        "the development executable accepted Windows startup mode");
    return Task.CompletedTask;
});

await Check("staging starts with isolated empty data and fresh-world defaults", () =>
{
    var productionRoot = Path.Combine(root, "instance-production");
    var stagingRoot = Path.Combine(root, "instance-staging");
    var productionWorld = Path.Combine(productionRoot, "worlds", "live-world");
    Directory.CreateDirectory(productionWorld);
    var productionMarker = Path.Combine(productionWorld, "owner-save.db");
    File.WriteAllText(productionMarker, "production-owner-data");
    string? EnvironmentValue(string name) => name switch
    {
        "TOGETHERSERVER_DATA_DIR" => productionRoot,
        "TOGETHERSERVER_STAGING_DATA_DIR" => stagingRoot,
        _ => null
    };
    var instance = AppInstance.Resolve(["--staging"], EnvironmentValue, Path.Combine(root, "unused-local"));
    Require(instance.IsStaging && instance.DefaultLocalPort == 5128 && instance.DefaultCompanionPort == 5132 &&
        instance.DefaultValheimPort == 2458 && instance.DefaultMinecraftJavaPort == 25566 &&
        instance.DefaultMinecraftBedrockPort == 19134, "staging defaults did not use their isolated ports");
    instance.PrepareDataRoot();
    using (var data = new LocalData(instance.DataRoot, instance.DefaultCompanionPort))
    {
        Require(data.LoadSettings().Profiles.Count == 0 && data.LoadSettings().CompanionPort == 5132 &&
            data.LoadRuns().Count == 0, "staging inherited production settings or runs");
    }
    Require(File.ReadAllText(productionMarker) == "production-owner-data" &&
        !Directory.EnumerateFiles(stagingRoot, "owner-save.db", SearchOption.AllDirectories).Any(),
        "staging changed or copied the production world marker");

    var profileId = Guid.NewGuid();
    var valid = Settings(new ServerProfile
    {
        Id = profileId,
        Kind = GameKinds.Valheim,
        Name = "staging",
        ServerName = "staging",
        WorldId = "fresh",
        WorldSource = "New",
        WorldDirectory = Path.Combine(stagingRoot, "worlds", profileId.ToString("N")),
        GamePort = instance.DefaultValheimPort,
        ExecutablePath = fixture
    });
    Require(instance.ValidateSettings(valid).Ok, "a fresh staging world inside the staging root was rejected");
    valid.Profiles[0].WorldDirectory = productionWorld;
    Require(instance.ValidateSettings(valid).Code == "StagingDataBoundary", "a production world path crossed the staging boundary");
    var linkedWorld = Path.Combine(stagingRoot, "worlds", "linked-production-world");
    Directory.CreateDirectory(Path.GetDirectoryName(linkedWorld)!);
    CreateJunction(linkedWorld, productionWorld);
    valid.Profiles[0].WorldDirectory = linkedWorld;
    Require(instance.ValidateSettings(valid).Code == "StagingDataBoundary",
        "a staging world junction crossed into the production world");
    valid.Profiles[0].WorldDirectory = Path.Combine(stagingRoot, "worlds", profileId.ToString("N"));
    valid.Profiles[0].WorldSource = "Existing";
    Require(instance.ValidateSettings(valid).Code == "StagingFreshWorldRequired", "staging accepted an existing Valheim world");
    valid.Profiles[0].Kind = GameKinds.Custom;
    Require(instance.ValidateSettings(valid).Code == "StagingCustomDisabled", "staging accepted an unrestricted custom script profile");
    valid.Profiles[0].Kind = GameKinds.Factorio;
    Require(instance.ValidateSettings(valid).Code == "StagingFactorioDisabled",
        "staging accepted a Factorio profile that requires an existing save copy");
    return Task.CompletedTask;
});

await Check("staging refuses overlapping or pre-populated data roots", () =>
{
    var productionRoot = Path.Combine(root, "boundary-production");
    Directory.CreateDirectory(productionRoot);
    AppInstance Resolve(string stagingRoot) => AppInstance.Resolve(["--staging"], name => name switch
    {
        "TOGETHERSERVER_DATA_DIR" => productionRoot,
        "TOGETHERSERVER_STAGING_DATA_DIR" => stagingRoot,
        _ => null
    }, Path.Combine(root, "unused-local"));
    RequireThrows<InvalidDataException>(() => Resolve(productionRoot), "staging accepted the production root");
    RequireThrows<InvalidDataException>(() => Resolve(Path.Combine(productionRoot, "child")), "staging accepted a root inside production");
    RequireThrows<ArgumentException>(() => AppInstance.Resolve(["--staging", "--startup"], _ => null,
        Path.Combine(root, "startup-local")), "staging accepted Windows startup mode");

    var populated = Path.Combine(root, "prepopulated-staging");
    Directory.CreateDirectory(populated);
    File.WriteAllText(Path.Combine(populated, "unknown.db"), "leave-me-alone");
    RequireThrows<InvalidDataException>(() => Resolve(populated).PrepareDataRoot(),
        "staging opened a pre-populated unmarked folder");
    Require(File.ReadAllText(Path.Combine(populated, "unknown.db")) == "leave-me-alone",
        "staging changed a refused pre-populated folder");

    var marked = Path.Combine(root, "marked-staging");
    var staging = Resolve(marked);
    staging.PrepareDataRoot();
    var production = AppInstance.Resolve([], name => name == "TOGETHERSERVER_DATA_DIR" ? marked : null,
        Path.Combine(root, "other-local"));
    RequireThrows<InvalidDataException>(production.PrepareDataRoot,
        "production opened a staging-marked folder");
    return Task.CompletedTask;
});

await Check("storage v1 migrates pairing to v3 and rejects downgrade or newer schemas", () =>
{
    var migrationRoot = Path.Combine(root, "storage-v1-to-v2");
    Directory.CreateDirectory(migrationRoot);
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    var profileId = Guid.NewGuid();
    var deviceId = Guid.NewGuid();
    var generation = Guid.NewGuid();
    var v1 = new PairingPersistentState
    {
        SchemaVersion = 1,
        Devices =
        [
            new PairedDevice
            {
                Id = deviceId,
                ProfileId = profileId,
                InviteGeneration = generation,
                AssignedProfileIds = [profileId],
                Name = "Migrated Friend PC",
                CanStart = true,
                CanStop = false,
                SaveReceiveProfileIds = [profileId],
                CredentialHash = new string('A', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
            }
        ],
        ServerInvites =
        [
            new ServerInviteState
            {
                ProfileId = profileId,
                Generation = generation,
                Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Endpoint = "https://127.0.0.1:5131",
                Fingerprint = new string('B', 64),
                PairingOpenedUtc = DateTimeOffset.UtcNow,
                PairingExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30),
                DurationMinutes = 30,
                DeviceLimit = 2
            }
        ],
        CredentialRenewals =
        [
            new CredentialRenewalReceipt
            {
                DeviceId = deviceId,
                RequestId = Guid.NewGuid(),
                Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
                PreviousAcceptedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(10)
            }
        ]
    };
    var protectedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(v1, json), null,
        DataProtectionScope.CurrentUser);
    var protectedPayload = JsonSerializer.Serialize(Convert.ToBase64String(protectedBytes), json);
    File.WriteAllText(Path.Combine(migrationRoot, "pairing-state.protected"),
        protectedPayload);
    File.WriteAllText(Path.Combine(migrationRoot, "storage-schema.json"), "{\"version\":1}");

    using (var migratedData = new LocalData(migrationRoot))
    {
        var migrated = migratedData.LoadPairingState();
        Require(migrated.SchemaVersion == 3 && migrated.Devices.Count == 1 &&
            migrated.Devices[0].Id == deviceId && migrated.Devices[0].Name == "Migrated Friend PC" &&
            migrated.Devices[0].AssignedProfileIds!.SequenceEqual([profileId]) &&
            migrated.Devices[0].CanStart && !migrated.Devices[0].CanStop &&
            migrated.Devices[0].AccessExpiresUtc is null && migrated.ServerInvites.Count == 1 &&
            migrated.ServerInvites[0].Generation == generation && migrated.CredentialRenewals.Count == 1,
            "v1 pairing devices, assignment, permissions, invite lineage, or renewal receipt were lost");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(migrationRoot, "storage-schema.json")));
        Require(marker.RootElement.GetProperty("version").GetInt32() == 4,
            "the directory schema marker did not advance to v4");
        var migratedBytes = migratedData.LoadProtected("pairing-state.protected")!;
        var migratedSnapshot = JsonSerializer.Deserialize<PairingPersistentState>(migratedBytes, json);
        Require(migratedSnapshot?.SchemaVersion == 3,
            "the protected pairing snapshot did not migrate to schema v3");
        var migratedPairing = new PairingService(migratedData);
        var migratedView = migratedPairing.Views().Single();
        Require(migratedView.SaveReceiveProfileIds?.Count == 0 &&
            migratedView.SharedWorldGrants?.Count == 0,
            "legacy Receive membership silently became a v3 sharing grant");
    }

    var pairingBeforeDowngrade = File.ReadAllBytes(Path.Combine(migrationRoot, "pairing-state.protected"));
    RequireThrows<InvalidDataException>(() =>
    {
        using var _ = new LocalData(migrationRoot, 5131, supportedStorageSchemaVersion: 1);
    }, "a simulated v1 binary opened a v2 access-expiry data root");
    Require(File.ReadAllBytes(Path.Combine(migrationRoot, "pairing-state.protected"))
            .SequenceEqual(pairingBeforeDowngrade),
        "the rejected downgrade modified protected pairing data");
    RequireThrows<InvalidDataException>(() =>
    {
        using var _ = new LocalData(migrationRoot, 5131, supportedStorageSchemaVersion: 2);
    }, "a simulated v2 binary opened v3 sharing authorization state");
    RequireThrows<InvalidDataException>(() =>
    {
        using var _ = new LocalData(migrationRoot, 5131, supportedStorageSchemaVersion: 3);
    }, "a simulated v3 binary opened v4 authority fencing state");

    var interruptedRoot = Path.Combine(root, "storage-v3-interrupted-pairing-v1");
    Directory.CreateDirectory(interruptedRoot);
    File.WriteAllText(Path.Combine(interruptedRoot, "pairing-state.protected"), protectedPayload);
    File.WriteAllText(Path.Combine(interruptedRoot, "storage-schema.json"), "{\"version\":3}");
    using (var resumedData = new LocalData(interruptedRoot))
    {
        var resumed = resumedData.LoadPairingState();
        Require(resumed.SchemaVersion == 3 && resumed.Devices.Single().Id == deviceId &&
            resumed.ServerInvites.Single().Generation == generation && resumed.CredentialRenewals.Count == 1,
            "a marker-first interrupted migration did not preserve and upgrade its v1 pairing snapshot");
    }

    var newerRoot = Path.Combine(root, "storage-newer-schema");
    Directory.CreateDirectory(newerRoot);
    File.WriteAllText(Path.Combine(newerRoot, "storage-schema.json"), "{\"version\":5}");
    RequireThrows<InvalidDataException>(() =>
    {
        using var _ = new LocalData(newerRoot);
    }, "the current binary opened an unknown newer storage schema");
    Require(File.ReadAllText(Path.Combine(newerRoot, "storage-schema.json")).Contains("5", StringComparison.Ordinal),
        "newer-schema rejection rewrote the unsupported marker");
    return Task.CompletedTask;
});

await Check("shared roster separates grants, proves PC key, and rejects rollback or tampering", () =>
{
    using var data = Data("shared-governance");
    var profile = Profile("governance", "world", FreePort());
    profile.SharedSavesEnabled = true;
    data.SaveSettings(Settings(profile));
    var deviceId = Guid.NewGuid();
    var inviteGeneration = Guid.NewGuid();
    data.SavePairingState(new PairingPersistentState
    {
        Devices = [new PairedDevice
    {
        Id = deviceId, ProfileId = profile.Id, InviteGeneration = inviteGeneration,
        AssignedProfileIds = [profile.Id],
        CredentialHash = new string('A', 64), CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
        CanStart = true, CanStop = true, CanViewLogs = true
    }],
        ServerInvites = [new ServerInviteState { ProfileId = profile.Id,
        Generation = inviteGeneration, Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }]
    });
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    var pairing = new PairingService(data, clock);
    using var pc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var publicKey = Convert.ToBase64String(pc.ExportSubjectPublicKeyInfo());
    var challenges = new SharedWorldEnrollmentNonces();
    var nonce = challenges.Issue(deviceId, profile.Id);
    Require(!challenges.Consume(Guid.NewGuid(), profile.Id, nonce) &&
        !challenges.Consume(deviceId, Guid.NewGuid(), nonce) &&
        challenges.Consume(deviceId, profile.Id, nonce) &&
        !challenges.Consume(deviceId, profile.Id, nonce),
        "enrollment nonce was not device-bound and one-use");
    var basis = SharedWorldRosterTrust.EnrollmentBasis(deviceId, nonce, publicKey);
    var forged = new SharedWorldEnrollmentRequest(nonce, publicKey,
        Convert.ToBase64String(other.SignData(basis, HashAlgorithmName.SHA256)));
    Require(!pairing.BindSharedWorldKey(deviceId, forged).Ok, "a forged proof bound a PC key");
    var proof = forged with { Signature = Convert.ToBase64String(pc.SignData(basis, HashAlgorithmName.SHA256)) };
    Require(pairing.BindSharedWorldKey(deviceId, proof).Ok, "valid PC proof did not bind its key");
    var otherKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
    var changed = new SharedWorldEnrollmentRequest(nonce, otherKey, Convert.ToBase64String(other.SignData(
        SharedWorldRosterTrust.EnrollmentBasis(deviceId, nonce, otherKey), HashAlgorithmName.SHA256)));
    Require(pairing.BindSharedWorldKey(deviceId, changed).Code == "KeyReviewRequired",
        "a key change bypassed explicit owner review");
    Require(pairing.ResetSharedWorldKey(deviceId).Ok &&
        pairing.BindSharedWorldKey(deviceId, changed).Ok &&
        pairing.ResetSharedWorldKey(deviceId).Ok &&
        pairing.BindSharedWorldKey(deviceId, proof).Ok,
        "explicit owner reset did not safely replace and restore the PC signing key");
    var service = new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System));
    var roster = service.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    Require(SharedWorldRosterTrust.Verify(roster) && roster.OwnerOverride &&
        !roster.Members.Single().Grants.Receive && !roster.Members.Single().Grants.EligibleHost &&
        !roster.Members.Single().Grants.RecoveryVoter && !roster.Members.Single().Grants.ManageSharing,
        "Start/Stop/log permissions leaked into sharing grants");
    Require(pairing.SetSharedWorldGrants(deviceId, profile.Id,
        new SharedWorldGrants(EligibleHost: true, RecoveryVoter: true, ManageSharing: true)).Ok,
        "separate governance grants failed");
    var governanceOnly = service.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    Require(governanceOnly.Members.Single().Grants.EligibleHost &&
        governanceOnly.Members.Single().Grants.RecoveryVoter &&
        governanceOnly.Members.Single().Grants.ManageSharing &&
        !governanceOnly.Members.Single().Grants.Receive &&
        !pairing.AuthorizeReceiveSaves(new PairedDevice { Id = deviceId }, profile.Id, out _).Ok,
        "governance roles inherited Receive transfer access");
    Require(pairing.SetSharedWorldGrants(deviceId, profile.Id,
        new SharedWorldGrants(Receive: true)).Ok, "Receive grant failed");
    var granted = service.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    Require(granted.Revision > roster.Revision && granted.Members.Single().Grants.Receive &&
        !granted.Members.Single().Grants.EligibleHost,
        "signed roster did not record the separate Receive grant");
    var disabled = service.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id), false);
    Require(!disabled.OwnerOverride && SharedWorldRosterTrust.Verify(disabled) &&
        service.ReadRoster(profile)?.OwnerOverride == false,
        "owner override toggle was not signed");
    Require(!SharedWorldRosterTrust.Verify(disabled with { OwnerOverride = true }) &&
        !SharedWorldRosterTrust.Accept(granted, profile.Id, deviceId, publicKey,
            disabled.OwnerPublicKey, disabled.Epoch, disabled.Revision),
        "tampering or a lower roster revision was accepted");
    Require(SharedWorldRosterTrust.Accept(disabled, profile.Id, deviceId, publicKey,
        disabled.OwnerPublicKey, disabled.Epoch, disabled.Revision),
        "the current signed roster was rejected");
    Require(pairing.SetAccessExpiry(deviceId, new DeviceAccessExpiryRequest(
        AccessExpiresUtc: clock.GetUtcNow().AddMinutes(1))).Ok, "access deadline was rejected");
    Require(pairing.SetSharedWorldGrants(deviceId, profile.Id,
        new(Receive: true, EligibleHost: true, RecoveryVoter: true, ManageSharing: true)).Ok,
        "deadline-bearing governance grants failed");
    var expiring = service.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    Require(expiring.Schema == 2 && SharedWorldRosterTrust.HasRole(expiring, deviceId, publicKey,
        grants => grants.Receive, clock), "signed access deadline denied a live grant");
    clock.Advance(TimeSpan.FromMinutes(1));
    Require(!SharedWorldRosterTrust.HasRole(expiring, deviceId, publicKey,
        grants => grants.Receive, clock) &&
        !SharedWorldRosterTrust.HasRole(expiring, deviceId, publicKey,
            grants => grants.EligibleHost || grants.RecoveryVoter || grants.ManageSharing, clock),
        "an expired offline grant remained usable");
    Require(pairing.SetAccessExpiry(deviceId, new DeviceAccessExpiryRequest(Clear: true)).Ok,
        "access deadline could not be cleared");
    var restored = service.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    pairing.ConfirmSharedRosterPublished(profile.Id, restored);
    Require(!pairing.SharedRosterDirty(profile.Id), "publication did not clear the durable repair marker");
    Require(pairing.SetServerAccess(deviceId, [], null, [profile.Id]).Ok,
        "server unassignment failed");
    Require(pairing.SharedRosterDirty(profile.Id), "unassignment did not block sharing pending publication");
    var unassigned = service.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    Require(unassigned.Revision > restored.Revision && unassigned.Members.All(member => member.DeviceId != deviceId),
        "unassignment remained in the signed roster");
    pairing.ConfirmSharedRosterPublished(profile.Id, unassigned);
    Require(pairing.SetServerAccess(deviceId, [profile.Id], null, [profile.Id]).Ok,
        "server reassignment failed");
    Require(pairing.SetSharedWorldGrants(deviceId, profile.Id, new(Receive: true)).Ok,
        "reassigned Receive grant failed");
    var reassigned = service.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    pairing.ConfirmSharedRosterPublished(profile.Id, reassigned);
    var activeRosterPath = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
        reassigned.GroupId.ToString("N") + ".roster.json");
    using (var locked = new FileStream(activeRosterPath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        Require(pairing.Revoke(deviceId).Ok && pairing.SharedRosterDirty(profile.Id),
            "revocation did not persist a repair marker");
        RequireThrows<IOException>(() => service.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id)),
            "a locked roster unexpectedly allowed publication");
    }
    var restartedPairing = new PairingService(data, clock);
    Require(restartedPairing.SharedRosterDirty(profile.Id), "restart lost the publication failure marker");
    var revokedAfterRestart = service.PublishRoster(profile, restartedPairing.SharedRosterMembers(profile.Id));
    Require(revokedAfterRestart.Members.Single().Revoked,
        "emergency revocation was not recorded in the signed roster");
    restartedPairing.ConfirmSharedRosterPublished(profile.Id, revokedAfterRestart);
    Require(!restartedPairing.SharedRosterDirty(profile.Id), "repair did not clear the marker");
    Require(!SharedWorldRosterTrust.Accept(revokedAfterRestart, profile.Id, deviceId, publicKey,
            revokedAfterRestart.OwnerPublicKey, disabled.Epoch, disabled.Revision),
        "revocation did not block new reads");
    var rosterPath = activeRosterPath;
    File.WriteAllBytes(rosterPath, JsonSerializer.SerializeToUtf8Bytes(revokedAfterRestart with { OwnerOverride = true }));
    RequireThrows<InvalidDataException>(() => service.ReadRoster(profile),
        "a tampered persisted roster was loaded");
    return Task.CompletedTask;
});

await Check("delegated roster chain enforces owner root, limited grants, and conflict fence", () =>
{
    using var data = Data("delegated-roster-chain");
    using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var delegateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var targetKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var ownerPublic = Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo());
    var delegatePublic = Convert.ToBase64String(delegateKey.ExportSubjectPublicKeyInfo());
    var targetPublic = Convert.ToBase64String(targetKey.ExportSubjectPublicKeyInfo());
    var profileId = Guid.NewGuid();
    var groupId = Guid.NewGuid();
    var delegateId = Guid.NewGuid();
    var targetId = Guid.NewGuid();
    SharedWorldRoster Sign(SharedWorldRoster draft, ECDsa signer) => draft with
    {
        Signature = Convert.ToBase64String(signer.SignData(
        SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
    };
    var initialMembers = new SharedWorldRosterMember[]
    {
        new(delegateId, delegatePublic, new SharedWorldGrants(ManageSharing: true), false),
        new(targetId, targetPublic, new SharedWorldGrants(), false)
    };
    var legacy = Sign(new SharedWorldRoster(2, groupId, profileId, 1, 1, true,
        ownerPublic, initialMembers, ""), owner);
    Require(SharedWorldRosterTrust.Verify(legacy), "schema2 owner root stopped verifying");
    var legacyV1 = Sign(new SharedWorldRoster(1, Guid.NewGuid(), Guid.NewGuid(), 1, 1,
        true, ownerPublic, initialMembers, ""), owner);
    using (var olderData = Data("delegated-roster-v1-root"))
    {
        var olderStore = new SharedWorldRosterChainStore(olderData);
        olderStore.Append(legacyV1, ownerPublic);
        Require(SharedWorldRosterTrust.Verify(legacyV1) &&
            new SharedWorldRosterChainStore(olderData).Heads(legacyV1.ProfileId).Single().Schema == 1,
            "schema1 signed roster stopped reading as a trusted owner root");
    }
    var chain = new SharedWorldRosterChainStore(data);
    chain.Append(legacy, ownerPublic);
    var changed = initialMembers.Select(member => member.DeviceId == targetId
        ? member with
        {
            Grants = new SharedWorldGrants(Receive: true, EligibleHost: true,
            RecoveryVoter: true)
        } : member).ToArray();
    var delegated = Sign(new SharedWorldRoster(3, groupId, profileId, 2, 2, true,
        ownerPublic, changed, "", SharedWorldRosterTrust.Hash(legacy), delegateId,
        delegatePublic), delegateKey);
    Require(!SharedWorldRosterTrust.Verify(delegated) &&
        SharedWorldRosterTrust.VerifyRevision(delegated, legacy, ownerPublic,
            DateTimeOffset.UtcNow, true),
        "delegated revision was independently trusted or exact parent was rejected");
    var wrongParent = Sign(delegated with
    { PreviousRosterHash = new string('0', 64), Signature = "" }, delegateKey);
    Require(!SharedWorldRosterTrust.VerifyRevision(wrongParent, legacy, ownerPublic,
        DateTimeOffset.UtcNow, true), "delegated revision accepted a changed parent hash");
    chain.Append(delegated, ownerPublic);
    Require(new SharedWorldRosterChainStore(data).Heads(profileId).Single().Signature ==
        delegated.Signature, "restart lost the delegated signed head");
    var selfChange = Sign(delegated with
    {
        Epoch = 3,
        Revision = 3,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(delegated),
        Members = [changed[0] with { Revoked = true }, changed[1]],
        Signature = ""
    }, delegateKey);
    RequireThrows<InvalidDataException>(() => chain.Append(selfChange, ownerPublic),
        "delegate revoked its own authority");
    var overrideChange = Sign(delegated with
    {
        Epoch = 3,
        Revision = 3,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(delegated),
        OwnerOverride = false,
        Signature = ""
    }, delegateKey);
    RequireThrows<InvalidDataException>(() => chain.Append(overrideChange, ownerPublic),
        "delegate changed owner override");
    var manageChange = Sign(delegated with
    {
        Epoch = 3,
        Revision = 3,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(delegated),
        Members = [changed[0], changed[1] with
        { Grants = changed[1].Grants with { ManageSharing = true } }],
        Signature = ""
    }, delegateKey);
    RequireThrows<InvalidDataException>(() => chain.Append(manageChange, ownerPublic),
        "delegate granted sharing management");
    var coManagerRoot = Sign(legacy with
    {
        Members = [initialMembers[0],
        initialMembers[1] with { Grants = new(ManageSharing: true) }],
        Signature = ""
    }, owner);
    var coManagerRevoke = Sign(delegated with
    {
        PreviousRosterHash =
        SharedWorldRosterTrust.Hash(coManagerRoot),
        Members = [coManagerRoot.Members[0], coManagerRoot.Members[1] with { Revoked = true }],
        Signature = ""
    }, delegateKey);
    Require(!SharedWorldRosterTrust.VerifyRevision(coManagerRevoke, coManagerRoot,
        ownerPublic, DateTimeOffset.UtcNow, true),
        "delegate revoked another owner-appointed sharing manager");
    var revoked = Sign(delegated with
    {
        Epoch = 3,
        Revision = 3,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(delegated),
        Members = [changed[0] with { Revoked = true }, changed[1]],
        SignerDeviceId = Guid.Empty,
        SignerPublicKey = ownerPublic,
        Signature = ""
    }, owner);
    var ownerControls = Sign(revoked with
    {
        OwnerOverride = false,
        Members = [changed[0] with { Revoked = true }, changed[1] with
        { Grants = changed[1].Grants with { ManageSharing = true } }],
        Signature = ""
    }, owner);
    Require(SharedWorldRosterTrust.VerifyRevision(ownerControls, delegated, ownerPublic,
        DateTimeOffset.UtcNow, true), "owner could not change owner-only sharing controls");
    chain.Append(revoked, ownerPublic, stopAfterPendingForChecks: true);
    Require(new SharedWorldRosterChainStore(data).Heads(profileId).Single().Signature ==
        revoked.Signature, "restart did not finish a protected pending roster revision");
    var revokedDelegate = Sign(delegated with
    {
        Epoch = 4,
        Revision = 4,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(revoked),
        Signature = ""
    }, delegateKey);
    RequireThrows<InvalidDataException>(() => chain.Append(revokedDelegate, ownerPublic),
        "revoked delegate signed a new revision");
    using (var expiredData = Data("delegated-roster-expired"))
    {
        var expiredRoot = Sign(legacy with
        {
            GroupId = Guid.NewGuid(),
            ProfileId = Guid.NewGuid(),
            Signature = "",
            Members = [initialMembers[0] with
            { AccessExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, initialMembers[1]]
        }, owner);
        var expiredStore = new SharedWorldRosterChainStore(expiredData);
        expiredStore.Append(expiredRoot, ownerPublic);
        var expiredChild = Sign(delegated with
        {
            GroupId = expiredRoot.GroupId,
            ProfileId = expiredRoot.ProfileId,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(expiredRoot),
            Members = [expiredRoot.Members[0], changed[1]],
            Signature = ""
        }, delegateKey);
        RequireThrows<InvalidDataException>(() => expiredStore.Append(expiredChild, ownerPublic),
            "expired sharing manager advanced the roster");
    }
    SharedWorldRoster OwnerSibling(bool overrideValue) => Sign(revoked with
    {
        Epoch = 4,
        Revision = 4,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(revoked),
        OwnerOverride = overrideValue,
        Signature = ""
    }, owner);
    var firstSibling = OwnerSibling(false);
    var secondSibling = OwnerSibling(true);
    chain.Append(firstSibling, ownerPublic, stopAfterFileForChecks: true);
    Require(new SharedWorldRosterChainStore(data).Heads(profileId).Single().Signature ==
        firstSibling.Signature, "restart did not finish a written roster revision");
    chain.Append(secondSibling, ownerPublic);
    Require(new SharedWorldRosterChainStore(data).Heads(profileId).Count == 2,
        "competing same-revision signed rosters were silently selected");
    var afterConflict = Sign(firstSibling with
    {
        Epoch = 5,
        Revision = 5,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(firstSibling),
        Signature = ""
    }, owner);
    RequireThrows<InvalidDataException>(() => chain.Append(afterConflict, ownerPublic),
        "governance advanced past competing sibling revisions");
    var ownerProfile = Profile("delegated-roster-owner", "delegated-world", FreePort());
    ownerProfile.Id = profileId;
    var ownerShares = new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System));
    RequireThrows<InvalidDataException>(() => ownerShares.PublishRoster(ownerProfile, initialMembers),
        "legacy owner publication bypassed the roster chain conflict");
    var revisionPath = Path.Combine(data.RootPath, "shared-worlds", profileId.ToString("N"),
        "roster-chain", SharedWorldRosterTrust.Hash(delegated) + ".json");
    File.AppendAllText(revisionPath, "tampered");
    RequireThrows<InvalidDataException>(() => new SharedWorldRosterChainStore(data).Read(profileId),
        "tampered delegated history survived restart");
    return Task.CompletedTask;
});

await Check("paged roster retains 270 owner and delegated revisions with offline catch-up", () =>
{
    using var host = Data("paged-roster-host");
    using var receiver = Data("paged-roster-receiver");
    using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var manager = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var target = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var ownerPublic = Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo());
    var managerPublic = Convert.ToBase64String(manager.ExportSubjectPublicKeyInfo());
    var targetPublic = Convert.ToBase64String(target.ExportSubjectPublicKeyInfo());
    var profileId = Guid.NewGuid();
    var groupId = Guid.NewGuid();
    var managerId = Guid.NewGuid();
    var targetId = Guid.NewGuid();
    var members = new SharedWorldRosterMember[]
    {
        new(managerId, managerPublic, new(ManageSharing: true), false),
        new(targetId, targetPublic, new(), false)
    };
    SharedWorldRoster Sign(SharedWorldRoster draft, ECDsa signer) => draft with
    {
        Signature = Convert.ToBase64String(signer.SignData(
            SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
    };
    var roster = Sign(new SharedWorldRoster(3, groupId, profileId, 1, 1, true,
        ownerPublic, members, "", SignerDeviceId: Guid.Empty,
        SignerPublicKey: ownerPublic), owner);
    var chain = new SharedWorldRosterChainStore(host);
    var received = new SharedWorldRosterChainStore(receiver);
    SharedWorldRoster? historic = null;
    chain.Append(roster, ownerPublic);
    received.Append(roster, ownerPublic);
    for (var n = 2; n <= 270; n++)
    {
        var delegated = n % 2 == 0;
        var nextMembers = n == 270 ?
            new SharedWorldRosterMember[] { members[0] with { Revoked = true }, members[1] } : members;
        var draft = roster with
        {
            Epoch = n,
            Revision = n,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(roster),
            SignerDeviceId = delegated && n != 270 ? managerId : Guid.Empty,
            SignerPublicKey = delegated && n != 270 ? managerPublic : ownerPublic,
            Members = nextMembers,
            Signature = ""
        };
        roster = Sign(draft, delegated && n != 270 ? manager : owner);
        chain.Append(roster, ownerPublic);
        if (n == 135) historic = roster;
        if (n <= 8) received.Append(roster, ownerPublic);
        members = nextMembers;
    }
    Require(chain.Count(profileId) == 270 && chain.ReadPage(profileId, 0).Count ==
        SharedWorldRosterChainStore.PageSize && chain.ReadPage(profileId, 269).Count == 1,
        "roster page size or count changed");
    var floorName = $"shared-roster-chain-{profileId:N}.protected";
    var floorBytes = host.LoadProtected(floorName)!;
    Require(floorBytes.Length < 2048 &&
        JsonDocument.Parse(floorBytes).RootElement.GetProperty("schema").GetInt32() == 2,
        "protected roster floor grew with the journal");
    var finalHash = SharedWorldRosterTrust.Hash(roster);
    var incomplete = received.Heads(profileId).Single();
    Require(SharedWorldRosterTrust.Hash(incomplete) != finalHash,
        "a truncated offline catch-up looked complete");
    for (long offset = received.Count(profileId); ;)
    {
        var page = chain.ReadPage(profileId, offset);
        foreach (var revision in page) received.Append(revision, ownerPublic);
        offset += page.Count;
        if (page.Count < SharedWorldRosterChainStore.PageSize) break;
    }
    Require(received.Count(profileId) == 270 &&
        SharedWorldRosterTrust.Hash(received.Heads(profileId).Single()) == finalHash &&
        new SharedWorldRosterChainStore(receiver).Read(profileId).Count == 270,
        "offline receiver did not accept the complete signed history");
    Require(historic is not null && chain.Contains(profileId, historic),
        "protected historical roster lookup was lost after 270 revisions");
    var transferHealth = new SharedWorldTransferHealth();
    transferHealth.Complete(profileId, transferHealth.Begin(profileId),
        new(false, "RosterCatchUpPending", "Signed membership is catching up."));
    Require(transferHealth.Issue(profileId) is null,
        "normal roster catch-up was reported as a stalled save transfer");
    var lookupName = $"shared-roster-lookup-{profileId:N}-{SharedWorldRosterTrust.Hash(historic!)}.protected";
    var lookupBytes = host.LoadProtected(lookupName)!;
    host.DeleteProtected(lookupName);
    Require(!new SharedWorldRosterChainStore(host).Contains(profileId, historic!),
        "authority accepted a historical roster without its protected lookup");
    host.SaveProtected(lookupName, lookupBytes);
    var rejected = Sign(roster with
    {
        Epoch = 271,
        Revision = 271,
        PreviousRosterHash = finalHash,
        SignerDeviceId = managerId,
        SignerPublicKey = managerPublic,
        Signature = ""
    }, manager);
    RequireThrows<InvalidDataException>(() => chain.Append(rejected, ownerPublic),
        "revoked delegate advanced the roster");
    var pending = Sign(rejected with
    { SignerDeviceId = Guid.Empty, SignerPublicKey = ownerPublic, Signature = "" }, owner);
    chain.Append(pending, ownerPublic, stopAfterPendingForChecks: true);
    Require(new SharedWorldRosterChainStore(host).Count(profileId) == 271,
        "pending roster commit did not recover idempotently");
    var second = Sign(pending with
    {
        Epoch = 272,
        Revision = 272,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(pending),
        Signature = ""
    }, owner);
    chain.Append(second, ownerPublic, stopAfterFileForChecks: true);
    Require(new SharedWorldRosterChainStore(host).Read(profileId).Count == 272,
        "written roster commit did not recover idempotently");
    var oldFloor = host.LoadProtected(floorName)!;
    var final = Sign(second with
    {
        Epoch = 273,
        Revision = 273,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(second),
        Signature = ""
    }, owner);
    chain.Append(final, ownerPublic);
    var finalFloor = host.LoadProtected(floorName)!;
    host.SaveProtected(floorName, oldFloor);
    RequireThrows<InvalidDataException>(() => new SharedWorldRosterChainStore(host).Read(profileId),
        "protected floor rollback accepted an extra revision file");
    host.SaveProtected(floorName, finalFloor);
    var revisionPath = Path.Combine(host.RootPath, "shared-worlds", profileId.ToString("N"),
        "roster-chain", SharedWorldRosterTrust.Hash(roster) + ".json");
    var original = File.ReadAllBytes(revisionPath);
    File.AppendAllText(revisionPath, "tamper");
    RequireThrows<InvalidDataException>(() => new SharedWorldRosterChainStore(host).Read(profileId),
        "historic roster tampering survived validation");
    File.WriteAllBytes(revisionPath, original);
    File.Delete(revisionPath);
    RequireThrows<InvalidDataException>(() => new SharedWorldRosterChainStore(host).Read(profileId),
        "missing historic roster revision survived validation");
    File.WriteAllBytes(revisionPath, original);
    Require(new SharedWorldRosterChainStore(host).Read(profileId).Count == 273,
        "restored valid roster journal could not be read");
    return Task.CompletedTask;
});

await Check("schema-1 roster floor migrates with a pending signed revision", () =>
{
    using var data = Data("roster-floor-migration");
    using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var ownerPublic = Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo());
    var id = Guid.NewGuid();
    var group = Guid.NewGuid();
    SharedWorldRoster Sign(SharedWorldRoster draft) => draft with
    {
        Signature = Convert.ToBase64String(owner.SignData(
            SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
    };
    var rootRoster = Sign(new SharedWorldRoster(2, group, id, 1, 1, true,
        ownerPublic, [], ""));
    var chain = new SharedWorldRosterChainStore(data);
    chain.Append(rootRoster, ownerPublic);
    var rootHash = SharedWorldRosterTrust.Hash(rootRoster);
    data.DeleteProtected($"shared-roster-lookup-{id:N}-{rootHash}.protected");
    var oldFloor = new
    {
        Schema = 1,
        ProfileId = id,
        GroupId = group,
        OwnerPublicKey = ownerPublic,
        Hashes = new[] { rootHash }
    };
    var floorBytes = JsonSerializer.SerializeToUtf8Bytes(oldFloor,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var next = Sign(rootRoster with
    {
        Schema = 3,
        Epoch = 2,
        Revision = 2,
        PreviousRosterHash = rootHash,
        SignerDeviceId = Guid.Empty,
        SignerPublicKey = ownerPublic,
        Signature = ""
    });
    var pending = new
    {
        Schema = 1,
        OldFloorHash = Convert.ToHexString(SHA256.HashData(floorBytes)),
        RosterHash = SharedWorldRosterTrust.Hash(next),
        Roster = next
    };
    data.SaveProtected($"shared-roster-chain-{id:N}.protected", floorBytes);
    data.SaveProtected($"shared-roster-pending-{id:N}.protected",
        JsonSerializer.SerializeToUtf8Bytes(pending, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    Require(new SharedWorldRosterChainStore(data).Read(id).Count == 2 &&
        new SharedWorldRosterChainStore(data).Heads(id).Single().Signature == next.Signature &&
        JsonDocument.Parse(data.LoadProtected($"shared-roster-chain-{id:N}.protected")!)
            .RootElement.GetProperty("schema").GetInt32() == 2,
        "legacy pending roster did not migrate and commit idempotently");
    return Task.CompletedTask;
});

await Check("companion roster revisions use bounded TestServer pages", async () =>
{
    using var data = Data("roster-pages-testserver");
    var profile = Profile("roster-pages", "roster-pages-world", FreePort());
    profile.Kind = "Fixture";
    profile.SharedSavesEnabled = true;
    var port = 51388;
    var address = $"https://127.0.0.1:{port}";
    var settings = Settings(profile);
    settings.CompanionEndpoint = address;
    settings.CompanionPort = port;
    settings.CompanionBindAddress = "127.0.0.1";
    settings.CompanionListeningEnabled = true;
    data.SaveSettings(settings);
    using var certificate = new HostIdentity(data).Ensure(address);
    using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var deviceId = Guid.NewGuid();
    var bearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var devicePublic = Convert.ToBase64String(deviceKey.ExportSubjectPublicKeyInfo());
    data.SavePairingState(new PairingPersistentState
    {
        Devices = [new PairedDevice
        {
            Id = deviceId, AssignedProfileIds = [profile.Id],
            CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer))),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            SharedWorldPublicKey = devicePublic,
            SharedWorldGrants = new() { [profile.Id] = new(Receive: true) }
        }]
    });
    var pairing = new PairingService(data);
    var manager = new HostManager(data, Games(data));
    var roster = await manager.PublishSharedWorldRosterAsync(profile.Id,
        [new(deviceId, devicePublic, new(Receive: true), false)]);
    pairing.ConfirmSharedRosterPublished(profile.Id, roster);
    var chain = new SharedWorldRosterChainStore(data);
    chain.Append(roster, roster.OwnerPublicKey);
    using var owner = ECDsa.Create();
    owner.ImportPkcs8PrivateKey(data.LoadProtected("shared-world-signing-key.protected")!, out _);
    for (var n = 2; n <= 270; n++)
    {
        var draft = roster with
        {
            Schema = 3,
            Epoch = n,
            Revision = n,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(roster),
            SignerDeviceId = Guid.Empty,
            SignerPublicKey = roster.OwnerPublicKey,
            Signature = ""
        };
        roster = draft with
        {
            Signature = Convert.ToBase64String(owner.SignData(
                SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
        };
        chain.Append(roster, roster.OwnerPublicKey);
    }
    using var gate = new SemaphoreSlim(1, 1);
    var listener = new CompanionServer(data, manager, pairing, Games(data),
        new ServerLogService(data, manager), gate, port + 2);
    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    builder.WebHost.UseTestServer();
    await using var app = listener.BuildInMemoryApp(builder, settings, new Uri(address));
    await app.StartAsync();
    using var client = app.GetTestServer().CreateClient();
    client.BaseAddress = new Uri(address + "/");
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
    client.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
    var path = $"api/companion/servers/{profile.Id}/shared-world/roster/revisions";
    foreach (var (offset, expected) in new[] { (0, 12), (12, 12), (264, 6), (270, 0) })
    {
        using var response = await client.GetAsync(path + $"?offset={offset}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var page = JsonSerializer.Deserialize<List<SharedWorldRoster>>(bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Require(response.IsSuccessStatusCode && bytes.Length <= 4 * 1024 * 1024 &&
            page?.Count == expected, "companion returned an unbounded or incorrect roster page");
    }
    using var currentResponse = await client.GetAsync(path + "/current");
    var current = await currentResponse.Content.ReadFromJsonAsync<SharedWorldRoster>();
    Require(currentResponse.IsSuccessStatusCode && current?.Signature == roster.Signature,
        "separate current roster did not match the signed journal head");
    using var invalid = await client.GetAsync(path + "?offset=-1");
    Require(invalid.StatusCode == HttpStatusCode.BadRequest, "negative roster offset was accepted");
    using var receiver = Data("roster-pages-offline-friend");
    var receiverChain = new SharedWorldRosterChainStore(receiver);
    foreach (var revision in chain.ReadPage(profile.Id, 0).Take(8))
        receiverChain.Append(revision, roster.OwnerPublicKey);
    var localHead = receiverChain.Heads(profile.Id).Single();
    receiver.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected",
        deviceKey.ExportPkcs8PrivateKey());
    receiver.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(
        new FriendConfiguration
        {
            Endpoint = address,
            Fingerprint = HostIdentity.Fingerprint(certificate),
            DeviceId = deviceId,
            Credential = bearer,
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            SharedWorldSigningKeys = new() { [profile.Id] = roster.OwnerPublicKey },
            SharedRosterFloors = new()
            {
                [profile.Id] = new(localHead.GroupId,
                localHead.Epoch, localHead.Revision, localHead.Signature)
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    using var friend = new FriendLink(receiver, "friend.protected", (_, _) =>
    {
        var peer = app.GetTestServer().CreateClient();
        peer.BaseAddress = new Uri(address + "/");
        return peer;
    });
    var friendView = await friend.PollAsync();
    Require(friendView.Profiles.Any(item => item.Id == profile.Id),
        "offline Friend did not establish its saved Host connection");
    SharedWorldSharingView? sharing = null;
    for (var attempt = 0; attempt < 7; attempt++)
    {
        var before = receiverChain.Count(profile.Id);
        sharing = await friend.CheckSharedWorldSharingAsync(profile.Id);
        var after = receiverChain.Count(profile.Id);
        Require(after - before <= 4 * SharedWorldRosterChainStore.PageSize,
            "Friend caught up more than four signed pages in one attempt");
        if (sharing.Available) break;
        Require(sharing.Code == "RosterCatchUpPending" && after > before,
            "Friend did not make bounded progress while catching up");
    }
    Require(sharing is { Available: true } && sharing.Revision == roster.Revision &&
        new SharedWorldRosterChainStore(receiver).Count(profile.Id) == 270 &&
        new SharedWorldRosterChainStore(receiver).Heads(profile.Id).Single().Signature == roster.Signature,
        "offline Friend did not catch up from its protected local floor");
    using var truncatedData = Data("roster-pages-truncated-friend");
    var truncatedChain = new SharedWorldRosterChainStore(truncatedData);
    foreach (var revision in chain.ReadPage(profile.Id, 0).Take(8))
        truncatedChain.Append(revision, roster.OwnerPublicKey);
    truncatedData.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected",
        deviceKey.ExportPkcs8PrivateKey());
    truncatedData.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(
        new FriendConfiguration
        {
            Endpoint = address,
            Fingerprint = HostIdentity.Fingerprint(certificate),
            DeviceId = deviceId,
            Credential = bearer,
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            SharedWorldSigningKeys = new() { [profile.Id] = roster.OwnerPublicKey },
            SharedRosterFloors = new()
            {
                [profile.Id] = new(localHead.GroupId,
                localHead.Epoch, localHead.Revision, localHead.Signature)
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    using var truncatedFriend = new FriendLink(truncatedData, "friend.protected", (_, _) =>
    {
        var peer = new HttpClient(new TruncatedRosterHandler(app.GetTestServer().CreateHandler()))
        { BaseAddress = new Uri(address + "/") };
        return peer;
    });
    Require((await truncatedFriend.PollAsync()).Profiles.Any(item => item.Id == profile.Id),
        "truncated-page Friend did not establish its saved Host connection");
    var truncated = await truncatedFriend.CheckSharedWorldSharingAsync(profile.Id);
    Require(!truncated.Available && truncated.Code == "RosterRejected" &&
        new SharedWorldRosterChainStore(truncatedData).Count(profile.Id) == 8,
        "Friend accepted a truncated page as the current signed head");
});

await Check("offline Friend accepts a Host-attested pre-expiry delegation", () =>
{
    using var data = Data("delegation-expiry-reconnect");
    using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var delegateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var start = DateTimeOffset.UtcNow;
    var clock = new ManualTimeProvider(start);
    var ownerPublic = Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo());
    var delegatePublic = Convert.ToBase64String(delegateKey.ExportSubjectPublicKeyInfo());
    var profileId = Guid.NewGuid();
    var deviceId = Guid.NewGuid();
    var rootDraft = new SharedWorldRoster(2, Guid.NewGuid(), profileId, 1, 1, true,
        ownerPublic, [new(deviceId, delegatePublic,
            new(ManageSharing: true), false, start.AddMinutes(1))], "");
    var root = rootDraft with
    {
        Signature = Convert.ToBase64String(owner.SignData(
        SharedWorldRosterTrust.Basis(rootDraft), HashAlgorithmName.SHA256))
    };
    var childDraft = root with
    {
        Schema = 3,
        Epoch = 2,
        Revision = 2,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(root),
        SignerDeviceId = deviceId,
        SignerPublicKey = delegatePublic,
        Signature = ""
    };
    var child = childDraft with
    {
        Signature = Convert.ToBase64String(delegateKey.SignData(
        SharedWorldRosterTrust.Basis(childDraft), HashAlgorithmName.SHA256))
    };
    var acceptedAt = start.AddSeconds(20);
    var accepted = child with
    {
        HostAcceptedUtc = acceptedAt,
        HostAcceptanceSignature = Convert.ToBase64String(owner.SignData(
            SharedWorldRosterTrust.HostAcceptanceBasis(child, acceptedAt), HashAlgorithmName.SHA256))
    };
    clock.Advance(TimeSpan.FromMinutes(2));
    var chain = new SharedWorldRosterChainStore(data, clock);
    chain.Append(root, ownerPublic);
    RequireThrows<InvalidDataException>(() => chain.Append(child, ownerPublic),
        "an expired unstamped delegation was accepted on first reconnect");
    RequireThrows<InvalidDataException>(() => chain.Append(accepted with
    { HostAcceptanceSignature = child.Signature }, ownerPublic),
        "a forged Host acceptance was trusted");
    chain.Append(accepted, ownerPublic);
    Require(new SharedWorldRosterChainStore(data, clock).Heads(profileId).Single().Signature ==
        child.Signature, "a signed pre-expiry publication was lost after reconnect");
    return Task.CompletedTask;
});

await Check("delegated publication serves current permissions and owner can revoke", async () =>
{
    using var host = Data("delegate-publication-host");
    using var receiver = Data("delegate-publication-friend");
    var profile = Profile("delegate-publication", "delegate-world", FreePort());
    profile.SharedSavesEnabled = true;
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    var manager = new HostManager(host,
        new GameServerRegistry([new ObservationFixtureDriver()], PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "Host fixture setup failed");
    using var delegateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var targetKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var delegateId = Guid.NewGuid();
    var targetId = Guid.NewGuid();
    var delegatePublic = Convert.ToBase64String(delegateKey.ExportSubjectPublicKeyInfo());
    var targetPublic = Convert.ToBase64String(targetKey.ExportSubjectPublicKeyInfo());
    var members = new SharedWorldRosterMember[]
    {
        new(delegateId, delegatePublic, new SharedWorldGrants(ManageSharing: true), false),
        new(targetId, targetPublic, new SharedWorldGrants(), false)
    };
    var root = await manager.PublishSharedWorldRosterAsync(profile.Id, members);
    var draft = root with
    {
        Schema = 3,
        Epoch = root.Epoch + 1,
        Revision = root.Revision + 1,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(root),
        SignerDeviceId = delegateId,
        SignerPublicKey = delegatePublic,
        Members = members.Select(item => item.DeviceId == targetId
            ? item with { Grants = item.Grants with { Receive = true, EligibleHost = true } } : item).ToArray(),
        Signature = ""
    };
    var revision = draft with
    {
        Signature = Convert.ToBase64String(delegateKey.SignData(
        SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
    };
    RequireThrows<InvalidDataException>(() => manager.PublishDelegatedRosterAsync(profile.Id,
        targetId, targetPublic, revision).GetAwaiter().GetResult(),
        "a different authenticated PC published the delegate's revision");
    RequireThrows<InvalidDataException>(() => manager.PublishDelegatedRosterAsync(profile.Id,
        delegateId, delegatePublic, revision, () => false).GetAwaiter().GetResult(),
        "a revoked transport credential published during the serialized check");
    var published = await manager.PublishDelegatedRosterAsync(profile.Id,
        delegateId, delegatePublic, revision);
    Require(published.Signature == revision.Signature &&
        (await manager.SharedWorldRosterAsync(profile.Id))?.Signature == revision.Signature &&
        (await manager.SharedWorldRosterHistoryAsync(profile.Id))?.Count == 2,
        "delegated permissions were not the served current roster");
    var friendChain = new SharedWorldRosterChainStore(receiver);
    foreach (var item in (await manager.SharedWorldRosterHistoryAsync(profile.Id))!)
        friendChain.Append(item, root.OwnerPublicKey);
    Require(new SharedWorldRosterChainStore(receiver).Heads(profile.Id).Single().Members
        .Single(item => item.DeviceId == targetId).Grants.Receive,
        "a receiving PC did not retain the signed permission change");
    var ownerChanged = await manager.PublishSharedWorldRosterAsync(profile.Id,
        [members[0] with { Revoked = true }, members[1]], ownerOverride: false);
    Require(ownerChanged.Schema == 3 && !ownerChanged.OwnerOverride &&
        ownerChanged.Members.Single(item => item.DeviceId == delegateId).Revoked,
        "the owner could not revoke the delegate and change owner-only control");
    var rejectedDraft = ownerChanged with
    {
        Epoch = ownerChanged.Epoch + 1,
        Revision = ownerChanged.Revision + 1,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(ownerChanged),
        SignerDeviceId = delegateId,
        SignerPublicKey = delegatePublic,
        Signature = ""
    };
    var rejected = rejectedDraft with
    {
        Signature = Convert.ToBase64String(delegateKey.SignData(
        SharedWorldRosterTrust.Basis(rejectedDraft), HashAlgorithmName.SHA256))
    };
    RequireThrows<InvalidDataException>(() => manager.PublishDelegatedRosterAsync(profile.Id,
        delegateId, delegatePublic, rejected).GetAwaiter().GetResult(),
        "revoked delegate published another revision");
    Require((await manager.SharedWorldRosterHistoryAsync(profile.Id))?.Count == 3,
        "owner revocation did not propagate in signed history");
});

await Check("owner edit preserves a delegated revoke across repair and restart", async () =>
{
    using var data = Data("delegated-owner-delta");
    var profile = Profile("owner-delta", "owner-delta-world", FreePort());
    profile.SharedSavesEnabled = true;
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    var games = new GameServerRegistry([new ObservationFixtureDriver()], PortProbeMode.LoopbackOnly);
    var manager = new HostManager(data, games);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "Host setup failed");
    using var delegateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var aKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var bKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var delegateId = Guid.NewGuid();
    var aId = Guid.NewGuid();
    var bId = Guid.NewGuid();
    var delegatePublic = Convert.ToBase64String(delegateKey.ExportSubjectPublicKeyInfo());
    var local = new SharedWorldRosterMember[]
    {
        new(delegateId, delegatePublic, new(ManageSharing: true), false),
        new(aId, Convert.ToBase64String(aKey.ExportSubjectPublicKeyInfo()), new(Receive: true), false),
        new(bId, Convert.ToBase64String(bKey.ExportSubjectPublicKeyInfo()), new(), false)
    };
    var root = await manager.PublishSharedWorldRosterAsync(profile.Id, local);
    var draft = root with
    {
        Schema = 3,
        Epoch = root.Epoch + 1,
        Revision = root.Revision + 1,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(root),
        SignerDeviceId = delegateId,
        SignerPublicKey = delegatePublic,
        Members = local.Select(item => item.DeviceId == aId ? item with { Revoked = true } : item).ToArray(),
        Signature = ""
    };
    var revoke = draft with
    {
        Signature = Convert.ToBase64String(delegateKey.SignData(
        SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
    };
    await manager.PublishDelegatedRosterAsync(profile.Id, delegateId, delegatePublic, revoke);
    var inviteGeneration = Guid.NewGuid();
    data.SavePairingState(new PairingPersistentState
    {
        Devices = local.Select(item => new PairedDevice
        {
            Id = item.DeviceId,
            ProfileId = profile.Id,
            InviteGeneration = inviteGeneration,
            AssignedProfileIds = [profile.Id],
            SharedWorldPublicKey = item.PublicKey,
            SharedWorldGrants = new Dictionary<Guid, SharedWorldGrants> { [profile.Id] = item.Grants },
            CredentialHash = new string('A', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
        }).ToList(),
        ServerInvites = [new ServerInviteState
    { ProfileId = profile.Id, Generation = inviteGeneration,
        Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }]
    });
    var pairing = new PairingService(data);
    var editedB = pairing.SetSharedWorldGrants(bId, profile.Id, new(Receive: true));
    Require(editedB.Ok, "owner could not edit B's local grant: " + editedB.Code);
    var ownerLocal = pairing.SharedRosterMembers(profile.Id);
    var ownerB = await manager.PublishSharedWorldRosterAsync(profile.Id, ownerLocal,
        ownerEdit: new(bId, Grants: ownerLocal.Single(item => item.DeviceId == bId).Grants));
    pairing.ConfirmSharedRosterPublished(profile.Id, ownerB);
    Require(ownerB.Members.Single(item => item.DeviceId == aId).Revoked &&
        ownerB.Members.Single(item => item.DeviceId == bId).Grants.Receive,
        "editing B silently restored A's delegated revoke");
    var restarted = new HostManager(data, games);
    var restartedPairing = new PairingService(data);
    restartedPairing.RequireSharedRosterPublication(profile.Id);
    var repaired = await restarted.PublishSharedWorldRosterAsync(profile.Id,
        restartedPairing.SharedRosterMembers(profile.Id));
    restartedPairing.ConfirmSharedRosterPublished(profile.Id, repaired);
    Require(repaired.Signature == ownerB.Signature &&
        repaired.Members.Single(item => item.DeviceId == aId).Revoked,
        "interrupted confirmation or restart restored A from stale local grants");
    var regranted = await restarted.PublishSharedWorldRosterAsync(profile.Id, ownerLocal,
        ownerEdit: new(aId, Receive: true, Revoked: false));
    restartedPairing.ConfirmSharedRosterPublished(profile.Id, regranted);
    Require(!regranted.Members.Single(item => item.DeviceId == aId).Revoked &&
        regranted.Members.Single(item => item.DeviceId == aId).Grants.Receive,
        "the owner could not intentionally regrant A");
    var matchingDraft = regranted with
    {
        Epoch = regranted.Epoch + 1,
        Revision = regranted.Revision + 1,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(regranted),
        SignerDeviceId = delegateId,
        SignerPublicKey = delegatePublic,
        Members = regranted.Members.Select(item => item.DeviceId == aId
            ? item with { Revoked = true } : item).ToArray(),
        Signature = ""
    };
    var matchingDelegate = matchingDraft with
    {
        Signature = Convert.ToBase64String(delegateKey.SignData(
        SharedWorldRosterTrust.Basis(matchingDraft), HashAlgorithmName.SHA256))
    };
    var delegatedAgain = await restarted.PublishDelegatedRosterAsync(profile.Id,
        delegateId, delegatePublic, matchingDelegate);
    Require(restartedPairing.Revoke(aId).Ok,
        "owner could not revoke A after the delegate's matching change");
    var baselineOnly = await restarted.PublishSharedWorldRosterAsync(profile.Id,
        restartedPairing.SharedRosterMembers(profile.Id));
    restartedPairing.ConfirmSharedRosterPublished(profile.Id, baselineOnly);
    Require(baselineOnly.Revision > delegatedAgain.Revision &&
        baselineOnly.Members.Single(item => item.DeviceId == aId).Revoked &&
        baselineOnly.OwnerLocalBaselineMembers?.Single(item => item.DeviceId == aId).Revoked == true &&
        !restartedPairing.SharedRosterDirty(profile.Id),
        "same effective owner edit did not advance its signed local baseline and clear repair");
});

await Check("takeover votes use the current delegated roster and fence forked governance", () =>
{
    using var data = Data("delegated-authority-voters");
    var profile = Profile("delegated-voters", "delegated-voters-world", FreePort());
    profile.SharedSavesEnabled = true;
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    data.SaveSettings(Settings(profile));
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "delegated voter fixture");
    using var managerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var candidateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var hostingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var newVoterKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var expiredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var managerId = Guid.NewGuid();
    var candidateId = Guid.NewGuid();
    var newVoterId = Guid.NewGuid();
    var expiredId = Guid.NewGuid();
    string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    var members = new SharedWorldRosterMember[]
    {
        new(managerId, Public(managerKey), new(RecoveryVoter: true, ManageSharing: true), false,
            DateTimeOffset.UtcNow.AddMinutes(5)),
        new(candidateId, Public(candidateKey), new(EligibleHost: true), false),
        new(newVoterId, Public(newVoterKey), new(), false),
        new(expiredId, Public(expiredKey), new(RecoveryVoter: true), false,
            DateTimeOffset.UtcNow.AddMinutes(-1))
    };
    var backups = new WorldBackupService(data, TimeProvider.System);
    var shares = new SharedWorldService(data, backups);
    var root = shares.PublishRoster(profile, members);
    var backup = backups.Create(profile, BackupKinds.Rolling);
    Require(backup.Ok && backup.Backup is not null, "delegated voter backup failed");
    var version = shares.PublishAfterStop(profile, backup.Backup!.Id).Version!;
    var chain = new SharedWorldRosterChainStore(data);
    chain.Append(root, root.OwnerPublicKey);
    SharedWorldRoster Sign(SharedWorldRoster draft, ECDsa key) => draft with
    {
        Signature = Convert.ToBase64String(key.SignData(
        SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
    };
    var delegated = Sign(root with
    {
        Schema = 3,
        Epoch = 2,
        Revision = 2,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(root),
        SignerDeviceId = managerId,
        SignerPublicKey = Public(managerKey),
        Members = members.Select(item => item.DeviceId == newVoterId
            ? item with { Grants = item.Grants with { RecoveryVoter = true } } : item).ToArray(),
        Signature = ""
    }, managerKey);
    chain.Append(delegated, root.OwnerPublicKey);
    Require(shares.ReadRoster(profile)?.Signature == delegated.Signature,
        "Host did not use current delegated voter grants");
    WorldAuthorityProposal Propose(SharedWorldRoster roster)
    {
        var binding = new WorldSuccessorBinding(candidateId, Public(candidateKey),
            Public(hostingKey), "");
        var draft = new WorldAuthorityProposal(2, roster.GroupId, profile.Id, 1, null,
            WorldAuthorityTrust.RosterHash(roster), version.VersionHash,
            Public(hostingKey), "https://127.0.0.1:5132", "Quorum",
            candidateId, Public(candidateKey), "", binding);
        binding = binding with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            WorldAuthorityTrust.BindingBasis(draft, binding), HashAlgorithmName.SHA256))
        };
        draft = draft with { SuccessorBinding = binding };
        return draft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
            WorldAuthorityTrust.ProposalBasis(draft), HashAlgorithmName.SHA256))
        };
    }
    var store = new WorldAuthorityStore(data);
    var proposal = Propose(delegated);
    RequireThrows<InvalidDataException>(() => store.SignLocalVote(profile.Id,
        Propose(root), root, managerId, managerKey),
        "a stale owner roster was used for a new vote");
    RequireThrows<InvalidDataException>(() => store.SignLocalVote(profile.Id,
        proposal, delegated, expiredId, expiredKey),
        "an expired recovery voter signed a takeover");
    var voteManager = store.SignLocalVote(profile.Id, proposal, delegated, managerId, managerKey);
    var voteNew = store.SignLocalVote(profile.Id, proposal, delegated, newVoterId, newVoterKey);
    var draftRecord = new WorldAuthorityRecord(1, proposal, delegated, version,
        [voteManager, voteNew], null, "");
    var record = draftRecord with
    {
        RecordHash = WorldAuthorityTrust.Hash(
        WorldAuthorityTrust.RecordBasis(draftRecord))
    };
    Require(!WorldAuthorityTrust.Verify(record, true),
        "a delegated authority without Host acceptance was trusted");
    var decisionHash = record.RecordHash;
    data.SaveProtected(WorldAuthorityStore.HostingKeyName(profile.Id),
        hostingKey.ExportPkcs8PrivateKey());
    store.Append(record);
    record = new WorldAuthorityStore(data).Read(profile.Id).Single();
    Require(record.RecordHash == decisionHash &&
        WorldAuthorityTrust.Verify(record, true) &&
        WorldAuthorityTrust.VerifyHostAcceptance(record),
        "delegated quorum did not survive authority restart");
    Require(store.ReadPage(profile.Id, 0).Single().RecordHash == decisionHash,
        "delegated authority was verified on disk but omitted from the paged Friend feed");
    Require(!WorldAuthorityTrust.VerifyHostAcceptance(record with
    { RecordHash = new string('0', 64) }) &&
        !WorldAuthorityTrust.VerifyHostAcceptance(record with
        { HostAcceptedUtc = record.HostAcceptedUtc!.Value.AddMinutes(1) }) &&
        !WorldAuthorityTrust.VerifyHostAcceptance(record with
        { Proposal = record.Proposal with { RosterHash = new string('0', 64) } }),
        "Host acceptance replayed against another decision, roster, or time");
    var afterDelegateExpiry = new WorldAuthorityStore(data,
        new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(6)));
    RequireThrows<InvalidDataException>(() => afterDelegateExpiry.SignLocalVote(profile.Id,
        proposal, delegated, managerId, managerKey),
        "an expired delegate retained a recovery vote");
    RequireThrows<InvalidDataException>(() => afterDelegateExpiry.Append(
        draftRecord with { RecordHash = decisionHash }),
        "an expired first submission received a Host attestation");
    RequireThrows<InvalidDataException>(() => afterDelegateExpiry.AppendReceived(
        record with { HostAcceptanceSignature = voteManager.Signature },
        profile.Id, root.GroupId, root.OwnerPublicKey),
        "a forged Host acceptance was accepted");
    using (var receiver = Data("delegated-authority-friend"))
    {
        var receivedChain = new SharedWorldRosterChainStore(receiver);
        receivedChain.Append(root, root.OwnerPublicKey);
        receivedChain.Append(delegated, root.OwnerPublicKey);
        var receivedAuthority = new WorldAuthorityStore(receiver,
            new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(6)));
        RequireThrows<InvalidDataException>(() => receivedAuthority.AppendReceived(
            draftRecord with { RecordHash = decisionHash },
            profile.Id, root.GroupId, root.OwnerPublicKey),
            "an offline Friend accepted an unattested delayed authority");
        RequireThrows<InvalidDataException>(() => receivedAuthority.AppendReceived(record,
            profile.Id, root.GroupId, root.OwnerPublicKey),
            "candidate-only timestamp bypassed an expired grant on first receipt");
        new WorldAuthorityStore(receiver).AppendReceived(record, profile.Id,
            root.GroupId, root.OwnerPublicKey);
        Require(receivedAuthority.Read(profile.Id).Single().RecordHash ==
            record.RecordHash, "Friend did not accept owner-rooted delegated quorum history");
        var trustedChain = FriendLink.VerifySharedChainBatchAsync(receiver, candidateId,
            profile.Id, version, version, [record],
            (_, _) => Task.FromResult<SharedWorldVersion?>(null), CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(trustedChain.Valid && !trustedChain.Conflict,
            "Friend rejected the save boundary after verifying delegated authority");
        using (var unrooted = Data("delegated-authority-unrooted"))
        {
            var untrustedChain = FriendLink.VerifySharedChainBatchAsync(unrooted, candidateId,
                profile.Id, version, version, [record],
                (_, _) => Task.FromResult<SharedWorldVersion?>(null), CancellationToken.None)
                .GetAwaiter().GetResult();
            Require(!untrustedChain.Valid,
                "an unrooted delegated authority verified a Friend save boundary");
        }
        var successorProfile = Profile("delegated-successor", profile.WorldId, FreePort());
        successorProfile.Id = profile.Id;
        successorProfile.Kind = profile.Kind;
        successorProfile.SharedSavesEnabled = true;
        receiver.SaveSettings(Settings(successorProfile));
        receiver.SaveProtected($"shared-world-pc-signing-{candidateId:N}.protected",
            candidateKey.ExportPkcs8PrivateKey());
        receiver.SaveProtected(WorldAuthorityStore.HostingKeyName(profile.Id),
            hostingKey.ExportPkcs8PrivateKey());
        receivedAuthority.BindLocalSuccessor(profile.Id, record.RecordHash, candidateId);
        var successorShares = new SharedWorldService(receiver,
            new WorldBackupService(receiver, TimeProvider.System));
        successorShares.AdoptSuccessor(successorProfile, record);
        Require(SharedWorldRosterTrust.Hash(successorShares.ReadRoster(successorProfile)!) ==
            SharedWorldRosterTrust.Hash(delegated),
            "successor used its machine's unrelated world key for a delegated roster");
        var revisionPath = Path.Combine(receiver.RootPath, "shared-worlds",
            profile.Id.ToString("N"), "roster-chain",
            SharedWorldRosterTrust.Hash(delegated) + ".json");
        File.AppendAllText(revisionPath, "tampered");
        RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(receiver).Read(profile.Id),
            "Friend authority ignored a tampered delegated roster floor");
    }
    using var ownerKey = ECDsa.Create();
    ownerKey.ImportPkcs8PrivateKey(data.LoadProtected("shared-world-signing-key.protected")!, out _);
    var revoked = Sign(delegated with
    {
        Epoch = 3,
        Revision = 3,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(delegated),
        SignerDeviceId = Guid.Empty,
        SignerPublicKey = root.OwnerPublicKey,
        Members = delegated.Members.Select(item => item.DeviceId == managerId
            ? item with { Revoked = true } : item).ToArray(),
        Signature = ""
    }, ownerKey);
    chain.Append(revoked, root.OwnerPublicKey);
    using (var lateReceiver = Data("delegated-authority-revoked-replay"))
    {
        var lateChain = new SharedWorldRosterChainStore(lateReceiver);
        lateChain.Append(root, root.OwnerPublicKey);
        lateChain.Append(delegated, root.OwnerPublicKey);
        lateChain.Append(revoked, root.OwnerPublicKey);
        RequireThrows<InvalidDataException>(() =>
            new WorldAuthorityStore(lateReceiver).AppendReceived(record,
                profile.Id, root.GroupId, root.OwnerPublicKey),
            "a first authority receipt replayed revoked, unexpired voter grants");
    }
    store.AppendReceived(record, profile.Id, root.GroupId, root.OwnerPublicKey);
    Require(store.Read(profile.Id).Single().RecordHash == record.RecordHash,
        "a protected historical decision was lost after roster revocation");
    RequireThrows<InvalidDataException>(() => store.SignLocalVote(profile.Id,
        Propose(revoked), revoked, managerId, managerKey),
        "a revoked delegate retained a recovery vote");
    var sibling = Sign(revoked with { OwnerOverride = false, Signature = "" }, ownerKey);
    chain.Append(sibling, root.OwnerPublicKey);
    RequireThrows<InvalidDataException>(() => store.SignLocalVote(profile.Id,
        Propose(revoked), revoked, newVoterId, newVoterKey),
        "a roster fork allowed another vote");
    RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(data).Read(profile.Id),
        "forked governance left prior authority apparently safe to use");
    return Task.CompletedTask;
});

await Check("sharing manager can inspect grants without Receive access", () =>
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var target = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var selfId = Guid.NewGuid();
    var targetId = Guid.NewGuid();
    var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    var member = new SharedWorldRosterMember(selfId, publicKey,
        new(ManageSharing: true), false);
    var roster = new SharedWorldRoster(2, Guid.NewGuid(), Guid.NewGuid(), 1, 1, true,
        publicKey, [member, new(targetId,
            Convert.ToBase64String(target.ExportSubjectPublicKeyInfo()), new(Receive: true), false)], "");
    var now = DateTimeOffset.UtcNow;
    var view = SharedWorldSharingProjection.Build(roster, selfId, publicKey, now);
    Require(view.CanManage && view.Members.Count == 2 &&
        !view.Members.Single(item => item.IsSelf).Grants.Receive &&
        view.Members.Single(item => item.DeviceId == targetId).Grants.Receive,
        "ManageSharing incorrectly required save receiving or hid the editable roster");
    Require(!SharedWorldSharingProjection.Build(roster with
    { Members = [member with { Revoked = true }, roster.Members[1]] },
        selfId, publicKey, now).CanManage &&
        !SharedWorldSharingProjection.Build(roster with
        { Members = [member with { AccessExpiresUtc = now }, roster.Members[1]] },
        selfId, publicKey, now).CanManage &&
        !SharedWorldSharingProjection.Build(roster, selfId,
            Convert.ToBase64String(target.ExportSubjectPublicKeyInfo()), now).CanManage,
        "revoked, expired, or mismatched PC identity kept management access");
    var newer = roster with { Epoch = 2, Revision = 2, Signature = "newer" };
    var floor = new SharedRosterFloor(roster.GroupId, 2, 2, "newer");
    Require(SharedWorldSharingFloor.Allows([roster, newer], floor) &&
        !SharedWorldSharingFloor.Allows([roster], floor) &&
        !SharedWorldSharingFloor.Allows([roster, newer with { Signature = "sibling" }], floor) &&
        !SharedWorldSharingFloor.Allows([roster, newer with { GroupId = Guid.NewGuid() }], floor),
        "manage-only check overwrote a protected legacy floor with rollback or a sibling");
    var signedRoot = roster with
    {
        Signature = Convert.ToBase64String(key.SignData(
        SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256))
    };
    var nextDraft = signedRoot with { Epoch = 2, Revision = 2, Signature = "" };
    var signedNext = nextDraft with
    {
        Signature = Convert.ToBase64String(key.SignData(
        SharedWorldRosterTrust.Basis(nextDraft), HashAlgorithmName.SHA256))
    };
    var legacyFloor = new SharedRosterFloor(signedRoot.GroupId, 1, 1, signedRoot.Signature);
    Require(SharedWorldSharingFloor.AllowsLegacyOwnerAdvance([signedNext], legacyFloor, false) &&
        !SharedWorldSharingFloor.AllowsLegacyOwnerAdvance([signedRoot], legacyFloor, false) &&
        !SharedWorldSharingFloor.AllowsLegacyOwnerAdvance([signedNext], legacyFloor, true) &&
        !SharedWorldSharingFloor.AllowsLegacyOwnerAdvance(
            [signedNext with { Signature = "altered" }], legacyFloor, false) &&
        !SharedWorldSharingFloor.AllowsLegacyOwnerAdvance(
            [signedNext with { GroupId = Guid.NewGuid() }], legacyFloor, false),
        "legacy owner advance bypassed the signed monotonic floor or an established chain");
    return Task.CompletedTask;
});

await Check("schema-3 delegated voter-only review verifies fenced multi-version proof and revocation", async () =>
{
    using var hostData = Data("voter-history-owner");
    using var voterData = Data("voter-history-friend");
    var profile = Profile("voter-history", "voter-history-world", FreePort());
    profile.Kind = "Fixture";
    profile.SharedSavesEnabled = true;
    var companionPort = 51389;
    var address = $"https://127.0.0.1:{companionPort}";
    var settings = Settings(profile);
    settings.CompanionEndpoint = address;
    settings.CompanionPort = companionPort;
    settings.CompanionBindAddress = "127.0.0.1";
    settings.CompanionListeningEnabled = true;
    hostData.SaveSettings(settings);
    using var certificate = new HostIdentity(hostData).Ensure(address);
    var deviceId = Guid.NewGuid();
    var bearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var candidateId = Guid.NewGuid();
    var delegateId = Guid.NewGuid();
    var otherVoterId = Guid.NewGuid();
    var ineligibleId = Guid.NewGuid();
    using var candidateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var delegateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var otherVoterKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var ineligibleKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var ineligibleBearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    hostData.SavePairingState(new PairingPersistentState
    {
        Devices = [new PairedDevice
        {
            Id = deviceId, AssignedProfileIds = [profile.Id],
            CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer))),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            SharedWorldGrants = new() { [profile.Id] = new(RecoveryVoter: true) }
        }, new PairedDevice
        {
            Id = delegateId, AssignedProfileIds = [profile.Id],
            CredentialHash = new string('C', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            SharedWorldGrants = new() { [profile.Id] = new(ManageSharing: true) },
            SharedWorldPublicKey = Convert.ToBase64String(delegateKey.ExportSubjectPublicKeyInfo())
        }, new PairedDevice
        {
            Id = candidateId, AssignedProfileIds = [profile.Id],
            CredentialHash = new string('A', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            SharedWorldGrants = new() { [profile.Id] = new(Receive: true, EligibleHost: true,
                RecoveryVoter: true) },
            SharedWorldPublicKey = Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo())
        }, new PairedDevice
        {
            Id = otherVoterId, AssignedProfileIds = [profile.Id],
            CredentialHash = new string('B', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            SharedWorldGrants = new() { [profile.Id] = new(RecoveryVoter: true) },
            SharedWorldPublicKey = Convert.ToBase64String(otherVoterKey.ExportSubjectPublicKeyInfo())
        }, new PairedDevice
        {
            Id = ineligibleId, AssignedProfileIds = [profile.Id],
            CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ineligibleBearer))),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            SharedWorldGrants = new() { [profile.Id] = new() },
            SharedWorldPublicKey = Convert.ToBase64String(ineligibleKey.ExportSubjectPublicKeyInfo())
        }]
    });
    var pairing = new PairingService(hostData);
    var manager = new HostManager(hostData, Games(hostData));
    if (pairing.SharedRosterDirty(profile.Id))
    {
        var initial = await manager.PublishSharedWorldRosterAsync(profile.Id,
            pairing.SharedRosterMembers(profile.Id));
        pairing.ConfirmSharedRosterPublished(profile.Id, initial);
    }
    var rootRoster = await manager.SharedWorldRosterAsync(profile.Id)
        ?? throw new Exception("owner root roster was unavailable");
    var delegatePublicKey = Convert.ToBase64String(delegateKey.ExportSubjectPublicKeyInfo());
    var delegatedDraft = rootRoster with
    {
        Schema = 3,
        Epoch = rootRoster.Epoch + 1,
        Revision = rootRoster.Revision + 1,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(rootRoster),
        SignerDeviceId = delegateId,
        SignerPublicKey = delegatePublicKey,
        Members = rootRoster.Members.Select(member => member.DeviceId == ineligibleId
            ? member with { Grants = member.Grants with { Receive = true } } : member).ToArray(),
        Signature = ""
    };
    var delegated = delegatedDraft with
    {
        Signature = Convert.ToBase64String(
        delegateKey.SignData(SharedWorldRosterTrust.Basis(delegatedDraft), HashAlgorithmName.SHA256))
    };
    var publishedRoster = await manager.PublishDelegatedRosterAsync(profile.Id,
        delegateId, delegatePublicKey, delegated);
    Require(publishedRoster.Schema == 3 && publishedRoster.Signature == delegated.Signature &&
        publishedRoster.HostAcceptanceSignature is not null,
        "schema-3 delegated roster did not retain Host-attested authority");
    voterData.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(
        new FriendConfiguration
        {
            Endpoint = address,
            Fingerprint = HostIdentity.Fingerprint(certificate),
            DeviceId = deviceId,
            Credential = bearer,
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    using var modeGate = new SemaphoreSlim(1, 1);
    var listener = new CompanionServer(hostData, manager, pairing, Games(hostData),
        new ServerLogService(hostData, manager), modeGate, companionPort + 2);
    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    builder.WebHost.UseTestServer();
    await using var app = listener.BuildInMemoryApp(builder, settings, new Uri(address));
    await app.StartAsync();
    HttpClient Client() => app.GetTestServer().CreateClient();
    using var voter = new FriendLink(voterData, "friend.protected", (endpoint, pins) =>
    {
        Require(endpoint == address && pins.Contains(HostIdentity.Fingerprint(certificate)),
            "voter history client lost its saved endpoint or certificate pin");
        var result = Client();
        result.BaseAddress = new Uri(address + "/");
        return result;
    });
    try
    {
        var pending = await voter.ReviewSharedHistoryAsync(profile.Id);
        Require(pending.Code == "GroupReviewRequired" && pending.GroupId is not null &&
            pending.OwnerPublicKey is not null &&
            pairing.Views().Single(item => item.Id == deviceId).SharedWorldKeyEnrolled &&
            voterData.LoadProtected("friend.protected") is not null,
            "voter-only PC did not enroll and require signed group review");
        var rejected = await voter.ReviewSharedHistoryAsync(profile.Id,
            new WorldHistoryReviewRequest(Guid.NewGuid(), pending.OwnerPublicKey));
        Require(rejected.Code == "GroupReviewRequired",
            "voter-only PC accepted confirmation for another group");
        var wrongOwner = await voter.ReviewSharedHistoryAsync(profile.Id,
            new WorldHistoryReviewRequest(pending.GroupId, "another owner"));
        Require(wrongOwner.Code == "GroupReviewRequired",
            "voter-only PC accepted confirmation for another owner identity");
        var confirmed = await voter.ReviewSharedHistoryAsync(profile.Id,
            new WorldHistoryReviewRequest(pending.GroupId, pending.OwnerPublicKey));
        var saved = JsonSerializer.Deserialize<FriendConfiguration>(
            voterData.LoadProtected("friend.protected")!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Require(confirmed.Ok && confirmed.Code == "HistoryReviewed" &&
            saved.ConsentedSharedWorldProfiles.Count == 0 &&
            saved.ApprovedSharedWorldGroups[profile.Id] == pending.GroupId &&
            saved.SharedWorldSigningKeys[profile.Id] == pending.OwnerPublicKey,
            "voter-only review did not pin the signed owner/group separately from save consent");
        var roster = await manager.SharedWorldReviewRosterAsync(profile.Id)
            ?? throw new Exception("enrollment did not publish owner-signed membership");
        var voterGrants = roster.Members.Single(member => member.DeviceId == deviceId).Grants;
        var rosterHistory = await manager.SharedWorldRosterHistoryAsync(profile.Id);
        Require(roster.Schema == 3 && roster.Revision >= publishedRoster.Revision &&
            rosterHistory?.Any(item => item.Signature == publishedRoster.Signature) == true &&
            voterGrants.RecoveryVoter && !voterGrants.Receive && !voterGrants.EligibleHost,
            "voter-only identity did not retain schema-3 delegated membership");
        var backups = new WorldBackupService(hostData, TimeProvider.System);
        var shares = new SharedWorldService(hostData, backups);
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "first signed version");
        var firstBackup = backups.Create(profile, BackupKinds.Rolling);
        Require(firstBackup.Ok && firstBackup.Backup is not null,
            "first disposable owner backup was unavailable");
        var firstVersion = shares.PublishAfterStop(profile, firstBackup.Backup!.Id).Version
            ?? throw new Exception("first disposable owner version was unavailable");
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "second signed version");
        var secondBackup = backups.Create(profile, BackupKinds.Rolling);
        Require(secondBackup.Ok && secondBackup.Backup is not null,
            "second disposable owner backup was unavailable");
        var secondVersion = shares.PublishAfterStop(profile, secondBackup.Backup!.Id).Version
            ?? throw new Exception("second disposable owner version was unavailable");
        var candidatePublicKey = Convert.ToBase64String(candidateKey.ExportSubjectPublicKeyInfo());
        var proposalDraft = new WorldAuthorityProposal(1, roster.GroupId, profile.Id, 1, null,
            WorldAuthorityTrust.RosterHash(roster), secondVersion.VersionHash, candidatePublicKey,
            "https://127.0.0.1:5132", "Quorum", candidateId, candidatePublicKey, "");
        var proposal = proposalDraft with
        {
            Signature = Convert.ToBase64String(candidateKey.SignData(
                WorldAuthorityTrust.ProposalBasis(proposalDraft), HashAlgorithmName.SHA256))
        };
        using var voterKey = ECDsa.Create();
        voterKey.ImportPkcs8PrivateKey(voterData.LoadProtected(
            $"shared-world-pc-signing-{deviceId:N}.protected")!, out _);
        WorldAuthorityVote SignVote(Guid voterId, ECDsa key)
        {
            var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(proposal),
                voterId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "");
            return draft with
            {
                Signature = Convert.ToBase64String(key.SignData(
                WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256))
            };
        }
        var recordDraft = new WorldAuthorityRecord(1, proposal, roster, secondVersion,
            [SignVote(deviceId, voterKey), SignVote(otherVoterId, otherVoterKey)], null, "",
            VersionLineageDigest: WorldAuthorityTrust.LineageDigest([firstVersion, secondVersion]));
        var hashedRecord = recordDraft with
        { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(recordDraft)) };
        var acceptedAt = DateTimeOffset.UtcNow;
        var record = hashedRecord with
        {
            HostAcceptedUtc = acceptedAt,
            HostAcceptanceSignature = Convert.ToBase64String(candidateKey.SignData(
                WorldAuthorityTrust.HostAcceptanceBasis(hashedRecord, acceptedAt), HashAlgorithmName.SHA256))
        };
        Require(WorldAuthorityTrust.Verify(record, rosterChainVerified: true),
            "multi-version schema-3 authority was not signed by a voter majority and accepted by the successor");
        new WorldAuthorityStore(hostData).AppendReceived(record, profile.Id, roster.GroupId,
            roster.OwnerPublicKey, [firstVersion, secondVersion]);
        Require((await manager.StartAsync(profile.Id)).Code == "SharedWorldAuthorityBlocked",
            "fenced old Host accepted a normal Start");
        WorldHistoryReviewResult reviewed = new(false, "Pending", "");
        for (var attempt = 0; attempt < 3 && !reviewed.Ok; attempt++)
            reviewed = await voter.ReviewSharedHistoryAsync(profile.Id);
        Require(reviewed.Ok && reviewed.RecordCount == 1 &&
            new WorldAuthorityStore(voterData).Read(profile.Id).Single().RecordHash == record.RecordHash &&
            saved.ConsentedSharedWorldProfiles.Count == 0,
            "first-time voter did not verify the fenced Host's two-version proof without save consent: " +
            reviewed.Code);
        using var client = Client();
        client.BaseAddress = new Uri(address + "/");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
        using var deniedSave = await client.GetAsync($"api/companion/servers/{profile.Id}/shared-world");
        Require(deniedSave.StatusCode == HttpStatusCode.Forbidden,
            "voter-only enrollment acquired save transfer permission");
        using var ineligibleClient = Client();
        ineligibleClient.BaseAddress = new Uri(address + "/");
        ineligibleClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ineligibleBearer);
        ineligibleClient.DefaultRequestHeaders.Add("X-Device-Id", ineligibleId.ToString());
        var dirtyPath = Path.Combine(hostData.RootPath, "shared-worlds", profile.Id.ToString("N"),
            "roster-dirty");
        File.WriteAllText(dirtyPath, "review required");
        using var dirtyIneligible = await ineligibleClient.GetAsync(
            $"api/companion/servers/{profile.Id}/shared-world/authority");
        using var dirtyIneligibleProof = await ineligibleClient.GetAsync(
            $"api/companion/servers/{profile.Id}/shared-world/authority/{record.RecordHash}/proof/1");
        using var dirtyEligible = await client.GetAsync(
            $"api/companion/servers/{profile.Id}/shared-world/authority");
        Require(dirtyIneligible.StatusCode == HttpStatusCode.Forbidden &&
            dirtyIneligibleProof.StatusCode == HttpStatusCode.Forbidden &&
            dirtyEligible.IsSuccessStatusCode,
            "dirty membership admitted an ineligible peer or blocked a still-granted voter");
        Require(pairing.Revoke(deviceId).Ok &&
            !(await voter.ReviewSharedHistoryAsync(profile.Id)).Ok,
            "revoked voter continued reading signed history");
        using var revokedHistory = await client.GetAsync(
            $"api/companion/servers/{profile.Id}/shared-world/authority");
        using var revokedProof = await client.GetAsync(
            $"api/companion/servers/{profile.Id}/shared-world/authority/{record.RecordHash}/proof/1");
        Require(revokedHistory.StatusCode == HttpStatusCode.Forbidden &&
            revokedProof.StatusCode == HttpStatusCode.Forbidden &&
            (await manager.StartAsync(profile.Id)).Code == "SharedWorldAuthorityBlocked",
            "revocation reopened history access or old-Host Start");
    }
    finally { await listener.StopAsync(); }
});

await Check("shared missing signed roster cannot reset a distributed revision before the first save", () =>
{
    using var data = Data("shared-roster-missing-before-save");
    var profile = Profile("roster-missing", "roster-missing", FreePort());
    profile.SharedSavesEnabled = true;
    var service = new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System));
    var bindingPath = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"), "source.json");
    RequireThrows<InvalidDataException>(() => service.PublishRoster(profile,
        [new SharedWorldRosterMember(Guid.Empty, "invalid", new(), false)]),
        "invalid first roster did not fail");
    Require(!File.Exists(bindingPath), "a failed first roster published its binding");
    var first = service.PublishRoster(profile, []);
    Require(File.Exists(bindingPath) && first.Revision == 1 && service.Status(profile).Latest is null,
        "the fixture unexpectedly published a save");
    var rosterPath = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
        first.GroupId.ToString("N") + ".roster.json");
    File.Delete(rosterPath);
    RequireThrows<InvalidDataException>(() => service.PublishRoster(profile, []),
        "same-source publication reset a possibly distributed roster revision");
    profile.WorldDirectory = Path.Combine(data.RootPath, "changed-world");
    Directory.CreateDirectory(profile.WorldDirectory);
    RequireThrows<InvalidDataException>(() => service.PublishRoster(profile, [], reviewSourceChange: true),
        "source review reset a possibly distributed roster revision");
    Require(!File.Exists(rosterPath), "a missing roster was silently recreated");
    return Task.CompletedTask;
});

await Check("control policy remains fail closed when audit logging is unavailable", async () =>
{
    using var data = Data("control-policy-audit");
    var profile = Profile("control-policy-audit", "control-policy-audit", FreePort());
    var seeded = Settings(profile);
    seeded.RemoteControlsEnabled = true;
    data.SaveSettings(seeded);
    var manager = Manager(data);
    using var auditLock = new FileStream(Path.Combine(root, "control-policy-audit", "audit.log"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var result = await manager.UpdateControlPolicyAsync(new(RemoteControlsEnabled: false));
    Require(result.Ok && !manager.RemoteControlsEnabled && !data.LoadSettings().RemoteControlsEnabled,
        "an unavailable audit log left remote controls enabled in memory or on disk");
});

await Check("malformed lifecycle state is quarantined and blocks Start until acknowledgement", async () =>
{
    var dataRoot = Path.Combine(root, "malformed-runs-recovery");
    Directory.CreateDirectory(dataRoot);
    File.WriteAllText(Path.Combine(dataRoot, "runs.json"), "{not valid json");
    using var data = new LocalData(dataRoot);
    var manager = Manager(data);
    var profile = Profile("recovered-world", "recovered-world", FreePort());
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "recovery settings failed");
    var snapshot = await manager.SnapshotAsync();
    Require(snapshot.Recovery is { LifecycleBlocked: true } &&
        snapshot.Recovery.Notices.Any(notice => notice.StateFile == "runs.json") &&
        Directory.EnumerateFiles(Path.Combine(dataRoot, "quarantine"), "*-runs.json").Any(),
        "malformed runs state was not quarantined and exposed as lifecycle-blocking recovery");
    Require((await manager.StartAsync(profile.Id)).Code == "DataRecoveryRequired",
        "Start was allowed before recovered lifecycle state was acknowledged");
    Require((await manager.RestartAsync(profile.Id)).Code == "DataRecoveryRequired" &&
        (await manager.StopAsync(profile.Id, _ => true)).Code == "DataRecoveryRequired" &&
        (await manager.StopAsync(profile.Id)).Code == "NotManaged" &&
        (await manager.RestoreBackupAsync(profile.Id, Guid.NewGuid())).Code == "DataRecoveryRequired" &&
        (await manager.MaintainIdleShutdownAsync()).Single().Code == "DataRecoveryRequired" &&
        (await manager.MaintainCrashRecoveryAsync()).Single().Code == "DataRecoveryRequired",
        "recovery did not pause restart, remote/automatic stop, restore, or crash recovery while retaining local Stop");
    data.AcknowledgeRecovery();
    Require((await manager.StartAsync(profile.Id)).Ok, "acknowledgement did not release a normal fixture Start");
    Require((await manager.StopAsync(profile.Id)).Ok, "recovery fixture cleanup failed");
});

await Check("an interrupted lifecycle quarantine is reconstructed until owner acknowledgement", () =>
{
    var dataRoot = Path.Combine(root, "orphaned-lifecycle-quarantine");
    var quarantineRoot = Path.Combine(dataRoot, "quarantine");
    Directory.CreateDirectory(quarantineRoot);
    var quarantineName = $"20260924T1200000000000Z-{Guid.NewGuid():N}-runs.json";
    var quarantinePath = Path.Combine(quarantineRoot, quarantineName);
    File.WriteAllText(quarantinePath, "{interrupted quarantine payload");
    using (var data = new LocalData(dataRoot))
    {
        Require(data.Recovery is { LifecycleBlocked: true } &&
            data.Recovery.Notices.Any(notice => notice.StateFile == "runs.json" &&
                notice.QuarantinedFile == quarantineName),
            "an orphaned lifecycle quarantine did not reconstruct a blocking recovery notice");
        data.AcknowledgeRecovery();
        Require(File.Exists(quarantinePath), "acknowledgement deleted the retained quarantine artifact");
    }
    using (var reopened = new LocalData(dataRoot))
        Require(!reopened.Recovery.LifecycleBlocked && reopened.Recovery.Notices.Count == 0,
            "an acknowledged retained quarantine artifact blocked lifecycle again after restart");
    return Task.CompletedTask;
});

await Check("corrupt Host settings cannot hide an authoritative recorded run", async () =>
{
    var dataRoot = Path.Combine(root, "malformed-host-with-run");
    using var data = new LocalData(dataRoot);
    var profile = Profile("orphan-profile", "orphan", FreePort(), Path.Combine(root, "orphan-world"));
    var seedingManager = Manager(data);
    Require((await seedingManager.UpdateSettingsAsync(Settings(profile))).Ok &&
        (await seedingManager.StartAsync(profile.Id)).Ok, "orphan fixture setup failed");
    var recorded = data.LoadRuns().Single(item => item.ProfileId == profile.Id);
    try
    {
        File.WriteAllText(Path.Combine(dataRoot, "host.json"), "{not valid json");
        var manager = Manager(data);
        var snapshot = await manager.SnapshotAsync();
        var orphan = snapshot.Runs.Single(item => item.ProfileId == profile.Id);
        Require(snapshot.Recovery is { LifecycleBlocked: true } &&
            orphan.State == "Process running" &&
            orphan.Detail.Contains("profile", StringComparison.OrdinalIgnoreCase),
            "a valid exact recorded run disappeared or was not locally stoppable when Host settings were quarantined");
        Require((await manager.StopAsync(profile.Id)).Ok,
            "an exact live orphan could not be stopped locally while recovery remained blocked");
    }
    finally
    {
        if (data.LoadRuns().Any(item => item.ProfileId == profile.Id))
            await DirectFixtureStop(recorded);
    }
});

await Check("duplicate start is serialized", async () =>
{
    using var data = Data("duplicate");
    var manager = Manager(data);
    var profile = Profile("duplicate-world", "duplicate-world", FreePort());
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    try
    {
        var starts = await Task.WhenAll(manager.StartAsync(profile.Id), manager.StartAsync(profile.Id));
        Require(starts.Count(result => result.Ok) == 1, "expected one successful launch");
        Require(starts.Single(result => !result.Ok).Code == "AlreadyManaged", "duplicate was not rejected");
        Require((await manager.HealthAsync(profile.Id)).Code == "FixtureProcessRunning", "fixture identity missing");
    }
    finally
    {
        var cleanup = await manager.StopAsync(profile.Id);
        Require(cleanup.Ok, $"fixture cleanup failed: {cleanup.Code} {cleanup.Message}");
    }
});

await Check("snapshots consume the observation supervisor cache", async () =>
{
    Require(!new GameHealthResult(true, "Probe", "Ready", "No count proof", OnlinePlayers: 0).PlayerCountTrusted,
        "a new driver result must not authorize Stop by default");
    using var data = Data("observation-cache");
    var manager = Manager(data);
    var profile = Profile("observation-cache", "observation-cache", FreePort());
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    Require((await manager.StartAsync(profile.Id)).Ok, "fixture start failed");
    try
    {
        var first = (await manager.SnapshotAsync()).Runs.Single(run => run.ProfileId == profile.Id);
        var second = (await manager.SnapshotAsync()).Runs.Single(run => run.ProfileId == profile.Id);
        Require(first.State == "Unknown" && second.State == "Unknown" &&
            first.Detail.Contains("observation supervisor", StringComparison.Ordinal),
            "a UI snapshot probed the game instead of waiting for the shared observation cache");
        await manager.RefreshObservationsAsync();
        var observed = (await manager.SnapshotAsync()).Runs.Single(run => run.ProfileId == profile.Id);
        Require(observed is { State: "Process running", PlayerCountTrusted: false },
            "the observation supervisor did not populate the shared cache");
    }
    finally
    {
        var cleanup = await manager.StopAsync(profile.Id);
        Require(cleanup.Ok, $"fixture cleanup failed: {cleanup.Code} {cleanup.Message}");
    }
});

await Check("status stays responsive and expires trusted counts while lifecycle work holds the gate", async () =>
{
    using var data = Data("cached-status-gate");
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    var profile = Profile("cached-status-gate", "cached-status-gate", FreePort());
    var settings = Settings(profile);
    settings.KeepAwakeWhileHosting = true;
    var driver = new ObservationFixtureDriver
    {
        HealthResult = new(true, "FixtureReady", "Ready", "Trusted fixture observation.",
            OnlinePlayers: 0, PlayerCountTrusted: true)
    };
    using var power = new BlockingPowerGuard();
    var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.LoopbackOnly), clock, power);
    Require((await manager.UpdateSettingsAsync(settings)).Ok, "settings failed");
    Require((await manager.StartAsync(profile.Id)).Ok, "fixture start failed");
    try
    {
        await manager.RefreshObservationsAsync();
        Require((await manager.SnapshotAsync()).Runs.Single().PlayerCountTrusted,
            "the fixture did not publish a trusted observation");
        power.Arm();
        var blocked = Task.Run(() => manager.UpdateSettingsAsync(settings));
        try
        {
            Require(power.Entered.Wait(TimeSpan.FromSeconds(5)), "the lifecycle gate did not enter the held operation");
            clock.Advance(TimeSpan.FromSeconds(11));
            var owner = await manager.SnapshotAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var companion = await manager.CompanionSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Require(owner.Runs.Single() is { State: "Unknown", PlayerCountTrusted: false, OnlinePlayers: null } &&
                companion.Runs.Single() is { State: "Unknown", PlayerCountTrusted: false, OnlinePlayers: null },
                "a cached status exposed stale zero-player evidence while the lifecycle gate was held");
            clock.Advance(TimeSpan.FromSeconds(-20));
            Require((await manager.CompanionSnapshotAsync()).Runs.Single().PlayerCountTrusted == false,
                "a clock rollback made cached zero-player evidence authoritative again");
        }
        finally { power.ReleaseBlock(); }
        Require((await blocked).Ok, "held settings operation failed");
    }
    finally
    {
        power.ReleaseBlock();
        Require((await manager.StopAsync(profile.Id)).Ok, "fixture cleanup failed");
    }
});

await Check("one writer per world", async () =>
{
    using var data = Data("world");
    var manager = Manager(data);
    var port = FreePort();
    var a = Profile("world-a", "same-world", port);
    var b = Profile("world-b", "same-world", port + 10, a.WorldDirectory + Path.DirectorySeparatorChar);
    var alias = Path.Combine(root, "world-junction");
    CreateJunction(alias, a.WorldDirectory);
    var c = Profile("world-c", "same-world", port + 20, alias);
    Require((await manager.UpdateSettingsAsync(Settings(a, b, c))).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try
    {
        Require((await manager.StartAsync(b.Id)).Code == "WorldConflict",
            "trailing-separator world alias was allowed a second writer");
        Require((await manager.StartAsync(c.Id)).Code == "WorldConflict",
            "junction world alias was allowed a second writer");
    }
    finally { Require((await manager.StopAsync(a.Id)).Ok, "fixture cleanup failed"); }
});

await Check("separate save folders can use the same world name", async () =>
{
    using var data = Data("same-name-separate-folders");
    var manager = Manager(data);
    var port = FreePort();
    var a = Profile("same-name-a", "same-name", port);
    var b = Profile("same-name-b", "same-name", port + 10);
    Require((await manager.UpdateSettingsAsync(Settings(a, b))).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try { Require((await manager.StartAsync(b.Id)).Ok, "independent save folder was blocked by its world name"); }
    finally
    {
        Require((await manager.StopAsync(a.Id)).Ok, "first fixture cleanup failed");
        if (data.LoadRuns().Any(run => run.ProfileId == b.Id))
            Require((await manager.StopAsync(b.Id)).Ok, "second fixture cleanup failed");
    }
});

await Check("managed maximum", async () =>
{
    using var data = Data("maximum");
    var manager = Manager(data);
    var port = FreePort();
    var a = Profile("max-a", "max-a", port);
    var b = Profile("max-b", "max-b", port + 10);
    var settings = Settings(a, b);
    settings.MaxConcurrentServers = 1;
    Require((await manager.UpdateSettingsAsync(settings)).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try { Require((await manager.StartAsync(b.Id)).Code == "MaxConcurrent", "maximum was bypassed"); }
    finally { Require((await manager.StopAsync(a.Id)).Ok, "fixture cleanup failed"); }
});

await Check("managed port overlap", async () =>
{
    using var data = Data("ports");
    var manager = Manager(data);
    var port = FreePort();
    var a = Profile("port-a", "port-a", port);
    var b = Profile("port-b", "port-b", port + 1);
    var settings = Settings(a, b);
    settings.MaxConcurrentServers = 1;
    Require((await manager.UpdateSettingsAsync(settings)).Ok, "settings failed");
    Require((await manager.StartAsync(a.Id)).Ok, "first start failed");
    try
    {
        var conflict = await manager.StartAsync(b.Id);
        var portConflict = conflict.PortConflicts?.SingleOrDefault();
        Require(conflict.Code == "PortConflict" && conflict.Message.Contains("port-a", StringComparison.Ordinal) &&
            portConflict is not null && portConflict.ProfileId == a.Id &&
            portConflict.SharedPorts.Any(shared => shared.Port == port + 1),
            "overlap was allowed or did not identify the running server before the general concurrency limit");
    }
    finally { Require((await manager.StopAsync(a.Id)).Ok, "fixture cleanup failed"); }
});

await Check("occupied local UDP port", async () =>
{
    using var data = Data("bound-port");
    var manager = Manager(data);
    var port = FreePort();
    var profile = Profile("bound-port", "bound-port", port);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
    Require((await manager.StartAsync(profile.Id)).Code == "PortInUse", "occupied port was allowed");
});

await Check("restart reattaches exact fixture identity", async () =>
{
    var profile = Profile("restart", "restart", FreePort());
    using (var data = Data("restart"))
    {
        var manager = Manager(data);
        Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
        Require((await manager.StartAsync(profile.Id)).Ok, "start failed");
    }
    using (var data = Data("restart"))
    {
        var manager = Manager(data);
        Require((await manager.HealthAsync(profile.Id)).Code == "FixtureProcessRunning", "reattach identity failed");
        Require((await manager.StopAsync(profile.Id)).Ok, "reattached fixture stop failed");
    }
});

await Check("identity mismatch blocks start and never stops an unrelated process", async () =>
{
    var a = Profile("identity-a", "identity-a", FreePort());
    var b = Profile("identity-b", "identity-b", FreePort());
    ManagedRun original;
    using (var data = Data("identity-a"))
    {
        var manager = Manager(data);
        Require((await manager.UpdateSettingsAsync(Settings(a))).Ok, "A settings failed");
        Require((await manager.StartAsync(a.Id)).Ok, "A start failed");
        original = data.LoadRuns().Single();
    }
    using var otherData = Data("identity-b");
    var other = Manager(otherData);
    Require((await other.UpdateSettingsAsync(Settings(b))).Ok, "B settings failed");
    Require((await other.StartAsync(b.Id)).Ok, "B start failed");
    var unrelated = otherData.LoadRuns().Single();
    try
    {
        using var data = Data("identity-a");
        var changed = data.LoadRuns().Single();
        changed.ProcessId = unrelated.ProcessId; // A stale or corrupt PID must never become authority over B.
        data.SaveRuns([changed]);
        var manager = Manager(data);
        Require((await manager.HealthAsync(a.Id)).Code == "IdentityUnknown", "identity mismatch not detected");
        Require((await manager.StopAsync(a.Id)).Code == "IdentityUnknown", "unrelated process received a stop");
        Require((await manager.ForgetAsync(a.Id)).Code == "IdentityUnknown", "identity-uncertain run was cleared");
        Require((await manager.StartAsync(a.Id)).Code == "AlreadyManaged", "unknown world accepted a second writer");
        Require((await other.HealthAsync(b.Id)).Code == "FixtureProcessRunning", "unrelated process was stopped");
    }
    finally
    {
        await DirectFixtureStop(original);
        Require((await other.StopAsync(b.Id)).Ok, "B cleanup failed");
    }
});

await Check("game drivers are explicit and unknown games fail closed", async () =>
{
    using var data = Data("drivers");
    var productionRegistry = new GameServerRegistry(data, false, PortProbeMode.LoopbackOnly);
    Require(!productionRegistry.TryGet(GameKinds.Fixture, out _),
        "the synthetic fixture driver was enabled without an explicit test opt-in");
    var registry = Games(data);
    Require(registry.All.Select(driver => driver.Kind).Order().SequenceEqual(new[]
        { GameKinds.Custom, GameKinds.Factorio, GameKinds.Fixture, GameKinds.MinecraftBedrock,
            GameKinds.MinecraftJava, GameKinds.Terraria, GameKinds.Valheim }),
        "The built-in, custom, and fixture games were not separately registered");
    var profile = Profile("unknown-game", "unknown-game", FreePort());
    profile.Kind = "UnregisteredGame";
    var manager = new HostManager(data, registry);
    var result = await manager.UpdateSettingsAsync(Settings(profile));
    Require(!result.Ok && result.Code == "InvalidSettings", "an unregistered game profile was accepted");
});

await Check("test port probes stay on loopback while app probes cover all interfaces", async () =>
{
    Require(GameServerRegistry.ProbeAddress("IPv4", PortProbeMode.LoopbackOnly).Equals(IPAddress.Loopback) &&
        GameServerRegistry.ProbeAddress("IPv6", PortProbeMode.LoopbackOnly).Equals(IPAddress.IPv6Loopback) &&
        GameServerRegistry.ProbeAddress("IPv4", PortProbeMode.AllInterfaces).Equals(IPAddress.Any) &&
        GameServerRegistry.ProbeAddress("IPv6", PortProbeMode.AllInterfaces).Equals(IPAddress.IPv6Any),
        "port probe mode selected the wrong bind address");
    using var occupied = new TcpListener(IPAddress.Loopback, 0);
    occupied.Start();
    var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
    Require(!GameServerRegistry.PortsAvailable([new("TCP", port, "occupied fixture")],
        PortProbeMode.LoopbackOnly), "loopback probe missed an occupied test port");
    await Task.CompletedTask;
});

await Check("Valheim startup connection sequences survive the ready boundary", () =>
{
    var startupJoin = ValheimServerLog.Parse(new StringReader(
        "09/25/2026 12:00:00: New connection\n" +
        "09/25/2026 12:00:01: Game server connected\n" +
        "09/25/2026 12:00:02: Got connection SteamID 111111\n"));
    var startupRoundTrip = ValheimServerLog.Parse(new StringReader(
        "09/25/2026 12:00:00: New connection\n" +
        "09/25/2026 12:00:01: Got connection SteamID 222222\n" +
        "09/25/2026 12:00:02: Game server connected\n" +
        "09/25/2026 12:00:03: RPC_Disconnect\n" +
        "09/25/2026 12:00:04: Closing socket 222222\n"));
    Require(startupJoin.Ready && startupJoin.Players?.Online == 1 &&
        startupRoundTrip.Ready && startupRoundTrip.Players?.Online == 0,
        "a complete connection sequence crossing the ready marker was discarded");
    return Task.CompletedTask;
});

await Check("managed server logs are exact-run bounded and sanitized independently of lifecycle", async () =>
{
    using var data = Data("server-logs");
    var profile = Profile("log-valheim", "log-world", FreePort());
    profile.Kind = GameKinds.Valheim;
    var custom = Profile("log-custom", "custom-world", FreePort());
    custom.Kind = GameKinds.Custom;
    custom.Custom = new CustomGameOptions();
    var other = Profile("log-valheim-other", "log-world-other", FreePort());
    other.Kind = GameKinds.Valheim;
    data.SaveSettings(Settings(profile, custom, other));
    var operationId = Guid.NewGuid();
    var path = data.NewRunLogPath(operationId);
    var run = new ManagedRun
    {
        ProfileId = profile.Id,
        OperationId = operationId,
        Kind = GameKinds.Valheim,
        WorldId = profile.WorldId,
        LogPath = path
    };
    var otherOperationId = Guid.NewGuid();
    var otherPath = data.NewRunLogPath(otherOperationId);
    File.WriteAllText(otherPath, "09/28/2026 12:10:00: another managed run\n");
    var otherRun = new ManagedRun
    {
        ProfileId = other.Id,
        OperationId = otherOperationId,
        Kind = GameKinds.Valheim,
        WorldId = other.WorldId,
        LogPath = otherPath
    };
    data.SaveRuns([run, otherRun]);
    File.WriteAllText(path,
        "09/28/2026 12:00:00: Game server connected\n" +
        "09/28/2026 12:00:01: password=host-secret bearer token-secret TS3-code-secret\n" +
        "09/28/2026 12:00:02: Chat player Alice SteamID 123456789 192.168.1.4 C:\\Users\\Alice\\private\\world.db \u001b[31mwarning\rspoof\n" +
        "09/28/2026 12:00:03: " + string.Join(' ', Enumerable.Repeat("diagnostic", 2500)) + "\n");
    var decoySecret = "must-not-read-decoy";
    File.WriteAllText(data.RunLogPath(Guid.NewGuid()), decoySecret + "\n");
    var manager = Manager(data);
    var logs = new ServerLogService(data, manager);

    var first = await logs.ReadAsync(profile.Id, new(Limit: 1), ServerLogAudience.Friend);
    Require(first.Ok && first.SourceState == ServerLogSourceStates.Active &&
        first.RunId == operationId.ToString("N") && first.Records.Count == 1 && first.HasMore &&
        !string.IsNullOrWhiteSpace(first.Cursor) && first.Cursor.Length <= 160,
        "initial bounded exact-run page was not returned");
    static byte[] DecodeCursor(string cursor)
    {
        var encoded = cursor.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
    }
    static string EncodeCursor(byte[] cursor) => Convert.ToBase64String(cursor)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var tamperedBytes = DecodeCursor(first.Cursor!);
    tamperedBytes[^1] ^= 1;
    var tamperedCursor = await logs.ReadAsync(profile.Id,
        new(EncodeCursor(tamperedBytes), 10), ServerLogAudience.Host);
    var fabricatedBytes = DecodeCursor(first.Cursor!);
    BinaryPrimitives.WriteInt64BigEndian(fabricatedBytes.AsSpan(17, sizeof(long)), 0);
    var fabricatedCursor = await logs.ReadAsync(profile.Id,
        new(EncodeCursor(fabricatedBytes), 10), ServerLogAudience.Host);
    var crossRunCursor = await logs.ReadAsync(other.Id,
        new(first.Cursor, 10), ServerLogAudience.Host);
    var restartedServiceCursor = await new ServerLogService(data, manager).ReadAsync(profile.Id,
        new(first.Cursor, 10), ServerLogAudience.Host);
    Require(tamperedCursor.Code == "InvalidLogCursor" &&
        fabricatedCursor.Code == "InvalidLogCursor" &&
        crossRunCursor.Code == "InvalidLogCursor" &&
        restartedServiceCursor.Code == "InvalidLogCursor",
        "tampered, fabricated-offset, cross-run, or previous-service cursor was accepted");
    var remainder = await logs.ReadAsync(profile.Id, new(first.Cursor, 10), ServerLogAudience.Friend);
    var friendText = string.Join(" ", remainder.Records.Select(item => item.Message));
    Require(remainder.Ok && remainder.Records.Count == 3 && remainder.Records.All(item => item.Message.Length <= 2048) &&
        !friendText.Contains("host-secret", StringComparison.Ordinal) &&
        !friendText.Contains("token-secret", StringComparison.Ordinal) &&
        !friendText.Contains("code-secret", StringComparison.Ordinal) &&
        !friendText.Contains("Alice", StringComparison.Ordinal) &&
        !friendText.Contains("123456789", StringComparison.Ordinal) &&
        !friendText.Contains("192.168.1.4", StringComparison.Ordinal) &&
        !friendText.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase) &&
        !friendText.Contains(decoySecret, StringComparison.Ordinal) &&
        remainder.Records.All(item => item.Message.All(character => !char.IsControl(character))),
        "Friend log output leaked a secret, identity, address, path, decoy run, or injected control character");
    var host = await logs.ReadAsync(profile.Id, new(Limit: 10), ServerLogAudience.Host);
    var hostText = string.Join(" ", host.Records.Select(item => item.Message));
    Require(!hostText.Contains("host-secret", StringComparison.Ordinal) &&
        !hostText.Contains("token-secret", StringComparison.Ordinal) &&
        !hostText.Contains("code-secret", StringComparison.Ordinal),
        "Host diagnostic output exposed protected app secrets");
    Require((await logs.ReadAsync(profile.Id, new(Limit: 0), ServerLogAudience.Host)).Code == "InvalidLogQuery" &&
        (await logs.ReadAsync(profile.Id, new(Contains: "bad\nfilter"), ServerLogAudience.Host)).Code == "InvalidLogQuery" &&
        (await logs.ReadAsync(profile.Id, new("not-a-cursor", 10), ServerLogAudience.Host)).Code == "InvalidLogCursor" &&
        (await logs.ReadAsync(custom.Id, new(), ServerLogAudience.Friend)).Code == "CustomRemoteLogsUnavailable",
        "limit, cursor, or Custom remote source failed open");

    run.LogPath = Path.Combine(root, "unrelated-sensitive.txt");
    File.WriteAllText(run.LogPath, "arbitrary-owner-file");
    data.SaveRuns([run]);
    var tampered = new ServerLogService(data, Manager(data));
    var denied = await tampered.ReadAsync(profile.Id, new(), ServerLogAudience.Host);
    Require(denied.Code == "LogSourceIdentityMismatch" && denied.Records.Count == 0,
        "a tampered run path enabled an arbitrary file read");

    using var endedData = Data("server-logs-ended");
    endedData.SaveSettings(Settings(profile));
    var endedOperation = Guid.NewGuid();
    File.WriteAllText(endedData.NewRunLogPath(endedOperation), "09/28/2026 12:05:00: shutdown complete\n");
    endedData.SaveRunArchive([new ManagedRunArchive(profile.Id, endedOperation, GameKinds.Valheim,
        profile.WorldId, null, null, true, "Stopped", DateTimeOffset.UtcNow, false)]);
    var endedLogs = new ServerLogService(endedData, Manager(endedData));
    var ended = await endedLogs.ReadAsync(profile.Id, new(), ServerLogAudience.Host);
    var friendEnded = await endedLogs.ReadAsync(profile.Id, new(), ServerLogAudience.Friend);
    Require(ended.Ok && ended.SourceState == ServerLogSourceStates.Ended &&
        ended.RunId == endedOperation.ToString("N") && ended.Records.Count == 1 &&
        !friendEnded.Ok && friendEnded.Code == "FriendRetainedLogsUnavailable" &&
        friendEnded.SourceState == ServerLogSourceStates.Ended && friendEnded.RunId is null &&
        friendEnded.Records.Count == 0,
        "Host retained logs or active-run-only Friend isolation was not enforced");
});

await Check("managed server log retention runs at startup and before reads", async () =>
{
    var retentionRoot = Path.Combine(root, "server-log-retention");
    var directory = Path.Combine(retentionRoot, "logs");
    Directory.CreateDirectory(directory);
    var startupExpired = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log");
    var startupRecent = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log");
    File.WriteAllText(startupExpired, "expired before startup\n");
    File.WriteAllText(startupRecent, "recent before startup\n");
    File.SetLastWriteTimeUtc(startupExpired, DateTime.UtcNow.AddDays(-31));
    File.SetLastWriteTimeUtc(startupRecent, DateTime.UtcNow.AddDays(-29));

    using var data = new LocalData(retentionRoot);
    Require(!File.Exists(startupExpired) && File.Exists(startupRecent),
        "startup did not enforce the documented 30-day owned-log retention boundary");

    var profile = Profile("log-retention-profile", "log-retention-world", FreePort());
    profile.Kind = GameKinds.Valheim;
    data.SaveSettings(Settings(profile));
    var operationId = Guid.NewGuid();
    var activePath = data.NewRunLogPath(operationId);
    File.WriteAllText(activePath, "09/28/2026 12:00:00: active owned run\n");
    data.SaveRuns([new ManagedRun
    {
        ProfileId = profile.Id,
        OperationId = operationId,
        Kind = GameKinds.Valheim,
        WorldId = profile.WorldId,
        LogPath = activePath
    }]);
    File.SetLastWriteTimeUtc(activePath, DateTime.UtcNow.AddDays(-31));
    var expiredBeforeRead = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log");
    File.WriteAllText(expiredBeforeRead, "expired before read\n");
    File.SetLastWriteTimeUtc(expiredBeforeRead, DateTime.UtcNow.AddDays(-31));

    _ = await new ServerLogService(data, Manager(data))
        .ReadAsync(profile.Id, new(), ServerLogAudience.Host);
    Require(!File.Exists(expiredBeforeRead) && File.Exists(activePath),
        "read-time retention did not remove an expired unowned log or preserve the recorded active run");

    var blockedRoot = Path.Combine(root, "server-log-retention-blocked");
    var blockedLogs = Path.Combine(blockedRoot, "logs");
    Directory.CreateDirectory(blockedLogs);
    var blockedEvidence = Path.Combine(blockedLogs, Guid.NewGuid().ToString("N") + ".log");
    File.WriteAllText(blockedEvidence, "retained while run identity needs recovery\n");
    File.SetLastWriteTimeUtc(blockedEvidence, DateTime.UtcNow.AddDays(-31));
    File.WriteAllText(Path.Combine(blockedRoot, "runs.json"), "{not-json");
    using var blockedData = new LocalData(blockedRoot);
    Require(blockedData.Recovery.LifecycleBlocked && File.Exists(blockedEvidence),
        "retention deleted possible run evidence while authoritative lifecycle state was quarantined");
});

await Check("port diagnostics show local game and Friend listeners honestly", async () =>
{
    using var data = Data("port-diagnostics");
    var gamePort = FreePort();
    using var game = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
    using var query = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
    game.Bind(new IPEndPoint(IPAddress.Loopback, gamePort));
    query.Bind(new IPEndPoint(IPAddress.Loopback, gamePort + 1));
    using var control = new TcpListener(IPAddress.Loopback, 0);
    control.Start();
    var controlPort = ((IPEndPoint)control.LocalEndpoint).Port;
    var profile = new ServerProfile
    {
        Kind = GameKinds.Valheim,
        Name = "Port check",
        WorldId = "port-check",
        WorldDirectory = root,
        ExecutablePath = fixture,
        GamePort = gamePort
    };
    var settings = Settings(profile);
    settings.CompanionPort = controlPort;
    settings.CompanionBindAddress = "127.0.0.1";
    settings.CompanionListeningEnabled = true;
    settings.CompanionEndpoint = $"https://1.2.3.4:{controlPort}";
    settings.PublicGameIp = "1.2.3.4";
    settings.PublicGameIpCheckedUtc = DateTimeOffset.UtcNow;
    var snapshot = new HostSnapshot(settings, [new RunView(profile.Id, "Ready", "fixture", 1)],
        "fixture", "Host", new Dictionary<Guid, bool>(), root);
    var device = new DeviceView(Guid.NewGuid(), profile.Id, [profile.Id], "Friend PC", true, false, false, true,
        DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
    var diagnostics = PortDiagnostics.Read(snapshot, Games(data), true, [device]);
    Require(diagnostics.Games.Single().State == "Loopback only" &&
        diagnostics.Games.Single().RouteKind == "Direct" && diagnostics.Games.Single().Kind == GameKinds.Valheim,
        "loopback-only fixture ports or their direct route were not reported");
    Require(diagnostics.Control.State == "Open on PC" && diagnostics.Control.BindScope == "Loopback only" &&
        diagnostics.Control.EndpointState == "Address hint" && diagnostics.Control.RemoteState == "Friend connected" &&
        diagnostics.Control.RemoteDetail.Contains("network location is unknown", StringComparison.Ordinal),
        "local TCP listener or authenticated Friend evidence overstated the outside-network route");
    profile.Crossplay = true;
    diagnostics = PortDiagnostics.Read(snapshot, Games(data), true, [device]);
    Require(diagnostics.Games.Single().RouteKind == "Relay" && diagnostics.Games.Single().State == "Relay ready",
        "Valheim Crossplay relay was presented as direct game-port forwarding");
    profile.Crossplay = false;
    settings.CompanionBindAddress = "0.0.0.0";
    diagnostics = PortDiagnostics.Read(snapshot, Games(data), true, [device]);
    Require(diagnostics.Control.State == "Closed on PC" && diagnostics.Control.RemoteState == "Not verified",
        "a listener on the wrong bind address was reported open");
    settings.CompanionBindAddress = "127.0.0.1";
    var staleDevice = device with { LastHeartbeatUtc = DateTimeOffset.UtcNow.AddSeconds(-46) };
    diagnostics = PortDiagnostics.Read(snapshot, Games(data), true, [staleDevice]);
    Require(diagnostics.Control.RemoteState == "Not verified", "a stale heartbeat was reported as connected");
    diagnostics = PortDiagnostics.Read(snapshot, Games(data), false, [], "TLS listener failed");
    Require(diagnostics.Control.State == "Not listening" && diagnostics.Control.Detail == "TLS listener failed",
        "an enabled but failed HTTPS listener was reported off");
    diagnostics = PortDiagnostics.Read(snapshot, Games(data), false, [], null, CompanionListenerStates.Idle);
    Require(diagnostics.Control.State == "Idle" && diagnostics.Control.RemoteState == "Not needed" &&
        diagnostics.Control.Detail.Contains("Nothing is wrong", StringComparison.Ordinal),
        "an unused Friend listener was presented as a connection failure");
    query.Dispose();
    diagnostics = PortDiagnostics.Read(snapshot, Games(data), true, []);
    Require(diagnostics.Games.Single().State == "Closed on PC" && diagnostics.Control.RemoteState == "Not verified",
        "a missing UDP port or absent Friend route was claimed as open");
    using var loopbackGame = new TcpListener(IPAddress.Loopback, 0);
    loopbackGame.Start();
    var javaProfile = new ServerProfile
    {
        Kind = GameKinds.MinecraftJava,
        Name = "Local game",
        GamePort = ((IPEndPoint)loopbackGame.LocalEndpoint).Port
    };
    var javaSnapshot = new HostSnapshot(Settings(javaProfile),
        [new RunView(javaProfile.Id, "Ready", "fixture", 1)],
        "fixture", "Host", new Dictionary<Guid, bool>(), root);
    var javaPorts = PortDiagnostics.Read(javaSnapshot, Games(data), false, []);
    Require(javaPorts.Games.Single().State == "Loopback only" &&
        javaPorts.Games.Single().Kind == GameKinds.MinecraftJava,
        "a loopback-only game socket was reported as available to other PCs");
    await Task.CompletedTask;
});

await Check("Windows startup and tray preference stay scoped to this user", async () =>
{
    var keyPath = @"Software\TogetherServer\Checks\" + Guid.NewGuid().ToString("N");
    var appDirectory = Path.Combine(root, "desktop preferences with spaces");
    Directory.CreateDirectory(appDirectory);
    var appPath = Path.Combine(appDirectory, "TogetherServer.exe");
    File.WriteAllText(appPath, "disposable startup path");
    var startup = new WindowsStartup(appPath, keyPath);
    try
    {
        Require(!startup.IsEnabled(), "startup was enabled before the user opted in");
        startup.SetEnabled(true);
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            Require(key?.GetValue("TogetherServer") as string == $"\"{appPath}\" --startup",
                "Windows startup did not quote the exact EXE path and tray argument");
        Require(startup.IsEnabled(), "saved startup entry was not recognized");
        var missing = new WindowsStartup(Path.Combine(appDirectory, "missing", "TogetherServer.exe"), keyPath);
        try { missing.SetEnabled(true); throw new Exception("a missing EXE was registered at sign-in"); }
        catch (InvalidOperationException) { }
        Require(startup.IsEnabled(), "an invalid path replaced the existing startup entry");
        startup.SetEnabled(false);
        Require(!startup.IsEnabled(), "disabling startup left its entry behind");
        using var data = Data("desktop-preferences");
        Require(!data.LoadDesktopPreferences().CloseToTray, "close-to-tray was enabled by default");
        data.SaveDesktopPreferences(new DesktopPreferences { CloseToTray = true });
        Require(data.LoadDesktopPreferences().CloseToTray, "close-to-tray did not persist");
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
    await Task.CompletedTask;
});

await Check("installer choices are optional, bounded, and preserve later app control", async () =>
{
    var keyPath = @"Software\TogetherServer\Checks\Installer\" + Guid.NewGuid().ToString("N");
    var caseRoot = Path.Combine(root, "installer-integration");
    var dataRoot = Path.Combine(caseRoot, "data");
    var appDirectory = Path.Combine(caseRoot, "installed app with spaces");
    Directory.CreateDirectory(appDirectory);
    var appPath = Path.Combine(appDirectory, "TogetherServer.exe");
    File.WriteAllText(appPath, "disposable installed app");
    string? EnvironmentValue(string name) => name == "TOGETHERSERVER_DATA_DIR" ? dataRoot : null;
    try
    {
        Require(InstallerIntegration.Run(
            [InstallerIntegration.ConfigureCommand, "--mode", "friend", "--close-to-tray", "--launch-at-login"],
            appPath, EnvironmentValue, Path.Combine(caseRoot, "unused-local-app-data"), keyPath) == 0,
            "valid optional installer choices were rejected");
        using (var data = new LocalData(dataRoot))
        {
            Require(data.LoadPreferredMode() == "Friend", "installer mode choice did not persist");
            Require(data.LoadDesktopPreferences().CloseToTray, "installer tray choice did not persist");
        }
        var startup = new WindowsStartup(appPath, keyPath);
        Require(startup.IsEnabled(), "installer startup choice did not register the installed EXE");

        Require(InstallerIntegration.Run([InstallerIntegration.ConfigureCommand, "--mode", "host"],
            appPath, EnvironmentValue, Path.Combine(caseRoot, "unused-local-app-data"), keyPath) == 0,
            "a later explicit installer mode choice failed");
        using (var data = new LocalData(dataRoot))
        {
            Require(data.LoadPreferredMode() == "Host", "later explicit Host choice was not applied");
            Require(data.LoadDesktopPreferences().CloseToTray,
                "an omitted installer tray choice disabled an existing app preference");
        }
        Require(startup.IsEnabled(), "an omitted installer startup choice disabled an existing registration");

        Require(InstallerIntegration.Run([InstallerIntegration.ConfigureCommand], appPath,
            EnvironmentValue, Path.Combine(caseRoot, "unused-local-app-data"), keyPath) == 2,
            "empty installer setup was accepted instead of remaining a no-op");
        Require(InstallerIntegration.Run([InstallerIntegration.ConfigureCommand, "--mode", "server"], appPath,
            EnvironmentValue, Path.Combine(caseRoot, "unused-local-app-data"), keyPath) == 2,
            "an unbounded installer mode was accepted");

        Require(InstallerIntegration.Run([InstallerIntegration.RemoveStartupCommand], appPath,
            EnvironmentValue, Path.Combine(caseRoot, "unused-local-app-data"), keyPath) == 0 && !startup.IsEnabled(),
            "uninstall cleanup did not remove its exact startup registration");
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
            key!.SetValue("TogetherServer", "\"C:\\Another App\\TogetherServer.exe\" --startup");
        Require(InstallerIntegration.Run([InstallerIntegration.RemoveStartupCommand], appPath,
            EnvironmentValue, Path.Combine(caseRoot, "unused-local-app-data"), keyPath) == 0,
            "uninstall cleanup rejected a foreign startup registration");
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            Require(key?.GetValue("TogetherServer") is not null,
                "uninstall cleanup removed another TogetherServer location's startup registration");
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
    await Task.CompletedTask;
});

await Check("remote operations persist idempotency and interrupt unfinished work", async () =>
{
    using var data = Data("remote-operations");
    var interruptedId = Guid.NewGuid();
    var interruptedDevice = Guid.NewGuid();
    var seeded = Enumerable.Range(0, 501).Select(index =>
        new RemoteOperation
        {
            DeviceId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            ProfileId = Guid.NewGuid(),
            Action = "stop",
            State = RemoteOperationStates.Running,
            RequestedUtc = DateTimeOffset.UtcNow.AddSeconds(index - 501),
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        }).ToList();
    seeded[^1].Id = interruptedId;
    seeded[^1].DeviceId = interruptedDevice;
    data.SaveRemoteOperations(seeded);
    await using var coordinator = new RemoteOperationCoordinator(data);
    var interrupted = coordinator.Find(interruptedDevice, interruptedId);
    Require(interrupted is { State: RemoteOperationStates.Interrupted, Code: "HostRestarted", Ok: false },
        "unfinished operation was not interrupted after Host restart");
    Require(data.LoadRemoteOperations().Count == 500,
        "operation journal did not retain an exact bounded newest-500 set");

    var deviceId = Guid.NewGuid();
    var requestId = Guid.NewGuid();
    var profileId = Guid.NewGuid();
    var executions = 0;
    var secondExecutions = 0;
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var submitted = coordinator.Submit(deviceId, requestId, profileId, "restart", async () =>
    {
        Interlocked.Increment(ref executions);
        await release.Task;
        return new RemoteOperationOutcome(true, "ServerRestarted",
            @"Restart completed using C:\private\world and secret script output.");
    });
    Require(submitted.Accepted && submitted.Operation is not null, "operation was not accepted");
    var unknown = coordinator.Submit(deviceId, Guid.NewGuid(), profileId, "arbitrary-action", () =>
        Task.FromResult(new RemoteOperationOutcome(true, "Unexpected", "must not execute")));
    Require(!unknown.Accepted && unknown.Code == "UnknownAction",
        "an unsupported remote action entered the durable queue");
    var submittedOperation = submitted.Operation ?? throw new Exception("accepted operation had no view");
    var retry = coordinator.Submit(deviceId, requestId, profileId, "restart", () =>
        Task.FromResult(new RemoteOperationOutcome(false, "Duplicate", "must not execute")));
    for (var attempt = 0; attempt < 100 && executions == 0; attempt++) await Task.Delay(10);
    Require(retry.Accepted && retry.Existing && retry.Operation?.Id == submittedOperation.Id && executions == 1,
        "idempotent retry did not return the original operation");
    var conflict = coordinator.Submit(deviceId, requestId, profileId, "stop", () =>
        Task.FromResult(new RemoteOperationOutcome(true, "Wrong", "must not execute")));
    Require(!conflict.Accepted && conflict.Code == "IdempotencyConflict",
        "request ID reuse for another action was accepted");
    var second = coordinator.Submit(deviceId, Guid.NewGuid(), Guid.NewGuid(), "start", () =>
    {
        Interlocked.Increment(ref secondExecutions);
        return Task.FromResult(new RemoteOperationOutcome(true, "FixtureStarted", "Second operation completed."));
    });
    Require(second.Accepted && second.Operation is not null, "second queued operation was not accepted");
    await Task.Delay(100);
    Require(secondExecutions == 0 && coordinator.Find(deviceId, second.Operation!.Id)?.State == RemoteOperationStates.Pending,
        "the in-process executor ran more than one remote operation at a time or skipped Pending");
    release.SetResult();
    RemoteOperationView? completed = null;
    for (var attempt = 0; attempt < 100; attempt++)
    {
        completed = coordinator.Find(deviceId, submittedOperation.Id);
        if (completed is not null && RemoteOperationStates.Terminal(completed.State)) break;
        await Task.Delay(20);
    }
    RemoteOperationView? secondCompleted = null;
    for (var attempt = 0; attempt < 100; attempt++)
    {
        secondCompleted = coordinator.Find(deviceId, second.Operation!.Id);
        if (secondCompleted is not null && RemoteOperationStates.Terminal(secondCompleted.State)) break;
        await Task.Delay(20);
    }
    Require(completed is { State: RemoteOperationStates.Succeeded, Code: "ServerRestarted", Ok: true } &&
        !completed.Message.Contains("private", StringComparison.OrdinalIgnoreCase) &&
        !completed.Message.Contains("script output", StringComparison.OrdinalIgnoreCase) &&
        secondCompleted is { State: RemoteOperationStates.Succeeded } && executions == 1 && secondExecutions == 1,
        "FIFO execution, one terminal execution, or operation-result redaction failed");
    await coordinator.DisposeAsync();
    Require(coordinator.Submit(deviceId, Guid.NewGuid(), profileId, "start", () =>
            Task.FromResult(new RemoteOperationOutcome(true, "Unexpected", "Must not run."))).Code ==
        "OperationCoordinatorStopped", "a disposed operation worker accepted new work");
    await using var reloadedCoordinator = new RemoteOperationCoordinator(data);
    var reloaded = reloadedCoordinator.Find(deviceId, submittedOperation.Id);
    Require(reloaded is { State: RemoteOperationStates.Succeeded }, "terminal operation did not survive reload");
});

await Check("remote operation execution survives an unavailable audit log", async () =>
{
    var dataRoot = Path.Combine(root, "remote-operation-audit");
    using var data = new LocalData(dataRoot);
    await using var coordinator = new RemoteOperationCoordinator(data);
    using var auditLock = new FileStream(Path.Combine(dataRoot, "audit.log"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);
    var executions = 0;
    var deviceId = Guid.NewGuid();
    var submitted = coordinator.Submit(deviceId, Guid.NewGuid(), Guid.NewGuid(), "start", () =>
    {
        Interlocked.Increment(ref executions);
        return Task.FromResult(new RemoteOperationOutcome(true, "FixtureStarted", "Started."));
    });
    Require(submitted.Accepted && submitted.Operation is not null, "audit lock prevented operation acceptance");
    RemoteOperationView? completed = null;
    for (var attempt = 0; attempt < 100; attempt++)
    {
        completed = coordinator.Find(deviceId, submitted.Operation!.Id);
        if (completed is not null && RemoteOperationStates.Terminal(completed.State)) break;
        await Task.Delay(20);
    }
    Require(executions == 1 && completed is { State: RemoteOperationStates.Succeeded },
        "an audit write failure stranded or suppressed an accepted operation");
});

await Check("remote operation disposal interrupts queued work without executing it", async () =>
{
    using var data = Data("remote-operation-disposal");
    var coordinator = new RemoteOperationCoordinator(data);
    var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var deviceId = Guid.NewGuid();
    var first = coordinator.Submit(deviceId, Guid.NewGuid(), Guid.NewGuid(), "start", async () =>
    {
        firstStarted.TrySetResult(true);
        await releaseFirst.Task;
        return new RemoteOperationOutcome(true, "FixtureStarted", "Started.");
    });
    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var queuedExecutions = 0;
    var second = coordinator.Submit(deviceId, Guid.NewGuid(), Guid.NewGuid(), "start", () =>
    {
        Interlocked.Increment(ref queuedExecutions);
        return Task.FromResult(new RemoteOperationOutcome(true, "Unexpected", "Must not run."));
    });
    var disposing = coordinator.DisposeAsync().AsTask();
    releaseFirst.TrySetResult(true);
    await disposing.WaitAsync(TimeSpan.FromSeconds(5));
    var interrupted = coordinator.Find(deviceId, second.Operation!.Id);
    Require(first.Accepted && second.Accepted && queuedExecutions == 0 &&
        interrupted is { State: RemoteOperationStates.Interrupted, Code: "HostShuttingDown", Ok: false },
        "disposing the owned worker ran or stranded queued remote work");
});

await Check("remote operation rejects safely when its journal cannot commit", async () =>
{
    var dataRoot = Path.Combine(root, "remote-operation-journal");
    using var data = new LocalData(dataRoot);
    data.SaveRemoteOperations([]);
    await using var coordinator = new RemoteOperationCoordinator(data);
    using var journalLock = new FileStream(Path.Combine(dataRoot, "remote-operations.json"), FileMode.Open,
        FileAccess.Read, FileShare.Read);
    var executions = 0;
    var submitted = coordinator.Submit(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "stop", () =>
    {
        Interlocked.Increment(ref executions);
        return Task.FromResult(new RemoteOperationOutcome(true, "Unexpected", "Must not run."));
    });
    await Task.Delay(100);
    Require(!submitted.Accepted && submitted.Code == "OperationJournalUnavailable" &&
        executions == 0 && coordinator.Recent().Count == 0,
        "an uncommitted operation remained in memory or executed without a durable journal entry");
});

await Check("authenticated request limiting is isolated per verified device", async () =>
{
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    var limiter = new AuthenticatedDeviceRateLimiter(3, TimeSpan.FromMinutes(1), clock);
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    Require(limiter.TryAcquire(first) && limiter.TryAcquire(first) && limiter.TryAcquire(first) &&
        !limiter.TryAcquire(first), "one authenticated device did not reach its own request limit");
    Require(limiter.TryAcquire(second), "one device's limit throttled another verified device");
    clock.Advance(TimeSpan.FromMinutes(1));
    Require(limiter.TryAcquire(first), "the authenticated device limit did not reset after its fixed window");
    await Task.CompletedTask;
});

await Check("exact-run session evidence accumulates trusted player observations across Host restart", async () =>
{
    using var data = Data("session-player-evidence");
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
    var profile = Profile("session-player-evidence", "session-private-world", FreePort());
    var driver = new ObservationFixtureDriver
    {
        HealthResult = new(true, "FixtureReady", "Ready", "Trusted fixture observation.",
            OnlinePlayers: 2, MaxPlayers: 10, PlayerCountTrusted: true)
    };
    var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.LoopbackOnly), clock);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    Require((await manager.StartAsync(profile.Id)).Ok, "start failed");
    await manager.RefreshObservationsAsync();
    var recorded = data.LoadRuns().Single();
    Require(recorded.WasReady && recorded.LastTrustedOnlinePlayers == 2 &&
        recorded.MaximumTrustedOnlinePlayers == 2,
        "the canonical supervisor did not persist the first trusted exact-run count");

    driver.HealthResult = new(false, "PlayerCountUnknown", "Unknown", "No trusted count.",
        OnlinePlayers: 0, MaxPlayers: 10, PlayerCountTrusted: false);
    await manager.RefreshObservationsAsync();
    recorded = data.LoadRuns().Single();
    Require(recorded.LastTrustedOnlinePlayers == 2 && recorded.MaximumTrustedOnlinePlayers == 2,
        "an Unknown or untrusted observation overwrote trusted evidence with zero");

    var restarted = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.LoopbackOnly), clock);
    driver.HealthResult = new(true, "FixtureReady", "Ready", "Trusted fixture observation.",
        OnlinePlayers: 5, MaxPlayers: 10, PlayerCountTrusted: true);
    await restarted.RefreshObservationsAsync();
    driver.HealthResult = new(true, "FixtureReady", "Ready", "Trusted fixture observation.",
        OnlinePlayers: 0, MaxPlayers: 10, PlayerCountTrusted: true);
    await restarted.RefreshObservationsAsync();
    recorded = data.LoadRuns().Single();
    Require(recorded.LastTrustedOnlinePlayers == 0 && recorded.MaximumTrustedOnlinePlayers == 5,
        "the exact-run last/maximum accumulator did not survive restart or retain its maximum");

    Require((await restarted.StopAsync(profile.Id)).Ok, "cleanup stop failed");
    var summary = data.LoadRunArchive().Single();
    Require(summary.SummaryVersion == HostManager.CurrentSessionSummaryVersion &&
        summary.Outcome == ServerSessionOutcome.GracefulStop &&
        summary.BackupResult == ServerSessionBackupResult.NotConfigured &&
        summary.WasReady && summary.StartedUtc is not null && summary.EndedUtc is not null &&
        summary.DurationSeconds is >= 0 && summary.LastTrustedOnlinePlayers == 0 &&
        summary.MaximumTrustedOnlinePlayers == 5,
        "the immutable graceful-stop summary lost authoritative run evidence");
    var recent = await restarted.RecentSessionsAsync(profile.Id, 8);
    var serialized = JsonSerializer.Serialize(recent, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Require(recent.Ok && recent.Sessions.Single().OperationId == summary.OperationId &&
        !serialized.Contains(profile.WorldDirectory, StringComparison.OrdinalIgnoreCase) &&
        !serialized.Contains(profile.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        !serialized.Contains(profile.WorldId, StringComparison.Ordinal),
        "the recent-session projection exposed a private path/world identifier or lost run identity");
});

await Check("failed and unconfirmed Stop never archive a successful session", async () =>
{
    using var data = Data("session-stop-failure");
    var profile = Profile("session-stop-failure", "session-stop-failure", FreePort());
    var driver = new ObservationFixtureDriver();
    var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    Require((await manager.StartAsync(profile.Id)).Ok, "start failed");

    driver.StopBehavior = FixtureStopBehavior.Failed;
    var failed = await manager.StopAsync(profile.Id);
    Require(!failed.Ok && data.LoadRuns().Count == 1 && data.LoadRunArchive().Count == 0,
        "a failed Stop archived a successful session");
    driver.StopBehavior = FixtureStopBehavior.Unconfirmed;
    var unconfirmed = await manager.StopAsync(profile.Id);
    Require(!unconfirmed.Ok && unconfirmed.Code == "StopUnconfirmed" &&
        data.LoadRuns().Count == 1 && data.LoadRunArchive().Count == 0,
        "an unconfirmed Stop archived a successful session");

    driver.StopBehavior = FixtureStopBehavior.Normal;
    Require((await manager.StopAsync(profile.Id)).Ok &&
        data.LoadRunArchive().Single().Outcome == ServerSessionOutcome.GracefulStop,
        "normal fixture cleanup did not archive the later confirmed graceful Stop");

    Require((await manager.StartAsync(profile.Id)).Ok, "interrupted-stop fixture start failed");
    var interrupted = data.LoadRuns().Single();
    interrupted.StopRequestedUtc = DateTimeOffset.UtcNow;
    data.SaveRuns([interrupted]);
    await KillFixture(interrupted);
    var restarted = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.LoopbackOnly));
    await restarted.RefreshObservationsAsync();
    var interruptedSummary = data.LoadRunArchive().Single(item => item.OperationId == interrupted.OperationId);
    Require(interruptedSummary.EndReason == ServerSessionEndReason.ProcessExited &&
        interruptedSummary.Outcome == ServerSessionOutcome.StopUnconfirmed &&
        interruptedSummary.BackupResult == ServerSessionBackupResult.NotAttempted,
        "persisted but unconfirmed Stop intent was archived as a successful session");
});

await Check("failed-before-Ready and owner-archived exits have distinct typed outcomes", async () =>
{
    using var data = Data("session-exit-outcomes");
    var failedProfile = Profile("session-failed-before-ready", "session-failed-before-ready", FreePort());
    var ownerProfile = Profile("session-owner-archive", "session-owner-archive", FreePort());
    var manager = Manager(data);
    Require((await manager.UpdateSettingsAsync(Settings(failedProfile, ownerProfile))).Ok, "settings failed");

    Require((await manager.StartAsync(failedProfile.Id)).Ok, "failed-before-Ready start failed");
    var failedRun = data.LoadRuns().Single(item => item.ProfileId == failedProfile.Id);
    await KillFixture(failedRun);
    await manager.RefreshObservationsAsync();
    Require(data.LoadRunArchive().Single(item => item.ProfileId == failedProfile.Id).Outcome ==
        ServerSessionOutcome.FailedBeforeReady,
        "a pre-Ready exit was not recorded distinctly");

    Require((await manager.StartAsync(ownerProfile.Id)).Ok, "owner-archive start failed");
    var ownerRun = data.LoadRuns().Single(item => item.ProfileId == ownerProfile.Id);
    await KillFixture(ownerRun);
    Require((await manager.ForgetAsync(ownerProfile.Id)).Ok &&
        data.LoadRunArchive().Single(item => item.ProfileId == ownerProfile.Id).Outcome ==
            ServerSessionOutcome.OwnerArchivedExited,
        "the owner-archived exited record did not retain its distinct typed outcome");
});

await Check("legacy archives load as incomplete and recent-session retention and limits stay bounded", async () =>
{
    var legacyRoot = Path.Combine(root, "session-legacy-bounds");
    var profile = Profile("session-legacy-bounds", "session-legacy-bounds", FreePort());
    using (var seed = new LocalData(legacyRoot)) seed.SaveSettings(Settings(profile));
    var legacyOperation = Guid.NewGuid();
    var legacyArchivedUtc = DateTimeOffset.UtcNow.AddHours(-1);
    File.WriteAllText(Path.Combine(legacyRoot, "run-archive.json"), JsonSerializer.Serialize(new[]
    {
        new
        {
            profileId = profile.Id,
            operationId = legacyOperation,
            kind = GameKinds.Fixture,
            worldId = profile.WorldId,
            processId = (int?)null,
            startTimeUtcTicks = (long?)null,
            wasReady = true,
            reason = "LegacyReason",
            archivedUtc = legacyArchivedUtc,
            crashRecoveryScheduled = false
        }
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    using var data = new LocalData(legacyRoot);
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
    var manager = new HostManager(data, Games(data), clock);
    var legacy = (await manager.RecentSessionsAsync(profile.Id, 8)).Sessions.Single();
    Require(legacy.OperationId == legacyOperation && legacy.EndedUtc is null &&
        legacy.StartedUtc is null && legacy.DurationSeconds is null &&
        legacy.ReadyEverObserved is null && legacy.Outcome is null && legacy.BackupResult is null,
        "a legacy archive failed to load or fabricated unavailable summary fields");

    var endedUtc = clock.GetUtcNow().AddMinutes(-1);
    var startedUtc = endedUtc.AddHours(-1);
    data.SaveRunArchive([
        new ManagedRunArchive(profile.Id, Guid.NewGuid(), GameKinds.Fixture, profile.WorldId,
            null, null, true, "GracefulStop", clock.GetUtcNow(), false,
            HostManager.CurrentSessionSummaryVersion, endedUtc.AddMinutes(1), endedUtc, 60,
            ServerSessionEndReason.GracefulStop, ServerSessionOutcome.GracefulStop,
            0, 1, ServerSessionBackupResult.NotConfigured),
        new ManagedRunArchive(profile.Id, Guid.NewGuid(), GameKinds.Fixture, profile.WorldId,
            null, null, true, "GracefulStop", clock.GetUtcNow(), false,
            HostManager.CurrentSessionSummaryVersion, startedUtc, endedUtc, 3600,
            ServerSessionEndReason.GracefulStop, ServerSessionOutcome.GracefulStop,
            0, 1_000_001, ServerSessionBackupResult.NotConfigured),
        new ManagedRunArchive(profile.Id, Guid.NewGuid(), GameKinds.Fixture, profile.WorldId,
            null, null, true, "ProcessExited", clock.GetUtcNow(), false,
            HostManager.CurrentSessionSummaryVersion, startedUtc, endedUtc, 3600,
            ServerSessionEndReason.ProcessExited, ServerSessionOutcome.UnexpectedExit,
            0, 1, ServerSessionBackupResult.Completed)
    ]);
    var malformed = (await manager.RecentSessionsAsync(profile.Id, 8)).Sessions;
    Require(malformed.Count == 3 && malformed.All(item => item.StartedUtc is null &&
            item.EndedUtc is null && item.DurationSeconds is null && item.ReadyEverObserved is null &&
            item.EndReason is null && item.Outcome is null && item.CrashRecoveryScheduled is null &&
            item.LastTrustedOnlinePlayers is null && item.MaximumTrustedOnlinePlayers is null &&
            item.BackupResult is null),
        "malformed versioned summaries escaped as current evidence instead of honest unavailable gaps");

    var seeded = Enumerable.Range(0, 505).Select(index => new ManagedRunArchive(profile.Id,
        Guid.NewGuid(), GameKinds.Fixture, profile.WorldId, null, null, false, "Legacy",
        clock.GetUtcNow().AddMinutes(-(index + 1)), false)).ToList();
    seeded.Add(new ManagedRunArchive(profile.Id, Guid.NewGuid(), GameKinds.Fixture, profile.WorldId,
        null, null, false, "Expired", clock.GetUtcNow().AddDays(-31), false));
    data.SaveRunArchive(seeded);
    Require((await manager.StartAsync(profile.Id)).Ok, "retention fixture start failed");
    var newestOperation = data.LoadRuns().Single().OperationId;
    Require((await manager.StopAsync(profile.Id)).Ok, "retention fixture stop failed");
    var retained = data.LoadRunArchive();
    var recent = await manager.RecentSessionsAsync(profile.Id, 1000);
    Require(retained.Count == 500 && retained.Select(item => item.OperationId).Distinct().Count() == 500 &&
        retained.Any(item => item.OperationId == newestOperation) &&
        retained.All(item => item.ArchivedUtc >= clock.GetUtcNow().AddDays(-30)) &&
        recent.Sessions.Count == HostManager.MaximumRecentSessionLimit,
        "run archive retention, exact-run uniqueness, or recent-session response bounds changed");
});

await Check("definitive exits archive and crash recovery is bounded", async () =>
{
    using var data = Data("crash-recovery");
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    var profile = Profile("crash-recovery", "crash-recovery", FreePort());
    profile.CrashRecovery.Enabled = true;
    var starter = new HostManager(data, Games(data), clock);
    Require((await starter.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    Require((await starter.StartAsync(profile.Id)).Ok, "initial start failed");
    var original = data.LoadRuns().Single();
    original.WasReady = true; // Represents a prior authoritative Ready observation.
    data.SaveRuns([original]);
    await KillFixture(original);

    var manager = new HostManager(data, Games(data), clock);
    await manager.RefreshObservationsAsync();
    Require(data.LoadRuns().Count == 0, "definitively absent process was not archived");
    var initialSummary = data.LoadRunArchive().Single();
    Require(initialSummary.CrashRecoveryScheduled &&
        initialSummary.Outcome == ServerSessionOutcome.UnexpectedExit,
        "eligible Ready crash did not retain its unexpected-exit outcome or schedule recovery");
    var state = data.LoadCrashRecoveryStates().Single();
    Require(state.State == CrashRecoveryStates.Pending && state.Attempts == 0 &&
        state.NextAttemptUtc == clock.GetUtcNow().AddMinutes(1), "first recovery delay was not one minute");

    foreach (var delay in new[] { 1, 5, 15 })
    {
        clock.Advance(TimeSpan.FromMinutes(delay));
        var attempts = await manager.MaintainCrashRecoveryAsync();
        Require(attempts.Count == 1 && attempts[0].Ok, $"recovery launch after {delay} minutes failed");
        var recoveredRun = data.LoadRuns().Single();
        await KillFixture(recoveredRun);
        await manager.RefreshObservationsAsync();
    }
    state = data.LoadCrashRecoveryStates().Single();
    Require(state.State == CrashRecoveryStates.Suspended && state.Attempts == 3 && state.NextAttemptUtc is null,
        "recovery did not suspend after exactly three failed launches");
    var summaries = data.LoadRunArchive();
    Require(summaries.Count == 4 &&
        summaries.Count(item => item.Outcome == ServerSessionOutcome.RecoveryFailedBeforeReady) == 3,
        "each definitively absent exact fixture run or failed-before-Ready recovery outcome was not archived");
});

await Check("crash recovery suspends a live process that never becomes Ready", async () =>
{
    using var data = Data("crash-readiness-timeout");
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    var profile = Profile("crash-readiness-timeout", "crash-readiness-timeout", FreePort());
    profile.CrashRecovery.Enabled = true;
    var starter = new HostManager(data, Games(data), clock);
    Require((await starter.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    Require((await starter.StartAsync(profile.Id)).Ok, "initial start failed");
    var original = data.LoadRuns().Single();
    original.WasReady = true;
    data.SaveRuns([original]);
    await KillFixture(original);

    var manager = new HostManager(data, Games(data), clock);
    await manager.RefreshObservationsAsync();
    clock.Advance(TimeSpan.FromMinutes(1));
    Require((await manager.MaintainCrashRecoveryAsync()).Single().Ok, "recovery launch failed");
    var starting = data.LoadCrashRecoveryStates().Single();
    Require(starting.State == CrashRecoveryStates.Starting &&
        starting.ReadinessDeadlineUtc == clock.GetUtcNow().AddMinutes(5),
        "recovery launch did not record a bounded readiness deadline");
    var recoveredRun = data.LoadRuns().Single();
    clock.Advance(TimeSpan.FromMinutes(5));
    var timeout = await manager.MaintainCrashRecoveryAsync();
    var suspended = data.LoadCrashRecoveryStates().Single();
    Require(timeout.Any(result => result.Code == "RecoveryStartupTimedOut") &&
        suspended.State == CrashRecoveryStates.Suspended && suspended.ReadinessDeadlineUtc is null &&
        data.LoadRuns().Single().ProcessId == recoveredRun.ProcessId &&
        !Process.GetProcessById(recoveredRun.ProcessId!.Value).HasExited,
        "a never-Ready recovery stayed Starting forever or its live process was force-stopped");
    Require((await manager.StopAsync(profile.Id)).Ok, "timed-out recovery fixture cleanup failed");
});

await Check("hosting power request and resume revalidation stay scoped and fail closed", async () =>
{
    using var data = Data("hosting-power-resume");
    var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-29T12:00:00Z"));
    var profile = Profile("hosting-power-resume", "hosting-power-resume", FreePort());
    var settings = Settings(profile);
    settings.KeepAwakeWhileHosting = true;
    settings.AutoShutdownEnabled = true;
    settings.IdleMinutes = 1;
    var driver = new ObservationFixtureDriver
    {
        HealthResult = new(true, "FixtureReady", "Ready", "Trusted fixture observation.",
            OnlinePlayers: 0, MaxPlayers: 10, PlayerCountTrusted: true)
    };
    using var power = new RecordingPowerGuard();
    var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.LoopbackOnly), clock, power);
    Require((await manager.UpdateSettingsAsync(settings)).Ok && !power.IsActive,
        "the scoped power request activated while no managed server was running");
    Require((await manager.StartAsync(profile.Id)).Ok && power.IsActive,
        "the scoped power request did not activate for the exact managed process");
    await manager.RefreshObservationsAsync();
    Require((await manager.ExtendAutoShutdownAsync(profile.Id, 5)).Ok, "could not save countdown time");
    var beforeResume = await manager.SnapshotAsync();
    Require(beforeResume.Runs.Single().AutoShutdownAtUtc is not null &&
        beforeResume.Runs.Single().AddedShutdownMinutes == 5,
        "the trusted zero-player observation did not start the fixture countdown");
    var activityCount = data.LoadActivity(100).Count;
    var powerRequestCount = power.Requests.Count;
    _ = await manager.SnapshotAsync();
    _ = await manager.CompanionSnapshotAsync();
    Require(data.LoadActivity(100).Count == activityCount && power.Requests.Count == powerRequestCount,
        "a status read changed countdown activity or the scoped power request");

    clock.Advance(TimeSpan.FromMinutes(10));
    var resumed = await manager.HandleSystemResumeAsync();
    var resumedRun = resumed.Runs.Single();
    Require(resumedRun.AutoShutdownAtUtc is null && !resumedRun.PlayerCountTrusted &&
        resumedRun.AddedShutdownMinutes == 5 && power.IsActive,
        "resume reused stale readiness/count data, discarded saved time, or dropped the live scoped request");
    await manager.RefreshObservationsAsync();
    var refreshed = await manager.SnapshotAsync();
    Require(refreshed.Runs.Single().AutoShutdownAtUtc == clock.GetUtcNow().AddMinutes(6),
        "a fresh trusted post-resume observation did not create a new bounded countdown");
    Require((await manager.StopAsync(profile.Id)).Ok && !power.IsActive,
        "the scoped power request was not cleared after confirmed fixture Stop");
    Require(power.Requests.Contains(true) && power.Requests.Last() == false,
        "the power guard did not receive a scoped activate-then-clear lifecycle");
});

await Check("offline backup reserves its world while Host and Friend status stay responsive", async () =>
{
    using var data = Data("world-copy-reservation");
    var profile = Profile("reserved-world-source", "reserved-world", FreePort());
    profile.Backups = new BackupOptions { Enabled = false, RetentionCount = 5, MinimumFreeSpaceMb = 0 };
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.txt"), "unchanged source");
    var manager = Manager(data);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    var service = typeof(HostManager).GetField("backups", System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic)?.GetValue(manager)
        ?? throw new Exception("backup service not found");
    var sync = service.GetType().GetField("sync", System.Reflection.BindingFlags.Instance |
        System.Reflection.BindingFlags.NonPublic)?.GetValue(service)
        ?? throw new Exception("backup synchronization boundary not found");
    using var entered = new ManualResetEventSlim(false);
    using var released = new ManualResetEventSlim(false);
    var holder = Task.Run(() =>
    {
        lock (sync)
        {
            entered.Set();
            released.Wait();
        }
    });
    Task<ActionResult>? backup = null;
    try
    {
        Require(entered.Wait(TimeSpan.FromSeconds(5)), "backup service lock was not held");
        backup = manager.CreateManualBackupAsync(profile.Id);
        HostSnapshot? during = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            during = await manager.CompanionSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (during.Runs.Single().State == "World copy in progress") break;
            await Task.Delay(10);
        }
        Require(during?.Runs.Single().State == "World copy in progress",
            "the offline world copy did not publish its reservation");
        Require((await manager.SnapshotAsync().WaitAsync(TimeSpan.FromSeconds(2))).Runs.Single().State ==
            "World copy in progress", "owner status stalled or hid the world copy");
        var denied = await manager.StartAsync(profile.Id).WaitAsync(TimeSpan.FromSeconds(2));
        Require(!denied.Ok && denied.Code == "WorldCopyInProgress",
            "Start was allowed to race an offline world copy");
    }
    finally
    {
        released.Set();
        await holder;
    }
    var completed = backup is null ? throw new Exception("backup request was not started") : await backup;
    Require(completed.Ok &&
        File.ReadAllText(Path.Combine(profile.WorldDirectory, "world.txt")) == "unchanged source",
        $"the isolated backup failed or changed the live world: {completed.Code} {completed.Message}");
});

await Check("manual backup verification and Safe restart protect the live world", async () =>
{
    using var data = Data("manual-backup-safe-restart");
    var profile = Profile("manual-backup-safe-restart-world", "manual-backup-safe-restart", FreePort());
    var alias = Profile("manual-backup-safe-restart-alias", "manual-backup-safe-restart-alias", FreePort());
    alias.WorldId = profile.WorldId;
    alias.WorldDirectory = profile.WorldDirectory;
    profile.Backups = new BackupOptions { Enabled = false, RetentionCount = 5, MinimumFreeSpaceMb = 0 };
    var marker = Path.Combine(profile.WorldDirectory, "world.txt");
    File.WriteAllText(marker, "offline checkpoint");
    var manager = Manager(data);
    var settings = Settings(profile);
    settings.Profiles.Add(alias);
    Require((await manager.UpdateSettingsAsync(settings)).Ok, "settings failed");
    var manual = await manager.CreateManualBackupAsync(profile.Id);
    var list = await manager.BackupsAsync(profile.Id);
    var first = list.Backups.Single();
    Require(manual.Ok && first.BackupKind == BackupKinds.Manual &&
        list.Status.RetainedSizeBytes > 0 && list.Status.AvailableSpaceBytes is > 0,
        "offline Back up now did not create a visible manual checkpoint with capacity evidence");
    Require((await manager.VerifyBackupAsync(profile.Id, first.Id)).Ok,
        "a newly completed manual checkpoint did not pass manifest verification");

    var payload = Path.Combine(data.BackupsRoot, profile.Id.ToString("N"),
        first.Id.ToString("N") + ".backup", "payload", "world.txt");
    File.WriteAllText(payload, "tampered checkpoint");
    File.WriteAllText(marker, "live world remains");
    var tampered = await manager.VerifyBackupAsync(profile.Id, first.Id);
    Require(!tampered.Ok && tampered.Code == "BackupIntegrityFailed" &&
        File.ReadAllText(marker) == "live world remains",
        "verification accepted tampering or changed the live world");

    Require((await manager.StartAsync(profile.Id)).Ok, "fixture start failed");
    Require((await manager.CreateManualBackupAsync(profile.Id)).Code == "ServerRunning",
        "manual backup copied a world while its exact managed process was running");
    Require((await manager.CreateManualBackupAsync(alias.Id)).Code == "WorldRunning",
        "manual backup copied a save directory owned by another live profile");
    var restarted = await manager.SafeRestartAsync(profile.Id);
    Require(restarted.Ok && restarted.Code == "SafeRestartCompleted" && data.LoadRuns().Count == 1,
        "Safe restart did not complete its graceful Stop, offline checkpoint, then Start sequence");
    list = await manager.BackupsAsync(profile.Id);
    Require(list.Backups.Count(item => item.BackupKind == BackupKinds.Manual) == 2 &&
        list.Backups.First().BackupKind == BackupKinds.Manual,
        "Safe restart did not retain its offline manual checkpoint");
    Directory.Delete(profile.WorldDirectory, recursive: true);
    var failedRestart = await manager.SafeRestartAsync(profile.Id);
    Require(!failedRestart.Ok && failedRestart.Code == "SafeRestartBackupFailed" && data.LoadRuns().Count == 0,
        "Safe restart launched again after its required offline checkpoint failed");
});

await Check("graceful stop backup and offline restore protect the world", async () =>
{
    using var data = Data("backup-integration");
    var profile = Profile("backup-integration-world", "backup-integration", FreePort());
    profile.Backups = new BackupOptions { Enabled = true, RetentionCount = 3, MinimumFreeSpaceMb = 0 };
    var marker = Path.Combine(profile.WorldDirectory, "world.txt");
    File.WriteAllText(marker, "before stop");
    var manager = Manager(data);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    Require((await manager.StartAsync(profile.Id)).Ok, "start failed");
    var stopped = await manager.StopAsync(profile.Id);
    Require(stopped.Ok && stopped.Message.Contains("rolling backup", StringComparison.OrdinalIgnoreCase),
        "confirmed graceful stop did not complete its rolling backup");
    Require(data.LoadRunArchive().Single() is
    {
        Outcome: ServerSessionOutcome.GracefulStop,
        BackupResult: ServerSessionBackupResult.Completed
    },
        "the graceful-stop summary did not record its completed rolling backup");
    var backup = (await manager.BackupsAsync(profile.Id)).Backups.Single();
    File.WriteAllText(marker, "after backup");
    Require((await manager.StartAsync(profile.Id)).Ok, "second start failed");
    Require((await manager.RestoreBackupAsync(profile.Id, backup.Id)).Code == "ServerRunning",
        "restore was allowed while the exact managed process was running");
    profile.Backups.Enabled = false;
    var settings = Settings(profile);
    Require((await manager.UpdateSettingsAsync(settings)).Ok, "could not disable the next rolling backup");
    Require((await manager.StopAsync(profile.Id)).Ok, "second stop failed");
    Require(data.LoadRunArchive().OrderBy(item => item.ArchivedUtc).Last().BackupResult ==
        ServerSessionBackupResult.NotConfigured,
        "the backup-off graceful Stop fabricated a backup attempt");
    var restored = await manager.RestoreBackupAsync(profile.Id, backup.Id);
    Require(restored.Ok && File.ReadAllText(marker) == "before stop", "offline restore did not recover the selected contents");
    var list = await manager.BackupsAsync(profile.Id);
    Require(list.Backups.Any(item => item.BackupKind == BackupKinds.PreRestore),
        "restore did not retain a pre-restore snapshot");
});

await Check("graceful Stop keeps a failed rolling-backup result distinct from session outcome", async () =>
{
    using var data = Data("backup-session-failure-data");
    var profile = Profile("backup-session-failure-world", "backup-session-failure", FreePort());
    profile.Backups = new BackupOptions { Enabled = true, RetentionCount = 3, MinimumFreeSpaceMb = 0 };
    var manager = Manager(data);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "settings failed");
    Require((await manager.StartAsync(profile.Id)).Ok, "start failed");
    Directory.Delete(profile.WorldDirectory, recursive: true);
    var stopped = await manager.StopAsync(profile.Id);
    var summary = data.LoadRunArchive().Single();
    Require(stopped.Ok && stopped.Code == "StoppedBackupFailed" &&
        summary.Outcome == ServerSessionOutcome.GracefulStop &&
        summary.BackupResult == ServerSessionBackupResult.Failed,
        "backup failure changed the confirmed Stop outcome or was omitted from the session summary");
});

await Check("backup staging, integrity, retention, and free-space checks fail closed", async () =>
{
    using var data = Data("backup-service");
    var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    var profile = Profile("backup-service-world", "backup-service", FreePort());
    profile.Backups = new BackupOptions { Enabled = true, RetentionCount = 2, MinimumFreeSpaceMb = 0 };
    var marker = Path.Combine(profile.WorldDirectory, "save.dat");
    var service = new WorldBackupService(data, clock);
    File.WriteAllText(marker, "one");
    var first = service.Create(profile, BackupKinds.Rolling);
    clock.Advance(TimeSpan.FromSeconds(1));
    File.WriteAllText(marker, "two");
    var second = service.Create(profile, BackupKinds.Rolling);
    clock.Advance(TimeSpan.FromSeconds(1));
    File.WriteAllText(marker, "three");
    var third = service.Create(profile, BackupKinds.Rolling);
    Require(first.Ok && second.Ok && third.Ok && service.List(profile.Id).Count == 2,
        "rolling retention did not keep exactly the configured newest backups");
    Require(!Directory.Exists(Path.Combine(data.BackupsRoot, profile.Id.ToString("N"),
        first.Backup!.Id.ToString("N") + ".backup")), "retention left the oldest completed backup on disk");

    var restored = service.Restore(profile, second.Backup!.Id);
    Require(restored.Ok && File.ReadAllText(marker) == "two", "verified backup restore failed");
    var selectedPath = Path.Combine(data.BackupsRoot, profile.Id.ToString("N"),
        second.Backup.Id.ToString("N") + ".backup", "payload", "save.dat");
    File.WriteAllText(selectedPath, "tampered");
    File.WriteAllText(marker, "live remains");
    Require(!service.Restore(profile, second.Backup.Id).Ok && File.ReadAllText(marker) == "live remains",
        "tampered backup changed the live save directory");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(selectedPath)!, "..", "complete.json"), "{");
    Require(!service.Verify(profile, second.Backup.Id).Ok && File.ReadAllText(marker) == "live remains",
        "a malformed completion manifest escaped typed verification failure or changed the live save directory");

    var stageRoot = Path.Combine(data.BackupsRoot, profile.Id.ToString("N"));
    var abandoned = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".staging");
    Directory.CreateDirectory(abandoned);
    File.WriteAllText(Path.Combine(abandoned, "partial"), "partial copy");
    _ = new WorldBackupService(data, clock, games: Games(data));
    Require(!Directory.Exists(abandoned), "interrupted staging directory was not cleaned safely");

    var noSpace = new WorldBackupService(data, clock, _ => 0);
    File.WriteAllText(marker, "needs space");
    Require(!noSpace.Create(profile, BackupKinds.Rolling).Ok,
        "backup ignored the configured destination free-space boundary");
});

await Check("shared Stop before first signed roster leaves setup recoverable", async () =>
{
    using var data = Data("shared-stop-before-roster");
    var profile = Profile("roster-race", "roster-race", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "first stop");
    var manager = new HostManager(data, new GameServerRegistry([new ObservationFixtureDriver()], PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok &&
        (await manager.StartAsync(profile.Id)).Ok &&
        (await manager.StopAsync(profile.Id)).Ok, "the first graceful Stop failed");
    var sharedRoot = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"));
    Require(!File.Exists(Path.Combine(sharedRoot, "source.json")) &&
        (await manager.SharedWorldStatusAsync(profile.Id)).Latest is null,
        "Stop published a binding or save before the signed roster");
    var roster = new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System))
        .PublishRoster(profile, []);
    Require(SharedWorldRosterTrust.Verify(roster) && roster.Revision == 1,
        "the first signed roster could not recover after Stop");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "second stop");
    Require((await manager.StartAsync(profile.Id)).Ok &&
        (await manager.StopAsync(profile.Id)).Ok &&
        (await manager.SharedWorldStatusAsync(profile.Id)).Latest is { Number: 1 },
        "a later confirmed Stop did not publish after roster setup");
});

await Check("shared save publishes only after confirmed Stop and rejects changed payload", async () =>
{
    using var data = Data("shared-world-stop");
    var profile = Profile("shared-stop", "shared-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "synthetic world one");
    var driver = new ObservationFixtureDriver();
    var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "fixture settings were rejected");
    new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System))
        .PublishRoster(profile, []);
    Require((await manager.SharedWorldStatusAsync(profile.Id)).Latest is null,
        "a save was published before a graceful Stop");
    Require((await manager.StartAsync(profile.Id)).Ok, "fixture Start failed");
    Require((await manager.SharedWorldStatusAsync(profile.Id)).Latest is null,
        "a save was published while the game was running");
    driver.StopBehavior = FixtureStopBehavior.Failed;
    Require(!(await manager.StopAsync(profile.Id)).Ok &&
        (await manager.SharedWorldStatusAsync(profile.Id)).Latest is null,
        "a failed Stop published a shared save");
    driver.StopBehavior = FixtureStopBehavior.Unconfirmed;
    Require(!(await manager.StopAsync(profile.Id)).Ok &&
        (await manager.SharedWorldStatusAsync(profile.Id)).Latest is null,
        "an unconfirmed Stop published a shared save");
    driver.StopBehavior = FixtureStopBehavior.Normal;
    Require((await manager.StopAsync(profile.Id)).Ok, "fixture Stop failed");
    var status = await manager.SharedWorldStatusAsync(profile.Id);
    Require(status.Enabled && status.Latest is { Number: 1, Schema: 4 } &&
        SharedWorldService.VerifySignature(status.Latest), "signed version was not published after Stop");
    var firstHash = status.Latest!.VersionHash;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "synthetic world two");
    Require((await manager.StartAsync(profile.Id)).Ok, "second fixture Start failed");
    driver.StopBehavior = FixtureStopBehavior.Failed;
    Require(!(await manager.StopAsync(profile.Id)).Ok &&
        (await manager.SharedWorldStatusAsync(profile.Id)).Latest?.VersionHash == firstHash,
        "failed later Stop advanced the previously shared copy");
    driver.StopBehavior = FixtureStopBehavior.Unconfirmed;
    Require(!(await manager.StopAsync(profile.Id)).Ok &&
        (await manager.SharedWorldStatusAsync(profile.Id)).Latest?.VersionHash == firstHash,
        "unconfirmed later Stop advanced the previously shared copy");
    driver.StopBehavior = FixtureStopBehavior.Normal;
    Require((await manager.StopAsync(profile.Id)).Ok &&
        (await manager.SharedWorldStatusAsync(profile.Id)).Latest is { Number: 2 },
        "confirmed later Stop did not advance the shared copy");
    Require(!SharedWorldService.VerifySignature(status.Latest! with
    { PortableSetup = status.Latest.PortableSetup with { GameVersion = "9.9.9" } }),
        "changed portable game requirements passed signature verification");
    Require(!SharedWorldService.VerifySignature(status.Latest! with { Schema = 1 }),
        "a v4 manifest was accepted as the old unsigned-setup schema");
    Require(!SharedWorldService.VerifySignature(status.Latest! with { CaptureKind = "LiveSave" }),
        "a post-Stop capture was relabeled as a live save");
    var chunk = manager.ReadSharedChunk(status.Latest!, 0, 0);
    Require(Encoding.UTF8.GetString(chunk) == "synthetic world one", "published chunk changed");
    var sourceService = new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System));
    using var reviewingPc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var reviewingDevice = Guid.NewGuid();
    var reviewingKey = Convert.ToBase64String(reviewingPc.ExportSubjectPublicKeyInfo());
    SharedWorldRosterMember[] reviewingMembers = [new(reviewingDevice, reviewingKey,
        new SharedWorldGrants(Receive: true), false)];
    var oldRoster = sourceService.PublishRoster(profile, reviewingMembers);
    var previousSource = profile.WorldDirectory;
    profile.WorldDirectory = Path.Combine(data.RootPath, "reviewed-new-world");
    Directory.CreateDirectory(profile.WorldDirectory);
    RequireThrows<InvalidDataException>(() => sourceService.PublishRoster(profile, reviewingMembers),
        "a changed world source silently received a new signed roster");
    Require(!sourceService.PublishAfterStop(profile, Guid.NewGuid()).Ok,
        "a changed world source silently published a save");
    var reviewedRoster = sourceService.PublishRoster(profile, reviewingMembers, reviewSourceChange: true);
    Require(reviewedRoster.GroupId != oldRoster.GroupId &&
        reviewedRoster.Epoch > oldRoster.Epoch && reviewedRoster.Revision > oldRoster.Revision &&
        sourceService.ReadRoster(profile)?.GroupId == reviewedRoster.GroupId &&
        File.Exists(Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
            oldRoster.GroupId.ToString("N") + ".roster.json")) &&
        File.Exists(Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
            status.Latest!.GroupId.ToString("N"), "1", "version.json")),
        "reviewed source rotation lost the signed rollback floor or prior history");
    Require(!SharedWorldRosterTrust.Accept(oldRoster, profile.Id, reviewingDevice, reviewingKey,
        oldRoster.OwnerPublicKey, reviewedRoster.Epoch, reviewedRoster.Revision) &&
        SharedWorldRosterTrust.Accept(reviewedRoster, profile.Id, reviewingDevice, reviewingKey,
            oldRoster.OwnerPublicKey, oldRoster.Epoch, oldRoster.Revision),
        "source rotation broke the Friend rollback floor");
    profile.WorldDirectory = previousSource;
    var payload = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
        status.Latest!.GroupId.ToString("N"), "1", "payload", "world.dat");
    File.WriteAllText(payload, "tampered world one");
    RequireThrows<InvalidDataException>(() => manager.ReadSharedChunk(status.Latest!, 0, 0),
        "tampered published file was transferred");
    Require(!SharedWorldService.SafePath("../world.dat") && !SharedWorldService.SafePath("C:/world.dat") &&
        !SharedWorldService.SafePath("safe/../../world.dat") &&
        !SharedWorldService.SafePath("safe/CON.txt"), "traversal or a Windows device name was accepted");
});

await Check("takeover readiness and rehearsal keep received saves isolated", async () =>
{
    using var data = Data("shared-readiness");
    var profile = Profile("readiness-fixture", "readiness-world", FreePort());
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "rehearsal marker");
    var manager = new HostManager(data, new GameServerRegistry([new ObservationFixtureDriver()], PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "fixture setup failed");
    await manager.PublishSharedWorldRosterAsync(profile.Id, []);
    Require((await manager.StartAsync(profile.Id)).Ok && (await manager.StopAsync(profile.Id)).Ok,
        "fixture Stop did not publish");
    var version = (await manager.SharedWorldStatusAsync(profile.Id)).Latest!;
    var vault = Path.Combine(data.RootPath, "received-shared-worlds", Guid.NewGuid().ToString("N"),
        profile.Id.ToString("N"));
    var payload = Path.Combine(vault, version.VersionHash, "payload");
    Directory.CreateDirectory(payload);
    File.WriteAllBytes(Path.Combine(payload, "world.dat"), manager.ReadSharedChunk(version, 0, 0));
    File.WriteAllBytes(Path.Combine(vault, "latest.json"),
        JsonSerializer.SerializeToUtf8Bytes(version, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var rehearsalRoot = Path.Combine(data.RootPath, "shared-world-rehearsals");
    var setup = new TakeoverLocalSetup(fixture, version.PortableSetup.GameVersion, [], true,
        FreePort(), version.PortableSetup.GamePort);
    var authority = new TakeoverAuthority(false, false, false);
    var good = SharedWorldReadiness.Check(vault, rehearsalRoot, setup, authority, version.SigningPublicKey, version.GroupId,
        _ => 2L * 1024 * 1024 * 1024);
    Require(!good.Ready && good.Reasons.Any(reason => reason.Contains("permission")) &&
        good.Reasons.Any(reason => reason.Contains("route")), "takeover passed without governance/routes");
    Require(SharedWorldReadiness.Check(vault, rehearsalRoot, setup, authority, "different-key", version.GroupId).Reasons.Any(
        reason => reason.Contains("approved Host signing identity")), "untrusted signing identity passed");
    Require(SharedWorldReadiness.Check(vault, rehearsalRoot, setup, authority, version.SigningPublicKey,
        Guid.NewGuid()).Reasons.Any(reason => reason.Contains("approved Host signing identity")),
        "unapproved group passed");
    var alternatePort = setup with { GamePort = FreePort() };
    Require(!SharedWorldReadiness.Check(vault, rehearsalRoot, alternatePort, authority,
        version.SigningPublicKey, version.GroupId).Reasons.Any(reason => reason.Contains("separate control and game ports")),
        "a valid successor-selected game port was rejected");
    Require(SharedWorldReadiness.Check(vault, rehearsalRoot, setup with { ServerFile = null }, authority, version.SigningPublicKey, version.GroupId,
        _ => 2L * 1024 * 1024 * 1024).Reasons.Any(reason => reason.Contains("installed game server")),
        "missing installation passed");
    Require(SharedWorldReadiness.Check(vault, rehearsalRoot, setup with { GameVersion = "wrong" }, authority, version.SigningPublicKey, version.GroupId,
        _ => 2L * 1024 * 1024 * 1024).Reasons.Any(reason => reason.Contains("matching game server")),
        "wrong game version passed");
    Require(SharedWorldReadiness.Check(vault, rehearsalRoot, setup with
    {
        EnabledAddOns =
        [new SharedWorldPortableAddOn("Unexpected", "1", "1", "Factorio mod")]
    }, authority, version.SigningPublicKey, version.GroupId,
        _ => 2L * 1024 * 1024 * 1024).Reasons.Any(reason => reason.Contains("add-ons")),
        "mismatched add-ons passed");
    Require(SharedWorldReadiness.Check(vault, rehearsalRoot, setup with { NewPasswordConfigured = false }, authority, version.SigningPublicKey, version.GroupId,
        _ => 2L * 1024 * 1024 * 1024).Reasons.Any(reason => reason.Contains("password")),
        "missing password passed");
    Require(SharedWorldReadiness.Check(vault, rehearsalRoot, setup with { ControlPort = setup.GamePort }, authority, version.SigningPublicKey, version.GroupId,
        _ => 2L * 1024 * 1024 * 1024).Reasons.Any(reason => reason.Contains("ports")),
        "overlapping ports passed");
    Require(SharedWorldReadiness.Check(vault, rehearsalRoot, setup, authority, version.SigningPublicKey, version.GroupId,
        _ => 1024).Reasons.Any(reason => reason.Contains("disk space")), "low space passed");
    var rehearsal = SharedWorldReadiness.Rehearse(data.RootPath, vault, setup, authority, version.SigningPublicKey, version.GroupId,
        _ => 2L * 1024 * 1024 * 1024);
    Require(rehearsal.RehearsalPassed && !rehearsal.ManagedProcessRehearsalPassed && !rehearsal.Ready &&
        !Directory.EnumerateFileSystemEntries(rehearsalRoot).Any() &&
        File.ReadAllText(Path.Combine(profile.WorldDirectory, "world.dat")) == "rehearsal marker" &&
        rehearsal.Reasons.Any(reason => reason.Contains("provenance")) &&
        rehearsal.Reasons.Any(reason => reason.Contains("real game load")), "rehearsal was not isolated or honest");
    var renamedRoot = Path.Combine(data.RootPath, "untrusted-fixture");
    Directory.CreateDirectory(renamedRoot);
    var renamedExecutable = Path.Combine(renamedRoot, "TogetherServer.Fixture.exe");
    File.WriteAllText(renamedExecutable, "arbitrary renamed executable, not a trusted fixture");
    var renamed = SharedWorldReadiness.Rehearse(data.RootPath, vault,
        setup with { ServerFile = renamedExecutable }, authority,
        version.SigningPublicKey, version.GroupId, _ => 2L * 1024 * 1024 * 1024);
    Require(renamed.RehearsalPassed && !renamed.ManagedProcessRehearsalPassed &&
        renamed.Reasons.Any(reason => reason.Contains("provenance")) &&
        File.ReadAllText(renamedExecutable) == "arbitrary renamed executable, not a trusted fixture" &&
        !Directory.EnumerateFileSystemEntries(rehearsalRoot).Any(),
        "renamed arbitrary executable was launched or disposable copy was left behind");
    var badFixture = SharedWorldReadiness.Rehearse(data.RootPath, vault,
        setup with { ServerFile = Path.Combine(data.RootPath, "missing-fixture.exe") },
        authority, version.SigningPublicKey, version.GroupId, _ => 2L * 1024 * 1024 * 1024);
    Require(!badFixture.ManagedProcessRehearsalPassed && badFixture.RehearsalPassed &&
        badFixture.Reasons.Any(reason => reason.Contains("installed game server")) &&
        badFixture.Reasons.Any(reason => reason.Contains("provenance")) &&
        !Directory.EnumerateFileSystemEntries(rehearsalRoot).Any(),
        "missing fixture file was accepted as installed or left a disposable copy");
    var linkedDevice = Path.Combine(data.RootPath, "received-shared-worlds", "linked-device");
    CreateJunction(linkedDevice, Path.GetDirectoryName(vault)!);
    var linkedVault = Path.Combine(linkedDevice, profile.Id.ToString("N"));
    Require(SharedWorldReadiness.Check(linkedVault, rehearsalRoot, setup, authority,
        version.SigningPublicKey, version.GroupId).Reasons.Any(reason => reason.Contains("verification")),
        "linked vault passed readiness");
    File.WriteAllText(Path.Combine(payload, "world.dat"), "tampered");
    Require(SharedWorldReadiness.Check(vault, rehearsalRoot, setup, authority, version.SigningPublicKey, version.GroupId).Reasons.Any(reason =>
        reason.Contains("verification")), "tampered vault passed readiness");
    Require(!SharedWorldReadiness.Rehearse(data.RootPath, vault, setup, authority, version.SigningPublicKey, version.GroupId).RehearsalPassed,
        "tampered vault was rehearsed");
});

await Check("shared portable setup signs reviewed requirements without machine secrets", async () =>
{
    using var data = Data("shared-portable-setup");
    foreach (var kind in new[] { GameKinds.Valheim, GameKinds.MinecraftJava,
        GameKinds.MinecraftBedrock, GameKinds.Factorio, GameKinds.Terraria })
    {
        var profile = Profile("portable-" + kind, "world", FreePort());
        profile.Kind = kind;
        profile.SharedSavesEnabled = true;
        profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
        profile.ServerName = "private server password marker";
        if (kind == GameKinds.MinecraftJava)
            File.WriteAllText(Path.Combine(profile.WorldDirectory, "whitelist.json"),
                "[{\"uuid\":\"11111111-1111-4111-8111-111111111111\",\"name\":\"Alice\",\"secret\":\"do-not-share\"}]");
        if (kind == GameKinds.MinecraftBedrock)
            File.WriteAllText(Path.Combine(profile.WorldDirectory, "allowlist.json"),
                "[{\"name\":\"Bob\",\"xuid\":\"1234567890123456789\",\"secret\":\"do-not-share\"}]");
        if (kind == GameKinds.Valheim)
            File.WriteAllText(Path.Combine(profile.WorldDirectory, "permittedlist.txt"),
                "# comment\nSteam_12345\n");
        var snapshot = ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data));
        var setup = SharedWorldPortableSetupReader.Capture(snapshot);
        Require(SharedWorldPortableSetupReader.Valid(kind, setup), "portable setup invalid for " + kind);
        Require(setup.GameVersion == ServerAddOns.GameVersion(profile), "game version was not captured");
        Require(setup.Allowlist!.Count == (kind is GameKinds.Valheim or GameKinds.MinecraftJava or
            GameKinds.MinecraftBedrock ? 1 : 0), "allowlist capture differs for " + kind);
        if (kind == GameKinds.MinecraftBedrock)
            Require(setup.Allowlist[0].Id == "1234567890123456789",
                "Bedrock XUID was missing from the signed player allowlist");
        var serialized = JsonSerializer.Serialize(setup);
        Require(!serialized.Contains(profile.WorldDirectory, StringComparison.OrdinalIgnoreCase) &&
            !serialized.Contains(profile.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            !serialized.Contains("do-not-share", StringComparison.Ordinal) &&
            !serialized.Contains(profile.ServerName, StringComparison.Ordinal),
            "portable setup included raw config, a local path, or a secret");
        if (kind == GameKinds.MinecraftJava)
            Require(setup.Allowlist.Single().Id == "11111111-1111-4111-8111-111111111111",
                "Java player identity missing");
    }
    var factorio = Profile("portable-mod", "world", FreePort());
    factorio.Kind = GameKinds.Factorio;
    var mods = Path.Combine(factorio.WorldDirectory, "mods");
    Directory.CreateDirectory(mods);
    using (var archive = ZipFile.Open(Path.Combine(mods, "fixturemod_1.0.0.zip"), ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("fixturemod_1.0.0/info.json").Open()))
        writer.Write("{\"name\":\"fixturemod\",\"version\":\"1.0.0\",\"factorio_version\":\"2.0\"}");
    File.WriteAllText(Path.Combine(mods, "mod-list.json"),
        "{\"mods\":[{\"name\":\"fixturemod\",\"enabled\":true}]}");
    var modSetup = SharedWorldPortableSetupReader.Capture(
        ServerSetupSnapshots.Read(factorio, ServerSetupSnapshots.Capture(factorio, data)));
    Require(modSetup.AddOns is [
    {
        Name: "fixturemod", Version: "1.0.0",
        RequiredGameVersion: "2.0", Type: "Factorio mod"
    }],
        "enabled add-on requirements were not captured");
    Require(!JsonSerializer.Serialize(modSetup).Contains("fixturemod_1.0.0.zip", StringComparison.Ordinal),
        "local package filename escaped portable setup");
    var invalid = new SharedWorldPortableSetup(2456, false, "Unknown", [],
        [new SharedWorldPortableAllowEntry("C:/private", null)]);
    Require(!SharedWorldPortableSetupReader.Valid(GameKinds.Valheim, invalid),
        "a machine path was accepted as a player identity");
    Require(!SharedWorldPortableSetupReader.Valid(GameKinds.Valheim, invalid with
    {
        Allowlist = Enumerable.Range(0, 129).Select(number =>
        new SharedWorldPortableAllowEntry("Player" + number, null)).ToArray()
    }),
        "oversized portable metadata was accepted");
    var java = Profile("malformed-portable", "world", FreePort());
    java.Kind = GameKinds.MinecraftJava;
    File.WriteAllText(Path.Combine(java.WorldDirectory, "whitelist.json"), "{\"password\":\"do-not-share\"}");
    RequireThrows<InvalidDataException>(() => SharedWorldPortableSetupReader.Capture(
        ServerSetupSnapshots.Read(java, ServerSetupSnapshots.Capture(java, data))),
        "malformed allowlist was silently shared");
});

await Check("new shared manifest reader accepts signed v1 history and rejects altered v2 setup", () =>
{
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var group = Guid.NewGuid();
    var profile = Guid.NewGuid();
    var backup = Guid.NewGuid();
    var createdUtc = DateTimeOffset.UtcNow;
    var file = new SharedWorldFile("world.dat", 4, new string('A', 64));
    var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    var portable = new SharedWorldPortableSetup(2456, false);
    var basis = JsonSerializer.Serialize(new
    {
        schema = 1,
        group,
        number = 1L,
        parent = (string?)null,
        profile,
        game = GameKinds.Valheim,
        world = "world",
        createdUtc,
        captureKind = SharedWorldCaptureKinds.PostStopBackup,
        backup,
        portableSetup = new { portable.GamePort, portable.Crossplay },
        files = new[] { file },
        publicKey
    }, json);
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
    var version = new SharedWorldVersion(1, group, 1, null, profile, GameKinds.Valheim,
        "world", createdUtc, SharedWorldCaptureKinds.PostStopBackup, backup, portable,
        [file], publicKey, Convert.ToHexString(digest), Convert.ToBase64String(key.SignHash(digest)));
    Require(SharedWorldService.VerifySignature(version), "signed v1 history was not readable");
    Require(!SharedWorldService.VerifySignature(version with
    { PortableSetup = portable with { GameVersion = "Unknown" } }),
        "unsigned metadata was injected into a signed v1 manifest");
    Require(!SharedWorldService.VerifySignature(version with { Schema = 2 }),
        "old signature was accepted for the new portable schema");
    var v2Setup = new SharedWorldPortableSetup(2456, false, "Unknown", [], []);
    var v2Basis = JsonSerializer.Serialize(new
    {
        schema = 2,
        group,
        number = 1L,
        parent = (string?)null,
        profile,
        game = GameKinds.Valheim,
        world = "world",
        createdUtc,
        captureKind = SharedWorldCaptureKinds.PostStopBackup,
        backup,
        portableSetup = new
        {
            v2Setup.GamePort,
            v2Setup.Crossplay,
            v2Setup.GameVersion,
            v2Setup.AddOns,
            v2Setup.Allowlist
        },
        files = new[] { file },
        publicKey
    }, json);
    var v2Digest = SHA256.HashData(Encoding.UTF8.GetBytes(v2Basis));
    var v2 = version with
    {
        Schema = 2,
        PortableSetup = v2Setup,
        VersionHash = Convert.ToHexString(v2Digest),
        Signature = Convert.ToBase64String(key.SignHash(v2Digest))
    };
    Require(SharedWorldService.VerifySignature(v2), "signed v2 history was not readable");
    Require(!SharedWorldService.VerifySignature(v2 with
    { PortableSetup = v2Setup with { PublicListing = true } }),
        "unsigned settings were injected into signed v2 history");
    var v3Setup = v2Setup with { PublicListing = true };
    var v3Basis = JsonSerializer.Serialize(new
    {
        schema = 3,
        group,
        number = 1L,
        parent = (string?)null,
        profile,
        game = GameKinds.Valheim,
        world = "world",
        createdUtc,
        captureKind = SharedWorldCaptureKinds.PostStopBackup,
        backup,
        portableSetup = new
        {
            v3Setup.GamePort,
            v3Setup.Crossplay,
            v3Setup.GameVersion,
            v3Setup.AddOns,
            v3Setup.Allowlist,
            v3Setup.PublicListing,
            v3Setup.MaxPlayers,
            v3Setup.GameMode,
            v3Setup.Difficulty,
            v3Setup.AllowlistEnabled
        },
        files = new[] { file },
        publicKey
    }, json);
    var v3Digest = SHA256.HashData(Encoding.UTF8.GetBytes(v3Basis));
    var v3 = version with
    {
        Schema = 3,
        PortableSetup = v3Setup,
        VersionHash = Convert.ToHexString(v3Digest),
        Signature = Convert.ToBase64String(key.SignHash(v3Digest))
    };
    Require(SharedWorldService.VerifySignature(v3), "signed v3 history was not readable");
    Require(!SharedWorldService.VerifySignature(v3 with
    { PortableSetup = v3Setup with { JavaServerJarSha256 = new string('A', 64) } }),
        "unsigned JAR identity was injected into signed v3 history");
    return Task.CompletedTask;
});

await Check("shared setup comes from the verified backup and accepts unknown Java version", () =>
{
    using var data = Data("shared-setup-checkpoint");
    var profile = Profile("shared-java-checkpoint", "world", FreePort());
    profile.Kind = GameKinds.MinecraftJava;
    profile.SharedSavesEnabled = true;
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.PublicListing = false;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "synthetic saved world");
    var config = Path.Combine(profile.WorldDirectory, "server.properties");
    var allow = Path.Combine(profile.WorldDirectory, "whitelist.json");
    File.WriteAllText(config,
        "max-players=8\ngamemode=survival\ndifficulty=normal\nwhite-list=true\nrcon.password=do-not-share\n");
    File.WriteAllText(allow, "[{\"name\":\"Alice\"}]");
    var backups = new WorldBackupService(data, TimeProvider.System);
    var backup = backups.Create(profile, BackupKinds.Rolling);
    Require(backup.Ok && backup.Backup is not null, "Java checkpoint backup failed");
    File.WriteAllText(config, "max-players=99\ngamemode=creative\nrcon.password=changed-secret\n");
    File.WriteAllText(allow, "[{\"name\":\"Mallory\"}]");
    profile.PublicListing = true;
    var service = new SharedWorldService(data, backups);
    service.PublishRoster(profile, []);
    var publication = service.PublishAfterStop(profile, backup.Backup!.Id);
    Require(publication.Ok && publication.Version is { Schema: 4 },
        "unknown Java server version blocked publication: " + publication.Message);
    var setup = publication.Version!.PortableSetup;
    Require(setup.GameVersion == "Unknown" && setup.MaxPlayers == 8 &&
        setup.GameMode == "survival" && setup.Difficulty == "normal" &&
        setup.AllowlistEnabled == true && !setup.PublicListing &&
        setup.Allowlist is [{ Name: "Alice" }],
        "portable setup drifted from the verified backup checkpoint");
    var manifest = JsonSerializer.Serialize(publication.Version);
    Require(!manifest.Contains("do-not-share", StringComparison.Ordinal) &&
        !manifest.Contains("changed-secret", StringComparison.Ordinal) &&
        !manifest.Contains(profile.WorldDirectory, StringComparison.OrdinalIgnoreCase) &&
        !manifest.Contains("Mallory", StringComparison.Ordinal),
        "raw config, machine path, or later allowlist escaped into the signed manifest");
    return Task.CompletedTask;
});

await Check("shared Java setup binds a reviewed server JAR hash without sharing its path", () =>
{
    using var data = Data("shared-java-jar-identity");
    var profile = Profile("shared-java-jar", "world", FreePort());
    profile.Kind = GameKinds.MinecraftJava;
    var jar = Path.Combine(profile.WorldDirectory, "server.jar");
    profile.Minecraft = new MinecraftOptions { ServerJarPath = jar };
    using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("META-INF/MANIFEST.MF").Open()))
        writer.Write("Manifest-Version: 1.0\nMain-Class: net.minecraft.server.Main\n");
    var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(jar)));
    MinecraftSetup.WriteManagedJavaProvenance(profile.WorldDirectory, "1.21.0",
        Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(jar))));
    var checkpoint = ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data));
    var setup = SharedWorldPortableSetupReader.Capture(checkpoint);
    Require(setup.GameVersion == "Unknown" && setup.JavaServerJarSha256 == expected &&
        SharedWorldPortableSetupReader.Valid(GameKinds.MinecraftJava, setup),
        "reviewed Java server JAR hash was unavailable");
    Require(!JsonSerializer.Serialize(setup).Contains(jar, StringComparison.OrdinalIgnoreCase),
        "Java JAR machine path escaped portable setup");
    Require(!SharedWorldPortableSetupReader.Valid(GameKinds.MinecraftJava,
        setup with { JavaServerJarSha256 = new string('Z', 64) }),
        "malformed JAR identity was accepted");
    profile.Minecraft.ServerJarPath = Path.Combine(root, "outside-server.jar");
    var unavailable = SharedWorldPortableSetupReader.Capture(
        ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data)));
    Require(unavailable.JavaServerJarSha256 == "Unknown",
        "a JAR outside the prepared server folder was identified as ready");
    File.AppendAllText(jar, "changed after checkpoint");
    Require(SharedWorldPortableSetupReader.Capture(checkpoint).JavaServerJarSha256 == expected,
        "Java identity drifted from its backup checkpoint");
    return Task.CompletedTask;
});

await Check("Bedrock portable requirements retain active pack UUID and reject missing packs", () =>
{
    var id = Guid.NewGuid();
    var snapshot = new ServerSetupSnapshot(2, Guid.NewGuid(), GameKinds.MinecraftBedrock,
        "world", "Unknown", "", [new ServerAddOnItem("behavior:" + id.ToString("D"),
            "Example Pack", "1.0.0", "1.21.0", true, "Check real client join", "behavior pack")],
        [], 19132);
    var setup = SharedWorldPortableSetupReader.Capture(snapshot);
    var pack = setup.AddOns?.SingleOrDefault() ?? throw new Exception("active pack was not captured");
    Require(pack.Id == id.ToString("D") &&
        SharedWorldPortableSetupReader.Valid(GameKinds.MinecraftBedrock, setup),
        "active Bedrock pack identity was lost");
    Require(!SharedWorldPortableSetupReader.Valid(GameKinds.MinecraftBedrock,
        setup with { AddOns = [pack with { Id = null }] }),
        "an active pack without a UUID was accepted");
    RequireThrows<InvalidDataException>(() => SharedWorldPortableSetupReader.Capture(snapshot with
    {
        AddOns = [new ServerAddOnItem("behavior:" + id.ToString("D"),
            "Shared or missing pack", "Unknown", "Unknown", true,
            "Outside this world's checkpoint", "External shared pack")]
    }), "incomplete external Bedrock pack was published");
    return Task.CompletedTask;
});

await Check("shared save grant is separate and revoked at access deadline or unassignment", () =>
{
    using var data = Data("shared-world-access");
    var profileId = Guid.NewGuid();
    var device = new PairedDevice
    {
        Id = Guid.NewGuid(),
        ProfileId = Guid.Empty,
        AssignedProfileIds = [profileId],
        CredentialHash = new string('A', 64),
        CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(10)
    };
    data.SavePairingState(new PairingPersistentState { Devices = [device] });
    var pairing = new PairingService(data);
    Require(!pairing.AuthorizeReceiveSaves(device, profileId, out _).Ok,
        "default receive permission was not off");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    Require(pairing.BindSharedWorldKey(device.Id, new SharedWorldEnrollmentRequest(nonce, publicKey,
        Convert.ToBase64String(key.SignData(SharedWorldRosterTrust.EnrollmentBasis(
            device.Id, nonce, publicKey), HashAlgorithmName.SHA256)))).Ok,
        "PC identity could not be enrolled");
    Require(pairing.SetReceiveSaves(device.Id, profileId, true).Ok &&
        pairing.AuthorizeReceiveSaves(device, profileId, out _).Ok,
        "explicit per-server save grant failed");
    Require(pairing.SetServerAccess(device.Id, [], null, [profileId]).Ok &&
        !pairing.AuthorizeReceiveSaves(device, profileId, out _).Ok,
        "unassignment retained save access");
    Require(pairing.SetServerAccess(device.Id, [profileId], null, [profileId]).Ok &&
        !pairing.AuthorizeReceiveSaves(device, profileId, out _).Ok,
        "reassignment silently restored the save grant");
    Require(pairing.SetReceiveSaves(device.Id, profileId, true).Ok,
        "second explicit grant failed");
    Require(pairing.SetAccessExpiry(device.Id, new DeviceAccessExpiryRequest(
        AccessExpiresUtc: DateTimeOffset.UtcNow.AddMilliseconds(100))).Ok,
        "access deadline could not be set");
    Thread.Sleep(200);
    Require(pairing.AuthorizeReceiveSaves(device, profileId, out _).Code == "AccessExpired",
        "expired access still read shared saves");
    return Task.CompletedTask;
});

await Check("checked-only signed Host heads retain a same-number fork across restart", () =>
{
    using var data = Data("shared-checked-head-fork");
    var profile = Profile("checked-head-fork", "checked-head-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0, RetentionCount = 5 };
    profile.SharedSavesEnabled = true;
    var backups = new WorldBackupService(data, TimeProvider.System);
    var shares = new SharedWorldService(data, backups);
    shares.PublishRoster(profile, []);
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "first fork");
    var firstBackup = backups.Create(profile, BackupKinds.Rolling);
    Require(firstBackup.Ok && firstBackup.Backup is not null, "first backup failed");
    var first = shares.PublishAfterStop(profile, firstBackup.Backup!.Id).Version;
    Require(first is not null && SharedWorldService.VerifySignature(first), "first signed head missing");
    var sharedRoot = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"));
    Directory.Move(Path.Combine(sharedRoot, first!.GroupId.ToString("N"), "1"),
        Path.Combine(data.RootPath, "held-first-signed-head"));
    File.Delete(Path.Combine(sharedRoot, "latest.json"));
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "second fork");
    var secondBackup = backups.Create(profile, BackupKinds.Rolling);
    Require(secondBackup.Ok && secondBackup.Backup is not null, "second backup failed");
    var second = shares.PublishAfterStop(profile, secondBackup.Backup!.Id).Version;
    Require(second is not null && SharedWorldService.VerifySignature(second) &&
        first.GroupId == second.GroupId && first.Number == second.Number &&
        first.VersionHash != second.VersionHash, "two valid competing signed heads were not created");
    var profileId = profile.Id;
    var config = new FriendConfiguration();
    Require(!FriendLink.ObserveHistory(config, profileId, null, first),
        "first checked head was unexpectedly marked conflicting");
    Require(FriendLink.CanCommitReceivedVersion(config, profileId, first),
        "a transfer could not commit its unchanged checked head");
    // Model Pull staging A, Check observing signed fork B, then Pull entering its final commit gate.
    Require(FriendLink.ObserveHistory(config, profileId, null, second!) &&
        config.SharedWorldConflicts.Contains(profileId) &&
        config.LastSharedHostHashes[profileId] == first.VersionHash &&
        config.LastSharedHostManifests[profileId].VersionHash == first.VersionHash &&
        config.CompetingSharedHostManifests[profileId].Single().VersionHash == second!.VersionHash,
        "checked-only fork replaced or lost a signed head");
    Require(!FriendLink.CanCommitReceivedVersion(config, profileId, first),
        "a staged transfer could commit after a competing signed head was checked");
    var restored = JsonSerializer.Deserialize<FriendConfiguration>(
        JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    Require(FriendLink.ObserveHistory(restored, profileId, null, first) &&
        restored.SharedWorldConflicts.Contains(profileId) &&
        restored.LastSharedHostHashes[profileId] == first.VersionHash &&
        !FriendLink.CanCommitReceivedVersion(restored, profileId, first) &&
        SharedWorldService.VerifySignature(restored.CompetingSharedHostManifests[profileId].Single()),
        "rechecking the original head cleared a persisted fork");
    return Task.CompletedTask;
});

await Check("shared save receipt resumes bounded chunks, keeps three verified copies, and preserves reserve", () =>
{
    using var data = Data("shared-world-receipt");
    var profile = Profile("shared-receipt", "received-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions
    {
        Enabled = true,
        MinimumFreeSpaceMb = 0,
        RetentionCount = 5
    };
    profile.SharedSavesEnabled = true;
    var backupService = new WorldBackupService(data, TimeProvider.System);
    var shares = new SharedWorldService(data, backupService);
    shares.PublishRoster(profile, []);
    var receiver = Path.Combine(data.RootPath, "disposable-receiver");
    Directory.CreateDirectory(receiver);
    SharedWorldVersion? latest = null;
    for (var number = 1; number <= 4; number++)
    {
        var latestPath = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"), "latest.json");
        var previousPointer = File.Exists(latestPath) ? File.ReadAllBytes(latestPath) : null;
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), $"synthetic version {number}");
        var backup = backupService.Create(profile, BackupKinds.Rolling);
        Require(backup.Ok && backup.Backup is not null, "synthetic backup failed");
        var published = shares.PublishAfterStop(profile, backup.Backup!.Id);
        Require(published.Ok && published.Version is not null &&
            published.Version.Number == number, "signed version chain failed");
        Require(!SharedWorldService.VerifySignature(published.Version! with
        { CreatedUtc = published.Version.CreatedUtc.AddSeconds(1) }),
            "displayed save completion time was not signed");
        if (number == 2)
        {
            File.WriteAllBytes(latestPath, previousPointer!);
            var recovered = shares.PublishAfterStop(profile, backup.Backup.Id);
            Require(recovered.Ok && recovered.Version?.VersionHash == published.Version.VersionHash &&
                shares.Status(profile).Latest?.VersionHash == published.Version.VersionHash,
                "verified orphan was not reconciled after a pointer crash");
        }
        if (number == 3)
        {
            var orphanFile = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
                published.Version.GroupId.ToString("N"), "3", "payload", "world.dat");
            var original = File.ReadAllBytes(orphanFile);
            File.WriteAllBytes(latestPath, previousPointer!);
            File.WriteAllText(orphanFile, "tampered orphan");
            Require(!shares.PublishAfterStop(profile, backup.Backup.Id).Ok &&
                shares.Status(profile).Latest?.Number == 2,
                "a corrupted orphan advanced the shared save pointer");
            File.WriteAllBytes(orphanFile, original);
            Require(shares.PublishAfterStop(profile, backup.Backup.Id).Version?.VersionHash ==
                published.Version.VersionHash, "a repaired verified orphan could not be reconciled");
        }
        latest = published.Version;
        var hostFile = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
            latest!.GroupId.ToString("N"), number.ToString(), "payload", "world.dat");
        var received = Path.Combine(receiver, latest!.VersionHash);
        Directory.CreateDirectory(received);
        Directory.CreateDirectory(Path.Combine(received, "payload"));
        File.Copy(hostFile, Path.Combine(received, "payload", "world.dat"));
        File.WriteAllBytes(Path.Combine(received, "version.json"),
            JsonSerializer.SerializeToUtf8Bytes(latest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    File.WriteAllBytes(Path.Combine(receiver, "latest.json"),
        JsonSerializer.SerializeToUtf8Bytes(latest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var secondManifest = shares.ReadEarlierVersion(latest!, 2);
    var thirdManifest = shares.ReadEarlierVersion(latest!, 3);
    Require(thirdManifest.ParentHash == secondManifest.VersionHash &&
        latest!.ParentHash == thirdManifest.VersionHash,
        "a missed-version chain did not retain signed parent hashes");
    var firstManifest = shares.ReadEarlierVersion(latest!, 1);
    Require(FriendLink.IsReceivedHistoryConflict(firstManifest,
            firstManifest with { VersionHash = latest!.VersionHash }) &&
        FriendLink.IsReceivedHistoryConflict(firstManifest,
            secondManifest with { ParentHash = latest!.VersionHash }) &&
        !FriendLink.IsReceivedHistoryConflict(firstManifest, firstManifest),
        "same-number fork or broken parent was not classified as a conflict");
    Require(FriendLink.DescribeReceivedHistory(true, null, false, false, 1,
            latest!.VersionHash, 1, firstManifest.VersionHash).StartsWith("Competing save histories") &&
        FriendLink.DescribeReceivedHistory(true, null, false, true, 2,
            secondManifest.VersionHash, 1, firstManifest.VersionHash).StartsWith("Competing save histories") &&
        FriendLink.DescribeReceivedHistory(true, null, false, false, 1,
            firstManifest.VersionHash, 1, firstManifest.VersionHash) == "Up to date when last checked" &&
        FriendLink.DescribeReceivedHistory(true, null, false, false, 1,
            null, 1, firstManifest.VersionHash) != "Up to date when last checked",
        "received-save status inferred currency from a version number without the signed hash");
    Require(FriendLink.VerifySharedChain(firstManifest, latest!, [secondManifest, thirdManifest]) &&
        !FriendLink.VerifySharedChain(firstManifest, latest!, [thirdManifest, secondManifest]) &&
        !FriendLink.VerifySharedChain(firstManifest, latest!, [secondManifest]) &&
        !FriendLink.VerifySharedChain(firstManifest, latest! with { ParentHash = firstManifest.VersionHash },
            [secondManifest, thirdManifest]),
        "missed-version ancestry accepted an unrelated or incomplete history");
    var publishedRoot = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
        latest!.GroupId.ToString("N"));
    Require(File.Exists(Path.Combine(publishedRoot, "1", "version.json")) &&
        File.Exists(Path.Combine(publishedRoot, "2", "version.json")) &&
        !File.Exists(Path.Combine(publishedRoot, "1", "payload", "world.dat")) &&
        File.Exists(Path.Combine(publishedRoot, "2", "payload", "world.dat")) &&
        File.Exists(Path.Combine(publishedRoot, "3", "payload", "world.dat")) &&
        File.Exists(Path.Combine(publishedRoot, "4", "payload", "world.dat")),
        "Host did not retain newest plus two payloads and all signed ancestry metadata");
    RequireThrows<InvalidDataException>(() => shares.ReadChunk(firstManifest, 0, 0),
        "pruned payload still served a chunk");
    var earlierManifestPath = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
        latest!.GroupId.ToString("N"), "2", "version.json");
    var validEarlierManifest = File.ReadAllBytes(earlierManifestPath);
    File.WriteAllText(earlierManifestPath, "{}");
    RequireThrows<InvalidDataException>(() => shares.ReadEarlierVersion(latest!, 2),
        "tampered earlier manifest was accepted");
    File.WriteAllBytes(earlierManifestPath, validEarlierManifest);
    Require(FriendLink.ReadReceivedLatest(receiver)?.VersionHash == latest!.VersionHash,
        "atomic latest pointer did not resolve to a verified copy");
    FriendLink.PruneReceived(receiver, latest.VersionHash);
    Require(Directory.EnumerateDirectories(receiver).Count() == 3 &&
        Directory.Exists(Path.Combine(receiver, latest.VersionHash)),
        "receipt retention did not keep newest plus two earlier copies");
    var chunkFile = Path.Combine(receiver, "partial.dat");
    var chunk = new SharedWorldFile("partial.dat", SharedWorldService.ChunkBytes * 2L,
        new string('0', 64));
    File.WriteAllBytes(chunkFile, new byte[SharedWorldService.ChunkBytes]);
    Require(FriendLink.ResumeOffset(chunkFile, chunk) == SharedWorldService.ChunkBytes,
        "bounded partial chunk was not resumable");
    File.WriteAllBytes(chunkFile, []);
    Require(FriendLink.ResumeOffset(chunkFile, chunk) == 0,
        "zero-byte interrupted chunk could not restart after reconnect");
    using (var restarted = FriendLink.OpenPartialOutput(chunkFile, 0)) restarted.WriteByte(1);
    Require(new FileInfo(chunkFile).Length == 1, "zero-byte partial could not restart after reconnect");
    File.WriteAllBytes(chunkFile, new byte[SharedWorldService.ChunkBytes * 2]);
    Require(FriendLink.ExistingPartialBytes(receiver, chunk) == 0,
        "corrupt complete partial understated the disk reserve needed for retry");
    using (var stream = File.OpenWrite(chunkFile)) { stream.Position = stream.Length; stream.WriteByte(1); }
    Require(FriendLink.ResumeOffset(chunkFile, chunk) == -1,
        "unaligned partial chunk was accepted");
    Require(!FriendLink.HasReceiverReserve(1024L * 1024 * 1024, 1) &&
        FriendLink.HasReceiverReserve(1024L * 1024 * 1024 + 1, 1),
        "1 GiB receiver reserve was not enforced");
    File.WriteAllText(Path.Combine(receiver, latest.VersionHash, "payload", "world.dat"), "tampered");
    RequireThrows<InvalidDataException>(() => FriendLink.ReadReceivedLatest(receiver),
        "tampered received copy was reported verified");
    return Task.CompletedTask;
});

await Check("shared version range fallback requires a non-JSON 404", async () =>
{
    using var legacy = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
    { Content = new StringContent("Not Found") };
    using var denied = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
    { Content = new StringContent("Not Found") };
    using var json = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
    { Content = new StringContent("{\"code\":\"Denied\"}") };
    using var disguisedJson = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
    { Content = new StringContent("{\"code\":\"Denied\"}", System.Text.Encoding.UTF8, "text/plain") };
    using var malformedJson = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
    { Content = new StringContent("{broken", System.Text.Encoding.UTF8, "text/plain") };
    Require(await FriendLink.IsUnsupportedSharedVersionRangeAsync(legacy, CancellationToken.None) &&
        !await FriendLink.IsUnsupportedSharedVersionRangeAsync(denied, CancellationToken.None) &&
        !await FriendLink.IsUnsupportedSharedVersionRangeAsync(json, CancellationToken.None) &&
        !await FriendLink.IsUnsupportedSharedVersionRangeAsync(disguisedJson, CancellationToken.None) &&
        !await FriendLink.IsUnsupportedSharedVersionRangeAsync(malformedJson, CancellationToken.None),
        "a denied or malformed range response enabled legacy fallback");
});

await Check("128 save catch-up reads Host history once and still denies a changed manifest", async () =>
{
    using var hostData = Data("shared-history-index");
    using var receiverData = Data("shared-history-index-receiver");
    var shares = new SharedWorldService(hostData,
        new WorldBackupService(hostData, TimeProvider.System));
    using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var groupId = Guid.NewGuid();
    var profileId = Guid.NewGuid();
    var template = new SharedWorldVersion(4, groupId, 1, null, profileId,
        GameKinds.Fixture, "indexed-world", DateTimeOffset.UtcNow,
        SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
        new SharedWorldPortableSetup(25565, false, "Fixture", [], []),
        [new SharedWorldFile("world.dat", 1, new string('A', 64))], "", "", "");
    var versions = new List<SharedWorldVersion>();
    var version = SharedWorldService.SignVersion(template, signer);
    for (var number = 1; number <= 131; number++)
    {
        if (number > 1)
            version = SharedWorldService.SignVersion(version with
            {
                Number = number,
                ParentHash = version.VersionHash,
                BackupId = Guid.NewGuid(),
                CreatedUtc = version.CreatedUtc.AddSeconds(1)
            }, signer);
        versions.Add(version);
        var folder = Path.Combine(hostData.RootPath, "shared-worlds", profileId.ToString("N"),
            groupId.ToString("N"), number.ToString());
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "version.json"),
            JsonSerializer.SerializeToUtf8Bytes(version,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    var anchor = versions[0];
    var latest = versions[^1];
    var receivingDevice = Guid.NewGuid();
    FriendLink.SharedChainCheck result;
    var batches = 0;
    do
    {
        result = await FriendLink.VerifySharedChainBatchAsync(receiverData,
            receivingDevice, profileId, anchor, latest, [],
            (number, _) => Task.FromResult<SharedWorldVersion?>(
                number == latest.Number ? latest : shares.ReadEarlierVersion(latest, number)),
            CancellationToken.None);
        Require(++batches <= 2, "a 130-save gap exceeded the bounded Friend check batches");
    } while (result.Pending);
    Require(result.Valid && shares.HistoricalManifestReadCount <= 260 &&
        shares.HistoricalVersionStepCount <= 260,
        $"Host rescanned signed history for every requested version: {shares.HistoricalVersionStepCount} steps, {shares.HistoricalManifestReadCount} manifests");
    var manager = new HostManager(hostData, Games(hostData));
    var range = manager.ReadEarlierSharedVersionRange(latest, 2, 128);
    Require(range.Count == 128 && range[0].VersionHash == versions[1].VersionHash &&
        range[^1].VersionHash == versions[128].VersionHash,
        "a bounded Host range omitted or reordered signed versions");
    RequireThrows<InvalidDataException>(() =>
        manager.ReadEarlierSharedVersionRange(latest, 2, 129),
        "Host allowed more than one check batch of historical versions");
    var disconnectedHead = SharedWorldService.SignVersion(latest with
    { ParentHash = "BAD", BackupId = Guid.NewGuid() }, signer);
    RequireThrows<InvalidDataException>(() => shares.ReadEarlierVersion(disconnectedHead, 50),
        "a signed fork reused the cached latest-head lineage index");
    var changedPath = Path.Combine(hostData.RootPath, "shared-worlds", profileId.ToString("N"),
        groupId.ToString("N"), "50", "version.json");
    var original = File.ReadAllBytes(changedPath);
    try
    {
        var changed = SharedWorldService.SignVersion(versions[49] with
        { BackupId = Guid.NewGuid() }, signer);
        File.WriteAllBytes(changedPath, JsonSerializer.SerializeToUtf8Bytes(changed,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        RequireThrows<InvalidDataException>(() => shares.ReadEarlierVersion(latest, 50),
            "the cached lineage accepted a newly signed fork at the requested number");
        RequireThrows<InvalidDataException>(() =>
            manager.ReadEarlierSharedVersionRange(latest, 50, 1),
            "a bounded Host range served a changed signed fork");
    }
    finally { File.WriteAllBytes(changedPath, original); }
    Require(shares.ReadEarlierVersion(latest, 50).VersionHash == versions[49].VersionHash,
        "a restored exact manifest could not resume indexed catch-up");
});

await Check("shared save source changes hide old publication and rotate the group", () =>
{
    using var data = Data("shared-world-source-binding");
    var profile = Profile("shared-source", "same-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "first source");
    var backups = new WorldBackupService(data, TimeProvider.System);
    var shares = new SharedWorldService(data, backups);
    var firstRoster = shares.PublishRoster(profile, []);
    var firstBackup = backups.Create(profile, BackupKinds.Rolling);
    Require(firstBackup.Ok && firstBackup.Backup is not null, "first source backup failed");
    var first = shares.PublishAfterStop(profile, firstBackup.Backup!.Id);
    Require(first.Ok && first.Version is not null, "first source publication failed");
    var oldDirectory = profile.WorldDirectory;
    profile.WorldDirectory = Path.Combine(data.RootPath, "replacement-world");
    Directory.CreateDirectory(profile.WorldDirectory);
    Require(shares.Status(profile).Latest is null, "old source remained addressable after directory change");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "replacement source");
    var nextBackup = backups.Create(profile, BackupKinds.Rolling);
    Require(nextBackup.Ok && nextBackup.Backup is not null, "replacement source backup failed");
    Require(!shares.PublishAfterStop(profile, nextBackup.Backup!.Id).Ok,
        "replacement source published without owner review");
    var secondRoster = shares.PublishRoster(profile, [], reviewSourceChange: true);
    Require(secondRoster.GroupId != firstRoster.GroupId &&
        secondRoster.Revision > firstRoster.Revision, "source review reset signed roster history");
    var next = shares.PublishAfterStop(profile, nextBackup.Backup!.Id);
    Require(next.Ok && next.Version?.GroupId != first.Version!.GroupId && next.Version?.Number == 1,
        "replacement source reused the old world group: " + next.Code + " " + next.Message);
    profile.WorldDirectory = Path.Combine(data.RootPath, "third-world");
    Directory.CreateDirectory(profile.WorldDirectory);
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "third source");
    var thirdBackup = backups.Create(profile, BackupKinds.Rolling);
    Require(thirdBackup.Ok && thirdBackup.Backup is not null, "third source backup failed");
    Require(!shares.PublishAfterStop(profile, thirdBackup.Backup!.Id).Ok,
        "third source published without owner review");
    var thirdRoster = shares.PublishRoster(profile, [], reviewSourceChange: true);
    Require(thirdRoster.Revision > secondRoster.Revision,
        "a second source review reset signed roster history");
    var third = shares.PublishAfterStop(profile, thirdBackup.Backup!.Id);
    Require(third.Ok && third.Version?.GroupId != next.Version!.GroupId &&
        Directory.EnumerateFiles(Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N")),
            "world.dat", SearchOption.AllDirectories).Count() == 3,
        "source rotation lost a competing group payload");
    profile.WorldDirectory = oldDirectory;
    Require(shares.Status(profile).Latest is null, "old source could read replacement publication");
    return Task.CompletedTask;
});

await Check("shared save retention follows signed numbers despite skewed capture clocks", () =>
{
    using var data = Data("shared-world-skew");
    var profile = Profile("shared-skew", "skew-world", FreePort());
    profile.Kind = "Fixture";
    profile.SharedSavesEnabled = true;
    var capture = new SkewedSharedCapture(profile.WorldDirectory);
    var shares = new SharedWorldService(data, capture);
    shares.PublishRoster(profile, []);
    var receiver = Path.Combine(data.RootPath, "skew-receiver");
    Directory.CreateDirectory(receiver);
    SharedWorldVersion? head = null;
    for (var number = 1; number <= 5; number++)
    {
        capture.CapturedUtc = DateTimeOffset.UtcNow.AddYears(10 - number);
        var bytes = Encoding.UTF8.GetBytes($"skew payload {number}");
        File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), bytes);
        var result = shares.PublishAfterStop(profile, Guid.NewGuid());
        Require(result.Ok && result.Version?.Number == number, "skewed capture failed to publish");
        head = result.Version;
        var received = Path.Combine(receiver, head!.VersionHash);
        Directory.CreateDirectory(Path.Combine(received, "payload"));
        File.WriteAllBytes(Path.Combine(received, "payload", "world.dat"), bytes);
        File.WriteAllBytes(Path.Combine(received, "version.json"),
            JsonSerializer.SerializeToUtf8Bytes(head, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    File.WriteAllBytes(Path.Combine(receiver, "latest.json"),
        JsonSerializer.SerializeToUtf8Bytes(head, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    FriendLink.PruneReceived(receiver, head!.VersionHash);
    var groupRoot = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
        head.GroupId.ToString("N"));
    for (var number = 1; number <= 5; number++)
    {
        var hostPayload = Path.Combine(groupRoot, number.ToString(), "payload", "world.dat");
        Require(File.Exists(hostPayload) == (number >= 3),
            "Host retention used capture time instead of signed version number");
        var version = number == 5 ? head : shares.ReadEarlierVersion(head, number);
        Require(Directory.Exists(Path.Combine(receiver, version.VersionHash)) == (number >= 3),
            "Friend retention used capture time instead of signed version number");
    }
    var branchHead = shares.ReadEarlierVersion(head, 1);
    var branchRoot = Path.Combine(receiver, branchHead.VersionHash);
    Directory.CreateDirectory(Path.Combine(branchRoot, "payload"));
    File.WriteAllText(Path.Combine(branchRoot, "payload", "world.dat"), "skew payload 1");
    File.WriteAllBytes(Path.Combine(branchRoot, "version.json"),
        JsonSerializer.SerializeToUtf8Bytes(branchHead, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    FriendLink.PruneReceived(receiver, head.VersionHash,
        new HashSet<string> { branchHead.VersionHash });
    Require(Directory.Exists(branchRoot) && Directory.EnumerateDirectories(receiver).Count() == 4,
        "retention deleted a separately preserved verified history");
    return Task.CompletedTask;
});

await Check("shared save totals reject overflow and over-limit payloads", () =>
{
    static SharedWorldFile FileOf(long length) => new("world.dat", length, new string('0', 64));
    Require(SharedWorldService.BoundedTotalBytes([FileOf(SharedWorldService.MaximumSharedWorldBytes)]) ==
        SharedWorldService.MaximumSharedWorldBytes, "exact transfer ceiling was rejected");
    RequireThrows<InvalidDataException>(() => SharedWorldService.BoundedTotalBytes(
        [FileOf(SharedWorldService.MaximumSharedWorldBytes), FileOf(1)]),
        "over-limit transfer was accepted");
    RequireThrows<InvalidDataException>(() => SharedWorldService.BoundedTotalBytes(
        [FileOf(long.MaxValue), FileOf(long.MaxValue)]),
        "overflowing transfer was accepted");
    using var data = Data("shared-world-oversize-publish");
    var profile = Profile("oversize source", "oversize-world", FreePort());
    profile.Kind = "Fixture";
    profile.SharedSavesEnabled = true;
    var vault = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"));
    var shares = new SharedWorldService(data, new OversizedSharedCapture(data.RootPath));
    shares.PublishRoster(profile, []);
    Require(!shares.PublishAfterStop(profile, Guid.NewGuid()).Ok &&
        !File.Exists(Path.Combine(vault, "latest.json")) &&
        !Directory.EnumerateDirectories(vault, ".stage-*", SearchOption.TopDirectoryOnly).Any(),
        "oversized source reached the copying or publication stage");
    return Task.CompletedTask;
});

await Check("shared save consent withdrawal prevents final receipt pointer", () =>
{
    using var data = Data("shared-receipt-withdrawal");
    using var link = new FriendLink(data, "absent-friend.protected");
    var profileId = Guid.NewGuid();
    var root = Path.Combine(data.RootPath, "receipt-race");
    var stage = Path.Combine(root, "stage");
    Directory.CreateDirectory(stage);
    File.WriteAllText(Path.Combine(stage, "world.dat"), "verified fixture");
    link.WithdrawSharedConsent(profileId);
    Require(!link.CommitSharedReceipt(profileId, stage, Path.Combine(root, "received"), root, [1]) &&
        !File.Exists(Path.Combine(root, "latest.json")) && Directory.Exists(stage),
        "withdrawal allowed a completed receipt to be recorded");
    using var shutdown = new CancellationTokenSource();
    shutdown.Cancel();
    var another = Guid.NewGuid();
    Require(!link.CommitSharedReceipt(another, stage, Path.Combine(root, "received"), root, [1], shutdown.Token) &&
        !File.Exists(Path.Combine(root, "latest.json")) && Directory.Exists(stage),
        "shutdown allowed a completed receipt to be recorded");
    return Task.CompletedTask;
});

await Check("shared save chunks detect later tampering without rescanning earlier chunks", () =>
{
    using var data = Data("shared-world-chunk-integrity");
    var profile = Profile("shared-chunks", "chunk-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"),
        new byte[SharedWorldService.ChunkBytes * 2]);
    var backups = new WorldBackupService(data, TimeProvider.System);
    var backup = backups.Create(profile, BackupKinds.Rolling);
    Require(backup.Ok && backup.Backup is not null, "chunk fixture backup failed");
    var shares = new SharedWorldService(data, backups);
    shares.PublishRoster(profile, []);
    var result = shares.PublishAfterStop(profile, backup.Backup!.Id);
    Require(result.Ok && result.Version is not null, "chunk fixture publication failed");
    var version = result.Version!;
    Require(shares.ReadChunk(version, 0, 0).Length == SharedWorldService.ChunkBytes,
        "first verified chunk could not be read");
    var published = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
        version.GroupId.ToString("N"), "1", "payload", "world.dat");
    using (var stream = new FileStream(published, FileMode.Open, FileAccess.Write, FileShare.None))
    {
        stream.Position = SharedWorldService.ChunkBytes;
        stream.WriteByte(1);
    }
    RequireThrows<InvalidDataException>(() => shares.ReadChunk(version, 0, SharedWorldService.ChunkBytes),
        "a later changed chunk was sent after its first verified read");
    return Task.CompletedTask;
});

await Check("shared save authorization is rechecked after asynchronous read", async () =>
{
    using var data = Data("shared-world-revocation");
    var profile = Profile("shared-revocation", "signed-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "fixture");
    var backupService = new WorldBackupService(data, TimeProvider.System);
    var backup = backupService.Create(profile, BackupKinds.Rolling);
    Require(backup.Ok && backup.Backup is not null, "fixture backup failed");
    var shares = new SharedWorldService(data, backupService);
    shares.PublishRoster(profile, []);
    var published = shares.PublishAfterStop(profile, backup.Backup!.Id);
    Require(published.Ok && published.Version is not null, "fixture publication failed");
    var device = new PairedDevice
    {
        Id = Guid.NewGuid(),
        ProfileId = Guid.Empty,
        AssignedProfileIds = [profile.Id],
        CredentialHash = new string('A', 64),
        CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(10)
    };
    data.SavePairingState(new PairingPersistentState { Devices = [device] });
    var pairing = new PairingService(data);
    Require(pairing.SetReceiveSaves(device.Id, profile.Id, true).Ok,
        "explicit grant failed");
    using var pcKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var pcPublicKey = Convert.ToBase64String(pcKey.ExportSubjectPublicKeyInfo());
    var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var proof = new SharedWorldEnrollmentRequest(nonce, pcPublicKey, Convert.ToBase64String(pcKey.SignData(
        SharedWorldRosterTrust.EnrollmentBasis(device.Id, nonce, pcPublicKey), HashAlgorithmName.SHA256)));
    Require(pairing.BindSharedWorldKey(device.Id, proof).Ok, "PC identity enrollment failed");
    var roster = shares.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    Require(SharedWorldRosterTrust.Accept(roster, profile.Id, device.Id, pcPublicKey,
        published.Version!.SigningPublicKey, 0, 0), "signed Receive roster did not allow this PC");
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var delayedRead = Task.Run(async () =>
    {
        var before = pairing.AuthorizeReceiveSaves(device, profile.Id, out var loaded);
        entered.SetResult();
        await release.Task;
        var payload = shares.ReadChunk(published.Version!, 0, 0);
        var after = pairing.AuthorizeReceiveSaves(loaded!, profile.Id, out _);
        return before.Ok && after.Ok ? payload : null;
    });
    await entered.Task;
    Require(pairing.SetReceiveSaves(device.Id, profile.Id, false).Ok,
        "grant removal failed");
    release.SetResult();
    Require(await delayedRead is null, "an in-flight read returned bytes after grant removal");
});

await Check("shared save proxy bounds declared and streamed bytes before receipt", async () =>
{
    using var declared = new ByteArrayContent([1]);
    declared.Headers.ContentLength = SharedWorldService.ChunkBytes + 1;
    using var streamed = new StreamContent(new MemoryStream(new byte[SharedWorldService.ChunkBytes + 1]));
    streamed.Headers.ContentLength = null;
    using var exact = new ByteArrayContent(new byte[SharedWorldService.ChunkBytes]);
    Require(await FriendLink.ReadBoundedSharedAsync(declared,
        SharedWorldService.ChunkBytes, CancellationToken.None) is null,
        "oversized declared chunk was buffered");
    Require(await FriendLink.ReadBoundedSharedAsync(streamed,
        SharedWorldService.ChunkBytes, CancellationToken.None) is null,
        "oversized streamed chunk was buffered");
    Require((await FriendLink.ReadBoundedSharedAsync(exact,
        SharedWorldService.ChunkBytes, CancellationToken.None))?.Length == SharedWorldService.ChunkBytes,
        "exact bounded chunk was rejected");
});

await Check("planned handoff requires exact final save receipt before durable old Host fence", async () =>
{
    using var data = Data("planned-handoff");
    var profile = Profile("planned-handoff-game", "planned-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "handoff marker");
    var driver = new ObservationFixtureDriver();
    var registry = new GameServerRegistry([driver], PortProbeMode.LoopbackOnly);
    using var successor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var routeObserver = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var successorId = Guid.NewGuid();
    var routeObserverId = Guid.NewGuid();
    var successorKey = Convert.ToBase64String(successor.ExportSubjectPublicKeyInfo());
    data.SavePairingState(new PairingPersistentState
    {
        Devices = [new PairedDevice
    {
        Id = successorId, ProfileId = Guid.Empty, AssignedProfileIds = [profile.Id],
        CredentialHash = new string('A', 64),
        CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(10),
        SharedWorldPublicKey = successorKey,
        SharedWorldGrants = new Dictionary<Guid, SharedWorldGrants>
        { [profile.Id] = new(Receive: true, EligibleHost: true) }
    }, new PairedDevice
    {
        Id = routeObserverId, ProfileId = Guid.Empty, AssignedProfileIds = [profile.Id],
        CredentialHash = new string('B', 64),
        CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(10),
        SharedWorldPublicKey = Convert.ToBase64String(routeObserver.ExportSubjectPublicKeyInfo()),
        SharedWorldGrants = new Dictionary<Guid, SharedWorldGrants>
        { [profile.Id] = new(Receive: true) }
    }]
    });
    var pairing = new PairingService(data);
    var manager = new HostManager(data, registry, TimeProvider.System,
        new NullHostingPowerGuard(), pairing: pairing);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "handoff fixture settings failed");
    var shares = new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System));
    var roster = shares.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    Require((await manager.StartAsync(profile.Id)).Ok, "handoff fixture Start failed");
    driver.StopBehavior = FixtureStopBehavior.Failed;
    Require((await manager.PreparePlannedHandoffAsync(profile.Id, successorId,
        "https://127.0.0.1:5132")).Code == "FinalSaveUnconfirmed" &&
        !data.HasProtected($"planned-handoff-{profile.Id:N}.protected"),
        "failed Stop left a prepared handoff");
    driver.StopBehavior = FixtureStopBehavior.Normal;
    var prepared = await manager.PreparePlannedHandoffAsync(profile.Id, successorId,
        "https://127.0.0.1:5132");
    var pendingStart = await manager.StartAsync(profile.Id);
    Require(prepared.Ok && prepared.Version is { Number: 1 } &&
        pendingStart.Code == "PlannedHandoffPending",
        $"final Stop did not publish and hold the old Host offline: {prepared.Code}, version={prepared.Version?.Number}, start={pendingStart.Code}, {prepared.Message}");
    var restartedPending = new HostManager(data, registry);
    var pendingStatus = await restartedPending.PlannedHandoffStatusAsync(profile.Id);
    Require(pendingStatus is
    {
        Pending: true, Code: "WaitingForSuccessorCopy",
        CanComplete: false, CanCancel: true, ReceiptConfirmed: false
    } &&
        pendingStatus.SuccessorDeviceId == successorId &&
        pendingStatus.FinalVersion == 1 &&
        pendingStatus.FinalVersionHash == prepared.Version!.VersionHash,
        "restart did not recover typed pending handoff status");
    Require((await manager.CompletePlannedHandoffAsync(profile.Id)).Code ==
        "WaitingForSuccessorCopy", "handoff succeeded before receipt");
    Require((await manager.CancelPlannedHandoffAsync(profile.Id)).Code == "HandoffCanceled" &&
        !new WorldAuthorityStore(data).HasState(profile.Id) &&
        (await manager.StartAsync(profile.Id)).Ok,
        "owner could not cancel an unsigned handoff and resume its exact world");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "second final marker");
    prepared = await manager.PreparePlannedHandoffAsync(profile.Id, successorId,
        "https://127.0.0.1:5132");
    Require(prepared.Ok && prepared.Version is { Number: 2 } &&
        (await manager.StartAsync(profile.Id)).Code == "PlannedHandoffPending",
        "retry did not capture a fresh final save and hold Start");
    var version = prepared.Version!;
    var earlierVersion = version;
    var receiptDraft = new SharedWorldReceipt(1, version.GroupId, profile.Id,
        version.VersionHash, successorId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
    var receipt = receiptDraft with
    {
        Signature = Convert.ToBase64String(successor.SignData(
        SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256))
    };
    Require((await manager.ConfirmSharedWorldReceiptAsync(profile.Id, successorId,
        receipt with { VersionHash = new string('B', 64) })).Code == "StaleOrWrongVersion",
        "wrong version receipt was accepted");
    Require((await manager.ConfirmSharedWorldReceiptAsync(profile.Id, successorId, receipt)).Ok,
        "exact successor receipt was rejected");
    Require(await manager.PlannedHandoffStatusAsync(profile.Id) is
    { Code: "ReadyToComplete", ReceiptConfirmed: true, CanComplete: true, CanCancel: true },
        "owner status did not expose exact signed receipt readiness");
    Require(pairing.SetSharedWorldGrants(successorId, profile.Id,
        new SharedWorldGrants(Receive: false, EligibleHost: false)).Ok,
        "successor grant withdrawal failed");
    Require((await manager.CompletePlannedHandoffAsync(profile.Id)).Code ==
        "SuccessorAccessChanged" && !new WorldAuthorityStore(data).HasState(profile.Id) &&
        (await manager.StartAsync(profile.Id)).Code == "PlannedHandoffPending",
        "a stale signed roster and receipt handed authority to a revoked successor");
    Require(await manager.PlannedHandoffStatusAsync(profile.Id) is
    {
        Code: "SuccessorAccessChanged", ReceiptConfirmed: true,
        CanComplete: false, CanCancel: true
    },
        "owner status offered completion after successor revocation");
    Require((await manager.CancelPlannedHandoffAsync(profile.Id)).Code == "HandoffCanceled",
        "dirty roster prevented safe owner cancellation");
    Require(await manager.PlannedHandoffStatusAsync(profile.Id) is
    { Pending: false, Code: "NoPendingHandoff" },
        "cancelled handoff remained pending in owner status");
    Require(pairing.SetSharedWorldGrants(successorId, profile.Id,
        new SharedWorldGrants(Receive: true, EligibleHost: true)).Ok,
        "successor grants could not be restored for retry");
    roster = shares.PublishRoster(profile, pairing.SharedRosterMembers(profile.Id));
    pairing.ConfirmSharedRosterPublished(profile.Id, roster);
    Require((await manager.StartAsync(profile.Id)).Ok,
        "cancelled handoff did not release Start after permissions were reviewed");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "third final marker");
    prepared = await manager.PreparePlannedHandoffAsync(profile.Id, successorId,
        "https://192.0.2.1:5132");
    Require(prepared.Ok && prepared.Version is { Number: 3 },
        "reviewed retry did not publish another final save");
    version = prepared.Version!;
    // The old Host is fenced when the handoff commits, so retain the signed
    // ancestry while it is still authorized to serve the final save.
    var signedHistory = Enumerable.Range(1, checked((int)version.Number - 1))
        .Select(number => shares.ReadEarlierVersion(version, number))
        .ToDictionary(item => item.Number);
    using (var largeSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256))
    {
        var largeBytes = 5L * 1024 * 1024 * 1024;
        var largeSigned = SharedWorldService.SignVersion(version with
        {
            Files = [version.Files[0] with { Length = largeBytes }]
        }, largeSigner);
        var signedBytes = SharedWorldService.BoundedTotalBytes(largeSigned.Files);
        var reserve = 1024L * 1024 * 1024;
        Require(SharedWorldService.VerifySignature(largeSigned) &&
            !SharedWorldReadiness.HasSpaceForCopies(data.RootPath, signedBytes, 3,
                _ => checked(reserve + 3 * signedBytes - 1)) &&
            SharedWorldReadiness.HasSpaceForCopies(data.RootPath, signedBytes, 3,
                _ => checked(reserve + 3 * signedBytes)) &&
            !SharedWorldReadiness.HasSpaceForCopies(data.RootPath, signedBytes, 2,
                _ => checked(reserve + 2 * signedBytes - 1)) &&
            SharedWorldReadiness.HasSpaceForCopies(data.RootPath, signedBytes, 2,
                _ => checked(reserve + 2 * signedBytes)),
            "a large signed world crossed the restore or first Stop space boundary");
    }
    receiptDraft = new SharedWorldReceipt(1, version.GroupId, profile.Id,
        version.VersionHash, successorId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
    receipt = receiptDraft with
    {
        Signature = Convert.ToBase64String(successor.SignData(
        SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256))
    };
    Require((await manager.ConfirmSharedWorldReceiptAsync(profile.Id, successorId, receipt)).Ok,
        "reviewed successor receipt was rejected");
    var completed = await manager.CompletePlannedHandoffAsync(profile.Id);
    Require(completed.Ok && completed.Code == "OldHostFenced" &&
        WorldAuthorityTrust.Verify(completed.Authority) &&
        completed.Authority!.SuccessorReceipt == receipt &&
        (await manager.ReadPlannedHandoffOfferAsync(profile.Id, successorId))?.RecordHash ==
            completed.Authority.RecordHash &&
        await manager.ReadPlannedHandoffOfferAsync(profile.Id, Guid.NewGuid()) is null &&
        (await manager.StartAsync(profile.Id)).Code == "SharedWorldAuthorityBlocked",
        "signed handoff was reported before a durable fence");
    var routeNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var routeChallenge = SharedWorldRouteTrust.SignChallenge(completed.Authority!,
        routeNonce, routeObserverId, routeObserver);
    var routeProof = SharedWorldRouteTrust.Sign(profile.Id, completed.Authority!.RecordHash,
        routeNonce, completed.Authority.Proposal.CandidateAddress, new string('A', 64), successor);
    Require(SharedWorldRouteTrust.DirectIpAddress(completed.Authority.Proposal.CandidateAddress) &&
        !SharedWorldRouteTrust.DirectIpAddress("https://127.0.0.1:5132") &&
        !SharedWorldRouteTrust.DirectIpAddress("https://example.test:5132") &&
        SharedWorldRouteTrust.VerifyChallenge(routeChallenge, completed.Authority,
            DateTimeOffset.UtcNow) &&
        !SharedWorldRouteTrust.VerifyChallenge(routeChallenge with { ObserverDeviceId = successorId },
            completed.Authority, DateTimeOffset.UtcNow) &&
        SharedWorldRouteTrust.Verify(routeProof, completed.Authority, routeNonce, new string('A', 64)) &&
        !SharedWorldRouteTrust.Verify(routeProof with { Endpoint = "https://192.0.2.2:5132" },
            completed.Authority, routeNonce, new string('A', 64)) &&
        !SharedWorldRouteTrust.Verify(routeProof, completed.Authority, routeNonce, new string('B', 64)),
        "signed direct-IP proof accepted loopback, DNS, tampering, or a changed certificate pin");
    var restarted = new HostManager(data, registry);
    Require((await restarted.StartAsync(profile.Id)).Code == "SharedWorldAuthorityBlocked" &&
        (await restarted.SharedWorldReadAsync(profile.Id)).Status.Latest is null,
        "old Host resumed Start or sharing after restart");
    var oldHostStatus = await restarted.SharedWorldStatusAsync(profile.Id);
    Require((await restarted.PlannedHandoffStatusAsync(profile.Id)).Code == "NoPendingHandoff" &&
        oldHostStatus.Authority is
        {
            State: "OldHostFenced", ExactManagedProcessRunning: false,
            Head: { Epoch: 1 }
        } &&
        oldHostStatus.Authority.Head.RecordHash == completed.Authority!.RecordHash &&
        oldHostStatus.Authority.Head.VersionHash == completed.Authority.Version.VersionHash &&
        oldHostStatus.Authority.Head.HostDeviceId == successorId &&
        oldHostStatus.Authority.Head.HostPublicKey == successorKey &&
        oldHostStatus.Authority.Head.HostAddress == "https://192.0.2.1:5132" &&
        oldHostStatus.Authority.Message.Contains("cannot start or share", StringComparison.OrdinalIgnoreCase),
        "restarted old Host did not show the signed successor and durable fence after pending handoff was cleared");
    using var receivingData = Data("planned-receiver");
    receivingData.SaveProtected($"shared-world-pc-signing-{successorId:N}.protected",
        successor.ExportPkcs8PrivateKey());
    var vault = Path.Combine(receivingData.RootPath, "received-shared-worlds",
        successorId.ToString("N"), profile.Id.ToString("N"));
    foreach (var file in version.Files)
    {
        var target = SharedWorldService.SafeChild(Path.Combine(vault, version.VersionHash,
            SharedWorldService.PayloadDirectory), file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var published = SharedWorldService.SafeChild(Path.Combine(data.RootPath,
            "shared-worlds", profile.Id.ToString("N"), version.GroupId.ToString("N"),
            version.Number.ToString(), SharedWorldService.PayloadDirectory), file.Path);
        File.Copy(published, target);
    }
    File.WriteAllBytes(Path.Combine(vault, "latest.json"),
        JsonSerializer.SerializeToUtf8Bytes(version, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    RequireThrows<InvalidDataException>(() => PlannedHandoffReceiver.StageAsync(receivingData,
        vault, completed.Authority!, profile.Id, version.GroupId, roster.OwnerPublicKey,
        successorId, successor, () => Task.FromResult(true), CancellationToken.None)
        .GetAwaiter().GetResult(),
        "the receiver accepted a handoff before verifying the signed owner lineage");
    var receivingAuthority = new WorldAuthorityStore(receivingData);
    var proofBatch = await FriendLink.StageAuthorityProofBatchAsync(receivingAuthority,
        completed.Authority!, null, WorldAuthorityTrust.ProofVersionsPerCheck,
        (number, _) => Task.FromResult<SharedWorldVersion?>(number == version.Number ?
            version : signedHistory.GetValueOrDefault(number)), CancellationToken.None);
    Require(proofBatch is { Complete: true, Used: 3 } &&
        FriendLink.VerifyStagedAuthorityProofBatch(receivingAuthority, completed.Authority!,
            null, WorldAuthorityTrust.ProofVersionsPerCheck, CancellationToken.None).Complete,
        "the receiver did not complete the bounded signed owner lineage proof");
    var staged = await PlannedHandoffReceiver.StageAsync(receivingData, vault,
        completed.Authority!, profile.Id, version.GroupId, roster.OwnerPublicKey,
        successorId, successor, () => Task.FromResult(true), CancellationToken.None);
    Require(staged.Ok && staged.Code == "StagedForSetup" &&
        new WorldAuthorityStore(receivingData).Read(profile.Id).Single().RecordHash ==
            completed.Authority!.RecordHash &&
        new WorldAuthorityStore(receivingData).Fenced(profile.Id,
            Convert.ToBase64String(successor.ExportSubjectPublicKeyInfo()), out _),
        $"receiver did not preserve proof and remain fenced before local setup: {staged.Code} {staged.Message}");
    var receiverControlPort = FreePort();
    receivingData.SaveSettings(new HostSettings { CompanionPort = receiverControlPort });
    var receiver = new HostManager(receivingData,
        new GameServerRegistry(receivingData, true, PortProbeMode.LoopbackOnly));
    var setup = new TakeoverLocalSetup(fixture, version.PortableSetup.GameVersion,
        [], true, receiverControlPort, FreePort());
    var restoreRequest = new SuccessorRestoreRequest(completed.Authority!.RecordHash,
        setup, "Recovered fixture", "Recovered fixture");
    var destination = receivingData.NewWorldDirectory(profile.Id);
    var stagedStatus = await receiver.SuccessorRestoreStatusAsync(profile.Id);
    Require(stagedStatus.Staged && !stagedStatus.Restored &&
        stagedStatus.RecordHash == completed.Authority.RecordHash,
        "staged handoff was not available after receiving the signed offer");
    File.WriteAllBytes(Path.Combine(vault, "latest.json"),
        JsonSerializer.SerializeToUtf8Bytes(earlierVersion,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    Require(!(await receiver.RestoreSharedSuccessorAsync(profile.Id, restoreRequest)).Ok &&
        !Directory.Exists(destination), "a behind verified save became the current authority copy");
    File.WriteAllBytes(Path.Combine(vault, "latest.json"),
        JsonSerializer.SerializeToUtf8Bytes(version,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var wrongPort = await receiver.RestoreSharedSuccessorAsync(profile.Id,
        restoreRequest with { Setup = setup with { ControlPort = receiverControlPort + 1 } });
    Require(wrongPort.Code == "ControlPortMismatch" && !Directory.Exists(destination),
        "restore accepted a control port that this Host will not use: " + wrongPort.Code);
    Require((await receiver.RestoreSharedSuccessorAsync(profile.Id,
        restoreRequest with { Setup = setup with { GamePort = 1023 } })).Code ==
        "InvalidGamePort" && !Directory.Exists(destination),
        "restore copied a world before rejecting an invalid Host game port");
    var missing = await receiver.RestoreSharedSuccessorAsync(profile.Id,
        restoreRequest with { Setup = setup with { ServerFile = null } });
    Require(missing.Code == "LocalSetupIncomplete" && !Directory.Exists(destination),
        "restore accepted missing installed game files");
    var lowSpace = await receiver.RestoreSharedSuccessorAsync(profile.Id,
        restoreRequest, freeBytes: _ => 0);
    Require(lowSpace.Code == "LocalSetupIncomplete" &&
        lowSpace.PendingChecks!.Any(item => item.Contains("disk space", StringComparison.OrdinalIgnoreCase)) &&
        !Directory.Exists(destination), "restore accepted insufficient free space");
    var signedWorldBytes = SharedWorldService.BoundedTotalBytes(version.Files);
    var restoreFreeBytes = checked(1024L * 1024 * 1024 + 3 * signedWorldBytes);
    Require((await receiver.RestoreSharedSuccessorAsync(profile.Id, restoreRequest,
        freeBytes: _ => restoreFreeBytes - 1)).Code == "LocalSetupIncomplete" &&
        !Directory.Exists(destination),
        "restore accepted space for the world without both first Stop copies");
    var stagedProof = Path.Combine(receivingData.RootPath, "shared-world-staged",
        profile.Id.ToString("N"), completed.Authority.RecordHash, "authority.json");
    var originalProof = File.ReadAllBytes(stagedProof);
    File.WriteAllText(stagedProof, "{}");
    Require((await receiver.RestoreSharedSuccessorAsync(profile.Id, restoreRequest)).Code ==
        "RestoreVerificationFailed" && !Directory.Exists(destination),
        "restore accepted changed signed handoff proof");
    File.WriteAllBytes(stagedProof, originalProof);
    Directory.CreateDirectory(destination);
    File.WriteAllText(Path.Combine(destination, "unrelated.txt"), "keep this world");
    Require((await receiver.RestoreSharedSuccessorAsync(profile.Id, restoreRequest)).Code ==
        "DestinationExists" && File.Exists(Path.Combine(destination, "unrelated.txt")),
        "restore overwrote an occupied world destination");
    Directory.Delete(destination, true); // Disposable test-only folder created above.
    var interruptedCopy = Path.Combine(Path.GetDirectoryName(destination)!,
        ".successor-" + completed.Authority.RecordHash);
    receivingData.SaveProtected($"successor-restore-{profile.Id:N}.protected",
        JsonSerializer.SerializeToUtf8Bytes(new SuccessorRestoreState(1, version.GroupId,
            completed.Authority.RecordHash, version.VersionHash, destination)));
    Directory.CreateDirectory(interruptedCopy); // Simulated crash after the journal but before file copy.
    foreach (var file in version.Files)
    {
        var copied = SharedWorldService.SafeChild(interruptedCopy, file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
        File.Copy(SharedWorldService.SafeChild(Path.Combine(receivingData.RootPath,
            "shared-world-staged", profile.Id.ToString("N"), completed.Authority.RecordHash,
            SharedWorldService.PayloadDirectory), file.Path), copied);
    }
    var restoreManager = new HostManager(receivingData,
        new GameServerRegistry(receivingData, true, PortProbeMode.LoopbackOnly))
    { SuccessorFreeBytesForChecks = _ => restoreFreeBytes };
    var spaceReads = 0;
    restoreManager.SuccessorFreeBytesForChecks = _ =>
        spaceReads++ == 0 ? restoreFreeBytes :
            checked(1024L * 1024 * 1024 + 2 * signedWorldBytes - 1);
    var postCopyLow = await restoreManager.RestoreSharedSuccessorAsync(profile.Id, restoreRequest);
    Require(postCopyLow.Code == "LocalSetupIncomplete" &&
        File.ReadAllText(Path.Combine(destination, "world.dat")) == "third final marker" &&
        receivingData.LoadSettings().Profiles.Count == 0,
        "space loss after copying removed the verified copy or allowed Host setup");
    restoreManager.SuccessorFreeBytesForChecks = _ => restoreFreeBytes;
    var restored = await restoreManager.RestoreSharedSuccessorAsync(profile.Id, restoreRequest);
    Require(restored.Ok && restored.Code == "RestoredPendingChecks" &&
        File.ReadAllText(Path.Combine(destination, "world.dat")) == "third final marker" &&
        receivingData.LoadSettings().Profiles.Single().WorldDirectory == destination &&
        (await new HostManager(receivingData,
            new GameServerRegistry(receivingData, true, PortProbeMode.LoopbackOnly))
            .StartAsync(profile.Id)).Code == "SuccessorChecksPending" &&
        (await new HostManager(receivingData,
            new GameServerRegistry(receivingData, true, PortProbeMode.LoopbackOnly))
            .StartAsync(profile.Id)).Code == "SuccessorChecksPending",
        $"fresh restore was not verified, imported, or durably fenced: {restored.Code} {restored.Message}");
    var resumedStatus = await new HostManager(receivingData,
        new GameServerRegistry(receivingData, true, PortProbeMode.LoopbackOnly))
        .SuccessorRestoreStatusAsync(profile.Id);
    Require(resumedStatus.Staged && resumedStatus.Restored &&
        resumedStatus.RecordHash == completed.Authority.RecordHash,
        "restored successor status did not survive app restart");
    Require(await receiver.SignSuccessorRouteProofAsync(profile.Id,
        completed.Authority.RecordHash, routeChallenge, new string('A', 64)) is null,
        "successor gave a route proof while its companion listener was disabled");
    var routeSettings = receivingData.LoadSettings();
    routeSettings.CompanionListeningEnabled = true;
    routeSettings.CompanionEndpoint = completed.Authority.Proposal.CandidateAddress;
    receivingData.SaveSettings(routeSettings);
    var routeManager = new HostManager(receivingData,
        new GameServerRegistry(receivingData, true, PortProbeMode.LoopbackOnly));
    using var receivingCertificate = new HostIdentity(receivingData).Ensure(routeSettings.CompanionEndpoint);
    var receivingPin = HostIdentity.Fingerprint(receivingCertificate);
    var observedProof = await routeManager.SignSuccessorRouteProofAsync(profile.Id,
        completed.Authority.RecordHash, routeChallenge, receivingPin);
    Require(SharedWorldRouteTrust.Verify(observedProof, completed.Authority,
        routeNonce, receivingPin) &&
        await routeManager.SignSuccessorRouteProofAsync(profile.Id,
            completed.Authority.RecordHash, routeChallenge with { Signature = "bad" },
            receivingPin) is null,
        "successor route signer accepted a forged observer or failed a valid signed challenge");
    Require((await routeManager.FinishSharedSuccessorAsync(profile.Id,
        new SuccessorFinishRequest(completed.Authority.RecordHash, setup))).Code ==
        "SuccessorChecksPending" &&
        await routeManager.ConfirmSuccessorRouteAsync(profile.Id, completed.Authority.RecordHash,
            new SharedWorldRouteConfirmation(routeChallenge, observedProof!), receivingPin),
        "a route challenge alone counted as a completed Friend round trip");
    Require((await routeManager.StartAsync(profile.Id)).Code == "SuccessorChecksPending",
        "a signed control route alone bypassed the finish action");
    var wrongFinish = await routeManager.FinishSharedSuccessorAsync(profile.Id,
        new SuccessorFinishRequest(completed.Authority.RecordHash,
            setup with { GamePort = setup.GamePort + 1 }));
    Require(!wrongFinish.Ok && (await routeManager.StartAsync(profile.Id)).Code ==
        "SuccessorChecksPending", "changed game port completed restore checks");
    var stopFreeBytes = checked(1024L * 1024 * 1024 + 2 * signedWorldBytes);
    routeManager.SuccessorFreeBytesForChecks = _ => stopFreeBytes - 1;
    Require((await routeManager.FinishSharedSuccessorAsync(profile.Id,
        new SuccessorFinishRequest(completed.Authority.RecordHash, setup))).Code ==
        "SuccessorChecksPending", "Finish accepted space below the first Stop copy boundary");
    routeManager.SuccessorFreeBytesForChecks = _ => stopFreeBytes;
    var finished = await routeManager.FinishSharedSuccessorAsync(profile.Id,
        new SuccessorFinishRequest(completed.Authority.RecordHash, setup));
    Require(finished.Code == "ReadyForManualStart" &&
        (await routeManager.SuccessorRestoreStatusAsync(profile.Id)).ReadyForManualStart,
        $"planned successor could not finish reviewed setup: {finished.Code} {finished.Message}");
    var savedSetup = receivingData.LoadSettings();
    var changedSetup = receivingData.LoadSettings();
    changedSetup.Profiles.Single().GamePort = FreePort();
    receivingData.SaveSettings(changedSetup);
    Require((await new HostManager(receivingData,
        new GameServerRegistry(receivingData, true, PortProbeMode.LoopbackOnly))
        .StartAsync(profile.Id)).Code == "SuccessorChecksPending",
        "a changed local setup identity bypassed manual Start checks");
    receivingData.SaveSettings(savedSetup);
    routeManager.SuccessorFreeBytesForChecks = _ => stopFreeBytes - 1;
    Require((await routeManager.StartAsync(profile.Id)).Code == "SuccessorChecksPending",
        "manual Start accepted space below the backup and publication boundary");
    routeManager.SuccessorFreeBytesForChecks = _ => stopFreeBytes;
    foreach (var changedFlag in new[] { "Crossplay", "PublicListing", "Backups" })
    {
        var changed = receivingData.LoadSettings();
        if (changedFlag == "Crossplay") changed.Profiles.Single().Crossplay = true;
        else if (changedFlag == "PublicListing") changed.Profiles.Single().PublicListing = true;
        else changed.Profiles.Single().Backups.Enabled = false;
        receivingData.SaveSettings(changed);
        Require((await new HostManager(receivingData,
            new GameServerRegistry(receivingData, true, PortProbeMode.LoopbackOnly))
            .StartAsync(profile.Id)).Code == "SuccessorChecksPending",
            $"{changedFlag} changed after Finish but manual Start was allowed");
        receivingData.SaveSettings(savedSetup);
    }
    var manualStart = await routeManager.StartAsync(profile.Id);
    Require(manualStart.Ok, $"planned successor manual Start failed: {manualStart.Code} {manualStart.Message}");
    File.WriteAllText(Path.Combine(destination, "world.dat"), "successor saved change");
    Require((await routeManager.StopAsync(profile.Id)).Ok,
        "planned successor fixture could not stop gracefully");
    var plannedLatest = (await routeManager.SharedWorldReadAsync(profile.Id)).Status.Latest;
    Require(plannedLatest is not null &&
        FriendLink.AuthorizedVersionSignerForRecords(completed.Authority.Roster.OwnerPublicKey,
            plannedLatest, new WorldAuthorityStore(receivingData).Read(profile.Id)) &&
        FriendLink.ResolvedAuthorityAnchorForRecords(completed.Authority.Roster.OwnerPublicKey,
            plannedLatest, new WorldAuthorityStore(receivingData).Read(profile.Id))?.VersionHash ==
            completed.Authority.Version.VersionHash,
        "an approved Friend could not follow the planned successor's signed save");
    var plannedRestart = await routeManager.StartAsync(profile.Id);
    Require(plannedLatest is { Number: 4 } && plannedRestart.Ok,
        $"successor Stop did not continue the signed save sequence or allow a later manual Start: version={plannedLatest?.Number}, {plannedRestart.Code} {plannedRestart.Message}");
    Require((await routeManager.StopAsync(profile.Id)).Ok,
        "successor could not stop the later manual fixture run");
    receivingData.SaveSettings(new HostSettings { CompanionPort = receiverControlPort });
    var interrupted = new HostManager(receivingData,
        new GameServerRegistry(receivingData, true, PortProbeMode.LoopbackOnly));
    var oldJournal = await interrupted.RestoreSharedSuccessorAsync(profile.Id, restoreRequest);
    Require(oldJournal.Code ==
        "RestoreVerificationFailed" &&
        File.ReadAllText(Path.Combine(destination, "world.dat")) == "successor saved change" &&
        receivingData.LoadSettings().Profiles.Count == 0,
        $"an old restore journal replaced a later signed save after settings loss: {oldJournal.Code} {oldJournal.Message}");
});

await Check("Minecraft successor requires owner-prepared local server roots", () =>
{
    using var data = Data("minecraft-successor-roots");
    var worldId = "restored-world";
    var javaRoot = Path.Combine(data.MinecraftInstallRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(javaRoot);
    var jar = Path.Combine(javaRoot, "server.jar");
    using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open()))
            writer.Write("Manifest-Version: 1.0\nMain-Class: net.minecraft.server.Main\n");
        using (var writer = new StreamWriter(zip.CreateEntry("version.json").Open()))
            writer.Write(new string('A', 150));
    }
    MinecraftSetup.WriteManagedJavaProvenance(javaRoot, "1.20", Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(jar))));
    File.WriteAllText(Path.Combine(javaRoot, "server.properties"),
        $"level-name={worldId}\nserver-port=25565\nwhite-list=false\n");
    File.WriteAllText(Path.Combine(javaRoot, "eula.txt"), "eula=false\n");
    var javaExe = Path.Combine(data.RootPath, "java.exe");
    File.WriteAllText(javaExe, "local owner-installed test stand-in");
    var dummy = new SharedWorldVersion(4, Guid.NewGuid(), 1, null, Guid.NewGuid(),
        GameKinds.MinecraftJava, worldId, DateTimeOffset.UtcNow,
        SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
        new SharedWorldPortableSetup(25565, false, "1.20", [], []),
        [new SharedWorldFile("level.dat", 1, new string('A', 64))], "", "", "");
    var javaSetup = new TakeoverLocalSetup(jar, "1.20", [], true, 5131, 25565);
    Require(MinecraftPreparedRoot.Check(data.RootPath, javaRoot, javaRoot,
        dummy, javaSetup, javaExe)?.Contains("EULA") == true,
        "Java prepared root bypassed local owner terms review");
    File.WriteAllText(Path.Combine(javaRoot, "eula.txt"), "eula=true\n");
    Require(MinecraftPreparedRoot.Check(data.RootPath, javaRoot, javaRoot,
        dummy, javaSetup, javaExe)?.Contains("no reviewed player allowlist") == true,
        "Java allowed a signed source with unknown allowlist policy");
    var reviewed = dummy with { PortableSetup = dummy.PortableSetup with { AllowlistEnabled = false } };
    Require(MinecraftPreparedRoot.Check(data.RootPath, javaRoot, javaRoot,
        reviewed, javaSetup, javaExe) is null &&
        MinecraftPreparedRoot.Check(data.RootPath, javaRoot, data.RootPath,
            reviewed, javaSetup, javaExe) is not null,
        "Java accepted a wrong prepared root or rejected separate runtime and reviewed JAR");
    var javaRestricted = reviewed with
    {
        PortableSetup = reviewed.PortableSetup with
        {
            MaxPlayers = 8,
            GameMode = "survival",
            Difficulty = "normal",
            AllowlistEnabled = true,
            Allowlist = [new SharedWorldPortableAllowEntry("Alice", "11111111-1111-4111-8111-111111111111")]
        }
    };
    File.WriteAllText(Path.Combine(javaRoot, "server.properties"),
        $"level-name={worldId}\nserver-port=25565\nmax-players=8\ngamemode=survival\ndifficulty=normal\nwhite-list=false\n");
    Require(MinecraftPreparedRoot.Check(data.RootPath, javaRoot, javaRoot,
        javaRestricted, javaSetup, javaExe)?.Contains("white-list=true") == true,
        "Java allowed a disabled prepared allowlist against the signed source");
    File.WriteAllText(Path.Combine(javaRoot, "server.properties"),
        $"level-name={worldId}\nserver-port=25565\nmax-players=8\ngamemode=survival\ndifficulty=normal\nwhite-list=true\n");
    Require(MinecraftPreparedRoot.Check(data.RootPath, javaRoot, javaRoot,
        javaRestricted, javaSetup, javaExe)?.Contains("whitelist.json") == true,
        "Java allowed missing player entries against the signed source");
    File.WriteAllText(Path.Combine(javaRoot, "whitelist.json"),
        "[{\"name\":\"Alice\",\"uuid\":\"11111111-1111-4111-8111-111111111111\"}]");
    Require(MinecraftPreparedRoot.Check(data.RootPath, javaRoot, javaRoot,
        javaRestricted, javaSetup, javaExe) is null,
        "Java refused prepared settings and allowlist matching the signed source");
    File.WriteAllText(Path.Combine(javaRoot, "server.properties"),
        $"level-name={worldId}\nserver-port=25565\nmax-players=7\ngamemode=creative\ndifficulty=normal\nwhite-list=true\n");
    Require(MinecraftPreparedRoot.Check(data.RootPath, javaRoot, javaRoot,
        javaRestricted, javaSetup, javaExe)?.Contains("max-players=8") == true,
        "Java allowed a changed player limit after matching the signed source");
    File.WriteAllText(Path.Combine(javaRoot, "server.properties"),
        $"level-name={worldId}\nserver-port=25565\nmax-players=8\ngamemode=creative\ndifficulty=normal\nwhite-list=true\n");
    Require(MinecraftPreparedRoot.Check(data.RootPath, javaRoot, javaRoot,
        javaRestricted, javaSetup, javaExe)?.Contains("gamemode=survival") == true,
        "Java allowed a changed game mode after matching the signed source");
    File.WriteAllText(Path.Combine(javaRoot, "server.properties"),
        $"level-name={worldId}\nserver-port=25565\nmax-players=8\ngamemode=survival\ndifficulty=normal\nwhite-list=true\nwhite-list=false\n");
    Require(MinecraftPreparedRoot.Check(data.RootPath, javaRoot, javaRoot,
        javaRestricted, javaSetup, javaExe) is not null,
        "Java accepted a duplicate prepared allowlist setting after restore");
    var bedrockRoot = Path.Combine(data.MinecraftInstallRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(bedrockRoot);
    File.WriteAllText(Path.Combine(bedrockRoot, "server.properties"),
        $"level-name={worldId}\nserver-port=19132\nallow-list=false\n");
    var bedrockExe = Path.Combine(bedrockRoot, "bedrock_server.exe");
    File.WriteAllText(bedrockExe, "local owner-installed test stand-in");
    var bedrock = reviewed with { Game = GameKinds.MinecraftBedrock };
    var bedrockSetup = javaSetup with { ServerFile = bedrockExe, GamePort = 19132 };
    Require(MinecraftPreparedRoot.Check(data.RootPath, bedrockRoot, bedrockRoot,
        bedrock, bedrockSetup, bedrockExe) is null &&
        MinecraftPreparedRoot.Check(data.RootPath, bedrockRoot, bedrockRoot,
            bedrock, bedrockSetup, javaExe) is not null &&
        !Directory.Exists(Path.Combine(bedrockRoot, "worlds", worldId)),
        "Bedrock accepted an external executable or changed the prepared root before a verified copy");
    var bedrockRestricted = bedrock with
    {
        PortableSetup = bedrock.PortableSetup with
        {
            AllowlistEnabled = true,
            Allowlist = [new SharedWorldPortableAllowEntry("Bob", "1234567890123456789")]
        }
    };
    File.WriteAllText(Path.Combine(bedrockRoot, "server.properties"),
        $"level-name={worldId}\nserver-port=19132\nallow-list=false\n");
    Require(MinecraftPreparedRoot.Check(data.RootPath, bedrockRoot, bedrockRoot,
        bedrockRestricted, bedrockSetup, bedrockExe)?.Contains("allow-list=true") == true,
        "Bedrock allowed a disabled prepared allowlist against the signed source");
    File.WriteAllText(Path.Combine(bedrockRoot, "server.properties"),
        $"level-name={worldId}\nserver-port=19132\nallow-list=true\n");
    File.WriteAllText(Path.Combine(bedrockRoot, "allowlist.json"),
        "[{\"name\":\"Bob\",\"xuid\":\"9999999999999999999\"}]");
    Require(MinecraftPreparedRoot.Check(data.RootPath, bedrockRoot, bedrockRoot,
        bedrockRestricted, bedrockSetup, bedrockExe)?.Contains("allowlist.json") == true,
        "Bedrock accepted the same player name with a different signed XUID");
    File.WriteAllText(Path.Combine(bedrockRoot, "allowlist.json"),
        "[{\"name\":\"Bob\",\"xuid\":\"1234567890123456789\"}]");
    Require(MinecraftPreparedRoot.Check(data.RootPath, bedrockRoot, bedrockRoot,
        bedrockRestricted, bedrockSetup, bedrockExe) is null,
        "Bedrock refused prepared allowlist matching the signed source");
    return Task.CompletedTask;
});

await Check("shared world authority requires signed majority, fences old Host, and survives restart", async () =>
{
    using var data = Data("authority-fence");
    var profile = Profile("authority", "authority-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    data.SaveSettings(Settings(profile));
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "authority fixture");
    var backups = new WorldBackupService(data, TimeProvider.System);
    var shares = new SharedWorldService(data, backups);
    var voters = Enumerable.Range(0, 3).Select(_ => (Id: Guid.NewGuid(), Key:
        ECDsa.Create(ECCurve.NamedCurves.nistP256))).ToArray();
    try
    {
        var duplicateKey = Convert.ToBase64String(voters[0].Key.ExportSubjectPublicKeyInfo());
        RequireThrows<InvalidDataException>(() => shares.PublishRoster(profile,
        [
            new SharedWorldRosterMember(voters[0].Id, duplicateKey,
                new SharedWorldGrants(RecoveryVoter: true), false),
            new SharedWorldRosterMember(voters[1].Id, duplicateKey,
                new SharedWorldGrants(RecoveryVoter: true), false)
        ]), "one PC key received two roster votes");
        var roster = shares.PublishRoster(profile, voters.Select(voter =>
            new SharedWorldRosterMember(voter.Id,
                Convert.ToBase64String(voter.Key.ExportSubjectPublicKeyInfo()),
                new SharedWorldGrants(Receive: true, EligibleHost: true, RecoveryVoter: true), false)).ToArray());
        var backup = backups.Create(profile, BackupKinds.Rolling);
        Require(backup.Ok && backup.Backup is not null, "authority fixture backup failed");
        var version = shares.PublishAfterStop(profile, backup.Backup!.Id).Version
            ?? throw new Exception("authority fixture version missing");
        var candidateKey = Convert.ToBase64String(voters[0].Key.ExportSubjectPublicKeyInfo());
        WorldAuthorityProposal Propose(ECDsa signer, string kind, string address)
        {
            var draft = new WorldAuthorityProposal(1, roster.GroupId, profile.Id, 1, null,
                WorldAuthorityTrust.RosterHash(roster), version.VersionHash, candidateKey,
                address, kind, voters[0].Id,
                Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()), "");
            return draft with
            {
                Signature = Convert.ToBase64String(signer.SignData(
                WorldAuthorityTrust.ProposalBasis(draft), HashAlgorithmName.SHA256))
            };
        }
        WorldAuthorityRecord Record(WorldAuthorityProposal proposal,
            IReadOnlyList<WorldAuthorityVote> votes, string? ownerApproval = null,
            SharedWorldReceipt? successorReceipt = null)
        {
            var draft = new WorldAuthorityRecord(1, proposal, roster, version, votes,
                ownerApproval, "", successorReceipt);
            return draft with { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(draft)) };
        }
        var store = new WorldAuthorityStore(data);
        var proposal = Propose(voters[0].Key, "Quorum", "https://127.0.0.1:5132");
        var vote0 = store.SignLocalVote(profile.Id, proposal, roster, voters[0].Id, voters[0].Key);
        Require(store.SignLocalVote(profile.Id, proposal, roster, voters[0].Id, voters[0].Key) == vote0,
            "same proposal vote retry changed the durable vote");
        Require(new WorldAuthorityStore(data).SignLocalVote(profile.Id, proposal, roster,
            voters[0].Id, voters[0].Key) == vote0, "restart forgot the durable local vote");
        var competing = Propose(voters[0].Key, "Quorum", "https://127.0.0.1:5133");
        RequireThrows<InvalidDataException>(() => store.SignLocalVote(profile.Id, competing,
            roster, voters[0].Id, voters[0].Key), "double vote was allowed");
        var vote1 = store.SignLocalVote(profile.Id, proposal, roster, voters[1].Id, voters[1].Key);
        var vote2 = store.SignLocalVote(profile.Id, proposal, roster, voters[2].Id, voters[2].Key);
        var voteLog = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
            "authority", "local-votes.jsonl");
        var originalVotes = File.ReadAllBytes(voteLog);
        Require(data.HasProtected($"authority-vote-floor-{profile.Id:N}.protected"),
            "the durable local vote floor was not protected");
        File.AppendAllText(voteLog, "tampered\n");
        RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(data).SignLocalVote(profile.Id,
            proposal, roster, voters[0].Id, voters[0].Key),
            "tampered local vote ledger allowed another signature");
        File.WriteAllBytes(voteLog, originalVotes[..(Array.IndexOf(originalVotes, (byte)'\n') + 1)]);
        RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(data).SignLocalVote(profile.Id,
            proposal, roster, voters[0].Id, voters[0].Key),
            "rolled-back local vote ledger allowed another signature");
        File.WriteAllBytes(voteLog, originalVotes);
        Require(new WorldAuthorityStore(data).SignLocalVote(profile.Id, proposal, roster,
            voters[0].Id, voters[0].Key) == vote0, "restored local vote was not reused");
        Require(!WorldAuthorityTrust.Verify(Record(proposal, [vote0])), "minority made quorum");
        Require(!WorldAuthorityTrust.Verify(Record(proposal, [vote0, vote0])), "duplicate vote made quorum");
        Require(!WorldAuthorityTrust.Verify(Record(proposal, [vote0, vote1 with { Signature = vote2.Signature }])),
            "forged vote made quorum");
        using var ownerKey = ECDsa.Create();
        ownerKey.ImportPkcs8PrivateKey(data.LoadProtected("shared-world-signing-key.protected")!, out _);
        var duplicateDraft = roster with
        {
            Members = [roster.Members[0], roster.Members[1] with
            { PublicKey = roster.Members[0].PublicKey }],
            Signature = ""
        };
        var duplicateSigned = duplicateDraft with
        {
            Signature = Convert.ToBase64String(ownerKey.SignData(
            SharedWorldRosterTrust.Basis(duplicateDraft), HashAlgorithmName.SHA256))
        };
        Require(!SharedWorldRosterTrust.Verify(duplicateSigned),
            "owner-signed roster assigned two votes to one public key");
        var overrideDraft = proposal with { Kind = "OwnerOverride", Signature = "" };
        var overrideProposal = overrideDraft with
        {
            Signature = Convert.ToBase64String(voters[0].Key.SignData(
            WorldAuthorityTrust.ProposalBasis(overrideDraft), HashAlgorithmName.SHA256))
        };
        var ownerApproval = Convert.ToBase64String(ownerKey.SignData(
            WorldAuthorityTrust.OwnerBasis(overrideProposal), HashAlgorithmName.SHA256));
        Require(WorldAuthorityTrust.Verify(Record(overrideProposal, [], ownerApproval)),
            "enabled owner override was rejected");
        var plannedDraft = overrideProposal with
        {
            Kind = "Planned",
            ProposerDeviceId = Guid.Empty,
            ProposerPublicKey = roster.OwnerPublicKey,
            Signature = ""
        };
        var plannedProposal = plannedDraft with
        {
            Signature = Convert.ToBase64String(ownerKey.SignData(
            WorldAuthorityTrust.ProposalBasis(plannedDraft), HashAlgorithmName.SHA256))
        };
        var plannedApproval = Convert.ToBase64String(ownerKey.SignData(
            WorldAuthorityTrust.OwnerBasis(plannedProposal), HashAlgorithmName.SHA256));
        var receiptDraft = new SharedWorldReceipt(1, version.GroupId, profile.Id,
            version.VersionHash, voters[0].Id, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
        var successorReceipt = receiptDraft with
        {
            Signature = Convert.ToBase64String(
            voters[0].Key.SignData(SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256))
        };
        Require(WorldAuthorityTrust.Verify(Record(plannedProposal, [], plannedApproval, successorReceipt)),
            "owner-signed planned handoff with exact successor receipt was rejected");
        Require(!WorldAuthorityTrust.Verify(Record(plannedProposal, [], plannedApproval)),
            "planned handoff without a successor copy receipt was accepted");
        Require(!WorldAuthorityTrust.Verify(Record(plannedProposal, [], plannedApproval,
            successorReceipt with { VersionHash = new string('A', 64) })),
            "planned handoff accepted a receipt for another save");
        Require(!WorldAuthorityTrust.Verify(Record(plannedProposal, [])),
            "planned handoff without owner approval was accepted");
        var noOverrideRoster = shares.PublishRoster(profile, roster.Members, ownerOverride: false);
        var disabledDraft = overrideProposal with
        { RosterHash = WorldAuthorityTrust.RosterHash(noOverrideRoster), Signature = "" };
        var disabledProposal = disabledDraft with
        {
            Signature = Convert.ToBase64String(voters[0].Key.SignData(
            WorldAuthorityTrust.ProposalBasis(disabledDraft), HashAlgorithmName.SHA256))
        };
        var disabledRecord = new WorldAuthorityRecord(1, disabledProposal, noOverrideRoster, version, [],
            Convert.ToBase64String(ownerKey.SignData(WorldAuthorityTrust.OwnerBasis(disabledProposal),
                HashAlgorithmName.SHA256)), "");
        disabledRecord = disabledRecord with
        {
            RecordHash = WorldAuthorityTrust.Hash(
            WorldAuthorityTrust.RecordBasis(disabledRecord))
        };
        Require(!WorldAuthorityTrust.Verify(disabledRecord), "disabled owner override was accepted");
        var accepted = Record(proposal, [vote0, vote1]);
        Require(WorldAuthorityTrust.Verify(accepted), "valid majority was rejected");
        RequireThrows<InvalidDataException>(() => store.AppendReceived(accepted, profile.Id,
            version.GroupId, candidateKey),
            "untrusted owner identity was accepted for a received authority");
        RequireThrows<InvalidDataException>(() => store.AppendReceived(accepted, Guid.NewGuid(),
            version.GroupId, roster.OwnerPublicKey), "authority crossed into a different profile");
        store.AppendReceived(accepted, profile.Id, version.GroupId, roster.OwnerPublicKey);
        Require(store.Fenced(profile.Id, shares.LocalAuthorityPublicKey(), out _),
            "old Host was not fenced");
        data.SaveProtected($"shared-world-pc-signing-{voters[0].Id:N}.protected",
            voters[0].Key.ExportPkcs8PrivateKey());
        RequireThrows<InvalidDataException>(() => store.BindLocalSuccessor(profile.Id,
            accepted.RecordHash, voters[1].Id), "unrelated PC bound the successor identity");
        RequireThrows<InvalidDataException>(() => store.BindLocalSuccessor(profile.Id,
            accepted.RecordHash, voters[0].Id),
            "legacy device key was accepted without a separate signed hosting binding");
        Require(store.Fenced(profile.Id, shares.LocalAuthorityPublicKey(), out _),
            "old Host was unfenced without explicit successor binding");
        {
            using var oldPortReservation = new TcpListener(IPAddress.Loopback, 0);
            oldPortReservation.Start();
            var oldPort = ((IPEndPoint)oldPortReservation.LocalEndpoint).Port;
            oldPortReservation.Stop();
            var oldAddress = $"https://127.0.0.1:{oldPort}";
            using var oldCertificate = new HostIdentity(data).Ensure(oldAddress);
            var oldSettings = data.LoadSettings();
            oldSettings.CompanionBindAddress = "127.0.0.1";
            oldSettings.CompanionEndpoint = oldAddress;
            oldSettings.CompanionPort = oldPort;
            oldSettings.CompanionListeningEnabled = true;
            data.SaveSettings(oldSettings);
            var bearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var remainingBearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var unsignedBearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var mismatchedBearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var unsignedId = Guid.NewGuid();
            using var unsignedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var mismatchedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            data.SavePairingState(new PairingPersistentState
            {
                Devices = [new PairedDevice
            {
                Id = voters[1].Id, AssignedProfileIds = [profile.Id],
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer))),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                SharedWorldGrants = new() { [profile.Id] = new(RecoveryVoter: true) },
                SharedWorldPublicKey = Convert.ToBase64String(voters[1].Key.ExportSubjectPublicKeyInfo())
            }, new PairedDevice
            {
                Id = voters[2].Id, AssignedProfileIds = [profile.Id],
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remainingBearer))),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                SharedWorldGrants = new() { [profile.Id] = new(Receive: true, RecoveryVoter: true) },
                SharedWorldPublicKey = Convert.ToBase64String(voters[2].Key.ExportSubjectPublicKeyInfo())
            }, new PairedDevice
            {
                Id = unsignedId, AssignedProfileIds = [profile.Id],
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unsignedBearer))),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                SharedWorldGrants = new() { [profile.Id] = new(Receive: true, RecoveryVoter: true) },
                SharedWorldPublicKey = Convert.ToBase64String(unsignedKey.ExportSubjectPublicKeyInfo())
            }, new PairedDevice
            {
                Id = voters[0].Id, AssignedProfileIds = [profile.Id],
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mismatchedBearer))),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                SharedWorldGrants = new() { [profile.Id] = new(Receive: true, RecoveryVoter: true) },
                SharedWorldPublicKey = Convert.ToBase64String(mismatchedKey.ExportSubjectPublicKeyInfo())
            }]
            });
            var manager = new HostManager(data, Games(data));
            var fixturePairing = new PairingService(data);
            // This fixture installs a synthetic signed roster rather than publishing
            // one from its synthetic pairing state. Model that publication as complete.
            var fixtureRosterDirty = Path.Combine(data.RootPath, "shared-worlds",
                profile.Id.ToString("N"), "roster-dirty");
            if (File.Exists(fixtureRosterDirty)) File.Delete(fixtureRosterDirty);
            var newerDraft = proposal with
            { RosterHash = WorldAuthorityTrust.RosterHash(noOverrideRoster), Signature = "" };
            var newerProposal = newerDraft with
            {
                Signature = Convert.ToBase64String(voters[0].Key.SignData(
                WorldAuthorityTrust.ProposalBasis(newerDraft), HashAlgorithmName.SHA256))
            };
            WorldAuthorityVote NewVote(int index)
            {
                var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(newerProposal),
                    voters[index].Id, Convert.ToBase64String(voters[index].Key.ExportSubjectPublicKeyInfo()), "");
                return draft with
                {
                    Signature = Convert.ToBase64String(voters[index].Key.SignData(
                    WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256))
                };
            }
            var newer = new WorldAuthorityRecord(1, newerProposal, noOverrideRoster, version,
                [NewVote(0), NewVote(1)], null, "");
            newer = newer with { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(newer)) };
            using var oldModeGate = new SemaphoreSlim(1, 1);
            var oldListener = new CompanionServer(data, manager, fixturePairing,
                Games(data), new ServerLogService(data, manager), oldModeGate, oldPort + 2);
            try
            {
                await oldListener.SyncAsync();
                Require(oldListener.ListenerState == CompanionListenerStates.Listening,
                    "the old Host's owner-enabled HTTPS listener did not open");
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                        certificate is not null && HostIdentity.Fingerprint(certificate) ==
                        HostIdentity.Fingerprint(oldCertificate)
                };
                using var client = new HttpClient(handler) { BaseAddress = new Uri(oldAddress + "/") };
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                client.DefaultRequestHeaders.Add("X-Device-Id", voters[1].Id.ToString());
                HttpClient ReviewClient(Guid id, string credential)
                {
                    var peer = new HttpClient(handler, disposeHandler: false)
                    { BaseAddress = new Uri(oldAddress + "/") };
                    peer.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
                    peer.DefaultRequestHeaders.Add("X-Device-Id", id.ToString());
                    return peer;
                }
                using var remainingClient = ReviewClient(voters[2].Id, remainingBearer);
                using var unsignedClient = ReviewClient(unsignedId, unsignedBearer);
                using var mismatchedClient = ReviewClient(voters[0].Id, mismatchedBearer);
                var route = $"api/companion/servers/{profile.Id}/shared-world/authority";
                using var invalid = await client.PostAsJsonAsync(route,
                    newer with { RecordHash = new string('0', 64) });
                Require(invalid.StatusCode == HttpStatusCode.Forbidden,
                    "old Host accepted a tampered authority decision");
                using var valid = await client.PostAsJsonAsync(route, newer);
                Require(valid.IsSuccessStatusCode,
                    "old Host did not ingest the signed newer authority over authenticated HTTPS: " +
                    valid.StatusCode + " " + await valid.Content.ReadAsStringAsync());
                using var reviewRoster = await client.GetAsync(
                    $"api/companion/servers/{profile.Id}/shared-world/roster");
                using var fencedRange = await client.GetAsync(
                    $"api/companion/servers/{profile.Id}/shared-world/versions/range/1/1");
                using var reviewHistory = await client.GetAsync(route);
                var reviewed = await reviewHistory.Content.ReadFromJsonAsync<List<WorldAuthorityRecord>>();
                Require(reviewRoster.IsSuccessStatusCode && reviewHistory.IsSuccessStatusCode &&
                    !fencedRange.IsSuccessStatusCode &&
                    reviewed?.Count == 2 &&
                    reviewed.Select(item => item.RecordHash).ToHashSet().SetEquals(
                        [accepted.RecordHash, newer.RecordHash]),
                    "voter-only PC could not review competing signed histories on the fenced Host");
                using (var voterData = Data("authority-review-voter"))
                {
                    voterData.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(
                        new FriendConfiguration
                        {
                            Endpoint = oldAddress,
                            Fingerprint = HostIdentity.Fingerprint(oldCertificate),
                            DeviceId = voters[1].Id,
                            Credential = bearer,
                            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
                        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    voterData.SaveProtected($"shared-world-pc-signing-{voters[1].Id:N}.protected",
                        voters[1].Key.ExportPkcs8PrivateKey());
                    using var voterLink = new FriendLink(voterData, "friend.protected");
                    var pendingReview = await voterLink.ReviewSharedHistoryAsync(profile.Id);
                    Require(pendingReview.Code == "GroupReviewRequired" &&
                        pendingReview.GroupId == roster.GroupId &&
                        pendingReview.OwnerPublicKey == roster.OwnerPublicKey,
                        "voter-only Friend did not require explicit signed group and owner review");
                    var voterReview = await voterLink.ReviewSharedHistoryAsync(profile.Id,
                        new WorldHistoryReviewRequest(roster.GroupId, roster.OwnerPublicKey));
                    Require(voterReview.Ok && voterReview.RecordCount == 2 &&
                        voterReview.CompetingHeads == 2,
                        "voter-only Friend could not fetch signed conflict history without save consent: " +
                        voterReview.Code);
                }
                using var deniedVersion = await client.GetAsync(
                    $"api/companion/servers/{profile.Id}/shared-world");
                Require(deniedVersion.StatusCode == HttpStatusCode.Forbidden,
                    "voter-only PC gained save transfer access from review permission");
                File.WriteAllText(fixtureRosterDirty, "review pending");
                using var dirtyHistory = await client.GetAsync(route);
                Require(dirtyHistory.IsSuccessStatusCode,
                    "a fenced Host withheld signed history from a still-granted reviewer");
                Require(fixturePairing.Revoke(voters[1].Id).Ok,
                    "fixture could not revoke the authority sender");
                using var revokedHistory = await client.GetAsync(route);
                using var remainingHistory = await remainingClient.GetAsync(route);
                using var remainingTransfer = await remainingClient.GetAsync(
                    $"api/companion/servers/{profile.Id}/shared-world");
                using var unsignedHistory = await unsignedClient.GetAsync(route);
                using var mismatchedHistory = await mismatchedClient.GetAsync(route);
                using var afterRevoke = await client.PostAsJsonAsync(route, newer);
                Require(revokedHistory.StatusCode == HttpStatusCode.Forbidden &&
                    remainingHistory.IsSuccessStatusCode &&
                    remainingTransfer.StatusCode == HttpStatusCode.Forbidden &&
                    unsignedHistory.StatusCode == HttpStatusCode.Forbidden &&
                    mismatchedHistory.StatusCode == HttpStatusCode.Forbidden &&
                    afterRevoke.StatusCode == HttpStatusCode.Forbidden &&
                    !fixturePairing.CommitSharedWorldAuthority(voters[1].Id, profile.Id,
                        Convert.ToBase64String(voters[1].Key.ExportSubjectPublicKeyInfo()),
                        () => throw new Exception("revoked sender reached authority append")),
                    "fenced revocation reopened access or blocked a still-granted reviewer");
            }
            finally { await oldListener.StopAsync(); }
            var restartedReview = new HostManager(data, Games(data));
            var restartedPairing = new PairingService(data);
            Require(await restartedReview.SharedWorldReviewRosterAsync(profile.Id) is not null &&
                restartedPairing.AuthorizeSharedHistory(
                    new PairedDevice { Id = voters[2].Id }, profile.Id, out _).Ok &&
                !restartedPairing.AuthorizeSharedHistory(
                    new PairedDevice { Id = voters[1].Id }, profile.Id, out _).Ok,
                "restart lost current-grant review access or revived a revoked PC");
            var retainedPairing = data.LoadPairingState();
            try
            {
                var alteredPairing = data.LoadPairingState();
                var remainingDevice = alteredPairing.Devices.Single(item => item.Id == voters[2].Id);
                remainingDevice.AssignedProfileIds = [];
                data.SavePairingState(alteredPairing);
                Require(!new PairingService(data).AuthorizeSharedHistory(
                    new PairedDevice { Id = voters[2].Id }, profile.Id, out _).Ok,
                    "a removed PC retained fenced history review access");
                remainingDevice.AssignedProfileIds = [profile.Id];
                remainingDevice.AccessExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                data.SavePairingState(alteredPairing);
                Require(!new PairingService(data).AuthorizeSharedHistory(
                    new PairedDevice { Id = voters[2].Id }, profile.Id, out _).Ok,
                    "an expired PC retained fenced history review access");
            }
            finally { data.SavePairingState(retainedPairing); }
            Require(store.Read(profile.Id).Count == 2, "newer signed roster authority was not applied");
            Require((await manager.StartAsync(profile.Id)).Code == "SharedWorldAuthorityBlocked",
                "local or remote Start path bypassed authority");
            Require((await manager.SharedWorldReadAsync(profile.Id)).Status.Latest is null,
                "old Host still served the shared head");
            Require(await manager.SharedWorldRosterAsync(profile.Id) is null,
                "old Host still served the signed roster through its sharing path");
            Require((await manager.ConfirmSharedWorldReceiptAsync(profile.Id, voters[0].Id,
                new SharedWorldReceipt(1, version.GroupId, profile.Id, version.VersionHash,
                    voters[0].Id, roster.Epoch, roster.Revision, Guid.NewGuid(), ""))).Code ==
                "SharedWorldAuthorityBlocked", "old Host accepted a copy receipt");
            Require((await manager.SetSharedSavesAsync(profile.Id, true)).Code ==
                "SharedWorldAuthorityBlocked", "old Host re-enabled sharing");
            RequireThrows<InvalidDataException>(() => manager.ReadSharedChunk(version, 0, 0),
                "old Host served a save chunk");
            RequireThrows<InvalidDataException>(() => manager.ReadEarlierSharedVersion(version, 0),
                "old Host served an earlier version");
        }
        profile.SharedSavesEnabled = false;
        data.SaveSettings(Settings(profile));
        {
            var restarted = new HostManager(data, Games(data));
            Require((await restarted.StartAsync(profile.Id)).Code == "SharedWorldAuthorityBlocked",
                "restart or sharing toggle bypassed authority");
        }
        var conflictVotes = new[] {
            new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(competing), voters[0].Id,
                candidateKey, "")
        };
        // A competing authority is retained as a separate history when it has
        // its own valid signatures; local double-vote prevention is independent.
        var competingVote1 = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(competing),
            voters[1].Id, Convert.ToBase64String(voters[1].Key.ExportSubjectPublicKeyInfo()), "");
        var signedCompeting = conflictVotes[0] with
        {
            Signature = Convert.ToBase64String(voters[0].Key.SignData(
            WorldAuthorityTrust.VoteBasis(conflictVotes[0]), HashAlgorithmName.SHA256))
        };
        competingVote1 = competingVote1 with
        {
            Signature = Convert.ToBase64String(voters[1].Key.SignData(
            WorldAuthorityTrust.VoteBasis(competingVote1), HashAlgorithmName.SHA256))
        };
        store.Append(Record(competing, [signedCompeting, competingVote1]),
            stopAfterLogForChecks: true, enforceCurrentGrants: false);
        Require(File.Exists(Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
            "authority", "append.pending")), "interrupted append did not retain its journal");
        Require(new WorldAuthorityStore(data).Read(profile.Id).Count == 3 &&
            !File.Exists(Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
                "authority", "append.pending")) && store.Fenced(profile.Id, candidateKey, out _),
            "same-epoch competing histories were silently selected");
        using (var resolutionData = Data("authority-resolution"))
        {
            var resolution = new WorldAuthorityStore(resolutionData);
            var losingVersion = SharedWorldService.SignVersion(version with
            { BackupId = Guid.NewGuid() }, ownerKey);
            var losingDraft = competing with { VersionHash = losingVersion.VersionHash, Signature = "" };
            var losingProposal = losingDraft with
            {
                Signature = Convert.ToBase64String(
                voters[0].Key.SignData(WorldAuthorityTrust.ProposalBasis(losingDraft),
                    HashAlgorithmName.SHA256))
            };
            WorldAuthorityVote LosingVote(int index)
            {
                var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(losingProposal),
                    voters[index].Id, Convert.ToBase64String(voters[index].Key.ExportSubjectPublicKeyInfo()), "");
                return draft with
                {
                    Signature = Convert.ToBase64String(voters[index].Key.SignData(
                    WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256))
                };
            }
            var losingDraftRecord = new WorldAuthorityRecord(1, losingProposal, roster,
                losingVersion, [LosingVote(0), LosingVote(1)], null, "");
            var losingRecord = losingDraftRecord with
            {
                RecordHash = WorldAuthorityTrust.Hash(
                WorldAuthorityTrust.RecordBasis(losingDraftRecord))
            };
            resolution.Append(accepted);
            resolution.Append(losingRecord);
            Require(resolution.ReadUniqueHead(profile.Id) is null &&
                WorldAuthorityTrust.EffectiveHeads(resolution.Read(profile.Id)).Length == 2,
                "split authority was silently selected before a decision");
            var selectedDevice = Convert.ToBase64String(voters[0].Key.ExportSubjectPublicKeyInfo());
            var hostingKey = resolution.PrepareLocalHostingKey(profile.Id);
            var hashes = new[] { accepted.RecordHash, losingRecord.RecordHash }
                .Order(StringComparer.Ordinal).ToArray();
            WorldAuthorityProposal ResolutionProposal(SharedWorldRoster selectedRoster,
                IReadOnlyList<string> namedHeads, string kind = "ResolutionQuorum",
                WorldAuthorityRecord? selected = null,
                SharedWorldVersion? versionChoice = null)
            {
                selected ??= accepted;
                var draft = new WorldAuthorityProposal(3, selectedRoster.GroupId, profile.Id, 2,
                    selected.RecordHash, WorldAuthorityTrust.RosterHash(selectedRoster),
                    versionChoice?.VersionHash ?? selected.Version.VersionHash, hostingKey,
                    "https://127.0.0.1:5132", kind,
                    voters[0].Id, selectedDevice, "", CompetingHeadHashes: namedHeads);
                var bindingDraft = new WorldSuccessorBinding(voters[0].Id, selectedDevice,
                    hostingKey, "");
                var binding = bindingDraft with
                {
                    Signature = Convert.ToBase64String(
                    voters[0].Key.SignData(WorldAuthorityTrust.BindingBasis(draft, bindingDraft),
                        HashAlgorithmName.SHA256))
                };
                draft = draft with { SuccessorBinding = binding };
                return draft with
                {
                    Signature = Convert.ToBase64String(voters[0].Key.SignData(
                    WorldAuthorityTrust.ProposalBasis(draft), HashAlgorithmName.SHA256))
                };
            }
            WorldAuthorityVote ResolutionVote(WorldAuthorityProposal forProposal, int index)
            {
                var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(forProposal),
                    voters[index].Id, Convert.ToBase64String(voters[index].Key.ExportSubjectPublicKeyInfo()), "");
                return draft with
                {
                    Signature = Convert.ToBase64String(voters[index].Key.SignData(
                    WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256))
                };
            }
            WorldAuthorityRecord ResolutionRecord(WorldAuthorityProposal forProposal,
                SharedWorldRoster selectedRoster, IReadOnlyList<WorldAuthorityVote> signedVotes,
                string? approval = null, SharedWorldVersion? selectedVersion = null)
            {
                var draft = new WorldAuthorityRecord(2, forProposal, selectedRoster,
                    selectedVersion ?? version,
                    signedVotes, approval, "");
                return draft with
                {
                    RecordHash = WorldAuthorityTrust.Hash(
                    WorldAuthorityTrust.RecordBasis(draft))
                };
            }
            var resolutionProposal = ResolutionProposal(roster, hashes);
            var resolved = ResolutionRecord(resolutionProposal, roster,
                [ResolutionVote(resolutionProposal, 0), ResolutionVote(resolutionProposal, 1)]);
            Require(WorldAuthorityTrust.Verify(resolved), "valid exact-head majority was rejected");
            Require(!WorldAuthorityTrust.Verify(ResolutionRecord(resolutionProposal, roster,
                [ResolutionVote(resolutionProposal, 0)])), "minority resolved a split");
            Require(!WorldAuthorityTrust.Verify(ResolutionRecord(resolutionProposal, roster,
                [ResolutionVote(resolutionProposal, 0), ResolutionVote(resolutionProposal, 0)])),
                "duplicate resolution votes counted twice");
            Require(!WorldAuthorityTrust.Verify(ResolutionRecord(resolutionProposal, roster,
                [ResolutionVote(resolutionProposal, 0), ResolutionVote(resolutionProposal, 1) with
                    { Signature = ResolutionVote(resolutionProposal, 2).Signature }])),
                "forged resolution vote was accepted");
            RequireThrows<InvalidDataException>(() => resolution.Append(ResolutionRecord(
                ResolutionProposal(roster, [accepted.RecordHash]), roster,
                [ResolutionVote(resolutionProposal, 0), ResolutionVote(resolutionProposal, 1)])),
                "incomplete head set was accepted");
            var extraHeads = hashes.Append(new string('A', 64)).Order(StringComparer.Ordinal).ToArray();
            var extraProposal = ResolutionProposal(roster, extraHeads);
            RequireThrows<InvalidDataException>(() => resolution.Append(ResolutionRecord(extraProposal,
                roster, [ResolutionVote(extraProposal, 0), ResolutionVote(extraProposal, 1)])),
                "extra head was accepted");
            var forgedHeads = new[] { accepted.RecordHash, new string('F', 64) }
                .Order(StringComparer.Ordinal).ToArray();
            var forgedProposal = ResolutionProposal(roster, forgedHeads);
            RequireThrows<InvalidDataException>(() => resolution.Append(ResolutionRecord(forgedProposal,
                roster, [ResolutionVote(forgedProposal, 0), ResolutionVote(forgedProposal, 1)])),
                "forged competing head was accepted");
            var unverifiedChoice = ResolutionProposal(roster, hashes,
                versionChoice: losingVersion);
            RequireThrows<InvalidDataException>(() => resolution.Append(ResolutionRecord(
                unverifiedChoice, roster,
                [ResolutionVote(unverifiedChoice, 0), ResolutionVote(unverifiedChoice, 1)],
                selectedVersion: losingVersion)),
                "a signed save outside the selected parent's lineage was accepted");
            var duplicateProposal = ResolutionProposal(roster,
                [accepted.RecordHash, accepted.RecordHash]);
            Require(!WorldAuthorityTrust.VerifyProposal(duplicateProposal, roster),
                "duplicate named heads were accepted");
            var ownerProposal = ResolutionProposal(roster, hashes, "ResolutionOwnerOverride");
            var ownerSignature = Convert.ToBase64String(ownerKey.SignData(
                WorldAuthorityTrust.OwnerBasis(ownerProposal), HashAlgorithmName.SHA256));
            Require(WorldAuthorityTrust.Verify(ResolutionRecord(ownerProposal, roster, [],
                ownerSignature)), "owner override of exact heads was rejected");
            var disabledResolutionProposal = ResolutionProposal(noOverrideRoster, hashes,
                "ResolutionOwnerOverride");
            Require(!WorldAuthorityTrust.Verify(ResolutionRecord(disabledResolutionProposal,
                noOverrideRoster, [], Convert.ToBase64String(ownerKey.SignData(
                    WorldAuthorityTrust.OwnerBasis(disabledResolutionProposal), HashAlgorithmName.SHA256)))),
                "disabled resolution owner override was accepted");
            var receivedRoot = Path.Combine(resolutionData.RootPath, "verified-candidate-copy");
            var receivedPayload = Path.Combine(receivedRoot, version.VersionHash,
                SharedWorldService.PayloadDirectory);
            Directory.CreateDirectory(receivedPayload);
            foreach (var file in version.Files)
            {
                var destination = SharedWorldService.SafeChild(receivedPayload, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(SharedWorldService.SafeChild(profile.WorldDirectory, file.Path),
                    destination);
            }
            File.WriteAllBytes(Path.Combine(receivedRoot, "latest.json"),
                JsonSerializer.SerializeToUtf8Bytes(version, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            void StoreVerifiedPayload(SharedWorldVersion manifest)
            {
                var branchRoot = Path.Combine(receivedRoot, manifest.VersionHash);
                var payloadRoot = Path.Combine(branchRoot, SharedWorldService.PayloadDirectory);
                Directory.CreateDirectory(payloadRoot);
                foreach (var file in manifest.Files)
                {
                    var destination = SharedWorldService.SafeChild(payloadRoot, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(SharedWorldService.SafeChild(profile.WorldDirectory, file.Path),
                        destination, true);
                }
                File.WriteAllBytes(Path.Combine(branchRoot, "version.json"),
                    JsonSerializer.SerializeToUtf8Bytes(manifest,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            StoreVerifiedPayload(version);
            StoreVerifiedPayload(losingVersion);
            var later = version;
            for (var i = 0; i < 4; i++)
            {
                later = SharedWorldService.SignVersion(later with
                {
                    Number = later.Number + 1,
                    ParentHash = later.VersionHash,
                    BackupId = Guid.NewGuid()
                }, ownerKey);
                StoreVerifiedPayload(later);
            }
            FriendLink.PruneReceived(receivedRoot, later.VersionHash,
                new HashSet<string>([version.VersionHash, losingVersion.VersionHash]));
            Require(Directory.Exists(Path.Combine(receivedRoot, losingVersion.VersionHash)) &&
                Directory.Exists(Path.Combine(receivedRoot, version.VersionHash)),
                "resolution retention pruned a losing verified payload");
            resolutionData.SaveProtected($"shared-world-pc-signing-{voters[0].Id:N}.protected",
                voters[0].Key.ExportPkcs8PrivateKey());
            var floor = new SharedRosterFloor(roster.GroupId, roster.Epoch,
                roster.Revision, roster.Signature);
            RequireThrows<InvalidDataException>(() => SharedWorldElection.PrepareResolutionOffer(
                receivedRoot, roster,
                new SharedRosterFloor(noOverrideRoster.GroupId, noOverrideRoster.Epoch,
                    noOverrideRoster.Revision, noOverrideRoster.Signature), voters[0].Id,
                voters[0].Key, "https://127.0.0.1:5132", new string('A', 64),
                resolution, accepted.RecordHash), "stale signed roster armed a resolution");
            var offered = SharedWorldElection.PrepareResolutionOffer(receivedRoot, roster, floor,
                voters[0].Id, voters[0].Key, "https://127.0.0.1:5132", new string('A', 64),
                resolution, accepted.RecordHash);
            var inbox = new SharedWorldVoteInbox(resolutionData);
            inbox.Arm(offered, receivedRoot);
            var signed0 = resolution.SignLocalVote(profile.Id, offered.Proposal, roster,
                voters[0].Id, voters[0].Key);
            var signed1 = resolution.SignLocalVote(profile.Id, offered.Proposal, roster,
                voters[1].Id, voters[1].Key);
            Require(inbox.AcceptVote(profile.Id,
                WorldAuthorityTrust.ProposalHash(offered.Proposal), signed0).Code == "VoteRecorded",
                "candidate did not retain the first signed vote");
            var majority = inbox.AcceptVote(profile.Id,
                WorldAuthorityTrust.ProposalHash(offered.Proposal), signed1);
            Require(majority is { Ok: true, Decision: not null } &&
                majority.Decision.Schema == 2 && WorldAuthorityTrust.Verify(majority.Decision),
                "candidate did not append a valid exact-head majority decision");
            Require(inbox.AcceptVote(profile.Id,
                WorldAuthorityTrust.ProposalHash(offered.Proposal), signed1).Decision?.RecordHash ==
                majority.Decision!.RecordHash &&
                inbox.OfferForTransport(profile.Id,
                    WorldAuthorityTrust.ProposalHash(offered.Proposal)) is not null,
                "completed decision could not be retried by a voter");
            resolution.Append(majority.Decision!);
            Require(resolution.Read(profile.Id).Count == 3 &&
                resolution.ReadUniqueHead(profile.Id)?.RecordHash == majority.Decision!.RecordHash &&
                WorldAuthorityTrust.EffectiveHeads(resolution.Read(profile.Id)).Length == 1,
                "resolution did not retire exactly the competing leaves or retry idempotently");
            using (var oldHostData = Data("resolved-old-host-status"))
            {
                oldHostData.SaveSettings(Settings(profile));
                var oldHostAuthority = new WorldAuthorityStore(oldHostData);
                oldHostAuthority.Append(accepted);
                oldHostAuthority.Append(losingRecord);
                oldHostAuthority.Append(majority.Decision!);
                var oldHostManager = new HostManager(oldHostData, Games(oldHostData));
                var oldHostStatus = (await oldHostManager.SharedWorldStatusAsync(profile.Id)).Authority;
                Require(oldHostStatus is { State: "OldHostFenced", Head: not null } &&
                    oldHostStatus.Head.RecordHash == majority.Decision.RecordHash &&
                    oldHostStatus.CompetingHeads is null &&
                    oldHostAuthority.Read(profile.Id).Any(item =>
                        item.RecordHash == losingRecord.RecordHash),
                    "old Host status did not use resolved effective head while preserving losing history");
            }
            using (var hostingSigner = ECDsa.Create())
            {
                hostingSigner.ImportPkcs8PrivateKey(resolutionData.LoadProtected(
                    WorldAuthorityStore.HostingKeyName(profile.Id))!, out _);
                var afterDecision = SharedWorldService.SignVersion(version with
                {
                    Number = version.Number + 1,
                    ParentHash = version.VersionHash,
                    BackupId = Guid.NewGuid()
                }, hostingSigner);
                Require(FriendLink.AuthorizedVersionSignerForRecords(roster.OwnerPublicKey,
                        afterDecision, resolution.Read(profile.Id)) &&
                    FriendLink.VerifySharedChain(version, afterDecision, [],
                        resolution.Read(profile.Id)) &&
                    !FriendLink.VerifySharedChain(losingVersion, afterDecision, [],
                        resolution.Read(profile.Id)),
                    "Friend receive did not follow only the selected signed branch");
            }
            Require(resolution.Read(profile.Id).Any(item => item.RecordHash == losingRecord.RecordHash),
                "losing authority record was removed");
            using (var selectedLoserData = Data("selected-losing-branch"))
            {
                var selectedLoser = new WorldAuthorityStore(selectedLoserData);
                selectedLoser.Append(accepted);
                selectedLoser.Append(losingRecord);
                var selectLoser = ResolutionProposal(roster, hashes,
                    selected: losingRecord);
                selectedLoser.Append(ResolutionRecord(selectLoser, roster,
                    [ResolutionVote(selectLoser, 0), ResolutionVote(selectLoser, 1)],
                    selectedVersion: losingVersion));
                Require(selectedLoser.FindProvenVersion(profile.Id, losingVersion.Number)?.VersionHash ==
                    losingVersion.VersionHash,
                    "authority pages returned the losing same-number version after selection");
            }
            using (var lineageData = Data("selected-lineage-after-child"))
            {
                var lineageStore = new WorldAuthorityStore(lineageData);
                lineageStore.Append(accepted);
                var lineageHostingKey = lineageStore.PrepareLocalHostingKey(profile.Id);
                var a = new List<SharedWorldVersion>();
                var b = new List<SharedWorldVersion>();
                var aPrior = version;
                var bPrior = version;
                for (var number = 2; number <= 6; number++)
                {
                    if (number <= 5)
                    {
                        aPrior = SharedWorldService.SignVersion(aPrior with
                        { Number = number, ParentHash = aPrior.VersionHash, BackupId = Guid.NewGuid() },
                            voters[0].Key);
                        a.Add(aPrior);
                    }
                    bPrior = SharedWorldService.SignVersion(bPrior with
                    { Number = number, ParentHash = bPrior.VersionHash, BackupId = Guid.NewGuid() },
                        voters[0].Key);
                    b.Add(bPrior);
                }
                var aFive = a[^1];
                var bFive = b[^2];
                var bSix = b[^1];
                WorldAuthorityRecord LineageRecord(WorldAuthorityRecord parent,
                    SharedWorldVersion head, IReadOnlyList<SharedWorldVersion>? proof,
                    int schema, string kind, IReadOnlyList<string>? competingHeads = null)
                {
                    var draft = new WorldAuthorityProposal(schema, roster.GroupId, profile.Id,
                        parent.Proposal.Epoch + 1, parent.RecordHash,
                        WorldAuthorityTrust.RosterHash(roster), head.VersionHash, lineageHostingKey,
                        "https://127.0.0.1:5132", kind, voters[0].Id, selectedDevice, "",
                        CompetingHeadHashes: competingHeads);
                    var bindingDraft = new WorldSuccessorBinding(voters[0].Id, selectedDevice,
                        lineageHostingKey, "");
                    var binding = bindingDraft with
                    {
                        Signature = Convert.ToBase64String(
                        voters[0].Key.SignData(WorldAuthorityTrust.BindingBasis(draft, bindingDraft),
                            HashAlgorithmName.SHA256))
                    };
                    draft = draft with { SuccessorBinding = binding };
                    var proposal = draft with
                    {
                        Signature = Convert.ToBase64String(
                        voters[0].Key.SignData(WorldAuthorityTrust.ProposalBasis(draft),
                            HashAlgorithmName.SHA256))
                    };
                    var recordDraft = new WorldAuthorityRecord(schema == 3 ? 2 : 1,
                        proposal, roster, head,
                        [ResolutionVote(proposal, 0), ResolutionVote(proposal, 1)], null, "",
                        VersionLineage: proof);
                    return recordDraft with
                    {
                        RecordHash = WorldAuthorityTrust.Hash(
                        WorldAuthorityTrust.RecordBasis(recordDraft))
                    };
                }
                var aRecord = LineageRecord(accepted, aFive, a, 2, "Quorum");
                var bRecord = LineageRecord(accepted, bSix, b, 2, "Quorum");
                lineageStore.Append(aRecord);
                lineageStore.Append(bRecord);
                var competingLineageHeads = new[] { aRecord.RecordHash, bRecord.RecordHash }
                    .Order(StringComparer.Ordinal).ToArray();
                var decision = LineageRecord(aRecord, aFive, null, 3,
                    "ResolutionQuorum", competingLineageHeads);
                lineageStore.Append(decision);
                Require(lineageStore.FindProvenVersion(profile.Id, 5)?.VersionHash == aFive.VersionHash &&
                    lineageStore.FindProvenVersion(profile.Id, 6) is null &&
                    lineageStore.Read(profile.Id).Any(record => record.RecordHash == bRecord.RecordHash),
                    "resolution served the retired B6 or discarded its signed evidence");
                lineageData.SaveProtected($"shared-world-pc-signing-{voters[0].Id:N}.protected",
                    voters[0].Key.ExportPkcs8PrivateKey());
                lineageStore.BindLocalSuccessor(profile.Id, decision.RecordHash, voters[0].Id);
                using var lineageSigner = ECDsa.Create();
                lineageSigner.ImportPkcs8PrivateKey(lineageData.LoadProtected(
                    WorldAuthorityStore.HostingKeyName(profile.Id))!, out _);
                var aSix = SharedWorldService.SignVersion(aFive with
                { Number = 6, ParentHash = aFive.VersionHash, BackupId = Guid.NewGuid() },
                    lineageSigner);
                var aSeven = SharedWorldService.SignVersion(aSix with
                { Number = 7, ParentHash = aSix.VersionHash, BackupId = Guid.NewGuid() },
                    lineageSigner);
                var sharedRoot = Path.Combine(lineageData.RootPath, "shared-worlds", profile.Id.ToString("N"));
                var sixRoot = Path.Combine(sharedRoot, version.GroupId.ToString("N"), "6");
                Directory.CreateDirectory(sixRoot);
                File.WriteAllBytes(Path.Combine(sharedRoot, "latest.json"),
                    JsonSerializer.SerializeToUtf8Bytes(aSeven));
                File.WriteAllBytes(Path.Combine(sixRoot, "version.json"),
                    JsonSerializer.SerializeToUtf8Bytes(bSix));
                var lineageShares = new SharedWorldService(lineageData,
                    new WorldBackupService(lineageData, TimeProvider.System));
                RequireThrows<InvalidDataException>(() => lineageShares.ReadEarlierVersion(aSeven, 6),
                    "transfer served retained B6 as the selected A6");
                File.WriteAllBytes(Path.Combine(sixRoot, "version.json"),
                    JsonSerializer.SerializeToUtf8Bytes(aSix));
                Require(lineageShares.ReadEarlierVersion(aSeven, 6).VersionHash == aSix.VersionHash &&
                    lineageShares.ReadEarlierVersion(aSeven, 5).VersionHash == aFive.VersionHash,
                    "transfer could not serve A5/A6 to a catching-up Friend");
                var child = LineageRecord(decision, aSix, [aSix], 2, "Quorum");
                lineageStore.Append(child);
                lineageStore.BindLocalSuccessor(profile.Id, child.RecordHash, voters[0].Id);
                var restartedRecords = new WorldAuthorityStore(lineageData).Read(profile.Id);
                var persistedFriend = JsonSerializer.Deserialize<FriendConfiguration>(
                    JsonSerializer.Serialize(new FriendConfiguration
                    {
                        SharedWorldSigningKeys = new() { [profile.Id] = roster.OwnerPublicKey }
                    }))!;
                var anchor = FriendLink.ResolvedAuthorityAnchorForRecords(
                    persistedFriend.SharedWorldSigningKeys[profile.Id], aSeven, restartedRecords);
                Require(anchor?.VersionHash == aFive.VersionHash &&
                    FriendLink.VerifySharedChain(anchor, aSeven, [aSix], restartedRecords) &&
                    !FriendLink.VerifySharedChain(bFive, aSeven, [aSix], restartedRecords) &&
                    !FriendLink.VerifySharedChain(bSix, aSeven, [], restartedRecords) &&
                    lineageStore.FindProvenVersion(profile.Id, 6)?.VersionHash == aSix.VersionHash &&
                    lineageShares.ReadEarlierVersion(aSeven, 6).VersionHash == aSix.VersionHash,
                    "restarted Friend did not recover the chosen A5 after an authority child");
                var forgedSeven = SharedWorldService.SignVersion(aSeven with
                { ParentHash = bSix.VersionHash, BackupId = Guid.NewGuid() }, lineageSigner);
                Require(!FriendLink.VerifySharedChain(anchor!, forgedSeven, [aSix], restartedRecords),
                    "a forked descendant passed the selected anchor");
                var authorityLog = Path.Combine(sharedRoot, "authority", "records.jsonl");
                var originalLog = File.ReadAllBytes(authorityLog);
                File.AppendAllText(authorityLog, "tampered\n");
                RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(lineageData)
                    .Read(profile.Id), "restart accepted a tampered authority child");
                File.WriteAllBytes(authorityLog, originalLog);
            }
            using (var ownerData = Data("authority-owner-resolution"))
            {
                var ownerStore = new WorldAuthorityStore(ownerData);
                ownerStore.Append(accepted);
                ownerStore.Append(losingRecord);
                ownerData.SaveProtected($"shared-world-pc-signing-{voters[0].Id:N}.protected",
                    voters[0].Key.ExportPkcs8PrivateKey());
                RequireThrows<InvalidDataException>(() => SharedWorldElection.PrepareResolutionOffer(
                    receivedRoot, noOverrideRoster,
                    new SharedRosterFloor(noOverrideRoster.GroupId, noOverrideRoster.Epoch,
                        noOverrideRoster.Revision, noOverrideRoster.Signature), voters[0].Id,
                    voters[0].Key, "https://127.0.0.1:5132", new string('A', 64),
                    ownerStore, accepted.RecordHash, ownerOverride: true),
                    "disabled owner override produced an offer");
                var ownerOffer = SharedWorldElection.PrepareResolutionOffer(receivedRoot,
                    roster, floor, voters[0].Id, voters[0].Key, "https://127.0.0.1:5132",
                    new string('A', 64), ownerStore, accepted.RecordHash, ownerOverride: true);
                ownerStore.StageResolutionOffer(ownerOffer);
                Require(ownerStore.PendingResolutionOffers(profile.Id).Count == 1,
                    "owner offer did not persist for review");
                var ownerInbox = new SharedWorldVoteInbox(ownerData);
                ownerInbox.Arm(ownerOffer, receivedRoot);
                var approval = new WorldAuthorityOwnerApproval(
                    WorldAuthorityTrust.ProposalHash(ownerOffer.Proposal),
                    Convert.ToBase64String(ownerKey.SignData(
                        WorldAuthorityTrust.OwnerBasis(ownerOffer.Proposal), HashAlgorithmName.SHA256)));
                var ownerResult = ownerInbox.AcceptOwnerApproval(profile.Id, approval);
                Require(ownerResult is { Ok: true, Decision: not null } &&
                    ownerStore.ReadUniqueHead(profile.Id)?.RecordHash == ownerResult.Decision.RecordHash &&
                    ownerInbox.AcceptOwnerApproval(profile.Id, approval).Decision?.RecordHash ==
                    ownerResult.Decision.RecordHash,
                    "owner override was not durable and retryable");
            }
            using (var oldHostData = Data("returned-resolution-host"))
            {
                oldHostData.SaveSettings(Settings(profile));
                var oldHostAuthority = new WorldAuthorityStore(oldHostData);
                oldHostAuthority.AppendReceived(accepted, profile.Id, roster.GroupId,
                    roster.OwnerPublicKey);
                oldHostAuthority.AppendReceived(losingRecord, profile.Id, roster.GroupId,
                    roster.OwnerPublicKey);
                Require(oldHostAuthority.ReadUniqueHead(profile.Id) is null,
                    "old Host selected a branch before receiving a decision");
                oldHostAuthority.AppendReceived(majority.Decision!, profile.Id, roster.GroupId,
                    roster.OwnerPublicKey);
                Require(oldHostAuthority.Fenced(profile.Id, roster.OwnerPublicKey, out _) &&
                    oldHostAuthority.ReadUniqueHead(profile.Id)?.RecordHash ==
                    majority.Decision!.RecordHash,
                    "old Host did not fence after verifying the returned decision");
                var oldHostManager = Manager(oldHostData);
                var blockedStart = await oldHostManager.StartAsync(profile.Id);
                var blockedSharing = await oldHostManager.SetSharedSavesAsync(profile.Id, true);
                Require(blockedStart.Code == "SharedWorldAuthorityBlocked" &&
                    blockedSharing.Code == "SharedWorldAuthorityBlocked",
                    "old Host accepted Start or sharing after the signed decision: " +
                    blockedStart.Code + ", " + blockedSharing.Code);
            }
        }
        var successorVersion = SharedWorldService.SignVersion(version with
        {
            Number = version.Number + 1,
            ParentHash = version.VersionHash,
            BackupId = Guid.NewGuid(),
            CreatedUtc = version.CreatedUtc.AddSeconds(1)
        }, voters[0].Key);
        var nextCandidate = Convert.ToBase64String(voters[1].Key.ExportSubjectPublicKeyInfo());
        var nextDraft = proposal with
        {
            Epoch = 2,
            ParentAuthorityHash = accepted.RecordHash,
            RosterHash = WorldAuthorityTrust.RosterHash(noOverrideRoster),
            VersionHash = successorVersion.VersionHash,
            CandidatePublicKey = nextCandidate,
            ProposerDeviceId = voters[1].Id,
            ProposerPublicKey = nextCandidate,
            Signature = ""
        };
        var nextProposal = nextDraft with
        {
            Signature = Convert.ToBase64String(voters[1].Key.SignData(
            WorldAuthorityTrust.ProposalBasis(nextDraft), HashAlgorithmName.SHA256))
        };
        WorldAuthorityVote NextVote(int index)
        {
            var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(nextProposal),
                voters[index].Id, Convert.ToBase64String(voters[index].Key.ExportSubjectPublicKeyInfo()), "");
            return draft with
            {
                Signature = Convert.ToBase64String(voters[index].Key.SignData(
                WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256))
            };
        }
        var nextAuthority = new WorldAuthorityRecord(1, nextProposal, noOverrideRoster,
            successorVersion, [NextVote(1), NextVote(2)], null, "",
            VersionLineage: [successorVersion]);
        nextAuthority = nextAuthority with
        {
            RecordHash = WorldAuthorityTrust.Hash(
            WorldAuthorityTrust.RecordBasis(nextAuthority))
        };
        Require(WorldAuthorityTrust.Verify(nextAuthority) &&
            WorldAuthorityTrust.VerifyLineage(nextAuthority, accepted),
            "signed successor save lineage was rejected");
        using (var linearData = Data("superseded-route-head"))
        {
            var linear = new WorldAuthorityStore(linearData);
            linear.Append(accepted);
            Require(linear.ReadUniqueHead(profile.Id)?.RecordHash == accepted.RecordHash,
                "current signed handoff was not selected as the sole authority head");
            linear.Append(nextAuthority);
            Require(linear.ReadUniqueHead(profile.Id)?.RecordHash == nextAuthority.RecordHash &&
                linear.ReadUniqueHead(profile.Id)?.RecordHash != accepted.RecordHash,
                "superseded signed handoff remained eligible for route proof");
        }
        var wrongSignerVersion = SharedWorldService.SignVersion(successorVersion, voters[1].Key);
        Require(!WorldAuthorityTrust.VerifyLineage(nextAuthority with
        { Version = wrongSignerVersion, VersionLineage = [wrongSignerVersion] }, accepted),
            "a different PC signed a successor save without authority");
        var unproven = nextAuthority with { VersionLineage = null, RecordHash = "" };
        unproven = unproven with
        {
            RecordHash = WorldAuthorityTrust.Hash(
            WorldAuthorityTrust.RecordBasis(unproven))
        };
        RequireThrows<InvalidDataException>(() => store.Append(unproven),
            "successor save without signed lineage was accepted");
        store.Append(nextAuthority, stopAfterJournalForChecks: true,
            enforceCurrentGrants: false);
        Require(new WorldAuthorityStore(data).Read(profile.Id).Count == 4 &&
            new WorldAuthorityStore(data).ReadUniqueHead(profile.Id) is null &&
            !File.Exists(Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
                "authority", "append.pending")),
            "a journal-only crash did not finish the signed successor authority");
        var competingStatus = await new HostManager(data, Games(data)).SharedWorldStatusAsync(profile.Id);
        Require(competingStatus.Authority is
        { State: "CompetingHistories", Head: null, CompetingHeads.Count: 3 },
            "local status silently chose one competing authority head");
        var longLineage = new List<SharedWorldVersion>();
        var predecessor = successorVersion;
        for (var index = 0; index < 71; index++)
        {
            predecessor = SharedWorldService.SignVersion(predecessor with
            {
                Number = predecessor.Number + 1,
                ParentHash = predecessor.VersionHash,
                BackupId = Guid.NewGuid(),
                CreatedUtc = predecessor.CreatedUtc.AddSeconds(1)
            }, voters[1].Key);
            longLineage.Add(predecessor);
        }
        var thirdDraft = nextProposal with
        {
            Epoch = 3,
            ParentAuthorityHash = nextAuthority.RecordHash,
            VersionHash = predecessor.VersionHash,
            CandidatePublicKey = Convert.ToBase64String(voters[2].Key.ExportSubjectPublicKeyInfo()),
            ProposerDeviceId = voters[2].Id,
            ProposerPublicKey = Convert.ToBase64String(voters[2].Key.ExportSubjectPublicKeyInfo()),
            Signature = ""
        };
        var thirdProposal = thirdDraft with
        {
            Signature = Convert.ToBase64String(voters[2].Key.SignData(
            WorldAuthorityTrust.ProposalBasis(thirdDraft), HashAlgorithmName.SHA256))
        };
        WorldAuthorityVote ThirdVote(int index)
        {
            var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(thirdProposal),
                voters[index].Id, Convert.ToBase64String(voters[index].Key.ExportSubjectPublicKeyInfo()), "");
            return draft with
            {
                Signature = Convert.ToBase64String(voters[index].Key.SignData(
                WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256))
            };
        }
        var third = new WorldAuthorityRecord(1, thirdProposal, noOverrideRoster,
            predecessor, [ThirdVote(1), ThirdVote(2)], null, "", null,
            VersionLineageDigest: WorldAuthorityTrust.LineageDigest(longLineage));
        third = third with { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(third)) };
        Require(WorldAuthorityTrust.Verify(third) &&
            WorldAuthorityTrust.VerifyLineage(third, nextAuthority, longLineage) &&
            JsonSerializer.SerializeToUtf8Bytes(third).Length < 400_000,
            "a second handoff after more than 64 saves failed its chained proof");
        Require(!WorldAuthorityTrust.VerifyLineage(third, nextAuthority, longLineage.Take(69).ToArray()) &&
            !WorldAuthorityTrust.VerifyLineage(third, nextAuthority,
                longLineage.Select((item, index) => index == 35 ? item with { ParentHash = "BAD" } : item).ToArray()),
            "missing or tampered handoff proof was accepted");
        Require(WorldAuthorityStore.FirstProofNumber(third with
        {
            Version = third.Version with
            { Number = nextAuthority.Version.Number + 4097 }
        }, nextAuthority) ==
            nextAuthority.Version.Number + 1,
            "a handoff after more than 4096 saves was rejected");
        RequireThrows<InvalidDataException>(() => store.Append(third),
            "second handoff without proof was accepted");
        var linkedProofRoot = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
            "authority", "proof-" + third.RecordHash);
        var outsideProof = Path.Combine(root, "outside-authority-proof");
        Directory.CreateDirectory(outsideProof);
        CreateJunction(linkedProofRoot, outsideProof);
        try
        {
            RequireThrows<InvalidDataException>(() => store.Append(third,
                enforceCurrentGrants: false, externalLineage: longLineage),
                "a linked authority proof root accepted writes");
            Require(!Directory.EnumerateFileSystemEntries(outsideProof).Any(),
                "authority proof wrote through a junction outside local data");
        }
        finally { Directory.Delete(linkedProofRoot); }
        Task<SharedWorldVersion?> FetchProof(long number, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(longLineage.FirstOrDefault(item => item.Number == number));
        }
        var firstProofBatch = await FriendLink.StageAuthorityProofBatchAsync(store, third,
            nextAuthority, 10, FetchProof, CancellationToken.None);
        Require(firstProofBatch == (false, 10),
            "authority proof was not bounded to the first continuation batch");
        using (var stoppedProof = new CancellationTokenSource())
        {
            stoppedProof.Cancel();
            RequireThrows<OperationCanceledException>(() => FriendLink.StageAuthorityProofBatchAsync(
                store, third, nextAuthority, 128, FetchProof, stoppedProof.Token).GetAwaiter().GetResult(),
                "cancelled proof work continued writing");
        }
        Require(store.Read(profile.Id).Count == 4 &&
            store.ReadStagedProofVersion(third, longLineage[10].Number) is null,
            "interrupted proof partially accepted authority");
        var tamperedProofPiece = SharedWorldService.SignVersion(longLineage[10] with
        { ParentHash = "BAD" }, voters[1].Key);
        RequireThrows<InvalidDataException>(() => FriendLink.StageAuthorityProofBatchAsync(
            store, third, nextAuthority, 128,
            (number, token) => Task.FromResult<SharedWorldVersion?>(
                number == tamperedProofPiece.Number ? tamperedProofPiece :
                longLineage.FirstOrDefault(item => item.Number == number)),
            CancellationToken.None).GetAwaiter().GetResult(),
            "a signed but disconnected continuation piece was staged");
        Require(store.ReadStagedProofVersion(third, longLineage[10].Number) is null,
            "a disconnected proof piece was written before ancestry verification");
        var resumedProofBatch = await FriendLink.StageAuthorityProofBatchAsync(store, third,
            nextAuthority, 128, FetchProof, CancellationToken.None);
        Require(resumedProofBatch == (true, 61),
            "authority proof did not resume from checked pieces after 64 saves");
        store.AppendReceived(third, profile.Id, version.GroupId,
            noOverrideRoster.OwnerPublicKey, store.ReadStagedProof(third, nextAuthority));
        store.ClearStagedProof(third, nextAuthority);
        Require(new WorldAuthorityStore(data).Read(profile.Id).Any(item => item.RecordHash == third.RecordHash),
            "resumed proof was not verified and durable after restart");
        var reviewManager = new HostManager(data, Games(data));
        Require((await reviewManager.SharedWorldStatusAsync(profile.Id)).Authority?.State ==
            "CompetingHistories" &&
            (await reviewManager.SharedWorldReviewProofAsync(profile.Id, third.RecordHash,
                longLineage[35].Number))?.VersionHash == longLineage[35].VersionHash &&
            await reviewManager.SharedWorldReviewProofAsync(profile.Id, third.RecordHash,
                nextAuthority.Version.Number) is null &&
            (await reviewManager.SharedWorldReadAsync(profile.Id)).Status.Latest is null,
            "fenced Host did not isolate signed proof review from ordinary save sharing");
        var indexedReview = new WorldAuthorityStore(data);
        foreach (var piece in longLineage)
        {
            var indexed = indexedReview.ReadReviewProofIndex(profile.Id, third.RecordHash,
                piece.Number);
            Require(indexed is { } expected && expected.ExpectedHash == piece.VersionHash &&
                indexedReview.ReadReviewProofVersion(expected.Record, piece.Number)?.VersionHash ==
                    piece.VersionHash,
                "a long signed review proof index returned the wrong piece");
        }
        Require(indexedReview.ReviewFullValidationCount == 1 &&
            indexedReview.ReviewProofIndexBuildCount == 1 &&
            indexedReview.ReviewProofPieceReadCount <= longLineage.Count * 3,
            "reviewing more than 70 proof pieces repeated the full lineage scan");
        var reviewProofPath = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
            "authority", "proof-" + third.RecordHash, longLineage[35].Number + ".json");
        var originalReviewProof = File.ReadAllBytes(reviewProofPath);
        try
        {
            File.WriteAllBytes(reviewProofPath, new byte[SharedWorldService.MaximumManifestBytes + 1]);
            RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(data)
                .ReadReviewProofVersion(third, longLineage[35].Number),
                "oversized review proof was served");
            File.WriteAllText(reviewProofPath, "{tampered");
            RequireThrows<JsonException>(() => new WorldAuthorityStore(data)
                .ReadReviewProofVersion(third, longLineage[35].Number),
                "tampered review proof was served");
        }
        finally { File.WriteAllBytes(reviewProofPath, originalReviewProof); }
        try
        {
            var replaced = SharedWorldService.SignVersion(longLineage[35] with
            { BackupId = Guid.NewGuid() }, voters[1].Key);
            File.WriteAllBytes(reviewProofPath, JsonSerializer.SerializeToUtf8Bytes(replaced,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            RequireThrows<InvalidDataException>(() => reviewManager.SharedWorldReviewProofAsync(
                    profile.Id, third.RecordHash, replaced.Number).GetAwaiter().GetResult(),
                "a signed replacement proof piece bypassed the verified review index");
        }
        finally { File.WriteAllBytes(reviewProofPath, originalReviewProof); }
        using (var reviewPortReservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reviewPortReservation.Start();
            var reviewPort = ((IPEndPoint)reviewPortReservation.LocalEndpoint).Port;
            reviewPortReservation.Stop();
            var reviewAddress = $"https://127.0.0.1:{reviewPort}";
            using var reviewCertificate = new HostIdentity(data).Ensure(reviewAddress);
            var reviewSettings = data.LoadSettings();
            reviewSettings.CompanionBindAddress = "127.0.0.1";
            reviewSettings.CompanionEndpoint = reviewAddress;
            reviewSettings.CompanionPort = reviewPort;
            reviewSettings.CompanionListeningEnabled = true;
            data.SaveSettings(reviewSettings);
            var reviewBearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            data.SavePairingState(new PairingPersistentState
            {
                Devices = [new PairedDevice
            {
                Id = voters[2].Id, AssignedProfileIds = [profile.Id],
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reviewBearer))),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                SharedWorldGrants = new() { [profile.Id] = new(RecoveryVoter: true) },
                SharedWorldPublicKey = Convert.ToBase64String(voters[2].Key.ExportSubjectPublicKeyInfo())
            }]
            });
            var dirtyPath = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
                "roster-dirty");
            if (File.Exists(dirtyPath)) File.Delete(dirtyPath);
            using var reviewGate = new SemaphoreSlim(1, 1);
            var liveReviewManager = new HostManager(data, Games(data));
            var reviewListener = new CompanionServer(data, liveReviewManager, new PairingService(data),
                Games(data), new ServerLogService(data, liveReviewManager), reviewGate, reviewPort + 2);
            try
            {
                await reviewListener.SyncAsync();
                Require(reviewListener.ListenerState == CompanionListenerStates.Listening,
                    "fenced review fixture listener did not open");
                using var voterData = Data("digest-review-voter");
                voterData.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(
                    new FriendConfiguration
                    {
                        Endpoint = reviewAddress,
                        Fingerprint = HostIdentity.Fingerprint(reviewCertificate),
                        DeviceId = voters[2].Id,
                        Credential = reviewBearer,
                        CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                        ApprovedSharedWorldGroups = new() { [profile.Id] = roster.GroupId },
                        SharedWorldSigningKeys = new() { [profile.Id] = roster.OwnerPublicKey },
                        SharedRosterFloors = new()
                        {
                            [profile.Id] = new(roster.GroupId,
                            noOverrideRoster.Epoch, noOverrideRoster.Revision,
                            noOverrideRoster.Signature)
                        }
                    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                voterData.SaveProtected($"shared-world-pc-signing-{voters[2].Id:N}.protected",
                    voters[2].Key.ExportPkcs8PrivateKey());
                using var voterLink = new FriendLink(voterData, "friend.protected");
                WorldHistoryReviewResult reviewResult = new(false, "Pending", "");
                for (var attempt = 0; attempt < 4 && !reviewResult.Ok; attempt++)
                    reviewResult = await voterLink.ReviewSharedHistoryAsync(profile.Id);
                Require(reviewResult.Ok && reviewResult.CompetingHeads >= 2 &&
                    new WorldAuthorityStore(voterData).Read(profile.Id)
                        .Any(item => item.RecordHash == third.RecordHash),
                    "voter-only Friend did not verify the digest-backed lineage under fencing: " +
                    reviewResult.Code);
            }
            finally { await reviewListener.StopAsync(); }
        }
        using (var longProofData = Data("authority-proof-over-4096"))
        {
            var longStore = new WorldAuthorityStore(longProofData);
            longStore.AppendReceived(accepted, profile.Id, version.GroupId, roster.OwnerPublicKey);
            var proofVersions = new List<SharedWorldVersion>(4097);
            var proofHead = version;
            for (var index = 0; index < 4097; index++)
            {
                proofHead = SharedWorldService.SignVersion(proofHead with
                {
                    Number = proofHead.Number + 1,
                    ParentHash = proofHead.VersionHash,
                    BackupId = Guid.NewGuid()
                }, voters[0].Key);
                proofVersions.Add(proofHead);
            }
            var longDraft = proposal with
            {
                Epoch = 2,
                ParentAuthorityHash = accepted.RecordHash,
                RosterHash = WorldAuthorityTrust.RosterHash(noOverrideRoster),
                VersionHash = proofHead.VersionHash,
                CandidatePublicKey = Convert.ToBase64String(voters[1].Key.ExportSubjectPublicKeyInfo()),
                Signature = ""
            };
            var longProposal = SignProposal(longDraft, voters[0].Key);
            var longRecordDraft = new WorldAuthorityRecord(1, longProposal, noOverrideRoster,
                proofHead, [SignVote(longProposal, 0), SignVote(longProposal, 1)], null, "", null,
                null, WorldAuthorityTrust.LineageDigest(proofVersions));
            var longRecord = longRecordDraft with
            { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(longRecordDraft)) };
            Task<SharedWorldVersion?> FetchLongProof(long number, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<SharedWorldVersion?>(proofVersions[(int)(number - version.Number - 1)]);
            }
            var boundedFirst = await FriendLink.StageAuthorityProofBatchAsync(longStore, longRecord,
                accepted, WorldAuthorityTrust.ProofVersionsPerCheck, FetchLongProof,
                CancellationToken.None);
            Require(!boundedFirst.Complete && boundedFirst.Used == WorldAuthorityTrust.ProofVersionsPerCheck &&
                longStore.Read(profile.Id).Count == 1,
                "a 4097-save handoff partially accepted authority or exceeded one proof batch");
            var resumedStore = new WorldAuthorityStore(longProofData);
            var proofBatches = 1;
            (bool Complete, int Used) proofBatch;
            do
            {
                proofBatch = await FriendLink.StageAuthorityProofBatchAsync(resumedStore, longRecord,
                    accepted, WorldAuthorityTrust.ProofVersionsPerCheck, FetchLongProof,
                    CancellationToken.None);
                Require(proofBatch.Used <= WorldAuthorityTrust.ProofVersionsPerCheck && ++proofBatches <= 33,
                    "long authority proof exceeded the per-check bound");
            } while (!proofBatch.Complete);
            RequireThrows<InvalidDataException>(() => resumedStore.AppendReceivedStaged(
                longRecord, accepted, profile.Id, version.GroupId, roster.OwnerPublicKey),
                "authority accepted a staged proof before the bounded final scan");
            var missingBoundary = Path.Combine(longProofData.RootPath, "shared-worlds",
                profile.Id.ToString("N"), "authority", "proof-stage-" + longRecord.RecordHash,
                proofVersions[0].Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
            File.Delete(missingBoundary);
            RequireThrows<InvalidDataException>(() => FriendLink.VerifyStagedAuthorityProofBatch(
                resumedStore, longRecord, accepted, WorldAuthorityTrust.ProofVersionsPerCheck,
                CancellationToken.None),
                "a missing staged boundary was accepted before committing authority");
            Require(resumedStore.Read(profile.Id).Count == 1,
                "a missing boundary partially accepted authority");
            resumedStore.StageReceivedProofVersion(longRecord, proofVersions[0]);
            using (var stoppedSeal = new CancellationTokenSource())
            {
                stoppedSeal.Cancel();
                RequireThrows<OperationCanceledException>(() => FriendLink.VerifyStagedAuthorityProofBatch(
                    resumedStore, longRecord, accepted, WorldAuthorityTrust.ProofVersionsPerCheck,
                    stoppedSeal.Token), "cancelled final proof verification continued");
            }
            var sealBatches = 0;
            (bool Complete, int Used) sealedBatch;
            do
            {
                sealedBatch = FriendLink.VerifyStagedAuthorityProofBatch(resumedStore, longRecord,
                    accepted, WorldAuthorityTrust.ProofVersionsPerCheck, CancellationToken.None);
                Require(sealedBatch.Used <= WorldAuthorityTrust.ProofVersionsPerCheck &&
                    ++sealBatches <= 33, "final authority scan exceeded its per-check bound");
            } while (!sealedBatch.Complete);
            resumedStore.AppendReceivedStaged(longRecord, accepted, profile.Id,
                version.GroupId, roster.OwnerPublicKey);
            resumedStore.ClearStagedProof(longRecord, accepted);
            Require(new WorldAuthorityStore(longProofData).Read(profile.Id).Count == 2,
                "a subsequent handoff after 4097 saves failed after restart");
        }
        var afterThird = SharedWorldService.SignVersion(predecessor with
        {
            Number = predecessor.Number + 1,
            ParentHash = predecessor.VersionHash,
            BackupId = Guid.NewGuid(),
            CreatedUtc = predecessor.CreatedUtc.AddSeconds(1)
        }, voters[2].Key);
        Require(FriendLink.VerifySharedChain(version, afterThird,
            [successorVersion, .. longLineage], [accepted, nextAuthority, third]),
            "a receiver several versions behind did not cross two handoffs");
        Require(!FriendLink.VerifySharedChain(version, afterThird,
            [successorVersion, .. longLineage.Skip(1)], [accepted, nextAuthority, third]),
            "a missing authority boundary was accepted by a catching-up receiver");
        var checkedFive = longLineage[2];
        var validSix = longLineage[3];
        var validSeven = longLineage[4];
        var noCopyFloor = new FriendConfiguration();
        Require(!FriendLink.ObserveHistory(noCopyFloor, profile.Id, null, checkedFive) &&
            FriendLink.NewestTrustedAnchor(noCopyFloor, profile.Id, null, version.GroupId)?.VersionHash ==
                checkedFive.VersionHash &&
            FriendLink.VerifySharedChain(checkedFive, validSeven, [validSix],
                [accepted, nextAuthority]),
            "no-copy checked v5 did not anchor a valid descendant");
        var forkFive = SharedWorldService.SignVersion(checkedFive with
        { BackupId = Guid.NewGuid() }, voters[1].Key);
        var forkSix = SharedWorldService.SignVersion(validSix with
        { ParentHash = forkFive.VersionHash, BackupId = Guid.NewGuid() }, voters[1].Key);
        var forkSeven = SharedWorldService.SignVersion(validSeven with
        { ParentHash = forkSix.VersionHash, BackupId = Guid.NewGuid() }, voters[1].Key);
        var restartedNoCopyFloor = JsonSerializer.Deserialize<FriendConfiguration>(
            JsonSerializer.Serialize(noCopyFloor))!;
        Require(!FriendLink.VerifySharedChain(checkedFive, forkSeven, [forkSix],
            [accepted, nextAuthority]) &&
            FriendLink.NewestTrustedAnchor(restartedNoCopyFloor, profile.Id, null,
                version.GroupId)?.VersionHash == checkedFive.VersionHash &&
            FriendLink.ObserveHistory(restartedNoCopyFloor, profile.Id, null, forkSeven,
                ancestryConflict: true) &&
            restartedNoCopyFloor.LastSharedHostManifests[profile.Id].VersionHash == checkedFive.VersionHash &&
            restartedNoCopyFloor.CompetingSharedHostManifests[profile.Id].Any(item =>
                item.VersionHash == forkSeven.VersionHash) &&
            !FriendLink.CanCommitReceivedVersion(restartedNoCopyFloor, profile.Id, forkSeven),
            "no-copy checked v5 was overwritten by a signed fork at v7");
        var gap = new List<SharedWorldVersion>();
        var gapHead = checkedFive;
        for (var index = 0; index < 300; index++)
        {
            gapHead = SharedWorldService.SignVersion(gapHead with
            {
                Number = gapHead.Number + 1,
                ParentHash = gapHead.VersionHash,
                BackupId = Guid.NewGuid()
            }, voters[1].Key);
            gap.Add(gapHead);
        }
        var chainRoot = Path.Combine(root, "chain-catch-up");
        var chainDevice = Guid.NewGuid();
        using (var chainData = new LocalData(chainRoot))
        {
            var fetched = 0;
            var firstBatch = await FriendLink.VerifySharedChainBatchAsync(chainData, chainDevice,
                profile.Id, checkedFive, gapHead, [accepted, nextAuthority],
                (number, token) =>
                {
                    fetched++;
                    return Task.FromResult<SharedWorldVersion?>(gap[(int)(number - checkedFive.Number - 1)]);
                }, CancellationToken.None);
            Require(firstBatch.Pending && fetched == WorldAuthorityTrust.ChainVersionsPerCheck,
                "a long save gap was checked without a bounded pending result");
        }
        using (var resumedData = new LocalData(chainRoot))
        {
            var nextNumber = checkedFive.Number + WorldAuthorityTrust.ChainVersionsPerCheck + 1;
            var forkPiece = SharedWorldService.SignVersion(gap[(int)(nextNumber - checkedFive.Number - 1)] with
            { ParentHash = "BAD", BackupId = Guid.NewGuid() }, voters[1].Key);
            var forkResult = await FriendLink.VerifySharedChainBatchAsync(resumedData, chainDevice,
                profile.Id, checkedFive, gapHead, [accepted, nextAuthority],
                (number, token) => Task.FromResult<SharedWorldVersion?>(forkPiece), CancellationToken.None);
            var protectedFloor = new FriendConfiguration();
            FriendLink.ObserveHistory(protectedFloor, profile.Id, null, checkedFive);
            Require(forkResult.Conflict && FriendLink.ObserveHistory(protectedFloor, profile.Id,
                    null, gapHead, ancestryConflict: true) &&
                protectedFloor.LastSharedHostHashes[profile.Id] == checkedFive.VersionHash &&
                !FriendLink.CanCommitReceivedVersion(protectedFloor, profile.Id, gapHead),
                "a fork after partial catch-up replaced the checked head");
            using var stoppedGap = new CancellationTokenSource();
            var interruptedFetches = 0;
            RequireThrows<OperationCanceledException>(() => FriendLink.VerifySharedChainBatchAsync(
                resumedData, chainDevice, profile.Id, checkedFive, gapHead,
                [accepted, nextAuthority], (number, token) =>
                {
                    if (++interruptedFetches == 5) stoppedGap.Cancel();
                    return Task.FromResult<SharedWorldVersion?>(gap[(int)(number - checkedFive.Number - 1)]);
                }, stoppedGap.Token).GetAwaiter().GetResult(),
                "cancelled save-chain catch-up continued");
        }
        using (var resumedAgain = new LocalData(chainRoot))
        {
            var firstFetched = long.MaxValue;
            var calls = 0;
            FriendLink.SharedChainCheck resumed;
            do
            {
                var batchFetches = 0;
                resumed = await FriendLink.VerifySharedChainBatchAsync(resumedAgain, chainDevice,
                    profile.Id, checkedFive, gapHead, [accepted, nextAuthority],
                    (number, token) =>
                    {
                        firstFetched = Math.Min(firstFetched, number);
                        batchFetches++;
                        return Task.FromResult<SharedWorldVersion?>(gap[(int)(number - checkedFive.Number - 1)]);
                    }, CancellationToken.None);
                Require(batchFetches <= WorldAuthorityTrust.ChainVersionsPerCheck && ++calls <= 3,
                    "resumed save-chain work exceeded the per-check limit");
            } while (resumed.Pending);
            Require(resumed.Valid && firstFetched == checkedFive.Number +
                WorldAuthorityTrust.ChainVersionsPerCheck + 1,
                "restart did not resume from the protected signed chain cursor");
        }
        var ownerSecond = SharedWorldService.SignVersion(version with
        {
            Number = version.Number + 1,
            ParentHash = version.VersionHash,
            BackupId = Guid.NewGuid(),
            CreatedUtc = version.CreatedUtc.AddSeconds(1)
        }, ownerKey);
        var ownerThird = SharedWorldService.SignVersion(ownerSecond with
        {
            Number = ownerSecond.Number + 1,
            ParentHash = ownerSecond.VersionHash,
            BackupId = Guid.NewGuid(),
            CreatedUtc = ownerSecond.CreatedUtc.AddSeconds(1)
        }, ownerKey);
        WorldAuthorityProposal SignProposal(WorldAuthorityProposal draft, ECDsa signer) =>
            draft with
            {
                Signature = Convert.ToBase64String(signer.SignData(
                WorldAuthorityTrust.ProposalBasis(draft), HashAlgorithmName.SHA256))
            };
        WorldAuthorityVote SignVote(WorldAuthorityProposal forProposal, int index)
        {
            var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(forProposal),
                voters[index].Id, Convert.ToBase64String(voters[index].Key.ExportSubjectPublicKeyInfo()), "");
            return draft with
            {
                Signature = Convert.ToBase64String(voters[index].Key.SignData(
                WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256))
            };
        }
        WorldAuthorityRecord SignRecord(WorldAuthorityProposal forProposal,
            SharedWorldVersion head, IReadOnlyList<SharedWorldVersion>? lineage,
            params int[] voteIndices)
        {
            var draft = new WorldAuthorityRecord(1, forProposal, noOverrideRoster, head,
                voteIndices.Select(index => SignVote(forProposal, index)).ToArray(), null, "",
                VersionLineage: lineage);
            return draft with { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(draft)) };
        }
        var lateFirstProposal = SignProposal(proposal with
        {
            RosterHash = WorldAuthorityTrust.RosterHash(noOverrideRoster),
            VersionHash = ownerThird.VersionHash,
            Signature = ""
        }, voters[0].Key);
        var lateFirst = SignRecord(lateFirstProposal, ownerThird,
            [version, ownerSecond, ownerThird], 0, 1);
        var unprovenLateHead = SharedWorldService.SignVersion(ownerThird with
        { Number = 5000, ParentHash = ownerThird.VersionHash, BackupId = Guid.NewGuid() }, ownerKey);
        var unprovenLateProposal = SignProposal(lateFirstProposal with
        { VersionHash = unprovenLateHead.VersionHash, Signature = "" }, voters[0].Key);
        var unprovenLateDraft = new WorldAuthorityRecord(1, unprovenLateProposal,
            noOverrideRoster, unprovenLateHead,
            [SignVote(unprovenLateProposal, 0), SignVote(unprovenLateProposal, 1)], null, "");
        var unprovenLateRecord = unprovenLateDraft with
        { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(unprovenLateDraft)) };
        Require(WorldAuthorityTrust.Verify(unprovenLateRecord) &&
            WorldAuthorityTrust.VerifyLineage(unprovenLateRecord, null),
            "legacy unproven first record fixture is invalid");
        using (var freshReceiver = Data("unproven-legacy-first"))
            RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(freshReceiver)
                .AppendReceived(unprovenLateRecord, profile.Id, version.GroupId,
                    noOverrideRoster.OwnerPublicKey),
                "a fresh receiver accepted an unproven 5000-save first handoff");
        var lateFirstSave = SharedWorldService.SignVersion(ownerThird with
        {
            Number = ownerThird.Number + 1,
            ParentHash = ownerThird.VersionHash,
            BackupId = Guid.NewGuid(),
            CreatedUtc = ownerThird.CreatedUtc.AddSeconds(1)
        }, voters[0].Key);
        var lateSecondHead = SharedWorldService.SignVersion(lateFirstSave with
        {
            Number = lateFirstSave.Number + 1,
            ParentHash = lateFirstSave.VersionHash,
            BackupId = Guid.NewGuid(),
            CreatedUtc = lateFirstSave.CreatedUtc.AddSeconds(1)
        }, voters[0].Key);
        var lateSecondProposal = SignProposal(nextProposal with
        {
            ParentAuthorityHash = lateFirst.RecordHash,
            VersionHash = lateSecondHead.VersionHash,
            Signature = ""
        }, voters[1].Key);
        var lateSecond = SignRecord(lateSecondProposal, lateSecondHead,
            [lateFirstSave, lateSecondHead], 1, 2);
        var lateSecondSave = SharedWorldService.SignVersion(lateSecondHead with
        {
            Number = lateSecondHead.Number + 1,
            ParentHash = lateSecondHead.VersionHash,
            BackupId = Guid.NewGuid(),
            CreatedUtc = lateSecondHead.CreatedUtc.AddSeconds(1)
        }, voters[1].Key);
        var afterHandoff = SharedWorldService.SignVersion(lateSecondSave with
        {
            Number = lateSecondSave.Number + 1,
            ParentHash = lateSecondSave.VersionHash,
            BackupId = Guid.NewGuid()
        }, voters[1].Key);
        var afterHandoffNext = SharedWorldService.SignVersion(afterHandoff with
        {
            Number = afterHandoff.Number + 1,
            ParentHash = afterHandoff.VersionHash,
            BackupId = Guid.NewGuid()
        }, voters[1].Key);
        var forkHandoffHead = SharedWorldService.SignVersion(lateSecondSave with
        { BackupId = Guid.NewGuid() }, voters[1].Key);
        var forkAfterHandoff = SharedWorldService.SignVersion(afterHandoff with
        { ParentHash = forkHandoffHead.VersionHash, BackupId = Guid.NewGuid() }, voters[1].Key);
        var forkAfterHandoffNext = SharedWorldService.SignVersion(afterHandoffNext with
        { ParentHash = forkAfterHandoff.VersionHash, BackupId = Guid.NewGuid() }, voters[1].Key);
        Require(WorldAuthorityTrust.VerifyLineage(lateFirst, null) &&
            WorldAuthorityTrust.VerifyLineage(lateSecond, lateFirst) &&
            FriendLink.VerifySharedChain(version, lateSecondSave,
                [ownerSecond, ownerThird, lateFirstSave, lateSecondHead],
                [lateFirst, lateSecond]) &&
            FriendLink.VerifySharedChain(lateFirst.Version, lateSecondSave,
                [lateFirstSave, lateSecondHead], [lateFirst, lateSecond]) &&
            FriendLink.VerifySharedChain(lateSecondSave, afterHandoffNext,
                [afterHandoff], [lateFirst, lateSecond]) &&
            !FriendLink.VerifySharedChain(lateSecondSave, forkAfterHandoffNext,
                [forkAfterHandoff], [lateFirst, lateSecond]),
            "a behind receiver or a new no-copy receiver could not cross two handoffs");
        using (var receiverData = Data("earlier-owner-version-proof"))
        {
            var receiverAuthority = new WorldAuthorityStore(receiverData);
            receiverAuthority.AppendReceived(lateFirst, profile.Id, version.GroupId,
                noOverrideRoster.OwnerPublicKey);
            receiverAuthority.AppendReceived(lateSecond, profile.Id, version.GroupId,
                noOverrideRoster.OwnerPublicKey);
            Require(receiverAuthority.FindProvenVersion(profile.Id, ownerSecond.Number)?.VersionHash ==
                ownerSecond.VersionHash,
                "successor authority lost an earlier owner manifest needed by a behind receiver");
        }
        using (var manyData = Data("authority-over-128-decisions"))
        {
            var decisions = new List<WorldAuthorityRecord> { accepted };
            for (var index = 0; index < 128; index++)
            {
                var parentDecision = decisions[^1];
                var draft = proposal with
                {
                    Epoch = parentDecision.Proposal.Epoch + 1,
                    ParentAuthorityHash = parentDecision.RecordHash,
                    Signature = ""
                };
                var signedProposal = SignProposal(draft, voters[0].Key);
                var unsignedRecord = new WorldAuthorityRecord(1, signedProposal, roster, version,
                    [SignVote(signedProposal, 0), SignVote(signedProposal, 1)], null, "");
                decisions.Add(unsignedRecord with
                { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(unsignedRecord)) });
            }
            var authorityRoot = Path.Combine(manyData.RootPath, "shared-worlds",
                profile.Id.ToString("N"), "authority");
            Directory.CreateDirectory(authorityRoot);
            var prefix = Encoding.UTF8.GetBytes(string.Join("", decisions.Take(128).Select(item =>
                JsonSerializer.Serialize(item) + "\n")));
            File.WriteAllBytes(Path.Combine(authorityRoot, "records.jsonl"), prefix);
            manyData.SaveProtected($"authority-floor-{profile.Id:N}.protected",
                JsonSerializer.SerializeToUtf8Bytes(new
                { Schema = 1, Count = 128, LogHash = Convert.ToHexString(SHA256.HashData(prefix)) }));
            var manyStore = new WorldAuthorityStore(manyData);
            Require(manyStore.Read(profile.Id).Count == 128,
                "a 128-decision signed prefix failed verification");
            Require(FriendLink.NewAuthorityPage(decisions.Take(128).ToArray(),
                    [decisions[127], decisions[128]]).Single().RecordHash == decisions[128].RecordHash,
                "authority pagination did not continue from its verified tail");
            RequireThrows<InvalidDataException>(() => FriendLink.NewAuthorityPage(
                decisions.Take(128).ToArray(), [decisions[126], decisions[128]]),
                "an older or equivocated authority page replaced the verified tail");
            manyStore.AppendReceived(decisions[128], profile.Id, version.GroupId,
                roster.OwnerPublicKey);
            Require(new WorldAuthorityStore(manyData).Read(profile.Id).Count == 129 &&
                manyStore.ReadPage(profile.Id, 127).Select(item => item.RecordHash)
                    .SequenceEqual(decisions.Skip(127).Select(item => item.RecordHash)) &&
                manyStore.ReadPage(profile.Id, 128).Single().RecordHash == decisions[128].RecordHash,
                "authority stopped accepting decisions after record 128");
            var forkDraft = proposal with
            {
                Epoch = decisions[128].Proposal.Epoch,
                ParentAuthorityHash = decisions[127].RecordHash,
                CandidateAddress = "https://127.0.0.1:5132",
                Signature = ""
            };
            var forkProposal = SignProposal(forkDraft, voters[0].Key);
            var forkUnsigned = new WorldAuthorityRecord(1, forkProposal, roster, version,
                [SignVote(forkProposal, 0), SignVote(forkProposal, 1)], null, "");
            var forkRecord = forkUnsigned with
            {
                RecordHash = WorldAuthorityTrust.Hash(
                WorldAuthorityTrust.RecordBasis(forkUnsigned))
            };
            manyStore.Append(forkRecord);
            var competingHeads = WorldAuthorityTrust.EffectiveHeads(manyStore.Read(profile.Id));
            Require(competingHeads.Length == 2 && manyStore.Read(profile.Id).Count == 130,
                "paged history lost a same-epoch competingHeads head after record 128");
            var hostingPublicKey = manyStore.PrepareLocalHostingKey(profile.Id);
            var headHashes = competingHeads.Select(item => item.RecordHash).Order(StringComparer.Ordinal).ToArray();
            var resolutionDraft = new WorldAuthorityProposal(3, roster.GroupId, profile.Id,
                competingHeads.Max(item => item.Proposal.Epoch) + 1, decisions[128].RecordHash,
                WorldAuthorityTrust.RosterHash(roster), version.VersionHash, hostingPublicKey,
                "https://127.0.0.1:5132", "ResolutionQuorum", voters[0].Id,
                Convert.ToBase64String(voters[0].Key.ExportSubjectPublicKeyInfo()), "",
                CompetingHeadHashes: headHashes);
            var bindingDraft = new WorldSuccessorBinding(voters[0].Id,
                resolutionDraft.ProposerPublicKey, hostingPublicKey, "");
            var bound = resolutionDraft with
            {
                SuccessorBinding = bindingDraft with
                {
                    Signature = Convert.ToBase64String(voters[0].Key.SignData(
                WorldAuthorityTrust.BindingBasis(resolutionDraft, bindingDraft),
                HashAlgorithmName.SHA256))
                }
            };
            var resolutionProposal = SignProposal(bound, voters[0].Key);
            var unsignedResolution = new WorldAuthorityRecord(2, resolutionProposal, roster,
                version, [SignVote(resolutionProposal, 0), SignVote(resolutionProposal, 1)], null, "");
            var signedResolution = unsignedResolution with
            {
                RecordHash = WorldAuthorityTrust.Hash(
                WorldAuthorityTrust.RecordBasis(unsignedResolution))
            };
            manyStore.Append(signedResolution);
            Require(manyStore.Read(profile.Id).Count == 131 &&
                manyStore.ReadUniqueHead(profile.Id)?.RecordHash == signedResolution.RecordHash &&
                manyStore.Read(profile.Id).Any(item => item.RecordHash == forkRecord.RecordHash),
                "a bounded two-head resolution was tied to total history lifetime or discarded the loser");
        }
        var friendFloor = new FriendConfiguration();
        Require(!FriendLink.ObserveHistory(friendFloor, profile.Id, null, lateSecondSave),
            "first verified successor head was treated as a conflict");
        var sameNumberFork = SharedWorldService.SignVersion(lateSecondSave with
        { BackupId = Guid.NewGuid() }, voters[1].Key);
        var restartedFloor = JsonSerializer.Deserialize<FriendConfiguration>(
            JsonSerializer.Serialize(friendFloor))!;
        Require(FriendLink.ObserveHistory(restartedFloor, profile.Id, null, sameNumberFork) &&
            restartedFloor.LastSharedHostHashes[profile.Id] == lateSecondSave.VersionHash &&
            !FriendLink.CanCommitReceivedVersion(restartedFloor, profile.Id, sameNumberFork),
            "restart lost the checked head or accepted a same-number signed fork");
        var proofFile = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
            "authority", "proof-" + third.RecordHash,
            longLineage[35].Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
        var proofOriginal = File.ReadAllBytes(proofFile);
        File.WriteAllText(proofFile, "{}");
        RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(data).Read(profile.Id),
            "restart accepted a tampered lineage page");
        File.WriteAllBytes(proofFile, proofOriginal);
        var expiredRosterDraft = noOverrideRoster with
        {
            Epoch = noOverrideRoster.Epoch + 1,
            Revision = noOverrideRoster.Revision + 1,
            Members = noOverrideRoster.Members.Select(member => member.DeviceId == voters[0].Id
                ? member with { AccessExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1) }
                : member).ToArray(),
            Signature = ""
        };
        var expiredRoster = expiredRosterDraft with
        {
            Signature = Convert.ToBase64String(ownerKey.SignData(
            SharedWorldRosterTrust.Basis(expiredRosterDraft), HashAlgorithmName.SHA256))
        };
        var expiredDraft = proposal with
        { RosterHash = WorldAuthorityTrust.RosterHash(expiredRoster), Signature = "" };
        var expiredProposal = expiredDraft with
        {
            Signature = Convert.ToBase64String(voters[0].Key.SignData(
            WorldAuthorityTrust.ProposalBasis(expiredDraft), HashAlgorithmName.SHA256))
        };
        WorldAuthorityVote ExpiredVote(int index)
        {
            var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(expiredProposal),
                voters[index].Id, Convert.ToBase64String(voters[index].Key.ExportSubjectPublicKeyInfo()), "");
            return draft with
            {
                Signature = Convert.ToBase64String(voters[index].Key.SignData(
                WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256))
            };
        }
        var expiredRecord = new WorldAuthorityRecord(1, expiredProposal, expiredRoster, version,
            [ExpiredVote(0), ExpiredVote(1)], null, "");
        expiredRecord = expiredRecord with
        {
            RecordHash = WorldAuthorityTrust.Hash(
            WorldAuthorityTrust.RecordBasis(expiredRecord))
        };
        Require(WorldAuthorityTrust.Verify(expiredRecord), "historical proof became time-dependent");
        var delayedStore = new WorldAuthorityStore(data,
            new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(2)));
        RequireThrows<InvalidDataException>(() => delayedStore.Append(expiredRecord),
            "expired participant was accepted for a new authority decision");
        RequireThrows<InvalidDataException>(() => delayedStore.AppendReceived(expiredRecord,
            profile.Id, version.GroupId, roster.OwnerPublicKey),
            "a delayed first receipt accepted an expired voter without acceptance proof");
        var acceptedAt = DateTimeOffset.UtcNow;
        expiredRecord = expiredRecord with
        {
            HostAcceptedUtc = acceptedAt,
            HostAcceptanceSignature = Convert.ToBase64String(voters[0].Key.SignData(
                WorldAuthorityTrust.HostAcceptanceBasis(expiredRecord, acceptedAt),
                HashAlgorithmName.SHA256))
        };
        RequireThrows<InvalidDataException>(() => delayedStore.AppendReceived(expiredRecord,
            profile.Id, version.GroupId, roster.OwnerPublicKey),
            "backdated candidate proof bypassed expired first receipt");
        store.AppendReceived(expiredRecord, profile.Id, version.GroupId, roster.OwnerPublicKey);
        Require(delayedStore.Read(profile.Id).Any(item => item.RecordHash == expiredRecord.RecordHash),
            "a signed historical branch was discarded using this PC's current clock");
        var log = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"),
            "authority", "records.jsonl");
        var intactLog = File.ReadAllBytes(log);
        File.AppendAllText(log, "tampered\n");
        Require(store.Fenced(profile.Id, candidateKey, out _), "tampered log opened authority");
        File.WriteAllBytes(log, intactLog[..(Array.IndexOf(intactLog, (byte)'\n') + 1)]);
        Require(store.Fenced(profile.Id, candidateKey, out _), "rolled-back log opened authority");
        var damagedStatus = await new HostManager(data, Games(data)).SharedWorldStatusAsync(profile.Id);
        Require(damagedStatus.Authority is { State: "ReviewRequired", Head: null },
            "local status treated a rolled-back authority log as a valid handoff");
    }
    finally { foreach (var voter in voters) voter.Key.Dispose(); }
});

await Check("Host-loss eligibility uses two minutes of monotonic transport failures", () =>
{
    long tick = 0;
    var loss = new SharedWorldHostLoss(() => tick, TimeSpan.TicksPerSecond);
    Require(!loss.MayPropose, "a new PC was eligible to propose takeover");
    loss.Observe(HostReachabilityObservation.TransportFailure);
    tick += TimeSpan.FromSeconds(119).Ticks;
    loss.Observe(HostReachabilityObservation.TransportFailure);
    Require(!loss.MayPropose, "a failed retry shortened the two-minute wait");
    tick += TimeSpan.FromSeconds(1).Ticks;
    Require(loss.MayPropose, "two full monotonic minutes did not allow a proposal");
    loss.Observe(HostReachabilityObservation.OtherResponse);
    Require(!loss.MayPropose && loss.UnreachableFor is null,
        "a reachable Host did not reset takeover eligibility");
    loss.Observe(HostReachabilityObservation.TransportFailure);
    tick += TimeSpan.FromSeconds(119).Ticks;
    Require(!loss.MayPropose, "one stale failure was treated as current Host loss");
    loss.Observe(HostReachabilityObservation.TransportFailure);
    tick += TimeSpan.FromSeconds(1).Ticks;
    Require(loss.MayPropose, "a second complete loss was ignored");
    loss.Observe(HostReachabilityObservation.Authenticated);
    Require(!loss.MayPropose, "authenticated Host return did not reset eligibility");
    return Task.CompletedTask;
});

await Check("three disposable PCs compare exact save heads before majority takeover", async () =>
{
    using var host = Data("quorum-host");
    var profile = Profile("quorum", "quorum-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "verified quorum world");
    var backup = new WorldBackupService(host, TimeProvider.System);
    var shares = new SharedWorldService(host, backup);
    var keys = Enumerable.Range(0, 3).Select(_ => ECDsa.Create(ECCurve.NamedCurves.nistP256)).ToArray();
    var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
    var pcs = new List<LocalData>();
    try
    {
        var roster = shares.PublishRoster(profile, ids.Select((id, index) =>
            new SharedWorldRosterMember(id,
                Convert.ToBase64String(keys[index].ExportSubjectPublicKeyInfo()),
                new SharedWorldGrants(true, true, true), false)).ToArray());
        var checkpoint = backup.Create(profile, BackupKinds.Rolling);
        Require(checkpoint.Ok && checkpoint.Backup is not null, "quorum fixture backup failed");
        var version = shares.PublishAfterStop(profile, checkpoint.Backup!.Id).Version
            ?? throw new Exception("quorum version missing");
        var floor = new SharedRosterFloor(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature);
        string Vault(LocalData pc, int index)
        {
            var root = Path.Combine(pc.RootPath, "received-shared-worlds", ids[index].ToString("N"),
                profile.Id.ToString("N"));
            var payload = Path.Combine(root, version.VersionHash, SharedWorldService.PayloadDirectory);
            Directory.CreateDirectory(payload);
            for (var fileIndex = 0; fileIndex < version.Files.Count; fileIndex++)
                File.WriteAllBytes(SharedWorldService.SafeChild(payload, version.Files[fileIndex].Path),
                    shares.ReadChunk(version, fileIndex, 0));
            File.WriteAllBytes(Path.Combine(root, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return root;
        }
        for (var i = 0; i < 3; i++) pcs.Add(Data("quorum-pc-" + i));
        var vaults = pcs.Select((pc, index) => Vault(pc, index)).ToArray();
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var candidatePort = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        var candidateAddress = $"https://127.0.0.1:{candidatePort}";
        using var candidateCertificate = new HostIdentity(pcs[0]).Ensure(candidateAddress);
        var candidatePin = HostIdentity.Fingerprint(candidateCertificate);
        long ticks = 0;
        var losses = Enumerable.Range(0, 3).Select(_ =>
            new SharedWorldHostLoss(() => ticks, TimeSpan.TicksPerSecond)).ToArray();
        foreach (var loss in losses) loss.Observe(HostReachabilityObservation.TransportFailure);
        RequireThrows<InvalidDataException>(() => SharedWorldElection.PrepareOffer(losses[0], vaults[0],
            roster, floor, ids[0], keys[0], candidateAddress, candidatePin,
            new WorldAuthorityStore(pcs[0])),
            "candidate proposed before the two-minute loss check");
        ticks += TimeSpan.FromSeconds(119).Ticks;
        foreach (var loss in losses) loss.Observe(HostReachabilityObservation.TransportFailure);
        ticks += TimeSpan.FromSeconds(1).Ticks;
        var offer = SharedWorldElection.PrepareOffer(losses[0], vaults[0], roster, floor,
            ids[0], keys[0], candidateAddress, candidatePin,
            new WorldAuthorityStore(pcs[0]));
        var routeOffer = SharedWorldElection.PrepareOffer(losses[0], vaults[0], roster, floor,
            ids[0], keys[0], $"https://192.0.2.10:{candidatePort}", candidatePin,
            new WorldAuthorityStore(pcs[0]));
        var routeBranch = SharedWorldSeparateCopyStore.Sign(routeOffer, keys[0]);
        var routeChallenge = SharedWorldSeparateRoute.SignChallenge(routeBranch, ids[1], keys[1]);
        var staleSeparateChallengeDraft = routeChallenge with
        { IssuedUtc = DateTimeOffset.UtcNow.AddHours(-1), Signature = "" };
        var staleSeparateChallenge = staleSeparateChallengeDraft with
        {
            Signature = Convert.ToBase64String(keys[1].SignData(
            SharedWorldSeparateRoute.ChallengeBasis(staleSeparateChallengeDraft), HashAlgorithmName.SHA256))
        };
        var routeProof = SharedWorldSeparateRoute.SignProof(routeBranch, routeChallenge, keys[0]);
        Require(SharedWorldSeparateRoute.VerifyChallenge(routeChallenge, routeBranch,
                DateTimeOffset.UtcNow) &&
            !SharedWorldSeparateRoute.VerifyChallenge(staleSeparateChallenge, routeBranch,
                DateTimeOffset.UtcNow) &&
            SharedWorldSeparateRoute.VerifyProof(routeProof, routeChallenge, routeBranch) &&
            !SharedWorldSeparateRoute.VerifyProof(routeProof with { Endpoint = candidateAddress },
                routeChallenge, routeBranch) &&
            !SharedWorldSeparateRoute.VerifyChallenge(routeChallenge with
            { ObserverDeviceId = ids[0] }, routeBranch, DateTimeOffset.UtcNow),
            "a separate-copy route proof did not bind a different enrolled PC and exact direct IP");
        var separate = new SharedWorldSeparateCopyStore(pcs[0]);
        RequireThrows<InvalidDataException>(() => separate.Declare(losses[0], offer,
            vaults[0], keys[0], false), "a separate copy skipped its explicit split warning");
        var separateBranch = separate.Declare(losses[0], offer, vaults[0], keys[0], true);
        Require(SharedWorldSeparateCopyStore.Verify(separateBranch) &&
            separate.Read(profile.Id).Single().BranchHash == separateBranch.BranchHash &&
            separate.Declare(losses[0], offer, vaults[0], keys[0], true).BranchHash ==
                separateBranch.BranchHash &&
            new WorldAuthorityStore(pcs[0]).Read(profile.Id).Count == 0,
            "warned separate copy changed authority or lost its preserved branch");
        using (var separatePc = CloneRecoveryPc(pcs[0], 3))
        {
            var separateVault = Path.Combine(separatePc.RootPath, "received-shared-worlds",
                ids[0].ToString("N"), profile.Id.ToString("N"));
            var separatePort = FreePort();
            var separateAddress = $"https://192.0.2.11:{separatePort}";
            using var separateCertificate = new HostIdentity(separatePc).Ensure(separateAddress);
            var separatePin = HostIdentity.Fingerprint(separateCertificate);
            var separateOffer = SharedWorldElection.PrepareOffer(losses[0], separateVault,
                roster, floor, ids[0], keys[0], separateAddress, separatePin,
                new WorldAuthorityStore(separatePc));
            var separateFork = new SharedWorldSeparateCopyStore(separatePc).Declare(
                losses[0], separateOffer, separateVault, keys[0], true);
            separatePc.SaveProtected($"shared-world-pc-signing-{ids[0]:N}.protected",
                keys[0].ExportPkcs8PrivateKey());
            new SharedWorldVoteInbox(separatePc).Arm(separateOffer, separateVault);
            var separateSettings = separatePc.LoadSettings();
            separateSettings.CompanionBindAddress = "127.0.0.1";
            separateSettings.CompanionEndpoint = separateAddress;
            separateSettings.CompanionPort = separatePort;
            separateSettings.CompanionListeningEnabled = true;
            separatePc.SaveSettings(separateSettings);
            var separateClock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var separateManager = new HostManager(separatePc, Games(separatePc), separateClock);
            using var separateModeGate = new SemaphoreSlim(1, 1);
            var separateListener = new CompanionServer(separatePc, separateManager,
                new PairingService(separatePc), Games(separatePc),
                new ServerLogService(separatePc, separateManager), separateModeGate,
                separatePort + 2);
            separateManager.CompanionListenerOwnershipProbe = separateListener.OwnsListener;
            try
            {
                await separateListener.SyncAsync();
                Require(separateListener.ListenerState == CompanionListenerStates.Listening &&
                    separateListener.OwnsListener(separateAddress, separatePort, separatePin,
                        "127.0.0.1"),
                    "the disposable candidate app did not own its pinned HTTPS listener");
                var separateSetup = new TakeoverLocalSetup(fixture,
                    version.PortableSetup.GameVersion, [], true, separatePort, FreePort());
                var separateRequest = new SeparateCopyHostRestoreRequest(separateFork.BranchHash,
                    separateSetup, "Warned fixture", "Warned fixture");
                Require((await Manager(separatePc).RestoreSeparateCopyAsync(profile.Id,
                        separateRequest)).Code == "LocalSetupIncomplete" &&
                    !separateListener.OwnsListener(separateAddress, separatePort,
                        new string('A', 64), "127.0.0.1"),
                    "an unowned or wrongly pinned control listener bypassed the port check");
                var separateFile = SharedWorldService.SafeChild(Path.Combine(separateVault,
                    version.VersionHash, SharedWorldService.PayloadDirectory), version.Files[0].Path);
                var preservedBytes = File.ReadAllBytes(separateFile);
                File.WriteAllText(separateFile, "tampered separate vault");
                Require(!(await separateManager.RestoreSeparateCopyAsync(profile.Id,
                    separateRequest)).Ok, "a warned copy restored tampered save files");
                File.WriteAllBytes(separateFile, preservedBytes);
                Require((await separateManager.RestoreSeparateCopyAsync(profile.Id,
                    separateRequest, freeBytes: _ => 0)).Code == "LocalSetupIncomplete",
                    "a warned copy ignored the 1 GiB space reserve");
                var separateRestored = await separateManager.RestoreSeparateCopyAsync(profile.Id,
                    separateRequest);
                Require(separateRestored.Ok && separateRestored.ProfileId is { } separateLocalId &&
                    (await separateManager.StartAsync(separateLocalId)).Code ==
                        "SeparateCopyManualStartRequired",
                    $"warned copy was not restored with an ordinary Start fence: {separateRestored.Code} {separateRestored.Message}");
                var restoredId = separateRestored.ProfileId!.Value;
                Require((await separateManager.SetSharedSavesAsync(restoredId, true)).Code ==
                    "SeparateCopyCannotShare" &&
                    (await separateManager.StartSeparateCopyAsync(restoredId,
                        new string('A', 64))).Code == "SeparateCopyMissing",
                    "warned copy gained authority sharing or accepted the wrong signed branch");
                Require((await separateManager.FinishSeparateCopyAsync(profile.Id,
                    new SeparateCopyHostFinishRequest(separateFork.BranchHash,
                        separateSetup))).Code == "SeparateChecksPending",
                    "warned copy finished without a second-PC route round trip");
                pcs[1].SaveProtected($"shared-world-pc-signing-{ids[1]:N}.protected",
                    keys[1].ExportPkcs8PrivateKey());
                using var observerKey = ECDsa.Create();
                observerKey.ImportPkcs8PrivateKey(pcs[1].LoadProtected(
                    $"shared-world-pc-signing-{ids[1]:N}.protected")!, out _);
                var separateChallenge = SharedWorldSeparateRoute.SignChallenge(separateFork,
                    ids[1], observerKey);
                using var separateHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                        certificate is not null && HostIdentity.Fingerprint(certificate) == separatePin
                };
                using var separateClient = new HttpClient(separateHandler)
                { BaseAddress = new Uri($"https://127.0.0.1:{separatePort}/") };
                var separateRoute = $"api/companion/servers/{profile.Id}/shared-world/" +
                    $"separate-route/{separateFork.BranchHash}";
                using var proofRequest = new HttpRequestMessage(HttpMethod.Post,
                    separateRoute + "/proof")
                { Content = JsonContent.Create(separateChallenge) };
                proofRequest.Headers.Host = new Uri(separateAddress).Authority;
                using var proofResponse = await separateClient.SendAsync(proofRequest);
                var separateProof = proofResponse.IsSuccessStatusCode ?
                    await proofResponse.Content.ReadFromJsonAsync<SeparateCopyRouteProof>() : null;
                Require(SharedWorldSeparateRoute.VerifyProof(separateProof,
                    separateChallenge, separateFork) &&
                    (await separateManager.FinishSeparateCopyAsync(profile.Id,
                        new SeparateCopyHostFinishRequest(separateFork.BranchHash,
                            separateSetup))).Code == "SeparateChecksPending",
                    "the observer PC did not receive a valid pinned HTTPS route proof");
                RequireThrows<InvalidDataException>(() =>
                    SharedWorldSeparateRoute.SignConfirmation(separateFork,
                        separateChallenge, separateProof!, keys[0]),
                    "the candidate signed its own observer confirmation");
                var receiptDraft = new SeparateCopyRouteReceipt(1,
                    separateFork.Offer.Proposal.GroupId, profile.Id, separateFork.BranchHash,
                    separateAddress, separatePin,
                    SharedWorldSeparateRoute.ChallengeHash(separateChallenge),
                    SharedWorldSeparateRoute.ProofHash(separateProof!), ids[1],
                    Convert.ToBase64String(observerKey.ExportSubjectPublicKeyInfo()), "");
                var candidateOnly = new SeparateCopyRouteConfirmation(separateChallenge,
                    separateProof!, receiptDraft with
                    {
                        Signature = Convert.ToBase64String(
                        keys[0].SignData(SharedWorldSeparateRoute.ReceiptBasis(receiptDraft),
                            HashAlgorithmName.SHA256))
                    });
                Require(!await separateManager.ConfirmSeparateRouteAsync(profile.Id,
                        separateFork.BranchHash, candidateOnly, separatePin),
                    "candidate self-confirmed without the second PC's private key");
                var observerConfirmation = SharedWorldSeparateRoute.SignConfirmation(
                    separateFork, separateChallenge, separateProof!, observerKey);
                var wrongObserverDraft = observerConfirmation.Receipt with
                {
                    ObserverDeviceId = ids[2],
                    ObserverPublicKey = Convert.ToBase64String(keys[2].ExportSubjectPublicKeyInfo()),
                    Signature = ""
                };
                var wrongObserver = observerConfirmation with
                {
                    Receipt = wrongObserverDraft with
                    {
                        Signature = Convert.ToBase64String(keys[2].SignData(
                    SharedWorldSeparateRoute.ReceiptBasis(wrongObserverDraft),
                    HashAlgorithmName.SHA256))
                    }
                };
                Require(!SharedWorldSeparateRoute.VerifyConfirmation(wrongObserver,
                        separateFork, DateTimeOffset.UtcNow) &&
                    !SharedWorldSeparateRoute.VerifyConfirmation(observerConfirmation with
                    { Receipt = observerConfirmation.Receipt with { GroupId = Guid.NewGuid() } },
                        separateFork, DateTimeOffset.UtcNow) &&
                    !SharedWorldSeparateRoute.VerifyConfirmation(observerConfirmation with
                    { Receipt = observerConfirmation.Receipt with { ProofHash = new string('A', 64) } },
                        separateFork, DateTimeOffset.UtcNow) &&
                    !SharedWorldSeparateRoute.VerifyConfirmation(observerConfirmation,
                        separateFork, DateTimeOffset.UtcNow.AddHours(1)),
                    "a wrong observer, group, proof, or expired route confirmation was accepted");
                using var confirmRequest = new HttpRequestMessage(HttpMethod.Post,
                    separateRoute + "/confirm")
                { Content = JsonContent.Create(observerConfirmation) };
                confirmRequest.Headers.Host = new Uri(separateAddress).Authority;
                using var confirmResponse = await separateClient.SendAsync(confirmRequest);
                Require(confirmResponse.IsSuccessStatusCode &&
                    !await separateManager.ConfirmSeparateRouteAsync(profile.Id,
                        separateFork.BranchHash, observerConfirmation, separatePin),
                    "the second PC's signed HTTPS confirmation was rejected or replayable");
                var separateFinished = await separateManager.FinishSeparateCopyAsync(profile.Id,
                    new SeparateCopyHostFinishRequest(separateFork.BranchHash, separateSetup));
                Require(separateFinished.Code == "SeparateReadyForManualStart" &&
                    new WorldAuthorityStore(separatePc).Read(profile.Id).Count == 0,
                    $"warned copy became authoritative or could not finish: {separateFinished.Code} {separateFinished.Message}");
                var restartedSeparateManager = new HostManager(separatePc,
                    Games(separatePc), separateClock)
                { CompanionListenerOwnershipProbe = separateListener.OwnsListener };
                Require(!(await restartedSeparateManager.SeparateCopyHostStatusAsync(profile.Id,
                        separateFork.BranchHash)).ReadyForManualStart &&
                    (await restartedSeparateManager.SeparateCopyHostStatusAsync(profile.Id,
                        separateFork.BranchHash, hostLossCurrent: true)).ReadyForManualStart,
                    "separate-copy readiness after restart omitted current Host-loss evidence");
                separateClock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
                Require(!(await separateManager.SeparateCopyHostStatusAsync(profile.Id,
                        separateFork.BranchHash, hostLossCurrent: true)).ReadyForManualStart &&
                    !(await separateManager.StartSeparateCopyAsync(restoredId,
                        separateFork.BranchHash)).Ok,
                    "an expired second-PC route proof still enabled manual Start");
                separateClock.Advance(-TimeSpan.FromHours(1) - TimeSpan.FromMinutes(1));
                var separateStarted = await separateManager.StartSeparateCopyAsync(restoredId,
                    separateFork.BranchHash);
                Require(separateStarted.Ok, $"manual warned Start failed: {separateStarted.Code} {separateStarted.Message}");
                File.WriteAllText(Path.Combine(separatePc.NewWorldDirectory(restoredId), "world.dat"),
                    "warned branch saved change");
                Require((await separateManager.StopAsync(restoredId)).Ok,
                    "warned copy could not stop its exact managed fixture run");
                Require((await separateManager.StartSeparateCopyAsync(restoredId,
                    separateFork.BranchHash)).Ok,
                    "a verified post-Stop warned save could not resume manually");
                Require((await separateManager.StopAsync(restoredId)).Ok,
                    "warned copy could not stop after a later manual Start");
                new SharedWorldSeparateCopyStore(separatePc).MarkHostReturned(profile.Id);
                Require((await separateManager.StartSeparateCopyAsync(restoredId,
                        separateFork.BranchHash)).Code == "SeparateCopyReviewRequired" &&
                    (await separateManager.SeparateCopyHostStatusAsync(profile.Id,
                        separateFork.BranchHash)).ReviewRequired &&
                    new SharedWorldSeparateCopyStore(separatePc).Read(profile.Id).Count == 2 &&
                    new WorldAuthorityStore(separatePc).Read(profile.Id).Count == 0,
                    "returning Host did not fence the warned branch or preserve both signed forks");
            }
            finally { await separateListener.StopAsync(); }
        }
        var branchName = $"shared-world-separate-{profile.Id:N}.protected";
        var branchBytes = pcs[0].LoadProtected(branchName)!;
        pcs[0].DeleteProtected(branchName);
        RequireThrows<InvalidDataException>(() => separate.Read(profile.Id),
            "a deleted separate branch silently disappeared despite its protected floor");
        pcs[0].SaveProtected(branchName, branchBytes);
        Require(!separate.HostReturned(profile.Id, separateBranch.BranchHash),
            "a new separate copy was already marked for review");
        separate.MarkHostReturned(profile.Id);
        Require(separate.HostReturned(profile.Id, separateBranch.BranchHash) &&
            separate.Read(profile.Id).Single().BranchHash == separateBranch.BranchHash,
            "Host return removed the signed separate history or did not fence it");
        var returnedBytes = pcs[0].LoadProtected(branchName)!;
        pcs[0].SaveProtected(branchName, branchBytes);
        RequireThrows<InvalidDataException>(() => separate.Read(profile.Id),
            "rolling back the Host-return fence was accepted");
        pcs[0].SaveProtected(branchName, returnedBytes);
        var vote0 = SharedWorldElection.Vote(losses[0], vaults[0], floor, roster.OwnerPublicKey,
            offer, ids[0], keys[0], new WorldAuthorityStore(pcs[0]));
        RequireThrows<InvalidDataException>(() => SharedWorldElection.ConfirmQuorum(offer, [vote0],
            new WorldAuthorityStore(pcs[0])), "one of three designated voters made a majority");
        var vote1 = SharedWorldElection.Vote(losses[1], vaults[1], floor, roster.OwnerPublicKey,
            offer, ids[1], keys[1], new WorldAuthorityStore(pcs[1]));
        pcs[0].SaveProtected($"shared-world-pc-signing-{ids[0]:N}.protected",
            keys[0].ExportPkcs8PrivateKey());
        var inbox = new SharedWorldVoteInbox(pcs[0]);
        inbox.Arm(offer, vaults[0]);
        var armedStatus = new SharedWorldVoteInbox(pcs[0]).Status(profile.Id, ids[0]);
        Require(armedStatus.State == "OfferArmed" && armedStatus.Votes == 0 &&
            armedStatus.Required == 2 && inbox.Armed(profile.Id)?.Proposal == offer.Proposal &&
            armedStatus.Version == offer.Version.Number &&
            armedStatus.CandidateAddress == candidateAddress && armedStatus.SeparateCopies == 1 &&
            armedStatus.ProposalHash == WorldAuthorityTrust.ProposalHash(offer.Proposal) &&
            armedStatus.AuthorityHeadHash is null,
            "candidate recovery status did not reopen from verified durable records");
        var statusJson = JsonSerializer.Serialize(armedStatus);
        Require(!statusJson.Contains("Files", StringComparison.OrdinalIgnoreCase) &&
            !statusJson.Contains("SigningPublicKey", StringComparison.OrdinalIgnoreCase) &&
            !statusJson.Contains("CandidatePublicKey", StringComparison.OrdinalIgnoreCase),
            "recovery status exposed a signed manifest or signing identity");
        var proposalHash = WorldAuthorityTrust.ProposalHash(offer.Proposal);
        var candidateSettings = pcs[0].LoadSettings();
        candidateSettings.CompanionBindAddress = "127.0.0.1";
        candidateSettings.CompanionEndpoint = candidateAddress;
        candidateSettings.CompanionPort = candidatePort;
        candidateSettings.CompanionListeningEnabled = false;
        pcs[0].SaveSettings(candidateSettings);
        using var modeGate = new SemaphoreSlim(1, 1);
        var candidateManager = Manager(pcs[0]);
        var candidateListener = new CompanionServer(pcs[0], candidateManager,
            new PairingService(pcs[0]), Games(pcs[0]),
            new ServerLogService(pcs[0], candidateManager), modeGate, candidatePort + 2,
            recoveryLossProbe: (_, _) => Task.FromResult(losses[0].MayPropose),
            recoveryLossCurrent: _ => losses[0].MayPropose);
        try
        {
            await candidateListener.SyncAsync();
            Require(candidateListener.ListenerState == CompanionListenerStates.Off &&
                !candidateListener.Active && !candidateManager.CompanionListeningEnabled,
                "arming an offer opened the candidate listener before the local action enabled it");
            var rejectedActivation = await SharedWorldCandidateListener.ActivateAsync(
                new(false, "HostLossNotConfirmed", "The old Host is still reachable."),
                candidateManager, candidateListener);
            Require(!rejectedActivation.Ok && !candidateListener.Active &&
                !candidateManager.CompanionListeningEnabled,
                "a failed recovery offer enabled an inactive listener");
            var activated = await SharedWorldCandidateListener.ActivateAsync(
                new(true, "RecoveryOfferArmed", "A signed offer is ready.", offer),
                candidateManager, candidateListener);
            Require(activated.Ok && candidateManager.CompanionListeningEnabled &&
                candidateListener.ListenerState == CompanionListenerStates.Listening &&
                candidateListener.Active,
                "a signed recovery offer did not open its owned candidate HTTPS listener");
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && HostIdentity.Fingerprint(certificate) == candidatePin
            };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(candidateAddress + "/") };
            var route = $"api/companion/servers/{profile.Id}/shared-world/recovery/{proposalHash}";
            using var overWireChallenge = await client.GetAsync($"{route}/challenge/{ids[1]}");
            Require(overWireChallenge.IsSuccessStatusCode,
                "signed roster voter could not reach candidate HTTPS challenge");
            var wireNonce = await overWireChallenge.Content.ReadFromJsonAsync<WorldAuthorityChallenge>();
            Require(wireNonce?.Nonce is { Length: 44 }, "candidate HTTPS challenge was invalid");
            using var deniedOffer = await client.PostAsJsonAsync(route + "/offer",
                new WorldAuthorityOfferRequest(1, roster.GroupId, profile.Id, proposalHash,
                    ids[1], Convert.ToBase64String(keys[1].ExportSubjectPublicKeyInfo()),
                    wireNonce!.Nonce, ""));
            Require(deniedOffer.StatusCode == HttpStatusCode.Forbidden,
                "unsigned HTTPS offer request was accepted");
            using var renewedChallenge = await client.GetAsync($"{route}/challenge/{ids[1]}");
            var validNonce = await renewedChallenge.Content.ReadFromJsonAsync<WorldAuthorityChallenge>();
            Require(validNonce?.Nonce is { Length: 44 }, "candidate did not renew its challenge");
            var wireDraft = new WorldAuthorityOfferRequest(1, roster.GroupId, profile.Id,
                proposalHash, ids[1], Convert.ToBase64String(keys[1].ExportSubjectPublicKeyInfo()),
                validNonce!.Nonce, "");
            var wireRequest = wireDraft with
            {
                Signature = Convert.ToBase64String(keys[1].SignData(
                SharedWorldVoteInbox.RequestBasis(wireDraft), HashAlgorithmName.SHA256))
            };
            using var wireOffer = await client.PostAsJsonAsync(route + "/offer", wireRequest);
            Require(wireOffer.IsSuccessStatusCode &&
                (await wireOffer.Content.ReadFromJsonAsync<WorldAuthorityOffer>())?.Proposal == offer.Proposal,
                "signed voter did not get the exact offer over pinned HTTPS");
            using var wireVote = await client.PostAsJsonAsync(route + "/vote", vote0);
            Require((await wireVote.Content.ReadFromJsonAsync<WorldAuthorityVoteResult>())?.Code ==
                "VoteRecorded", "candidate HTTPS endpoint did not durably record the signed vote");
        }
        finally { await candidateListener.StopAsync(); }
        var challenge = inbox.Challenge(profile.Id, proposalHash, ids[1])
            ?? throw new Exception("designated voter could not request the candidate offer");
        var requestDraft = new WorldAuthorityOfferRequest(1, roster.GroupId, profile.Id,
            proposalHash, ids[1], Convert.ToBase64String(keys[1].ExportSubjectPublicKeyInfo()),
            challenge.Nonce, "");
        var request = requestDraft with
        {
            Signature = Convert.ToBase64String(keys[1].SignData(
            SharedWorldVoteInbox.RequestBasis(requestDraft), HashAlgorithmName.SHA256))
        };
        Require(inbox.ReadOffer(request)?.Proposal == offer.Proposal,
            "signed voter did not receive the exact candidate offer");
        Require(inbox.ReadOffer(request) is null, "candidate challenge was reusable");
        Require(inbox.AcceptVote(profile.Id, proposalHash, vote0).Code == "VoteRecorded",
            "candidate did not retain the first signed vote");
        var inboxAfterRestart = new SharedWorldVoteInbox(pcs[0]);
        Require(inboxAfterRestart.Status(profile.Id).Votes == 1 &&
            !inboxAfterRestart.Status(profile.Id).MajorityReached,
            "reopened recovery status lost or overstated the first vote");
        Require(inboxAfterRestart.AcceptVote(profile.Id, proposalHash, vote0).Votes == 1,
            "candidate lost or duplicated a vote on restart");
        Require(inboxAfterRestart.AcceptVote(profile.Id, proposalHash, vote1,
            () => false).Code == "VoteRejected" &&
            new WorldAuthorityStore(pcs[0]).Read(profile.Id).Count == 0,
            "delayed majority completed after the Host became reachable");
        var hostingKeyName = WorldAuthorityStore.HostingKeyName(profile.Id);
        var hostingKey = pcs[0].LoadProtected(hostingKeyName)!;
        pcs[0].DeleteProtected(hostingKeyName);
        Require(!inboxAfterRestart.AcceptVote(profile.Id, proposalHash, vote1).Ok &&
            new WorldAuthorityStore(pcs[0]).Read(profile.Id).Count == 0 &&
            inboxAfterRestart.Challenge(profile.Id, proposalHash, ids[1]) is null,
            "a candidate without its protected hosting key accepted a majority or served a challenge");
        pcs[0].SaveProtected(hostingKeyName, hostingKey);
        RequireThrows<InvalidOperationException>(() => inboxAfterRestart.AcceptVote(profile.Id,
            proposalHash, vote1, stopAfterAppendForChecks: true),
            "append-before-bind interruption fixture did not stop at the intended point");
        var decision = new WorldAuthorityStore(pcs[0]).Read(profile.Id).Single();
        Require(new WorldAuthorityStore(pcs[0]).LocalAuthorizedHead(profile.Id) is null &&
            new WorldAuthorityStore(pcs[0]).Fenced(profile.Id,
                Convert.ToBase64String(keys[0].ExportSubjectPublicKeyInfo()), out _),
            "an unbound appended majority allowed Start before reconciliation");
        pcs[0].DeleteProtected(hostingKeyName);
        Require(new SharedWorldVoteInbox(pcs[0]).Status(profile.Id, ids[0]).State == "ObservedMajority" &&
            new WorldAuthorityStore(pcs[0]).LocalAuthorizedHead(profile.Id) is null,
            "a restarted candidate without its hosting key claimed local authority");
        pcs[0].SaveProtected(hostingKeyName, hostingKey);
        var quorumResult = new WorldAuthorityVoteResult(true, "MajorityRecorded", 2, 2, decision);
        Require(quorumResult.Decision!.Proposal.Schema == 2 &&
            quorumResult.Decision.Proposal.SuccessorBinding?.DevicePublicKey ==
                Convert.ToBase64String(keys[0].ExportSubjectPublicKeyInfo()) &&
            quorumResult.Decision.Proposal.CandidatePublicKey ==
                new WorldAuthorityStore(pcs[0]).PrepareLocalHostingKey(profile.Id),
            "majority did not bind the candidate PC to its separate hosting key");
        var decidedStatus = new SharedWorldVoteInbox(pcs[0]).Status(profile.Id, ids[0]);
        Require(decidedStatus.State == "MajorityRecorded" && decidedStatus.MajorityReached &&
            decidedStatus.Votes == 2 && decidedStatus.Required == 2 &&
            decidedStatus.CandidateDeviceId == offer.Proposal.ProposerDeviceId &&
            decidedStatus.ProposalHash == proposalHash &&
            decidedStatus.AuthorityHeadHash == quorumResult.Decision!.RecordHash &&
            inboxAfterRestart.Armed(profile.Id) is null,
            "reopened recovery status did not reconcile the exact durable majority head");
        Require(new WorldAuthorityStore(pcs[0]).LocalAuthorizedHead(profile.Id)?.RecordHash ==
            decision.RecordHash && new WorldAuthorityStore(pcs[0]).Read(profile.Id).Count == 1,
            "reconciliation did not bind the existing majority without another authority head");
        var bindingName = $"authority-host-{profile.Id:N}.protected";
        var originalBinding = pcs[0].LoadProtected(bindingName)!;
        pcs[0].DeleteProtected(bindingName);
        Require(new SharedWorldVoteInbox(pcs[0]).Status(profile.Id, ids[0]).State == "MajorityRecorded" &&
            new WorldAuthorityStore(pcs[0]).Read(profile.Id).Count == 1,
            "a missing local binding was not repaired from the exact current majority");
        pcs[0].SaveProtected(bindingName, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            groupId = roster.GroupId,
            recordHash = quorumResult.Decision!.RecordHash,
            deviceId = ids[1],
            publicKey = Convert.ToBase64String(keys[1].ExportSubjectPublicKeyInfo())
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Require(new SharedWorldVoteInbox(pcs[0]).Status(profile.Id, ids[1]).State == "ObservedMajority",
            "another device claimed this PC's majority");
        Require(new SharedWorldVoteInbox(pcs[0]).Status(profile.Id, ids[0]).State == "MajorityRecorded",
            "a mismatched local binding was not repaired from the exact signed majority");
        pcs[0].SaveProtected(bindingName, originalBinding);
        new WorldAuthorityStore(pcs[1]).AppendReceived(quorumResult.Decision!, profile.Id,
            roster.GroupId, roster.OwnerPublicKey);
        var observed = new SharedWorldVoteInbox(pcs[1]).Status(profile.Id, ids[1]);
        Require(observed.State == "ObservedMajority" && observed.MajorityReached &&
            observed.CandidateDeviceId == quorumResult.Decision!.Proposal.ProposerDeviceId &&
            observed.CandidateDeviceId == ids[0] && observed.Votes == 2,
            "an observing Friend PC claimed another candidate's signed majority");
        using (var historyObserver = Data("quorum-history-observer"))
        {
            var historyStore = new WorldAuthorityStore(historyObserver);
            historyStore.AppendReceived(quorumResult.Decision!, profile.Id,
                roster.GroupId, roster.OwnerPublicKey);
            var receiptDraft = new SharedWorldReceipt(1, roster.GroupId, profile.Id,
                version.VersionHash, ids[2], roster.Epoch, roster.Revision, Guid.NewGuid(), "");
            var receipt = receiptDraft with
            {
                Signature = Convert.ToBase64String(keys[2].SignData(
                SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256))
            };
            var plannedChild = shares.SignPlannedHandoff(roster, version, receipt, ids[2],
                $"https://127.0.0.1:{candidatePort + 1}", 2, quorumResult.Decision!.RecordHash);
            historyStore.AppendReceived(plannedChild, profile.Id, roster.GroupId,
                roster.OwnerPublicKey);
            var historical = new SharedWorldVoteInbox(historyObserver).Status(profile.Id, ids[1]);
            Require(historical.State == "HistoricalRecovery" && !historical.MajorityReached &&
                historical.Votes == 0 && historical.CandidateDeviceId is null &&
                historyStore.Read(profile.Id).Count == 2,
                "an old quorum was presented as current after a planned child authority");
        }
        Require(inboxAfterRestart.Challenge(profile.Id, proposalHash, ids[1]) is null &&
            !inboxAfterRestart.HasArmedOffer(candidateAddress),
            "a completed offer remained reachable for another vote");
        RequireThrows<InvalidDataException>(() => inboxAfterRestart.Arm(offer, vaults[0]),
            "a completed offer reused its old authority epoch");
        var childOffer = SharedWorldElection.PrepareOffer(losses[0], vaults[0], roster, floor,
            ids[0], keys[0], candidateAddress, candidatePin, new WorldAuthorityStore(pcs[0]));
        Require(inboxAfterRestart.Arm(childOffer, vaults[0]).Proposal.Epoch ==
            offer.Proposal.Epoch + 1, "a signed child epoch could not replace a completed offer");
        inboxAfterRestart.Retire(profile.Id);
        Require(!inboxAfterRestart.HasArmedOffer(candidateAddress) &&
            inboxAfterRestart.Armed(profile.Id) is null,
            "withdrawal or Host return left a candidate offer armed");
        Require(new WorldAuthorityStore(pcs[0]).Fenced(profile.Id,
            Convert.ToBase64String(keys[0].ExportSubjectPublicKeyInfo()), out _) == false,
            "signed successor stayed fenced after proving its local PC identity");
        var accepted = SharedWorldElection.ConfirmQuorum(offer, [vote0, vote1],
            new WorldAuthorityStore(pcs[0]));
        Require(WorldAuthorityTrust.Verify(accepted) &&
            new WorldAuthorityStore(pcs[0]).Read(profile.Id).Count == 1,
            "two of three designated voters did not produce a durable signed decision");
        Require(separate.Read(profile.Id).Single().BranchHash == separateBranch.BranchHash,
            "majority authority overwrote the warned separate history");
        var fakeReceipt = offer with
        {
            CandidateReceipt = offer.CandidateReceipt with
            { VersionHash = new string('0', 64) }
        };
        RequireThrows<InvalidDataException>(() => SharedWorldElection.Vote(losses[2], vaults[2],
            floor, roster.OwnerPublicKey, fakeReceipt, ids[2], keys[2],
            new WorldAuthorityStore(pcs[2])), "a mismatched candidate copy receipt was accepted");
        RequireThrows<InvalidDataException>(() => SharedWorldElection.Vote(losses[2], vaults[2],
            floor, roster.OwnerPublicKey, offer with { CandidateTlsFingerprint = new string('B', 64) },
            ids[2], keys[2], new WorldAuthorityStore(pcs[2])),
            "an unsigned change to the candidate TLS pin was accepted");
        var competing = SharedWorldElection.PrepareOffer(losses[0], vaults[0], roster, floor,
            ids[0], keys[0], $"https://127.0.0.1:{candidatePort + 1}", candidatePin,
            new WorldAuthorityStore(pcs[2]));
        RequireThrows<InvalidDataException>(() => SharedWorldElection.Vote(losses[1], vaults[1],
            floor, roster.OwnerPublicKey, competing, ids[1], keys[1],
            new WorldAuthorityStore(pcs[1])), "a second vote for the same parent and epoch was accepted");
        File.WriteAllText(SharedWorldService.SafeChild(Path.Combine(vaults[2], version.VersionHash,
            SharedWorldService.PayloadDirectory), version.Files[0].Path), "tampered");
        RequireThrows<InvalidDataException>(() => SharedWorldElection.Vote(losses[2], vaults[2],
            floor, roster.OwnerPublicKey, offer, ids[2], keys[2],
            new WorldAuthorityStore(pcs[2])), "a tampered local copy could vote");
        LocalData CloneRecoveryPc(LocalData source, int index)
        {
            var clone = Data("quorum-restore-pc-" + index);
            foreach (var directory in Directory.EnumerateDirectories(source.RootPath, "*",
                         SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(clone.RootPath,
                    Path.GetRelativePath(source.RootPath, directory)));
            foreach (var file in Directory.EnumerateFiles(source.RootPath, "*",
                         SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).Equals("host.lock", StringComparison.OrdinalIgnoreCase))
                    continue;
                var target = Path.Combine(clone.RootPath,
                    Path.GetRelativePath(source.RootPath, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
            return clone;
        }

        // A different signed child can advance the head while the first PC's
        // child offer and companion listener remain armed. Keep both histories.
        inboxAfterRestart.Arm(childOffer, vaults[0]);
        var staleHash = WorldAuthorityTrust.ProposalHash(childOffer.Proposal);
        var staleChallenge = inboxAfterRestart.Challenge(profile.Id, staleHash, ids[1])
            ?? throw new Exception("child candidate challenge was unavailable before supersession");
        var staleDraft = new WorldAuthorityOfferRequest(1, roster.GroupId, profile.Id,
            staleHash, ids[1], Convert.ToBase64String(keys[1].ExportSubjectPublicKeyInfo()),
            staleChallenge.Nonce, "");
        var staleRequest = staleDraft with
        {
            Signature = Convert.ToBase64String(keys[1].SignData(
            SharedWorldVoteInbox.RequestBasis(staleDraft), HashAlgorithmName.SHA256))
        };
        var staleVote = SharedWorldElection.Vote(losses[0], vaults[0], floor,
            roster.OwnerPublicKey, childOffer, ids[0], keys[0], new WorldAuthorityStore(pcs[0]));
        new WorldAuthorityStore(pcs[2]).AppendReceived(quorumResult.Decision!, profile.Id,
            roster.GroupId, roster.OwnerPublicKey);
        var otherAddress = $"https://127.0.0.1:{candidatePort + 1}";
        var otherChild = SharedWorldElection.PrepareOffer(losses[1], vaults[1], roster, floor,
            ids[1], keys[1], otherAddress, candidatePin, new WorldAuthorityStore(pcs[1]));
        var otherVote1 = SharedWorldElection.Vote(losses[1], vaults[1], floor,
            roster.OwnerPublicKey, otherChild, ids[1], keys[1], new WorldAuthorityStore(pcs[1]));
        // Repair only the disposable third PC's tampered vault file before a
        // valid child vote; the earlier tamper rejection remains covered.
        File.WriteAllBytes(SharedWorldService.SafeChild(Path.Combine(vaults[2], version.VersionHash,
            SharedWorldService.PayloadDirectory), version.Files[0].Path),
            shares.ReadChunk(version, 0, 0));
        var otherVote2 = SharedWorldElection.Vote(losses[2], vaults[2], floor,
            roster.OwnerPublicKey, otherChild, ids[2], keys[2], new WorldAuthorityStore(pcs[2]));
        var staleListener = new CompanionServer(pcs[0], candidateManager,
            new PairingService(pcs[0]), Games(pcs[0]),
            new ServerLogService(pcs[0], candidateManager), modeGate, candidatePort + 2,
            recoveryLossProbe: (_, _) => Task.FromResult(losses[0].MayPropose),
            recoveryLossCurrent: _ => losses[0].MayPropose);
        try
        {
            await staleListener.SyncAsync();
            Require(staleListener.ListenerState == CompanionListenerStates.Listening,
                "child candidate listener did not open before authority advanced");
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && HostIdentity.Fingerprint(certificate) == candidatePin
            };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(candidateAddress + "/") };
            var route = $"api/companion/servers/{profile.Id}/shared-world/recovery/{staleHash}";
            var next = SharedWorldElection.ConfirmQuorum(otherChild, [otherVote1, otherVote2],
                new WorldAuthorityStore(pcs[0]));
            Require(next.Proposal.ParentAuthorityHash == quorumResult.Decision!.RecordHash &&
                new WorldAuthorityStore(pcs[0]).Read(profile.Id).Count == 2,
                "different signed child authority did not supersede the armed offer");
            var after = new SharedWorldVoteInbox(pcs[0]).Status(profile.Id, ids[0]);
            Require(after.State == "ObservedMajority" && after.CandidateDeviceId == ids[1] &&
                after.Votes == 2 && after.MajorityReached &&
                after.ProposalHash == WorldAuthorityTrust.ProposalHash(next.Proposal) &&
                after.AuthorityHeadHash == next.RecordHash &&
                inboxAfterRestart.Armed(profile.Id) is null &&
                !inboxAfterRestart.HasArmedOffer(candidateAddress),
                "stale candidate offer or older quorum remained current after child authority");
            RequireThrows<InvalidDataException>(() => inboxAfterRestart.Arm(childOffer, vaults[0]),
                "preparing the same stale signed offer returned a shareable code");
            Require(inboxAfterRestart.Challenge(profile.Id, staleHash, ids[1]) is null &&
                inboxAfterRestart.ReadOffer(staleRequest) is null &&
                !inboxAfterRestart.AcceptVote(profile.Id, staleHash, staleVote).Ok,
                "stale candidate inbox served an offer or accepted a vote");
            using var deniedChallenge = await client.GetAsync($"{route}/challenge/{ids[1]}");
            using var deniedOffer = await client.PostAsJsonAsync(route + "/offer", staleRequest);
            using var deniedVote = await client.PostAsJsonAsync(route + "/vote", staleVote);
            Require(deniedChallenge.StatusCode == HttpStatusCode.NotFound &&
                deniedOffer.StatusCode == HttpStatusCode.NotFound &&
                deniedVote.StatusCode == HttpStatusCode.NotFound,
                "candidate companion served a superseded challenge, offer, or vote route");
            // A deliberately double-signed fixture ballot creates competing
            // heads. The read projection must refuse to pick either as current.
            var forkDraft = new WorldAuthorityVote(1, staleHash, ids[1],
                Convert.ToBase64String(keys[1].ExportSubjectPublicKeyInfo()), "");
            var forkVote = forkDraft with
            {
                Signature = Convert.ToBase64String(keys[1].SignData(
                WorldAuthorityTrust.VoteBasis(forkDraft), HashAlgorithmName.SHA256))
            };
            SharedWorldElection.ConfirmQuorum(childOffer, [staleVote, forkVote],
                new WorldAuthorityStore(pcs[0]));
            var conflict = new SharedWorldVoteInbox(pcs[0]).Status(profile.Id, ids[0]);
            Require(conflict.State == "HistoryReviewRequired" && !conflict.MajorityReached &&
                conflict.CandidateDeviceId is null && inboxAfterRestart.Armed(profile.Id) is null &&
                new WorldAuthorityStore(pcs[0]).Read(profile.Id).Count == 3,
                "competing verified heads were reduced to a current majority or erased");
        }
        finally { await staleListener.StopAsync(); }
    }
    finally
    {
        foreach (var pc in pcs) pc.Dispose();
        foreach (var key in keys) key.Dispose();
    }
});
await Check("quorum successor restores and approved PC pulls its next save", async () =>
{
    var nonceClock = new ManualTimeProvider(DateTimeOffset.UtcNow);
    var nonces = new SharedWorldEnrollmentNonces(nonceClock);
    var expiryDevice = Guid.NewGuid();
    var expiryProfile = Guid.NewGuid();
    var expiredNonce = nonces.Issue(expiryDevice, expiryProfile);
    nonceClock.Advance(TimeSpan.FromMinutes(6));
    Require(!nonces.Consume(expiryDevice, expiryProfile, expiredNonce) &&
        !nonces.Consume(expiryDevice, expiryProfile, expiredNonce),
        "expired or replayed successor challenge was accepted");
    using var host = Data("quorum-restore-host");
    var profile = Profile("quorum-restore", "quorum-restore-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "verified quorum restore world");
    var backup = new WorldBackupService(host, TimeProvider.System);
    var shares = new SharedWorldService(host, backup);
    var keys = Enumerable.Range(0, 3).Select(_ => ECDsa.Create(ECCurve.NamedCurves.nistP256)).ToArray();
    var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
    var revokedId = Guid.NewGuid();
    using var revokedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var pcs = new List<LocalData>();
    try
    {
        var roster = shares.PublishRoster(profile, ids.Select((id, index) =>
            new SharedWorldRosterMember(id,
                Convert.ToBase64String(keys[index].ExportSubjectPublicKeyInfo()),
                new SharedWorldGrants(true, true, true), false)).Append(
            new SharedWorldRosterMember(revokedId,
                Convert.ToBase64String(revokedKey.ExportSubjectPublicKeyInfo()),
                new SharedWorldGrants(Receive: true), true)).ToArray());
        var checkpoint = backup.Create(profile, BackupKinds.Rolling);
        Require(checkpoint.Ok && checkpoint.Backup is not null, "quorum restore backup failed");
        var version = shares.PublishAfterStop(profile, checkpoint.Backup!.Id).Version
            ?? throw new Exception("quorum restore version missing");
        var floor = new SharedRosterFloor(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature);
        for (var i = 0; i < 3; i++) pcs.Add(Data("quorum-restore-pc-" + i));
        var vaults = pcs.Select((pc, index) =>
        {
            var root = Path.Combine(pc.RootPath, "received-shared-worlds", ids[index].ToString("N"),
                profile.Id.ToString("N"));
            var payload = Path.Combine(root, version.VersionHash, SharedWorldService.PayloadDirectory);
            Directory.CreateDirectory(payload);
            for (var fileIndex = 0; fileIndex < version.Files.Count; fileIndex++)
                File.WriteAllBytes(SharedWorldService.SafeChild(payload, version.Files[fileIndex].Path),
                    shares.ReadChunk(version, fileIndex, 0));
            File.WriteAllBytes(Path.Combine(root, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return root;
        }).ToArray();
        var candidatePort = FreePort();
        var candidateAddress = $"https://127.0.0.1:{candidatePort}";
        using var candidateCertificate = new HostIdentity(pcs[0]).Ensure(candidateAddress);
        var candidatePin = HostIdentity.Fingerprint(candidateCertificate);
        long ticks = 0;
        var losses = Enumerable.Range(0, 3).Select(_ =>
            new SharedWorldHostLoss(() => ticks, TimeSpan.TicksPerSecond)).ToArray();
        foreach (var loss in losses) loss.Observe(HostReachabilityObservation.TransportFailure);
        ticks += TimeSpan.FromMinutes(2).Ticks;
        foreach (var loss in losses) loss.Observe(HostReachabilityObservation.TransportFailure);
        var offer = SharedWorldElection.PrepareOffer(losses[0], vaults[0], roster, floor,
            ids[0], keys[0], candidateAddress, candidatePin, new WorldAuthorityStore(pcs[0]));
        var vote0 = SharedWorldElection.Vote(losses[0], vaults[0], floor, roster.OwnerPublicKey,
            offer, ids[0], keys[0], new WorldAuthorityStore(pcs[0]));
        var vote1 = SharedWorldElection.Vote(losses[1], vaults[1], floor, roster.OwnerPublicKey,
            offer, ids[1], keys[1], new WorldAuthorityStore(pcs[1]));
        pcs[0].SaveProtected($"shared-world-pc-signing-{ids[0]:N}.protected",
            keys[0].ExportPkcs8PrivateKey());
        var firstInbox = new SharedWorldVoteInbox(pcs[0]);
        firstInbox.Arm(offer, vaults[0]);
        Require(firstInbox.AcceptVote(profile.Id, WorldAuthorityTrust.ProposalHash(offer.Proposal),
            vote0).Code == "VoteRecorded", "first quorum restore ballot was not stored");
        var accepted = firstInbox.AcceptVote(profile.Id,
            WorldAuthorityTrust.ProposalHash(offer.Proposal), vote1).Decision
            ?? throw new Exception("first quorum restore majority was not recorded");
        new WorldAuthorityStore(pcs[1]).AppendReceived(accepted, profile.Id,
            roster.GroupId, roster.OwnerPublicKey);
        var directAddress = $"https://192.0.2.10:{candidatePort}";
        using var directCertificate = new HostIdentity(pcs[0]).Ensure(directAddress);
        var directPin = HostIdentity.Fingerprint(directCertificate);
        var directOffer = SharedWorldElection.PrepareOffer(losses[0], vaults[0], roster, floor,
            ids[0], keys[0], directAddress, directPin, new WorldAuthorityStore(pcs[0]));
        var directInbox = new SharedWorldVoteInbox(pcs[0]);
        directInbox.Arm(directOffer, vaults[0]);
        var directHash = WorldAuthorityTrust.ProposalHash(directOffer.Proposal);
        var directVote0 = SharedWorldElection.Vote(losses[0], vaults[0], floor,
            roster.OwnerPublicKey, directOffer, ids[0], keys[0], new WorldAuthorityStore(pcs[0]));
        var directVote1 = SharedWorldElection.Vote(losses[1], vaults[1], floor,
            roster.OwnerPublicKey, directOffer, ids[1], keys[1], new WorldAuthorityStore(pcs[1]));
        Require(directInbox.AcceptVote(profile.Id, directHash, directVote0).Code == "VoteRecorded",
            "new direct-IP candidate lost the first vote");
        var directDecision = directInbox.AcceptVote(profile.Id, directHash, directVote1).Decision
            ?? throw new Exception("direct-IP majority did not sign the candidate");
        var directSettings = pcs[0].LoadSettings();
        directSettings.CompanionEndpoint = directAddress;
        directSettings.CompanionPort = candidatePort;
        directSettings.CompanionListeningEnabled = true;
        pcs[0].SaveSettings(directSettings);
        var successorManager = Manager(pcs[0]);
        var successorSetup = new TakeoverLocalSetup(fixture, version.PortableSetup.GameVersion,
            [], true, candidatePort, FreePort());
        var successorRequest = new SuccessorRestoreRequest(directDecision.RecordHash,
            successorSetup, "Quorum fixture", "Quorum fixture");
        Require((await successorManager.SuccessorRestoreStatusAsync(profile.Id)).Staged,
            "signed majority was not available as a verified restore candidate");
        var exactFile = SharedWorldService.SafeChild(Path.Combine(vaults[0], version.VersionHash,
            SharedWorldService.PayloadDirectory), version.Files[0].Path);
        var exactBytes = File.ReadAllBytes(exactFile);
        File.WriteAllText(exactFile, "tampered candidate copy");
        Require(!(await successorManager.RestoreSharedSuccessorAsync(profile.Id,
            successorRequest)).Ok, "candidate restored a tampered vault copy");
        File.WriteAllBytes(exactFile, exactBytes);
        Require((await successorManager.RestoreSharedSuccessorAsync(profile.Id,
            successorRequest, freeBytes: _ => 0)).Code == "LocalSetupIncomplete",
            "candidate ignored the 1 GiB free-space reserve");
        Require((await successorManager.RestoreSharedSuccessorAsync(profile.Id,
            successorRequest with { Setup = successorSetup with { NewPasswordConfigured = false } })).Code ==
            "LocalSetupIncomplete", "candidate accepted an unset new password");
        Require((await successorManager.RestoreSharedSuccessorAsync(profile.Id,
            successorRequest with
            {
                Setup = successorSetup with
                {
                    EnabledAddOns = [new SharedWorldPortableAddOn("wrong", "1", "1", "Factorio mod")]
                }
            })).Code ==
            "LocalSetupIncomplete", "candidate accepted wrong add-ons");
        Require((await successorManager.RestoreSharedSuccessorAsync(profile.Id,
            successorRequest with { Setup = successorSetup with { ControlPort = candidatePort + 1 } })).Code ==
            "ControlPortMismatch", "candidate accepted a wrong control port");
        var quorumRestored = await successorManager.RestoreSharedSuccessorAsync(profile.Id,
            successorRequest);
        Require(quorumRestored.Ok && (await successorManager.StartAsync(profile.Id)).Code ==
            "SuccessorChecksPending", $"majority copy did not restore fenced: {quorumRestored.Code} {quorumRestored.Message}");
        Require((await successorManager.FinishSharedSuccessorAsync(profile.Id,
            new SuccessorFinishRequest(directDecision.RecordHash, successorSetup))).Code ==
            "SuccessorChecksPending", "candidate finished without a second PC control route check");
        var routeNonce2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge2 = SharedWorldRouteTrust.SignChallenge(directDecision,
            routeNonce2, ids[1], keys[1]);
        var routeProof2 = await successorManager.SignSuccessorRouteProofAsync(profile.Id,
            directDecision.RecordHash, challenge2, directPin);
        Require(SharedWorldRouteTrust.Verify(routeProof2, directDecision,
            routeNonce2, directPin), "schema-2 hosting key could not prove the control route");
        Require((await successorManager.FinishSharedSuccessorAsync(profile.Id,
            new SuccessorFinishRequest(directDecision.RecordHash, successorSetup))).Code ==
            "SuccessorChecksPending" &&
            await successorManager.ConfirmSuccessorRouteAsync(profile.Id,
                directDecision.RecordHash,
                new SharedWorldRouteConfirmation(challenge2, routeProof2!), directPin),
            "schema-2 route challenge was accepted without the Friend return confirmation");
        var quorumFinished = await successorManager.FinishSharedSuccessorAsync(profile.Id,
            new SuccessorFinishRequest(directDecision.RecordHash, successorSetup));
        Require(quorumFinished.Code == "ReadyForManualStart",
            $"majority successor finish failed: {quorumFinished.Code} {quorumFinished.Message}");
        var routeObservationName = $"successor-route-{profile.Id:N}.protected";
        var currentRouteBytes = pcs[0].LoadProtected(routeObservationName)!;
        var currentRoute = JsonSerializer.Deserialize<SuccessorRouteObservation>(currentRouteBytes)!;
        pcs[0].SaveProtected(routeObservationName, JsonSerializer.SerializeToUtf8Bytes(
            currentRoute with { ObservedUtc = DateTimeOffset.UtcNow.AddHours(-2) }));
        Require((await successorManager.StartAsync(profile.Id)).Code == "SuccessorChecksPending",
            "an expired control route observation authorized Start");
        pcs[0].SaveProtected(routeObservationName, currentRouteBytes);
        var quorumStarted = await successorManager.StartAsync(profile.Id);
        Require(quorumStarted.Ok,
            $"majority successor manual Start failed: {quorumStarted.Code} {quorumStarted.Message}");
        File.WriteAllText(Path.Combine(pcs[0].NewWorldDirectory(profile.Id), "world.dat"),
            "majority saved change");
        Require((await successorManager.StopAsync(profile.Id)).Ok,
            "majority successor failed graceful Stop");
        var majorityLatest = (await successorManager.SharedWorldReadAsync(profile.Id)).Status.Latest;
        var majorityRestart = await successorManager.StartAsync(profile.Id);
        Require(majorityLatest is { Number: 2 } && majorityRestart.Ok,
            $"majority successor failed signed save continuity or later Start: version={majorityLatest?.Number}, {majorityRestart.Code} {majorityRestart.Message}");
        Require((await successorManager.StopAsync(profile.Id)).Ok,
            "majority successor could not stop its later manual fixture run");
        new WorldAuthorityStore(pcs[1]).AppendReceived(directDecision, profile.Id,
            roster.GroupId, roster.OwnerPublicKey);
        pcs[1].SaveProtected($"shared-world-pc-signing-{ids[1]:N}.protected",
            keys[1].ExportPkcs8PrivateKey());
        var sourceConfig = new FriendConfiguration
        {
            DeviceId = ids[1],
            HostId = Guid.NewGuid(),
            Endpoint = $"https://127.0.0.1:{FreePort()}",
            Fingerprint = new string('A', 64),
            Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            ConsentedSharedWorldProfiles = [profile.Id],
            SharedWorldSigningKeys = new() { [profile.Id] = roster.OwnerPublicKey },
            ApprovedSharedWorldGroups = new() { [profile.Id] = roster.GroupId },
            SharedRosterFloors = new() { [profile.Id] = floor }
        };
        pcs[1].SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(sourceConfig));
        HttpClient RedirectedSuccessorClient(string endpoint, IEnumerable<string> fingerprints)
        {
            var pins = fingerprints.ToHashSet(StringComparer.Ordinal);
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, candidatePort, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            handler.SslOptions.RemoteCertificateValidationCallback = (_, seen, _, _) =>
                seen is not null && pins.Contains(Convert.ToHexString(SHA256.HashData(seen.GetRawCertData())));
            return new HttpClient(handler)
            {
                BaseAddress = new Uri(endpoint + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
        }
        using var successorModeGate = new SemaphoreSlim(1, 1);
        var successorPairing = new PairingService(pcs[0]);
        var successorListener = new CompanionServer(pcs[0], successorManager, successorPairing,
            Games(pcs[0]), new ServerLogService(pcs[0], successorManager),
            successorModeGate, candidatePort + 2);
        try
        {
            await successorListener.SyncAsync();
            Require(successorListener.Active, "the restored successor listener was not available");
            using var rawClient = RedirectedSuccessorClient(directAddress, [directPin]);
            var path = $"api/companion/servers/{profile.Id}/shared-world/successor-enrollment";
            using var revokedChallenge = await rawClient.GetAsync(
                $"{path}/{directDecision.RecordHash}/{revokedId}");
            Require(revokedChallenge.StatusCode == HttpStatusCode.NotFound,
                "revoked signed member received a successor enrollment challenge");
            using var challengeResponse = await rawClient.GetAsync(
                $"{path}/{directDecision.RecordHash}/{ids[1]}");
            var challenge = await challengeResponse.Content.ReadFromJsonAsync<SuccessorEnrollmentChallenge>()
                ?? throw new Exception("approved receiver did not receive a successor challenge");
            var goodDraft = new SuccessorEnrollmentRequest(ids[1], challenge.Nonce,
                directDecision.RecordHash, roster.GroupId, roster.OwnerPublicKey,
                directAddress, directPin,
                Convert.ToBase64String(keys[1].ExportSubjectPublicKeyInfo()), "");
            var wrong = goodDraft with
            {
                Signature = Convert.ToBase64String(revokedKey.SignData(
                SharedWorldSuccessorEnrollment.Basis(goodDraft), HashAlgorithmName.SHA256))
            };
            using var wrongResponse = await rawClient.PostAsJsonAsync(path, wrong);
            using var replayAfterWrong = await rawClient.PostAsJsonAsync(path,
                goodDraft with
                {
                    Signature = Convert.ToBase64String(keys[1].SignData(
                    SharedWorldSuccessorEnrollment.Basis(goodDraft), HashAlgorithmName.SHA256))
                });
            Require(wrongResponse.StatusCode == HttpStatusCode.Forbidden &&
                replayAfterWrong.StatusCode == HttpStatusCode.Forbidden &&
                successorPairing.Views().All(item => item.Id != ids[1]),
                "wrong identity or consumed challenge created a successor bearer");
            using var friend = new FriendService(pcs[1], RedirectedSuccessorClient);
            var wrongPin = await friend.EnrollWithSuccessorAsync(profile.Id,
                directDecision.RecordHash, new string('F', 64), CancellationToken.None);
            Require(!wrongPin.Ok && successorPairing.Views().All(item => item.Id != ids[1]) &&
                friend.View().Connections?.Count == 1,
                "an unpinned successor created Friend access");
            var joined = await friend.EnrollWithSuccessorAsync(profile.Id,
                directDecision.RecordHash, directPin, CancellationToken.None);
            var successorConnection = friend.View();
            var nextConfig = pcs[1].LoadProtectedJson<FriendConfiguration>(
                $"friend-{successorConnection.ConnectionId:N}.protected");
            Require(joined.Ok && nextConfig is not null && nextConfig.DeviceId == ids[1] &&
                nextConfig.Endpoint == directAddress && nextConfig.Credential != sourceConfig.Credential &&
                nextConfig.SharedWorldSigningKeys.GetValueOrDefault(profile.Id) == roster.OwnerPublicKey &&
                pcs[1].HasProtected("friend.protected") && successorConnection.Connections?.Count == 2,
                $"approved receiver could not join successor: {joined.Code} {joined.Message}");
            var verifiedConfig = nextConfig ?? throw new Exception("Successor connection was not saved.");
            var authenticated = successorPairing.Authenticate(ids[1], verifiedConfig.Credential,
                out var enrolledDevice);
            Require(authenticated.Ok && enrolledDevice?.AssignedProfileIds?.Contains(profile.Id) == true,
                $"successor bearer did not retain the original roster assignment: {authenticated.Code}");
            Require(new PairingService(pcs[0]).Authenticate(ids[1], verifiedConfig.Credential,
                out _).Ok, "successor bearer was not durable across pairing restart");
            Require(successorConnection.Profiles.Any(item => item.Id == profile.Id),
                $"successor heartbeat did not expose the inherited world: {successorConnection.ConnectionCode} {successorConnection.Detail}");
            Require((await successorManager.SharedWorldRosterAsync(profile.Id))?.Signature == roster.Signature &&
                !successorPairing.SharedRosterDirty(profile.Id),
                "successor enrollment changed signed membership");
            var receiverRecords = new WorldAuthorityStore(pcs[1]).Read(profile.Id);
            var receiverHeads = WorldAuthorityTrust.EffectiveHeads(receiverRecords);
            var successorLatest = (await successorManager.SharedWorldReadAsync(profile.Id)).Status.Latest!;
            Require(FriendLink.AuthorizedVersionSignerForRecords(roster.OwnerPublicKey,
                successorLatest, receiverRecords),
                $"successor version signer was not authorized: heads={receiverHeads.Length}, " +
                $"headSchema={receiverHeads.FirstOrDefault()?.Proposal.Schema}, latest={successorLatest.Number}, " +
                $"headVersion={receiverHeads.FirstOrDefault()?.Version.Number}, " +
                $"signer={successorLatest.SigningPublicKey == receiverHeads.FirstOrDefault()?.Proposal.CandidatePublicKey}");
            var pulled = await friend.PullSharedWorldAsync(profile.Id);
            var afterPullConfig = JsonSerializer.Deserialize<FriendConfiguration>(
                pcs[1].LoadProtected($"friend-{successorConnection.ConnectionId:N}.protected")!)!;
            var afterPullRecords = new WorldAuthorityStore(pcs[1]).Read(profile.Id);
            Require(pulled.Ok && FriendLink.ReadReceivedLatest(vaults[1]) is { Number: > 1 } received &&
                File.ReadAllText(SharedWorldService.SafeChild(Path.Combine(vaults[1],
                    received.VersionHash, SharedWorldService.PayloadDirectory),
                    received.Files[0].Path)) == "majority saved change",
                $"approved receiver did not pull the successor's signed save: {pulled.Code} {pulled.Message}; " +
                $"pinned={afterPullConfig.SharedWorldSigningKeys.GetValueOrDefault(profile.Id)?.Substring(0, 12)}, " +
                $"owner={roster.OwnerPublicKey.Substring(0, 12)}, " +
                $"records={afterPullRecords.Count}, heads={WorldAuthorityTrust.EffectiveHeads(afterPullRecords).Length}");
            using var replay = await rawClient.PostAsJsonAsync(path, goodDraft with
            {
                Signature = Convert.ToBase64String(keys[1].SignData(
                SharedWorldSuccessorEnrollment.Basis(goodDraft), HashAlgorithmName.SHA256))
            });
            Require(replay.StatusCode == HttpStatusCode.Forbidden,
                "consumed enrollment challenge issued a second bearer");
            using var restartedFriend = new FriendService(pcs[1], RedirectedSuccessorClient);
            Require(restartedFriend.View().ConnectionId == successorConnection.ConnectionId &&
                restartedFriend.View().Connections?.Count == 2,
                "a Friend restart lost the successor or original Host connection");
            // A lost enrollment response can be retried with a fresh, one-use
            // challenge. The old successor bearer is rotated, never duplicated.
            using var retryChallengeResponse = await rawClient.GetAsync(
                $"{path}/{directDecision.RecordHash}/{ids[1]}");
            var retryChallenge = await retryChallengeResponse.Content
                .ReadFromJsonAsync<SuccessorEnrollmentChallenge>()
                ?? throw new Exception("successor retry challenge was missing");
            var retryDraft = goodDraft with { Nonce = retryChallenge.Nonce, Signature = "" };
            using var retryResponse = await rawClient.PostAsJsonAsync(path, retryDraft with
            {
                Signature = Convert.ToBase64String(keys[1].SignData(
                SharedWorldSuccessorEnrollment.Basis(retryDraft), HashAlgorithmName.SHA256))
            });
            var retryCredential = await retryResponse.Content.ReadFromJsonAsync<PairingCredential>();
            Require(retryResponse.IsSuccessStatusCode && retryCredential is not null &&
                retryCredential.Credential != verifiedConfig.Credential &&
                !successorPairing.Authenticate(ids[1], verifiedConfig.Credential, out _).Ok &&
                successorPairing.Authenticate(ids[1], retryCredential.Credential, out _).Ok &&
                (await successorManager.SharedWorldRosterAsync(profile.Id))?.Signature == roster.Signature,
                "fresh signed retry did not rotate only the successor bearer");
        }
        finally { await successorListener.StopAsync(); }
        host.SaveSettings(Settings(profile));
        new WorldAuthorityStore(host).AppendReceived(accepted, profile.Id,
            roster.GroupId, roster.OwnerPublicKey);
        new WorldAuthorityStore(host).AppendReceived(directDecision, profile.Id,
            roster.GroupId, roster.OwnerPublicKey);
        Require((await Manager(host).StartAsync(profile.Id)).Code == "SharedWorldAuthorityBlocked",
            "old Host return escaped the signed authority fence");
        var newerOffer = SharedWorldElection.PrepareOffer(losses[0], vaults[0], roster,
            floor, ids[0], keys[0], directAddress, directPin,
            new WorldAuthorityStore(pcs[0]));
        directInbox.Arm(newerOffer, vaults[0]);
        var newerHash = WorldAuthorityTrust.ProposalHash(newerOffer.Proposal);
        var newerVote0 = SharedWorldElection.Vote(losses[0], vaults[0], floor,
            roster.OwnerPublicKey, newerOffer, ids[0], keys[0],
            new WorldAuthorityStore(pcs[0]));
        new WorldAuthorityStore(pcs[2]).AppendReceived(accepted, profile.Id,
            roster.GroupId, roster.OwnerPublicKey);
        new WorldAuthorityStore(pcs[2]).AppendReceived(directDecision, profile.Id,
            roster.GroupId, roster.OwnerPublicKey);
        var newerVote1 = SharedWorldElection.Vote(losses[2], vaults[2], floor,
            roster.OwnerPublicKey, newerOffer, ids[2], keys[2],
            new WorldAuthorityStore(pcs[2]));
        directInbox.AcceptVote(profile.Id, newerHash, newerVote0);
        Require(directInbox.AcceptVote(profile.Id, newerHash, newerVote1).Code ==
            "MajorityRecorded" &&
            (await Manager(pcs[0]).StartAsync(profile.Id)).Code == "SuccessorChecksPending",
            "a new signed authority head did not re-fence the old restore marker");
    }
    finally
    {
        foreach (var pc in pcs) pc.Dispose();
        foreach (var key in keys) key.Dispose();
    }
});
await Check("linked authority still permits durable credential revoke and fences the world", async () =>
{
    using var data = Data("linked-authority-revoke");
    var profile = Profile("linked-authority-world", "linked-authority-world", FreePort());
    profile.Kind = "Fixture";
    profile.SharedSavesEnabled = true;
    data.SaveSettings(Settings(profile));
    var pairing = new PairingService(data);
    var invite = pairing.IssueServer(profile.Id, false, false,
        "https://127.0.0.1:5132", new string('A', 64), refresh: false);
    var credential = pairing.Activate(new PairingActivation(profile.Id, invite.Code,
        ServerScope: true));
    Require(credential is not null &&
        pairing.Authenticate(credential.DeviceId, credential.Credential, out _).Ok,
        "the disposable Friend credential was not paired before authority damage");
    profile.SharedSavesEnabled = false;
    data.SaveSettings(Settings(profile));
    var sharedRoot = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"));
    Directory.CreateDirectory(sharedRoot);
    var linkedTarget = Path.Combine(root, "linked-authority-target-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(linkedTarget);
    File.WriteAllText(Path.Combine(linkedTarget, "sentinel.txt"), "unchanged");
    CreateJunction(Path.Combine(sharedRoot, "authority"), linkedTarget);
    RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(data).HasState(profile.Id),
        "linked authority did not reproduce the damaged-state read failure");
    var damagedPairing = new PairingService(data);
    Require(damagedPairing.Revoke(credential!.DeviceId).Ok &&
        !damagedPairing.Authenticate(credential.DeviceId, credential.Credential, out _).Ok &&
        damagedPairing.SharedRosterDirty(profile.Id) &&
        File.ReadAllText(Path.Combine(linkedTarget, "sentinel.txt")) == "unchanged" &&
        Directory.EnumerateFileSystemEntries(linkedTarget).Count() == 1,
        "damaged authority prevented revoke or caused a write through its junction");
    data.Dispose();
    using var reopened = new LocalData(data.RootPath);
    var restartedPairing = new PairingService(reopened);
    Require(!restartedPairing.Authenticate(credential.DeviceId, credential.Credential, out _).Ok &&
        reopened.LoadPairingState().Devices.Single(item => item.Id == credential.DeviceId).Revoked &&
        restartedPairing.SharedRosterDirty(profile.Id) &&
        (await new HostManager(reopened, Games(reopened)).StartAsync(profile.Id)).Code ==
            "SharedWorldAuthorityBlocked",
        "restart lost the revoked credential or the conservative authority fence");
});

await Check("successor hosting key continues exact save lineage and stays bound after restart", async () =>
{
    using var ownerData = Data("lineage-owner");
    using var successorData = Data("lineage-successor");
    var owner = Profile("lineage-owner-world", "lineage-world", FreePort());
    owner.Kind = "Fixture";
    owner.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    owner.SharedSavesEnabled = true;
    File.WriteAllText(Path.Combine(owner.WorldDirectory, "world.dat"), "original");
    var ownerBackups = new WorldBackupService(ownerData, TimeProvider.System);
    var ownerShares = new SharedWorldService(ownerData, ownerBackups);
    using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var voter = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var deviceId = Guid.NewGuid();
    var voterId = Guid.NewGuid();
    var deviceKey = Convert.ToBase64String(device.ExportSubjectPublicKeyInfo());
    var voterKey = Convert.ToBase64String(voter.ExportSubjectPublicKeyInfo());
    var roster = ownerShares.PublishRoster(owner,
    [
        new SharedWorldRosterMember(deviceId, deviceKey,
            new SharedWorldGrants(Receive: true, EligibleHost: true, RecoveryVoter: true), false),
        new SharedWorldRosterMember(voterId, voterKey,
            new SharedWorldGrants(RecoveryVoter: true), false)
    ]);
    var ownerBackup = ownerBackups.Create(owner, BackupKinds.Rolling);
    Require(ownerBackup.Ok && ownerBackup.Backup is not null, "owner backup failed");
    var origin = ownerShares.PublishAfterStop(owner, ownerBackup.Backup!.Id).Version!;
    var initialOwnerVersion = origin;
    ownerData.SaveSettings(Settings(owner));
    ownerData.SavePairingState(new PairingPersistentState
    {
        Devices = [new PairedDevice
        {
            Id = voterId, ProfileId = owner.Id, AssignedProfileIds = [owner.Id],
            ServerPermissionOverrides = [], SaveReceiveProfileIds = [],
            SharedWorldPublicKey = voterKey,
            SharedWorldGrants = new() { [owner.Id] = new SharedWorldGrants(RecoveryVoter: true) }
        }]
    });
    var ownerPairing = new PairingService(ownerData);
    File.WriteAllText(Path.Combine(owner.WorldDirectory, "world.dat"), "owner next");
    var ownerNextBackup = ownerBackups.Create(owner, BackupKinds.Rolling);
    Require(ownerNextBackup.Ok && ownerNextBackup.Backup is not null,
        "owner backup for publication race failed");
    using var publishEntered = new ManualResetEventSlim();
    using var allowPublication = new ManualResetEventSlim();
    ownerShares.AfterGovernanceCheckForChecks = () =>
    {
        publishEntered.Set();
        if (!allowPublication.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("shared publication check did not resume");
    };
    var publicationTask = Task.Run(() => ownerShares.PublishAfterStop(owner, ownerNextBackup.Backup!.Id));
    Require(publishEntered.Wait(TimeSpan.FromSeconds(10)),
        "publication did not reach the governance check boundary");
    using var revokeEntered = new ManualResetEventSlim();
    var ownerRevokeTask = Task.Run(() =>
    {
        revokeEntered.Set();
        return ownerPairing.Revoke(voterId);
    });
    try
    {
        Require(revokeEntered.Wait(TimeSpan.FromSeconds(10)), "revoke did not start");
        await Task.Delay(100);
        Require(!ownerRevokeTask.IsCompleted,
            "revoke completed while publication could still return SharedSavePublished");
    }
    finally { allowPublication.Set(); }
    var publishedOwnerVersion = await publicationTask.WaitAsync(TimeSpan.FromSeconds(20));
    Require(publishedOwnerVersion.Code == "SharedSavePublished" && publishedOwnerVersion.Version is not null,
        "authorized publication did not finish before revocation");
    origin = publishedOwnerVersion.Version!;
    Require((await ownerRevokeTask.WaitAsync(TimeSpan.FromSeconds(20))).Ok &&
        ownerPairing.SharedRosterDirty(owner.Id) &&
        !ownerShares.PublishAfterStop(owner, ownerNextBackup.Backup!.Id).Ok,
        "publication succeeded after completed revocation");
    ownerShares.AfterGovernanceCheckForChecks = null;
    var successor = Profile("lineage-successor-world", owner.WorldId, FreePort());
    successor.Id = owner.Id;
    successor.Kind = owner.Kind;
    successor.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    successor.SharedSavesEnabled = true;
    var independent = Profile("lineage-independent-world", "independent-world", FreePort());
    independent.Kind = "Fixture";
    independent.SharedSavesEnabled = true;
    successorData.SaveSettings(Settings(successor, independent));
    File.WriteAllText(Path.Combine(successor.WorldDirectory, "world.dat"), "successor first");
    var successorStore = new WorldAuthorityStore(successorData);
    var hostKey = successorStore.PrepareLocalHostingKey(successor.Id);
    successorData.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected",
        device.ExportPkcs8PrivateKey());
    var unsigned = new WorldAuthorityProposal(2, roster.GroupId, owner.Id, 1, null,
        WorldAuthorityTrust.RosterHash(roster), origin.VersionHash, hostKey,
        "https://127.0.0.1:5132", "Quorum", deviceId, deviceKey, "");
    var bindingDraft = new WorldSuccessorBinding(deviceId, deviceKey, hostKey, "");
    var binding = bindingDraft with
    {
        Signature = Convert.ToBase64String(device.SignData(
        WorldAuthorityTrust.BindingBasis(unsigned, bindingDraft), HashAlgorithmName.SHA256))
    };
    unsigned = unsigned with { SuccessorBinding = binding };
    var proposal = unsigned with
    {
        Signature = Convert.ToBase64String(device.SignData(
        WorldAuthorityTrust.ProposalBasis(unsigned), HashAlgorithmName.SHA256))
    };
    var badBindingDraft = unsigned with
    {
        SuccessorBinding = binding with
        {
            Signature = Convert.ToBase64String(wrong.SignData(
        WorldAuthorityTrust.BindingBasis(unsigned, binding), HashAlgorithmName.SHA256))
        },
        Signature = ""
    };
    var badBindingProposal = badBindingDraft with
    {
        Signature = Convert.ToBase64String(device.SignData(
        WorldAuthorityTrust.ProposalBasis(badBindingDraft), HashAlgorithmName.SHA256))
    };
    Require(!WorldAuthorityTrust.VerifyProposal(badBindingProposal, roster),
        "a proposer signature hid an invalid enrolled-device binding");
    WorldAuthorityVote Vote(Guid id, ECDsa key, string publicKey)
    {
        var draft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(proposal), id, publicKey, "");
        return draft with
        {
            Signature = Convert.ToBase64String(key.SignData(
            WorldAuthorityTrust.VoteBasis(draft), HashAlgorithmName.SHA256))
        };
    }
    var recordDraft = new WorldAuthorityRecord(1, proposal, roster, origin,
        [Vote(deviceId, device, deviceKey), Vote(voterId, voter, voterKey)], null, "",
        VersionLineage: [initialOwnerVersion, origin]);
    var record = recordDraft with
    {
        RecordHash = WorldAuthorityTrust.Hash(
        WorldAuthorityTrust.RecordBasis(recordDraft))
    };
    Require(WorldAuthorityTrust.Verify(record), "signed device to hosting key proof failed");
    using (var scopedData = Data("lineage-scoped-revoke"))
    {
        scopedData.SaveSettings(Settings(successor, independent));
        new WorldAuthorityStore(scopedData).AppendReceived(record, owner.Id,
            roster.GroupId, roster.OwnerPublicKey);
        var codeGeneration = Guid.NewGuid();
        scopedData.SavePairingState(new PairingPersistentState
        {
            Devices = [new PairedDevice
            {
                Id = voterId, ProfileId = independent.Id,
                InviteGeneration = codeGeneration, AssignedProfileIds = [owner.Id, independent.Id],
                ServerPermissionOverrides = [], SaveReceiveProfileIds = [],
                SharedWorldPublicKey = voterKey,
                SharedWorldGrants = new() { [owner.Id] = new SharedWorldGrants(RecoveryVoter: true) }
            }],
            ServerInvites = [new ServerInviteState
            {
                ProfileId = independent.Id, Generation = codeGeneration,
                Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Endpoint = "https://127.0.0.1:5132", Fingerprint = new string('A', 64)
            }]
        });
        var scopedPairing = new PairingService(scopedData);
        Require(record.Roster.Members.Any(member => member.DeviceId == voterId &&
            member.PublicKey == voterKey), "cross-profile test device is not in the signed roster");
        RequireThrows<InvalidOperationException>(() => scopedPairing.IssueServer(independent.Id,
            false, false, "https://127.0.0.1:5132", new string('A', 64), refresh: true),
            "B code refresh could revoke a device granted to successor world A");
        Require(!scopedPairing.SharedRosterDirty(owner.Id) &&
            scopedPairing.EmergencyRevoke(independent.Id).Ok &&
            scopedPairing.SharedRosterDirty(owner.Id) &&
            scopedPairing.SharedRosterDirty(independent.Id),
            "emergency revoke missed a revoked B device's grant to A");
    }
    using (var unsignedData = Data("lineage-unsigned-cross-revoke"))
    {
        unsignedData.SaveSettings(Settings(successor, independent));
        new WorldAuthorityStore(unsignedData).AppendReceived(record, owner.Id,
            roster.GroupId, roster.OwnerPublicKey);
        var generation = Guid.NewGuid();
        var unsignedId = Guid.NewGuid();
        unsignedData.SavePairingState(new PairingPersistentState
        {
            Devices = [new PairedDevice
            {
                Id = unsignedId, ProfileId = independent.Id, InviteGeneration = generation,
                AssignedProfileIds = [owner.Id, independent.Id], ServerPermissionOverrides = [],
                SaveReceiveProfileIds = [], SharedWorldGrants = new()
                { [owner.Id] = new SharedWorldGrants(RecoveryVoter: true) }
            }],
            ServerInvites = [new ServerInviteState
            {
                ProfileId = independent.Id, Generation = generation,
                Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                Endpoint = "https://127.0.0.1:5132", Fingerprint = new string('A', 64)
            }]
        });
        var unsignedPairing = new PairingService(unsignedData);
        Require(!record.Roster.Members.Any(member => member.DeviceId == unsignedId) &&
            unsignedPairing.EmergencyRevoke(independent.Id).Ok &&
            unsignedData.LoadPairingState().Devices.Single(item => item.Id == unsignedId).Revoked &&
            !unsignedPairing.SharedRosterDirty(owner.Id) &&
            unsignedPairing.IssueServer(independent.Id, false, false,
                "https://127.0.0.1:5132", new string('A', 64), refresh: true).DeviceId == independent.Id &&
            !unsignedPairing.SharedRosterDirty(owner.Id),
            "unsigned B access dirtied unrelated successor roster A");
    }
    Require(!WorldAuthorityTrust.Verify(record with
    {
        Proposal = proposal with
        { SuccessorBinding = binding with { HostingPublicKey = voterKey } }
    }),
        "tampered hosting binding passed verification");
    successorStore.AppendReceived(record, owner.Id, roster.GroupId, roster.OwnerPublicKey);
    var pairingGeneration = Guid.NewGuid();
    PairedDevice SuccessorPairedDevice(Guid id, string publicKey, SharedWorldGrants grants,
        bool approvalPending = false) => new()
        {
            Id = id,
            ProfileId = owner.Id,
            InviteGeneration = pairingGeneration,
            AssignedProfileIds = [owner.Id],
            ServerPermissionOverrides = [],
            CredentialHash = new string('A', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(10),
            SharedWorldPublicKey = publicKey,
            SharedWorldGrants = new() { [owner.Id] = grants },
            // Deliberately stale: startup normalization must not dirty a successor roster.
            SaveReceiveProfileIds = [],
            ApprovalPending = approvalPending
        };
    successorData.SavePairingState(new PairingPersistentState
    {
        Devices = [SuccessorPairedDevice(deviceId, deviceKey,
                roster.Members.Single(item => item.DeviceId == deviceId).Grants),
            SuccessorPairedDevice(voterId, voterKey,
                roster.Members.Single(item => item.DeviceId == voterId).Grants, true)],
        ServerInvites = [new ServerInviteState
        {
            ProfileId = owner.Id, Generation = pairingGeneration,
            Code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            Endpoint = "https://127.0.0.1:5132", Fingerprint = new string('A', 64)
        }]
    });
    var pairingBefore = JsonSerializer.Serialize(successorData.LoadPairingState());
    var successorPairing = new PairingService(successorData);
    successorPairing.ReconcileProfiles([owner.Id]);
    Require(!successorPairing.SharedRosterDirty(owner.Id) &&
        JsonSerializer.Serialize(successorData.LoadPairingState()) == pairingBefore,
        "successor startup normalized pairing and dirtied inherited membership");
    var candidatePairing = successorData.LoadPairingState().Devices.Single(item => item.Id == deviceId);
    Require(successorPairing.AuthorizeReceiveSaves(candidatePairing, owner.Id, out _).Ok,
        "successor startup lost existing authorized receiver");
    Require(!successorPairing.SetReceiveSaves(deviceId, owner.Id, false).Ok &&
        !successorPairing.SetSharedWorldGrants(deviceId, owner.Id, new()).Ok &&
        !successorPairing.SetServerAccess(deviceId, [], null, [owner.Id]).Ok &&
        !successorPairing.SetAccessExpiry(deviceId,
            new DeviceAccessExpiryRequest(Clear: true)).Ok &&
        !successorPairing.Approve(voterId).Ok &&
        !successorPairing.ResetSharedWorldKey(deviceId).Ok,
        "successor pairing routes accepted a signed-membership change");
    var enrollmentNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    Require(!successorPairing.BindSharedWorldKey(deviceId, new SharedWorldEnrollmentRequest(
        enrollmentNonce, deviceKey, Convert.ToBase64String(device.SignData(
            SharedWorldRosterTrust.EnrollmentBasis(deviceId, enrollmentNonce, deviceKey),
            HashAlgorithmName.SHA256)))).Ok,
        "successor enrollment accepted a signed-membership change");
    RequireThrows<InvalidOperationException>(() => successorPairing.IssueServer(owner.Id, false, false,
        "https://127.0.0.1:5132", new string('A', 64), refresh: true),
        "successor server-code refresh rotated membership before publication");
    Require(!successorPairing.SharedRosterDirty(owner.Id) &&
        JsonSerializer.Serialize(successorData.LoadPairingState()) == pairingBefore,
        "rejected successor pairing action wrote state or stalled transfers");
    Require(successorPairing.IssueServer(independent.Id, false, false,
        "https://127.0.0.1:5132", new string('A', 64), refresh: true).DeviceId == independent.Id &&
        !successorPairing.SharedRosterDirty(owner.Id),
        "an unrelated successor world blocked server-code replacement");
    Require(successorPairing.EmergencyRevoke(independent.Id).Ok &&
        !successorPairing.SharedRosterDirty(owner.Id) &&
        !successorPairing.SharedRosterDirty(independent.Id),
        "an emergency code rotation without signed members dirtied a roster");
    Require(successorStore.Fenced(owner.Id, ownerShares.LocalAuthorityPublicKey(), out _),
        "successor started before local binding");
    RequireThrows<InvalidDataException>(() => successorStore.BindLocalSuccessor(owner.Id,
        record.RecordHash, voterId), "wrong device was bound");
    successorStore.BindLocalSuccessor(owner.Id, record.RecordHash, deviceId);
    var successorBackups = new WorldBackupService(successorData, TimeProvider.System);
    var successorShares = new SharedWorldService(successorData, successorBackups);
    successorShares.AdoptSuccessor(successor, record);
    var rosterPath = Path.Combine(successorData.RootPath, "shared-worlds", successor.Id.ToString("N"),
        roster.GroupId.ToString("N") + ".roster.json");
    var signedRosterBytes = File.ReadAllBytes(rosterPath);
    var signedBindingBytes = File.ReadAllBytes(Path.Combine(successorData.RootPath,
        "shared-worlds", successor.Id.ToString("N"), "source.json"));
    RequireThrows<InvalidDataException>(() => successorShares.PublishRoster(successor,
        [.. roster.Members, new SharedWorldRosterMember(Guid.NewGuid(),
            Convert.ToBase64String(wrong.ExportSubjectPublicKeyInfo()),
            new SharedWorldGrants(ManageSharing: true), false)]),
        "successor enrolled a member without the original owner's signature");
    RequireThrows<InvalidDataException>(() => successorShares.PublishRoster(successor,
        roster.Members, ownerOverride: false), "successor changed the owner's override");
    RequireThrows<InvalidDataException>(() => new SharedWorldService(successorData, successorBackups)
        .PublishRoster(successor, roster.Members), "restart allowed successor roster publication");
    Require(File.ReadAllBytes(rosterPath).SequenceEqual(signedRosterBytes) &&
        File.ReadAllBytes(Path.Combine(successorData.RootPath, "shared-worlds",
            successor.Id.ToString("N"), "source.json")).SequenceEqual(signedBindingBytes) &&
        successorShares.ReadRoster(successor)?.Signature == roster.Signature,
        "rejected successor mutation damaged the signed membership or binding");
    Require(!(await new HostManager(successorData, Games(successorData))
        .SharedWorldStatusAsync(successor.Id)).CanManageSharing,
        "successor UI claimed sharing management is available");
    var firstBackup = successorBackups.Create(successor, BackupKinds.Rolling);
    Require(firstBackup.Ok && firstBackup.Backup is not null, "first successor backup failed");
    var first = successorShares.PublishAfterStop(successor, firstBackup.Backup!.Id).Version!;
    Require(first.GroupId == origin.GroupId && first.Number == origin.Number + 1 &&
        first.ParentHash == origin.VersionHash && first.SigningPublicKey == hostKey &&
        FriendLink.VerifySharedChain(origin, first, [], record),
        "first successor version did not continue exact authority head");
    Require(!FriendLink.VerifySharedChain(origin, SharedWorldService.SignVersion(first, wrong), [], record),
        "Friend accepted a version signed by the wrong hosting key");
    using (var ownerSigner = ECDsa.Create())
    {
        ownerSigner.ImportPkcs8PrivateKey(ownerData.LoadProtected(
            "shared-world-signing-key.protected")!, out _);
        var ownerFork = SharedWorldService.SignVersion(first, ownerSigner);
        Require(!FriendLink.VerifySharedChain(origin, ownerFork, [], record),
            "Friend accepted an old owner fork after takeover");
    }
    var savedHostKey = successorData.LoadProtected(WorldAuthorityStore.HostingKeyName(owner.Id))!;
    successorData.SaveProtected(WorldAuthorityStore.HostingKeyName(owner.Id), wrong.ExportPkcs8PrivateKey());
    Require(new WorldAuthorityStore(successorData).Fenced(owner.Id,
        ownerShares.LocalAuthorityPublicKey(), out _), "wrong hosting key bypassed fence");
    successorData.SaveProtected(WorldAuthorityStore.HostingKeyName(owner.Id), savedHostKey);
    File.WriteAllText(Path.Combine(successor.WorldDirectory, "world.dat"), "successor second");
    var secondBackup = successorBackups.Create(successor, BackupKinds.Rolling);
    Require(secondBackup.Ok && secondBackup.Backup is not null, "second successor backup failed");
    var second = new SharedWorldService(successorData, successorBackups)
        .PublishAfterStop(successor, secondBackup.Backup!.Id).Version!;
    Require(second.Number == first.Number + 1 && second.ParentHash == first.VersionHash &&
        second.SigningPublicKey == hostKey && FriendLink.VerifySharedChain(origin, second, [first], record),
        "continued successor version failed after service restart");
    Require(FriendLink.AuthorizedVersionSignerForRecords(roster.OwnerPublicKey, second, [record]) &&
        FriendLink.VerifySharedChain(origin, second, [first], [record]),
        "newly enrolled Friend with no copy rejected two successor saves");
    Require(successorShares.ReadEarlierVersion(second, origin.Number).VersionHash == origin.VersionHash &&
        successorShares.ReadEarlierVersion(second, first.Number).VersionHash == first.VersionHash,
        "successor did not serve earlier signed versions across the handoff");
    Require(!new WorldAuthorityStore(successorData).Fenced(successor.Id,
        ownerShares.LocalAuthorityPublicKey(), out _),
        "restart lost the enrolled successor binding");
    using (var hostSigner = ECDsa.Create())
    {
        hostSigner.ImportPkcs8PrivateKey(savedHostKey, out _);
        var fork = SharedWorldService.SignVersion(second with
        { ParentHash = new string('A', 64) }, hostSigner);
        var latestPath = Path.Combine(successorData.RootPath, "shared-worlds",
            successor.Id.ToString("N"), "latest.json");
        File.WriteAllBytes(latestPath, JsonSerializer.SerializeToUtf8Bytes(fork,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Require((await new HostManager(successorData, Games(successorData))
            .StartAsync(successor.Id)).Code == "SharedWorldAuthorityBlocked",
            "signed local fork bypassed successor Start gate");
        File.WriteAllBytes(latestPath, JsonSerializer.SerializeToUtf8Bytes(second,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    var signedRosterBeforeUnsignedRevoke = successorShares.ReadRoster(successor)!.Signature;
    var ordinaryCode = successorPairing.IssueServer(owner.Id, false, false,
        "https://127.0.0.1:5132", new string('A', 64), refresh: false);
    var ordinaryPc = successorPairing.Activate(new PairingActivation(owner.Id,
        ordinaryCode.Code, ServerScope: true));
    Require(ordinaryPc is not null && !record.Roster.Members.Any(member =>
        member.DeviceId == ordinaryPc.DeviceId) &&
        successorData.LoadPairingState().Devices.Single(item => item.Id == ordinaryPc.DeviceId)
            .SharedWorldPublicKey is null,
        "ordinary successor pairing unexpectedly joined the signed roster");
    Require(successorPairing.Revoke(ordinaryPc!.DeviceId).Ok &&
        !successorPairing.Authenticate(ordinaryPc.DeviceId, ordinaryPc.Credential, out _).Ok &&
        !successorPairing.SharedRosterDirty(owner.Id) &&
        successorShares.ReadRoster(successor)?.Signature == signedRosterBeforeUnsignedRevoke &&
        !new PairingService(successorData).SharedRosterDirty(owner.Id),
        "revoking an unsigned transport credential paused the unchanged signed roster");
    Require(WorldAuthorityTrust.VerifyVote(successorStore.SignLocalVote(owner.Id,
        proposal, roster, deviceId, device), proposal, roster),
        "unsigned revoke blocked a valid local vote");
    Require(successorShares.PublishAfterStop(successor, secondBackup.Backup.Id).Ok,
        "unsigned revoke blocked shared publication");
    var unaffectedManager = new HostManager(successorData, Games(successorData));
    Require((await unaffectedManager.StartAsync(owner.Id)).Ok &&
        (await unaffectedManager.StopAsync(owner.Id)).Ok &&
        !successorPairing.SharedRosterDirty(owner.Id),
        "unsigned revoke blocked managed Start or post-Stop sharing");
    Require(new SharedWorldService(successorData, successorBackups).AuthorizedPublishedLineage(successor),
        "successor lineage became invalid before the Start race");
    var startManager = new HostManager(successorData, Games(successorData));
    Require((await startManager.SetSharedSavesAsync(owner.Id, false)).Ok,
        "successor could not turn sharing off before local revocation");
    Require(!new WorldAuthorityStore(successorData).Fenced(owner.Id,
        string.Empty, out var beforeStartReason),
        "successor was fenced before sharing-off Start: " + beforeStartReason);
    using var startEntered = new ManualResetEventSlim();
    using var allowLaunch = new ManualResetEventSlim();
    startManager.BeforeManagedLaunchForChecks = () =>
    {
        startEntered.Set();
        if (!allowLaunch.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("managed launch check did not resume");
    };
    var startTask = Task.Run(() => startManager.StartAsync(owner.Id));
    Require(startEntered.Wait(TimeSpan.FromSeconds(10)),
        "shared Start did not reach the managed launch boundary: " +
        (startTask.IsCompleted ? (await startTask).Code + " " + (await startTask).Message : "still waiting"));
    using var startRevokeEntered = new ManualResetEventSlim();
    var revokeTask = Task.Run(() =>
    {
        startRevokeEntered.Set();
        return successorPairing.Revoke(voterId);
    });
    try
    {
        Require(startRevokeEntered.Wait(TimeSpan.FromSeconds(10)), "start-race revoke did not begin");
        await Task.Delay(100);
        Require(!revokeTask.IsCompleted, "revoke completed before the managed launch boundary released");
    }
    finally { allowLaunch.Set(); }
    Require((await startTask.WaitAsync(TimeSpan.FromSeconds(20))).Ok,
        "authorized Start did not launch before revoke returned");
    Require((await revokeTask.WaitAsync(TimeSpan.FromSeconds(20))).Ok,
        "revoke did not complete after managed launch");
    startManager.BeforeManagedLaunchForChecks = null;
    Require((await startManager.StopAsync(owner.Id)).Ok,
        "fixture run did not stop after revoked membership");
    Require((await startManager.StartAsync(owner.Id)).Code == "SharedWorldAuthorityBlocked",
        "a later Start launched after completed revoke");
    Require(successorPairing.SharedRosterDirty(owner.Id) &&
        successorPairing.AuthorizeReceiveSaves(candidatePairing, owner.Id, out _).Ok,
        "sharing-off revocation did not mark successor group authority unresolved");
    RequireThrows<InvalidDataException>(() => successorStore.SignLocalVote(owner.Id,
        proposal, roster, deviceId, device),
        "successor counted a stale signed roster after local revocation");
    RequireThrows<InvalidDataException>(() => successorStore.SignLocalVote(owner.Id,
        proposal, roster, voterId, voter),
        "revoked member cast a local vote using the stale signed roster");
    Require(!successorShares.PublishAfterStop(successor, secondBackup.Backup!.Id).Ok,
        "successor published another shared save under unresolved membership");
    Require((await new HostManager(successorData, Games(successorData))
        .SharedWorldReadAsync(owner.Id)).Status.Latest is null,
        "successor served a save under unresolved signed membership");
    var restartedPairing = new PairingService(successorData);
    restartedPairing.ReconcileProfiles([owner.Id]);
    Require(restartedPairing.SharedRosterDirty(owner.Id) &&
        new WorldAuthorityStore(successorData).GovernanceUnresolved(owner.Id),
        "restart cleared the unresolved successor governance fence");
    Require(restartedPairing.EmergencyRevoke(owner.Id).Ok &&
        restartedPairing.SharedRosterDirty(owner.Id) &&
        !restartedPairing.AuthorizeReceiveSaves(candidatePairing, owner.Id, out _).Ok,
        "emergency revocation allowed further receiver authorization");
    using (var oldStatusData = Data("schema2-old-host-status"))
    {
        oldStatusData.SaveSettings(Settings(owner));
        new WorldAuthorityStore(oldStatusData).AppendReceived(record, owner.Id, roster.GroupId,
            roster.OwnerPublicKey);
        var oldQuorumStatus = await new HostManager(oldStatusData, Games(oldStatusData))
            .SharedWorldStatusAsync(owner.Id);
        Require(oldQuorumStatus.Authority is { State: "OldHostFenced", Head: { Epoch: 1 } } &&
            oldQuorumStatus.Authority.Head.HostDeviceId == deviceId &&
            oldQuorumStatus.Authority.Head.HostPublicKey == hostKey &&
            oldQuorumStatus.Authority.Head.RecordHash == record.RecordHash &&
            !oldQuorumStatus.CanManageSharing,
            "restarted old Host did not identify the schema-2 PC through its signed binding: " +
            oldQuorumStatus.Authority?.State);
    }
    new WorldAuthorityStore(ownerData).AppendReceived(record, owner.Id, roster.GroupId,
        roster.OwnerPublicKey);
    Require(new WorldAuthorityStore(ownerData).Fenced(owner.Id,
        ownerShares.LocalAuthorityPublicKey(), out _) &&
        !ownerShares.PublishAfterStop(owner, Guid.NewGuid()).Ok,
        "old owner published a fork after newer authority");
    await Task.CompletedTask;
});

await Check("signed copy receipts count only the exact latest verified version", () =>
{
    using var data = Data("signed-copy-receipts");
    var profile = Profile("receipt-world", "receipt-world", FreePort());
    profile.Kind = "Fixture";
    profile.Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 };
    profile.SharedSavesEnabled = true;
    var worldFile = Path.Combine(profile.WorldDirectory, "world.dat");
    File.WriteAllText(worldFile, "first save");
    var backups = new WorldBackupService(data, TimeProvider.System);
    var shares = new SharedWorldService(data, backups);
    var device = Guid.NewGuid();
    using var pc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var fake = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var key = Convert.ToBase64String(pc.ExportSubjectPublicKeyInfo());
    var roster = shares.PublishRoster(profile, [new SharedWorldRosterMember(device, key,
        new SharedWorldGrants(Receive: true), false)]);
    var firstBackup = backups.Create(profile, BackupKinds.Rolling);
    Require(firstBackup.Ok && firstBackup.Backup is not null, "first backup failed");
    var first = shares.PublishAfterStop(profile, firstBackup.Backup!.Id).Version!;
    SharedWorldReceipt Sign(SharedWorldVersion version, ECDsa signer, Guid deviceId,
        long epoch, long revision)
    {
        var draft = new SharedWorldReceipt(1, version.GroupId, profile.Id, version.VersionHash,
            deviceId, epoch, revision, Guid.NewGuid(), "");
        return draft with
        {
            Signature = Convert.ToBase64String(signer.SignData(
            SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256))
        };
    }
    var receipt = Sign(first, pc, device, roster.Epoch, roster.Revision);
    Require(!shares.ConfirmReceipt(profile, device, Sign(first, fake, device,
        roster.Epoch, roster.Revision)).Ok, "fake key confirmed a copy");
    Require(!shares.ConfirmReceipt(profile, Guid.NewGuid(), receipt).Ok,
        "different transport device confirmed a copy");
    Require(!shares.ConfirmReceipt(profile, device, receipt with { VersionHash = new string('A', 64) }).Ok,
        "altered hash confirmed a copy");
    Require(shares.Status(profile).ConfirmedCopies == 0, "unverified copy was counted");
    Require(shares.ConfirmReceipt(profile, device, receipt).Ok &&
        shares.ConfirmReceipt(profile, device, receipt).Code == "AlreadyConfirmed",
        "valid receipt or network retry failed");
    Require(!shares.ConfirmReceipt(profile, device, Sign(first, pc, device,
        roster.Epoch, roster.Revision)).Ok, "replay with a fresh nonce was accepted");
    Require(shares.Status(profile).ConfirmedCopies == 1, "confirmed copy was not counted");
    var receiptsFile = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"), "receipts.json");
    var originalReceipts = File.ReadAllBytes(receiptsFile);
    File.WriteAllBytes(receiptsFile, JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = 1,
        groupId = first.GroupId,
        profileId = profile.Id,
        versionHash = first.VersionHash,
        receipts = new[] { receipt with { ReceiptId = Guid.NewGuid() } }
    }));
    Require(shares.Status(profile).ConfirmedCopies == 0, "tampered persisted receipt inflated the count");
    File.WriteAllBytes(receiptsFile, originalReceipts);
    Require(shares.Status(profile).ConfirmedCopies == 1, "valid persisted receipt was not restored");
    File.WriteAllText(worldFile, "second save");
    var nextBackup = backups.Create(profile, BackupKinds.Rolling);
    Require(nextBackup.Ok && nextBackup.Backup is not null, "second backup failed");
    var next = shares.PublishAfterStop(profile, nextBackup.Backup!.Id).Version!;
    Require(shares.Status(profile).ConfirmedCopies == 0 &&
        !shares.ConfirmReceipt(profile, device, receipt).Ok,
        "old head was counted or accepted after a new save");
    roster = shares.PublishRoster(profile, [new SharedWorldRosterMember(device, key,
        new SharedWorldGrants(), false)]);
    Require(!shares.ConfirmReceipt(profile, device, Sign(next, pc, device,
        roster.Epoch, roster.Revision)).Ok, "revoked Receive grant confirmed a copy");
    return Task.CompletedTask;
});

await Check("copy receipt requests reject oversized declared and chunked bodies", async () =>
{
    using var declared = new MemoryStream(new byte[1]);
    Require(await SharedWorldReceiptTrust.ReadBoundedAsync(declared, 4097, CancellationToken.None) is null,
        "oversized declared receipt was read");
    using var chunked = new MemoryStream(new byte[4097]);
    Require(await SharedWorldReceiptTrust.ReadBoundedAsync(chunked, null, CancellationToken.None) is null,
        "oversized chunked receipt was accepted");
    using var exact = new MemoryStream(new byte[4096]);
    Require((await SharedWorldReceiptTrust.ReadBoundedAsync(exact, null, CancellationToken.None))?.Length == 4096,
        "bounded chunked receipt was rejected");
    using var pc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var draft = new SharedWorldReceipt(1, Guid.NewGuid(), Guid.NewGuid(), new string('A', 64),
        Guid.NewGuid(), 2, 3, Guid.NewGuid(), "");
    var signed = draft with
    {
        Signature = Convert.ToBase64String(pc.SignData(
        SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256))
    };
    var friendWire = JsonSerializer.SerializeToUtf8Bytes(signed,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    using var wireStream = new MemoryStream(friendWire);
    var parsed = SharedWorldReceiptTrust.Parse((await SharedWorldReceiptTrust.ReadBoundedAsync(
        wireStream, null, CancellationToken.None))!);
    Require(parsed == signed && SharedWorldReceiptTrust.Verify(parsed,
        Convert.ToBase64String(pc.ExportSubjectPublicKeyInfo())),
        "the Host could not parse and verify the Friend's camel-case signed receipt");
});

await Check("Terraria preview copies an isolated world and treats listener evidence as player-count unknown", async () =>
{
    using var data = Data("terraria-preview");
    var source = Path.Combine(root, "source-terraria-world.wld");
    File.WriteAllText(source, "disposable Terraria world fixture");
    var imported = TerrariaSetup.ImportCopy(data, Guid.NewGuid(), source);
    Require(imported.Ok && imported.WorldDirectory is not null && imported.WorldId is not null &&
        File.ReadAllText(source) == "disposable Terraria world fixture", "Terraria import changed the source world");
    var importedDirectory = imported.WorldDirectory ?? throw new Exception("Terraria managed directory missing");
    var importedWorldId = imported.WorldId ?? throw new Exception("Terraria world name missing");
    var importedProfileId = Guid.Parse(Path.GetFileName(importedDirectory));
    Require(!TerrariaSetup.ImportCopy(data, importedProfileId, source).Ok,
        "Terraria import overwrote an existing managed world");
    var executable = Path.Combine(root, "TerrariaServer.exe");
    File.WriteAllText(executable, "synthetic executable name only");
    var profile = new ServerProfile
    {
        Id = importedProfileId,
        Kind = GameKinds.Terraria,
        Name = "Terraria preview",
        WorldId = importedWorldId,
        WorldDirectory = importedDirectory,
        GamePort = FreePort(),
        ExecutablePath = executable
    };
    var driver = new TerrariaServerDriver(data);
    Require(driver.ValidateForStart(profile) is null && !driver.SupportsCrashRecovery && driver.SupportsBackups &&
        driver.Ports(profile).Single().Protocol == "TCP", "Terraria preview driver accepted the wrong managed world or port");
    var health = driver.Health(new ManagedRun { GamePort = profile.GamePort });
    Require(!health.Ok && !health.PlayerCountTrusted && health.OnlinePlayers is null,
        "Terraria preview invented a trusted player count without a game response");
    using (var listener = new TcpListener(IPAddress.Loopback, profile.GamePort))
    {
        listener.Start();
        var listening = driver.Health(new ManagedRun { GamePort = profile.GamePort });
        Require(listening is { Ok: true, State: "Listening", PlayerCountTrusted: false },
            "a Terraria TCP listener was presented as verified game readiness");
    }
    await Task.CompletedTask;
});

await Check("Host move kit verifies exported hashes and rejects a tampered world", async () =>
{
    using var data = Data("host-move-kit");
    var source = Path.Combine(root, "host-move-source.wld");
    File.WriteAllText(source, "move-kit-world");
    var profileId = Guid.NewGuid();
    var imported = TerrariaSetup.ImportCopy(data, profileId, source);
    Require(imported.Ok && imported.WorldDirectory is not null && imported.WorldId is not null,
        "fixture world import failed");
    var importedDirectory = imported.WorldDirectory ?? throw new Exception("Move kit managed directory missing");
    var importedWorldId = imported.WorldId ?? throw new Exception("Move kit world name missing");
    var profile = new ServerProfile
    {
        Id = profileId,
        Kind = GameKinds.Terraria,
        Name = "Move this world",
        WorldId = importedWorldId,
        WorldDirectory = importedDirectory,
        GamePort = 7777,
        Backups = new BackupOptions { MinimumFreeSpaceMb = 0 }
    };
    var service = new WorldBackupService(data, TimeProvider.System);
    var backup = service.Create(profile, BackupKinds.Manual);
    Require(backup.Ok && backup.Backup is not null, "fixture backup failed");
    var backupRecord = backup.Backup ?? throw new Exception("Move kit backup record missing");
    var destination = Path.Combine(root, "move-kit-destination");
    Directory.CreateDirectory(destination);
    var moved = service.PrepareMoveKit(profile, backupRecord.Id, destination);
    var kitDirectory = Path.Combine(destination, "TogetherServer Backups", profileId.ToString("N"),
        backupRecord.Id.ToString("N") + ".backup");
    Require(moved.Ok && moved.Kit?.Kind == GameKinds.Terraria &&
        service.InspectMoveKit(kitDirectory).Ok, "verified Host move kit was not readable on a new PC");
    File.WriteAllText(Path.Combine(kitDirectory, "payload", profile.WorldId + ".wld"), "changed");
    Require(!service.InspectMoveKit(kitDirectory).Ok &&
        File.ReadAllText(source) == "move-kit-world", "a tampered kit passed verification or changed the source");
    await Task.CompletedTask;
});

await Check("interrupted restore journal reconciles rollback and installed replacement", async () =>
{
    using var data = Data("restore-reconciliation");
    var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-24T12:00:00Z"));
    var parent = Path.Combine(root, "restore-reconciliation-worlds");
    Directory.CreateDirectory(parent);

    var rollbackId = Guid.NewGuid();
    var rollbackBackupId = Guid.NewGuid();
    var rollbackWorld = Path.Combine(parent, "rollback-world");
    var rollbackProfile = Profile("rollback-profile", "rollback-world", FreePort(), rollbackWorld);
    Directory.Delete(rollbackWorld, true);
    var rollback = Path.Combine(parent, $".togetherserver-restore-{rollbackId:N}.rollback");
    var stage = Path.Combine(parent, $".togetherserver-restore-{rollbackId:N}.staging");
    Directory.CreateDirectory(rollback);
    Directory.CreateDirectory(stage);
    File.WriteAllText(Path.Combine(rollback, "world.txt"), "original");
    File.WriteAllText(Path.Combine(stage, "world.txt"), "replacement");

    var installedId = Guid.NewGuid();
    var installedBackupId = Guid.NewGuid();
    var installedWorld = Path.Combine(parent, "installed-world");
    var installedProfile = Profile("installed-profile", "installed-world", FreePort(), installedWorld);
    var installedRollback = Path.Combine(parent, $".togetherserver-restore-{installedId:N}.rollback");
    Directory.CreateDirectory(installedRollback);
    File.WriteAllText(Path.Combine(installedWorld, "world.txt"), "replacement");
    File.WriteAllText(Path.Combine(installedRollback, "world.txt"), "original");
    data.SaveSettings(Settings(rollbackProfile, installedProfile));
    data.SaveBackupCatalog(new BackupCatalog
    {
        Records =
        [
            new() { Id = rollbackBackupId, ProfileId = rollbackProfile.Id, Kind = rollbackProfile.Kind,
                WorldId = rollbackProfile.WorldId },
            new() { Id = installedBackupId, ProfileId = installedProfile.Id, Kind = installedProfile.Kind,
                WorldId = installedProfile.WorldId }
        ]
    });
    data.SaveState("restore-transactions.json", new List<WorldRestoreTransaction>
    {
        new() { Id = rollbackId, ProfileId = rollbackProfile.Id, BackupId = rollbackBackupId,
            Kind = rollbackProfile.Kind, WorldId = rollbackProfile.WorldId,
            ProfileWorldDirectory = rollbackProfile.WorldDirectory, WorldDirectory = rollbackWorld,
            Phase = WorldRestorePhases.LiveMoved },
        new() { Id = installedId, ProfileId = installedProfile.Id, BackupId = installedBackupId,
            Kind = installedProfile.Kind, WorldId = installedProfile.WorldId,
            ProfileWorldDirectory = installedProfile.WorldDirectory, WorldDirectory = installedWorld,
            Phase = WorldRestorePhases.ReplacementInstalled }
    });
    _ = new WorldBackupService(data, clock, games: Games(data));
    Require(File.ReadAllText(Path.Combine(rollbackWorld, "world.txt")) == "original" &&
        !Directory.Exists(rollback) && !Directory.Exists(stage),
        "an interrupted live-directory move did not restore the original world");
    Require(File.ReadAllText(Path.Combine(installedWorld, "world.txt")) == "replacement" &&
        !Directory.Exists(installedRollback) &&
        data.LoadState("restore-transactions.json", new List<WorldRestoreTransaction>()).Count == 0,
        "an installed replacement did not roll forward or clear its restore journal");

    using var tamperData = Data("restore-journal-tamper");
    var ownedWorld = Path.Combine(parent, "owned-world");
    var unrelatedWorld = Path.Combine(parent, "unrelated-world");
    var tamperProfile = Profile("tamper-profile", "tamper-world", FreePort(), ownedWorld);
    Directory.CreateDirectory(unrelatedWorld);
    File.WriteAllText(Path.Combine(unrelatedWorld, "do-not-touch.txt"), "owner data");
    var tamperBackupId = Guid.NewGuid();
    tamperData.SaveSettings(Settings(tamperProfile));
    tamperData.SaveBackupCatalog(new BackupCatalog
    {
        Records = [new()
    {
        Id = tamperBackupId, ProfileId = tamperProfile.Id, Kind = tamperProfile.Kind,
        WorldId = tamperProfile.WorldId
    }]
    });
    tamperData.SaveState("restore-transactions.json", new List<WorldRestoreTransaction> { new()
    {
        Id = Guid.NewGuid(), ProfileId = tamperProfile.Id, BackupId = tamperBackupId,
        Kind = tamperProfile.Kind, WorldId = tamperProfile.WorldId,
        ProfileWorldDirectory = tamperProfile.WorldDirectory,
        WorldDirectory = Path.Combine(tamperProfile.WorldDirectory, "..", Path.GetFileName(unrelatedWorld)),
        Phase = WorldRestorePhases.Prepared
    } });
    _ = new WorldBackupService(tamperData, clock);
    Require(File.ReadAllText(Path.Combine(unrelatedWorld, "do-not-touch.txt")) == "owner data" &&
        tamperData.Recovery.LifecycleBlocked &&
        tamperData.Recovery.Notices.Any(notice => notice.StateFile == "restore-transactions.json"),
        "a tampered restore journal touched an unrelated directory or failed to block lifecycle");

    using var semanticData = Data("restore-journal-semantic");
    semanticData.SaveState("restore-transactions.json", new List<WorldRestoreTransaction> { null! });
    _ = new WorldBackupService(semanticData, clock);
    Require(semanticData.Recovery.LifecycleBlocked &&
        semanticData.Recovery.Notices.Any(notice => notice.StateFile == "restore-transactions.json"),
        "a syntactically valid but semantically invalid restore journal silently disappeared");
    await Task.CompletedTask;
});

await Check("acceptance confirmations are configuration-bound and persist no route or server details", () =>
{
    using var data = Data("acceptance-recorder");
    var profile = Profile("Private server name", "private-world", FreePort());
    profile.ServerName = "Private server name";
    profile.ExecutablePath = Path.Combine(data.RootPath, "synthetic-server.exe");
    File.WriteAllText(profile.ExecutablePath, "server build one");
    var settings = Settings(profile);
    settings.CompanionPort = 55131;
    settings.ConnectionRoute = new() { Mode = "Manual", Address = "198.51.100.44" };
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    var recorder = new AcceptanceRecorder(data, clock);
    var recorded = recorder.Change(settings, profile.Id,
        new AcceptanceChange(AcceptanceCheckIds.FriendRoute, true));
    Require(recorded.Ok && recorded.View.Checks.Single(item =>
            item.Id == AcceptanceCheckIds.FriendRoute).Confirmed,
        "owner confirmation was not recorded for the current configuration");
    var persisted = File.ReadAllText(Path.Combine(data.RootPath, "acceptance-records.json"));
    Require(!persisted.Contains(profile.ServerName, StringComparison.Ordinal) &&
        !persisted.Contains(settings.ConnectionRoute.Address, StringComparison.Ordinal) &&
        !persisted.Contains(profile.WorldDirectory, StringComparison.OrdinalIgnoreCase),
        "acceptance storage persisted server, route, or path details instead of a fingerprint");
    profile.GamePort++;
    var stale = recorder.View(settings, profile.Id);
    Require(stale.Stale && stale.Checks.All(item => !item.Confirmed),
        "a changed game configuration retained current acceptance credit");
    profile.GamePort--;
    File.WriteAllText(profile.ExecutablePath, "server build two");
    var changedGame = recorder.View(settings, profile.Id);
    Require(changedGame.Stale && changedGame.GameFilesAvailable && changedGame.GameFilesChanged &&
        changedGame.Checks.All(item => !item.Confirmed),
        "a changed game executable retained real-game acceptance credit");
    var reconfirmed = recorder.Change(settings, profile.Id,
        new AcceptanceChange(AcceptanceCheckIds.RealJoin, true));
    Require(reconfirmed.Ok && !reconfirmed.View.GameFilesChanged && !reconfirmed.View.Stale,
        "a new real-game confirmation did not bind to the current executable");
    return Task.CompletedTask;
});

await Check("update checkpoint is same-root, bounded, hash-verified, and rejects tampering", () =>
{
    using var data = Data("state-checkpoint");
    var profile = Profile("checkpoint", "checkpoint-world", FreePort());
    data.SaveSettings(Settings(profile));
    data.SaveState("checkpoint-fixture.json", new { Value = "owner-local-state" });
    data.SaveState("app-session.json", new { Excluded = true });
    var executable = Path.Combine(root, "checkpoint-previous.exe");
    File.WriteAllBytes(executable, SHA256.HashData(Encoding.UTF8.GetBytes("synthetic executable")));
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 30, 12, 30, 0, TimeSpan.Zero));
    var service = new StateCheckpointService(data, clock);
    var result = service.Create("0.2.1", "0.2.2", executable);
    Require(result.Ok && result.Checkpoint is not null && result.Checkpoint.FileCount > 0 &&
        AppInstance.ContainsPath(data.UpdateCheckpointsRoot, result.Checkpoint.Directory),
        "verified update checkpoint was not created under the owned checkpoint root");
    var checkpoint = result.Checkpoint ?? throw new Exception("checkpoint reference was missing");
    Require(!File.Exists(Path.Combine(checkpoint.Directory, "app-session.json")),
        "ephemeral app-session marker was included in recovery state");
    Require(StateCheckpointService.TryValidate(data.RootPath, checkpoint.Directory,
        checkpoint.ManifestSha256, out _), "fresh checkpoint did not validate");
    Require(!StateCheckpointService.TryValidate(data.RootPath, checkpoint.Directory,
        checkpoint.ManifestSha256, out _, new string('A', 64)),
        "checkpoint was not bound to the previous executable hash");
    var copiedState = Directory.EnumerateFiles(checkpoint.Directory, "*.json")
        .First(path => !Path.GetFileName(path).Equals("checkpoint-manifest.json", StringComparison.OrdinalIgnoreCase));
    File.AppendAllText(copiedState, "tamper");
    Require(!StateCheckpointService.TryValidate(data.RootPath, checkpoint.Directory,
        checkpoint.ManifestSha256, out _), "tampered checkpoint was accepted");
    return Task.CompletedTask;
});

await Check("interrupted app recovery requires deliberate resume and clean exit clears the prompt", () =>
{
    using var data = Data("startup-recovery");
    var profile = Profile("resume profile", "resume-world", FreePort());
    var settings = Settings(profile);
    data.SaveState("app-session.json", new ApplicationSessionState
    {
        SessionId = Guid.NewGuid(),
        StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
        CleanExit = false,
        ActiveProfileIds = [profile.Id]
    });
    var recovery = new StartupRecoveryService(data, [], "not-an-executable");
    var offline = recovery.View([], settings);
    Require(offline.PreviousSessionInterrupted && recovery.CanResume(profile.Id) &&
        offline.Items.Single().State == "Ready to resume" && offline.Items.Single().CanResume,
        "interrupted offline server was not presented as a deliberate resume choice");
    var attached = recovery.View([new RunView(profile.Id, "Ready", "exact process", 123)], settings);
    Require(attached.Items.Single().State == "Reattached" && !attached.Items.Single().CanResume &&
        !recovery.CanResume(profile.Id) && recovery.View([], settings).Items.Count == 0,
        "an exact live process was offered a duplicate resume start");
    recovery.MarkClean();
    var next = new StartupRecoveryService(data, [], "not-an-executable");
    Require(!next.View([], settings).PreviousSessionInterrupted,
        "clean shutdown retained an interrupted-session warning");
    next.MarkClean();
    return Task.CompletedTask;
});

await Check("storage and resource health remains informational lifecycle evidence", () =>
{
    using var data = Data("storage-health");
    var profile = Profile("storage profile", "storage-world", FreePort());
    var health = new StorageHealthService(data, TimeProvider.System).Read(Settings(profile));
    Require(health.Locations.Any(item => item.Id == "app-data") &&
        health.Locations.Any(item => item.Id == "world-" + profile.Id.ToString("N")) &&
        health.Resources.LogicalProcessors > 0 &&
        health.Resources.EvidenceBoundary.Contains("never authorize lifecycle actions", StringComparison.Ordinal),
        "storage/resource projection lost its non-authoritative evidence boundary");
    return Task.CompletedTask;
});

await Check("server file edits require maintenance, an offline checkpoint, and a matching version", async () =>
{
    using var data = Data("server-file-edits");
    var profile = Profile("server-file-world", "server-file-world", FreePort());
    profile.Kind = GameKinds.Valheim;
    profile.ServerName = "Server file checks";
    profile.Backups = new BackupOptions { MinimumFreeSpaceMb = 0 };
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.txt"), "recognizable save");
    var adminList = Path.Combine(profile.WorldDirectory, "adminlist.txt");
    File.WriteAllText(adminList, "Steam_111\n");
    var manager = Manager(data);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "server-file profile setup failed");
    var files = await manager.ServerFilesAsync(profile.Id);
    Require(files.Ok && files.Locations.Any(item => item.Key == "save" && item.Available) &&
        files.Files.Any(item => item.Key == "admin-list" && item.Available) &&
        await manager.ServerFolderAsync(profile.Id, "arbitrary") is null,
        "server file list exposed an arbitrary location or omitted the active file");
    var before = await manager.ReadServerFileAsync(profile.Id, "admin-list");
    Require(before.Ok && before.Content == "Steam_111\n" && before.Sha256 is not null,
        "server file read did not return the exact active version");
    var request = new ServerFileChangeRequest(before.Sha256!, "Steam_222\n");
    Require((await manager.SaveServerFileAsync(profile.Id, "admin-list", request)).Code ==
        "MaintenanceRequired" && File.ReadAllText(adminList) == "Steam_111\n",
        "file changed without maintenance mode");
    profile.Maintenance.Enabled = true;
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "maintenance setup failed");
    Require((await manager.SaveServerFileAsync(profile.Id, "admin-list",
        request with { ExpectedSha256 = new string('0', 64) })).Code == "FileChanged" &&
        (await manager.BackupsAsync(profile.Id)).Backups.Count == 0,
        "stale file edit created a checkpoint or overwrote external changes");
    var saved = await manager.SaveServerFileAsync(profile.Id, "admin-list", request);
    Require(saved.Ok && saved.CanUndo && File.ReadAllText(adminList) == "Steam_222\n" &&
        (await manager.BackupsAsync(profile.Id)).Backups.Count == 1,
        "offline config edit did not checkpoint, save, and retain Undo");
    var restored = await manager.UndoServerFileAsync(profile.Id, "admin-list",
        new ServerFileUndoRequest(saved.Sha256!));
    Require(restored.Ok && File.ReadAllText(adminList) == "Steam_111\n" &&
        (await manager.BackupsAsync(profile.Id)).Backups.Count == 2 &&
        !(await manager.ReadServerFileAsync(profile.Id, "admin-list")).CanUndo,
        "Undo did not restore the previous file after another offline checkpoint");
});

await Check("Minecraft file editor rejects a world or port mismatch", () =>
{
    using var data = Data("minecraft-file-locations");
    var profile = Profile("server-properties-world", "known-world", FreePort());
    profile.Kind = GameKinds.MinecraftBedrock;
    Directory.CreateDirectory(Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId));
    Directory.CreateDirectory(Path.Combine(profile.WorldDirectory, "behavior_packs"));
    var locations = ServerFiles.List(data, profile).Locations;
    Require(locations.Any(item => item.Key == "server" && item.Path == profile.WorldDirectory && item.Available) &&
        locations.Any(item => item.Key == "save" && item.Path ==
            Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId) && item.Available) &&
        locations.Any(item => item.Key == "behavior-packs" && item.Available),
        "Minecraft folders did not point at the selected server, world, and shared add-ons");
    var valid = $"level-name=known-world\nserver-port={profile.GamePort}\nserver-portv6=19133\nenable-lan-visibility=false\n";
    Require(ServerFiles.ValidateChange(profile, "server-properties",
        new ServerFileChangeRequest("old-hash", valid)).Ok, "matching properties were rejected");
    Require(ServerFiles.ValidateChange(profile, "server-properties",
        new ServerFileChangeRequest("old-hash", valid.Replace("known-world", "wrong-world"))).Code ==
        "InvalidConfiguration", "world mismatch was accepted");
    Require(ServerFiles.ValidateChange(profile, "server-properties",
        new ServerFileChangeRequest("old-hash", valid + "server-port=12345\n")).Code ==
        "InvalidConfiguration", "duplicate port was accepted");
    profile.ExecutablePath = Path.Combine(profile.WorldDirectory, "bedrock_server.exe");
    File.WriteAllText(profile.ExecutablePath, "synthetic name only");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "server.properties"), valid);
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "allowlist.json"), "not JSON");
    var driver = new MinecraftBedrockServerDriver(data);
    Require(driver.ValidateForStart(profile)?.Code == "MinecraftConfigurationInvalid",
        "a malformed player list edited outside the app was allowed at Start");
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "allowlist.json"), "[]");
    Require(driver.ValidateForStart(profile) is null,
        "a valid Bedrock player list was blocked at Start");
    return Task.CompletedTask;
});

await Check("guided server changes pause Friend controls before a zero-player Stop and checkpoint", async () =>
{
    using var data = Data("guided-server-change");
    var profile = Profile("guided-change-world", "guided-change-world", FreePort());
    profile.Backups = new BackupOptions { MinimumFreeSpaceMb = 0 };
    File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.txt"), "recognizable save");
    var driver = new ObservationFixtureDriver
    {
        HealthResult = new(true, "FixtureReady", "Ready", "Trusted fixture observation.",
            OnlinePlayers: 2, MaxPlayers: 10, PlayerCountTrusted: true)
    };
    var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.LoopbackOnly));
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "guided-change profile failed to save");
    Require((await manager.StartAsync(profile.Id)).Ok, "guided-change fixture failed to start");
    Require((await manager.PrepareServerChangeAsync(profile.Id)).Code == "PlayersOnlineOrUnknown" &&
        data.LoadSettings().Profiles.Single(item => item.Id == profile.Id).Maintenance.Enabled &&
        data.LoadRuns().Count == 1,
        "guided change did not pause Friend controls or protect an occupied server");
    driver.HealthResult = new(true, "FixtureReady", "Ready", "Trusted fixture observation.",
        OnlinePlayers: 0, MaxPlayers: 10, PlayerCountTrusted: true);
    var prepared = await manager.PrepareServerChangeAsync(profile.Id);
    var saved = data.LoadSettings().Profiles.Single(item => item.Id == profile.Id);
    Require(prepared.Ok && saved.Maintenance.Enabled && data.LoadRuns().Count == 0 &&
        (await manager.BackupsAsync(profile.Id)).Backups.Single().SetupIncluded,
        "guided change did not pause Friend controls, confirm Stop, and checkpoint the offline setup");
    Require((await manager.FinishServerChangeAsync(profile.Id, true)).Code == "ServerNotReady",
        "maintenance ended before the game reported Ready");
    Require((await manager.StartAsync(profile.Id)).Ok, "guided-change fixture did not restart");
    Require((await manager.FinishServerChangeAsync(profile.Id, false)).Code == "GameJoinNotConfirmed" &&
        data.LoadSettings().Profiles.Single(item => item.Id == profile.Id).Maintenance.Enabled,
        "maintenance ended without an explicit real-join confirmation");
    Require((await manager.FinishServerChangeAsync(profile.Id, true)).Ok &&
        !data.LoadSettings().Profiles.Single(item => item.Id == profile.Id).Maintenance.Enabled,
        "guided change did not end maintenance after readiness and owner confirmation");
    Require((await manager.StopAsync(profile.Id)).Ok, "guided-change fixture did not stop");
});

await Check("complete setup checkpoint restores reviewed configuration with the world", async () =>
{
    using var data = Data("complete-setup-restore");
    var profile = Profile("complete-setup-world", "checkpoint-world", FreePort());
    profile.Kind = GameKinds.MinecraftBedrock;
    profile.Maintenance.Enabled = true;
    profile.Backups = new BackupOptions { MinimumFreeSpaceMb = 0, RetentionCount = 5 };
    var save = Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId);
    Directory.CreateDirectory(save);
    var world = Path.Combine(save, "recognizable-world.txt");
    File.WriteAllText(world, "before");
    var properties = Path.Combine(profile.WorldDirectory, "server.properties");
    File.WriteAllText(properties, $"level-name={profile.WorldId}\nserver-port={profile.GamePort}\n");
    var allowlist = Path.Combine(profile.WorldDirectory, "allowlist.json");
    File.WriteAllText(allowlist, "[{\"name\":\"Player One\",\"ignoresPlayerLimit\":false}]");
    var manager = Manager(data);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "complete setup profile failed to save");
    var created = await manager.CreateCompleteSetupBackupAsync(profile.Id);
    var backup = (await manager.BackupsAsync(profile.Id)).Backups.Single();
    Require(created.Ok && backup.SetupIncluded, "complete setup checkpoint omitted reviewed config");
    File.WriteAllText(world, "after");
    File.WriteAllText(allowlist, "[]");
    File.WriteAllText(properties, $"level-name={profile.WorldId}\nserver-port={profile.GamePort}\nmax-players=2\n");
    var restored = await manager.RestoreCompleteSetupAsync(profile.Id, backup.Id);
    Require(restored.Ok && File.ReadAllText(world) == "before" &&
        File.ReadAllText(allowlist).Contains("Player One", StringComparison.Ordinal) &&
        !File.ReadAllText(properties).Contains("max-players=2", StringComparison.Ordinal),
        "complete setup restore did not return the world and active configuration together");
    var pendingName = $"setup-restore-pending-{profile.Id:N}.protected";
    data.SaveProtected(pendingName, Encoding.UTF8.GetBytes(backup.Id.ToString("N")));
    Require((await manager.StartAsync(profile.Id)).Code == "SetupRestoreRecoveryRequired" &&
        (await manager.CreateCompleteSetupBackupAsync(profile.Id)).Code == "SetupRestoreRecoveryRequired",
        "an interrupted complete setup restore did not block Start and new checkpoints");
    var retried = await manager.RestoreCompleteSetupAsync(profile.Id, backup.Id);
    Require(retried.Ok && !data.HasProtected(pendingName),
        "retrying a complete setup restore did not clear its durable recovery guard");
    var setupPath = Path.Combine(data.BackupsRoot, profile.Id.ToString("N"),
        backup.Id.ToString("N") + ".backup", "setup.protected");
    File.AppendAllText(setupPath, "tampered");
    File.WriteAllText(world, "later");
    Require(!(await manager.RestoreCompleteSetupAsync(profile.Id, backup.Id)).Ok &&
        File.ReadAllText(world) == "later",
        "tampered setup checkpoint changed the live world");
});

await Check("Factorio mod import and Undo keep packages inside a managed world", async () =>
{
    using var data = Data("factorio-mod-import");
    var original = Path.Combine(root, "factorio-original-" + Guid.NewGuid().ToString("N") + ".zip");
    using (var archive = ZipFile.Open(original, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("save.dat").Open())) writer.Write("synthetic save");
    var profile = new ServerProfile
    {
        Kind = GameKinds.Factorio,
        Name = "Mod check",
        GamePort = FreePort(),
        ExecutablePath = fixture,
        Maintenance = new MaintenanceOptions { Enabled = true },
        Backups = new BackupOptions { MinimumFreeSpaceMb = 0 }
    };
    var importedSave = FactorioSetup.ImportCopy(data, profile.Id, original);
    Require(importedSave.Ok, "synthetic Factorio save import failed");
    profile.WorldId = importedSave.WorldId!;
    profile.WorldDirectory = importedSave.WorldDirectory!;
    var gameVersion = ServerAddOns.GameVersion(profile);
    var requiredVersion = gameVersion == "Unknown" ? "2.0" :
        string.Join('.', gameVersion.Split('.').Take(2));
    var source = Path.Combine(root, "fixture-mod-" + Guid.NewGuid().ToString("N") + ".zip");
    using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(archive.CreateEntry("fixturemod_1.0.0/info.json").Open());
        writer.Write(JsonSerializer.Serialize(new
        {
            name = "fixturemod",
            version = "1.0.0",
            factorio_version = requiredVersion
        }));
    }
    var manager = Manager(data);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "Factorio mod profile failed to save");
    var settingsCreated = await manager.CreateServerConfigurationAsync(profile.Id, "factorio-settings");
    Require(settingsCreated.Ok && File.Exists(Path.Combine(profile.WorldDirectory, "server-settings.json")) &&
        ServerFiles.ValidateChange(profile, "factorio-settings", new ServerFileChangeRequest("old",
            "{\"visibility\":{\"public\":true}}")).Code == "InvalidConfiguration",
        "managed Factorio settings were not created or public listing escaped the saved profile gate");
    var initial = await manager.ServerAddOnsAsync(profile.Id);
    var imported = await manager.ImportServerAddOnAsync(profile.Id, source,
        new ServerAddOnImportRequest(initial.StateToken));
    Require(imported.Ok && imported.View.Items.Single().Enabled &&
        File.Exists(Path.Combine(profile.WorldDirectory, "mods", "fixturemod_1.0.0.zip")),
        "Factorio mod import did not copy and enable the owner-selected package: " + imported.Code + " " + imported.Message);
    var stale = await manager.SetServerAddOnAsync(profile.Id,
        new ServerAddOnChangeRequest(initial.StateToken, imported.View.Items.Single().Key, false));
    Require(!stale.Ok && stale.Code == "AddOnsChanged", "stale mod state was accepted");
    var undo = await manager.UndoServerAddOnAsync(profile.Id,
        new ServerAddOnUndoRequest(imported.View.StateToken));
    Require(undo.Ok && undo.View.Items.Count == 0 &&
        !File.Exists(Path.Combine(profile.WorldDirectory, "mods", "fixturemod_1.0.0.zip")),
        "Factorio add-on Undo did not restore the prior package and activation state");
    data.SaveProtected($"addon-game-version-{profile.Id:N}.protected", Encoding.UTF8.GetBytes("0.0.0"));
    Require(ServerAddOns.VersionWarning(data, profile) is not null,
        "a changed game version did not require add-on review");
});

await Check("Bedrock world pack import and Undo keep shared server packs untouched", async () =>
{
    using var data = Data("bedrock-pack-import");
    var profile = Profile("bedrock-pack-world", "pack-world", FreePort());
    profile.Kind = GameKinds.MinecraftBedrock;
    profile.Maintenance.Enabled = true;
    profile.Backups = new BackupOptions { MinimumFreeSpaceMb = 0 };
    Directory.CreateDirectory(Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId));
    var shared = Path.Combine(profile.WorldDirectory, "behavior_packs");
    Directory.CreateDirectory(shared);
    File.WriteAllText(Path.Combine(shared, "owner-file.txt"), "untouched");
    var source = Path.Combine(root, "fixture-pack-" + Guid.NewGuid().ToString("N") + ".mcpack");
    var packId = Guid.NewGuid();
    using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
        writer.Write(JsonSerializer.Serialize(new
        {
            format_version = 2,
            header = new
            {
                name = "Fixture pack",
                uuid = packId.ToString(),
                version = new[] { 1, 0, 0 },
                min_engine_version = new[] { 1, 0, 0 }
            },
            modules = new[] { new { type = "data", uuid = Guid.NewGuid().ToString(), version = new[] { 1, 0, 0 } } }
        }));
    }
    var manager = Manager(data);
    Require((await manager.UpdateSettingsAsync(Settings(profile))).Ok, "Bedrock pack profile failed to save");
    var initial = await manager.ServerAddOnsAsync(profile.Id);
    var imported = await manager.ImportServerAddOnAsync(profile.Id, source,
        new ServerAddOnImportRequest(initial.StateToken));
    Require(imported.Ok && imported.View.Items.Single().Enabled &&
        imported.View.Items.Single().Type == "behavior pack",
        "Bedrock world pack was not activated: " + imported.Code + " " + imported.Message);
    var undo = await manager.UndoServerAddOnAsync(profile.Id,
        new ServerAddOnUndoRequest(imported.View.StateToken));
    Require(undo.Ok && undo.View.Items.Count == 0 &&
        File.ReadAllText(Path.Combine(shared, "owner-file.txt")) == "untouched",
        "Bedrock pack Undo changed the shared server folder or left the world pack active");
    var malformed = Path.Combine(root, "malformed-pack-" + Guid.NewGuid().ToString("N") + ".mcpack");
    using (var archive = ZipFile.Open(malformed, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open())) writer.Write("{}");
    Require(ServerAddOns.ValidatePackage(profile, malformed, out _) is not null,
        "a malformed pack manifest escaped typed package rejection");
});

Console.WriteLine($"Checks: {passed} passed, {failed} failed. Fixture data: {root}");
return failed == 0 ? 0 : 1;

static async Task DirectFixtureStop(ManagedRun run)
{
    using var pipe = new NamedPipeClientStream(".", run.StopPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await pipe.ConnectAsync(timeout.Token);
    using var writer = new StreamWriter(pipe) { AutoFlush = true };
    await writer.WriteLineAsync("stop");
    using var process = Process.GetProcessById(run.ProcessId!.Value);
    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    await process.WaitForExitAsync(exitTimeout.Token);
}

static async Task KillFixture(ManagedRun run)
{
    using var process = Process.GetProcessById(run.ProcessId!.Value);
    var actual = process.MainModule?.FileName;
    if (actual is null || !Path.GetFullPath(actual).Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase) ||
        process.StartTime.ToUniversalTime().Ticks != run.StartTimeUtcTicks)
        throw new Exception("refused to terminate a fixture whose exact recorded identity did not match");
    process.Kill(entireProcessTree: true);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    await process.WaitForExitAsync(timeout.Token);
}

static void ValidateStringBounds(JsonElement element, int maximum)
{
    if (element.ValueKind == JsonValueKind.String && (element.GetString()?.Length ?? 0) > maximum)
        throw new Exception("support report serialized an oversized string field");
    if (element.ValueKind == JsonValueKind.Array)
        foreach (var item in element.EnumerateArray()) ValidateStringBounds(item, maximum);
    if (element.ValueKind == JsonValueKind.Object)
        foreach (var property in element.EnumerateObject()) ValidateStringBounds(property.Value, maximum);
}

enum FixtureStopBehavior
{
    Normal,
    Failed,
    Unconfirmed
}

sealed class ObservationFixtureDriver : IGameServerDriver
{
    private readonly FixtureServerDriver inner = new();

    public GameHealthResult HealthResult { get; set; } =
        new(true, "FixtureProcessRunning", "Process running", "Synthetic fixture only; no readiness evidence.");
    public FixtureStopBehavior StopBehavior { get; set; }
    public string Kind => inner.Kind;
    public string DisplayName => inner.DisplayName;
    public bool ShowPortDiagnostics => inner.ShowPortDiagnostics;
    public bool SupportsCrashRecovery => inner.SupportsCrashRecovery;
    public bool SupportsBackups => inner.SupportsBackups;
    public string? ManagedSaveDirectory(ServerProfile profile) => inner.ManagedSaveDirectory(profile);
    public string ManagedExecutablePath(ServerProfile profile) => inner.ManagedExecutablePath(profile);
    public IReadOnlyList<GamePort> Ports(ServerProfile profile) => inner.Ports(profile);
    public string? JoinAddress(ServerProfile profile, string? publicIp) => inner.JoinAddress(profile, publicIp);
    public GameValidation? ValidateForStart(ServerProfile profile) => inner.ValidateForStart(profile);
    public void PrepareStart(ServerProfile profile, ManagedRun run) => inner.PrepareStart(profile, run);
    public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => inner.Start(profile, run);
    public GameHealthResult Health(ManagedRun run) => HealthResult;
    public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => StopBehavior switch
    {
        FixtureStopBehavior.Failed => Task.FromResult(new GameStopResult("StopFailed",
            "Synthetic Stop failed without signaling the process.", 1)),
        FixtureStopBehavior.Unconfirmed => Task.FromResult(new GameStopResult("FixtureStopReturned",
            "Synthetic Stop returned without an observed exit.", 0)),
        _ => inner.StopAsync(process, run)
    };
}

sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset utcNow = utcNow;
    public override DateTimeOffset GetUtcNow() => utcNow;
    public void Advance(TimeSpan value) => utcNow = utcNow.Add(value);
}

sealed class RecordingPowerGuard : IHostingPowerGuard
{
    public List<bool> Requests { get; } = [];
    public bool IsActive { get; private set; }
    public string? LastError => null;
    public void SetRequired(bool required)
    {
        Requests.Add(required);
        IsActive = required;
    }
    public void Dispose() => SetRequired(false);
}

sealed class BlockingPowerGuard : IHostingPowerGuard
{
    private int blockNext;
    private readonly ManualResetEventSlim released = new(true);
    public ManualResetEventSlim Entered { get; } = new(false);
    public bool IsActive { get; private set; }
    public string? LastError => null;
    public void Arm()
    {
        Entered.Reset();
        released.Reset();
        Interlocked.Exchange(ref blockNext, 1);
    }
    public void ReleaseBlock() => released.Set();
    public void SetRequired(bool required)
    {
        if (required && Interlocked.Exchange(ref blockNext, 0) == 1)
        {
            Entered.Set();
            released.Wait();
        }
        IsActive = required;
    }
    public void Dispose()
    {
        released.Set();
        released.Dispose();
        Entered.Dispose();
    }
}

sealed class OversizedSharedCapture(string root) : ISharedWorldCaptureAdapter
{
    public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId) =>
        new(1, SharedWorldCaptureKinds.PostStopBackup, backupId, DateTimeOffset.UtcNow,
            [new SharedWorldFile("world.dat", SharedWorldService.MaximumSharedWorldBytes + 1,
                new string('0', 64))], root);
}

sealed class SkewedSharedCapture(string root) : ISharedWorldCaptureAdapter
{
    public DateTimeOffset CapturedUtc { get; set; }

    public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId)
    {
        var path = Path.Combine(root, "world.dat");
        var bytes = File.ReadAllBytes(path);
        return new(1, SharedWorldCaptureKinds.PostStopBackup, backupId, CapturedUtc,
            [new SharedWorldFile("world.dat", bytes.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)))], root,
            new ServerSetupSnapshot(2, profile.Id, profile.Kind, profile.WorldId,
                "Unknown", "", [], [], profile.GamePort, profile.Crossplay, profile.PublicListing));
    }
}

sealed class TruncatedRosterHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/shared-world/roster/revisions",
                StringComparison.Ordinal) == true &&
            request.RequestUri.Query == "?offset=8")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        return base.SendAsync(request, cancellationToken);
    }
}
