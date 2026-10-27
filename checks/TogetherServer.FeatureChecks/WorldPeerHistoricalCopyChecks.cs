using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Generated signed branch publications and the existing in-memory HTTP handler.
// No sockets, process, app, game or production LocalData are used.
internal static class WorldPeerHistoricalCopyChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task RunAsync(string parent)
    {
        foreach (var origin in new[] { "Separate", "Received", "ReceivedBranch", "Published" }) await ReuseAsync(parent, origin);
        await UnpointedReceiveAsync(parent);
        foreach (var mutation in new[] { "Payload", "Manifest", "MissingParent", "Ambiguous", "Divergent" })
            await DamagedOrAmbiguousAsync(parent, mutation);
        foreach (var mutation in new[] { "Head", "Registry", "Authority", "Consent", "Receive", "Roster", "Revoked", "Expired", "Key", "Sharing" })
            await ChangedScopeAsync(parent, mutation);
        await ChangedAncestorBeforeCommitAsync(parent);
        await CanceledAsync(parent);
    }

    private static async Task ReuseAsync(string parent, string origin)
    {
        using var history = new History(parent, origin);
        var fixture = history.Fixture;
        var before = fixture.Pointers();
        var payload = File.ReadAllBytes(history.PayloadPath);
        var hashes = 0;
        var workBefore = fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "PayloadVerification").Count;
        fixture.TargetService.ReceiptPayloadHashingForChecks = () => hashes++;
        // Unrelated malformed archive metadata must not be audited by this exact
        // source lookup. Its selected strict parent chain remains fully checked.
        if (origin != "Published")
            File.WriteAllText(Path.Combine(history.Vault, "signed-history", "unrelated-invalid.json"), "{unrelated");
        await fixture.PullAsync();
        var path = history.Sidecars().Single();
        var receiptBytes = File.ReadAllBytes(path);
        var receipt = JsonSerializer.Deserialize<SharedWorldReceipt>(receiptBytes, Json)!;
        Require(history.ValidReceipt(receipt) && hashes == 1 && fixture.Pointers() == before &&
            fixture.RemoteHandler.Operations.Contains(WorldPeerOperations.Receipt) &&
            !fixture.RemoteHandler.Operations.Contains(WorldPeerOperations.Chunk) && history.NoNewCopy() &&
            fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "PayloadVerification").Count == workBefore + 1,
            "An exact historical save8 with head9 was transferred, copied, downgraded, not counted or not exactly acknowledged: " + origin);
        await fixture.PullAsync();
        Require(hashes == 1 && history.Sidecars().Length == 1 && File.ReadAllBytes(path).SequenceEqual(receiptBytes) &&
            fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "PayloadVerification").Count == workBefore + 1,
            "A confirmed exact historical receipt poll rehashed payload or rewrote its immutable receipt.");
        var seeded = await history.SeedAsync();
        Require(seeded is { Code: "SeedKnown", Receipt: not null } && seeded.Receipt == receipt && hashes == 2 &&
            fixture.SourceHandler.Operations.Count(operation => operation == WorldPeerOperations.Seed) == 1 && history.NoNewCopy(),
            "An initial historical seed offer requested payload instead of revalidating the exact local save8.");
        fixture.ReopenTarget();
        var reopened = await history.SeedAsync();
        Require(reopened is { Code: "SeedKnown", Receipt: not null } && reopened.Receipt == receipt &&
            history.Sidecars().Length == 1 && File.ReadAllBytes(path).SequenceEqual(receiptBytes) &&
            File.ReadAllBytes(history.PayloadPath).SequenceEqual(payload) && fixture.Pointers() == before && history.NoNewCopy() &&
            fixture.A.LoadRuns().Count == 0 && fixture.B.LoadRuns().Count == 0,
            "Reopening lost historical provenance, regenerated receipt identity, changed head9 or created a run/copy.");
    }

    private static async Task DamagedOrAmbiguousAsync(string parent, string mutation)
    {
        using var history = new History(parent);
        var fixture = history.Fixture;
        switch (mutation)
        {
            case "Payload":
                var modified = File.GetLastWriteTimeUtc(history.PayloadPath);
                var bytes = File.ReadAllBytes(history.PayloadPath); bytes[0] ^= 1;
                File.WriteAllBytes(history.PayloadPath, bytes);
                File.SetLastWriteTimeUtc(history.PayloadPath, modified);
                break;
            case "Manifest": File.WriteAllText(Path.Combine(history.DirectoryPath, "version.json"), "{damaged exact manifest"); break;
            case "MissingParent":
                foreach (var path in Directory.EnumerateFiles(Path.Combine(history.Vault, "signed-history"),
                    "*" + history.Versions[7].VersionHash + ".json", SearchOption.AllDirectories)) File.Delete(path);
                File.Delete(Path.Combine(history.Vault, history.Versions[7].VersionHash, "version.json"));
                break;
            case "Ambiguous":
                history.WriteCopy(new SharedWorldSeparateCopyStore(fixture.Target).RetainedRoot(history.Branch), history.Descriptor.Version,
                    updateHead: false);
                break;
            case "Divergent":
                using (var key = history.HostingKey())
                {
                    var sibling = SharedWorldService.SignVersion(history.Descriptor.Version with { BackupId = Guid.NewGuid() }, key);
                    history.WriteCopy(history.Vault, sibling, updateHead: false);
                    history.Descriptor = new(sibling, history.Branch);
                }
                break;
        }
        var before = fixture.Pointers();
        var payload = File.ReadAllBytes(history.PayloadPath);
        await RejectAsync(fixture.PullAsync, "A damaged/ambiguous historical source fell through to pull: " + mutation);
        await RejectAsync(async () => { await history.SeedAsync(); },
            "A damaged/ambiguous historical source returned a seed receipt or requested bytes: " + mutation);
        Require(history.Sidecars().Length == 0 && history.NoNewCopy() && fixture.Pointers() == before &&
            File.ReadAllBytes(history.PayloadPath).SequenceEqual(payload) &&
            !fixture.RemoteHandler.Operations.Contains(WorldPeerOperations.Chunk),
            "Historical refusal changed a source/pointer, made a duplicate or requested payload: " + mutation);
    }

    private static async Task UnpointedReceiveAsync(string parent)
    {
        foreach (var origin in new[] { "Received", "ReceivedBranch" })
            foreach (var invalidPointer in new[] { false, true })
            {
                using var history = new History(parent, origin);
                var fixture = history.Fixture;
                var payload = File.ReadAllBytes(history.PayloadPath);
                var pointer = Path.Combine(history.Vault, "latest.json");
                if (invalidPointer) File.WriteAllText(pointer, "{damaged present receive pointer");
                else
                {
                    File.Delete(pointer);
                    if (origin == "ReceivedBranch") File.Delete(Path.Combine(history.Vault, "branch.json"));
                }
                var before = fixture.Pointers();
                var verifications = fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "PayloadVerification").Count;
                if (invalidPointer)
                {
                    await RejectAsync(async () => { await history.SeedAsync(); },
                        "An invalid present receive pointer was treated as an absent historical source: " + origin);
                    Require(fixture.Pointers() == before, "A damaged receive pointer was replaced during historical lookup.");
                }
                else
                {
                    var completed = await history.SeedAsync();
                    Require(completed is { Code: "SeedComplete", Receipt: not null } && history.ValidReceipt(completed.Receipt) &&
                        history.Head().VersionHash == history.Descriptor.Version.VersionHash &&
                        fixture.TargetService.WorkSnapshot().Work.Single(item => item.Stage == "PayloadVerification").Count == verifications + 1 &&
                        Directory.Exists(Path.Combine(history.Vault, history.Versions[9].VersionHash)),
                        "An unpointed exact destination bypassed full completion, lost its newer retained payload or requested bytes: " + origin);
                }
                Require(history.Sidecars().Length == 0 && history.NoNewCopy() &&
                    File.ReadAllBytes(history.PayloadPath).SequenceEqual(payload) &&
                    fixture.SourceHandler.Operations.Count(operation => operation == WorldPeerOperations.Seed) == 1 &&
                    !fixture.SourceHandler.Operations.Contains(WorldPeerOperations.Chunk),
                    "Orphan completion aliased a historical receipt or transferred/changed existing payload: " + origin);
            }
    }

    private static async Task ChangedScopeAsync(string parent, string mutation)
    {
        // Fixture.RevokeSender changes the owner's current pairing state. Make
        // that owner the receiver so this is a known incoming-sender revocation,
        // rather than pretending an unpublished remote edit reached a member.
        using var history = new History(parent, mutation == "Revoked" ? "Published" : "Separate");
        var fixture = history.Fixture;
        var changed = false;
        Exception? mutationFailure = null;
        fixture.TargetService.ReceiptPayloadHashingForChecks = () =>
        {
            fixture.TargetService.ReceiptPayloadHashingForChecks = null;
            try
            {
                switch (mutation)
                {
                    case "Head": history.Append(); break;
                    case "Registry":
                        File.Delete(Path.Combine(fixture.Target.RootPath, $"shared-world-separate-{fixture.Root.ProfileId:N}.protected"));
                        break;
                    case "Authority":
                        fixture.Target.SaveProtected($"authority-floor-{fixture.Root.ProfileId:N}.protected", Encoding.UTF8.GetBytes("{damaged synthetic floor"));
                        break;
                    case "Consent":
                        Require(fixture.TargetService.SetReceivingAsync(fixture.Root.GroupKey, false, default).GetAwaiter().GetResult().Ok,
                            "The generated historical-copy consent change failed.");
                        break;
                    case "Receive": fixture.AdvanceRoster(receive: false); break;
                    case "Roster": fixture.AdvanceRoster(receive: true); break;
                    case "Revoked": fixture.RevokeSender(); break;
                    case "Expired": history.ExpireReceiver(); break;
                    case "Key":
                        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                            fixture.Target.SaveProtected(Path.GetFileName(fixture.KeyPath), key.ExportPkcs8PrivateKey());
                        break;
                    case "Sharing": fixture.SaveMemberships(sharing: false, targetOnly: true); break;
                }
                changed = true;
            }
            catch (Exception error) { mutationFailure = error; }
        };
        await RejectAsync(async () => { await history.SeedAsync(); },
            "A historical receipt survived a changed current source/scope: " + mutation);
        if (mutationFailure is not null)
            throw new InvalidOperationException("The synthetic historical scope mutation failed: " + mutation, mutationFailure);
        Require(changed && history.Sidecars().Length == 0 && history.NoNewCopy() &&
            history.Head().VersionHash == history.Versions[mutation == "Head" ? 10 : 9].VersionHash &&
            !fixture.SourceHandler.Operations.Contains(WorldPeerOperations.Chunk),
            "Historical source refusal lost its mutation, changed a head or transferred payload: " + mutation);
    }

    private static async Task CanceledAsync(string parent)
    {
        foreach (var duringHash in new[] { false, true })
        {
            using var history = new History(parent);
            using var canceled = new CancellationTokenSource();
            var changed = false;
            var before = history.Fixture.Pointers();
            if (duringHash) history.Fixture.TargetService.ReceiptPayloadHashingForChecks = () => { canceled.Cancel(); changed = true; };
            else { canceled.Cancel(); changed = true; }
            await RejectAsync(() => history.ReuseAsync(canceled.Token), "Canceled historical reuse created an exact receipt.");
            Require(changed && history.Sidecars().Length == 0 && history.Fixture.Pointers() == before && history.NoNewCopy(),
                "Canceled historical reuse changed copy state or lost its cancellation witness.");
        }
    }

    private static async Task ChangedAncestorBeforeCommitAsync(string parent)
    {
        foreach (var remove in new[] { false, true })
        {
            using var history = new History(parent);
            var fixture = history.Fixture;
            var before = fixture.Pointers();
            var changed = false;
            var held = false;
            fixture.TargetService.HistoricalCopyCommittingForChecks = () =>
            {
                fixture.TargetService.HistoricalCopyCommittingForChecks = null;
                try { using var writer = new FileStream(history.PayloadPath, FileMode.Open, FileAccess.Write, FileShare.Read); }
                catch (IOException) { held = true; }
                var archive = Directory.EnumerateFiles(Path.Combine(history.Vault, "signed-history"),
                    "*" + history.Versions[7].VersionHash + ".json", SearchOption.AllDirectories).Single();
                if (remove)
                {
                    File.Delete(archive);
                    File.Delete(Path.Combine(history.Vault, history.Versions[7].VersionHash, "version.json"));
                }
                else File.WriteAllText(archive, "{damaged generated parent after full verification");
                changed = true;
            };
            await RejectAsync(async () => { await history.SeedAsync(); },
                "A historical receipt committed after its verified parent7 changed at the final boundary.");
            Require(changed && held && history.Sidecars().Length == 0 && fixture.Pointers() == before &&
                history.NoNewCopy() && !fixture.SourceHandler.Operations.Contains(WorldPeerOperations.Chunk),
                "The final historical ancestry refusal lost its held-payload/mutation witness or changed copy state.");
        }
    }

    private sealed class History : IDisposable
    {
        internal WorldPeerExistingCopyChecks.Fixture Fixture { get; }
        private readonly string origin;
        internal Dictionary<int, SharedWorldVersion> Versions { get; } = [];
        internal WorldSeparateCopyBranch Branch => Fixture.Descriptor.Branch!;
        internal string Vault => origin switch
        {
            "Received" => Path.Combine(Fixture.Target.RootPath, "received-shared-worlds", Guid.Empty.ToString("N"), Fixture.Root.ProfileId.ToString("N")),
            "ReceivedBranch" => SharedWorldBranchVault.Root(Fixture.Target, Branch),
            "Published" => Path.Combine(Fixture.Target.RootPath, "shared-worlds", Fixture.Root.ProfileId.ToString("N"), Fixture.Root.GroupId.ToString("N")),
            _ => new SharedWorldSeparateCopyStore(Fixture.Target).PublishedRoot(Branch)
        };
        internal WorldPeerSaveDescriptor Descriptor
        {
            get => Fixture.RemoteHandler.Catalog!.Saves.Single();
            set => Fixture.RemoteHandler.Catalog = new(Fixture.Root, [value]);
        }
        internal string DirectoryPath => Path.Combine(Vault, origin == "Published" ?
            Descriptor.Version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : Descriptor.Version.VersionHash);
        internal string PayloadPath => Path.Combine(DirectoryPath, SharedWorldService.PayloadDirectory, "world.dat");
        private readonly byte[] payload;

        internal History(string parent, string origin = "Separate")
        {
            this.origin = origin;
            Fixture = new(parent, origin is "Received" or "Published" ? "Original" : "Branch");
            payload = File.ReadAllBytes(Fixture.PayloadPath);
            Versions.Add(2, Fixture.Descriptor.Version);
            if (origin is "Received" or "ReceivedBranch")
            {
                var first = origin == "Received" ? JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(Path.Combine(
                    Fixture.A.RootPath, "shared-worlds", Fixture.Root.ProfileId.ToString("N"), Fixture.Root.GroupId.ToString("N"), "1", "version.json")), Json)! :
                    Branch.Offer.Version;
                WriteCopy(Vault, first, updateHead: true);
                WriteCopy(Vault, Versions[2], updateHead: true);
                if (origin == "ReceivedBranch") File.WriteAllBytes(Path.Combine(Vault, "branch.json"), JsonSerializer.SerializeToUtf8Bytes(Branch, Json));
            }
            for (var number = 3; number <= 9; number++) Append();
            Descriptor = new(Versions[8], Fixture.Descriptor.Branch);
        }

        internal ECDsa HostingKey()
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Fixture.Target.LoadProtected(origin is "Received" or "Published" ?
                "shared-world-signing-key.protected" : WorldAuthorityStore.HostingKeyName(Fixture.Root.ProfileId))!, out _);
            return key;
        }

        internal void Append()
        {
            var current = Versions.Values.MaxBy(version => version.Number)!;
            using var key = HostingKey();
            var next = SharedWorldService.SignVersion(current with
            { Number = current.Number + 1, ParentHash = current.VersionHash, BackupId = Guid.NewGuid() }, key);
            Versions.Add(checked((int)next.Number), next);
            WriteCopy(Vault, next, updateHead: true);
        }

        internal void WriteCopy(string vault, SharedWorldVersion version, bool updateHead)
        {
            var directory = Path.Combine(vault, origin == "Published" ?
                version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : version.VersionHash);
            Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
            File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), payload);
            File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            if (origin != "Published") FriendLink.KeepSignedManifest(vault, version);
            if (updateHead) File.WriteAllBytes(Path.Combine(origin == "Published" ? Path.GetDirectoryName(vault)! : vault, "latest.json"),
                JsonSerializer.SerializeToUtf8Bytes(version, Json));
        }

        internal void ExpireReceiver()
        {
            var field = typeof(WorldPeerExistingCopyChecks.Fixture).GetField("roster", Private)!;
            var roster = (SharedWorldRoster)field.GetValue(Fixture)!;
            var draft = roster with
            {
                Epoch = roster.Epoch + 1,
                Revision = roster.Revision + 1,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(roster),
                Members = roster.Members.Select(member => member with { AccessExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }).ToArray(),
                Signature = ""
            };
            using var owner = ECDsa.Create();
            owner.ImportPkcs8PrivateKey(Fixture.A.LoadProtected("shared-world-signing-key.protected")!, out _);
            var expired = draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            field.SetValue(Fixture, expired);
            Fixture.SaveMemberships();
        }

        private object Group(WorldPeerService service) => typeof(WorldPeerService).GetMethod("Find", Private)!
            .Invoke(service, [Fixture.Root.GroupKey])!;
        internal Task<WorldPeerSeedResponse> SeedAsync() => (Task<WorldPeerSeedResponse>)typeof(WorldPeerService)
            .GetMethod("SendSeedAsync", Private)!.Invoke(Fixture.RequesterService,
                [Group(Fixture.RequesterService), Fixture.SourceRoute, Descriptor, null, null, null, null, CancellationToken.None])!;
        internal Task ReuseAsync(CancellationToken cancellationToken) => (Task)typeof(WorldPeerService)
            .GetMethod("TryReuseExistingCopyAsync", Private)!.Invoke(Fixture.TargetService,
                [Group(Fixture.TargetService), Descriptor, cancellationToken, null, null, null])!;
        internal SharedWorldVersion Head() => JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(Path.Combine(
            origin == "Published" ? Path.GetDirectoryName(Vault)! : Vault, "latest.json")), Json)!;
        internal string[] Sidecars() => Directory.EnumerateFiles(DirectoryPath, "self-receipt-*.json").ToArray();
        internal bool NoNewCopy() => (origin is "Received" or "ReceivedBranch" || !Directory.Exists(Fixture.DuplicateVault)) &&
            !Directory.Exists(Path.Combine(Fixture.Target.RootPath, "received-shared-histories", Fixture.Root.GroupKey)) &&
            (origin == "Received" || !Directory.Exists(Path.Combine(Fixture.Target.RootPath, "received-shared-worlds",
                (Fixture.Descriptor.Branch?.Offer.Proposal.ProposerDeviceId ?? Guid.Empty).ToString("N"),
                Fixture.Root.ProfileId.ToString("N"), Descriptor.Version.VersionHash)));
        internal bool ValidReceipt(SharedWorldReceipt receipt) => receipt.GroupId == Fixture.Root.GroupId &&
            receipt.ProfileId == Fixture.Root.ProfileId && receipt.VersionHash == Descriptor.Version.VersionHash &&
            (Fixture.Descriptor.Branch is { } branch ? receipt.DeviceId == branch.Offer.Proposal.ProposerDeviceId &&
                SharedWorldReceiptTrust.Verify(receipt, branch.CandidatePublicKey) :
                receipt.DeviceId == Guid.Empty && SharedWorldReceiptTrust.VerifyPeerOwner(receipt, Fixture.Root));
        public void Dispose() => Fixture.Dispose();
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or CryptographicException or JsonException or OperationCanceledException)
        { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require([DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
