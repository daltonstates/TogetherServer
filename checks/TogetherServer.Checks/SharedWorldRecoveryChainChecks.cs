using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

internal static class SharedWorldRecoveryChainChecks
{
    internal static async Task RunAsync(string root, string fixture, string rosterKind = "Legacy")
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var owner = new LocalData(Path.Combine(root, "owner"));
        using var first = new LocalData(Path.Combine(root, "first"));
        using var second = new LocalData(Path.Combine(root, "second"));
        using var voter = new LocalData(Path.Combine(root, "voter"));
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var voterKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var devices = new[] { (Data: first, Key: firstKey, Id: Guid.NewGuid()),
            (Data: second, Key: secondKey, Id: Guid.NewGuid()),
            (Data: voter, Key: voterKey, Id: Guid.NewGuid()) };
        var profile = new ServerProfile
        {
            Name = "Recovery chain",
            Kind = GameKinds.Fixture,
            WorldId = "chain",
            WorldDirectory = Path.Combine(owner.RootPath, "world"),
            ExecutablePath = fixture,
            GamePort = 51745,
            SharedSavesEnabled = true,
            Backups = new() { Enabled = true, MinimumFreeSpaceMb = 0 }
        };
        Directory.CreateDirectory(profile.WorldDirectory);
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "owner progress");
        owner.SaveSettings(new HostSettings { Profiles = [profile] });
        var backups = new WorldBackupService(owner, TimeProvider.System);
        var shares = new SharedWorldService(owner, backups);
        var roster = shares.PublishRoster(profile, devices.Select(device =>
            new SharedWorldRosterMember(device.Id,
                Convert.ToBase64String(device.Key.ExportSubjectPublicKeyInfo()),
                new(Receive: true, EligibleHost: true, RecoveryVoter: true,
                    ManageSharing: device.Id == devices[0].Id), false)).ToArray());
        if (rosterKind != "Legacy")
        {
            var originalRoster = roster;
            new SharedWorldRosterChainStore(owner).Append(originalRoster, originalRoster.OwnerPublicKey);
            if (rosterKind == "Owner") roster = shares.PublishRoster(profile, roster.Members, ownerOverride: false);
            else
            {
                var revision = roster with
                {
                    Schema = 3,
                    Epoch = roster.Epoch + 1,
                    Revision = roster.Revision + 1,
                    PreviousRosterHash = SharedWorldRosterTrust.Hash(roster),
                    SignerDeviceId = devices[0].Id,
                    SignerPublicKey = Convert.ToBase64String(firstKey.ExportSubjectPublicKeyInfo()),
                    Members = roster.Members.Select(member => member.DeviceId == devices[2].Id
                        ? member with { Grants = member.Grants with { EligibleHost = false } } : member).ToArray(),
                    Signature = ""
                };
                revision = revision with
                {
                    Signature = Convert.ToBase64String(firstKey.SignData(
                    SharedWorldRosterTrust.Basis(revision), HashAlgorithmName.SHA256))
                };
                roster = shares.CountersignDelegatedRoster(revision, DateTimeOffset.UtcNow);
                Require(SharedWorldRosterTrust.Verify(roster) &&
                    !SharedWorldRosterTrust.Verify(roster with { HostAcceptanceSignature = "invalid" }),
                    "delegated recovery membership did not require the owner's exact countersignature");
                new SharedWorldRosterChainStore(owner).Append(roster, originalRoster.OwnerPublicKey);
            }
            foreach (var device in devices)
            {
                var chain = new SharedWorldRosterChainStore(device.Data);
                chain.Append(originalRoster, originalRoster.OwnerPublicKey);
                chain.Append(roster, originalRoster.OwnerPublicKey);
            }
        }
        var floor = new SharedRosterFloor(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature);
        SharedWorldVersion Publish(SharedWorldService publisher, WorldBackupService backupService,
            ServerProfile source)
        {
            var backup = backupService.Create(source, BackupKinds.Rolling);
            Require(backup.Ok && backup.Backup is not null, "backup failed");
            return publisher.PublishAfterStop(source, backup.Backup!.Id).Version ??
                throw new Exception("publication failed");
        }
        string Vault(int index) => Path.Combine(devices[index].Data.RootPath,
            "received-shared-worlds", devices[index].Id.ToString("N"), profile.Id.ToString("N"));
        void Receive(int index, SharedWorldVersion version, string text)
        {
            var vault = Vault(index);
            var copy = Path.Combine(vault, version.VersionHash);
            Directory.CreateDirectory(Path.Combine(copy, SharedWorldService.PayloadDirectory));
            File.WriteAllText(Path.Combine(copy, SharedWorldService.PayloadDirectory, "world.dat"), text);
            File.WriteAllBytes(Path.Combine(copy, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, json));
            File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, json));
            FriendLink.KeepSignedManifest(vault, version);
        }
        var original = Publish(shares, backups, profile);
        for (var i = 0; i < devices.Length; i++)
        {
            devices[i].Data.SaveProtected($"shared-world-pc-signing-{devices[i].Id:N}.protected",
                devices[i].Key.ExportPkcs8PrivateKey());
            Receive(i, original, "owner progress");
        }
        long stamp = 0;
        var loss = new SharedWorldHostLoss(() => stamp, 1000);
        for (var i = 0; i <= 12; i++)
        {
            stamp = i * 10_000;
            loss.Observe(HostReachabilityObservation.TransportFailure);
        }
        var firstStore = new WorldAuthorityStore(first);
        Require(SharedWorldRosterTrust.VerifySignature(roster) &&
            SharedWorldRosterTrust.HasRole(roster, devices[0].Id,
                Convert.ToBase64String(firstKey.ExportSubjectPublicKeyInfo()), grants => grants.EligibleHost && grants.Receive),
            $"roster signature or candidate grant invalid: schema={roster.Schema}, owner={SharedWorldRosterTrust.Verify(roster)}, candidate={roster.Members.Single(member => member.DeviceId == devices[0].Id).Grants}");
        var firstOffer = SharedWorldElection.PrepareOffer(loss, Vault(0), roster, floor,
            devices[0].Id, firstKey, "https://192.0.2.10:51748", new string('A', 64), firstStore);
        new SharedWorldVoteInbox(first).Arm(firstOffer, Vault(0));
        var firstVotes = new[] { SharedWorldElection.Vote(loss, Vault(0), floor,
                roster.OwnerPublicKey, firstOffer, devices[0].Id, firstKey, firstStore),
            SharedWorldElection.Vote(loss, Vault(1), floor, roster.OwnerPublicKey,
                firstOffer, devices[1].Id, secondKey, new WorldAuthorityStore(second)) };
        var firstRecord = SharedWorldElection.ConfirmQuorum(firstOffer, firstVotes, firstStore, Vault(0));
        firstStore.BindLocalSuccessor(profile.Id, firstRecord.RecordHash, devices[0].Id);
        for (var i = 1; i < devices.Length; i++)
            new WorldAuthorityStore(devices[i].Data).AppendReceived(firstRecord, profile.Id,
                roster.GroupId, roster.OwnerPublicKey);

        var successorProfile = JsonSerializer.Deserialize<ServerProfile>(
            JsonSerializer.SerializeToUtf8Bytes(profile, json), json)!;
        successorProfile.WorldDirectory = Path.Combine(first.RootPath, "world");
        Directory.CreateDirectory(successorProfile.WorldDirectory);
        File.WriteAllText(Path.Combine(successorProfile.WorldDirectory, "world.dat"), "successor progress");
        first.SaveSettings(new HostSettings { Profiles = [successorProfile] });
        var successorBackups = new WorldBackupService(first, TimeProvider.System);
        var successorShares = new SharedWorldService(first, successorBackups);
        successorShares.AdoptSuccessor(successorProfile, firstRecord);
        var intermediate = Publish(successorShares, successorBackups, successorProfile);
        Receive(1, intermediate, "successor progress");
        var progress = Publish(successorShares, successorBackups, successorProfile);
        Require(progress.Number == original.Number + 2 && progress.ParentHash == intermediate.VersionHash &&
            progress.SigningPublicKey == firstOffer.Proposal.CandidatePublicKey, "successor lineage is wrong");
        var unauthorized = SharedWorldService.SignVersion(progress, secondKey);
        Receive(1, unauthorized, "successor progress");
        RequireRejected(() => SharedWorldElection.PrepareOffer(loss, Vault(1), roster, floor,
            devices[1].Id, secondKey, "https://192.0.2.11:51748", new string('B', 64),
            new WorldAuthorityStore(second)), "an eligible device bypassed the current hosting signer");
        using var hostingKey = ECDsa.Create();
        hostingKey.ImportPkcs8PrivateKey(first.LoadProtected(WorldAuthorityStore.HostingKeyName(profile.Id))!, out _);
        var broken = SharedWorldService.SignVersion(progress with
        { Number = progress.Number + 1, ParentHash = new string('0', 64) }, hostingKey);
        Receive(1, broken, "successor progress");
        RequireRejected(() => SharedWorldElection.PrepareOffer(loss, Vault(1), roster, floor,
            devices[1].Id, secondKey, "https://192.0.2.11:51748", new string('B', 64),
            new WorldAuthorityStore(second)), "a signed save with missing ancestry was offered");
        Receive(1, progress, "successor progress");
        // The third voter still has the earlier owner's copy. Its vote must verify
        // the signer transition against the accepted first authority decision.
        var secondStore = new WorldAuthorityStore(second);
        var nextOffer = SharedWorldElection.PrepareOffer(loss, Vault(1), roster, floor,
            devices[1].Id, secondKey, "https://192.0.2.11:51748", new string('B', 64), secondStore);
        var separate = new SharedWorldSeparateCopyStore(second).Declare(loss, nextOffer,
            Vault(1), secondKey, true);
        second.SaveSettings(new HostSettings { CompanionPort = 51748 });
        var separateRestore = await new HostManager(second,
            new GameServerRegistry(second, true, PortProbeMode.ObserveOnly)).RestoreSeparateCopyAsync(profile.Id,
            new(separate.BranchHash, new(fixture, progress.PortableSetup.GameVersion,
                progress.PortableSetup.AddOns, true, 51748, 51745), "Separate", "Separate"));
        Require(separateRestore.Code == "LocalSetupIncomplete",
            "warned separate restore rejected the successor signature or bypassed listener checks: " + separateRestore.Message);
        var nextVotes = new[] { SharedWorldElection.Vote(loss, Vault(1), floor,
                roster.OwnerPublicKey, nextOffer, devices[1].Id, secondKey, secondStore),
            SharedWorldElection.Vote(loss, Vault(2), floor, roster.OwnerPublicKey,
                nextOffer, devices[2].Id, voterKey, new WorldAuthorityStore(voter)) };
        var candidateInbox = new SharedWorldVoteInbox(second);
        candidateInbox.Arm(nextOffer, Vault(1));
        var proposalHash = WorldAuthorityTrust.ProposalHash(nextOffer.Proposal);
        Require(candidateInbox.AcceptVote(profile.Id, proposalHash, nextVotes[0]).Code == "VoteRecorded",
            "candidate vote was not recorded");
        voter.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            Endpoint = firstRecord.Proposal.CandidateAddress,
            HostId = Guid.NewGuid(),
            DeviceId = devices[2].Id,
            Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            Fingerprint = new string('C', 64),
            ConsentedSharedWorldProfiles = [profile.Id],
            SharedWorldSigningKeys = new() { [profile.Id] = roster.OwnerPublicKey },
            ApprovedSharedWorldGroups = new() { [profile.Id] = roster.GroupId },
            SharedRosterFloors = new() { [profile.Id] = floor }
        }, json));
        voter.SaveProtected($"shared-world-roster-{devices[2].Id:N}-{profile.Id:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(roster, json));
        using var voterLink = new FriendLink(voter, "friend.protected", clientFactory: (endpoint, pins) =>
            new HttpClient(new RecoveryHandler(async request =>
            {
                if (endpoint == firstRecord.Proposal.CandidateAddress)
                    throw new HttpRequestException("Synthetic unreachable current Host");
                Require(endpoint == nextOffer.Proposal.CandidateAddress && pins.Contains(nextOffer.CandidateTlsFingerprint),
                    "voter did not use the candidate's reviewed address and pin");
                var path = request.RequestUri!.AbsolutePath;
                object? response = path.EndsWith("/challenge/" + devices[2].Id)
                    ? candidateInbox.Challenge(profile.Id, proposalHash, devices[2].Id)
                    : path.EndsWith("/offer")
                        ? candidateInbox.ReadOffer((await request.Content!.ReadFromJsonAsync<WorldAuthorityOfferRequest>(json))!)
                        : path.EndsWith("/vote")
                            ? candidateInbox.AcceptVote(profile.Id, proposalHash,
                                (await request.Content!.ReadFromJsonAsync<WorldAuthorityVote>(json))!)
                            : throw new Exception("Unexpected recovery request " + path);
                Require(response is not null, "candidate rejected the signed recovery request");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(response, options: json) };
            }))
            { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/") }, hostLoss: loss);
        foreach (var address in new[] { "https://127.0.0.1:51748", "https://0.0.0.0:51748", "https://192.0.2.11:0" })
        {
            voter.SaveSettings(new HostSettings { CompanionEndpoint = address });
            Require((await voterLink.PrepareRecoveryOfferAsync(profile.Id)).Code == "CandidateRouteUnavailable",
                "recovery armed an unusable candidate route");
        }
        var voteResult = await voterLink.VoteOnRecoveryOfferAsync(profile.Id, nextOffer);
        Require(voteResult.Ok && voteResult.Decision is not null, "voter could not confirm the majority: " + voteResult.Message);
        var nextRecord = voteResult.Decision!;
        var voterStore = new WorldAuthorityStore(voter);
        Require(voterStore.ReadUniqueHead(profile.Id)?.RecordHash == nextRecord.RecordHash &&
            FriendLink.ReadReceivedLatest(Vault(2))?.VersionHash == original.VersionHash,
            "the older voter failed to learn the majority or advertised an unreceived payload");
        secondStore.BindLocalSuccessor(profile.Id, nextRecord.RecordHash, devices[1].Id);
        Require(new WorldAuthorityStore(second).ReadUniqueHead(profile.Id)?.RecordHash == nextRecord.RecordHash,
            "second authority did not survive reopening");
        Require(new WorldAuthorityStore(second).Read(profile.Id).Count == 2,
            "second authority lost the original owner trust root");
        var bindingPath = Path.Combine(first.RootPath, $"authority-host-{profile.Id:N}.protected");
        var bindingBytes = File.ReadAllBytes(bindingPath);
        // Exercise metadata reads, authority binding, and inbox callbacks from
        // separate instances concurrently. These previously took opposite locks.
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            for (var iteration = 0; iteration < 6; iteration++)
            {
                if (index % 2 == 0)
                    lock (SharedWorldMutationGate.For(first.RootPath))
                    {
                        new WorldAuthorityStore(first).BindLocalSuccessor(profile.Id,
                            firstRecord.RecordHash, devices[0].Id);
                        new SharedWorldVoteInbox(first).Status(profile.Id, devices[0].Id);
                    }
                else new SharedWorldVoteInbox(first).Status(profile.Id, devices[0].Id);
            }
        }))).WaitAsync(TimeSpan.FromSeconds(20));
        Require(File.ReadAllBytes(bindingPath).SequenceEqual(bindingBytes),
            "idempotent successor binding rewrote protected state");
        var firstKeyName = $"shared-world-pc-signing-{devices[0].Id:N}.protected";
        first.SaveProtected(firstKeyName, secondKey.ExportPkcs8PrivateKey());
        RequireRejected(() => firstStore.BindLocalSuccessor(profile.Id, firstRecord.RecordHash,
            devices[0].Id), "an existing binding bypassed private-key possession");
        first.SaveProtected(firstKeyName, firstKey.ExportPkcs8PrivateKey());

        var manager = new HostManager(second, new GameServerRegistry(second, true, PortProbeMode.ObserveOnly));
        var status = await manager.SuccessorRestoreStatusAsync(profile.Id);
        Require(status.Staged, "second successor cannot restore the exact accepted save: " + status.Message);
        var setup = new TakeoverLocalSetup(fixture, progress.PortableSetup.GameVersion,
            progress.PortableSetup.AddOns, true, 51748, 51745);
        var readiness = SharedWorldReadiness.Check(Vault(1), Path.Combine(second.RootPath, "rehearsal"),
            setup, new(true, false, false), roster.OwnerPublicKey, roster.GroupId,
            _ => long.MaxValue, authorityRecords: secondStore.Read(profile.Id));
        Require(!readiness.Reasons.Any(reason => reason.Contains("signing identity")),
            "readiness rejected authorized successor signature");
        var rehearsal = SharedWorldReadiness.Rehearse(second.RootPath, Vault(1), setup,
            new(true, false, false), roster.OwnerPublicKey, roster.GroupId, _ => long.MaxValue,
            authorityRecords: secondStore.Read(profile.Id));
        Require(rehearsal.RehearsalPassed, "successor rehearsal did not copy verified progress");
        var wrongOwner = SharedWorldReadiness.Check(Vault(1), Path.Combine(second.RootPath, "rehearsal"),
            setup, new(true, false, false), Convert.ToBase64String(voterKey.ExportSubjectPublicKeyInfo()),
            roster.GroupId, _ => long.MaxValue, authorityRecords: secondStore.Read(profile.Id));
        Require(wrongOwner.Reasons.Any(reason => reason.Contains("signing identity")),
            "readiness accepted an unrelated owner pin");

        // Restore and finish setup using signed synthetic route evidence only.
        // No listener or game process is launched by this file-based check.
        second.SaveSettings(new HostSettings
        {
            CompanionEndpoint = nextRecord.Proposal.CandidateAddress,
            CompanionPort = setup.ControlPort,
            CompanionBindAddress = "0.0.0.0",
            CompanionListeningEnabled = false
        });
        var restoreManager = new HostManager(second,
            new GameServerRegistry(second, true, PortProbeMode.ObserveOnly))
        { SuccessorFreeBytesForChecks = _ => long.MaxValue };
        var restored = await restoreManager.RestoreSharedSuccessorAsync(profile.Id,
            new(nextRecord.RecordHash, setup, "Second successor", "Second successor"));
        Require(restored.Ok, "second successor restore failed: " + restored.Code + " " + restored.Message);
        var restoredSettings = second.LoadSettings();
        restoredSettings.CompanionListeningEnabled = true;
        second.SaveSettings(restoredSettings);
        restoreManager = new HostManager(second,
            new GameServerRegistry(second, true, PortProbeMode.ObserveOnly))
        { SuccessorFreeBytesForChecks = _ => long.MaxValue };
        var challenge = SharedWorldRouteTrust.SignChallenge(nextRecord,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            devices[2].Id, voterKey);
        using var certificate = new HostIdentity(second).Ensure(nextRecord.Proposal.CandidateAddress);
        var fingerprint = HostIdentity.Fingerprint(certificate);
        var routeProof = await restoreManager.SignSuccessorRouteProofAsync(profile.Id,
            nextRecord.RecordHash, challenge, fingerprint);
        Require(routeProof is not null && await restoreManager.ConfirmSuccessorRouteAsync(profile.Id,
            nextRecord.RecordHash, new(challenge, routeProof), fingerprint), "synthetic route proof failed");
        var finished = await restoreManager.FinishSharedSuccessorAsync(profile.Id, new(nextRecord.RecordHash, setup));
        Require(finished.Ok, "second successor finish failed: " + finished.Message);
        var restoredProfile = second.LoadSettings().Profiles.Single();
        var currentFile = Path.Combine(restoredProfile.WorldDirectory, "world.dat");
        var growingText = new string('s', 8192);
        File.WriteAllText(currentFile, growingText);
        var nextBackups = new WorldBackupService(second, TimeProvider.System);
        var nextShares = new SharedWorldService(second, nextBackups);
        var current = Publish(nextShares, nextBackups, restoredProfile);
        Require(current.Number == progress.Number + 1 &&
            (await restoreManager.SuccessorRestoreStatusAsync(profile.Id)).ReadyForManualStart,
            "the verified newer signed save cannot restart");
        File.WriteAllText(currentFile, "unpublished alteration");
        Require(!(await restoreManager.SuccessorRestoreStatusAsync(profile.Id)).ReadyForManualStart,
            "unpublished world changes passed successor Start checks");
        File.WriteAllText(currentFile, "successor progress");
        Require(!(await restoreManager.SuccessorRestoreStatusAsync(profile.Id)).ReadyForManualStart,
            "rollback to the earlier handoff copy passed after new progress was signed");
        File.WriteAllText(currentFile, growingText);
        restoreManager.SuccessorFreeBytesForChecks = _ => checked(1024L * 1024 * 1024 +
            2 * SharedWorldService.BoundedTotalBytes(progress.Files));
        Require(!(await restoreManager.SuccessorRestoreStatusAsync(profile.Id)).ReadyForManualStart,
            "storage checks used the original small copy after world growth");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void RequireRejected(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }

    private sealed class RecoveryHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }
}
