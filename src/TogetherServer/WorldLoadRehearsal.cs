using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldLoadPreparationRequest(Guid BackupId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldLoadConfirmationRequest(string Step, bool Passed, string? GameVersion = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldLoadCleanupRequest(bool ConfirmStopped);
public sealed record WorldLoadRehearsalView(Guid Id, string Game, string SourceKind,
    string CopyIdentity, string AppVersion, string GameVersion, string State,
    bool CanLaunch, string LaunchReason, int GamePort, string WorldDirectory,
    Guid? RehearsalProfileId, string LoadOutcome, string ChangeOutcome, string RestartOutcome,
    int ManagedStarts, int GracefulStops, bool Cleaned, DateTimeOffset PreparedUtc, string? OwnerReportedGameVersion = null);
public sealed record WorldLoadRehearsalResult(bool Ok, string Code, string Message,
    WorldLoadRehearsalView? Rehearsal = null);
public sealed record WorldLoadRehearsalList(bool Ok, string Code, string Message, IReadOnlyList<WorldLoadRehearsalView> Rehearsals);
internal sealed record VerifiedWorldLoadSource(Guid ProfileId, string Game, string WorldId,
    string Kind, string CopyIdentity, IReadOnlyList<SharedWorldFile> Files, string PayloadRoot,
    ServerProfile? InstalledProfile = null);

internal sealed partial class WorldBackupService
{
    internal VerifiedWorldLoadSource ReadVerifiedWorldLoadSource(ServerProfile profile, Guid backupId)
    {
        lock (sync)
        {
            var record = data.LoadBackupCatalog().Records.SingleOrDefault(item => item.ProfileId == profile.Id &&
                item.Id == backupId && item.Kind == profile.Kind && item.WorldId == profile.WorldId)
                ?? throw new InvalidDataException("A matching completed backup is required.");
            var directory = BackupDirectory(profile.Id, backupId);
            var manifest = ReadAndVerifyManifest(record, directory);
            var identity = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));
            return new(profile.Id, profile.Kind, profile.WorldId, "Backup", identity,
                manifest.Files.Select(file => new SharedWorldFile(file.Path, file.Length, file.Sha256)).ToArray(),
                Path.Combine(directory, "payload"), profile);
        }
    }
}

internal sealed class WorldLoadRehearsalRecord
{
    public int Schema { get; set; } = 1;
    public Guid Id { get; set; }
    public Guid SourceProfileId { get; set; }
    public Guid WorkingProfileId { get; set; }
    public string Game { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string CopyIdentity { get; set; } = "";
    public string AppVersion { get; set; } = CompanionProtocol.AppVersion;
    public string GameVersion { get; set; } = "Unknown";
    public string? OwnerReportedGameVersion { get; set; }
    public string? BinaryPath { get; set; }
    public string? BinarySha256 { get; set; }
    public string WorldDirectory { get; set; } = "";
    public string State { get; set; } = "Preparing";
    public bool Automated { get; set; }
    public int GamePort { get; set; }
    public string LoadOutcome { get; set; } = "Unobserved";
    public string ChangeOutcome { get; set; } = "Unobserved";
    public string RestartOutcome { get; set; } = "Unobserved";
    public int ManagedStarts { get; set; }
    public int GracefulStops { get; set; }
    public int StartsAtChange { get; set; }
    public int StopsAtChange { get; set; }
    public Guid? LastOperationId { get; set; }
    public Guid? LastStoppedOperationId { get; set; }
    public bool Cleaned { get; set; }
    public DateTimeOffset PreparedUtc { get; set; }
}

public sealed partial class HostManager
{
    private const string WorldLoadRecordsName = "world-load-rehearsals.protected";
    internal Func<long>? WorldLoadAvailableBytesForChecks { get; set; }
    internal Action? WorldLoadAfterCopyForChecks { get; set; }
    private List<WorldLoadRehearsalRecord> WorldLoadRecords()
    {
        var existed = data.HasProtected(WorldLoadRecordsName);
        var bytes = data.LoadProtected(WorldLoadRecordsName);
        if (bytes is null)
        {
            if (existed || data.Recovery.Notices.Any(notice => notice.StateFile == WorldLoadRecordsName))
                throw new InvalidDataException("Rehearsal records need review.");
            return [];
        }
        var records = JsonSerializer.Deserialize<List<WorldLoadRehearsalRecord>>(bytes);
        if (records is null || records.Count > 20 || records.Any(item => item is null) || records.Select(item => item.Id).Distinct().Count() != records.Count ||
            records.Select(item => item.WorkingProfileId).Distinct().Count() != records.Count ||
            records.Any(item => item.Schema != 1 || item.Id == Guid.Empty || item.WorkingProfileId == Guid.Empty ||
                item.WorldDirectory != data.NewWorldDirectory(item.WorkingProfileId) ||
                item.CopyIdentity is not { Length: 64 } || !item.CopyIdentity.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Rehearsal records need review.");
        return records;
    }

    private void SaveWorldLoadRecords(List<WorldLoadRehearsalRecord> records) =>
        data.SaveProtected(WorldLoadRecordsName, JsonSerializer.SerializeToUtf8Bytes(records));

    private static string? WorldLoadBinaryHash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The game binary is linked.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 512L * 1024 * 1024) throw new InvalidDataException("The game binary cannot be bounded.");
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private bool WorldLoadBinaryMatches(WorldLoadRehearsalRecord record) =>
        record.BinaryPath is null || WorldLoadBinaryHash(record.BinaryPath) == record.BinarySha256;

    private WorldLoadRehearsalView WorldLoadView(WorldLoadRehearsalRecord record)
    {
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == record.WorkingProfileId);
        var run = runs.SingleOrDefault(item => item.ProfileId == record.WorkingProfileId);
        var binaryMatches = WorldLoadBinaryMatches(record);
        var state = record.Cleaned ? "Cleaned" : !binaryMatches ? "Game binary changed" :
            run is null ? record.State : Identity(run) == "Matched" ? "Managed process running" : "Process needs review";
        return new(record.Id, record.Game, record.SourceKind, record.CopyIdentity, record.AppVersion,
            record.GameVersion, state, record.Automated && !record.Cleaned && binaryMatches && profile is not null &&
                record.State is not ("Preparing" or "Preparation interrupted"),
            record.Automated ? "Reviewed isolated driver trial. A running process or readiness reply does not prove game load." :
                "Use a fresh manual game setup. Automatic isolation is not available for this copy or installed binary.",
            record.GamePort, record.WorldDirectory, profile?.Id, record.LoadOutcome, record.ChangeOutcome,
            record.RestartOutcome, record.ManagedStarts, record.GracefulStops, record.Cleaned, record.PreparedUtc, record.OwnerReportedGameVersion);
    }

    private static bool WorldLoadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidDataException or JsonException or CryptographicException or ArgumentException or InvalidOperationException or OverflowException;

    public async Task<WorldLoadRehearsalResult> PrepareWorldLoadRehearsalAsync(Guid profileId, Guid backupId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return new(false, "UnknownProfile", "Choose a saved server.");
            return PrepareWorldLoadUnderGate(backups.ReadVerifiedWorldLoadSource(profile, backupId));
        }
        catch (Exception ex) when (WorldLoadFailure(ex))
        { return new(false, "WorldLoadPreparationFailed", "The completed backup could not be prepared safely. The source and live world were kept."); }
        finally { gate.Release(); }
    }

    internal async Task<WorldLoadRehearsalResult> PrepareReceivedWorldLoadRehearsalAsync(VerifiedWorldLoadSource source)
    {
        await gate.WaitAsync();
        try { return PrepareWorldLoadUnderGate(source); }
        catch (Exception ex) when (WorldLoadFailure(ex))
        { return new(false, "WorldLoadPreparationFailed", "The verified received copy could not be prepared safely. Its receipt and payload were kept."); }
        finally { gate.Release(); }
    }

    private WorldLoadRehearsalResult PrepareWorldLoadUnderGate(VerifiedWorldLoadSource source)
    {
        if (data.Recovery.LifecycleBlocked) return new(false, "DataRecoveryRequired", "Review local data recovery first.");
        var records = WorldLoadRecords();
        if (records.Count(item => !item.Cleaned) >= 8)
            return new(false, "WorldLoadRetentionLimit", "Clean up a completed disposable rehearsal before preparing another.");
        if (source.Files.Count is < 1 or > SharedWorldService.MaximumFiles || source.Files.Any(file => file.Length < 0) ||
            source.CopyIdentity.Length != 64 || !source.CopyIdentity.All(Uri.IsHexDigit))
            throw new InvalidDataException("A bounded verified copy is required.");
        var size = source.Files.Aggregate(0L, (total, file) => checked(total + file.Length));
        if (size < 0 || size > SharedWorldService.MaximumSharedWorldBytes)
            throw new InvalidDataException("The verified copy exceeds the rehearsal limit.");
        var available = WorldLoadAvailableBytesForChecks?.Invoke() ?? new DriveInfo(Path.GetPathRoot(data.RootPath)!).AvailableFreeSpace;
        if (available < 1024L * 1024 * 1024 + checked(size * 2))
            return new(false, "WorldLoadLowSpace", "Keep 1 GiB free after preparing the disposable working copy.");
        var workingId = Guid.NewGuid();
        var root = data.NewWorldDirectory(workingId);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        if (Directory.Exists(root) || File.Exists(root)) throw new InvalidDataException("The working directory must be fresh.");
        var binary = source.InstalledProfile?.ExecutablePath;
        var hash = WorldLoadBinaryHash(binary);
        var automated = hash is not null && source.Game is (GameKinds.Valheim or GameKinds.Fixture) &&
            games.TryGet(source.Game, out _);
        var record = new WorldLoadRehearsalRecord
        {
            Id = Guid.NewGuid(),
            SourceProfileId = source.ProfileId,
            WorkingProfileId = workingId,
            Game = source.Game,
            WorldId = source.WorldId,
            SourceKind = source.Kind,
            CopyIdentity = source.CopyIdentity,
            WorldDirectory = root,
            BinaryPath = binary,
            BinarySha256 = hash,
            Automated = automated,
            GameVersion = source.InstalledProfile is null ? "Unknown" : ServerAddOns.GameVersion(source.InstalledProfile),
            PreparedUtc = clock.GetUtcNow(),
            GamePort = PickWorldLoadPort()
        };
        if (records.Count == 20) records.Remove(records.First(item => item.Cleaned));
        records.Add(record);
        SaveWorldLoadRecords(records); // A crash leaves a reviewable exact directory, never an automatic retry.
        try
        {
            Directory.CreateDirectory(root);
            foreach (var file in source.Files)
            {
                var from = SharedWorldService.SafeChild(source.PayloadRoot, file.Path);
                SharedWorldService.VerifyFile(from, file);
                var to = SharedWorldService.SafeChild(root, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                SharedWorldService.VerifyFile(to, file);
                SharedWorldService.VerifyFile(from, file);
            }
            WorldLoadAfterCopyForChecks?.Invoke();
            if (automated)
            {
                var profile = new ServerProfile
                {
                    Id = workingId,
                    WorldLoadRehearsalId = record.Id,
                    Kind = source.Game,
                    Name = "Disposable load rehearsal",
                    ServerName = "Disposable load rehearsal",
                    WorldId = source.WorldId,
                    WorldDirectory = root,
                    WorldSource = "New",
                    ExecutablePath = binary!,
                    GamePort = record.GamePort,
                    Maintenance = new() { Enabled = true, Message = "Owner-only disposable world-load rehearsal." },
                    Backups = new() { Enabled = false },
                    CrashRecovery = new() { Enabled = false }
                };
                data.RecordNewWorld(profile);
                if (profile.Kind == GameKinds.Valheim)
                    data.SaveValheimPassword(profile.Id, Convert.ToHexString(RandomNumberGenerator.GetBytes(12)));
                settings.Profiles.Add(profile);
                data.SaveSettings(settings);
            }
            record.State = "Prepared";
            SaveWorldLoadRecords(records);
            AdvanceReadModel();
            Volatile.Write(ref lastOwnerSnapshot, Snapshot());
            return new(true, "WorldLoadPrepared", "Exact copy prepared in fresh disposable storage. Game load and saved restart remain unverified.", WorldLoadView(record));
        }
        catch
        {
            record.State = "Preparation interrupted";
            SaveWorldLoadRecords(records);
            throw;
        }
    }

    private int PickWorldLoadPort()
    {
        var declared = runs.SelectMany(run => run.DeclaredPorts ?? []).Select(port => port.Port)
            .Concat(settings.Profiles.SelectMany(profile => new[] { profile.GamePort, profile.GamePort + 1 })).ToHashSet();
        var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
        var used = properties.GetActiveTcpListeners().Concat(properties.GetActiveUdpListeners()).Select(item => item.Port).ToHashSet();
        for (var port = 35000; port < 45000; port += 2)
            if (!declared.Contains(port) && !declared.Contains(port + 1) && !used.Contains(port) && !used.Contains(port + 1)) return port;
        throw new InvalidDataException("No nonconflicting rehearsal port pair is available.");
    }

    private ActionResult? WorldLoadStartBlock(ServerProfile profile)
    {
        var record = WorldLoadRecords().SingleOrDefault(item => item.WorkingProfileId == profile.Id && !item.Cleaned);
        if (record is null) return profile.WorldLoadRehearsalId is null ? null :
            Result(false, "WorldLoadReviewRequired", "The disposable rehearsal record is missing.");
        return record.Automated && record.State != "Preparing" && record.State != "Preparation interrupted" &&
            profile.WorldLoadRehearsalId == record.Id && profile.Kind == record.Game &&
            profile.WorldId == record.WorldId && profile.WorldDirectory == record.WorldDirectory &&
            profile.ExecutablePath == record.BinaryPath && profile.GamePort == record.GamePort &&
            profile.Maintenance.Enabled && !profile.SharedSavesEnabled && !profile.Backups.Enabled &&
            !profile.CrashRecovery.Enabled && WorldLoadBinaryMatches(record)
            ? null : Result(false, "WorldLoadSetupChanged", "The exact rehearsal setup or game binary changed. Prepare another disposable copy.");
    }

    public async Task<WorldLoadRehearsalResult> WorldLoadRehearsalAsync(Guid id, string action = "status",
        WorldLoadConfirmationRequest? confirmation = null, bool confirmStopped = false)
    {
        await gate.WaitAsync();
        try
        {
            var records = WorldLoadRecords();
            var record = records.SingleOrDefault(item => item.Id == id);
            if (record is null) return new(false, "WorldLoadNotFound", "Choose a prepared rehearsal.");
            if (action != "status" && record.Cleaned) return new(false, "WorldLoadCleaned", "This disposable copy has already been cleaned up.", WorldLoadView(record));
            if (action is "start" or "stop")
            {
                if (!record.Automated) return new(false, "WorldLoadManualOnly", "Use the manual load steps for this copy.", WorldLoadView(record));
                if (action == "start")
                {
                    var existing = runs.SingleOrDefault(item => item.ProfileId == record.WorkingProfileId);
                    if (existing is not null) return new(false, "WorldLoadAlreadyRunning", "Stop or resolve the exact rehearsal process first.", WorldLoadView(record));
                    var started = StartUnderGate(record.WorkingProfileId, false, worldLoadTrial: true);
                    if (!started.Ok) return new(false, started.Code, started.Message, WorldLoadView(record));
                    var launched = runs.Single(item => item.ProfileId == record.WorkingProfileId);
                    record.ManagedStarts++; record.LastOperationId = launched.OperationId;
                    record.State = "Started";
                }
                else
                {
                    var run = runs.SingleOrDefault(item => item.ProfileId == record.WorkingProfileId);
                    if (run is null) return new(false, "WorldLoadNotRunning", "No recorded rehearsal process is running.", WorldLoadView(record));
                    var stopped = await StopUnderGateAsync(record.WorkingProfileId, null);
                    if (!stopped.Ok) return new(false, stopped.Code, stopped.Message, WorldLoadView(record));
                    record.GracefulStops++; record.LastStoppedOperationId = run.OperationId;
                    record.State = "Stopped";
                }
            }
            else if (action == "confirm")
            {
                if (confirmation is null || confirmation.Step is not ("Load" or "Change" or "Restart") || !WorldLoadBinaryMatches(record))
                    return new(false, "WorldLoadConfirmationRejected", "Choose a reviewed step with unchanged game setup.", WorldLoadView(record));
                if (confirmation.GameVersion is { } version && (version.Length is < 1 or > 40 ||
                    version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_' and not ' ')))
                    return new(false, "WorldLoadVersionRejected", "Use a bounded game version without addresses or paths.", WorldLoadView(record));
                if (confirmation.Step != "Load" && record.LoadOutcome != "OwnerConfirmed")
                    return new(false, "WorldLoadOrderRequired", "Confirm the initial game load before the saved-change and restart steps.", WorldLoadView(record));
                if (confirmation.Step == "Restart" && record.ChangeOutcome != "OwnerConfirmed")
                    return new(false, "WorldLoadOrderRequired", "Confirm a recognizable saved change before the restart step.", WorldLoadView(record));
                if (confirmation.Step == "Restart" && record.Automated && (record.ManagedStarts < 2 || record.GracefulStops < 1 ||
                    record.ManagedStarts <= record.StartsAtChange || record.GracefulStops <= record.StopsAtChange))
                    return new(false, "WorldLoadRestartUnobserved", "Complete an exact graceful Stop and another Start before confirming restart.", WorldLoadView(record));
                var outcome = confirmation.Passed ? "OwnerConfirmed" : "OwnerFailed";
                if (confirmation.Step == "Load")
                {
                    record.LoadOutcome = outcome;
                    if (!confirmation.Passed) { record.ChangeOutcome = "Unobserved"; record.RestartOutcome = "Unobserved"; }
                }
                else if (confirmation.Step == "Change")
                {
                    record.ChangeOutcome = outcome; record.RestartOutcome = "Unobserved";
                    record.StartsAtChange = record.ManagedStarts; record.StopsAtChange = record.GracefulStops;
                }
                else record.RestartOutcome = outcome;
                if (confirmation.GameVersion is not null) record.OwnerReportedGameVersion = confirmation.GameVersion;
            }
            else if (action == "cleanup")
            {
                if (!confirmStopped) return new(false, "WorldLoadStopConfirmationRequired", "Confirm the disposable game is stopped before cleanup.", WorldLoadView(record));
                if (runs.Any(run => CanonicalWorldPath(run.WorldDirectory) == CanonicalWorldPath(record.WorldDirectory)))
                    return new(false, "WorldLoadProcessUnresolved", "Stop or resolve the exact recorded rehearsal process before cleanup.", WorldLoadView(record));
                var working = CanonicalWorldPath(record.WorldDirectory);
                if (settings.Profiles.Any(profile => profile.Id != record.WorkingProfileId &&
                    (CanonicalWorldPath(profile.WorldDirectory).Equals(working, StringComparison.OrdinalIgnoreCase) ||
                    CanonicalWorldPath(profile.WorldDirectory).StartsWith(working + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                    return new(false, "WorldLoadDirectoryInUse", "Another saved server refers to this working directory. Keep it for review.", WorldLoadView(record));
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, record.WorldDirectory);
                if (Directory.Exists(record.WorldDirectory))
                {
                    var pending = new Stack<string>(); pending.Push(record.WorldDirectory);
                    while (pending.TryPop(out var path))
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Linked rehearsal files cannot be cleaned up automatically.");
                        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) pending.Push(child);
                    }
                    Directory.Delete(record.WorldDirectory, true);
                }
                settings.Profiles.RemoveAll(profile => profile.Id == record.WorkingProfileId);
                data.SaveSettings(settings);
                data.DeleteProtected($"valheim-password-{record.WorkingProfileId:N}.protected");
                record.Cleaned = true; record.State = "Cleaned";
            }
            else if (action != "status") return new(false, "WorldLoadActionRejected", "Choose a fixed rehearsal action.");
            if (action != "status")
            {
                SaveWorldLoadRecords(records);
                AdvanceReadModel();
                Volatile.Write(ref lastOwnerSnapshot, Snapshot());
            }
            return new(true, "WorldLoadUpdated", action == "cleanup" ? "Only the exact disposable working copy was removed. The source and bounded result were retained." :
                "Rehearsal evidence updated. Owner confirmations are not automatic game-load proof or hosting authority.", WorldLoadView(record));
        }
        catch (Exception ex) when (WorldLoadFailure(ex))
        { return new(false, "WorldLoadReviewRequired", "The exact rehearsal could not be verified. Keep its files and source copy for review."); }
        finally { gate.Release(); }
    }

    public async Task<WorldLoadRehearsalList> WorldLoadRehearsalsAsync(Guid sourceProfileId)
    {
        await gate.WaitAsync();
        try
        {
            return new(true, "WorldLoadListed", "Owner confirmations and unobserved steps are separate.",
            WorldLoadRecords().Where(item => item.SourceProfileId == sourceProfileId).Select(WorldLoadView).Reverse().ToArray());
        }
        catch (Exception ex) when (WorldLoadFailure(ex))
        { return new(false, "WorldLoadReviewRequired", "Disposable rehearsal records need review. Keep the working copies.", []); }
        finally { gate.Release(); }
    }
}
