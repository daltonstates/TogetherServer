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
    private delegate bool EnumWindow(IntPtr window, IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr value);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public static IntPtr[] VisibleWindows(int processId) {
        var values = new List<IntPtr>();
        EnumWindows((window, _) => { uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == (uint)processId && IsWindowVisible(window)) values.Add(window);
            return true; }, IntPtr.Zero);
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
        osUiSetup = [ordered]@{ attempted = $false; status = 'NotAttempted'; stateBefore = $null; stateAfter = $null;
            binaryVerified = $false; ownedProcessVerified = $false; ownedWindowVerified = $false;
            controls = @(); failureCode = $null; cleanupCompleted = $true; appRestored = $false };
        physicalClickSent = $false; exactAppForeground = $false; nativeDestinationVerified = $false;
        searchPolls = 0; shellRootsObserved = 0; matchingTitlesObserved = 0 }
    trayQuit = [ordered]@{ exactTrayIconObserved = $false; overflowOpened = $false; ownedQuitMenuObserved = $false;
        menuInvoked = $false; nativeFlushObserved = $false; exactProcessExited = $false;
        searchPolls = 0; shellRootsObserved = 0; iconCandidates = 0; overflowCandidates = 0 }
}
$qolShellDisplayName = 'TogetherServer DEVELOPMENT'
$qolBalloonTitle = $qolShellDisplayName + ' is still running'
$qolBalloonBody = 'Open it from the tray icon. Right-click the icon to quit.'

function Wait-QolNotificationReadiness($InitialState) {
    # UIA/posted window actions do not establish physical session input. On the
    # disposable hosted desktop, click the exact native caption once; observe
    # the unchanged production notification gate rather than overriding it.
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $state = $InitialState
    $proof = $nativeShellProof.notification
    $captionStateRecorded = $false
    $settingsStateRecorded = $false
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
        if ($InitialState.systemState -eq 'QuietTime' -and $systemState -eq 'QuietTime' -and !$proof.osUiSetup.attempted -and
            $env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_OS -eq 'Windows') {
            $proof.osUiSetup.stateBefore = $systemState
            try { Invoke-QolHostedNotificationSettings }
            catch {
                Write-Host ('NOTIFICATION_OS_UI_UNAVAILABLE ' + (@{ stage = $proof.osUiSetup.failureCode;
                    cleanupCompleted = $proof.osUiSetup.cleanupCompleted; appRestored = $proof.osUiSetup.appRestored } | ConvertTo-Json -Compress))
                if (!$proof.osUiSetup.cleanupCompleted -or !$proof.osUiSetup.appRestored) { throw }
            }
            $state = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/notifications" -Headers $headers -TimeoutSec 2 -NoProxy
            $clock.Restart()
            continue
        }
        if ($proof.osUiSetup.attempted -and !$settingsStateRecorded) {
            $proof.osUiSetup.stateAfter = $systemState
            $settingsStateRecorded = $true
            Write-Host ('NOTIFICATION_AFTER_OS_UI ' + (@{ systemState = $systemState; systemAllowsNotifications = $allows;
                setupStatus = $proof.osUiSetup.status } | ConvertTo-Json -Compress))
        }
        if ($allows -or $clock.Elapsed.TotalSeconds -ge 15) {
            Write-Host ('NOTIFICATION_READINESS ' + (@{ systemState = $systemState; systemStatesObserved = $proof.systemStatesObserved;
                systemAllowsNotifications = $allows; polls = $proof.stateReadinessPolls; elapsedMs = $proof.stateReadinessElapsedMs } | ConvertTo-Json -Compress))
            Require $allows "Windows notification readiness stayed $systemState for 15 seconds; real OS delivery remains blocked after the bounded supported UI attempt."
            Require (!$proof.osUiSetup.attempted -or $proof.osUiSetup.status -eq 'Completed') 'The required supported Settings UI attempt was unavailable; full native acceptance cannot pass.'
            return $state
        }
        Start-Sleep -Milliseconds 250
        $state = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/notifications" -Headers $headers -TimeoutSec 2 -NoProxy
    } while ($true)
}

function Invoke-QolHostedNotificationSettings {
    Require ($AllowInteractiveTests -and $env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_OS -eq 'Windows') 'Windows notification setup requires the opted-in disposable hosted desktop.'
    $proof = $nativeShellProof.notification.osUiSetup
    Require (!$proof.attempted -and $proof.stateBefore -eq 'QuietTime') 'Only one initial-QuietTime Settings attempt is permitted.'
    $proof.attempted = $true
    $proof.status = 'Unavailable'
    $original = Read-Rect
    $settings = @{ Identity = $null; Window = [IntPtr]::Zero; Root = $null }
    $stage = 'BinaryVerification'
    $settingsDeadline = [DateTime]::UtcNow.AddSeconds(40)
    $assertSettings = {
        Require ([DateTime]::UtcNow -lt $settingsDeadline) 'The supported Settings setup exceeded its bound.'
        $identity = $settings.Identity
        Require ($null -ne $identity) 'The Settings process was not retained.'
        $identity.Process.Refresh()
        Require (!$identity.Process.HasExited -and $identity.Process.Id -eq $identity.Id -and
            $identity.Process.StartTime.ToUniversalTime().Ticks -eq $identity.Ticks -and
            $identity.Process.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId -and
            [IO.Path]::GetFullPath($identity.Process.Path).Equals($identity.Path, [StringComparison]::OrdinalIgnoreCase)) 'The exact hosted Settings identity changed.'
    }
    $readToggle = {
        param([string]$name)
        & $assertSettings
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $matches = @($settings.Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) | Where-Object {
            $_.Current.ProcessId -eq $settings.Identity.Id })
        $toggles = @($matches | Where-Object { $pattern = $null; $_.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern) })
        Require ($toggles.Count -le 1) 'A whitelisted Windows notification toggle was ambiguous.'
        if ($toggles.Count -eq 0) { return $null }
        Require ($toggles[0].Current.IsEnabled) 'A whitelisted Windows notification toggle was disabled.'
        if ($toggles[0].Current.IsOffscreen) {
            $scroll = $null
            if ($toggles[0].TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scroll)) { $scroll.ScrollIntoView() }
            else { $toggles[0].SetFocus() }
            Require (!$toggles[0].Current.IsOffscreen) 'A whitelisted Windows notification toggle could not be brought into view.'
        }
        $pattern = $toggles[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $value = [string]$pattern.Current.ToggleState
        Require ($value -in @('On', 'Off')) 'A whitelisted Windows notification toggle had an unsupported state.'
        return @{ Element = $toggles[0]; Pattern = $pattern; State = $value }
    }
    try {
        $settingsPath = Join-Path ([Environment]::GetFolderPath('Windows')) 'ImmersiveControlPanel/SystemSettings.exe'
        Require (Test-Path -LiteralPath $settingsPath -PathType Leaf) 'The documented Windows Settings application is unavailable.'
        Require (((Get-Item -LiteralPath $settingsPath).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'The Settings executable is not a regular system file.'
        $signature = Get-AuthenticodeSignature -LiteralPath $settingsPath
        Require ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Subject -match '(^|,\s*)O=Microsoft Corporation(,|$)') 'The system Settings executable did not have a valid Microsoft signature.'
        $proof.binaryVerified = $true
        $stage = 'FreshSettingsSession'
        Require (@(Get-Process -Name SystemSettings -ErrorAction SilentlyContinue | Where-Object SessionId -eq ([Diagnostics.Process]::GetCurrentProcess().SessionId)).Count -eq 0) 'A pre-existing Settings process cannot be adopted.'
        $launched = [DateTime]::UtcNow
        # This is the documented Settings URI, not an undocumented executable
        # argument, registry edit, policy change or application notification hook.
        Start-Process -FilePath 'ms-settings:notifications' | Out-Null
        $stage = 'OwnedSettingsProcess'
        Wait-Until {
            $matches = @(Get-Process -Name SystemSettings -ErrorAction SilentlyContinue | Where-Object SessionId -eq ([Diagnostics.Process]::GetCurrentProcess().SessionId))
            Require ($matches.Count -le 1) 'The fresh Settings process was ambiguous.'
            if ($matches.Count -eq 0) { return $false }
            $process = $matches[0]
            Require ($process.StartTime.ToUniversalTime() -ge $launched -and
                [IO.Path]::GetFullPath($process.Path).Equals([IO.Path]::GetFullPath($settingsPath), [StringComparison]::OrdinalIgnoreCase)) 'The Settings URI did not activate the verified fresh system executable.'
            $settings.Identity = Record-Process $process $settingsPath
            return $true
        } 'The documented Settings URI did not activate a fresh verified Settings process.' 12
        $proof.ownedProcessVerified = $true
        $stage = 'OwnedSettingsWindow'
        Wait-Until {
            & $assertSettings
            $windows = @([TogetherServerQolShellCheck]::VisibleWindows($settings.Identity.Id))
            Require ($windows.Count -le 4) 'The owned Settings window inventory exceeded its bound.'
            if ($windows.Count -ne 1) { return $false }
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($windows[0])
            if ($root.Current.ProcessId -ne $settings.Identity.Id -or $root.Current.IsOffscreen) { return $false }
            $settings.Window = $windows[0]; $settings.Root = $root; return $true
        } 'The exact fresh Settings process did not expose one visible owned window.' 8
        $proof.ownedWindowVerified = $true
        foreach ($choice in @(@{ Name = 'Notifications'; Expected = 'On' }, @{ Name = 'Do not disturb'; Expected = 'Off' })) {
            $stage = if ($choice.Name -eq 'Notifications') { 'NotificationsToggle' } else { 'DoNotDisturbToggle' }
            $found = @{ Value = $null }
            Wait-Until { $found.Value = & $readToggle $choice.Name; return $null -ne $found.Value } 'A required ordinary notification control was unavailable.' 5
            $row = [ordered]@{ name = $choice.Name; present = $true; before = $found.Value.State; after = $null }
            $proof.controls += $row
            if ($found.Value.State -ne $choice.Expected) { $found.Value.Pattern.Toggle() }
            Wait-Until { $found.Value = & $readToggle $choice.Name; return $found.Value -and $found.Value.State -eq $choice.Expected } 'An ordinary Windows notification toggle did not retain its requested state.' 5
            $row.after = $found.Value.State
        }
        $stage = 'FirstHourAutomaticRule'
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Turn on do not disturb automatically')
        $expanders = @($settings.Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) | Where-Object {
            $expandPattern = $null
            $_.Current.ProcessId -eq $settings.Identity.Id -and !$_.Current.IsOffscreen -and $_.Current.IsEnabled -and
                $_.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expandPattern) })
        Require ($expanders.Count -le 1) 'The ordinary automatic notification section was ambiguous.'
        if ($expanders.Count -eq 1) {
            & $assertSettings
            $pattern = $null
            Require ($expanders[0].TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) 'The ordinary automatic notification section had no supported expand action.'
            if ($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) { $pattern.Expand() }
            Wait-Until { & $assertSettings; return $pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded } 'The automatic notification section did not expand.' 3
        }
        $name = 'For the first hour after a Windows feature update'
        $firstHourClock = [Diagnostics.Stopwatch]::StartNew()
        do {
            $firstHour = & $readToggle $name
            if ($firstHour) { break }
            Start-Sleep -Milliseconds 100
        } while ($firstHourClock.Elapsed.TotalSeconds -lt 3)
        $row = [ordered]@{ name = $name; present = $null -ne $firstHour; before = $null; after = $null }
        $proof.controls += $row
        if ($firstHour) {
            $row.before = $firstHour.State
            if ($firstHour.State -ne 'Off') { $firstHour.Pattern.Toggle() }
            Wait-Until { $current = & $readToggle $name; return $current -and $current.State -eq 'Off' } 'The first-hour automatic rule did not remain off.' 5
            $row.after = 'Off'
        }
        $proof.status = 'Completed'
    } catch {
        $proof.failureCode = $stage
        throw 'The bounded supported Windows notification setup was unavailable; inspect its whitelisted state evidence.'
    } finally {
        try {
            if ($settings.Identity -and !$settings.Identity.Process.HasExited) {
                # Cleanup retains PID/start-time/path ownership even if the
                # setup deadline expired; no name-only process mutation.
                $settingsDeadline = [DateTime]::UtcNow.AddSeconds(15)
                & $assertSettings
                if ($settings.Window -ne [IntPtr]::Zero -and [TogetherServerQolWindowCheck]::IsWindowVisible($settings.Window)) {
                    [uint32]$owner = 0
                    [TogetherServerQolShellCheck]::GetWindowThreadProcessId($settings.Window, [ref]$owner) | Out-Null
                    Require ($owner -eq $settings.Identity.Id) 'The Settings HWND changed owner before Close.'
                    Require ([TogetherServerQolWindowCheck]::PostMessage($settings.Window, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)) 'The exact Settings window refused Close.'
                    Wait-Until { return $settings.Identity.Process.HasExited -or ![TogetherServerQolWindowCheck]::IsWindowVisible($settings.Window) } 'The exact Settings window did not close.' 8
                }
                if (!$settings.Identity.Process.HasExited) {
                    & $assertSettings
                    $settings.Identity.Process.Kill()
                    Require ($settings.Identity.Process.WaitForExit(5000)) 'The exact newly launched Settings process did not exit.'
                }
            }
            Assert-OwnedWindow $windowHandle
            Require ((Invoke-RestMethod -Uri "$baseUrl/api/local/show" -Method Post -Headers $headers -TimeoutSec 5 -NoProxy).ok) 'The unchanged owner API refused to restore the test app.'
            Wait-Until { Assert-OwnedWindow $windowHandle; return [TogetherServerQolShellCheck]::GetForegroundWindow() -eq $windowHandle } 'Closing Settings did not restore the exact test-app foreground HWND.' 5
            $after = Read-Rect
            Require (![TogetherServerQolWindowCheck]::IsZoomed($windowHandle) -and ![TogetherServerQolWindowCheck]::IsIconic($windowHandle) -and
                $original.Left -eq $after.Left -and $original.Top -eq $after.Top -and $original.Right -eq $after.Right -and $original.Bottom -eq $after.Bottom) 'The supported Settings attempt changed the original normal test-window geometry.'
            $proof.appRestored = $true
        } catch { $proof.cleanupCompleted = $false; throw }
        Write-Host ('NOTIFICATION_OS_UI ' + ($proof | ConvertTo-Json -Depth 5 -Compress))
    }
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
            if ($root.Current.ProcessId -eq [int]$ownerId) { $roots += $root }
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
function Get-QolRuntimeId($Element) { return [string]::Join(',', [string[]]$Element.GetRuntimeId()) }
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
            foreach ($title in @($items | Where-Object { (Test-QolVisibleElement $_) -and $_.Current.Name.Contains($qolBalloonTitle, [StringComparison]::Ordinal) })) {
                $nativeShellProof.notification.matchingTitlesObserved++
                $candidate = $title
                for ($depth = 0; $candidate -and $depth -lt 12; $depth++) {
                    if ($candidate.Current.ProcessId -ne $root.Current.ProcessId) { break }
                    $box = $candidate.Current.BoundingRectangle
                    if ($box.Width -gt 1000 -or $box.Height -gt 800) { break }
                    $texts = @(Get-QolShellDescendants $candidate | ForEach-Object { $_.Current.Name })
                    if (@($texts | Where-Object { $_.Contains($qolBalloonBody, [StringComparison]::Ordinal) }).Count -gt 0 -and
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
    $state = @{ Match = $null }
    Wait-Until {
        Assert-AppIdentity
        $matches = @(Find-QolBalloon)
        Require ($matches.Count -le 1) 'The fixed development notification is ambiguous; no OS surface was clicked.'
        if ($matches.Count -eq 1) { $state.Match = $matches[0]; return $true }
        return $false
    } 'Windows did not expose the freshly triggered development balloon/toast title and fixed body; delivery acceptance failed.' 12
    return $state.Match
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
