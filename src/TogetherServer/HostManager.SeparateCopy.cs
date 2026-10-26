using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record SeparateCopyHostStatus(bool Recorded, bool Restored,
    bool ReadyForManualStart, bool ReviewRequired, string Message,
    Guid? LocalProfileId = null, string? BranchHash = null,
    string? PreparedServerRoot = null,
    IReadOnlyList<SharedWorldPortableAddOn>? RequiredAddOns = null,
    bool Running = false, bool ControlRouteVerified = false);
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
    // Called only after the lifecycle gate proved exact graceful exit and
    // completed its rolling backup. It never scans a running world.
    internal SharedWorldResult PublishSeparateCopyAfterStopUnderGate(ServerProfile profile,
        Guid backupId, CancellationToken cancellationToken = default, Action? requireCurrentSource = null)
    {
        try
        {
            if (profile.SeparateCopySourceProfileId is not { } source ||
                profile.SeparateCopyBranchHash is not { } hash)
                return new(false, "SeparateCopyMissing", "Choose a recorded local recovery branch.");
            var branch = CurrentSeparateBranch(source, hash);
            if (branch is null)
                return new(false, "RecoverySnapshotPublishFailed",
                    "The completed backup remains local; the recovery branch identity needs review.");
            return new SharedWorldSeparateCopyStore(data).PublishAfterStop(branch, profile, backupId, backups,
                cancellationToken, requireCurrentSource);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, "RecoverySnapshotPublishFailed",
                "The completed backup remains local; the recovery branch identity needs review.");
        }
    }

    internal async Task<WorldSeparateCopySnapshot?> ReadSeparateCopySnapshotAsync(
        Guid sourceProfileId, string branchHash)
    {
        await gate.WaitAsync();
        try
        {
            var branch = CurrentSeparateBranch(sourceProfileId, branchHash);
            return branch is null ? null : new SharedWorldSeparateCopyStore(data).ReadLatestSnapshot(branch);
        }
        finally { gate.Release(); }
    }

    internal async Task<SharedWorldVersion?> ReadSeparateCopyManifestAsync(Guid sourceProfileId,
        string branchHash, long number, string versionHash)
    {
        await gate.WaitAsync();
        try
        {
            var branch = CurrentSeparateBranch(sourceProfileId, branchHash);
            return branch is null ? null : new SharedWorldSeparateCopyStore(data)
                .ReadPublishedManifest(branch, number, versionHash);
        }
        finally { gate.Release(); }
    }

    internal async Task<byte[]?> ReadSeparateCopyChunkAsync(Guid sourceProfileId,
        string branchHash, long number, string versionHash, int fileIndex, long offset)
    {
        await gate.WaitAsync();
        try
        {
            var branch = CurrentSeparateBranch(sourceProfileId, branchHash);
            return branch is null ? null : new SharedWorldSeparateCopyStore(data)
                .ReadPublishedChunk(branch, number, versionHash, fileIndex, offset);
        }
        finally { gate.Release(); }
    }

    internal Func<string, int, string, string, bool>? CompanionListenerOwnershipProbe { get; set; }

    private bool OwnsSeparateControlListener(WorldSeparateCopyBranch branch) =>
        settings.CompanionListeningEnabled &&
        settings.CompanionEndpoint == branch.Offer.Proposal.CandidateAddress &&
        CompanionListenerOwnershipProbe?.Invoke(settings.CompanionEndpoint,
            settings.CompanionPort, branch.Offer.CandidateTlsFingerprint,
            settings.CompanionBindAddress) == true;

    private static string SeparateHostName(Guid localProfileId) => SharedWorldBranchLiveSource.StateName(localProfileId);
    private static string SeparateRouteName(Guid sourceProfileId, string branchHash) =>
        $"separate-route-{sourceProfileId:N}-{branchHash}.protected";

    private SeparateCopyHostState? ReadSeparateHost(Guid localProfileId) => SharedWorldBranchLiveSource.ReadState(data, localProfileId);

    private void SaveSeparateHost(SeparateCopyHostState state) =>
        data.SaveProtected(SeparateHostName(state.LocalProfileId),
            JsonSerializer.SerializeToUtf8Bytes(state));

    private WorldSeparateCopyBranch? CurrentSeparateBranch(Guid sourceProfileId,
        string branchHash)
    {
        try { return SharedWorldBranchLiveSource.RequireCurrentBranch(data, sourceProfileId, branchHash, clock.GetUtcNow()); }
        catch (Exception ex) when (SharedWorldBranchLiveSource.ExpectedFailure(ex)) { return null; }
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
            var keyBytes = data.LoadProtected(SharedWorldSeparateCopyStore.CandidateSigningKeyName(branch));
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
            var now = clock.GetUtcNow();
            if (branch is null || confirmation?.Challenge is null ||
                !settings.CompanionListeningEnabled ||
                settings.CompanionEndpoint != branch.Offer.Proposal.CandidateAddress ||
                tlsFingerprint != branch.Offer.CandidateTlsFingerprint ||
                data.HasProtected($"shared-world-pc-signing-{confirmation.Challenge.ObserverDeviceId:N}.protected") ||
                !SharedWorldSeparateRoute.VerifyConfirmation(confirmation, branch, now))
                return false;
            var name = SeparateRouteName(sourceProfileId, branchHash);
            var priorBytes = data.LoadProtected(name);
            if (priorBytes is null && data.HasProtected(name)) return false;
            if (priorBytes is { Length: > 32 * 1024 }) return false;
            var prior = priorBytes is null ? null :
                JsonSerializer.Deserialize<SeparateCopyRouteObservation>(priorBytes);
            if (priorBytes is not null &&
                !ValidSeparateRouteObservation(prior, branch, now)) return false;
            var used = prior?.UsedNonces.Where(item =>
                now - item.ConfirmedUtc <= TimeSpan.FromMinutes(11)).ToList() ?? [];
            if (used.Count >= 128 || used.Any(item =>
                item.Nonce == confirmation.Challenge.Nonce)) return false;
            used.Add(new(confirmation.Challenge.Nonce, now));
            data.SaveProtected(name,
                JsonSerializer.SerializeToUtf8Bytes(new SeparateCopyRouteObservation(2,
                    branchHash, confirmation, tlsFingerprint, now, used)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return false; }
        finally { gate.Release(); }
    }

    private bool RecentSeparateRoute(WorldSeparateCopyBranch branch)
    {
        try
        {
            var bytes = data.LoadProtected(SeparateRouteName(branch.Offer.Proposal.ProfileId,
                branch.BranchHash));
            if (bytes is null || bytes.Length > 32 * 1024) return false;
            var observation = JsonSerializer.Deserialize<SeparateCopyRouteObservation>(bytes);
            return ValidSeparateRouteObservation(observation, branch, clock.GetUtcNow()) &&
                clock.GetUtcNow() - observation!.ObservedUtc <= TimeSpan.FromHours(1);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    private static bool ValidSeparateRouteObservation(SeparateCopyRouteObservation? observation,
        WorldSeparateCopyBranch branch, DateTimeOffset now)
    {
        if (observation is not { Schema: 2 } ||
            observation.BranchHash != branch.BranchHash ||
            observation.TlsFingerprint != branch.Offer.CandidateTlsFingerprint ||
            observation.ObservedUtc > now ||
            observation.Confirmation?.Challenge is not { } challenge ||
            observation.UsedNonces is not { Count: > 0 and <= 128 } used ||
            used.Any(item => item is null || item.ConfirmedUtc > observation.ObservedUtc ||
                item.Nonce is not { Length: 43 }) ||
            used.Select(item => item.Nonce).Distinct(StringComparer.Ordinal).Count() != used.Count ||
            !used.Any(item => item.Nonce == challenge.Nonce &&
                item.ConfirmedUtc == observation.ObservedUtc)) return false;
        return SharedWorldSeparateRoute.VerifyConfirmation(observation.Confirmation,
            branch, observation.ObservedUtc);
    }

    private static Guid SeparateLocalProfileId(string branchHash) => SharedWorldBranchLiveSource.LocalProfileId(branchHash);

    private static string SeparateWorldRoot(LocalData data, SharedWorldVersion version,
        Guid localProfileId) => SharedWorldBranchLiveSource.WorldRoot(data, version, localProfileId);

    private static string SeparateSetupHash(ServerProfile profile, TakeoverLocalSetup setup) => SharedWorldBranchLiveSource.SetupHash(profile, setup);

    private string VerifySeparateVault(WorldSeparateCopyBranch branch)
    {
        var proposal = branch.Offer.Proposal;
        var retained = new SharedWorldSeparateCopyStore(data).RetainedRoot(branch);
        var vault = Directory.Exists(retained) ? retained :
            Path.Combine(data.RootPath, "received-shared-worlds",
                proposal.ProposerDeviceId.ToString("N"), proposal.ProfileId.ToString("N"));
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vault);
        var version = FriendLink.ReadReceivedLatest(vault);
        if (version?.VersionHash != branch.Offer.Version.VersionHash ||
            version.GroupId != proposal.GroupId || !SharedWorldSeparateCopyStore.Verify(branch))
            throw new InvalidDataException("The exact signed save is missing from this PC's vault.");
        var payload = Path.Combine(vault, version.VersionHash,
            SharedWorldService.PayloadDirectory);
        foreach (var file in version.Files)
            SharedWorldService.VerifyFile(SharedWorldService.SafeChild(payload, file.Path), file);
        return vault;
    }

    public async Task<SeparateCopyHostStatus> SeparateCopyHostStatusAsync(Guid sourceProfileId,
        string branchHash, bool hostLossCurrent = false)
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
            var issue = !review && !running && state is { Ready: true } && profile is not null ?
                SeparateCopyStartIssue(localId, profile, branchHash) : null;
            var ready = state?.Ready == true && !review && !running && issue is null;
            return new(true, profile is not null, ready,
                review, review ?
                    "The recovery branch's signed identity or local setup needs review. Keep every history; a returning Host does not stop this server." :
                    running ? "This warned separate copy is running. Another server may also be running; keep both histories for group review." :
                    issue is not null ? "Separate-copy checks need attention: " + issue :
                    ready ? "Manual recovery Start is available on this PC. Another server may still be running; this branch is not the group's default world." :
                    profile is not null ? "The recovery world is restored. Finish the local setup checks; a second-PC route check is optional evidence." :
                    "The signed separate copy is recorded. Restore it into a fresh local managed world.",
                localId, branchHash,
                branch.Offer.Version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock ?
                    SeparateWorldRoot(data, branch.Offer.Version, localId) : null,
                branch.Offer.Version.PortableSetup.AddOns, running, RecentSeparateRoute(branch));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or
            CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, false, false, true,
            "Separate-copy state failed verification. Keep every save and review this PC before hosting.");
        }
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
                    "The recovery branch's signed identity or local hosting keys need review. Keep both histories.");
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
                    new(true, "SeparateRestored", "This recovery world is preserved. Finish the local setup checks before manual Start.",
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
                version.SigningPublicKey, version.GroupId, freeBytes,
                ownedControlListener: OwnsSeparateControlListener(branch),
                authorityRecords: []);
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
                Id = localId,
                Kind = version.Game,
                WorldId = version.WorldId,
                Name = request.Name,
                ServerName = request.ServerName,
                WorldSource = "Existing",
                WorldDirectory = worldRoot,
                GamePort = setup.GamePort,
                ExecutablePath = executable,
                Crossplay = version.PortableSetup.Crossplay,
                PublicListing = false,
                SharedSavesEnabled = false,
                SeparateCopySourceProfileId = sourceProfileId,
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
                "The verified save is in a fresh managed location. Finish local setup and manually Start this separate recovery branch.",
                localId, version.VersionHash,
                ["Finish local game, add-on, password, port, and space checks.",
                 "A second-PC control-route check is optional evidence. Test a real game join after Start."]);
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
            settings.CompanionEndpoint != branch.Offer.Proposal.CandidateAddress)
            return "The separate-copy profile, signed save, or direct-IP Host setup changed.";
        var localId = SeparateLocalProfileId(branch.BranchHash);
        if (profile.Id != localId ||
            state.WorldDirectory != SeparateWorldRoot(data, version, localId) ||
            CurrentSeparateBranch(state.SourceProfileId, branch.BranchHash) is null)
            return "The recovery branch's signed identity or local keys changed. Keep both histories for review.";
        using (var certificate = new HostIdentity(data).Load())
            if (certificate is null || !certificate.HasPrivateKey ||
                HostIdentity.Fingerprint(certificate) != branch.Offer.CandidateTlsFingerprint)
                return "This PC's pinned HTTPS certificate changed.";
        var vault = VerifySeparateVault(branch);
        var readiness = SharedWorldReadiness.Check(vault, state.WorldDirectory, setup,
            new TakeoverAuthority(true, true, true, true, true, true),
            version.SigningPublicKey, version.GroupId,
            ownedControlListener: OwnsSeparateControlListener(branch),
            authorityRecords: []);
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
                state.WorldDirectory, version, setup, profile.ExecutablePath) is { } minecraftIssue)
            return minecraftIssue;
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
            SaveSeparateHost(state with
            {
                Ready = true,
                SetupHash = SeparateSetupHash(profile, request.Setup)
            });
            return new(true, "SeparateReadyForManualStart",
                "This recovery branch is ready for an explicit manual Start, including while the original Host responds. It does not select the group's default world. Test a real join and saved Stop after Start.",
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

    public Task<ActionResult> StartSeparateCopyAsync(Guid localProfileId, string branchHash) =>
        StartSeparateCopyAsync(localProfileId, branchHash, CancellationToken.None);

    public async Task<ActionResult> StartSeparateCopyAsync(Guid localProfileId,
        string branchHash, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == localProfileId);
            if (profile?.SeparateCopySourceProfileId is null ||
                profile.SeparateCopyBranchHash != branchHash)
                return Result(false, "SeparateCopyMissing",
                    "Choose a restored signed separate copy on this PC.");
            var started = StartUnderGate(localProfileId, false, branchHash, cancellationToken: cancellationToken);
            if (!started.Ok) return started;
            try
            {
                var state = ReadSeparateHost(localProfileId) ??
                    throw new InvalidDataException("Separate-copy hosting state is missing.");
                SaveSeparateHost(state with { Started = true });
                return started with
                {
                    Code = "SeparateCopyStarted",
                    Message = "The server is starting on this PC."
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
