/** Synthetic UI transport. Never dispatches HTTP, launches processes, or reads local app data. */
import { readFileSync } from 'node:fs';
import * as worldReview from './ui-polish-world-review.mjs';
import * as accessReview from './ui-polish-access-review.mjs';
import { simpleHostingPermission, simpleHostingStatus } from './simple-shared-hosting-review.mjs';

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
const reviewHelpers = Object.freeze({ reply, result, ids, hash, root, boundary, fixtureNow, clone });
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
  'setup-java', 'setup-bedrock', 'setup-factorio', 'setup-terraria', 'setup-custom',
  'host-stopping', 'host-error', 'host-java-offline', 'host-bedrock-offline', 'host-factorio-offline',
  'host-terraria-offline', 'host-custom-offline', 'host-empty-backups', 'host-backup-failure',
  'host-no-devices', 'host-removed-devices', 'host-expired-devices', 'host-pending-devices',
  'host-update-available', 'host-update-error', 'host-peers', 'host-peers-conflict',
  'host-shared-offline', 'host-live-save', 'host-live-interrupted',
  'friend-revoked', 'friend-awaiting-approval', 'friend-no-assignment', 'friend-status-only',
  'friend-starting', 'friend-stopping', 'friend-unknown', 'friend-maintenance', 'friend-peers',
  'friend-peers-conflict', 'friend-peers-follower', 'friend-shared-verifying-offline',
  'friend-shared-receipt-offline', 'friend-shared-low-space', 'friend-shared-untrusted',
  'host-chat-empty', 'host-chat-notice', 'friend-chat-empty', 'friend-chat-notice',
  'host-java-maintenance', 'host-bedrock-maintenance', 'host-valheim-maintenance',
  'host-loading', 'host-load-error', 'host-logs-loading', 'host-logs-error', 'host-backups-error',
  'host-session-recovery', 'host-data-recovery', 'host-data-warning', 'host-recovery-orphan',
  'host-failed-session', 'host-crash-recovery', 'host-custom-certified-offline', 'host-custom-certification-step',
  'host-update-downloading', 'host-update-blocked', 'host-update-failed',
  'host-factorio-addons-maintenance', 'host-factorio-config-maintenance', 'host-terraria-config-maintenance',
  ...worldReview.extraScenarioNames, ...accessReview.extraScenarioNames
]);

const stageIds = ['completion', 'snapshot', 'transfer', 'load', 'change', 'restart'];
const stageSources = ['ObservedExactRun', 'VerifiedImmutableSnapshot', 'SignedSeparateDeviceReceipt', 'OwnerReported', 'OwnerReported', 'OwnerReported'];
function liveAcceptance(state) {
  return { accepted: false, code: 'SyntheticEvidenceOnly', message: boundary,
    stages: stageIds.map((id, index) => ({ id, state: 'Unverified', detail: boundary, source: stageSources[index] })),
    tests: /live-interrupted/.test(state.scenario) ? [{ testId: ids.session, state: 'Interrupted', reviewed: false,
      artifact: null, stages: stageIds.map((id, index) => ({ id, state: 'Unverified', detail: boundary, source: stageSources[index] })) }] : [] };
}
function peerGroups(state) {
  if (!/peers/.test(state.scenario)) return [];
  const conflict = /conflict/.test(state.scenario), follower = /follower/.test(state.scenario), owner = state.mode === 'Host';
  const branchHash = 'B'.repeat(64), secondBranch = 'C'.repeat(64), selfDeviceId = owner ? '00000000-0000-0000-0000-000000000000' : ids.device;
  const peers = [ids.otherDevice, ids.third].map(deviceId => ({ deviceId, publicKey: 'synthetic-public-key', revision: 1,
    routes: [{ address: endpoint, certificateSha256: hash }], hasConflict: false, authorized: true,
    availableVersionHashes: [hash], announcedUtc: fixtureNow, contact: null }));
  const sessions = peers.slice(0, conflict ? 2 : 1).map((peer, index) => ({ deviceId: peer.deviceId, publicKey: peer.publicKey,
    hasConflict: false, authorized: true, session: { sessionId: index === 0 ? ids.session : ids.secondary,
      branchHash: index === 0 ? branchHash : secondBranch, revision: 1, state: index === 0 ? 'Hosting' : 'Preparing',
      baseVersionHash: hash, headVersionHash: hash, startedUtc: fixtureNow, endedUtc: null } }));
  return [{ groupKey: hash, name: 'Weekend shared world', profileId: ids.primary, selfDeviceId,
    sharingEnabled: false, receiveConsented: !follower, canReceive: !follower, canReshare: owner,
    canHost: !follower, warning: null, selection: { canChoose: owner, needsChoice: conflict, heads: [] },
    state: { root: { ownerPublicKey: 'synthetic-owner-key', profileId: ids.primary, groupId: ids.primary }, groupKey: hash, peers, branches: sessions.map(item => ({ deviceId: item.deviceId, publicKey: item.publicKey,
      authorized: true, hasConflict: false, branch: { branchHash: item.session.branchHash, baseVersionHash: hash,
        headVersionHash: hash, parentVersionHash: null, versionNumber: 5, authorityHash: null } })),
      sessions, defaultChoices: [], needsChoice: conflict, knownSessions: sessions.length, announcementCount: peers.length,
      replication: { knownPeers: peers.length, peersWithRoutes: peers.length, peersLastSyncSucceeded: 0,
        claimedCopyPeers: peers.length, lastSuccessfulSyncUtc: null, hasConflicts: conflict, capacityReached: false,
        state: 'Synthetic signed information; live connections unverified' } } }];
}

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
    playerCountRefreshing: false, playerCountFailureCount: 0,
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
    playerCountTrusted: r.playerCountTrusted, playerObservationSource: r.playerObservationSource,
    playerCountObservedUtc: r.playerCountObservedUtc, playerCountRefreshing: r.playerCountRefreshing ?? false,
    playerCountFailureCount: r.playerCountFailureCount ?? 0,
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
    preset === 'friend-shared-receiving-offline' ? 'receiving' :
    preset === 'friend-shared-verifying-offline' ? 'verifying' :
    preset === 'friend-shared-receipt-offline' ? 'receipt' : null;
  const firstUse = /first-use|firstuse|setup/.test(preset);
  const host = clone(contract);
  host.settings.profiles = firstUse ? [] : [profile(ids.primary, 'Valheim', 'Weekend Valheim', 2456)];
  const game = /java/.test(preset) ? 'MinecraftJava' : /bedrock/.test(preset) ? 'MinecraftBedrock' :
    /factorio/.test(preset) ? 'Factorio' : /terraria/.test(preset) ? 'Terraria' : /custom/.test(preset) ? 'Custom' : null;
  if (game && !firstUse) host.settings.profiles = [profile(ids.primary, game, `Weekend ${game}`, game === 'MinecraftJava' ? 25565 : game === 'MinecraftBedrock' ? 19132 : game === 'Factorio' ? 34197 : game === 'Terraria' ? 7777 : 27015)];
  if (/multi|compare/.test(preset)) host.settings.profiles.push(
    profile(ids.secondary, 'MinecraftJava', 'Minecraft Java', 25565), profile(ids.third, 'MinecraftBedrock', 'Bedrock world', 19132));
  host.evidence = boundary;
  Object.assign(host.settings, { maxConcurrentServers: 3, autoShutdownEnabled: /countdown/.test(preset), keepAwakeWhileHosting: false,
    remoteControlsEnabled: true, companionListeningEnabled: true, companionEndpoint: endpoint, publicGameIp: '192.0.2.10',
    publicGameIpCheckedUtc: fixtureNow, connectionRoute: { mode: 'DirectInternet', address: '192.0.2.10' } });
  host.runs = host.settings.profiles.map((p, index) => makeRun(p, index === 2 || /offline|maintenance|update-(available|downloading|blocked|failed)/.test(preset) ? 'Offline' : /starting/.test(preset) ? 'Starting' : /stopping/.test(preset) ? 'Stopping' : /host-error/.test(preset) ? 'Error' : 'Ready', /countdown/.test(preset) ? 0 : 2));
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
  const friend = { mode: 'Friend', state: unpaired ? 'Unpaired' : /expired/.test(preset) ? 'Access expired' : /revoked/.test(preset) ? 'Revoked' : /awaiting-approval/.test(preset) ? 'Awaiting approval' : /disabled/.test(preset) ? 'Disabled' : /friend-disconnected/.test(preset) ? 'Disconnected' : 'Connected',
    detail: boundary, endpoint: unpaired ? '' : endpoint, lastConnectedUtc: unpaired || /friend-disconnected/.test(preset) ? null : fixtureNow,
    remoteControlsEnabled: !/disabled/.test(preset), canStart: true, canStop: true, profiles: unpaired ? [] : [publicProfile(p, r)],
    connectionId: unpaired ? '' : ids.connection, connections: null, connectionName: unpaired ? null : 'Weekend Host',
    hostId: ids.host, hostVersion: '0.3.2', friendVersion: '0.3.2', hostProtocolVersion: 1, protocolCompatible: true,
    hostCapabilities: ['server-chat-v1', 'server-logs-v1', 'shared-worlds-v2', 'shared-world-peers-v1'], credentialExpiresUtc: null,
    certificateExpiresUtc: '2027-10-10T16:00:00Z', expiryWarning: null, routeMode: 'DirectInternet', routeAddress: '192.0.2.10',
    activity: clone(host.activity), chatProfiles: unpaired ? [] : [{ id: p.id, name: p.name, supported: true }] };
  const state = { scenario: name, now: fixtureNow, mode: /^friend|^join/.test(preset) ? 'Friend' : 'Host', host, friend,
    devices: [device(ids.device, 'Friend PC'), device(ids.otherDevice, 'Second Friend PC')],
    consented: receivedSaveState !== null || !/consent|friend-shared/.test(preset), receivedSaveState,
    sharingEnabled: /shared|sharing/.test(preset), manageSharing: /managed-sharing|friend-shared/.test(preset),
    drafts: {}, chatEntries: {}, desktopPreferences: { available: true, launchAtLogin: false, closeToTray: false, startupAvailable: true },
    notifications: { quietMode: false, allowedEvents: ['Lifecycle', 'Backup', 'Access', 'Update'], servers: [], systemState: 'AcceptsNotifications', systemAllowsNotifications: true },
    gettingStarted: { offered: false, intent: 'Choose', dismissed: false }, hostingRemedy: options.hostingRemedy ?? null,
    simpleHosting: options.simpleHosting ?? null, simpleHostingPrepared: false, simpleHostingStarted: false,
    hostingStartPending: options.hostingStartPending === true,
    hostingProgress: options.hostingProgress ?? null, hostingProgressReads: 0, hostingProgressPending: false,
    directoryRemote: options.directoryRemote ?? null,
    ownerContinuations: options.ownerContinuations === true,
    update: { state: 'Current', currentVersion: '0.3.2', latestVersion: null, message: boundary, publisherTrust: 'Development build',
      releaseNotes: '', releaseNotesUrl: null, snoozedUntilUtc: null, promptSnoozed: false, versionSkipped: false },
    requests: [] };
  if (options.removedHost === true) friend.connections = [{ ...clone(friend), connections: null },
    { ...clone(friend), connectionId: ids.otherDevice, hostId: ids.otherDevice, connections: null,
      connectionName: 'Removed Host', hostProfileName: 'Removed friend', state: 'Revoked', connectionCode: 'Revoked',
      lastConnectedUtc: null, profiles: [], chatProfiles: [], canStart: false, canStop: false }];
  if (/no-devices/.test(preset)) state.devices = [];
  if (options.permissions === 'unassigned') Object.assign(state.devices[0], { assignedProfileIds: [], serverPermissions: [] });
  if (options.permissions === 'partial') Object.assign(state.devices[0], {
    assignedProfileIds: [ids.primary, ids.secondary], canStart: true, canStop: false, canExtendTimer: true, canViewLogs: false,
    serverPermissions: [{ profileId: ids.secondary, canStart: false, canStop: true, canExtendTimer: true, canViewLogs: true }]
  });
  if (/removed-devices/.test(preset)) state.devices[1].revoked = true;
  if (/expired-devices/.test(preset)) Object.assign(state.devices[1], { accessExpired: true, accessExpiresUtc: '2026-10-09T16:00:00Z' });
  if (/pending-devices/.test(preset)) Object.assign(state.devices[1], { approvalPending: true, paired: false });
  if (/backup-failure/.test(preset)) Object.assign(host.backups[ids.primary], { lastFailureUtc: fixtureNow, lastFailure: 'Synthetic backup failure. Original world unchanged.' });
  if (/empty-backups/.test(preset)) Object.assign(host.backups[ids.primary], { completedCount: 0, retainedSizeBytes: 0, lastSuccessfulUtc: null });
  if (/no-assignment|revoked|awaiting-approval/.test(preset)) friend.profiles = [];
  if (preset === 'friend-expired') Object.assign(friend, { state: 'Access expired', connectionCode: 'AccessExpired',
    detail: 'The Host ended access for this PC at the saved time.', remoteControlsEnabled: false,
    canStart: false, canStop: false, profiles: [], chatProfiles: [] });
  if (/status-only/.test(preset)) { friend.canStart = false; friend.canStop = false;
    for (const item of friend.profiles) Object.assign(item, { canStart: false, canStop: false, canStopNow: false, canRestartNow: false, canExtendTimer: false }); }
  if (/update-available/.test(preset)) Object.assign(state.update, { state: 'Available', latestVersion: '0.3.3',
    releaseNotes: 'Synthetic update review. No download or installation is available in this UI fixture.',
    releaseNotesUrl: 'https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3' });
  if (/update-error/.test(preset)) Object.assign(state.update, { state: 'Failed', message: 'Synthetic update check failure; current app remains unchanged.' });
  if (preset === 'host-session-recovery') {
    host.runs = host.settings.profiles.map(item => makeRun(item, 'Offline'));
    host.startupRecovery = { previousSessionInterrupted: true, previousStartedUtc: '2026-10-09T16:00:00Z',
      items: [{ profileId: ids.primary, state: 'Exited', canResume: true, detail: 'Synthetic exact recorded process exited; review before manual Resume.' }],
      message: 'Synthetic interrupted app session. No game was started by reopening.', windowsRestartRegistered: false };
  }
  if (/^host-data-(recovery|warning)$/.test(preset) || preset === 'host-recovery-orphan') {
    const blocked = preset !== 'host-data-warning';
    host.recovery = { lifecycleBlocked: blocked, notices: [{ stateFile: 'settings.json', quarantinedFile: 'settings.synthetic.invalid.json',
      reason: 'Synthetic unreadable protected data. No real local file was read or moved.', detectedUtc: fixtureNow, blocksLifecycle: blocked }] };
    host.runs = host.settings.profiles.map(item => makeRun(item, 'Offline'));
    if (preset === 'host-recovery-orphan') host.runs.push({ ...makeRun(host.settings.profiles[0], 'Failed'), profileId: ids.secondary });
  }
  if (preset === 'host-failed-session') host.runs = host.settings.profiles.map(item => makeRun(item, 'Failed'));
  if (preset === 'host-crash-recovery') host.crashRecovery[ids.primary] = { profileId: ids.primary, cycleId: ids.session,
    state: 'Suspended', attempts: 3, crashDetectedUtc: fixtureNow, nextAttemptUtc: null, readinessDeadlineUtc: null,
    recoveredUtc: null, lastFailure: 'Synthetic restart attempts exhausted. Review this saved session.' };
  if (/host-custom-certif/.test(preset)) host.customCertifications[ids.primary] = { profileId: ids.primary,
    stage: preset.includes('certified') ? 'Certified' : 'ConfirmFirstChange', message: boundary,
    inProgress: !preset.includes('certified'), certified: preset.includes('certified'),
    certifiedUtc: preset.includes('certified') ? fixtureNow : null, onlinePlayers: 0, blockReason: null };
  if (/^host-update-(downloading|blocked|failed)$/.test(preset)) {
    const stage = preset.endsWith('downloading') ? 'Downloading' : preset.endsWith('blocked') ? 'Blocked' : 'Failed';
    Object.assign(state.update, { state: 'Available', latestVersion: '0.3.3', publisherTrust: 'GitHub digest only', releaseNotes: boundary,
      releaseNotesUrl: 'https://github.com/daltonstates/TogetherServer/releases/tag/v0.3.3',
      preparation: { stage, message: boundary, downloadedBytes: stage === 'Downloading' ? 1048576 : 0,
        totalBytes: stage === 'Downloading' ? 4194304 : null, blocker: stage === 'Downloading' ? null : 'Synthetic update preparation needs review. No EXE is replaced.' } });
  }
  state.worldPeers = peerGroups(state);
  if (options.password) {
    if (!friend.hostCapabilities.includes('server-game-password-v1')) friend.hostCapabilities.push('server-game-password-v1');
    for (const item of friend.profiles) if (item.kind === 'Valheim') Object.assign(item, { canViewPassword: true, gamePasswordChangedUtc: fixtureNow });
  }
  worldReview.configure(state, reviewHelpers);
  accessReview.configure(state, reviewHelpers);
  if (state.ownerContinuations) {
    const original = host.settings.profiles[0];
    original.sharedSavesEnabled = true;
    const retained = { ...clone(original), id: ids.secondary, name: 'Retained server copy', sharedSavesEnabled: false,
      separateCopySourceProfileId: original.id, separateCopyBranchHash: 'B'.repeat(64), worldDirectory: `${root}\\shared\\retained` };
    const current = { ...clone(original), id: ids.third, sharedSavesEnabled: false,
      separateCopySourceProfileId: original.id, separateCopyBranchHash: 'C'.repeat(64), worldDirectory: `${root}\\shared\\current` };
    host.settings.profiles = [current, original, retained];
    host.runs = [makeRun(current, 'Ready', 0), makeRun(original, 'Offline'), makeRun(retained, 'Offline')];
    for (const item of [retained, current]) {
      host.passwordConfigured[item.id] = true;
      host.backups[item.id] = backupStatus(item.id);
    }
    host.hostingPower.managedServerRunning = true;
    state.sharingEnabled = true;
  }
  if (state.directoryRemote) {
    const original = host.settings.profiles[0];
    const continuation = { ...clone(original), id: ids.secondary, name: 'Remote continuation' };
    state.directoryRemoteProfileId = continuation.id;
    Object.assign(friend, { connectionName: 'Remote friend’s PC', hostProfileName: 'Remote friend',
      profiles: [publicProfile(continuation, makeRun(continuation, state.directoryRemote === 'Running' ? 'Ready' : 'Offline', 0))],
      chatProfiles: [{ id: continuation.id, name: continuation.name, supported: true }] });
  }
  return state;
}

function chat(state, id, host) {
  return result({ hostId: ids.host, profileId: id, selfAuthorId: host ? '00000000-0000-0000-0000-000000000000' : ids.device,
    entries: state.chatEntries[id] ?? (/chat-empty/.test(state.scenario) ? [] : [
    { id: ids.session, hostId: ids.host, profileId: id, authorId: ids.device, author: 'Friend PC',
      sentUtc: '2026-10-10T15:55:00Z', text: 'Ready for the next session?', signature: 'synthetic-signature' } ]),
    pending: [], members: host ? state.devices.map(d => ({ deviceId: d.id, name: d.name, allowed: true })) : null,
    notice: /chat-notice/.test(state.scenario) ? { hostId: ids.host, profileId: id, revision: 1, updatedUtc: fixtureNow,
      text: 'Synthetic room notice: review the server plan before the next session.', signature: `${'A'.repeat(86)}==` } : null,
    noticeCached: false, noticeSupported: true });
}
function sharing(state) {
  return { available: true, canManage: state.manageSharing, selfDeviceId: ids.device, revision: 1,
    code: 'SyntheticFixture', message: boundary, checkedUtc: fixtureNow,
    members: state.manageSharing ? state.devices.map(d => ({ deviceId: d.id, grants: d.sharedWorldGrants[ids.primary],
      revoked: false, accessExpiresUtc: null, isSelf: d.id === ids.device })) : [] };
}

// Display-only combined workspace projection. It reads only this synthetic
// in-memory state and carries no addresses, permissions, scripts, or file paths.
function serverDirectory(state) {
  const now = Date.parse(state.now ?? fixtureNow);
  const fresh = value => typeof value === 'string' && Number.isFinite(Date.parse(value)) &&
    Date.parse(value) <= now && now - Date.parse(value) <= 10000;
  const row = (profile, run, current = true) => {
    const trusted = current && run?.state === 'Ready' && run.playerCountTrusted === true &&
      Number.isSafeInteger(run.onlinePlayers) && run.onlinePlayers >= 0 && fresh(run.playerCountObservedUtc);
    const observed = current ? run?.playerCountObservedUtc ?? null : null;
    return { id: profile.id, name: profile.name, kind: profile.gameKind ?? profile.kind, state: !current ? 'Unknown' :
      run?.state === 'Ready' && !fresh(observed) ? 'Unknown' : run?.state ?? 'Unknown',
    onlinePlayers: trusted ? run.onlinePlayers : null, maxPlayers: trusted ? run.maxPlayers ?? null : null,
    playerCountTrusted: trusted, playerObservationSource: current ? run?.playerObservationSource ?? null : null,
    playerCountObservedUtc: observed, playerCountRefreshing: current && run?.playerCountRefreshing === true,
    playerCountFailureCount: current ? run?.playerCountFailureCount ?? 0 : 0,
    separateCopySourceProfileId: profile.separateCopySourceProfileId ?? null, sourceName: null, sourceHostName: null,
    ...(state.directoryRemote ? { sharedWorldId: hash, originalProfileId: ids.primary, isSuccessor: profile.id === ids.secondary } : {}) };
  };
  const connections = state.friend.connections?.length ? state.friend.connections : [state.friend];
  const friendHosts = connections.filter(host => host.connectionId && !/first-use|firstuse|setup/.test(state.scenario))
    .slice(0, 100).map(host => ({ connectionId: host.connectionId, hostId: host.hostId ?? null,
      connectionName: host.connectionName ?? 'Friend Host', hostProfileName: host.hostProfileName ?? null,
      state: host.state, connectionCode: host.connectionCode ?? null, lastConnectedUtc: host.lastConnectedUtc ?? null,
      servers: host.state === 'Revoked' || host.connectionCode === 'Revoked' || host.state === 'Access expired' || host.connectionCode === 'AccessExpired'
        ? [] : host.profiles.slice(0, 100).map(profile => row(profile, profile,
          ['Connected', 'Disabled'].includes(host.state) && fresh(host.lastConnectedUtc))) }));
  const localServers = state.host.settings.profiles.slice(0, 100)
    .map(profile => row(profile, state.host.runs.find(run => run.profileId === profile.id)));
  for (const local of localServers) if (local.separateCopySourceProfileId) {
    const sourceHost = friendHosts.find(host => host.state !== 'Revoked' && host.connectionCode !== 'Revoked' &&
      host.servers.some(profile => profile.id === local.separateCopySourceProfileId));
    local.sourceName = sourceHost?.servers.find(profile => profile.id === local.separateCopySourceProfileId)?.name ??
      localServers.find(profile => profile.id === local.separateCopySourceProfileId && !profile.separateCopySourceProfileId)?.name ?? null;
    local.sourceHostName = sourceHost?.hostProfileName ?? sourceHost?.connectionName ?? null;
  }
  return { localServers, friendHosts };
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
  if (path === '/api/local/profile' && method === 'GET') return reply({ displayName: 'Synthetic owner' });
  if (path === '/api/local/getting-started' && (method === 'GET' || method === 'PUT')) {
    if (url.search || method === 'GET' && Object.keys(body).length) return null;
    if (method === 'PUT') {
      if (!body || typeof body !== 'object' || Array.isArray(body) ||
          Object.keys(body).sort().join(',') !== 'dismissed,intent' || typeof body.dismissed !== 'boolean' ||
          !['Choose', 'Host', 'Join'].includes(body.intent)) return null;
      state.gettingStarted = { offered: true, intent: body.intent, dismissed: body.dismissed };
    }
    return reply(state.gettingStarted);
  }
  if (path === '/api/local/required-setup' && method === 'GET') return reply({ features: [{
    id: 'profile-name', revision: 1, title: 'Your name', description: 'Share your name with paired PCs.', completed: true
  }] });
  if (path === '/api/local/servers' && method === 'GET') {
    if (url.search || Object.keys(body).length) return { status: 400,
      body: { code: 'InvalidServerDirectoryRequest', message: 'The saved server list accepts no query or request body.' } };
    return reply(serverDirectory(state));
  }
  const reviewRequest = { method, path, body, query: url.search };
  const reviewResponse = worldReview.route(state, reviewRequest, reviewHelpers) ?? accessReview.route(state, reviewRequest, reviewHelpers);
  if (reviewResponse) return reviewResponse;
  const snapshot = () => state.mode === 'Host' ? state.host : state.friend;
  if (path === '/api/local/world-peers' && method === 'GET') return reply(state.worldPeers);
  const peerMatch = /^\/api\/local\/world-peers\/([a-f0-9]{64})\/(.+)$/i.exec(path);
  if (peerMatch) {
    const group = state.worldPeers.find(item => item.groupKey === peerMatch[1]), action = peerMatch[2];
    if (action === 'live-tests' && method === 'GET') return reply([]);
    if (action === 'contact-code' && method === 'GET') return reply({ code: 'SYNTHETIC-CONTACT-ONLY-NOT-VALID' });
    if (group && method === 'PUT' && ['sharing', 'receiving'].includes(action)) {
      group[action === 'sharing' ? 'sharingEnabled' : 'receiveConsented'] = body.enabled === true;
      return reply(result());
    }
    if (method === 'POST' && ['sync', 'contact', 'default', 'connect', 'recovery-source', 'owner-recovery'].includes(action))
      return unavailable('Peer network, signed default or recovery action');
    if (action.startsWith('owner-recovery')) return unavailable('Owner recovery hosting preparation');
  }
  if (path === '/api/local/snapshot' && method === 'GET') {
    if (state.scenario === 'host-loading') return { ...reply(snapshot()), pending: true };
    if (state.scenario === 'host-load-error') return unavailable('Local snapshot');
    return reply(snapshot());
  }
  if (/^\/api\/local\/mode\/(host|friend)$/i.test(path) && method === 'POST') {
    state.mode = path.toLowerCase().endsWith('/host') ? 'Host' : 'Friend'; return reply(result());
  }
  if (path === '/api/local/instance' && method === 'GET') return reply({ kind: /update-(available|downloading|blocked|failed)/.test(state.scenario) ? 'Production' : 'Staging', displayName: 'TogetherServer SYNTHETIC',
    isStaging: !/update-(available|downloading|blocked|failed)/.test(state.scenario), freshWorldsOnly: false, startupAvailable: false, updatesAvailable: /update-(available|downloading|blocked|failed)/.test(state.scenario), localPort: 5128,
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
  if (/^\/api\/local\/friend\/connections\/[^/]+\/select$/.test(path) && method === 'POST') {
    const connectionId = path.split('/')[5];
    const saved = state.friend.connections?.length ? state.friend.connections : [state.friend];
    const selected = saved.find(host => host.connectionId === connectionId);
    if (!selected) return { status: 404, body: { ok: false, code: 'UnknownConnection', message: boundary } };
    state.friend = { ...clone(selected), connections: state.friend.connections ? clone(saved) : null };
    return reply(result());
  }
  const match = /^\/api\/local\/(profiles|friend)\/([^/]+)\/(.+)$/.exec(path);
  const chatMatch = /^\/api\/local\/friend\/connections\/[^/]+\/servers\/([^/]+)\/(chat.*)$/.exec(path);
  if (match || chatMatch) {
    // Saved-connection routes also match the legacy Friend prefix. Prefer the
    // specific room route so "connections" cannot become a profile identity.
    const host = !chatMatch && match?.[1] === 'profiles'; const id = chatMatch?.[1] ?? match[2]; const suffix = chatMatch?.[2] ?? match[3];
    const p = state.host.settings.profiles.find(p => p.id === id) ?? profile(id, 'Valheim', 'Weekend Valheim', 2456);
    if (suffix === 'logs' && method === 'GET' && state.scenario === 'host-logs-loading') return { ...reply({}), pending: true };
    if (suffix === 'logs' && method === 'GET' && state.scenario === 'host-logs-error') return unavailable('Server log read');
    if (suffix === 'backup-catalog' && method === 'GET' && state.scenario === 'host-backups-error') return unavailable('Backup catalog read');
    if (suffix === 'chat/summary' && method === 'GET') {
      const room = chat(state, id, host);
      return reply(result({ hostId: ids.host, profileId: id, messageIds: room.entries.map(entry => entry.id),
        notice: room.notice, noticeCached: room.noticeCached, noticeSupported: room.noticeSupported }));
    }
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
    if (suffix === 'backups' && method === 'GET') return reply({ backups: /empty-backups/.test(state.scenario) ? [] : [{ id: ids.backup, profileId: id, kind: p.kind, worldId: p.worldId,
      backupKind: 'Manual', createdUtc: '2026-10-10T15:00:00Z', sizeBytes: 4194304, fileCount: 2, label: 'Before the weekend', pinned: true, setupIncluded: true }], status: state.host.backups[id] ?? backupStatus(id) });
    if (suffix === 'backup-catalog' && method === 'GET') return reply(result({ profileId: id,
      backups: /empty-backups/.test(state.scenario) ? [] : [{ backupId: ids.backup, profileId: id, createdUtc: '2026-10-10T15:00:00Z', backupKind: 'Manual', sizeBytes: 4194304,
        label: 'Before the weekend', pinned: true, gameKind: p.kind, worldId: p.worldId, fileCount: 2, setupIncluded: true,
        payloadSha256: hash, setupSha256: hash, metadataAvailable: true, evidence: [] }], pinnedCount: /empty-backups/.test(state.scenario) ? 0 : 1, pinnedSizeBytes: /empty-backups/.test(state.scenario) ? 0 : 4194304,
      maximumLabelLength: 64, maximumPinnedCount: 20, maximumPinnedSizeBytes: 50 * 1024 ** 3, moreBackupsAvailable: false,
      retention: { retentionCount: 5, minimumFreeSpaceBytes: 1073741824, rollingEnabled: true },
      retainedSizeBytes: /empty-backups/.test(state.scenario) ? 0 : 4194304, availableSpaceBytes: 68719476736, evidenceAvailable: true }));
    if (suffix === 'files' && method === 'GET') return reply(result({ locations: [{ key: 'world', label: 'Managed world', path: p.worldDirectory, available: true }],
      files: /config-maintenance/.test(state.scenario) ? [{ key: p.kind === 'Factorio' ? 'factorio-settings' : 'terraria-config',
        label: p.kind === 'Factorio' ? 'Factorio settings' : 'Terraria configuration', path: `${root}\\synthetic-config.json`, available: false }] :
        [{ key: 'server-properties', label: 'Server properties', path: `${root}\\server.properties`, available: true }] }));
    if (suffix.startsWith('files/') && method === 'GET') return reply(result({ key: suffix.slice(6), content: '# Synthetic configuration\nmax-players=10\n', sha256: hash, canUndo: false }));
    if (suffix === 'game-settings' && method === 'GET') return reply({ ok: true, code: 'SyntheticFixture', message: boundary, kind: p.kind,
      sha256: p.kind.startsWith('Minecraft') ? hash : null, settings: p.kind.startsWith('Minecraft') ?
        { difficulty: 'normal', maximumPlayers: 10, gameMode: 'survival', allowListEnabled: false, forceGameMode: false } : null,
      canUndo: false, lists: p.kind === 'Valheim' ? [{ key: 'admin-list', label: 'Administrators', available: true },
        { key: 'ban-list', label: 'Banned players', available: true }, { key: 'permit-list', label: 'Permitted players', available: true }] :
        p.kind.startsWith('Minecraft') ? [{ key: 'allow-list', label: 'Allowed players', available: true }] : [] });
    if (/^game-settings\/lists\/(allow-list|admin-list|ban-list|permit-list)$/.test(suffix) && method === 'GET')
      return reply({ ok: true, code: 'SyntheticFixture', message: boundary, kind: p.kind, key: suffix.split('/').at(-1),
        label: p.kind === 'Valheim' ? 'Administrators' : 'Allowed players', sha256: hash, entries: [], canUndo: false });
    if (suffix.startsWith('game-settings/') && method === 'POST') return unavailable('Game settings or player list writes');
    if (suffix === 'addons' && method === 'GET') return reply(result({ gameVersion: 'Unknown', requiredOnFriendPc: boundary, stateToken: 'synthetic',
      items: /addons-maintenance/.test(state.scenario) ? [{ key: 'synthetic-mod', name: 'Synthetic mod', version: '1.0', requiredGameVersion: 'Unknown', enabled: true,
        compatibility: 'Owner review required', type: 'Factorio mod' }] : [], canImport: /addons-maintenance/.test(state.scenario), canUndo: /addons-maintenance/.test(state.scenario),
      warning: null, importType: /addons-maintenance/.test(state.scenario) ? 'FactorioMod' : null }));
    if (suffix === 'acceptance' && method === 'GET') return reply({ profileId: id, stale: false, updatedUtc: null, checks: [],
      evidenceBoundary: boundary, gameFilesAvailable: false, gameFilesChanged: false });
    if (suffix === 'live-save-acceptance' && method === 'GET') return reply(liveAcceptance(state));
    if (suffix.startsWith('live-save-acceptance/') && method === 'POST') return unavailable('Installed game acceptance or capture');
    if (suffix.startsWith('custom-scripts')) return unavailable('Custom script reveal or writes');
    if (suffix === 'shared-world/hosting' && host && method === 'PUT') {
      if (Object.keys(body).join(',') !== 'enabled' || typeof body.enabled !== 'boolean') return null;
      state.sharingEnabled = body.enabled;
      p.sharedSavesEnabled = body.enabled;
      return reply(result({ code: 'SharedHostingSaved', message: 'Friends can host this synthetic server.' }));
    }
    if (!host && suffix === 'shared-world/hosting/access' && method === 'GET') {
      if (state.simpleHosting) return reply(simpleHostingPermission(state, boundary));
      const allowed = !!state.hostingRemedy;
      return reply({ allowed, code: allowed ? 'HostingAllowed' : 'HostingPermissionRequired', message: boundary });
    }
    if (!host && suffix === 'shared-world/hosting/progress' && method === 'GET') {
      if (state.hostingStartPendingRequest) return { ...reply(null), pending: true };
      if (!state.hostingProgressPending) return reply(null);
      const receiving = state.hostingProgress === 'Receiving';
      state.hostingProgressReads++;
      const receivedBytes = state.hostingProgressReceivedBytes ?? 1048576;
      return reply({ phase: state.hostingProgress, state: 'Working',
        message: receiving ? 'Receiving the completed synthetic save.' : 'Checking every received synthetic file against its signed hash.',
        startedUtc: fixtureNow, updatedUtc: fixtureNow, receivedBytes: receiving ? receivedBytes : 4194304,
        totalBytes: 4194304, percent: receiving ? receivedBytes / 4194304 * 100 : null });
    }
    if (state.simpleHosting && !host && suffix === 'shared-world/hosting' && method === 'GET') {
      if (state.simpleHostingReadFailure) return unavailable('Hosting automatic checks');
      return reply(simpleHostingStatus(state, id, boundary));
    }
    if (state.simpleHosting && !host && suffix === 'shared-world/hosting/prepare' && method === 'POST') {
      if (Object.keys(body).sort().join(',') !== 'acceptSplitWarning,allowReceiving,gamePassword,requirePeerConnection,serverFile' ||
          body.allowReceiving !== true || body.acceptSplitWarning !== true || body.requirePeerConnection !== true ||
          body.serverFile !== null || body.gamePassword !== null) return null;
      if (!['prepare', 'ready'].includes(state.simpleHosting)) return reply(simpleHostingStatus(state, id, boundary));
      if (state.hostingProgress) {
        state.hostingProgressPending = true;
        return { ...reply(simpleHostingStatus(state, id, boundary)), pending: true };
      }
      state.simpleHostingPrepared = true;
      return reply(simpleHostingStatus(state, id, boundary));
    }
    if (state.simpleHosting && !host && suffix === 'shared-world/hosting/start' && method === 'POST') {
      if (!simpleHostingStatus(state, id, boundary).readyForManualStart || Object.keys(body).sort().join(',') !== 'acceptSplitWarning,branchHash,localProfileId,requestId' ||
          body.branchHash !== 'A'.repeat(64) || body.localProfileId !== id || body.requestId !== null || body.acceptSplitWarning !== true) return null;
      if (state.hostingStartPending) {
        state.hostingStartPendingRequest = true;
        return { ...reply(result({ code: 'Starting', message: 'Synthetic Start result deliberately pending.' })), pending: true };
      }
      state.simpleHostingStarted = true;
      const run = state.host.runs.find(item => item.profileId === id);
      Object.assign(run, { state: 'Starting', detail: 'Starting the synthetic local copy.', onlinePlayers: null,
        playerCountTrusted: false, playerCountObservedUtc: null, playerObservationSource: null });
      return reply(result({ code: 'Starting', message: 'Starting the synthetic local copy.' }));
    }
    if (suffix === 'shared-world/hosting' && !host && method === 'GET' && state.hostingRemedy) return reply({
      ok: false, code: state.hostingRemedy === 'missing-server' ? 'InstalledServerRequired' : 'LocalConnectionSetupRequired',
      message: state.hostingRemedy === 'missing-server' ? 'Install the matching game server, then choose its file.' : 'Allow incoming connections on this PC before preparing hosting.',
      allowed: true, consented: false, game: 'Valheim', name: 'Weekend Valheim', needsServerFile: state.hostingRemedy === 'missing-server',
      needsPassword: false, serverFile: state.hostingRemedy === 'missing-server' ? null : `${root}\\valheim_server.exe`, gameVersion: 'Synthetic', gamePort: 2456, controlPort: 5131,
      localProfileId: null, branchHash: null, readyForManualStart: false, running: false,
      remedy: state.hostingRemedy === 'connection' ? { kind: 'Connection', title: 'Let friends connect to this PC', message: 'Allow incoming connections on this PC before preparing hosting.' } : null,
      steps: ['permission', 'server', 'save', 'ports', 'connection'].map(key => ({ key,
        state: key === 'permission' || key === 'ports' || key === 'server' && state.hostingRemedy !== 'missing-server' ? 'Ready' : 'ActionRequired', message: boundary }))
    });
    if (suffix === 'shared-world/hosting' && !host && method === 'GET') return reply({
      ok: false, code: 'SharedWorldUnavailable', message: 'No shared hosting grant in this synthetic fixture.',
      allowed: false, consented: false, game: null, name: 'Shared server', needsServerFile: false,
      needsPassword: false, serverFile: null, gameVersion: null, gamePort: 2456, controlPort: 5131,
      localProfileId: null, branchHash: null, readyForManualStart: false, running: false,
      steps: ['permission', 'server', 'save', 'ports', 'connection'].map(key => ({ key,
        state: key === 'permission' ? 'ActionRequired' : 'Unknown', message: boundary }))
    });
    if (suffix === 'game-password/reveal' && method === 'POST') return reply(result({ profileId: id, password: 'SYNTHETIC-ONLY', gamePasswordChangedUtc: fixtureNow }));
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
        completedUtc: fixtureNow, receivedBytes: 1048576, totalBytes: 4194304, transferPhase: 'Receiving' } : {}),
      ...(state.consented && state.receivedSaveState === 'verifying' ? { hostVersion: 5, state: 'Verifying',
        completedUtc: fixtureNow, receivedBytes: 4194304, totalBytes: 4194304, transferPhase: 'Verifying' } : {}),
      ...(state.consented && state.receivedSaveState === 'receipt' ? { hostVersion: 5, state: 'ReceiptPending',
        completedUtc: fixtureNow, receivedBytes: 4194304, totalBytes: 4194304, transferPhase: 'Receipt' } : {}),
      ...(/low-space/.test(state.scenario) ? { capacityState: 'Low space', capacityNotice: 'Synthetic low-space condition. No partial copy is playable.' } : {}),
      ...(/untrusted/.test(state.scenario) ? { trust: 'Unverified', rosterRevision: null } : {}) });
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
  if (/^\/api\/local\/devices\/[^/]+\//.test(path) && method !== 'GET') return unavailable('Friend access changes');
  if (method === 'GET' && /^\/api\/local\/world-load\/source\/[a-f\d-]{36}$/i.test(path)) return reply({ ok: true, message: boundary, rehearsals: [] });
  return null;
}
