using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record PlannedHandoffStageResult(bool Ok, string Code, string Message,
    string? VersionHash = null);

internal static class PlannedHandoffReceiver
{
    private const long ReserveBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<PlannedHandoffStageResult> StageAsync(LocalData data,
        string vaultRoot, WorldAuthorityRecord record, Guid profileId, Guid groupId,
        string pinnedOwnerKey, Guid deviceId, ECDsa deviceKey,
        Func<Task<bool>> stillConsented, CancellationToken cancellationToken)
    {
        if (!WorldAuthorityTrust.Verify(record) || record.Proposal.Kind != "Planned" ||
            record.Proposal.ProfileId != profileId || record.Proposal.GroupId != groupId ||
            record.Roster.OwnerPublicKey != pinnedOwnerKey ||
            record.SuccessorReceipt?.DeviceId != deviceId ||
            record.Proposal.CandidatePublicKey !=
                Convert.ToBase64String(deviceKey.ExportSubjectPublicKeyInfo()))
            return new(false, "HandoffProofInvalid", "The signed handoff is not for this PC and world.");
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, vaultRoot);
        var received = FriendLink.ReadReceivedLatest(vaultRoot);
        if (received is null || received.VersionHash != record.Version.VersionHash ||
            received.GroupId != groupId || received.SigningPublicKey != pinnedOwnerKey)
            return new(false, "FinalCopyMissing", "Receive and hash-check the exact post-Stop file copy before staging takeover.");
        var bytes = SharedWorldService.BoundedTotalBytes(received.Files);
        var root = Path.Combine(data.RootPath, "shared-world-staged", profileId.ToString("N"));
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, root);
        var available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace;
        if (available < bytes + ReserveBytes)
            return new(false, "LowSpace", "Free enough space to stage this copy and keep at least 1 GiB free.");
        var destination = Path.Combine(root, record.RecordHash);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, destination);
        if (!Directory.Exists(destination))
        {
            Directory.CreateDirectory(root);
            var stage = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                SharedWorldService.EnsureUnlinkedRoot(data.RootPath, stage);
                foreach (var file in received.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = SharedWorldService.SafeChild(Path.Combine(vaultRoot,
                        received.VersionHash, SharedWorldService.PayloadDirectory), file.Path);
                    SharedWorldService.VerifyFile(source, file);
                    var target = SharedWorldService.SafeChild(Path.Combine(stage,
                        SharedWorldService.PayloadDirectory), file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        SharedWorldService.ChunkBytes, FileOptions.Asynchronous))
                    await using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, SharedWorldService.ChunkBytes, FileOptions.Asynchronous | FileOptions.WriteThrough))
                        await from.CopyToAsync(to, SharedWorldService.ChunkBytes, cancellationToken);
                    SharedWorldService.VerifyFile(target, file);
                }
                File.WriteAllBytes(Path.Combine(stage, "authority.json"),
                    JsonSerializer.SerializeToUtf8Bytes(record, Json));
                Directory.Move(stage, destination);
            }
            finally
            {
                if (Directory.Exists(stage))
                {
                    SharedWorldService.EnsureUnlinkedRoot(data.RootPath, stage);
                    if (Directory.EnumerateFileSystemEntries(stage, "*", SearchOption.AllDirectories)
                        .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                        throw new InvalidDataException("The partial handoff stage contains a linked path.");
                    Directory.Delete(stage, true);
                }
            }
        }
        VerifyStage(data.RootPath, destination, record);
        if (!await stillConsented())
            return new(false, "ConsentWithdrawn", "This PC stopped receiving this shared world during staging.");
        var authority = new WorldAuthorityStore(data);
        var accepted = authority.Read(profileId);
        if (!accepted.Any(item => item.RecordHash == record.RecordHash))
        {
            var parent = accepted.SingleOrDefault(item =>
                item.RecordHash == record.Proposal.ParentAuthorityHash);
            if (record.VersionLineageDigest is null)
                authority.AppendReceived(record, profileId, groupId, pinnedOwnerKey);
            else
            {
                authority.AppendReceivedStaged(record, parent, profileId, groupId, pinnedOwnerKey);
                try { authority.ClearStagedProof(record, parent); }
                catch (IOException) { /* Verified authority is already durable. */ }
            }
        }
        var records = authority.Read(profileId);
        var heads = records.Where(item => !records.Any(child =>
            child.Proposal.ParentAuthorityHash == item.RecordHash)).ToArray();
        if (heads.Length != 1 || heads[0].RecordHash != record.RecordHash)
            return new(false, "CompetingAuthority",
                "Another signed takeover history is present. Keep both copies and review before hosting.");
        return new(true, "StagedForSetup",
            "The signed final copy is staged in a fresh app-owned location. Start remains blocked until local setup, save signing, and direct routes are verified.",
            received.VersionHash);
    }

    internal static void VerifyStage(string dataRoot, string destination, WorldAuthorityRecord expected)
    {
        SharedWorldService.EnsureUnlinkedRoot(dataRoot, destination);
        var path = Path.Combine(destination, "authority.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The staged handoff proof is missing or linked.");
        var recorded = JsonSerializer.Deserialize<WorldAuthorityRecord>(File.ReadAllBytes(path), Json);
        if (!WorldAuthorityTrust.Verify(recorded) || recorded!.RecordHash != expected.RecordHash)
            throw new InvalidDataException("The staged handoff proof changed.");
        foreach (var file in expected.Version.Files)
            SharedWorldService.VerifyFile(SharedWorldService.SafeChild(Path.Combine(destination,
                SharedWorldService.PayloadDirectory), file.Path), file);
    }
}
