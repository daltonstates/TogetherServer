using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TogetherServer;

internal static class SnapshotReparseChecks
{
    internal static void Run(string root,
        Func<LocalData, (ServerProfile Profile, ManagedRun Run, GameServerRegistry Games)> fixtureFactory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Reparse checks require Windows.");
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("An absolute local disposable check root is required.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Require(root != Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), "A drive root is not a disposable check root.");
        Check(root, fixtureFactory, bootstrap: true);
        Check(root, fixtureFactory, bootstrap: false);
    }

    private static void Check(string root,
        Func<LocalData, (ServerProfile Profile, ManagedRun Run, GameServerRegistry Games)> fixtureFactory, bool bootstrap)
    {
        var trial = Owned(root, Path.Combine(root, $"snapshot-reparse-{(bootstrap ? "bootstrap" : "payload")}-{Guid.NewGuid():N}"));
        SharedWorldService.EnsureUnlinkedRoot(root, trial);
        Directory.CreateDirectory(trial);
        using var data = new LocalData(Path.Combine(trial, "data"));
        var (profile, run, games) = fixtureFactory(data);
        Require(profile.Kind == GameKinds.Fixture && run.Kind == GameKinds.Fixture &&
            run.ProfileId == profile.Id && run.ProcessId == Environment.ProcessId,
            "Reparse checks may use only this check process's synthetic fixture.");
        var world = Owned(trial, profile.WorldDirectory);
        SharedWorldService.EnsureUnlinkedRoot(trial, world);
        var nestedSource = Owned(trial, Path.Combine(world, "nested"));
        Directory.CreateDirectory(nestedSource);
        var source = Path.Combine(nestedSource, "save.bin");
        File.WriteAllText(source, "unchanged synthetic source bytes");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        var redirect = Owned(trial, Path.Combine(trial, "redirect"));
        Directory.CreateDirectory(redirect);
        var marker = Path.Combine(redirect, "keep.bin");
        File.WriteAllText(marker, "unchanged disposable redirect marker");
        var markerHash = SHA256.HashData(File.ReadAllBytes(marker));
        var requestId = Guid.NewGuid();
        var profileRoot = Owned(trial, Path.Combine(data.RootPath, "managed-live-snapshots", profile.Id.ToString("N")));
        var partial = Owned(trial, Path.Combine(profileRoot, requestId.ToString("N") + ".partial"));
        var expectedLink = bootstrap ? profileRoot : Path.Combine(partial, "payload", "nested");
        var sealName = $"managed-live-snapshot-{profile.Id:N}-{requestId:N}.protected";
        var attempted = false;
        var reparsed = false;
        var store = new ManagedLiveSnapshotStore(data, games) { FixtureAvailableBytesForChecks = () => long.MaxValue };
        void Substitute(string path)
        {
            Require(!attempted && Owned(trial, path).Equals(expectedLink, StringComparison.OrdinalIgnoreCase),
                "The reparse hook did not name this exact disposable private directory.");
            Require(!Directory.EnumerateFileSystemEntries(path).Any(), "The private reparse parent was not newly empty.");
            attempted = true;
            reparsed = TryMountPoint(root, path, redirect);
        }
        if (bootstrap) store.AfterPrivateDirectoryCreatedForChecks = Substitute;
        else store.BeforeDestinationWriteForChecks = () =>
        {
            var pending = Directory.EnumerateDirectories(profileRoot, "*.partial").Single();
            Substitute(Path.Combine(pending, "payload", "nested"));
        };
        void Intact()
        {
            Require(Directory.EnumerateFileSystemEntries(redirect).SequenceEqual([marker]) &&
                SHA256.HashData(File.ReadAllBytes(marker)).SequenceEqual(markerHash), "A payload or guard write escaped into the redirect.");
            Require(Directory.EnumerateFileSystemEntries(world).SequenceEqual([nestedSource]) &&
                Directory.EnumerateFileSystemEntries(nestedSource).SequenceEqual([source]) &&
                SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(sourceHash), "A private reparse changed synthetic source state.");
        }
        ImmutableLiveSaveSnapshot? snapshot = null;
        Exception? failure = null;
        var discarded = 0;
        try
        {
            var completion = new LiveSaveCompletionEvidence(profile.Id, run.OperationId, run.ProcessId!.Value,
                run.StartTimeUtcTicks!.Value, LiveSaveEvidence.RunScopedCompletion, DateTimeOffset.UtcNow, true);
            try { snapshot = store.Stage(profile, run, completion, reservedSnapshotId: requestId); }
            catch (Exception ex) when (ManagedLiveSnapshotStore.ExpectedFailure(ex)) { failure = ex; }
            Require(attempted, "Capture failed before exercising the attribute-only reparse hook: " + failure?.Message);
            Intact();
            if (reparsed)
            {
                Require(snapshot is null && failure is not null, "An attribute-only redirected private capture succeeded.");
                using var intent = JsonDocument.Parse(data.LoadProtected(sealName) ?? throw new Exception("The redirected attempt lost its reviewable intent."));
                var authority = intent.RootElement;
                Require(authority.GetProperty("schema").GetInt32() == 1 && !authority.GetProperty("complete").GetBoolean() &&
                    authority.GetProperty("snapshot").GetProperty("profileId").GetGuid() == profile.Id &&
                    authority.GetProperty("snapshot").GetProperty("operationId").GetGuid() == run.OperationId &&
                    authority.GetProperty("snapshot").GetProperty("snapshotId").GetGuid() == requestId,
                    "The redirected attempt did not retain its exact incomplete protected intent.");
            }
            else Require(failure is null && snapshot is not null && store.Verify(profile, run, snapshot),
                "A denied reparse did not preserve a working capture: " + failure?.Message);
        }
        finally
        {
            store.AfterPrivateDirectoryCreatedForChecks = null;
            store.BeforeDestinationWriteForChecks = null;
            if (reparsed)
            {
                var link = Owned(trial, expectedLink);
                SharedWorldService.EnsureUnlinkedRoot(trial, Path.GetDirectoryName(link)!);
                Require((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "The test junction changed before exact cleanup.");
                Directory.Delete(link, recursive: false);
            }
            if (snapshot is not null) store.Discard(snapshot);
            discarded = store.DiscardInterrupted(profile.Id);
        }
        Intact();
        if (reparsed) Require(discarded == 1 && !data.HasProtected(sealName) && !Directory.Exists(partial) &&
            !Directory.Exists(Path.Combine(profileRoot, requestId.ToString("N"))), "Removing only the test junction did not recover the exact interrupted private capture.");
    }

    private static bool TryMountPoint(string root, string path, string target)
    {
        path = Owned(root, path); target = Owned(root, target);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
        // Attribute-only access is intentionally used: sharing denies GENERIC_WRITE
        // but does not deny FILE_WRITE_ATTRIBUTES, which can authorize this FSCTL.
        // Native Win32 calls need the extended local path for a deeply nested
        // disposable trial. A MAX_PATH failure must never count as safe denial.
        using var handle = CreateFile(@"\\?\" + path, 0x100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) return Denied(Marshal.GetLastWin32Error());
        if (DeviceIoControl(handle, 0x000900A4, buffer, checked((uint)buffer.Length), IntPtr.Zero, 0, out _, IntPtr.Zero)) return true;
        return Denied(Marshal.GetLastWin32Error());
    }

    private static bool Denied(int error)
    {
        if (error is 5 or 32 or 145 or 1314) return false;
        throw new Win32Exception(error, $"The attribute-only mount-point probe failed unexpectedly (Windows error {error}).");
    }

    private static string Owned(string root, string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("A probe target must be absolute.");
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A probe target escaped its exact disposable root.");
        return full;
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
