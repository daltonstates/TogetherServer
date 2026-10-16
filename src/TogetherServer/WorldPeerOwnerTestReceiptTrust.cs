using System.Security.Cryptography;

namespace TogetherServer;

// A separate private-test receipt envelope. Canonical save receipts deliberately
// continue rejecting schema 2 and the root owner's reserved device identity.
internal static class WorldPeerOwnerTestReceiptTrust
{
    internal static bool Verify(SharedWorldReceipt? receipt, SharedWorldPeerRoot root)
    {
        if (receipt is null || receipt.Schema != 2 || receipt.DeviceId != Guid.Empty ||
            receipt.GroupId != root.GroupId || receipt.ProfileId != root.ProfileId ||
            receipt.ReceiptId == Guid.Empty || receipt.RosterEpoch < 1 || receipt.RosterRevision < 1 ||
            !SharedWorldPeerTrust.ValidRoot(root) || !SharedWorldPeerTrust.ValidHash(receipt.VersionHash) ||
            receipt.Signature is not { Length: >= 80 and <= 192 }) return false;
        try
        {
            using var key = ECDsa.Create();
            var publicKey = Convert.FromBase64String(root.OwnerPublicKey);
            key.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            return consumed == publicKey.Length && key.KeySize == 256 &&
                key.VerifyData(SharedWorldReceiptTrust.Basis(receipt), Convert.FromBase64String(receipt.Signature),
                    HashAlgorithmName.SHA256);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        { return false; }
    }
}
