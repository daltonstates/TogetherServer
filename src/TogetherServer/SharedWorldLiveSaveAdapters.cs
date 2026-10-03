namespace TogetherServer;

// Candidate protocols only. A real in-game change must be saved while running,
// transferred, loaded on another PC, and survive another graceful restart before
// a game may publish a LiveSave version. No such game has passed that gate yet.
internal enum LiveSaveChannel { None, ExactManagedConsole, AuthenticatedLocalRcon }
internal enum LiveSaveEvidence { None, RunScopedCompletion, FrozenSnapshotQuery, ClosedSaveArchive }

internal sealed record LiveSaveStep(string Action, string? FixedCommand,
    LiveSaveChannel Channel, LiveSaveEvidence RequiredEvidence);

internal sealed record LiveSaveCandidate(string Game, IReadOnlyList<LiveSaveStep> Steps,
    string MissingProof)
{
    // Keep this separate from any command or log parser. Neither a response to
    // a command nor a file timestamp proves that another game process can load it.
    public bool LiveCaptureAccepted => false;
}

internal static class SharedWorldLiveSaveAdapters
{
    private static readonly IReadOnlyDictionary<string, LiveSaveCandidate> Candidates =
        new Dictionary<string, LiveSaveCandidate>(StringComparer.Ordinal)
        {
            [GameKinds.Valheim] = new(GameKinds.Valheim,
            [
                new("Wait for the game's configured autosave to finish", null,
                    LiveSaveChannel.None, LiveSaveEvidence.RunScopedCompletion)
            ], "A completed autosave must be tied to the exact managed run and proven loadable on another PC."),
            [GameKinds.MinecraftJava] = new(GameKinds.MinecraftJava,
            [
                new("Flush the world through the managed console", "save-all flush",
                    LiveSaveChannel.ExactManagedConsole, LiveSaveEvidence.RunScopedCompletion)
            ], "A console response alone does not prove the complete world is safe to copy or load."),
            [GameKinds.MinecraftBedrock] = new(GameKinds.MinecraftBedrock,
            [
                new("Hold world writes", "save hold", LiveSaveChannel.ExactManagedConsole,
                    LiveSaveEvidence.RunScopedCompletion),
                new("Query the held snapshot", "save query", LiveSaveChannel.ExactManagedConsole,
                    LiveSaveEvidence.FrozenSnapshotQuery),
                new("Resume world writes after copying or failure", "save resume",
                    LiveSaveChannel.ExactManagedConsole, LiveSaveEvidence.RunScopedCompletion)
            ], "The hold/query/resume snapshot needs an exact-run, failure-safe copy and a real load test."),
            [GameKinds.Factorio] = new(GameKinds.Factorio,
            [
                new("Request a server save", "/server-save", LiveSaveChannel.AuthenticatedLocalRcon,
                    LiveSaveEvidence.ClosedSaveArchive)
            ], "An RCON reply or ZIP timestamp does not prove the archive is closed and loadable."),
            [GameKinds.Terraria] = new(GameKinds.Terraria,
            [
                new("Save through the managed console", "save", LiveSaveChannel.ExactManagedConsole,
                    LiveSaveEvidence.RunScopedCompletion)
            ], "The console save needs a trusted completion signal and a real load test.")
        };

    public static LiveSaveCandidate? ForGame(string? game) =>
        game is not null && Candidates.TryGetValue(game, out var candidate) ? candidate : null;

    public static SharedWorldLiveSaveStatus Status(string? game)
    {
        var candidate = ForGame(game);
        return new(false, candidate is null
            ? "Live save sharing is unavailable for this server."
            : "Live save sharing is unavailable for this game. Use its verified post-Stop copy.");
    }
}

public sealed record SharedWorldLiveSaveStatus(bool Available, string Message);
