/** Additional real-UI screens backed only by synthetic in-memory access/setup state. */
const heading = name => ({ role: 'heading', name });
const region = name => ({ role: 'region', name });
const accessSection = ['This Host PC sections', 'access', 'Friend access'];
const openAccess = [{ button: 'Manage access', first: true }];
const accessTab = name => ({ nav: 'PC access sections', button: name });
const access = (id, steps, screen, scenario = 'host-access-review-multi') => ({
  id: `review-${id}`, scenario, workspace: 'Host', section: accessSection,
  steps: [...openAccess, ...steps], screen
});
const setupWorld = [{ button: 'Continue' }];
const worldName = { fill: /^World name/, value: 'Synthetic meadow' };
const password = { fill: /^Game password/, value: 'synthetic-only' };
const setup = (id, steps, screen) => ({ id: `review-${id}`, scenario: 'host-setup-access-review',
  workspace: 'Host', action: 'setup-game', game: 'Valheim', steps, screen });

export const extraCases = [
  access('deadline-choices', [accessTab('Access time'), { summary: 'Change deadline' }, { role: 'radio', name: '7 days' }],
    { css: '.access-deadline-controls[open]' }, 'host-access-deadline-review'),
  access('deadline-local-preview', [accessTab('Access time'), { summary: 'Change deadline' },
    { summary: 'Advanced: custom local date and time' }, { fill: 'Local date and time', value: '2026-10-13T18:30' }],
    { css: '.access-deadline-preview', text: 'Exact UTC' }, 'host-access-deadline-review'),
  access('deadline-saved', [accessTab('Access time'), { summary: 'Change deadline' }, { role: 'radio', name: '7 days' },
    { button: 'Save deadline' }], { css: '.access-deadline-feedback', text: 'Synthetic owner deadline saved' }, 'host-access-deadline-review'),
  access('permissions-before-after', [{ select: /^Permission preset/, value: 'status' }], region('Review access changes')),
  access('permissions-saved', [{ select: /^Permission preset/, value: 'status' }, { button: 'Apply preset' }],
    { css: '.settings-dialog[open]', text: 'Synthetic permission preset saved' }),
  access('individual-permissions', [{ summary: 'Individual permissions' }], { css: '.settings-disclosure[open] .device-permissions-grid' }),
  access('effective-access', [{ summary: 'Current effective access' }], region('Friend PC · Access summary')),
  access('assignment-before-after', [accessTab('Servers'), { button: 'Select all', within: '.server-picker-toolbar' }], region('Review access changes')),
  access('assignment-saved', [accessTab('Servers'), { button: 'Select all', within: '.server-picker-toolbar' }, { button: 'Save access' }],
    { css: '.settings-dialog[open]', text: 'Synthetic server assignments saved' }),
  access('access-discard-review', [accessTab('Servers'), { button: 'Select all', within: '.server-picker-toolbar' },
    { button: 'Cancel', within: '.settings-dialog-footer' }], { css: '.settings-inline-confirm', text: 'Discard unsaved PC changes?' }),
  access('helper-active', [accessTab('Access time')], { css: '.temporary-helper', text: 'Its usual permissions return automatically.' }, 'host-access-helper-review'),
  access('remove-access-review', [accessTab('PC details'), { button: 'Remove access' }],
    { css: '.settings-inline-confirm', text: 'Remove access for Friend PC?' }),
  { id: 'review-delete-pc', scenario: 'host-access-removed-review', workspace: 'Host', section: accessSection,
    steps: [{ select: /^PC status/, value: 'revoked' }, { button: 'Delete PC' }], screen: { role: 'dialog', name: 'Delete Second Friend PC?' } },
  { id: 'review-saved-host-picker', scenario: 'friend-access-picker-review', workspace: 'Join', action: 'picker',
    screen: { css: '.context-picker-popover', text: 'Camp Host' } },
  { id: 'review-saved-host-removed', scenario: 'friend-access-picker-review', workspace: 'Join', action: 'picker',
    steps: [{ check: 'Show removed Hosts' }], screen: { css: '.context-picker-popover', text: 'Removed Host' } },
  { id: 'review-delete-saved-host', scenario: 'friend-access-picker-review', workspace: 'Join', action: 'picker',
    steps: [{ check: 'Show removed Hosts' }, { button: 'Delete saved Host Removed Host' }],
    screen: { role: 'dialog', name: 'Delete this saved Host?' } },
  { id: 'review-host-context-notifications', scenario: 'host-access-review-offline', workspace: 'Host', action: 'server-tools',
    steps: [{ button: 'Server notifications' }, { summary: 'Choose alert types' }],
    screen: { role: 'dialog', name: 'Notifications for Weekend Valheim' } },
  { id: 'review-friend-context-notifications', scenario: 'friend-access-picker-review', workspace: 'Join',
    section: ['Join sections', 'connection', 'Connection settings'],
    steps: [{ button: 'Server notifications' }, { summary: 'Choose alert types' }],
    screen: { role: 'dialog', name: 'Notifications for Weekend Valheim' } },
  setup('local-world-import', [{ button: 'Use existing' }, { button: 'Copy world', within: '.choice:has-text("Local meadow")' }], region('Review world import')),
  setup('cached-world-import', [{ button: 'Use existing' }, { button: 'Copy world', within: '.choice:has-text("Cached meadow")' }], region('Review world import')),
  setup('cached-world-confirmation', [{ button: 'Use existing' }, { button: 'Copy world', within: '.choice:has-text("Cached meadow")' }, { button: 'Confirm copy' }],
    { role: 'dialog', name: 'Copy cached world?' }),
  { id: 'review-protected-setup-draft', scenario: 'host-access-draft-review', workspace: 'Host', screen: region('Recovered server setup') },
  { id: 'review-restored-setup', scenario: 'host-access-draft-review', workspace: 'Host',
    steps: [{ button: 'Review saved setup' }], screen: heading('Review and start') },
  setup('finish-later-confirmation', [worldName, password, { button: 'Finish later' }], { role: 'dialog', name: 'Finish setup later?' }),
  setup('paused-setup', [worldName, password, { button: 'Finish later' }, { button: 'Finish later', within: '.settings-dialog-footer' }],
    { css: '.host-setup-continuation', text: 'Setup in progress' }),
  setup('discard-setup-confirmation', [worldName, { button: 'Cancel setup' }], { role: 'dialog', name: 'Discard setup changes?' }),
  ...['Java', 'Bedrock'].map(edition => ({ id: `review-minecraft-${edition.toLowerCase()}-unchecked-terms`, scenario: 'host-setup-access-review',
    workspace: 'Host', action: 'setup-game', game: `Minecraft ${edition}`,
    steps: [{ button: 'Install a new official server' }, { fill: 'Server name', value: `Synthetic ${edition}` },
      { fill: 'World name', value: 'synthetic-world' }, ...setupWorld],
    screen: { role: 'checkbox', name: 'I have read and accept the terms for this download.' } }))
];

export const extraScenarioNames = Object.freeze([...new Set(extraCases.map(item => item.scenario))]);

export function configure(state, helpers) {
  if (!extraScenarioNames.includes(state.scenario)) return;
  const { ids, clone, root, fixtureNow } = helpers;
  const later = new Date(Date.parse(fixtureNow) + 7 * 86_400_000).toISOString();
  if (state.scenario === 'host-access-deadline-review') Object.assign(state.devices[0], { accessExpiresUtc: later, accessExpired: false });
  if (state.scenario === 'host-access-helper-review') Object.assign(state.devices[0], {
    temporaryHelperActive: true, temporaryHelperUntilUtc: new Date(Date.parse(fixtureNow) + 3_600_000).toISOString() });
  if (state.scenario === 'host-access-removed-review') state.devices[1].revoked = true;
  if (state.scenario === 'host-access-draft-review') {
    const profile = { ...clone(state.host.settings.profiles[0]), name: 'Recovered meadow', serverName: 'Recovered meadow',
      worldId: 'Recovered-meadow', worldSource: 'New', worldDirectory: `${root}\\worlds\\recovered`, sharedSavesEnabled: false };
    state.drafts[JSON.stringify(['settings', '00000000-0000-0000-0000-000000000000', null, 'host-setup'])] = {
      text: JSON.stringify({ version: 3, profiles: [profile], activeProfileId: profile.id, step: 'review', sourceRoot: '' }), revision: 7 };
    state.host.settings.profiles = []; state.host.runs = []; state.host.backups = {}; state.host.passwordConfigured = {};
  }
  if (state.scenario === 'friend-access-picker-review') {
    const selected = clone(state.friend); selected.connections = null;
    const other = { ...clone(selected), connectionId: ids.backup, connectionName: 'Camp Host', state: 'Disconnected', lastConnectedUtc: null };
    const removed = { ...clone(selected), connectionId: ids.otherDevice, connectionName: 'Removed Host', state: 'Revoked',
      profiles: [], connectionCode: 'Revoked', lastConnectedUtc: null };
    state.friend.connections = [selected, other, removed];
    state.notifications.servers = [{ profileId: ids.primary, connectionId: ids.connection, allowedEvents: ['Lifecycle', 'Access'] }];
  }
  if (state.scenario === 'host-access-review-offline')
    state.notifications.servers = [{ profileId: ids.primary, connectionId: null, allowedEvents: ['Lifecycle', 'Backup'] }];
}

export function route(state, request, helpers) {
  if (!extraScenarioNames.includes(state.scenario)) return null;
  const { method, path, body = {} } = request;
  const { reply, result, fixtureNow, root, ids, clone } = helpers;
  if (state.scenario === 'host-setup-access-review' && path === '/api/local/valheim/discover' && method === 'GET') {
    return reply({ installations: [{ executablePath: `${root}\\server-apps\\Valheim\\valheim_server.exe`, source: 'Synthetic owner-installed app' }],
      worlds: ['worlds_local', 'worlds'].map((sourceFolder, index) => ({ name: index ? 'Cached meadow' : 'Local meadow',
        saveRoot: `${root}\\source-review`, sourceFolder, format: index ? 'Steam cloud folder' : 'Local files',
        totalBytes: 3_145_728, modifiedUtc: fixtureNow, sourceFiles: [
          { name: 'meadow.db', bytes: 3_145_000, modifiedUtc: fixtureNow }, { name: 'meadow.fwl', bytes: 728, modifiedUtc: fixtureNow }] })) });
  }
  const match = /^\/api\/local\/devices\/([^/]+)\/(permissions|servers|access-expiry)$/.exec(path);
  if (!match || method !== 'PUT' || !['host-access-review-multi', 'host-access-deadline-review'].includes(state.scenario)) return null;
  const device = state.devices.find(item => item.id === match[1] && !item.revoked);
  if (!device || device.id !== ids.device) return { status: 400, body: result({ ok: false, code: 'SyntheticScopeMismatch', message: 'Only the selected synthetic PC can change.' }) };
  if (match[2] === 'permissions') {
    const keys = ['canStart', 'canStop', 'canExtendTimer', 'canViewLogs'];
    if (keys.some(key => typeof body[key] !== 'boolean') || body.scope !== null || Object.keys(body).some(key => ![...keys, 'scope'].includes(key))) return null;
    Object.assign(device, Object.fromEntries(keys.map(key => [key, body[key]])), { serverPermissions: [] });
    return reply(result({ message: 'Synthetic permission preset saved. No real PC access changed.' }));
  }
  if (match[2] === 'servers') {
    const known = new Set(state.host.settings.profiles.map(profile => profile.id));
    if (!Array.isArray(body.profileIds) || !Array.isArray(body.permissions) || body.profileIds.some(id => !known.has(id))) return null;
    device.assignedProfileIds = clone(body.profileIds); device.serverPermissions = clone(body.permissions);
    return reply(result({ message: 'Synthetic server assignments saved. No real PC access changed.' }));
  }
  const days = { OneHour: 1 / 24, EightHours: 1 / 3, OneDay: 1, SevenDays: 7, ThirtyDays: 30, NinetyDays: 90 };
  if (body.clear !== true && typeof body.accessExpiresUtc !== 'string' && !Object.hasOwn(days, body.duration)) return null;
  device.accessExpiresUtc = body.clear === true ? null : body.accessExpiresUtc ??
    new Date(Date.parse(fixtureNow) + days[body.duration] * 86_400_000).toISOString();
  device.accessExpired = false;
  return reply(result({ message: 'Synthetic owner deadline saved. Assignments and permissions unchanged.',
    accessExpiresUtc: device.accessExpiresUtc, accessExpired: false }));
}
