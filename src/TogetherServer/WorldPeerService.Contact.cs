using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record WorldPeerContactCard(int Schema, SharedWorldPeerRoot Root, Guid DeviceId,
    string PublicKey, long Revision, IReadOnlyList<SharedWorldPeerRoute> Routes, string Signature);

internal sealed partial class WorldPeerService
{
    internal static byte[] ContactBasis(WorldPeerContactCard card) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "TogetherServer world peer contact v1",
        card.Schema,
        card.Root,
        card.DeviceId,
        card.PublicKey,
        card.Revision,
        card.Routes
    }, Json);

    internal string CreateContactCode(string groupKey)
    {
        var group = Find(groupKey) ?? throw new InvalidDataException("Choose a known shared world.");
        using var key = LocalKey(group) ?? throw new InvalidDataException("This world membership is unavailable.");
        var self = Store(group).Project().Peers.SingleOrDefault(item => item.DeviceId == group.DeviceId &&
            item.PublicKey == Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        if (self is null || self.HasConflict || self.Routes.Count == 0 || !group.SharingEnabled)
            throw new InvalidDataException("Enable this PC's peer connection and sync its signed address first.");
        var card = new WorldPeerContactCard(1, group.Root, group.DeviceId, self.PublicKey,
            self.Revision, self.Routes, "");
        card = card with { Signature = Convert.ToBase64String(key.SignData(ContactBasis(card), HashAlgorithmName.SHA256)) };
        return "TSW1-" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(card, Json));
    }

    // A contact is a pinned address/key hint. The owner-rooted handshake must
    // separately prove current membership before any roster or world exchange.
    private bool ValidContact(Group group, WorldPeerContactCard card)
        => card.Root == group.Root && VerifyContact(card);

    internal static bool VerifyContact(WorldPeerContactCard card)
    {
        if (card.Schema != 1 || !SharedWorldPeerTrust.ValidRoot(card.Root) || card.Revision < 1 || card.Routes is null ||
            card.Routes.Count is < 1 or > 4 || card.Routes.Any(route => !SharedWorldPeerTrust.ValidRoute(route)) ||
            card.Routes.Select(route => route.Address).Distinct(StringComparer.Ordinal).Count() != card.Routes.Count ||
            !SharedWorldRosterTrust.ValidKey(card.PublicKey) ||
            card.Signature is not { Length: >= 80 and <= 192 }) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(card.PublicKey), out _);
            return key.VerifyData(ContactBasis(card), Convert.FromBase64String(card.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or CryptographicException)
        { return false; }
    }

    private IReadOnlyList<SharedWorldPeerRoute> AdditionalRoutes(Group group)
    {
        var bytes = data.LoadProtected($"world-peer-contacts-{group.Root.GroupKey}.protected");
        if (bytes is null) return [];
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("Peer contacts need review.");
        var cards = JsonSerializer.Deserialize<WorldPeerContactCard[]>(bytes, Json);
        if (cards is null || cards.Length > 128) throw new InvalidDataException("Peer contacts need review.");
        return cards.Where(card => ValidContact(group, card)).SelectMany(card => card.Routes).Distinct().ToArray();
    }

    private string? AdditionalRoutePublicKey(Group group, SharedWorldPeerRoute route)
    {
        var bytes = data.LoadProtected($"world-peer-contacts-{group.Root.GroupKey}.protected");
        if (bytes is null) return null;
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("Peer contacts need review.");
        var cards = JsonSerializer.Deserialize<WorldPeerContactCard[]>(bytes, Json) ?? [];
        if (cards.Length > 128) throw new InvalidDataException("Peer contacts need review.");
        var keys = cards.Where(card => ValidContact(group, card) && card.Routes.Contains(route))
            .Select(card => card.PublicKey).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return keys.Length == 1 ? keys[0] : null;
    }

    internal WorldPeerActionResult ImportContactCode(string groupKey, string code)
    {
        var group = Find(groupKey);
        if (group is null || code is not { Length: > 5 and <= 16384 } || !code.StartsWith("TSW1-", StringComparison.Ordinal))
            return new(false, "PeerContactRejected", "Use a contact code from an approved member of this known world.");
        try
        {
            var bytes = Convert.FromBase64String(code[5..]);
            var card = WorldPeerProtocol.DecodeEnvelope<WorldPeerContactCard>(bytes, 12288);
            if (!ValidContact(group, card)) return new(false, "PeerContactRejected", "The signed contact does not match this known world.");
            lock (stateSync)
            {
                var name = $"world-peer-contacts-{groupKey}.protected";
                var oldBytes = data.LoadProtected(name);
                var cards = oldBytes is null ? [] : JsonSerializer.Deserialize<List<WorldPeerContactCard>>(oldBytes, Json) ?? [];
                var prior = cards.SingleOrDefault(item => item.DeviceId == card.DeviceId && item.PublicKey == card.PublicKey);
                var knownRevision = Store(group).Project().Peers.FirstOrDefault(item => item.DeviceId == card.DeviceId &&
                    item.PublicKey == card.PublicKey)?.Revision ?? 0;
                if (card.Revision < Math.Max(prior?.Revision ?? 0, knownRevision))
                    return new(false, "PeerContactOlder", "This contact is older than the signed information already known.");
                if (prior?.Revision == card.Revision && !prior.Routes.SequenceEqual(card.Routes))
                    return new(false, "PeerContactConflict", "Two signed contacts disagree at the same revision. Existing contact information was kept for review.");
                cards.RemoveAll(item => item.DeviceId == card.DeviceId && item.PublicKey == card.PublicKey);
                cards.Add(card);
                if (cards.Count > 128) return new(false, "PeerContactCapacity", "Peer contact capacity needs review.");
                var stored = JsonSerializer.SerializeToUtf8Bytes(cards, Json);
                if (stored.Length > 256 * 1024) return new(false, "PeerContactCapacity", "Peer contact capacity needs review.");
                data.SaveProtected(name, stored);
                RequestBackgroundSync();
            }
            return new(true, "PeerContactStored", "The signed peer address was saved. Sync verifies its current membership and pinned connection.");
        }
        catch (Exception error) when (PeerFailure(error) || error is FormatException)
        { return new(false, "PeerContactRejected", "The signed contact code could not be verified."); }
    }
}
