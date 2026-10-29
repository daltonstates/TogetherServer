namespace TogetherServer;

public sealed record WorldPeerTransferProgress(string State, string Phase, string Operation, string HttpStep,
    int? HttpStatus, string Result, string Error, DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc,
    long ElapsedMilliseconds, long SubmittedBytes, long ReceivedBytes, int CompletedFiles,
    int? RequestedFileIndex, long? RequestedOffset, bool ExactReceiptAccepted,
    long? TotalBytes = null, int? TotalFiles = null, long? PublicationNumber = null, long? ReceiptPublicationNumber = null,
    long LocalStagedBytes = 0)
{
    // Scheduler-only exact staging scope. Kept out of API/support projection.
    internal string? StagingVersionHash { get; init; }
}
public sealed record WorldPeerTransferView(int RouteOrdinal, string RecipientRole,
    WorldPeerTransferProgress? Current, WorldPeerTransferProgress? LastResult, WorldPeerTransferProgress? LastFailure);

internal enum WorldPeerTransferPhase { Queued, Metadata, Preparing, Pulling, Offering, Sending, VerifyingLocal, VerifyingRemote, Receipt }
internal enum WorldPeerTransferOperation { None, Metadata, Catalog, Manifest, Chunk, Seed, Receipt }
internal enum WorldPeerTransferHttpStep { None, Challenge, Exchange }
internal enum WorldPeerTransferRecipientRole { Unknown, OriginalHost, ApprovedPeer }
internal enum WorldPeerTransferResult
{
    None, NoPayload, PassComplete, SeedPaused, SeedHistoryPending, ManifestUnavailable, ChunkUnavailable,
    WorkBudget, ByteBudget, CompleteReceiptAccepted, KnownReceiptAccepted, KnownWithoutReceipt, Canceled, Interrupted, Failed
}
internal enum WorldPeerTransferError
{
    None, Timeout, Unreachable, HttpRejected, Unauthorized, PeerRejected, InvalidResponse, InvalidHistory,
    SourceUnavailable, VerificationFailed, Capacity, Canceled, Unexpected
}

// Display-only process memory. A route key can contain an endpoint/pin, but no
// key, identity, path, exception text or payload is returned in these snapshots.
// Nothing here schedules work, acknowledges bytes or grants hosting permission.
internal sealed class WorldPeerTransferObservationStore(TimeProvider? clock = null)
{
    internal const int MaximumRoutes = 256;
    internal const long MaximumBytes = 1L << 50;
    internal const int MaximumFiles = 100_000;
    internal const long MaximumPublicationNumber = 9_007_199_254_740_991;
    internal const long MaximumElapsedMilliseconds = 86_400_000;
    private readonly object sync = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<(string World, string Route), Entry> entries = [];
    private long sequence;

    private sealed class Entry(int ordinal)
    {
        internal int Ordinal { get; } = ordinal;
        internal long Touched;
        internal WorldPeerTransferRecipientRole Role;
        internal MutableProgress? Active;
        internal WorldPeerTransferProgress? LastResult;
        internal WorldPeerTransferProgress? LastFailure;
    }

    internal sealed class MutableProgress((string World, string Route) key, DateTimeOffset utc, long timestamp)
    {
        internal readonly (string World, string Route) Key = key;
        internal readonly DateTimeOffset StartedUtc = utc;
        internal readonly long Timestamp = timestamp;
        internal DateTimeOffset UpdatedUtc = utc;
        internal string State = "Working";
        internal WorldPeerTransferPhase Phase;
        internal WorldPeerTransferOperation Operation;
        internal WorldPeerTransferHttpStep HttpStep;
        internal int? HttpStatus;
        internal WorldPeerTransferResult Result;
        internal WorldPeerTransferError Error;
        internal long SubmittedBytes, ReceivedBytes, LocalStagedBytes;
        internal string? StagingVersionHash;
        internal int CompletedFiles;
        internal int? RequestedFileIndex;
        internal long? RequestedOffset, TotalBytes, PublicationNumber, ReceiptPublicationNumber;
        internal int? TotalFiles;
        internal long? FrozenElapsed;
    }

    internal Attempt Begin(string worldKey, string routeKey)
    {
        // Capacity or an invalid observation key never blocks the real work.
        if (string.IsNullOrEmpty(worldKey) || worldKey.Length > 128 || string.IsNullOrEmpty(routeKey) || routeKey.Length > 1024)
            return new(this, null);
        lock (sync)
        {
            var key = (worldKey, routeKey);
            if (!entries.TryGetValue(key, out var entry))
            {
                if (entries.Count == MaximumRoutes)
                {
                    var oldest = entries.Where(item => item.Value.Active is null).OrderBy(item => item.Value.Touched).FirstOrDefault();
                    if (oldest.Value is null) return new(this, null);
                    entries.Remove(oldest.Key);
                }
                var used = entries.Values.Select(item => item.Ordinal).ToHashSet();
                entry = new(Enumerable.Range(1, MaximumRoutes).First(value => !used.Contains(value)));
                entries.Add(key, entry);
            }
            if (entry.Active is { } prior) Finish(prior, WorldPeerTransferResult.Interrupted, WorldPeerTransferError.Canceled);
            var current = new MutableProgress(key, time.GetUtcNow().ToUniversalTime(), time.GetTimestamp());
            entry.Active = current;
            entry.Touched = ++sequence;
            return new(this, current);
        }
    }

    internal WorldPeerTransferView? Read(string worldKey, string routeKey)
    {
        lock (sync) return entries.TryGetValue((worldKey, routeKey), out var entry) ? View(entry) : null;
    }

    internal IReadOnlyList<WorldPeerTransferView> ReadWorld(string worldKey)
    {
        lock (sync) return entries.Where(item => item.Key.World == worldKey).OrderBy(item => item.Value.Ordinal)
            .Select(item => View(item.Value)).ToArray();
    }

    private WorldPeerTransferView View(Entry entry) => new(entry.Ordinal, entry.Role.ToString(),
        entry.Active is { } current ? Snapshot(current) : null, entry.LastResult, entry.LastFailure);

    private long Elapsed(MutableProgress progress) => Math.Clamp(
        time.GetElapsedTime(progress.Timestamp, time.GetTimestamp()).Ticks / TimeSpan.TicksPerMillisecond,
        0, MaximumElapsedMilliseconds);

    private WorldPeerTransferProgress Snapshot(MutableProgress item) => new(item.State, item.Phase.ToString(),
        item.Operation.ToString(), item.HttpStep.ToString(), item.HttpStatus, item.Result.ToString(), item.Error.ToString(),
        item.StartedUtc, item.UpdatedUtc, item.FrozenElapsed ?? Elapsed(item), item.SubmittedBytes, item.ReceivedBytes,
        item.CompletedFiles, item.RequestedFileIndex, item.RequestedOffset, item.ReceiptPublicationNumber is not null,
        item.TotalBytes, item.TotalFiles, item.PublicationNumber, item.ReceiptPublicationNumber, item.LocalStagedBytes)
    { StagingVersionHash = item.StagingVersionHash };

    private void Change(MutableProgress? current, Func<MutableProgress, bool> change)
    {
        if (current is null) return;
        lock (sync)
        {
            if (!entries.TryGetValue(current.Key, out var entry) || !ReferenceEquals(entry.Active, current) || !change(current)) return;
            current.UpdatedUtc = time.GetUtcNow().ToUniversalTime();
            entry.Touched = ++sequence;
        }
    }

    private void Finish(MutableProgress? current, WorldPeerTransferResult result, WorldPeerTransferError error)
    {
        if (current is null) return;
        lock (sync)
        {
            if (!entries.TryGetValue(current.Key, out var entry) || !ReferenceEquals(entry.Active, current)) return;
            current.Result = Enum.IsDefined(result) ? result : WorldPeerTransferResult.Failed;
            current.Error = Enum.IsDefined(error) ? error : WorldPeerTransferError.Unexpected;
            current.State = current.Error != WorldPeerTransferError.None || current.Result is WorldPeerTransferResult.Failed or
                WorldPeerTransferResult.ManifestUnavailable or WorldPeerTransferResult.ChunkUnavailable ? "Failed" :
                current.Result is WorldPeerTransferResult.Canceled or WorldPeerTransferResult.Interrupted ? "Canceled" :
                (current.Result is WorldPeerTransferResult.CompleteReceiptAccepted or WorldPeerTransferResult.KnownReceiptAccepted) &&
                    current.ReceiptPublicationNumber is not null ? "Succeeded" : "Pending";
            if (current.Error == WorldPeerTransferError.Canceled) current.State = "Canceled";
            current.UpdatedUtc = time.GetUtcNow().ToUniversalTime();
            current.FrozenElapsed = Elapsed(current);
            entry.LastResult = Snapshot(current);
            if (current.State is "Failed" or "Canceled") entry.LastFailure = entry.LastResult;
            entry.Active = null;
            entry.Touched = ++sequence;
        }
    }

    private static long Added(long current, long delta) => delta <= 0 ? current : current + Math.Min(delta, MaximumBytes - current);
    private static long? Publication(long? value) => value is > 0 and <= MaximumPublicationNumber ? value : null;

    internal sealed class Attempt(WorldPeerTransferObservationStore store, MutableProgress? current) : IDisposable
    {
        // Scheduling consumes only this exact finished attempt, never a prior
        // route result while its current owner is still running.
        internal WorldPeerTransferProgress? FinishedResult
        {
            get { lock (store.sync) return current is { FrozenElapsed: not null } finished ? store.Snapshot(finished) : null; }
        }
        internal void Phase(WorldPeerTransferPhase phase) => store.Change(current, item =>
        {
            if (!Enum.IsDefined(phase) || item.Phase == phase) return false;
            item.Phase = phase;
            return true;
        });
        internal void Request(WorldPeerTransferOperation operation, WorldPeerTransferHttpStep step) => store.Change(current, item =>
        {
            item.Operation = Enum.IsDefined(operation) ? operation : WorldPeerTransferOperation.None;
            item.HttpStep = Enum.IsDefined(step) ? step : WorldPeerTransferHttpStep.None;
            item.HttpStatus = null;
            return true;
        });
        internal void Response(int? httpStatus) => store.Change(current, item =>
        {
            var status = httpStatus is >= 100 and <= 599 ? httpStatus : null;
            if (item.HttpStatus == status) return false;
            item.HttpStatus = status;
            return true;
        });
        internal void Expected(long? totalBytes, int? totalFiles, long? publicationNumber = null,
            string? stagingVersionHash = null) => store.Change(current, item =>
        {
            if (item.StagingVersionHash != stagingVersionHash) item.LocalStagedBytes = 0;
            item.StagingVersionHash = stagingVersionHash;
            item.TotalBytes = totalBytes is >= 0 ? Math.Min(totalBytes.Value, MaximumBytes) : null;
            item.TotalFiles = totalFiles is >= 0 ? Math.Min(totalFiles.Value, MaximumFiles) : null;
            item.PublicationNumber = Publication(publicationNumber);
            return true;
        });
        internal void Requested(int? fileIndex, long? offset) => store.Change(current, item =>
        {
            item.RequestedFileIndex = fileIndex is >= 0 and <= MaximumFiles ? fileIndex : null;
            item.RequestedOffset = offset is >= 0 ? Math.Min(offset.Value, MaximumBytes) : null;
            return true;
        });
        // Submitted means handed to this pass's transport, not delivered or verified.
        internal void Submitted(long bytes) => store.Change(current, item =>
        {
            var next = Added(item.SubmittedBytes, bytes);
            if (next == item.SubmittedBytes) return false;
            item.SubmittedBytes = next;
            return true;
        });
        internal void Received(long bytes) => store.Change(current, item =>
        {
            var next = Added(item.ReceivedBytes, bytes);
            if (next == item.ReceivedBytes) return false;
            item.ReceivedBytes = next;
            return true;
        });
        // Local bytes flushed into resumable staging after a current-scope
        // check. Staging still needs full verification; it is not wire traffic.
        internal void LocalStaged(long bytes) => store.Change(current, item =>
        {
            var next = Added(item.LocalStagedBytes, bytes);
            if (next == item.LocalStagedBytes) return false;
            item.LocalStagedBytes = next;
            return true;
        });
        internal void FileCompleted(int count = 1) => store.Change(current, item =>
        {
            if (count <= 0 || item.CompletedFiles == MaximumFiles) return false;
            item.CompletedFiles += Math.Min(count, MaximumFiles - item.CompletedFiles);
            return true;
        });
        internal void AuthenticatedRole(WorldPeerTransferRecipientRole role) => store.Change(current, item =>
        {
            if (!Enum.IsDefined(role) || role == WorldPeerTransferRecipientRole.Unknown) return false;
            var entry = store.entries[item.Key];
            if (entry.Role == role) return false;
            entry.Role = role;
            return true;
        });
        // Caller invokes only after the existing exact receipt validation succeeds.
        // The historical number is sticky; it never certifies the newest/current save.
        internal void ReceiptAccepted(long publicationNumber) => store.Change(current, item =>
        {
            var number = Publication(publicationNumber);
            if (number is null || item.ReceiptPublicationNumber is { } previous && previous >= number.Value) return false;
            item.ReceiptPublicationNumber = number;
            return true;
        });
        internal void Complete(WorldPeerTransferResult result) => store.Finish(current, result, WorldPeerTransferError.None);
        internal void Fail(WorldPeerTransferError error, WorldPeerTransferResult result = WorldPeerTransferResult.Failed) =>
            store.Finish(current, result, error == WorldPeerTransferError.None ? WorldPeerTransferError.Unexpected : error);
        public void Dispose() => store.Finish(current, WorldPeerTransferResult.Interrupted, WorldPeerTransferError.Canceled);
    }
}
