using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Fake HTTP only: a saved credential is useful only after a fresh authenticated
// reply. These checks do not open a listener or start an app or game.
internal static class WorldPeerSavedConnectionChecks
{
    internal static async Task RunAsync(string rootPath)
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new SharedWorldPeerRoot(Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo()),
            Guid.NewGuid(), Guid.NewGuid());
        var memberId = Guid.NewGuid();
        var branchHash = new string('B', 64);
        var route = new SharedWorldPeerRoute("https://192.0.2.95:5131", new string('A', 64));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var roster = new SharedWorldRoster(3, root.GroupId, root.ProfileId, 1, 1, true, root.OwnerPublicKey,
            [new(memberId, Convert.ToBase64String(member.ExportSubjectPublicKeyInfo()), new(), false)], "",
            SignerDeviceId: Guid.Empty, SignerPublicKey: root.OwnerPublicKey);
        roster = roster with { Signature = Convert.ToBase64String(owner.SignData(
            SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };
        using var data = new LocalData(Path.Combine(rootPath, "peer-saved-connection-" + Guid.NewGuid().ToString("N")));
        var config = new FriendConfiguration
        {
            Endpoint = route.Address, Fingerprint = route.CertificateSha256,
            DeviceId = Guid.NewGuid(), Credential = new string('C', 64),
            CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(60),
            PeerWorld = root, PeerWorldMemberDeviceId = memberId, PeerWorldBranchHash = branchHash,
            SharedWorldSigningKeys = new() { [root.ProfileId] = root.OwnerPublicKey },
            ApprovedSharedWorldGroups = new() { [root.ProfileId] = root.GroupId }
        };
        data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(config, json));
        using var handler = new Handler();
        HttpClient Client(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint == route.Address && pins.Contains(route.CertificateSha256),
                "saved peer reuse changed its pinned origin");
            return new HttpClient(handler, false) { BaseAddress = new Uri(endpoint + "/") };
        }
        using (var friends = new FriendService(data, Client))
        {
            Require(!await friends.SelectExistingPeerAsync(route.Address, root, memberId, branchHash, false) &&
                handler.Requests == 1, "a rejected saved token bypassed fresh enrollment");
            handler.Accept = true;
            Require(await friends.SelectExistingPeerAsync(route.Address, root, memberId, branchHash, false) &&
                handler.Requests == 2, "a freshly authenticated saved token could not be reused");
        }
        config.CredentialExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(config, json));
        using (var expired = new FriendService(data, Client))
            Require(!await expired.SelectExistingPeerAsync(route.Address, root, memberId, branchHash, false) &&
                handler.Requests == 2, "an expired token was selected or polled instead of allowing fresh enrollment");

        using var adoptionData = new LocalData(Path.Combine(rootPath, "peer-adoption-" + Guid.NewGuid().ToString("N")));
        using var adopted = new FriendService(adoptionData, Client);
        var enrollment = new WorldPeerSessionEnrollment(new(Guid.NewGuid(), new string('D', 64),
            DateTimeOffset.UtcNow.AddDays(60)), Guid.NewGuid(), Guid.NewGuid(), branchHash, memberId);
        handler.Accept = false;
        var saved = await adopted.AdoptPeerConnectionAsync(root, roster, memberId, false, route, enrollment, default);
        Require(saved.Ok && saved.Code == "PeerSessionSaved" && adopted.View().State == "Disconnected/Unknown",
            "saving a peer token reported a verified connection after the fresh poll failed");
        handler.Accept = true;
        var connected = await adopted.AdoptPeerConnectionAsync(root, roster, memberId, false, route,
            enrollment with { Credential = new(Guid.NewGuid(), new string('E', 64), DateTimeOffset.UtcNow.AddDays(60)) }, default);
        Require(connected.Ok && connected.Code == "PeerSessionConnected" && adopted.View().State == "Disabled",
            "a verified status-only peer connection was not reported accurately");
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal bool Accept;
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/companion/heartbeat" &&
                request.Headers.Authorization?.Scheme == "Bearer", "peer reuse sent an unexpected fixed operation");
            return Task.FromResult(Accept
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new CompanionStatus(
                    false, "Status only", [], false, false, DateTimeOffset.UtcNow, CompanionProtocol.Describe())) }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
