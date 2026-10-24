using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class LocalHostingProgressChecks
{
    internal static Task RunAsync()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var store = new LocalHostingProgressStore();
        var connection = new object();
        var otherConnection = new object();
        var profile = Guid.NewGuid();
        Require(store.Read(connection, profile) is null, "Reading idle progress created an operation.");
        using var first = store.Begin(connection, profile);
        Require(store.Read(otherConnection, profile) is null && store.Read(connection, Guid.NewGuid()) is null,
            "A different connection or world could read another operation's progress.");
        first.Transfer(250, 1000);
        Require(store.Read(connection, profile) is { State: "Working", Phase: "Receiving", Percent: 25 },
            "Real received bytes did not produce transfer progress.");
        first.Transfer(100, 1000);
        first.Transfer(2000, 1000);
        first.Transfer(-1, 1000);
        Require(store.Read(connection, profile) is { ReceivedBytes: 250, Percent: 25 },
            "Regressing or impossible byte updates fabricated progress.");
        first.Transfer(1000, 1000);
        Require(store.Read(connection, profile) is { State: "Working", Percent: 100 },
            "Finishing transfer incorrectly completed hosting preparation.");
        first.Report("Verifying", "Checking file hashes.");
        Require(store.Read(connection, profile) is { State: "Working", Percent: null, TotalBytes: 0 },
            "An unmeasured verification phase inherited a transfer percentage.");
        using var second = store.Begin(otherConnection, profile);
        first.Transfer(1000, 1000);
        first.Complete(true, "Old result");
        Require(store.Read(connection, profile) is null &&
            store.Read(otherConnection, profile) is { State: "Working", Phase: "Permission", Percent: null },
            "A late old-connection result replaced a new operation's progress.");
        second.Transfer(long.MaxValue / 2, long.MaxValue);
        Require(store.Read(otherConnection, profile)?.Percent == 49,
            "Large valid byte totals overflowed transfer progress.");
        second.Dispose();
        second.Transfer(1, 1);
        second.Complete(true, "Late result");
        Require(store.Read(otherConnection, profile) is { State: "Failed", Percent: null },
            "Cancellation or a late callback claimed hosting completion.");
        using var completed = store.Begin(connection, profile);
        completed.Complete(true, "Prepared");
        completed.Dispose();
        Require(store.Read(connection, profile) is { State: "Completed", Percent: null },
            "Disposal rewrote a completed result or fabricated an overall percentage.");
        return Task.CompletedTask;
    }
}
