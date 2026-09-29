using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            one.Bind(new IPEndPoint(IPAddress.Any, port));
            two.Bind(new IPEndPoint(IPAddress.Any, port + 1));
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
GameServerRegistry Games(LocalData data) => new(data, includeFixture: true);
HostManager Manager(LocalData data) => new(data, Games(data));

await Check("owner diagnostics reuse canonical state and support export stays bounded and redacted", async () =>
{
    using var data = Data("owner-diagnostics");
    var profile = Profile("Support fixture", "support-world", FreePort());
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
        activity, operations, data.ReadSupportLogMetadata());
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

await Check("storage v1 migrates pairing to v2 and rejects downgrade or newer schemas", () =>
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
        Require(migrated.SchemaVersion == 2 && migrated.Devices.Count == 1 &&
            migrated.Devices[0].Id == deviceId && migrated.Devices[0].Name == "Migrated Friend PC" &&
            migrated.Devices[0].AssignedProfileIds!.SequenceEqual([profileId]) &&
            migrated.Devices[0].CanStart && !migrated.Devices[0].CanStop &&
            migrated.Devices[0].AccessExpiresUtc is null && migrated.ServerInvites.Count == 1 &&
            migrated.ServerInvites[0].Generation == generation && migrated.CredentialRenewals.Count == 1,
            "v1 pairing devices, assignment, permissions, invite lineage, or renewal receipt were lost");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(migrationRoot, "storage-schema.json")));
        Require(marker.RootElement.GetProperty("version").GetInt32() == 2,
            "the directory schema marker did not advance to v2");
        var migratedBytes = migratedData.LoadProtected("pairing-state.protected")!;
        var migratedSnapshot = JsonSerializer.Deserialize<PairingPersistentState>(migratedBytes, json);
        Require(migratedSnapshot?.SchemaVersion == 2,
            "the protected pairing snapshot did not migrate to schema v2");
    }

    var pairingBeforeDowngrade = File.ReadAllBytes(Path.Combine(migrationRoot, "pairing-state.protected"));
    RequireThrows<InvalidDataException>(() =>
    {
        using var _ = new LocalData(migrationRoot, 5131, supportedStorageSchemaVersion: 1);
    }, "a simulated v1 binary opened a v2 access-expiry data root");
    Require(File.ReadAllBytes(Path.Combine(migrationRoot, "pairing-state.protected"))
            .SequenceEqual(pairingBeforeDowngrade),
        "the rejected downgrade modified protected pairing data");

    var interruptedRoot = Path.Combine(root, "storage-v2-interrupted-pairing-v1");
    Directory.CreateDirectory(interruptedRoot);
    File.WriteAllText(Path.Combine(interruptedRoot, "pairing-state.protected"), protectedPayload);
    File.WriteAllText(Path.Combine(interruptedRoot, "storage-schema.json"), "{\"version\":2}");
    using (var resumedData = new LocalData(interruptedRoot))
    {
        var resumed = resumedData.LoadPairingState();
        Require(resumed.SchemaVersion == 2 && resumed.Devices.Single().Id == deviceId &&
            resumed.ServerInvites.Single().Generation == generation && resumed.CredentialRenewals.Count == 1,
            "a marker-first interrupted migration did not preserve and upgrade its v1 pairing snapshot");
    }

    var newerRoot = Path.Combine(root, "storage-newer-schema");
    Directory.CreateDirectory(newerRoot);
    File.WriteAllText(Path.Combine(newerRoot, "storage-schema.json"), "{\"version\":3}");
    RequireThrows<InvalidDataException>(() =>
    {
        using var _ = new LocalData(newerRoot);
    }, "the current binary opened an unknown newer storage schema");
    Require(File.ReadAllText(Path.Combine(newerRoot, "storage-schema.json")).Contains("3", StringComparison.Ordinal),
        "newer-schema rejection rewrote the unsupported marker");
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
        Require(observed is { State: "Process running", PlayerCountTrusted: true },
            "the observation supervisor did not populate the shared cache");
    }
    finally
    {
        var cleanup = await manager.StopAsync(profile.Id);
        Require(cleanup.Ok, $"fixture cleanup failed: {cleanup.Code} {cleanup.Message}");
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
    socket.Bind(new IPEndPoint(IPAddress.Any, port));
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
    var productionRegistry = new GameServerRegistry(data);
    Require(!productionRegistry.TryGet(GameKinds.Fixture, out _),
        "the synthetic fixture driver was enabled without an explicit test opt-in");
    var registry = Games(data);
    Require(registry.All.Select(driver => driver.Kind).Order().SequenceEqual(new[]
        { GameKinds.Custom, GameKinds.Fixture, GameKinds.MinecraftBedrock, GameKinds.MinecraftJava, GameKinds.Valheim }),
        "The built-in, custom, and fixture games were not separately registered");
    var profile = Profile("unknown-game", "unknown-game", FreePort());
    profile.Kind = "UnregisteredGame";
    var manager = new HostManager(data, registry);
    var result = await manager.UpdateSettingsAsync(Settings(profile));
    Require(!result.Ok && result.Code == "InvalidSettings", "an unregistered game profile was accepted");
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
    game.Bind(new IPEndPoint(IPAddress.Any, gamePort));
    query.Bind(new IPEndPoint(IPAddress.Any, gamePort + 1));
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
    Require(diagnostics.Games.Single().State == "Open on PC" &&
        diagnostics.Games.Single().RouteKind == "Direct" && diagnostics.Games.Single().Kind == GameKinds.Valheim,
        "open Valheim Steam UDP ports or their direct route were not reported");
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
    var manager = new HostManager(data, new GameServerRegistry([driver]), clock);
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

    var restarted = new HostManager(data, new GameServerRegistry([driver]), clock);
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
    var manager = new HostManager(data, new GameServerRegistry([driver]));
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
    var restarted = new HostManager(data, new GameServerRegistry([driver]));
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

    var stageRoot = Path.Combine(data.BackupsRoot, profile.Id.ToString("N"));
    var abandoned = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".staging");
    Directory.CreateDirectory(abandoned);
    File.WriteAllText(Path.Combine(abandoned, "partial"), "partial copy");
    _ = new WorldBackupService(data, clock);
    Require(!Directory.Exists(abandoned), "interrupted staging directory was not cleaned safely");

    var noSpace = new WorldBackupService(data, clock, _ => 0);
    File.WriteAllText(marker, "needs space");
    Require(!noSpace.Create(profile, BackupKinds.Rolling).Ok,
        "backup ignored the configured destination free-space boundary");
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
    _ = new WorldBackupService(data, clock);
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
