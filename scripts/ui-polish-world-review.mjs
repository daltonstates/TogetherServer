/** Additional world-tool review states. Pure synthetic responses; no IO or game actions. */
const backupsSection = ['Selected server sections', 'backups', 'Backups'];
const sharingSteps = [{ nav: 'World protection sections', button: 'Sharing & hosting' }];
const liveSteps = [...sharingSteps, { summary: 'Live-save setup & acceptance' }, { summary: /Automatic recovery copies/ }];
const rehearsalSteps = [
  { summary: 'Test a copy in the game', within: '.protection-backup-library' },
  { summary: 'Test a copy in the game', within: '.protection-backup-library .connection-doctor' },
];
const ownerSteps = [
  ...sharingSteps, { summary: 'Peer sessions & connections' },
  { summary: 'Save histories and copy claims', within: '.world-peer-panel' },
  { button: 'Recover this history on the owner PC', first: true },
  { check: 'I understand another server may still be running and the saves will remain separate.' },
  { button: 'Prepare owner recovery copy' }, { button: 'Show recovery copies on this PC' },
  { button: 'Prepare this recovery copy' },
];
const hostCase = (id, scenario, steps, screen) => ({ id, scenario, workspace: 'Host', section: backupsSection, steps, screen });
const region = name => ({ role: 'region', name });

export const extraCases = [
  hostCase('world-backup-review-tools', 'host-world-review-offline', [{ button: 'Review backup', first: true }],
    { css: '.backup-library-review-tools.is-open', text: 'Protection results' }),
  hostCase('world-backup-compare-two', 'host-world-review-offline', [
    { check: 'Compare Before the weekend' }, { check: 'Compare Earlier checkpoint' },
  ], region('Backup comparison')),
  hostCase('world-backup-restore-review', 'host-world-review-offline', [
    { button: 'Review backup', first: true }, { button: 'Review Restore', first: true },
  ], region('Review Restore')),
  hostCase('world-rehearsal-results', 'host-world-rehearsal-offline', rehearsalSteps, region('World load steps')),
  hostCase('world-rehearsal-exact-copy', 'host-world-rehearsal-offline', [
    ...rehearsalSteps, { summary: 'Exact copy and setup', within: '.protection-backup-library .connection-doctor' },
  ], { css: '[aria-label="World load steps"] details[open]' }),
  hostCase('world-rehearsal-cleanup-confirmation', 'host-world-rehearsal-offline', [
    ...rehearsalSteps, { button: 'Clean up this disposable copy' },
  ], { role: 'dialog', name: 'Clean up disposable copy?' }),
  hostCase('world-rehearsal-reported-results', 'host-world-rehearsal-confirmed-offline', rehearsalSteps, region('World load steps')),
  hostCase('world-rehearsal-isolated-controls', 'host-world-rehearsal-isolated-offline', rehearsalSteps,
    { css: '[aria-label="World load steps"]', text: 'Start this isolated copy.' }),
  hostCase('world-owner-recovery-local-fields', 'host-peers-world-owner-prepared', ownerSteps, region('Host this separate copy')),
  hostCase('world-owner-recovery-restored-checks', 'host-peers-world-owner-restored', ownerSteps, region('Host this separate copy')),
  hostCase('world-owner-recovery-route-evidence', 'host-peers-world-owner-route', [
    ...ownerSteps, { summary: 'Signed recovery proof' },
  ], { css: '[aria-label="Owner recovery from peer history"]', text: 'Another approved PC observed the pinned control route.' }),
  hostCase('world-owner-recovery-manual-start', 'host-peers-world-owner-ready', ownerSteps,
    { css: '[aria-label="Host this separate copy"]', text: 'Before starting this recovery copy' }),
  hostCase('world-live-capture-confirmation', 'host-world-live-empty', [
    ...liveSteps, { button: 'Capture private test save' },
  ], { role: 'dialog', name: 'Capture a private test save?' }),
  hostCase('world-live-capture-progress', 'host-world-live-capturing', liveSteps,
    { css: '.live-save-installation-body', text: 'Waiting for a fresh game save' }),
  hostCase('world-live-artifact-transfer', 'host-world-live-artifact', liveSteps,
    { css: '.live-save-installation-body', text: 'Transfer this private copy to a different approved PC.' }),
  ...['load', 'change', 'restart'].map(stage => hostCase(`world-live-${stage}-confirmation`, `host-world-live-${stage}`, [
    ...liveSteps, { button: `I verified ${stage} in the game` },
  ], { role: 'dialog', name: `Confirm actual game ${stage}?` })),
  hostCase('world-live-six-stage-evidence', 'host-world-live-accepted', [
    ...liveSteps, { summary: 'Evidence and installation details' },
  ], { css: '.live-save-disclosure[open]', text: 'App evidence covers save, copy and transfer' }),
  hostCase('world-live-private-history', 'host-world-live-artifact', [
    ...liveSteps, { summary: 'Private test history' },
  ], { css: '.live-save-test-history', text: 'ArtifactReady' }),
  { id: 'world-private-test-received-copy', scenario: 'friend-peers-world-test-copies', workspace: 'Join',
    section: ['Join sections', 'shared-saves', 'Shared saves'], steps: [
      { summary: 'Private live-save test copies', within: '.world-peer-panel' },
      { button: 'Show private test copies on this PC' },
    ], screen: { css: '.world-peer-panel details[open]', text: 'Private payload folder' } },
];

export const extraScenarioNames = [...new Set(extraCases.map(item => item.scenario))];
const scenarios = new Set(extraScenarioNames);
const stageIds = ['completion', 'snapshot', 'transfer', 'load', 'change', 'restart'];
const sources = ['ObservedExactRun', 'VerifiedImmutableSnapshot', 'SignedSeparateDeviceReceipt', 'OwnerReported', 'OwnerReported', 'OwnerReported'];
const fixtureBranch = 'B'.repeat(64);

function liveAcceptance(state, helpers) {
  const { ids, hash, fixtureNow, boundary } = helpers;
  const mode = state.worldReview.liveMode;
  const passed = mode === 'accepted' ? 6 : mode === 'restart' ? 5 : mode === 'change' ? 4 : mode === 'load' ? 3 : mode === 'artifact' ? 2 : 0;
  const stages = stageIds.map((id, index) => ({ id, state: index < passed ? 'Passed' : 'Unverified',
    detail: `${boundary} ${index < 3 ? 'Mock app evidence' : 'Mock owner report'} for this exact private test.`,
    source: sources[index], observedUtc: index < passed ? fixtureNow : null }));
  const scope = { contractVersion: 1, profileId: ids.primary, game: 'Valheim', installationId: 'synthetic-ui-installation',
    binarySha256: 'C'.repeat(64), configurationSha256: 'D'.repeat(64), synthetic: true, binaryVersion: 'Synthetic UI review' };
  return { accepted: mode === 'accepted', code: mode === 'accepted' ? 'LiveSaveReady' : 'GameAcceptanceRequired',
    message: boundary, scope, stages, tests: mode === 'empty' ? [] : [{ testId: ids.session,
      state: mode === 'capturing' ? 'Capturing' : mode === 'accepted' ? 'Accepted' : 'ArtifactReady',
      code: 'SyntheticUiReview', requestedUtc: fixtureNow, scope, reviewed: false, stages,
      artifact: mode === 'capturing' ? null : { schema: 1, testOnly: true, testId: ids.session, scope,
        operationId: ids.third, sourceDeviceId: ids.device, completionUtc: fixtureNow, snapshotUtc: fixtureNow,
        version: { versionHash: hash }, payloadSha256: 'B'.repeat(64), payloadBytes: 4194304,
        chunkBytes: 1048576, chunkCount: 4, signature: 'synthetic-ui-artifact' } }] };
}

function catalog(state, helpers) {
  const { ids, hash, result, fixtureNow } = helpers;
  const profile = state.host.settings.profiles[0];
  const rows = [
    { backupId: ids.backup, createdUtc: '2026-10-10T15:00:00Z', backupKind: 'Manual', label: 'Before the weekend', pinned: true, sizeBytes: 4194304 },
    { backupId: ids.third, createdUtc: '2026-10-09T15:00:00Z', backupKind: 'Rolling', label: 'Earlier checkpoint', pinned: false, sizeBytes: 3145728 },
  ].map(item => ({ ...item, profileId: ids.primary, gameKind: profile.kind, worldId: profile.worldId,
    fileCount: 2, setupIncluded: true, payloadSha256: item.pinned ? hash : 'B'.repeat(64), setupSha256: hash,
    metadataAvailable: true, evidence: item.pinned ? [
      { backupId: item.backupId, kind: 'Integrity', outcome: 'Passed', checkedUtc: fixtureNow, code: 'SyntheticIntegrity' },
      { backupId: item.backupId, kind: 'Vault', outcome: 'Passed', checkedUtc: fixtureNow, code: 'SyntheticVault' },
      { backupId: item.backupId, kind: 'HashRehearsal', outcome: 'Passed', checkedUtc: fixtureNow, code: 'SyntheticHashRehearsal' },
      { backupId: item.backupId, kind: 'OwnerGameRehearsal', outcome: 'Incomplete', checkedUtc: fixtureNow, code: 'SyntheticOwnerReport' },
    ] : [] }));
  return result({ profileId: ids.primary, backups: rows, pinnedCount: 1, pinnedSizeBytes: 4194304,
    maximumLabelLength: 64, maximumPinnedCount: 20, maximumPinnedSizeBytes: 50 * 1024 ** 3,
    moreBackupsAvailable: false, retention: { retentionCount: 5, minimumFreeSpaceBytes: 1073741824, rollingEnabled: true },
    retainedSizeBytes: 7340032, availableSpaceBytes: 68719476736, evidenceAvailable: true });
}

function rehearsal(state, helpers) {
  const { ids, hash, root, fixtureNow, boundary } = helpers;
  const confirmed = state.worldReview.rehearsalConfirmed;
  return { id: ids.session, game: 'Valheim', sourceKind: 'Backup', copyIdentity: hash,
    appVersion: '0.3.2 synthetic', gameVersion: 'Synthetic installed version', state: confirmed ? 'Owner observations recorded' : 'Prepared',
    canLaunch: state.scenario.includes('rehearsal-isolated'), launchReason: boundary, ownerReportedGameVersion: confirmed ? 'Synthetic observed version' : null,
    gamePort: 35000, worldDirectory: `${root}\\disposable-rehearsal`, rehearsalProfileId: state.scenario.includes('rehearsal-isolated') ? ids.secondary : null,
    loadOutcome: confirmed ? 'OwnerConfirmed' : 'Unobserved', changeOutcome: confirmed ? 'OwnerConfirmed' : 'Unobserved',
    restartOutcome: confirmed ? 'OwnerConfirmed' : 'Unobserved', managedStarts: 0, gracefulStops: 0,
    cleaned: false, preparedUtc: fixtureNow };
}

export function configure(state, helpers) {
  if (!scenarios.has(state.scenario)) return;
  state.worldReview = { ownerRecorded: false, liveMode: state.scenario.startsWith('host-world-live-') ? state.scenario.slice('host-world-live-'.length) : null,
    rehearsalConfirmed: state.scenario.includes('rehearsal-confirmed') };
  const profile = state.host.settings.profiles[0];
  if (!profile) throw new Error('World review requires an existing synthetic Host profile.');
  profile.sharedSavesEnabled = true;
  state.sharingEnabled = true;
  if (!state.scenario.startsWith('host-world-live-')) {
    for (const run of state.host.runs) Object.assign(run, { state: 'Offline', runOperationId: null, processId: null,
      onlinePlayers: null, playerCountTrusted: false, playerCountObservedUtc: null, playerObservationSource: null });
  }
  if (state.scenario.startsWith('host-peers-world-owner-')) {
    for (const group of state.worldPeers) Object.assign(group, { canHost: true, receiveConsented: true });
  }
  const status = state.host.backups[helpers.ids.primary];
  if (status) Object.assign(status, { completedCount: 2, retainedSizeBytes: 7340032 });
}

export function route(state, request, helpers) {
  if (!scenarios.has(state.scenario)) return null;
  const { method, path, body } = request;
  const { reply, result, ids, hash, root, fixtureNow, boundary } = helpers;
  const profileBase = `/api/local/profiles/${ids.primary}`;
  if (method === 'GET' && path === `/api/local/world-peers/${hash}/live-tests`)
    return reply([{ testId: ids.session, versionHash: hash, payloadDirectory: `${root}\\private-test-copy`,
      message: `${boundary} Mock separate-PC transfer and private-copy receipt.` }]);
  if (method === 'GET' && path === `${profileBase}/backup-catalog`) return reply(catalog(state, helpers));
  if (method === 'GET' && path === `${profileBase}/backups`) return reply({ backups: catalog(state, helpers).backups.map(item => ({
    id: item.backupId, profileId: ids.primary, kind: item.gameKind, worldId: item.worldId, backupKind: item.backupKind,
    createdUtc: item.createdUtc, sizeBytes: item.sizeBytes, fileCount: item.fileCount, label: item.label, pinned: item.pinned,
    setupIncluded: item.setupIncluded })), status: state.host.backups[ids.primary] });
  if (method === 'GET' && path === `/api/local/world-load/source/${ids.primary}`)
    return reply({ ok: true, message: boundary, rehearsals: [rehearsal(state, helpers)] });
  if (method === 'POST' && path === `${profileBase}/world-load/prepare`)
    return reply(result({ rehearsal: rehearsal(state, helpers) }));
  if (method === 'GET' && path === `${profileBase}/live-save-acceptance`) return reply(liveAcceptance(state, helpers));
  if (method === 'POST' && path === `${profileBase}/live-save-acceptance/tests`) {
    state.worldReview.liveMode = 'capturing';
    return reply(result({ code: 'SyntheticCaptureRequested' }));
  }
  if (method === 'POST' && path === `${profileBase}/live-save-acceptance/tests/${ids.session}/confirm`) {
    if (body?.stage === 'load') state.worldReview.liveMode = 'change';
    else if (body?.stage === 'change') state.worldReview.liveMode = 'restart';
    else if (body?.stage === 'restart') state.worldReview.liveMode = 'accepted';
    return reply(result({ code: 'SyntheticObservationRecorded' }));
  }
  const ownerBase = `/api/local/world-peers/${hash}/owner-recovery`;
  if (path === ownerBase && method === 'POST') {
    state.worldReview.ownerRecorded = true;
    return reply(result({ code: 'SyntheticOwnerRecoveryPrepared' }));
  }
  if (path === ownerBase && method === 'GET') return reply(state.worldReview.ownerRecorded ? [{ branchHash: fixtureBranch,
    offer: { proposal: { profileId: ids.primary, candidateAddress: 'https://192.0.2.10:5131' },
      version: { number: 5, createdUtc: fixtureNow, game: 'Valheim' } }, signature: 'synthetic-owner-recovery-proof' }] : []);
  if (path === `${ownerBase}/hosting/${fixtureBranch}` && method === 'GET') {
    const ready = /owner-ready|owner-route/.test(state.scenario);
    return reply({ recorded: true, restored: !state.scenario.endsWith('prepared'), readyForManualStart: ready,
      reviewRequired: false, running: false, message: ready ? 'Local setup reviewed. Manual Start remains a separate choice.' : boundary,
      localProfileId: ready ? ids.secondary : null, branchHash: fixtureBranch,
      preparedServerRoot: `${root}\\owner-recovery`, requiredAddOns: [], controlRouteVerified: state.scenario.endsWith('route') });
  }
  return null;
}
