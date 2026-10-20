using System.Text.Json.Serialization;

namespace TogetherServer;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GamePasswordResult(bool Ok, string Code, string Message, Guid ProfileId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password = null,
    DateTimeOffset? GamePasswordChangedUtc = null)
{
    public override string ToString() => $"GamePasswordResult {{ Ok = {Ok}, Code = {Code}, ProfileId = {ProfileId} }}";
}

internal static class GamePasswords
{
    internal const int MaximumResponseBytes = 2048;
    internal static bool ValidPassword(string? value) => value is { Length: >= 5 and <= 64 } && !value.Any(char.IsControl);

    // A display hint over the fixed encrypted file, never a password hash or
    // authorization/readiness signal. Older saved passwords need no migration.
    internal static DateTimeOffset? ChangedUtc(LocalData data, Guid profileId)
    {
        try
        {
            var path = Path.Combine(data.RootPath, $"valheim-password-{profileId:N}.protected");
            if (!File.Exists(path)) return null;
            var changed = File.GetLastWriteTimeUtc(path);
            return changed.Year >= 1970 ? new DateTimeOffset(changed) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }

    internal static GamePasswordResult Read(LocalData data, Guid profileId)
    {
        if (data.LoadSettings().Profiles.SingleOrDefault(profile => profile.Id == profileId)?.Kind != GameKinds.Valheim)
            return Failure(profileId, "GamePasswordUnsupported");
        var saved = data.ReadValheimPasswordInfo(profileId);
        return ValidPassword(saved.Password)
            ? new(true, "GamePasswordRevealed", "Current game password read from the Host.", profileId, saved.Password, saved.ChangedUtc)
            : Failure(profileId, "GamePasswordUnavailable");
    }

    internal static GamePasswordResult Failure(Guid profileId, string? code)
    {
        if (code == "Expired") code = "CredentialExpired";
        var reviewed = code is "Revoked" or "AccessExpired" or "ApprovalPending" or "CredentialExpired" or
            "PermissionDenied" or "RateLimited" or "Unauthorized" or "NotPaired" or "ConnectionClosed" or
            "ConnectionChanged" or "GamePasswordUnsupported" or "GamePasswordUpdateRequired" or
            "GamePasswordUnavailable" or "GamePasswordResponseRejected" or "GamePasswordTimedOut" or
            "GamePasswordChanged" or "GamePasswordRequestRejected" or "GamePasswordOperationUnavailable"
            ? code : "GamePasswordUnavailable";
        var message = reviewed switch
        {
            "Revoked" => "The Host removed this PC's access.",
            "AccessExpired" => "The Host ended this PC's access. Ask the Host to extend or clear the deadline.",
            "ApprovalPending" => "The Host must approve this PC first.",
            "CredentialExpired" => "This PC's saved access expired. Connect with the current server code.",
            "PermissionDenied" => "The Host has not granted game password access for this server to this PC.",
            "GamePasswordUnsupported" => "Game password sharing is available for saved Valheim servers.",
            "GamePasswordUpdateRequired" => "Update both apps before sharing this game password.",
            "ConnectionChanged" => "The selected Host connection changed. Reveal the password again from its server card.",
            "GamePasswordChanged" => "The Host changed the password while it was being read. Reveal it again.",
            "GamePasswordRequestRejected" => "Choose only a saved Host connection and assigned server.",
            "GamePasswordOperationUnavailable" => "Finish the current app operation before revealing the password.",
            "GamePasswordTimedOut" => "The Host did not answer in time. Reveal the password again when connected.",
            "GamePasswordResponseRejected" => "The Host's password response could not be verified.",
            "RateLimited" => "Wait a moment before revealing the password again.",
            _ => "The current game password could not be read. Check the Host connection and try again."
        };
        return new(false, reviewed!, message, profileId);
    }
}
