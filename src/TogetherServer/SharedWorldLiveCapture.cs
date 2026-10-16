using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

// Only a trusted, exact managed-run adapter may construct this evidence. No
// HTTP or UI surface accepts one. Real adapters require protected exact-scope
// acceptance after an explicit private candidate test.
internal sealed record LiveSaveCompletionEvidence(Guid ProfileId, Guid OperationId,
    int ProcessId, long StartTimeUtcTicks, LiveSaveEvidence Kind,
    DateTimeOffset CompletedUtc, bool Complete);

internal sealed record LiveSavePublicationApproval(Guid CaptureId, Guid OperationId,
    bool Approved);

internal sealed record LiveSaveCaptureManifest(int Schema, Guid CaptureId,
    Guid ProfileId, Guid OperationId, int ProcessId, long StartTimeUtcTicks,
    string Game, string WorldId, string SourceDirectory, LiveSaveEvidence EvidenceKind,
    DateTimeOffset CapturedUtc, IReadOnlyList<SharedWorldFile> Files,
    string SetupSha256, string SigningPublicKey, string Signature);

internal sealed partial class SharedWorldService
{
    private const string LiveCapturesDirectory = "shared-live-captures";
    private const int MaximumPendingLiveCaptures = 8;
    private const string QuarantinedLiveDirectory = ".quarantined-live";
    private const int MaximumQuarantinedLiveVersions = 8;
    // The only test override is for the synthetic fixture game. Real games
    // require the protected profile/installation predicate at publication.
    internal bool FixtureLiveCaptureAcceptedForChecks { get; set; }
    internal GameServerRegistry? LiveGameRegistryForChecks { get; set; }
    internal Action? AfterLiveSourceScanForChecks { get; set; }
    internal Func<ManagedRun, bool>? FixtureLiveRunIdentityForChecks { get; set; }
    internal Func<long>? FixtureLiveAvailableBytesForChecks { get; set; }
    internal Action? AfterLiveSignedDirectoryForChecks { get; set; }
    private GameServerRegistry? stagingFixtureRegistry;
    private GameServerRegistry? managedLiveSnapshotRegistry;
    private Func<ServerProfile, bool>? protectedLiveSaveAcceptance;
    internal void EnableStagingLiveFixture(GameServerRegistry registry) => stagingFixtureRegistry = registry;
    internal void ConfigureManagedLiveSnapshots(GameServerRegistry registry) => managedLiveSnapshotRegistry = registry;
    internal void ConfigureManagedLiveSnapshots(GameServerRegistry registry, Func<ServerProfile, bool> acceptance)
    {
        managedLiveSnapshotRegistry = registry;
        protectedLiveSaveAcceptance = acceptance;
    }

    private bool LiveCaptureAccepted(ServerProfile profile) =>
        profile.Kind == GameKinds.Fixture ? FixtureLiveCaptureAcceptedForChecks || stagingFixtureRegistry is not null :
            SharedWorldLiveSaveAdapters.ForGame(profile.Kind) is not null && protectedLiveSaveAcceptance?.Invoke(profile) == true;

    // A test is deliberately outside normal signed publication. Its kind is
    // covered by both signatures and VerifySignature rejects it categorically,
    // even if a caller strips the descriptor's TestOnly flag.
    internal static SharedWorldVersion SignAcceptanceTestVersion(SharedWorldVersion draft, ECDsa key)
    {
        var unsigned = draft with
        {
            Schema = 5, Number = 1, ParentHash = null,
            CaptureKind = LiveSaveAcceptanceTrust.TestCaptureKind,
            SigningPublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), VersionHash = "", Signature = ""
        };
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(VersionBasis(unsigned.GroupId,
            unsigned.Number, unsigned.ParentHash, unsigned.ProfileId, unsigned.Game, unsigned.WorldId,
            unsigned.CreatedUtc, unsigned.CaptureKind, unsigned.BackupId, unsigned.PortableSetup,
            unsigned.Files, unsigned.SigningPublicKey, unsigned.Schema)));
        var signed = unsigned with { VersionHash = Convert.ToHexString(hash), Signature = Convert.ToBase64String(key.SignHash(hash)) };
        if (!VerifyAcceptanceTestVersion(signed)) throw new InvalidDataException("The private test manifest could not be signed.");
        return signed;
    }

    internal static bool VerifyAcceptanceTestVersion(SharedWorldVersion version)
    {
        try
        {
            if (version.Schema != 5 || version.CaptureKind != LiveSaveAcceptanceTrust.TestCaptureKind ||
                version.Number != 1 || version.ParentHash is not null || version.BackupId == Guid.Empty ||
                version.GroupId == Guid.Empty || version.ProfileId == Guid.Empty || version.CreatedUtc == default ||
                version.WorldId is not { Length: > 0 and <= 160 } ||
                SharedWorldLiveSaveAdapters.ForGame(version.Game) is null ||
                version.Files is null || version.Files.Count is < 1 or > MaximumFiles ||
                version.PortableSetup is null || !SharedWorldPortableSetupReader.Valid(version.Game, version.PortableSetup, 5) ||
                version.Files.Any(file => file is null || !SafePath(file.Path) || file.Length < 0 ||
                    file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit)) ||
                version.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != version.Files.Count)
                return false;
            _ = BoundedTotalBytes(version.Files);
            var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(VersionBasis(version.GroupId,
                version.Number, version.ParentHash, version.ProfileId, version.Game, version.WorldId,
                version.CreatedUtc, version.CaptureKind, version.BackupId, version.PortableSetup,
                version.Files, version.SigningPublicKey, version.Schema)));
            using var key = ECDsa.Create();
            var publicKey = Convert.FromBase64String(version.SigningPublicKey);
            key.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            return consumed == publicKey.Length && key.KeySize == 256 &&
                version.VersionHash == Convert.ToHexString(hash) && key.VerifyHash(hash, Convert.FromBase64String(version.Signature));
        }
        catch (Exception ex) when (LiveSaveAcceptanceTrust.ExpectedFailure(ex)) { return false; }
    }

    internal (LiveSaveTestArtifact Artifact, string SealedPayload) PrepareAcceptanceTestArtifact(
        ServerProfile profile, ManagedRun run, ImmutableLiveSaveSnapshot snapshot, LiveSaveAcceptanceScope scope, CancellationToken token)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                if (LiveSaveAcceptanceTrust.IsRecoveryProfile(profile))
                    return PrepareBranchAcceptanceTestArtifact(profile, run, snapshot, scope, token);
                var captureId = snapshot.SnapshotId;
                if (!SharedWorldLiveSaveAdapters.SameRealInstallation(scope, scope) ||
                    scope.ProfileId != profile.Id || scope.Game != profile.Kind ||
                    Authority.GovernanceUnresolved(profile.Id) ||
                    Authority.HasState(profile.Id) && Authority.LocalAuthorizedHead(profile.Id) is null ||
                    !AuthorizedPublishedLineage(profile) ||
                    !new ManagedLiveSnapshotStore(data, managedLiveSnapshotRegistry ?? new GameServerRegistry(data))
                        .Verify(profile, run, snapshot, token)) throw new InvalidDataException("Review this test's installed scope, immutable seal and source authority.");
                var binding = ReadBinding(profile.Id);
                var roster = ReadRoster(profile);
                if (!profile.SharedSavesEnabled || binding is null || !BindingMatches(binding, profile) || roster is null)
                    throw new InvalidDataException("The signed shared source changed before the private test.");
                var (capture, setup, payload) = ReadVerifiedLiveCapture(profile, captureId, token);
                if (capture.OperationId != run.OperationId ||
                    !RecordedExactLiveRun(profile, capture.OperationId, capture.ProcessId, capture.StartTimeUtcTicks, requireReady: false))
                    throw new InvalidDataException("The exact test run changed.");
                using var key = LoadPublishingKey(profile.Id);
                var version = SignAcceptanceTestVersion(new(5, binding.GroupId, 1, null, profile.Id,
                    profile.Kind, profile.WorldId, capture.CapturedUtc, LiveSaveAcceptanceTrust.TestCaptureKind,
                    captureId, SharedWorldPortableSetupReader.Capture(setup), capture.Files, "", "", ""), key);
                var sourceHead = Authority.LocalAuthorizedHead(profile.Id);
                var sourceDevice = version.SigningPublicKey == roster.OwnerPublicKey ? Guid.Empty :
                    sourceHead?.Proposal.SuccessorBinding?.DeviceId ?? sourceHead?.SuccessorReceipt?.DeviceId ??
                    roster.Members.SingleOrDefault(member => member.PublicKey == version.SigningPublicKey)?.DeviceId ??
                    throw new InvalidDataException("The private test's source hosting key has no enrolled device binding.");
                var unsigned = new LiveSaveTestArtifact(1, true, captureId, scope, run.OperationId,
                    sourceDevice, capture.CapturedUtc, snapshot.CapturedUtc,
                    LiveSaveAcceptanceTrust.PayloadHash(version.Files), LiveSaveAcceptanceTrust.TotalBytes(version.Files),
                    ChunkBytes, LiveSaveAcceptanceTrust.ChunkCount(version.Files), version, "");
                var artifact = unsigned with { Signature = Convert.ToBase64String(key.SignData(
                    LiveSaveAcceptanceTrust.ArtifactBasis(unsigned), HashAlgorithmName.SHA256)) };
                if (!LiveSaveAcceptanceTrust.VerifyArtifact(artifact)) throw new InvalidDataException();
                return (artifact, payload);
            }
    }

    internal bool LiveAcceptanceSourceMatches(ServerProfile profile)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
          lock (sync)
        {
            if (LiveSaveAcceptanceTrust.IsRecoveryProfile(profile))
            {
                _ = SharedWorldBranchLiveSource.Resolve(data, profile);
                return true;
            }
            var binding = ReadBinding(profile.Id);
            return binding is not null && BindingMatches(binding, profile) && ReadRoster(profile) is not null;
        }
    }

    internal bool AcceptanceTestSourceMatches(ServerProfile profile, LiveSaveTestArtifact artifact, DateTimeOffset? now = null)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
          lock (sync)
        {
            return LiveSaveAcceptanceTrust.MatchesSource(artifact, ResolveLiveSaveSource(profile, now), profile, now ?? DateTimeOffset.UtcNow);
        }
    }

    internal LiveSaveSourceContext ResolveLiveSaveSource(ServerProfile profile, DateTimeOffset? now = null,
        bool requireStarted = false)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
          lock (sync)
        {
            if (LiveSaveAcceptanceTrust.IsRecoveryProfile(profile))
                return SharedWorldBranchLiveSource.Resolve(data, profile, now, requireStarted);
            var binding = ReadBinding(profile.Id);
            var roster = ReadRoster(profile);
            if (!profile.SharedSavesEnabled || binding is null || !BindingMatches(binding, profile) || roster is null ||
                roster.ProfileId != profile.Id || roster.GroupId != binding.GroupId)
                throw new InvalidDataException("The signed shared source is missing or changed.");
            using var key = LoadPublishingKey(profile.Id);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var head = Authority.LocalAuthorizedHead(profile.Id);
            var deviceId = publicKey == roster.OwnerPublicKey ? Guid.Empty :
                head?.Proposal.SuccessorBinding?.DeviceId ?? head?.SuccessorReceipt?.DeviceId ??
                roster.Members.SingleOrDefault(member => member.PublicKey == publicKey)?.DeviceId ??
                throw new InvalidDataException("The current hosting key has no enrolled source device binding.");
            return new(profile.Id, new(roster.OwnerPublicKey, profile.Id, binding.GroupId), null,
                deviceId, publicKey, roster);
        }
    }

    private (LiveSaveTestArtifact Artifact, string SealedPayload) PrepareBranchAcceptanceTestArtifact(
        ServerProfile profile, ManagedRun run, ImmutableLiveSaveSnapshot snapshot,
        LiveSaveAcceptanceScope scope, CancellationToken token)
    {
        var source = ResolveLiveSaveSource(profile, requireStarted: true);
        var snapshots = new ManagedLiveSnapshotStore(data, managedLiveSnapshotRegistry ?? new GameServerRegistry(data));
        if (!SharedWorldLiveSaveAdapters.SameRealInstallation(scope, scope) || scope.ProfileId != profile.Id ||
            scope.Game != profile.Kind || !snapshots.Verify(profile, run, snapshot, token) ||
            !OwnsRecoveryLiveSource(profile, ReviewedLiveSource(profile, run)))
            throw new InvalidDataException("The recovery test requires its exact installed run and protected immutable snapshot.");
        var setup = ServerSetupSnapshots.Read(profile, ServerSetupSnapshots.Capture(profile, data));
        var bytes = data.LoadProtected(WorldAuthorityStore.HostingKeyName(source.Root.ProfileId)) ??
            throw new InvalidDataException("The recovery publishing key is missing.");
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(bytes, out var consumed);
        if (consumed != bytes.Length || Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != source.PublishingPublicKey)
            throw new InvalidDataException("The recovery publishing key changed.");
        var version = SignAcceptanceTestVersion(new(5, source.Root.GroupId, 1, null, source.Root.ProfileId,
            profile.Kind, profile.WorldId, snapshot.Completion.CompletedUtc, LiveSaveAcceptanceTrust.TestCaptureKind,
            snapshot.SnapshotId, SharedWorldPortableSetupReader.Capture(setup), snapshot.Files, "", "", ""), key);
        var draft = new LiveSaveTestArtifact(2, true, snapshot.SnapshotId, scope, run.OperationId,
            source.SourceDeviceId, snapshot.Completion.CompletedUtc, snapshot.CapturedUtc,
            LiveSaveAcceptanceTrust.PayloadHash(version.Files), LiveSaveAcceptanceTrust.TotalBytes(version.Files),
            ChunkBytes, LiveSaveAcceptanceTrust.ChunkCount(version.Files), version, "", source.Branch);
        var artifact = draft with { Signature = Convert.ToBase64String(key.SignData(
            LiveSaveAcceptanceTrust.ArtifactBasis(draft), HashAlgorithmName.SHA256)) };
        token.ThrowIfCancellationRequested();
        if (!LiveSaveAcceptanceTrust.MatchesSource(artifact, source, profile, DateTimeOffset.UtcNow) ||
            !SharedWorldBranchLiveSource.SameSource(source, ResolveLiveSaveSource(profile, requireStarted: true)) ||
            !snapshots.Verify(profile, run, snapshot, token))
            throw new InvalidDataException("The sealed recovery test or its current signed source changed.");
        return (artifact, snapshot.SnapshotDirectory);
    }

    internal bool OwnsRecoveryLiveSource(ServerProfile profile, string source) =>
        SharedWorldBranchLiveSource.IsOwnedSaveSource(data, profile, source);

    private string LiveCaptureProfileRoot(Guid profileId)
    {
        var root = Path.Combine(data.RootPath, LiveCapturesDirectory, profileId.ToString("N"));
        EnsureUnlinkedRoot(data.RootPath, root);
        return root;
    }

    private string LiveCaptureDirectory(Guid profileId, Guid captureId) =>
        Path.Combine(LiveCaptureProfileRoot(profileId), captureId.ToString("N"));

    private string ReviewedLiveSource(ServerProfile profile, ManagedRun run)
    {
        var games = profile.Kind == GameKinds.Fixture ? stagingFixtureRegistry ?? LiveGameRegistryForChecks ?? new GameServerRegistry(data) :
            managedLiveSnapshotRegistry ?? new GameServerRegistry(data);
        if (!games.TryGet(profile.Kind, out var driver) ||
            driver.ManagedSaveDirectory(profile) is not { } selected ||
            !Path.IsPathFullyQualified(selected))
            throw new InvalidDataException("The game has no reviewed managed save source.");
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        var world = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.WorldDirectory));
        if (!Path.IsPathFullyQualified(run.WorldDirectory) ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(run.WorldDirectory))
                .Equals(world, StringComparison.OrdinalIgnoreCase) ||
            !Inside(world, source))
            throw new InvalidDataException("The exact run or reviewed save source changed.");
        var managedRoots = new[] { data.ManagedWorldsRoot, data.WorldImportsRoot,
            data.MinecraftInstallRoot, data.FactorioServersRoot };
        if (!managedRoots.Any(root => Inside(root, source) &&
            !Path.GetFullPath(root).Equals(source, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The live save source is outside reviewed managed storage.");
        var owned = LiveSaveAcceptanceTrust.IsRecoveryProfile(profile) ? OwnsRecoveryLiveSource(profile, source) : profile.Kind switch
        {
            GameKinds.Valheim => profile.WorldSource == "New" &&
                world.Equals(data.NewWorldDirectory(profile.Id), StringComparison.OrdinalIgnoreCase) &&
                data.OwnsNewWorld(profile) || profile.WorldSource == "Existing" &&
                ValheimSetup.IsImportedWorld(data, profile.Id, world),
            GameKinds.Terraria or GameKinds.Fixture =>
                world.Equals(data.NewWorldDirectory(profile.Id), StringComparison.OrdinalIgnoreCase),
            GameKinds.Factorio => FactorioSetup.IsImportedCopy(data, profile),
            GameKinds.MinecraftJava or GameKinds.MinecraftBedrock =>
                Path.GetDirectoryName(world)!.Equals(data.MinecraftInstallRoot,
                    StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(world).StartsWith(profile.Kind == GameKinds.MinecraftJava
                    ? "java-" : "bedrock-", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!owned)
            throw new InvalidDataException("The reviewed live save does not belong to this managed profile.");
        EnsureUnlinkedRoot(data.RootPath, source);
        if (!Directory.Exists(source))
            throw new InvalidDataException("The reviewed live save source is missing.");
        return source;
    }

    private static bool Inside(string root, string child)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        return fullChild.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullChild.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private bool ExactLiveRun(ServerProfile profile, ManagedRun run)
    {
        if (profile.Kind == GameKinds.Fixture && FixtureLiveRunIdentityForChecks is not null)
            return FixtureLiveRunIdentityForChecks(run);
        if (run.ProcessId is not > 0 || run.StartTimeUtcTicks is not > 0 ||
            string.IsNullOrWhiteSpace(run.ExecutablePath) ||
            !Path.IsPathFullyQualified(run.ExecutablePath)) return false;
        try
        {
            using var process = Process.GetProcessById(run.ProcessId.Value);
            return !process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                process.MainModule?.FileName is { } executable &&
                Path.GetFullPath(executable).Equals(Path.GetFullPath(run.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private bool RecordedExactLiveRun(ServerProfile profile, Guid operationId,
        int processId, long startTimeUtcTicks, bool requireReady = true)
    {
        var recorded = data.LoadRuns().SingleOrDefault(item =>
            item.ProfileId == profile.Id && item.OperationId == operationId);
        return recorded is not null && recorded.Kind == profile.Kind &&
            recorded.WorldId == profile.WorldId && recorded.ProcessId == processId &&
            recorded.StartTimeUtcTicks == startTimeUtcTicks &&
            recorded.StopRequestedUtc is null &&
            (!requireReady || recorded.WasReady || profile.Kind == GameKinds.Fixture && stagingFixtureRegistry is not null) &&
            !string.IsNullOrWhiteSpace(recorded.WorldDirectory) &&
            !string.IsNullOrWhiteSpace(profile.WorldDirectory) &&
            Path.GetFullPath(recorded.WorldDirectory).Equals(
                Path.GetFullPath(profile.WorldDirectory), StringComparison.OrdinalIgnoreCase) &&
            ExactLiveRun(profile, recorded);
    }

    // The source is derived from the registered driver and app-owned roots;
    // callers cannot supply a path. This scanner handles only exact-run
    // completion. Frozen query file lists and closed archives require typed
    // adapter-owned staging before those evidence kinds may publish.
    // A partial directory is never a capture.
    internal Guid StageLiveCapture(ServerProfile profile, ManagedRun run,
        LiveSaveCompletionEvidence completion, CancellationToken cancellationToken = default, Guid? reservedCaptureId = null)
        => StageLiveCaptureCore(profile, run, completion, null, cancellationToken, reservedCaptureId);

    internal Guid StageLiveCapture(ServerProfile profile, ManagedRun run,
        ImmutableLiveSaveSnapshot snapshot, CancellationToken cancellationToken = default, Guid? reservedCaptureId = null)
    {
        if (snapshot is null || snapshot.Completion is null)
            throw new InvalidDataException("An adapter-owned immutable snapshot is required.");
        return StageLiveCaptureCore(profile, run, snapshot.Completion, snapshot, cancellationToken, reservedCaptureId);
    }

    private static LiveSaveEvidence ExpectedLiveSnapshotEvidence(string game) => game switch
    {
        GameKinds.MinecraftBedrock => LiveSaveEvidence.FrozenSnapshotQuery,
        GameKinds.Factorio => LiveSaveEvidence.ClosedSaveArchive,
        GameKinds.Valheim or GameKinds.MinecraftJava or GameKinds.Terraria or GameKinds.Fixture =>
            LiveSaveEvidence.RunScopedCompletion,
        _ => LiveSaveEvidence.None
    };

    private Guid StageLiveCaptureCore(ServerProfile profile, ManagedRun run,
        LiveSaveCompletionEvidence completion, ImmutableLiveSaveSnapshot? snapshot,
        CancellationToken cancellationToken, Guid? reservedCaptureId)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                // Completion alone permits only the synthetic fixture scanner.
                // Every real game must supply its sealed adapter-owned bytes.
                if (snapshot is null && profile.Kind != GameKinds.Fixture)
                    throw new InvalidDataException("Live save staging requires an immutable snapshot binding.");
                // An accepted real adapter's completed sealed snapshot is the
                // authority for this owner-requested save. It does not create
                // readiness or occupancy evidence for remote/automatic Stop.
                var requireReady = snapshot is null || profile.Kind == GameKinds.Fixture;
                if (!profile.SharedSavesEnabled || profile.Kind == GameKinds.Custom ||
                    run.ProfileId != profile.Id || run.Kind != profile.Kind ||
                    run.WorldId != profile.WorldId || run.OperationId == Guid.Empty ||
                    run.ProcessId is not > 0 || run.StartTimeUtcTicks is not > 0 ||
                    (requireReady && !run.WasReady && stagingFixtureRegistry is null) || run.StopRequestedUtc is not null ||
                    completion is null || !completion.Complete || completion.Kind == LiveSaveEvidence.None ||
                    completion.Kind != ExpectedLiveSnapshotEvidence(profile.Kind) ||
                    completion.ProfileId != run.ProfileId ||
                    completion.OperationId != run.OperationId ||
                    completion.ProcessId != run.ProcessId ||
                    completion.StartTimeUtcTicks != run.StartTimeUtcTicks ||
                    completion.CompletedUtc.UtcTicks < run.StartTimeUtcTicks ||
                    completion.CompletedUtc > DateTimeOffset.UtcNow.AddMinutes(1))
                    throw new InvalidDataException("An exact completed managed-run save is required.");
                if (!RecordedExactLiveRun(profile, run.OperationId,
                        run.ProcessId.Value, run.StartTimeUtcTicks.Value, requireReady))
                    throw new InvalidDataException("The completion does not match the recorded managed run.");
                var source = ReviewedLiveSource(profile, run);
                var snapshotStore = snapshot is null ? null : new ManagedLiveSnapshotStore(data,
                    managedLiveSnapshotRegistry ?? stagingFixtureRegistry ?? LiveGameRegistryForChecks ?? new GameServerRegistry(data));
                if (snapshot is not null && (!string.Equals(snapshot.SourceDirectory, source, StringComparison.OrdinalIgnoreCase) ||
                    !snapshotStore!.Verify(profile, run, snapshot, cancellationToken)))
                    throw new InvalidDataException("The adapter-owned immutable snapshot failed verification.");
                var copySource = snapshot?.SnapshotDirectory ?? source;
                var binding = ReadBinding(profile.Id);
                if (binding is null || !BindingMatches(binding, profile) || ReadRoster(profile) is null)
                    throw new InvalidDataException("The signed shared source is missing or changed.");
                var beforeSetup = ServerSetupSnapshots.Capture(profile, data);
                var before = ServerSetupSnapshots.Read(profile, beforeSetup);
                _ = SharedWorldPortableSetupReader.Capture(before);
                var sourceFiles = snapshot?.Files.ToArray() ?? ScanLiveTree(source, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = BoundedTotalBytes(sourceFiles);
                RequireLiveSpace(bytes, profile.Kind);
                AfterLiveSourceScanForChecks?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                var captureId = reservedCaptureId ?? Guid.NewGuid();
                if (captureId == Guid.Empty || Directory.Exists(LiveCaptureDirectory(profile.Id, captureId)))
                    throw new InvalidDataException("A fresh capture identity is required.");
                var profileRoot = LiveCaptureProfileRoot(profile.Id);
                Directory.CreateDirectory(profileRoot);
                // Crash leftovers have no completion marker and can never be
                // published. Remove only unlinked partial staging directories.
                foreach (var partialLeftover in Directory.EnumerateDirectories(profileRoot, ".partial-*"))
                    TryDeleteStage(partialLeftover);
                if (Directory.EnumerateFileSystemEntries(profileRoot).Count() >= MaximumPendingLiveCaptures)
                    throw new InvalidDataException("Review existing live captures before staging another save.");
                var partial = Path.Combine(profileRoot, ".partial-" + captureId.ToString("N"));
                try
                {
                    Directory.CreateDirectory(partial);
                    var payload = Path.Combine(partial, PayloadDirectory);
                    Directory.CreateDirectory(payload);
                    foreach (var file in sourceFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var destination = SafeChild(payload, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        CopyLiveFile(SafeChild(copySource, file.Path), destination, cancellationToken);
                        VerifyLiveFile(destination, file, cancellationToken);
                    }
                    if (snapshot is null && !sourceFiles.SequenceEqual(ScanLiveTree(source, cancellationToken)))
                        throw new InvalidDataException("The live save source changed during capture.");
                    if (snapshot is not null && !snapshotStore!.Verify(profile, run, snapshot, cancellationToken))
                        throw new InvalidDataException("The adapter-owned immutable snapshot changed during staging.");
                    if (!RecordedExactLiveRun(profile, run.OperationId,
                            run.ProcessId.Value, run.StartTimeUtcTicks.Value, requireReady))
                        throw new InvalidDataException("The managed run changed during capture.");
                    var afterSetup = ServerSetupSnapshots.Read(profile,
                        ServerSetupSnapshots.Capture(profile, data));
                    if (!JsonSerializer.SerializeToUtf8Bytes(before, Json).AsSpan().SequenceEqual(
                            JsonSerializer.SerializeToUtf8Bytes(afterSetup, Json)))
                        throw new InvalidDataException("The reviewed setup changed during capture.");
                    File.WriteAllBytes(Path.Combine(partial, "setup.protected"), beforeSetup);
                    using var key = LoadPublishingKey(profile.Id);
                    var unsigned = new LiveSaveCaptureManifest(1, captureId, profile.Id,
                        run.OperationId, run.ProcessId.Value, run.StartTimeUtcTicks.Value,
                        profile.Kind, profile.WorldId, source, completion.Kind,
                        completion.CompletedUtc, sourceFiles,
                        Convert.ToHexString(SHA256.HashData(beforeSetup)),
                        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "");
                    var signed = unsigned with
                    {
                        Signature = Convert.ToBase64String(
                        key.SignData(JsonSerializer.SerializeToUtf8Bytes(unsigned, Json),
                            HashAlgorithmName.SHA256))
                    };
                    var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(signed, Json);
                    if (manifestBytes.Length > MaximumManifestBytes)
                        throw new InvalidDataException("The live capture manifest is too large.");
                    File.WriteAllBytes(Path.Combine(partial, "complete.json"), manifestBytes);
                    VerifyLiveTree(payload, sourceFiles, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Move(partial, LiveCaptureDirectory(profile.Id, captureId));
                    return captureId;
                }
                finally { TryDeleteStage(partial); }
            }
    }

    private static SharedWorldFile[] ScanLiveTree(string root, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The live save source is missing or linked.");
        var files = new List<SharedWorldFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        var directories = 0;
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++directories > 1024)
                throw new InvalidDataException("The live save has too many directories.");
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => path,
                         StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The live save contains a link.");
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (!SafePath(relative))
                    throw new InvalidDataException("The live save contains an invalid path.");
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                if (!File.Exists(entry) || !paths.Add(relative) || files.Count >= MaximumFiles)
                    throw new InvalidDataException("The live save has a duplicate, unsupported or excess file.");
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaximumSharedWorldBytes)
                    throw new InvalidDataException("The live save exceeds the transfer limit.");
                files.Add(new SharedWorldFile(relative, stream.Length,
                    HashLiveFile(stream, cancellationToken)));
            }
        }
        if (files.Count == 0) throw new InvalidDataException("The live save has no complete files.");
        var ordered = files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        BoundedTotalBytes(ordered);
        return ordered;
    }

    private static void VerifyLiveTree(string root, IReadOnlyList<SharedWorldFile> expected, CancellationToken cancellationToken = default)
    {
        var actual = ScanLiveTree(root, cancellationToken);
        if (!actual.SequenceEqual(expected))
            throw new InvalidDataException("The staged live save payload changed.");
    }

    private void RequireLiveSpace(long size, string game)
    {
        const long reserve = 1024L * 1024 * 1024;
        var drive = new DriveInfo(Path.GetPathRoot(data.RootPath)!);
        var available = drive.AvailableFreeSpace;
        if (game == GameKinds.Fixture && FixtureLiveAvailableBytesForChecks is not null)
            available = Math.Min(available, FixtureLiveAvailableBytesForChecks());
        if (available < reserve || available - reserve < size)
            throw new IOException("Keep 1 GiB free after staging the live save.");
    }

    private (LiveSaveCaptureManifest Capture, ServerSetupSnapshot Setup, string PayloadRoot)
        ReadVerifiedLiveCapture(ServerProfile profile, Guid captureId, CancellationToken cancellationToken = default)
    {
        if (captureId == Guid.Empty) throw new InvalidDataException("The live capture ID is missing.");
        var root = LiveCaptureDirectory(profile.Id, captureId);
        EnsureUnlinkedRoot(data.RootPath, root);
        if (!Directory.Exists(root)) throw new InvalidDataException("The completed live capture is missing.");
        var marker = SafeChild(root, "complete.json");
        var setupPath = SafeChild(root, "setup.protected");
        if (!File.Exists(marker) || new FileInfo(marker).Length is < 1 or > MaximumManifestBytes ||
            !File.Exists(setupPath) || new FileInfo(setupPath).Length is < 1 or > 3 * 1024 * 1024 + 512)
            throw new InvalidDataException("The live capture or setup checkpoint is incomplete.");
        var capture = JsonSerializer.Deserialize<LiveSaveCaptureManifest>(File.ReadAllBytes(marker), Json);
        if (capture is null || capture.Schema != 1 || capture.CaptureId != captureId ||
            capture.ProfileId != profile.Id || capture.OperationId == Guid.Empty ||
            capture.ProcessId <= 0 || capture.StartTimeUtcTicks <= 0 ||
            capture.Game != profile.Kind || capture.WorldId != profile.WorldId ||
            capture.EvidenceKind == LiveSaveEvidence.None || capture.EvidenceKind != ExpectedLiveSnapshotEvidence(profile.Kind) ||
            capture.CapturedUtc.UtcTicks < capture.StartTimeUtcTicks ||
            capture.Files is null || capture.Files.Count is < 1 or > MaximumFiles ||
            capture.Files.Any(file => !SafePath(file.Path) || file.Length < 0 ||
                file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigit)) ||
            capture.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != capture.Files.Count ||
            capture.SetupSha256 is not { Length: 64 } || !capture.SetupSha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("The live capture manifest is invalid.");
        BoundedTotalBytes(capture.Files);
        using var key = LoadPublishingKey(profile.Id);
        if (capture.SigningPublicKey != Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) ||
            !key.VerifyData(JsonSerializer.SerializeToUtf8Bytes(capture with { Signature = "" }, Json),
                Convert.FromBase64String(capture.Signature), HashAlgorithmName.SHA256))
            throw new InvalidDataException("The live capture seal failed verification.");
        var run = new ManagedRun { WorldDirectory = profile.WorldDirectory };
        if (!ReviewedLiveSource(profile, run).Equals(capture.SourceDirectory,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed live save source changed.");
        var setupBytes = File.ReadAllBytes(setupPath);
        if (!Convert.ToHexString(SHA256.HashData(setupBytes)).Equals(capture.SetupSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException("The staged setup checkpoint changed.");
        var setup = ServerSetupSnapshots.Read(profile, setupBytes);
        _ = SharedWorldPortableSetupReader.Capture(setup);
        var currentSetup = ServerSetupSnapshots.Read(profile,
            ServerSetupSnapshots.Capture(profile, data));
        if (!JsonSerializer.SerializeToUtf8Bytes(setup, Json).AsSpan().SequenceEqual(
                JsonSerializer.SerializeToUtf8Bytes(currentSetup, Json)))
            throw new InvalidDataException("The reviewed setup changed after live capture.");
        var payload = Path.Combine(root, PayloadDirectory);
        VerifyLiveTree(payload, capture.Files, cancellationToken);
        if (Directory.EnumerateFileSystemEntries(root).Count() != 3)
            throw new InvalidDataException("The live capture contains unexpected entries.");
        return (capture, setup, payload);
    }

    // The owner staging fixture action is the only surfaced caller. There is no
    // companion capture action; approval remains tied to this sealed exact run.
    internal SharedWorldResult PublishLiveCapture(ServerProfile profile, Guid captureId,
        LiveSavePublicationApproval approval, CancellationToken cancellationToken = default)
    {
        if (!profile.SharedSavesEnabled || profile.Kind == GameKinds.Custom)
            return new(false, "SharingOff", "Shared saves are off for this server.");
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                string? stage = null;
                try
                {
                    if (!LiveCaptureAccepted(profile))
                        throw new InvalidDataException("Live save publication has not passed game acceptance.");
                    if (!approval.Approved || approval.CaptureId != captureId)
                        throw new InvalidDataException("The exact live capture has no publication approval.");
                    var (capture, setup, payload) = ReadVerifiedLiveCapture(profile, captureId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (approval.OperationId != capture.OperationId)
                        throw new InvalidDataException("The approval belongs to another managed run.");
                    if (!RecordedExactLiveRun(profile, capture.OperationId,
                            capture.ProcessId, capture.StartTimeUtcTicks, requireReady: profile.Kind == GameKinds.Fixture))
                        throw new InvalidDataException("The exact managed process is no longer running.");
                    var successor = Authority.LocalAuthorizedHead(profile.Id);
                    if (Authority.GovernanceUnresolved(profile.Id) ||
                        Authority.HasState(profile.Id) && successor is null ||
                        successor is not null && !AuthorizedPublishedLineage(profile))
                        throw new InvalidDataException("Shared world authority is unresolved.");
                    AfterGovernanceCheckForChecks?.Invoke();
                    var binding = ReadBinding(profile.Id);
                    if (binding is null || !BindingMatches(binding, profile) || ReadRoster(profile) is null)
                        throw new InvalidDataException("The signed shared source is missing or changed.");
                    var priorStatus = Status(profile);
                    if (File.Exists(LatestPath(profile.Id)) && priorStatus.Latest is null)
                        throw new InvalidDataException("The existing shared save pointer failed verification.");
                    var previous = ReconcilePublishedVersion(profile, priorStatus.Latest ?? successor?.Version);
                    if (successor is not null && (previous!.GroupId != successor.Version.GroupId ||
                        previous.Number < successor.Version.Number))
                        throw new InvalidDataException("Published history does not continue the authority head.");
                    if (previous?.BackupId == captureId &&
                        previous.CaptureKind == SharedWorldCaptureKinds.LiveSave)
                        return new(true, "SharedSavePublished", "The approved live capture is already published.", previous);
                    if (previous is not null && capture.CapturedUtc <= previous.CreatedUtc)
                        throw new InvalidDataException("The live capture is older than the published save.");
                    var files = capture.Files.ToArray();
                    var size = BoundedTotalBytes(files);
                    if (previous is not null) PrunePublishedPayloads(previous);
                    RequireLiveSpace(size, profile.Kind);
                    var portable = SharedWorldPortableSetupReader.Capture(setup);
                    using var key = LoadPublishingKey(profile.Id);
                    var draft = new SharedWorldVersion(5, binding.GroupId,
                        previous?.Number + 1 ?? 1, previous?.VersionHash, profile.Id,
                        profile.Kind, profile.WorldId, capture.CapturedUtc,
                        SharedWorldCaptureKinds.LiveSave, captureId, portable, files,
                        "", "", "");
                    var version = SignVersion(draft, key);
                    var versionBytes = JsonSerializer.SerializeToUtf8Bytes(version, Json);
                    if (versionBytes.Length > MaximumManifestBytes)
                        throw new InvalidDataException("The shared save manifest is too large.");
                    var root = Root(profile.Id);
                    Directory.CreateDirectory(root);
                    stage = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stage);
                    var destinationPayload = Path.Combine(stage, PayloadDirectory);
                    Directory.CreateDirectory(destinationPayload);
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var destination = SafeChild(destinationPayload, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        CopyLiveFile(SafeChild(payload, file.Path), destination, cancellationToken);
                        VerifyLiveFile(destination, file, cancellationToken);
                    }
                    VerifyLiveTree(destinationPayload, files, cancellationToken);
                    File.WriteAllBytes(Path.Combine(stage, "version.json"), versionBytes);
                    var destinationRoot = VersionRoot(version);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationRoot)!);
                    if (Directory.Exists(destinationRoot))
                        throw new InvalidDataException("Shared version already exists.");
                    Directory.Move(stage, destinationRoot);
                    stage = null;
                    AfterLiveSignedDirectoryForChecks?.Invoke();
                    VerifyLiveTree(Path.Combine(destinationRoot, PayloadDirectory), files, cancellationToken);
                    var latestStage = LatestPath(profile.Id) + ".new";
                    cancellationToken.ThrowIfCancellationRequested();
                    File.WriteAllBytes(latestStage, versionBytes);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!RecordedExactLiveRun(profile, capture.OperationId,
                            capture.ProcessId, capture.StartTimeUtcTicks, requireReady: profile.Kind == GameKinds.Fixture))
                        throw new InvalidDataException("The exact managed process changed before publication. Keep the signed interrupted copy for review.");
                    if (!LiveCaptureAccepted(profile))
                        throw new InvalidDataException("The protected installed acceptance scope changed before publication. Keep the signed interrupted copy for review.");
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(latestStage, LatestPath(profile.Id), true);
                    // The published payload is verified and now has its own
                    // durable pointer. The private capture can be discarded.
                    TryDeleteStage(LiveCaptureDirectory(profile.Id, captureId));
                    EvictOldChunkHashes(profile.Id, version.VersionHash);
                    try { PrunePublishedPayloads(version); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        InvalidDataException or JsonException)
                    { }
                    try { if (File.Exists(ErrorPath(profile.Id))) File.Delete(ErrorPath(profile.Id)); }
                    catch (IOException) { }
                    return new(true, "SharedSavePublished",
                        "The approved live file capture is available to approved PCs. Game load has not been checked.", version);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    InvalidDataException or CryptographicException or JsonException or OverflowException or
                    FormatException or ArgumentException)
                {
                    return new(false, "LiveSavePublishDenied", ex.Message);
                }
                finally { if (stage is not null) TryDeleteStage(stage); }
            }
    }

    internal void DiscardLiveCapture(Guid profileId, Guid captureId)
    {
        if (profileId == Guid.Empty || captureId == Guid.Empty) throw new InvalidDataException("A fixed capture is required.");
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync) TryDeleteStage(LiveCaptureDirectory(profileId, captureId));
    }

    private static void CopyLiveFile(string source, string destination, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1024 * 1024]; int read;
        while ((read = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }
    private static string HashLiveFile(Stream input, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; int read;
        while ((read = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void VerifyLiveFile(string path, SharedWorldFile expected, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The copy is linked.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != expected.Length || HashLiveFile(input, cancellationToken) != expected.Sha256)
            throw new InvalidDataException("The immutable copy failed verification.");
    }

    // Read-only owner projection. It deliberately returns no candidate hash when
    // any authority, pointer, lineage, payload, or quarantine check fails.
    internal SharedWorldLiveOrphanReview ReviewLiveOrphan(ServerProfile profile)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                try
                {
                    var verified = ReadVerifiedLiveOrphan(profile);
                    return verified is null
                        ? new("None", null, "No interrupted live file copy needs review.")
                        : new("Verified", verified.Value.Version.VersionHash,
                            "A signed interrupted live file copy is verified and ready for owner review.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    InvalidDataException or CryptographicException or JsonException or OverflowException or
                    FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    return new("ReviewRequired", null,
                        "An interrupted file copy could not be verified. Keep sharing blocked and review the saved copies.");
                }
            }
    }

    // A disabled world can retain its old signed history. Distinguish that
    // ordinary state from a possible unpublished version before reporting an
    // authority warning; uncertainty must still require review.
    internal bool HasPotentialLiveOrphan(ServerProfile profile)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                var root = Root(profile.Id);
                EnsureUnlinkedRoot(data.RootPath, root);
                if (File.Exists(root)) throw new InvalidDataException("Shared save storage needs review.");
                if (!Directory.Exists(root)) return false;
                var binding = ReadBinding(profile.Id) ??
                    throw new InvalidDataException("Shared save source is missing.");
                if (!BindingMatches(binding, profile))
                    throw new InvalidDataException("Shared save source changed.");
                var latestPath = LatestPath(profile.Id);
                var status = Status(profile);
                if (File.Exists(latestPath) && status.Latest is null)
                    throw new InvalidDataException("Published shared save needs review.");
                var groupRoot = Path.Combine(root, binding.GroupId.ToString("N"));
                EnsureUnlinkedRoot(data.RootPath, groupRoot);
                if (File.Exists(groupRoot))
                    throw new InvalidDataException("Shared save group needs review.");
                if (!Directory.Exists(groupRoot)) return false;
                var publishedNumber = status.Latest?.Number ?? 0;
                foreach (var entry in Directory.EnumerateFileSystemEntries(groupRoot))
                {
                    if (!Directory.Exists(entry) ||
                        (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ||
                        !long.TryParse(Path.GetFileName(entry),
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1)
                        throw new InvalidDataException("Shared save history needs review.");
                    if (number > publishedNumber) return true;
                }
                return false;
            }
    }

    // An interrupted schema-5 publication is never reconciled into latest.json.
    // This remains the only mutation: move the exact verified directory while
    // leaving its signed files and the previous published pointer untouched.
    internal SharedWorldResult QuarantineVerifiedLiveOrphan(ServerProfile profile,
        string expectedVersionHash)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                try
                {
                    if (expectedVersionHash is not { Length: 64 } ||
                        !expectedVersionHash.All(char.IsAsciiHexDigit))
                        throw new InvalidDataException("An exact orphan version hash is required.");
                    var verified = ReadVerifiedLiveOrphan(profile) ??
                        throw new InvalidDataException("The next unpublished live version is missing.");
                    if (!verified.Version.VersionHash.Equals(expectedVersionHash, StringComparison.Ordinal))
                        throw new InvalidDataException("The requested live version changed.");
                    var destination = Path.Combine(verified.QuarantineRoot,
                        $"{verified.Version.GroupId:N}-{verified.Version.Number}-{verified.Version.VersionHash}");
                    Directory.CreateDirectory(verified.QuarantineRoot);
                    Directory.Move(verified.OrphanRoot, destination);
                    return new(true, "LiveSaveOrphanQuarantined",
                        "The signed unpublished file copy was preserved for review. It was not published. A new post-Stop copy can now be made; game load has not been checked.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    InvalidDataException or CryptographicException or JsonException or OverflowException or
                    FormatException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    return new(false, "LiveSaveOrphanQuarantineDenied",
                        "The interrupted file copy could not be quarantined safely. No new copy was published.");
                }
            }
    }

    private (SharedWorldVersion Version, string OrphanRoot, string QuarantineRoot)?
        ReadVerifiedLiveOrphan(ServerProfile profile)
    {
        var profileRoot = Root(profile.Id);
        if (File.Exists(profileRoot))
            throw new InvalidDataException("Shared save storage needs review.");
        if (!Directory.Exists(profileRoot) && !Authority.HasState(profile.Id)) return null;
        if (profile.Kind == GameKinds.Custom || profile.SeparateCopySourceProfileId is not null)
            throw new InvalidDataException("This profile cannot review a live publication.");
        var successor = Authority.LocalAuthorizedHead(profile.Id);
        if (Authority.GovernanceUnresolved(profile.Id) ||
            Authority.HasState(profile.Id) && successor is null ||
            successor is not null && !AuthorizedPublishedLineage(profile))
            throw new InvalidDataException("Shared world authority is unresolved.");
        var binding = ReadBinding(profile.Id);
        if (binding is null || !BindingMatches(binding, profile) || ReadRoster(profile) is null)
            throw new InvalidDataException("The signed shared source is missing or changed.");
        var latestPath = LatestPath(profile.Id);
        var status = Status(profile);
        var hasLatestPointer = File.Exists(latestPath);
        if (!hasLatestPointer && Directory.Exists(profileRoot) &&
            Directory.EnumerateFileSystemEntries(profileRoot, "latest.json").Any())
            throw new InvalidDataException("The existing shared save pointer is not a file.");
        if (hasLatestPointer && status.Latest is null)
            throw new InvalidDataException("The existing shared save pointer failed verification.");
        if (successor is not null &&
            (!WorldAuthorityTrust.Verify(successor, true) ||
             !VerifySignature(successor.Version) ||
             successor.Proposal.GroupId != binding.GroupId ||
             successor.Proposal.ProfileId != profile.Id ||
             successor.Version.GroupId != binding.GroupId ||
             successor.Version.ProfileId != profile.Id ||
             successor.Version.Game != profile.Kind ||
             successor.Version.WorldId != profile.WorldId ||
             successor.Version.VersionHash != successor.Proposal.VersionHash))
            throw new InvalidDataException("The signed hosting head does not match this world.");
        // A successor's signed authority head can be the predecessor even when
        // this PC has no latest.json or local copy of that predecessor.
        var previous = status.Latest ?? (!hasLatestPointer ? successor?.Version : null);
        if (successor is not null && (previous is null ||
            previous.GroupId != successor.Version.GroupId ||
            previous.Number < successor.Version.Number))
            throw new InvalidDataException("Published history does not continue the authority head.");
        var groupRoot = Path.Combine(Root(profile.Id), binding.GroupId.ToString("N"));
        var orphanRoot = Path.Combine(groupRoot,
            ((previous?.Number ?? 0) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        EnsureUnlinkedRoot(data.RootPath, orphanRoot);
        if (!Directory.Exists(orphanRoot))
        {
            if (File.Exists(orphanRoot))
                throw new InvalidDataException("The unpublished version is not a directory.");
            return null;
        }
        if (hasLatestPointer && previous is not null)
        {
            var previousRoot = VersionRoot(previous);
            EnsureUnlinkedRoot(data.RootPath, previousRoot);
            var previousPath = SafeChild(previousRoot, "version.json");
            if (!File.Exists(previousPath) ||
                new FileInfo(previousPath).Length is < 1 or > MaximumManifestBytes)
                throw new InvalidDataException("The published save directory is incomplete.");
            var previousBytes = File.ReadAllBytes(previousPath);
            var manifest = JsonSerializer.Deserialize<SharedWorldVersion>(previousBytes, Json);
            if (manifest is null || !VerifySignature(manifest) ||
                !previousBytes.AsSpan().SequenceEqual(File.ReadAllBytes(latestPath)) ||
                !JsonSerializer.SerializeToUtf8Bytes(manifest, Json).AsSpan().SequenceEqual(
                    JsonSerializer.SerializeToUtf8Bytes(previous, Json)))
                throw new InvalidDataException("The published save pointer differs from its signed directory.");
            VerifyLiveTree(Path.Combine(previousRoot, PayloadDirectory), previous.Files);
            if (previous.Number > 1) _ = ReadEarlierVersion(previous, 1);
        }
        var manifestPath = SafeChild(orphanRoot, "version.json");
        if (!File.Exists(manifestPath) ||
            new FileInfo(manifestPath).Length is < 1 or > MaximumManifestBytes)
            throw new InvalidDataException("The unpublished live version is incomplete.");
        var candidate = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(manifestPath), Json);
        using var key = LoadPublishingKey(profile.Id);
        if (candidate is null || candidate.Schema != 5 ||
            candidate.CaptureKind != SharedWorldCaptureKinds.LiveSave ||
            !VerifySignature(candidate) ||
            candidate.ProfileId != profile.Id || candidate.Game != profile.Kind ||
            candidate.WorldId != profile.WorldId || candidate.GroupId != binding.GroupId ||
            candidate.Number != (previous?.Number ?? 0) + 1 ||
            candidate.ParentHash != previous?.VersionHash ||
            candidate.BackupId == Guid.Empty ||
            previous is not null && candidate.CreatedUtc <= previous.CreatedUtc ||
            candidate.SigningPublicKey != Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) ||
            !orphanRoot.Equals(VersionRoot(candidate), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The live orphan does not continue the verified history.");
        var entries = Directory.EnumerateFileSystemEntries(orphanRoot).ToArray();
        if (entries.Length != 2 || !entries.Contains(manifestPath,
                StringComparer.OrdinalIgnoreCase) ||
            !entries.Contains(Path.Combine(orphanRoot, PayloadDirectory),
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The live orphan contains unexpected entries.");
        BoundedTotalBytes(candidate.Files);
        VerifyLiveTree(Path.Combine(orphanRoot, PayloadDirectory), candidate.Files);

        var quarantineRoot = Path.Combine(Root(profile.Id), QuarantinedLiveDirectory);
        EnsureUnlinkedRoot(data.RootPath, quarantineRoot);
        if (Directory.Exists(quarantineRoot))
        {
            var existing = Directory.EnumerateFileSystemEntries(quarantineRoot).ToArray();
            if (existing.Length >= MaximumQuarantinedLiveVersions ||
                existing.Any(path => !Directory.Exists(path) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("The bounded live orphan quarantine needs review.");
        }
        var destination = Path.Combine(quarantineRoot,
            $"{candidate.GroupId:N}-{candidate.Number}-{candidate.VersionHash}");
        EnsureUnlinkedRoot(data.RootPath, destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidDataException("This live orphan is already quarantined.");
        return (candidate, orphanRoot, quarantineRoot);
    }
}
