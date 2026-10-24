// Browser automation is authorized ONLY on the explicitly selected hosted Windows runner.
// The exact --list invocation emits pure inventory and exits before output writes or capture setup.
// Keep this guard before imports, filesystem reads, servers, or browser side effects.
const args = process.argv.slice(2);
const listOnly = args.length === 1 && args[0] === '--list' || args.length === 2 && args.includes('--list') &&
  (args.includes('--simple-shared-hosting-only') || args.includes('--server-access-only'));
if (!listOnly && (!args.includes('--allow-isolated-browser-tests') ||
    args.indexOf('--hosted-runner') < 0 || args[args.indexOf('--hosted-runner') + 1] !== 'github-actions' ||
    process.env.GITHUB_ACTIONS !== 'true' || process.env.RUNNER_OS !== 'Windows' ||
    process.env.RUNNER_ENVIRONMENT !== 'github-hosted' || process.platform !== 'win32')) {
  throw new Error('Refusing browser execution: requires --allow-isolated-browser-tests --hosted-runner github-actions on an actual github-hosted Windows runner (GITHUB_ACTIONS=true, RUNNER_OS=Windows, RUNNER_ENVIRONMENT=github-hosted). Never run on the owner desktop.');
}

// Implementation below intentionally uses dynamic imports after authorization.
const { readFile, writeFile, mkdir, realpath, stat, rename } = await import('node:fs/promises');
const { resolve, dirname, extname, relative, isAbsolute } = await import('node:path');
const { fileURLToPath } = await import('node:url');
const { createServer } = await import('node:http');
const { createRequire } = await import('node:module');
const { createHash } = await import('node:crypto');
const { createFixtureState, routeFixture, fixtureNow, fixtureIds } = await import('./ui-redesign-fixtures.mjs');
const { extraCases: worldReviewCases } = await import('./ui-polish-world-review.mjs');
const { extraCases: accessReviewCases, serverAccessCaseIds, assertServerAccessUi } = await import('./ui-polish-access-review.mjs');
const { simpleHostingCases, simpleHostingVariants, assertSimpleHostingUi } = await import('./simple-shared-hosting-review.mjs');
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(root, 'local-data/ui-redesign-visual-review');
if (!listOnly) await mkdir(output, { recursive: true });
const manifest = {
  version: 1, evidence: 'Actual bundled React UI; synthetic responses only. No game, .NET, production storage, network or real-world acceptance.',
  authorization: { explicitFlags: args, hostedRunner: 'github-actions', runnerOS: process.env.RUNNER_OS, runId: process.env.GITHUB_RUN_ID ?? null },
  sourceAuthority: 'Current TogetherServer ui/src integrated by root before this hosted build; never the stale isolated UI.',
  build: {}, cases: [], fatalErrors: [], status: 'running',
};
let manifestWrites = Promise.resolve();
const saveManifest = () => {
  // Concurrent contexts share only reporting. Serialize writes so one capture
  // cannot truncate a newer manifest while another capture is finishing.
  const write = manifestWrites.catch(() => {}).then(async () => {
    const temporary = resolve(output, 'manifest.json.tmp');
    await writeFile(temporary, `${JSON.stringify(manifest, null, 2)}\n`);
    await rename(temporary, resolve(output, 'manifest.json'));
  });
  manifestWrites = write;
  return write;
};
if (!listOnly) await saveManifest();

// The inventory is explicit so a disappeared screen cannot silently reduce coverage.
const heading = name => ({ role: 'heading', name });
const region = name => ({ role: 'region', name });
const extraReviewCases = [...worldReviewCases, ...accessReviewCases];
const isDialogReview = testCase => testCase.id.startsWith('dialog-') || testCase.screen?.role === 'dialog';
const hostTabs = [
  ['overview', 'Overview', region('Server controls')], ['chat', 'Chat', heading('Server chat')],
  ['players', 'Players', heading('Players')], ['logs', 'Logs', region('Server logs')],
  ['sessions', 'Sessions', heading('Recent sessions')], ['backups', 'Backups', heading('Backups & sharing')],
  ['files', 'Settings & files', heading('Game settings')],
  ['setup', 'Setup & maintenance', { css: '.maintenance-guide > summary', text: 'Prepare maintenance' }],
];
// Server controls is an aria-labelled div (a group screen, not an ARIA region).
hostTabs[0][2] = { css: '[aria-label="Server controls"]', text: 'Stop server' };
const pcSections = [
  ['access', 'Access', 'Friend access'], ['stop', 'Shutdown', 'Stop & timer'],
  ['network', 'Network', 'Connection help'], ['diagnostics', 'Diagnostics', 'Server diagnostics'],
  ['advanced', 'Advanced', 'Advanced'],
];
const joinSections = [
  ['play', 'Play', heading('Play')], ['chat', 'Chat', heading('Server chat')],
  ['logs', 'Logs', region('Server logs')], ['shared-saves', 'Shared saves', region('Next shared-save action')],
  ['network', 'Connection help', heading('Connection help')],
  ['connection', 'Connection settings', heading('Connection settings')],
  ['pairing', 'Add a Host', heading('Add another Host')],
];
const appSections = [
  ['profile', 'Profile', 'Profile'],
  ['appearance', 'Appearance', 'Appearance'], ['notifications', 'Notifications', 'Notifications'],
  ['windows', 'Windows behavior', 'Windows behavior'], ['updates', 'Updates', 'App updates'],
  ['shortcuts', 'Keyboard shortcuts', 'Keyboard shortcuts'],
];
const setupPolishCases = [
  { id: 'setup-polish-hosting-notice', scenario: 'friend-peers', workspace: 'Join', section: ['Join sections', 'play', 'Play'], screen: { css: '.shared-host-notice', text: 'reports hosting' } },
  ...['missing-server', 'connection'].map(remedy => ({ id: `setup-polish-hosting-${remedy}`, scenario: 'friend-connected',
    fixture: { hostingRemedy: remedy }, workspace: 'Join', section: ['Join sections', 'play', 'Play'],
    steps: [{ button: 'Host on this PC' }], screen: { role: 'dialog', name: 'Host on this PC' } })),
  { id: 'setup-polish-chat-host', scenario: 'host-ready', workspace: 'Host', section: ['Selected server sections', 'chat', 'Chat'], screen: heading('Server chat') },
  { id: 'setup-polish-chat-friend', scenario: 'friend-connected', workspace: 'Join', section: ['Join sections', 'chat', 'Chat'], screen: heading('Server chat') },
  { id: 'setup-polish-password', scenario: 'friend-connected', fixture: { password: true }, workspace: 'Join', section: ['Join sections', 'play', 'Play'],
    steps: [{ button: 'Show password', within: '.shared-game-password' }], screen: { css: '.shared-game-password', text: 'SYNTHETIC-ONLY' } },
  { id: 'setup-polish-sharing-owner', scenario: 'host-shared', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    action: 'manage-sharing', screen: { role: 'dialog', name: 'Manage sharing' } },
  { id: 'setup-polish-sharing-next-step', scenario: 'host-shared', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    action: 'manage-sharing', steps: [{ nav: 'Sharing sections', button: 'Save sharing' }], screen: heading('What happens next') },
  { id: 'setup-polish-notifications-empty', scenario: 'host-ready', workspace: 'Host', action: 'notifications-empty', screen: { css: '.notification-empty', text: 'No recent notifications.' } },
  { id: 'setup-polish-notifications-activity', scenario: 'host-attention', workspace: 'Host', action: 'notifications-menu', screen: { css: '.notification-panel', text: 'Recent app and connection activity' } },
  { id: 'setup-polish-permissions-partial', scenario: 'host-multi', fixture: { permissions: 'partial' }, workspace: 'Host', section: ['This Host PC sections', 'access', 'Access'], screen: { css: '.friend-pc-card', text: 'Friend PC' } },
  { id: 'setup-polish-permissions-unassigned', scenario: 'host-ready', fixture: { permissions: 'unassigned' }, workspace: 'Host', section: ['This Host PC sections', 'access', 'Access'], screen: { css: '.friend-pc-card', text: 'No server access.' } },
  ...[['profile', 'Profile'], ['appearance', 'Appearance'], ['shortcuts', 'Keyboard shortcuts']].map(([id, title]) => ({
    id: `setup-polish-settings-${id}`, scenario: 'host-ready', workspace: 'App Settings', section: ['App settings sections', id, title], screen: heading(title) })),
  { id: 'setup-polish-settings-scroll', scenario: 'host-ready', workspace: 'App Settings', section: ['App settings sections', 'profile', 'Profile'], screen: heading('Profile') },
  { id: 'setup-polish-guide-welcome', scenario: 'host-ready', workspace: 'Host', action: 'guide-welcome', screen: { role: 'dialog', name: 'Make room for your next game night' } },
  { id: 'setup-polish-guide-host', scenario: 'host-ready', workspace: 'Host', action: 'guide-host', screen: { css: '.guide-coach', text: 'Make this PC your Host' } },
  { id: 'setup-polish-guide-join', scenario: 'friend-pairing', workspace: 'Join', action: 'guide-join', screen: { css: '.guide-coach', text: 'Start with your friend’s server code' } },
  { id: 'setup-polish-wizard-game-tip', scenario: 'host-first-use', workspace: 'Host', action: 'setup', screen: heading('Choose a game') },
  { id: 'setup-polish-wizard-server-tip', scenario: 'host-setup-access-review', workspace: 'Host', action: 'setup-game', game: 'Valheim',
    steps: [{ fill: /^World name/, value: 'Synthetic meadow' }, { fill: /^Game password/, value: 'synthetic-only' }, { button: 'Continue' }], screen: heading('Server app') }
];
const cases = [
  ...setupPolishCases,
  ...extraReviewCases.filter(testCase => !isDialogReview(testCase)),
  { id: 'sidebar-host-long-names', scenario: 'host-ready', workspace: 'Host', sidebarReview: true, screen: { css: '.server-directory' } },
  { id: 'sidebar-join-long-names', scenario: 'friend-connected', workspace: 'Join', sidebarReview: true, screen: { css: '.server-directory' } },
  { id: 'sidebar-settings', scenario: 'host-ready', workspace: 'App Settings', sidebarReview: true, screen: heading('Appearance') },
  { id: 'app-loading', scenario: 'host-loading', workspace: 'Host', bootOnly: true, pendingState: true, screen: { css: 'main', text: 'Loading TogetherServer' } },
  { id: 'app-load-error', scenario: 'host-load-error', workspace: 'Host', bootOnly: true, screen: { css: '[role="alert"]', text: 'Could not connect to TogetherServer' } },
  { id: 'host-session-recovery', scenario: 'host-session-recovery', workspace: 'Host', screen: heading('App reopened after an interruption') },
  { id: 'attention-session-recovery', scenario: 'host-session-recovery', workspace: 'Attention', steps: [{ summary: 'Session recovery' }], screen: heading('App reopened after an interruption') },
  ...['recovery', 'warning'].map(state => ({ id: `app-data-${state}`, scenario: `host-data-${state}`, workspace: 'Host', screen: heading('Local data needs review') })),
  { id: 'host-custom-certification-progress', scenario: 'host-custom-certification-step', workspace: 'Host', section: ['Selected server sections', 'setup', 'Setup & maintenance'], screen: { css: '.custom-certification', text: 'Certification in progress' } },
  { id: 'host-crash-recovery-suspended', scenario: 'host-crash-recovery', workspace: 'Host', screen: { css: '.safety-status', text: 'Crash recovery: Suspended' } },
  ...['downloading', 'blocked', 'failed'].map(state => ({ id: `settings-update-${state}`, scenario: `host-update-${state}`, workspace: 'App Settings',
    section: ['App settings sections', 'updates', 'Updates'], screen: { css: '.update-preparation' } })),
  { id: 'host-invite-code', scenario: 'host-ready', workspace: 'Host', steps: [{ button: 'Invite friends' }], screen: { css: '.inline-invite', text: 'Server code ready' } },
  ...hostTabs.map(([id, label, screen]) => ({ id: `host-${id}`, scenario: 'host-ready', workspace: 'Host', section: ['Selected server sections', id, label], screen })),
  ...pcSections.map(([id, label, title]) => ({ id: `host-pc-${id}`, scenario: 'host-ready', workspace: 'Host', section: ['This Host PC sections', id, label], screen: heading(title) })),
  { id: 'host-diagnostics-report-privacy', scenario: 'host-ready', workspace: 'Host', section: ['This Host PC sections', 'diagnostics', 'Diagnostics'],
    steps: [{ summary: 'Report contents & privacy' }], screen: { css: '.support-export-privacy[open]' } },
  { id: 'host-first-use', scenario: 'host-first-use', workspace: 'Host', screen: heading('Host or join a server') },
  { id: 'host-compare-all', scenario: 'host-multi', workspace: 'Host', action: 'compare', screen: { role: 'dialog', name: 'All servers' } },
  ...joinSections.map(([id, label, screen]) => ({ id: `join-${id}`, scenario: 'friend-connected', workspace: 'Join', section: ['Join sections', id, label], screen })),
  { id: 'join-play-instructions-expanded', scenario: 'friend-connected', workspace: 'Join', section: ['Join sections', 'play', 'Play'],
    steps: [{ summary: 'Join instructions', within: '.friend-play-flow' }],
    screen: { css: '.friend-play-flow details[open]', text: 'Open Valheim, choose your character, then choose Join Game and Join IP.' } },
  { id: 'join-play-tools-expanded', scenario: 'friend-connected', workspace: 'Join', section: ['Join sections', 'play', 'Play'],
    steps: [{ summary: 'Tools & help', within: '.friend-play-flow' }, { summary: /^Your access:/, within: '.friend-play-flow' }],
    screen: { css: '.friend-play-flow details[open]', text: 'Permissions apply only to this server. Stop and Restart also require a Ready server and a current player count of zero, checked again before stopping.' } },
  { id: 'join-shared-verified-copy', scenario: 'friend-shared-verified-offline', workspace: 'Join',
    section: ['Join sections', 'shared-saves', 'Shared saves'], screen: heading('Prepare hosting on this PC'), action: 'shared-verified-copy' },
  { id: 'join-shared-receiving', scenario: 'friend-shared-receiving-offline', workspace: 'Join',
    section: ['Join sections', 'shared-saves', 'Shared saves'], screen: region('Save transfer progress'), action: 'shared-receiving' },
  { id: 'join-first-pairing', scenario: 'friend-pairing', workspace: 'Join', screen: heading('Join a server') },
  { id: 'join-permitted-controls', scenario: 'friend-permitted', workspace: 'Join', section: ['Join sections', 'connection', 'Connection settings'], screen: heading('Connection settings'), action: 'permitted-controls' },
  { id: 'join-permitted-start', scenario: 'friend-offline', workspace: 'Join', section: ['Join sections', 'play', 'Play'], screen: heading('Play'), action: 'permitted-start' },
  { id: 'attention-issues', scenario: 'host-attention', workspace: 'Attention', screen: heading('Attention Center') },
  { id: 'attention-history', scenario: 'host-attention', workspace: 'Attention', screen: region('Activity history'), action: 'history' },
  ...appSections.map(([id, label, title]) => ({ id: `settings-${id}`, scenario: 'host-ready', workspace: 'App Settings', section: ['App settings sections', id, label], screen: heading(title) })),
  ...['offline', 'starting', 'stopping', 'unknown', 'countdown', 'maintenance', 'error'].map(state => ({
    id: `host-overview-${state}`, scenario: `host-${state}`, workspace: 'Host', screen: { css: '[aria-label="Server controls"]' } })),
  ...['unknown', 'countdown', 'offline', 'terraria-offline', 'custom-offline'].map(state => ({
    id: `host-players-${state}`, scenario: `host-${state}`, workspace: 'Host',
    section: ['Selected server sections', 'players', 'Players'], screen: heading('Players') })),
  ...['Count source & timer details', 'Player-count help', 'Recent count changes'].map((name, index) => ({
    id: `host-players-details-${index}`, scenario: 'host-unknown', workspace: 'Host',
    section: ['Selected server sections', 'players', 'Players'], steps: [{ summary: name }], screen: { css: '.players-disclosure[open]' } })),
  { id: 'host-players-add-time', scenario: 'host-countdown', workspace: 'Host', section: ['Selected server sections', 'players', 'Players'],
    steps: [{ summary: 'Add shutdown time' }], screen: { css: '.server-support-disclosure[open]' } },
  ...['empty-backups', 'backup-failure'].map(state => ({ id: `host-backups-${state}`, scenario: `host-${state}`, workspace: 'Host',
    section: ['Selected server sections', 'backups', 'Backups'], screen: heading('Backups & sharing') })),
  { id: 'host-backups-error', scenario: 'host-backups-error', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'], screen: { css: '.backup-catalog [role="alert"]', text: 'Backups unavailable' } },
  { id: 'host-logs-loading', scenario: 'host-logs-loading', workspace: 'Host', pendingState: true, section: ['Selected server sections', 'logs', 'Logs'], screen: region('Server logs') },
  { id: 'host-logs-error', scenario: 'host-logs-error', workspace: 'Host', section: ['Selected server sections', 'logs', 'Logs'], screen: region('Server logs') },
  ...['Host', 'Join'].map(workspace => ({ id: `${workspace.toLowerCase()}-log-filters-expanded`, scenario: workspace === 'Host' ? 'host-ready' : 'friend-connected', workspace,
    section: workspace === 'Host' ? ['Selected server sections', 'logs', 'Logs'] : ['Join sections', 'logs', 'Logs'],
    steps: [{ button: 'Filters', whenVisible: true }], screen: { role: 'combobox', name: 'Log severity' } })),
  ...[['sharing', 'Sharing & hosting'], ['settings', 'Protection settings']].map(([id, name]) => ({
    id: `host-protection-${id}`, scenario: 'host-shared-offline', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    steps: [{ nav: 'World protection sections', button: name }], screen: id === 'sharing' ? region('Shared saves') : heading('Protect this world automatically') })),
  ...['Name and pin backup', 'Preview which backups are kept', 'Backup checks and pin limits'].map((name, index) => ({
    id: `host-backup-details-${index}`, scenario: 'host-offline', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    steps: [{ summary: name }], screen: { css: '.backup-catalog details[open]' } })),
  ...['no-devices', 'removed-devices', 'expired-devices', 'pending-devices'].map(state => ({
    id: `host-access-${state}`, scenario: `host-${state}`, workspace: 'Host', section: ['This Host PC sections', 'access', 'Friend access'], screen: heading('Friend access') })),
  ...['disabled', 'offline', 'starting', 'stopping', 'unknown', 'maintenance', 'status-only'].map(state => ({
    id: `join-play-${state}`, scenario: `friend-${state}`, workspace: 'Join', section: ['Join sections', 'play', 'Play'], screen: heading('Play') })),
  ...['expired', 'revoked', 'awaiting-approval', 'no-assignment', 'disconnected'].map(state => ({
    id: `join-connection-${state}`, scenario: `friend-${state}`, workspace: 'Join', section: state === 'revoked' ? ['Join sections', 'pairing', 'Add a Host'] : ['Join sections', 'connection', 'Connection settings'],
    screen: state === 'revoked' ? heading('Join a server') : { css: '.friend-panel' } })),
  ...['verifying', 'receipt'].map(state => ({ id: `join-shared-${state}`, scenario: `friend-shared-${state}-offline`, workspace: 'Join',
    section: ['Join sections', 'shared-saves', 'Shared saves'], screen: region('Save transfer progress') })),
  ...['low-space', 'untrusted'].map(state => ({ id: `join-shared-${state}`, scenario: `friend-shared-${state}`, workspace: 'Join',
    section: ['Join sections', 'shared-saves', 'Shared saves'], screen: region('Next shared-save action') })),
  ...['Host', 'Join'].flatMap(workspace => ['peers', 'peers-conflict', ...(workspace === 'Join' ? ['peers-follower'] : [])].map(state => ({
    id: `${workspace.toLowerCase()}-${state}`, scenario: `${workspace === 'Host' ? 'host' : 'friend'}-${state}`, workspace,
    section: workspace === 'Host' ? ['Selected server sections', 'backups', 'Backups'] : ['Join sections', 'shared-saves', 'Shared saves'],
    steps: workspace === 'Host' ? [{ nav: 'World protection sections', button: 'Sharing & hosting' }, { summary: 'Other Hosts & connections' }] : [], screen: region('World groups') }))),
  ...['Save histories and reported copies', 'Contact another group PC', 'Technical details', 'Private live-save test copies'].map((name, index) => ({
    id: `join-peer-details-${index}`, scenario: 'friend-peers-conflict', workspace: 'Join', section: ['Join sections', 'shared-saves', 'Shared saves'],
    steps: [{ summary: name, within: '.world-peer-panel' }], screen: { css: '.world-peer-panel details[open]' } })),
  ...['Host', 'Join'].map(workspace => ({ id: `${workspace.toLowerCase()}-peer-save-permissions`, scenario: workspace === 'Host' ? 'host-peers' : 'friend-peers', workspace,
    section: workspace === 'Host' ? ['Selected server sections', 'backups', 'Backups'] : ['Join sections', 'shared-saves', 'Shared saves'],
    steps: [...(workspace === 'Host' ? [{ nav: 'World protection sections', button: 'Sharing & hosting' }, { summary: 'Other Hosts & connections' }] : []),
      { summary: 'Save sharing & permissions', within: '.world-peer-panel' }], screen: { css: '.world-peer-permissions[open]' } })),
  ...['Host', 'Join'].flatMap(workspace => ['empty', 'notice'].map(state => ({
    id: `${workspace.toLowerCase()}-chat-${state}`, scenario: `${workspace === 'Host' ? 'host' : 'friend'}-chat-${state}`, workspace,
    section: workspace === 'Host' ? ['Selected server sections', 'chat', 'Chat'] : ['Join sections', 'chat', 'Chat'], screen: heading('Server chat') }))),
  { id: 'host-chat-members', scenario: 'host-ready', workspace: 'Host', section: ['Selected server sections', 'chat', 'Chat'],
    steps: [{ summary: /^People in this room/ }], screen: { css: '.server-chat-members[open]' } },
  { id: 'host-chat-edit-notice', scenario: 'host-chat-notice', workspace: 'Host', section: ['Selected server sections', 'chat', 'Chat'],
    steps: [{ button: /Edit notice/ }], screen: { role: 'textbox', name: 'Notice text' } },
  ...['available', 'error'].map(state => ({ id: `settings-updates-${state}`, scenario: `host-update-${state}`, workspace: 'App Settings',
    section: ['App settings sections', 'updates', 'Updates'], screen: heading('App updates') })),
  ...['Files', 'Add-ons', 'Checkpoints'].map(name => ({ id: `host-server-files-${name.toLowerCase()}`, scenario: 'host-java-maintenance', workspace: 'Host',
    section: ['Selected server sections', 'files', 'Settings & files'], steps: [{ role: 'tab', name: 'Server files' }, { nav: 'Server file sections', button: name === 'Checkpoints' ? 'Setup backups' : name }],
    screen: heading(name === 'Files' ? 'Settings files' : name === 'Checkpoints' ? 'Setup backups' : 'Add-ons & game version') })),
  { id: 'host-server-file-editor', scenario: 'host-java-maintenance', workspace: 'Host', section: ['Selected server sections', 'files', 'Settings & files'],
    steps: [{ role: 'tab', name: 'Server files' }, { button: 'Edit file' }], screen: { role: 'textbox', name: 'File contents' } },
  { id: 'host-server-file-review', scenario: 'host-java-maintenance', workspace: 'Host', section: ['Selected server sections', 'files', 'Settings & files'],
    steps: [{ role: 'tab', name: 'Server files' }, { button: 'Edit file' }, { fill: 'File contents', value: '# Synthetic reviewed change\nmax-players=12\n' }, { button: 'Review file changes' }], screen: region('Review raw file changes') },
  { id: 'host-server-folders', scenario: 'host-java-maintenance', workspace: 'Host', section: ['Selected server sections', 'files', 'Settings & files'],
    steps: [{ role: 'tab', name: 'Server files' }, { summary: /^Folders on this PC/ }], screen: { css: '.server-files-locations[open]' } },
  ...['java', 'bedrock', 'valheim'].flatMap(game => ['General', 'Players'].filter(page => game !== 'valheim' || page === 'Players').map(page => ({
    id: `host-game-settings-${game}-${page.toLowerCase()}`, scenario: `host-${game}-maintenance`, workspace: 'Host', section: ['Selected server sections', 'files', 'Settings & files'],
    steps: [{ nav: 'Game settings sections', button: page }], screen: page === 'Players' ? { role: 'combobox', name: 'Player list' } : heading('Game settings') }))),
  { id: 'host-live-save-acceptance', scenario: 'host-live-save', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    action: 'manage-sharing', steps: [{ nav: 'Sharing sections', button: 'Save sharing' }, { summary: 'Advanced: game save proof and review' }, { summary: /Automatic recovery copies/ }], screen: region('Live-save installation test') },
  { id: 'host-live-interrupted', scenario: 'host-live-interrupted', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    action: 'manage-sharing', steps: [{ nav: 'Sharing sections', button: 'Save sharing' }, { summary: 'Advanced: game save proof and review' }, { summary: /Automatic recovery copies/ }, { summary: /Private test history/ }], screen: region('Live-save installation test') },
];
const dialogs = [
  ...extraReviewCases.filter(isDialogReview),
  { id: 'dialog-server-tools', scenario: 'host-ready', workspace: 'Host', action: 'server-tools', screen: { role: 'dialog', name: 'Server tools' } },
  ...[
    ['capacity', 'advanced', 'Advanced', 'Edit hosting limits', 'Hosting limits'],
    ['network', 'advanced', 'Advanced', 'Edit network settings', 'Advanced network settings'],
    ['power-timer', 'stop', 'Stop & timer', 'Edit countdown', 'Empty-server countdown'],
    ['stop-safety', 'stop', 'Stop & timer', 'Review Stop safety', 'Remote Stop safety'],
    ['friend-route', 'network', 'Connection help', 'Edit connection route', 'Friend connection route'],
    ['connection-doctor', 'network', 'Connection help', 'Connection help', 'Connection help'],
    ['extensions', 'stop', 'Stop & timer', 'Edit extensions', 'Friend timer extensions'],
    ['tcp', 'network', 'Connection help', 'Open connection checks', 'Internet and local connection checks'],
    ['identity', 'advanced', 'Advanced', 'Review Host identity', 'Secure Host identity'],
    ['move', 'advanced', 'Advanced', 'Open move guide', 'Move hosting to another PC'],
    ['rehearsal', 'network', 'Connection help', 'Open rehearsal', 'Test with another owned PC'],
    ['notifications', 'advanced', 'Advanced', 'Edit server notifications', 'Server notifications'],
  ].map(([id, section, label, button, title]) => ({ id: `dialog-${id}`, scenario: 'host-ready', workspace: 'Host',
    section: ['This Host PC sections', section, label], button, screen: { role: 'dialog', name: title } })),
  { id: 'dialog-setup-game-chooser', scenario: 'host-first-use', workspace: 'Host', action: 'setup', screen: heading('Choose a game') },
  ...['Valheim', 'Minecraft Java', 'Minecraft Bedrock', 'Factorio', 'Terraria', 'Custom game'].map(game => ({
    id: `dialog-setup-${game.toLowerCase().replaceAll(' ', '-')}`, scenario: 'host-first-use', workspace: 'Host', action: 'setup-game', game,
    screen: heading(game === 'Valheim' ? 'Choose a world' : 'Name this server'),
  })),
  { id: 'dialog-managed-sharing-host', scenario: 'host-shared', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'], action: 'manage-sharing', screen: { role: 'dialog', name: 'Manage sharing' } },
  { id: 'join-consent', scenario: 'friend-shared', workspace: 'Join', section: ['Join sections', 'shared-saves', 'Shared saves'], action: 'consent', screen: region('Next shared-save action') },
  { id: 'dialog-save-consent', scenario: 'friend-shared', workspace: 'Join', section: ['Join sections', 'shared-saves', 'Shared saves'], button: 'Host this save', screen: { role: 'dialog', name: 'Host this save' } },
  { id: 'dialog-managed-sharing-friend', scenario: 'friend-shared', workspace: 'Join', section: ['Join sections', 'shared-saves', 'Shared saves'], action: 'manage-sharing', screen: { role: 'dialog', name: 'Manage sharing' } },
  { id: 'tooltip-hover', scenario: 'host-ready', workspace: 'Host', action: 'tooltip-hover', screen: { role: 'tooltip', name: /.+/ } },
  { id: 'tooltip-keyboard', scenario: 'host-ready', workspace: 'Host', action: 'tooltip-keyboard', screen: { role: 'tooltip', name: /.+/ } },
  ...['Permissions', 'Servers', 'Access time', 'PC details'].map(name => ({
    id: `dialog-access-${name.toLowerCase().replaceAll(' ', '-')}`, scenario: 'host-ready', workspace: 'Host', section: ['This Host PC sections', 'access', 'Friend access'],
    steps: [{ button: 'Manage access', first: true }, { nav: 'PC access sections', button: name }], screen: { role: 'dialog', name: 'Manage access for Friend PC' } })),
  { id: 'dialog-protection', scenario: 'host-shared-offline', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    steps: [{ nav: 'World protection sections', button: 'Protection settings' }, { button: 'Change protection settings' }], screen: { role: 'dialog', name: 'Protection settings' } },
  { id: 'dialog-command-palette', scenario: 'host-ready', workspace: 'Host', steps: [{ button: 'Commands' }], screen: { role: 'dialog', name: 'Command palette' } },
  { id: 'dialog-command-empty', scenario: 'host-ready', workspace: 'Host', steps: [{ button: 'Commands' }, { fill: 'Search commands', value: 'NoSyntheticCommandMatches' }], screen: { role: 'dialog', name: 'Command palette' } },
  { id: 'dialog-update-review', scenario: 'host-update-available', workspace: 'Host', steps: [{ button: 'Update and restart', first: true }], screen: { role: 'dialog', name: 'Update TogetherServer' } },
  ...['valheim', 'java', 'bedrock', 'factorio', 'terraria', 'custom'].flatMap(game => ['review', 'server', 'world'].map(step => ({
    id: `dialog-edit-setup-${game}-${step}`, scenario: game === 'valheim' ? 'host-offline' : `host-${game}-offline`, workspace: 'Host', action: 'server-tools',
    steps: [{ button: 'Edit setup' }, ...(step === 'review' ? [] : [{ button: 'Back' }]), ...(step === 'world' ? [{ button: 'Back' }] : [])],
    screen: heading(step === 'review' ? 'Review and start' : step === 'server' ? 'Server app' : game === 'valheim' ? 'Choose a world' : 'Name this server') }))),
  { id: 'dialog-confirm-remove-server', scenario: 'host-offline', workspace: 'Host', action: 'server-tools',
    steps: [{ button: 'Edit setup' }, { summary: 'Danger zone' }, { button: 'Remove from TogetherServer' }], screen: { role: 'dialog', name: 'Remove server?' } },
  { id: 'dialog-notifications-menu', scenario: 'host-attention', workspace: 'Host', action: 'notifications-menu', screen: { css: '.notification-menu[open]' } },
  { id: 'dialog-selected-server-picker', scenario: 'host-multi', workspace: 'Host', action: 'picker', screen: { css: '.context-picker[open] .context-picker-popover' } },
  { id: 'dialog-selected-server-picker-empty', scenario: 'host-multi', workspace: 'Host', action: 'picker',
    steps: [{ fill: 'Find selected server', value: 'NoSyntheticServerMatches' }], screen: { css: '.context-picker[open] .context-picker-popover' } },
  { id: 'dialog-confirm-stop', scenario: 'host-ready', workspace: 'Host', steps: [{ button: 'Stop server' }], screen: { role: 'dialog', name: 'Stop Weekend Valheim?' } },
  { id: 'dialog-confirm-forget-host', scenario: 'friend-connected', workspace: 'Join', section: ['Join sections', 'connection', 'Connection settings'],
    steps: [{ summary: 'Connection identity and recovery' }, { button: 'Forget this Host' }], screen: { role: 'dialog', name: 'Forget this saved Host?' } },
  ...['People', 'Save sharing', 'History & recovery'].map(name => ({
    id: `dialog-host-sharing-${name.toLowerCase().replaceAll(/[^a-z]+/g, '-')}`, scenario: 'host-shared', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    action: 'manage-sharing', steps: [{ nav: 'Sharing sections', button: name }], screen: { role: 'dialog', name: 'Manage sharing' } })),
  { id: 'dialog-move-hosting', scenario: 'host-shared-offline', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'], action: 'manage-sharing',
    steps: [{ nav: 'Sharing sections', button: 'History & recovery' }, { button: 'Move hosting', within: '.settings-dialog[open] [aria-label="Move hosting"]' }], screen: { role: 'dialog', name: 'Move hosting' } },
  { id: 'dialog-peer-default-review', scenario: 'host-peers-conflict', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    steps: [{ nav: 'World protection sections', button: 'Sharing & hosting' }, { summary: 'Other Hosts & connections' }, { button: 'Use as group default', first: true }], screen: region('Review group default') },
  { id: 'dialog-owner-peer-recovery', scenario: 'host-peers-conflict', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'],
    steps: [{ nav: 'World protection sections', button: 'Sharing & hosting' }, { summary: 'Other Hosts & connections' }, { summary: 'Save histories and reported copies', within: '.world-peer-panel' },
      { button: 'Recover this history on the owner PC', first: true }], screen: region('Owner recovery from peer history') },
  ...['Copy status', 'People', 'Recovery', 'Details'].map(name => ({
    id: `dialog-friend-sharing-${name.toLowerCase().replaceAll(' ', '-')}`, scenario: 'friend-shared-verified-offline', workspace: 'Join',
    section: ['Join sections', 'shared-saves', 'Shared saves'], action: 'manage-sharing',
    steps: [{ nav: 'Shared-save management sections', button: name }], screen: { role: 'dialog', name: 'Manage sharing' } })),
  { id: 'dialog-confirm-restart', scenario: 'host-ready', workspace: 'Host', action: 'server-tools', steps: [{ button: 'Restart server' }], screen: { role: 'dialog', name: 'Restart Weekend Valheim?' } },
  { id: 'dialog-confirm-safe-restart', scenario: 'host-ready', workspace: 'Host', action: 'server-tools', steps: [{ button: 'Back up and restart' }], screen: { role: 'dialog', name: 'Back up and restart?' } },
  ...[['reload', 'Reload file', 'Reload server file?'], ['discard', 'Discard unsaved file edits', 'Discard file edits?']].map(([id, button, name]) => ({
    id: `dialog-file-${id}`, scenario: 'host-java-maintenance', workspace: 'Host', section: ['Selected server sections', 'files', 'Settings & files'],
    steps: [{ role: 'tab', name: 'Server files' }, { button: 'Edit file' }, { fill: 'File contents', value: '# Synthetic unsaved change\nmax-players=12\n' }, { button }],
    screen: { role: 'dialog', name } })),
  { id: 'dialog-restore-setup', scenario: 'host-java-maintenance', workspace: 'Host', section: ['Selected server sections', 'files', 'Settings & files'],
    steps: [{ role: 'tab', name: 'Server files' }, { nav: 'Server file sections', button: 'Setup backups' }, { button: 'Restore setup' }], screen: { role: 'dialog', name: 'Restore complete setup?' } },
  ...[['reload', 'Reload game settings', 'Reload game settings?'], ['discard', 'Discard unsaved settings', 'Discard unsaved settings?']].map(([id, button, name]) => ({
    id: `dialog-game-settings-${id}`, scenario: 'host-java-maintenance', workspace: 'Host', section: ['Selected server sections', 'files', 'Settings & files'],
    steps: [{ fill: 'Maximum players', value: '12' }, { button }], screen: { role: 'dialog', name } })),
  { id: 'dialog-resume-hosting', scenario: 'host-session-recovery', workspace: 'Host', steps: [{ button: 'Resume hosting' }], screen: { role: 'dialog', name: 'Resume hosting?' } },
  { id: 'dialog-forget-orphaned-session', scenario: 'host-recovery-orphan', workspace: 'Host', steps: [{ button: 'Forget stopped session' }], screen: { role: 'dialog', name: 'Forget stopped session' } },
  { id: 'dialog-archive-session', scenario: 'host-failed-session', workspace: 'Host', action: 'server-tools', steps: [{ button: 'Archive exited record' }], screen: { role: 'dialog', name: 'Archive this session?' } },
  { id: 'dialog-replace-invite', scenario: 'host-ready', workspace: 'Host', steps: [{ button: 'Invite friends' }, { summary: 'New PCs and replacing the code' },
    { button: 'Replace code and remove old access' }], screen: { role: 'dialog', name: 'Replace the code for Weekend Valheim?' } },
  { id: 'dialog-revoke-custom-certification', scenario: 'host-custom-certified-offline', workspace: 'Host', section: ['Selected server sections', 'setup', 'Setup & maintenance'],
    steps: [{ button: 'Remove control approval' }], screen: { role: 'dialog', name: 'Remove Custom control approval?' } },
  { id: 'dialog-chat-navigation-guard', scenario: 'host-chat-notice', workspace: 'Host', section: ['Selected server sections', 'chat', 'Chat'],
    steps: [{ button: 'Edit notice' }, { fill: 'Notice text', value: 'Synthetic unsaved room notice' }, { nav: 'TogetherServer navigation', button: 'App Settings' }],
    screen: { role: 'dialog', name: 'Discard unsaved chat edits?' } },
  { id: 'dialog-mode-change-with-setup-draft', scenario: 'host-setup-access-review', workspace: 'Host', action: 'setup-game', game: 'Valheim',
    steps: [{ fill: /^World name/, value: 'Synthetic draft' }, { fill: /^Game password/, value: 'synthetic-only' }, { button: 'Finish later' },
      { button: 'Finish later', within: '.settings-dialog-footer' }, { nav: 'TogetherServer navigation', button: 'App Settings' }],
    screen: { role: 'dialog', name: 'Keep a draft and change pages?' } },
  ...[['reload', 'Reload this list', 'Reload player list?'], ['discard', 'Discard unsaved list edits', 'Discard player list edits?']].map(([id, button, name]) => ({
    id: `dialog-player-list-${id}`, scenario: 'host-valheim-maintenance', workspace: 'Host', section: ['Selected server sections', 'files', 'Settings & files'],
    steps: [{ fill: 'Platform user ID', value: 'Steam_SYNTHETIC' }, { button: 'Add player' }, { button }], screen: { role: 'dialog', name } })),
  ...['Disable', 'Import mod ZIP', 'Undo last add-on change'].map((button, index) => ({ id: `dialog-addon-review-${index}`, scenario: 'host-factorio-addons-maintenance', workspace: 'Host',
    section: ['Selected server sections', 'files', 'Settings & files'], steps: [{ role: 'tab', name: 'Server files' }, { nav: 'Server file sections', button: 'Add-ons' }, { button }],
    screen: { role: 'dialog', name: 'Confirm add-on change' } })),
  ...['factorio', 'terraria'].map(game => ({ id: `dialog-create-${game}-configuration`, scenario: `host-${game}-config-maintenance`, workspace: 'Host',
    section: ['Selected server sections', 'files', 'Settings & files'], steps: [{ role: 'tab', name: 'Server files' }, { button: 'Create config' }],
    screen: { role: 'dialog', name: 'Create game configuration?' } })),
];
const variants = [
  { id: 'darkdesktop', viewport: { width: 1280, height: 900 }, theme: 'dark', scale: 100 },
  { id: 'lightmedium', viewport: { width: 900, height: 900 }, theme: 'light', scale: 100 },
  { id: 'lightnarrow', viewport: { width: 390, height: 844 }, theme: 'light', scale: 100 },
];
const largeTextPages = new Set(['host-overview', 'host-players', 'host-backups', 'host-files', 'host-pc-access', 'host-pc-stop',
  'host-first-use', 'join-play', 'join-play-instructions-expanded', 'join-play-tools-expanded', 'join-shared-saves', 'join-first-pairing', 'attention-issues', 'settings-appearance',
  'tooltip-hover', 'tooltip-keyboard', 'host-server-file-editor', 'host-server-file-review', 'host-server-files-checkpoints',
  'dialog-protection', 'dialog-access-access-time', 'host-live-save-acceptance', 'join-peers-conflict',
  ...extraReviewCases.filter(testCase => /deadline|owner-recovery|restore|setup|live-.+-confirmation|^move-hosting-/.test(testCase.id)).map(testCase => testCase.id)]);
const setupPolishOnly = args.includes('--setup-polish-only');
const simpleHostingOnly = args.includes('--simple-shared-hosting-only');
const serverAccessOnly = args.includes('--server-access-only');
const setupPolishVariants = [
  { id: 'darkdesktop', viewport: { width: 1280, height: 900 }, theme: 'dark', scale: 100, reducedMotion: 'no-preference' },
  { id: 'lightnarrow150', viewport: { width: 390, height: 844 }, theme: 'light', scale: 150, reducedMotion: 'reduce' },
  { id: 'darkshortdesktop', viewport: { width: 1280, height: 650 }, theme: 'dark', scale: 100, reducedMotion: 'reduce' }
];
const reviewCases = serverAccessOnly ? serverAccessCaseIds.map(id => {
  const testCase = accessReviewCases.find(testCase => testCase.id === id);
  if (!testCase) throw new Error(`Required server-access review case is missing: ${id}`);
  return testCase;
}) : simpleHostingOnly ? simpleHostingCases : setupPolishOnly ? setupPolishCases : args.includes('--move-hosting-only')
  ? [...cases, ...dialogs].filter(testCase => testCase.id.startsWith('move-hosting-'))
  : args.includes('--sidebar-only') ? [...cases, ...dialogs].filter(testCase => testCase.sidebarReview ||
    ['host-files', 'host-logs', 'host-sessions', 'host-pc-access', 'host-first-use', 'join-first-pairing', 'attention-issues', 'settings-updates', 'dialog-protection'].includes(testCase.id))
  : [...cases, ...dialogs];
const plans = simpleHostingOnly ? reviewCases.flatMap(testCase => simpleHostingVariants.map(variant => ({ testCase, variant }))) : setupPolishOnly || serverAccessOnly ? reviewCases.flatMap(testCase => setupPolishVariants.map(variant => ({ testCase, variant }))) : reviewCases.flatMap(testCase => [
  ...variants.map(variant => ({ testCase, variant })),
  ...(largeTextPages.has(testCase.id) || testCase.sidebarReview ? [{ testCase, variant: { id: 'lightnarrow150', viewport: { width: 390, height: 844 }, theme: 'light', scale: 150 } }] : []),
  ...(testCase.sidebarReview ? [
    { testCase, variant: { id: 'darkdesktop150', viewport: { width: 1280, height: 900 }, theme: 'dark', scale: 150 } },
    { testCase, variant: { id: 'darkwide', viewport: { width: 1600, height: 1000 }, theme: 'dark', scale: 100 } },
    { testCase, variant: { id: 'darkcompact', viewport: { width: 1280, height: 900 }, theme: 'dark', scale: 100, density: 'compact' } },
  ] : []),
  ...(['dialog-selected-server-picker', 'host-files', 'host-pc-advanced', 'host-backups'].includes(testCase.id) || testCase.id.startsWith('move-hosting-') || testCase.sidebarReview ? [{ testCase, variant: { id: 'darkshortdesktop', viewport: { width: 1280, height: 650 }, theme: 'dark', scale: 100 } }] : []),
]);
manifest.coverage = { scope: serverAccessOnly ? 'server-access' : simpleHostingOnly ? 'simple-shared-hosting' : setupPolishOnly ? 'setup-polish' : args.includes('--move-hosting-only') ? 'move-hosting' : args.includes('--sidebar-only') ? 'sidebars' : 'all',
  mainPages: reviewCases.filter(testCase => !isDialogReview(testCase)).length,
  dialogAndTooltipCases: reviewCases.filter(isDialogReview).length, variants: (simpleHostingOnly ? simpleHostingVariants : setupPolishOnly || serverAccessOnly ? setupPolishVariants : variants).map(item => item.id),
  text150Pages: reviewCases.filter(testCase => largeTextPages.has(testCase.id)).map(testCase => testCase.id), plannedCaptures: plans.length };
manifest.acceptanceBoundaries = [
  'Finite source inventory of pages, reachable dialogs, disclosures and representative states; not every combinatorial data value.',
  'Native startup/runtime/close messages and OS file/folder pickers require approved native Windows acceptance and are not represented as browser screenshots.',
  'Synthetic signed data and test statuses are presentation fixtures. No real signing, peer connection, game readiness, world transfer or game acceptance is performed.',
  'Loading, request failure, empty, blocked and progress states must be labeled by their scenario and exact source in each capture.'
];
manifest.inventory = plans.map(({ testCase, variant }) => ({ ...variant, id: `${testCase.id}--${variant.id}`, scenario: testCase.scenario, workspace: testCase.workspace, section: testCase.section ?? null, expectedScreen: { ...testCase.screen, name: String(testCase.screen.name ?? '') } }));
if (listOnly) {
  process.stdout.write(`${JSON.stringify({ coverage: manifest.coverage, inventory: manifest.inventory }, null, 2)}\n`);
  process.exit(0);
}
await saveManifest();

function locatorFor(page, screen) {
  return screen.css ? page.locator(screen.css).filter({ hasText: screen.text }) : page.getByRole(screen.role, { name: screen.name, exact: typeof screen.name === 'string' });
}
async function visible(locator) { await locator.first().waitFor({ state: 'visible', timeout: 8000 }); return locator.first(); }
// networkidle may describe an earlier lifecycle event. Observe these exact
// intercepted responses and a stable painted interval before judging capture.
async function waitForSyntheticResponses(page, pending, revision, timeoutMs = 8000, now = () => performance.now()) {
  const deadline = now() + timeoutMs;
  const failure = () => new Error(`Synthetic API responses did not settle within ${timeoutMs} ms at capture` +
    (pending.size ? `: ${[...pending].map(item => `${item.method} ${item.path}`).join(', ')}` : ': response activity did not become quiet'));
  const bounded = async promise => {
    const remaining = deadline - now();
    if (remaining <= 0) throw failure();
    let timer;
    try {
      await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(failure()), remaining); })]);
    } finally { clearTimeout(timer); }
  };
  while (now() < deadline) {
    if (pending.size) {
      await bounded(Promise.all([...pending].map(item => item.done)));
      continue;
    }
    const observed = revision();
    await bounded(page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done)))));
    if (!pending.size && revision() === observed) return { pending: 0, responseRevision: observed, stablePaintFrames: 2 };
  }
  throw failure();
}
async function scrollCaptureScreen(page, specification, pending, revision, timeoutMs = 8000, now = () => performance.now()) {
  const deadline = now() + timeoutMs;
  const remaining = () => {
    const value = deadline - now();
    if (value <= 0) throw new Error(`Named capture screen did not remain attached within ${timeoutMs} ms`);
    return value;
  };
  for (let attempt = 1; attempt <= 3; attempt++) {
    await waitForSyntheticResponses(page, pending, revision, remaining(), now);
    // A mode change replaces the workspace. Resolve its requested screen again
    // after the exact responses and paints, including on a detach-only retry.
    const screen = locatorFor(page, specification).first();
    try {
      await screen.waitFor({ state: 'visible', timeout: remaining() });
      await screen.scrollIntoViewIfNeeded({ timeout: remaining() });
      await waitForSyntheticResponses(page, pending, revision, remaining(), now);
      const current = locatorFor(page, specification).first();
      await current.waitFor({ state: 'visible', timeout: remaining() });
      return current;
    } catch (error) {
      if (!/Element is not attached to the DOM/u.test(error.message ?? '')) throw error;
      if (attempt === 3) throw new Error('Named capture screen detached during all three settled scroll attempts', { cause: error });
    }
  }
}
async function navigateHeader(page, workspace, record) {
  const navigation = page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true });
  await visible(navigation);
  record.actions.push({ action: 'navigate-workspace', name: workspace });
  if (workspace === 'Attention') {
    await navigation.getByLabel(/^Notifications/).click();
    await navigation.getByRole('button', { name: 'Open Attention Center', exact: true }).click();
    await visible(page.getByRole('heading', { name: 'Attention Center', exact: true }));
  } else {
    const name = ['Host', 'Join'].includes(workspace) ? 'Servers' : workspace;
    await navigation.getByRole('button', { name, exact: true }).click();
    await visible(navigation.getByRole('button', { name, exact: true }).and(page.locator('[aria-current="page"]')));
  }
}
// Runs inside the page. A section may exceed the pane height; its heading must
// be exposed after focus arrives and the real scroll owner stops moving.
async function settledSettingsJump(sectionId) {
  const read = () => {
    const target = document.querySelector(`[data-settings-section="${sectionId}"]`), heading = target?.querySelector('h2');
    if (!target || !heading || document.activeElement !== target ||
        document.querySelector('select[aria-label="App settings sections"]')?.value !== sectionId) return null;
    let root = target.parentElement;
    while (root && !/(auto|scroll)/.test(getComputedStyle(root).overflowY)) root = root.parentElement;
    if (!root) return null;
    const pane = root.getBoundingClientRect(), bounds = heading.getBoundingClientRect();
    const visible = { top: Math.max(0, pane.top + root.clientTop), left: Math.max(0, pane.left + root.clientLeft),
      bottom: Math.min(innerHeight, pane.top + root.clientTop + root.clientHeight),
      right: Math.min(innerWidth, pane.left + root.clientLeft + root.clientWidth) };
    if (!bounds.width || !bounds.height || bounds.top < visible.top - 1 || bounds.bottom > visible.bottom + 1 ||
        bounds.left < visible.left - 1 || bounds.right > visible.right + 1) return null;
    return { sectionId, scroller: root.className, scrollTop: root.scrollTop,
      headingTop: bounds.top, headingBottom: bounds.bottom, visible };
  };
  let current = read();
  if (!current) return false;
  for (let frame = 0; frame < 3; frame++) {
    await new Promise(done => requestAnimationFrame(done));
    const next = read();
    if (!next || Math.abs(next.scrollTop - current.scrollTop) > .5 || Math.abs(next.headingTop - current.headingTop) > .5) return false;
    current = next;
  }
  return current;
}
async function waitForSettledSettingsJump(page, sectionId) {
  const deadline = performance.now() + 8000;
  while (performance.now() < deadline) {
    // evaluate awaits the helper's three-frame Promise. An async predicate must
    // never be passed to waitForFunction, where its Promise can count as truthy.
    const evidence = await page.evaluate(settledSettingsJump, sectionId);
    if (evidence && typeof evidence === 'object' && evidence.sectionId === sectionId &&
        Number.isFinite(evidence.scrollTop) && Number.isFinite(evidence.headingTop)) return evidence;
    await page.evaluate(() => new Promise(done => requestAnimationFrame(done)));
  }
  throw new Error(`Settings jump to ${sectionId} did not reach a focused, visible heading with the current navigation and settled scroll within 8 seconds`);
}
async function section(page, [label, value, button], record) {
  // Both owned and paired servers now use the selected-server sidebar.
  if (label === 'Join sections') label = 'Selected server sections';
  if (label === 'This Host PC sections') {
    const disclosure = page.locator('.server-sections-disclosure');
    if (!await disclosure.evaluate(el => el.open)) await disclosure.locator(':scope > summary').click();
  }
  record.actions.push({ action: 'select-section', label, value, button });
  const select = page.getByRole('combobox', { name: label, exact: true });
  if (await select.isVisible()) await select.selectOption(value);
  else await page.getByRole('navigation', { name: label, exact: true }).getByRole('button', { name: button, exact: true }).click();
  // Assert the selected navigation state, separately from the screen heading.
  if (await select.isVisible()) {
    await page.waitForFunction(({ label, value }) => [...document.querySelectorAll('select')].some(el => el.getAttribute('aria-label') === label && el.value === value), { label, value });
  } else await visible(page.getByRole('navigation', { name: label, exact: true }).locator(label === 'App settings sections' ? '[aria-current="location"]' : '[aria-current="page"]').filter({ hasText: button }));
  if (label === 'App settings sections') {
    record.assertions.push({ settingsJumpSettled: await waitForSettledSettingsJump(page, value) });
  }
}
async function click(page, name, record, exact = true) {
  record.actions.push({ action: 'click', role: 'button', name: String(name) });
  await page.getByRole('button', { name, exact }).click({ timeout: 8000 });
}
async function performActions(page, testCase, record) {
  if (testCase.section) await section(page, testCase.section, record);
  if (testCase.button) await click(page, new RegExp(testCase.button), record, false);
  switch (testCase.action) {
    case 'shared-verified-copy': case 'shared-receiving': {
      const next = await visible(page.getByRole('region', { name: 'Next shared-save action', exact: true }));
      const verified = testCase.action === 'shared-verified-copy';
      if (testCase.action === 'shared-verified-copy') {
        await visible(next.getByRole('heading', { name: 'Prepare hosting on this PC', exact: true }));
        await visible(page.getByText('Verified save copy 5 on this PC. Copy possession does not grant hosting authority or prove readiness.', { exact: true }));
      } else {
        await visible(next.getByRole('heading', { name: 'Receiving the save copy', exact: true }));
        const progress = await visible(page.getByRole('region', { name: 'Save transfer progress', exact: true }));
        const bytes = await visible(progress.getByRole('progressbar', { name: 'Save bytes received', exact: true }));
        const values = await bytes.evaluate(el => ({ value: el.value, max: el.max, description: el.getAttribute('aria-valuetext') }));
        if (values.value !== 1048576 || values.max !== 4194304 || values.description !== '1 MiB of 4 MiB')
          throw new Error('Receiving byte progress or its accessible description differs from the strict fixture');
      }
      const primary = next.locator('button:not(.secondary):not(.text-button)');
      if (verified) {
        if (await primary.count() !== 1 || (await primary.textContent()).trim() !== 'Host this save' || !await primary.isEnabled())
          throw new Error('Verified-copy landing must expose only the enabled primary action Host this save');
      } else {
        if (await primary.count() > 1) throw new Error('Receiving progress must not compete with multiple primary actions');
        const redundantGet = page.getByRole('button', { name: 'Get latest save', exact: true });
        if (await redundantGet.isVisible() && await redundantGet.isEnabled())
          throw new Error('Receiving progress must disable a redundant Get latest save request');
        await visible(page.getByText('Get latest save is unavailable while this copy is being received, verified or acknowledged.', { exact: true }));
      }
      const manage = await visible(next.getByRole('button', { name: 'Manage sharing', exact: true }));
      if (!await manage.isEnabled()) throw new Error('Shared-save landing must keep Manage sharing reachable');
      if (await page.getByRole('button', { name: 'Review Start server', exact: true }).count())
        throw new Error('Synthetic copy possession must never establish Start authority');
      record.assertions.push(verified ? 'Synthetic verified copy exposes one primary Host this save and secondary Manage sharing; no signed Host or Start authority created' :
        'Synthetic Receiving byte progress leads with at most one primary, redundant receiving blocked, and Manage sharing reachable; no signed Host or Start authority created');
      break;
    }
    case 'compare': await click(page, 'Compare all servers', record); break;
    case 'server-tools': await click(page, 'More server actions', record); break;
    case 'setup': case 'setup-game': {
      await click(page, 'Host a server', record);
      await visible(page.getByRole('heading', { name: 'Choose a game', exact: true }));
      // Capture initial focus before subsequent game/Next actions move it.
      record.dialogInitialFocus = await focusDetails(page);
      if (testCase.game) {
        await click(page, new RegExp(`^${testCase.game}`), record, false);
        await click(page, 'Continue', record);
      }
      break;
    }
    case 'manage-sharing':
      if (testCase.workspace === 'Host') {
        record.actions.push({ action: 'select-protection-section', name: 'Sharing & hosting' });
        await page.getByRole('navigation', { name: 'World protection sections', exact: true }).getByRole('button', { name: 'Sharing & hosting', exact: true }).click();
      }
      await click(page, 'Manage sharing', record); break;
    case 'picker':
      await page.locator('.context-picker > summary').click();
      await visible(page.locator('.context-picker[open] .context-picker-popover'));
      break;
    case 'notifications-menu': case 'notifications-empty':
      await page.locator('.notification-menu > summary[aria-label]').click();
      if (testCase.action === 'notifications-empty') await click(page, 'Clear activity', record);
      break;
    case 'guide-welcome': case 'guide-host': case 'guide-join':
      await click(page, 'Setup guides', record);
      await visible(page.getByRole('dialog', { name: 'Make room for your next game night', exact: true }));
      if (testCase.action !== 'guide-welcome') await click(page, testCase.action === 'guide-host' ? /^Host on this PC/ : /^Join a friend/, record, false);
      break;
    case 'consent': {
      const consent = await visible(page.getByRole('checkbox', { name: 'Allow saves on this PC', exact: true }));
      record.actions.push({ action: 'check', name: 'Allow saves on this PC', fixtureOnly: true });
      if (await consent.isChecked()) throw new Error('Consent fixture must start with receiving disabled');
      const responsePromise = page.waitForResponse(response => response.request().method() === 'PUT' &&
        new URL(response.url()).pathname === `/api/local/friend/${fixtureIds.primary}/shared-world/consent`);
      await consent.click();
      const response = await responsePromise;
      const body = await response.json();
      if (response.status() !== 200 || body.ok !== true || body.status?.consented !== true)
        throw new Error('Synthetic consent response did not confirm receiving enabled');
      await page.waitForFunction(() => [...document.querySelectorAll('input[type="checkbox"]')].some(el => el.checked && el.closest('label')?.textContent.includes('Allow saves on this PC')));
      await visible(page.getByRole('region', { name: 'Next shared-save action', exact: true })
        .getByRole('heading', { name: 'Get the save before preparing hosting', exact: true }));
      record.assertions.push('Synthetic consent completed successfully and the controlled checkbox committed checked state');
      break;
    }
    case 'permitted-controls':
      await visible(page.getByRole('button', { name: 'Restart server', exact: true }));
      await visible(page.getByRole('button', { name: /^Add \d+ minutes$/ }));
      record.assertions.push('Permitted Restart and Add time controls visible; no dispatch requested');
      break;
    case 'permitted-start': {
      const start = await visible(page.getByRole('button', { name: 'Start server', exact: true }));
      if (!await start.isEnabled()) throw new Error('Permitted synthetic Start button is disabled');
      record.assertions.push('Permitted Start control enabled; no dispatch requested'); break;
    }
    case 'history': {
      const disclosure = page.locator('.attention-history-disclosure');
      const summary = await visible(disclosure.locator(':scope > summary').filter({ hasText: 'Activity history' }));
      record.actions.push({ action: 'open-disclosure', name: 'Activity history' });
      if (!await disclosure.evaluate(el => el.open)) await summary.click();
      await visible(page.getByRole('region', { name: 'Activity history', exact: true }).getByRole('heading', { name: /^Activity history/ }));
      await visible(page.getByRole('searchbox', { name: 'Search attention', exact: true }));
      record.assertions.push('Activity history disclosure opened with its genuine history heading and search filter visible');
      break;
    }
    case 'tooltip-hover': case 'tooltip-keyboard': {
      const target = page.locator('.notifications-menu summary, .notification-menu summary, summary[aria-label^="Notifications"]').first();
      await visible(target);
      if (testCase.action === 'tooltip-hover') await target.hover();
      else {
        // Reach the real trigger through keyboard navigation; do not call focus().
        await page.mouse.move(0, 0);
        let reached = false;
        for (let index = 0; index < 100; index++) {
          await page.keyboard.press('Tab');
          reached = await target.evaluate(el => document.activeElement === el);
          if (reached) break;
        }
        if (!reached) throw new Error('Notifications tooltip trigger not reached in 100 keyboard Tab steps');
      }
      record.actions.push({ action: testCase.action, target: 'Notifications summary' });
      const tip = await visible(page.getByRole('tooltip'));
      record.tooltipGeometry = await tip.evaluate(el => {
        const rect = el.getBoundingClientRect(), style = getComputedStyle(el), tail = getComputedStyle(el, '::after');
        return { bounds: rect.toJSON(), side: el.dataset.side, tailOffset: style.getPropertyValue('--tooltip-tail'),
          tail: { content: tail.content, width: tail.width, height: tail.height, top: tail.top, left: tail.left, transform: tail.transform, clipPath: tail.clipPath, border: tail.border, background: tail.backgroundColor } };
      });
      record.tooltipGeometry.triggerBounds = await target.evaluate(el => el.getBoundingClientRect().toJSON());
      const geometry = record.tooltipGeometry;
      const expectedTail = Math.max(10, Math.min(geometry.triggerBounds.left + geometry.triggerBounds.width / 2 - geometry.bounds.left, geometry.bounds.width - 10));
      if (![geometry.tailOffset, geometry.tail.width, geometry.tail.height].every(value => Number.isFinite(Number.parseFloat(value))) ||
          Math.abs(Number.parseFloat(geometry.tailOffset) - expectedTail) > 2 ||
          Number.parseFloat(geometry.tail.width) <= 0 || Number.parseFloat(geometry.tail.height) <= 0 ||
          ['none', 'normal'].includes(geometry.tail.content)) {
        record.layoutFindings.push({ kind: 'tooltip-tail-missing-or-detached', geometry, expectedTail });
      }
      if (await target.getAttribute('title')) record.layoutFindings.push({ kind: 'duplicate-native-tooltip-title' });
      break;
    }
  }
  for (const step of testCase.steps ?? []) {
    const scope = step.within ? page.locator(step.within) : page;
    record.actions.push({ action: 'step', ...step, summary: step.summary ? String(step.summary) : undefined,
      button: step.button ? String(step.button) : undefined });
    if (step.summary) {
      const target = scope.locator('summary').filter({ hasText: step.summary }).first();
      await visible(target);
      if (!await target.evaluate(el => el.parentElement.open)) await target.click();
      await target.evaluate(el => { if (!el.parentElement.open) throw new Error('Expected disclosure did not open'); });
    } else if (step.nav) {
      await scope.getByRole('navigation', { name: step.nav, exact: true }).getByRole('button', { name: step.button, exact: typeof step.button === 'string' }).click();
    } else if (step.fill) {
      // A textarea's initial value is text inside its wrapping label. Use the
      // control's accessible name as well as native label lookup, so contents
      // cannot change the harness selector for File contents or Message.
      const named = ['textbox', 'spinbutton', 'searchbox', 'combobox'].map(role =>
        scope.getByRole(role, { name: step.fill, exact: true }));
      const target = named.reduce((locator, next) => locator.or(next), scope.getByLabel(step.fill, { exact: true }));
      await target.fill(step.value);
    } else if (step.select) {
      await scope.getByLabel(step.select, { exact: true }).selectOption(step.value);
    } else if (step.check) {
      let target = scope.getByRole('checkbox', { name: step.check, exact: true });
      if (await target.count() === 0) target = scope.getByRole('radio', { name: step.check, exact: true });
      if (step.first) target = target.first();
      await target.check();
    } else if (step.uncheck) {
      let target = scope.getByRole('checkbox', { name: step.uncheck, exact: true });
      if (step.first) target = target.first();
      await target.uncheck();
    } else if (step.role) {
      await scope.getByRole(step.role, { name: step.name, exact: true }).click();
    } else if (step.button) {
      let target = scope.getByRole('button', { name: step.button, exact: typeof step.button === 'string' });
      if (step.first) target = target.first();
      if (step.whenVisible && !await target.isVisible()) {
        record.actions.push({ action: 'responsive-control-hidden', button: String(step.button), evidence: 'The asserted inline content must still be visible.' });
        continue;
      }
      await target.click();
    }
  }
}
async function focusDetails(page) {
  return page.evaluate(() => {
    const el = document.activeElement, dialog = [...document.querySelectorAll('dialog[open]')].at(-1);
    return { tag: el?.tagName, id: el?.id, text: (el?.textContent ?? '').trim().slice(0, 180),
      label: el?.getAttribute('aria-label'), insideDialog: !!dialog && (el === dialog || dialog.contains(el)),
      bodyControl: !!el?.closest('.settings-dialog-body'), closeControl: el?.classList.contains('settings-dialog-close') ?? false };
  });
}
async function layoutFindings(page) {
  return page.evaluate(() => {
    function concealedByClosedDetails(el) {
      for (let ancestor = el.parentElement; ancestor; ancestor = ancestor.parentElement) {
        if (ancestor.tagName !== 'DETAILS' || ancestor.open) continue;
        const summary = [...ancestor.children].find(child => child.tagName === 'SUMMARY');
        if (!summary?.contains(el)) return true;
      }
      return false;
    }
    const visible = el => {
      const r = el.getBoundingClientRect(), style = getComputedStyle(el);
      return r.width > 0 && r.height > 0 && style.visibility !== 'hidden' && style.display !== 'none' && !el.closest('[hidden], [inert]') && !concealedByClosedDetails(el);
    };
    const findings = [];
    if (document.documentElement.scrollWidth > innerWidth + 2) findings.push({ kind: 'document-horizontal-overflow', width: document.documentElement.scrollWidth, viewport: innerWidth });
    for (const el of document.querySelectorAll('button,input,select,textarea,summary,[role="tooltip"],dialog[open],h1,h2,h3,h4')) {
      if (!visible(el) || el.closest('.sr-only')) continue;
      const r = el.getBoundingClientRect(), label = (el.getAttribute('aria-label') || el.textContent || el.tagName).trim().slice(0, 120);
      if (r.left < -2 || r.right > innerWidth + 2) findings.push({ kind: 'element-outside-horizontal-viewport', tag: el.tagName, label, bounds: r.toJSON() });
      if ((el.matches('dialog[open],[role="tooltip"]')) && (r.top < -2 || r.bottom > innerHeight + 2)) findings.push({ kind: 'overlay-outside-viewport', label, bounds: r.toJSON() });
      if (el.matches('button') && el.scrollWidth > el.clientWidth + 3) findings.push({ kind: 'possible-clipped-button-text', label, scrollWidth: el.scrollWidth, clientWidth: el.clientWidth });
    }
    return findings;
  });
}

async function independentScrollEvidence(page, record) {
  const panes = await page.locator('.context-sidebar, .context-content').all();
  if (panes.length !== 2) return;
  const initial = await Promise.all(panes.map(pane => pane.evaluate(el => ({ top: el.scrollTop, left: el.scrollLeft,
    height: el.clientHeight, contentHeight: el.scrollHeight }))));
  record.independentScroll = [];
  for (let index = 0; index < panes.length; index++) {
    const other = 1 - index, pane = panes[index], before = await panes[other].evaluate(el => el.scrollTop);
    if (initial[index].contentHeight <= initial[index].height + 2) {
      record.independentScroll.push({ pane: index === 0 ? 'sidebar' : 'content', state: 'fits-without-scroll' }); continue;
    }
    await pane.evaluate(el => { el.scrollTop = 0; });
    const bounds = await pane.boundingBox();
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + Math.min(bounds.height - 20, 120));
    await page.mouse.wheel(0, 240);
    await page.waitForFunction(selector => document.querySelector(selector)?.scrollTop > 0,
      index === 0 ? '.context-sidebar' : '.context-content', { timeout: 5000 });
    const after = await pane.evaluate(el => el.scrollTop), otherAfter = await panes[other].evaluate(el => el.scrollTop);
    record.independentScroll.push({ pane: index === 0 ? 'sidebar' : 'content', state: 'wheel-scrolled', top: after, otherBefore: before, otherAfter });
    if (otherAfter !== before) record.layoutFindings.push({ kind: 'pane-wheel-moved-other-pane', pane: index, otherBefore: before, otherAfter });
    await pane.evaluate((el, top) => { el.scrollTop = top; }, initial[index].top);
  }
  // Keyboard focus must reveal the final sidebar row without scrolling content.
  const sidebar = page.locator('.context-sidebar'), last = sidebar.getByRole('button').last();
  const contentBefore = await page.locator('.context-content').evaluate(el => el.scrollTop);
  await sidebar.focus();
  let reached = false;
  const buttonCount = await sidebar.getByRole('button').count();
  for (let index = 0; index < buttonCount + 8; index++) {
    await page.keyboard.press('Tab');
    if (await last.evaluate(el => document.activeElement === el)) { reached = true; break; }
  }
  const lastBounds = await last.boundingBox(), sidebarBounds = await sidebar.boundingBox();
  record.sidebarKeyboard = { reached, lastBounds, sidebarBounds };
  if (!reached || !lastBounds || lastBounds.y < sidebarBounds.y - 2 || lastBounds.y + lastBounds.height > sidebarBounds.y + sidebarBounds.height + 2)
    record.layoutFindings.push({ kind: 'last-sidebar-row-unreachable', ...record.sidebarKeyboard });
  if (await page.locator('.context-content').evaluate(el => el.scrollTop) !== contentBefore)
    record.layoutFindings.push({ kind: 'sidebar-keyboard-focus-moved-content-pane' });
  await page.keyboard.press('Escape');
  await sidebar.evaluate((el, top) => { el.scrollTop = top; }, initial[0].top);
}

async function sidebarResizeEvidence(page, testCase, record) {
  if (await page.locator('.workspace-nav').count()) throw new Error('The global sidebar is still present');
  const splitter = page.getByRole('separator');
  const viewport = page.viewportSize();
  if (viewport.width <= 700) {
    if (await splitter.isVisible()) throw new Error('Sidebar resizing must be hidden in the stacked layout');
    const sidebar = await page.locator('.context-sidebar').boundingBox();
    const content = await page.locator('.context-content').boundingBox();
    if (content.y < sidebar.y + sidebar.height) throw new Error('Narrow sidebar and content overlap');
    record.assertions.push({ sidebarResize: 'stacked layout, divider hidden', sidebar, content });
    return;
  }
  await visible(splitter);
  const width = () => splitter.evaluate(el => Number(el.getAttribute('aria-valuenow')));
  const initial = await width();
  const initialBounds = await page.locator('.context-sidebar').boundingBox();
  if (initialBounds.x > 1) throw new Error('Page sidebar did not reclaim the left edge');
  const before = `${record.id}--before-resize.png`;
  await page.screenshot({ path: resolve(output, before), animations: 'disabled' });
  record.screenshots.push({ file: before, kind: 'sidebar-before-resize' });
  const bounds = await splitter.boundingBox();
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + Math.min(bounds.height / 2, 220));
  await page.mouse.down();
  await page.mouse.move(bounds.x + bounds.width / 2 + 160, bounds.y + Math.min(bounds.height / 2, 220), { steps: 8 });
  await page.mouse.up();
  const dragged = await width();
  if (dragged <= initial) throw new Error('Dragging did not widen the sidebar');
  const draggedBounds = await page.locator('.context-sidebar').boundingBox();
  if (Math.abs(draggedBounds.width - dragged) > 2) throw new Error('Rendered sidebar width differs from its accessible value');
  await splitter.press('Home');
  if (await width() !== 200) throw new Error('Home did not set the minimum sidebar width');
  await splitter.press('End');
  const maximum = await splitter.evaluate(el => Number(el.getAttribute('aria-valuemax')));
  if (await width() !== maximum) throw new Error('End did not set the available maximum sidebar width');
  await splitter.dblclick();
  if (await width() !== Math.min(268, maximum)) throw new Error('Double-click did not reset sidebar width');
  await splitter.press('End');
  await splitter.press('ArrowLeft');
  const saved = await width();
  await page.reload({ waitUntil: 'domcontentloaded' });
  await visible(page.locator('section.context-workspace'));
  await navigateHeader(page, testCase.workspace, record);
  await visible(splitter);
  if (await width() !== saved) throw new Error('Sidebar width was not remembered after reload');
  await page.setViewportSize({ width: 760, height: viewport.height });
  await page.waitForFunction(() => document.querySelector('.context-content').getBoundingClientRect().width >= 359);
  await page.waitForFunction(saved => Number(document.querySelector('[role="separator"]').getAttribute('aria-valuenow')) < saved, saved);
  const constrained = await width();
  if (constrained >= saved) throw new Error('Sidebar did not shrink to protect content in a smaller window');
  await page.setViewportSize(viewport);
  await page.waitForFunction(saved => Number(document.querySelector('[role="separator"]').getAttribute('aria-valuenow')) === saved, saved);
  record.assertions.push({ sidebarResize: { initial, dragged, maximum, saved, constrained, restored: await width() } });
  await page.mouse.move(0, 0);
  await page.keyboard.press('Escape');
}

async function workspaceChromeEvidence(page, record) {
  const evidence = await page.evaluate(() => {
    const bounds = selector => {
      const element = document.querySelector(selector);
      if (!element) return null;
      const r = element.getBoundingClientRect();
      return r.width && r.height ? { top: r.top, right: r.right, bottom: r.bottom, left: r.left } : null;
    };
    const panes = ['.context-sidebar', '.context-content'].map(bounds);
    const workspace = bounds('.context-workspace'), scroll = bounds('.workspace-scroll');
    const footer = bounds('.status-strip');
    const findings = [];
    if (innerWidth > 700 && workspace && scroll && panes.every(Boolean)) {
      for (const [index, pane] of panes.entries()) {
        for (const edge of ['top', 'bottom'])
          if (Math.abs(pane[edge] - workspace[edge]) > 1)
            findings.push({ kind: 'workspace-pane-edge-gap', pane: index, edge, paneEdge: pane[edge], workspaceEdge: workspace[edge] });
      }
      if (!bounds('.workspace-scroll > .startup-recovery') && Math.abs(workspace.top - scroll.top) > 1)
        findings.push({ kind: 'workspace-header-gap', workspaceTop: workspace.top, scrollTop: scroll.top });
      if (Math.abs(workspace.bottom - scroll.bottom) > 1 || footer && Math.abs(scroll.bottom - footer.top) > 1)
        findings.push({ kind: 'workspace-footer-gap', workspaceBottom: workspace.bottom, scrollBottom: scroll.bottom, footerTop: footer?.top });
      if (Math.abs(panes[1].right - innerWidth) > 1)
        findings.push({ kind: 'workspace-right-gutter', contentRight: panes[1].right, viewport: innerWidth });
    }
    const candidates = new Set(document.querySelectorAll('.context-sidebar,.context-content,dialog[open],textarea,.server-log-console'));
    for (const element of document.querySelectorAll('body *')) {
      const style = getComputedStyle(element);
      if ((element.scrollHeight > element.clientHeight + 1 && /auto|scroll/.test(style.overflowY)) ||
          (element.scrollWidth > element.clientWidth + 1 && /auto|scroll/.test(style.overflowX))) candidates.add(element);
    }
    const scrollbars = [...candidates].flatMap(element => {
      const r = element.getBoundingClientRect();
      if (!r.width || !r.height) return [];
      const bar = getComputedStyle(element, '::-webkit-scrollbar');
      const thumb = getComputedStyle(element, '::-webkit-scrollbar-thumb');
      const button = getComputedStyle(element, '::-webkit-scrollbar-button');
      const sample = { tag: element.tagName, className: element.className, width: bar.width, height: bar.height,
        thumb: thumb.backgroundColor, button: button.display, overflowY: getComputedStyle(element).overflowY };
      if (bar.width !== '10px' || bar.height !== '10px' || button.display !== 'none' ||
          !thumb.backgroundColor || thumb.backgroundColor === 'rgba(0, 0, 0, 0)')
        findings.push({ kind: 'unthemed-scrollbar', ...sample });
      return [sample];
    });
    return { workspace, scroll, panes, footer, scrollbars, findings };
  });
  record.workspaceChrome = evidence;
  record.layoutFindings.push(...evidence.findings);
}

// Runs inside the page. A bounding box alone can hide clipping by a nested pane
// or an overlapping coach. The normal result is truthy only for an exposed target.
function guideTargetExposure(requireExposed = true) {
  const target = document.querySelector('[data-guide-highlight="true"]');
  if (!target) return requireExposed ? false : { exposed: false, reason: 'No highlighted guide target' };
  const box = element => {
    const value = element.getBoundingClientRect();
    return { top: value.top, right: value.right, bottom: value.bottom, left: value.left, width: value.width, height: value.height };
  };
  const bounds = box(target), targetStyle = getComputedStyle(target);
  const clipped = { top: 0, left: 0, right: innerWidth, bottom: innerHeight };
  const clippingAncestors = [];
  for (let ancestor = target.parentElement; ancestor; ancestor = ancestor.parentElement) {
    const style = getComputedStyle(ancestor), value = box(ancestor);
    const clipX = /^(auto|scroll|hidden|clip)$/.test(style.overflowX);
    const clipY = /^(auto|scroll|hidden|clip)$/.test(style.overflowY);
    if (!clipX && !clipY) continue;
    const client = { left: value.left + ancestor.clientLeft, top: value.top + ancestor.clientTop };
    client.right = client.left + ancestor.clientWidth; client.bottom = client.top + ancestor.clientHeight;
    if (clipX) { clipped.left = Math.max(clipped.left, client.left); clipped.right = Math.min(clipped.right, client.right); }
    if (clipY) { clipped.top = Math.max(clipped.top, client.top); clipped.bottom = Math.min(clipped.bottom, client.bottom); }
    clippingAncestors.push({ name: ancestor.getAttribute('aria-label') || ancestor.className || ancestor.tagName, clipX, clipY, client });
  }
  const fullyInside = bounds.width > 0 && bounds.height > 0 && bounds.left >= clipped.left - 1 && bounds.right <= clipped.right + 1 &&
    bounds.top >= clipped.top - 1 && bounds.bottom <= clipped.bottom + 1;
  const coach = document.querySelector('.guide-coach'), coachBounds = coach ? box(coach) : null;
  const coachOverlap = coachBounds ? {
    width: Math.max(0, Math.min(bounds.right, coachBounds.right) - Math.max(bounds.left, coachBounds.left)),
    height: Math.max(0, Math.min(bounds.bottom, coachBounds.bottom) - Math.max(bounds.top, coachBounds.top))
  } : { width: 0, height: 0 };
  const hits = [];
  if (bounds.width > 0 && bounds.height > 0) {
    const insetX = Math.min(4, bounds.width / 4), insetY = Math.min(4, bounds.height / 4);
    for (const x of [bounds.left + insetX, (bounds.left + bounds.right) / 2, bounds.right - insetX])
      for (const y of [bounds.top + insetY, (bounds.top + bounds.bottom) / 2, bounds.bottom - insetY]) {
        const hit = document.elementFromPoint(x, y);
        hits.push({ x, y, target: !!hit && target.contains(hit), hit: hit ? hit.getAttribute('aria-label') || hit.className?.baseVal || hit.className || hit.tagName : null });
      }
  }
  const exposed = fullyInside && targetStyle.visibility !== 'hidden' && targetStyle.display !== 'none' &&
    !target.closest('[hidden], [inert]') && !(coachOverlap.width > 1 && coachOverlap.height > 1) && hits.length === 9 && hits.every(hit => hit.target);
  const evidence = { exposed, tag: target.tagName, text: target.textContent.trim(), bounds, clipped, clippingAncestors,
    coachBounds, coachOverlap, hits, animation: targetStyle.animationName };
  return requireExposed ? exposed ? evidence : false : evidence;
}

async function setupPolishEvidence(page, testCase, variant, record) {
  if (!testCase.id.startsWith('setup-polish-')) return;
  const paint = () => page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
  const shot = async name => {
    const file = `${record.id}--${name}.png`;
    await page.screenshot({ path: resolve(output, file), animations: 'disabled' });
    record.screenshots.push({ file, kind: name });
  };
  const sidebarHeadings = await page.locator('.context-sidebar :is(h2,h3)').allTextContents();
  const names = sidebarHeadings.map(value => value.trim()).filter(Boolean);
  if (new Set(names).size !== names.length) throw new Error('The page sidebar repeats a heading');
  record.assertions.push({ distinctSidebarHeadings: names });
  if (testCase.id.includes('-notifications-')) {
    const spacing = await page.locator('.notification-panel').evaluate(panel => {
      const style = getComputedStyle(panel), heading = panel.querySelector('.notification-panel-heading').getBoundingClientRect();
      const action = panel.querySelector('.notification-center-link').getBoundingClientRect();
      return { padding: [style.paddingTop, style.paddingRight, style.paddingBottom, style.paddingLeft].map(parseFloat),
        actionGap: action.top - heading.bottom, itemCount: panel.querySelectorAll('.notification-item').length,
        emptyCount: panel.querySelectorAll('.notification-empty').length };
    });
    if (spacing.padding.some(value => value < 12) || spacing.actionGap < 10)
      throw new Error(`Notification content has cramped padding or action spacing: ${JSON.stringify(spacing)}`);
    if (testCase.action === 'notifications-empty' ? spacing.emptyCount !== 1 || spacing.itemCount !== 0 : spacing.itemCount < 1)
      throw new Error('Notification contents do not match the empty/activity case');
    record.assertions.push({ notificationSpacing: spacing });
  }
  if (testCase.fixture?.permissions) {
    const chips = await page.locator('.friend-pc-card').first().locator('.friend-grant-chips > li').evaluateAll(elements =>
      elements.map(el => ({ label: el.getAttribute('aria-label'), state: el.dataset.permissionState,
        displayed: getComputedStyle(el).display !== 'none' && el.getBoundingClientRect().height > 0 })));
    const labels = ['Start', 'Stop when empty', 'Restart', 'Add time', 'View logs', 'Game password'];
    const expectedCounts = [1, 1, 0, 2, 1, 0], partial = testCase.fixture.permissions === 'partial';
    if (chips.length !== labels.length || chips.some((chip, index) => !chip.displayed ||
        chip.label !== (partial ? `${labels[index]}: ${expectedCounts[index]} of 2 assigned servers` : `${labels[index]}: no assigned servers`) ||
        chip.state !== (partial && expectedCounts[index] > 0 ? 'granted' : 'unassigned')))
      throw new Error(`All permission states must remain visible and exact: ${JSON.stringify(chips)}`);
    record.assertions.push({ renderedPermissionChips: chips });
  }
  if (testCase.workspace === 'App Settings') {
    const titles = await page.locator('.app-settings-block h2').allTextContents();
    const expected = ['Profile', 'Appearance', 'Notifications', 'Windows behavior', 'App updates', 'Keyboard shortcuts'];
    if (JSON.stringify(titles) !== JSON.stringify(expected)) throw new Error('App Settings does not retain all six continuous sections');
    record.assertions.push({ continuousSettingsSections: titles });
    if (testCase.id.endsWith('-scroll')) {
      const scroller = await page.locator('.app-settings-page').evaluate(page => {
        for (let parent = page.parentElement; parent; parent = parent.parentElement)
          if (/(auto|scroll)/.test(getComputedStyle(parent).overflowY)) return parent.matches('.context-content') ? '.app-settings-workspace > .context-content' : '.workspace-scroll';
        throw new Error('No actual settings scroll container found');
      });
      const desiredTop = await page.locator(scroller).evaluate(root => {
        const target = root.querySelector('[data-settings-section="notifications"]');
        const top = root.scrollTop + target.getBoundingClientRect().top - root.getBoundingClientRect().top - root.clientTop - 16;
        root.scrollTop = top;
        return root.scrollTop;
      });
      await page.waitForFunction(() => document.querySelector('.context-sidebar [aria-current="location"]')?.textContent.trim() === 'Notifications');
      await paint();
      const actualTop = await page.locator(scroller).evaluate(root => root.scrollTop);
      if (Math.abs(actualTop - desiredTop) > 1) throw new Error('Passive settings section tracking moved the viewport');
      record.assertions.push({ passiveSettingsScroll: { scroller, desiredTop, actualTop, current: 'notifications' } });
      await shot('passive-notifications-location');
      await section(page, ['App settings sections', 'shortcuts', 'Keyboard shortcuts'], record);
      await shot('shortcuts-jump-location');
      await section(page, ['App settings sections', 'profile', 'Profile'], record);
      record.assertions.push({ settingsClickJump: { target: 'shortcuts', focusedAndVisible: true,
        reducedMotion: await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches) } });
    }
  }
  if (testCase.action === 'guide-welcome') {
    const dialog = page.getByRole('dialog', { name: 'Make room for your next game night', exact: true });
    await visible(dialog.getByRole('button', { name: /^Host on this PC/ }));
    await visible(dialog.getByRole('button', { name: /^Join a friend/ }));
    record.assertions.push({ guideWelcomeOpenedFromHeader: true });
  }
  if (['guide-host', 'guide-join'].includes(testCase.action)) {
    const targetEvidence = async () => {
      let handle;
      try { handle = await page.waitForFunction(guideTargetExposure); }
      catch {
        const failure = await page.evaluate(guideTargetExposure, false);
        record.assertions.push({ guideTargetNotExposed: failure });
        throw new Error(`Guide target is clipped or covered: ${JSON.stringify(failure)}`);
      }
      const target = await handle.jsonValue();
      await handle.dispose();
      const controls = await page.locator('.guide-coach .guide-coach-top button, .guide-coach footer button').evaluateAll(buttons => buttons.map(button => {
        const bounds = button.getBoundingClientRect(), coach = button.closest('.guide-coach').getBoundingClientRect();
        const hit = document.elementFromPoint(bounds.left + bounds.width / 2, bounds.top + bounds.height / 2);
        return { text: button.getAttribute('aria-label') || button.textContent.trim(),
          exposed: bounds.width > 0 && bounds.height > 0 && bounds.top >= Math.max(0, coach.top) &&
            bounds.bottom <= Math.min(innerHeight, coach.bottom) && bounds.left >= Math.max(0, coach.left) &&
            bounds.right <= Math.min(innerWidth, coach.right) && button.contains(hit) };
      }));
      if (!controls.length || controls.some(control => !control.exposed))
        throw new Error(`Guide buttons must stay visible without scrolling: ${JSON.stringify(controls)}`);
      target.controls = controls;
      if (variant.reducedMotion !== 'no-preference' && target.animation !== 'none') throw new Error('Reduced motion did not disable the guide highlight animation');
      return target;
    };
    const initial = await targetEvidence();
    await page.locator('.guide-coach').getByRole('button', { name: 'Show me', exact: true }).click();
    const located = await targetEvidence();
    if (testCase.action === 'guide-host') {
      await page.locator('.guide-coach').getByRole('button', { name: 'Next tip', exact: true }).click();
      await visible(page.locator('.guide-coach').getByRole('heading', { name: 'Start when you’re ready', exact: true }));
      const next = await targetEvidence();
      await shot('host-guide-next-tip');
      await page.locator('.guide-coach').getByRole('button', { name: 'Back', exact: true }).click();
      await visible(page.locator('.guide-coach').getByRole('heading', { name: 'Make this PC your Host', exact: true }));
      await targetEvidence();
      record.assertions.push({ hostGuideNextBack: { next, noServerActionRequested: true } });
    }
    record.assertions.push({ guideTarget: { initial, located, reducedMotion: variant.reducedMotion ?? 'reduce' } });
  }
  if (testCase.id.includes('-wizard-')) {
    const tip = await visible(page.getByRole('complementary', { name: 'Setup tip', exact: true }));
    const text = await tip.textContent();
    if (testCase.id.endsWith('-game-tip') ? !text.includes('Java and Bedrock are separate editions') :
        !text.includes('SteamCMD is an optional way to install it')) throw new Error('The wizard tip does not match the active stage');
    record.assertions.push({ wizardStageTip: text.trim() });
  }
  if (record.apiRequests.some(request => /\/(start|stop|restart|install|invite)(?:\/|$)/.test(request.path)))
    throw new Error('Setup-polish presentation checks unexpectedly dispatched a server or installation action');
}

function scrollCaptureOffsets(height, contentHeight) {
  if (!Number.isFinite(height) || height <= 0 || !Number.isFinite(contentHeight) || contentHeight < 0)
    throw new Error('Scroll capture requires finite positive pane height and nonnegative content height');
  const maximum = Math.max(0, contentHeight - height);
  if (maximum === 0) return [0];
  const intervals = Math.ceil(maximum / (height * .85));
  // Fixture content is finite. Fail explicitly if unexpected geometry would
  // produce an excessive artifact rather than silently omitting any content.
  if (intervals > 128) throw new Error('Scroll pane needs more than 129 screenshots; complete coverage was not captured');
  return Array.from({ length: intervals + 1 }, (_, index) => index === intervals ? maximum : maximum * index / intervals);
}

function scrollCaptureCoverage(contentHeight, ranges, tolerance = 1) {
  if (!Number.isFinite(contentHeight) || contentHeight < 0 || !Number.isFinite(tolerance) || tolerance < 0)
    throw new Error('Scroll coverage requires finite nonnegative dimensions');
  let end = 0;
  const gaps = [];
  for (const range of [...ranges].sort((left, right) => left.start - right.start)) {
    if (!Number.isFinite(range.start) || !Number.isFinite(range.end) || range.end < range.start)
      throw new Error('Scroll coverage contains an invalid captured range');
    const start = Math.max(0, Math.min(contentHeight, range.start)), next = Math.max(0, Math.min(contentHeight, range.end));
    if (start > end + tolerance) gaps.push({ start: end, end: start });
    end = Math.max(end, next);
  }
  if (end < contentHeight - tolerance) gaps.push({ start: end, end: contentHeight });
  return { complete: gaps.length === 0, contentHeight, coveredThrough: end, tolerance, gaps };
}

function scrollCaptureVisibleRange(el, axis = 'vertical') {
  const horizontal = axis === 'horizontal', edge = horizontal ? 'left' : 'top';
  const clientInset = horizontal ? 'clientLeft' : 'clientTop', clientSize = horizontal ? 'clientWidth' : 'clientHeight';
  const scrollSize = horizontal ? 'scrollWidth' : 'scrollHeight', scrollOffset = horizontal ? 'scrollLeft' : 'scrollTop';
  const bounds = el.getBoundingClientRect(), contentTop = bounds[edge] + el[clientInset];
  const viewportSize = horizontal ? innerWidth : innerHeight;
  let clipTop = 0, clipBottom = viewportSize;
  const clippedBy = [{ name: 'viewport', start: 0, end: viewportSize }], modal = el.closest('dialog:modal');
  // A modal is painted in the browser top layer. Its DOM ancestors outside the
  // modal do not clip it, while ordinary nested readers retain ancestor clips.
  if (el !== modal) for (let parent = el.parentElement; parent; parent = parent.parentElement) {
    if (/^(auto|scroll|hidden|clip)$/.test(getComputedStyle(parent)[horizontal ? 'overflowX' : 'overflowY'])) {
      const parentBounds = parent.getBoundingClientRect(), top = parentBounds[edge] + parent[clientInset];
      const bottom = top + parent[clientSize];
      if (top > clipTop || bottom < clipBottom) clippedBy.push({
        name: parent.getAttribute('aria-label') || parent.className || parent.tagName, start: top, end: bottom
      });
      clipTop = Math.max(clipTop, top); clipBottom = Math.min(clipBottom, bottom);
    }
    if (parent === modal) break;
  }
  const visibleTop = Math.max(0, Math.min(el[clientSize], clipTop - contentTop));
  const visibleBottom = Math.max(visibleTop, Math.min(el[clientSize], clipBottom - contentTop));
  return { [scrollOffset]: el[scrollOffset], start: Math.min(el[scrollSize], el[scrollOffset] + visibleTop),
    end: Math.min(el[scrollSize], el[scrollOffset] + visibleBottom),
    [horizontal ? 'visibleWidth' : 'visibleHeight']: visibleBottom - visibleTop,
    clippedBy: visibleBottom - visibleTop < el[clientSize] - 1 ? clippedBy : [] };
}

function scrollCaptureReaderCoverage(contentWidth, contentHeight, views) {
  const horizontal = views.map(view => scrollCaptureCoverage(contentWidth, view.coveredRanges));
  // A vertical band counts only when every column in that band was pictured.
  // Otherwise separate left-top and right-bottom views could falsely pass.
  const vertical = scrollCaptureCoverage(contentHeight, views.flatMap((view, index) =>
    horizontal[index].complete ? [{ start: view.top, end: view.bottom }] : []));
  return { complete: horizontal.every(coverage => coverage.complete) && vertical.complete,
    horizontal, vertical };
}

async function captureFileDiffEvidence(page, record, output, frame) {
  const readers = page.locator('.server-file-diff-scroll');
  for (let index = 0; index < await readers.count(); index++) {
    const reader = readers.nth(index);
    const geometry = await reader.evaluate(el => {
      const topDialog = [...document.querySelectorAll('dialog[open]')].at(-1);
      if (topDialog && !topDialog.contains(el) || el.closest('[hidden],[inert]')) return null;
      const bounds = el.getBoundingClientRect(), style = getComputedStyle(el);
      if (bounds.width < 1 || bounds.height < 1 || style.visibility === 'hidden') return null;
      return { left: el.scrollLeft, width: el.clientWidth, contentWidth: el.scrollWidth,
        height: el.clientHeight, contentHeight: el.scrollHeight, name: el.getAttribute('aria-label') || el.className };
    });
    if (!geometry || geometry.width < 1 || geometry.contentWidth <= geometry.width + 1) continue;
    let evidence = record.horizontalScrollEvidence.find(item => item.readerIndex === index);
    if (!evidence) {
      evidence = { ...geometry, readerIndex: index, plannedOffsets: scrollCaptureOffsets(geometry.width, geometry.contentWidth), views: [] };
      record.horizontalScrollEvidence.push(evidence);
    }
    const vertical = await reader.evaluate(scrollCaptureVisibleRange);
    const horizontal = await reader.evaluate(scrollCaptureVisibleRange, 'horizontal');
    if (vertical.visibleHeight < 1 || horizontal.visibleWidth < 1) continue;
    const view = { frame, top: 0, bottom: geometry.contentHeight, coveredRanges: [] };
    evidence.views.push(view);
    try {
      for (let segment = 0; segment < evidence.plannedOffsets.length; segment++) {
        await reader.evaluate((el, value) => { el.scrollLeft = value; }, evidence.plannedOffsets[segment]);
        await page.evaluate(() => new Promise(resolveFrame => requestAnimationFrame(() => requestAnimationFrame(resolveFrame))));
        const range = await reader.evaluate(scrollCaptureVisibleRange, 'horizontal');
        const verticalRange = await reader.evaluate(scrollCaptureVisibleRange);
        const position = segment === 0 ? 'left' : segment === evidence.plannedOffsets.length - 1 ? 'right'
          : `segment-${String(segment + 1).padStart(2, '0')}-of-${String(evidence.plannedOffsets.length).padStart(2, '0')}`;
        const filename = `${record.id}--${frame}--file-diff-${index}-${position}.png`;
        await page.screenshot({ path: resolve(output, filename), animations: 'disabled' });
        record.screenshots.push({ file: filename, kind: 'horizontal-scroll-pane', pane: geometry.name, frame, position, ...range, verticalRange });
        view.coveredRanges.push({ file: filename, ...range });
        view.top = Math.max(view.top, verticalRange.start);
        view.bottom = Math.max(view.top, Math.min(view.bottom, verticalRange.end));
      }
    } finally {
      await reader.evaluate((el, left) => { el.scrollLeft = left; }, geometry.left);
    }
  }
}

async function captureScrollEvidence(page, record, output) {
  if (record.id.startsWith('tooltip-') || record.id.startsWith('dialog-selected-server-picker')) return;
  record.horizontalScrollEvidence = [];
  await captureFileDiffEvidence(page, record, output, 'viewport');
  const panes = page.locator('.workspace-scroll, .workspace-scroll *, .context-content, .context-content *, dialog[open], dialog[open] *');
  const indices = await panes.evaluateAll(elements => {
    const topDialog = [...document.querySelectorAll('dialog[open]')].at(-1);
    return elements.flatMap((el, index) => {
      if (topDialog ? el !== topDialog && !topDialog.contains(el) : !el.closest('.workspace-scroll,.context-content')) return [];
      if (el.matches('textarea,input') || el.closest('[hidden],[inert]')) return [];
      const bounds = el.getBoundingClientRect(), style = getComputedStyle(el);
      if (bounds.width < 1 || bounds.height < 40 || style.visibility === 'hidden' ||
          !/auto|scroll/.test(style.overflowY) || el.scrollHeight <= el.clientHeight + 10) return [];
      for (let ancestor = el.parentElement; ancestor; ancestor = ancestor.parentElement) {
        if (ancestor.tagName === 'DETAILS' && !ancestor.open) {
          const summary = [...ancestor.children].find(child => child.tagName === 'SUMMARY');
          if (!summary?.contains(el)) return [];
        }
      }
      return [index];
    });
  });
  record.scrollEvidence = [];
  for (const index of indices) {
    const pane = panes.nth(index);
    if (!await pane.isVisible()) continue;
    const geometry = await pane.evaluate(el => ({ top: el.scrollTop, height: el.clientHeight, contentHeight: el.scrollHeight,
      name: el.getAttribute('aria-label') || el.className }));
    if (geometry.contentHeight <= geometry.height + 10) continue;
    const offsets = scrollCaptureOffsets(geometry.height, geometry.contentHeight);
    // Reveal nested readers such as .host-sharing-pane before recording their
    // offsets, then restore the surrounding workspace/dialog reading position.
    const ancestorScroll = await pane.evaluate(el => {
      const positions = [];
      for (let parent = el.parentElement; parent; parent = parent.parentElement)
        positions.push({ top: parent.scrollTop, left: parent.scrollLeft });
      return positions;
    });
    await pane.scrollIntoViewIfNeeded();
    const evidence = { ...geometry, paneIndex: index, plannedOffsets: offsets, capturedOffsets: [], coveredRanges: [] };
    record.scrollEvidence.push(evidence);
    try {
      for (let segment = 0; segment < offsets.length; segment++) {
        await pane.evaluate((el, value) => { el.scrollTop = value; }, offsets[segment]);
        await page.evaluate(() => new Promise(resolveFrame => requestAnimationFrame(() => requestAnimationFrame(resolveFrame))));
        // Use actual browser scroll offsets and the viewport intersection, so
        // rounding or a clipped pane cannot turn missing pixels into a pass.
        const range = await pane.evaluate(scrollCaptureVisibleRange);
        const position = segment === 0 ? 'top' : segment === offsets.length - 1 ? 'bottom'
          : offsets.length === 3 ? 'middle' : `segment-${String(segment + 1).padStart(2, '0')}-of-${String(offsets.length).padStart(2, '0')}`;
        const filename = `${record.id}--pane-${index}-${position}.png`;
        await page.screenshot({ path: resolve(output, filename), animations: 'disabled' });
        record.screenshots.push({ file: filename, kind: 'scroll-pane', pane: geometry.name, position, ...range });
        evidence.capturedOffsets.push(range.scrollTop);
        evidence.coveredRanges.push({ file: filename, ...range });
        await captureFileDiffEvidence(page, record, output, `pane-${index}-${position}`);
      }
      evidence.coverage = scrollCaptureCoverage(geometry.contentHeight, evidence.coveredRanges);
      if (!evidence.coverage.complete) {
        record.layoutFindings.push({ kind: 'scroll-capture-incomplete', pane: geometry.name, ...evidence.coverage });
        throw new Error(`Scrollable pane ${geometry.name} has unpictured content: ${JSON.stringify(evidence.coverage.gaps)}`);
      }
    } finally {
      await pane.evaluate((el, top) => { el.scrollTop = top; }, geometry.top);
      await pane.evaluate((el, positions) => {
        let index = 0;
        for (let parent = el.parentElement; parent; parent = parent.parentElement) {
          parent.scrollTop = positions[index].top; parent.scrollLeft = positions[index].left; index++;
        }
      }, ancestorScroll);
    }
  }
  for (const evidence of record.horizontalScrollEvidence) {
    evidence.coverage = scrollCaptureReaderCoverage(evidence.contentWidth, evidence.contentHeight, evidence.views);
    if (!evidence.coverage.complete) {
      record.layoutFindings.push({ kind: 'file-diff-capture-incomplete', pane: evidence.name, ...evidence.coverage });
      throw new Error(`File comparison ${evidence.name} has unpictured rows or columns`);
    }
  }
}

async function pickerEvidence(page, record) {
  const picker = await visible(page.locator('.context-picker[open] .context-picker-popover'));
  const bounds = await picker.boundingBox();
  const hits = await picker.locator('button').evaluateAll(buttons => buttons.map(button => {
    const box = button.getBoundingClientRect(), sampleX = box.left + Math.min(box.width / 2, 60), sampleY = box.top + box.height / 2;
    const within = sampleX >= 0 && sampleX < innerWidth && sampleY >= 0 && sampleY < innerHeight;
    const hit = within ? document.elementFromPoint(sampleX, sampleY) : null;
    const parent = button.closest('.context-picker-popover').getBoundingClientRect();
    const visibleInPicker = sampleY >= parent.top && sampleY < parent.bottom;
    return { label: button.getAttribute('aria-label') || button.textContent.trim(), bounds: box.toJSON(), visibleInPicker,
      hit: !visibleInPicker || hit === button || button.contains(hit) };
  }));
  record.pickerEvidence = { bounds, hits };
  if (hits.some(item => !item.hit)) record.layoutFindings.push({ kind: 'picker-button-covered', hits: hits.filter(item => !item.hit) });
  if (bounds.x < -2 || bounds.y < -2 || bounds.x + bounds.width > record.viewport.width + 2 || bounds.y + bounds.height > record.viewport.height + 2)
    record.layoutFindings.push({ kind: 'picker-outside-viewport', bounds });
}

async function writeGallery() {
  const escape = value => String(value).replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
  const rows = manifest.cases.map(record => `<article data-search="${escape(`${record.id} ${record.scenario} ${record.workspace} ${record.status}`.toLowerCase())}"><header><h2>${escape(record.id)}</h2><p>${escape(record.status)} · ${escape(record.scenario)}</p></header>${record.error ? `<pre>${escape(record.error)}</pre>` : ''}<div class="shots">${record.screenshots.map(shot => `<a href="${escape(shot.file)}"><img loading="lazy" src="${escape(shot.file)}" alt="${escape(`${record.id}: ${shot.kind} ${shot.position ?? ''}`)}"><span>${escape(shot.file)}</span></a>`).join('')}</div><a href="${escape(record.id)}.json">Assertions and findings</a></article>`).join('\n');
  await writeFile(resolve(output, 'index.html'), `<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>TogetherServer UI screenshot review</title><style>body{margin:24px;background:#121720;color:#e5eaf3;font:15px system-ui}h1{font-size:24px}h2{font-size:17px}p{color:#aeb8ca}input{padding:12px;width:min(90%,600px)}article{border:1px solid #46516a;border-radius:12px;padding:18px;margin-top:20px}.shots{display:flex;gap:14px;overflow-x:auto}.shots a{flex:0 0 320px}.shots img{width:320px;max-height:350px;object-fit:contain;object-position:top;background:#080b10}a{color:#abcaff}span{display:block;font-size:12px;overflow-wrap:anywhere}pre{white-space:pre-wrap}[hidden]{display:none}</style><h1>TogetherServer · bundled UI screenshot review</h1><p>Synthetic data only. Native Windows, real game, network and save acceptance remain separate.</p><p>Revision ${escape(manifest.build.sourceRevision)} · ${escape(JSON.stringify(manifest.summary))}</p><label>Filter screens <input type="search" id="filter" placeholder="Try sharing, narrow, error, dialog, files"></label><main>${rows}</main><script>document.querySelector('#filter').addEventListener('input',event=>{const words=event.target.value.toLowerCase().split(/\s+/).filter(Boolean);document.querySelectorAll('article').forEach(article=>{article.hidden=!words.every(word=>article.dataset.search.includes(word))})})</script></html>\n`);
}

let server, browser;
try {
  const dist = await realpath(resolve(root, 'ui/dist'));
  const indexBytes = await readFile(resolve(dist, 'index.html'));
  manifest.build.indexSha256 = createHash('sha256').update(indexBytes).digest('hex');
  manifest.build.sourceRevision = process.env.GITHUB_SHA ?? null;
  const mime = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.json': 'application/json' };
  server = createServer(async (req, res) => {
    try {
      if (!['GET', 'HEAD'].includes(req.method)) { res.writeHead(405).end(); return; }
      const pathname = decodeURIComponent(new URL(req.url, 'http://127.0.0.1').pathname);
      if (pathname.startsWith('/api/')) { res.writeHead(500).end('API must be intercepted by synthetic fixtures'); return; }
      const target = await realpath(resolve(dist, `.${pathname === '/' ? '/index.html' : pathname}`));
      const rel = relative(dist, target);
      if (rel.startsWith('..') || isAbsolute(rel) || !(await stat(target)).isFile()) { res.writeHead(403).end(); return; }
      res.writeHead(200, { 'Content-Type': mime[extname(target)] ?? 'application/octet-stream', 'Cache-Control': 'no-store',
        'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'" });
      res.end(req.method === 'HEAD' ? undefined : await readFile(target));
    } catch { res.writeHead(404).end('Built UI file not found'); }
  });
  await new Promise((resolveListen, rejectListen) => { server.once('error', rejectListen); server.listen(0, '127.0.0.1', resolveListen); });
  const origin = `http://127.0.0.1:${server.address().port}`;
  // Resolve exclusively from existing locked ui dependencies; no npx/install in the renderer.
  const requireUI = createRequire(resolve(root, 'ui/package.json'));
  const { chromium } = requireUI('playwright');
  browser = await chromium.launch({ headless: true });
  const runPlan = async ({ testCase, variant }) => {
    const record = { id: `${testCase.id}--${variant.id}`, scenario: testCase.scenario, workspace: testCase.workspace,
      viewport: variant.viewport, theme: variant.theme, scale: variant.scale, density: variant.density ?? 'comfortable', status: 'running',
      actions: [], assertions: [], skippedSelectors: [], failedSelectors: [], jsErrors: [], consoleErrors: [],
      unhandledFixtures: [], deliberateFailures: [], blockedNetwork: [], failedRequests: [], httpFailures: [], layoutFindings: [], screenshots: [], apiRequests: [] };
    manifest.cases.push(record);
    let context;
    try {
      const state = createFixtureState({ id: testCase.scenario, ...testCase.fixture });
      if (testCase.sidebarReview) {
        for (const profile of state.host.settings.profiles) profile.name = `${profile.name} · Our weekend adventure with a very long server name`;
        for (const profile of state.friend.profiles) profile.name = `${profile.name} · Our friends' shared adventure with a very long server name`;
      }
      context = await browser.newContext({ viewport: variant.viewport, colorScheme: variant.theme, deviceScaleFactor: 1, serviceWorkers: 'block', acceptDownloads: false, locale: 'en-US', timezoneId: 'UTC', reducedMotion: variant.reducedMotion ?? 'reduce' });
      const pending = new Set(), deliberatelyFailedURLs = new Set(), deliberatelyPendingURLs = new Set();
      let responseRevision = 0;
      await context.route('**/*', async route => {
        const request = route.request(), url = new URL(request.url());
        if (url.origin !== origin) {
          record.blockedNetwork.push({ url: request.url(), method: request.method() });
          await route.abort('blockedbyclient'); return;
        }
        if (url.pathname.startsWith('/api/local/')) {
          let settled;
          const token = { method: request.method(), path: `${url.pathname}${url.search}`,
            done: new Promise(resolve => { settled = resolve; }) };
          pending.add(token); responseRevision++;
          try {
            let body;
            try { body = request.postDataJSON(); } catch { body = request.postData(); }
            const fixture = routeFixture(state, { method: request.method(), path: `${url.pathname}${url.search}`, body });
            record.apiRequests.push({ method: request.method(), path: `${url.pathname}${url.search}`, status: fixture?.status ?? 501 });
            if (fixture?.pending) {
              deliberatelyPendingURLs.add(request.url());
              record.deliberateFailures.push({ kind: 'intentional-pending-request', method: request.method(), path: url.pathname,
                evidence: 'Synthetic loading state; request deliberately remains unresolved until this isolated context closes.' });
              return;
            }
            if (!fixture) {
              record.unhandledFixtures.push({ method: request.method(), path: `${url.pathname}${url.search}` });
              await route.fulfill({ status: 501, json: { code: 'UnhandledVisualFixture', message: 'No synthetic fixture for this route; no application call made.' } });
            } else {
              if (fixture.deliberateFailure) { deliberatelyFailedURLs.add(request.url()); record.deliberateFailures.push({ method: request.method(), path: `${url.pathname}${url.search}`, status: fixture.status, reason: fixture.deliberateFailure, evidence: 'Intentional synthetic error state; no success claim for this API surface.' }); }
              await route.fulfill({ status: fixture.status, json: fixture.body });
            }
          } catch (error) {
            record.unhandledFixtures.push({ method: request.method(), path: `${url.pathname}${url.search}`, reason: `Fixture handler threw: ${error.message}` });
            await route.fulfill({ status: 500, json: { code: 'VisualFixtureException', message: 'Synthetic fixture handler failed; no application call made.' } });
          } finally { pending.delete(token); responseRevision++; settled(); }
          return;
        }
        if (url.pathname.startsWith('/api/')) { record.blockedNetwork.push({ url: request.url(), reason: 'Non-local API forbidden' }); await route.abort('blockedbyclient'); return; }
        await route.continue();
      });
      await context.routeWebSocket('**/*', socket => { record.blockedNetwork.push({ url: socket.url(), reason: 'WebSocket forbidden' }); socket.close(); });
      const page = await context.newPage();
      page.setDefaultTimeout(8000);
      page.on('pageerror', error => record.jsErrors.push(error.message));
      page.on('console', message => {
        if (message.type() !== 'error') return;
        const entry = { text: message.text(), location: message.location() };
        if (deliberatelyFailedURLs.has(entry.location.url) && /^Failed to load resource: the server responded with a status of/.test(entry.text)) record.deliberateFailures.push({ ...entry, kind: 'expected-browser-http-console' });
        else record.consoleErrors.push(entry);
      });
      page.on('requestfailed', request => {
        if (deliberatelyPendingURLs.has(request.url())) return;
        record.failedRequests.push({ url: request.url(), error: request.failure()?.errorText });
      });
      page.on('response', response => {
        if (response.status() >= 400 && !deliberatelyFailedURLs.has(response.url())) record.httpFailures.push({ url: response.url(), status: response.status() });
      });
      await page.addInitScript(({ theme, scale, now, density = 'comfortable' }) => {
        // Fixed synthetic observation time; keep real timers for visible-state waits.
        const RealDate = Date, fixed = RealDate.parse(now);
        globalThis.Date = class extends RealDate { constructor(...values) { super(...(values.length ? values : [fixed])); } static now() { return fixed; } };
        let fakeId = 0;
        Object.defineProperty(globalThis.crypto, 'randomUUID', { value: () => `aaaaaaaa-aaaa-4aaa-8aaa-${String(++fakeId).padStart(12, '0')}` });
        localStorage.setItem('togetherserver.ui-preferences.v1', JSON.stringify({ theme, density, textScale: scale, highContrast: false, quietMode: false, selectedServer: '', favoriteServers: [], favoriteHosts: [], serverOrder: [], serverTabs: {} }));
      }, { ...variant, now: fixtureNow });
      await page.goto(origin, { waitUntil: 'domcontentloaded' });
      await visible(page.getByRole('navigation', { name: 'TogetherServer navigation', exact: true }));
      if (!testCase.bootOnly) {
        // Navigation exists before the snapshot loads. Wait for a real mode workspace before clicking it.
        await visible(page.locator('section.context-workspace'));
        await page.waitForLoadState('networkidle', { timeout: 8000 });
        await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolveFrame => requestAnimationFrame(() => requestAnimationFrame(resolveFrame))); });
        await navigateHeader(page, testCase.workspace, record);
        if (testCase.workspace === 'Host' || testCase.workspace === 'Join') {
          const source = testCase.workspace === 'Host' ? state.host.settings.profiles[0] : state.friend.profiles[0];
          if (source) {
            const owner = testCase.workspace === 'Host' ? 'this PC' : state.friend.hostProfileName || state.friend.connectionName || 'Friend Host';
            const name = `Open ${source.name} on ${owner}`;
            await visible(page.getByRole('button', { name, exact: true }));
            await page.getByRole('button', { name, exact: true }).click();
            record.actions.push({ action: 'select-saved-server', name });
            await visible(page.locator('section.context-workspace'));
          }
        }
        await performActions(page, testCase, record);
      }
      // A click can still be finishing Host -> Friend navigation. Settle the
      // current responses and painted UI before resolving or scrolling a pane.
      if (!testCase.pendingState) await page.waitForLoadState('networkidle', { timeout: 8000 });
      record.assertions.push({ syntheticResponsesSettled: await waitForSyntheticResponses(page, pending, () => responseRevision) });
      let screen = await visible(locatorFor(page, testCase.screen));
      record.assertions.push({ correctNamedScreen: { ...testCase.screen, name: String(testCase.screen.name ?? '') } });
      const hasDialog = await page.locator('dialog[open]').count() > 0;
      if (hasDialog && !record.dialogInitialFocus) record.dialogInitialFocus = await focusDetails(page);
      if (!testCase.action?.startsWith('tooltip') && testCase.action !== 'picker' && !testCase.id.startsWith('setup-polish-settings-')) {
        record.actions.push({ action: 'scroll-asserted-screen-into-view' });
        screen = await scrollCaptureScreen(page, testCase.screen, pending, () => responseRevision);
      }
      // Wait for UI fetches/fonts/paint using signals, never arbitrary sleep.
      await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolveFrame => requestAnimationFrame(() => requestAnimationFrame(resolveFrame))); });
      if (testCase.id.endsWith('-chat-empty')) {
        const messages = await visible(page.getByRole('log', { name: 'Messages', exact: true }));
        if (await messages.locator('.server-chat-message').count() !== 0)
          throw new Error('The empty-chat fixture rendered a message or queued entry');
        await visible(messages.getByText('No messages yet.', { exact: true }));
        record.assertions.push({ emptyChatHasNoMessagesOrQueue: true });
      }
      if (testCase.id === 'join-connection-expired') {
        await visible(page.locator('.friend-access-expired').getByText('Access expired', { exact: true }));
        if (state.friend.state !== 'Access expired' || state.friend.connectionCode !== 'AccessExpired' || state.friend.profiles.length)
          throw new Error('The expired-access fixture does not match the actual denied Friend snapshot');
        record.assertions.push({ ownerDeadlineNoticeVisible: true, noStaleAuthorizedProfiles: true });
      }
      if (testCase.id.startsWith('host-server-files-')) {
        if (!await page.locator('[aria-label="Server controls"]').getByRole('button', { name: 'Start server', exact: true }).isEnabled())
          throw new Error('Unopened file or player editors incorrectly block Start without unsaved changes');
        record.assertions.push({ unopenedEditorsLeaveStartAvailable: true });
      }
      await visible(screen);
      // Workspace navigation can retain a focus tooltip even after hover exits.
      // Escape dismisses it through the UI; never send Escape into an open dialog.
      if (!hasDialog && !testCase.action?.startsWith('tooltip') && !testCase.action?.startsWith('guide-') && testCase.action !== 'picker') {
        await page.mouse.move(0, 0);
        if (await page.getByRole('tooltip').count()) {
          const headerMenu = page.locator('.header-tools > details[open]');
          if (await headerMenu.count()) {
            // Escape correctly closes the menu too. Move keyboard focus into
            // its real first control to dismiss only the trigger tooltip.
            await page.keyboard.press('Tab');
            await visible(headerMenu);
            if (!await headerMenu.evaluate(el => el.contains(document.activeElement)))
              throw new Error('Tooltip cleanup moved keyboard focus outside the open header menu');
            record.actions.push({ action: 'dismiss-page-tooltip', method: 'hover-exit-and-Tab-inside-header-menu' });
          } else {
            await page.keyboard.press('Escape');
            record.actions.push({ action: 'dismiss-page-tooltip', method: 'hover-exit-and-Escape' });
          }
          await page.getByRole('tooltip').waitFor({ state: 'hidden', timeout: 8000 });
        }
      }
      await setupPolishEvidence(page, testCase, variant, record);
      await assertSimpleHostingUi(page, testCase, record, state);
      await assertServerAccessUi(page, testCase, record, state);
      record.screenBounds = await screen.boundingBox();
      if (!record.screenBounds || record.screenBounds.y >= variant.viewport.height || record.screenBounds.y + record.screenBounds.height <= 0) {
        throw new Error('The named screen is outside the capture viewport');
      }
      if (hasDialog && !record.dialogInitialFocus.insideDialog) record.layoutFindings.push({ kind: 'dialog-initial-focus-outside', ...record.dialogInitialFocus });
      if (hasDialog && record.dialogInitialFocus.closeControl) record.layoutFindings.push({ kind: 'dialog-initial-focus-on-close', ...record.dialogInitialFocus });
      if (testCase.action === 'picker') {
        if (hasDialog) record.pickerEvidence = { state: 'under-active-modal', expectedInert: true,
          evidence: 'The asserted confirmation dialog is the active surface; underlying picker buttons are expected to fail hit testing.' };
        else await pickerEvidence(page, record);
      }
      if (testCase.sidebarReview) await sidebarResizeEvidence(page, testCase, record);
      await workspaceChromeEvidence(page, record);
      record.layoutFindings.push(...await layoutFindings(page));
      if (testCase.id.startsWith('move-hosting-')) {
        const spacing = await page.evaluate(() => {
          const results = [];
          const check = (first, next, minimum, label, maximum) => {
            if (!first || !next) return;
            const a = first.getBoundingClientRect(), b = next.getBoundingClientRect();
            if (!a.width || !a.height || !b.width || !b.height) return;
            results.push({ label, minimum, maximum, actual: Math.round((b.top - a.bottom) * 100) / 100 });
          };
          for (const copy of document.querySelectorAll('.host-sharing-summary-copy')) {
            check(copy.querySelector('.host-sharing-summary-title'), copy.querySelector('p'), 6, 'save title to status');
            check(copy.querySelector('p'), copy.querySelector('small'), 6, 'save status to explanation');
          }
          for (const people of document.querySelectorAll('.host-sharing-direct-people')) {
            check(people.querySelector('h3'), people.querySelector('p'), 6, 'sharing heading to explanation');
            check(people.querySelector('p'), people.querySelector('.host-sharing-people'), 12, 'sharing explanation to PC cards');
          }
          for (const person of document.querySelectorAll('.host-sharing-person')) {
            check(person.querySelector('header'), person.querySelector('.host-sharing-host-permission'), 12, 'PC heading to hosting permission');
            const permission = person.querySelector('.host-sharing-host-permission');
            check(permission?.querySelector('strong'), permission?.querySelector('small'), 6, 'permission title to explanation');
            check(permission, permission?.nextElementSibling, 12, 'hosting permission to following detail');
            const advanced = person.querySelector('.host-sharing-advanced-permissions');
            check(advanced, advanced?.nextElementSibling, 10, 'advanced permissions to help');
          }
          for (const field of document.querySelectorAll('.host-sharing-handoff-field, .local-hosting-flow > label:not(.local-hosting-consent), .local-hosting-fix > label')) {
            // A text node's range measures the actual label ink above its control.
            const label = [...field.childNodes].find(node => node.nodeType === Node.TEXT_NODE && node.textContent.trim());
            if (label) {
              const range = document.createRange(); range.selectNodeContents(label);
              check(range, field.querySelector('select, input:not([type="checkbox"])'), 6, 'hosting field label to control');
            }
          }
          for (const flow of document.querySelectorAll('.host-sharing-handoff')) {
            const heading = flow.querySelector(':scope > h4');
            const copy = heading?.nextElementSibling;
            const maximum = parseFloat(getComputedStyle(flow).rowGap) + .5;
            check(heading, copy, 12, 'move heading to explanation', maximum);
            if (copy?.nextElementSibling?.matches('label'))
              check(copy, copy.nextElementSibling, 12, 'move explanation to PC field', maximum);
          }
          for (const card of document.querySelectorAll('.local-hosting-card'))
            check(card.querySelector('h3'), card.querySelector('p'), 4, 'local hosting heading to explanation');
          for (const item of document.querySelectorAll('.local-hosting-checks li'))
            check(item.querySelector('strong'), item.querySelector('p'), 4, 'preflight title to explanation');
          return results;
        });
        record.assertions.push({ sharingSpacing: spacing });
        record.layoutFindings.push(...spacing.filter(item => item.actual < item.minimum || item.actual > item.maximum)
          .map(item => ({ kind: 'inconsistent-sharing-spacing', ...item })));
        if (hasDialog && await page.getByLabel("Next PC's HTTPS address and port", { exact: true }).count())
          throw new Error('Move hosting still asks the owner to enter the paired PC address.');
      }
      const filename = `${record.id}.png`;
      await page.screenshot({ path: resolve(output, filename), animations: 'disabled' });
      record.screenshots.push({ file: filename, kind: 'viewport' });
      await captureScrollEvidence(page, record, output);
      // An open modal makes the workspace inert. Its own complete scroll
      // evidence is captured above; wheel input cannot scroll background panes.
      if (!hasDialog && variant.id === 'darkshortdesktop' && testCase.action !== 'picker') await independentScrollEvidence(page, record);
      // A full page companion makes below-fold content reviewable; the viewport shot preserves overlay placement.
      if (!hasDialog && testCase.action?.startsWith('tooltip') !== true && await page.evaluate(() => document.documentElement.scrollHeight > innerHeight + 10)) {
        const full = `${record.id}--full.png`;
        await page.screenshot({ path: resolve(output, full), fullPage: true, animations: 'disabled' });
        record.screenshots.push({ file: full, kind: 'full-page' });
      }
      record.status = [record.unhandledFixtures, record.jsErrors, record.consoleErrors, record.blockedNetwork, record.failedRequests, record.httpFailures].some(items => items.length) ? 'failed' : record.layoutFindings.length ? 'captured-with-layout-findings' : record.deliberateFailures.length ? 'captured-intentional-error-state' : 'captured';
    } catch (error) {
      record.status = 'failed'; record.error = error.stack ?? String(error);
      record.failedSelectors.push({ expected: { ...testCase.screen, name: String(testCase.screen.name ?? '') }, action: record.actions.at(-1) ?? null, reason: error.message });
      if (context) {
        const page = context.pages()[0];
        if (page && !page.isClosed()) {
          const file = `${record.id}--FAILED.png`;
          try { await page.screenshot({ path: resolve(output, file), animations: 'disabled', timeout: 5000 }); record.screenshots.push({ file, kind: 'failure-evidence-not-accepted-screen' }); } catch (captureError) { record.captureError = captureError.message; }
        }
      }
    } finally {
      if (context) {
        try { await context.close(); }
        catch (error) { record.status = 'failed'; record.cleanupError = error.message; }
      }
      // Include errors emitted after the screenshot or while closing this isolated context.
      if ([record.unhandledFixtures, record.jsErrors, record.consoleErrors, record.blockedNetwork, record.failedRequests, record.httpFailures].some(items => items.length)) record.status = 'failed';
      const caseTemporary = resolve(output, `${record.id}.json.tmp`);
      await writeFile(caseTemporary, `${JSON.stringify(record, null, 2)}\n`);
      await rename(caseTemporary, resolve(output, `${record.id}.json`));
      await saveManifest();
      console.log(`${record.status}: ${record.id}`);
    }
  };
  let nextPlan = 0;
  // Three isolated contexts keep UI-only screenshot review bounded on one
  // hosted runner. Each plan owns its fixture state, context and filenames.
  manifest.execution = { concurrentContexts: 3, browsers: 1, assetServers: 1, serializedManifest: true };
  await Promise.all(Array.from({ length: Math.min(3, plans.length) }, async () => {
    while (nextPlan < plans.length) {
      const planIndex = nextPlan++;
      try { await runPlan(plans[planIndex]); }
      catch (error) {
        // An infrastructure/write failure must not abandon the remaining
        // inventory or close the browser under other active contexts.
        manifest.fatalErrors.push(`Capture ${planIndex} ${plans[planIndex].testCase.id}: ${error.stack ?? String(error)}`);
      }
    }
  }));
} catch (error) {
  manifest.fatalErrors.push(error.stack ?? String(error));
} finally {
  if (browser) await browser.close();
  if (server) await new Promise(resolveClose => server.close(resolveClose));
  const attempted = new Set(manifest.cases.map(entry => entry.id));
  manifest.skippedCases = manifest.inventory.filter(entry => !attempted.has(entry.id)).map(entry => ({ ...entry, status: 'skipped', reason: 'Run ended before this case; see fatalErrors. No screen assertion or capture performed.' }));
  manifest.summary = { planned: plans.length, attempted: manifest.cases.length, screenshots: manifest.cases.reduce((count, entry) => count + entry.screenshots.length, 0),
    failed: manifest.cases.filter(entry => entry.status === 'failed').length, layoutFindings: manifest.cases.reduce((count, entry) => count + entry.layoutFindings.length, 0),
    intentionalErrorCases: manifest.cases.filter(entry => entry.deliberateFailures.length).length, skipped: manifest.skippedCases.length };
  manifest.status = manifest.fatalErrors.length || manifest.summary.failed || manifest.summary.skipped ? 'failed' : manifest.summary.layoutFindings ? 'captured-with-layout-findings' : 'captured-awaiting-visual-inspection';
  await saveManifest();
  await writeGallery();
  if (manifest.status === 'failed' || manifest.summary.layoutFindings > 0) process.exitCode = 1;
}
