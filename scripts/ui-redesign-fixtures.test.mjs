import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { createRequire, registerHooks, stripTypeScriptTypes } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { createFixtureState, routeFixture, fixtureScenarios, fixtureIds, FIXED_NOW } from './ui-redesign-fixtures.mjs';
import { extraCases as worldReviewCases } from './ui-polish-world-review.mjs';
import { extraCases as accessReviewCases } from './ui-polish-access-review.mjs';
import { simpleHostingCases, simpleHostingVariants } from './simple-shared-hosting-review.mjs';

// Read-only source checks against the CURRENT UI, not this older isolated checkout.
const sourceRoot = process.env.TOGETHERSERVER_UI_SOURCE_ROOT
  ? new URL('./', pathToFileURL(resolve(process.env.TOGETHERSERVER_UI_SOURCE_ROOT, 'contracts.ts')))
  : new URL('../ui/src/', import.meta.url);
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier.startsWith('.') && context.parentURL?.startsWith(sourceRoot.href)) {
      const candidate = new URL(specifier + '.ts', context.parentURL);
      if (existsSync(candidate)) return { url: candidate.href, shortCircuit: true };
    }
    return nextResolve(specifier, context);
  },
  load(url, context, nextLoad) {
    if (url.startsWith(sourceRoot.href) && url.endsWith('.ts')) return {
      format: 'module', source: stripTypeScriptTypes(readFileSync(new URL(url), 'utf8'), { mode: 'strip', sourceUrl: fileURLToPath(url) }), shortCircuit: true
    };
    return nextLoad(url, context);
  }
});
const contracts = await import(new URL('contracts.ts', sourceRoot).href);
const { parseBackupCatalog } = await import(new URL('backupCatalogWire.ts', sourceRoot).href);
const { parseWeeklyServerSummary } = await import(new URL('weeklySummaryWire.ts', sourceRoot).href);
const { parseGameSettings } = await import(new URL('gameSettings.ts', sourceRoot).href);
const { parseChatRoomWithNotice, parsePinnedNoticeFields } = await import(new URL('pinnedNoticeContracts.ts', sourceRoot).href);
const { parseNotificationPreferences, parseDesktopUpdateDetails, parseUpdatePreparation } = await import(new URL('desktopPreferencesWire.ts', sourceRoot).href);
const { parseProtectedDraftResult } = await import(new URL('protectedUiDrafts.ts', sourceRoot).href);
const { normalizedDirectIpHttpsEndpoint } = await import(new URL('sharedWorldUx.ts', sourceRoot).href);
const { parsePlannedHandoffStatus } = await import(new URL('plannedHandoff.ts', sourceRoot).href);
const { hostPcSections } = await import(new URL('workspaceSections.ts', sourceRoot).href);
const ts = createRequire(new URL('../package.json', sourceRoot))('typescript');

// Extract actual pure decoders into memory, excluding components and all imports.
// This verifies inline TSX contracts without loading React or running a renderer.
function pureDecoders(file, names, globals = {}) {
  const source = readFileSync(new URL(file, sourceRoot), 'utf8');
  const ast = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const selected = ast.statements.filter(statement =>
    ts.isFunctionDeclaration(statement) && names.includes(statement.name?.text) ||
    ts.isVariableStatement(statement) && statement.declarationList.declarations.some(d => names.includes(d.name.getText(ast))));
  const selectedNames = selected.flatMap(statement => ts.isFunctionDeclaration(statement) ? [statement.name.text] :
    statement.declarationList.declarations.map(d => d.name.getText(ast)));
  for (const name of names) assert.ok(selectedNames.includes(name), `${file} pure decoder ${name} still exists`);
  const pureSource = selected.map(s => s.getText(ast).replace(/^export /u, '')).join('\n') + `\n;({${names.join(',')}})`;
  const js = ts.transpileModule(pureSource, { compilerOptions: { target: ts.ScriptTarget.ESNext, module: ts.ModuleKind.ESNext } }).outputText;
  return runInNewContext(js, { ...globals });
}
const chat = pureDecoders('ServerChat.tsx', ['parseManagedChatRoom', 'parseChatRoomSummary'], {
  parseChatRoomWithNotice, parsePinnedNoticeFields, ContractError: contracts.ContractError,
  messageIdPattern: /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
});
const live = pureDecoders('LiveSaveControls.tsx', ['record', 'text', 'parseAttempt', 'parseLiveSaveStatus'], {
  ContractError: contracts.ContractError, stageLabels: Object.fromEntries(['completion', 'snapshot', 'transfer', 'load', 'change', 'restart'].map(id => [id, id]))
});
const shared = pureDecoders('SharedWorldControls.tsx', ['record', 'boolean', 'numberOrNull', 'textOrNull', 'timestampOrNull',
  'shortText', 'guid', 'hash', 'captureKind', 'parseRecoveryStatus', 'parseGrants', 'parseRosterMember',
  'parseHostSharedWorldStatus', 'parseAuthorityHead', 'parseAuthorityStatus', 'parseFriendSharedWorldStatus', 'parseFriendResult', 'parseRoster', 'parseSharingView'], {
  deviceIdPattern: /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
  parseLiveSaveStatus: live.parseLiveSaveStatus, parseBasicResult: contracts.parseBasicResult
});
const files = pureDecoders('ServerFilesPanel.tsx', ['entry', 'field', 'flag', 'parseFilesView', 'parseFileContent', 'parseSetupBackups'],
  { ContractError: contracts.ContractError });
const addons = pureDecoders('ServerAddOnsPanel.tsx', ['object', 'string', 'boolean', 'parseAddOns'], { ContractError: contracts.ContractError });
const readiness = pureDecoders('SharedWorldReadinessPanel.tsx', ['parseRestoreStatus'], { normalizedDirectIpHttpsEndpoint });
const localHosting = pureDecoders('FriendLocalHosting.tsx', ['boundedText', 'parseLocalHostingStatus'], {
  stepKeys: ['permission', 'server', 'save', 'ports', 'connection'],
  guidPattern: /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
  hashPattern: /^[0-9a-f]{64}$/i, parsePlannedHandoffStatus
});
const requiredSetup = pureDecoders('RequiredFeatureSetup.tsx', ['parseRequiredSetup'], { ContractError: contracts.ContractError });
const gettingStarted = pureDecoders('useGettingStarted.ts', ['parseGettingStarted'], { ContractError: contracts.ContractError });
const successor = pureDecoders('SharedHostingIdentity.tsx', ['record', 'validHash', 'parseSharedSuccessorIdentity'], { normalizedDirectIpHttpsEndpoint });
const peers = pureDecoders('WorldPeerPanel.tsx', ['object', 'text', 'flag', 'count', 'identity', 'date', 'list',
  'parsePeer', 'parseBranch', 'parseSession', 'parseSelection', 'parseWorldPeerGroups'], {
  guidPattern: /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
  hashPattern: /^[0-9a-f]{64}$/i
});
const installation = pureDecoders('LiveSaveAcceptancePanel.tsx', ['object', 'text', 'identity', 'stages', 'parseLiveSaveAcceptance']);
const worldLoad = pureDecoders('WorldLoadRehearsal.tsx', ['object', 'text', 'count', 'parseWorldLoadView', 'parseList'], { ContractError: contracts.ContractError });
const separate = pureDecoders('SharedWorldSeparateRoutePanel.tsx', ['literalHttpsOrigin', 'branch', 'branches', 'hostStatus'], { URL });
const get = (state, path) => {
  const response = routeFixture(state, { method: 'GET', path });
  assert.equal(response?.status, 200, path);
  if (response.body?.ok === false) {
    assert.match(path, /\/(weekly-summary|requirements|compatibility)$/u, `${path} is a reviewed intentional unavailable projection`);
    assert.equal(typeof response.deliberateFailure, 'string', `${path} labels intentional unavailable state`);
  }
  else assert.equal(response.deliberateFailure, undefined, path);
  return response.body;
};

test('ordinary Friend hosting status is a decoded denial without inventing a hosting grant', () => {
  const state = createFixtureState('friend-connected');
  const path = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  const response = routeFixture(state, { method: 'GET', path });
  assert.equal(response.status, 200);
  const view = localHosting.parseLocalHostingStatus(response.body);
  assert.equal(view.allowed, false);
  assert.equal(view.consented, false);
  assert.equal(view.readyForManualStart, false);
  assert.equal(view.localProfileId, null);
  assert.equal(routeFixture(state, { method: 'POST', path }), null, 'a status fixture adds no hosting mutation');
});

test('all presets decode Host and Friend snapshots with current strict decoders', () => {
  for (const scenario of fixtureScenarios) {
    const state = createFixtureState(scenario);
    assert.doesNotThrow(() => contracts.parseSnapshot(state.host), `${scenario} host`);
    assert.doesNotThrow(() => contracts.parseFriendSnapshot(state.friend), `${scenario} friend`);
    assert.equal(contracts.parseLocalProfile(get(state, '/api/local/profile')).displayName, 'Synthetic owner');
    const setup = requiredSetup.parseRequiredSetup(get(state, '/api/local/required-setup'));
    assert.equal(setup.features.length, 1);
    assert.equal(setup.features[0].id, 'profile-name');
    assert.equal(setup.features[0].revision, 1);
    assert.equal(setup.features[0].completed, true);
    if (scenario === 'host-load-error') assert.equal(routeFixture(state, { method: 'GET', path: '/api/local/snapshot' }).status, 503);
    else assert.doesNotThrow(() => contracts.parseSnapshot(get(state, '/api/local/snapshot')), scenario);
  }
});

test('every world-group preset passes the current strict peer decoder and stays synthetic', () => {
  for (const scenario of fixtureScenarios) {
    const state = createFixtureState(scenario);
    const groups = peers.parseWorldPeerGroups(get(state, '/api/local/world-peers'));
    assert.equal(groups.length, /peers/.test(scenario) ? 1 : 0, scenario);
    for (const group of groups) {
      assert.ok(group.state.peers.every(peer => peer.routes.every(route => route.address.includes('192.0.2.10'))));
      assert.equal(routeFixture(state, { method: 'POST', path: `/api/local/world-peers/${group.groupKey}/connect`,
        body: { deviceId: fixtureIds.otherDevice, sessionId: fixtureIds.session } }).status, 503);
    }
  }
});

test('installation and transfer-progress variants decode without accepting a real game', () => {
  for (const scenario of ['host-live-save', 'host-live-interrupted']) {
    const state = createFixtureState(scenario);
    const view = installation.parseLiveSaveAcceptance(get(state, `/api/local/profiles/${fixtureIds.primary}/live-save-acceptance`));
    assert.equal(view.accepted, false); assert.equal(view.code, 'SyntheticEvidenceOnly');
    assert.equal(view.stages.length, 6); assert.ok(view.stages.every(stage => stage.state === 'Unverified'));
  }
  for (const scenario of ['friend-shared-verifying-offline', 'friend-shared-receipt-offline', 'friend-shared-low-space', 'friend-shared-untrusted']) {
    const state = createFixtureState(scenario);
    shared.parseFriendSharedWorldStatus(get(state, `/api/local/friend/${fixtureIds.primary}/shared-world`));
  }
});

test('expanded world evidence uses the actual backup, rehearsal, acceptance and owner-recovery decoders', () => {
  for (const scenario of new Set(worldReviewCases.map(testCase => testCase.scenario))) {
    const state = createFixtureState(scenario);
    const profile = `/api/local/profiles/${fixtureIds.primary}`;
    const catalog = parseBackupCatalog(get(state, `${profile}/backup-catalog`));
    assert.equal(catalog.backups.length, 2, scenario);
    const rehearsals = worldLoad.parseList(get(state, `/api/local/world-load/source/${fixtureIds.primary}`));
    assert.equal(rehearsals.rehearsals.length, 1, scenario);
    const acceptance = installation.parseLiveSaveAcceptance(get(state, `${profile}/live-save-acceptance`));
    assert.match(acceptance.message, /Synthetic UI fixture only/);
    if (scenario.startsWith('host-peers-world-owner-')) {
      const base = `/api/local/world-peers/${'A'.repeat(64)}/owner-recovery`;
      assert.equal(routeFixture(state, { method: 'POST', path: base, body: { acceptSplitWarning: true } }).body.ok, true);
      const copies = separate.branches(get(state, base), fixtureIds.primary);
      assert.equal(copies.length, 1);
      separate.hostStatus(get(state, `${base}/hosting/${copies[0].branchHash}`));
    }
  }
});

test('world default review has eligible synthetic sessions and module scenarios stay isolated', () => {
  const state = createFixtureState('host-peers-conflict');
  const [group] = peers.parseWorldPeerGroups(get(state, '/api/local/world-peers'));
  assert.equal(group.selection.canChoose, true);
  assert.ok(group.state.sessions.some(item => item.authorized && !item.hasConflict && ['Preparing', 'Hosting'].includes(item.session.state)));
  for (const file of ['ui-polish-world-review.mjs', 'ui-polish-access-review.mjs']) {
    const source = readFileSync(new URL(file, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /(?:from|import\()\s*['"](?:node:)?(?:fs|http|https|net|child_process)(?:\/|['"])/u,
      `${file} must remain a pure in-memory extension`);
  }
  const baseline = createFixtureState('host-ready');
  assert.equal(baseline.worldReview, undefined);
  assert.equal(baseline.devices[0].accessExpiresUtc, null);
});

test('core settings and every populated Host tab use valid contracts', () => {
  const state = createFixtureState('host-ready');
  const routes = [
    ['/api/local/instance', contracts.parseAppInstance], ['/api/local/companion', contracts.parseCompanionInfo],
    ['/api/local/data-recovery', contracts.parseDataRecoveryView], ['/api/local/network/ports', contracts.parsePortDiagnostics],
    ['/api/local/network/routes', contracts.parseRouteDiscovery], ['/api/local/diagnostics', contracts.parseOwnerDiagnostics],
    ['/api/local/desktop/preferences', contracts.parseDesktopPreferences], ['/api/local/desktop/notifications', parseNotificationPreferences],
    ['/api/local/update', parseDesktopUpdateDetails], ['/api/local/update/preparation', parseUpdatePreparation],
    [`/api/local/profiles/${fixtureIds.primary}/chat`, parseChatRoomWithNotice],
    [`/api/local/profiles/${fixtureIds.primary}/logs`, contracts.parseServerLogResult],
    [`/api/local/profiles/${fixtureIds.primary}/sessions?limit=20`, contracts.parseRecentServerSessions],
    [`/api/local/profiles/${fixtureIds.primary}/weekly-summary`, parseWeeklyServerSummary],
    [`/api/local/profiles/${fixtureIds.primary}/backups`, contracts.parseWorldBackupList],
    [`/api/local/profiles/${fixtureIds.primary}/backup-catalog`, parseBackupCatalog],
    [`/api/local/profiles/${fixtureIds.primary}/game-settings`, parseGameSettings],
    [`/api/local/profiles/${fixtureIds.primary}/acceptance`, contracts.parseAcceptanceView],
    ['/api/local/valheim/discover', contracts.parseDiscovery], ['/api/local/minecraft/discover', contracts.parseMinecraftDiscovery]
  ];
  for (const [path, decode] of routes) assert.doesNotThrow(() => decode(get(state, path)), path);
});

test('primary Friend tabs decode with current contracts', () => {
  const state = createFixtureState('friend-connected');
  const base = `/api/local/friend/${fixtureIds.primary}`;
  contracts.parseChatRoomView(get(state, `${base}/chat`));
  parseChatRoomWithNotice(get(state, `${base}/chat`));
  contracts.parseServerLogResult(get(state, `${base}/logs`));
  contracts.parseGameRequirements(get(state, `${base}/requirements`));
  contracts.parseGameCompatibility(get(state, `${base}/compatibility`));
  contracts.parseFriendSnapshot(routeFixture(state, { method: 'POST', path: '/api/local/friend/poll' }).body);
});

test('saved-connection Friend chat sync preserves room identity and passes the actual managed-room decoder', () => {
  const state = createFixtureState('friend-connected');
  for (const profileId of [fixtureIds.primary, fixtureIds.secondary]) {
    const base = `/api/local/friend/connections/${fixtureIds.connection}/servers/${profileId}/chat`;
    const response = routeFixture(state, { method: 'POST', path: `${base}/sync` });
    assert.equal(response.status, 200);
    assert.equal(response.deliberateFailure, undefined);
    const room = chat.parseManagedChatRoom(response.body);
    assert.equal(room.ok, true); assert.equal(room.code, 'ChatSynced');
    assert.equal(room.hostId, fixtureIds.host); assert.equal(room.profileId, profileId);
    assert.equal(room.entries[0].profileId, profileId); assert.equal(room.members, null);
    assert.equal(chat.parseManagedChatRoom(get(state, base)).profileId, profileId);
    response.body.entries[0].text = 'Changed response';
    assert.equal(chat.parseManagedChatRoom(routeFixture(state, { method: 'POST', path: `${base}/sync` }).body).entries[0].text,
      'Ready for the next session?', 'repeated sync returns isolated fixture data');
    assert.equal(routeFixture(state, { method: 'POST', path: `${base}/unreviewed-write` }).status, 503);
  }
  const legacy = routeFixture(state, { method: 'POST', path: `/api/local/friend/${fixtureIds.primary}/chat/sync` });
  assert.equal(chat.parseManagedChatRoom(legacy.body).profileId, fixtureIds.primary);
  assert.equal(routeFixture(state, { method: 'POST', path: `/api/local/profiles/${fixtureIds.primary}/chat/sync` }).status, 503);
  assert.throws(() => chat.parseManagedChatRoom({ ...legacy.body, pending: [{ id: 'draft', text: 'Hello', submitted: 'false' }] }),
    /invalid delivery state/, 'the actual strict decoder still rejects malformed queue state');
});

test('empty chat remains empty across room reads, summary polling and Friend sync', () => {
  for (const mode of ['host', 'friend']) {
    const state = createFixtureState(`${mode}-chat-empty`);
    const base = mode === 'host' ? `/api/local/profiles/${fixtureIds.primary}/chat`
      : `/api/local/friend/connections/${fixtureIds.connection}/servers/${fixtureIds.primary}/chat`;
    for (let poll = 0; poll < 2; poll++) {
      const room = chat.parseManagedChatRoom(get(state, base));
      assert.equal(room.entries.length, 0); assert.equal(room.pending.length, 0);
      assert.equal(chat.parseChatRoomSummary(get(state, `${base}/summary`)).messageIds.length, 0);
      if (mode === 'friend') {
        const synced = chat.parseManagedChatRoom(routeFixture(state, { method: 'POST', path: `${base}/sync` }).body);
        assert.equal(synced.entries.length, 0); assert.equal(synced.pending.length, 0);
      }
    }
  }
  const populated = createFixtureState('friend-connected');
  const base = `/api/local/friend/connections/${fixtureIds.connection}/servers/${fixtureIds.primary}/chat`;
  const room = chat.parseManagedChatRoom(get(populated, base));
  const summary = chat.parseChatRoomSummary(get(populated, `${base}/summary`));
  assert.deepEqual(Array.from(summary.messageIds), Array.from(room.entries, entry => entry.id));
});

test('expired access matches the backend denial snapshot while retaining its saved Host', () => {
  const state = createFixtureState('friend-expired');
  const snapshot = contracts.parseFriendSnapshot(get(state, '/api/local/snapshot'));
  assert.equal(snapshot.state, 'Access expired'); assert.equal(snapshot.connectionCode, 'AccessExpired');
  assert.equal(snapshot.profiles.length, 0); assert.equal(snapshot.remoteControlsEnabled, false);
  assert.equal(snapshot.canStart, false); assert.equal(snapshot.canStop, false);
  assert.equal(snapshot.connectionId, fixtureIds.connection); assert.equal(snapshot.endpoint, 'https://192.0.2.10:5131');
  const poll = contracts.parseFriendSnapshot(routeFixture(state, { method: 'POST', path: '/api/local/friend/poll' }).body);
  assert.equal(poll.connectionCode, 'AccessExpired'); assert.equal(poll.profiles.length, 0);
});

test('Files and add-ons decode actual current inline contracts', () => {
  const state = createFixtureState('host-ready');
  const base = `/api/local/profiles/${fixtureIds.primary}`;
  files.parseFilesView(get(state, `${base}/files`));
  files.parseFileContent(get(state, `${base}/files/server-properties`));
  files.parseSetupBackups(get(state, `${base}/backups`));
  addons.parseAddOns(get(state, `${base}/addons`));
});

test('state changes are isolated and responses cannot mutate their source', () => {
  const first = createFixtureState('host-ready'), second = createFixtureState('host-ready');
  const received = get(first, '/api/local/snapshot'); received.settings.profiles[0].name = 'Changed';
  assert.equal(first.host.settings.profiles[0].name, 'Weekend Valheim');
  routeFixture(first, { method: 'POST', path: '/api/local/mode/friend' });
  assert.equal(get(first, '/api/local/snapshot').mode, 'Friend'); assert.equal(second.mode, 'Host');
  routeFixture(first, { method: 'PUT', path: '/api/local/desktop/preferences', body: { closeToTray: true } });
  assert.equal(first.desktopPreferences.closeToTray, true); assert.equal(second.desktopPreferences.closeToTray, false);
  assert.equal(FIXED_NOW, Date.parse('2026-10-10T16:00:00Z'));
  for (const scenario of ['host-first-use', 'setup-valheim', 'setup-java', 'setup-bedrock', 'setup-factorio', 'setup-terraria', 'setup-custom']) {
    assert.equal(get(createFixtureState(scenario), '/api/local/instance').freshWorldsOnly, false,
      `${scenario} retains all six synthetic game setup choices`);
  }
});

test('consent and sharing mutate only synthetic state', () => {
  const state = createFixtureState('friend-shared');
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world`;
  assert.equal(get(state, base).consented, false);
  routeFixture(state, { method: 'PUT', path: `${base}/consent`, body: { enabled: true } });
  assert.equal(get(state, base).consented, true);
  let response = routeFixture(state, { method: 'POST', path: `${base}/sharing/check` });
  assert.equal(response.body.canManage, true); assert.equal(response.body.members.length, 2);
  routeFixture(state, { method: 'PUT', path: `${base}/sharing`, body: { deviceId: fixtureIds.otherDevice, eligibleHost: true } });
  response = routeFixture(state, { method: 'POST', path: `${base}/sharing/check` });
  assert.equal(response.body.members[1].grants.eligibleHost, true);
});

test('Host and Friend shared-save primary views pass actual inline decoders', () => {
  const host = createFixtureState('host-shared'), friend = createFixtureState('friend-shared');
  const hostBase = `/api/local/profiles/${fixtureIds.primary}/shared-world`;
  shared.parseHostSharedWorldStatus(get(host, hostBase));
  shared.parseRoster(get(host, `${hostBase}/governance`));
  parsePlannedHandoffStatus(get(host, `${hostBase}/handoff`));
  const friendBase = `/api/local/friend/${fixtureIds.primary}/shared-world`;
  shared.parseFriendSharedWorldStatus(get(friend, friendBase));
  shared.parseRecoveryStatus(get(friend, `${friendBase}/recovery`));
  shared.parseSharingView(routeFixture(friend, { method: 'POST', path: `${friendBase}/sharing/check` }).body);
  const consent = routeFixture(friend, { method: 'PUT', path: `${friendBase}/consent`, body: { enabled: true } });
  const decodedConsent = shared.parseFriendResult(consent.body);
  assert.equal(decodedConsent.ok, true); assert.equal(decodedConsent.status.consented, true);
});

test('ordinary Friend fixtures have no shared-world lineage while shared scenarios retain their signed roster', () => {
  const path = `/api/local/friend/${fixtureIds.primary}/shared-world`;
  for (const scenario of ['friend-connected', 'friend-offline', 'friend-disabled']) {
    const decoded = shared.parseFriendSharedWorldStatus(get(createFixtureState(scenario), path));
    assert.equal(decoded.rosterRevision, null, scenario);
    assert.equal(decoded.trust, 'Unverified', scenario);
    assert.equal(decoded.hostVersion, null, scenario);
    assert.equal(decoded.thisPcVersion, null, scenario);
  }
  const sharedStatus = shared.parseFriendSharedWorldStatus(get(createFixtureState('friend-shared'), path));
  assert.equal(sharedStatus.rosterRevision, 1);
  assert.equal(sharedStatus.trust, 'VerifiedSignedRoster');
});

test('recipient request and accepted fixtures preserve missing consent/setup and never manufacture Start readiness', () => {
  for (const scenario of ['friend-world-hosting-request', 'friend-world-hosting-accepted']) {
    const state = createFixtureState(scenario);
    const view = localHosting.parseLocalHostingStatus(get(state,
      `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`));
    assert.equal(view.allowed, true);
    assert.equal(view.consented, false);
    assert.equal(view.readyForManualStart, false);
    assert.equal(view.localProfileId, null);
    assert.equal(view.branchHash, null);
    assert.equal(view.handoffRequest.recipientState, scenario.endsWith('-request') ? 'Requested' : 'Accepted');
    assert.equal(view.handoffRequest.canComplete, false);
  }
});

test('verified-copy and Receiving landings decode without creating signed Host or Start authority', () => {
  const path = `/api/local/friend/${fixtureIds.primary}/shared-world`;
  for (const scenario of ['friend-shared-verified-offline', 'friend-shared-receiving-offline']) {
    const state = createFixtureState(scenario);
    const status = shared.parseFriendSharedWorldStatus(get(state, path));
    assert.equal(status.consented, true);
    assert.equal(status.hostVersion, 5);
    assert.equal(status.rosterRevision, 1);
    assert.equal(status.trust, 'VerifiedSignedRoster');
    assert.equal(state.friend.state, 'Connected');
    assert.equal(state.friend.profiles[0].state, 'Offline');
    const recovery = shared.parseRecoveryStatus(get(state, `${path}/recovery`));
    assert.equal(recovery.state, 'NoOffer');
    for (const key of ['authorityHeadHash', 'candidateAddress', 'candidateDeviceId', 'proposalHash'])
      assert.equal(recovery[key], null, `${scenario} does not invent ${key}`);
    const restore = get(state, `${path}/handoff/restore`);
    const decodedRestore = readiness.parseRestoreStatus(restore);
    assert.equal(decodedRestore.staged, false);
    assert.equal(decodedRestore.restored, false);
    assert.equal(decodedRestore.readyForManualStart, false);
    assert.equal(successor.parseSharedSuccessorIdentity(restore), null);
    if (scenario.includes('verified')) {
      assert.equal(status.thisPcVersion, 5);
      assert.equal(status.state, 'Ready');
      assert.equal(status.transferPhase, null);
      assert.equal(status.receiptConfirmed, true);
      assert.equal(status.receivedBytes, status.totalBytes);
      assert.ok(status.receivedUtc);
    } else {
      assert.equal(status.thisPcVersion, null);
      assert.equal(status.state, 'Receiving');
      assert.equal(status.transferPhase, 'Receiving');
      assert.equal(status.receivedBytes, 1048576);
      assert.equal(status.totalBytes, 4194304);
      assert.equal(status.receivedUtc, null);
      assert.equal(status.receiptConfirmed, null);
    }
    const pull = routeFixture(state, { method: 'POST', path: `${path}/pull` });
    assert.equal(pull.status, 503, 'landing capture cannot silently trigger a real transfer');
  }
});

test('layout visibility excludes closed disclosure bodies while retaining native summaries and open content', () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  let helper;
  const visit = node => {
    if (ts.isFunctionDeclaration(node) && node.name?.text === 'concealedByClosedDetails') helper = node.getText(ast);
    ts.forEachChild(node, visit);
  };
  visit(ast); assert.ok(helper, 'actual browser layout helper exists');
  const concealed = runInNewContext(`${helper}\n;concealedByClosedDetails`);
  const node = (tagName, parentElement = null, open = false) => {
    const el = { tagName, parentElement, open, children: [], contains(other) {
      for (let current = other; current; current = current.parentElement) if (current === this) return true;
      return false;
    } };
    parentElement?.children.push(el); return el;
  };
  const outer = node('DETAILS'), summary = node('SUMMARY', outer), summaryIcon = node('SPAN', summary);
  const body = node('DIV', outer), clearActivity = node('BUTTON', body);
  const inner = node('DETAILS', body, true), innerSummary = node('SUMMARY', inner), fileButton = node('BUTTON', inner);
  assert.equal(concealed(outer), false); assert.equal(concealed(summary), false); assert.equal(concealed(summaryIcon), false);
  for (const el of [clearActivity, innerSummary, fileButton]) assert.equal(concealed(el), true);
  outer.open = true;
  for (const el of [clearActivity, innerSummary, fileButton]) assert.equal(concealed(el), false);
  inner.open = false;
  assert.equal(concealed(innerSummary), false); assert.equal(concealed(fileButton), true);
  const secondSummary = node('SUMMARY', inner);
  assert.equal(concealed(secondSummary), true, 'only the first direct summary is exposed by a closed native disclosure');
  assert.equal(concealed(node('BUTTON')), false, 'a genuinely visible clipped control remains eligible for checking');
});

test('scroll screenshot segments cover short, long and narrow panes on both axes without unpictured intervals', () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const names = ['scrollCaptureOffsets', 'scrollCaptureCoverage', 'scrollCaptureVisibleRange', 'scrollCaptureReaderCoverage'];
  const helpers = ast.statements.filter(node => ts.isFunctionDeclaration(node) && names.includes(node.name?.text));
  assert.equal(helpers.length, 4, 'test the actual renderer calculations without launching a browser');
  const { scrollCaptureOffsets: offsetsFor, scrollCaptureCoverage: coverageFor, scrollCaptureVisibleRange: rangeFor,
    scrollCaptureReaderCoverage: readerCoverageFor } = runInNewContext(
    helpers.map(node => node.getText(ast)).join('\n') + '\n;({scrollCaptureOffsets,scrollCaptureCoverage,scrollCaptureVisibleRange,scrollCaptureReaderCoverage})', {
      innerHeight: 900, innerWidth: 1280,
      getComputedStyle: node => ({ overflowY: node.overflowY ?? 'visible', overflowX: node.overflowX ?? 'visible' })
    });
  for (const [height, contentHeight] of [
    [672, 2020], [723, 2302], [487, 1484], // Observed gaps in the initial hosted artifact.
    [844, 100], [844, 844], [844, 855], [400, 800], [400, 1200], [400, 1201],
    [240, 10000], [40, 4000], [373.5, 9980.25], [244, 704], [160, 1030] // Narrow horizontal file readers.
  ]) {
    const offsets = offsetsFor(height, contentHeight), maximum = Math.max(0, contentHeight - height);
    assert.equal(offsets[0], 0); assert.equal(offsets.at(-1), maximum);
    assert.equal(new Set(offsets).size, offsets.length, 'every segment has a distinct offset');
    for (let index = 1; index < offsets.length; index++) {
      assert.ok(offsets[index] > offsets[index - 1]);
      assert.ok(offsets[index] - offsets[index - 1] <= height * .85 + 1e-8, 'adjacent views overlap by at least 15%');
    }
    const ranges = offsets.map(start => ({ start, end: Math.min(contentHeight, start + height) }));
    const coverage = coverageFor(contentHeight, ranges);
    assert.equal(coverage.complete, true, `${height}/${contentHeight} covers the whole scroll height`);
    assert.equal(coverage.gaps.length, 0); assert.equal(coverage.coveredThrough, contentHeight);
    const rounded = offsets.map(start => ({ start: Math.min(maximum, Math.round(start)),
      end: Math.min(contentHeight, Math.min(maximum, Math.round(start)) + height) }));
    assert.equal(coverageFor(contentHeight, rounded).complete, true, 'actual integer browser offsets preserve coverage');
  }
  const height = 723, contentHeight = 2302;
  const oldThreeViews = [0, 790, 1579].map(start => ({ start, end: Math.min(contentHeight, start + height) }));
  assert.equal(coverageFor(contentHeight, oldThreeViews).complete, false, 'the original three-point sampling is rejected');
  assert.equal(coverageFor(contentHeight, oldThreeViews).gaps.length, 2);
  const complete = offsetsFor(height, contentHeight).map(start => ({ start, end: Math.min(contentHeight, start + height) }));
  assert.equal(coverageFor(contentHeight, complete.slice().reverse()).complete, true, 'range order is irrelevant');
  assert.equal(coverageFor(contentHeight, complete.filter((_, index) => index !== 1)).complete, false, 'a missing interior screenshot fails coverage');
  assert.equal(coverageFor(contentHeight, complete.map(range => ({ ...range, end: range.end - 40 }))).complete, false, 'viewport clipping cannot pass as full content');
  assert.equal(coverageFor(100, [{ start: .5, end: 99.5 }]).complete, true, 'subpixel rounding is tolerated');
  assert.equal(coverageFor(100, [{ start: 2, end: 100 }]).complete, false, 'an unpictured top is reported');
  for (const dimensions of [[0, 100], [-1, 100], [NaN, 100], [100, Infinity], [100, -1]])
    assert.throws(() => offsetsFor(...dimensions), /finite positive/);
  assert.throws(() => offsetsFor(40, 100000), /complete coverage was not captured/, 'unexpected huge content fails explicitly instead of truncating the gallery');

  const node = ({ top = 0, height = 900, contentHeight = height, scrollTop = 0, left = 0, width = 1280,
    contentWidth = width, scrollLeft = 0, parent = null, overflowY = 'visible', overflowX = 'visible', modal = false }) => ({
    clientTop: 0, clientHeight: height, scrollHeight: contentHeight, scrollTop, parentElement: parent, overflowY, overflowX, modal,
    clientLeft: 0, clientWidth: width, scrollWidth: contentWidth, scrollLeft,
    className: 'synthetic-pane', tagName: modal ? 'DIALOG' : 'DIV', getAttribute: () => null,
    getBoundingClientRect: () => ({ top, bottom: top + height, left, right: left + width }),
    closest() { for (let current = this; current; current = current.parentElement) if (current.modal) return current; return null; }
  });
  for (const overflowY of ['auto', 'scroll', 'hidden', 'clip']) {
    const outer = node({ top: 120, height: 500, overflowY });
    const inner = node({ top: 180, height: 350, overflowY, parent: outer });
    const pane = node({ top: 100, height: 723, contentHeight: 2302, scrollTop: 790, parent: inner });
    const range = rangeFor(pane);
    assert.equal(range.start, 870); assert.equal(range.end, 1220); assert.equal(range.visibleHeight, 350,
      `${overflowY} ancestors reduce the actually pictured scroll range`);
    assert.ok(range.clippedBy.length > 1);
  }
  const visibleOverflow = node({ top: 180, height: 350 });
  assert.equal(rangeFor(node({ top: 100, height: 723, parent: visibleOverflow })).visibleHeight, 723);
  const workspace = node({ top: 400, height: 100, overflowY: 'hidden' });
  const modal = node({ top: 100, height: 700, overflowY: 'auto', parent: workspace, modal: true });
  const modalPane = rangeFor(node({ top: 120, height: 600, contentHeight: 2000, parent: modal }));
  assert.equal(modalPane.visibleHeight, 600, 'the top-layer modal escapes its underlying workspace clipping');
  assert.equal(rangeFor(modal).visibleHeight, 700);
  const viewportClipped = rangeFor(node({ top: 800, height: 200, contentHeight: 500 }));
  assert.equal(viewportClipped.visibleHeight, 100); assert.equal(viewportClipped.end, 100);
  assert.equal(viewportClipped.clippedBy[0].name, 'viewport');

  for (const overflowX of ['auto', 'scroll', 'hidden', 'clip']) {
    const parent = node({ left: 100, width: 300, overflowX });
    const range = rangeFor(node({ left: 50, width: 400, contentWidth: 1200, scrollLeft: 350, parent }), 'horizontal');
    assert.equal(range.scrollLeft, 350); assert.equal(range.start, 400); assert.equal(range.end, 700);
    assert.equal(range.visibleWidth, 300, `${overflowX} ancestors clip the actual captured columns`);
  }
  const clippedColumns = rangeFor(node({ left: 1200, width: 200, contentWidth: 800 }), 'horizontal');
  assert.equal(clippedColumns.visibleWidth, 80); assert.equal(clippedColumns.end, 80);
  const narrowWorkspace = node({ left: 600, width: 100, overflowX: 'hidden' });
  const wideModal = node({ left: 100, width: 1100, parent: narrowWorkspace, modal: true });
  assert.equal(rangeFor(node({ left: 120, width: 600, contentWidth: 1400, parent: wideModal }), 'horizontal').visibleWidth, 600,
    'top-layer modal readers also escape underlying horizontal clipping');

  const columns = offsetsFor(300, 800).map(start => ({ start, end: start + 300 }));
  const bands = [{ top: 0, bottom: 220, coveredRanges: columns }, { top: 180, bottom: 400, coveredRanges: columns }];
  const completeReader = readerCoverageFor(800, 400, bands);
  assert.equal(completeReader.complete, true); assert.equal(completeReader.vertical.coveredThrough, 400);
  assert.equal(readerCoverageFor(800, 400, bands.slice(0, 1)).complete, false, 'all columns in only the top rows are incomplete');
  assert.equal(readerCoverageFor(800, 400, [bands[0], { ...bands[1], coveredRanges: columns.slice(0, -1) }]).complete, false,
    'bottom rows missing exact-text columns cannot pass full reader coverage');
  assert.equal(readerCoverageFor(800, 400, [
    { top: 0, bottom: 220, coveredRanges: [{ start: 0, end: 400 }] },
    { top: 180, bottom: 400, coveredRanges: [{ start: 400, end: 800 }] }
  ]).complete, false, 'independent row and column unions cannot disguise uncaptured corners');
  assert.equal(readerCoverageFor(800, 400, []).complete, false, 'an offscreen reader without captures fails explicitly');
});

test('draft revision storage never crosses fixture states', () => {
  const state = createFixtureState();
  const identity = { purpose: 'settings', profileId: fixtureIds.primary, key: 'settings:properties' };
  const read = () => routeFixture(state, { method: 'POST', path: '/api/local/ui-drafts/read', body: identity }).body;
  parseProtectedDraftResult(read()); assert.equal(read().revision, 0);
  const saved = routeFixture(state, { method: 'PUT', path: '/api/local/ui-drafts', body: { ...identity, expectedRevision: 0, text: 'Synthetic edit' } }).body;
  parseProtectedDraftResult(saved); assert.equal(saved.revision, 1);
  const conflict = routeFixture(state, { method: 'PUT', path: '/api/local/ui-drafts', body: { ...identity, expectedRevision: 0, text: 'Stale edit' } }).body;
  assert.equal(conflict.ok, false); assert.equal(read().text, 'Synthetic edit');
});

test('unsafe actions fail deliberately and unknown or external routes are unhandled', () => {
  const state = createFixtureState();
  for (const path of [`/api/local/profiles/${fixtureIds.primary}/start`, `/api/local/friend/${fixtureIds.primary}/open-game`,
    '/api/local/minecraft/install', `/api/local/friend/${fixtureIds.primary}/shared-world/handoff/restore`]) {
    const response = routeFixture(state, { method: 'POST', path });
    assert.equal(response.status, 503); assert.match(response.deliberateFailure, /Synthetic fixture unavailable/);
  }
  assert.equal(routeFixture(state, { method: 'GET', path: 'https://192.0.2.10:5131/api/local/snapshot' }), null);
  assert.equal(routeFixture(state, { method: 'GET', path: '/api/local/no-such-route' }), null);
  assert.equal(routeFixture(state, { method: 'GET', path: '/api/companion/status' }), null);
  for (const path of ['/api/local/network/new-unknown-check', `/api/local/friend/${fixtureIds.primary}/shared-world/new-unknown-state`])
    for (const method of ['GET', 'POST']) assert.equal(routeFixture(state, { method, path }), null, `${method} ${path} remains an unhandled finding`);
  for (const path of ['/api/local/snapshot', '/api/local/desktop/preferences', '/api/local/update',
    `/api/local/profiles/${fixtureIds.primary}/requirements`, `/api/local/profiles/${fixtureIds.primary}/files`])
    assert.equal(routeFixture(state, { method: 'POST', path }), null, `unsupported POST ${path} cannot receive fake success`);
  assert.equal(state.host.runs[0].state, 'Ready');
});

test('renderer guard precedes imports and refuses desktop execution by construction', () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const guard = source.slice(0, source.indexOf('await import('));
  assert.match(guard, /throw new Error/u);
  for (const term of ['--allow-isolated-browser-tests', '--hosted-runner', 'github-actions', 'GITHUB_ACTIONS', 'RUNNER_OS'])
    assert.ok(guard.includes(term), `authorization guard contains ${term}`);
  assert.doesNotMatch(guard, /(?:^|\n)import /u, 'no static module imports bypass authorization');
  assert.ok(source.includes('routeFixture'), 'renderer routes synthetic API requests');
  assert.ok(source.includes('route.abort'), 'renderer blocks unapproved network requests');
  const pureGuard = source.slice(0, source.indexOf('// Implementation below'));
  const hosted = { GITHUB_ACTIONS: 'true', RUNNER_OS: 'Windows', RUNNER_ENVIRONMENT: 'github-hosted' };
  const flags = ['--allow-isolated-browser-tests', '--hosted-runner', 'github-actions'];
  const check = (argv, env, platform = 'win32') => runInNewContext(pureGuard, { process: { argv: ['node', 'renderer', ...argv], env, platform } });
  assert.doesNotThrow(() => check(flags, hosted), 'both flags and the hosted Windows identity pass the pure guard');
  assert.doesNotThrow(() => check(['--list'], {}, 'linux'), 'the exact inventory-only invocation needs no browser authorization');
  assert.doesNotThrow(() => check(['--list', '--simple-shared-hosting-only'], {}, 'linux'), 'the bounded hosting inventory remains read-only');
  for (const [argv, env, platform] of [
    [[], hosted, 'win32'], [flags, {}, 'win32'],
    [['--list', '--allow-isolated-browser-tests'], {}, 'win32'], [['--list', 'capture'], {}, 'win32'],
    [['--list', '--setup-polish-only'], {}, 'win32'], [['--setup-polish-only'], hosted, 'win32'],
    [['--hosted-runner', 'github-actions'], hosted, 'win32'],
    [['github-actions', '--allow-isolated-browser-tests'], hosted, 'win32'],
    [flags, { ...hosted, RUNNER_ENVIRONMENT: 'self-hosted' }, 'win32'],
    [flags, hosted, 'linux']
  ]) assert.throws(() => check(argv, env, platform), /Refusing browser execution/);
});

test('renderer uses the CommonJS Playwright export with a Chromium launcher', () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  assert.match(source, /const \{ chromium \} = requireUI\('playwright'\);/u);
  const requireUI = createRequire(new URL('../ui/package.json', import.meta.url));
  const { chromium } = requireUI('playwright');
  assert.equal(typeof chromium?.launch, 'function', 'CommonJS export exposes chromium.launch without launching a browser');
});

test('pure renderer inventory covers every required workspace and fixture scenario', () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const start = source.indexOf('const heading ='), end = source.indexOf('manifest.inventory =');
  assert.ok(start > 0 && end > start, 'literal inventory has stable boundaries');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const assignment = ast.statements.find(node => ts.isExpressionStatement(node) && node.expression.left?.getText(ast) === 'manifest.inventory');
  assert.ok(assignment, 'test the actual emitted inventory mapping, including object spread order');
  const { plans, inventory } = runInNewContext(source.slice(start, end) + assignment.getText(ast) + '\n;({plans,inventory:manifest.inventory})',
    { manifest: {}, worldReviewCases, accessReviewCases, args: [] });
  const focused = runInNewContext(source.slice(start, end) + assignment.getText(ast) + '\n;({plans,inventory:manifest.inventory})',
    { manifest: {}, worldReviewCases, accessReviewCases, args: ['--move-hosting-only'] });
  assert.equal(focused.plans.length, 35, 'seven move/sharing surfaces each cover five viewport/text variants');
  assert.equal(focused.inventory.length, focused.plans.length);
  assert.ok(focused.plans.every(plan => plan.testCase.id.startsWith('move-hosting-')));
  const simple = runInNewContext(source.slice(start, end) + assignment.getText(ast) + '\n;({plans,inventory:manifest.inventory})',
    { manifest: {}, worldReviewCases, accessReviewCases, simpleHostingCases, simpleHostingVariants, args: ['--simple-shared-hosting-only'] });
  assert.equal(simple.plans.length, 60, 'twenty shared-hosting surfaces cover desktop, narrow and enlarged narrow text');
  assert.equal(simple.inventory.length, simple.plans.length);
  assert.ok(simple.plans.every(plan => plan.testCase.id.startsWith('simple-hosting-')));
  for (const item of hostPcSections) {
    const plan = plans.find(plan => plan.testCase.id === `host-pc-${item.id}`);
    assert.equal(plan.testCase.section[2], item.label, `Host PC ${item.id} uses the actual sidebar action label`);
  }
  const sidebars = runInNewContext(source.slice(start, end) + assignment.getText(ast) + '\n;({plans,inventory:manifest.inventory})',
    { manifest: {}, worldReviewCases, accessReviewCases, args: ['--sidebar-only'] });
  for (const id of ['sidebar-host-long-names', 'sidebar-join-long-names', 'sidebar-settings']) {
    const variants = sidebars.plans.filter(plan => plan.testCase.id === id);
    assert.equal(variants.length, 8, `${id} covers desktop, wide, tablet, narrow, short, compact density and 150 percent text`);
    assert.ok(variants.some(plan => plan.variant.density === 'compact'), `${id} exercises compact pane spacing`);
    assert.ok(variants.every(plan => plan.testCase.sidebarReview), `${id} performs real resize checks on the hosted runner`);
  }
  const setupPolish = runInNewContext(source.slice(start, end) + assignment.getText(ast) + '\n;({plans,inventory:manifest.inventory,coverage:manifest.coverage})',
    { manifest: {}, worldReviewCases, accessReviewCases, args: ['--setup-polish-only'] });
  assert.equal(setupPolish.coverage.scope, 'setup-polish');
  assert.equal(new Set(setupPolish.plans.map(plan => plan.testCase.id)).size, 21);
  assert.equal(setupPolish.plans.length, 63, 'twenty-one focused surfaces cover desktop, narrow 150 percent text and short desktop');
  assert.equal(new Set(setupPolish.inventory.map(entry => entry.id)).size, 63);
  assert.ok(setupPolish.plans.every(plan => fixtureScenarios.includes(plan.testCase.scenario)));
  for (const { testCase } of setupPolish.plans.filter(plan => plan.testCase.section?.[0] === 'This Host PC sections')) {
    const [, id, button] = testCase.section;
    assert.equal(button, hostPcSections.find(item => item.id === id)?.label,
      `${testCase.id} clicks the current Host PC sidebar label on desktop`);
  }
  for (const id of ['notifications-empty', 'notifications-activity', 'permissions-partial', 'permissions-unassigned',
    'settings-profile', 'settings-appearance', 'settings-shortcuts', 'settings-scroll', 'guide-welcome', 'guide-host', 'guide-join',
    'wizard-game-tip', 'wizard-server-tip']) assert.ok(setupPolish.plans.some(plan => plan.testCase.id === `setup-polish-${id}`), id);
  assert.ok(setupPolish.plans.some(plan => plan.variant.reducedMotion === 'no-preference'));
  assert.ok(setupPolish.plans.some(plan => plan.variant.scale === 150 && plan.variant.reducedMotion === 'reduce'));
  const captures = new Set(plans.map(plan => plan.testCase.id));
  for (const expected of [
    ...['overview', 'chat', 'players', 'logs', 'sessions', 'backups', 'files', 'setup'].map(id => `host-${id}`),
    ...['access', 'stop', 'network', 'diagnostics', 'advanced'].map(id => `host-pc-${id}`),
    'host-first-use', 'host-compare-all',
    ...['play', 'chat', 'logs', 'shared-saves', 'network', 'connection', 'pairing'].map(id => `join-${id}`),
    'join-shared-verified-copy', 'join-shared-receiving', 'join-play-instructions-expanded', 'join-play-tools-expanded',
    'join-first-pairing', 'join-permitted-controls', 'join-permitted-start', 'attention-issues', 'attention-history',
    ...['appearance', 'notifications', 'windows', 'updates', 'shortcuts'].map(id => `settings-${id}`),
    'dialog-server-tools', 'dialog-managed-sharing-host', 'dialog-managed-sharing-friend', 'join-consent', 'dialog-save-consent',
    ...['valheim', 'minecraft-java', 'minecraft-bedrock', 'factorio', 'terraria', 'custom-game'].map(id => `dialog-setup-${id}`),
    'tooltip-hover', 'tooltip-keyboard'
  ]) assert.ok(captures.has(expected), `capture inventory retains ${expected}`);
  const unique = new Set(plans.map(plan => `${plan.testCase.id}--${plan.variant.id}`));
  assert.equal(unique.size, plans.length, 'capture keys are unique');
  assert.equal(inventory.length, plans.length);
  assert.equal(new Set(inventory.map(entry => entry.id)).size, plans.length, 'variant spreads cannot replace full capture IDs');
  for (let index = 0; index < plans.length; index++) {
    assert.equal(inventory[index].id, `${plans[index].testCase.id}--${plans[index].variant.id}`);
    assert.equal(inventory[index].theme, plans[index].variant.theme);
    assert.equal(inventory[index].viewport.width, plans[index].variant.viewport.width);
  }
  const accounting = source.slice(source.indexOf('  const attempted = new Set(manifest.cases.map'), source.indexOf('  manifest.status = manifest.fatalErrors.length'));
  assert.ok(accounting.includes('manifest.skippedCases'), 'exercise the actual final manifest accounting');
  const account = entries => {
    const manifest = { inventory, cases: entries.map(entry => ({ id: entry.id, status: 'captured', screenshots: [], layoutFindings: [], deliberateFailures: [] })) };
    runInNewContext(accounting, { manifest, plans });
    return manifest;
  };
  const completeRun = account(inventory);
  assert.equal(completeRun.skippedCases.length, 0); assert.equal(completeRun.summary.skipped, 0,
    'a complete capture run has no skipped records rather than one false skip per variant');
  const partialRun = account(inventory.filter((_, index) => index !== 7));
  assert.equal(partialRun.skippedCases.length, 1); assert.equal(partialRun.skippedCases[0].id, inventory[7].id);
  assert.equal(partialRun.summary.skipped, 1); assert.equal(partialRun.skippedCases[0].status, 'skipped');
  const duplicateAttempt = account([...inventory.filter((_, index) => index !== 7), inventory[0]]);
  assert.equal(duplicateAttempt.summary.skipped, 1, 'a duplicate attempted ID cannot disguise a missing planned capture');
  for (const plan of plans) {
    assert.ok(fixtureScenarios.includes(plan.testCase.scenario), `known fixture ${plan.testCase.scenario}`);
    assert.ok(plan.testCase.screen, `${plan.testCase.id} asserts an actual screen`);
  }
  const history = plans.find(plan => plan.testCase.id === 'attention-history').testCase;
  assert.equal(history.action, 'history'); assert.equal(history.screen.role, 'region'); assert.equal(history.screen.name, 'Activity history');
  for (const id of ['join-shared-saves', 'join-consent']) {
    const screen = plans.find(plan => plan.testCase.id === id).testCase.screen;
    assert.equal(screen.role, 'region');
    assert.equal(screen.name, 'Next shared-save action', `${id} targets the actual task card after Shared copy polish`);
  }
  for (const [id, summary] of [['join-play-instructions-expanded', 'Join instructions'], ['join-play-tools-expanded', 'Tools & help']]) {
    const testCase = plans.find(plan => plan.testCase.id === id).testCase;
    assert.equal(testCase.screen.css, '.friend-play-flow details[open]');
    assert.ok(testCase.screen.text.length > 50, `${id} asserts actual guidance rather than only its disclosure title`);
    assert.equal(testCase.steps[0].summary, summary); assert.equal(testCase.steps[0].within, '.friend-play-flow');
    assert.equal(testCase.steps.length, id === 'join-play-instructions-expanded' ? 1 : 2,
      `${id} opens its content without a redundant instruction disclosure`);
    assert.ok(plans.some(plan => plan.testCase.id === id && plan.variant.scale === 150 && plan.variant.viewport.width === 390),
      `${id} includes narrow 150% text coverage`);
  }
  for (const id of captures) {
    const variants = plans.filter(plan => plan.testCase.id === id).map(plan => plan.variant);
    assert.ok(variants.some(v => v.theme === 'dark' && v.viewport.width >= 1000), `${id} dark desktop`);
    assert.ok(variants.some(v => v.theme === 'light' && v.viewport.width <= 390), `${id} light narrow`);
  }
  assert.ok(plans.some(plan => plan.variant.scale === 150), 'large text coverage is retained');
  for (const id of ['tooltip-hover', 'tooltip-keyboard'])
    assert.ok(plans.some(plan => plan.testCase.id === id && plan.variant.scale === 150 && plan.variant.viewport.width === 390),
      `${id} checks anchored tails at narrow 150% text`);
  for (const [id, action, role, name] of [
    ['join-shared-verified-copy', 'shared-verified-copy', 'heading', 'Prepare hosting on this PC'],
    ['join-shared-receiving', 'shared-receiving', 'region', 'Save transfer progress']
  ]) {
    const screen = plans.find(plan => plan.testCase.id === id).testCase;
    assert.equal(screen.action, action);
    assert.equal(screen.screen.role, role);
    assert.equal(screen.screen.name, name);
  }
});

test('hosted workflow pins dependencies and runs source-only checks before rendering', () => {
  const source = readFileSync(new URL('../.github/workflows/ui-redesign-visual-review.yml', import.meta.url), 'utf8');
  for (const line of source.split('\n').filter(line => /uses:/u.test(line)))
    assert.match(line, /@[a-f0-9]{40}\s*$/u, 'third-party actions are commit pinned');
  assert.match(source, /branches: \['review\/ui-redesign-\*'\]/u);
  assert.match(source, /runs-on: windows-latest/u);
  for (const command of ['npm ci --ignore-scripts', 'node --test scripts/ui-redesign-fixtures.test.mjs',
    'npm run build', 'npx --no-install playwright install chromium', '--allow-isolated-browser-tests', '--hosted-runner', 'github-actions'])
    assert.ok(source.includes(command), `workflow retains ${command}`);
  assert.ok(source.includes('if: always()'), 'failure evidence is uploaded');
  assert.ok(source.indexOf('node --test') < source.indexOf('node scripts/render-ui-redesign.mjs'), 'Node checks precede renderer');
  assert.ok(source.includes("startsWith(github.ref_name, 'review/ui-redesign-setup-polish-')"));
  assert.ok(source.includes("$env:SETUP_POLISH_REVIEW -eq 'true'"));
  assert.ok(source.includes("$reviewArguments += '--setup-polish-only'"));
});

test('settings jump evidence accepts tall sections but waits for pane visibility, focus, location and settled scrolling', async () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helper = ast.statements.find(statement => ts.isFunctionDeclaration(statement) && statement.name?.text === 'settledSettingsJump');
  assert.ok(helper, 'use the renderer actual jump wait without running a browser');
  const rect = (left, top, width, height) => ({ left, top, right: left + width, bottom: top + height, width, height });
  let headingTop = 120, moving = false;
  const root = { className: 'context-content settings-content', parentElement: null, scrollTop: 1400,
    clientLeft: 0, clientTop: 0, clientWidth: 300, clientHeight: 350,
    getBoundingClientRect: () => rect(0, 100, 300, 350) };
  const heading = { getBoundingClientRect: () => rect(20, headingTop, 240, 25) };
  const target = { parentElement: root, getBoundingClientRect: () => rect(20, headingTop, 260, 800),
    querySelector: selector => selector === 'h2' ? heading : null };
  const picker = { value: 'shortcuts' };
  const document = { activeElement: target, querySelector: selector => selector.startsWith('[data-settings-section=') ? target : picker };
  const wait = runInNewContext(`${helper.getText(ast)}\n;settledSettingsJump`, {
    document, innerHeight: 600, innerWidth: 390, getComputedStyle: () => ({ overflowY: 'auto' }),
    requestAnimationFrame: callback => { if (moving) root.scrollTop += 8; callback(0); }
  });
  assert.equal((await wait('shortcuts')).sectionId, 'shortcuts', 'a visible focused heading can lead a section taller than its pane');
  root.className = 'workspace-scroll'; root.clientHeight = 250;
  assert.equal((await wait('shortcuts')).scroller, 'workspace-scroll', 'narrow layouts wait on the outer scroll owner');
  headingTop = 80;
  assert.equal(await wait('shortcuts'), false, 'a viewport-visible heading clipped above its scroll pane cannot pass');
  headingTop = 120; document.activeElement = picker;
  assert.equal(await wait('shortcuts'), false, 'a requested location alone cannot pass before target focus arrives');
  document.activeElement = target; picker.value = 'profile';
  assert.equal(await wait('shortcuts'), false, 'passive location must agree with the destination');
  picker.value = 'shortcuts'; moving = true;
  assert.equal(await wait('shortcuts'), false, 'a smooth jump still moving cannot be captured');
  moving = false;
  assert.equal((await wait('shortcuts')).scrollTop, root.scrollTop, 'the same destination passes once it settles');
});

test('settings jump coordinator awaits async false results and records only settled evidence within its deadline', async () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helper = ast.statements.find(statement => ts.isFunctionDeclaration(statement) && statement.name?.text === 'waitForSettledSettingsJump');
  assert.ok(helper);
  assert.doesNotMatch(source, /waitForFunction\(settledSettingsJump\b/u,
    'a Promise returned by the asynchronous geometry helper must not be treated as a truthy settled result');
  const predicate = () => {};
  let elapsed = 0, paintCount = 0, evaluations = 0;
  const evidence = { sectionId: 'profile', scrollTop: 0, headingTop: 128 };
  const replies = [false, false, evidence];
  const wait = runInNewContext(`${helper.getText(ast)}\n;waitForSettledSettingsJump`, {
    settledSettingsJump: predicate, performance: { now: () => elapsed }
  });
  const page = { evaluate: async (callback, sectionId) => {
    elapsed += 100;
    if (callback !== predicate) { paintCount++; return }
    assert.equal(sectionId, 'profile'); evaluations++;
    return await Promise.resolve(replies.shift());
  } };
  assert.equal(await wait(page, 'profile'), evidence);
  assert.equal(evaluations, 3, 'asynchronous false results are retried');
  assert.equal(paintCount, 2, 'retries advance a real animation frame');
  elapsed = 0; evaluations = 0;
  const neverSettled = { evaluate: async callback => {
    elapsed += 1000;
    if (callback === predicate) evaluations++;
    return await Promise.resolve(false);
  } };
  await assert.rejects(wait(neverSettled, 'profile'), /did not reach a focused, visible heading.*within 8 seconds/u);
  assert.ok(evaluations > 1 && evaluations <= 8, 'an unfulfilled destination is retried and remains a bounded failure');
});

test('optional guide fixtures stay quiet until opened and persist only the reviewed choice', () => {
  const state = createFixtureState('host-ready');
  const before = JSON.stringify({ host: state.host, friend: state.friend, devices: state.devices });
  const path = '/api/local/getting-started';
  const initial = routeFixture(state, { method: 'GET', path });
  assert.deepEqual(initial.body, { offered: false, intent: 'Choose', dismissed: false });
  assert.equal(gettingStarted.parseGettingStarted(initial.body).offered, false);
  const saved = routeFixture(state, { method: 'PUT', path, body: { intent: 'Host', dismissed: true } });
  assert.deepEqual(saved.body, { offered: true, intent: 'Host', dismissed: true });
  assert.equal(gettingStarted.parseGettingStarted(saved.body).intent, 'Host');
  assert.deepEqual(routeFixture(state, { method: 'GET', path }).body, saved.body);
  assert.equal(JSON.stringify({ host: state.host, friend: state.friend, devices: state.devices }), before);
  for (const request of [{ method: 'POST', path }, { method: 'GET', path: `${path}?target=arbitrary` },
    { method: 'GET', path, body: { intent: 'Host' } },
    { method: 'PUT', path, body: { intent: 'Host', dismissed: true, path: 'arbitrary' } },
    { method: 'PUT', path, body: { intent: 'Unknown', dismissed: true } }]) assert.equal(routeFixture(state, request), null);
});

test('focused permission fixtures retain explicit partial grants and zero assignments', () => {
  const partial = createFixtureState({ id: 'host-multi', permissions: 'partial' });
  const device = partial.devices[0];
  assert.deepEqual(device.assignedProfileIds, [fixtureIds.primary, fixtureIds.secondary]);
  assert.equal(device.canStart, true); assert.equal(device.canStop, false); assert.equal(device.canViewLogs, false);
  assert.deepEqual(device.serverPermissions, [{ profileId: fixtureIds.secondary, canStart: false, canStop: true, canExtendTimer: true, canViewLogs: true }]);
  assert.doesNotThrow(() => contracts.parseSnapshot(partial.host));
  const unassigned = createFixtureState({ id: 'host-ready', permissions: 'unassigned' });
  assert.deepEqual(unassigned.devices[0].assignedProfileIds, []);
  assert.deepEqual(unassigned.devices[0].serverPermissions, []);
  assert.deepEqual(createFixtureState('host-ready').devices[0].assignedProfileIds, [fixtureIds.primary], 'ordinary fixtures remain assigned');
});


test('simple hosting review stays bounded and uses only the current automatic hosting contract', async () => {
  const { simpleHostingCases, simpleHostingVariants, simpleHostingStatus, assertSimpleHostingUi } = await import('./simple-shared-hosting-review.mjs');
  assert.equal(simpleHostingCases.length, 20);
  assert.equal(simpleHostingVariants.length, 3);
  assert.ok(simpleHostingCases.every(item => item.id.startsWith('simple-hosting-')));
  assert.ok(simpleHostingVariants.some(item => item.viewport.width === 390));
  assert.ok(simpleHostingVariants.some(item => item.viewport.width === 390 && item.scale === 150));
  const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'ready' });
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  assert.deepEqual(Object.keys(routeFixture(state, { method: 'GET', path: `${base}/access` }).body).sort(), ['allowed', 'code', 'message']);
  assert.equal(routeFixture(state, { method: 'GET', path: `${base}/access` }).body.allowed, true);
  assert.equal(simpleHostingStatus(state, fixtureIds.primary, 'Synthetic').allowed, true);
  assert.equal(routeFixture(state, { method: 'GET', path: base }).body.readyForManualStart, true);
  assert.doesNotThrow(() => localHosting.parseLocalHostingStatus(routeFixture(state, { method: 'GET', path: base }).body));
  const start = { localProfileId: fixtureIds.primary, branchHash: 'A'.repeat(64), requestId: null, acceptSplitWarning: true };
  assert.equal(routeFixture(state, { method: 'POST', path: `${base}/start`, body: start }), null, 'Start requires this exact preparation');
  const prepare = { allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null };
  assert.equal(routeFixture(state, { method: 'POST', path: `${base}/prepare`, body: prepare }).body.readyForManualStart, true);
  assert.equal(routeFixture(state, { method: 'POST', path: `${base}/start`, body: { ...start, branchHash: 'B'.repeat(64) } }), null);
  assert.equal(routeFixture(state, { method: 'POST', path: `${base}/start`, body: start }).body.ok, true);
  assert.equal(state.simpleHostingStarted, true);
  assert.doesNotThrow(() => contracts.parseSnapshot(state.host));
  for (const stage of ['not-allowed', 'missing-server', 'connection', 'conflict']) {
    const blocked = createFixtureState({ id: 'friend-offline', simpleHosting: stage });
    const status = routeFixture(blocked, { method: 'GET', path: base }).body;
    assert.doesNotThrow(() => localHosting.parseLocalHostingStatus(status));
    assert.equal(status.readyForManualStart, false);
    assert.equal(status.allowed, stage !== 'not-allowed');
  }
  const owner = createFixtureState('host-offline');
  const ownerBase = `/api/local/profiles/${fixtureIds.primary}/shared-world/hosting`;
  assert.equal(routeFixture(owner, { method: 'PUT', path: ownerBase, body: { enabled: true } }).body.ok, true);
  assert.equal(owner.sharingEnabled, true);
  assert.equal(routeFixture(owner, { method: 'PUT', path: ownerBase, body: { enabled: true, grants: {} } }), null);
  const returnedOwner = createFixtureState({ id: 'host-offline', ownerContinuations: true });
  const [current, original, retained] = returnedOwner.host.settings.profiles;
  assert.equal(current.separateCopySourceProfileId, original.id);
  assert.equal(retained.separateCopySourceProfileId, original.id);
  assert.equal(original.sharedSavesEnabled, true);
  assert.equal(current.sharedSavesEnabled, false);
  assert.deepEqual(returnedOwner.host.runs.map(run => run.state), ['Ready', 'Offline', 'Offline']);
  assert.equal(routeFixture(returnedOwner, { method: 'GET', path: `/api/local/profiles/${original.id}/shared-world` }).body.enabled, true);
  assert.equal(simpleHostingCases.find(item => item.id === 'simple-hosting-owner-continuation').fixture.ownerContinuations, true);
  const ownerOn = simpleHostingCases.find(item => item.id === 'simple-hosting-owner-on');
  assert.deepEqual(ownerOn.steps.at(-1), { role: 'checkbox', name: 'Allow friends to host this server' },
    'the controlled switch uses an ordinary role click instead of an immediate native checked-state assertion');
  const toggle = { checked: false, disabled: true };
  let waited = false;
  const checkbox = { count: async () => 1, isChecked: async () => toggle.checked, isEnabled: async () => !toggle.disabled };
  const workspace = { getByRole: role => role === 'checkbox' ? checkbox : { count: async () => 0 } };
  const page = {
    locator: selector => { assert.equal(selector, '.shared-world-workspace'); return workspace; },
    waitForFunction: async (predicate, argument, options) => {
      assert.deepEqual(options, { timeout: 8000 });
      const matches = () => runInNewContext(`(${predicate.toString()})()`, { document: { querySelector: () => toggle } });
      assert.equal(matches(), false, 'a pending unchecked switch is not saved');
      toggle.checked = true;
      assert.equal(matches(), false, 'an optimistically checked but disabled switch is not saved');
      toggle.disabled = false;
      assert.equal(matches(), true, 'only the checked and enabled canonical state completes the wait');
      waited = true;
    }
  };
  await assertSimpleHostingUi(page, ownerOn, { assertions: [] }, { requests: [{ method: 'PUT', path: ownerBase }] });
  assert.equal(waited, true);
  const continuation = simpleHostingCases.find(item => item.id === 'simple-hosting-owner-continuation');
  const continuationPage = {
    locator: selector => selector === '.shared-world-workspace' ? workspace : selector === '.server-directory'
      ? { getByRole: () => ({ count: async () => 0 }) }
      : { count: async () => Number(selector.includes('Open Weekend Valheim on this PC')) },
    waitForFunction: async () => {}
  };
  returnedOwner.requests = [
    { method: 'GET', path: `/api/local/profiles/${current.id}/shared-world` },
    { method: 'GET', path: `/api/local/profiles/${original.id}/shared-world` },
    { method: 'GET', path: `/api/local/profiles/${original.id}/shared-world/governance` }
  ];
  await assertSimpleHostingUi(continuationPage, continuation, { assertions: [] }, returnedOwner);
  returnedOwner.requests[2].path = `/api/local/profiles/${current.id}/shared-world/governance`;
  await assert.rejects(assertSimpleHostingUi(continuationPage, continuation, { assertions: [] }, returnedOwner),
    /Owner continuation sharing must use its exact original policy/u,
    'reading active save status is valid, but editing the child policy remains rejected');
});

test('hosting review distinguishes unavailable permission, review and an access change after entry', async () => {
  const { simpleHostingPermission, simpleHostingStatus } = await import('./simple-shared-hosting-review.mjs');
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  for (const [stage, code] of [['permission-unknown', 'RosterUnavailable'], ['permission-review', 'RosterRollback'],
    ['permission-denied', 'HostingPermissionRequired']]) {
    const state = createFixtureState({ id: 'friend-offline', simpleHosting: stage });
    const access = routeFixture(state, { method: 'GET', path: `${base}/access` }).body;
    assert.deepEqual(access, simpleHostingPermission(state, access.message));
    assert.equal(access.allowed, stage === 'permission-denied', 'only the explicit permission-change case allows the entry lookup');
    const status = routeFixture(state, { method: 'GET', path: base }).body;
    assert.deepEqual(status, simpleHostingStatus(state, fixtureIds.primary, status.message));
    assert.equal(status.code, code);
    assert.equal(status.allowed, false);
    assert.equal(status.readyForManualStart, false);
    assert.equal(status.steps[0].state, stage === 'permission-denied' ? 'ActionRequired' : 'Unknown');
    assert.doesNotThrow(() => localHosting.parseLocalHostingStatus(status));
    assert.equal(state.requests.some(request => request.method !== 'GET'), false);
  }
  const ports = createFixtureState({ id: 'friend-offline', simpleHosting: 'ports' });
  const status = routeFixture(ports, { method: 'GET', path: base }).body;
  assert.equal(status.code, 'PortConflict');
  assert.equal(status.readyForManualStart, false);
  assert.equal(status.steps.find(step => step.key === 'ports').state, 'ActionRequired');
});

test('automatic hosting check failures and recovery stay explicit synthetic reads', () => {
  const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'prepare' });
  const path = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  const first = routeFixture(state, { method: 'GET', path });
  assert.equal(first.status, 200);
  state.simpleHostingReadFailure = true;
  for (let attempt = 0; attempt < 2; attempt++) {
    const failed = routeFixture(state, { method: 'GET', path });
    assert.equal(failed.status, 503);
    assert.match(failed.deliberateFailure, /Hosting automatic checks/u);
    assert.equal(failed.body.code, 'SyntheticFixtureUnavailable');
  }
  state.simpleHostingReadFailure = false;
  assert.deepEqual(routeFixture(state, { method: 'GET', path }), first);
  assert.equal(state.simpleHostingPrepared, false);
  assert.equal(state.simpleHostingStarted, false);
  assert.ok(state.requests.every(request => request.method === 'GET'));
});

test('removed-access review uses separate saved connections and rejects stacked or undersized label geometry', async () => {
  const { assertRemovedAccessBounds } = await import('./simple-shared-hosting-review.mjs');
  const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'prepare', removedHost: true });
  assert.doesNotThrow(() => contracts.parseSnapshot(state.friend));
  const directory = routeFixture(state, { method: 'GET', path: '/api/local/servers' }).body;
  assert.equal(directory.friendHosts.length, 2);
  assert.notEqual(directory.friendHosts[0].connectionId, directory.friendHosts[1].connectionId);
  assert.equal(directory.friendHosts[0].state, 'Connected');
  assert.equal(directory.friendHosts[1].state, 'Revoked');
  assert.equal(directory.friendHosts[1].connectionCode, 'Revoked');
  assert.equal(directory.friendHosts[1].hostProfileName, 'Removed friend');
  assert.deepEqual(directory.friendHosts[1].servers, []);
  const bounds = { label: { x: 0, y: 0, width: 200, height: 36 }, checkbox: { x: 0, y: 9, width: 17, height: 17 },
    text: { x: 25, y: 9, width: 150, height: 17 }, display: 'flex' };
  assert.doesNotThrow(() => assertRemovedAccessBounds(bounds));
  for (const changed of [{ display: 'grid' }, { label: { ...bounds.label, height: 20 } },
    { text: { ...bounds.text, y: 40 } }, { text: { ...bounds.text, x: 10 } }, { checkbox: null }])
    assert.throws(() => assertRemovedAccessBounds({ ...bounds, ...changed }), /inline row with a full-height label/u);
});
