using System.Security.Cryptography;
using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Fresh synthetic files and protected setup only. No app, game, process,
// listener, browser, native dialog or existing backup/world is opened.
internal static class BackupCompletionIntegrityChecks
{
    internal static void Run(string root)
    {
        var checkRoot = Path.Combine(Path.GetFullPath(root), "backup-completion-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(checkRoot, "synthetic-world");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var listBytes = Encoding.UTF8.GetBytes("//" + new string('s', 40) + "\n");
        Require(listBytes.Length == 43, "the generated reviewed configuration length changed");
        File.WriteAllBytes(Path.Combine(source, "permittedlist.txt"), listBytes);
        File.WriteAllText(Path.Combine(source, "adminlist.txt"), "// synthetic administrators\n");
        File.WriteAllText(Path.Combine(source, "bannedlist.txt"), "// synthetic exclusions\n");
        File.WriteAllBytes(Path.Combine(source, "nested", "world.bin"), Enumerable.Range(0, 513).Select(value => (byte)value).ToArray());
        File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
        var sourceBefore = Fingerprints(source);
        var profile = new ServerProfile
        {
            Id = Guid.NewGuid(),
            Kind = GameKinds.Valheim,
            Name = "Synthetic completion integrity",
            WorldId = "SyntheticCompletion",
            WorldDirectory = source,
            ExecutablePath = "",
            GamePort = 2456,
            Backups = new() { RetentionCount = 50, MinimumFreeSpaceMb = 0 }
        };
        using var data = new LocalData(Path.Combine(checkRoot, "synthetic-app-data"));
        data.SaveSettings(new HostSettings { Profiles = [profile] });
        var service = new WorldBackupService(data, TimeProvider.System, _ => 8L * 1024 * 1024 * 1024);
        Require(service.BeforeCompletionVerificationForChecks is null, "a production backup installed a completion mutation hook");
        var baseline = service.Create(profile, BackupKinds.Rolling);
        Require(baseline.Ok && baseline.Backup is { SetupIncluded: true }, "the unchanged synthetic backup did not complete with setup");
        var retainedId = baseline.Backup!.Id;
        var retainedRoot = BackupRoot(data, profile, retainedId);
        var retainedBefore = Fingerprints(retainedRoot);
        Require(service.Verify(profile, retainedId).Ok, "the initial completed synthetic backup did not verify");

        foreach (var change in new[] { "same-size-payload", "longer-payload", "changed-setup", "missing-setup" })
        {
            string? candidateRoot = null;
            Guid candidateId = Guid.Empty;
            var mutationApplied = false;
            service.BeforeCompletionVerificationForChecks = stage =>
            {
                candidateRoot = stage;
                Require(Path.GetDirectoryName(stage) == Path.Combine(data.BackupsRoot, profile.Id.ToString("N")) &&
                    stage.EndsWith(".staging", StringComparison.Ordinal) &&
                    Guid.TryParseExact(Path.GetFileNameWithoutExtension(stage), "N", out candidateId),
                    "the completion hook was given a path outside the generated candidate");
                Require(!File.Exists(Path.Combine(stage, "complete.json")) &&
                    data.LoadBackupCatalog().Records.All(item => item.Id != candidateId),
                    "completion was written or catalogued before destination verification");
                var path = Path.Combine(stage, "payload", "permittedlist.txt");
                var copied = File.ReadAllBytes(path);
                Require(copied.SequenceEqual(listBytes), "the generated payload differed before the test mutation");
                switch (change)
                {
                    case "same-size-payload":
                        copied[10] ^= 1;
                        File.WriteAllBytes(path, copied);
                        break;
                    case "longer-payload":
                        File.WriteAllBytes(path, [.. copied, 1, 2, 3, 4]);
                        break;
                    case "changed-setup":
                        var setupPath = Path.Combine(stage, "setup.protected");
                        var setup = File.ReadAllBytes(setupPath);
                        setup[setup.Length / 2] ^= 1;
                        File.WriteAllBytes(setupPath, setup);
                        break;
                    case "missing-setup":
                        File.Delete(Path.Combine(stage, "setup.protected"));
                        break;
                }
                mutationApplied = true;
            };
            var failed = service.Create(profile, BackupKinds.Rolling);
            Require(mutationApplied && !failed.Ok && failed.Code == "BackupCompletionIntegrityFailed" && failed.Backup is null,
                "a changed candidate was completed or did not return the fixed integrity failure");
            Require(failed.Message == "The server stopped, but its rolling backup failed: The copied backup did not pass its final integrity check. Existing backups and the live world were kept.",
                "a completion failure reflected an exception detail or private path");
            Require(candidateRoot is not null && candidateId != Guid.Empty && !Directory.Exists(candidateRoot) &&
                !Directory.Exists(BackupRoot(data, profile, candidateId)),
                "a failed candidate survived as a staged or promoted completed backup");
            var catalog = data.LoadBackupCatalog();
            Require(catalog.Records.Count == 1 && catalog.Records.Single().Id == retainedId &&
                catalog.Failures.Single(item => item.ProfileId == profile.Id).Code == "BackupCompletionIntegrityFailed" &&
                catalog.Failures.Single(item => item.ProfileId == profile.Id).Message ==
                    "The copied backup did not pass its final integrity check. Existing backups and the live world were kept.",
                "a failed candidate changed completed catalog membership or persisted raw error text");
            ExpectUnavailableTransfer(service, profile, candidateId);
            Require(Fingerprints(source).SequenceEqual(sourceBefore) && Fingerprints(retainedRoot).SequenceEqual(retainedBefore),
                "failed candidate verification changed the source world or an earlier completed backup");
        }

        service.BeforeCompletionVerificationForChecks = null;
        var unchanged = service.Create(profile, BackupKinds.Rolling);
        Require(unchanged.Ok && unchanged.Backup is { SetupIncluded: true } &&
            service.Verify(profile, unchanged.Backup.Id).Ok && service.List(profile.Id).Count == 2,
            "an unchanged candidate could not complete after integrity failures");
        var transferable = new PostStopBackupCaptureAdapter(service).ReadVerified(profile, unchanged.Backup!.Id);
        Require(transferable.Files.Count == sourceBefore.Count && transferable.Setup is not null &&
            transferable.Kind == SharedWorldCaptureKinds.PostStopBackup,
            "an unchanged completed candidate was not eligible for the existing verified publication adapter");
        Require(Fingerprints(source).SequenceEqual(sourceBefore) && Fingerprints(retainedRoot).SequenceEqual(retainedBefore),
            "successful completion changed the source world or earlier backup bytes");
    }

    private static void ExpectUnavailableTransfer(WorldBackupService service, ServerProfile profile, Guid candidateId)
    {
        try { new PostStopBackupCaptureAdapter(service).ReadVerified(profile, candidateId); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("a failed completion candidate entered the verified publication adapter");
    }

    private static string BackupRoot(LocalData data, ServerProfile profile, Guid id) =>
        Path.Combine(data.BackupsRoot, profile.Id.ToString("N"), id.ToString("N") + ".backup");

    private static IReadOnlyList<string> Fingerprints(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + new FileInfo(path).Length + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .Order(StringComparer.Ordinal).ToArray();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
