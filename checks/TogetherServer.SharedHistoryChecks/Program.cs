using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

var parent = Path.Combine(Path.GetTempPath(), "TogetherServer-shared-history-checks");
var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var profile = Guid.NewGuid();
    var device = Guid.NewGuid();
    var draft = new SharedWorldVersion(4, Guid.NewGuid(), 1, null, profile,
        GameKinds.Valheim, "fixture-world", DateTimeOffset.UtcNow,
        SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
        new SharedWorldPortableSetup(2456, false, "fixture", [], []),
        [new SharedWorldFile("world.dat", 1, Convert.ToHexString(SHA256.HashData([1])))],
        "", "", "");
    var versions = new List<SharedWorldVersion>();
    var head = SharedWorldService.SignVersion(draft, signer);
    versions.Add(head);
    for (var i = 1; i < 4102; i++)
    {
        head = SharedWorldService.SignVersion(head with
        {
            Number = head.Number + 1,
            ParentHash = head.VersionHash,
            BackupId = Guid.NewGuid()
        }, signer);
        versions.Add(head);
    }
    var vault = Path.Combine(root, "received-shared-worlds", device.ToString("N"), profile.ToString("N"));
    var firstFetch = long.MaxValue;
    var calls = 0;
    using (var data = new LocalData(root))
    {
        FriendLink.SharedChainCheck result;
        do
        {
            var fetched = 0;
            result = await FriendLink.VerifySharedChainBatchAsync(data, device, profile,
                versions[0], head, [], (number, _) =>
                {
                    firstFetch = Math.Min(firstFetch, number);
                    fetched++;
                    return Task.FromResult<SharedWorldVersion?>(versions[checked((int)number - 1)]);
                }, CancellationToken.None);
            Require(fetched <= 128, "one catch-up request exceeded 128 versions");
            Require(++calls <= 33, "catch-up failed to progress");
        } while (result.Pending);
        Require(result.Valid && firstFetch == 2 && calls == 33,
            "a new PC could not page genesis to a head beyond 4,096");
    }
    using (var restarted = new LocalData(root))
    {
        var fetched = 0;
        var result = await FriendLink.VerifySharedChainBatchAsync(restarted, device, profile,
            versions[0], head, [], (_, _) =>
            {
                fetched++;
                return Task.FromResult<SharedWorldVersion?>(null);
            }, CancellationToken.None);
        Require(result.Valid && fetched == 0, "protected cursor did not resume at the verified head");
        restarted.DeleteProtected($"shared-chain-{device:N}-{profile:N}.protected");
        fetched = 0;
        result = await FriendLink.VerifySharedChainBatchAsync(restarted, device, profile,
            versions[0], head, [], (number, _) =>
            {
                fetched++;
                return Task.FromResult<SharedWorldVersion?>(versions[checked((int)number - 1)]);
            }, CancellationToken.None);
        Require(result.Pending && fetched == 128,
            "manifest-before-cursor crash replay did not safely recheck the first batch");
    }
    var payload = Path.Combine(vault, head.VersionHash, SharedWorldService.PayloadDirectory);
    Directory.CreateDirectory(payload);
    File.WriteAllBytes(Path.Combine(payload, "world.dat"), [1]);
    File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(head));
    Require(FriendLink.ReadVerifiedReceivedLineage(vault, head, null).LongCount() == 4102,
        "takeover could not prove more than 4,096 signed ancestors");

    var bucket = Path.Combine(vault, "signed-history", (head.Number / 1024).ToString("x16"));
    var orphan = Path.Combine(bucket, head.Number + "-" + head.VersionHash + ".json.new");
    File.WriteAllBytes(orphan, [1]);
    RequireThrows<InvalidDataException>(() => FriendLink.KeepSignedManifest(vault, head),
        "an orphan archive write was ignored");
    File.Delete(orphan);
    FriendLink.KeepSignedManifest(vault, head); // replay after manifest-before-cursor is idempotent
    var unexpected = Path.Combine(bucket, "unexpected.json");
    File.WriteAllBytes(unexpected, [1]);
    RequireThrows<InvalidDataException>(() => FriendLink.SignedHistoryUsage(vault),
        "an unexpected archive entry was accepted by the full audit");
    File.Delete(unexpected);

    var middle = versions[2048];
    var middlePath = Path.Combine(vault, "signed-history", (middle.Number / 1024).ToString("x16"),
        middle.Number + "-" + middle.VersionHash + ".json");
    var original = File.ReadAllBytes(middlePath);
    File.WriteAllText(middlePath, "{tampered");
    RequireThrows<Exception>(() => FriendLink.ReadVerifiedReceivedLineage(vault, head, null).LongCount(),
        "tampered takeover ancestor was accepted");
    File.WriteAllBytes(middlePath, original);
    File.Delete(middlePath);
    RequireThrows<InvalidDataException>(() => FriendLink.ReadVerifiedReceivedLineage(vault, head, null).LongCount(),
        "missing takeover ancestor was accepted");
    Console.WriteLine("PASS 4,102-version catch-up, cursor restart, replay, orphan, tamper, takeover proof");
}
finally
{
    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Fixture root escaped its temporary parent.");
    Directory.Delete(root, true);
}

static void Require(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static void RequireThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception(message);
}
