using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

internal sealed record WorldPeerReceiveConsentDecision(bool? Enabled, long Revision, bool NeedsReview = false);

// Local consent follows the pinned source world and source member, never a
// Host address, bearer credential, local hosting profile or session alias.
internal sealed class WorldPeerReceiveConsentStore(LocalData data)
{
    internal const string StateName = "world-peer-receive-consent.protected";
    internal const int MaximumEntries = 512;
    private const int MaximumBytes = 256 * 1024;
    private sealed record Entry(SharedWorldPeerRoot Root, Guid DeviceId, bool Enabled, long Revision);
    private sealed record State(int Schema, IReadOnlyList<Entry> Overrides, bool Denied = false);
    private sealed class Failure { internal bool Denied; }
    private static readonly ConditionalWeakTable<object, Failure> Failures = new();
    private static readonly ConditionalWeakTable<object,
        ConcurrentDictionary<(SharedWorldPeerRoot Root, Guid Device), WorldPeerReceiveConsentDecision>> Observations = new();
    private static readonly ConditionalWeakTable<object,
        ConcurrentDictionary<(SharedWorldPeerRoot Root, Guid Device), bool>> EffectiveObservations = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private object Sync => SharedWorldMutationGate.For(data.RootPath);

    // Display-only process observation, shared across aliases/LocalData wrappers
    // for this same app directory. Reading active transfer progress must not
    // wait for a world mutation lock or reopen protected consent state. Actual
    // transfers and launch continue to call Read/Effective authoritatively.
    internal bool? ObservedEnabled(SharedWorldPeerRoot root, Guid deviceId)
    {
        if (Failures.TryGetValue(Sync, out var failure) && failure.Denied) return false;
        if (Observations.TryGetValue(Sync, out var items) && items.TryGetValue((root, deviceId), out var item) &&
            item.Enabled is { } explicitDecision) return explicitDecision;
        return EffectiveObservations.TryGetValue(Sync, out var effective) && effective.TryGetValue((root, deviceId), out var enabled)
            ? enabled : null;
    }
    private WorldPeerReceiveConsentDecision Observe(SharedWorldPeerRoot root, Guid deviceId, WorldPeerReceiveConsentDecision decision)
    {
        Observations.GetValue(Sync, _ => new())[(root, deviceId)] = decision;
        return decision;
    }

    internal WorldPeerReceiveConsentDecision Read(SharedWorldPeerRoot root, Guid deviceId)
    {
        lock (Sync)
        {
            if (!SharedWorldPeerTrust.ValidRoot(root)) return new(false, -1, true);
            var state = Load();
            if (state is null || state.Denied)
            {
                Failures.GetValue(Sync, _ => new Failure()).Denied = true;
                return Observe(root, deviceId, new(false, -1, true));
            }
            var item = state.Overrides.SingleOrDefault(item => item.Root == root && item.DeviceId == deviceId);
            return Observe(root, deviceId, item is null ? new(null, 0) : new(item.Enabled, item.Revision));
        }
    }

    internal bool Effective(SharedWorldPeerRoot root, Guid deviceId, bool legacyConsent)
    {
        var enabled = Read(root, deviceId).Enabled ?? legacyConsent;
        // A verified automatic/legacy decision is useful cached display state
        // too. In particular, a busy link must not turn an already confirmed
        // automatic Receive policy into a false local-consent refusal. Signed
        // permission and transfer/launch checks still call this method fresh.
        EffectiveObservations.GetValue(Sync, _ => new())[(root, deviceId)] = enabled;
        return enabled;
    }

    // expectedRevision prevents an older queued opt-in from undoing a newer
    // immediate withdrawal. A deny never depends on a previously captured view.
    internal bool Set(SharedWorldPeerRoot root, Guid deviceId, bool enabled, long? expectedRevision = null)
    {
        if (!SharedWorldPeerTrust.ValidRoot(root)) throw new InvalidDataException("The trusted world identity is invalid.");
        lock (Sync)
        {
            try
            {
                var state = Load();
                if (state is null || state.Denied)
                {
                    if (enabled) throw new InvalidDataException("Receiving consent needs local review before it can be enabled.");
                    Failures.GetValue(Sync, _ => new Failure()).Denied = true;
                    Observe(root, deviceId, new(false, -1, true));
                    return true; // Corrupt or exhausted consent state already denies every receive path.
                }
                var old = state.Overrides.SingleOrDefault(item => item.Root == root && item.DeviceId == deviceId);
                if (enabled && expectedRevision is { } revision && revision != (old?.Revision ?? 0)) return false;
                var entries = state.Overrides.Where(item => item.Root != root || item.DeviceId != deviceId).ToList();
                var denied = false;
                if (old is null && entries.Count == MaximumEntries)
                {
                    if (enabled) throw new InvalidDataException("Receiving consent storage is full; existing decisions were kept.");
                    denied = true; // Preserve every decision and keep a withdrawal available at capacity.
                }
                else
                {
                    var next = checked(state.Overrides.Select(item => item.Revision).DefaultIfEmpty().Max() + 1);
                    entries.Add(new(root, deviceId, enabled, next));
                }
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(1, entries, denied), Json);
                if (bytes.Length > MaximumBytes && !enabled)
                {
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new State(1, state.Overrides, true), Json);
                    denied = true;
                }
                if (bytes.Length > MaximumBytes) throw new InvalidDataException("Receiving consent storage is oversized.");
                data.SaveProtected(StateName, bytes);
                if (denied) Failures.GetValue(Sync, _ => new Failure()).Denied = true;
                Observe(root, deviceId, new(denied ? false : enabled,
                    entries.LastOrDefault(item => item.Root == root && item.DeviceId == deviceId)?.Revision ?? -1, denied));
                return true;
            }
            catch
            {
                // Even a pre-write failure cannot revive a prior allow for an
                // already requested deny in this process.
                if (!enabled) Failures.GetValue(Sync, _ => new Failure()).Denied = true;
                throw;
            }
        }
    }

    private State? Load()
    {
        if (Failures.GetValue(Sync, _ => new Failure()).Denied) return null;
        try
        {
            var existed = data.HasProtected(StateName);
            var path = Path.Combine(data.RootPath, StateName);
            if (existed && (new FileInfo(path).Length > MaximumBytes * 2L ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("Receiving consent storage is oversized or linked.");
            var bytes = data.LoadProtected(StateName);
            if (bytes is null)
            {
                if (existed || data.Recovery.Notices.Any(notice => notice.StateFile == StateName))
                    throw new InvalidDataException("Receiving consent storage could not be read.");
                return new(1, []);
            }
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Receiving consent storage is oversized.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            CheckDuplicates(document.RootElement);
            var state = JsonSerializer.Deserialize<State>(bytes, Json);
            if (state is not { Schema: 1 } || state.Overrides is null || state.Overrides.Count > MaximumEntries ||
                state.Overrides.Any(item => item is null || !SharedWorldPeerTrust.ValidRoot(item.Root) ||
                    item.Revision is < 1 or long.MaxValue) ||
                state.Overrides.Select(item => (item.Root.GroupKey, item.DeviceId)).Distinct().Count() != state.Overrides.Count)
                throw new InvalidDataException("Receiving consent storage failed verification.");
            return state;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or JsonException or InvalidDataException or
            ArgumentException or NotSupportedException)
        {
            Failures.GetValue(Sync, _ => new Failure()).Denied = true;
            return null;
        }
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Receiving consent storage has duplicate fields.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
}
