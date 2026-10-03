using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record SuccessorRestoreRequest(string RecordHash, TakeoverLocalSetup Setup,
    string Name, string ServerName, string? GamePassword = null,
    string? ExecutablePath = null);
public sealed record SuccessorRestoreResult(bool Ok, string Code, string Message,
    Guid? ProfileId = null, string? VersionHash = null,
    IReadOnlyList<string>? PendingChecks = null);
public sealed record SuccessorRestoreStatus(bool Staged, bool Restored,
    string? RecordHash, string Message, IReadOnlyList<string> PendingChecks);

internal sealed record SuccessorRestoreState(int Schema, Guid GroupId, string RecordHash,
    string VersionHash, string WorldDirectory);

public sealed partial class HostManager
{
    private static string SuccessorRestoreName(Guid profileId) =>
        $"successor-restore-{profileId:N}.protected";

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
                 "Confirm a recognizable change survives a graceful restart."]);
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
            if (settings.Profiles.Any(item => item.Id == profileId) ||
                runs.Any(item => item.ProfileId == profileId) ||
                data.HasProtected(SuccessorRestoreName(profileId)))
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
            if (Directory.Exists(worldRoot) || File.Exists(worldRoot))
                return new(false, "DestinationExists", "A local managed world already occupies this location. No files were changed.");
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
            var parent = Path.GetDirectoryName(worldRoot)!;
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, parent);
            Directory.CreateDirectory(parent);
            var temporary = Path.Combine(parent, ".successor-" + Guid.NewGuid().ToString("N"));
            var pending = new SuccessorRestoreState(1, record.Proposal.GroupId,
                record.RecordHash, record.Version.VersionHash, worldRoot);
            // The protected marker is durable before any Host profile can be made.
            data.SaveProtected(SuccessorRestoreName(profileId),
                JsonSerializer.SerializeToUtf8Bytes(pending));
            Directory.CreateDirectory(temporary);
            var temporaryPayload = SuccessorPayloadRoot(temporary, record.Version);
            foreach (var file in record.Version.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = SharedWorldService.SafeChild(Path.Combine(stage,
                    SharedWorldService.PayloadDirectory), file.Path);
                SharedWorldService.VerifyFile(source, file);
                var target = SharedWorldService.SafeChild(temporaryPayload, file.Path);
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
            Directory.Move(temporary, worldRoot);
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
                    ? new FactorioOptions() : null
            };
            if (record.Version.Game == GameKinds.Valheim)
                data.SaveValheimPassword(profileId, request.GamePassword!);
            var next = CopySettings(settings);
            next.Profiles = [.. settings.Profiles, profile];
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
}
