using System.IO.Compression;
using System.Security.Cryptography;

namespace TogetherServer;

public sealed record FactorioImportRequest(Guid ProfileId);
public sealed record FactorioImportResult(bool Ok, string Code, string Message,
    string? WorldId = null, string? WorldDirectory = null);

internal static class FactorioSetup
{
    public static FactorioImportResult ImportCopy(LocalData data, Guid profileId, string? sourceSave)
    {
        if (profileId == Guid.Empty || string.IsNullOrWhiteSpace(sourceSave) ||
            !Path.IsPathFullyQualified(sourceSave))
            return new(false, "InvalidFactorioSave", "Choose an existing Factorio .zip save.");
        string source;
        try { source = Path.GetFullPath(sourceSave); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return new(false, "InvalidFactorioSave", "The selected Factorio save path is invalid."); }
        var worldId = Path.GetFileNameWithoutExtension(source);
        try
        {
            if (!Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                !ValheimSetup.ValidWorldId(worldId) || !File.Exists(source) ||
                !ManagedImportFiles.PlainFile(source))
                return new(false, "InvalidFactorioSave", "Choose an existing Factorio .zip save that is not a link.");
            if (AppInstance.ContainsPath(data.FactorioServersRoot, source))
                return new(false, "ManagedFactorioSave", "Choose the original save outside TogetherServer's managed copy.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Security.SecurityException)
        { return new(false, "FactorioSaveUnavailable", "The selected Factorio save could not be read. No copy was made."); }

        var profileRoot = Path.Combine(data.FactorioServersRoot, profileId.ToString("N"));
        var destination = Path.Combine(profileRoot, worldId);
        var stage = Path.Combine(data.FactorioServersRoot, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            ManagedImportFiles.EnsurePlainDirectory(data.FactorioServersRoot, "The managed Factorio root");
            ManagedImportFiles.EnsurePlainDirectory(profileRoot, "The managed Factorio profile folder");
            if (Directory.Exists(destination))
                return new(false, "AlreadyImported",
                    "This save name already has a managed copy for this server. The existing copy was not overwritten.");
            ManagedImportFiles.EnsurePlainDirectory(stage, "The Factorio import staging folder");
            var target = Path.Combine(stage, worldId + ".zip");
            var length = ManagedImportFiles.CopyVerified(source, target);
            using (var archive = ZipFile.OpenRead(target))
                if (archive.Entries.Count == 0)
                    throw new InvalidDataException("The selected Factorio save archive is empty.");
            Directory.Move(stage, destination);
            stage = "";
            data.TryAudit($"factorio-save-imported {profileId} world={worldId} bytes={length} {DateTimeOffset.UtcNow:O}");
            return new(true, "FactorioSaveImported",
                "Factorio save copied and verified. The original file was left unchanged.", worldId, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   CryptographicException or OverflowException or ArgumentException or
                                   System.Security.SecurityException)
        {
            data.TryAudit($"factorio-save-import-failed {profileId} {ex.GetType().Name} {DateTimeOffset.UtcNow:O}");
            return new(false, "FactorioImportFailed",
                "The Factorio save could not be copied and verified. The original file was left unchanged.");
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(stage) && Directory.Exists(stage) &&
                    AppInstance.ContainsPath(data.FactorioServersRoot, stage)) Directory.Delete(stage, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static bool IsImportedCopy(LocalData data, ServerProfile profile)
    {
        if (profile.Id == Guid.Empty || !ValheimSetup.ValidWorldId(profile.WorldId) ||
            string.IsNullOrWhiteSpace(profile.WorldDirectory)) return false;
        try
        {
            var expected = Path.Combine(data.FactorioServersRoot, profile.Id.ToString("N"), profile.WorldId);
            return Path.GetFullPath(profile.WorldDirectory).Equals(Path.GetFullPath(expected),
                       StringComparison.OrdinalIgnoreCase) &&
                   ManagedImportFiles.PlainDirectory(data.FactorioServersRoot) &&
                   ManagedImportFiles.PlainDirectory(Path.GetDirectoryName(expected)!) &&
                   ManagedImportFiles.PlainDirectory(expected) &&
                   ManagedImportFiles.PlainFile(Path.Combine(expected, profile.WorldId + ".zip"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                   NotSupportedException or PathTooLongException or System.Security.SecurityException)
        { return false; }
    }

}
