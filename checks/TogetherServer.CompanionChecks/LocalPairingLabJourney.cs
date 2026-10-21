using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

internal static partial class CoreRemoteJourney
{
    private sealed record PairingLabStage(string Id, string State = "NotRun", string? Code = null, long DurationMs = 0,
        IReadOnlyList<PairingLabOperation>? Operations = null);
    private sealed record PairingLabOperation(string Id, string State, long StartedMs, long DurationMs, long TimeoutMs,
        string? Code = null, string? ExceptionType = null, string? InnerExceptionType = null, bool HttpClientTimeout = false);
    private sealed record PairingLabDevices(IReadOnlyList<DeviceView> Devices);
    private sealed record PairingLabTransferObservation(int Attempt, long CandidateSyncMs, long ObserverSyncMs,
        bool CandidateHeadAdvertised, bool CandidateClaimsPayload, bool ObserverReceiveConsented, bool ObserverCanReceive,
        bool ObserverSeesHead, bool ObserverSeesPayload, bool ObserverSeesRoute, bool CandidateHasWarning,
        bool ObserverHasWarning, bool ObserverHasConflicts, bool PointerMatched);
    private sealed class PairingLabFailure(string code) : Exception
    {
        internal string Code { get; } = code;
    }

    // Independent app identities exercise real pinned TLS on one Windows PC.
    // Only the guarded backend and pipe-only Fixture run; physical-PC reachability
    // and real-game acceptance remain outside this rehearsal.
    public static async Task RunLocalPairingLabAsync(string appPath, string fixturePath, string outputRoot,
        string? lanAddress = null, bool runUx = false)
    {
        LabRequire(!runUx || lanAddress is not null, "LanRequiredForTwoEndedUx");
        if (lanAddress is not null)
        {
            LabRequire(IPAddress.TryParse(lanAddress, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                address.ToString() == lanAddress && NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Any(item => item.Address.Equals(address)), "AssignedLabLanAddressRequired");
            var octets = IPAddress.Parse(lanAddress).GetAddressBytes();
            LabRequire(octets[0] == 10 || octets[0] == 172 && octets[1] is >= 16 and <= 31 ||
                octets[0] == 192 && octets[1] == 168, "PrivateLabLanAddressRequired");
        }
        var transportAddress = lanAddress ?? "127.0.0.1";
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
        var stageNames = new List<string> { "isolated-apps", "pinned-pairing", "separate-permissions-and-consent", "signed-chat",
            "synthetic-stop-copy-receipts", "password-permission-revoke", "unsupported-password-source-denied", "guided-hosting",
            "local-copy-hosting" };
        if (lanAddress is not null) stageNames.AddRange(["new-host-notice", "new-host-authenticated-connect", "new-host-permission-isolation", "new-host-stop-and-transfer",
            "new-host-restart-reconnect", "new-host-revocation-isolation"]);
        stageNames.Add("lost-response-cleanup");
        if (lanAddress is not null) stageNames.Add("new-host-receipt-confirmed");
        stageNames.Add("cleanup");
        if (runUx) stageNames = ["isolated-apps", "two-ended-ux", "cleanup"];
        var stages = stageNames.Select(id => new PairingLabStage(id)).ToList();
        var processes = new List<PairingLabProcess>();
        var processGate = new object();
        var runOperations = new List<Guid>();
        var appPorts = new[] { FreeTcpPort() }.ToList();
        while (appPorts.Count < (lanAddress is null ? 5 : 6)) appPorts.Add(FreeTcpPort([.. appPorts]));
        var hostPort = appPorts[0];
        var friendPort = appPorts[1];
        var observerPort = appPorts[2];
        var hostData = Path.Combine(root, "host");
        var friendData = Path.Combine(root, "friend-a");
        var observerData = Path.Combine(root, "friend-b");
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            Name = "Local pairing lab",
            ServerName = "Synthetic lab",
            WorldId = "lab-world",
            WorldSource = "New",
            WorldDirectory = Path.Combine(hostData, "worlds", "lab-world"),
            ExecutablePath = Path.GetFullPath(fixturePath),
            GamePort = FreeUdpPair(),
            Backups = new() { Enabled = false, MinimumFreeSpaceMb = 0 }
        };
        using var owner = LocalClient(hostPort);
        using var first = LocalClient(friendPort);
        using var second = LocalClient(observerPort);
        foreach (var client in new[] { owner, first, second }) client.DefaultRequestHeaders.Add("X-TogetherServer-Local", "1");
        Guid deviceA = default, deviceB = default, connectionA = default, originalHostId = default;
        Guid? localProfileId = null;
        LocalHostingStatus? prepared = null;
        WorldSeparateCopyBranch? preparedBranch = null;
        SharedWorldRoster? signedRoster = null;
        string? groupKey = null, receivedBranchVersionHash = null;
        string? candidateSessionState = null;
        Guid? candidateSessionId = null;
        Guid ownerAlias = default, observerAlias = default;
        var discoveredHost = false;
        var connectedOwner = false;
        var connectedObserver = false;
        var transferredCandidateCopy = false;
        var restartedHostConnections = false;
        var isolatedRevocation = false;
        var transferObservations = new List<PairingLabTransferObservation>();
        var guidedOperations = new List<PairingLabOperation>();
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
                stages[index] = new(id, skip is null ? "Passed" : "Skipped", skip, timer.ElapsedMilliseconds,
                    id == "guided-hosting" ? guidedOperations.ToArray() : null);
                Console.WriteLine($"{(skip is null ? "PASS" : "SKIP")} local pairing lab: {id}{(skip is null ? "" : " (" + skip + ")")}");
            }
            catch (Exception error)
            {
                var windowFailure = Volatile.Read(ref windowObservationFailure);
                var code = windowFailure != 0 ? windowFailure == 1 ? "VisibleOwnedWindowObserved" : "OwnedWindowObservationUnavailable" :
                    error is PairingLabFailure assertion ? assertion.Code : error.GetType().Name;
                stages[index] = new(id, "Failed", code, timer.ElapsedMilliseconds,
                    id == "guided-hosting" ? guidedOperations.ToArray() : null);
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

        async Task<RunView> WaitForFixtureIdentityAsync(RunView initial, Func<Task<HostSnapshot>> snapshot)
        {
            LabRequire(initial.ProcessId is not null, "FixtureRunIdentityMissing");
            RunView? matched = null;
            // Start can return a PID before Windows exposes its main module.
            // Poll only this run; never repeat Start or adopt a replacement PID.
            await PairingLabWaitAsync(async () =>
            {
                var run = (await snapshot()).Runs.Single(item => item.ProfileId == initial.ProfileId);
                LabRequire(run.ProcessId == initial.ProcessId &&
                    (initial.RunOperationId is null || run.RunOperationId is null || run.RunOperationId == initial.RunOperationId),
                    "FixtureRunIdentityChanged");
                if (run.State != "Process running" || run.RunOperationId is not { } operationId || operationId == Guid.Empty) return false;
                matched = run;
                return true;
            }, "FixtureRunIdentityNotObserved", TimeSpan.FromSeconds(10));
            return matched!;
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
                        var contained = job.ProcessIds();
                        long containedWorkingSet = 0;
                        foreach (var id in contained)
                        {
                            try { using var member = Process.GetProcessById(id); containedWorkingSet += member.WorkingSet64; }
                            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                        }
                        peakWorkingSet = Math.Max(peakWorkingSet, containedWorkingSet);
                        peakProcessCount = Math.Max(peakProcessCount, contained.Length);
                        var counts = contained.ToDictionary(id => id, _ => 0);
                        var observed = PairingLabEnumWindows((window, parameter) =>
                        {
                            PairingLabGetWindowThreadProcessId(window, out var processId);
                            if (counts.ContainsKey((int)processId) && PairingLabIsWindowVisible(window)) counts[(int)processId]++;
                            return true;
                        }, IntPtr.Zero);
                        windowObservationSamples++;
                        peakVisibleOwnedWindows = Math.Max(peakVisibleOwnedWindows, counts.Values.Sum());
                        foreach (var process in owned) process.ObserveWindows(counts.GetValueOrDefault(process.Id));
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
                Track(StartPairingLabApp(appPath, "--host", hostPort, hostData, lanAddress), "host", appPath);
                Track(StartPairingLabApp(appPath, "--friend", friendPort, friendData, lanAddress), "friend-a", appPath);
                if (!runUx) Track(StartPairingLabApp(appPath, "--friend", observerPort, observerData, lanAddress), "friend-b", appPath);
                await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(friendPort), runUx ? Task.CompletedTask : WaitLocalAsync(observerPort));
                lock (processGate)
                    foreach (var process in processes) LabRequire(process.Matches(), "AppIdentityMismatch");
                WindowsListenerOwners.RequireTogetherServerOwner(hostPort, processes[0].Process);
                WindowsListenerOwners.RequireTogetherServerOwner(friendPort, processes[1].Process);
                if (!runUx) WindowsListenerOwners.RequireTogetherServerOwner(observerPort, processes[2].Process);
                Directory.CreateDirectory(profile.WorldDirectory);
                if (runUx) await File.WriteAllTextAsync(Path.Combine(profile.WorldDirectory, "world.dat"),
                    "Synthetic UI rehearsal payload " + runId.ToString("N"));
                LabRequire((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings", new()
                {
                    Profiles = [profile],
                    MaxConcurrentServers = 1,
                    CompanionBindAddress = transportAddress,
                    CompanionEndpoint = $"https://{transportAddress}:{appPorts[3]}",
                    CompanionPort = appPorts[3]
                })).Ok, "HostSettingsRejected");
                // Configure each route before pairing; only explicit actions enable listeners.
                LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "CandidateHostModeRejected");
                LabRequire((await PutAsync<HostSettings, ActionResult>(first, "/api/local/settings", new()
                {
                    Profiles = [],
                    CompanionBindAddress = transportAddress,
                    CompanionPort = appPorts[4],
                    CompanionEndpoint = $"https://{transportAddress}:{appPorts[4]}",
                    CompanionListeningEnabled = false,
                    RemoteControlsEnabled = false
                })).Ok, "CandidateSettingsRejected");
                LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(), "CandidateFriendModeRejected");
                if (lanAddress is not null && !runUx)
                {
                    LabRequire((await PostAsync<object, JsonElement>(second, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "ObserverHostModeRejected");
                    LabRequire((await PutAsync<HostSettings, ActionResult>(second, "/api/local/settings", new()
                    {
                        Profiles = [],
                        CompanionBindAddress = transportAddress,
                        CompanionPort = appPorts[5],
                        CompanionEndpoint = $"https://{transportAddress}:{appPorts[5]}",
                        CompanionListeningEnabled = false
                    })).Ok, "ObserverSettingsRejected");
                    LabRequire((await PostAsync<object, JsonElement>(second, "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(), "ObserverFriendModeRejected");
                }
                LabRequire(!Directory.Exists(hostData + "-production-unused") && !Directory.Exists(friendData + "-production-unused") &&
                    !Directory.Exists(observerData + "-production-unused"), "ProductionSentinelTouched");
                return null;
            });

            if (runUx)
            {
                await Stage("two-ended-ux", async () =>
                {
                    await RunLocalPairingLabUxAsync(root, job, hostPort, friendPort, observerPort,
                        profile.Id, fixturePath, lanAddress);
                    return null;
                });
            }
            else
            {
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
                    originalHostId = a.HostId;
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
                        !(device.SharedWorldGrants?.Values.Any(grants => grants.Receive || grants.EligibleHost || grants.Reshare ||
                            grants.RecoveryVoter || grants.ManageSharing) ?? false)), "GrantsDefaultedOn");
                    LabRequire((await PutAsync<object, SharedWorldResult>(owner,
                        $"/api/local/profiles/{profile.Id}/shared-world/hosting", new { enabled = true })).Ok, "OwnerSharingRejected");
                    var initialRoster = await PairingLabGetAsync<SharedWorldRoster>(owner,
                        $"/api/local/profiles/{profile.Id}/shared-world/governance");
                    LabRequire(SharedWorldRosterTrust.Verify(initialRoster), "InitialSignedRosterRequired");
                    groupKey = new SharedWorldPeerRoot(initialRoster.OwnerPublicKey, profile.Id, initialRoster.GroupId).GroupKey;
                    var clients = new[] { (Id: deviceA, Client: first), (Id: deviceB, Client: second) };
                    foreach (var pc in clients)
                    {
                        // Ordinary peer refresh strictly enrolls this identity;
                        // the server choice supplies all three save permissions.
                        await SyncPeersAsync(pc.Client);
                        var enrolledRoster = await PairingLabGetAsync<SharedWorldRoster>(owner,
                            $"/api/local/profiles/{profile.Id}/shared-world/governance");
                        LabRequire(SharedWorldRosterTrust.Verify(enrolledRoster) && enrolledRoster.Members.Any(member =>
                            member.DeviceId == pc.Id && !member.Revoked && !string.IsNullOrWhiteSpace(member.PublicKey) &&
                            member.Grants is { Receive: true, EligibleHost: true, Reshare: true, RecoveryVoter: false, ManageSharing: false }),
                            "AutomaticSignedGrantsMissing");
                        LabRequire((await PairingLabGetAsync<ReceivedSharedWorldStatus>(pc.Client,
                            $"/api/local/friend/{profile.Id}/shared-world")).Consented, "AutomaticReceivingMissing");
                        var receiveEdit = await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                            $"/api/local/devices/{pc.Id}/shared-world/{profile.Id}", new(false));
                        LabRequire(!receiveEdit.Ok && receiveEdit.Code == "ServerHostingSettingRequired", "PerPcReceiveOverrodeServerChoice");
                    }
                    var grantEdit = await PutAsync<SharedWorldDeviceGrantsRequest, PairingDecision>(owner,
                        $"/api/local/devices/{deviceA}/shared-world/{profile.Id}/grants", new(new(Receive: true, EligibleHost: true)));
                    LabRequire(!grantEdit.Ok && grantEdit.Code == "ServerHostingSettingRequired", "PerPcGrantsOverrodeServerChoice");
                    var current = (await PairingLabGetAsync<PairingLabDevices>(owner, "/api/local/companion")).Devices;
                    LabRequire(current.Count == 2 && current.All(device => !device.CanViewPassword &&
                        device.SharedWorldGrants?.GetValueOrDefault(profile.Id) is
                        { Receive: true, EligibleHost: true, Reshare: true, RecoveryVoter: false, ManageSharing: false }),
                        "AutomaticGrantScopeLost");
                    signedRoster = await PairingLabGetAsync<SharedWorldRoster>(owner, $"/api/local/profiles/{profile.Id}/shared-world/governance");
                    LabRequire(SharedWorldRosterTrust.Verify(signedRoster) && signedRoster.Members.Count == 2 &&
                        signedRoster.Members.All(member => !member.Revoked && member.Grants is
                        { Receive: true, EligibleHost: true, Reshare: true, RecoveryVoter: false, ManageSharing: false }),
                        "CurrentSignedRosterRequired");
                    groupKey = new SharedWorldPeerRoot(signedRoster.OwnerPublicKey, profile.Id, signedRoster.GroupId).GroupKey;
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
                    LabRequire(start.Ok, "FixtureStartRejected");
                    var run = await WaitForFixtureIdentityAsync(start.Snapshot.Runs.Single(item => item.ProfileId == profile.Id),
                        () => PairingLabGetAsync<HostSnapshot>(owner, "/api/local/snapshot"));
                    TrackRun(run);
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
                await Stage("unsupported-password-source-denied", async () =>
                {
                    var result = await PostAsync<object, GamePasswordResult>(first,
                        $"/api/local/friend/{profile.Id}/game-password/reveal", new { connectionId = connectionA });
                    LabRequire(!result.Ok && result.Password is null && result.Code == "GamePasswordUnsupported", "FixturePasswordBoundaryChanged");
                    return null;
                });

                await Stage("guided-hosting", async () =>
                {
                    var operationClock = Stopwatch.StartNew();
                    async Task<T> ObserveAsync<T>(string id, HttpClient client, Func<Task<T>> action)
                    {
                        var startedMs = operationClock.ElapsedMilliseconds;
                        var timeoutMs = (long)client.Timeout.TotalMilliseconds;
                        Console.WriteLine("LOCAL_PAIRING_GUIDED_OPERATION " + JsonSerializer.Serialize(
                            new PairingLabOperation(id, "Started", startedMs, 0, timeoutMs), Json));
                        try
                        {
                            var result = await action();
                            var code = result switch
                            {
                                LocalHostingStatus status => status.Code,
                                ActionResult status => status.Code,
                                JsonElement json when json.ValueKind == JsonValueKind.Object &&
                                    json.TryGetProperty("code", out var field) && field.ValueKind == JsonValueKind.String => field.GetString(),
                                _ => null
                            };
                            // Only fixed result codes are exported. No response
                            // body, path, identity, message or raw error is saved.
                            if (code is not { Length: > 0 and <= 64 } || !code.All(char.IsAsciiLetterOrDigit)) code = null;
                            var observation = new PairingLabOperation(id, "Returned", startedMs,
                                operationClock.ElapsedMilliseconds - startedMs, timeoutMs, code);
                            guidedOperations.Add(observation);
                            Console.WriteLine("LOCAL_PAIRING_GUIDED_OPERATION " + JsonSerializer.Serialize(observation, Json));
                            return result;
                        }
                        catch (Exception error)
                        {
                            var observation = new PairingLabOperation(id, "Failed", startedMs,
                                operationClock.ElapsedMilliseconds - startedMs, timeoutMs,
                                ExceptionType: error.GetType().Name, InnerExceptionType: error.InnerException?.GetType().Name,
                                HttpClientTimeout: error is TaskCanceledException && error.InnerException is TimeoutException);
                            guidedOperations.Add(observation);
                            Console.WriteLine("LOCAL_PAIRING_GUIDED_OPERATION " + JsonSerializer.Serialize(observation, Json));
                            throw new PairingLabFailure("Guided" + id + (error is TaskCanceledException ? "TimedOut" : "Failed"));
                        }
                    }
                    async Task<HostSnapshot> SnapshotAsync(string phase)
                    {
                        LabRequire((await ObserveAsync(phase + "OpenHost", first, () =>
                            PostAsync<object, JsonElement>(first, "/api/local/mode/host", new { }))).GetProperty("ok").GetBoolean(),
                            "CandidateSnapshotModeRejected");
                        try { return await ObserveAsync(phase + "Read", first, () => PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot")); }
                        finally
                        {
                            LabRequire((await ObserveAsync(phase + "RestoreFriend", first, () =>
                                PostAsync<object, JsonElement>(first, "/api/local/mode/friend", new { }))).GetProperty("ok").GetBoolean(),
                                "CandidateSnapshotRestoreRejected");
                        }
                    }
                    // Preparation repeats peer/save checks and may check the
                    // incoming route. Match the UI's bounded operation budget,
                    // keeping ordinary status requests on their shorter client.
                    using var prepareClient = LocalClient(friendPort);
                    prepareClient.Timeout = TimeSpan.FromSeconds(120);
                    prepared = await ObserveAsync("PrepareLocal", prepareClient, () => PostAsync<LocalHostingPrepareRequest, LocalHostingStatus>(prepareClient,
                        $"/api/local/friend/{profile.Id}/shared-world/hosting/prepare",
                        new(true, true, ServerFile: Path.GetFullPath(fixturePath))));
                    LabRequire(prepared.LocalProfileId is not null && prepared.BranchHash is not null &&
                        prepared.ReadyForManualStart && !prepared.Running, "SyntheticHostingPreparationRejected");
                    var exactLocalProfile = prepared.LocalProfileId ?? throw new PairingLabFailure("PreparedProfileMissing");
                    localProfileId = exactLocalProfile;
                    var before = await SnapshotAsync("BeforeStart");
                    LabRequire(before.Runs.All(run => run.ProcessId is null), "PreparationStartedProcess");
                    if (lanAddress is not null)
                    {
                        // Save only explicit incoming consent. The exact guided
                        // preparation registers its signed group before listener
                        // activation; lifecycle permission remains independent.
                        LabRequire((await ObserveAsync("ListenerOpenHost", first, () => PostAsync<object, JsonElement>(first,
                            "/api/local/mode/host", new { }))).GetProperty("ok").GetBoolean(), "CandidateListenerModeRejected");
                        LabRequire((await ObserveAsync("ListenerEnable", first, () => PutAsync<HostControlPolicyChange, ActionResult>(first,
                            "/api/local/settings/control-policy", new(CompanionListeningEnabled: true)))).Ok, "CandidateListenerRejected");
                        LabRequire((await ObserveAsync("ListenerRestoreFriend", first, () => PostAsync<object, JsonElement>(first,
                            "/api/local/mode/friend", new { }))).GetProperty("ok").GetBoolean(), "CandidateListenerRestoreRejected");
                        prepared = await ObserveAsync("PrepareIncoming", prepareClient, () => PostAsync<LocalHostingPrepareRequest, LocalHostingStatus>(prepareClient,
                            $"/api/local/friend/{profile.Id}/shared-world/hosting/prepare",
                            new(true, true, ServerFile: Path.GetFullPath(fixturePath), RequirePeerConnection: true)));
                        LabRequire(prepared.ReadyForManualStart && prepared.LocalProfileId == exactLocalProfile &&
                            prepared.Remedy is not { Kind: "Connection" or "Ports" or "Review" }, "LanHostingPreparationRejected");
                        WindowsListenerOwners.RequireTogetherServerOwner(appPorts[4], processes.Single(process => process.Role == "friend-a").Process);
                        preparedBranch = (await ObserveAsync("ReadBranchProof", first, () => PairingLabGetAsync<WorldSeparateCopyBranch[]>(first,
                            $"/api/local/friend/{profile.Id}/shared-world/recovery/separate"))).Single(branch => branch.BranchHash == prepared.BranchHash);
                        LabRequire(SharedWorldSeparateCopyStore.Verify(preparedBranch) &&
                            preparedBranch.Offer.Roster.OwnerPublicKey == signedRoster!.OwnerPublicKey &&
                            preparedBranch.CandidatePublicKey == signedRoster.Members.Single(member => member.DeviceId == deviceA).PublicKey,
                            "PreparedCandidateBranchNotAuthorized");
                    }
                    using var startClient = LocalClient(friendPort);
                    startClient.Timeout = TimeSpan.FromSeconds(30);
                    var result = await ObserveAsync("Start", startClient, () => PostAsync<object, JsonElement>(startClient,
                        $"/api/local/friend/{profile.Id}/shared-world/hosting/start", new
                        {
                            localProfileId = exactLocalProfile,
                            branchHash = prepared.BranchHash,
                            requestId = (Guid?)null,
                            acceptSplitWarning = true
                        }));
                    var after = await SnapshotAsync("AfterStart");
                    if (lanAddress is null)
                    {
                        // Ordinary peer-route rules deliberately refuse loopback.
                        LabRequire(!result.GetProperty("ok").GetBoolean() && prepared.Remedy?.Kind == "Connection" &&
                            result.GetProperty("code").GetString() == "LocalHostingActionRequired", "LoopbackHostingConnectionBlockerMissing");
                        LabRequire(after.Runs.All(run => run.ProcessId is null), "BlockedGuidedStartCreatedProcess");
                    }
                    else
                    {
                        LabRequire(result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "SeparateCopyStarted",
                            "GuidedLanStartRejected");
                        var run = await WaitForFixtureIdentityAsync(after.Runs.Single(item => item.ProfileId == exactLocalProfile),
                            () => SnapshotAsync("MatchedRun"));
                        TrackRun(run);
                        candidateSessionId = run.RunOperationId;
                        hostedCopy = true;
                    }
                    return null;
                });

                await Stage("local-copy-hosting", async () =>
                {
                    if (lanAddress is not null)
                    {
                        var candidate = await CandidateSnapshotAsync();
                        var active = candidate.Runs.Single(item => item.ProfileId == localProfileId);
                        LabRequire(active.State == "Process running" && active.RunOperationId == candidateSessionId &&
                            candidate.Runs.Count(item => item.ProcessId is not null) == 1, "GuidedCandidateRunChanged");
                        var source = await PairingLabGetAsync<HostSnapshot>(owner, "/api/local/snapshot");
                        LabRequire(source.Runs.All(item => item.ProcessId is null), "OriginalHostRestartedDuringCopyHosting");
                        var restored = candidate.Settings.Profiles.Single(item => item.Id == localProfileId);
                        LabRequire(AppInstance.ContainsPath(friendData, restored.WorldDirectory) && restored.WorldDirectory != profile.WorldDirectory &&
                            await File.ReadAllTextAsync(Path.Combine(restored.WorldDirectory, "world.dat")) == "Synthetic fixture payload " + runId.ToString("N"),
                            "GuidedStartDidNotUseReceivedPayload");
                        return null;
                    }
                    LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "LocalCopyHostModeRejected");
                    var started = await PostAsync<object, ActionResult>(first,
                        $"/api/local/profiles/{localProfileId}/shared-world/hosting/start", new { branchHash = prepared!.BranchHash, acceptSplitWarning = true });
                    LabRequire(started.Ok, "ExplicitLocalCopyStartRejected");
                    var run = await WaitForFixtureIdentityAsync(started.Snapshot.Runs.Single(item => item.ProfileId == localProfileId),
                        () => PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot"));
                    TrackRun(run);
                    hostedCopy = true;
                    LabRequire((await PostAsync<object, ActionResult>(first, $"/api/local/profiles/{localProfileId}/stop", new { })).Ok, "LocalCopyPipeStopRejected");
                    _ = await WaitForRunStateAsync(first, run.ProfileId, "Offline");
                    return null;
                });
                if (lanAddress is not null) await Stage("new-host-notice", async () =>
                {
                    // The same fixed projection supplies Other Hosts and the inline
                    // notice. Fixture health remains Preparing, never game Ready.
                    await SyncPeersAsync(owner);
                    await SyncPeersAsync(second);
                    LabRequire((await PutAsync<WorldPeerSharingRequest, WorldPeerActionResult>(second,
                        $"/api/local/world-peers/{groupKey}/sharing", new(true))).Ok, "ObserverPeerListenerRejected");
                    WindowsListenerOwners.RequireTogetherServerOwner(appPorts[5], processes.Single(process => process.Role == "friend-b").Process);
                    await PairingLabWaitAsync(async () =>
                    {
                        await SyncPeersAsync(first);
                        await SyncPeersAsync(owner);
                        await SyncPeersAsync(second);
                        var candidate = await PairingLabGetAsync<JsonElement>(first, "/api/local/companion");
                        var fingerprint = candidate.GetProperty("fingerprint").GetString();
                        return await SeesCandidateAsync(owner, fingerprint) && await SeesCandidateAsync(second, fingerprint);
                    }, "SignedCandidateSessionNotDiscovered");
                    discoveredHost = true;
                    return null;
                });
                if (lanAddress is not null)
                {
                    await Stage("new-host-authenticated-connect", async () =>
                    {
                        var connect = new { deviceId = deviceA, sessionId = candidateSessionId!.Value };
                        var observedAfter = DateTimeOffset.UtcNow.AddSeconds(-1);
                        var result = await PostAsync<object, WorldPeerActionResult>(owner, $"/api/local/world-peers/{groupKey}/connect", connect);
                        LabRequire(result.Ok && result.Code == "PeerSessionConnected", "OriginalOwnerPeerConnectNotVerified");
                        LabRequire((await PostAsync<object, JsonElement>(owner, "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(), "OriginalOwnerJoinModeRejected");
                        await RequireCandidateConnectionAsync(owner, observedAfter);
                        connectedOwner = true;
                        var candidateDevices = (await PairingLabGetAsync<PairingLabDevices>(first, "/api/local/companion")).Devices;
                        LabRequire(candidateDevices.Count == 1, "OriginalOwnerCredentialMissing");
                        ownerAlias = candidateDevices.Single().Id;
                        result = await PostAsync<object, WorldPeerActionResult>(second, $"/api/local/world-peers/{groupKey}/connect", connect);
                        LabRequire(result.Ok && result.Code == "PeerSessionConnected", "ObserverPeerConnectNotVerified");
                        await RequireCandidateConnectionAsync(second, observedAfter);
                        connectedObserver = true;
                        candidateDevices = (await PairingLabGetAsync<PairingLabDevices>(first, "/api/local/companion")).Devices;
                        LabRequire(candidateDevices.Count == 2 && candidateDevices.All(device => device.Id != deviceA && device.Id != deviceB &&
                            device.AssignedProfileIds.SequenceEqual([localProfileId!.Value])), "CandidateCredentialsNotIndependent");
                        observerAlias = candidateDevices.Single(device => device.Id != ownerAlias).Id;
                        LabRequire(observerAlias != Guid.Empty && observerAlias != ownerAlias, "DistinctCandidateCredentialsRequired");
                        LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "CandidateChatModeRejected");
                        var sent = await PostAsync<ChatPostRequest, ChatRoomView>(first, $"/api/local/profiles/{localProfileId}/chat/messages", new("New synthetic Host message"));
                        LabRequire(sent.Ok && sent.Entries.Count == 1, "CandidateChatRejected");
                        var ownerRoom = await PostAsync<object, ChatRoomView>(owner, $"/api/local/friend/{localProfileId}/chat/sync", new { });
                        var observerRoom = await PostAsync<object, ChatRoomView>(second, $"/api/local/friend/{localProfileId}/chat/sync", new { });
                        LabRequire(ownerRoom.Ok && observerRoom.Ok && ownerRoom.Entries.Single().Id == sent.Entries.Single().Id &&
                            observerRoom.Entries.Single().Id == sent.Entries.Single().Id, "NewHostAuthenticatedChatReadFailed");
                        return null;
                    });
                    await Stage("new-host-permission-isolation", async () =>
                    {
                        LabRequire((await PutAsync<DevicePermissionRequest, PairingDecision>(first,
                            $"/api/local/devices/{ownerAlias}/permissions", new(true, false, Scope: "start"))).Ok, "CandidateOwnerPermissionRejected");
                        LabRequire((await PutAsync<HostControlPolicyChange, ActionResult>(first, "/api/local/settings/control-policy",
                            new(RemoteControlsEnabled: true))).Ok, "CandidateRemoteControlsRejected");
                        var allowed = await RequireCandidateConnectionAsync(owner, DateTimeOffset.UtcNow.AddSeconds(-1));
                        var restricted = await RequireCandidateConnectionAsync(second, DateTimeOffset.UtcNow.AddSeconds(-1));
                        LabRequire(allowed.Profiles.Single().CanStart && !restricted.Profiles.Single().CanStart &&
                            !restricted.Profiles.Single().CanStop && !restricted.Profiles.Single().CanViewPassword, "CandidatePermissionScopeLost");
                        var denied = await FriendActionAsync(second, localProfileId!.Value, "start");
                        LabRequire(!denied.Ok && denied.Code == "PermissionDenied", "ObserverObtainedOwnerLifecyclePermission");
                        var current = await PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot");
                        LabRequire(current.Runs.Count(run => run.ProcessId is not null) == 1 &&
                            current.Runs.Single(run => run.ProfileId == localProfileId).RunOperationId == candidateSessionId, "DeniedStartChangedCandidateRun");
                        LabRequire((await PutAsync<DevicePermissionRequest, PairingDecision>(first,
                            $"/api/local/devices/{ownerAlias}/permissions", new(false, false, Scope: "start"))).Ok, "CandidateOwnerPermissionRevokeRejected");
                        return null;
                    });
                    await Stage("new-host-stop-and-transfer", async () =>
                    {
                        var snapshot = await PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot");
                        var restored = snapshot.Settings.Profiles.Single(item => item.Id == localProfileId);
                        LabRequire(AppInstance.ContainsPath(friendData, restored.WorldDirectory), "CandidateWorldBoundaryChanged");
                        var seed = "Synthetic candidate payload " + runId.ToString("N");
                        await File.WriteAllTextAsync(Path.Combine(restored.WorldDirectory, "world.dat"), seed);
                        var managed = snapshot.Runs.Single(run => run.ProfileId == localProfileId);
                        LabRequire(managed.RunOperationId == candidateSessionId && managed.ProcessId is not null, "CandidateStopIdentityChanged");
                        LabRequire((await PostAsync<object, ActionResult>(first, $"/api/local/profiles/{localProfileId}/stop", new { })).Ok, "CandidateOwnerPipeStopRejected");
                        _ = await WaitForRunStateAsync(first, localProfileId!.Value, "Offline");
                        LabRequire(processes.Where(process => process.Role == "fixture").All(process => process.HasExitedNow()), "StoppedCandidateProcessStillRunning");
                        var published = Path.Combine(friendData, "shared-world-separate-published", profile.Id.ToString("N"), prepared!.BranchHash!);
                        var version = FriendLink.ReadReceivedLatest(published);
                        LabRequire(version is not null && SharedWorldService.VerifySignature(version) && version.ProfileId == profile.Id &&
                            version.SigningPublicKey == preparedBranch!.Offer.Proposal.CandidatePublicKey && version.Number > 1,
                            "CandidateStoppedCopyNotPublished");
                        receivedBranchVersionHash = version!.VersionHash;
                        var vault = Path.Combine(observerData, "received-shared-branches", groupKey!, prepared.BranchHash!);
                        var transferAttempt = 0;
                        await PairingLabWaitAsync(async () =>
                        {
                            var timing = Stopwatch.StartNew();
                            await SyncPeersAsync(first);
                            var candidateSyncMs = timing.ElapsedMilliseconds;
                            timing.Restart();
                            await SyncPeersAsync(second);
                            var observerSyncMs = timing.ElapsedMilliseconds;
                            var candidateGroup = (await PairingLabGetAsync<WorldPeerGroupView[]>(first, "/api/local/world-peers"))
                                .SingleOrDefault(item => item.GroupKey == groupKey);
                            var observerGroup = (await PairingLabGetAsync<WorldPeerGroupView[]>(second, "/api/local/world-peers"))
                                .SingleOrDefault(item => item.GroupKey == groupKey);
                            bool HasHead(WorldPeerGroupView? value) => value?.State.Branches.Any(item =>
                                item.DeviceId == deviceA && item.Authorized && !item.HasConflict &&
                                item.Branch.BranchHash == prepared.BranchHash && item.Branch.HeadVersionHash == version.VersionHash) == true;
                            bool HasPayload(WorldPeerGroupView? value) => value?.State.Peers.Any(item =>
                                item.DeviceId == deviceA && item.Authorized && !item.HasConflict &&
                                item.AvailableVersionHashes.Contains(version.VersionHash)) == true;
                            var matched = FriendLink.ReadReceivedLatest(vault)?.VersionHash == version.VersionHash;
                            if (transferObservations.Count == 12) transferObservations.RemoveAt(0);
                            transferObservations.Add(new(++transferAttempt, candidateSyncMs, observerSyncMs,
                                HasHead(candidateGroup), HasPayload(candidateGroup), observerGroup?.ReceiveConsented == true,
                                observerGroup?.CanReceive == true, HasHead(observerGroup), HasPayload(observerGroup),
                                observerGroup?.State.Peers.Any(item => item.DeviceId == deviceA && item.Authorized && !item.HasConflict &&
                                    item.Routes.Any(route => route.Address == $"https://{transportAddress}:{appPorts[4]}")) == true,
                                candidateGroup?.Warning is not null, observerGroup?.Warning is not null,
                                observerGroup?.State.Replication.HasConflicts == true, matched));
                            return matched;
                        }, "CandidateBranchTransferNotVerified", TimeSpan.FromSeconds(90));
                        var proof = JsonSerializer.Deserialize<WorldSeparateCopyBranch>(await File.ReadAllBytesAsync(Path.Combine(vault, "branch.json")), Json);
                        LabRequire(SharedWorldSeparateCopyStore.Verify(proof) && proof!.BranchHash == preparedBranch.BranchHash &&
                            proof.Offer.Roster.OwnerPublicKey == signedRoster!.OwnerPublicKey &&
                            SharedWorldSeparateCopyStore.VerifySnapshotVersion(proof, version), "ReceivedCandidateBranchProofRejected");
                        var payload = Path.Combine(vault, version.VersionHash, "payload", "world.dat");
                        LabRequire(await File.ReadAllTextAsync(payload) == seed, "CandidateTransferredBytesDiffer");
                        var receipt = JsonSerializer.Deserialize<SharedWorldReceipt>(await File.ReadAllBytesAsync(Path.Combine(vault, version.VersionHash, "receipt.json")), Json);
                        LabRequire(receipt is not null && receipt.DeviceId == deviceB && receipt.VersionHash == version.VersionHash &&
                            SharedWorldReceiptTrust.Verify(receipt, signedRoster!.Members.Single(member => member.DeviceId == deviceB).PublicKey), "CandidateCopyReceiptRejected");
                        transferredCandidateCopy = true;
                        LabRequire((await PostAsync<object, JsonElement>(owner, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "OriginalOwnerHostModeRestoreRejected");
                        return null;
                    });
                    await Stage("new-host-restart-reconnect", async () =>
                    {
                        // Only app identities already recorded in our disposable job
                        // are stopped. The saved branch has already stopped safely.
                        LabRequire((await PostAsync<object, JsonElement>(owner, "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(), "RestartOwnerJoinModeRejected");
                        var savedOwner = await WaitForConnectedAsync(owner, allowDisabled: true);
                        var savedObserver = await WaitForConnectedAsync(second, allowDisabled: true);
                        var savedDevices = (await PairingLabGetAsync<PairingLabDevices>(first, "/api/local/companion")).Devices;
                        LabRequire(savedDevices.Count == 2 && savedDevices.All(device => device.Paired && !device.Revoked &&
                            !device.CanStart && !device.CanStop && !device.CanViewPassword), "RestartCredentialBaselineInvalid");
                        var beforeCompanion = await PairingLabGetAsync<JsonElement>(first, "/api/local/companion");
                        var fingerprint = beforeCompanion.GetProperty("fingerprint").GetString();
                        var oldCandidate = processes.Single(process => process.Role == "friend-a" && process.Alive);
                        var oldOwner = processes.Single(process => process.Role == "host" && process.Alive);
                        await oldCandidate.StopOwnedAsync();
                        await oldOwner.StopOwnedAsync();
                        LabRequire(oldCandidate.HasExitedNow() && oldOwner.HasExitedNow() &&
                            !oldCandidate.IdentityReview && !oldOwner.IdentityReview, "RestartOldAppIdentityStillActive");
                        await PairingLabWaitAsync(async () =>
                        {
                            var view = await PostAsync<object, FriendView>(second, "/api/local/friend/poll", new { });
                            return view.State == "Disconnected/Unknown" && view.ConnectionId == savedObserver.ConnectionId &&
                                view.Endpoint == savedObserver.Endpoint;
                        }, "OfflineCandidateWasNotUnknown");
                        var candidateApp = Track(StartPairingLabApp(appPath, "--host", friendPort, friendData, lanAddress), "friend-a", appPath);
                        var ownerApp = Track(StartPairingLabApp(appPath, "--friend", hostPort, hostData, lanAddress), "host", appPath);
                        await Task.WhenAll(WaitLocalAsync(friendPort), WaitLocalAsync(hostPort));
                        LabRequire(candidateApp.Matches() && ownerApp.Matches() && candidateApp.Id != oldCandidate.Id && ownerApp.Id != oldOwner.Id,
                            "RestartNewAppIdentitiesMissing");
                        WindowsListenerOwners.RequireTogetherServerOwner(friendPort, candidateApp.Process);
                        WindowsListenerOwners.RequireTogetherServerOwner(hostPort, ownerApp.Process);
                        WindowsListenerOwners.RequireTogetherServerOwner(appPorts[4], candidateApp.Process);
                        var afterCompanion = await PairingLabGetAsync<JsonElement>(first, "/api/local/companion");
                        LabRequire(afterCompanion.GetProperty("fingerprint").GetString() == fingerprint, "RestartChangedCandidateTlsIdentity");
                        var restored = await PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot");
                        LabRequire(restored.Runs.All(run => run.ProcessId is null && run.RunOperationId is null) &&
                            restored.Settings.Profiles.Single(item => item.Id == localProfileId).SeparateCopyBranchHash == prepared!.BranchHash,
                            "AppRestartReplayedHostingStart");
                        var published = Path.Combine(friendData, "shared-world-separate-published", profile.Id.ToString("N"), prepared!.BranchHash!);
                        LabRequire(FriendLink.ReadReceivedLatest(published)?.VersionHash == receivedBranchVersionHash,
                            "RestartLostPublishedBranchCopy");
                        var persistedDevices = (await PairingLabGetAsync<PairingLabDevices>(first, "/api/local/companion")).Devices;
                        LabRequire(persistedDevices.Count == 2 && persistedDevices.All(device => savedDevices.Any(saved => saved.Id == device.Id &&
                            saved.CredentialExpiresUtc == device.CredentialExpiresUtc && saved.AssignedProfileIds.SequenceEqual(device.AssignedProfileIds)) &&
                            device.Paired && !device.Revoked && !device.CanStart && !device.CanStop && !device.CanViewPassword),
                            "RestartChangedCredentialOrPermissions");
                        // Reconnect before issuing Start: authenticated status and
                        // repeated polling must leave the prepared game offline.
                        foreach (var client in new[] { owner, second })
                        {
                            var offline = await WaitForConnectedAsync(client, allowDisabled: true);
                            LabRequire(offline.Endpoint == savedObserver.Endpoint && offline.HostId == savedObserver.HostId &&
                                offline.Profiles.Count == 1 && offline.Profiles.Single().Id == localProfileId &&
                                offline.Profiles.Single().State == "Offline" && offline.Profiles.Single().RunOperationId is null,
                                "RestartReconnectionReplayedGameStart");
                        }
                        restored = await PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot");
                        LabRequire(restored.Runs.All(run => run.ProcessId is null && run.RunOperationId is null), "RestartPollingStartedPreparedGame");
                        var oldSession = candidateSessionId;
                        var started = await PostAsync<object, ActionResult>(first,
                            $"/api/local/profiles/{localProfileId}/shared-world/hosting/start", new { branchHash = prepared.BranchHash, acceptSplitWarning = true });
                        LabRequire(started.Ok, "RestartExplicitCandidateStartRejected");
                        var run = await WaitForFixtureIdentityAsync(started.Snapshot.Runs.Single(item => item.ProfileId == localProfileId),
                            () => PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot"));
                        TrackRun(run);
                        LabRequire(run.RunOperationId != oldSession && run.RunOperationId is not null && !run.PlayerCountTrusted && run.OnlinePlayers is null,
                            "RestartReusedRunIdentityOrClaimedReadiness");
                        candidateSessionId = run.RunOperationId;
                        var freshAfter = DateTimeOffset.UtcNow.AddSeconds(-1);
                        var ownerView = await RequireCandidateConnectionAsync(owner, freshAfter);
                        var observerView = await RequireCandidateConnectionAsync(second, freshAfter);
                        LabRequire(ownerView.HostId == savedOwner.HostId && observerView.HostId == savedObserver.HostId &&
                            ownerView.ConnectionId == savedOwner.ConnectionId && observerView.ConnectionId == savedObserver.ConnectionId &&
                            !ownerView.Profiles.Single().CanStart && !observerView.Profiles.Single().CanStart &&
                            !observerView.Profiles.Single().CanViewPassword, "RestartDidNotReusePinnedScopedConnections");
                        await PairingLabWaitAsync(async () =>
                        {
                            await SyncPeersAsync(first); await SyncPeersAsync(owner); await SyncPeersAsync(second);
                            return await SeesCandidateAsync(owner, fingerprint) && await SeesCandidateAsync(second, fingerprint);
                        }, "RestartedCandidateSessionNotDiscovered");
                        var sent = await PostAsync<ChatPostRequest, ChatRoomView>(first, $"/api/local/profiles/{localProfileId}/chat/messages", new("Restarted synthetic Host message"));
                        var ownerRoom = await PostAsync<object, ChatRoomView>(owner, $"/api/local/friend/{localProfileId}/chat/sync", new { });
                        var observerRoom = await PostAsync<object, ChatRoomView>(second, $"/api/local/friend/{localProfileId}/chat/sync", new { });
                        LabRequire(sent.Ok && ownerRoom.Ok && observerRoom.Ok && sent.Entries.All(entry =>
                            ownerRoom.Entries.Any(copy => copy.Id == entry.Id) && observerRoom.Entries.Any(copy => copy.Id == entry.Id)),
                            "RestartedHostAuthenticatedChatFailed");
                        restartedHostConnections = true;
                        return null;
                    });
                    await Stage("new-host-revocation-isolation", async () =>
                    {
                        // Revoke only the candidate's derived owner credential. The
                        // owner's original world membership and Friend B stay intact.
                        LabRequire((await PostAsync<object, PairingDecision>(first,
                            $"/api/local/devices/{ownerAlias}/revoke", new { })).Ok, "CandidateOwnerAliasRevokeRejected");
                        await PairingLabWaitAsync(async () =>
                        {
                            var view = await PostAsync<object, FriendView>(owner, "/api/local/friend/poll", new { });
                            return view.State == "Revoked" && view.ConnectionCode == "Revoked";
                        }, "RevokedCandidateAliasStillAuthenticated");
                        var observer = await RequireCandidateConnectionAsync(second, DateTimeOffset.UtcNow.AddSeconds(-1));
                        var devices = (await PairingLabGetAsync<PairingLabDevices>(first, "/api/local/companion")).Devices;
                        LabRequire(devices.Single(device => device.Id == ownerAlias).Revoked &&
                            devices.Single(device => device.Id == observerAlias) is { Revoked: false, Paired: true } &&
                            !observer.Profiles.Single().CanStart && !observer.Profiles.Single().CanStop && !observer.Profiles.Single().CanViewPassword,
                            "CandidateRevocationAffectedOtherCredential");
                        var denied = await FriendActionAsync(second, localProfileId!.Value, "start");
                        LabRequire(!denied.Ok && denied.Code == "PermissionDenied", "RestartBroadenedObserverPermission");
                        var run = (await PairingLabGetAsync<HostSnapshot>(first, "/api/local/snapshot")).Runs.Single(item => item.ProfileId == localProfileId);
                        LabRequire(run.RunOperationId == candidateSessionId && run.State == "Process running", "AliasRevocationChangedManagedRun");
                        LabRequire((await PostAsync<object, ActionResult>(first, $"/api/local/profiles/{localProfileId}/stop", new { })).Ok,
                            "RestartedCandidateOwnerStopRejected");
                        _ = await WaitForRunStateAsync(first, localProfileId.Value, "Offline");
                        LabRequire((await PostAsync<object, JsonElement>(owner, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(),
                            "RestartOwnerHostModeRestoreRejected");
                        isolatedRevocation = true;
                        return null;
                    });
                }
                await Stage("lost-response-cleanup", async () =>
                {
                    var started = await PostAsync<object, ActionResult>(owner, $"/api/local/profiles/{profile.Id}/start", new { });
                    LabRequire(started.Ok, "CleanupFaultFixtureStartRejected");
                    var run = await WaitForFixtureIdentityAsync(started.Snapshot.Runs.Single(item => item.ProfileId == profile.Id),
                        () => PairingLabGetAsync<HostSnapshot>(owner, "/api/local/snapshot"));
                    LabRequire(run.ProcessId is not null && run.RunOperationId is not null, "CleanupFaultFixtureIdentityMissing");
                    // Deliberately do not add this child to processes, just as when
                    // Start succeeds but its HTTP response never reaches tracking.
                    using var untracked = Process.GetProcessById(run.ProcessId!.Value);
                    LabRequire(!untracked.HasExited && string.Equals(untracked.MainModule?.FileName,
                        Path.GetFullPath(fixturePath), StringComparison.OrdinalIgnoreCase), "CleanupFaultFixtureMismatch");
                    runOperations.Add(run.RunOperationId!.Value);
                    var stoppedHost = processes.Single(process => process.Role == "host" && process.Alive);
                    await stoppedHost.StopOwnedAsync();
                    LabRequire(!stoppedHost.Alive && !stoppedHost.IdentityReview, "CleanupFaultHostDidNotExit");
                    LabRequire(!untracked.HasExited, "CleanupFaultDidNotLeaveChildForJob");
                    await job.StopAsync();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await untracked.WaitForExitAsync(deadline.Token);
                    LabRequire(untracked.HasExited && job.ActiveCount() == 0, "UntrackedFixtureSurvivedJobCleanup");
                    return null;
                });
                if (lanAddress is not null)
                    await Stage("new-host-receipt-confirmed", async () =>
                    {
                        // An empty job does not guarantee every terminated process
                        // has released its file handles. Confirm those exact PIDs,
                        // then allow only the Windows lock-release race to settle.
                        await PairingLabWaitAsync(() => Task.FromResult(processes.All(process => process.HasExitedNow())),
                            "ReceiptReviewProcessesDidNotExit", TimeSpan.FromSeconds(5));
                        LocalData? candidateData = null;
                        await PairingLabWaitAsync(() =>
                        {
                            try { candidateData = new LocalData(friendData); return Task.FromResult(true); }
                            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return Task.FromResult(false); }
                        }, "ReceiptReviewDataLockNotReleased", TimeSpan.FromSeconds(5));
                        using var receiptData = candidateData!;
                        var receipts = receiptData.LoadProtectedJson<List<SharedWorldReceipt>>($"world-peer-receipts-{groupKey}.protected") ?? [];
                        LabRequire(receipts.Any(receipt => receipt.DeviceId == deviceB && receipt.ProfileId == profile.Id &&
                            receipt.VersionHash == receivedBranchVersionHash && SharedWorldReceiptTrust.Verify(receipt,
                                signedRoster!.Members.Single(member => member.DeviceId == deviceB).PublicKey)), "NewHostDidNotRecordVerifiedCopyReceipt");
                        return null;
                    });
            }
        }
        catch (Exception error) { failureCode = error is PairingLabFailure assertion ? assertion.Code : error.GetType().Name; }
        finally
        {
            var failedStage = activeStage;
            try
            {
                await Stage("cleanup", async () =>
                {
                    // A successful UX Connect leaves the original app in Friend
                    // mode. Review local runs on each exact disposable app;
                    // never interpret its remote Friend snapshot as local runs.
                    await StopLocalLabRunsAsync(owner, hostPort, "host");
                    await StopLocalLabRunsAsync(first, friendPort, "friend-a");
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
            var uxPassed = stages.Any(stage => stage.Id == "two-ended-ux" && stage.State == "Passed");
            var report = new
            {
                schema = 1,
                runId,
                root,
                startedUtc,
                endedUtc = DateTimeOffset.UtcNow,
                durationMs = clock.ElapsedMilliseconds,
                mode = runUx ? "SamePcLanUx" : lanAddress is null ? "LoopbackBlocker" : "SamePcLanApi",
                physicalPcCount = 1,
                outcome = failureCode is null ? stages.Any(stage => stage.State == "Skipped") ? "CompletedWithSkips" : "Passed" : "Failed",
                failedStage = failureCode is null ? null : failedStage,
                failureCode,
                stages,
                runOperations,
                syntheticCopyHosted = runUx ? (bool?)null : hostedCopy,
                uxStagePassed = uxPassed,
                apiHostChange = runUx ? null : new
                {
                    discoveredHost,
                    connectedOwner,
                    connectedObserver,
                    candidateSessionId,
                    candidateSessionState,
                    transferredCandidateCopy,
                    restartedHostConnections,
                    isolatedRevocation
                },
                branchTransfer = new { convergenceTimeoutSeconds = 90, observations = transferObservations },
                cleanup = new { jobActiveProcesses = job.ActiveCount(), killRemainingMembersOnHarnessExit = true },
                resources = new
                {
                    sampledEveryMs = 250,
                    peakProcessCount,
                    peakWorkingSetBytes = peakWorkingSet,
                    currentWorkingSetBytes = processes.Sum(process => process.CurrentWorkingSetBytes)
                },
                windowObservation = new
                {
                    sampledEveryMs = 250,
                    samples = windowObservationSamples,
                    state = windowObservationFailure != 0 ? "Failed" : windowObservationSamples > 0 ? "Passed" : "NotObserved",
                    peakVisibleOwnedWindows,
                    observedVisibleWindow = windowObservationFailure == 1,
                    observationUnavailable = windowObservationFailure == 2,
                    scope = "All job-member top-level windows, including app, Node, browser and fixture descendants; sampled observation, no focus or placement calls"
                },
                ownedProcesses = processes.Select(process => process.Report()).ToArray(),
                acceptance = new[] { lanAddress is null ? "LoopbackOnly" : "SamePcAssignedLanAddressOnly", "SyntheticSeededPayloadOnly", "NoGameReadinessOrPlayerCountClaim",
                    "ValheimPasswordRevealUnverified", uxPassed ? "HostChangeExercisedByTwoEndedUx" : discoveredHost && connectedOwner && connectedObserver ?
                        "SignedHostSessionAndAuthenticatedConnectVerified" : "NewHostNoticeAndConnectUnverified",
                    uxPassed ? "TwoEndedBrowserUxRehearsal" : "RenderedUiUnverified",
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

        async Task StopLocalLabRunsAsync(HttpClient client, int port, string role)
        {
            var app = processes.LastOrDefault(process => process.Role == role && process.Alive);
            if (app is null || !app.Alive || !app.Matches()) return;
            try
            {
                var mode = await PostAsync<object, JsonElement>(client, "/api/local/mode/host", new { });
                if (!mode.GetProperty("ok").GetBoolean()) throw new PairingLabFailure("CleanupHostModeRejected");
                var snapshot = await PairingLabGetAsync<HostSnapshot>(client, "/api/local/snapshot");
                foreach (var run in snapshot.Runs.Where(run => run.ProcessId is not null))
                    await TryStopManagedRunAsync(port, run.ProfileId, app.Process);
            }
            catch (Exception error)
            {
                // The enclosing job still removes every remaining disposable
                // process. Do not print an app response or private path here.
                Console.Error.WriteLine($"Local lab graceful cleanup unavailable: {error.GetType().Name}");
            }
        }

        async Task SyncPeersAsync(HttpClient client)
        {
            LabRequire(groupKey is not null, "SignedWorldRootMissing");
            // Peer synchronization has its own reviewed 45-second transport
            // budget. The ordinary eight-second status client is too short for
            // a real multi-peer copy plus its signed receipt and queued sync.
            using var syncClient = LocalClient(client.BaseAddress!.Port);
            syncClient.Timeout = TimeSpan.FromSeconds(65);
            var result = await PostAsync<object, WorldPeerActionResult>(syncClient, $"/api/local/world-peers/{groupKey}/sync", new { });
            LabRequire(result.Ok && result.Code == "WorldPeersChecked", "PeerSyncRequestRejected");
        }

        async Task<bool> SeesCandidateAsync(HttpClient client, string? fingerprint)
        {
            var group = (await PairingLabGetAsync<WorldPeerGroupView[]>(client, "/api/local/world-peers"))
                .SingleOrDefault(value => value.GroupKey == groupKey && value.ProfileId == profile.Id);
            if (group is null || group.State.Root.OwnerPublicKey != signedRoster!.OwnerPublicKey) return false;
            var publicKey = signedRoster.Members.Single(member => member.DeviceId == deviceA).PublicKey;
            var peer = group.State.Peers.SingleOrDefault(value => value.DeviceId == deviceA && value.PublicKey == publicKey &&
                value.Authorized && !value.HasConflict);
            var session = group.State.Sessions.SingleOrDefault(value => value.DeviceId == deviceA && value.PublicKey == publicKey &&
                value.Authorized && !value.HasConflict && value.Session.SessionId == candidateSessionId &&
                value.Session.BranchHash == prepared!.BranchHash && value.Session.State == "Preparing");
            var route = $"https://{transportAddress}:{appPorts[4]}";
            if (peer is null || session is null || !peer.Routes.Any(value => value.Address == route &&
                string.Equals(value.CertificateSha256, fingerprint, StringComparison.OrdinalIgnoreCase))) return false;
            candidateSessionState = session.Session.State;
            return true;
        }

        async Task<FriendView> RequireCandidateConnectionAsync(HttpClient client, DateTimeOffset observedAfter)
        {
            var connected = await WaitForConnectedAsync(client, allowDisabled: true);
            LabRequire(connected.Endpoint == $"https://{transportAddress}:{appPorts[4]}" && connected.ConnectionCode is null &&
                connected.ProtocolCompatible && connected.LastConnectedUtc >= observedAfter && connected.HostId != Guid.Empty &&
                connected.HostId != originalHostId && connected.Profiles.Count == 1 && connected.Profiles.Single().Id == localProfileId &&
                connected.Profiles.Single().State == "Process running" && connected.Profiles.Single().RunOperationId == candidateSessionId,
                "FreshCandidateAuthenticationNotObserved");
            return connected;
        }
    }

    private static void LabRequire([DoesNotReturnIf(false)] bool value, string code)
    {
        if (!value) throw new PairingLabFailure(code);
    }

    private static async Task<T> PairingLabGetAsync<T>(HttpClient client, string path,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0)
    {
        try
        {
            return await client.GetFromJsonAsync<T>(path, Json) ?? throw new PairingLabFailure("LocalResponseMissing");
        }
        catch (HttpRequestException error)
        {
            // Export only the status and this public check's source line.
            // A URL, response body or raw exception can contain pairing data.
            throw new PairingLabFailure($"LocalGet{(int?)error.StatusCode ?? 0}At{sourceLine}");
        }
    }

    private static async Task PairingLabWaitAsync(Func<Task<bool>> check, string code, TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < (timeout ?? TimeSpan.FromSeconds(25)))
        {
            if (await check()) return;
            await Task.Delay(250);
        }
        throw new PairingLabFailure(code);
    }

    private static Process StartPairingLabApp(string path, string mode, int port, string data, string? lanAddress = null)
    {
        Directory.CreateDirectory(data);
        var start = new ProcessStartInfo(Path.GetFullPath(path))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "--staging", "--local-pairing-lab", mode, "--port", port.ToString() }) start.ArgumentList.Add(argument);
        start.Environment["TOGETHERSERVER_STAGING_DATA_DIR"] = data;
        start.Environment["TOGETHERSERVER_DATA_DIR"] = data + "-production-unused";
        start.Environment["TOGETHERSERVER_FIXTURE_ROOT"] = data;
        if (lanAddress is null) start.Environment.Remove("TOGETHERSERVER_LAB_LAN_ADDRESS");
        else start.Environment["TOGETHERSERVER_LAB_LAN_ADDRESS"] = lanAddress;
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

        internal bool HasExitedNow()
        {
            lock (sampleGate)
            {
                SampleCore();
                return !Alive;
            }
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

        internal object Report() => new
        {
            role = Role,
            processId = id,
            startedUtcTicks = startedTicks,
            alive = Alive,
            identityReview = IdentityReview,
            currentWorkingSetBytes = CurrentWorkingSetBytes,
            peakWorkingSetBytes = peakWorkingSet,
            observedCpuMs = cpuMs,
            resourceSampleUnavailable,
            visibleWindowSamples,
            peakVisibleWindows
        };
    }
}
