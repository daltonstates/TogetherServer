param(
    [string]$AppPath = '',
    [switch]$AllowInteractiveTests,
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'
if ($WhatIf) {
    Write-Output 'SKIP ForegroundSafety: window chrome smoke dry run; no desktop query, input, app or native handle was used.'
    return
}
if (!$AllowInteractiveTests) {
    throw 'ForegroundSafety: window chrome smoke clicks the taskbar and tray and changes foreground focus. -AllowInteractiveTests requires explicit owner approval for a separate PC or dedicated unattended Windows session. Never run on the active owner desktop.'
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Window chrome smoke requires an approved Windows desktop session.' }

# One disposable DEVELOPMENT process, with its own EXE and data root. This test
# does not install anything, use a game, or open a companion/public listener.
$repository = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }
$candidate = (Resolve-Path -LiteralPath $AppPath).Path
$candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
$caseId = [guid]::NewGuid().ToString('N')
$caseRoot = [IO.Path]::GetFullPath((Join-Path $repository ('local-data/window-chrome-smoke/' + $caseId)))
$appRoot = Join-Path $caseRoot 'app'
$dataRoot = Join-Path $caseRoot 'data'
$evidenceRoot = Join-Path $repository ('local-data/ci-evidence/window-chrome/' + $caseId)
New-Item -ItemType Directory -Path $appRoot, $evidenceRoot -Force | Out-Null
$developmentApp = Join-Path $appRoot 'TogetherServer DEVELOPMENT.exe'
Copy-Item -LiteralPath $candidate -Destination $developmentApp
if ((Get-FileHash -LiteralPath $developmentApp -Algorithm SHA256).Hash -ne $candidateHash) { throw 'Candidate copy hash mismatch.' }
$listeners = @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | ForEach-Object Port)
$port = 0
for ($attempt = 0; $attempt -lt 100 -and $port -eq 0; $attempt++) {
    $value = Get-Random -Minimum 51000 -Maximum 60000
    if ($listeners -notcontains $value) { $port = $value }
}
if ($port -eq 0) { throw 'No unused disposable local app port found.' }
$baseUrl = "http://127.0.0.1:$port"
$headers = @{ Origin = $baseUrl; 'X-TogetherServer-Local' = '1' }
$displayName = 'TogetherServer DEVELOPMENT'
$processes = [Collections.Generic.List[object]]::new()
$results = [Collections.Generic.List[object]]::new()
$shellObservations = [Collections.Generic.List[object]]::new()
$windowGeometry = [Collections.Generic.List[object]]::new()
$app = $null
$failure = $null
$requiredScopeCompleted = $false

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class TogetherServerChromeSmoke {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    public delegate bool EnumWindow(IntPtr window, IntPtr value);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindow callback, IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder value, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string kind, string name);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect value);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect value);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] public static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public static bool GetClientScreenRect(IntPtr window, out Rect value) {
        if (!GetClientRect(window, out value)) return false;
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) return false;
        value.Left += origin.X; value.Right += origin.X;
        value.Top += origin.Y; value.Bottom += origin.Y;
        return true;
    }
    public static IntPtr WindowFor(int processId, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner != processId) return true;
            var text = new StringBuilder(256); GetWindowText(window, text, text.Capacity);
            if (text.ToString() != title) return true;
            found = window; return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Wait-Until([scriptblock]$Condition, [string]$Message) {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    }
    throw $Message
}
function App-Handle {
    Require ($null -ne $app -and !$app.HasExited) 'The disposable app exited unexpectedly.'
    $handle = [TogetherServerChromeSmoke]::WindowFor($app.Id, $displayName)
    Require ($handle -ne [IntPtr]::Zero) 'The disposable app native window was not found.'
    return $handle
}
function Start-App {
    $start = [Diagnostics.ProcessStartInfo]::new($developmentApp)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Arguments = "--desktop --staging --port $port"
    $start.EnvironmentVariables['TOGETHERSERVER_STAGING_DATA_DIR'] = $dataRoot
    $started = [Diagnostics.Process]::Start($start)
    $processes.Add([pscustomobject]@{ Process = $started; StartTime = $started.StartTime.ToUniversalTime() })
    return $started
}
function Local-Post([string]$Path, $Body = $null, [string]$Method = 'POST') {
    $parameters = @{ Uri = $baseUrl + $Path; Method = $Method; Headers = $headers; TimeoutSec = 10 }
    if ($null -ne $Body) { $parameters.ContentType = 'application/json'; $parameters.Body = $Body | ConvertTo-Json -Compress }
    return Invoke-RestMethod @parameters
}
function Chrome-Button([string]$Action) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle((App-Handle))
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, "$Action $displayName")
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-Chrome([string]$Action) {
    $button = $null
    Wait-Until { $script:chromeButton = Chrome-Button $Action; return $null -ne $script:chromeButton } "Missing $Action caption button."
    $button = $script:chromeButton
    ($button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Click-Element($Element, [switch]$Double) {
    $rectangle = $Element.Current.BoundingRectangle
    Require (!$rectangle.IsEmpty -and $rectangle.Width -gt 0 -and $rectangle.Height -gt 0) 'Shell button has no clickable bounds.'
    Require ([TogetherServerChromeSmoke]::SetCursorPos([int]($rectangle.Left + $rectangle.Width / 2),
        [int]($rectangle.Top + $rectangle.Height / 2))) 'Could not position the approved smoke input.'
    for ($click = 0; $click -lt $(if ($Double) { 2 } else { 1 }); $click++) {
        [TogetherServerChromeSmoke]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [TogetherServerChromeSmoke]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        if ($Double -and $click -eq 0) { Start-Sleep -Milliseconds 80 }
    }
}
function Shell-Elements([IntPtr]$Handle) {
    if ($Handle -eq [IntPtr]::Zero) { return @() }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    return @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition))
}
function Shell-Buttons([IntPtr]$Handle) {
    return @(Shell-Elements $Handle | Where-Object {
        $_.Current.ControlType -in @([System.Windows.Automation.ControlType]::Button,
            [System.Windows.Automation.ControlType]::ListItem, [System.Windows.Automation.ControlType]::TabItem) -or
        ($_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Custom -and
            $_.Current.ClassName -match 'TaskListButton|TaskbarButton|NotifyItem')
    })
}
function Explorer-Elements {
    $taskbar = [TogetherServerChromeSmoke]::FindWindow('Shell_TrayWnd', $null)
    if ($taskbar -eq [IntPtr]::Zero) { return @() }
    $shellProcess = [uint32]0
    [TogetherServerChromeSmoke]::GetWindowThreadProcessId($taskbar, [ref]$shellProcess) | Out-Null
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$shellProcess)
    # Modern notification overflow may be a separate XAML popup with a class
    # that differs from legacy NotifyIconOverflowWindow. Stay in Explorer's
    # process while inspecting its desktop UIA descendants.
    return @([System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition))
}
function Safe-ShellText([string]$Text) {
    $safe = $Text -replace '[\x00-\x1f\x7f]', ' '
    $safe = $safe -replace '(?i)(?:https?://|[a-z]:\\|\\\\)\S+', '[private location]'
    $safe = $safe -replace '(?i)\b(?:gh[pousr]_[a-z0-9_]+|github_pat_[a-z0-9_]+|(?:bearer|token|password|secret)[=: ]+\S+)', '[redacted]'
    if ($safe.Length -gt 180) { $safe = $safe.Substring(0, 180) }
    return $safe
}
function Shell-Description($Element) {
    $current = $Element.Current
    $rectangle = $current.BoundingRectangle
    return [pscustomobject]@{
        name = Safe-ShellText $current.Name
        type = $current.ControlType.ProgrammaticName
        className = Safe-ShellText $current.ClassName
        automationId = Safe-ShellText $current.AutomationId
        offscreen = $current.IsOffscreen
        bounds = @($rectangle.Left, $rectangle.Top, $rectangle.Width, $rectangle.Height)
    }
}
function Save-ShellDiagnostics {
    $shells = [Collections.Generic.List[object]]::new()
    foreach ($kind in @('Shell_TrayWnd', 'Shell_SecondaryTrayWnd', 'NotifyIconOverflowWindow', 'TopLevelWindowForOverflowXamlIsland')) {
        $handle = [TogetherServerChromeSmoke]::FindWindow($kind, $null)
        if ($handle -eq [IntPtr]::Zero) { continue }
        $items = [Collections.Generic.List[object]]::new()
        try {
            foreach ($element in @(Shell-Elements $handle | Select-Object -First 300)) {
                try { $items.Add((Shell-Description $element)) }
                catch { $items.Add([pscustomobject]@{ error = $_.Exception.GetType().Name }) }
            }
            $shells.Add([pscustomobject]@{ windowClass = $kind; descendants = $items })
        } catch { $shells.Add([pscustomobject]@{ windowClass = $kind; error = $_.Exception.GetType().Name }) }
    }
    $explorerItems = [Collections.Generic.List[object]]::new()
    try {
        foreach ($element in @(Explorer-Elements | Select-Object -First 300)) {
            try { $explorerItems.Add((Shell-Description $element)) }
            catch { $explorerItems.Add([pscustomobject]@{ error = $_.Exception.GetType().Name }) }
        }
        $shells.Add([pscustomobject]@{ windowClass = 'Explorer process desktop UIA descendants'; descendants = $explorerItems })
    } catch { $shells.Add([pscustomobject]@{ windowClass = 'Explorer process desktop UIA descendants'; error = $_.Exception.GetType().Name }) }
    [ordered]@{ observations = $shellObservations; shells = $shells } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'shell-diagnostics.json') -Encoding UTF8
    foreach ($shell in $shells) {
        Write-Host "Shell diagnostic $($shell.windowClass): $(@($shell.descendants).Count) descendants"
        foreach ($item in @($shell.descendants | Where-Object { $_.name } | Select-Object -First 40)) {
            Write-Host "  $($item.type) | $($item.className) | $($item.name)"
        }
    }
}
function Taskbar-Button {
    $taskbar = [TogetherServerChromeSmoke]::FindWindow('Shell_TrayWnd', $null)
    Require ($taskbar -ne [IntPtr]::Zero) 'Explorer taskbar is unavailable; direct taskbar acceptance cannot run in this session.'
    $registration = [Diagnostics.Stopwatch]::StartNew()
    $polls = 0
    $lastCount = 0
    $lastError = $null
    do {
        $polls++
        try {
            $buttons = @(Shell-Buttons $taskbar | Where-Object {
                $current = $_.Current
                $rectangle = $current.BoundingRectangle
                # Windows may use the EXE's product name for the taskbar label
                # rather than its full native staging title. The disposable
                # runner has one TogetherServer instance; ambiguity still fails.
                $current.Name.IndexOf('TogetherServer', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                    !$current.Name.Contains('Hosting:') -and !$current.IsOffscreen -and
                    !$rectangle.IsEmpty -and $rectangle.Width -gt 0 -and $rectangle.Height -gt 0
            })
            $exact = @($buttons | Where-Object { $_.Current.Name.Contains($displayName) })
            if ($exact.Count -gt 0) { $buttons = $exact }
            $lastCount = $buttons.Count
            if ($buttons.Count -eq 1) {
                $description = Shell-Description $buttons[0]
                $shellObservations.Add([pscustomobject]@{ action = 'Taskbar button discovered'; polls = $polls;
                    elapsedMilliseconds = $registration.ElapsedMilliseconds; element = $description })
                Write-Host "Taskbar item after $polls polls: $($description.type) | $($description.className) | $($description.name)"
                return $buttons[0]
            }
        } catch { $lastError = $_.Exception.GetType().Name }
        Start-Sleep -Milliseconds 250
    } while ($registration.ElapsedMilliseconds -lt 15000)
    $shellObservations.Add([pscustomobject]@{ action = 'Taskbar discovery failed'; polls = $polls;
        elapsedMilliseconds = $registration.ElapsedMilliseconds; candidates = $lastCount; error = $lastError })
    throw "Expected one disposable app taskbar item after bounded shell registration wait, found $lastCount (polls=$polls, error=$lastError)."
}
function Visible-TrayIcons {
    $buttonCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    # The opened overflow can be hosted outside the taskbar window or by a
    # separate shell process. Match its real visible button from the desktop
    # UIA tree instead of assuming a popup window class or owner process.
    $buttons = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    return @($buttons | Where-Object {
        $current = $_.Current
        $rectangle = $current.BoundingRectangle
        $current.Name.Contains($displayName) -and $current.ClassName -notmatch 'TaskList' -and
            !$current.IsOffscreen -and !$rectangle.IsEmpty -and $rectangle.Width -gt 0 -and $rectangle.Height -gt 0
    })
}
function Tray-Discovery {
    $taskbar = [TogetherServerChromeSmoke]::FindWindow('Shell_TrayWnd', $null)
    $icon = @(Visible-TrayIcons)
    if ($icon.Count -eq 0) {
        $buttons = @(Shell-Buttons $taskbar)
        $expand = @($buttons | Where-Object { $_.Current.Name -match '^(Show hidden icons|Hidden icon menu)' })
        if ($expand.Count -ne 1) {
            return [pscustomobject]@{ available = $false; reason = 'Explorer did not expose a unique notification overflow control or the disposable tray icon.'; element = $null }
        }
        Click-Element $expand[0]
        $wait = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 250
            $icon = @(Visible-TrayIcons)
            if ($icon.Count -gt 0) { break }
        } while ($wait.ElapsedMilliseconds -lt 8000)
    }
    if ($icon.Count -ne 1) {
        return [pscustomobject]@{ available = $false; reason = "Explorer did not expose a unique visible disposable tray icon after opening notification overflow (found $($icon.Count))."; element = $null }
    }
    $description = Shell-Description $icon[0]
    $shellObservations.Add([pscustomobject]@{ action = 'Tray icon discovered'; element = $description })
    return [pscustomobject]@{ available = $true; reason = $null; element = $icon[0] }
}
function Read-Rect {
    $rectangle = [TogetherServerChromeSmoke+Rect]::new()
    Require ([TogetherServerChromeSmoke]::GetWindowRect((App-Handle), [ref]$rectangle)) 'Could not read disposable window bounds.'
    return $rectangle
}
function Read-ClientRect {
    $rectangle = [TogetherServerChromeSmoke+Rect]::new()
    Require ([TogetherServerChromeSmoke]::GetClientScreenRect((App-Handle), [ref]$rectangle)) 'Could not read disposable client screen bounds.'
    return $rectangle
}
function Same-Rect($Expected) {
    $actual = Read-Rect
    return [Math]::Abs($actual.Left - $Expected.Left) -le 2 -and [Math]::Abs($actual.Top - $Expected.Top) -le 2 -and
        [Math]::Abs($actual.Right - $Expected.Right) -le 2 -and [Math]::Abs($actual.Bottom - $Expected.Bottom) -le 2
}
function Same-ClientRect($Expected) {
    $actual = Read-ClientRect
    return [Math]::Abs($actual.Left - $Expected.Left) -le 2 -and [Math]::Abs($actual.Top - $Expected.Top) -le 2 -and
        [Math]::Abs($actual.Right - $Expected.Right) -le 2 -and [Math]::Abs($actual.Bottom - $Expected.Bottom) -le 2
}
function Record-Pass([string]$Name) {
    $results.Add([pscustomobject]@{ name = $Name; status = 'PASS'; dpi = [TogetherServerChromeSmoke]::GetDpiForWindow((App-Handle)) })
    Write-Host "PASS $Name"
}
function Record-Skip([string]$Name, [string]$Reason) {
    $results.Add([pscustomobject]@{ name = $Name; status = 'SKIP'; reason = $Reason })
    Write-Host ('SKIP {0}: {1}' -f $Name, $Reason)
}
function Capture-Chrome([string]$Name) {
    $rectangle = Read-ClientRect
    $width = $rectangle.Right - $rectangle.Left
    $height = [int][Math]::Ceiling(44 * [TogetherServerChromeSmoke]::GetDpiForWindow((App-Handle)) / 96)
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rectangle.Left, $rectangle.Top, 0, 0, [Drawing.Size]::new($width, $height))
        $bitmap.Save((Join-Path $evidenceRoot ($Name + '.png')), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Capture-DesktopFailure([string]$Name = 'failure-desktop') {
    $rectangle = [Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap = [Drawing.Bitmap]::new($rectangle.Width, $rectangle.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rectangle.Left, $rectangle.Top, 0, 0, $rectangle.Size)
        $bitmap.Save((Join-Path $evidenceRoot ($Name + '.png')), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

try {
    $app = Start-App
    Wait-Until {
        if ($app.HasExited) { throw 'Disposable native app exited before rendering.' }
        try {
            $window = Invoke-RestMethod "$baseUrl/api/local/window" -TimeoutSec 2
            return $window.available -and $window.customChrome -and $window.visible -and $window.rendered
        } catch { return $false }
    } 'Disposable custom native window did not render.'
    $instance = Invoke-RestMethod "$baseUrl/api/local/instance" -TimeoutSec 2
    Require ($instance.isStaging -and [IO.Path]::GetFullPath($instance.dataRoot) -eq $dataRoot) 'Disposable staging identity/data mismatch.'
    $handle = App-Handle
    $style = [TogetherServerChromeSmoke]::GetWindowLong($handle, -16)
    Require (($style -band 0x00CF0000) -eq 0x00CF0000) 'Native shell/resize/caption window styles are missing.'
    $client = [TogetherServerChromeSmoke+Rect]::new()
    Require ([TogetherServerChromeSmoke]::GetClientRect($handle, [ref]$client)) 'Could not read native client bounds.'
    $normal = Read-Rect
    Require ([Math]::Abs(($normal.Right - $normal.Left) - $client.Right) -le 2 -and
        [Math]::Abs(($normal.Bottom - $normal.Top) - $client.Bottom) -le 2) 'The standard native frame was not removed.'
    Capture-Chrome 'normal-caption'
    Record-Pass 'Custom client frame and accessible equal-size caption controls'

    foreach ($maximized in @($false, $true)) {
        if ($maximized) { Invoke-Chrome 'Maximize' } else { Require ((Local-Post '/api/local/show').ok) 'Could not reveal native app.' }
        Wait-Until { return [TogetherServerChromeSmoke]::IsZoomed((App-Handle)) -eq $maximized } 'Expected starting window state was not reached.'
        Require ((Local-Post '/api/local/show').ok) 'Could not foreground the disposable app.'
        Wait-Until { return [TogetherServerChromeSmoke]::GetForegroundWindow() -eq (App-Handle) } 'App was not foreground before taskbar minimize.'
        $before = Read-Rect
        $clientBefore = Read-ClientRect
        $label = if ($maximized) { 'Maximized' } else { 'Normal' }
        $screen = [Windows.Forms.Screen]::FromHandle((App-Handle))
        $area = $screen.WorkingArea
        $bounds = $screen.Bounds
        $geometry = [pscustomobject]@{ phase = "$label before taskbar click";
            window = @($before.Left, $before.Top, $before.Right, $before.Bottom);
            clientScreen = @($clientBefore.Left, $clientBefore.Top, $clientBefore.Right, $clientBefore.Bottom);
            workArea = @($area.Left, $area.Top, $area.Right, $area.Bottom);
            monitor = @($bounds.Left, $bounds.Top, $bounds.Right, $bounds.Bottom);
            dpi = [TogetherServerChromeSmoke]::GetDpiForWindow((App-Handle)) }
        $windowGeometry.Add($geometry)
        if ($maximized) {
            # GetWindowRect can include Windows' invisible maximized resize
            # frame. The actual client screen rectangle must exactly fill the
            # work area so the titlebar/content cannot be clipped or behind it.
            Require ([Math]::Abs($clientBefore.Left - $area.Left) -le 2 -and [Math]::Abs($clientBefore.Top - $area.Top) -le 2 -and
                [Math]::Abs($clientBefore.Right - $area.Right) -le 2 -and [Math]::Abs($clientBefore.Bottom - $area.Bottom) -le 2) (
                    "Maximized client content escaped the monitor work area: client=[$($geometry.clientScreen -join ',')], rawWindow=[$($geometry.window -join ',')], workArea=[$($geometry.workArea -join ',')], monitor=[$($geometry.monitor -join ',')], dpi=$($geometry.dpi).")
            Capture-Chrome 'maximized-caption'
        }
        Click-Element (Taskbar-Button)
        Wait-Until { return [TogetherServerChromeSmoke]::IsIconic((App-Handle)) } "$label taskbar click did not minimize the active app."
        Click-Element (Taskbar-Button)
        Wait-Until { return ![TogetherServerChromeSmoke]::IsIconic((App-Handle)) -and
            [TogetherServerChromeSmoke]::IsZoomed((App-Handle)) -eq $maximized -and (Same-Rect $before) -and (Same-ClientRect $clientBefore) } "$label taskbar click did not restore its prior state/window/client bounds."
        Record-Pass "$label taskbar click minimizes and restores prior state/bounds"

        Invoke-Chrome 'Minimize'
        Wait-Until { return [TogetherServerChromeSmoke]::IsIconic((App-Handle)) } 'Caption minimize did not take effect.'
        Require ((Local-Post '/api/local/show').ok) 'ShowAsync reveal failed.'
        Wait-Until { return ![TogetherServerChromeSmoke]::IsIconic((App-Handle)) -and
            [TogetherServerChromeSmoke]::IsZoomed((App-Handle)) -eq $maximized -and (Same-Rect $before) -and (Same-ClientRect $clientBefore) } 'ShowAsync lost the pre-minimize state/window/client bounds.'
        Record-Pass "$label caption minimize and ShowAsync preserve state/bounds"

        Invoke-Chrome 'Minimize'
        Wait-Until { return [TogetherServerChromeSmoke]::IsIconic((App-Handle)) } 'Caption minimize did not take effect before duplicate launch.'
        $duplicate = Start-App
        Require ($duplicate.WaitForExit(15000) -and !$app.HasExited) 'Duplicate launch did not hand off to the recorded instance.'
        Wait-Until { return ![TogetherServerChromeSmoke]::IsIconic((App-Handle)) -and
            [TogetherServerChromeSmoke]::IsZoomed((App-Handle)) -eq $maximized -and (Same-Rect $before) -and (Same-ClientRect $clientBefore) } 'Single-instance reveal lost prior state/window/client bounds.'
        Record-Pass "$label single-instance reveal preserves state/bounds"
    }
    Invoke-Chrome 'Restore'
    Wait-Until { return ![TogetherServerChromeSmoke]::IsZoomed((App-Handle)) -and (Same-Rect $normal) } 'Maximized taskbar journeys lost normal restore bounds.'
    Record-Pass 'Restore after maximized taskbar journeys keeps the original normal bounds'
    $requiredScopeCompleted = $true

    # Requested taskbar/caption/reveal acceptance is complete before inspecting
    # notification overflow. Missing runner tray UI is a separately recorded
    # skip; a real icon click that loses state/bounds remains a test failure.
    $trayUnavailableReason = $null
    foreach ($maximized in @($false, $true)) {
        $label = if ($maximized) { 'Maximized' } else { 'Normal' }
        $trayCase = "$label close-to-tray and tray reveal preserve state/bounds"
        if ($null -ne $trayUnavailableReason) { Record-Skip $trayCase $trayUnavailableReason; continue }
        if ($maximized) { Invoke-Chrome 'Maximize' }
        Wait-Until { return [TogetherServerChromeSmoke]::IsZoomed((App-Handle)) -eq $maximized } 'Optional tray case did not reach its starting window state.'
        $before = Read-Rect
        $clientBefore = Read-ClientRect
        Require ((Local-Post '/api/local/desktop/preferences' @{ closeToTray = $true } 'PUT').ok) 'Disposable close-to-tray setting failed.'
        Invoke-Chrome 'Close'
        Wait-Until { return ![TogetherServerChromeSmoke]::IsWindowVisible((App-Handle)) } 'Caption Close did not hide to tray.'
        try { $tray = Tray-Discovery }
        catch {
            $tray = [pscustomobject]@{ available = $false; reason = "Runner Explorer tray discovery was unavailable ($($_.Exception.GetType().Name))."; element = $null }
        }
        if (!$tray.available) {
            $trayUnavailableReason = $tray.reason
            Record-Skip $trayCase $trayUnavailableReason
            try { Save-ShellDiagnostics } catch { Write-Host "Optional tray diagnostics unavailable: $($_.Exception.GetType().Name)" }
            try { Capture-DesktopFailure 'tray-unavailable-desktop' } catch { Write-Host "Optional tray desktop capture unavailable: $($_.Exception.GetType().Name)" }
            Require ((Local-Post '/api/local/show').ok) 'Could not reveal disposable app after unavailable runner tray UI.'
            Wait-Until { return [TogetherServerChromeSmoke]::IsWindowVisible((App-Handle)) -and
                [TogetherServerChromeSmoke]::IsZoomed((App-Handle)) -eq $maximized -and (Same-Rect $before) -and (Same-ClientRect $clientBefore) } 'ShowAsync after tray UI skip lost state/window/client bounds.'
            continue
        }
        Click-Element $tray.element -Double
        Wait-Until { return [TogetherServerChromeSmoke]::IsWindowVisible((App-Handle)) -and
            [TogetherServerChromeSmoke]::IsZoomed((App-Handle)) -eq $maximized -and (Same-Rect $before) -and (Same-ClientRect $clientBefore) } 'Tray double-click lost prior state/window/client bounds.'
        Record-Pass $trayCase
    }
    if ([TogetherServerChromeSmoke]::IsZoomed((App-Handle))) { Invoke-Chrome 'Restore' }
    Wait-Until { return Same-Rect $normal } 'Optional tray journeys lost original normal restore bounds.'
    Require ((Local-Post '/api/local/quit').ok) 'Disposable native app refused safe Quit.'
    Require ($app.WaitForExit(15000)) 'Disposable app did not exit after safe Quit.'
} catch {
    $failure = $_
    $results.Add([pscustomobject]@{ name = 'Native window chrome acceptance'; status = 'FAIL'; reason = $_.Exception.Message })
    try { Save-ShellDiagnostics } catch { Write-Host "Shell diagnostics unavailable: $($_.Exception.GetType().Name)" }
    try { Capture-DesktopFailure } catch { Write-Host "Failure desktop capture unavailable: $($_.Exception.GetType().Name)" }
} finally {
    foreach ($tracked in $processes) {
        $process = $tracked.Process
        if ($process.HasExited) { continue }
        $process.Refresh()
        if ($process.StartTime.ToUniversalTime() -eq $tracked.StartTime -and
            [IO.Path]::GetFullPath($process.MainModule.FileName).Equals($developmentApp, [StringComparison]::OrdinalIgnoreCase)) {
            $process.Kill()
            $process.WaitForExit(10000) | Out-Null
        } else { throw 'Disposable cleanup refused a changed process identity.' }
    }
    [ordered]@{ candidateSha256 = $candidateHash; candidate = $candidate; caseId = $caseId;
        source = 'Approved separate Windows desktop only'; results = $results; shellObservations = $shellObservations; windowGeometry = $windowGeometry;
        requiredScope = 'Taskbar, caption, ShowAsync and single-instance normal/maximized restoration';
        requiredScopeCompleted = $requiredScopeCompleted; skipped = @($results | Where-Object status -eq 'SKIP').Count; passed = $null -eq $failure } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'result.json') -Encoding UTF8
    Write-Host "Window chrome evidence: $evidenceRoot"
}
if ($null -ne $failure) { throw $failure }
