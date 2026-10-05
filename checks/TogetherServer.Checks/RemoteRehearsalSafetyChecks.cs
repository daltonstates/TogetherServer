using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

internal static class RemoteRehearsalSafetyChecks
{
    internal static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "rehearsal-safety");
        using var data = new LocalData(directory);
        var games = new GameServerRegistry(data, includeFixture: true, PortProbeMode.ObserveOnly);
        var manager = new HostManager(data, games);
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        Require(!(await manager.PrepareRemoteRehearsalAsync(false)).Ok && data.LoadSettings().Profiles.Count == 0,
            "production can prepare synthetic data");
        var prepared = await manager.PrepareRemoteRehearsalAsync(true);
        if (!prepared.Ok && data.LoadSettings().Profiles.SingleOrDefault() is { } failedProfile &&
            data.LoadBackupCatalog().Records.LastOrDefault() is { } backup)
            throw new Exception(new SharedWorldService(data, new WorldBackupService(data, TimeProvider.System, games: games))
                .PublishAfterStop(failedProfile, backup.Id).Message);
        Require(prepared.Ok && prepared.ProfileId is not null, "staging prepare failed: " + prepared.Message);
        var id = prepared.ProfileId!.Value;
        Require((await manager.PrepareRemoteRehearsalAsync(true)).ProfileId == id, "prepare retry changed identity");
        var shared = await manager.SharedWorldStatusAsync(id);
        Require(shared.Latest is not null && SharedWorldService.VerifySignature(shared.Latest), "synthetic manifest signature invalid");
        var version = shared.Latest!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid();
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var roster = await manager.PublishSharedWorldRosterAsync(id,
            [new(deviceId, publicKey, new(Receive: true), false)]);
        var chat = new ServerChat(data);
        var hostId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        chat.Post(hostId, id, deviceId, "Test PC", RemoteRehearsal.Message(requestId), requestId);
        var exchange = await manager.RehearsalExchangeAsync(id, deviceId, requestId, requestId, chat, hostId, true);
        Require(exchange.Ok && !exchange.ReceiptConfirmed, "a synthetic copy was confirmed without a receipt");
        var draft = new SharedWorldReceipt(1, version.GroupId, id, version.VersionHash, deviceId,
            roster.Epoch, roster.Revision, Guid.NewGuid(), "");
        var signed = draft with
        {
            Signature = Convert.ToBase64String(key.SignData(
            SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256))
        };
        Require(!(await manager.ConfirmSharedWorldReceiptAsync(id, deviceId, signed with { Signature = "forged" })).Ok,
            "forged receipt accepted");
        Require(!(await manager.ConfirmSharedWorldReceiptAsync(id, deviceId, signed with { VersionHash = new string('0', 64) })).Ok,
            "another copy receipt accepted");
        Require((await manager.ConfirmSharedWorldReceiptAsync(id, deviceId, signed)).Ok, "valid exact receipt rejected");
        Require((await manager.RehearsalExchangeAsync(id, deviceId, requestId, requestId, chat, hostId, true)).ReceiptConfirmed,
            "valid receipt was not verified");
        Require(!(await manager.RehearsalExchangeAsync(Guid.NewGuid(), deviceId, requestId, requestId, chat, hostId, true)).Ok,
            "wrong profile accepted");
        Require(!(await manager.RehearsalExchangeAsync(id, deviceId, Guid.NewGuid(), requestId, chat, hostId, true)).Ok,
            "another request's chat message accepted");
        await manager.PublishSharedWorldRosterAsync(id,
            [new(deviceId, publicKey, new(Receive: true), false, DateTimeOffset.UtcNow.AddSeconds(-1))]);
        Require(!(await manager.RehearsalExchangeAsync(id, deviceId, requestId, requestId, chat, hostId, true)).Ok,
            "expired membership accepted");
        await manager.PublishSharedWorldRosterAsync(id, [new(deviceId, publicKey, new(Receive: true), false)]);
        Require(!(await manager.RehearsalExchangeAsync(id, deviceId, requestId, requestId, chat, hostId, true)).ReceiptConfirmed,
            "stale roster receipt accepted after an access revision");
        Require(RemoteRehearsal.Context("https://127.0.0.1:5132", "SeparateNetwork") == "Loopback" &&
            RemoteRehearsal.Context("https://192.0.2.1:5132", "SeparateNetwork") == "OwnerReportedSeparateNetwork",
            "network evidence inferred a WAN route");
        await FreshStatusAsync(root);
    }

    private static async Task FreshStatusAsync(string root)
    {
        foreach (var failure in new[] { "unassigned", "denied", "malformed" })
        {
            using var receiver = new LocalData(Path.Combine(root,
                "rehearsal-status-" + failure));
            var profileId = Guid.NewGuid();
            var pendingProfileId = Guid.NewGuid();
            var operationId = Guid.NewGuid();
            var configuration = new FriendConfiguration
            {
                Endpoint = "https://127.0.0.1:5132",
                Fingerprint = new string('A', 64),
                DeviceId = Guid.NewGuid(),
                Credential = new string('B', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
                CachedProfiles = [new(profileId, "Disposable rehearsal", "Offline", null, Kind: GameKinds.Fixture)],
                PendingOperations = [new() { RequestId = Guid.NewGuid(), ProfileId = pendingProfileId,
                    Action = "start", OperationId = operationId }]
            };
            receiver.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(configuration,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var statusReads = 0;
            using var friend = new FriendLink(receiver, "friend.protected", (endpoint, pins) =>
            {
                if (endpoint != configuration.Endpoint || !pins.Contains(configuration.Fingerprint))
                    throw new Exception("rehearsal status did not use the saved pinned connection");
                return new HttpClient(new RehearsalStatusHandler(request =>
                {
                    if (request.RequestUri!.AbsolutePath == $"/api/companion/operations/{operationId}")
                        return new(HttpStatusCode.OK)
                        {
                            Content = JsonContent.Create(new RemoteOperationView(
                            operationId, pendingProfileId, "start", RemoteOperationStates.Running, null,
                            "OperationRunning", "Synthetic operation", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null))
                        };
                    if (request.RequestUri.AbsolutePath != "/api/companion/status")
                        throw new Exception("unexpected rehearsal status request");
                    statusReads++;
                    if (failure == "denied") return new(HttpStatusCode.Forbidden);
                    if (failure == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("{") };
                    return new(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new CompanionStatus(true, null,
                            [new(pendingProfileId, "Other assigned server", "Starting", null)],
                            false, false, DateTimeOffset.UtcNow, CompanionProtocol.Describe()))
                    };
                }))
                { BaseAddress = new Uri(endpoint + "/") };
            });
            var cached = await friend.PollAsync();
            if (cached.State != "Connected" || !cached.Profiles.Any(profile => profile.Id == profileId) || statusReads != 0)
                throw new Exception("pending-operation regression did not retain the old server assignment");
            var report = await friend.RunRemoteRehearsalAsync(profileId, new(Guid.NewGuid()));
            if (statusReads != 1 || report.Stages.Single(stage => stage.Id == "connection").State != "Failed" ||
                report.Stages.Any(stage => stage.State == "Passed"))
                throw new Exception("cached operation status passed a rehearsal after current assignment or access was denied");
        }
    }

    private sealed class RehearsalStatusHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
