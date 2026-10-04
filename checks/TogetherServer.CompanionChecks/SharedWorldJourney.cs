using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

// Isolated copies of the packaged app exercise the public companion route.
// The harness never starts a listener; only TogetherServer.exe owns HTTP ports.
internal static partial class SharedWorldJourney
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string appPath, string valheimFixturePath)
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "shared-world-journey",
            Guid.NewGuid().ToString("N")));
        var hostData = Path.Combine(root, "host");
        var friendAData = Path.Combine(root, "friend-a");
        var friendBData = Path.Combine(root, "friend-b");
        var friendCData = Path.Combine(root, "friend-c");
        var ports = AvailablePorts(3);
        var hostPort = ports[0];
        var companionPort = ports[1];
        var friendAPort = ports[2];
        var friendBPort = AvailablePorts(1, ports)[0];
        var candidatePort = AvailablePorts(1, hostPort, companionPort, friendAPort, friendBPort)[0];
        var friendCPort = AvailablePorts(1, hostPort, companionPort, friendAPort,
            friendBPort, candidatePort)[0];
        var candidateAPort = AvailablePorts(1, hostPort, companionPort, friendAPort,
            friendBPort, friendCPort, candidatePort)[0];
        var gamePort = AvailableGamePort();
        var world = Path.Combine(hostData, "worlds", "disposable-world");
        Directory.CreateDirectory(world);
        var profile = new ServerProfile
        {
            Kind = GameKinds.Valheim,
            Name = "Disposable shared world",
            ServerName = "Fixture \"Valheim\"",
            WorldSource = "New",
            WorldId = "fixture-world",
            WorldDirectory = world,
            GamePort = gamePort,
            ExecutablePath = valheimFixturePath,
            Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        var settings = new HostSettings
        {
            MaxConcurrentServers = 1,
            Profiles = [profile],
            CompanionEndpoint = $"https://127.0.0.1:{companionPort}",
            CompanionPort = companionPort,
            CompanionBindAddress = "127.0.0.1"
        };
        Process? host = null;
        Process? friendA = null;
        Process? friendB = null;
        Process? friendC = null;
        var passed = 0;
        try
        {
            host = StartApp(appPath, "--host", hostPort, hostData);
            friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
            friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(friendAPort),
                WaitLocalAsync(friendBPort));
            using var owner = LocalClient(hostPort);
            using var aLocal = LocalClient(friendAPort);
            using var bLocal = LocalClient(friendBPort);
            using var cLocal = LocalClient(friendCPort);
            Require((await PutAsync<HostSettings, ActionResult>(owner,
                "/api/local/settings", settings)).Ok, "disposable Host settings failed");
            Require((await PostAsync<ValheimPasswordRequest, ActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/password", new("fixture-pass-123"))).Ok,
                "disposable game password failed");
            var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner,
                $"/api/local/servers/{profile.Id}/invite",
                new(false, true, true, DeviceLimit: 3));
            Require(invite.GetProperty("ok").GetBoolean() &&
                invite.GetProperty("listenerActive").GetBoolean(),
                "packaged Host did not open its deliberate HTTPS listener");
            var code = invite.GetProperty("password").GetString();
            Require(!string.IsNullOrWhiteSpace(code), "invite has no code");
            Require((await PostAsync<FriendPairRequest, FriendActionResult>(aLocal,
                "/api/local/friend/pair", new(code!))).Ok, "Friend A did not pair");
            var deviceA = await OnlyDeviceAsync(owner, profile.Id);
            Require((await PostAsync<FriendPairRequest, FriendActionResult>(bLocal,
                "/api/local/friend/pair", new(code!))).Ok, "Friend B did not pair");
            var devices = await DeviceIdsAsync(owner, profile.Id);
            Require(devices.Count == 2 && devices.Contains(deviceA),
                "two Friends did not receive separate device identities");
            var deviceB = devices.Single(id => id != deviceA);
            var firstAPoll = await PollAsync(aLocal);
            var firstBPoll = await PollAsync(bLocal);
            Require(firstAPoll.State == "Connected" && firstBPoll.State == "Connected" &&
                firstAPoll.ConnectionId != Guid.Empty && firstBPoll.ConnectionId != Guid.Empty,
                "both packaged Friends did not authenticate through pinned HTTPS");
            var aConnectionId = firstAPoll.ConnectionId;
            var bConnectionId = firstBPoll.ConnectionId;
            Require((await PutAsync<SharedWorldConsentRequest, SharedWorldResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world", new(true))).Ok,
                "owner could not enable sharing");
            foreach (var id in devices)
                Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                    $"/api/local/devices/{id}/shared-world/{profile.Id}", new(true))).Ok,
                    "owner could not grant Receive separately to both PCs");
            var beforeConsent = await PostAsync<object, ReceivedSharedWorldResult>(bLocal,
                $"/api/local/friend/{profile.Id}/shared-world/check", new { });
            Require(!beforeConsent.Ok && beforeConsent.Code == "ConsentRequired",
                "a Friend accepted signed sharing before this PC consented");
            foreach (var client in new[] { aLocal, bLocal })
                Require((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(client,
                    $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok,
                    "a Friend PC could not consent to receiving");
            Require(!(await PostAsync<object, ReceivedSharedWorldResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/pull", new { })).Ok,
                "a save was received before a confirmed graceful Stop");
            var beforeSave = await GetAsync<ReceivedSharedWorldStatus>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(beforeSave.State == "Transfer unavailable" && beforeSave.Error is not null,
                "a missing Host save was misreported as a stalled transfer");

            File.WriteAllText(Path.Combine(world, "world.dat"), "first verified fixture change");
            await StartAndStopAsync(owner, profile.Id);
            var first = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world");
            Require(first.Latest is { Number: 1 }, "the first signed save was not published");
            var firstVersion = first.Latest!;
            await PollAsync(aLocal);
            await PollAsync(bLocal);
            await WaitVersionAsync(aLocal, profile.Id, 1);
            await WaitVersionAsync(bLocal, profile.Id, 1);
            await WaitCopiesAsync(owner, profile.Id, 2);
            var verifiedStatus = await GetAsync<ReceivedSharedWorldStatus>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(verifiedStatus.ThisPcVersion == 1 && verifiedStatus.Error is null &&
                verifiedStatus.State != "Stalled" && verifiedStatus.State != "Low space",
                "verified catch-up left a stale transfer alert");
            var aRoot = ReceiverRoot(friendAData, deviceA, profile.Id);
            var bRoot = ReceiverRoot(friendBData, deviceB, profile.Id);
            Require(File.ReadAllText(Path.Combine(aRoot, firstVersion.VersionHash,
                        "payload", "world.dat")) == "first verified fixture change" &&
                    File.ReadAllText(Path.Combine(bRoot, firstVersion.VersionHash,
                        "payload", "world.dat")) == "first verified fixture change",
                "the two received payloads did not match the completed save");
            Console.WriteLine("PASS two packaged Friend PCs independently receive and attest one signed save");
            passed++;

            StopApp(friendA);
            friendA = null;
            StopApp(friendB);
            friendB = null;
            using (var stream = new FileStream(Path.Combine(world, "world.dat"),
                       FileMode.Create, FileAccess.Write))
                stream.SetLength(4L * SharedWorldService.ChunkBytes);
            await StartAndStopAsync(owner, profile.Id);
            var second = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world");
            Require(second.Latest is { Number: 2 }, "second signed save was not published");
            var secondVersion = second.Latest!;
            Require(File.Exists(Path.Combine(bRoot, firstVersion.VersionHash,
                        "payload", "world.dat")) &&
                    !File.Exists(Path.Combine(bRoot, secondVersion.VersionHash,
                        "payload", "world.dat")),
                "offline Friend B did not remain one save behind");
            Console.WriteLine("PASS an offline PC keeps its verified earlier copy while the Host advances");
            passed++;

            friendA = StartApp(appPath, "--friend", friendAPort, friendAData,
                receiveDelayMs: 3000);
            await WaitLocalAsync(friendAPort);
            await PollAsync(aLocal);
            var partial = Path.Combine(aRoot, ".partial-" + secondVersion.VersionHash,
                "payload", "world.dat");
            await WaitForAsync(() => File.Exists(partial) &&
                new FileInfo(partial).Length >= SharedWorldService.ChunkBytes,
                "Friend A did not retain one bounded chunk before interruption", 15000);
            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceA}/shared-world/{profile.Id}", new(false))).Ok,
                "owner could not revoke Receive while a chunk was pending");
            await Task.Delay(3500);
            var revoked = await GetAsync<ReceivedSharedWorldStatus>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(revoked.ThisPcVersion == 1 && File.Exists(partial),
                "revocation committed the pending save or removed resumable data");
            StopApp(friendA);
            friendA = null;
            Require((await PutAsync<SharedWorldGrantRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceA}/shared-world/{profile.Id}", new(true))).Ok,
                "owner could not deliberately restore Receive");
            friendA = StartApp(appPath, "--friend", friendAPort, friendAData,
                beforeChunkDelayMs: 3000);
            await WaitLocalAsync(friendAPort);
            await PollAsync(aLocal);
            await WaitSharedStateAsync(aLocal, profile.Id, "Receiving",
                SharedWorldService.ChunkBytes);
            Require((await PutAsync<HostControlPolicyChange, ActionResult>(owner,
                "/api/local/settings/control-policy", new(false))).Ok,
                "disposable Host listener did not close during a partial transfer");
            await WaitSharedStateAsync(aLocal, profile.Id, "Transfer interrupted");
            Require((await PutAsync<HostControlPolicyChange, ActionResult>(owner,
                "/api/local/settings/control-policy", new(true, true))).Ok,
                "disposable Host listener did not reopen for the retry");
            var retry = PostAsync<object, ReceivedSharedWorldResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
            await WaitSharedStateAsync(aLocal, profile.Id, "Receiving",
                SharedWorldService.ChunkBytes);
            Require((await PutAsync<HostControlPolicyChange, ActionResult>(owner,
                "/api/local/settings/control-policy", new(false))).Ok,
                "disposable Host listener did not close during the no-progress retry");
            Require((await retry).Code == "NetworkUnavailable",
                "a dropped post-payload HTTPS request did not report a retryable network failure");
            await WaitSharedStateAsync(aLocal, profile.Id, "Stalled");
            Require((await PutAsync<HostControlPolicyChange, ActionResult>(owner,
                "/api/local/settings/control-policy", new(true, true))).Ok,
                "disposable Host listener did not reopen for resumed receipt");
            await WaitVersionAsync(aLocal, profile.Id, 2);
            var resumed = await GetAsync<ReceivedSharedWorldStatus>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(resumed.Error is null && resumed.State != "Stalled",
                "verified resumed receipt kept a stale transport stall");
            Require(File.Exists(Path.Combine(aRoot, secondVersion.VersionHash,
                        "payload", "world.dat")),
                "interrupted transfer did not resume into a verified version");
            Console.WriteLine("PASS interrupted chunks survive restart; real route loss stalls only repeated no-progress retries and clears on receipt");
            passed++;

            friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
            await WaitLocalAsync(friendBPort);
            await PollAsync(bLocal);
            await WaitVersionAsync(bLocal, profile.Id, 2);
            await WaitCopiesAsync(owner, profile.Id, 2);
            Require(File.Exists(Path.Combine(bRoot, firstVersion.VersionHash,
                        "payload", "world.dat")),
                "catch-up deleted the earlier verified copy");
            Console.WriteLine("PASS offline Friend B catches up and retains its earlier verified version");
            passed++;

            var tamperedFile = Path.Combine(aRoot, secondVersion.VersionHash,
                "payload", "world.dat");
            using (var stream = new FileStream(tamperedFile, FileMode.Open, FileAccess.Write))
                stream.WriteByte(42);
            var tamperedStatus = await GetAsync<ReceivedSharedWorldStatus>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            var tamperedPull = await PostAsync<object, ReceivedSharedWorldResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
            var healthyB = await GetAsync<ReceivedSharedWorldStatus>(bLocal,
                $"/api/local/friend/{profile.Id}/shared-world");
            Require(tamperedStatus.State == "Error" && tamperedStatus.ThisPcVersion is null &&
                !tamperedPull.Ok && healthyB.ThisPcVersion == 2 &&
                File.Exists(Path.Combine(aRoot, firstVersion.VersionHash,
                    "payload", "world.dat")),
                "tampered copy was marked ready, or the independent verified copy was lost");
            Console.WriteLine("PASS payload tampering fails verification without erasing other PCs' copies");
            passed++;

            // The packaged route reads the real disk. Filling a machine to test its
            // reserve is unsafe, so the deterministic boundary is exercised here.
            const long reserve = 1024L * 1024 * 1024;
            Require(!FriendLink.HasReceiverReserve(reserve, 1) &&
                !FriendLink.HasReceiverReserve(reserve - 1, 0) &&
                FriendLink.HasReceiverReserve(reserve + 1, 1),
                "1 GiB receiving reserve boundary changed");
            Console.WriteLine("PASS 1 GiB disk reserve boundary (policy check; no disk was filled)");
            passed++;

            StopApp(host);
            host = null;
            var disconnected = await PollAsync(bLocal);
            Require(disconnected.State == "Disconnected/Unknown" &&
                (await GetAsync<ReceivedSharedWorldStatus>(bLocal,
                    $"/api/local/friend/{profile.Id}/shared-world")).ThisPcVersion == 2,
                "Host loss erased the Friend's verified copy or was reported as connected");
            host = StartApp(appPath, "--host", hostPort, hostData);
            await WaitLocalAsync(hostPort);
            var returned = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world");
            Require(returned.Latest?.VersionHash == secondVersion.VersionHash &&
                (await WaitConnectedAsync(bLocal)).State == "Connected",
                "ordinary Host restart did not retain signed history and reauthenticate Friend B");
            Console.WriteLine("PASS ordinary Host restart keeps signed history and saved Friend access");
            passed++;

            // Restore the one byte this disposable fixture changed above, then
            // require the app to verify the original signed bytes again.
            StopApp(friendA);
            friendA = null;
            using (var stream = new FileStream(tamperedFile, FileMode.Open, FileAccess.Write))
                stream.WriteByte(0);
            friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
            await WaitLocalAsync(friendAPort);
            await PollAsync(aLocal);
            await WaitVersionAsync(aLocal, profile.Id, 2);

            friendC = StartApp(appPath, "--friend", friendCPort, friendCData);
            await WaitLocalAsync(friendCPort);
            Require((await PostAsync<FriendPairRequest, FriendActionResult>(cLocal,
                "/api/local/friend/pair", new(code!))).Ok,
                "Friend C could not join the disposable recovery group");
            var deviceC = (await DeviceIdsAsync(owner, profile.Id))
                .Single(id => id != deviceA && id != deviceB);
            Require((await PutAsync<SharedWorldDeviceGrantsRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceC}/shared-world/{profile.Id}/grants",
                new(new SharedWorldGrants(RecoveryVoter: true)))).Ok,
                "owner could not appoint a voter without Receive access");
            var voterReview = await PostAsync<WorldHistoryReviewRequest, WorldHistoryReviewResult>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/history/review", new());
            Require(!voterReview.Ok && voterReview.Code == "GroupReviewRequired" &&
                voterReview.GroupId is not null && voterReview.OwnerPublicKey is not null,
                "voter-only PC did not require explicit signed group review");
            var voterConfirmed = await PostAsync<WorldHistoryReviewRequest, WorldHistoryReviewResult>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/history/review",
                new(voterReview.GroupId, voterReview.OwnerPublicKey));
            Require(voterConfirmed.Ok && voterConfirmed.Code == "HistoryReviewed" &&
                !(await GetAsync<ReceivedSharedWorldStatus>(cLocal,
                    $"/api/local/friend/{profile.Id}/shared-world")).Consented,
                "voter-only review accidentally granted Receive consent");
            StopApp(friendC);
            friendC = null;

            foreach (var id in devices)
                Require((await PutAsync<SharedWorldDeviceGrantsRequest, PairingDecision>(owner,
                    $"/api/local/devices/{id}/shared-world/{profile.Id}/grants",
                    new(new SharedWorldGrants(Receive: true, EligibleHost: true,
                        RecoveryVoter: true)))).Ok,
                    "owner could not appoint two independent recovery voters");
            foreach (var client in new[] { aLocal, bLocal })
                Require((await PostAsync<object, ReceivedSharedWorldResult>(client,
                    $"/api/local/friend/{profile.Id}/shared-world/check", new { })).Ok,
                    "a Friend did not verify the updated signed recovery roster");
            Require((await PostAsync<object, JsonElement>(bLocal,
                "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(),
                "Friend B could not edit its future Host address");
            Require((await PutAsync<HostSettings, ActionResult>(bLocal,
                "/api/local/settings", new HostSettings
                {
                    CompanionEndpoint = $"https://127.0.0.1:{candidatePort}",
                    CompanionPort = candidatePort,
                    CompanionBindAddress = "127.0.0.1"
                })).Ok, "Friend B could not save its direct candidate address");
            Require((await PostAsync<object, JsonElement>(bLocal,
                "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(),
                "candidate PC did not return to Friend mode");
            Require((await PostAsync<object, JsonElement>(aLocal,
                "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(),
                "Friend A could not edit its future Host address");
            Require((await PutAsync<HostSettings, ActionResult>(aLocal,
                "/api/local/settings", new HostSettings
                {
                    CompanionEndpoint = $"https://127.0.0.1:{candidateAPort}",
                    CompanionPort = candidateAPort,
                    CompanionBindAddress = "127.0.0.1"
                })).Ok, "Friend A could not save its direct candidate address");
            Require((await PostAsync<object, JsonElement>(aLocal,
                "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(),
                "Friend A did not return to Friend mode");

            StopApp(host);
            host = null;
            await Task.WhenAll(PollAsync(aLocal), PollAsync(bLocal));
            var early = await PostAsync<object, WorldAuthorityOfferResult>(bLocal,
                $"/api/local/friend/{profile.Id}/shared-world/recovery/offer", new { });
            Require(!early.Ok && early.Code == "HostLossNotConfirmed",
                "takeover was offered before two minutes of failed secure Host checks");
            var lossWait = Stopwatch.StartNew();
            while (lossWait.Elapsed < SharedWorldHostLoss.RequiredDelay + TimeSpan.FromSeconds(2))
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                var observations = await Task.WhenAll(PollAsync(aLocal), PollAsync(bLocal));
                Require(observations.All(item => item.State == "Disconnected/Unknown"),
                    "a Friend saw the stopped Host as connected during recovery wait");
            }
            var offerResult = await PostAsync<object, WorldAuthorityOfferResult>(bLocal,
                $"/api/local/friend/{profile.Id}/shared-world/recovery/offer", new { });
            Require(offerResult is { Ok: true, Offer: { } } &&
                SharedWorldElection.VerifyOffer(offerResult.Offer) &&
                offerResult.Offer.Version.VersionHash == secondVersion.VersionHash &&
                offerResult.Offer.Proposal.CandidateAddress == $"https://127.0.0.1:{candidatePort}",
                $"candidate did not arm an exact, signed direct-address offer: {offerResult.Code} {offerResult.Message}");
            var offer = offerResult.Offer!;
            Require((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/consent", new(false))).Ok,
                "Friend A could not withdraw consent before a stale vote attempt");
            var staleVote = await PostAsync<WorldAuthorityOffer, WorldAuthorityVoteAction>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/recovery/vote", offer);
            Require(!staleVote.Ok && staleVote.Code == "ConsentRequired",
                "a stale signed offer bypassed this PC's withdrawn consent");
            Require((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok,
                "Friend A could not restore explicit consent for the vote");
            var firstVote = await PostAsync<WorldAuthorityOffer, WorldAuthorityVoteAction>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/recovery/vote", offer);
            Require(firstVote is
            {
                Ok: true, Code: "VoteRecorded", Votes: 1, Required: 2,
                Decision: null
            },
                $"first signed vote did not wait for the designated majority: {firstVote.Code} {firstVote.Message}");
            var majority = await PostAsync<WorldAuthorityOffer, WorldAuthorityVoteAction>(bLocal,
                $"/api/local/friend/{profile.Id}/shared-world/recovery/vote", offer);
            Require(majority is
            {
                Ok: true, Code: "MajorityRecorded", Votes: 2, Required: 2,
                Decision: { }
            } && WorldAuthorityTrust.Verify(majority.Decision) &&
                majority.Decision.Votes.Select(vote => vote.VoterDeviceId).ToHashSet()
                    .SetEquals(devices),
                $"two separate PC signatures did not confirm the exact save: {majority.Code} {majority.Message}");
            var decision = majority.Decision!;
            Require((await PostAsync<object, JsonElement>(bLocal,
                "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(),
                "candidate PC could not inspect its local managed runs");
            Require(!(await GetAsync<HostSnapshot>(bLocal, "/api/local/snapshot"))
                    .Runs.Any(run => run.State != "Offline"),
                "signed takeover unexpectedly started a game server");
            Require((await PostAsync<object, JsonElement>(bLocal,
                "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(),
                "candidate PC did not return to Friend mode after the local run check");
            Console.WriteLine("PASS two-minute loss and two signed PC votes confirm one exact save without Start");
            passed++;

            StopApp(friendA);
            friendA = null;
            StopApp(friendB);
            friendB = null;
            FriendConfiguration aCredentials;
            using (var savedFriend = new LocalData(friendAData))
                aCredentials = savedFriend.LoadProtectedJson<FriendConfiguration>(
                    $"friend-{aConnectionId:N}.protected") ??
                    throw new Exception("disposable Friend A credential could not be read");
            FriendConfiguration bCredentials;
            using (var savedFriend = new LocalData(friendBData))
                bCredentials = savedFriend.LoadProtectedJson<FriendConfiguration>(
                    $"friend-{bConnectionId:N}.protected") ??
                    throw new Exception("disposable Friend B credential could not be read");
            host = StartApp(appPath, "--host", hostPort, hostData);
            await WaitLocalAsync(hostPort);
            File.WriteAllText(Path.Combine(world, "world.dat"), "old Host split save retained");
            await StartReadyAsync(owner, profile.Id);
            var planned = await PostAsync<PreparePlannedHandoffRequest, PlannedHandoffResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/handoff/prepare",
                new(deviceA, $"https://127.0.0.1:{candidateAPort}"));
            Require(planned is { Ok: true, Code: "WaitingForSuccessorCopy", Version: { Number: 3 } } &&
                planned.Version.ParentHash == secondVersion.VersionHash &&
                planned.Version.VersionHash != decision.Version.VersionHash,
                $"old Host did not sign an independent final save: {planned.Code} {planned.Message}");
            var splitVersion = planned.Version!;
            friendA = StartApp(appPath, "--friend", friendAPort, friendAData);
            await WaitLocalAsync(friendAPort);
            await PollAsync(aLocal);
            await WaitVersionAsync(aLocal, profile.Id, 3);
            await WaitCopiesAsync(owner, profile.Id, 1);
            var handoff = await PostAsync<object, PlannedHandoffResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/handoff/complete", new { });
            Require(handoff is { Ok: true, Code: "OldHostFenced", Authority: { } } &&
                WorldAuthorityTrust.Verify(handoff.Authority) &&
                handoff.Authority.Proposal.Kind == "Planned" &&
                handoff.Authority.Version.VersionHash == splitVersion.VersionHash,
                $"owner did not sign the independently staged handoff: {handoff.Code} {handoff.Message}");
            var plannedAuthority = handoff.Authority!;
            var staged = await PostAsync<object, PlannedHandoffStageResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/handoff/stage", new { });
            Require(staged.Ok && staged.VersionHash == splitVersion.VersionHash,
                $"successor did not stage the exact signed planned branch: {staged.Code} {staged.Message}");
            friendB = StartApp(appPath, "--friend", friendBPort, friendBData);
            await WaitLocalAsync(friendBPort);
            var authorityLog = Path.Combine(hostData, "shared-worlds", profile.Id.ToString("N"),
                "authority", "records.jsonl");
            for (var attempt = 0; attempt < 30 && !RecordedDecision(authorityLog, decision); attempt++)
            {
                await PollAsync(bLocal);
                if (!RecordedDecision(authorityLog, decision)) await Task.Delay(250);
            }
            Require(RecordedDecision(authorityLog, decision) &&
                RecordedDecision(authorityLog, plannedAuthority),
                "old Host did not retain both independently signed authority branches");
            var competing = (await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world")).Authority;
            Require(competing?.CompetingHeads is { Count: 2 } &&
                competing.CompetingHeads.Select(head => head.RecordHash).ToHashSet()
                    .SetEquals([decision.RecordHash, plannedAuthority.RecordHash]),
                "old Host did not expose both signed competing heads for review");
            var blockedStart = await PostAsync<object, ActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/start", new { });
            var blockedSharing = await PutAsync<SharedWorldConsentRequest, SharedWorldResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world", new(true));
            using var oldHostClient = PinnedFriendClient(bCredentials);
            using var manifest = await oldHostClient.GetAsync(
                $"api/companion/servers/{profile.Id}/shared-world");
            using var chunk = await oldHostClient.GetAsync(
                $"api/companion/servers/{profile.Id}/shared-world/{splitVersion.VersionHash}/files/0/chunks/0");
            var afterReturn = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world");
            var retainedRoot = Path.Combine(hostData, "shared-worlds", profile.Id.ToString("N"),
                splitVersion.GroupId.ToString("N"), splitVersion.Number.ToString());
            var retainedManifest = JsonSerializer.Deserialize<SharedWorldVersion>(
                File.ReadAllText(Path.Combine(retainedRoot, "version.json")), Json);
            Require(!blockedStart.Ok && blockedStart.Code == "SharedWorldAuthorityBlocked" &&
                !blockedSharing.Ok && blockedSharing.Code == "SuccessorRosterReadOnly" &&
                manifest.StatusCode == HttpStatusCode.Forbidden &&
                chunk.StatusCode == HttpStatusCode.Forbidden &&
                afterReturn.Latest is null &&
                retainedManifest?.VersionHash == splitVersion.VersionHash &&
                SharedWorldService.VerifySignature(retainedManifest) &&
                File.ReadAllText(Path.Combine(retainedRoot, "payload", "world.dat")) ==
                    "old Host split save retained" &&
                File.ReadAllText(Path.Combine(world, "world.dat")) == "old Host split save retained" &&
                File.Exists(Path.Combine(bRoot, secondVersion.VersionHash, "payload", "world.dat")),
                $"old Host fence/retention mismatch: Start={blockedStart.Code}, " +
                $"sharing={blockedSharing.Code}, manifest={(int)manifest.StatusCode}, " +
                $"chunk={(int)chunk.StatusCode}, fencedReadModel={afterReturn.Latest is null}, " +
                $"retainedSignedSave={retainedManifest?.VersionHash == splitVersion.VersionHash && SharedWorldService.VerifySignature(retainedManifest)}, " +
                $"retainedWorld={File.ReadAllText(Path.Combine(world, "world.dat")) == "old Host split save retained"}, " +
                $"retainedFriendCopy={File.Exists(Path.Combine(bRoot, secondVersion.VersionHash, "payload", "world.dat"))}");
            Console.WriteLine("PASS independent planned and quorum histories remain signed and fenced on old Host return");
            passed++;

            var aReview = await PostAsync<WorldHistoryReviewRequest, WorldHistoryReviewResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/history/review", new());
            friendC = StartApp(appPath, "--friend", friendCPort, friendCData);
            await WaitLocalAsync(friendCPort);
            var cReview = await PostAsync<WorldHistoryReviewRequest, WorldHistoryReviewResult>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/history/review", new());
            Require(aReview is { Ok: true, Code: "HistoryReviewed", CompetingHeads: 2 } &&
                cReview is { Ok: true, Code: "HistoryReviewed", CompetingHeads: 2 } &&
                (await GetAsync<WorldResolutionChoice[]>(aLocal,
                    $"/api/local/friend/{profile.Id}/shared-world/resolution/heads")) is { Length: 2 },
                $"two PCs did not independently verify both signed heads: A={aReview.Code}, C={cReview.Code}");
            Require((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok,
                "voter-only PC could not consent to a reviewed resolution vote");
            var choices = await GetAsync<WorldResolutionChoice[]>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/resolution/heads");
            var selected = choices.Single(choice => choice.RecordHash == plannedAuthority.RecordHash);
            Require(selected.AvailableHere && choices.Single(choice =>
                    choice.RecordHash == decision.RecordHash).Version == secondVersion.Number,
                "resolution choices did not distinguish verified local copy from other signed history");
            var ownerOffer = await PostAsync<object, WorldAuthorityOfferResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/resolution/owner-offer/{selected.RecordHash}",
                new { });
            Require(ownerOffer is { Ok: true, Code: "ResolutionOfferArmed", Offer: { } } &&
                ownerOffer.Offer.Proposal.Kind == "ResolutionOwnerOverride" &&
                ownerOffer.Offer.Roster.OwnerOverride &&
                ownerOffer.Offer.Proposal.CompetingHeadHashes?.ToHashSet()
                    .SetEquals([decision.RecordHash, plannedAuthority.RecordHash]) == true,
                $"owner-enabled resolution did not name the exact signed split: {ownerOffer.Code} {ownerOffer.Message}");
            var ownerOfferHash = WorldAuthorityTrust.ProposalHash(ownerOffer.Offer!.Proposal);
            JsonElement[]? pendingOffers = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await PollAsync(aLocal);
                pendingOffers = await GetAsync<JsonElement[]>(owner,
                    $"/api/local/profiles/{profile.Id}/shared-world/resolution/offers");
                if (pendingOffers.Any(item => item.GetProperty("proposalHash").GetString() == ownerOfferHash))
                    break;
                await Task.Delay(250);
            }
            Require(pendingOffers?.Any(item => item.GetProperty("proposalHash").GetString() ==
                    ownerOfferHash) == true,
                "fenced old Host did not receive the candidate's authenticated owner offer");
            using var ownerOfferClient = PinnedFriendClient(aCredentials);
            using var ownerOfferReceipt = await ownerOfferClient.PostAsJsonAsync(
                $"api/companion/servers/{profile.Id}/shared-world/resolution/owner-offer",
                ownerOffer.Offer);
            var receiptBody = await ownerOfferReceipt.Content.ReadFromJsonAsync<JsonElement>();
            Require(ownerOfferReceipt.IsSuccessStatusCode &&
                receiptBody.GetProperty("code").GetString() == "ResolutionOfferRecorded" &&
                receiptBody.GetProperty("proposalHash").GetString() == ownerOfferHash,
                "old Host did not acknowledge the exact authenticated owner offer");
            var approved = await PostAsync<object, JsonElement>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/resolution/approve/{ownerOfferHash}",
                new { });
            Require(approved.GetProperty("code").GetString() == "OwnerOverrideRecorded" &&
                approved.TryGetProperty("recordHash", out var approvedHash) &&
                approvedHash.GetString() is { Length: 64 },
                "owner's typed approval did not produce a signed resolution");
            var resolvedHash = approved.GetProperty("recordHash").GetString()!;
            var resolved = ReadRecordedAuthority(authorityLog, resolvedHash);
            Require(resolved is { Schema: 2, OwnerSignature: not null } &&
                resolved.Proposal.Kind == "ResolutionOwnerOverride" &&
                resolved.Votes.Count == 0 && WorldAuthorityTrust.Verify(resolved) &&
                resolved.Proposal.CompetingHeadHashes?.ToHashSet()
                    .SetEquals([decision.RecordHash, plannedAuthority.RecordHash]) == true &&
                resolved.Version.VersionHash == splitVersion.VersionHash &&
                ReadRecordedAuthorities(authorityLog).Count >= 3 &&
                !(await PostAsync<object, ActionResult>(owner,
                    $"/api/local/profiles/{profile.Id}/start", new { })).Ok,
                "owner override did not resolve exactly two retained branches or preserve old-Host fence");
            Console.WriteLine("PASS owner override chooses one verified branch by signed approval and retains both competing records");
            passed++;

            passed += await RunOverrideOffResolutionAsync(appPath, valheimFixturePath);
            Console.WriteLine($"Shared Worlds packaged journey: {passed} groups passed, 0 failed, 0 recovery scenarios skipped. Disposable data: {root}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL Shared Worlds packaged journey after {passed} passing groups: {ex}");
            throw;
        }
        finally
        {
            if (host is { HasExited: false })
            {
                try
                {
                    using var owner = LocalClient(hostPort);
                    var snapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
                    if (snapshot?.Runs.SingleOrDefault(run => run.ProfileId == profile.Id)?.State != "Offline")
                        _ = await PostAsync<object, ActionResult>(owner,
                            $"/api/local/profiles/{profile.Id}/stop", new { });
                }
                catch (Exception cleanupError)
                { Console.Error.WriteLine("Disposable run cleanup: " + cleanupError.Message); }
            }
            StopApp(friendB);
            StopApp(friendA);
            StopApp(friendC);
            StopApp(host);
        }
    }

    private static string ReceiverRoot(string data, Guid device, Guid profile) =>
        Path.Combine(data, "received-shared-worlds", device.ToString("N"), profile.ToString("N"));

    private static bool RecordedDecision(string path, WorldAuthorityRecord expected)
    {
        try
        {
            return File.Exists(path) && File.ReadAllLines(path).Any(line =>
            {
                var recorded = JsonSerializer.Deserialize<WorldAuthorityRecord>(line, Json);
                return recorded?.RecordHash == expected.RecordHash &&
                    WorldAuthorityTrust.Verify(recorded);
            });
        }
        catch (IOException) { return false; }
    }

    private static IReadOnlyList<WorldAuthorityRecord> ReadRecordedAuthorities(string path) =>
        File.ReadAllLines(path).Select(line =>
            JsonSerializer.Deserialize<WorldAuthorityRecord>(line, Json) ??
            throw new InvalidDataException("A signed authority record is unreadable.")).ToArray();

    private static WorldAuthorityRecord? ReadRecordedAuthority(string path, string hash) =>
        ReadRecordedAuthorities(path).SingleOrDefault(record => record.RecordHash == hash);

    private static HttpClient PinnedFriendClient(FriendConfiguration friend)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.RawData), Convert.FromHexString(friend.Fingerprint))
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(friend.Endpoint),
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            friend.Credential);
        client.DefaultRequestHeaders.Add("X-Device-Id", friend.DeviceId.ToString());
        client.DefaultRequestHeaders.Add(CompanionProtocol.HeaderName,
            CompanionProtocol.Current.ToString());
        return client;
    }

    private static async Task StartAndStopAsync(HttpClient owner, Guid profileId)
    {
        await StartReadyAsync(owner, profileId);
        var stop = await PostAsync<object, ActionResult>(owner,
            $"/api/local/profiles/{profileId}/stop", new { });
        Require(stop.Ok, $"fixture graceful Stop failed: {stop.Code} {stop.Message}");
    }

    private static async Task StartReadyAsync(HttpClient owner, Guid profileId)
    {
        var start = await PostAsync<object, ActionResult>(owner,
            $"/api/local/profiles/{profileId}/start", new { });
        Require(start.Ok, $"fixture Start failed: {start.Code} {start.Message}");
        var ready = false;
        for (var attempt = 0; attempt < 100 && !ready; attempt++)
        {
            var snapshot = await owner.GetFromJsonAsync<HostSnapshot>("/api/local/snapshot");
            ready = snapshot?.Runs.Single(run => run.ProfileId == profileId).State == "Ready";
            if (!ready) await Task.Delay(100);
        }
        Require(ready, "fixture did not reach Ready before Stop");
    }

    private static async Task<Guid> OnlyDeviceAsync(HttpClient owner, Guid profileId)
    {
        var ids = await DeviceIdsAsync(owner, profileId);
        Require(ids.Count == 1, "first pairing did not issue exactly one device identity");
        return ids.Single();
    }

    private static async Task<IReadOnlyList<Guid>> DeviceIdsAsync(HttpClient owner, Guid profileId)
    {
        var companion = await GetAsync<JsonElement>(owner, "/api/local/companion");
        return companion.GetProperty("devices").EnumerateArray()
            .Where(device => device.GetProperty("profileId").GetGuid() == profileId)
            .Select(device => device.GetProperty("id").GetGuid()).ToArray();
    }

    private static async Task<FriendView> PollAsync(HttpClient friend) =>
        await PostAsync<object, FriendView>(friend, "/api/local/friend/poll", new { });

    private static async Task<FriendView> WaitConnectedAsync(HttpClient friend)
    {
        FriendView? latest = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            latest = await PollAsync(friend);
            if (latest.State == "Connected") return latest;
            await Task.Delay(250);
        }
        throw new Exception($"Friend did not reconnect: {latest?.State} {latest?.ConnectionCode}");
    }

    private static async Task WaitVersionAsync(HttpClient friend, Guid profileId, long expected)
    {
        ReceivedSharedWorldStatus? latest = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            latest = await GetAsync<ReceivedSharedWorldStatus>(friend,
                $"/api/local/friend/{profileId}/shared-world");
            if (latest.ThisPcVersion == expected) return;
            if (attempt % 10 == 9) await PollAsync(friend);
            await Task.Delay(250);
        }
        throw new Exception($"Friend did not verify version {expected}: {latest?.State} {latest?.Error}");
    }

    private static async Task WaitSharedStateAsync(HttpClient friend, Guid profileId,
        string expected, long minimumReceivedBytes = 0)
    {
        ReceivedSharedWorldStatus? latest = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            latest = await GetAsync<ReceivedSharedWorldStatus>(friend,
                $"/api/local/friend/{profileId}/shared-world");
            if (latest.State == expected && latest.ReceivedBytes >= minimumReceivedBytes) return;
            await Task.Delay(100);
        }
        throw new Exception($"Friend transfer did not reach {expected}: {latest?.State} {latest?.Error}");
    }

    private static async Task WaitCopiesAsync(HttpClient owner, Guid profileId, int expected)
    {
        SharedWorldStatus? latest = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            latest = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profileId}/shared-world");
            if (latest.ConfirmedCopies == expected) return;
            await Task.Delay(250);
        }
        throw new Exception($"Host counted {latest?.ConfirmedCopies} of {expected} verified PCs");
    }

    private static async Task WaitForAsync(Func<bool> condition, string error, int timeoutMs)
    {
        var until = Stopwatch.StartNew();
        while (until.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new Exception(error);
    }

    private static Process StartApp(string path, string mode, int port, string data,
        int receiveDelayMs = 0, int beforeChunkDelayMs = 0)
    {
        Directory.CreateDirectory(data);
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(port.ToString());
        info.Environment["TOGETHERSERVER_DATA_DIR"] = data;
        info.Environment["TOGETHERSERVER_FIXTURE_ROOT"] = data;
        info.Environment[GameServerRegistry.FixtureOptInEnvironmentVariable] = "1";
        info.Environment["Logging__LogLevel__Default"] = "Warning";
        if (receiveDelayMs > 0)
            info.Environment["TOGETHERSERVER_FIXTURE_RECEIVE_DELAY_MS"] = receiveDelayMs.ToString();
        if (beforeChunkDelayMs > 0)
            info.Environment["TOGETHERSERVER_FIXTURE_BEFORE_CHUNK_DELAY_MS"] = beforeChunkDelayMs.ToString();
        var process = Process.Start(info) ?? throw new Exception("packaged app did not start");
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                Console.Error.WriteLine($"Disposable {mode} TCP {port}: {eventArgs.Data}");
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                Console.Error.WriteLine($"Disposable {mode} TCP {port}: {eventArgs.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void StopApp(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private static int[] AvailablePorts(int count, params int[] excluded)
    {
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Select(endpoint => endpoint.Port).Concat(excluded).ToHashSet();
        var result = new List<int>();
        for (var attempt = 0; attempt < 1000 && result.Count < count; attempt++)
        {
            var port = Random.Shared.Next(51000, 60000);
            if (active.Add(port)) result.Add(port);
        }
        if (result.Count != count) throw new Exception("no disposable app ports available");
        return [.. result];
    }

    private static int AvailableGamePort()
    {
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
            .Select(endpoint => endpoint.Port).ToHashSet();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var port = Random.Shared.Next(36000, 49000);
            if (!active.Contains(port) && !active.Contains(port + 1)) return port;
        }
        throw new Exception("no disposable Valheim game/query port pair available");
    }

    private static async Task WaitLocalAsync(int port)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromMilliseconds(300)
        };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var response = await client.GetAsync("/api/local/snapshot");
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(100);
        }
        throw new Exception($"disposable app on TCP {port} did not start");
    }

    private static HttpClient LocalClient(int port) => new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<T>(Json) ??
            throw new Exception($"empty local GET {path}: {(int)response.StatusCode}");
    }

    private static async Task<TResponse> PostAsync<TRequest, TResponse>(HttpClient client,
        string path, TRequest body) => await MutateAsync<TRequest, TResponse>(client,
        HttpMethod.Post, path, body);

    private static async Task<TResponse> PutAsync<TRequest, TResponse>(HttpClient client,
        string path, TRequest body) => await MutateAsync<TRequest, TResponse>(client,
        HttpMethod.Put, path, body);

    private static async Task<TResponse> MutateAsync<TRequest, TResponse>(HttpClient client,
        HttpMethod method, string path, TRequest body)
    {
        using var request = new HttpRequestMessage(method, path)
        { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add("Origin", client.BaseAddress!.ToString().TrimEnd('/'));
        request.Headers.Add("X-TogetherServer-Local", "1");
        using var response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<TResponse>(Json) ??
            throw new Exception($"empty local {method} {path}: {(int)response.StatusCode}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
