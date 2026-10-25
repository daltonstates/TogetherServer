using System.Buffers;
using System.Security.Cryptography;

namespace TogetherServer;

// Display-only work observations. No identity, path or peer-supplied text is
// exposed, and a failed observer cannot change verification or authorization.
internal sealed record WorldPeerSourceProgress(string Phase, string Message,
    long CompletedBytes = 0, long TotalBytes = 0, int CompletedFiles = 0, int TotalFiles = 0)
{
    internal static void Report(Action<WorldPeerSourceProgress>? observer, WorldPeerSourceProgress value)
    {
        try { observer?.Invoke(value); }
        catch (Exception) { /* Display failures cannot authorize a copy. */ }
    }
}

internal static class WorldPeerPayloadHash
{
    internal static void Report(Action<long, long, int, int>? observer, long bytes, long total, int files, int count)
    {
        try { observer?.Invoke(bytes, total, files, count); }
        catch (Exception) { /* Display callbacks never change payload verification. */ }
    }

    internal static string Hash(Stream input, CancellationToken cancellationToken, Action<long>? progress = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(512 * 1024);
        long readBytes = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer.AsSpan(0, 512 * 1024));
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                readBytes += read;
                try { progress?.Invoke(readBytes); }
                catch (Exception) { /* Hash verification is independent of display. */ }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    internal static void VerifyFile(string path, SharedWorldFile file, CancellationToken cancellationToken,
        Action<long>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Shared save file is missing or linked.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != file.Length || (File.GetAttributes(input.SafeFileHandle) & FileAttributes.ReparsePoint) != 0 ||
            !Hash(input, cancellationToken, progress).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Shared save file changed or failed its signed hash.");
    }
}
