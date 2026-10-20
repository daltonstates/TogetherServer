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
    notification = [ordered]@{ systemAllowsNotifications = $false; freshTitleAndBodyObserved = $false;
        physicalClickSent = $false; exactAppForeground = $false; nativeDestinationVerified = $false;
        searchPolls = 0; shellRootsObserved = 0; matchingTitlesObserved = 0 }
    trayQuit = [ordered]@{ exactTrayIconObserved = $false; overflowOpened = $false; ownedQuitMenuObserved = $false;
        menuInvoked = $false; nativeFlushObserved = $false; exactProcessExited = $false;
        searchPolls = 0; shellRootsObserved = 0; iconCandidates = 0; overflowCandidates = 0 }
}
$qolShellDisplayName = 'TogetherServer DEVELOPMENT'
$qolBalloonTitle = $qolShellDisplayName + ' is still running'
$qolBalloonBody = 'Open it from the tray icon. Right-click the icon to quit.'

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
function Click-QolShellElement($Element, $Root, [switch]$Right) {
    Assert-AppIdentity
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
function Find-QolTrayIcon {
    $state = @{ Match = $null; Expanded = $false }
    Wait-Until {
        Assert-AppIdentity
        $icons = @(); $expanders = @()
        $roots = @(Get-QolShellRoots)
        $nativeShellProof.trayQuit.searchPolls++
        $nativeShellProof.trayQuit.shellRootsObserved = [Math]::Max($nativeShellProof.trayQuit.shellRootsObserved, $roots.Count)
        foreach ($root in $roots) {
            foreach ($element in @(Get-QolShellDescendants $root)) {
                if (!(Test-QolVisibleElement $element) -or $element.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) { continue }
                if ($element.Current.Name -eq $qolShellDisplayName -and $element.Current.ClassName -notmatch 'TaskList|TaskbarButton') {
                    $icons += [pscustomobject]@{ Element = $element; Root = $root }
                } elseif ($element.Current.Name -match '^(Show hidden icons|Hidden icon menu)') {
                    $expanders += [pscustomobject]@{ Element = $element; Root = $root }
                }
            }
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
