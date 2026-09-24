using System.Net;
using System.Net.Sockets;

namespace TogetherServer;

public static class GameEndpointProbe
{
    public static GameEndpointProbeResult Check(PublicProfile profile)
    {
        var checkedUtc = DateTimeOffset.UtcNow;
        if (profile.Kind is not (GameKinds.Valheim or GameKinds.MinecraftJava or GameKinds.MinecraftBedrock))
            return new(false, "UnsupportedGameProbe",
                "A Friend-side game endpoint check is available only for built-in Valheim and Minecraft servers.", checkedUtc);
        if (!TryEndpoint(profile.JoinAddress, out var address, out var port))
            return new(false, "GameEndpointUnavailable", "The Host has not shared a valid game endpoint.", checkedUtc);
        try
        {
            GamePlayerCount? players;
            bool answered;
            if (profile.Kind == GameKinds.Valheim)
            {
                if (port == 65535) return new(false, "GameEndpointInvalid", "The Valheim query port is outside the valid range.", checkedUtc);
                var result = ValveServerQuery.Info(address, port + 1);
                answered = !result.NoReply && result.Players is not null;
                players = result.Players;
            }
            else
            {
                var result = profile.Kind == GameKinds.MinecraftJava
                    ? MinecraftStatusProbe.Java(address, port)
                    : MinecraftStatusProbe.Bedrock(address, port);
                answered = result is not null;
                players = result?.Players;
            }
            return answered
                ? new(true, "GameEndpointAnswered", "Game endpoint answered from this PC. This does not verify that a player joined.",
                    checkedUtc, players?.Online, players?.Capacity)
                : new(false, "GameEndpointNoReply", "The game query did not receive a valid reply from this PC. This does not prove the server is offline.", checkedUtc);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException)
        {
            return new(false, "GameEndpointNoReply", "The game query did not receive a valid reply from this PC. This does not prove the server is offline.", checkedUtc);
        }
    }

    private static bool TryEndpoint(string? value, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || !IPAddress.TryParse(value[..separator], out var parsed) ||
            parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !int.TryParse(value[(separator + 1)..], out port) || port is < 1 or > 65535) return false;
        address = parsed;
        return true;
    }
}
