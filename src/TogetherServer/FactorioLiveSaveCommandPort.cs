using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TogetherServer;

// Internal candidate only. No Host/Friend route or shared-copy publisher calls this.
internal sealed class ExactManagedFactorioLiveSaveCommandPort(LocalData data)
{
    private const string SaveCommand = "/server-save";

    public LiveSaveCommandDispatch RequestFactorioSave(ManagedRun requestedRun)
    {
        ArgumentNullException.ThrowIfNull(requestedRun);
        if (requestedRun.Kind != GameKinds.Factorio || requestedRun.ProfileId == Guid.Empty ||
            requestedRun.OperationId == Guid.Empty || requestedRun.ProcessId is null ||
            requestedRun.StartTimeUtcTicks is null || string.IsNullOrWhiteSpace(requestedRun.ExecutablePath))
            throw new InvalidOperationException("A recorded Factorio managed run is required.");

        // Do not let a stale caller, edited run, or changed saved profile redirect RCON.
        var run = data.LoadRuns().SingleOrDefault(item => item.OperationId == requestedRun.OperationId);
        var profile = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == requestedRun.ProfileId);
        if (run is null || profile is null || run.Kind != GameKinds.Factorio ||
            profile.Kind != GameKinds.Factorio || run.ProfileId != profile.Id ||
            run.StopRequestedUtc is not null ||
            run.ProcessId != requestedRun.ProcessId || run.StartTimeUtcTicks != requestedRun.StartTimeUtcTicks ||
            run.WorldId != requestedRun.WorldId || run.GamePort != requestedRun.GamePort ||
            !SamePath(run.WorldDirectory, requestedRun.WorldDirectory) ||
            !SamePath(run.ExecutablePath, requestedRun.ExecutablePath) ||
            !run.DeclaredPorts.SequenceEqual(requestedRun.DeclaredPorts) ||
            run.WorldId != profile.WorldId || run.GamePort != profile.GamePort ||
            !SamePath(run.WorldDirectory, profile.WorldDirectory) ||
            !SamePath(run.ExecutablePath, profile.ExecutablePath) ||
            !FactorioSetup.IsImportedCopy(data, profile))
            throw new InvalidOperationException("The exact Factorio managed run and saved profile could not be verified.");

        var rconPort = profile.Factorio?.RconPort;
        if (rconPort is not >= 1024 or > 65535 || rconPort == run.GamePort ||
            run.DeclaredPorts.Count != 2 ||
            !run.DeclaredPorts.Any(port => port.Protocol == "UDP" && port.Port == run.GamePort) ||
            !run.DeclaredPorts.Any(port => port.Protocol == "TCP" && port.Port == rconPort))
            throw new InvalidOperationException("The saved Factorio RCON port does not match the exact run.");

        // Start created this protected credential. A candidate dispatch must not create or rotate one.
        var protectedPassword = data.LoadProtected($"factorio-rcon-{profile.Id:N}.protected");
        if (protectedPassword is null)
            throw new InvalidOperationException("The saved Factorio RCON credential is unavailable.");
        var password = new UTF8Encoding(false, true).GetString(protectedPassword);
        if (password.Length is < 32 or > 128 || password.Any(char.IsControl))
            throw new InvalidOperationException("The saved Factorio RCON credential is invalid.");

        Process process;
        try { process = Process.GetProcessById(run.ProcessId!.Value); }
        catch (ArgumentException ex)
        { throw new InvalidOperationException("The exact Factorio managed process could not be verified.", ex); }
        using (process)
        {
            VerifyExactProcess(process, run);
            _ = FactorioRcon.Execute(rconPort.Value, password, SaveCommand);
            VerifyExactProcess(process, run);
        }

        // RCON acknowledgement cannot prove a closed, loadable save or a publishable copy.
        return new(run.OperationId, SaveCommand, CompletionConfirmed: false);
    }

    private static void VerifyExactProcess(Process process, ManagedRun run)
    {
        try
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                SamePath(process.MainModule?.FileName, run.ExecutablePath)) return;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or IOException or
                                   NotSupportedException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("The exact Factorio managed process could not be verified.", ex);
        }
        throw new InvalidOperationException("The exact Factorio managed process could not be verified.");
    }

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
