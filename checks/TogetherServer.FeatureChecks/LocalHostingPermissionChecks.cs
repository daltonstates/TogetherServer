using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Signed synthetic state and injected HTTP only. Permission reads must never
// launch a process/listener or enter save, installed-server, port or route checks.
internal static class LocalHostingPermissionChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);
    private static readonly SharedWorldGrants Hosting = new(Receive: true, EligibleHost: true);

    internal static async Task RunAsync(string root)
    {
        await PermissionBeforePreparationAsync(root);
        await RefreshSignedPermissionAsync(root);
        await RefreshStatusWithoutHeartbeatAsync(root);
        await ConnectionAndCancellationAsync(root);
        foreach (var fault in new[] { "missing-grant", "receive-only", "revoked-member", "expired-member",
            "known-revoked", "owner-expired", "pending-approval", "missing-floor", "stale-floor", "missing-owner",
            "wrong-owner", "missing-group", "pending-group", "conflicting-history", "missing-key", "wrong-key",
            "missing-roster", "damaged-signature", "authority-fence" })
        {
            using var fixture = new Fixture(root);
            var expected = "HostingPermissionRequired";
            switch (fault)
            {
                case "missing-grant": fixture.ReplaceMember(member => member with { Grants = new() }); break;
                case "receive-only": fixture.ReplaceMember(member => member with { Grants = new(Receive: true) }); break;
                case "revoked-member": fixture.ReplaceMember(member => member with { Revoked = true }); break;
                case "expired-member": fixture.ReplaceMember(member => member with { AccessExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }); break;
                case "known-revoked": fixture.Config.KnownRevoked = true; expected = "Revoked"; break;
                case "owner-expired": fixture.SetViewCode("AccessExpired"); expected = "AccessExpired"; break;
                case "pending-approval": fixture.SetViewCode("ApprovalPending"); expected = "ApprovalPending"; break;
                case "missing-floor": fixture.Config.SharedRosterFloors.Clear(); expected = "SharingPermissionUnknown"; break;
                case "stale-floor":
                    fixture.Config.SharedRosterFloors[fixture.ProfileId] =
                    fixture.Config.SharedRosterFloors[fixture.ProfileId] with { Revision = 2 };
                    expected = "SharingReviewRequired"; break;
                case "missing-owner": fixture.Config.SharedWorldSigningKeys.Clear(); expected = "SharingPermissionUnknown"; break;
                case "wrong-owner":
                    fixture.Config.SharedWorldSigningKeys[fixture.ProfileId] = fixture.MemberPublicKey;
                    expected = "SigningIdentityChanged"; break;
                case "missing-group": fixture.Config.ApprovedSharedWorldGroups.Clear(); expected = "SharingPermissionUnknown"; break;
                case "pending-group":
                    fixture.Config.PendingSharedWorldGroups[fixture.ProfileId] = Guid.NewGuid();
                    expected = "WorldHistoryReviewRequired"; break;
                case "conflicting-history":
                    fixture.Config.SharedWorldConflicts.Add(fixture.ProfileId);
                    expected = "WorldHistoryReviewRequired"; break;
                case "missing-key":
                    fixture.Data.DeleteProtected(fixture.KeyName);
                    expected = "HostingIdentityRequired"; break;
                case "wrong-key":
                    using (var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                        fixture.Data.SaveProtected(fixture.KeyName, wrong.ExportPkcs8PrivateKey());
                    expected = "HostingIdentityRequired";
                    break;
                case "missing-roster": fixture.Data.DeleteProtected(fixture.RosterName); expected = "SharingPermissionUnknown"; break;
                case "damaged-signature":
                    fixture.SaveRoster(fixture.Roster with { Signature = new string('A', 88) });
                    expected = "SharingReviewRequired"; break;
                case "authority-fence":
                    var chain = new SharedWorldRosterChainStore(fixture.Data);
                    chain.Append(fixture.Roster, fixture.Roster.OwnerPublicKey);
                    fixture.AdvanceRoster(Hosting);
                    chain.Append(fixture.Roster, fixture.Roster.OwnerPublicKey);
                    expected = "SharingReviewRequired";
                    break;
            }
            var result = await fixture.Service.PermissionAsync(fixture.ProfileId);
            Require(!result.Allowed && result.Code == expected && fixture.Requests == 0,
                $"{fault} crossed the permission boundary: {result.Code}");
            fixture.AssertNoPreparation();
        }
    }

    private static async Task PermissionBeforePreparationAsync(string root)
    {
        foreach (var save in new[] { "missing", "complete", "damaged-pointer", "damaged-payload" })
        {
            using var fixture = new Fixture(root);
            if (save != "missing") fixture.SeedSave(save);
            // Hold the Host lifecycle gate. An accidental snapshot/port/readiness
            // check would wait here instead of returning the permission result.
            var lifecycleGate = Field<SemaphoreSlim>(fixture.Manager, "gate");
            await lifecycleGate.WaitAsync();
            try
            {
                var result = await fixture.Service.PermissionAsync(fixture.ProfileId).WaitAsync(Watchdog);
                Require(result == new LocalHostingPermission(true, "HostingAllowed",
                    "The owner allows this PC to host this server."),
                    $"{save} save/server/port/readiness setup hid an authorized hosting action");
                var access = await fixture.Link.LocalHostingAccessAsync(fixture.ProfileId, false,
                    CancellationToken.None, verifyFiles: true, permissionOnly: true);
                Require(access.Allowed && access.Version is null && !access.SaveVerified,
                    "a permission-only read selected or verified save bytes");
            }
            finally { lifecycleGate.Release(); }
            Require(fixture.Requests == 0, "an offline permission read issued a network request");
            fixture.AssertNoPreparation();
        }
    }

    private static async Task RefreshSignedPermissionAsync(string root)
    {
        using (var first = new Fixture(root))
        {
            first.Config.SharedRosterFloors.Clear();
            first.Config.SharedWorldSigningKeys.Clear();
            first.Config.ApprovedSharedWorldGroups.Clear();
            first.Data.DeleteProtected(first.RosterName);
            first.Data.DeleteProtected(first.KeyName);
            first.EnrollFreshIdentity = true;
            first.Connect();
            var result = await first.Service.PermissionAsync(first.ProfileId);
            Require(result.Allowed && first.Requests == 4 &&
                first.Config.SharedRosterFloors[first.ProfileId].Signature == first.Roster.Signature,
                $"first-time signed enrollment did not expose hosting permission: {result.Code}");
            first.AssertNoPreparation();
        }
        using (var enabled = new Fixture(root))
        {
            enabled.ReplaceMember(member => member with { Grants = new() });
            Require(!(await enabled.Service.PermissionAsync(enabled.ProfileId)).Allowed,
                "the unsigned local UI state granted hosting");
            enabled.AdvanceRoster(Hosting);
            enabled.Connect();
            Require((await enabled.Service.PermissionAsync(enabled.ProfileId)).Allowed && enabled.Requests == 4 &&
                enabled.Config.SharedRosterFloors[enabled.ProfileId].Revision == 2,
                "a newly enabled owner permission remained hidden behind the older signed grant");
            enabled.AssertNoPreparation();
        }
        using (var offline = new Fixture(root))
        {
            offline.Connect();
            offline.TransportOffline = true;
            Require((await offline.Service.PermissionAsync(offline.ProfileId)).Allowed && offline.Requests == 1,
                "an unreachable original Host invalidated a current signed offline grant");
            offline.AssertNoPreparation();
        }
        using (var denied = new Fixture(root))
        {
            denied.Connect();
            denied.EnrollmentDenied = true;
            var result = await denied.Service.PermissionAsync(denied.ProfileId);
            Require(!result.Allowed && result.Code == "EnrollmentDenied" && denied.Requests == 1,
                "an explicit reachable denial fell back to a cached hosting grant");
        }
        using (var removed = new Fixture(root))
        {
            removed.AdvanceRoster(new());
            removed.Connect();
            var result = await removed.Service.PermissionAsync(removed.ProfileId);
            Require(!result.Allowed && result.Code == "HostingPermissionRequired" &&
                removed.Config.SharedRosterFloors[removed.ProfileId].Revision == 2,
                "a newly withdrawn signed grant retained hosting permission");
        }
    }

    private static async Task RefreshStatusWithoutHeartbeatAsync(string root)
    {
        using var fixture = new Fixture(root);
        fixture.Config.SharedRosterFloors.Clear();
        fixture.Config.SharedWorldSigningKeys.Clear();
        fixture.Config.ApprovedSharedWorldGroups.Clear();
        fixture.Data.DeleteProtected(fixture.RosterName);
        fixture.Connect();
        var statusService = new FriendLocalHostingService(fixture.Data, fixture.Friends, fixture.Manager, _ => []);
        fixture.TransportOffline = true;
        var unknown = await statusService.StatusAsync(fixture.ProfileId);
        Require(!unknown.Allowed && unknown.Code == "SharingPermissionUnknown" &&
            unknown.Steps.Single(step => step.Key == "permission").State == "Unknown" && !unknown.ReadyForManualStart,
            "an unavailable first status read fabricated an owner denial");
        fixture.TransportOffline = false;
        fixture.Disconnect();
        var refreshed = await statusService.StatusAsync(fixture.ProfileId);
        Require(refreshed.Allowed && refreshed.Steps.Single(step => step.Key == "permission").State == "Ready" &&
            fixture.Config.SharedRosterFloors[fixture.ProfileId].Signature == fixture.Roster.Signature &&
            !refreshed.ReadyForManualStart,
            $"dialog Retry did not acquire current signed permission without a heartbeat or prepare: code={refreshed.Code}, allowed={refreshed.Allowed}, permission={refreshed.Steps.Single(step => step.Key == "permission").State}, floor={fixture.Config.SharedRosterFloors.GetValueOrDefault(fixture.ProfileId)?.Revision}, requests={fixture.Requests}");
        var requests = fixture.Requests;
        fixture.TransportOffline = true;
        var retained = await statusService.StatusAsync(fixture.ProfileId);
        Require(retained.Allowed && retained.Steps.Single(step => step.Key == "permission").State == "Ready" &&
            fixture.Requests == requests && !retained.ReadyForManualStart,
            "a disconnected status projection contacted the Host despite a current verified signed grant");
        fixture.TransportOffline = false;
        fixture.Connect();
        fixture.AdvanceRoster(new());
        var withdrawn = await statusService.StatusAsync(fixture.ProfileId);
        Require(!withdrawn.Allowed && withdrawn.Code == "HostingPermissionRequired" &&
            withdrawn.Steps.Single(step => step.Key == "permission").State == "ActionRequired" &&
            fixture.Config.SharedRosterFloors[fixture.ProfileId].Revision == 2 && !withdrawn.ReadyForManualStart,
            "a full status read did not enforce a freshly signed hosting withdrawal");
        fixture.AssertNoPreparation();
    }

    private static async Task ConnectionAndCancellationAsync(string root)
    {
        using (var changed = new Fixture(root))
        {
            var otherId = changed.AddConnection();
            changed.Connect();
            changed.BeforeCurrentRoster = () => Require(changed.Friends.Select(otherId).Ok,
                "could not switch the synthetic saved Host");
            var result = await changed.Service.PermissionAsync(changed.ProfileId);
            Require(!result.Allowed && result.Code == "ConnectionChanged" &&
                Field<int>(changed.Link, "retainedOperations") == 0,
                "a permission from the previously selected Host escaped connection fencing");
        }
        using (var closed = new Fixture(root))
        {
            closed.Connect();
            closed.BeforeCurrentRoster = closed.Friends.Dispose;
            var result = await closed.Service.PermissionAsync(closed.ProfileId);
            Require(!result.Allowed && result.Code == "ConnectionChanged" &&
                Field<int>(closed.Link, "retainedOperations") == 0,
                "a closing saved connection escaped the retained permission read");
        }
        using (var cancelled = new Fixture(root))
        using (var token = new CancellationTokenSource())
        {
            token.Cancel();
            try
            {
                await cancelled.Service.PermissionAsync(cancelled.ProfileId, token.Token);
                throw new InvalidOperationException("cancelled permission read returned a result");
            }
            catch (OperationCanceledException) { }
            Require(Field<int>(cancelled.Link, "retainedOperations") == 0 &&
                (await cancelled.Service.PermissionAsync(cancelled.ProfileId)).Allowed && cancelled.Requests == 0,
                "cancelled permission retained the connection or damaged its gate");
        }
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly LocalHostingChecks.FilesOnlyDriver driver = new();
        private readonly Guid deviceId = Guid.NewGuid();
        private readonly List<SharedWorldRoster> history = [];
        internal readonly Guid ProfileId = Guid.NewGuid();
        internal readonly LocalData Data;
        internal readonly FriendService Friends;
        internal readonly FriendLink Link;
        internal readonly HostManager Manager;
        internal readonly FriendLocalHostingService Service;
        internal FriendConfiguration Config => Field<FriendConfiguration>(Link, "config");
        internal SharedWorldRoster Roster { get; private set; }
        internal string MemberPublicKey => Public(member);
        internal string KeyName => $"shared-world-pc-signing-{deviceId:N}.protected";
        internal string RosterName => $"shared-world-roster-{deviceId:N}-{ProfileId:N}.protected";
        internal int Requests { get; private set; }
        internal bool TransportOffline, EnrollmentDenied, EnrollFreshIdentity;
        internal Action? BeforeCurrentRoster;

        internal Fixture(string root)
        {
            Data = new(Path.Combine(root, "hosting-permission-" + Guid.NewGuid().ToString("N")));
            Data.SaveSettings(new HostSettings());
            Roster = Sign(new(3, Guid.NewGuid(), ProfileId, 1, 1, true, Public(owner),
                [new(deviceId, MemberPublicKey, Hosting, false)], "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: Public(owner)));
            history.Add(Roster);
            Data.SaveProtected(KeyName, member.ExportPkcs8PrivateKey());
            SaveRoster(Roster);
            Data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
            {
                DeviceId = deviceId,
                Endpoint = "https://192.0.2.10:5131",
                Fingerprint = new string('A', 64),
                Credential = new string('C', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                CachedProfiles = [new(ProfileId, "Synthetic hosting permission", "Offline", null, Kind: GameKinds.Fixture)],
                ApprovedSharedWorldGroups = new() { [ProfileId] = Roster.GroupId },
                SharedWorldSigningKeys = new() { [ProfileId] = Roster.OwnerPublicKey },
                SharedRosterFloors = new() { [ProfileId] = new(Roster.GroupId, 1, 1, Roster.Signature) }
            }, Json));
            Friends = new(Data, (endpoint, pins) =>
            {
                Require(endpoint == "https://192.0.2.10:5131" && pins.Contains(new string('A', 64)),
                    "permission refresh escaped the saved pinned transport");
                return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri(endpoint) };
            });
            using var connection = Friends.RetainLocalHostingConnection();
            Link = connection?.Link ?? throw new InvalidOperationException("the synthetic saved connection is missing");
            Manager = new(Data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
            var route = new LocalHostingRouteCheck(_ => throw Preflight(), (_, _, _) => throw Preflight());
            Service = new(Data, Friends, Manager, _ => throw Preflight(), routeCheck: route,
                synchronizeListener: () => throw Preflight(), announceHosting: (_, _) => throw Preflight(),
                latestSource: (_, _) => throw Preflight());
        }

        internal void Connect() => SetField(Link, "view", Link.View() with
        { State = "Connected", Profiles = Config.CachedProfiles!, HostCapabilities = [CompanionProtocol.SharedWorldsCapability] });
        internal void Disconnect() => SetField(Link, "view", Link.View() with { State = "Disconnected/Unknown" });
        internal void SetViewCode(string code) => SetField(Link, "view", Link.View() with { ConnectionCode = code });
        internal void SaveRoster(SharedWorldRoster roster) => Data.SaveProtected(RosterName,
            JsonSerializer.SerializeToUtf8Bytes(roster, Json));
        internal void ReplaceMember(Func<SharedWorldRosterMember, SharedWorldRosterMember> change)
        {
            Roster = Sign(Roster with { Members = [change(Roster.Members.Single())] });
            history[0] = Roster;
            Config.SharedRosterFloors[ProfileId] = new(Roster.GroupId, Roster.Epoch, Roster.Revision, Roster.Signature);
            SaveRoster(Roster);
        }
        internal void AdvanceRoster(SharedWorldGrants grants)
        {
            Roster = Sign(Roster with
            {
                Revision = Roster.Revision + 1,
                Epoch = Roster.Epoch + 1,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(Roster),
                Members = [Roster.Members.Single() with { Grants = grants }]
            });
            history.Add(Roster);
        }
        internal Guid AddConnection()
        {
            var id = Guid.NewGuid();
            var file = $"friend-{id:N}.protected";
            Data.SaveProtected(file, JsonSerializer.SerializeToUtf8Bytes(Config, Json));
            Field<List<(Guid Id, FriendLink Link)>>(Friends, "links").Add((id, new(Data, file)));
            return id;
        }
        internal void SeedSave(string state)
        {
            var bytes = Encoding.UTF8.GetBytes("synthetic permission-only save");
            var version = SharedWorldService.SignVersion(new(4, Roster.GroupId, 1, null, ProfileId,
                GameKinds.Fixture, "SyntheticPermission", DateTimeOffset.UtcNow, SharedWorldCaptureKinds.PostStopBackup,
                Guid.NewGuid(), new(2456, false, "Unknown", [], []),
                [new("world.dat", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)))], "", "", ""), owner);
            var vault = Path.Combine(Data.RootPath, "received-shared-worlds", deviceId.ToString("N"), ProfileId.ToString("N"));
            var payload = Path.Combine(vault, version.VersionHash, SharedWorldService.PayloadDirectory);
            Directory.CreateDirectory(payload);
            File.WriteAllBytes(Path.Combine(payload, "world.dat"), state == "damaged-payload" ? [1] : bytes);
            File.WriteAllBytes(Path.Combine(vault, version.VersionHash, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            File.WriteAllBytes(Path.Combine(vault, "latest.json"), state == "damaged-pointer" ? [0x7b] : JsonSerializer.SerializeToUtf8Bytes(version, Json));
            Config.ConsentedSharedWorldProfiles.Add(ProfileId);
            Config.LastSharedHostManifests[ProfileId] = version;
        }
        internal void AssertNoPreparation() => Require(driver.ProcessAttempts == 0 && Data.LoadRuns().Count == 0 &&
            Data.LoadSettings().Profiles.Count == 0 && !Data.LoadSettings().CompanionListeningEnabled &&
            !Data.HasProtected("host-certificate.protected") &&
            new SharedWorldSeparateCopyStore(Data).Read(ProfileId).Count == 0,
            "a permission read prepared a world, server, TLS identity, listener or process");
        private SharedWorldRoster Sign(SharedWorldRoster draft) => draft with
        { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            if (TransportOffline) throw new HttpRequestException("synthetic unreachable original Host");
            Require(request.Headers.Authorization?.Parameter == new string('C', 64) &&
                request.Headers.GetValues("X-Device-Id").Single() == deviceId.ToString(),
                "permission refresh lost device authentication");
            var path = request.RequestUri!.AbsolutePath;
            var prefix = $"/api/companion/servers/{ProfileId}/shared-world";
            if (path == prefix + "/handoff/request")
                return new(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
            object body;
            if (path == prefix + "/enrollment")
            {
                if (EnrollmentDenied) return new(HttpStatusCode.Forbidden);
                if (request.Method == HttpMethod.Get) body = new SharedWorldEnrollmentChallenge(new string('N', 44));
                else
                {
                    Require(request.Method == HttpMethod.Post, "permission used an unexpected enrollment method");
                    if (EnrollFreshIdentity)
                    {
                        var proof = JsonSerializer.Deserialize<SharedWorldEnrollmentRequest>(
                            await request.Content!.ReadAsByteArrayAsync(cancellationToken), Json)!;
                        Roster = Sign(Roster with { Members = [Roster.Members.Single() with { PublicKey = proof.PublicKey }] });
                        history[0] = Roster;
                    }
                    body = new { ok = true };
                }
            }
            else if (path == prefix + "/roster/revisions/current")
            {
                BeforeCurrentRoster?.Invoke();
                body = Roster;
            }
            else if (path == prefix + "/roster/revisions")
            {
                Require(long.TryParse(request.RequestUri.Query.AsSpan("?offset=".Length), out var offset), "invalid roster offset");
                body = history.Skip(checked((int)offset)).ToArray();
            }
            else throw Preflight();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json)) };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Friends.Dispose(); Data.Dispose(); owner.Dispose(); member.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static InvalidOperationException Preflight() => new("Permission reads must not enter hosting preflight.");
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
