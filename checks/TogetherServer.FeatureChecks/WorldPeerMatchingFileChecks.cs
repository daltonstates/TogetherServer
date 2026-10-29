using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Generated signed saves and existing in-memory HTTP dispatch only. No app,
// socket, process, game, browser or production storage is accessed.
internal static partial class WorldPeerMatchingFileChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync(string parent)
    {
        await PullAsync(parent);
        await SeedAsync(parent);
        await ExactPathAsync(parent);
        await SeedFirstPullBudgetAsync(parent);
        PathAliases(parent);
        foreach (var seed in new[] { false, true })
        {
            await ResumeAsync(parent, seed);
            await NonPrefixStagedProgressAsync(parent, seed);
            await RetainedPrefixStillNeedsHashAsync(parent, seed);
            await CancellationRecheckRefusalAsync(parent, seed);
            await MissingFileAsync(parent, seed);
            foreach (var target in new[] { false, true }) await LinkedFileAsync(parent, seed, target);
            foreach (var shape in new[] { "SourcePayload", "SourceAncestor", "TargetPayload", "TargetAncestor" })
                await ReparseAsync(parent, seed, shape);
            foreach (var mutation in new[] { "Payload", "Consent", "Roster", "Grant", "SourceAncestry", "TargetAncestry" })
                await RefusalAsync(parent, seed, mutation);
        }
    }

    private static async Task ExactPathAsync(string parent)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication(caseOnlyPath: true);
        await fixture.PullAsync();
        VerifyCompleted(fixture);
        var paths = PayloadRequests(fixture, seed: false).Select(request => fixture.Descriptor.Version.Files[request.FileIndex!.Value].Path)
            .Order(StringComparer.Ordinal).ToArray();
        Require(paths.SequenceEqual(new[] { "World.dat", "added.dat", "changed.dat" }),
            "Matching-file reuse folded case instead of requiring an exact signed slash-normalized path.");
    }

    private static void PathAliases(string parent)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        var prior = fixture.PrepareMatchingPublication();
        var file = prior.Version.Files.Single(item => item.Path == "world.dat");
        try
        {
            typeof(WorldPeerService).GetMethod("MatchingFiles", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [prior.Version with { Files = [.. prior.Version.Files, file with { Path = "World.dat" }] }, fixture.Descriptor.Version]);
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { return; }
        throw new InvalidOperationException("Ambiguous signed-path aliases were used for local file reuse.");
    }

    private static async Task PullAsync(string parent)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        var prior = fixture.PrepareMatchingPublication();
        var sourceBefore = SourceHashes(fixture);
        var copied = 0;
        fixture.TargetService.MatchingFileCopyingForChecks = bytes => { if (bytes > 0) copied++; };
        await fixture.SyncExactAsync();
        var requests = PayloadRequests(fixture, seed: false);
        var expected = ChangedBytes(fixture);
        Require(copied > 0 && RequestsOnlyChanged(fixture, requests) && WireBytes(fixture, requests) == expected &&
            expected < SharedWorldService.BoundedTotalBytes(prior.Version.Files) && SourceHashes(fixture) == sourceBefore,
            "Pull did not reuse same-path signed unchanged files or transferred an unrelated/equal-byte renamed file locally.");
        var progress = fixture.TargetService.Views().Single().BackgroundTransfers!.Single().LastResult;
        Require(progress is not null && progress.ReceivedBytes == expected,
            "Locally reused bytes were counted as received network payload.");
        VerifyCompleted(fixture);
        var transferred = requests.Length;
        var localCopies = copied;
        await fixture.PullAsync();
        Require(PayloadRequests(fixture, seed: false).Length == transferred && copied == localCopies && SourceHashes(fixture) == sourceBefore,
            "An unchanged follow-up pulled or recopied a newly completed exact publication.");
    }

    private static async Task SeedAsync(string parent)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication();
        var sourceBefore = SourceHashes(fixture);
        var copied = 0;
        var inventories = 0;
        var chunkInventories = new List<int>();
        fixture.TargetService.LocalSaveInventoryForChecks = () => inventories++;
        fixture.TargetService.MatchingFileCopyingForChecks = bytes => { if (bytes > 0) copied++; };
        var offer = await fixture.SeedAsync();
        Require(offer is { Code: "SeedChunk", Receipt: null } && copied > 0 &&
            !File.Exists(Path.Combine(fixture.IncomingDirectory, "receipt.json")) &&
            !File.Exists(Path.Combine(fixture.DuplicateVault, "latest.json")),
            "Subset hydration acknowledged an incomplete target or bypassed its ordinary completion path.");
        var initialCopies = copied;
        fixture.SourceHandler.BeforeExchange = exchange =>
        {
            if (exchange.Request is { Operation: WorldPeerOperations.Seed, FileIndex: not null })
                chunkInventories.Add(inventories);
        };
        await fixture.SeedAllAsync();
        var requests = PayloadRequests(fixture, seed: true);
        Require(RequestsOnlyChanged(fixture, requests) && WireBytes(fixture, requests) == ChangedBytes(fixture) &&
            copied == initialCopies && chunkInventories.Count == requests.Length && chunkInventories.Distinct().Count() == 1 &&
            fixture.SourceHandler.SeedResponses.Where(response => response.Code != "SeedComplete" && response.Code != "SeedKnown")
                .All(response => response.Receipt is null) && SourceHashes(fixture) == sourceBefore,
            "Seed requested unchanged bytes, repeated source hydration/inventory per chunk, or acknowledged a partial copy.");
        VerifyCompleted(fixture);
        var transferred = requests.Length;
        await fixture.SeedAllAsync();
        Require(PayloadRequests(fixture, seed: true).Length == transferred && copied == initialCopies && SourceHashes(fixture) == sourceBefore,
            "An unchanged Seed follow-up sent or recopied completed payload.");
    }

    private static async Task ResumeAsync(string parent, bool seed)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication();
        var sourceBefore = SourceHashes(fixture);
        var unchanged = fixture.Descriptor.Version.Files.Single(file => file.Path == "nested/unchanged.bin");
        var source = File.ReadAllBytes(SharedWorldService.SafeChild(Path.Combine(fixture.DirectoryPath, SharedWorldService.PayloadDirectory), unchanged.Path));
        var prefix = source[..19];
        var stagePath = SharedWorldService.SafeChild(fixture.IncomingStage, unchanged.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(stagePath)!); File.WriteAllBytes(stagePath, prefix);
        using var canceled = new CancellationTokenSource();
        BackgroundJob? intermediate = null;
        fixture.TargetService.MatchingFileCopyingForChecks = bytes =>
        {
            if (bytes < 512 * 1024) return;
            intermediate = BackgroundJobs.For(fixture.Target).Read().Jobs.Single(job => job.Operation == "Save download");
            canceled.Cancel();
        };
        await RejectAsync(async () =>
        {
            if (seed) await fixture.SeedAllAsync(canceled.Token);
            else await fixture.PullWithCancellationAsync(canceled.Token);
        }, "An interrupted local matching-file copy completed: " + seed);
        var retained = File.ReadAllBytes(stagePath);
        Require(intermediate is { Phase: "Preparing", ReceivedBytes: > 0 } && intermediate.ReceivedBytes < intermediate.TotalBytes &&
            retained.Length >= SharedWorldService.ChunkBytes && retained.Length % SharedWorldService.ChunkBytes == 0 &&
            retained.AsSpan(0, prefix.Length).SequenceEqual(prefix) && retained.AsSpan().SequenceEqual(source.AsSpan(0, retained.Length)) &&
            SourceHashes(fixture) == sourceBefore &&
            !File.Exists(Path.Combine(fixture.DuplicateVault, "latest.json")) && PayloadRequests(fixture, seed).Length == 0,
            "Preparing progress or safely retained canceled chunks lost the existing prefix/source or acknowledged/sent partial payload: " + seed);
        var paused = BackgroundJobs.For(fixture.Target).Read().Jobs.Single(job => job.Operation == "Save download");
        Require(paused.ReceivedBytes == retained.Length && paused.State != "Succeeded",
            "Canceled staging failed to report its retained unverified byte baseline: " + seed);
        fixture.TargetService.MatchingFileCopyingForChecks = null;
        if (seed) await fixture.SeedAllAsync(); else await fixture.PullAsync();
        VerifyCompleted(fixture);
        var resumedRequests = PayloadRequests(fixture, seed).Where(request =>
            fixture.Descriptor.Version.Files[request.FileIndex!.Value].Path == unchanged.Path).ToArray();
        Require(resumedRequests.Length > 0 && resumedRequests.All(request => request.ChunkOffset >= retained.Length) &&
            SourceHashes(fixture) == sourceBefore,
            "Resuming canceled local staging recopied a growing prefix or changed its source instead of using bounded signed chunks: " + seed);
    }

    private static async Task MissingFileAsync(string parent, bool seed)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication();
        File.Delete(SharedWorldService.SafeChild(Path.Combine(fixture.DirectoryPath, SharedWorldService.PayloadDirectory), "nested/unchanged.bin"));
        if (seed) await fixture.SeedAllAsync(); else await fixture.PullAsync();
        VerifyCompleted(fixture);
        var requests = PayloadRequests(fixture, seed);
        Require(requests.Any(request => fixture.Descriptor.Version.Files[request.FileIndex!.Value].Path == "nested/unchanged.bin") &&
            !requests.Any(request => fixture.Descriptor.Version.Files[request.FileIndex!.Value].Path == "world.dat"),
            "A missing optional local source file was acknowledged or prevented normal verified network fallback: " + seed);
    }

    private static async Task RefusalAsync(string parent, bool seed, string mutation)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        var prior = fixture.PrepareMatchingPublication();
        var oldPointer = File.ReadAllBytes(Path.Combine(fixture.Target.RootPath, "shared-worlds", fixture.Root.ProfileId.ToString("N"), "latest.json"));
        var mutated = false;
        fixture.TargetService.MatchingFileCopyingForChecks = bytes =>
        {
            if (mutated || bytes != 0) return;
            mutated = true;
            if (mutation == "Payload")
            {
                var path = Path.Combine(fixture.DirectoryPath, SharedWorldService.PayloadDirectory, "nested", "unchanged.bin");
                var bytesBefore = File.ReadAllBytes(path); bytesBefore[^1] ^= 1; File.WriteAllBytes(path, bytesBefore);
            }
            else if (mutation == "Consent") Require(fixture.TargetService.SetReceivingAsync(fixture.Root.GroupKey, false, default).Result.Ok, "Synthetic consent withdrawal failed.");
            else if (mutation == "Roster") fixture.AdvanceRoster(receive: true);
            else if (mutation == "Grant") fixture.AdvanceRoster(receive: false);
            else if (mutation == "SourceAncestry")
                File.WriteAllText(Path.Combine(fixture.Target.RootPath, "shared-worlds", fixture.Root.ProfileId.ToString("N"),
                    fixture.Root.GroupId.ToString("N"), "1", "version.json"), "{changed source parent");
            else if (mutation == "TargetAncestry")
            {
                foreach (var path in Directory.EnumerateFiles(Path.Combine(fixture.DuplicateVault, "signed-history"),
                    "*" + prior.Version.VersionHash + "*.json", SearchOption.AllDirectories)) File.Delete(path);
            }
        };
        await RejectAsync(async () =>
        {
            if (seed) await fixture.SeedAllAsync(); else await fixture.PullAsync();
        }, "Changed matching-file scope/source/history completed: " + seed + "/" + mutation);
        Require(mutated && !File.Exists(Path.Combine(fixture.IncomingDirectory, "receipt.json")) &&
            !File.Exists(Path.Combine(fixture.DuplicateVault, "latest.json")) &&
            File.ReadAllBytes(Path.Combine(fixture.Target.RootPath, "shared-worlds", fixture.Root.ProfileId.ToString("N"), "latest.json")).SequenceEqual(oldPointer),
            "A failed local reuse created a receipt, promoted a target or rewrote its original source head: " + seed + "/" + mutation);
    }

    private static async Task LinkedFileAsync(string parent, bool seed, bool target)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication();
        var sourcePath = SharedWorldService.SafeChild(Path.Combine(fixture.DirectoryPath, SharedWorldService.PayloadDirectory), "nested/unchanged.bin");
        var original = File.ReadAllBytes(sourcePath);
        var outside = Path.Combine(fixture.Target.RootPath, "generated-link-original.dat");
        File.WriteAllBytes(outside, target ? original[..19] : original);
        var linked = target ? SharedWorldService.SafeChild(fixture.IncomingStage, "nested/unchanged.bin") : sourcePath;
        Directory.CreateDirectory(Path.GetDirectoryName(linked)!);
        if (File.Exists(linked)) File.Delete(linked);
        Require(CreateHardLink(@"\\?\" + Path.GetFullPath(linked), @"\\?\" + Path.GetFullPath(outside), IntPtr.Zero),
            "The generated matching-file hard-link fixture could not be created.");
        var before = File.ReadAllBytes(outside);
        await RejectAsync(async () => { if (seed) await fixture.SeedAllAsync(); else await fixture.PullAsync(); },
            "A linked matching source/target file was accepted: " + seed + "/" + target);
        Require(File.ReadAllBytes(outside).SequenceEqual(before) &&
            !File.Exists(Path.Combine(fixture.IncomingDirectory, "receipt.json")) &&
            !File.Exists(Path.Combine(fixture.DuplicateVault, "latest.json")),
            "Linked-file refusal changed the external generated bytes or acknowledged a target.");
    }

    private static async Task ReparseAsync(string parent, bool seed, string shape)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication();
        var outside = Path.Combine(fixture.Target.RootPath, "generated-reuse-redirect");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.dat"); File.WriteAllBytes(marker, [7, 8, 9]);
        var path = shape switch
        {
            "SourcePayload" => Path.Combine(fixture.DirectoryPath, SharedWorldService.PayloadDirectory),
            "SourceAncestor" => fixture.DirectoryPath,
            "TargetPayload" => fixture.IncomingStage,
            _ => Path.GetDirectoryName(fixture.IncomingStage)!
        };
        var attempted = false;
        var redirected = false;
        Exception? probeFailure = null;
        fixture.TargetService.MatchingFileCopyingForChecks = bytes =>
        {
            if (attempted || bytes != 0) return;
            attempted = true;
            try { redirected = TryMountPoint(fixture.Target.RootPath, path, outside); }
            catch (Exception error) { probeFailure = error; }
        };
        Exception? failure = null;
        try
        {
            try { if (seed) await fixture.SeedAllAsync(); else await fixture.PullAsync(); }
            catch (Exception error) when (error is InvalidDataException or IOException or CryptographicException or JsonException or OperationCanceledException)
            { failure = error; }
            Require(attempted && probeFailure is null && File.ReadAllBytes(marker).SequenceEqual(new byte[] { 7, 8, 9 }) &&
                Directory.EnumerateFileSystemEntries(outside).SequenceEqual(new[] { marker }),
                "The matching-file namespace probe was skipped or touched its generated redirect target: " + shape);
            if (redirected)
                Require(failure is not null && !File.Exists(Path.Combine(fixture.DuplicateVault, "latest.json")),
                    "An attribute-only matching-file namespace redirect was accepted: " + shape);
            else
            {
                Require(failure is null, "A safely refused generated reparse probe blocked normal reuse: " + shape);
                VerifyCompleted(fixture);
            }
        }
        finally
        {
            if (redirected)
            {
                Require(AppInstance.ContainsPath(fixture.Target.RootPath, path) &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0,
                    "The generated reparse path escaped its exact nonrecursive cleanup scope.");
                Directory.Delete(path, recursive: false);
            }
        }
    }

    private static bool TryMountPoint(string root, string path, string target)
    {
        Require(AppInstance.ContainsPath(root, path) && AppInstance.ContainsPath(root, target) &&
            Path.IsPathFullyQualified(path) && Path.IsPathFullyQualified(target),
            "The generated matching-file namespace probe escaped disposable storage.");
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = OpenReparseProbe(@"\\?\" + path, 0x100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        var applied = !handle.IsInvalid && SetReparseProbe(handle, 0x000900A4, buffer,
            checked((uint)buffer.Length), IntPtr.Zero, 0, out _, IntPtr.Zero);
        if (applied) return true;
        var error = Marshal.GetLastWin32Error();
        if (error is 5 or 32 or 145 or 1314) return false;
        throw new Win32Exception(error, "The generated matching-file reparse probe failed unexpectedly.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenReparseProbe(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetReparseProbe(SafeFileHandle handle, uint operation, byte[] input,
        uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);

    private static void VerifyCompleted(WorldPeerExistingCopyChecks.Fixture fixture)
    {
        var receipt = JsonSerializer.Deserialize<SharedWorldReceipt>(File.ReadAllBytes(Path.Combine(fixture.IncomingDirectory, "receipt.json")), Json)!;
        Require(fixture.ValidReceipt(receipt) && JsonSerializer.Deserialize<SharedWorldVersion>(
            File.ReadAllBytes(Path.Combine(fixture.DuplicateVault, "latest.json")), Json)?.VersionHash == fixture.Descriptor.Version.VersionHash,
            "The new publication did not obtain its own exact current verified receipt/pointer.");
        foreach (var file in fixture.Descriptor.Version.Files)
            WorldPeerPayloadHash.VerifyFile(SharedWorldService.SafeChild(Path.Combine(fixture.IncomingDirectory, SharedWorldService.PayloadDirectory), file.Path), file, default);
    }

    private static WorldPeerRequest[] PayloadRequests(WorldPeerExistingCopyChecks.Fixture fixture, bool seed) =>
        (seed ? fixture.SourceHandler : fixture.RemoteHandler).Requests.Where(request =>
            request.VersionHash == fixture.Descriptor.Version.VersionHash && request.FileIndex is not null &&
            request.Operation == (seed ? WorldPeerOperations.Seed : WorldPeerOperations.Chunk)).ToArray();
    private static bool RequestsOnlyChanged(WorldPeerExistingCopyChecks.Fixture fixture, WorldPeerRequest[] requests) =>
        requests.Length == 2 && requests.Select(request => fixture.Descriptor.Version.Files[request.FileIndex!.Value].Path)
            .Order(StringComparer.Ordinal).SequenceEqual(new[] { "added.dat", "changed.dat" });
    private static long WireBytes(WorldPeerExistingCopyChecks.Fixture fixture, WorldPeerRequest[] requests) => requests.Sum(request =>
        Math.Min(SharedWorldService.ChunkBytes, fixture.Descriptor.Version.Files[request.FileIndex!.Value].Length - request.ChunkOffset!.Value));
    private static long ChangedBytes(WorldPeerExistingCopyChecks.Fixture fixture) => fixture.Descriptor.Version.Files
        .Where(file => file.Path is "added.dat" or "changed.dat").Sum(file => file.Length);
    private static string SourceHashes(WorldPeerExistingCopyChecks.Fixture fixture) => string.Join('|',
        Directory.EnumerateFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(fixture.DirectoryPath, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or CryptographicException or JsonException or OperationCanceledException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require([DoesNotReturnIf(false)] bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
