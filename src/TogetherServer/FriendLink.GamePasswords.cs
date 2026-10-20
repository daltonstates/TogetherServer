using System.Net;
using System.Text.Json;

namespace TogetherServer;

internal sealed partial class FriendLink
{
    internal async Task<GamePasswordResult> RevealGamePasswordAsync(Guid profileId, CancellationToken ct = default)
    {
        if (!TryRetain()) return GamePasswords.Failure(profileId, "ConnectionClosed");
        var entered = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await gate.WaitAsync(timeout.Token); entered = true;
            if (config is null) return GamePasswords.Failure(profileId, "NotPaired");
            if (config.KnownRevoked) return GamePasswords.Failure(profileId, "Revoked");
            var (profile, denial) = await PasswordProfileAsync(profileId, timeout.Token);
            if (profile is null) return GamePasswords.Failure(profileId, denial);
            if (!view.HostCapabilities.Contains(CompanionProtocol.GamePasswordCapability, StringComparer.Ordinal))
                return GamePasswords.Failure(profileId, "GamePasswordUpdateRequired");
            if (profile.Kind != GameKinds.Valheim) return GamePasswords.Failure(profileId, "GamePasswordUnsupported");
            if (!profile.CanViewPassword) return GamePasswords.Failure(profileId, "PermissionDenied");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/companion/servers/{profileId}/game-password/reveal");
            request.Headers.Add("X-TogetherServer-Capability", CompanionProtocol.GamePasswordCapability);
            using var response = await HostClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var payload = await ReadFeaturePayloadAsync(response.Content, GamePasswords.MaximumResponseBytes, timeout.Token);
            if (payload is null) return GamePasswords.Failure(profileId, "GamePasswordResponseRejected");
            if (!response.IsSuccessStatusCode)
                return PasswordDenial(profileId, response.StatusCode, payload);
            var result = JsonSerializer.Deserialize<GamePasswordResult>(payload, Json);
            if (result is not { Ok: true, Code: "GamePasswordRevealed" } || result.ProfileId != profileId ||
                !GamePasswords.ValidPassword(result.Password))
                return GamePasswords.Failure(profileId, "GamePasswordResponseRejected");
            // No password enters config, cached profiles, status, chat or logs.
            // Reauthenticate before returning the ephemeral result to the UI.
            var (current, finalDenial) = await PasswordProfileAsync(profileId, timeout.Token);
            if (current is null) return GamePasswords.Failure(profileId, finalDenial);
            if (!view.HostCapabilities.Contains(CompanionProtocol.GamePasswordCapability, StringComparer.Ordinal))
                return GamePasswords.Failure(profileId, "GamePasswordUpdateRequired");
            if (!current.CanViewPassword || current.Kind != GameKinds.Valheim)
                return GamePasswords.Failure(profileId, "PermissionDenied");
            if (current.GamePasswordChangedUtc != result.GamePasswordChangedUtc)
                return GamePasswords.Failure(profileId, "GamePasswordChanged");
            return new(true, "GamePasswordRevealed", "Current game password read from the Host.",
                profileId, result.Password, result.GamePasswordChangedUtc);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return GamePasswords.Failure(profileId, "GamePasswordTimedOut"); }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException or
            InvalidDataException or System.Security.Cryptography.CryptographicException or ArgumentException)
        { return GamePasswords.Failure(profileId, "GamePasswordUnavailable"); }
        finally { if (entered) gate.Release(); ReleaseRetained(); }
    }

    private async Task<(PublicProfile? Profile, string Denial)> PasswordProfileAsync(Guid profileId, CancellationToken ct)
    {
        if (config is null || config.CredentialExpiresUtc <= DateTimeOffset.UtcNow) return (null, "CredentialExpired");
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/companion/status");
        using var response = await HostClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var payload = await ReadFeaturePayloadAsync(response.Content, 512 * 1024, ct);
        if (payload is null) return (null, "GamePasswordResponseRejected");
        if (!response.IsSuccessStatusCode) return (null, PasswordDenial(profileId, response.StatusCode, payload).Code);
        var status = JsonSerializer.Deserialize<CompanionStatus>(payload, Json);
        if (status?.Profiles is null || status.Profiles.Count > 64 || status.Profiles.Any(item => item is null ||
                item.Id == Guid.Empty || item.Name is not { Length: > 0 and <= 100 } || item.Kind is not { Length: > 0 and <= 80 } ||
                item.State is not { Length: > 0 and <= 32 } || item.JoinAddress is { Length: > 128 }) ||
            status.Protocol?.Capabilities is null || status.Protocol.Capabilities.Count > 64 ||
            status.Protocol.Capabilities.Any(item => item is null || item.Length is < 1 or > 80 || item.Any(char.IsControl)) ||
            !CompanionProtocol.Supports(status.Protocol) || status.Protocol.Compatible != true ||
            status.Profiles.Select(item => item.Id).Distinct().Count() != status.Profiles.Count)
            return (null, "GamePasswordResponseRejected");
        var profile = status.Profiles.SingleOrDefault(item => item.Id == profileId);
        ApplyStatus(status);
        if (profile is null) return (null, "PermissionDenied");
        if (!status.Protocol.Capabilities.Contains(CompanionProtocol.GamePasswordCapability, StringComparer.Ordinal))
            return (null, "GamePasswordUpdateRequired");
        return profile.GameKind == GameKinds.Valheim
            ? (profile with { Kind = GameKinds.Valheim }, "") : (null, "GamePasswordUnsupported");
    }

    private GamePasswordResult PasswordDenial(Guid profileId, HttpStatusCode statusCode, byte[] payload)
    {
        PairingDecision? denial;
        try { denial = JsonSerializer.Deserialize<PairingDecision>(payload, Json); }
        catch (JsonException) { return GamePasswords.Failure(profileId, "GamePasswordResponseRejected"); }
        if (denial?.Ok != false) return GamePasswords.Failure(profileId, "GamePasswordResponseRejected");
        // Only the actual pinned HTTP 403 and explicit negative response can
        // teach durable revocation. A code with lost transport/status context,
        // a generic denial or a malformed positive body cannot establish it.
        if (denial.Code == "Revoked")
        {
            if (statusCode != HttpStatusCode.Forbidden || config is null)
                return GamePasswords.Failure(profileId, "GamePasswordUnavailable");
            config.KnownRevoked = true;
            ApplyKnownRevocation();
            SaveConfig();
        }
        else if (denial.Code == "AccessExpired")
        {
            if (statusCode != HttpStatusCode.Forbidden) return GamePasswords.Failure(profileId, "GamePasswordUnavailable");
            ApplyAccessExpired();
        }
        return GamePasswords.Failure(profileId, denial.Code);
    }
}
