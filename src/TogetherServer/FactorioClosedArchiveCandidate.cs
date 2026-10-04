using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace TogetherServer;

// Internal, unaccepted evidence only. No route or shared-world publisher uses it.
internal sealed record FactorioClosedArchiveSnapshot(Guid ProfileId, Guid OperationId,
    string ArchivePath, long ArchiveLength, int EntryCount, string SourceBeforeSha256,
    string SourceAfterSha256, string StagedSha256)
{
    public bool CompletionConfirmed => false;
    public bool LiveCaptureAccepted => false;
}

internal sealed record FactorioArchiveInspection(long ArchiveLength, int EntryCount);

internal sealed class FactorioClosedArchiveCandidate(LocalData data)
{
    internal const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    internal const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    internal const long MaximumEntryBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaximumEntries = 4096;
    private const int MaximumPathLength = 1024;
    private static readonly uint[] CrcTable = MakeCrcTable();

    internal FactorioClosedArchiveSnapshot Stage(ManagedRun requestedRun)
    {
        var (run, profile, _) = ExactManagedFactorioRun.Require(data, requestedRun, requireReady: true);
        using var process = ExactManagedFactorioRun.OpenProcess(run);

        // The fixed driver starts this one imported ZIP. The caller supplies only
        // the run identity, never an archive path or command.
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, run.WorldDirectory);
        var sourcePath = Path.Combine(run.WorldDirectory, run.WorldId + ".zip");
        if (!FactorioSetup.IsImportedCopy(data, profile) || !ManagedImportFiles.PlainFile(sourcePath))
            throw new InvalidDataException("The reviewed Factorio save ZIP is unavailable.");

        var candidateRoot = Path.Combine(data.RootPath, "factorio-archive-candidates",
            run.ProfileId.ToString("N"), run.OperationId.ToString("N"));
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, candidateRoot);
        Directory.CreateDirectory(candidateRoot);
        SharedWorldService.EnsureUnlinkedRoot(data.RootPath, candidateRoot);
        var finalPath = Path.Combine(candidateRoot, "archive.zip");
        if (File.Exists(finalPath))
            throw new InvalidOperationException("This Factorio run already has an immutable archive candidate.");
        var temporary = Path.Combine(candidateRoot, "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            // Exclusive access proves the ZIP is closed at this instant and
            // prevents a normal game write until inspection and staging finish.
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            var inspected = InspectArchiveContents(source);
            source.Position = 0;
            var before = SHA256.HashData(source);
            source.Position = 0;
            using (var staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
            {
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                int read;
                while ((read = source.Read(buffer)) != 0)
                {
                    copied = checked(copied + read);
                    if (copied > MaximumArchiveBytes)
                        throw new InvalidDataException("The Factorio archive exceeds the candidate size limit.");
                    staged.Write(buffer.AsSpan(0, read));
                }
                if (copied != inspected.ArchiveLength)
                    throw new InvalidDataException("The Factorio archive length changed during staging.");
                staged.Flush(true);
            }

            source.Position = 0;
            var after = SHA256.HashData(source);
            byte[] stagedHash;
            using (var stagedRead = new FileStream(temporary, FileMode.Open, FileAccess.Read,
                       FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                if (stagedRead.Length != inspected.ArchiveLength)
                    throw new InvalidDataException("The staged Factorio archive length differs from its source.");
                stagedHash = SHA256.HashData(stagedRead);
                if (!CryptographicOperations.FixedTimeEquals(before, after) ||
                    !CryptographicOperations.FixedTimeEquals(before, stagedHash))
                    throw new CryptographicException("The Factorio archive changed during staging.");
                stagedRead.Position = 0;
                var stagedInspection = InspectArchiveContents(stagedRead);
                if (stagedInspection != inspected)
                    throw new InvalidDataException("The staged Factorio archive structure differs from its source.");
            }

            // Recheck the saved run/profile and exact process before the only
            // finalization step. A stopped or edited run leaves no final archive.
            _ = ExactManagedFactorioRun.Require(data, run, requireReady: true);
            ExactManagedFactorioRun.VerifyProcess(process, run);
            if (!ManagedImportFiles.PlainFile(sourcePath))
                throw new InvalidDataException("The reviewed Factorio save ZIP changed location.");
            SharedWorldService.EnsureUnlinkedRoot(data.RootPath, candidateRoot);
            File.SetAttributes(temporary, FileAttributes.ReadOnly);
            File.Move(temporary, finalPath); // Same directory, no replacement.
            return new(run.ProfileId, run.OperationId, finalPath, inspected.ArchiveLength,
                inspected.EntryCount, Convert.ToHexString(before), Convert.ToHexString(after),
                Convert.ToHexString(stagedHash));
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.SetAttributes(temporary, FileAttributes.Normal);
                    File.Delete(temporary);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // Kept separate for pure file checks. Production always opens the source
    // with FileShare.None before calling this inspector.
    internal static FactorioArchiveInspection InspectArchiveContents(FileStream file)
    {
        if (!file.CanRead || !file.CanSeek || file.Length is < 22 or > MaximumArchiveBytes)
            throw new InvalidDataException("The Factorio archive size is invalid.");
        var expectedEntries = ReadClosedCentralDirectory(file);
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != expectedEntries)
            throw new InvalidDataException("The Factorio archive entry count changed.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[64 * 1024];
        long expanded = 0;
        var nonemptyFiles = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            var directory = name.EndsWith('/');
            var normalized = directory ? name[..^1] : name;
            if (!ValidEntryPath(normalized) || !names.Add(normalized) ||
                (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 ||
                entry.Length is < 0 or > MaximumEntryBytes ||
                entry.CompressedLength is < 0 or > MaximumArchiveBytes ||
                (directory && entry.Length != 0))
                throw new InvalidDataException("The Factorio archive has an unsafe entry.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes)
                throw new InvalidDataException("The Factorio archive expands beyond the candidate limit.");
            if (!directory) files.Add(normalized);

            using var content = entry.Open();
            long actual = 0;
            var crc = uint.MaxValue;
            int read;
            while ((read = content.Read(buffer)) != 0)
            {
                actual = checked(actual + read);
                if (actual > entry.Length || actual > MaximumEntryBytes)
                    throw new InvalidDataException("The Factorio archive entry exceeded its declared size.");
                crc = UpdateCrc(crc, buffer.AsSpan(0, read));
            }
            if (actual != entry.Length || ~crc != entry.Crc32)
                throw new InvalidDataException("The Factorio archive entry content or CRC is invalid.");
            if (!directory && actual > 0) nonemptyFiles++;
        }
        if (nonemptyFiles == 0)
            throw new InvalidDataException("The Factorio archive has no readable file content.");
        foreach (var name in names)
        {
            var slash = name.LastIndexOf('/');
            while (slash >= 0)
            {
                if (files.Contains(name[..slash]))
                    throw new InvalidDataException("The Factorio archive has a file used as a directory.");
                slash = name.LastIndexOf('/', slash - 1);
            }
        }
        return new(file.Length, expectedEntries);
    }

    private static int ReadClosedCentralDirectory(FileStream file)
    {
        const int minimumEndRecord = 22;
        var tailLength = (int)Math.Min(file.Length, minimumEndRecord + ushort.MaxValue);
        var tail = new byte[tailLength];
        file.Position = file.Length - tailLength;
        file.ReadExactly(tail);
        for (var index = tail.Length - minimumEndRecord; index >= 0; index--)
        {
            var end = tail.AsSpan(index);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054B50 ||
                BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != tail.Length - index - minimumEndRecord)
                continue;
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(end[4..]);
            var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[6..]);
            var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(end[8..]);
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            var centralBytes = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
            var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
            var endOffset = file.Length - tail.Length + index;
            if (disk != 0 || centralDisk != 0 || diskEntries != entries ||
                entries is < 1 or > MaximumEntries ||
                centralBytes == uint.MaxValue || centralOffset == uint.MaxValue ||
                centralBytes < entries * 46L || centralOffset + (long)centralBytes != endOffset)
                throw new InvalidDataException("The Factorio ZIP central directory is invalid or unsupported.");
            return entries;
        }
        throw new InvalidDataException("The Factorio ZIP is not fully closed.");
    }

    private static bool ValidEntryPath(string name)
    {
        if (name.Length is < 1 or > MaximumPathLength || name.StartsWith('/') ||
            name.Contains('\\') || name.Any(ch => char.IsControl(ch) || ch is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
            return false;
        var parts = name.Split('/');
        return parts.Length <= 64 && parts.All(part => part.Length is >= 1 and <= 255 && part is not "." and not "..");
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
