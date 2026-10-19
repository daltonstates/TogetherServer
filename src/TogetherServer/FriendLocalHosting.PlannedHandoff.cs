using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed record PlannedLocalHostingState(int Schema, Guid RequestId, Guid ProfileId,
    string SetupIdentity, TakeoverLocalSetup Setup, string ExecutablePath,
    string? PreparedServerRoot, int? FactorioRconPort, string? GamePassword,
    bool Prepared = false);

internal sealed partial class FriendLocalHostingService
{
    private static string PlannedLocalName(Guid profileId) => $"planned-local-hosting-{profileId:N}.protected";
    internal static string PortableSetupIdentity(string game, string worldId, SharedWorldPortableSetup portableSetup) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Game = game, WorldId = worldId, PortableSetup = portableSetup })));
    private static string PortableSetupIdentity(SharedWorldVersion version) => PortableSetupIdentity(version.Game, version.WorldId, version.PortableSetup);

    private static SharedWorldVersion? PreflightMetadata(Guid profileId, PlannedHandoffStatus request) => request.Source is { } source &&
        SharedWorldPortableSetupReader.Valid(source.Game, source.PortableSetup) &&
        source.SetupIdentity == PortableSetupIdentity(source.Game, source.WorldId, source.PortableSetup) ?
        new(4, Guid.Empty, 0, null, profileId, source.Game, source.WorldId, DateTimeOffset.MinValue,
            "SetupMetadataOnly", Guid.Empty, source.PortableSetup, [], "", "", "") : null;

    private PlannedLocalHostingState? ReadAnyPlannedLocal(Guid profileId)
    {
        var bytes = data.LoadProtected(PlannedLocalName(profileId));
        var state = bytes is { Length: > 0 and <= 262144 } ? JsonSerializer.Deserialize<PlannedLocalHostingState>(bytes) : null;
        return state is { Schema: 1 } && state.ProfileId == profileId && state.RequestId != Guid.Empty &&
            SharedWorldPeerTrust.ValidHash(state.SetupIdentity) && state.Setup is not null ? state : null;
    }

    private PlannedLocalHostingState? ReadPlannedLocal(Guid profileId, Guid requestId) =>
        ReadAnyPlannedLocal(profileId) is { } state && state.RequestId == requestId ? state : null;

    private async Task<PlannedHandoffStatus?> LocalCompletedHandoffAsync(FriendService.LocalHostingConnection connection,
        Guid profileId, CancellationToken cancellationToken)
    {
        var state = ReadAnyPlannedLocal(profileId);
        if (state is null) return null;
        var record = new WorldAuthorityStore(data).LocalAuthorizedHead(profileId);
        if (record?.Proposal.Kind != "Planned" || record.SuccessorReceipt is null ||
            state.SetupIdentity != PortableSetupIdentity(record.Version) ||
            !await connection.Link.MatchesPlannedAuthorityAsync(record, cancellationToken)) return null;
        return new(false, "OldHostFenced", "This PC retains the signed hosting move. Its existing local server and manual Start checks remain available while the old Host is offline.",
            record.SuccessorReceipt.DeviceId, record.Version.Number, record.Version.VersionHash, true,
            RequestId: state.RequestId, RecipientState: "Completed", PreparedVersionHash: record.Version.VersionHash,
            Source: new(record.Version.Game, record.Version.WorldId, record.Version.PortableSetup, state.SetupIdentity));
    }

    private async Task<LocalHostingStatus> PlannedStatusAsync(FriendService.LocalHostingConnection connection,
        Guid profileId, PlannedHandoffStatus request, CancellationToken cancellationToken)
    {
        var access = await connection.Link.LocalHostingAccessAsync(profileId, false, cancellationToken);
        var snapshot = await manager.CompanionSnapshotAsync();
        var local = ReadPlannedLocal(profileId, request.RequestId!.Value);
        var restored = request.RecipientState == "Completed" ?
            await manager.RecordedPlannedHostingStatusAsync(profileId, local?.Setup, cancellationToken) : null;
        var running = snapshot.Runs.Any(run => run.ProfileId == profileId && run.ProcessId is not null);
        var ready = access.Allowed && access.Consented && local?.Prepared == true && restored?.ReadyForManualStart == true && !running;
        var version = access.Version ?? PreflightMetadata(profileId, request);
        var ports = version is null ? new LocalHostingPortPlan(2456, snapshot.Settings.CompanionPort, null, false) :
            await manager.LocalHostingPortsAsync(version, snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == profileId), cancellationToken);
        var server = local?.Setup.ServerFile ?? (version is null ? null : await Task.Run(() => FindServer(version, snapshot.Settings.Profiles, null), cancellationToken));
        var current = routeCheck is null ? null : await routeCheck.CurrentAsync(cancellationToken);
        var connected = current?.Identity is { Enabled: true, Active: true, ListenerOwned: true } &&
            SharedWorldPeerTrust.ValidRoute(new(current.Identity.Endpoint, current.Identity.CertificateFingerprint ?? ""));
        var prepared = request.RecipientState == "Prepared" && local?.Prepared == true &&
            (!request.Pending || request.PreparedVersionHash == access.Version?.VersionHash);
        var state = Unavailable(request.Code, request.Message) with
        {
            Ok = ready || running || prepared, Allowed = access.Allowed, Consented = access.Consented,
            Name = access.Name, Game = version?.Game ?? access.Game, GameVersion = version?.PortableSetup.GameVersion,
            NeedsServerFile = server is null, ServerFile = server,
            GamePort = local?.Setup.GamePort ?? ports.GamePort, ControlPort = snapshot.Settings.CompanionPort,
            LocalProfileId = restored?.Restored == true ? profileId : null,
            ReadyForManualStart = ready, Running = running, HandoffRequest = request,
            Steps = [new("permission", access.Allowed ? "Ready" : "ActionRequired", access.Message),
                new("server", local?.Prepared == true ? "Ready" : "ActionRequired", local?.Prepared == true ?
                    "This PC's installed game setup passed preparation. Current files are checked again before restore and Start." :
                    server is null ? "Install or select the matching dedicated server on this PC, then prepare again." :
                    "The matching server is available. Prepare this PC to check its game-specific setup."),
                new("save", prepared && request.PreparedVersionHash is not null || restored?.Restored == true ? "Ready" : "ActionRequired",
                    restored?.Restored == true ? "The final signed save is restored in fresh managed storage." :
                    request.Pending ? "Receive and prepare the exact final post-Stop save. Existing worlds stay intact." :
                    "Preparation verifies the available signed save before the current Host stops."),
                new("ports", ports.Available || running ? "Ready" : "ActionRequired", $"Local game port {local?.Setup.GamePort ?? ports.GamePort}; Start repeats the driver port checks."),
                new("connection", connected ? "Ready" : "ActionRequired", connected ?
                    "This PC's deliberate HTTPS listener is ready to share its signed address. The old Host verifies a pinned round trip after restore; game joins remain separate." :
                    "Configure and deliberately enable this PC's Friend connection before hosting moves.")]
        };
        return friend.CurrentLocalHostingConnection(connection) ? state :
            Unavailable("ConnectionChanged", "The selected Host changed. Review the hosting request again.");
    }

    private async Task<LocalHostingStatus> PreparePlannedAsync(FriendService.LocalHostingConnection connection,
        Guid profileId, LocalHostingPrepareRequest input, CancellationToken cancellationToken)
    {
        var link = connection.Link;
        var request = await link.HandoffRequestAsync(profileId, cancellationToken) ??
            await LocalCompletedHandoffAsync(connection, profileId, cancellationToken);
        if (request?.RequestId != input.RequestId || request is null ||
            request.RecipientState is not ("Accepted" or "Prepared" or "Completed"))
            return Unavailable("HostingRequestConfirmationRequired", "Accept the exact current hosting request before preparing this PC.");
        async Task<LocalHostingStatus> Result(string code, string message, bool ok = false)
        {
            var current = await link.HandoffRequestAsync(profileId, cancellationToken) ?? request;
            return (await PlannedStatusAsync(connection, profileId, current, cancellationToken)) with
                { Ok = ok, Code = code, Message = message, ReadyForManualStart = false };
        }
        try
        {
            if (request.RecipientState != "Completed")
            {
                var beginning = await link.BeginHandoffPreparationAsync(profileId, request.RequestId!.Value, cancellationToken);
                if (!beginning.Ok) return await Result(beginning.Code, beginning.Message);
            }
            if (ReadPlannedLocal(profileId, request.RequestId!.Value) is { } previous)
                data.SaveProtected(PlannedLocalName(profileId), JsonSerializer.SerializeToUtf8Bytes(previous with { Prepared = false }));
            var access = await link.LocalHostingAccessAsync(profileId, true, cancellationToken);
            if (!access.Allowed || !friend.CurrentLocalHostingConnection(connection))
                return await Result("HostingPermissionChanged", "The selected connection or hosting permission changed.");
            if (!access.Consented)
            {
                if (!input.AllowReceiving) return await Result("LocalConsentRequired", "Allow this PC to keep the shared save before preparing hosting.");
                var consent = await link.SetSharedWorldConsentAsync(profileId, true);
                if (!consent.Ok) return await Result(consent.Code, consent.Message);
            }
            if (request.RecipientState != "Completed")
            {
                var pulled = await link.PullSharedWorldAsync(profileId, cancellationToken);
                if (!pulled.Ok && request.Pending) return await Result(pulled.Code, pulled.Message);
            }
            access = await link.LocalHostingAccessAsync(profileId, false, cancellationToken, verifyFiles: true);
            var initial = !request.Pending && request.RecipientState != "Completed";
            var version = initial ? PreflightMetadata(profileId, request) ?? access.Version : access.Version;
            var metadataOnly = version?.CaptureKind == "SetupMetadataOnly" && initial;
            if (!access.Allowed || version is null || !metadataOnly && !access.SaveVerified)
                return await Result("SharedSaveRequired", "Receive a complete verified save from the current Host, then prepare again.");
            if (request.Pending && request.FinalVersionHash != version.VersionHash)
                return await Result("WaitingForSuccessorCopy", "The exact final post-Stop save is still being received.");
            var snapshot = await manager.CompanionSnapshotAsync();
            var local = ReadPlannedLocal(profileId, request.RequestId!.Value);
            if (local is not null && local.SetupIdentity != PortableSetupIdentity(version))
                return await Result("LocalServerSetupRequired", "The shared game's required setup changed. Review this PC's matching installation before the move continues.");
            if (local is null)
            {
                if (snapshot.Settings.Profiles.Any(item => item.Id == profileId))
                    return await Result("LocalHostingReviewRequired", "This world already has a local profile. Existing progress was kept; review it before moving hosting.");
                var server = await Task.Run(() => FindServer(version, snapshot.Settings.Profiles, input.ServerFile), cancellationToken);
                if (server is null) return await Result("InstalledServerRequired", "Install or select the matching dedicated server, then prepare again. No game software was downloaded.");
                var ports = await manager.LocalHostingPortsAsync(version, null, cancellationToken);
                if (!ports.Available) return await Result("LocalPortsRequired", "The game's complete local port plan needs attention.");
                var password = version.Game == GameKinds.Valheim ? input.GamePassword ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) : null;
                var setup = new TakeoverLocalSetup(server, version.PortableSetup.GameVersion,
                    version.PortableSetup.AddOns, true, ports.ControlPort, ports.GamePort);
                var executable = server;
                string? preparedRoot = null;
                if (version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock)
                {
                    if (!input.AcceptedTerms) return await Result("MinecraftTermsRequired", "Review and accept Minecraft's terms on this PC before preparing its server installation.");
                    var minecraft = PlannedHandoffMinecraftSetup.Prepare(data, version, request.RequestId.Value,
                        server, ports.GamePort, snapshot.Settings.Profiles);
                    setup = setup with { ServerFile = minecraft.ArtifactPath };
                    executable = minecraft.ExecutablePath;
                    preparedRoot = minecraft.ServerDirectory;
                }
                local = new(1, request.RequestId.Value, profileId, PortableSetupIdentity(version), setup,
                    executable, preparedRoot, ports.FactorioRconPort, password);
                data.SaveProtected(PlannedLocalName(profileId), JsonSerializer.SerializeToUtf8Bytes(local));
            }
            var currentServer = await Task.Run(() => FindServer(version, snapshot.Settings.Profiles,
                input.ServerFile ?? local.Setup.ServerFile), cancellationToken);
            if (currentServer is null || version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock &&
                !string.Equals(currentServer, local.Setup.ServerFile, StringComparison.OrdinalIgnoreCase))
                return await Result("InstalledServerRequired", "The prepared game server changed or is missing. Select the exact matching installed server and prepare again.");
            if (version.Game is not (GameKinds.MinecraftJava or GameKinds.MinecraftBedrock) &&
                !string.Equals(currentServer, local.Setup.ServerFile, StringComparison.OrdinalIgnoreCase))
            {
                local = local with { Setup = local.Setup with { ServerFile = currentServer }, ExecutablePath = currentServer };
                data.SaveProtected(PlannedLocalName(profileId), JsonSerializer.SerializeToUtf8Bytes(local));
            }
            if (synchronizeListener is not null) await synchronizeListener();
            var route = routeCheck is null ? null : await routeCheck.CurrentAsync(cancellationToken);
            if (route?.Identity is not { Enabled: true, Active: true, ListenerOwned: true } identity ||
                !SharedWorldPeerTrust.ValidRoute(new(identity.Endpoint, identity.CertificateFingerprint ?? "")))
                return await Result("RecipientConnectionRequired", "Configure and deliberately enable this PC's incoming Friend connection, then retry preparation. No network setting was changed.");
            if (!metadataOnly)
            {
                var preflight = await link.CheckHandoffLocalSetupAsync(profileId, local.Setup, true, cancellationToken);
                if (!preflight.Ready) return await Result("LocalServerSetupRequired", string.Join(" ", preflight.Reasons));
            }
            else if (!SharedWorldReadiness.HasSpaceForCopies(data.RootPath, 0, 3))
                return await Result("LocalServerSetupRequired", "Keep at least 1 GiB free. The exact final save's space and file checks run after the source Host stops.");
            var gameIssue = await manager.PlannedLocalGameIssueAsync(version, local, cancellationToken);
            if (gameIssue is not null) return await Result("LocalServerSetupRequired", gameIssue);
            if (!friend.CurrentLocalHostingConnection(connection)) return await Result("ConnectionChanged", "The selected Host changed. The prepared setup was kept.");
            local = local with { Prepared = true };
            data.SaveProtected(PlannedLocalName(profileId), JsonSerializer.SerializeToUtf8Bytes(local));
            if (request.RecipientState != "Completed")
            {
                var confirmed = await link.ConfirmHandoffLocalPreparationAsync(profileId, request.RequestId.Value,
                    metadataOnly ? access.Version?.VersionHash : version.VersionHash, local.SetupIdentity,
                    new(identity.Endpoint, identity.CertificateFingerprint!), cancellationToken);
                return confirmed.Ok ? await PlannedStatusAsync(connection, profileId,
                    await link.HandoffRequestAsync(profileId, cancellationToken) ?? request, cancellationToken) :
                    await Result(confirmed.Code, confirmed.Message);
            }
            var staged = await link.StagePlannedHandoffAsync(profileId, cancellationToken);
            if (!staged.Ok) return await Result(staged.Code, staged.Message);
            var record = new WorldAuthorityStore(data).ReadUniqueHead(profileId);
            if (record is null) return await Result("HandoffProofPending", "The signed final hosting decision is still being verified.");
            var restored = await manager.RestoreSharedSuccessorAsync(profileId,
                new(record.RecordHash, local.Setup, access.Name, access.Name, local.GamePassword,
                    local.ExecutablePath, local.PreparedServerRoot, local.FactorioRconPort), cancellationToken);
            if (!restored.Ok) return await Result(restored.Code, restored.Message);
            // The original owner is a distinct authenticated observer. It uses
            // the saved signed certificate pin, never a typed address.
            var observed = await link.RequestOwnerHandoffRouteCheckAsync(profileId, request.RequestId.Value, cancellationToken);
            if (!observed.Ok) return await Result(observed.Code, observed.Message);
            var finished = await manager.FinishSharedSuccessorAsync(profileId,
                new(record.RecordHash, local.Setup, local.ExecutablePath, local.PreparedServerRoot));
            return finished.Ok ? await PlannedStatusAsync(connection, profileId, request, cancellationToken) :
                await Result(finished.Code, finished.Message);
        }
        catch (Exception error) when (ExpectedFailure(error))
        { return await Result("LocalHostingReviewRequired", "Preparation stopped safely. Existing saves were kept; review the checks and retry."); }
    }
}
