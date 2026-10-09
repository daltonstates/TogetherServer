using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;

namespace TogetherServer;

public sealed record GameClientLaunchResult(bool Ok, string Code, string Message);

internal interface IGameClientLaunchAdapter
{
    GameClientLaunchResult Prepare(string fixedUri, CancellationToken ct);
    GameClientLaunchResult Open(string fixedUri);
}

internal static class GameClientLaunch
{
    // Valve's Steamworks API overview documents steam://run/<AppID>.
    // No address, password, argument or Host-supplied string is included.
    internal static string? FixedUri(string kind) => kind switch
    {
        GameKinds.Valheim => "steam://run/892970",
        GameKinds.Factorio => "steam://run/427520",
        GameKinds.Terraria => "steam://run/105600",
        _ => null
    };
    internal static bool ValidAddress(string? address)
    {
        if (address is null || address.Length > 32) return false;
        var pieces = address.Split(':');
        return pieces.Length == 2 && IPAddress.TryParse(pieces[0], out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
            ip.ToString() == pieces[0] && pieces[1].All(char.IsAsciiDigit) && int.TryParse(pieces[1], out var port) && port is >= 1 and <= 65535;
    }
    internal static GameClientLaunchResult Open(PublicProfile profile, IGameClientLaunchAdapter adapter)
    {
        if (!ValidAddress(profile.JoinAddress)) return new(false, "GameAddressUnavailable", "The Host must share a valid game address before opening the game here.");
        if (FixedUri(profile.Kind) is not { } uri)
            return new(false, "ManualLaunchRequired", profile.Kind is GameKinds.MinecraftJava or GameKinds.MinecraftBedrock
                ? "Open Minecraft from Windows Start or your own launcher. Choose Multiplayer or Servers, then use Copy beside Server IP."
                : "Open your game yourself and use Copy beside Server IP.");
        try { return adapter.Open(uri); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        { return new(false, "GameLaunchFailed", "The game could not be opened. Open it in Steam yourself and copy the server address."); }
    }
}

internal sealed class WindowsSteamLaunchAdapter : IGameClientLaunchAdapter
{
    private string? preparedUri;
    public GameClientLaunchResult Prepare(string fixedUri, CancellationToken ct)
    {
        preparedUri = null;
        if (fixedUri is not ("steam://run/892970" or "steam://run/427520" or "steam://run/105600"))
            return new(false, "UnsupportedGame", "This game has no reviewed automatic launch mapping.");
        try
        {
            ct.ThrowIfCancellationRequested();
            using var handler = Registry.ClassesRoot.OpenSubKey("steam");
            if (handler?.GetValue("URL Protocol") is null)
                return new(false, "SteamHandlerUnavailable", "Steam's game-opening handler is unavailable. Open Steam yourself, then copy the server address.");
            if (!InstalledGameClient.HasInstalledSteamClient(fixedUri, ct))
                return new(false, "GameInstallationUnknown", "A completed Steam installation was not found safely. Open your installed game yourself, then copy the server address; TogetherServer will not request installation.");
            ct.ThrowIfCancellationRequested(); preparedUri = fixedUri;
            return new(true, "GameClientAvailable", "The reviewed installed Steam client may be opened.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        { return new(false, "GameInstallationUnknown", "The installed game could not be inspected safely. Open it yourself and copy the server address."); }
    }
    public GameClientLaunchResult Open(string fixedUri)
    {
        if (preparedUri != fixedUri || fixedUri is not ("steam://run/892970" or "steam://run/427520" or "steam://run/105600"))
            return new(false, "GameInstallationUnknown", "Review the installed game before opening it.");
        preparedUri = null;
        try
        {
            using var started = Process.Start(new ProcessStartInfo(fixedUri) { UseShellExecute = true });
            return new(true, "GameOpenRequested", "Asked Steam to open the game. Use Copy beside Server IP and join in the game; opening does not verify a join.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3 or 1155)
        { return new(false, "SteamHandlerUnavailable", "Steam's game-opening handler is unavailable. Open Steam yourself, then copy the server address."); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return new(false, "GameLaunchFailed", "The game could not be opened. Open it in Steam yourself and copy the server address."); }
    }
}
