# Imported only by the explicitly opted-in QoL desktop smoke. Nothing here is a
# notification simulator: shell discovery, screenshots and clicks use Windows.
if (!$AllowInteractiveTests) { throw 'ForegroundSafety: native shell acceptance requires the guarded dedicated-session smoke.' }
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class TogetherServerQolShellCheck {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindow(IntPtr window, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr value);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public static string WindowClass(IntPtr window) {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }
    public static int[] WindowBounds(IntPtr window) {
        return GetWindowRect(window, out var rect)
            ? new[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top }
            : Array.Empty<int>();
    }
    public static IntPtr[] VisibleTopLevelWindows() {
        var values = new List<IntPtr>();
        var count = 0;
        var exceeded = false;
        EnumWindows((window, _) => {
            if (++count > 512) { exceeded = true; return false; }
            if (IsWindowVisible(window)) values.Add(window);
            return true;
        }, IntPtr.Zero);
        if (exceeded) throw new InvalidOperationException("The dedicated desktop window count exceeded its bound.");
        return values.ToArray();
    }
    public static IntPtr[] ShellWindows() {
        var values = new List<IntPtr>();
        var count = 0;
        var exceeded = false;
        EnumWindows((window, _) => {
            if (++count > 512) { exceeded = true; return false; }
            if (!IsWindowVisible(window)) return true;
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            switch (name.ToString()) {
                case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd": case "NotifyIconOverflowWindow":
                case "TopLevelWindowForOverflowXamlIsland": case "tooltips_class32":
                case "Windows.UI.Core.CoreWindow": case "Xaml_WindowedPopupClass":
                case "XamlExplorerHostIslandWindow": case "DesktopWindowXamlSource":
                case "Windows.UI.Composition.DesktopWindowContentBridge":
                    values.Add(window); break;
            }
            return true;
        }, IntPtr.Zero);
        if (exceeded) throw new InvalidOperationException("The dedicated desktop shell window count exceeded its bound.");
        return values.ToArray();
    }
}
'@
$nativeShellProof = [ordered]@{
    notification = [ordered]@{ systemAllowsNotifications = $false; systemState = 'Unknown'; systemStatesObserved = @();
        stateReadinessPolls = 0; stateReadinessElapsedMs = 0; freshTitleAndBodyObserved = $false;
        systemStateBeforeCaptionClick = $null; systemStateAfterCaptionClick = $null;
        systemAllowsBeforeCaptionClick = $false; systemAllowsAfterCaptionClick = $false;
        captionClickSent = $false; captionClickExactOwnedHit = $false; captionClickForegroundVerified = $false;
        captionClickNormalBoundsPreserved = $false; captionBoundsBefore = @(); captionBoundsAfter = @();
        osUiSetup = [ordered]@{ attempted = $false; status = 'NotRepeated'; priorRunId = '39398164189'; priorResult = 'QuietTime';
            stateBefore = $null; stateAfter = $null;
            binaryVerified = $false; ownedProcessVerified = $false; ownedWindowVerified = $false;
            controls = @(); failureCode = $null; cleanupCompleted = $true; appRestored = $false };
        physicalClickSent = $false; exactAppForeground = $false; nativeDestinationVerified = $false;
        searchPolls = 0; shellRootsObserved = 0; matchingTitlesObserved = 0;
        shellObservations = @(); shellObservationsTruncated = $false; lastShellObservation = $null;
        shellWindowMetadata = @(); shellWindowMetadataTruncated = $false; shellWindowMetadataReadFailures = 0;
        postTriggerStates = @(); postTriggerStatesTruncated = $false; failureStateReadFailed = $false }
    trayQuit = [ordered]@{ exactTrayIconObserved = $false; overflowOpened = $false; ownedQuitMenuObserved = $false;
        menuInvoked = $false; nativeFlushObserved = $false; exactProcessExited = $false;
        searchPolls = 0; shellRootsObserved = 0; iconCandidates = 0; overflowCandidates = 0 }
}
$qolShellDisplayName = 'TogetherServer DEVELOPMENT'
$qolBalloonTitle = $qolShellDisplayName + ' is still running'
$qolBalloonBody = 'Open it from the tray icon. Right-click the icon to quit.'
$qolShellRootContexts = @{}
$qolShellDiagnosticKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$qolShellWindowDiagnosticKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$qolDeliveryWaitActive = $false

function Wait-QolNotificationReadiness($InitialState) {
    # UIA/posted window actions do not establish physical session input. On the
    # disposable hosted desktop, click the exact native caption once; observe
    # the unchanged production notification gate rather than overriding it.
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $state = $InitialState
    $proof = $nativeShellProof.notification
    $captionStateRecorded = $false
    do {
        Assert-AppIdentity
        $systemState = [string]$state.systemState
        Require ($systemState -in @('Unknown', 'NotPresent', 'Busy', 'RunningD3DFullScreen', 'PresentationMode',
            'AcceptsNotifications', 'QuietTime', 'App') -and $state.systemAllowsNotifications -is [bool]) 'The app returned an invalid Windows notification state.'
        $allows = $state.systemAllowsNotifications -eq $true
        Require ($allows -eq ($systemState -eq 'AcceptsNotifications')) 'The Windows notification state and capability disagree.'
        $proof.systemAllowsNotifications = $allows
        $proof.systemState = $systemState
        if ($systemState -notin $proof.systemStatesObserved) { $proof.systemStatesObserved += $systemState }
        $proof.stateReadinessPolls++
        $proof.stateReadinessElapsedMs = $clock.ElapsedMilliseconds
        if (!$captionStateRecorded -and $env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_OS -eq 'Windows') {
            $proof.systemStateBeforeCaptionClick = $systemState
            $proof.systemAllowsBeforeCaptionClick = $allows
            Write-Host ('NOTIFICATION_BEFORE_CAPTION_INPUT ' + (@{ systemState = $systemState; systemAllowsNotifications = $allows } | ConvertTo-Json -Compress))
            Invoke-QolNotificationCaptionClick
            $state = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/notifications" -Headers $headers -TimeoutSec 2 -NoProxy
            $captionStateRecorded = $true
            continue
        }
        if ($captionStateRecorded -and $null -eq $proof.systemStateAfterCaptionClick) {
            $proof.systemStateAfterCaptionClick = $systemState
            $proof.systemAllowsAfterCaptionClick = $allows
            Write-Host ('NOTIFICATION_AFTER_CAPTION_INPUT ' + (@{ systemState = $systemState; systemAllowsNotifications = $allows } | ConvertTo-Json -Compress))
        }
        if ($allows -or $clock.Elapsed.TotalSeconds -ge 15) {
            Write-Host ('NOTIFICATION_READINESS ' + (@{ systemState = $systemState; systemStatesObserved = $proof.systemStatesObserved;
                systemAllowsNotifications = $allows; polls = $proof.stateReadinessPolls; elapsedMs = $proof.stateReadinessElapsedMs } | ConvertTo-Json -Compress))
            Require $allows "Windows notification readiness stayed $systemState for 15 seconds; real OS delivery remains blocked. The earlier supported Settings attempt is not repeated."
            return $state
        }
        Start-Sleep -Milliseconds 250
        $state = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/notifications" -Headers $headers -TimeoutSec 2 -NoProxy
    } while ($true)
}


function Invoke-QolNotificationCaptionClick {
    Require ($AllowInteractiveTests -and $env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_OS -eq 'Windows') 'Caption presence input requires the opted-in disposable hosted Windows desktop.'
    Assert-OwnedWindow $windowHandle
    Require ($app.Process.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId -and
        [TogetherServerQolWindowCheck]::IsWindowVisible($windowHandle) -and
        ![TogetherServerQolWindowCheck]::IsZoomed($windowHandle) -and
        ![TogetherServerQolWindowCheck]::IsIconic($windowHandle)) 'Caption presence input requires the exact visible normal test window in this session.'
    $before = Read-Rect
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    Require ($root.Current.ProcessId -eq $app.Id -and [IntPtr]$root.Current.NativeWindowHandle -eq $windowHandle) 'The exact native test HWND changed before caption input.'
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $qolShellDisplayName)
    $captions = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) | Where-Object {
        $_.Current.ProcessId -eq $app.Id -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and
        $_.Current.NativeWindowHandle -ne 0 -and (Test-QolVisibleElement $_) })
    Require ($captions.Count -eq 1) 'One exact visible native title label is required for neutral caption input.'
    $caption = $captions[0]
    Assert-OwnedWindow ([IntPtr]$caption.Current.NativeWindowHandle)
    $box = $caption.Current.BoundingRectangle
    $dpi = [TogetherServerQolWindowCheck]::GetDpiForWindow($windowHandle)
    Require ($dpi -gt 0 -and $box.Left -gt $before.Left -and $box.Right -lt $before.Right -and
        $box.Top -gt $before.Top -and $box.Bottom -le $before.Top + 42 * $dpi / 96) 'The matched title label escaped the native caption band.'
    $proof = $nativeShellProof.notification
    $proof.captionBoundsBefore = @($before.Left, $before.Top, $before.Right, $before.Bottom)
    # Passing the caption itself as the hit-test root requires its exact UIA
    # ancestry at the physical point, refusing overlays or any other control.
    Click-QolShellElement $caption $caption
    $proof.captionClickSent = $true
    $proof.captionClickExactOwnedHit = $true
    Wait-Until {
        Assert-OwnedWindow $windowHandle
        return [TogetherServerQolShellCheck]::GetForegroundWindow() -eq $windowHandle
    } 'The exact caption click did not foreground the owned test HWND.' 5
    $proof.captionClickForegroundVerified = $true
    $after = Read-Rect
    $proof.captionBoundsAfter = @($after.Left, $after.Top, $after.Right, $after.Bottom)
    Require (![TogetherServerQolWindowCheck]::IsZoomed($windowHandle) -and ![TogetherServerQolWindowCheck]::IsIconic($windowHandle) -and
        $before.Left -eq $after.Left -and $before.Top -eq $after.Top -and
        $before.Right -eq $after.Right -and $before.Bottom -eq $after.Bottom) 'The single neutral caption click changed the original normal window geometry.'
    $proof.captionClickNormalBoundsPreserved = $true
}

function Get-QolShellRoots {
    $roots = @()
    $script:qolShellRootContexts = @{}
    foreach ($handle in [TogetherServerQolShellCheck]::ShellWindows()) {
        [uint32]$ownerId = 0
        [TogetherServerQolShellCheck]::GetWindowThreadProcessId($handle, [ref]$ownerId) | Out-Null
        $process = $null
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$ownerId)
            $shellPath = [IO.Path]::GetFullPath($process.MainModule.FileName)
            $windowsRoot = [IO.Path]::GetFullPath($env:WINDIR).TrimEnd('\') + '\'
            if ($process.HasExited -or $process.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId -or
                !$shellPath.StartsWith($windowsRoot, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($shellPath) -notin @('explorer.exe', 'ShellExperienceHost.exe', 'ShellHost.exe', 'StartMenuExperienceHost.exe')) { continue }
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
            if ($root.Current.ProcessId -eq [int]$ownerId) {
                $script:qolShellRootContexts[(Get-QolRuntimeId $root)] = [ordered]@{
                    windowClass = [TogetherServerQolShellCheck]::WindowClass($handle)
                    approvedProcess = [IO.Path]::GetFileName($shellPath)
                    sameSession = $true; windowsPathVerified = $true; rootOwnerVerified = $true
                }
                $roots += $root
            }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
        finally { if ($process) { $process.Dispose() } }
    }
    return $roots
}
function Get-QolShellDescendants($Root) {
    $items = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    Require ($items.Count -le 1024) 'A dedicated shell UIA surface exceeded its bounded control inventory.'
    return @($Root) + @($items)
}
function Test-QolVisibleElement($Element) {
    $current = $Element.Current
    $box = $current.BoundingRectangle
    return !$current.IsOffscreen -and $current.IsEnabled -and !$box.IsEmpty -and $box.Width -gt 0 -and $box.Height -gt 0
}
function Test-QolReadableElement($Element) {
    # Static shell text need not be enabled. Input still requires the stricter
    # visible/enabled predicate and the existing exact owner and hit tests.
    $current = $Element.Current
    $box = $current.BoundingRectangle
    return !$current.IsOffscreen -and !$box.IsEmpty -and $box.Width -gt 0 -and $box.Height -gt 0
}
function Get-QolRuntimeId($Element) { return [string]::Join(',', [string[]]$Element.GetRuntimeId()) }
function Add-QolShellWindowMetadata([ValidateSet('Poll','Failure')][string]$Phase) {
    # Unlike action discovery, this reads only Win32 class/rectangle metadata.
    # Rejected classes never gain UIA traversal, screenshots or click authority.
    $proof = $nativeShellProof.notification
    try { $handles = [TogetherServerQolShellCheck]::VisibleTopLevelWindows() }
    catch { $proof.shellWindowMetadataReadFailures++; return }
    $approvedClasses = @('Shell_TrayWnd','Shell_SecondaryTrayWnd','NotifyIconOverflowWindow',
        'TopLevelWindowForOverflowXamlIsland','tooltips_class32','Windows.UI.Core.CoreWindow',
        'Xaml_WindowedPopupClass','XamlExplorerHostIslandWindow','DesktopWindowXamlSource',
        'Windows.UI.Composition.DesktopWindowContentBridge')
    foreach ($handle in $handles) {
        [uint32]$ownerId = 0
        [TogetherServerQolShellCheck]::GetWindowThreadProcessId($handle, [ref]$ownerId) | Out-Null
        $process = $null
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$ownerId)
            $shellPath = [IO.Path]::GetFullPath($process.MainModule.FileName)
            $windowsRoot = [IO.Path]::GetFullPath($env:WINDIR).TrimEnd('\') + '\'
            $processName = [IO.Path]::GetFileName($shellPath)
            if ($process.HasExited -or $process.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId -or
                !$shellPath.StartsWith($windowsRoot, [StringComparison]::OrdinalIgnoreCase) -or
                $processName -notin @('explorer.exe','ShellExperienceHost.exe','ShellHost.exe','StartMenuExperienceHost.exe')) { continue }
            $class = [TogetherServerQolShellCheck]::WindowClass($handle)
            $geometry = @([TogetherServerQolShellCheck]::WindowBounds($handle))
            if ($geometry.Count -ne 4 -or $geometry[2] -le 0 -or $geometry[3] -le 0 -or
                @($geometry | Where-Object { [Math]::Abs([long]$_) -gt 262144 }).Count) { continue }
            $record = [ordered]@{
                windowClass = $(if ($class -cmatch '^[A-Za-z0-9_.#-]{1,127}$') { $class } else { 'Other' })
                approvedProcess = $processName; geometry = $geometry; admittedClass = $class -cin $approvedClasses
            }
            $key = $record | ConvertTo-Json -Depth 3 -Compress
            if (!$qolShellWindowDiagnosticKeys.Contains($key)) {
                if ($proof.shellWindowMetadata.Count -lt 32) {
                    [void]$qolShellWindowDiagnosticKeys.Add($key)
                    $record['firstObservedDuring'] = $Phase
                    $proof.shellWindowMetadata += $record
                } else { $proof.shellWindowMetadataTruncated = $true }
            }
        } catch { $proof.shellWindowMetadataReadFailures++ }
        finally { if ($process) { $process.Dispose() } }
    }
}
function Add-QolShellObservation($Root, $Items) {
    $runtimeId = Get-QolRuntimeId $Root
    $context = $script:qolShellRootContexts[$runtimeId]
    if (!$context) { return }
    $box = $Root.Current.BoundingRectangle
    $geometry = @($box.Left, $box.Top, $box.Width, $box.Height)
    $boundedGeometry = !$box.IsEmpty -and @($geometry | Where-Object {
        [double]::IsNaN($_) -or [double]::IsInfinity($_) -or [Math]::Abs($_) -gt 262144
    }).Count -eq 0
    $titles = @($Items | Where-Object {
        (Test-QolReadableElement $_) -and $_.Current.Name.Contains($qolBalloonTitle, [StringComparison]::Ordinal)
    })
    $bodies = @($Items | Where-Object {
        (Test-QolReadableElement $_) -and $_.Current.Name.Contains($qolBalloonBody, [StringComparison]::Ordinal)
    })
    # Only fixed class/process labels, geometry and synthetic-text booleans are
    # exported. Never retain UIA names, process paths, IDs or arbitrary text.
    $observation = [ordered]@{
        windowClass = $context.windowClass; approvedProcess = $context.approvedProcess
        sameSession = $context.sameSession; windowsPathVerified = $context.windowsPathVerified
        rootOwnerVerified = $context.rootOwnerVerified; duringDeliveryWait = $script:qolDeliveryWaitActive
        geometryAvailable = $boundedGeometry
        geometry = $(if ($boundedGeometry) { @($geometry | ForEach-Object { [Math]::Round($_, 1) }) } else { @() })
        fixedTitlePresent = $titles.Count -gt 0; fixedBodyPresent = $bodies.Count -gt 0
        fixedTitleEnabled = @($titles | Where-Object { $_.Current.IsEnabled }).Count -gt 0
        fixedBodyEnabled = @($bodies | Where-Object { $_.Current.IsEnabled }).Count -gt 0
    }
    $proof = $nativeShellProof.notification
    $proof.lastShellObservation = $observation
    $key = $runtimeId + '|' + ($observation | ConvertTo-Json -Depth 3 -Compress)
    if (!$qolShellDiagnosticKeys.Contains($key)) {
        if ($proof.shellObservations.Count -lt 32) {
            [void]$qolShellDiagnosticKeys.Add($key)
            $proof.shellObservations += $observation
        } else { $proof.shellObservationsTruncated = $true }
    }
}
function Add-QolPostTriggerNotificationState([ValidateSet('Poll','Failure')][string]$Phase, [long]$ElapsedMs) {
    Assert-AppIdentity
    $sample = [ordered]@{
        phase = $Phase; elapsedMs = $ElapsedMs; outcome = 'Unavailable'
        systemState = 'Unknown'; systemAllowsNotifications = $null; quietMode = $null
        globalLifecycleAllowed = $null; allowedEvents = @(); serverPreferenceCount = $null
    }
    $view = $null
    try {
        $view = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/local/desktop/notifications" -Headers $headers -TimeoutSec 1 -NoProxy
    } catch { }
    Assert-AppIdentity
    if ($null -ne $view) {
        $events = @('Lifecycle','Backup','Access','Connections','Network','Recovery','Countdown','Players',
            'Maintenance','Configuration','AddOns','Remote','Update')
        $sample.outcome = 'Rejected'
        if ($view.systemState -in @('Unknown','NotPresent','Busy','RunningD3DFullScreen','PresentationMode',
                'AcceptsNotifications','QuietTime','App') -and $view.systemAllowsNotifications -is [bool] -and
            $view.quietMode -is [bool] -and $null -ne $view.allowedEvents -and $null -ne $view.servers -and
            @($view.allowedEvents).Count -le $events.Count -and @($view.servers).Count -le 128 -and
            @($view.allowedEvents | Where-Object { $_ -isnot [string] -or $_ -notin $events }).Count -eq 0) {
            $sample.outcome = 'Read'
            $sample.systemState = [string]$view.systemState
            $sample.systemAllowsNotifications = $view.systemAllowsNotifications
            $sample.quietMode = $view.quietMode
            $sample.allowedEvents = @($events | Where-Object { $_ -in $view.allowedEvents })
            $sample.globalLifecycleAllowed = 'Lifecycle' -in $view.allowedEvents
            $sample.serverPreferenceCount = @($view.servers).Count
        }
    }
    $proof = $nativeShellProof.notification
    if ($Phase -eq 'Failure' -and $sample.outcome -ne 'Read') { $proof.failureStateReadFailed = $true }
    if ($proof.postTriggerStates.Count -lt 16) { $proof.postTriggerStates += $sample }
    else { $proof.postTriggerStatesTruncated = $true }
}
function Click-QolShellElement($Element, $Root, [switch]$Right, [switch]$Double) {
    Assert-AppIdentity
    Require (!($Right -and $Double)) 'A shell action cannot combine right-click and double-click.'
    Require (Test-QolVisibleElement $Element) 'The matched native shell element is no longer visible and enabled.'
    Require ($Element.Current.ProcessId -eq $Root.Current.ProcessId) 'The matched native shell element changed owner.'
    $box = $Element.Current.BoundingRectangle
    $point = [Windows.Point]::new($box.Left + $box.Width / 2, $box.Top + $box.Height / 2)
    $hit = [System.Windows.Automation.AutomationElement]::FromPoint($point)
    $rootId = Get-QolRuntimeId $Root
    $inside = $false
    for ($depth = 0; $hit -and $depth -lt 24; $depth++) {
        if ((Get-QolRuntimeId $hit) -eq $rootId) { $inside = $true; break }
        $hit = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($hit)
    }
    Require $inside 'The actual click point is covered by another native surface; shell input was refused.'
    Require ([TogetherServerQolShellCheck]::SetCursorPos([int]$point.X, [int]$point.Y)) 'The dedicated shell input could not be positioned.'
    [TogetherServerQolShellCheck]::mouse_event($(if ($Right) { 0x0008 } else { 0x0002 }), 0, 0, 0, [UIntPtr]::Zero)
    [TogetherServerQolShellCheck]::mouse_event($(if ($Right) { 0x0010 } else { 0x0004 }), 0, 0, 0, [UIntPtr]::Zero)
    if ($Double) {
        Start-Sleep -Milliseconds 80
        [TogetherServerQolShellCheck]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [TogetherServerQolShellCheck]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    }
}
function Save-QolShellScreenshot($Element, [string]$Name) {
    Require ($Name -in @('native-os-notification.png', 'native-explorer-quit-menu.png')) 'Unexpected shell capture destination.'
    $box = $Element.Current.BoundingRectangle
    $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $left = [int][Math]::Floor($box.Left); $top = [int][Math]::Floor($box.Top)
    $width = [int][Math]::Ceiling($box.Width); $height = [int][Math]::Ceiling($box.Height)
    Require ($width -gt 0 -and $height -gt 0 -and $width -le 1000 -and $height -le 800 -and
        $left -ge $screen.Left -and $top -ge $screen.Top -and $left + $width -le $screen.Right -and $top + $height -le $screen.Bottom) 'The matched shell capture escaped its bounded visible surface.'
    $picture = [System.Drawing.Bitmap]::new($width, $height)
    $drawing = [System.Drawing.Graphics]::FromImage($picture)
    try {
        $drawing.CopyFromScreen($left, $top, 0, 0, $picture.Size)
        $picture.Save((Join-Path $caseRoot $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $drawing.Dispose(); $picture.Dispose() }
}
function Find-QolBalloon {
    $matches = @()
    $roots = @(Get-QolShellRoots)
    $nativeShellProof.notification.searchPolls++
    $nativeShellProof.notification.shellRootsObserved = [Math]::Max($nativeShellProof.notification.shellRootsObserved, $roots.Count)
    foreach ($root in $roots) {
        try {
            $items = @(Get-QolShellDescendants $root)
            Add-QolShellObservation $root $items
            foreach ($title in @($items | Where-Object { (Test-QolReadableElement $_) -and $_.Current.Name.Contains($qolBalloonTitle, [StringComparison]::Ordinal) })) {
                $nativeShellProof.notification.matchingTitlesObserved++
                $candidate = $title
                for ($depth = 0; $candidate -and $depth -lt 12; $depth++) {
                    if ($candidate.Current.ProcessId -ne $root.Current.ProcessId) { break }
                    $box = $candidate.Current.BoundingRectangle
                    if ($box.Width -gt 1000 -or $box.Height -gt 800) { break }
                    $bodies = @(Get-QolShellDescendants $candidate | Where-Object {
                        (Test-QolReadableElement $_) -and $_.Current.Name.Contains($qolBalloonBody, [StringComparison]::Ordinal)
                    })
                    if ($bodies.Count -gt 0 -and
                        (Test-QolVisibleElement $candidate)) {
                        $matches += [pscustomobject]@{ Element = $candidate; Root = $root }; break
                    }
                    if ((Get-QolRuntimeId $candidate) -eq (Get-QolRuntimeId $root)) { break }
                    $candidate = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($candidate)
                }
            }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
    }
    return @($matches | Group-Object { Get-QolRuntimeId $_.Element } | ForEach-Object { $_.Group[0] })
}
function Wait-QolBalloon {
    $state = @{ Match = $null; Clock = [Diagnostics.Stopwatch]::StartNew(); LastStateMs = -1000 }
    $script:qolDeliveryWaitActive = $true
    try {
        Wait-Until {
            Assert-AppIdentity
            if ($state.Clock.ElapsedMilliseconds - $state.LastStateMs -ge 1000) {
                Add-QolShellWindowMetadata 'Poll'
                Add-QolPostTriggerNotificationState 'Poll' $state.Clock.ElapsedMilliseconds
                $state.LastStateMs = $state.Clock.ElapsedMilliseconds
            }
            $matches = @(Find-QolBalloon)
            Require ($matches.Count -le 1) 'The fixed development notification is ambiguous; no OS surface was clicked.'
            if ($matches.Count -eq 1) { $state.Match = $matches[0]; return $true }
            return $false
        } 'Windows did not expose the freshly triggered development balloon/toast title and fixed body; delivery acceptance failed.' 12
        return $state.Match
    } catch {
        $originalFailure = $_
        try {
            Add-QolShellWindowMetadata 'Failure'
            Add-QolPostTriggerNotificationState 'Failure' $state.Clock.ElapsedMilliseconds
        }
        catch { $nativeShellProof.notification.failureStateReadFailed = $true }
        Write-Host ('NOTIFICATION_DELIVERY_DIAGNOSTICS ' + ($nativeShellProof.notification | ConvertTo-Json -Depth 5 -Compress))
        throw $originalFailure
    } finally { $script:qolDeliveryWaitActive = $false }
}
function Test-QolTrayName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 256) { return $false }
    $normalized = [regex]::Replace($Name, '\s+', ' ').Trim()
    if ($normalized.Equals($qolShellDisplayName, [StringComparison]::Ordinal)) { return $true }
    # Explorer can prefix the display title to NotifyIcon.Text and flatten its
    # line breaks. Both forms come from the fixed production TraySummaryText.
    $title = [regex]::Escape($qolShellDisplayName)
    $pattern = '^(?:' + $title + ' )?' + $title + ' Hosting: (?<running>[0-9]{1,3}) running, (?<review>[0-9]{1,3}) need review Join: (?:Connected|Disabled|Unknown|Disconnected/Unknown|Revoked|Awaiting approval|No saved connection|Not connected|Update required|Access expired)$'
    $match = [regex]::Match($normalized, $pattern)
    return $match.Success -and [int]$match.Groups['running'].Value -le 128 -and [int]$match.Groups['review'].Value -le 128
}
function Find-QolTrayIcon {
    $state = @{ Match = $null; Expanded = $false }
    Wait-Until {
        Assert-AppIdentity
        $icons = @(); $expanders = @()
        $roots = @(Get-QolShellRoots)
        $nativeShellProof.trayQuit.searchPolls++
        $nativeShellProof.trayQuit.shellRootsObserved = [Math]::Max($nativeShellProof.trayQuit.shellRootsObserved, $roots.Count)
        foreach ($root in $roots) {
            try {
                foreach ($element in @(Get-QolShellDescendants $root)) {
                    if (!(Test-QolVisibleElement $element) -or $element.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) { continue }
                    if ((Test-QolTrayName $element.Current.Name) -and $element.Current.ClassName -notmatch 'TaskList|TaskbarButton') {
                        $icons += [pscustomobject]@{ Element = $element; Root = $root }
                    } elseif ($element.Current.Name -match '^(Show hidden icons|Hidden icon menu)') {
                        $expanders += [pscustomobject]@{ Element = $element; Root = $root }
                    }
                }
            } catch [System.Windows.Automation.ElementNotAvailableException] { }
        }
        $nativeShellProof.trayQuit.iconCandidates = $icons.Count
        $nativeShellProof.trayQuit.overflowCandidates = $expanders.Count
        Require ($icons.Count -le 1) 'More than one exact development tray icon is visible; input refused.'
        if ($icons.Count -eq 1) { $state.Match = $icons[0]; return $true }
        if (!$state.Expanded -and $expanders.Count -eq 1) {
            Click-QolShellElement $expanders[0].Element $expanders[0].Root
            $state.Expanded = $true; $nativeShellProof.trayQuit.overflowOpened = $true
        }
        return $false
    } 'Explorer did not expose one exact development tray icon, including its real overflow menu.' 15
    $nativeShellProof.trayQuit.exactTrayIconObserved = $true
    return $state.Match
}
function Invoke-QolExplorerQuit {
    $icon = Find-QolTrayIcon
    Click-QolShellElement $icon.Element $icon.Root -Right
    $state = @{ Quit = $null; Menu = $null }
    $name = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, "Quit $qolShellDisplayName")
    $ownerCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$app.Id)
    $condition = [System.Windows.Automation.AndCondition]::new($name, $ownerCondition)
    Wait-Until {
        Assert-AppIdentity
        $items = @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) |
            Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem -and (Test-QolVisibleElement $_) })
        Require ($items.Count -le 1) 'The exact app-owned tray Quit menu item is ambiguous.'
        if ($items.Count -eq 0) { return $false }
        $menu = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($items[0])
        Require ($menu -and $menu.Current.ProcessId -eq $app.Id -and $menu.Current.ControlType -eq [System.Windows.Automation.ControlType]::Menu) 'The Quit item is outside the exact app-owned tray context menu.'
        $names = @(Get-QolShellDescendants $menu | ForEach-Object { $_.Current.Name })
        Require ($names -contains "Open $qolShellDisplayName" -and $names -contains "Quit $qolShellDisplayName") 'The native popup is not the app-specific Open/Quit tray menu.'
        $state.Quit = $items[0]; $state.Menu = $menu; return $true
    } 'Right-clicking the actual development tray icon did not open its app-owned Quit menu.' 8
    $nativeShellProof.trayQuit.ownedQuitMenuObserved = $true
    Save-QolShellScreenshot $state.Menu 'native-explorer-quit-menu.png'
    Assert-AppIdentity
    Require ($state.Quit.Current.ProcessId -eq $app.Id -and (Test-QolVisibleElement $state.Quit)) 'The exact app-owned Quit item changed before invocation.'
    $invoke = $state.Quit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    $nativeShellProof.trayQuit.menuInvoked = $true
}
