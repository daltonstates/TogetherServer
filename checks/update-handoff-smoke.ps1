param([string]$AppPath = '')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }
$appPath = (Resolve-Path -LiteralPath $AppPath).Path
$root = Join-Path $repository ('local-data/update-handoff/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $root 'data'
$stage = Join-Path $dataRoot ('updates/' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'install'
New-Item -ItemType Directory -Path $stage, $install -Force | Out-Null
$target = Join-Path $install 'TogetherServer.exe'
$payload = Join-Path $stage 'TogetherServer-win-x64.exe'
$helper = Join-Path $stage 'TogetherServer-updater.exe'
$ready = Join-Path $stage 'ready.signal'
Set-Content -LiteralPath $target -Value 'previous isolated executable'
Copy-Item -LiteralPath $appPath -Destination $payload
Copy-Item -LiteralPath $appPath -Destination $helper
$hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash

$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 5127)
$portProbe.Start()
$portProbe.Stop()
$oldDataRoot = $env:TOGETHERSERVER_DATA_DIR
$env:TOGETHERSERVER_DATA_DIR = $dataRoot
$parent = $null
$updater = $null
try {
    $parent = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 4') -WindowStyle Hidden -PassThru
    $startTicks = $parent.StartTime.ToUniversalTime().Ticks
    $argumentLine = "--apply-update $($parent.Id) $startTicks `"$target`" `"$payload`" $hash `"$dataRoot`" `"$ready`""
    $updater = Start-Process -FilePath $helper -ArgumentList $argumentLine -WindowStyle Hidden -PassThru
    $signaled = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if (Test-Path -LiteralPath $ready) { $signaled = $true; break }
        if ($updater.HasExited) { throw "Updater exited before readiness with code $($updater.ExitCode)." }
        Start-Sleep -Milliseconds 50
    }
    if (!$signaled) { throw 'Updater did not signal readiness.' }
    Write-Host 'PASS verified updater helper signaled readiness before old process exit'
    if (!$updater.WaitForExit(20000) -or $updater.ExitCode -ne 0) { throw 'Updater did not finish the replacement and relaunch.' }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'The installed EXE does not match the verified payload.' }
    if ((Get-Content -LiteralPath ($target + '.previous') -Raw).Trim() -ne 'previous isolated executable') { throw 'Previous EXE backup was not preserved.' }
    Write-Host 'PASS isolated EXE replacement kept the old file and installed the verified payload'

    $base = 'http://127.0.0.1:5127'
    $running = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        try {
            $snapshot = Invoke-RestMethod -Uri "$base/api/local/snapshot" -TimeoutSec 1
            if ($snapshot.mode -eq 'Host') { $running = $true; break }
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$running) { throw 'The replaced EXE did not relaunch its local app.' }
    $instance = Get-CimInstance Win32_Process -Filter "name = 'TogetherServer.exe'" |
        Where-Object { $_.ExecutablePath -eq $target }
    if (!$instance) { throw 'The expected isolated EXE was not the app serving the local API.' }
    $headers = @{ Origin = $base; 'X-TogetherServer-Local' = '1' }
    $quit = Invoke-RestMethod -Uri "$base/api/local/quit" -Method Post -Headers $headers
    if (!$quit.ok) { throw "Relaunched app did not quit cleanly: $($quit.message)" }
    Write-Host 'PASS updated EXE relaunched and quit through its guarded local API'
    Write-Host "Update handoff data: $root"
}
finally {
    if ($parent -and !$parent.HasExited) { Stop-Process -Id $parent.Id -Force }
    if ($updater -and !$updater.HasExited) { Stop-Process -Id $updater.Id -Force }
    $remaining = Get-CimInstance Win32_Process -Filter "name = 'TogetherServer.exe'" |
        Where-Object { $_.ExecutablePath -eq $target }
    foreach ($process in @($remaining)) { if ($process) { Stop-Process -Id $process.ProcessId -Force } }
    $env:TOGETHERSERVER_DATA_DIR = $oldDataRoot
}
