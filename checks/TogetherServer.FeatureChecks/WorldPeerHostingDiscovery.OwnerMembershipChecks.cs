using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class WorldPeerHostingDiscoveryChecks
{
    private static async Task OwnerMembershipRegistrationAsync(string parent)
    {
        await FirstOwnerWithoutSaveAsync(parent);
        await RegisteredOwnerStartBoundaryAsync(parent);
        using var fixture = new Fixture(parent, "owner-registration");
        var world = fixture.Target;
        SaveGroups(fixture.Data, [Membership(fixture.Other.Root, fixture.Other.Roster, Guid.Empty, fixture.Other.Route)]);
        var preserved = OwnerIdentityFiles(fixture.Data);
        var otherBefore = RegisteredGroupJson(fixture.Data, fixture.Other.Root);
        var settingsBefore = File.ReadAllBytes(Path.Combine(fixture.Data.RootPath, "host.json"));
        var registered = await fixture.Service.EnsureOwnerHostingMembershipAsync(world.Root, default);
        var view = fixture.Service.Views().Single(item => item.GroupKey == world.Root.GroupKey);
        Require(registered.Ok && view.SelfDeviceId == Guid.Empty && view.ReceiveConsented && view.CanHost &&
            !view.SharingEnabled && RegisteredGroupJson(fixture.Data, fixture.Other.Root) == otherBefore &&
            File.ReadAllBytes(Path.Combine(fixture.Data.RootPath, "host.json")).SequenceEqual(settingsBefore),
            "initial owner registration did not retain the exact world, other membership and disabled listener policy");
        RequireOwnerIdentityFiles(fixture.Data, preserved);
        var registry = Path.Combine(fixture.Data.RootPath, "world-peer-memberships.protected");
        var unchanged = File.ReadAllBytes(registry);
        Require((await fixture.Service.EnsureOwnerHostingMembershipAsync(world.Root, default)).Ok &&
            File.ReadAllBytes(registry).SequenceEqual(unchanged),
            "unchanged owner registration rewrote protected membership state");
        SaveGroups(fixture.Data, [Membership(fixture.Other.Root, fixture.Other.Roster, Guid.Empty, fixture.Other.Route),
            new { Root = world.Root, Roster = world.Roster, DeviceId = Guid.Empty, Name = world.Profile.Name,
                ReceiveConsented = true, SharingEnabled = true, Bootstrap = world.Route }]);
        unchanged = File.ReadAllBytes(registry);
        Require((await fixture.Service.EnsureOwnerHostingMembershipAsync(world.Root, default)).Ok &&
            File.ReadAllBytes(registry).SequenceEqual(unchanged),
            "owner registration replaced a retained sharing choice or bootstrap route");

        using var wrongOwner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Refused(world.Root with { OwnerPublicKey = Public(wrongOwner) }, "wrong owner");
        await Refused(world.Root with { GroupId = Guid.NewGuid() }, "wrong group");
        await Refused(world.Root with { ProfileId = Guid.NewGuid() }, "unknown profile");
        await Refused(world.Root with { ProfileId = Guid.Empty }, "invalid root");
        var keyPath = Path.Combine(fixture.Data.RootPath, "shared-world-signing-key.protected");
        var keyBytes = File.ReadAllBytes(keyPath);
        File.Delete(keyPath);
        await Refused(world.Root, "missing retained owner key");
        Require(!File.Exists(keyPath), "owner registration regenerated a missing signing key");
        File.WriteAllBytes(keyPath, keyBytes);

        var hostPath = Path.Combine(fixture.Data.RootPath, "host.json");
        var hostBytes = File.ReadAllBytes(hostPath);
        var settings = fixture.Data.LoadSettings();
        settings.Profiles.Single(item => item.Id == world.Profile.Id).SeparateCopySourceProfileId = Guid.NewGuid();
        fixture.Data.SaveSettings(settings);
        await Refused(world.Root, "member-owned local profile");
        File.WriteAllBytes(hostPath, hostBytes);

        var rosterPath = Path.Combine(fixture.Data.RootPath, "shared-worlds", world.Profile.Id.ToString("N"),
            world.Root.GroupId.ToString("N") + ".roster.json");
        var rosterBytes = File.ReadAllBytes(rosterPath);
        File.WriteAllBytes(rosterPath, JsonSerializer.SerializeToUtf8Bytes(world.Roster with
        { Signature = Convert.ToBase64String(new byte[64]) }, Json));
        await Refused(world.Root, "invalid published signature");
        File.WriteAllBytes(rosterPath, rosterBytes);

        var normalRegistry = File.ReadAllBytes(registry);
        var stale = SignRoster(world.Roster with { Epoch = world.Roster.Epoch + 1, Revision = world.Roster.Revision + 1 }, fixture.Owner);
        SaveGroups(fixture.Data, [Membership(fixture.Other.Root, fixture.Other.Roster, Guid.Empty, fixture.Other.Route),
            Membership(world.Root, stale, Guid.Empty, world.Route)]);
        await Refused(world.Root, "published roster older than retained membership");
        var competing = SignRoster(world.Roster with { OwnerOverride = !world.Roster.OwnerOverride }, fixture.Owner);
        SaveGroups(fixture.Data, [Membership(fixture.Other.Root, fixture.Other.Roster, Guid.Empty, fixture.Other.Route),
            Membership(world.Root, competing, Guid.Empty, world.Route)]);
        await Refused(world.Root, "same-revision conflicting signed roster");
        SaveGroups(fixture.Data, [Membership(fixture.Other.Root, fixture.Other.Roster, Guid.Empty, fixture.Other.Route),
            Membership(world.Root, world.Roster, world.Roster.Members.Single().DeviceId, world.Route)]);
        await Refused(world.Root, "retained member identity");
        fixture.Data.SaveProtected("world-peer-memberships.protected", Encoding.UTF8.GetBytes("{broken-registry"));
        await Refused(world.Root, "damaged protected registry");
        File.WriteAllBytes(registry, normalRegistry);

        var consent = new WorldPeerReceiveConsentStore(fixture.Data);
        Require(consent.Set(world.Root, Guid.Empty, false), "synthetic owner consent withdrawal failed");
        await Refused(world.Root, "protected receive consent withdrawal");
        File.Delete(Path.Combine(fixture.Data.RootPath, WorldPeerReceiveConsentStore.StateName));
        var dirty = Path.Combine(fixture.Data.RootPath, "shared-worlds", world.Profile.Id.ToString("N"), "roster-dirty");
        File.WriteAllText(dirty, "Synthetic pending signed permission");
        await Refused(world.Root, "unpublished local permission change");
        File.Delete(dirty);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledBefore = File.ReadAllBytes(registry);
        try
        {
            await fixture.Service.EnsureOwnerHostingMembershipAsync(world.Root, canceled.Token);
            throw new InvalidOperationException("Canceled owner registration completed.");
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        Require(File.ReadAllBytes(registry).SequenceEqual(canceledBefore) &&
            fixture.Target.Handler.Challenges == 0 && fixture.Other.Handler.Challenges == 0 &&
            fixture.Data.LoadRuns().Count == 0 && !fixture.Data.LoadSettings().CompanionListeningEnabled,
            "local registration performed network/process/listener work or mutated canceled state");
        RequireOwnerIdentityFiles(fixture.Data, preserved);
        world.VerifyCachedCopy();
        fixture.Other.VerifyCachedCopy();

        async Task Refused(SharedWorldPeerRoot root, string reason)
        {
            var registryBefore = File.ReadAllBytes(registry);
            var identityBefore = OwnerIdentityFiles(fixture.Data);
            var result = await fixture.Service.EnsureOwnerHostingMembershipAsync(root, default).WaitAsync(TimeSpan.FromSeconds(3));
            Require(!result.Ok && File.ReadAllBytes(registry).SequenceEqual(registryBefore),
                reason + " was accepted or rebuilt the protected membership registry");
            RequireOwnerIdentityFiles(fixture.Data, identityBefore);
        }
    }

    private static async Task RegisteredOwnerStartBoundaryAsync(string parent)
    {
        using var fixture = new Fixture(parent, "first-owner-start");
        var world = fixture.Target;
        var settings = fixture.Data.LoadSettings();
        var profile = settings.Profiles.Single(item => item.Id == world.Profile.Id);
        Directory.CreateDirectory(profile.WorldDirectory);
        var working = Encoding.UTF8.GetBytes("synthetic cached " + profile.WorldId);
        File.WriteAllBytes(Path.Combine(profile.WorldDirectory, "world.dat"), working);
        profile.ExecutablePath = Path.Combine(fixture.Data.RootPath, "never-executed.synthetic");
        File.WriteAllText(profile.ExecutablePath, "Synthetic file; no process may execute it.");
        fixture.Data.SaveSettings(settings);
        SaveGroups(fixture.Data, [Membership(fixture.Other.Root, fixture.Other.Roster, Guid.Empty, fixture.Other.Route)]);
        var driver = new OwnerStartBoundaryDriver();
        var manager = new HostManager(fixture.Data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
        var otherBefore = RegisteredGroupJson(fixture.Data, fixture.Other.Root);
        var keyBefore = File.ReadAllBytes(Path.Combine(fixture.Data.RootPath, "shared-world-signing-key.protected"));
        var requests = 0;
        HttpClient ExactWorld(string endpoint, IEnumerable<string> pins)
        {
            Require(endpoint.TrimEnd('/') == world.Route.Address && pins.SequenceEqual([world.Route.CertificateSha256]),
                "first owner Start refreshed an unrelated world or changed a pinned route");
            requests++;
            return new(world.Handler, disposeHandler: false) { BaseAddress = new Uri(world.Route.Address + "/") };
        }
        using var friends = new FriendService(fixture.Data, ExactWorld);
        using var peers = new WorldPeerService(fixture.Data, manager, friends, new PairingService(fixture.Data), ExactWorld);
        var owner = new SimpleOwnerHostingService(fixture.Data, manager, peers,
            () => throw new InvalidOperationException("First original Start must not activate a listener."));
        var result = await owner.StartAsync(profile.Id, default).WaitAsync(TimeSpan.FromSeconds(10));
        Require(result.Code == "SharedHostingReviewRequired" && driver.StartBoundaryAttempts == 1 &&
            fixture.Data.LoadRuns().Count == 0 && RegisteredGroupJson(fixture.Data, fixture.Other.Root) == otherBefore &&
            File.ReadAllBytes(Path.Combine(profile.WorldDirectory, "world.dat")).SequenceEqual(working) &&
            File.ReadAllBytes(Path.Combine(fixture.Data.RootPath, "shared-world-signing-key.protected")).SequenceEqual(keyBefore) &&
            fixture.Other.Handler.Challenges == 0 && !fixture.Data.LoadSettings().CompanionListeningEnabled,
            $"first original owner Start failed before its refusing fake driver: {result.Code}; attempts {driver.StartBoundaryAttempts}; target requests {requests}");
        world.VerifyCachedCopy();
        fixture.Other.VerifyCachedCopy();
    }

    private static async Task FirstOwnerWithoutSaveAsync(string parent)
    {
        using var data = new LocalData(Path.Combine(parent, "first-owner-no-save-" + Guid.NewGuid().ToString("N")));
        var profile = new ServerProfile
        {
            Kind = GameKinds.Fixture,
            Name = "Synthetic first owner",
            ServerName = "Synthetic first owner",
            WorldId = "InitialOwner",
            WorldDirectory = Path.Combine(data.RootPath, "synthetic-world"),
            ExecutablePath = Path.Combine(data.RootPath, "never-executed.synthetic")
        };
        Directory.CreateDirectory(profile.WorldDirectory);
        File.WriteAllText(Path.Combine(profile.WorldDirectory, "world.dat"), "Initial synthetic world.");
        File.WriteAllText(profile.ExecutablePath, "Synthetic file; no process may execute it.");
        data.SaveSettings(new() { Profiles = [profile] });
        var driver = new OwnerStartBoundaryDriver();
        var manager = new HostManager(data, new GameServerRegistry([driver], PortProbeMode.ObserveOnly));
        var sharing = await manager.SetSimpleSharedHostingAsync(profile.Id, true, default);
        Require(sharing.Ok, $"synthetic first owner sharing could not be enabled: {sharing.Code}; {sharing.Message}");
        var roster = await manager.PeerPublishedRosterAsync(profile.Id) ?? throw new InvalidOperationException("Missing synthetic signed roster.");
        var root = new SharedWorldPeerRoot(roster.OwnerPublicKey, profile.Id, roster.GroupId);
        var keyBefore = File.ReadAllBytes(Path.Combine(data.RootPath, "shared-world-signing-key.protected"));
        var identityBefore = OwnerIdentityFiles(data);
        HttpClient NoNetwork(string _, IEnumerable<string> __) => throw new InvalidOperationException("First local registration cannot make an outbound request.");
        using var friends = new FriendService(data, NoNetwork);
        using var peers = new WorldPeerService(data, manager, friends, new PairingService(data), NoNetwork);
        Require(!data.HasProtected("world-peer-memberships.protected") && await manager.PeerPublishedVersionAsync(root) is null,
            "initial owner fixture already had a membership or save publication");
        var owner = new SimpleOwnerHostingService(data, manager, peers,
            () => throw new InvalidOperationException("First local registration cannot activate a listener."));
        var result = await owner.StartAsync(profile.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
        Require(result.Code == "SharedHostingReviewRequired" && driver.StartBoundaryAttempts == 1 &&
            data.LoadRuns().Count == 0 && !data.LoadSettings().CompanionListeningEnabled &&
            File.ReadAllBytes(Path.Combine(data.RootPath, "shared-world-signing-key.protected")).SequenceEqual(keyBefore),
            $"first owner Start without a save publication missed the final refusing fake driver: {result.Code}");
        // Ordinary latest-save observation may add peer announcements. The
        // published roster, source binding and retained signing key stay intact.
        foreach (var item in identityBefore.Where(item => !item.Key.Contains("world-peers", StringComparison.Ordinal)))
            Require(File.Exists(Path.Combine(data.RootPath, item.Key)) &&
                File.ReadAllBytes(Path.Combine(data.RootPath, item.Key)).SequenceEqual(item.Value),
                "initial owner Start changed a pre-existing signed source record");
    }

    private static string RegisteredGroupJson(LocalData data, SharedWorldPeerRoot root)
    {
        using var registry = JsonDocument.Parse(data.LoadProtected("world-peer-memberships.protected")!);
        return registry.RootElement.GetProperty("groups").EnumerateArray().Single(group =>
            group.GetProperty("root").GetProperty("profileId").GetGuid() == root.ProfileId).GetRawText();
    }

    private static Dictionary<string, byte[]> OwnerIdentityFiles(LocalData data)
    {
        var root = Path.Combine(data.RootPath, "shared-worlds");
        var files = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : [];
        // Peer journals, roster floors/indexes and the retained owner key live
        // beside the signed world tree, rather than inside it.
        var histories = Directory.EnumerateFiles(data.RootPath, "shared-*.protected*");
        var consent = Path.Combine(data.RootPath, WorldPeerReceiveConsentStore.StateName);
        return files.Concat(histories).Concat(File.Exists(consent) ? [consent] : []).ToDictionary(
            path => Path.GetRelativePath(data.RootPath, path), File.ReadAllBytes);
    }

    private static void RequireOwnerIdentityFiles(LocalData data, IReadOnlyDictionary<string, byte[]> before)
    {
        var after = OwnerIdentityFiles(data);
        Require(before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out var bytes) && bytes.SequenceEqual(item.Value)),
            "owner membership registration changed a retained signing key or signed source/history file");
    }

    private sealed class OwnerStartBoundaryDriver : IGameServerDriver
    {
        internal int StartBoundaryAttempts { get; private set; }
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "File-only owner Start boundary";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile)
        { StartBoundaryAttempts++; throw NoProcess(); }
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
        private static InvalidOperationException NoProcess() => new("No process operation is permitted.");
    }
}
