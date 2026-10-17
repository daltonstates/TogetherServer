using System.Text.Json;
using System.Text.Json.Serialization;

namespace TogetherServer;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectionBackupPolicy(
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] int RetentionCount,
    [property: JsonRequired] long MinimumFreeSpaceMb);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectionRecoveryPolicy([property: JsonRequired] bool Enabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectionPolicy(
    [property: JsonRequired] ProtectionBackupPolicy Backups,
    [property: JsonRequired] ProtectionRecoveryPolicy CrashRecovery)
{
    internal static ProtectionPolicy Read(ServerProfile profile) => new(
        new(profile.Backups?.Enabled ?? false, profile.Backups?.RetentionCount ?? 5,
            profile.Backups?.MinimumFreeSpaceMb ?? 1024), new(profile.CrashRecovery?.Enabled ?? false));

    internal static bool Valid(ProtectionPolicy? policy) => policy?.Backups is { } backup &&
        policy.CrashRecovery is not null && backup.RetentionCount is >= 1 and <= 50 &&
        backup.MinimumFreeSpaceMb is >= 0 and <= 1_048_576;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectionSettingsRequest(
    [property: JsonRequired] ProtectionPolicy Expected,
    [property: JsonRequired] ProtectionPolicy Next);

public sealed partial class HostManager
{
    // The client submits only these policy values. Comparing and merging under
    // one lifecycle gate prevents a stale page from replacing any other setting,
    // profile, world identity or more recent protection decision.
    public async Task<ActionResult> UpdateProtectionSettingsAsync(Guid profileId,
        ProtectionSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (profileId == Guid.Empty || request is null || !ProtectionPolicy.Valid(request.Expected) ||
                !ProtectionPolicy.Valid(request.Next))
                return Result(false, "InvalidProtectionPolicy",
                    "Use a complete backup and recovery policy, 1–50 retained backups and a reserve from 0 to 1048576 MB.");
            if (data.Recovery.LifecycleBlocked)
                return Result(false, "DataRecoveryRequired",
                    "Review and acknowledge local data recovery before changing protection settings.");
            var current = settings.Profiles.SingleOrDefault(profile => profile.Id == profileId);
            if (current is null) return Result(false, "UnknownProfile", "Choose a saved Host server.");
            if (current.WorldLoadRehearsalId is not null)
                return Result(false, "WorldLoadSetupReadOnly",
                    "Protection settings for a disposable rehearsal are read-only. Use its fixed review and cleanup actions.");
            if (data.HasProtected(SetupRestorePendingName(profileId)))
                return Result(false, "SetupRestoreRecoveryRequired",
                    "A complete setup restore was interrupted. Finish its recovery before changing protection settings.");
            if (!games.TryGet(current.Kind, out var driver) || !driver.SupportsBackups || current.Kind == GameKinds.Custom)
                return Result(false, "ProtectionUnsupported",
                    "Protection settings require a reviewed built-in game with backup support.");
            if ((current.SeparateCopySourceProfileId is not null || current.SeparateCopyBranchHash is not null) &&
                request.Next.CrashRecovery.Enabled)
                return Result(false, "SeparateCopyManualStartRequired",
                    "A separate recovery branch requires an explicit manual Start. Automatic crash recovery cannot be enabled.");
            if (request.Next.CrashRecovery.Enabled && !driver.SupportsCrashRecovery)
                return Result(false, "CrashRecoveryUnsupported", "Automatic crash recovery is unavailable for this game preview.");
            // Retained Failed/Unknown run records are not Offline. Do not clear
            // or reinterpret the selected server's identity as a settings edit.
            if (runs.Any(run => run.ProfileId == profileId))
                return Result(false, "ProfileInUse", "Stop or resolve this server and confirm it is Offline before changing protection settings.");
            if (BlockIfSaveDirectoryActive(current, "changing protection settings") is { } active) return active;
            if (ProtectionPolicy.Read(current) != request.Expected)
                return Result(false, "ProtectionPolicyChanged",
                    "Protection settings changed since this editor opened. Review the current values before saving again.");

            // UpdateSettingsLocked validates the existing sharing, listener,
            // setup, profile and run restrictions. A deep copy keeps its
            // normalization/validation from mutating current state on rejection.
            var next = JsonSerializer.Deserialize<HostSettings>(JsonSerializer.SerializeToUtf8Bytes(settings)) ??
                throw new InvalidDataException("The current Host settings could not be copied.");
            var changed = next.Profiles.Single(profile => profile.Id == profileId);
            changed.Backups = new() { Enabled = request.Next.Backups.Enabled,
                RetentionCount = request.Next.Backups.RetentionCount,
                MinimumFreeSpaceMb = request.Next.Backups.MinimumFreeSpaceMb };
            changed.CrashRecovery = new() { Enabled = request.Next.CrashRecovery.Enabled };
            cancellationToken.ThrowIfCancellationRequested();
            return UpdateSettingsLocked(next);
        }
        finally { gate.Release(); }
    }
}
