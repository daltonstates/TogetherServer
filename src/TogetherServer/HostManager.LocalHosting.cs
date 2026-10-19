namespace TogetherServer;

internal sealed record LocalHostingPortPlan(int GamePort, int ControlPort,
    int? FactorioRconPort, bool Available);

public sealed partial class HostManager
{
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
                    Kind = version.Game, GamePort = gamePort,
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
