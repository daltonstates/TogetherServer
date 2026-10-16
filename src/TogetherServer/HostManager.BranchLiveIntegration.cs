using System.Text.Json;

namespace TogetherServer;

public sealed partial class HostManager
{
    private SharedWorldStatus RecoverySharedWorldStatusUnderGate(ServerProfile profile)
    {
        try
        {
            var branch = BranchForLiveProfileUnderGate(profile);
            var latest = new SharedWorldSeparateCopyStore(data).ReadPublished(branch)?.Version ?? branch.Offer.Version;
            var allowed = LiveAcceptanceSourceBlock(profile) is null;
            return new(allowed, latest, allowed ? null :
                "This recovery source or current hosting permission needs review. Its signed history was kept.",
                LiveSave: LiveSaveStatusUnderGate(profile), CanManageSharing: false,
                Authority: new("NoTakeover", "This PC shares a separate recovered history. The group's selected default and other Hosts are unchanged."));
        }
        catch (Exception error) when (LiveOrphanReviewFailure(error))
        {
            return new(false, null, "This recovery history needs verification. Keep its source and every signed copy.",
                LiveSave: SharedWorldLiveSaveAdapters.Status(profile.Kind), CanManageSharing: false);
        }
    }

    private void RequireAcceptedBranchLiveScopeUnderGate(ServerProfile profile, ManagedRun run,
        LiveSaveSourceContext expectedSource, LiveSaveAcceptanceRecord evidence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireLiveAcceptanceRunProfile(profile, run);
        var currentSource = ResolveBranchLiveSourceUnderGate(profile, requireStarted: true);
        var currentScope = LiveSaveInstallationScope(profile, token);
        if (!SharedWorldBranchLiveSource.SameSource(expectedSource, currentSource) ||
            !SharedWorldLiveSaveAdapters.SameRealInstallation(evidence.Scope, currentScope) ||
            evidence.Artifact is not { Schema: 2 } artifact ||
            !LiveSaveAcceptanceTrust.MatchesSource(artifact, currentSource, profile, clock.GetUtcNow()) ||
            ProtectedLiveSaveAcceptanceRecord(profile, currentScope)?.TestId != evidence.TestId)
            throw new InvalidDataException("The recovery installation's protected six-stage acceptance or current source changed.");
        token.ThrowIfCancellationRequested();
    }

    private WorldSeparateCopyBranch BranchForLiveProfileUnderGate(ServerProfile profile)
    {
        if (profile.SeparateCopySourceProfileId is not { } rootId || profile.SeparateCopyBranchHash is not { } hash ||
            profile.Id != SharedWorldBranchLiveSource.LocalProfileId(hash))
            throw new InvalidDataException("The local recovery installation identity changed.");
        var branch = new SharedWorldSeparateCopyStore(data).Read(rootId).SingleOrDefault(item => item.BranchHash == hash);
        if (branch is null || !SharedWorldSeparateCopyStore.Verify(branch) ||
            branch.Offer.Version.ProfileId != rootId || branch.Offer.Version.Game != profile.Kind ||
            branch.Offer.Version.WorldId != profile.WorldId)
            throw new InvalidDataException("The saved recovery history changed.");
        return branch;
    }

    private SharedWorldVersion? LivePublishedVersionUnderGate(ServerProfile profile)
    {
        if (!LiveSaveAcceptanceTrust.IsRecoveryProfile(profile)) return sharedWorlds.Status(profile).Latest;
        var branch = BranchForLiveProfileUnderGate(profile);
        return new SharedWorldSeparateCopyStore(data).ReadPublished(branch)?.Version ?? branch.Offer.Version;
    }

    // Metadata review only: all version/ancestry decisions go through the branch
    // store's existing validators. Stages, orphan directories and pointer intents
    // stay intact and block another capture; no canonical path is consulted.
    private bool LivePublicationNeedsReviewUnderGate(ServerProfile profile)
    {
        if (!LiveSaveAcceptanceTrust.IsRecoveryProfile(profile))
            return sharedWorlds.ReviewLiveOrphan(profile).Code != "None";
        var branch = BranchForLiveProfileUnderGate(profile);
        var store = new SharedWorldSeparateCopyStore(data);
        _ = store.ReadPublished(branch);
        var root = store.PublishedRoot(branch);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        if (File.Exists(root)) return true;
        if (!Directory.Exists(root)) return false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(entry);
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return true;
            if (Directory.Exists(entry))
            {
                if (name == "signed-history") continue;
                if (!SharedWorldPeerTrust.ValidHash(name)) return true;
                var path = Path.Combine(entry, "version.json");
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(path).Length > SharedWorldService.MaximumManifestBytes) return true;
                var version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), LiveSaveAcceptanceTrust.Json);
                if (version is null || version.VersionHash != name || version.Number <= branch.Offer.Version.Number) return true;
                // An uncommitted next directory is absent from this exact
                // branch's committed lineage and therefore requires review.
                try
                {
                    if (store.ReadPublishedManifest(branch, version.Number, name).Signature != version.Signature) return true;
                }
                catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { return true; }
            }
            else if (name != "latest.json") return true;
        }
        return false;
    }

    internal static bool BranchLiveAttemptMatches(ServerProfile profile, LiveSaveAttempt attempt,
        WorldSeparateCopyBranch branch, SharedWorldVersion current)
    {
        if (attempt.ProfileId != profile.Id || attempt.BranchHash != profile.SeparateCopyBranchHash ||
            attempt.BranchHash != branch.BranchHash || profile.SeparateCopySourceProfileId != current.ProfileId ||
            profile.Id != SharedWorldBranchLiveSource.LocalProfileId(branch.BranchHash) ||
            attempt.CaptureId != attempt.RequestId || !SharedWorldSeparateCopyStore.Verify(branch) ||
            !SharedWorldSeparateCopyStore.VerifySnapshotVersion(branch, current) ||
            current.Game != profile.Kind || current.WorldId != profile.WorldId) return false;
        if (attempt.State == "Unchanged")
            return current.VersionHash == attempt.VersionHash && current.Number == attempt.VersionNumber;
        return attempt.State is "Publishing" or "Published" && current.Schema == 5 &&
            current.SigningPublicKey == branch.Offer.Proposal.CandidatePublicKey &&
            current.CaptureKind == SharedWorldCaptureKinds.LiveSave && current.BackupId == attempt.CaptureId;
    }

    private bool RecoverBranchLiveAttemptUnderGate(ServerProfile profile, LiveSaveAttempt attempt)
    {
        if (attempt.State is not ("Publishing" or "Published" or "Unchanged") || attempt.CaptureId is null ||
            attempt.BranchHash != profile.SeparateCopyBranchHash) return false;
        var branch = BranchForLiveProfileUnderGate(profile);
        var latest = new SharedWorldSeparateCopyStore(data).ReadPublished(branch)?.Version ?? branch.Offer.Version;
        if (!BranchLiveAttemptMatches(profile, attempt, branch, latest)) return false;
        CompleteBranchLiveAttempt(attempt, latest, attempt.State == "Unchanged");
        return true;
    }

    private void CompleteBranchLiveAttempt(LiveSaveAttempt attempt, SharedWorldVersion version, bool unchanged)
    {
        attempt.State = unchanged ? "Unchanged" : "Published";
        attempt.Code = unchanged ? "LiveSaveUnchanged" : "LiveSavePublished";
        attempt.Message = unchanged
            ? "The sealed capture matches this recovery branch's verified copy. Its original save date stays visible."
            : "The accepted adapter's sealed save is current within this recovery branch. Other histories remain preserved; game load is a separate check.";
        attempt.VersionHash = version.VersionHash; attempt.VersionNumber = version.Number;
        attempt.CompletedUtc ??= clock.GetUtcNow();
    }
}
