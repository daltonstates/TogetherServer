using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record SeparateCopyHostStatus(bool Recorded, bool Restored,
    bool ReadyForManualStart, bool ReviewRequired, string Message,
    Guid? LocalProfileId = null, string? BranchHash = null,
    string? PreparedServerRoot = null,
    IReadOnlyList<SharedWorldPortableAddOn>? RequiredAddOns = null,
    bool Running = false);
public sealed record SeparateCopyHostRestoreRequest(string BranchHash,
    TakeoverLocalSetup Setup, string Name, string ServerName,
    string? GamePassword = null, string? ExecutablePath = null,
    string? PreparedServerRoot = null, int? FactorioRconPort = null);
public sealed record SeparateCopyHostFinishRequest(string BranchHash,
    TakeoverLocalSetup Setup, string? ExecutablePath = null,
    string? PreparedServerRoot = null);
public sealed record SeparateCopyHostStartRequest(string BranchHash,
    Guid LocalProfileId, bool AcceptSplitWarning);

internal sealed record SeparateCopyHostState(int Schema, Guid SourceProfileId,
    Guid LocalProfileId, Guid GroupId, string BranchHash, string VersionHash,
    string WorldDirectory, bool Ready = false, bool Started = false,
    string? SetupHash = null);

public sealed partial class HostManager
{
    private static string SeparateHostName(Guid localProfileId) =>
        $"separate-host-{localProfileId:N}.protected";
    private static string SeparateRouteName(Guid sourceProfileId, string branchHash) =>
        $"separate-route-{sourceProfileId:N}-{branchHash}.protected";

    private SeparateCopyHostState? ReadSeparateHost(Guid localProfileId)
    {
        var name = SeparateHostName(localProfileId);
        var bytes = data.LoadProtected(name);
        if (bytes is null)
        {
            if (data.HasProtected(name))
                throw new InvalidDataException("Separate-copy hosting state could not be read.");
            return null;
        }
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("Separate-copy hosting state is oversized.");
        var state = JsonSerializer.Deserialize<SeparateCopyHostState>(bytes);
        if (state is not { Schema: 1 } || state.LocalProfileId != localProfileId ||
            state.SourceProfileId == Guid.Empty || state.SourceProfileId == localProfileId ||
            state.GroupId == Guid.Empty || state.BranchHash.Length != 64 ||
            !state.BranchHash.All(Uri.IsHexDigit) || state.VersionHash.Length != 64 ||
            !state.VersionHash.All(Uri.IsHexDigit) || !Path.IsPathFullyQualified(state.WorldDirectory))
            throw new InvalidDataException("Separate-copy hosting state is invalid.");
        return state;
    }

    private void SaveSeparateHost(SeparateCopyHostState state) =>
        data.SaveProtected(SeparateHostName(state.LocalProfileId),
            JsonSerializer.SerializeToUtf8Bytes(state));

    private WorldSeparateCopyBranch? CurrentSeparateBranch(Guid sourceProfileId,
        string branchHash)
    {
        var store = new SharedWorldSeparateCopyStore(data);
        var branch = store.Read(sourceProfileId).SingleOrDefault(item =>
            item.BranchHash == branchHash);
        if (branch is null || store.HostReturned(sourceProfileId, branchHash) ||
            !SharedWorldSeparateCopyStore.Verify(branch) ||
            !SharedWorldRouteTrust.DirectIpAddress(branch.Offer.Proposal.CandidateAddress))
            return null;
        var records = authority.Read(sourceProfileId);
        var heads = records.Where(item => !records.Any(child =>
            child.Proposal.ParentAuthorityHash == item.RecordHash)).ToArray();
        var proposal = branch.Offer.Proposal;
        var parent = heads.SingleOrDefault();
        if (heads.Length > 1 || proposal.ParentAuthorityHash != parent?.RecordHash ||
            proposal.Epoch != (parent?.Proposal.Epoch ?? 0) + 1)
            return null;
        var candidate = branch.Offer.Roster.Members.SingleOrDefault(item =>
            item.DeviceId == proposal.ProposerDeviceId &&
            item.PublicKey == branch.CandidatePublicKey);
        if (candidate is not { Revoked: false, Grants: { Receive: true, EligibleHost: true } } ||
            candidate.AccessExpiresUtc is { } end && end <= clock.GetUtcNow() ||
            !authority.HasLocalSuccessorKeys(sourceProfileId, proposal,
                proposal.ProposerDeviceId)) return null;
        return branch;
    }

    internal async Task<SeparateCopyRouteProof?> SignSeparateRouteProofAsync(
        Guid sourceProfileId, string branchHash, SeparateCopyRouteChallenge challenge,
        string tlsFingerprint)
    {
        await gate.WaitAsync();
        try
        {
            var branch = CurrentSeparateBranch(sourceProfileId, branchHash);
            if (branch is null || challenge is null ||
                settings.CompanionEndpoint != branch.Offer.Proposal.CandidateAddress ||
                !settings.CompanionListeningEnabled ||
                tlsFingerprint != branch.Offer.CandidateTlsFingerprint ||
                !SharedWorldSeparateRoute.VerifyChallenge(challenge, branch, clock.GetUtcNow()) ||
                data.HasProtected($"shared-world-pc-signing-{challenge.ObserverDeviceId:N}.protected"))
                return null;
            var keyBytes = data.LoadProtected(
                $"shared-world-pc-signing-{branch.Offer.Proposal.ProposerDeviceId:N}.protected");
            if (keyBytes is null) return null;
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(keyBytes, out _);
            return SharedWorldSeparateRoute.SignProof(branch, challenge, key);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return null; }
        finally { gate.Release(); }
    }

    internal async Task<bool> ConfirmSeparateRouteAsync(Guid sourceProfileId,
        string branchHash, SeparateCopyRouteConfirmation confirmation,
        string tlsFingerprint)
    {
        await gate.WaitAsync();
        try
        {
            var branch = CurrentSeparateBranch(sourceProfileId, branchHash);
            if (branch is null || confirmation?.Challenge is null || confirmation.Proof is null ||
                !settings.CompanionListeningEnabled ||
                settings.CompanionEndpoint != branch.Offer.Proposal.CandidateAddress ||
                tlsFingerprint != branch.Offer.CandidateTlsFingerprint ||
                data.HasProtected($"shared-world-pc-signing-{confirmation.Challenge.ObserverDeviceId:N}.protected") ||
                !SharedWorldSeparateRoute.VerifyChallenge(confirmation.Challenge, branch,
                    clock.GetUtcNow()) ||
                !SharedWorldSeparateRoute.VerifyProof(confirmation.Proof,
                    confirmation.Challenge, branch)) return false;
            data.SaveProtected(SeparateRouteName(sourceProfileId, branchHash),
                JsonSerializer.SerializeToUtf8Bytes(new SeparateCopyRouteObservation(1,
                    branchHash, confirmation.Challenge, tlsFingerprint, clock.GetUtcNow())));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return false; }
        finally { gate.Release(); }
    }

    private bool RecentSeparateRoute(WorldSeparateCopyBranch branch)
    {
        var bytes = data.LoadProtected(SeparateRouteName(branch.Offer.Proposal.ProfileId,
            branch.BranchHash));
        if (bytes is null) return false;
        var observation = JsonSerializer.Deserialize<SeparateCopyRouteObservation>(bytes);
        if (observation is not { Schema: 1 } ||
            observation.BranchHash != branch.BranchHash ||
            observation.TlsFingerprint != branch.Offer.CandidateTlsFingerprint ||
            observation.ObservedUtc > clock.GetUtcNow() ||
            clock.GetUtcNow() - observation.ObservedUtc > TimeSpan.FromHours(1) ||
            !SharedWorldSeparateRoute.VerifyChallenge(observation.Challenge, branch,
                observation.ObservedUtc)) return false;
        return true;
    }

    private static Guid SeparateLocalProfileId(string branchHash)
    {
        if (branchHash.Length != 64 || !branchHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Separate-copy branch hash is invalid.");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            "TogetherServer separate local profile v1\n" + branchHash));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string SeparateWorldRoot(LocalData data, SharedWorldVersion version,
        Guid localProfileId) => version.Game switch
    {
        GameKinds.Valheim => Path.Combine(data.WorldImportsRoot, localProfileId.ToString("N")),
        GameKinds.Factorio => Path.Combine(data.FactorioServersRoot,
            localProfileId.ToString("N"), version.WorldId),
        GameKinds.MinecraftJava or GameKinds.MinecraftBedrock =>
            Path.Combine(data.MinecraftInstallRoot, localProfileId.ToString("N")),
        _ => data.NewWorldDirectory(localProfileId)
    };

    private static string SeparateSetupHash(ServerProfile profile, TakeoverLocalSetup setup) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.Id, profile.Kind, profile.WorldId, profile.WorldDirectory,
            ProfileGamePort = profile.GamePort, profile.ExecutablePath, profile.ServerName,
            profile.SeparateCopySourceProfileId, profile.SeparateCopyBranchHash,
            profile.Minecraft?.ServerJarPath, profile.Factorio?.RconPort,
            setup.ServerFile, setup.GameVersion, setup.EnabledAddOns,
            setup.ControlPort, setup.GamePort
        })));

    private string VerifySeparateVault(WorldSeparateCopyBranch branch)
    {
        var proposal = branch.Offer.Proposal;
        var vault = Path.Combine(data.RootPath, "received-shared-worlds",
            proposal.ProposerDeviceId.ToString("N"), proposal.ProfileId.ToString("N"));
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
        var version = FriendLink.ReadReceivedLatest(vault);
        if (version?.VersionHash != branch.Offer.Version.VersionHash ||
            version.GroupId != proposal.GroupId ||
            version.SigningPublicKey != branch.Offer.Roster.OwnerPublicKey)
            throw new InvalidDataException("The exact signed save is missing from this PC's vault.");
        var payload = Path.Combine(vault, version.VersionHash,
            SharedWorldService.PayloadDirectory);
        foreach (var file in version.Files)
            SharedWorldService.VerifyFile(SharedWorldService.SafeChild(payload, file.Path), file);
        return vault;
    }

    public async Task<SeparateCopyHostStatus> SeparateCopyHostStatusAsync(Guid sourceProfileId,
        string branchHash)
    {
        await gate.WaitAsync();
        try
        {
            var branch = new SharedWorldSeparateCopyStore(data).Read(sourceProfileId)
                .SingleOrDefault(item => item.BranchHash == branchHash);
            if (branch is null) return new(false, false, false, true,
                "This signed separate-copy proof is missing. Keep existing saves for review.");
            var localId = SeparateLocalProfileId(branchHash);
            var state = ReadSeparateHost(localId);
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == localId);
            var review = CurrentSeparateBranch(sourceProfileId, branchHash) is null ||
                state is not null && (state.SourceProfileId != sourceProfileId ||
                    state.BranchHash != branchHash ||
                    state.VersionHash != branch.Offer.Version.VersionHash ||
                    state.WorldDirectory != SeparateWorldRoot(data, branch.Offer.Version, localId)) ||
                profile is not null && (state is null ||
                    profile.SeparateCopySourceProfileId != sourceProfileId ||
                    profile.SeparateCopyBranchHash != branchHash);
            var running = runs.Any(item => item.ProfileId == localId &&
                Identity(item) == "Matched");
            return new(true, profile is not null, state?.Ready == true && !review && !running,
                review, review ?
                    "The old Host returned or signed authority changed. Keep both histories, gracefully stop any running separate server, and ask the group to review." :
                    running ? "This warned separate copy is running. Another server may also be running; keep both histories for group review." :
                    state?.Ready == true ? "Manual separate-copy Start is available while the old Host remains unreachable. This is not the group's authoritative world." :
                    profile is not null ? "The separate world is restored. Finish the second-PC route and local setup checks." :
                    "The signed separate copy is recorded. Restore it into a fresh local managed world.",
                localId, branchHash,
                branch.Offer.Version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock ?
                    SeparateWorldRoot(data, branch.Offer.Version, localId) : null,
                branch.Offer.Version.PortableSetup.AddOns, running);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return new(false, false, false, true,
            "Separate-copy state failed verification. Keep every save and review this PC before hosting."); }
        finally { gate.Release(); }
    }

    public async Task<SuccessorRestoreResult> RestoreSeparateCopyAsync(Guid sourceProfileId,
        SeparateCopyHostRestoreRequest request, CancellationToken cancellationToken = default,
        Func<string, long>? freeBytes = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (request?.Setup is null || sourceProfileId == Guid.Empty ||
                request.BranchHash is not { Length: 64 } ||
                !request.BranchHash.All(Uri.IsHexDigit))
                return new(false, "InvalidSeparateRestore", "Choose an exact signed separate copy.");
            var branch = CurrentSeparateBranch(sourceProfileId, request.BranchHash);
            if (branch is null)
                return new(false, "SeparateHistoryReviewRequired",
                    "The old Host returned, authority changed, or this signed copy is no longer current. Keep both histories for review.");
            var version = branch.Offer.Version;
            if (!ValheimSetup.ValidWorldId(version.WorldId))
                return new(false, "InvalidSeparateRestore", "The signed world name is invalid.");
            var localId = SeparateLocalProfileId(branch.BranchHash);
            if (localId == sourceProfileId || runs.Any(item => item.ProfileId == localId))
                return new(false, "WorldAlreadyManaged", "A managed run already uses this separate-copy location.");
            var worldRoot = SeparateWorldRoot(data, version, localId);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, worldRoot);
            var destination = SuccessorPayloadRoot(worldRoot, version);
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, destination);
            var prior = ReadSeparateHost(localId);
            var retry = prior is { Schema: 1 } && prior.SourceProfileId == sourceProfileId &&
                prior.GroupId == version.GroupId && prior.BranchHash == branch.BranchHash &&
                prior.VersionHash == version.VersionHash && prior.WorldDirectory == worldRoot;
            if (prior is not null && !retry)
                return new(false, "SeparateRestoreReviewRequired", "A different restore attempt occupies this location. No files were replaced.");
            var existingProfile = settings.Profiles.SingleOrDefault(item => item.Id == localId);
            if (existingProfile is not null)
                return retry && existingProfile.SeparateCopySourceProfileId == sourceProfileId &&
                    existingProfile.SeparateCopyBranchHash == branch.BranchHash &&
                    existingProfile.WorldDirectory == worldRoot ?
                    new(true, "SeparateRestored", "This separate world is preserved. Finish the local and second-PC route checks before manual Start.",
                        localId, version.VersionHash) :
                    new(false, "WorldAlreadyManaged", "Another local server already uses this managed profile ID.");
            var minecraft = version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock;
            if (!retry && (Directory.Exists(destination) || File.Exists(destination) ||
                !minecraft && (Directory.Exists(worldRoot) || File.Exists(worldRoot))))
                return new(false, "DestinationExists", "A local managed world already occupies this location. No files were changed.");
            var vault = VerifySeparateVault(branch);
            var setup = request.Setup;
            if (setup.ControlPort != settings.CompanionPort ||
                setup.GamePort is < 1024 or > 65535 ||
                version.Game == GameKinds.Valheim && setup.GamePort == 65535)
                return new(false, "SeparatePortsInvalid", "Choose this PC's current control port and a valid game port.");
            var local = SharedWorldReadiness.Check(vault, worldRoot, setup,
                new TakeoverAuthority(true, true, true, true, true, true),
                branch.Offer.Roster.OwnerPublicKey, version.GroupId, freeBytes);
            if (local.Reasons.Count > 0)
                return new(false, "LocalSetupIncomplete", "Complete local game, add-on, password, port, and space checks.",
                    PendingChecks: local.Reasons);
            if (version.Game == GameKinds.Valheim &&
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
                    request.PreparedServerRoot, version, setup, executable) is { } minecraftIssue)
                return new(false, "MinecraftSetupIncomplete", minecraftIssue);
            if (version.Game == GameKinds.Factorio &&
                (request.FactorioRconPort is not >= 1024 or > 65535 ||
                 request.FactorioRconPort == setup.GamePort ||
                 request.FactorioRconPort == setup.ControlPort))
                return new(false, "FactorioRconPortInvalid", "Choose a separate local Factorio RCON port.");
            var profile = new ServerProfile
            {
                Id = localId, Kind = version.Game, WorldId = version.WorldId,
                Name = request.Name, ServerName = request.ServerName,
                WorldSource = "Existing", WorldDirectory = worldRoot,
                GamePort = setup.GamePort, ExecutablePath = executable,
                Crossplay = version.PortableSetup.Crossplay, PublicListing = false,
                SharedSavesEnabled = false, SeparateCopySourceProfileId = sourceProfileId,
                SeparateCopyBranchHash = branch.BranchHash,
                Backups = new BackupOptions { Enabled = true, RetentionCount = 50 },
                CrashRecovery = new CrashRecoveryOptions { Enabled = false },
                Minecraft = version.Game == GameKinds.MinecraftJava
                    ? new MinecraftOptions { ServerJarPath = setup.ServerFile! } : null,
                Factorio = version.Game == GameKinds.Factorio
                    ? new FactorioOptions { RconPort = request.FactorioRconPort!.Value } : null
            };
            var next = CopySettings(settings);
            next.Profiles = [.. settings.Profiles, profile];
            if (Validate(next) is { } invalid)
                return new(false, "HostSetupRejected", invalid);
            if (!games.TryGet(profile.Kind, out var driver) ||
                !games.PortsAvailableForStart(driver.Ports(profile)))
                return new(false, "GamePortInUse", "A selected game port is already in use.");
            var payload = Path.Combine(vault, version.VersionHash,
                SharedWorldService.PayloadDirectory);
            var parent = Path.GetDirectoryName(destination)!;
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, parent);
            Directory.CreateDirectory(parent);
            var temporary = Path.Combine(parent, ".separate-" + branch.BranchHash);
            if (!retry) SaveSeparateHost(new SeparateCopyHostState(1, sourceProfileId,
                localId, version.GroupId, branch.BranchHash, version.VersionHash, worldRoot));
            if (!Directory.Exists(destination))
            {
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, temporary);
                if (Directory.Exists(temporary))
                {
                    if (!retry) return new(false, "SeparateRestoreReviewRequired",
                        "An unexpected partial copy remains in managed storage. Keep it for review.");
                }
                else Directory.CreateDirectory(temporary);
                foreach (var file in version.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = SharedWorldService.SafeChild(payload, file.Path);
                    SharedWorldService.VerifyFile(source, file);
                    var target = SharedWorldService.SafeChild(temporary, file.Path);
                    if (!File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None, SharedWorldService.ChunkBytes, FileOptions.WriteThrough);
                        input.CopyTo(output);
                    }
                    SharedWorldService.VerifyFile(target, file);
                }
                if (!VerifiedSuccessorCopy(temporary, version))
                    return new(false, "RestoreVerificationFailed", "The copied save failed hash verification.");
                VerifySeparateVault(branch);
                if (CurrentSeparateBranch(sourceProfileId, branch.BranchHash) is null)
                    return new(false, "SeparateHistoryReviewRequired", "Signed authority changed during copying.");
                if (Directory.Exists(destination) || File.Exists(destination))
                    return new(false, "DestinationExists", "The world destination changed during copying. No files were replaced.");
                Directory.Move(temporary, destination);
            }
            if (!VerifiedSuccessorCopy(destination, version))
                return new(false, "RestoreVerificationFailed", "The restored world differs from the signed save.");
            if (CurrentSeparateBranch(sourceProfileId, branch.BranchHash) is null)
                return new(false, "SeparateHistoryReviewRequired", "Signed authority changed before setup was saved.");
            if (version.Game == GameKinds.Valheim)
                data.SaveValheimPassword(localId, request.GamePassword!);
            var saved = UpdateSettingsLocked(next);
            if (!saved.Ok) return new(false, "HostSetupRejected", saved.Message);
            return new(true, "SeparateRestored",
                "The verified save is in a fresh managed location. It is a separate copy and cannot start until local and second-PC route checks pass.",
                localId, version.VersionHash,
                ["Check the direct-IP control route from another approved PC.",
                 "Start manually only after the two-minute Host-loss check; test the game join after Start."]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(false, "SeparateRestoreInterrupted", "The copy was interrupted. Its staging files are kept for a safe retry."); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "RestoreVerificationFailed", "The signed save or local setup failed verification: " + ex.Message); }
        finally { gate.Release(); }
    }

    private string? SeparateCopyPreStartIssue(SeparateCopyHostState state,
        WorldSeparateCopyBranch branch, ServerProfile profile, TakeoverLocalSetup setup)
    {
        var version = branch.Offer.Version;
        if (state.Schema != 1 || state.SourceProfileId != branch.Offer.Proposal.ProfileId ||
            state.BranchHash != branch.BranchHash || state.GroupId != version.GroupId ||
            state.VersionHash != version.VersionHash || state.LocalProfileId != profile.Id ||
            profile.SeparateCopySourceProfileId != state.SourceProfileId ||
            profile.SeparateCopyBranchHash != branch.BranchHash ||
            profile.WorldDirectory != state.WorldDirectory || profile.Kind != version.Game ||
            profile.WorldId != version.WorldId || profile.GamePort != setup.GamePort ||
            setup.ControlPort != settings.CompanionPort ||
            !setup.NewPasswordConfigured || profile.SharedSavesEnabled ||
            profile.CrashRecovery.Enabled ||
            settings.CompanionEndpoint != branch.Offer.Proposal.CandidateAddress ||
            !settings.CompanionListeningEnabled)
            return "The separate-copy profile, signed save, or direct-IP Host setup changed.";
        var localId = SeparateLocalProfileId(branch.BranchHash);
        if (profile.Id != localId ||
            state.WorldDirectory != SeparateWorldRoot(data, version, localId) ||
            CurrentSeparateBranch(state.SourceProfileId, branch.BranchHash) is null)
            return "The old Host returned or signed authority changed. Keep both histories for review.";
        using (var certificate = new HostIdentity(data).Ensure(settings.CompanionEndpoint))
            if (HostIdentity.Fingerprint(certificate) != branch.Offer.CandidateTlsFingerprint)
                return "This PC's pinned HTTPS certificate changed.";
        if (!RecentSeparateRoute(branch))
            return "A recent signed control-route check from a different approved Friend PC is required.";
        var vault = VerifySeparateVault(branch);
        var readiness = SharedWorldReadiness.Check(vault, state.WorldDirectory, setup,
            new TakeoverAuthority(true, true, true, true, true, true),
            branch.Offer.Roster.OwnerPublicKey, version.GroupId);
        if (readiness.Reasons.Count > 0)
            return readiness.Reasons[0];
        var installed = ServerAddOns.List(data, profile);
        if (version.Game is not (GameKinds.Fixture or GameKinds.MinecraftJava) &&
            (!installed.Ok || installed.GameVersion != version.PortableSetup.GameVersion) ||
            setup.EnabledAddOns is null ||
            (version.PortableSetup.AddOns?.Count ?? 0) != setup.EnabledAddOns.Count ||
            (version.PortableSetup.AddOns ?? []).Any(required =>
                !setup.EnabledAddOns.Contains(required) || !installed.Items.Any(item =>
                    item.Enabled && item.Name == required.Name && item.Version == required.Version &&
                    item.RequiredGameVersion == required.RequiredGameVersion &&
                    item.Type == required.Type && (required.Id is null ||
                        item.Key.EndsWith(required.Id, StringComparison.OrdinalIgnoreCase)))))
            return "Installed game files or enabled add-ons differ from the signed setup.";
        if (version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock &&
            MinecraftPreparedRoot.Check(data.RootPath, state.WorldDirectory,
                state.WorldDirectory, version, setup, profile.ExecutablePath) is not null)
            return "The prepared Minecraft server files changed.";
        if (version.Game == GameKinds.Valheim &&
            data.LoadValheimPassword(profile.Id) is not { Length: >= 5 and <= 64 })
            return "A new game password is missing.";
        if (!state.Started)
        {
            if (!VerifiedSuccessorCopy(SuccessorPayloadRoot(state.WorldDirectory, version), version))
                return "The restored files no longer match the exact signed save.";
        }
        else
        {
            var latestBackup = backups.List(profile.Id).FirstOrDefault(item =>
                item.BackupKind == BackupKinds.Rolling);
            if (latestBackup is null || !backups.Verify(profile, latestBackup.Id).Ok)
                return "The separate world needs a verified post-Stop backup before another Start.";
        }
        if (!games.TryGet(profile.Kind, out var driver) ||
            driver.ValidateForStart(profile) is not null ||
            !games.PortsAvailableForStart(driver.Ports(profile)))
            return "The installed game files or local game ports are not ready for Start.";
        if (runs.Any(item => item.ProfileId != profile.Id &&
            settings.Profiles.Any(other => other.Id == item.ProfileId &&
                other.SeparateCopySourceProfileId == state.SourceProfileId)))
            return "Another managed separate copy of this world is already running.";
        return null;
    }

    public async Task<SuccessorRestoreResult> FinishSeparateCopyAsync(Guid sourceProfileId,
        SeparateCopyHostFinishRequest request)
    {
        await gate.WaitAsync();
        try
        {
            if (request?.Setup is null || request.BranchHash is not { Length: 64 } ||
                !request.BranchHash.All(Uri.IsHexDigit))
                return new(false, "InvalidSeparateFinish", "Choose the exact restored separate copy.");
            var localId = SeparateLocalProfileId(request.BranchHash);
            var state = ReadSeparateHost(localId);
            var branch = CurrentSeparateBranch(sourceProfileId, request.BranchHash);
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == localId);
            if (state is null || branch is null || profile is null)
                return new(false, "SeparateHistoryReviewRequired", "The separate copy or its signed history needs review.");
            if (request.ExecutablePath is not null &&
                request.ExecutablePath != profile.ExecutablePath ||
                request.PreparedServerRoot is not null &&
                request.PreparedServerRoot != state.WorldDirectory)
                return new(false, "SetupChanged", "The reviewed local server setup changed.");
            var issue = SeparateCopyPreStartIssue(state, branch, profile, request.Setup);
            if (issue is not null) return new(false, "SeparateChecksPending", issue);
            SaveSeparateHost(state with { Ready = true,
                SetupHash = SeparateSetupHash(profile, request.Setup) });
            return new(true, "SeparateReadyForManualStart",
                "This warned separate copy is ready for an explicit manual Start while the old Host stays unreachable. It is not authoritative. Test a real game join and saved Stop after Start.",
                localId, branch.Offer.Version.VersionHash);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "SeparateChecksPending", "The signed save or local checks failed verification."); }
        finally { gate.Release(); }
    }

    private string? SeparateCopyStartIssue(Guid localProfileId, ServerProfile profile,
        string branchHash)
    {
        var state = ReadSeparateHost(localProfileId);
        var branch = profile.SeparateCopySourceProfileId is { } source ?
            CurrentSeparateBranch(source, branchHash) : null;
        if (state is not { Ready: true } || branch is null ||
            profile.SeparateCopyBranchHash != branchHash)
            return "The separate copy is no longer Start-ready. Review both histories and the Host connection.";
        var setup = new TakeoverLocalSetup(profile.Kind == GameKinds.MinecraftJava ?
            profile.Minecraft?.ServerJarPath : profile.ExecutablePath,
            branch.Offer.Version.PortableSetup.GameVersion,
            branch.Offer.Version.PortableSetup.AddOns, true,
            settings.CompanionPort, profile.GamePort);
        if (state.SetupHash != SeparateSetupHash(profile, setup))
            return "The local setup changed after review. Finish separate-copy checks again.";
        return SeparateCopyPreStartIssue(state, branch, profile, setup);
    }

    public async Task<ActionResult> StartSeparateCopyAsync(Guid localProfileId,
        string branchHash)
    {
        await gate.WaitAsync();
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == localProfileId);
            if (profile?.SeparateCopySourceProfileId is null ||
                profile.SeparateCopyBranchHash != branchHash)
                return Result(false, "SeparateCopyMissing",
                    "Choose a restored signed separate copy on this PC.");
            var started = StartUnderGate(localProfileId, false, branchHash);
            if (!started.Ok) return started;
            try
            {
                var state = ReadSeparateHost(localProfileId) ??
                    throw new InvalidDataException("Separate-copy hosting state is missing.");
                SaveSeparateHost(state with { Started = true });
                return started with
                {
                    Code = "SeparateCopyStarted",
                    Message = "The warned separate server started on this PC. Another game server may still be running. Keep both histories and arrange group review."
                };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
                CryptographicException or UnauthorizedAccessException or ArgumentException)
            {
                return Result(false, "SeparateRunReviewRequired",
                    "The game may have started, but its separate-copy record could not be saved. Check the exact managed run before another action.");
            }
        }
        finally { gate.Release(); }
    }
}
