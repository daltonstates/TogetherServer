namespace TogetherServer;

internal sealed record LocalHostingPortPlan(int GamePort, int ControlPort,
    int? FactorioRconPort, bool Available);

public sealed partial class HostManager
{
    // The owner's single switch includes backups after safe Stop. Preserve the
    // current retention/reserve and every per-PC permission; no listener starts.
    internal async Task<SharedWorldResult> SetSimpleSharedHostingAsync(Guid profileId, bool enabled,
        CancellationToken cancellationToken)
    {
        if (!enabled) return await SetSharedSavesAsync(profileId, false);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return new(false, "UnknownProfile", "Choose a saved server.");
            if (profile.WorldLoadRehearsalId is not null || profile.SeparateCopySourceProfileId is not null ||
                profile.SeparateCopyBranchHash is not null || authority.HasState(profileId))
                return new(false, "OwnerSharingRequired", "Change shared hosting on the original owner's PC. Keep every saved history.");
            if (data.HasProtected(PlannedHandoffName(profileId)))
                return new(false, "PlannedHandoffPending", "Finish or cancel the hosting move before changing shared hosting.");
            if (SharedAuthorityBlocked(profileId, out var reason)) return new(false, "SharedWorldAuthorityBlocked", reason);
            if (data.Recovery.LifecycleBlocked) return new(false, "DataRecoveryRequired", "Review this PC's recovery notice before enabling shared hosting.");
            if (data.HasProtected(SetupRestorePendingName(profileId)))
                return new(false, "SetupRestoreRecoveryRequired", "Finish the interrupted setup restore first.");
            if (profile.Kind == GameKinds.Custom || !games.TryGet(profile.Kind, out var driver) || !driver.SupportsBackups)
                return new(false, "SharingUnsupported", "Shared hosting needs a supported built-in game with backups.");
            var next = System.Text.Json.JsonSerializer.Deserialize<HostSettings>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings)) ??
                throw new InvalidDataException("The current Host settings could not be copied.");
            var changed = next.Profiles.Single(item => item.Id == profileId);
            changed.Backups ??= new();
            changed.Backups.Enabled = true;
            changed.SharedSavesEnabled = true;
            cancellationToken.ThrowIfCancellationRequested();
            var saved = UpdateSettingsLocked(next);
            return new(saved.Ok, saved.Code, saved.Ok ?
                "Shared hosting is on. Backups after safe Stop are on too. Choose which friends can host; each friend still approves keeping a save." : saved.Message);
        }
        finally { gate.Release(); }
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
        ServerProfile? existing, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (existing is not null)
            {
                var owned = runs.Any(run => run.ProfileId == existing.Id && Identity(run) == "Matched");
                var available = owned || games.TryGet(existing.Kind, out var existingDriver) &&
                    games.PortsAvailableForStart(existingDriver.Ports(existing));
                return new(existing.GamePort, settings.CompanionPort,
                    existing.Factorio?.RconPort, available);
            }
            var first = version.PortableSetup.GamePort;
            if (first is < 1024 or > 65534) first = 2456;
            if (!games.TryGet(version.Game, out var driver))
                return new(first, settings.CompanionPort, null, false);
            var reserved = new HashSet<int> { settings.CompanionPort };
            foreach (var saved in settings.Profiles)
                if (games.TryGet(saved.Kind, out var savedDriver))
                    foreach (var port in savedDriver.Ports(saved)) reserved.Add(port.Port);
            // A bounded search uses every port declared by the actual driver,
            // including Valheim's query and Factorio's private RCON port.
            for (var attempt = 0; attempt < 256; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gamePort = 1024 + (first - 1024 + attempt * 2) % (65534 - 1024);
                var rconPort = 27015 + attempt;
                if (version.Game == GameKinds.Factorio && (rconPort == gamePort || rconPort == settings.CompanionPort)) continue;
                var candidate = new ServerProfile
                {
                    Kind = version.Game,
                    GamePort = gamePort,
                    Factorio = version.Game == GameKinds.Factorio ? new() { RconPort = rconPort } : null
                };
                var ports = driver.Ports(candidate);
                if (ports.Any(port => reserved.Contains(port.Port)) ||
                    !games.PortsAvailableForStart(ports)) continue;
                return new(gamePort, settings.CompanionPort,
                    candidate.Factorio?.RconPort, true);
            }
            return new(first, settings.CompanionPort, null, false);
        }
        finally { gate.Release(); }
    }
}
