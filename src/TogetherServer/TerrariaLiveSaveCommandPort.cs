using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace TogetherServer;

// Internal command candidate. Only an operation ID enters this boundary; the
// command, executable, world, and console target come from the saved Host run.
internal sealed class ExactManagedTerrariaLiveSaveCommandPort(LocalData data)
{
    public LiveSaveCommandDispatch RequestTerrariaSave(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new InvalidOperationException("A recorded Terraria managed run is required.");

        var run = data.LoadRuns().SingleOrDefault(item => item.OperationId == operationId);
        if (run is null || run.Kind != GameKinds.Terraria || run.ProfileId == Guid.Empty ||
            run.ProcessId is not > 0 || run.StartTimeUtcTicks is not > 0 ||
            run.StopRequestedUtc is not null)
            throw new InvalidOperationException("An active Terraria managed run is required.");

        var hasCapture = run.ConsoleCaptureProcessId is not null || run.ConsoleCaptureStartTimeUtcTicks is not null ||
            !string.IsNullOrEmpty(run.ConsoleCaptureExecutablePath);
        if (hasCapture && (!WindowsConsoleProcess.CaptureIdentityMatches(run) ||
            !SamePath(run.LogPath, data.RunLogPath(operationId))))
            throw new InvalidOperationException("The exact Terraria owned console capture could not be verified.");

        var profile = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == run.ProfileId);
        var driver = new TerrariaServerDriver(data);
        if (profile is null || profile.Kind != GameKinds.Terraria ||
            driver.ValidateForStart(profile) is not null ||
            run.WorldId != profile.WorldId || run.GamePort != profile.GamePort ||
            !SamePath(run.WorldDirectory, driver.ManagedSaveDirectory(profile)) ||
            !SamePath(run.ExecutablePath, driver.ManagedExecutablePath(profile)) ||
            !run.DeclaredPorts.SequenceEqual(driver.Ports(profile)))
            throw new InvalidOperationException("The Terraria managed run and saved profile could not be verified.");

        Process process;
        try { process = Process.GetProcessById(run.ProcessId.Value); }
        catch (ArgumentException ex)
        { throw new InvalidOperationException("The exact Terraria managed process could not be verified.", ex); }

        using (process)
        {
            VerifyExactProcess(process, run);
            WindowsConsoleProcess.RequestTerrariaSave(process, run);
            VerifyExactProcess(process, run);
        }

        // Console dispatch cannot confirm a completed write or a loadable copy.
        return new(operationId, "save", CompletionConfirmed: false);
    }

    private static void VerifyExactProcess(Process process, ManagedRun run)
    {
        try
        {
            process.Refresh();
            if (!process.HasExited && process.Id == run.ProcessId &&
                process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                SamePath(process.MainModule?.FileName, run.ExecutablePath)) return;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or IOException or
                                   NotSupportedException or SecurityException)
        {
            throw new InvalidOperationException("The exact Terraria managed process could not be verified.", ex);
        }
        throw new InvalidOperationException("The exact Terraria managed process could not be verified.");
    }

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
