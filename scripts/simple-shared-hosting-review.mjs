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
  friend('network-warning', 'prepare'),
  friend('missing-install', 'missing-server'),
  friend('missing-connection', 'connection'),
  friend('save-conflict', 'conflict'),
  friend('ready-confirmation', 'ready'),
  friend('starting', 'ready', { steps: [{ button: 'Host locally' }, { button: 'Host locally', within: 'dialog[open]' }],
    screen: { css: '.server-command-header', text: 'Starting' } }),
];
export const simpleHostingVariants = [
  { id: 'darkdesktop', viewport: { width: 1280, height: 900 }, theme: 'dark', scale: 100 },
  { id: 'lightnarrow', viewport: { width: 390, height: 844 }, theme: 'light', scale: 100 },
];

export function simpleHostingStatus(state, profileId, boundary) {
  const stage = state.simpleHosting;
  const prepared = state.simpleHostingPrepared || stage === 'ready';
  const allowed = stage !== 'not-allowed';
  const missing = stage === 'missing-server';
  const connection = stage === 'connection';
  const conflict = stage === 'conflict';
  return { ok: allowed && !missing && !connection && !conflict,
    code: !allowed ? 'HostingPermissionDenied' : missing ? 'InstalledServerRequired' : connection ? 'LocalConnectionSetupRequired' :
      conflict ? 'HostingHistoryChoiceRequired' : prepared ? 'LocalHostingPrepared' : 'NeedsPreparation',
    message: boundary, allowed, consented: prepared, game: 'Valheim', name: 'Weekend Valheim', needsServerFile: missing,
    needsPassword: false, serverFile: missing ? null : 'C:\\TogetherServer-Synthetic\\valheim_server.exe',
    gameVersion: 'Synthetic', gamePort: 2456, controlPort: 5131, localProfileId: prepared ? profileId : null,
    branchHash: prepared ? 'A'.repeat(64) : null, readyForManualStart: allowed && prepared && !connection && !missing && !conflict,
    running: false, steps: ['permission', 'server', 'save', 'ports', 'connection'].map(key => ({ key,
      state: key === 'permission' && !allowed || key === 'server' && missing || key === 'connection' && connection ? 'ActionRequired' :
        key === 'save' && !prepared ? 'Unknown' : 'Ready', message: boundary })) };
}

export async function assertSimpleHostingUi(page, testCase, record, state) {
  if (!testCase.id.startsWith('simple-hosting-')) return;
  const writes = state.requests.filter(item => item.method !== 'GET' && /shared-world\/hosting(?:\/|$)/u.test(item.path));
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
      const policyReads = state.requests.filter(request => request.method === 'GET' &&
        /\/shared-world(?:\/governance)?$/u.test(request.path));
      if (!original || !policyReads.length || policyReads.some(request => !request.path.startsWith(`/api/local/profiles/${original.id}/`)))
        throw new Error('Owner continuation sharing must use its exact original policy');
      if (await page.locator('.server-directory button[aria-label="Open Weekend Valheim on this PC"]').count() !== 1 ||
          await page.locator('.server-directory').getByRole('heading', { name: 'Hosting for friends', exact: true }).count() ||
          await page.locator('.guest-hosting-label').count())
        throw new Error('Owner continuation exposed retained copies or a guest-hosting label');
    }
  } else if (testCase.id.endsWith('-not-allowed')) {
    if (await page.getByRole('button', { name: 'Host locally', exact: true }).count() || writes.length)
      throw new Error('An unauthorized Friend can see or invoke local hosting');
  } else if (testCase.id.endsWith('-starting')) {
    if (!state.simpleHostingStarted || writes.length !== 2 || !writes[0].path.endsWith('/prepare') || !writes[1].path.endsWith('/start'))
      throw new Error('The single confirmation did not prepare then Start exactly once');
  } else {
    const modal = page.getByRole('dialog', { name: 'Host locally', exact: true });
    if (await modal.locator('.local-hosting-warning').count() !== 1 || await modal.getByRole('checkbox').count())
      throw new Error('The Friend flow must contain one network warning and no save-permission checkbox');
    if (await modal.locator('.local-hosting-advanced').count() || writes.length)
      throw new Error('Opening automatic checks must not expose branch setup or mutate hosting');
    if (testCase.id.endsWith('-save-conflict') && await modal.getByRole('button', { name: 'Host locally', exact: true }).isEnabled())
      throw new Error('Divergent save histories must block Start');
  }
  record.assertions.push({ simpleSharedHosting: testCase.id, hostingWrites: writes, evidence: 'Synthetic UI behavior only' });
}
