namespace TogetherServer;

internal sealed record LocalHostingPortPlan(int GamePort, int ControlPort,
    int? FactorioRconPort, bool Available, Guid? ApprovedPortId = null);

public sealed partial class HostManager
{
    // One server choice authorizes all approved assigned PCs. Keep normal PC
    // access checks and backup policy; publishing never starts a listener/game.
    internal async Task<SharedWorldResult> SetSimpleSharedHostingAsync(Guid profileId, bool enabled,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
            {
                var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
                if (profile is null) return new(false, "UnknownProfile", "Choose a saved server.");
                if (profile.WorldLoadRehearsalId is not null || profile.SeparateCopySourceProfileId is not null ||
                    profile.SeparateCopyBranchHash is not null || authority.HasState(profileId))
                    return new(false, "OwnerSharingRequired", "Change shared hosting on the original owner's PC. Keep every saved history.");
                if (data.HasProtected(PlannedHandoffName(profileId)))
                    return new(false, "PlannedHandoffPending", "Finish or cancel the hosting move before changing shared hosting.");
                if (enabled && SharedAuthorityBlocked(profileId, out var reason)) return new(false, "SharedWorldAuthorityBlocked", reason);
                if (enabled && data.Recovery.LifecycleBlocked) return new(false, "DataRecoveryRequired", "Review this PC's recovery notice before enabling shared hosting.");
                if (enabled && data.HasProtected(SetupRestorePendingName(profileId)))
                    return new(false, "SetupRestoreRecoveryRequired", "Finish the interrupted setup restore first.");
                if (enabled && (profile.Kind == GameKinds.Custom || !games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups))
                    return new(false, "SharingUnsupported", "Shared hosting needs a supported built-in game with backups.");
                if (enabled) RequireSimpleSharedHostingIdentity(profile);
                var next = System.Text.Json.JsonSerializer.Deserialize<HostSettings>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings)) ??
                    throw new InvalidDataException("The current Host settings could not be copied.");
                var changed = next.Profiles.Single(item => item.Id == profileId);
                if (enabled)
                {
                    changed.Backups ??= new();
                    changed.Backups.Enabled = true;
                }
                changed.SharedSavesEnabled = enabled;
                cancellationToken.ThrowIfCancellationRequested();
                // Durable fencing precedes both settings and pairing writes. Incomplete
                // publication cannot keep serving an older roster as current authority.
                pairing.RequireSharedRosterPublication(profileId);
                var saved = UpdateSettingsLocked(next);
                if (!saved.Ok) return new(false, saved.Code, saved.Message);
                pairing.ReconcileSimpleSharedHosting(profileId);
                var roster = PublishSimpleSharedHostingRosterUnderGate(profileId);
                pairing.ConfirmSharedRosterPublished(profileId, roster);
                AnnounceSharingPolicy(profileId, enabled);
                return new(true, "SharedHostingSaved", enabled ?
                    "Friends assigned to this server can receive, share and host its verified saves. Backups after safe Stop are on." :
                    "Shared hosting is off. Friends can no longer receive, share or start hosting this server's saves.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return new(false, "SharedHostingUnconfirmed",
                "The shared hosting change needs review. New save sharing and hosting permissions stay blocked until its signed access is confirmed.");
        }
        finally { gate.Release(); }
    }

    internal async Task<SharedWorldRoster> PublishSimpleSharedHostingRosterAsync(Guid profileId)
    {
        await gate.WaitAsync();
        try
        {
            lock (SharedWorldMutationGate.For(data.RootPath))
                return PublishSimpleSharedHostingRosterUnderGate(profileId);
        }
        finally { gate.Release(); }
    }

    private SharedWorldRoster PublishSimpleSharedHostingRosterUnderGate(Guid profileId)
    {
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId) ??
            throw new InvalidDataException("Choose a saved server.");
        if (profile.WorldLoadRehearsalId is not null || profile.SeparateCopySourceProfileId is not null ||
            profile.SeparateCopyBranchHash is not null || authority.HasState(profileId) ||
            data.HasProtected(PlannedHandoffName(profileId)) || SharedAuthorityBlocked(profileId, out _))
            throw new InvalidDataException("Only the original owner can publish the server's current shared hosting choice.");
        RequireSimpleSharedHostingIdentity(profile);
        pairing.ReconcileSimpleSharedHosting(profileId);
        return sharedWorlds.PublishRoster(profile, pairing.SharedRosterMembers(profileId), applyHostingPolicy: true);
    }

    private void RequireSimpleSharedHostingIdentity(ServerProfile profile)
    {
        var roster = sharedWorlds.ReadRoster(profile);
        var root = Path.Combine(data.RootPath, "shared-worlds", profile.Id.ToString("N"));
        if (roster is null && Directory.Exists(root) &&
            Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) != "roster-dirty"))
            throw new InvalidDataException("The earlier sharing identity needs review. Keep its source and signed history.");
    }

    internal async Task InvalidateLocalHostingPreparationAsync(Guid sourceId, string branchHash,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var localId = SeparateLocalProfileId(branchHash);
            // Never change an active or unresolved run's protected source state.
            if (runs.Any(run => run.ProfileId == localId)) return;
            var state = ReadSeparateHost(localId);
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == localId);
            if (state is { Ready: true } && state.SourceProfileId == sourceId && state.BranchHash == branchHash &&
                profile?.SeparateCopySourceProfileId == sourceId && profile.SeparateCopyBranchHash == branchHash)
                SaveSeparateHost(state with { Ready = false });
        }
        finally { gate.Release(); }
    }

    // Passive status reports the last protected preparation, with current
    // signed access and identity checks. Payload/game hashes are repeated only
    // by explicit Prepare/Finish/Start, never by a dialog polling every few seconds.
    internal async Task<SeparateCopyHostStatus> RecordedLocalHostingStatusAsync(Guid sourceId,
        string branchHash, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var branch = CurrentSeparateBranch(sourceId, branchHash);
            if (branch is null) return new(false, false, false, true, "The signed local history needs review.");
            var localId = SeparateLocalProfileId(branchHash);
            var state = ReadSeparateHost(localId);
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == localId);
            var running = runs.Any(run => run.ProfileId == localId && Identity(run) == "Matched");
            var ready = state is { Ready: true } && profile is not null;
            var review = data.Recovery.LifecycleBlocked;
            if (ready)
            {
                try
                {
                    _ = SharedWorldBranchLiveSource.Resolve(data, profile!);
                    using var certificate = new HostIdentity(data).Load();
                    review |= settings.CompanionEndpoint != branch.Offer.Proposal.CandidateAddress ||
                        certificate is null || !certificate.HasPrivateKey ||
                        HostIdentity.Fingerprint(certificate) != branch.Offer.CandidateTlsFingerprint;
                }
                catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
                { review = true; }
            }
            else if (profile is not null && (state is null || state.BranchHash != branchHash ||
                state.SourceProfileId != sourceId || profile.SeparateCopyBranchHash != branchHash)) review = true;
            return new(true, profile is not null, ready && !running && !review, review,
                review ? "The saved local history or setup needs review. Existing worlds were kept." :
                running ? "This PC is already hosting its local continuation." :
                ready ? "The last preparation passed. Start repeats current file, save and port checks." :
                "Finish preparation before manually starting this local continuation.",
                localId, branchHash, RequiredAddOns: branch.Offer.Version.PortableSetup.AddOns,
                Running: running, ControlRouteVerified: RecentSeparateRoute(branch));
        }
        catch (Exception error) when (SharedWorldBranchLiveSource.ExpectedFailure(error))
        { return new(false, false, false, true, "The saved local history needs review. Existing worlds were kept."); }
        finally { gate.Release(); }
    }

    internal async Task<bool> EnsureLocalHostingEndpointAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.CompanionEndpoint))
                return HostIdentity.TryEndpoint(settings.CompanionEndpoint, out var current) &&
                    current.Port == settings.CompanionPort;
            if (settings.CompanionListeningEnabled) return false;
            var next = CopySettings(settings);
            next.CompanionEndpoint = $"https://127.0.0.1:{settings.CompanionPort}";
            // This supplies a stable signing identity for a local continuation.
            // It does not enable a listener, change its bind or enable controls.
            return UpdateSettingsLocked(next).Ok;
        }
        finally { gate.Release(); }
    }

    internal async Task<LocalHostingPortPlan> LocalHostingPortsAsync(SharedWorldVersion version,
        ServerProfile? existing, CancellationToken cancellationToken, Guid? approvedPortId = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (existing is not null)
            {
                var owned = runs.Any(run => run.ProfileId == existing.Id && Identity(run) == "Matched");
                var available = owned || games.TryGet(existing.Kind, out var existingDriver) &&
                    existingDriver.Ports(existing).All(port => port.Port != settings.CompanionPort) &&
                    games.PortsAvailableForStart(existingDriver.Ports(existing));
                var approval = data.ApprovedGamePorts.Matching(existing.Kind, existing.GamePort,
                    existing.Kind == GameKinds.Factorio ? existing.Factorio?.RconPort : null);
                return new(existing.GamePort, settings.CompanionPort,
                    existing.Factorio?.RconPort, available, approval?.Id);
            }
            var approved = data.ApprovedGamePorts.Find(version.Game, approvedPortId);
            var first = approved?.GamePort ?? version.PortableSetup.GamePort;
            if (first is < 1024 or > 65535 || version.Game == GameKinds.Valheim && first == 65535) first = 2456;
            if (!games.TryGet(version.Game, out var driver))
                return new(first, settings.CompanionPort, null, false);
            var reserved = new HashSet<int> { settings.CompanionPort };
            foreach (var saved in settings.Profiles.Where(saved => runs.Any(run => run.ProfileId == saved.Id)))
                if (games.TryGet(saved.Kind, out var savedDriver))
                    foreach (var port in savedDriver.Ports(saved)) reserved.Add(port.Port);
            // Reuse only this PC's explicit choice. Source ports are a display
            // suggestion until approved; a busy approved port never triggers
            // silent selection of a different unapproved port.
            var candidate = new ServerProfile
            {
                Kind = version.Game, GamePort = first,
                Factorio = version.Game == GameKinds.Factorio ? new() { RconPort = approved?.FactorioRconPort ?? 27015 } : null
            };
            cancellationToken.ThrowIfCancellationRequested();
            var ports = driver.Ports(candidate).ToList();
            if (version.Game == GameKinds.MinecraftBedrock)
                ports.Add(new("UDP", first == 65535 ? first - 1 : first + 1, "IPv6 game", "IPv6"));
            return new(first, settings.CompanionPort, candidate.Factorio?.RconPort,
                ports.All(port => !reserved.Contains(port.Port)) && games.PortsAvailableForStart(ports), approved?.Id);
        }
        finally { gate.Release(); }
    }
}
