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
    public bool LiveCaptureAccepted => SharedWorldLiveSaveAdapters.IsGameAccepted(Game);
}

internal static class SharedWorldLiveSaveAdapters
{
    // Add an individual game only after its reviewed, version-specific external
    // acceptance record exists. There is no flag that enables all candidates.
    private static readonly IReadOnlySet<string> AcceptedGames = new HashSet<string>(StringComparer.Ordinal);
    internal static bool IsGameAccepted(string game) => AcceptedGames.Contains(game);

    internal static IManagedLiveSnapshotAdapter? CreateSnapshotAdapter(LocalData data,
        GameServerRegistry games, string game) => game switch
        {
            GameKinds.Valheim => new ValheimManagedSnapshotAdapter(data, games),
            GameKinds.MinecraftJava => new JavaManagedSnapshotAdapter(data, games),
            GameKinds.MinecraftBedrock => new BedrockManagedSnapshotAdapter(data, games),
            GameKinds.Factorio => new FactorioManagedSnapshotAdapter(data, games),
            GameKinds.Terraria => new TerrariaManagedSnapshotAdapter(data, games),
            _ => null
        };

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
        var reason = candidate?.MissingProof ?? "This game has no accepted live-save adapter.";
        return new(false, reason + " Use a verified post-Stop copy.", game ?? "Unknown", "GameAcceptanceRequired",
            AcceptanceStages(game));
    }

    internal static IReadOnlyList<LiveSaveAcceptanceStage> AcceptanceStages(string? game) =>
    [
        new("completion", "Unverified", ForGame(game)?.MissingProof ?? (game == GameKinds.Fixture ? "Synthetic fixture completion only." : "No reviewed completion signal exists for this game.")),
        new("snapshot", "Unverified", "A game-specific immutable byte binding is required; a stable timestamp or hash is insufficient."),
        new("transfer", "Unverified", "Transfer the exact live copy with a verified receipt to another owner-controlled PC."),
        new("load", "Unverified", "Load the exact copy in the owner-installed game on that PC."),
        new("change", "Unverified", "Confirm the recognizable change made before the running save."),
        new("restart", "Unverified", "Gracefully save, stop and restart the disposable game; confirm the change remains.")
    ];
}

public sealed record LiveSaveAcceptanceStage(string Id, string State, string Detail);
public sealed record SharedWorldLiveSaveStatus(bool Available, string Message, string? Game = null,
    string? Code = null, IReadOnlyList<LiveSaveAcceptanceStage>? Stages = null, bool ResumePending = false,
    LiveSaveAttemptView? LastAttempt = null);
