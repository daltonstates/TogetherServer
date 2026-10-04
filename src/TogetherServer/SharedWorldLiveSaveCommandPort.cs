using System.ComponentModel;
using System.Diagnostics;

namespace TogetherServer;

// Internal candidate boundary. No Host, Friend, or shared-copy route calls this.
// A successful dispatch is deliberately weaker than completion or a live capture.
internal interface ISharedWorldLiveSaveCommandPort
{
    LiveSaveCommandDispatch RequestJavaFlush(ManagedRun run);
}

internal sealed record LiveSaveCommandDispatch(Guid OperationId, string FixedCommand,
    bool CompletionConfirmed);

internal sealed class ExactManagedConsoleLiveSaveCommandPort : ISharedWorldLiveSaveCommandPort
{
    public LiveSaveCommandDispatch RequestJavaFlush(ManagedRun run)
    {
        if (run.Kind != GameKinds.MinecraftJava || run.OperationId == Guid.Empty ||
            run.ProcessId is null || run.StartTimeUtcTicks is null ||
            string.IsNullOrWhiteSpace(run.ExecutablePath))
            throw new InvalidOperationException("A recorded Java managed run is required.");

        try
        {
            using var process = Process.GetProcessById(run.ProcessId.Value);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != run.StartTimeUtcTicks ||
                !Path.GetFullPath(process.MainModule!.FileName).Equals(
                    Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The exact Java managed process could not be verified.");

            WindowsConsoleProcess.RequestJavaSaveFlush(process, run);
            return new(run.OperationId, "save-all flush", CompletionConfirmed: false);
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or IOException or
                                   NotSupportedException)
        {
            throw new InvalidOperationException("The exact Java managed process could not be verified.", ex);
        }
    }
}
