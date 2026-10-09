param(
    [string]$AppPath = '',
    [switch]$AllowInteractiveTests
)
$ErrorActionPreference = 'Stop'
if (!$AllowInteractiveTests) {
    throw 'ForegroundSafety: QoL desktop smoke opens a native app, browser debugging connection and file picker. -AllowInteractiveTests requires explicit owner approval for a separate Windows test PC or unattended CI runner. Never run on the active owner desktop.'
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw 'QoL desktop smoke requires Windows and PowerShell 7 on the approved dedicated runner.'
}

# One disposable DEVELOPMENT process; no installed copy, registry, game Start,
# updater replacement, public listener, terms acceptance or production reads.
$repository = Split-Path -Parent $PSScriptRoot
if (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }
$candidate = (Resolve-Path -LiteralPath $AppPath).Path
$candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
$fixture = Join-Path $repository 'src/TogetherServer.ValheimFixture/bin/Release/net10.0/valheim_server.exe'
if (!(Test-Path -LiteralPath $fixture -PathType Leaf)) { throw 'Build the reviewed Valheim fixture before QoL desktop smoke.' }
if (!(Test-Path -LiteralPath (Join-Path $repository 'ui/node_modules/playwright') -PathType Container)) {
    throw 'Run the locked UI dependency install first; this check uses its Playwright CDP client and downloads no browser.'
}
$node = (Get-Command node -ErrorAction Stop).Source
$caseId = [guid]::NewGuid().ToString('N')
$caseRoot = [IO.Path]::GetFullPath((Join-Path $repository ('local-data/qol-desktop-smoke/' + $caseId)))
$evidenceParent = [IO.Path]::GetFullPath((Join-Path $repository 'local-data/ci-evidence/qol-desktop'))
$evidenceRoot = [IO.Path]::GetFullPath((Join-Path $evidenceParent $caseId))
$testSucceeded = $false
$cleanupSucceeded = $true
$caseResults = [ordered]@{
    appearance = @{ name = 'Native WebView with 150 percent text'; status = 'SKIP'; reason = 'Not reached.' }
    compact = @{ name = 'Compact native window'; status = 'SKIP'; reason = 'Not reached.' }
    controls = @{ name = 'Native window controls and monitor bounds'; status = 'SKIP'; reason = 'Not reached.' }
    tray = @{ name = 'Close to tray and reopen'; status = 'SKIP'; reason = 'Not reached.' }
    picker = @{ name = 'Disposable native file selection'; status = 'SKIP'; reason = 'Not reached.' }
    nativeFlush = @{ name = 'Native Quit protected draft flush'; status = 'SKIP'; reason = 'Not reached.' }
    recovery = @{ name = 'Protected draft and normal placement recovery'; status = 'SKIP'; reason = 'Not reached.' }
    maximized = @{ name = 'Maximized cold start and normal restore'; status = 'SKIP'; reason = 'Not reached.' }
    rejectedFlush = @{ name = 'Rejected draft keeps native app open'; status = 'SKIP'; reason = 'Not reached.' }
    startup = @{ name = 'Hidden startup and duplicate launch'; status = 'SKIP'; reason = 'Not reached.' }
    isolation = @{ name = 'Candidate and fake boundary unchanged'; status = 'SKIP'; reason = 'Not reached.' }
    explorerQuit = @{ name = 'Explorer tray menu Quit'; status = 'SKIP'; reason = 'Not exercised by this smoke.' }
    notifications = @{ name = 'OS balloon delivery and click'; status = 'SKIP'; reason = 'Not exercised by this smoke.' }
    cleanup = @{ name = 'Exact disposable process cleanup'; status = 'SKIP'; reason = 'Not reached.' }
}
$dataRoot = Join-Path $caseRoot 'staging-data'
$fakeProductionRoot = Join-Path $caseRoot 'fake-production-sentinel'
$installRoot = Join-Path $caseRoot 'candidate'
$selectedRoot = Join-Path $caseRoot 'picker-fixture'
New-Item -ItemType Directory -Path $installRoot, $selectedRoot, $fakeProductionRoot -Force | Out-Null
$sentinel = Join-Path $fakeProductionRoot 'untouched.txt'
[IO.File]::WriteAllText($sentinel, 'synthetic production boundary; never read by the development app')
$sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$developmentApp = Join-Path $installRoot 'TogetherServer DEVELOPMENT.exe'
$selectedFixture = Join-Path $selectedRoot 'valheim_server.exe'
Copy-Item -LiteralPath $candidate -Destination $developmentApp
Copy-Item -LiteralPath $fixture -Destination $selectedFixture
if ((Get-FileHash -LiteralPath $developmentApp -Algorithm SHA256).Hash -ne $candidateHash) { throw 'Candidate copy hash mismatch.' }
$listeners = @([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | ForEach-Object Port)
$ports = @()
for ($i = 0; $i -lt 200 -and $ports.Count -lt 2; $i++) {
    $value = Get-Random -Minimum 51000 -Maximum 60000
    if ($listeners -notcontains $value -and $ports -notcontains $value) { $ports += $value }
}
if ($ports.Count -ne 2) { throw 'Two unused loopback app/debugging ports were not found without binding a listener.' }
$port = $ports[0]
$debugPort = $ports[1]
$debugUserDataRoot = Join-Path $dataRoot 'webview2'
$baseUrl = "http://127.0.0.1:$port"
$headers = @{ Origin = $baseUrl; 'X-TogetherServer-Local' = '1' }
$profileId = [guid]::NewGuid().ToString('D')
$draftMarker = 'Synthetic native Quit draft ' + [guid]::NewGuid().ToString('N')
$processes = [Collections.Generic.List[object]]::new()
$app = $null
$nodePhase = $null
$windowHandle = [IntPtr]::Zero

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TogetherServerQolWindowCheck {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int capacity);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindow(IntPtr window, IntPtr value);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindow callback, IntPtr value);
    public static IntPtr FindOwnedDialog(int processId) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner != processId || !IsWindowVisible(window)) return true;
            var name = new System.Text.StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (name.ToString() != "#32770") return true;
            found = window; return false;
        }, IntPtr.Zero);
        return found;
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string[] CommandLineArguments(string commandLine) {
        int count; var memory = CommandLineToArgvW(commandLine, out count);
        if (memory == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try {
            var arguments = new string[count];
            for (int index = 0; index < count; index++) arguments[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, index * IntPtr.Size));
            return arguments;
        } finally { LocalFree(memory); }
    }
}
'@

function Require([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Begin-Case([string]$Key) {
    $caseResults[$Key].status = 'FAIL'
    $caseResults[$Key].reason = 'Case did not complete.'
}
function Pass-Case([string]$Key) {
    $caseResults[$Key].status = 'PASS'
    $caseResults[$Key].reason = 'Completed assertions.'
}
function Wait-Until([scriptblock]$Check, [string]$Message, [int]$Seconds = 25) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { if (& $Check) { return }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw $Message
}
function Record-Process([Diagnostics.Process]$Process, [string]$ExpectedPath) {
    $Process.Refresh()
    # A successful duplicate startup may exit before its creation time is read.
    # An exited identity is never used for mutation or fallback cleanup.
    $ticks = 0L
    try { $ticks = $Process.StartTime.ToUniversalTime().Ticks }
    catch { if (!$Process.HasExited) { throw } }
    $identity = @{ Process = $Process; Id = $Process.Id; Ticks = $ticks; Path = [IO.Path]::GetFullPath($ExpectedPath) }
    $processes.Add($identity)
    return $identity
}
function Assert-AppIdentity {
    Require ($null -ne $app -and !$app.Process.HasExited) 'The disposable development app exited unexpectedly.'
    $app.Process.Refresh()
    Require ($app.Process.StartTime.ToUniversalTime().Ticks -eq $app.Ticks -and
        [IO.Path]::GetFullPath($app.Process.Path).Equals($developmentApp, [StringComparison]::OrdinalIgnoreCase)) 'The test app process identity changed.'
}
function Wait-OwnedWebViewDebugger {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    $lastState = 'No debugging listener was observed.'
    do {
        Assert-AppIdentity
        $connections = @(Get-NetTCPConnection -State Listen -LocalPort $debugPort -ErrorAction SilentlyContinue)
        if ($connections.Count -gt 0) {
            Require (@($connections | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') }).Count -eq 0) 'The disposable WebView debugger did not bind only to loopback.'
            $owners = @($connections.OwningProcess | Sort-Object -Unique)
            Require ($owners.Count -eq 1) 'The debugging port has ambiguous process ownership.'
            $metadata = Get-CimInstance Win32_Process -Filter ("ProcessId = " + $owners[0]) -ErrorAction Stop
            Require ($null -ne $metadata -and
                [IO.Path]::GetFileName($metadata.ExecutablePath) -eq 'msedgewebview2.exe') "The debugging listener is not the disposable app's WebView2 browser."
            $browserProcess = [Diagnostics.Process]::GetProcessById([int]$owners[0])
            try {
                $browserProcess.Refresh()
                $browserTicks = $browserProcess.StartTime.ToUniversalTime().Ticks
                Require (!$browserProcess.HasExited -and $browserTicks -ge $app.Ticks -and
                    [IO.Path]::GetFullPath($browserProcess.Path).Equals([IO.Path]::GetFullPath($metadata.ExecutablePath), [StringComparison]::OrdinalIgnoreCase)) 'The owned WebView2 browser identity changed.'
                $ancestor = $metadata
                $ownedDescendant = $false
                $seenParents = [Collections.Generic.HashSet[int]]::new()
                for ($depth = 0; $depth -lt 8; $depth++) {
                    if ($ancestor.ParentProcessId -eq $app.Id) { $ownedDescendant = $true; break }
                    Require ($ancestor.ParentProcessId -gt 0 -and $seenParents.Add([int]$ancestor.ParentProcessId)) 'The debugger browser has invalid process ancestry.'
                    $ancestor = Get-CimInstance Win32_Process -Filter ("ProcessId = " + $ancestor.ParentProcessId) -ErrorAction Stop
                    Require ($null -ne $ancestor -and
                        [IO.Path]::GetFullPath($ancestor.ExecutablePath).Equals([IO.Path]::GetFullPath($metadata.ExecutablePath), [StringComparison]::OrdinalIgnoreCase)) 'The debugger browser is outside the exact disposable WebView2 process tree.'
                    $ancestorProcess = [Diagnostics.Process]::GetProcessById([int]$ancestor.ProcessId)
                    try {
                        $ancestorTicks = $ancestorProcess.StartTime.ToUniversalTime().Ticks
                        Require (!$ancestorProcess.HasExited -and $ancestorTicks -ge $app.Ticks -and $ancestorTicks -le $browserTicks -and
                            [IO.Path]::GetFullPath($ancestorProcess.Path).Equals([IO.Path]::GetFullPath($metadata.ExecutablePath), [StringComparison]::OrdinalIgnoreCase)) 'The debugger browser ancestor identity changed.'
                    } finally { $ancestorProcess.Dispose() }
                }
                Require $ownedDescendant 'The debugger browser is not a descendant of the exact disposable app.'
                $arguments = [TogetherServerQolWindowCheck]::CommandLineArguments($metadata.CommandLine)
                Require ($arguments -contains "--remote-debugging-port=$debugPort") 'The owned WebView2 browser did not receive the requested debugging port. Elevated WebView2 hosts ignore environment overrides; use a separately approved standard-integrity runner or an explicit staging-only API option.'
                $profileArgument = @($arguments | Where-Object { $_.StartsWith('--user-data-dir=', [StringComparison]::Ordinal) })
                Require ($profileArgument.Count -eq 1 -and
                    [IO.Path]::GetFullPath($profileArgument[0].Substring('--user-data-dir='.Length)).Equals($debugUserDataRoot, [StringComparison]::OrdinalIgnoreCase)) 'The debugger browser profile is outside the disposable staging data.'
                try {
                    $version = Invoke-RestMethod "http://127.0.0.1:$debugPort/json/version" -TimeoutSec 2 -NoProxy
                    $socket = $null
                    Require ([Uri]::TryCreate([string]$version.webSocketDebuggerUrl, [UriKind]::Absolute, [ref]$socket) -and
                        $socket.Scheme -eq 'ws' -and $socket.Host -eq '127.0.0.1' -and $socket.Port -eq $debugPort -and
                        $socket.AbsolutePath -match '^/devtools/browser/[a-zA-Z0-9-]{1,128}$' -and
                        !$socket.UserInfo -and !$socket.Query -and !$socket.Fragment) 'The owned debugger advertised an invalid loopback browser transport.'
                    $current = [Diagnostics.Process]::GetProcessById([int]$owners[0])
                    try {
                        Require (!$current.HasExited -and $current.StartTime.ToUniversalTime().Ticks -eq $browserTicks -and
                            [IO.Path]::GetFullPath($current.Path).Equals([IO.Path]::GetFullPath($metadata.ExecutablePath), [StringComparison]::OrdinalIgnoreCase)) 'The WebView2 browser changed during debugger readiness.'
                    } finally { $current.Dispose() }
                    Assert-AppIdentity
                    return $socket.AbsoluteUri
                } catch [Microsoft.PowerShell.Commands.HttpResponseException] { $lastState = 'Owned loopback debugger HTTP metadata was not ready.' }
                  catch [System.Net.Http.HttpRequestException] { $lastState = 'Owned loopback debugger transport was not ready.' }
                  catch [System.Threading.Tasks.TaskCanceledException] { $lastState = 'Owned loopback debugger metadata timed out.' }
            } finally { $browserProcess.Dispose() }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $elevated = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    throw ("The disposable WebView2 debugger was not ready within 25 seconds. HostElevated=$elevated. $lastState Elevated hosts ignore WEBVIEW2 environment flags; no unrelated endpoint was contacted.")
}
function Start-TestApp([switch]$Startup) {
    $start = [Diagnostics.ProcessStartInfo]::new($developmentApp)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $caseRoot
    $start.ArgumentList.Add($(if ($Startup) { '--startup' } else { '--desktop' }))
    $start.ArgumentList.Add('--port'); $start.ArgumentList.Add([string]$port)
    $start.EnvironmentVariables['TOGETHERSERVER_DATA_DIR'] = $fakeProductionRoot
    $start.EnvironmentVariables['TOGETHERSERVER_STAGING_DATA_DIR'] = $dataRoot
    # This is consumed only by the marked staging app's CI debug API option.
    # It never changes the parent environment or a registry/policy setting.
    $start.EnvironmentVariables['TOGETHERSERVER_CI_WEBVIEW_DEBUG_PORT'] = [string]$debugPort
    $start.EnvironmentVariables['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = "--remote-debugging-port=$debugPort --remote-debugging-address=127.0.0.1"
    $process = [Diagnostics.Process]::Start($start)
    return Record-Process $process $developmentApp
}
function Wait-TestApp([bool]$Visible) {
    Wait-Until {
        Assert-AppIdentity
        try {
            $instance = Invoke-RestMethod "$baseUrl/api/local/instance" -TimeoutSec 2
            Require ($instance.isStaging -and [IO.Path]::GetFullPath($instance.dataRoot).Equals($dataRoot, [StringComparison]::OrdinalIgnoreCase)) 'App escaped the disposable staging root.'
            $window = Invoke-RestMethod "$baseUrl/api/local/window" -TimeoutSec 2
            if ($window.loadState -eq 'Failed') { throw "Native WebView failed: $($window.loadErrorCode) $($window.loadFailureKind)" }
            return $window.available -and $window.rendered -and $window.customChrome -and $window.visible -eq $Visible
        } catch [System.Net.Http.HttpRequestException] { return $false }
        catch [System.Net.WebException] { return $false }
    } 'The development WebView did not reach the expected rendered/visibility state.'
    if ($Visible) {
        $app.Process.Refresh()
        $script:windowHandle = $app.Process.MainWindowHandle
        Assert-OwnedWindow $windowHandle
    }
}
function Assert-OwnedWindow([IntPtr]$Handle) {
    Assert-AppIdentity
    [uint32]$owner = 0
    [TogetherServerQolWindowCheck]::GetWindowThreadProcessId($Handle, [ref]$owner) | Out-Null
    Require ($Handle -ne [IntPtr]::Zero -and $owner -eq $app.Id) 'Refusing to touch a window outside the exact disposable app.'
}
function Read-Rect {
    Assert-OwnedWindow $windowHandle
    $rect = [TogetherServerQolWindowCheck+Rect]::new()
    Require ([TogetherServerQolWindowCheck]::GetWindowRect($windowHandle, [ref]$rect)) 'Could not read the owned window bounds.'
    return $rect
}
function Chrome-Pattern([string]$Name) {
    Assert-OwnedWindow $windowHandle
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    Require ($null -ne $element) "The owned native title-bar control is unavailable: $Name"
    return $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
}
function Invoke-Chrome([string]$Name) { (Chrome-Pattern $Name).Invoke() }
function Post-Json([string]$Path, $Body = $null, [string]$Method = 'POST') {
    $arguments = @{ Uri = $baseUrl + $Path; Method = $Method; Headers = $headers; TimeoutSec = 25 }
    if ($null -ne $Body) { $arguments.ContentType = 'application/json'; $arguments.Body = $Body | ConvertTo-Json -Depth 20 -Compress }
    return Invoke-RestMethod @arguments
}

# CDP attaches only to this disposable WebView2. It never launches Chromium or
# calls Browser.close on the remote native browser. A process exit detaches it.
$nodeScript = Join-Path $caseRoot 'native-webview.mjs'
[IO.File]::WriteAllText($nodeScript, @'
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const [repository, mode, debugEndpoint, base, profileId, caseRoot, marker] = process.argv.slice(2);
const require = createRequire(path.join(repository, 'ui', 'package.json'));
const { chromium } = require('playwright');
const out = name => {
  const value = path.resolve(caseRoot, name);
  assert.ok(value.startsWith(path.resolve(caseRoot) + path.sep));
  return value;
};
const headers = { Origin: base, 'X-TogetherServer-Local': '1' };
let browser;
async function json(url, body) {
  const response = await fetch(base + url, { method: body === undefined ? 'GET' : 'POST', headers: { ...headers, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
  assert.ok(response.ok, `local HTTP ${response.status}`);
  return response.json();
}
try {
  const transport = new URL(debugEndpoint);
  assert.equal(transport.protocol, 'ws:'); assert.equal(transport.hostname, '127.0.0.1');
  assert.match(transport.pathname, /^\/devtools\/browser\/[a-zA-Z0-9-]{1,128}$/);
  assert.equal(transport.search, ''); assert.equal(transport.hash, '');
  browser = await chromium.connectOverCDP(debugEndpoint, { timeout: 25000 });
  const pages = browser.contexts().flatMap(context => context.pages());
  const page = pages.find(candidate => new URL(candidate.url()).origin === base);
  assert.ok(page, 'The owned local WebView page was not present in CDP.');
  page.setDefaultTimeout(25000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message.slice(0, 300)));
  const workspaces = page.getByRole('navigation', { name: 'TogetherServer workspaces', exact: true });
  const openChat = async () => {
    await workspaces.getByRole('button', { name: 'Host', exact: true }).click();
    await page.locator('.server-master-item').filter({ hasText: 'Native desktop fixture' }).click();
    await page.getByRole('navigation', { name: 'Selected server sections', exact: true }).getByRole('button', { name: 'Chat', exact: true }).click();
    await page.getByLabel('Message', { exact: true }).waitFor();
  };
  if (mode === 'appearance') {
    await workspaces.getByRole('button', { name: 'Settings', exact: true }).click();
    await page.getByLabel('Text size', { exact: true }).selectOption('150');
    await page.getByLabel('Layout', { exact: true }).selectOption('compact');
    await page.waitForFunction(() => document.documentElement.style.getPropertyValue('--qol-text-scale') === '1.5');
    await page.screenshot({ path: out('native-150-percent.png') });
    await openChat();
    await page.screenshot({ path: out('native-chat-150-percent.png') });
  } else if (mode === 'arm-draft' || mode === 'arm-failed-draft') {
    await openChat();
    const identity = { purpose: 'chat', profileId, connectionId: null, key: 'compose' };
    const baseline = await json('/api/local/ui-drafts/read', identity);
    assert.equal(baseline.ok, true); assert.equal(baseline.text, null);
    let observed = false;
    const held = [];
    const settleDraft = route => mode === 'arm-failed-draft'
      ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ok: false, text: null, revision: baseline.revision, message: 'Synthetic draft storage rejection.' }) })
      : route.continue();
    await page.exposeBinding('__qolSmokeNativeFlush', async (_source, requestId) => {
      assert.match(requestId, /^[0-9a-f-]{36}$/i);
      observed = true;
      fs.writeFileSync(out(`${mode}-native-request.json`), JSON.stringify({ observed: true }));
      for (const route of held.splice(0)) await settleDraft(route);
    });
    await page.evaluate(() => {
      window.__qolSmokeNativeListener = event => {
        if (event.data?.type === 'together-flush-drafts') void window.__qolSmokeNativeFlush(event.data.requestId);
      };
      window.chrome.webview.addEventListener('message', window.__qolSmokeNativeListener);
    });
    await page.route('**/api/local/ui-drafts', async route => {
      const body = route.request().postDataJSON();
      if (body?.purpose !== 'chat' || body.profileId !== profileId || body.text !== marker) return route.continue();
      if (!observed) { held.push(route); return; }
      return settleDraft(route);
    });
    await page.getByLabel('Message', { exact: true }).fill(marker);
    await page.waitForFunction(() => document.querySelector('.server-chat-compose')?.textContent?.includes('Saving unfinished message'));
    const beforeQuit = await json('/api/local/ui-drafts/read', identity);
    assert.equal(beforeQuit.text, null, 'Draft must be absent until native Quit asks the page to flush.');
    fs.writeFileSync(out(`${mode}-ready.json`), JSON.stringify({ ready: true }));
    if (mode === 'arm-failed-draft') {
      await page.waitForFunction(() => document.querySelector('.action-feedback,.notice,.helper-text')?.textContent?.includes('protected draft') || document.body.textContent.includes('could not be saved as a protected draft'));
      await page.screenshot({ path: out('native-quit-draft-failure.png') });
    } else {
      // CDP transport disappears on native process exit; the PowerShell owner
      // separately verifies that the native request happened and reads recovery.
      await Promise.race([new Promise(resolve => browser.once('disconnected', resolve)), new Promise(resolve => setTimeout(resolve, 30000))]);
    }
  } else if (mode === 'recovery') {
    const result = await json('/api/local/ui-drafts/read', { purpose: 'chat', profileId, connectionId: null, key: 'compose' });
    assert.equal(result.ok, true); assert.equal(result.text, marker);
    await openChat();
    assert.equal(await page.getByLabel('Message', { exact: true }).inputValue(), '');
    await page.getByRole('button', { name: 'Use recovered message', exact: true }).click();
    assert.equal(await page.getByLabel('Message', { exact: true }).inputValue(), marker);
    await page.screenshot({ path: out('native-quit-recovered-draft.png') });
  } else if (mode === 'clear') {
    const identity = { purpose: 'chat', profileId, connectionId: null, key: 'compose' };
    const draft = await json('/api/local/ui-drafts/read', identity);
    const cleared = await json('/api/local/ui-drafts/clear', { ...identity, expectedRevision: draft.revision });
    assert.equal(cleared.ok, true);
    await page.reload(); await page.locator('.shell').waitFor();
  } else if (mode === 'picker') {
    const selected = await json('/api/local/valheim/browse-server', {});
    assert.equal(selected.ok, true); assert.equal(selected.code, 'ServerSelected');
    assert.equal(path.resolve(selected.executablePath), path.resolve(caseRoot, 'picker-fixture', 'valheim_server.exe'));
    fs.writeFileSync(out('picker-result.json'), JSON.stringify({ selectedDisposableFixture: true }));
  } else if (mode === 'inspect' || mode === 'inspect-compact') {
    await page.locator('.shell').waitFor();
    await page.screenshot({ path: out(mode === 'inspect-compact' ? 'native-compact-150-percent.png' : 'native-final-window.png') });
  } else throw new Error('Unknown fixed smoke phase.');
  assert.deepEqual(errors, [], 'Native WebView had uncaught page errors.');
  if (browser.isConnected() && !page.isClosed()) {
    await page.unrouteAll({ behavior: 'wait' });
    await page.evaluate(() => {
      if (window.__qolSmokeNativeListener) window.chrome.webview.removeEventListener('message', window.__qolSmokeNativeListener);
      delete window.__qolSmokeNativeListener;
    });
  }
  fs.writeSync(1, `PASS native WebView phase ${mode}\n`);
  process.exit(0);
} catch (error) {
  fs.writeSync(2, `FAIL native WebView phase ${mode}: ${String(error.message).slice(0, 600)}\n`);
  process.exit(1);
}
'@, [Text.UTF8Encoding]::new($false))

function Start-WebViewPhase([string]$Phase, [string]$Marker = $draftMarker) {
    Assert-AppIdentity
    $debugEndpoint = Wait-OwnedWebViewDebugger
    $start = [Diagnostics.ProcessStartInfo]::new($node)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @($nodeScript, $repository, $Phase, $debugEndpoint, $baseUrl, $profileId, $caseRoot, $Marker)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $identity = Record-Process $process $node
    return @{ Identity = $identity; Output = $process.StandardOutput.ReadToEndAsync(); Error = $process.StandardError.ReadToEndAsync(); Phase = $Phase }
}
function Finish-WebViewPhase($Phase, [int]$Seconds = 35) {
    Require ($Phase.Identity.Process.WaitForExit($Seconds * 1000)) "Native WebView phase timed out: $($Phase.Phase)"
    $output = $Phase.Output.GetAwaiter().GetResult()
    $errorText = $Phase.Error.GetAwaiter().GetResult()
    if ($output) { Write-Host $output.Trim() }
    Require ($Phase.Identity.Process.ExitCode -eq 0) "Native WebView phase failed: $($Phase.Phase) $errorText"
}
function Run-WebViewPhase([string]$Phase) { Finish-WebViewPhase (Start-WebViewPhase $Phase) }
function Wait-PhaseReady($Phase) {
    $path = Join-Path $caseRoot ($Phase.Phase + '-ready.json')
    Wait-Until {
        if ($Phase.Identity.Process.HasExited) { Finish-WebViewPhase $Phase; throw 'The native draft phase exited without readiness.' }
        return Test-Path -LiteralPath $path
    } 'The native draft composer was not armed.'
}
function Require-RectNear($Expected) {
    $current = Read-Rect
    Require ([Math]::Abs($current.Left - $Expected.Left) -le 12 -and [Math]::Abs($current.Top - $Expected.Top) -le 12 -and
        [Math]::Abs(($current.Right - $current.Left) - ($Expected.Right - $Expected.Left)) -le 12 -and
        [Math]::Abs(($current.Bottom - $current.Top) - ($Expected.Bottom - $Expected.Top)) -le 12) 'Native saved bounds did not restore within the DPI tolerance.'
}
function Dialog-Element([IntPtr]$Dialog, [string]$Name, [string]$AutomationId = '') {
    Assert-OwnedWindow $Dialog
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Dialog)
    $element = $null
    if ($AutomationId) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
        $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    if (!$element) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
        $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    Require ($null -ne $element) "Unsupported OS popup: owned dialog control is unavailable ($Name / $AutomationId)."
    return $element
}

try {
    Begin-Case 'appearance'
    $app = Start-TestApp
    Wait-TestApp $true
    $snapshot = Invoke-RestMethod "$baseUrl/api/local/snapshot" -TimeoutSec 3
    $settings = $snapshot.settings
    $settings.profiles = @(@{ id = $profileId; kind = 'Fixture'; name = 'Native desktop fixture'; worldId = 'native-fixture';
        worldSource = 'New'; worldDirectory = (Join-Path $dataRoot 'worlds/native-fixture'); gamePort = 2458;
        executablePath = (Join-Path $caseRoot 'never-launched/TogetherServer.Fixture.exe') })
    $settings.remoteControlsEnabled = $false; $settings.companionListeningEnabled = $false
    Require ((Post-Json '/api/local/settings' $settings 'PUT').ok) 'Disposable fixture profile metadata was not accepted.'
    Run-WebViewPhase 'appearance'
    Pass-Case 'appearance'

    $dpi = [TogetherServerQolWindowCheck]::GetDpiForWindow($windowHandle)
    if ($dpi -eq 0) { $dpi = 96 }
    $area = [System.Windows.Forms.Screen]::FromHandle($windowHandle).WorkingArea
    $compactWidth = [int][Math]::Round(390 * $dpi / 96)
    $compactHeight = [int][Math]::Round(600 * $dpi / 96)
    if ($area.Width -ge $compactWidth -and $area.Height -ge $compactHeight) {
        Begin-Case 'compact'
        Require ([TogetherServerQolWindowCheck]::MoveWindow($windowHandle, $area.Left, $area.Top, $compactWidth, $compactHeight, $true)) 'Could not apply the compact native window size.'
        $compact = Read-Rect
        Require ([Math]::Abs(($compact.Right - $compact.Left) - $compactWidth) -le 8 -and
            [Math]::Abs(($compact.Bottom - $compact.Top) - $compactHeight) -le 8) 'Native minimum bounds rejected the supported 390x600 logical size.'
        Run-WebViewPhase 'inspect-compact'
        Write-Host 'PASS native compact 390x600 logical window with the persisted 150% text setting'
        Pass-Case 'compact'
    } else {
        $caseResults.compact.reason = 'Runner work area is too small for the requested logical size.'
        Write-Host 'SKIP compact 390x600 native window: the dedicated runner monitor work area is smaller than that logical size.'
    }
    Begin-Case 'controls'
    $width = [Math]::Min([int](920 * $dpi / 96), $area.Width - 40)
    $height = [Math]::Min([int](640 * $dpi / 96), $area.Height - 40)
    Require ([TogetherServerQolWindowCheck]::MoveWindow($windowHandle, $area.Left + 20, $area.Top + 20, $width, $height, $true)) 'Could not move the owned native window.'
    $remembered = Read-Rect
    Wait-Until { Test-Path -LiteralPath (Join-Path $dataRoot 'qol-desktop.json') } 'Window placement was not written.'
    Invoke-Chrome 'Maximize TogetherServer DEVELOPMENT'
    Wait-Until { [TogetherServerQolWindowCheck]::IsZoomed($windowHandle) } 'Native maximize did not take effect.'
    $maximized = Read-Rect
    Require ($maximized.Left -ge $area.Left - 8 -and $maximized.Top -ge $area.Top - 8 -and
        $maximized.Right -le $area.Right + 8 -and $maximized.Bottom -le $area.Bottom + 8) 'Maximized window escaped its current monitor work area.'
    Invoke-Chrome 'Restore TogetherServer DEVELOPMENT'
    Wait-Until { ![TogetherServerQolWindowCheck]::IsZoomed($windowHandle) } 'Native restore did not take effect.'
    Require-RectNear $remembered
    Invoke-Chrome 'Minimize TogetherServer DEVELOPMENT'
    Wait-Until { [TogetherServerQolWindowCheck]::IsIconic($windowHandle) } 'Native minimize did not take effect.'
    Require ((Post-Json '/api/local/show').ok) 'Owner-local reopen failed.'
    Wait-TestApp $true
    Require (![TogetherServerQolWindowCheck]::IsIconic($windowHandle)) 'Reopen did not restore a minimized native window.'
    Require-RectNear $remembered
    Write-Host 'PASS native monitor bounds, minimize/maximize/restore, and rendered 150% text in the real WebView2'
    Pass-Case 'controls'

    Begin-Case 'tray'
    Require ((Post-Json '/api/local/desktop/preferences' @{ closeToTray = $true } 'PUT').ok) 'Close-to-tray could not be enabled in the disposable instance.'
    $originalId = $app.Id; $originalTicks = $app.Ticks
    Invoke-Chrome 'Close TogetherServer DEVELOPMENT'
    Wait-TestApp $false
    Require ($app.Id -eq $originalId -and $app.Ticks -eq $originalTicks) 'Close-to-tray replaced the app process.'
    Require ((Post-Json '/api/local/show').ok) 'The tray-hidden native window did not reopen.'
    Wait-TestApp $true
    Require-RectNear $remembered
    Write-Host 'PASS Close-to-tray hides and reopens the exact same native app'
    Pass-Case 'tray'

    # The actual native OpenFileDialog receives only a compiled, copied fixture.
    Begin-Case 'picker'
    $nodePhase = Start-WebViewPhase 'picker'
    $dialog = [IntPtr]::Zero
    Wait-Until { $script:dialog = [TogetherServerQolWindowCheck]::FindOwnedDialog($app.Id); return $dialog -ne [IntPtr]::Zero } 'The owned native file picker did not open.'
    $fileName = Dialog-Element $dialog 'File name:' '1148'
    $valuePattern = $null
    Require ($fileName.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) 'Unsupported OS popup: the owned file-name control has no ValuePattern.'
    $valuePattern.SetValue($selectedFixture)
    (Dialog-Element $dialog 'Open' '1').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Finish-WebViewPhase $nodePhase; $nodePhase = $null
    Require (Test-Path -LiteralPath (Join-Path $caseRoot 'picker-result.json')) 'Native picker did not select the disposable fixture.'
    Write-Host 'PASS native picker selects the reviewed disposable file without executing it'
    Pass-Case 'picker'

    Begin-Case 'nativeFlush'
    Require ((Post-Json '/api/local/desktop/preferences' @{ closeToTray = $false } 'PUT').ok) 'Close-to-tray could not be disabled.'
    $closePattern = Chrome-Pattern 'Close TogetherServer DEVELOPMENT'
    $nodePhase = Start-WebViewPhase 'arm-draft'
    Wait-PhaseReady $nodePhase
    $closePattern.Invoke()
    Require ($app.Process.WaitForExit(25000)) 'Native Quit did not complete its draft acknowledgement and guarded HTTP shutdown within 25 seconds.'
    Require (Test-Path -LiteralPath (Join-Path $caseRoot 'arm-draft-native-request.json')) 'Native Close bypassed the draft-flush bridge.'
    Finish-WebViewPhase $nodePhase; $nodePhase = $null
    Write-Host 'PASS native Close requests draft flush before Quit; a held autosave cannot hide a missing handshake'
    Pass-Case 'nativeFlush'

    Begin-Case 'recovery'
    $app = Start-TestApp
    Wait-TestApp $true
    Require-RectNear $remembered
    Run-WebViewPhase 'recovery'
    Run-WebViewPhase 'clear'
    Write-Host 'PASS exact native window bounds and protected unfinished chat survive a process restart'
    Pass-Case 'recovery'

    Begin-Case 'maximized'
    Invoke-Chrome 'Maximize TogetherServer DEVELOPMENT'
    Wait-Until { [TogetherServerQolWindowCheck]::IsZoomed($windowHandle) } 'The cold-start maximized placement case did not maximize.'
    Invoke-Chrome 'Close TogetherServer DEVELOPMENT'
    Require ($app.Process.WaitForExit(25000)) 'Native Quit from a maximized window did not finish.'
    $app = Start-TestApp
    Wait-TestApp $true
    Require ([TogetherServerQolWindowCheck]::IsZoomed($windowHandle)) 'Saved maximized native placement was not restored at cold start.'
    Invoke-Chrome 'Restore TogetherServer DEVELOPMENT'
    Wait-Until { ![TogetherServerQolWindowCheck]::IsZoomed($windowHandle) } 'Cold-start maximized placement could not restore.'
    Require-RectNear $remembered
    Write-Host 'PASS maximized placement survives cold start and restores the same prior normal bounds'
    Pass-Case 'maximized'

    Begin-Case 'rejectedFlush'
    $closePattern = Chrome-Pattern 'Close TogetherServer DEVELOPMENT'
    $nodePhase = Start-WebViewPhase 'arm-failed-draft' ('Synthetic failed draft ' + [guid]::NewGuid().ToString('N'))
    Wait-PhaseReady $nodePhase
    $closePattern.Invoke()
    $dialog = [IntPtr]::Zero
    Wait-Until { $script:dialog = [TogetherServerQolWindowCheck]::FindOwnedDialog($app.Id); return $dialog -ne [IntPtr]::Zero } 'Native Quit did not explain the rejected draft acknowledgement.'
    Assert-AppIdentity
    Require (Test-Path -LiteralPath (Join-Path $caseRoot 'arm-failed-draft-native-request.json')) 'Failure case did not use the native flush bridge.'
    (Dialog-Element $dialog 'OK' '1').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Finish-WebViewPhase $nodePhase; $nodePhase = $null
    Wait-TestApp $true
    Run-WebViewPhase 'clear'
    Write-Host 'PASS a synthetic storage rejection leaves the actual native app open with an owner-visible explanation'
    Pass-Case 'rejectedFlush'

    Begin-Case 'startup'
    Invoke-Chrome 'Close TogetherServer DEVELOPMENT'
    Require ($app.Process.WaitForExit(25000)) 'The clean native window did not quit.'
    $app = Start-TestApp -Startup
    Wait-TestApp $false
    $duplicate = Start-TestApp -Startup
    Require ($duplicate.Process.WaitForExit(10000)) 'Duplicate startup launch did not return to the existing app.'
    Wait-TestApp $false
    Require ((Post-Json '/api/local/show').ok) 'Startup-hidden native window did not open.'
    Wait-TestApp $true
    Require-RectNear $remembered
    Run-WebViewPhase 'inspect'
    Invoke-Chrome 'Close TogetherServer DEVELOPMENT'
    Require ($app.Process.WaitForExit(25000)) 'The startup-hidden app did not later quit through native Close.'
    Write-Host 'PASS startup and duplicate startup keep the GUI hidden; reopening restores the same saved native bounds'
    Pass-Case 'startup'

    Begin-Case 'isolation'
    Require ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -eq $candidateHash) 'Original candidate bytes changed.'
    Require ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $sentinelHash) 'The fake production marker changed.'
    Pass-Case 'isolation'
    Write-Host 'SKIP Explorer tray-context-menu Quit and OS balloon delivery/click: this smoke proves the shared native Close/Quit path and tray hiding, not OS notification acceptance.'
    Write-Host 'No game was started; no updater replacement, installer, terms acceptance, public listener or personal registry setting was used.'
    Write-Host "QoL native artifacts and synthetic state: $caseRoot"
    $testSucceeded = $true
}
finally {
    Begin-Case 'cleanup'
    # Cancel only a dialog owned by the exact disposable app, then stop only
    # retained PID/start-time/path identities. No name-based or recursive cleanup.
    if ($app -and !$app.Process.HasExited) {
        try {
            Assert-AppIdentity
            $dialog = [TogetherServerQolWindowCheck]::FindOwnedDialog($app.Id)
            if ($dialog -ne [IntPtr]::Zero) { [TogetherServerQolWindowCheck]::PostMessage($dialog, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
        } catch { $cleanupSucceeded = $false; Write-Warning 'Could not cancel the disposable native dialog during cleanup.' }
    }
    foreach ($identity in $processes) {
        try {
            $process = $identity.Process
            if ($process.HasExited) { continue }
            $process.Refresh()
            if ($process.Id -eq $identity.Id -and $process.StartTime.ToUniversalTime().Ticks -eq $identity.Ticks -and
                [IO.Path]::GetFullPath($process.Path).Equals($identity.Path, [StringComparison]::OrdinalIgnoreCase)) {
                $process.Kill($true)
                if (!$process.WaitForExit(5000)) { $cleanupSucceeded = $false }
            } else { $cleanupSucceeded = $false; Write-Warning 'Disposable process identity changed; cleanup refused to stop it.' }
        } catch { $cleanupSucceeded = $false; Write-Warning 'A recorded disposable process could not be stopped during cleanup.' }
    }
    if ($cleanupSucceeded) { Pass-Case 'cleanup' }

    # Public CI evidence is a strict screenshot-only export. Never upload the
    # staging tree, manifests, drafts, binaries, helper scripts or raw errors.
    Require ([IO.Path]::GetDirectoryName($evidenceRoot).Equals($evidenceParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($evidenceRoot) -eq $caseId -and $caseId -match '^[0-9a-f]{32}$') 'Invalid bounded evidence destination.'
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    $exportSucceeded = $true
    $screenshots = [Collections.Generic.List[string]]::new()
    foreach ($picture in @(Get-ChildItem -LiteralPath $caseRoot -Filter '*.png' -File -ErrorAction SilentlyContinue)) {
        if (($picture.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $exportSucceeded = $false; continue }
        try {
            Copy-Item -LiteralPath $picture.FullName -Destination (Join-Path $evidenceRoot $picture.Name) -ErrorAction Stop
            $screenshots.Add($picture.Name)
        } catch { $exportSucceeded = $false; Write-Warning 'A top-level screenshot could not be exported.' }
    }
    $success = $testSucceeded -and $cleanupSucceeded -and $exportSucceeded
    $summary = [ordered]@{
        schemaVersion = 1
        suite = 'QoL native desktop smoke'
        success = $success
        result = $(if ($success) { 'PASS' } else { 'FAIL' })
        testAssertionsCompleted = $testSucceeded
        cleanupCompleted = $cleanupSucceeded
        screenshotExportCompleted = $exportSucceeded
        cases = @($caseResults.Values)
        screenshots = @($screenshots.ToArray())
        boundary = 'Dedicated approved Windows runner; synthetic state only. Real games, installer, updater replacement and OS notification acceptance are not established.'
    }
    [IO.File]::WriteAllText((Join-Path $evidenceRoot 'summary.json'), ($summary | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Host 'Redacted native CI evidence exported: top-level screenshots and fixed result summary only.'
    if ($testSucceeded -and !$success) { throw 'Native assertions completed, but disposable cleanup or screenshot evidence export failed.' }
}
