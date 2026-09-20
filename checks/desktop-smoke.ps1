$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$appPath = Join-Path $repository 'local-data/release/TogetherServer.exe'
$fixturePath = Join-Path $repository 'src/TogetherServer.ValheimFixture/bin/Release/net10.0/valheim_server.exe'
if (!(Test-Path -LiteralPath $appPath) -or !(Test-Path -LiteralPath $fixturePath)) { throw 'Run scripts/build.ps1 first.' }

# No arguments is the exact Explorer/double-click launch path.
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 5127)
try { $probe.Start() }
catch { throw 'Default GUI port 5127 is in use; close that local app before this desktop smoke.' }
finally { $probe.Stop() }

$caseRoot = Join-Path $repository ('local-data/desktop-smoke/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
$oldTaskData = $env:TOGETHERSERVER_DATA_DIR
$oldFixtureRoot = $env:TOGETHERSERVER_FIXTURE_ROOT
$env:TOGETHERSERVER_DATA_DIR = $caseRoot
$env:TOGETHERSERVER_FIXTURE_ROOT = $caseRoot
$baseUrl = 'http://127.0.0.1:5127'
$headers = @{ Origin = $baseUrl; 'X-TogetherServer-Local' = '1' }
$first = $null
$second = $null
$reopened = $null
$valheimRun = $null
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TogetherServerWindowCheck
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);
}
'@

function Wait-ForGui($process) {
    for ($i = 0; $i -lt 80; $i++) {
        if ($process.HasExited) { throw 'The no-argument desktop EXE exited before serving its GUI.' }
        try { return Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot" -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 100 }
    }
    throw 'The no-argument desktop EXE did not serve its GUI.'
}

function Wait-ForWindow($process) {
    for ($i = 0; $i -lt 100; $i++) {
        if ($process.HasExited) { throw 'The TogetherServer window process exited.' }
        try {
            $window = Invoke-RestMethod -Uri "$baseUrl/api/local/window" -TimeoutSec 2
            $process.Refresh()
            if ($window.visible -and $window.rendered -and $process.MainWindowHandle -ne [IntPtr]::Zero) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 100
    }
    throw 'The native TogetherServer window did not visibly render React.'
}

try {
    $first = Start-Process -FilePath $appPath -PassThru
    $state = Wait-ForGui $first
    if ($state.mode -ne 'Host') { throw 'First desktop launch did not default to Host mode.' }
    Wait-ForWindow $first
    Write-Host 'PASS double-click path opens a visible native window with rendered React'

    $saveRoot = Join-Path $caseRoot 'source-save'
    $saveFiles = Join-Path $saveRoot 'worlds_local'
    New-Item -ItemType Directory -Path $saveFiles -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $saveFiles 'fixture-world.db') -Value 'synthetic database'
    Set-Content -LiteralPath (Join-Path $saveFiles 'fixture-world.fwl') -Value 'synthetic metadata'
    $profileId = [guid]::NewGuid().ToString()
    $import = Invoke-RestMethod -Uri "$baseUrl/api/local/valheim/import" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ profileId = $profileId; sourceSaveRoot = $saveRoot; worldId = 'fixture-world' } | ConvertTo-Json)
    if (!$import.ok) { throw 'Desktop synthetic world import failed.' }
    $profile = @{ id = $profileId; kind = 'Valheim'; name = 'Desktop fixture'; serverName = 'Fixture "Valheim"'; worldId = 'fixture-world'; worldDirectory = $import.worldDirectory; gamePort = (Get-Random -Minimum 35000 -Maximum 45000); executablePath = $fixturePath }
    $settings = @{ maxConcurrentServers = 1; idleMinutes = 15; autoShutdownEnabled = $false; remoteControlsEnabled = $false; profiles = @($profile) }
    $saved = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($settings | ConvertTo-Json -Depth 8)
    if (!$saved.ok) { throw 'Desktop synthetic Valheim settings failed.' }
    $password = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/password" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"password":"fixture-pass-123"}'
    if (!$password.ok) { throw 'Desktop synthetic Valheim password failed.' }
    $started = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/start" -Method Post -Headers $headers
    if (!$started.ok) { throw "Desktop synthetic Valheim launch failed: $($started.message)" }
    $valheimRun = (Get-Content (Join-Path $caseRoot 'runs.json') -Raw | ConvertFrom-Json) | Where-Object profileId -EQ $profileId
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        $health = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/health" -Method Post -Headers $headers
        if ($health.code -eq 'ValheimLogReady') { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Desktop synthetic server readiness was not observed.' }
    $stopped = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/stop" -Method Post -Headers $headers -TimeoutSec 15
    if (!$stopped.ok -or !(Test-Path -LiteralPath (Join-Path $import.worldDirectory 'synthetic-stop.marker'))) {
        throw "Desktop synthetic Ctrl+C stop failed: $($stopped.message)"
    }
    $valheimRun = $null
    Write-Host 'PASS no-argument desktop EXE starts and gracefully stops synthetic Valheim'

    $restarted = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/start" -Method Post -Headers $headers
    if (!$restarted.ok -or $restarted.code -ne 'ValheimStarting') { throw 'Desktop synthetic restart failed.' }
    $valheimRun = (Get-Content (Join-Path $caseRoot 'runs.json') -Raw | ConvertFrom-Json) | Where-Object profileId -EQ $profileId
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        $health = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/health" -Method Post -Headers $headers
        if ($health.code -eq 'ValheimLogReady') { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Desktop synthetic restart readiness was not observed.' }
    $stoppedAgain = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/stop" -Method Post -Headers $headers -TimeoutSec 15
    if (!$stoppedAgain.ok) { throw "Desktop synthetic restart stop failed: $($stoppedAgain.message)" }
    $valheimRun = $null
    Write-Host 'PASS no-argument desktop EXE restarts and stops synthetic Valheim again'

    $first.Refresh()
    [TogetherServerWindowCheck]::ShowWindow($first.MainWindowHandle, 6) | Out-Null
    if (![TogetherServerWindowCheck]::IsIconic($first.MainWindowHandle)) { throw 'The first window did not minimize.' }
    $second = Start-Process -FilePath $appPath -PassThru
    if (!$second.WaitForExit(10000) -or $first.HasExited) {
        throw 'Second desktop launch did not return to the running app.'
    }
    $restored = $false
    for ($i = 0; $i -lt 40; $i++) {
        if (![TogetherServerWindowCheck]::IsIconic($first.MainWindowHandle)) { $restored = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (!$restored) { throw 'Second desktop launch did not restore the minimized TogetherServer window.' }
    Write-Host 'PASS second double-click restores the existing native window'

    $mode = Invoke-RestMethod -Uri "$baseUrl/api/local/mode/friend" -Method Post -Headers $headers
    if (!$mode.ok -or (Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot").mode -ne 'Friend') {
        throw 'Friend mode selection failed.'
    }
    $first.Refresh()
    $posted = [TogetherServerWindowCheck]::PostMessage($first.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
    if (!$posted) { throw "Could not send close to the native window: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
    if (!$first.WaitForExit(10000)) {
        $windowState = Invoke-RestMethod -Uri "$baseUrl/api/local/window"
        throw "Closing the native window did not exit: visible=$($windowState.visible), rendered=$($windowState.rendered), title=$($first.MainWindowTitle)"
    }

    $reopened = Start-Process -FilePath $appPath -PassThru
    $state = Wait-ForGui $reopened
    Wait-ForWindow $reopened
    if ($state.mode -ne 'Friend') { throw 'Friend mode was not restored after a normal double-click relaunch.' }
    $closed = Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers
    if (!$closed.ok -or !$reopened.WaitForExit(10000)) { throw 'Quit app did not close the Friend instance.' }
    Write-Host 'PASS Friend mode persists; window close and Quit app both exit'
    Write-Host "Desktop smoke data: $caseRoot"
}
finally {
    if ($valheimRun -and $valheimRun.processId) {
        $remaining = Get-Process -Id $valheimRun.processId -ErrorAction SilentlyContinue
        if ($remaining -and $remaining.Path -eq $fixturePath -and
            $remaining.StartTime.ToUniversalTime().Ticks -eq $valheimRun.startTimeUtcTicks) {
            Stop-Process -Id $remaining.Id # Exact disposable synthetic fixture only.
        }
    }
    foreach ($started in @($first, $second, $reopened)) {
        if ($null -eq $started -or $started.HasExited) { continue }
        $running = Get-Process -Id $started.Id -ErrorAction SilentlyContinue
        if ($running -and $running.Path -eq $appPath -and $running.StartTime -eq $started.StartTime) {
            Stop-Process -Id $started.Id
        }
    }
    $env:TOGETHERSERVER_DATA_DIR = $oldTaskData
    $env:TOGETHERSERVER_FIXTURE_ROOT = $oldFixtureRoot
}
