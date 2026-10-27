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
let baselineUiDir
if (options.baselineUiDir) {
  assert(path.isAbsolute(options.baselineUiDir), '--baseline-ui-dir must be absolute.')
  baselineUiDir = await realpath(options.baselineUiDir)
  assert((await lstat(baselineUiDir)).isDirectory() && (await lstat(path.join(baselineUiDir, 'index.html'))).isFile(),
    '--baseline-ui-dir must contain a built index.html and assets directory.')
}
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
  journeys: [], screenshots: [], layout: [], browserErrors: [], failedRequests: [], expectedModeDenials: [], expectedModeCancellations: [], deduplicatedConsoleHttpErrors: [], modeTransitionDiagnostics: [], guidedEditorDom: [], chatRecovery: [], chatDelivery: [],
  dialogs: [], requiredSetup: [], protectedReloads: [], lifecycleStarts: [], cleanup: [], startedUtc: new Date().toISOString() }
const browserCollectors = []
const protectedReloadTickets = new WeakMap()
const pendingResponseClassifications = new Set()
let browser
let failed = false
let stopping = false
let cleanupPromise

function parseArguments(args) {
  const parsed = { allowInteractive: false, appPath: '', outputDir: '', baselineUiDir: '' }
  for (let index = 0; index < args.length; index++) {
    const argument = args[index]
    if (argument === '--allow-interactive-tests') parsed.allowInteractive = true
    else if (argument === '--app-path' || argument === '--output-dir' || argument === '--baseline-ui-dir') {
      assert(args[index + 1] && !args[index + 1].startsWith('--'), `${argument} requires a value.`)
      parsed[argument === '--app-path' ? 'appPath' : argument === '--baseline-ui-dir' ? 'baselineUiDir' : 'outputDir'] = args[++index]
    } else throw new Error('Use --app-path <absolute EXE> --allow-interactive-tests [--output-dir <artifact folder>] [--baseline-ui-dir <absolute built UI directory>].')
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
async function boundedResponseJson(response, maximumBytes = 2048, diagnostics) {
  const headers = response.headers()
  const jsonContentType = /^application\/json(?:;|$)/iu.test(headers['content-type'] ?? '')
  const length = headers['content-length']
  const validLength = length === undefined || /^\d{1,9}$/u.test(length)
  if (diagnostics) {
    diagnostics.contentType = jsonContentType ? 'json' : headers['content-type'] ? 'other' : 'missing'
    diagnostics.declaredBytes = length !== undefined && validLength ? Number(length) : null
  }
  const outcome = value => { if (diagnostics) diagnostics.bodyOutcome = value }
  if (!jsonContentType) { outcome('non-json-content-type'); return null }
  if (!validLength) { outcome('invalid-content-length'); return null }
  if (length !== undefined && Number(length) > maximumBytes) { outcome('declared-body-over-limit'); return null }
  let timer
  let stage = 'body'
  try {
    const bytes = await Promise.race([
      response.body(),
      new Promise(resolve => { timer = setTimeout(() => resolve(null), 5000) })
    ])
    if (!bytes) { outcome('body-read-timeout'); return null }
    if (bytes.length > maximumBytes) { outcome('body-over-limit'); return null }
    if (diagnostics) diagnostics.bodyBytes = bytes.length
    stage = 'json'
    const value = JSON.parse(bytes.toString('utf8'))
    if (!value || typeof value !== 'object' || Array.isArray(value)) { outcome('non-object-json'); return null }
    outcome('json-object')
    return value
  } catch { outcome(stage === 'json' ? 'invalid-json' : 'body-read-failed'); return null }
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
    started: order, responded: null, bodyReady: null, confirmed: false }
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
    transition.bodyReady = ++collector.order
    transition.confirmed = body?.ok === true && body.code === 'ModeChanged'
    if (transition.confirmed && order > collector.modeOrder) {
      collector.mode = transition.to; collector.modeOrder = order
    }
  } else if (url.origin === collector.origin && url.pathname === '/api/local/snapshot' && !url.search &&
      request.method() === 'GET' && status === 200) {
    const body = await boundedResponseJson(response, 512 * 1024)
    const bodyReady = ++collector.order
    if (body?.mode === 'Host' || body?.mode === 'Friend') {
      collector.snapshots.push({ mode: body.mode, started, responded: order, bodyReady })
      if (order > collector.modeOrder) { collector.mode = body.mode; collector.modeOrder = order }
    }
  }
  if (status < 400) return
  const responseShape = { contentType: null, declaredBytes: null, bodyBytes: null, bodyOutcome: 'foreign-origin',
    codeField: 'unread', alternateCodeField: false, requestFailure: null, requestFailureOrder: null }
  const body = url.origin === collector.origin ? await boundedResponseJson(response, 2048, responseShape) : null
  const bodyReady = ++collector.order
  const code = typeof body?.code === 'string' && /^[a-z][a-z\d]{0,63}$/iu.test(body.code) ? body.code : null
  if (body) {
    responseShape.codeField = !Object.hasOwn(body, 'code') ? 'missing' : code ? 'valid' : typeof body.code === 'string' ? 'invalid-string' : 'non-string'
    responseShape.alternateCodeField = Object.hasOwn(body, 'Code')
  }
  const failure = request.failure()
  responseShape.requestFailure = !failure ? null : failure.errorText === 'net::ERR_ABORTED' ? 'aborted' : 'other'
  collector.responses.push({ request, url: response.url(), status, code, role: roleBoundRead(request, url, collector.origin), started,
    responded: order, bodyReady, evidence: { method: request.method(), status, route: redact(url.pathname), code, responseShape }, classified: false, consoleConsumed: false })
}
function modeBeforeRequest(collector, started) {
  // A delayed snapshot may have captured its mode before a later successful
  // change. Its request chronology cannot overwrite that change's receipt.
  return [...collector.snapshots.filter(snapshot => Number.isSafeInteger(snapshot.started))
    .map(snapshot => ({ mode: snapshot.mode, causalOrder: snapshot.started, responded: snapshot.responded })),
  ...collector.transitions.filter(transition => transition.confirmed)
    .map(transition => ({ mode: transition.to, causalOrder: transition.responded, responded: transition.responded }))]
    .filter(mode => mode.responded < started)
    .sort((left, right) => right.causalOrder - left.causalOrder || right.responded - left.responded)[0]?.mode ?? null
}
function modeTransitionWindow(collector, transition, index) {
  const nextStarted = collector.transitions[index + 1]?.started ?? Infinity
  // Only a read initiated after the exact ModeChanged response can acknowledge
  // this change. Headers alone do not mean its body is ready for the UI.
  const acknowledgement = transition.confirmed ? collector.snapshots.filter(snapshot => snapshot.mode === transition.to &&
    snapshot.started > transition.responded && snapshot.started < nextStarted &&
    snapshot.responded > transition.responded && Number.isSafeInteger(snapshot.bodyReady))
    .sort((left, right) => left.bodyReady - right.bodyReady)[0] : undefined
  return { acknowledgement, ended: transition.responded === null ? null :
    Math.min(acknowledgement?.bodyReady ?? transition.responded, nextStarted) }
}
function correlatedModeTransition(collector, response) {
  if (!response.role || !Number.isSafeInteger(response.started)) return null
  return collector.transitions.find((transition, index) => {
    if (!transition.confirmed || transition.from !== response.role || transition.to === response.role) return false
    const { ended } = modeTransitionWindow(collector, transition, index)
    return response.started < ended && response.responded > transition.started
  }) ?? null
}
function expectedModeTransition(collector, response) {
  if (response.status !== 409 || response.code !== (response.role === 'Host' ? 'FriendMode' : 'HostMode')) return null
  return correlatedModeTransition(collector, response)
}
function expectedModeCancellation(collector, response) {
  if (response.status !== 409 || response.code !== null || response.abort?.kind !== 'aborted' ||
      !['body-read-failed', 'body-read-timeout'].includes(response.evidence.responseShape.bodyOutcome) ||
      response.abort.order <= response.responded || response.abort.order > response.bodyReady) return null
  const transition = correlatedModeTransition(collector, response)
  if (!transition) return null
  // A fresh acknowledgement fences new reads. An earlier in-flight read may
  // finish cancelling after that acknowledgement as its old UI scope unmounts.
  const nextStarted = collector.transitions[collector.transitions.indexOf(transition) + 1]?.started ?? Infinity
  return response.abort.order < nextStarted ? transition : null
}
function checkModeTransitionCorrelation() {
  const transition = { id: 1, from: 'Host', to: 'Friend', started: 10, responded: 20, bodyReady: 28, confirmed: true }
  const collector = { transitions: [transition], snapshots: [
    { mode: 'Host', started: 1, responded: 2, bodyReady: 3 },
    { mode: 'Friend', started: 15, responded: 21, bodyReady: 25 }, // Poll issued before ModeChanged.
    { mode: 'Friend', started: 30, responded: 40, bodyReady: 50 },
    { mode: 'Host', started: 5, responded: 70, bodyReady: 71 } // Late stale response cannot undo the confirmed change.
  ] }
  const denial = { status: 409, role: 'Host', code: 'FriendMode', started: 45, responded: 46 }
  assert.equal(modeBeforeRequest(collector, 10), 'Host')
  assert.equal(modeBeforeRequest(collector, 80), 'Friend')
  assert.equal(modeTransitionWindow(collector, transition, 0).acknowledgement.started, 30)
  assert.equal(expectedModeTransition(collector, denial), transition, 'Snapshot headers cannot close the transition before its body is ready.')
  assert.equal(expectedModeTransition(collector, { ...denial, started: 50, responded: 51 }), null, 'Post-ack wrong-role reads remain failures.')
  assert.equal(expectedModeTransition(collector, { ...denial, status: 400 }), null)
  assert.equal(expectedModeTransition(collector, { ...denial, code: 'UnknownProfile' }), null)
  assert.equal(expectedModeTransition(collector, { ...denial, code: null }), null, 'An unread or untyped denial remains a failure, even during a real transition.')
  assert.equal(expectedModeTransition(collector, { ...denial, role: null }), null)
  const cancelled = { ...denial, code: null, bodyReady: 49, abort: { kind: 'aborted', order: 48 },
    evidence: { responseShape: { bodyOutcome: 'body-read-failed' } } }
  assert.equal(expectedModeCancellation(collector, cancelled), transition, 'A real scoped request abort is separate from an inferred mode denial.')
  assert.equal(expectedModeCancellation(collector, { ...cancelled, abort: null }), null)
  assert.equal(expectedModeCancellation(collector, { ...cancelled, code: 'UnknownProfile' }), null)
  assert.equal(expectedModeCancellation(collector, { ...cancelled, role: null }), null)
  assert.equal(expectedModeCancellation(collector, { ...cancelled, started: 50 }), null, 'Post-ack aborts do not excuse wrong-role requests.')
  assert.equal(expectedModeCancellation(collector, { ...cancelled, started: 45, responded: 46, bodyReady: 52,
    abort: { kind: 'aborted', order: 51 } }), transition, 'An earlier in-flight read can cancel after acknowledgement; the request start remains fenced.')
  assert.equal(expectedModeCancellation(collector, { ...cancelled, abort: { kind: 'aborted', order: 50 } }), null, 'An abort after the failed body read cannot establish its cause.')
  assert.equal(expectedModeCancellation(collector, { ...cancelled, evidence: { responseShape: { bodyOutcome: 'json-object' } } }), null, 'Readable untyped HTTP failures remain failures.')
  collector.transitions.push({ id: 2, from: 'Friend', to: 'Host', started: 44, responded: 60, bodyReady: 61, confirmed: true })
  assert.equal(expectedModeTransition(collector, denial), null, 'A later transition cannot extend an earlier allowance.')
  assert.equal(expectedModeCancellation(collector, cancelled), null)
}
async function flushBrowserEvidence() {
  while (pendingResponseClassifications.size > 0) await Promise.allSettled([...pendingResponseClassifications])
  for (const collector of browserCollectors) {
    for (const transition of collector.transitions) {
      transition.from = modeBeforeRequest(collector, transition.started)
    }
    for (const response of collector.responses) {
      if (response.classified) continue
      const failure = collector.requestFailures.get(response.request)
      response.abort = failure?.kind === 'aborted' ? failure : null
      response.evidence.responseShape.requestFailure = failure?.kind ?? response.evidence.responseShape.requestFailure
      response.evidence.responseShape.requestFailureOrder = failure?.order ?? null
      const transition = expectedModeTransition(collector, response)
      const cancellation = transition ? null : expectedModeCancellation(collector, response)
      response.expected = !!transition || !!cancellation
      response.expectedAs = transition ? 'expected-mode-denial' : cancellation ? 'expected-mode-cancellation' : 'failed-request'
      if (transition) report.expectedModeDenials.push({ ...response.evidence, page: collector.id, transition: transition.id,
        fromMode: transition.from, toMode: transition.to, transitionCode: 'ModeChanged',
        requestOrder: response.started, responseOrder: response.responded, bodyReadyOrder: response.bodyReady })
      else if (cancellation) report.expectedModeCancellations.push({ ...response.evidence, page: collector.id, transition: cancellation.id,
        fromMode: cancellation.from, toMode: cancellation.to, transitionCode: 'ModeChanged',
        requestOrder: response.started, responseOrder: response.responded, bodyReadyOrder: response.bodyReady })
      else report.failedRequests.push({ ...response.evidence, page: collector.id, readRole: response.role,
        requestOrder: response.started ?? null, responseOrder: response.responded, bodyReadyOrder: response.bodyReady })
      response.classified = true
    }
    for (const message of collector.consoleErrors.splice(0)) {
      const match = /^Failed to load resource: the server responded with a status of (\d{3})(?: \([^\r\n]*\))?$/u.exec(message.text)
      const corresponding = match && collector.responses.find(response => !response.consoleConsumed &&
        response.url === message.url && response.status === Number(match[1]))
      if (corresponding) {
        corresponding.consoleConsumed = true
        report.deduplicatedConsoleHttpErrors.push({ ...corresponding.evidence, page: collector.id,
          observedAs: corresponding.expectedAs })
      } else report.browserErrors.push({ kind: 'console', message: redact(message.text) })
    }
  }
  report.modeTransitionDiagnostics = browserCollectors.flatMap(collector => collector.transitions.map((transition, index) => {
    const { acknowledgement, ended } = modeTransitionWindow(collector, transition, index)
    return { page: collector.id, transition: transition.id, fromMode: transition.from, toMode: transition.to,
      confirmed: transition.confirmed, requestOrder: transition.started, responseOrder: transition.responded,
      bodyReadyOrder: transition.bodyReady, acknowledgementRequestOrder: acknowledgement?.started ?? null,
      acknowledgementResponseOrder: acknowledgement?.responded ?? null, acknowledgementBodyReadyOrder: acknowledgement?.bodyReady ?? null,
      windowEndOrder: ended }
  }))
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
async function selectWorkspaceSection(page, label, name, value) {
  const mobile = page.getByRole('combobox', { name: label, exact: true })
  if (await mobile.isVisible()) {
    await mobile.selectOption(value)
    await eventually(() => mobile.inputValue(), selected => selected === value, `${name} section selection`)
  } else {
    const button = page.getByRole('navigation', { name: label, exact: true }).getByRole('button', { name, exact: true })
    await button.click()
    await eventually(() => button.getAttribute('aria-current'),
      current => current === (label === 'App settings sections' ? 'location' : 'page'), `${name} section navigation`)
  }
}
async function openAttention(page) {
  const navigation = page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true })
  const notifications = navigation.locator('.notification-menu')
  if (!await notifications.evaluate(element => element.open)) await notifications.locator(':scope > summary').click()
  await notifications.getByRole('button', { name: 'Open Attention Center', exact: true }).click()
  await eventually(() => navigation.getByRole('button', { name: 'Open Attention Center', exact: true, includeHidden: true }).getAttribute('aria-current'),
    value => value === 'page', 'Attention navigation completes through the notification menu')
}
async function openServerDirectory(page) {
  const sidebar = page.getByRole('complementary', { name: 'Server navigation', exact: true })
  await sidebar.waitFor({ state: 'visible' })
  const directory = sidebar.getByRole('region', { name: 'Servers', exact: true })
  await directory.waitFor({ state: 'visible' })
  assert.equal(await directory.count(), 1, 'The sidebar must expose one unified local and Friend server directory.')
  await directory.getByRole('searchbox', { name: 'Find a server or friend', exact: true }).waitFor({ state: 'visible' })
  return directory
}
async function selectHostServer(page, name) {
  await page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true }).getByRole('button', { name: 'Servers', exact: true }).click()
  const directory = await openServerDirectory(page)
  await directory.getByRole('searchbox', { name: 'Find a server or friend', exact: true }).fill('')
  const row = directory.getByRole('button', { name: `Open ${name} on this PC`, exact: true })
  await row.click()
  await eventually(() => row.getAttribute('aria-current'), value => value === 'true', 'selected local server directory row')
  const workspace = page.getByRole('region', { name: `${name} workspace`, exact: true })
  await workspace.waitFor()
  await eventually(() => workspace.getAttribute('data-server-tab'), value => value === 'overview',
    'directory selection completes before a subsequent server section request')
}
async function selectServer(page, name, tab = 'Overview') {
  await selectHostServer(page, name)
  const label = ({ Files: 'Settings & files', Setup: 'Setup & maintenance' })[tab] ?? tab
  const value = ({ Files: 'files', Setup: 'setup' })[tab] ?? tab.toLowerCase()
  await selectWorkspaceSection(page, 'Selected server sections', label, value)
  return page.getByRole('region', { name: `${name} workspace`, exact: true })
}
async function closeCommandPalette(palette, action, label) {
  // showModal/close is effect-driven. Hidden geometry precedes the native close
  // event, whose React handler can otherwise close a freshly reopened palette.
  const previous = await palette.evaluate(dialog => {
    if (dialog.dataset.qolCloseReceipts === undefined) {
      dialog.dataset.qolCloseReceipts = '0'
      dialog.addEventListener('close', () => {
        dialog.dataset.qolCloseReceipts = String(Number(dialog.dataset.qolCloseReceipts) + 1)
      })
    }
    return Number(dialog.dataset.qolCloseReceipts)
  })
  await action()
  await eventually(() => palette.evaluate(dialog => ({ receipts: Number(dialog.dataset.qolCloseReceipts), open: dialog.open,
    expanded: dialog.querySelector('[aria-label="Search commands"]')?.getAttribute('aria-expanded') })),
  current => current.receipts > previous && !current.open && current.expanded === 'false', `${label}: native palette close receipt`)
  await palette.waitFor({ state: 'hidden' })
  report.dialogs.push({ type: 'command-palette', outcome: 'native-close-received', action: label })
}
async function acceptAppConfirmation(page, title, confirmLabel) {
  const dialog = page.getByRole('dialog', { name: title, exact: true })
  await dialog.waitFor({ state: 'visible' })
  assert.equal(await dialog.getByRole('button', { name: 'Cancel', exact: true }).isVisible(), true,
    'The owner must retain a visible way to cancel the reviewed action.')
  report.dialogs.push({ type: 'app-confirmation', title, outcome: 'accepted-reviewed-confirm', confirmLabel })
  await dialog.getByRole('button', { name: confirmLabel, exact: true }).click()
  await dialog.waitFor({ state: 'hidden' })
}
async function completeRequiredProfileSetup(page, instance) {
  await step(`required profile setup gates the fresh ${instance.name} before ordinary navigation`, async () => {
    const setup = page.getByRole('region', { name: 'Required app setup', exact: true })
    const navigation = page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true })
    await setup.waitFor({ state: 'visible' })
    const before = await api(instance, '/api/local/required-setup')
    assert.deepEqual(before.features.map(feature => ({ id: feature.id, revision: feature.revision, completed: feature.completed })),
      [{ id: 'profile-name', revision: 1, completed: false }], 'A fresh instance must report its actual missing profile step.')
    assert.equal((await api(instance, '/api/local/profile')).displayName, '', 'Required setup must not invent a name.')
    assert.equal(await navigation.count(), 0, 'Ordinary workspace navigation stays unavailable before required setup.')
    await setup.getByRole('heading', { level: 1 }).click()
    await page.keyboard.press('Alt+2')
    await page.keyboard.press('Control+k')
    assert.equal(await navigation.count(), 0, 'Workspace shortcuts cannot bypass required setup.')
    assert.equal(await page.getByRole('dialog', { name: 'Command palette', exact: true }).count(), 0,
      'Commands cannot bypass required setup.')
    const profile = setup.getByRole('form', { name: 'Shared profile', exact: true })
    const save = profile.getByRole('button', { name: 'Save and continue', exact: true })
    assert.equal(await save.isDisabled(), true, 'An empty required profile cannot be submitted.')
    const displayName = `Synthetic browser ${instance.name}`
    await profile.getByRole('textbox', { name: 'Your name', exact: true }).fill(displayName)
    const [response] = await Promise.all([
      page.waitForResponse(value => value.url() === `${instance.origin}/api/local/required-setup/profile-name/complete` &&
        value.request().method() === 'POST'),
      save.click()
    ])
    assert.equal(response.status(), 200)
    assert.deepEqual(response.request().postDataJSON(), { revision: 1 }, 'The UI acknowledges only the reviewed feature revision.')
    const result = await boundedResponseJson(response)
    assert.equal(result?.ok, true)
    assert.equal(result?.code, 'RequiredFeatureCompleted')
    await navigation.waitFor({ state: 'visible' })
    await setup.waitFor({ state: 'hidden' })
    const [saved, current, snapshot] = await Promise.all([
      api(instance, '/api/local/profile'), api(instance, '/api/local/required-setup'), api(instance, '/api/local/snapshot')
    ])
    assert.equal(saved.displayName, displayName, 'The displayed name must persist through the real owner API.')
    assert(current.features.every(feature => feature.completed), 'Navigation follows authoritative completed setup.')
    assert.equal(snapshot.profiles?.length ?? snapshot.settings?.profiles.length, 0, 'Profile setup cannot configure a game server.')
    assert.equal(snapshot.runs?.length ?? 0, 0, 'Profile setup cannot start a managed game process.')
    await page.reload({ waitUntil: 'domcontentloaded' })
    await navigation.waitFor({ state: 'visible' })
    assert.equal(await setup.count(), 0, 'A saved valid name skips required setup on the next app page load.')
    report.requiredSetup.push({ instance: instance.name, revision: 1, shortcutsGated: true, persistedNameMatches: true,
      authoritativeCompletion: true, reloadSkippedCompletedStep: true, noGameConfiguredOrStarted: true })
  })
}
async function openPage(instance, context) {
  const page = await context.newPage()
  page.setDefaultTimeout(15_000)
  const collector = { id: browserCollectors.length + 1, origin: instance.origin, order: 0, mode: null, modeOrder: 0,
    requestOrders: new WeakMap(), requestFailures: new WeakMap(), modeRequests: new WeakMap(), transitions: [], snapshots: [], responses: [], consoleErrors: [] }
  browserCollectors.push(collector)
  page.on('request', request => observeBrowserRequest(collector, request))
  page.on('pageerror', error => report.browserErrors.push({ kind: 'pageerror', message: redact(error.message) }))
  page.on('console', message => {
    if (message.type() === 'error') collector.consoleErrors.push({ text: message.text(), url: message.location().url })
  })
  page.on('requestfailed', request => {
    const failure = request.failure()?.errorText ?? 'Request failed'
    const kind = failure === 'net::ERR_ABORTED' ? 'aborted' : 'other'
    collector.requestFailures.set(request, { kind, order: ++collector.order })
    if (kind === 'aborted') return // Preserve actual cancellation metadata for any observed HTTP response.
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
    const ticket = protectedReloadTickets.get(page)
    if (dialog.type() === 'beforeunload' && ticket && ticket.expiresUtc > Date.now()) {
      protectedReloadTickets.delete(page) // One protected reload, never a blanket dialog exception.
      ticket.evidence.dialogObserved = true
      report.dialogs.push({ type: dialog.type(), message: redact(dialog.message()), outcome: 'accepted-protected-reload', purpose: ticket.purpose })
      await dialog.accept()
    } else if (dialog.type() === 'confirm' && /discard|reload|prepare to change|stop|restart|finish later clears/iu.test(dialog.message())) {
      report.dialogs.push({ type: dialog.type(), message: redact(dialog.message()), outcome: 'accepted-reviewed-confirm' })
      await dialog.accept()
    } else {
      report.dialogs.push({ type: dialog.type(), message: redact(dialog.message()), outcome: 'dismissed-unexpected' })
      await dialog.dismiss(); report.browserErrors.push({ kind: 'unexpected-dialog', message: 'An unexpected dialog was dismissed.' })
    }
  })
  await page.goto(instance.origin, { waitUntil: 'domcontentloaded' })
  await completeRequiredProfileSetup(page, instance)
  await page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true }).waitFor()
  await step(`optional setup guide can be deferred and reopened on ${instance.name}`, async () => {
    const welcome = page.getByRole('dialog', { name: 'Make room for your next game night', exact: true })
    const initial = await api(instance, '/api/local/getting-started')
    if (!initial.offered || initial.dismissed)
      await page.getByRole('button', { name: 'Setup guides', exact: true }).click()
    await welcome.waitFor({ state: 'visible' })
    await welcome.getByRole('button', { name: /^Host on this PC/u }).waitFor({ state: 'visible' })
    await welcome.getByRole('button', { name: /^Join a friend/u }).waitFor({ state: 'visible' })
    const [response] = await Promise.all([
      page.waitForResponse(value => value.url() === `${instance.origin}/api/local/getting-started` && value.request().method() === 'PUT'),
      welcome.getByRole('button', { name: 'Set up later', exact: true }).click()
    ])
    assert.equal(response.status(), 200)
    assert.equal((await boundedResponseJson(response)).dismissed, true)
    await welcome.waitFor({ state: 'hidden' })
    await page.reload({ waitUntil: 'domcontentloaded' })
    await page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true }).waitFor()
    assert.equal((await api(instance, '/api/local/getting-started')).dismissed, true)
    assert.equal(await welcome.count(), 0, 'Deferred optional help must stay closed after reload.')
    await page.getByRole('button', { name: 'Setup guides', exact: true }).click()
    await welcome.waitFor({ state: 'visible' })
    await welcome.getByRole('button', { name: 'Set up later', exact: true }).click()
    await welcome.waitFor({ state: 'hidden' })
    report.optionalGuides ??= []
    report.optionalGuides.push({ instance: instance.name, actualWelcomeChoices: true,
      persistedDeferral: true, reopenedFromHeader: true })
  })
  // These nodes come from the candidate's embedded bundle, never a Vite server or page.setContent.
  assert(await page.locator('script[src^="/assets/"]').count() > 0, 'The candidate must serve its bundled React assets.')
  return page
}

async function reloadProtectedSetup(page, host, expected, purpose) {
  assert(['first-use-setup-recovery', 'populated-setup-recovery'].includes(purpose), 'Only the two deliberate protected setup reloads may accept beforeunload.')
  const evidence = { purpose, protectedReadOk: false, exactScopeMatches: false, exactWorldMatches: false,
    exactStepMatches: false, secretExcluded: false, dialogObserved: false, outcome: 'checking-store' }
  report.protectedReloads.push(evidence)
  const stored = await api(host, '/api/local/ui-drafts/read', 'POST', { purpose: 'settings',
    profileId: '00000000-0000-0000-0000-000000000000', connectionId: null, key: 'host-setup' })
  evidence.protectedReadOk = stored.ok === true && typeof stored.text === 'string' && stored.text.length <= 128 * 1024
  assert(evidence.protectedReadOk, 'Reload requires a real persisted protected setup response.')
  const draft = JSON.parse(stored.text)
  const profile = Array.isArray(draft.profiles) ? draft.profiles.find(item => item.id === expected.profileId) : null
  evidence.exactScopeMatches = draft.version === 3 && draft.activeProfileId === expected.profileId && profile !== null && profile !== undefined
  evidence.exactWorldMatches = profile?.worldId === expected.worldId
  evidence.exactStepMatches = draft.step === expected.step
  evidence.secretExcluded = !stored.text.includes('fixture-pass-123') && !Object.hasOwn(profile ?? {}, 'password')
  assert(evidence.exactScopeMatches && evidence.exactWorldMatches && evidence.exactStepMatches && evidence.secretExcluded,
    'Reload requires the exact selected nonsecret setup and step in the real protected store.')
  evidence.outcome = 'authorized-one-reload'
  assert(!protectedReloadTickets.has(page), 'A page cannot have overlapping protected reload tickets.')
  protectedReloadTickets.set(page, { purpose, expiresUtc: Date.now() + 15_000, evidence })
  try {
    await page.reload({ waitUntil: 'domcontentloaded' })
    evidence.outcome = 'reloaded'
  } catch (error) {
    evidence.outcome = 'reload-failed'
    throw error
  } finally { protectedReloadTickets.delete(page) }
}
async function screenshot(page, name, viewport, subject, { assertLayout = true } = {}) {
  await page.setViewportSize(viewport)
  if (subject) {
    assert.equal(await subject.count(), 1, `${name}: the screenshot subject must be unique.`)
    await subject.waitFor({ state: 'visible' })
    await subject.scrollIntoViewIfNeeded()
  }
  const metrics = await page.evaluate(() => ({ width: window.innerWidth,
    documentWidth: document.documentElement.scrollWidth, theme: document.documentElement.dataset.theme,
    density: document.documentElement.dataset.density, textScale: getComputedStyle(document.documentElement).getPropertyValue('--qol-text-scale').trim(),
    highContrast: document.documentElement.dataset.highContrast,
    liveStatusCount: document.querySelectorAll('[role="status"],[aria-live="polite"]').length,
    headerControls: [...document.querySelectorAll('.header-tools > .header-guides-trigger, .header-tools > .header-servers-trigger, .header-tools > details > summary, .header-tools > .app-settings-trigger, .header-tools > .command-trigger')]
      .map(control => { const bounds = control.getBoundingClientRect(); return { name: control.getAttribute('aria-label'),
        left: bounds.left, right: bounds.right, width: bounds.width, height: bounds.height } }) }))
  report.layout.push({ name, ...metrics })
  await page.screenshot({ path: path.join(evidenceRoot, `${name}.png`), fullPage: false,
    mask: [page.locator('code:visible'), page.locator('input[type="password"]:visible'), page.locator('.invite-input:visible')] })
  report.screenshots.push(`${name}.png`)
  if (!assertLayout) return // Baseline is observation-only, never a passing layout assertion.
  assert(metrics.documentWidth <= metrics.width + 1, `${name}: the rendered page must not overflow horizontally.`)
  assert.deepEqual(metrics.headerControls.map(control => control.name?.replace(/^Notifications.*$/u, 'Notifications')),
    ['Setup guides', 'Servers', 'Notifications', 'App Settings', 'Commands'], `${name}: all five header navigation controls must be present.`)
  assert(metrics.headerControls.every(control => control.name && control.width > 0 && control.height > 0 &&
    control.left >= -1 && control.right <= metrics.width + 1), `${name}: named header controls must fit inside the viewport.`)
}

async function firstUseAndSetupRecovery(page, host) {
  const navigation = page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true })
  const firstHost = page.getByRole('region', { name: 'Host or join a server', exact: true })
  const viewports = [{ width: 1440, height: 900 }, { width: 390, height: 844 }]
  const assertEmptyBackend = async () => {
    const snapshot = await api(host, '/api/local/snapshot')
    assert.equal(snapshot.mode, 'Host')
    assert.equal(snapshot.settings.profiles.length, 0, 'First-use and draft review must not save a server profile.')
    assert.equal(snapshot.runs.length, 0, 'First-use setup must not create a managed game process.')
  }
  const capture = async (phase, recovery = null) => {
    for (const textScale of ['100', '150']) {
      await page.setViewportSize({ width: 1440, height: 900 })
      await navigation.getByRole('button', { name: 'App Settings', exact: true }).click()
      await setAppearanceTextScale(page, textScale)
      await eventually(() => page.evaluate(() => getComputedStyle(document.documentElement).fontSize),
        value => value === (textScale === '150' ? '24px' : '16px'), `${phase} ${textScale} percent text`)
      await navigation.getByRole('button', { name: 'Servers', exact: true }).click()
      await firstHost.waitFor({ state: 'visible' })
      for (const viewport of viewports) {
        const name = `${phase}-${viewport.width}x${viewport.height}-${textScale}`
        await screenshot(page, name, viewport, firstHost)
        const banner = page.locator('.staging-banner')
        const staging = await banner.evaluate(element => {
          const bounds = element.getBoundingClientRect(), label = element.querySelector(':scope > strong'), labelBounds = label.getBoundingClientRect(),
            style = getComputedStyle(element), painted = document.elementFromPoint(labelBounds.left + labelBounds.width / 2, labelBounds.top + labelBounds.height / 2)
          const initialScrollTop = element.scrollTop
          element.scrollTop = element.scrollHeight
          const factsBottomAtEnd = element.querySelector(':scope > span').getBoundingClientRect().bottom, endScrollTop = element.scrollTop
          element.scrollTop = initialScrollTop
          return { top: bounds.top, bottom: bounds.bottom, scrollTop: initialScrollTop, scrollHeight: element.scrollHeight,
            clientHeight: element.clientHeight, overflowY: style.overflowY, labelTop: labelBounds.top, labelBottom: labelBounds.bottom,
            labelPainted: painted === label || label.contains(painted), factsBottomAtEnd, endScrollTop }
        })
        report.layout.push({ name: `${name}-staging-ribbon`, staging })
        assert.equal(staging.scrollTop, 0, `${name}: staging ribbon must initially show its first content.`)
        assert(staging.labelTop >= staging.top - 1 && staging.labelBottom <= staging.bottom + 1 && staging.labelPainted,
          `${name}: DEVELOPMENT / STAGING must be inside the painted visible banner, never centered above its scrollport.`)
        assert(staging.scrollHeight <= staging.clientHeight + 1 ||
          ['auto', 'scroll'].includes(staging.overflowY) && staging.endScrollTop > 0,
          `${name}: long staging facts must remain scrollable.`)
        assert(staging.factsBottomAtEnd <= staging.bottom + 1 && staging.factsBottomAtEnd >= staging.top,
          `${name}: the last staging facts must be reachable at the end of the banner scroll.`)
        const alignment = await firstHost.evaluate(element => {
          const bounds = element.getBoundingClientRect(), container = element.closest('.context-content'),
            workspace = container.getBoundingClientRect(), workspaceStyle = getComputedStyle(container),
            style = getComputedStyle(element), recovered = element.querySelector('.setup-draft-recovery')?.getBoundingClientRect()
          return { left: bounds.left, right: bounds.right, width: bounds.width,
            workspaceLeft: workspace.left + container.clientLeft + Number.parseFloat(workspaceStyle.paddingLeft),
            workspaceRight: workspace.left + container.clientLeft + container.clientWidth - Number.parseFloat(workspaceStyle.paddingRight),
            contentLeft: bounds.left + Number.parseFloat(style.paddingLeft) + Number.parseFloat(style.borderLeftWidth),
            contentRight: bounds.right - Number.parseFloat(style.paddingRight) - Number.parseFloat(style.borderRightWidth),
            recoveryLeft: recovered?.left ?? null, recoveryRight: recovered?.right ?? null }
        })
        report.layout.push({ name: `${name}-shared-width`, alignment })
        assert(Math.abs(alignment.left - alignment.workspaceLeft) <= 1 && Math.abs(alignment.right - alignment.workspaceRight) <= 1,
          `${name}: first-use content must fill the current workspace content width.`)
        assert.equal(await firstHost.getByRole('button', { name: 'Host a server', exact: true }).isVisible(), true)
        assert.equal(await firstHost.getByRole('button', { name: 'Join a server', exact: true }).isVisible(), true)
        if (recovery) {
          assert.equal(await recovery.getByRole('button', { name: 'Review saved setup', exact: true }).isVisible(), true)
          assert.equal(await recovery.getByRole('button', { name: 'Discard saved setup', exact: true }).isVisible(), true)
          assert(Math.abs(alignment.recoveryLeft - alignment.contentLeft) <= 1 && Math.abs(alignment.recoveryRight - alignment.contentRight) <= 1,
            `${name}: protected setup recovery uses the first-use panel's content width.`)
        }
      }
    }
  }
  await assertEmptyBackend()
  await firstHost.waitFor({ state: 'visible' })
  await capture('first-host-empty')
  await firstHost.getByRole('button', { name: 'Host a server', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Add new server', exact: true })
  await dialog.waitFor({ state: 'visible' })
  await dialog.getByRole('heading', { name: 'Choose a game', exact: true }).waitFor()
  await dialog.getByRole('button', { name: 'Cancel setup', exact: true }).click()
  await acceptAppConfirmation(page, 'Discard setup changes?', 'Discard changes')
  await dialog.waitFor({ state: 'hidden' })
  assert.equal(await firstHost.getByRole('button', { name: 'Host a server', exact: true }).isVisible(), true)
  await assertEmptyBackend()

  // Keep only a new-world name. Never enter a password, accept terms, select a binary,
  // Save settings, Start a server or manipulate world files in this first-use journey.
  await firstHost.getByRole('button', { name: 'Host a server', exact: true }).click()
  await dialog.getByRole('button', { name: 'Continue', exact: true }).click()
  const firstWorldName = dialog.getByRole('textbox', { name: /^World name/u })
  await firstWorldName.fill('browser-first-use-world')
  const firstProfileId = (await firstWorldName.getAttribute('id'))?.replace(/^setup-/u, '').replace(/-world-id$/u, '')
  assert(guidExpression.test(firstProfileId ?? ''), 'The first-use world input must identify its actual draft profile.')
  const password = dialog.locator('input[id^="setup-"][id$="-game-password"]')
  assert.equal(await password.inputValue(), '', 'First-use evidence must contain no entered password.')
  await dialog.getByRole('button', { name: 'Finish later', exact: true }).click()
  await dialog.waitFor({ state: 'hidden' })
  const protectedDraft = await api(host, '/api/local/ui-drafts/read', 'POST', { purpose: 'settings',
    profileId: '00000000-0000-0000-0000-000000000000', connectionId: null, key: 'host-setup' })
  assert.equal(protectedDraft.ok, true)
  assert(protectedDraft.text?.includes('browser-first-use-world'), 'Finish later must keep the nonsecret setup in the real protected store.')
  // Reload is intentional: a recovery prompt appears for the persisted draft on the next load.
  await reloadProtectedSetup(page, host, { profileId: firstProfileId, worldId: 'browser-first-use-world', step: 'world' }, 'first-use-setup-recovery')
  const recovery = firstHost.getByRole('region', { name: 'Recovered server setup', exact: true })
  await recovery.waitFor({ state: 'visible' })
  assert.equal(await recovery.getByText('Saved server setup', { exact: true }).isVisible(), true)
  assert.equal(await recovery.getByText('The server file has not changed.', { exact: false }).count(), 0,
    'Server setup recovery uses setup wording, rather than suggesting a saved file edit.')
  await capture('first-host-protected-setup', recovery)
  await recovery.getByRole('button', { name: 'Review saved setup', exact: true }).click()
  await dialog.getByRole('heading', { name: 'Choose a world', exact: true }).waitFor()
  assert.equal(await dialog.getByRole('textbox', { name: /^World name/u }).inputValue(), 'browser-first-use-world')
  assert.equal(await password.inputValue(), '', 'Recovery cannot restore a game password.')
  await dialog.getByRole('button', { name: 'Cancel setup', exact: true }).click()
  await acceptAppConfirmation(page, 'Discard setup changes?', 'Discard changes')
  await dialog.waitFor({ state: 'hidden' })
  await recovery.waitFor({ state: 'hidden' })
  await assertEmptyBackend()
  const cleared = await api(host, '/api/local/ui-drafts/read', 'POST', { purpose: 'settings',
    profileId: '00000000-0000-0000-0000-000000000000', connectionId: null, key: 'host-setup' })
  assert.equal(cleared.ok, true)
  assert.equal(cleared.text, null, 'Cancel must clear only this disposable unsaved setup draft before profile seeding.')
  await page.setViewportSize({ width: 1440, height: 900 })
  await navigation.getByRole('button', { name: 'App Settings', exact: true }).click()
  await setAppearanceTextScale(page, '100')
  await navigation.getByRole('button', { name: 'Servers', exact: true }).click()
}

async function openAppearanceEditor(page, { baseline = false } = {}) {
  const appearance = page.getByRole('group', { name: 'Appearance' })
  if (!baseline) {
    await page.getByRole('region', { name: 'App Settings', exact: true }).waitFor({ state: 'visible' })
    await selectWorkspaceSection(page, 'App settings sections', 'Appearance', 'appearance')
    await appearance.waitFor({ state: 'visible' })
    return appearance
  }
  // Caller-supplied baseline assets may retain the earlier focused dialog.
  if (await appearance.isVisible()) return appearance
  await page.getByRole('button', { name: 'Appearance Edit appearance', exact: true }).click()
  await page.getByRole('dialog', { name: 'Appearance', exact: true }).waitFor({ state: 'visible' })
  await appearance.waitFor({ state: 'visible' })
  return appearance
}

async function closeAppearanceEditor(page) {
  const dialog = page.getByRole('dialog', { name: 'Appearance', exact: true })
  if (!await dialog.isVisible()) return
  await dialog.getByRole('button', { name: 'Done', exact: true }).click()
  await dialog.waitFor({ state: 'hidden' })
}

async function setAppearanceTextScale(page, textScale, options) {
  const appearance = await openAppearanceEditor(page, options)
  await appearance.getByRole('combobox', { name: /^Text size$/u }).selectOption(textScale)
  await closeAppearanceEditor(page)
}

async function routingAndAppearance(page, profiles) {
  const navigation = page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true })
  for (const [shortcut, destination] of [['Alt+3', 'Open Attention Center'], ['Alt+4', 'App Settings'], ['Alt+1', 'Servers']]) {
    // Global navigation deliberately leaves a focused editor's typing alone.
    await page.locator('.brand').click()
    await page.keyboard.press(shortcut)
    await eventually(() => navigation.getByRole('button', { name: destination, exact: true, includeHidden: true }).getAttribute('aria-current'),
      value => value === 'page', `keyboard opens ${destination}`)
  }
  await page.locator('.brand').click()
  await page.keyboard.press('Alt+2')
  await page.getByLabel('Server code', { exact: true }).waitFor({ state: 'visible' })
  assert.equal(await navigation.getByRole('button', { name: 'Servers', exact: true }).getAttribute('aria-current'), 'page',
    'Connect opens the Friend pairing flow inside Servers.')
  await selectHostServer(page, profiles.valheim.name)
  await navigation.getByRole('button', { name: 'App Settings', exact: true }).click()
  await page.getByRole('region', { name: 'App Settings', exact: true }).waitFor({ state: 'visible' })
  await page.keyboard.press('Control+k')
  const palette = page.getByRole('dialog', { name: 'Command palette', exact: true, includeHidden: true })
  const search = palette.getByRole('combobox', { name: 'Search commands', exact: true })
  await search.fill('Open Attention')
  await closeCommandPalette(palette, () => search.press('Enter'), 'Open Attention command')
  await eventually(() => navigation.getByRole('button', { name: 'Open Attention Center', exact: true, includeHidden: true }).getAttribute('aria-current'),
    value => value === 'page', 'Attention command destination renders before another palette request')
  await page.keyboard.press('Control+k')
  await search.fill('Connect server')
  await closeCommandPalette(palette, () => search.press('Enter'), 'Connect server command')
  await page.getByLabel('Server code', { exact: true }).waitFor({ state: 'visible' })
  await page.keyboard.press('Control+k')
  await search.fill('Open Servers')
  await closeCommandPalette(palette, () => search.press('Enter'), 'Open Servers command')
  await eventually(() => navigation.getByRole('button', { name: 'Servers', exact: true }).getAttribute('aria-current'),
    value => value === 'page', 'Servers palette navigation from Friend pairing')
  assert.equal(await page.getByLabel('Server code', { exact: true }).isVisible(), true,
    'Open Servers preserves the current Friend pairing mode.')
  await selectHostServer(page, profiles.valheim.name)
  await navigation.getByRole('button', { name: 'App Settings', exact: true }).click()
  await page.getByRole('region', { name: 'App Settings', exact: true }).waitFor({ state: 'visible' })
  await page.keyboard.press('Control+k')
  await search.fill('Synthetic Bedrock B')
  await closeCommandPalette(palette, () => palette.getByRole('option').filter({ hasText: /files/iu }).click(), 'Open selected server files command')
  await page.getByRole('region', { name: 'Synthetic Bedrock B workspace', exact: true }).waitFor()
  assert.equal(await page.locator('.server-detail').getAttribute('data-server-tab'), 'files')
  await navigation.getByRole('button', { name: 'App Settings', exact: true }).click()
  const appearance = await openAppearanceEditor(page)
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
  await screenshot(page, 'appearance-section-wide-150', { width: 1440, height: 900 }, appearance)
  await screenshot(page, 'appearance-section-narrow-150', { width: 390, height: 844 }, appearance)
  await closeAppearanceEditor(page)
  await screenshot(page, 'settings-wide-150', { width: 1440, height: 900 })
  await screenshot(page, 'settings-narrow-150', { width: 390, height: 844 })
  await page.setViewportSize({ width: 1440, height: 900 })
  await openAppearanceEditor(page)
  await appearance.getByRole('combobox', { name: /^Text size$/u }).selectOption('100')
  await appearance.getByRole('combobox', { name: /^Layout$/u }).selectOption('comfortable')
  await appearance.getByRole('combobox', { name: /^Theme$/u }).selectOption('dark')
  await appearance.getByRole('checkbox', { name: 'Stronger contrast and status outlines' }).uncheck()
  await closeAppearanceEditor(page)
  await page.setViewportSize({ width: 1440, height: 900 })
}
async function headerOverlayRegression(page, host) {
  // Augment only this browser's real disposable Host snapshot. Keep the candidate bundle,
  // staging instance, transport and collectors; never seed production data or replace the app.
  const snapshotRoute = `${host.origin}/api/local/snapshot`
  const syntheticEvents = Array.from({ length: 8 }, (_, index) => ({ id: randomUUID(), occurredUtc: new Date(Date.now() - index * 60_000).toISOString(),
    category: ['Lifecycle', 'Backup', 'Access', 'Network', 'Players', 'Maintenance', 'Recovery', 'Connections'][index],
    action: `BrowserLayout${index}`, severity: index % 2 ? 'Warning' : 'Info', profileId: null, deviceId: null, visibility: 'Host',
    message: `Synthetic browser-only layout row ${index + 1}. ${'Long notification detail wraps beside its glyph and remains readable without clipping. '.repeat(3)}` }))
  const handler = async route => {
    const response = await route.fetch()
    const state = await response.json()
    assert.equal(state.mode, 'Host', 'Header layout regression uses the disposable Host only.')
    state.activity = syntheticEvents
    state.settings.profiles = state.settings.profiles.map(profile => ({ ...profile,
      name: `${profile.name} long browser layout command destination`.slice(0, 64) }))
    await route.fulfill({ response, json: state })
  }
  const navigation = page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true })
  const notifications = page.locator('.notification-menu > summary')
  const appSettings = page.locator('.header-tools .app-settings-trigger')
  const palette = page.getByRole('dialog', { name: 'Command palette', exact: true, includeHidden: true })
  const viewports = [{ width: 1180, height: 774 }, { width: 1440, height: 900 }, { width: 641, height: 844 },
    { width: 700, height: 844 }, { width: 390, height: 844 }, { width: 900, height: 400 }]
  const box = async (locator, name, observeOnly = false) => {
    const metrics = await locator.evaluate(element => {
      const bounds = element.getBoundingClientRect(), style = getComputedStyle(element)
      const exposed = document.elementFromPoint(bounds.left + bounds.width / 2, bounds.top + bounds.height / 2)
      return { left: bounds.left, top: bounds.top, right: bounds.right, bottom: bounds.bottom, width: bounds.width, height: bounds.height,
        clientWidth: element.clientWidth, scrollWidth: element.scrollWidth, clientHeight: element.clientHeight, scrollHeight: element.scrollHeight,
        overflowY: style.overflowY, viewportWidth: window.innerWidth, viewportHeight: window.innerHeight,
        centerExposed: exposed === element || element.contains(exposed) }
    })
    report.layout.push({ name, overlay: metrics })
    if (observeOnly) return metrics
    assert(metrics.width > 0 && metrics.height > 0, `${name}: surface must have rendered geometry.`)
    assert(metrics.left >= -1 && metrics.right <= metrics.viewportWidth + 1 && metrics.top >= -1 && metrics.bottom <= metrics.viewportHeight + 1,
      `${name}: the complete overlay must stay inside the viewport.`)
    assert(metrics.scrollWidth <= metrics.clientWidth + 1, `${name}: overlay content must not overflow horizontally.`)
    assert(metrics.centerExposed, `${name}: the actual painted surface must not be clipped or covered by an ancestor.`)
    if (metrics.scrollHeight > metrics.clientHeight + 1) assert(['auto', 'scroll'].includes(metrics.overflowY), `${name}: overflow must remain scrollable.`)
    return metrics
  }
  await page.route(snapshotRoute, handler)
  try {
    if (baselineUiDir) {
      // The explicitly supplied historical bundle keeps its original rail.
      const baselineNavigation = page.getByRole('navigation', { name: 'TogetherServer workspaces', exact: true })
      const baselinePattern = `${host.origin}/**`
      report.baselineUi = { codeIdentity: 'a1eb74e', provenance: 'Caller-supplied built baseline; CI must build the named commit.',
        indexSha256: createHash('sha256').update(await readFile(path.join(baselineUiDir, 'index.html'))).digest('hex'),
        outcome: 'capturing-observations-only', servedFiles: [], screenshots: [] }
      const baselineAssets = async route => {
        const request = route.request(), url = new URL(request.url())
        if (request.method() !== 'GET' || url.origin !== host.origin ||
            !(url.pathname === '/' || url.pathname === '/index.html' || url.pathname.startsWith('/assets/'))) return route.fallback()
        const relative = decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname).replace(/^\/+/, '')
        const target = path.resolve(baselineUiDir, relative)
        assert(inside(baselineUiDir, target), 'Baseline assets must stay within their supplied built directory.')
        const resolved = await realpath(target)
        assert(inside(baselineUiDir, resolved) && (await lstat(target)).isFile(), 'Baseline assets must be ordinary files inside the built directory.')
        const body = await readFile(resolved)
        const contentType = ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml',
          '.png': 'image/png', '.woff2': 'font/woff2', '.json': 'application/json' })[path.extname(resolved).toLowerCase()] ?? 'application/octet-stream'
        if (!report.baselineUi.servedFiles.some(file => file.asset === relative)) report.baselineUi.servedFiles.push({ asset: relative,
          sha256: createHash('sha256').update(body).digest('hex') })
        await route.fulfill({ status: 200, body, contentType })
      }
      await page.route(baselinePattern, baselineAssets)
      try {
        await page.reload({ waitUntil: 'domcontentloaded' })
        await baselineNavigation.waitFor()
        await eventually(() => page.locator('.staging-banner').isVisible(), value => value, 'baseline uses the same actual staging banner')
        for (const textScale of ['100', '150']) {
          const viewport = { width: 1180, height: 774 }, name = `baseline-a1eb74e-notifications-1180x774-${textScale}`
          await page.setViewportSize(viewport)
          await baselineNavigation.getByRole('button', { name: 'Settings', exact: true }).click()
          await setAppearanceTextScale(page, textScale, { baseline: true })
          await eventually(() => page.evaluate(() => getComputedStyle(document.documentElement).fontSize),
            value => value === (textScale === '150' ? '24px' : '16px'), `baseline ${textScale} percent text`)
          await baselineNavigation.getByRole('button', { name: 'Host', exact: true }).click()
          await notifications.click()
          const panel = page.locator('.notification-panel')
          await panel.waitFor({ state: 'visible' })
          // Matching real screenshot/masking/report helper, with baseline assertions deliberately disabled.
          await screenshot(page, name, viewport, undefined, { assertLayout: false })
          await box(panel, name, true)
          const columns = await panel.locator('.notification-item').filter({ hasText: 'Synthetic browser-only layout row' }).evaluateAll(elements => elements.map(element => {
            const glyph = element.querySelector(':scope > span').getBoundingClientRect(), content = element.querySelector(':scope > div').getBoundingClientRect()
            return { firstColumn: getComputedStyle(element).gridTemplateColumns.split(' ')[0], glyphWidth: glyph.width, contentWidth: content.width,
              glyphRight: glyph.right, contentLeft: content.left, contentRight: content.right }
          }))
          report.layout.push({ name: `${name}-columns`, baselineObservationOnly: true, columns })
          report.baselineUi.screenshots.push(`${name}.png`)
          await notifications.click()
        }
        report.baselineUi.outcome = 'captured-not-validated'
      } finally { await page.unroute(baselinePattern, baselineAssets) }
    } else skip('matching baseline notification screenshots', 'No --baseline-ui-dir supplied; candidate screenshots do not establish a before/after comparison.')
    // APIs/snapshot fixture stayed unchanged. Restore candidate embedded assets before any assertion journey.
    await page.reload({ waitUntil: 'domcontentloaded' })
    await navigation.waitFor()
    assert.equal(await page.locator('.workspace-nav').count(), 0, 'Candidate navigation lives in the header without a global rail.')
    await eventually(() => page.locator('.staging-banner').isVisible(), value => value, 'actual staging banner')
    for (const textScale of ['100', '150']) {
      await page.setViewportSize({ width: 1440, height: 900 })
      await navigation.getByRole('button', { name: 'App Settings', exact: true }).click()
      await setAppearanceTextScale(page, textScale)
      await eventually(() => page.evaluate(() => getComputedStyle(document.documentElement).fontSize),
        value => value === (textScale === '150' ? '24px' : '16px'), `${textScale} percent overlay text`)
      await navigation.getByRole('button', { name: 'Servers', exact: true }).click()
      for (const viewport of viewports) {
        const suffix = `${viewport.width}x${viewport.height}-${textScale}`
        await page.setViewportSize(viewport)
        await notifications.click()
        const panel = page.locator('.notification-panel')
        await panel.waitFor({ state: 'visible' })
        await screenshot(page, `notifications-${suffix}`, viewport, panel)
        await box(panel, `notifications-${suffix}`)
        const rows = panel.locator('.notification-item').filter({ hasText: 'Synthetic browser-only layout row' })
        assert.equal(await rows.count(), 8, `${suffix}: all eight long fixture rows must render.`)
        const columns = await rows.evaluateAll(elements => elements.map(element => {
          const bounds = element.getBoundingClientRect(), glyph = element.querySelector(':scope > span').getBoundingClientRect(),
            content = element.querySelector(':scope > div').getBoundingClientRect()
          return { width: bounds.width, glyphWidth: glyph.width, firstColumn: getComputedStyle(element).gridTemplateColumns.split(' ')[0],
            contentWidth: content.width, glyphRight: glyph.right, contentLeft: content.left, contentRight: content.right, rowRight: bounds.right }
        }))
        report.layout.push({ name: `notification-columns-${suffix}`, columns })
        assert(columns.every(row => Number.parseFloat(row.firstColumn) <= 40 && row.glyphWidth <= 40 &&
          row.contentWidth >= Math.min(200, row.width - 80) && row.glyphRight <= row.contentLeft && row.contentRight <= row.rowRight + 1),
          `${suffix}: the real glyph column must stay narrow and reserve width for the notification text.`)
        // The header gear now opens the shared App Settings workspace. Moving to
        // that page must still close the notification overlay without hiding controls.
        await appSettings.click()
        await page.getByRole('region', { name: 'App Settings', exact: true }).waitFor({ state: 'visible' })
        assert.equal(await page.locator('.notification-menu').evaluate(element => element.open), false)
        assert.equal(await navigation.getByRole('button', { name: 'App Settings', exact: true }).getAttribute('aria-current'), 'page')
        await screenshot(page, `app-settings-${suffix}`, viewport)
        await notifications.click()
        await page.keyboard.press('Escape')
        assert.equal(await page.locator('.notification-menu').evaluate(element => element.open), false)
        assert.equal(await notifications.evaluate(element => element === document.activeElement), true)
        await notifications.click(); await page.mouse.click(3, viewport.height - 3)
        assert.equal(await page.locator('.notification-menu').evaluate(element => element.open), false, 'Outside press closes Notifications.')
        await notifications.click()
        await page.getByRole('button', { name: 'Commands', exact: true }).click()
        await palette.waitFor({ state: 'visible' })
        assert.equal(await page.locator('.notification-menu').evaluate(element => element.open), false, 'Opening Commands closes Notifications.')
        await closeCommandPalette(palette, () => page.keyboard.press('Escape'), 'Escape closes Commands')
        await page.getByRole('button', { name: 'Commands', exact: true }).click()
        const search = palette.getByRole('combobox', { name: 'Search commands', exact: true })
        await search.waitFor()
        const options = palette.getByRole('option'), count = await options.count()
        assert(count >= 32, 'The actual command palette must contain at least 32 destinations.')
        for (let index = 1; index < count; index++) await search.press('ArrowDown')
        const lastId = await options.last().getAttribute('id')
        await eventually(() => search.getAttribute('aria-activedescendant'), value => value === lastId, 'keyboard last command')
        await screenshot(page, `commands-last-${suffix}`, viewport)
        await box(palette, `commands-${suffix}`)
        const active = palette.locator('[role="option"][aria-selected="true"]')
        assert.equal(await active.count(), 1)
        const activeBounds = await active.boundingBox(), listBounds = await palette.locator('.command-list').boundingBox()
        report.layout.push({ name: `commands-selected-${suffix}`, activeBounds, listBounds })
        assert(activeBounds && listBounds && activeBounds.y >= listBounds.y - 1 && activeBounds.y + activeBounds.height <= listBounds.y + listBounds.height + 1,
          `${suffix}: the last keyboard-selected command must be inside the list scrollport.`)
        const close = palette.getByRole('button', { name: 'Close commands', exact: true })
        await box(close, `command-close-${suffix}`)
        await closeCommandPalette(palette, () => close.click(), 'Close commands button')
        if (viewport.width <= 700) {
          const mobileControls = await navigation.locator(':scope > .header-guides-trigger, :scope > .header-servers-trigger, :scope > .notification-menu > summary, :scope > .app-settings-trigger, :scope > .command-trigger').evaluateAll(elements => elements.map(element => {
            const bounds = element.getBoundingClientRect(), icon = element.querySelector('svg')?.getBoundingClientRect()
            return { name: element.getAttribute('aria-label')?.replace(/^Notifications.*$/u, 'Notifications'),
              width: bounds.width, height: bounds.height, iconVisible: !!icon && icon.width > 0 && icon.height > 0 }
          }))
          report.layout.push({ name: `mobile-navigation-${suffix}`, mobileControls })
          assert.deepEqual(mobileControls.map(control => control.name), ['Setup guides', 'Servers', 'Notifications', 'App Settings', 'Commands'])
          assert(mobileControls.every(control => control.iconVisible && control.width >= 44 && control.height >= 44),
            `${suffix}: header navigation retains five accessible names, painted icons and 44px touch targets.`)
        }
      }
    }
  } finally {
    await page.unroute(snapshotRoute, handler)
    await page.setViewportSize({ width: 1440, height: 900 })
    await page.reload({ waitUntil: 'domcontentloaded' })
    await navigation.waitFor()
    await navigation.getByRole('button', { name: 'App Settings', exact: true }).click()
    await setAppearanceTextScale(page, '100')
    await navigation.getByRole('button', { name: 'Servers', exact: true }).click()
  }
}
async function serverNavigation(page, profiles) {
  await selectServer(page, profiles.valheim.name, 'Logs')
  await selectServer(page, profiles.bedrock.name, 'Files')
  await page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true }).getByRole('button', { name: 'App Settings', exact: true }).click()
  await page.getByRole('region', { name: 'App Settings', exact: true }).waitFor({ state: 'visible' })
  await page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true }).getByRole('button', { name: 'Servers', exact: true }).click()
  await page.getByRole('region', { name: `${profiles.bedrock.name} workspace`, exact: true }).waitFor()
  assert.equal(await page.locator('.server-detail').getAttribute('data-server-tab'), 'files',
    'Header navigation must preserve the selected server and its open tab.')
  await page.reload({ waitUntil: 'domcontentloaded' })
  await page.getByRole('region', { name: `${profiles.bedrock.name} workspace`, exact: true }).waitFor()
  assert.equal(await page.locator('.server-detail').getAttribute('data-server-tab'), 'files',
    'Reload must restore the selected server and its saved tab.')
  await selectHostServer(page, profiles.valheim.name)
  await eventually(() => page.locator('.server-detail').getAttribute('data-server-tab'), value => value === 'overview',
    'Opening a directory row leads with its Overview')
  await page.reload({ waitUntil: 'domcontentloaded' })
  await page.getByRole('region', { name: `${profiles.valheim.name} workspace`, exact: true }).waitFor()
  await eventually(() => page.locator('.server-detail').getAttribute('data-server-tab'), value => value === 'logs',
    'The previous explicit tab choice remains saved for its own server')
  const directory = await openServerDirectory(page)
  const rows = directory.locator('.server-directory-row')
  const search = directory.getByRole('searchbox', { name: 'Find a server or friend', exact: true })
  assert.equal(await rows.count(), 3, 'All three saved local servers must be reachable in the directory.')
  await directory.getByRole('heading', { name: 'Your servers', exact: true }).waitFor()
  await directory.getByRole('heading', { name: 'Friend servers', exact: true }).waitFor()
  await search.fill('Bedrock')
  await eventually(() => rows.count(), count => count === 1, 'directory search filters by game')
  const bedrock = directory.getByRole('button', { name: `Open ${profiles.bedrock.name} on this PC`, exact: true })
  await bedrock.waitFor({ state: 'visible' })
  assert.equal(await page.getByRole('region', { name: `${profiles.valheim.name} workspace`, exact: true }).isVisible(), true,
    'Filtering the directory must preserve the selected workspace.')
  await search.fill('no-synthetic-server-matches')
  await directory.getByText('No matching servers.', { exact: true }).waitFor()
  assert.equal(await rows.count(), 0, 'An unmatched search must not show an unrelated server.')
  await search.fill(profiles.extra.name)
  await eventually(() => rows.count(), count => count === 1, 'directory search filters by server name')
  await directory.getByRole('button', { name: `Open ${profiles.extra.name} on this PC`, exact: true }).click()
  await page.getByRole('region', { name: `${profiles.extra.name} workspace`, exact: true }).waitFor()
  assert.equal(await page.getByRole('region', { name: `${profiles.valheim.name} workspace`, exact: true }).count(), 0,
    'Selecting a filtered row must replace the previous server workspace.')
  await search.fill('')
  await eventually(() => rows.count(), count => count === 3, 'cleared directory search restores every saved server')
  const order = await rows.evaluateAll(elements => elements.map(element => element.getAttribute('aria-label')))
  const sidebar = page.getByRole('complementary', { name: 'Server navigation', exact: true })
  const divider = page.getByRole('separator', { name: 'Server sidebar width', exact: true })
  await divider.waitFor({ state: 'visible' })
  assert.equal(await divider.getAttribute('aria-controls'), await sidebar.getAttribute('id'),
    'The resize separator must identify the actual server navigation pane.')
  await divider.press('Home')
  await divider.press('ArrowLeft')
  assert.equal(await divider.getAttribute('aria-valuenow'), '200', 'Keyboard resizing must respect the sidebar minimum.')
  await divider.press('Shift+ArrowRight')
  await divider.press('ArrowRight')
  await eventually(() => divider.getAttribute('aria-valuenow'), value => value === '250', 'shift and ordinary sidebar width increments')
  await eventually(() => sidebar.boundingBox(), bounds => bounds && Math.abs(bounds.width - 250) <= 1,
    'the painted sidebar matches its accessible resize value')
  await page.reload({ waitUntil: 'domcontentloaded' })
  await page.getByRole('region', { name: `${profiles.extra.name} workspace`, exact: true }).waitFor()
  await openServerDirectory(page)
  await divider.waitFor({ state: 'visible' })
  assert.equal(await divider.getAttribute('aria-valuenow'), '250', 'Reload must retain the owner-selected sidebar width.')
  await eventually(() => rows.evaluateAll(elements => elements.map(element => element.getAttribute('aria-label'))),
    value => JSON.stringify(value) === JSON.stringify(order), 'directory order remains stable after reload')
  assert.equal(await directory.locator('.server-directory-row[aria-current="true"]').count(), 1,
    'The directory must identify exactly one selected server after reload.')
  assert.equal(await directory.getByRole('button', { name: `Open ${profiles.extra.name} on this PC`, exact: true }).getAttribute('aria-current'), 'true')
  // The unified directory has no favorite/manual-order editor. Exercise the actual
  // named rows, search, canonical order and remembered selection instead.
  await divider.press('End')
  await divider.press('ArrowRight')
  assert.equal(await divider.getAttribute('aria-valuenow'), await divider.getAttribute('aria-valuemax'),
    'Keyboard resizing must respect the current available maximum.')
  await divider.dblclick()
  await eventually(() => divider.getAttribute('aria-valuenow'), value => value === '268', 'double-click resets the server sidebar width')
  await screenshot(page, 'host-wide-100', { width: 1440, height: 900 })
}
async function setupReview(page, host, profiles) {
  await selectServer(page, profiles.valheim.name)
  const addServer = (await openServerDirectory(page)).getByRole('button', { name: 'New server', exact: true })
  assert.equal(await addServer.count(), 1, 'The populated server directory exposes one canonical New server action.')
  await addServer.click()
  const dialog = page.getByRole('dialog', { name: 'Add new server' })
  await dialog.getByRole('heading', { name: 'Choose a game', exact: true }).waitFor()
  await dialog.getByText("Use settings from another server", { exact: true }).click()
  await dialog.getByRole('button', { name: `Use setup from ${profiles.valheim.name}`, exact: true }).click()
  await dialog.getByRole('heading', { name: 'Choose a world', exact: true }).waitFor()
  // The Game step has no current blockers. Reuse opens World, where the
  // deliberately missing new-world name and password must block Continue.
  const blockers = dialog.getByRole('region', { name: 'Setup blockers', exact: true })
  await blockers.waitFor({ state: 'visible' })
  assert(await blockers.locator('.setup-issue-list').getByRole('button').count() >= 2,
    'The World step must show its missing name and password blockers together.')
  assert.equal(await dialog.getByRole('button', { name: 'Continue', exact: true }).isDisabled(), true)
  const worldName = dialog.getByRole('textbox', { name: /^World name/u })
  // These implicit labels include helper text; the password label also contains its visibility checkbox.
  const gamePassword = dialog.getByLabel(/^Game password/u)
    .and(dialog.locator('input[id^="setup-"][id$="-game-password"]'))
  assert.equal(await worldName.inputValue(), '')
  assert.equal(await gamePassword.inputValue(), '')
  await worldName.fill('browser-new-world')
  const setupProfileId = (await worldName.getAttribute('id'))?.replace(/^setup-/u, '').replace(/-world-id$/u, '')
  assert(guidExpression.test(setupProfileId ?? ''), 'The setup world input must identify its actual reused draft profile.')
  await gamePassword.fill('fixture-pass-123')
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
  await acceptAppConfirmation(page, 'Finish setup later?', 'Finish later')
  await dialog.waitFor({ state: 'hidden' })
  await reloadProtectedSetup(page, host, { profileId: setupProfileId, worldId: 'browser-new-world', step: 'review' }, 'populated-setup-recovery')
  await page.getByRole('button', { name: 'Review saved setup', exact: true }).click()
  await page.getByRole('dialog', { name: 'Add new server' }).getByText('Review and start', { exact: true }).waitFor()
  await page.getByRole('dialog', { name: 'Add new server' }).getByRole('button', { name: 'Cancel', exact: true }).click()
  await acceptAppConfirmation(page, 'Discard setup changes?', 'Discard changes')
  await page.getByRole('dialog', { name: 'Add new server' }).waitFor({ state: 'hidden' })
}
async function recordGuidedEditorDom(page, phase) {
  const panels = page.locator('.server-detail .game-settings-panel')
  const panelCount = await panels.count()
  const workspace = page.locator('.server-detail')
  const workspaceCount = await workspace.count()
  const workspaceName = workspaceCount === 1 ? await workspace.evaluate(element => {
    const label = element.getAttribute('aria-label')
    if (label) return label
    return (element.getAttribute('aria-labelledby') ?? '').split(/\s+/u).filter(Boolean)
      .map(id => document.getElementById(id)?.textContent ?? '').join(' ')
  }) : null
  const selectedTabs = page.locator('[aria-label="Selected server sections"] button[aria-current="page"]')
  const selectedTabCount = await selectedTabs.count()
  const reloads = page.getByRole('button', { name: 'Reload game settings', exact: true })
  const reloadCount = await reloads.count()
  const reloadDomCount = await page.locator('.server-detail .game-settings-panel button')
    .filter({ hasText: /^Reload game settings$/u }).count()
  const fallbackText = 'Game settings could not be shown'
  const fallback = page.locator('.server-detail .pane-error[role="alert"]')
    .filter({ has: page.getByText(fallbackText, { exact: true }) })
  const fallbackCount = await fallback.count()
  const panelAriaLabel = panelCount === 1 ? await panels.getAttribute('aria-label') : null
  const state = { phase, panelCount,
    panelAriaLabel: panelAriaLabel === null ? null : redact(panelAriaLabel).slice(0, 120),
    panelVisible: panelCount === 1 ? await panels.isVisible() : false,
    panelAriaHidden: panelCount === 1 ? await panels.getAttribute('aria-hidden') : null,
    panelHiddenAncestor: panelCount === 1 ? await panels.evaluate(element => !!element.closest('[aria-hidden="true"], [inert]')) : false,
    regionCount: await page.getByRole('region', { name: 'Simple game settings', exact: true }).count(),
    reloadDomCount, reloadCount, reloadVisible: reloadCount === 1 ? await reloads.isVisible() : false,
    fallbackCount, fallbackText: fallbackCount > 0 ? fallbackText : null,
    workspaceCount, workspaceName: workspaceName === null ? null : redact(workspaceName).slice(0, 120),
    selectedTabCount, selectedTab: selectedTabCount === 1 ? redact(await selectedTabs.innerText()).slice(0, 40) : null,
    workspaceTab: workspaceCount === 1 ? await workspace.getAttribute('data-server-tab') : null }
  report.guidedEditorDom.push(state)
  return state
}
async function editorRecovery(page, host, profiles) {
  await selectServer(page, profiles.bedrock.name, 'Files')
  const settings = page.getByRole('region', { name: 'Simple game settings', exact: true })
  await recordGuidedEditorDom(page, 'initial')
  try {
    await settings.waitFor({ state: 'visible' })
  } catch (error) {
    await recordGuidedEditorDom(page, 'initial-failed')
    throw new Error(`The initial guided settings region was not visible; inspect guidedEditorDom. ${redact(error.message)}`, { cause: error })
  }
  await recordGuidedEditorDom(page, 'initial-visible')
  const files = page.getByRole('region', { name: 'Server files and settings' })
  const fileSections = page.getByRole('tablist', { name: 'Settings & files sections', exact: true })
  await fileSections.getByRole('tab', { name: 'Server files', exact: true }).click()
  const row = files.locator('.server-file-row').filter({ hasText: 'server.properties' })
  await row.getByRole('button', { name: 'Edit file', exact: true }).click()
  const contents = files.getByRole('textbox', { name: /^File contents/u })
  const raw = profiles.properties + '# QoL browser raw draft\n'
  await contents.fill(raw)
  await selectWorkspaceSection(page, 'Selected server sections', 'Logs', 'logs')
  const stored = await api(host, '/api/local/ui-drafts/read', 'POST', { purpose: 'file', profileId: profiles.bedrock.id,
    connectionId: null, key: 'file:server-properties' })
  assert.equal(stored.ok, true)
  assert(stored.text?.includes('QoL browser raw draft'), 'Immediate navigation must flush the last raw keystroke through the real protected store.')
  assert.equal(await readFile(path.join(profiles.bedrock.worldDirectory, 'server.properties'), 'utf8'), profiles.properties)
  await selectServer(page, profiles.bedrock.name, 'Files')
  await fileSections.getByRole('tab', { name: 'Server files', exact: true }).click()
  await row.getByRole('button', { name: 'Edit file', exact: true }).click()
  await files.getByRole('button', { name: 'Review recovered draft', exact: true }).click()
  assert.equal(await contents.inputValue(), raw)
  await files.getByRole('button', { name: 'Review file changes', exact: true }).click()
  await files.getByRole('region', { name: 'Review raw file changes' }).getByText(/QoL browser raw draft/u).waitFor()
  await files.getByRole('button', { name: 'Discard unsaved file edits', exact: true }).click()
  await acceptAppConfirmation(page, 'Discard file edits?', 'Discard file edits')
  await fileSections.getByRole('tab', { name: 'Game settings', exact: true }).click()
  await recordGuidedEditorDom(page, 'before-guided')
  const difficulty = settings.getByRole('combobox', { name: /^Difficulty/u })
  const guided = await api(host, `/api/local/profiles/${profiles.bedrock.id}/game-settings`)
  const settingKeys = guided?.settings && typeof guided.settings === 'object' && !Array.isArray(guided.settings)
    ? Object.keys(guided.settings).sort() : []
  const guidedSummary = `ok=${guided?.ok === true}, code=${redact(guided?.code)}, kind=${redact(guided?.kind)}, ` +
    `viewKeys=${redact(Object.keys(guided ?? {}).sort().join(','))}, settingKeys=${redact(settingKeys.join(','))}, ` +
    `settingCount=${settingKeys.length}, listCount=${Array.isArray(guided?.lists) ? guided.lists.length : -1}, ` +
    `shaPresent=${typeof guided?.sha256 === 'string'}, message=${redact(guided?.message).replace(/\b[a-f\d]{64}\b/giu, '[hash]')}`
  try {
    assert.equal(guided?.ok, true, guidedSummary)
    assert.equal(guided?.code, 'GameSettingsReady', guidedSummary)
    assert.equal(guided?.kind, 'MinecraftBedrock', guidedSummary)
    assert.equal(guided?.settings?.difficulty, 'normal', guidedSummary)
    assert((await readFile(path.join(profiles.bedrock.worldDirectory, 'server.properties'), 'utf8')) === profiles.properties,
      'Discard and the guided read must leave the original synthetic file unchanged.')
    await difficulty.selectOption('hard')
  } catch (error) {
    await recordGuidedEditorDom(page, 'guided-failed')
    const regionCount = await settings.count()
    const notices = regionCount === 1
      ? await settings.locator(':scope > .notice, :scope > .helper-text').allTextContents() : []
    const notice = redact(notices.join(' ').replace(/\b[a-f\d]{64}\b/giu, '[hash]'))
    const selectCount = regionCount === 1 ? await settings.locator('select').count() : 0
    const difficultyCount = regionCount === 1 ? await difficulty.count() : 0
    throw new Error(`Guided settings precondition/control failed; inspect guidedEditorDom: ${guidedSummary}; ` +
      `regions=${regionCount}, selects=${selectCount}, difficultyControls=${difficultyCount}, notice=${notice}; ${redact(error.message)}`, { cause: error })
  }
  await settings.getByLabel('Search game settings', { exact: true }).fill('players')
  assert.equal(await difficulty.count(), 0)
  await settings.getByLabel('Search game settings', { exact: true }).fill('')
  assert.equal(await difficulty.inputValue(), 'hard')
  await selectWorkspaceSection(page, 'Selected server sections', 'Overview', 'overview')
  await selectServer(page, profiles.bedrock.name, 'Files')
  await settings.getByRole('button', { name: 'Review recovered draft', exact: true }).click()
  assert.equal(await difficulty.inputValue(), 'hard')
  await settings.getByRole('button', { name: 'Review settings changes', exact: true }).click()
  await settings.getByRole('region', { name: 'Review exact changes' }).waitFor()
  await screenshot(page, 'editor-recovery-wide', { width: 1440, height: 900 })
  await settings.getByRole('button', { name: 'Discard unsaved settings', exact: true }).click()
  await acceptAppConfirmation(page, 'Discard unsaved settings?', 'Discard settings')
  assert.equal(await readFile(path.join(profiles.bedrock.worldDirectory, 'server.properties'), 'utf8'), profiles.properties)
}
async function lifecycleLogsAndSessions(page, host, profiles) {
  await selectServer(page, profiles.valheim.name)
  const workspace = page.getByRole('region', { name: `${profiles.valheim.name} workspace`, exact: true })
  const observation = { phase: 'initial-host-start', httpStatus: null, ok: null, code: 'Unread',
    responseElapsedMs: null, readinessElapsedMs: null, readinessPolls: 0, snapshotReadFailures: 0,
    matchingRunCount: null, lastState: 'Unobserved', processRecorded: false, operationRecorded: false }
  report.lifecycleStarts.push(observation)
  const startBegan = Date.now()
  let clickedAt = startBegan
  const [response] = await Promise.all([
    page.waitForResponse(value => value.url() === `${host.origin}/api/local/profiles/${profiles.valheim.id}/start` &&
      value.request().method() === 'POST'),
    workspace.getByRole('button', { name: 'Start server', exact: true }).first().click().then(() => { clickedAt = Date.now() })
  ])
  observation.httpStatus = response.status()
  const started = await response.json()
  observation.responseElapsedMs = Date.now() - startBegan
  observation.ok = started?.ok === true
  observation.code = typeof started?.code === 'string' && /^[A-Za-z][A-Za-z0-9]{0,95}$/u.test(started.code)
    ? started.code : 'Unclassified'
  assert(response.status() === 200 && started?.ok === true,
    `Synthetic Valheim Start refused: HTTP ${observation.httpStatus}, ${observation.code}.`)
  try {
    await eventually(async () => {
      observation.readinessPolls++
      let snapshot
      try { snapshot = await api(host, '/api/local/snapshot') }
      catch (error) { observation.snapshotReadFailures++; throw error }
      const matches = snapshot.runs.filter(run => run.profileId === profiles.valheim.id)
      observation.matchingRunCount = matches.length
      const run = matches.length === 1 ? matches[0] : null
      observation.lastState = run && typeof run.state === 'string' && /^[A-Za-z ]{1,48}$/u.test(run.state)
        ? run.state : matches.length === 0 ? 'Missing' : 'Unclassified'
      observation.processRecorded = Number.isInteger(run?.processId) && run.processId > 0
      observation.operationRecorded = typeof run?.operationId === 'string' && /^[\da-f-]{36}$/iu.test(run.operationId)
      return snapshot
    }, value => value.runs.some(run => run.profileId === profiles.valheim.id && run.state === 'Ready'),
    'synthetic Valheim readiness', Math.max(0, 30_000 - (Date.now() - clickedAt)))
  } finally { observation.readinessElapsedMs = Date.now() - clickedAt }
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
  assert((await api(host, '/api/local/snapshot')).runs.some(value => value.profileId === profiles.valheim.id && value.state === 'Ready'),
    'Opening owner Stop review must not stop the disposable server before confirmation.')
  await acceptAppConfirmation(page, `Stop ${profiles.valheim.name}?`, 'Stop server')
  await eventually(() => api(host, '/api/local/snapshot'), value => value.runs.some(value => value.profileId === profiles.valheim.id && value.state === 'Offline'), 'exact synthetic Stop', 45_000)
  // API completion can precede the shell's next three-second snapshot. Restore
  // must keep its offline guard until the selected workspace observes that state.
  await eventually(() => workspace.locator('.server-current-state .status').innerText(),
    value => value === 'Offline', 'selected Host observes completed Stop before Restore review')
  await selectServer(page, profiles.valheim.name, 'Sessions')
  const weekly = page.locator('.weekly-summary')
  const completed = weekly.getByRole('button', { name: /^Ended sessions:/u })
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
  await first.getByText('Name and pin backup', { exact: true }).click()
  await first.getByLabel('Backup name', { exact: true }).fill('Browser checkpoint')
  await first.getByRole('checkbox', { name: 'Pin this backup', exact: true }).check()
  const pinnedCatalog = page.waitForResponse(response => response.request().method() === 'GET' &&
    response.url() === `${host.origin}/api/local/profiles/${profiles.valheim.id}/backup-catalog`)
  await Promise.all([pinnedCatalog, first.getByRole('button', { name: 'Save name and pin', exact: true }).click()])
  first = catalog.locator('.backup-bookmark').filter({ hasText: 'Browser checkpoint' })
  await first.getByText('Pinned', { exact: true }).waitFor()
  await catalog.getByLabel('Search backups', { exact: true }).fill('Browser checkpoint')
  assert.equal(await catalog.locator('.backup-bookmark').count(), 1)
  await catalog.getByRole('button', { name: /^Filter backups/u }).click()
  await catalog.getByRole('combobox', { name: /^Pins$/u }).selectOption('pinned')
  assert.equal(await catalog.locator('.backup-bookmark').count(), 1)
  await catalog.getByRole('button', { name: 'Clear filters', exact: true }).click()
  await catalog.getByText('Preview which backups are kept', { exact: true }).click()
  await catalog.getByLabel('Preview unpinned copies to keep', { exact: true }).fill('1')
  await catalog.getByText(/This preview does not delete anything\./u).waitFor()
  assert((await api(host, '/api/local/snapshot')).runs.some(run => run.profileId === profiles.valheim.id && run.state === 'Offline'),
    'Restore-cancellation sentinels are written only while the disposable fixture is Offline.')
  const currentDb = 'Current synthetic DB after backups; cancel must preserve this.'
  const currentFwl = 'Current synthetic FWL after backups; cancel must preserve this.'
  await writeFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.db'), currentDb)
  await writeFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.fwl'), currentFwl)
  // The manual-backup API above can be observed as a temporary world-copy
  // reservation by the shell. Wait for its current Offline evidence as well.
  const workspace = page.getByRole('region', { name: `${profiles.valheim.name} workspace`, exact: true })
  await eventually(() => workspace.locator('.server-current-state .status').innerText(),
    value => value === 'Offline', 'selected Host observes completed backup before Restore review')
  await first.getByRole('button', { name: 'Review backup', exact: true }).click()
  await first.getByRole('button', { name: 'Review Restore', exact: true }).click()
  const review = catalog.getByRole('region', { name: 'Review Restore', exact: true })
  const restore = review.getByRole('button', { name: 'Restore reviewed backup', exact: true })
  assert.equal(await restore.isDisabled(), true, 'An Offline server still requires explicit Restore review confirmation.')
  await review.getByRole('checkbox').check()
  assert.equal(await restore.isEnabled(), true, 'Current Offline evidence and explicit confirmation enable the reviewed Restore.')
  await screenshot(page, 'backup-restore-review', { width: 1440, height: 900 })
  await review.getByRole('button', { name: 'Cancel Restore', exact: true }).click()
  // Cancelling review cannot change either synthetic world file.
  assert.equal(await readFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.db'), 'utf8'), currentDb)
  assert.equal(await readFile(path.join(profiles.valheim.worldDirectory, 'fixture-world.fwl'), 'utf8'), currentFwl)
  // Verify refreshes the catalog, then the owning workspace reloads its backup facts.
  // The latter remounts each disclosure, so reopen Review on the canonical refreshed row.
  let verificationCatalogReads = 0
  const refreshedEvidence = page.waitForResponse(response => response.request().method() === 'GET' &&
    response.url() === `${host.origin}/api/local/profiles/${profiles.valheim.id}/backup-catalog` &&
    ++verificationCatalogReads === 2)
  await Promise.all([refreshedEvidence, catalog.getByRole('button', { name: 'Verify', exact: true }).first().click()])
  const verified = catalog.locator('.backup-bookmark').filter({ hasText: 'Browser checkpoint' })
  await verified.getByText('Files matched when checked. The world still needs testing in the game.', { exact: true }).waitFor()
  await verified.getByRole('button', { name: 'Review backup', exact: true }).click()
  await catalog.getByLabel('Recorded protection results').locator('small').filter({ hasText: /Backup file check: Passed/u }).first().waitFor()
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
  await page.locator('form.join-row').filter({ has: page.getByLabel('Server code', { exact: true }) })
    .getByRole('button', { name: 'Connect', exact: true }).click()
  const paired = await eventually(() => api(friend, '/api/local/snapshot'), value => value.state === 'Connected' && value.profiles.some(profile => profile.id === profiles.valheim.id), 'pinned loopback Friend pairing')
  assert(guidExpression.test(paired.connectionId) && guidExpression.test(paired.hostId), 'Pairing must identify the actual saved connection and pinned Host.')
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
  const directory = await openServerDirectory(page)
  const owner = paired.hostProfileName?.trim() || paired.connectionName
  assert(owner, 'The paired Host must supply the directory owner label for this saved connection.')
  const friendRows = directory.locator('.server-directory-host .server-directory-row')
  await eventually(() => friendRows.count(), count => count === 2, 'the two currently assigned Friend directory rows')
  const bedrockRow = directory.getByRole('button', { name: `Open ${profiles.bedrock.name} on ${owner}`, exact: true })
  const valheimRow = directory.getByRole('button', { name: `Open ${profiles.valheim.name} on ${owner}`, exact: true })
  await bedrockRow.click()
  await selectWorkspaceSection(page, 'Selected server sections', 'Play', 'play')
  await second.waitFor()
  assert.equal(await second.getByRole('button', { name: 'Start server', exact: true }).isDisabled(), true)
  assert.equal(await bedrockRow.getAttribute('aria-current'), 'true', 'Friend permissions must belong to the selected directory row.')
  assert.equal(await first.count(), 0, 'Join renders only the selected assigned server.')
  await valheimRow.click()
  await first.waitFor({ state: 'visible' })
  assert.equal(await valheimRow.getAttribute('aria-current'), 'true')
  assert.equal(await second.count(), 0, 'Selecting another Friend row must unmount the previous server card.')
  const play = first.getByRole('region', { name: 'Play', exact: true })
  await play.getByRole('button', { name: 'Start server', exact: true }).click()
  await eventually(() => api(host, '/api/local/snapshot'), value => value.runs.some(run => run.profileId === profiles.valheim.id && run.state === 'Ready'), 'Friend UI starts the actual synthetic driver')
  await rememberManagedProcesses(host)
  await api(friend, '/api/local/friend/poll', 'POST')
  const readiness = play.getByRole('paragraph').filter({ hasText: /^The Host reports readiness\./u })
  await readiness.waitFor({ state: 'visible' })
  assert.equal(await readiness.count(), 1, 'The selected Friend Play flow must expose one current readiness explanation.')
  assert((await readiness.innerText()).includes('not proof that a player joined'),
    'Host readiness must retain its explicit real-game join boundary.')
  assert.equal(await first.getByRole('button', { name: 'Stop server', exact: true }).count(), 0)
  assert.equal(await first.getByRole('button', { name: 'View logs', exact: true }).count(), 0)
  assert.equal(await page.getByRole('navigation', { name: 'Selected server sections', exact: true }).getByRole('button', { name: 'Logs', exact: true }).count(), 0,
    'The selected server has no log section without its separate View logs grant.')
  assert.equal(await page.getByRole('combobox', { name: 'Selected server sections', exact: true, includeHidden: true }).locator('option[value="logs"]').count(), 0,
    'Mobile section navigation must enforce the same View logs presentation.')
  await play.locator('summary[aria-label="Server tools and connection help"]').click()
  await play.getByRole('button', { name: 'Open Connection help', exact: true }).click()
  const doctor = page.locator('.friend-connection-tools .friend-connection-doctor')
  await doctor.waitFor({ state: 'visible' })
  assert.equal(await doctor.evaluate(element => element.open), true, 'Play must open the Doctor in the Connection help section.')
  assert.equal(await doctor.locator(':scope > summary').evaluate(element => element === document.activeElement), true,
    'The Connection help summary receives keyboard focus.')
  // The OS/Open game control is only observed; this harness never invokes a native client.
  await selectWorkspaceSection(page, 'Selected server sections', 'Chat', 'chat')
  const chat = first.locator('.server-chat')
  const message = 'Synthetic browser message kept during navigation'
  await chat.getByLabel('Message', { exact: true }).fill(message)
  const navigation = page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true })
  const recoveryDiagnostics = { stage: 'leaving-compose', activeWorkspace: null, attentionReached: false,
    composerUnmounted: false, selectedConnectionMatches: false, selectedHostMatches: false, serverStillAssigned: false,
    protectedReadOk: false, persistedTextMatches: false, joinReached: false, recoveryPromptVisible: false,
    recoveredTextMatches: false, composerDomCount: null, recoveryButtonDomCount: null }
  report.chatRecovery.push(recoveryDiagnostics)
  try {
    await openAttention(page)
    // A click returns before protected flush completes. Confirm navigation before
    // issuing Join so its new navigation epoch cannot supersede pending Attention.
    await eventually(() => navigation.getByRole('button', { name: 'Open Attention Center', exact: true, includeHidden: true }).getAttribute('aria-current'),
      value => value === 'page', 'Attention navigation completes after chat draft flush')
    recoveryDiagnostics.attentionReached = true
    await eventually(() => chat.count(), value => value === 0, 'Friend composer unmounted on Attention')
    recoveryDiagnostics.composerUnmounted = true
    recoveryDiagnostics.stage = 'protected-draft'
    const savedScope = await api(friend, '/api/local/snapshot')
    recoveryDiagnostics.selectedConnectionMatches = savedScope.connectionId === paired.connectionId
    recoveryDiagnostics.selectedHostMatches = savedScope.hostId === paired.hostId
    recoveryDiagnostics.serverStillAssigned = savedScope.profiles.some(profile => profile.id === profiles.valheim.id)
    assert(recoveryDiagnostics.selectedConnectionMatches && recoveryDiagnostics.selectedHostMatches && recoveryDiagnostics.serverStillAssigned,
      'Draft recovery must retain the selected saved Host and assigned room.')
    const stored = await api(friend, '/api/local/ui-drafts/read', 'POST', { purpose: 'chat',
      profileId: profiles.valheim.id, connectionId: paired.connectionId, key: 'compose' })
    recoveryDiagnostics.protectedReadOk = stored.ok === true
    recoveryDiagnostics.persistedTextMatches = stored.text === message
    assert(recoveryDiagnostics.protectedReadOk && recoveryDiagnostics.persistedTextMatches,
      'Completed navigation must protect the exact unfinished message in its selected connection/server scope.')
    recoveryDiagnostics.stage = 'returning-to-compose'
    await navigation.getByRole('button', { name: 'Servers', exact: true }).click()
    await eventually(() => navigation.getByRole('button', { name: 'Servers', exact: true }).getAttribute('aria-current'),
      value => value === 'page', 'Join navigation completes before chat recovery')
    recoveryDiagnostics.joinReached = true
    await chat.waitFor({ state: 'visible' })
    const recover = chat.getByRole('button', { name: 'Use saved draft', exact: true })
    await recover.waitFor({ state: 'visible' })
    recoveryDiagnostics.recoveryPromptVisible = true
    await recover.click()
    recoveryDiagnostics.recoveredTextMatches = await chat.getByLabel('Message', { exact: true }).inputValue() === message
    assert(recoveryDiagnostics.recoveredTextMatches, 'Review must restore the exact protected message into the composer.')
    recoveryDiagnostics.stage = 'recovered'
  } finally {
    recoveryDiagnostics.activeWorkspace = await navigation.locator('button[aria-current="page"]').evaluate(element => element.getAttribute('aria-label') || element.textContent.trim()).catch(() => null)
    recoveryDiagnostics.composerDomCount = await chat.count().catch(() => null)
    recoveryDiagnostics.recoveryButtonDomCount = await chat.getByRole('button', { name: 'Use saved draft', exact: true }).count().catch(() => null)
  }
  const selected = await api(friend, '/api/local/snapshot')
  assert.equal(selected.connectionId, paired.connectionId, 'Recovered compose must stay on the selected saved Host.')
  assert.equal(selected.hostId, paired.hostId, 'Recovered compose must retain the paired Host identity.')
  assert(selected.profiles.some(profile => profile.id === profiles.valheim.id), 'The recovered room must remain assigned to this Friend.')
  const hostRoomRoute = `/api/local/profiles/${profiles.valheim.id}/chat`
  // The existing read-only room GET returns the selected saved link's cache.
  // Scope is checked in its payload and the selected snapshot on every receipt poll.
  const friendRoomRoute = `/api/local/friend/${profiles.valheim.id}/chat`
  const scopedFriendRoomRoute = `/api/local/friend/connections/${paired.connectionId}/servers/${profiles.valheim.id}/chat`
  const hostRoomBefore = await api(host, hostRoomRoute)
  assert.equal(hostRoomBefore.ok, true)
  assert.equal(hostRoomBefore.hostId, paired.hostId, 'Delivery must target the actual paired Host room.')
  assert.equal(hostRoomBefore.profileId, profiles.valheim.id)
  assert(!hostRoomBefore.entries.some(entry => entry.text === message), 'This journey must prove a new delivery, not an older matching message.')
  const signedMessage = chat.getByRole('log', { name: 'Messages', exact: true })
    .locator('.server-chat-message:not(.pending)').filter({ has: page.getByText(message, { exact: true }) })
  const code = value => typeof value === 'string' && /^[A-Za-z][A-Za-z0-9]{0,63}$/u.test(value) ? value : null
  const diagnostics = { stage: 'automatic-delivery', postStatus: null, postCode: null, postPendingCount: null,
    friendState: redact(selected.state), selectedScopeMatches: true, hostCode: null, friendCode: null,
    hostScopeMatches: false, friendScopeMatches: false, hostMatchCount: 0, friendMatchCount: 0,
    friendPendingCount: null, signedUiCount: 0, pendingUiCount: 0, receiptMatches: false, delivered: false }
  report.chatDelivery.push(diagnostics)
  const postUrl = new URL(`${scopedFriendRoomRoute}/messages`, friend.origin).href
  try {
    // Observe the real UI Send receipt. Visible text can still be compose or a local pending entry.
    const [response] = await Promise.all([
      page.waitForResponse(value => value.url() === postUrl && value.request().method() === 'POST'),
      chat.getByRole('button', { name: 'Send', exact: true }).click()
    ])
    diagnostics.postStatus = response.status()
    const posted = await boundedResponseJson(response, 768 * 1024)
    diagnostics.postCode = code(posted?.code)
    diagnostics.postPendingCount = Array.isArray(posted?.pending) ? posted.pending.length : null
    assert.equal(response.status(), 200, 'The selected room Send must return an actual local receipt.')
    assert.equal(posted?.ok, true, 'The selected room must accept the browser message.')
    assert.equal(posted.hostId, paired.hostId)
    assert.equal(posted.profileId, profiles.valheim.id)
    assert.equal(response.request().postDataJSON().text, message, 'The observed Send must contain the recovered compose text.')
    const matchesMessage = entry => entry.text === message && entry.hostId === paired.hostId &&
      entry.profileId === profiles.valheim.id && entry.authorId === devices[0].id &&
      guidExpression.test(entry.id) && typeof entry.signature === 'string' && entry.signature.length > 0
    // Do not invoke Sync now here: automatic Send/background sync must independently deliver.
    await eventually(async () => {
      const [hostRoom, friendRoom, current, signedUiCount, pendingUiCount] = await Promise.all([
        api(host, hostRoomRoute), api(friend, friendRoomRoute), api(friend, '/api/local/snapshot'), signedMessage.count(),
        chat.locator('.server-chat-messages .server-chat-message.pending').filter({ has: page.getByText(message, { exact: true }) }).count()
      ])
      diagnostics.friendState = redact(current.state)
      diagnostics.selectedScopeMatches = current.connectionId === paired.connectionId && current.hostId === paired.hostId &&
        current.profiles.some(profile => profile.id === profiles.valheim.id)
      diagnostics.hostCode = code(hostRoom.code)
      diagnostics.friendCode = code(friendRoom.code)
      diagnostics.hostScopeMatches = hostRoom.ok === true && hostRoom.hostId === paired.hostId && hostRoom.profileId === profiles.valheim.id
      diagnostics.friendScopeMatches = friendRoom.ok === true && friendRoom.hostId === paired.hostId && friendRoom.profileId === profiles.valheim.id
      const hostEntries = Array.isArray(hostRoom.entries) ? hostRoom.entries.filter(matchesMessage) : []
      const friendEntries = Array.isArray(friendRoom.entries) ? friendRoom.entries.filter(matchesMessage) : []
      diagnostics.hostMatchCount = hostEntries.length
      diagnostics.friendMatchCount = friendEntries.length
      diagnostics.friendPendingCount = Array.isArray(friendRoom.pending) ? friendRoom.pending.length : null
      diagnostics.signedUiCount = signedUiCount
      diagnostics.pendingUiCount = pendingUiCount
      diagnostics.receiptMatches = hostEntries.length === 1 && friendEntries.length === 1 &&
        hostEntries[0].id === friendEntries[0].id && hostEntries[0].signature === friendEntries[0].signature
      return diagnostics.selectedScopeMatches && diagnostics.hostScopeMatches && diagnostics.friendScopeMatches && diagnostics.receiptMatches &&
        signedUiCount === 1 && pendingUiCount === 0 && !friendRoom.pending.some(entry => entry.text === message)
    }, delivered => delivered, 'automatic signed Host delivery and selected Friend UI receipt')
    await signedMessage.waitFor({ state: 'visible' })
    assert.equal(await signedMessage.count(), 1, 'Exactly one non-pending signed UI entry must confirm automatic delivery.')
    const afterDelivery = await api(friend, '/api/local/snapshot')
    diagnostics.selectedScopeMatches = afterDelivery.connectionId === paired.connectionId && afterDelivery.hostId === paired.hostId &&
      afterDelivery.profiles.some(profile => profile.id === profiles.valheim.id)
    diagnostics.friendState = redact(afterDelivery.state)
    assert.equal(diagnostics.selectedScopeMatches, true, 'Delivery must preserve the exact selected Host and server assignment.')
    diagnostics.delivered = true
  } catch (error) {
    throw new Error(`Automatic signed chat delivery failed; inspect chatDelivery. ${redact(error.message)}`, { cause: error })
  }
  const chatViewport = page.viewportSize() ?? { width: 1440, height: 900 }
  await screenshot(page, 'friend-chat-confirmed-narrow', { width: 390, height: 844 }, signedMessage)
  await page.setViewportSize(chatViewport)
  await selectWorkspaceSection(page, 'Selected server sections', 'Settings', 'connection')
  await page.getByRole('button', { name: 'Add another Host', exact: true }).click()
  await page.getByRole('button', { name: 'Cancel and return to saved Host', exact: true }).click()
  await first.getByRole('region', { name: 'Play', exact: true }).waitFor()
  await screenshot(page, 'friend-play-wide', { width: 1440, height: 900 }, play)
  await screenshot(page, 'friend-play-narrow', { width: 390, height: 844 }, play)
}
async function attentionAndLargeText(page) {
  const navigation = page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true })
  await openAttention(page)
  const attention = page.locator('.attention-workspace')
  const history = attention.locator('.attention-history-disclosure')
  await history.waitFor({ state: 'visible' })
  if (!await history.evaluate(element => element.open)) await history.locator(':scope > summary').click()
  await attention.getByRole('combobox', { name: /^History time/u }).selectOption('day')
  await attention.getByRole('searchbox', { name: 'Search activity', exact: true }).fill('Synthetic')
  await attention.getByRole('button', { name: 'Mark read', exact: true }).first().click()
  await attention.getByText('Marked read.', { exact: true }).waitFor()
  await attention.getByRole('button', { name: 'Dismiss notice', exact: true }).first().click()
  await screenshot(page, 'attention-narrow', { width: 390, height: 844 })
  await navigation.getByRole('button', { name: 'App Settings', exact: true }).click()
  await setAppearanceTextScale(page, '150')
  await navigation.getByRole('button', { name: 'Servers', exact: true }).click()
  await page.setViewportSize({ width: 1440, height: 900 })
  const directory = await openServerDirectory(page)
  await directory.locator('.server-directory-row').first().click()
  const selectedDetailHeader = page.locator('.server-detail-card > .server-command-header > .profile-top')
  await screenshot(page, 'host-narrow-150', { width: 390, height: 844 }, selectedDetailHeader)
  assert.equal(await page.getByRole('separator', { name: 'Server sidebar width', exact: true, includeHidden: true }).isVisible(), false,
    'Narrow stacked navigation must hide the desktop resize divider.')
  await screenshot(page, 'host-tablet-150', { width: 768, height: 1024 }, selectedDetailHeader)
  assert.equal(await page.getByRole('separator', { name: 'Server sidebar width', exact: true }).isVisible(), true,
    'The tablet workspace must retain its accessible resize divider.')
  assert(report.layout.filter(layout => 'liveStatusCount' in layout).every(layout => layout.liveStatusCount > 0),
    'Each screenshot surface should retain accessible status announcements; geometry-only records are separate evidence.')
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
  checkModeTransitionCorrelation()
  const { chromium } = await import('playwright')
  const host = await startInstance('host')
  // No Playwright browser download. The separate Windows runner supplies Microsoft Edge.
  browser = await chromium.launch({ channel: 'msedge', headless: true })
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: 'dark' })
  const hostPage = await openPage(host, context)
  await step('real first-use Host choices, setup cancel and protected nonsecret setup recovery at desktop/mobile text sizes',
    () => firstUseAndSetupRecovery(hostPage, host))
  const profiles = await seedProfiles(host)
  await hostPage.reload({ waitUntil: 'domcontentloaded' })
  await hostPage.getByRole('complementary', { name: 'Server navigation', exact: true }).getByRole('region', { name: 'Servers', exact: true }).waitFor()
  await step('workspace keyboard, command palette and appearance preferences', () => routingAndAppearance(hostPage, profiles))
  await step('staging notifications, shared App Settings, keyboard commands and accessible mobile header navigation at viewport boundaries', () => headerOverlayRegression(hostPage, host))
  await step('unified server directory search, selected rows, remembered tabs and sidebar width across reload', () => serverNavigation(hostPage, profiles))
  await step('setup blockers, nonsecret reuse, applied port suggestion and paused-step recovery', () => setupReview(hostPage, host, profiles))
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
