using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class WorldPeerHostingDiscoveryChecks
{
    private static async Task OriginalPublicationStartChecksAsync(string parent)
    {
        await StaleBackgroundAdvertisementScheduleAsync(parent);
        await OriginalAdvertisementScheduleAsync(parent);
        await LocalAdvertisementIdentityChecksAsync(parent);
        await ReturningOwnerMissingCopyChecksAsync(parent);
        await OriginalPendingSelectionCompletenessChecksAsync();
        await OriginalPendingAnchoredSelectionChecksAsync(parent);
        await OriginalDiscoveryBudgetDiagnosticAsync(parent);
        using (var fixture = new OriginalPublicationFixture(parent, "two-new-publications"))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var version = attempt == 0 ? fixture.Current : fixture.PublishSameBytes();
                fixture.Driver.AllowPreparation = true;
                fixture.Driver.Validate = _ => RequireHeldOriginal(fixture.PublicationFile(version));
                fixture.Driver.Prepare = _ => RequireHeldOriginal(fixture.PublicationFile(version));
                fixture.Driver.AtStart = _ => RequireHeldOriginal(fixture.PublicationFile(version));
                var result = await fixture.Owner.StartAsync(fixture.Profile.Id, default).WaitAsync(TimeSpan.FromSeconds(10));
                Require(result.Code == "LaunchFailed" && fixture.Driver.ValidationAttempts == attempt + 1 &&
                    fixture.Driver.PrepareAttempts == attempt + 1 && fixture.Driver.StartAttempts == attempt + 1 &&
                    version.Files.Count == 315 && fixture.Peers.Views().Single(item => item.GroupKey == fixture.Root.GroupKey) is
                    { SelfDeviceId: var id, SharingEnabled: true, ReceiveConsented: true, CanHost: true } && id == Guid.Empty,
                    $"original publication {version.Number} did not reach the final refusing driver with armed outbound sharing: {result.Code}");
                fixture.RequireNoCopyOrTransfer();
                using var released = new FileStream(fixture.PublicationFile(version), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Require(released.CanWrite, "original Start leaked its held publication after the refusing driver returned");
            }
        }
        foreach (var fault in new[] { "fork", "permission", "consent", "payload", "parent" })
        {
            using var fixture = new OriginalPublicationFixture(parent, "refuse-" + fault, fileCount: 3);
            if (fault == "fork")
            {
                var basis = fixture.Base.Target.Version;
                var claim = SharedWorldService.SignVersion(basis with
                {
                    Number = basis.Number + 1,
                    ParentHash = basis.VersionHash,
                    BackupId = Guid.NewGuid(),
                    Files = [Fact(Encoding.UTF8.GetBytes("synthetic unreceived " + fault))]
                }, fixture.Base.Owner);
                fixture.Base.Target.Announce(claim);
            }
            else if (fault == "permission") fixture.CorruptRoster();
            else if (fault == "consent")
                Require(new WorldPeerReceiveConsentStore(fixture.Data).Set(fixture.Root, Guid.Empty, false),
                    "synthetic original consent withdrawal failed");
            else if (fault == "parent") File.Delete(Path.Combine(fixture.Data.RootPath, "shared-worlds", fixture.Profile.Id.ToString("N"),
                fixture.Root.GroupId.ToString("N"), "1", "version.json"));
            else File.WriteAllText(fixture.PublicationFile(fixture.Current), "synthetic changed publication");
            var result = await fixture.Owner.StartAsync(fixture.Profile.Id, default).WaitAsync(TimeSpan.FromSeconds(10));
            Require(!result.Ok && fixture.Driver.ValidationAttempts == 0 && fixture.Driver.StartAttempts == 0,
                $"original {fault} bypassed latest signed permission/save checks: {result.Code}");
            fixture.RequireNoCopyOrTransfer();
        }
        foreach (var fault in new[] { "material", "settings", "consent", "permission", "pending-permission" })
        {
            using var fixture = new OriginalPublicationFixture(parent, "final-" + fault, fileCount: 3);
            fixture.Driver.AllowPreparation = true;
            fixture.Driver.Prepare = _ =>
            {
                RequireHeldOriginal(fixture.PublicationFile(fixture.Current));
                if (fault == "material")
                {
                    var store = Store(fixture.Data, fixture.Root, fixture.Base.Target.Roster);
                    var current = store.ReadPage().Announcements.Where(item => item.DeviceId == Guid.Empty)
                        .OrderByDescending(item => item.Revision).First();
                    store.Publish(Guid.Empty, fixture.Base.Owner, current.Routes, current.Sessions, current.Branches,
                        [.. current.AvailableVersionHashes, new string('A', 64)]);
                }
                else if (fault == "settings")
                {
                    var settings = fixture.Data.LoadSettings();
                    settings.Profiles.Single(item => item.Id == fixture.Profile.Id).Crossplay = true;
                    fixture.Data.SaveSettings(settings);
                }
                else if (fault == "consent")
                    Require(new WorldPeerReceiveConsentStore(fixture.Data).Set(fixture.Root, Guid.Empty, false),
                        "synthetic final original consent withdrawal failed");
                else if (fault == "pending-permission")
                    File.WriteAllText(Path.Combine(fixture.Data.RootPath, "shared-worlds", fixture.Profile.Id.ToString("N"), "roster-dirty"),
                        "Synthetic unsigned permission change");
                else fixture.CorruptRoster();
            };
            var result = await fixture.Owner.StartAsync(fixture.Profile.Id, default).WaitAsync(TimeSpan.FromSeconds(10));
            Require(result.Code == "LaunchFailed" && fixture.Driver.ValidationAttempts == 1 &&
                fixture.Driver.PrepareAttempts == 1 && fixture.Driver.StartAttempts == 0,
                $"original {fault} changed during common preparation without final refusal: {result.Code}");
            fixture.RequireNoCopyOrTransfer();
            using var released = new FileStream(fixture.PublicationFile(fixture.Current), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(released.CanWrite, "final original-source refusal leaked its publication lease");
        }
        using (var fixture = new OriginalPublicationFixture(parent, "cancel-verification", fileCount: 3))
        using (var canceled = new CancellationTokenSource())
        {
            Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
                "synthetic cancellation fixture could not register its original world");
            var sawVerification = false;
            try
            {
                await fixture.Peers.ResolveLatestOriginalStartSourceAsync(fixture.Root, canceled.Token, update =>
                {
                    if (update.Phase != "Verifying") return;
                    sawVerification = true;
                    canceled.Cancel();
                });
                throw new InvalidOperationException("Canceled original verification returned a usable source.");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            Require(sawVerification && fixture.Driver.ValidationAttempts == 0, "original cancellation did not exercise payload verification");
            fixture.RequireNoCopyOrTransfer();
            using var released = new FileStream(fixture.PublicationFile(fixture.Current), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(released.CanWrite, "canceled original verification leaked its held payload");
        }
    }

    private static async Task ReturningOwnerMissingCopyChecksAsync(string parent)
    {
        using (var fixture = new ReturningOwnerCopyFixture(parent))
        {
            await fixture.AnnounceAsync();
            var original = await fixture.Peers.ResolveLatestOriginalStartSourceAsync(fixture.Root, default);
            Require(original is { Ok: true, RecoveryRequired: true, Source: null, Code: "SharedSaveCopyPending" } &&
                fixture.Diagnostics.Any(item => item.Reason == "KnownProgressNotLocal"),
                "An announced newer save without local payload did not route original Start into common catch-up.");
            fixture.RequireNoReceivedCopy();
            var latest = await fixture.Peers.ResolveLatestRecoverySourceAsync(fixture.Root, default);
            Require(latest.Ok && latest.Source?.Descriptor.Version.VersionHash == fixture.Latest.VersionHash &&
                fixture.Copies.SourceHandler.Operations.Contains(WorldPeerOperations.Catalog) &&
                fixture.Copies.SourceHandler.Operations.Contains(WorldPeerOperations.Chunk),
                "The routed returning owner did not receive and select the exact complete newer signed continuation.");
            latest.Source!.Verify(fixture.Copies.A, fixture.Root);
            fixture.RequireOriginalPreserved();
        }
        foreach (var unavailable in new[] { "Offline", "Revoked", "Expired", "Consent" })
        {
            using var fixture = new ReturningOwnerCopyFixture(parent);
            await fixture.AnnounceAsync();
            var original = await fixture.Peers.ResolveLatestOriginalStartSourceAsync(fixture.Root, default);
            Require(original is { RecoveryRequired: true, Source: null }, "The generated pending owner source was not classified.");
            fixture.RequireNoReceivedCopy();
            if (unavailable == "Offline") fixture.Offline = true;
            else if (unavailable == "Consent")
                Require(new WorldPeerReceiveConsentStore(fixture.Copies.A).Set(fixture.Root, Guid.Empty, false),
                    "The generated returning-owner consent withdrawal failed.");
            else fixture.RemoveSourceAccess(expired: unavailable == "Expired");
            var result = await fixture.Peers.ResolveLatestRecoverySourceAsync(fixture.Root, default).WaitAsync(TimeSpan.FromSeconds(8));
            Require(!result.Ok && result.Source is null && (unavailable == "Consent" ? result.Code == "HostingPermissionRequired" :
                result.Code == "SharedSaveUnavailable" && result.Message.Contains("not complete and verified", StringComparison.Ordinal)),
                "An unavailable newer save became a generic permission/history error or an old-world Start proof: " + unavailable + "/" + result.Code);
            fixture.RequireNoReceivedCopy();
            fixture.RequireOriginalPreserved();
        }
    }

    private static async Task OriginalPendingSelectionCompletenessChecksAsync()
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var first = Version(root, owner, Encoding.UTF8.GetBytes("generated complete local origin"));
        var second = SharedWorldService.SignVersion(first with { Number = 2, ParentHash = first.VersionHash, BackupId = Guid.NewGuid() }, owner);
        var third = SharedWorldService.SignVersion(second with { Number = 3, ParentHash = second.VersionHash, BackupId = Guid.NewGuid() }, owner);
        var fork = SharedWorldService.SignVersion(second with { BackupId = Guid.NewGuid() }, owner);
        var laterFork = SharedWorldService.SignVersion(fork with { Number = 3, ParentHash = fork.VersionHash, BackupId = Guid.NewGuid() }, owner);
        var versions = new Dictionary<string, SharedWorldVersion> { [first.VersionHash] = first, [second.VersionHash] = second };
        Task<SharedWorldVersion?> Read(string hash, long number, CancellationToken _) => Task.FromResult(
            versions.TryGetValue(hash, out var version) && version.Number == number ? version : null);
        async Task Refuses(IReadOnlyList<WorldPeerSaveDescriptor> candidates, SharedWorldVersion known, bool pending,
            Func<int, CancellationToken, Task>? verify = null)
        {
            try
            {
                await WorldPeerLatestSelection.SelectAsync(root, candidates, [known.VersionHash],
                    verify ?? ((_, _) => Task.CompletedTask), Read, default, knownHeadNumbers:
                    new Dictionary<string, long> { [known.VersionHash] = known.Number });
            }
            catch (InvalidDataException error)
            {
                Require((error.Data["WorldPeerLatestReason"] is "KnownProgressNotLocal") == pending,
                    "Missing/corrupt/divergent local history was misclassified as fetchable missing progress.");
                return;
            }
            throw new InvalidOperationException("Missing known progress granted a selected local Start source.");
        }
        await Refuses([new(second)], third, pending: true);
        await Refuses([new(second)], laterFork, pending: true); // Unknown remote ancestry is only a catch-up hint.
        versions.Add(fork.VersionHash, fork);
        await Refuses([new(second), new(laterFork)], laterFork, pending: false); // Receiving it cannot merge the fork.
        await Refuses([new(second)], fork, pending: false);
        await Refuses([new(second), new(fork)], third, pending: false);
        await Refuses([new(second)], third, pending: false, (_, _) => Task.FromException(new InvalidDataException("Generated corrupt local payload.")));
        versions.Remove(first.VersionHash);
        await Refuses([new(second)], third, pending: false);
        var selected = await WorldPeerLatestSelection.SelectAsync(root, [new(second)], [], (_, _) => Task.CompletedTask, Read, default);
        Require(selected == 0, "Completeness used for pending classification tightened an existing valid anchored selection.");
    }

    private static async Task OriginalDiscoveryBudgetDiagnosticAsync(string parent)
    {
        using var fixture = new OriginalPublicationFixture(parent, "discovery-budget-diagnostic", fileCount: 3);
        Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
            "The generated original discovery budget check lacked membership.");
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gates = typeof(WorldPeerService).GetMethod("ExchangeGatesFor", flags)!.Invoke(fixture.Peers, [fixture.Root])!;
        var gate = (SemaphoreSlim)gates.GetType().GetProperty("LocalAdvertisement", flags)!.GetValue(gates)!;
        var diagnostics = new List<WorldPeerLatestReviewDiagnostic>();
        fixture.Peers.LatestReviewDiagnostic = diagnostics.Add;
        await gate.WaitAsync();
        try
        {
            var result = await fixture.Peers.ResolveLatestOriginalStartSourceAsync(fixture.Root, default).WaitAsync(TimeSpan.FromSeconds(15));
            Require(!result.Ok && !result.RecoveryRequired && result.Source is null && result.Code == "SharedSaveHistoryNeedsReview" &&
                diagnostics.Any(item => item.Phase == "OriginalDiscovery" && item.Reason == "ReviewRequired" && item.ExceptionType == "InvalidDataException"),
                "An unsettled original metadata budget entered payload catch-up or lost its fixed diagnostic phase.");
            fixture.RequireNoCopyOrTransfer();
        }
        finally { gate.Release(); }
    }

    private static async Task OriginalPendingAnchoredSelectionChecksAsync(string parent)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Branch");
        var bytes = File.ReadAllBytes(fixture.PayloadPath);
        var first = fixture.Descriptor.Branch!.Offer.Version;
        using var owner = ECDsa.Create();
        owner.ImportPkcs8PrivateKey(fixture.A.LoadProtected("shared-world-signing-key.protected")!, out _);
        var second = SharedWorldService.SignVersion(first with
        { Number = 2, ParentHash = first.VersionHash, BackupId = Guid.NewGuid() }, owner);
        var third = SharedWorldService.SignVersion(second with
        { Number = 3, ParentHash = second.VersionHash, BackupId = Guid.NewGuid() }, owner);
        var source = Path.Combine(fixture.B.RootPath, "synthetic-complete-anchor-source");
        foreach (var version in new[] { first, second, third }) WriteCopy(source, version, bytes);
        using var member = ECDsa.Create();
        member.ImportPkcs8PrivateKey(fixture.B.LoadProtected(Path.GetFileName(fixture.KeyPath))!, out _);
        var roster = fixture.Descriptor.Branch.Offer.Roster;
        var offer = SharedWorldSeparateCopyStore.PrepareSoloOffer(source, roster,
            new(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature),
            fixture.Descriptor.Branch.Offer.Proposal.ProposerDeviceId, member,
            fixture.SourceRoute.Address, fixture.SourceRoute.CertificateSha256, new WorldAuthorityStore(fixture.B));
        var branch = SharedWorldSeparateCopyStore.Sign(offer, member);
        Require(SharedWorldSeparateCopyStore.Verify(branch) && branch.Offer.Version.VersionHash == third.VersionHash,
            "The generated version3 anchor lacks an actual verified signed declaration.");
        using var host = ECDsa.Create();
        host.ImportPkcs8PrivateKey(fixture.B.LoadProtected(WorldAuthorityStore.HostingKeyName(fixture.Root.ProfileId))!, out _);
        var retained = Path.Combine(fixture.B.RootPath, "synthetic-retained-anchor");
        WriteCopy(retained, third, bytes);
        var versions = new Dictionary<long, SharedWorldVersion> { [3] = third };
        var current = third;
        for (var number = 4; number <= 11; number++)
        {
            current = SharedWorldService.SignVersion(current with
            { Number = number, ParentHash = current.VersionHash, BackupId = Guid.NewGuid() }, host);
            versions.Add(number, current);
            if (number <= 9) WriteCopy(retained, current, bytes);
        }
        var invalidOlder = false;
        Task<SharedWorldVersion?> Read(string hash, long number, CancellationToken _) => Task.FromResult(
            number == 2 && invalidOlder ? second with { Signature = "" } :
                FriendLink.ReadRetainedSignedManifest(retained, number, hash));
        var candidates = new WorldPeerSaveDescriptor[] { new(third), new(versions[9], branch) };
        Task Verify(int index, CancellationToken token)
        {
            var descriptor = candidates[index];
            using var lease = WorldPeerVerifiedCopy.Open(fixture.B.RootPath, Path.Combine(retained, descriptor.Version.VersionHash), descriptor.Version, token);
            if (descriptor.Branch is not null) WorldPeerFileTransfer.VerifyHistory(retained, descriptor, third);
            return Task.CompletedTask;
        }
        async Task Refuses(bool pending, bool requiredOlder = false)
        {
            try
            {
                await WorldPeerLatestSelection.SelectAsync(fixture.Root, candidates,
                    requiredOlder ? [versions[11].VersionHash, first.VersionHash] : [versions[11].VersionHash], Verify, Read, default,
                    knownHeadNumbers: new Dictionary<string, long> { [versions[11].VersionHash] = 11, [first.VersionHash] = 1 });
            }
            catch (InvalidDataException error)
            {
                Require((error.Data["WorldPeerLatestReason"] is "KnownProgressNotLocal") == pending,
                    "Anchored pending routing hid missing/corrupt required ancestry or required unavailable pre-anchor history.");
                return;
            }
            throw new InvalidOperationException("A missing announced version11 became a selected anchored Start source.");
        }
        Require(FriendLink.ReadRetainedSignedManifest(retained, 1, first.VersionHash) is null &&
            FriendLink.ReadRetainedSignedManifest(retained, 2, second.VersionHash) is null,
            "The anchored regression accidentally retained the pre-anchor manifests.");
        await Refuses(pending: true);
        await Refuses(pending: false, requiredOlder: true);
        invalidOlder = true;
        await Refuses(pending: false);
        invalidOlder = false;
        var parentPath = Directory.EnumerateFiles(Path.Combine(retained, "signed-history"),
            "*" + versions[7].VersionHash + ".json", SearchOption.AllDirectories).Single();
        var parentBytes = File.ReadAllBytes(parentPath);
        File.WriteAllText(parentPath, "{damaged generated required parent7");
        await Refuses(pending: false);
        File.WriteAllBytes(parentPath, parentBytes);
        File.Delete(parentPath);
        File.Delete(Path.Combine(retained, versions[7].VersionHash, "version.json"));
        await Refuses(pending: false);
        Require(!Directory.Exists(Path.Combine(fixture.B.RootPath, "world-peer-recovery-sources")) &&
            fixture.B.LoadSettings().Profiles.Count == 0 && fixture.A.LoadRuns().Count == 0 && fixture.B.LoadRuns().Count == 0 &&
            fixture.SourceHandler.Operations.Count == 0 && fixture.RemoteHandler.Operations.Count == 0 &&
            File.ReadAllBytes(Path.Combine(retained, versions[9].VersionHash, SharedWorldService.PayloadDirectory, "world.dat")).SequenceEqual(bytes),
            "Anchored classification created a source/setup/run, used transport or changed the verified version9 payload.");
    }

    private sealed class ReturningOwnerCopyFixture : IDisposable
    {
        internal WorldPeerExistingCopyChecks.Fixture Copies { get; }
        internal SharedWorldPeerRoot Root => Copies.Root;
        internal WorldPeerService Peers { get; }
        internal SharedWorldVersion Latest { get; }
        internal List<WorldPeerLatestReviewDiagnostic> Diagnostics { get; } = [];
        internal bool Offline { get; set; }
        private readonly FriendService friends;
        private readonly SharedWorldVersion original;
        private readonly string originalPayload, liveMarker;
        private readonly byte[] originalBytes;
        private const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        internal ReturningOwnerCopyFixture(string parent)
        {
            Copies = new(parent, "Branch");
            var profile = Copies.A.LoadSettings().Profiles.Single();
            original = Copies.Descriptor.Branch!.Offer.Version;
            originalPayload = Path.Combine(Copies.A.RootPath, "shared-worlds", Root.ProfileId.ToString("N"), Root.GroupId.ToString("N"),
                "1", SharedWorldService.PayloadDirectory, "world.dat");
            originalBytes = File.ReadAllBytes(originalPayload);
            liveMarker = Path.Combine(profile.WorldDirectory, "preserved-owner.synthetic");
            File.WriteAllText(liveMarker, "generated original live world stays intact");
            using (var host = ECDsa.Create())
            {
                host.ImportPkcs8PrivateKey(Copies.B.LoadProtected(WorldAuthorityStore.HostingKeyName(Root.ProfileId))!, out _);
                var current = Copies.Descriptor.Version;
                for (var number = 3; number <= 11; number++)
                {
                    current = SharedWorldService.SignVersion(current with
                    { Number = number, ParentHash = current.VersionHash, BackupId = Guid.NewGuid() }, host);
                    WriteCopy(Path.GetDirectoryName(Copies.DirectoryPath)!, current, originalBytes);
                }
                Latest = current;
            }
            HttpClient Client(string endpoint, IEnumerable<string> pins)
            {
                Require(endpoint.TrimEnd('/') == Copies.SourceRoute.Address && pins.SequenceEqual([Copies.SourceRoute.CertificateSha256]),
                    "The generated returning owner changed its pinned approved source.");
                return new(new ReturningOwnerWire(this, Copies.SourceHandler))
                { BaseAddress = new Uri(Copies.SourceRoute.Address + "/"), Timeout = TimeSpan.FromSeconds(6) };
            }
            friends = new(Copies.A, Client);
            var pairing = Read<PairingService>("pairing");
            Peers = new(Copies.A, new HostManager(Copies.A), friends, pairing, Client);
            Peers.LatestReviewDiagnostic = Diagnostics.Add;
        }

        private T Read<T>(string name) => (T)typeof(WorldPeerExistingCopyChecks.Fixture).GetField(name, Private)!.GetValue(Copies)!;
        internal async Task AnnounceAsync()
        {
            var group = typeof(WorldPeerService).GetMethod("Find", Private)!.Invoke(Copies.TargetService, [Root.GroupKey])!;
            var publication = (Task<bool>)typeof(WorldPeerService).GetMethod("PublishLocalAdvertisementAsync", Private)!
                .Invoke(Copies.TargetService, [group, CancellationToken.None, false, false])!;
            Require(await publication, "The generated peer could not publish its signed completed continuation.");
        }

        internal void RemoveSourceAccess(bool expired)
        {
            var roster = Read<SharedWorldRoster>("roster");
            var members = roster.Members.Select(member => member with
            { Revoked = !expired, AccessExpiresUtc = expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : member.AccessExpiresUtc }).ToArray();
            var draft = roster with
            {
                Epoch = roster.Epoch + 1,
                Revision = roster.Revision + 1,
                PreviousRosterHash = SharedWorldRosterTrust.Hash(roster),
                Members = members,
                OwnerLocalBaselineMembers = members,
                Signature = ""
            };
            using var owner = ECDsa.Create();
            owner.ImportPkcs8PrivateKey(Copies.A.LoadProtected("shared-world-signing-key.protected")!, out _);
            var changed = draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            typeof(WorldPeerExistingCopyChecks.Fixture).GetField("roster", Private)!.SetValue(Copies, changed);
            Copies.SaveMemberships();
        }

        internal void RequireNoReceivedCopy() => Require(
            !Directory.Exists(SharedWorldBranchVault.Root(Copies.A, Copies.Descriptor.Branch!)) &&
            !Directory.Exists(Path.Combine(Copies.A.RootPath, "world-peer-recovery-sources")) &&
            !Copies.SourceHandler.Operations.Any(operation => operation is WorldPeerOperations.Catalog or WorldPeerOperations.Chunk or WorldPeerOperations.Seed),
            "A pending/unauthorized original Start received payload, copied a source or bypassed its metadata-only routing boundary.");
        internal void RequireOriginalPreserved() => Require(
            File.ReadAllBytes(originalPayload).SequenceEqual(originalBytes) &&
            File.ReadAllText(liveMarker) == "generated original live world stays intact" &&
            JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(Path.Combine(Copies.A.RootPath, "shared-worlds",
                Root.ProfileId.ToString("N"), "latest.json")), Json)?.VersionHash == original.VersionHash &&
            Copies.A.LoadSettings().Profiles.Count == 1 && !Copies.A.LoadSettings().CompanionListeningEnabled &&
            Copies.A.LoadRuns().Count == 0 && Copies.B.LoadRuns().Count == 0,
            "Returning-owner catch-up overwrote original progress, restored/setup a profile, enabled a listener or created a run.");
        public void Dispose() { Peers.Dispose(); friends.Dispose(); Copies.Dispose(); }

        private sealed class ReturningOwnerWire(ReturningOwnerCopyFixture fixture, HttpMessageHandler handler) : HttpMessageHandler
        {
            private readonly HttpMessageInvoker target = new(handler, disposeHandler: false);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                fixture.Offline ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Generated approved peer is unreachable.")) :
                    target.SendAsync(request, cancellationToken);
            protected override void Dispose(bool disposing) { if (disposing) target.Dispose(); base.Dispose(disposing); }
        }
    }

    private static async Task StaleBackgroundAdvertisementScheduleAsync(string parent)
    {
        using var fixture = new OriginalPublicationFixture(parent, "stale-background-advertisement", fileCount: 3);
        Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
            "the stale background schedule lacked its original signed membership");
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var group = typeof(WorldPeerService).GetMethod("Find", flags)!.Invoke(fixture.Peers, [fixture.Root.GroupKey])!;
        using var release = new ManualResetEventSlim();
        var barrierReleased = false;
        var built = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = typeof(WorldPeerService).GetField("localAdvertisementBuiltForChecks", flags)!;
        barrier.SetValue(fixture.Peers, (Action)(() =>
        {
            built.TrySetResult(true);
            barrierReleased = release.Wait(TimeSpan.FromSeconds(5));
        }));
        var background = Task.Run(async () => await (Task)typeof(WorldPeerService).GetMethod("SyncScheduledGroupAsync", flags)!
            .Invoke(fixture.Peers, [group, CancellationToken.None, CancellationToken.None, false])!);
        try
        {
            await built.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var preparation = fixture.Peers.BeginHostingPreparation(fixture.Root);
            var before = fixture.Peers.LatestObservationHash(fixture.Root);
            release.Set();
            await background.WaitAsync(TimeSpan.FromSeconds(5));
            Require(barrierReleased && before is not null && fixture.Peers.LatestObservationHash(fixture.Root) == before,
                "a background advertisement begun before preparation published stale material after preparation started");
            var builds = 0;
            barrier.SetValue(fixture.Peers, (Action)(() => builds++));
            await (Task)typeof(WorldPeerService).GetMethod("SyncScheduledGroupAsync", flags)!
                .Invoke(fixture.Peers, [group, CancellationToken.None, CancellationToken.None, false])!;
            Require(builds == 0 && fixture.Peers.LatestObservationHash(fixture.Root) == before && fixture.Base.Target.Handler.Challenges == 0,
                "a new background publisher did not yield before snapshot/network work during preparation");
            fixture.RequireNoCopyOrTransfer();
        }
        finally { release.Set(); barrier.SetValue(fixture.Peers, null); await background; }
    }

    private static async Task OriginalAdvertisementScheduleAsync(string parent)
    {
        using var fixture = new OriginalPublicationFixture(parent, "local-advertisement-schedule", fileCount: 3);
        Require((await fixture.Peers.EnsureOwnerHostingMembershipAsync(fixture.Root, default)).Ok,
            "the local advertisement schedule lacked its original signed membership");
        var armed = await fixture.Peers.AnnounceLocalHostingAsync(fixture.Root, default);
        Require(armed.Ok || armed.Code == "RecipientConnectionRequired", "the local advertisement schedule could not arm its existing world");
        var published = false;
        var changed = false;
        var version = fixture.Current.VersionHash;
        var result = await fixture.Peers.ResolveLatestOriginalStartSourceAsync(fixture.Root, default, update =>
        {
            if (published || update.Phase != "Verifying") return;
            published = true;
            var before = fixture.Peers.LatestObservationHash(fixture.Root);
            // Exercise the real background metadata builder at the exact
            // verification boundary. The publication and setup stay unchanged.
            var advertisement = LocalAdvertisementForChecksAsync(fixture.Peers, fixture.Root).GetAwaiter().GetResult();
            Store(fixture.Data, fixture.Root, fixture.Base.Target.Roster).Publish(Guid.Empty, fixture.Base.Owner,
                advertisement.Routes, advertisement.Sessions, advertisement.Branches, advertisement.AvailableVersionHashes);
            changed = before != fixture.Peers.LatestObservationHash(fixture.Root);
        });
        using var source = result.Source;
        Require(published && !changed && result.Ok && source?.Descriptor.Version.VersionHash == version,
            $"same-publication local advertisement invalidated original Start: {result.Code}; published={published}; materialChanged={changed}");
        source.RequireCurrent(fixture.Data, fixture.Root);
        fixture.RequireNoCopyOrTransfer();
    }

    private static async Task<WorldPeerAdvertisement> LocalAdvertisementForChecksAsync(WorldPeerService service, SharedWorldPeerRoot root)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var group = typeof(WorldPeerService).GetMethod("Find", flags)!.Invoke(service, [root.GroupKey])!;
        var method = typeof(WorldPeerService).GetMethod("BuildAdvertisementAsync", flags)!;
        object?[] arguments = method.GetParameters().Length == 2 ? [group, CancellationToken.None] : [group, CancellationToken.None, true];
        return await (Task<WorldPeerAdvertisement>)method.Invoke(service, arguments)!;
    }

    private static void RequireHeldOriginal(string path)
    {
        var blocked = false;
        try { using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
        catch (IOException) { blocked = true; }
        Require(blocked, "the common driver boundary did not hold the original immutable publication against writes");
    }

    private sealed class OriginalPublicationFixture : IDisposable
    {
        internal Fixture Base { get; }
        internal LocalData Data => Base.Data;
        internal ServerProfile Profile { get; }
        internal SharedWorldPeerRoot Root => Base.Target.Root;
        internal SharedWorldVersion Current { get; private set; }
        internal OriginalStartDriver Driver { get; } = new();
        internal WorldPeerService Peers { get; }
        internal SimpleOwnerHostingService Owner { get; }
        private readonly OriginalFilesCapture capture;
        private readonly SharedWorldService publisher;
        private readonly FriendService friends;

        internal OriginalPublicationFixture(string parent, string name, int fileCount = 315)
        {
            Base = new(parent, "original-" + name);
            var settings = Data.LoadSettings();
            Profile = settings.Profiles.Single(item => item.Id == Base.Target.Profile.Id);
            Profile.ExecutablePath = Path.Combine(Data.RootPath, "never-executed-original.synthetic");
            File.WriteAllText(Profile.ExecutablePath, "Synthetic file; no process may execute it.");
            Data.SaveSettings(settings);
            capture = new(Data.RootPath, Profile, fileCount);
            publisher = new(Data, capture);
            Current = PublishSameBytes();
            // This generated fixture deliberately has no exact recovery cache
            // for its fresh publications, nor an owner registry entry.
            Directory.Delete(Path.Combine(Data.RootPath, "world-peer-recovery-sources"), recursive: true);
            SaveGroups(Data, [Membership(Base.Other.Root, Base.Other.Roster, Guid.Empty, Base.Other.Route)]);
            var manager = new HostManager(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly));
            HttpClient Client(string endpoint, IEnumerable<string> pins)
            {
                Require(endpoint.TrimEnd('/') == Base.Target.Route.Address && pins.SequenceEqual([Base.Target.Route.CertificateSha256]),
                    "original Start contacted another world or changed its approved TLS pin");
                return new(Base.Target.Handler, disposeHandler: false) { BaseAddress = new Uri(Base.Target.Route.Address + "/") };
            }
            friends = new(Data, Client);
            Peers = new(Data, manager, friends, new PairingService(Data), Client);
            manager.SharedHostingObservation = Peers.LatestObservationHash;
            manager.SharedHostingStartBlock = (root, hash) => Peers.CanonicalStartBlock(root, hash)?.Message;
            Owner = new(Data, manager, Peers, () => throw new InvalidOperationException("Original Start must not enable a listener."));
        }

        internal SharedWorldVersion PublishSameBytes()
        {
            var published = publisher.PublishAfterStop(Profile, Guid.NewGuid());
            Require(published is { Ok: true, Version: not null }, "synthetic original publication failed: " + published.Code + "; " + published.Message);
            Current = published.Version;
            Base.Target.Announce(Current);
            return Current;
        }

        internal string PublicationFile(SharedWorldVersion version) => Path.Combine(Data.RootPath, "shared-worlds",
            Profile.Id.ToString("N"), Root.GroupId.ToString("N"), version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SharedWorldService.PayloadDirectory, version.Files[0].Path.Replace('/', Path.DirectorySeparatorChar));

        internal void CorruptRoster()
        {
            var path = Path.Combine(Data.RootPath, "shared-worlds", Profile.Id.ToString("N"), Root.GroupId.ToString("N") + ".roster.json");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(Base.Target.Roster with
            { Signature = Convert.ToBase64String(new byte[64]) }, Json));
        }

        internal void RequireNoCopyOrTransfer() => Require(
            !Directory.Exists(Path.Combine(Data.RootPath, "world-peer-recovery-sources")) &&
            Base.Target.Handler.PayloadRequests == 0 && Base.Other.Handler.Challenges == 0 && Data.LoadRuns().Count == 0 &&
            !Data.LoadSettings().CompanionListeningEnabled,
            "original Start created a recovery copy, transferred payloads, contacted another world, launched a process or enabled listening");

        public void Dispose() { Peers.Dispose(); friends.Dispose(); Base.Dispose(); }
    }

    private sealed class OriginalFilesCapture : ISharedWorldCaptureAdapter
    {
        private readonly string payload;
        private readonly SharedWorldFile[] files;
        internal OriginalFilesCapture(string root, ServerProfile profile, int fileCount)
        {
            payload = Path.Combine(root, "synthetic-original-315");
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(profile.WorldDirectory);
            files = Enumerable.Range(0, fileCount).Select(index =>
            {
                var path = index == 0 ? "world.dat" : $"synthetic-{index:D3}.dat";
                var bytes = Encoding.UTF8.GetBytes("strict generated original file " + index);
                File.WriteAllBytes(Path.Combine(payload, path), bytes);
                File.WriteAllBytes(Path.Combine(profile.WorldDirectory, path), bytes);
                return new SharedWorldFile(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            }).ToArray();
        }
        public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId) =>
            new(1, SharedWorldCaptureKinds.PostStopBackup, backupId, DateTimeOffset.UtcNow, files, payload,
                new(3, profile.Id, profile.Kind, profile.WorldId, "Unknown", "Synthetic315Files", [], [], profile.GamePort));
    }

    private sealed class OriginalStartDriver : IGameServerDriver
    {
        internal Action<ServerProfile>? Validate { get; set; }
        internal Action<ServerProfile>? Prepare { get; set; }
        internal Action<ServerProfile>? AtStart { get; set; }
        internal bool AllowPreparation { get; set; }
        internal int ValidationAttempts { get; private set; }
        internal int PrepareAttempts { get; private set; }
        internal int StartAttempts { get; private set; }
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "File-only original Start boundary";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile)
        {
            ValidationAttempts++;
            Validate?.Invoke(profile);
            return AllowPreparation ? null : new("OriginalSourceVerified", "The file-only driver deliberately refuses process launch.");
        }
        public void PrepareStart(ServerProfile profile, ManagedRun run) { PrepareAttempts++; Prepare?.Invoke(profile); }
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) { StartAttempts++; AtStart?.Invoke(profile); throw NoProcess(); }
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
        private static InvalidOperationException NoProcess() => new("No process operation is permitted.");
    }
}
