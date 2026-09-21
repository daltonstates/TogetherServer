using System.Security.Cryptography;
using System.Text;

namespace TogetherServer;

public sealed record StopListResult(bool Ok, string Code, string Message);

// Valheim's permittedlist.txt excludes every player not named in it. Remote Stop
// uses that server-side restriction together with fresh reports from every
// enrolled PC. The file must be in the save directory when Valheim starts and
// remain unchanged for this run.
public static class RemoteStopSafety
{
    public static bool ValidPlatformUserId(string? value)
    {
        if (value is null || value.Length is < 3 or > 128) return false;
        var separator = value.IndexOf('_');
        return separator > 0 && separator < value.Length - 1 &&
            value[..separator].All(char.IsAsciiLetterOrDigit) &&
            value[(separator + 1)..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }

    public static string FingerprintAtStart(ServerProfile profile)
    {
        if (profile.Kind != "Valheim") return "";
        try
        {
            using var file = OpenList(profile.WorldDirectory);
            return file is null ? "" : ReadList(file)?.Hash ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DecoderFallbackException)
        { return ""; }
    }

    public static StopListResult CreateList(HostSnapshot snapshot, Guid profileId, LocalData data, PairingService pairing)
    {
        var profile = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == profileId);
        if (profile?.Kind != "Valheim") return new(false, "InvalidProfile", "Choose a saved Valheim server.");
        if (data.LoadRuns().Any(item => item.ProfileId == profileId))
            return new(false, "ServerRunning", "Stop this server locally before changing who may join.");
        var managedNewWorld = Path.GetFullPath(profile.WorldDirectory).Equals(data.NewWorldDirectory(profile.Id), StringComparison.OrdinalIgnoreCase);
        var importedCopy = ValheimSetup.IsImportedWorld(data, profile.Id, profile.WorldDirectory);
        if (!managedNewWorld && !importedCopy)
            return new(false, "CustomSavePath", "Create the permitted-player list manually for a custom save folder.");
        if (!pairing.TryAssignedPlayerIds(profileId, out var friendIds, out var reason))
            return new(false, "PlayerIdsMissing", reason);
        var ownerId = snapshot.Settings.OwnerPlatformUserId ?? "";
        if (ownerId.Length > 0 && !ValidPlatformUserId(ownerId))
            return new(false, "InvalidPlayerId", "Enter a valid owner Valheim player ID.");
        var ids = friendIds.ToHashSet(StringComparer.Ordinal);
        if (ownerId.Length > 0 && !ids.Add(ownerId))
            return new(false, "DuplicatePlayerId", "The owner's player ID must differ from each Friend's ID.");
        try
        {
            if (managedNewWorld) Directory.CreateDirectory(profile.WorldDirectory);
            var path = Path.Combine(profile.WorldDirectory, "permittedlist.txt");
            if (File.Exists(path))
            {
                using var existing = OpenList(profile.WorldDirectory);
                var list = existing is null ? null : ReadList(existing);
                return list is not null && list.Value.Ids.ToHashSet(StringComparer.Ordinal).SetEquals(ids)
                    ? new(true, "AlreadyMatches", "The permitted-player list already matches these PCs. Restart the server if it was changed while running.")
                    : new(false, "ListExists", "An existing permitted-player list differs. Review it manually; TogetherServer will not overwrite it.");
            }
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                foreach (var id in ids.Order(StringComparer.Ordinal)) writer.WriteLine(id);
            data.Audit($"permitted-list-created {profile.Id} {DateTimeOffset.UtcNow:O}");
            return new(true, "ListCreated", "Player-only access is ready. Start the server to load it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DecoderFallbackException)
        { return new(false, "ListUnavailable", "The permitted-player list could not be created: " + ex.Message); }
    }

    public static StopPermit TryAcquire(HostSnapshot snapshot, Guid profileId, LocalData data, PairingService pairing)
    {
        var profile = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == profileId);
        var run = snapshot.Runs.SingleOrDefault(item => item.ProfileId == profileId);
        if (profile is null) return StopPermit.Denied("The saved server is unavailable.");
        if (profile.Kind != GameKinds.Valheim)
            return StopPermit.Denied("Remote Stop is unavailable for this game until player coverage and safe Stop are verified.");
        if (run?.State != "Ready")
            return StopPermit.Denied("Remote Stop needs a running, ready Valheim server.");
        var recorded = data.LoadRuns().SingleOrDefault(item => item.ProfileId == profileId);
        if (recorded is null || string.IsNullOrEmpty(recorded.PermittedListSha256))
            return StopPermit.Denied("Start this server with a complete permittedlist.txt before allowing remote Stop.");
        if (!pairing.TryCoveredPlayerIds(profileId, out var friendIds, out var reason)) return StopPermit.Denied(reason);
        var ownerId = snapshot.Settings.OwnerPlatformUserId ?? "";
        if (ownerId.Length > 0 && !ValidPlatformUserId(ownerId))
            return StopPermit.Denied("Enter the owner's Valheim player ID in Stop safety settings.");
        if (ownerId.Length > 0 && ClientMonitor.IsRunning(snapshot.Settings.OwnerClientExecutablePath) != false)
            return StopPermit.Denied("The owner's Valheim game is running or its process check is unknown.");
        if (ownerId.Length == 0 && ClientMonitor.IsRunning(snapshot.Settings.OwnerClientExecutablePath) == true)
            return StopPermit.Denied("The owner's Valheim game is running.");
        var expected = friendIds.ToHashSet(StringComparer.Ordinal);
        if (ownerId.Length > 0 && !expected.Add(ownerId))
            return StopPermit.Denied("The owner's player ID must differ from every Friend's ID.");
        FileStream? file = null;
        try
        {
            file = OpenList(profile.WorldDirectory);
            if (file is null) return StopPermit.Denied("permittedlist.txt is missing from this server's save directory.");
            var list = ReadList(file);
            if (list is null || !string.Equals(list.Value.Hash, recorded.PermittedListSha256, StringComparison.Ordinal))
                return StopPermit.Denied("permittedlist.txt changed after this server started. Restart it locally before remote Stop.");
            if (!list.Value.Ids.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
                return StopPermit.Denied("permittedlist.txt must contain exactly the owner and paired Friend player IDs.");
            var permit = new StopPermit(file, pairing, profileId, snapshot.Settings.OwnerClientExecutablePath, ownerId.Length > 0);
            file = null;
            return permit;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DecoderFallbackException)
        { return StopPermit.Denied("The Valheim permitted-player list could not be checked: " + ex.Message); }
        finally { file?.Dispose(); }
    }

    private static FileStream? OpenList(string saveDirectory)
    {
        var path = Path.Combine(saveDirectory, "permittedlist.txt");
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    private static (string Hash, string[] Ids)? ReadList(FileStream file)
    {
        file.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(file));
        file.Position = 0;
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), true, leaveOpen: true);
        var ids = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var id = line.Trim();
            if (id.Length == 0) continue;
            if (!ValidPlatformUserId(id) || ids.Contains(id, StringComparer.Ordinal)) return null;
            ids.Add(id);
        }
        return ids.Count == 0 ? null : (hash, ids.ToArray());
    }
}

public sealed class StopPermit : IDisposable
{
    private readonly FileStream? list;
    private readonly PairingService? pairing;
    private readonly Guid profileId;
    private readonly string ownerClientPath;
    private readonly bool ownerAllowed;

    internal StopPermit(FileStream list, PairingService pairing, Guid profileId, string ownerClientPath, bool ownerAllowed)
    {
        this.list = list;
        this.pairing = pairing;
        this.profileId = profileId;
        this.ownerClientPath = ownerClientPath;
        this.ownerAllowed = ownerAllowed;
        Allowed = true;
        Reason = "All permitted players have fresh closed-game reports.";
    }

    private StopPermit(string reason)
    {
        ownerClientPath = "";
        Reason = reason;
    }

    public bool Allowed { get; }
    public string Reason { get; }
    public static StopPermit Denied(string reason) => new(reason);
    public bool StillSafe() => Allowed && pairing!.AllKnownNotPlaying(profileId) &&
        (ownerAllowed ? ClientMonitor.IsRunning(ownerClientPath) == false : ClientMonitor.IsRunning(ownerClientPath) != true);
    public void Dispose() => list?.Dispose();
}
