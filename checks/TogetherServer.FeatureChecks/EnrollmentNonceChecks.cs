using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure nonce-store checks with a fake clock. No app, HTTP, process, listener,
// protected state, fixture executable, browser or native dialog is invoked.
internal static class EnrollmentNonceChecks
{
    internal static async Task RunAsync()
    {
        CheckSupersededNonce();
        CheckWrongNonce();
        CheckScopeBinding();
        CheckExpiryAndReplay();
        await CheckConcurrentConsumptionAsync();
    }

    private static void CheckSupersededNonce()
    {
        var challenges = new SharedWorldEnrollmentNonces(new NonceClock());
        var device = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var old = challenges.Issue(device, profile);
        var latest = challenges.Issue(device, profile);
        Require(old != latest, "a new enrollment challenge reused the superseded nonce");
        Require(!challenges.Consume(device, profile, old), "a superseded enrollment challenge was accepted");
        Require(challenges.Consume(device, profile, latest), "the stale request consumed the latest challenge");
        Require(!challenges.Consume(device, profile, latest) && !challenges.Consume(device, profile, old),
            "a consumed or superseded enrollment challenge was replayed");
    }

    private static void CheckWrongNonce()
    {
        var challenges = new SharedWorldEnrollmentNonces(new NonceClock());
        var device = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var nonce = challenges.Issue(device, profile);
        var wrong = (nonce[0] == 'A' ? "B" : "A") + nonce[1..];
        Require(!challenges.Consume(device, profile, wrong), "a wrong same-scope enrollment nonce was accepted");
        Require(challenges.Consume(device, profile, nonce), "a wrong same-scope nonce consumed the valid challenge");
        Require(!challenges.Consume(device, profile, nonce), "the valid challenge was accepted twice");
    }

    private static void CheckScopeBinding()
    {
        var challenges = new SharedWorldEnrollmentNonces(new NonceClock());
        var device = Guid.NewGuid();
        var otherDevice = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var otherProfile = Guid.NewGuid();
        var nonce = challenges.Issue(device, profile);
        var deviceNonce = challenges.Issue(otherDevice, profile);
        var profileNonce = challenges.Issue(device, otherProfile);
        Require(!challenges.Consume(otherDevice, profile, nonce) &&
            !challenges.Consume(device, otherProfile, nonce), "an enrollment nonce escaped its device/profile scope");
        Require(challenges.Consume(otherDevice, profile, deviceNonce) &&
            challenges.Consume(device, otherProfile, profileNonce) &&
            challenges.Consume(device, profile, nonce), "a wrong-scope nonce consumed another scope's challenge");
    }

    private static void CheckExpiryAndReplay()
    {
        var clock = new NonceClock();
        var challenges = new SharedWorldEnrollmentNonces(clock);
        var device = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var boundary = challenges.Issue(device, profile);
        clock.Advance(TimeSpan.FromMinutes(5));
        Require(challenges.Consume(device, profile, boundary), "the existing inclusive five-minute expiry boundary changed");
        Require(!challenges.Consume(device, profile, boundary), "an expiry-boundary challenge was replayed");
        var expired = challenges.Issue(device, profile);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));
        Require(!challenges.Consume(device, profile, expired) && !challenges.Consume(device, profile, expired),
            "an expired enrollment challenge was accepted or replayed");
        var fresh = challenges.Issue(device, profile);
        Require(challenges.Consume(device, profile, fresh), "an expired challenge prevented a fresh enrollment");
    }

    private static async Task CheckConcurrentConsumptionAsync()
    {
        var challenges = new SharedWorldEnrollmentNonces(new NonceClock());
        var device = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var nonce = challenges.Issue(device, profile);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 32).Select(async _ =>
        {
            await release.Task;
            return challenges.Consume(device, profile, nonce);
        }).ToArray();
        Require(callers.All(task => !task.IsCompleted), "concurrent consumers were not armed behind the same release");
        release.SetResult();
        var results = await Task.WhenAll(callers);
        Require(results.Count(accepted => accepted) == 1, "concurrent matching consumers did not accept exactly once");
        Require(!challenges.Consume(device, profile, nonce), "a concurrently consumed challenge was replayed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class NonceClock : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => utcNow;
        internal void Advance(TimeSpan duration) => utcNow += duration;
    }
}
