using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// The existing file-only fixture supplies generated identities, protected
// state and a driver which throws before any process can be launched.
internal static partial class LocalHostingChecks
{
    internal static async Task PreparedPublicationReuseAsync(string root)
    {
        await PreparedPublicationAsync(root, "none", 315);
        foreach (var fault in new[] { "published-file", "missing-published", "missing-branch-proof", "working-file",
            "setup", "newer", "fork", "consent", "grant", "revoked", "expired", "selection", "observation",
            "late-consent", "late-revoked", "late-setup", "late-semantic-setup" })
            await PreparedPublicationAsync(root, fault, 1);
        await PreparedPublicationLifetimeAsync(root);
    }

    private static async Task PreparedPublicationAsync(string root, string fault, int fileCount)
    {
        using var fixture = new Fixture(root, granted: true, automaticReceiving: true, fileCount: fileCount, initialVersion: 3,
            alternateConnection: fault == "selection");
        var prepared = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(prepared.ReadyForManualStart && prepared.LocalProfileId is not null && prepared.BranchHash is not null,
            "the generated base-three preparation was not ready");
        var publication = fixture.PublishStoppedContinuation(prepared);
        var heldAtDriver = false;
        if (fault == "none") fixture.Driver.BeforeProcessAttempt = () =>
        {
            try
            {
                using var blockedWrite = new FileStream(Path.Combine(publication.Vault, publication.Version.VersionHash,
                    SharedWorldService.PayloadDirectory, "world.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { heldAtDriver = true; }
        };
        using var peers = fixture.PreparedPublicationPeers();
        await peers.RefreshMembershipsAsync(false, default);
        fixture.PublishPreparedClaim(publication.Branch, publication.Version);
        var beforeStart = await fixture.Service.StatusAsync(fixture.ProfileId);
        Require(beforeStart.Allowed && beforeStart.Consented && beforeStart.ReadyForManualStart && !beforeStart.Running &&
            beforeStart.LocalProfileId == prepared.LocalProfileId && beforeStart.BranchHash == prepared.BranchHash,
            $"generated stopped publication lacks exact pre-Start readiness: code={beforeStart.Code}; allowed={beforeStart.Allowed}; " +
            $"consented={beforeStart.Consented}; ready={beforeStart.ReadyForManualStart}; running={beforeStart.Running}; " +
            $"profileMatch={beforeStart.LocalProfileId == prepared.LocalProfileId}; branchMatch={beforeStart.BranchHash == prepared.BranchHash}; " +
            $"steps={string.Join(',', beforeStart.Steps.Select(step => step.Key + ':' + step.State))}");
        var cacheRoot = Path.Combine(fixture.Data.RootPath, "world-peer-recovery-sources");
        Require(!Directory.Exists(cacheRoot), "the fixture already had an unrelated retained recovery cache");

        // A legitimate publication is a different provenance from a recovery
        // cache. The old generic source contract must still reject that alias.
        var genericSource = new WorldPeerRecoverySource(fixture.PublicationRoot, publication.Branch.BranchHash,
            new(publication.Version, publication.Branch), publication.Vault);
        var rejectedAlias = false;
        try { genericSource.Verify(fixture.Data, fixture.PublicationRoot); }
        catch (InvalidDataException) { rejectedAlias = true; }
        Require(rejectedAlias, "a published-directory alias bypassed the strict recovery StorageRoot contract");

        if (fault is "grant" or "revoked" or "expired")
            await fixture.ChangePreparedGrantAsync(fault);
        else if (fault == "consent")
            Require(new WorldPeerReceiveConsentStore(fixture.Data).Set(fixture.PublicationRoot, fixture.DeviceId, false),
                "the generated receive-consent withdrawal failed");
        else if (fault == "published-file")
            File.WriteAllText(Path.Combine(publication.Vault, publication.Version.VersionHash,
                SharedWorldService.PayloadDirectory, "world.dat"), "changed signed publication bytes");
        else if (fault == "missing-published")
            File.Delete(Path.Combine(publication.Vault, "latest.json"));
        else if (fault == "missing-branch-proof")
            fixture.RemovePreparedBranchProof();
        else if (fault == "working-file")
            File.WriteAllText(Path.Combine(publication.Profile.WorldDirectory, "world.dat"), "unpublished later working bytes");
        else if (fault == "setup")
            fixture.ChangePreparedSetup(prepared.LocalProfileId.Value);
        else if (fault is "newer" or "fork")
        {
            var head = SharedWorldService.SignVersion(publication.Version with
            {
                Number = fault == "newer" ? 5 : 4,
                ParentHash = fault == "newer" ? publication.Version.VersionHash : publication.Branch.Offer.Version.VersionHash,
                BackupId = Guid.NewGuid(),
                Files = [new("world.dat", 4, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fork"))))]
            }, fixture.PreparedMemberKey);
            fixture.PublishPreparedClaim(publication.Branch, head);
        }

        var resolving = 0;
        var legacyResolutions = 0;
        var copying = 0;
        var verifiedFiles = 0;
        var announcements = 0;
        var observations = 0;
        var semanticSetupRejected = false;
        fixture.Manager.SharedHostingObservation = world =>
        {
            observations++;
            return peers.LatestObservationHash(world);
        };
        var service = fixture.WithPreparedPublicationSource(async (world, localId, branchHash, token, progress) =>
        {
            resolving++;
            var result = await peers.ResolveLatestPreparedStartSourceAsync(world, localId, branchHash, token, update =>
            {
                if (update.Phase == "PreparingCopy") copying++;
                if (update.CompletedFiles is { } completed) verifiedFiles = Math.Max(verifiedFiles, completed);
                progress?.Invoke(update);
            });
            if (fault is "selection" or "late-consent" or "late-revoked" or "late-setup" or "late-semantic-setup" or "observation")
                Require(result is { Ok: true, Source: not null },
                    "the intended after-proof mutation did not receive a valid held source: " + fault);
            if (fault == "selection") fixture.SelectDifferentPreparedConnection();
            else if (fault == "late-consent")
                Require(new WorldPeerReceiveConsentStore(fixture.Data).Set(world, fixture.DeviceId, false),
                    "the generated late consent withdrawal failed");
            else if (fault == "late-revoked") await fixture.ChangePreparedGrantAsync("revoked");
            else if (fault == "late-setup") fixture.ChangePreparedSetup(localId);
            else if (fault == "late-semantic-setup")
            {
                fixture.ChangePreparedSemanticSetup(localId);
                Require(result.Source is not null, "the semantic setup check did not receive a real held proof");
                try { result.Source.RequireCurrent(fixture.Data, world, localId, branchHash, token); }
                catch (InvalidDataException) { semanticSetupRejected = true; }
                Require(semanticSetupRejected, "full decoded setup changed without invalidating the held prepared proof");
            }
            else if (fault == "observation") fixture.ChangePreparedCopyObservation(publication.Branch, publication.Version);
            return result;
        }, (world, token) =>
        {
            token.ThrowIfCancellationRequested();
            Require(world == fixture.PublicationRoot, "prepared Start announced another generated world");
            announcements++;
            return Task.FromResult(new WorldPeerActionResult(true, "HostingAddressPrepared", "Synthetic announcement."));
        }, (world, token) =>
        {
            legacyResolutions++;
            return peers.ResolveLatestPreparedSourceAsync(world, token, update =>
            {
                if (update.Phase == "PreparingCopy") copying++;
            });
        });

        // A stale Connected badge is not a reason to re-enroll, query a
        // planned move, or pull again from the original Host during Start.
        // This fixture refuses every direct original-Host network request.
        if (fault == "none") fixture.MarkOriginalHostConnected();

        LocalHostingActionResult? result = null;
        var reachedDriver = false;
        var rejectedStoredHistory = false;
        try
        {
            result = await service.StartPreparedAsync(fixture.ProfileId,
                new(prepared.LocalProfileId.Value, prepared.BranchHash, null, true), default);
        }
        catch (InvalidOperationException error) when (error.Message == "No process operation is permitted.")
        { reachedDriver = true; }
        catch (InvalidDataException) when (fault == "missing-branch-proof")
        { rejectedStoredHistory = true; }

        Require(legacyResolutions == 0 && copying == 0 && !Directory.Exists(cacheRoot) && fixture.Data.LoadRuns().Count == 0,
            "prepared Start copied a recovery cache, reported a transfer or recorded a managed process: " + fault +
            $"; legacy={legacyResolutions}; copying={copying}; cache={Directory.Exists(cacheRoot)}; runs={fixture.Data.LoadRuns().Count}");
        using (var reopened = new FileStream(Path.Combine(publication.Vault, publication.Version.VersionHash,
            SharedWorldService.PayloadDirectory, "world.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require(reopened.CanWrite, "the held prepared source leaked its immutable read lease after Start returned");
        if (fault == "missing-branch-proof")
        {
            // Direct service calls retain the strict store exception. The local
            // HTTP route maps it to LocalHostingRequestRejected; no proof or
            // launch boundary may be reached with the protected branch absent.
            Require(rejectedStoredHistory && !reachedDriver && resolving == 0 &&
                fixture.Driver.ProcessAttempts == 0 && result is null,
                "missing protected history did not fail its strict read before prepared-source resolution");
            return;
        }
        if (fault == "none")
        {
            Require(reachedDriver && heldAtDriver && fixture.Driver.ProcessAttempts == 1 && resolving == 1 && announcements == 1 && observations == 2 &&
                verifiedFiles == 315 && publication.Version.Number == 4 && publication.Version.Files.Count == 315 &&
                publication.Version.ParentHash == publication.Branch.Offer.Version.VersionHash &&
                (await fixture.Manager.VerifiedLocalContinuationAsync(fixture.ProfileId, prepared.LocalProfileId.Value, default))?
                    .VersionHash == publication.Version.VersionHash,
                "exact published four did not reach only the final fake launch boundary without retaining its 315-file cache: " +
                $"code={result?.Code ?? "FakeBoundary"}; driverReached={reachedDriver}; held={heldAtDriver}; " +
                $"driverAttempts={fixture.Driver.ProcessAttempts}; preparedResolutions={resolving}; legacyResolutions={legacyResolutions}; " +
                $"announcements={announcements}; observations={observations}; verifiedFiles={verifiedFiles}; " +
                $"version={publication.Version.Number}; files={publication.Version.Files.Count}; " +
                $"parentMatch={publication.Version.ParentHash == publication.Branch.Offer.Version.VersionHash}");
            heldAtDriver = false;
            var ownerResult = await new SimpleOwnerHostingService(fixture.Data, fixture.Manager, peers,
                () => throw new InvalidOperationException("A prepared local Start must not repeat listener setup."))
                .StartAsync(prepared.LocalProfileId.Value, default);
            // This service maps the refusing fake driver's exception to its
            // normal typed failure. Actual boundary entry is the attempt count
            // and held file lease, never an inferred successful process.
            Require(ownerResult is { Ok: false, Code: "SharedHostingReviewRequired" } && heldAtDriver && fixture.Driver.ProcessAttempts == 2 &&
                !Directory.Exists(cacheRoot) && fixture.Data.LoadRuns().Count == 0,
                "the Host screen did not reuse the same exact 315-file prepared publication through the common launch gate: " +
                $"code={ownerResult.Code}; held={heldAtDriver}; " +
                $"attempts={fixture.Driver.ProcessAttempts}; cache={Directory.Exists(cacheRoot)}");
            using var ownerReleased = new FileStream(Path.Combine(publication.Vault, publication.Version.VersionHash,
                SharedWorldService.PayloadDirectory, "world.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(ownerReleased.CanWrite, "the Host screen leaked its immutable prepared-source lease");
        }
        else
        {
            Require(result is { Ok: false } && !reachedDriver && fixture.Driver.ProcessAttempts == 0 &&
                result.Code is "LocalHostingPreparationRequired" or "LatestSaveChanged" or "HostingPermissionRequired" or
                    "LocalConsentRequired" or "ConnectionChanged" or "LocalStoppedProgressNeedsSharing" or "SharingReviewRequired",
                "invalid prepared publication lost its typed blocker or crossed a process boundary: " + fault + ": " + result?.Code);
            if (fault is "newer" or "fork" or "observation")
                Require(result is { Code: "LatestSaveChanged" }, "known changed material evidence did not fence prepared Start: " + fault);
            if (fault == "selection")
                Require(result is { Code: "ConnectionChanged" }, "a selected-connection change did not fence prepared Start");
            if (fault == "observation")
                Require(observations == 1, "changed material evidence did not fail the first final observation");
            if (fault == "late-semantic-setup")
                Require(semanticSetupRejected && result is { Code: "LocalHostingPreparationRequired" },
                    "a late semantic setup change lost its independent proof check or typed refusal");
            if (fault is "published-file" or "missing-published" or "working-file" or "setup" or "newer" or "fork" or "consent" or "grant" or "revoked" or "expired")
            {
                var ownerRefused = await new SimpleOwnerHostingService(fixture.Data, fixture.Manager, peers,
                    () => throw new InvalidOperationException("Rejected prepared Start repeated setup."))
                    .StartAsync(prepared.LocalProfileId.Value, default);
                Require(!ownerRefused.Ok && fixture.Driver.ProcessAttempts == 0 && !Directory.Exists(cacheRoot),
                    "the Host screen bypassed the prepared-source guard or copied a cache: " + fault);
            }
        }
    }

    private static async Task PreparedPublicationLifetimeAsync(string root)
    {
        using var fixture = new Fixture(root, granted: true, automaticReceiving: true, initialVersion: 3);
        var prepared = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(prepared.ReadyForManualStart && prepared.LocalProfileId is not null && prepared.BranchHash is not null,
            "the generated lease-lifetime preparation was not ready");
        var publication = fixture.PublishStoppedContinuation(prepared);
        using var peers = fixture.PreparedPublicationPeers();
        await peers.RefreshMembershipsAsync(false, default);
        fixture.PublishPreparedClaim(publication.Branch, publication.Version);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var propagated = false;
            try
            {
                _ = await peers.ResolveLatestPreparedStartSourceAsync(fixture.PublicationRoot,
                prepared.LocalProfileId.Value, prepared.BranchHash, canceled.Token);
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { propagated = true; }
            Require(propagated, "prepared-source discovery swallowed caller cancellation");
        }
        using (var canceledDuringHash = new CancellationTokenSource())
        {
            var propagated = false;
            try
            {
                using var unexpected = WorldPeerPreparedStartSource.Open(fixture.Data, fixture.PublicationRoot,
                    prepared.LocalProfileId.Value, prepared.BranchHash, canceledDuringHash.Token, progress =>
                    {
                        if (progress.CompletedFiles is >= 1) canceledDuringHash.Cancel();
                    });
            }
            catch (OperationCanceledException) when (canceledDuringHash.IsCancellationRequested) { propagated = true; }
            Require(propagated, "cancellation while hashing the prepared source was not propagated");
        }
        var latest = await peers.ResolveLatestPreparedStartSourceAsync(fixture.PublicationRoot,
            prepared.LocalProfileId.Value, prepared.BranchHash, default);
        Require(latest.Ok && latest.Source is not null, "the canceled held source could not be opened again");
        latest.Source.Dispose();
        fixture.Manager.SharedHostingObservation = peers.LatestObservationHash;
        await fixture.Manager.RecordHostingObservationAsync(prepared.LocalProfileId.Value, latest, () => { }, default);
        var refused = await fixture.Manager.StartSeparateCopyAsync(prepared.LocalProfileId.Value, prepared.BranchHash, default);
        Require(!refused.Ok && refused.Code == "LatestSaveChanged" && fixture.Driver.ProcessAttempts == 0 &&
            fixture.Data.LoadRuns().Count == 0 && !Directory.Exists(Path.Combine(fixture.Data.RootPath, "world-peer-recovery-sources")),
            "an unconsumed observation reused a disposed source lease or cancellation created a cache/process");
        using var released = new FileStream(Path.Combine(publication.Vault, publication.Version.VersionHash,
            SharedWorldService.PayloadDirectory, "world.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Require(released.CanWrite, "cancellation or a disposed observation leaked its source file handles");
    }

    private sealed record PreparedPublication(ServerProfile Profile, WorldSeparateCopyBranch Branch,
        SharedWorldVersion Version, string Vault);

    private sealed partial class Fixture
    {
        internal void MarkOriginalHostConnected()
        {
            using var connection = friends.RetainLocalHostingConnection();
            var link = connection!.Link;
            typeof(FriendLink).GetField("view", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(link, link.View() with
                {
                    State = "Connected",
                    Profiles = [new(ProfileId, "Generated prepared world", "Offline", null, Kind: GameKinds.Fixture)],
                    HostCapabilities = [CompanionProtocol.SharedWorldsCapability, CompanionProtocol.SharedWorldPeersCapability]
                });
        }

        internal SharedWorldPeerRoot PublicationRoot => new(Public(owner), ProfileId, GroupId);
        internal ECDsa PreparedMemberKey => member;
        private SharedWorldRoster PreparedRoster => JsonSerializer.Deserialize<SharedWorldRoster>(
            Data.LoadProtected($"shared-world-roster-{DeviceId:N}-{ProfileId:N}.protected")!, Json)!;

        internal PreparedPublication PublishStoppedContinuation(LocalHostingStatus prepared)
        {
            var store = new SharedWorldSeparateCopyStore(Data);
            var branch = store.Read(ProfileId).Single(item => item.BranchHash == prepared.BranchHash);
            Require(branch.Offer.Version.Number == 3, "the generated source was not the signed third version");
            var profile = Data.LoadSettings().Profiles.Single(item => item.Id == prepared.LocalProfileId);
            var bytes = Encoding.UTF8.GetBytes("strict synthetic stopped progress four");
            foreach (var file in branch.Offer.Version.Files)
                File.WriteAllBytes(SharedWorldService.SafeChild(profile.WorldDirectory, file.Path), bytes);
            var state = SharedWorldBranchLiveSource.ReadState(Data, profile.Id)!;
            Data.SaveProtected(SharedWorldBranchLiveSource.StateName(profile.Id),
                JsonSerializer.SerializeToUtf8Bytes(state with { Started = true }));
            profile.Backups.MinimumFreeSpaceMb = 0;
            var backups = new WorldBackupService(Data, TimeProvider.System,
                games: new GameServerRegistry([Driver], PortProbeMode.ObserveOnly));
            var backup = backups.Create(profile, BackupKinds.Rolling);
            Require(backup is { Ok: true, Backup.SetupIncluded: true }, "the generated stopped backup failed: " + backup.Code);
            var published = store.PublishAfterStop(branch, profile, backup.Backup.Id, backups);
            Require(published is { Ok: true, Version.Number: 4 }, "the generated strict fourth publication failed");
            return new(profile, branch, published.Version, store.PublishedRoot(branch));
        }

        internal WorldPeerService PreparedPublicationPeers() => new(Data, Manager, friends,
            new PairingService(Data), (_, _) => throw new HttpRequestException("Generated unavailable peer."));

        internal void PublishPreparedClaim(WorldSeparateCopyBranch branch, SharedWorldVersion version)
        {
            var roster = PreparedRoster;
            var store = new SharedWorldPeerStore(Data, PublicationRoot, new SharedWorldPeerTrust(PublicationRoot,
                (id, key) => SharedWorldPeerTrust.AccessFromRoster(PublicationRoot, roster, id, key)));
            store.Publish(DeviceId, member, [], [], [new(branch.BranchHash, branch.Offer.Version.VersionHash,
                version.VersionHash, version.ParentHash, version.Number)], [version.VersionHash]);
        }

        internal void ChangePreparedCopyObservation(WorldSeparateCopyBranch branch, SharedWorldVersion version)
        {
            var roster = PreparedRoster;
            var store = new SharedWorldPeerStore(Data, PublicationRoot, new SharedWorldPeerTrust(PublicationRoot,
                (id, key) => SharedWorldPeerTrust.AccessFromRoster(PublicationRoot, roster, id, key)));
            var extra = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("new generated material copy claim")));
            store.Publish(DeviceId, member, [], [], [new(branch.BranchHash, branch.Offer.Version.VersionHash,
                version.VersionHash, version.ParentHash, version.Number)], [version.VersionHash, extra]);
        }

        internal async Task ChangePreparedGrantAsync(string fault)
        {
            var old = PreparedRoster;
            var members = old.Members.Select(item => item with
            {
                Revoked = fault == "revoked",
                AccessExpiresUtc = fault == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
                Grants = fault == "grant" ? item.Grants with { EligibleHost = false } : item.Grants
            }).ToArray();
            var draft = old with
            {
                Epoch = old.Epoch + 1,
                Revision = old.Revision + 1,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(old),
                Members = members,
                OwnerLocalBaselineMembers = members,
                Signature = ""
            };
            var denied = draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            var chain = new SharedWorldRosterChainStore(Data);
            if (!chain.HasState(ProfileId)) chain.Append(old, PublicationRoot.OwnerPublicKey);
            chain.Append(denied, PublicationRoot.OwnerPublicKey);
            await friends.ApplyPeerRosterAsync(denied, default);
        }

        internal void ChangePreparedSetup(Guid localId)
        {
            var state = SharedWorldBranchLiveSource.ReadState(Data, localId)!;
            Data.SaveProtected(SharedWorldBranchLiveSource.StateName(localId),
                JsonSerializer.SerializeToUtf8Bytes(state with { SetupHash = new string('A', 64) }));
        }

        internal void ChangePreparedSemanticSetup(Guid localId)
        {
            var before = SharedWorldBranchLiveSource.ReadState(Data, localId)!.SetupHash;
            var settings = Data.LoadSettings();
            var profile = settings.Profiles.Single(item => item.Id == localId);
            profile.Crossplay = !profile.Crossplay;
            Data.SaveSettings(settings);
            Require(SharedWorldBranchLiveSource.ReadState(Data, localId)!.SetupHash == before,
                "the semantic setup fixture accidentally changed the old protected setup guard");
        }

        internal void RemovePreparedBranchProof()
        {
            var path = Path.Combine(Data.RootPath, $"shared-world-separate-{ProfileId:N}.protected");
            Require(File.Exists(path), "the exact generated branch proof filename changed");
            File.Delete(path);
        }

        internal void SelectDifferentPreparedConnection()
        {
            Require(alternateConnectionId is { } id && friends.Select(id).Ok,
                "the generated alternate saved connection could not be selected");
        }

        internal FriendLocalHostingService WithPreparedPublicationSource(
            Func<SharedWorldPeerRoot, Guid, string, CancellationToken, Action<WorldPeerSourceProgress>?, Task<WorldPeerPreparedStartResult>> preparedSource,
            Func<SharedWorldPeerRoot, CancellationToken, Task<WorldPeerActionResult>> announce,
            Func<SharedWorldPeerRoot, CancellationToken, Task<WorldPeerLatestSourceResult>> legacySource) =>
            new(Data, friends, Manager, _ => [Path.Combine(Data.RootPath, "never-run.synthetic")], announceHosting: announce,
                latestSource: legacySource,
                preparedStartSource: preparedSource);
    }
}
