param([string]$AppPath = '')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }
$appPath = (Resolve-Path -LiteralPath $AppPath).Path
$caseRoot = Join-Path $repository ('local-data/staging-smoke/' + [guid]::NewGuid().ToString('N'))
$productionRoot = Join-Path $caseRoot 'production'
$stagingRoot = Join-Path $caseRoot 'staging'
$developmentAppPath = Join-Path $caseRoot 'TogetherServer DEVELOPMENT.exe'
New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
Copy-Item -LiteralPath $appPath -Destination $developmentAppPath -Force
if ((Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $developmentAppPath -Algorithm SHA256).Hash) {
    throw 'The development executable copy does not match the candidate bytes.'
}
$productionWorld = Join-Path $productionRoot 'worlds/live-world'
New-Item -ItemType Directory -Path $productionWorld -Force | Out-Null
$productionSave = Join-Path $productionWorld 'owner-save.db'
[IO.File]::WriteAllText($productionSave, 'production-owner-data')

$listeners = @(
    [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0),
    [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
)
try {
    $listeners[0].Start()
    $listeners[1].Start()
    $productionPort = ([Net.IPEndPoint]$listeners[0].LocalEndpoint).Port
    $stagingPort = ([Net.IPEndPoint]$listeners[1].LocalEndpoint).Port
}
finally { $listeners | ForEach-Object { $_.Stop() } }
$productionUrl = "http://127.0.0.1:$productionPort"
$stagingUrl = "http://127.0.0.1:$stagingPort"
$productionHeaders = @{ Origin = $productionUrl; 'X-TogetherServer-Local' = '1' }
$stagingHeaders = @{ Origin = $stagingUrl; 'X-TogetherServer-Local' = '1' }

function Wait-LocalApp([Diagnostics.Process]$Process, [string]$Url, [string]$Name) {
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        if ($Process.HasExited) { throw "$Name exited during startup." }
        try {
            if ((Invoke-WebRequest -Uri "$Url/api/local/instance" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { return }
        }
        catch {
            if ($Process.HasExited) { throw "$Name exited during startup." }
            Start-Sleep -Milliseconds 100
        }
    }
    throw "$Name did not start its local API."
}

function Read-StartupValue {
    try {
        return Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TogetherServer' -ErrorAction Stop
    }
    catch [System.Management.Automation.ItemNotFoundException] { return $null }
    catch [System.Management.Automation.PSArgumentException] { return $null }
}

$oldProductionRoot = $env:TOGETHERSERVER_DATA_DIR
$oldStagingRoot = $env:TOGETHERSERVER_STAGING_DATA_DIR
$productionProcess = $null
$stagingProcess = $null
try {
    $env:TOGETHERSERVER_DATA_DIR = $productionRoot
    Remove-Item Env:TOGETHERSERVER_STAGING_DATA_DIR -ErrorAction SilentlyContinue
    $productionProcess = Start-Process -FilePath $appPath -ArgumentList @('--host', '--port', $productionPort) `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $caseRoot 'production-stdout.txt') `
        -RedirectStandardError (Join-Path $caseRoot 'production-stderr.txt')
    Wait-LocalApp $productionProcess $productionUrl 'Production instance'

    $env:TOGETHERSERVER_DATA_DIR = $productionRoot
    $env:TOGETHERSERVER_STAGING_DATA_DIR = $stagingRoot
    $stagingProcess = Start-Process -FilePath $developmentAppPath -ArgumentList @('--host', '--port', $stagingPort) `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $caseRoot 'staging-stdout.txt') `
        -RedirectStandardError (Join-Path $caseRoot 'staging-stderr.txt')
    Wait-LocalApp $stagingProcess $stagingUrl 'Staging instance'

    $productionInstance = Invoke-RestMethod -Uri "$productionUrl/api/local/instance"
    $stagingInstance = Invoke-RestMethod -Uri "$stagingUrl/api/local/instance"
    if ($productionInstance.kind -ne 'Production' -or $productionInstance.isStaging -or
        $stagingInstance.kind -ne 'Staging' -or $stagingInstance.displayName -ne 'TogetherServer DEVELOPMENT' -or
        !$stagingInstance.isStaging -or !$stagingInstance.freshWorldsOnly) {
        throw 'The two running processes did not report distinct production and staging identities.'
    }
    if ($productionInstance.localPort -ne $productionPort -or $stagingInstance.localPort -ne $stagingPort -or
        $productionInstance.dataRoot -eq $stagingInstance.dataRoot) {
        throw 'Production and development did not report their own local API ports and data roots.'
    }
    if ($stagingInstance.companionPort -ne 5132 -or $stagingInstance.valheimPort -ne 2458 -or
        $stagingInstance.minecraftJavaPort -ne 25566 -or $stagingInstance.minecraftBedrockPort -ne 19134) {
        throw 'The staging process did not report its isolated default ports.'
    }

    $productionSnapshot = Invoke-RestMethod -Uri "$productionUrl/api/local/snapshot"
    $stagingSnapshot = Invoke-RestMethod -Uri "$stagingUrl/api/local/snapshot"
    if ($productionSnapshot.settings.companionPort -ne 5131 -or $stagingSnapshot.settings.companionPort -ne 5132 -or
        @($stagingSnapshot.settings.profiles).Count -ne 0 -or @($stagingSnapshot.runs).Count -ne 0) {
        throw 'Staging inherited production settings, profiles, or runs.'
    }
    $productionPorts = Invoke-RestMethod -Uri "$productionUrl/api/local/network/ports"
    $stagingPorts = Invoke-RestMethod -Uri "$stagingUrl/api/local/network/ports"
    if ($productionPorts.control.port -ne 5131 -or $stagingPorts.control.port -ne 5132 -or
        $null -ne $productionPorts.control.endpoint -or $null -ne $stagingPorts.control.endpoint) {
        throw 'Production and development port diagnostics crossed instances or mishandled an unconfigured endpoint.'
    }
    if ([IO.File]::ReadAllText($productionSave) -ne 'production-owner-data' -or
        (Get-ChildItem -LiteralPath $stagingRoot -Filter 'owner-save.db' -Recurse -ErrorAction SilentlyContinue)) {
        throw 'The production world marker changed or was copied into staging.'
    }

    $unsafeSettings = $stagingSnapshot.settings
    $profileId = [guid]::NewGuid().ToString()
    $unsafeSettings.profiles = @([pscustomobject]@{
        id = $profileId; kind = 'Valheim'; name = 'unsafe'; serverName = 'unsafe'; worldId = 'live-world'
        worldSource = 'New'; worldDirectory = $productionWorld; gamePort = 2458; executablePath = $appPath
    })
    $boundary = Invoke-RestMethod -Uri "$stagingUrl/api/local/settings" -Method Put -Headers $stagingHeaders `
        -ContentType 'application/json' -Body ($unsafeSettings | ConvertTo-Json -Depth 10)
    if ($boundary.ok -or $boundary.code -ne 'StagingDataBoundary' -or @($boundary.snapshot.settings.profiles).Count -ne 0) {
        throw 'Staging accepted a production world directory.'
    }
    $import = Invoke-RestMethod -Uri "$stagingUrl/api/local/valheim/import" -Method Post -Headers $stagingHeaders `
        -ContentType 'application/json' -Body (@{ profileId = $profileId; sourceSaveRoot = $productionWorld; worldId = 'live-world' } | ConvertTo-Json)
    if ($import.ok -or $import.code -ne 'StagingFreshWorldRequired') { throw 'Staging accepted an existing-world import.' }
    $discovery = Invoke-RestMethod -Uri "$stagingUrl/api/local/valheim/discover"
    if (@($discovery.worlds).Count -ne 0) { throw 'Staging exposed existing Valheim worlds.' }

    $startupBefore = Read-StartupValue
    $preferences = Invoke-RestMethod -Uri "$stagingUrl/api/local/desktop/preferences"
    $startupAttempt = Invoke-RestMethod -Uri "$stagingUrl/api/local/desktop/preferences" -Method Put `
        -Headers $stagingHeaders -ContentType 'application/json' -Body '{"launchAtLogin":true}'
    $startupAfter = Read-StartupValue
    if ($preferences.startupAvailable -or $startupAttempt.ok -or $startupAttempt.code -ne 'StagingStartupDisabled' -or
        $startupBefore -ne $startupAfter) {
        throw 'Staging changed or enabled the production Windows sign-in registration.'
    }
    $update = Invoke-RestMethod -Uri "$stagingUrl/api/local/update"
    if ($update.state -ne 'Unsupported' -or $update.message -notlike '*disabled in staging*') {
        throw 'Staging did not disable automatic release updates.'
    }

    $productionPage = Invoke-WebRequest -Uri "$productionUrl/" -UseBasicParsing
    $stagingPage = Invoke-WebRequest -Uri "$stagingUrl/" -UseBasicParsing
    if ($productionPage.Content -notmatch '/assets/' -or $stagingPage.Content -notmatch '/assets/') {
        throw 'One of the simultaneous instances did not serve its bundled UI.'
    }
    $stagingScriptPath = [regex]::Match($stagingPage.Content, '/assets/[^" ]+\.js').Value
    $stagingScript = Invoke-WebRequest -Uri ($stagingUrl + $stagingScriptPath) -UseBasicParsing
    if (!$stagingScript.Content.Contains('DEVELOPMENT / STAGING') -or
        !$stagingScript.Content.Contains('Fresh disposable worlds only') -or
        !$stagingScript.Content.Contains('Production profiles, credentials, settings, runs, and world saves are not loaded or copied.')) {
        throw 'The bundled staging UI is missing its visible data-isolation warning.'
    }
    Write-Host 'PASS production and directly opened development app run simultaneously with separate roots and ports'
    Write-Host 'PASS staging rejects production world paths and existing-world import'
    Write-Host 'PASS staging leaves Windows startup and automatic updates disabled'

    $closedStaging = Invoke-RestMethod -Uri "$stagingUrl/api/local/quit" -Method Post -Headers $stagingHeaders
    $closedProduction = Invoke-RestMethod -Uri "$productionUrl/api/local/quit" -Method Post -Headers $productionHeaders
    if (!$closedStaging.ok -or !$closedProduction.ok -or !$stagingProcess.WaitForExit(5000) -or
        !$productionProcess.WaitForExit(5000)) { throw 'The two isolated instances did not close cleanly.' }
    Write-Host "Staging smoke data: $caseRoot"
}
finally {
    if ($stagingProcess -and !$stagingProcess.HasExited) { Stop-Process -Id $stagingProcess.Id }
    if ($productionProcess -and !$productionProcess.HasExited) { Stop-Process -Id $productionProcess.Id }
    if ($null -eq $oldProductionRoot) { Remove-Item Env:TOGETHERSERVER_DATA_DIR -ErrorAction SilentlyContinue }
    else { $env:TOGETHERSERVER_DATA_DIR = $oldProductionRoot }
    if ($null -eq $oldStagingRoot) { Remove-Item Env:TOGETHERSERVER_STAGING_DATA_DIR -ErrorAction SilentlyContinue }
    else { $env:TOGETHERSERVER_STAGING_DATA_DIR = $oldStagingRoot }
}
