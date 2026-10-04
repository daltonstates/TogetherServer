using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace TogetherServer;

// Internal candidate only. There is deliberately no production console-output parser,
// Host action, copy, or publisher entry point for this protocol.
internal enum BedrockQuerySource { FixtureSynthetic }
internal sealed record BedrockSnapshotFile(string FileName, long FileSize);
internal sealed record BedrockQueryEvidence(Guid OperationId, BedrockQuerySource Source,
    IReadOnlyList<BedrockSnapshotFile> Files);
internal sealed record BedrockValidatedSnapshot(Guid OperationId,
    IReadOnlyList<BedrockSnapshotFile> Files, bool CompletionConfirmed = false,
    bool LiveCaptureAccepted = false);
internal sealed record BedrockPendingResume(Guid ProfileId, Guid OperationId, int ProcessId,
    long StartTimeUtcTicks, string ExecutablePath, string WorldDirectory, Guid AttemptNonce,
    bool ResumeDispatched = false);
internal sealed record BedrockFixtureResumeAcknowledgement(BedrockQuerySource Source,
    Guid ProfileId, Guid OperationId, int ProcessId, long StartTimeUtcTicks,
    string ExecutablePath, string WorldDirectory, Guid AttemptNonce);

internal sealed class BedrockLiveSaveCandidate(LocalData data)
{
    private readonly string markerPath = Path.Combine(data.RootPath, "bedrock-live-save-pending-resume.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal bool HasPendingResume => File.Exists(markerPath);

    internal void Hold(ManagedRun requestedRun)
    {
        var run = ExactRun(requestedRun);
        using var process = ExactProcess(run);
        var marker = new BedrockPendingResume(run.ProfileId, run.OperationId, run.ProcessId!.Value,
            run.StartTimeUtcTicks!.Value, run.ExecutablePath, run.WorldDirectory, Guid.NewGuid());
        // The write-through marker exists before a hold can reach the console. An
        // ambiguous dispatch leaves it for explicit recovery on a later Host instance.
        using (var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, marker, Json);
            stream.Flush(true);
        }
        WindowsConsoleProcess.RequestBedrockSaveHold(process, run);
    }

    internal BedrockValidatedSnapshot QueryFixture(ManagedRun requestedRun,
        Func<BedrockQueryEvidence> readEvidence)
    {
        var run = ExactRun(requestedRun);
        RequirePending(run);
        using var process = ExactProcess(run);
        WindowsConsoleProcess.RequestBedrockSaveQuery(process, run);
        // The fixture reader runs only after the fixed query was dispatched. No
        // real Bedrock console text is interpreted or accepted here.
        return Validate(run, readEvidence());
    }

    internal void Resume(ManagedRun requestedRun)
    {
        var run = ExactRun(requestedRun);
        var marker = RequirePending(run);
        using var process = ExactProcess(run);
        WindowsConsoleProcess.RequestBedrockSaveResume(process, run);
        // Queueing a console command is not evidence that Bedrock resumed writes.
        // Record dispatch durably, but retain the marker until acknowledgement.
        // If this write fails, recovery can safely retry the fixed command.
        WriteMarker(marker with { ResumeDispatched = true }, replace: true);
    }

    internal bool RecoverPending(ManagedRun requestedRun)
    {
        if (!HasPendingResume) return false;
        Resume(requestedRun);
        return true;
    }

    internal BedrockValidatedSnapshot CaptureFixtureQuery(ManagedRun run,
        Func<BedrockQueryEvidence> evidence,
        Action<BedrockFixtureResumeAcknowledgement> prepareResume,
        Func<BedrockFixtureResumeAcknowledgement> resumeAcknowledgement)
    {
        try
        {
            Hold(run);
            return QueryFixture(run, evidence);
        }
        finally
        {
            if (HasPendingResume)
            {
                try { prepareResume(FixtureResumeRequest(run)); }
                finally { Resume(run); }
                AcknowledgeFixtureResume(run, resumeAcknowledgement());
            }
        }
    }

    internal BedrockFixtureResumeAcknowledgement FixtureResumeRequest(ManagedRun requestedRun)
    {
        var run = ExactRun(requestedRun);
        var marker = RequirePending(run);
        return new(BedrockQuerySource.FixtureSynthetic, marker.ProfileId, marker.OperationId,
            marker.ProcessId, marker.StartTimeUtcTicks, marker.ExecutablePath,
            marker.WorldDirectory, marker.AttemptNonce);
    }

    internal void AcknowledgeFixtureResume(ManagedRun requestedRun,
        BedrockFixtureResumeAcknowledgement acknowledgement)
    {
        var run = ExactRun(requestedRun);
        var marker = RequirePending(run);
        if (!marker.ResumeDispatched || acknowledgement is null ||
            acknowledgement.Source != BedrockQuerySource.FixtureSynthetic ||
            acknowledgement.ProfileId != marker.ProfileId ||
            acknowledgement.OperationId != marker.OperationId ||
            acknowledgement.ProcessId != marker.ProcessId ||
            acknowledgement.StartTimeUtcTicks != marker.StartTimeUtcTicks ||
            !SamePath(acknowledgement.ExecutablePath, marker.ExecutablePath) ||
            !SamePath(acknowledgement.WorldDirectory, marker.WorldDirectory) ||
            acknowledgement.AttemptNonce == Guid.Empty ||
            acknowledgement.AttemptNonce != marker.AttemptNonce)
            throw new InvalidOperationException("Fixture resume acknowledgement does not match the pending hold.");
        File.Delete(markerPath);
    }

    private void WriteMarker(BedrockPendingResume marker, bool replace)
    {
        var temporary = markerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, marker, Json);
                stream.Flush(true);
            }
            File.Move(temporary, markerPath, replace);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private ManagedRun ExactRun(ManagedRun requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.Kind != GameKinds.MinecraftBedrock || requested.ProfileId == Guid.Empty ||
            requested.OperationId == Guid.Empty || requested.ProcessId is null ||
            requested.StartTimeUtcTicks is null || requested.StopRequestedUtc is not null)
            throw new InvalidOperationException("An active Bedrock managed run is required.");
        var run = data.LoadRuns().SingleOrDefault(item => item.OperationId == requested.OperationId);
        var profile = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == requested.ProfileId);
        if (run is null || profile is null || run.Kind != GameKinds.MinecraftBedrock ||
            profile.Kind != GameKinds.MinecraftBedrock || run.ProfileId != profile.Id ||
            run.StopRequestedUtc is not null || run.ProcessId != requested.ProcessId ||
            run.StartTimeUtcTicks != requested.StartTimeUtcTicks ||
            run.WorldId != requested.WorldId || run.WorldId != profile.WorldId ||
            !SamePath(run.WorldDirectory, requested.WorldDirectory) ||
            !SamePath(run.WorldDirectory, profile.WorldDirectory) ||
            !SamePath(run.ExecutablePath, requested.ExecutablePath) ||
            !SamePath(run.ExecutablePath, profile.ExecutablePath))
            throw new InvalidOperationException("The exact Bedrock managed run and profile could not be verified.");
        return run;
    }

    private static Process ExactProcess(ManagedRun run)
    {
        Process process;
        try { process = Process.GetProcessById(run.ProcessId!.Value); }
        catch (ArgumentException ex)
        { throw new InvalidOperationException("The exact Bedrock process could not be verified.", ex); }
        try
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                SamePath(process.MainModule?.FileName, run.ExecutablePath)) return process;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or NotSupportedException)
        {
            process.Dispose();
            throw new InvalidOperationException("The exact Bedrock process could not be verified.", ex);
        }
        process.Dispose();
        throw new InvalidOperationException("The exact Bedrock process could not be verified.");
    }

    private BedrockPendingResume RequirePending(ManagedRun run)
    {
        BedrockPendingResume? marker;
        try
        {
            var info = new FileInfo(markerPath);
            if (!info.Exists || info.Length is <= 0 or > 4096 ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException();
            marker = JsonSerializer.Deserialize<BedrockPendingResume>(File.ReadAllBytes(markerPath), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        { throw new InvalidOperationException("Pending Bedrock resume needs manual review.", ex); }
        if (marker is null || marker.AttemptNonce == Guid.Empty ||
            marker.ProfileId != run.ProfileId || marker.OperationId != run.OperationId ||
            marker.ProcessId != run.ProcessId || marker.StartTimeUtcTicks != run.StartTimeUtcTicks ||
            !SamePath(marker.ExecutablePath, run.ExecutablePath) ||
            !SamePath(marker.WorldDirectory, run.WorldDirectory))
            throw new InvalidOperationException("Pending Bedrock resume belongs to another run.");
        return marker;
    }

    private static BedrockValidatedSnapshot Validate(ManagedRun run, BedrockQueryEvidence evidence)
    {
        if (evidence.OperationId != run.OperationId || evidence.Source != BedrockQuerySource.FixtureSynthetic ||
            evidence.Files is null || evidence.Files.Count is < 1 or > 4096)
            throw new InvalidDataException("Bedrock query evidence is missing or belongs to another run.");
        if (string.IsNullOrWhiteSpace(run.WorldId) || run.WorldId is "." or ".." ||
            run.WorldId.Contains('/') || run.WorldId.Contains('\\') || run.WorldId.Contains(':'))
            throw new InvalidDataException("Bedrock world name is invalid.");
        var serverRoot = Path.GetFullPath(run.WorldDirectory);
        var worldsRoot = Path.Combine(serverRoot, "worlds");
        var world = Path.Combine(worldsRoot, run.WorldId);
        if (new[] { serverRoot, worldsRoot, world }.Any(path =>
                !Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Bedrock world root is unavailable.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in evidence.Files)
        {
            var name = file.FileName;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 1024 ||
                Path.IsPathRooted(name) || name.Contains(':') || name.Contains('\\') ||
                name.Split('/').Any(part => part is "" or "." or "..") ||
                !names.Add(name) || file.FileSize is < 0 or > 2_147_483_648L)
                throw new InvalidDataException("Bedrock query path or size is invalid.");
            total = checked(total + file.FileSize);
            if (total > 8L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Bedrock query exceeds candidate bounds.");
            var path = world;
            foreach (var part in name.Split('/'))
            {
                path = Path.Combine(path, part);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Bedrock query contains a filesystem link.");
            }
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.FileSize)
                throw new InvalidDataException("Bedrock query file size changed.");
        }
        return new(run.OperationId, evidence.Files.ToArray());
    }

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
