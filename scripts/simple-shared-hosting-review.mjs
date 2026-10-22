// Pure plan and assertions for the existing hosted-only renderer. Importing this
// module does not open a browser, write files or contact a network.
const play = ['Join sections', 'play', 'Play'];
const backups = ['Selected server sections', 'backups', 'Backups'];
const sharing = { nav: 'World protection sections', button: 'Sharing & hosting' };
const dialog = { role: 'dialog', name: 'Host locally' };
const friend = (id, stage, extra = {}) => ({ id: `simple-hosting-${id}`, scenario: 'friend-offline', workspace: 'Join',
  fixture: { simpleHosting: stage }, section: play, steps: [{ button: 'Host locally' }], screen: dialog, ...extra });
export const simpleHostingCases = [
  { id: 'simple-hosting-owner-off', scenario: 'host-offline', workspace: 'Host', section: backups,
    steps: [sharing], screen: { role: 'region', name: 'Shared hosting' } },
  { id: 'simple-hosting-owner-on', scenario: 'host-offline', workspace: 'Host', section: backups,
    steps: [sharing, { role: 'checkbox', name: 'Allow friends to host this server' }], screen: { role: 'region', name: 'Shared hosting' } },
  { id: 'simple-hosting-owner-continuation', scenario: 'host-offline', fixture: { ownerContinuations: true }, workspace: 'Host',
    section: backups, steps: [sharing], screen: { role: 'region', name: 'Shared hosting' } },
  friend('not-allowed', 'not-allowed', { steps: [], screen: { role: 'region', name: 'Play' } }),
  friend('entry', 'prepare', { steps: [], screen: { role: 'region', name: 'Play' } }),
  friend('missing-install-entry', 'missing-server', { steps: [], screen: { role: 'region', name: 'Play' } }),
  friend('play-ready', 'prepare', { scenario: 'friend-connected', steps: [], screen: { role: 'region', name: 'Play' } }),
  friend('network-warning', 'prepare'),
  friend('missing-install', 'missing-server'),
  friend('missing-connection', 'connection', { steps: [{ button: 'Host locally' },
    { button: 'Set up Connections from other PCs', within: 'dialog[open]' }] }),
  friend('save-conflict', 'conflict'),
  friend('ready-confirmation', 'ready'),
  friend('starting', 'ready', { steps: [{ button: 'Host locally' }, { button: 'Host locally', within: 'dialog[open]' }],
    screen: { css: '.server-command-header', text: 'Starting' } }),
  friend('guide-permission-unknown', 'permission-unknown'),
  friend('guide-permission-denied', 'permission-denied'),
  friend('guide-ports', 'ports', { steps: [{ button: 'Host locally' },
    { button: 'Set up Local game ports', within: 'dialog[open]' }] }),
  friend('reopen-entry', 'permission-review'),
  friend('stale-retry', 'prepare'),
  friend('recovered-retry', 'prepare'),
  friend('directory-removed', 'prepare', { fixture: { simpleHosting: 'prepare', removedHost: true },
    steps: [], screen: { role: 'region', name: 'Play' } }),
];
export const simpleHostingVariants = [
  { id: 'darkdesktop', viewport: { width: 1280, height: 900 }, theme: 'dark', scale: 100 },
  { id: 'lightnarrow', viewport: { width: 390, height: 844 }, theme: 'light', scale: 100 },
  { id: 'lightnarrow150', viewport: { width: 390, height: 844 }, theme: 'light', scale: 150 },
];

const permissionCodes = { 'not-allowed': 'HostingPermissionRequired', 'permission-unknown': 'RosterUnavailable',
  'permission-review': 'RosterRollback', 'permission-denied': 'HostingPermissionRequired' };
export function simpleHostingPermission(state, boundary) {
  // The denied-dialog case represents permission changing between the entry
  // lookup and the full checks. Other denials retain their exact typed code.
  const code = state.simpleHosting === 'permission-denied' ? null : permissionCodes[state.simpleHosting];
  return { allowed: !code, code: code ?? 'HostingAllowed', message: boundary };
}

export function simpleHostingStatus(state, profileId, boundary) {
  const stage = state.simpleHosting;
  const prepared = state.simpleHostingPrepared || stage === 'ready';
  const allowed = !permissionCodes[stage];
  const missing = stage === 'missing-server';
  const connection = stage === 'connection';
  const conflict = stage === 'conflict';
  const ports = stage === 'ports';
  return { ok: allowed && !missing && !connection && !conflict && !ports,
    code: permissionCodes[stage] ?? (missing ? 'InstalledServerRequired' : connection ? 'LocalConnectionSetupRequired' : ports ? 'PortConflict' :
      conflict ? 'HostingHistoryChoiceRequired' : prepared ? 'LocalHostingPrepared' : 'NeedsPreparation'),
    message: boundary, allowed, consented: prepared, game: 'Valheim', name: 'Weekend Valheim', needsServerFile: missing,
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
  const checked = modal.locator('.local-hosting-results time');
  await checked.waitFor({ state: 'visible', timeout: 8000 });
  const dateTime = await checked.getAttribute('datetime');
  if (!Number.isFinite(Date.parse(dateTime ?? '')) || !/Last checked/u.test(await checked.textContent()))
    throw new Error('The hosting results must show their last successful check time');
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
  if (testCase.id.includes('-owner-')) {
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
  } else if (testCase.id.endsWith('-not-allowed')) {
    await page.getByRole('button', { name: 'Host locally', exact: true }).waitFor({ state: 'hidden', timeout: 8000 });
    if (await page.getByRole('button', { name: 'Host locally', exact: true }).count() || writes.length)
      throw new Error('An unauthorized Friend can see or invoke local hosting');
  } else if (['simple-hosting-entry', 'simple-hosting-missing-install-entry', 'simple-hosting-play-ready'].includes(testCase.id)) {
    const playRegion = page.getByRole('region', { name: 'Play', exact: true });
    const entry = playRegion.getByRole('button', { name: 'Host locally', exact: true });
    await entry.waitFor({ state: 'visible', timeout: 8000 });
    if (!/Host locally/u.test(await entry.textContent()) || writes.length ||
        state.requests.some(item => item.method === 'GET' && item.path.endsWith('/shared-world/hosting')) ||
        await page.getByRole('dialog', { name: 'Host locally', exact: true }).count() ||
        await page.getByText('Host decision not checked', { exact: true }).count())
      throw new Error('The labeled hosting entry must appear before setup checks or diagnostic clutter');
    if (testCase.id.endsWith('-play-ready')) {
      const open = await playRegion.getByRole('button', { name: 'Open game', exact: true }).boundingBox();
      const stop = await playRegion.getByRole('button', { name: 'Stop server', exact: true }).boundingBox();
      const styles = {};
      for (const name of ['Open game', 'Stop server']) styles[name] = await playRegion.getByRole('button', { name, exact: true }).evaluate(element => {
        const style = getComputedStyle(element);
        return { classes: element.className, minHeight: style.minHeight, minBlockSize: style.minBlockSize,
          fontSize: style.fontSize, padding: style.padding, transform: style.transform };
      });
      record.assertions.push({ playActionSizes: { open, stop }, styles });
      if (!open || !stop || Math.abs(open.width - stop.width) > 1 || Math.abs(open.height - stop.height) > 1 ||
          await playRegion.getByText('Asks Steam to open an installed game. Join the server from the game.', { exact: true }).count())
        throw new Error('Open game and Stop server must have equal dimensions without the intervening helper text');
    }
  } else if (testCase.id.endsWith('-starting')) {
    if (!state.simpleHostingStarted || writes.length !== 2 || !writes[0].path.endsWith('/prepare') || !writes[1].path.endsWith('/start'))
      throw new Error('The single confirmation did not prepare then Start exactly once');
  } else {
    const modal = page.getByRole('dialog', { name: 'Host locally', exact: true });
    const checked = await assertVisibleChecks(modal, record);
    if (await modal.locator('.local-hosting-warning').count() !== 1 || await modal.getByRole('checkbox').count())
      throw new Error('The Friend flow must contain one network warning and no save-permission checkbox');
    if (await modal.locator('.local-hosting-advanced').count() || writes.length)
      throw new Error('Opening automatic checks must not expose branch setup or mutate hosting');
    if (testCase.id.endsWith('-save-conflict') && await modal.getByRole('button', { name: 'Host locally', exact: true }).isEnabled())
      throw new Error('Divergent save histories must block Start');
    if (testCase.id.endsWith('-reopen-entry')) {
      await modal.getByRole('button', { name: 'Close Host locally', exact: true }).click();
      await modal.waitFor({ state: 'hidden', timeout: 8000 });
      const entry = page.getByRole('region', { name: 'Play', exact: true }).getByRole('button', { name: 'Host locally', exact: true });
      await entry.waitFor({ state: 'visible', timeout: 8000 });
      const reads = state.requests.filter(item => item.method === 'GET' && item.path.endsWith('/shared-world/hosting')).length;
      const refreshed = page.waitForResponse(response => response.url().endsWith('/shared-world/hosting') &&
        response.request().method() === 'GET', { timeout: 8000 });
      await entry.click();
      await refreshed;
      await modal.waitFor({ state: 'visible', timeout: 8000 });
      await assertVisibleChecks(modal, record);
      if (state.requests.filter(item => item.method === 'GET' && item.path.endsWith('/shared-world/hosting')).length <= reads ||
          await modal.getByRole('button', { name: 'Host locally', exact: true }).isEnabled())
        throw new Error('Reopening a review failure must read current checks while keeping Start blocked');
      record.assertions.push({ reopenedAfterReviewFailure: true, startRemainsBlocked: true });
    }
    if (/-stale-retry$|-recovered-retry$/u.test(testCase.id)) {
      state.simpleHostingReadFailure = true;
      await modal.getByRole('button', { name: 'Retry automatic checks', exact: true }).click();
      const stale = modal.getByText('Showing the last results. Retry the checks before starting.', { exact: true });
      await stale.waitFor({ state: 'visible', timeout: 8000 });
      const retained = await assertVisibleChecks(modal, record);
      if (retained.dateTime !== checked.dateTime || await modal.getByRole('button', { name: 'Host locally', exact: true }).isEnabled())
        throw new Error('A failed retry must retain previous results and check time while blocking Start');
      if (testCase.id.endsWith('-recovered-retry')) {
        state.simpleHostingReadFailure = false;
        await modal.getByRole('button', { name: 'Retry automatic checks', exact: true }).click();
        await page.waitForFunction(button => !button.disabled,
          await modal.getByRole('button', { name: 'Host locally', exact: true }).elementHandle(), { timeout: 8000 });
        await stale.waitFor({ state: 'hidden', timeout: 8000 });
        await assertVisibleChecks(modal, record);
        if (!await modal.getByRole('button', { name: 'Host locally', exact: true }).isEnabled())
          throw new Error('A successful retry must restore the current-check confirmation');
      }
      record.assertions.push({ previousResultsRetainedOnFailure: true, recovered: testCase.id.endsWith('-recovered-retry') });
    }
    const guide = modal.getByRole('region', { name: 'Local hosting setup guide', exact: true });
    const guideExpectation = testCase.id.endsWith('-guide-permission-unknown') ? ['permission', 'Check the current hosting permission'] :
      testCase.id.endsWith('-guide-permission-denied') ? ['permission', 'Ask the owner to allow shared hosting'] :
      testCase.id.endsWith('-missing-install') ? ['server', 'Install or select the dedicated server'] :
      testCase.id.endsWith('-save-conflict') ? ['save', 'Ask the owner to review different progress'] :
      testCase.id.endsWith('-guide-ports') ? ['ports', 'Free the game ports on this PC'] :
      testCase.id.endsWith('-missing-connection') ? ['connection', 'Set up connections to this PC'] : null;
    if (guideExpectation) {
      const [key, title] = guideExpectation;
      await guide.getByRole('heading', { name: title, exact: true }).waitFor({ state: 'visible', timeout: 8000 });
      if (await guide.locator('.local-hosting-guide-content').getAttribute('data-step') !== key)
        throw new Error('The hosting check did not open its corresponding setup guide');
      if (key === 'permission' && await modal.getByRole('button', { name: 'Host locally', exact: true }).isEnabled())
        throw new Error('The permission guide must not allow hosting through an unresolved or denied result');
      record.assertions.push({ hostingGuideStep: key, heading: title });
    }
    if (hostingWrites(state).length) throw new Error('Checks, retry, guide navigation and reopening must not write hosting state');
  }
  record.assertions.push({ simpleSharedHosting: testCase.id, hostingWrites: hostingWrites(state), evidence: 'Synthetic UI behavior only' });
}
