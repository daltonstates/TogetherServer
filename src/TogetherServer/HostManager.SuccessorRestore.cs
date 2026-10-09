using System.Security.Cryptography;
using System.Text;
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
    string? PreparedServerRoot = null, bool ReadyForManualStart = false,
    IReadOnlyList<SharedWorldPortableAddOn>? RequiredAddOns = null,
    string? ControlRouteFingerprint = null, string? ControlRouteAddress = null);
public sealed record SuccessorFinishRequest(string RecordHash, TakeoverLocalSetup Setup,
    string? ExecutablePath = null, string? PreparedServerRoot = null);

internal sealed record SuccessorRestoreState(int Schema, Guid GroupId, string RecordHash,
    string VersionHash, string WorldDirectory, bool Ready = false,
    string? SetupHash = null);
internal sealed record SuccessorRouteObservation(int Schema, string RecordHash,
    SharedWorldRouteChallenge Challenge, string TlsFingerprint, DateTimeOffset ObservedUtc,
    bool Confirmed = false);

public sealed partial class HostManager
{
    // Deterministic storage readings for the isolated core checks.
    internal Func<string, long>? SuccessorFreeBytesForChecks { get; set; }

    private static string SuccessorRestoreName(Guid profileId) =>
        $"successor-restore-{profileId:N}.protected";
    private static string SuccessorRouteName(Guid profileId) =>
        $"successor-route-{profileId:N}.protected";

    internal bool HasSuccessorRouteCandidate(HostSettings proposed)
    {
        if (!SharedWorldRouteTrust.DirectIpAddress(proposed.CompanionEndpoint)) return false;
        try
        {
            return proposed.Profiles.Any(profile =>
            {
                var bytes = data.LoadProtected(SuccessorRestoreName(profile.Id));
                var state = bytes is null ? null :
                    JsonSerializer.Deserialize<SuccessorRestoreState>(bytes);
                var head = authority.LocalAuthorizedHead(profile.Id);
                if (state is not { Schema: 1 } || head is null ||
                    authority.GovernanceUnresolved(profile.Id) ||
                    head.RecordHash != state.RecordHash ||
                    head.Proposal.CandidateAddress != proposed.CompanionEndpoint ||
                    head.Version.VersionHash != state.VersionHash ||
                    profile.WorldDirectory != state.WorldDirectory) return false;
                var member = head.Roster.Members.SingleOrDefault(item => item.PublicKey ==
                    WorldAuthorityTrust.CandidateDevicePublicKey(head.Proposal));
                return member is { Revoked: false, Grants.EligibleHost: true } &&
                    (member.AccessExpiresUtc is not { } end || end > clock.GetUtcNow());
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

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
                authority.LocalAuthorizedHead(profileId)?.RecordHash != recordHash ||
                record.Proposal.CandidateAddress != settings.CompanionEndpoint ||
                settings.Profiles.SingleOrDefault(item => item.Id == profileId) is not { } profile ||
                profile.WorldDirectory != restored.WorldDirectory ||
                !SharedWorldRouteTrust.VerifyChallenge(challenge, record, clock.GetUtcNow()))
                return null;
            var member = record.Roster.Members.SingleOrDefault(item =>
                item.PublicKey == WorldAuthorityTrust.CandidateDevicePublicKey(record.Proposal));
            if (member is not { Revoked: false, Grants.EligibleHost: true } ||
                member.AccessExpiresUtc is { } expiry && expiry <= clock.GetUtcNow()) return null;
            var bytes = data.LoadProtected(record.Proposal.Schema == 2 ?
                WorldAuthorityStore.HostingKeyName(profileId) :
                $"shared-world-pc-signing-{member.DeviceId:N}.protected");
            if (bytes is null) return null;
            using var certificate = new HostIdentity(data).Ensure(settings.CompanionEndpoint);
            if (HostIdentity.Fingerprint(certificate) != tlsFingerprint) return null;
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(bytes, out _);
            if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) !=
                record.Proposal.CandidatePublicKey) return null;
            var proof = SharedWorldRouteTrust.Sign(profileId, recordHash, challenge.Nonce,
                settings.CompanionEndpoint, tlsFingerprint, key);
            data.SaveProtected(SuccessorRouteName(profileId), JsonSerializer.SerializeToUtf8Bytes(
                new SuccessorRouteObservation(1, recordHash, challenge, tlsFingerprint,
                    clock.GetUtcNow())));
            return proof;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return null; }
        finally { gate.Release(); }
    }

    internal async Task<bool> ConfirmSuccessorRouteAsync(Guid profileId, string recordHash,
        SharedWorldRouteConfirmation confirmation, string tlsFingerprint)
    {
        await gate.WaitAsync();
        try
        {
            var record = authority.ReadUniqueHead(profileId);
            var bytes = data.LoadProtected(SuccessorRouteName(profileId));
            var observed = bytes is null ? null :
                JsonSerializer.Deserialize<SuccessorRouteObservation>(bytes);
            if (record is null || confirmation?.Challenge is null || confirmation.Proof is null ||
                observed is not { Schema: 1 } || observed.RecordHash != recordHash ||
                record.RecordHash != recordHash ||
                authority.LocalAuthorizedHead(profileId)?.RecordHash != recordHash ||
                observed.TlsFingerprint != tlsFingerprint ||
                observed.Challenge != confirmation.Challenge ||
                clock.GetUtcNow() - observed.ObservedUtc > TimeSpan.FromMinutes(5) ||
                !SharedWorldRouteTrust.VerifyChallenge(confirmation.Challenge, record,
                    clock.GetUtcNow()) ||
                !SharedWorldRouteTrust.Verify(confirmation.Proof, record,
                    confirmation.Challenge.Nonce, tlsFingerprint)) return false;
            data.SaveProtected(SuccessorRouteName(profileId),
                JsonSerializer.SerializeToUtf8Bytes(observed with { Confirmed = true }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return false; }
        finally { gate.Release(); }
    }

    public async Task<SuccessorRestoreStatus> SuccessorRestoreStatusAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            var record = authority.ReadUniqueHead(profileId);
            if (record is null || record.Proposal.Kind is not ("Planned" or "Quorum") ||
                !WorldAuthorityTrust.Verify(record))
                return new(false, false, null,
                    "No single locally authorized signed handoff or majority is ready for this world.", []);
            var stateBytes = data.LoadProtected(SuccessorRestoreName(profileId));
            var state = stateBytes is null ? null :
                JsonSerializer.Deserialize<SuccessorRestoreState>(stateBytes);
            if (state?.Ready != true)
            {
                if (record.Proposal.Kind == "Planned")
                    PlannedHandoffReceiver.VerifyStage(data.RootPath,
                        StageRoot(profileId, record), record);
                else
                    VerifyVaultCopy(record);
            }
            var restored = settings.Profiles.Any(item => item.Id == profileId) &&
                state is not null;
            var issue = state?.Ready == true && state.RecordHash == record.RecordHash &&
                settings.Profiles.SingleOrDefault(item => item.Id == profileId) is { } profile
                ? SuccessorStartIssue(profileId, profile) : null;
            var ready = state?.Ready == true && state.RecordHash == record.RecordHash &&
                settings.Profiles.Any(item => item.Id == profileId) && issue is null;
            string? routeFingerprint = null;
            if (settings.CompanionEndpoint == record.Proposal.CandidateAddress &&
                SharedWorldRouteTrust.DirectIpAddress(settings.CompanionEndpoint))
            {
                using var certificate = new HostIdentity(data).Ensure(settings.CompanionEndpoint);
                routeFingerprint = HostIdentity.Fingerprint(certificate);
            }
            return new(true, restored, record.RecordHash,
                ready ? "Pre-Start checks passed. Switch to Host and start manually." :
                restored ? "The verified copy is in local managed storage. Finish setup and checks." :
                    "The signed save is verified. Review local setup before restoring it.",
                issue is not null ? [issue] : ready ? [] :
                ["Check the direct-IP Friend control route from another PC.",
                 "Check local game ports before Start; test a real game join after Start."],
                record.Version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock
                    ? SuccessorWorldRoot(data, record.Version) : null, ready,
                record.Version.PortableSetup.AddOns, routeFingerprint,
                routeFingerprint is null ? null : record.Proposal.CandidateAddress);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException or ArgumentException or OverflowException)
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
            var record = authority.ReadUniqueHead(profileId);
            if (record is null || record.RecordHash != request.RecordHash ||
                record.Proposal.Kind is not ("Planned" or "Quorum") ||
                !WorldAuthorityTrust.Verify(record) ||
                !ValheimSetup.ValidWorldId(record.Version.WorldId))
                return new(false, "AuthorityReviewRequired", "The signed authority is missing or competing histories need review.");
            var member = record.Roster.Members.SingleOrDefault(item =>
                item.PublicKey == WorldAuthorityTrust.CandidateDevicePublicKey(record.Proposal));
            if (member is not { Revoked: false, Grants: { Receive: true, EligibleHost: true } } ||
                member.AccessExpiresUtc is { } expiry && expiry <= clock.GetUtcNow())
                return new(false, "SuccessorAccessChanged", "This PC no longer has current hosting permission.");
            authority.BindLocalSuccessor(profileId, record.RecordHash, member.DeviceId);
            if (authority.LocalAuthorizedHead(profileId)?.RecordHash != record.RecordHash)
                return new(false, "LocalBindingInvalid", "This PC does not hold the signed hosting key.");
            var vault = VerifyVaultCopy(record);
            var stage = record.Proposal.Kind == "Planned" ? StageRoot(profileId, record) :
                Path.Combine(vault, record.Version.VersionHash);
            if (record.Proposal.Kind == "Planned")
                PlannedHandoffReceiver.VerifyStage(data.RootPath, stage, record);
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
                new TakeoverAuthority(true, true, true, true, true, true),
                record.Roster.OwnerPublicKey, record.Proposal.GroupId,
                freeBytes ?? SuccessorFreeBytesForChecks,
                settings.CompanionListeningEnabled &&
                settings.CompanionEndpoint == record.Proposal.CandidateAddress &&
                authority.LocalAuthorizedHead(profileId)?.RecordHash == record.RecordHash,
                requiredCopies: 3, authorityRecords: authority.Read(profileId));
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
                Id = profileId,
                Kind = record.Version.Game,
                WorldId = record.Version.WorldId,
                Name = request.Name,
                ServerName = request.ServerName,
                WorldSource = "Existing",
                WorldDirectory = worldRoot,
                GamePort = setup.GamePort,
                ExecutablePath = executable,
                Crossplay = record.Version.PortableSetup.Crossplay,
                PublicListing = false,
                SharedSavesEnabled = false,
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
            var temporary = Path.Combine(parent, ".successor-" + record.RecordHash);
            var pending = new SuccessorRestoreState(1, record.Proposal.GroupId,
                record.RecordHash, record.Version.VersionHash, worldRoot);
            // The protected marker is durable before any Host profile can be made.
            if (!retry) data.SaveProtected(SuccessorRestoreName(profileId),
                JsonSerializer.SerializeToUtf8Bytes(pending));
            if (!Directory.Exists(destination))
            {
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, temporary);
                if (Directory.Exists(temporary))
                {
                    if (!retry)
                        return new(false, "RestoreReviewRequired",
                            "An unexpected restore copy remains in managed storage. Keep it for review; no world was replaced.");
                }
                else
                    Directory.CreateDirectory(temporary);
                foreach (var file in record.Version.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = SharedWorldService.SafeChild(Path.Combine(stage,
                        SharedWorldService.PayloadDirectory), file.Path);
                    SharedWorldService.VerifyFile(source, file);
                    var target = SharedWorldService.SafeChild(temporary, file.Path);
                    if (!File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                                   FileShare.None, SharedWorldService.ChunkBytes, FileOptions.WriteThrough))
                            input.CopyTo(output);
                    }
                    SharedWorldService.VerifyFile(target, file);
                }
                // A failed partial copy stays in app-owned storage for manual review.
                // Never recursively delete a world or an unexpected link.
                if (!VerifiedSuccessorCopy(temporary, record.Version))
                    return new(false, "RestoreReviewRequired", "The copied files failed verification.");
                if (record.Proposal.Kind == "Planned")
                    PlannedHandoffReceiver.VerifyStage(data.RootPath, stage, record);
                VerifyVaultCopy(record);
                if (authority.ReadUniqueHead(profileId)?.RecordHash != record.RecordHash ||
                    authority.LocalAuthorizedHead(profileId)?.RecordHash != record.RecordHash)
                    return new(false, "AuthorityChanged", "Signed authority changed during copying.");
                if (Directory.Exists(destination) || File.Exists(destination))
                    return new(false, "DestinationExists", "The world destination changed during copying. No files were replaced.");
                Directory.Move(temporary, destination);
            }
            if (!VerifiedSuccessorCopy(destination, record.Version))
                return new(false, "RestoreReviewRequired", "The restored world differs from the signed final copy. No files were replaced.");
            if (SuccessorStorageIssue(pending, record,
                    freeBytes ?? SuccessorFreeBytesForChecks) is { } storageIssue)
                return new(false, "LocalSetupIncomplete", storageIssue, PendingChecks: [storageIssue]);
            var installedAddOns = ServerAddOns.List(data, profile);
            if (record.Version.PortableSetup.AddOns?.Count > 0 &&
                (!installedAddOns.Ok || record.Version.PortableSetup.AddOns.Any(required =>
                    !installedAddOns.Items.Any(item => item.Enabled &&
                        item.Name == required.Name && item.Version == required.Version &&
                        item.RequiredGameVersion == required.RequiredGameVersion &&
                        item.Type == required.Type && (required.Id is null ||
                            item.Key.EndsWith(required.Id, StringComparison.OrdinalIgnoreCase))))))
                return new(false, "AddOnsMismatch", "Install the exact reviewed add-ons in this managed server folder.");
            if (authority.ReadUniqueHead(profileId)?.RecordHash != record.RecordHash ||
                authority.LocalAuthorizedHead(profileId)?.RecordHash != record.RecordHash)
                return new(false, "AuthorityChanged", "Signed authority changed before local setup was saved.");
            if (record.Version.Game == GameKinds.Valheim)
                data.SaveValheimPassword(profileId, request.GamePassword!);
            var saved = UpdateSettingsLocked(next);
            if (!saved.Ok)
                return new(false, "HostSetupRejected", saved.Message);
            // This is a draft Host profile. No ordinary Start is possible until
            // an independently verified route and game rehearsal completes.
            return new(true, "RestoredPendingChecks",
                "The post-Stop file copy is in fresh managed storage. Game load has not been checked. Check server setup and both direct routes with a Friend before hosting.",
                profileId, record.Version.VersionHash,
                ["Check the direct-IP Friend control route from another PC.",
                 "Load this copy in a disposable managed game rehearsal and test a real Friend join.",
                 "Confirm a recognizable change survives a graceful restart."]);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                                   CryptographicException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return new(false, "RestoreVerificationFailed",
                "The staged proof, local setup, or copied files did not pass verification: " + ex.Message);
        }
        finally { gate.Release(); }
    }

    private string StageRoot(Guid profileId, WorldAuthorityRecord record) =>
        Path.Combine(data.RootPath, "shared-world-staged", profileId.ToString("N"),
            record.RecordHash);

    private string VerifyVaultCopy(WorldAuthorityRecord record)
    {
        var member = record.Roster.Members.Single(item => item.PublicKey ==
            WorldAuthorityTrust.CandidateDevicePublicKey(record.Proposal));
        var vault = Path.Combine(data.RootPath, "received-shared-worlds",
            member.DeviceId.ToString("N"), record.Proposal.ProfileId.ToString("N"));
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
        var latest = FriendLink.ReadReceivedLatest(vault);
        if (latest?.VersionHash != record.Version.VersionHash ||
            latest.GroupId != record.Proposal.GroupId ||
            !FriendLink.AuthorizedVersionSignerForRecords(record.Roster.OwnerPublicKey,
                latest, authority.Read(record.Proposal.ProfileId)))
            throw new InvalidDataException("The exact signed save is missing from this PC's vault.");
        foreach (var file in record.Version.Files)
            SharedWorldService.VerifyFile(SharedWorldService.SafeChild(Path.Combine(vault,
                record.Version.VersionHash, SharedWorldService.PayloadDirectory), file.Path), file);
        return vault;
    }

    private string SetupHash(ServerProfile profile, TakeoverLocalSetup setup,
        WorldAuthorityRecord record) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            record.RecordHash,
            record.Version.VersionHash,
            record.Version.PortableSetup,
            profile,
            setup,
            settings.CompanionEndpoint,
            settings.CompanionPort,
            settings.CompanionListeningEnabled
        })));

    private string? SuccessorStorageIssue(SuccessorRestoreState state,
        WorldAuthorityRecord record, Func<string, long>? freeBytes = null,
        SharedWorldVersion? currentVersion = null)
    {
        // The restored payload is already present. Its first graceful Stop needs
        // one rolling backup and one published copy on the managed volume.
        var size = SharedWorldService.BoundedTotalBytes((currentVersion ?? record.Version).Files);
        return SharedWorldReadiness.HasSpaceForCopies(state.WorldDirectory, size, 2,
            freeBytes ?? SuccessorFreeBytesForChecks) ? null :
            "Free space for a rolling backup and shared publication of this save, plus 1 GiB, before Start.";
    }

    private string? SuccessorPreStartIssue(Guid profileId, SuccessorRestoreState state,
        WorldAuthorityRecord record, ServerProfile profile, TakeoverLocalSetup setup,
        bool requireRoute, bool requireVault)
    {
        if (state.Schema != 1 || state.RecordHash != record.RecordHash ||
            state.GroupId != record.Proposal.GroupId ||
            state.VersionHash != record.Version.VersionHash ||
            authority.GovernanceUnresolved(profileId) ||
            authority.LocalAuthorizedHead(profileId)?.RecordHash != record.RecordHash ||
            !WorldAuthorityTrust.Verify(record)) return "Signed authority or local key binding changed.";
        var member = record.Roster.Members.SingleOrDefault(item => item.PublicKey ==
            WorldAuthorityTrust.CandidateDevicePublicKey(record.Proposal));
        if (member is not { Revoked: false, Grants.EligibleHost: true } ||
            member.AccessExpiresUtc is { } end && end <= clock.GetUtcNow())
            return "This PC's hosting access ended.";
        if (profile.WorldDirectory != state.WorldDirectory || profile.Kind != record.Version.Game ||
            profile.WorldId != record.Version.WorldId || profile.GamePort != setup.GamePort ||
            profile.Crossplay != record.Version.PortableSetup.Crossplay ||
            profile.PublicListing && !record.Version.PortableSetup.PublicListing ||
            !profile.Backups.Enabled ||
            setup.ControlPort != settings.CompanionPort ||
            setup.ControlPort == setup.GamePort || !setup.NewPasswordConfigured ||
            profile.ExecutablePath != (record.Version.Game == GameKinds.MinecraftJava ?
                profile.ExecutablePath : setup.ServerFile) ||
            !SharedWorldRouteTrust.DirectIpAddress(settings.CompanionEndpoint) ||
            settings.CompanionEndpoint != record.Proposal.CandidateAddress ||
            !settings.CompanionListeningEnabled)
            return "Local Host setup or direct-IP control address changed.";
        if (requireVault) VerifyVaultCopy(record);
        var currentVersion = record.Version;
        if (sharedWorlds.Status(profile).Latest is { } latest && latest.Number > record.Version.Number)
        {
            if (!sharedWorlds.AuthorizedPublishedLineage(profile))
                return "The newer signed save does not continue the hosting decision.";
            currentVersion = latest;
        }
        bool verifiedCopy;
        try
        {
            verifiedCopy = VerifiedSuccessorCopy(
                SuccessorPayloadRoot(state.WorldDirectory, currentVersion), currentVersion);
        }
        catch (InvalidDataException) { verifiedCopy = false; }
        if (!verifiedCopy)
            return "The local world differs from the latest signed save. Keep it offline for review.";
        if (SuccessorStorageIssue(state, record, currentVersion: currentVersion) is { } storageIssue)
            return storageIssue;
        if (record.Version.Game == GameKinds.Valheim &&
            data.LoadValheimPassword(profileId) is not { Length: >= 5 and <= 64 })
            return "A new game password is missing.";
        var installed = ServerAddOns.List(data, profile);
        if (record.Version.Game is not (GameKinds.Fixture or GameKinds.MinecraftJava) &&
            (!installed.Ok || installed.GameVersion != record.Version.PortableSetup.GameVersion) ||
            setup.GameVersion != record.Version.PortableSetup.GameVersion ||
            setup.EnabledAddOns is null ||
            (record.Version.PortableSetup.AddOns?.Count ?? 0) != setup.EnabledAddOns.Count ||
            (record.Version.PortableSetup.AddOns ?? []).Any(required =>
                !setup.EnabledAddOns.Contains(required) || !installed.Items.Any(item =>
                    item.Enabled && item.Name == required.Name && item.Version == required.Version &&
                    item.RequiredGameVersion == required.RequiredGameVersion &&
                    item.Type == required.Type && (required.Id is null ||
                        item.Key.EndsWith(required.Id, StringComparison.OrdinalIgnoreCase)))))
            return "Installed game files or enabled add-ons differ from the signed setup.";
        if (record.Version.Game == GameKinds.MinecraftJava)
        {
            var jar = profile.Minecraft?.ServerJarPath;
            if (jar is null || !File.Exists(jar))
                return "The installed Java server JAR differs from the signed setup.";
            using var jarStream = File.OpenRead(jar);
            if (Convert.ToHexString(SHA256.HashData(jarStream)) !=
                record.Version.PortableSetup.JavaServerJarSha256)
                return "The installed Java server JAR differs from the signed setup.";
        }
        if (!games.TryGet(profile.Kind, out var driver) ||
            driver.ValidateForStart(profile) is not null ||
            !games.PortsAvailableForStart(driver.Ports(profile)))
            return "The game files or local game ports are not ready for Start.";
        if (record.Version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock &&
            MinecraftPreparedRoot.Check(data.RootPath, state.WorldDirectory,
                state.WorldDirectory, record.Version, setup, profile.ExecutablePath) is { } minecraftIssue)
            return minecraftIssue;
        if (requireRoute)
        {
            var bytes = data.LoadProtected(SuccessorRouteName(profileId));
            var observed = bytes is null ? null :
                JsonSerializer.Deserialize<SuccessorRouteObservation>(bytes);
            using var certificate = new HostIdentity(data).Ensure(settings.CompanionEndpoint);
            if (observed is not { Schema: 1, Confirmed: true } ||
                observed.RecordHash != record.RecordHash ||
                observed.TlsFingerprint != HostIdentity.Fingerprint(certificate) ||
                clock.GetUtcNow() - observed.ObservedUtc > TimeSpan.FromHours(1) ||
                observed.ObservedUtc > clock.GetUtcNow() ||
                !SharedWorldRouteTrust.VerifyChallenge(observed.Challenge, record,
                    observed.ObservedUtc))
                return "A recent signed direct-IP control route check from another Friend PC is required.";
        }
        return null;
    }

    public async Task<SuccessorRestoreResult> FinishSharedSuccessorAsync(Guid profileId,
        SuccessorFinishRequest request)
    {
        await gate.WaitAsync();
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var bytes = data.LoadProtected(SuccessorRestoreName(profileId));
                var state = bytes is null ? null : JsonSerializer.Deserialize<SuccessorRestoreState>(bytes);
                var record = authority.ReadUniqueHead(profileId);
                var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
                if (request?.Setup is null || state is null || record is null || profile is null ||
                    request.RecordHash != record.RecordHash)
                    return new(false, "RestoreNotCurrent", "Restore the exact current signed save first.");
                var issue = SuccessorPreStartIssue(profileId, state, record, profile,
                    request.Setup, true, true);
                if (issue is not null) return new(false, "SuccessorChecksPending", issue);
                if (request.ExecutablePath is not null && request.ExecutablePath != profile.ExecutablePath ||
                    request.PreparedServerRoot is not null &&
                    request.PreparedServerRoot != state.WorldDirectory)
                    return new(false, "SetupChanged", "The reviewed local setup changed.");
                sharedWorlds.AdoptSuccessor(profile, record);
                var previousSharing = profile.SharedSavesEnabled;
                profile.SharedSavesEnabled = true;
                try { data.SaveSettings(settings); }
                catch { profile.SharedSavesEnabled = previousSharing; throw; }
                var ready = state with
                {
                    Ready = true,
                    SetupHash = SetupHash(profile, request.Setup, record)
                };
                data.SaveProtected(SuccessorRestoreName(profileId),
                    JsonSerializer.SerializeToUtf8Bytes(ready));
                return new(true, "ReadyForManualStart",
                    "Pre-Start checks passed. Switch to Host and start manually. Test the game route and a real join after Start.",
                    profileId, record.Version.VersionHash);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return new(false, "SuccessorChecksPending", "The signed copy or local checks could not be verified."); }
        finally { gate.Release(); }
    }

    private string? SuccessorStartIssue(Guid profileId, ServerProfile profile)
    {
        var bytes = data.LoadProtected(SuccessorRestoreName(profileId));
        var state = bytes is null ? null : JsonSerializer.Deserialize<SuccessorRestoreState>(bytes);
        var record = authority.ReadUniqueHead(profileId);
        if (state is not { Ready: true } || record is null)
            return "Finish the signed restore and local checks before Start.";
        var setup = new TakeoverLocalSetup(profile.Kind == GameKinds.MinecraftJava ?
            profile.Minecraft?.ServerJarPath : profile.ExecutablePath,
            record.Version.PortableSetup.GameVersion, record.Version.PortableSetup.AddOns,
            true, settings.CompanionPort, profile.GamePort);
        if (state.SetupHash != SetupHash(profile, setup, record))
            return "Local setup changed after approval. Finish the checks again.";
        if (!profile.SharedSavesEnabled)
            return "Signed save continuity is disabled on this Host.";
        return SuccessorPreStartIssue(profileId, state, record, profile, setup, true, false);
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
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
        Dictionary<string, string> properties;
        SharedWorldPortableSetupReader.PortableSettings prepared;
        try
        {
            properties = SharedWorldPortableSetupReader.ParseProperties(
                ReadPlainText(expectedRoot, "server.properties"));
            prepared = SharedWorldPortableSetupReader.ReadMinecraftSettings(version.Game, properties);
        }
        catch (InvalidDataException ex)
        {
            return $"Review the prepared server.properties file: {ex.Message}";
        }
        catch (IOException)
        {
            return "The prepared server.properties file could not be read. Check this local server folder.";
        }
        catch (UnauthorizedAccessException)
        {
            return "The prepared server.properties file could not be read. Check this local server folder.";
        }
        if (!properties.TryGetValue("level-name", out var world) || world != version.WorldId ||
            !properties.TryGetValue("server-port", out var portText) ||
            !int.TryParse(portText, out var port) || port != setup.GamePort)
            return "Set this world's name and selected game port in the prepared server.properties file.";
        var signed = version.PortableSetup;
        if (signed.AllowlistEnabled is null)
            return "This signed save has no reviewed player allowlist setting. Receive a newer save before restoring this Minecraft world.";
        if (signed.MaxPlayers is { } maxPlayers && prepared.MaxPlayers != maxPlayers)
            return $"Set max-players={maxPlayers} in the prepared server.properties file to match the signed save.";
        if (signed.GameMode is { } gameMode && prepared.GameMode != gameMode)
            return $"Set gamemode={gameMode} in the prepared server.properties file to match the signed save.";
        if (signed.Difficulty is { } difficulty && prepared.Difficulty != difficulty)
            return $"Set difficulty={difficulty} in the prepared server.properties file to match the signed save.";
        var allowlistSetting = version.Game == GameKinds.MinecraftJava ? "white-list" : "allow-list";
        if (signed.AllowlistEnabled is { } allowlistEnabled &&
            prepared.AllowlistEnabled != allowlistEnabled)
            return $"Set {allowlistSetting}={allowlistEnabled.ToString().ToLowerInvariant()} in the prepared server.properties file to match the signed save.";
        if (signed.Allowlist is not null)
        {
            var allowlistFile = version.Game == GameKinds.MinecraftJava ? "whitelist.json" : "allowlist.json";
            IReadOnlyList<SharedWorldPortableAllowEntry> actual;
            try
            {
                var path = Path.Combine(expectedRoot, allowlistFile);
                actual = File.Exists(path) ?
                    SharedWorldPortableSetupReader.ParseMinecraftAllowlist(version.Game,
                        ReadPlainText(expectedRoot, allowlistFile)) : [];
            }
            catch (InvalidDataException ex)
            {
                return $"Review the prepared {allowlistFile} file: {ex.Message}";
            }
            catch (IOException)
            {
                return $"The prepared {allowlistFile} file could not be read. Check this local server folder.";
            }
            catch (UnauthorizedAccessException)
            {
                return $"The prepared {allowlistFile} file could not be read. Check this local server folder.";
            }
            if (signed.Allowlist.Count != actual.Count || signed.Allowlist.Any(required =>
                    !actual.Any(entry =>
                        string.Equals(entry.Name, required.Name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entry.Id, required.Id, StringComparison.OrdinalIgnoreCase))))
                return $"Match {allowlistFile} to the signed player allowlist before restoring or starting this world.";
        }
        return null;
    }

    private static bool PlainFile(string path) => File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static string ReadPlainText(string root, string filename)
    {
        var path = Path.Combine(root, filename);
        if (!PlainFile(path) || new FileInfo(path).Length > 32 * 1024)
            throw new InvalidDataException($"{filename} is missing, linked, or too large.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 32 * 1024 || !PlainFile(path))
            throw new InvalidDataException($"{filename} changed while being checked.");
        try { return StrictUtf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException ex)
        { throw new InvalidDataException($"{filename} is not plain text.", ex); }
    }

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
