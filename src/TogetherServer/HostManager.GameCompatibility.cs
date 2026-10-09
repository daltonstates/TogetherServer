using System.Text.Json;

namespace TogetherServer;

public sealed partial class HostManager
{
    public async Task<GameRequirementsResult> GameRequirementsAsync(Guid profileId, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GameCompatibility.ReadTimeout);
        try
        {
            ServerProfile profile;
            await gate.WaitAsync(timeout.Token);
            try
            {
                var saved = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
                if (saved is null) return GameCompatibility.Failure("UnknownProfile", "Choose a saved server.");
                profile = JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(saved))!;
            }
            finally { gate.Release(); }
            var result = await Task.Run(() => GameCompatibility.Observe(data, profile, timeout.Token), timeout.Token).WaitAsync(timeout.Token);
            await gate.WaitAsync(timeout.Token);
            try
            {
                var current = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
                return current is null || GameCompatibility.Fingerprint(current) != GameCompatibility.Fingerprint(profile)
                    ? GameCompatibility.Failure("ProfileChanged", "The saved server changed. Refresh its requirements.") : result;
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return GameCompatibility.Failure("RequirementsTimedOut", "Requirements could not be read within the time limit. Refresh or state the required version."); }
        catch (Exception ex) when (GameCompatibility.ReadFailure(ex) || ex is System.Security.Cryptography.CryptographicException)
        { return GameCompatibility.Failure("RequirementsUnavailable", "Requirements could not be inspected safely."); }
    }

    public async Task<GameRequirementsResult> SetGameRequirementAsync(Guid profileId, GameRequirementChange change,
        CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return GameCompatibility.Failure("UnknownProfile", "Choose a saved server.");
            if (!GameCompatibility.Supported(profile.Kind)) return GameCompatibility.Failure("UnsupportedGame", "Choose a built-in game.");
            if (change.Version is not null && !GameCompatibility.ValidVersion(change.Version))
                return GameCompatibility.Failure("InvalidVersion", "Enter a game version of at most 48 letters, digits, dots, plus signs or dashes, starting with a digit.");
            GameCompatibility.SaveOwnerVersion(data, profile, change.Version);
        }
        catch (Exception ex) when (GameCompatibility.ReadFailure(ex) || ex is System.Security.Cryptography.CryptographicException)
        { return GameCompatibility.Failure("RequirementSaveFailed", "The required game version could not be saved."); }
        finally { gate.Release(); }
        return await GameRequirementsAsync(profileId, ct);
    }
}
