using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Only generated audit metadata: no listener, request transport or app is run.
internal static class PeerRequestFailureDiagnosticsChecks
{
    internal static Task RunAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "fixed-receive-request-diagnostics"));
        var clock = new ManualClock();
        var diagnostics = new PeerRequestFailureDiagnostics(data, clock);
        const string sensitive = "Bearer synthetic-secret at C:\\private-world\\payload.dat https://192.0.2.9:5131/?token=synthetic-secret";
        var error = new IOException(sensitive, unchecked((int)0x80070020));
        Require(diagnostics.Record(PeerRequestFailureStage.ExecuteExchange, error, default,
            WorldPeerTransferOperation.Seed, WorldPeerTransferPhase.Preparing), "The first fixed request failure was not retained.");
        Require(!diagnostics.Record(PeerRequestFailureStage.ExecuteExchange, error, default,
            WorldPeerTransferOperation.Seed, WorldPeerTransferPhase.Preparing), "An identical retry created another audit row immediately.");
        Require(diagnostics.Record(PeerRequestFailureStage.ExecuteExchange, error, default,
            WorldPeerTransferOperation.Seed, WorldPeerTransferPhase.VerifyingLocal), "A distinct failure phase was suppressed.");
        clock.Utc = clock.Utc.AddSeconds(31);
        Require(diagnostics.Record(PeerRequestFailureStage.ExecuteExchange, error, default,
            WorldPeerTransferOperation.Seed, WorldPeerTransferPhase.Preparing), "The bounded failure window never became observable again.");
        var lines = File.ReadAllLines(Path.Combine(data.RootPath, "audit.log"));
        Require(lines.Length == 3 && lines.All(line => line.Contains("operation=Seed", StringComparison.Ordinal) &&
            line.Contains("code=SaveTransferFileBusy", StringComparison.Ordinal) && line.Contains("pending=True", StringComparison.Ordinal)),
            "Fixed operation, result and pause metadata did not survive audit persistence.");
        Require(lines.All(line => !line.Contains("synthetic-secret", StringComparison.Ordinal) &&
            !line.Contains("private-world", StringComparison.Ordinal) && !line.Contains("192.0.2.9", StringComparison.Ordinal)),
            "Exception text or private transfer content entered request diagnostics.");
        Require(lines.All(line => SupportReportRedactor.Redact(line).Contains("SaveTransferFileBusy", StringComparison.Ordinal)),
            "Safe request failure metadata was hidden as player activity.");
        Require(diagnostics.Record((PeerRequestFailureStage)int.MaxValue, new InvalidDataException(sensitive), default,
            (WorldPeerTransferOperation)int.MaxValue, (WorldPeerTransferPhase)int.MaxValue), "Unknown enum context was not bounded safely.");
        var final = File.ReadLines(Path.Combine(data.RootPath, "audit.log")).Last();
        Require(final.Contains("stage=ReadExchange", StringComparison.Ordinal) && final.Contains("operation=None", StringComparison.Ordinal) &&
            final.Contains("phase=Queued", StringComparison.Ordinal) && !final.Contains("synthetic-secret", StringComparison.Ordinal),
            "Unrecognized request context escaped the fixed diagnostic vocabulary.");
        return Task.CompletedTask;
    }

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
