using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TogetherServer;

// Internal observation candidate only. It has no capture, publication, or Host/Friend route.
internal sealed class ValheimAutosaveObservationCandidate(LocalData data)
{
    private const int MaximumLineBytes = 4096;
    private const int MaximumReadBytes = 65536;
    private const int CursorAnchorBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal Cursor Begin(ManagedRun requestedRun)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Valheim log identity requires Windows.");
        var run = ExactRun(requestedRun);
        VerifyProcess(run);
        var path = data.RunLogPath(run.OperationId);
        if (File.GetAttributes(Path.GetDirectoryName(path)!).HasFlag(FileAttributes.ReparsePoint) ||
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("The owned Valheim log is a link.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var identity = FileIdentity.Read(stream.SafeFileHandle);
            var offset = stream.Length;
            var discardFirstLine = false;
            if (offset > 0)
            {
                stream.Position = offset - 1;
                discardFirstLine = stream.ReadByte() != '\n';
            }
            stream.Position = offset;
            var anchor = ReadAnchor(stream.SafeFileHandle, offset);
            VerifyProcess(run);
            return new Cursor(run.ProfileId, run.OperationId, run.ProcessId!.Value,
                run.StartTimeUtcTicks!.Value, run.ExecutablePath, path,
                stream, identity, offset, discardFirstLine, anchor);
        }
        catch { stream.Dispose(); throw; }
    }

    internal bool Observe(ManagedRun requestedRun, Cursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.Invalid || cursor.Stream is null)
            throw new InvalidOperationException("The Valheim log cursor is unavailable.");
        try
        {
            var run = ExactRun(requestedRun);
            if (cursor.ProfileId != run.ProfileId || cursor.OperationId != run.OperationId ||
                cursor.ProcessId != run.ProcessId || cursor.StartTimeUtcTicks != run.StartTimeUtcTicks ||
                !SamePath(cursor.ExecutablePath, run.ExecutablePath) ||
                !SamePath(cursor.LogPath, data.RunLogPath(run.OperationId)))
                throw new InvalidOperationException("The Valheim log cursor belongs to another run.");
            VerifyProcess(run);
            if (File.GetAttributes(Path.GetDirectoryName(cursor.LogPath)!).HasFlag(FileAttributes.ReparsePoint) ||
                File.GetAttributes(cursor.LogPath).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("The owned Valheim log was replaced.");
            using var current = new FileStream(cursor.LogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (FileIdentity.Read(current.SafeFileHandle) != cursor.Identity ||
                FileIdentity.Read(cursor.Stream.SafeFileHandle) != cursor.Identity ||
                current.Length < cursor.Offset || cursor.Stream.Length < cursor.Offset)
                throw new InvalidOperationException("The owned Valheim log was replaced or truncated.");
            if (!ReadAnchor(current.SafeFileHandle, cursor.Offset).AsSpan()
                    .SequenceEqual(cursor.Anchor))
                throw new InvalidOperationException("The owned Valheim log changed before the cursor.");

            var available = (int)Math.Min(current.Length - cursor.Offset, MaximumReadBytes);
            var bytes = ReadBytes(current.SafeFileHandle, cursor.Offset, available);
            var completed = false;
            foreach (var value in bytes)
            {
                cursor.Offset++;
                if (cursor.DiscardFirstLine)
                {
                    if (value == '\n') cursor.DiscardFirstLine = false;
                    continue;
                }
                if (value == '\n')
                {
                    if (cursor.Line.Count > 0 && cursor.Line[^1] == '\r')
                        cursor.Line.RemoveAt(cursor.Line.Count - 1);
                    var line = StrictUtf8.GetString(cursor.Line.ToArray());
                    cursor.Line.Clear();
                    completed |= IsCompletionLine(line);
                }
                else
                {
                    if (cursor.Line.Count == MaximumLineBytes)
                        throw new InvalidOperationException("The Valheim log line is oversized.");
                    cursor.Line.Add((byte)value);
                }
            }
            cursor.Anchor = ReadAnchor(current.SafeFileHandle, cursor.Offset);
            VerifyProcess(run);
            return completed;
        }
        catch
        {
            cursor.Invalid = true;
            throw;
        }
    }

    private ManagedRun ExactRun(ManagedRun requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.Kind != GameKinds.Valheim || requested.ProfileId == Guid.Empty ||
            requested.OperationId == Guid.Empty || requested.ProcessId is not > 0 ||
            requested.StartTimeUtcTicks is not > 0 || string.IsNullOrWhiteSpace(requested.ExecutablePath))
            throw new InvalidOperationException("A recorded Valheim managed run is required.");
        var run = data.LoadRuns().SingleOrDefault(item => item.OperationId == requested.OperationId);
        var profile = data.LoadSettings().Profiles.SingleOrDefault(item => item.Id == requested.ProfileId);
        if (run is null || profile is null || run.Kind != GameKinds.Valheim ||
            profile.Kind != GameKinds.Valheim || run.ProfileId != profile.Id ||
            run.StopRequestedUtc is not null || run.ProcessId != requested.ProcessId ||
            run.StartTimeUtcTicks != requested.StartTimeUtcTicks ||
            !SamePath(run.ExecutablePath, requested.ExecutablePath) ||
            !SamePath(run.ExecutablePath, profile.ExecutablePath) ||
            !SamePath(run.LogPath, requested.LogPath) ||
            !SamePath(run.LogPath, data.RunLogPath(run.OperationId)) ||
            run.WorldId != requested.WorldId || run.WorldId != profile.WorldId ||
            !SamePath(run.WorldDirectory, requested.WorldDirectory) ||
            !SamePath(run.WorldDirectory, profile.WorldDirectory) ||
            run.GamePort != requested.GamePort || run.GamePort != profile.GamePort)
            throw new InvalidOperationException("The exact Valheim run and profile could not be verified.");
        return run;
    }

    private static void VerifyProcess(ManagedRun run)
    {
        try
        {
            using var process = Process.GetProcessById(run.ProcessId!.Value);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks &&
                SamePath(process.MainModule?.FileName, run.ExecutablePath)) return;
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or IOException or
                                   NotSupportedException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("The exact Valheim process could not be verified.", ex);
        }
        throw new InvalidOperationException("The exact Valheim process could not be verified.");
    }

    // Observed in read-only historical dedicated-server logs. Keep the full
    // line anchored so unrelated text containing the phrase cannot count.
    private static bool IsCompletionLine(string line)
    {
        const string marker = ": World save (5/5) done. Total time [";
        const string suffix = "ms]";
        const int stampLength = 19; // MM/dd/yyyy HH:mm:ss
        if (line.Length < stampLength + marker.Length + suffix.Length + 1 ||
            !line.AsSpan(stampLength).StartsWith(marker, StringComparison.Ordinal) ||
            !line.EndsWith(suffix, StringComparison.Ordinal) ||
            !DateTime.TryParseExact(line.AsSpan(0, stampLength), "MM/dd/yyyy HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return false;
        var elapsed = line.AsSpan(stampLength + marker.Length,
            line.Length - stampLength - marker.Length - suffix.Length);
        return elapsed.Length is >= 1 and <= 6 &&
            int.TryParse(elapsed, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static byte[] ReadAnchor(SafeFileHandle handle, long offset)
    {
        var length = (int)Math.Min(offset, CursorAnchorBytes);
        return ReadBytes(handle, offset - length, length);
    }

    private static byte[] ReadBytes(SafeFileHandle handle, long position, int length)
    {
        var bytes = new byte[length];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(read), position + read);
            if (count == 0)
                throw new InvalidOperationException("The owned Valheim log was truncated.");
            read += count;
        }
        return bytes;
    }

    internal sealed class Cursor : IDisposable
    {
        internal readonly Guid ProfileId;
        internal readonly Guid OperationId;
        internal readonly int ProcessId;
        internal readonly long StartTimeUtcTicks;
        internal readonly string ExecutablePath;
        internal readonly string LogPath;
        internal readonly FileStream Stream;
        internal readonly FileIdentity Identity;
        internal byte[] Anchor;
        internal readonly List<byte> Line = [];
        internal long Offset;
        internal bool DiscardFirstLine;
        internal bool Invalid;

        internal Cursor(Guid profileId, Guid operationId, int processId, long ticks,
            string executablePath, string logPath, FileStream stream, FileIdentity identity,
            long offset, bool discardFirstLine, byte[] anchor)
        {
            ProfileId = profileId; OperationId = operationId; ProcessId = processId;
            StartTimeUtcTicks = ticks; ExecutablePath = executablePath; LogPath = logPath;
            Stream = stream; Identity = identity; Offset = offset; Anchor = anchor;
            DiscardFirstLine = discardFirstLine;
        }

        public void Dispose() => Stream.Dispose();
    }

    internal readonly record struct FileIdentity(uint Volume, uint High, uint Low)
    {
        internal static FileIdentity Read(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file,
        out ByHandleFileInformation information);
}
