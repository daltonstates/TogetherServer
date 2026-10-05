using System.Globalization;
using System.Text;

namespace TogetherServer;

internal sealed class ValheimManagedSnapshotAdapter(LocalData data, GameServerRegistry games)
    : IManagedLiveSnapshotAdapter
{
    public string Game => GameKinds.Valheim;
    public bool LiveCaptureAccepted => SharedWorldLiveSaveAdapters.IsGameAccepted(Game);

    public async Task<ImmutableLiveSaveSnapshot> CaptureAsync(ServerProfile profile, ManagedRun run,
        CancellationToken cancellationToken, Guid? snapshotId = null)
    {
        if (profile.Kind != Game) throw new InvalidDataException("The save adapter belongs to another game.");
        var store = new ManagedLiveSnapshotStore(data, games);
        store.RequireExactRun(profile, run);
        var observer = new ValheimAutosaveObservationCandidate(data);
        using var cursor = observer.Begin(run); // Reuse the actual, strict owned-log observer.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            store.RequireExactRun(profile, run);
            if (observer.Observe(run, cursor))
                return store.Stage(profile, run, Completion(profile, run), cancellationToken: timeout.Token, reservedSnapshotId: snapshotId);
            await Task.Delay(100, timeout.Token);
        }
    }

    internal static LiveSaveCompletionEvidence Completion(ServerProfile profile, ManagedRun run) =>
        new(profile.Id, run.OperationId, run.ProcessId!.Value, run.StartTimeUtcTicks!.Value,
            LiveSaveEvidence.RunScopedCompletion, DateTimeOffset.UtcNow, true);
}

internal sealed class JavaManagedSnapshotAdapter(LocalData data, GameServerRegistry games)
    : IManagedLiveSnapshotAdapter
{
    public string Game => GameKinds.MinecraftJava;
    public bool LiveCaptureAccepted => SharedWorldLiveSaveAdapters.IsGameAccepted(Game);

    public async Task<ImmutableLiveSaveSnapshot> CaptureAsync(ServerProfile profile, ManagedRun run,
        CancellationToken cancellationToken, Guid? snapshotId = null)
    {
        if (profile.Kind != Game) throw new InvalidDataException("The save adapter belongs to another game.");
        using var cursor = ManagedSaveLogCursor.Open(data, games, profile, run);
        var requestedUtc = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        var dispatch = new ExactManagedConsoleLiveSaveCommandPort().RequestJavaFlush(run);
        if (dispatch.OperationId != run.OperationId || dispatch.FixedCommand != "save-all flush" || dispatch.CompletionConfirmed)
            throw new InvalidDataException("The fixed Java flush dispatch returned unexpected evidence.");
        return await ConsoleSnapshotCompletion.WaitAndStageAsync(data, games, profile, run,
            cursor, new(Game, requestedUtc), cancellationToken, snapshotId);
    }
}

internal sealed class TerrariaManagedSnapshotAdapter(LocalData data, GameServerRegistry games)
    : IManagedLiveSnapshotAdapter
{
    public string Game => GameKinds.Terraria;
    public bool LiveCaptureAccepted => SharedWorldLiveSaveAdapters.IsGameAccepted(Game);

    public async Task<ImmutableLiveSaveSnapshot> CaptureAsync(ServerProfile profile, ManagedRun run,
        CancellationToken cancellationToken, Guid? snapshotId = null)
    {
        if (profile.Kind != Game) throw new InvalidDataException("The save adapter belongs to another game.");
        using var cursor = ManagedSaveLogCursor.Open(data, games, profile, run);
        var requestedUtc = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        var dispatch = new ExactManagedTerrariaLiveSaveCommandPort(data).RequestTerrariaSave(run.OperationId);
        if (dispatch.OperationId != run.OperationId || dispatch.FixedCommand != "save" || dispatch.CompletionConfirmed)
            throw new InvalidDataException("The fixed Terraria save dispatch returned unexpected evidence.");
        return await ConsoleSnapshotCompletion.WaitAndStageAsync(data, games, profile, run,
            cursor, new(Game, requestedUtc), cancellationToken, snapshotId);
    }
}

internal static class ConsoleSnapshotCompletion
{
    internal static async Task<ImmutableLiveSaveSnapshot> WaitAndStageAsync(LocalData data,
        GameServerRegistry games, ServerProfile profile, ManagedRun run, ManagedSaveLogCursor cursor,
        ManagedConsoleSaveGrammar grammar, CancellationToken cancellationToken, Guid? snapshotId = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            foreach (var physicalLine in cursor.ReadNewLines(profile, run, timeout.Token))
                if (grammar.Observe(physicalLine, DateTimeOffset.UtcNow))
                    return new ManagedLiveSnapshotStore(data, games).Stage(profile, run,
                        ValheimManagedSnapshotAdapter.Completion(profile, run), cancellationToken: timeout.Token, reservedSnapshotId: snapshotId);
            await Task.Delay(100, timeout.Token);
        }
    }
}

// Version/locale candidates only. Unknown output has no completion authority.
// Fresh capture frames and this grammar do not make a mutable source consistent;
// Stage must subsequently hold every selected byte closed against writes/deletes.
internal sealed class ManagedConsoleSaveGrammar(string game, DateTimeOffset requestedUtc)
{
    private bool started;
    private bool finished;
    private DateTimeOffset? lastObservedUtc;

    internal bool Observe(string physicalLine, DateTimeOffset now)
    {
        if (finished) throw new InvalidDataException("A save completion observation cannot be reused.");
        if (!MinecraftCapturedLogFrame.TryDecode(Encoding.UTF8.GetBytes(physicalLine), out var captured) || captured is null)
            throw new InvalidDataException("An owned console save log contains an unknown capture frame.");
        if (captured.Stream == "Capture")
            throw new InvalidDataException("Console capture reported incomplete output during a save request.");
        if (captured.Stream != "Stdout" || captured.Truncated || captured.CapturedUtc < requestedUtc ||
            captured.CapturedUtc > now.AddSeconds(5) || lastObservedUtc is { } previous && captured.CapturedUtc < previous)
            return false;
        lastObservedUtc = captured.CapturedUtc;
        string? message = game == GameKinds.MinecraftJava ? JavaMessage(captured.Message) :
            game == GameKinds.Terraria ? captured.Message : null;
        var begin = game == GameKinds.MinecraftJava ? "Saving the game (this may take a moment!)" : "Saving world data: 100%";
        var complete = game == GameKinds.MinecraftJava ? "Saved the game" : "World saved.";
        if (message == begin)
        {
            if (started) throw new InvalidDataException("Overlapping save output cannot identify one completed request.");
            started = true;
            return false;
        }
        if (!started || message != complete) return false;
        finished = true;
        return true;
    }

    private static string? JavaMessage(string text)
    {
        const string prefixEnd = "] [Server thread/INFO]: ";
        if (text.Length < 10 + prefixEnd.Length || text[0] != '[' ||
            !TimeOnly.TryParseExact(text.AsSpan(1, 8), "HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _) || !text.AsSpan(9).StartsWith(prefixEnd, StringComparison.Ordinal)) return null;
        return text[(9 + prefixEnd.Length)..];
    }
}
