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
$activeTcp = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
    ForEach-Object { $_.Port }
if ($activeTcp -contains $port) {
    $port = 5128
    $targetFileName = 'TogetherServer DEVELOPMENT.exe'
}
if ($activeTcp -contains $port) { throw 'Update handoff needs production port 5127 or staging port 5128 to be free.' }
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
    storageSchemaVersion = 4
    previousExecutableSha256 = $previousHash
    files = @()
} | ConvertTo-Json -Depth 4
$checkpointManifestPath = Join-Path $checkpoint 'checkpoint-manifest.json'
[IO.File]::WriteAllText($checkpointManifestPath, $checkpointManifest, [Text.UTF8Encoding]::new($false))
$checkpointHash = (Get-FileHash -LiteralPath $checkpointManifestPath -Algorithm SHA256).Hash

$oldDataRoot = $env:TOGETHERSERVER_DATA_DIR
$oldStagingDataRoot = $env:TOGETHERSERVER_STAGING_DATA_DIR
$parentPath = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source

function Record-OwnedProcess([Diagnostics.Process]$Process, [string]$ExpectedPath) {
    # Retain the original process handle so an exited PID cannot be reused by cleanup.
    $identity = @{ Process = $Process; Id = $Process.Id; Ticks = 0L;
        Path = [IO.Path]::GetFullPath($ExpectedPath) }
    try {
        $null = $Process.SafeHandle
        $identity.Ticks = $Process.StartTime.ToUniversalTime().Ticks
    } catch {
        $Process.Refresh()
        if (!$Process.HasExited) { throw }
    }
    return $identity
}

function Stop-OwnedProcess($Identity) {
    if ($null -eq $Identity) { return }
    $process = $Identity.Process
    $process.Refresh()
    if ($process.HasExited) { return }
    try {
        $ticks = $process.StartTime.ToUniversalTime().Ticks
        $path = $process.MainModule.FileName
    } catch {
        $process.Refresh()
        if ($process.HasExited) { return }
        throw
    }
    if ($Identity.Ticks -le 0 -or $process.Id -ne $Identity.Id -or $ticks -ne $Identity.Ticks -or
        [string]::IsNullOrWhiteSpace($path) -or ![IO.Path]::IsPathRooted($path) -or
        ![IO.Path]::GetFullPath($path).Equals($Identity.Path, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Update handoff cleanup refused a changed or uncertain disposable process identity.'
    }
    $process.Refresh()
    if ($process.HasExited) { return }
    try { $process.Kill() }
    catch {
        # A graceful API Quit may finish after the identity check but before Kill.
        $process.Refresh()
        if (!$process.HasExited) { throw }
    }
    if (!$process.WaitForExit(5000)) { throw 'A retained disposable update process did not exit during cleanup.' }
}

if ($port -eq 5128) {
    $env:TOGETHERSERVER_DATA_DIR = Join-Path $root 'production-data'
    $env:TOGETHERSERVER_STAGING_DATA_DIR = Join-Path $root 'staging-data'
}
else { $env:TOGETHERSERVER_DATA_DIR = $dataRoot }
$parent = $null
$updater = $null
$parentIdentity = $null
$updaterIdentity = $null
$appIdentity = $null
try {
    $parent = Start-Process -FilePath $parentPath -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 4') -WindowStyle Hidden -PassThru
    $parentIdentity = Record-OwnedProcess $parent $parentPath
    $startTicks = $parent.StartTime.ToUniversalTime().Ticks
    $argumentLine = "--apply-update $($parent.Id) $startTicks `"$target`" `"$payload`" $hash `"$dataRoot`" `"$ready`" $verification `"$checkpoint`" $checkpointHash"
    $updater = Start-Process -FilePath $helper -ArgumentList $argumentLine -WindowStyle Hidden -PassThru
    $updaterIdentity = Record-OwnedProcess $updater $helper
    $signaled = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if (Test-Path -LiteralPath $ready) { $signaled = $true; break }
        if ($updater.HasExited) { throw "Updater exited before readiness with code $($updater.ExitCode)." }
        Start-Sleep -Milliseconds 50
    }
    if (!$signaled) { throw 'Updater did not signal readiness.' }
    Write-Host "PASS $verificationLabel-verified updater helper rechecked the local-state checkpoint and signaled readiness before old process exit"
    if (!$updater.WaitForExit(20000) -or $updater.ExitCode -ne 0) { throw 'Updater did not finish the replacement and relaunch.' }
    # Record the replacement before hash or API assertions can fail. Cleanup never
    # discovers an unrecorded process; this short wait covers initial CIM visibility.
    $discoveryDeadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $instance = @(Get-CimInstance Win32_Process -Filter "name = '$targetProcessName'" |
            Where-Object { $_.ExecutablePath -eq $target })
        if ($instance.Count -gt 1) { throw 'More than one process has the exact isolated replacement executable path.' }
        if ($instance.Count -eq 1) { break }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $discoveryDeadline)
    if ($instance.Count -ne 1) { throw 'The expected isolated replacement process was not found within 3 seconds.' }
    $relaunched = [Diagnostics.Process]::GetProcessById([int]$instance[0].ProcessId)
    $appIdentity = Record-OwnedProcess $relaunched $target
    do {
        $relaunched.Refresh()
        if ($relaunched.HasExited -or $appIdentity.Ticks -le 0 -or $relaunched.Id -ne $appIdentity.Id -or
            $relaunched.StartTime.ToUniversalTime().Ticks -ne $appIdentity.Ticks) {
            throw 'The relaunched isolated app identity changed before replacement assertions.'
        }
        $observedPath = [string]$relaunched.MainModule.FileName
        if (![string]::IsNullOrWhiteSpace($observedPath)) {
            if (![IO.Path]::IsPathFullyQualified($observedPath) -or
                ![IO.Path]::GetFullPath($observedPath).Equals($appIdentity.Path, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The relaunched isolated app executable path did not match the retained identity.'
            }
            break
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $discoveryDeadline)
    if ([string]::IsNullOrWhiteSpace($observedPath)) { throw 'The isolated replacement path was not ready within its 3-second discovery window.' }
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
    if ($relaunched.HasExited -or $appIdentity.Ticks -le 0 -or $relaunched.Id -ne $appIdentity.Id -or
        $relaunched.StartTime.ToUniversalTime().Ticks -ne $appIdentity.Ticks -or
        ![IO.Path]::GetFullPath($relaunched.MainModule.FileName).Equals($appIdentity.Path, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The relaunched isolated app identity could not be verified before guarded Quit.'
    }
    $headers = @{ Origin = $base; 'X-TogetherServer-Local' = '1' }
    $quit = Invoke-RestMethod -Uri "$base/api/local/quit" -Method Post -Headers $headers
    if (!$quit.ok) { throw "Relaunched app did not quit cleanly: $($quit.message)" }
    if (!$relaunched.WaitForExit(10000)) { throw 'The exact relaunched app did not exit after guarded local API Quit.' }
    Write-Host 'PASS updated EXE relaunched and quit through its guarded local API'
    Write-Host "Update handoff data: $root"
}
finally {
    $cleanupErrors = @()
    foreach ($identity in @($parentIdentity, $updaterIdentity, $appIdentity)) {
        if ($null -eq $identity) { continue }
        try { Stop-OwnedProcess $identity }
        catch { $cleanupErrors += $_.Exception.Message }
        finally { $identity.Process.Dispose() }
    }
    $env:TOGETHERSERVER_DATA_DIR = $oldDataRoot
    $env:TOGETHERSERVER_STAGING_DATA_DIR = $oldStagingDataRoot
    if ($cleanupErrors.Count -gt 0) { throw ('Update handoff cleanup failed: ' + ($cleanupErrors -join '; ')) }
}
