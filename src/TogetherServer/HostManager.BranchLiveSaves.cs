namespace TogetherServer;

public sealed partial class HostManager
{
    // These hooks never acquire the lifecycle gate: acceptance/capture callers
    // already own it, including while a fixed save adapter is completing.
    internal LiveSaveSourceContext ResolveBranchLiveSourceUnderGate(ServerProfile profile,
        bool requireStarted = false) => SharedWorldBranchLiveSource.Resolve(data, profile, clock.GetUtcNow(), requireStarted);

    internal SharedWorldResult PublishBranchLiveSnapshotUnderGate(ServerProfile profile, ManagedRun run,
        ImmutableLiveSaveSnapshot snapshot, Action requireAcceptedScope, CancellationToken cancellationToken = default)
    {
        var source = ResolveBranchLiveSourceUnderGate(profile, requireStarted: true);
        return new SharedWorldSeparateCopyStore(data).PublishLiveCapture(source.Branch!, profile, run,
            snapshot, new ManagedLiveSnapshotStore(data, games), requireAcceptedScope, cancellationToken);
    }
}
