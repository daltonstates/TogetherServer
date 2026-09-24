param([string]$AppPath = '', [int]$Port = 5127, [switch]$Interactive)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release/TogetherServer.exe' }
$appPath = (Resolve-Path -LiteralPath $AppPath).Path
$fixturePath = Join-Path $repository 'src/TogetherServer.ValheimFixture/bin/Release/net10.0/valheim_server.exe'
if (!(Test-Path -LiteralPath $appPath) -or !(Test-Path -LiteralPath $fixturePath)) { throw 'Run scripts/build.ps1 first.' }

# Routine runs stay in the tray so they do not interrupt the desktop. -Interactive exercises the visible
# no-argument Explorer path, custom window controls, sizing, and native file pickers.
if ($Port -eq 0) {
    $freePortProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $freePortProbe.Start()
    $Port = ([Net.IPEndPoint]$freePortProbe.LocalEndpoint).Port
    $freePortProbe.Stop()
}
$launchArguments = if (!$Interactive) {
    if ($Port -eq 5127) { @('--startup') } else { @('--startup', '--port', "$Port") }
} elseif ($Port -eq 5127) { @() } else { @('--desktop', '--port', "$Port") }
$launchLabel = if (!$Interactive) { 'background desktop EXE' }
    elseif ($launchArguments.Count -eq 0) { 'no-argument EXE' } else { 'isolated desktop EXE' }
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
$login = $null
$loginDuplicate = $null
$loginSecond = $null
$valheimRun = $null
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TogetherServerWindowCheck
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsZoomed(IntPtr window);
    public delegate bool EnumWindow(IntPtr window, IntPtr value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindow callback, IntPtr value);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int capacity);
    public static IntPtr FindDialog(int processId)
    {
        IntPtr dialog = IntPtr.Zero;
        EnumWindows((window, _) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != processId || !IsWindowVisible(window)) return true;
            var kind = new System.Text.StringBuilder(128);
            GetClassName(window, kind, kind.Capacity);
            if (kind.ToString() != "#32770") return true;
            dialog = window;
            return false;
        }, IntPtr.Zero);
        return dialog;
    }
}
'@
Add-Type -AssemblyName UIAutomationClient

function Get-ChromeButton($process, [string]$name) {
    $process.Refresh()
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-ChromeButton($process, [string]$name) {
    $button = $null
    for ($i = 0; $i -lt 30 -and $null -eq $button; $i++) {
        $button = Get-ChromeButton $process $name
        if ($null -eq $button) { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $button) { throw "Custom title-bar control was not found: $name" }
    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

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
    throw 'Could not reserve a free UDP port pair for the desktop smoke.'
}

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
    $lastWindow = $null
    for ($i = 0; $i -lt 200; $i++) {
        if ($process.HasExited) { throw 'The TogetherServer window process exited.' }
        try {
            $window = Invoke-RestMethod -Uri "$baseUrl/api/local/window" -TimeoutSec 2
            $lastWindow = $window
        }
        catch { Start-Sleep -Milliseconds 100; continue }
        Assert-WindowDiagnostics $window
        $process.Refresh()
        if ($window.visible -and $window.rendered -and $window.loadState -eq 'Rendered' -and
            !$window.loadErrorCode -and !$window.loadFailureKind -and !$window.loadFailureHResult -and
            $process.MainWindowHandle -ne [IntPtr]::Zero) {
            if (!$window.customChrome) { throw 'The desktop app did not use its custom borderless window frame.' }
            return
        }
        Start-Sleep -Milliseconds 100
    }
    $diagnostic = Format-WindowDiagnostic $lastWindow
    throw "The native TogetherServer window did not visibly render React ($diagnostic)."
}

function Wait-ForBackgroundWindow($process) {
    $lastWindow = $null
    for ($i = 0; $i -lt 200; $i++) {
        if ($process.HasExited) { throw 'The background TogetherServer process exited.' }
        try {
            $window = Invoke-RestMethod -Uri "$baseUrl/api/local/window" -TimeoutSec 2
            $lastWindow = $window
        }
        catch { Start-Sleep -Milliseconds 100; continue }
        Assert-WindowDiagnostics $window
        if (!$window.visible -and $window.rendered -and $window.loadState -eq 'Rendered' -and
            !$window.loadErrorCode -and !$window.loadFailureKind -and !$window.loadFailureHResult -and
            $window.customChrome) { return }
        Start-Sleep -Milliseconds 100
    }
    $diagnostic = Format-WindowDiagnostic $lastWindow
    throw "The background TogetherServer window did not render while hidden ($diagnostic)."
}

function Assert-WindowDiagnostics($window) {
    $knownStates = @('NotStarted', 'WindowStarting', 'WindowShown', 'CreatingEnvironment',
        'InitializingWebView', 'Navigating', 'ProbingRender', 'Rendered', 'Failed')
    $knownErrors = @('WindowInitializationFailed', 'WebViewRuntimeMissing', 'EnvironmentCreationFailed',
        'WebViewInitializationFailed', 'NavigationSetupFailed', 'NavigationFailed', 'RenderProbeTimedOut',
        'StartupTimedOut', 'InitializationFailed')
    $knownFailureKinds = @('Com', 'Unauthorized', 'InvalidOperation', 'Argument', 'IO', 'Unexpected')
    if ($knownStates -notcontains [string]$window.loadState) {
        throw "The desktop app returned an unknown load-state code: $($window.loadState)"
    }
    if ($window.loadErrorCode -and $knownErrors -notcontains [string]$window.loadErrorCode) {
        throw "The desktop app returned an unknown load-error code: $($window.loadErrorCode)"
    }
    if ($window.loadFailureKind -and $knownFailureKinds -notcontains [string]$window.loadFailureKind) {
        throw "The desktop app returned an unknown load-failure kind: $($window.loadFailureKind)"
    }
    if ($window.loadFailureHResult -and [string]$window.loadFailureHResult -notmatch '^[0-9A-F]{8}$') {
        throw "The desktop app returned an invalid load-failure HRESULT."
    }
    if ($window.loadState -eq 'Failed' -and
        [bool]$window.loadFailureKind -ne [bool]$window.loadFailureHResult) {
        throw "The desktop app returned incomplete load-failure diagnostics."
    }
}

function Format-WindowDiagnostic($window) {
    if ($null -eq $window) { return 'window status unavailable' }
    $errorCode = if ($window.loadErrorCode) { [string]$window.loadErrorCode } else { 'none' }
    $failureKind = if ($window.loadFailureKind) { [string]$window.loadFailureKind } else { 'none' }
    $failureHResult = if ($window.loadFailureHResult) { [string]$window.loadFailureHResult } else { 'none' }
    return "loadState=$($window.loadState), loadErrorCode=$errorCode, loadFailureKind=$failureKind, loadFailureHResult=$failureHResult"
}

try {
    $first = Start-Gui
    $state = Wait-ForGui $first
    if ($state.mode -ne 'Host') { throw 'First desktop launch did not default to Host mode.' }
    if ($Interactive) {
        Wait-ForWindow $first
        Write-Host "PASS $launchLabel opens a visible native window with custom chrome and rendered React"

        $originalRect = [TogetherServerWindowCheck+WindowRect]::new()
        if (![TogetherServerWindowCheck]::GetWindowRect($first.MainWindowHandle, [ref]$originalRect)) {
            throw 'Could not read the native window size.'
        }
        $windowDpi = [TogetherServerWindowCheck]::GetDpiForWindow($first.MainWindowHandle)
        if ($windowDpi -eq 0) { $windowDpi = 96 }
        $compactWidth = [int][Math]::Round(390 * $windowDpi / 96)
        $compactHeight = [int][Math]::Round(600 * $windowDpi / 96)
        if (![TogetherServerWindowCheck]::MoveWindow($first.MainWindowHandle, $originalRect.Left, $originalRect.Top,
            $compactWidth, $compactHeight, $true)) { throw 'Could not resize the native window.' }
        $compactRect = [TogetherServerWindowCheck+WindowRect]::new()
        for ($i = 0; $i -lt 30; $i++) {
            [TogetherServerWindowCheck]::GetWindowRect($first.MainWindowHandle, [ref]$compactRect) | Out-Null
            $actualWidth = $compactRect.Right - $compactRect.Left
            $actualHeight = $compactRect.Bottom - $compactRect.Top
            if ([Math]::Abs($actualWidth - $compactWidth) -le 8 -and [Math]::Abs($actualHeight - $compactHeight) -le 8) { break }
            Start-Sleep -Milliseconds 100
        }
        if ([Math]::Abs($actualWidth - $compactWidth) -gt 8 -or [Math]::Abs($actualHeight - $compactHeight) -gt 8) {
            throw "Native window rejected the compact size: requested ${compactWidth}x${compactHeight}, received ${actualWidth}x${actualHeight}."
        }
        [TogetherServerWindowCheck]::MoveWindow($first.MainWindowHandle, $originalRect.Left, $originalRect.Top,
            $originalRect.Right - $originalRect.Left, $originalRect.Bottom - $originalRect.Top, $true) | Out-Null
        Write-Host 'PASS native window accepts a compact 390x600 logical-pixel size'

        foreach ($control in @('Minimize TogetherServer', 'Maximize TogetherServer', 'Close TogetherServer')) {
            if ($null -eq (Get-ChromeButton $first $control)) { throw "Missing accessible title-bar control: $control" }
        }
        Invoke-ChromeButton $first 'Maximize TogetherServer'
        for ($i = 0; $i -lt 30 -and ![TogetherServerWindowCheck]::IsZoomed($first.MainWindowHandle); $i++) {
            Start-Sleep -Milliseconds 100
        }
        if (![TogetherServerWindowCheck]::IsZoomed($first.MainWindowHandle)) { throw 'Custom maximize did not maximize.' }
        Invoke-ChromeButton $first 'Restore TogetherServer'
        for ($i = 0; $i -lt 30 -and [TogetherServerWindowCheck]::IsZoomed($first.MainWindowHandle); $i++) {
            Start-Sleep -Milliseconds 100
        }
        if ([TogetherServerWindowCheck]::IsZoomed($first.MainWindowHandle)) { throw 'Custom restore did not restore.' }
        Invoke-ChromeButton $first 'Minimize TogetherServer'
        for ($i = 0; $i -lt 30 -and ![TogetherServerWindowCheck]::IsIconic($first.MainWindowHandle); $i++) {
            Start-Sleep -Milliseconds 100
        }
        if (![TogetherServerWindowCheck]::IsIconic($first.MainWindowHandle)) { throw 'Custom minimize did not minimize.' }
        [TogetherServerWindowCheck]::ShowWindow($first.MainWindowHandle, 9) | Out-Null
        Write-Host 'PASS custom minimize, maximize, and restore controls are accessible and functional'

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
                $popup = [TogetherServerWindowCheck]::FindDialog($first.Id)
                if ($windowState.fileDialogOpen -and $popup -ne [IntPtr]::Zero -and $popup -ne $first.MainWindowHandle) { break }
                Start-Sleep -Milliseconds 100
            }
            if (!$windowState.fileDialogOpen -or $popup -eq [IntPtr]::Zero -or $popup -eq $first.MainWindowHandle) {
                throw "Browse $pickerKind did not open a native file picker."
            }
            $popupTitle = [System.Text.StringBuilder]::new(256)
            $popupClass = [System.Text.StringBuilder]::new(256)
            [TogetherServerWindowCheck]::GetWindowText($popup, $popupTitle, $popupTitle.Capacity) | Out-Null
            [TogetherServerWindowCheck]::GetClassName($popup, $popupClass, $popupClass.Capacity) | Out-Null
            if (![TogetherServerWindowCheck]::PostMessage($popup, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'Could not cancel the native file picker.'
            }
            if (!(Wait-Job -Job $picker -Timeout 10)) {
                throw "Canceled $pickerKind picker did not return (popup class=$popupClass title=$popupTitle)."
            }
            $choice = Receive-Job -Job $picker
            if ($choice.code -ne 'Canceled') { throw "Canceled picker returned $($choice.code)." }
            Write-Host "PASS Browse $pickerKind opens and cancels a native Windows file picker"
        }
        finally { Stop-Job -Job $picker -ErrorAction SilentlyContinue; Remove-Job -Job $picker -Force -ErrorAction SilentlyContinue }
        }

        foreach ($browse in @(
        @{ kind = 'MinecraftJava'; target = 'jar' },
        @{ kind = 'MinecraftJava'; target = 'folder' },
        @{ kind = 'MinecraftBedrock'; target = 'executable' }
    )) {
        $picker = Start-Job -ArgumentList $baseUrl, $browse.kind, $browse.target -ScriptBlock {
            param($url, $kind, $target)
            Invoke-RestMethod -Uri "$url/api/local/minecraft/browse" -Method Post -ContentType 'application/json' -Body (@{ kind = $kind; target = $target } | ConvertTo-Json) -Headers @{ Origin = $url; 'X-TogetherServer-Local' = '1' } -TimeoutSec 30
        }
        try {
            $popup = [IntPtr]::Zero
            for ($i = 0; $i -lt 100; $i++) {
                $windowState = Invoke-RestMethod -Uri "$baseUrl/api/local/window" -TimeoutSec 2
                $first.Refresh()
                $popup = [TogetherServerWindowCheck]::FindDialog($first.Id)
                if ($windowState.fileDialogOpen -and $popup -ne [IntPtr]::Zero -and $popup -ne $first.MainWindowHandle) { break }
                Start-Sleep -Milliseconds 100
            }
            if (!$windowState.fileDialogOpen -or $popup -eq [IntPtr]::Zero -or $popup -eq $first.MainWindowHandle) {
                throw "Minecraft $($browse.kind) $($browse.target) did not open a native picker."
            }
            if (![TogetherServerWindowCheck]::PostMessage($popup, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw 'Could not cancel the Minecraft file picker.'
            }
            if (!(Wait-Job -Job $picker -Timeout 10)) { throw 'Canceled Minecraft picker did not return.' }
            $choice = Receive-Job -Job $picker
            if ($choice.code -ne 'Canceled') { throw "Canceled Minecraft picker returned $($choice.code)." }
            Write-Host "PASS Minecraft $($browse.kind) $($browse.target) opens and cancels a native Windows picker"
        }
        finally { Stop-Job -Job $picker -ErrorAction SilentlyContinue; Remove-Job -Job $picker -Force -ErrorAction SilentlyContinue }
        }
    }
    else {
        Wait-ForBackgroundWindow $first
        Write-Host "PASS $launchLabel renders React with custom chrome while hidden in the tray"
    }

    $saveRoot = Join-Path $caseRoot 'source-save'
    $saveFiles = Join-Path $saveRoot 'worlds_local'
    New-Item -ItemType Directory -Path $saveFiles -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $saveFiles 'fixture-world.db') -Value 'synthetic database'
    Set-Content -LiteralPath (Join-Path $saveFiles 'fixture-world.fwl') -Value 'synthetic metadata'
    $profileId = [guid]::NewGuid().ToString()
    $import = Invoke-RestMethod -Uri "$baseUrl/api/local/valheim/import" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{ profileId = $profileId; sourceSaveRoot = $saveRoot; worldId = 'fixture-world' } | ConvertTo-Json)
    if (!$import.ok) { throw 'Desktop synthetic world import failed.' }
    $profile = @{ id = $profileId; kind = 'Valheim'; name = 'Desktop fixture'; serverName = 'Fixture "Valheim"'; worldId = 'fixture-world'; worldDirectory = $import.worldDirectory; gamePort = (Get-FreeUdpPair); executablePath = $fixturePath }
    $settings = @{ maxConcurrentServers = 1; idleMinutes = 15; autoShutdownEnabled = $true; remoteControlsEnabled = $false; profiles = @($profile) }
    $saved = Invoke-RestMethod -Uri "$baseUrl/api/local/settings" -Method Put -Headers $headers -ContentType 'application/json' -Body ($settings | ConvertTo-Json -Depth 8)
    if (!$saved.ok) { throw 'Desktop synthetic Valheim settings failed.' }
    $password = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/password" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"password":"fixture-pass-123"}'
    if (!$password.ok) { throw 'Desktop synthetic Valheim password failed.' }
    $started = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/start" -Method Post -Headers $headers
    if (!$started.ok) { throw "Desktop synthetic Valheim launch failed: $($started.message)" }
    $blockedUpdate = Invoke-RestMethod -Uri "$baseUrl/api/local/update/install" -Method Post -Headers $headers
    if ($blockedUpdate.ok -or $blockedUpdate.code -ne 'ManagedRunPresent') { throw 'Updater tried to close the app while a managed game was running.' }
    Write-Host 'PASS updater refuses to replace the desktop EXE while a managed game is running'

    $preference = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/preferences" -Method Put -Headers $headers -ContentType 'application/json' -Body '{"closeToTray":true}'
    if (!$preference.ok -or !$preference.preferences.closeToTray) { throw 'Close-to-tray preference was not saved.' }
    if ($Interactive) {
        Invoke-ChromeButton $first 'Close TogetherServer'
        $hidden = $false
        for ($i = 0; $i -lt 40; $i++) {
            $windowState = Invoke-RestMethod -Uri "$baseUrl/api/local/window"
            if (!$windowState.visible) { $hidden = $true; break }
            Start-Sleep -Milliseconds 100
        }
        if (!$hidden -or $first.HasExited) { throw 'Closing to tray stopped the app instead of hiding the window.' }
    }
    else {
        $windowState = Invoke-RestMethod -Uri "$baseUrl/api/local/window"
        if ($windowState.visible -or $first.HasExited) { throw 'The background desktop run did not remain hidden.' }
    }
    $stillHosting = Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot"
    if (@($stillHosting.runs | Where-Object profileId -EQ $profileId).Count -ne 1) { throw 'The managed server disappeared while the window was hidden.' }
    $trayQuitBlocked = Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers
    if ($trayQuitBlocked.ok -or $trayQuitBlocked.code -ne 'ManagedRunPresent') { throw 'Quit bypassed the managed-server safety check while hidden.' }
    if ($Interactive) {
        $shownAgain = Invoke-RestMethod -Uri "$baseUrl/api/local/show" -Method Post -Headers $headers
        if (!$shownAgain.ok) { throw 'The hidden app did not reopen.' }
        Wait-ForWindow $first
        Write-Host 'PASS Close hides to tray while hosting; reopen and guarded Quit keep the managed server safe'
    }
    else { Write-Host 'PASS hidden tray run keeps the managed server active and guarded Quit remains enforced' }
    $valheimRun = (Get-Content (Join-Path $caseRoot 'runs.json') -Raw | ConvertFrom-Json) | Where-Object profileId -EQ $profileId
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        $health = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/health" -Method Post -Headers $headers
        if ($health.code -eq 'ValheimLogReady') { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Desktop synthetic server readiness was not observed.' }
    $desktopRun = $null
    for ($i = 0; $i -lt 50; $i++) {
        $desktopRun = @((Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot").runs) | Where-Object profileId -EQ $profileId
        if ($desktopRun.onlinePlayers -eq 0 -and $desktopRun.maxPlayers -eq 10 -and $null -ne $desktopRun.autoShutdownAtUtc) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($desktopRun.onlinePlayers -ne 0 -or $desktopRun.maxPlayers -ne 10 -or $null -eq $desktopRun.autoShutdownAtUtc) {
        throw 'Desktop GUI snapshot did not expose the synthetic Valheim 0 of 10 player count and shutdown deadline.'
    }
    $desktopDeadline = [DateTimeOffset]::Parse($desktopRun.autoShutdownAtUtc)
    $extended = Invoke-RestMethod -Uri "$baseUrl/api/local/profiles/$profileId/countdown/extend" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"minutes":45}'
    $extendedDeadline = [DateTimeOffset]::Parse((@($extended.snapshot.runs) | Where-Object profileId -EQ $profileId).autoShutdownAtUtc)
    if (!$extended.ok -or ($extendedDeadline - $desktopDeadline).TotalMinutes -lt 44.99) {
        throw 'Desktop Host could not extend the active empty-server countdown.'
    }
    Write-Host 'PASS desktop Host receives server count and extends its active countdown'
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

    if ($Interactive) {
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
        $preference = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/preferences" -Method Put -Headers $headers -ContentType 'application/json' -Body '{"closeToTray":false}'
        if (!$preference.ok -or $preference.preferences.closeToTray) { throw 'Close-to-tray could not be turned off from Friend mode.' }
        Invoke-ChromeButton $first 'Close TogetherServer'
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
        Write-Host 'PASS Friend mode persists; custom title-bar close and local Quit both exit'

        $login = Start-Process -FilePath $appPath -ArgumentList @('--startup', '--port', "$Port") -PassThru
        $state = Wait-ForGui $login
        if ($state.mode -ne 'Friend') { throw 'Windows startup launch lost the saved Friend page.' }
        $created = $false
        for ($i = 0; $i -lt 40; $i++) {
            if ($login.HasExited) { throw 'The Windows startup app exited unexpectedly.' }
            $windowState = Invoke-RestMethod -Uri "$baseUrl/api/local/window"
            if ($windowState.visible) { throw 'Windows startup opened a visible window instead of starting in the tray.' }
            if ($windowState.customChrome) { $created = $true }
            Start-Sleep -Milliseconds 100
        }
        if (!$created) { throw 'Windows startup did not create a native window for later reopening.' }
        $loginDuplicate = Start-Process -FilePath $appPath -ArgumentList @('--startup', '--port', "$Port") -PassThru
        if (!$loginDuplicate.WaitForExit(10000) -or $login.HasExited -or
            (Invoke-RestMethod -Uri "$baseUrl/api/local/window").visible) {
            throw 'A duplicate Windows startup launch opened the existing hidden window.'
        }
        $loginSecond = Start-Gui
        if (!$loginSecond.WaitForExit(10000) -or $login.HasExited) { throw 'A manual launch did not return to the app started in the tray.' }
        Wait-ForWindow $login
        $closed = Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers
        if (!$closed.ok -or !$login.WaitForExit(10000)) { throw 'Quit app did not close the login-started instance.' }
        Write-Host 'PASS Windows startup stays in tray; manual launch opens that same running app'
    }
    else {
        $second = Start-Gui
        if (!$second.WaitForExit(10000) -or $first.HasExited -or
            (Invoke-RestMethod -Uri "$baseUrl/api/local/window").visible) {
            throw 'A duplicate background launch opened the hidden window.'
        }
        Write-Host 'PASS duplicate background launch leaves the running window hidden'

        $mode = Invoke-RestMethod -Uri "$baseUrl/api/local/mode/friend" -Method Post -Headers $headers
        if (!$mode.ok -or (Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot").mode -ne 'Friend') {
            throw 'Friend mode selection failed.'
        }
        $closed = Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers
        if (!$closed.ok -or !$first.WaitForExit(10000)) { throw 'Quit app did not close the hidden Friend instance.' }

        $reopened = Start-Gui
        $state = Wait-ForGui $reopened
        Wait-ForBackgroundWindow $reopened
        if ($state.mode -ne 'Friend') { throw 'Friend mode was not restored in a background relaunch.' }
        $closed = Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers
        if (!$closed.ok -or !$reopened.WaitForExit(10000)) { throw 'Quit app did not close the background relaunch.' }
        Write-Host 'PASS Friend mode persists across hidden relaunches and local Quit exits cleanly'
    }
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
    foreach ($started in @($first, $second, $reopened, $login, $loginDuplicate, $loginSecond)) {
        if ($null -eq $started -or $started.HasExited) { continue }
        $running = Get-Process -Id $started.Id -ErrorAction SilentlyContinue
        if ($running -and $running.Path -eq $appPath -and $running.StartTime -eq $started.StartTime) {
            Stop-Process -Id $started.Id
        }
    }
    $env:TOGETHERSERVER_DATA_DIR = $oldTaskData
    $env:TOGETHERSERVER_FIXTURE_ROOT = $oldFixtureRoot
}
