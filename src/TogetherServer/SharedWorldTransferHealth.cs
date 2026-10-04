namespace TogetherServer;

// Friend-side transfer observations are local UI state. They never authorize a save or a lifecycle action.
internal sealed class SharedWorldTransferHealth
{
    private sealed class WorldState
    {
        internal int Epoch;
        internal string? VersionHash;
        internal long LastBytes;
        internal int NoProgressFailures;
        internal TransferIssue? Issue;
    }

    internal sealed class Attempt(int epoch)
    {
        internal int Epoch { get; } = epoch;
        internal string? VersionHash { get; private set; }
        internal long StartBytes { get; private set; }
        internal long ReceivedBytes { get; private set; }
        internal long TotalBytes { get; private set; }
        internal bool ReachedPayload => VersionHash is not null;
        internal bool Advanced => ReceivedBytes > StartBytes;

        internal void Progress(string versionHash, long receivedBytes, long totalBytes)
        {
            if (VersionHash != versionHash)
            {
                VersionHash = versionHash;
                StartBytes = receivedBytes;
                ReceivedBytes = receivedBytes;
            }
            else ReceivedBytes = Math.Max(ReceivedBytes, receivedBytes);
            TotalBytes = totalBytes;
        }
    }

    internal sealed record TransferIssue(string State, string Message, long ReceivedBytes, long TotalBytes);

    private readonly object sync = new();
    private readonly Dictionary<Guid, WorldState> worlds = [];

    private WorldState State(Guid profileId)
    {
        if (!worlds.TryGetValue(profileId, out var state)) worlds[profileId] = state = new();
        return state;
    }

    internal Attempt Begin(Guid profileId)
    {
        lock (sync) return new(State(profileId).Epoch);
    }

    internal void Reset(Guid profileId)
    {
        lock (sync)
        {
            var state = State(profileId);
            state.Epoch++;
            state.VersionHash = null;
            state.LastBytes = 0;
            state.NoProgressFailures = 0;
            state.Issue = null;
        }
    }

    internal TransferIssue? Issue(Guid profileId)
    {
        lock (sync) return worlds.TryGetValue(profileId, out var state) ? state.Issue : null;
    }

    internal void Complete(Guid profileId, Attempt attempt, ReceivedSharedWorldResult result)
    {
        lock (sync)
        {
            var state = State(profileId);
            if (state.Epoch != attempt.Epoch) return; // Consent changed during this attempt.
            if (result.Ok)
            {
                state.Issue = null;
                state.NoProgressFailures = 0;
                state.VersionHash = null;
                state.LastBytes = 0;
                return;
            }

            if (result.Code == "InsufficientSpace")
            {
                state.NoProgressFailures = 0;
                state.Issue = new("Low space", result.Message, attempt.ReceivedBytes, attempt.TotalBytes);
                return;
            }

            if (attempt.ReachedPayload && result.Code == "TransferInterrupted")
            {
                state.NoProgressFailures = attempt.Advanced ? 0 :
                    state.VersionHash == attempt.VersionHash && state.LastBytes == attempt.ReceivedBytes
                        ? state.NoProgressFailures + 1 : 1;
                state.VersionHash = attempt.VersionHash;
                state.LastBytes = attempt.ReceivedBytes;
                state.Issue = new(state.NoProgressFailures >= 2 ? "Stalled" : "Transfer interrupted",
                    state.NoProgressFailures >= 2
                        ? "Receiving made no progress across repeated attempts. Check the Host connection and retry. " + result.Message
                        : result.Message, attempt.ReceivedBytes, attempt.TotalBytes);
                return;
            }

            // A failed secure check, access decision, or network request before payload receipt
            // does not prove the file transfer stalled.
            state.NoProgressFailures = 0;
            state.VersionHash = null;
            state.LastBytes = 0;
            state.Issue = new("Transfer unavailable", result.Message, attempt.ReceivedBytes, attempt.TotalBytes);
        }
    }
}
