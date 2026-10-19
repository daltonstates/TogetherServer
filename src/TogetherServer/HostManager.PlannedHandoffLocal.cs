using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record RecordedPlannedHostingStatus(bool Restored, bool ReadyForManualStart);

public sealed partial class HostManager
{
    internal async Task<RecordedPlannedHostingStatus> RecordedPlannedHostingStatusAsync(Guid profileId,
        TakeoverLocalSetup? setup, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var bytes = data.LoadProtected(SuccessorRestoreName(profileId));
            var state = bytes is { Length: > 0 and <= 16384 } ? JsonSerializer.Deserialize<SuccessorRestoreState>(bytes) : null;
            var record = authority.LocalAuthorizedHead(profileId);
            var profile = settings.Profiles.SingleOrDefault(item => item.Id == profileId);
            var valid = state is { Schema: 1 } && record is not null && profile is not null &&
                record.RecordHash == state.RecordHash && record.Version.VersionHash == state.VersionHash &&
                record.Proposal.GroupId == state.GroupId && profile.WorldDirectory == state.WorldDirectory &&
                profile.Kind == record.Version.Game && profile.WorldId == record.Version.WorldId &&
                !authority.GovernanceUnresolved(profileId);
            if (!valid) return new(false, false);
            var member = record!.Roster.Members.SingleOrDefault(item => item.PublicKey ==
                WorldAuthorityTrust.CandidateDevicePublicKey(record.Proposal));
            var current = member is { Revoked: false, Grants: { Receive: true, EligibleHost: true } } &&
                (member.AccessExpiresUtc is not { } expires || expires > clock.GetUtcNow());
            var routeBytes = data.LoadProtected(SuccessorRouteName(profileId));
            var observed = routeBytes is { Length: > 0 and <= 16384 } ? JsonSerializer.Deserialize<SuccessorRouteObservation>(routeBytes) : null;
            using var certificate = new HostIdentity(data).Load();
            var now = clock.GetUtcNow();
            var currentRoute = observed is { Schema: 1, Confirmed: true } && observed.RecordHash == record.RecordHash &&
                observed.ObservedUtc <= now && now - observed.ObservedUtc <= TimeSpan.FromHours(1) &&
                certificate is { HasPrivateKey: true } && now >= certificate.NotBefore.ToUniversalTime() &&
                now <= certificate.NotAfter.ToUniversalTime() && observed.TlsFingerprint == HostIdentity.Fingerprint(certificate) &&
                SharedWorldRouteTrust.VerifyChallenge(observed.Challenge, record, observed.ObservedUtc);
            return new(true, current && currentRoute && state!.Ready && setup is not null && !data.Recovery.LifecycleBlocked &&
                !runs.Any(run => run.ProfileId == profileId) && settings.CompanionListeningEnabled &&
                settings.CompanionEndpoint == record.Proposal.CandidateAddress &&
                state.SetupHash == SetupHash(profile!, setup, record));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException or UnauthorizedAccessException)
        { return new(false, false); }
        finally { gate.Release(); }
    }

    internal bool HasPendingHandoffPreparation(HostSettings proposed)
    {
        if (!proposed.CompanionListeningEnabled || !SharedWorldRouteTrust.DirectIpAddress(proposed.CompanionEndpoint)) return false;
        try
        {
            foreach (var path in Directory.EnumerateFiles(data.RootPath, "planned-local-hosting-*.protected").Take(129))
            {
                var bytes = data.LoadProtected(Path.GetFileName(path));
                var state = bytes is { Length: > 0 and <= 262144 } ? JsonSerializer.Deserialize<PlannedLocalHostingState>(bytes) : null;
                if (state is { Schema: 1 } && state.RequestId != Guid.Empty && state.ProfileId != Guid.Empty &&
                    state.Setup?.ControlPort == proposed.CompanionPort && SharedWorldPeerTrust.ValidHash(state.SetupIdentity)) return true;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException or UnauthorizedAccessException)
        { /* Unverifiable preparation cannot keep a listener available. */ }
        return false;
    }

    internal async Task<string?> PlannedLocalGameIssueAsync(SharedWorldVersion version,
        PlannedLocalHostingState state, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (data.Recovery.LifecycleBlocked || runs.Any(run => run.ProfileId == version.ProfileId) ||
                state.ProfileId != version.ProfileId || state.Setup.ControlPort != settings.CompanionPort)
                return "Resolve this PC's recovery or existing managed world before moving hosting.";
            if (version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock &&
                MinecraftPreparedRoot.Check(data.RootPath, SuccessorWorldRoot(data, version),
                    state.PreparedServerRoot, version, state.Setup, state.ExecutablePath) is { } issue) return issue;
            var profile = new ServerProfile
            {
                Id = version.ProfileId,
                Kind = version.Game,
                WorldId = version.WorldId,
                WorldDirectory = SuccessorWorldRoot(data, version),
                GamePort = state.Setup.GamePort,
                ExecutablePath = state.ExecutablePath,
                Crossplay = version.PortableSetup.Crossplay,
                Minecraft = version.Game == GameKinds.MinecraftJava ? new() { ServerJarPath = state.Setup.ServerFile! } : null,
                Factorio = version.Game == GameKinds.Factorio ? new() { RconPort = state.FactorioRconPort ?? 0 } : null
            };
            if (!games.TryGet(version.Game, out var driver) || !games.PortsAvailableForStart(driver.Ports(profile)))
                return "The complete local game-port plan is unavailable. Review this PC's running servers.";
            if (version.Game == GameKinds.Factorio && state.FactorioRconPort is not >= 1024 or > 65535)
                return "Select a separate local Factorio RCON port.";
            if ((version.PortableSetup.AddOns?.Count ?? 0) > 0)
            {
                // World-contained packs/mods are verified again after the final
                // signed copy is restored. Require an already matching local set
                // for the initial move preflight; it cannot fabricate installed state.
                var candidate = settings.Profiles.FirstOrDefault(item => item.Kind == version.Game &&
                    item.ExecutablePath == state.ExecutablePath);
                var installed = candidate is null ? null : ServerAddOns.List(data, candidate);
                if (installed?.Ok != true || version.PortableSetup.AddOns!.Any(required =>
                    !installed.Items.Any(item => item.Enabled && item.Name == required.Name && item.Version == required.Version &&
                        item.RequiredGameVersion == required.RequiredGameVersion && item.Type == required.Type &&
                        (required.Id is null || item.Key.EndsWith(required.Id, StringComparison.OrdinalIgnoreCase)))))
                    return "Use normal Host setup to install and enable this save's matching add-ons, then retry preparation.";
            }
            return null;
        }
        finally { gate.Release(); }
    }
}
