namespace TogetherServer;

public sealed partial class FriendService
{
    public Task<GameCompatibilityResult> ReadGameCompatibilityAsync(Guid profileId, CancellationToken ct = default) =>
        SelectedLink()?.ReadGameCompatibilityAsync(profileId, ct: ct) ?? Task.FromResult(
            new GameCompatibilityResult(false, "NotPaired", "Choose a saved Host connection."));

    public Task<GameCompatibilityResult> SetManualClientVersionAsync(Guid profileId, ManualClientVersionChange change,
        CancellationToken ct = default) => SelectedLink()?.ReadGameCompatibilityAsync(profileId, change, ct) ?? Task.FromResult(
            new GameCompatibilityResult(false, "NotPaired", "Choose a saved Host connection."));

    public Task<GameClientLaunchResult> OpenGameAsync(Guid profileId, CancellationToken ct = default) =>
        SelectedLink()?.OpenGameAsync(profileId, new WindowsSteamLaunchAdapter(), ct) ?? Task.FromResult(
            new GameClientLaunchResult(false, "NotPaired", "Choose a saved Host connection."));
}
