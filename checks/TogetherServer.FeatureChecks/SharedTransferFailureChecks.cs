using System.ComponentModel;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Lease ownership and typed projections use generated identities and counters.
// Actual slow HTTP transfer coverage lives in SlowSaveTransferChecks.
internal static class SharedTransferFailureChecks
{
    internal static Task RunAsync(string parent)
    {
        SurvivingOwner(parent);
        FinalFailurePersists(parent);
        PausesAndNewWorkDoNotComplete(parent);
        IndependentJobLeases(parent);
        TypedSafeFailures();
        PreparingTiming();
        return Task.CompletedTask;
    }

    private static void SurvivingOwner(string parent)
    {
        using var fixture = new Fixture(parent, "surviving-transfer-owner");
        using var first = fixture.Begin();
        using var survivor = fixture.Begin();
        fixture.Report("Receiving", 4);
        var id = fixture.Job().Id;
        SharedSaveTransferProgress.ReceiveFailure(fixture.Data, fixture.Root, fixture.Version.VersionHash,
            new InvalidDataException("Synthetic private payload detail"), default, WorldPeerTransferOperation.Chunk);
        fixture.Clear();
        first.Dispose();
        Require(fixture.Jobs().ActiveCount == 1 && fixture.Job() is { State: "Running", ReceivedBytes: 4 } &&
            SharedSaveTransferProgress.Read(fixture.Data, fixture.Root) is { ReceivedBytes: 4 } && SharedSaveTransferProgress.HasPending(fixture.Data),
            "One failed overlap detached the surviving transfer job or erased measured progress.");
        fixture.Report("Verifying", 8);
        Require(fixture.Job() is { State: "Running", ReceivedBytes: 8, Phase: "Verifying" } && fixture.Job().Id == id,
            "The surviving owner could not advance its original application job.");
        fixture.Report("Receipt", fixture.Total);
        SharedSaveTransferProgress.Complete(fixture.Data, fixture.Root, fixture.Version.VersionHash);
        Require(fixture.Jobs().ActiveCount == 1 && fixture.Job().State == "Running",
            "Verification/receipt completion ended a job while an activity lease was still held.");
        survivor.Dispose();
        Require(fixture.Job() is { State: "Succeeded", ResultCode: "SaveReceived" } && fixture.Jobs().ActiveCount == 0 &&
            SharedSaveTransferProgress.Read(fixture.Data, fixture.Root) is null,
            "Verified surviving work did not finish exactly one shared job after all leases ended.");
    }

    private static void FinalFailurePersists(string parent)
    {
        using var fixture = new Fixture(parent, "typed-transfer-failure");
        using var first = fixture.Begin();
        using var survivor = fixture.Begin();
        fixture.Report("Verifying", 8);
        var id = fixture.Job().Id;
        SharedSaveTransferProgress.ReceiveFailure(fixture.Data, fixture.Root, fixture.Version.VersionHash,
            new InvalidDataException("password=private-value C:\\private\\save.db https://192.0.2.1:5131"), default,
            WorldPeerTransferOperation.Chunk);
        fixture.Clear();
        first.Dispose();
        SharedSaveTransferProgress.Pending(fixture.Data, fixture.Root, fixture.Version.VersionHash,
            "SaveTransferWorkBudget", "A bounded pass ended.");
        survivor.Dispose();
        var failed = fixture.Job();
        var diagnostic = StartAttemptDiagnostics.Recent(fixture.Data, fixture.Root.ProfileId, 20).Single(item => item.OperationId == id);
        Require(failed is { State: "Failed", ResultCode: "SaveTransferVerificationFailed" } &&
            failed.Message?.Contains("did not pass verification", StringComparison.Ordinal) == true &&
            diagnostic is { Operation: "ReceiveSave_Chunk", Phase: "VerifyingLocal", Code: "SaveTransferVerificationFailed", Kind: "TransferFailed" } &&
            diagnostic.RelatedJobId == id && !diagnostic.Message.Contains("private-value", StringComparison.Ordinal),
            "A later passive lease erased typed failure context or redacted a fixed explanation as player activity.");
        var restarted = new BackgroundJobs(fixture.Data).Read().Jobs.Single();
        Require(restarted.State == "Failed" && restarted.ResultCode == failed.ResultCode && restarted.Message == failed.Message,
            "Typed transfer failure did not survive background-job history reload.");
        var persisted = File.ReadAllText(Path.Combine(fixture.Data.RootPath, "start-attempts.json"));
        Require(!persisted.Contains("private-value", StringComparison.Ordinal) && !persisted.Contains("192.0.2.1", StringComparison.Ordinal) &&
            !persisted.Contains("save.db", StringComparison.Ordinal), "Transfer diagnostics stored raw exception content.");
    }

    private static void PausesAndNewWorkDoNotComplete(string parent)
    {
        using var fixture = new Fixture(parent, "paused-transfer-owner");
        using (var first = fixture.Begin())
        using (var survivor = fixture.Begin())
        {
            fixture.Report("Preparing", 2);
            Require(SharedSaveTransferProgress.Read(fixture.Data, fixture.Root) is { TransferPhase: "Preparing" } preparing &&
                SharedSaveTransferProgress.BlocksHosting(preparing), "Local-reuse preparation did not block Host locally.");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            SharedSaveTransferProgress.ReceiveFailure(fixture.Data, fixture.Root, fixture.Version.VersionHash,
                new OperationCanceledException(canceled.Token), canceled.Token, WorldPeerTransferOperation.Seed, workBudget: true);
            fixture.Clear();
            first.Dispose();
            fixture.Report("Preparing", 3);
            Require(fixture.Jobs().ActiveCount == 1 && fixture.Job() is { State: "Running", ReceivedBytes: 3 },
                "One bounded pause detached an active local-preparation job.");
        }
        Require(fixture.Job() is { State: "Waiting", ResultCode: "SaveTransferWorkBudget" },
            "A bounded paused transfer claimed completion or became a generic failure.");
        fixture.Clear();
        using (var first = fixture.Begin())
        using (var survivor = fixture.Begin())
        {
            fixture.Report("Receiving", 4);
            SharedSaveTransferProgress.Complete(fixture.Data, fixture.Root, fixture.Version.VersionHash);
            fixture.Report("Receiving", 4); // Even identical work is not another owner's completion.
        }
        Require(fixture.Job().State != "Succeeded", "An identical transfer report inherited an older owner's completion.");
        fixture.Clear();
    }

    private static void IndependentJobLeases(string parent)
    {
        using var data = new LocalData(Path.Combine(parent, "direct-job-lease-failure"));
        var jobs = new BackgroundJobs(data);
        var profile = Guid.NewGuid();
        using var failed = jobs.Begin("scope", profile, "Synthetic server", "Synthetic world", "Save download");
        using var surviving = jobs.Begin("scope", profile, "Synthetic server", "Synthetic world", "Save download");
        failed.Report("Receiving", 4, 12);
        var failure = SharedSaveTransferFailure.Classify(new IOException("private", unchecked((int)0x80070070)), default,
            WorldPeerTransferOperation.Chunk);
        failed.Fail(failure);
        Require(jobs.Read().ActiveCount == 1 && jobs.Read().Jobs.Single().State == "Running",
            "One direct job lease ended another owner's active job.");
        surviving.Dispose();
        Require(jobs.Read().Jobs.Single() is { State: "Failed", ResultCode: "SaveTransferInsufficientSpace" },
            "A surviving lease's disposal erased an earlier useful failure.");
        using var complete = jobs.Begin("scope", profile, "Synthetic server", "Synthetic world", "Save download");
        using var fresh = jobs.Begin("scope", profile, "Synthetic server", "Synthetic world", "Save download");
        complete.Complete("SaveReceived");
        fresh.Report("Preparing", 1, 12);
        fresh.Dispose();
        Require(jobs.Read().Jobs.Single().State != "Succeeded", "New direct job work inherited an older completion.");
        using var identicalComplete = jobs.Begin("scope", profile, "Synthetic server", "Synthetic world", "Save download");
        using var identicalWork = jobs.Begin("scope", profile, "Synthetic server", "Synthetic world", "Save download");
        identicalComplete.Report("Receiving", 10, 12);
        var before = jobs.Read().Jobs.Single();
        identicalComplete.Complete("SaveReceived");
        identicalWork.Report("Receiving", 10, 12);
        Require(jobs.Read().Jobs.Single().UpdatedUtc == before.UpdatedUtc,
            "An identical report renewed visible progress while invalidating another owner's completion.");
        identicalWork.Dispose();
        Require(jobs.Read().Jobs.Single().State != "Succeeded", "Identical counters let a passive lease inherit another owner's success.");
    }

    private static void TypedSafeFailures()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var cases = new (Exception Error, CancellationToken Token, string Code, bool Pending)[]
        {
            (new OperationCanceledException(canceled.Token), canceled.Token, "SaveTransferPaused", true),
            (new OperationCanceledException("private"), default, "SaveTransferPermissionChanged", false),
            (new SaveTransferPermissionChangedException(), canceled.Token, "SaveTransferPermissionChanged", false),
            (new TaskCanceledException("private", new TimeoutException()), default, "SaveTransferTimedOut", true),
            (new HttpRequestException("private", null, HttpStatusCode.Unauthorized), default, "SaveTransferAuthFailed", false),
            (new HttpRequestException("private", null, HttpStatusCode.Forbidden), default, "SaveTransferHttpRejected", false),
            (new HttpRequestException("private", null, HttpStatusCode.ServiceUnavailable), default, "SaveTransferRemoteBusy", true),
            (new HttpRequestException("private"), default, "SaveTransferUnreachable", true),
            (new AuthenticationException("private"), default, "SaveTransferAuthFailed", false),
            (new CryptographicException("private"), default, "SaveTransferInvalidHistory", false),
            (new IOException("private", unchecked((int)0x80070020)), default, "SaveTransferFileBusy", true),
            (new IOException("private", new Win32Exception(33)), default, "SaveTransferFileBusy", true),
            (new IOException("private", unchecked((int)0x80070070)), default, "SaveTransferInsufficientSpace", false),
            (new UnauthorizedAccessException("private"), default, "SaveTransferStorageDenied", false)
        };
        foreach (var item in cases)
        {
            var failure = SharedSaveTransferFailure.Classify(item.Error, item.Token, WorldPeerTransferOperation.Chunk, WorldPeerTransferPhase.Preparing);
            Require(failure.Code == item.Code && failure.Pending == item.Pending && failure.Operation == "Chunk" && failure.Phase == "Preparing" &&
                SupportReportRedactor.Token(failure.Code) == failure.Code &&
                SupportReportRedactor.Redact(failure.Message, 500) == failure.Message && !failure.Message.Contains("private", StringComparison.Ordinal),
                "A typed transfer error lost safe context, misclassified a pause or lost its fixed explanation.");
        }
        var protectedDetails = SupportReportRedactor.Redact("password=private-value C:\\private\\save.db https://192.0.2.1:5131");
        Require(SupportReportRedactor.Redact("Peer Alice joined the world").Contains("Player activity redacted", StringComparison.Ordinal) &&
            !SupportReportRedactor.Redact("Player Alice said in chat: private place").Contains("Alice", StringComparison.Ordinal) &&
            !protectedDetails.Contains("private-value", StringComparison.Ordinal) && !protectedDetails.Contains("save.db", StringComparison.Ordinal) &&
            !protectedDetails.Contains("192.0.2.1", StringComparison.Ordinal),
            "Fixed-message handling relaxed generic player, credential or path protection.");
        var fixedFailure = SharedSaveTransferFailure.Classify(new IOException(), default).Message;
        var appended = SupportReportRedactor.Redact(fixedFailure + " Peer Alice password=private-value");
        Require(!appended.Contains("Alice", StringComparison.Ordinal) && !appended.Contains("private-value", StringComparison.Ordinal),
            "An appended untrusted value inherited fixed-message treatment.");
    }

    private static void PreparingTiming()
    {
        var clock = new Clock();
        var store = new LocalHostingProgressStore(clock);
        var connection = new object();
        var profile = Guid.NewGuid();
        using var operation = store.Begin(connection, profile);
        operation.Source(new("Preparing", "Preparing the checked local save.", 1, 10, 0, 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        operation.Source(new("Preparing", "Preparing the checked local save.", 4, 10, 1, 2));
        var measured = store.Read(connection, profile)!;
        Require(measured is { Phase: "Preparing", ReceivedBytes: 4, CompletedFiles: 1 } &&
            measured.PhaseTimings?.Single(item => item.Phase == "Preparing").ElapsedMilliseconds == 3000,
            "Measured local preparation was omitted from phase timing or byte/file progress.");
        clock.Advance(TimeSpan.FromMinutes(1));
        operation.Source(new("Preparing", "Preparing the checked local save.", 4, 10, 1, 2));
        Require(store.Read(connection, profile) == measured, "An unchanged preparation poll renewed progress or its Start instruction lifetime.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal LocalData Data { get; }
        internal SharedWorldPeerRoot Root { get; }
        internal SharedWorldVersion Version { get; }
        internal long Total => Version.Files.Sum(file => file.Length);
        internal Fixture(string parent, string name)
        {
            Data = new LocalData(Path.Combine(parent, name));
            Root = new(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Guid.NewGuid(), Guid.NewGuid());
            var bytes = Encoding.UTF8.GetBytes("Generated transfer ownership evidence.");
            Version = SharedWorldService.SignVersion(new(1, Root.GroupId, 1, null, Root.ProfileId,
                GameKinds.Valheim, "Synthetic", DateTimeOffset.UtcNow, SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
                new(2456, false), [new("synthetic.db", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)))], "", "", ""), key);
        }
        internal IDisposable Begin() => SharedSaveTransferProgress.Begin(Data, Root, Version.VersionHash);
        internal void Report(string phase, long bytes) => SharedSaveTransferProgress.Report(Data, Root, Version, phase, bytes, Total);
        internal void Clear() => SharedSaveTransferProgress.Clear(Data, Root, Version.VersionHash);
        internal BackgroundJobList Jobs() => BackgroundJobs.For(Data).Read();
        internal BackgroundJob Job() => Jobs().Jobs.Single();
        public void Dispose() { Clear(); Data.Dispose(); key.Dispose(); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset utc = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
        private long ticks;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan amount) { utc += amount; ticks += amount.Ticks; }
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
