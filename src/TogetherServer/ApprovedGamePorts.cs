using System.Text.Json;

namespace TogetherServer;

public sealed record ApprovedGamePort(Guid Id, string Game, int GamePort, int? FactorioRconPort = null);
public sealed record ApprovedGamePortsView(IReadOnlyList<ApprovedGamePort> ApprovedPorts);
public sealed record ApprovedGamePortsResult(bool Ok, string Code, string Message,
    IReadOnlyList<ApprovedGamePort> ApprovedPorts);
internal sealed record ApprovedGamePortInput(string Game, int GamePort, int? FactorioRconPort);
internal sealed record ApprovedGamePortsState(int Schema, IReadOnlyList<ApprovedGamePort> ApprovedPorts,
    bool LocalProfilesImported = false);

// Deliberate choices on this PC only. Signed remote setup is never an approval,
// and remembering a port grants no firewall, router or listener authority.
internal sealed class ApprovedGamePortsStore
{
    internal const string FileName = "approved-game-ports.protected";
    private const int MaximumPlans = 128;
    private readonly LocalData data;
    private readonly object sync = new();
    private List<ApprovedGamePort> plans;

    internal ApprovedGamePortsStore(LocalData data)
    {
        this.data = data;
        var hadSavedFile = data.HasProtected(FileName);
        var state = data.LoadProtectedJson<ApprovedGamePortsState>(FileName);
        var damaged = hadSavedFile && state is null;
        if (state is not null && (state.Schema != 1 || state.ApprovedPorts is null ||
            state.ApprovedPorts.Count > MaximumPlans || state.ApprovedPorts.Any(plan => plan is null ||
                plan.Id == Guid.Empty || !Valid(plan.Game, plan.GamePort, plan.FactorioRconPort)) ||
            state.ApprovedPorts.Select(plan => plan.Id).Distinct().Count() != state.ApprovedPorts.Count ||
            state.ApprovedPorts.Select(plan => (plan.Game, plan.GamePort, plan.FactorioRconPort)).Distinct().Count() != state.ApprovedPorts.Count))
        {
            data.QuarantineState(FileName, "The approved game ports need review. Approve this PC's game ports again.", false);
            state = null;
            damaged = true;
        }
        plans = state?.ApprovedPorts.ToList() ?? [];
        if (state?.LocalProfilesImported != true)
        {
            // Earlier ordinary Host setup already represented a local port
            // choice. Preserve that consent once, excluding every received or
            // inherited hosting identity. A removed approval stays removed.
            if (!damaged)
            {
                var authority = new WorldAuthorityStore(data);
                foreach (var profile in data.LoadSettings().Profiles ?? [])
                {
                    if (profile.Kind == GameKinds.Fixture || profile.WorldLoadRehearsalId is not null ||
                        profile.SeparateCopySourceProfileId is not null || profile.SeparateCopyBranchHash is not null ||
                        data.HasProtected(SharedWorldBranchLiveSource.StateName(profile.Id)) ||
                        data.HasProtected($"successor-restore-{profile.Id:N}.protected") || authority.HasState(profile.Id)) continue;
                    var rcon = profile.Kind == GameKinds.Factorio ? profile.Factorio?.RconPort : null;
                    if (!Valid(profile.Kind, profile.GamePort, rcon) || plans.Count >= MaximumPlans ||
                        plans.Any(plan => plan.Game == profile.Kind && plan.GamePort == profile.GamePort && plan.FactorioRconPort == rcon)) continue;
                    plans.Add(new(Guid.NewGuid(), profile.Kind, profile.GamePort, rcon));
                }
            }
            Persist(plans);
        }
    }

    internal static bool Supported(string game) => game is GameKinds.Valheim or GameKinds.MinecraftJava or
        GameKinds.MinecraftBedrock or GameKinds.Factorio or GameKinds.Terraria or GameKinds.Fixture;

    internal static bool Valid(string game, int gamePort, int? rconPort) => Supported(game) &&
        gamePort is >= 1024 and <= 65535 && (game != GameKinds.Valheim || gamePort < 65535) &&
        (game == GameKinds.Factorio ? rconPort is >= 1024 and <= 65535 && rconPort != gamePort : rconPort is null);

    internal ApprovedGamePortsView View() { lock (sync) return new(plans.ToArray()); }
    internal ApprovedGamePort? Find(string game, Guid? id = null)
    { lock (sync) return id is { } exact ? plans.SingleOrDefault(plan => plan.Id == exact && plan.Game == game) : plans.LastOrDefault(plan => plan.Game == game); }
    internal ApprovedGamePort? Matching(string game, int port, int? rcon)
    { lock (sync) return plans.SingleOrDefault(plan => plan.Game == game && plan.GamePort == port && plan.FactorioRconPort == rcon); }

    internal ApprovedGamePortsResult Save(ApprovedGamePortInput input, Guid? id = null)
    {
        lock (sync)
        {
            ApprovedGamePortsResult Result(bool ok, string code, string message) => new(ok, code, message, plans.ToArray());
            if (!Valid(input.Game, input.GamePort, input.FactorioRconPort))
                return Result(false, "InvalidGamePorts", "Choose a supported game and valid local game ports. Factorio also needs a different private RCON port.");
            if (id is { } exact && plans.All(plan => plan.Id != exact))
                return Result(false, "UnknownApprovedPorts", "Choose a saved approved game-port plan.");
            var duplicate = plans.SingleOrDefault(plan => plan.Game == input.Game && plan.GamePort == input.GamePort && plan.FactorioRconPort == input.FactorioRconPort);
            if (duplicate is not null && id is not null && duplicate.Id != id)
                return Result(false, "DuplicateApprovedPorts", "Those game ports are already approved for this game.");
            if (id is null && duplicate is null && plans.Count >= MaximumPlans)
                return Result(false, "ApprovedPortsLimit", "Remove an unused approved game-port plan before adding another.");
            var plan = new ApprovedGamePort(id ?? duplicate?.Id ?? Guid.NewGuid(), input.Game, input.GamePort, input.FactorioRconPort);
            var next = plans.Where(item => item.Id != plan.Id).Append(plan).ToList();
            Persist(next);
            return Result(true, "GamePortsApproved", "These game ports are approved for reuse on this PC. Network settings were not changed.");
        }
    }

    internal ApprovedGamePortsResult Delete(Guid id)
    {
        lock (sync)
        {
            if (plans.All(plan => plan.Id != id)) return new(false, "UnknownApprovedPorts", "Choose a saved approved game-port plan.", plans.ToArray());
            Persist(plans.Where(plan => plan.Id != id).ToList());
            return new(true, "ApprovedPortsRemoved", "The saved approval was removed. Existing server setup and running servers were kept.", plans.ToArray());
        }
    }

    private void Persist(List<ApprovedGamePort> next)
    {
        data.SaveProtected(FileName, JsonSerializer.SerializeToUtf8Bytes(new ApprovedGamePortsState(1, next, LocalProfilesImported: true)));
        plans = next;
    }
}
