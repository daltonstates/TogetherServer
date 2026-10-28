using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Signed synthetic saves, real source selection and fresh protected data only.
// No network, listener, fixture process, game, app or native dialog is used.
internal static class WorldPeerRecoverySelectionChecks
{
    internal static async Task RunAsync(string root)
    {
        using (var complete = new Fixture(root, "complete-gap"))
        {
            var before = complete.Store.Project();
            Require(before.Branches.Count == 2 && before.Branches.All(item => item.HasConflict) &&
                before.Peers.All(item => !item.HasConflict), "the fixture did not represent missed announcement2");
            var selected = await complete.Service.ResolveRecoverySourceAsync(complete.Root.GroupKey, complete.Lane, default);
            selected.Verify(complete.Data, complete.Root);
            Require(selected.Descriptor.Version.VersionHash == complete.Third.VersionHash &&
                FriendLink.ReadReceivedLatest(selected.Vault)?.VersionHash == complete.Third.VersionHash &&
                complete.Store.Project().Branches.Count == 2 &&
                new WorldAuthorityStore(complete.Data).Read(complete.Root.ProfileId).Count == 0,
                "complete1->2->3 ancestry did not select3 without erasing uncertain metadata or changing authority");
            var latest = await complete.Service.ResolveLatestRecoverySourceAsync(complete.Root, default);
            Require(latest.Ok && latest.Source?.Descriptor.Version.VersionHash == complete.Third.VersionHash &&
                !complete.Data.LoadSettings().CompanionListeningEnabled,
                "ordinary latest-save preparation required a history/default decision or enabled incoming connections");
        }
        using (var missing = new Fixture(root, "missing-parent", includeSecond: false))
        {
            await RefusesAsync(missing, "a complete payload with missing signed parent2 resolved the lane");
            var latest = await missing.Service.ResolveLatestRecoverySourceAsync(missing.Root, default);
            Require(!latest.Ok && latest.Code == "SharedSaveHistoryNeedsReview",
                "an incomplete newest known history was treated as an unavailable pristine world");
            Require(FriendLink.ReadReceivedLatest(missing.Vault)?.VersionHash == missing.Third.VersionHash,
                "refused incomplete ancestry erased retained payload evidence");
        }
        using (var fork = new Fixture(root, "true-fork"))
        {
            var otherSecond = fork.Child(fork.First, "divergent second");
            var otherThird = fork.Child(otherSecond, "divergent third");
            var retained = Path.Combine(fork.Data.RootPath, "received-shared-histories", fork.Root.GroupKey, otherThird.VersionHash);
            fork.Write(retained, fork.First, "initial");
            fork.Write(retained, otherSecond, "divergent second");
            fork.Write(retained, otherThird, "divergent third");
            _ = fork.Publish(otherThird);
            await RefusesAsync(fork, "a same-number divergent signed3 was selected instead of preserving the fork");
            Require(FriendLink.ReadReceivedLatest(retained)?.VersionHash == otherThird.VersionHash,
                "reviewing a true fork erased its complete copy");
        }
        using (var crossed = new Fixture(root, "cross-root", foreignThird: true))
            await RefusesAsync(crossed, "a save signed by the same key but for another root/group was selected");
        using (var equivocated = new Fixture(root, "signer-equivocation"))
        {
            var changed = SharedWorldPeerTrust.Sign(equivocated.Last with
            {
                AnnouncedUtc = equivocated.Last.AnnouncedUtc.AddSeconds(1),
                AnnouncementHash = "",
                Signature = ""
            }, equivocated.Owner);
            Require(equivocated.Store.Import(changed).Changed && equivocated.Store.Project().Peers.Single().HasConflict,
                "the fixture did not retain actual same-revision signer equivocation");
            await RefusesAsync(equivocated, "valid payload ancestry silently repaired actual signer equivocation");
        }
        using (var corrupt = new Fixture(root, "damaged-payload"))
        {
            File.WriteAllText(Path.Combine(corrupt.Vault, corrupt.Third.VersionHash,
                SharedWorldService.PayloadDirectory, "world.dat"), "changed");
            await RefusesAsync(corrupt, "signed metadata ancestry selected a candidate with damaged payload bytes");
        }
        await CheckLatestGraphAsync(root);
        await CheckOperationInventoryAsync(root);
        await CheckInterrupted315FileCopyAsync(root);
    }

    private static async Task CheckOperationInventoryAsync(string root)
    {
        using var fixture = new Fixture(root, "operation-inventory");
        var head = fixture.Third;
        SharedWorldVersion? parent = null;
        for (var number = 4; number <= 8; number++)
        {
            var payload = "inventory " + number;
            head = fixture.Child(head, payload);
            fixture.Write(fixture.Vault, head, payload);
            _ = fixture.Publish(head);
            if (number == 4) parent = head;
        }
        var inventories = 0;
        fixture.Service.LocalSaveInventoryForChecks = () => inventories++;
        var selected = await SelectLocalAsync(fixture);
        Require(selected?.Version.VersionHash == head.VersionHash && inventories == 1,
            "an eight-version signed ancestry rebuilt the complete local inventory for its parents");

        head = fixture.Child(head, "inventory 9");
        fixture.Write(fixture.Vault, head, "inventory 9");
        _ = fixture.Publish(head);
        selected = await SelectLocalAsync(fixture);
        Require(selected?.Version.VersionHash == head.VersionHash && inventories == 2,
            "the next selection reused an old inventory instead of discovering the new exact local head");

        var requiredParent = parent ?? throw new InvalidOperationException("the synthetic parent was not created");
        var archive = Directory.EnumerateFiles(Path.Combine(fixture.Vault, "signed-history"),
            "*-" + requiredParent.VersionHash + ".json", SearchOption.AllDirectories).Single();
        var manifest = Path.Combine(fixture.Vault, requiredParent.VersionHash, "version.json");
        var archivedBytes = File.ReadAllBytes(archive);
        var manifestBytes = File.ReadAllBytes(manifest);
        var pointer = File.ReadAllBytes(Path.Combine(fixture.Vault, "latest.json"));
        foreach (var change in new[] { "missing", "signature", "parent-number", "parent-root" })
        {
            var before = inventories;
            var changed = false;
            Exception? mutationError = null;
            fixture.Service.LocalSaveInventoryForChecks = () =>
            {
                inventories++;
                if (changed || mutationError is not null) return;
                try
                {
                    if (change == "missing")
                    {
                        File.Delete(archive);
                        File.Delete(manifest);
                    }
                    else
                    {
                        var altered = change switch
                        {
                            "signature" => requiredParent with { Signature = Convert.ToBase64String(new byte[64]) },
                            "parent-number" => SharedWorldService.SignVersion(requiredParent with { Number = requiredParent.Number + 1 }, fixture.Owner),
                            _ => SharedWorldService.SignVersion(requiredParent with { GroupId = Guid.NewGuid() }, fixture.Owner)
                        };
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(altered, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        File.WriteAllBytes(archive, bytes);
                        File.WriteAllBytes(manifest, bytes);
                    }
                    changed = true;
                }
                catch (Exception error) { mutationError = error; }
            };
            var refused = false;
            try
            {
                try { await SelectLocalAsync(fixture); }
                catch (InvalidDataException) { refused = true; }
                Require(changed && mutationError is null && refused && inventories == before + 1 &&
                    File.ReadAllBytes(Path.Combine(fixture.Vault, "latest.json")).SequenceEqual(pointer),
                    "operation inventory reuse accepted changed required parent metadata or changed the source: " + change);
                RequireNoHostingMutation(fixture);
                Require(!Directory.Exists(Path.Combine(fixture.Data.RootPath, "world-peer-recovery-sources")),
                    "a refused ancestry created a retained hosting source");
            }
            finally
            {
                File.WriteAllBytes(archive, archivedBytes);
                File.WriteAllBytes(manifest, manifestBytes);
            }
        }
        fixture.Service.LocalSaveInventoryForChecks = () => inventories++;
        var beforeRestored = inventories;
        selected = await SelectLocalAsync(fixture);
        Require(selected?.Version.VersionHash == head.VersionHash && inventories == beforeRestored + 1,
            "a new operation retained stale failed history after the signed parent was restored");
    }

    private static async Task<WorldPeerSaveDescriptor?> SelectLocalAsync(Fixture fixture)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var group = typeof(WorldPeerService).GetMethod("Find", flags)!.Invoke(fixture.Service, [fixture.Root.GroupKey])!;
        var task = (Task)typeof(WorldPeerService).GetMethod("LatestLocalSaveAsync", flags)!.Invoke(fixture.Service,
            [group, CancellationToken.None, null, null])!;
        await task;
        var selected = task.GetType().GetProperty("Result")!.GetValue(task);
        return selected is null ? null : (WorldPeerSaveDescriptor)selected.GetType().GetProperty("Descriptor")!.GetValue(selected)!;
    }

    private static async Task CheckInterrupted315FileCopyAsync(string root)
    {
        using (var interrupted = new Fixture(root, "interrupted-315-file-copy", fileCount: 315))
        {
            var receivedPointer = File.ReadAllBytes(Path.Combine(interrupted.Vault, "latest.json"));
            var retained = await InterruptAt75FilesAsync(interrupted);
            var partial = Path.Combine(retained, ".partial-" + interrupted.Third.VersionHash, SharedWorldService.PayloadDirectory);
            var cached = interrupted.Third.Files.Take(75).Select(file =>
                (File: file, Bytes: File.ReadAllBytes(Path.Combine(partial, file.Path)))).ToArray();
            Require(cached.All(item => item.Bytes.LongLength == item.File.Length),
                "cancelling at 75 did not keep the completed disposable file copies for retry");
            RequireSourceUnchanged(interrupted, receivedPointer);
            RequireNoHostingMutation(interrupted, retained);

            Require((await interrupted.Service.SetReceivingAsync(interrupted.Root.GroupKey, false, default)).Ok,
                "the synthetic receiver could not withdraw current consent before retry");
            var deniedProgress = new List<WorldPeerSourceProgress>();
            var denied = await interrupted.Service.ResolveLatestRecoverySourceAsync(interrupted.Root, default, deniedProgress.Add);
            Require(!denied.Ok && denied.Code == "HostingPermissionRequired" &&
                deniedProgress.All(item => item.Phase != "PreparingCopy") &&
                cached.All(item => File.ReadAllBytes(Path.Combine(partial, item.File.Path)).SequenceEqual(item.Bytes)),
                "retry bypassed current consent or changed the already retained 75 files while denied");
            RequireSourceUnchanged(interrupted, receivedPointer);
            RequireNoHostingMutation(interrupted, retained);

            Require((await interrupted.Service.SetReceivingAsync(interrupted.Root.GroupKey, true, default)).Ok,
                "the synthetic receiver could not restore current consent for retry");
            var progress = new List<WorldPeerSourceProgress>();
            var completed = await interrupted.Service.ResolveLatestRecoverySourceAsync(interrupted.Root, default, progress.Add);
            var total = interrupted.Third.Files.Sum(file => file.Length);
            Require(completed.Ok && completed.Source is not null && completed.Source.Vault == retained &&
                completed.Source.Descriptor.Version.VersionHash == interrupted.Third.VersionHash &&
                progress.Any(item => item.Phase == "PreparingCopy" && item.CompletedFiles == 315 &&
                    item.TotalFiles == 315 && item.CompletedBytes == total && item.TotalBytes == total) &&
                progress.Any(item => item.Phase == "Verifying" && item.CompletedFiles == 315 &&
                    item.TotalFiles == 315 && item.CompletedBytes == total && item.TotalBytes == total),
                "retry did not prepare and fully verify the exact 315-file latest source with byte/file progress");
            completed.Source!.Verify(interrupted.Data, interrupted.Root);
            Require(FriendLink.ReadReceivedLatest(retained)?.VersionHash == interrupted.Third.VersionHash &&
                !Directory.Exists(Path.GetDirectoryName(partial)) &&
                cached.All(item => File.ReadAllBytes(Path.Combine(retained, interrupted.Third.VersionHash,
                    SharedWorldService.PayloadDirectory, item.File.Path)).SequenceEqual(item.Bytes)),
                "retry did not reuse the retained files in one complete verified source");
            RequireSourceUnchanged(interrupted, receivedPointer);
            RequireNoHostingMutation(interrupted);
        }
        using (var damaged = new Fixture(root, "damaged-cached-315-file-copy", fileCount: 315))
        {
            var receivedPointer = File.ReadAllBytes(Path.Combine(damaged.Vault, "latest.json"));
            var retained = await InterruptAt75FilesAsync(damaged);
            var cachedFile = Path.Combine(retained, ".partial-" + damaged.Third.VersionHash,
                SharedWorldService.PayloadDirectory, damaged.Third.Files[0].Path);
            var changed = File.ReadAllBytes(cachedFile); changed[0] ^= 0x7F;
            File.WriteAllBytes(cachedFile, changed); // Same length must still fail the final signed hash.
            var progress = new List<WorldPeerSourceProgress>();
            var rejected = await damaged.Service.ResolveLatestRecoverySourceAsync(damaged.Root, default, progress.Add);
            Require(!rejected.Ok && rejected.Code == "SharedSaveHistoryNeedsReview" && rejected.Source is null &&
                progress.Any(item => item.Phase == "PreparingCopy" && item.CompletedFiles == 315) &&
                progress.Any(item => item.Phase == "Verifying"),
                "a same-size damaged cached file became a complete source instead of failing final verification");
            RequireSourceUnchanged(damaged, receivedPointer);
            RequireNoHostingMutation(damaged, retained);
        }
    }

    private static async Task<string> InterruptAt75FilesAsync(Fixture fixture)
    {
        using var stop = new CancellationTokenSource();
        WorldPeerSourceProgress? cutoff = null;
        var cancelled = false;
        try
        {
            await fixture.Service.ResolveLatestRecoverySourceAsync(fixture.Root, stop.Token, item =>
            {
                if (item.Phase != "PreparingCopy" || item.CompletedFiles != 75 || cutoff is not null) return;
                cutoff = item;
                stop.Cancel();
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { cancelled = true; }
        Require(cancelled && cutoff is { CompletedFiles: 75, TotalFiles: 315 } &&
            cutoff.CompletedBytes > 0 && cutoff.CompletedBytes < cutoff.TotalBytes,
            "actual latest-source retention did not cancel deterministically at 75 of 315 files");
        return WorldPeerRecoverySource.StorageRoot(fixture.Data, fixture.Root, fixture.Lane, fixture.Third.VersionHash);
    }

    private static void RequireSourceUnchanged(Fixture fixture, byte[] pointer)
    {
        Require(File.ReadAllBytes(Path.Combine(fixture.Vault, "latest.json")).SequenceEqual(pointer) &&
            FriendLink.ReadReceivedLatest(fixture.Vault)?.VersionHash == fixture.Third.VersionHash &&
            fixture.Third.Files.All(file => File.ReadAllText(Path.Combine(fixture.Vault,
                fixture.Third.VersionHash, SharedWorldService.PayloadDirectory, file.Path)) == "third"),
            "preparing or cancelling the retained source changed its received pointer or original payload bytes");
    }

    private static void RequireNoHostingMutation(Fixture fixture, string? unpromoted = null)
    {
        Require((unpromoted is null || !File.Exists(Path.Combine(unpromoted, "latest.json"))) &&
            fixture.Data.LoadSettings().Profiles.Count == 0 && !fixture.Data.LoadSettings().CompanionListeningEnabled &&
            fixture.Data.LoadRuns().Count == 0 &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.Root.ProfileId).Count == 0 &&
            new WorldAuthorityStore(fixture.Data).Read(fixture.Root.ProfileId).Count == 0 &&
            !fixture.Data.HasProtected(WorldAuthorityStore.HostingKeyName(fixture.Root.ProfileId)) &&
            !Directory.EnumerateFiles(fixture.Data.RootPath, "separate-host-*.protected").Any(),
            "source retention promoted an unfinished pointer, prepared a hosting branch/profile or recorded a Start");
    }

    private static async Task CheckLatestGraphAsync(string root)
    {
        using var fixture = new Fixture(root, "latest-graph");
        var second = fixture.Child(fixture.First, "graph second");
        var third = fixture.Child(second, "graph third") with { };
        // All three signed save times are deliberately identical. Only parent
        // hashes can establish which continuation includes every older head.
        var versions = new[] { fixture.First, second, third }.ToDictionary(item => item.VersionHash);
        Task<SharedWorldVersion?> Read(string hash, long number, CancellationToken _) =>
            Task.FromResult(versions.TryGetValue(hash, out var value) && value.Number == number ? value : null);
        var selected = await WorldPeerLatestSelection.SelectAsync(fixture.Root,
            [new(fixture.First), new(third), new(second)], [fixture.First.VersionHash, second.VersionHash],
            (_, _) => Task.CompletedTask, Read, default);
        Require(selected == 1, "latest selection depended on candidate order or timestamp instead of ancestry");
        var fork = fixture.Child(fixture.First, "competing graph second");
        versions.Add(fork.VersionHash, fork);
        async Task Refuses(IReadOnlyList<WorldPeerSaveDescriptor> copies, IReadOnlyList<string> claims,
            Func<int, CancellationToken, Task>? verify = null)
        {
            try
            {
                await WorldPeerLatestSelection.SelectAsync(fixture.Root, copies, claims,
                    verify ?? ((_, _) => Task.CompletedTask), Read, default);
            }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("an incomplete or divergent latest history selected an older copy");
        }
        await Refuses([new(third), new(fork)], []);
        await Refuses([new(second)], [third.VersionHash]);
        await Refuses([new(fixture.First), new(third)], [], (index, _) => index == 1
            ? Task.FromException(new InvalidDataException("synthetic latest payload changed")) : Task.CompletedTask);
        versions.Remove(second.VersionHash);
        await Refuses([new(fixture.First), new(third)], []);
    }

    private static async Task RefusesAsync(Fixture fixture, string message)
    {
        try
        {
            await fixture.Service.ResolveRecoverySourceAsync(fixture.Root.GroupKey, fixture.Lane, default);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or JsonException)
        {
            Require(!Directory.Exists(Path.Combine(fixture.Data.RootPath, "world-peer-recovery-sources")),
                "an ambiguous lane was normalized or promoted before proof resolution");
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        internal LocalData Data { get; }
        internal ECDsa Owner { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal SharedWorldPeerRoot Root { get; }
        internal string Lane { get; }
        internal string Vault { get; }
        internal SharedWorldVersion First { get; }
        internal SharedWorldVersion Third { get; }
        internal SharedWorldPeerStore Store { get; }
        internal SharedWorldPeerAnnouncement Last { get; }
        internal WorldPeerService Service { get; }
        private readonly FriendService friends;

        internal Fixture(string parent, string name, bool includeSecond = true, bool foreignThird = false, int fileCount = 1)
        {
            Data = new LocalData(Path.Combine(parent, "recovery-selection-" + name + "-" + Guid.NewGuid().ToString("N")));
            Root = new(Convert.ToBase64String(Owner.ExportSubjectPublicKeyInfo()), Guid.NewGuid(), Guid.NewGuid());
            Lane = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic lane " + Root.GroupKey)));
            var draft = new SharedWorldRoster(3, Root.GroupId, Root.ProfileId, 1, 1, true, Root.OwnerPublicKey,
                [], "", SignerDeviceId: Guid.Empty, SignerPublicKey: Root.OwnerPublicKey);
            var roster = draft with
            {
                Signature = Convert.ToBase64String(Owner.SignData(
                SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
            };
            Data.SaveSettings(new HostSettings { Profiles = [] });
            Data.SaveProtected("shared-world-signing-key.protected", Owner.ExportPkcs8PrivateKey());
            new SharedWorldRosterChainStore(Data).Append(roster, Root.OwnerPublicKey);
            Data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
            {
                Schema = 1,
                Groups = new[] { new { Root, Roster = roster, DeviceId = Guid.Empty,
                    Name = "Synthetic selection", ReceiveConsented = true, SharingEnabled = false,
                    Bootstrap = (SharedWorldPeerRoute?)null } }
            }, Json));
            First = SharedWorldService.SignVersion(new(4, Root.GroupId, 1, null, Root.ProfileId,
                GameKinds.Fixture, "SyntheticSelection", DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
                SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(2456, false, "Unknown", [], []),
                [Fact("initial")], "", "", ""), Owner);
            var second = Child(First, "second");
            Third = Child(second, "third", fileCount);
            if (foreignThird) Third = SharedWorldService.SignVersion(Third with { GroupId = Guid.NewGuid() }, Owner);
            Vault = Path.Combine(Data.RootPath, "received-shared-worlds", Guid.Empty.ToString("N"), Root.ProfileId.ToString("N"));
            Write(Vault, First, "initial");
            if (includeSecond) Write(Vault, second, "second");
            Write(Vault, Third, "third");
            Store = new(Data, Root, new SharedWorldPeerTrust(Root,
                (id, key) => SharedWorldPeerTrust.AccessFromRoster(Root, roster, id, key)));
            _ = Publish(First);
            Last = Publish(Third);
            friends = new FriendService(Data, (_, _) => throw new InvalidOperationException("No network is allowed in selection checks."));
            Service = new(Data, new HostManager(Data), friends, new PairingService(Data),
                (_, _) => throw new InvalidOperationException("No network is allowed in selection checks."));
        }

        internal SharedWorldVersion Child(SharedWorldVersion parent, string payload, int fileCount = 1) => SharedWorldService.SignVersion(parent with
        {
            Number = parent.Number + 1,
            ParentHash = parent.VersionHash,
            BackupId = Guid.NewGuid(),
            Files = fileCount == 1 ? [Fact(payload)] : Enumerable.Range(0, fileCount)
                .Select(index => Fact(payload, $"world-{index:D3}.dat")).ToArray()
        }, Owner);
        internal SharedWorldPeerAnnouncement Publish(SharedWorldVersion version) => Store.Publish(Guid.Empty, Owner,
            [], [], [new(Lane, First.VersionHash, version.VersionHash, version.ParentHash, version.Number)]);
        internal void Write(string root, SharedWorldVersion version, string payload)
        {
            var directory = Path.Combine(root, version.VersionHash);
            Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
            foreach (var file in version.Files)
            {
                var path = SharedWorldService.SafeChild(Path.Combine(directory, SharedWorldService.PayloadDirectory), file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, payload);
            }
            File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            FriendLink.KeepSignedManifest(root, version);
            File.WriteAllBytes(Path.Combine(root, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        }
        private static SharedWorldFile Fact(string payload, string path = "world.dat")
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            return new(path, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        public void Dispose() { Service.Dispose(); friends.Dispose(); Data.Dispose(); Owner.Dispose(); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
