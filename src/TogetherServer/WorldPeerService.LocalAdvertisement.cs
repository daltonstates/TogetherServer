using System.Security.Cryptography;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class WorldPeerService
{
    // A private, normally null barrier for deterministic source-only checks.
    // It runs outside stateSync and supplies no authorization or payload input.
    private Action? localAdvertisementBuiltForChecks = null;
    // Serialize only the local metadata snapshot/sign/publish. No payload
    // hashing, transfer or managed launch holds this gate.
    private async Task<bool> PublishLocalAdvertisementAsync(Group requested, CancellationToken cancellationToken,
        bool background = false, bool retainedRoutesOnly = false)
    {
        var gates = ExchangeGatesFor(requested.Root);
        await gates.LocalAdvertisement.WaitAsync(cancellationToken);
        try
        {
            Group group;
            string publicKey;
            lock (stateSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (background && gates.Preparations > 0) return false;
                group = Find(requested.Root.GroupKey) ?? throw new InvalidDataException("World membership changed before its local advertisement.");
                if (group.Root != requested.Root) throw new InvalidDataException("The advertised world changed.");
                using var key = retainedRoutesOnly ? RetainedAdvertisementKey(group) : RetainedLocalKey(group);
                if (key is null) return false;
                publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
                if (KnownRosterRevision(group) < group.Roster.Revision || Access(group, group.DeviceId, publicKey) is null) return false;
            }
            var advertisement = await BuildAdvertisementAsync(group, cancellationToken, retainedRoutesOnly);
            try { localAdvertisementBuiltForChecks?.Invoke(); }
            catch (Exception) { /* A check observation cannot alter authorization. */ }
            lock (stateSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A snapshot begun before Start/Prepare must not publish later
                // and invalidate that instruction's already checked source.
                if (background && gates.Preparations > 0) return false;
                var current = Find(group.Root.GroupKey);
                using var key = current is null ? null : retainedRoutesOnly ? RetainedAdvertisementKey(current) : RetainedLocalKey(current);
                if (current is null || current.Root != group.Root || current.DeviceId != group.DeviceId ||
                    current.Roster.Signature != group.Roster.Signature || current.ReceiveConsented != group.ReceiveConsented ||
                    current.SharingEnabled != group.SharingEnabled || key is null ||
                    Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != publicKey ||
                    KnownRosterRevision(current) < current.Roster.Revision || Access(current, current.DeviceId, publicKey) is null)
                    throw new InvalidDataException("Current local advertisement identity or signed membership changed.");
                Store(current).Publish(current.DeviceId, key, advertisement.Routes, advertisement.Sessions,
                    advertisement.Branches, advertisement.AvailableVersionHashes);
                return true;
            }
        }
        finally { gates.LocalAdvertisement.Release(); }
    }

    // Foreground discovery consumes an existing key without quarantining or
    // replacing malformed protected records. Names are derived only from the
    // registered local identity, never a request-supplied path.
    private ECDsa? RetainedAdvertisementKey(Group group)
    {
        var name = group.DeviceId == Guid.Empty ? "shared-world-signing-key.protected" :
            $"shared-world-pc-signing-{group.DeviceId:N}.protected";
        var path = Path.Combine(data.RootPath, name);
        if (!File.Exists(path)) return null;
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length is < 1 or > 64 * 1024)
            throw new InvalidDataException("The existing local signing key is linked or oversized.");
        var encoded = JsonSerializer.Deserialize<string>(File.ReadAllBytes(path), Json);
        if (encoded is null) throw new InvalidDataException("The existing local signing key is unavailable.");
        byte[] ciphertext;
        try { ciphertext = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("The existing local signing key encoding is invalid.", error); }
        var plain = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(plain, out var read);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            if (read != plain.Length || !SharedWorldRosterTrust.ValidKey(publicKey) ||
                group.DeviceId == Guid.Empty && publicKey != group.Root.OwnerPublicKey)
                throw new InvalidDataException("The existing local signing identity changed.");
            return key;
        }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
