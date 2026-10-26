using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using TogetherServer;

// Evidence only for this runner's disposable fixture. No diagnostic changes
// lifecycle authority, retries a process, or publishes a path or captured text.
internal sealed class FixtureReadinessDiagnostics : IDisposable
{
    private const int MaximumReadBytes = 64 * 1024;
    private const long MaximumLogBytes = 8L * 1024 * 1024;
    private const string StartupPrefix = "[TogetherServer fixture/ERROR]: synthetic listener startup failed; code=31; socketError=";
    private readonly string fixtureRoot;
    private Process? process;
    private string? logPath;

    private FixtureReadinessDiagnostics(string fixtureRoot) => this.fixtureRoot = Path.GetFullPath(fixtureRoot);

    internal static FixtureReadinessDiagnostics Capture(LocalData data, Guid profileId,
        ActionResult launch, string fixtureRoot)
    {
        var diagnostic = new FixtureReadinessDiagnostics(fixtureRoot);
        Process? candidate = null;
        try
        {
            var operationId = launch.Snapshot.Runs.SingleOrDefault(run => run.ProfileId == profileId)?.RunOperationId;
            var run = data.LoadRuns().SingleOrDefault(run => run.ProfileId == profileId && run.OperationId == operationId);
            if (run is null || run.OperationId == Guid.Empty ||
                run.Kind is not (GameKinds.MinecraftJava or GameKinds.MinecraftBedrock) ||
                !diagnostic.IsOwnedPath(run.WorldDirectory) || !diagnostic.IsOwnedPath(run.ExecutablePath) ||
                !diagnostic.IsOwnedPath(run.LogPath) ||
                !Path.GetFullPath(run.LogPath).Equals(data.RunLogPath(run.OperationId), StringComparison.OrdinalIgnoreCase))
                return diagnostic;

            // Retain the exact operation's log before Refresh can archive its run.
            diagnostic.logPath = run.LogPath;
            if (run.ProcessId is not > 0 || run.StartTimeUtcTicks is null) return diagnostic;
            candidate = Process.GetProcessById(run.ProcessId.Value);
            _ = candidate.Handle; // Keep this process's exit information after it exits.
            if (candidate.HasExited || candidate.StartTime.ToUniversalTime().Ticks != run.StartTimeUtcTicks ||
                candidate.MainModule?.FileName is not { } actualPath ||
                !Path.GetFullPath(actualPath).Equals(Path.GetFullPath(run.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                return diagnostic;
            diagnostic.process = candidate;
            candidate = null;
        }
        catch (Exception ex) when (Unavailable(ex)) { }
        finally { candidate?.Dispose(); }
        return diagnostic;
    }

    internal FixtureFailureEvidence Read()
    {
        bool? exited = null;
        int? exitCode = null;
        if (process is not null)
        {
            try
            {
                exited = process.HasExited;
                if (exited == true) exitCode = process.ExitCode;
            }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
        var startup = ReadStartupFailure();
        return new(process is not null, exited, exitCode, startup.Code, startup.SocketError is null ? null : 31,
            startup.SocketError);
    }

    private (string Code, int? SocketError) ReadStartupFailure()
    {
        try
        {
            if (logPath is null || !IsOwnedPath(logPath)) return ("Unavailable", null);
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumLogBytes) return ("Unavailable", null);
            var bytes = new byte[(int)Math.Min(stream.Length, MaximumReadBytes)];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            var start = 0;
            for (var index = 0; index < count; index++)
            {
                if (bytes[index] != (byte)'\n') continue;
                if (TryParseStartupFrame(bytes.AsSpan(start, index - start), out var socketError))
                    return ("FixtureListenerStartupFailed", socketError);
                start = index + 1;
            }
            return ("NotObserved", null);
        }
        catch (Exception ex) when (Unavailable(ex)) { return ("Unavailable", null); }
    }

    private bool IsOwnedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (current.Equals(fixtureRoot, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static bool TryParseStartupFrame(ReadOnlySpan<byte> frame, out int socketError)
    {
        socketError = 0;
        if (!MinecraftCapturedLogFrame.TryDecode(frame, out var line) || line is null ||
            line.Stream != "Stderr" || line.Truncated || !line.Message.StartsWith(StartupPrefix, StringComparison.Ordinal) ||
            !line.Message.EndsWith(".", StringComparison.Ordinal)) return false;
        var numeric = line.Message.AsSpan(StartupPrefix.Length, line.Message.Length - StartupPrefix.Length - 1);
        return int.TryParse(numeric, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out socketError) &&
            numeric.SequenceEqual(socketError.ToString(CultureInfo.InvariantCulture)) && Enum.IsDefined((SocketError)socketError);
    }

    internal static void CheckParser()
    {
        byte[] Frame(string message, string stream = "Stderr", bool truncated = false) =>
            MinecraftCapturedLogFrame.Encode(new(DateTimeOffset.UnixEpoch, stream, message, truncated));
        void Require(bool condition) { if (!condition) throw new InvalidDataException("Fixture diagnostic parser regression."); }
        foreach (var code in new[] { 0, -1, (int)SocketError.AddressAlreadyInUse, (int)SocketError.AccessDenied })
            Require(TryParseStartupFrame(Frame(StartupPrefix + code.ToString(CultureInfo.InvariantCulture) + "."), out var parsed) && parsed == code);
        var valid = StartupPrefix + "10048.";
        foreach (var frame in new[]
        {
            Frame(valid, "Stdout"), Frame(valid, truncated: true), Frame(valid + " extra"), Frame("prefix " + valid),
            Frame(StartupPrefix + "010048."), Frame(StartupPrefix + "+10048."), Frame(StartupPrefix + "2147483648."),
            Frame(StartupPrefix + "123456."), Frame(valid.Replace("code=31", "code=32", StringComparison.Ordinal)),
            Frame(valid + "\n"), "unframed startup code=31"u8.ToArray(), new byte[MinecraftConsoleCapture.MaximumFrameBytes + 1]
        }) Require(!TryParseStartupFrame(frame, out _));
    }

    private static bool Unavailable(Exception exception) => exception is IOException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException or
        System.Security.SecurityException or System.Text.Json.JsonException;

    public void Dispose() => process?.Dispose();
}

internal sealed record FixtureFailureEvidence(bool ExactProcessIdentityRetained, bool? Exited, int? ExitCode,
    string StartupEvidenceCode, int? StartupFailureCode, int? SocketError);
