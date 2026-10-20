using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
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
            "synthetic-stop-copy-receipts", "password-permission-revoke", "password-reveal", "guided-hosting",
            "local-copy-hosting", "new-host-notice" };
        if (lanAddress is not null) stageNames.AddRange(["new-host-authenticated-connect", "new-host-permission-isolation", "new-host-stop-and-transfer"]);
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
            Kind = GameKinds.Fixture, Name = "Local pairing lab", ServerName = "Synthetic lab",
            WorldId = "lab-world", WorldSource = "New", WorldDirectory = Path.Combine(hostData, "worlds", "lab-world"),
            ExecutablePath = Path.GetFullPath(fixturePath), GamePort = FreeUdpPair(),
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
                    Profiles = [profile], MaxConcurrentServers = 1, CompanionBindAddress = transportAddress,
                    CompanionEndpoint = $"https://{transportAddress}:{appPorts[3]}", CompanionPort = appPorts[3]
                })).Ok, "HostSettingsRejected");
                // Configure each route before pairing; only explicit actions enable listeners.
                LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "CandidateHostModeRejected");
                LabRequire((await PutAsync<HostSettings, ActionResult>(first, "/api/local/settings", new()
                {
                    Profiles = [], CompanionBindAddress = transportAddress, CompanionPort = appPorts[4],
                    CompanionEndpoint = $"https://{transportAddress}:{appPorts[4]}", CompanionListeningEnabled = false,
                    RemoteControlsEnabled = false
                })).Ok, "CandidateSettingsRejected");
                LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(), "CandidateFriendModeRejected");
                if (lanAddress is not null && !runUx)
                {
                    LabRequire((await PostAsync<object, JsonElement>(second, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "ObserverHostModeRejected");
                    LabRequire((await PutAsync<HostSettings, ActionResult>(second, "/api/local/settings", new()
                    {
                        Profiles = [], CompanionBindAddress = transportAddress, CompanionPort = appPorts[5],
                        CompanionEndpoint = $"https://{transportAddress}:{appPorts[5]}", CompanionListeningEnabled = false
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
                signedRoster = await PairingLabGetAsync<SharedWorldRoster>(owner, $"/api/local/profiles/{profile.Id}/shared-world/governance");
                LabRequire(SharedWorldRosterTrust.Verify(signedRoster), "CurrentSignedRosterRequired");
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
                if (lanAddress is not null)
                {
                    // First preparation records the exact branch. Only then can
                    // its deliberately enabled listener satisfy the normal gates.
                    LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(), "CandidateListenerModeRejected");
                    before.Settings.CompanionListeningEnabled = true;
                    before.Settings.RemoteControlsEnabled = true;
                    LabRequire((await PutAsync<HostSettings, ActionResult>(first, "/api/local/settings", before.Settings)).Ok, "CandidateListenerRejected");
                    LabRequire((await PostAsync<object, JsonElement>(first, "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(), "CandidateListenerRestoreRejected");
                    await PairingLabWaitAsync(async () =>
                    {
                        var companion = await PairingLabGetAsync<JsonElement>(first, "/api/local/companion");
                        return companion.GetProperty("listenerActive").GetBoolean();
                    }, "CandidateListenerUnavailable");
                    WindowsListenerOwners.RequireTogetherServerOwner(appPorts[4], processes.Single(process => process.Role == "friend-a").Process);
                    prepared = await PostAsync<LocalHostingPrepareRequest, LocalHostingStatus>(first,
                        $"/api/local/friend/{profile.Id}/shared-world/hosting/prepare",
                        new(true, true, ServerFile: Path.GetFullPath(fixturePath), RequirePeerConnection: true));
                    LabRequire(prepared.ReadyForManualStart && prepared.LocalProfileId == exactLocalProfile &&
                        prepared.Remedy is not { Kind: "Connection" or "Ports" or "Review" }, "LanHostingPreparationRejected");
                    preparedBranch = (await PairingLabGetAsync<WorldSeparateCopyBranch[]>(first,
                        $"/api/local/friend/{profile.Id}/shared-world/recovery/separate")).Single(branch => branch.BranchHash == prepared.BranchHash);
                    LabRequire(SharedWorldSeparateCopyStore.Verify(preparedBranch) &&
                        preparedBranch.Offer.Roster.OwnerPublicKey == signedRoster!.OwnerPublicKey &&
                        preparedBranch.CandidatePublicKey == signedRoster.Members.Single(member => member.DeviceId == deviceA).PublicKey,
                        "PreparedCandidateBranchNotAuthorized");
                }
                var result = await PostAsync<object, JsonElement>(first,
                    $"/api/local/friend/{profile.Id}/shared-world/hosting/start", new
                    {
                        localProfileId = exactLocalProfile, branchHash = prepared.BranchHash,
                        requestId = (Guid?)null, acceptSplitWarning = true
                    });
                var after = await CandidateSnapshotAsync();
                if (lanAddress is null)
                {
                    // Ordinary peer-route rules deliberately refuse loopback.
                    LabRequire(!result.GetProperty("ok").GetBoolean() && prepared.Remedy?.Kind == "Connection" &&
                        result.GetProperty("code").GetString() == "LocalHostingActionRequired", "LoopbackHostingConnectionBlockerMissing");
                    LabRequire(after.Runs.All(run => run.ProcessId is null), "BlockedGuidedStartCreatedProcess");
                }
                else
                {
                    var run = after.Runs.Single(item => item.ProfileId == exactLocalProfile);
                    if (run.ProcessId is not null) TrackRun(run);
                    LabRequire(result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "SeparateCopyStarted" &&
                        run.ProcessId is not null && run.RunOperationId is not null, "GuidedLanStartRejected");
                    candidateSessionId = run.RunOperationId;
                    hostedCopy = true;
                    await PairingLabWaitAsync(async () => (await CandidateSnapshotAsync()).Runs
                        .Single(item => item.ProfileId == exactLocalProfile).State == "Process running", "GuidedFixtureHealthNotObserved");
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
                var run = started.Snapshot.Runs.Single(item => item.ProfileId == localProfileId);
                if (run.ProcessId is not null) TrackRun(run);
                LabRequire(started.Ok, "ExplicitLocalCopyStartRejected");
                _ = await WaitForRunStateAsync(first, localProfileId!.Value, "Process running");
                hostedCopy = true;
                LabRequire((await PostAsync<object, ActionResult>(first, $"/api/local/profiles/{localProfileId}/stop", new { })).Ok, "LocalCopyPipeStopRejected");
                _ = await WaitForRunStateAsync(first, localProfileId.Value, "Offline");
                return null;
            });
            await Stage("new-host-notice", async () =>
            {
                if (lanAddress is null) return "LoopbackPeerRouteCannotProveNewHostAnnouncementOrConnect";
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
                    await PairingLabWaitAsync(async () =>
                    {
                        await SyncPeersAsync(first);
                        await SyncPeersAsync(second);
                        return FriendLink.ReadReceivedLatest(vault)?.VersionHash == version.VersionHash;
                    }, "CandidateBranchTransferNotVerified");
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
            }
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
            if (lanAddress is not null)
                await Stage("new-host-receipt-confirmed", () =>
                {
                    // All app locks are released by the job check above. Read
                    // only this run's protected received-receipt ledger.
                    using var candidateData = new LocalData(friendData);
                    var receipts = candidateData.LoadProtectedJson<List<SharedWorldReceipt>>($"world-peer-receipts-{groupKey}.protected") ?? [];
                    LabRequire(receipts.Any(receipt => receipt.DeviceId == deviceB && receipt.ProfileId == profile.Id &&
                        receipt.VersionHash == receivedBranchVersionHash && SharedWorldReceiptTrust.Verify(receipt,
                            signedRoster!.Members.Single(member => member.DeviceId == deviceB).PublicKey)), "NewHostDidNotRecordVerifiedCopyReceipt");
                    return Task.FromResult<string?>(null);
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
                schema = 1, runId, root, startedUtc, endedUtc = DateTimeOffset.UtcNow, durationMs = clock.ElapsedMilliseconds,
                mode = runUx ? "SamePcLanUx" : lanAddress is null ? "LoopbackBlocker" : "SamePcLanApi",
                physicalPcCount = 1,
                outcome = failureCode is null ? stages.Any(stage => stage.State == "Skipped") ? "CompletedWithSkips" : "Passed" : "Failed",
                failedStage = failureCode is null ? null : failedStage,
                failureCode, stages, runOperations, syntheticCopyHosted = runUx ? (bool?)null : hostedCopy,
                uxStagePassed = uxPassed,
                apiHostChange = runUx ? null : new { discoveredHost, connectedOwner, connectedObserver, candidateSessionId,
                    candidateSessionState, transferredCandidateCopy },
                cleanup = new { jobActiveProcesses = job.ActiveCount(), killRemainingMembersOnHarnessExit = true },
                resources = new { sampledEveryMs = 250, peakProcessCount, peakWorkingSetBytes = peakWorkingSet,
                    currentWorkingSetBytes = processes.Sum(process => process.CurrentWorkingSetBytes) },
                windowObservation = new { sampledEveryMs = 250, samples = windowObservationSamples,
                    state = windowObservationFailure != 0 ? "Failed" : windowObservationSamples > 0 ? "Passed" : "NotObserved",
                    peakVisibleOwnedWindows, observedVisibleWindow = windowObservationFailure == 1,
                    observationUnavailable = windowObservationFailure == 2,
                    scope = "All job-member top-level windows, including app, Node, browser and fixture descendants; sampled observation, no focus or placement calls" },
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
            var app = processes.FirstOrDefault(process => process.Role == role);
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
            var result = await PostAsync<object, WorldPeerActionResult>(client, $"/api/local/world-peers/{groupKey}/sync", new { });
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

    private static Process StartPairingLabApp(string path, string mode, int port, string data, string? lanAddress = null)
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

        internal object Report() => new { role = Role, processId = id, startedUtcTicks = startedTicks,
            alive = Alive, identityReview = IdentityReview, currentWorkingSetBytes = CurrentWorkingSetBytes,
            peakWorkingSetBytes = peakWorkingSet, observedCpuMs = cpuMs, resourceSampleUnavailable,
            visibleWindowSamples, peakVisibleWindows };
    }
}
