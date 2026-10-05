param([string]$InstallerPath = '', [string]$ExpectedAppPath = '', [switch]$AllowInteractiveTests)
$ErrorActionPreference = 'Stop'
if (!$AllowInteractiveTests) {
    throw 'ForegroundSafety: Installer smoke can launch windows and change focus. -AllowInteractiveTests requires explicit owner approval for a separate test PC or dedicated unattended Windows session.'
}
$repository = Split-Path -Parent $PSScriptRoot
if (!$InstallerPath) { $InstallerPath = Join-Path $repository 'local-data/installer/TogetherServer-Setup-win-x64.exe' }
if (!$ExpectedAppPath) { $ExpectedAppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$expectedApp = (Resolve-Path -LiteralPath $ExpectedAppPath).Path
$id = [guid]::NewGuid().ToString('N')
$caseRoot = Join-Path $repository "local-data/installer-smoke/$id"
$installRoot = Join-Path $caseRoot 'installed app'
$dataRoot = Join-Path $caseRoot 'data'
$setupLog = Join-Path $caseRoot 'setup.log'
$uninstallLog = Join-Path $caseRoot 'uninstall.log'
$groupName = "TogetherServer Installer Smoke $id"
$shortcutRoot = Join-Path ([Environment]::GetFolderPath('Programs')) $groupName
$shortcut = Join-Path $shortcutRoot 'TogetherServer.lnk'
$defaultInstallRoot = Join-Path $caseRoot 'default installed app'
$defaultDataRoot = Join-Path $caseRoot 'default data'
$defaultSetupLog = Join-Path $caseRoot 'default-setup.log'
$defaultUninstallLog = Join-Path $caseRoot 'default-uninstall.log'
$defaultGroupName = "TogetherServer Installer Smoke Default $id"
$defaultShortcutRoot = Join-Path ([Environment]::GetFolderPath('Programs')) $defaultGroupName
$defaultShortcut = Join-Path $defaultShortcutRoot 'TogetherServer.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$baseUrl = 'http://127.0.0.1:5127'
$headers = @{ Origin = $baseUrl; 'X-TogetherServer-Local' = '1' }
$expectedHash = (Get-FileHash -LiteralPath $expectedApp -Algorithm SHA256).Hash
$installedApp = Join-Path $installRoot 'TogetherServer.exe'
$defaultInstalledApp = Join-Path $defaultInstallRoot 'TogetherServer.exe'
$passes = 0
$installed = $false
$uninstalled = $false
$defaultInstalled = $false
$defaultUninstalled = $false
$priorData = $env:TOGETHERSERVER_DATA_DIR

function Require([bool]$condition, [string]$message) { if (!$condition) { throw $message } }
function Pass([string]$message) { $script:passes++; Write-Host "PASS $message" }
function TestProcesses {
    return @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
            [IO.Path]::GetFullPath($caseRoot) + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)
    })
}
function UninstallEntries([string]$targetInstallRoot) {
    $roots = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
    $entries = foreach ($root in $roots) {
        if (Test-Path $root) { Get-ChildItem $root | Get-ItemProperty -ErrorAction SilentlyContinue }
    }
    return @($entries | Where-Object {
        $_.DisplayName -like 'TogetherServer*' -and $_.InstallLocation -and
        [IO.Path]::GetFullPath($_.InstallLocation).TrimEnd('\').Equals(
            [IO.Path]::GetFullPath($targetInstallRoot).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
    })
}
function AllTogetherServerUninstallEntries {
    $roots = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
    $entries = foreach ($root in $roots) {
        if (Test-Path $root) { Get-ChildItem $root | Get-ItemProperty -ErrorAction SilentlyContinue }
    }
    return @($entries | Where-Object { $_.DisplayName -like 'TogetherServer*' })
}
function Stop-TestApp {
    try {
        Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers -TimeoutSec 5 | Out-Null
    }
    catch { }
    for ($i = 0; $i -lt 120 -and (TestProcesses).Count -gt 0; $i++) { Start-Sleep -Milliseconds 100 }
    foreach ($process in @(TestProcesses)) {
        $path = [IO.Path]::GetFullPath($process.ExecutablePath)
        if ($path.StartsWith([IO.Path]::GetFullPath($caseRoot) + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}
function Run-Uninstaller([string]$targetInstallRoot, [string]$targetLog, [bool]$strict) {
    $uninstaller = Get-ChildItem -LiteralPath $targetInstallRoot -Filter 'unins*.exe' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (!$uninstaller) {
        if ($strict) { throw 'The installed app has no Inno Setup uninstaller.' }
        return
    }
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$targetLog`"")
    $process = Start-Process -FilePath $uninstaller.FullName -ArgumentList $arguments -PassThru -Wait
    if ($strict -and $process.ExitCode -ne 0) { throw "Uninstaller exited with $($process.ExitCode)." }
}
function Remove-SmokeShortcut([string]$targetRoot, [string]$expectedName) {
    if (!(Test-Path -LiteralPath $targetRoot -PathType Container)) { return }
    $resolvedRoot = (Resolve-Path -LiteralPath $targetRoot).Path
    $programs = [IO.Path]::GetFullPath([Environment]::GetFolderPath('Programs')).TrimEnd('\')
    if ((Split-Path -Parent $resolvedRoot) -eq $programs -and
        (Split-Path -Leaf $resolvedRoot) -eq $expectedName) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

try
{
    Require ((@(Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'TogetherServer*.exe' })).Count -eq 0) `
        'Installer smoke requires no production TogetherServer process to be running.'
    Require (!(Get-NetTCPConnection -State Listen -LocalPort 5127 -ErrorAction SilentlyContinue)) `
        'Installer smoke requires loopback port 5127 to be free.'
    Require (@(AllTogetherServerUninstallEntries).Count -eq 0) `
        'Installer smoke requires no existing installed TogetherServer copy; it never replaces a user installation.'
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null

    $env:TOGETHERSERVER_DATA_DIR = $defaultDataRoot
    $defaultSetupArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS',
        "/DIR=`"$defaultInstallRoot`"", "/GROUP=`"$defaultGroupName`"", "/LOG=`"$defaultSetupLog`"")
    $defaultSetup = Start-Process -FilePath $installer -ArgumentList $defaultSetupArguments -PassThru -Wait
    Require ($defaultSetup.ExitCode -eq 0) "Default installer exited with $($defaultSetup.ExitCode)."
    $defaultInstalled = $true
    Require ((Get-FileHash -LiteralPath $defaultInstalledApp -Algorithm SHA256).Hash -eq $expectedHash) `
        'Default install did not place the exact verified app bytes.'
    Require (Test-Path -LiteralPath $defaultShortcut -PathType Leaf) `
        'Default install did not create its Start-menu shortcut.'
    Require (@(UninstallEntries $defaultInstallRoot).Count -eq 1) `
        'Default install did not create its uninstall registration.'
    Require (!(Test-Path -LiteralPath $defaultDataRoot)) `
        'Skipping optional installer setup unexpectedly created app settings.'
    Require (!(Get-ItemProperty -Path $runKey -Name TogetherServer -ErrorAction SilentlyContinue)) `
        'Skipping optional installer setup unexpectedly enabled Windows startup.'
    Run-Uninstaller $defaultInstallRoot $defaultUninstallLog $true
    $defaultUninstalled = $true
    Require (!(Test-Path -LiteralPath $defaultInstalledApp)) 'Default uninstall left the installed app.'
    Require (!(Test-Path -LiteralPath $defaultShortcut)) 'Default uninstall left its Start-menu shortcut.'
    Require (@(UninstallEntries $defaultInstallRoot).Count -eq 0) `
        'Default uninstall left its registration.'
    Pass 'every optional setup choice can be skipped without blocking installation'

    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $env:TOGETHERSERVER_DATA_DIR = $dataRoot

    $setupArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS',
        "/DIR=`"$installRoot`"", "/GROUP=`"$groupName`"", "/LOG=`"$setupLog`"",
        '/INITIALMODE=friend', '/STARTWITHWINDOWS=1', '/CLOSETOTRAY=1')
    $setup = Start-Process -FilePath $installer -ArgumentList $setupArguments -PassThru -Wait
    Require ($setup.ExitCode -eq 0) "Installer exited with $($setup.ExitCode)."
    $installed = $true
    Require (Test-Path -LiteralPath $installedApp -PathType Leaf) 'Installer did not create TogetherServer.exe.'
    Require ((Get-FileHash -LiteralPath $installedApp -Algorithm SHA256).Hash -eq $expectedHash) `
        'Installed EXE bytes do not match the verified candidate.'
    Pass 'installer places the exact verified app bytes in a per-user destination'

    $mode = Get-Content -LiteralPath (Join-Path $dataRoot 'mode.json') -Raw | ConvertFrom-Json
    $desktop = Get-Content -LiteralPath (Join-Path $dataRoot 'desktop.json') -Raw | ConvertFrom-Json
    $startup = (Get-ItemProperty -Path $runKey -Name TogetherServer -ErrorAction Stop).TogetherServer
    Require ($mode -eq 'Friend') 'Optional installer role did not persist Friend mode.'
    Require ($desktop.closeToTray -eq $true) 'Optional installer preference did not enable close to tray.'
    Require ($startup -eq "`"$installedApp`" --startup") 'Optional installer preference registered the wrong startup command.'
    Pass 'optional installer choices persist mode, tray, and exact per-user startup command'

    Require (Test-Path -LiteralPath $shortcut -PathType Leaf) 'Start-menu shortcut was not created.'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    Require ([IO.Path]::GetFullPath($link.TargetPath).Equals([IO.Path]::GetFullPath($installedApp),
        [StringComparison]::OrdinalIgnoreCase)) 'Start-menu shortcut targets the wrong EXE.'
    $installedEntries = @(UninstallEntries $installRoot)
    Require ($installedEntries.Count -eq 1) `
        "Add or Remove Programs registration is missing or duplicated; found $($installedEntries.Count)."
    Pass 'Start-menu search shortcut and per-user uninstall registration are present'

    $app = Start-Process -FilePath $installedApp -ArgumentList '--startup' -WorkingDirectory $installRoot `
        -WindowStyle Hidden -PassThru
    $snapshot = $null
    for ($i = 0; $i -lt 160; $i++) {
        if ($app.HasExited) { throw "Installed app exited during startup with $($app.ExitCode)." }
        try { $snapshot = Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot" -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 100; continue }
        if ($snapshot.mode) { break }
    }
    Require ($snapshot.mode -eq 'Friend') 'Installed app did not open in the optional Friend starting mode.'
    $instance = Invoke-RestMethod -Uri "$baseUrl/api/local/instance" -TimeoutSec 5
    $preferences = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/preferences" -TimeoutSec 5
    Require ([IO.Path]::GetFullPath($instance.dataRoot).Equals([IO.Path]::GetFullPath($dataRoot),
        [StringComparison]::OrdinalIgnoreCase)) 'Installed app escaped the isolated smoke-test data root.'
    Require ($preferences.closeToTray -eq $true -and $preferences.launchAtLogin -eq $true) `
        'Installed app did not recognize its installer preferences.'
    Pass 'installed app launches with the selected non-blocking preferences'
    Stop-TestApp
    Require ((TestProcesses).Count -eq 0) 'Installed app did not quit cleanly before uninstall.'

    Run-Uninstaller $installRoot $uninstallLog $true
    $uninstalled = $true
    Require (!(Test-Path -LiteralPath $installedApp)) 'Uninstall left the installed TogetherServer EXE.'
    Require (!(Test-Path -LiteralPath $shortcut)) 'Uninstall left the Start-menu shortcut.'
    Require (@(UninstallEntries $installRoot).Count -eq 0) 'Uninstall left Add or Remove Programs registration.'
    $remainingStartup = (Get-ItemProperty -Path $runKey -Name TogetherServer -ErrorAction SilentlyContinue).TogetherServer
    Require (!$remainingStartup) 'Uninstall left its Windows startup registration.'
    Require ((Test-Path -LiteralPath (Join-Path $dataRoot 'mode.json')) -and
        (Test-Path -LiteralPath (Join-Path $dataRoot 'desktop.json'))) `
        'Uninstall deleted the user data that should be preserved.'
    Pass 'uninstall removes app integration but preserves settings and data'

    Require ((TestProcesses).Count -eq 0) 'Installer smoke left a TogetherServer test process running.'
    Write-Host "Installer checks: $passes passed, 0 failed. Isolated data: $caseRoot"
}
finally
{
    Stop-TestApp
    if ($installed -and !$uninstalled) {
        try { Run-Uninstaller $installRoot $uninstallLog $false } catch { }
    }
    if ($defaultInstalled -and !$defaultUninstalled) {
        try { Run-Uninstaller $defaultInstallRoot $defaultUninstallLog $false } catch { }
    }
    $startup = (Get-ItemProperty -Path $runKey -Name TogetherServer -ErrorAction SilentlyContinue).TogetherServer
    if ($startup -eq "`"$installedApp`" --startup") {
        Remove-ItemProperty -Path $runKey -Name TogetherServer -ErrorAction SilentlyContinue
    }
    Remove-SmokeShortcut $shortcutRoot $groupName
    Remove-SmokeShortcut $defaultShortcutRoot $defaultGroupName
    if ($null -eq $priorData) { Remove-Item Env:TOGETHERSERVER_DATA_DIR -ErrorAction SilentlyContinue }
    else { $env:TOGETHERSERVER_DATA_DIR = $priorData }
}
