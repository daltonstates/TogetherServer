using System.Net.Http.Json;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    internal async Task<GameCompatibilityResult> ReadGameCompatibilityAsync(Guid profileId,
        ManualClientVersionChange? change = null, CancellationToken ct = default,
        Func<string, CancellationToken, InstalledClientMetadata>? discover = null)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This saved Host connection is closing.");
        var entered = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await gate.WaitAsync(timeout.Token); entered = true;
            if (config is null) return new(false, "NotPaired", "Connect to a Host first.");
            if (view.HostCapabilities.Contains(CompanionProtocol.GameRequirementsCapability, StringComparer.Ordinal) != true)
                return new(false, "RequirementsUpdateRequired", "Update the Host app to share pre-join requirements.");
            var (profile, denial) = await FeatureProfileAsync(profileId, timeout.Token);
            if (profile is null) return new(false, denial, FeatureDenialMessage(denial));
            if (!view.HostCapabilities.Contains(CompanionProtocol.GameRequirementsCapability, StringComparer.Ordinal))
                return new(false, "RequirementsUpdateRequired", "Update the Host app to share pre-join requirements.");
            if (!GameCompatibility.Supported(profile.Kind)) return new(false, "UnsupportedGame", "This game has no reviewed pre-join checks.");
            if (change is not null && change.Version is not null && !GameCompatibility.ValidVersion(change.Version))
                return new(false, "InvalidVersion", "Enter the version shown in your game, using at most 48 version characters.");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/companion/servers/{profileId}/requirements");
            request.Headers.Add("X-TogetherServer-Capability", CompanionProtocol.GameRequirementsCapability);
            using var response = await HostClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var payload = await ReadFeaturePayloadAsync(response.Content, GameCompatibility.MaximumWireBytes, timeout.Token);
            if (payload is null) return new(false, "RequirementsResponseTooLarge", "The Host returned more requirement data than allowed.");
            if (!response.IsSuccessStatusCode)
            {
                var code = FeatureDenialCode(payload);
                return new(false, code, FeatureDenialMessage(code));
            }
            var result = JsonSerializer.Deserialize<GameRequirementsResult>(payload, Json);
            if (result?.Ok != true)
                return new(false, "RequirementsUnavailable", "The Host could not safely read this server's requirements.");
            if (!GameCompatibility.ValidRequirements(result.Requirements, profileId, profile.Kind))
                return new(false, "InvalidRequirements", "The Host returned invalid requirement data.");
            var requirements = result.Requirements!;
            var installed = await Task.Run(() => (discover ?? InstalledGameClient.Observe)(profile.Kind, timeout.Token), timeout.Token).WaitAsync(timeout.Token);
            // Reauthenticate after both Host reads and local discovery. A stale card is not assignment.
            var (current, finalDenial) = await FeatureProfileAsync(profileId, timeout.Token);
            if (current is null) return new(false, finalDenial, FeatureDenialMessage(finalDenial));
            if (current.Kind != profile.Kind) return new(false, "ProfileChanged", "The Host changed this game's setup. Refresh the server card.");
            if (change is not null) ManualClientVersionStore.Save(data, config.HostId, profileId, profile.Kind, change.Version);
            var manual = ManualClientVersionStore.Read(data, config.HostId, profileId, profile.Kind);
            // A manual value is used only when the installed game version cannot be observed.
            var version = installed.Version ?? manual;
            var source = installed.Version is not null ? installed.Source : manual is not null ? "Manual" : "Unknown";
            return new(true, "CompatibilityRead", "Version and add-on comparisons are informational. A real game join remains unverified.",
                requirements, version, source, GameCompatibility.CompareVersions(requirements.RequiredVersion, version),
                requirements.Kind == GameKinds.Factorio && requirements.AddOnState == "Known"
                    ? GameCompatibility.CompareAddOns(requirements.AddOns, installed.AddOns) : "Unknown");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(false, "RequirementsTimedOut", "The compatibility check timed out. Refresh when the Host is available."); }
        catch (Exception ex) when (GameCompatibility.ReadFailure(ex) || ex is HttpRequestException or System.Security.Cryptography.CryptographicException)
        { return new(false, "RequirementsUnavailable", "Compatibility could not be checked safely. Refresh or review the game version manually."); }
        finally { if (entered) gate.Release(); ReleaseRetained(); }
    }

    internal async Task<GameClientLaunchResult> OpenGameAsync(Guid profileId, IGameClientLaunchAdapter adapter,
        CancellationToken ct = default)
    {
        if (!TryRetain()) return new(false, "ConnectionClosed", "This saved Host connection is closing.");
        var entered = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await gate.WaitAsync(timeout.Token); entered = true;
            if (config is null) return new(false, "NotPaired", "Connect to a Host first.");
            var shown = view.Profiles.SingleOrDefault(item => item.Id == profileId);
            if (shown is null) return new(false, "PermissionDenied", "This server is not assigned to the selected saved connection.");
            var (current, denial) = await FeatureProfileAsync(profileId, timeout.Token);
            if (current is null) return new(false, denial, FeatureDenialMessage(denial));
            if (current.Kind != (shown.GameKind ?? shown.Kind) || current.JoinAddress != shown.JoinAddress)
                return new(false, "ProfileChanged", "The Host changed this game's setup or address. Refresh the server card before opening it.");
            if (GameClientLaunch.ValidAddress(current.JoinAddress) && GameClientLaunch.FixedUri(current.Kind) is { } uri)
            {
                var ready = await Task.Run(() => adapter.Prepare(uri, timeout.Token), timeout.Token).WaitAsync(timeout.Token);
                if (!ready.Ok) return ready;
                var (final, finalDenial) = await FeatureProfileAsync(profileId, timeout.Token);
                if (final is null) return new(false, finalDenial, FeatureDenialMessage(finalDenial));
                if (final.Kind != current.Kind || final.JoinAddress != current.JoinAddress)
                    return new(false, "ProfileChanged", "The Host changed this game's setup. Refresh before opening it.");
                current = final;
            }
            timeout.Token.ThrowIfCancellationRequested();
            // This is the only launch call. Polling, requirement reads and metadata discovery never call it.
            return GameClientLaunch.Open(current, adapter);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(false, "GameLaunchAuthorizationTimedOut", "The Host could not confirm current access. Refresh before opening the game."); }
        catch (Exception ex) when (GameCompatibility.ReadFailure(ex) || ex is HttpRequestException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        { return new(false, "GameLaunchUnavailable", "Current Host access could not be checked. Open the game yourself if needed."); }
        finally { if (entered) gate.Release(); ReleaseRetained(); }
    }

    private async Task<(PublicProfile? Profile, string Denial)> FeatureProfileAsync(Guid profileId, CancellationToken ct)
    {
        if (config is null || config.CredentialExpiresUtc <= DateTimeOffset.UtcNow) return (null, "CredentialExpired");
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/companion/status");
        using var response = await HostClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var payload = await ReadFeaturePayloadAsync(response.Content, 512 * 1024, ct);
        if (payload is null) return (null, "InvalidResponse");
        if (!response.IsSuccessStatusCode) return (null, FeatureDenialCode(payload));
        var status = JsonSerializer.Deserialize<CompanionStatus>(payload, Json);
        if (status is null || status.Profiles is null || status.Profiles.Count > 64 || status.Profiles.Any(item => item is null ||
                item.Id == Guid.Empty || item.Name is not { Length: > 0 and <= 100 } || item.Kind is not { Length: > 0 and <= 80 } ||
                item.State is not { Length: > 0 and <= 32 } || item.JoinAddress is { Length: > 128 }) || status.Protocol is null ||
            status.Protocol.Capabilities is null || status.Protocol.Capabilities.Count > 64 ||
            status.Protocol.Capabilities.Any(item => item is null || item.Length is < 1 or > 80 || item.Any(char.IsControl)) ||
            !CompanionProtocol.Supports(status.Protocol) || status.Protocol.Compatible != true ||
            status.Profiles.Select(item => item.Id).Distinct().Count() != status.Profiles.Count)
            return (null, "InvalidResponse");
        var profile = status.Profiles.SingleOrDefault(item => item.Id == profileId);
        ApplyStatus(status);
        if (profile is null) return (null, "PermissionDenied");
        // Kind is a legacy display label for Custom profiles. Launch/discovery must use
        // the saved driver kind, so a Custom game named "Valheim" cannot select its client.
        if (!status.Protocol.Capabilities.Contains(CompanionProtocol.GameRequirementsCapability, StringComparer.Ordinal) ||
            profile.GameKind is null)
            return (null, "RequirementsUpdateRequired");
        if (profile.GameKind is not (GameKinds.Valheim or GameKinds.Factorio or GameKinds.Terraria or
                GameKinds.MinecraftJava or GameKinds.MinecraftBedrock or GameKinds.Custom or GameKinds.Fixture))
            return (null, "InvalidResponse");
        return (profile with { Kind = profile.GameKind }, "");
    }
    private static string FeatureDenialCode(byte[] payload)
    {
        try
        {
            var denial = JsonSerializer.Deserialize<PairingDecision>(payload, Json);
            return denial?.Code is "Revoked" or "AccessExpired" or "ApprovalPending" or "CredentialExpired" or
                "PermissionDenied" or "RateLimited" or "RequirementsUpdateRequired" ? denial.Code : "HostAccessDenied";
        }
        catch (JsonException) { return "InvalidResponse"; }
    }
    private static string FeatureDenialMessage(string code) => code switch
    {
        "Revoked" => "The Host removed this PC's access. Ask the Host for current access.",
        "AccessExpired" => "The Host ended this PC's access. Ask the Host to extend or clear the deadline.",
        "PermissionDenied" => "This server is not currently assigned to this PC.",
        "ApprovalPending" => "The Host must approve this PC first.",
        "CredentialExpired" => "This PC's credential expired. Connect with the current server code.",
        "RequirementsUpdateRequired" => "Update both apps to share game requirements.",
        _ => "Current authenticated Host access could not be confirmed. Refresh and try again."
    };
    internal static async Task<byte[]?> ReadFeaturePayloadAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) return null;
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - (int)output.Length + 1)), ct);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximum) return null;
            output.Write(buffer, 0, read);
        }
    }
}
