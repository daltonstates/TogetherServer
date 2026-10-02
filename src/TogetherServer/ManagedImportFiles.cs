using System.Security.Cryptography;

namespace TogetherServer;

internal static class ManagedImportFiles
{
    public static bool PlainFile(string path) => File.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    public static bool PlainDirectory(string path) => Directory.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    public static void EnsurePlainDirectory(string path, string description)
    {
        Directory.CreateDirectory(path);
        if (!PlainDirectory(path)) throw new InvalidDataException(description + " cannot be a link or reparse point.");
    }

    public static long CopyVerified(string source, string destination)
    {
        if (!PlainFile(source)) throw new InvalidDataException("The source file is missing or linked.");
        long length = 0;
        byte[] sourceHash;
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                   1024 * 1024, FileOptions.SequentialScan))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   1024 * 1024, FileOptions.SequentialScan))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
                length = checked(length + read);
            }
            output.Flush(true);
            sourceHash = hash.GetHashAndReset();
        }
        using var copied = File.OpenRead(destination);
        if (copied.Length != length ||
            !CryptographicOperations.FixedTimeEquals(sourceHash, SHA256.HashData(copied)))
            throw new CryptographicException("The managed file copy did not verify.");
        using var original = File.OpenRead(source);
        if (original.Length != length ||
            !CryptographicOperations.FixedTimeEquals(sourceHash, SHA256.HashData(original)))
            throw new CryptographicException("The source changed while its managed copy was being verified.");
        return length;
    }
}
