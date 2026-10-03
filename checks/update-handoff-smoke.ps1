param([string]$AppPath = '')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }
$appPath = (Resolve-Path -LiteralPath $AppPath).Path
$signature = Get-AuthenticodeSignature -LiteralPath $appPath
if ($signature.Status -eq 'Valid' -and $signature.SignerCertificate) {
    $publisherHasher = [Security.Cryptography.SHA256]::Create()
    try {
        $verification = ([BitConverter]::ToString(
            $publisherHasher.ComputeHash($signature.SignerCertificate.GetPublicKey()))).Replace('-', '')
    }
    finally { $publisherHasher.Dispose() }
    $verificationLabel = 'SHA-256 and same-publisher'
}
elseif ($signature.Status -eq 'NotSigned') {
    $verification = 'HASH_ONLY'
    $verificationLabel = 'SHA-256'
}
else { throw "Update handoff candidate has an invalid Authenticode state: $($signature.Status)." }
$root = Join-Path $repository ('local-data/update-handoff/' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $root 'data'
$stage = Join-Path $dataRoot ('updates/' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'install'
New-Item -ItemType Directory -Path $stage, $install -Force | Out-Null
$port = 5127
$targetFileName = 'TogetherServer-win-x64 (4).exe'
$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
try { $portProbe.Start() }
catch [Net.Sockets.SocketException] {
    $port = 5128
    $targetFileName = 'TogetherServer DEVELOPMENT.exe'
    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
    try { $portProbe.Start() }
    catch [Net.Sockets.SocketException] { throw 'Update handoff needs production port 5127 or staging port 5128 to be free.' }
}
finally { $portProbe.Stop() }
$target = Join-Path $install $targetFileName
$targetProcessName = [IO.Path]::GetFileName($target)
$payload = Join-Path $stage 'TogetherServer-win-x64.exe'
$helper = Join-Path $stage 'TogetherServer-updater.exe'
$ready = Join-Path $stage 'ready.signal'
Copy-Item -LiteralPath $appPath -Destination $target
Copy-Item -LiteralPath $appPath -Destination $payload
Copy-Item -LiteralPath $appPath -Destination $helper
$hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
$previousHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
$checkpointId = [guid]::NewGuid().ToString('N')
$checkpoint = Join-Path $dataRoot "update-checkpoints/$checkpointId.checkpoint"
New-Item -ItemType Directory -Path $checkpoint -Force | Out-Null
$checkpointManifest = [ordered]@{
    schemaVersion = 1
    id = ([guid]::ParseExact($checkpointId, 'N')).ToString('D')
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    currentVersion = '0.2.1'
    targetVersion = '0.2.1'
    storageSchemaVersion = 3
    previousExecutableSha256 = $previousHash
    files = @()
} | ConvertTo-Json -Depth 4
$checkpointManifestPath = Join-Path $checkpoint 'checkpoint-manifest.json'
[IO.File]::WriteAllText($checkpointManifestPath, $checkpointManifest, [Text.UTF8Encoding]::new($false))
$checkpointHash = (Get-FileHash -LiteralPath $checkpointManifestPath -Algorithm SHA256).Hash

$oldDataRoot = $env:TOGETHERSERVER_DATA_DIR
$oldStagingDataRoot = $env:TOGETHERSERVER_STAGING_DATA_DIR
if ($port -eq 5128) {
    $env:TOGETHERSERVER_DATA_DIR = Join-Path $root 'production-data'
    $env:TOGETHERSERVER_STAGING_DATA_DIR = Join-Path $root 'staging-data'
}
else { $env:TOGETHERSERVER_DATA_DIR = $dataRoot }
$parent = $null
$updater = $null
try {
    $parent = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 4') -WindowStyle Hidden -PassThru
    $startTicks = $parent.StartTime.ToUniversalTime().Ticks
    $argumentLine = "--apply-update $($parent.Id) $startTicks `"$target`" `"$payload`" $hash `"$dataRoot`" `"$ready`" $verification `"$checkpoint`" $checkpointHash"
    $updater = Start-Process -FilePath $helper -ArgumentList $argumentLine -WindowStyle Hidden -PassThru
    $signaled = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if (Test-Path -LiteralPath $ready) { $signaled = $true; break }
        if ($updater.HasExited) { throw "Updater exited before readiness with code $($updater.ExitCode)." }
        Start-Sleep -Milliseconds 50
    }
    if (!$signaled) { throw 'Updater did not signal readiness.' }
    Write-Host "PASS $verificationLabel-verified updater helper rechecked the local-state checkpoint and signaled readiness before old process exit"
    if (!$updater.WaitForExit(20000) -or $updater.ExitCode -ne 0) { throw 'Updater did not finish the replacement and relaunch.' }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'The installed EXE does not match the verified payload.' }
    if ((Get-FileHash -LiteralPath ($target + '.previous') -Algorithm SHA256).Hash -ne $previousHash) { throw 'Previous EXE backup was not preserved.' }
    Write-Host 'PASS isolated EXE replacement kept the old file and installed the verified payload'

    $base = "http://127.0.0.1:$port"
    $running = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        try {
            $snapshot = Invoke-RestMethod -Uri "$base/api/local/snapshot" -TimeoutSec 1
            if ($snapshot.mode -eq 'Host') { $running = $true; break }
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$running) { throw 'The replaced EXE did not relaunch its local app.' }
    $instance = Get-CimInstance Win32_Process -Filter "name = '$targetProcessName'" |
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
    $remaining = Get-CimInstance Win32_Process -Filter "name = '$targetProcessName'" |
        Where-Object { $_.ExecutablePath -eq $target }
    foreach ($process in @($remaining)) { if ($process) { Stop-Process -Id $process.ProcessId -Force } }
    $env:TOGETHERSERVER_DATA_DIR = $oldDataRoot
    $env:TOGETHERSERVER_STAGING_DATA_DIR = $oldStagingDataRoot
}
