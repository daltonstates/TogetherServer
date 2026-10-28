using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class PlannedHandoffRequestChecks
{
    private static async Task CompletedMoveSimpleOwnerStartAsync(Fixture fixture, LocalData data, HostManager manager,
        FilesOnlyDriver driver, FriendService friends, string destination, byte[] before)
    {
        HttpClient NoNetwork(string _, IEnumerable<string> __) => throw new InvalidOperationException("Planned Host Start must remain local.");
        using var peers = new WorldPeerService(data, manager, friends, new PairingService(data), NoNetwork);
        manager.SharedHostingStartBlock = (root, hash) => peers.CanonicalStartBlock(root, hash)?.Message;
        var hosting = new SimpleOwnerHostingService(data, manager, peers,
            () => throw new InvalidOperationException("Planned Host Start must not enable a listener."));
        var profile = data.LoadSettings().Profiles.Single(item => item.Id == fixture.Profile.Id);
        var ownerKey = Path.Combine(data.RootPath, "shared-world-signing-key.protected");
        var authorityRoot = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"), "authority");
        var log = Path.Combine(authorityRoot, "records.jsonl");
        var floor = Path.Combine(data.RootPath, $"authority-floor-{profile.Id:N}.protected");
        var binding = Path.Combine(data.RootPath, $"authority-host-{profile.Id:N}.protected");
        var route = Path.Combine(data.RootPath, $"successor-route-{profile.Id:N}.protected");
        var save = Path.Combine(destination, "world.dat");
        Require(profile.SharedSavesEnabled && profile.SeparateCopySourceProfileId is null && profile.SeparateCopyBranchHash is null &&
            new WorldAuthorityStore(data).HasState(profile.Id) && !File.Exists(ownerKey) && driver.ProcessAttempts == 0,
            "the completed planned Host fixture did not have its original ID, foreign owner and absent global owner key");

        foreach (var fault in new[] { "missing-authority", "missing-host-binding", "receipt", "final-source", "route" })
        {
            var touched = fault switch
            {
                "missing-authority" => new[] { floor, log, Path.Combine(authorityRoot, "append.pending") },
                "missing-host-binding" => [binding],
                "receipt" => [log],
                "final-source" => [save],
                _ => [route]
            };
            var saved = touched.Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
            Require(saved.Count > 0, "the planned Host negative lacked its exact synthetic proof: " + fault);
            try
            {
                if (fault == "receipt")
                {
                    var records = File.ReadAllLines(log).Where(line => line.Length > 0)
                        .Select(line => JsonSerializer.Deserialize<WorldAuthorityRecord>(line, Json)!).ToArray();
                    Require(records.Length > 0 && records[^1].SuccessorReceipt is not null,
                        "the completed planned authority lacked its required signed final-copy receipt");
                    records[^1] = records[^1] with
                    {
                        SuccessorReceipt = records[^1].SuccessorReceipt! with
                        { Signature = Convert.ToBase64String(new byte[64]) }
                    };
                    File.WriteAllText(log, string.Join('\n', records.Select(record => JsonSerializer.Serialize(record, Json))) + "\n");
                }
                else foreach (var path in saved.Keys) File.Delete(path);
                var protectedBefore = ProtectedFiles(data);
                var result = await hosting.StartAsync(profile.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
                Require(!result.Ok && driver.ProcessAttempts == 0 && data.LoadRuns().Count == 0 && !File.Exists(ownerKey),
                    $"planned Host {fault} bypassed its common proof gates or created an original owner key: {result.Code}");
                RequireProtectedFiles(data, protectedBefore);
            }
            finally { foreach (var (path, bytes) in saved) File.WriteAllBytes(path, bytes); }
        }

        // HasState also classifies a fenced original source. It never grants
        // permission to restart that source after the signed planned move.
        using var sourceFriends = new FriendService(fixture.Data, NoNetwork);
        using var sourcePeers = new WorldPeerService(fixture.Data, fixture.Manager, sourceFriends, new PairingService(fixture.Data), NoNetwork);
        var sourceHosting = new SimpleOwnerHostingService(fixture.Data, fixture.Manager, sourcePeers,
            () => throw new InvalidOperationException("The fenced source cannot enable a listener."));
        var sourceResult = await sourceHosting.StartAsync(profile.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
        Require(!sourceResult.Ok && sourceResult.Code == "SharedWorldAuthorityBlocked" && fixture.Driver.ProcessAttempts == 0 &&
            fixture.Data.LoadRuns().Count == 0, "authority dispatch revived the fenced original source: " + sourceResult.Code);

        var beforeStart = ProtectedFiles(data);
        var valid = await hosting.StartAsync(profile.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
        Require(valid.Code == "LaunchFailed" && driver.ProcessAttempts == 1 && data.LoadRuns().Count == 0 &&
            !File.Exists(ownerKey) && File.ReadAllBytes(save).SequenceEqual(before),
            $"the completed planned Host card did not reach its refusing fake driver through common Start: {valid.Code}; attempts={driver.ProcessAttempts}");
        RequireProtectedFiles(data, beforeStart);
    }

    private static async Task CompletedMoveChangedRouteStartAsync(Guid profileId, LocalData data, HostManager manager,
        FilesOnlyDriver driver, FriendService friends, int attempts, string destination, byte[] before)
    {
        HttpClient NoNetwork(string _, IEnumerable<string> __) => throw new InvalidOperationException("Planned Host Start must remain local.");
        using var peers = new WorldPeerService(data, manager, friends, new PairingService(data), NoNetwork);
        var hosting = new SimpleOwnerHostingService(data, manager, peers,
            () => throw new InvalidOperationException("Planned Host Start must not change its route."));
        var protectedBefore = ProtectedFiles(data);
        var result = await hosting.StartAsync(profileId, default).WaitAsync(TimeSpan.FromSeconds(5));
        Require(!result.Ok && result.Code == "SuccessorChecksPending" && driver.ProcessAttempts == attempts && data.LoadRuns().Count == 0 &&
            !File.Exists(Path.Combine(data.RootPath, "shared-world-signing-key.protected")) &&
            File.ReadAllBytes(Path.Combine(destination, "world.dat")).SequenceEqual(before),
            "the Host action retained readiness after the planned successor's current certificate changed: " + result.Code);
        RequireProtectedFiles(data, protectedBefore);
    }

    private static Dictionary<string, byte[]> ProtectedFiles(LocalData data) => Directory.EnumerateFiles(data.RootPath, "*.protected*")
        .ToDictionary(path => path, File.ReadAllBytes);

    private static void RequireProtectedFiles(LocalData data, IReadOnlyDictionary<string, byte[]> before)
    {
        var current = ProtectedFiles(data);
        Require(before.Count == current.Count && before.All(item => current.TryGetValue(item.Key, out var bytes) && bytes.SequenceEqual(item.Value)),
            "a planned Host proof check created or changed protected identity/authority records");
    }
}
