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
            failedNetwork.Progress(version, 0, 64);
            health.Complete(otherWorld, failedNetwork, Failure("NetworkUnavailable"));
        }
        Require(health.Issue(otherWorld)?.State == "Transfer unavailable",
            "repeated network failures were called a stall");

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
