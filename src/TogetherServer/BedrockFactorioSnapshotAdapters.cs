using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TogetherServer;

// Reviewed internal grammars. They provide synthetic adapter evidence, never
// game acceptance; an unknown game version/output shape fails closed.
internal static class BedrockHeldSnapshotGrammar
{
    internal const string QueryReady = "Data saved. Files are now ready to be copied.";
    internal const string ResumeAcknowledged = "Changes to the world are resumed.";
    internal const int MaximumQueryCharacters = 64 * 1024;
    internal const int MaximumFiles = 4096;
    private static readonly Regex Prefix = new(@"\A\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{3})? INFO\] ",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    internal static string? TrustedBody(string physicalLine, DateTimeOffset requestedUtc)
    {
        if (!MinecraftCapturedLogFrame.TryDecode(Encoding.UTF8.GetBytes(physicalLine), out var frame) ||
            frame is not { Stream: "Stdout", Truncated: false } || frame.CapturedUtc < requestedUtc ||
            frame.CapturedUtc > DateTimeOffset.UtcNow.AddSeconds(5) ||
            frame.Message.Length > MaximumQueryCharacters || frame.Message.Any(char.IsControl)) return null;
        var prefix = Prefix.Match(frame.Message);
        return prefix.Success ? frame.Message[prefix.Length..] : null;
    }

    internal static IReadOnlyList<ManagedLiveSnapshotFile> ParseFiles(ServerProfile profile, string body)
    {
        if (profile.Kind != GameKinds.MinecraftBedrock || !ValidWorldName(profile.WorldId) ||
            string.IsNullOrEmpty(body) || body.Length > MaximumQueryCharacters || body.Any(char.IsControl))
            throw new InvalidDataException("The Bedrock held-file query is invalid.");
        // Only the reviewed server-relative prefix is supported. It never
        // selects a root: Stage resolves the suffix through the saved driver.
        var prefix = "worlds/" + profile.WorldId + "/";
        var entries = body.Split(", ", StringSplitOptions.None);
        if (entries.Length is < 1 or > MaximumFiles)
            throw new InvalidDataException("The Bedrock held-file query exceeds its bounds.");
        var files = new List<ManagedLiveSnapshotFile>(entries.Length);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in entries)
        {
            var colon = entry.LastIndexOf(':');
            if (!entry.StartsWith(prefix, StringComparison.Ordinal) || colon <= prefix.Length ||
                entry.AsSpan(0, colon).Contains(':') ||
                !long.TryParse(entry.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length is < 0 or > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("The Bedrock held-file path or length is invalid.");
            var relative = entry[prefix.Length..colon];
            if (!ValidRelativePath(relative) || !names.Add(relative))
                throw new InvalidDataException("The Bedrock held-file query contains an unsafe or duplicate path.");
            total = checked(total + length);
            if (total > 8L * 1024 * 1024 * 1024)
                throw new InvalidDataException("The Bedrock held-file query exceeds its byte limit.");
            files.Add(new(relative, length));
        }
        return files;
    }

    internal static BedrockOwnedResumeAcknowledgement? ResumeEvidence(string physicalLine, BedrockPendingResume marker)
    {
        if (marker.ResumeRequestedUtc is not { } requested || !marker.ResumeDispatched ||
            TrustedBody(physicalLine, requested) != ResumeAcknowledged ||
            !MinecraftCapturedLogFrame.TryDecode(Encoding.UTF8.GetBytes(physicalLine), out var frame) || frame is null) return null;
        return new(marker.ProfileId, marker.OperationId, marker.ProcessId, marker.StartTimeUtcTicks,
            marker.AttemptNonce, requested, frame.CapturedUtc);
    }

    private static bool ValidWorldName(string world) => !string.IsNullOrWhiteSpace(world) && world.Length <= 64 &&
        world is not ("." or "..") && !world.EndsWith('.') && !world.EndsWith(' ') &&
        !world.Any(character => char.IsControl(character) || "/\\:,\"<>|?*".Contains(character));

    private static bool ValidRelativePath(string path) => path.Length is > 0 and <= 1024 &&
        !Path.IsPathRooted(path) && !path.Any(character => char.IsControl(character) || "\\:,\"<>|?*".Contains(character)) &&
        path.Split('/') is { Length: <= 64 } parts &&
        parts.All(part => part.Length is > 0 and <= 255 && part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' '));
}

internal sealed class FactorioSaveCompletionGrammar(ServerProfile profile)
{
    private static readonly Regex Line = new(@"\A\s*[0-9]+\.[0-9]{3} Info (?:AppManager|AppManagerStates|MainLoop)\.cpp:[0-9]+: (?<body>[^\r\n]+)\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private readonly string target = ReviewedTarget(profile);
    private bool started;
    private bool completed;
    internal string Target => target;

    internal bool Observe(string line)
    {
        if (line.Length > 4096 || line.Any(character => char.IsControl(character) && character != '\t'))
            throw new InvalidDataException("The Factorio save completion line is invalid.");
        var parsed = Line.Match(line);
        if (!parsed.Success) return false;
        var body = parsed.Groups["body"].Value;
        var start = body.StartsWith("Saving game as ", StringComparison.Ordinal) ? "Saving game as " :
            body.StartsWith("Saving map as ", StringComparison.Ordinal) ? "Saving map as " : null;
        if (start is not null)
        {
            if (started || completed || !SamePath(body[start.Length..], target))
                throw new InvalidDataException("The Factorio save names a different or ambiguous target.");
            started = true;
        }
        else if (body.StartsWith("Auto saving map as ", StringComparison.Ordinal))
            throw new InvalidDataException("The Factorio save is not the reviewed requested target sequence.");
        else if (body == "Saving finished")
        {
            if (!started || completed)
                throw new InvalidDataException("The Factorio completion has no unique matching save request.");
            completed = true;
        }
        return completed;
    }

    internal static string ReviewedTarget(ServerProfile profile)
    {
        if (profile.Kind != GameKinds.Factorio || profile.Id == Guid.Empty ||
            !ValheimSetup.ValidWorldId(profile.WorldId) || string.IsNullOrWhiteSpace(profile.WorldDirectory))
            throw new InvalidDataException("The saved Factorio archive target is invalid.");
        return Path.Combine(Path.GetFullPath(profile.WorldDirectory), profile.WorldId + ".zip");
    }

    private static bool SamePath(string observed, string expected) => Path.IsPathFullyQualified(observed) &&
        !observed.Any(char.IsControl) && observed.IndexOfAny(['"', '<', '>', '|']) < 0 &&
        !observed.Split(['/', '\\']).Any(part => part is "." or "..") &&
        Path.GetFullPath(observed).Equals(expected, StringComparison.OrdinalIgnoreCase);
}

internal sealed class BedrockManagedSnapshotAdapter(LocalData data, GameServerRegistry games) : IManagedLiveSnapshotAdapter
{
    public string Game => GameKinds.MinecraftBedrock;
    public bool LiveCaptureAccepted => SharedWorldLiveSaveAdapters.IsGameAccepted(Game);
    private readonly ManagedLiveSnapshotStore snapshots = new(data, games);

    public async Task<ImmutableLiveSaveSnapshot> CaptureAsync(ServerProfile profile, ManagedRun run,
        CancellationToken cancellationToken, Guid? snapshotId = null)
    {
        if (profile.Kind != Game || run.Kind != Game || profile.Id != run.ProfileId)
            throw new InvalidDataException("An exact saved Bedrock profile is required.");
        var candidate = new BedrockLiveSaveCandidate(data);
        if (candidate.HasPendingResume)
            throw new InvalidOperationException("The earlier Bedrock hold must be resumed before another snapshot.");
        var nonce = Guid.NewGuid();
        BedrockPendingResume? ownedHold = null;
        ImmutableLiveSaveSnapshot? snapshot = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidate.Hold(run, nonce, marker => ownedHold = marker);
            using var cursor = ManagedSaveLogCursor.Open(data, games, profile, run);
            var requestedUtc = DateTimeOffset.UtcNow;
            candidate.Query(run);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var ready = false;
            IReadOnlyList<ManagedLiveSnapshotFile>? files = null;
            while (files is null)
            {
                foreach (var line in cursor.ReadNewLines(profile, run, timeout.Token))
                {
                    var body = BedrockHeldSnapshotGrammar.TrustedBody(line, requestedUtc);
                    if (body == BedrockHeldSnapshotGrammar.QueryReady)
                    {
                        if (ready) throw new InvalidDataException("The held Bedrock query is ambiguous.");
                        ready = true;
                    }
                    else if (ready && body is not null)
                    {
                        if (files is not null)
                            throw new InvalidDataException("The held Bedrock query has more than one file-list reply.");
                        files = BedrockHeldSnapshotGrammar.ParseFiles(profile, body);
                    }
                }
                if (files is null) await Task.Delay(100, timeout.Token);
            }
            var completion = new LiveSaveCompletionEvidence(profile.Id, run.OperationId,
                run.ProcessId!.Value, run.StartTimeUtcTicks!.Value, LiveSaveEvidence.FrozenSnapshotQuery,
                DateTimeOffset.UtcNow, true);
            snapshot = snapshots.Stage(profile, run, completion, files, cancellationToken, reservedSnapshotId: snapshotId);
            return snapshot;
        }
        finally
        {
            // A failed hold dispatch can already have reached the game. Only
            // this attempt's durable marker may drive the mandatory resume.
            if (ownedHold is not null)
            {
                try
                {
                    using var resumeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    if (!await RecoverPendingResumeAsync(profile, run, resumeTimeout.Token, ownedHold))
                        throw new InvalidDataException("Bedrock resume is unacknowledged; its durable marker remains.");
                }
                catch
                {
                    if (snapshot is not null) snapshots.Discard(snapshot);
                    throw;
                }
            }
        }
    }

    internal Task<bool> ResumeAsync(ServerProfile profile, ManagedRun run, CancellationToken cancellationToken)
        => RecoverPendingResumeAsync(profile, run, cancellationToken);

    private async Task<bool> RecoverPendingResumeAsync(ServerProfile profile, ManagedRun run,
        CancellationToken cancellationToken, BedrockPendingResume? ownedHold = null)
    {
        if (profile.Kind != Game || run.Kind != Game || profile.Id != run.ProfileId) return false;
        var candidate = new BedrockLiveSaveCandidate(data);
        if (!candidate.HasPendingResume && ownedHold is null) return false;
        ManagedSaveLogCursor? cursor = null;
        BedrockPendingResume marker;
        try
        {
            // Resume is attempted even if a missing/damaged log prevents an
            // acknowledgement. Its marker is retained for owner recovery.
            try { cursor = ManagedSaveLogCursor.Open(data, games, profile, run); }
            finally { marker = ownedHold is null ? candidate.Resume(run) : candidate.ResumeOwnedAttempt(run, ownedHold); }
            if (cursor is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                foreach (var line in cursor.ReadNewLines(profile, run, timeout.Token))
                {
                    var acknowledged = BedrockHeldSnapshotGrammar.ResumeEvidence(line, marker);
                    if (acknowledged is null) continue;
                    candidate.AcknowledgeOwnedResume(run, acknowledged);
                    return true;
                }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
            UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException or ArgumentException)
        { return false; }
        finally { cursor?.Dispose(); }
    }
}

internal sealed class FactorioManagedSnapshotAdapter(LocalData data, GameServerRegistry games) : IManagedLiveSnapshotAdapter
{
    public string Game => GameKinds.Factorio;
    public bool LiveCaptureAccepted => SharedWorldLiveSaveAdapters.IsGameAccepted(Game);
    private readonly ManagedLiveSnapshotStore snapshots = new(data, games);

    public async Task<ImmutableLiveSaveSnapshot> CaptureAsync(ServerProfile profile, ManagedRun run,
        CancellationToken cancellationToken, Guid? snapshotId = null)
    {
        if (profile.Kind != Game || run.Kind != Game || profile.Id != run.ProfileId)
            throw new InvalidDataException("An exact saved Factorio profile is required.");
        var exact = ExactManagedFactorioRun.Require(data, run, requireReady: true);
        var grammar = new FactorioSaveCompletionGrammar(exact.Profile);
        using var cursor = ManagedSaveLogCursor.Open(data, games, profile, run);
        cancellationToken.ThrowIfCancellationRequested();
        var dispatched = new ExactManagedFactorioLiveSaveCommandPort(data).RequestFactorioSave(run);
        if (dispatched.OperationId != run.OperationId || dispatched.FixedCommand != "/server-save")
            throw new InvalidDataException("The Factorio save dispatch belongs to another run.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var complete = false;
        while (!complete)
        {
            foreach (var line in cursor.ReadNewLines(profile, run, timeout.Token))
                complete = grammar.Observe(line) || complete;
            if (!complete) await Task.Delay(100, timeout.Token);
        }
        var completion = new LiveSaveCompletionEvidence(profile.Id, run.OperationId,
            run.ProcessId!.Value, run.StartTimeUtcTicks!.Value, LiveSaveEvidence.ClosedSaveArchive,
            DateTimeOffset.UtcNow, true);
        // Compatible read leases deny new writers/deletion while allowing the
        // common store to hold its own source lease through sealing.
        using var archive = new FileStream(grammar.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var inspection = FactorioClosedArchiveCandidate.InspectArchiveContents(archive, cancellationToken);
        var before = Hash(archive, cancellationToken);
        ImmutableLiveSaveSnapshot? snapshot = null;
        try
        {
            snapshot = snapshots.Stage(profile, run, completion,
                cancellationToken: cancellationToken, reservedSnapshotId: snapshotId);
            if (snapshot.Files.Count != 1 || snapshot.Files[0].Path != profile.WorldId + ".zip" ||
                snapshot.Files[0].Length != inspection.ArchiveLength || snapshot.Files[0].Sha256 != Convert.ToHexString(before) ||
                !before.AsSpan().SequenceEqual(Hash(archive, cancellationToken)) ||
                !snapshots.Verify(profile, run, snapshot, cancellationToken))
                throw new InvalidDataException("The completed Factorio archive changed during its snapshot.");
            _ = ExactManagedFactorioRun.Require(data, run, requireReady: true);
            return snapshot;
        }
        catch
        {
            if (snapshot is not null) snapshots.Discard(snapshot);
            throw;
        }
    }

    private static byte[] Hash(FileStream file, CancellationToken cancellationToken)
    {
        file.Position = 0;
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = file.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer.AsSpan(0, read));
        }
        return hash.GetHashAndReset();
    }
}
