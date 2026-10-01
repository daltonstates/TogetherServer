using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record SetupFileSnapshot(string Key, byte[]? Content);
internal sealed record ServerSetupSnapshot(int Version, Guid ProfileId, string Kind, string WorldId,
    string GameVersion, string AddOnStateToken, IReadOnlyList<ServerAddOnItem> AddOns,
    IReadOnlyList<SetupFileSnapshot> Files);

internal static class ServerSetupSnapshots
{
    private const int MaximumFileBytes = 256 * 1024;
    private const int MaximumSnapshotBytes = 3 * 1024 * 1024;

    public static byte[] Capture(ServerProfile profile, LocalData data)
    {
        var files = new List<SetupFileSnapshot>();
        foreach (var (key, path) in ServerFiles.SnapshotPaths(profile))
        {
            if (!File.Exists(path)) { files.Add(new(key, null)); continue; }
            if (!PlainFile(path) || new FileInfo(path).Length > MaximumFileBytes)
                throw new InvalidDataException("A reviewed server configuration is linked or too large for a setup checkpoint.");
            files.Add(new(key, File.ReadAllBytes(path)));
        }
        var addons = ServerAddOns.List(data, profile);
        if (!addons.Ok) throw new InvalidDataException("The add-on inventory is unavailable for a complete setup checkpoint.");
        if (addons.Items.Any(item => item.Type == "External shared pack"))
            throw new InvalidDataException("Active shared Bedrock packs are outside this world's checkpoint.");
        var snapshot = new ServerSetupSnapshot(1, profile.Id, profile.Kind, profile.WorldId,
            addons.GameVersion, addons.StateToken, addons.Items, files);
        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (plain.Length > MaximumSnapshotBytes)
            throw new InvalidDataException("The server setup is too large for a protected checkpoint.");
        return ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
    }

    public static ServerSetupSnapshot Read(ServerProfile profile, byte[] encrypted)
    {
        if (encrypted.Length is < 1 or > MaximumSnapshotBytes + 512)
            throw new InvalidDataException("The protected setup checkpoint is too large.");
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        var snapshot = JsonSerializer.Deserialize<ServerSetupSnapshot>(plain);
        if (snapshot is null || snapshot.Version != 1 || snapshot.ProfileId != profile.Id ||
            snapshot.Kind != profile.Kind || snapshot.WorldId != profile.WorldId ||
            snapshot.Files is null || snapshot.AddOns is null)
            throw new InvalidDataException("The setup checkpoint belongs to a different server or format.");
        var paths = ServerFiles.SnapshotPaths(profile);
        if (snapshot.Files.Count != paths.Count ||
            snapshot.Files.Select(file => file.Key).Distinct(StringComparer.Ordinal).Count() != paths.Count ||
            snapshot.Files.Any(file => !paths.ContainsKey(file.Key) || file.Content?.Length > MaximumFileBytes))
            throw new InvalidDataException("The setup checkpoint contains unexpected files.");
        return snapshot;
    }

    public static void Restore(ServerProfile profile, ServerSetupSnapshot snapshot)
    {
        var paths = ServerFiles.SnapshotPaths(profile);
        var prepared = new List<(string Path, byte[]? Before, byte[]? After)>();
        foreach (var item in snapshot.Files)
        {
            var path = paths[item.Key];
            var parent = Path.GetDirectoryName(path)!;
            if (!PlainDirectory(parent) || File.Exists(path) && !PlainFile(path))
                throw new InvalidDataException("A configuration path is linked or unavailable.");
            var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (before?.Length > MaximumFileBytes)
                throw new InvalidDataException("A current configuration is too large for safe restore.");
            prepared.Add((path, before, item.Content));
        }
        var changed = new List<(string Path, byte[]? Before)>();
        try
        {
            foreach (var (path, before, after) in prepared)
            {
                if (BytesEqual(before, after)) continue;
                Write(path, after);
                changed.Add((path, before));
            }
        }
        catch
        {
            for (var index = changed.Count - 1; index >= 0; index--)
            {
                try { Write(changed[index].Path, changed[index].Before); }
                catch (Exception) { /* Caller reports that manual recovery may be required. */ }
            }
            throw;
        }
    }

    private static void Write(string path, byte[]? content)
    {
        if (content is null) { if (File.Exists(path)) File.Delete(path); return; }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool BytesEqual(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private static bool PlainFile(string path) => File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
        PlainDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    private static bool PlainDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
