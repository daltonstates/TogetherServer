using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// File-only preparation with an adapter that refuses every process operation.
// These checks create no app, game, listener, window or console attachment.
internal static class LocalHostingChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string root)
    {
        Inputs();
        using var fixture = new Fixture(root, granted: true);
        var before = await fixture.Service.StatusAsync(fixture.ProfileId);
        Require(before.Allowed && !before.Consented && !before.ReadyForManualStart &&
            !before.NeedsServerFile && !before.NeedsPassword && before.BranchHash is null &&
            fixture.Data.LoadSettings().CompanionEndpoint == "" && fixture.Driver.ProcessAttempts == 0,
            "passive status lost signed consent-off eligibility, required an unnecessary input, or changed local setup");
        var noConsent = await fixture.Service.PrepareAsync(fixture.ProfileId, new(false, true));
        Require(!noConsent.Ok && !noConsent.Consented && noConsent.Code == "LocalConsentRequired" &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 0,
            "preparation silently consented or declared a history without local approval");
        var noWarning = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, false));
        Require(!noWarning.Ok && noWarning.Code == "SplitWarningRequired" &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 0,
            "a fresh continuation bypassed its separate-history warning");
        var prepared = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(prepared.Ok && prepared.ReadyForManualStart && !prepared.Running &&
            prepared.LocalProfileId is not null && prepared.BranchHash is not null &&
            prepared.GamePort != fixture.ControlPort && prepared.Steps.Take(4).All(step => step.State == "Ready") &&
            prepared.Steps[4].State == "Unknown" && !fixture.Data.LoadSettings().CompanionListeningEnabled &&
            !fixture.Data.LoadSettings().RemoteControlsEnabled && fixture.Data.LoadSettings().CompanionBindAddress == "127.0.0.1" &&
            fixture.Data.LoadSettings().CompanionEndpoint == $"https://127.0.0.1:{fixture.ControlPort}" &&
            fixture.Data.LoadRuns().Count == 0 && fixture.Driver.ProcessAttempts == 0,
            $"offline verified preparation or honest route/port evidence failed: {prepared.Code}; " +
            $"ok={prepared.Ok}, ready={prepared.ReadyForManualStart}, running={prepared.Running}, " +
            $"profile={prepared.LocalProfileId.HasValue}, branch={prepared.BranchHash is not null}, " +
            $"ports={prepared.GamePort}/{prepared.ControlPort}/{fixture.ControlPort}, " +
            $"steps={string.Join(',', prepared.Steps.Select(step => step.Key + ':' + step.State))}; {prepared.Message}");
        var passive = await fixture.Service.StatusAsync(fixture.ProfileId);
        Require(passive.ReadyForManualStart && !passive.Running && passive.LocalProfileId == prepared.LocalProfileId &&
            passive.Steps.Take(4).All(step => step.State == "Ready") && fixture.Data.LoadRuns().Count == 0 &&
            fixture.Driver.ProcessAttempts == 0, "passive prepared status lost readiness or attempted a process operation");
        var repeated = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(repeated.Ok && repeated.LocalProfileId == prepared.LocalProfileId && repeated.BranchHash == prepared.BranchHash &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 1 && fixture.Driver.ProcessAttempts == 0,
            "a repeated preparation created another signed branch or tried to start a process");
        var branch = new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Single();
        var routeIdentity = new LocalHostingRouteSnapshot(branch.Offer.Proposal.CandidateAddress,
            fixture.ControlPort, "0.0.0.0", true, true, CompanionListenerStates.Listening,
            branch.Offer.CandidateTlsFingerprint, true);
        var routeEvidence = new LocalHostingRouteEvidence("NotChecked", "Unknown", "Synthetic current identity",
            DateTimeOffset.UtcNow, routeIdentity);
        Require(FriendLocalHostingService.CurrentSignedControlRoute(true, branch, routeEvidence) &&
            !FriendLocalHostingService.CurrentSignedControlRoute(false, branch, routeEvidence),
            "a matching current listener lost a real signed route proof or fabricated one");
        foreach (var changed in new[] { routeIdentity with { Enabled = false }, routeIdentity with { Active = false },
            routeIdentity with { ListenerOwned = false }, routeIdentity with { CertificateFingerprint = new string('B', 64) },
            routeIdentity with { Endpoint = "https://192.0.2.88:5131" } })
            Require(!FriendLocalHostingService.CurrentSignedControlRoute(true, branch, routeEvidence with { Identity = changed }),
                "an old signed route proof stayed Checked after listener or identity changed");
        Require((await fixture.Manager.StartAsync(prepared.LocalProfileId!.Value)).Code == "SeparateCopyManualStartRequired" &&
            fixture.Driver.ProcessAttempts == 0, "ordinary Start bypassed the explicit local-branch gate");

        var profile = fixture.Data.LoadSettings().Profiles.Single(item => item.Id == prepared.LocalProfileId);
        var localSave = Path.Combine(profile.WorldDirectory, "world.dat");
        var advanced = Encoding.UTF8.GetBytes("local progress which must never be reset by preparation");
        File.WriteAllBytes(localSave, advanced);
        fixture.AdvanceSource();
        var afterSourceAdvance = await fixture.Service.PrepareAsync(fixture.ProfileId, new(true, true));
        Require(!afterSourceAdvance.Ok && !afterSourceAdvance.ReadyForManualStart &&
            afterSourceAdvance.LocalProfileId == prepared.LocalProfileId &&
            afterSourceAdvance.BranchHash == prepared.BranchHash &&
            File.ReadAllBytes(localSave).SequenceEqual(advanced) &&
            new SharedWorldSeparateCopyStore(fixture.Data).Read(fixture.ProfileId).Count == 1 &&
            fixture.Data.LoadSettings().Profiles.Count == 1 && fixture.Driver.ProcessAttempts == 0,
            "preparation overwrote local progress, selected a newer foreign head, or created a second branch");
        var failedPassive = await fixture.Service.StatusAsync(fixture.ProfileId);
        Require(!failedPassive.ReadyForManualStart && failedPassive.LocalProfileId == prepared.LocalProfileId &&
            failedPassive.BranchHash == prepared.BranchHash && File.ReadAllBytes(localSave).SequenceEqual(advanced),
            "passive polling revived a preparation known to have failed or changed the retained world");
        var blockedStart = await fixture.Manager.StartSeparateCopyAsync(profile.Id, prepared.BranchHash!);
        Require(!blockedStart.Ok && fixture.Driver.ProcessAttempts == 0,
            "manual Start trusted the cheap status instead of rechecking altered world bytes");

        using var denied = new Fixture(root, granted: false);
        var rejected = await denied.Service.PrepareAsync(denied.ProfileId, new(true, true));
        Require(!rejected.Ok && !rejected.Allowed && !rejected.Consented &&
            denied.Data.LoadSettings().CompanionEndpoint == "" && denied.Data.LoadSettings().Profiles.Count == 0 &&
            denied.Driver.ProcessAttempts == 0, "an ungranted PC received hosting, local consent, a profile or identity setup");
    }

    private static void Inputs()
    {
        var valid = FriendLocalHostingService.ParseRequest(Encoding.UTF8.GetBytes(
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true}"));
        Require(valid.AllowReceiving && valid.AcceptSplitWarning && valid.ServerFile is null && valid.GamePassword is null,
            "fixed preparation choices did not decode");
        foreach (var input in new[]
        {
            "{}", "null", "[]", "{\"allowReceiving\":true}",
            "{\"allowReceiving\":true,\"allowReceiving\":false,\"acceptSplitWarning\":true}",
            "{\"AllowReceiving\":true,\"acceptSplitWarning\":true}",
            "{\"allowReceiving\":\"true\",\"acceptSplitWarning\":true}",
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"command\":\"anything\"}",
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"serverFile\":\"relative.exe\"}",
            "{\"allowReceiving\":true,\"acceptSplitWarning\":true,\"gamePassword\":\"bad\"}"
        }) Reject(() => FriendLocalHostingService.ParseRequest(Encoding.UTF8.GetBytes(input)), "invalid preparation input was accepted");
        var hash = new string('A', 64);
        Require(FriendLocalHostingRoutes.ParseStart(Encoding.UTF8.GetBytes(
            $"{{\"branchHash\":\"{hash}\",\"acceptSplitWarning\":true}}")).AcceptSplitWarning,
            "exact local Start did not decode");
        foreach (var input in new[] { "{}", "{\"branchHash\":\"bad\",\"acceptSplitWarning\":true}",
            $"{{\"branchHash\":\"{hash}\",\"acceptSplitWarning\":true,\"localProfileId\":\"other\"}}",
            $"{{\"branchHash\":\"{hash}\",\"branchHash\":\"{hash}\",\"acceptSplitWarning\":true}}" })
            Reject(() => FriendLocalHostingRoutes.ParseStart(Encoding.UTF8.GetBytes(input)), "invalid exact Start input was accepted");
        var context = new DefaultHttpContext();
        Require(!FriendLocalHostingRoutes.Fixed(context, true), "sensitive hosting GET omitted its local header");
        context.Request.Headers["X-TogetherServer-Local"] = "1";
        Require(FriendLocalHostingRoutes.Fixed(context, true), "fixed local GET was refused");
        context.Request.QueryString = new("?path=anything");
        Require(!FriendLocalHostingRoutes.Fixed(context), "hosting accepted arbitrary query input");
        context.Request.QueryString = QueryString.Empty;
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        Require(!FriendLocalHostingRoutes.Fixed(context, true), "hosting GET accepted a request body");
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }

    private sealed class Fixture : IDisposable
    {
        internal readonly LocalData Data;
        internal readonly Guid ProfileId = Guid.NewGuid();
        internal readonly Guid GroupId = Guid.NewGuid();
        internal readonly Guid DeviceId = Guid.NewGuid();
        internal readonly int ControlPort;
        internal readonly FilesOnlyDriver Driver = new();
        internal readonly HostManager Manager;
        internal readonly FriendLocalHostingService Service;
        private readonly FriendService friends;
        private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa member = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string vault;
        private SharedWorldVersion head;

        internal Fixture(string root, bool granted)
        {
            Data = new(Path.Combine(root, "local-hosting-" + Guid.NewGuid().ToString("N")));
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var occupied = properties.GetActiveTcpListeners().Concat(properties.GetActiveUdpListeners()).Select(item => item.Port).ToHashSet();
            ControlPort = Enumerable.Range(50000, 8000).First(port => Enumerable.Range(port, 4).All(item => !occupied.Contains(item)));
            Data.SaveSettings(new HostSettings { CompanionPort = ControlPort, Profiles = [] });
            var draft = new SharedWorldRoster(3, GroupId, ProfileId, 1, 1, true, Public(owner),
                [new(DeviceId, Public(member), new(Receive: granted, EligibleHost: granted), false)], "",
                SignerDeviceId: Guid.Empty, SignerPublicKey: Public(owner));
            var roster = draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            Data.SaveProtected($"shared-world-pc-signing-{DeviceId:N}.protected", member.ExportPkcs8PrivateKey());
            Data.SaveProtected($"shared-world-roster-{DeviceId:N}-{ProfileId:N}.protected", JsonSerializer.SerializeToUtf8Bytes(roster, Json));
            vault = Path.Combine(Data.RootPath, "received-shared-worlds", DeviceId.ToString("N"), ProfileId.ToString("N"));
            var bytes = Encoding.UTF8.GetBytes("complete signed source bytes");
            head = Version(1, null, bytes);
            WriteCopy(head, bytes);
            Data.SaveProtected("friend.protected", JsonSerializer.SerializeToUtf8Bytes(new FriendConfiguration
            {
                DeviceId = DeviceId, DisplayName = "Synthetic local hosting", Endpoint = "https://192.0.2.10:5131",
                Fingerprint = new string('A', 64), Credential = new string('C', 64),
                CredentialExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
                ApprovedSharedWorldGroups = new() { [ProfileId] = GroupId },
                SharedWorldSigningKeys = new() { [ProfileId] = Public(owner) },
                SharedRosterFloors = new() { [ProfileId] = new(GroupId, 1, 1, roster.Signature) },
                LastSharedHostManifests = new() { [ProfileId] = head }
            }, Json));
            var executable = Path.Combine(Data.RootPath, "never-run.synthetic");
            File.WriteAllText(executable, "file boundary only; never executable");
            Manager = new(Data, new GameServerRegistry([Driver], PortProbeMode.ObserveOnly));
            friends = new(Data, (_, _) => throw new InvalidOperationException("Network access is forbidden in file-only preparation checks."));
            Service = new(Data, friends, Manager, _ => [executable]);
        }

        internal void AdvanceSource()
        {
            var bytes = Encoding.UTF8.GetBytes("a later source history, distinct from local progress");
            head = Version(head.Number + 1, head.VersionHash, bytes);
            WriteCopy(head, bytes);
        }
        private SharedWorldVersion Version(long number, string? parent, byte[] bytes) => SharedWorldService.SignVersion(new(4,
            GroupId, number, parent, ProfileId, GameKinds.Fixture, "SyntheticLocalHosting", DateTimeOffset.UtcNow,
            SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(), new(ControlPort, false, "Unknown", [], []),
            [new("world.dat", bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)))], "", "", ""), owner);
        private void WriteCopy(SharedWorldVersion version, byte[] bytes)
        {
            var directory = Path.Combine(vault, version.VersionHash);
            Directory.CreateDirectory(Path.Combine(directory, SharedWorldService.PayloadDirectory));
            File.WriteAllBytes(Path.Combine(directory, SharedWorldService.PayloadDirectory, "world.dat"), bytes);
            File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
            FriendLink.KeepSignedManifest(vault, version);
            File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, Json));
        }
        public void Dispose() { friends.Dispose(); Data.Dispose(); owner.Dispose(); member.Dispose(); }
    }

    internal sealed class FilesOnlyDriver : IGameServerDriver
    {
        internal int ProcessAttempts { get; private set; }
        public string Kind => GameKinds.Fixture;
        public string DisplayName => "Source-only preparation";
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
        public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [new("UDP", profile.GamePort, "Game"), new("UDP", profile.GamePort + 1, "Query")];
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => null;
        private InvalidOperationException NoProcess() { ProcessAttempts++; return new("No process operation is permitted."); }
        public void PrepareStart(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run) => throw NoProcess();
        public GameHealthResult Health(ManagedRun run) => throw NoProcess();
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => throw NoProcess();
    }

    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
