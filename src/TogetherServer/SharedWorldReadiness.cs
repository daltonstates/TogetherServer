using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;

namespace TogetherServer;

public sealed record TakeoverLocalSetup(string? ServerFile, string? GameVersion,
    IReadOnlyList<SharedWorldPortableAddOn>? EnabledAddOns, bool NewPasswordConfigured,
    int ControlPort, int GamePort);
public sealed record TakeoverReadiness(bool Ready, IReadOnlyList<string> Reasons,
    long? Version, string? VersionHash, bool RehearsalPassed = false);

// Eligibility and externally observed routes are deliberately separate from local file checks.
// A later signed takeover decision may supply these inputs; the current caller supplies false.
internal sealed record TakeoverAuthority(bool Eligible, bool ControlRouteVerified,
    bool GameRouteVerified, bool FreshDestinationVerified = false,
    bool GameLoadVerified = false, bool AddOnsVerified = false);

internal static class SharedWorldReadiness
{
    private const long ReserveBytes = 1024L * 1024 * 1024;

    internal static TakeoverReadiness Check(string vaultRoot, string rehearsalRoot,
        TakeoverLocalSetup setup, TakeoverAuthority authority, string? pinnedKey, Guid? approvedGroup,
        Func<string, long>? freeBytes = null)
    {
        var reasons = new List<string>();
        SharedWorldVersion? version = null;
        try
        {
            var deviceRoot = Path.GetDirectoryName(Path.GetFullPath(vaultRoot))!;
            var receivedRoot = Path.GetDirectoryName(deviceRoot)!;
            if (Path.GetFileName(receivedRoot) != "received-shared-worlds")
                throw new InvalidDataException("The received vault location is invalid.");
            SharedWorldService.EnsureUnlinkedRoot(Path.GetDirectoryName(receivedRoot)!, vaultRoot);
            version = FriendLink.ReadReceivedLatest(vaultRoot);
            if (version is null) reasons.Add("Receive a verified save first.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or CryptographicException)
        { reasons.Add("The received save did not pass verification."); }
        if (version is not null)
        {
            if (string.IsNullOrWhiteSpace(pinnedKey) || approvedGroup is null ||
                version.SigningPublicKey != pinnedKey || version.GroupId != approvedGroup)
                reasons.Add("This save is not from the approved Host signing identity and group.");
            if (version.Schema < 4) reasons.Add("Receive a save with current portable setup details.");
            try
            {
                if (string.IsNullOrWhiteSpace(setup.ServerFile) || !Path.IsPathFullyQualified(setup.ServerFile) ||
                    !File.Exists(setup.ServerFile) ||
                    (File.GetAttributes(setup.ServerFile) & FileAttributes.ReparsePoint) != 0)
                    reasons.Add("Choose an installed game server file on this PC.");
                else if (version.Game == GameKinds.MinecraftJava)
                {
                    var expected = version.PortableSetup.JavaServerJarSha256;
                    if (!Path.GetExtension(setup.ServerFile).Equals(".jar", StringComparison.OrdinalIgnoreCase))
                        reasons.Add("Choose the matching Java server JAR.");
                    else
                    {
                        using var installed = File.OpenRead(setup.ServerFile);
                        var actual = Convert.ToHexString(SHA256.HashData(installed));
                        if (expected == "Unknown" || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                            reasons.Add("The installed Java server JAR does not match this save.");
                    }
                }
                else if (version.Game != GameKinds.Fixture)
                {
                    var detected = ServerAddOns.GameVersion(new ServerProfile
                    { Kind = version.Game, ExecutablePath = setup.ServerFile });
                    if (detected == "Unknown" || detected != version.PortableSetup.GameVersion)
                        reasons.Add("The selected game server file does not report the required version.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { reasons.Add("The selected game server file could not be checked."); }
            if (string.IsNullOrWhiteSpace(setup.GameVersion) ||
                !string.Equals(version.PortableSetup.GameVersion, setup.GameVersion, StringComparison.Ordinal))
                reasons.Add("Install and select the matching game server version.");
            var expectedAddOns = version.PortableSetup.AddOns ?? [];
            var actualAddOns = setup.EnabledAddOns ?? [];
            if (expectedAddOns.Count != actualAddOns.Count || expectedAddOns.Any(item =>
                !actualAddOns.Contains(item)))
                reasons.Add("Install and enable the matching add-ons and packs.");
            else if (expectedAddOns.Count > 0 && !authority.AddOnsVerified)
                reasons.Add("Verify installed add-on packages against the shared requirements.");
            if (!setup.NewPasswordConfigured)
                reasons.Add("Set a new game password on this PC.");
            if (setup.GamePort is < 1 or > 65535 ||
                setup.ControlPort is < 1 or > 65535 || setup.ControlPort == setup.GamePort)
                reasons.Add("Choose valid, separate control and game ports for this PC.");
            else
            {
                try
                {
                    if (PortInUse(setup.ControlPort) || PortInUse(setup.GamePort))
                        reasons.Add("A selected port is already in use on this PC.");
                }
                catch (NetworkInformationException)
                { reasons.Add("The selected ports could not be checked on this PC."); }
            }
            try
            {
                var bytes = SharedWorldService.BoundedTotalBytes(version.Files);
                var available = freeBytes?.Invoke(rehearsalRoot) ??
                    new DriveInfo(Path.GetPathRoot(Path.GetFullPath(rehearsalRoot))!).AvailableFreeSpace;
                if (available < bytes + ReserveBytes)
                    reasons.Add("Free more disk space before copying this save; keep at least 1 GiB free.");
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException)
            { reasons.Add("Available disk space could not be checked."); }
        }
        if (!authority.Eligible) reasons.Add("The owner has not granted this PC takeover permission.");
        if (!authority.FreshDestinationVerified) reasons.Add("Choose a fresh managed world location on this PC.");
        if (!authority.ControlRouteVerified) reasons.Add("Test the future Host's direct-IP Friend control route from another PC.");
        if (!authority.GameRouteVerified) reasons.Add("Test the future Host's game route with a real Friend join.");
        if (!authority.GameLoadVerified) reasons.Add("Confirm this save loads and survives a restart in the real game.");
        return new(reasons.Count == 0, reasons, version?.Number, version?.VersionHash);
    }

    internal static TakeoverReadiness Rehearse(string dataRoot, string vaultRoot,
        TakeoverLocalSetup setup, TakeoverAuthority authority, string? pinnedKey, Guid? approvedGroup,
        Func<string, long>? freeBytes = null)
    {
        var root = Path.Combine(dataRoot, "shared-world-rehearsals");
        SharedWorldService.EnsureUnlinkedRoot(dataRoot, root);
        var checkedState = Check(vaultRoot, root, setup, authority, pinnedKey, approvedGroup, freeBytes);
        if (checkedState.Reasons.Any(reason => reason.Contains("verification", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("approved Host signing identity", StringComparison.OrdinalIgnoreCase)))
            return checkedState;
        SharedWorldVersion? version;
        try { version = FriendLink.ReadReceivedLatest(vaultRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or CryptographicException)
        { return checkedState with { Reasons = [.. checkedState.Reasons, "The received save changed during rehearsal. No copy was made."] }; }
        if (version is not null && (version.VersionHash != checkedState.VersionHash ||
            version.SigningPublicKey != pinnedKey || version.GroupId != approvedGroup))
            return checkedState with
            {
                Reasons = [.. checkedState.Reasons,
                "The approved save changed during rehearsal. No copy was made."]
            };
        if (version is null || checkedState.Reasons.Any(reason => reason.Contains("disk space", StringComparison.OrdinalIgnoreCase)))
            return checkedState;
        var destination = Path.Combine(root, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(destination) || File.Exists(destination))
            return checkedState with { Reasons = [.. checkedState.Reasons, "A fresh rehearsal folder could not be created."] };
        Directory.CreateDirectory(destination);
        try
        {
            SharedWorldService.EnsureUnlinkedRoot(dataRoot, destination);
            foreach (var item in version.Files)
            {
                var source = SharedWorldService.SafeChild(Path.Combine(vaultRoot, version.VersionHash,
                    SharedWorldService.PayloadDirectory), item.Path);
                SharedWorldService.VerifyFile(source, item);
                var target = SharedWorldService.SafeChild(destination, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    from.CopyTo(to);
                SharedWorldService.VerifyFile(target, item);
            }
            return checkedState with
            {
                RehearsalPassed = true,
                Reasons = [.. checkedState.Reasons,
                    "Disposable file copy passed hash checks. A real game load, join, and save still need testing."]
            };
        }
        finally
        {
            // This fresh GUID directory was created by this call only. Refuse cleanup
            // if any link appeared; never recurse through an arbitrary destination.
            if (Path.GetDirectoryName(destination) != root || !SafeRehearsalTree(destination))
                throw new InvalidDataException("The rehearsal folder could not be removed safely.");
            Directory.Delete(destination, true);
        }
    }

    private static bool SafeRehearsalTree(string root)
    {
        if (!Directory.Exists(root)) return false;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            if (!Directory.Exists(path)) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                pending.Push(child);
        }
        return true;
    }

    private static bool PortInUse(int port)
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        return properties.GetActiveTcpListeners().Any(item => item.Port == port) ||
            properties.GetActiveUdpListeners().Any(item => item.Port == port);
    }
}
