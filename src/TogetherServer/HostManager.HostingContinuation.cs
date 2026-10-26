namespace TogetherServer;

public sealed partial class HostManager
{
    internal Func<SharedWorldPeerRoot, string?, string?>? SharedHostingStartBlock { get; set; }
    internal Func<SharedWorldPeerRoot, string?>? SharedHostingObservation { get; set; }
    private sealed record HostingObservation(SharedWorldPeerRoot Root, string VersionHash,
        string ObservationHash, DateTimeOffset CheckedUtc,
        WorldPeerPreparedStartSource? PreparedSource = null, Action? RequireScope = null,
        WorldPeerOriginalStartSource? OriginalSource = null);
    private readonly Dictionary<Guid, HostingObservation> hostingObservations = [];

    internal async Task RecordHostingObservationAsync(Guid localProfileId, WorldPeerLatestSourceResult latest,
        CancellationToken cancellationToken)
    {
        if (!latest.Ok || latest.Source is null || !SharedWorldPeerTrust.ValidHash(latest.ObservationHash))
            throw new InvalidDataException("Repeat the latest-save checks before starting.");
        await RecordHostingObservationAsync(localProfileId, latest.Source.Root,
            latest.Source.Descriptor.Version.VersionHash, latest.ObservationHash!, cancellationToken);
    }

    internal Task RecordHostingObservationAsync(Guid localProfileId, WorldPeerPreparedStartResult latest,
        Action requireScope, CancellationToken cancellationToken)
    {
        if (!latest.Ok || latest.Source is null || latest.Source.LocalProfileId != localProfileId ||
            !SharedWorldPeerTrust.ValidHash(latest.ObservationHash))
            throw new InvalidDataException("Repeat the prepared source checks before starting.");
        return RecordHostingObservationAsync(localProfileId, latest.Source.Root,
            latest.Source.Descriptor.Version.VersionHash, latest.ObservationHash!, cancellationToken, latest.Source, requireScope);
    }

    internal Task RecordOriginalHostingObservationAsync(WorldPeerOriginalStartResult latest,
        Action requireScope, CancellationToken cancellationToken)
    {
        if (!latest.Ok || latest.Source is null || latest.RecoveryRequired ||
            !SharedWorldPeerTrust.ValidHash(latest.ObservationHash))
            throw new InvalidDataException("Repeat the original publication checks before starting.");
        return RecordHostingObservationAsync(latest.Source.Root.ProfileId, latest.Source.Root,
            latest.Source.Descriptor.Version.VersionHash, latest.ObservationHash!, cancellationToken,
            requireScope: requireScope, originalSource: latest.Source);
    }

    private async Task RecordHostingObservationAsync(Guid localProfileId, SharedWorldPeerRoot root,
        string versionHash, string observationHash, CancellationToken cancellationToken,
        WorldPeerPreparedStartSource? preparedSource = null, Action? requireScope = null,
        WorldPeerOriginalStartSource? originalSource = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            hostingObservations[localProfileId] = new(root, versionHash, observationHash, clock.GetUtcNow(),
                preparedSource, requireScope, originalSource);
        }
        finally { gate.Release(); }
    }

    private string? SharedContinuationStartIssue(ServerProfile profile, out Action? requireOriginalSource)
    {
        requireOriginalSource = null;
        if (profile.SeparateCopySourceProfileId is null)
        {
            if (!hostingObservations.TryGetValue(profile.Id, out var pendingOriginal) || pendingOriginal.OriginalSource is null)
                return null;
            hostingObservations.Remove(profile.Id);
            void RequireOriginal()
            {
                if (pendingOriginal.Root.ProfileId != profile.Id ||
                    clock.GetUtcNow() - pendingOriginal.CheckedUtc > TimeSpan.FromSeconds(30) ||
                    pendingOriginal.RequireScope is null ||
                    SharedHostingObservation is { } observe && observe(pendingOriginal.Root) != pendingOriginal.ObservationHash)
                    throw new InvalidDataException("The original save's current signed information changed.");
                pendingOriginal.RequireScope?.Invoke();
                pendingOriginal.OriginalSource.RequireProfile(data, profile);
            }
            try { RequireOriginal(); requireOriginalSource = RequireOriginal; return null; }
            catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
            { return "The original publication or current sharing permission changed. Check the latest save again before Start."; }
        }
        if (SharedHostingObservation is null &&
            (!hostingObservations.TryGetValue(profile.Id, out var pending) || pending.PreparedSource is null)) return null;
        try
        {
            if (!hostingObservations.Remove(profile.Id, out var observation) ||
                clock.GetUtcNow() - observation.CheckedUtc > TimeSpan.FromSeconds(30) ||
                observation.Root.ProfileId != profile.SeparateCopySourceProfileId ||
                SharedHostingObservation is null || SharedHostingObservation(observation.Root) != observation.ObservationHash)
                return "A shared save or permission changed. Continue hosting to check the latest copy before starting.";
            var branch = profile.SeparateCopyBranchHash is { } hash ?
                CurrentSeparateBranch(observation.Root.ProfileId, hash) : null;
            var state = ReadSeparateHost(profile.Id);
            observation.RequireScope?.Invoke();
            if (observation.PreparedSource is { } prepared)
                prepared.RequireCurrent(data, observation.Root, profile.Id, profile.SeparateCopyBranchHash!);
            var current = observation.PreparedSource?.Descriptor.Version ?? (state?.Started == true && branch is not null ?
                new SharedWorldSeparateCopyStore(data).ReadLatestSnapshot(branch)?.Version : branch?.Offer.Version);
            if (current?.VersionHash != observation.VersionHash ||
                !VerifiedSuccessorCopy(SuccessorPayloadRoot(profile.WorldDirectory, current), current))
                return "This server no longer matches the latest checked save. Continue hosting to check it again.";
            observation.RequireScope?.Invoke();
            observation.PreparedSource?.RequireCurrent(data, observation.Root, profile.Id, profile.SeparateCopyBranchHash!);
            return SharedHostingObservation(observation.Root) == observation.ObservationHash ? null :
                "A shared save or permission changed. Continue hosting to check the latest copy before starting.";
        }
        catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
        { return "The retained shared save needs review before hosting. Existing worlds were kept."; }
    }

    internal async Task VerifyRetainedContinuationsAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var profile in settings.Profiles.Where(item => item.SeparateCopySourceProfileId == sourceId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (runs.Any(run => run.ProfileId == profile.Id))
                    throw new InvalidDataException("Stop this world's local server safely before switching hosts.");
                var state = ReadSeparateHost(profile.Id);
                if (state is null) throw new InvalidDataException("A retained local save needs review.");
                var branch = profile.SeparateCopyBranchHash is { } hash ? CurrentSeparateBranch(sourceId, hash) : null;
                var version = state.Started && branch is not null ?
                    new SharedWorldSeparateCopyStore(data).ReadLatestSnapshot(branch)?.Version : branch?.Offer.Version;
                if (version is null || !VerifiedSuccessorCopy(SuccessorPayloadRoot(profile.WorldDirectory, version), version))
                    throw new InvalidDataException("This PC has progress that has not been shared safely. Keep every local save and review it before switching hosts.");
            }
        }
        finally { gate.Release(); }
    }

    private string? SharedHostingStartIssue(ServerProfile profile)
    {
        if (!profile.SharedSavesEnabled || profile.SeparateCopySourceProfileId is not null ||
            SharedHostingStartBlock is null) return null;
        try
        {
            var roster = sharedWorlds.ReadRoster(profile, requireRetainedSigningKey: true);
            return roster is null ? null : SharedHostingStartBlock(new(roster.OwnerPublicKey, profile.Id, roster.GroupId),
                sharedWorlds.Status(profile).Latest?.VersionHash);
        }
        catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
        { return "The latest shared save or signed permission needs review before hosting. Existing worlds were kept."; }
    }

    internal async Task VerifyOriginalContinuationAsync(Guid profileId, SharedWorldVersion? version,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null || profile.SeparateCopySourceProfileId is not null ||
                runs.Any(run => run.ProfileId == profileId || settings.Profiles.Any(item =>
                    item.Id == run.ProfileId && item.SeparateCopySourceProfileId == profileId)))
                throw new InvalidDataException("Stop this world's local server safely before switching hosts.");
            if (version is null || version.ProfileId != profileId || version.Game != profile.Kind ||
                version.WorldId != profile.WorldId ||
                !VerifiedSuccessorCopy(SuccessorPayloadRoot(profile.WorldDirectory, version), version))
                throw new InvalidDataException("This PC has progress that has not been shared safely. Keep its world and review the saves before switching hosts.");
        }
        finally { gate.Release(); }
    }

    // Explicit preparation only. A newer peer save is restored into a fresh
    // world; this check prevents unpublished local edits being silently ignored.
    internal async Task<SharedWorldVersion?> VerifiedLocalContinuationAsync(Guid sourceProfileId,
        Guid localProfileId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (runs.Any(run => run.ProfileId == localProfileId))
                throw new InvalidDataException("This server is running or needs process review. Stop it safely before changing hosts.");
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == localProfileId);
            if (profile?.SeparateCopySourceProfileId != sourceProfileId ||
                profile.SeparateCopyBranchHash is not { } branchHash)
                throw new InvalidDataException("The retained local save needs review.");
            var branch = CurrentSeparateBranch(sourceProfileId, branchHash) ??
                throw new InvalidDataException("The retained save's signed permission needs review.");
            var state = ReadSeparateHost(localProfileId) ??
                throw new InvalidDataException("The retained local save needs review.");
            var version = state.Started ? new SharedWorldSeparateCopyStore(data).ReadLatestSnapshot(branch)?.Version :
                branch.Offer.Version;
            if (version is null || !VerifiedSuccessorCopy(SuccessorPayloadRoot(profile.WorldDirectory, version), version))
                throw new InvalidDataException("This PC has progress that has not been shared safely. Keep this save and review it before switching hosts.");
            cancellationToken.ThrowIfCancellationRequested();
            return version;
        }
        finally { gate.Release(); }
    }
}
