using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record SharedWorldFile(string Path, long Length, string Sha256);
public sealed record SharedWorldPortableAddOn(string Name, string Version,
    string RequiredGameVersion, string Type, string? Id = null);
public sealed record SharedWorldPortableAllowEntry(string Name, string? Id);
public sealed record SharedWorldPortableSetup(int GamePort, bool Crossplay,
    string? GameVersion = null, IReadOnlyList<SharedWorldPortableAddOn>? AddOns = null,
    IReadOnlyList<SharedWorldPortableAllowEntry>? Allowlist = null,
    bool PublicListing = false, int? MaxPlayers = null, string? GameMode = null,
    string? Difficulty = null, bool? AllowlistEnabled = null,
    string? JavaServerJarSha256 = null);
public static class SharedWorldCaptureKinds
{
    public const string PostStopBackup = "PostStopBackup";
}

internal sealed record VerifiedSharedWorldCapture(int Schema, string Kind, Guid BackupId,
    DateTimeOffset CapturedUtc, IReadOnlyList<SharedWorldFile> Files, string PayloadRoot,
    ServerSetupSnapshot? Setup = null);

internal interface ISharedWorldCaptureAdapter
{
    VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId);
}

internal sealed class PostStopBackupCaptureAdapter(WorldBackupService backups) : ISharedWorldCaptureAdapter
{
    public VerifiedSharedWorldCapture ReadVerified(ServerProfile profile, Guid backupId)
    {
        var (manifest, payloadRoot) = backups.ReadVerifiedTransfer(profile, backupId);
        return new(1, SharedWorldCaptureKinds.PostStopBackup, backupId, manifest.CreatedUtc,
            manifest.Files.Select(file => new SharedWorldFile(file.Path, file.Length, file.Sha256)).ToArray(),
            payloadRoot, backups.ReadSetup(profile, backupId));
    }
}

public sealed record SharedWorldVersion(int Schema, Guid GroupId, long Number, string? ParentHash,
    Guid ProfileId, string Game, string WorldId, DateTimeOffset CreatedUtc, string CaptureKind, Guid BackupId,
    SharedWorldPortableSetup PortableSetup, IReadOnlyList<SharedWorldFile> Files,
    string SigningPublicKey, string VersionHash, string Signature);
public sealed record SharedWorldStatus(bool Enabled, SharedWorldVersion? Latest,
    string? Error = null, int ConfirmedCopies = 0,
    SharedWorldLiveSaveStatus? LiveSave = null);
public sealed record SharedWorldResult(bool Ok, string Code, string Message,
    SharedWorldVersion? Version = null);
public sealed record SharedWorldConsentRequest(bool Enabled);
public sealed record SharedWorldGrantRequest(bool Enabled);

// These versions contain only completed post-Stop backup files. Version 4
// signs Java server JAR identity alongside reviewed setup. Earlier signatures stay readable.
internal sealed partial class SharedWorldService
{
    internal const int ChunkBytes = 256 * 1024;
    internal const string PayloadDirectory = "payload";
    internal const int MaximumFiles = 512;
    internal const long MaximumManifestBytes = 256 * 1024;
    internal const long MaximumSharedWorldBytes = 64L * 1024 * 1024 * 1024;
    private sealed record SourceBinding(string Directory, string Game, string WorldId, Guid GroupId);
    private const string SigningKeyFile = "shared-world-signing-key.protected";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LocalData data;
    private readonly ISharedWorldCaptureAdapter capture;
    private readonly object sync = new();
    private readonly Dictionary<(string VersionHash, int FileIndex), string[]> chunkHashes = new();

    public SharedWorldService(LocalData data, WorldBackupService backups)
    {
        this.data = data;
        capture = new PostStopBackupCaptureAdapter(backups);
    }

    internal SharedWorldService(LocalData data, ISharedWorldCaptureAdapter capture)
    {
        this.data = data;
        this.capture = capture;
    }

    private string Root(Guid profileId)
    {
        var path = Path.Combine(data.RootPath, "shared-worlds", profileId.ToString("N"));
        EnsureUnlinkedRoot(data.RootPath, path);
        return path;
    }
    private string VersionRoot(SharedWorldVersion version) =>
        Path.Combine(Root(version.ProfileId), version.GroupId.ToString("N"),
            version.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private string LatestPath(Guid profileId) => Path.Combine(Root(profileId), "latest.json");
    private string ErrorPath(Guid profileId) => Path.Combine(Root(profileId), "last-error.txt");
    private string BindingPath(Guid profileId) => Path.Combine(Root(profileId), "source.json");
    private string RosterPath(Guid profileId) => Path.Combine(Root(profileId), "roster.json");
    private string GroupRosterPath(Guid profileId, Guid groupId) =>
        Path.Combine(Root(profileId), groupId.ToString("N") + ".roster.json");
    private static string SourceDirectory(ServerProfile profile) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.WorldDirectory));

    private SourceBinding? ReadBinding(Guid profileId)
    {
        var path = BindingPath(profileId);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Shared save source binding is invalid.");
        var binding = JsonSerializer.Deserialize<SourceBinding>(File.ReadAllBytes(path), Json);
        if (binding is null || binding.GroupId == Guid.Empty || !Path.IsPathFullyQualified(binding.Directory) ||
            string.IsNullOrWhiteSpace(binding.Game) || string.IsNullOrWhiteSpace(binding.WorldId))
            throw new InvalidDataException("Shared save source binding is invalid.");
        return binding;
    }

    private SourceBinding BindSource(ServerProfile profile)
    {
        var directory = SourceDirectory(profile);
        var binding = ReadBinding(profile.Id);
        if (binding is not null && BindingMatches(binding, profile))
            return binding;
        if (binding is not null)
            throw new InvalidDataException("The shared save source changed. Review and sign a new source group first.");
        binding = new(directory, profile.Kind, profile.WorldId, Guid.NewGuid());
        var path = BindingPath(profile.Id);
        Directory.CreateDirectory(Root(profile.Id));
        var stage = path + ".new";
        File.WriteAllBytes(stage, JsonSerializer.SerializeToUtf8Bytes(binding, Json));
        File.Move(stage, path, true);
        return binding;
    }

    private static bool BindingMatches(SourceBinding binding, ServerProfile profile) =>
        binding.Directory.Equals(SourceDirectory(profile), StringComparison.OrdinalIgnoreCase) &&
        binding.Game == profile.Kind && binding.WorldId == profile.WorldId;

    internal SharedWorldRoster? ReadRoster(ServerProfile profile)
    {
        lock (sync)
        {
            var binding = ReadBinding(profile.Id);
            var path = binding is not null && File.Exists(GroupRosterPath(profile.Id, binding.GroupId))
                ? GroupRosterPath(profile.Id, binding.GroupId) : RosterPath(profile.Id);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaximumManifestBytes ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Shared roster is oversized or linked.");
            var roster = JsonSerializer.Deserialize<SharedWorldRoster>(File.ReadAllBytes(path), Json);
            using var key = LoadSigningKey();
            if (!SharedWorldRosterTrust.Verify(roster) || binding is null ||
                !BindingMatches(binding, profile) || roster!.GroupId != binding.GroupId ||
                roster.ProfileId != profile.Id ||
                roster.OwnerPublicKey != Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))
                throw new InvalidDataException("Shared roster failed verification.");
            return roster;
        }
    }

    internal SharedWorldRoster PublishRoster(ServerProfile profile,
        IReadOnlyList<SharedWorldRosterMember> members, bool? ownerOverride = null,
        bool reviewSourceChange = false)
    {
        lock (sync)
        {
            var oldBinding = ReadBinding(profile.Id);
            var sourceChanged = oldBinding is not null && !BindingMatches(oldBinding, profile);
            if (sourceChanged && !reviewSourceChange)
                throw new InvalidDataException("The shared save source changed. Review the new source before signing a new group.");
            // Read the old signed head while its binding still verifies. Never reset the rollback clock.
            var prior = oldBinding is null ? null : ReadRosterForBinding(profile, oldBinding);
            // A roster can reach Friends before the first save. An existing binding
            // without its signed roster has an unknown distributed revision, so
            // neither same-source publication nor source review may reset it.
            if (oldBinding is not null && prior is null)
                throw new InvalidDataException("The previous signed roster is missing; sharing history cannot be advanced safely.");
            // Publish the signed roster before its first binding. If that write
            // fails, retry may choose a fresh group because none was exposed.
            var binding = sourceChanged || oldBinding is null
                ? new SourceBinding(SourceDirectory(profile), profile.Kind, profile.WorldId, Guid.NewGuid())
                : oldBinding;
            if (members.Count > 128 || members.Any(member => member.DeviceId == Guid.Empty ||
                member.Grants is null || !SharedWorldRosterTrust.ValidKey(member.PublicKey)) ||
                members.Select(member => member.DeviceId).Distinct().Count() != members.Count)
                throw new InvalidDataException("Shared roster members are invalid.");
            using var key = LoadSigningKey();
            var ordered = members.OrderBy(member => member.DeviceId).ToArray();
            if (!sourceChanged && prior is { Schema: 2 } &&
                prior.OwnerOverride == (ownerOverride ?? prior.OwnerOverride) &&
                prior.Members.SequenceEqual(ordered)) return prior;
            var draft = new SharedWorldRoster(2, binding.GroupId, profile.Id,
                checked((prior?.Epoch ?? 0) + 1), checked((prior?.Revision ?? 0) + 1),
                ownerOverride ?? prior?.OwnerOverride ?? true,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                ordered, "");
            var roster = draft with
            {
                Signature = Convert.ToBase64String(key.SignData(
                SharedWorldRosterTrust.Basis(draft), HashAlgorithmName.SHA256))
            };
            var path = GroupRosterPath(profile.Id, binding.GroupId);
            Directory.CreateDirectory(Root(profile.Id));
            var stage = path + ".new";
            File.WriteAllBytes(stage, JsonSerializer.SerializeToUtf8Bytes(roster, Json));
            File.Move(stage, path, true);
            if (sourceChanged || oldBinding is null)
            {
                var bindingPath = BindingPath(profile.Id);
                var bindingStage = bindingPath + ".new";
                File.WriteAllBytes(bindingStage, JsonSerializer.SerializeToUtf8Bytes(binding, Json));
                File.Move(bindingStage, bindingPath, true);
            }
            return roster;
        }
    }

    private SharedWorldRoster? ReadRosterForBinding(ServerProfile profile, SourceBinding binding)
    {
        var path = File.Exists(GroupRosterPath(profile.Id, binding.GroupId))
            ? GroupRosterPath(profile.Id, binding.GroupId) : RosterPath(profile.Id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumManifestBytes ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Shared roster is oversized or linked.");
        var roster = JsonSerializer.Deserialize<SharedWorldRoster>(File.ReadAllBytes(path), Json);
        using var key = LoadSigningKey();
        if (!SharedWorldRosterTrust.Verify(roster) || roster!.GroupId != binding.GroupId ||
            roster.ProfileId != profile.Id ||
            roster.OwnerPublicKey != Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))
            throw new InvalidDataException("The previous shared roster failed verification.");
        return roster;
    }

    public SharedWorldStatus Status(ServerProfile profile)
    {
        lock (sync)
        {
            var live = SharedWorldLiveSaveAdapters.Status(profile.Kind);
            if (profile.Kind == GameKinds.Custom)
                return new(false, null, LiveSave: live);
            try
            {
                var path = LatestPath(profile.Id);
                string? error = null;
                var errorPath = ErrorPath(profile.Id);
                if (File.Exists(errorPath) && new FileInfo(errorPath).Length <= 300 &&
                    (File.GetAttributes(errorPath) & FileAttributes.ReparsePoint) == 0)
                    error = File.ReadAllText(errorPath);
                if (!File.Exists(path)) return new(profile.SharedSavesEnabled, null, error, LiveSave: live);
                if (new FileInfo(path).Length > MaximumManifestBytes ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Shared save metadata is oversized or linked.");
                var version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json);
                var binding = ReadBinding(profile.Id);
                if (version is null || binding is null || binding.GroupId != version.GroupId ||
                    !BindingMatches(binding, profile) ||
                    version.ProfileId != profile.Id || version.Game != profile.Kind ||
                    version.WorldId != profile.WorldId || !VerifySignature(version))
                    throw new InvalidDataException("Shared save metadata failed verification.");
                using var currentKey = LoadSigningKey();
                if (Convert.ToBase64String(currentKey.ExportSubjectPublicKeyInfo()) != version.SigningPublicKey)
                    throw new InvalidDataException("The world signing identity changed.");
                return new(profile.SharedSavesEnabled, version, error, CountReceipts(profile, version), live);
            }
            catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or InvalidDataException)
            { return new(profile.SharedSavesEnabled, null, "The published save could not be verified.", LiveSave: live); }
        }
    }

    public SharedWorldResult PublishAfterStop(ServerProfile profile, Guid backupId)
    {
        if (!profile.SharedSavesEnabled || profile.Kind == GameKinds.Custom)
            return new(false, "SharingOff", "Shared saves are off for this server.");
        lock (sync)
        {
            string? stage = null;
            try
            {
                var oldBinding = ReadBinding(profile.Id);
                // Enabling sharing and a graceful Stop can race. Publication must
                // wait for the signed roster to establish the group; a save must
                // never create a bare binding that strands first setup.
                if (oldBinding is null || ReadRoster(profile) is null)
                    throw new InvalidDataException("Sign the shared world roster before publishing a save.");
                var sameSource = oldBinding is not null && BindingMatches(oldBinding, profile);
                var priorStatus = Status(profile);
                if (sameSource && File.Exists(LatestPath(profile.Id)) && priorStatus.Latest is null &&
                    !VerifiedPreviousGroupPointer(profile, oldBinding!))
                    throw new InvalidDataException("The existing shared save pointer failed verification.");
                var binding = BindSource(profile);
                var previous = ReconcilePublishedVersion(profile, priorStatus.Latest);
                if (previous?.BackupId == backupId)
                    return new(true, "SharedSavePublished", "The completed post-Stop backup is ready for approved PCs.", previous);
                var source = capture.ReadVerified(profile, backupId);
                if (source.Schema != 1 || source.Kind != SharedWorldCaptureKinds.PostStopBackup ||
                    source.Files.Count is < 1 or > MaximumFiles ||
                    source.Files.Any(file => !SafePath(file.Path) || file.Length < 0 ||
                        file.Sha256.Length != 64))
                    throw new InvalidDataException("The backup manifest cannot be shared safely.");
                var size = BoundedTotalBytes(source.Files);
                var drive = new DriveInfo(Path.GetPathRoot(data.RootPath)!);
                if (drive.AvailableFreeSpace < 1024L * 1024 * 1024 ||
                    drive.AvailableFreeSpace - 1024L * 1024 * 1024 < size)
                    throw new IOException("Keep 1 GiB free after copying the published backup.");
                var group = binding.GroupId;
                var number = previous?.Number + 1 ?? 1;
                var files = source.Files.ToArray();
                if (source.Setup is null)
                    throw new InvalidDataException("The backup lacks a complete reviewed setup checkpoint.");
                var portableSetup = SharedWorldPortableSetupReader.Capture(source.Setup);
                using var key = LoadSigningKey();
                var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
                var basis = VersionBasis(group, number, previous?.VersionHash, profile.Id,
                    profile.Kind, profile.WorldId, source.CapturedUtc, source.Kind,
                    backupId, portableSetup, files, publicKey, 4);
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)));
                var signature = Convert.ToBase64String(key.SignHash(Convert.FromHexString(digest)));
                var version = new SharedWorldVersion(4, group, number, previous?.VersionHash,
                    profile.Id, profile.Kind, profile.WorldId, source.CapturedUtc, source.Kind, backupId,
                    portableSetup, files, publicKey, digest, signature);
                if (JsonSerializer.SerializeToUtf8Bytes(version, Json).Length > MaximumManifestBytes)
                    throw new InvalidDataException("The shared save manifest is too large.");
                var root = Root(profile.Id);
                Directory.CreateDirectory(root);
                stage = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                foreach (var file in files)
                {
                    var sourcePath = SafeChild(source.PayloadRoot, file.Path);
                    var destination = SafeChild(Path.Combine(stage, PayloadDirectory), file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(sourcePath, destination, false);
                    VerifyFile(destination, file);
                }
                File.WriteAllBytes(Path.Combine(stage, "version.json"),
                    JsonSerializer.SerializeToUtf8Bytes(version, Json));
                var destinationRoot = VersionRoot(version);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationRoot)!);
                if (Directory.Exists(destinationRoot)) throw new InvalidDataException("Shared version already exists.");
                Directory.Move(stage, destinationRoot);
                stage = null;
                var latestStage = LatestPath(profile.Id) + ".new";
                File.WriteAllBytes(latestStage, JsonSerializer.SerializeToUtf8Bytes(version, Json));
                File.Move(latestStage, LatestPath(profile.Id), true);
                try { PrunePublishedPayloads(version); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                { /* The latest verified payload remains available; cleanup can retry at next publish. */ }
                try { if (File.Exists(ErrorPath(profile.Id))) File.Delete(ErrorPath(profile.Id)); }
                catch (IOException) { }
                return new(true, "SharedSavePublished", "A completed post-Stop backup is ready for approved PCs.", version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                CryptographicException or JsonException or OverflowException)
            {
                try
                {
                    Directory.CreateDirectory(Root(profile.Id));
                    File.WriteAllText(ErrorPath(profile.Id),
                        "The latest completed backup could not be published. Review free space and backup integrity.");
                }
                catch (Exception recordEx) when (recordEx is IOException or UnauthorizedAccessException) { }
                return new(false, "SharedSavePublishFailed", "The completed backup could not be published: " + ex.Message);
            }
            finally { if (stage is not null) TryDeleteStage(stage); }
        }
    }

    private bool VerifiedPreviousGroupPointer(ServerProfile profile, SourceBinding binding)
    {
        var path = LatestPath(profile.Id);
        if (new FileInfo(path).Length > MaximumManifestBytes ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        var previous = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json);
        if (previous is null || previous.GroupId == binding.GroupId ||
            previous.ProfileId != profile.Id || !VerifySignature(previous)) return false;
        using var key = LoadSigningKey();
        return previous.SigningPublicKey == Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) &&
            File.Exists(Path.Combine(VersionRoot(previous), "version.json"));
    }

    public byte[] ReadChunk(SharedWorldVersion version, int fileIndex, long offset)
    {
        lock (sync)
        {
            if (!VerifySignature(version) || fileIndex < 0 || fileIndex >= version.Files.Count)
                throw new InvalidDataException("Shared version is invalid.");
            var file = version.Files[fileIndex];
            if (offset < 0 || offset >= file.Length || offset % ChunkBytes != 0)
                throw new InvalidDataException("Chunk offset is invalid.");
            var path = SafeChild(Path.Combine(VersionRoot(version), PayloadDirectory), file.Path);
            if (!File.Exists(path))
                throw new InvalidDataException("Shared save payload was pruned; request the latest version.");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(path).Length != file.Length)
                throw new InvalidDataException("Shared save file changed during transfer.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cacheKey = (version.VersionHash, fileIndex);
            if (!chunkHashes.TryGetValue(cacheKey, out var hashes))
            {
                hashes = HashChunksAndVerify(stream, file);
                chunkHashes.Add(cacheKey, hashes);
            }
            stream.Position = offset;
            var count = (int)Math.Min(ChunkBytes, file.Length - offset);
            var result = new byte[count];
            stream.ReadExactly(result);
            if (!Convert.ToHexString(SHA256.HashData(result)).Equals(
                    hashes[checked((int)(offset / ChunkBytes))], StringComparison.Ordinal))
                throw new InvalidDataException("Shared save chunk changed during transfer.");
            return result;
        }
    }

    public SharedWorldVersion ReadEarlierVersion(SharedWorldVersion latest, long number)
    {
        lock (sync)
        {
            if (!VerifySignature(latest) || number < 1 || number >= latest.Number)
                throw new InvalidDataException("Shared version number is invalid.");
            var versionRoot = Path.Combine(Root(latest.ProfileId), latest.GroupId.ToString("N"),
                number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var path = SafeChild(versionRoot, "version.json");
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumManifestBytes ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Earlier shared version is missing or linked.");
            var prior = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(path), Json);
            if (prior is null || !VerifySignature(prior) || prior.Number != number ||
                prior.GroupId != latest.GroupId || prior.ProfileId != latest.ProfileId ||
                prior.Game != latest.Game || prior.WorldId != latest.WorldId ||
                prior.SigningPublicKey != latest.SigningPublicKey)
                throw new InvalidDataException("Earlier shared version is invalid.");
            return prior;
        }
    }

    internal static bool VerifySignature(SharedWorldVersion value)
    {
        try
        {
            if (value.Schema is not (1 or 2 or 3 or 4) || value.GroupId == Guid.Empty || value.ProfileId == Guid.Empty ||
                value.Number < 1 || value.Files.Count is < 1 or > MaximumFiles ||
                value.CaptureKind != SharedWorldCaptureKinds.PostStopBackup ||
                value.PortableSetup is null || value.PortableSetup.GamePort is < 1 or > 65535 ||
                value.Schema == 1 && !SharedWorldPortableSetupReader.LegacyFieldsEmpty(value.PortableSetup, 1) ||
                value.Schema == 2 && !SharedWorldPortableSetupReader.LegacyFieldsEmpty(value.PortableSetup, 2) ||
                value.Schema == 3 && !SharedWorldPortableSetupReader.LegacyFieldsEmpty(value.PortableSetup, 3) ||
                value.Schema >= 2 && !SharedWorldPortableSetupReader.Valid(value.Game, value.PortableSetup, value.Schema) ||
                value.Files.Any(file => !SafePath(file.Path) || file.Length < 0 ||
                    file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) ||
                value.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Files.Count)
                return false;
            var basis = VersionBasis(value.GroupId, value.Number, value.ParentHash,
                value.ProfileId, value.Game, value.WorldId, value.CreatedUtc, value.CaptureKind, value.BackupId,
                value.PortableSetup, value.Files, value.SigningPublicKey, value.Schema);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(value.SigningPublicKey), out _);
            if (key.KeySize != 256) return false;
            return Convert.ToHexString(hash).Equals(value.VersionHash, StringComparison.Ordinal) &&
                key.VerifyHash(hash, Convert.FromBase64String(value.Signature));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { return false; }
    }

    private ECDsa LoadSigningKey()
    {
        var bytes = data.LoadProtected(SigningKeyFile);
        if (bytes is null)
        {
            var sharedRoot = Path.Combine(data.RootPath, "shared-worlds");
            EnsureUnlinkedRoot(data.RootPath, sharedRoot);
            if (Directory.Exists(sharedRoot) && Directory.EnumerateDirectories(sharedRoot)
                    .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                        File.Exists(Path.Combine(path, "latest.json"))))
                throw new InvalidDataException("The existing world signing identity is missing.");
            using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            bytes = created.ExportPkcs8PrivateKey();
            data.SaveProtected(SigningKeyFile, bytes);
        }
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(bytes, out _);
        return key;
    }

    private static string VersionBasis(Guid group, long number, string? parent, Guid profile,
        string game, string world, DateTimeOffset createdUtc, string captureKind, Guid backup,
        SharedWorldPortableSetup portableSetup, IReadOnlyList<SharedWorldFile> files, string publicKey,
        int schema) => schema == 1
            ? JsonSerializer.Serialize(new
            {
                schema = 1,
                group,
                number,
                parent,
                profile,
                game,
                world,
                createdUtc,
                captureKind,
                backup,
                portableSetup = new { portableSetup.GamePort, portableSetup.Crossplay },
                files,
                publicKey
            }, Json)
            : schema == 2
                ? JsonSerializer.Serialize(new
                {
                    schema = 2,
                    group,
                    number,
                    parent,
                    profile,
                    game,
                    world,
                    createdUtc,
                    captureKind,
                    backup,
                    portableSetup = new
                    {
                        portableSetup.GamePort,
                        portableSetup.Crossplay,
                        portableSetup.GameVersion,
                        AddOns = portableSetup.AddOns?.Select(item => new
                        { item.Name, item.Version, item.RequiredGameVersion, item.Type }).ToArray(),
                        portableSetup.Allowlist
                    },
                    files,
                    publicKey
                }, Json)
                : schema == 3
                    ? JsonSerializer.Serialize(new
                    {
                        schema = 3,
                        group,
                        number,
                        parent,
                        profile,
                        game,
                        world,
                        createdUtc,
                        captureKind,
                        backup,
                        portableSetup = new
                        {
                            portableSetup.GamePort,
                            portableSetup.Crossplay,
                            portableSetup.GameVersion,
                            AddOns = portableSetup.AddOns?.Select(item => new
                            { item.Name, item.Version, item.RequiredGameVersion, item.Type, item.Id }).ToArray(),
                            portableSetup.Allowlist,
                            portableSetup.PublicListing,
                            portableSetup.MaxPlayers,
                            portableSetup.GameMode,
                            portableSetup.Difficulty,
                            portableSetup.AllowlistEnabled
                        },
                        files,
                        publicKey
                    }, Json)
                    : JsonSerializer.Serialize(new
                    {
                        schema = 4,
                        group,
                        number,
                        parent,
                        profile,
                        game,
                        world,
                        createdUtc,
                        captureKind,
                        backup,
                        portableSetup,
                        files,
                        publicKey
                    }, Json);

    internal static bool SafePath(string path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 240 &&
        !Path.IsPathRooted(path) && !path.Contains('\\') &&
        path.Split('/').All(SafePart);

    private static bool SafePart(string part)
    {
        if (part.Length is < 1 or > 100 || part is "." or ".." ||
            part.EndsWith('.') || part.EndsWith(' ') ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Any(char.IsControl)) return false;
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9");
    }

    internal static string SafeChild(string root, string relative)
    {
        if (!SafePath(relative)) throw new InvalidDataException("A shared save path is invalid.");
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A shared save path escaped its vault.");
        for (var current = Path.GetDirectoryName(path); current is not null &&
             current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Shared save contains a linked directory.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Shared save contains a linked file.");
        return path;
    }

    internal static void EnsureUnlinkedRoot(string dataRoot, string target)
    {
        var trusted = Path.GetFullPath(dataRoot);
        var current = Path.GetFullPath(target);
        if (!current.Equals(trusted, StringComparison.OrdinalIgnoreCase) &&
            !current.StartsWith(trusted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Shared save root escaped local data.");
        while (true)
        {
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Shared save root contains a link or reparse point.");
            if (current.Equals(trusted, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ??
                throw new InvalidDataException("Shared save root could not be resolved.");
        }
    }

    private static void TryDeleteStage(string stage)
    {
        try
        {
            if (!Directory.Exists(stage)) return;
            var queue = new Queue<string>();
            queue.Enqueue(stage);
            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) return;
                    if (Directory.Exists(child)) queue.Enqueue(child);
                }
            }
            Directory.Delete(stage, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private SharedWorldVersion? ReconcilePublishedVersion(ServerProfile profile, SharedWorldVersion? previous)
    {
        var binding = ReadBinding(profile.Id) ?? throw new InvalidDataException("Shared save source is unbound.");
        var groupRoot = previous is null ? null : Path.Combine(Root(profile.Id), previous.GroupId.ToString("N"));
        // A first publication has no group ID in latest.json. Find its sole verified
        // candidate; ambiguity or an invalid directory must fail closed.
        var candidates = previous is null
            ? Directory.Exists(Path.Combine(Root(profile.Id), binding.GroupId.ToString("N")))
                ? Directory.EnumerateDirectories(Root(profile.Id))
                    .Where(path => Path.GetFileName(path).Equals(binding.GroupId.ToString("N"), StringComparison.OrdinalIgnoreCase) &&
                        Directory.Exists(Path.Combine(path, "1"))).Select(path => Path.Combine(path, "1")).ToArray()
                : []
            : Directory.Exists(Path.Combine(groupRoot!, (previous.Number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                ? [Path.Combine(groupRoot!, (previous.Number + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))]
                : [];
        if (candidates.Length == 0) return previous;
        if (candidates.Length != 1) throw new InvalidDataException("Multiple unpublished shared versions exist.");
        var directory = candidates[0];
        EnsureUnlinkedRoot(data.RootPath, directory);
        var manifestPath = SafeChild(directory, "version.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > MaximumManifestBytes)
            throw new InvalidDataException("Unpublished shared version is incomplete.");
        var candidate = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(manifestPath), Json);
        using var key = LoadSigningKey();
        if (candidate is null || !VerifySignature(candidate) || candidate.ProfileId != profile.Id ||
            candidate.Game != profile.Kind || candidate.WorldId != profile.WorldId ||
            candidate.Number != (previous?.Number ?? 0) + 1 ||
            candidate.ParentHash != previous?.VersionHash ||
            candidate.GroupId != binding.GroupId ||
            candidate.SigningPublicKey != Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) ||
            directory != VersionRoot(candidate))
            throw new InvalidDataException("Unpublished shared version does not continue the verified history.");
        foreach (var file in candidate.Files)
            VerifyFile(SafeChild(Path.Combine(directory, PayloadDirectory), file.Path), file);
        var stagedLatest = LatestPath(profile.Id) + ".new";
        File.WriteAllBytes(stagedLatest, JsonSerializer.SerializeToUtf8Bytes(candidate, Json));
        File.Move(stagedLatest, LatestPath(profile.Id), true);
        return candidate;
    }

    internal static long BoundedTotalBytes(IReadOnlyList<SharedWorldFile> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            if (file.Length < 0 || file.Length > MaximumSharedWorldBytes - total)
                throw new InvalidDataException("Shared save exceeds the 64 GiB transfer limit.");
            total += file.Length;
        }
        return total;
    }

    private void PrunePublishedPayloads(SharedWorldVersion latest)
    {
        foreach (var file in latest.Files)
            VerifyFile(SafeChild(Path.Combine(VersionRoot(latest), PayloadDirectory), file.Path), file);
        var candidates = new List<SharedWorldVersion>();
        foreach (var group in Directory.EnumerateDirectories(Root(latest.ProfileId)))
        {
            if (!Guid.TryParseExact(Path.GetFileName(group), "N", out var groupId) ||
                (File.GetAttributes(group) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var directory in Directory.EnumerateDirectories(group))
            {
                if (!long.TryParse(Path.GetFileName(directory), out var number) || number < 1 ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                var manifest = SafeChild(directory, "version.json");
                if (!File.Exists(manifest) || new FileInfo(manifest).Length > MaximumManifestBytes) continue;
                var version = JsonSerializer.Deserialize<SharedWorldVersion>(File.ReadAllBytes(manifest), Json);
                if (version is not null && VerifySignature(version) && version.ProfileId == latest.ProfileId &&
                    version.GroupId == groupId && version.Number == number &&
                    version.SigningPublicKey == latest.SigningPublicKey)
                    candidates.Add(version);
            }
        }
        var retained = new HashSet<string>(StringComparer.Ordinal) { latest.VersionHash };
        foreach (var group in candidates.GroupBy(item => item.GroupId))
        {
            var kept = 0;
            foreach (var candidate in group.OrderByDescending(item => item.Number))
            {
                try
                {
                    foreach (var file in candidate.Files)
                        VerifyFile(SafeChild(Path.Combine(VersionRoot(candidate), PayloadDirectory), file.Path), file);
                    if (kept++ < 3) retained.Add(candidate.VersionHash);
                }
                catch (InvalidDataException) { /* Damaged payload cannot count as a verified fallback. */ }
            }
        }
        foreach (var candidate in candidates.Where(item => !retained.Contains(item.VersionHash)))
            foreach (var file in candidate.Files)
            {
                var path = SafeChild(Path.Combine(VersionRoot(candidate), PayloadDirectory), file.Path);
                if (File.Exists(path)) File.Delete(path);
            }
    }

    private static string[] HashChunksAndVerify(Stream stream, SharedWorldFile file)
    {
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunks = new List<string>();
        var buffer = new byte[ChunkBytes];
        int read;
        while ((read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) != 0)
        {
            whole.AppendData(buffer.AsSpan(0, read));
            chunks.Add(Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read))));
        }
        if (!Convert.ToHexString(whole.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Shared save hash changed.");
        return chunks.ToArray();
    }

    internal static void VerifyFile(string path, SharedWorldFile file)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length != file.Length)
            throw new InvalidDataException("Shared save file is missing, linked or changed.");
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Shared save hash changed.");
    }
}
