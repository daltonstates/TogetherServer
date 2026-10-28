using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record UpdateArtifactCleanupResult(int RemovedDownloads, int RemovedCheckpoints, int PreservedUnknown);

// This owns only flat update artifacts, never live settings, game runtimes,
// worlds or world backups. Unknown contents and unresolved handoffs are kept.
internal static class UpdateArtifactCleanup
{
    internal const string LeaseName = ".artifact.lock";
    private const string HandoffName = "handoff.json";
    private const int MaximumDirectories = 1024;
    private const int MaximumRemovalsPerPass = 8;
    private static readonly TimeSpan AbandonedAge = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Handoff(int Schema, string TargetSha256, string? CheckpointName);
    private sealed record Checkpoint(string Directory, DateTimeOffset CreatedUtc, string PreviousSha256, IReadOnlySet<string> Files);

    internal static FileStream Lease(string directory) => new(Path.Combine(directory, LeaseName),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    internal static void MarkPending(string directory, string targetSha256, StateCheckpointReference? checkpoint)
    {
        if (!Hash(targetSha256) || checkpoint is not null && !Named(Path.GetFileName(checkpoint.Directory), ".checkpoint"))
            throw new InvalidDataException("The update handoff identity is invalid.");
        var path = Path.Combine(directory, HandoffName);
        File.WriteAllBytes(path + ".new", JsonSerializer.SerializeToUtf8Bytes(
            new Handoff(1, targetSha256, checkpoint is null ? null : Path.GetFileName(checkpoint.Directory)), Json));
        File.Move(path + ".new", path, true);
    }

    internal static void MarkFailed(string directory)
    {
        try { File.WriteAllText(Path.Combine(directory, "failed.signal"), "failed"); }
        catch (Exception error) when (Failure(error)) { }
    }

    internal static bool DiscardIncomplete(string dataRoot, string directory)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(dataRoot, "updates"));
            var full = Path.GetFullPath(directory);
            return Named(Path.GetFileName(full)) &&
                Path.GetDirectoryName(full)!.Equals(root, StringComparison.OrdinalIgnoreCase) && DeleteDownload(root, full);
        }
        catch (Exception error) when (Failure(error)) { return false; }
    }

    internal static UpdateArtifactCleanupResult Run(string dataRoot, string executablePath, string? preparedPath = null,
        string? protectedCheckpoint = null, DateTimeOffset? nowUtc = null)
    {
        var removedDownloads = 0; var removedCheckpoints = 0; var unknown = 0;
        try
        {
            dataRoot = Path.GetFullPath(dataRoot);
            executablePath = Path.GetFullPath(executablePath);
            if (!PlainDirectory(dataRoot)) return new(0, 0, 1);
            var updates = Path.Combine(dataRoot, "updates");
            var checkpoints = Path.Combine(dataRoot, "update-checkpoints");
            var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (protectedCheckpoint is not null) protectedNames.Add(Path.GetFileName(protectedCheckpoint));
            var now = nowUtc ?? DateTimeOffset.UtcNow;
            var canPruneCheckpoints = true;
            string? installedHash = null;
            foreach (var directory in Children(updates))
            {
                if (!Named(Path.GetFileName(directory)) || !PlainDirectory(directory))
                { unknown++; canPruneCheckpoints = false; continue; }
                var active = Contains(directory, executablePath) || preparedPath is not null && Contains(directory, Path.GetFullPath(preparedPath));
                var marker = Path.Combine(directory, HandoffName);
                var completed = false;
                if (File.Exists(marker))
                {
                    var handoff = ReadHandoff(marker);
                    if (handoff is null) { unknown++; canPruneCheckpoints = false; continue; }
                    installedHash ??= FileHash(executablePath);
                    completed = installedHash is not null && installedHash.Equals(handoff.TargetSha256, StringComparison.OrdinalIgnoreCase);
                    if (!completed && !PlainFile(Path.Combine(directory, "failed.signal")))
                    {
                        active = true;
                        if (handoff.CheckpointName is not null) protectedNames.Add(handoff.CheckpointName);
                    }
                }
                if (active || !completed && new DirectoryInfo(directory).LastWriteTimeUtc > now.Subtract(AbandonedAge).UtcDateTime) continue;
                if (removedDownloads < MaximumRemovalsPerPass && DeleteDownload(updates, directory)) removedDownloads++;
            }
            if (!canPruneCheckpoints) return new(removedDownloads, 0, unknown);
            var completedCheckpoints = new List<Checkpoint>();
            foreach (var directory in Children(checkpoints))
            {
                var name = Path.GetFileName(directory);
                if (!PlainDirectory(directory)) { unknown++; continue; }
                if (Named(name, ".staging"))
                {
                    if (removedCheckpoints < MaximumRemovalsPerPass && !protectedNames.Contains(name) &&
                        new DirectoryInfo(directory).LastWriteTimeUtc <= now.Subtract(AbandonedAge).UtcDateTime &&
                        DeleteCheckpoint(checkpoints, directory)) removedCheckpoints++;
                    continue;
                }
                if (!Named(name, ".checkpoint")) { unknown++; continue; }
                var manifestPath = Path.Combine(directory, "checkpoint-manifest.json");
                if (!PlainFile(manifestPath) || new FileInfo(manifestPath).Length > 2 * 1024 * 1024) { unknown++; continue; }
                StateCheckpointManifest? manifest;
                try { manifest = JsonSerializer.Deserialize<StateCheckpointManifest>(File.ReadAllBytes(manifestPath), Json); }
                catch (JsonException) { unknown++; continue; }
                if (manifest is not { SchemaVersion: 1 } || manifest.Id == Guid.Empty ||
                    manifest.Id.ToString("N") + ".checkpoint" != name || manifest.CreatedUtc == default ||
                    manifest.CreatedUtc > now.AddMinutes(5) || !Hash(manifest.PreviousExecutableSha256) || manifest.Files is null ||
                    manifest.Files.Count > 250 || manifest.Files.Any(file => file is null || !FlatCheckpointName(file.Name) ||
                        file.Length is < 0 or > 32L * 1024 * 1024 || !Hash(file.Sha256)) ||
                    manifest.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count ||
                    manifest.Files.Sum(file => file.Length) > 128L * 1024 * 1024) { unknown++; continue; }
                completedCheckpoints.Add(new(directory, manifest.CreatedUtc, manifest.PreviousExecutableSha256,
                    manifest.Files.Select(file => file.Name).Append("checkpoint-manifest.json").ToHashSet(StringComparer.OrdinalIgnoreCase)));
            }
            var ordered = completedCheckpoints.OrderByDescending(item => item.CreatedUtc).ToArray();
            foreach (var item in ordered.Take(2)) protectedNames.Add(Path.GetFileName(item.Directory));
            // The single verified .previous EXE stays beside the installed app.
            // Keep its newest matching local-state checkpoint across upgrades.
            var previousHash = FileHash(executablePath + ".previous");
            var rollback = ordered.FirstOrDefault(item => item.PreviousSha256.Equals(previousHash, StringComparison.OrdinalIgnoreCase));
            if (rollback is not null) protectedNames.Add(Path.GetFileName(rollback.Directory));
            foreach (var item in ordered)
                if (removedCheckpoints < MaximumRemovalsPerPass && !protectedNames.Contains(Path.GetFileName(item.Directory)) &&
                    DeleteCheckpoint(checkpoints, item.Directory, item.Files))
                    removedCheckpoints++;
        }
        catch (Exception error) when (Failure(error)) { unknown++; }
        return new(removedDownloads, removedCheckpoints, unknown);
    }

    private static string[] Children(string root)
    {
        if (!Directory.Exists(root)) return [];
        if (!PlainDirectory(root)) throw new InvalidDataException("Update storage is linked.");
        var children = Directory.EnumerateDirectories(root).Take(MaximumDirectories + 1).ToArray();
        if (children.Length > MaximumDirectories) throw new InvalidDataException("Update storage needs review.");
        return children;
    }

    private static Handoff? ReadHandoff(string path)
    {
        try
        {
            if (!PlainFile(path) || new FileInfo(path).Length > 4096) return null;
            var value = JsonSerializer.Deserialize<Handoff>(File.ReadAllBytes(path), Json);
            return value is { Schema: 1 } && Hash(value.TargetSha256) &&
                (value.CheckpointName is null || Named(value.CheckpointName, ".checkpoint")) ? value : null;
        }
        catch (Exception error) when (Failure(error)) { return null; }
    }

    private static bool DeleteDownload(string root, string directory)
    {
        try
        {
            static bool Allowed(string name) => name is AppUpdater.AssetName or "TogetherServer-updater.exe" or
                "ready.signal" or "failed.signal" or LeaseName or HandoffName or "handoff.json.new";
            if (!SafeFlat(root, directory, Allowed)) return false;
            using (Lease(directory))
            {
                if (!SafeFlat(root, directory, Allowed)) return false;
                DeleteFiles(root, directory, LeaseName);
            }
            if (!SafeFlat(root, directory, name => name == LeaseName)) return false;
            File.Delete(Path.Combine(directory, LeaseName));
            Directory.Delete(directory, false);
            return true;
        }
        catch (Exception error) when (Failure(error)) { return false; }
    }

    private static bool DeleteCheckpoint(string root, string directory, IReadOnlySet<string>? expectedFiles = null)
    {
        try
        {
            if (!SafeFlat(root, directory, name => expectedFiles?.Contains(name) ?? FlatCheckpointName(name))) return false;
            DeleteFiles(root, directory);
            Directory.Delete(directory, false);
            return true;
        }
        catch (Exception error) when (Failure(error)) { return false; }
    }

    private static bool SafeFlat(string root, string directory, Func<string, bool> allowed) =>
        PlainDirectory(Path.GetDirectoryName(root)!) && PlainDirectory(root) && PlainDirectory(directory) &&
        Path.GetDirectoryName(Path.GetFullPath(directory))!.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
        Directory.EnumerateFileSystemEntries(directory).Take(252).ToArray() is { Length: <= 251 } entries &&
        entries.All(path => PlainFile(path) && allowed(Path.GetFileName(path)));

    private static void DeleteFiles(string root, string directory, string? except = null)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (Path.GetFileName(path) == except) continue;
            if (!PlainDirectory(Path.GetDirectoryName(root)!) || !PlainDirectory(root) || !PlainDirectory(directory) || !PlainFile(path))
                throw new InvalidDataException("An update artifact changed during cleanup.");
            File.Delete(path);
        }
    }

    private static bool Named(string value, string suffix = "") => value.Length == 32 + suffix.Length &&
        value.EndsWith(suffix, StringComparison.Ordinal) && Guid.TryParseExact(value[..32], "N", out _);
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool FlatCheckpointName(string name) => !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Path.GetExtension(name) is ".json" or ".protected";
    private static bool Contains(string directory, string file) => file.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) +
        Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool PlainDirectory(string path) => Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    private static bool PlainFile(string path) => File.Exists(path) && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
    private static string? FileHash(string path)
    {
        if (!PlainFile(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool Failure(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or
        InvalidDataException or JsonException or System.Security.SecurityException or CryptographicException;
}
