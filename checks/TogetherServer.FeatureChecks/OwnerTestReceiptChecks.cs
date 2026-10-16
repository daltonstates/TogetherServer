using System.Security.Cryptography;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class OwnerTestReceiptChecks
{
    internal static void Run()
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new SharedWorldPeerRoot(Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo()), Guid.NewGuid(), Guid.NewGuid());
        var draft = new SharedWorldReceipt(2, root.GroupId, root.ProfileId, new string('A', 64), Guid.Empty, 1, 1, Guid.NewGuid(), "");
        var receipt = draft with { Signature = Convert.ToBase64String(owner.SignData(SharedWorldReceiptTrust.Basis(draft), HashAlgorithmName.SHA256)) };
        Require(WorldPeerOwnerTestReceiptTrust.Verify(receipt, root), "The reserved owner private-test receipt must verify with the exact root key.");
        Require(!SharedWorldReceiptTrust.Verify(receipt, root.OwnerPublicKey), "A private owner receipt must never satisfy canonical save/election checks.");
        Require(!WorldPeerOwnerTestReceiptTrust.Verify(receipt with { VersionHash = new string('B', 64) }, root), "The receipt must bind the exact payload version.");
        Require(!WorldPeerOwnerTestReceiptTrust.Verify(receipt, root with { GroupId = Guid.NewGuid() }), "Another world's owner receipt must be rejected.");
        Require(!WorldPeerOwnerTestReceiptTrust.Verify(receipt with { DeviceId = Guid.NewGuid() }, root), "This envelope must never impersonate an enrolled Friend identity.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
