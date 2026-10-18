using System.Security.Cryptography;
using TogetherServer;

namespace TogetherServer.FeatureChecks;

internal static class WorldPeerDefaultChecks
{
    internal static void Run()
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var friend = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new SharedWorldPeerRoot(Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo()),
            Guid.NewGuid(), Guid.NewGuid());
        var friendKey = Convert.ToBase64String(friend.ExportSubjectPublicKeyInfo());
        var draft = new WorldPeerDefaultSelection(1, root, Guid.Empty, root.OwnerPublicKey, 1, [],
            Guid.NewGuid(), friendKey, Guid.NewGuid(), new string('A', 64), DateTimeOffset.UtcNow, "", "");
        var first = WorldPeerDefaultTrust.Sign(draft, owner);
        Require(WorldPeerDefaultTrust.Verify(first, root), "An owner-signed exact-session choice verifies.");
        Require(!WorldPeerDefaultTrust.Verify(first with { SessionId = Guid.NewGuid() }, root), "A changed session fails signature verification.");
        Require(!WorldPeerDefaultTrust.Verify(first, root with { GroupId = Guid.NewGuid() }), "Another world's default is rejected.");
        var competing = WorldPeerDefaultTrust.Sign(draft with { SessionId = Guid.NewGuid() }, owner);
        Require(WorldPeerDefaultTrust.Heads([first, competing]).Length == 2, "Concurrent choices remain visible, regardless of timestamp.");
        var resolved = WorldPeerDefaultTrust.Sign(draft with
        {
            Revision = 2,
            PreviousHashes = [first.SelectionHash, competing.SelectionHash]
        }, owner);
        Require(WorldPeerDefaultTrust.Heads([first, competing, resolved]).Single() == resolved,
            "An explicit choice naming both heads resolves only default selection.");
        Require(!WorldPeerDefaultTrust.Verify(first with { PreviousHashes = [first.SelectionHash] }, root), "A self-parent is rejected.");

        // A newcomer can catch up after the prior sharing manager lost that
        // role. Its old choice is needed as ancestry, but grants no current role.
        var formerManager = WorldPeerDefaultTrust.Sign(draft with
        { SelectorDeviceId = Guid.NewGuid(), SelectorPublicKey = friendKey }, friend);
        var reaffirmed = WorldPeerDefaultTrust.Sign(draft with
        { Revision = 2, PreviousHashes = [formerManager.SelectionHash] }, owner);
        var unrelated = WorldPeerDefaultTrust.Sign(draft with
        { SelectorDeviceId = Guid.NewGuid(), SelectorPublicKey = friendKey, SessionId = Guid.NewGuid() }, friend);
        bool OwnerOnly(Guid id, string key) => id == Guid.Empty && key == root.OwnerPublicKey;
        var caughtUp = WorldPeerDefaultTrust.Merge(root, [], [reaffirmed, unrelated, formerManager], OwnerOnly);
        Require(caughtUp.Length == 2 && WorldPeerDefaultTrust.Heads(caughtUp).Single() == reaffirmed &&
            caughtUp.All(value => value.SelectionHash != unrelated.SelectionHash),
            "A current owner's choice must import its exact inert ancestry without admitting unrelated unauthorized heads.");
        Require(WorldPeerDefaultTrust.Merge(root, [], [formerManager, unrelated], OwnerOnly).Length == 0,
            "Standalone choices from a former manager must not become imported heads.");
        Require(WorldPeerDefaultTrust.Merge(root, [], [reaffirmed], OwnerOnly).Length == 0,
            "Missing ancestry must not partially persist an owner resolution.");
        Require(Refuses(() => WorldPeerDefaultTrust.Merge(root, [],
            [formerManager, reaffirmed with { SessionId = Guid.NewGuid() }], OwnerOnly)),
            "A current role must not bypass the signed target binding.");
        var wrongRevision = WorldPeerDefaultTrust.Sign(draft with
        { Revision = 3, PreviousHashes = [formerManager.SelectionHash] }, owner);
        Require(Refuses(() => WorldPeerDefaultTrust.Merge(root, [], [formerManager, wrongRevision], OwnerOnly)),
            "A signed resolution must have contiguous dependency revisions.");
    }

    private static bool Refuses(Action action)
    { try { action(); return false; } catch (InvalidDataException) { return true; } }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
