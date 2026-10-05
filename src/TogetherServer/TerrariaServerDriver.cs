using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace TogetherServer;

public sealed record TerrariaImportRequest(Guid ProfileId);
public sealed record TerrariaImportResult(bool Ok, string Code, string Message,
    string? WorldId = null, string? WorldDirectory = null);

internal static class TerrariaSetup
{
    public static TerrariaImportResult ImportCopy(LocalData data, Guid profileId, string sourcePath)
    {
        if (profileId == Guid.Empty || string.IsNullOrWhiteSpace(sourcePath))
            return new(false, "InvalidTerrariaWorld", "Choose an existing Terraria .wld file.");
        var stage = "";
        try
        {
            var source = Path.GetFullPath(sourcePath);
            var worldId = Path.GetFileNameWithoutExtension(source);
            if (!Path.GetExtension(source).Equals(".wld", StringComparison.OrdinalIgnoreCase) ||
                !ValheimSetup.ValidWorldId(worldId) || !ManagedImportFiles.PlainFile(source) ||
                AppInstance.ContainsPath(data.ManagedWorldsRoot, source))
                return new(false, "InvalidTerrariaWorld", "Choose an original Terraria .wld file outside managed storage.");
            var destination = data.NewWorldDirectory(profileId);
            if (Directory.Exists(destination))
                return new(false, "AlreadyImported", "This server already has managed world storage. No files were replaced.");
            Directory.CreateDirectory(data.ManagedWorldsRoot);
            if (!ManagedImportFiles.PlainDirectory(data.ManagedWorldsRoot))
                throw new InvalidDataException("Managed world storage cannot be a filesystem link.");
            stage = Path.Combine(data.ManagedWorldsRoot, ".terraria-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            ManagedImportFiles.CopyVerified(source, Path.Combine(stage, worldId + ".wld"));
            var backup = source + ".bak";
            if (File.Exists(backup))
            {
                if (!ManagedImportFiles.PlainFile(backup)) throw new InvalidDataException("The Terraria world backup is linked.");
                ManagedImportFiles.CopyVerified(backup, Path.Combine(stage, worldId + ".wld.bak"));
            }
            Directory.Move(stage, destination);
            stage = "";
            data.TryAudit($"terraria-world-imported {profileId} world={worldId} {DateTimeOffset.UtcNow:O}");
            return new(true, "TerrariaWorldImported",
                "World copied and hash-verified in managed storage. The original was left unchanged.",
                worldId, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   CryptographicException or ArgumentException or NotSupportedException)
        {
            return new(false, "TerrariaImportFailed", "The Terraria world could not be copied and verified. The original was left unchanged.");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(stage) && Directory.Exists(stage) &&
                AppInstance.ContainsPath(data.ManagedWorldsRoot, stage))
            {
                try { Directory.Delete(stage, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static bool IsImportedCopy(LocalData data, ServerProfile profile)
    {
        try
        {
            var expected = data.NewWorldDirectory(profile.Id);
            return Path.GetFullPath(profile.WorldDirectory).Equals(Path.GetFullPath(expected),
                       StringComparison.OrdinalIgnoreCase) && ManagedImportFiles.PlainDirectory(data.ManagedWorldsRoot) &&
                   ManagedImportFiles.PlainDirectory(expected) &&
                   ManagedImportFiles.PlainFile(Path.Combine(expected, profile.WorldId + ".wld"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

}

internal sealed class TerrariaServerDriver(LocalData data) : IGameServerDriver
{
    public string Kind => GameKinds.Terraria;
    public string DisplayName => "Terraria (preview)";
    public bool ShowPortDiagnostics => true;
    public bool SupportsCrashRecovery => false;
    public bool SupportsBackups => true;
    public string? ManagedSaveDirectory(ServerProfile profile) => profile.WorldDirectory;
    public string ManagedExecutablePath(ServerProfile profile) => profile.ExecutablePath;
    public IReadOnlyList<GamePort> Ports(ServerProfile profile) => [new("TCP", profile.GamePort, "Game")];
    public string? JoinAddress(ServerProfile profile, string? publicIp) =>
        GameConnection.IsPublicIpv4(publicIp) ? $"{IPAddress.Parse(publicIp!)}:{profile.GamePort}" : null;

    public GameValidation? ValidateForStart(ServerProfile profile)
    {
        if (!Path.GetFileName(profile.ExecutablePath).Equals("TerrariaServer.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(profile.ExecutablePath))
            return new("TerrariaExecutableRequired", "Choose owner-installed TerrariaServer.exe.");
        if (!TerrariaSetup.IsImportedCopy(data, profile))
            return new("TerrariaImportRequired", "Copy an existing .wld world into this server's managed storage before Start.");
        if (ServerFiles.CheckActiveConfiguration(data, profile, "terraria-config") is { } configurationIssue)
            return new("TerrariaConfigInvalid", configurationIssue);
        return null;
    }

    public void PrepareStart(ServerProfile profile, ManagedRun run) =>
        run.LogPath = data.NewRunLogPath(run.OperationId);

    public GameLaunchResult Start(ServerProfile profile, ManagedRun run)
    {
        var world = Path.Combine(profile.WorldDirectory, profile.WorldId + ".wld");
        var arguments = new List<string>
        {
            "-world", world, "-port", run.GamePort.ToString(System.Globalization.CultureInfo.InvariantCulture), "-noupnp"
        };
        var configuration = Path.Combine(run.WorldDirectory, "serverconfig.txt");
        if (File.Exists(configuration))
        {
            arguments.Add("-config");
            arguments.Add(configuration);
        }
        var id = WindowsConsoleProcess.StartMinecraftCaptured(run, arguments);
        return new("TerrariaStarting", "Terraria preview process launched. Waiting for a local TCP listener; real join and save acceptance remain pending.", id);
    }

    public GameHealthResult Health(ManagedRun run)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            client.ConnectAsync(IPAddress.Loopback, run.GamePort, timeout.Token).GetAwaiter().GetResult();
            return LocalTcpObservation(true);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return LocalTcpObservation(false);
        }
    }

    internal static GameHealthResult LocalTcpObservation(bool open) => open
        ? new(true, "TerrariaTcpOpen", "Listening",
            "A local TCP listener is open. Terraria readiness, player count, Friend join, and save integrity are unverified; remote Stop stays blocked.",
            PlayerCountTrusted: false)
        : new(false, "TerrariaStarting", "Starting",
            "The exact managed process exists; waiting for a local TCP listener.", PlayerCountTrusted: false);

    public Task<GameStopResult> StopAsync(Process process, ManagedRun run)
    {
        var handle = process.Handle;
        WindowsConsoleProcess.RequestTerrariaExit(process, run);
        var exitCode = WindowsConsoleProcess.ExitCode(handle);
        return Task.FromResult(exitCode == 0
            ? new GameStopResult("TerrariaStopped", "Terraria exited after its fixed exit command. Verify a real saved change before relying on this preview.", exitCode)
            : new GameStopResult("StopFailed", "Terraria exited with a nonzero status. The run remains recorded for review.", exitCode));
    }
}
