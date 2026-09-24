using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TogetherServer;

public sealed record CustomRemoteCertification(
    Guid ProfileId,
    string Fingerprint,
    int ContractVersion,
    DateTimeOffset CertifiedUtc,
    string WorldId,
    string WorkingDirectory,
    IReadOnlyList<GamePort> DeclaredPorts);

public sealed record CustomCertificationState(
    Guid ProfileId,
    string Stage,
    string Message,
    bool InProgress,
    bool Certified,
    DateTimeOffset? CertifiedUtc = null,
    int? OnlinePlayers = null,
    string? BlockReason = null);

public sealed record CustomCertificationResult(
    bool Ok,
    string Code,
    string Message,
    HostSnapshot Snapshot,
    CustomCertificationState Certification);

internal sealed class CustomCertificationSession
{
    public required Guid ProfileId { get; init; }
    public required string Fingerprint { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required string Stage { get; set; }
    public int? OnlinePlayers { get; set; }
    public string? Failure { get; set; }
}

internal static class CustomCertification
{
    public const int ContractVersion = 2;
    public const string StartingFirstRun = "StartingFirstRun";
    public const string AwaitingFirstJoin = "AwaitingFirstJoin";
    public const string AwaitingFirstLeave = "AwaitingFirstLeave";
    public const string ConfirmFirstChange = "ConfirmFirstChange";
    public const string AwaitingRestartReady = "AwaitingRestartReady";
    public const string AwaitingSecondJoin = "AwaitingSecondJoin";
    public const string ConfirmSecondChange = "ConfirmSecondChange";
    public const string AwaitingSecondLeave = "AwaitingSecondLeave";
    public const string Failed = "Failed";
    public const string Certified = "Certified";
    public const string NotCertified = "NotCertified";

    public static CustomRemoteCertification Create(ServerProfile profile, CustomScriptBundle scripts,
        IReadOnlyList<GamePort> ports, DateTimeOffset certifiedUtc) =>
        new(profile.Id, Fingerprint(profile, scripts, ports), ContractVersion, certifiedUtc,
            profile.WorldId, NormalizeDirectory(profile.WorldDirectory), NormalizePorts(ports));

    public static string Fingerprint(ServerProfile profile, CustomScriptBundle scripts,
        IReadOnlyList<GamePort> ports)
    {
        var canonical = new
        {
            contractVersion = ContractVersion,
            start = scripts.Start,
            status = scripts.Status,
            stop = scripts.Stop,
            worldId = profile.WorldId,
            workingDirectory = NormalizeDirectory(profile.WorldDirectory),
            declaredPorts = NormalizePorts(ports)
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(canonical, new JsonSerializerOptions(JsonSerializerDefaults.Web)))));
    }

    public static bool Matches(CustomRemoteCertification certification, ServerProfile profile,
        CustomScriptBundle scripts, IReadOnlyList<GamePort> ports) =>
        certification.ProfileId == profile.Id &&
        certification.ContractVersion == ContractVersion &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(certification.Fingerprint),
            Convert.FromHexString(Fingerprint(profile, scripts, ports)));

    public static string NormalizeDirectory(string directory) =>
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();

    private static IReadOnlyList<GamePort> NormalizePorts(IEnumerable<GamePort> ports) => ports
        .Select(port => new GamePort(port.Protocol.ToUpperInvariant(), port.Port, port.Label,
            port.Family))
        .OrderBy(port => port.Protocol, StringComparer.Ordinal)
        .ThenBy(port => port.Family, StringComparer.Ordinal)
        .ThenBy(port => port.Port)
        .ThenBy(port => port.Label, StringComparer.Ordinal)
        .ToList();

    public static CustomCertificationState State(CustomCertificationSession session)
    {
        var (message, inProgress) = session.Stage switch
        {
            StartingFirstRun => ("Starting the first live run. Check again when the server is ready and empty.", true),
            AwaitingFirstJoin => ("Ready with zero players. Join with a real game client and make a recognizable persistent change, then check again.", true),
            AwaitingFirstLeave => ("A real client was observed. Leave the server, wait for zero players, then check again.", true),
            ConfirmFirstChange => ("Zero players was observed after the join. Confirm that you made the recognizable persistent change; TogetherServer will safely stop and restart the same world.", true),
            AwaitingRestartReady => ("The exact wrapper stopped and the same profile restarted. Check again when it is ready.", true),
            AwaitingSecondJoin => ("The restarted server is ready and empty. Rejoin and verify that the recognizable change survived, then check again.", true),
            ConfirmSecondChange => ("A client rejoined. Confirm that the recognizable change survived the restart while the client is still online.", true),
            AwaitingSecondLeave => ("Save survival was confirmed. Leave the server, wait for zero players, then check again to finish certification.", true),
            Failed => (session.Failure ?? "Certification failed. Begin again after correcting the problem.", false),
            _ => ("Certification state is unknown. Cancel and begin again.", false)
        };
        return new(session.ProfileId, session.Stage, message, inProgress, false,
            OnlinePlayers: session.OnlinePlayers, BlockReason: session.Failure);
    }
}
