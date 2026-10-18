using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

public sealed record WorldPeerDefaultSelection(int Schema, SharedWorldPeerRoot Root,
    Guid SelectorDeviceId, string SelectorPublicKey, long Revision,
    IReadOnlyList<string> PreviousHashes, Guid DeviceId, string PublicKey,
    Guid SessionId, string BranchHash, DateTimeOffset SelectedUtc,
    string SelectionHash, string Signature);
public sealed record WorldPeerDefaultHead(string SelectionHash, Guid DeviceId,
    string PublicKey, Guid SessionId, string BranchHash, bool Authorized, bool TargetKnown);
public sealed record WorldPeerDefaultView(bool CanChoose, bool NeedsChoice,
    IReadOnlyList<WorldPeerDefaultHead> Heads);

internal static class WorldPeerDefaultTrust
{
    internal static byte[] Basis(WorldPeerDefaultSelection value) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "TogetherServer explicit world session default v1",
        value.Schema,
        value.Root,
        value.SelectorDeviceId,
        value.SelectorPublicKey,
        value.Revision,
        value.PreviousHashes,
        value.DeviceId,
        value.PublicKey,
        value.SessionId,
        value.BranchHash,
        value.SelectedUtc
    }, SharedWorldPeerTrust.Json);

    internal static WorldPeerDefaultSelection Sign(WorldPeerDefaultSelection value, ECDsa key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Basis(value)));
        return value with
        {
            SelectionHash = hash,
            Signature = Convert.ToBase64String(key.SignData(Basis(value), HashAlgorithmName.SHA256))
        };
    }

    internal static bool Verify(WorldPeerDefaultSelection value, SharedWorldPeerRoot root)
    {
        if (value is null || value.Schema != 1 || value.Root != root || !SharedWorldPeerTrust.ValidRoot(root) ||
            value.Revision < 1 || value.SessionId == Guid.Empty ||
            !SharedWorldRosterTrust.ValidKey(value.SelectorPublicKey) ||
            !SharedWorldRosterTrust.ValidKey(value.PublicKey) ||
            !SharedWorldPeerTrust.ValidHash(value.SelectionHash) || !SharedWorldPeerTrust.ValidHash(value.BranchHash) ||
            value.PreviousHashes is null || value.PreviousHashes.Count > 64 ||
            value.PreviousHashes.Any(hash => !SharedWorldPeerTrust.ValidHash(hash) || hash == value.SelectionHash) ||
            value.PreviousHashes.Distinct().Count() != value.PreviousHashes.Count ||
            value.SelectedUtc == default || value.SelectedUtc.Offset != TimeSpan.Zero ||
            value.Signature is not { Length: >= 80 and <= 192 }) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value.SelectorPublicKey), out var read);
            return read == Convert.FromBase64String(value.SelectorPublicKey).Length &&
                Convert.ToHexString(SHA256.HashData(Basis(value))) == value.SelectionHash &&
                key.VerifyData(Basis(value), Convert.FromBase64String(value.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        { return false; }
    }

    internal static WorldPeerDefaultSelection[] Heads(IEnumerable<WorldPeerDefaultSelection> values)
    {
        var all = values.ToArray();
        var parents = all.SelectMany(value => value.PreviousHashes).ToHashSet(StringComparer.Ordinal);
        return all.Where(value => !parents.Contains(value.SelectionHash)).ToArray();
    }

    internal static WorldPeerDefaultSelection[] Merge(SharedWorldPeerRoot root,
        IReadOnlyList<WorldPeerDefaultSelection> existing, IReadOnlyList<WorldPeerDefaultSelection> incoming,
        Func<Guid, string, bool> maySelect)
    {
        if (incoming.Count > 64 || existing.Count > 64 ||
            incoming.Any(value => !Verify(value, root)))
            throw new InvalidDataException("The signed group default page was rejected.");
        var all = existing.ToDictionary(value => value.SelectionHash, StringComparer.Ordinal);
        var page = incoming.GroupBy(value => value.SelectionHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var value in page.Values.OrderBy(value => value.Revision))
        {
            if (all.ContainsKey(value.SelectionHash) || !maySelect(value.SelectorDeviceId, value.SelectorPublicKey)) continue;
            var pending = new Dictionary<string, WorldPeerDefaultSelection>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            bool Include(string hash)
            {
                if (all.ContainsKey(hash) || pending.ContainsKey(hash)) return true;
                if (!page.TryGetValue(hash, out var node)) return false;
                if (!visiting.Add(hash)) throw new InvalidDataException("The group default ancestry contains a cycle.");
                foreach (var parent in node.PreviousHashes)
                    if (!Include(parent)) return false;
                var expected = node.PreviousHashes.Count == 0 ? 1 : checked(node.PreviousHashes.Max(parent =>
                    all.TryGetValue(parent, out var known) ? known.Revision : pending[parent].Revision) + 1);
                if (node.Revision != expected) throw new InvalidDataException("The group default has invalid ancestry.");
                if (all.Count + pending.Count >= 64)
                    throw new InvalidDataException("Group default history is full; existing choices were kept.");
                visiting.Remove(hash);
                pending.Add(hash, node);
                return true;
            }
            // A current manager expressly signs these parent hashes. Unapproved
            // ancestors are inert dependencies, never standalone imported heads.
            // Missing pages cannot partially persist this dependency closure.
            if (Include(value.SelectionHash))
                foreach (var node in pending) all.Add(node.Key, node.Value);
        }
        return all.Values.ToArray();
    }
}

internal sealed partial class WorldPeerService
{
    private string DefaultsName(Group group) => $"world-peer-defaults-{group.Root.GroupKey}.protected";

    private IReadOnlyList<WorldPeerDefaultSelection> ReadDefaultSelections(Group group)
    {
        lock (stateSync)
        {
            var name = DefaultsName(group);
            var existed = data.HasProtected(name);
            var bytes = data.LoadProtected(name);
            if (bytes is null)
            {
                if (existed || data.Recovery.Notices.Any(notice => notice.StateFile == name))
                    throw new InvalidDataException("The saved group default needs review.");
                return [];
            }
            if (bytes.Length > 256 * 1024) throw new InvalidDataException("The group default history is oversized.");
            var all = WorldPeerProtocol.DecodeEnvelope<WorldPeerDefaultSelection[]>(bytes, 256 * 1024);
            if (all.Length > 64 || all.Any(value => !WorldPeerDefaultTrust.Verify(value, group.Root)) ||
                all.Select(value => value.SelectionHash).Distinct().Count() != all.Length)
                throw new InvalidDataException("The group default history failed verification.");
            return all;
        }
    }

    private void ImportDefaultSelections(Group group, IReadOnlyList<WorldPeerDefaultSelection>? selections)
    {
        if (selections is null || selections.Count == 0) return;
        lock (stateSync)
        {
            group = Find(group.Root.GroupKey) ?? throw new InvalidDataException("World membership is unavailable.");
            var all = WorldPeerDefaultTrust.Merge(group.Root, ReadDefaultSelections(group), selections,
                (id, key) => Access(group, id, key)?.ManageSharing == true);
            data.SaveProtected(DefaultsName(group), JsonSerializer.SerializeToUtf8Bytes(all, Json));
        }
    }

    private WorldPeerDefaultView DefaultView(Group group, SharedWorldPeerGroupState state)
    {
        using var key = LocalKey(group);
        var mayChoose = key is not null && Access(group, group.DeviceId,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))?.ManageSharing == true;
        var heads = WorldPeerDefaultTrust.Heads(ReadDefaultSelections(group)).Select(value =>
            new WorldPeerDefaultHead(value.SelectionHash, value.DeviceId, value.PublicKey,
                value.SessionId, value.BranchHash,
                Access(group, value.SelectorDeviceId, value.SelectorPublicKey)?.ManageSharing == true,
                state.Sessions.Any(session => session.DeviceId == value.DeviceId && session.PublicKey == value.PublicKey &&
                    session.Session.SessionId == value.SessionId && session.Session.BranchHash == value.BranchHash &&
                    session.Authorized && !session.HasConflict))).ToArray();
        return new(mayChoose, heads.Length > 1 || heads.Any(head => !head.Authorized || !head.TargetKnown), heads);
    }

    internal WorldPeerActionResult SelectDefault(string groupKey, Guid deviceId, Guid sessionId)
    {
        lock (stateSync)
        {
            var group = Find(groupKey);
            if (group is null) return new(false, "UnknownWorld", "Choose a known world.");
            using var key = LocalKey(group);
            if (key is null || Access(group, group.DeviceId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))?.ManageSharing != true)
                return new(false, "ManageSharingRequired", "The owner or an approved sharing manager must choose the group default.");
            var state = Store(group).Project();
            var target = state.Sessions.SingleOrDefault(value => value.DeviceId == deviceId &&
                value.Session.SessionId == sessionId && value.Authorized && !value.HasConflict &&
                value.Session.State is "Hosting" or "Preparing");
            if (target is null) return new(false, "UnknownWorldSession", "Refresh and choose a verified running session.");
            var old = ReadDefaultSelections(group);
            var heads = WorldPeerDefaultTrust.Heads(old);
            if (heads.Length == 1 && heads[0].DeviceId == deviceId && heads[0].SessionId == sessionId &&
                heads[0].PublicKey == target.PublicKey && heads[0].BranchHash == target.Session.BranchHash &&
                Access(group, heads[0].SelectorDeviceId, heads[0].SelectorPublicKey)?.ManageSharing == true)
                return new(true, "GroupDefaultUnchanged", "This session is already the group's selected default.");
            var draft = new WorldPeerDefaultSelection(1, group.Root, group.DeviceId,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                heads.Length == 0 ? 1 : checked(heads.Max(value => value.Revision) + 1),
                heads.Select(value => value.SelectionHash).Order(StringComparer.Ordinal).ToArray(),
                target.DeviceId, target.PublicKey, sessionId, target.Session.BranchHash,
                DateTimeOffset.UtcNow, "", "");
            ImportDefaultSelections(group, [WorldPeerDefaultTrust.Sign(draft, key)]);
            return new(true, "GroupDefaultSelected", "The signed group default was recorded for peer sync. Both histories and all running games were kept.");
        }
    }
}
