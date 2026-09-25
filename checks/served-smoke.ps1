param([string]$AppPath = '')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release/TogetherServer.exe' }
$appPath = $AppPath
$fixturePath = Join-Path $repository 'src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe'
$valheimFixturePath = Join-Path $repository 'src/TogetherServer.ValheimFixture/bin/Release/net10.0/valheim_server.exe'
if (!(Test-Path -LiteralPath $appPath) -or !(Test-Path -LiteralPath $fixturePath) -or !(Test-Path -LiteralPath $valheimFixturePath)) {
    throw 'Run scripts/build.ps1 first.'
}

$caseRoot = Join-Path $repository ('local-data/served-smoke/' + [guid]::NewGuid().ToString('N'))
$worldDirectory = Join-Path $caseRoot 'disposable-world'
New-Item -ItemType Directory -Path $worldDirectory -Force | Out-Null
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$probe.Start()
$port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port
$probe.Stop()
function Get-FreeUdpPair {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $candidate = Get-Random -Minimum 35000 -Maximum 45000
        $one = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::InterNetwork,
            [Net.Sockets.SocketType]::Dgram, [Net.Sockets.ProtocolType]::Udp)
        $two = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::InterNetwork,
            [Net.Sockets.SocketType]::Dgram, [Net.Sockets.ProtocolType]::Udp)
        try {
            $one.ExclusiveAddressUse = $true
            $two.ExclusiveAddressUse = $true
            $one.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, $candidate))
            $two.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, $candidate + 1))
            return $candidate
        }
        catch [Net.Sockets.SocketException] { }
        finally { $one.Dispose(); $two.Dispose() }
    }
    throw 'Could not reserve a free UDP port pair for the served smoke.'
}
$gamePort = Get-FreeUdpPair
$baseUrl = "http://127.0.0.1:$port"
$profileId = [guid]::NewGuid().ToString()
$headers = @{ Origin = $baseUrl; 'X-TogetherServer-Local' = '1' }
$oldDataDirectory = $env:TOGETHERSERVER_DATA_DIR
$oldFixtureRoot = $env:TOGETHERSERVER_FIXTURE_ROOT
$oldFixtureDriver = $env:TOGETHERSERVER_ENABLE_FIXTURE_DRIVER
$env:TOGETHERSERVER_DATA_DIR = $caseRoot
$env:TOGETHERSERVER_FIXTURE_ROOT = $caseRoot
$env:TOGETHERSERVER_ENABLE_FIXTURE_DRIVER = '1'
$appProcess = $null
$fixtureStarted = $false
$valheimStarted = $false
$valheimRun = $null
try {
    $appProcess = Start-Process -FilePath $appPath -ArgumentList @('--host', '--port', $port) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $caseRoot 'stdout.txt') -RedirectStandardError (Join-Path $caseRoot 'stderr.txt')
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ($appProcess.HasExited) { throw 'The local GUI exited during startup.' }
        try {
            $page = Invoke-WebRequest -Uri "$baseUrl/" -UseBasicParsing -TimeoutSec 2
            $ready = $page.StatusCode -eq 200
            if ($ready) { break }
        }
        catch {
            if ($appProcess.HasExited) { throw 'The local GUI exited during startup.' }
            Start-Sleep -Milliseconds 100
        }
    }
    if (!$ready) { throw 'The local GUI did not start.' }
    $jsMatch = [regex]::Match($page.Content, '/assets/[^" ]+\.js')
    $cssMatch = [regex]::Match($page.Content, '/assets/[^" ]+\.css')
    if (!$jsMatch.Success -or !$cssMatch.Success) {
        throw 'The served page did not reference bundled React assets.'
    }
    $js = Invoke-WebRequest -Uri ($baseUrl + $jsMatch.Value) -UseBasicParsing
    if ($js.StatusCode -ne 200 -or $js.RawContentLength -lt 10000) { throw 'The embedded JavaScript was not served.' }
    $css = Invoke-WebRequest -Uri ($baseUrl + $cssMatch.Value) -UseBasicParsing
    if ($css.StatusCode -ne 200 -or $css.RawContentLength -lt 1000) { throw 'The embedded CSS was not served.' }
    if (!$css.Content.Contains('.ui-button')) { throw 'The shared control-library styles were not bundled.' }
    $rawControls = Get-ChildItem -LiteralPath (Join-Path $repository 'ui/src') -Filter '*.tsx' |
        Where-Object Name -NE 'Controls.tsx' |
        Select-String -CaseSensitive -Pattern '<(button|input|select|textarea)\b'
    if ($rawControls) { throw "A page bypasses the shared control library: $($rawControls[0].Path):$($rawControls[0].LineNumber)" }
    $requiredUiText = @(
        'Host a server', 'Join a server', 'Choose a game', 'Create new', 'Use existing',
        'Browse for a world folder', 'Use an existing server', 'TogetherServer installs the latest official server',
        'Servers found on this PC', 'Finish later', 'Continue server setup', 'Save and start',
        'Start server', 'Invite friends',
        'Paste your server code', 'Saved servers',
        'Friend access and settings', 'PC name', 'Server access', 'Choose servers', 'Search servers',
        'Select all', 'Clear all', 'Save access', 'Start servers', 'Request Stop', 'On with server exceptions',
        'Allow remote Start and Stop', 'Stop & timer',
        'Connection help', 'Advanced network and game paths', 'Technical details',
        'Game server', 'Friend app', 'Outside connection', 'Reachable outside network', 'Recommended next step',
        'Connection details', 'Hidden for stream safety', 'Server IP', 'Game password',
        'Use an eye to show only that value', 'Copy keeps it hidden', 'Notifications', 'Recent app and connection activity',
        'Refreshing connection details', 'Connection details updated.',
        'Maximum servers running at once', 'Duplicate saved game port', 'Stop empty server and start this one',
        'Pairing window options', 'Close pairing', 'Emergency-revoke code credentials',
        'Require local Host approval for each new PC', 'Saved connection name', 'Forget this Host',
        'Maintenance mode', 'Extend empty-server timer', 'Friend extension increment',
        'Empty-server countdown', 'Stop empty servers automatically',
        'Wait after the server reaches 0 players', 'Stops in', 'Timer not running', 'Extend this countdown',
        'Retry player count', 'Refresh player count',
        'Extra minutes for this countdown only.', 'Friend apps do not gate the timer', 'Remote Stop safety', 'There are no player IDs to enter',
        'Custom game', 'local PowerShell actions', 'Status and players script',
        'contract v2 echoes plus the guided live certification are required', 'TogetherServer never force-kills the game.',
        'steam://install/896660'
    )
    foreach ($expectedText in $requiredUiText) {
        if (!$js.Content.Contains($expectedText)) {
            throw "The published GUI is missing expected guided-flow text: $expectedText"
        }
    }
    if (!$css.Content.Contains('.server-picker-list{') -or !$css.Content.Contains('max-height:min(420px,45vh)')) {
        throw 'The bounded server assignment picker styles were not bundled.'
    }
    if (!$css.Content.Contains('.idle-countdown{') -or !$css.Content.Contains('font-variant-numeric:tabular-nums')) {
        throw 'The shared empty-server countdown styles were not bundled.'
    }
    if (!$css.Content.Contains('.player-count-refresh.ui-button{')) {
        throw 'The compact player-count refresh control styles were not bundled.'
    }
    if (!$css.Content.Contains('.technical-details-grid{') -or !$css.Content.Contains('.technical-detail-card{')) {
        throw 'The grouped Host technical-detail styles were not bundled.'
    }
    if (!$css.Content.Contains('.connection-details-card{') -or !$css.Content.Contains('.notification-badge{') -or
        !$css.Content.Contains('@keyframes icon-spin')) {
        throw 'The private connection card, notification badge, or loading spinner styles were not bundled.'
    }
    if ($css.Content -notmatch '\.join-row\{[^}]*align-items:flex-end' -or
        $css.Content -notmatch '\.friend-panel \.join-row \.invite-input\{[^}]*margin-bottom:0' -or
        $css.Content -notmatch '\.notification-item>span\{[^}]*background:var\(--color-accent\);[^}]*color:var\(--color-accent-ink\)') {
        throw 'The aligned Connect row or orange notification icon styles were not bundled.'
    }
    $positivePalette = @(
        '--color-success:var(--color-accent-hover)',
        '--color-success-bright:var(--color-accent-light)',
        '--color-success-text:var(--color-accent-text)',
        '--color-success-bg:var(--color-accent-soft)',
        '--color-success-bg-strong:var(--color-accent-soft-strong)',
        '--color-success-border:var(--color-accent-border)',
        '--color-success-ring:var(--color-focus-ring)'
    )
    foreach ($token in $positivePalette) {
        if (!$css.Content.Contains($token)) {
            throw "Positive status colors are not using the orange product palette: $token"
        }
    }
    $greenDominantColors = @([regex]::Matches($css.Content, '#(?<rgb>[0-9a-fA-F]{6})(?:[0-9a-fA-F]{2})?\b') |
        ForEach-Object {
            $rgb = $_.Groups['rgb'].Value
            $red = [Convert]::ToInt32($rgb.Substring(0, 2), 16)
            $green = [Convert]::ToInt32($rgb.Substring(2, 2), 16)
            $blue = [Convert]::ToInt32($rgb.Substring(4, 2), 16)
            if (($green - $red) -ge 12 -and ($green - $blue) -ge 12) { "#$rgb" }
        } | Sort-Object -Unique)
    if ($greenDominantColors.Count -gt 0 -or $css.Content -match '(?i)\b(?:green|lime|emerald|teal|olive|chartreuse|seafoam|mint)\b') {
        throw "The bundled product palette contains green: $($greenDominantColors -join ', ')"
    }
    if (!$css.Content.Contains('.custom-script-manager{') -or !$css.Content.Contains('.custom-port-row{')) {
        throw 'The custom game script-manager or port-editor styles were not bundled.'
    }
    if ($js.Content.Contains('Servers this PC can control')) { throw 'The unbounded inline server checklist is still bundled.' }
    if ($js.Content.Contains('Public IPv4 address for Valheim')) { throw 'The old manual game IP field is still bundled.' }
    if ($js.Content.Contains('Game activity check') -or $js.Content.Contains('Browse for game')) {
        throw 'The removed game-running controls are still bundled.'
    }
    Write-Host 'PASS standalone EXE, published HTML, embedded React JS, and CSS over loopback'

    $discovery = Invoke-RestMethod -Uri "$baseUrl/api/local/valheim/discover"
    if ($null -eq $discovery.installations -or $null -eq $discovery.worlds -or $null -ne $discovery.clients) { throw 'Valheim discovery route returned the wrong result shape.' }
    Write-Host 'PASS loopback-only Valheim discovery route and bundled setup controls'
    $minecraftDiscovery = Invoke-RestMethod -Uri "$baseUrl/api/local/minecraft/discover"
    if ($null -eq $minecraftDiscovery.installations -or $null -eq $minecraftDiscovery.javaRuntimePath) { throw 'Minecraft discovery route returned no result shape.' }
    $installBody = '{"kind":"MinecraftJava","worldName":"world","gamePort":25565,"acceptedTerms":false}'
    $installForbidden = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/minecraft/install" -Method Post -ContentType 'application/json' -Body $installBody -UseBasicParsing | Out-Null }
    catch { $installForbidden = [int]$_.Exception.Response.StatusCode -eq 403 }
    if (!$installForbidden) { throw 'Minecraft install bypassed the local mutation gate.' }
    $installDenied = Invoke-RestMethod -Uri "$baseUrl/api/local/minecraft/install" -Method Post -Headers $headers -ContentType 'application/json' -Body $installBody
    if ($installDenied.ok -or $installDenied.code -ne 'TermsRequired' -or (Test-Path -LiteralPath (Join-Path $caseRoot 'minecraft-servers'))) { throw 'Minecraft install ran without consent.' }
    Write-Host 'PASS Minecraft discovery and in-app install consent gate without a game download'
    $gameTypes = @(Invoke-RestMethod -Uri "$baseUrl/api/local/game-types")
    if (@($gameTypes.kind | Sort-Object) -join ',' -ne 'Custom,Fixture,MinecraftBedrock,MinecraftJava,Valheim') { throw 'Registered game drivers were not exposed distinctly.' }
    Write-Host 'PASS explicit game-driver catalog exposes the custom script manager and reviewed built-in drivers'

    $customId = [guid]::NewGuid().ToString()
    $customDirectory = Join-Path $caseRoot 'custom-game'
    New-Item -ItemType Directory -Path $customDirectory -Force | Out-Null
    $customProfile = @{
        id = $customId; kind = 'Custom'; name = 'HTTP custom game'; serverName = 'HTTP custom game';
        worldId = 'http-custom'; worldSource = 'Existing'; worldDirectory = $customDirectory;
        gamePort = ($gamePort + 40); executablePath = '';
        custom = @{ gameName = 'Synthetic custom game'; primaryProtocol = 'UDP'; shareJoinAddress = $false;
            additionalPorts = @(@{ protocol = 'TCP'; port = ($gamePort + 41); label = 'Query'; family = 'Any' }) }
    }
    $customSettings = @{ maxConcurrentServers = 1; idleMinutes = 1; autoShutdownEnabled = $true; profiles = @($customProfile) }
    $customSaved = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($customSettings | ConvertTo-Json -Depth 8)
    if (!$customSaved.ok) { throw "Custom settings rejected: $($customSaved.message)" }
    $startScript = @'
$stop = Join-Path $env:TOGETHERSERVER_WORKING_DIRECTORY 'stop.signal'
Remove-Item -LiteralPath $stop -Force -ErrorAction SilentlyContinue
while (-not (Test-Path -LiteralPath $stop)) { Start-Sleep -Milliseconds 100 }
'@
    $statusScript = '@{ state = ''Ready''; detail = ''Packaged custom status''; onlinePlayers = 0; maxPlayers = 4; players = @(''Alice'') } | ConvertTo-Json -Compress'
    $stopScript = '$stop = Join-Path $env:TOGETHERSERVER_WORKING_DIRECTORY ''stop.signal''; New-Item -ItemType File -Path $stop -Force | Out-Null'
    $customScriptsBody = @{ start = $startScript; status = $statusScript; stop = $stopScript } | ConvertTo-Json
    $customScriptsForbidden = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/profiles/$customId/custom-scripts" -Method Put -ContentType 'application/json' -Body $customScriptsBody -UseBasicParsing | Out-Null }
    catch { $customScriptsForbidden = [int]$_.Exception.Response.StatusCode -eq 403 }
    if (!$customScriptsForbidden) { throw 'Custom scripts bypassed the local mutation gate.' }
    $customScriptsSaved = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$customId/custom-scripts" -Method Put -Headers $headers -ContentType 'application/json' -Body $customScriptsBody
    if (!$customScriptsSaved.ok -or (Get-Content (Join-Path $caseRoot 'host.json') -Raw).Contains('Packaged custom status')) { throw 'Custom scripts were rejected or stored in plaintext settings.' }
    $customRevealForbidden = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/profiles/$customId/custom-scripts/reveal" -Method Post -UseBasicParsing | Out-Null }
    catch { $customRevealForbidden = [int]$_.Exception.Response.StatusCode -eq 403 }
    if (!$customRevealForbidden) { throw 'Custom scripts were revealed without local mutation headers.' }
    $revealedScripts = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$customId/custom-scripts/reveal" -Method Post -Headers $headers
    if (!$revealedScripts.ok -or !$revealedScripts.scripts.status.Contains('Packaged custom status')) { throw 'Host could not reveal its protected custom scripts.' }
    $customStarted = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$customId/start" -Method Post -Headers $headers
    if (!$customStarted.ok -or $customStarted.code -ne 'CustomStarting') { throw "Custom Start failed: $($customStarted.message)" }
    $customReady = $null
    for ($i = 0; $i -lt 50; $i++) {
        $customReady = (Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot").runs | Where-Object profileId -EQ $customId
        if ($customReady.state -eq 'Ready') { break }
        Start-Sleep -Milliseconds 100
    }
    if ($customReady.state -ne 'Ready' -or $customReady.onlinePlayers -ne 0 -or
        @($customReady.playerNames).Count -ne 1 -or $customReady.playerCountTrusted -ne $false -or
        !$customReady.autoShutdownReason.Contains('certification')) { throw 'Packaged uncertified custom status did not remain display-only and fail closed.' }
    $customStopped = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$customId/stop" -Method Post -Headers $headers
    if (!$customStopped.ok -or $customStopped.code -ne 'CustomStopped') { throw "Custom Stop failed: $($customStopped.message)" }
    $customRemoved = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body (@{ profiles = @() } | ConvertTo-Json)
    if (!$customRemoved.ok -or (Test-Path -LiteralPath (Join-Path $caseRoot ("custom-scripts-" + $customId.Replace('-', '') + '.protected')))) { throw 'Removing a custom profile retained its protected scripts.' }
    Write-Host 'PASS packaged custom Start, status/player list, local Stop, protected storage, and fail-closed safety'

    $forbidden = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/mode/friend" -Method Post -UseBasicParsing | Out-Null }
    catch { $forbidden = [int]$_.Exception.Response.StatusCode -eq 403 }
    if (!$forbidden) { throw 'A mutation without the local request headers was allowed.' }
    Write-Host 'PASS local mutation gate'
    if (!$js.Content.Contains('Update and restart') -or !$js.Content.Contains('Check for updates')) { throw 'The update controls were not bundled.' }
    $updateForbidden = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/update/install" -Method Post -UseBasicParsing | Out-Null }
    catch { $updateForbidden = [int]$_.Exception.Response.StatusCode -eq 403 }
    if (!$updateForbidden) { throw 'The update action bypassed the local mutation gate.' }
    $headlessUpdate = Invoke-RestMethod -Uri "$baseUrl/api/local/update/install" -Method Post -Headers $headers
    if ($headlessUpdate.ok -or $headlessUpdate.code -ne 'WindowUnavailable') { throw 'A headless app was allowed to update its EXE.' }
    Write-Host 'PASS bundled update controls and local-only desktop update action'

    $desktopPreferences = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/preferences"
    if ($desktopPreferences.available -or $desktopPreferences.closeToTray) { throw 'Headless desktop preferences reported a visible app or default tray opt-in.' }
    $preferenceForbidden = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/desktop/preferences" -Method Put -ContentType 'application/json' -Body '{"closeToTray":true}' -UseBasicParsing | Out-Null }
    catch { $preferenceForbidden = [int]$_.Exception.Response.StatusCode -eq 403 }
    if (!$preferenceForbidden) { throw 'Desktop preferences bypassed the local mutation gate.' }
    $headlessPreference = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/preferences" -Method Put -Headers $headers -ContentType 'application/json' -Body '{"launchAtLogin":true}'
    if ($headlessPreference.ok -or $headlessPreference.code -ne 'WindowUnavailable') { throw 'A headless process changed Windows startup.' }
    Write-Host 'PASS desktop preferences require the local desktop app and mutation gate'

    $incomplete = @{ id = [guid]::NewGuid().ToString(); kind = 'Valheim'; name = 'Needs setup'; serverName = 'Needs setup'; worldId = 'V1release'; worldSource = 'Existing'; worldDirectory = ''; gamePort = $gamePort; executablePath = '' }
    $incompleteSettings = @{ maxConcurrentServers = 1; idleMinutes = 15; profiles = @($incomplete) }
    $missingWorld = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($incompleteSettings | ConvertTo-Json -Depth 8)
    if ($missingWorld.ok -or $missingWorld.code -ne 'InvalidSettings' -or !$missingWorld.message.Contains('copy an existing world')) { throw 'Incomplete world setup was not explained.' }
    $incomplete.worldDirectory = $worldDirectory
    $missingServer = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($incompleteSettings | ConvertTo-Json -Depth 8)
    if ($missingServer.ok -or $missingServer.code -ne 'InvalidSettings' -or !$missingServer.message.Contains('Select an installed server')) { throw 'Missing server executable was not explained.' }
    if (@((Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot").settings.profiles).Count -ne 0) { throw 'Incomplete setup was saved.' }
    Write-Host 'PASS incomplete setup names the missing world or server without saving'

    $incompleteSettings.publicGameIp = '127.0.0.1'
    $badGameIp = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($incompleteSettings | ConvertTo-Json -Depth 8)
    if ($badGameIp.ok -or !$badGameIp.message.Contains('public IPv4')) { throw 'Loopback was accepted as a public Friend game address.' }
    $incompleteSettings.Remove('publicGameIp')
    Write-Host 'PASS loopback cannot be saved as a public Friend game address'

    $profile = @{ id = $profileId; name = 'HTTP fixture'; worldId = 'http-smoke'; worldDirectory = $worldDirectory; gamePort = $gamePort; executablePath = $fixturePath }
    $settings = @{ maxConcurrentServers = 1; idleMinutes = 15; autoShutdownEnabled = $false; remoteControlsEnabled = $false; publicGameIp = '1.2.3.4'; publicGameIpCheckedUtc = (Get-Date).ToUniversalTime().ToString('o'); profiles = @($profile) }
    $saved = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($settings | ConvertTo-Json -Depth 8)
    if (!$saved.ok) { throw "Settings rejected: $($saved.message)" }
    if ((Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot").settings.publicGameIp -ne '1.2.3.4') { throw 'Friend game address was not saved.' }
    $invite = Invoke-RestMethod -Uri "$baseUrl/api/local/servers/$profileId/invite" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"refresh":false,"canStart":true,"enableConnections":false}'
    $sameInvite = Invoke-RestMethod -Uri "$baseUrl/api/local/servers/$profileId/invite/current" -Method Post -Headers $headers
    $inviteReadForbidden = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/servers/$profileId/invite/current" -Method Post -UseBasicParsing | Out-Null }
    catch { $inviteReadForbidden = [int]$_.Exception.Response.StatusCode -eq 403 }
    $invitedSettings = (Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot").settings
    if (!$invite.ok -or !$invite.password.StartsWith('TS3-') -or !$sameInvite.exists -or !$sameInvite.open -or $sameInvite.password -ne $invite.password -or !$sameInvite.canStart -or $sameInvite.durationMinutes -ne 30 -or $sameInvite.deviceLimit -ne 1 -or !$inviteReadForbidden -or $invitedSettings.companionEndpoint -ne 'https://1.2.3.4:5131' -or $invitedSettings.companionBindAddress -ne '0.0.0.0' -or $invitedSettings.companionListeningEnabled) { throw 'Creating a pairing window did not keep one protected bounded code and permission default or prepare the standard Host address safely.' }
    $settings.companionEndpoint = $invitedSettings.companionEndpoint
    $settings.companionBindAddress = $invitedSettings.companionBindAddress
    $settings.companionPort = $invitedSettings.companionPort
    Write-Host 'PASS one current bounded server code prepares the standard app address without opening a listener'
    $started = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/start" -Method Post -Headers $headers
    if (!$started.ok -or $started.code -ne 'FixtureStarted') { throw "Start failed: $($started.message)" }
    $fixtureStarted = $true
    $health = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/health" -Method Post -Headers $headers
    if (!$health.ok -or $health.code -ne 'FixtureProcessRunning') { throw "Health failed: $($health.message)" }
    $friendView = Invoke-RestMethod -Uri "$baseUrl/api/local/mode/friend" -Method Post -Headers $headers
    if (!$friendView.ok -or (Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot").mode -ne 'Friend') { throw 'Joining another Host view stopped a managed server.' }
    $quitBlocked = Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers
    if ($quitBlocked.code -ne 'ManagedRunPresent') { throw 'Friend view let the app quit while its server was running.' }
    $hostView = Invoke-RestMethod -Uri "$baseUrl/api/local/mode/host" -Method Post -Headers $headers
    if (!$hostView.ok) { throw 'Could not return to the Host dashboard while its server was running.' }
    $stopped = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/stop" -Method Post -Headers $headers
    if (!$stopped.ok) { throw "Stop failed: $($stopped.message)" }
    $fixtureStarted = $false
    Write-Host 'PASS served settings, Start, Health, Stop, and simultaneous Host/Friend views'

    $valheimId = [guid]::NewGuid().ToString()
    $valheimProfile = @{ id = $valheimId; kind = 'Valheim'; name = 'Synthetic Valheim settings'; serverName = 'Fixture Valheim'; worldId = 'not-started'; worldDirectory = $worldDirectory; gamePort = ($gamePort + 10); executablePath = $valheimFixturePath }
    $settings.profiles = @($profile, $valheimProfile)
    $saved = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($settings | ConvertTo-Json -Depth 8)
    if (!$saved.ok) { throw "Valheim settings rejected: $($saved.message)" }
    $password = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/password" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"password":"fixture-pass-123"}'
    if (!$password.ok -or !$password.snapshot.passwordConfigured.$valheimId) { throw 'Protected Valheim password route failed.' }
    if ((Get-Content (Join-Path $caseRoot 'host.json') -Raw).Contains('fixture-pass-123')) { throw 'Valheim password leaked into Host settings.' }
    $copyWithoutLocalHeaders = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/profiles/$valheimId/game-password/reveal" -Method Post -UseBasicParsing | Out-Null }
    catch { $copyWithoutLocalHeaders = [int]$_.Exception.Response.StatusCode -eq 403 }
    if (!$copyWithoutLocalHeaders) { throw 'Game password was revealed without local mutation headers.' }
    $copiedPassword = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/game-password/reveal" -Method Post -Headers $headers
    if (!$copiedPassword.ok -or $copiedPassword.password -ne 'fixture-pass-123') { throw 'Host could not copy its protected game password.' }
    Write-Host 'PASS Host-only game password copy route and local request gate (synthetic secret)'
    $missing = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/start" -Method Post -Headers $headers
    if ($missing.code -ne 'MissingWorldData') { throw 'Missing world data did not block synthetic Valheim launch.' }
    $sourceSave = Join-Path $caseRoot 'source-save'
    $sourceWorlds = Join-Path $sourceSave 'worlds_local'
    New-Item -ItemType Directory -Path $sourceWorlds -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $sourceWorlds 'fixture-world.db') -Value 'synthetic database'
    Set-Content -LiteralPath (Join-Path $sourceWorlds 'fixture-world.fwl') -Value 'synthetic metadata'
    $importBody = @{ profileId = $valheimId; sourceSaveRoot = $sourceSave; worldId = 'fixture-world' } | ConvertTo-Json
    $imported = Invoke-RestMethod -Uri "$baseUrl/api/local/valheim/import" -Method Post -Headers $headers -ContentType 'application/json' -Body $importBody
    if (!$imported.ok -or !(Test-Path -LiteralPath (Join-Path $imported.worldDirectory 'worlds_local/fixture-world.db'))) { throw 'Local world import failed.' }
    if ((Get-Content -LiteralPath (Join-Path $sourceWorlds 'fixture-world.db') -Raw).Trim() -ne 'synthetic database') { throw 'Source save changed during import.' }
    $repeat = Invoke-RestMethod -Uri "$baseUrl/api/local/valheim/import" -Method Post -Headers $headers -ContentType 'application/json' -Body $importBody
    if ($repeat.code -ne 'AlreadyImported') { throw 'Import overwrote an existing copy.' }
    $cloudRoot = Join-Path $caseRoot 'Steam/userdata/synthetic/892970/remote'
    $cloudWorld = Join-Path $cloudRoot 'worlds/chunked-http'
    New-Item -ItemType Directory -Path $cloudWorld -Force | Out-Null
    foreach ($file in @('_main.7.db2', '_main.7.fwl2', '_main.7.chunks', '_main.7.ok', 'terrain.7.chunk')) {
        Set-Content -LiteralPath (Join-Path $cloudWorld $file) -Value "synthetic $file"
    }
    $cloudImport = Invoke-RestMethod -Uri "$baseUrl/api/local/valheim/import" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{
        profileId = [guid]::NewGuid().ToString(); sourceSaveRoot = $cloudRoot; worldId = 'chunked-http'; sourceFolder = 'worlds'
    } | ConvertTo-Json)
    if (!$cloudImport.ok -or !(Test-Path -LiteralPath (Join-Path $cloudImport.worldDirectory 'worlds_local/chunked-http/terrain.7.chunk')) -or
        !(Test-Path -LiteralPath (Join-Path $cloudWorld '_main.7.db2'))) { throw 'Synthetic cloud folder HTTP import failed or changed its source.' }
    Write-Host 'PASS served Valheim profile and protected password endpoint (synthetic settings)'
    Write-Host 'PASS served missing-save guard and copy import without source mutation (synthetic)'
    Write-Host 'PASS served synthetic Steam cloud folder import contract without source mutation'

    $valheimProfile.worldId = 'fixture-world'
    $valheimProfile.serverName = 'Fixture "Valheim"'
    $valheimProfile.worldDirectory = $imported.worldDirectory
    $settings.autoShutdownEnabled = $true
    $saved = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($settings | ConvertTo-Json -Depth 8)
    if (!$saved.ok) { throw 'Imported synthetic Valheim profile was rejected.' }
    $valheimStart = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/start" -Method Post -Headers $headers
    if (!$valheimStart.ok -or $valheimStart.code -ne 'ValheimStarting') { throw "Synthetic Valheim launch failed: $($valheimStart.message)" }
    $valheimStarted = $true
    $valheimRun = (Get-Content (Join-Path $caseRoot 'runs.json') -Raw | ConvertFrom-Json) | Where-Object profileId -EQ $valheimId
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        $health = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/health" -Method Post -Headers $headers
        if ($health.code -eq 'ValheimLogReady') { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Published EXE did not see the synthetic server-connected log.' }
    $valheimView = $null
    for ($i = 0; $i -lt 50; $i++) {
        $valheimSnapshot = Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot"
        $valheimView = @($valheimSnapshot.runs) | Where-Object profileId -EQ $valheimId
        if ($valheimView.onlinePlayers -eq 0 -and $valheimView.maxPlayers -eq 10 -and $null -ne $valheimView.autoShutdownAtUtc) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($valheimView.onlinePlayers -ne 0 -or $valheimView.maxPlayers -ne 10 -or $null -eq $valheimView.autoShutdownAtUtc) {
        throw 'Published EXE did not expose the synthetic Valheim 0 of 10 player count and shutdown deadline.'
    }
    $refreshedPlayers = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/players/refresh" -Method Post -Headers $headers
    $refreshedPlayerView = @($refreshedPlayers.snapshot.runs) | Where-Object profileId -EQ $valheimId
    if (!$refreshedPlayers.ok -or $refreshedPlayers.code -ne 'PlayerCountRefreshed' -or $refreshedPlayerView.onlinePlayers -ne 0) {
        throw 'Published EXE did not return the canonical player count from the manual Host refresh.'
    }
    $originalDeadline = [DateTimeOffset]::Parse($valheimView.autoShutdownAtUtc)
    $extension = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/countdown/extend" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"minutes":23}'
    $extendedView = @($extension.snapshot.runs) | Where-Object profileId -EQ $valheimId
    $extendedDeadline = [DateTimeOffset]::Parse($extendedView.autoShutdownAtUtc)
    $addedMinutes = ($extendedDeadline - $originalDeadline).TotalMinutes
    if (!$extension.ok -or $addedMinutes -lt 22.99 -or $addedMinutes -gt 23.01) {
        throw 'Published EXE did not extend the active countdown by the requested number of minutes.'
    }
    Write-Host 'PASS published GUI snapshot exposes server count and supports an exact Host countdown extension'
    $ports = Invoke-RestMethod -Uri "$baseUrl/api/local/network/ports"
    $gameCheck = @($ports.games) | Where-Object profileId -EQ $valheimId
    if ($gameCheck.state -ne 'Open on PC' -or @($gameCheck.ports).Count -ne 2 -or $ports.control.state -ne 'Off' -or $ports.control.remoteState -ne 'Not verified') {
        throw 'Local game or Friend control port diagnostics overstated or missed their evidence.'
    }
    Write-Host 'PASS local UDP game-port check and honest unverified Friend route'
    $valheimStop = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/stop" -Method Post -Headers $headers -TimeoutSec 15
    if (!$valheimStop.ok -or !(Test-Path -LiteralPath (Join-Path $imported.worldDirectory 'synthetic-stop.marker'))) {
        throw "Published EXE did not stop the synthetic Valheim process with Ctrl+C: $($valheimStop.message)"
    }
    $valheimStarted = $false
    Write-Host 'PASS published EXE starts and gracefully stops synthetic Valheim'

    $mode = Invoke-RestMethod -Uri "$baseUrl/api/local/mode/friend" -Method Post -Headers $headers
    $friend = Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot"
    if (!$mode.ok -or $friend.mode -ne 'Friend' -or $friend.state -ne 'Not paired') { throw 'Friend mode switch failed.' }
    $friendReadGamePassword = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/profiles/$valheimId/game-password/reveal" -Method Post -Headers $headers -UseBasicParsing | Out-Null }
    catch { $friendReadGamePassword = [int]$_.Exception.Response.StatusCode -eq 409 }
    if (!$friendReadGamePassword) { throw 'Friend mode revealed the Host game password.' }
    $friendDiscovery = Invoke-RestMethod -Uri "$baseUrl/api/local/valheim/discover"
    if ($null -eq $friendDiscovery.installations -or $null -ne $friendDiscovery.clients) { throw 'Friend mode returned the wrong server discovery shape.' }
    $friendInstallDenied = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/minecraft/install" -Method Post -Headers $headers -ContentType 'application/json' -Body $installBody -UseBasicParsing | Out-Null }
    catch { $friendInstallDenied = [int]$_.Exception.Response.StatusCode -eq 409 }
    if (!$friendInstallDenied) { throw 'Friend mode accessed Minecraft install.' }
    $mode = Invoke-RestMethod -Uri "$baseUrl/api/local/mode/host" -Method Post -Headers $headers
    $hostState = Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot"
    if (!$mode.ok -or $hostState.mode -ne 'Host') { throw 'Host mode switch failed.' }
    Write-Host 'PASS in-app Host/Friend mode switching'
    $closed = Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers
    if (!$closed.ok -or !$appProcess.WaitForExit(5000)) { throw 'Quit app did not close the published EXE.' }
    Write-Host 'PASS local Quit app after managed servers stop'
    Write-Host "Smoke data: $caseRoot"
}
finally {
    if ($valheimStarted) {
        try { Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/stop" -Method Post -Headers $headers -TimeoutSec 5 | Out-Null }
        catch { Write-Warning 'Synthetic Valheim Stop failed; checking exact fixture identity for cleanup.' }
        if ($valheimRun -and $valheimRun.processId) {
            $remaining = Get-Process -Id $valheimRun.processId -ErrorAction SilentlyContinue
            if ($remaining -and $remaining.Path -eq $valheimFixturePath -and
                $remaining.StartTime.ToUniversalTime().Ticks -eq $valheimRun.startTimeUtcTicks) {
                Stop-Process -Id $remaining.Id
            }
        }
    }
    if ($fixtureStarted) {
        try { Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/stop" -Method Post -Headers $headers | Out-Null }
        catch { Write-Warning 'Fixture stop did not complete; inspect the smoke data before closing the app.' }
    }
    if ($appProcess -and !$appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
    $env:TOGETHERSERVER_DATA_DIR = $oldDataDirectory
    $env:TOGETHERSERVER_FIXTURE_ROOT = $oldFixtureRoot
    $env:TOGETHERSERVER_ENABLE_FIXTURE_DRIVER = $oldFixtureDriver
}
