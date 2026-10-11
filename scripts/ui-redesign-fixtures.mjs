/** Synthetic UI transport. Never dispatches HTTP, launches processes, or reads local app data. */
import { readFileSync } from 'node:fs';

const contract = JSON.parse(readFileSync(new URL('../contracts/host-snapshot.v1.json', import.meta.url), 'utf8'));
export const fixtureNow = '2026-10-10T16:00:00Z';
export const FIXED_NOW = Date.parse(fixtureNow);
export const fixtureIds = Object.freeze({
  primary: '11111111-1111-4111-8111-111111111111', secondary: '22222222-2222-4222-8222-222222222222',
  third: '33333333-3333-4333-8333-333333333333', host: '44444444-4444-4444-8444-444444444444',
  device: '55555555-5555-4555-8555-555555555555', otherDevice: '66666666-6666-4666-8666-666666666666',
  connection: '77777777-7777-4777-8777-777777777777', backup: '88888888-8888-4888-8888-888888888888',
  session: '99999999-9999-4999-8999-999999999999'
});
const ids = fixtureIds;
const root = 'C:\\TogetherServer-Synthetic';
const endpoint = 'https://192.0.2.10:5131';
const hash = 'A'.repeat(64);
const runId = '1'.repeat(32);
const boundary = 'Synthetic UI fixture only. No real game, network, save or readiness acceptance.';
const clone = value => structuredClone(value);
const result = (extra = {}) => ({ ok: true, code: 'SyntheticFixture', message: boundary, ...extra });
const reply = body => ({ status: 200, body: clone(body) });
const unavailable = feature => ({ status: 503, deliberateFailure: `Synthetic fixture unavailable: ${feature}`,
  body: { ok: false, code: 'SyntheticFixtureUnavailable', message: `${feature} is deliberately unavailable in this synthetic UI fixture.` } });
const unavailableState = (feature, body) => ({ ...reply(body), deliberateFailure: `Synthetic fixture unavailable state: ${feature}` });
const unsupportedSharedGets = new Set(['shared-world/handoff/restore', 'shared-world/recovery/offer-code',
  'shared-world/recovery/separate', 'shared-world/resolution/heads', 'shared-world/resolution/offers']);
const unsupportedSharedPosts = new Set(['shared-world/check', 'shared-world/pull', 'shared-world/successor-enrollment',
  'shared-world/route-check', 'shared-world/history/review', 'shared-world/live-orphan/quarantine',
  'shared-world/handoff/offer', 'shared-world/handoff/complete', 'shared-world/handoff/cancel',
  'shared-world/handoff/stage', 'shared-world/handoff/restore', 'shared-world/handoff/finish',
  'shared-world/recovery/offer', 'shared-world/recovery/vote', 'shared-world/recovery/separate',
  'shared-world/recovery/separate/route-check', 'shared-world/resolution/invitations',
  'shared-world/live/capture', 'shared-world/live/cancel', 'shared-world/live/resume']);
const unsupportedNativePosts = new Set(['/api/local/quit', '/api/local/update/install',
  '/api/local/network/detect-public-ip', '/api/local/network/test-friend-route', '/api/local/minecraft/install',
  '/api/local/minecraft/browse', '/api/local/valheim/browse-server', '/api/local/valheim/browse-world',
  '/api/local/valheim/browse-world-folder', '/api/local/valheim/import', '/api/local/setup/import-confirm',
  '/api/local/factorio/preview-save', '/api/local/factorio/browse-executable',
  '/api/local/terraria/preview-world', '/api/local/terraria/browse-executable',
  '/api/local/custom/browse-working-directory', '/api/local/move-kit/inspect',
  '/api/local/shared-worlds/browse-server-file', '/api/local/shared-world/repair-rosters']);

/** Inventory describes data presets; the renderer owns page/tab navigation. */
export const fixtureScenarios = Object.freeze([
  'host-ready', 'host-offline', 'host-starting', 'host-unknown', 'host-countdown', 'host-maintenance',
  'host-multi', 'host-first-use', 'host-attention', 'host-shared', 'host-managed-sharing',
  'friend-connected', 'friend-permitted', 'friend-pairing', 'friend-unpaired', 'friend-disabled', 'friend-offline', 'friend-disconnected', 'friend-expired',
  'friend-consent', 'friend-shared', 'friend-shared-verified-offline', 'friend-shared-receiving-offline',
  'friend-managed-sharing', 'settings', 'setup-valheim',
  'setup-java', 'setup-bedrock', 'setup-factorio', 'setup-terraria', 'setup-custom'
]);

function profile(id, kind, name, gamePort) {
  return { ...clone(contract.settings.profiles[0]), id, kind, name, serverName: name,
    worldId: name.replaceAll(' ', '-').toLowerCase(), worldDirectory: `${root}\\worlds\\${id}`,
    executablePath: `${root}\\server-apps\\${kind}\\server.exe`, gamePort,
    minecraft: kind === 'MinecraftJava' ? { serverJarPath: `${root}\\server-apps\\Java\\server.jar` } : null,
    custom: kind === 'Custom' ? { gameName: 'Custom game', primaryProtocol: 'UDP', shareJoinAddress: true, additionalPorts: [] } : null,
    maintenance: { enabled: false, message: '' }, sharedSavesEnabled: false };
}
function makeRun(p, state = 'Ready', count = 2) {
  const ready = state === 'Ready';
  return { ...clone(contract.runs[0]), profileId: p.id, runOperationId: state === 'Offline' ? null : ids.session,
    state, detail: boundary, processId: state === 'Offline' ? null : 4242,
    onlinePlayers: ready ? count : null, maxPlayers: ready ? 10 : null, playerCountTrusted: ready && count !== null,
    playerObservationSource: ready && count !== null ? 'FixtureReady' : null,
    playerCountObservedUtc: ready && count !== null ? fixtureNow : null,
    hostAddedTime: false, friendAddedMinutes: 0, addedShutdownMinutes: 0, autoShutdownAtUtc: null,
    autoShutdownReason: ready && count > 0 ? 'Waiting for the server to be empty.' : 'Player count is Unknown.' };
}
function backupStatus(id) {
  return { profileId: id, lastSuccessfulUtc: '2026-10-10T15:00:00Z', lastFailureUtc: null, lastFailure: null,
    completedCount: 1, retainedSizeBytes: 4194304, availableSpaceBytes: 68719476736 };
}
function device(id, name) {
  return { id, profileId: ids.primary, assignedProfileIds: [ids.primary], name, canStart: true, canStop: true,
    canExtendTimer: true, canViewLogs: true, saveReceiveProfileIds: [ids.primary],
    sharedWorldGrants: { [ids.primary]: { receive: true, eligibleHost: false, recoveryVoter: false, manageSharing: false } },
    sharedWorldKeyEnrolled: true, revoked: false, paired: true, approvalPending: false,
    credentialExpiresUtc: null, lastHeartbeatUtc: fixtureNow, serverPermissions: [],
    accessExpiresUtc: null, accessExpired: false, temporaryHelperUntilUtc: null, temporaryHelperActive: false };
}
function publicProfile(p, r) {
  return { id: p.id, runOperationId: r.runOperationId, name: p.name, kind: p.kind, gameKind: p.kind,
    state: r.state, joinAddress: r.state === 'Ready' ? `192.0.2.10:${p.gamePort}` : null,
    onlinePlayers: r.onlinePlayers, maxPlayers: r.maxPlayers, canStart: true, canStop: true,
    canStopNow: r.state === 'Ready' && r.onlinePlayers === 0, stopReason: r.onlinePlayers === 0 ? null : 'Players are online.',
    canRestartNow: r.state === 'Ready' && r.onlinePlayers === 0, restartReason: r.onlinePlayers === 0 ? null : 'Players are online.', canExtendTimer: true,
    timerExtensionMinutes: 15, timerExtensionRemainingMinutes: 60, canViewLogs: true,
    autoShutdownAtUtc: r.autoShutdownAtUtc, autoShutdownReason: r.autoShutdownReason,
    maintenanceEnabled: p.maintenance.enabled, maintenanceMessage: p.maintenance.message, operation: null };
}

export function createFixtureState(scenario = 'host-ready') {
  const options = typeof scenario === 'string' ? { id: scenario } : scenario;
  const name = options.id ?? options.name ?? 'host-ready';
  const preset = String(name).toLowerCase();
  const receivedSaveState = preset === 'friend-shared-verified-offline' ? 'verified' :
    preset === 'friend-shared-receiving-offline' ? 'receiving' : null;
  const firstUse = /first-use|firstuse|setup/.test(preset);
  const host = clone(contract);
  host.settings.profiles = firstUse ? [] : [profile(ids.primary, 'Valheim', 'Weekend Valheim', 2456)];
  if (/multi|compare/.test(preset)) host.settings.profiles.push(
    profile(ids.secondary, 'MinecraftJava', 'Minecraft Java', 25565), profile(ids.third, 'MinecraftBedrock', 'Bedrock world', 19132));
  host.evidence = boundary;
  Object.assign(host.settings, { maxConcurrentServers: 3, autoShutdownEnabled: /countdown/.test(preset), keepAwakeWhileHosting: false,
    remoteControlsEnabled: true, companionListeningEnabled: true, companionEndpoint: endpoint, publicGameIp: '192.0.2.10',
    publicGameIpCheckedUtc: fixtureNow, connectionRoute: { mode: 'DirectInternet', address: '192.0.2.10' } });
  host.runs = host.settings.profiles.map((p, index) => makeRun(p, index === 2 || /offline|maintenance/.test(preset) ? 'Offline' : /starting/.test(preset) ? 'Starting' : 'Ready', /countdown/.test(preset) ? 0 : 2));
  if (/unknown|attention|multi/.test(preset) && host.runs[0]) Object.assign(host.runs[0], { onlinePlayers: null, playerCountTrusted: false, playerCountObservedUtc: null, playerObservationSource: null });
  if (/countdown/.test(preset) && host.runs[0]) Object.assign(host.runs[0], { autoShutdownAtUtc: '2026-10-10T16:15:00Z', autoShutdownReason: 'Synthetic empty-server countdown.' });
  if (/maintenance/.test(preset) && host.settings.profiles[0]) host.settings.profiles[0].maintenance = { enabled: true, message: 'Review settings before the next session.' };
  if (/shared|sharing/.test(preset) && host.settings.profiles[0]) host.settings.profiles[0].sharedSavesEnabled = true;
  host.passwordConfigured = Object.fromEntries(host.settings.profiles.map(p => [p.id, p.kind === 'Valheim']));
  host.managedWorldsRoot = `${root}\\worlds`;
  host.backups = Object.fromEntries(host.settings.profiles.map(p => [p.id, backupStatus(p.id)]));
  host.customCertifications = {}; host.crashRecovery = {};
  host.activity = [ { id: 'synthetic-backup', occurredUtc: '2026-10-10T15:00:00Z', category: 'Backup', action: 'Completed',
    message: 'Synthetic completed backup is available for review.', severity: 'Info', profileId: ids.primary, deviceId: null, visibility: 'Owner' } ];
  if (/attention|multi/.test(preset)) host.activity.unshift({ id: 'synthetic-warning', occurredUtc: fixtureNow,
    category: 'Network', action: 'RouteUnknown', message: 'Outside Friend route has not been checked.', severity: 'Warning', profileId: ids.primary, deviceId: null, visibility: 'Owner' });
  host.hostingPower = { settingEnabled: false, managedServerRunning: host.runs.some(r => r.state !== 'Offline'), requestActive: false, state: 'Disabled', message: boundary };
  host.storageHealth = { checkedUtc: fixtureNow, locations: [{ id: 'worlds', label: 'Managed worlds', state: 'Available', detail: boundary,
    availableSpaceBytes: 68719476736, usedBytes: 8388608, measurementTruncated: false }], warnings: [],
    resources: { logicalProcessors: 8, processWorkingSetBytes: 134217728, managedMemoryBytes: 33554432,
      totalAvailableMemoryBytes: 8589934592, evidenceBoundary: boundary } };
  host.startupRecovery = { previousSessionInterrupted: false, previousStartedUtc: null, items: [], message: boundary, windowsRestartRegistered: false };
  const p = host.settings.profiles[0] ?? profile(ids.primary, 'Valheim', 'Weekend Valheim', 2456);
  const r = host.runs[0] ?? makeRun(p);
  if (/permitted/.test(preset)) Object.assign(r, { onlinePlayers: 0, playerCountTrusted: true });
  const unpaired = /unpaired|pairing/.test(preset);
  const friend = { mode: 'Friend', state: unpaired ? 'Unpaired' : /expired/.test(preset) ? 'AccessExpired' : /disabled/.test(preset) ? 'Disabled' : /friend-disconnected/.test(preset) ? 'Disconnected' : 'Connected',
    detail: boundary, endpoint: unpaired ? '' : endpoint, lastConnectedUtc: unpaired || /friend-disconnected/.test(preset) ? null : fixtureNow,
    remoteControlsEnabled: !/disabled/.test(preset), canStart: true, canStop: true, profiles: unpaired ? [] : [publicProfile(p, r)],
    connectionId: unpaired ? '' : ids.connection, connections: null, connectionName: unpaired ? null : 'Weekend Host',
    hostId: ids.host, hostVersion: '0.3.2', friendVersion: '0.3.2', hostProtocolVersion: 1, protocolCompatible: true,
    hostCapabilities: ['server-chat-v1', 'server-logs-v1', 'shared-worlds-v2'], credentialExpiresUtc: null,
    certificateExpiresUtc: '2027-10-10T16:00:00Z', expiryWarning: null, routeMode: 'DirectInternet', routeAddress: '192.0.2.10',
    activity: clone(host.activity), chatProfiles: unpaired ? [] : [{ id: p.id, name: p.name, supported: true }] };
  const state = { scenario: name, now: fixtureNow, mode: /^friend|^join/.test(preset) ? 'Friend' : 'Host', host, friend,
    devices: [device(ids.device, 'Friend PC'), device(ids.otherDevice, 'Second Friend PC')],
    consented: receivedSaveState !== null || !/consent|friend-shared/.test(preset), receivedSaveState,
    sharingEnabled: /shared|sharing/.test(preset), manageSharing: /managed-sharing|friend-shared/.test(preset),
    drafts: {}, chatEntries: {}, desktopPreferences: { available: true, launchAtLogin: false, closeToTray: false, startupAvailable: true },
    notifications: { quietMode: false, allowedEvents: ['Lifecycle', 'Backup', 'Access', 'Update'], servers: [], systemState: 'AcceptsNotifications', systemAllowsNotifications: true },
    update: { state: 'Current', currentVersion: '0.3.2', latestVersion: null, message: boundary, publisherTrust: 'Development build',
      releaseNotes: '', releaseNotesUrl: null, snoozedUntilUtc: null, promptSnoozed: false, versionSkipped: false },
    requests: [] };
  return state;
}

function chat(state, id, host) {
  return result({ hostId: ids.host, profileId: id, entries: state.chatEntries[id] ?? [
    { id: 'synthetic-message-1', hostId: ids.host, profileId: id, authorId: ids.device, author: 'Friend PC',
      sentUtc: '2026-10-10T15:55:00Z', text: 'Ready for the next session?', signature: 'synthetic-signature' } ],
    pending: [], members: host ? state.devices.map(d => ({ deviceId: d.id, name: d.name, allowed: true })) : null,
    notice: null, noticeCached: false, noticeSupported: true });
}
function sharing(state) {
  return { available: true, canManage: state.manageSharing, selfDeviceId: ids.device, revision: 1,
    code: 'SyntheticFixture', message: boundary, checkedUtc: fixtureNow,
    members: state.manageSharing ? state.devices.map(d => ({ deviceId: d.id, grants: d.sharedWorldGrants[ids.primary],
      revoked: false, accessExpiresUtc: null, isSelf: d.id === ids.device })) : [] };
}

/** Return null for unknown routes; the caller MUST reject them, never proxy them. */
export function routeFixture(state, request) {
  const method = (request.method ?? 'GET').toUpperCase();
  const input = String(request.path ?? '');
  if (!input.startsWith('/api/local/')) return null;
  let url;
  try { url = new URL(input, 'http://fixture.invalid'); } catch { return null; }
  const path = url.pathname;
  let body = request.body ?? {};
  if (typeof body === 'string') { try { body = JSON.parse(body); } catch { return { status: 400, body: { code: 'SyntheticInvalidJson', message: 'Invalid synthetic request body.' } }; } }
  state.requests.push({ method, path });
  const snapshot = () => state.mode === 'Host' ? state.host : state.friend;
  if (path === '/api/local/snapshot' && method === 'GET') return reply(snapshot());
  if (/^\/api\/local\/mode\/(host|friend)$/i.test(path) && method === 'POST') {
    state.mode = path.toLowerCase().endsWith('/host') ? 'Host' : 'Friend'; return reply(result());
  }
  if (path === '/api/local/instance' && method === 'GET') return reply({ kind: 'Staging', displayName: 'TogetherServer SYNTHETIC',
    isStaging: true, freshWorldsOnly: false, startupAvailable: false, updatesAvailable: false, localPort: 5128,
    companionPort: 5132, valheimPort: 2458, minecraftJavaPort: 25566, minecraftBedrockPort: 19134,
    dataRoot: root, dataIsolation: boundary });
  if (path === '/api/local/data-recovery' && method === 'GET') return reply(state.host.recovery);
  if (path === '/api/local/companion' && method === 'GET') return reply({ listenerActive: true, listenerState: 'Listening', listenerWarning: null,
    endpoint, fingerprint: hash, certificates: { hostId: ids.host, activeFingerprint: hash, activeExpiresUtc: '2027-10-10T16:00:00Z',
      nextFingerprint: null, nextExpiresUtc: null, previousFingerprint: null, previousAcceptedUntilUtc: null },
    route: state.host.settings.connectionRoute, devices: state.devices, stopSafety: Object.fromEntries(state.host.runs.map(r => [r.profileId,
      { available: r.onlinePlayers === 0 && r.playerCountTrusted, reason: 'Synthetic player-count evidence only.' }])) });
  if (path === '/api/local/desktop/preferences' && (method === 'GET' || method === 'PUT')) {
    if (method === 'PUT') for (const key of ['launchAtLogin', 'closeToTray']) if (typeof body[key] === 'boolean') state.desktopPreferences[key] = body[key];
    return reply(method === 'PUT' ? result({ preferences: state.desktopPreferences }) : state.desktopPreferences);
  }
  if (path === '/api/local/desktop/notifications' && (method === 'GET' || method === 'PUT')) {
    if (method === 'PUT') { if (typeof body.quietMode === 'boolean') state.notifications.quietMode = body.quietMode;
      if (Array.isArray(body.allowedEvents) && !body.profileId) state.notifications.allowedEvents = clone(body.allowedEvents); }
    return reply(state.notifications);
  }
  if (path === '/api/local/update' && method === 'GET' || path === '/api/local/update/check' && method === 'POST') return reply(state.update);
  if (path === '/api/local/update/preparation' && method === 'GET') return reply({ stage: 'Idle', message: boundary, downloadedBytes: 0, totalBytes: null, blocker: null });
  if (path === '/api/local/network/ports' && method === 'GET') return reply({ checkedUtc: fixtureNow,
    control: { port: 5131, state: 'Open on PC', detail: boundary, remoteState: 'Unknown', remoteDetail: 'Outside route was not checked.',
      bindAddress: '127.0.0.1', bindScope: 'Loopback only', endpoint, endpointState: 'Address hint', endpointDetail: boundary, lanAddresses: [], lanForwardDetail: boundary },
    games: state.host.settings.profiles.map(p => ({ profileId: p.id, label: p.name, ports: [p.gamePort], protocol: 'UDP', state: 'Unknown', detail: boundary, kind: p.kind, routeKind: 'Unknown' })) });
  if (path === '/api/local/network/routes' && method === 'GET') return reply({ privateMeshCandidates: [], advancedCandidates: [] });
  if (path === '/api/local/diagnostics' && method === 'GET') return reply({ generatedUtc: fixtureNow, evidenceBoundary: boundary, truncated: false,
    servers: state.host.settings.profiles.map(p => ({ profileId: p.id, label: p.name, kind: p.kind, checks: [
      { id: 'synthetic-readiness', label: 'Game readiness', state: 'Unverified', detail: boundary, nextAction: 'Review the server overview.', location: 'Host', tone: 'Attention', observedUtc: null }] })),
    sharedChecks: [] });
  if (path === '/api/local/settings' && method === 'PUT') return unavailable('Persistent settings changes');
  if (path.startsWith('/api/local/ui-drafts')) {
    const key = JSON.stringify([body.purpose, body.profileId, body.connectionId ?? null, body.key]);
    const draft = state.drafts[key] ?? { text: null, revision: 0 };
    if (path.endsWith('/read') && method === 'POST') return reply({ ok: true, ...draft, message: boundary });
    if (path === '/api/local/ui-drafts' && method === 'PUT' || path === '/api/local/ui-drafts/clear' && method === 'POST') {
      if (body.expectedRevision !== draft.revision) return reply({ ok: false, ...draft, message: 'Synthetic draft revision changed.' });
      state.drafts[key] = { text: path.endsWith('/clear') ? null : body.text ?? null, revision: draft.revision + 1 };
      return reply({ ok: true, ...state.drafts[key], message: boundary });
    }
  }
  if (path === '/api/local/friend/poll' && method === 'POST') return reply(state.friend);
  if (path === '/api/local/friend/pair' && method === 'POST') {
    const paired = createFixtureState('friend-connected').friend; state.friend = paired; state.mode = 'Friend'; return reply(result());
  }
  if (/^\/api\/local\/friend\/connections\/[^/]+\/select$/.test(path) && method === 'POST') return reply(result());
  const match = /^\/api\/local\/(profiles|friend)\/([^/]+)\/(.+)$/.exec(path);
  const chatMatch = /^\/api\/local\/friend\/connections\/[^/]+\/servers\/([^/]+)\/(chat.*)$/.exec(path);
  if (match || chatMatch) {
    // Saved-connection routes also match the legacy Friend prefix. Prefer the
    // specific room route so "connections" cannot become a profile identity.
    const host = !chatMatch && match?.[1] === 'profiles'; const id = chatMatch?.[1] ?? match[2]; const suffix = chatMatch?.[2] ?? match[3];
    const p = state.host.settings.profiles.find(p => p.id === id) ?? profile(id, 'Valheim', 'Weekend Valheim', 2456);
    if (suffix === 'chat' && method === 'GET') return reply(chat(state, id, host));
    if (suffix === 'chat/sync' && !host && method === 'POST') return reply({ ...chat(state, id, false), code: 'ChatSynced' });
    if (suffix.startsWith('chat')) return unavailable('Chat writes');
    if (suffix === 'logs' && method === 'GET') return reply(result({ sourceState: 'Active', runId, cursor: 'synthetic-cursor', hasMore: false,
      records: [{ timestampUtc: fixtureNow, severity: 'Info', category: 'Server', stream: 'stdout', message: 'Synthetic log: server observation requested.' },
        { timestampUtc: fixtureNow, severity: 'Warning', category: 'Network', stream: 'stdout', message: 'Synthetic log: external route is unverified.' }] }));
    if (suffix === 'sessions' && method === 'GET') return reply(result({ profileId: id, sessions: [{ profileId: id, operationId: ids.session,
      gameKind: p.kind, startedUtc: '2026-10-09T18:00:00Z', endedUtc: '2026-10-09T20:00:00Z', durationSeconds: 7200,
      readyEverObserved: true, endReason: 'GracefulStop', outcome: 'GracefulStop', crashRecoveryScheduled: false,
      lastTrustedOnlinePlayers: 0, maximumTrustedOnlinePlayers: 3, backupResult: 'Completed' }] }));
    if (suffix === 'weekly-summary' && method === 'GET') return unavailableState('Seven-day archive', result({ profileId: id, summary: null, ok: false, code: 'SyntheticUnavailable', message: 'No seven-day archive in this synthetic fixture.' }));
    if (suffix === 'backups' && method === 'GET') return reply({ backups: [{ id: ids.backup, profileId: id, kind: p.kind, worldId: p.worldId,
      backupKind: 'Manual', createdUtc: '2026-10-10T15:00:00Z', sizeBytes: 4194304, fileCount: 2, label: 'Before the weekend', pinned: true, setupIncluded: true }], status: backupStatus(id) });
    if (suffix === 'backup-catalog' && method === 'GET') return reply(result({ profileId: id,
      backups: [{ backupId: ids.backup, profileId: id, createdUtc: '2026-10-10T15:00:00Z', backupKind: 'Manual', sizeBytes: 4194304,
        label: 'Before the weekend', pinned: true, gameKind: p.kind, worldId: p.worldId, fileCount: 2, setupIncluded: true,
        payloadSha256: hash, setupSha256: hash, metadataAvailable: true, evidence: [] }], pinnedCount: 1, pinnedSizeBytes: 4194304,
      maximumLabelLength: 64, maximumPinnedCount: 20, maximumPinnedSizeBytes: 50 * 1024 ** 3, moreBackupsAvailable: false,
      retention: { retentionCount: 5, minimumFreeSpaceBytes: 1073741824, rollingEnabled: true },
      retainedSizeBytes: 4194304, availableSpaceBytes: 68719476736, evidenceAvailable: true }));
    if (suffix === 'files' && method === 'GET') return reply(result({ locations: [{ key: 'world', label: 'Managed world', path: p.worldDirectory, available: true }],
      files: [{ key: 'server-properties', label: 'Server properties', path: `${root}\\server.properties`, available: true }] }));
    if (suffix.startsWith('files/') && method === 'GET') return reply(result({ key: suffix.slice(6), content: '# Synthetic configuration\nmax-players=10\n', sha256: hash, canUndo: false }));
    if (suffix === 'game-settings' && method === 'GET') return reply({ ok: true, code: 'SyntheticFixture', message: boundary, kind: p.kind,
      sha256: p.kind.startsWith('Minecraft') ? hash : null, settings: p.kind.startsWith('Minecraft') ?
        { difficulty: 'normal', maximumPlayers: 10, gameMode: 'survival', allowListEnabled: false, forceGameMode: false } : null,
      canUndo: false, lists: p.kind === 'Valheim' ? [{ key: 'admin-list', label: 'Administrators', available: false }] : [] });
    if (suffix === 'addons' && method === 'GET') return reply(result({ gameVersion: 'Unknown', requiredOnFriendPc: boundary, stateToken: 'synthetic',
      items: [], canImport: false, canUndo: false, warning: null, importType: null }));
    if (suffix === 'acceptance' && method === 'GET') return reply({ profileId: id, stale: false, updatedUtc: null, checks: [],
      evidenceBoundary: boundary, gameFilesAvailable: false, gameFilesChanged: false });
    if (suffix === 'game-password/reveal' && method === 'POST') return reply(result({ password: 'SYNTHETIC-ONLY' }));
    if ((suffix === 'requirements' || suffix === 'compatibility') && method === 'GET') return unavailableState('Game compatibility inventory', result({ ok: false, requirements: null,
      ...(suffix === 'compatibility' ? { clientVersion: null, clientVersionSource: 'Unknown', versionComparison: 'Unknown', addOnComparison: 'Unknown' } : {}) }));
    if (suffix === 'shared-world' && host && method === 'GET') return reply({ enabled: state.sharingEnabled, latest: null,
      error: null, confirmedCopies: 0, canManageSharing: true, authority: null, liveSave: { available: false, message: boundary } });
    if (suffix === 'shared-world' && host && method === 'PUT') { state.sharingEnabled = body.enabled === true; return reply(result()); }
    if (suffix === 'shared-world/governance' && method === 'GET') return reply(state.sharingEnabled ? { revision: 1, ownerOverride: true,
      members: state.devices.map(d => ({ deviceId: d.id, grants: d.sharedWorldGrants[ids.primary], revoked: false, accessExpiresUtc: null })) } : null);
    if (suffix === 'shared-world/handoff' && host && method === 'GET') return reply({ pending: false, code: 'None', message: boundary,
      successorDeviceId: null, finalVersion: null, receiptConfirmed: false, canComplete: false, canCancel: false });
    if (suffix === 'shared-world/live-orphan' && method === 'GET') return reply({ code: 'None', message: boundary, versionHash: null });
    const friendShared = () => ({ consented: state.consented, hostVersion: null, thisPcVersion: null, state: state.consented ? 'No published copy' : 'Consent needed',
      error: null, completedUtc: null, receivedUtc: null, receiptConfirmed: null, receivedBytes: 0, totalBytes: 0, transferPhase: null,
      capacityNotice: null, capacityState: null, rosterRevision: state.sharingEnabled ? 1 : null,
      trust: state.sharingEnabled ? 'VerifiedSignedRoster' : 'Unverified',
      ...(state.consented && state.receivedSaveState === 'verified' ? { hostVersion: 5, thisPcVersion: 5, state: 'Ready',
        completedUtc: fixtureNow, receivedUtc: fixtureNow, receiptConfirmed: true,
        receivedBytes: 4194304, totalBytes: 4194304 } : {}),
      ...(state.consented && state.receivedSaveState === 'receiving' ? { hostVersion: 5, state: 'Receiving',
        completedUtc: fixtureNow, receivedBytes: 1048576, totalBytes: 4194304, transferPhase: 'Receiving' } : {}) });
    if (suffix === 'shared-world' && !host && method === 'GET') return reply(friendShared());
    if (suffix === 'shared-world/consent' && !host && method === 'PUT') { state.consented = body.enabled === true || body.consented === true;
      return reply(result({ status: friendShared() })); }
    if (suffix === 'shared-world/sharing/check' && method === 'POST') return reply(sharing(state));
    if (suffix === 'shared-world/sharing' && (method === 'PUT' || method === 'POST')) {
      const target = state.devices.find(d => d.id === body.deviceId && d.id !== ids.device);
      if (target && state.manageSharing) for (const key of ['receive', 'eligibleHost', 'recoveryVoter'])
        if (typeof body[key] === 'boolean') target.sharedWorldGrants[ids.primary][key] = body[key];
      return reply(method === 'POST' ? result({ status: friendShared() }) : sharing(state));
    }
    if (suffix === 'shared-world/recovery' && method === 'GET') return reply({ state: 'NoOffer', votes: 0, required: 0,
      version: null, versionHash: null, candidateAddress: null, candidateDeviceId: null, majorityReached: false,
      separateCopies: 0, proposalHash: null, authorityHeadHash: null });
    // Copy possession and transfer progress never manufacture a signed successor or Start readiness.
    if (suffix === 'shared-world/handoff/restore' && !host && method === 'GET' && state.receivedSaveState !== null)
      return reply({ staged: false, restored: false, recordHash: null, message: boundary, pendingChecks: [],
        preparedServerRoot: null, readyForManualStart: false, requiredAddOns: [],
        controlRouteFingerprint: null, controlRouteAddress: null });
    if (method === 'GET' && unsupportedSharedGets.has(suffix) || method === 'POST' && unsupportedSharedPosts.has(suffix) ||
      method === 'POST' && /^shared-world\/resolution\/(approve|vote|offer|override)\/[a-f\d]{64}$/i.test(suffix))
      return unavailable('Shared-save signed transfer, hosting or recovery');
    if (method === 'POST' && /^(start|stop|restart|safe-restart|resume-hosting|forget|open-game|health|players\/refresh|extend|refresh|replace|probe-game|countdown\/extend)$/.test(suffix)) return unavailable('Game, lifecycle or network actions');
  }
  if (path === '/api/local/valheim/discover' && method === 'GET') return reply({ installations: [{ executablePath: `${root}\\server-apps\\Valheim\\valheim_server.exe`, source: 'Synthetic discovery' }], worlds: [] });
  if (path === '/api/local/minecraft/discover' && method === 'GET') return reply({ installations: [], javaRuntimePath: '' });
  if (/^\/api\/local\/servers\/[^/]+\/invite\/current$/.test(path) && method === 'POST') return reply({ exists: true, open: true, canStart: true, requireApproval: true });
  if (/^\/api\/local\/servers\/[^/]+\/invite$/.test(path) && method === 'POST') return reply(result({ password: 'SYNTHETIC-INVITE-NOT-VALID', expiresUtc: null, listenerActive: true, listenerWarning: null }));
  if (method === 'POST' && unsupportedNativePosts.has(path)) return unavailable('Native, file, install or external actions');
  if (method === 'GET' && /^\/api\/local\/world-load\/source\/[a-f\d-]{36}$/i.test(path)) return unavailable('Game restore rehearsal');
  return null;
}
