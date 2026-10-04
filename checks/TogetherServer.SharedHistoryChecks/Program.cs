using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

if (args is ["--replay", var replayVault, var replayManifest])
{
    var candidate = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(replayManifest),
        new JsonSerializerOptions(JsonSerializerDefaults.Web)) ??
        throw new InvalidDataException("The disposable replay manifest is invalid.");
    FriendLink.KeepSignedManifest(replayVault, candidate);
    return;
}
if (args is ["--replay-chain", var replayRoot, var replayDevice, var replayProfile,
        var replayAnchor, var replayHead])
{
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var anchor = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(replayAnchor), json) ??
        throw new InvalidDataException("The disposable replay anchor is invalid.");
    var latest = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(replayHead), json) ??
        throw new InvalidDataException("The disposable replay head is invalid.");
    using var replayData = new LocalData(replayRoot);
    var replayed = await FriendLink.VerifySharedChainBatchAsync(replayData,
        Guid.Parse(replayDevice), Guid.Parse(replayProfile), anchor, latest, [],
        (_, _) => throw new InvalidDataException("The replay unexpectedly requested an older version."),
        CancellationToken.None);
    Require(replayed.Valid, "the completed temporary manifest did not replay through catch-up");
    return;
}

var parent = Path.Combine(Path.GetTempPath(), "TogetherServer-shared-history-checks");
var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var profile = Guid.NewGuid();
    var device = Guid.NewGuid();
    var draft = new SharedWorldVersion(4, Guid.NewGuid(), 1, null, profile,
        GameKinds.Valheim, "fixture-world", DateTimeOffset.UtcNow,
        SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
        new SharedWorldPortableSetup(2456, false, "fixture", [], []),
        [new SharedWorldFile("world.dat", 1, Convert.ToHexString(SHA256.HashData([1])))],
        "", "", "");
    var versions = new List<SharedWorldVersion>();
    var head = SharedWorldService.SignVersion(draft, signer);
    versions.Add(head);
    for (var i = 1; i < 4102; i++)
    {
        head = SharedWorldService.SignVersion(head with
        {
            Number = head.Number + 1,
            ParentHash = head.VersionHash,
            BackupId = Guid.NewGuid()
        }, signer);
        versions.Add(head);
    }
    var vault = Path.Combine(root, "received-shared-worlds", device.ToString("N"), profile.ToString("N"));
    var firstFetch = long.MaxValue;
    var calls = 0;
    using (var data = new LocalData(root))
    {
        FriendLink.SharedChainCheck result;
        do
        {
            var fetched = 0;
            result = await FriendLink.VerifySharedChainBatchAsync(data, device, profile,
                versions[0], head, [], (number, _) =>
                {
                    firstFetch = Math.Min(firstFetch, number);
                    fetched++;
                    return Task.FromResult<SharedWorldVersion?>(versions[checked((int)number - 1)]);
                }, CancellationToken.None);
            Require(fetched <= 128, "one catch-up request exceeded 128 versions");
            Require(++calls <= 33, "catch-up failed to progress");
        } while (result.Pending);
        Require(result.Valid && firstFetch == 2 && calls == 33,
            "a new PC could not page genesis to a head beyond 4,096");
    }
    using (var restarted = new LocalData(root))
    {
        var fetched = 0;
        var result = await FriendLink.VerifySharedChainBatchAsync(restarted, device, profile,
            versions[0], head, [], (_, _) =>
            {
                fetched++;
                return Task.FromResult<SharedWorldVersion?>(null);
            }, CancellationToken.None);
        Require(result.Valid && fetched == 0, "protected cursor did not resume at the verified head");
        var progressName = $"shared-chain-{device:N}-{profile:N}.protected";
        var protectedProgress = restarted.LoadProtected(progressName) ??
            throw new Exception("The verified catch-up cursor was not durable.");
        restarted.DeleteProtected(progressName);
        fetched = 0;
        result = await FriendLink.VerifySharedChainBatchAsync(restarted, device, profile,
            versions[0], head, [], (number, _) =>
            {
                fetched++;
                return Task.FromResult<SharedWorldVersion?>(versions[checked((int)number - 1)]);
            }, CancellationToken.None);
        Require(result.Pending && fetched == 128,
            "manifest-before-cursor crash replay did not safely recheck the first batch");
        restarted.SaveProtected(progressName, protectedProgress);
    }
    var payload = Path.Combine(vault, head.VersionHash, SharedWorldService.PayloadDirectory);
    Directory.CreateDirectory(payload);
    File.WriteAllBytes(Path.Combine(payload, "world.dat"), [1]);
    File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(head));
    Require(FriendLink.ReadVerifiedReceivedLineage(vault, head, null).LongCount() == 4102,
        "takeover could not prove more than 4,096 signed ancestors");

    using (var reviewData = new LocalData(root))
    using (var successor = ECDsa.Create(ECCurve.NamedCurves.nistP256))
    {
        var successorId = Guid.NewGuid();
        var successorKey = Convert.ToBase64String(successor.ExportSubjectPublicKeyInfo());
        var rosterDraft = new SharedWorldRoster(2, head.GroupId, profile, 1, 1, true,
            head.SigningPublicKey,
            [new SharedWorldRosterMember(successorId, successorKey,
                new SharedWorldGrants(EligibleHost: true, RecoveryVoter: true), false)], "");
        var roster = rosterDraft with
        {
            Signature = Convert.ToBase64String(signer.SignData(
                SharedWorldRosterTrust.Basis(rosterDraft), HashAlgorithmName.SHA256))
        };
        var reviewVersions = versions.Take(140).ToArray();
        var reviewHead = reviewVersions[^1];
        var proposalDraft = new WorldAuthorityProposal(1, head.GroupId, profile, 1, null,
            WorldAuthorityTrust.RosterHash(roster), reviewHead.VersionHash, successorKey,
            "https://127.0.0.1:5132", "Quorum", successorId, successorKey, "");
        var proposal = proposalDraft with
        {
            Signature = Convert.ToBase64String(successor.SignData(
                WorldAuthorityTrust.ProposalBasis(proposalDraft), HashAlgorithmName.SHA256))
        };
        var voteDraft = new WorldAuthorityVote(1, WorldAuthorityTrust.ProposalHash(proposal),
            successorId, successorKey, "");
        var vote = voteDraft with
        {
            Signature = Convert.ToBase64String(successor.SignData(
                WorldAuthorityTrust.VoteBasis(voteDraft), HashAlgorithmName.SHA256))
        };
        var recordDraft = new WorldAuthorityRecord(1, proposal, roster, reviewHead,
            [vote], null, "", VersionLineageDigest: WorldAuthorityTrust.LineageDigest(reviewVersions));
        var record = recordDraft with
        { RecordHash = WorldAuthorityTrust.Hash(WorldAuthorityTrust.RecordBasis(recordDraft)) };
        Require(WorldAuthorityTrust.Verify(record) &&
            WorldAuthorityTrust.VerifyLineage(record, null, reviewVersions),
            "the disposable review authority was not signed and linked");
        var authority = new WorldAuthorityStore(reviewData);
        authority.AppendReceived(record, profile, head.GroupId, head.SigningPublicKey,
            reviewVersions);
        var review = new WorldAuthorityStore(reviewData);
        foreach (var number in new long[] { 1, 128, 129, 140 })
        {
            var indexed = review.ReadReviewProofIndex(profile, record.RecordHash, number);
            Require(indexed is { } expected &&
                expected.ExpectedHash == reviewVersions[checked((int)number - 1)].VersionHash &&
                review.ReadReviewProofVersion(expected.Record, number)?.VersionHash ==
                    expected.ExpectedHash,
                "the bounded review index returned the wrong signed proof piece");
        }
        Require(review.ReviewProofIndexBuildCount == 2 &&
            review.ReviewFullValidationCount == 1,
            "the review index did not cache only its requested 128-version page");
        var proofPath = Path.Combine(root, "shared-worlds", profile.ToString("N"), "authority",
            "proof-" + record.RecordHash, "129.json");
        var correctProof = File.ReadAllBytes(proofPath);
        var changed = SharedWorldService.SignVersion(reviewVersions[128] with
        { BackupId = Guid.NewGuid() }, signer);
        File.WriteAllBytes(proofPath, JsonSerializer.SerializeToUtf8Bytes(changed,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        RequireThrows<InvalidDataException>(() => new WorldAuthorityStore(reviewData)
            .ReadReviewProofIndex(profile, record.RecordHash, 129),
            "a signed but changed review proof passed full-lineage verification");
        File.WriteAllBytes(proofPath, correctProof);
    }

    var replay = SharedWorldService.SignVersion(head with
    {
        Number = head.Number + 1,
        ParentHash = head.VersionHash,
        BackupId = Guid.NewGuid()
    }, signer);
    var replayBytes = JsonSerializer.SerializeToUtf8Bytes(replay,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var replayInput = Path.Combine(root, "replay-version.json");
    File.WriteAllBytes(replayInput, replayBytes);
    var anchorInput = Path.Combine(root, "replay-anchor.json");
    File.WriteAllBytes(anchorInput, JsonSerializer.SerializeToUtf8Bytes(versions[0],
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var bucket = Path.Combine(vault, "signed-history", (replay.Number / 1024).ToString("x16"));
    var replayFinal = Path.Combine(bucket, replay.Number + "-" + replay.VersionHash + ".json");
    var orphan = replayFinal + ".new";
    File.WriteAllBytes(orphan, replayBytes);
    Require(RunFreshProcess("--replay-chain", root, device.ToString(), profile.ToString(),
            anchorInput, replayInput) == 0 &&
        File.Exists(replayFinal) && !File.Exists(orphan),
        "a complete signed temporary manifest did not replay through catch-up after restart");
    File.WriteAllBytes(orphan, replayBytes);
    Require(RunFreshProcess("--replay", vault, replayInput) == 0 &&
        File.Exists(replayFinal) && !File.Exists(orphan),
        "an exact duplicate temporary manifest was not cleaned idempotently");

    var malformed = SharedWorldService.SignVersion(replay with
    {
        Number = replay.Number + 1,
        ParentHash = replay.VersionHash,
        BackupId = Guid.NewGuid()
    }, signer);
    var malformedInput = Path.Combine(root, "malformed-version.json");
    File.WriteAllBytes(malformedInput, JsonSerializer.SerializeToUtf8Bytes(malformed,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var malformedTemp = Path.Combine(bucket,
        malformed.Number + "-" + malformed.VersionHash + ".json.new");
    File.WriteAllBytes(malformedTemp, [1]);
    Require(RunFreshProcess("--replay", vault, malformedInput) != 0 &&
        File.ReadAllBytes(malformedTemp).SequenceEqual(new byte[] { 1 }) &&
        !File.Exists(malformedTemp[..^4]),
        "a malformed interrupted manifest was accepted or deleted");
    File.Delete(malformedTemp);
    var unexpected = Path.Combine(bucket, "unexpected.json");
    File.WriteAllBytes(unexpected, [1]);
    RequireThrows<InvalidDataException>(() => FriendLink.SignedHistoryUsage(vault),
        "an unexpected archive entry was accepted by the full audit");
    File.Delete(unexpected);

    var middle = versions[2048];
    var middlePath = Path.Combine(vault, "signed-history", (middle.Number / 1024).ToString("x16"),
        middle.Number + "-" + middle.VersionHash + ".json");
    var original = File.ReadAllBytes(middlePath);
    File.WriteAllText(middlePath, "{tampered");
    RequireThrows<Exception>(() => FriendLink.ReadVerifiedReceivedLineage(vault, head, null).LongCount(),
        "tampered takeover ancestor was accepted");
    File.WriteAllBytes(middlePath, original);
    File.Delete(middlePath);
    RequireThrows<InvalidDataException>(() => FriendLink.ReadVerifiedReceivedLineage(vault, head, null).LongCount(),
        "missing takeover ancestor was accepted");
    Console.WriteLine("PASS 4,102-version catch-up, cursor restart, exact-temp replay, orphan, tamper, takeover proof, 128-version review paging");
}
finally
{
    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Fixture root escaped its temporary parent.");
    Directory.Delete(root, true);
}

static void Require(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static void RequireThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception(message);
}

static int RunFreshProcess(params string[] childArguments)
{
    var executable = Environment.ProcessPath ?? throw new InvalidDataException("Check process path is unavailable.");
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardError = true
    };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (var argument in childArguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("The replay check did not start.");
    _ = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(60_000))
    {
        process.Kill(entireProcessTree: true);
        throw new Exception("The replay check did not finish.");
    }
    return process.ExitCode;
}
