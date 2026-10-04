using System.Security.Cryptography;
using TogetherServer;

internal static class SharedWorldTransferAlertChecks
{
    internal static void Run()
    {
        var health = new SharedWorldTransferHealth();
        var world = Guid.NewGuid();
        var otherWorld = Guid.NewGuid();
        const string version = "verified-version-hash";

        var first = health.Begin(world);
        first.Progress(version, 0, 64);
        health.Complete(world, first, Failure("TransferInterrupted"));
        Require(health.Issue(world)?.State == "Transfer interrupted", "first interruption was called a stall");

        var second = health.Begin(world);
        second.Progress(version, 0, 64);
        health.Complete(world, second, Failure("TransferInterrupted"));
        Require(health.Issue(world)?.State == "Stalled", "repeated no-progress receipt was not a stall");
        Require(health.Issue(otherWorld) is null, "a transfer alert leaked to another world");

        var network = health.Begin(otherWorld);
        health.Complete(otherWorld, network, Failure("TransferFailed"));
        Require(health.Issue(otherWorld)?.State == "Transfer unavailable",
            "network failure before payload receipt was called a stall");
        var policy = health.Begin(otherWorld);
        policy.Progress(version, 0, 64);
        health.Complete(otherWorld, policy, Failure("PermissionDenied"));
        Require(health.Issue(otherWorld)?.State == "Transfer unavailable",
            "an access denial was called a stall");
        for (var count = 0; count < 2; count++)
        {
            var failedNetwork = health.Begin(otherWorld);
            health.Complete(otherWorld, failedNetwork, Failure("NetworkUnavailable"));
        }
        Require(health.Issue(otherWorld)?.State == "Transfer unavailable",
            "repeated failures before selecting a payload were called a stall");

        health.Reset(world);
        var dropped = health.Begin(world);
        dropped.Progress(version, 0, 64);
        health.Complete(world, dropped, Failure("NetworkUnavailable"));
        Require(health.Issue(world)?.State == "Transfer interrupted",
            "first post-payload disconnect was called a stall");
        var droppedAgain = health.Begin(world);
        droppedAgain.Progress(version, 0, 64);
        health.Complete(world, droppedAgain, Failure("NetworkUnavailable"));
        Require(health.Issue(world)?.State == "Stalled",
            "repeated post-payload disconnects at the same offset were not a stall");

        var cancelledAfterProgress = health.Begin(world);
        cancelledAfterProgress.Progress(version, 0, 64);
        cancelledAfterProgress.Progress(version, 32, 64);
        health.Cancel(world, cancelledAfterProgress);
        Require(health.Issue(world) is null,
            "a caller-cancelled retry kept a stale stall after retaining new bytes");
        var cancelledBeforeProgress = health.Begin(world);
        cancelledBeforeProgress.Progress(version, 32, 64);
        health.Complete(world, cancelledBeforeProgress, Failure("NetworkUnavailable"));
        Require(health.Issue(world)?.State == "Transfer interrupted",
            "the fixture did not begin with an interruption");
        health.Cancel(world, health.Begin(world));
        Require(health.Issue(world) is null,
            "a caller-cancelled retry kept an earlier transport alert");
        var userCancelled = health.Begin(world);
        userCancelled.Progress(version, 32, 64);
        health.Complete(world, userCancelled, Failure("TransferCanceled"));
        Require(health.Issue(world)?.State == "Transfer unavailable",
            "a user cancellation was called a transport stall");

        var advancing = health.Begin(world);
        advancing.Progress(version, 0, 64);
        advancing.Progress(version, 32, 64);
        health.Complete(world, advancing, Failure("TransferInterrupted"));
        Require(health.Issue(world)?.State == "Transfer interrupted" &&
            health.Issue(world)?.ReceivedBytes == 32,
            "new bytes did not clear the no-progress classification");

        var lowSpace = health.Begin(world);
        lowSpace.Progress(version, 32, 64);
        health.Complete(world, lowSpace, Failure("InsufficientSpace"));
        Require(health.Issue(world)?.State == "Low space" &&
            health.Issue(world)?.ReceivedBytes == 32, "low space was not reported immediately");
        var historyFull = health.Begin(world);
        health.Complete(world, historyFull, Failure("SignedHistoryFull"));
        Require(health.Issue(world)?.State == "Signed history full",
            "signed-history capacity was hidden as a generic transfer failure");
        health.Report(otherWorld, "Signed history full", "Another PC is needed.");
        Require(health.Issue(otherWorld)?.State == "Signed history full" &&
            health.Issue(otherWorld)?.Message == "Another PC is needed.",
            "a pre-transfer history check did not surface its capacity alert");
        health.Complete(world, health.Begin(world), new(true, "AlreadyReceived", "Verified."));
        Require(health.Issue(world) is null, "verified receipt left an old alert");

        var inFlight = health.Begin(world);
        inFlight.Progress(version, 0, 64);
        health.Reset(world);
        health.Complete(world, inFlight, Failure("InsufficientSpace"));
        Require(health.Issue(world) is null, "an old transfer restored an alert after consent changed");

        CheckFixtureSpaceCeiling();
        CheckInterruptedSignedHistoryWrite();
        Console.WriteLine("PASS shared-world transfer alert transitions");
    }

    private static void CheckFixtureSpaceCeiling()
    {
        var journeyRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
            "TogetherServer-space-checks", "shared-world-journey", Guid.NewGuid().ToString("N")));
        var dataRoot = Path.Combine(journeyRoot, "friend-test");
        var vault = Path.Combine(dataRoot, "received-shared-worlds",
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vault);
        var names = new[] { GameServerRegistry.FixtureOptInEnvironmentVariable,
            "TOGETHERSERVER_FIXTURE_ROOT", "TOGETHERSERVER_DATA_DIR",
            SharedWorldFixtureSpace.CeilingEnvironmentVariable };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            Environment.SetEnvironmentVariable(names[0], "1");
            Environment.SetEnvironmentVariable(names[1], dataRoot);
            Environment.SetEnvironmentVariable(names[2], dataRoot);
            Environment.SetEnvironmentVariable(names[3], "0");
            Require(SharedWorldFixtureSpace.AvailableBytes(vault) == 0,
                "the disposable Friend ceiling did not lower available space");
            Environment.SetEnvironmentVariable(names[3], long.MaxValue.ToString());
            Require(SharedWorldFixtureSpace.AvailableBytes(vault) <=
                    new DriveInfo(Path.GetPathRoot(vault)!).AvailableFreeSpace,
                "the fixture ceiling raised real available space");
            Environment.SetEnvironmentVariable(names[3], "0");
            Environment.SetEnvironmentVariable(names[2], Path.Combine(journeyRoot, "other"));
            Require(SharedWorldFixtureSpace.AvailableBytes(vault) ==
                    new DriveInfo(Path.GetPathRoot(vault)!).AvailableFreeSpace,
                "a ceiling escaped its exact disposable data root");
        }
        finally
        {
            for (var index = 0; index < names.Length; index++)
                Environment.SetEnvironmentVariable(names[index], previous[index]);
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "TogetherServer-space-checks", "shared-world-journey")) + Path.DirectorySeparatorChar;
            if (!journeyRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The disposable space check directory changed unexpectedly.");
            Directory.Delete(journeyRoot, recursive: true);
        }
    }

    private static void CheckInterruptedSignedHistoryWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "TogetherServer-signed-history-checks",
            Guid.NewGuid().ToString("N"));
        var history = Path.Combine(root, "signed-history");
        Directory.CreateDirectory(history);
        try
        {
            File.WriteAllBytes(Path.Combine(history, "interrupted.new"), [1, 2, 3]);
            var usage = FriendLink.SignedHistoryUsage(root);
            Require(usage == (1, 3),
                "an interrupted signed-history write was omitted from the storage budget");

            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var draft = new SharedWorldVersion(4, Guid.NewGuid(), 1, null,
                Guid.NewGuid(), GameKinds.Valheim, "fixture-world", DateTimeOffset.UtcNow,
                SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
                new SharedWorldPortableSetup(2456, false, "fixture", [], []),
                [new SharedWorldFile("world.dat", 1,
                    Convert.ToHexString(SHA256.HashData([1])))], "", "", "");
            var first = SharedWorldService.SignVersion(draft, signer);
            FriendLink.KeepSignedManifest(root, first);
            usage = FriendLink.SignedHistoryUsage(root);
            Require(usage.Count == 2 && usage.Bytes > 3,
                "a verified manifest and interrupted write were not both counted");

            for (var number = usage.Count; number < 4096; number++)
                File.WriteAllBytes(Path.Combine(history, $"interrupted-{number}.new"), []);
            var next = SharedWorldService.SignVersion(draft with
            {
                Number = 2,
                ParentHash = first.VersionHash
            }, signer);
            var blocked = false;
            try { FriendLink.KeepSignedManifest(root, next); }
            catch (IOException ex) { blocked = ex.Message.Contains("full", StringComparison.OrdinalIgnoreCase); }
            Require(blocked && FriendLink.SignedHistoryUsage(root).Count == 4096 &&
                File.Exists(Path.Combine(history, first.VersionHash + ".json")),
                "the archive accepted an entry beyond its count cap or removed its verified manifest");
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "TogetherServer-signed-history-checks")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The signed-history check directory changed unexpectedly.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static ReceivedSharedWorldResult Failure(string code) =>
        new(false, code, "The transfer needs attention.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
