using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public static class CrashRecoveryStates
{
    public const string Pending = "Pending";
    public const string Starting = "Starting";
    public const string Recovered = "Recovered";
    public const string Suspended = "Suspended";
}

public sealed class CrashRecoveryState
{
    public Guid ProfileId { get; set; }
    public Guid CycleId { get; set; } = Guid.NewGuid();
    public string State { get; set; } = CrashRecoveryStates.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CrashDetectedUtc { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public DateTimeOffset? ReadinessDeadlineUtc { get; set; }
    public DateTimeOffset? RecoveredUtc { get; set; }
    public string? LastFailure { get; set; }
}

public sealed record ManagedRunArchive(Guid ProfileId, Guid OperationId, string Kind, string WorldId,
    int? ProcessId, long? StartTimeUtcTicks, bool WasReady, string Reason,
    DateTimeOffset ArchivedUtc, bool CrashRecoveryScheduled);

public static class BackupKinds
{
    public const string Rolling = "Rolling";
    public const string PreRestore = "PreRestore";
}

public sealed class WorldBackupRecord
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public string Kind { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string BackupKind { get; set; } = BackupKinds.Rolling;
    public DateTimeOffset CreatedUtc { get; set; }
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
}

public sealed class BackupFailure
{
    public Guid ProfileId { get; set; }
    public DateTimeOffset FailedUtc { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class BackupCatalog
{
    public List<WorldBackupRecord> Records { get; set; } = [];
    public List<BackupFailure> Failures { get; set; } = [];
}

public sealed record WorldBackupStatus(Guid ProfileId, DateTimeOffset? LastSuccessfulUtc,
    DateTimeOffset? LastFailureUtc, string? LastFailure, int CompletedCount);
public sealed record WorldBackupList(IReadOnlyList<WorldBackupRecord> Backups, WorldBackupStatus Status);
public sealed record WorldBackupResult(bool Ok, string Code, string Message, WorldBackupRecord? Backup = null);
public sealed record RestoreBackupRequest(Guid BackupId);

internal static class WorldRestorePhases
{
    public const string Prepared = "Prepared";
    public const string LiveMoved = "LiveMoved";
    public const string ReplacementInstalled = "ReplacementInstalled";
}

internal sealed class WorldRestoreTransaction
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public Guid BackupId { get; set; }
    public string Kind { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string ProfileWorldDirectory { get; set; } = "";
    public string WorldDirectory { get; set; } = "";
    public string Phase { get; set; } = WorldRestorePhases.Prepared;
}

internal sealed record WorldRestorePaths(string World, string Parent, string Stage, string Rollback);

internal sealed class BackupManifest
{
    public Guid BackupId { get; set; }
    public Guid ProfileId { get; set; }
    public string Kind { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string BackupKind { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public List<BackupManifestFile> Files { get; set; } = [];
}

internal sealed record BackupManifestFile(string Path, long Length, string Sha256);

internal sealed class WorldBackupService
{
    private const string RestoreTransactionsFile = "restore-transactions.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly LocalData data;
    private readonly TimeProvider clock;
    private readonly Func<string, long> availableSpace;
    private readonly object sync = new();

    public WorldBackupService(LocalData data, TimeProvider clock, Func<string, long>? availableSpace = null)
    {
        this.data = data;
        this.clock = clock;
        this.availableSpace = availableSpace ?? (path => new DriveInfo(path).AvailableFreeSpace);
        Directory.CreateDirectory(data.BackupsRoot);
        ReconcileInterruptedRestores();
        CleanInterruptedStages();
    }

    public IReadOnlyList<WorldBackupRecord> List(Guid profileId)
    {
        lock (sync)
            return data.LoadBackupCatalog().Records.Where(item => item.ProfileId == profileId)
                .OrderByDescending(item => item.CreatedUtc).ToList();
    }

    public WorldBackupStatus Status(Guid profileId)
    {
        lock (sync)
        {
            var catalog = data.LoadBackupCatalog();
            var completed = catalog.Records.Where(item => item.ProfileId == profileId).ToList();
            var failure = catalog.Failures.Where(item => item.ProfileId == profileId)
                .OrderByDescending(item => item.FailedUtc).FirstOrDefault();
            return new(profileId, completed.Count == 0 ? null : completed.Max(item => item.CreatedUtc),
                failure?.FailedUtc, failure?.Message, completed.Count);
        }
    }

    public WorldBackupResult Create(ServerProfile profile, string backupKind) =>
        Create(profile, backupKind, profile.WorldDirectory);

    public WorldBackupResult Create(ServerProfile profile, string backupKind, string saveDirectory,
        Guid? protectedBackupId = null)
    {
        lock (sync)
        {
            var id = Guid.NewGuid();
            var profileRoot = ProfileRoot(profile.Id);
            var stage = Path.Combine(profileRoot, id.ToString("N") + ".staging");
            try
            {
                if (backupKind is not (BackupKinds.Rolling or BackupKinds.PreRestore))
                    throw new InvalidDataException("Unknown backup kind.");
                var source = SafeWorldRoot(saveDirectory);
                if (Contains(source, data.BackupsRoot) || Contains(data.BackupsRoot, source))
                    throw new InvalidOperationException("The backup store and server save directory must be separate.");
                Directory.CreateDirectory(profileRoot);
                var estimate = MeasureTree(source);
                EnsureFreeSpace(profileRoot, estimate.SizeBytes, profile.Backups.MinimumFreeSpaceMb);
                Directory.CreateDirectory(stage);
                var payload = Path.Combine(stage, "payload");
                Directory.CreateDirectory(payload);
                var copied = CopyTree(source, payload);
                var created = clock.GetUtcNow();
                var manifest = new BackupManifest
                {
                    BackupId = id,
                    ProfileId = profile.Id,
                    Kind = profile.Kind,
                    WorldId = profile.WorldId,
                    BackupKind = backupKind,
                    CreatedUtc = created,
                    Files = copied.Files
                };
                File.WriteAllText(Path.Combine(stage, "complete.json"), JsonSerializer.Serialize(manifest, Json));
                var destination = BackupDirectory(profile.Id, id);
                Directory.Move(stage, destination);
                var record = new WorldBackupRecord
                {
                    Id = id,
                    ProfileId = profile.Id,
                    Kind = profile.Kind,
                    WorldId = profile.WorldId,
                    BackupKind = backupKind,
                    CreatedUtc = created,
                    SizeBytes = copied.SizeBytes,
                    FileCount = copied.Files.Count
                };
                var catalog = data.LoadBackupCatalog();
                catalog.Records.Add(record);
                catalog.Failures.RemoveAll(item => item.ProfileId == profile.Id);
                data.SaveBackupCatalog(catalog);
                string? retentionWarning = null;
                try { EnforceRetention(profile, catalog, id, protectedBackupId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    retentionWarning = " The new backup is complete, but old-backup retention could not finish: " + ex.Message;
                    RecordFailure(profile.Id, "BackupRetentionFailed", ex.Message);
                }
                if (!data.TryAudit($"backup-complete {profile.Id} {id} kind={backupKind} files={record.FileCount} bytes={record.SizeBytes} {created:O}"))
                    retentionWarning = (retentionWarning ?? "") + " The backup is complete, but its local audit entry could not be written.";
                return new(true, retentionWarning is null ? "BackupCompleted" : "BackupCompletedRetentionFailed",
                    $"Backup completed with {record.FileCount} files." + retentionWarning, record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or OverflowException)
            {
                try { SafeDeleteDirectory(stage, profileRoot); }
                catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                try { RecordFailure(profile.Id, "BackupFailed", ex.Message); }
                catch (Exception recordEx) when (recordEx is IOException or UnauthorizedAccessException or InvalidDataException) { }
                return new(false, "BackupFailed", "The server stopped, but its rolling backup failed: " + ex.Message);
            }
        }
    }

    public WorldBackupResult Restore(ServerProfile profile, Guid backupId) =>
        Restore(profile, backupId, profile.WorldDirectory);

    public WorldBackupResult Restore(ServerProfile profile, Guid backupId, string saveDirectory)
    {
        lock (sync)
        {
            if (data.LoadState(RestoreTransactionsFile, new List<WorldRestoreTransaction>()).Count > 0)
                return new(false, "RestoreRecoveryPending",
                    "An earlier restore still requires startup reconciliation before another restore can begin.");
            var catalog = data.LoadBackupCatalog();
            var record = catalog.Records.SingleOrDefault(item => item.Id == backupId && item.ProfileId == profile.Id);
            if (record is null) return new(false, "BackupNotFound", "Choose a completed backup for this server.");
            if (!record.Kind.Equals(profile.Kind, StringComparison.Ordinal) ||
                !record.WorldId.Equals(profile.WorldId, StringComparison.Ordinal))
                return new(false, "BackupProfileMismatch", "That backup belongs to a different game or world configuration.");
            var sourceDirectory = BackupDirectory(profile.Id, backupId);
            try
            {
                var manifest = ReadAndVerifyManifest(record, sourceDirectory);
                var world = SafeWorldRoot(saveDirectory);
                var parent = Directory.GetParent(world)?.FullName
                    ?? throw new InvalidOperationException("A drive root cannot be restored as a server save directory.");
                var restoreId = Guid.NewGuid().ToString("N");
                var stage = Path.Combine(parent, $".togetherserver-restore-{restoreId}.staging");
                var rollback = Path.Combine(parent, $".togetherserver-restore-{restoreId}.rollback");
                var preRestore = Create(profile, BackupKinds.PreRestore, saveDirectory, backupId);
                if (!preRestore.Ok) return new(false, "PreRestoreBackupFailed",
                    "Restore did not begin because the required pre-restore snapshot failed. " + preRestore.Message);
                var warnings = new List<string>();
                try
                {
                    EnsureFreeSpace(parent, record.SizeBytes, profile.Backups.MinimumFreeSpaceMb);
                    Directory.CreateDirectory(stage);
                    CopyTree(Path.Combine(sourceDirectory, "payload"), stage);
                    VerifyTree(stage, manifest.Files);
                    var transaction = new WorldRestoreTransaction
                    {
                        Id = Guid.ParseExact(restoreId, "N"),
                        ProfileId = profile.Id,
                        BackupId = backupId,
                        Kind = profile.Kind,
                        WorldId = profile.WorldId,
                        ProfileWorldDirectory = NormalizeRestoreWorld(profile.WorldDirectory),
                        WorldDirectory = world
                    };
                    SaveRestoreTransaction(transaction);
                    try
                    {
                        Directory.Move(world, rollback);
                        transaction.Phase = WorldRestorePhases.LiveMoved;
                        SaveRestoreTransaction(transaction);
                        Directory.Move(stage, world);
                        transaction.Phase = WorldRestorePhases.ReplacementInstalled;
                        try { SaveRestoreTransaction(transaction); }
                        catch (Exception ex) when (StateWriteFailure(ex))
                        {
                            // The verified replacement is already authoritative.
                            // The older journal phase still lets startup roll forward.
                            warnings.Add("the completed restore journal could not be advanced and will be reconciled on restart");
                        }
                    }
                    catch
                    {
                        var authorized = AuthorizeRestoreTransaction(transaction,
                            data.LoadSettings(), data.LoadBackupCatalog());
                        ReconcileRestoreTransaction(authorized);
                        TryCompleteRestoreTransaction(transaction.Id);
                        throw;
                    }
                    try { SafeDeleteDirectory(rollback, parent); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    { warnings.Add("the replaced directory could not be removed and was left beside the save folder"); }
                    try
                    {
                        var updatedCatalog = data.LoadBackupCatalog();
                        EnforceRetention(profile, updatedCatalog, preRestore.Backup!.Id, backupId);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    { warnings.Add("backup retention could not finish"); }
                    if (!TryCompleteRestoreTransaction(transaction.Id))
                        warnings.Add("the completed restore journal could not be cleared and will be reconciled on restart");
                    if (!data.TryAudit($"backup-restore {profile.Id} {backupId} pre={preRestore.Backup!.Id} {clock.GetUtcNow():O}"))
                        warnings.Add("the local audit entry could not be written");
                    return new(true, warnings.Count == 0 ? "BackupRestored" : "BackupRestoredWithWarnings",
                        "Backup restored while the server was offline. A pre-restore snapshot was retained." +
                        (warnings.Count == 0 ? "" : " Warning: " + string.Join("; ", warnings) + "."), record);
                }
                catch
                {
                    try { SafeDeleteDirectory(stage, parent); }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidOperationException)
                    { /* The journal retains authority if the swap had started. */ }
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or CryptographicException or OverflowException)
            {
                try { RecordFailure(profile.Id, "RestoreFailed", ex.Message); }
                catch (Exception recordEx) when (recordEx is IOException or UnauthorizedAccessException or InvalidDataException) { }
                return new(false, "RestoreFailed", "Restore failed without starting the server: " + ex.Message);
            }
        }
    }

    private void EnforceRetention(ServerProfile profile, BackupCatalog catalog, Guid keepId, Guid? protectedBackupId)
    {
        var retention = Math.Clamp(profile.Backups.RetentionCount, 1, 50);
        var protectedIds = new HashSet<Guid> { keepId };
        if (protectedBackupId is { } protectedId) protectedIds.Add(protectedId);
        var retainedSlots = Math.Max(0, retention - protectedIds.Count);
        var remove = catalog.Records.Where(item => item.ProfileId == profile.Id && !protectedIds.Contains(item.Id))
            .OrderByDescending(item => item.CreatedUtc).Skip(retainedSlots).ToList();
        foreach (var record in remove)
        {
            SafeDeleteDirectory(BackupDirectory(profile.Id, record.Id), ProfileRoot(profile.Id));
            catalog.Records.Remove(record);
        }
        data.SaveBackupCatalog(catalog);
    }

    private BackupManifest ReadAndVerifyManifest(WorldBackupRecord record, string directory)
    {
        if (!Contains(data.BackupsRoot, directory) || !Directory.Exists(directory))
            throw new InvalidDataException("The completed backup directory is missing.");
        var marker = Path.Combine(directory, "complete.json");
        var manifest = File.Exists(marker)
            ? JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(marker), Json)
            : null;
        if (manifest is null || manifest.BackupId != record.Id || manifest.ProfileId != record.ProfileId ||
            !manifest.Kind.Equals(record.Kind, StringComparison.Ordinal) ||
            !manifest.WorldId.Equals(record.WorldId, StringComparison.Ordinal) ||
            manifest.Files.Count != record.FileCount)
            throw new InvalidDataException("The backup completion marker is missing or does not match its catalog record.");
        VerifyTree(Path.Combine(directory, "payload"), manifest.Files);
        return manifest;
    }

    private static (long SizeBytes, List<BackupManifestFile> Files) CopyTree(string source, string destination)
    {
        var files = new List<BackupManifestFile>();
        var total = 0L;
        var queue = new Queue<(string Source, string Destination, string Relative)>();
        queue.Enqueue((source, destination, ""));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var sourceInfo = new DirectoryInfo(current.Source);
            if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Save directories containing links or reparse points are not backed up.");
            Directory.CreateDirectory(current.Destination);
            foreach (var directory in sourceInfo.EnumerateDirectories())
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Save directories containing links or reparse points are not backed up.");
                var relative = Path.Combine(current.Relative, directory.Name);
                queue.Enqueue((directory.FullName, Path.Combine(current.Destination, directory.Name), relative));
            }
            foreach (var file in sourceInfo.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Save directories containing links or reparse points are not backed up.");
                var target = Path.Combine(current.Destination, file.Name);
                using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.SequentialScan);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                long fileBytes = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                    total = checked(total + read);
                    fileBytes = checked(fileBytes + read);
                }
                output.Flush(true);
                files.Add(new(Path.Combine(current.Relative, file.Name).Replace('\\', '/'), fileBytes,
                    Convert.ToHexString(hash.GetHashAndReset())));
            }
        }
        return (total, files.OrderBy(item => item.Path, StringComparer.Ordinal).ToList());
    }

    private static (long SizeBytes, int FileCount) MeasureTree(string source)
    {
        long size = 0;
        var count = 0;
        var queue = new Queue<DirectoryInfo>();
        queue.Enqueue(new DirectoryInfo(source));
        while (queue.Count > 0)
        {
            var directory = queue.Dequeue();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Save directories containing links or reparse points are not backed up.");
            foreach (var child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Save directories containing links or reparse points are not backed up.");
                queue.Enqueue(child);
            }
            foreach (var file in directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Save directories containing links or reparse points are not backed up.");
                size = checked(size + file.Length);
                count++;
            }
        }
        return (size, count);
    }

    private static void VerifyTree(string root, IReadOnlyList<BackupManifestFile> expected)
    {
        if (!Directory.Exists(root)) throw new InvalidDataException("Backup payload is missing.");
        var actual = new List<BackupManifestFile>();
        var measured = CopyTreeToHashes(root);
        actual.AddRange(measured);
        if (actual.Count != expected.Count) throw new InvalidDataException("Backup file count changed.");
        for (var index = 0; index < expected.Count; index++)
        {
            if (!actual[index].Equals(expected[index]))
                throw new InvalidDataException("Backup contents did not match the completed manifest.");
        }
    }

    private static List<BackupManifestFile> CopyTreeToHashes(string root)
    {
        var result = new List<BackupManifestFile>();
        var queue = new Queue<(DirectoryInfo Directory, string Relative)>();
        queue.Enqueue((new DirectoryInfo(root), ""));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if ((current.Directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Backup payload contains a link or reparse point.");
            foreach (var directory in current.Directory.EnumerateDirectories())
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Backup payload contains a link or reparse point.");
                queue.Enqueue((directory, Path.Combine(current.Relative, directory.Name)));
            }
            foreach (var file in current.Directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Backup payload contains a link or reparse point.");
                using var stream = file.OpenRead();
                result.Add(new(Path.Combine(current.Relative, file.Name).Replace('\\', '/'), file.Length,
                    Convert.ToHexString(SHA256.HashData(stream))));
            }
        }
        return result.OrderBy(item => item.Path, StringComparer.Ordinal).ToList();
    }

    private void CleanInterruptedStages()
    {
        try
        {
            if (!Directory.Exists(data.BackupsRoot)) return;
            foreach (var profileRoot in Directory.EnumerateDirectories(data.BackupsRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(profileRoot), "N", out _)) continue;
                foreach (var stage in Directory.EnumerateDirectories(profileRoot, "*.staging", SearchOption.TopDirectoryOnly))
                {
                    try { SafeDeleteDirectory(stage, profileRoot); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void ReconcileInterruptedRestores()
    {
        var transactions = data.LoadState(RestoreTransactionsFile, new List<WorldRestoreTransaction>());
        if (transactions.Count == 0) return;
        var settings = data.LoadSettings();
        var catalog = data.LoadBackupCatalog();
        List<(WorldRestoreTransaction Transaction, WorldRestorePaths Paths)> authorized;
        try
        {
            if (transactions.Count > 100 || transactions.Any(item => item is null) ||
                settings.Profiles is null || settings.Profiles.Any(item => item is null) ||
                catalog.Records is null || catalog.Records.Any(item => item is null) ||
                transactions.Select(item => item.Id).Distinct().Count() != transactions.Count ||
                transactions.Select(item => item.ProfileId).Distinct().Count() != transactions.Count)
                throw new InvalidDataException("Interrupted restore journal entries overlap.");
            authorized = transactions.Select(transaction =>
                (transaction, AuthorizeRestoreTransaction(transaction, settings, catalog))).ToList();
            if (authorized.Select(entry => entry.Item2.World).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                authorized.Count)
                throw new InvalidDataException("Interrupted restore journal entries target the same save directory.");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or
                                   NotSupportedException or OverflowException)
        {
            data.QuarantineState(RestoreTransactionsFile,
                "TogetherServer could not safely bind an interrupted restore to its saved server and backup.", true);
            data.TryAudit($"backup-restore-journal-quarantined {ex.GetType().Name} {clock.GetUtcNow():O}");
            return;
        }

        foreach (var entry in authorized)
        {
            try { ReconcileRestoreTransaction(entry.Paths); }
            catch (InvalidDataException ex)
            {
                data.QuarantineState(RestoreTransactionsFile,
                    "TogetherServer found an ambiguous interrupted restore that requires owner review.", true);
                data.TryAudit($"backup-restore-journal-quarantined layout {ex.GetType().Name} {clock.GetUtcNow():O}");
                return;
            }
            transactions.Remove(entry.Transaction);
            data.SaveState(RestoreTransactionsFile, transactions);
            data.TryAudit($"backup-restore-reconciled {entry.Transaction.ProfileId} {entry.Transaction.BackupId} phase={entry.Transaction.Phase} {clock.GetUtcNow():O}");
        }
    }

    private void SaveRestoreTransaction(WorldRestoreTransaction transaction)
    {
        var transactions = data.LoadState(RestoreTransactionsFile, new List<WorldRestoreTransaction>());
        transactions.RemoveAll(item => item.Id == transaction.Id);
        transactions.Add(transaction);
        data.SaveState(RestoreTransactionsFile, transactions);
    }

    private bool TryCompleteRestoreTransaction(Guid transactionId)
    {
        try
        {
            var transactions = data.LoadState(RestoreTransactionsFile, new List<WorldRestoreTransaction>());
            if (transactions.RemoveAll(item => item.Id == transactionId) > 0)
                data.SaveState(RestoreTransactionsFile, transactions);
            return true;
        }
        catch (Exception ex) when (StateWriteFailure(ex)) { return false; }
    }

    private static WorldRestorePaths AuthorizeRestoreTransaction(WorldRestoreTransaction transaction,
        HostSettings settings, BackupCatalog catalog)
    {
        if (transaction.Id == Guid.Empty || transaction.ProfileId == Guid.Empty || transaction.BackupId == Guid.Empty ||
            string.IsNullOrWhiteSpace(transaction.Kind) || string.IsNullOrWhiteSpace(transaction.WorldId) ||
            string.IsNullOrWhiteSpace(transaction.ProfileWorldDirectory) ||
            !Path.IsPathFullyQualified(transaction.ProfileWorldDirectory) ||
            string.IsNullOrWhiteSpace(transaction.WorldDirectory) || !Path.IsPathFullyQualified(transaction.WorldDirectory) ||
            transaction.Phase is not (WorldRestorePhases.Prepared or WorldRestorePhases.LiveMoved or
                WorldRestorePhases.ReplacementInstalled))
            throw new InvalidDataException("An interrupted restore journal entry is invalid.");
        var profile = settings.Profiles.SingleOrDefault(item => item is not null && item.Id == transaction.ProfileId)
            ?? throw new InvalidDataException("An interrupted restore no longer has a saved server profile.");
        if (!string.Equals(profile.Kind, transaction.Kind, StringComparison.Ordinal) ||
            !string.Equals(profile.WorldId, transaction.WorldId, StringComparison.Ordinal) ||
            !SamePath(profile.WorldDirectory, transaction.ProfileWorldDirectory) ||
            !SamePath(ExpectedSaveDirectory(profile), transaction.WorldDirectory))
            throw new InvalidDataException("An interrupted restore does not match its saved server profile.");
        var backup = catalog.Records.SingleOrDefault(item => item is not null && item.Id == transaction.BackupId &&
            item.ProfileId == transaction.ProfileId);
        if (backup is null || !string.Equals(backup.Kind, transaction.Kind, StringComparison.Ordinal) ||
            !string.Equals(backup.WorldId, transaction.WorldId, StringComparison.Ordinal))
            throw new InvalidDataException("An interrupted restore does not match a completed backup record.");
        var world = NormalizeRestoreWorld(transaction.WorldDirectory);
        var parent = Directory.GetParent(world)?.FullName
            ?? throw new InvalidDataException("An interrupted restore targeted a drive root.");
        var token = transaction.Id.ToString("N");
        var stage = Path.Combine(parent, $".togetherserver-restore-{token}.staging");
        var rollback = Path.Combine(parent, $".togetherserver-restore-{token}.rollback");
        foreach (var path in new[] { world, stage, rollback })
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("An interrupted restore path is a link or reparse point.");
        var hasWorld = Directory.Exists(world);
        var hasStage = Directory.Exists(stage);
        var hasRollback = Directory.Exists(rollback);
        if (hasWorld && hasRollback && hasStage || !hasWorld && !hasRollback && !hasStage)
            throw new InvalidDataException("Interrupted restore directories are ambiguous and require owner review.");
        return new(world, parent, stage, rollback);
    }

    private static void ReconcileRestoreTransaction(WorldRestorePaths paths)
    {
        var hasWorld = Directory.Exists(paths.World);
        var hasStage = Directory.Exists(paths.Stage);
        var hasRollback = Directory.Exists(paths.Rollback);
        if (hasWorld)
        {
            // A missing stage means the verified replacement reached its final
            // path. Keep it and finish cleanup; otherwise the swap never began.
            if (hasStage) SafeDeleteDirectory(paths.Stage, paths.Parent);
            if (hasRollback) SafeDeleteDirectory(paths.Rollback, paths.Parent);
            return;
        }
        if (hasRollback)
        {
            Directory.Move(paths.Rollback, paths.World);
            if (hasStage) SafeDeleteDirectory(paths.Stage, paths.Parent);
            return;
        }
        if (hasStage)
        {
            // The stage was manifest-verified before the journal was created.
            Directory.Move(paths.Stage, paths.World);
            return;
        }
        throw new InvalidDataException("Interrupted restore journal has no live, staged, or rollback directory.");
    }

    private static string ExpectedSaveDirectory(ServerProfile profile) => profile.Kind switch
    {
        GameKinds.MinecraftJava => Path.Combine(profile.WorldDirectory, profile.WorldId),
        GameKinds.MinecraftBedrock => Path.Combine(profile.WorldDirectory, "worlds", profile.WorldId),
        _ => profile.WorldDirectory
    };

    private static bool SamePath(string left, string right) =>
        NormalizeRestoreWorld(left).Equals(NormalizeRestoreWorld(right), StringComparison.OrdinalIgnoreCase);

    private static bool StateWriteFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or ArgumentException or JsonException;

    private void RecordFailure(Guid profileId, string code, string message)
    {
        var catalog = data.LoadBackupCatalog();
        catalog.Failures.RemoveAll(item => item.ProfileId == profileId);
        catalog.Failures.Add(new BackupFailure
        {
            ProfileId = profileId,
            FailedUtc = clock.GetUtcNow(),
            Code = code,
            Message = message.Length > 500 ? message[..500] : message
        });
        data.SaveBackupCatalog(catalog);
        data.TryAudit($"backup-failed {profileId} code={code} {clock.GetUtcNow():O}");
    }

    private string ProfileRoot(Guid profileId) => Path.Combine(data.BackupsRoot, profileId.ToString("N"));
    private string BackupDirectory(Guid profileId, Guid backupId) =>
        Path.Combine(ProfileRoot(profileId), backupId.ToString("N") + ".backup");

    private static string SafeWorldRoot(string value)
    {
        var path = NormalizeRestoreWorld(value);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The configured server save directory is missing.");
        return path;
    }

    private static string NormalizeRestoreWorld(string value)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (string.Equals(path, Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            Directory.GetParent(path) is null)
            throw new InvalidOperationException("A drive root cannot be used as a server save directory for backup or restore.");
        return path;
    }

    private void EnsureFreeSpace(string destination, long copyBytes, long minimumFreeSpaceMb)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("Backup destination drive could not be identified.");
        var available = availableSpace(root);
        var reserve = checked(Math.Clamp(minimumFreeSpaceMb, 0, 1_048_576) * 1024L * 1024L);
        if (available < copyBytes || available - copyBytes < reserve)
            throw new IOException("The configured free-space reserve would be crossed by this copy.");
    }

    private static bool Contains(string parent, string child)
    {
        var parentPath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var childPath = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return childPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void SafeDeleteDirectory(string path, string requiredParent)
    {
        if (!Directory.Exists(path)) return;
        var full = Path.GetFullPath(path);
        if (!Contains(requiredParent, full) || string.Equals(full, Path.GetFullPath(requiredParent), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refused to remove a directory outside the expected staging or backup root.");
        Directory.Delete(full, true);
    }
}
