using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Deterministic schedules over real signed journals and disposable protected
// storage. No app, peer listener, game, process or production state is opened.
internal static class SharedRosterConcurrentImportChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Run(string root)
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo());
        foreach (var replayLegacy in new[] { false, true })
        {
            using var data = new LocalData(Path.Combine(root, "concurrent-roster-" + Guid.NewGuid().ToString("N")));
            var firstImporter = new SharedWorldRosterChainStore(data);
            var peerImporter = new SharedWorldRosterChainStore(data);
            var member = new SharedWorldRosterMember(Guid.NewGuid(), Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()),
                new(Receive: true, EligibleHost: true, Reshare: true), false);
            var baseline = Sign(new(2, Guid.NewGuid(), Guid.NewGuid(), 1, 1, false, key, [member], ""), owner);
            firstImporter.Append(baseline, key);
            var incoming = replayLegacy ? Sign(baseline with { Epoch = 2, Revision = 2 }, owner) : Child(baseline, owner);
            Require(!firstImporter.Contains(baseline.ProfileId, incoming), "incoming revision was already retained before peer import");
            // Another importer commits the same response and its descendant
            // after the first importer's Contains check, before its Append.
            peerImporter.Append(incoming, key);
            var latest = Sign(Child(incoming, owner) with { Members = [member with { Revoked = true }] }, owner);
            peerImporter.Append(latest, key);
            var before = Snapshot(data);
            firstImporter.Append(incoming, key);
            var reopened = new SharedWorldRosterChainStore(data);
            reopened.Append(baseline, key);
            reopened.Append(latest, key);
            Require(reopened.Read(baseline.ProfileId).Count == 3 &&
                reopened.Heads(baseline.ProfileId).Single().Signature == latest.Signature &&
                !SharedWorldRosterTrust.HasRole(reopened.Heads(baseline.ProfileId).Single(), member.DeviceId,
                    member.PublicKey, grants => grants.EligibleHost) &&
                Snapshot(data).SequenceEqual(before), "replaying retained revisions changed the journal, current head or revocation");

            Reject(() => reopened.Append(incoming, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())),
                "a retained revision accepted a different pinned owner");
            Reject(() => reopened.Append(latest, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())),
                "the current retained head accepted a different pinned owner");
            Reject(() => reopened.Append(incoming with { Signature = new string('A', 88) }, key),
                "a corrupted replay signature was accepted");
            var unrelatedParent = Sign(baseline with { OwnerOverride = !baseline.OwnerOverride }, owner);
            Reject(() => reopened.Append(Child(unrelatedParent, owner), key),
                "a missing signed parent was replaced by same-counter history");
            Require(Snapshot(data).SequenceEqual(before), "a rejected replay changed protected history");

            var lookup = $"shared-roster-lookup-{baseline.ProfileId:N}-{SharedWorldRosterTrust.Hash(incoming)}.protected";
            var savedLookup = data.LoadProtected(lookup)!;
            data.DeleteProtected(lookup);
            Reject(() => reopened.Append(incoming, key), "replay rebuilt a missing protected lookup");
            Require(!data.HasProtected(lookup), "replay silently repaired a missing trust record");
            data.SaveProtected(lookup, savedLookup);
            var revisionPath = Path.Combine(data.RootPath, "shared-worlds", baseline.ProfileId.ToString("N"),
                "roster-chain", SharedWorldRosterTrust.Hash(incoming) + ".json");
            var original = File.ReadAllBytes(revisionPath);
            File.WriteAllBytes(revisionPath, JsonSerializer.SerializeToUtf8Bytes(incoming with { OwnerOverride = !incoming.OwnerOverride }, Json));
            var damaged = Snapshot(data);
            Reject(() => reopened.Append(incoming, key), "replay accepted a modified retained revision file");
            Require(Snapshot(data).SequenceEqual(damaged), "replay replaced damaged history");
            File.WriteAllBytes(revisionPath, original);
            Require(reopened.Read(baseline.ProfileId).Count == 3, "negative replay checks advanced or reset the journal");
        }
    }

    private static SharedWorldRoster Sign(SharedWorldRoster roster, ECDsa key) => roster with
    { Signature = Convert.ToBase64String(key.SignData(SharedWorldRosterTrust.Basis(roster), HashAlgorithmName.SHA256)) };

    private static SharedWorldRoster Child(SharedWorldRoster parent, ECDsa key) => Sign(parent with
    {
        Schema = 3,
        Epoch = parent.Epoch + 1,
        Revision = parent.Revision + 1,
        PreviousRosterHash = SharedWorldRosterTrust.Hash(parent),
        SignerDeviceId = Guid.Empty,
        SignerPublicKey = parent.OwnerPublicKey
    }, key);

    private static string[] Snapshot(LocalData data) => Directory.EnumerateFiles(data.RootPath, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "host.lock")
        .Select(path => Path.GetRelativePath(data.RootPath, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .Order(StringComparer.Ordinal).ToArray();

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
