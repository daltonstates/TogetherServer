using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record LocalHostingStep(string Key, string State, string Message);
public sealed record LocalHostingRemedy(string Kind, string Title, string Message);
public sealed record LocalHostingPermission(bool Allowed, string Code, string Message);
public sealed record LocalHostingStatus(bool Ok, string Code, string Message,
    bool Allowed, bool Consented, string? Game, string Name, bool NeedsServerFile,
    bool NeedsPassword, string? ServerFile, string? GameVersion, int GamePort, int ControlPort,
    Guid? LocalProfileId, string? BranchHash, bool ReadyForManualStart, bool Running,
    IReadOnlyList<LocalHostingStep> Steps, PlannedHandoffStatus? HandoffRequest = null,
    LocalHostingRemedy? Remedy = null);
public sealed record LocalHostingPrepareRequest(bool AllowReceiving, bool AcceptSplitWarning,
    string? ServerFile = null, string? GamePassword = null, Guid? RequestId = null,
    bool AcceptedTerms = false, bool RequirePeerConnection = false);

// Preparation composes the existing signed-grant, receive, fresh restore and
// Finish gates. Only the explicit Start action launches; reads never do.
internal sealed partial class FriendLocalHostingService(LocalData data, FriendService friend, HostManager manager,
    Func<string, IReadOnlyList<string>>? discoverServers = null, LocalHostingRouteCheck? routeCheck = null,
    Func<Task>? synchronizeListener = null,
    Func<SharedWorldPeerRoot, CancellationToken, Task<WorldPeerActionResult>>? announceHosting = null,
    Func<SharedWorldPeerRoot, CancellationToken, Task<WorldPeerLatestSourceResult>>? latestSource = null)
{
    private static bool ExpectedFailure(Exception error) => error is IOException or InvalidDataException or
        JsonException or CryptographicException or UnauthorizedAccessException or ArgumentException;

    internal static bool ConfirmedHostingDenial(string code) => code is
        "HostingPermissionRequired" or "PermissionDenied" or "HostingNotAllowed" or
        "Revoked" or "AccessExpired" or "ApprovalPending";

    private static string HostingPermissionState(bool allowed, string code) =>
        allowed ? "Ready" : ConfirmedHostingDenial(code) ? "ActionRequired" : "Unknown";

    internal async Task<LocalHostingPermission> PermissionAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        using var connection = friend.RetainLocalHostingConnection();
        if (connection is null) return new(false, "NotPaired", "Choose a saved Host connection first.");
        var access = await connection.Link.LocalHostingAccessAsync(profileId, true, cancellationToken,
            permissionOnly: true);
        cancellationToken.ThrowIfCancellationRequested();
        return friend.CurrentLocalHostingConnection(connection)
            ? new(access.Allowed, access.Code, access.Message)
            : new(false, "ConnectionChanged", "The selected Host changed. Review the shared server again.");
    }

    internal static bool CurrentSignedControlRoute(bool verified, WorldSeparateCopyBranch? branch,
        LocalHostingRouteEvidence? current) => verified && branch is not null &&
        current?.Identity is
        {
            Enabled: true, Active: true, ListenerOwned: true,
            ListenerState: CompanionListenerStates.Listening
        } identity &&
        identity.Endpoint.TrimEnd('/') == branch.Offer.Proposal.CandidateAddress.TrimEnd('/') &&
        string.Equals(identity.CertificateFingerprint, branch.Offer.CandidateTlsFingerprint, StringComparison.OrdinalIgnoreCase);

    internal static LocalHostingPrepareRequest ParseRequest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 8192) throw new InvalidDataException("The preparation request is invalid.");
        using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Use a fixed preparation request.");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (!fields.Add(field.Name) || field.Name is not ("allowReceiving" or "acceptSplitWarning" or "serverFile" or "gamePassword" or "requestId" or "acceptedTerms" or "requirePeerConnection"))
                throw new InvalidDataException("The preparation request contains unexpected fields.");
            if (field.Name is "allowReceiving" or "acceptSplitWarning" or "acceptedTerms" or "requirePeerConnection")
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("The preparation choices must be explicit.");
            }
            else if (field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("The local setup field is invalid.");
        }
        if (!fields.Contains("allowReceiving") || !fields.Contains("acceptSplitWarning"))
            throw new InvalidDataException("The preparation choices are required.");
        string? Optional(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var path = Optional("serverFile");
        var password = Optional("gamePassword");
        if (path is not null && (path.Length is 0 or > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)) ||
            password is not null && (password.Length is < 5 or > 64 || password.Any(char.IsControl)))
            throw new InvalidDataException("The local setup field is invalid.");
        var requestText = Optional("requestId");
        Guid? requestId = requestText is null ? null : Guid.TryParse(requestText, out var parsed) && parsed != Guid.Empty ? parsed :
            throw new InvalidDataException("Choose the exact hosting request.");
        return new(root.GetProperty("allowReceiving").GetBoolean(), root.GetProperty("acceptSplitWarning").GetBoolean(), path, password,
            requestId, root.TryGetProperty("acceptedTerms", out var terms) && terms.GetBoolean(),
            root.TryGetProperty("requirePeerConnection", out var peerConnection) && peerConnection.GetBoolean());
    }

    internal async Task<LocalHostingStatus> StatusAsync(Guid profileId,
        CancellationToken cancellationToken = default, string? selectedFile = null)
    {
        using var connection = friend.RetainLocalHostingConnection();
        if (connection is null) return Unavailable("NotPaired", "Choose a saved Host connection first.");
        return await StatusForRetainedConnectionAsync(connection, profileId, cancellationToken, selectedFile);
    }

    private async Task<LocalHostingStatus> StatusForRetainedConnectionAsync(FriendService.LocalHostingConnection connection,
        Guid profileId, CancellationToken cancellationToken, string? selectedFile = null,
        LocalHostingAccess? checkedAccess = null)
    {
        var access = checkedAccess ?? await connection.Link.LocalHostingAccessAsync(profileId, true, cancellationToken,
            refreshDisconnected: true);
        var handoff = await connection.Link.HandoffRequestAsync(profileId, cancellationToken) ??
            await LocalCompletedHandoffAsync(connection, profileId, cancellationToken);
        return handoff?.RequestId is not null && handoff.RecipientState is "Requested" or "Accepted" or "Prepared" or "Completed" ?
            await PlannedStatusAsync(connection, profileId, handoff, cancellationToken, access) :
            (await StatusForConnectionAsync(connection, profileId, cancellationToken, selectedFile, access)) with { HandoffRequest = handoff };
    }

    private async Task<LocalHostingStatus> StatusForConnectionAsync(FriendService.LocalHostingConnection connection,
        Guid profileId, CancellationToken cancellationToken, string? selectedFile = null,
        LocalHostingAccess? checkedAccess = null)
    {
        var access = checkedAccess ?? await connection.Link.LocalHostingAccessAsync(profileId, false, cancellationToken);
        var snapshot = await manager.CompanionSnapshotAsync();
        var branches = connection.Link.SeparateCopyBranches(profileId);
        var restored = snapshot.Settings.Profiles.Where(profile => profile.SeparateCopySourceProfileId == profileId &&
            branches.Any(branch => branch.BranchHash == profile.SeparateCopyBranchHash)).ToArray();
        var runningProfiles = restored.Where(profile => snapshot.Runs.Any(run => run.ProfileId == profile.Id &&
            run.ProcessId is not null)).ToArray();
        var selected = access.Allowed ? SelectedBranch(connection, profileId, access.Root, branches) : null;
        var existing = runningProfiles.Length == 1 ? runningProfiles[0] : selected is not null ?
            restored.SingleOrDefault(profile => profile.SeparateCopyBranchHash == selected.BranchHash) :
            restored.Length == 1 ? restored[0] : null;
        var choices = existing is null && restored.Length == 0 ? branches.Where(branch =>
            branch.Offer.Version.VersionHash == access.Version?.VersionHash).ToArray() : [];
        var branch = existing is not null ? branches.Single(item => item.BranchHash == existing.SeparateCopyBranchHash) :
            selected ?? (choices.Length == 1 ? choices[0] : null);
        var ambiguous = runningProfiles.Length > 1 || selected is null &&
            (restored.Length > 1 && existing is null || choices.Length > 1);
        var version = branch?.Offer.Version ?? access.Version;
        var game = version?.Game ?? access.Game;
        var gameVersion = version?.PortableSetup.GameVersion;
        var serverFile = existing is not null ? existing.Kind == GameKinds.MinecraftJava ?
            existing.Minecraft?.ServerJarPath : existing.ExecutablePath :
            version is null ? selectedFile : await Task.Run(() => FindServer(version, snapshot.Settings.Profiles, selectedFile), cancellationToken);
        if (serverFile is not null && (!File.Exists(serverFile) || !ManagedImportFiles.PlainFile(serverFile))) serverFile = null;
        var manualSetup = existing is not null && version is not null && !InstalledAddOnsMatch(existing, version);
        var ports = version is null ? new LocalHostingPortPlan(2456, snapshot.Settings.CompanionPort, null, false) :
            await manager.LocalHostingPortsAsync(version, existing, cancellationToken);
        var hosted = branch is null ? null : await manager.RecordedLocalHostingStatusAsync(profileId,
            branch.BranchHash, cancellationToken);
        var running = hosted?.Running == true;
        var saveReady = hosted is { Restored: true, ReviewRequired: false } && (hosted.ReadyForManualStart || running);
        var ready = access.Allowed && access.Consented && !ambiguous && !running &&
            saveReady && ports.Available && serverFile is not null && !manualSetup &&
            hosted is { ReadyForManualStart: true };
        var endpointValid = HostIdentity.TryEndpoint(snapshot.Settings.CompanionEndpoint, out var endpoint);
        var localOnly = endpointValid && System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(endpoint.Host));
        var currentRoute = routeCheck is null ? null : await routeCheck.CurrentAsync(cancellationToken);
        var connectionStep = CurrentSignedControlRoute(hosted?.ControlRouteVerified == true, branch, currentRoute) ? new LocalHostingStep("connection", "Ready",
            "Another approved PC verified this local history's pinned HTTPS control route. A real game join remains separate.") :
            currentRoute is not null ? currentRoute.Step :
            new LocalHostingStep("connection", "Unknown", !endpointValid || localOnly ?
                "Hosting can be prepared for this PC. Configure this PC's Friend connection before inviting others; no incoming listener is enabled here." :
                "This PC has an address configured. A real incoming connection and game join still need checking; preparation changes no network settings.");
        var serverMessage = manualSetup ?
            "This save needs matching add-ons. Open game setup to install and enable them, then continue the checks." :
            serverFile is null ? "Install or select the matching dedicated server already on this PC. No command-line tool is required for an installed Valheim server." :
            existing is not null ? "The server file passed preparation. Start repeats its current checks." :
            "The matching server file is available on this PC. Start repeats its checks.";
        var code = ambiguous ? "HostingHistoryChoiceRequired" : !access.Allowed ? access.Code :
            hosted?.ReviewRequired == true ? "LocalHostingReviewRequired" : ready ? "LocalHostingPrepared" :
            running ? "LocalHostingRunning" : manualSetup ? "LocalServerSetupRequired" :
            !access.Consented ? "LocalConsentRequired" : !saveReady ? hosted?.Restored == true ?
                "LocalHostingPreparationRequired" : "SharedSaveRequired" :
            serverFile is null ? "InstalledServerRequired" : !ports.Available ? "LocalPortsRequired" : "LocalHostingPreparationRequired";
        var message = ambiguous ? "More than one local history is available. Choose a history in shared-save setup; every save is preserved." :
            !access.Allowed ? access.Message : hosted?.ReviewRequired == true ? hosted.Message :
            running ? "This shared save is already hosted on this PC. Its local world is preserved." :
            ready ? "This PC is prepared. Current save, game and port checks run again when you choose Host on this PC." :
            manualSetup ? serverMessage : "Prepare this PC to receive a verified save and finish local hosting checks.";
        var result = new LocalHostingStatus(ready || running, code, message, access.Allowed, access.Consented, game,
            access.Name, version is not null && serverFile is null && !manualSetup, false, serverFile, gameVersion,
            ports.GamePort, ports.ControlPort, existing?.Id ?? (hosted?.Restored == true ? hosted.LocalProfileId : null), branch?.BranchHash,
            ready, running,
            [new("permission", HostingPermissionState(access.Allowed, access.Code), access.Message),
             new("server", serverFile is not null && !manualSetup ? "Ready" : "ActionRequired", serverMessage),
             new("save", saveReady ? "Ready" : "ActionRequired", ambiguous ? "Choose an exact shared history before continuing." :
                saveReady ? "Preparation verified this retained signed save. Start repeats current checks; existing local progress is preserved." :
                hosted?.Restored == true ? "The existing local world is preserved. Prepare again to repeat its verification checks." :
                !access.Consented ? "Prepare can allow shared saves on this PC after your consent." : "Receive a complete verified save from an approved reachable PC."),
             new("ports", ports.Available ? "Ready" : "ActionRequired", running ?
                $"This PC's running server uses game port {ports.GamePort}." : ports.Available ?
                $"Local game port {ports.GamePort} is selected; Start repeats every declared game-port check." : "Local game ports need attention before preparation."),
             connectionStep]);
        return friend.CurrentLocalHostingConnection(connection) ? result :
            Unavailable("ConnectionChanged", "The selected Host changed. Review the shared server again.");
    }

    internal static LocalHostingStatus Unavailable(string code, string message) => new(false, code, message,
        false, false, null, "Shared server", false, false, null, null, 2456, 5131, null, null, false, false,
        [new("permission", HostingPermissionState(false, code), message), new("server", "Unknown", "Check this PC's server setup."),
         new("save", "Unknown", "No save was selected."), new("ports", "Unknown", "Local ports have not been checked."),
         new("connection", "Unknown", "Incoming connections and game joins have not been checked.")]);

    internal async Task<LocalHostingStatus> PrepareAsync(Guid profileId, LocalHostingPrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        using var connection = friend.RetainLocalHostingConnection();
        if (connection is null) return Unavailable("NotPaired", "Choose a saved Host connection first.");
        var link = connection.Link;
        if (request.RequestId is not null)
            return await PreparePlannedAsync(connection, profileId, request, cancellationToken);
        if ((await link.HandoffRequestAsync(profileId, cancellationToken)) is { RecipientState: "Requested" or "Accepted" or "Prepared" or "Completed" })
            return Unavailable("HostingRequestConfirmationRequired", "Review and accept the planned hosting request before preparing this PC.");
        bool SameConnection() => friend.CurrentLocalHostingConnection(connection);
        Task<LocalHostingStatus> Status() => StatusForConnectionAsync(connection, profileId, cancellationToken, request.ServerFile);
        async Task<LocalHostingStatus> Result(string code, string message, bool? ok = false)
        {
            var status = await Status();
            return status with
            {
                Ok = ok ?? status.Ok,
                Code = code,
                Message = message,
                ReadyForManualStart = (ok ?? status.Ok) && status.ReadyForManualStart
            };
        }
        try
        {
            var access = await link.LocalHostingAccessAsync(profileId, true, cancellationToken);
            if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. Review the shared server again.");
            if (!access.Allowed) return await Result(access.Code, access.Message);
            if (!access.Consented)
            {
                if (!request.AllowReceiving) return await Result("LocalConsentRequired", "Allow shared saves on this PC to prepare hosting.");
                var consent = await link.SetSharedWorldConsentAsync(profileId, true);
                if (!consent.Ok) return await Result(consent.Code, "Shared-save storage consent could not be saved. Review it before continuing.");
            }
            var status = await Status();
            if (status.Running) return status;
            if (status.Code == "HostingHistoryChoiceRequired" && latestSource is null) return status;
            if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. Review the shared server again.");
            // The explicit network confirmation also lets a paired PC receive
            // an outbound seed before it has its first local hosting branch.
            if (latestSource is not null && access.Root is { } receivingRoot)
            {
                var receiving = await PrepareReceivingRouteAsync(request, receivingRoot, cancellationToken);
                if (receiving is not null) return await Result(receiving.Code, receiving.Message);
                if (!SameConnection()) return await Result("ConnectionChanged", "The selected server changed. Check hosting again.");
            }
            if ((status.LocalProfileId is null || latestSource is not null) && link.View().State is "Connected" or "Disabled")
            {
                var pulled = await link.PullSharedWorldAsync(profileId, cancellationToken);
                if (!pulled.Ok && pulled.Code != "NetworkUnavailable")
                    return await Result(pulled.Code, "The shared save could not be verified. Review shared-save status before hosting.");
            }
            access = await link.LocalHostingAccessAsync(profileId, false, cancellationToken, verifyFiles: status.LocalProfileId is null);
            if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. Review the shared server again.");
            if (!access.Allowed) return await Result(access.Code, access.Message);
            if (!access.Consented) return await Result("LocalConsentRequired", "Allow shared saves on this PC before hosting.");
            status = await Status();
            WorldPeerRecoverySource? selectedSource = null;
            var createBranch = status.BranchHash is null;
            if (latestSource is not null && access.Root is { } root)
            {
                var latest = await latestSource(root, cancellationToken);
                if (!SameConnection()) return await Result("ConnectionChanged", "The selected server changed. Check hosting again.");
                if (!latest.Ok || latest.Source is null)
                {
                    if (status.BranchHash is { } refusedBranch)
                        await manager.InvalidateLocalHostingPreparationAsync(profileId, refusedBranch, cancellationToken);
                    return await Result(latest.Code == "SharedSaveHistoryNeedsReview" ?
                        "HostingHistoryChoiceRequired" : latest.Code, latest.Message);
                }
                selectedSource = latest.Source;
                selectedSource.Verify(data, root);
                await manager.VerifyRetainedContinuationsAsync(profileId, cancellationToken);
                if (status.LocalProfileId is { } currentLocalId)
                {
                    var retained = await manager.VerifiedLocalContinuationAsync(profileId, currentLocalId, cancellationToken);
                    createBranch = retained?.VersionHash != selectedSource.Descriptor.Version.VersionHash;
                    if (!createBranch && status.BranchHash is { } retainedBranch)
                        SelectBranch(connection, root, link.SeparateCopyBranches(profileId).Single(item => item.BranchHash == retainedBranch));
                }
                else createBranch = status.BranchHash is not { } preparedBranch ||
                    link.SeparateCopyBranches(profileId).SingleOrDefault(item => item.BranchHash == preparedBranch)?
                        .Offer.Version.VersionHash != selectedSource.Descriptor.Version.VersionHash;
            }
            if (status.LocalProfileId is null && selectedSource is null && !access.SaveVerified)
                return await Result("SharedSaveRequired", "A complete verified save is not available yet. Keep an approved source reachable and retry.");
            var nextGame = selectedSource?.Descriptor.Version.Game ?? status.Game;
            if ((status.LocalProfileId is null || createBranch) && nextGame is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock && !request.AcceptedTerms)
                return await Result("MinecraftTermsRequired", "Read and accept Minecraft's terms on this PC before preparing its server. No terms were accepted for you.");
            if (!request.AcceptSplitWarning && createBranch)
                return await Result("SplitWarningRequired", "Confirm hosting on this PC. Other players will use its connection, and any different progress stays protected.");
            if (request.RequirePeerConnection)
            {
                if (synchronizeListener is not null) await synchronizeListener();
                cancellationToken.ThrowIfCancellationRequested();
                if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. Review the shared server again.");
                var route = routeCheck is null ? null : await routeCheck.CurrentAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. Review the shared server again.");
                // A first listener stays Idle until this PC has a signed
                // candidate. Require the owner's configured incoming route now;
                // require the actual owned listener after that candidate exists.
                if (route?.Identity is not { } identity ||
                    !SharedWorldRouteTrust.DirectIpAddress(identity.Endpoint) ||
                    !HostIdentity.TryEndpoint(identity.Endpoint, out var endpoint) || endpoint.Port != identity.ControlPort ||
                    System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(endpoint.Host)))
                    return await Result("LocalConnectionSetupRequired", "Set this PC's connection address in Connection help, then continue hosting.");
                if (!identity.Enabled)
                {
                    if (!request.AllowReceiving || !request.AcceptSplitWarning)
                        return await Result("SplitWarningRequired", "Confirm that other players may connect to this PC before allowing incoming connections.");
                    var enabled = await manager.UpdateControlPolicyAsync(new(CompanionListeningEnabled: true));
                    if (!enabled.Ok) return await Result(enabled.Code, enabled.Message);
                }
                using var certificate = new HostIdentity(data).Ensure(identity.Endpoint);
                if (!SharedWorldPeerTrust.ValidRoute(new(identity.Endpoint, HostIdentity.Fingerprint(certificate))))
                    return await Result("LocalConnectionSetupRequired", "Review this PC's saved Friend connection address before preparing the shared Host. No listener or network setting was changed.");
            }
            var endpointReady = await manager.EnsureLocalHostingEndpointAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. Review the shared server again.");
            if (!endpointReady)
                return await Result("LocalConnectionSetupRequired", "Review this PC's Friend connection address before preparing hosting.");
            var previousLocalProfileId = status.LocalProfileId;
            if (createBranch)
            {
                var offer = await link.PrepareSoloRecoveryOfferAsync(profileId, selectedSource?.BranchHash, selectedSource);
                if (!offer.Ok) return await Result(offer.Code, "The exact signed save could not be prepared. Review shared-save status.");
                if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. Review the shared server again.");
                var declared = await link.DeclareSoloRecoveryAsync(profileId, request.AcceptSplitWarning);
                if (!declared.Ok) return await Result(declared.Code, "This PC's signed local continuation could not be recorded. Existing saves were kept.");
                if (selectedSource is not null && declared.Branch is not null)
                    SelectBranch(connection, selectedSource.Root, declared.Branch);
                status = await Status();
            }
            var manualSetup = status.Code == "LocalServerSetupRequired";
            if (status.ServerFile is null && !manualSetup)
                return await Result("InstalledServerRequired", "Install or select the matching dedicated server, then continue hosting.");
            if (manualSetup)
            {
                if (routeCheck is not null) await routeCheck.CheckAsync(cancellationToken);
                return await Status();
            }
            var branch = link.SeparateCopyBranches(profileId).SingleOrDefault(item => item.BranchHash == status.BranchHash);
            if (branch is null || !SameConnection()) return await Result("LocalHostingReviewRequired", "The selected shared history changed. Existing saves were kept.");
            var snapshot = await manager.CompanionSnapshotAsync();
            var existing = snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == status.LocalProfileId);
            var preferred = existing ?? snapshot.Settings.Profiles.SingleOrDefault(item => item.Id == previousLocalProfileId);
            var ports = await manager.LocalHostingPortsAsync(branch.Offer.Version, preferred, cancellationToken);
            if (!ports.Available) return await Result("LocalPortsRequired", "A complete local game-port plan is unavailable. Review the running servers and retry.");
            var setup = new TakeoverLocalSetup(status.ServerFile, branch.Offer.Version.PortableSetup.GameVersion,
                branch.Offer.Version.PortableSetup.AddOns ?? [], true, ports.ControlPort, ports.GamePort);
            if (existing is null)
            {
                MinecraftInstallation? minecraft = null;
                if (branch.Offer.Version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock)
                {
                    var localId = SharedWorldBranchLiveSource.LocalProfileId(branch.BranchHash);
                    // Reuse the reviewed copy-only runtime preparer in this
                    // branch's fresh local folder. The original signed version
                    // remains the sole authority for restore and every check.
                    var localMetadata = branch.Offer.Version with { ProfileId = localId };
                    minecraft = await Task.Run(() => PlannedHandoffMinecraftSetup.Prepare(data, localMetadata,
                        localId, status.ServerFile!, ports.GamePort, snapshot.Settings.Profiles), cancellationToken);
                    setup = setup with { ServerFile = minecraft.ArtifactPath };
                }
                var password = branch.Offer.Version.Game == GameKinds.Valheim ? request.GamePassword ??
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) : null;
                var restored = await manager.RestoreSeparateCopyAsync(profileId, new(branch.BranchHash, setup,
                    access.Name, access.Name, password, minecraft?.ExecutablePath, minecraft?.ServerDirectory,
                    ports.FactorioRconPort), cancellationToken);
                if (!restored.Ok) return await Result(restored.Code, restored.PendingChecks?.FirstOrDefault() ?? restored.Message);
            }
            if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. The prepared world was kept; review it before Start.");
            var finished = await manager.FinishSeparateCopyAsync(profileId, new(branch.BranchHash, setup));
            cancellationToken.ThrowIfCancellationRequested();
            if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. The prepared world was kept; review it before Start.");
            if (!finished.Ok)
            {
                await manager.InvalidateLocalHostingPreparationAsync(profileId, branch.BranchHash, cancellationToken);
                var current = await Status();
                if (current.Code == "LocalServerSetupRequired") return current with { Ok = false, ReadyForManualStart = false };
                return await Result(finished.Code, finished.Message);
            }
            if (request.RequirePeerConnection)
            {
                // The explicit guided hosting instruction also permits sharing
                // this exact signed branch's address with its approved group.
                // Register it before Sync: the first listener otherwise remains
                // Idle until Start, while Start requires this listener first.
                if (announceHosting is not null)
                {
                    var announced = await announceHosting(new(branch.Offer.Roster.OwnerPublicKey,
                        profileId, branch.Offer.Roster.GroupId), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. The prepared world was kept; review it before Start.");
                    if (!announced.Ok)
                    {
                        await manager.InvalidateLocalHostingPreparationAsync(profileId, branch.BranchHash, cancellationToken);
                        return await Result(announced.Code, announced.Message);
                    }
                }
                if (synchronizeListener is not null) await synchronizeListener();
                cancellationToken.ThrowIfCancellationRequested();
                if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. The prepared world was kept; review it before Start.");
                if (routeCheck is not null) await routeCheck.CheckAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. The prepared world was kept; review it before Start.");
                var route = routeCheck is null ? null : await routeCheck.CurrentAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. The prepared world was kept; review it before Start.");
                if (route?.Identity is not { Enabled: true, Active: true, ListenerOwned: true, ListenerState: CompanionListenerStates.Listening } identity ||
                    route?.Step.State == "ActionRequired" ||
                    !SharedWorldPeerTrust.ValidRoute(new(identity.Endpoint, identity.CertificateFingerprint ?? "")) ||
                    identity.Endpoint.TrimEnd('/') != branch.Offer.Proposal.CandidateAddress.TrimEnd('/') ||
                    !string.Equals(identity.CertificateFingerprint, branch.Offer.CandidateTlsFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    await manager.InvalidateLocalHostingPreparationAsync(profileId, branch.BranchHash, cancellationToken);
                    if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. The prepared world was kept; review it before Start.");
                    return await Result("LocalConnectionSetupRequired", "This PC's incoming Friend connection could not start or match the prepared Host. Open Connection help, fix the reported problem, then retry. The prepared save was kept.");
                }
            }
            else if (routeCheck is not null) await routeCheck.CheckAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!SameConnection()) return await Result("ConnectionChanged", "The selected Host changed. The prepared world was kept; review it before Start.");
            return await Result("LocalHostingPrepared", "This PC is prepared with a verified save. Choose Start server to host it.", null);
        }
        catch (Exception error) when (ExpectedFailure(error))
        { return await Result("LocalHostingReviewRequired", "Preparation stopped safely. Existing saves were kept; review the checks and retry."); }
    }

    private bool InstalledAddOnsMatch(ServerProfile profile, SharedWorldVersion version)
    {
        var required = version.PortableSetup.AddOns ?? [];
        if (required.Count == 0) return true;
        var installed = ServerAddOns.List(data, profile);
        return installed.Ok && required.All(addOn => installed.Items.Any(item => item.Enabled &&
            item.Name == addOn.Name && item.Version == addOn.Version && item.RequiredGameVersion == addOn.RequiredGameVersion &&
            item.Type == addOn.Type && (addOn.Id is null || item.Key.EndsWith(addOn.Id, StringComparison.OrdinalIgnoreCase))));
    }

    private string? FindServer(SharedWorldVersion version, IReadOnlyList<ServerProfile> profiles, string? selectedFile)
    {
        var candidates = new List<string>();
        if (selectedFile is not null) candidates.Add(selectedFile);
        candidates.AddRange(profiles.Where(profile => profile.Kind == version.Game)
            .Select(profile => version.Game == GameKinds.MinecraftJava ? profile.Minecraft?.ServerJarPath ?? "" : profile.ExecutablePath));
        if (discoverServers is not null) candidates.AddRange(discoverServers(version.Game));
        else if (version.Game == GameKinds.Valheim)
            candidates.AddRange(ValheimSetup.Scan(includeWorlds: false).Installations.Select(item => item.ExecutablePath));
        else if (version.Game is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock)
            candidates.AddRange(MinecraftSetup.ScanManaged(data, profiles).Installations.Where(item => item.Kind == version.Game)
                .Select(item => item.ArtifactPath));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate) ||
                    !ManagedImportFiles.PlainFile(candidate)) continue;
                if (version.Game == GameKinds.Fixture) return candidate;
                if (version.Game == GameKinds.MinecraftJava)
                {
                    if (!MinecraftSetup.IsSupportedVanillaServerJar(candidate, Path.GetDirectoryName(candidate)!)) continue;
                    using var stream = File.OpenRead(candidate);
                    if (string.Equals(Convert.ToHexString(SHA256.HashData(stream)), version.PortableSetup.JavaServerJarSha256,
                        StringComparison.OrdinalIgnoreCase)) return candidate;
                }
                else if (version.PortableSetup.GameVersion is { Length: > 0 } required && required != "Unknown" &&
                    ServerAddOns.GameVersion(new() { Kind = version.Game, ExecutablePath = candidate }) == required)
                    return candidate;
            }
            catch (Exception error) when (ExpectedFailure(error)) { }
        }
        return null;
    }
}
