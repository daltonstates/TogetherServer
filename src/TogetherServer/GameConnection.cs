using System.Net;
using System.Net.Sockets;

namespace TogetherServer;

public static class GameConnection
{
    public static bool IsPublicIpv4(string? value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var octets = address.GetAddressBytes();
        var a = octets[0];
        var b = octets[1];
        var c = octets[2];
        return a is >= 1 and < 224 &&
            a != 10 && a != 127 &&
            !(a == 100 && b is >= 64 and <= 127) &&
            !(a == 169 && b == 254) &&
            !(a == 172 && b is >= 16 and <= 31) &&
            !(a == 192 && b == 168) &&
            !(a == 192 && b == 0 && c is 0 or 2) &&
            !(a == 198 && (b == 18 || b == 19 || b == 51 && c == 100)) &&
            !(a == 203 && b == 0 && c == 113);
    }

    public static string? JoinAddress(ServerProfile profile, string? publicIp) =>
        profile.Kind == "Valheim" && IsPublicIpv4(publicIp)
            ? $"{IPAddress.Parse(publicIp!)}:{profile.GamePort}" : null;
}
