using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class GameCompatibilityChecks
{
    internal static void Run(string root)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        using var data = new LocalData(Path.Combine(root, "requirements-" + Guid.NewGuid().ToString("N")));
        var profile = new ServerProfile { Id = Guid.NewGuid(), Kind = GameKinds.Valheim, WorldId = "Synthetic", WorldDirectory = Path.Combine(data.RootPath, "synthetic") };
        Directory.CreateDirectory(profile.WorldDirectory);
        Require(GameCompatibility.Observe(data, profile, default).Requirements is { RequiredVersion: null, VersionSource: "Unknown" }, "unknown cannot be inferred from a file's existence");
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "version.txt"), "9.9.9");
        Require(GameCompatibility.Observe(data, profile, default).Requirements?.RequiredVersion is null, "unreviewed version file was accepted");
        GameCompatibility.SaveOwnerVersion(data, profile, "0.219.16");
        Require(GameCompatibility.Observe(data, profile, default).Requirements is { RequiredVersion: "0.219.16", VersionSource: "OwnerReported" }, "owner version lost its source label");
        profile.WorldId = "Changed";
        Require(GameCompatibility.Observe(data, profile, default).Requirements?.RequiredVersion is null, "owner version leaked across changed profile identity");
        profile.WorldId = "Synthetic";
        GameCompatibility.SaveOwnerVersion(data, profile, null);
        Require(GameCompatibility.Observe(data, profile, default).Requirements?.RequiredVersion is null, "clearing manual requirement failed");
        foreach (var invalid in new[] { "C:\\secret", "https://host/1.2", "1.2\n3", "1.2\u202e", new string('1', 49) })
            Require(!GameCompatibility.ValidVersion(invalid), "unsafe version text accepted");
        Require(GameCompatibility.CompareVersions("1.2", null) == "Unknown" && GameCompatibility.CompareVersions("1.2", "1.3") == "Mismatch", "version uncertainty became a match");

        var mods = Path.Combine(profile.WorldDirectory, "mods"); Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "mod-list.json"), "{\"mods\":[{\"name\":\"base\",\"enabled\":true},{\"name\":\"reviewed\",\"enabled\":true}]}");
        using (var zip = ZipFile.Open(Path.Combine(mods, "reviewed_1.2.3.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("reviewed_1.2.3/info.json").Open());
            writer.Write("{\"name\":\"reviewed\",\"version\":\"1.2.3\",\"factorio_version\":\"2.0\"}");
        }
        var requiredMods = GameCompatibility.ReadFactorioMods(mods, default);
        Require(requiredMods.Count == 1 && requiredMods[0].Id == "reviewed", "reviewed Factorio activation/metadata did not produce the enabled mod");
        Require(GameCompatibility.CompareAddOns(requiredMods, requiredMods) == "Match" &&
            GameCompatibility.CompareAddOns(requiredMods, []) == "Mismatch" && GameCompatibility.CompareAddOns(requiredMods, null) == "Unknown", "mod comparison collapsed missing metadata");
        var duplicate = Path.Combine(mods, "reviewed_2.0.0.zip");
        using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("reviewed_2.0.0/info.json").Open()); writer.Write("{\"name\":\"reviewed\",\"version\":\"2.0.0\"}"); }
        profile.Kind = GameKinds.Factorio;
        Require(GameCompatibility.Observe(data, profile, default).Requirements?.AddOnState == "Unknown", "ambiguous ZIP versions picked an arbitrary effective mod version");
        File.Delete(duplicate);
        Require(GameCompatibility.Observe(data, profile, default).Requirements?.AddOnState == "Known", "valid mod inventory unavailable");
        File.WriteAllText(Path.Combine(mods, "mod-list.json"), "{\"mods\":[{\"name\":\"base\",\"enabled\":true},{\"name\":\"space-age\",\"enabled\":true}]}");
        Require(GameCompatibility.Observe(data, profile, default).Requirements?.AddOnState == "Unknown", "unobserved bundled component was discarded as a match");
        var bundledRoot = Path.Combine(profile.WorldDirectory, "synthetic-game-data");
        Directory.CreateDirectory(Path.Combine(bundledRoot, "space-age"));
        File.WriteAllText(Path.Combine(bundledRoot, "space-age", "info.json"), "{\"name\":\"space-age\",\"version\":\"2.0.42\"}");
        var expanded = GameCompatibility.ReadFactorioMods(mods, default, bundledRoot);
        Require(expanded.Single().Id == "space-age" && GameCompatibility.CompareAddOns(expanded, []) == "Mismatch", "enabled bundled expansion difference was erased");
        File.WriteAllText(Path.Combine(mods, "mod-list.json"), "{\"mods\":[{\"name\":\"missing\",\"enabled\":true}]}");
        Require(GameCompatibility.Observe(data, profile, default).Requirements is { AddOnState: "Unknown", AddOns.Count: 0 }, "missing enabled package was reported compatible");

        var java = Path.Combine(profile.WorldDirectory, "java"); Directory.CreateDirectory(java);
        var jar = Path.Combine(java, "server.jar");
        using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("version.json").Open())) writer.Write("{\"id\":\"1.21.1\"}");
            using (var writer = new StreamWriter(zip.CreateEntry("META-INF/MANIFEST.MF").Open())) writer.Write("Main-Class: net.minecraft.bundler.Main\n");
        }
        var hash = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(jar)));
        MinecraftSetup.WriteManagedJavaProvenance(java, "1.21.1", hash);
        profile.Kind = GameKinds.MinecraftJava; profile.Minecraft = new() { ServerJarPath = jar };
        Require(GameCompatibility.ObservedVersion(profile, default) == "1.21.1", "hash-bound Java installation version was not observed");
        File.AppendAllText(jar, "tampered");
        Require(GameCompatibility.ObservedVersion(profile, default) is null, "changed Java bytes retained observed compatibility");

        profile.Kind = GameKinds.MinecraftBedrock;
        var world = Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId); Directory.CreateDirectory(world);
        var packId = Guid.NewGuid();
        var packRoot = Path.Combine(world, "resource_packs", "reviewed"); Directory.CreateDirectory(packRoot);
        File.WriteAllText(Path.Combine(packRoot, "manifest.json"), JsonSerializer.Serialize(new { header = new { uuid = packId.ToString(), version = new[] { 1, 2, 3 } } }));
        File.WriteAllText(Path.Combine(world, "world_resource_packs.json"), JsonSerializer.Serialize(new[] { new { pack_id = packId.ToString(), version = new[] { 1, 2, 3 } } }));
        Require(GameCompatibility.Observe(data, profile, default).Requirements?.AddOns.Single() is { Version: "1.2.3", Type: "BedrockResourcePack" }, "reviewed Bedrock activation metadata was omitted");
        File.Delete(Path.Combine(packRoot, "manifest.json"));
        Require(GameCompatibility.Observe(data, profile, default).Requirements?.AddOnState == "Unknown", "external or missing Bedrock pack became a reviewed inventory");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { GameCompatibility.Observe(data, profile, cancelled.Token); throw new Exception("cancelled observation ran"); }
        catch (OperationCanceledException) { }
        data.SaveSettings(new HostSettings { Profiles = [profile] });
        var authClock = new RequirementsClock(DateTimeOffset.Parse("2026-10-08T15:00:00Z"));
        var pairing = new PairingService(data, authClock);
        // Exercise the real pure pairing/lineage state machine in synthetic storage.
        // An invented device without its issuing generation correctly fails closed.
        var invite = pairing.IssueServer(profile.Id, false, false, "https://127.0.0.1:5131", new string('A', 64), refresh: false);
        var credential = pairing.Activate(new(profile.Id, invite.Code, ServerScope: true));
        Require(credential is not null, "synthetic status-only pairing failed");
        var deviceId = credential!.DeviceId;
        Require(pairing.AuthorizeGameRequirements(deviceId, profile.Id, out _).Ok, "assigned status-only access was denied");
        Require(!pairing.AuthorizeGameRequirements(deviceId, Guid.NewGuid(), out _).Ok, "unassigned requirements were exposed");
        Require(pairing.SetAccessExpiry(deviceId, new(AccessExpiresUtc: authClock.Now.AddMinutes(1))).Ok, "synthetic owner deadline failed");
        authClock.Now = authClock.Now.AddMinutes(1);
        Require(pairing.AuthorizeGameRequirements(deviceId, profile.Id, out _).Code == "AccessExpired", "exact owner deadline equality was accepted");
        Require(pairing.SetAccessExpiry(deviceId, new(Clear: true)).Ok && pairing.AuthorizeGameRequirements(deviceId, profile.Id, out _).Ok, "cleared deadline did not restore same credential eligibility");
        Require(pairing.SetServerAccess(deviceId, [], null, [profile.Id]).Ok &&
            pairing.AuthorizeGameRequirements(deviceId, profile.Id, out _).Code == "PermissionDenied", "assignment removal did not affect requirements immediately");
        Require(pairing.Revoke(deviceId).Ok && !pairing.AuthorizeGameRequirements(deviceId, profile.Id, out _).Ok, "revocation retained requirement access");
    }
    private sealed class RequirementsClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
