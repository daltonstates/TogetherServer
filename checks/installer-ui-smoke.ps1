param([string]$InstallerPath = '', [string]$ExpectedAppPath = '', [switch]$AllowInteractiveTests)
$ErrorActionPreference = 'Stop'
if (!$AllowInteractiveTests) {
    throw 'ForegroundSafety: Installer UI acceptance requires explicit approval for a dedicated hosted Windows runner. Never run it on the active owner desktop.'
}
if (![OperatingSystem]::IsWindows() -or $PSVersionTable.PSVersion.Major -lt 7 -or
    $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Installer UI acceptance runs only on an explicitly opted-in dedicated hosted Windows runner.'
}
$repository = Split-Path -Parent $PSScriptRoot
if (!$InstallerPath) { $InstallerPath = Join-Path $repository 'local-data/release-assets/TogetherServer-Setup-win-x64.exe' }
if (!$ExpectedAppPath) { $ExpectedAppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$candidate = (Resolve-Path -LiteralPath $ExpectedAppPath).Path
$candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
$installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
$caseId = [guid]::NewGuid().ToString('N')
$caseRoot = [IO.Path]::GetFullPath((Join-Path $repository "local-data/installer-ui-smoke/$caseId"))
$evidenceRoot = Join-Path $repository "local-data/ci-evidence/installer-ui/$caseId"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$baseUrl = 'http://127.0.0.1:5127'
$headers = @{ Origin = $baseUrl; 'X-TogetherServer-Local' = '1' }
$priorData = $env:TOGETHERSERVER_DATA_DIR
$owned = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$captureObservations = [Collections.Generic.List[object]]::new()
$results = @(
    [pscustomobject]@{ choice = 'Host'; intent = 'Host'; label = 'Host game servers'; mode = 'Host'; state = 'NotRun' },
    [pscustomobject]@{ choice = 'Friend'; intent = 'Join'; label = "Join a friend's server"; mode = 'Friend'; state = 'NotRun' },
    [pscustomobject]@{ choice = 'DecideLater'; intent = 'Choose'; label = 'Decide later in TogetherServer'; mode = 'Host'; state = 'NotRun' }
)
$currentStage = 'preflight'
$currentCase = $null
$failure = $null
$cleanupCompleted = $true
$app = $null
$installRoot = $null
$dataRoot = $null
$groupName = $null
$installedApp = $null

function Require([bool]$value, [string]$message) { if (!$value) { throw $message } }
function Uninstall-Entries([string]$target = '') {
    foreach ($root in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        if (!(Test-Path -LiteralPath $root)) { continue }
        foreach ($entry in @(Get-ChildItem -LiteralPath $root | Get-ItemProperty -ErrorAction SilentlyContinue)) {
            if ($entry.DisplayName -notlike 'TogetherServer*') { continue }
            if (!$target -or ($entry.InstallLocation -and [IO.Path]::GetFullPath($entry.InstallLocation).TrimEnd('\').Equals($target.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase))) { $entry }
        }
    }
}
function Add-Owned([Diagnostics.Process]$process, [string]$path) {
    $process.Refresh()
    Require (!$process.HasExited) 'An owned process exited before its identity was retained.'
    $observed = [IO.Path]::GetFullPath($process.MainModule.FileName)
    Require ($observed.Equals([IO.Path]::GetFullPath($path), [StringComparison]::OrdinalIgnoreCase)) 'An owned executable path did not match.'
    $item = [pscustomobject]@{ process = $process; id = $process.Id; ticks = $process.StartTime.ToUniversalTime().Ticks; path = $observed }
    $owned.Add($item)
    return $item
}
function Assert-Owned($item) {
    $item.process.Refresh()
    Require (!$item.process.HasExited -and $item.process.StartTime.ToUniversalTime().Ticks -eq $item.ticks -and
        [IO.Path]::GetFullPath($item.process.MainModule.FileName).Equals($item.path, [StringComparison]::OrdinalIgnoreCase)) 'A disposable process identity changed.'
}
function Refresh-Owned {
    # Inno's verified setup/uninstaller stub owns a temporary wizard child. Retain
    # that exact ancestry and creation time; never discover or terminate by name.
    $rows = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, ExecutablePath, CreationDate)
    for ($depth = 0; $depth -lt 8; $depth++) {
        $added = $false
        foreach ($row in $rows) {
            if (@($owned | Where-Object id -eq $row.ProcessId).Count) { continue }
            $parent = @($owned | Where-Object id -eq $row.ParentProcessId | Select-Object -First 1)
            if (!$parent.Count -or !$row.ExecutablePath -or !$row.CreationDate) { continue }
            $born = ([DateTime]$row.CreationDate).ToUniversalTime().Ticks
            if ($born -lt $parent[0].ticks) { continue }
            $parent[0].process.Refresh()
            if ($parent[0].process.HasExited -and $born -gt $parent[0].process.ExitTime.ToUniversalTime().Ticks) { continue }
            try { $process = [Diagnostics.Process]::GetProcessById([int]$row.ProcessId) }
            catch [ArgumentException] { continue }
            if ($process.HasExited) { $process.Dispose(); continue }
            try { Add-Owned $process $row.ExecutablePath | Out-Null }
            catch {
                $process.Refresh()
                if ($process.HasExited) { $process.Dispose(); continue }
                throw
            }
            $added = $true
        }
        if (!$added) { break }
    }
}
function Wait-Until([scriptblock]$read, [string]$message, [int]$seconds = 30) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        Refresh-Owned
        $value = & $read
        if ($value) { return $value }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt $seconds)
    throw $message
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class TogetherServerInstallerUi {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    public sealed class Window { public IntPtr Handle; public int Pid; public string Class; }
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    public static string ClassName(IntPtr window) { var text = new StringBuilder(256); GetClassName(window, text, text.Capacity); return text.ToString(); }
    public static bool IsOwnedVisibleWindow(IntPtr window, int expectedPid) {
        uint pid;
        return IsWindow(window) && IsWindowVisible(window) &&
            GetWindowThreadProcessId(window, out pid) != 0 && pid == (uint)expectedPid;
    }
    public static Window[] Windows(int[] pids) {
        var ids = new HashSet<int>(pids); var result = new List<Window>();
        EnumWindows((window, _) => { uint pid; GetWindowThreadProcessId(window, out pid);
            if (ids.Contains((int)pid) && IsWindowVisible(window)) result.Add(new Window { Handle=window, Pid=(int)pid, Class=ClassName(window) });
            return true; }, IntPtr.Zero);
        return result.ToArray();
    }
    public static void ClickRow(IntPtr window, int x, int y) {
        var point = new Point { X=x, Y=y };
        if (!ScreenToClient(window, ref point) || point.X < 0 || point.Y < 0 || point.X > 32767 || point.Y > 32767)
            throw new InvalidOperationException("The owned wizard row has invalid coordinates.");
        var position = new IntPtr((point.Y << 16) | point.X);
        if (!PostMessage(window, 0x0201, new IntPtr(1), position) || !PostMessage(window, 0x0202, IntPtr.Zero, position))
            throw new InvalidOperationException("The owned wizard row click was refused.");
    }
}
'@

function Owned-Windows {
    $live = @($owned | Where-Object { !$_.process.HasExited } | ForEach-Object id)
    return @([TogetherServerInstallerUi]::Windows([int[]]$live))
}
function Capture-Dialogs([string]$phase, [IntPtr]$requiredWindow = [IntPtr]::Zero) {
    $capturedRequired = $false
    $visible = @(Owned-Windows)
    # Process ownership also includes auxiliary Win32/WebView windows. Their
    # IsWindowVisible flag does not make them a wizard or an app screenshot.
    # Normal captures require the exact UIA wizard or installed-app HWND; failure
    # evidence uses only those same reviewed primary surfaces.
    $appWindow = [IntPtr]::Zero
    if ($app -and !$app.process.HasExited) {
        $app.process.Refresh()
        $appWindow = $app.process.MainWindowHandle
    }
    $targets = @($visible | Where-Object {
        if ($requiredWindow -ne [IntPtr]::Zero) { $_.Handle -eq $requiredWindow }
        else { $_.Class -eq 'TWizardForm' -or ($appWindow -ne [IntPtr]::Zero -and $_.Handle -eq $appWindow) }
    })
    foreach ($window in $targets) {
        Assert-Owned (@($owned | Where-Object id -eq $window.Pid)[0])
        Require ([TogetherServerInstallerUi]::IsOwnedVisibleWindow($window.Handle, $window.Pid)) 'The exact capture window is no longer visible and owned.'
        $rect = [TogetherServerInstallerUi+Rect]::new()
        Require ([TogetherServerInstallerUi]::GetWindowRect($window.Handle, [ref]$rect)) 'An owned dialog rectangle was unavailable.'
        $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
        if ($captureObservations.Count -lt 128) {
            $captureObservations.Add(@{ choice = $currentCase.choice; phase = $phase; windowClass = $window.Class;
                width = $width; height = $height; exactRequiredWindow = ($window.Handle -eq $requiredWindow);
                otherOwnedVisibleWindows = $visible.Count - 1 })
        }
        Require ($width -gt 0 -and $height -gt 0 -and $width -le 2400 -and $height -le 1800 -and $screenshots.Count -lt 64) 'An owned dialog exceeded the screenshot bound.'
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $dc = $graphics.GetHdc()
        try { Require ([TogetherServerInstallerUi]::PrintWindow($window.Handle, $dc, 2)) 'The owned dialog could not be captured.' }
        finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
        try {
            $name = '{0}-{1:d2}-{2}.png' -f $currentCase.choice, $screenshots.Count, $phase
            $bitmap.Save((Join-Path $evidenceRoot $name), [Drawing.Imaging.ImageFormat]::Png)
            $screenshots.Add($name)
            if ($window.Handle -eq $requiredWindow) { $capturedRequired = $true }
        } finally { $bitmap.Dispose() }
    }
    Require ($requiredWindow -eq [IntPtr]::Zero -or $capturedRequired) 'The exact required visible wizard or installed app window was not captured.'
}
function Wizard-Root {
    $windows = @(Owned-Windows | Where-Object Class -eq 'TWizardForm')
    Require ($windows.Count -le 1) 'More than one owned installer wizard was visible.'
    if ($windows.Count) {
        try { return [Windows.Automation.AutomationElement]::FromHandle($windows[0].Handle) }
        catch [Windows.Automation.ElementNotAvailableException], [ArgumentException] {
            # Finish can destroy this exact owned HWND between enumeration and
            # UIA lookup. A still-visible owned window keeps its original error.
            if (![TogetherServerInstallerUi]::IsOwnedVisibleWindow($windows[0].Handle, $windows[0].Pid)) { return $null }
            throw
        }
    }
    return $null
}
function Find-Ui($root, [string]$name) {
    if ($null -eq $root) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)
    $matches = @($root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition) | Where-Object { !$_.Current.IsOffscreen })
    Require ($matches.Count -le 1) 'A named wizard control was ambiguous.'
    if ($matches.Count) { return $matches[0] }
    return $null
}
function Checked($element) {
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        return $pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On
    }
    if ($element.TryGetCurrentPattern([Windows.Automation.LegacyIAccessiblePattern]::Pattern, [ref]$pattern)) {
        return ([int]$pattern.Current.State -band 0x10) -ne 0
    }
    throw 'The actual wizard did not expose an observable checked state.'
}
function Assert-Element($element) {
    Require ($null -ne $element -and !$element.Current.IsOffscreen -and $element.Current.IsEnabled -and
        @($owned | Where-Object id -eq $element.Current.ProcessId).Count -eq 1) 'An installer UI action was not bound to one enabled owned control.'
    Assert-Owned (@($owned | Where-Object id -eq $element.Current.ProcessId)[0])
}
function Capture-Wizard($root, [string]$phase) {
    Assert-Element $root
    $handle = [IntPtr]$root.Current.NativeWindowHandle
    Require ($handle -ne [IntPtr]::Zero -and [TogetherServerInstallerUi]::ClassName($handle) -eq 'TWizardForm') 'The wizard capture requires its exact native UIA root.'
    Capture-Dialogs $phase $handle
}
function Click-Choice($element) {
    Assert-Element $element
    $rect = $element.Current.BoundingRectangle
    $native = $element
    for ($depth = 0; $native -and !$native.Current.NativeWindowHandle -and $depth -lt 10; $depth++) {
        $native = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($native)
    }
    Require ($null -ne $native -and $native.Current.NativeWindowHandle -ne 0) 'The actual wizard choice has no owned native control.'
    Assert-Element $native
    $handle = [IntPtr]$native.Current.NativeWindowHandle
    $kind = [TogetherServerInstallerUi]::ClassName($handle)
    if ($kind -eq 'TNewCheckListBox') {
        # Inno's accessible custom rows have no default action. Click their real
        # checkbox/radio area and then read the actual MSAA checked state.
        [TogetherServerInstallerUi]::ClickRow($handle, [int]($rect.Left + 10), [int]($rect.Top + $rect.Height / 2))
    } else {
        Require ($kind -in @('TNewCheckBox', 'Button')) 'The wizard choice is not a reviewed native checkbox.'
        Require ([TogetherServerInstallerUi]::PostMessage($handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)) 'The owned native checkbox click was refused.'
    }
}
function Set-Checked($root, [string]$name, [bool]$value, [bool]$force = $false) {
    $element = Find-Ui $root $name
    Assert-Element $element
    if ($force -or (Checked $element) -ne $value) { Click-Choice $element }
    Wait-Until { $current = Find-Ui (Wizard-Root) $name; if ($current -and (Checked $current) -eq $value) { $true } } 'A real wizard choice did not retain its checked state.' 10 | Out-Null
}
function Button-Ui($root, [string[]]$names) {
    $buttons = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
    return @($buttons | Where-Object { !$_.Current.IsOffscreen -and $_.Current.IsEnabled -and
        ($_.Current.Name.Replace('&', '').Trim() -in $names) } | Select-Object -First 1)[0]
}
function Invoke-Button($element) {
    Assert-Element $element
    $pattern = $null
    if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
    Require ($element.Current.NativeWindowHandle -ne 0) 'The actual wizard button has no native invoke path.'
    Require ([TogetherServerInstallerUi]::PostMessage([IntPtr]$element.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)) 'The native wizard button invoke was refused.'
}
function Page-Signature($root) {
    return (@($root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
        Where-Object { !$_.Current.IsOffscreen } | ForEach-Object { $_.Current.Name }) -join '|')
}
function Stop-Owned {
    Refresh-Owned
    foreach ($item in @($owned.ToArray()) | Sort-Object ticks -Descending) {
        if ($item.process.HasExited) { continue }
        Assert-Owned $item
        $item.process.Kill()
        Require ($item.process.WaitForExit(10000)) 'An exact disposable process did not exit during cleanup.'
    }
}
function Uninstall-Case {
    if (!$installRoot -or !(Test-Path -LiteralPath $installRoot)) { return }
    $uninstallers = @(Get-ChildItem -LiteralPath $installRoot -Filter 'unins*.exe' -File)
    if (!$uninstallers.Count) { return }
    Require ($uninstallers.Count -eq 1 -and ($uninstallers[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'The disposable uninstaller identity was ambiguous.'
    $uninstall = Add-Owned (Start-Process -FilePath $uninstallers[0].FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -PassThru) $uninstallers[0].FullName
    Wait-Until { $uninstall.process.HasExited } 'The disposable uninstaller timed out.' 60 | Out-Null
    Require ($uninstall.process.ExitCode -eq 0) 'The disposable uninstaller failed.'
}
function Remove-EmptyShortcutGroup {
    if (!$groupName) { return }
    $programs = [IO.Path]::GetFullPath([Environment]::GetFolderPath('Programs')).TrimEnd('\')
    $target = Join-Path $programs $groupName
    if (!(Test-Path -LiteralPath $target)) { return }
    $resolved = (Resolve-Path -LiteralPath $target).Path
    Require ((Split-Path -Parent $resolved).Equals($programs, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -eq $groupName -and
        (((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) 'Shortcut cleanup refused an unexpected path.'
    Require (@(Get-ChildItem -LiteralPath $resolved -Force).Count -eq 0) 'Uninstall left a disposable Start-menu shortcut.'
    Remove-Item -LiteralPath $resolved -Force
}

try {
    Require (@(Get-CimInstance Win32_Process | Where-Object Name -like 'TogetherServer*.exe').Count -eq 0) 'Installer UI acceptance requires no existing TogetherServer process.'
    Require (@(Uninstall-Entries).Count -eq 0) 'Installer UI acceptance requires no installed TogetherServer copy.'
    Require (!(Get-NetTCPConnection -State Listen -LocalPort 5127 -ErrorAction SilentlyContinue)) 'Installer UI acceptance requires a free owner GUI port.'
    Require (!(Get-ItemProperty -Path $runKey -Name TogetherServer -ErrorAction SilentlyContinue)) 'Installer UI acceptance preserves an existing startup registration by refusing to run.'
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'TogetherServer.lnk'
    Require (!(Test-Path -LiteralPath $desktopShortcut)) 'Installer UI acceptance preserves an existing desktop shortcut by refusing to run.'
    New-Item -ItemType Directory -Path $caseRoot, $evidenceRoot -Force | Out-Null
    foreach ($case in $results) {
        $currentCase = $case
        $installRoot = Join-Path $caseRoot ($case.choice + '/installed app')
        $dataRoot = Join-Path $caseRoot ($case.choice + '/data')
        $installedApp = Join-Path $installRoot 'TogetherServer.exe'
        $groupName = "TogetherServer Wizard UI $caseId $($case.choice)"
        $env:TOGETHERSERVER_DATA_DIR = $dataRoot
        $app = $null
        try {
            New-Item -ItemType Directory -Path (Join-Path $caseRoot $case.choice) -Force | Out-Null
            $currentStage = 'wizard'
            # Path/log isolation arguments only. Every role and preference is
            # chosen through the actual visible wizard, with no silent flags.
            $setup = Add-Owned (Start-Process -FilePath $installer -ArgumentList @('/SP-', '/NORESTART', '/NOCLOSEAPPLICATIONS',
                "/DIR=`"$installRoot`"", "/GROUP=`"$groupName`"", "/LOG=`"$(Join-Path $caseRoot ($case.choice + '/setup.log'))`"") -PassThru) $installer
            $firstWizard = Wait-Until { Wizard-Root } 'The real installer wizard did not appear.' 45
            $wizardOwner = @($owned | Where-Object id -eq $firstWizard.Current.ProcessId)
            Require ($wizardOwner.Count -eq 1) 'The actual installer wizard process was not retained.'
            $roleSeen = $false; $preferencesSeen = $false; $tasksSeen = $false; $installed = $false; $finished = $false
            for ($page = 0; $page -lt 12 -and !$finished; $page++) {
                $root = Wizard-Root
                Require ($null -ne $root) 'The owned wizard vanished before Finish.'
                $signature = Page-Signature $root
                if (Find-Ui $root 'Decide later in TogetherServer') {
                    Require (!$roleSeen) 'The installer repeated its role page.'
                    Set-Checked $root $case.label $true $true
                    $roleSeen = $true
                    Capture-Wizard $root 'role'
                } elseif (Find-Ui $root 'Open TogetherServer at Windows sign-in') {
                    Require (!$preferencesSeen) 'The installer repeated its Windows preferences page.'
                    foreach ($label in @('Open TogetherServer at Windows sign-in', 'Close to the tray instead of quitting')) {
                        Require (!(Checked (Find-Ui $root $label))) 'An optional Windows preference was enabled by default.'
                        Set-Checked $root $label $true
                        Set-Checked $root $label $false
                    }
                    $preferencesSeen = $true
                    Capture-Wizard $root 'windows-options-off'
                } elseif (Find-Ui $root 'Create a desktop shortcut') {
                    Require (!(Checked (Find-Ui $root 'Create a desktop shortcut'))) 'The optional desktop shortcut was enabled by default.'
                    Set-Checked $root 'Create a desktop shortcut' $true
                    Set-Checked $root 'Create a desktop shortcut' $false
                    $tasksSeen = $true
                    Capture-Wizard $root 'desktop-shortcut-off'
                } else { Capture-Wizard $root 'page' }
                $finish = Button-Ui $root @('Finish')
                if ($finish) {
                    Require ($installed -and $roleSeen -and $preferencesSeen -and $tasksSeen) 'Finish was reached without all actual wizard choices.'
                    Set-Checked $root 'Open TogetherServer' $false
                    Capture-Wizard $root 'finish-launch-off'
                    Invoke-Button $finish
                    Wait-Until { !(Wizard-Root) } 'The real Finish button did not close the wizard.' 30 | Out-Null
                    $finished = $true
                    break
                }
                $install = Button-Ui $root @('Install')
                if ($install) {
                    Require ($roleSeen -and $preferencesSeen -and $tasksSeen) 'The wizard omitted a required setup choice.'
                    Invoke-Button $install
                    $installed = $true
                    Wait-Until { $current = Wizard-Root; if ($current -and (Button-Ui $current @('Finish'))) { $true } } 'The actual installer did not reach Finish.' 120 | Out-Null
                } else {
                    $next = Button-Ui $root @('Next >', 'Next')
                    Require ($null -ne $next) 'The actual wizard exposed no reviewed Next or Install action.'
                    Invoke-Button $next
                    Wait-Until { $current = Wizard-Root; if ($current -and (Page-Signature $current) -ne $signature) { $true } } 'The real Next button did not advance the wizard.' 20 | Out-Null
                }
            }
            Require $finished 'The actual installer wizard exceeded its page bound.'
            Wait-Until { @($owned | Where-Object { !$_.process.HasExited }).Count -eq 0 } 'The installer or its configure helper did not exit.' 30 | Out-Null
            Require ($setup.process.ExitCode -eq 0 -and $wizardOwner[0].process.ExitCode -eq 0) 'The actual installer setup or wizard exited unsuccessfully.'
            Require ((Get-FileHash -LiteralPath $installedApp -Algorithm SHA256).Hash -eq $candidateHash) 'The UI install did not place the exact candidate bytes.'
            Require (@(Uninstall-Entries $installRoot).Count -eq 1) 'The UI install did not register its disposable destination.'
            $guidePath = Join-Path $dataRoot 'getting-started.json'
            $guide = Get-Content -LiteralPath $guidePath -Raw | ConvertFrom-Json
            Require ($guide.offered -eq $true -and $guide.intent -eq $case.intent -and $guide.dismissed -eq $false) 'The actual role choice did not persist its guide intent.'
            Require (!(Get-ItemProperty -Path $runKey -Name TogetherServer -ErrorAction SilentlyContinue)) 'Skipping Windows startup still created a registry entry.'
            Require (!(Test-Path -LiteralPath $desktopShortcut)) 'Skipping the desktop task still created a shortcut.'
            if ($case.choice -eq 'DecideLater') {
                Require (!(Test-Path -LiteralPath (Join-Path $dataRoot 'mode.json'))) 'Decide later selected a starting mode before the first app launch.'
            }
            $currentStage = 'installed-app'
            $app = Add-Owned (Start-Process -FilePath $installedApp -WorkingDirectory $installRoot -PassThru) $installedApp
            $snapshot = Wait-Until {
                Assert-Owned $app
                try { $view = Invoke-RestMethod -Uri "$baseUrl/api/local/snapshot" -TimeoutSec 2; if ($view.mode) { $view } } catch { }
            } 'The UI-installed app did not serve its fresh local backend.' 60
            $instance = Invoke-RestMethod -Uri "$baseUrl/api/local/instance" -TimeoutSec 5
            Require ([IO.Path]::GetFullPath($instance.dataRoot).Equals([IO.Path]::GetFullPath($dataRoot), [StringComparison]::OrdinalIgnoreCase)) 'The UI-installed app escaped its fresh data root.'
            $preferences = Invoke-RestMethod -Uri "$baseUrl/api/local/desktop/preferences" -TimeoutSec 5
            $appGuide = Invoke-RestMethod -Uri "$baseUrl/api/local/getting-started" -Headers $headers -TimeoutSec 5
            Require ($snapshot.mode -eq $case.mode -and $appGuide.intent -eq $case.intent -and $appGuide.offered -eq $true) 'The installed backend did not honor the actual wizard choice.'
            Require ($preferences.launchAtLogin -eq $false -and $preferences.closeToTray -eq $false) 'An optional Windows preference remained enabled after it was skipped.'
            if ($snapshot.mode -eq 'Host') { Require (@($snapshot.settings.profiles).Count -eq 0 -and @($snapshot.runs).Count -eq 0 -and !$snapshot.settings.companionListeningEnabled) 'The installer configured a game or incoming connection.' }
            Wait-Until {
                Assert-Owned $app
                try { $window = Invoke-RestMethod -Uri "$baseUrl/api/local/window" -TimeoutSec 2 }
                catch { return $false }
                Require ($window.loadState -ne 'Failed') 'The actual installed native window failed to render.'
                $app.process.Refresh()
                if ($window.visible -and $window.rendered -and $window.loadState -eq 'Rendered' -and $window.customChrome -and
                    !$window.loadErrorCode -and !$window.loadFailureKind -and !$window.loadFailureHResult -and
                    $app.process.MainWindowHandle -ne [IntPtr]::Zero -and
                    [TogetherServerInstallerUi]::IsOwnedVisibleWindow($app.process.MainWindowHandle, $app.id)) { $true }
            } 'The UI-installed app did not show a rendered native window.' 60 | Out-Null
            Capture-Dialogs 'installed-app' $app.process.MainWindowHandle
            Assert-Owned $app
            Require ((Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers -TimeoutSec 10).ok) 'The exact installed app refused graceful Quit.'
            Wait-Until { $app.process.HasExited } 'The exact UI-installed app did not exit after Quit.' 30 | Out-Null
            Require ($app.process.ExitCode -eq 0) 'The UI-installed app exited unsuccessfully after graceful Quit.'
            $currentStage = 'uninstall'
            $guideHash = (Get-FileHash -LiteralPath $guidePath -Algorithm SHA256).Hash
            $save = Join-Path $dataRoot 'worlds/retained-synthetic-save/world.dat'
            New-Item -ItemType Directory -Path (Split-Path -Parent $save) -Force | Out-Null
            [IO.File]::WriteAllText($save, 'Disposable owner save; uninstall must preserve these bytes.')
            $saveHash = (Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash
            Uninstall-Case
            Require (!(Test-Path -LiteralPath $installedApp) -and @(Uninstall-Entries $installRoot).Count -eq 0) 'Uninstall left application bytes or registration.'
            Remove-EmptyShortcutGroup
            Require ((Get-FileHash -LiteralPath $guidePath -Algorithm SHA256).Hash -eq $guideHash -and
                (Get-FileHash -LiteralPath $save -Algorithm SHA256).Hash -eq $saveHash) 'Uninstall changed the disposable owner guide or save.'
            Wait-Until { @($owned | Where-Object { !$_.process.HasExited }).Count -eq 0 } 'A disposable installer/app descendant remained after uninstall.' 30 | Out-Null
            $case.state = 'Passed'
            Write-Host "PASS actual installer UI: $($case.choice), optional tasks skipped, installed backend verified, owner data preserved."
        } catch {
            $case.state = 'Failed'
            try { Refresh-Owned; Capture-Dialogs 'failure' } catch { }
            throw
        } finally {
            if ($app -and !$app.process.HasExited) {
                try {
                    Assert-Owned $app
                    $instance = Invoke-RestMethod -Uri "$baseUrl/api/local/instance" -TimeoutSec 3
                    if ([IO.Path]::GetFullPath($instance.dataRoot).Equals([IO.Path]::GetFullPath($dataRoot), [StringComparison]::OrdinalIgnoreCase)) {
                        Invoke-RestMethod -Uri "$baseUrl/api/local/quit" -Method Post -Headers $headers -TimeoutSec 5 | Out-Null
                        $app.process.WaitForExit(5000) | Out-Null
                    }
                } catch { }
            }
            try {
                Stop-Owned
                Uninstall-Case
                Stop-Owned
                Require (@(Uninstall-Entries $installRoot).Count -eq 0) 'Disposable installer cleanup left registration.'
                Remove-EmptyShortcutGroup
                Require (!(Get-ItemProperty -Path $runKey -Name TogetherServer -ErrorAction SilentlyContinue)) 'Disposable installer cleanup left Windows startup enabled.'
            } catch { $cleanupCompleted = $false; throw }
        }
    }
} catch {
    $failure = $_
    if ($currentCase -and $currentCase.state -ne 'Passed') { $currentCase.state = 'Failed' }
    try { Refresh-Owned; if ($currentCase) { Capture-Dialogs 'failure' } } catch { }
} finally {
    try { Stop-Owned } catch { $cleanupCompleted = $false; if (!$failure) { $failure = $_ } }
    if ($null -eq $priorData) { Remove-Item Env:TOGETHERSERVER_DATA_DIR -ErrorAction SilentlyContinue }
    else { $env:TOGETHERSERVER_DATA_DIR = $priorData }
    if (Test-Path -LiteralPath $evidenceRoot) {
        $unchanged = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -eq $candidateHash -and
            (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -eq $installerHash
        $diagnostic = $null
        if ($failure) {
            $diagnostic = $failure.Exception.Message -replace [regex]::Escape($caseRoot), '[disposable data]' -replace '[A-Za-z]:[\\/][^\r\n"'']+', '[location]'
            $diagnostic = $diagnostic.Substring(0, [Math]::Min(500, $diagnostic.Length))
        }
        $summary = [ordered]@{ schema = 1; sourceRevision = $env:GITHUB_SHA; candidateSha256 = $candidateHash.ToLowerInvariant();
            installerSha256 = $installerHash.ToLowerInvariant(); candidateAndInstallerUnchanged = $unchanged;
            passed = !$failure -and $cleanupCompleted -and $unchanged -and @($results | Where-Object state -ne 'Passed').Count -eq 0;
            cases = @($results | Select-Object choice, intent, mode, state); skipped = 0; cleanupCompleted = $cleanupCompleted;
            failedStage = $(if ($failure) { $currentStage } else { $null });
            diagnostic = $diagnostic;
            screenshots = @($screenshots.ToArray()); captureObservations = @($captureObservations.ToArray());
            boundary = 'Actual Inno wizard and fresh installed app on a dedicated hosted Windows runner; synthetic owner save only; no game, credentials or network changes.' }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'summary.json') -Encoding utf8
        if (!$unchanged -and !$failure) { $failure = 'Verified app or installer bytes changed during actual wizard acceptance.' }
    }
    foreach ($item in $owned) { $item.process.Dispose() }
}
if ($failure) { throw $failure }
Write-Host 'Installer UI checks: 3 actual wizard choices passed, 0 failed, 0 skipped.'
