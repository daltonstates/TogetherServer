using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace TogetherServer;

public sealed record ReceivedSharedWorldStatus(bool Consented, long? HostVersion, long? ThisPcVersion,
    string State, string? Error = null, long ReceivedBytes = 0, long TotalBytes = 0,
    long? RosterRevision = null, string Trust = "Roster not verified");
public sealed record ReceivedSharedWorldResult(bool Ok, string Code, string Message,
    ReceivedSharedWorldStatus? Status = null);

internal sealed partial class FriendLink
{
    private const long ReceiverReserveBytes = 1024L * 1024 * 1024;
    private readonly ConcurrentDictionary<Guid, ReceivedSharedWorldStatus> sharedTransfers = new();
    private readonly ConcurrentDictionary<Guid, byte> withdrawnSharedConsent = new();
    private readonly object sharedReceiptSync = new();

    private async Task<ReceivedSharedWorldResult?> TrustSharedRosterAsync(Guid profileId,
        CancellationToken cancellationToken)
    {
        using var pcKey = LoadPcSigningKey();
        var publicKey = Convert.ToBase64String(pcKey.ExportSubjectPublicKeyInfo());
        using var challengeResponse = await HostClient().GetAsync(
            $"api/companion/servers/{profileId}/shared-world/enrollment", cancellationToken);
        if (!challengeResponse.IsSuccessStatusCode)
            return SharedFailure("EnrollmentDenied", "The Host did not allow this PC to enroll for this server.");
        var challengeBytes = await ReadBoundedSharedAsync(challengeResponse.Content, 512, cancellationToken);
        var challenge = challengeBytes is null ? null :
            JsonSerializer.Deserialize<SharedWorldEnrollmentChallenge>(challengeBytes, Json);
        if (challenge?.Nonce is null || challenge.Nonce.Length != 44)
            return SharedFailure("InvalidChallenge", "The Host sent an invalid enrollment challenge.");
        var proof = new SharedWorldEnrollmentRequest(challenge.Nonce, publicKey,
            Convert.ToBase64String(pcKey.SignData(SharedWorldRosterTrust.EnrollmentBasis(
                config!.DeviceId, challenge.Nonce, publicKey), HashAlgorithmName.SHA256)));
        using var enrollment = await HostClient().PostAsJsonAsync(
            $"api/companion/servers/{profileId}/shared-world/enrollment", proof, Json, cancellationToken);
        if (!enrollment.IsSuccessStatusCode)
            return SharedFailure("KeyReviewRequired", "The Host did not accept this PC's signing identity. Ask the owner to review it.");
        using var response = await HostClient().GetAsync(
            $"api/companion/servers/{profileId}/shared-world/roster",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await ReadBoundedSharedAsync(response.Content,
            SharedWorldService.MaximumManifestBytes, cancellationToken);
        if (!response.IsSuccessStatusCode || bytes is null)
            return SharedFailure("RosterUnavailable", "The current owner-signed roster is unavailable.");
        var roster = JsonSerializer.Deserialize<SharedWorldRoster>(bytes, Json);
        if (!SharedWorldRosterTrust.Verify(roster) || roster!.ProfileId != profileId)
            return SharedFailure("RosterRejected", "The owner-signed roster failed verification.");
        config.SharedWorldSigningKeys ??= [];
        config.SharedRosterFloors ??= [];
        var pinned = config.SharedWorldSigningKeys.GetValueOrDefault(profileId);
        if (pinned is not null && roster?.OwnerPublicKey != pinned)
            return SharedFailure("SigningIdentityChanged", "The Host's world signing identity changed. Ask the owner to review it.");
        var floor = config.SharedRosterFloors.GetValueOrDefault(profileId);
        if (roster is not null && floor is not null && floor.GroupId != roster.GroupId &&
            config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != roster.GroupId)
        {
            config.PendingSharedWorldGroups ??= [];
            config.PendingSharedWorldGroups[profileId] = roster.GroupId;
            SaveConfig();
            return SharedFailure("SourceReviewRequired",
                "The Host changed this save source. Turn Allow saves off, then on to review the new signed group.");
        }
        if (roster is not null && floor?.GroupId != roster.GroupId) floor = null;
        if (roster is null || !SharedWorldRosterTrust.Accept(roster, profileId, config.DeviceId,
                publicKey, pinned ?? roster.OwnerPublicKey, floor?.Epoch ?? 0, floor?.Revision ?? 0) ||
            floor is not null && roster.Epoch == floor.Epoch && roster.Revision == floor.Revision &&
                roster.Signature != floor.Signature)
            return SharedFailure("RosterRejected", "The signed roster is invalid, older, or does not grant this PC Receive access.");
        if (withdrawnSharedConsent.ContainsKey(profileId))
            return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
        // Persist the rollback floor before any manifest or chunks are trusted.
        config.SharedWorldSigningKeys[profileId] = roster.OwnerPublicKey;
        config.SharedRosterFloors[profileId] = new(roster.GroupId, roster.Epoch, roster.Revision, roster.Signature);
        SaveConfig();
        return null;
    }

    private string ReceivedRoot(Guid profileId)
    {
        var path = Path.Combine(data.RootPath, "received-shared-worlds",
            config!.DeviceId.ToString("N"), profileId.ToString("N"));
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
        return path;
    }

    public async Task<ReceivedSharedWorldResult> SetSharedWorldConsentAsync(Guid profileId, bool enabled)
    {
        if (!TryRetain()) return SharedFailure("ConnectionClosed", "This connection is closing.");
        if (!enabled) WithdrawSharedConsent(profileId);
        await gate.WaitAsync();
        try
        {
            if (config is null) return SharedFailure("NotPaired", "Connect to a Host first.");
            config.ConsentedSharedWorldProfiles ??= [];
            var previouslyConsented = config.ConsentedSharedWorldProfiles.Contains(profileId);
            Guid? priorApproval = config.ApprovedSharedWorldGroups?.TryGetValue(profileId,
                out var approvedGroup) == true ? approvedGroup : null;
            Guid? priorPending = config.PendingSharedWorldGroups?.TryGetValue(profileId,
                out var pendingBefore) == true ? pendingBefore : null;
            config.ConsentedSharedWorldProfiles.Remove(profileId);
            if (enabled)
            {
                using var signingKey = LoadPcSigningKey();
                config.ConsentedSharedWorldProfiles.Add(profileId);
            }
            if (enabled && !previouslyConsented &&
                config.PendingSharedWorldGroups?.TryGetValue(profileId, out var pendingGroup) == true)
            {
                config.ApprovedSharedWorldGroups ??= [];
                config.ApprovedSharedWorldGroups[profileId] = pendingGroup;
                config.PendingSharedWorldGroups.Remove(profileId);
            }
            try { SaveConfig(); }
            catch
            {
                config.ConsentedSharedWorldProfiles.Remove(profileId);
                if (previouslyConsented) config.ConsentedSharedWorldProfiles.Add(profileId);
                if (priorApproval is Guid approved) config.ApprovedSharedWorldGroups![profileId] = approved;
                else config.ApprovedSharedWorldGroups?.Remove(profileId);
                if (priorPending is Guid pending) config.PendingSharedWorldGroups![profileId] = pending;
                if (previouslyConsented) lock (sharedReceiptSync)
                    withdrawnSharedConsent.TryRemove(profileId, out _);
                throw;
            }
            if (enabled) lock (sharedReceiptSync) withdrawnSharedConsent.TryRemove(profileId, out _);
            return new(true, "ConsentSaved", enabled ? "This PC may pull approved completed saves." :
                "This PC will no longer pull shared saves.", LocalSharedWorldStatus(profileId));
        }
        finally { gate.Release(); ReleaseRetained(); }
    }

    public ReceivedSharedWorldStatus SharedWorldStatus(Guid profileId)
    {
        if (config is null) return new(false, null, null, "Not paired");
        return sharedTransfers.TryGetValue(profileId, out var active) ? active : LocalSharedWorldStatus(profileId);
    }

    public async Task<ReceivedSharedWorldResult> CheckSharedWorldAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (!TryRetain()) return SharedFailure("ConnectionClosed", "This connection is closing.");
        var entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            entered = true;
            if (config is null) return SharedFailure("NotPaired", "Connect to a Host first.");
            if (config.ConsentedSharedWorldProfiles?.Contains(profileId) != true)
                return SharedFailure("ConsentRequired", "Allow shared saves on this PC first.");
            var profile = view.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return SharedFailure("UnknownProfile", "This server is not assigned to this PC.");
            if (!view.HostCapabilities.Contains(CompanionProtocol.SharedWorldsCapability, StringComparer.Ordinal))
                return SharedFailure("SharedWorldsUpdateRequired", "Update the Host app to receive shared saves.");
            var trustFailure = await TrustSharedRosterAsync(profileId, cancellationToken);
            if (trustFailure is not null) return trustFailure;
            using var response = await HostClient().GetAsync($"api/companion/servers/{profileId}/shared-world",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var bytes = await ReadBoundedSharedAsync(response.Content,
                SharedWorldService.MaximumManifestBytes, cancellationToken);
            if (withdrawnSharedConsent.ContainsKey(profileId))
                return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
            if (!response.IsSuccessStatusCode || bytes is null)
                return RemoteSharedDenial(bytes);
            var version = JsonSerializer.Deserialize<SharedWorldVersion>(bytes, Json);
            if (version is null || !SharedWorldService.VerifySignature(version) ||
                version.ProfileId != profileId || version.Game != profile.Kind)
                return SharedFailure("InvalidManifest", "The Host's shared save failed verification.");
            if (config.SharedRosterFloors?.GetValueOrDefault(profileId)?.GroupId != version.GroupId)
                return SharedFailure("GroupMismatch", "The save version does not match the verified shared roster.");
            config.SharedWorldSigningKeys ??= [];
            if (config.SharedWorldSigningKeys.TryGetValue(profileId, out var pinned) &&
                pinned != version.SigningPublicKey)
                return SharedFailure("SigningIdentityChanged", "The Host's world signing identity changed.");
            config.LastSharedHostGroups ??= [];
            if (config.LastSharedHostGroups.GetValueOrDefault(profileId) == version.GroupId &&
                config.LastSharedHostVersions?.GetValueOrDefault(profileId) > version.Number)
                return SharedFailure("VersionRollback", "The Host returned an older save version for this group.");
            config.SharedWorldSigningKeys[profileId] = version.SigningPublicKey;
            config.LastSharedHostVersions ??= [];
            config.LastSharedHostVersions[profileId] = version.Number;
            config.LastSharedHostGroups[profileId] = version.GroupId;
            RememberSourceReview(profileId, version);
            SaveConfig();
            var held = ReadReceivedLatest(ReceivedRoot(profileId));
            if (held?.VersionHash == version.VersionHash)
                await SendSharedReceiptAsync(profileId, held, cancellationToken);
            return new(true, "SharedWorldChecked", "Latest Host version checked securely.",
                LocalSharedWorldStatus(profileId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
            CryptographicException or InvalidDataException or UnauthorizedAccessException)
        { return SharedFailure("CheckFailed", "The latest Host save could not be checked: " + ex.Message); }
        finally { if (entered) gate.Release(); ReleaseRetained(); }
    }

    private ReceivedSharedWorldStatus LocalSharedWorldStatus(Guid profileId)
    {
        var consent = config?.ConsentedSharedWorldProfiles?.Contains(profileId) == true;
        long? received = null;
        string? error = null;
        try
        {
            received = ReadReceivedLatest(ReceivedRoot(profileId))?.Number;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or InvalidDataException)
        { error = "The stored save failed verification. The live world was not changed."; }
        var hostVersion = config?.LastSharedHostVersions?.GetValueOrDefault(profileId);
        var review = config?.PendingSharedWorldGroups?.ContainsKey(profileId) == true;
        var floor = config?.SharedRosterFloors?.GetValueOrDefault(profileId);
        return new(consent, hostVersion, received,
            !consent ? "Consent off" : error is not null ? "Error" :
            review ? "Host save source changed. Turn Allow saves off, then on to approve the new signed group. Earlier verified copies stay here." :
            hostVersion is null ? "Host version not checked" :
            hostVersion == received ? "Up to date when last checked" : "Ready to pull", error,
            RosterRevision: floor?.Revision,
            Trust: floor is null ? "Roster not verified" : "Owner signature and this PC's Receive grant verified when last checked");
    }

    private void RememberSourceReview(Guid profileId, SharedWorldVersion version)
    {
        var old = ReadReceivedLatest(ReceivedRoot(profileId));
        config!.PendingSharedWorldGroups ??= [];
        if (old is not null && old.GroupId != version.GroupId &&
            config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) != version.GroupId)
            config.PendingSharedWorldGroups[profileId] = version.GroupId;
        else
            config.PendingSharedWorldGroups.Remove(profileId);
    }

    public async Task<ReceivedSharedWorldResult> PullSharedWorldAsync(Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (!TryRetain()) return SharedFailure("ConnectionClosed", "This connection is closing.");
        var entered = false;
        try
        {
            await gate.WaitAsync(cancellationToken);
            entered = true;
            if (config is null) return SharedFailure("NotPaired", "Connect to a Host first.");
            if (config.ConsentedSharedWorldProfiles?.Contains(profileId) != true)
                return SharedFailure("ConsentRequired", "Allow shared saves on this PC first.");
            var profile = view.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (profile is null) return SharedFailure("UnknownProfile", "This server is not assigned to this PC.");
            if (profile.Kind == GameKinds.Custom)
                return SharedFailure("SharingUnsupported", "Custom game worlds cannot be shared.");
            if (!view.HostCapabilities.Contains(CompanionProtocol.SharedWorldsCapability, StringComparer.Ordinal))
                return SharedFailure("SharedWorldsUpdateRequired", "Update the Host app to receive shared saves.");
            var trustFailure = await TrustSharedRosterAsync(profileId, cancellationToken);
            if (trustFailure is not null) return trustFailure;
            using var response = await HostClient().GetAsync($"api/companion/servers/{profileId}/shared-world",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var manifestBytes = await ReadBoundedSharedAsync(response.Content,
                SharedWorldService.MaximumManifestBytes, cancellationToken);
            if (withdrawnSharedConsent.ContainsKey(profileId))
                return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
            if (manifestBytes is null) return SharedFailure("InvalidManifest", "The Host sent an oversized manifest.");
            if (!response.IsSuccessStatusCode)
                return RemoteSharedDenial(manifestBytes);
            SharedWorldVersion? version;
            try { version = JsonSerializer.Deserialize<SharedWorldVersion>(manifestBytes, Json); }
            catch (JsonException) { return SharedFailure("InvalidManifest", "The Host sent an invalid manifest."); }
            if (version is null || !SharedWorldService.VerifySignature(version) || version.ProfileId != profileId ||
                version.Game != profile.Kind || !SharedWorldSizeAllowed(version.Files))
                return SharedFailure("InvalidManifest", "The published version failed integrity or identity checks.");
            if (config.SharedRosterFloors?.GetValueOrDefault(profileId)?.GroupId != version.GroupId)
                return SharedFailure("GroupMismatch", "The save version does not match the verified shared roster.");
            config.SharedWorldSigningKeys ??= [];
            if (config.SharedWorldSigningKeys.TryGetValue(profileId, out var pinnedKey) &&
                pinnedKey != version.SigningPublicKey)
                return SharedFailure("SigningIdentityChanged", "The Host's world signing identity changed. Ask the owner to review it.");
            config.LastSharedHostGroups ??= [];
            if (config.LastSharedHostGroups.GetValueOrDefault(profileId) == version.GroupId &&
                config.LastSharedHostVersions?.GetValueOrDefault(profileId) > version.Number)
                return SharedFailure("VersionRollback", "The Host returned an older save version for this group.");
            config.LastSharedHostVersions ??= [];
            config.LastSharedHostVersions[profileId] = version.Number;
            config.LastSharedHostGroups[profileId] = version.GroupId;
            RememberSourceReview(profileId, version);
            SaveConfig();
            config.SharedWorldSigningKeys[profileId] = version.SigningPublicKey;
            SaveConfig();
            var root = ReceivedRoot(profileId);
            Directory.CreateDirectory(root);
            PrunePartialStages(root, version.VersionHash);
            var old = ReadReceivedLatest(root);
            var newWorldGroup = old is not null && old.GroupId != version.GroupId &&
                config.ApprovedSharedWorldGroups?.GetValueOrDefault(profileId) == version.GroupId;
            if (old is not null && old.GroupId != version.GroupId && !newWorldGroup)
                return SharedFailure("SourceReviewRequired",
                    "The Host changed this save source. Turn Allow saves off, then on to approve the new signed group. Earlier verified copies stay here.");
            if (old is not null && !newWorldGroup && (old.GroupId != version.GroupId ||
                old.Number > version.Number ||
                old.Number == version.Number && old.VersionHash != version.VersionHash ||
                old.Number + 1 == version.Number && version.ParentHash != old.VersionHash))
                return SharedFailure("VersionConflict", "The published version does not continue this PC's verified world history.");
            if (old?.VersionHash == version.VersionHash)
            {
                await SendSharedReceiptAsync(profileId, old, cancellationToken);
                return new(true, "AlreadyReceived", "This PC already has the latest verified save.",
                    LocalSharedWorldStatus(profileId));
            }
            if (old is not null && !newWorldGroup && version.Number - old.Number > 1 &&
                !await VerifySharedChainAsync(profileId, old, version, cancellationToken))
                return SharedFailure("VersionChainInvalid", "This PC could not verify every missed version's parent hash.");
            var stage = Path.Combine(root, ".partial-" + version.VersionHash);
            if (Directory.Exists(stage) && (File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0)
                return SharedFailure("LinkedVault", "The receiving vault contains a linked folder.");
            Directory.CreateDirectory(stage);
            var payloadStage = Path.Combine(stage, SharedWorldService.PayloadDirectory);
            var remaining = SharedWorldService.BoundedTotalBytes(version.Files) -
                version.Files.Sum(file => ExistingPartialBytes(payloadStage, file));
            var totalBytes = SharedWorldService.BoundedTotalBytes(version.Files);
            var receivedBytes = totalBytes - remaining;
            sharedTransfers[profileId] = new(true, version.Number, old?.Number,
                "Receiving", null, receivedBytes, totalBytes,
                config.SharedRosterFloors[profileId].Revision,
                "Owner signature and this PC's Receive grant verified when last checked");
            var driveRoot = Path.GetPathRoot(root)!;
            if (!HasReceiverReserve(new DriveInfo(driveRoot).AvailableFreeSpace, remaining))
                return SharedFailure("InsufficientSpace", "Keep at least 1 GiB free after receiving this save. Existing verified copies were kept.");
            for (var index = 0; index < version.Files.Count; index++)
            {
                var file = version.Files[index];
                var path = SharedWorldService.SafeChild(payloadStage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (file.Length == 0)
                {
                    if (!File.Exists(path)) File.WriteAllBytes(path, []);
                    SharedWorldService.VerifyFile(path, file);
                    continue;
                }
                if (File.Exists(path) && new FileInfo(path).Length == file.Length)
                {
                    try { SharedWorldService.VerifyFile(path, file); continue; }
                    catch (InvalidDataException) { File.Delete(path); }
                }
                var offset = ResumeOffset(path, file);
                if (offset < 0)
                {
                    File.Delete(path);
                    offset = 0;
                }
                if (offset == 0 && File.Exists(path)) File.Delete(path);
                await using var output = OpenPartialOutput(path, offset);
                output.Position = offset;
                while (offset < file.Length)
                {
                    if (withdrawnSharedConsent.ContainsKey(profileId))
                        return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
                    using var chunkResponse = await HostClient().GetAsync(
                        $"api/companion/servers/{profileId}/shared-world/{version.VersionHash}/files/{index}/chunks/{offset}",
                        HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    var chunk = await ReadBoundedSharedAsync(chunkResponse.Content,
                        SharedWorldService.ChunkBytes, cancellationToken);
                    if (withdrawnSharedConsent.ContainsKey(profileId))
                        return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
                    var expected = (int)Math.Min(SharedWorldService.ChunkBytes, file.Length - offset);
                    if (!chunkResponse.IsSuccessStatusCode || chunk is null || chunk.Length != expected)
                        return !chunkResponse.IsSuccessStatusCode ? RemoteSharedDenial(chunk) :
                            SharedFailure("TransferInterrupted", "The Host stopped this transfer. Progress was kept for retry.");
                    await output.WriteAsync(chunk, cancellationToken);
                    offset += chunk.Length;
                    receivedBytes += chunk.Length;
                    sharedTransfers[profileId] = new(true, version.Number, old?.Number,
                        "Receiving", null, receivedBytes, totalBytes,
                        config.SharedRosterFloors[profileId].Revision,
                        "Owner signature and this PC's Receive grant verified when last checked");
                }
                await output.FlushAsync(cancellationToken);
                await output.DisposeAsync();
                SharedWorldService.VerifyFile(path, file);
            }
            foreach (var file in version.Files)
                SharedWorldService.VerifyFile(SharedWorldService.SafeChild(payloadStage, file.Path), file);
            if (withdrawnSharedConsent.ContainsKey(profileId))
                return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
            File.WriteAllBytes(Path.Combine(stage, "version.json"), manifestBytes);
            var destination = Path.Combine(root, version.VersionHash);
            if (Directory.Exists(destination)) throw new InvalidDataException("Received version already exists.");
            if (!CommitSharedReceipt(profileId, stage, destination, root, manifestBytes))
                return SharedFailure("ConsentWithdrawn", "This PC stopped receiving shared saves.");
            config.SharedWorldSigningKeys[profileId] = version.SigningPublicKey;
            SaveConfig();
            var copyConfirmed = await SendSharedReceiptAsync(profileId, version, cancellationToken);
            try { PruneReceived(root, version.VersionHash); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { /* A verified receipt is kept even if old-version cleanup fails. */ }
            return new(true, "SaveReceived", copyConfirmed ?
                    "A completed save was verified here and confirmed to the Host." :
                    "A completed save was verified here. Host confirmation is pending; retry when connected.",
                LocalSharedWorldStatus(profileId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or
            CryptographicException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        { return SharedFailure("TransferFailed", "The transfer stopped; verified copies were kept. " + ex.Message); }
        finally { sharedTransfers.TryRemove(profileId, out _); if (entered) gate.Release(); ReleaseRetained(); }
    }

    internal static bool HasReceiverReserve(long freeBytes, long remainingBytes) =>
        freeBytes >= ReceiverReserveBytes && remainingBytes >= 0 &&
        freeBytes - ReceiverReserveBytes >= remainingBytes;

    private static bool SharedWorldSizeAllowed(IReadOnlyList<SharedWorldFile> files)
    {
        try { SharedWorldService.BoundedTotalBytes(files); return true; }
        catch (InvalidDataException) { return false; }
    }

    internal void WithdrawSharedConsent(Guid profileId)
    {
        lock (sharedReceiptSync) withdrawnSharedConsent[profileId] = 1;
    }

    internal bool CommitSharedReceipt(Guid profileId, string stage, string destination,
        string root, byte[] manifestBytes)
    {
        lock (sharedReceiptSync)
        {
            if (withdrawnSharedConsent.ContainsKey(profileId)) return false;
            Directory.Move(stage, destination);
            var latestStage = Path.Combine(root, "latest.json.new");
            File.WriteAllBytes(latestStage, manifestBytes);
            File.Move(latestStage, Path.Combine(root, "latest.json"), true);
            return true;
        }
    }

    private async Task<bool> VerifySharedChainAsync(Guid profileId, SharedWorldVersion old,
        SharedWorldVersion latest, CancellationToken cancellationToken)
    {
        if (latest.Number - old.Number > 1024) return false;
        var ancestors = new List<SharedWorldVersion>();
        for (var number = old.Number + 1; number < latest.Number; number++)
        {
            if (withdrawnSharedConsent.ContainsKey(profileId)) return false;
            using var response = await HostClient().GetAsync(
                $"api/companion/servers/{profileId}/shared-world/versions/{number}",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var bytes = await ReadBoundedSharedAsync(response.Content,
                SharedWorldService.MaximumManifestBytes, cancellationToken);
            if (!response.IsSuccessStatusCode || bytes is null) return false;
            var item = JsonSerializer.Deserialize<SharedWorldVersion>(bytes, Json);
            if (item is null) return false;
            ancestors.Add(item);
        }
        return VerifySharedChain(old, latest, ancestors);
    }

    internal static bool VerifySharedChain(SharedWorldVersion old, SharedWorldVersion latest,
        IReadOnlyList<SharedWorldVersion> ancestors)
    {
        if (old.Number >= latest.Number || ancestors.Count != latest.Number - old.Number - 1 ||
            old.GroupId != latest.GroupId || old.ProfileId != latest.ProfileId ||
            old.Game != latest.Game || old.WorldId != latest.WorldId ||
            old.SigningPublicKey != latest.SigningPublicKey ||
            !SharedWorldService.VerifySignature(old) || !SharedWorldService.VerifySignature(latest)) return false;
        var parent = old.VersionHash;
        for (var i = 0; i < ancestors.Count; i++)
        {
            var item = ancestors[i];
            if (!SharedWorldService.VerifySignature(item) || item.Number != old.Number + i + 1 ||
                item.GroupId != latest.GroupId || item.ProfileId != latest.ProfileId ||
                item.Game != latest.Game || item.WorldId != latest.WorldId ||
                item.SigningPublicKey != latest.SigningPublicKey || item.ParentHash != parent) return false;
            parent = item.VersionHash;
        }
        return latest.ParentHash == parent;
    }

    internal static long ExistingPartialBytes(string root, SharedWorldFile file)
    {
        var path = SharedWorldService.SafeChild(root, file.Path);
        var offset = ResumeOffset(path, file);
        if (offset == file.Length && offset > 0)
        {
            try { SharedWorldService.VerifyFile(path, file); }
            catch (InvalidDataException) { return 0; }
        }
        return offset > 0 ? offset : 0;
    }

    internal static long ResumeOffset(string path, SharedWorldFile file)
    {
        if (!File.Exists(path)) return 0;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return -1;
        var length = new FileInfo(path).Length;
        return length <= file.Length && length % SharedWorldService.ChunkBytes == 0 ? length : -1;
    }

    internal static FileStream OpenPartialOutput(string path, long offset) =>
        new(path, offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write,
            FileShare.None, SharedWorldService.ChunkBytes, FileOptions.WriteThrough);

    private async Task<bool> SendSharedReceiptAsync(Guid profileId, SharedWorldVersion version,
        CancellationToken cancellationToken)
    {
        if (config is null || config.ConsentedSharedWorldProfiles?.Contains(profileId) != true ||
            withdrawnSharedConsent.ContainsKey(profileId)) return false;
        var floor = config.SharedRosterFloors?.GetValueOrDefault(profileId);
        if (floor is null || floor.GroupId != version.GroupId) return false;
        // ReadReceivedLatest verifies every payload hash immediately before attesting.
        if (ReadReceivedLatest(ReceivedRoot(profileId))?.VersionHash != version.VersionHash) return false;
        using var key = LoadPcSigningKey();
        var receiptFile = Path.Combine(ReceivedRoot(profileId), version.VersionHash, "receipt.json");
        SharedWorldReceipt? receipt = null;
        if (File.Exists(receiptFile))
        {
            if (new FileInfo(receiptFile).Length > 4096 ||
                (File.GetAttributes(receiptFile) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Stored copy receipt is invalid or linked.");
            receipt = JsonSerializer.Deserialize<SharedWorldReceipt>(File.ReadAllBytes(receiptFile), Json);
            if (receipt is not null && (!SharedWorldReceiptTrust.Verify(receipt,
                    Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) ||
                receipt.DeviceId != config.DeviceId || receipt.ProfileId != profileId ||
                receipt.VersionHash != version.VersionHash || receipt.GroupId != version.GroupId))
                throw new InvalidDataException("Stored copy receipt failed verification.");
        }
        if (receipt is null || receipt.RosterEpoch != floor.Epoch || receipt.RosterRevision != floor.Revision)
        {
            var draft = new SharedWorldReceipt(1, version.GroupId, profileId, version.VersionHash,
                config.DeviceId, floor.Epoch, floor.Revision, Guid.NewGuid(), "");
            receipt = draft with { Signature = Convert.ToBase64String(key.SignData(
                SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256)) };
            var stage = receiptFile + ".new";
            File.WriteAllBytes(stage, JsonSerializer.SerializeToUtf8Bytes(receipt, Json));
            File.Move(stage, receiptFile, true);
        }
        try
        {
            using var response = await HostClient().PostAsJsonAsync(
                $"api/companion/servers/{profileId}/shared-world/receipts", receipt, Json, cancellationToken);
            return response.IsSuccessStatusCode && !withdrawnSharedConsent.ContainsKey(profileId);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        { return false; }
    }

    private ECDsa LoadPcSigningKey()
    {
        var path = $"shared-world-pc-signing-{config!.DeviceId:N}.protected";
        var bytes = data.LoadProtected(path);
        if (bytes is null)
        {
            using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            bytes = created.ExportPkcs8PrivateKey();
            data.SaveProtected(path, bytes);
        }
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(bytes, out _);
        return key;
    }

    internal static SharedWorldVersion? ReadReceivedLatest(string root)
    {
        var path = Path.Combine(root, "latest.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > SharedWorldService.MaximumManifestBytes ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The received version metadata is oversized or linked.");
        var version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json);
        if (version is null || !SharedWorldService.VerifySignature(version) ||
            !Directory.Exists(Path.Combine(root, version.VersionHash)))
            throw new InvalidDataException("The prior received version failed verification.");
        foreach (var file in version.Files)
            SharedWorldService.VerifyFile(SharedWorldService.SafeChild(
                Path.Combine(root, version.VersionHash, SharedWorldService.PayloadDirectory), file.Path), file);
        return version;
    }

    internal static void PruneReceived(string root, string newest)
    {
        var versions = Directory.EnumerateDirectories(root)
            .Where(path => Path.GetFileName(path).Length == 64 &&
                Path.GetFileName(path).All(Uri.IsHexDigit))
            .Select(path => (Path: path, Version: ReadVersionForRetention(path)))
            .Where(item => item.Version is not null && SharedWorldService.VerifySignature(item.Version))
            .ToList();
        foreach (var group in versions.GroupBy(item => item.Version!.GroupId))
        {
            var keep = group.OrderByDescending(item => item.Version!.Number)
                .Take(3).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            keep.Add(Path.Combine(root, newest));
            foreach (var old in group.Where(item => !keep.Contains(item.Path)))
                if (!ContainsReparsePoint(old.Path)) Directory.Delete(old.Path, true);
        }
    }

    private static void PrunePartialStages(string root, string currentHash)
    {
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(".partial-", StringComparison.Ordinal) ||
                name == ".partial-" + currentHash ||
                name.Length != ".partial-".Length + 64 ||
                !name[".partial-".Length..].All(Uri.IsHexDigit) || ContainsReparsePoint(path)) continue;
            Directory.Delete(path, true);
        }
    }

    private static SharedWorldVersion? ReadVersionForRetention(string path)
    {
        try
        {
            var file = Path.Combine(path, "version.json");
            if (!File.Exists(file) || new FileInfo(file).Length > SharedWorldService.MaximumManifestBytes ||
                ContainsReparsePoint(path)) return null;
            var version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(file), Json);
            if (version is null || !SharedWorldService.VerifySignature(version) ||
                !Path.GetFileName(path).Equals(version.VersionHash, StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var item in version.Files)
                SharedWorldService.VerifyFile(SharedWorldService.SafeChild(
                    Path.Combine(path, SharedWorldService.PayloadDirectory), item.Path), item);
            return version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return null; }
    }

    private static bool ContainsReparsePoint(string root)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) return true;
                if (Directory.Exists(child)) queue.Enqueue(child);
            }
        }
        return false;
    }

    internal static async Task<byte[]?> ReadBoundedSharedAsync(HttpContent content, long maximum,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximum) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximum) return null;
            output.Write(buffer, 0, read);
        }
    }

    private static ReceivedSharedWorldResult SharedFailure(string code, string message) =>
        new(false, code, message);

    private static ReceivedSharedWorldResult RemoteSharedDenial(byte[]? payload)
    {
        if (payload is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("code", out var codeElement) &&
                    codeElement.GetString() is { } code)
                {
                    return code switch
                    {
                        "Revoked" => SharedFailure(code, "The Host removed this PC's access."),
                        "AccessExpired" => SharedFailure(code, "The Host ended this PC's access at its saved deadline."),
                        "ApprovalPending" => SharedFailure(code, "The Host has not approved this PC."),
                        "PermissionDenied" => SharedFailure(code, "The Host has not granted this PC shared save access."),
                        "SharingOff" => SharedFailure(code, "The Host turned off sharing for this server."),
                        "NoPublishedSave" => SharedFailure(code, "The Host has no completed post-Stop save yet."),
                        "SharedVersionUnavailable" => SharedFailure(code,
                            "This save version is no longer available from the Host. Check for the latest save."),
                        "SharedChunkUnavailable" => SharedFailure(code,
                            "This save payload is no longer available from the Host. Check for the latest save."),
                        _ => SharedFailure("HostDenied", "The Host denied this shared save read.")
                    };
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }
        return SharedFailure("HostDenied", "The Host denied this shared save read.");
    }
}
