using System.Security.Cryptography;
using System.Text.Json;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

// Pure signatures/projections and synthetic vault files only; never starts a game or transfer listener.
internal static class SharedWorldProjectionChecks
{
    internal static void Run(string root)
    {
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(deviceKey.ExportSubjectPublicKeyInfo());
        var profileId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var created = DateTimeOffset.Parse("2026-10-08T09:00:00-04:00");
        var signed = DateTimeOffset.Parse("2026-10-08T15:00:00Z");
        byte[] synthetic = [1, 2, 3, 4];
        var version = SharedWorldService.SignVersion(new(1, Guid.NewGuid(), 1, null, profileId,
            GameKinds.Valheim, "synthetic", created, SharedWorldCaptureKinds.PostStopBackup, Guid.NewGuid(),
            new(2456, false), [new("synthetic.db", synthetic.Length, Convert.ToHexString(SHA256.HashData(synthetic)))],
            "", "", ""), ownerKey);
        var vault = Path.Combine(root, "shared-projection", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(vault, version.VersionHash);
        var payload = Path.Combine(directory, SharedWorldService.PayloadDirectory);
        Directory.CreateDirectory(payload);
        File.WriteAllBytes(Path.Combine(payload, "synthetic.db"), synthetic);
        File.WriteAllBytes(Path.Combine(vault, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(version, json));
        var verified = FriendLink.ReadReceivedLatest(vault);
        Require(verified is not null && ReceivedSharedWorldProjection.CompletedUtc(verified) == created.ToUniversalTime() &&
            ReceivedSharedWorldProjection.CompletedUtc(null) is null &&
            ReceivedSharedWorldProjection.CompletedUtc(version with { CreatedUtc = default }) is null,
            "completion date was not the verified local manifest date or an unavailable date was invented");

        var draft = new SharedWorldReceipt(1, version.GroupId, profileId, version.VersionHash, deviceId, 1, 1, Guid.NewGuid(), "");
        var receipt = draft with
        {
            Signature = Convert.ToBase64String(deviceKey.SignData(
            SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256))
        };
        var receiptFile = Path.Combine(directory, "receipt.json");
        var observationFile = Path.Combine(directory, "receipt-display.json");
        File.WriteAllBytes(receiptFile, JsonSerializer.SerializeToUtf8Bytes(receipt, json));
        File.SetLastWriteTimeUtc(receiptFile, signed.AddDays(-1).UtcDateTime);
        Require(FriendLink.ReadLocalReceiptObservationFiles(vault, verified!, deviceId, publicKey) is null,
            "legacy receipt time or acknowledgement was inferred from filesystem metadata");
        var pending = ReceivedSharedWorldProjection.Sign(receipt, signed, false, deviceKey);
        Require(ReceivedSharedWorldProjection.Verify(receipt, pending, publicKey) && pending.ReceivedUtc == signed &&
            !pending.HostConfirmed, "local signing time became Host acknowledgement");
        File.WriteAllBytes(observationFile, JsonSerializer.SerializeToUtf8Bytes(pending, json));
        var reloaded = FriendLink.ReadLocalReceiptObservationFiles(vault, verified!, deviceId, publicKey);
        Require(reloaded == pending, "signed local receipt display facts did not survive a file roundtrip");
        var confirmed = ReceivedSharedWorldProjection.Sign(receipt, signed, true, deviceKey);
        File.WriteAllBytes(observationFile, JsonSerializer.SerializeToUtf8Bytes(confirmed, json));
        Require(FriendLink.ReadLocalReceiptObservationFiles(vault, verified!, deviceId, publicKey) == confirmed &&
            confirmed.ReceivedUtc == pending.ReceivedUtc, "Host confirmation changed the actual local signing time");
        var oldConfirmed = ReceivedSharedWorldProjection.Sign(receipt, null, true, deviceKey);
        Require(ReceivedSharedWorldProjection.Verify(receipt, oldConfirmed, publicKey) && oldConfirmed.ReceivedUtc is null,
            "confirmation of an old receipt backfilled its unknown receive time");
        foreach (var changed in new[]
        {
            pending with { ReceivedUtc = signed.AddSeconds(1) }, pending with { HostConfirmed = true },
            pending with { ReceiptId = Guid.NewGuid() }, pending with { Schema = 2 }, pending with { Signature = "malformed" }
        }) Require(!ReceivedSharedWorldProjection.Verify(receipt, changed, publicKey), "tampered local receipt facts verified");
        Require(!ReceivedSharedWorldProjection.Verify(receipt, pending,
            Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo())), "another PC's key verified local receipt facts");
        Require(!ReceivedSharedWorldProjection.Verify(receipt,
            ReceivedSharedWorldProjection.Sign(receipt, signed.ToOffset(TimeSpan.FromHours(-4)), false, deviceKey), publicKey),
            "non-UTC local signing time was accepted");
        Require(FriendLink.ReadLocalReceiptObservationFiles(vault, verified!, Guid.NewGuid(), publicKey) is null &&
            FriendLink.ReadLocalReceiptObservationFiles(vault, verified! with { ProfileId = Guid.NewGuid() }, deviceId, publicKey) is null &&
            FriendLink.ReadLocalReceiptObservationFiles(vault, verified! with { GroupId = Guid.NewGuid() }, deviceId, publicKey) is null,
            "receipt display facts crossed device, profile or source-group boundaries");
        File.WriteAllBytes(observationFile, JsonSerializer.SerializeToUtf8Bytes(pending with { HostConfirmed = true }, json));
        Require(FriendLink.ReadLocalReceiptObservationFiles(vault, verified!, deviceId, publicKey) is null &&
            SharedWorldReceiptTrust.Verify(receipt, publicKey), "invalid display facts changed the protocol receipt's validity");
        File.WriteAllBytes(observationFile, new byte[SharedWorldReceiptTrust.MaximumRequestBytes + 1]);
        Require(FriendLink.ReadLocalReceiptObservationFiles(vault, verified!, deviceId, publicKey) is null,
            "oversized local display metadata was read");

        var defaults = new ReceivedSharedWorldStatus(true, 1, null, "Receiving", ReceivedBytes: 4, TotalBytes: 4,
            TransferPhase: "Receiving");
        Require(defaults.CompletedUtc is null && defaults.ReceivedUtc is null && defaults.ReceiptConfirmed is null,
            "100 percent byte progress invented copy verification or Host receipt evidence");
        var oldCopy = defaults with
        {
            HostVersion = 2,
            ThisPcVersion = 1,
            CompletedUtc = created,
            ReceivedUtc = signed,
            ReceiptConfirmed = true,
            TransferPhase = "Verifying"
        };
        Require(oldCopy.HostVersion != oldCopy.ThisPcVersion && oldCopy.TransferPhase == "Verifying",
            "historical local dates became completion evidence for the newer active transfer");
        var legacy = JsonSerializer.Deserialize<ReceivedSharedWorldStatus>(
            "{\"consented\":true,\"hostVersion\":1,\"thisPcVersion\":1,\"state\":\"Up to date when last checked\"}", json)!;
        Require(legacy.CompletedUtc is null && legacy.ReceivedUtc is null && legacy.TransferPhase is null &&
            legacy.ReceiptConfirmed is null, "legacy projection gaps were replaced by invented facts");
        using var projected = JsonDocument.Parse(JsonSerializer.Serialize(oldCopy, json));
        Require(projected.RootElement.GetProperty("completedUtc").ValueKind == JsonValueKind.String &&
            projected.RootElement.GetProperty("receivedUtc").ValueKind == JsonValueKind.String &&
            projected.RootElement.GetProperty("transferPhase").GetString() == "Verifying" &&
            projected.RootElement.GetProperty("receiptConfirmed").GetBoolean(), "optional JSON field names or types drifted");

        File.WriteAllBytes(Path.Combine(payload, "synthetic.db"), [9, 8, 7, 6]);
        SharedWorldVersion? damaged = null;
        try { damaged = FriendLink.ReadReceivedLatest(vault); }
        catch (InvalidDataException) { }
        Require(damaged is null && ReceivedSharedWorldProjection.CompletedUtc(damaged) is null,
            "a payload failing verification retained a completed-copy date");
    }
}
