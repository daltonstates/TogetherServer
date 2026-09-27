using System.Security;

namespace TogetherServer;

// GUI launches can inherit redirected standard handles from an installer or updater.
// Those handles may disappear after the parent exits, so diagnostics must never affect app behavior.
internal static class DiagnosticOutput
{
    public static void WriteLine(string message) => TryWrite(() => Console.WriteLine(message));

    public static void WriteError(string message) => TryWrite(() => Console.Error.WriteLine(message));

    private static void TryWrite(Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or SecurityException)
        {
            // Best-effort diagnostics only. The desktop app has no required console.
        }
    }
}
