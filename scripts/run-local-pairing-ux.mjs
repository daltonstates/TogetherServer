// Invoked only by the guarded lab harness, after assignment to its Windows Job.
// One stdin line is the launch barrier. No UI response is mocked or replaced.
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs/promises';
import readline from 'node:readline';
import { createHash } from 'node:crypto';
import { guidedHostingStage, roleTransitionRequestFacts, roleTransitionPayloadFacts, roleTransitionBodyFacts,
  classifyRoleTransitionRefusal } from './local-pairing-role-transition.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(path.join(repo, 'ui', 'package.json'));
const lines = readline.createInterface({ input: process.stdin, terminal: false });
const input = await new Promise((resolve, reject) => {
  lines.once('line', line => { try { resolve(JSON.parse(line)); } catch { reject(new Error('Invalid launch contract')); } });
  lines.once('close', () => reject(new Error('Missing launch barrier')));
});
lines.close();
if (!process.argv.includes('--allow-windowless-ux') || input.authorization !== 'owner-approved-local-windowless-ux'
  || input.runnerPid !== process.pid || input.parentPid !== process.ppid) throw new Error('Use the contained local UX launcher');
const runRoot = path.resolve(input.root);
const labRoot = path.join(repo, 'local-data', 'local-pairing-lab');
const relativeRoot = path.relative(labRoot, runRoot);
if (!relativeRoot || relativeRoot.startsWith('..') || path.isAbsolute(relativeRoot)) throw new Error('Invalid disposable root');
const ports = [input.hostPort, input.friendPort];
if (ports.some(port => !Number.isInteger(port) || port < 1024 || port > 65535) || ports[0] === ports[1]) throw new Error('Invalid local ports');
const origins = new Set(ports.map(port => `http://127.0.0.1:${port}`));
const artifacts = path.join(runRoot, 'ux');
await fs.mkdir(artifacts, { recursive: true });
const report = { ok: false, stages: [], errors: [], failedRequests: [], canceledRequests: [], controlPolicyChanges: [], boundaries: [
  'Actual isolated app backends; UI mutations only; no response substitution.',
  'Synthetic fixture health remains Preparing, with unknown players.',
  'No real game, WAN, installer, native dialog or game-save acceptance.'
] };
let browser;
let currentStage = 'launch';
let candidate;
let completedSave;
let friendDeviceId;
let hostingStartRequests = 0;
let hostingPrepareRequests = 0;
const hostingStartResults = [];
const hostingRequestDiagnostics = [];
const hostingUiTransitions = [];
const hostingRequests = new WeakMap();
const diagnosticsStartedAt = Date.now();
const transitionClockStartedAt = performance.now();
const browserRequests = new WeakMap();
const transitionStarts = [];
const transitionModeChanges = [];
const transitionHostSnapshots = [];
const httpFailures = [];
const pendingResponseDiagnostics = new Set();
let diagnosticOrder = 0;
let transitionObservationsTruncated = false;
const diagnosticStamp = () => ({ order: ++diagnosticOrder,
  ms: Math.round((performance.now() - transitionClockStartedAt) * 1000) / 1000, stage: currentStage });
const roleTransitionRefusals = [];
report.roleTransitionRefusals = roleTransitionRefusals;
function captureTransitionEvent(collection, event) {
  if (collection.length < 16) collection.push(event);
  else transitionObservationsTruncated = true;
}
async function settleResponseDiagnostics() {
  const pending = [...pendingResponseDiagnostics];
  if (!pending.length) return;
  let timer;
  const settled = await Promise.race([Promise.allSettled(pending).then(() => true),
    new Promise(resolve => { timer = setTimeout(() => resolve(false), 10000); })]);
  clearTimeout(timer);
  if (!settled && report.errors.length < 20) report.errors.push({ stage: currentStage, kind: 'request-diagnostic-timeout' });
}
function classifyHttpFailures() {
  const allHttpEntries = new Set(httpFailures.map(item => item.reportEntry));
  const failed = report.failedRequests.filter(entry => !allHttpEntries.has(entry));
  roleTransitionRefusals.length = 0;
  for (const item of httpFailures) {
    const classification = classifyRoleTransitionRefusal(item.event, { startRequestCount: hostingStartRequests,
      starts: transitionStarts, modeChanges: transitionModeChanges, hostSnapshots: transitionHostSnapshots,
      observationsTruncated: transitionObservationsTruncated }, roleTransitionRefusals.length);
    if (classification.accepted) roleTransitionRefusals.push(classification.evidence);
    else {
      if (item.event.response.status === 409) item.reportEntry.roleTransitionClassification = classification.reason;
      failed.push(item.reportEntry);
    }
  }
  report.failedRequests.splice(0, report.failedRequests.length, ...failed);
}
let closingBrowser = false;
let previousHostingUi = '';
let previousConnectionId;
let startConnectionId;
let latestHostingUi = null;
const fixedCode = value => typeof value === 'string' && /^[A-Za-z][A-Za-z0-9]{0,79}$/u.test(value) ? value : 'Unavailable';
const requestFailureKind = value => new Map([
  ['net::ERR_ABORTED', 'Aborted'], ['net::ERR_TIMED_OUT', 'TimedOut'],
  ['net::ERR_CONNECTION_CLOSED', 'ConnectionClosed'], ['net::ERR_CONNECTION_RESET', 'ConnectionReset']
]).get(value) ?? 'Other';
report.hostingRequestDiagnostics = hostingRequestDiagnostics;
report.hostingUiTransitions = hostingUiTransitions;
async function hostingUiPresence(page) {
  return page.evaluate(profileId => ({
    sourceArticlePresent: document.getElementById(`friend-server-${profileId}`) !== null,
    hostingActionPresent: document.querySelector('button.local-hosting-trigger') !== null,
    hostingDialogOpen: [...document.querySelectorAll('dialog[open]')].some(dialog =>
      dialog.querySelector('h2')?.textContent === 'Host locally')
  }), input.profileId);
}
const privateText = new Set();
const pages = [];
const timeout = 30000;
const exact = value => new RegExp(`^${value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'i');
const visible = locator => locator.filter({ visible: true });
async function button(page, name) {
  const match = visible(page.getByRole('button', { name: typeof name === 'string' ? exact(name) : name }));
  await match.first().click({ timeout: 15000 });
}
async function optional(page, name) {
  const match = visible(page.getByRole('button', { name: typeof name === 'string' ? exact(name) : name }));
  if (await match.count()) { await match.first().click(); return true; }
  return false;
}
async function selectedLocalProfile(page) {
  return page.evaluate(() => {
    try {
      const value = JSON.parse(localStorage.getItem('togetherserver.ui-preferences.v1') ?? 'null');
      return typeof value?.selectedServer === 'string' && /^[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12}$/i.test(value.selectedServer)
        ? value.selectedServer : null;
    } catch { return null; }
  });
}
function directoryNavigation(directory, name, profileId, now) {
  const locations = [
    ...directory.localServers.map(server => ({ server, target: { kind: 'local', profileId: server.id } })),
    ...directory.friendHosts.filter(host => host.state !== 'Revoked' && host.connectionCode !== 'Revoked')
      .flatMap(host => host.servers.map(server => ({ server, host,
        target: { kind: 'friend', connectionId: host.connectionId, profileId: server.id } })))
  ];
  const signed = item => /^[a-f\d]{64}$/i.test(item.server.sharedWorldId ?? '') &&
    /^[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12}$/i.test(item.server.originalProfileId ?? '');
  const identity = item => signed(item)
    ? `${item.server.sharedWorldId.toUpperCase()}:${item.server.originalProfileId.toLowerCase()}`
    : JSON.stringify(item.target);
  const requested = locations.filter(item => (name === 'Host' ? item.target.kind === 'local' : item.target.kind === 'friend') &&
    (item.server.id === profileId || name === 'Join' && signed(item) && item.server.originalProfileId === profileId));
  const roots = new Set(requested.map(identity));
  if (roots.size !== 1) throw new Error('The exact requested directory world is missing or ambiguous');
  const family = locations.filter(item => roots.has(identity(item)));
  const anchor = family.find(item => !item.server.isSuccessor && item.server.id === item.server.originalProfileId) ?? family[0];
  const active = family.filter(item => {
    const checked = item.host?.lastConnectedUtc ? Date.parse(item.host.lastConnectedUtc) : NaN;
    const current = !item.host || ['Connected', 'Disabled'].includes(item.host.state) && Number.isFinite(checked) && checked <= now && now - checked <= 15000;
    return current && (['Ready', 'Starting', 'Stopping', 'Process running', 'Listening', 'Unknown'].includes(item.server.state) || item.server.requiresReview);
  });
  if (active.length > 1) throw new Error('Several hosting PCs need an explicit selection; the lab will not choose one');
  const route = active[0] ?? anchor;
  if (route.target.kind !== (name === 'Host' ? 'local' : 'friend') || name === 'Host' && route.server.id !== profileId)
    throw new Error('The grouped world row does not route to the requested PC and profile');
  const worldName = anchor.server.isSuccessor ? anchor.server.sourceName || anchor.server.name : anchor.server.name;
  const owner = anchor.host ? anchor.host.hostProfileName || anchor.host.connectionName : 'this PC';
  return { target: route.target, label: `Open ${worldName} on ${owner}` };
}
async function verifyWorldSidebar(page, location, stopped = false) {
  const onOwner = location === 'owner';
  const directory = await until(() => read(page, '/api/local/servers'), view => {
    const original = (onOwner ? view.localServers : view.friendHosts.flatMap(host => host.servers))
      .filter(server => server.id === input.profileId && !server.isSuccessor && server.sharedWorldId === candidate.groupKey &&
        server.originalProfileId === input.profileId);
    const successor = (onOwner ? view.friendHosts.flatMap(host => host.servers) : view.localServers)
      .filter(server => server.id === candidate.profileId && server.isSuccessor && server.sharedWorldId === candidate.groupKey &&
        server.originalProfileId === input.profileId);
    return original.length === 1 && successor.length === 1 && original[0].state === 'Offline' &&
      successor[0].state === (stopped ? 'Offline' : 'Process running');
  }, `${location} canonical signed original world and exact successor state`);
  const name = onOwner && !stopped ? 'Join' : 'Host';
  const profileId = onOwner ? input.profileId : candidate.profileId;
  const plan = directoryNavigation(directory, name, profileId, Date.now());
  const expectedKind = onOwner && !stopped ? 'friend' : 'local';
  const expectedProfile = stopped ? input.profileId : candidate.profileId;
  if (plan.target.kind !== expectedKind || plan.target.profileId !== expectedProfile)
    throw new Error('The single signed world row selected the wrong original or successor profile');
  const hostingPc = onOwner ? directory.friendHosts.find(host => host.connectionId === plan.target.connectionId) : null;
  const badge = onOwner ? `Hosted by ${hostingPc?.hostProfileName || hostingPc?.connectionName}` : 'Hosting on this PC';
  const originalHost = onOwner ? null : directory.friendHosts.find(host => host.servers.some(server =>
    server.id === input.profileId && server.sharedWorldId === candidate.groupKey && server.originalProfileId === input.profileId));
  const region = page.getByRole('region', { name: 'Servers', exact: true });
  const row = region.getByRole('button', { name: exact(plan.label) });
  const singleRow = async () => {
    if (await row.count() !== 1 || await region.getByRole('button', { name: /^Open /u }).count() !== 1 ||
      await region.getByRole('heading', { level: 4 }).count() !== (onOwner ? 0 : 1)) return false;
    if (!onOwner && (await region.getByRole('heading', { level: 4 }).innerText()).trim() !==
      (originalHost?.hostProfileName || originalHost?.connectionName)) return false;
    const badges = row.locator('.remote-hosting-badge');
    return stopped ? await badges.count() === 0 : await badges.count() === 1 && (await badges.innerText()).trim() === badge;
  };
  await until(singleRow, Boolean, `${location} one original-world sidebar row with exact hosting badge`);
  await row.click();
  await until(async () => ({ snapshot: await read(page), selected: expectedKind === 'local' ? await selectedLocalProfile(page) : null }), value => {
    const snapshot = value.snapshot;
    if (expectedKind === 'friend') return snapshot.mode === 'Friend' && snapshot.connectionId === plan.target.connectionId &&
      snapshot.endpoint.replace(/\/$/, '') === candidate.endpoint && snapshot.profiles.length === 1 &&
      snapshot.profiles[0].id === candidate.profileId && snapshot.profiles[0].runOperationId === candidate.runId;
    return snapshot.mode === 'Host' && value.selected === expectedProfile && snapshot.settings.profiles.some(profile => profile.id === expectedProfile) &&
      (stopped || snapshot.runs.some(run => run.profileId === candidate.profileId && run.runOperationId === candidate.runId));
  }, `${location} sidebar routes to the exact ${stopped ? 'original' : 'successor'} PC and profile`);
  await until(singleRow, Boolean, `${location} canonical row remains after navigation`);
  return { singleOriginalRow: true, hostingBadge: !stopped, exactRoute: true, noSuccessorHostGroup: true };
}
async function nav(page, name, requestedProfileId) {
  // Host and Join are directory selections in the current UI, not header tabs.
  if (name === 'Host' || name === 'Join') {
    const snapshot = await read(page);
    const selected = snapshot.mode === 'Host' ? await selectedLocalProfile(page) : null;
    const profileId = requestedProfileId ?? (name === 'Host' ? selected : null) ?? input.profileId;
    if (name === 'Host' && snapshot.mode === 'Host' && selected === profileId &&
        snapshot.settings.profiles.some(profile => profile.id === profileId) && snapshot.runs.some(run => run.profileId === profileId)) return;
    if (name === 'Join' && snapshot.mode === 'Friend' && snapshot.connectionId &&
        snapshot.profiles.some(profile => profile.id === profileId)) return;
    const directory = await read(page, '/api/local/servers');
    if (name === 'Join' && directory.friendHosts.length === 0) {
      await page.locator('.server-directory-actions').getByRole('button', { name: 'Connect', exact: true }).click(); return;
    }
    const plan = directoryNavigation(directory, name, profileId, Date.now());
    const row = visible(page.getByRole('button', { name: exact(plan.label) }));
    if (await row.count() !== 1) throw new Error('The exact directory navigation row is missing or ambiguous');
    await row.click({ timeout: 15000 });
    await until(async () => ({ snapshot: await read(page), selected: name === 'Host' ? await selectedLocalProfile(page) : null }),
      value => name === 'Host' ? value.snapshot.mode === 'Host' && value.selected === plan.target.profileId &&
        value.snapshot.settings.profiles.some(profile => profile.id === plan.target.profileId) :
        value.snapshot.mode === 'Friend' && value.snapshot.connectionId === plan.target.connectionId &&
        value.snapshot.profiles.some(profile => profile.id === plan.target.profileId), 'exact directory navigation target');
    return;
  }
  if (name === 'Local pairing lab') {
    const local = visible(page.getByRole('button', { name: /^Open Local pairing lab on this PC$/i }));
    if (await local.count()) { await local.first().click(); return; }
    return;
  }
  const match = visible(page.getByRole('button', { name: exact(name) })
    .or(page.getByRole('tab', { name: exact(name) })).or(page.getByRole('link', { name: exact(name) })));
  await match.first().click({ timeout: 15000 });
}
async function choice(page, name, enabled, savePath) {
  const control = visible(page.getByRole('checkbox', { name })).first();
  await control.waitFor({ state: 'visible', timeout });
  await until(() => control.isEnabled(), Boolean, 'saved checkbox enabled');
  if (await control.isChecked() !== enabled) {
    // A native checkbox toggles before React receives the durable save result.
    // Wait for that result before another click or navigation can race it.
    const saving = page.waitForResponse(response => response.request().method() === 'PUT'
      && new URL(response.url()).origin === new URL(page.url()).origin
      && (savePath ? savePath.test(new URL(response.url()).pathname)
        : new URL(response.url()).pathname.startsWith('/api/local/')), { timeout });
    await control.click({ timeout });
    const response = await saving;
    const result = await response.json();
    if (!response.ok() || result?.ok === false) throw new Error('The checkbox save was rejected');
  }
  await until(() => control.isChecked(), value => value === enabled, 'saved checkbox choice');
}
const checked = (page, name, savePath) => choice(page, name, true, savePath);
async function verifyOwnedPayload(directory, files, owner) {
  const ownerRoot = path.join(runRoot, owner);
  for (const entry of files) {
    if (typeof entry.path !== 'string' || !entry.path || entry.path.includes('\\')
      || entry.path.split('/').some(part => !part || part === '.' || part === '..')
      || !/^[0-9a-f]{64}$/i.test(entry.sha256) || !Number.isSafeInteger(entry.length) || entry.length < 0 || entry.length > 16 * 1024 * 1024)
      throw new Error('The synthetic completed-save manifest is invalid or exceeds its fixed bound');
    const file = path.resolve(directory, entry.path);
    const relative = path.relative(ownerRoot, file);
    if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) throw new Error('Synthetic copy escaped its exact owned app root');
    const info = await fs.lstat(file);
    if (!info.isFile() || info.isSymbolicLink() || info.size !== entry.length
      || (await fs.realpath(file)).toLowerCase() !== file.toLowerCase()) throw new Error('Synthetic copy has a linked, missing or changed file');
    const actual = createHash('sha256').update(await fs.readFile(file)).digest('hex');
    if (actual !== entry.sha256.toLowerCase()) throw new Error('Synthetic copy differs from the exact completed-save hash');
  }
}
async function read(page, suffix = '/api/local/snapshot') {
  return page.evaluate(async endpoint => {
    const response = await fetch(endpoint, { credentials: 'same-origin', headers: { 'X-TogetherServer-Local': '1' } });
    if (!response.ok) throw new Error(`Read-only ground truth returned ${response.status}`);
    return response.json();
  }, suffix);
}
async function until(action, predicate, label, milliseconds = timeout) {
  const end = Date.now() + milliseconds;
  while (Date.now() < end) {
    const value = await action();
    if (predicate(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 350));
  }
  throw new Error(`Timed out: ${label}`);
}
async function shot(page, name) {
  await page.screenshot({ path: path.join(artifacts, `${name}.png`), fullPage: true,
    mask: [page.locator('input[type=password]'), page.locator('.invite-ready'), page.locator('.invite-code')] });
}
async function geometry(page) {
  const result = await page.evaluate(() => {
    const isVisible = element => element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden';
    const headings = [...document.querySelectorAll('h1,h2,h3')].filter(isVisible);
    const keys = headings.map(element => `${element.tagName}:${element.textContent.trim()}`);
    return { overflow: document.documentElement.scrollWidth > innerWidth + 2,
      duplicates: keys.filter((key, index) => keys.indexOf(key) !== index) };
  });
  if (result.overflow || result.duplicates.length) throw new Error('Visible duplicate headings or horizontal page overflow');
}
async function stage(name, work) {
  currentStage = name;
  const started = Date.now();
  await work();
  report.stages.push({ name, passed: true, elapsedMs: Date.now() - started });
}
async function setup(page, name) {
  await page.goto([...origins][pages.indexOf(page)], { waitUntil: 'domcontentloaded' });
  await until(async () => ({ name: await page.getByLabel('Your name', { exact: true }).count(),
    main: await page.getByRole('button', { name: 'Servers', exact: true }).count() }),
  value => value.name || value.main, 'initial setup');
  if (await page.getByLabel('Your name', { exact: true }).count()) {
    await page.getByLabel('Your name', { exact: true }).fill(name);
    await button(page, 'Save and continue');
  }
  await page.getByRole('button', { name: 'Servers', exact: true }).waitFor({ timeout });
  const guide = await read(page, '/api/local/getting-started');
  if (!guide.offered || guide.dismissed) await button(page, 'Setup guides');
  await button(page, 'Set up later');
  await optional(page, 'Pause walkthrough');
}
async function ownerOverview(page) {
  await nav(page, 'Host');
  await optional(page, 'Return to selected server');
  await nav(page, 'Local pairing lab').catch(async () => {
    // A prepared local branch may have its own name. Its selection stays intact.
    if (!(await visible(page.getByRole('button', { name: /Start server|Stop server/ })).count())) throw new Error('No selected local server');
  });
  await nav(page, 'Overview');
}
async function access(page) {
  const directAccess = visible(page.getByRole('button', { name: 'Access', exact: true }));
  if (await directAccess.count()) { await directAccess.first().click(); return; }
  if (!await visible(page.locator('details.server-sections-disclosure')).count()) await nav(page, 'Host');
  const direct = await optional(page, 'Friend access');
  if (!direct) {
    const disclosure = page.locator('details.server-sections-disclosure');
    if (!await disclosure.evaluate(element => element.open)) await disclosure.locator('summary').click();
    await nav(page, 'Access');
  }
}
async function sharedSaves(page) {
  await nav(page, 'Shared saves');
  await page.getByRole('button', { name: 'Host locally', exact: true }).waitFor({ state: 'visible', timeout });
}
async function stop(page) {
  await button(page, 'Stop server');
  const dialog = visible(page.getByRole('dialog'));
  if (await dialog.count()) await visible(dialog.getByRole('button', { name: exact('Stop server') })).click();
  await until(() => read(page), snapshot => snapshot.runs.every(run => !['Starting', 'Process running', 'Listening', 'Ready', 'Stopping'].includes(run.state)), 'owner local Stop');
}
async function chat(page, message) {
  await nav(page, 'Chat');
  const region = page.getByRole('region', { name: 'Server chat', exact: true });
  await region.getByLabel('Message', { exact: true }).fill(message);
  await region.getByRole('button', { name: 'Send', exact: true }).click();
}

try {
  const { chromium } = require('playwright');
  browser = await chromium.launch({ headless: true, args: ['--mute-audio', '--disable-features=MediaRouter', '--disable-background-networking'] });
  for (const origin of origins) {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: false });
    await context.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (origins.has(url.origin)) return route.continue();
      if (report.failedRequests.length < 40) report.failedRequests.push({ stage: currentStage, reason: 'Nonlocal browser request blocked' });
      return route.abort('blockedbyclient');
    });
    const page = await context.newPage();
    page.on('request', request => {
      const event = { request: { ...diagnosticStamp(), ...roleTransitionRequestFacts({ url: request.url(),
        pageOrigin: origin, candidateOrigin: `http://127.0.0.1:${input.friendPort}`,
        ownerOrigin: `http://127.0.0.1:${input.hostPort}`, originalProfileId: input.profileId,
        method: request.method(), headers: request.headers(), postData: request.postData() }) }, state: 'Pending' };
      browserRequests.set(request, event);
      if (request.method() === 'POST' && new URL(request.url()).pathname ===
        `/api/local/friend/${input.profileId}/shared-world/hosting/start`) {
        hostingStartRequests++;
        captureTransitionEvent(transitionStarts, event);
      }
      if (event.request.route === 'HostMode' && event.request.pageRole === 'Candidate' && hostingStartRequests > 0)
        captureTransitionEvent(transitionModeChanges, event);
      if (request.method() === 'POST' && new URL(request.url()).pathname ===
        `/api/local/friend/${input.profileId}/shared-world/hosting/prepare`) hostingPrepareRequests++;
      const operation = request.method() === 'POST' && new URL(request.url()).pathname.match(
        new RegExp(`^/api/local/friend/${input.profileId}/shared-world/hosting/(prepare|start)$`, 'u'))?.[1];
      if (operation && hostingRequestDiagnostics.length < 16) {
        if (operation === 'start') startConnectionId = previousConnectionId;
        const entry = { operation, startedMs: Date.now() - diagnosticsStartedAt, elapsedMs: null,
          state: 'Pending', status: null, code: 'Unavailable', ok: false, failureKind: null, duringBrowserCleanup: false };
        hostingRequests.set(request, entry);
        hostingRequestDiagnostics.push(entry);
      }
    });
    page.on('pageerror', () => { if (report.errors.length < 20) report.errors.push({ stage: currentStage, kind: 'pageerror' }); });
    page.on('requestfailed', request => {
      const transition = browserRequests.get(request);
      if (transition) transition.state = 'Failed';
      const entry = hostingRequests.get(request);
      if (entry) Object.assign(entry, { state: 'Failed', elapsedMs: Date.now() - diagnosticsStartedAt - entry.startedMs,
        failureKind: requestFailureKind(request.failure()?.errorText), duringBrowserCleanup: closingBrowser });
      if (entry?.operation === 'start') {
        entry.lastSnapshot = latestHostingUi;
        void hostingUiPresence(page).then(presence => { entry.uiAtFailure = presence; }).catch(() => {});
      }
      const entries = request.method() === 'GET' && request.failure()?.errorText === 'net::ERR_ABORTED'
        ? report.canceledRequests : report.failedRequests;
      if (entries.length < 40) entries.push({ stage: currentStage,
        method: request.method(), path: new URL(request.url()).pathname.replace(/[0-9a-f]{8}-[0-9a-f-]{27,}/gi, ':id').slice(0, 160) });
    });
    page.on('requestfinished', request => {
      const transition = browserRequests.get(request);
      if (transition && transition.state !== 'Failed') transition.state = 'Finished';
      const entry = hostingRequests.get(request);
      if (entry) Object.assign(entry, { state: 'Finished', elapsedMs: Date.now() - diagnosticsStartedAt - entry.startedMs });
    });
    page.on('response', response => {
      const request = response.request();
      const observed = diagnosticStamp();
      const transition = browserRequests.get(request);
      if (transition) transition.response = { ...observed, status: response.status(), ...roleTransitionPayloadFacts(null) };
      // HTTP errors remain failures until the final complete timeline proves the
      // one exact role-transition refusal. No requestfailed event enters here.
      if (response.status() >= 400 && report.failedRequests.length < 40) {
        const reportEntry = { stage: observed.stage, status: response.status(), method: request.method(),
          path: new URL(response.url()).pathname.replace(/[0-9a-f]{8}-[0-9a-f-]{27,}/gi, ':id').slice(0, 160),
          code: 'Unavailable', request: transition?.request ?? null, response: transition?.response ?? null };
        report.failedRequests.push(reportEntry);
        if (transition) httpFailures.push({ event: transition, reportEntry });
      }
      const recording = (async () => {
        const entry = hostingRequests.get(request);
        if (entry) entry.status = response.status();
        const isStart = request.method() === 'POST' && new URL(response.url()).pathname ===
          `/api/local/friend/${input.profileId}/shared-world/hosting/start`;
        const isSnapshot = origin === `http://127.0.0.1:${input.friendPort}` &&
          new URL(response.url()).pathname === '/api/local/snapshot';
        const isPolicyChange = response.url().endsWith('/api/local/settings/control-policy') && request.method() === 'PUT';
        const needsBody = entry || isStart || isSnapshot || isPolicyChange || response.status() >= 400 || transition?.request.route === 'HostMode';
        const isProgressRefusal = transition?.request.route === 'OriginalHostingProgress' && response.status() === 409;
        // Only allowlisted code/mode/boolean facts survive this local decode.
        // Progress intentionally returns an empty 409 after the mode changes.
        // Capture actual bytes; a failed JSON parse cannot establish emptiness.
        const progressBody = isProgressRefusal ? await response.body().catch(() => undefined) : undefined;
        const result = needsBody && !isProgressRefusal ? await response.json().catch(() => null) : null;
        let bodyFinished = false;
        if (transition && needsBody) {
          bodyFinished = await response.finished().then(error => error === null).catch(() => false);
          if (!bodyFinished) transition.state = 'Failed';
          else if (transition.state !== 'Failed') transition.state = 'Finished';
        }
        if (transition && needsBody) Object.assign(transition.response, roleTransitionPayloadFacts(result),
          roleTransitionBodyFacts(isProgressRefusal && bodyFinished ? progressBody : undefined));
        const failure = httpFailures.find(item => item.event === transition);
        if (failure) failure.reportEntry.code = transition.response.code;
        if (entry) Object.assign(entry, { status: response.status(), code: fixedCode(result?.code), ok: result?.ok === true });
        if (isStart) hostingStartResults.push({ status: response.status(), ok: result?.ok === true });
        if (isSnapshot) {
          const snapshot = result;
          if (transition?.response.mode === 'Host' && transitionStarts.length &&
            transition.response.order > transitionStarts[0].request.order) {
            // Keep the first Host response by observed event order, independently
            // of asynchronous JSON/UI diagnostic completion order.
            if (!transitionHostSnapshots.length || transition.response.order < transitionHostSnapshots[0].response.order)
              transitionHostSnapshots.splice(0, transitionHostSnapshots.length, transition);
          }
          try {
            const connectionStates = ['Connected', 'Disabled', 'Disconnected/Unknown', 'Revoked', 'Access expired',
              'Awaiting approval', 'Update required', 'Not connected'];
            const observation = {
              mode: ['Host', 'Friend'].includes(snapshot?.mode) ? snapshot.mode : 'Other',
              connectionState: connectionStates.includes(snapshot?.state) ? snapshot.state : 'Other',
              connectionCode: snapshot?.connectionCode == null ? 'None' : fixedCode(snapshot.connectionCode),
              connectionChanged: previousConnectionId !== undefined && previousConnectionId !== snapshot?.connectionId,
              connectionMatchesStart: startConnectionId === undefined ? null : startConnectionId === snapshot?.connectionId,
              sourceAssigned: Array.isArray(snapshot?.profiles) && snapshot.profiles.some(profile => profile?.id === input.profileId),
              assignedProfiles: Array.isArray(snapshot?.profiles) ? Math.min(100, snapshot.profiles.length) : 0,
              managedRuns: Array.isArray(snapshot?.runs) ? Math.min(2, snapshot.runs.length) : 0,
              ...await hostingUiPresence(page)
            };
            previousConnectionId = snapshot?.connectionId;
            latestHostingUi = { elapsedMs: Date.now() - diagnosticsStartedAt, ...observation };
            const signature = JSON.stringify(observation);
            if (signature !== previousHostingUi) {
              previousHostingUi = signature;
              if (hostingUiTransitions.length === 64) hostingUiTransitions.shift();
              hostingUiTransitions.push({ elapsedMs: Date.now() - diagnosticsStartedAt, stage: currentStage, ...observation });
            }
          } catch { /* Read-only diagnostics cannot change the journey's result. */ }
        }
        if (isPolicyChange) report.controlPolicyChanges.push({ status: response.status(), ok: result?.ok === true,
          code: /^[A-Za-z0-9]{1,80}$/.test(result?.code ?? '') ? result.code : 'Unavailable',
          listening: result?.snapshot?.settings?.companionListeningEnabled === true });
      })();
      pendingResponseDiagnostics.add(recording);
      void recording.catch(() => {
        if (report.errors.length < 20) report.errors.push({ stage: observed.stage, kind: 'request-diagnostic-error' });
      }).finally(() => pendingResponseDiagnostics.delete(recording));
    });
    page.on('download', download => { void download.cancel(); report.errors.push({ stage: currentStage, kind: 'download-refused' }); });
    page.on('dialog', dialog => { void dialog.dismiss(); report.errors.push({ stage: currentStage, kind: 'native-dialog-refused' }); });
    pages.push(page);
  }
  const [host, friend] = pages;
  await stage('required setup on both PCs', async () => {
    await setup(host, 'Lab Owner');
    await setup(friend, 'Lab Friend');
    await shot(host, '01-host-setup'); await shot(friend, '01-friend-setup');
  });
  await stage('server code pairing and owner approval', async () => {
    await ownerOverview(host);
    const issued = host.waitForResponse(response => response.url().endsWith(`/api/local/servers/${input.profileId}/invite`) && response.request().method() === 'POST');
    await button(host, 'Invite friends');
    let invitation = await (await issued).json();
    await host.getByText('New PCs and replacing the code', { exact: true }).click();
    const approval = host.getByRole('checkbox', { name: /Ask me to approve each new PC/ });
    if (await approval.count() && !await approval.isChecked()) {
      await approval.check();
      const saved = host.waitForResponse(response => response.url().endsWith(`/api/local/servers/${input.profileId}/invite`) && response.request().method() === 'POST');
      await button(host, 'Save approval setting'); invitation = await (await saved).json();
    }
    if (!invitation.ok || !invitation.password) throw new Error('UI did not issue a server code');
    privateText.add(invitation.password);
    await nav(friend, 'Join');
    await friend.getByLabel('Server code', { exact: true }).fill(invitation.password);
    await friend.locator('form.join-row').getByRole('button', { name: 'Connect', exact: true }).click();
    invitation = null;
    await button(host, 'Done');
    await access(host);
    await button(host, 'Approve this PC');
    await until(() => read(friend), snapshot => ['Connected', 'Disabled'].includes(snapshot.state) && snapshot.profiles?.length === 1, 'approved assigned server');
    await nav(friend, 'Play');
    await until(() => read(friend, `/api/local/friend/${input.profileId}/shared-world/hosting`),
      value => !value.allowed && !value.readyForManualStart && !value.running, 'sharing initially off');
    if (await visible(friend.getByRole('button', { name: 'Host locally', exact: true })).count()
      || hostingStartRequests || hostingPrepareRequests) throw new Error('A Friend without shared-hosting permission can invoke local hosting');
    await shot(host, '02-owner-approval'); await shot(friend, '02-paired-friend');
  });
  await stage('one server choice automatically authorizes verified receiving', async () => {
    await ownerOverview(host); await nav(host, 'Backups'); await nav(host, 'Sharing & hosting');
    const workspace = host.getByRole('region', { name: 'Shared hosting', exact: true });
    await workspace.waitFor({ state: 'visible', timeout });
    if (await workspace.getByRole('checkbox').count() !== 1) throw new Error('Shared hosting must have one owner switch');
    await checked(host, 'Allow friends to host this server', new RegExp(`^/api/local/profiles/${input.profileId}/shared-world/hosting$`, 'i'));
    await sharedSaves(friend);
    await until(() => read(friend, `/api/local/friend/${input.profileId}/shared-world`),
      value => value.consented === true, 'automatic approved-PC receiving');
    await until(() => read(friend, `/api/local/friend/${input.profileId}/shared-world/hosting`),
      value => value.allowed && value.consented, 'automatic signed hosting grant');
    const devices = (await read(host, '/api/local/companion')).devices.filter(device => device.paired && !device.revoked);
    if (devices.length !== 1 || !/^[0-9a-f-]{36}$/i.test(devices[0].id)) throw new Error('The lab did not retain one separate approved PC identity');
    friendDeviceId = devices[0].id;
    const roster = await read(host, `/api/local/profiles/${input.profileId}/shared-world/governance`);
    const member = roster.members?.find(item => item.deviceId === friendDeviceId);
    if (member?.revoked || !member?.grants?.receive || !member.grants.eligibleHost || !member.grants.reshare
      || member.grants.recoveryVoter || member.grants.manageSharing || !roster.signature)
      throw new Error('The owner switch did not publish the exact automatic signed sharing permissions');
    report.automaticSharing = { ownerSwitches: 1, unauthorizedActionHidden: true, automaticReceiving: true };
    await shot(host, '03-host-sharing'); await shot(friend, '03-friend-automatic-receiving');
  });
  await stage('server policy withdrawal blocks stale hosting intent', async () => {
    const policyPath = new RegExp(`^/api/local/profiles/${input.profileId}/shared-world/hosting$`, 'i');
    await nav(friend, 'Play'); await button(friend, 'Host locally');
    const dialog = friend.getByRole('dialog', { name: 'Host locally', exact: true });
    await dialog.waitFor({ state: 'visible', timeout });
    await choice(host, 'Allow friends to host this server', false, policyPath);
    await until(() => read(friend, `/api/local/friend/${input.profileId}/shared-world/hosting`),
      value => !value.allowed && !value.readyForManualStart && !value.running, 'withdrawn server hosting policy');
    await until(() => dialog.getByRole('button', { name: 'Host locally', exact: true }).isEnabled(),
      enabled => !enabled, 'stale hosting confirmation disabled');
    await dialog.getByRole('button', { name: 'Close Host locally', exact: true }).click();
    await nav(friend, 'Play');
    await until(() => visible(friend.getByRole('button', { name: 'Host locally', exact: true })).count(),
      count => count === 0, 'removed hosting action');
    if (hostingStartRequests || hostingPrepareRequests) throw new Error('Withdrawing the server policy dispatched hosting');
    report.failurePathCoverage = { hostingGrantWithdrawal: false, grantWithdrawalObserved: true,
      grantRestoredByOwner: false, authenticatedRecovery: false, signedGrantRefreshed: false,
      serverPolicyWithdrawal: true, staleConfirmationDisabled: true,
      recoveryStep: 'restoring-owner-policy', automaticRecoveryBudgetMs: timeout, explicitRecoveryUsed: false };
    await shot(friend, '03-hosting-permission-removed');
    await checked(host, 'Allow friends to host this server', policyPath);
    report.failurePathCoverage.grantRestoredByOwner = true;
    report.failurePathCoverage.recoveryStep = 'waiting-for-automatic-connection';
    await until(async () => {
      const connection = await read(friend);
      report.failurePathCoverage.lastConnectionState = ['Connected', 'Disabled', 'Disconnected/Unknown', 'Revoked', 'Access expired', 'Awaiting approval']
        .includes(connection.state) ? connection.state : 'Other';
      return ['Connected', 'Disabled'].includes(connection.state) &&
        connection.profiles?.some(profile => profile.id === input.profileId);
    }, Boolean, 'automatic fresh assigned connection after restored grant');
    report.failurePathCoverage.authenticatedRecovery = true;
    report.failurePathCoverage.recoveryStep = 'waiting-for-automatic-authenticated-grant';
    await until(async () => {
      const connection = await read(friend);
      const hosting = await read(friend, `/api/local/friend/${input.profileId}/shared-world/hosting`);
      report.failurePathCoverage.lastConnectionState = ['Connected', 'Disabled', 'Disconnected/Unknown', 'Revoked', 'Access expired', 'Awaiting approval']
        .includes(connection.state) ? connection.state : 'Other';
      report.failurePathCoverage.lastHostingCode = /^[A-Za-z0-9]{1,80}$/.test(hosting.code ?? '') ? hosting.code : 'Unavailable';
      report.failurePathCoverage.lastHostingAllowed = hosting.allowed === true;
      report.failurePathCoverage.lastHostingConsented = hosting.consented === true;
      return ['Connected', 'Disabled'].includes(connection.state) &&
        connection.profiles?.some(profile => profile.id === input.profileId) && hosting.allowed && hosting.consented;
    }, Boolean, 'automatic restored hosting permission and fresh assigned connection');
    report.failurePathCoverage.authenticatedRecovery = true;
    report.failurePathCoverage.signedGrantRefreshed = true;
    report.failurePathCoverage.recoveryStep = 'returning-to-play';
    await nav(friend, 'Play');
    await until(() => read(friend, `/api/local/friend/${input.profileId}/shared-world/hosting`),
      value => value.allowed && value.consented, 'restored hosting permission without losing consent');
    if (hostingStartRequests || hostingPrepareRequests) throw new Error('Restoring the policy replayed canceled hosting intent');
    report.failurePathCoverage.hostingGrantWithdrawal = true;
    report.failurePathCoverage.recoveryStep = 'complete';
  });
  await stage('network confirmation and cancel never prepare or Start', async () => {
    await nav(friend, 'Play'); await button(friend, 'Host locally');
    const dialog = friend.getByRole('dialog', { name: 'Host locally', exact: true });
    await dialog.waitFor({ state: 'visible', timeout });
    const portApproval = dialog.getByRole('checkbox', { name: 'Approve these game ports for reuse on this PC.', exact: true });
    if (await dialog.locator('.local-hosting-warning').count() !== 1 ||
      await dialog.getByRole('checkbox').count() !== await portApproval.count()
      || hostingPrepareRequests || hostingStartRequests) throw new Error('Opening hosting did not preserve its single network confirmation');
    await shot(friend, '03-explicit-network-confirmation');
    await dialog.getByRole('button', { name: 'Close Host locally', exact: true }).click();
    const status = await read(friend, `/api/local/friend/${input.profileId}/shared-world/hosting`);
    if (hostingStartRequests || hostingPrepareRequests || status.running || status.localProfileId)
      throw new Error('Canceling network confirmation prepared or started a local server');
    // Retained report key denotes local network consent, not a removed save toggle.
    report.failurePathCoverage.localConsentAndCancel = true;
    report.failurePathCoverage.networkConfirmationAndCancel = true;
  });
  await stage('bidirectional signed chat and mute', async () => {
    await chat(host, 'Local UI owner message');
    await chat(friend, 'Local UI friend reply');
    await until(async () => await friend.getByText('Local UI owner message', { exact: true }).count(), Boolean, 'owner chat delivered');
    await until(async () => await host.getByText('Local UI friend reply', { exact: true }).count(), Boolean, 'friend reply delivered');
    for (const page of pages) {
      // Exercise both saved choices even when the default is already muted.
      await optional(page, 'Mute chat sounds');
      const unmute = page.getByRole('button', { name: 'Unmute chat sounds', exact: true });
      await unmute.waitFor({ state: 'visible', timeout });
      await until(() => unmute.isEnabled(), Boolean, 'chat sound control enabled');
      await unmute.click();
      const mute = page.getByRole('button', { name: 'Mute chat sounds', exact: true });
      await mute.waitFor({ state: 'visible', timeout });
      if (await mute.getAttribute('aria-pressed') !== 'true') throw new Error('Chat unmute was not saved');
      await mute.click();
      await unmute.waitFor({ state: 'visible', timeout });
      if (await unmute.getAttribute('aria-pressed') !== 'false') throw new Error('Chat mute was not saved');
      await geometry(page);
    }
    await shot(host, '04-owner-chat'); await shot(friend, '04-friend-chat');
  });
  await stage('owner fixture Start and graceful Stop publish copy', async () => {
    await ownerOverview(host); await button(host, 'Start server');
    await until(() => read(host), snapshot => snapshot.runs.some(run => run.profileId === input.profileId && run.state === 'Process running'), 'truthful fixture running');
    await shot(host, '05-owner-running'); await stop(host);
    const originalProfile = (await read(host)).settings.profiles.find(profile => profile.id === input.profileId);
    const shared = await read(host, `/api/local/profiles/${input.profileId}/shared-world`);
    if (!originalProfile || shared.latest?.profileId !== input.profileId || shared.latest.number !== 1
      || !/^[0-9a-f]{64}$/i.test(shared.latest.versionHash) || !shared.latest.signature
      || !Array.isArray(shared.latest.files) || shared.latest.files.length !== 1 || shared.latest.files[0].path !== 'world.dat')
      throw new Error('Graceful Stop did not publish the exact synthetic completed-save manifest');
    completedSave = { version: shared.latest, originalDirectory: originalProfile.worldDirectory,
      receiverDirectory: path.join(runRoot, 'friend-a', 'received-shared-worlds', friendDeviceId.replaceAll('-', ''),
        input.profileId.replaceAll('-', ''), shared.latest.versionHash, 'payload') };
    await sharedSaves(friend);
    await until(() => read(friend, `/api/local/friend/${input.profileId}/shared-world`),
      value => value.consented === true && value.thisPcVersion === completedSave.version.number && value.receiptConfirmed === true,
      'automatically verified received copy ground truth');
    await verifyOwnedPayload(completedSave.originalDirectory, completedSave.version.files, 'host');
    await verifyOwnedPayload(completedSave.receiverDirectory, completedSave.version.files, 'friend-a');
    report.copyIntegrity = { latestVersion: completedSave.version.number, filesChecked: completedSave.version.files.length,
      automaticReceipt: true, receivedHashesMatch: true, originalUnchanged: true };
    await shot(friend, '05-verified-copy');
  });
  await stage(guidedHostingStage, async () => {
    await nav(friend, 'Play'); await button(friend, 'Host locally');
    const dialog = friend.getByRole('dialog', { name: 'Host locally', exact: true });
    await dialog.waitFor({ state: 'visible', timeout });
    const server = dialog.getByLabel('Installed server file', { exact: true });
    await server.waitFor({ state: 'visible', timeout });
    const missing = await read(friend, `/api/local/friend/${input.profileId}/shared-world/hosting`);
    if (!missing.allowed || !missing.consented || !missing.needsServerFile || missing.readyForManualStart || missing.running
      || await dialog.getByRole('button', { name: 'Host locally', exact: true }).isEnabled()
      || hostingPrepareRequests || hostingStartRequests) throw new Error('A missing installed server did not block local hosting');
    await shot(friend, '06-installed-server-required');
    await server.fill(input.fixturePath);
    const portApproval = dialog.getByRole('checkbox', { name: 'Approve these game ports for reuse on this PC.', exact: true });
    if (await portApproval.count()) await portApproval.check();
    const confirm = dialog.getByRole('button', { name: 'Host locally', exact: true });
    await until(() => confirm.isEnabled(), Boolean, 'explicit network confirmation enabled after choosing server and local ports');
    // Product preparation and Start each allow 120 seconds. One shared
    // 255-second deadline covers their automatic sequence without another click.
    const hostingDeadline = Date.now() + 255000;
    const remainingHostingTime = () => Math.max(1, hostingDeadline - Date.now());
    const firstCheck = friend.waitForResponse(response => response.request().method() === 'POST' &&
      new URL(response.url()).pathname === `/api/local/friend/${input.profileId}/shared-world/hosting/prepare`, { timeout: remainingHostingTime() });
    await confirm.click();
    const prepared = await (await firstCheck).json();
    report.preparationDiagnostics = {
      ok: prepared?.ok === true, allowed: prepared?.allowed === true, consented: prepared?.consented === true,
      ready: prepared?.readyForManualStart === true, running: prepared?.running === true,
      needsServerFile: prepared?.needsServerFile === true,
      hasLocalProfile: typeof prepared?.localProfileId === 'string' && prepared.localProfileId.length > 0,
      hasBranch: typeof prepared?.branchHash === 'string' && prepared.branchHash.length > 0,
      code: typeof prepared?.code === 'string' && /^[A-Za-z0-9]{1,80}$/u.test(prepared.code) ? prepared.code : 'Unavailable',
      steps: ['permission', 'server', 'save', 'ports', 'connection'].flatMap(key => {
        const step = Array.isArray(prepared?.steps) ? prepared.steps.find(item => item?.key === key &&
          ['Ready', 'ActionRequired', 'Unknown'].includes(item?.state)) : null;
        return step ? [{ key, state: step.state }] : [];
      })
    };
    if (!prepared.ok || !prepared.allowed || !prepared.consented || !prepared.readyForManualStart
      || !prepared.localProfileId || !prepared.branchHash || prepared.running)
      throw new Error('The single network confirmation did not prepare the exact verified copy');
    const active = await until(() => read(friend), snapshot => {
      const running = snapshot.runs?.filter(run => ['Starting', 'Process running', 'Listening', 'Ready'].includes(run.state));
      return running?.length === 1 && running[0].state === 'Process running' && running[0].runOperationId;
    }, 'one candidate fixture with exact running identity', remainingHostingTime());
    const run = active.runs.find(item => item.state === 'Process running');
    const profile = active.settings.profiles.find(item => item.id === run.profileId);
    if (!profile || profile.separateCopySourceProfileId !== input.profileId || !profile.separateCopyBranchHash ||
      run.playerCountTrusted || run.onlinePlayers != null || !active.settings.companionListeningEnabled)
      throw new Error('Candidate run lost its separate source, confirmed connection or truthful occupancy');
    await verifyOwnedPayload(profile.worldDirectory, completedSave.version.files, 'friend-a');
    await verifyOwnedPayload(completedSave.originalDirectory, completedSave.version.files, 'host');
    report.copyIntegrity.hostedHashesMatch = true;
    report.copyIntegrity.freshDestination = path.resolve(profile.worldDirectory).toLowerCase() !== path.resolve(completedSave.originalDirectory).toLowerCase();
    if (!report.copyIntegrity.freshDestination) throw new Error('Hosting reused the original world directory');
    const localGroups = await until(() => read(friend, '/api/local/world-peers'), groups => groups.some(group =>
      group.profileId === input.profileId && group.canHost && group.receiveConsented && group.state.branches.some(item =>
        item.branch.branchHash === profile.separateCopyBranchHash && item.deviceId === group.selfDeviceId
        && item.authorized && !item.hasConflict && item.branch.baseVersionHash === completedSave.version.versionHash)),
      'candidate authorized world membership and exact selected save', remainingHostingTime());
    const group = localGroups.find(item => item.profileId === input.profileId);
    const selected = group.state.branches.find(item => item.branch.branchHash === profile.separateCopyBranchHash
      && item.deviceId === group.selfDeviceId && item.authorized && !item.hasConflict);
    if (selected?.branch.baseVersionHash !== completedSave.version.versionHash)
      throw new Error('Hosting selected a different completed save');
    candidate = { profileId: profile.id, runId: run.runOperationId, branchHash: profile.separateCopyBranchHash,
      deviceId: group.selfDeviceId, groupKey: group.groupKey, root: group.state.root,
      endpoint: active.settings.companionEndpoint.replace(/\/$/, '') };
    await until(async () => hostingStartResults, results => results.length === 1 && results[0].ok && results[0].status < 400,
      'one successful guided Start response', remainingHostingTime());
    if (hostingPrepareRequests !== 1 || hostingStartRequests !== 1) throw new Error('The single confirmation dispatched duplicate preparation or Start requests');
    report.guidedHosting = { exactRunningSession: true, separateBranch: true, truthfulUnknownPlayers: true,
      missingServerBlocked: true, singleNetworkConfirmation: true, prepareRequests: hostingPrepareRequests,
      startRequests: hostingStartRequests, successfulStartResponses: hostingStartResults.filter(result => result.ok).length };
    await nav(friend, 'Overview');
    if (active.runs.filter(run => run.state === 'Ready').length) throw new Error('Fixture falsely reports game readiness');
    await visible(friend.getByRole('button', { name: 'Stop server', exact: true })).waitFor({ state: 'visible', timeout });
    report.guidedHosting.sidebar = await verifyWorldSidebar(friend, 'candidate');
    await shot(friend, '06-candidate-running');
  });
  await stage('signed first-run discovery and Connect on owner', async () => {
    const groups = await until(() => read(host, '/api/local/world-peers'), value => {
      const group = value.find(item => item.groupKey === candidate.groupKey && item.profileId === input.profileId);
      if (!group || JSON.stringify(group.state.root) !== JSON.stringify(candidate.root)) return false;
      const session = group.state.sessions.find(item => item.deviceId === candidate.deviceId && item.authorized && !item.hasConflict &&
        item.session.sessionId === candidate.runId && item.session.branchHash === candidate.branchHash &&
        item.session.state === 'Preparing' && !item.session.endedUtc);
      const branch = group.state.branches.find(item => item.deviceId === candidate.deviceId && item.authorized && !item.hasConflict &&
        item.branch.branchHash === candidate.branchHash && item.publicKey === session?.publicKey &&
        item.branch.baseVersionHash === session?.session.baseVersionHash && item.branch.headVersionHash === session?.session.headVersionHash);
      const peer = group.state.peers.find(item => item.deviceId === candidate.deviceId && item.authorized && !item.hasConflict &&
        item.publicKey === session?.publicKey && item.routes.some(route => route.address.replace(/\/$/, '') === candidate.endpoint));
      return Boolean(session && branch && peer);
    }, 'owner learns exact signed candidate session, branch and route');
    const learned = groups.find(group => group.groupKey === candidate.groupKey);
    const peer = learned.state.peers.find(item => item.deviceId === candidate.deviceId);
    await ownerOverview(host);
    const ownerSelection = await selectedLocalProfile(host);
    const reviewSummary = visible(host.getByLabel('Reported Host differs from the original Host', { exact: true }));
    await reviewSummary.waitFor({ state: 'visible', timeout });
    if (await reviewSummary.count() !== 1) throw new Error('The exact signed Host review is missing or ambiguous');
    const review = reviewSummary.locator('..');
    if (!await review.evaluate(element => element.open)) await reviewSummary.click();
    const reviewDetails = review.getByRole('region', { name: 'Shared Host status details', exact: true });
    await reviewDetails.waitFor({ state: 'visible', timeout });
    if ((await read(host)).mode !== 'Host' || await selectedLocalProfile(host) !== ownerSelection || hostingStartRequests !== 1)
      throw new Error('Opening the compact hosting report changed the selected Host or repeated Start');
    const connect = visible(reviewDetails.getByRole('button', {
      name: `Connect to PC ending ${candidate.deviceId.slice(-6).toUpperCase()}`, exact: true
    }));
    await connect.waitFor({ state: 'visible', timeout });
    if (await connect.count() !== 1) throw new Error('The exact candidate Connect action is missing or ambiguous');
    await until(() => connect.isEnabled(), Boolean, 'explicit Connect enabled inside the signed Host review');
    const connecting = host.waitForRequest(request => request.method() === 'POST' && new URL(request.url()).pathname ===
      `/api/local/world-peers/${candidate.groupKey}/connect`);
    await connect.click({ timeout: 15000 });
    const selected = (await connecting).postDataJSON();
    if (selected.deviceId !== candidate.deviceId || selected.sessionId !== candidate.runId) throw new Error('Connect selected another signed session');
    await until(() => read(host), snapshot => snapshot.mode === 'Friend' && ['Connected', 'Disabled'].includes(snapshot.state) &&
      snapshot.endpoint.replace(/\/$/, '') === candidate.endpoint && peer.routes.some(route => route.address.replace(/\/$/, '') === candidate.endpoint) &&
      snapshot.profiles?.length === 1 && snapshot.profiles[0].id === candidate.profileId &&
      snapshot.profiles[0].runOperationId === candidate.runId && snapshot.profiles[0].state === 'Process running' &&
      !snapshot.profiles[0].playerCountTrusted && snapshot.profiles[0].onlinePlayers == null,
    'owner connected to exact advertised candidate profile, session and endpoint');
    if (hostingStartRequests !== 1) throw new Error('Connect or polling repeated guided Start');
    report.signedDiscovery = { exactWorldRoot: true, exactCandidateSession: true, exactBranch: true,
      compactReviewOpened: true, displayPreservedSelection: true,
      authorizedWithoutConflict: true, exactAdvertisedEndpoint: true, exactConnectSelection: true,
      sidebar: await verifyWorldSidebar(host, 'owner') };
    await shot(host, '07-owner-connected-new-host');
    await friend.setViewportSize({ width: 430, height: 920 });
    await geometry(friend); await shot(friend, '07-narrow-candidate');
    await friend.setViewportSize({ width: 1440, height: 1000 });
  });
  await stage('candidate owner Stop', async () => {
    await nav(friend, 'Host', candidate.profileId); await optional(friend, 'Return to selected server'); await nav(friend, 'Overview');
    await stop(friend); await shot(friend, '08-candidate-stopped');
    report.signedDiscovery.stoppedSidebar = await verifyWorldSidebar(host, 'owner', true);
    await shot(host, '08-owner-after-candidate-stop');
    await verifyOwnedPayload(completedSave.originalDirectory, completedSave.version.files, 'host');
    await verifyOwnedPayload(completedSave.receiverDirectory, completedSave.version.files, 'friend-a');
    if (hostingStartRequests !== 1 || hostingPrepareRequests !== 1) throw new Error('Discovery, polling or Stop repeated hosting intent');
    report.copyIntegrity.originalUnchangedAfterCandidateStop = true;
  });
  await settleResponseDiagnostics();
  classifyHttpFailures();
  if (report.errors.length || report.failedRequests.length) throw new Error('Browser errors, failed requests or forbidden native interactions were observed');
  report.ok = true;
} catch (error) {
  let detail = String(error?.message ?? error).split('\n').slice(0, 3).join(' ').slice(0, 1200);
  for (const text of privateText) detail = detail.replaceAll(text, '[private code]');
  report.diagnostic = detail;
  const failureCode = report.errors.some(entry => ['download-refused', 'native-dialog-refused'].includes(entry.kind))
    ? 'ForbiddenNativeInteraction' : error?.name === 'TimeoutError' ? 'UiTimeout'
      : report.errors.length ? 'BrowserRuntimeError' : report.failedRequests.length ? 'BrowserRequestFailed'
        : error instanceof Error ? 'UiAssertionFailed' : 'UiRunnerFailed';
  report.failure = { stage: currentStage, code: failureCode,
    message: 'The named UI stage did not satisfy its assertions. Inspect the masked screenshots and bounded request diagnostics.' };
  for (let index = 0; index < pages.length; index++) await shot(pages[index], `failure-${index}`).catch(() => {});
} finally {
  closingBrowser = true;
  if (browser) await browser.close().catch(() => {});
  await settleResponseDiagnostics();
  classifyHttpFailures();
  if (report.ok && (report.errors.length || report.failedRequests.length)) {
    report.ok = false;
    report.failure = { stage: currentStage, code: report.errors.length ? 'BrowserRuntimeError' : 'BrowserRequestFailed',
      message: 'Browser errors or failed requests were observed before report completion.' };
  }
  for (const entry of hostingRequestDiagnostics) entry.elapsedMs ??= Date.now() - diagnosticsStartedAt - entry.startedMs;
  await fs.writeFile(path.join(artifacts, 'report.json'), JSON.stringify(report, null, 2));
  process.stdout.write(`${JSON.stringify({ ok: report.ok, stages: report.stages.length, failedStage: report.failure?.stage })}\n`);
  process.exitCode = report.ok ? 0 : 1;
}
