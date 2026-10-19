using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// All game adapters refuse launches, observations and Stop. Route transport is
// injected HTTP and all bytes, credentials and keys are disposable synthetic data.
internal static class PlannedHandoffRequestChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        Inputs();
        await ConsentAndStaleRequestsAsync(root);
        await FirstMoveWithoutPublishedSaveAsync(root);
        await AccessChangesAsync(root);
    }

    private static void Inputs()
    {
        var id = Guid.NewGuid();
        Require(PlannedHandoffInputs.Selection(Encoding.UTF8.GetBytes($"{{\"successorDeviceId\":\"{id}\"}}")) == id,
            "a paired-PC selection did not decode");
        foreach (var input in new[] { "{}", "[]", "null", $"{{\"successorDeviceId\":\"{id}\",\"successorAddress\":\"https://192.0.2.8:5131\"}}",
            $"{{\"successorDeviceId\":\"{id}\",\"successorDeviceId\":\"{id}\"}}" })
            Reject(() => PlannedHandoffInputs.Selection(Encoding.UTF8.GetBytes(input)), "owner supplied an address, ambiguous selection or empty request");
        foreach (var input in new[] { $"{{\"requestId\":\"{id}\"}}", $"{{\"requestId\":\"{id}\",\"accept\":\"true\"}}",
            $"{{\"requestId\":\"{id}\",\"accept\":true,\"address\":\"unused\"}}" })
            Reject(() => PlannedHandoffInputs.Response(Encoding.UTF8.GetBytes(input)), "recipient confirmation was not an explicit fixed choice");
        var valid = PlannedHandoffInputs.Response(Encoding.UTF8.GetBytes($"{{\"requestId\":\"{id}\",\"accept\":false}}"));
        Require(valid.RequestId == id && !valid.Accept, "an explicit decline did not decode");
    }

    private static async Task ConsentAndStaleRequestsAsync(string root)
    {
        using var fixture = new Fixture(root);
        var initial = File.ReadAllBytes(fixture.SavePath);
        var requested = await fixture.Manager.RequestPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId);
        var state = await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id);
        Require(requested.Ok && state.RequestId is not null && state.RecipientState == "Requested" &&
            !state.Pending && state.Source?.Game == GameKinds.Fixture && fixture.Shared.Status(fixture.Profile).Latest is null &&
            File.ReadAllBytes(fixture.SavePath).SequenceEqual(initial) && fixture.Driver.ProcessAttempts == 0,
            "requesting a PC without an incoming endpoint stopped a game, copied a live world or required a published save");
        var requestId = state.RequestId!.Value;
        Require(!(await fixture.Manager.RespondPlannedHandoffAsync(fixture.Profile.Id, Guid.NewGuid(), new(requestId, true))).Ok &&
            !(await fixture.Manager.RespondPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId, new(Guid.NewGuid(), true))).Ok,
            "a different recipient or replayed request accepted hosting");
        Require((await fixture.Manager.RespondPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId, new(requestId, false))).Ok &&
            (await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id)).RecipientState == "Declined" &&
            !(await fixture.Manager.ContinuePlannedHandoffAsync(fixture.Profile.Id, requestId)).Ok &&
            fixture.Shared.Status(fixture.Profile).Latest is null && fixture.Driver.ProcessAttempts == 0,
            "declining hosting created a final copy or attempted a lifecycle action");
        Require((await fixture.Manager.RequestPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId)).Ok,
            "a declined request could not be replaced");
        var replacement = (await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id)).RequestId!.Value;
        Require(replacement != requestId && !(await fixture.Manager.CancelPlannedHandoffAsync(fixture.Profile.Id, requestId)).Ok &&
            !(await fixture.Manager.ContinuePlannedHandoffAsync(fixture.Profile.Id, requestId)).Ok &&
            !(await fixture.Manager.RespondPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId, new(requestId, true))).Ok &&
            (await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id)).RequestId == replacement,
            "a stale dialog changed a replacement request");
        Require((await fixture.Manager.CancelPlannedHandoffAsync(fixture.Profile.Id, replacement)).Ok &&
            (await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id)).RecipientState == "Canceled" &&
            File.ReadAllBytes(fixture.SavePath).SequenceEqual(initial) && fixture.Driver.ProcessAttempts == 0,
            "canceling an unsigned request changed the source world or invoked a process");
    }

    private static async Task FirstMoveWithoutPublishedSaveAsync(string root)
    {
        using var fixture = new Fixture(root);
        using var recipientData = new LocalData(Path.Combine(root, "planned-recipient-" + Guid.NewGuid().ToString("N")));
        var controlPort = FreePort();
        var endpoint = $"https://192.0.2.44:{controlPort}";
        recipientData.SaveSettings(new HostSettings
        {
            CompanionEndpoint = endpoint,
            CompanionPort = controlPort,
            CompanionBindAddress = "0.0.0.0",
            CompanionListeningEnabled = true
        });
        recipientData.SaveProtected($"shared-world-pc-signing-{fixture.DeviceId:N}.protected", fixture.MemberKey.ExportPkcs8PrivateKey());
        using var certificate = new HostIdentity(recipientData).Ensure(endpoint);
        var fingerprint = HostIdentity.Fingerprint(certificate);
        var server = Path.Combine(recipientData.RootPath, "never-run.synthetic");
        File.WriteAllText(server, "synthetic installed artifact; never executable");
        var recipientDriver = new FilesOnlyDriver();
        var recipient = new HostManager(recipientData, new GameServerRegistry([recipientDriver], PortProbeMode.ObserveOnly));

        Require((await fixture.Manager.RequestPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId)).Ok, "first move request failed");
        var invitation = await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id);
        var requestId = invitation.RequestId!.Value;
        Require(invitation.Source is not null && fixture.Shared.Status(fixture.Profile).Latest is null, "first move depended on an old published copy");
        Require((await fixture.Manager.RespondPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId, new(requestId, true))).Ok,
            "recipient could not explicitly accept");
        Require(!(await fixture.Manager.ContinuePlannedHandoffAsync(fixture.Profile.Id, requestId)).Ok && fixture.Driver.ProcessAttempts == 0,
            "owner continued before recipient game preparation");
        var gamePort = FreePort(controlPort + 1);
        var setup = new TakeoverLocalSetup(server, invitation.Source!.PortableSetup.GameVersion, [], true, controlPort, gamePort);
        var prepared = new PlannedLocalHostingState(1, requestId, fixture.Profile.Id, invitation.Source.SetupIdentity,
            setup, server, null, null, null);
        var setupOnly = new SharedWorldVersion(4, fixture.Roster.GroupId, 0, null, fixture.Profile.Id,
            GameKinds.Fixture, fixture.Profile.WorldId, DateTimeOffset.UtcNow, "SetupMetadataOnly", Guid.Empty,
            invitation.Source.PortableSetup, [], "", "", "");
        Require(!SharedWorldService.VerifySignature(setupOnly) &&
            await recipient.PlannedLocalGameIssueAsync(setupOnly, prepared, default) is null &&
            recipientData.LoadSettings().Profiles.Count == 0 && recipientData.LoadRuns().Count == 0 && recipientDriver.ProcessAttempts == 0,
            "initial game setup invented a verified save or launched a process");
        var contact = fixture.Contact(endpoint, fingerprint);
        var duplicateRoute = fixture.Sign(contact with { Routes = [contact.Routes[0], contact.Routes[0]] });
        Require(!(await fixture.Manager.ConfirmHandoffPreparationAsync(fixture.Profile.Id, fixture.DeviceId,
            new(requestId, null, duplicateRoute, invitation.Source.SetupIdentity))).Ok &&
            fixture.Shared.Status(fixture.Profile).Latest is null && fixture.Driver.ProcessAttempts == 0,
            "duplicate signed addresses reached final-copy preparation");
        Require((await fixture.Manager.ConfirmHandoffPreparationAsync(fixture.Profile.Id, fixture.DeviceId,
            new(requestId, null, contact, invitation.Source.SetupIdentity))).Ok, "metadata-only initial preparation was not accepted");
        Require((await fixture.Manager.RespondPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId, new(requestId, true))).Ok &&
            (await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id)).RecipientState == "Prepared",
            "a repeated Accept demoted completed preparation");
        var final = await fixture.Manager.ContinuePlannedHandoffAsync(fixture.Profile.Id, requestId);
        var waiting = await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id);
        Require(final.Ok && final.Version is not null && waiting.Pending && waiting.RecipientState == "Accepted" &&
            !waiting.CanComplete && !new WorldAuthorityStore(fixture.Data).HasState(fixture.Profile.Id) && fixture.Driver.ProcessAttempts == 0,
            $"first Offline move did not publish a fresh checked copy or fenced before exact receiver preparation: {final.Code}: {final.Message}; " +
            $"pending={waiting.Pending}; recipient={waiting.RecipientState}; canComplete={waiting.CanComplete}");
        var version = final.Version!;
        Require(!(await fixture.Manager.ConfirmHandoffPreparationAsync(fixture.Profile.Id, fixture.DeviceId,
            new(requestId, version.VersionHash, contact, invitation.Source.SetupIdentity))).Ok,
            "a preparation acknowledgement replaced the required signed file receipt");

        var vault = Path.Combine(recipientData.RootPath, "received-shared-worlds", fixture.DeviceId.ToString("N"), fixture.Profile.Id.ToString("N"));
        var copiedRoot = Path.Combine(vault, version.VersionHash);
        Directory.CreateDirectory(Path.Combine(copiedRoot, SharedWorldService.PayloadDirectory));
        for (var index = 0; index < version.Files.Count; index++)
        {
            var file = version.Files[index];
            var path = SharedWorldService.SafeChild(Path.Combine(copiedRoot, SharedWorldService.PayloadDirectory), file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                for (long offset = 0; offset < file.Length; offset += SharedWorldService.ChunkBytes)
                    output.Write(fixture.Shared.ReadChunk(version, index, offset)!);
            }
            SharedWorldService.VerifyFile(path, file);
        }
        File.WriteAllBytes(Path.Combine(copiedRoot, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        FriendLink.KeepSignedManifest(vault, version);
        var receipt = new SharedWorldReceipt(1, version.GroupId, version.ProfileId, version.VersionHash,
            fixture.DeviceId, fixture.Roster.Epoch, fixture.Roster.Revision, Guid.NewGuid(), "");
        receipt = receipt with { Signature = Convert.ToBase64String(fixture.MemberKey.SignData(SharedWorldReceiptTrust.Basis(receipt), HashAlgorithmName.SHA256)) };
        Require((await fixture.Manager.ConfirmSharedWorldReceiptAsync(fixture.Profile.Id, fixture.DeviceId, receipt)).Ok,
            "the copied final save's signed receipt was rejected");
        Require(!(await fixture.Manager.ContinuePlannedHandoffAsync(fixture.Profile.Id, requestId)).Ok,
            "receipt-only continuation skipped exact final game preparation");
        Require((await fixture.Manager.ConfirmHandoffPreparationAsync(fixture.Profile.Id, fixture.DeviceId,
            new(requestId, version.VersionHash, contact, invitation.Source.SetupIdentity))).Ok,
            "exact final preparation failed");
        var completed = await fixture.Manager.ContinuePlannedHandoffAsync(fixture.Profile.Id, requestId);
        Require(completed.Ok && completed.Authority is not null && WorldAuthorityTrust.Verify(completed.Authority) &&
            new WorldAuthorityStore(fixture.Data).Fenced(fixture.Profile.Id, fixture.Roster.OwnerPublicKey, out _) &&
            (await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id)).RecipientState == "Completed",
            "the signed move did not fence the old Host before reporting completion");
        var record = completed.Authority!;
        var staged = await PlannedHandoffReceiver.StageAsync(recipientData, vault, record, fixture.Profile.Id,
            fixture.Roster.GroupId, fixture.Roster.OwnerPublicKey, fixture.DeviceId, fixture.MemberKey,
            () => Task.FromResult(true), default);
        Require(staged.Ok, "the exact final signed copy could not be staged: " + staged.Message);
        var restored = await recipient.RestoreSharedSuccessorAsync(fixture.Profile.Id,
            new(record.RecordHash, setup, "Synthetic moved server", "Synthetic moved server"));
        Require(restored.Ok && recipientData.LoadRuns().Count == 0, "the final copy could not restore without starting: " + restored.Message);
        fixture.Manager.HandoffRouteClientForChecks = (address, pins) =>
        {
            Require(address == endpoint && pins.SequenceEqual([fingerprint]), "owner route check discarded its authenticated stored pin");
            return new HttpClient(new RouteHandler(recipient, record, fingerprint)) { BaseAddress = new Uri(endpoint) };
        };
        var observed = await fixture.Manager.CheckPlannedSuccessorRouteAsync(fixture.Profile.Id, fixture.DeviceId, requestId, default);
        Require(observed.Ok, "two-PC owner route observation failed: " + observed.Message);
        var finished = await recipient.FinishSharedSuccessorAsync(fixture.Profile.Id, new(record.RecordHash, setup));
        var status = await recipient.SuccessorRestoreStatusAsync(fixture.Profile.Id);
        Require(finished.Ok && status.ReadyForManualStart && recipientData.LoadRuns().Count == 0 && fixture.Data.LoadRuns().Count == 0 &&
            fixture.Driver.ProcessAttempts == 0 && recipientDriver.ProcessAttempts == 0,
            "final setup did not preserve the separate explicit manual Start gate: " + finished.Message);
        var destination = recipientData.LoadSettings().Profiles.Single().WorldDirectory;
        Require(File.ReadAllBytes(Path.Combine(destination, "world.dat")).SequenceEqual(File.ReadAllBytes(fixture.SavePath)),
            "the final moved bytes differ from the untouched synthetic source");
        recipientData.SaveProtected($"planned-local-hosting-{fixture.Profile.Id:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(prepared with { Prepared = true }));
        recipientData.SaveProtected($"shared-world-roster-{fixture.DeviceId:N}-{fixture.Profile.Id:N}.protected",
            JsonSerializer.SerializeToUtf8Bytes(fixture.Roster, Json));
        recipientData.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
        {
            DeviceId = fixture.DeviceId,
            DisplayName = "Offline old Host",
            Endpoint = "https://192.0.2.20:5131",
            Fingerprint = new string('A', 64),
            Credential = new string('C', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
            ConsentedSharedWorldProfiles = [fixture.Profile.Id],
            CachedProfiles = [new(fixture.Profile.Id, "Synthetic moved server", "Offline", null, Kind: GameKinds.Fixture)],
            ApprovedSharedWorldGroups = new() { [fixture.Profile.Id] = fixture.Roster.GroupId },
            SharedWorldSigningKeys = new() { [fixture.Profile.Id] = fixture.Roster.OwnerPublicKey },
            SharedRosterFloors = new() { [fixture.Profile.Id] = new(fixture.Roster.GroupId, fixture.Roster.Epoch, fixture.Roster.Revision, fixture.Roster.Signature) },
            LastSharedHostManifests = new() { [fixture.Profile.Id] = version }
        }, Json));
        using var offlineFriend = new FriendService(recipientData, (_, _) => throw new InvalidOperationException("Offline projection must not access a network."));
        var service = new FriendLocalHostingService(recipientData, offlineFriend, recipient, _ => [server]);
        var beforeOffline = File.ReadAllBytes(Path.Combine(destination, "world.dat"));
        var offline = await service.StatusAsync(fixture.Profile.Id);
        Require(offline.HandoffRequest?.RecipientState == "Completed" && offline.HandoffRequest.RequestId == requestId &&
            offline.LocalProfileId == fixture.Profile.Id && offline.ReadyForManualStart && offline.BranchHash is null &&
            new SharedWorldSeparateCopyStore(recipientData).Read(fixture.Profile.Id).Count == 0 &&
            recipientData.LoadRuns().Count == 0 && recipientDriver.ProcessAttempts == 0 &&
            File.ReadAllBytes(Path.Combine(destination, "world.dat")).SequenceEqual(beforeOffline),
            "the old Host going offline hid the completed local move, created a recovery branch or changed a world");
        var identity = new HostIdentity(recipientData);
        identity.StageNext(endpoint, force: true);
        identity.ActivateNext();
        var changedCertificate = await service.StatusAsync(fixture.Profile.Id);
        Require(changedCertificate.LocalProfileId == fixture.Profile.Id && !changedCertificate.ReadyForManualStart &&
            changedCertificate.HandoffRequest?.RecipientState == "Completed" && recipientDriver.ProcessAttempts == 0 &&
            File.ReadAllBytes(Path.Combine(destination, "world.dat")).SequenceEqual(beforeOffline),
            "a changed current certificate retained the old signed route's readiness or changed the moved world");
    }

    private static async Task AccessChangesAsync(string root)
    {
        foreach (var change in new[] { "revoke", "expiry", "assignment", "receive", "hosting" })
        {
            using var fixture = new Fixture(root);
            Require((await fixture.Manager.RequestPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId)).Ok, "access fixture request failed");
            var requestId = (await fixture.Manager.PlannedHandoffStatusAsync(fixture.Profile.Id)).RequestId!.Value;
            Require((await fixture.Manager.RespondPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId, new(requestId, true))).Ok, "access fixture consent failed");
            if (change == "expiry") fixture.Clock.Now = fixture.Clock.Now.AddHours(2);
            else
            {
                var state = fixture.Data.LoadPairingState();
                var device = state.Devices.Single();
                if (change == "revoke") device.Revoked = true;
                if (change == "assignment") device.AssignedProfileIds = [];
                if (change == "receive") device.SharedWorldGrants[fixture.Profile.Id] = new(false, true);
                if (change == "hosting") device.SharedWorldGrants[fixture.Profile.Id] = new(true, false);
                fixture.Data.SavePairingState(state);
                fixture.ReopenManager();
            }
            var denied = await fixture.Manager.RespondPlannedHandoffAsync(fixture.Profile.Id, fixture.DeviceId, new(requestId, true));
            Require(!denied.Ok && fixture.Shared.Status(fixture.Profile).Latest is null && fixture.Driver.ProcessAttempts == 0 &&
                !(await fixture.Manager.ContinuePlannedHandoffAsync(fixture.Profile.Id, requestId)).Ok,
                "changed " + change + " access authorized a move or lifecycle work");
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal LocalData Data { get; }
        internal ServerProfile Profile { get; }
        internal Guid DeviceId { get; }
        internal ECDsa MemberKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal TestClock Clock { get; } = new();
        internal FilesOnlyDriver Driver { get; } = new();
        internal HostManager Manager { get; private set; }
        internal SharedWorldService Shared { get; }
        internal SharedWorldRoster Roster { get; }
        internal string SavePath => Path.Combine(Profile.WorldDirectory, "world.dat");
        internal Fixture(string root)
        {
            Data = new(Path.Combine(root, "planned-source-" + Guid.NewGuid().ToString("N")));
            Profile = new()
            {
                Kind = GameKinds.Fixture,
                Name = "Synthetic move",
                ServerName = "Synthetic move",
                WorldId = "SyntheticMove",
                GamePort = 2456,
                SharedSavesEnabled = true,
                Backups = new() { Enabled = true },
                ExecutablePath = Path.Combine(Data.RootPath, "never-run.synthetic")
            };
            Profile.WorldDirectory = Data.NewWorldDirectory(Profile.Id);
            Directory.CreateDirectory(Profile.WorldDirectory);
            File.WriteAllText(SavePath, "synthetic final-world bytes");
            File.WriteAllText(Profile.ExecutablePath, "never executable");
            Data.SaveSettings(new HostSettings { Profiles = [Profile] });
            var pairing = new PairingService(Data, Clock);
            var code = pairing.IssueServer(Profile.Id, false, false, "https://192.0.2.20:5131", new string('A', 64), refresh: false);
            var credential = pairing.Activate(new(Profile.Id, code.Code, ServerScope: true)) ?? throw new InvalidOperationException("synthetic pairing failed");
            DeviceId = credential.DeviceId;
            var state = Data.LoadPairingState();
            var device = state.Devices.Single();
            device.SharedWorldPublicKey = Convert.ToBase64String(MemberKey.ExportSubjectPublicKeyInfo());
            device.SharedWorldGrants[Profile.Id] = new(true, true);
            device.SaveReceiveProfileIds = [Profile.Id];
            device.AccessExpiresUtc = Clock.Now.AddHours(1);
            Data.SavePairingState(state);
            pairing = new(Data, Clock);
            Shared = new(Data, new WorldBackupService(Data, Clock, games: new GameServerRegistry([Driver], PortProbeMode.ObserveOnly)));
            Roster = Shared.PublishRoster(Profile, pairing.SharedRosterMembers(Profile.Id));
            pairing.ConfirmSharedRosterPublished(Profile.Id, Roster);
            Manager = new(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly), Clock, new NullHostingPowerGuard(), pairing: pairing);
        }
        internal void ReopenManager() => Manager = new(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly), Clock,
            new NullHostingPowerGuard(), pairing: new PairingService(Data, Clock));
        internal WorldPeerContactCard Contact(string endpoint, string fingerprint) => Sign(new(1,
            new(Roster.OwnerPublicKey, Profile.Id, Roster.GroupId), DeviceId, Convert.ToBase64String(MemberKey.ExportSubjectPublicKeyInfo()),
            1, [new(endpoint, fingerprint)], ""));
        internal WorldPeerContactCard Sign(WorldPeerContactCard value) => value with
        { Signature = Convert.ToBase64String(MemberKey.SignData(WorldPeerService.ContactBasis(value), HashAlgorithmName.SHA256)) };
        public void Dispose() { MemberKey.Dispose(); Data.Dispose(); }
    }

    private sealed class RouteHandler(HostManager recipient, WorldAuthorityRecord record, string fingerprint) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("route-proof", StringComparison.Ordinal))
            {
                var challenge = await request.Content!.ReadFromJsonAsync<SharedWorldRouteChallenge>(Json, cancellationToken);
                var proof = await recipient.SignSuccessorRouteProofAsync(record.Proposal.ProfileId, record.RecordHash, challenge!, fingerprint);
                return new(proof is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = JsonContent.Create(proof, options: Json) };
            }
            var confirmation = await request.Content!.ReadFromJsonAsync<SharedWorldRouteConfirmation>(Json, cancellationToken);
            return new(await recipient.ConfirmSuccessorRouteAsync(record.Proposal.ProfileId, record.RecordHash, confirmation!, fingerprint)
                ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        }
    }

    internal sealed class FilesOnlyDriver : IGameServerDriver
    {
        internal int ProcessAttempts { get; private set; }
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Files-only move";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => null;
        private Exception NoProcess() { ProcessAttempts++; return new InvalidOperationException("No process operation is permitted."); }
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
    }
    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static int FreePort(int first = 62000)
    {
        var info = IPGlobalProperties.GetIPGlobalProperties();
        var ports = info.GetActiveTcpListeners().Concat(info.GetActiveUdpListeners()).Select(value => value.Port).ToHashSet();
        return Enumerable.Range(first, 65000 - first).First(value => !ports.Contains(value));
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
