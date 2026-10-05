using System.Diagnostics;
using System.Text;

namespace TogetherServer;

// Fresh bytes from the recorded app-owned operation log, never from a caller path.
// A log observation is only completion evidence; it does not bind mutable save bytes.
internal sealed class ManagedSaveLogCursor : IDisposable
{
    private const int MaximumReadBytes = 64 * 1024;
    private const int AnchorBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly LocalData data;
    private readonly ManagedLiveSnapshotStore identities;
    private readonly Guid profileId;
    private readonly Guid operationId;
    private readonly int processId;
    private readonly long startTicks;
    private readonly string logPath;
    private readonly FileStream stream;
    private readonly ValheimAutosaveObservationCandidate.FileIdentity identity;
    private readonly List<byte> line = [];
    private byte[] anchor;
    private long offset;
    private bool discardFirstLine;
    private bool invalid;
    internal DateTimeOffset OpenedUtc { get; } = DateTimeOffset.UtcNow;

    private ManagedSaveLogCursor(LocalData data, ManagedLiveSnapshotStore identities,
        ServerProfile profile, ManagedRun run, FileStream stream)
    {
        this.data = data;
        this.identities = identities;
        profileId = profile.Id; operationId = run.OperationId;
        processId = run.ProcessId!.Value; startTicks = run.StartTimeUtcTicks!.Value;
        logPath = data.RunLogPath(operationId);
        this.stream = stream;
        identity = ValheimAutosaveObservationCandidate.FileIdentity.Read(stream.SafeFileHandle);
        offset = stream.Length;
        if (offset > 0)
        {
            Span<byte> last = stackalloc byte[1];
            if (RandomAccess.Read(stream.SafeFileHandle, last, offset - 1) != 1)
                throw new InvalidDataException("The owned save log changed while opening its cursor.");
            discardFirstLine = last[0] != (byte)'\n';
        }
        anchor = ReadAnchor(stream, offset);
    }

    internal static ManagedSaveLogCursor Open(LocalData data, GameServerRegistry games,
        ServerProfile profile, ManagedRun run, Func<ManagedRun, bool>? fixtureRunIdentityForChecks = null)
    {
        if (fixtureRunIdentityForChecks is not null && profile.Kind != GameKinds.Fixture)
            throw new InvalidDataException("A synthetic process override cannot authorize a real game log.");
        var identities = new ManagedLiveSnapshotStore(data, games)
        { FixtureRunIdentityForChecks = fixtureRunIdentityForChecks };
        identities.RequireExactRun(profile, run);
        RequireCaptureIdentity(profile, run);
        var expected = data.RunLogPath(run.OperationId);
        if (!Path.IsPathFullyQualified(run.LogPath) ||
            !Path.GetFullPath(run.LogPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A save observer requires this operation's app-owned log.");
        RequirePlainLog(data, expected);
        var stream = new FileStream(expected, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        try
        {
            var cursor = new ManagedSaveLogCursor(data, identities, profile, run, stream);
            cursor.RequireCurrent(profile, run);
            return cursor;
        }
        catch { stream.Dispose(); throw; }
    }

    internal IReadOnlyList<string> ReadNewLines(ServerProfile profile, ManagedRun run,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invalid) throw new InvalidDataException("The owned save log cursor is no longer valid.");
        try
        {
            RequireCurrent(profile, run);
            if (stream.Length < offset || !ReadAnchor(stream, offset).AsSpan().SequenceEqual(anchor))
                throw new InvalidDataException("The owned save log was truncated or its retained anchor changed.");
            var bytes = new byte[MaximumReadBytes];
            var read = RandomAccess.Read(stream.SafeFileHandle, bytes, offset);
            var lines = new List<string>();
            for (var index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = bytes[index];
                if (discardFirstLine)
                {
                    if (value == (byte)'\n') discardFirstLine = false;
                    continue;
                }
                if (value == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r') line.RemoveAt(line.Count - 1);
                    lines.Add(Utf8.GetString(line.ToArray()));
                    line.Clear();
                }
                else
                {
                    if (line.Count >= MinecraftConsoleCapture.MaximumFrameBytes)
                        throw new InvalidDataException("An owned save log line exceeded its bounded grammar.");
                    line.Add(value);
                }
            }
            offset = checked(offset + read);
            anchor = ReadAnchor(stream, offset);
            RequireCurrent(profile, run);
            return lines.AsReadOnly();
        }
        catch (Exception ex) when (ManagedLiveSnapshotStore.ExpectedFailure(ex))
        { invalid = true; throw; }
    }

    private void RequireCurrent(ServerProfile profile, ManagedRun run)
    {
        if (profile.Id != profileId || run.OperationId != operationId || run.ProcessId != processId ||
            run.StartTimeUtcTicks != startTicks || !Path.IsPathFullyQualified(run.LogPath) ||
            !Path.GetFullPath(run.LogPath).Equals(logPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The save log cursor belongs to another exact run.");
        identities.RequireExactRun(profile, run);
        RequireCaptureIdentity(profile, run);
        RequirePlainLog(data, logPath);
        using var named = new FileStream(logPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (ValheimAutosaveObservationCandidate.FileIdentity.Read(named.SafeFileHandle) != identity ||
            ValheimAutosaveObservationCandidate.FileIdentity.Read(stream.SafeFileHandle) != identity)
            throw new InvalidDataException("The operation's owned save log was replaced.");
    }

    private static void RequireCaptureIdentity(ServerProfile profile, ManagedRun run)
    {
        if (profile.Kind is not (GameKinds.MinecraftJava or GameKinds.MinecraftBedrock or GameKinds.Terraria)) return;
        if (run.ConsoleCaptureProcessId is not > 0 || run.ConsoleCaptureStartTimeUtcTicks is not > 0 ||
            run.ConsoleCaptureProcessId == run.ProcessId || run.ConsoleCaptureProcessId == Environment.ProcessId ||
            !Path.IsPathFullyQualified(run.ConsoleCaptureExecutablePath))
            throw new InvalidDataException("The exact managed console capture is unavailable.");
        using var capture = Process.GetProcessById(run.ConsoleCaptureProcessId.Value);
        if (capture.HasExited || capture.StartTime.ToUniversalTime().Ticks != run.ConsoleCaptureStartTimeUtcTicks ||
            capture.MainModule?.FileName is not { } executable ||
            !Path.GetFullPath(executable).Equals(Path.GetFullPath(run.ConsoleCaptureExecutablePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The operation's managed console capture changed.");
    }

    private static void RequirePlainLog(LocalData data, string path)
    {
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The app-owned save log is missing or linked.");
    }

    private static byte[] ReadAnchor(FileStream stream, long offset)
    {
        var length = (int)Math.Min(AnchorBytes, offset);
        var result = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = RandomAccess.Read(stream.SafeFileHandle, result.AsSpan(read), offset - length + read);
            if (count == 0) throw new InvalidDataException("The save log anchor was truncated.");
            read += count;
        }
        return result;
    }

    public void Dispose() { invalid = true; stream.Dispose(); }
}
