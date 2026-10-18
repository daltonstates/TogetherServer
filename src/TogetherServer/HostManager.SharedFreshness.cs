using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed partial class HostManager
{
    private SharedWorldFreshnessScheduler? sharedFreshnessScheduler;

    // The coordinator calls this from the existing supervisor and may discard the
    // returned (internally observed) task. Repeated calls share the current flight.
    // Never await this while already holding HostManager's lifecycle gate.
    public Task ScheduleSharedFreshness(CancellationToken cancellationToken) =>
        LazyInitializer.EnsureInitialized(ref sharedFreshnessScheduler, () =>
            new SharedWorldFreshnessScheduler(clock, SharedFreshnessTargetsAsync,
                (profileId, token) => SaveAndShareAsync(profileId, new(Guid.NewGuid()), token)))!
            .Schedule(cancellationToken);

    internal async Task<IReadOnlyList<SharedFreshnessTarget>> SharedFreshnessTargetsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var attempts = ReadLiveSaveAttempts();
            var acceptance = AcceptanceStore.Read();
            return settings.Profiles.Where(profile => profile.SharedSavesEnabled ||
                LiveSaveAcceptanceTrust.IsRecoveryProfile(profile) && HasProtectedLiveAcceptanceCandidate(profile, acceptance)).Select(profile =>
            {
                var prior = attempts.LastOrDefault(item => item.ProfileId == profile.Id);
                var code = SharedFreshnessBlockUnderGate(profile, nominationOnly: true, acceptance: acceptance);
                return new SharedFreshnessTarget(profile.Id, code is null, code ?? "SharedFreshnessPending",
                    prior?.CompletedUtc ?? prior?.RequestedUtc, prior?.State is "Failed" or "Canceled");
            }).ToArray();
        }
        finally { gate.Release(); }
    }

    private string? SharedFreshnessBlockUnderGate(ServerProfile profile, bool nominationOnly = false,
        IReadOnlyList<LiveSaveAcceptanceRecord>? acceptance = null)
    {
        var recovery = LiveSaveAcceptanceTrust.IsRecoveryProfile(profile);
        if (!profile.SharedSavesEnabled && !recovery) return "SharingOff";
        if (profile.WorldLoadRehearsalId is not null)
            return "LiveSaveAuthorityBlocked";
        if (!(!recovery && profile.Kind == GameKinds.Fixture && stagingLiveFixtureEnabled) &&
            !(nominationOnly ? HasProtectedLiveAcceptanceCandidate(profile, acceptance ?? AcceptanceStore.Read()) :
                InstalledLiveSaveAccepted(profile))) return "GameAcceptanceRequired";
        if (data.Recovery.LifecycleBlocked || (recovery ? LiveAcceptanceSourceBlock(profile) is not null :
            data.HasProtected(PlannedHandoffName(profile.Id)) || SharedAuthorityBlocked(profile.Id, out _)))
            return "LiveSaveAuthorityBlocked";
        var run = runs.SingleOrDefault(item => item.ProfileId == profile.Id);
        return run is null || run.StopRequestedUtc is not null ? "LiveSaveNotRunning" : null;
    }

    // Owner read projection. Verification reads only signed published payloads,
    // never the running world. Restore/solo recovery must still use their own
    // current lineage, consent and exact-copy verification gates.
    public async Task<SharedWorldFreshnessStatus> SharedFreshnessStatusAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            var live = profile is null ? SharedWorldLiveSaveAdapters.Status(null) : LiveSaveStatusUnderGate(profile);
            if (profile is null)
                return new(profileId, false, "UnknownProfile", "Choose a saved server.",
                    null, null, null, null, null, null, null, false, 0, live);
            var state = sharedFreshnessScheduler?.State(profileId) ?? new();
            var attempts = ReadLiveSaveAttempts().Where(item => item.ProfileId == profileId).ToArray();
            var last = attempts.LastOrDefault();
            var failure = attempts.LastOrDefault(item => item.State is "Failed" or "Canceled");
            var lastAttemptUtc = state.LastAttemptUtc > last?.RequestedUtc ? state.LastAttemptUtc :
                last?.RequestedUtc ?? state.LastAttemptUtc;
            var journalFailureUtc = failure?.CompletedUtc;
            var schedulerFailureIsLatest = state.LastFailureUtc is { } scheduledFailure &&
                (journalFailureUtc is null || scheduledFailure > journalFailureUtc.Value);
            var lastFailureUtc = schedulerFailureIsLatest ? state.LastFailureUtc : journalFailureUtc;
            var lastFailureCode = schedulerFailureIsLatest ? state.LastFailureCode : failure?.Code;
            var block = SharedFreshnessBlockUnderGate(profile);
            var enabled = profile.SharedSavesEnabled || LiveSaveAcceptanceTrust.IsRecoveryProfile(profile) &&
                block is not ("GameAcceptanceRequired" or "LiveSaveAuthorityBlocked");
            SharedWorldFreshnessCopy? copy;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                copy = await Task.Run(() => sharedWorlds.NewestCompleteVerified(profile,
                    clock.GetUtcNow(), deadline.Token), CancellationToken.None);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(profileId, enabled, "SharedFreshnessVerificationPending",
                    "Complete-copy verification exceeded this bounded check. Keep all copies and retry; no save was rejected because of age.",
                    null, null, lastAttemptUtc, null, lastFailureUtc, lastFailureCode,
                    state.NextAttemptUtc, state.InProgress, state.ConsecutiveFailures, live);
            }
            var safeAttempt = copy is null ? null : attempts.LastOrDefault(item =>
                item.VersionHash == copy.Version.VersionHash && item.State is "Published" or "Unchanged");
            var latestHash = LivePublishedVersionUnderGate(profile)?.VersionHash;
            var code = block ?? (state.InProgress ? "SharedFreshnessCapturing" :
                state.ConsecutiveFailures > 0 ? "SharedFreshnessBackoff" :
                copy is null ? "NoVerifiedSharedSave" :
                copy.Version.VersionHash != latestHash ? "NewerCopyUnverified" : "SharedFreshnessReady");
            var message = code switch
            {
                "SharingOff" => "Automatic shared snapshots are off. Existing verified copies have no recovery age cutoff.",
                "GameAcceptanceRequired" => "Automatic live snapshots await this game's real completion, immutable snapshot, separate-device transfer and load/change/restart acceptance. Existing verified copies remain usable regardless of age.",
                "LiveSaveNotRunning" => "Waiting for a managed running server. Scheduling never starts or stops a game; verified older saves remain usable.",
                "LiveSaveAuthorityBlocked" => "Shared authority or copy work needs review before another capture. Keep every existing copy.",
                "SharedFreshnessCapturing" => "A bounded safe save capture is in progress. The last complete verified copy stays available.",
                "SharedFreshnessBackoff" => "The latest safe-capture attempt failed. The next automatic attempt waits for backoff; older verified copies stay available.",
                "NewerCopyUnverified" => "A newer published payload is incomplete or failed verification. The newest complete verified predecessor is shown; no copies or branches were removed.",
                "NoVerifiedSharedSave" => "No complete verified shared payload was found in this source's signed history. Incomplete and unverified copies are never selected.",
                _ => "The newest complete verified shared copy is shown. Age is information, never a recovery cutoff; game load remains a separate check."
            };
            return new(profileId, enabled, code, message, copy,
                SharedWorldFreshness.CopyAgeSeconds(copy, clock.GetUtcNow()), lastAttemptUtc,
                safeAttempt?.CapturedUtc is { } saved && saved != default ? saved : SharedWorldFreshness.CapturedUtc(copy),
                lastFailureUtc, lastFailureCode,
                block is null ? state.NextAttemptUtc : null, state.InProgress || activeLiveSaves.ContainsKey(profileId),
                state.ConsecutiveFailures, live);
        }
        catch (Exception ex) when (LiveOrphanReviewFailure(ex))
        {
            return new(profileId, false, "SharedFreshnessReviewRequired",
                "Shared-copy or attempt records could not be verified. Keep source, signed history and concurrent recovery copies for review.",
                null, null, null, null, null, "SharedFreshnessReviewRequired", null, false, 0,
                SharedWorldLiveSaveAdapters.Status(null));
        }
        finally { gate.Release(); }
    }

    // Compatibility projection of the actual protected candidate workflow.
    public async Task<LiveSaveInstallationAcceptance> LiveSaveAcceptanceChecklistAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var view = await LiveSaveAcceptanceAsync(profileId, cancellationToken);
        return new(view.Accepted, view.Code, view.Message, view.Scope, view.Stages);
    }

    private LiveSaveAcceptanceScope LiveSaveInstallationScope(ServerProfile profile, CancellationToken cancellationToken)
    {
        var binaries = new List<object>();
        var versions = new List<string>();
        var executable = profile.Kind == GameKinds.Fixture ? profile.ExecutablePath :
            games.TryGet(profile.Kind, out var installedDriver) ? installedDriver.ManagedExecutablePath(profile) : null;
        foreach (var path in profile.Kind == GameKinds.MinecraftJava
                     ? new[] { executable ?? "", profile.Minecraft?.ServerJarPath ?? "" }
                     : new[] { executable ?? "" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException();
            var fullPath = Path.GetFullPath(path);
            for (string? node = fullPath; node is not null; node = Path.GetDirectoryName(node))
                if ((File.GetAttributes(node) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 1024L * 1024 * 1024 ||
                (File.GetAttributes(stream.SafeFileHandle) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = stream.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(fullPath);
            var numericVersion = string.IsNullOrWhiteSpace(version.FileVersion) ? "Unavailable" :
                $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}";
            versions.Add(numericVersion);
            binaries.Add(new { Path = fullPath.ToUpperInvariant(), stream.Length, Version = numericVersion, Hash = Convert.ToHexString(hash.GetHashAndReset()) });
        }
        cancellationToken.ThrowIfCancellationRequested();
        var setup = ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data));
        var configuration = new
        {
            Contract = 2,
            Profile = profile,
            SaveRoot = Path.GetFullPath(profile.WorldDirectory).ToUpperInvariant(),
            profile.GamePort,
            profile.Crossplay,
            profile.PublicListing,
            RconPort = profile.Factorio?.RconPort,
            Setup = setup
        };
        cancellationToken.ThrowIfCancellationRequested();
        return SharedWorldLiveSaveAdapters.BindInstallation(profile.Id, profile.Kind,
            Path.GetFullPath(data.RootPath).ToUpperInvariant(),
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binaries))),
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configuration))),
            profile.Kind == GameKinds.Fixture) with
        { BinaryVersion = string.Join(";", versions) };
    }
}
