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
        health.Complete(world, health.Begin(world), new(true, "AlreadyReceived", "Verified."));
        Require(health.Issue(world) is null, "verified receipt left an old alert");

        var inFlight = health.Begin(world);
        inFlight.Progress(version, 0, 64);
        health.Reset(world);
        health.Complete(world, inFlight, Failure("InsufficientSpace"));
        Require(health.Issue(world) is null, "an old transfer restored an alert after consent changed");

        Console.WriteLine("PASS shared-world transfer alert transitions");
    }

    private static ReceivedSharedWorldResult Failure(string code) =>
        new(false, code, "The transfer needs attention.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
