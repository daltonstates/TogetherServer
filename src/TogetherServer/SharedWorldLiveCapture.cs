using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

// Only a trusted, exact managed-run adapter may construct this evidence. No
// HTTP or UI surface accepts one, and no production adapter is accepted yet.
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
    // The only test override is for the synthetic fixture game. Real games
    // still require their adapter's LiveCaptureAccepted gate to turn true.
    internal bool FixtureLiveCaptureAcceptedForChecks { get; set; }
    internal GameServerRegistry? LiveGameRegistryForChecks { get; set; }
    internal Action? AfterLiveSourceScanForChecks { get; set; }
    internal Func<ManagedRun, bool>? FixtureLiveRunIdentityForChecks { get; set; }
    internal Func<long>? FixtureLiveAvailableBytesForChecks { get; set; }

    private bool LiveCaptureAccepted(string game) =>
        SharedWorldLiveSaveAdapters.ForGame(game)?.LiveCaptureAccepted == true ||
        game == GameKinds.Fixture && FixtureLiveCaptureAcceptedForChecks;

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
        var games = profile.Kind == GameKinds.Fixture && LiveGameRegistryForChecks is not null
            ? LiveGameRegistryForChecks : new GameServerRegistry(data);
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
        var owned = profile.Kind switch
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
        int processId, long startTimeUtcTicks)
    {
        var recorded = data.LoadRuns().SingleOrDefault(item =>
            item.ProfileId == profile.Id && item.OperationId == operationId);
        return recorded is not null && recorded.Kind == profile.Kind &&
            recorded.WorldId == profile.WorldId && recorded.ProcessId == processId &&
            recorded.StartTimeUtcTicks == startTimeUtcTicks &&
            recorded.StopRequestedUtc is null && recorded.WasReady &&
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
        LiveSaveCompletionEvidence completion)
    {
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                if (!profile.SharedSavesEnabled || profile.Kind == GameKinds.Custom ||
                    run.ProfileId != profile.Id || run.Kind != profile.Kind ||
                    run.WorldId != profile.WorldId || run.OperationId == Guid.Empty ||
                    run.ProcessId is not > 0 || run.StartTimeUtcTicks is not > 0 ||
                    !run.WasReady || run.StopRequestedUtc is not null ||
                    !completion.Complete || completion.Kind != LiveSaveEvidence.RunScopedCompletion ||
                    completion.ProfileId != run.ProfileId ||
                    completion.OperationId != run.OperationId ||
                    completion.ProcessId != run.ProcessId ||
                    completion.StartTimeUtcTicks != run.StartTimeUtcTicks ||
                    completion.CompletedUtc.UtcTicks < run.StartTimeUtcTicks ||
                    completion.CompletedUtc > DateTimeOffset.UtcNow.AddMinutes(1))
                    throw new InvalidDataException("An exact completed managed-run save is required.");
                if (!RecordedExactLiveRun(profile, run.OperationId,
                        run.ProcessId.Value, run.StartTimeUtcTicks.Value))
                    throw new InvalidDataException("The completion does not match the recorded managed run.");
                var source = ReviewedLiveSource(profile, run);
                var binding = ReadBinding(profile.Id);
                if (binding is null || !BindingMatches(binding, profile) || ReadRoster(profile) is null)
                    throw new InvalidDataException("The signed shared source is missing or changed.");
                var beforeSetup = ServerSetupSnapshots.Capture(profile, data);
                var before = ServerSetupSnapshots.Read(profile, beforeSetup);
                _ = SharedWorldPortableSetupReader.Capture(before);
                var sourceFiles = ScanLiveTree(source);
                var bytes = BoundedTotalBytes(sourceFiles);
                RequireLiveSpace(bytes, profile.Kind);
                AfterLiveSourceScanForChecks?.Invoke();
                var captureId = Guid.NewGuid();
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
                        var destination = SafeChild(payload, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(SafeChild(source, file.Path), destination, false);
                        VerifyFile(destination, file);
                    }
                    if (!sourceFiles.SequenceEqual(ScanLiveTree(source)))
                        throw new InvalidDataException("The live save source changed during capture.");
                    if (!RecordedExactLiveRun(profile, run.OperationId,
                            run.ProcessId.Value, run.StartTimeUtcTicks.Value))
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
                    var signed = unsigned with { Signature = Convert.ToBase64String(
                        key.SignData(JsonSerializer.SerializeToUtf8Bytes(unsigned, Json),
                            HashAlgorithmName.SHA256)) };
                    var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(signed, Json);
                    if (manifestBytes.Length > MaximumManifestBytes)
                        throw new InvalidDataException("The live capture manifest is too large.");
                    File.WriteAllBytes(Path.Combine(partial, "complete.json"), manifestBytes);
                    VerifyLiveTree(payload, sourceFiles);
                    Directory.Move(partial, LiveCaptureDirectory(profile.Id, captureId));
                    return captureId;
                }
                finally { TryDeleteStage(partial); }
            }
    }

    private static SharedWorldFile[] ScanLiveTree(string root)
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
                    Convert.ToHexString(SHA256.HashData(stream))));
            }
        }
        if (files.Count == 0) throw new InvalidDataException("The live save has no complete files.");
        var ordered = files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        BoundedTotalBytes(ordered);
        return ordered;
    }

    private static void VerifyLiveTree(string root, IReadOnlyList<SharedWorldFile> expected)
    {
        var actual = ScanLiveTree(root);
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
        ReadVerifiedLiveCapture(ServerProfile profile, Guid captureId)
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
            capture.EvidenceKind != LiveSaveEvidence.RunScopedCompletion ||
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
        VerifyLiveTree(payload, capture.Files);
        if (Directory.EnumerateFileSystemEntries(root).Count() != 3)
            throw new InvalidDataException("The live capture contains unexpected entries.");
        return (capture, setup, payload);
    }

    // This is deliberately internal and has no HostManager, UI, local HTTP or
    // companion route. Its approval is tied to the sealed exact-run capture.
    internal SharedWorldResult PublishLiveCapture(ServerProfile profile, Guid captureId,
        LiveSavePublicationApproval approval)
    {
        if (!profile.SharedSavesEnabled || profile.Kind == GameKinds.Custom)
            return new(false, "SharingOff", "Shared saves are off for this server.");
        lock (SharedWorldMutationGate.For(data.RootPath))
            lock (sync)
            {
                string? stage = null;
                try
                {
                    if (!LiveCaptureAccepted(profile.Kind))
                        throw new InvalidDataException("Live save publication has not passed game acceptance.");
                    if (!approval.Approved || approval.CaptureId != captureId)
                        throw new InvalidDataException("The exact live capture has no publication approval.");
                    var (capture, setup, payload) = ReadVerifiedLiveCapture(profile, captureId);
                    if (approval.OperationId != capture.OperationId)
                        throw new InvalidDataException("The approval belongs to another managed run.");
                    if (!RecordedExactLiveRun(profile, capture.OperationId,
                            capture.ProcessId, capture.StartTimeUtcTicks))
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
                        var destination = SafeChild(destinationPayload, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(SafeChild(payload, file.Path), destination, false);
                        VerifyFile(destination, file);
                    }
                    VerifyLiveTree(destinationPayload, files);
                    File.WriteAllBytes(Path.Combine(stage, "version.json"), versionBytes);
                    var destinationRoot = VersionRoot(version);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationRoot)!);
                    if (Directory.Exists(destinationRoot))
                        throw new InvalidDataException("Shared version already exists.");
                    Directory.Move(stage, destinationRoot);
                    stage = null;
                    VerifyLiveTree(Path.Combine(destinationRoot, PayloadDirectory), files);
                    var latestStage = LatestPath(profile.Id) + ".new";
                    File.WriteAllBytes(latestStage, versionBytes);
                    File.Move(latestStage, LatestPath(profile.Id), true);
                    // The published payload is verified and now has its own
                    // durable pointer. The private capture can be discarded.
                    TryDeleteStage(LiveCaptureDirectory(profile.Id, captureId));
                    EvictOldChunkHashes(profile.Id, version.VersionHash);
                    try { PrunePublishedPayloads(version); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        InvalidDataException or JsonException) { }
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
}
