param([string]$AppPath = '', [int]$Port = 5127)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release/TogetherServer.exe' }
$appPath = $AppPath
$fixturePath = Join-Path $repository 'src/TogetherServer.ValheimFixture/bin/Release/net10.0/valheim_server.exe'
if (!(Test-Path -LiteralPath $appPath) -or !(Test-Path -LiteralPath $fixturePath)) { throw 'Run scripts/build.ps1 first.' }

# The default checks the exact no-argument Explorer path; an isolated port uses --desktop for parallel testing.
if ($Port -eq 0) {
    $freePortProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $freePortProbe.Start()
    $Port = ([Net.IPEndPoint]$freePortProbe.LocalEndpoint).Port
    $freePortProbe.Stop()
}
$launchArguments = if ($Port -eq 5127) { @() } else { @('--desktop', '--port', "$Port") }
$launchLabel = if ($launchArguments.Count -eq 0) { 'no-argument EXE' } else { 'isolated desktop EXE' }
$probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try { $probe.Start() }
catch { throw "Local GUI port $Port is in use; choose another port for this desktop smoke." }
finally { $probe.Stop() }

$caseRoot = Join-Path $repository ('local-data/desktop-smoke/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
$oldTaskData = $env:TOGETHERSERVER_DATA_DIR
$oldFixtureRoot = $env:TOGETHERSERVER_FIXTURE_ROOT
$env:TOGETHERSERVER_DATA_DIR = $caseRoot
$env:TOGETHERSERVER_FIXTURE_ROOT = $caseRoot
$baseUrl = "http://127.0.0.1:$Port"
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
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr window, uint command);
}
'@

function Start-Gui {
    if ($launchArguments.Count -eq 0) { return Start-Process -FilePath $appPath -PassThru }
    return Start-Process -FilePath $appPath -ArgumentList $launchArguments -PassThru
}

function Wait-ForGui($process) {
    for ($i = 0; $i -lt 80; $i++) {
        if ($process.HasExited) { throw 'The desktop EXE exited before serving its GUI.' }
        try { return Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot" -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 100 }
    }
    throw 'The desktop EXE did not serve its GUI.'
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
    $first = Start-Gui
    $state = Wait-ForGui $first
    if ($state.mode -ne 'Host') { throw 'First desktop launch did not default to Host mode.' }
    Wait-ForWindow $first
    Write-Host "PASS $launchLabel opens a visible native window with rendered React"

    foreach ($pickerKind in @('world', 'world-folder', 'server')) {
        $picker = Start-Job -ArgumentList $baseUrl, $pickerKind -ScriptBlock {
            param($url, $kind)
            Invoke-RestMethod -Uri "$url/api/local/valheim/browse-$kind" -Method Post -Headers @{ Origin = $url; 'X-TogetherServer-Local' = '1' } -TimeoutSec 30
        }
        try {
            $popup = [IntPtr]::Zero
            for ($i = 0; $i -lt 100; $i++) {
                $windowState = Invoke-RestMethod -Uri "$baseUrl/api/local/window" -TimeoutSec 2
                $first.Refresh()
                $popup = [TogetherServerWindowCheck]::GetWindow($first.MainWindowHandle, 6)
                if ($windowState.fileDialogOpen -and $popup -ne [IntPtr]::Zero -and $popup -ne $first.MainWindowHandle) { break }
                Start-Sleep -Milliseconds 100
            }
            if (!$windowState.fileDialogOpen -or $popup -eq [IntPtr]::Zero -or $popup -eq $first.MainWindowHandle) {
                throw "Browse $pickerKind did not open a native file picker."
            }
            if (![TogetherServerWindowCheck]::PostMessage($popup, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'Could not cancel the native file picker.'
            }
            if (!(Wait-Job -Job $picker -Timeout 10)) { throw 'Canceled file picker did not return.' }
            $choice = Receive-Job -Job $picker
            if ($choice.code -ne 'Canceled') { throw "Canceled picker returned $($choice.code)." }
            Write-Host "PASS Browse $pickerKind opens and cancels a native Windows file picker"
        }
        finally { Stop-Job -Job $picker -ErrorAction SilentlyContinue; Remove-Job -Job $picker -Force -ErrorAction SilentlyContinue }
    }

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
    Write-Host "PASS $launchLabel starts and gracefully stops synthetic Valheim"

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
    Write-Host "PASS $launchLabel restarts and stops synthetic Valheim again"

    $first.Refresh()
    [TogetherServerWindowCheck]::ShowWindow($first.MainWindowHandle, 6) | Out-Null
    if (![TogetherServerWindowCheck]::IsIconic($first.MainWindowHandle)) { throw 'The first window did not minimize.' }
    $second = Start-Gui
    if (!$second.WaitForExit(10000) -or $first.HasExited) {
        throw 'Second desktop launch did not return to the running app.'
    }
    $restored = $false
    for ($i = 0; $i -lt 40; $i++) {
        if (![TogetherServerWindowCheck]::IsIconic($first.MainWindowHandle)) { $restored = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (!$restored) { throw 'Second desktop launch did not restore the minimized TogetherServer window.' }
    Write-Host 'PASS second launch restores the existing native window'

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

    $reopened = Start-Gui
    $state = Wait-ForGui $reopened
    Wait-ForWindow $reopened
    if ($state.mode -ne 'Friend') { throw 'Friend mode was not restored after a relaunch.' }
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
