using System.Reflection;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static partial class WorldPeerMatchingFileChecks
{
    private static async Task CancellationRecheckRefusalAsync(string parent, bool seed)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication();
        var pointer = Path.Combine(fixture.Target.RootPath, "shared-worlds", fixture.Root.ProfileId.ToString("N"), "latest.json");
        var originalPointer = File.ReadAllBytes(pointer);
        var sourceBefore = SourceHashes(fixture);
        var prefix = File.ReadAllBytes(SharedWorldService.SafeChild(Path.Combine(fixture.DirectoryPath,
            SharedWorldService.PayloadDirectory), "nested/unchanged.bin"))[..19];
        var stage = SharedWorldService.SafeChild(fixture.IncomingStage, "nested/unchanged.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        File.WriteAllBytes(stage, prefix);
        using var canceled = new CancellationTokenSource();
        var changed = false;
        fixture.TargetService.MatchingFileCopyingForChecks = count =>
        {
            if (count < 512 * 1024 || changed) return;
            File.WriteAllText(pointer, "{invalid generated current source pointer");
            changed = true;
            canceled.Cancel();
        };
        Exception? refusal = null;
        try
        {
            try
            {
                if (seed) await fixture.SeedAllAsync(canceled.Token);
                else await fixture.PullWithCancellationAsync(canceled.Token);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or System.Text.Json.JsonException or OperationCanceledException)
            { refusal = error; }
            var job = BackgroundJobs.For(fixture.Target).Read().Jobs.Single(item => item.Operation == "Save download");
            Require(changed && refusal is not null && refusal is not OperationCanceledException &&
                job is { State: "Failed", ResultCode: "SaveTransferInvalidData", Phase: "Preparing" } &&
                job.ReceivedBytes == prefix.Length && File.ReadAllBytes(stage).SequenceEqual(prefix) &&
                !File.Exists(Path.Combine(fixture.DuplicateVault, "latest.json")) &&
                !File.Exists(Path.Combine(fixture.IncomingDirectory, "receipt.json")) && SourceHashes(fixture) == sourceBefore,
                "A canceled-prefix source recheck refusal was hidden as a routine pause or retained unsafe appended bytes: " + seed);
        }
        finally { File.WriteAllBytes(pointer, originalPointer); }
    }

    private static async Task SeedFirstPullBudgetAsync(string parent)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication();
        var copying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var held = 0;
        fixture.TargetService.MatchingFileCopyingForChecks = count =>
        {
            if (count < 512 * 1024 || Interlocked.Exchange(ref held, 1) != 0) return;
            copying.TrySetResult();
            Require(release.Wait(TimeSpan.FromSeconds(10)), "The generated seed did not release its local staging lease.");
        };
        fixture.RemoteHandler.AfterExchange = exchange =>
        {
            if (exchange.Request.Operation == WorldPeerOperations.Catalog) catalog.TrySetResult();
        };
        var seed = Task.Run(() => fixture.SeedAllAsync());
        Task? pull = null;
        using var budget = new CancellationTokenSource();
        try
        {
            await copying.Task.WaitAsync(TimeSpan.FromSeconds(10));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var group = typeof(WorldPeerService).GetMethod("Find", flags)!.Invoke(fixture.TargetService, [fixture.Root.GroupKey]);
            pull = Task.Run(async () => await (Task)typeof(WorldPeerService).GetMethod("SyncScheduledGroupAsync", flags)!
                .Invoke(fixture.TargetService, [group, budget.Token, CancellationToken.None, false])!);
            await catalog.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(!pull.IsCompleted && !seed.IsCompleted,
                "The contended pull did not reach its catalog while the seed owned real local staging.");
            budget.Cancel();
            await pull.WaitAsync(TimeSpan.FromSeconds(5));
            var job = BackgroundJobs.For(fixture.Target).Read().Jobs.Single(item => item.Operation == "Save download");
            var result = fixture.TargetService.Views().Single().BackgroundTransfers!.Single().LastResult;
            Require(!seed.IsCompleted && job is { Phase: "Preparing", State: "Running", ReceivedBytes: > 0 } &&
                result is { Result: "WorkBudget", Error: "None", Operation: "Catalog" } &&
                !File.Exists(Path.Combine(fixture.DuplicateVault, "latest.json")),
                "A pull's bounded catalog/receive-gate wait hid or failed the independently owned preparing seed.");
        }
        finally
        {
            release.Set();
            if (pull is not null) await pull;
            await seed;
        }
        VerifyCompleted(fixture);
    }

    private static async Task NonPrefixStagedProgressAsync(string parent, bool seed)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication(forceLargeChanged: true);
        var version = fixture.Descriptor.Version;
        var later = version.Files.Single(file => file.Path == "nested/unchanged.bin");
        var source = SharedWorldService.SafeChild(Path.Combine(Path.GetDirectoryName(fixture.CanonicalReceivedPointer)!,
            version.VersionHash, SharedWorldService.PayloadDirectory), later.Path);
        var target = SharedWorldService.SafeChild(fixture.IncomingStage, later.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var held = 0;
        var handler = seed ? fixture.SourceHandler : fixture.RemoteHandler;
        handler.BeforeExchange = exchange =>
        {
            var payload = seed ? exchange.Request.Operation == WorldPeerOperations.Seed && exchange.Request.FileIndex is not null :
                exchange.Request.Operation == WorldPeerOperations.Chunk;
            if (!payload || Interlocked.Exchange(ref held, 1) != 0) return;
            entered.TrySetResult();
            Require(release.Wait(TimeSpan.FromSeconds(10)), "The staged-progress fixture did not release its generated request.");
        };
        var work = Task.Run(async () => { if (seed) await fixture.SeedAllAsync(); else await fixture.PullAsync(); });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var job = BackgroundJobs.For(fixture.Target).Read().Jobs.Single(item => item.Operation == "Save download");
            Require(!work.IsCompleted && job.Phase == "Receiving" && job.State is ("Running" or "Waiting") &&
                job.ReceivedBytes >= later.Length && job.ReceivedBytes < job.TotalBytes &&
                File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(source)),
                "An early missing file hid completed later staged bytes until its network response finished: " + seed);
        }
        finally { release.Set(); await work; }
        VerifyCompleted(fixture);
    }

    private static async Task RetainedPrefixStillNeedsHashAsync(string parent, bool seed)
    {
        using var fixture = new WorldPeerExistingCopyChecks.Fixture(parent, "Original", matchingFiles: true);
        fixture.PrepareMatchingPublication();
        var before = SourceHashes(fixture);
        using var canceled = new CancellationTokenSource();
        fixture.TargetService.MatchingFileCopyingForChecks = count => { if (count >= 512 * 1024) canceled.Cancel(); };
        await RejectAsync(async () =>
        {
            if (seed) await fixture.SeedAllAsync(canceled.Token);
            else await fixture.PullWithCancellationAsync(canceled.Token);
        }, "Canceled matching-file staging unexpectedly completed.");
        fixture.TargetService.MatchingFileCopyingForChecks = null;
        var path = SharedWorldService.SafeChild(fixture.IncomingStage, "nested/unchanged.bin");
        var bytes = File.ReadAllBytes(path);
        Require(bytes.Length >= SharedWorldService.ChunkBytes, "No bounded unverified prefix survived the generated cancellation.");
        bytes[0] ^= 0xff;
        File.WriteAllBytes(path, bytes);
        await RejectAsync(async () => { if (seed) await fixture.SeedAllAsync(); else await fixture.PullAsync(); },
            "A corrupted retained staging prefix was promoted after bounded network resume.");
        Require(!File.Exists(Path.Combine(fixture.DuplicateVault, "latest.json")) &&
            !File.Exists(Path.Combine(fixture.IncomingDirectory, "receipt.json")) &&
            !File.Exists(Path.Combine(fixture.IncomingDirectory, "version.json")) && SourceHashes(fixture) == before,
            "A retained unverified prefix bypassed final full hashing, receipt safety or source independence: " + seed);
    }
}
