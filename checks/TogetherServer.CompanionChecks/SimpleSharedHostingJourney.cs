using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

internal static partial class CoreRemoteJourney
{
    // Hosted Windows only. Three packaged apps use the existing disposable,
    // pipe-only lab driver and an explicitly assigned private runner address.
    // No real game, native window, browser, installer or network policy change.
    public static async Task RunSimpleSharedHostingAsync(string appPath, string fixturePath, string lanAddress)
    {
        LabRequire(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true",
            "HostedWindowsRequired");
        _ = LocalPairingLabNetwork.Resolve(lanAddress);
        LabRequire(!IPAddress.IsLoopback(IPAddress.Parse(lanAddress)) && File.Exists(appPath) && File.Exists(fixturePath),
            "PrivateRunnerAddressAndBinariesRequired");
        var runId = Guid.NewGuid();
        var root = Path.GetFullPath(Path.Combine("local-data", "local-pairing-lab", "simple-hosting-" + runId.ToString("N")));
        Directory.CreateDirectory(root);
        var hostData = Path.Combine(root, "host");
        var firstData = Path.Combine(root, "friend-a");
        var secondData = Path.Combine(root, "friend-b");
        var ports = new List<int>();
        while (ports.Count < 6) ports.Add(FreeTcpPort(ports.ToArray()));
        using var owner = LocalClient(ports[0]);
        using var first = LocalClient(ports[1]);
        using var second = LocalClient(ports[2]);
        foreach (var client in new[] { owner, first, second })
        {
            client.DefaultRequestHeaders.Add("X-TogetherServer-Local", "1");
            client.Timeout = TimeSpan.FromMinutes(2);
        }
        using var job = new LocalPairingLabJob();
        var processes = new List<PairingLabProcess>();
        var stages = new List<string>();
        PairingLabProcess? host = null, candidate = null, receiver = null;
        string? failed = null, groupKey = null;
        string activeCheck = "setup";
        string? failedCheck = null;
        long? failedJsonLine = null, failedJsonByte = null;
        var initialBytes = "Synthetic original " + runId.ToString("N");
        var continuedBytes = "Synthetic continued progress " + runId.ToString("N");
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            Name = "Automatic shared hosting",
            ServerName = "Synthetic shared hosting",
            WorldId = "simple-hosting",
            WorldSource = "New",
            WorldDirectory = Path.Combine(hostData, "worlds", "simple-hosting"),
            ExecutablePath = Path.GetFullPath(fixturePath),
            GamePort = FreeUdpPair(),
            Backups = new() { Enabled = false, MinimumFreeSpaceMb = 0 }
        };
        Guid firstDevice = default, secondDevice = default;
        var friendStartAttempts = 0;
        var ownerStartAttempts = 0;
        LocalHostingStatus? prepared = null;
        SharedWorldVersion? initialVersion = null, continuedVersion = null;
        var timer = Stopwatch.StartNew();
        try
        {
            host = Launch("host", "--host", ports[0], hostData);
            candidate = Launch("friend-a", "--friend", ports[1], firstData);
            receiver = Launch("friend-b", "--friend", ports[2], secondData);
            await Task.WhenAll(WaitLocalAsync(ports[0]), WaitLocalAsync(ports[1]), WaitLocalAsync(ports[2]));
            RequireReady(host, ports[0]);
            RequireReady(candidate, ports[1]);
            RequireReady(receiver, ports[2]);
            Directory.CreateDirectory(profile.WorldDirectory);
            await File.WriteAllTextAsync(Path.Combine(profile.WorldDirectory, "world.dat"), initialBytes);
            LabRequire((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", new()
            {
                Profiles = [profile],
                MaxConcurrentServers = 2,
                CompanionBindAddress = lanAddress,
                CompanionEndpoint = $"https://{lanAddress}:{ports[3]}",
                CompanionPort = ports[3]
            })).Ok, "SimpleHostSettingsRejected");
            await ConfigureFriendAsync(first, ports[4]);
            await ConfigureFriendAsync(second, ports[5]);
            Pass("isolated-private-lab");

            var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner,
                $"/api/local/servers/{profile.Id}/invite", new(false, false, true));
            LabRequire(invite.GetProperty("ok").GetBoolean() && invite.GetProperty("listenerActive").GetBoolean(),
                "SimpleInviteUnavailable");
            var code = invite.GetProperty("password").GetString();
            LabRequire(!string.IsNullOrWhiteSpace(code), "SimpleInviteMissing");
            LabRequire((await PostAsync<FriendPairRequest, FriendActionResult>(first, "/api/local/friend/pair", new(code!))).Ok,
                "SimpleFirstPairingRejected");
            firstDevice = (await PairingLabGetAsync<PairingLabDevices>(owner, "/api/local/companion")).Devices.Single().Id;
            LabRequire((await PostAsync<FriendPairRequest, FriendActionResult>(second, "/api/local/friend/pair", new(code!))).Ok,
                "SimpleSecondPairingRejected");
            secondDevice = (await PairingLabGetAsync<PairingLabDevices>(owner, "/api/local/companion")).Devices.Single(item => item.Id != firstDevice).Id;
            _ = await WaitForConnectedAsync(first, allowDisabled: true);
            _ = await WaitForConnectedAsync(second, allowDisabled: true);
            LabRequire((await PutAsync<object, SharedWorldResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/hosting", new { enabled = true })).Ok,
                "SimpleServerChoiceRejected");
            var initialRoster = await PairingLabGetAsync<SharedWorldRoster>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/governance");
            groupKey = new SharedWorldPeerRoot(initialRoster.OwnerPublicKey, profile.Id, initialRoster.GroupId).GroupKey;
            // The ordinary peer refresh enrolls identities before save consent
            // is projected. No per-PC grant or receiving choice is submitted.
            await SyncAsync(first);
            await SyncAsync(second);
            var roster = await PairingLabGetAsync<SharedWorldRoster>(owner, $"/api/local/profiles/{profile.Id}/shared-world/governance");
            LabRequire(SharedWorldRosterTrust.Verify(roster) && roster.Members.Count == 2 && roster.Members.All(member =>
                !member.Revoked && member.Grants is { Receive: true, EligibleHost: true, Reshare: true, RecoveryVoter: false, ManageSharing: false }),
                "SimpleAutomaticSignedGrantsMissing");
            groupKey = new SharedWorldPeerRoot(roster.OwnerPublicKey, profile.Id, roster.GroupId).GroupKey;
            foreach (var client in new[] { first, second })
            {
                var permission = await PairingLabGetAsync<LocalHostingPermission>(client,
                    $"/api/local/friend/{profile.Id}/shared-world/hosting/access");
                LabRequire(permission.Allowed && permission.Code == "HostingAllowed",
                    "SimpleHostingEntryPermissionMissingBeforeFirstSave");
                LabRequire((await PairingLabGetAsync<ReceivedSharedWorldStatus>(client,
                    $"/api/local/friend/{profile.Id}/shared-world")).Consented,
                    "SimpleAutomaticReceivingMissing");
            }
            Pass("one-server-choice-enrolls-both-friends");

            for (var publication = 1; publication <= 2; publication++)
            {
                var previousHash = initialVersion?.VersionHash;
                var originalStart = await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/start", new { });
                LabRequire(originalStart.Ok, "SimpleOriginalStartRejected");
                await TrackFixtureAsync(originalStart.Snapshot.Runs.Single(item => item.ProfileId == profile.Id), owner);
                LabRequire((await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/stop", new { })).Ok,
                    "SimpleOriginalStopRejected");
                initialVersion = (await PairingLabGetAsync<SharedWorldStatus>(owner,
                    $"/api/local/profiles/{profile.Id}/shared-world")).Latest;
                LabRequire(initialVersion is not null && initialVersion.Number == publication && initialVersion.ParentHash == previousHash,
                    "SimpleInitialHistoryMissing");
            }
            Pass("original-host-completes-two-linked-saves");
            foreach (var pc in new[] { (Client: first, Data: firstData, Device: firstDevice), (Client: second, Data: secondData, Device: secondDevice) })
            {
                await PairingLabWaitAsync(async () =>
                {
                    _ = await PostAsync<object, ReceivedSharedWorldResult>(pc.Client,
                        $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
                    return (await PairingLabGetAsync<ReceivedSharedWorldStatus>(pc.Client,
                        $"/api/local/friend/{profile.Id}/shared-world")).ReceiptConfirmed == true;
                }, "SimpleAutomaticCopyUnconfirmed", TimeSpan.FromSeconds(45));
                RequireCopy(Path.Combine(pc.Data, "received-shared-worlds", pc.Device.ToString("N"), profile.Id.ToString("N")),
                    initialVersion!, initialBytes);
            }
            prepared = await PostAsync<LocalHostingPrepareRequest, LocalHostingStatus>(first,
                $"/api/local/friend/{profile.Id}/shared-world/hosting/prepare",
                new(true, true, ServerFile: Path.GetFullPath(fixturePath), RequirePeerConnection: true));
            LabRequire(prepared.ReadyForManualStart && prepared.LocalProfileId is not null && prepared.BranchHash is not null,
                "SimpleFriendPreparationRejected");
            await PairingLabWaitAsync(async () =>
            {
                friendStartAttempts++;
                var result = await PostAsync<object, LocalHostingActionResult>(first,
                    $"/api/local/friend/{profile.Id}/shared-world/hosting/start", new
                    { localProfileId = prepared.LocalProfileId, branchHash = prepared.BranchHash, requestId = (Guid?)null, acceptSplitWarning = true });
                LabRequire(result.Ok || result.Code == "LatestSaveChanged", "SimpleFriendStartRejected-" + result.Code);
                return result.Ok;
            }, "SimpleFriendStartDidNotStabilize", TimeSpan.FromSeconds(45));
            HostSnapshot candidateSnapshot;
            await ModeAsync(first, hostMode: true);
            try
            {
                candidateSnapshot = await PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot");
                await TrackFixtureAsync(candidateSnapshot.Runs.Single(item => item.ProfileId == prepared.LocalProfileId), first);
            }
            finally { await ModeAsync(first, hostMode: false); }
            await SyncAsync(first);
            await SyncAsync(owner);
            await SyncAsync(second);
            var receiverGroup = (await PairingLabGetAsync<WorldPeerGroupView[]>(second, "/api/local/world-peers")).Single(item => item.GroupKey == groupKey);
            LabRequire(receiverGroup.State.Peers.Any(peer => peer.DeviceId == firstDevice && peer.Authorized &&
                peer.Routes.Any(route => route.Address == $"https://{lanAddress}:{ports[4]}")), "SimpleCandidateRouteNotLearned");
            Pass("normal-friend-prepare-and-start");

            var candidateRun = candidateSnapshot.Runs.Single(item => item.ProfileId == prepared.LocalProfileId);
            LabRequire(candidateRun.RunOperationId is not null, "SimpleCandidateSessionMissing");
            activeCheck = "original-owner-session-connect";
            var ownerConnection = await PostAsync<object, WorldPeerActionResult>(owner,
                $"/api/local/world-peers/{groupKey}/connect", new { deviceId = firstDevice, sessionId = candidateRun.RunOperationId.Value });
            LabRequire(ownerConnection.Ok && ownerConnection.Code == "PeerSessionConnected", "SimpleOwnerSuccessorConnectionRejected");
            activeCheck = "original-owner-authenticated-status";
            await ModeAsync(owner, hostMode: false);
            var ownerView = await WaitForConnectedAsync(owner, allowDisabled: true);
            LabRequire(ownerView.Profiles.Count == 1 && ownerView.Profiles.Single().Id == prepared.LocalProfileId &&
                ownerView.Profiles.Single().CanStop && !ownerView.Profiles.Single().CanStart &&
                !ownerView.Profiles.Single().CanViewLogs && !ownerView.RemoteControlsEnabled,
                "SimpleOriginalOwnerGuardedStopPermissionMissing");
            // The disposable lab permits the passive directory but deliberately
            // excludes the sharing-management POST. This peer row uses the same
            // verified cached roster; the exact sharing projection has separate
            // source coverage without widening the packaged lab's allowlist.
            activeCheck = "original-owner-signed-world-directory";
            var directory = await PairingLabGetAsync<ServerDirectoryView>(owner, "/api/local/servers");
            var originalRow = directory.LocalServers.Single(item => item.Id == profile.Id);
            var successorRow = directory.FriendHosts.SelectMany(item => item.Servers).Single(item => item.Id == prepared.LocalProfileId);
            LabRequire(originalRow.SharedWorldId == groupKey && successorRow.SharedWorldId == groupKey &&
                originalRow.OriginalProfileId == profile.Id && successorRow.OriginalProfileId == profile.Id &&
                !originalRow.IsSuccessor && successorRow.IsSuccessor,
                "SimpleOwnerDirectorySplitOneSignedWorld");
            activeCheck = "original-owner-controls-disabled-stop";
            var disabledStop = await FriendActionAsync(owner, prepared.LocalProfileId.Value, "stop");
            LabRequire(!disabledStop.Ok && disabledStop.Code == "RemoteControlsDisabled", "SimpleOwnerBypassedRemoteControlsOff");
            await ModeAsync(first, hostMode: true);
            activeCheck = "candidate-enable-remote-controls";
            LabRequire((await PutAsync<HostControlPolicyChange, ActionResult>(first, "/api/local/settings/control-policy",
                new(RemoteControlsEnabled: true))).Ok, "SimpleCandidateRemoteControlsEnableRejected");
            try
            {
                activeCheck = "original-owner-untrusted-count-status";
                ownerView = await WaitForConnectedAsync(owner);
                LabRequire(ownerView.Profiles.Single().CanStop && !ownerView.Profiles.Single().CanStopNow &&
                    ownerView.Profiles.Single().State == "Process running", "SimpleFixtureBecameTrustedEmptyGame");
                activeCheck = "original-owner-untrusted-count-stop";
                var countBlockedStop = await FriendActionAsync(owner, prepared.LocalProfileId.Value, "stop");
                LabRequire(!countBlockedStop.Ok && countBlockedStop.Code == "ServerNotReady", "SimpleOwnerBypassedUntrustedFixtureCount");
                var unchangedRun = (await PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot")).Runs
                    .Single(item => item.ProfileId == prepared.LocalProfileId);
                LabRequire(unchangedRun.ProcessId == candidateRun.ProcessId && unchangedRun.RunOperationId == candidateRun.RunOperationId,
                    "SimpleBlockedOwnerStopChangedManagedRun");
            }
            finally
            {
                LabRequire((await PutAsync<HostControlPolicyChange, ActionResult>(first, "/api/local/settings/control-policy",
                    new(RemoteControlsEnabled: false))).Ok, "SimpleCandidateRemoteControlsResetRejected");
                await ModeAsync(first, hostMode: false);
                await ModeAsync(owner, hostMode: true);
            }
            Pass("original-owner-stop-keeps-controls-and-count-gates");

            // Keep the original offline until the listener-less receiver holds
            // the descendant, so a direct producer transfer cannot satisfy the relay check.
            await host.StopOwnedAsync();
            LabRequire(host.HasExitedNow(), "SimpleOriginalAppDidNotExit");
            var candidateProfile = candidateSnapshot.Settings.Profiles.Single(item => item.Id == prepared.LocalProfileId);
            LabRequire(AppInstance.ContainsPath(firstData, candidateProfile.WorldDirectory), "SimpleCandidateWorldEscaped");
            await File.WriteAllTextAsync(Path.Combine(candidateProfile.WorldDirectory, "world.dat"), continuedBytes);
            await ModeAsync(first, hostMode: true);
            LabRequire((await PostAsync<object, ActionResult>(first,
                $"/api/local/profiles/{prepared.LocalProfileId}/stop", new { })).Ok, "SimpleCandidateStopRejected");
            await ModeAsync(first, hostMode: false);
            continuedVersion = FriendLink.ReadReceivedLatest(Path.Combine(firstData, "shared-world-separate-published",
                profile.Id.ToString("N"), prepared.BranchHash));
            LabRequire(continuedVersion is not null && continuedVersion.Number > initialVersion!.Number &&
                continuedVersion.VersionHash != initialVersion.VersionHash, "SimpleDescendantNotPublished");
            var secondVault = Path.Combine(secondData, "received-shared-branches", groupKey, prepared.BranchHash);
            await PairingLabWaitAsync(async () =>
            {
                await SyncAsync(second);
                return FriendLink.ReadReceivedLatest(secondVault)?.VersionHash == continuedVersion!.VersionHash;
            }, "SimpleReceiverDidNotCopyDescendant", TimeSpan.FromSeconds(90));
            RequireCopy(secondVault, continuedVersion!, continuedBytes);
            var secondSettings = (await SnapshotInHostModeAsync(second)).Settings;
            LabRequire(!secondSettings.CompanionListeningEnabled &&
                !(await PairingLabGetAsync<JsonElement>(second, "/api/local/companion")).GetProperty("listenerActive").GetBoolean(),
                "SimpleReceiverUnexpectedListenerConsent");
            await candidate.StopOwnedAsync();
            LabRequire(candidate.HasExitedNow(), "SimpleProducerAppDidNotExit");
            Pass("listener-less-receiver-verifies-descendant");

            host = Launch("host-returned", "--host", ports[0], hostData);
            await WaitLocalAsync(ports[0]);
            RequireReady(host, ports[0]);
            await PairingLabWaitAsync(async () => (await PairingLabGetAsync<JsonElement>(owner,
                "/api/local/companion")).GetProperty("listenerActive").GetBoolean(), "SimpleReturningListenerUnavailable");
            WindowsListenerOwners.RequireTogetherServerOwner(ports[3], host.Process);
            var ownerVault = Path.Combine(hostData, "received-shared-branches", groupKey, prepared.BranchHash);
            await PairingLabWaitAsync(async () =>
            {
                await SyncAsync(second);
                return FriendLink.ReadReceivedLatest(ownerVault)?.VersionHash == continuedVersion!.VersionHash;
            }, "SimpleOutboundSeedNotReceived", TimeSpan.FromSeconds(90));
            RequireCopy(ownerVault, continuedVersion!, continuedBytes);
            LabRequire(!(await SnapshotInHostModeAsync(second)).Settings.CompanionListeningEnabled && candidate.HasExitedNow() &&
                await File.ReadAllTextAsync(Path.Combine(profile.WorldDirectory, "world.dat")) == initialBytes,
                "SimpleRelayUsedProducerOrChangedOriginal");
            Pass("outbound-only-peer-seeds-returning-owner");
            ActionResult? returnedStart = null;
            await PairingLabWaitAsync(async () =>
            {
                ownerStartAttempts++;
                returnedStart = await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/start", new { });
                LabRequire(returnedStart.Ok || returnedStart.Code is "LatestSaveChanged" or "NewerSharedSaveAvailable",
                    "SimpleReturningOwnerStartRejected-" + returnedStart.Code);
                return returnedStart.Ok;
            }, "SimpleReturningOwnerStartDidNotStabilize", TimeSpan.FromSeconds(45));
            var returned = returnedStart ?? throw new PairingLabFailure("SimpleReturningOwnerResultMissing");
            var returnedRun = returned.Snapshot.Runs.Single(item => item.ProcessId is not null);
            await TrackFixtureAsync(returnedRun, owner);
            var returnedProfile = returned.Snapshot.Settings.Profiles.Single(item => item.Id == returnedRun.ProfileId);
            LabRequire(returnedProfile.SeparateCopySourceProfileId == profile.Id && returnedProfile.Id != profile.Id &&
                AppInstance.ContainsPath(hostData, returnedProfile.WorldDirectory) &&
                await File.ReadAllTextAsync(Path.Combine(returnedProfile.WorldDirectory, "world.dat")) == continuedBytes &&
                await File.ReadAllTextAsync(Path.Combine(profile.WorldDirectory, "world.dat")) == initialBytes,
                "SimpleReturningOwnerStartedOlderProgress");
            LabRequire((await PostAsync<object, ActionResult>(owner,
                $"/api/local/profiles/{returnedProfile.Id}/stop", new { })).Ok, "SimpleReturnedOwnerStopRejected");
            Pass("outbound-relay-and-normal-owner-latest-start");

            LabRequire((await PutAsync<object, SharedWorldResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/hosting", new { enabled = false })).Ok,
                "SimplePolicyDisableRejected");
            var disabled = await PairingLabGetAsync<SharedWorldRoster>(owner, $"/api/local/profiles/{profile.Id}/shared-world/governance");
            LabRequire(disabled.Members.Count == 2 && disabled.Members.All(member =>
                !member.Grants.Receive && !member.Grants.EligibleHost && !member.Grants.Reshare), "SimpleDisableKeptSignedRights");
            await SyncAsync(second);
            var disabledGroup = (await PairingLabGetAsync<WorldPeerGroupView[]>(second, "/api/local/world-peers")).Single(item => item.GroupKey == groupKey);
            LabRequire(!disabledGroup.CanHost && !disabledGroup.CanReceive && !disabledGroup.CanReshare,
                "SimpleReceiverKeptDisabledPolicy");
            Pass("global-off-withdraws-signed-rights");
        }
        catch (Exception error)
        {
            failed = error is PairingLabFailure lab ? lab.Code : error.GetType().Name;
            failedCheck = activeCheck;
            if (error is JsonException json) { failedJsonLine = json.LineNumber; failedJsonByte = json.BytePositionInLine; }
            throw new InvalidOperationException($"Simple shared-hosting journey failed: {failed} at {failedCheck}. Evidence: {root}");
        }
        finally
        {
            foreach (var item in new[] { (Client: owner, Process: host), (Client: first, Process: candidate), (Client: second, Process: receiver) })
            {
                if (item.Process is null || item.Process.HasExitedNow() || !item.Process.Matches()) continue;
                try
                {
                    await ModeAsync(item.Client, hostMode: true);
                    foreach (var run in (await PairingLabGetAsync<HostSnapshot>(item.Client, "/api/local/snapshot")).Runs.Where(run => run.ProcessId is not null))
                        _ = await PostAsync<object, ActionResult>(item.Client, $"/api/local/profiles/{run.ProfileId}/stop", new { });
                }
                catch (Exception error) { Console.Error.WriteLine("Simple hosting graceful cleanup unavailable: " + error.GetType().Name); }
            }
            foreach (var process in processes.AsEnumerable().Reverse()) await process.StopOwnedAsync();
            await job.StopAsync();
            var clean = job.ActiveCount() == 0;
            JsonElement? latestReviewDiagnostic = null;
            var diagnosticFile = Path.Combine(hostData, "simple-hosting-latest-review.json");
            if (failed is not null && File.Exists(diagnosticFile) && new FileInfo(diagnosticFile).Length <= 16_384)
            {
                try { latestReviewDiagnostic = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(diagnosticFile)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            }
            await File.WriteAllTextAsync(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new
            {
                schema = 1,
                kind = "HostedSimpleSharedHosting",
                durationMs = timer.ElapsedMilliseconds,
                stages,
                failed,
                failedCheck,
                failedJsonLine,
                failedJsonByte,
                cleanup = new { jobEmpty = clean },
                sourceVersionHash = initialVersion?.VersionHash,
                sourceVersionNumber = initialVersion?.Number,
                descendantVersionHash = continuedVersion?.VersionHash,
                descendantVersionNumber = continuedVersion?.Number,
                friendStartAttempts,
                ownerStartAttempts,
                latestReviewDiagnostic,
                processes = processes.Select(process => process.Report()).ToArray(),
                acceptance = new[] { "SameRunnerPrivateAddressOnly", "PackagedPinnedSyntheticFlow", "NoRealGameOrPhysicalFriendPcAcceptance" }
            }, new JsonSerializerOptions(Json) { WriteIndented = true }));
            foreach (var process in processes) process.Process.Dispose();
            LabRequire(clean, "SimpleJourneyCleanupIncomplete");
        }

        PairingLabProcess Launch(string role, string mode, int port, string data)
        {
            var process = StartPairingLabApp(appPath, mode, port, data, lanAddress);
            job.Add(process);
            var owned = new PairingLabProcess(process, role, appPath);
            processes.Add(owned);
            return owned;
        }
        static void RequireReady(PairingLabProcess owned, int port)
        {
            // Windows may not expose MainModule immediately after CreateProcess.
            // Check the recorded identity after HTTP startup, as the existing lab
            // does, and bind the responding listener to that exact process.
            owned.Sample();
            LabRequire(owned.Matches(), "SimpleAppIdentityMismatch-" + owned.Role);
            WindowsListenerOwners.RequireTogetherServerOwner(port, owned.Process);
        }
        async Task TrackFixtureAsync(RunView initial, HttpClient client)
        {
            LabRequire(initial.ProcessId is not null, "SimpleFixtureIdentityMissing");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            RunView? matched = null;
            try
            {
                // Start may return before Windows exposes MainModule. Observe
                // only its exact run; no Start or replacement PID is accepted.
                while (matched is null)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local/snapshot");
                    request.Headers.Add("X-TogetherServer-Local", "1");
                    using var response = await client.SendAsync(request, deadline.Token);
                    response.EnsureSuccessStatusCode();
                    var snapshot = await response.Content.ReadFromJsonAsync<HostSnapshot>(Json, deadline.Token);
                    var run = snapshot?.Runs.SingleOrDefault(item => item.ProfileId == initial.ProfileId);
                    LabRequire(run is not null && run.ProcessId == initial.ProcessId &&
                        (initial.RunOperationId is null || run.RunOperationId is null || run.RunOperationId == initial.RunOperationId),
                        "SimpleFixtureIdentityChanged");
                    LabRequire(run.State != "Failed", "SimpleFixtureExited");
                    if (run.State == "Process running" && run.RunOperationId is { } operationId && operationId != Guid.Empty)
                        matched = run;
                    else await Task.Delay(100, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            { throw new PairingLabFailure("SimpleFixtureIdentityNotObserved"); }
            var owned = new PairingLabProcess(Process.GetProcessById(matched.ProcessId!.Value), "fixture", fixturePath);
            processes.Add(owned);
            LabRequire(owned.Matches(), "SimpleFixtureIdentityMismatch");
        }
        void Pass(string stage) { stages.Add(stage); activeCheck = stage; Console.WriteLine("PASS simple shared hosting: " + stage); }
        async Task ConfigureFriendAsync(HttpClient client, int companionPort)
        {
            await ModeAsync(client, hostMode: true);
            LabRequire((await PutAsync<HostSettings, ActionResult>(client, "/api/local/settings", new()
            {
                Profiles = [],
                MaxConcurrentServers = 2,
                CompanionBindAddress = lanAddress,
                CompanionEndpoint = $"https://{lanAddress}:{companionPort}",
                CompanionPort = companionPort,
                CompanionListeningEnabled = false,
                RemoteControlsEnabled = false
            })).Ok, "SimpleFriendSettingsRejected");
            await ModeAsync(client, hostMode: false);
        }
        async Task<HostSnapshot> SnapshotInHostModeAsync(HttpClient client)
        {
            await ModeAsync(client, hostMode: true);
            try { return await PairingLabGetAsync<HostSnapshot>(client, "/api/local/snapshot"); }
            finally { await ModeAsync(client, hostMode: false); }
        }
        async Task SyncAsync(HttpClient client)
        {
            LabRequire(groupKey is not null, "SimpleWorldRootMissing");
            LabRequire((await PostAsync<object, WorldPeerActionResult>(client,
                $"/api/local/world-peers/{groupKey}/sync", new { })).Ok, "SimplePeerSyncRejected");
        }
        static async Task ModeAsync(HttpClient client, bool hostMode)
        {
            LabRequire((await PostAsync<object, JsonElement>(client,
                hostMode ? "/api/local/mode/host" : "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(),
                "SimpleModeChangeRejected");
        }
        static void RequireCopy(string vault, SharedWorldVersion expected, string bytes)
        {
            LabRequire(FriendLink.ReadReceivedLatest(vault)?.VersionHash == expected.VersionHash && SharedWorldService.VerifySignature(expected),
                "SimpleCopyIdentityMismatch");
            foreach (var file in expected.Files)
                SharedWorldService.VerifyFile(SharedWorldService.SafeChild(Path.Combine(vault, expected.VersionHash, "payload"), file.Path), file);
            LabRequire(File.ReadAllText(Path.Combine(vault, expected.VersionHash, "payload", "world.dat")) == bytes,
                "SimpleCopiedProgressMismatch");
        }
    }
}
