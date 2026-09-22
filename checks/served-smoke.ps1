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
$env:TOGETHERSERVER_DATA_DIR = $caseRoot
$env:TOGETHERSERVER_FIXTURE_ROOT = $caseRoot
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
        'Paste your server code', 'Saved servers', 'Optional game activity', 'Browse for game',
        'Friend access and settings', 'PC name', 'Servers this PC can control', 'Can start assigned servers',
        'Allow remote Start and Stop', 'Remote Stop',
        'Connection help', 'Advanced network and game paths', 'Technical details',
        'Maximum servers running at once',
        'Revoke all access and create a new code', 'Remote Stop safety', 'There are no player IDs to enter',
        'steam://install/896660'
    )
    foreach ($expectedText in $requiredUiText) {
        if (!$js.Content.Contains($expectedText)) {
            throw "The published GUI is missing expected guided-flow text: $expectedText"
        }
    }
    if ($js.Content.Contains('Public IPv4 address for Valheim')) { throw 'The old manual game IP field is still bundled.' }
    Write-Host 'PASS standalone EXE, published HTML, embedded React JS, and CSS over loopback'

    $discovery = Invoke-RestMethod -Uri "$baseUrl/api/local/valheim/discover"
    if ($null -eq $discovery.installations -or $null -eq $discovery.clients -or $null -eq $discovery.worlds) { throw 'Valheim discovery route returned no result shape.' }
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
    if (@($gameTypes.kind | Sort-Object) -join ',' -ne 'Fixture,MinecraftBedrock,MinecraftJava,Valheim') { throw 'Registered game drivers were not exposed distinctly.' }
    Write-Host 'PASS explicit game-driver catalog exposes Valheim, Minecraft Java, Minecraft Bedrock, and the fixture'

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
    if (!$invite.ok -or !$invite.password.StartsWith('TS3-') -or !$sameInvite.exists -or $sameInvite.password -ne $invite.password -or !$sameInvite.canStart -or !$inviteReadForbidden -or $invitedSettings.companionEndpoint -ne 'https://1.2.3.4:5131' -or $invitedSettings.companionBindAddress -ne '0.0.0.0' -or $invitedSettings.companionListeningEnabled) { throw 'Creating a server code did not keep one protected current code and permission default or prepare the standard Host address safely.' }
    $settings.companionEndpoint = $invitedSettings.companionEndpoint
    $settings.companionBindAddress = $invitedSettings.companionBindAddress
    $settings.companionPort = $invitedSettings.companionPort
    Write-Host 'PASS one persistent server code prepares the standard app address without opening a listener'
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
    $valheimSnapshot = Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot"
    $valheimView = @($valheimSnapshot.runs) | Where-Object profileId -EQ $valheimId
    if ($valheimView.onlinePlayers -ne 0 -or $valheimView.maxPlayers -ne 10) {
        throw 'Published EXE did not expose the synthetic Valheim 0 of 10 player count.'
    }
    Write-Host 'PASS published GUI snapshot exposes the synthetic Valheim player count'
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
    if ($null -eq $friendDiscovery.clients) { throw 'Friend mode could not discover its installed Valheim game client.' }
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
}
