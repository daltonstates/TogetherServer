namespace TogetherServer;

public sealed partial class HostManager
{
    internal Func<bool>? ApplicationStartAllowed { get; set; }

    private ActionResult? ApplicationStartIssue() => ApplicationStartAllowed?.Invoke() == false
        ? Result(false, "HostShuttingDown", "TogetherServer is closing or checking a Quit request. The server was not started.")
        : null;

    // Lifecycle admission must inspect current managed records after waiting
    // for in-flight work. Cached Offline status is only for observation.
    internal async Task<bool> HasManagedRunsAsync()
    {
        await gate.WaitAsync();
        try { return runs.Count != 0; }
        finally { gate.Release(); }
    }
}
