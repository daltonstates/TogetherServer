namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    // The local setup picker follows the trusted source world, independently
    // of a successor Host's local profile ID. It cannot start or import a game.
    internal async Task<bool> MayBrowseRecoverySourceAsync(Guid sourceProfileId, string game,
        CancellationToken cancellationToken)
    {
        foreach (var group in Groups().Where(group => group.Root.ProfileId == sourceProfileId &&
                     group.DeviceId != Guid.Empty))
        {
            if (!await RecoveryAllowedAsync(group, cancellationToken)) continue;
            if ((await LocalSavesAsync(group, cancellationToken)).Any(save =>
                    save.Descriptor.Version.Game == game && !save.Descriptor.TestOnly &&
                    save.Descriptor.TestArtifact is null && AuthorizedDescriptor(group, save.Descriptor))) return true;
        }
        return false;
    }
}
