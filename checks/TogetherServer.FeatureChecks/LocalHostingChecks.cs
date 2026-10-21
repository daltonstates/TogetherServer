using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// File-only preparation with an adapter that refuses every process operation.
// These checks create no app, game, listener, window or console attachment.
internal static class LocalHostingChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        Inputs();
        await OwnerSwitchAsync(root);
        await IncomingConsentAsync(root);
        await FirstIncomingListenerAsync(root);
        await LatestSelectionBlocksAsync(root);
        using (var automatic = new Fixture(root, granted: true, automaticReceiving: true))
        {
            var automaticStatus = await automatic.Service.StatusAsync(automatic.ProfileId);
            Require(automaticStatus.Allowed && automaticStatus.Consented &&
                !automatic.Data.LoadSettings().CompanionListeningEnabled && automatic.Driver.ProcessAttempts == 0,
                "the combined hosting grant required a storage choice or opened an incoming listener");
            Require((await automatic.WithdrawReceivingAsync()).Ok &&
                !(await automatic.Service.StatusAsync(automatic.ProfileId)).Consented,
                "automatic receiving overrode a saved local withdrawal");
        }
        using var fixture = new Fixture(root, granted: true);
        var before = await fixture.Service.StatusAsync(fixture.ProfileId);
        Require(before.Allowed && !before.Consented && !before.ReadyForManualStart &&
            !before.NeedsServerFile && !before.NeedsPassword && before.BranchHash is null &&
            fixture.Data.LoadSettings().CompanionEndpoint == "" && fixture.Driver.ProcessAttempts == 0,
            "passive status lost signed consent-off eligibility, required an unnecessary input, or changed local setup");
        var noConsent = await fixture.Service.PrepareAsync(fixture.ProfileId, new(false, true));
        Require(!noConsent.Ok && !noConsent.Consented && noConsent.Code == "LocalConsentRequired" &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 0,
            "preparation silently consented or declared a history without local approval");
        var noWarning = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, false));
        Require(!noWarning.Ok && noWarning.Code == "SplitWarningRequired" &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 0,
            "a fresh continuation bypassed its separate-history warning");
        var needsConnection = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true, RequirePeerConnection: true));
        Require(!needsConnection.Ok && needsConnection.Code == "LocalConnectionSetupRequired" &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 0 &&
            fixture.Data.LoadSettings().CompanionEndpoint == "" && !fixture.Data.LoadSettings().CompanionListeningEnabled &&
            fixture.Driver.ProcessAttempts == 0,
            "guided hosting declared a signed branch or enabled a listener before deliberate incoming-connection setup");
        var prepared = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(prepared.Ok && prepared.ReadyForManualStart && !prepared.Running &&
            prepared.LocalProfileId is not null && prepared.BranchHash is not null &&
            prepared.GamePort != fixture.ControlPort && prepared.Steps.Take(4).All(step => step.State == "Ready") &&
            prepared.Steps[4].State == "Unknown" && !fixture.Data.LoadSettings().CompanionListeningEnabled &&
            !fixture.Data.LoadSettings().RemoteControlsEnabled && fixture.Data.LoadSettings().CompanionBindAddress == "127.0.0.1" &&
            fixture.Data.LoadSettings().CompanionEndpoint == $"https://127.0.0.1:{fixture.ControlPort}" &&
            fixture.Data.LoadRuns().Count == 0 && fixture.Driver.ProcessAttempts == 0,
            $"offline verified preparation or honest route/port evidence failed: {prepared.Code}; " +
            $"ok={prepared.Ok}, ready={prepared.ReadyForManualStart}, running={prepared.Running}, " +
            $"profile={prepared.LocalProfileId.HasValue}, branch={prepared.BranchHash is not null}, " +
            $"ports={prepared.GamePort}/{prepared.ControlPort}/{fixture.ControlPort}, " +
            $"steps={string.Join(',', prepared.Steps.Select(step => step.Key + ':' + step.State))}; {prepared.Message}");
        var passive = await fixture.Service.StatusAsync(fixture.ProfileId);
        var stamp = new string('D', 64);
        fixture.Manager.SharedHostingObservation = _ => stamp;
        await fixture.Manager.RecordHostingObservationAsync(prepared.LocalProfileId!.Value,
            fixture.LatestObservation(prepared.BranchHash!, stamp), CancellationToken.None);
        fixture.Manager.SharedHostingObservation = _ => new string('E', 64);
        Require((await fixture.Manager.StartSeparateCopyAsync(prepared.LocalProfileId.Value, prepared.BranchHash!)).Code == "LatestSaveChanged" &&
            fixture.Driver.ProcessAttempts == 0, "a locally learned newer save crossed the in-gate Start check");
        fixture.Manager.SharedHostingObservation = _ => stamp;
        Require((await fixture.Manager.StartSeparateCopyAsync(prepared.LocalProfileId.Value, prepared.BranchHash!)).Code == "LatestSaveChanged" &&
            fixture.Driver.ProcessAttempts == 0, "a rejected one-shot latest-save observation could be replayed");
        fixture.Manager.SharedHostingObservation = null;
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            foreach (var start in new Func<Task<ActionResult>>[] {
                () => fixture.Manager.StartAsync(prepared.LocalProfileId!.Value, canceled.Token),
                () => fixture.Manager.StartSeparateCopyAsync(prepared.LocalProfileId!.Value, prepared.BranchHash!, canceled.Token) })
            {
                try { await start(); throw new InvalidOperationException("A canceled Start reached lifecycle dispatch."); }
                catch (OperationCanceledException) { }
            }
            Require(fixture.Driver.ProcessAttempts == 0 && fixture.Data.LoadRuns().Count == 0,
                "a canceled local hosting instruction changed a managed run");
        }
        Require(passive.ReadyForManualStart && !passive.Running && passive.LocalProfileId == prepared.LocalProfileId &&
            passive.Steps.Take(4).All(step => step.State == "Ready") && fixture.Data.LoadRuns().Count == 0 &&
            fixture.Driver.ProcessAttempts == 0, "passive prepared status lost readiness or attempted a process operation");
        var repeated = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(repeated.Ok && repeated.LocalProfileId == prepared.LocalProfileId && repeated.BranchHash == prepared.BranchHash &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 1 && fixture.Driver.ProcessAttempts == 0,
            "a repeated preparation created another signed branch or tried to start a process");
        var branch = new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Single();
        var nextPortPlan = await fixture.Manager.LocalHostingPortsAsync(branch.Offer.Version, null, CancellationToken.None);
        Require(nextPortPlan.Available && nextPortPlan.GamePort == prepared.GamePort,
            "an inactive retained world unnecessarily changed the next Host's configured game port");
        var routeIdentity = new LocalHostingRouteSnapshot(branch.Offer.Proposal.CandidateAddress,
            fixture.ControlPort, "0.0.0.0", true, true, CompanionListenerStates.Listening,
            branch.Offer.CandidateTlsFingerprint, true);
        var routeEvidence = new LocalHostingRouteEvidence("NotChecked", "Unknown", "Synthetic current identity",
            DateTimeOffset.UtcNow, routeIdentity);
        Require(FriendLocalHostingService.CurrentSignedControlRoute(true, branch, routeEvidence) &&
            !FriendLocalHostingService.CurrentSignedControlRoute(false, branch, routeEvidence),
            "a matching current listener lost a real signed route proof or fabricated one");
        foreach (var changed in new[] { routeIdentity with { Enabled = false }, routeIdentity with { Active = false },
            routeIdentity with { ListenerOwned = false }, routeIdentity with { CertificateFingerprint = new string('B', 64) },
            routeIdentity with { Endpoint = "https://192.0.2.88:5131" } })
            Require(!FriendLocalHostingService.CurrentSignedControlRoute(true, branch, routeEvidence with { Identity = changed }),
                "an old signed route proof stayed Checked after listener or identity changed");
        Require((await fixture.Manager.StartAsync(prepared.LocalProfileId!.Value)).Code == "SeparateCopyManualStartRequired" &&
            fixture.Driver.ProcessAttempts == 0, "ordinary Start bypassed the explicit local-branch gate");

        var profile = fixture.Data.LoadSettings().Profiles.Single(item => item.Id == prepared.LocalProfileId);
        var localSave = Path.Combine(profile.WorldDirectory, "world.dat");
        var advanced = Encoding.UTF8.GetBytes("local progress which must never be reset by preparation");
        File.WriteAllBytes(localSave, advanced);
        fixture.AdvanceSource();
        var afterSourceAdvance = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(!afterSourceAdvance.Ok && !afterSourceAdvance.ReadyForManualStart &&
            afterSourceAdvance.LocalProfileId == prepared.LocalProfileId &&
            afterSourceAdvance.BranchHash == prepared.BranchHash &&
            File.ReadAllBytes(localSave).SequenceEqual(advanced) &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 1 &&
            fixture.Data.LoadSettings().Profiles.Count == 1 && fixture.Driver.ProcessAttempts == 0,
            "preparation overwrote local progress, selected a newer foreign head, or created a second branch");
        var failedPassive = await fixture.Service.StatusAsync(fixture.ProfileId);
        Require(!failedPassive.ReadyForManualStart && failedPassive.LocalProfileId == prepared.LocalProfileId &&
            failedPassive.BranchHash == prepared.BranchHash && File.ReadAllBytes(localSave).SequenceEqual(advanced),
            "passive polling revived a preparation known to have failed or changed the retained world");
        var blockedStart = await fixture.Manager.StartSeparateCopyAsync(profile.Id, prepared.BranchHash!);
        Require(!blockedStart.Ok && fixture.Driver.ProcessAttempts == 0,
            "manual Start trusted the cheap status instead of rechecking altered world bytes");

        using var denied = new Fixture(root, granted: false);
        var rejected = await denied.Service.PrepareAsync(denied.ProfileId, new(true, true));
        Require(!rejected.Ok && !rejected.Allowed && !rejected.Consented &&
            denied.Data.LoadSettings().CompanionEndpoint == "" && denied.Data.LoadSettings().Profiles.Count == 0 &&
            denied.Driver.ProcessAttempts == 0, "an ungranted PC received hosting, local consent, a profile or identity setup");
    }

    private static async Task IncomingConsentAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "incoming-consent-" + Guid.NewGuid().ToString("N")));
        var games = new GameServerRegistry([new FilesOnlyDriver()], PortProbeMode.ObserveOnly);
        var manager = new HostManager(data, games);
        var settings = new HostSettings { CompanionEndpoint = "https://192.168.50.9:5131", CompanionBindAddress = "192.168.50.9" };
        Require((await manager.UpdateSettingsAsync(settings)).Ok, "an off incoming route could not be saved");
        var unpaired = data.LoadSettings();
        unpaired.CompanionListeningEnabled = true;
        Require((await manager.UpdateSettingsAsync(unpaired)).Code == "PairingRequired" && !data.LoadSettings().CompanionListeningEnabled,
            "a full settings save enabled incoming connections without the explicit control choice");
        var enabled = await manager.UpdateControlPolicyAsync(new(CompanionListeningEnabled: true));
        Require(enabled.Ok && data.LoadSettings().CompanionListeningEnabled && !data.LoadSettings().RemoteControlsEnabled &&
            !data.HasProtected("host-certificate.protected") && data.LoadPairingState().Devices.Count == 0,
            "the explicit incoming choice required an invite first or manufactured credentials, TLS or remote permission");
        using var modeGate = new SemaphoreSlim(1, 1);
        var listener = new CompanionServer(data, manager, new PairingService(data), games,
            new ServerLogService(data, manager), modeGate, 55320,
            inMemoryTransport: _ => throw new InvalidOperationException("The file-only consent check must never build a listener."));
        try
        {
            await listener.SyncAsync();
            Require(!listener.Active && listener.ListenerState == CompanionListenerStates.Idle && listener.InMemoryApp is null,
                "incoming consent alone opened or attempted to build a listener before pairing/TLS preparation");
            var next = data.LoadSettings();
            next.MaxConcurrentServers = 2;
            Require((await manager.UpdateSettingsAsync(next)).Ok && data.LoadSettings().CompanionListeningEnabled,
                "saving existing incoming consent still blocked unrelated validated setup");
            Require((await manager.UpdateControlPolicyAsync(new(RemoteControlsEnabled: true))).Code == "FriendPermissionRequired" &&
                !data.LoadSettings().RemoteControlsEnabled,
                "pending incoming consent bypassed the separate remote Start/Stop permission gate");
            Require((await manager.UpdateControlPolicyAsync(new(CompanionListeningEnabled: false))).Ok,
                "the owner could not withdraw incoming consent before pairing");
            await listener.SyncAsync();
            Require(!listener.Active && listener.ListenerState == CompanionListenerStates.Off,
                "withdrawn incoming consent did not remain off");
        }
        finally { await listener.StopAsync(); }
    }

    private static async Task LatestSelectionBlocksAsync(string root)
    {
        using var fixture = new Fixture(root, granted: true);
        var prepared = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(prepared.Ok && prepared.LocalProfileId is not null && prepared.BranchHash is not null,
            "the newest-selection check did not prepare its synthetic baseline");
        var profile = fixture.Data.LoadSettings().Profiles.Single(item => item.Id == prepared.LocalProfileId);
        var save = File.ReadAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"));
        var attempts = 0;
        var service = fixture.WithLatestSource((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            return Task.FromResult(new WorldPeerLatestSourceResult(false, "SharedSaveHistoryNeedsReview",
                "Different progress needs review."));
        });
        var refused = await service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(!refused.Ok && refused.Code == "HostingHistoryChoiceRequired" && !refused.ReadyForManualStart &&
            !(await service.StatusAsync(fixture.ProfileId)).ReadyForManualStart && attempts == 1 &&
            File.ReadAllBytes(Path.Combine(profile.WorldDirectory, "world.dat")).SequenceEqual(save) &&
            fixture.Data.LoadSettings().Profiles.Count == 1 && fixture.Driver.ProcessAttempts == 0,
            "known competing progress fell back to an older prepared world or polling revived a refused Start");
    }

    private static async Task FirstIncomingListenerAsync(string root)
    {
        foreach (var test in new[] { (Enabled: true, Starts: true, Announces: true), (Enabled: true, Starts: false, Announces: true),
            (Enabled: true, Starts: true, Announces: false), (Enabled: false, Starts: true, Announces: true) })
        {
            using var fixture = new Fixture(root, granted: true);
            var settings = fixture.Data.LoadSettings();
            settings.CompanionEndpoint = $"https://192.168.50.9:{fixture.ControlPort}";
            settings.CompanionBindAddress = "192.168.50.9";
            settings.CompanionListeningEnabled = false;
            Require((await fixture.Manager.UpdateSettingsAsync(settings)).Ok, "the configured file-only incoming route was rejected");
            if (test.Enabled)
                Require((await fixture.Manager.UpdateControlPolicyAsync(new(CompanionListeningEnabled: true))).Ok,
                    "the first explicit incoming choice was blocked before a signed candidate could be prepared");
            var active = false;
            var sawIdleBeforeBranch = false;
            var sawFinishedBranch = false;
            var announceAttempts = 0;
            var shared = false;
            var checker = new LocalHostingRouteCheck(async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = (await fixture.Manager.CompanionSnapshotAsync()).Settings;
                using var certificate = new HostIdentity(fixture.Data).Load();
                return new(current.CompanionEndpoint, current.CompanionPort, current.CompanionBindAddress,
                    current.CompanionListeningEnabled, active, active ? CompanionListenerStates.Listening : CompanionListenerStates.Idle,
                    certificate is null ? null : HostIdentity.Fingerprint(certificate), active);
            }, (_, _, _) => throw new InvalidOperationException("A private file-only route must not make an outside network request."));
            async Task Synchronize()
            {
                var branches = new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId);
                if (branches.Count == 0) { sawIdleBeforeBranch = true; return; }
                var branch = branches.Single();
                var prepared = await fixture.Manager.RecordedLocalHostingStatusAsync(fixture.ProfileId, branch.BranchHash, CancellationToken.None);
                sawFinishedBranch |= prepared.Restored && prepared.ReadyForManualStart;
                active = test.Starts && sawFinishedBranch && shared;
            }
            async Task<WorldPeerActionResult> Announce(SharedWorldPeerRoot source, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var branch = new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Single();
                var prepared = await fixture.Manager.RecordedLocalHostingStatusAsync(fixture.ProfileId, branch.BranchHash, cancellationToken);
                Require(source == new SharedWorldPeerRoot(branch.Offer.Roster.OwnerPublicKey, fixture.ProfileId, branch.Offer.Roster.GroupId) &&
                    prepared.Restored && prepared.ReadyForManualStart && !active,
                    "first hosting published an unrelated or unprepared branch before its exact signed save was verified");
                sawFinishedBranch = true;
                announceAttempts++;
                shared = test.Announces;
                return new(test.Announces, test.Announces ? "HostingAddressPrepared" : "PeerMembershipRequired", "File-only signed-group registration.");
            }
            var service = fixture.WithIncomingListener(checker, Synchronize, Announce);
            var result = await service.PrepareAsync(fixture.ProfileId, new(true, true, RequirePeerConnection: true));
            var retainedBranches = new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId);
            Require(sawIdleBeforeBranch && fixture.Driver.ProcessAttempts == 0 && fixture.Data.LoadRuns().Count == 0 &&
                fixture.Data.LoadSettings().CompanionListeningEnabled &&
                fixture.Data.LoadSettings().CompanionEndpoint == settings.CompanionEndpoint &&
                fixture.Data.LoadSettings().CompanionBindAddress == settings.CompanionBindAddress,
                "confirmed hosting launched a process or changed the configured route");
            Require(sawFinishedBranch && retainedBranches.Count == 1 && result.LocalProfileId is not null &&
                result.BranchHash == retainedBranches.Single().BranchHash,
                "the configured Idle listener prevented its first exact signed save from being prepared");
            var restored = fixture.Data.LoadSettings().Profiles.Single(profile => profile.Id == result.LocalProfileId);
            Require(File.ReadAllText(Path.Combine(restored.WorldDirectory, "world.dat")) == "complete signed source bytes",
                "incoming-listener preparation did not preserve the exact received source payload");
            var passive = await service.StatusAsync(fixture.ProfileId);
            Require(announceAttempts == 1, "guided preparation failed to register its signed group exactly once");
            if (!test.Announces)
                Require(!result.Ok && !result.ReadyForManualStart && !passive.ReadyForManualStart &&
                    result.Code == "PeerMembershipRequired" && !active,
                    "rejected signed-group registration kept Start readiness or activated the listener");
            else if (test.Starts)
                Require(result.Ok && result.ReadyForManualStart && passive.ReadyForManualStart && !result.Running &&
                    result.Steps.Single(step => step.Key == "connection").State == "Unknown",
                    "a first owned private listener did not finish preparation or fabricated an outside/game route pass");
            else
                Require(!result.Ok && !result.ReadyForManualStart && !passive.ReadyForManualStart &&
                    result.Code == "LocalConnectionSetupRequired" &&
                    FriendLocalHostingService.WithRemedy(result).Remedy?.Kind == "Connection",
                    "a listener that stayed Idle retained Start readiness or lost its actionable connection remedy");
        }
    }

    private static async Task OwnerSwitchAsync(string root)
    {
        using var data = new LocalData(Path.Combine(root, "owner-hosting-switch-" + Guid.NewGuid().ToString("N")));
        var executable = Path.Combine(data.RootPath, "never-run.synthetic");
        File.WriteAllText(executable, "synthetic file only");
        var profile = new ServerProfile
        {
            Id = Guid.NewGuid(),
            Kind = GameKinds.Fixture,
            Name = "Synthetic owner",
            ServerName = "Synthetic owner",
            WorldId = "SyntheticOwner",
            WorldDirectory = Path.Combine(data.RootPath, "world"),
            ExecutablePath = executable,
            Backups = new() { Enabled = false, RetentionCount = 13, MinimumFreeSpaceMb = 2048 }
        };
        data.SaveSettings(new HostSettings { Profiles = [profile] });
        var driver = new FilesOnlyDriver();
        var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
        var enabled = await manager.SetSimpleSharedHostingAsync(profile.Id, true, CancellationToken.None);
        var saved = data.LoadSettings();
        Require(enabled.Ok && saved.Profiles.Single().SharedSavesEnabled && saved.Profiles.Single().Backups.Enabled &&
            saved.Profiles.Single().Backups.RetentionCount == 13 && saved.Profiles.Single().Backups.MinimumFreeSpaceMb == 2048 &&
            !saved.CompanionListeningEnabled && !saved.RemoteControlsEnabled && driver.ProcessAttempts == 0,
            $"the owner switch failed to preserve storage/network choices: {enabled.Code}; {enabled.Message}");
        Require((await manager.SetSimpleSharedHostingAsync(profile.Id, false, CancellationToken.None)).Ok &&
            !data.LoadSettings().Profiles.Single().SharedSavesEnabled && data.LoadSettings().Profiles.Single().Backups.Enabled &&
            driver.ProcessAttempts == 0, "turning sharing off also disabled backup protection or attempted a process operation");
    }

    private static void Inputs()
    {
        var valid = FriendLocalHostingService.ParseRequest(Encoding.UTF8.GetBytes(
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true}"));
        Require(valid.AllowReceiving && valid.AcceptSplitWarning && valid.ServerFile is null && valid.GamePassword is null,
            "fixed preparation choices did not decode");
        foreach (var input in new[]
        {
            "{}", "null", "[]", "{\"allowReceiving\":true}",
            "{\"allowReceiving\":true,\"allowReceiving\":false,\"acceptSplitWarning\":true}",
            "{\"AllowReceiving\":true,\"acceptSplitWarning\":true}",
            "{\"allowReceiving\":\"true\",\"acceptSplitWarning\":true}",
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"command\":\"anything\"}",
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"serverFile\":\"relative.exe\"}",
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"gamePassword\":\"bad\"}"
        }) Reject(() => FriendLocalHostingService.ParseRequest(Encoding.UTF8.GetBytes(input)), "invalid preparation input was accepted");
        var hash = new string('A', 64);
        Require(FriendLocalHostingRoutes.ParseStart(Encoding.UTF8.GetBytes(
            $"{{\"branchHash\":\"{hash}\",\"acceptSplitWarning\":true}}")).AcceptSplitWarning,
            "exact local Start did not decode");
        foreach (var input in new[] { "{}", "{\"branchHash\":\"bad\",\"acceptSplitWarning\":true}",
            $"{{\"branchHash\":\"{hash}\",\"acceptSplitWarning\":true,\"localProfileId\":\"other\"}}",
            $"{{\"branchHash\":\"{hash}\",\"branchHash\":\"{hash}\",\"acceptSplitWarning\":true}}" })
            Reject(() => FriendLocalHostingRoutes.ParseStart(Encoding.UTF8.GetBytes(input)), "invalid exact Start input was accepted");
        var context = new DefaultHttpContext();
        Require(!FriendLocalHostingRoutes.Fixed(context, true), "sensitive hosting GET omitted its local header");
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        Require(FriendLocalHostingRoutes.Fixed(context, true), "fixed local GET was refused");
        context.Request.QueryString = new("?path=anything");
        Require(!FriendLocalHostingRoutes.Fixed(context), "hosting accepted arbitrary query input");
        context.Request.QueryString = QueryString.Empty;
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        Require(!FriendLocalHostingRoutes.Fixed(context, true), "hosting GET accepted a request body");
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }

    private sealed class Fixture : IDisposable
    {
        internal readonly LocalData Data;
        internal readonly Guid ProfileId = Guid.NewGuid();
        internal readonly Guid GroupId = Guid.NewGuid();
        internal readonly Guid DeviceId = Guid.NewGuid();
        internal readonly int ControlPort;
        internal readonly FilesOnlyDriver Driver = new();
        internal readonly HostManager Manager;
        internal readonly FriendLocalHostingService Service;
        private readonly FriendService friends;
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string vault;
        private SharedWorldVersion head;

        internal Fixture(string root, bool granted, bool automaticReceiving = false)
        {
            Data = new(Path.Combine(root, "local-hosting-" + Guid.NewGuid().ToString("N")));
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var occupied = properties.GetActiveTcpListeners().Concat(properties.GetActiveUdpListeners()).Select(item => item.Port).ToHashSet();
            ControlPort = Enumerable.Range(50000, 8000).First(port => Enumerable.Range(port, 4).All(item => !occupied.Contains(item)));
            Data.SaveSettings(new HostSettings { CompanionPort = ControlPort, Profiles = [] });
            var draft = new SharedWorldRoster(3, GroupId, ProfileId, 1, 1, true, Public(owner),
                [new(DeviceId, Public(member), new(Receive: granted, EligibleHost: granted, Reshare: automaticReceiving), false)], "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: Public(owner));
            var roster = draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            Data.SaveProtected($"shared-world-pc-signing-{DeviceId:N}.protected", member.ExportPkcs8PrivateKey());
            Data.SaveProtected($"shared-world-roster-{DeviceId:N}-{ProfileId:N}.protected", JsonSerializer.SerializeToUtf8Bytes(roster, Json));
            vault = Path.Combine(Data.RootPath, "received-shared-worlds", DeviceId.ToString("N"), ProfileId.ToString("N"));
            var bytes = Encoding.UTF8.GetBytes("complete signed source bytes");
            head = Version(1, null, bytes);
            WriteCopy(head, bytes);
            Data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
            {
                DeviceId = DeviceId,
                DisplayName = "Synthetic local hosting",
                Endpoint = "https://192.0.2.10:5131",
                Fingerprint = new string('A', 64),
                Credential = new string('C', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                ApprovedSharedWorldGroups = new() { [ProfileId] = GroupId },
                SharedWorldSigningKeys = new() { [ProfileId] = Public(owner) },
                SharedRosterFloors = new() { [ProfileId] = new(GroupId, 1, 1, roster.Signature) },
                LastSharedHostManifests = new() { [ProfileId] = head }
            }, Json));
            var executable = Path.Combine(Data.RootPath, "never-run.synthetic");
            File.WriteAllText(executable, "file boundary only; never executable");
            Manager = new(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly));
            friends = new(Data, (_, _) => throw new InvalidOperationException("Network access is forbidden in file-only preparation checks."));
            Service = new(Data, friends, Manager, _ => [executable]);
        }

        internal void AdvanceSource()
        {
            var bytes = Encoding.UTF8.GetBytes("a later source history, distinct from local progress");
            head = Version(head.Number + 1, head.VersionHash, bytes);
            WriteCopy(head, bytes);
        }
        internal Task<ReceivedSharedWorldResult> WithdrawReceivingAsync() =>
            friends.SetSharedWorldConsentAsync(ProfileId, false);
        internal WorldPeerLatestSourceResult LatestObservation(string branchHash, string observation) =>
            new(true, "LatestSharedSaveReady", "Synthetic latest-save observation.",
                new(new(Public(owner), ProfileId, GroupId), branchHash, new(head), vault), observation);
        internal FriendLocalHostingService WithLatestSource(
            Func<SharedWorldPeerRoot, CancellationToken, Task<WorldPeerLatestSourceResult>> latest) =>
            new(Data, friends, Manager, _ => [Path.Combine(Data.RootPath, "never-run.synthetic")], latestSource: latest);
        internal FriendLocalHostingService WithIncomingListener(LocalHostingRouteCheck checker, Func<Task> synchronize,
            Func<SharedWorldPeerRoot, CancellationToken, Task<WorldPeerActionResult>> announce) =>
            new(Data, friends, Manager, _ => [Path.Combine(Data.RootPath, "never-run.synthetic")],
                routeCheck: checker, synchronizeListener: synchronize, announceHosting: announce);
        private SharedWorldVersion Version(long number, string? parent, byte[] bytes) => SharedWorldService.SignVersion(new(4,
            GroupId, number, parent, ProfileId, GameKinds.Fixture, "SyntheticLocalHosting", DateTimeOffset.UtcNow,
            SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(ControlPort, false, "Unknown", [], []),
            [new("world.dat", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)))], "", "", ""), owner);
        private void WriteCopy(SharedWorldVersion version, byte[] bytes)
        {
            var directory = Path.Combine(vault, version.VersionHash);
            Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
            File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
            File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            FriendLink.KeepSignedManifest(vault, version);
            File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        }
        public void Dispose() { friends.Dispose(); Data.Dispose(); owner.Dispose(); member.Dispose(); }
    }

    internal sealed class FilesOnlyDriver : IGameServerDriver
    {
        internal int ProcessAttempts { get; private set; }
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Source-only preparation";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [new("UDP", profile.GamePort, "Game"), new("UDP", profile.GamePort + 1, "Query")];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => null;
        private InvalidOperationException NoProcess() { ProcessAttempts++; return new("No process operation is permitted."); }
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
    }

    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
