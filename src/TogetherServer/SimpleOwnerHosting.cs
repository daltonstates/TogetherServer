using System.Security.Cryptography;

namespace TogetherServer;

// Host and Join consume the same held proof for an existing continuation.
// Adopting different completed progress prepares fresh managed storage and
// leaves the original world intact.
internal sealed class SimpleOwnerHostingService(LocalData data, HostManager manager,
    WorldPeerService peers, Func<Task> synchronizeListener)
{
    internal async Task<ActionResult> StartAsync(Guid profileId, CancellationToken cancellationToken)
    {
        async Task<ActionResult> Refused(string code, string message) =>
            new(false, code, message, await manager.SnapshotAsync());
        try
        {
            var snapshot = await manager.CompanionSnapshotAsync();
            var requested = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (requested is null) return await manager.StartAsync(profileId, cancellationToken);
            var sourceId = requested.SeparateCopySourceProfileId ?? requested.Id;
            var original = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == sourceId &&
                item.SeparateCopySourceProfileId is null);
            if (requested.SeparateCopyBranchHash is { } directBranch)
            {
                var retainedBranch = new SharedWorldSeparateCopyStore(data).Read(sourceId)
                    .SingleOrDefault(item => item.BranchHash == directBranch);
                if (retainedBranch is null)
                    return await Refused("SharedHostingReviewRequired", "Check this server's signed save before hosting.");
                var retainedRoot = new SharedWorldPeerRoot(retainedBranch.Offer.Roster.OwnerPublicKey,
                    sourceId, retainedBranch.Offer.Roster.GroupId);
                using var preparation = peers.BeginHostingPreparation(retainedRoot);
                var newest = await peers.ResolveLatestPreparedStartSourceAsync(retainedRoot, profileId, directBranch, cancellationToken);
                if (!newest.Ok || newest.Source is null) return await Refused(newest.Code, newest.Message);
                using var heldSource = newest.Source;
                void RequireScope()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    heldSource.RequireCurrent(data, retainedRoot, profileId, directBranch, cancellationToken);
                }
                RequireScope();
                using var working = await manager.VerifyLocalContinuationForStartAsync(sourceId, profileId, cancellationToken);
                if (working is null)
                    return await Refused("LocalHostingBusy", "Start is already being checked for this server. Check its current result before another instruction.");
                RequireScope();
                if (working.VersionHash != newest.Source.Descriptor.Version.VersionHash)
                    return await Refused("LatestSaveChanged", "A newer save is available. Use Host locally on the joined server to continue from it.");
                await manager.RecordHostingObservationAsync(profileId, newest, RequireScope, cancellationToken, working);
                return await manager.StartSeparateCopyAsync(profileId, directBranch, cancellationToken);
            }
            // A completed planned successor retains the original profile ID.
            // Authority state classifies this path only: the common manager
            // still verifies the signed restore, route, final save and fences.
            // Fenced sources and damaged/historical authority use those same gates.
            if (requested.SeparateCopySourceProfileId is null && new WorldAuthorityStore(data).HasState(requested.Id))
                return await manager.StartAsync(profileId, cancellationToken);
            if (original is null || !original.SharedSavesEnabled)
                return await manager.StartAsync(profileId, cancellationToken);
            var roster = await peers.ReadOwnerHostingRosterAsync(sourceId, cancellationToken);
            if (roster is null)
                return await Refused("SharedHostingReviewRequired", "This server's sharing permission needs review before hosting.");
            var root = new SharedWorldPeerRoot(roster.OwnerPublicKey, sourceId, roster.GroupId);
            // Enabling sharing can be followed immediately by Start. Register
            // this saved owner's signed world locally, without waiting for a
            // periodic pass or any saved Friend connection.
            var membership = await peers.EnsureOwnerHostingMembershipAsync(root, cancellationToken);
            if (!membership.Ok) return await Refused(membership.Code, membership.Message);
            using var ownerPreparation = peers.BeginHostingPreparation(root);
            var originalVersion = await manager.PeerPublishedVersionAsync(root);
            if (requested.Id == sourceId)
            {
                // Arm outbound completed-save sharing without changing listener
                // policy. Incoming connectivity is advisory for ordinary Start.
                var announcedOriginal = await peers.AnnounceLocalHostingAsync(root, cancellationToken);
                if (!announcedOriginal.Ok && announcedOriginal.Code != "RecipientConnectionRequired")
                    return await Refused(announcedOriginal.Code, announcedOriginal.Message);
                var originalLatest = await peers.ResolveLatestOriginalStartSourceAsync(root, cancellationToken);
                if (originalLatest.Source is { } originalSource)
                {
                    using var heldOriginal = originalSource;
                    void RequireOriginalScope()
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        heldOriginal.RequireCurrent(data, root, cancellationToken);
                        if (peers.LatestObservationHash(root) != originalLatest.ObservationHash)
                            throw new InvalidDataException("The original save's current signed information changed.");
                    }
                    RequireOriginalScope();
                    await manager.VerifyRetainedContinuationsAsync(sourceId, cancellationToken);
                    await manager.RecordOriginalHostingObservationAsync(originalLatest, RequireOriginalScope, cancellationToken);
                    return await manager.StartAsync(sourceId, cancellationToken);
                }
                if (!originalLatest.RecoveryRequired)
                {
                    if (originalLatest.Code == "SharedSaveUnavailable" && originalVersion is null)
                        return await manager.StartAsync(profileId, cancellationToken);
                    return await Refused(originalLatest.Code, originalLatest.Message);
                }
            }
            var latest = await peers.ResolveLatestRecoverySourceAsync(root, cancellationToken);
            if (!latest.Ok || latest.Source is null)
            {
                if (latest.Code == "SharedSaveUnavailable" && originalVersion is null && requested.Id == sourceId)
                    return await manager.StartAsync(profileId, cancellationToken);
                return await Refused(latest.Code, latest.Message);
            }
            var source = latest.Source;
            source.Verify(data, root);
            await manager.VerifyRetainedContinuationsAsync(sourceId, cancellationToken);
            if (requested.Id == sourceId && source.Descriptor.Version.VersionHash == originalVersion?.VersionHash)
                return await manager.StartAsync(sourceId, cancellationToken);
            await manager.VerifyOriginalContinuationAsync(sourceId, originalVersion, cancellationToken);

            var branch = source.Descriptor.Branch;
            var localId = branch is null ? (Guid?)null : SharedWorldBranchLiveSource.LocalProfileId(branch.BranchHash);
            var existing = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == localId &&
                item.SeparateCopySourceProfileId == sourceId);
            if (requested.SeparateCopySourceProfileId is not null)
                await manager.VerifiedLocalContinuationAsync(sourceId, requested.Id, cancellationToken);
            if (existing is not null)
            {
                var retained = await manager.VerifiedLocalContinuationAsync(sourceId, existing.Id, cancellationToken);
                if (retained?.VersionHash != source.Descriptor.Version.VersionHash) existing = null;
            }
            if (existing is null)
            {
                var declared = await peers.PrepareOwnerRecoveryAsync(root.GroupKey, source.BranchHash, true,
                    cancellationToken, source);
                if (!declared.Ok || declared.Branch is null) return await Refused(declared.Code, declared.Message);
                branch = declared.Branch;
            }
            if (branch is null) return await Refused("SharedHostingReviewRequired", "The latest save needs review before hosting.");
            var version = branch.Offer.Version;
            var ports = await manager.LocalHostingPortsAsync(version, existing ?? original, cancellationToken);
            if (!ports.Available) return await Refused("LocalPortsRequired", "The local game ports need attention before hosting.");
            var setup = new TakeoverLocalSetup(existing?.Kind == GameKinds.MinecraftJava ? existing.Minecraft?.ServerJarPath :
                existing?.ExecutablePath ?? (original.Kind == GameKinds.MinecraftJava ? original.Minecraft?.ServerJarPath : original.ExecutablePath),
                version.PortableSetup.GameVersion, version.PortableSetup.AddOns ?? [], true, ports.ControlPort, ports.GamePort);
            if (existing is null)
            {
                MinecraftInstallation? minecraft = null;
                if (version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock)
                {
                    var preparedId = SharedWorldBranchLiveSource.LocalProfileId(branch.BranchHash);
                    minecraft = await Task.Run(() => PlannedHandoffMinecraftSetup.Prepare(data, version with { ProfileId = preparedId },
                        preparedId, setup.ServerFile!, ports.GamePort, snapshot.Settings.Profiles), cancellationToken);
                    setup = setup with { ServerFile = minecraft.ArtifactPath };
                }
                var password = version.Game == GameKinds.Valheim ? data.LoadValheimPassword(original.Id) ??
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) : null;
                var restored = await manager.RestoreSeparateCopyAsync(sourceId, new(branch.BranchHash, setup,
                    original.Name, original.ServerName, password, minecraft?.ExecutablePath, minecraft?.ServerDirectory,
                    ports.FactorioRconPort), cancellationToken);
                if (!restored.Ok) return await Refused(restored.Code, restored.PendingChecks?.FirstOrDefault() ?? restored.Message);
            }
            var finished = await manager.FinishSeparateCopyAsync(sourceId, new(branch.BranchHash, setup));
            if (!finished.Ok || finished.ProfileId is null) return await Refused(finished.Code, finished.Message);
            var announced = await peers.AnnounceLocalHostingAsync(root, cancellationToken);
            if (!announced.Ok) return await Refused(announced.Code, announced.Message);
            await synchronizeListener();
            var rechecked = await peers.ResolveLatestRecoverySourceAsync(root, cancellationToken);
            if (!rechecked.Ok || rechecked.Source?.Descriptor.Version.VersionHash != source.Descriptor.Version.VersionHash)
                return await Refused(rechecked.Ok ? "LatestSaveChanged" : rechecked.Code,
                    rechecked.Ok ? "A newer completed save arrived. Continue hosting to check it before starting." : rechecked.Message);
            await manager.RecordHostingObservationAsync(finished.ProfileId.Value, rechecked, cancellationToken);
            return await manager.StartSeparateCopyAsync(finished.ProfileId.Value, branch.BranchHash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
        { return await Refused("SharedHostingReviewRequired", "Hosting stopped safely. The latest save or local setup needs review; existing worlds were kept."); }
    }
}
