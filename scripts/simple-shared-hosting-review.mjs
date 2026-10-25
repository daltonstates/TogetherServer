// Pure plan and assertions for the existing hosted-only renderer. Importing this
// module does not open a browser, write files or contact a network.
const play = ['Join sections', 'play', 'Play'];
const sharedSaves = ['Join sections', 'shared-saves', 'Shared saves'];
const backups = ['Selected server sections', 'backups', 'Backups'];
const sharing = { nav: 'World protection sections', button: 'Sharing & hosting' };
const dialog = { role: 'dialog', name: 'Host locally' };
const friend = (id, stage, extra = {}) => ({ id: `simple-hosting-${id}`, scenario: 'friend-offline', workspace: 'Join',
  fixture: { simpleHosting: stage }, section: play, steps: [{ button: 'Host locally' }], screen: dialog, ...extra });
const hiddenEntry = (id, stage, card = false) => friend(id, stage, { steps: [], hostingEntry: 'hidden',
  section: card ? sharedSaves : play, screen: card ? { css: '.friend-primary' } : { role: 'region', name: 'Play' } });
export const simpleHostingCases = [
  { id: 'simple-hosting-owner-off', scenario: 'host-offline', workspace: 'Host', section: backups,
    steps: [sharing], screen: { role: 'region', name: 'Shared hosting' } },
  { id: 'simple-hosting-owner-on', scenario: 'host-offline', workspace: 'Host', section: backups,
    steps: [sharing, { role: 'checkbox', name: 'Allow friends to host this server' }], screen: { role: 'region', name: 'Shared hosting' } },
  { id: 'simple-hosting-owner-continuation', scenario: 'host-offline', fixture: { ownerContinuations: true }, workspace: 'Host',
    section: backups, steps: [sharing], screen: { role: 'region', name: 'Shared hosting' } },
  hiddenEntry('not-allowed', 'not-allowed'),
  hiddenEntry('not-allowed-card', 'not-allowed', true),
  hiddenEntry('permission-unknown-entry', 'permission-unknown'),
  hiddenEntry('permission-unknown-card', 'permission-unknown', true),
  hiddenEntry('permission-review-entry', 'permission-review'),
  hiddenEntry('permission-review-card', 'permission-review', true),
  friend('entry', 'prepare', { steps: [], hostingEntry: 'visible', screen: { role: 'region', name: 'Play' } }),
  friend('card-entry', 'prepare', { steps: [], hostingEntry: 'visible', section: sharedSaves,
    screen: { role: 'region', name: 'Host locally' } }),
  friend('missing-install-entry', 'missing-server', { steps: [], hostingEntry: 'visible', screen: { role: 'region', name: 'Play' } }),
  friend('play-ready', 'prepare', { scenario: 'friend-connected', steps: [], hostingEntry: 'visible', screen: { role: 'region', name: 'Play' } }),
  friend('network-warning', 'prepare'),
  friend('guide-save-preparation', 'prepare'),
  friend('missing-install', 'missing-server'),
  friend('missing-connection', 'connection', { steps: [{ button: 'Host locally' },
    { button: 'Set up Connections from other PCs', within: 'dialog[open]' }] }),
  friend('save-conflict', 'conflict'),
  friend('ready-confirmation', 'ready'),
  friend('starting', 'ready', { steps: [{ button: 'Host locally' }, { button: 'Start hosting', within: 'dialog[open]' }],
    screen: { css: '.server-command-header', text: 'Starting' } }),
  friend('ready-explicit-start', 'ready', { readyStartJourney: true, screen: { css: 'main.workspace-main' } }),
  friend('ready-start-timeout', 'ready', { readyStartTimeout: true,
    fixture: { simpleHosting: 'ready', hostingStartPending: true } }),
  friend('guide-permission-unknown', 'permission-unknown-after-entry'),
  friend('guide-permission-denied', 'permission-denied-after-entry'),
  friend('guide-ports', 'ports', { steps: [{ button: 'Host locally' },
    { button: 'Set up Local game ports', within: 'dialog[open]' }] }),
  friend('reopen-entry', 'permission-review-after-entry'),
  friend('stale-retry', 'prepare'),
  friend('recovered-retry', 'prepare'),
  friend('directory-removed', 'prepare', { fixture: { simpleHosting: 'prepare', removedHost: true },
    steps: [], screen: { role: 'region', name: 'Servers' } }),
  ...['Receiving', 'Verifying'].map(phase => friend(`progress-${phase.toLowerCase()}`, 'prepare', {
    fixture: { simpleHosting: 'prepare', hostingProgress: phase }, pendingState: true,
    steps: [{ button: 'Host locally' }, { button: 'Host locally', within: 'dialog[open]' }],
    screen: { role: 'region', name: 'Hosting progress' } })),
  ...['Running', 'Stopped'].map(session => ({ id: `simple-hosting-directory-remote-${session.toLowerCase()}`,
    scenario: 'host-offline', workspace: 'Host', fixture: { directoryRemote: session },
    steps: [], screen: { role: 'region', name: 'Servers' } })),
];
export const simpleHostingVariants = [
  { id: 'darkdesktop', viewport: { width: 1280, height: 900 }, theme: 'dark', scale: 100 },
  { id: 'lightnarrow', viewport: { width: 390, height: 844 }, theme: 'light', scale: 100 },
  { id: 'lightnarrow150', viewport: { width: 390, height: 844 }, theme: 'light', scale: 150 },
];

const permissionCodes = { 'not-allowed': 'HostingPermissionRequired', 'permission-unknown': 'RosterUnavailable',
  'permission-review': 'RosterRollback', 'permission-denied': 'HostingPermissionRequired' };
const permissionMessages = {
  HostingPermissionRequired: 'The owner has not allowed this PC to host this synthetic server.',
  RosterUnavailable: 'This synthetic PC could not check the Host\'s sharing permission. Keep the Host reachable and check again.',
  RosterRollback: 'This synthetic PC\'s saved sharing history needs review. Keep both apps open and check hosting access again.'
};
export function simpleHostingPermission(state, boundary) {
  // An already-open guide can learn that access changed after its positive
  // entry lookup. Initial denied and unknown lookups remain fail-closed.
  const code = state.simpleHosting?.endsWith('-after-entry') ? null : permissionCodes[state.simpleHosting];
  return { allowed: !code, code: code ?? 'HostingAllowed',
    message: code ? `${permissionMessages[code]} ${boundary}`.trim() : boundary };
}

export function simpleHostingStatus(state, profileId, boundary) {
  const stage = state.simpleHosting?.replace(/-after-entry$/u, '');
  const prepared = state.simpleHostingPrepared || stage === 'ready';
  const allowed = !permissionCodes[stage];
  const missing = stage === 'missing-server';
  const connection = stage === 'connection';
  const conflict = stage === 'conflict';
  const ports = stage === 'ports';
  return { ok: allowed && prepared && !missing && !connection && !conflict && !ports,
    code: permissionCodes[stage] ?? (missing ? 'InstalledServerRequired' : connection ? 'LocalConnectionSetupRequired' : ports ? 'PortConflict' :
      conflict ? 'HostingHistoryChoiceRequired' : prepared ? 'LocalHostingPrepared' : 'LocalHostingPreparationRequired'),
    message: boundary, allowed, consented: prepared || stage === 'prepare', game: 'Valheim', name: 'Weekend Valheim', needsServerFile: missing,
    needsPassword: false, serverFile: missing ? null : 'C:\\TogetherServer-Synthetic\\valheim_server.exe',
    gameVersion: 'Synthetic', gamePort: 2456, controlPort: 5131, localProfileId: prepared ? profileId : null,
    branchHash: prepared ? 'A'.repeat(64) : null, readyForManualStart: allowed && prepared && !connection && !missing && !conflict && !ports,
    running: false, steps: ['permission', 'server', 'save', 'ports', 'connection'].map(key => ({ key,
      state: key === 'permission' && ['permission-unknown', 'permission-review'].includes(stage) ? 'Unknown' :
        key === 'permission' && !allowed || key === 'server' && missing || key === 'ports' && ports || key === 'connection' && connection ? 'ActionRequired' :
        key === 'save' && !prepared ? 'Unknown' : 'Ready', message: boundary })) };
}

const checkNames = ['Permission to host', 'Server app and tools', 'Save file checks', 'Local game ports', 'Connections from other PCs'];
const hostingWrites = state => state.requests.filter(item => item.method !== 'GET' && /shared-world\/hosting(?:\/|$)/u.test(item.path));

export function assertPreparedHostingRequests(state, expectedStarts) {
  const writes = hostingWrites(state);
  if (writes.length !== expectedStarts || writes.some(item => item.method !== 'POST' || !item.path.endsWith('/hosting/start')))
    throw new Error('A checked prepared copy must use only the expected explicit Start request without preparation or replay');
  return { startRequests: writes.length, prepareRequests: 0 };
}

async function assertVisibleChecks(modal, record) {
  const checks = modal.getByRole('list', { name: 'Local hosting checks', exact: true });
  await checks.waitFor({ state: 'visible', timeout: 8000 });
  if (await checks.getByRole('listitem').count() !== 5 || await checks.evaluate(element => !!element.closest('details')))
    throw new Error('All five hosting checks must remain visible without a disclosure');
  for (const name of checkNames) {
    if (!await checks.getByText(name, { exact: true }).isVisible() ||
        await checks.getByRole('button', { name: new RegExp(`^(?:Set up|Review) ${name}$`, 'u') }).count() !== 1)
      throw new Error('Each hosting check needs its visible result and setup or review action');
  }
  const checked = modal.locator('.local-hosting-check-time time');
  await checked.waitFor({ state: 'visible', timeout: 8000 });
  const dateTime = await checked.getAttribute('datetime');
  if (!Number.isFinite(Date.parse(dateTime ?? '')) || !/Last checked/u.test(await checked.textContent()))
    throw new Error('The hosting results must show their last successful check time');
  const result = modal.getByRole('status', { name: 'Last check result', exact: true });
  if (!await result.isVisible() || (await result.locator('strong').textContent() ?? '').trim().length < 5 ||
      (await result.locator('p').textContent() ?? '').trim().length < 10 ||
      await checks.locator('.local-hosting-check-next').count() !== 5)
    throw new Error('The completed hosting check needs its visible result and next action');
  if (await modal.getByRole('region', { name: 'Local hosting setup guide', exact: true }).count() !== 1)
    throw new Error('The hosting checks need one local setup guide');
  record.assertions.push({ persistentHostingChecks: 5, lastCheckedUtc: dateTime, disclosureRequired: false });
  return { checks, dateTime };
}

export function assertRemovedAccessBounds({ label, checkbox, text, display }) {
  if (!label || !checkbox || !text || display !== 'flex' || label.height < 36 ||
      checkbox.width < 14 || checkbox.width > 24 || text.x < checkbox.x + checkbox.width + 4 ||
      Math.abs(checkbox.y + checkbox.height / 2 - text.y - text.height / 2) > 2)
    throw new Error('Removed-access checkbox and text must share an inline row with a full-height label target');
}

export async function assertSimpleHostingUi(page, testCase, record, state) {
  if (!testCase.id.startsWith('simple-hosting-')) return;
  const writes = hostingWrites(state);
  if (testCase.fixture?.hostingProgress) {
    const modal = page.getByRole('dialog', { name: 'Host locally', exact: true });
    const live = modal.getByRole('region', { name: 'Hosting progress', exact: true });
    const phase = testCase.fixture.hostingProgress;
    await live.waitFor({ state: 'visible', timeout: 8000 });
    if (phase === 'Receiving') {
      await live.getByText('25% received', { exact: true }).waitFor({ state: 'visible', timeout: 8000 });
      state.hostingProgressReceivedBytes = 3145728;
      await live.getByText('75% received', { exact: true }).waitFor({ state: 'visible', timeout: 8000 });
      const bytes = live.getByRole('progressbar', { name: 'Shared save bytes received', exact: true });
      if (await bytes.getAttribute('value') !== '3145728' || await bytes.getAttribute('max') !== '4194304' ||
          !await live.getByText('3 MiB of 4 MiB', { exact: true }).isVisible())
        throw new Error('Hosting progress must update using the current completed/total transfer bytes');
    } else {
      const stage = live.getByRole('progressbar', { name: 'Verifying the save files', exact: true });
      await stage.waitFor({ state: 'visible', timeout: 8000 });
      if (await stage.getAttribute('aria-valuenow') !== null || await live.getByText(/\d+%/u).count())
        throw new Error('Verification has no measured denominator and must stay indeterminate');
    }
    if (!await live.locator('.local-hosting-elapsed').isVisible() || writes.length !== 1 || !writes[0].path.endsWith('/prepare') ||
        !state.requests.some(item => item.method === 'GET' && item.path.endsWith('/hosting/progress')) || state.simpleHostingStarted)
      throw new Error('Live progress needs elapsed feedback and passive progress reads during the one pending preparation');
    record.assertions.push({ liveHostingPhase: phase, realTransferBytes: phase === 'Receiving' ? [1048576, 3145728, 4194304] : null,
      fabricatedAggregatePercent: false, progressReadOnly: true });
  } else if (testCase.fixture?.directoryRemote) {
    const directory = page.getByRole('region', { name: 'Servers', exact: true });
    const world = directory.getByRole('button', { name: 'Open Weekend Valheim on this PC', exact: true });
    await world.waitFor({ state: 'visible', timeout: 8000 });
    const running = testCase.fixture.directoryRemote === 'Running';
    if (await world.count() !== 1 || await directory.getByRole('heading', { level: 4, name: 'Remote friend', exact: true }).count() ||
        await directory.getByRole('button', { name: /Remote continuation/u }).count() ||
        await world.getByText('Hosted by Remote friend', { exact: true }).count() !== Number(running))
      throw new Error('A shared world needs one original owner row with the remote-host badge only while hosting');
    if (running && (state.mode !== 'Friend' || state.friend.profiles[0]?.id !== state.directoryRemoteProfileId))
      throw new Error('The original world row must open the actual successor-PC profile');
    if (!running && state.mode !== 'Host') throw new Error('A stopped successor must leave the original local world selected');
    record.assertions.push({ ownerWorldRows: 1, successorSidebarRows: 0, actualSuccessorControls: running, stoppedSuccessorHidden: !running });
  } else if (testCase.id.includes('-owner-')) {
    const workspace = page.locator('.shared-world-workspace');
    if (await workspace.getByRole('checkbox').count() !== 1) throw new Error('Host sharing must have exactly one switch');
    if (await workspace.getByRole('button', { name: /Manage sharing|Move hosting|Approve selected copy/u }).count())
      throw new Error('Legacy sharing management is still reachable');
    const toggled = testCase.id.endsWith('-on');
    const enabled = toggled || testCase.fixture?.ownerContinuations === true;
    if (enabled) await page.waitForFunction(() => {
      const checkbox = document.querySelector('.shared-world-workspace .host-sharing-master-toggle input[type="checkbox"]');
      return checkbox?.checked === true && checkbox.disabled === false;
    }, null, { timeout: 8000 });
    if (await workspace.getByRole('checkbox').isChecked() !== enabled || enabled && !await workspace.getByRole('checkbox').isEnabled() ||
        writes.length !== Number(toggled))
      throw new Error('Host switch did not retain the exact requested state');
    if (testCase.fixture?.ownerContinuations) {
      const original = state.host.settings.profiles.find(profile => !profile.separateCopySourceProfileId);
      // Overview may read the active continuation's own saved status before
      // Sharing opens. The policy editor is identified by its roster read.
      const policyReads = state.requests.filter(request => request.method === 'GET' &&
        request.path.endsWith('/shared-world/governance'));
      const policyBase = original && `/api/local/profiles/${original.id}/shared-world`;
      if (!policyBase || !policyReads.length || policyReads.some(request => request.path !== `${policyBase}/governance`) ||
          !state.requests.some(request => request.method === 'GET' && request.path === policyBase))
        throw new Error('Owner continuation sharing must use its exact original policy');
      if (await page.locator('.server-directory button[aria-label="Open Weekend Valheim on this PC"]').count() !== 1 ||
          await page.locator('.server-directory').getByRole('heading', { name: 'Hosting for friends', exact: true }).count() ||
          await page.locator('.guest-hosting-label').count())
        throw new Error('Owner continuation exposed retained copies or a guest-hosting label');
    }
  } else if (testCase.id.endsWith('-directory-removed')) {
    const directory = page.getByRole('region', { name: 'Servers', exact: true });
    const checkbox = directory.getByRole('checkbox', { name: 'Show removed access', exact: true });
    await checkbox.waitFor({ state: 'visible', timeout: 8000 });
    const label = directory.locator('label.server-directory-removed');
    const text = label.locator('span');
    const bounds = { label: await label.boundingBox(), checkbox: await checkbox.boundingBox(), text: await text.boundingBox(),
      display: await label.evaluate(element => getComputedStyle(element).display) };
    assertRemovedAccessBounds(bounds);
    const removedHost = directory.getByRole('heading', { level: 4, name: /^Removed friend\s*Access removed$/u });
    if (await checkbox.isChecked() || await removedHost.count())
      throw new Error('Removed Host access must start hidden');
    await text.click();
    await removedHost.waitFor({ state: 'visible', timeout: 8000 });
    if (await removedHost.count() !== 1 || !await checkbox.isChecked() || hostingWrites(state).length)
      throw new Error('Clicking the removed-access label must reveal the saved Host without a hosting action');
    record.assertions.push({ removedAccessInline: bounds, clickableLabelRevealedRemovedHost: true });
  } else if (testCase.hostingEntry === 'hidden') {
    const card = testCase.section === sharedSaves;
    const scope = card ? page.locator('.friend-primary') : page.getByRole('region', { name: 'Play', exact: true });
    const permission = simpleHostingPermission(state, '');
    const access = scope.getByRole('status', { name: 'Hosting access', exact: true });
    const accessPath = `/api/local/friend/${state.friend.profiles[0]?.id}/shared-world/hosting/access`;
    const assertHidden = async () => {
      if (!state.requests.some(item => item.method === 'GET' && item.path === accessPath) ||
        state.requests.some(item => /shared-world\/hosting(?:\/|$)/u.test(item.path) &&
          (item.method !== 'GET' || item.path !== accessPath)) ||
        await page.getByRole('button', { name: 'Host locally', exact: true }).count() ||
        await page.getByRole('region', { name: 'Host locally', exact: true }).count() ||
        await page.getByRole('dialog', { name: 'Host locally', exact: true }).count() ||
        await page.getByRole('list', { name: 'Local hosting checks', exact: true }).count() || hostingWrites(state).length ||
        state.requests.some(item => item.method === 'GET' && item.path.endsWith('/shared-world/hosting')))
        throw new Error('Denied or unknown permission must hide the hosting launcher without opening checks or writing state');
    };
    await page.getByRole('button', { name: 'Host locally', exact: true }).waitFor({ state: 'hidden', timeout: 8000 });
    await access.waitFor({ state: 'visible', timeout: 8000 });
    if (await access.count() !== 1 || !await access.isVisible())
      throw new Error('Denied or unknown permission must show one scoped Hosting access status');
    await access.getByText(permission.message, { exact: false }).waitFor({ state: 'visible', timeout: 8000 });
    await assertHidden();
    const retry = scope.getByRole('button', { name: 'Check hosting access again', exact: true });
    const retryExpected = permission.code !== 'HostingPermissionRequired';
    if (await retry.count() !== Number(retryExpected) || retryExpected && !await retry.isEnabled())
      throw new Error('Unknown or review-required hosting access needs an enabled read-only retry; a confirmed denial needs its explanation');
    if (retryExpected) {
      const reads = state.requests.filter(item => item.method === 'GET' && item.path === accessPath).length;
      const retried = page.waitForResponse(response => response.url().endsWith(accessPath) &&
        response.request().method() === 'GET' && response.ok(), { timeout: 8000 });
      await retry.click();
      const result = await (await retried).json();
      if (result.allowed !== false || result.code !== permission.code ||
          state.requests.filter(item => item.method === 'GET' && item.path === accessPath).length <= reads)
        throw new Error('A hosting access retry must read the current scoped permission without granting access');
      await access.getByText(permission.message, { exact: false }).waitFor({ state: 'visible', timeout: 8000 });
      await assertHidden();
    }
    record.assertions.push({ hostingEntryHidden: true, hostingAccessStatusVisible: true, hostingAccessRetry: retryExpected,
      permissionCode: permission.code, presentation: card ? 'card' : 'action', accessReadOnly: true });
  } else if (testCase.hostingEntry === 'visible') {
    const card = testCase.section === sharedSaves;
    const entryRegion = page.getByRole('region', { name: card ? 'Host locally' : 'Play', exact: true });
    const entry = entryRegion.getByRole('button', { name: 'Host locally', exact: true });
    await entry.waitFor({ state: 'visible', timeout: 8000 });
    if (!/Host locally/u.test(await entry.textContent()) || writes.length ||
        state.requests.some(item => item.method === 'GET' && item.path.endsWith('/shared-world/hosting')) ||
        await page.getByRole('dialog', { name: 'Host locally', exact: true }).count() ||
        await page.getByText('Host decision not checked', { exact: true }).count())
      throw new Error('The labeled hosting entry must appear before setup checks or diagnostic clutter');
    if (await page.locator('.local-hosting-card').count() !== Number(card))
      throw new Error('Confirmed hosting access must show exactly its requested entry presentation');
    if (testCase.id.endsWith('-play-ready')) {
      const open = await entryRegion.getByRole('button', { name: 'Open game', exact: true }).boundingBox();
      const stop = await entryRegion.getByRole('button', { name: 'Stop server', exact: true }).boundingBox();
      const styles = {};
      for (const name of ['Open game', 'Stop server']) styles[name] = await entryRegion.getByRole('button', { name, exact: true }).evaluate(element => {
        const style = getComputedStyle(element);
        return { classes: element.className, minHeight: style.minHeight, minBlockSize: style.minBlockSize,
          fontSize: style.fontSize, padding: style.padding, transform: style.transform };
      });
      record.assertions.push({ playActionSizes: { open, stop }, styles });
      if (!open || !stop || Math.abs(open.width - stop.width) > 1 || Math.abs(open.height - stop.height) > 1 ||
          await entryRegion.getByText('Asks Steam to open an installed game. Join the server from the game.', { exact: true }).count())
        throw new Error('Open game and Stop server must have equal dimensions without the intervening helper text');
    }
  } else if (testCase.readyStartJourney || testCase.readyStartTimeout) {
    const modal = page.getByRole('dialog', { name: 'Host locally', exact: true });
    await assertVisibleChecks(modal, record);
    const start = modal.getByRole('button', { name: 'Start hosting', exact: true });
    await start.waitFor({ state: 'visible', timeout: 8000 });
    if (!await start.isEnabled() || await modal.getByRole('button', { name: 'Retry and start', exact: true }).count())
      throw new Error('A checked prepared copy needs the explicit enabled Start hosting action');
    assertPreparedHostingRequests(state, 0);
    const refreshed = page.waitForResponse(response => response.url().endsWith('/shared-world/hosting') &&
      response.request().method() === 'GET', { timeout: 8000 });
    await modal.getByRole('button', { name: 'Retry automatic checks', exact: true }).click();
    await refreshed;
    await page.waitForFunction(button => !button.disabled, await start.elementHandle(), { timeout: 8000 });
    assertPreparedHostingRequests(state, 0);
    record.assertions.push({ preparedOpeningReadOnly: true, preparedRefreshReadOnly: true });
    if (testCase.readyStartTimeout) {
      await page.clock.install();
      await start.click();
      await modal.getByRole('button', { name: 'Requesting Start...', exact: true }).waitFor({ state: 'visible', timeout: 8000 });
      assertPreparedHostingRequests(state, 1);
      await page.clock.fastForward(120001);
      const failure = modal.getByRole('alert');
      await failure.waitFor({ state: 'visible', timeout: 8000 });
      if (!/The Start result could not be checked in time/u.test(await failure.textContent() ?? ''))
        throw new Error('The pending prepared-copy Start did not expose its timeout result');
      await page.clock.fastForward(30001);
      assertPreparedHostingRequests(state, 1);
      const checkedAgain = page.waitForResponse(response => response.url().endsWith('/shared-world/hosting') &&
        response.request().method() === 'GET', { timeout: 8000 });
      await modal.getByRole('button', { name: 'Retry automatic checks', exact: true }).click();
      await checkedAgain;
      await page.waitForFunction(button => !button.disabled, await start.elementHandle(), { timeout: 8000 });
      assertPreparedHostingRequests(state, 1);
      await modal.getByRole('button', { name: 'Close Host locally', exact: true }).click();
      await modal.waitFor({ state: 'hidden', timeout: 8000 });
      await page.getByRole('region', { name: 'Play', exact: true }).getByRole('button', { name: 'Host locally', exact: true }).click();
      await start.waitFor({ state: 'visible', timeout: 8000 });
      await page.waitForFunction(button => !button.disabled, await start.elementHandle(), { timeout: 8000 });
      await page.clock.fastForward(30001);
      assertPreparedHostingRequests(state, 1);
      if (state.simpleHostingStarted) throw new Error('A timed-out synthetic Start must not claim a running server');
      await page.clock.resume();
      record.assertions.push({ preparedStartTimedOut: true, timeoutAndNavigationReplay: false,
        ...assertPreparedHostingRequests(state, 1), evidence: 'Synthetic pending Start and browser clock only' });
    } else {
      const started = page.waitForResponse(response => response.url().endsWith('/shared-world/hosting/start') &&
        response.request().method() === 'POST', { timeout: 8000 });
      await start.click();
      const result = await (await started).json();
      await modal.waitFor({ state: 'hidden', timeout: 8000 });
      await page.locator('.server-command-header').getByText('Starting', { exact: true }).waitFor({ state: 'visible', timeout: 8000 });
      if (!result.ok || !state.simpleHostingStarted) throw new Error('The explicit prepared-copy Start did not reach the synthetic Starting state');
      record.assertions.push({ preparedExplicitStart: true, ...assertPreparedHostingRequests(state, 1) });
    }
  } else if (testCase.id.endsWith('-starting')) {
    assertPreparedHostingRequests(state, 1);
    if (!state.simpleHostingStarted) throw new Error('The explicit prepared-copy Start did not start exactly once');
  } else {
    const modal = page.getByRole('dialog', { name: 'Host locally', exact: true });
    const checked = await assertVisibleChecks(modal, record);
    if (await modal.locator('.local-hosting-warning').count() !== 1 || await modal.getByRole('checkbox').count())
      throw new Error('The Friend flow must contain one network warning and no save-permission checkbox');
    if (await modal.locator('.local-hosting-advanced').count() || writes.length)
      throw new Error('Opening automatic checks must not expose branch setup or mutate hosting');
    const primaryName = simpleHostingStatus(state, state.friend.profiles[0]?.id, '').readyForManualStart ? 'Start hosting' : 'Host locally';
    if (testCase.id.endsWith('-ready-confirmation')) {
      if (!await modal.getByRole('button', { name: primaryName, exact: true }).isEnabled() || primaryName !== 'Start hosting')
        throw new Error('A ready confirmation must offer explicit Start without repeating preparation');
      record.assertions.push({ readyConfirmation: true, ...assertPreparedHostingRequests(state, 0) });
    }
    if (testCase.id.endsWith('-save-conflict') && await modal.getByRole('button', { name: primaryName, exact: true }).isEnabled())
      throw new Error('Divergent save histories must block Start');
    if (testCase.id.endsWith('-reopen-entry')) {
      state.simpleHosting = 'permission-review';
      await modal.getByRole('button', { name: 'Close Host locally', exact: true }).click();
      await modal.waitFor({ state: 'hidden', timeout: 8000 });
      const entry = page.getByRole('region', { name: 'Play', exact: true }).getByRole('button', { name: 'Host locally', exact: true });
      await entry.waitFor({ state: 'hidden', timeout: 8000 });
      if (await entry.count()) throw new Error('Closing a permission review must hide the entry until access is confirmed again');
      const accessRestored = page.waitForResponse(async response => response.url().endsWith('/shared-world/hosting/access') &&
        response.request().method() === 'GET' && response.ok() && (await response.json()).allowed === true, { timeout: 15000 });
      state.simpleHosting = 'prepare';
      await accessRestored;
      await entry.waitFor({ state: 'visible', timeout: 8000 });
      state.simpleHosting = 'permission-review-after-entry';
      const reads = state.requests.filter(item => item.method === 'GET' && item.path.endsWith('/shared-world/hosting')).length;
      const refreshed = page.waitForResponse(response => response.url().endsWith('/shared-world/hosting') &&
        response.request().method() === 'GET', { timeout: 8000 });
      await entry.click();
      await refreshed;
      await modal.waitFor({ state: 'visible', timeout: 8000 });
      await assertVisibleChecks(modal, record);
      if (state.requests.filter(item => item.method === 'GET' && item.path.endsWith('/shared-world/hosting')).length <= reads ||
          await modal.getByRole('button', { name: primaryName, exact: true }).isEnabled())
        throw new Error('Reopening a review failure must read current checks while keeping Start blocked');
      record.assertions.push({ hiddenUntilPermissionRestored: true, reopenedAfterReviewFailure: true, startRemainsBlocked: true });
    }
    if (/-stale-retry$|-recovered-retry$/u.test(testCase.id)) {
      state.simpleHostingReadFailure = true;
      await modal.getByRole('button', { name: 'Retry automatic checks', exact: true }).click();
      const stale = modal.getByText('Showing the last results. Retry the checks before starting.', { exact: true });
      await stale.waitFor({ state: 'visible', timeout: 8000 });
      const retained = await assertVisibleChecks(modal, record);
      if (retained.dateTime !== checked.dateTime || await modal.getByRole('button', { name: primaryName, exact: true }).isEnabled())
        throw new Error('A failed retry must retain previous results and check time while blocking Start');
      if (testCase.id.endsWith('-recovered-retry')) {
        state.simpleHostingReadFailure = false;
        await modal.getByRole('button', { name: 'Retry automatic checks', exact: true }).click();
        await page.waitForFunction(button => !button.disabled,
          await modal.getByRole('button', { name: primaryName, exact: true }).elementHandle(), { timeout: 8000 });
        await stale.waitFor({ state: 'hidden', timeout: 8000 });
        await assertVisibleChecks(modal, record);
        if (!await modal.getByRole('button', { name: primaryName, exact: true }).isEnabled())
          throw new Error('A successful retry must restore the current-check confirmation');
      }
      record.assertions.push({ previousResultsRetainedOnFailure: true, recovered: testCase.id.endsWith('-recovered-retry') });
    }
    const guide = modal.getByRole('region', { name: 'Local hosting setup guide', exact: true });
    const guideExpectation = testCase.id.endsWith('-guide-permission-unknown') ? ['permission', 'Check the current hosting permission'] :
      testCase.id.endsWith('-guide-permission-denied') ? ['permission', 'Ask the owner to allow shared hosting'] :
      testCase.id.endsWith('-missing-install') ? ['server', 'Install or select the dedicated server'] :
      testCase.id.endsWith('-guide-save-preparation') ? ['save', 'Prepare this PC\'s hosting copy'] :
      testCase.id.endsWith('-save-conflict') ? ['save', 'Ask the owner to review different progress'] :
      testCase.id.endsWith('-guide-ports') ? ['ports', 'Free the game ports on this PC'] :
      testCase.id.endsWith('-missing-connection') ? ['connection', 'Set up connections to this PC'] : null;
    if (guideExpectation) {
      const [key, title] = guideExpectation;
      await guide.getByRole('heading', { name: title, exact: true }).waitFor({ state: 'visible', timeout: 8000 });
      if (await guide.locator('.local-hosting-guide-content').getAttribute('data-step') !== key)
        throw new Error('The hosting check did not open its corresponding setup guide');
      if (key === 'permission' && await modal.getByRole('button', { name: primaryName, exact: true }).isEnabled())
        throw new Error('The permission guide must not allow hosting through an unresolved or denied result');
      if (testCase.id.endsWith('-guide-save-preparation')) {
        if (await guide.getByText(/receives it if needed and prepares a separate local copy/u).count() !== 1 ||
            await modal.getByText(/interrupted transfer|stop the game server safely/iu).count() ||
            !await modal.getByRole('button', { name: primaryName, exact: true }).isEnabled())
          throw new Error('An unprepared hosting copy must explain preparation and retain explicit network confirmation without implying a failed transfer');
        record.assertions.push({ unpreparedCopyExplained: true, transferFailureImplied: false, networkConfirmationStillRequired: true });
      }
      record.assertions.push({ hostingGuideStep: key, heading: title });
    }
    if (hostingWrites(state).length) throw new Error('Checks, retry, guide navigation and reopening must not write hosting state');
  }
  record.assertions.push({ simpleSharedHosting: testCase.id, hostingWrites: hostingWrites(state), evidence: 'Synthetic UI behavior only' });
}
