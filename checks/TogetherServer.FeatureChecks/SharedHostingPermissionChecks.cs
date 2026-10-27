using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Protected synthetic files and a driver that refuses every process action.
// These checks never launch an app, game, listener, browser or native dialog.
internal static class SharedHostingPermissionChecks
{
    private static readonly SharedWorldGrants Hosting = new(Receive: true, EligibleHost: true);
    private static readonly SharedWorldGrants AutomaticHosting = new(Receive: true, EligibleHost: true, Reshare: true);

    internal static async Task RetryPublicationAsync(string root)
    {
        foreach (var request in new[] { "{}", " { } " })
            FriendLocalHostingRoutes.ValidatePublicationRequest(System.Text.Encoding.UTF8.GetBytes(request));
        foreach (var request in new[] { "", "[]", "null", "{\"backupId\":\"chosen-by-caller\"}",
            "{\"path\":\"C:/private\"}", "{\"command\":\"run\"}", new string(' ', 65) })
        {
            var rejected = false;
            try { FriendLocalHostingRoutes.ValidatePublicationRequest(System.Text.Encoding.UTF8.GetBytes(request)); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { rejected = true; }
            Require(rejected, "backup publication accepted a caller-selected file, ID, command or malformed body");
        }

        using (var fixture = new Fixture(root, "retry-default-valheim", profile => profile.Backups.MinimumFreeSpaceMb = 0,
            kind: GameKinds.Valheim))
        {
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok,
                "could not enable the synthetic Valheim source");
            var profile = fixture.Current().Profiles[0];
            Directory.CreateDirectory(profile.WorldDirectory);
            var worldPath = Path.Combine(profile.WorldDirectory, "SyntheticWorld.db");
            var savedPayload = System.Text.Encoding.UTF8.GetBytes("completed synthetic world before the failed publication");
            File.WriteAllBytes(worldPath, savedPayload);
            File.WriteAllText(Path.Combine(profile.WorldDirectory, "permittedlist.txt"),
                "// List of permitted players ID ONE per line\r\n");
            var backups = new WorldBackupService(fixture.Data, fixture.Clock);
            var completed = backups.Create(profile, BackupKinds.Rolling);
            Require(completed is { Ok: true, Backup.SetupIncluded: true }, "the existing sealed Valheim backup was not created");
            var setupPath = Path.Combine(fixture.Data.BackupsRoot, profile.Id.ToString("N"),
                completed.Backup!.Id.ToString("N") + ".backup", "setup.protected");
            var originalSetup = File.ReadAllBytes(setupPath);
            var errorPath = Path.Combine(fixture.Data.RootPath, "shared-worlds", profile.Id.ToString("N"), "last-error.txt");
            File.WriteAllText(errorPath, "The latest completed backup could not be published. Review free space and backup integrity.");
            fixture.Clock.Now += TimeSpan.FromMinutes(1);
            File.WriteAllText(worldPath, "current world differs from the sealed backup");
            Require(backups.Create(profile, BackupKinds.Manual).Ok, "could not create the newer manual backup exclusion case");
            var catalogBefore = File.ReadAllBytes(Path.Combine(fixture.Data.RootPath, "backups.json"));
            var liveBefore = File.ReadAllBytes(worldPath);
            var settingsBefore = JsonSerializer.SerializeToUtf8Bytes(fixture.Current());
            var pairingBefore = File.ReadAllBytes(Path.Combine(fixture.Data.RootPath, "pairing-state.protected"));

            var published = await fixture.Manager.RetrySharedSavePublicationAsync(profile.Id, CancellationToken.None);
            Require(published is { Ok: true, Version: { Number: 1 } } &&
                published.Version.BackupId == completed.Backup.Id &&
                published.Version.PortableSetup.Allowlist is { Count: 0 } &&
                SharedWorldService.VerifySignature(published.Version),
                "retry did not publish the existing signed rolling backup with its default Valheim comment ignored");
            var shared = new SharedWorldService(fixture.Data, backups);
            var index = Array.FindIndex(published.Version!.Files.ToArray(), file => file.Path == "SyntheticWorld.db");
            Require(index >= 0 && shared.ReadChunk(published.Version, index, 0).AsSpan().SequenceEqual(savedPayload) &&
                !File.Exists(errorPath) &&
                File.ReadAllBytes(setupPath).AsSpan().SequenceEqual(originalSetup) &&
                File.ReadAllBytes(Path.Combine(fixture.Data.RootPath, "backups.json")).AsSpan().SequenceEqual(catalogBefore) &&
                File.ReadAllBytes(worldPath).AsSpan().SequenceEqual(liveBefore) &&
                JsonSerializer.SerializeToUtf8Bytes(fixture.Current()).AsSpan().SequenceEqual(settingsBefore) &&
                File.ReadAllBytes(Path.Combine(fixture.Data.RootPath, "pairing-state.protected")).AsSpan().SequenceEqual(pairingBefore) &&
                fixture.Driver.Actions == 0 && fixture.Data.LoadRuns().Count == 0 && shared.Status(profile).ConfirmedCopies == 0,
                "retry changed a backup, live world, settings or grants, invoked a driver, or invented a receiving-PC confirmation");
            File.WriteAllText(errorPath, "old publication notice retained after a successful copy");
            var repeated = await fixture.Manager.RetrySharedSavePublicationAsync(profile.Id, CancellationToken.None);
            Require(repeated.Ok && repeated.Version?.VersionHash == published.Version.VersionHash &&
                shared.Status(profile).Error is null && !File.Exists(errorPath),
                "an exact repeated retry created another version or retained a stale publication error");
            var publishedWorld = Path.Combine(fixture.Data.RootPath, "shared-worlds", profile.Id.ToString("N"),
                published.Version.GroupId.ToString("N"), "1", "payload", "SyntheticWorld.db");
            File.WriteAllText(publishedWorld, "damaged published bytes");
            File.WriteAllText(errorPath, "unresolved publication notice");
            var damagedPublished = await fixture.Manager.RetrySharedSavePublicationAsync(profile.Id, CancellationToken.None);
            Require(!damagedPublished.Ok && shared.Status(profile).Error is not null && File.Exists(errorPath),
                "an idempotent retry cleared the error without verifying the published payload");
            File.WriteAllBytes(publishedWorld, savedPayload);
            Require((await fixture.Manager.RetrySharedSavePublicationAsync(profile.Id, CancellationToken.None)).Ok &&
                shared.Status(profile).Error is null, "retry did not clear the error after the exact published bytes verified again");

            fixture.Clock.Now += TimeSpan.FromMinutes(1);
            var damaged = backups.Create(profile, BackupKinds.Rolling);
            Require(damaged is { Ok: true, Backup.SetupIncluded: true }, "could not create the later damaged-backup case");
            File.AppendAllText(Path.Combine(fixture.Data.BackupsRoot, profile.Id.ToString("N"),
                damaged.Backup!.Id.ToString("N") + ".backup", "payload", "SyntheticWorld.db"), "tampered");
            var refused = await fixture.Manager.RetrySharedSavePublicationAsync(profile.Id, CancellationToken.None);
            var after = shared.Status(profile);
            Require(!refused.Ok && after.Latest?.VersionHash == published.Version.VersionHash &&
                after.Error?.Contains("could not be verified", StringComparison.Ordinal) == true &&
                after.Error?.Contains("free space", StringComparison.Ordinal) != true && fixture.Driver.Actions == 0,
                "retry published a damaged newer backup, fell back to another copy, lost the prior shared save, or blamed free space");
        }

        using (var fixture = new Fixture(root, "retry-prerequisites"))
        {
            Require((await fixture.Manager.RetrySharedSavePublicationAsync(fixture.ProfileId, CancellationToken.None)).Code == "SharingOff",
                "retry enabled sharing without owner approval");
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok,
                "could not enable the retry prerequisite source");
            Require((await fixture.Manager.RetrySharedSavePublicationAsync(fixture.ProfileId, CancellationToken.None)).Code == "NoCompletedBackup",
                "retry invented a completed backup or attempted to read the live world");
            fixture.Pairing.RequireSharedRosterPublication(fixture.ProfileId);
            Require((await fixture.Manager.RetrySharedSavePublicationAsync(fixture.ProfileId, CancellationToken.None)).Code == "SharingAccessNeedsReview" &&
                fixture.Driver.Actions == 0 && new SharedWorldService(fixture.Data, new WorldBackupService(fixture.Data, fixture.Clock))
                    .Status(fixture.Current().Profiles[0]).Latest is null,
                "retry bypassed unresolved permissions or invoked a process");
        }

        using (var fixture = new Fixture(root, "retry-inherited", profile =>
            { profile.SharedSavesEnabled = true; profile.SeparateCopySourceProfileId = Guid.NewGuid(); }))
            Require((await fixture.Manager.RetrySharedSavePublicationAsync(fixture.ProfileId, CancellationToken.None)).Code == "OwnerSharingRequired" &&
                fixture.Driver.Actions == 0, "an inherited Host retried original-owner publication");

        using (var fixture = new Fixture(root, "retry-completion-order", profile => profile.Backups.MinimumFreeSpaceMb = 0))
        {
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok,
                "could not enable the backup completion-order case");
            var profile = fixture.Current().Profiles[0];
            Directory.CreateDirectory(profile.WorldDirectory);
            var worldPath = Path.Combine(profile.WorldDirectory, "world.dat");
            var backups = new WorldBackupService(fixture.Data, fixture.Clock);
            var shared = new SharedWorldService(fixture.Data, backups);
            SharedWorldVersion? expected = null;
            for (var number = 1; number <= 3; number++)
            {
                if (number == 3) fixture.Clock.Now -= TimeSpan.FromMinutes(1);
                File.WriteAllText(worldPath, "completion " + number);
                var completed = backups.Create(profile, BackupKinds.Rolling);
                Require(completed is { Ok: true, Backup.SetupIncluded: true }, "could not create the ordered backup");
                var normalPublication = shared.PublishAfterStop(profile, completed.Backup!.Id);
                Require(normalPublication.Ok && normalPublication.Version?.Number == number,
                    "normal post-Stop publication could not establish the newest ordered save");
                expected = normalPublication.Version;
                var retry = await fixture.Manager.RetrySharedSavePublicationAsync(profile.Id, CancellationToken.None);
                Require(retry.Ok && retry.Version?.VersionHash == expected!.VersionHash &&
                    retry.Version.Number == number && fixture.Driver.Actions == 0,
                    "retry republished older progress when backup clocks tied or moved backward");
            }
            var latest = backups.CompletedRollingForSharing(profile).Last();
            var catalog = fixture.Data.LoadBackupCatalog();
            catalog.Records.RemoveAll(item => item.Id != latest.Id);
            fixture.Data.SaveBackupCatalog(catalog);
            fixture.Clock.Now -= TimeSpan.FromMinutes(1);
            File.WriteAllText(worldPath, "later completion with missing published baseline");
            Require(backups.Create(profile, BackupKinds.Rolling).Ok, "could not create the unproven-baseline backup");
            catalog = fixture.Data.LoadBackupCatalog();
            catalog.Records.RemoveAll(item => item.Id == latest.Id);
            fixture.Data.SaveBackupCatalog(catalog);
            var refused = await fixture.Manager.RetrySharedSavePublicationAsync(profile.Id, CancellationToken.None);
            Require(!refused.Ok && refused.Code == "SharedSaveOrderNeedsReview" &&
                shared.Status(profile).Latest?.VersionHash == expected!.VersionHash,
                "retry replaced a shared head without a retained completion-order baseline");
        }

        foreach (var retainRecoveredBackup in new[] { true, false })
        {
            using var fixture = new Fixture(root, "retry-interrupted-publication-" + retainRecoveredBackup,
                profile => profile.Backups.MinimumFreeSpaceMb = 0);
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok,
                "could not enable the interrupted-publication source");
            var profile = fixture.Current().Profiles[0];
            Directory.CreateDirectory(profile.WorldDirectory);
            var worldPath = Path.Combine(profile.WorldDirectory, "world.dat");
            var backups = new WorldBackupService(fixture.Data, fixture.Clock);
            var shared = new SharedWorldService(fixture.Data, backups);
            File.WriteAllText(worldPath, "first completed save");
            var first = backups.Create(profile, BackupKinds.Rolling);
            var firstVersion = shared.PublishAfterStop(profile, first.Backup!.Id).Version ??
                throw new InvalidOperationException("could not publish the first interrupted-retry baseline");
            var pointerPath = Path.Combine(fixture.Data.RootPath, "shared-worlds", profile.Id.ToString("N"), "latest.json");
            var firstPointer = File.ReadAllBytes(pointerPath);
            File.WriteAllText(worldPath, "newer sealed save before the pointer update");
            var next = backups.Create(profile, BackupKinds.Rolling);
            var nextVersion = shared.PublishAfterStop(profile, next.Backup!.Id).Version ??
                throw new InvalidOperationException("could not establish the sealed next version");
            // Model a completed version directory whose final pointer write was
            // interrupted. The signed payload remains exactly as published.
            File.WriteAllBytes(pointerPath, firstPointer);
            if (!retainRecoveredBackup)
            {
                var catalog = fixture.Data.LoadBackupCatalog();
                catalog.Records.RemoveAll(item => item.Id == next.Backup.Id);
                fixture.Data.SaveBackupCatalog(catalog);
            }
            var result = await fixture.Manager.RetrySharedSavePublicationAsync(profile.Id, CancellationToken.None);
            var recovered = shared.Status(profile).Latest;
            Require(result.Ok == retainRecoveredBackup && recovered?.VersionHash == nextVersion.VersionHash &&
                recovered.Number == 2 && recovered.BackupId != firstVersion.BackupId && fixture.Driver.Actions == 0,
                "retry replaced a reconciled newer save with the older backup selected before recovery");
        }
    }

    internal static async Task RunAsync(string root)
    {
        await LateEnrollmentAfterUnassignmentAsync(root);
        await SimpleServerPolicyAsync(root);
        await LegacyJournalRepairAsync(root);
        using (var fixture = new Fixture(root, "current-settings"))
        {
            Require(fixture.Pairing.SetSharedWorldGrants(fixture.DeviceId, fixture.ProfileId,
                new(RecoveryVoter: true)).Ok, "could not seed an independent advanced grant");
            var gate = Field<SemaphoreSlim>(fixture.Manager, "gate");
            await gate.WaitAsync();
            Task<ActionResult> edit;
            Task<PairingDecision> permission;
            var newer = fixture.Current();
            newer.IdleMinutes = 71;
            newer.Profiles[0].Name = "Newer target name";
            newer.Profiles[0].Backups.RetentionCount = 17;
            newer.Profiles[0].Backups.MinimumFreeSpaceMb = 3072;
            newer.Profiles[0].CrashRecovery.Enabled = true;
            newer.Profiles[1].Name = "Newer sibling name";
            try
            {
                edit = fixture.Manager.UpdateSettingsAsync(newer);
                permission = fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId,
                    fixture.ProfileId, Hosting with { RecoveryVoter = true });
                Require(!edit.IsCompleted && !permission.IsCompleted,
                    "save permissions did not wait on the lifecycle gate");
            }
            finally { gate.Release(); }
            Require((await edit).Ok && (await permission).Ok, "one-action save hosting did not accept current settings");
            var expected = Clone(newer);
            expected.Profiles[0].Backups.Enabled = true;
            expected.Profiles[0].SharedSavesEnabled = true;
            Require(Same(fixture.Current(), expected),
                "one-action hosting replaced newer settings, retention, reserve, recovery, sibling or listener policy");
            Require(fixture.Grants() == (Hosting with { RecoveryVoter = true }) &&
                !fixture.Current().CompanionListeningEnabled && !fixture.Current().RemoteControlsEnabled &&
                fixture.Data.LoadRuns().Count == 0 && fixture.Driver.Actions == 0,
                "one-action hosting granted unrelated rights, enabled a listener or invoked a game action");
            var roster = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
            Require(roster is not null && roster.Members.Single(member => member.DeviceId == fixture.DeviceId).Grants ==
                (Hosting with { RecoveryVoter = true }) && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId) &&
                (await fixture.Manager.SharedWorldStatusAsync(fixture.ProfileId)).Latest is null,
                "the hosting permission was not signed or created an unexpected playable save");
        }

        using (var fixture = new Fixture(root, "withdraw-while-off"))
        {
            Require((await fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId, fixture.ProfileId,
                Hosting with { Reshare = true })).Ok, "could not prepare hosting for withdrawal");
            var paused = fixture.Current(); paused.Profiles[0].SharedSavesEnabled = false;
            Require((await fixture.Manager.UpdateSettingsAsync(paused)).Ok, "could not pause synthetic sharing");
            var before = fixture.Current();
            var priorRoster = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId) ??
                throw new InvalidOperationException("the prepared signed roster is missing");
            var withdrawn = await fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId,
                fixture.ProfileId, Hosting with { EligibleHost = false, Reshare = true });
            var afterRoster = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
            Require(withdrawn.Ok && Same(fixture.Current(), before) &&
                fixture.Grants() == (Hosting with { EligibleHost = false, Reshare = true }) &&
                afterRoster is not null && afterRoster.Members.Single(member => member.DeviceId == fixture.DeviceId).Grants == fixture.Grants() &&
                afterRoster.GroupId == priorRoster.GroupId && afterRoster.OwnerPublicKey == priorRoster.OwnerPublicKey &&
                afterRoster.Revision > priorRoster.Revision && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId),
                "hosting withdrawal resumed sharing or removed receive/forwarding rights");
            var receiveOff = await fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId,
                fixture.ProfileId, new(Reshare: true));
            Require(receiveOff.Ok && Same(fixture.Current(), before) && fixture.Grants() == new SharedWorldGrants(Reshare: true),
                "receive withdrawal enabled sharing or changed unrelated permissions");
            var receiveRoster = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
            Require(receiveRoster is not null && receiveRoster.Members.Single(member => member.DeviceId == fixture.DeviceId).Grants == fixture.Grants() &&
                receiveRoster.GroupId == priorRoster.GroupId && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId) &&
                (await fixture.Manager.SharedWorldStatusAsync(fixture.ProfileId)).Latest is null &&
                !fixture.Current().CompanionListeningEnabled && fixture.Data.LoadRuns().Count == 0,
                "paused receive withdrawal did not update signed permissions or published/started a world");
        }

        using (var fixture = new Fixture(root, "receive-only"))
        {
            var before = fixture.Current();
            var result = await fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId,
                fixture.ProfileId, new(Receive: true));
            Require(result.Ok && fixture.Grants() == new SharedWorldGrants(Receive: true) && Same(fixture.Current(), before),
                "a receive-only permission silently enabled hosting or changed sharing policy");
        }

        using (var pending = new Fixture(root, "pending-signing", configureDevice: device => device.SharedWorldPublicKey = null))
        {
            Require((await pending.Manager.SetSharedWorldGrantsAsync(pending.DeviceId, pending.ProfileId, Hosting)).Ok,
                "a pending signing identity lost the owner's chosen hosting permission");
            var roster = await pending.Manager.SharedWorldRosterAsync(pending.ProfileId);
            Require(pending.Grants() == Hosting && roster is not null && roster.Members.All(member => member.DeviceId != pending.DeviceId) &&
                !pending.Pairing.SharedRosterDirty(pending.ProfileId),
                "a pending identity was represented as an enrolled signed member");
        }

        using (var changed = new Fixture(root, "source-changed-while-paused"))
        {
            Require((await changed.Manager.SetSharedWorldGrantsAsync(changed.DeviceId, changed.ProfileId, Hosting)).Ok,
                "could not seed source identity review");
            var prior = await changed.Manager.SharedWorldRosterAsync(changed.ProfileId) ??
                throw new InvalidOperationException("the source identity was not signed");
            var originalProfile = Clone(changed.Current()).Profiles[0];
            var next = changed.Current();
            next.Profiles[0].WorldId = "DifferentSyntheticWorld";
            Require((await changed.Manager.UpdateSettingsAsync(next)).Ok && !changed.Current().Profiles[0].SharedSavesEnabled,
                "the synthetic owner source change was not fenced from sharing");
            var before = changed.Current();
            var result = await changed.Manager.SetSharedWorldGrantsAsync(changed.DeviceId, changed.ProfileId,
                Hosting with { EligibleHost = false });
            Require(!result.Ok && result.Code == "SharedHostingPermissionUnconfirmed" &&
                Same(changed.Current(), before) && changed.Grants() == Hosting,
                "a paused permission edit reviewed a different save source or rewrote its group");
            var old = new SharedWorldService(changed.Data, new WorldBackupService(changed.Data, TimeProvider.System))
                .ReadRoster(originalProfile);
            Require(old is not null && old.GroupId == prior.GroupId && old.Signature == prior.Signature && old.Revision == prior.Revision,
                "a paused permission edit replaced the earlier signed roster or rollback clock");
        }

        using (var unknown = new Fixture(root, "unknown-device"))
            await RejectsAsync(unknown, "Unauthorized", deviceId: Guid.NewGuid());
        using (var unknown = new Fixture(root, "unknown-profile"))
            await RejectsAsync(unknown, "UnknownProfile", profileId: Guid.NewGuid());
        using (var pending = new Fixture(root, "pending-device", configureDevice: device => device.ApprovalPending = true))
            await RejectsAsync(pending, "ApprovalPending");
        using (var revoked = new Fixture(root, "revoked-device", configureDevice: device => device.Revoked = true))
            await RejectsAsync(revoked, "Revoked");
        using (var unassigned = new Fixture(root, "unassigned-device", configureDevice: device => device.AssignedProfileIds = []))
            await RejectsAsync(unassigned, "PermissionDenied");
        using (var expired = new Fixture(root, "expired-access"))
        {
            Require(expired.Pairing.SetAccessExpiry(expired.DeviceId, new(Duration: "OneHour")).Ok,
                "could not seed an owner access deadline");
            expired.Clock.Now = expired.Clock.Now.AddHours(1);
            await RejectsAsync(expired, "AccessExpired");
        }
        using (var trial = new Fixture(root, "rehearsal", profile => profile.WorldLoadRehearsalId = Guid.NewGuid()))
            await RejectsAsync(trial, "WorldLoadCannotShare");
        using (var branch = new Fixture(root, "separate-history", profile =>
        {
            profile.SeparateCopySourceProfileId = Guid.NewGuid();
            profile.SeparateCopyBranchHash = new string('A', 64);
        })) await RejectsAsync(branch, "SeparateCopyCannotShare");
        using (var custom = new Fixture(root, "unsupported", kind: GameKinds.Custom))
            await RejectsAsync(custom, "SharingUnsupported");
        using (var authority = new Fixture(root, "unverified-authority"))
        {
            authority.Data.SaveProtected($"authority-floor-{authority.ProfileId:N}.protected", [1, 2, 3]);
            await RejectsAsync(authority, "SuccessorRosterReadOnly");
        }
        using (var handoff = new Fixture(root, "pending-move"))
        {
            handoff.Data.SaveProtected($"planned-handoff-{handoff.ProfileId:N}.protected", [1, 2, 3]);
            await RejectsAsync(handoff, "PlannedHandoffPending");
        }
        using (var restore = new Fixture(root, "pending-restore"))
        {
            restore.Data.SaveProtected($"setup-restore-pending-{restore.ProfileId:N}.protected", [1, 2, 3]);
            await RejectsAsync(restore, "SetupRestoreRecoveryRequired");
        }
        using (var recovery = new Fixture(root, "data-recovery"))
        {
            recovery.Data.SaveProtected("synthetic-recovery.protected", [1, 2, 3]);
            recovery.Data.QuarantineState("synthetic-recovery.protected", "Synthetic lifecycle recovery", true);
            await RejectsAsync(recovery, "DataRecoveryRequired");
        }

        // Windows file sharing denies the exact synthetic destination replacement
        // while permitting reads. No ACL, real file or desktop state is changed.
        using (var failed = new Fixture(root, "settings-write-failure"))
        {
            var before = failed.Current();
            PairingDecision result;
            using (var hold = new FileStream(Path.Combine(failed.Data.RootPath, "host.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
                result = await failed.Manager.SetSharedWorldGrantsAsync(failed.DeviceId, failed.ProfileId, Hosting);
            Require(!result.Ok && result.Code == "SharedHostingPermissionUnconfirmed" &&
                Same(failed.Current(), before) && failed.Grants() == new SharedWorldGrants(),
                "a failed prerequisite save changed settings or granted hosting");
        }
        using (var failed = new Fixture(root, "pairing-write-failure"))
        {
            PairingDecision result;
            using (var hold = new FileStream(Path.Combine(failed.Data.RootPath, "pairing-state.protected"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
                result = await failed.Manager.SetSharedWorldGrantsAsync(failed.DeviceId, failed.ProfileId, Hosting);
            Require(!result.Ok && result.Code == "SharedHostingPermissionUnconfirmed" &&
                failed.Current().Profiles[0].Backups.Enabled && failed.Current().Profiles[0].SharedSavesEnabled &&
                failed.Grants() == new SharedWorldGrants() &&
                !failed.Pairing.TryGetActiveDevice(failed.DeviceId, out _) && failed.Driver.Actions == 0,
                "a failed grant persistence leaked new hosting access or concealed partial prerequisite preparation");
        }
    }

    private static async Task LateEnrollmentAfterUnassignmentAsync(string root)
    {
        using var fixture = new Fixture(root, "late-enrollment-after-unassignment",
            profile => profile.Backups.MinimumFreeSpaceMb = 0,
            device => device.SharedWorldPublicKey = null);
        var retainedPeer = fixture.AddPeer();
        var joinId = fixture.Current().Profiles[1].Id;
        Require(fixture.Pairing.SetServerAccess(fixture.DeviceId, [joinId], null,
            [fixture.ProfileId, joinId]).Ok, "could not reassign the pending-key PC to its Join server");
        Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok,
            "could not enable the original source after its pending-key PC was unassigned");
        var before = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId) ??
            throw new InvalidOperationException("the original source roster is missing");
        Require(before.Members.Count == 1 && before.Members.Single().DeviceId == retainedPeer &&
            before.Members.Single().Grants == AutomaticHosting && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId) &&
            fixture.Data.LoadPairingState().Devices.Single(device => device.Id == fixture.DeviceId) is
            { SharedWorldPublicKey: null, ProfileId: var original } && original == fixture.ProfileId,
            "the late enrollment fixture lost the original scope, pending key, or retained approved member");

        var profile = fixture.Current().Profiles[0];
        Directory.CreateDirectory(profile.WorldDirectory);
        var payload = System.Text.Encoding.UTF8.GetBytes("completed source save before late Join enrollment");
        File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), payload);
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "version.json"), "legitimate version payload");
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "membership.json"), "legitimate membership payload");
        var backups = new WorldBackupService(fixture.Data, fixture.Clock);
        var backup = backups.Create(profile, BackupKinds.Rolling);
        Require(backup is { Ok: true, Backup.SetupIncluded: true } &&
            backups.Verify(profile, backup.Backup.Id).Ok, "the completed synthetic backup or setup failed verification");
        fixture.Enroll(fixture.DeviceId);
        var shared = new SharedWorldService(fixture.Data, backups);
        var fenced = shared.PublishAfterStop(profile, backup.Backup!.Id);
        Require(fixture.Pairing.SharedRosterDirty(fixture.ProfileId) && !fenced.Ok &&
            fenced.Code == "SharedSavePublishFailed" &&
            fenced.Message.Contains("Signed membership is unresolved", StringComparison.Ordinal),
            "late enrollment did not preserve the original source publication fence");

        // Match the companion enrollment endpoint's actual resealing work. The
        // unassigned original scope can be dirty without appearing in assignments.
        var reseal = fixture.Pairing.SharedEnrollmentProfileIds(fixture.DeviceId, joinId);
        Require(reseal.ToHashSet().SetEquals([fixture.ProfileId, joinId]),
            "late Join enrollment omitted the dirty original source from roster resealing");
        foreach (var id in reseal)
        {
            Require(await fixture.Manager.SharedRosterManagementAvailableAsync(id),
                "late enrollment selected an inherited or unresolved roster for owner signing");
            await fixture.PublishPolicyAsync(id);
        }
        var after = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
        Require(after is not null && SharedWorldRosterTrust.Verify(after) && after.GroupId == before.GroupId &&
            after.OwnerPublicKey == before.OwnerPublicKey && after.Signature == before.Signature &&
            after.Members.SequenceEqual(before.Members) && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId) &&
            !fixture.Pairing.SharedRosterDirty(joinId),
            "resealing changed the source owner/group/grants or left an affected server fenced");
        Require(fixture.Pairing.TryGetActiveDevice(fixture.DeviceId, out var unassigned) && unassigned is not null &&
            unassigned.ProfileId == fixture.ProfileId && unassigned.SharedWorldPublicKey is not null &&
            unassigned.AssignedProfileIds!.SequenceEqual([joinId]) &&
            !fixture.Pairing.CanAccess(unassigned, fixture.ProfileId) && fixture.Pairing.CanAccess(unassigned, joinId) &&
            fixture.Pairing.AuthorizeReceiveSaves(unassigned, fixture.ProfileId, out _).Code == "PermissionDenied" &&
            fixture.Pairing.AuthorizeSharedHistory(unassigned, fixture.ProfileId, out _).Code == "PermissionDenied" &&
            fixture.Grants() == new SharedWorldGrants() && !fixture.Current().Profiles[1].SharedSavesEnabled,
            "resealing exposed source status, manifest/chunk admission, or hosting rights to the unassigned PC");
        var published = shared.PublishAfterStop(profile, backup.Backup.Id);
        Require(published is { Ok: true, Version: { Number: 1 } } &&
            published.Version.BackupId == backup.Backup.Id && published.Version.GroupId == before.GroupId &&
            published.Version.SigningPublicKey == before.OwnerPublicKey && SharedWorldService.VerifySignature(published.Version) &&
            published.Version.Files.Count == 3 && published.Version.Files.Single(file => file.Path == "world.dat").Sha256 ==
                Convert.ToHexString(SHA256.HashData(payload)),
            "the same complete verified backup could not publish after all affected rosters were resealed");
        var worldIndex = Array.FindIndex(published.Version!.Files.ToArray(), file => file.Path == "world.dat");
        Require(shared.ReadChunk(published.Version, worldIndex, 0).AsSpan().SequenceEqual(payload) &&
            fixture.Driver.Actions == 0 && fixture.Data.LoadRuns().Count == 0 &&
            !fixture.Current().CompanionListeningEnabled && !fixture.Current().RemoteControlsEnabled,
            "late enrollment changed the completed payload, invoked a process, or enabled incoming controls");
    }

    private static async Task SimpleServerPolicyAsync(string root)
    {
        using (var fixture = new Fixture(root, "server-policy"))
        {
            var second = fixture.AddPeer();
            var waitingKey = fixture.AddPeer(enroll: false);
            Require(fixture.Pairing.SetSharedWorldGrants(fixture.DeviceId, fixture.ProfileId,
                new(RecoveryVoter: true)).Ok, "could not retain an earlier independent voting grant");
            var before = fixture.Current();
            var enabled = await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None);
            Require(fixture.Data.LoadActivity(100).Any(activity => activity.ProfileId == fixture.ProfileId &&
                activity.Action == "SharedSavesEnabled" && activity.Visibility == ActivityVisibility.AssignedFriends),
                "the shared-hosting switch did not publish its scoped enrollment invalidation after signed policy commit");
            var expected = Clone(before);
            expected.Profiles[0].SharedSavesEnabled = true;
            expected.Profiles[0].Backups.Enabled = true;
            Require(enabled.Ok && Same(fixture.Current(), expected) &&
                fixture.Grants() == (AutomaticHosting with { RecoveryVoter = true }) &&
                fixture.Grants(second) == AutomaticHosting && fixture.Grants(waitingKey) == AutomaticHosting,
                "the server choice did not cover every approved assigned current/pending-key PC or replaced unrelated settings");
            var roster = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
            Require(roster is not null && SharedWorldRosterTrust.Verify(roster) && roster.Members.Count == 2 &&
                roster.Members.All(member => member.Grants.Receive && member.Grants.EligibleHost && member.Grants.Reshare) &&
                roster.Members.All(member => !member.Grants.ManageSharing) &&
                roster.Members.All(member => member.DeviceId != waitingKey) && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId),
                "the server choice forged pending membership, omitted forwarding, or granted management");
            Require((await fixture.Manager.SetSharedWorldGrantsAsync(fixture.DeviceId, fixture.ProfileId, new())).Code ==
                "ServerHostingSettingRequired" &&
                fixture.Pairing.SetReceiveSaves(fixture.DeviceId, fixture.ProfileId, false).Code == "ServerHostingSettingRequired" &&
                fixture.Grants() == (AutomaticHosting with { RecoveryVoter = true }) &&
                (await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId))!.Signature == roster!.Signature,
                "an old per-PC sharing endpoint contradicted the enabled server choice");

            var later = fixture.AddPeer(enroll: false);
            Require(fixture.Grants(later) == AutomaticHosting && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId),
                "later pairing did not inherit the server choice or fenced unchanged signed membership before enrollment");
            fixture.Enroll(later);
            fixture.Enroll(waitingKey);
            await fixture.PublishPolicyAsync();
            var enrolled = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
            Require(enrolled is not null && enrolled.Members.Count == 4 &&
                enrolled.Members.All(member => member.Grants.Receive && member.Grants.EligibleHost && member.Grants.Reshare),
                "later signing enrollment did not automatically complete each PC's hosting membership");

            var disabled = await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, false, CancellationToken.None);
            Require(fixture.Data.LoadActivity(100).Any(activity => activity.ProfileId == fixture.ProfileId &&
                activity.Action == "SharedSavesDisabled" && activity.Visibility == ActivityVisibility.AssignedFriends),
                "disabling shared hosting did not publish its scoped policy invalidation");
            var withdrawn = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
            Require(disabled.Ok && !fixture.Current().Profiles[0].SharedSavesEnabled && fixture.Current().Profiles[0].Backups.Enabled &&
                withdrawn is not null && withdrawn.GroupId == roster!.GroupId && withdrawn.Revision > enrolled!.Revision &&
                withdrawn.Members.All(member => !member.Grants.Receive && !member.Grants.EligibleHost && !member.Grants.Reshare) &&
                fixture.Grants() == new SharedWorldGrants(RecoveryVoter: true) &&
                fixture.Grants(second) == new SharedWorldGrants() && !fixture.Pairing.SharedRosterDirty(fixture.ProfileId),
                "turning the server choice off left signed receiving, forwarding or hosting authority, or disabled backup protection");
            var afterOff = fixture.AddPeer(enroll: false);
            Require(fixture.Grants(afterOff) == new SharedWorldGrants(), "pairing re-enabled shared hosting while the server choice was off");
            var restarted = new PairingService(fixture.Data, fixture.Clock);
            Require(restarted.SharedRosterMembers(fixture.ProfileId).All(member =>
                !member.Grants.Receive && !member.Grants.EligibleHost && !member.Grants.Reshare) &&
                fixture.Driver.Actions == 0 && fixture.Data.LoadRuns().Count == 0 &&
                !fixture.Current().CompanionListeningEnabled && !fixture.Current().RemoteControlsEnabled,
                "restart restored withdrawn authority or the choice changed listener, remote controls or game state");
        }

        using (var fixture = new Fixture(root, "later-approval", configureDevice: device => device.ApprovalPending = true))
        {
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok &&
                fixture.Grants() == new SharedWorldGrants(), "a waiting PC received hosting before approval");
            Require(fixture.Pairing.Approve(fixture.DeviceId).Ok && fixture.Grants() == AutomaticHosting,
                "later owner approval did not inherit the enabled server choice");
            await fixture.PublishPolicyAsync();
        }
        using (var fixture = new Fixture(root, "later-assignment", configureDevice: device => device.AssignedProfileIds = []))
        {
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok &&
                fixture.Grants() == new SharedWorldGrants(), "an unassigned PC received shared hosting");
            var known = fixture.Current().Profiles.Select(profile => profile.Id).ToArray();
            Require(fixture.Pairing.SetServerAccess(fixture.DeviceId, [fixture.ProfileId], null, known).Ok &&
                fixture.Grants() == AutomaticHosting, "later assignment required a second hosting grant");
            await fixture.PublishPolicyAsync();
            Require(fixture.Pairing.SetServerAccess(fixture.DeviceId, [], null, known).Ok,
                "the shared server choice prevented removing one PC's assignment");
            await fixture.PublishPolicyAsync();
            Require((await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId))!.Members.All(member => member.DeviceId != fixture.DeviceId),
                "removing one PC's assignment left signed hosting authority");
        }
        using (var fixture = new Fixture(root, "later-access-extension", configureDevice: device =>
            device.AccessExpiresUtc = new(2029, 1, 1, 0, 0, 0, TimeSpan.Zero)))
        {
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok &&
                fixture.Grants() == new SharedWorldGrants(), "an owner-expired PC received shared hosting");
            Require(fixture.Pairing.SetAccessExpiry(fixture.DeviceId, new(Clear: true)).Ok && fixture.Grants() == AutomaticHosting,
                "clearing the owner access deadline did not restore the enabled server policy");
            await fixture.PublishPolicyAsync();
            Require(fixture.Pairing.Revoke(fixture.DeviceId).Ok, "the shared server choice prevented removing one PC");
            await fixture.PublishPolicyAsync();
            var revoked = (await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId))!.Members.Single();
            Require(revoked.Revoked && !revoked.Grants.Receive && !revoked.Grants.EligibleHost && !revoked.Grants.Reshare,
                "PC revocation retained hosting or forwarding authority");
        }
        using (var fixture = new Fixture(root, "session-credential", configureDevice: device => device.PeerWorldGroupKey = new string('B', 64)))
        {
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok &&
                fixture.Grants() == new SharedWorldGrants() &&
                (await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId))!.Members.Count == 0,
                "the server choice blessed a peer session credential as a second signed member");
        }

        using (var fixture = new Fixture(root, "all-enrolled-assignments", configureDevice: device => device.SharedWorldPublicKey = null))
        {
            var siblingId = fixture.Current().Profiles[1].Id;
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok &&
                (await fixture.Manager.SetSimpleSharedHostingAsync(siblingId, true, CancellationToken.None)).Ok,
                "could not enable the two synthetic shared servers");
            Require(fixture.Pairing.SetServerAccess(fixture.DeviceId, [fixture.ProfileId, siblingId], null,
                [fixture.ProfileId, siblingId]).Ok, "could not assign a pending signing identity to both shared servers");
            foreach (var assigned in new[] { fixture.ProfileId, siblingId }) await fixture.PublishPolicyAsync(assigned);
            fixture.Enroll(fixture.DeviceId);
            var changed = fixture.Pairing.SharedEnrollmentProfileIds(fixture.DeviceId, fixture.ProfileId);
            Require(changed.Count == 2 && changed.All(fixture.Pairing.SharedRosterDirty),
                "enrollment did not retain every assigned server requiring signed publication");
            foreach (var assigned in changed) await fixture.PublishPolicyAsync(assigned);
            foreach (var assigned in changed)
                Require(!fixture.Pairing.SharedRosterDirty(assigned) &&
                    (await fixture.Manager.SharedWorldRosterAsync(assigned))!.Members.Single().Grants == AutomaticHosting,
                    "enrollment through one server left another assigned shared server fenced or ungranted");
        }

        await WithdrawDelegatedHostingAsync(root);

        using (var fixture = new Fixture(root, "server-policy-pairing-failure"))
        {
            SharedWorldResult result;
            using (var hold = new FileStream(Path.Combine(fixture.Data.RootPath, "pairing-state.protected"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
                result = await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None);
            Require(!result.Ok && result.Code == "SharedHostingUnconfirmed" && fixture.Pairing.SharedRosterDirty(fixture.ProfileId) &&
                !fixture.Pairing.TryGetActiveDevice(fixture.DeviceId, out _) && fixture.Driver.Actions == 0,
                "an incomplete server policy write leaked access or removed the signed-publication fence");
        }
        using (var fixture = new Fixture(root, "server-policy-disable-failure"))
        {
            Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, true, CancellationToken.None)).Ok,
                "could not seed the interrupted disable check");
            var roster = (await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId))!;
            var journalFloor = Path.Combine(fixture.Data.RootPath, $"shared-roster-chain-{fixture.ProfileId:N}.protected");
            var path = File.Exists(journalFloor) ? journalFloor : Path.Combine(fixture.Data.RootPath,
                "shared-worlds", fixture.ProfileId.ToString("N"), roster.GroupId.ToString("N") + ".roster.json");
            SharedWorldResult result;
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                result = await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, false, CancellationToken.None);
            Require(!result.Ok && result.Code == "SharedHostingUnconfirmed" &&
                !fixture.Current().Profiles[0].SharedSavesEnabled && fixture.Grants() == new SharedWorldGrants() &&
                fixture.Pairing.SharedRosterDirty(fixture.ProfileId),
                "a failed signed disable retained canonical hosting access or dropped its publication fence");
            await fixture.PublishPolicyAsync();
            Require((await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId))!.Members.All(member =>
                !member.Grants.Receive && !member.Grants.EligibleHost && !member.Grants.Reshare) &&
                !fixture.Pairing.SharedRosterDirty(fixture.ProfileId) && fixture.Driver.Actions == 0,
                "repair of an interrupted disable resumed hosting or kept stale signed authority");
        }
    }

    private static async Task LegacyJournalRepairAsync(string root)
    {
        using var fixture = new Fixture(root, "legacy-journal-repair", profile =>
        { profile.SharedSavesEnabled = true; profile.Backups.Enabled = true; });
        fixture.Pairing.ReconcileSimpleSharedHosting(fixture.ProfileId);
        var profile = fixture.Current().Profiles[0];
        var service = new SharedWorldService(fixture.Data, new WorldBackupService(fixture.Data, fixture.Clock));
        var owner = service.LocalAuthorityPublicKey();
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(fixture.Data.LoadProtected("shared-world-signing-key.protected")!, out _);
        SharedWorldRoster Sign(SharedWorldRoster draft) => draft with
        { Signature = Convert.ToBase64String(key.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
        var old = Sign(new(2, Guid.NewGuid(), profile.Id, 1, 1, true, owner,
            fixture.Pairing.SharedRosterMembers(profile.Id).Select(member => member with { Grants = new() }).ToArray(), ""));
        var latest = Sign(old with { Epoch = 3, Revision = 3, Members = fixture.Pairing.SharedRosterMembers(profile.Id) });
        var directory = Path.Combine(fixture.Data.RootPath, "shared-worlds", profile.Id.ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "source.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { directory = profile.WorldDirectory, game = profile.Kind, worldId = profile.WorldId, groupId = old.GroupId }));
        var snapshotPath = Path.Combine(directory, old.GroupId.ToString("N") + ".roster.json");
        void SaveSnapshot(SharedWorldRoster roster) => File.WriteAllBytes(snapshotPath,
            JsonSerializer.SerializeToUtf8Bytes(roster, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        SaveSnapshot(latest);
        var chain = new SharedWorldRosterChainStore(fixture.Data);
        // This is the installed-state race: the legacy owner exposed revision 3,
        // then a stale peer registry seeded the journal with revision 1.
        chain.Append(old, owner);
        Require(chain.Heads(profile.Id).Single().Signature == old.Signature &&
            chain.Count(profile.Id) == 1 && old.Members.All(member => !member.Grants.EligibleHost),
            "the existing-state regression did not start with stale signed hosting rights");

        foreach (var fault in new[] { "same-epoch", "same-revision", "sibling", "wrong-group", "wrong-profile", "wrong-owner", "bad-signature" })
        {
            using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var candidate = Sign(fault switch
            {
                "same-epoch" => latest with { Epoch = old.Epoch },
                "same-revision" => latest with { Revision = old.Revision },
                "sibling" => old with { OwnerOverride = !old.OwnerOverride },
                "wrong-group" => latest with { GroupId = Guid.NewGuid() },
                "wrong-profile" => latest with { ProfileId = Guid.NewGuid() },
                "wrong-owner" => latest with { OwnerPublicKey = Convert.ToBase64String(wrong.ExportSubjectPublicKeyInfo()) },
                _ => latest
            });
            if (fault == "bad-signature") candidate = candidate with { Signature = new string('A', 88) };
            SaveSnapshot(candidate);
            var rejected = false;
            try { _ = await fixture.Manager.SharedWorldRosterAsync(profile.Id); }
            catch (Exception error) when (error is InvalidDataException or CryptographicException) { rejected = true; }
            Require(rejected && chain.Count(profile.Id) == 1 &&
                chain.Heads(profile.Id).Single().Signature == old.Signature,
                "ambiguous legacy state changed a protected journal: " + fault);
        }

        SaveSnapshot(latest);
        var repaired = await fixture.Manager.SharedWorldRosterAsync(profile.Id);
        Require(repaired?.Signature == latest.Signature && chain.Count(profile.Id) == 2 &&
            chain.Read(profile.Id).Select(item => item.Signature).SequenceEqual([old.Signature, latest.Signature]) &&
            chain.ContainsFloor(profile.Id, new(old.GroupId, old.Epoch, old.Revision, old.Signature)) &&
            chain.ContainsFloor(profile.Id, new(latest.GroupId, latest.Epoch, latest.Revision, latest.Signature)),
            "the same owner's newer legacy snapshot was not retained alongside its earlier floor");
        Require((await fixture.Manager.SharedWorldRosterAsync(profile.Id))!.Signature == latest.Signature &&
            chain.Count(profile.Id) == 2, "legacy repair replay appended or re-signed an accepted snapshot");
        Require((await fixture.Manager.SetSimpleSharedHostingAsync(profile.Id, true, CancellationToken.None)).Ok,
            "the current original-owner policy could not continue repaired legacy history");
        var child = (await fixture.Manager.SharedWorldRosterAsync(profile.Id))!;
        Require(child.Schema == 3 && child.Revision == latest.Revision + 1 &&
            SharedWorldRosterTrust.VerifyRevision(child, latest, owner, fixture.Clock.GetUtcNow(), true) &&
            child.Members.All(member => member.Grants.Receive && member.Grants.EligibleHost && member.Grants.Reshare) &&
            chain.ReadAfter(profile.Id, SharedWorldRosterTrust.Hash(old))!.Select(item => item.Signature)
                .SequenceEqual([latest.Signature, child.Signature]) &&
            chain.ReadAfter(profile.Id, SharedWorldRosterTrust.Hash(latest))!.Single().Signature == child.Signature &&
            chain.ReadAfter(profile.Id, SharedWorldRosterTrust.Hash(child))!.Count == 0 &&
            chain.ReadAfter(profile.Id, new string('A', 64)) is null &&
            chain.ReadAfterRevision(profile.Id, 1).Select(item => item.Signature).SequenceEqual([latest.Signature, child.Signature]) &&
            chain.ReadAfterRevision(profile.Id, 2).Select(item => item.Signature).SequenceEqual([latest.Signature, child.Signature]) &&
            chain.ReadAfterRevision(profile.Id, latest.Revision).Single().Signature == child.Signature &&
            // Numeric peer cursors cannot distinguish a legacy standalone edit
            // from its equal-counter owner child. Replay this boundary proof.
            chain.ReadAfterRevision(profile.Id, child.Revision).Select(item => item.Signature)
                .SequenceEqual([latest.Signature, child.Signature]),
            "repaired owner history lost its exact signed parent or protected anchor positions");
        Require((await fixture.Manager.SetSimpleSharedHostingAsync(profile.Id, true, CancellationToken.None)).Ok &&
            (await fixture.Manager.SharedWorldRosterAsync(profile.Id))!.Signature == child.Signature && chain.Count(profile.Id) == 3,
            "repeating the same policy after a lost response changed its signed history");
        var lateLegacy = Sign(latest with { Epoch = 100, Revision = 100 });
        var refused = false;
        try { chain.Append(lateLegacy, owner); }
        catch (InvalidDataException) { refused = true; }
        Require(refused && chain.Count(profile.Id) == 3 && chain.Heads(profile.Id).Single().Signature == child.Signature,
            "a standalone snapshot replaced an exact-parent schema-3 history");
        SaveSnapshot(lateLegacy);
        Require((await fixture.Manager.SharedWorldRosterAsync(profile.Id))!.Signature == child.Signature,
            "an obsolete legacy file overrode the schema-3 journal");
        Require((await fixture.Manager.SetSimpleSharedHostingAsync(profile.Id, false, CancellationToken.None)).Ok &&
            (await fixture.Manager.SharedWorldRosterAsync(profile.Id))!.Members.All(member =>
                !member.Grants.Receive && !member.Grants.EligibleHost && !member.Grants.Reshare) &&
            File.ReadAllBytes(snapshotPath).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(lateLegacy,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))) && fixture.Driver.Actions == 0,
            "repair lost signed evidence, invoked a process or prevented immediate policy withdrawal");
    }

    private static async Task WithdrawDelegatedHostingAsync(string root)
    {
        using var fixture = new Fixture(root, "withdraw-delegated");
        var target = fixture.AddPeer();
        Require(fixture.Pairing.SetSharedWorldGrants(fixture.DeviceId, fixture.ProfileId, new(ManageSharing: true)).Ok,
            "could not seed an earlier sharing manager");
        var profile = fixture.Current().Profiles[0];
        var service = new SharedWorldService(fixture.Data, new WorldBackupService(fixture.Data, fixture.Clock));
        var baseline = service.PublishRoster(profile, fixture.Pairing.SharedRosterMembers(fixture.ProfileId));
        var chain = new SharedWorldRosterChainStore(fixture.Data);
        chain.Append(baseline, baseline.OwnerPublicKey);
        var draft = baseline with
        {
            Schema = 3,
            Epoch = baseline.Epoch + 1,
            Revision = baseline.Revision + 1,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(baseline),
            SignerDeviceId = fixture.DeviceId,
            SignerPublicKey = baseline.Members.Single(member => member.DeviceId == fixture.DeviceId).PublicKey,
            Members = baseline.Members.Select(member => member.DeviceId == target ? member with { Grants = AutomaticHosting } : member).ToArray(),
            Signature = ""
        };
        var delegated = service.CountersignDelegatedRoster(draft with
        {
            Signature = Convert.ToBase64String(fixture.DeviceKey.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
        }, fixture.Clock.GetUtcNow());
        chain.Append(delegated, baseline.OwnerPublicKey);
        Require(fixture.Grants(target) == new SharedWorldGrants() &&
            (await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId))!.Members.Single(member => member.DeviceId == target).Grants == AutomaticHosting,
            "the delegated stale-hosting fixture did not preserve a distinct owner baseline");
        Require((await fixture.Manager.SetSimpleSharedHostingAsync(fixture.ProfileId, false, CancellationToken.None)).Ok,
            "turning off hosting could not override a delegated grant");
        var withdrawn = await fixture.Manager.SharedWorldRosterAsync(fixture.ProfileId);
        Require(withdrawn is not null && withdrawn.Revision > delegated.Revision &&
            withdrawn.Members.Single(member => member.DeviceId == target).Grants == new SharedWorldGrants() &&
            withdrawn.Members.Single(member => member.DeviceId == fixture.DeviceId).Grants.ManageSharing &&
            !fixture.Pairing.SharedRosterDirty(fixture.ProfileId),
            "an unchanged local baseline let an older delegated hosting grant survive server disable");
    }

    private static async Task RejectsAsync(Fixture fixture, string code, Guid? deviceId = null, Guid? profileId = null)
    {
        var before = fixture.Current();
        var grants = fixture.Grants();
        var result = await fixture.Manager.SetSharedWorldGrantsAsync(deviceId ?? fixture.DeviceId,
            profileId ?? fixture.ProfileId, Hosting);
        Require(!result.Ok && result.Code == code && Same(fixture.Current(), before) && fixture.Grants() == grants &&
            fixture.Driver.Actions == 0, "rejected hosting permission saved prerequisites or changed access: " + code);
    }

    private sealed class Fixture : IDisposable
    {
        internal LocalData Data { get; }
        internal HostManager Manager { get; }
        internal PairingService Pairing { get; }
        internal PolicyOnlyDriver Driver { get; }
        internal TestClock Clock { get; } = new();
        internal Guid DeviceId { get; }
        internal Guid ProfileId { get; }
        internal ECDsa DeviceKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        internal Fixture(string root, string label, Action<ServerProfile>? configureProfile = null,
            Action<PairedDevice>? configureDevice = null, string kind = GameKinds.Fixture)
        {
            Data = new(Path.Combine(root, "save-hosting-" + label + "-" + Guid.NewGuid().ToString("N")));
            var target = Profile("Synthetic target", 2456, kind);
            configureProfile?.Invoke(target); ProfileId = target.Id;
            Data.SaveSettings(new HostSettings
            {
                MaxConcurrentServers = 3,
                Profiles = [target, Profile("Synthetic sibling", 2458, kind)]
            });
            var issuer = new PairingService(Data, Clock);
            var code = issuer.IssueServer(ProfileId, false, false, "https://127.0.0.1:5131", new string('A', 64), refresh: false);
            var access = issuer.Activate(new(ProfileId, code.Code, ServerScope: true)) ??
                throw new InvalidOperationException("synthetic save-hosting pairing failed");
            DeviceId = access.DeviceId;
            var state = Data.LoadPairingState();
            var device = state.Devices.Single(device => device.Id == DeviceId);
            device.SharedWorldPublicKey = Convert.ToBase64String(DeviceKey.ExportSubjectPublicKeyInfo());
            configureDevice?.Invoke(device);
            Data.SavePairingState(state);
            Pairing = new PairingService(Data, Clock);
            Driver = new(kind);
            Manager = new(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly), Clock,
                new NullHostingPowerGuard(), pairing: Pairing);
        }
        private ServerProfile Profile(string name, int port, string kind) => new()
        {
            Name = name,
            ServerName = name,
            Kind = kind,
            WorldId = "SyntheticPermission",
            WorldSource = "New",
            GamePort = port,
            WorldDirectory = Path.Combine(Data.RootPath, "never-open-world-" + Guid.NewGuid().ToString("N")),
            ExecutablePath = Path.Combine(Data.RootPath, "never-start-server.exe")
        };
        internal HostSettings Current() => Data.LoadSettings();
        internal SharedWorldGrants Grants(Guid? deviceId = null) => Data.LoadPairingState().Devices.Single(device => device.Id == (deviceId ?? DeviceId))
            .SharedWorldGrants?.GetValueOrDefault(ProfileId) ?? new();
        internal Guid AddPeer(bool enroll = true)
        {
            var code = Pairing.IssueServer(ProfileId, false, false, "https://127.0.0.1:5131", new string('A', 64), refresh: false);
            var access = Pairing.Activate(new(ProfileId, code.Code, ServerScope: true)) ??
                throw new InvalidOperationException("later synthetic save-hosting pairing failed");
            if (enroll) Enroll(access.DeviceId);
            return access.DeviceId;
        }
        internal void Enroll(Guid deviceId)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Require(Pairing.BindSharedWorldKey(deviceId, new(nonce, publicKey,
                Convert.ToBase64String(key.SignData(SharedWorldRosterTrust.EnrollmentBasis(deviceId, nonce, publicKey), HashAlgorithmName.SHA256)))).Ok,
                "the later paired PC could not enroll its signing identity");
        }
        internal async Task PublishPolicyAsync(Guid? profileId = null)
        {
            var selected = profileId ?? ProfileId;
            var roster = await Manager.PublishSimpleSharedHostingRosterAsync(selected);
            Pairing.ConfirmSharedRosterPublished(selected, roster);
        }
        public void Dispose() { DeviceKey.Dispose(); Data.Dispose(); }
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PolicyOnlyDriver(string kind) : IGameServerDriver
    {
        internal int Actions { get; private set; }
        public string Kind => kind;
        public string DisplayName => "Synthetic save-permission adapter";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => true;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => throw NoProcess();
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
        private Exception NoProcess() { Actions++; return new InvalidOperationException("No game/process action is permitted in save-permission checks."); }
    }

    private static T Field<T>(HostManager manager, string name) => (T)typeof(HostManager)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
    private static HostSettings Clone(HostSettings value) => JsonSerializer.Deserialize<HostSettings>(JsonSerializer.SerializeToUtf8Bytes(value))!;
    private static bool Same(HostSettings left, HostSettings right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
