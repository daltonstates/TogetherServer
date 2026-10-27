namespace TogetherServer;

public sealed partial class HostManager
{
    // Subscribers only wake/coalesce background work. A successful publication
    // does not wait for a recipient, repeat capture or turn Stop into a move.
    internal event Action? SharedSavePublished;
    private string sharedRunObservation = "";
    private readonly HostingWorkCounters sharedHostingWork = new();
    internal HostingWorkMeasurement WorkingVerificationMeasurement() => sharedHostingWork.Read(HostingWorkStage.WorkingVerification);

    private void AnnounceSharingPolicy(Guid profileId, bool enabled)
    {
        Activity("Backup", enabled ? "SharedSavesEnabled" : "SharedSavesDisabled",
            enabled ? "The owner enabled post-Stop save sharing." : "The owner disabled post-Stop save sharing.",
            ActivitySeverity.Important, profileId, visibility: ActivityVisibility.AssignedFriends);
        NotifySharedSavePublished();
    }

    private void NotifySharedRunChanges(HostSnapshot snapshot)
    {
        var shared = settings.Profiles.Where(profile => profile.SharedSavesEnabled || profile.SeparateCopySourceProfileId is not null)
            .Select(profile => profile.Id).ToHashSet();
        var material = string.Join(";", snapshot.Runs.Where(run => shared.Contains(run.ProfileId))
            .OrderBy(run => run.ProfileId).Select(run => $"{run.ProfileId:N}:{run.RunOperationId}:{run.State}"));
        if (material == sharedRunObservation) return;
        sharedRunObservation = material;
        NotifySharedSavePublished();
    }
    private void NotifySharedSavePublished()
    {
        try { SharedSavePublished?.Invoke(); }
        catch (Exception) { /* Delivery scheduling cannot undo a completed save. */ }
    }
}
