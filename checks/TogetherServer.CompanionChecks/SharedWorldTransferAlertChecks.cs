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

        CheckInterruptedSignedHistoryWrite();
        Console.WriteLine("PASS shared-world transfer alert transitions");
    }

    private static void CheckInterruptedSignedHistoryWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "TogetherServer-signed-history-checks",
            Guid.NewGuid().ToString("N"));
        var history = Path.Combine(root, "signed-history");
        Directory.CreateDirectory(history);
        try
        {
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var draft = new SharedWorldVersion(4, Guid.NewGuid(), 1, null,
                Guid.NewGuid(), GameKinds.Valheim, "fixture-world", DateTimeOffset.UtcNow,
                SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
                new SharedWorldPortableSetup(2456, false, "fixture", [], []),
                [new SharedWorldFile("world.dat", 1,
                    Convert.ToHexString(SHA256.HashData([1])))], "", "", "");
            var first = SharedWorldService.SignVersion(draft, signer);
            FriendLink.KeepSignedManifest(root, first);
            var usage = FriendLink.SignedHistoryUsage(root);
            Require(usage.Count == 1 && usage.Bytes > 0,
                "a verified signed manifest was not audited");
            var orphan = Path.Combine(history, "0000000000000000",
                "1-" + first.VersionHash + ".json.new");
            File.WriteAllBytes(orphan, [1, 2, 3]);
            RequireThrows<InvalidDataException>(() => FriendLink.SignedHistoryUsage(root),
                "an interrupted signed-history write was omitted from the archive audit");
            File.Delete(orphan);

            var next = SharedWorldService.SignVersion(draft with
            {
                Number = 2,
                ParentHash = first.VersionHash
            }, signer);
            FriendLink.KeepSignedManifest(root, next);
            Require(FriendLink.SignedHistoryUsage(root).Count == 2 &&
                File.Exists(Path.Combine(history, "0000000000000000", "1-" + first.VersionHash + ".json")),
                "a signed manifest was lost while another version was archived");
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

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception(message);
    }
}
