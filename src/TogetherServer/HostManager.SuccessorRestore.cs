using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record SuccessorRestoreRequest(string RecordHash, TakeoverLocalSetup Setup,
    string Name, string ServerName, string? GamePassword = null,
    string? ExecutablePath = null, string? PreparedServerRoot = null,
    int? FactorioRconPort = null);
public sealed record SuccessorRestoreResult(bool Ok, string Code, string Message,
    Guid? ProfileId = null, string? VersionHash = null,
    IReadOnlyList<string>? PendingChecks = null);
public sealed record SuccessorRestoreStatus(bool Staged, bool Restored,
    string? RecordHash, string Message, IReadOnlyList<string> PendingChecks,
    string? PreparedServerRoot = null);

internal sealed record SuccessorRestoreState(int Schema, Guid GroupId, string RecordHash,
    string VersionHash, string WorldDirectory);

public sealed partial class HostManager
{
    private static string SuccessorRestoreName(Guid profileId) =>
        $"successor-restore-{profileId:N}.protected";

    internal async Task<SharedWorldRouteProof?> SignSuccessorRouteProofAsync(Guid profileId,
        string recordHash, SharedWorldRouteChallenge challenge, string tlsFingerprint)
    {
        await gate.WaitAsync();
        try
        {
            if (challenge is null || profileId == Guid.Empty || recordHash.Length != 64 ||
                !recordHash.All(Uri.IsHexDigit) || !SharedWorldRouteTrust.DirectIpAddress(
                    settings.CompanionEndpoint) || !settings.CompanionListeningEnabled)
                return null;
            var restoredBytes = data.LoadProtected(SuccessorRestoreName(profileId));
            var restored = restoredBytes is null ? null :
                JsonSerializer.Deserialize<SuccessorRestoreState>(restoredBytes);
            var record = authority.ReadUniqueHead(profileId);
            if (restored is not { Schema: 1 } || record is null ||
                record.RecordHash != recordHash ||
                !WorldAuthorityTrust.Verify(record) || restored.GroupId != record.Proposal.GroupId ||
                restored.RecordHash != recordHash ||
                restored.VersionHash != record.Version.VersionHash ||
                record.Proposal.CandidateAddress != settings.CompanionEndpoint ||
                settings.Profiles.SingleOrDefault(item => item.Id == profileId) is not { } profile ||
                profile.WorldDirectory != restored.WorldDirectory ||
                !SharedWorldRouteTrust.VerifyChallenge(challenge, record, clock.GetUtcNow()))
                return null;
            var member = record.Roster.Members.SingleOrDefault(item =>
                item.PublicKey == record.Proposal.CandidatePublicKey);
            if (member is not { Revoked: false, Grants.EligibleHost: true } ||
                member.AccessExpiresUtc is { } expiry && expiry <= clock.GetUtcNow()) return null;
            var bytes = data.LoadProtected($"shared-world-pc-signing-{member.DeviceId:N}.protected");
            if (bytes is null) return null;
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(bytes, out _);
            if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) !=
                record.Proposal.CandidatePublicKey) return null;
            return SharedWorldRouteTrust.Sign(profileId, recordHash, challenge.Nonce,
                settings.CompanionEndpoint, tlsFingerprint, key);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return null; }
        finally { gate.Release(); }
    }

    public async Task<SuccessorRestoreStatus> SuccessorRestoreStatusAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var records = authority.Read(profileId);
            var heads = records.Where(item => !records.Any(child =>
                child.Proposal.ParentAuthorityHash == item.RecordHash)).ToArray();
            if (heads.Length != 1 || heads[0].Proposal.Kind != "Planned")
                return new(false, false, null,
                    "No single signed planned handoff is staged for this world.", []);
            var record = heads[0];
            var stage = Path.Combine(data.RootPath, "shared-world-staged",
                profileId.ToString("N"), record.RecordHash);
            PlannedHandoffReceiver.VerifyStage(data.RootPath, stage, record);
            var restored = settings.Profiles.Any(item => item.Id == profileId) &&
                data.HasProtected(SuccessorRestoreName(profileId));
            return new(true, restored, record.RecordHash,
                restored ? "The verified copy is in local managed storage. Hosting checks are pending." :
                    "The signed final save is staged. Review the local setup before restoring it.",
                ["Test the direct-IP Friend control route from another PC.",
                 "Run a disposable managed game rehearsal and test a real Friend join.",
                 "Confirm a recognizable change survives a graceful restart."],
                record.Version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock
                    ? SuccessorWorldRoot(data, record.Version) : null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, false, null,
                "The staged handoff could not be verified. Keep this world offline and review its history.", []);
        }
        finally { gate.Release(); }
    }

    // Called only by the owner's loopback API. The destination is derived from LocalData,
    // never accepted from the signed offer or from a request.
    public async Task<SuccessorRestoreResult> RestoreSharedSuccessorAsync(Guid profileId,
        SuccessorRestoreRequest request, CancellationToken cancellationToken = default,
        Func<string, long>? freeBytes = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (profileId == Guid.Empty || request is null || request.Setup is null ||
                request.RecordHash.Length != 64 || request.RecordHash.Any(ch => !Uri.IsHexDigit(ch)))
                return new(false, "InvalidRestoreRequest", "Choose an exact staged handoff.");
            var pendingBytes = data.LoadProtected(SuccessorRestoreName(profileId));
            var previousRestore = pendingBytes is null ? null :
                JsonSerializer.Deserialize<SuccessorRestoreState>(pendingBytes);
            if (runs.Any(item => item.ProfileId == profileId) ||
                settings.Profiles.Any(item => item.Id == profileId) && previousRestore is null)
                return new(false, "WorldAlreadyManaged", "This world already has local Host setup. Keep the existing copy and review it.");
            var records = authority.Read(profileId);
            var heads = records.Where(item => !records.Any(child =>
                child.Proposal.ParentAuthorityHash == item.RecordHash)).ToArray();
            var record = heads.Length == 1 ? heads[0] : null;
            if (record is null || record.RecordHash != request.RecordHash ||
                record.Proposal.Kind != "Planned" || !WorldAuthorityTrust.Verify(record) ||
                !ValheimSetup.ValidWorldId(record.Version.WorldId))
                return new(false, "AuthorityReviewRequired", "The signed handoff is missing or competing histories need review.");
            var member = record.Roster.Members.SingleOrDefault(item =>
                item.PublicKey == record.Proposal.CandidatePublicKey);
            if (member is not { Revoked: false, Grants: { Receive: true, EligibleHost: true } } ||
                member.AccessExpiresUtc is { } expiry && expiry <= clock.GetUtcNow())
                return new(false, "SuccessorAccessChanged", "This PC no longer has current hosting permission.");
            authority.BindLocalSuccessor(profileId, record.RecordHash, member.DeviceId);
            var stage = Path.Combine(data.RootPath, "shared-world-staged", profileId.ToString("N"),
                request.RecordHash);
            PlannedHandoffReceiver.VerifyStage(data.RootPath, stage, record);
            var vault = Path.Combine(data.RootPath, "received-shared-worlds",
                member.DeviceId.ToString("N"), profileId.ToString("N"));
            var latest = FriendLink.ReadReceivedLatest(vault);
            if (latest?.VersionHash != record.Version.VersionHash ||
                latest.GroupId != record.Proposal.GroupId ||
                latest.SigningPublicKey != record.Roster.OwnerPublicKey)
                return new(false, "FinalCopyMissing", "Receive and verify the exact final save on this PC.");
            var setup = request.Setup;
            var worldRoot = SuccessorWorldRoot(data, record.Version);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, worldRoot);
            var minecraft = record.Version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock;
            var destination = SuccessorPayloadRoot(worldRoot, record.Version);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, destination);
            var retry = previousRestore is { Schema: 1 } &&
                previousRestore.GroupId == record.Proposal.GroupId &&
                previousRestore.RecordHash == record.RecordHash &&
                previousRestore.VersionHash == record.Version.VersionHash &&
                previousRestore.WorldDirectory == worldRoot;
            if (previousRestore is not null && !retry)
                return new(false, "RestoreReviewRequired", "An earlier restore attempt needs review. No files were replaced.");
            if (!retry && (Directory.Exists(destination) || File.Exists(destination) ||
                !minecraft && (Directory.Exists(worldRoot) || File.Exists(worldRoot))))
                return new(false, "DestinationExists", "A local managed world already occupies this location. No files were changed.");
            var existingProfile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (retry && existingProfile is not null)
                return existingProfile.WorldDirectory == worldRoot &&
                       existingProfile.Kind == record.Version.Game &&
                       existingProfile.WorldId == record.Version.WorldId &&
                       VerifiedSuccessorCopy(destination, record.Version) ?
                    new(true, "RestoredPendingChecks", "This restored Host profile remains fenced pending real Friend route and game tests.",
                        profileId, record.Version.VersionHash) :
                    new(false, "RestoreReviewRequired", "The restored files changed. Keep this world offline for review.");
            if (setup.ControlPort != settings.CompanionPort)
                return new(false, "ControlPortMismatch",
                    "Set this PC's Friend control port in Host settings, then check the same port here.");
            if (setup.GamePort is < 1024 or > 65535 ||
                record.Version.Game == GameKinds.Valheim && setup.GamePort == 65535)
                return new(false, "InvalidGamePort", "Choose a game port supported by this Host game.");
            var local = SharedWorldReadiness.Check(vault, worldRoot, setup,
                new TakeoverAuthority(true, true, true, true, true),
                record.Roster.OwnerPublicKey, record.Proposal.GroupId, freeBytes);
            if (local.Reasons.Count > 0)
                return new(false, "LocalSetupIncomplete", "Finish the local game, add-on, password, port, and space checks before restoring.",
                    PendingChecks: local.Reasons);
            if (record.Version.Game == GameKinds.Valheim &&
                (request.GamePassword is null || request.GamePassword.Length is < 5 or > 64 ||
                 request.GamePassword.Any(char.IsControl)))
                return new(false, "PasswordRequired", "Enter a new Valheim password of 5 to 64 characters.");
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 ||
                request.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(request.ServerName) ||
                request.ServerName.Length > 80 || request.ServerName.Any(char.IsControl))
                return new(false, "InvalidServerName", "Enter a short local server name without line breaks.");
            var executable = request.ExecutablePath ?? setup.ServerFile!;
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
                (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
                return new(false, "ServerFileMissing", "Choose an installed local game server executable.");
            if (minecraft && MinecraftPreparedRoot.Check(data.RootPath, worldRoot,
                    request.PreparedServerRoot, record.Version, setup, executable) is { } minecraftIssue)
                return new(false, "MinecraftSetupIncomplete", minecraftIssue);
            if (record.Version.Game == GameKinds.Factorio &&
                (request.FactorioRconPort is not >= 1024 or > 65535 ||
                 request.FactorioRconPort == setup.GamePort ||
                 request.FactorioRconPort == setup.ControlPort))
                return new(false, "FactorioRconPortInvalid",
                    "Choose a separate local Factorio RCON port from 1024 to 65535.");
            var profile = new ServerProfile
            {
                Id = profileId, Kind = record.Version.Game, WorldId = record.Version.WorldId,
                Name = request.Name, ServerName = request.ServerName,
                WorldSource = "Existing", WorldDirectory = worldRoot,
                GamePort = setup.GamePort, ExecutablePath = executable,
                Crossplay = record.Version.PortableSetup.Crossplay,
                PublicListing = false, SharedSavesEnabled = false,
                Backups = new BackupOptions { Enabled = true },
                Minecraft = record.Version.Game == GameKinds.MinecraftJava
                    ? new MinecraftOptions { ServerJarPath = setup.ServerFile! } : null,
                Factorio = record.Version.Game == GameKinds.Factorio
                    ? new FactorioOptions { RconPort = request.FactorioRconPort!.Value } : null
            };
            var next = CopySettings(settings);
            next.Profiles = [.. settings.Profiles, profile];
            if (Validate(next) is { } invalid)
                return new(false, "HostSetupRejected", invalid);
            if (!games.TryGet(profile.Kind, out var driver) ||
                !games.PortsAvailableForStart(driver.Ports(profile)))
                return new(false, "GamePortInUse", "A selected game or Factorio RCON port is already in use.");
            var parent = Path.GetDirectoryName(destination)!;
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, parent);
            Directory.CreateDirectory(parent);
            var temporary = Path.Combine(parent, ".successor-" + Guid.NewGuid().ToString("N"));
            var pending = new SuccessorRestoreState(1, record.Proposal.GroupId,
                record.RecordHash, record.Version.VersionHash, worldRoot);
            // The protected marker is durable before any Host profile can be made.
            if (!retry) data.SaveProtected(SuccessorRestoreName(profileId),
                JsonSerializer.SerializeToUtf8Bytes(pending));
            if (!Directory.Exists(destination))
            {
                Directory.CreateDirectory(temporary);
                foreach (var file in record.Version.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = SharedWorldService.SafeChild(Path.Combine(stage,
                        SharedWorldService.PayloadDirectory), file.Path);
                    SharedWorldService.VerifyFile(source, file);
                    var target = SharedWorldService.SafeChild(temporary, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                               FileShare.None, SharedWorldService.ChunkBytes, FileOptions.WriteThrough))
                        input.CopyTo(output);
                    SharedWorldService.VerifyFile(target, file);
                }
                // A failed partial copy stays in app-owned storage for manual review.
                // Never recursively delete a world or an unexpected link.
                PlannedHandoffReceiver.VerifyStage(data.RootPath, stage, record);
                if (Directory.Exists(destination) || File.Exists(destination))
                    return new(false, "DestinationExists", "The world destination changed during copying. No files were replaced.");
                Directory.Move(temporary, destination);
            }
            if (!VerifiedSuccessorCopy(destination, record.Version))
                return new(false, "RestoreReviewRequired", "The restored world differs from the signed final copy. No files were replaced.");
            if (record.Version.Game == GameKinds.Valheim)
                data.SaveValheimPassword(profileId, request.GamePassword!);
            var saved = UpdateSettingsLocked(next);
            if (!saved.Ok)
                return new(false, "HostSetupRejected", saved.Message);
            // This is a draft Host profile. No ordinary Start is possible until
            // an independently verified route and game rehearsal completes.
            return new(true, "RestoredPendingChecks",
                "The final save is in fresh managed storage. Check the installed server setup and test both direct routes with a Friend before hosting.",
                profileId, record.Version.VersionHash,
                ["Check the direct-IP Friend control route from another PC.",
                 "Load this copy in a disposable managed game rehearsal and test a real Friend join.",
                 "Confirm a recognizable change survives a graceful restart."]);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, "RestoreVerificationFailed",
                "The staged proof, local setup, or copied files did not pass verification: " + ex.Message);
        }
        finally { gate.Release(); }
    }

    private static string SuccessorWorldRoot(LocalData data, SharedWorldVersion version) =>
        version.Game switch
        {
            GameKinds.Valheim => Path.Combine(data.WorldImportsRoot, version.ProfileId.ToString("N")),
            GameKinds.Factorio => Path.Combine(data.FactorioServersRoot,
                version.ProfileId.ToString("N"), version.WorldId),
            GameKinds.MinecraftJava or GameKinds.MinecraftBedrock =>
                Path.Combine(data.MinecraftInstallRoot, version.ProfileId.ToString("N")),
            _ => data.NewWorldDirectory(version.ProfileId)
        };

    private static string SuccessorPayloadRoot(string root, SharedWorldVersion version) =>
        version.Game switch
        {
            GameKinds.MinecraftJava => Path.Combine(root, version.WorldId),
            GameKinds.MinecraftBedrock => Path.Combine(root, "worlds", version.WorldId),
            _ => root
        };

    private static bool VerifiedSuccessorCopy(string destination, SharedWorldVersion version)
    {
        if (!Directory.Exists(destination) ||
            (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) return false;
        var expected = version.Files.Select(item => item.Path.Replace('/', Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(destination, "*",
                     SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ||
                File.Exists(entry) && !expected.Contains(Path.GetRelativePath(destination, entry)))
                return false;
        foreach (var file in version.Files)
            SharedWorldService.VerifyFile(SharedWorldService.SafeChild(destination, file.Path), file);
        return true;
    }
}

internal static class MinecraftPreparedRoot
{
    internal static string? Check(string dataRoot, string expectedRoot, string? selectedRoot,
        SharedWorldVersion version, TakeoverLocalSetup setup, string executable)
    {
        if (version.Game is not (GameKinds.MinecraftJava or GameKinds.MinecraftBedrock))
            return "This is not a Minecraft shared world.";
        if (string.IsNullOrWhiteSpace(selectedRoot) || !Path.IsPathFullyQualified(selectedRoot) ||
            !Path.GetFullPath(selectedRoot).Equals(Path.GetFullPath(expectedRoot),
                StringComparison.OrdinalIgnoreCase) || !Directory.Exists(expectedRoot))
            return "Prepare this world's separate Minecraft server folder in TogetherServer managed storage first.";
        SharedWorldService.EnsureUnlinkedRoot(dataRoot, expectedRoot);
        var jar = setup.ServerFile;
        if (version.Game == GameKinds.MinecraftJava)
        {
            if (!Path.GetFileName(executable).Equals("java.exe", StringComparison.OrdinalIgnoreCase) ||
                jar is null || !Path.GetFullPath(jar).Equals(Path.Combine(expectedRoot, "server.jar"),
                    StringComparison.OrdinalIgnoreCase) || !PlainFile(jar) ||
                !MinecraftSetup.IsSupportedVanillaServerJar(jar, expectedRoot))
                return "Select local java.exe and the reviewed vanilla server.jar inside this prepared folder.";
            if (!string.Equals(Property(expectedRoot, "eula.txt", "eula"), "true",
                    StringComparison.OrdinalIgnoreCase))
                return "Review and accept Minecraft's EULA yourself in this prepared server folder.";
        }
        else if (!Path.GetFullPath(executable).Equals(Path.Combine(expectedRoot,
                     "bedrock_server.exe"), StringComparison.OrdinalIgnoreCase) ||
                 !PlainFile(executable))
            return "Install and select bedrock_server.exe inside this prepared folder.";
        if (Property(expectedRoot, "server.properties", "level-name") != version.WorldId ||
            !int.TryParse(Property(expectedRoot, "server.properties", "server-port"), out var port) ||
            port != setup.GamePort)
            return "Set this world's name and selected game port in the prepared server.properties file.";
        return null;
    }

    private static bool PlainFile(string path) => File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static string? Property(string root, string filename, string key)
    {
        var path = Path.Combine(root, filename);
        if (!PlainFile(path) || new FileInfo(path).Length > 64 * 1024) return null;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#')) continue;
            var separator = trimmed.IndexOf('=');
            if (separator > 0 && trimmed[..separator].Trim().Equals(key,
                    StringComparison.OrdinalIgnoreCase))
                return trimmed[(separator + 1)..].Trim();
        }
        return null;
    }
}
