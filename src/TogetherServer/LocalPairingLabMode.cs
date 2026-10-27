using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TogetherServer;

// Explicit disposable test mode. Unlike a hidden desktop launch, this mode
// never prepares a native console, window, tray, installer, or real game driver.
internal sealed class LocalPairingLabMode
{
    internal const string Argument = "--local-pairing-lab";
    internal string DataRoot { get; }
    internal string FixturePath { get; }
    internal string FixtureSha256 { get; }
    internal string? SelectedLanAddress => network.SelectedLanAddress;
    private readonly LocalPairingLabNetwork network;

    private LocalPairingLabMode(string dataRoot, string fixturePath, LocalPairingLabNetwork network)
    {
        DataRoot = dataRoot;
        FixturePath = fixturePath;
        this.network = network;
        RejectLinks(dataRoot);
        FixtureSha256 = HashFixture();
    }

    internal static LocalPairingLabMode? Resolve(string[] args, string? repository = null,
        Func<string, string?>? environment = null, Func<IEnumerable<LocalPairingLabInterface>>? interfaces = null)
    {
        if (!args.Contains(Argument, StringComparer.OrdinalIgnoreCase)) return null;
        environment ??= Environment.GetEnvironmentVariable;
        ValidateArguments(args);
        repository = Path.GetFullPath(repository ?? Environment.CurrentDirectory);
        var staging = environment("TOGETHERSERVER_STAGING_DATA_DIR");
        var unusedProduction = environment("TOGETHERSERVER_DATA_DIR");
        var fixtureRoot = environment("TOGETHERSERVER_FIXTURE_ROOT");
        var labRoot = Path.Combine(repository, "local-data", "local-pairing-lab");
        if (string.IsNullOrWhiteSpace(staging) || !Path.IsPathFullyQualified(staging) ||
            Path.GetFullPath(staging).Equals(labRoot, StringComparison.OrdinalIgnoreCase) ||
            !AppInstance.ContainsPath(labRoot, staging) ||
            string.IsNullOrWhiteSpace(fixtureRoot) || !Path.IsPathFullyQualified(fixtureRoot) ||
            !Path.GetFullPath(staging).Equals(Path.GetFullPath(fixtureRoot), StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(unusedProduction) || !Path.IsPathFullyQualified(unusedProduction) ||
            AppInstance.ContainsPath(staging, unusedProduction) || AppInstance.ContainsPath(unusedProduction, staging))
            throw new InvalidDataException("The pairing lab requires separate disposable folders under local-data/local-pairing-lab.");
        return new(Path.GetFullPath(staging), Path.Combine(repository, "src", "TogetherServer.Fixture", "bin",
            "Release", "net10.0", "TogetherServer.Fixture.exe"),
            LocalPairingLabNetwork.Resolve(environment(LocalPairingLabNetwork.LanAddressEnvironmentVariable), interfaces));
    }

    internal static void ValidateArguments(string[] args)
    {
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!flags.Add(arg)) throw new ArgumentException("Duplicate pairing lab arguments are not allowed.");
            if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length &&
                int.TryParse(args[++index], out var port) && port is >= 1024 and <= 65535) continue;
            if (arg.ToLowerInvariant() is Argument or "--staging" or "--host" or "--friend") continue;
            throw new ArgumentException("The pairing lab accepts only staging, Host/Friend mode and a local port; native commands are disabled.");
        }
        if (!flags.Contains(Argument) || !flags.Contains("--staging") || !flags.Contains("--port") ||
            flags.Contains("--host") == flags.Contains("--friend"))
            throw new ArgumentException("Choose --local-pairing-lab --staging, one Host/Friend mode, and --port.");
    }

    internal InstanceValidation ValidateSettings(HostSettings settings)
    {
        if (!IPAddress.TryParse(settings.CompanionBindAddress, out var bind) || !AllowsBind(bind) ||
            (!string.IsNullOrWhiteSpace(settings.CompanionEndpoint) && !IsAllowedEndpoint(settings.CompanionEndpoint)) ||
            settings.AutoShutdownEnabled || settings.KeepAwakeWhileHosting || settings.MaxConcurrentServers > 2 || settings.Profiles is null)
            return new(false, "LabSettingsOnly", "The pairing lab uses only its permitted local addresses, manual Stop and at most two synthetic runs.");
        foreach (var profile in settings.Profiles)
            if (ValidateProfile(profile) is { } failure)
                return new(false, failure.Code, failure.Message);
        return new(true, "LabSettings", "Disposable synthetic lab settings.");
    }

    internal void PrepareDataRoot()
    {
        RejectLinks(DataRoot);
        var marker = Path.Combine(DataRoot, ".local-pairing-lab.json");
        if (File.Exists(marker))
        {
            var saved = JsonSerializer.Deserialize<LabMarker>(File.ReadAllText(marker));
            if (saved is not { Schema: 2 } || saved.FixtureSha256 != FixtureSha256 || saved.LanAddress != SelectedLanAddress)
                throw new InvalidDataException("The lab marker, fixture or network choice changed; begin a fresh lab run.");
            return;
        }
        if (Directory.EnumerateFileSystemEntries(DataRoot).Any(path => Path.GetFileName(path) != ".togetherserver-instance.json"))
            throw new InvalidDataException("The pairing lab requires a fresh disposable data folder.");
        File.WriteAllText(marker, JsonSerializer.Serialize(new LabMarker(2, FixtureSha256, SelectedLanAddress)));
    }

    private sealed record LabMarker(int Schema, string FixtureSha256, string? LanAddress);

    private string HashFixture()
    {
        // The .exe is a framework-dependent apphost. Its managed assembly and
        // runtime/dependency manifests are part of the reviewed input too.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in new[] { FixturePath, Path.ChangeExtension(FixturePath, ".dll"),
            Path.ChangeExtension(FixturePath, ".deps.json"), Path.ChangeExtension(FixturePath, ".runtimeconfig.json") })
        {
            RejectLinks(file);
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal void SuppressNativeErrorDialogs()
    {
        if (OperatingSystem.IsWindows()) SetErrorMode(0x0001 | 0x0002 | 0x8000);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    internal GameValidation? ValidateProfile(ServerProfile profile)
    {
        try
        {
            if (profile.Kind != GameKinds.Fixture || !Path.IsPathFullyQualified(profile.WorldDirectory) ||
                !AppInstance.ContainsPath(DataRoot, profile.WorldDirectory) ||
                Path.GetFullPath(profile.WorldDirectory).Equals(DataRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.IsPathFullyQualified(profile.ExecutablePath) ||
                !Path.GetFullPath(profile.ExecutablePath).Equals(FixturePath, StringComparison.OrdinalIgnoreCase))
                return new("LabFixtureOnly", "The pairing lab accepts only its reviewed synthetic fixture and disposable saves.");
            RejectLinks(profile.WorldDirectory);
            RejectLinks(profile.ExecutablePath);
            if (!HashFixture().Equals(FixtureSha256, StringComparison.Ordinal))
                return new("LabFixtureChanged", "The synthetic fixture changed; build and begin a fresh lab run.");
            return null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new("LabFixtureUnavailable", "The disposable fixture or save path is unavailable."); }
    }

    internal bool AllowsBind(IPAddress address) => network.AllowsBind(address);
    internal static bool AllowsControlPolicy(HostControlPolicyChange change) =>
        change.AutoShutdownEnabled != true && change.KeepAwakeWhileHosting != true;
    internal bool IsAllowedEndpoint(string endpoint) => network.IsAllowedEndpoint(endpoint);
    internal HttpMessageHandler GuardClient(HttpMessageHandler inner) => network.GuardClient(inner);
    internal static bool IsLoopbackEndpoint(string endpoint) => LocalPairingLabNetwork.Resolve(null).IsAllowedEndpoint(endpoint);

    // Keep all shell, discovery, download, native-dialog, update, installer and
    // arbitrary endpoint recovery routes outside this deliberately small lab.
    internal static bool AllowsRequest(string method, string path)
    {
        if (method == "GET") return path is "/" or "/index.html" or "/favicon.svg" or
            "/api/local/snapshot" or "/api/local/instance" or "/api/local/data-recovery" or "/api/local/window" or
            "/api/local/companion" or "/api/local/friend" or "/api/local/friend/connections" or "/api/local/servers" or "/api/local/world-peers" or "/api/local/world-peers/work" or
            "/api/local/profile" or "/api/local/required-setup" or "/api/local/getting-started" or
            "/api/local/desktop/notifications" or "/api/local/desktop/preferences" or
            "/api/local/network/ports" or "/api/local/network/routes" or "/api/local/game-types" or
            "/api/local/update" or "/api/local/update/preparation" ||
            Regex.IsMatch(path, @"^/assets/[A-Za-z0-9_-]+\.(js|css)$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/api/local/friend/connections/[0-9a-fA-F-]{36}/servers/[0-9a-fA-F-]{36}/chat/summary$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/api/local/(profiles|friend)/[0-9a-fA-F-]{36}/(shared-world|chat|protection|world-peers)(/[^.]*)?$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/api/local/profiles/[0-9a-fA-F-]{36}/(backups|backup-catalog|live-save-acceptance)$", RegexOptions.CultureInvariant);
        if (method == "PUT") return path is "/api/local/settings" or "/api/local/settings/control-policy" or "/api/local/profile" or "/api/local/getting-started" or
            "/api/local/desktop/notifications" or "/api/local/ui-drafts" ||
            Regex.IsMatch(path, @"^/api/local/world-peers/[0-9a-fA-F]{64}/sharing$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/api/local/devices/[0-9a-fA-F-]{36}/(permissions|servers|access-expiry|shared-world/[0-9a-fA-F-]{36}(/grants)?)$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/api/local/(profiles|friend)/[0-9a-fA-F-]{36}/(protection|shared-world(/(consent|hosting))?|chat/(notice|members/[0-9a-fA-F-]{36}))$", RegexOptions.CultureInvariant);
        if (method == "DELETE") return Regex.IsMatch(path,
            @"^/api/local/devices/[0-9a-fA-F-]{36}/servers/[0-9a-fA-F-]{36}/access$", RegexOptions.CultureInvariant);
        if (method != "POST") return false;
        return path is "/api/local/friend/pair" or "/api/local/friend/poll" or "/api/local/quit" or
            "/api/local/required-setup/profile-name/complete" or "/api/local/ui-drafts/read" or "/api/local/ui-drafts/clear" ||
            Regex.IsMatch(path, @"^/api/local/(mode/(host|friend)|servers/[0-9a-fA-F-]{36}/invite(/current)?|devices/[0-9a-fA-F-]{36}/(approve|revoke)|friend/connections/[0-9a-fA-F-]{36}/select)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ||
            Regex.IsMatch(path, @"^/api/local/friend/connections/[0-9a-fA-F-]{36}/servers/[0-9a-fA-F-]{36}/chat/(sync|messages)$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/api/local/friend/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/shared-world/hosting/access/check\z", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/api/local/profiles/[0-9a-fA-F-]{36}/(start|stop|health|chat/messages|backups/manual|shared-world/hosting/start)$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/api/local/friend/[0-9a-fA-F-]{36}/(start|stop|chat/(sync|messages)|shared-world/(pull|check|hosting(/(prepare|start))?|recovery/separate(/(restore|finish|start))?)|game-password/reveal)$", RegexOptions.CultureInvariant) ||
            // The lost-response cleanup journey prepares the original owner's
            // fresh continuation before using the ordinary guarded profile Start.
            Regex.IsMatch(path, @"^/api/local/world-peers/[0-9a-fA-F]{64}/owner-recovery(/(restore|finish))?$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(path, @"^/api/local/world-peers/[0-9a-fA-F]{64}/(sync|connect)$", RegexOptions.CultureInvariant);
    }

    internal GameServerRegistry Registry() => new([new LabFixtureDriver(this)], PortProbeMode.LoopbackOnly);

    internal static HttpClient OfflineClient(TimeSpan timeout) => new(new OfflineHandler()) { Timeout = timeout };
    internal static HttpMessageHandler GuardLoopback(HttpMessageHandler inner) => LocalPairingLabNetwork.Resolve(null).GuardClient(inner);
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Internet services are disabled in the disposable pairing lab."));
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The pairing lab cannot use linked folders or files.");
    }

    private sealed class LabFixtureDriver(LocalPairingLabMode lab) : IGameServerDriver
    {
        private readonly FixtureServerDriver inner = new();
        public string Kind => inner.Kind;
        public string DisplayName => inner.DisplayName;
        public bool ShowPortDiagnostics => false;
        public bool SupportsCrashRecovery => false;
        public bool SupportsBackups => true;
        public string? ManagedSaveDirectory(ServerProfile profile) => inner.ManagedSaveDirectory(profile);
        public string ManagedExecutablePath(ServerProfile profile) => inner.ManagedExecutablePath(profile);
        public IReadOnlyList<GamePort> Ports(ServerProfile profile) => inner.Ports(profile);
        public string? JoinAddress(ServerProfile profile, string? publicIp) => null;
        public GameValidation? ValidateForStart(ServerProfile profile) => lab.ValidateProfile(profile) ?? inner.ValidateForStart(profile);
        public void PrepareStart(ServerProfile profile, ManagedRun run)
        {
            if (ValidateForStart(profile) is { } failure) throw new InvalidOperationException(failure.Message);
            inner.PrepareStart(profile, run);
        }
        public GameLaunchResult Start(ServerProfile profile, ManagedRun run)
        {
            if (ValidateForStart(profile) is { } failure) throw new InvalidOperationException(failure.Message);
            return inner.Start(profile, run);
        }
        public GameHealthResult Health(ManagedRun run) => inner.Health(run);
        public Task<GameStopResult> StopAsync(Process process, ManagedRun run) => inner.StopAsync(process, run);
    }
}
