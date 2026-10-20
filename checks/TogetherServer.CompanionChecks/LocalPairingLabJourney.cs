using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

internal static partial class CoreRemoteJourney
{
    private sealed record PairingLabStage(string Id, string State = "NotRun", string? Code = null, long DurationMs = 0);
    private sealed record PairingLabDevices(IReadOnlyList<DeviceView> Devices);
    private sealed class PairingLabFailure(string code) : Exception
    {
        internal string Code { get; } = code;
    }

    // This is a process/API lab, not native UI, network-route or real-game acceptance.
    // Only the app's guarded backend-only lab mode and the pipe-only Fixture run.
    public static async Task RunLocalPairingLabAsync(string appPath, string fixturePath, string outputRoot)
    {
        var runId = Guid.NewGuid();
        var root = Path.GetFullPath(Path.Combine(outputRoot, "run-" + runId.ToString("N")));
        var allowedRoot = Path.GetFullPath(Path.Combine("local-data", "local-pairing-lab"));
        LabRequire(AppInstance.ContainsPath(allowedRoot, root), "LabOutputBoundary");
        LabRequire(File.Exists(appPath) && File.Exists(fixturePath) &&
            Path.GetFileName(fixturePath).Equals("TogetherServer.Fixture.exe", StringComparison.OrdinalIgnoreCase), "LabBinariesRequired");
        Directory.CreateDirectory(root);
        var startedUtc = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        using var job = new LocalPairingLabJob();
        var stages = new[] { "isolated-apps", "pinned-pairing", "separate-permissions-and-consent", "signed-chat",
            "synthetic-stop-copy-receipts", "password-permission-revoke", "password-reveal", "guided-hosting",
            "local-copy-hosting", "new-host-notice", "lost-response-cleanup", "cleanup" }.Select(id => new PairingLabStage(id)).ToList();
        var processes = new List<PairingLabProcess>();
        var processGate = new object();
        var runOperations = new List<Guid>();
        var appPorts = new[] { FreeTcpPort() }.ToList();
        while (appPorts.Count < 5) appPorts.Add(FreeTcpPort([.. appPorts]));
        var hostPort = appPorts[0];
        var friendPort = appPorts[1];
        var observerPort = appPorts[2];
        var hostData = Path.Combine(root, "host");
        var friendData = Path.Combine(root, "friend-a");
        var observerData = Path.Combine(root, "friend-b");
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture, Name = "Local pairing lab", ServerName = "Synthetic lab",
            WorldId = "lab-world", WorldSource = "New", WorldDirectory = Path.Combine(hostData, "worlds", "lab-world"),
            ExecutablePath = Path.GetFullPath(fixturePath), GamePort = FreeUdpPair(),
            Backups = new() { Enabled = false, MinimumFreeSpaceMb = 0 }
        };
        using var owner = LocalClient(hostPort);
        using var first = LocalClient(friendPort);
        using var second = LocalClient(observerPort);
        foreach (var client in new[] { owner, first, second }) client.DefaultRequestHeaders.Add("X-TogetherServer-Local", "1");
        Guid deviceA = default, deviceB = default, connectionA = default;
        Guid? localProfileId = null;
        LocalHostingStatus? prepared = null;
        var hostedCopy = false;
        string? failureCode = null;
        string? activeStage = null;
        long peakWorkingSet = 0;
        int peakProcessCount = 0;
        int windowObservationFailure = 0, windowObservationSamples = 0, peakVisibleOwnedWindows = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = SampleAsync();

        async Task Stage(string id, Func<Task<string?>> action)
        {
            activeStage = id;
            var index = stages.FindIndex(stage => stage.Id == id);
            var timer = Stopwatch.StartNew();
            try
            {
                var skip = await action();
                if (id != "cleanup") LabRequire(Volatile.Read(ref windowObservationFailure) == 0, "VisibleOwnedWindowObserved");
                stages[index] = new(id, skip is null ? "Passed" : "Skipped", skip, timer.ElapsedMilliseconds);
                Console.WriteLine($"{(skip is null ? "PASS" : "SKIP")} local pairing lab: {id}{(skip is null ? "" : " (" + skip + ")")}");
            }
            catch (Exception error)
            {
                var windowFailure = Volatile.Read(ref windowObservationFailure);
                var code = windowFailure != 0 ? windowFailure == 1 ? "VisibleOwnedWindowObserved" : "OwnedWindowObservationUnavailable" :
                    error is PairingLabFailure assertion ? assertion.Code : error.GetType().Name;
                stages[index] = new(id, "Failed", code, timer.ElapsedMilliseconds);
                throw new PairingLabFailure(code);
            }
        }

        PairingLabProcess Track(Process process, string role, string executable)
        {
            if (role != "fixture") job.Add(process);
            var item = new PairingLabProcess(process, role, executable);
            lock (processGate) processes.Add(item);
            return item;
        }

        void TrackRun(RunView run)
        {
            LabRequire(run.ProcessId is not null && run.RunOperationId is not null, "FixtureRunIdentityMissing");
            var item = Track(Process.GetProcessById(run.ProcessId!.Value), "fixture", fixturePath);
            LabRequire(item.Matches(), "FixtureRunIdentityMismatch");
            runOperations.Add(run.RunOperationId!.Value);
        }

        async Task SampleAsync()
        {
            try
            {
                while (!sampling.IsCancellationRequested)
                {
                    PairingLabProcess[] owned;
                    lock (processGate)
                    {
                        foreach (var process in processes) process.Sample();
                        peakWorkingSet = Math.Max(peakWorkingSet, processes.Sum(process => process.CurrentWorkingSetBytes));
                        peakProcessCount = Math.Max(peakProcessCount, processes.Count(process => process.Alive));
                        owned = processes.Where(process => process.Alive).ToArray();
                    }
                    if (owned.Length > 0)
                    {
                        var counts = owned.ToDictionary(process => process.Id, _ => 0);
                        var observed = PairingLabEnumWindows((window, parameter) =>
                        {
                            PairingLabGetWindowThreadProcessId(window, out var processId);
                            if (counts.ContainsKey((int)processId) && PairingLabIsWindowVisible(window)) counts[(int)processId]++;
                            return true;
                        }, IntPtr.Zero);
                        windowObservationSamples++;
                        peakVisibleOwnedWindows = Math.Max(peakVisibleOwnedWindows, counts.Values.Sum());
                        foreach (var process in owned) process.ObserveWindows(counts[process.Id]);
                        if (!observed || counts.Values.Any(count => count > 0))
                        {
                            Interlocked.Exchange(ref windowObservationFailure, observed ? 1 : 2);
                            await job.StopAsync();
                            foreach (var process in owned.OrderBy(process => process.Role == "fixture" ? 0 : 1))
                                await process.StopOwnedAsync();
                            return;
                        }
                    }
                    await Task.Delay(250, sampling.Token);
                }
            }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        }

        try
        {
            await Stage("isolated-apps", async () =>
            {
                Track(StartPairingLabApp(appPath, "--host", hostPort, hostData), "host", appPath);
                Track(StartPairingLabApp(appPath, "--friend", friendPort, friendData), "friend-a", appPath);
                Track(StartPairingLabApp(appPath, "--friend", observerPort, observerData), "friend-b", appPath);
                await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(friendPort), WaitLocalAsync(observerPort));
                lock (processGate)
                    foreach (var process in processes) LabRequire(process.Matches(), "AppIdentityMismatch");
                WindowsListenerOwners.RequireTogetherServerOwner(hostPort, processes[0].Process);
                WindowsListenerOwners.RequireTogetherServerOwner(friendPort, processes[1].Process);
                WindowsListenerOwners.RequireTogetherServerOwner(observerPort, processes[2].Process);
                Directory.CreateDirectory(profile.WorldDirectory);
                LabRequire((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", new()
                {
                    Profiles = [profile], MaxConcurrentServers = 1, CompanionBindAddress = "127.0.0.1",
                    CompanionEndpoint = $"https://127.0.0.1:{appPorts[3]}", CompanionPort = appPorts[3]
                })).Ok, "HostSettingsRejected");
                // A candidate's incoming listener is deliberate and remains loopback-only.
                LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "CandidateHostModeRejected");
                LabRequire((await PutAsync<HostSettings, ActionResult>(first, "/api/local/settings", new()
                {
                    Profiles = [], CompanionBindAddress = "127.0.0.1", CompanionPort = appPorts[4],
                    CompanionEndpoint = $"https://127.0.0.1:{appPorts[4]}", CompanionListeningEnabled = false
                })).Ok, "CandidateSettingsRejected");
                LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(), "CandidateFriendModeRejected");
                LabRequire(!Directory.Exists(hostData + "-production-unused") && !Directory.Exists(friendData + "-production-unused") &&
                    !Directory.Exists(observerData + "-production-unused"), "ProductionSentinelTouched");
                return null;
            });

            await Stage("pinned-pairing", async () =>
            {
                var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner,
                    $"/api/local/servers/{profile.Id}/invite", new(false, false, true));
                LabRequire(invite.GetProperty("ok").GetBoolean() && invite.GetProperty("listenerActive").GetBoolean(), "InviteListenerUnavailable");
                var code = invite.GetProperty("password").GetString();
                LabRequire(!string.IsNullOrWhiteSpace(code), "InviteMissing");
                LabRequire((await PostAsync<FriendPairRequest, FriendActionResult>(first, "/api/local/friend/pair", new(code!))).Ok, "FirstPairingFailed");
                deviceA = (await PairingLabGetAsync<PairingLabDevices>(owner, "/api/local/companion")).Devices.Single().Id;
                LabRequire((await PostAsync<FriendPairRequest, FriendActionResult>(second, "/api/local/friend/pair", new(code!))).Ok, "SecondPairingFailed");
                var devices = (await PairingLabGetAsync<PairingLabDevices>(owner, "/api/local/companion")).Devices;
                LabRequire(devices.Count == 2, "DistinctDevicesRequired");
                deviceB = devices.Single(device => device.Id != deviceA).Id;
                var a = await WaitForConnectedAsync(first, allowDisabled: true);
                var b = await WaitForConnectedAsync(second, allowDisabled: true);
                connectionA = a.ConnectionId;
                LabRequire(a.LastConnectedUtc is not null && b.LastConnectedUtc is not null && a.HostId == b.HostId &&
                    a.Profiles.Single().Id == profile.Id && b.Profiles.Single().Id == profile.Id, "PinnedAuthenticationNotObserved");
                WindowsListenerOwners.RequireTogetherServerOwner(appPorts[3], processes[0].Process);
                return null;
            });

            await Stage("separate-permissions-and-consent", async () =>
            {
                var devices = (await PairingLabGetAsync<PairingLabDevices>(owner, "/api/local/companion")).Devices;
                LabRequire(devices.All(device => !device.CanViewPassword &&
                    (device.SaveReceiveProfileIds?.Count ?? 0) == 0 &&
                    !(device.SharedWorldGrants?.Values.Any(grants => grants.Receive || grants.EligibleHost) ?? false)), "GrantsDefaultedOn");
                LabRequire((await PutAsync<object, SharedWorldResult>(owner,
                    $"/api/local/profiles/{profile.Id}/shared-world/hosting", new { enabled = true })).Ok, "OwnerSharingRejected");
                var clients = new[] { (Id: deviceA, Client: first), (Id: deviceB, Client: second) };
                foreach (var pc in clients)
                {
                    LabRequire((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                        $"/api/local/devices/{pc.Id}/shared-world/{profile.Id}", new(true))).Ok, "ReceiveGrantRejected");
                    var before = await PairingLabGetAsync<ReceivedSharedWorldStatus>(pc.Client, $"/api/local/friend/{profile.Id}/shared-world");
                    LabRequire(!before.Consented, "RemoteGrantImpliedLocalConsent");
                    LabRequire((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(pc.Client,
                        $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok, "LocalConsentRejected");
                    await PairingLabWaitAsync(async () =>
                    {
                        _ = await PostAsync<object, ReceivedSharedWorldResult>(pc.Client,
                            $"/api/local/friend/{profile.Id}/shared-world/check", new { });
                        using var response = await owner.GetAsync($"/api/local/profiles/{profile.Id}/shared-world/governance");
                        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return false;
                        response.EnsureSuccessStatusCode();
                        var roster = await response.Content.ReadFromJsonAsync<SharedWorldRoster>(Json);
                        return roster?.Members.Any(member => member.DeviceId == pc.Id && !string.IsNullOrWhiteSpace(member.PublicKey)) == true;
                    }, "SigningEnrollmentNotObserved");
                }
                LabRequire((await PutAsync<SharedWorldDeviceGrantsRequest, PairingDecision>(owner,
                    $"/api/local/devices/{deviceA}/shared-world/{profile.Id}/grants", new(new(Receive: true, EligibleHost: true)))).Ok, "EligibleHostGrantRejected");
                var current = (await PairingLabGetAsync<PairingLabDevices>(owner, "/api/local/companion")).Devices;
                LabRequire(current.Single(device => device.Id == deviceA).SharedWorldGrants?.GetValueOrDefault(profile.Id)?.EligibleHost == true &&
                    current.Single(device => device.Id == deviceB).SharedWorldGrants?.GetValueOrDefault(profile.Id)?.EligibleHost != true &&
                    current.All(device => !device.CanViewPassword), "IndependentGrantScopeLost");
                return null;
            });

            await Stage("signed-chat", async () =>
            {
                var sent = await PostAsync<ChatPostRequest, ChatRoomView>(owner,
                    $"/api/local/profiles/{profile.Id}/chat/messages", new("Synthetic lab host message"));
                LabRequire(sent.Ok && sent.Entries.Count == 1, "HostChatRejected");
                var a = await PostAsync<object, ChatRoomView>(first, $"/api/local/friend/{profile.Id}/chat/sync", new { });
                var b = await PostAsync<object, ChatRoomView>(second, $"/api/local/friend/{profile.Id}/chat/sync", new { });
                LabRequire(a.Ok && b.Ok && a.Entries.Single().Id == b.Entries.Single().Id, "SignedChatCopiesDiffer");
                LabRequire((await PostAsync<ChatPostRequest, ChatRoomView>(first,
                    $"/api/local/friend/{profile.Id}/chat/messages", new("Synthetic lab Friend reply"))).Ok, "FriendChatRejected");
                b = await PostAsync<object, ChatRoomView>(second, $"/api/local/friend/{profile.Id}/chat/sync", new { });
                LabRequire(b.Ok && b.Entries.Count == 2, "SecondFriendMissedReply");
                LabRequire((await PutAsync<ChatMemberChange, ChatRoomView>(owner,
                    $"/api/local/profiles/{profile.Id}/chat/members/{deviceA}", new(false))).Ok, "ChatRemovalRejected");
                var denied = await PostAsync<object, ChatRoomView>(first, $"/api/local/friend/{profile.Id}/chat/sync", new { });
                LabRequire(!denied.Ok && denied.Code == "ChatAccessDenied" && denied.Entries.Count == 0, "ChatRemovalNotEnforced");
                LabRequire((await PutAsync<ChatMemberChange, ChatRoomView>(owner,
                    $"/api/local/profiles/{profile.Id}/chat/members/{deviceA}", new(true))).Ok, "ChatReadmissionRejected");
                return null;
            });

            await Stage("synthetic-stop-copy-receipts", async () =>
            {
                var seed = "Synthetic fixture payload " + runId.ToString("N");
                await File.WriteAllTextAsync(Path.Combine(profile.WorldDirectory, "world.dat"), seed);
                var start = await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/start", new { });
                var run = start.Snapshot.Runs.Single(item => item.ProfileId == profile.Id);
                if (run.ProcessId is not null) TrackRun(run);
                LabRequire(start.Ok, "FixtureStartRejected");
                _ = await WaitForRunStateAsync(owner, profile.Id, "Process running");
                LabRequire((await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/stop", new { })).Ok, "FixturePipeStopRejected");
                _ = await WaitForRunStateAsync(owner, profile.Id, "Offline");
                var shared = await PairingLabGetAsync<SharedWorldStatus>(owner, $"/api/local/profiles/{profile.Id}/shared-world");
                LabRequire(shared.Latest is { Number: 1 }, "StoppedSyntheticCopyNotPublished");
                foreach (var pc in new[] { (Client: first, Data: friendData, Device: deviceA), (Client: second, Data: observerData, Device: deviceB) })
                {
                    await PairingLabWaitAsync(async () =>
                    {
                        _ = await PostAsync<object, FriendView>(pc.Client, "/api/local/friend/poll", new { });
                        _ = await PostAsync<object, ReceivedSharedWorldResult>(pc.Client, $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
                        var received = await PairingLabGetAsync<ReceivedSharedWorldStatus>(pc.Client, $"/api/local/friend/{profile.Id}/shared-world");
                        return received.ThisPcVersion == 1 && received.ReceiptConfirmed == true;
                    }, "VerifiedCopyReceiptNotObserved");
                    var payload = Path.Combine(pc.Data, "received-shared-worlds", pc.Device.ToString("N"), profile.Id.ToString("N"), shared.Latest!.VersionHash, "payload", "world.dat");
                    LabRequire(await File.ReadAllTextAsync(payload) == seed, "ReceivedSyntheticBytesDiffer");
                }
                shared = await PairingLabGetAsync<SharedWorldStatus>(owner, $"/api/local/profiles/{profile.Id}/shared-world");
                LabRequire(shared.ConfirmedCopies == 2, "BothSignedReceiptsNotConfirmed");
                return null;
            });

            await Stage("password-permission-revoke", async () =>
            {
                foreach (var allowed in new[] { true, false })
                {
                    LabRequire((await PutAsync<DevicePermissionRequest, PairingDecision>(owner,
                        $"/api/local/devices/{deviceA}/permissions", new(false, false, Scope: "password", CanViewPassword: allowed))).Ok, "PasswordPermissionChangeRejected");
                    var devices = (await PairingLabGetAsync<PairingLabDevices>(owner, "/api/local/companion")).Devices;
                    LabRequire(devices.Single(device => device.Id == deviceA).CanViewPassword == allowed &&
                        !devices.Single(device => device.Id == deviceB).CanViewPassword, "PasswordPermissionScopeLost");
                }
                return null;
            });
            await Stage("password-reveal", async () =>
            {
                var result = await PostAsync<object, GamePasswordResult>(first,
                    $"/api/local/friend/{profile.Id}/game-password/reveal", new { connectionId = connectionA });
                LabRequire(!result.Ok && result.Password is null && result.Code == "GamePasswordUnsupported", "FixturePasswordBoundaryChanged");
                return "ValheimPasswordSourceRequiresSeparateAcceptance";
            });

            await Stage("guided-hosting", async () =>
            {
                prepared = await PostAsync<LocalHostingPrepareRequest, LocalHostingStatus>(first,
                    $"/api/local/friend/{profile.Id}/shared-world/hosting/prepare",
                    new(true, true, ServerFile: Path.GetFullPath(fixturePath)));
                LabRequire(prepared.LocalProfileId is not null && prepared.BranchHash is not null &&
                    prepared.ReadyForManualStart && !prepared.Running, "SyntheticHostingPreparationRejected");
                var exactLocalProfile = prepared.LocalProfileId ?? throw new PairingLabFailure("PreparedProfileMissing");
                localProfileId = exactLocalProfile;
                var before = await CandidateSnapshotAsync();
                LabRequire(before.Runs.All(run => run.ProcessId is null), "PreparationStartedProcess");
                var result = await PostAsync<object, JsonElement>(first,
                    $"/api/local/friend/{profile.Id}/shared-world/hosting/start", new
                    {
                        localProfileId = exactLocalProfile, branchHash = prepared.BranchHash,
                        requestId = (Guid?)null, acceptSplitWarning = true
                    });
                // Ordinary peer-route rules deliberately refuse loopback. Do not bypass them.
                LabRequire(!result.GetProperty("ok").GetBoolean() && prepared.Remedy?.Kind == "Connection" &&
                    result.GetProperty("code").GetString() == "LocalHostingActionRequired", "LoopbackHostingConnectionBlockerMissing");
                var after = await CandidateSnapshotAsync();
                LabRequire(after.Runs.All(run => run.ProcessId is null), "BlockedGuidedStartCreatedProcess");
                return null;
            });

            await Stage("local-copy-hosting", async () =>
            {
                LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "LocalCopyHostModeRejected");
                var started = await PostAsync<object, ActionResult>(first,
                    $"/api/local/profiles/{localProfileId}/shared-world/hosting/start", new { branchHash = prepared!.BranchHash, acceptSplitWarning = true });
                var run = started.Snapshot.Runs.Single(item => item.ProfileId == localProfileId);
                if (run.ProcessId is not null) TrackRun(run);
                LabRequire(started.Ok, "ExplicitLocalCopyStartRejected");
                _ = await WaitForRunStateAsync(first, localProfileId!.Value, "Process running");
                hostedCopy = true;
                LabRequire((await PostAsync<object, ActionResult>(first, $"/api/local/profiles/{localProfileId}/stop", new { })).Ok, "LocalCopyPipeStopRejected");
                _ = await WaitForRunStateAsync(first, localProfileId.Value, "Offline");
                return null;
            });
            await Stage("new-host-notice", () => Task.FromResult<string?>(
                "LoopbackPeerRouteCannotProveNewHostAnnouncementOrConnect"));
            await Stage("lost-response-cleanup", async () =>
            {
                var started = await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/start", new { });
                LabRequire(started.Ok, "CleanupFaultFixtureStartRejected");
                var run = started.Snapshot.Runs.Single(item => item.ProfileId == profile.Id);
                LabRequire(run.ProcessId is not null && run.RunOperationId is not null, "CleanupFaultFixtureIdentityMissing");
                // Deliberately do not add this child to processes, just as when
                // Start succeeds but its HTTP response never reaches tracking.
                using var untracked = Process.GetProcessById(run.ProcessId!.Value);
                LabRequire(!untracked.HasExited && string.Equals(untracked.MainModule?.FileName,
                    Path.GetFullPath(fixturePath), StringComparison.OrdinalIgnoreCase), "CleanupFaultFixtureMismatch");
                runOperations.Add(run.RunOperationId!.Value);
                var stoppedHost = processes.Single(process => process.Role == "host");
                await stoppedHost.StopOwnedAsync();
                LabRequire(!stoppedHost.Alive && !stoppedHost.IdentityReview, "CleanupFaultHostDidNotExit");
                LabRequire(!untracked.HasExited, "CleanupFaultDidNotLeaveChildForJob");
                await job.StopAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await untracked.WaitForExitAsync(deadline.Token);
                LabRequire(untracked.HasExited && job.ActiveCount() == 0, "UntrackedFixtureSurvivedJobCleanup");
                return null;
            });
        }
        catch (Exception error) { failureCode = error is PairingLabFailure assertion ? assertion.Code : error.GetType().Name; }
        finally
        {
            var failedStage = activeStage;
            try
            {
                await Stage("cleanup", async () =>
                {
                    // Ask the exact app to stop its saved fixture before terminating our app processes.
                    await TryStopManagedRunAsync(hostPort, profile.Id, processes.FirstOrDefault(process => process.Role == "host")?.Process);
                    if (localProfileId is { } local)
                        await TryStopManagedRunAsync(friendPort, local, processes.FirstOrDefault(process => process.Role == "friend-a")?.Process);
                    sampling.Cancel();
                    await sampler;
                    await job.StopAsync();
                    foreach (var process in processes.OrderBy(process => process.Role == "fixture" ? 0 : 1))
                        await process.StopOwnedAsync();
                    LabRequire(processes.All(process => !process.Alive && !process.IdentityReview), "OwnedProcessCleanupNeedsReview");
                    LabRequire(job.ActiveCount() == 0, "LabJobCleanupNeedsReview");
                    LabRequire(!Directory.Exists(hostData + "-production-unused") && !Directory.Exists(friendData + "-production-unused") &&
                        !Directory.Exists(observerData + "-production-unused"), "ProductionSentinelTouched");
                    return null;
                });
            }
            catch (Exception error)
            {
                if (failureCode is null) failedStage = "cleanup";
                failureCode ??= error is PairingLabFailure assertion ? assertion.Code : error.GetType().Name;
            }
            sampling.Cancel();
            await sampler;
            if (windowObservationFailure != 0 && failureCode is null)
            {
                failedStage = "window-observation";
                failureCode = windowObservationFailure == 1 ? "VisibleOwnedWindowObserved" : "OwnedWindowObservationUnavailable";
            }
            var report = new
            {
                schema = 1, runId, root, startedUtc, endedUtc = DateTimeOffset.UtcNow, durationMs = clock.ElapsedMilliseconds,
                outcome = failureCode is null ? "CompletedWithSkips" : "Failed", failedStage = failureCode is null ? null : failedStage,
                failureCode, stages, runOperations, syntheticCopyHosted = hostedCopy,
                cleanup = new { jobActiveProcesses = job.ActiveCount(), killRemainingMembersOnHarnessExit = true },
                resources = new { sampledEveryMs = 250, peakProcessCount, peakWorkingSetBytes = peakWorkingSet,
                    currentWorkingSetBytes = processes.Sum(process => process.CurrentWorkingSetBytes) },
                windowObservation = new { sampledEveryMs = 250, samples = windowObservationSamples,
                    state = windowObservationFailure != 0 ? "Failed" : windowObservationSamples > 0 ? "Passed" : "NotObserved",
                    peakVisibleOwnedWindows, observedVisibleWindow = windowObservationFailure == 1,
                    observationUnavailable = windowObservationFailure == 2,
                    scope = "Owned app and fixture top-level windows; sampled observation, no focus or placement calls" },
                ownedProcesses = processes.Select(process => process.Report()).ToArray(),
                acceptance = new[] { "LoopbackOnly", "SyntheticSeededPayloadOnly", "NoGameReadinessOrPlayerCountClaim",
                    "ValheimPasswordRevealUnverified", "NewHostNoticeAndConnectUnverified", "RenderedUiUnverified",
                    "SeparatePcAndNetworkUnverified", "RealGameJoinSaveLoadRestartUnverified" }
            };
            var reportPath = Path.Combine(root, "report.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(Json) { WriteIndented = true }));
            foreach (var process in processes) process.Process.Dispose();
            Console.WriteLine($"Local pairing lab: {stages.Count(stage => stage.State == "Passed")} stages passed, {stages.Count(stage => stage.State == "Failed")} failed, {stages.Count(stage => stage.State == "Skipped")} skipped; {windowObservationSamples} owned-window checks, {peakVisibleOwnedWindows} peak visible windows. Report: {reportPath}");
        }
        if (failureCode is not null) throw new InvalidOperationException($"Local pairing lab failed: {failureCode}. Evidence: {root}");

        async Task<HostSnapshot> CandidateSnapshotAsync()
        {
            LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "CandidateSnapshotModeRejected");
            try { return await PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot"); }
            finally
            {
                LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(), "CandidateSnapshotRestoreRejected");
            }
        }
    }

    private static void LabRequire(bool value, string code)
    {
        if (!value) throw new PairingLabFailure(code);
    }

    private static async Task<T> PairingLabGetAsync<T>(HttpClient client, string path) =>
        await client.GetFromJsonAsync<T>(path, Json) ?? throw new PairingLabFailure("LocalResponseMissing");

    private static async Task PairingLabWaitAsync(Func<Task<bool>> check, string code)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(25))
        {
            if (await check()) return;
            await Task.Delay(250);
        }
        throw new PairingLabFailure(code);
    }

    private static Process StartPairingLabApp(string path, string mode, int port, string data)
    {
        Directory.CreateDirectory(data);
        var start = new ProcessStartInfo(Path.GetFullPath(path))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "--staging", "--local-pairing-lab", mode, "--port", port.ToString() }) start.ArgumentList.Add(argument);
        start.Environment["TOGETHERSERVER_STAGING_DATA_DIR"] = data;
        start.Environment["TOGETHERSERVER_DATA_DIR"] = data + "-production-unused";
        start.Environment["TOGETHERSERVER_FIXTURE_ROOT"] = data;
        start.Environment[GameServerRegistry.FixtureOptInEnvironmentVariable] = "1";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment.Remove("DOTNET_STARTUP_HOOKS");
        start.Environment.Remove("DOTNET_ADDITIONAL_DEPS");
        start.Environment.Remove("DOTNET_SHARED_STORE");
        var process = Process.Start(start) ?? throw new PairingLabFailure("LabAppNotStarted");
        // Drain rather than save raw app output, which can contain private setup details.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private delegate bool PairingLabWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "EnumWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PairingLabEnumWindows(PairingLabWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PairingLabIsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint PairingLabGetWindowThreadProcessId(IntPtr window, out uint processId);

    private sealed class PairingLabProcess
    {
        internal Process Process { get; }
        internal int Id => id;
        internal string Role { get; }
        private readonly string executable;
        private readonly long startedTicks;
        private readonly int id;
        private readonly object sampleGate = new();
        private bool stopping;
        internal bool Alive { get; private set; } = true;
        internal bool IdentityReview { get; private set; }
        internal long CurrentWorkingSetBytes { get; private set; }
        private long peakWorkingSet;
        private long cpuMs;
        private int visibleWindowSamples, peakVisibleWindows;
        private bool resourceSampleUnavailable;

        internal PairingLabProcess(Process process, string role, string executable)
        {
            Process = process; Role = role; this.executable = Path.GetFullPath(executable);
            id = process.Id;
            try { startedTicks = process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            { IdentityReview = true; }
            Sample();
        }

        internal bool Matches()
        {
            lock (sampleGate) return MatchesCore();
        }
        private bool MatchesCore() => !Process.HasExited && Process.StartTime.ToUniversalTime().Ticks == startedTicks &&
            string.Equals(Process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);

        internal void Sample()
        {
            lock (sampleGate)
                if (!stopping) SampleCore();
        }

        private void SampleCore()
        {
            try
            {
                Process.Refresh();
                Alive = !Process.HasExited;
                CurrentWorkingSetBytes = Alive ? Process.WorkingSet64 : 0;
                if (Alive)
                {
                    peakWorkingSet = Math.Max(peakWorkingSet, Process.PeakWorkingSet64);
                    cpuMs = (long)Process.TotalProcessorTime.TotalMilliseconds;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            { CurrentWorkingSetBytes = 0; resourceSampleUnavailable = true; }
        }

        internal void ObserveWindows(int count)
        {
            visibleWindowSamples++;
            peakVisibleWindows = Math.Max(peakVisibleWindows, count);
        }

        internal async Task StopOwnedAsync()
        {
            try
            {
                lock (sampleGate)
                {
                    SampleCore();
                    if (!Alive) return;
                    if (!MatchesCore()) { IdentityReview = true; return; }
                    stopping = true;
                    Process.Kill();
                }
                // WaitForExitAsync also drains redirected pipes. A deliberately
                // orphaned fixture can keep its parent's pipe handles open;
                // process termination must be checked independently of EOF.
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    lock (sampleGate)
                        if (Process.HasExited) return;
                    await Task.Delay(100);
                }
                IdentityReview = true;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            { IdentityReview = true; }
            finally
            {
                lock (sampleGate) { stopping = false; SampleCore(); }
            }
        }

        internal object Report() => new { role = Role, processId = id, startedUtcTicks = startedTicks,
            alive = Alive, identityReview = IdentityReview, currentWorkingSetBytes = CurrentWorkingSetBytes,
            peakWorkingSetBytes = peakWorkingSet, observedCpuMs = cpuMs, resourceSampleUnavailable,
            visibleWindowSamples, peakVisibleWindows };
    }
}
