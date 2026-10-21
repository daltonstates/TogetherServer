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

    internal static async Task RunAsync(string root)
    {
        await SimpleServerPolicyAsync(root);
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
            var path = Path.Combine(fixture.Data.RootPath, "shared-worlds", fixture.ProfileId.ToString("N"),
                roster.GroupId.ToString("N") + ".roster.json");
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
