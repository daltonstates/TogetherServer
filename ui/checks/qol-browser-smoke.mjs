/* global document, window, getComputedStyle */
// The owner authorized a separate Windows CI runner. This file is never a local focus-safe check.
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createHash, randomInt, randomUUID } from 'node:crypto'
import { appendFile, copyFile, lstat, mkdir, mkdtemp, readFile, readdir, realpath, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const options = parseArguments(process.argv.slice(2))
if (!options.allowInteractive || process.platform !== 'win32') {
  throw new Error('ForegroundSafety: QoL browser smoke requires --allow-interactive-tests and explicit owner approval for a separate Windows test PC or unattended CI runner. Never run on the active owner desktop.')
}
assert(options.appPath && path.isAbsolute(options.appPath), 'Supply the exact absolute candidate with --app-path.')
const appPath = await realpath(options.appPath)
assert((await lstat(appPath)).isFile() && path.extname(appPath).toLowerCase() === '.exe', 'The candidate must be an existing Windows EXE.')
const candidateSha256 = createHash('sha256').update(await readFile(appPath)).digest('hex')
const ownerId = randomUUID()
const caseRoot = await mkdtemp(path.join(await realpath(tmpdir()), 'togetherserver-qol-browser-'))
await writeFile(path.join(caseRoot, '.qol-smoke-owner'), ownerId, { flag: 'wx' })
const outputRoot = path.resolve(options.outputDir ?? path.join(repository, 'local-data/qol-browser'))
const evidenceRoot = path.join(outputRoot, ownerId)
await mkdir(evidenceRoot, { recursive: true })
const instances = []
const ownedProcesses = new Map()
const ownedFixtureExecutables = new Set()
const claimedPorts = new Set()
const report = { schema: 1, candidateSha256, boundary: 'Bundled React and loopback synthetic fixtures in a separately approved Windows environment. No real game, WAN, join or save acceptance.',
  journeys: [], screenshots: [], layout: [], browserErrors: [], failedRequests: [], expectedModeDenials: [], deduplicatedConsoleHttpErrors: [],
  cleanup: [], startedUtc: new Date().toISOString() }
const browserCollectors = []
const pendingResponseClassifications = new Set()
let browser
let failed = false
let stopping = false
let cleanupPromise

function parseArguments(args) {
  const parsed = { allowInteractive: false, appPath: '', outputDir: '' }
  for (let index = 0; index < args.length; index++) {
    const argument = args[index]
    if (argument === '--allow-interactive-tests') parsed.allowInteractive = true
    else if (argument === '--app-path' || argument === '--output-dir') {
      assert(args[index + 1] && !args[index + 1].startsWith('--'), `${argument} requires a value.`)
      parsed[argument === '--app-path' ? 'appPath' : 'outputDir'] = args[++index]
    } else throw new Error('Use --app-path <absolute EXE> --allow-interactive-tests [--output-dir <artifact folder>].')
  }
  if (!parsed.outputDir) delete parsed.outputDir
  return parsed
}

const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))
function inside(root, target) {
  const relative = path.relative(root, target)
  return relative !== '' && relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative)
}
function redact(value) {
  return String(value).replaceAll(caseRoot, '[disposable data]').replaceAll(appPath, '[candidate]')
    .replace(/TS[123]-[^\s"'<>]+/gu, '[synthetic code]')
    .replace(/[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}/giu, '[id]')
    .replace(/(?:https?:\/\/|[A-Za-z]:[\\/])[^\s"'<>]+/gu, '[location]').slice(0, 600)
}
async function boundedResponseJson(response, maximumBytes = 2048) {
  const headers = response.headers()
  if (!/^application\/json(?:;|$)/iu.test(headers['content-type'] ?? '')) return null
  const length = headers['content-length']
  if (length !== undefined && (!/^\d{1,9}$/u.test(length) || Number(length) > maximumBytes)) return null
  let timer
  try {
    const bytes = await Promise.race([
      response.body(),
      new Promise(resolve => { timer = setTimeout(() => resolve(null), 5000) })
    ])
    if (!bytes || bytes.length > maximumBytes) return null
    const value = JSON.parse(bytes.toString('utf8'))
    return value && typeof value === 'object' && !Array.isArray(value) ? value : null
  } catch { return null }
  finally { clearTimeout(timer) }
}
const guidPattern = '[\\da-f]{8}-[\\da-f]{4}-[\\da-f]{4}-[\\da-f]{4}-[\\da-f]{12}'
const guidExpression = new RegExp(`^${guidPattern}$`, 'iu')
const hostReadPath = new RegExp(`^/api/local/profiles/${guidPattern}/(?:requirements|chat(?:/summary)?|shared-world(?:/(?:governance|handoff))?)$`, 'iu')
const friendReadPath = new RegExp(`^/api/local/friend/${guidPattern}/(?:compatibility|shared-world(?:/(?:recovery|handoff/restore))?)$`, 'iu')
const scopedFriendChatPath = new RegExp(`^/api/local/friend/connections/${guidPattern}/servers/${guidPattern}/chat(?:/summary)?$`, 'iu')
function roleBoundRead(request, url, origin) {
  if (url.origin !== origin || url.search || request.resourceType() !== 'fetch') return null
  if (request.method() === 'GET') {
    if (hostReadPath.test(url.pathname) || url.pathname === '/api/local/minecraft/discover') return 'Host'
    if (friendReadPath.test(url.pathname) || scopedFriendChatPath.test(url.pathname)) return 'Friend'
    return null
  }
  if (request.method() !== 'POST') return null
  if (url.pathname === '/api/local/network/detect-public-ip') {
    const bytes = request.postDataBuffer()
    return !bytes || bytes.length === 0 ? 'Host' : null
  }
  if (url.pathname !== '/api/local/ui-drafts/read') return null
  const bytes = request.postDataBuffer()
  if (!bytes || bytes.length > 1024) return null
  try {
    const body = JSON.parse(bytes.toString('utf8'))
    if (!body || typeof body !== 'object' || Array.isArray(body) || Object.keys(body).length !== 4 ||
        !Object.keys(body).every(key => ['purpose', 'profileId', 'connectionId', 'key'].includes(key)) ||
        !['file', 'settings', 'list', 'chat'].includes(body.purpose) || typeof body.profileId !== 'string' ||
        !guidExpression.test(body.profileId) || typeof body.key !== 'string' || !/^[a-z\d_:-]{1,96}$/iu.test(body.key)) return null
    const empty = '00000000-0000-0000-0000-000000000000'
    if (body.profileId === empty && (body.purpose !== 'settings' || body.key !== 'host-setup' || body.connectionId !== null)) return null
    if (body.connectionId === null) return 'Host'
    return body.purpose === 'chat' && body.key === 'compose' && typeof body.connectionId === 'string' &&
      guidExpression.test(body.connectionId) ? 'Friend' : null
  } catch { return null }
}
function observeBrowserRequest(collector, request) {
  const order = ++collector.order
  collector.requestOrders.set(request, order)
  const url = new URL(request.url())
  const match = /^\/api\/local\/mode\/(host|friend)$/u.exec(url.pathname)
  if (url.origin !== collector.origin || url.search || request.method() !== 'POST' || request.resourceType() !== 'fetch' ||
      !match) return
  const bytes = request.postDataBuffer()
  if (bytes && bytes.length !== 0) return
  const transition = { id: collector.transitions.length + 1, from: collector.mode, to: match[1] === 'host' ? 'Host' : 'Friend',
    started: order, responded: null, confirmed: false }
  collector.transitions.push(transition)
  collector.modeRequests.set(request, transition)
}
async function observeBrowserResponse(collector, response, order) {
  const request = response.request()
  const url = new URL(response.url())
  const started = collector.requestOrders.get(request)
  const transition = collector.modeRequests.get(request)
  const status = response.status()
  if (transition) {
    transition.responded = order
    const body = status === 200 ? await boundedResponseJson(response) : null
    transition.confirmed = body?.ok === true && body.code === 'ModeChanged'
    if (transition.confirmed && order > collector.modeOrder) {
      collector.mode = transition.to; collector.modeOrder = order
    }
  } else if (url.origin === collector.origin && url.pathname === '/api/local/snapshot' && !url.search &&
      request.method() === 'GET' && status === 200) {
    const body = await boundedResponseJson(response, 512 * 1024)
    if (body?.mode === 'Host' || body?.mode === 'Friend') {
      collector.snapshots.push({ mode: body.mode, started, responded: order })
      if (order > collector.modeOrder) { collector.mode = body.mode; collector.modeOrder = order }
    }
  }
  if (status < 400) return
  const body = url.origin === collector.origin ? await boundedResponseJson(response) : null
  const code = typeof body?.code === 'string' && /^[a-z][a-z\d]{0,63}$/iu.test(body.code) ? body.code : null
  collector.responses.push({ url: response.url(), status, code, role: roleBoundRead(request, url, collector.origin), started,
    responded: order, evidence: { method: request.method(), status, route: redact(url.pathname), code }, classified: false, consoleConsumed: false })
}
function expectedModeTransition(collector, response) {
  if (response.status !== 409 || !response.role || response.started === undefined ||
      response.code !== (response.role === 'Host' ? 'FriendMode' : 'HostMode')) return null
  return collector.transitions.find((transition, index) => {
    if (!transition.confirmed || transition.from !== response.role || transition.to === response.role) return false
    // The first matching snapshot acknowledges the new scope. Later wrong-role requests remain failures.
    const acknowledgement = collector.snapshots.filter(snapshot => snapshot.mode === transition.to &&
      snapshot.started >= transition.started && snapshot.responded >= transition.responded)
      .sort((left, right) => left.responded - right.responded)[0]
    const ended = Math.min(acknowledgement?.responded ?? transition.responded,
      collector.transitions[index + 1]?.started ?? Infinity)
    return response.started < ended && response.responded > transition.started
  }) ?? null
}
async function flushBrowserEvidence() {
  while (pendingResponseClassifications.size > 0) await Promise.allSettled([...pendingResponseClassifications])
  for (const collector of browserCollectors) {
    const modes = [...collector.snapshots,
      ...collector.transitions.filter(transition => transition.confirmed)
        .map(transition => ({ mode: transition.to, responded: transition.responded }))]
      .sort((left, right) => right.responded - left.responded)
    for (const transition of collector.transitions) {
      // Body reads can finish out of order; use the last mode receipt observed before this request.
      transition.from = modes.find(mode => mode.responded < transition.started)?.mode ?? transition.from
    }
    for (const response of collector.responses) {
      if (response.classified) continue
      const transition = expectedModeTransition(collector, response)
      response.expected = !!transition
      if (transition) report.expectedModeDenials.push({ ...response.evidence, page: collector.id, transition: transition.id,
        fromMode: transition.from, toMode: transition.to, transitionCode: 'ModeChanged' })
      else report.failedRequests.push(response.evidence)
      response.classified = true
    }
    for (const message of collector.consoleErrors.splice(0)) {
      const match = /^Failed to load resource: the server responded with a status of (\d{3})(?: \([^\r\n]*\))?$/u.exec(message.text)
      const corresponding = match && collector.responses.find(response => !response.consoleConsumed &&
        response.url === message.url && response.status === Number(match[1]))
      if (corresponding) {
        corresponding.consoleConsumed = true
        report.deduplicatedConsoleHttpErrors.push({ ...corresponding.evidence, page: collector.id,
          observedAs: corresponding.expected ? 'expected-mode-denial' : 'failed-request' })
      } else report.browserErrors.push({ kind: 'console', message: redact(message.text) })
    }
  }
}
async function eventually(read, predicate, label, timeout = 30_000) {
  const end = Date.now() + timeout
  while (Date.now() < end) {
    let value
    try { value = await read() } catch { /* Startup and canonical background refresh are bounded. */ }
    if (value !== undefined && predicate(value)) return value
    await delay(150)
  }
  throw new Error(`Timed out: ${label}.`)
}
async function step(name, action) {
  assert(!stopping, 'The CI smoke was cancelled.')
  const started = Date.now()
  try {
    await action()
    report.journeys.push({ name, outcome: 'passed', elapsedMs: Date.now() - started })
    console.log(`PASS ${name}`)
  } catch (error) {
    failed = true
    report.journeys.push({ name, outcome: 'failed', elapsedMs: Date.now() - started, message: redact(error.message) })
    throw error
  }
}
function skip(name, reason) {
  report.journeys.push({ name, outcome: 'unexercised', reason })
}
async function unusedPorts(count = 1) {
  // Read-only Windows metadata. Check runners never own a probe/listener socket.
  const script = `$taskNetwork = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
$taskPorts = @($taskNetwork.GetActiveTcpListeners().Port) + @($taskNetwork.GetActiveUdpListeners().Port)
ConvertTo-Json -InputObject @($taskPorts) -Compress`
  const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] })
  let stdout = ''
  child.stdout.on('data', data => { stdout += data.toString() })
  await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', code => code === 0 ? resolve() : reject(new Error('Could not read Windows listener metadata.'))) })
  const active = new Set([JSON.parse(stdout)].flat())
  for (let attempt = 0; attempt < 100; attempt++) {
    const port = randomInt(51000, 62000 - count)
    const candidates = Array.from({ length: count }, (_, offset) => port + offset)
    if (candidates.every(value => !active.has(value) && !claimedPorts.has(value))) {
      candidates.forEach(value => claimedPorts.add(value))
      return port
    }
  }
  throw new Error('No unused disposable CI port candidate is available.')
}

// JSON goes over stdin to a fixed script: no caller text is interpolated into a shell command.
async function processIdentity(request, terminate = false) {
  const script = `$ErrorActionPreference='Stop'
$taskInput = [Console]::In.ReadToEnd() | ConvertFrom-Json
$taskProcess = Get-Process -Id $taskInput.pid -ErrorAction SilentlyContinue
if ($null -eq $taskProcess) { [Console]::Out.Write('null'); exit 0 }
$taskPath = $taskProcess.Path
$taskTicks = $taskProcess.StartTime.ToUniversalTime().Ticks.ToString()
if ($taskInput.path -and -not [String]::Equals($taskPath,$taskInput.path,[StringComparison]::OrdinalIgnoreCase)) { throw 'Owned process path changed.' }
if ($taskInput.ticks -and $taskTicks -ne $taskInput.ticks) { throw 'Owned process start identity changed.' }
${terminate ? "Stop-Process -Id $taskProcess.Id -ErrorAction Stop" : ''}
@{pid=$taskProcess.Id;path=$taskPath;ticks=$taskTicks} | ConvertTo-Json -Compress`
  const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] })
  let stdout = ''
  child.stdout.on('data', data => { stdout += data.toString() })
  child.stderr.on('data', () => {})
  child.stdin.end(JSON.stringify(request))
  await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', code => code === 0 ? resolve() : reject(new Error('Exact owned process identity could not be confirmed.'))) })
  return JSON.parse(stdout)
}
async function api(instance, route, method = 'GET', body, accepted = [200]) {
  assert(route.startsWith('/api/local/') && !route.includes('://'), 'Only the disposable local API may be used.')
  assert(!stopping || /\/(?:instance|mode\/host|stop|quit)$/u.test(route), 'The CI smoke was cancelled.')
  const response = await fetch(instance.origin + route, { method,
    headers: { Origin: instance.origin, 'X-TogetherServer-Local': '1', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(25_000) })
  assert(accepted.includes(response.status), `Local ${method} ${route.replace(/[\da-f-]{36}/giu, '[id]')} returned HTTP ${response.status}.`)
  return response.json()
}
async function startInstance(name, mode = 'host') {
  const dataRoot = path.join(caseRoot, name)
  await mkdir(dataRoot)
  const port = await unusedPorts()
  const origin = `http://127.0.0.1:${port}`
  const child = spawn(appPath, [`--${mode}`, '--staging', '--port', String(port)], { cwd: path.dirname(appPath), windowsHide: true,
    env: { ...process.env, TOGETHERSERVER_DATA_DIR: path.join(caseRoot, `${name}-unused-production`), TOGETHERSERVER_STAGING_DATA_DIR: dataRoot,
      TOGETHERSERVER_FIXTURE_ROOT: caseRoot, TOGETHERSERVER_ENABLE_FIXTURE_DRIVER: '1' }, stdio: ['ignore', 'ignore', 'ignore'] })
  const instance = { name, dataRoot, origin, child, profileIds: new Set(), verified: false, identity: null }
  instances.push(instance)
  child.on('error', () => {})
  assert(child.pid, 'The disposable app could not start.')
  const identity = await processIdentity({ pid: child.pid, path: appPath })
  assert(identity, 'The disposable app exited during startup.')
  instance.identity = identity
  ownedProcesses.set(identity.pid, identity)
  const view = await eventually(() => api(instance, '/api/local/instance'), value => path.resolve(value.dataRoot) === dataRoot,
    'candidate instance isolation', 45_000)
  assert.equal(view.localPort, port)
  assert.equal(view.isStaging, true)
  assert.equal(view.updatesAvailable, false)
  assert.equal(child.exitCode, null, 'The exact disposable app must still be running.')
  instance.verified = true
  return instance
}

async function copyFixture(project, executableName, destination) {
  const source = path.join(repository, 'src', project, 'bin/Release/net10.0')
  assert(inside(caseRoot, destination), 'Fixture copies must stay in this disposable case.')
  await mkdir(destination, { recursive: true })
  const files = (await readdir(source)).filter(name => /\.(?:exe|dll|json)$/iu.test(name))
  assert(files.length > 0 && files.length <= 40, 'Build the bounded synthetic fixture outputs before this smoke.')
  for (const file of files) {
    const from = path.join(source, file)
    assert((await lstat(from)).isFile(), 'Fixture binaries must be plain files.')
    await copyFile(from, path.join(destination, file))
  }
  const originalName = project === 'TogetherServer.ValheimFixture' ? 'valheim_server.exe' : `${project}.exe`
  if (originalName !== executableName) await copyFile(path.join(destination, originalName), path.join(destination, executableName))
  const executable = path.join(destination, executableName)
  ownedFixtureExecutables.add(executable.toLowerCase())
  return executable
}
async function seedProfiles(host) {
  const valheimId = randomUUID()
  const bedrockId = randomUUID()
  const extraId = randomUUID()
  const companionPort = await unusedPorts()
  const valheimExecutable = await copyFixture('TogetherServer.ValheimFixture', 'valheim_server.exe', path.join(caseRoot, 'valheim-binary'))
  const bedrockRoot = path.join(host.dataRoot, 'minecraft-servers', 'bedrock-synthetic')
  const bedrockExecutable = await copyFixture('TogetherServer.MinecraftFixture', 'bedrock_server.exe', bedrockRoot)
  const bedrockPort = await unusedPorts()
  const bedrockV6Port = await unusedPorts()
  const valheim = { id: valheimId, kind: 'Valheim', name: 'Synthetic Valheim A', serverName: 'Fixture "Valheim"',
    worldId: 'fixture-world', worldSource: 'New', worldDirectory: path.join(host.dataRoot, 'worlds', valheimId.replaceAll('-', '')),
    gamePort: await unusedPorts(2), executablePath: valheimExecutable, publicListing: false, crossplay: false,
    backups: { enabled: true, retentionCount: 5, minimumFreeSpaceMb: 0 } }
  const bedrock = { id: bedrockId, kind: 'MinecraftBedrock', name: 'Synthetic Bedrock B', serverName: 'Synthetic Bedrock B',
    worldId: 'synthetic-bedrock', worldSource: 'Existing', worldDirectory: bedrockRoot, gamePort: bedrockPort,
    executablePath: bedrockExecutable, maintenance: { enabled: true, message: 'Disposable editor smoke.' },
    backups: { enabled: false, retentionCount: 5, minimumFreeSpaceMb: 0 } }
  const extra = { ...valheim, id: extraId, name: 'Synthetic Valheim C', gamePort: await unusedPorts(2),
    worldDirectory: path.join(host.dataRoot, 'worlds', extraId.replaceAll('-', '')) }
  for (const profile of [valheim, extra]) await mkdir(profile.worldDirectory, { recursive: true })
  await mkdir(path.join(bedrockRoot, 'worlds', bedrock.worldId, 'db'), { recursive: true })
  const properties = `# Synthetic configuration only\nlevel-name=${bedrock.worldId}\nserver-port=${bedrockPort}\nserver-portv6=${bedrockV6Port}\nenable-lan-visibility=false\ndifficulty=normal\nmax-players=10\ngamemode=survival\nforce-gamemode=false\nallow-list=false\n`
  await writeFile(path.join(bedrockRoot, 'server.properties'), properties)
  await writeFile(path.join(bedrockRoot, 'allowlist.json'), '[]\n')
  await writeFile(path.join(bedrockRoot, 'permissions.json'), '[]\n')
  await writeFile(path.join(bedrockRoot, 'worlds', bedrock.worldId, 'db/synthetic.dat'), 'Disposable fixture bytes. No real game data.')
  const settings = { ...(await api(host, '/api/local/snapshot')).settings, profiles: [valheim, bedrock, extra],
    maxConcurrentServers: 2, autoShutdownEnabled: false, remoteControlsEnabled: false, companionListeningEnabled: false,
    companionEndpoint: `https://127.0.0.1:${companionPort}`, companionPort, companionBindAddress: '127.0.0.1' }
  assert.equal((await api(host, '/api/local/settings', 'PUT', settings)).ok, true, 'Synthetic profiles must save through the real backend.')
  for (const profile of [valheim, bedrock, extra]) host.profileIds.add(profile.id)
  for (const profile of [valheim, extra]) {
    assert.equal((await api(host, `/api/local/profiles/${profile.id}/password`, 'POST', { password: 'fixture-pass-123' })).ok, true)
    await writeFile(path.join(profile.worldDirectory, 'adminlist.txt'), '# Synthetic owner list\n')
  }
  return { valheim, bedrock, extra, properties }
}
async function rememberManagedProcesses(instance) {
  let runs
  try {
    const text = await readFile(path.join(instance.dataRoot, 'runs.json'), 'utf8')
    // Preserve .NET 100ns identity ticks without IEEE-754 rounding.
    runs = JSON.parse(text.replace(/("(?:startTimeUtcTicks|consoleCaptureStartTimeUtcTicks)"\s*:\s*)(\d+)/gu, '$1"$2"'))
  } catch { return }
  for (const run of runs) {
    if (!instance.profileIds.has(run.profileId) || !inside(caseRoot, path.resolve(run.worldDirectory))) continue
    for (const identity of [
      { pid: run.processId, path: run.executablePath, ticks: run.startTimeUtcTicks },
      { pid: run.consoleCaptureProcessId, path: run.consoleCaptureExecutablePath, ticks: run.consoleCaptureStartTimeUtcTicks }
    ]) {
      if (!Number.isSafeInteger(identity.pid) || identity.pid <= 0 || typeof identity.ticks !== 'string' ||
          !(ownedFixtureExecutables.has(path.resolve(identity.path).toLowerCase()) || path.resolve(identity.path).toLowerCase() === appPath.toLowerCase())) continue
      const confirmed = await processIdentity(identity)
      if (confirmed) ownedProcesses.set(confirmed.pid, confirmed)
    }
  }
}
async function selectServer(page, name, tab = 'Overview') {
  await page.getByRole('navigation', { name: 'TogetherServer workspaces' }).getByRole('button', { name: 'Host', exact: true }).click()
  await page.getByLabel('Find server', { exact: true }).fill('')
  const list = page.getByRole('complementary', { name: 'Saved servers' })
  await list.getByRole('button', { name: new RegExp(`^${name}`) }).click()
  await page.getByRole('region', { name: `${name} workspace`, exact: true }).waitFor()
  await page.getByRole('navigation', { name: 'Selected server sections' }).getByRole('button', { name: tab, exact: true }).click()
  return page.getByRole('region', { name: `${name} workspace`, exact: true })
}
async function openPage(instance, context) {
  const page = await context.newPage()
  page.setDefaultTimeout(15_000)
  const collector = { id: browserCollectors.length + 1, origin: instance.origin, order: 0, mode: null, modeOrder: 0,
    requestOrders: new WeakMap(), modeRequests: new WeakMap(), transitions: [], snapshots: [], responses: [], consoleErrors: [] }
  browserCollectors.push(collector)
  page.on('request', request => observeBrowserRequest(collector, request))
  page.on('pageerror', error => report.browserErrors.push({ kind: 'pageerror', message: redact(error.message) }))
  page.on('console', message => {
    if (message.type() === 'error') collector.consoleErrors.push({ text: message.text(), url: message.location().url })
  })
  page.on('requestfailed', request => {
    const failure = request.failure()?.errorText ?? 'Request failed'
    if (failure.includes('ERR_ABORTED')) return // Normal scoped polling/navigation cancellation.
    const url = new URL(request.url())
    report.failedRequests.push({ method: request.method(), route: redact(url.pathname), failure: redact(failure) })
  })
  page.on('response', response => {
    const order = ++collector.order
    const pending = observeBrowserResponse(collector, response, order).catch(error => {
      if (response.status() >= 400) report.failedRequests.push({ method: response.request().method(),
        status: response.status(), route: redact(new URL(response.url()).pathname), code: null })
      report.browserErrors.push({ kind: 'response-collector', message: redact(error.message) })
    })
    pendingResponseClassifications.add(pending)
    void pending.finally(() => pendingResponseClassifications.delete(pending))
  })
  page.on('dialog', async dialog => {
    if (dialog.type() === 'confirm' && /discard|reload|prepare to change|stop|restart|finish later clears/iu.test(dialog.message())) await dialog.accept()
    else { await dialog.dismiss(); report.browserErrors.push({ kind: 'unexpected-dialog', message: 'An unexpected dialog was dismissed.' }) }
  })
  await page.goto(instance.origin, { waitUntil: 'domcontentloaded' })
  await page.getByRole('navigation', { name: 'TogetherServer workspaces' }).waitFor()
  // These nodes come from the candidate's embedded bundle, never a Vite server or page.setContent.
  assert(await page.locator('script[src^="/assets/"]').count() > 0, 'The candidate must serve its bundled React assets.')
  return page
}
async function screenshot(page, name, viewport) {
  await page.setViewportSize(viewport)
  const metrics = await page.evaluate(() => ({ width: window.innerWidth,
    documentWidth: document.documentElement.scrollWidth, theme: document.documentElement.dataset.theme,
    density: document.documentElement.dataset.density, textScale: getComputedStyle(document.documentElement).getPropertyValue('--qol-text-scale').trim(),
    highContrast: document.documentElement.dataset.highContrast,
    liveStatusCount: document.querySelectorAll('[role="status"],[aria-live="polite"]').length }))
  report.layout.push({ name, ...metrics })
  await page.screenshot({ path: path.join(evidenceRoot, `${name}.png`), fullPage: true,
    mask: [page.locator('code'), page.locator('input[type="password"]'), page.locator('.invite-input')] })
  report.screenshots.push(`${name}.png`)
  assert(metrics.documentWidth <= metrics.width + 1, `${name}: the rendered page must not overflow horizontally.`)
}

async function routingAndAppearance(page) {
  const navigation = page.getByRole('navigation', { name: 'TogetherServer workspaces' })
  for (const [shortcut, destination] of [['Alt+2', 'Join'], ['Alt+3', 'Attention'], ['Alt+4', 'Settings'], ['Alt+1', 'Host']]) {
    // Global navigation deliberately leaves a focused editor's typing alone.
    await page.getByRole('heading', { level: 1 }).click()
    await page.keyboard.press(shortcut)
    await eventually(() => navigation.getByRole('button', { name: destination, exact: true }).getAttribute('aria-current'),
      value => value === 'page', `keyboard opens ${destination}`)
  }
  await navigation.getByRole('button', { name: 'Settings', exact: true }).click()
  await page.keyboard.press('Control+k')
  const palette = page.getByRole('dialog', { name: 'Command palette' })
  await palette.getByRole('combobox', { name: 'Search commands' }).fill('Open Attention Center')
  await page.keyboard.press('Enter')
  await palette.waitFor({ state: 'hidden' })
  assert.equal(await navigation.getByRole('button', { name: 'Attention', exact: true }).getAttribute('aria-current'), 'page')
  await navigation.getByRole('button', { name: 'Join', exact: true }).click()
  await page.keyboard.press('Control+k')
  await palette.getByRole('combobox', { name: 'Search commands' }).fill('Open Host')
  await page.keyboard.press('Enter')
  await palette.waitFor({ state: 'hidden' })
  await eventually(() => navigation.getByRole('button', { name: 'Host', exact: true }).getAttribute('aria-current'), value => value === 'page', 'Host palette navigation from Join')
  await navigation.getByRole('button', { name: 'Settings', exact: true }).click()
  await page.keyboard.press('Control+k')
  await palette.getByRole('combobox', { name: 'Search commands' }).fill('Synthetic Bedrock B')
  await palette.getByRole('option').filter({ hasText: /files/iu }).click()
  await palette.waitFor({ state: 'hidden' })
  await page.getByRole('region', { name: 'Synthetic Bedrock B workspace', exact: true }).waitFor()
  assert.equal(await page.locator('.server-detail').getAttribute('data-server-tab'), 'files')
  await navigation.getByRole('button', { name: 'Settings', exact: true }).click()
  const appearance = page.getByRole('group', { name: 'Appearance' })
  for (const theme of ['system', 'light', 'dark']) {
    await appearance.getByRole('combobox', { name: /^Theme$/u }).selectOption(theme)
    await eventually(() => page.locator('html').getAttribute('data-theme'), value => value === theme, `apply ${theme} theme`)
  }
  await appearance.getByRole('combobox', { name: /^Theme$/u }).selectOption('system')
  await page.emulateMedia({ colorScheme: 'light' })
  await eventually(() => page.evaluate(() => getComputedStyle(document.documentElement).colorScheme), value => value.includes('light'), 'system light preference')
  await page.emulateMedia({ colorScheme: 'dark' })
  await eventually(() => page.evaluate(() => getComputedStyle(document.documentElement).colorScheme), value => value.includes('dark'), 'system dark preference')
  await appearance.getByRole('combobox', { name: /^Layout$/u }).selectOption('compact')
  await appearance.getByRole('combobox', { name: /^Text size$/u }).selectOption('150')
  await appearance.getByRole('checkbox', { name: 'Stronger contrast and status outlines' }).check()
  await eventually(() => page.locator('html').getAttribute('data-density'), value => value === 'compact', 'compact layout')
  await eventually(() => page.evaluate(() => getComputedStyle(document.documentElement).fontSize), value => value === '24px', '150 percent root text size')
  await screenshot(page, 'settings-wide-150', { width: 1440, height: 900 })
  await screenshot(page, 'settings-narrow-150', { width: 390, height: 844 })
  await appearance.getByRole('combobox', { name: /^Text size$/u }).selectOption('100')
  await appearance.getByRole('combobox', { name: /^Layout$/u }).selectOption('comfortable')
  await appearance.getByRole('combobox', { name: /^Theme$/u }).selectOption('dark')
  await appearance.getByRole('checkbox', { name: 'Stronger contrast and status outlines' }).uncheck()
  await page.setViewportSize({ width: 1440, height: 900 })
}
async function serverNavigation(page, profiles) {
  const list = page.getByRole('complementary', { name: 'Saved servers' })
  await selectServer(page, profiles.valheim.name, 'Logs')
  await selectServer(page, profiles.bedrock.name, 'Files')
  await list.getByRole('button', { name: new RegExp(`^${profiles.valheim.name}`) }).click()
  await eventually(() => page.locator('.server-detail').getAttribute('data-server-tab'), value => value === 'logs', 'per-server last tab')
  await page.getByLabel('Find server', { exact: true }).fill('Bedrock')
  assert.equal(await list.locator('.server-master-item').count(), 1)
  assert((await list.locator('.server-master-item').innerText()).includes(profiles.bedrock.name))
  await page.getByLabel('Find server', { exact: true }).fill('')
  await list.getByRole('button', { name: `Pin ${profiles.extra.name}`, exact: true }).click()
  await eventually(() => list.locator('.server-master-item').first().innerText(), value => value.includes(profiles.extra.name), 'favorite server ordering')
  await list.getByRole('button', { name: `Unpin ${profiles.extra.name}`, exact: true }).click()
  await list.getByRole('button', { name: 'Reorder servers', exact: true }).click()
  await list.getByRole('button', { name: `Move ${profiles.extra.name} up`, exact: true }).click()
  await list.getByRole('button', { name: `Move ${profiles.extra.name} up`, exact: true }).click()
  await list.getByRole('button', { name: 'Finish ordering', exact: true }).click()
  await page.reload({ waitUntil: 'domcontentloaded' })
  await eventually(() => list.locator('.server-master-item').first().innerText(), value => value.includes(profiles.extra.name), 'saved manual ordering after reload')
  await screenshot(page, 'host-wide-100', { width: 1440, height: 900 })
}
async function setupReview(page, profiles) {
  await selectServer(page, profiles.valheim.name)
  await page.getByRole('complementary', { name: 'Saved servers' }).getByRole('button', { name: 'Add server', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Add new server' })
  await dialog.getByRole('region', { name: 'Setup blockers' }).waitFor()
  assert(await dialog.getByRole('region', { name: 'Setup blockers' }).getByRole('button').count() >= 2, 'Setup must show its blockers together.')
  await dialog.getByText("Reuse a saved server's nonsecret setup", { exact: true }).click()
  await dialog.getByRole('button', { name: `Use setup from ${profiles.valheim.name}`, exact: true }).click()
  assert.equal(await dialog.getByLabel('World name', { exact: true }).inputValue(), '')
  assert.equal(await dialog.getByLabel('Game password', { exact: true }).inputValue(), '')
  await dialog.getByLabel('World name', { exact: true }).fill('browser-new-world')
  await dialog.getByLabel('Game password', { exact: true }).fill('fixture-pass-123')
  await dialog.getByRole('button', { name: 'Continue', exact: true }).click()
  await dialog.getByText('Selected Valheim Dedicated Server', { exact: true }).waitFor()
  await dialog.getByRole('button', { name: 'Continue', exact: true }).click()
  await dialog.getByText('Advanced server settings', { exact: true }).click()
  const port = dialog.getByLabel('Game UDP start port', { exact: true })
  await port.fill(String(profiles.valheim.gamePort))
  await dialog.getByRole('button', { name: 'Apply suggested ports', exact: true }).click()
  assert.notEqual(await port.inputValue(), String(profiles.valheim.gamePort), 'Applying the suggestion changes the actual draft.')
  const savedBefore = profiles.valheim.worldDirectory
  assert.notEqual(await dialog.getByLabel('Save directory', { exact: true }).inputValue(), savedBefore)
  await screenshot(page, 'setup-review-wide', { width: 1440, height: 900 })
  await dialog.getByRole('button', { name: 'Finish later', exact: true }).click()
  await dialog.waitFor({ state: 'hidden' })
  await page.getByRole('button', { name: 'Review recovered draft', exact: true }).click()
  await page.getByRole('dialog', { name: 'Add new server' }).getByText('Review and start', { exact: true }).waitFor()
  await page.getByRole('dialog', { name: 'Add new server' }).getByRole('button', { name: 'Cancel', exact: true }).click()
  await page.getByRole('dialog', { name: 'Add new server' }).waitFor({ state: 'hidden' })
}
async function editorRecovery(page, host, profiles) {
  await selectServer(page, profiles.bedrock.name, 'Files')
  const files = page.getByRole('region', { name: 'Server files and settings' })
  const row = files.locator('.server-file-row').filter({ hasText: 'server.properties' })
  await row.getByRole('button', { name: 'Edit file', exact: true }).click()
  const contents = files.getByLabel('File contents', { exact: true })
  const raw = profiles.properties + '# QoL browser raw draft\n'
  await contents.fill(raw)
  await page.getByRole('navigation', { name: 'Selected server sections' }).getByRole('button', { name: 'Logs', exact: true }).click()
  const stored = await api(host, '/api/local/ui-drafts/read', 'POST', { purpose: 'file', profileId: profiles.bedrock.id,
    connectionId: null, key: 'file:server-properties' })
  assert.equal(stored.ok, true)
  assert(stored.text?.includes('QoL browser raw draft'), 'Immediate navigation must flush the last raw keystroke through the real protected store.')
  assert.equal(await readFile(path.join(profiles.bedrock.worldDirectory, 'server.properties'), 'utf8'), profiles.properties)
  await selectServer(page, profiles.bedrock.name, 'Files')
  await row.getByRole('button', { name: 'Edit file', exact: true }).click()
  await files.getByRole('button', { name: 'Review recovered draft', exact: true }).click()
  assert.equal(await contents.inputValue(), raw)
  await files.getByRole('button', { name: 'Review file changes', exact: true }).click()
  await files.getByRole('region', { name: 'Review raw file changes' }).getByText(/QoL browser raw draft/u).waitFor()
  await files.getByRole('button', { name: 'Discard unsaved file edits', exact: true }).click()
  const settings = page.getByRole('region', { name: 'Simple game settings' })
  await settings.getByRole('combobox', { name: /^Difficulty/u }).selectOption('hard')
  await settings.getByLabel('Search game settings', { exact: true }).fill('players')
  assert.equal(await settings.getByRole('combobox', { name: /^Difficulty/u }).count(), 0)
  await settings.getByLabel('Search game settings', { exact: true }).fill('')
  assert.equal(await settings.getByLabel('Difficulty', { exact: true }).inputValue(), 'hard')
  await page.getByRole('navigation', { name: 'Selected server sections' }).getByRole('button', { name: 'Overview', exact: true }).click()
  await selectServer(page, profiles.bedrock.name, 'Files')
  await settings.getByRole('button', { name: 'Review recovered draft', exact: true }).click()
  assert.equal(await settings.getByLabel('Difficulty', { exact: true }).inputValue(), 'hard')
  await settings.getByRole('button', { name: 'Review settings changes', exact: true }).click()
  await settings.getByRole('region', { name: 'Review exact changes' }).waitFor()
  await screenshot(page, 'editor-recovery-wide', { width: 1440, height: 900 })
  await settings.getByRole('button', { name: 'Discard unsaved settings', exact: true }).click()
  assert.equal(await readFile(path.join(profiles.bedrock.worldDirectory, 'server.properties'), 'utf8'), profiles.properties)
}
async function lifecycleLogsAndSessions(page, host, profiles) {
  await selectServer(page, profiles.valheim.name)
  const workspace = page.getByRole('region', { name: `${profiles.valheim.name} workspace`, exact: true })
  await workspace.getByRole('button', { name: 'Start server', exact: true }).first().click()
  await eventually(() => api(host, '/api/local/snapshot'), value => value.runs.some(run => run.profileId === profiles.valheim.id && run.state === 'Ready'), 'synthetic Valheim readiness')
  await rememberManagedProcesses(host)
  // Normal Start has now recorded ownership of this fresh synthetic world.
  // The stand-in emits no game payload; create these fixture bytes only in that owned root.
  await writeFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.db'), 'Disposable synthetic DB bytes.')
  await writeFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.fwl'), 'Disposable synthetic FWL bytes.')
  const runs = JSON.parse(await readFile(path.join(host.dataRoot, 'runs.json'), 'utf8'))
  const run = runs.find(value => value.profileId === profiles.valheim.id)
  assert(inside(host.dataRoot, path.resolve(run.logPath)), 'Only this disposable run log may be extended.')
  await appendFile(run.logPath, Array.from({ length: 90 }, (_, index) => `Synthetic browser log line ${index}\n`).join(''))
  await selectServer(page, profiles.valheim.name, 'Logs')
  const logs = page.getByRole('region', { name: 'Server logs', exact: true })
  await logs.getByText('Synthetic browser log line 80', { exact: true }).waitFor()
  await logs.getByRole('button', { name: 'Follow latest', exact: true }).click()
  assert.equal(await logs.getByRole('button', { name: 'Follow latest', exact: true }).getAttribute('aria-pressed'), 'false')
  await appendFile(run.logPath, 'Synthetic browser log line after pause\n')
  await logs.getByText('Synthetic browser log line after pause', { exact: true }).waitFor()
  await logs.getByRole('button', { name: 'Jump to latest', exact: true }).click()
  assert.equal(await logs.getByRole('button', { name: 'Follow latest', exact: true }).getAttribute('aria-pressed'), 'true')
  await screenshot(page, 'logs-wide', { width: 1440, height: 900 })
  await selectServer(page, profiles.valheim.name)
  await workspace.getByRole('button', { name: 'Stop server', exact: true }).click()
  await eventually(() => api(host, '/api/local/snapshot'), value => value.runs.some(value => value.profileId === profiles.valheim.id && value.state === 'Offline'), 'exact synthetic Stop', 45_000)
  await selectServer(page, profiles.valheim.name, 'Sessions')
  const weekly = page.locator('.weekly-summary')
  const completed = weekly.getByRole('button', { name: /^Completed sessions:/u })
  await completed.click()
  assert.equal(await completed.getAttribute('aria-pressed'), 'true')
  assert.match(run.operationId, /^[\da-f-]{36}$/iu, 'The actual managed run must have an archive identity.')
  await weekly.locator('.recent-session-list .recent-session-card').filter({ hasText: `Run ${run.operationId.slice(0, 8)}` })
    .getByText('Stopped gracefully', { exact: true }).waitFor()
  assert(await weekly.locator('.weekly-runtime-chart svg').count() > 0, 'Recorded UTC-day activity must come from the actual archived session.')
  await screenshot(page, 'sessions-wide', { width: 1440, height: 900 })
}
async function backupCatalog(page, host, profiles) {
  assert.equal((await api(host, `/api/local/profiles/${profiles.valheim.id}/backups/manual`, 'POST')).ok, true)
  await selectServer(page, profiles.valheim.name, 'Backups')
  const catalog = page.locator('.backup-catalog')
  await catalog.getByLabel('Search backups', { exact: true }).waitFor()
  await eventually(() => catalog.locator('.backup-bookmark').count(), value => value >= 2, 'completed unified backup catalog')
  let first = catalog.locator('.backup-bookmark').first()
  await first.getByText('Name and retention', { exact: true }).click()
  await first.getByLabel('Backup name', { exact: true }).fill('Browser checkpoint')
  await first.getByRole('checkbox', { name: 'Pin this backup', exact: true }).check()
  await first.getByRole('button', { name: 'Save name and pin', exact: true }).click()
  first = catalog.locator('.backup-bookmark').filter({ hasText: 'Browser checkpoint' })
  await first.getByText('Pinned', { exact: true }).waitFor()
  await catalog.getByLabel('Search backups', { exact: true }).fill('Browser checkpoint')
  assert.equal(await catalog.locator('.backup-bookmark').count(), 1)
  await catalog.getByRole('combobox', { name: /^Pins$/u }).selectOption('pinned')
  assert.equal(await catalog.locator('.backup-bookmark').count(), 1)
  await catalog.getByRole('button', { name: 'Clear filters', exact: true }).click()
  await catalog.getByText('Retention preview', { exact: true }).click()
  await catalog.getByLabel('Preview unpinned retention count', { exact: true }).fill('1')
  await catalog.getByText(/Changing this preview removes nothing/u).waitFor()
  assert((await api(host, '/api/local/snapshot')).runs.some(run => run.profileId === profiles.valheim.id && run.state === 'Offline'),
    'Restore-cancellation sentinels are written only while the disposable fixture is Offline.')
  const currentDb = 'Current synthetic DB after backups; cancel must preserve this.'
  const currentFwl = 'Current synthetic FWL after backups; cancel must preserve this.'
  await writeFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.db'), currentDb)
  await writeFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.fwl'), currentFwl)
  await first.getByRole('button', { name: 'Review Restore', exact: true }).click()
  const review = catalog.getByRole('region', { name: 'Review Restore', exact: true })
  const restore = review.getByRole('button', { name: 'Restore reviewed backup', exact: true })
  assert.equal(await restore.isDisabled(), true)
  await review.getByRole('checkbox').check()
  assert.equal(await restore.isEnabled(), true)
  await screenshot(page, 'backup-restore-review', { width: 1440, height: 900 })
  await review.getByRole('button', { name: 'Cancel Restore', exact: true }).click()
  // Cancelling review cannot change either synthetic world file.
  assert.equal(await readFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.db'), 'utf8'), currentDb)
  assert.equal(await readFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.fwl'), 'utf8'), currentFwl)
  await catalog.getByRole('button', { name: 'Verify', exact: true }).first().click()
  await catalog.getByText(/Local integrity: Passed/u).first().waitFor()
}

async function friendPlayAndChat(host, friend, context, profiles) {
  const invitation = await api(host, `/api/local/servers/${profiles.valheim.id}/invite`, 'POST',
    { refresh: false, canStart: true, enableConnections: true })
  assert.equal(invitation.ok, true)
  assert.equal(invitation.listenerActive, true)
  const hostSettings = (await api(host, '/api/local/snapshot')).settings
  assert.equal(hostSettings.companionBindAddress, '127.0.0.1', 'The fixture listener must remain loopback only.')
  const page = await openPage(friend, context)
  await page.getByLabel('Server code', { exact: true }).fill(invitation.password)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await eventually(() => api(friend, '/api/local/snapshot'), value => value.state === 'Connected' && value.profiles.some(profile => profile.id === profiles.valheim.id), 'pinned loopback Friend pairing')
  const devices = (await api(host, '/api/local/companion')).devices
  assert.equal(devices.length, 1, 'Only the synthetic Friend PC should be paired in the disposable Host.')
  const access = await api(host, `/api/local/devices/${devices[0].id}/servers`, 'PUT',
    { profileIds: [profiles.valheim.id, profiles.bedrock.id], permissions: [
      { profileId: profiles.valheim.id, canStart: true, canStop: false, canExtendTimer: false, canViewLogs: false },
      { profileId: profiles.bedrock.id, canStart: false, canStop: false, canExtendTimer: false, canViewLogs: false }
    ] })
  assert.equal(access.ok, true)
  await api(friend, '/api/local/friend/poll', 'POST')
  const first = page.locator(`#friend-server-${profiles.valheim.id}`)
  const second = page.locator(`#friend-server-${profiles.bedrock.id}`)
  await second.waitFor()
  assert.equal(await second.getByRole('button', { name: 'Start server', exact: true }).isDisabled(), true)
  const play = first.getByRole('region', { name: 'Play', exact: true })
  await play.getByRole('button', { name: 'Start server', exact: true }).click()
  await eventually(() => api(host, '/api/local/snapshot'), value => value.runs.some(run => run.profileId === profiles.valheim.id && run.state === 'Ready'), 'Friend UI starts the actual synthetic driver')
  await rememberManagedProcesses(host)
  await api(friend, '/api/local/friend/poll', 'POST')
  await first.getByText(/The Host reports readiness/u).waitFor()
  assert.equal(await first.getByRole('button', { name: 'Stop server', exact: true }).count(), 0)
  assert.equal(await first.getByRole('button', { name: 'View logs', exact: true }).count(), 0)
  // The OS/Open game control is only observed; this harness never invokes a native client.
  await first.getByRole('button', { name: 'Open chat', exact: true }).first().click()
  const chat = first.locator('.server-chat')
  const message = 'Synthetic browser message kept during navigation'
  await chat.getByLabel('Message', { exact: true }).fill(message)
  await page.getByRole('navigation', { name: 'TogetherServer workspaces' }).getByRole('button', { name: 'Attention', exact: true }).click()
  await page.getByRole('navigation', { name: 'TogetherServer workspaces' }).getByRole('button', { name: 'Join', exact: true }).click()
  await chat.getByRole('button', { name: 'Use recovered message', exact: true }).click()
  assert.equal(await chat.getByLabel('Message', { exact: true }).inputValue(), message)
  await chat.getByRole('button', { name: 'Send', exact: true }).click()
  await chat.getByText(message, { exact: true }).waitFor()
  const room = await api(host, `/api/local/profiles/${profiles.valheim.id}/chat`)
  assert(room.entries.some(entry => entry.text === message), 'The browser message must reach the real signed Host room.')
  await page.getByRole('button', { name: 'Add another Host', exact: true }).click()
  await page.getByRole('button', { name: 'Cancel and return to saved Host', exact: true }).click()
  await first.getByRole('region', { name: 'Play', exact: true }).waitFor()
  await screenshot(page, 'friend-play-wide', { width: 1440, height: 900 })
  await screenshot(page, 'friend-play-narrow', { width: 390, height: 844 })
}
async function attentionAndLargeText(page) {
  const navigation = page.getByRole('navigation', { name: 'TogetherServer workspaces' })
  await navigation.getByRole('button', { name: 'Attention', exact: true }).click()
  const attention = page.locator('.attention-workspace')
  await attention.getByRole('combobox', { name: /^History time/u }).selectOption('day')
  await attention.getByLabel('Search attention', { exact: true }).fill('Synthetic')
  await attention.getByRole('button', { name: 'Mark read', exact: true }).first().click()
  await attention.getByText('History notice marked read.', { exact: true }).waitFor()
  await attention.getByRole('button', { name: 'Dismiss notice', exact: true }).first().click()
  await screenshot(page, 'attention-narrow', { width: 390, height: 844 })
  await navigation.getByRole('button', { name: 'Settings', exact: true }).click()
  const appearance = page.getByRole('group', { name: 'Appearance' })
  await appearance.getByRole('combobox', { name: /^Text size$/u }).selectOption('150')
  await navigation.getByRole('button', { name: 'Host', exact: true }).click()
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.getByRole('complementary', { name: 'Saved servers' }).locator('.server-master-item').first().click()
  await screenshot(page, 'host-narrow-150', { width: 390, height: 844 })
  await screenshot(page, 'host-tablet-150', { width: 768, height: 1024 })
  assert(report.layout.every(layout => layout.liveStatusCount > 0), 'Each rendered surface should retain accessible status announcements.')
}
async function cleanupOwnedResources() {
  stopping = true
  let safeToRemove = true
  if (browser) {
    try { await browser.close() }
    catch { safeToRemove = false; report.cleanup.push({ resource: 'owned browser', outcome: 'close failed' }) }
  }
  await flushBrowserEvidence()
  for (const instance of [...instances].reverse()) {
    try { await rememberManagedProcesses(instance) }
    catch { safeToRemove = false; report.cleanup.push({ resource: instance.name, outcome: 'managed identity needs review' }) }
    if (!instance.verified || !instance.identity) {
      if (instance.child.exitCode === null && !instance.identity) safeToRemove = false
      report.cleanup.push({ resource: instance.name, outcome: 'unverified endpoint received no cleanup mutations' })
      continue
    }
    try {
      if (!await processIdentity(instance.identity)) continue
      const currentInstance = await api(instance, '/api/local/instance')
      assert.equal(path.resolve(currentInstance.dataRoot).toLowerCase(), instance.dataRoot.toLowerCase(), 'Cleanup endpoint no longer belongs to this disposable app.')
      await api(instance, '/api/local/mode/host', 'POST')
      for (const profileId of instance.profileIds) {
        try { await api(instance, `/api/local/profiles/${profileId}/stop`, 'POST') }
        catch { /* Only confirmed exact owned fixture identities may be terminated below. */ }
      }
      const quit = await api(instance, '/api/local/quit', 'POST')
      if (quit.ok !== true) { safeToRemove = false; report.cleanup.push({ resource: instance.name, outcome: 'guarded quit still reports a managed run' }) }
      await eventually(() => instance.child.exitCode, value => value !== null, 'owned app exit', 7000)
    } catch { /* Exact identity is rechecked for each fallback; process names are never used. */ }
  }
  for (const identity of ownedProcesses.values()) {
    try {
      const existing = await processIdentity(identity)
      if (existing) {
        failed = true
        await processIdentity(identity, true)
        await eventually(() => processIdentity(identity), value => value === null, 'exact disposable process exit', 5000)
        report.cleanup.push({ resource: 'exact owned process', outcome: 'disposable fallback termination; graceful cleanup incomplete' })
      }
    } catch {
      safeToRemove = false
      report.cleanup.push({ resource: 'exact owned process', outcome: 'identity mismatch or termination failed; unrelated processes untouched' })
    }
  }
  if (safeToRemove) {
    const resolved = await realpath(caseRoot)
    assert.equal(resolved.toLowerCase(), caseRoot.toLowerCase(), 'The disposable root changed identity before cleanup.')
    assert.equal((await lstat(caseRoot)).isSymbolicLink(), false)
    assert.equal(await readFile(path.join(caseRoot, '.qol-smoke-owner'), 'utf8'), ownerId)
    assert(inside(await realpath(tmpdir()), resolved), 'Refuse cleanup outside the original temporary parent.')
    await rm(resolved, { recursive: true, force: false })
    report.cleanup.push({ resource: 'marked disposable root', outcome: 'removed' })
  } else {
    failed = true
    report.cleanup.push({ resource: 'marked disposable root', outcome: 'retained because cleanup could not be confirmed' })
  }
  report.finishedUtc = new Date().toISOString()
  report.outcome = failed || report.browserErrors.length > 0 || report.failedRequests.length > 0 ? 'failed' : 'passed'
  await writeFile(path.join(evidenceRoot, 'qol-browser-report.json'), JSON.stringify(report, null, 2))
}
function cleanup() {
  cleanupPromise ??= cleanupOwnedResources()
  return cleanupPromise
}
for (const [signal, code] of [['SIGINT', 130], ['SIGTERM', 143]]) {
  process.once(signal, () => {
    failed = true
    void cleanup().finally(() => process.exit(code))
  })
}

try {
  const { chromium } = await import('playwright')
  const host = await startInstance('host')
  const profiles = await seedProfiles(host)
  // No Playwright browser download. The separate Windows runner supplies Microsoft Edge.
  browser = await chromium.launch({ channel: 'msedge', headless: true })
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: 'dark' })
  const hostPage = await openPage(host, context)
  await step('workspace keyboard, command palette and appearance preferences', () => routingAndAppearance(hostPage))
  await step('saved-server search, per-server tabs, favorites and durable manual ordering', () => serverNavigation(hostPage, profiles))
  await step('setup blockers, nonsecret reuse, applied port suggestion and paused-step recovery', () => setupReview(hostPage, profiles))
  await step('raw and guided protected drafts survive immediate navigation without changing files', () => editorRecovery(hostPage, host, profiles))
  await step('real synthetic lifecycle, Logs reading controls and contributing weekly sessions', () => lifecycleLogsAndSessions(hostPage, host, profiles))
  await step('unified backup pin/filter/retention/verification and explicit cancelled Restore review', () => backupCatalog(hostPage, host, profiles))
  const friend = await startInstance('friend', 'friend')
  await step('pinned loopback Friend Play, per-server permissions, recovered chat and signed delivery', () => friendPlayAndChat(host, friend, context, profiles))
  await step('Attention notice controls and narrow/tablet 150 percent layouts', () => attentionAndLargeText(hostPage))
  skip('native import selection and import review', 'Native fixture pickers are exercised by the separate desktop CI lane; staging deliberately refuses existing-world import.')
  skip('Steam/client launch, game terms and official downloads', 'No real game client, native game launcher, terms consent or download is invoked by this browser smoke.')
  skip('real-game, separate-PC/WAN and world save/load/restart acceptance', 'All data and protocols here are disposable synthetic loopback fixtures; those external gates require their own evidence.')
  assert.equal(createHash('sha256').update(await readFile(appPath)).digest('hex'), candidateSha256, 'The exact candidate changed during browser smoke.')
  await flushBrowserEvidence()
  assert.equal(report.browserErrors.length, 0, 'The bundled UI emitted browser errors; inspect the redacted report.')
  assert.equal(report.failedRequests.length, 0, 'The bundled UI emitted failed requests; inspect the redacted report.')
} catch (error) {
  failed = true
  report.failure = redact(error.message)
  console.error(`FAIL QoL browser smoke: ${report.failure}`)
  if (browser) {
    for (const [index, page] of browser.contexts().flatMap(context => context.pages()).entries()) {
      try { await screenshot(page, `failure-${index}`, page.viewportSize() ?? { width: 1440, height: 900 }) }
      catch { /* Preserve the primary failure and always continue exact lifecycle cleanup. */ }
    }
  }
} finally {
  try { await cleanup() }
  catch (error) {
    failed = true
    report.cleanup.push({ resource: 'cleanup', outcome: 'failed', message: redact(error.message) })
    report.outcome = 'failed'
    await writeFile(path.join(evidenceRoot, 'qol-browser-report.json'), JSON.stringify(report, null, 2))
  }
  console.log(`QoL browser evidence: ${evidenceRoot}`)
  process.exitCode = failed || report.outcome === 'failed' ? 1 : 0
}
