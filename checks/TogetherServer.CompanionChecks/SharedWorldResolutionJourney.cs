using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using TogetherServer;
using TogetherServer.CompanionChecks;

internal static partial class SharedWorldJourney
{
    // A second independent group fixes owner override to Off before any authority
    // exists. The three voter identities and both branches come from packaged PCs.
    internal static async Task<int> RunOverrideOffResolutionAsync(string appPath,
        string valheimFixturePath)
    {
        var root = Path.GetFullPath(Path.Combine("local-data", "shared-world-resolution-off",
            Guid.NewGuid().ToString("N")));
        var hostData = Path.Combine(root, "host");
        var aData = Path.Combine(root, "friend-a");
        var bData = Path.Combine(root, "friend-b");
        var cData = Path.Combine(root, "friend-c");
        var ports = AvailablePorts(7);
        var hostPort = ports[0];
        var companionPort = ports[1];
        var aPort = ports[2];
        var bPort = ports[3];
        var cPort = ports[4];
        var aCandidatePort = ports[5];
        var bCandidatePort = ports[6];
        var candidateAddress = AssignedPrivateIpv4ForJourney();
        var aCandidateEndpoint = $"https://{candidateAddress}:{aCandidatePort}";
        var bCandidateEndpoint = $"https://{candidateAddress}:{bCandidatePort}";
        var world = Path.Combine(hostData, "worlds", "owner-override-off");
        Directory.CreateDirectory(world);
        var profile = new ServerProfile
        {
            Kind = GameKinds.Valheim,
            Name = "Disposable owner override off",
            ServerName = "Fixture \"Valheim\"",
            WorldSource = "New",
            WorldId = "fixture-world",
            WorldDirectory = world,
            GamePort = AvailableGamePort(),
            ExecutablePath = valheimFixturePath,
            Backups = new BackupOptions { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        Process? host = null;
        Process? friendA = null;
        Process? friendB = null;
        Process? friendC = null;
        try
        {
            host = StartApp(appPath, "--host", hostPort, hostData);
            friendA = StartApp(appPath, "--friend", aPort, aData);
            friendB = StartApp(appPath, "--friend", bPort, bData);
            friendC = StartApp(appPath, "--friend", cPort, cData);
            await Task.WhenAll(WaitLocalAsync(hostPort), WaitLocalAsync(aPort),
                WaitLocalAsync(bPort), WaitLocalAsync(cPort));
            using var owner = LocalClient(hostPort);
            using var aLocal = LocalClient(aPort);
            using var bLocal = LocalClient(bPort);
            using var cLocal = LocalClient(cPort);
            Require((await PutAsync<HostSettings, ActionResult>(owner, "/api/local/settings",
                new HostSettings
                {
                    MaxConcurrentServers = 1,
                    Profiles = [profile],
                    CompanionEndpoint = $"https://127.0.0.1:{companionPort}",
                    CompanionPort = companionPort,
                    CompanionBindAddress = "127.0.0.1"
                })).Ok, "owner-override-off Host settings failed");
            Require((await PostAsync<ValheimPasswordRequest, ActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/password", new("fixture-pass-123"))).Ok,
                "owner-override-off fixture password failed");
            var invite = await PostAsync<ServerInviteRequest, JsonElement>(owner,
                $"/api/local/servers/{profile.Id}/invite", new(false, true, true, DeviceLimit: 3));
            Require(invite.GetProperty("ok").GetBoolean() &&
                invite.GetProperty("listenerActive").GetBoolean(),
                "owner-override-off Host listener was unavailable");
            var code = invite.GetProperty("password").GetString() ??
                throw new Exception("owner-override-off invite was empty");
            Require((await PostAsync<FriendPairRequest, FriendActionResult>(aLocal,
                "/api/local/friend/pair", new(code))).Ok, "Friend A did not pair");
            var deviceA = await OnlyDeviceAsync(owner, profile.Id);
            Require((await PostAsync<FriendPairRequest, FriendActionResult>(bLocal,
                "/api/local/friend/pair", new(code))).Ok, "Friend B did not pair");
            var deviceB = (await DeviceIdsAsync(owner, profile.Id)).Single(id => id != deviceA);
            Require((await PostAsync<FriendPairRequest, FriendActionResult>(cLocal,
                "/api/local/friend/pair", new(code))).Ok, "voter-only Friend C did not pair");
            var deviceC = (await DeviceIdsAsync(owner, profile.Id)).Single(id =>
                id != deviceA && id != deviceB);
            await Task.WhenAll(PollAsync(aLocal), PollAsync(bLocal));
            Require((await PutAsync<SharedWorldConsentRequest, SharedWorldResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world", new(true))).Ok,
                "owner-override-off sharing was unavailable");
            foreach (var id in new[] { deviceA, deviceB })
                Require((await PutAsync<SharedWorldDeviceGrantsRequest, PairingDecision>(owner,
                    $"/api/local/devices/{id}/shared-world/{profile.Id}/grants",
                    new(new SharedWorldGrants(Receive: true, EligibleHost: true,
                        RecoveryVoter: true)))).Ok,
                    "owner could not appoint two receiving recovery voters");
            Require((await PutAsync<SharedWorldDeviceGrantsRequest, PairingDecision>(owner,
                $"/api/local/devices/{deviceC}/shared-world/{profile.Id}/grants",
                new(new SharedWorldGrants(RecoveryVoter: true)))).Ok,
                "owner could not appoint a voter without Receive");
            foreach (var friend in new[] { aLocal, bLocal })
                Require((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(friend,
                    $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok,
                    "receiving Friend could not consent");
            var firstCReview = await PostAsync<WorldHistoryReviewRequest, WorldHistoryReviewResult>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/history/review", new());
            Require(firstCReview.Code == "GroupReviewRequired" &&
                firstCReview.GroupId is not null && firstCReview.OwnerPublicKey is not null,
                "voter-only Friend did not review owner and group before any save");
            Require((await PostAsync<WorldHistoryReviewRequest, WorldHistoryReviewResult>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/history/review",
                new(firstCReview.GroupId, firstCReview.OwnerPublicKey))).Ok,
                "voter-only Friend could not pin owner-signed membership");
            var governance = await PutAsync<SharedWorldGovernanceRequest, SharedWorldRoster>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/governance", new(false));
            Require(!governance.OwnerOverride && SharedWorldRosterTrust.Verify(governance),
                "owner override Off was not recorded in a signed roster");

            File.WriteAllText(Path.Combine(world, "world.dat"), "override off: first save");
            var firstStop = await StartAndStopAsync(owner, profile.Id);
            var firstStatus = await GetAsync<SharedWorldStatus>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world");
            var first = firstStatus.Latest;
            var firstDiagnostic = first is { Number: 1 } ? "" :
                await DescribeMissingSharedPublicationAsync(owner, profile.Id, firstStop, firstStatus, root);
            Require(first is { Number: 1 }, "owner-override-off first save was not signed" + firstDiagnostic);
            var firstVersion = first!;
            await Task.WhenAll(PollAsync(aLocal), PollAsync(bLocal));
            await Task.WhenAll(WaitVersionAsync(aLocal, profile.Id, 1),
                WaitVersionAsync(bLocal, profile.Id, 1));
            var recoveryVersion = firstVersion;
            var receivedHashes = new List<string> { firstVersion.VersionHash };
            for (var number = 2; number <= 5; number++)
            {
                File.WriteAllText(Path.Combine(world, "world.dat"),
                    $"override off: save {number}");
                await StartAndStopAsync(owner, profile.Id);
                var next = (await GetAsync<SharedWorldStatus>(owner,
                    $"/api/local/profiles/{profile.Id}/shared-world")).Latest;
                Require(next?.Number == number && next!.ParentHash == recoveryVersion.VersionHash,
                    $"owner-override-off save {number} did not extend the signed history");
                recoveryVersion = next!;
                receivedHashes.Add(recoveryVersion.VersionHash);
                await Task.WhenAll(PollAsync(aLocal), PollAsync(bLocal));
                foreach (var friend in new[] { aLocal, bLocal })
                {
                    var pulled = await PostAsync<object, ReceivedSharedWorldResult>(friend,
                        $"/api/local/friend/{profile.Id}/shared-world/pull", new { });
                    Require(pulled.Ok,
                        $"Friend did not pull signed save {number}: {pulled.Code} {pulled.Message}");
                }
                await Task.WhenAll(WaitVersionAsync(aLocal, profile.Id, number),
                    WaitVersionAsync(bLocal, profile.Id, number));
            }
            var bVault = ReceiverRoot(bData, deviceB, profile.Id);
            var retainedPayloads = Directory.EnumerateDirectories(bVault)
                .Select(Path.GetFileName)
                .Where(name => name is { Length: 64 } && name.All(Uri.IsHexDigit))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Require(retainedPayloads.SetEquals(receivedHashes.Skip(2)) &&
                !Directory.Exists(Path.Combine(bVault, firstVersion.VersionHash)) &&
                File.Exists(Path.Combine(bVault, "signed-history",
                    (firstVersion.Number / 1024).ToString("x16"),
                    firstVersion.Number + "-" + firstVersion.VersionHash + ".json")),
                "exactly three recent payloads and the earlier signed manifest were not retained");
            Require((await PostAsync<WorldHistoryReviewRequest, WorldHistoryReviewResult>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/history/review", new())).Ok,
                "voter-only Friend did not verify the final pre-split roster");
            foreach (var (friend, port) in new[] { (aLocal, aCandidatePort),
                (bLocal, bCandidatePort) })
            {
                Require((await PostAsync<object, JsonElement>(friend,
                    "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean(),
                    "candidate PC could not set its direct address");
                var endpoint = $"https://{candidateAddress}:{port}";
                var savedCandidate = await PutAsync<HostSettings, ActionResult>(friend,
                    "/api/local/settings", new HostSettings
                    {
                        CompanionEndpoint = endpoint,
                        CompanionPort = port,
                        CompanionBindAddress = candidateAddress,
                        ConnectionRoute = new() { Mode = ConnectionRouteModes.AdvancedAddress, Address = candidateAddress }
                    });
                Require(savedCandidate.Ok &&
                    savedCandidate.Snapshot.Settings.CompanionEndpoint == endpoint &&
                    savedCandidate.Snapshot.Settings.CompanionBindAddress == candidateAddress,
                    $"candidate assigned private address was not saved: {savedCandidate.Code} {savedCandidate.Message}");
                Require((await PostAsync<object, JsonElement>(friend,
                    "/api/local/mode/friend", new { })).GetProperty("ok").GetBoolean(),
                    "candidate did not return to Friend mode");
            }
            StopApp(friendC);
            friendC = null;
            StopApp(host);
            host = null;
            await Task.WhenAll(PollAsync(aLocal), PollAsync(bLocal));
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < SharedWorldHostLoss.RequiredDelay + TimeSpan.FromSeconds(2))
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                var states = await Task.WhenAll(PollAsync(aLocal), PollAsync(bLocal));
                Require(states.All(view => view.State == "Disconnected/Unknown"),
                    "owner-override-off Friends did not confirm Host loss");
            }
            var offerResult = await PostAsync<object, WorldAuthorityOfferResult>(bLocal,
                $"/api/local/friend/{profile.Id}/shared-world/recovery/offer", new { });
            Require(offerResult is { Ok: true, Offer: { } } &&
                SharedWorldElection.VerifyOffer(offerResult.Offer) &&
                offerResult.Offer!.Version.VersionHash == recoveryVersion.VersionHash &&
                offerResult.Offer.Proposal.CandidateAddress == bCandidateEndpoint &&
                !offerResult.Offer.Roster.OwnerOverride,
                $"override-off signed recovery offer failed: {offerResult.Code} {offerResult.Message}");
            WindowsListenerOwners.RequireTogetherServerOwner(bCandidatePort, friendB);
            var firstVote = await PostAsync<WorldAuthorityOffer, WorldAuthorityVoteAction>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/recovery/vote", offerResult.Offer!);
            var secondVote = await PostAsync<WorldAuthorityOffer, WorldAuthorityVoteAction>(bLocal,
                $"/api/local/friend/{profile.Id}/shared-world/recovery/vote", offerResult.Offer!);
            Require(firstVote is { Ok: true, Votes: 1, Required: 2 } &&
                secondVote is { Ok: true, Votes: 2, Required: 2, Decision: { } } &&
                WorldAuthorityTrust.Verify(secondVote.Decision),
                "owner-override-off split did not get two independent signed votes");
            var quorum = secondVote.Decision!;
            StopApp(friendA);
            friendA = null;
            StopApp(friendB);
            friendB = null;

            host = StartApp(appPath, "--host", hostPort, hostData);
            await WaitLocalAsync(hostPort);
            File.WriteAllText(Path.Combine(world, "world.dat"), "override off: second save");
            await StartReadyAsync(owner, profile.Id);
            var prepared = await PostAsync<PreparePlannedHandoffRequest, PlannedHandoffResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/handoff/prepare",
                new(deviceA, aCandidateEndpoint));
            Require(prepared is { Ok: true, Version: { Number: 6 } } &&
                prepared.Version!.ParentHash == recoveryVersion.VersionHash,
                $"override-off independent final save failed: {prepared.Code} {prepared.Message}");
            friendA = StartApp(appPath, "--friend", aPort, aData);
            await WaitLocalAsync(aPort);
            await PollAsync(aLocal);
            await WaitVersionAsync(aLocal, profile.Id, 6);
            await WaitCopiesAsync(owner, profile.Id, 1);
            var completed = await PostAsync<object, PlannedHandoffResult>(owner,
                $"/api/local/profiles/{profile.Id}/shared-world/handoff/complete", new { });
            Require(completed is { Ok: true, Authority: { } } &&
                WorldAuthorityTrust.Verify(completed.Authority),
                $"override-off planned authority failed: {completed.Code} {completed.Message}");
            var planned = completed.Authority!;
            Require((await PostAsync<object, PlannedHandoffStageResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/handoff/stage", new { })).Ok,
                "override-off successor did not stage the signed branch");
            friendB = StartApp(appPath, "--friend", bPort, bData);
            await WaitLocalAsync(bPort);
            var authorityPath = Path.Combine(hostData, "shared-worlds", profile.Id.ToString("N"),
                "authority", "records.jsonl");
            for (var attempt = 0; attempt < 30 && !RecordedDecision(authorityPath, quorum); attempt++)
            {
                await PollAsync(bLocal);
                if (!RecordedDecision(authorityPath, quorum)) await Task.Delay(250);
            }
            Require(RecordedDecision(authorityPath, quorum) &&
                RecordedDecision(authorityPath, planned),
                "override-off old Host did not retain both signed branches");
            friendC = StartApp(appPath, "--friend", cPort, cData);
            await WaitLocalAsync(cPort);
            var aReview = await PostAsync<WorldHistoryReviewRequest, WorldHistoryReviewResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/history/review", new());
            var cReview = await PostAsync<WorldHistoryReviewRequest, WorldHistoryReviewResult>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/history/review", new());
            Require(aReview is { Ok: true, CompetingHeads: 2 } &&
                cReview is { Ok: true, CompetingHeads: 2 },
                $"override-off voters could not verify exact signed split: A={aReview.Code}, C={cReview.Code}");
            Require((await PutAsync<SharedWorldConsentRequest, ReceivedSharedWorldResult>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/consent", new(true))).Ok,
                "voter-only Friend could not consent to resolution vote");
            var forbiddenOwner = await PostAsync<object, WorldAuthorityOfferResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/resolution/owner-offer/{planned.RecordHash}",
                new { });
            Require(!forbiddenOwner.Ok && forbiddenOwner.Code == "OwnerOverrideDisabled",
                "the signed Off setting did not block typed owner override");
            var resolution = await PostAsync<object, WorldAuthorityOfferResult>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/resolution/offer/{planned.RecordHash}",
                new { });
            Require(resolution is { Ok: true, Offer: { } } &&
                resolution.Offer.Proposal.Kind == "ResolutionQuorum" &&
                !resolution.Offer.Roster.OwnerOverride &&
                resolution.Offer.Proposal.CompetingHeadHashes?.ToHashSet()
                    .SetEquals([quorum.RecordHash, planned.RecordHash]) == true,
                $"override-off quorum resolution offer failed: {resolution.Code} {resolution.Message}");
            var proposalHash = WorldAuthorityTrust.ProposalHash(resolution.Offer!.Proposal);
            foreach (var voter in new[] { aLocal, cLocal })
                Require((await PostAsync<WorldAuthorityOffer, WorldResolutionInvitationResult>(voter,
                    $"/api/local/friend/{profile.Id}/shared-world/resolution/invitations",
                    resolution.Offer)).Ok,
                    "a voter accepted an unreviewed or changed signed split");
            var aVote = await PostAsync<object, WorldAuthorityVoteAction>(aLocal,
                $"/api/local/friend/{profile.Id}/shared-world/resolution/vote/{proposalHash}", new { });
            var cVote = await PostAsync<object, WorldAuthorityVoteAction>(cLocal,
                $"/api/local/friend/{profile.Id}/shared-world/resolution/vote/{proposalHash}", new { });
            Require(aVote is { Ok: true, Votes: 1, Required: 2 } &&
                cVote is { Ok: true, Votes: 2, Required: 2, Decision: { } } &&
                WorldAuthorityTrust.Verify(cVote.Decision) &&
                cVote.Decision.Proposal.Kind == "ResolutionQuorum" &&
                WorldAuthorityTrust.ProposalHash(cVote.Decision.Proposal) == proposalHash &&
                cVote.Decision.OwnerSignature is null &&
                cVote.Decision.Votes.Select(vote => vote.VoterDeviceId).ToHashSet()
                    .SetEquals([deviceA, deviceC]),
                $"override-off voters did not resolve the reviewed split: A={aVote.Code}, C={cVote.Code}");
            var resolved = cVote.Decision!;
            for (var attempt = 0; attempt < 30 && !RecordedDecision(authorityPath, resolved); attempt++)
            {
                await PollAsync(cLocal);
                if (!RecordedDecision(authorityPath, resolved)) await Task.Delay(250);
            }
            var blockedStart = await PostAsync<object, ActionResult>(owner,
                $"/api/local/profiles/{profile.Id}/start", new { });
            Require(RecordedDecision(authorityPath, resolved) &&
                ReadRecordedAuthorities(authorityPath).Count >= 3 &&
                !blockedStart.Ok && blockedStart.Code == "SharedWorldAuthorityBlocked" &&
                File.ReadAllText(Path.Combine(world, "world.dat")) == "override off: second save" &&
                File.ReadAllText(Path.Combine(ReceiverRoot(aData, deviceA, profile.Id),
                    prepared.Version!.VersionHash, "payload", "world.dat")) ==
                    "override off: second save" &&
                File.Exists(Path.Combine(bVault, recoveryVersion.VersionHash,
                    "payload", "world.dat")) &&
                File.Exists(Path.Combine(bVault, "signed-history",
                    (firstVersion.Number / 1024).ToString("x16"),
                    firstVersion.Number + "-" + firstVersion.VersionHash + ".json")),
                "override-off resolution lost a signed branch, old-Host fence, or verified copy");
            Require((await PostAsync<object, JsonElement>(aLocal,
                "/api/local/mode/host", new { })).GetProperty("ok").GetBoolean() &&
                !(await GetAsync<HostSnapshot>(aLocal, "/api/local/snapshot"))
                    .Runs.Any(run => run.State != "Offline"),
                "resolution unexpectedly started the candidate's game process");
            Console.WriteLine("PASS owner override Off blocks unilateral approval; voter-only PC joins a typed majority resolution");
            return 1;
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
                { Console.Error.WriteLine("Disposable resolution cleanup: " + cleanupError.Message); }
            }
            StopApp(friendC);
            StopApp(friendB);
            StopApp(friendA);
            StopApp(host);
        }
    }
    private static async Task<string> DescribeMissingSharedPublicationAsync(HttpClient owner,
        Guid profileId, ActionResult stop, SharedWorldStatus sharing, string disposableRoot)
    {
        var backupFacts = "backups=unavailable";
        try
        {
            var list = await GetAsync<WorldBackupList>(owner,
                $"/api/local/profiles/{profileId}/backups");
            var latest = list.Backups.OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
            backupFacts = $"completedCount={list.Status.CompletedCount}; " +
                $"latestBackupSetupIncluded={latest?.SetupIncluded.ToString() ?? "none"}; " +
                $"backupError={BoundedPublicationDiagnostic(list.Status.LastFailure)}";
        }
        catch (Exception error)
        {
            // Diagnostic reads must never replace the original assertion or emit raw paths.
            backupFacts = "backupsRead=" + error.GetType().Name;
        }
        var freeBytes = "unavailable";
        try
        {
            freeBytes = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(disposableRoot))!)
                .AvailableFreeSpace.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception error) { freeBytes = error.GetType().Name; }
        return $"; stopCode={BoundedPublicationDiagnostic(stop.Code)}; " +
            $"stopMessage={BoundedPublicationDiagnostic(stop.Message)}; " +
            $"sharingEnabled={sharing.Enabled}; canManageSharing={sharing.CanManageSharing}; " +
            $"latestNumber={sharing.Latest?.Number.ToString() ?? "none"}; " +
            $"sharingError={BoundedPublicationDiagnostic(sharing.Error)}; " +
            $"{backupFacts}; disposableDriveFreeBytes={freeBytes}";
    }

    private static string BoundedPublicationDiagnostic(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var text = new string(value.Take(4096).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        text = text.Replace("fixture-pass-123", "[synthetic password]", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)(?:https?://|[a-z]:[\\/]|\\\\)[^;]*", "[location]");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "[id]");
        text = System.Text.RegularExpressions.Regex.Replace(text,
            @"[A-Za-z0-9+/=_-]{40,}", "[opaque value]");
        return text.Length <= 240 ? text : text[..240] + "...";
    }
}
