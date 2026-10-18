using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Native file leases and disposable protected state only. Fabricated in-memory
// signatures cannot accept a real installation; no game/app/process runs.
internal static class WorldPeerLiveTestChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Run(string rootPath)
    {
        ResumeLongPath(rootPath);
        using var data = new LocalData(Path.Combine(rootPath, "peer-private-test-" + Guid.NewGuid().ToString("N")));
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var receiver = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new SharedWorldPeerRoot(Public(owner), Guid.NewGuid(), Guid.NewGuid());
        var receiverId = Guid.NewGuid();
        var payloadBytes = Encoding.UTF8.GetBytes("signed private acceptance payload");
        var artifact = Artifact(root, owner, payloadBytes);
        var rosterDraft = new SharedWorldRoster(2, root.GroupId, root.ProfileId, 1, 1, true,
            root.OwnerPublicKey, [new(receiverId, Public(receiver), new(Receive: true), false)], "");
        var roster = rosterDraft with
        {
            Signature = Convert.ToBase64String(owner.SignData(
            SharedWorldRosterTrust.Basis(rosterDraft), HashAlgorithmName.SHA256))
        };
        data.SaveProtected($"shared-world-pc-signing-{receiverId:N}.protected", receiver.ExportPkcs8PrivateKey());
        data.SaveProtected("world-peer-memberships.protected", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = 1,
            Groups = new[] { new { Root = root, Roster = roster, DeviceId = receiverId,
                Name = "Private test", ReceiveConsented = true, SharingEnabled = false, Bootstrap = (SharedWorldPeerRoute?)null } }
        }, Json));
        using var friends = new FriendService(data);
        using var service = new WorldPeerService(data, new HostManager(data), friends, new PairingService(data),
            (_, _) => throw new InvalidOperationException("No network is allowed in a private-copy check."));
        var path = WorldPeerReceivedTestJournal.StorageRoot(data, root, artifact.TestId);
        var partial = path + ".partial";
        using (var names = new WorldPeerTestNamespace(data.RootPath, partial, create: true))
        {
            names.WriteArtifact(artifact);
            using (var output = names.Resume("payload/world.db")) { output.Write(payloadBytes); output.Flush(true); }
            using (var empty = names.Resume("payload/empty.dat")) empty.Flush(true);
            Require(Refuses(() => Directory.Move(partial, path)), "a partial writer did not hold its namespace against replacement");
        }
        Directory.Move(partial, path);
        var journal = new WorldPeerReceivedTestJournal(data, root);
        Require(journal.Read(artifact) is null && service.ReceivedLiveTests(root.GroupKey).Count == 0,
            "a renamed uncommitted directory was exposed as a completed test");
        var receiptDraft = new SharedWorldReceipt(1, root.GroupId, root.ProfileId, artifact.Version.VersionHash,
            receiverId, roster.Epoch, roster.Revision, Guid.NewGuid(), "");
        var receipt = receiptDraft with
        {
            Signature = Convert.ToBase64String(receiver.SignData(
            SharedWorldReceiptTrust.Basis(receiptDraft), HashAlgorithmName.SHA256))
        };
        var completion = new WorldPeerTestCompletion(1, root, artifact.TestId, artifact.Version.VersionHash,
            WorldPeerVerifiedTestCopy.HashArtifact(artifact), receiverId, Public(receiver), 1, 1, receipt);
        WorldPeerVerifiedTestCopy lease;
        using (lease = WorldPeerVerifiedTestCopy.Open(data.RootPath, path, artifact, default))
        {
            Require(Refuses(() => File.WriteAllText(Path.Combine(path, "payload", "world.db"), "changed")) &&
                Refuses(() => File.WriteAllText(Path.Combine(path, "artifact.json"), "changed")) &&
                Refuses(() => Directory.Move(path, path + ".moved")),
                "a verified descriptor, payload or parent changed before completion/receipt");
            Require(Refuses(() => journal.Save(artifact, completion with { VersionHash = new string('A', 64) }, lease)),
                "a completion marker named a different exact version");
            journal.Save(artifact, completion, lease);
            var stored = journal.Read(artifact);
            Require(stored?.Receipt == receipt && !stored.ReceiptRegistered,
                "the exact signed receipt was not retained for idempotent retry");
            journal.Save(artifact, stored! with { ReceiptRegistered = true }, lease);
            Require(journal.Read(artifact)?.Receipt == receipt && journal.Read(artifact)?.ReceiptRegistered == true,
                "a successful registration lost its stable receipt identity");
            var ownerDraft = receiptDraft with { Schema = 2, DeviceId = Guid.Empty, ReceiptId = Guid.NewGuid() };
            var ownerReceipt = ownerDraft with
            {
                Signature = Convert.ToBase64String(owner.SignData(
                SharedWorldReceiptTrust.Basis(ownerDraft), HashAlgorithmName.SHA256))
            };
            Require(WorldPeerOwnerTestReceiptTrust.Verify(ownerReceipt, root) &&
                !SharedWorldReceiptTrust.Verify(ownerReceipt, root.OwnerPublicKey),
                "an owner-only private receipt relaxed the canonical member validator");
            journal.Save(artifact, completion with
            {
                ReceiverDeviceId = Guid.Empty,
                ReceiverPublicKey = root.OwnerPublicKey,
                Receipt = ownerReceipt,
                ReceiptRegistered = false
            }, lease);
            Require(journal.Read(artifact)?.Receipt == ownerReceipt,
                "the private owner receiver receipt was not retained as separate evidence");
            journal.Save(artifact, completion with { ReceiptRegistered = true }, lease);
        }
        Require(Refuses(() => journal.Save(artifact, completion, lease)),
            "a disposed copy lease authorized a new completion marker");
        Require(service.ReceivedLiveTests(root.GroupKey).Count == 1,
            "the protected exact completed copy was not listed");
        var changedDescriptor = artifact with { Scope = artifact.Scope with { BinaryVersion = "different" } };
        changedDescriptor = SignArtifact(changedDescriptor, owner);
        Require(Refuses(() => WorldPeerVerifiedTestCopy.Open(data.RootPath, path, changedDescriptor, default)),
            "a re-signed descriptor for the same test ID replaced immutable received evidence");
        var file = Path.Combine(path, "payload", "world.db");
        File.WriteAllText(file, "changed payload");
        Require(Refuses(() => WorldPeerVerifiedTestCopy.Open(data.RootPath, path, artifact, default)) &&
            service.ReceivedLiveTests(root.GroupKey).Count == 0 && journal.Read(artifact) is not null,
            "a corrupted completed payload was exposed, repaired, or erased instead of retained");
        File.WriteAllBytes(file, payloadBytes);
        File.WriteAllText(Path.Combine(path, "payload", "unsigned-extra.dat"), "unsigned");
        Require(Refuses(() => WorldPeerVerifiedTestCopy.Open(data.RootPath, path, artifact, default)),
            "unsigned extra files became part of an exact private copy");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Require(Refuses(() => WorldPeerVerifiedTestCopy.Open(data.RootPath, path, artifact, canceled.Token)),
            "a canceled receiver could still verify and commit a copy");
        Require(!SharedWorldService.VerifySignature(artifact.Version) &&
            SharedWorldFreshness.SelectNewestCompleteVerified([new(artifact.Version, true, true)], root.ProfileId, root.GroupId) is null,
            "a private test was admitted into normal recovery/freshness");
    }

    private static void ResumeLongPath(string rootPath)
    {
        var namespaceRoot = Path.Combine(rootPath, "resume-" + Guid.NewGuid().ToString("N"),
            new string('a', 120), new string('b', 120));
        const string relative = "payload/chunks/world.db";
        var file = Path.Combine(namespaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Require(file.Length > 260, "the resumed private-file regression did not exceed Win32 MAX_PATH");
        using (var names = new WorldPeerTestNamespace(rootPath, namespaceRoot, create: true))
        {
            using (var output = names.Resume(relative))
            {
                output.Write([1, 2]); output.Flush(true);
                Require(Refuses(() => { using var competing = names.Resume(relative); }) &&
                    Refuses(() => File.WriteAllBytes(file, [9])) &&
                    Refuses(() => Directory.Move(namespaceRoot, namespaceRoot + ".moved")),
                    "a resumed private writer lost its exclusive file or ancestor leases");
            }
            using (var output = names.Resume(relative))
            {
                Require(output.Length == 2 && output.Position == 0 && output.ReadByte() == 1 && output.ReadByte() == 2,
                    "resuming a long private file truncated or replaced the existing bytes");
                output.Seek(0, SeekOrigin.End); output.WriteByte(3); output.Flush(true);
            }
            Require(File.ReadAllBytes(file).SequenceEqual(new byte[] { 1, 2, 3 }) &&
                Refuses(() => { using var directory = names.Resume("payload/chunks"); }) &&
                Refuses(() => { using var escaped = names.Resume("payload/../outside.dat"); }),
                "a resumed private file lost bytes or accepted a directory/escaping child");
            var source = Path.Combine(rootPath, "resume-source-" + Guid.NewGuid().ToString("N") + ".dat");
            File.WriteAllBytes(source, [4, 5]);
            var linked = Path.Combine(namespaceRoot, "payload", "linked.dat");
            Require(CreateHardLink(NativePath(linked), NativePath(source), IntPtr.Zero),
                "the private resume hard-link rejection fixture could not be created");
            Require(Refuses(() => { using var rejected = names.Resume("payload/linked.dat"); }) &&
                File.ReadAllBytes(source).SequenceEqual(new byte[] { 4, 5 }),
                "a resumed hard-linked private file was accepted or changed the original bytes");
        }
        using var readOnly = new WorldPeerTestNamespace(rootPath, namespaceRoot, create: false);
        Require(Refuses(() => { using var rejected = readOnly.Resume(relative); }),
            "a read-only private namespace acquired a resumable writer");
    }

    private static string NativePath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full :
            full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);

    private static LiveSaveTestArtifact Artifact(SharedWorldPeerRoot root, ECDsa owner, byte[] bytes)
    {
        var testId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var scope = SharedWorldLiveSaveAdapters.BindInstallation(root.ProfileId, GameKinds.Valheim,
            "fabricated private-copy cryptographic scope", Hash([1]), Hash([2]), false);
        SharedWorldFile[] files = [new("world.db", bytes.LongLength, Hash(bytes)), new("empty.dat", 0, Hash([]))];
        var version = SharedWorldService.SignAcceptanceTestVersion(new(5, root.GroupId, 1, null, root.ProfileId,
            GameKinds.Valheim, "synthetic-private-copy", now, LiveSaveAcceptanceTrust.TestCaptureKind, testId,
            new(2456, false, "synthetic", [], []), files, "", "", ""), owner);
        return SignArtifact(new(1, true, testId, scope, Guid.NewGuid(), Guid.Empty, now, now.AddSeconds(1),
            LiveSaveAcceptanceTrust.PayloadHash(files), files.Sum(file => file.Length), SharedWorldService.ChunkBytes,
            LiveSaveAcceptanceTrust.ChunkCount(files), version, ""), owner);
    }
    private static LiveSaveTestArtifact SignArtifact(LiveSaveTestArtifact artifact, ECDsa owner) => artifact with
    { Signature = Convert.ToBase64String(owner.SignData(LiveSaveAcceptanceTrust.ArtifactBasis(artifact), HashAlgorithmName.SHA256)) };
    private static string Public(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Refuses(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (LiveSaveAcceptanceTrust.ExpectedFailure(error) || error is OperationCanceledException)
        { return true; }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
