using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Real FriendLink calls over fake authenticated HTTP; no listener or process.
internal static class SharedRosterBootstrapChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string root)
    {
        using (var fixture = new Fixture(root))
        {
            fixture.Current = fixture.Legacy;
            fixture.Page = [fixture.Bootstrap];
            var raced = await fixture.CheckAsync();
            Require(!raced.Available && raced.Code == "RosterCatchUpPending" &&
                !fixture.Chain.HasState(fixture.ProfileId), "roster race committed a mismatched root");
            fixture.AssertFloor(fixture.Legacy);
            fixture.Current = fixture.Bootstrap;
            var retried = await fixture.CheckAsync();
            Require(retried.Available && retried.Revision == fixture.Bootstrap.Revision,
                "matching retry did not recover from a concurrent legacy roster update");
            fixture.AssertFloor(fixture.Bootstrap);
        }

        using (var fixture = new Fixture(root))
        {
            fixture.Seed();
            var recovered = await fixture.CheckAsync();
            Require(recovered.Available && recovered.Revision == fixture.Current.Revision &&
                fixture.Chain.Count(fixture.ProfileId) == 2 && fixture.LastOffset == 2,
                "valid persisted legacy bootstrap did not recover at its committed offset");
            fixture.AssertFloor(fixture.Current);
        }

        foreach (var fault in new[] { "missing-cache", "invalid-cache", "invalid-cache-json", "wrong-cache-profile",
            "wrong-cache-group", "wrong-cache-owner", "schema3-floor", "same-epoch", "same-revision",
            "sibling", "wrong-group", "wrong-owner", "missing-middle", "competing-heads", "missing-lookup" })
        {
            using var fixture = new Fixture(root);
            var expected = "RosterRollback";
            switch (fault)
            {
                case "missing-cache": fixture.Data.DeleteProtected(fixture.CacheName); break;
                case "invalid-cache": fixture.SaveCache(fixture.Legacy with { Signature = new string('A', 88) }); break;
                case "invalid-cache-json":
                    fixture.Data.SaveProtected(fixture.CacheName, [0x7b]);
                    expected = "RosterRejected";
                    break;
                case "wrong-cache-profile": fixture.SaveCache(fixture.Sign(fixture.Legacy with { ProfileId = Guid.NewGuid() })); break;
                case "wrong-cache-group": fixture.SaveCache(fixture.Sign(fixture.Legacy with { GroupId = Guid.NewGuid() })); break;
                case "wrong-cache-owner": fixture.SaveCache(fixture.OtherOwnerRoster(fixture.Legacy)); break;
                case "schema3-floor":
                    fixture.Legacy = fixture.Sign(fixture.Legacy with
                    { Schema = 3, SignerDeviceId = Guid.Empty, SignerPublicKey = fixture.Legacy.OwnerPublicKey });
                    fixture.SaveCache(fixture.Legacy);
                    break;
                case "same-epoch": fixture.Bootstrap = fixture.Sign(fixture.Bootstrap with { Epoch = fixture.Legacy.Epoch }); break;
                case "same-revision": fixture.Bootstrap = fixture.Sign(fixture.Bootstrap with { Revision = fixture.Legacy.Revision }); break;
                case "sibling": fixture.Bootstrap = fixture.Sign(fixture.Bootstrap with { Epoch = 1, Revision = 1, OwnerOverride = true }); break;
                case "wrong-group": fixture.Bootstrap = fixture.Sign(fixture.Bootstrap with { GroupId = Guid.NewGuid() }); break;
                case "wrong-owner":
                    fixture.Bootstrap = fixture.OtherOwnerRoster(fixture.Bootstrap);
                    expected = "SigningIdentityChanged";
                    break;
                case "missing-middle":
                case "missing-lookup": expected = "RosterRejected"; break;
            }
            fixture.Seed(includeChild: fault != "missing-middle");
            if (fault == "missing-middle")
            {
                fixture.Current = fixture.Child(fixture.Child(fixture.Bootstrap));
                fixture.Page = [fixture.Current];
            }
            if (fault == "competing-heads")
            {
                var sibling = fixture.Sign(fixture.Current with { OwnerOverride = !fixture.Current.OwnerOverride });
                fixture.Chain.Append(sibling, sibling.OwnerPublicKey);
            }
            if (fault == "missing-lookup")
                fixture.Data.DeleteProtected($"shared-roster-lookup-{fixture.ProfileId:N}-{SharedWorldRosterTrust.Hash(fixture.Bootstrap)}.protected");
            var rejected = await fixture.CheckAsync();
            Require(!rejected.Available && rejected.Code == expected,
                $"{fault} crossed the legacy floor: {rejected.Code}, expected {expected}");
            fixture.AssertFloor(fixture.Legacy);
        }

        // Mismatched singleton pages must never poison an empty journal.
        foreach (var fault in new[] { "sibling", "older", "wrong-profile", "wrong-group", "wrong-owner" })
        {
            using var fixture = new Fixture(root);
            fixture.Current = fixture.Bootstrap;
            fixture.Page = [fault switch
            {
                "sibling" => fixture.Sign(fixture.Bootstrap with { OwnerOverride = true }),
                "older" => fixture.Legacy,
                "wrong-profile" => fixture.Sign(fixture.Bootstrap with { ProfileId = Guid.NewGuid() }),
                "wrong-group" => fixture.Sign(fixture.Bootstrap with { GroupId = Guid.NewGuid() }),
                _ => fixture.OtherOwnerRoster(fixture.Bootstrap)
            }];
            var rejected = await fixture.CheckAsync();
            Require(!rejected.Available && rejected.Code == "RosterRejected" &&
                !fixture.Chain.HasState(fixture.ProfileId), $"{fault} singleton changed the journal");
            fixture.AssertFloor(fixture.Legacy);
        }

        using (var fixture = new Fixture(root))
        {
            fixture.Data.SaveProtected(fixture.CacheName, [0x7b]);
            fixture.Current = fixture.Legacy;
            fixture.Page = [fixture.Legacy];
            Require((await fixture.CheckAsync()).Available,
                "an exact signed trusted floor acquired a cache dependency");
        }
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly Guid deviceId = Guid.NewGuid();
        private readonly string credential = new('B', 64);
        private FriendLink? link;
        public Guid ProfileId { get; } = Guid.NewGuid();
        public LocalData Data { get; }
        public SharedWorldRosterChainStore Chain { get; }
        public SharedWorldRoster Legacy { get; set; }
        public SharedWorldRoster Bootstrap { get; set; }
        public SharedWorldRoster Current { get; set; }
        public IReadOnlyList<SharedWorldRoster> Page { get; set; } = [];
        public long LastOffset { get; private set; } = -1;
        public string CacheName => $"shared-world-roster-{deviceId:N}-{ProfileId:N}.protected";

        public Fixture(string root)
        {
            Data = new LocalData(Path.Combine(root, "roster-bootstrap-" + Guid.NewGuid().ToString("N")));
            Chain = new(Data);
            Legacy = Sign(new(2, Guid.NewGuid(), ProfileId, 1, 1, false,
                Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo()),
                [new(deviceId, Convert.ToBase64String(device.ExportSubjectPublicKeyInfo()), new(Receive: true), false)], ""));
            Bootstrap = Sign(Legacy with { Epoch = 2, Revision = 2 });
            Current = Bootstrap;
            SaveCache(Legacy);
            Data.SaveProtected($"shared-world-pc-signing-{deviceId:N}.protected", device.ExportPkcs8PrivateKey());
        }

        public SharedWorldRoster Sign(SharedWorldRoster roster) => roster with
        { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };

        public SharedWorldRoster OtherOwnerRoster(SharedWorldRoster roster)
        {
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var draft = roster with { OwnerPublicKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()) };
            return draft with { Signature = Convert.ToBase64String(other.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
        }

        public SharedWorldRoster Child(SharedWorldRoster parent) => Sign(parent with
        {
            Schema = 3,
            Epoch = parent.Epoch + 1,
            Revision = parent.Revision + 1,
            PreviousRosterHash = SharedWorldRosterTrust.Hash(parent),
            SignerDeviceId = Guid.Empty,
            SignerPublicKey = parent.OwnerPublicKey
        });

        public void SaveCache(SharedWorldRoster roster) => Data.SaveProtected(CacheName, JsonSerializer.SerializeToUtf8Bytes(roster, Json));

        public void Seed(bool includeChild = true)
        {
            Chain.Append(Bootstrap, Bootstrap.OwnerPublicKey);
            Current = Bootstrap;
            if (includeChild)
            {
                Current = Child(Bootstrap);
                // The wrong-owner refusal is checked before reading the chain;
                // its child still needs a valid owner signature to seed it.
                if (Current.OwnerPublicKey != Legacy.OwnerPublicKey) Current = Bootstrap;
                else Chain.Append(Current, Current.OwnerPublicKey);
            }
        }

        public async Task<SharedWorldSharingView> CheckAsync()
        {
            if (link is null)
            {
                const string endpoint = "https://127.0.0.1:5131";
                Data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
                {
                    DeviceId = deviceId,
                    HostId = Guid.NewGuid(),
                    Endpoint = endpoint,
                    Fingerprint = new string('A', 64),
                    Credential = credential,
                    CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(90),
                    SharedWorldSigningKeys = new() { [ProfileId] = Legacy.OwnerPublicKey },
                    SharedRosterFloors = new() { [ProfileId] = new(Legacy.GroupId, Legacy.Epoch, Legacy.Revision, Legacy.Signature) }
                }, Json));
                link = new FriendLink(Data, "friend.protected", (address, pins) =>
                {
                    Require(address == endpoint && pins.Contains(new string('A', 64)), "escaped the pinned transport");
                    return new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri(address) };
                });
                Require((await link.PollAsync()).Profiles.Single().Id == ProfileId, "synthetic profile unavailable");
            }
            return await link.CheckSharedWorldSharingAsync(ProfileId);
        }

        public void AssertFloor(SharedWorldRoster expected)
        {
            var saved = JsonSerializer.Deserialize<FriendConfiguration>(Data.LoadProtected("friend.protected")!, Json)!;
            Require(saved.SharedRosterFloors[ProfileId] == new SharedRosterFloor(expected.GroupId,
                expected.Epoch, expected.Revision, expected.Signature) &&
                saved.SharedWorldSigningKeys[ProfileId] == Legacy.OwnerPublicKey,
                "verification changed the saved trust floor or owner");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.Headers.Authorization?.Parameter == credential &&
                request.Headers.GetValues("X-Device-Id").Single() == deviceId.ToString(), "lost device authentication");
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/api/companion/heartbeat") body = new CompanionStatus(false, null,
                [new(ProfileId, "Synthetic roster", "Offline", null, Kind: GameKinds.Valheim)], false, false,
                DateTimeOffset.UtcNow, new("synthetic", CompanionProtocol.Current, CompanionProtocol.Minimum,
                    [CompanionProtocol.SharedWorldsCapability]));
            else if (path.EndsWith("/enrollment", StringComparison.Ordinal))
                body = request.Method == HttpMethod.Get ? new SharedWorldEnrollmentChallenge(new string('N', 44)) : new { ok = true };
            else if (path.EndsWith("/revisions/current", StringComparison.Ordinal)) body = Current;
            else if (path.EndsWith("/revisions", StringComparison.Ordinal))
            {
                Require(long.TryParse(request.RequestUri.Query.AsSpan("?offset=".Length), out var offset), "invalid offset");
                LastOffset = offset;
                body = Page;
            }
            else throw new InvalidOperationException("Unexpected synthetic roster request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json)) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { link?.Dispose(); owner.Dispose(); device.Dispose(); Data.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
