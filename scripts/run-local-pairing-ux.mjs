// Invoked only by the guarded lab harness, after assignment to its Windows Job.
// One stdin line is the launch barrier. No UI response is mocked or replaced.
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs/promises';
import readline from 'node:readline';

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
let hostingStartRequests = 0;
const hostingStartResults = [];
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
async function nav(page, name) {
  // Host and Join are directory selections in the current UI, not header tabs.
  if (name === 'Host' || name === 'Join') {
    const rows = visible(page.locator('.server-directory-row'));
    const target = rows;
    for (let index = 0; index < await target.count(); index++) {
      const label = await target.nth(index).getAttribute('aria-label') ?? '';
      if (label && (name === 'Host' ? / on this PC$/.test(label) : !/ on this PC$/.test(label))) {
        await target.nth(index).click(); return;
      }
    }
    if (name === 'Join') { await page.locator('.server-directory-actions').getByRole('button', { name: 'Connect', exact: true }).click(); return; }
    throw new Error('No local directory server available');
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
  const section = page.locator('details.local-hosting-advanced').filter({ hasText: 'Shared saves & people' });
  await section.waitFor({ state: 'visible', timeout });
  if (!await section.evaluate(element => element.open)) await section.locator('summary').click();
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
      if (request.method() === 'POST' && new URL(request.url()).pathname ===
        `/api/local/friend/${input.profileId}/shared-world/hosting/start`) hostingStartRequests++;
    });
    page.on('pageerror', () => { if (report.errors.length < 20) report.errors.push({ stage: currentStage, kind: 'pageerror' }); });
    page.on('requestfailed', request => {
      const entries = request.method() === 'GET' && request.failure()?.errorText === 'net::ERR_ABORTED'
        ? report.canceledRequests : report.failedRequests;
      if (entries.length < 40) entries.push({ stage: currentStage,
        method: request.method(), path: new URL(request.url()).pathname.replace(/[0-9a-f]{8}-[0-9a-f-]{27,}/gi, ':id').slice(0, 160) });
    });
    page.on('response', async response => {
      if (response.request().method() === 'POST' && new URL(response.url()).pathname ===
        `/api/local/friend/${input.profileId}/shared-world/hosting/start`) {
        const result = await response.json().catch(() => null);
        hostingStartResults.push({ status: response.status(), ok: result?.ok === true });
      }
      if (response.status() >= 400 && report.failedRequests.length < 40) report.failedRequests.push({ stage: currentStage,
        status: response.status(), method: response.request().method(),
        path: new URL(response.url()).pathname.replace(/[0-9a-f]{8}-[0-9a-f-]{27,}/gi, ':id').slice(0, 160) });
      if (response.url().endsWith('/api/local/settings/control-policy') && response.request().method() === 'PUT') {
        const result = await response.json().catch(() => null);
        report.controlPolicyChanges.push({ status: response.status(), ok: result?.ok === true,
          code: /^[A-Za-z0-9]{1,80}$/.test(result?.code ?? '') ? result.code : 'Unavailable',
          listening: result?.snapshot?.settings?.companionListeningEnabled === true });
      }
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
    await shot(host, '02-owner-approval'); await shot(friend, '02-paired-friend');
  });
  await stage('shared hosting grant and private-copy consent', async () => {
    await ownerOverview(host); await nav(host, 'Backups'); await nav(host, 'Sharing & hosting');
    await checked(host, 'Allow shared hosting');
    await checked(host, /^Can host this save for /);
    await sharedSaves(friend);
    await checked(friend, /Allow saves on this PC/);
    await shot(host, '03-host-sharing'); await shot(friend, '03-friend-consent');
  });
  await stage('hosting permission withdrawal blocks the action', async () => {
    const grantPath = new RegExp(`^/api/local/devices/[0-9a-f-]{36}/shared-world/${input.profileId}/grants$`, 'i');
    await choice(host, /^Can host this save for /, false, grantPath);
    await nav(friend, 'Play');
    await until(() => read(friend, `/api/local/friend/${input.profileId}/shared-world/hosting`),
      value => !value.allowed && !value.readyForManualStart && !value.running, 'withdrawn hosting permission');
    await until(() => visible(friend.getByRole('button', { name: 'Host on this PC', exact: true })).count(),
      count => count === 0, 'removed hosting action');
    if (hostingStartRequests) throw new Error('Removing hosting permission dispatched Start');
    report.failurePathCoverage = { hostingGrantWithdrawal: false, grantWithdrawalObserved: true,
      grantRestoredByOwner: false, authenticatedRecovery: false, signedGrantRefreshed: false,
      recoveryStep: 'restoring-owner-grant', automaticRecoveryBudgetMs: timeout, explicitRecoveryUsed: false };
    await shot(friend, '03-hosting-permission-removed');
    await checked(host, /^Can host this save for /, grantPath);
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
    // Keep the status surface mounted until its read and automatic signed-roster
    // catchup finish. Immediate navigation used to abort this observation.
    report.failurePathCoverage.recoveryStep = 'waiting-for-shared-save-read';
    const refreshed = friend.waitForResponse(response => response.request().method() === 'GET' &&
      new URL(response.url()).pathname === `/api/local/friend/${input.profileId}/shared-world`, { timeout });
    await sharedSaves(friend);
    const refreshedResponse = await refreshed;
    const result = await refreshedResponse.json();
    if (!refreshedResponse.ok() || result.consented !== true) throw new Error('Restoring the grant lost the saved local consent');
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
    report.failurePathCoverage.hostingGrantWithdrawal = true;
    report.failurePathCoverage.recoveryStep = 'complete';
  });
  await stage('local save consent is required and cancel starts nothing', async () => {
    await sharedSaves(friend);
    await choice(friend, /Allow saves on this PC/, false);
    await until(() => read(friend, `/api/local/friend/${input.profileId}/shared-world`),
      value => value.consented === false, 'local save consent withdrawn');
    await nav(friend, 'Play'); await button(friend, 'Host on this PC');
    const dialog = friend.getByRole('dialog', { name: 'Host on this PC', exact: true });
    await dialog.waitFor({ state: 'visible', timeout });
    const consent = dialog.getByRole('checkbox', { name: /Keep this save on my PC and host my own copy/ });
    if (await consent.isChecked() || await dialog.getByRole('button', { name: 'Continue and start', exact: true }).isEnabled())
      throw new Error('Hosting did not require this PC\'s explicit save consent');
    await shot(friend, '03-explicit-local-consent-required');
    await dialog.getByRole('button', { name: 'Close Host on this PC', exact: true }).click();
    if (hostingStartRequests) throw new Error('Canceling local consent dispatched Start');
    await sharedSaves(friend); await checked(friend, /Allow saves on this PC/);
    report.failurePathCoverage.localConsentAndCancel = true;
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
    await sharedSaves(friend);
    await optional(friend, 'Check latest');
    const getSave = visible(friend.getByRole('button', { name: 'Get latest save', exact: true }));
    if (await getSave.count() && await getSave.isEnabled()) await getSave.click();
    await until(() => read(friend, `/api/local/friend/${input.profileId}/shared-world`),
      value => value.consented === true && value.thisPcVersion >= 1 && value.receiptConfirmed === true, 'verified received copy ground truth');
    await shot(friend, '05-verified-copy');
  });
  await stage('guided hosting connection repair and single explicit Start', async () => {
    await nav(friend, 'Play'); await button(friend, 'Host on this PC');
    let dialog = friend.getByRole('dialog', { name: 'Host on this PC', exact: true });
    await dialog.waitFor();
    const server = dialog.getByLabel('Installed server file', { exact: true });
    if (await server.count()) await server.fill(input.fixturePath);
    const consent = dialog.getByRole('checkbox', { name: /Keep this save on my PC and host my own copy/ });
    if (await consent.count()) await consent.check();
    const firstCheck = friend.waitForResponse(response => response.request().method() === 'POST' &&
      new URL(response.url()).pathname === `/api/local/friend/${input.profileId}/shared-world/hosting/prepare`);
    await button(friend, 'Continue and start');
    const blocked = await (await firstCheck).json();
    if (blocked.ok || blocked.code !== 'LocalConnectionSetupRequired' || blocked.remedy?.kind !== 'Connection')
      throw new Error('The disabled incoming connection did not produce its exact actionable repair');
    const repair = dialog.getByRole('button', { name: 'Connection help', exact: true });
    await repair.waitFor({ timeout });
    await until(() => repair.isEnabled(), Boolean, 'connection repair ready to use');
    if (hostingStartRequests !== 0) throw new Error('Connection repair dispatched Start before its checks passed');
    await shot(friend, '06-actionable-connection-repair');
    await button(friend, 'Connection help');
    await button(friend, 'Manage access'); await checked(friend, /Allow Friend app connections/);
    await nav(friend, 'Join'); await nav(friend, 'Play'); await button(friend, 'Host on this PC');
    dialog = friend.getByRole('dialog', { name: 'Host on this PC', exact: true });
    await dialog.waitFor({ state: 'visible', timeout });
    {
      const file = dialog.getByLabel('Installed server file', { exact: true });
      await file.waitFor({ state: 'visible', timeout });
      await file.fill(input.fixturePath);
      const allow = dialog.getByRole('checkbox', { name: /Keep this save on my PC and host my own copy/ });
      if (await allow.count()) await allow.check();
      await button(friend, 'Continue and start');
    }
    const active = await until(() => read(friend), snapshot => {
      const running = snapshot.runs?.filter(run => ['Starting', 'Process running', 'Listening', 'Ready'].includes(run.state));
      return running?.length === 1 && running[0].state === 'Process running' && running[0].runOperationId;
    }, 'one candidate fixture with exact running identity');
    const run = active.runs.find(item => item.state === 'Process running');
    const profile = active.settings.profiles.find(item => item.id === run.profileId);
    if (!profile || profile.separateCopySourceProfileId !== input.profileId || !profile.separateCopyBranchHash ||
      run.playerCountTrusted || run.onlinePlayers != null) throw new Error('Candidate run lost its separate source or truthful occupancy');
    const localGroups = await until(() => read(friend, '/api/local/world-peers'), groups => groups.some(group =>
      group.profileId === input.profileId && group.canHost && group.receiveConsented), 'candidate authorized world membership');
    const group = localGroups.find(item => item.profileId === input.profileId);
    candidate = { profileId: profile.id, runId: run.runOperationId, branchHash: profile.separateCopyBranchHash,
      deviceId: group.selfDeviceId, groupKey: group.groupKey, root: group.state.root,
      endpoint: active.settings.companionEndpoint.replace(/\/$/, '') };
    await until(async () => hostingStartResults, results => results.length === 1 && results[0].ok && results[0].status < 400,
      'one successful guided Start response');
    if (hostingStartRequests !== 1) throw new Error('Guided hosting dispatched duplicate Start requests');
    report.guidedHosting = { exactRunningSession: true, separateBranch: true, truthfulUnknownPlayers: true,
      startRequests: hostingStartRequests, successfulStartResponses: hostingStartResults.filter(result => result.ok).length };
    await nav(friend, 'Overview');
    if (active.runs.filter(run => run.state === 'Ready').length) throw new Error('Fixture falsely reports game readiness');
    await visible(friend.getByRole('button', { name: 'Stop server', exact: true })).waitFor({ state: 'visible', timeout });
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
    const connecting = host.waitForRequest(request => request.method() === 'POST' && new URL(request.url()).pathname ===
      `/api/local/world-peers/${candidate.groupKey}/connect`);
    await button(host, `Connect to PC ending ${candidate.deviceId.slice(-6).toUpperCase()}`);
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
      authorizedWithoutConflict: true, exactAdvertisedEndpoint: true, exactConnectSelection: true };
    await shot(host, '07-owner-connected-new-host');
    await friend.setViewportSize({ width: 430, height: 920 });
    await geometry(friend); await shot(friend, '07-narrow-candidate');
    await friend.setViewportSize({ width: 1440, height: 1000 });
  });
  await stage('candidate owner Stop', async () => {
    await nav(friend, 'Host'); await optional(friend, 'Return to selected server'); await nav(friend, 'Overview');
    await stop(friend); await shot(friend, '08-candidate-stopped');
  });
  if (report.errors.length || report.failedRequests.length) throw new Error('Browser errors, failed requests or forbidden native interactions were observed');
  report.ok = true;
} catch (error) {
  let detail = String(error?.message ?? error).split('\n').slice(0, 3).join(' ').slice(0, 1200);
  for (const text of privateText) detail = detail.replaceAll(text, '[private code]');
  report.diagnostic = detail;
  report.failure = { stage: currentStage, message: 'The named UI stage did not satisfy its assertions. Inspect the masked screenshots and bounded request diagnostics.' };
  for (let index = 0; index < pages.length; index++) await shot(pages[index], `failure-${index}`).catch(() => {});
} finally {
  if (browser) await browser.close().catch(() => {});
  await fs.writeFile(path.join(artifacts, 'report.json'), JSON.stringify(report, null, 2));
  process.stdout.write(`${JSON.stringify({ ok: report.ok, stages: report.stages.length, failedStage: report.failure?.stage })}\n`);
  process.exitCode = report.ok ? 0 : 1;
}
