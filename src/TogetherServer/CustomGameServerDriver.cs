using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

internal sealed class CustomGameServerDriver(LocalData data) : IGameServerDriver
{
    public string Kind => GameKinds.Custom;
    public string DisplayName => "Custom scripted game";
    public bool ShowPortDiagnostics => true;
    public string ManagedExecutablePath(ServerProfile profile) => CustomPowerShell.Path;

    public IReadOnlyList<GamePort> Ports(ServerProfile profile)
    {
        var custom = profile.Custom ?? new CustomGameOptions();
        return new[] { new GamePort(custom.PrimaryProtocol, profile.GamePort, "Game") }
            .Concat(custom.AdditionalPorts ?? [])
            .DistinctBy(port => (port.Protocol.ToUpperInvariant(), port.Family, port.Port))
            .ToList();
    }

    public string? JoinAddress(ServerProfile profile, string? publicIp) =>
        profile.Custom?.ShareJoinAddress == true && GameConnection.IsPublicIpv4(publicIp)
            ? $"{IPAddress.Parse(publicIp!)}:{profile.GamePort}" : null;

    public GameValidation? ValidateForStart(ServerProfile profile)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(CustomPowerShell.Path))
            return new("PowerShellUnavailable", "Windows PowerShell is required for a custom scripted game.");
        if (!Directory.Exists(profile.WorldDirectory))
            return new("CustomWorkingDirectoryMissing", "Choose an existing working/save directory for the custom game.");
        if (!data.HasCustomScripts(profile.Id))
            return new("CustomScriptsRequired", "Save the Start, Status/players, and Stop scripts before starting.");
        try
        {
            var scripts = data.LoadCustomScripts(profile.Id);
            var error = scripts is null ? "Custom scripts are unavailable." : CustomGameScripts.Validate(scripts);
            return error is null ? null : new("CustomScriptsInvalid", error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Security.Cryptography.CryptographicException or JsonException)
        {
            return new("CustomScriptsUnavailable", "Protected custom scripts could not be read: " + ex.Message);
        }
    }

    public void PrepareStart(ServerProfile profile, ManagedRun run) { }

    public GameLaunchResult Start(ServerProfile profile, ManagedRun run)
    {
        var scripts = data.LoadCustomScripts(profile.Id)
            ?? throw new InvalidOperationException("Custom scripts are unavailable.");
        var processId = CustomPowerShell.StartLongRunning(scripts.Start, run);
        return new("CustomStarting",
            "Custom Start script launched. Waiting for the Status/players script.", processId);
    }

    public GameHealthResult Health(ManagedRun run) => ProbeContract(run).Health;

    internal CustomContractProbe ProbeContract(ManagedRun run)
    {
        CustomScriptBundle? scripts;
        try { scripts = data.LoadCustomScripts(run.ProfileId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Security.Cryptography.CryptographicException or JsonException)
        {
            return Untrusted("CustomScriptsUnavailable", "Protected custom scripts could not be read: " + ex.Message);
        }
        if (scripts is null) return Untrusted("CustomScriptsUnavailable", "Protected custom scripts are unavailable.");

        var probeId = Guid.NewGuid().ToString("N");
        var operationId = run.OperationId.ToString("D");
        var result = CustomPowerShell.Run(scripts.Status, run, "status", TimeSpan.FromSeconds(4), probeId);
        if (result.TimedOut)
            return Untrusted("CustomStatusTimedOut", "The custom Status/players script did not finish within 4 seconds.");
        if (result.ExitCode != 0)
            return Untrusted("CustomStatusFailed", "The custom Status/players script failed" + ErrorSuffix(result.Error) + ".");
        if (result.Output.Length > CustomGameScripts.MaxOutputCharacters)
            return Untrusted("CustomStatusTooLarge", "The custom Status/players script returned too much output.");

        CustomHealthPayload? payload;
        try { payload = JsonSerializer.Deserialize<CustomHealthPayload>(result.Output, CustomGameScripts.Json); }
        catch (JsonException ex)
        {
            return Untrusted("CustomStatusInvalid",
                "Status/players did not return one valid JSON object: " + ex.Message);
        }
        var error = CustomGameScripts.Validate(payload);
        if (error is not null) return Untrusted("CustomStatusInvalid", error);

        var contractError = CustomGameScripts.ValidateContractV2(payload!, probeId, operationId);
        var contractValid = contractError is null;
        var certificationError = contractValid ? CertificationError(run, scripts) : contractError;
        var trusted = contractValid && certificationError is null;

        var state = payload!.State;
        var ready = state.Equals("Ready", StringComparison.OrdinalIgnoreCase);
        var normalizedState = ready ? "Ready" : state.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? "Failed" : "Starting";
        var detail = string.IsNullOrWhiteSpace(payload.Detail)
            ? $"Custom script reports {normalizedState}."
            : payload.Detail.Trim();
        if (ready && trusted)
            detail += " Owner-certified Custom control is active; exact player counts may authorize guarded lifecycle actions.";
        else if (ready && contractValid)
            detail += " Contract v2 is valid, but owner certification is required before the count can authorize lifecycle actions.";
        else if (ready)
            detail += " Player data is display-only because the authoritative Custom contract is invalid: " + contractError;
        var health = new GameHealthResult(ready, ready ? (trusted ? "CustomCertifiedReady" : "CustomScriptReady") : "CustomScript" + normalizedState,
            normalizedState, detail, payload.OnlinePlayers, payload.MaxPlayers,
            payload.Players?.Select(name => name.Trim()).ToList(), PlayerCountTrusted: trusted);
        return new(health, contractValid, contractError, certificationError);
    }

    public async Task<GameStopResult> StopAsync(Process process, ManagedRun run)
    {
        var scripts = data.LoadCustomScripts(run.ProfileId)
            ?? throw new InvalidOperationException("Custom scripts are unavailable.");
        var stop = CustomPowerShell.Run(scripts.Stop, run, "stop", TimeSpan.FromSeconds(15));
        if (stop.TimedOut)
            return new("CustomStopTimedOut", "The custom Stop script did not finish within 15 seconds. The managed run remains recorded.", 1);
        if (stop.ExitCode != 0)
            return new("CustomStopScriptFailed", "The custom Stop script failed" + ErrorSuffix(stop.Error) + ". The managed run remains recorded.", 1);

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            process.Refresh();
            if (process.HasExited) break;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The custom Start script process did not exit within 90 seconds.");
            await Task.Delay(100);
        }
        return new("CustomStopped",
            "The custom Start script process exited after the Stop script. Game save behavior remains owner-verified.", 0);
    }

    private string? CertificationError(ManagedRun run, CustomScriptBundle scripts)
    {
        try
        {
            var profile = data.LoadSettings().Profiles.SingleOrDefault(candidate =>
                candidate.Id == run.ProfileId && candidate.Kind == GameKinds.Custom);
            if (profile is null) return "The saved Custom profile is unavailable.";
            var certification = data.LoadCustomCertification(run.ProfileId);
            if (certification is null) return "Owner certification has not been completed.";
            return CustomCertification.Matches(certification, profile, scripts, Ports(profile))
                ? null
                : "The Custom lifecycle configuration changed after certification.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return "The protected Custom certification could not be verified: " + ex.GetType().Name + ".";
        }
    }

    private static CustomContractProbe Untrusted(string code, string detail) =>
        new(new(false, code, "Unknown", detail, PlayerCountTrusted: false), false, detail, detail);

    private static string ErrorSuffix(string error) => string.IsNullOrWhiteSpace(error)
        ? "" : ": " + error.Trim().Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(240, error.Trim().Length)];
}

internal sealed record CustomContractProbe(GameHealthResult Health, bool ContractValid,
    string? ContractError, string? CertificationError);

internal sealed record CustomHealthPayload(string State, string? Detail, int? OnlinePlayers,
    int? MaxPlayers, IReadOnlyList<string>? Players, int? ContractVersion = null,
    string? ProbeId = null, string? OperationId = null);

internal static class CustomGameScripts
{
    public const int MaxScriptCharacters = 64 * 1024;
    public const int MaxOutputCharacters = 64 * 1024;
    public const string StatusContract = "Status/players must output one JSON object with state Ready, Starting, or Failed; optional detail, onlinePlayers, maxPlayers, and players fields. Contract v2 certification also requires contractVersion, probeId, operationId, and an exact onlinePlayers count.";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive = true };

    public static string? Validate(CustomScriptBundle scripts)
    {
        if (string.IsNullOrWhiteSpace(scripts.Start) || string.IsNullOrWhiteSpace(scripts.Status) || string.IsNullOrWhiteSpace(scripts.Stop))
            return "Start, Status/players, and Stop scripts are all required.";
        if (scripts.Start.Length > MaxScriptCharacters || scripts.Status.Length > MaxScriptCharacters || scripts.Stop.Length > MaxScriptCharacters)
            return "Each custom script must be 64 KB or smaller.";
        return null;
    }

    public static string? Validate(CustomHealthPayload? payload)
    {
        if (payload is null) return "Status/players returned JSON null instead of a status object.";
        if (!(payload.State?.Equals("Ready", StringComparison.OrdinalIgnoreCase) == true ||
            payload.State?.Equals("Starting", StringComparison.OrdinalIgnoreCase) == true ||
            payload.State?.Equals("Failed", StringComparison.OrdinalIgnoreCase) == true))
            return "Status state must be Ready, Starting, or Failed.";
        if (payload.Detail is { Length: > 300 } || payload.Detail?.Any(char.IsControl) == true)
            return "Status detail must be at most 300 characters without control characters.";
        if (payload.OnlinePlayers is < 0 || payload.MaxPlayers is <= 0 ||
            payload.OnlinePlayers is { } online && payload.MaxPlayers is { } maximum && online > maximum)
            return "Status player counts must be nonnegative, with onlinePlayers no greater than maxPlayers.";
        if (payload.Players is { Count: > 64 } || payload.Players?.Any(name =>
            string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl)) == true)
            return "Status players may contain at most 64 names of 1 to 64 characters without control characters.";
        return null;
    }

    public static string? ValidateContractV2(CustomHealthPayload payload, string probeId, string operationId)
    {
        if (payload.ContractVersion != CustomCertification.ContractVersion)
            return $"contractVersion must equal {CustomCertification.ContractVersion}.";
        if (!string.Equals(payload.ProbeId, probeId, StringComparison.Ordinal))
            return "probeId did not echo the unique current probe.";
        if (!string.Equals(payload.OperationId, operationId, StringComparison.OrdinalIgnoreCase))
            return "operationId did not echo the current managed run.";
        if (payload.OnlinePlayers is null)
            return "onlinePlayers is required for authoritative Custom control.";
        if (payload.Players is { } players && players.Count != payload.OnlinePlayers)
            return "players and onlinePlayers contradict each other.";
        if (payload.State.Equals("Failed", StringComparison.OrdinalIgnoreCase) && payload.OnlinePlayers != 0)
            return "a Failed state cannot report online players.";
        return null;
    }
}

internal sealed record CustomScriptRun(int ExitCode, string Output, string Error, bool TimedOut);

internal static class CustomPowerShell
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public static int StartLongRunning(string script, ManagedRun run)
    {
        using var process = new Process { StartInfo = StartInfo(run, "start", redirectOutput: false) };
        if (!process.Start()) throw new InvalidOperationException("Windows PowerShell did not start.");
        process.StandardInput.WriteLine("$env:TOGETHERSERVER_MANAGED_PID = [string]$PID; " + Invocation(script));
        process.StandardInput.Close();
        return process.Id;
    }

    public static CustomScriptRun Run(string script, ManagedRun run, string action, TimeSpan timeout,
        string? probeId = null)
    {
        using var process = new Process { StartInfo = StartInfo(run, action, redirectOutput: true, probeId) };
        if (!process.Start()) return new(1, "", "Windows PowerShell did not start", false);
        var output = ReadLimitedAsync(process.StandardOutput, CustomGameScripts.MaxOutputCharacters + 1);
        var error = ReadLimitedAsync(process.StandardError, 4096);
        process.StandardInput.WriteLine(Invocation(script));
        process.StandardInput.Close();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(); process.WaitForExit(2000); } catch { /* This is the exact auxiliary process we launched. */ }
            return new(1, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult(), true);
        }
        return new(process.ExitCode, output.GetAwaiter().GetResult().Trim(), error.GetAwaiter().GetResult().Trim(), false);
    }

    private static ProcessStartInfo StartInfo(ManagedRun run, string action, bool redirectOutput,
        string? probeId = null)
    {
        var info = new ProcessStartInfo(Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            WorkingDirectory = run.WorldDirectory
        };
        info.ArgumentList.Add("-NoLogo");
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("-");
        info.Environment["TOGETHERSERVER_ACTION"] = action;
        info.Environment["TOGETHERSERVER_PROFILE_ID"] = run.ProfileId.ToString("D");
        info.Environment["TOGETHERSERVER_WORLD_ID"] = run.WorldId;
        info.Environment["TOGETHERSERVER_WORKING_DIRECTORY"] = run.WorldDirectory;
        info.Environment["TOGETHERSERVER_GAME_PORT"] = run.GamePort.ToString();
        info.Environment["TOGETHERSERVER_MANAGED_PID"] = run.ProcessId?.ToString() ?? "";
        info.Environment["TOGETHERSERVER_CONTRACT_VERSION"] = CustomCertification.ContractVersion.ToString();
        info.Environment["TOGETHERSERVER_PROBE_ID"] = probeId ?? "";
        info.Environment["TOGETHERSERVER_OPERATION_ID"] = run.OperationId.ToString("D");
        return info;
    }

    // Keep the owner-provided script out of process command-line inspection,
    // while ensuring PowerShell receives a single complete statement even for
    // multi-line scripts over redirected standard input.
    private static string Invocation(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        return "& ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
            encoded + "'))))";
    }

    private static async Task<string> ReadLimitedAsync(TextReader reader, int limit)
    {
        var result = new StringBuilder(Math.Min(limit, 4096));
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            var remaining = limit - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, read));
        }
        return result.ToString();
    }
}
