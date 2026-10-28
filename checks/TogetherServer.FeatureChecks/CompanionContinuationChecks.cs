using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Uses signed branches from the disposable owner-recovery fixture. Snapshots
// are canonical local evidence; no network announcement or process is used.
internal static class CompanionContinuationChecks
{
    internal static void Run(LocalData data, SharedWorldRoster roster, WorldSeparateCopyBranch first, WorldSeparateCopyBranch second)
    {
        var original = new ServerProfile { Id = roster.ProfileId, Kind = GameKinds.Fixture, SharedSavesEnabled = true, Name = "Same name" };
        ServerProfile Copy(WorldSeparateCopyBranch branch) => new()
        {
            Id = SharedWorldBranchLiveSource.LocalProfileId(branch.BranchHash),
            Kind = original.Kind,
            Name = original.Name,
            SeparateCopySourceProfileId = original.Id,
            SeparateCopyBranchHash = branch.BranchHash
        };
        var continuation = Copy(first);
        var other = Copy(second);
        var operation = Guid.NewGuid();
        RunView Run(ServerProfile profile, Guid? id) => new(profile.Id, id is null ? "Offline" : "Ready", "Synthetic",
            null, OnlinePlayers: id is null ? null : 0, PlayerCountTrusted: id is not null, RunOperationId: id);
        HostSnapshot Snapshot(params RunView[] runs) => new(new() { Profiles = [original, continuation, other] }, runs,
            "Synthetic", "Host", new Dictionary<Guid, bool>(), "synthetic");
        var snapshot = Snapshot(Run(original, null), Run(continuation, operation), Run(other, null));
        Require(CompanionContinuation.Resolve(snapshot, original, roster, [first, second]) == continuation.Id,
            "The original assigned server did not resolve its one signed local running continuation.");
        var driver = new CountOnlyDriver();
        var managed = new ManagedRun { ProfileId = continuation.Id, OperationId = operation, Kind = continuation.Kind };
        using (var permit = RemoteStopSafety.TryAcquire(snapshot, continuation.Id, data,
            new GameServerRegistry([driver], PortProbeMode.ObserveOnly), [managed]))
        {
            Require(permit.Allowed && permit.StillSafe(managed) && driver.Reads == 2,
                "The resolved continuation could not use its exact run and two fresh zero-count Stop checks.");
            driver.Players = 1;
            Require(!permit.StillSafe(managed), "A resolved continuation permitted Stop with a player online.");
            driver.Players = null;
            Require(!permit.StillSafe(managed), "A resolved continuation treated an unknown count as empty.");
            Require(!permit.StillSafe(new ManagedRun { OperationId = Guid.NewGuid() }),
                "An original server's Stop permit followed a different managed operation.");
        }
        Require(CompanionContinuation.Resolve(Snapshot(Run(original, null), Run(continuation, null), Run(other, null)),
            original, roster, [first, second]) == original.Id, "A stored or announced branch fabricated a running local target.");
        Require(CompanionContinuation.Resolve(Snapshot(Run(original, operation), Run(continuation, Guid.NewGuid())),
            original, roster, [first, second]) == original.Id, "An active original was displaced by a continuation.");
        Require(CompanionContinuation.Resolve(Snapshot(Run(original, null), Run(continuation, operation), Run(other, Guid.NewGuid())),
            original, roster, [first, second]) == original.Id, "Ambiguous local writers selected a control target.");
        Require(CompanionContinuation.Resolve(snapshot, original, roster, [first with { Signature = "invalid" }]) == original.Id,
            "An invalid signed branch redirected controls.");
        original.SharedSavesEnabled = false;
        Require(CompanionContinuation.Resolve(snapshot, original, roster, [first, second]) == original.Id,
            "Disabled sharing redirected an assigned server.");
        original.SharedSavesEnabled = true;
        continuation.SeparateCopySourceProfileId = Guid.NewGuid();
        Require(CompanionContinuation.Resolve(snapshot, original, roster, [first, second]) == original.Id,
            "A name match or different canonical source redirected controls.");
    }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private sealed class CountOnlyDriver : IGameServerDriver
    {
        internal int? Players = 0;
        internal int Reads;
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Synthetic count-only continuation";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => false;
        public string? ManagedSaveDirectory(ServerProfile profile) => null;
        public string ManagedExecutablePath(ServerProfile profile) => "never-executed";
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? address) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => null;
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No process allowed.");
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw new InvalidOperationException("No process allowed.");
        public GameHealthResult Health(ManagedRun run)
        { Reads++; return new(true, "SyntheticCount", "Ready", "Synthetic count", Players, PlayerCountTrusted: Players is not null); }
        public Task<GameStopResult> StopAsync(System.Diagnostics.Process process, ManagedRun run) => throw new InvalidOperationException("No process allowed.");
    }
}
