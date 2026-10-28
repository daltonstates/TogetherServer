using System.Security.Cryptography;

namespace TogetherServer;

// Only canonical public-key parsing is reusable. This stores exact positive
// strings, never key handles, signatures, permissions or roster decisions.
internal sealed class CanonicalPublicKeyParsingCache(Action? validationStartedForChecks = null)
{
    internal const int MaximumEntries = 256;
    internal static readonly CanonicalPublicKeyParsingCache Shared = new();
    private readonly object sync = new();
    private readonly HashSet<string> valid = new(StringComparer.Ordinal);
    private readonly Queue<string> insertionOrder = new();
    internal int Count { get { lock (sync) return valid.Count; } }

    internal bool IsValid(string? value)
    {
        if (value is null || value.Length is < 80 or > 512) return false;
        lock (sync) if (valid.Contains(value)) return true;

        // CNG import/export happens outside the cache lock. Concurrent misses
        // may repeat this pure validation, then coalesce only their insertion.
        validationStartedForChecks?.Invoke();
        if (!ValidateCore(value)) return false;
        lock (sync)
        {
            if (valid.Contains(value)) return true;
            if (valid.Count >= MaximumEntries) valid.Remove(insertionOrder.Dequeue());
            valid.Add(value);
            insertionOrder.Enqueue(value);
        }
        return true;
    }

    // Preserve the original SharedWorldRosterTrust.ValidKey predicate exactly.
    private static bool ValidateCore(string? value)
    {
        try
        {
            if (value is null || value.Length is < 80 or > 512) return false;
            var bytes = Convert.FromBase64String(value);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            return read == bytes.Length && key.KeySize == 256 &&
                value == Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        { return false; }
    }
}
