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
$gamePort = Get-Random -Minimum 35000 -Maximum 45000
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
    $appProcess = Start-Process -FilePath $appPath -ArgumentList @('--host', '--port', $port) -NoNewWindow -PassThru
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
    if (!$js.Content.Contains('Find Valheim installs and saves') -or !$js.Content.Contains('Browse and copy a world save') -or !$js.Content.Contains('Browse for valheim_server.exe') -or !$js.Content.Contains('steam://install/896660') -or !$js.Content.Contains('Quit app')) {
        throw 'The published GUI is missing the Valheim setup controls.'
    }
    Write-Host 'PASS standalone EXE, published HTML, embedded React JS, and CSS over loopback'

    $discovery = Invoke-RestMethod -Uri "$baseUrl/api/local/valheim/discover"
    if ($null -eq $discovery.installations -or $null -eq $discovery.worlds) { throw 'Valheim discovery route returned no result shape.' }
    Write-Host 'PASS loopback-only Valheim discovery route and bundled setup controls'

    $forbidden = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/mode/friend" -Method Post -UseBasicParsing | Out-Null }
    catch { $forbidden = [int]$_.Exception.Response.StatusCode -eq 403 }
    if (!$forbidden) { throw 'A mutation without the local request headers was allowed.' }
    Write-Host 'PASS local mutation gate'

    $profile = @{ id = $profileId; name = 'HTTP fixture'; worldId = 'http-smoke'; worldDirectory = $worldDirectory; gamePort = $gamePort; executablePath = $fixturePath }
    $settings = @{ maxConcurrentServers = 1; idleMinutes = 15; autoShutdownEnabled = $false; remoteControlsEnabled = $false; profiles = @($profile) }
    $saved = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($settings | ConvertTo-Json -Depth 8)
    if (!$saved.ok) { throw "Settings rejected: $($saved.message)" }
    $started = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/start" -Method Post -Headers $headers
    if (!$started.ok -or $started.code -ne 'FixtureStarted') { throw "Start failed: $($started.message)" }
    $fixtureStarted = $true
    $health = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/health" -Method Post -Headers $headers
    if (!$health.ok -or $health.code -ne 'FixtureProcessRunning') { throw "Health failed: $($health.message)" }
    $blocked = $false
    try { Invoke-WebRequest -Uri "$baseUrl/api/local/mode/friend" -Method Post -Headers $headers -UseBasicParsing | Out-Null }
    catch { $blocked = [int]$_.Exception.Response.StatusCode -eq 409 }
    if (!$blocked) { throw 'Friend mode was allowed while a managed process was active.' }
    $quitBlocked = Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers
    if ($quitBlocked.code -ne 'ManagedRunPresent') { throw 'The app quit while a managed server was running.' }
    $stopped = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/stop" -Method Post -Headers $headers
    if (!$stopped.ok) { throw "Stop failed: $($stopped.message)" }
    $fixtureStarted = $false
    Write-Host 'PASS served settings, Start, Health, Stop, and mode guard'

    $valheimId = [guid]::NewGuid().ToString()
    $valheimProfile = @{ id = $valheimId; kind = 'Valheim'; name = 'Synthetic Valheim settings'; serverName = 'Fixture Valheim'; worldId = 'not-started'; worldDirectory = $worldDirectory; gamePort = ($gamePort + 10); executablePath = $valheimFixturePath }
    $settings.profiles = @($profile, $valheimProfile)
    $saved = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($settings | ConvertTo-Json -Depth 8)
    if (!$saved.ok) { throw "Valheim settings rejected: $($saved.message)" }
    $password = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/password" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"password":"fixture-pass-123"}'
    if (!$password.ok -or !$password.snapshot.passwordConfigured.$valheimId) { throw 'Protected Valheim password route failed.' }
    if ((Get-Content (Join-Path $caseRoot 'host.json') -Raw).Contains('fixture-pass-123')) { throw 'Valheim password leaked into Host settings.' }
    $missing = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/start" -Method Post -Headers $headers
    if ($missing.code -ne 'MissingWorldPair') { throw 'Missing save pair did not block synthetic Valheim launch.' }
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
    Write-Host 'PASS served Valheim profile and protected password endpoint (synthetic settings)'
    Write-Host 'PASS served missing-save guard and copy import without source mutation (synthetic)'

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
    $valheimStop = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$valheimId/stop" -Method Post -Headers $headers -TimeoutSec 15
    if (!$valheimStop.ok -or !(Test-Path -LiteralPath (Join-Path $imported.worldDirectory 'synthetic-stop.marker'))) {
        throw "Published EXE did not stop the synthetic Valheim process with Ctrl+C: $($valheimStop.message)"
    }
    $valheimStarted = $false
    Write-Host 'PASS published EXE starts and gracefully stops synthetic Valheim'

    $mode = Invoke-RestMethod -Uri "$baseUrl/api/local/mode/friend" -Method Post -Headers $headers
    $friend = Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot"
    if (!$mode.ok -or $friend.mode -ne 'Friend' -or $friend.state -ne 'Not paired') { throw 'Friend mode switch failed.' }
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
