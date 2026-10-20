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
$buttonObservations = [Collections.Generic.List[object]]::new()
$buttonObservationKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$choiceObservations = [Collections.Generic.List[object]]::new()
$choiceObservationKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$choiceInventories = [Collections.Generic.List[object]]::new()
$choiceInventoryKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$reviewedChoiceLabels = @('Decide later in TogetherServer', 'Host game servers', "Join a friend's server",
    'Open TogetherServer at Windows sign-in', 'Close to the tray instead of quitting', 'Create a desktop shortcut', 'Open TogetherServer')
$testSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$results = @(
    [pscustomobject]@{ choice = 'Host'; intent = 'Host'; label = 'Host game servers'; mode = 'Host'; state = 'NotRun' },
    [pscustomobject]@{ choice = 'Friend'; intent = 'Join'; label = "Join a friend's server"; mode = 'Friend'; state = 'NotRun' },
    [pscustomobject]@{ choice = 'DecideLater'; intent = 'Choose'; label = 'Decide later in TogetherServer'; mode = 'Host'; state = 'NotRun' }
)
$currentStage = 'preflight'
$currentCase = $null
$failure = $null
$failureStage = $null
$failureObservations = [Collections.Generic.List[object]]::new()
$identityObservations = [Collections.Generic.List[object]]::new()
$cleanupCompleted = $true
$app = $null
$installRoot = $null
$dataRoot = $null
$groupName = $null
$installedApp = $null

function Require([bool]$value, [string]$message) { if (!$value) { throw $message } }
function Remember-Failure($record, [string]$origin) {
    if ($null -eq $script:failure) { $script:failure = $record; $script:failureStage = $currentStage }
    if ($failureObservations.Count -ge 32) { return }
    $frames = @([regex]::Matches([string]$record.ScriptStackTrace, '(?m)^at (?<function>[^,\r\n]+), [^\r\n]*: line (?<line>\d+)') |
        Select-Object -First 8 | ForEach-Object {
            $function = $_.Groups['function'].Value
            if ($function -notmatch '^[A-Za-z0-9_<>.-]{1,80}$') { $function = '[script]' }
            @{ function = $function; line = [int]$_.Groups['line'].Value }
        })
    $message = [string]$record.Exception.Message -replace [regex]::Escape($caseRoot), '[disposable data]' -replace '[A-Za-z]:[\\/][^\r\n"'']+', '[location]'
    $message = $message.Substring(0, [Math]::Min(500, $message.Length))
    $observed = @{ stage = $currentStage; origin = $origin; type = $record.Exception.GetType().Name;
        line = $record.InvocationInfo.ScriptLineNumber; frames = $frames; diagnostic = $message }
    $failureObservations.Add($observed)
    Write-Host ('WIZARD_FAILURE ' + ($observed | ConvertTo-Json -Depth 4 -Compress))
}
function Path-Kind([string]$path) {
    if ($path.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) { return 'ExtendedUNC' }
    if ($path.StartsWith('\\?\', [StringComparison]::Ordinal)) { return 'ExtendedDOS' }
    if ($path.StartsWith('\\', [StringComparison]::Ordinal)) { return 'UNC' }
    if ($path -match '^[A-Za-z]:[\\/]') { return 'DOS' }
    return 'Other'
}
function Record-Identity([string]$origin, [string]$state, [string]$expectedPath, [string]$observedPath, $sameFile = $null, $sameFinalPath = $null) {
    if ($identityObservations.Count -ge 128) { return }
    $expectedName = ([IO.Path]::GetFileName($expectedPath) -replace '[^A-Za-z0-9_.-]', '_')
    $observedName = ([IO.Path]::GetFileName($observedPath) -replace '[^A-Za-z0-9_.-]', '_')
    $observed = @{ stage = $currentStage; origin = $origin; state = $state;
        expectedName = $expectedName.Substring(0, [Math]::Min(80, $expectedName.Length));
        observedName = $observedName.Substring(0, [Math]::Min(80, $observedName.Length));
        expectedPathKind = (Path-Kind $expectedPath); observedPathKind = (Path-Kind $observedPath);
        expectedShortAlias = ($expectedPath -match '~[0-9A-Za-z]+(?:\.[^\\/]*)?(?:[\\/]|$)');
        observedShortAlias = ($observedPath -match '~[0-9A-Za-z]+(?:\.[^\\/]*)?(?:[\\/]|$)');
        sameTextPath = [string]::Equals($expectedPath, $observedPath, [StringComparison]::OrdinalIgnoreCase);
        sameDirectory = [string]::Equals([IO.Path]::GetDirectoryName($expectedPath), [IO.Path]::GetDirectoryName($observedPath), [StringComparison]::OrdinalIgnoreCase);
        sameFileIdentity = $sameFile; sameFinalPath = $sameFinalPath }
    $identityObservations.Add($observed)
    Write-Host ('WIZARD_IDENTITY ' + ($observed | ConvertTo-Json -Compress))
}
function Uninstall-Entries([string]$target = '') {
    foreach ($root in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        if (!(Test-Path -LiteralPath $root)) { continue }
        foreach ($entry in @(Get-ChildItem -LiteralPath $root | Get-ItemProperty -ErrorAction SilentlyContinue)) {
            if ($entry.DisplayName -notlike 'TogetherServer*') { continue }
            if (!$target -or ($entry.InstallLocation -and [IO.Path]::GetFullPath($entry.InstallLocation).TrimEnd('\').Equals($target.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase))) { $entry }
        }
    }
}
function Add-Owned([Diagnostics.Process]$process, [string]$path, [Nullable[long]]$expectedTicks = $null, [string]$origin = 'direct-launch') {
    try {
    $process.Refresh()
    Require (!$process.HasExited) 'An owned process exited before its identity was retained.'
    $ticks = $process.StartTime.ToUniversalTime().Ticks
    Require ($null -eq $expectedTicks -or $ticks -eq $expectedTicks) 'The discovered process creation identity changed before retention.'
    Require ($process.SessionId -eq $testSessionId) 'An owned process is outside the dedicated test session.'
    # Query the retained kernel process handle, not its mutable/partly initialized
    # PEB module list. Both spellings must resolve to the same physical file.
    $observed = ''
    try {
        $observed = [TogetherServerInstallerUi]::ProcessImage($process.Handle)
        $actualFile = [TogetherServerInstallerUi]::ImageFile($observed)
        $expectedFile = [TogetherServerInstallerUi]::ImageFile($path)
    } catch { Record-Identity $origin 'FileIdentityUnavailable' $path $observed; throw }
    $sameFile = $actualFile.Identity -eq $expectedFile.Identity
    $sameFinalPath = [string]::Equals($actualFile.FinalPath, $expectedFile.FinalPath, [StringComparison]::OrdinalIgnoreCase)
    Record-Identity $origin 'Retention' $path $observed $sameFile $sameFinalPath
    Require ($sameFile) 'An owned executable file identity did not match its expected binary.'
    $process.Refresh()
    Require (!$process.HasExited -and $process.StartTime.ToUniversalTime().Ticks -eq $ticks -and $process.SessionId -eq $testSessionId) 'The owned process changed while retaining its image identity.'
    $item = [pscustomobject]@{ process = $process; id = $process.Id; ticks = $ticks; path = $actualFile.FinalPath;
        fileIdentity = $actualFile.Identity; sessionId = $process.SessionId }
    $owned.Add($item)
    return $item
    } catch {
        # Never claim successful cleanup for a live process whose exact image
        # could not be retained. Unknown identities do not receive any input.
        if ($origin -ne 'verified-descendant') {
            try { $process.Refresh(); if (!$process.HasExited) { $script:cleanupCompleted = $false } }
            catch { $script:cleanupCompleted = $false }
        }
        throw
    }
}
function Assert-Owned($item) {
    $item.process.Refresh()
    Require (!$item.process.HasExited -and $item.process.StartTime.ToUniversalTime().Ticks -eq $item.ticks -and
        $item.process.SessionId -eq $item.sessionId -and $item.sessionId -eq $testSessionId) 'A disposable process identity changed.'
    $observed = [TogetherServerInstallerUi]::ProcessImage($item.process.Handle)
    $image = [TogetherServerInstallerUi]::ImageFile($observed)
    if ($image.Identity -ne $item.fileIdentity) {
        Record-Identity 'owned-recheck' 'FileIdentityMismatch' $item.path $observed $false
        throw 'The retained process no longer uses the exact owned executable file.'
    }
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
            try {
                $parent[0].process.Refresh()
                if ($parent[0].process.HasExited) {
                    if ($born -gt $parent[0].process.ExitTime.ToUniversalTime().Ticks) { continue }
                } else { Assert-Owned $parent[0] }
            } catch {
                $parentFailure = $_
                $parent[0].process.Refresh()
                if (!$parent[0].process.HasExited -or $born -gt $parent[0].process.ExitTime.ToUniversalTime().Ticks) { throw $parentFailure }
                # Only discovery may tolerate an exact retained parent exiting
                # during this read. Its retained exit time still bounds the child.
            }
            try { $process = [Diagnostics.Process]::GetProcessById([int]$row.ProcessId) }
            catch [ArgumentException] { continue }
            if ($process.HasExited) { $process.Dispose(); continue }
            $retained = $false
            try {
                $ticks = $process.StartTime.ToUniversalTime().Ticks
                # CIM datetime preserves microseconds; FILETIME also has a final
                # 100ns digit. Compare the exact common precision, not a time gap.
                $fresh = Get-CimInstance Win32_Process -Filter ("ProcessId = " + [int]$row.ProcessId) -Property ProcessId, ParentProcessId, ExecutablePath, CreationDate
                if (!$fresh -or !$fresh.CreationDate -or !$fresh.ExecutablePath -or
                    $fresh.ParentProcessId -ne $parent[0].id -or
                    ([DateTime]$fresh.CreationDate).ToUniversalTime().Ticks -ne $born) {
                    Record-Identity 'descendant-discovery' 'FreshMetadataChanged' $row.ExecutablePath ''
                    continue
                }
                if (($ticks - ($ticks % 10)) -ne $born) {
                    Record-Identity 'descendant-discovery' 'FreshCreationMismatch' $row.ExecutablePath ''
                    throw 'The fresh child creation time did not match its retained kernel process identity.'
                }
                Add-Owned $process $fresh.ExecutablePath -expectedTicks $ticks -origin 'verified-descendant' | Out-Null
                $retained = $true
            }
            catch {
                $childFailure = $_
                $childExited = $false
                try { $process.Refresh(); $childExited = $process.HasExited } catch { }
                if ($childExited) { continue }
                $script:cleanupCompleted = $false
                throw $childFailure
            }
            finally { if (!$retained) { $process.Dispose() } }
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
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation {
        public uint Attributes;
        public FileTime Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    public sealed class ImageIdentity { public string FinalPath, Identity; }
    public sealed class Window { public IntPtr Handle; public int Pid; public string Class; }
    public sealed class NavigationButton {
        public IntPtr Handle, Root;
        public int Pid;
        public string Class, Caption;
        public bool Visible, Enabled, InRoot;
    }
    public sealed class Choice {
        public IntPtr Handle, Root;
        public int Pid, ChildId, Role, State, Left, Top, Width, Height;
        public string Class, Name;
        public bool Checked, Visible, Enabled;
    }
    // Inno 6.7.3 implements native IAccessible, but not IDispatch.Invoke.
    // Preserve the four IDispatch slots before the IAccessible vtable methods.
    // https://github.com/jrsoftware/issrc/blob/is-6_7_3/Components/NewCheckListBox.pas
    [ComImport, Guid("618736e0-3c3d-11cf-810c-00aa00389b71"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AccessibleRows {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint locale, out IntPtr info);
        [PreserveSig] int GetIDsOfNames(ref Guid iid, IntPtr names, uint count, uint locale, IntPtr ids);
        [PreserveSig] int Invoke(int id, ref Guid iid, uint locale, ushort flags, IntPtr arguments, IntPtr result, IntPtr error, IntPtr argumentError);
        [PreserveSig] int Parent(out IntPtr parent);
        [PreserveSig] int ChildCount(out int count);
        [PreserveSig] int Child([MarshalAs(UnmanagedType.Struct)] object child, out IntPtr value);
        [PreserveSig] int Name([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.BStr)] out string value);
        [PreserveSig] int Value([MarshalAs(UnmanagedType.Struct)] object child, out IntPtr value);
        [PreserveSig] int Description([MarshalAs(UnmanagedType.Struct)] object child, out IntPtr value);
        [PreserveSig] int Role([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int State([MarshalAs(UnmanagedType.Struct)] object child, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int Help([MarshalAs(UnmanagedType.Struct)] object child, out IntPtr value);
        [PreserveSig] int HelpTopic(out IntPtr helpFile, [MarshalAs(UnmanagedType.Struct)] object child, out int topic);
        [PreserveSig] int KeyboardShortcut([MarshalAs(UnmanagedType.Struct)] object child, out IntPtr value);
        [PreserveSig] int Focus([MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int Selection([MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int DefaultAction([MarshalAs(UnmanagedType.Struct)] object child, out IntPtr value);
        [PreserveSig] int Select(int flags, [MarshalAs(UnmanagedType.Struct)] object child);
        [PreserveSig] int Location(out int left, out int top, out int width, out int height, [MarshalAs(UnmanagedType.Struct)] object child);
    }
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out AccessibleRows value);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder text, ref uint length);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetFileInformationByHandle(IntPtr file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder text, uint length, uint flags);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static string ProcessImage(IntPtr process) {
        var text = new StringBuilder(32768);
        uint length = (uint)text.Capacity;
        if (!QueryFullProcessImageName(process, 0, text, ref length) || length == 0 || length >= text.Capacity)
            throw new InvalidOperationException("The retained kernel process image path was unavailable: " + Marshal.GetLastWin32Error());
        return text.ToString();
    }
    public static ImageIdentity ImageFile(string path) {
        var file = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (file == IntPtr.Zero || file == new IntPtr(-1))
            throw new InvalidOperationException("The expected executable file could not be identified: " + Marshal.GetLastWin32Error());
        try {
            if (!GetFileInformationByHandle(file, out var information) || (information.Attributes & 0x10) != 0 ||
                (information.IndexHigh == 0 && information.IndexLow == 0))
                throw new InvalidOperationException("An exact executable file identity was unavailable: " + Marshal.GetLastWin32Error());
            var text = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(file, text, (uint)text.Capacity, 0);
            if (length == 0 || length >= text.Capacity)
                throw new InvalidOperationException("The executable's canonical final path was unavailable: " + Marshal.GetLastWin32Error());
            return new ImageIdentity { FinalPath=text.ToString(),
                Identity=information.Volume.ToString("X8") + ":" + information.IndexHigh.ToString("X8") + information.IndexLow.ToString("X8") };
        } finally { CloseHandle(file); }
    }
    public static string ClassName(IntPtr window) { var text = new StringBuilder(256); GetClassName(window, text, text.Capacity); return text.ToString(); }
    private static string Caption(IntPtr window, bool readControl = false) {
        var text = new StringBuilder(128);
        GetWindowText(window, text, text.Capacity);
        if (text.Length == 0 && readControl) {
            UIntPtr result;
            SendMessageTimeout(window, 0x000D, new UIntPtr((uint)text.Capacity), text, 0x0002, 250, out result);
        }
        return text.ToString().Replace("&", "").Trim();
    }
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
    public static NavigationButton[] Buttons(IntPtr root, int expectedPid) {
        if (!IsOwnedVisibleWindow(root, expectedPid) || ClassName(root) != "TWizardForm")
            throw new InvalidOperationException("Native navigation requires the exact visible owned wizard.");
        if (!IsWindowEnabled(root)) return Array.Empty<NavigationButton>();
        var result = new List<NavigationButton>();
        var count = 0;
        var exceeded = false;
        EnumChildWindows(root, (window, _) => {
            if (++count > 512) { exceeded = true; return false; }
            var kind = ClassName(window);
            var caption = Caption(window, kind == "TNewButton" || kind == "Button");
            var navigationCaption = caption == "Next" || caption == "Next >" || caption == "Install" || caption == "Finish";
            if (kind != "TNewButton" && kind != "Button" && !navigationCaption) return true;
            if (result.Count == 32) { exceeded = true; return false; }
            uint pid; GetWindowThreadProcessId(window, out pid);
            result.Add(new NavigationButton { Handle=window, Root=root, Pid=(int)pid, Class=kind, Caption=caption,
                Visible=IsWindowVisible(window), Enabled=IsWindowEnabled(window), InRoot=IsChild(root, window) });
            return true;
        }, IntPtr.Zero);
        if (exceeded) throw new InvalidOperationException("The owned wizard navigation inventory exceeded its bound.");
        return result.ToArray();
    }
    public static void ClickButton(NavigationButton button) {
        var allowed = button.Caption == "Next" || button.Caption == "Next >" || button.Caption == "Install" || button.Caption == "Finish";
        if (!allowed || !IsOwnedVisibleWindow(button.Root, button.Pid) || !IsWindowEnabled(button.Root) ||
            ClassName(button.Root) != "TWizardForm" || !IsOwnedVisibleWindow(button.Handle, button.Pid) ||
            !IsWindowEnabled(button.Handle) || !IsChild(button.Root, button.Handle) ||
            (button.Class != "TNewButton" && button.Class != "Button") || ClassName(button.Handle) != button.Class ||
            Caption(button.Handle, true) != button.Caption)
            throw new InvalidOperationException("The exact owned wizard navigation button changed before input.");
        if (!PostMessage(button.Handle, 0x00F5, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("The exact native wizard button click was refused.");
    }
    private static bool ChoiceClass(string kind) { return kind == "TNewCheckListBox" || kind == "TNewCheckBox" || kind == "Button"; }
    private static bool ChoiceName(string name) {
        return name == "Decide later in TogetherServer" || name == "Host game servers" || name == "Join a friend's server" ||
            name == "Open TogetherServer at Windows sign-in" || name == "Close to the tray instead of quitting" ||
            name == "Create a desktop shortcut" || name == "Open TogetherServer";
    }
    private static void AssertChoiceWindow(IntPtr root, IntPtr control, int pid) {
        if (!IsOwnedVisibleWindow(root, pid) || !IsWindowEnabled(root) || ClassName(root) != "TWizardForm" ||
            !IsOwnedVisibleWindow(control, pid) || !IsChild(root, control) || !ChoiceClass(ClassName(control)))
            throw new InvalidOperationException("Choice discovery requires the exact owned visible wizard control.");
    }
    private static AccessibleRows Rows(IntPtr control) {
        var iid = typeof(AccessibleRows).GUID;
        var hr = AccessibleObjectFromWindow(control, 0xFFFFFFFC, ref iid, out var rows);
        if (hr != 0 || rows == null)
            throw new InvalidOperationException("The owned " + ClassName(control) + " choice control has no native IAccessible provider: " + hr.ToString("X8"));
        return rows;
    }
    private static Choice Row(AccessibleRows rows, IntPtr root, IntPtr control, int pid, int child) {
        Marshal.ThrowExceptionForHR(rows.Role(child, out var roleValue));
        if (!(roleValue is int role)) throw new InvalidOperationException("The native choice role was not an exact integer.");
        if (role != 0x2C && role != 0x2D) return null;
        Marshal.ThrowExceptionForHR(rows.Name(child, out var name));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new InvalidOperationException("The native choice caption was unavailable or unbounded.");
        Marshal.ThrowExceptionForHR(rows.State(child, out var stateValue));
        if (!(stateValue is int state)) throw new InvalidOperationException("The native choice state was unavailable.");
        Marshal.ThrowExceptionForHR(rows.Location(out var left, out var top, out var width, out var height, child));
        return new Choice { Root=root, Handle=control, Pid=pid, ChildId=child, Class=ClassName(control),
            Name=name.Replace("&", "").Trim(), Role=role, State=state, Checked=(state & 0x10) != 0,
            Visible=(state & 0x18000) == 0 && width > 0 && height > 0,
            Enabled=(state & 1) == 0 && IsWindowEnabled(control), Left=left, Top=top, Width=width, Height=height };
    }
    public static Choice[] Choices(IntPtr root, int pid, out string[] inventory) {
        if (!IsOwnedVisibleWindow(root, pid) || !IsWindowEnabled(root) || ClassName(root) != "TWizardForm")
            throw new InvalidOperationException("Native choices require the exact enabled owned wizard.");
        var controls = new List<IntPtr>();
        var count = 0;
        var exceeded = false;
        EnumChildWindows(root, (control, _) => {
            if (++count > 512) { exceeded = true; return false; }
            if (ChoiceClass(ClassName(control)) && IsWindowVisible(control)) {
                if (controls.Count == 16) { exceeded = true; return false; }
                controls.Add(control);
            }
            return true;
        }, IntPtr.Zero);
        if (exceeded) throw new InvalidOperationException("The owned choice control inventory exceeded its bound.");
        var result = new List<Choice>();
        var observed = new List<string>();
        foreach (var control in controls) {
            AssertChoiceWindow(root, control, pid);
            var rows = Rows(control);
            try {
                Marshal.ThrowExceptionForHR(rows.ChildCount(out var children));
                if (children < 0 || children > 32) throw new InvalidOperationException("The owned choice row count exceeded its bound.");
                observed.Add(ClassName(control) + ":" + children);
                var first = ClassName(control) == "TNewCheckListBox" ? 1 : 0;
                var last = first == 1 ? children : 0;
                for (var child = first; child <= last; child++) {
                    var row = Row(rows, root, control, pid, child);
                    if (row != null && row.Visible) result.Add(row);
                    if (result.Count > 32) throw new InvalidOperationException("The visible choice inventory exceeded its bound.");
                }
            } finally { Marshal.ReleaseComObject(rows); }
        }
        inventory = observed.ToArray();
        return result.ToArray();
    }
    public static Choice ReadChoice(Choice expected) {
        AssertChoiceWindow(expected.Root, expected.Handle, expected.Pid);
        if (ClassName(expected.Handle) != expected.Class || !ChoiceName(expected.Name))
            throw new InvalidOperationException("The exact reviewed native choice control changed.");
        var rows = Rows(expected.Handle);
        try {
            var current = Row(rows, expected.Root, expected.Handle, expected.Pid, expected.ChildId);
            if (current == null || current.Name != expected.Name || current.Role != expected.Role || !current.Visible || (current.State & 0x20) != 0)
                throw new InvalidOperationException("The exact native choice row changed or has an indeterminate state.");
            return current;
        } finally { Marshal.ReleaseComObject(rows); }
    }
    public static void ClickChoice(Choice expected) {
        var current = ReadChoice(expected);
        if (!current.Enabled) throw new InvalidOperationException("The exact native choice is disabled.");
        if (!GetWindowRect(current.Handle, out var control) || current.Width > 2400 || current.Height > 1800 ||
            current.Left < control.Left || current.Top < control.Top ||
            (long)current.Left + current.Width > control.Right || (long)current.Top + current.Height > control.Bottom)
            throw new InvalidOperationException("The exact native choice rectangle is outside its owned control.");
        if (current.Class == "TNewCheckListBox")
            ClickRow(current.Handle, current.Left + Math.Min(10, current.Width / 2), current.Top + current.Height / 2);
        else if (!PostMessage(current.Handle, 0x00F5, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("The exact native checkbox click was refused.");
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
    if ($name -in $reviewedChoiceLabels) {
        # Prefer Inno's native row provider even when UIA exposes only a named
        # Pane without a checked-state pattern. The input still targets its real
        # checkbox/radio row and the next read must observe the requested state.
        Assert-Element $root
        $inventory = [string[]]@()
        $choices = @([TogetherServerInstallerUi]::Choices([IntPtr]$root.Current.NativeWindowHandle, $root.Current.ProcessId, [ref]$inventory))
        $key = $currentCase.choice + '|' + ($inventory -join '|')
        if ($choiceInventories.Count -lt 64 -and $choiceInventoryKeys.Add($key)) {
            $observed = @{ choice = $currentCase.choice; controls = @($inventory); visibleChoiceRows = $choices.Count }
            $choiceInventories.Add($observed)
            Write-Host ('WIZARD_CHOICE_INVENTORY ' + ($observed | ConvertTo-Json -Compress))
        }
        foreach ($choice in $choices) { Record-Choice $choice }
        $nativeMatches = @($choices | Where-Object Name -CEQ $name)
        Require ($nativeMatches.Count -le 1) 'The exact native wizard choice caption was ambiguous.'
        if ($nativeMatches.Count) { return $nativeMatches[0] }
    }
    Require ($matches.Count -le 1) 'A named wizard control was ambiguous.'
    if ($matches.Count) { return $matches[0] }
    return $null
}
function Record-Choice($choice) {
    $key = '{0}|{1}|{2}|{3}|{4}' -f $currentCase.choice, $choice.Name, $choice.Role, $choice.Checked, $choice.Enabled
    if ($choiceObservations.Count -lt 128 -and $choiceObservationKeys.Add($key)) {
        $name = if ($choice.Name -in $reviewedChoiceLabels) { $choice.Name } else { '[unreviewed]' }
        $observed = @{ choice = $currentCase.choice; caption = $name; windowClass = $choice.Class; childId = $choice.ChildId;
            role = $choice.Role; checked = $choice.Checked; enabled = $choice.Enabled; visible = $choice.Visible;
            rect = @{ left = $choice.Left; top = $choice.Top; width = $choice.Width; height = $choice.Height }; provider = 'NativeIAccessible' }
        $choiceObservations.Add($observed)
        Write-Host ('WIZARD_CHOICE ' + ($observed | ConvertTo-Json -Depth 3 -Compress))
    }
}
function Assert-NativeChoice($choice) {
    $owner = @($owned | Where-Object id -eq $choice.Pid)
    Require ($owner.Count -eq 1) 'The native choice does not belong to one retained process.'
    Assert-Owned $owner[0]
    $root = Wizard-Root
    Assert-Element $root
    Require ([IntPtr]$root.Current.NativeWindowHandle -eq $choice.Root -and $root.Current.ProcessId -eq $choice.Pid) 'The choice no longer belongs to the exact current wizard.'
}
function Checked($element) {
    if ($element -is [TogetherServerInstallerUi+Choice]) {
        Assert-NativeChoice $element
        $current = [TogetherServerInstallerUi]::ReadChoice($element)
        Record-Choice $current
        return $current.Checked
    }
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
    if ($element -is [TogetherServerInstallerUi+Choice]) { Assert-NativeChoice $element; return }
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
    if ($element -is [TogetherServerInstallerUi+Choice]) {
        Assert-NativeChoice $element
        [TogetherServerInstallerUi]::ClickChoice($element)
        return
    }
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
    Require ($null -ne $root) 'Navigation requires the current wizard root.'
    $rootOwner = @($owned | Where-Object id -eq $root.Current.ProcessId)
    Require ($rootOwner.Count -eq 1) 'The navigation root does not belong to one retained process.'
    Assert-Owned $rootOwner[0]
    $rootHandle = [IntPtr]$root.Current.NativeWindowHandle
    Require ($rootHandle -ne [IntPtr]::Zero -and [TogetherServerInstallerUi]::ClassName($rootHandle) -eq 'TWizardForm' -and
        [TogetherServerInstallerUi]::IsOwnedVisibleWindow($rootHandle, $root.Current.ProcessId)) 'Navigation requires the exact visible owned native wizard root.'
    Require ($names.Count -gt 0 -and @($names | Where-Object { $_ -notin @('Next', 'Next >', 'Install', 'Finish') }).Count -eq 0) 'An unreviewed wizard navigation caption was requested.'
    # Install can temporarily disable its window; discovery waits, while every
    # actual click still requires an enabled current root and enabled control.
    if (![TogetherServerInstallerUi]::IsWindowEnabled($rootHandle)) { return $null }
    $buttons = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button))
    $uiaMatches = @($buttons | Where-Object { !$_.Current.IsOffscreen -and $_.Current.IsEnabled -and
        ($_.Current.Name.Replace('&', '').Trim() -in $names) })
    Require ($uiaMatches.Count -le 1) 'The requested UIA wizard navigation button was ambiguous.'
    $nativeButtons = @([TogetherServerInstallerUi]::Buttons($rootHandle, $root.Current.ProcessId))
    foreach ($button in $nativeButtons) {
        $key = '{0}|{1}|{2}|{3}|{4}|{5}' -f $currentCase.choice, $button.Class, $button.Caption, $button.Visible, $button.Enabled, $button.InRoot
        if ($buttonObservations.Count -lt 128 -and $buttonObservationKeys.Add($key)) {
            $caption = if ($button.Caption -in @('Next', 'Next >', 'Install', 'Finish', 'Cancel', '< Back', 'Back', 'Browse...')) { $button.Caption } else { '[unreviewed]' }
            $uiaType = 'Unavailable'
            try { $uiaType = [Windows.Automation.AutomationElement]::FromHandle($button.Handle).Current.ControlType.ProgrammaticName } catch { }
            $observation = @{ choice = $currentCase.choice; windowClass = $button.Class; caption = $caption; uiaControlType = $uiaType;
                visible = $button.Visible; enabled = $button.Enabled; inWizard = $button.InRoot; ownedPid = ($button.Pid -eq $root.Current.ProcessId) }
            $buttonObservations.Add($observation)
            Write-Host ('WIZARD_NAVIGATION ' + ($observation | ConvertTo-Json -Compress))
        }
    }
    if ($uiaMatches.Count) { return $uiaMatches[0] }
    # Inno native buttons can be exposed as UIA Pane controls. Match their exact
    # reviewed captions and real HWND ancestry rather than relaxing navigation.
    $nativeMatches = @($nativeButtons | Where-Object { $_.Class -in @('TNewButton', 'Button') -and $_.Pid -eq $root.Current.ProcessId -and
        $_.Visible -and $_.Enabled -and $_.InRoot -and $_.Caption -in $names })
    Require ($nativeMatches.Count -le 1) 'The requested native wizard navigation button was ambiguous.'
    if ($nativeMatches.Count) { return $nativeMatches[0] }
    return $null
}
function Invoke-Button($element) {
    if ($element -is [TogetherServerInstallerUi+NavigationButton]) {
        $owner = @($owned | Where-Object id -eq $element.Pid)
        Require ($owner.Count -eq 1) 'The native wizard button does not belong to one retained process.'
        Assert-Owned $owner[0]
        $root = Wizard-Root
        Assert-Element $root
        Require ([IntPtr]$root.Current.NativeWindowHandle -eq $element.Root -and $root.Current.ProcessId -eq $element.Pid) 'The native button no longer belongs to the current wizard.'
        [TogetherServerInstallerUi]::ClickButton($element)
        return
    }
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
    $stopFailure = $null
    try { Refresh-Owned }
    catch { $stopFailure = $_; Remember-Failure $_ 'cleanup-discovery' }
    foreach ($item in @($owned.ToArray()) | Sort-Object ticks -Descending) {
        try {
            $item.process.Refresh()
            if ($item.process.HasExited) { continue }
            Assert-Owned $item
            $item.process.Kill()
            Require ($item.process.WaitForExit(10000)) 'An exact disposable process did not exit during cleanup.'
        } catch {
            $ownedStopFailure = $_
            $exited = $false
            try { $item.process.Refresh(); $exited = $item.process.HasExited } catch { }
            if ($exited) { continue }
            if ($null -eq $stopFailure) { $stopFailure = $ownedStopFailure }
            Remember-Failure $ownedStopFailure 'cleanup-owned-process'
        }
    }
    if ($stopFailure) { throw $stopFailure }
}
function Uninstall-Case {
    if (!$installRoot -or !(Test-Path -LiteralPath $installRoot)) { return }
    $uninstallers = @(Get-ChildItem -LiteralPath $installRoot -Filter 'unins*.exe' -File)
    if (!$uninstallers.Count) { return }
    Require ($uninstallers.Count -eq 1 -and ($uninstallers[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'The disposable uninstaller identity was ambiguous.'
    $uninstall = Add-Owned (Start-Process -FilePath $uninstallers[0].FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -PassThru) $uninstallers[0].FullName -origin 'uninstaller-launch'
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
        $caseFailure = $null
        try {
            New-Item -ItemType Directory -Path (Join-Path $caseRoot $case.choice) -Force | Out-Null
            $currentStage = 'wizard'
            # Path/log isolation arguments only. Every role and preference is
            # chosen through the actual visible wizard, with no silent flags.
            $setup = Add-Owned (Start-Process -FilePath $installer -ArgumentList @('/SP-', '/NORESTART', '/NOCLOSEAPPLICATIONS',
                "/DIR=`"$installRoot`"", "/GROUP=`"$groupName`"", "/LOG=`"$(Join-Path $caseRoot ($case.choice + '/setup.log'))`"") -PassThru) $installer -origin 'installer-launch'
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
                    $currentStage = 'wizard-finish'
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
            $currentStage = 'installer-helper-exit'
            Wait-Until { @($owned | Where-Object { !$_.process.HasExited }).Count -eq 0 } 'The installer or its configure helper did not exit.' 30 | Out-Null
            Require ($setup.process.ExitCode -eq 0 -and $wizardOwner[0].process.ExitCode -eq 0) 'The actual installer setup or wizard exited unsuccessfully.'
            $currentStage = 'installed-file-and-guide'
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
            $app = Add-Owned (Start-Process -FilePath $installedApp -WorkingDirectory $installRoot -PassThru) $installedApp -origin 'installed-app-launch'
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
            $caseFailure = $_
            Remember-Failure $caseFailure 'case'
            $case.state = 'Failed'
            try { Refresh-Owned; Capture-Dialogs 'failure' } catch { }
            throw $caseFailure
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
            $cleanupFailure = $null
            $stageBeforeCleanup = $currentStage
            foreach ($cleanup in @(
                @{ name = 'stop-before-uninstall'; action = { Stop-Owned } },
                @{ name = 'uninstall'; action = { Uninstall-Case } },
                @{ name = 'stop-after-uninstall'; action = { Stop-Owned } },
                @{ name = 'verify'; action = {
                    Require (@(Uninstall-Entries $installRoot).Count -eq 0) 'Disposable installer cleanup left registration.'
                    Remove-EmptyShortcutGroup
                    Require (!(Get-ItemProperty -Path $runKey -Name TogetherServer -ErrorAction SilentlyContinue)) 'Disposable installer cleanup left Windows startup enabled.'
                } }
            )) {
                $currentStage = 'cleanup-' + $cleanup.name
                try { & $cleanup.action }
                catch {
                    $cleanupCompleted = $false
                    if ($null -eq $cleanupFailure) { $cleanupFailure = $_ }
                    Remember-Failure $_ 'case-cleanup'
                }
            }
            $currentStage = $stageBeforeCleanup
            if ($cleanupFailure -and !$caseFailure) { throw $cleanupFailure }
        }
    }
} catch {
    Remember-Failure $_ 'outer'
    if ($currentCase -and $currentCase.state -ne 'Passed') { $currentCase.state = 'Failed' }
    try { Refresh-Owned; if ($currentCase) { Capture-Dialogs 'failure' } } catch { }
} finally {
    $currentStage = 'cleanup-final-owned-processes'
    try { Stop-Owned } catch { $cleanupCompleted = $false; Remember-Failure $_ 'final-cleanup' }
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
            failedStage = $(if ($failure) { $failureStage } else { $null });
            diagnostic = $diagnostic; failures = @($failureObservations.ToArray()); identityObservations = @($identityObservations.ToArray());
            screenshots = @($screenshots.ToArray()); captureObservations = @($captureObservations.ToArray()); buttonObservations = @($buttonObservations.ToArray());
            choiceLabelsExpected = $reviewedChoiceLabels; choiceInventories = @($choiceInventories.ToArray()); choiceObservations = @($choiceObservations.ToArray());
            boundary = 'Actual Inno wizard and fresh installed app on a dedicated hosted Windows runner; synthetic owner save only; no game, credentials or network changes.' }
        $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'summary.json') -Encoding utf8
        if (!$unchanged -and !$failure) { $failure = 'Verified app or installer bytes changed during actual wizard acceptance.' }
    }
    foreach ($item in $owned) { $item.process.Dispose() }
}
if ($failure) { throw $failure }
Write-Host 'Installer UI checks: 3 actual wizard choices passed, 0 failed, 0 skipped.'
