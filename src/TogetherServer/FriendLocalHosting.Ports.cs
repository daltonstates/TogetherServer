namespace TogetherServer;

internal sealed record LocalHostingPortSelection(bool Ok, string Code, string Message, LocalHostingPortPlan Ports);

internal sealed partial class FriendLocalHostingService
{
    private async Task<LocalHostingPortSelection> ChooseLocalPortsAsync(SharedWorldVersion version,
        ServerProfile? existing, LocalHostingPrepareRequest request, CancellationToken cancellationToken)
    {
        var current = await manager.LocalHostingPortsAsync(version, existing, cancellationToken);
        LocalHostingPortSelection Refused(string code, string message) => new(false, code, message, current);
        ApprovedGamePort? selected = null;
        if (request.ApprovedPortId is { } selectedId)
        {
            selected = data.ApprovedGamePorts.Find(version.Game, selectedId);
            if (selected is null) return Refused("LocalPortApprovalRequired", "The selected approved ports changed or belong to another game. Choose this PC's game ports again.");
        }
        else if (request.ApprovePorts)
        {
            var gamePort = request.GamePort ?? current.GamePort;
            var rconPort = version.Game == GameKinds.Factorio ? request.FactorioRconPort ?? current.FactorioRconPort : request.FactorioRconPort;
            if (existing is not null && (gamePort != existing.GamePort || rconPort != existing.Factorio?.RconPort))
                return Refused("LocalPortChangeRequired", "This prepared server retains its saved ports. Change them in its local game setup before approving a different plan.");
            var saved = data.ApprovedGamePorts.Save(new(version.Game, gamePort, rconPort));
            if (!saved.Ok) return Refused(saved.Code, saved.Message);
            selected = data.ApprovedGamePorts.Matching(version.Game, gamePort, rconPort);
        }
        else if (request.GamePort is not null || request.FactorioRconPort is not null)
            return Refused("LocalPortApprovalRequired", "Explicitly approve the local game ports before preparing hosting.");
        else selected = current.ApprovedPortId is { } currentId ? data.ApprovedGamePorts.Find(version.Game, currentId) : null;
        if (selected is null) return Refused("LocalPortApprovalRequired", "Choose and approve this PC's game ports once before preparing hosting. The other Host's port is only a suggestion.");
        if (existing is not null && (selected.GamePort != existing.GamePort || selected.FactorioRconPort != existing.Factorio?.RconPort))
            return Refused("LocalPortChangeRequired", "This prepared server retains its saved ports. Change them in its local game setup before choosing a different approved plan.");
        // Remember explicit selection as this game's preferred reusable plan.
        if (request.ApprovedPortId is not null)
            data.ApprovedGamePorts.Save(new(selected.Game, selected.GamePort, selected.FactorioRconPort), selected.Id);
        var ports = await manager.LocalHostingPortsAsync(version, existing, cancellationToken, selected.Id);
        return ports.Available ? new(true, "LocalPortsApproved", "This PC's approved game ports are available.", ports) :
            new(false, "LocalPortsRequired", "An approved game port is in use or overlaps this PC's Friend connection. Free it or choose another approved plan; ports were not changed automatically.", ports);
    }
}
