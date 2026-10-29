namespace TogetherServer;

internal enum PeerRequestFailureStage { ReadChallenge, PrepareChallenge, ReadExchange, ExecuteExchange }

// Local fixed diagnostic fields only. In particular, an unauthenticated
// request cannot put exception text, identities, keys or payloads in audit.
internal sealed class PeerRequestFailureDiagnostics(LocalData data, TimeProvider? clock = null)
{
    private const int MaximumKeys = 128;
    private readonly object sync = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<(PeerRequestFailureStage Stage, string Code, string Operation, string Phase), DateTimeOffset> recent = [];

    internal bool Record(PeerRequestFailureStage stage, Exception error, CancellationToken cancellationToken,
        WorldPeerTransferOperation operation = WorldPeerTransferOperation.None,
        WorldPeerTransferPhase phase = WorldPeerTransferPhase.Metadata)
    {
        if (!Enum.IsDefined(stage)) stage = PeerRequestFailureStage.ReadExchange;
        var failure = SharedSaveTransferFailure.Classify(error, cancellationToken, operation, phase);
        var now = time.GetUtcNow();
        var key = (stage, failure.Code, failure.Operation, failure.Phase);
        lock (sync)
        {
            if (recent.TryGetValue(key, out var previous) && now >= previous && now - previous < TimeSpan.FromSeconds(30))
                return false;
            if (recent.Count >= MaximumKeys && !recent.ContainsKey(key))
                recent.Remove(recent.MinBy(item => item.Value).Key);
            recent[key] = now;
        }
        return data.TryAudit($"save-request-rejected stage={stage} operation={failure.Operation} phase={failure.Phase} " +
            $"code={failure.Code} error={failure.Error} pending={failure.Pending} observed={now:O}");
    }
}
