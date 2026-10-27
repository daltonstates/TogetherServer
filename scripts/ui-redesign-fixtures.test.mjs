import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { createRequire, registerHooks, stripTypeScriptTypes } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { createFixtureState, routeFixture, expectedFixtureRequestAbort, fixtureScenarios, fixtureIds, FIXED_NOW } from './ui-redesign-fixtures.mjs';
import { extraCases as worldReviewCases } from './ui-polish-world-review.mjs';
import { extraCases as accessReviewCases, serverAccessCaseIds, assertServerAccessUi } from './ui-polish-access-review.mjs';
import { advancePreparedHostingCheck, assertPreparedHostingRequests, assertStoppedProgressRequests, assertUnconfirmedStart, installHostingReviewClock, simpleHostingCases, simpleHostingVariants, stoppedProgressMessages } from './simple-shared-hosting-review.mjs';
import { uiReviewBuild } from './ui-review-provenance.mjs';
import { sharingDeliveryCases, applySharingDeliveryFixture, assertSharingDeliveryEvidence } from './sharing-delivery-review.mjs';

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
const localHosting = pureDecoders('FriendLocalHosting.tsx', ['remedyKinds', 'boundedText', 'parseLocalHostingStatus'], {
  stepKeys: ['permission', 'server', 'save', 'ports', 'connection'],
  guidPattern: /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
  hashPattern: /^[0-9a-f]{64}$/i, parsePlannedHandoffStatus
});
const hostingProgress = pureDecoders('LocalHostingProgress.tsx', ['timingLabels', 'parseHostingProgress']);
const requiredSetup = pureDecoders('RequiredFeatureSetup.tsx', ['parseRequiredSetup'], { ContractError: contracts.ContractError });
const gettingStarted = pureDecoders('useGettingStarted.ts', ['parseGettingStarted'], { ContractError: contracts.ContractError });
const successor = pureDecoders('SharedHostingIdentity.tsx', ['record', 'validHash', 'parseSharedSuccessorIdentity'], { normalizedDirectIpHttpsEndpoint });
const peers = pureDecoders('WorldPeerPanel.tsx', ['object', 'text', 'flag', 'count', 'identity', 'date', 'list',
  'transferCodes', 'transferCode', 'transferCount', 'optionalTransferCount', 'parseTransferProgress', 'parseBackgroundTransfers',
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

test('sharing-delivery capture states pass the current decoder with separate payload, metadata and historical receipt evidence', () => {
  assert.deepEqual(sharingDeliveryCases.map(item => item.sharingDelivery), ['sending', 'timeout-metadata', 'historical-receipt']);
  for (const item of sharingDeliveryCases) {
    const state = createFixtureState(item.scenario);
    applySharingDeliveryFixture(state, item, new Date(FIXED_NOW).toISOString());
    const [group] = peers.parseWorldPeerGroups(get(state, '/api/local/world-peers'));
    const transfer = group.backgroundTransfers[0];
    assert.equal(transfer.recipientRole, 'OriginalHost');
    assert.equal(transfer.current.publicationNumber, 6);
    assert.ok(item.steps.every(step => !step.summary), 'capture navigation opens no disclosure');
    if (item.sharingDelivery === 'sending') {
      assert.equal(transfer.current.phase, 'Sending');
      assert.equal(transfer.lastResult.result, 'ByteBudget');
      assert.equal(transfer.current.exactReceiptAccepted, false);
    } else if (item.sharingDelivery === 'timeout-metadata') {
      assert.equal(transfer.current.phase, 'Metadata');
      assert.equal(transfer.lastResult.state, 'Succeeded');
      assert.equal(transfer.lastFailure.error, 'Timeout');
      assert.equal(transfer.lastFailure.exactReceiptAccepted, false);
    } else {
      assert.equal(transfer.current.state, 'Pending');
      assert.equal(transfer.current.exactReceiptAccepted, true);
      assert.equal(transfer.current.receiptPublicationNumber, 5);
    }
  }
  const untouched = createFixtureState('host-peers'), before = structuredClone(untouched);
  applySharingDeliveryFixture(untouched, { id: 'existing-case' }, new Date(FIXED_NOW).toISOString());
  assert.deepEqual(untouched, before, 'new capture fixtures cannot alter existing plans');
  assert.throws(() => applySharingDeliveryFixture(untouched, { sharingDelivery: 'arbitrary' }, new Date(FIXED_NOW).toISOString()));
});

test('sharing-delivery acceptance requires visible closed-details status and refuses mutations or fabricated receipts', () => {
  const snapshots = {
    sending: 'Background save transfer Original Host Sending save files In progress This pass: More bytes remain for another background pass',
    'timeout-metadata': 'Background save transfer Original Host Checking world metadata This pass: Previous save transfer issue: did not respond in time',
    'historical-receipt': 'Background save transfer Original Host This pass: Save 6 Verified receipt for save 5 accepted'
  };
  for (const item of sharingDeliveryCases) {
    const good = { visible: true, permissionsOpen: false, diagnosticsOpen: false, detailsOpen: false,
      text: snapshots[item.sharingDelivery], apiRequests: [{ method: 'GET', path: '/api/local/world-peers' }] };
    assert.doesNotThrow(() => assertSharingDeliveryEvidence(item, good));
    for (const patch of [{ visible: false }, { permissionsOpen: true }, { diagnosticsOpen: true }, { detailsOpen: true },
      { apiRequests: [{ method: 'POST', path: '/api/local/world-peers/known/sync' }] }, { text: good.text + ' 100%' }])
      assert.throws(() => assertSharingDeliveryEvidence(item, { ...good, ...patch }));
    assert.throws(() => assertSharingDeliveryEvidence(item, { ...good,
      text: item.sharingDelivery === 'historical-receipt' ? good.text.replace('save 5 accepted', 'save 6 accepted')
        : good.text + ' Verified receipt for save 6 accepted' }));
  }
});

test('sharing-delivery-only inventory is exactly three states by three viewports and preserves other mode inventories', () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const assignment = ast.statements.find(node => ts.isExpressionStatement(node) && node.expression.left?.getText(ast) === 'manifest.inventory');
  const inventory = source.slice(source.indexOf('const heading ='), source.indexOf('manifest.inventory =')) + assignment.getText(ast) + '\n;({plans,inventory:manifest.inventory,coverage:manifest.coverage})';
  const focused = runInNewContext(inventory, { manifest: {}, worldReviewCases, accessReviewCases, serverAccessCaseIds,
    sharingDeliveryCases, simpleHostingVariants, args: ['--sharing-delivery-only'] });
  assert.equal(focused.coverage.scope, 'sharing-delivery');
  assert.equal(focused.coverage.plannedCaptures, 9);
  assert.equal(focused.inventory.length, 9);
  assert.equal(new Set(focused.inventory.map(item => item.id)).size, 9);
  assert.deepEqual(Array.from(new Set(focused.plans.map(item => item.testCase.id))), sharingDeliveryCases.map(item => item.id));
  assert.deepEqual(Array.from(focused.coverage.variants), simpleHostingVariants.map(item => item.id));
  assert.equal(serverAccessCaseIds.length, 12);
  assert.equal(simpleHostingCases.length, 36);
  const pureModule = readFileSync(new URL('sharing-delivery-review.mjs', import.meta.url), 'utf8');
  assert.doesNotMatch(pureModule, /(?:from|import\()\s*['"](?:node:)?(?:fs|http|https|net|child_process)(?:\/|['"])/u);
  assert.doesNotMatch(pureModule, /\.click\(|\.launch\(|createServer\(/u);
});

test('sharing-delivery hosted capture binds candidate bytes and refuses incomplete or disclosure-dependent evidence', () => {
  const workflow = readFileSync(new URL('../.github/workflows/windows-ci.yml', import.meta.url), 'utf8');
  const capture = workflow.slice(workflow.indexOf('      - name: Review visible sharing delivery'), workflow.indexOf('\n  verify:'));
  assert.ok(capture.length > 0);
  for (const required of ['--sharing-delivery-only --allow-isolated-browser-tests --hosted-runner github-actions',
    'candidateSha256', 'verificationRevision', 'uiAssets', "$manifest.coverage.scope -cne 'sharing-delivery'",
    '$manifest.summary.planned -ne 9', '$manifest.summary.attempted -ne 9', '$manifest.summary.skipped -ne 0',
    '$manifest.summary.layoutFindings -ne 0', '$manifest.inventory', '$manifest.cases',
    'visibleWithoutPermissions', 'allSharingDetailsClosed', 'transferDiagnosticsClosed', 'observationOnly',
    'name: sharing-delivery-visual-review']) assert.ok(capture.includes(required), required);
  assert.doesNotMatch(capture, /dotnet\s|npm run build|Start-Process|\.exe\s+--/u);
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

test('standalone peer fixtures decode pending transfer failures and historical receipts independently', () => {
  const state = createFixtureState('friend-peers');
  const groups = get(state, '/api/local/world-peers');
  const progress = { state: 'Working', phase: 'Metadata', operation: 'Metadata', httpStep: 'Exchange', httpStatus: 200,
    result: 'None', error: 'None', startedUtc: new Date(FIXED_NOW).toISOString(), updatedUtc: new Date(FIXED_NOW).toISOString(),
    elapsedMilliseconds: 1000, submittedBytes: 100, receivedBytes: 100, completedFiles: 1,
    requestedFileIndex: null, requestedOffset: null, exactReceiptAccepted: false, totalBytes: 100, totalFiles: 1,
    publicationNumber: 6, receiptPublicationNumber: null };
  groups[0].backgroundTransfers = [{ routeOrdinal: 1, recipientRole: 'OriginalHost', current: progress,
    lastResult: { ...progress, state: 'Succeeded', result: 'KnownReceiptAccepted', exactReceiptAccepted: true, receiptPublicationNumber: 5 },
    lastFailure: { ...progress, state: 'Failed', phase: 'Pulling', operation: 'Catalog', result: 'Failed', error: 'HttpRejected', httpStatus: 503 } }];
  const [decoded] = peers.parseWorldPeerGroups(groups);
  const [transfer] = decoded.backgroundTransfers;
  assert.equal(transfer.current.exactReceiptAccepted, false, 'metadata success and all byte counters do not certify a receipt');
  assert.equal(transfer.lastResult.receiptPublicationNumber, 5, 'the signed receipt applies to its historical publication');
  assert.equal(transfer.lastResult.publicationNumber, 6, 'the currently described save can be newer');
  assert.equal(transfer.lastFailure.operation, 'Catalog');
  assert.equal(transfer.lastFailure.httpStatus, 503, 'a later metadata result retains the payload failure');
  const baseline = createFixtureState('friend-peers');
  assert.equal(peers.parseWorldPeerGroups(get(baseline, '/api/local/world-peers'))[0].backgroundTransfers.length, 0,
    'older fixtures and app sessions without observations remain valid');
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

test('scroll capture pins the discovered pane across sibling insertions and still rejects a missing pane', async () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const names = ['scrollCapturePanes', 'captureScrollEvidence', 'scrollCaptureOffsets', 'scrollCaptureCoverage'];
  const helpers = ast.statements.filter(node => ts.isFunctionDeclaration(node) && names.includes(node.name?.text));
  assert.equal(helpers.length, names.length);
  const parent = { tagName: 'DIALOG', scrollTop: 13, scrollLeft: 7, parentElement: null,
    clientHeight: 800, scrollHeight: 800, matches: () => false, closest: () => null,
    getBoundingClientRect: () => ({ width: 300, height: 800 }) };
  let scrollTop = 0, visible = true, disposed = 0;
  const pane = { tagName: 'DIV', parentElement: parent, className: 'settings-dialog-body', clientHeight: 634, scrollHeight: 6261,
    matches: () => false, closest: () => null, getAttribute: () => null,
    getBoundingClientRect: () => ({ width: 300, height: 634 }),
    get scrollTop() { return scrollTop; }, set scrollTop(value) { scrollTop = Math.min(5627, Math.max(0, value)); } };
  parent.contains = element => element === pane;
  const catalog = [parent, pane];
  const { captureScrollEvidence, scrollCaptureOffsets: offsetsFor } = runInNewContext(
    helpers.map(node => node.getText(ast)).join('\n') + '\n;({captureScrollEvidence,scrollCaptureOffsets})', {
      document: { querySelectorAll: selector => selector === 'dialog[open]' ? [parent] : catalog },
      getComputedStyle: () => ({ visibility: 'visible', overflowY: 'auto' }),
      captureFileDiffEvidence: async () => {}, scrollCaptureVisibleRange: function scrollCaptureVisibleRange() {}, resolve
    });
  const elementHandle = { isVisible: async () => visible, scrollIntoViewIfNeeded: async () => {},
    evaluate: async (fn, argument) => fn.name === 'scrollCaptureVisibleRange'
      ? { scrollTop, start: scrollTop, end: scrollTop + pane.clientHeight, visibleHeight: pane.clientHeight }
      : fn(pane, argument) };
  const handle = value => ({
    getProperties: async () => new Map(Object.entries(value).map(([key, item]) => [key, handle(item)])),
    getProperty: async key => handle(value[key]), jsonValue: async () => value,
    asElement: () => value === pane ? elementHandle : null,
    dispose: async () => { if (value === pane) disposed++; }
  });
  let shots = 0;
  const page = {
    locator: () => { throw new Error('A shifting indexed locator must not select the scroll pane.'); },
    evaluateHandle: async fn => handle(fn()), evaluate: async () => {},
    screenshot: async () => { shots++; catalog.unshift({ ...parent }); }
  };
  const record = { id: 'simple-hosting-ready-start-timeout--lightnarrow150', screenshots: [], layoutFindings: [] };
  await captureScrollEvidence(page, record, '/synthetic-only');
  assert.equal(shots, offsetsFor(634, 6261).length);
  assert.ok(catalog.indexOf(pane) > 1, 'background sibling insertions changed the old locator index during capture');
  assert.deepEqual([...record.scrollEvidence[0].capturedOffsets], [...offsetsFor(634, 6261)]);
  assert.equal(record.scrollEvidence[0].coverage.complete, true);
  assert.equal(record.scrollEvidence[0].coverage.coveredThrough, 6261);
  assert.equal(scrollTop, 0); assert.equal(parent.scrollTop, 13); assert.equal(parent.scrollLeft, 7);
  assert.equal(disposed, 1);
  visible = false;
  await assert.rejects(captureScrollEvidence(page, { ...record, screenshots: [] }, '/synthetic-only'), /disappeared before capture/u,
    'a genuinely missing pane remains a failed capture instead of a skipped surface');
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
  assert.doesNotThrow(() => check(['--list', '--server-access-only'], {}, 'linux'), 'the bounded server-access inventory remains read-only');
  assert.doesNotThrow(() => check(['--list', '--sharing-delivery-only'], {}, 'linux'), 'the bounded sharing-delivery inventory remains read-only');
  for (const [argv, env, platform] of [
    [[], hosted, 'win32'], [flags, {}, 'win32'],
    [['--list', '--allow-isolated-browser-tests'], {}, 'win32'], [['--list', 'capture'], {}, 'win32'],
    [['--list', '--setup-polish-only'], {}, 'win32'], [['--setup-polish-only'], hosted, 'win32'],
    [['--server-access-only'], hosted, 'win32'], [['--sharing-delivery-only'], hosted, 'win32'],
    [['--list', '--sharing-delivery-only', '--server-access-only'], {}, 'win32'],
    [['--list', '--simple-shared-hosting-only', '--server-access-only'], {}, 'win32'],
    [['--hosted-runner', 'github-actions'], hosted, 'win32'],
    [['github-actions', '--allow-isolated-browser-tests'], hosted, 'win32'],
    [flags, { ...hosted, RUNNER_ENVIRONMENT: 'self-hosted' }, 'win32'],
    [flags, hosted, 'linux']
  ]) assert.throws(() => check(argv, env, platform), /Refusing browser execution/);
});

test('render provenance separates immutable candidate source from the current verification harness', () => {
  const indexSha256 = '1'.repeat(64), sourceRevision = 'a'.repeat(40), verificationRevision = 'b'.repeat(40);
  const candidateSha256 = '2'.repeat(64);
  const context = { sourceRevision, verificationRevision, candidateRunId: '40240756633', candidateSha256,
    rebuilt: false, appLaunched: false, fullyAccepted: false,
    uiAssets: [{ asset: 'index.html', sha256: indexSha256, sizeBytes: 123 },
      { asset: 'assets/synthetic.js', sha256: '3'.repeat(64), sizeBytes: 456 }] };
  assert.deepEqual(uiReviewBuild(indexSha256, verificationRevision),
    { indexSha256, sourceRevision: verificationRevision, verificationRevision }, 'ordinary CI retains its source identity');
  assert.deepEqual(uiReviewBuild(indexSha256, verificationRevision, context),
    { indexSha256, sourceRevision, verificationRevision, candidateRunId: '40240756633', candidateSha256 },
    'retesting unchanged candidate bytes does not relabel them as newly built verification source');
  const mutations = [
    value => { value.sourceRevision = 'candidate-main'; },
    value => { value.verificationRevision = sourceRevision; },
    value => { value.candidateRunId = 40240756633; },
    value => { value.candidateRunId = '0'; },
    value => { value.candidateSha256 = 'C'.repeat(64); },
    value => { value.rebuilt = true; },
    value => { delete value.appLaunched; },
    value => { value.fullyAccepted = true; },
    value => { value.uiAssets[0].sha256 = '4'.repeat(64); },
    value => { value.uiAssets.push({ ...value.uiAssets[0], asset: 'INDEX.HTML' }); },
    value => { value.uiAssets[1].asset = '../synthetic.js'; },
    value => { value.uiAssets[1].asset = '/synthetic.js'; },
    value => { value.uiAssets[1].asset = 'assets\\synthetic.js'; },
    value => { value.uiAssets[1].asset = 'assets//synthetic.js'; },
    value => { value.uiAssets[1].sizeBytes = -1; },
    value => { value.uiAssets.shift(); },
    value => { value.uiAssets = []; }
  ];
  for (const mutate of mutations) {
    const altered = structuredClone(context); mutate(altered);
    assert.throws(() => uiReviewBuild(indexSha256, verificationRevision, altered), /Candidate UI provenance is invalid/u);
  }
  assert.throws(() => uiReviewBuild(indexSha256, null, context), /Candidate UI provenance is invalid/u,
    'an immutable retest cannot omit the actual verification revision');
  for (const malformed of [null, false, 0, [], ''])
    assert.throws(() => uiReviewBuild(indexSha256, verificationRevision, malformed), /Candidate UI provenance is invalid/u,
      'an explicitly supplied malformed context cannot fall back to ordinary CI identity');
  const renderer = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const bind = renderer.indexOf('manifest.build = uiReviewBuild(');
  assert.ok(bind > 0 && bind < renderer.indexOf('server = createServer('), 'binding failures precede listener/browser creation');
  assert.match(renderer, /process\.env\.TOGETHERSERVER_UI_CANDIDATE_CONTEXT/u);
  assert.doesNotMatch(renderer, /process\.env\.GITHUB_SHA\s*=/u, 'candidate context never replaces the actual harness identity');
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
  assert.equal(simple.plans.length, 108, 'all thirty-three existing surfaces plus three stopped-progress journeys cover desktop, narrow and enlarged narrow text');
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

test('server-access render mode pins twelve exact surfaces and all three candidate viewport variants', () => {
  const expected = [
    'server-access-global-friend-pcs', 'server-access-selected-friend-pcs',
    'review-permissions-before-after', 'review-permissions-saved', 'review-individual-permissions', 'review-effective-access',
    'review-assignment-before-after', 'review-assignment-saved', 'review-access-discard-review', 'review-helper-active',
    'server-access-remove-server-review', 'review-remove-access-review'
  ];
  assert.deepEqual(serverAccessCaseIds, expected);
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const assignment = ast.statements.find(node => ts.isExpressionStatement(node) && node.expression.left?.getText(ast) === 'manifest.inventory');
  const start = source.indexOf('const heading ='), end = source.indexOf('manifest.inventory =');
  const inventory = source.slice(start, end) + assignment.getText(ast) + '\n;({plans,inventory:manifest.inventory,coverage:manifest.coverage})';
  const context = { manifest: {}, worldReviewCases, accessReviewCases, serverAccessCaseIds, args: ['--server-access-only'] };
  const focused = runInNewContext(inventory, context);
  assert.equal(focused.coverage.scope, 'server-access');
  assert.equal(focused.coverage.plannedCaptures, 36);
  assert.deepEqual(Array.from(focused.coverage.variants), ['darkdesktop', 'lightnarrow150', 'darkshortdesktop']);
  assert.deepEqual(Array.from(new Set(focused.plans.map(plan => plan.testCase.id))), expected);
  assert.equal(focused.inventory.length, 36);
  assert.equal(new Set(focused.inventory.map(plan => plan.id)).size, 36);
  for (const id of expected) {
    const captures = focused.plans.filter(plan => plan.testCase.id === id);
    assert.equal(captures.length, 3, `${id} cannot silently omit a viewport`);
    assert.ok(captures.some(plan => plan.variant.viewport.width === 1280 && plan.variant.viewport.height === 900 && plan.variant.theme === 'dark'));
    assert.ok(captures.some(plan => plan.variant.viewport.width === 390 && plan.variant.scale === 150));
    assert.ok(captures.some(plan => plan.variant.viewport.width === 1280 && plan.variant.viewport.height === 650));
    for (const plan of captures) {
      assert.ok(fixtureScenarios.includes(plan.testCase.scenario));
      if (plan.testCase.section?.[0] === 'This Host PC sections') assert.equal(plan.testCase.section[2], 'Access');
      assert.ok(!(plan.testCase.steps ?? []).some(step => /^Confirm remove|^Start server$|^Stop server$/u.test(String(step.button ?? ''))),
        'visual confirmation review never submits removal or lifecycle actions');
    }
  }
  assert.throws(() => runInNewContext(inventory, { ...context, manifest: {},
    accessReviewCases: accessReviewCases.filter(item => item.id !== 'server-access-remove-server-review') }),
  /Required server-access review case is missing/u, 'a disappeared named surface fails the planner');
  const scoped = accessReviewCases.find(item => item.id === 'server-access-remove-server-review');
  assert.equal(scoped.section, undefined);
  assert.equal(scoped.steps[0].button, 'Manage friend access', 'the scoped confirmation starts from the selected server action');
  const all = accessReviewCases.find(item => item.id === 'review-remove-access-review');
  assert.deepEqual(all.steps.slice(-3), [{ nav: 'PC access sections', button: 'PC details' },
    { summary: 'All server access' }, { button: 'Remove all server access' }]);
  assert.equal(all.screen.text, 'Remove all server access for Friend PC?');
});

test('server-access fixture evidence distinguishes selected and global views and keeps removal confirmations read-only', async () => {
  const state = createFixtureState('host-server-access-review-multi');
  const companion = contracts.parseCompanionInfo(get(state, '/api/local/companion'));
  assert.deepEqual(companion.devices.map(device => device.assignedProfileIds), [[fixtureIds.primary], [fixtureIds.secondary]]);
  const seen = [];
  const page = { getByRole: (role, options) => {
    seen.push([role, options.name]); return { waitFor: async () => {} };
  }, getByText: () => ({ isVisible: async () => true }) };
  for (const id of ['server-access-remove-server-review', 'review-remove-access-review']) {
    const testCase = accessReviewCases.find(item => item.id === id);
    const record = { assertions: [] };
    await assertServerAccessUi(page, testCase, record, state);
    assert.equal(record.assertions[0].submitted, false);
  }
  assert.deepEqual(seen, [['button', 'Confirm remove Weekend Valheim access'], ['button', 'Confirm remove all server access']]);
  for (const request of [{ method: 'DELETE', path: `/api/local/devices/${fixtureIds.device}/servers/${fixtureIds.primary}/access` },
    { method: 'POST', path: `/api/local/profiles/${fixtureIds.primary}/start` }]) {
    await assert.rejects(assertServerAccessUi(page, accessReviewCases.find(item => item.id === 'server-access-remove-server-review'),
      { assertions: [] }, { ...state, requests: [request] }), /must not submit removal or lifecycle/u);
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

test('capture waits for exact synthetic responses and retries activity that starts during painted observation', async () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helper = ast.statements.find(statement => ts.isFunctionDeclaration(statement) && statement.name?.text === 'waitForSyntheticResponses');
  assert.ok(helper, 'exercise the actual harness helper without a browser or listener');
  const wait = runInNewContext(`${helper.getText(ast)}\n;waitForSyntheticResponses`, { setTimeout, clearTimeout });
  let revision = 1, paints = 0, elapsed = 0;
  const pending = new Set();
  let finish;
  const token = { method: 'GET', path: '/api/local/profiles/synthetic/shared-world',
    done: new Promise(resolve => { finish = resolve; }) };
  pending.add(token);
  const page = { evaluate: async () => {
    paints++; elapsed += 16;
    assert.equal(pending.size, 0, 'paint observation follows completion of the current exact response');
    if (paints === 1) revision += 2; // A new request starts and finishes between the two samples.
  } };
  const waiting = wait(page, pending, () => revision, 8000, () => elapsed);
  assert.equal(paints, 0, 'an active route is awaited rather than failing a one-time pending sample');
  pending.delete(token); revision++; finish();
  const evidence = await waiting;
  assert.equal(evidence.pending, 0);
  assert.equal(evidence.responseRevision, 4);
  assert.equal(evidence.stablePaintFrames, 2);
  assert.equal(paints, 2, 'a response revision change invalidates the first painted interval');
  assert.match(source, /finally \{ pending\.delete\(token\); responseRevision\+\+; settled\(\); \}/u,
    'exact route completion signals both the pending set and the observed revision');
  assert.match(source, /syntheticResponsesSettled: await waitForSyntheticResponses\(page, pending, \(\) => responseRevision\)/u,
    'capture uses the bounded helper rather than a single pending-size sample');
});

test('capture quiet wait preserves failures and bounds unresolved responses, browser observations and continuous activity', async () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helper = ast.statements.find(statement => ts.isFunctionDeclaration(statement) && statement.name?.text === 'waitForSyntheticResponses');
  const wait = runInNewContext(`${helper.getText(ast)}\n;waitForSyntheticResponses`, { setTimeout, clearTimeout });
  const unresolved = new Set([{ method: 'GET', path: '/api/local/never-settles', done: new Promise(() => {}) }]);
  await assert.rejects(wait({ evaluate: async () => assert.fail('do not paint before the response settles') }, unresolved,
    () => 1, 10, () => 0), /did not settle within 10 ms.*GET \/api\/local\/never-settles/u);
  assert.equal(unresolved.size, 1, 'timing out never discards an unresolved request');
  await assert.rejects(wait({ evaluate: () => new Promise(() => {}) }, new Set(), () => 0, 10, () => 0),
    /did not settle within 10 ms/u, 'an unresponsive browser observation is also bounded');
  const failedObservation = new Error('Synthetic browser observation failed.');
  await assert.rejects(wait({ evaluate: async () => { throw failedObservation; } }, new Set(), () => 0, 8000, () => 0),
    error => error === failedObservation, 'an observed error remains a failed capture');
  let elapsed = 0, revision = 0, paints = 0;
  await assert.rejects(wait({ evaluate: async () => { elapsed += 1000; revision += 2; paints++; } }, new Set(),
    () => revision, 8000, () => elapsed), /did not settle within 8000 ms/u);
  assert.equal(paints, 8, 'continuous response activity uses one shared deadline instead of restarting its timeout');
});

test('capture settles navigation before resolving a pane and re-resolves the same screen after a detached scroll', async () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helper = ast.statements.find(statement => ts.isFunctionDeclaration(statement) && statement.name?.text === 'scrollCaptureScreen');
  assert.ok(helper);
  const beforeScreen = source.indexOf('let screen = await visible(locatorFor(page, testCase.screen));');
  assert.ok(source.lastIndexOf('syntheticResponsesSettled: await waitForSyntheticResponses(page, pending, () => responseRevision)', beforeScreen) >
    source.lastIndexOf('await performActions(page, testCase, record)', beforeScreen), 'mode-changing actions settle before the first named pane lookup');
  const specification = { role: 'region', name: 'Servers' };
  const events = [], resolved = [];
  let scrolls = 0;
  const scroll = runInNewContext(`${helper.getText(ast)}\n;scrollCaptureScreen`, {
    waitForSyntheticResponses: async () => { events.push('settle'); },
    locatorFor: (_page, requested) => {
      assert.equal(requested, specification, 'a retry must retain the exact requested named screen');
      events.push('resolve');
      const pane = { id: resolved.length, waitFor: async () => { events.push('visible'); }, scrollIntoViewIfNeeded: async () => {
        events.push('scroll');
        if (++scrolls === 1) throw new Error('locator.scrollIntoViewIfNeeded: Element is not attached to the DOM');
      } };
      resolved.push(pane);
      return { first: () => pane };
    }
  });
  const current = await scroll({}, specification, new Set(), () => 0, 8000, () => 0);
  assert.deepEqual(events, ['settle', 'resolve', 'visible', 'scroll', 'settle', 'resolve', 'visible', 'scroll', 'settle', 'resolve', 'visible']);
  assert.equal(scrolls, 2);
  assert.equal(current, resolved[2], 'return the fresh named panel after final response/paint settlement, never the detached pane');
});

test('capture scroll retries only detach failures and bounds repeated replacement with one shared deadline', async () => {
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('renderer.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helper = ast.statements.find(statement => ts.isFunctionDeclaration(statement) && statement.name?.text === 'scrollCaptureScreen');
  const detached = new Error('Element is not attached to the DOM');
  let scrolls = 0;
  const scope = { waitForSyntheticResponses: async () => {}, locatorFor: () => ({ first: () => ({
    waitFor: async () => {}, scrollIntoViewIfNeeded: async () => { scrolls++; throw detached; }
  }) }) };
  const scroll = runInNewContext(`${helper.getText(ast)}\n;scrollCaptureScreen`, scope);
  await assert.rejects(scroll({}, {}, new Set(), () => 0, 8000, () => 0),
    error => /all three settled scroll attempts/u.test(error.message) && error.cause === detached);
  assert.equal(scrolls, 3, 'persistent replacement stays a failed capture after three exact detach attempts');
  const other = new Error('The named screen is clipped by another panel.');
  scope.locatorFor = () => ({ first: () => ({ waitFor: async () => {}, scrollIntoViewIfNeeded: async () => { throw other; } }) });
  await assert.rejects(scroll({}, {}, new Set(), () => 0, 8000, () => 0), error => error === other,
    'other scroll and visibility errors propagate without a retry');
  let elapsed = 0;
  const budgets = [];
  scope.waitForSyntheticResponses = async (_page, _pending, _revision, remaining) => { budgets.push(remaining); elapsed += 1000; };
  scope.locatorFor = () => ({ first: () => ({ waitFor: async () => { elapsed += 2000; },
    scrollIntoViewIfNeeded: async () => { elapsed += 2000; throw detached; } }) });
  await assert.rejects(scroll({}, {}, new Set(), () => 0, 8000, () => elapsed), /did not remain attached within 8000 ms/u);
  assert.deepEqual(budgets, [8000, 3000], 'retrying never resets the overall deadline');
});

test('two-ended lab keeps its exact selected local run despite the grouped row naming the original friend', async () => {
  const source = readFileSync(new URL('run-local-pairing-ux.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('pairing-ux.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helper = ast.statements.find(statement => ts.isFunctionDeclaration(statement) && statement.name?.text === 'nav');
  const profileId = fixtureIds.secondary;
  const snapshot = { mode: 'Host', settings: { profiles: [{ id: profileId, name: 'Shared lab', separateCopySourceProfileId: fixtureIds.primary }] },
    runs: [{ profileId, state: 'Process running' }] };
  let reads = 0;
  const nav = runInNewContext(`${helper.getText(ast)}\n;nav`, {
    input: { profileId: fixtureIds.primary }, read: async (_page, endpoint) => {
      assert.equal(endpoint, undefined, 'keeping the current scope does not even need a directory lookup'); reads++; return snapshot;
    }, selectedLocalProfile: async () => profileId
  });
  const page = { locator: () => assert.fail('do not click a grouped friend label or another saved server'),
    getByRole: () => assert.fail('the exact current local run requires no navigation click') };
  await nav(page, 'Host', profileId);
  assert.equal(reads, 1);
  assert.ok(source.includes("nav(friend, 'Host', candidate.profileId)"), 'candidate Stop names the previously verified exact local profile');
});

test('two-ended lab directory navigation follows signed target identity instead of original-owner label suffixes', async () => {
  const source = readFileSync(new URL('run-local-pairing-ux.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('pairing-ux.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helpers = ['directoryNavigation', 'nav'].map(name => ast.statements.find(statement => ts.isFunctionDeclaration(statement) && statement.name?.text === name).getText(ast)).join('\n');
  const identity = { sharedWorldId: 'A'.repeat(64), originalProfileId: fixtureIds.primary };
  const original = { ...identity, id: fixtureIds.primary, name: 'Shared lab', state: 'Offline', isSuccessor: false };
  const continuation = { ...identity, id: fixtureIds.secondary, name: 'Shared lab continuation', state: 'Process running', isSuccessor: true };
  const host = servers => ({ connectionId: fixtureIds.connection, hostProfileName: 'Lab Owner', connectionName: 'Original PC',
    state: 'Connected', lastConnectedUtc: new Date().toISOString(), servers });
  for (const mode of ['Host', 'Join']) {
    const directory = mode === 'Host' ? { localServers: [continuation], friendHosts: [host([original])] } :
      { localServers: [original], friendHosts: [host([continuation])] };
    const profileId = mode === 'Host' ? continuation.id : original.id;
    let selected = null, clicks = 0;
    let snapshot = mode === 'Host' ? { mode: 'Friend', profiles: [original] } :
      { mode: 'Host', settings: { profiles: [original] }, runs: [{ profileId: original.id, state: 'Offline' }] };
    const nav = runInNewContext(`${helpers}\n;nav`, {
      input: { profileId: original.id }, read: async (_page, endpoint) => endpoint === '/api/local/servers' ? directory : snapshot,
      selectedLocalProfile: async () => selected, visible: value => value,
      exact: value => new RegExp(`^${value}$`), until: async (action, predicate) => {
        const value = await action(); assert.equal(predicate(value), true, 'navigation verifies the actual canonical target after the UI click'); return value;
      }
    });
    const page = { getByRole: (role, options) => {
      assert.equal(role, 'button');
      const expected = mode === 'Host' ? 'Open Shared lab on Lab Owner' : 'Open Shared lab on this PC';
      assert.ok(options.name.test(expected), 'display ownership is independent of the actual navigation target');
      return { count: async () => 1, click: async () => {
        clicks++;
        if (mode === 'Host') { selected = continuation.id; snapshot = { mode: 'Host', settings: { profiles: [continuation] }, runs: [{ profileId: continuation.id }] }; }
        else snapshot = { mode: 'Friend', connectionId: fixtureIds.connection, profiles: [continuation] };
      } };
    } };
    await nav(page, mode, profileId);
    assert.equal(clicks, 1, 'exact target navigation uses one UI directory click and no lifecycle request');
  }
  const planner = runInNewContext(`${helpers}\n;directoryNavigation`);
  assert.throws(() => planner({ localServers: [original], friendHosts: [host([continuation])] }, 'Host', original.id, Date.now()),
    /does not route to the requested PC/u, 'an owner-labelled row cannot masquerade as a local target while the friend hosts');
  assert.throws(() => planner({ localServers: [], friendHosts: [host([original]), { ...host([{ ...original, sharedWorldId: 'B'.repeat(64) }]),
    connectionId: fixtureIds.otherDevice }] }, 'Join', original.id, Date.now()), /missing or ambiguous/u,
    'unrelated signed worlds with the same original profile/name never choose an arbitrary row');
});

test('two-ended lab opens Connect for an unpaired Friend and preserves an existing exact Friend without clicking', async () => {
  const source = readFileSync(new URL('run-local-pairing-ux.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('pairing-ux.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helper = ast.statements.find(statement => ts.isFunctionDeclaration(statement) && statement.name?.text === 'nav');
  for (const paired of [false, true]) {
    let clicks = 0;
    const reads = [];
    const nav = runInNewContext(`${helper.getText(ast)}\n;nav`, {
      input: { profileId: fixtureIds.primary }, read: async (_page, endpoint) => {
        reads.push(endpoint ?? '/api/local/snapshot');
        if (endpoint) { assert.equal(endpoint, '/api/local/servers'); return { localServers: [], friendHosts: [] }; }
        return { mode: 'Friend', connectionId: paired ? fixtureIds.connection : '',
          profiles: paired ? [{ id: fixtureIds.primary, name: 'Shared lab' }] : [] };
      }
    });
    const page = { locator: selector => {
      assert.equal(selector, '.server-directory-actions');
      return { getByRole: (role, options) => {
        assert.equal(role, 'button'); assert.equal(options.name, 'Connect'); assert.equal(options.exact, true);
        return { click: async () => { clicks++; } };
      } };
    } };
    await nav(page, 'Join');
    assert.equal(clicks, paired ? 0 : 1);
    assert.deepEqual(reads, paired ? ['/api/local/snapshot'] : ['/api/local/snapshot', '/api/local/servers'],
      'navigation uses read-only evidence and never invokes a lifecycle API');
  }
});

test('two-ended lab treats blank or malformed selection as absent and selects only the exact input Host row', async () => {
  const source = readFileSync(new URL('run-local-pairing-ux.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('pairing-ux.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helpers = ['selectedLocalProfile', 'directoryNavigation', 'nav'].map(name => ast.statements.find(statement =>
    ts.isFunctionDeclaration(statement) && statement.name?.text === name).getText(ast)).join('\n');
  let preference = '', clicks = 0;
  const profile = { id: fixtureIds.primary, name: 'Local pairing lab', state: 'Offline' };
  const snapshot = { mode: 'Host', settings: { profiles: [profile] }, runs: [{ profileId: profile.id, state: 'Offline' }] };
  const scope = { input: { profileId: profile.id }, localStorage: { getItem: () => JSON.stringify({ selectedServer: preference }) },
    read: async (_page, endpoint) => endpoint ? { localServers: [profile], friendHosts: [] } : snapshot,
    visible: value => value, exact: value => new RegExp(`^${value}$`), until: async (action, predicate) => {
      const value = await action(); assert.equal(predicate(value), true); return value;
    } };
  const { nav, selectedLocalProfile } = runInNewContext(`${helpers}\n;({nav,selectedLocalProfile})`, scope);
  const page = { evaluate: async callback => callback(), getByRole: (role, options) => {
    assert.equal(role, 'button'); assert.ok(options.name.test('Open Local pairing lab on this PC'));
    return { count: async () => 1, click: async () => { clicks++; preference = profile.id; } };
  } };
  for (const invalid of ['', 'not-a-profile', '   ']) {
    preference = invalid;
    assert.equal(await selectedLocalProfile(page), null);
    await nav(page, 'Host');
  }
  assert.equal(clicks, 3, 'every missing preference falls back to the exact input profile, never an arbitrary first row');
});

test('two-ended lab proves signed single-world routing and rejects split, stale, or misrouted sidebars', async () => {
  const source = readFileSync(new URL('run-local-pairing-ux.mjs', import.meta.url), 'utf8');
  const ast = ts.createSourceFile('pairing-ux.mjs', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  const helpers = ['directoryNavigation', 'verifyWorldSidebar'].map(name => ast.statements.find(statement =>
    ts.isFunctionDeclaration(statement) && statement.name?.text === name).getText(ast)).join('\n');
  assert.equal((source.match(/await stage\(/gu) ?? []).length, 10, 'the existing ten UX stages remain required');
  assert.ok(source.includes("verifyWorldSidebar(friend, 'candidate')"));
  assert.ok(source.includes("verifyWorldSidebar(host, 'owner')"));
  assert.ok(source.includes("verifyWorldSidebar(host, 'owner', true)"));
  const run = async (location, stopped, fault) => {
    const candidate = { profileId: fixtureIds.secondary, groupKey: 'A'.repeat(64), runId: fixtureIds.session,
      endpoint: 'https://192.0.2.11:5132' };
    const identity = { sharedWorldId: candidate.groupKey, originalProfileId: fixtureIds.primary };
    const original = { ...identity, id: fixtureIds.primary, name: 'Our world', state: 'Offline', isSuccessor: false };
    const successor = { ...identity, id: candidate.profileId, name: 'Different successor name', isSuccessor: true,
      state: stopped ? 'Offline' : 'Process running' };
    if (fault === 'wrong root') successor.sharedWorldId = 'B'.repeat(64);
    if (fault === 'unknown after Stop') successor.state = 'Unknown';
    const onOwner = location === 'owner';
    const connection = { connectionId: fixtureIds.connection, state: 'Connected', lastConnectedUtc: new Date().toISOString(),
      hostProfileName: onOwner ? 'Successor PC' : 'Original owner', servers: [onOwner ? successor : original] };
    const directory = { localServers: [onOwner ? original : successor], friendHosts: [connection] };
    const reads = [];
    let clicks = 0;
    let snapshot = null;
    let selected = null;
    const row = { count: async () => 1, locator: selector => {
      assert.equal(selector, '.remote-hosting-badge');
      return { count: async () => stopped || fault === 'missing badge' ? 0 : 1,
        innerText: async () => onOwner ? 'Hosted by Successor PC' : 'Hosting on this PC' };
    }, click: async () => {
      clicks++;
      const profileId = stopped ? original.id : candidate.profileId;
      selected = profileId;
      snapshot = onOwner && !stopped && fault !== 'wrong route' ? { mode: 'Friend', connectionId: connection.connectionId,
        endpoint: candidate.endpoint, profiles: [{ id: candidate.profileId, runOperationId: candidate.runId }] } :
        { mode: 'Host', settings: { profiles: [{ id: profileId }] }, runs: [{ profileId, runOperationId: candidate.runId }] };
      if (fault === 'wrong connection') snapshot.connectionId = fixtureIds.otherDevice;
      if (fault === 'wrong successor profile') snapshot.profiles[0].id = fixtureIds.third;
      if (fault === 'wrong session') snapshot.profiles[0].runOperationId = fixtureIds.backup;
    } };
    const region = { getByRole: (role, options) => {
      if (role === 'heading') {
        assert.equal(options.level, 4);
        return { count: async () => fault === 'successor host group' ? 1 : onOwner ? 0 : 1, innerText: async () => 'Original owner' };
      }
      assert.equal(role, 'button');
      if (options.name.source === '^Open ') return { count: async () => fault === 'duplicate world row' ? 2 : 1 };
      assert.ok(options.name.test(`Open Our world on ${onOwner ? 'this PC' : 'Original owner'}`), 'the original owner labels the route');
      return row;
    } };
    const page = { getByRole: (role, options) => {
      assert.equal(role, 'region'); assert.equal(options.name, 'Servers'); assert.equal(options.exact, true); return region;
    } };
    const verify = runInNewContext(`${helpers}\n;verifyWorldSidebar`, {
      candidate, input: { profileId: original.id }, exact: value => new RegExp(`^${value}$`),
      read: async (_page, endpoint) => { reads.push(endpoint); assert.ok(endpoint === '/api/local/servers' || endpoint === undefined); return endpoint ? directory : snapshot; },
      selectedLocalProfile: async () => selected, until: async (action, predicate, label) => {
        const value = await action(); assert.equal(Boolean(predicate(value)), true, label); return value;
      }
    });
    if (fault) {
      await assert.rejects(verify(page, location, stopped), /canonical signed|one original-world|exact successor/u);
      assert.equal(clicks, ['wrong route', 'wrong connection', 'wrong successor profile', 'wrong session'].includes(fault) ? 1 : 0,
        'missing proof never leads to a UI click');
    } else {
      const proof = await verify(page, location, stopped);
      assert.equal(proof.singleOriginalRow, true); assert.equal(proof.exactRoute, true); assert.equal(proof.noSuccessorHostGroup, true);
      assert.equal(proof.hostingBadge, !stopped); assert.equal(clicks, 1);
    }
    assert.ok(reads.length > 0, 'the helper observes exact read-only directory/snapshot responses');
  };
  await run('candidate', false);
  await run('owner', false);
  await run('owner', true);
  for (const fault of ['wrong root', 'missing badge', 'duplicate world row', 'successor host group',
    'wrong route', 'wrong connection', 'wrong successor profile', 'wrong session']) await run('owner', false, fault);
  await run('owner', true, 'unknown after Stop');
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
  assert.equal(simpleHostingCases.length, 36);
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
  assert.deepEqual(assertPreparedHostingRequests(state, 0), { startRequests: 0, prepareRequests: 0 });
  assert.equal(state.simpleHostingPrepared, false, 'a ready status read does not perform preparation');
  assert.equal(routeFixture(state, { method: 'POST', path: `${base}/start`, body: start }).body.ok, true,
    'the exact already prepared identity can Start without another preparation request');
  assert.deepEqual(assertPreparedHostingRequests(state, 1), { startRequests: 1, prepareRequests: 0 });
  const invalid = createFixtureState({ id: 'friend-offline', simpleHosting: 'ready' });
  assert.equal(routeFixture(invalid, { method: 'POST', path: `${base}/start`, body: { ...start, branchHash: 'B'.repeat(64) } }), null);
  assert.equal(invalid.simpleHostingStarted, false);
  const prepare = { allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null };
  const unprepared = createFixtureState({ id: 'friend-offline', simpleHosting: 'prepare' });
  assert.equal(routeFixture(unprepared, { method: 'POST', path: `${base}/start`, body: start }), null,
    'an unprepared fixture remains unable to Start');
  assert.equal(routeFixture(unprepared, { method: 'POST', path: `${base}/prepare`, body: prepare }).body.readyForManualStart, true);
  assert.equal(routeFixture(unprepared, { method: 'POST', path: `${base}/start`, body: start }).body.ok, true);
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

test('stopped-progress coverage adds three journeys while retaining every previous hosting case and variant', () => {
  const original = ['owner-off', 'owner-on', 'owner-continuation', 'not-allowed', 'not-allowed-card', 'permission-unknown-entry',
    'permission-unknown-card', 'permission-review-entry', 'permission-review-card', 'entry', 'card-entry', 'missing-install-entry',
    'play-ready', 'network-warning', 'guide-save-preparation', 'missing-install', 'missing-connection', 'save-conflict',
    'ready-confirmation', 'starting', 'ready-explicit-start', 'ready-start-timeout', 'guide-permission-unknown',
    'guide-permission-denied', 'guide-ports', 'reopen-entry', 'stale-retry', 'recovered-retry', 'directory-removed',
    'progress-receiving', 'progress-verifying', 'directory-remote-running', 'directory-remote-stopped'].map(id => `simple-hosting-${id}`);
  assert.deepEqual(simpleHostingCases.slice(0, 33).map(item => item.id), original);
  const added = simpleHostingCases.slice(33);
  assert.deepEqual(added.map(item => item.id), ['simple-hosting-stopped-progress-required', 'simple-hosting-stopped-progress-shared', 'simple-hosting-stopped-progress-refused']);
  assert.deepEqual(added.map(item => item.stoppedProgressJourney), ['required', 'shared', 'refused']);
  assert.ok(added.every(item => item.fixture.simpleHosting === 'stopped-progress' &&
    JSON.stringify(item.steps) === JSON.stringify([{ button: 'Host locally' }])));
  assert.equal(new Set(simpleHostingCases.map(item => item.id)).size, 36);
  assert.deepEqual(simpleHostingVariants.map(item => item.id), ['darkdesktop', 'lightnarrow', 'lightnarrow150']);
  assert.equal(simpleHostingCases.length * simpleHostingVariants.length, 108);
});

test('stopped-progress fixtures use exact production status and BasicResult contracts without preparation, Start or invented success', () => {
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  const body = { localProfileId: fixtureIds.primary, branchHash: 'A'.repeat(64) };
  const service = readFileSync(new URL('../src/TogetherServer/FriendLocalHosting.StoppedProgress.cs', import.meta.url), 'utf8');
  const manager = readFileSync(new URL('../src/TogetherServer/HostManager.StoppedSavePublication.cs', import.meta.url), 'utf8');
  assert.ok(service.includes(stoppedProgressMessages.required));
  assert.ok(manager.includes(stoppedProgressMessages.shared));
  assert.ok(manager.includes(stoppedProgressMessages.proofRequired));
  for (const outcome of ['success', 'proof-refusal']) {
    const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'stopped-progress', stoppedProgressShare: outcome });
    const beforeHost = JSON.stringify(state.host);
    const initial = localHosting.parseLocalHostingStatus(routeFixture(state, { method: 'GET', path: base }).body);
    assert.equal(initial.code, 'LocalStoppedProgressNeedsSharing');
    assert.equal(initial.remedy.kind, 'ShareStoppedProgress');
    assert.equal(initial.message, stoppedProgressMessages.required);
    assert.equal(initial.readyForManualStart, false);
    assert.equal(initial.steps.find(item => item.key === 'save').state, 'ActionRequired');
    assert.deepEqual(assertStoppedProgressRequests(state, 0), { shareRequests: 0, startRequests: 0, prepareRequests: 0 });
    const wire = routeFixture(state, { method: 'POST', path: `${base}/share-stopped-progress`, body }).body;
    assert.deepEqual(Object.keys(wire).sort(), ['code', 'message', 'ok']);
    const result = contracts.parseBasicResult(wire);
    const expected = outcome === 'success'
      ? { ok: true, code: 'StoppedProgressShared', message: stoppedProgressMessages.shared }
      : { ok: false, code: 'StoppedProgressProofRequired', message: stoppedProgressMessages.proofRequired };
    assert.deepEqual(result, { ...expected, portConflicts: undefined, operationId: undefined, operationState: undefined });
    const checked = localHosting.parseLocalHostingStatus(get(state, base));
    assert.equal(checked.readyForManualStart, true, 'success needs a fresh ready read; refusal deliberately exercises a later generic Prepared read');
    assert.equal(checked.code, 'LocalHostingPrepared');
    assert.equal(state.stoppedProgressShared, outcome === 'success');
    assert.equal(state.stoppedProgressShareRefused, outcome === 'proof-refusal');
    assert.deepEqual(assertStoppedProgressRequests(state, 1), { shareRequests: 1, startRequests: 0, prepareRequests: 0, shareBody: body });
    assert.equal(JSON.stringify(state.host), beforeHost, 'sharing does not invent a running server or mutate the synthetic world/settings');
  }
});

test('stopped-progress fixture rejects unknown actions, extra fields, wrong identities and Start before confirmed sharing', () => {
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  const body = { localProfileId: fixtureIds.primary, branchHash: 'A'.repeat(64) };
  for (const request of [
    { method: 'GET', path: `${base}/share-stopped-progress` },
    { method: 'POST', path: `${base}/share-stopped-progress?unexpected=1`, body },
    { method: 'POST', path: `${base}/share-stopped-progress`, body: { ...body, acceptSplitWarning: true } },
    { method: 'POST', path: `${base}/share-stopped-progress`, body: { ...body, branchHash: 'B'.repeat(64) } },
    { method: 'POST', path: `${base}/share-stopped-progress`, body: { ...body, localProfileId: fixtureIds.secondary } },
    { method: 'POST', path: `${base}/share-stopped-progress`, body: { localProfileId: fixtureIds.primary } },
    { method: 'POST', path: `/api/local/friend/${fixtureIds.secondary}/shared-world/hosting/share-stopped-progress`,
      body: { ...body, localProfileId: fixtureIds.secondary } },
    { method: 'POST', path: `${base}/share-stopped-progress-again`, body },
    { method: 'POST', path: `${base}/prepare`, body: {} },
    { method: 'POST', path: `${base}/start`, body: { ...body, requestId: null, acceptSplitWarning: true } }
  ]) {
    const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'stopped-progress' });
    assert.equal(routeFixture(state, request), null, `${request.method} ${request.path} remains an unhandled failure`);
    assert.equal(state.stoppedProgressShared, false);
    assert.equal(state.stoppedProgressShareRefused, false);
    assert.deepEqual(state.stoppedProgressShareBodies, []);
    assert.equal(state.simpleHostingStarted, false);
  }
  const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'stopped-progress', stoppedProgressShare: 'proof-refusal' });
  routeFixture(state, { method: 'POST', path: `${base}/share-stopped-progress`, body });
  get(state, base); // Deliberately generic read must not make an unshared copy startable in the synthetic backend.
  assert.equal(routeFixture(state, { method: 'POST', path: `${base}/start`, body: { ...body, requestId: null, acceptSplitWarning: true } }), null);
  assert.throws(() => assertStoppedProgressRequests(state, 1), /without preparation, Start or replay/u);
  assert.throws(() => assertStoppedProgressRequests({ ...state, requests: [] }, 0), /without preparation, Start or replay/u,
    'recorded sharing bodies cannot disappear from the request inventory');
  const unconfigured = createFixtureState({ id: 'friend-offline', simpleHosting: 'ready' });
  assert.equal(routeFixture(unconfigured, { method: 'POST', path: `${base}/share-stopped-progress`, body }), null);
  const unknownOutcome = createFixtureState({ id: 'friend-offline', simpleHosting: 'stopped-progress', stoppedProgressShare: 'invented-success' });
  assert.equal(routeFixture(unknownOutcome, { method: 'POST', path: `${base}/share-stopped-progress`, body }), null);
  assert.equal(unknownOutcome.stoppedProgressShared, false);
});

test('prepared-copy rendered journeys retain the entry label, exact Start footer, and no-prepare/replay evidence', () => {
  const starting = simpleHostingCases.find(item => item.id === 'simple-hosting-starting');
  assert.deepEqual(starting.steps, [{ button: 'Host locally' }, { button: 'Start hosting', within: 'dialog[open]' }]);
  const journey = simpleHostingCases.find(item => item.readyStartJourney);
  const timeout = simpleHostingCases.find(item => item.readyStartTimeout);
  assert.equal(journey.fixture.simpleHosting, 'ready');
  assert.deepEqual(journey.steps, [{ button: 'Host locally' }], 'the hosted assertion checks the read-only dialog before clicking Start');
  assert.equal(timeout.fixture.simpleHosting, 'ready');
  assert.equal(timeout.fixture.hostingStartPending, true);
  assert.deepEqual(timeout.steps, [{ button: 'Host locally' }]);
  assert.throws(() => assertPreparedHostingRequests({ requests: [{ method: 'POST', path: '/shared-world/hosting/prepare' }] }, 0),
    /without preparation or replay/u);
  assert.throws(() => assertPreparedHostingRequests({ requests: [
    { method: 'POST', path: '/shared-world/hosting/prepare' }, { method: 'POST', path: '/shared-world/hosting/start' }
  ] }, 1), /without preparation or replay/u, 'the previous prepare-then-Start loop fails the ready-copy assertion');
  assert.throws(() => assertPreparedHostingRequests({ requests: [
    { method: 'POST', path: '/shared-world/hosting/start' }, { method: 'POST', path: '/shared-world/hosting/start' }
  ] }, 1), /without preparation or replay/u, 'a second Start is never accepted as timeout recovery');
  const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'ready', hostingStartPending: true });
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  const before = JSON.stringify(state.host.runs);
  const body = { localProfileId: fixtureIds.primary, branchHash: 'A'.repeat(64), requestId: null, acceptSplitWarning: true };
  const pending = routeFixture(state, { method: 'POST', path: `${base}/start`, body });
  assert.equal(pending.pending, true);
  const passive = routeFixture(state, { method: 'GET', path: `${base}/progress` });
  assert.equal(passive.pending, true, 'the timeout fixture explicitly retains its passive progress read until the UI aborts it');
  assert.equal(passive.body, null, 'a missing progress response does not invent completion or byte evidence');
  assert.equal(state.simpleHostingStarted, false);
  assert.equal(JSON.stringify(state.host.runs), before, 'pending Start is not a synthetic running-server claim');
  for (let attempt = 0; attempt < 3; attempt++) {
    assert.equal(localHosting.parseLocalHostingStatus(get(state, base)).readyForManualStart, true);
    assert.equal(get(state, `${base}/access`).allowed, true);
    routeFixture(state, { method: 'POST', path: '/api/local/mode/host' });
    routeFixture(state, { method: 'POST', path: '/api/local/mode/friend' });
  }
  assert.deepEqual(assertPreparedHostingRequests(state, 1), { startRequests: 1, prepareRequests: 0 },
    'read-only refreshes and synthetic page changes do not submit another hosting instruction');
});

test('only the exact passive progress abort after a validated pending Start is an intentional fixture outcome', () => {
  const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'ready', hostingStartPending: true });
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  const aborted = { method: 'GET', path: `${base}/progress`, error: 'net::ERR_ABORTED' };
  assert.equal(expectedFixtureRequestAbort(state, aborted), false, 'an abort before Start remains a failure');
  assert.equal(routeFixture(state, { method: 'POST', path: `${base}/start`, body: {} }), null);
  assert.equal(expectedFixtureRequestAbort(state, aborted), false, 'an invalid Start body cannot authorize an expected abort');
  routeFixture(state, { method: 'POST', path: `${base}/start`,
    body: { localProfileId: fixtureIds.primary, branchHash: 'A'.repeat(64), requestId: null, acceptSplitWarning: true } });
  assert.equal(state.requests.some(item => item.path.endsWith('/progress')), false,
    'the observed browser race aborts before the passive route handler runs');
  assert.equal(expectedFixtureRequestAbort(state, aborted), true);
  for (const changed of [
    { method: 'POST' }, { error: 'net::ERR_FAILED' }, { path: `${base}/start` },
    { path: `/api/local/friend/${fixtureIds.secondary}/shared-world/hosting/progress` }, { path: `${base}/progress?unexpected=1` }
  ]) assert.equal(expectedFixtureRequestAbort(state, { ...aborted, ...changed }), false,
    'unrelated failures cannot be hidden as a deliberate passive-read cancellation');
  assert.equal(expectedFixtureRequestAbort({ ...state, hostingStartPending: false }, aborted), false);
  assert.equal(expectedFixtureRequestAbort({ ...state, simpleHosting: 'prepare' }, aborted), false);
  const source = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  assert.match(source, /kind: 'intentional-passive-progress-abort'/u, 'the scoped cancellation remains in the artifact evidence');
});

test('clock-driven hosting captures install before polling and wait for settled responses after advancing time', async () => {
  const installs = [];
  for (const testCase of simpleHostingCases) {
    const installed = await installHostingReviewClock({ clock: { install: async options => installs.push({ id: testCase.id, options }) } }, testCase, '2026-10-10T16:00:00Z');
    assert.equal(installed, testCase.readyStartTimeout === true || testCase.stoppedProgressJourney === 'refused' || testCase.hostingEntry === 'hidden');
  }
  assert.deepEqual(installs, [
    ...simpleHostingCases.filter(testCase => testCase.hostingEntry === 'hidden')
      .map(testCase => ({ id: testCase.id, options: { time: '2026-10-10T16:00:00Z' } })),
    { id: 'simple-hosting-ready-start-timeout', options: { time: '2026-10-10T16:00:00Z' } },
    { id: 'simple-hosting-stopped-progress-refused', options: { time: '2026-10-10T16:00:00Z' } }
  ]);
  const renderer = readFileSync(new URL('render-ui-redesign.mjs', import.meta.url), 'utf8');
  const clockInstall = renderer.indexOf('await installHostingReviewClock(page, testCase, fixtureNow)');
  assert.ok(clockInstall > 0 && clockInstall < renderer.indexOf('await page.goto('),
    'every clock-driven journey installs before the application can register its first polling timer');
  const review = readFileSync(new URL('simple-shared-hosting-review.mjs', import.meta.url), 'utf8');
  assert.doesNotMatch(review.slice(review.indexOf('export async function assertSimpleHostingUi')), /clock\.install/u,
    'no late installation can strand a pre-existing automatic poll on native timers');
  const rendererAst = ts.createSourceFile('renderer.mjs', renderer, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  let initSource;
  const findInit = node => {
    if (ts.isCallExpression(node) && node.expression.getText(rendererAst) === 'page.addInitScript') initSource = node.arguments[0].getText(rendererAst);
    ts.forEachChild(node, findInit);
  };
  findInit(rendererAst);
  assert.ok(initSource);
  for (const clockDriven of [true, false]) {
    const globals = { Date, crypto: {}, localStorage: { setItem: () => {} } };
    const init = runInNewContext(`(${initSource})`, globals);
    init({ theme: 'dark', scale: 100, now: '2026-10-10T16:00:00Z', clockDriven });
    if (clockDriven) assert.equal(globals.Date, Date, 'the fixed-date shim cannot replace the installed clock Date');
    else assert.equal(globals.Date.now(), FIXED_NOW, 'ordinary captures retain their fixed synthetic observation time');
  }
  let completeRead;
  let loading = true;
  let expectedButton = 'Start hosting', expectedTicks = 30001;
  const order = [];
  const response = { url: () => 'http://fixture.invalid/api/local/friend/synthetic/shared-world/hosting',
    request: () => ({ method: () => 'GET' }), ok: () => true,
    finished: async () => { order.push('body-finished'); loading = false; } };
  const button = { isConnected: true, get disabled() { return loading; } };
  const modal = { getByRole: (role, options) => {
    if (role === 'button') { assert.equal(options.name, expectedButton); return { elementHandle: async () => button }; }
    assert.equal(role, 'region'); assert.equal(options.name, 'Hosting progress');
    return { count: async () => Number(loading) };
  } };
  const page = {
    waitForResponse: (predicate, options) => {
      order.push('response-registered'); assert.deepEqual(options, { timeout: 8000 });
      assert.equal(predicate(response), true);
      assert.equal(predicate({ ...response, url: () => `${response.url()}/progress` }), false);
      assert.equal(predicate({ ...response, request: () => ({ method: () => 'POST' }) }), false);
      return new Promise(resolve => { completeRead = resolve; });
    },
    clock: { fastForward: async ticks => { assert.equal(ticks, expectedTicks); order.push('clock-advanced'); } },
    waitForFunction: async (predicate, element, options) => {
      assert.deepEqual(options, { timeout: 8000 }); assert.equal(predicate(element), true);
      order.push('control-settled');
    },
    evaluate: async () => { assert.equal(loading, false); order.push('paint-settled'); }
  };
  const flight = advancePreparedHostingCheck(page, modal, 30001);
  await Promise.resolve();
  assert.deepEqual(order, ['response-registered', 'clock-advanced'], 'advancing timers alone cannot authorize capture');
  assert.equal(loading, true);
  completeRead(response);
  assert.equal(await flight, response);
  assert.deepEqual(order, ['response-registered', 'clock-advanced', 'body-finished', 'control-settled', 'paint-settled']);
  expectedButton = 'Share stopped progress'; expectedTicks = 10001; loading = true;
  const sharingCheck = advancePreparedHostingCheck(page, modal, 10001, 'Share stopped progress');
  await Promise.resolve();
  completeRead(response);
  assert.equal(await sharingCheck, response, 'the refusal journey can await the automatic read while retaining its sharing footer');
  expectedButton = 'Start hosting'; expectedTicks = 30001;
  loading = true;
  const failed = advancePreparedHostingCheck(page, modal, 30001);
  await Promise.resolve();
  completeRead({ ...response, ok: () => false });
  await assert.rejects(failed, /final automatic hosting check did not succeed/u,
    'failed refreshes cannot be relabeled as a stable captured result');
});

test('prepared timeout capture requires blocked Start and honest read-only result checks after generic Prepared', async () => {
  let startEnabled = false;
  let earlierText = 'The earlier Start result is unconfirmed.';
  let progressText = 'Checking the earlier Start result. No matching progress was received.';
  const actions = [];
  const checkButton = { isConnected: true, disabled: false };
  const page = { waitForFunction: async (predicate, element) => { assert.equal(predicate(element), true); } };
  const modal = { getByRole: (role, options) => {
    if (role === 'button' && options.name === 'Check Start result') return {
      waitFor: async () => actions.push('read-only-control-visible'), elementHandle: async () => checkButton,
      click: () => { throw new Error('A capture assertion must not issue a new instruction.'); }
    };
    if (role === 'button' && options.name === 'Start hosting') return { isEnabled: async () => startEnabled };
    if (role === 'button' && options.name === 'Retry and start') return { count: async () => 0 };
    if (role === 'status' && options.name === 'Last check result') return { getByText: pattern => {
      assert.equal(pattern.test('Checking this PC…'), false);
      assert.equal(pattern.test('Refresh finished: results unchanged'), true);
      return { waitFor: async () => actions.push('automatic-read-finished') };
    } };
    if (role === 'status' && options.name === 'Earlier Start result') return {
      waitFor: async () => actions.push('unconfirmed-result-visible'), textContent: async () => earlierText
    };
    assert.equal(role, 'region'); assert.equal(options.name, 'Hosting progress');
    return { count: async () => 1, textContent: async () => progressText };
  } };
  await assertUnconfirmedStart(page, modal);
  assert.deepEqual(actions, ['read-only-control-visible', 'automatic-read-finished', 'unconfirmed-result-visible']);
  startEnabled = true;
  await assert.rejects(assertUnconfirmedStart(page, modal), /cannot authorize another Start/u,
    'the previous re-enabled Start behavior must fail the new timeout journey');
  startEnabled = false; earlierText = 'This PC is prepared.';
  await assert.rejects(assertUnconfirmedStart(page, modal), /cannot authorize another Start/u,
    'generic readiness cannot replace the unconfirmed outcome');
  earlierText = 'The earlier Start result is unconfirmed.'; progressText = 'Checking this PC...';
  await assert.rejects(assertUnconfirmedStart(page, modal), /separate read-only result feedback/u,
    'ordinary refresh loading is not a settled unconfirmed Start result');
  const timeout = simpleHostingCases.find(item => item.readyStartTimeout);
  const state = createFixtureState({ id: timeout.scenario, ...timeout.fixture });
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  routeFixture(state, { method: 'POST', path: `${base}/start`,
    body: { localProfileId: fixtureIds.primary, branchHash: 'A'.repeat(64), requestId: null, acceptSplitWarning: true } });
  for (let read = 0; read < 3; read++) assert.equal(get(state, base).readyForManualStart, true);
  assert.equal(routeFixture(state, { method: 'GET', path: `${base}/progress` }).pending, true,
    'this journey deliberately has no matching terminal response; it cannot claim reconciliation');
  assert.deepEqual(assertPreparedHostingRequests(state, 1), { startRequests: 1, prepareRequests: 0 });
});

test('hosting progress fixtures expose passive byte updates and indeterminate verification during one pending preparation', () => {
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  const prepare = { allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null };
  for (const phase of ['Receiving', 'Verifying']) {
    const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'prepare', hostingProgress: phase });
    assert.equal(routeFixture(state, { method: 'GET', path: `${base}/progress` }).body, null);
    assert.equal(routeFixture(state, { method: 'POST', path: `${base}/prepare`, body: prepare }).pending, true);
    const first = hostingProgress.parseHostingProgress(get(state, `${base}/progress`));
    assert.equal(first.phase, phase);
    assert.equal(first.state, 'Working');
    assert.equal(first.totalBytes, 4194304);
    assert.equal(first.percent, phase === 'Receiving' ? 25 : null);
    assert.ok(first.phaseTimings.some(timing => timing.phase === phase && timing.elapsedMilliseconds > 0));
    assert.throws(() => hostingProgress.parseHostingProgress({ ...first,
      phaseTimings: [{ phase: 'Verifying', elapsedMilliseconds: -1 }] }), /progress/u);
    state.hostingProgressReceivedBytes = 3145728;
    const next = hostingProgress.parseHostingProgress(get(state, `${base}/progress`));
    assert.equal(next.percent, phase === 'Receiving' ? 75 : null);
    assert.equal(next.receivedBytes, phase === 'Receiving' ? 3145728 : 4194304);
    assert.equal(state.simpleHostingStarted, false);
    assert.equal(state.requests.filter(item => item.method !== 'GET').length, 1);
    const plan = simpleHostingCases.find(item => item.fixture?.hostingProgress === phase);
    assert.equal(plan.pendingState, true);
    assert.equal(plan.screen.name, 'Hosting progress');
  }
});

test('owner remote-host directory fixtures bind one signed identity through running and stopped successor sessions', () => {
  for (const session of ['Running', 'Stopped']) {
    const state = createFixtureState({ id: 'host-offline', directoryRemote: session });
    const directory = get(state, '/api/local/servers');
    const original = directory.localServers[0];
    const successor = directory.friendHosts[0].servers[0];
    assert.equal(original.sharedWorldId, successor.sharedWorldId);
    assert.equal(original.id, successor.originalProfileId);
    assert.equal(original.isSuccessor, false);
    assert.equal(successor.isSuccessor, true);
    assert.notEqual(original.id, successor.id);
    assert.equal(successor.state, session === 'Running' ? 'Ready' : 'Offline');
    assert.doesNotThrow(() => contracts.parseSnapshot(state.host));
    assert.doesNotThrow(() => contracts.parseFriendSnapshot(state.friend));
    assert.ok(simpleHostingCases.some(item => item.fixture?.directoryRemote === session));
  }
});

test('hosting review distinguishes initial permission failures from access changes after entry', async () => {
  const { simpleHostingPermission, simpleHostingStatus } = await import('./simple-shared-hosting-review.mjs');
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  for (const state of [createFixtureState('friend-offline'), {}]) {
    assert.deepEqual(simpleHostingPermission(state, 'Synthetic'), { allowed: true, code: 'HostingAllowed', message: 'Synthetic' });
    assert.equal(simpleHostingStatus(state, fixtureIds.primary, 'Synthetic').allowed, true);
  }
  for (const [stage, code] of [['permission-unknown', 'RosterUnavailable'], ['permission-review', 'RosterRollback'],
    ['permission-denied', 'HostingPermissionRequired']]) {
    for (const changedAfterEntry of [false, true]) {
      const state = createFixtureState({ id: 'friend-offline', simpleHosting: `${stage}${changedAfterEntry ? '-after-entry' : ''}` });
      const access = routeFixture(state, { method: 'GET', path: `${base}/access` }).body;
      const status = routeFixture(state, { method: 'GET', path: base }).body;
      // The status keeps the fixture boundary; access already adds its own explanation.
      assert.deepEqual(access, simpleHostingPermission(state, status.message));
      assert.equal(access.allowed, changedAfterEntry, 'only the explicit access-change fixture permits the initial entry lookup');
      assert.equal(access.code, changedAfterEntry ? 'HostingAllowed' : code);
      assert.deepEqual(status, simpleHostingStatus(state, fixtureIds.primary, status.message));
      assert.equal(status.code, code);
      assert.equal(status.allowed, false);
      assert.equal(status.readyForManualStart, false);
      assert.equal(status.steps[0].state, stage === 'permission-denied' ? 'ActionRequired' : 'Unknown');
      assert.doesNotThrow(() => localHosting.parseLocalHostingStatus(status));
      assert.equal(state.requests.some(request => request.method !== 'GET'), false);
    }
  }
  for (const id of ['guide-permission-unknown', 'guide-permission-denied', 'reopen-entry']) {
    const testCase = simpleHostingCases.find(item => item.id === `simple-hosting-${id}`);
    const state = createFixtureState({ id: testCase.scenario, ...testCase.fixture });
    assert.equal(routeFixture(state, { method: 'GET', path: `${base}/access` }).body.allowed, true,
      'a guide case must start from a positively authorized visible entry');
    assert.equal(routeFixture(state, { method: 'GET', path: base }).body.allowed, false,
      'the already-open guide then receives the changed permission');
  }
  const ports = createFixtureState({ id: 'friend-offline', simpleHosting: 'ports' });
  const status = routeFixture(ports, { method: 'GET', path: base }).body;
  assert.equal(status.code, 'PortConflict');
  assert.equal(status.readyForManualStart, false);
  assert.equal(status.steps.find(step => step.key === 'ports').state, 'ActionRequired');
});

test('unprepared hosting fixtures require explicit preparation and preserve no-grant Start denial', () => {
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'prepare' });
  const access = routeFixture(state, { method: 'GET', path: `${base}/access` }).body;
  const status = routeFixture(state, { method: 'GET', path: base }).body;
  assert.equal(access.allowed, true);
  assert.equal(status.code, 'LocalHostingPreparationRequired');
  assert.equal(status.ok, false);
  assert.equal(status.consented, true, 'this state is past consent and has not prepared a hosting copy');
  assert.equal(status.readyForManualStart, false);
  assert.equal(status.localProfileId, null);
  assert.equal(status.branchHash, null);
  assert.equal(status.steps.find(step => step.key === 'save').state, 'Unknown');
  assert.equal(state.simpleHostingPrepared, false);
  assert.equal(state.simpleHostingStarted, false);
  assert.ok(state.requests.every(request => request.method === 'GET'));
  assert.doesNotThrow(() => localHosting.parseLocalHostingStatus(status));
  const guide = simpleHostingCases.find(item => item.id === 'simple-hosting-guide-save-preparation');
  assert.equal(guide.fixture.simpleHosting, 'prepare');
  assert.deepEqual(guide.steps, [{ button: 'Host locally' }], 'the rendered preparation guide stops before the network confirmation');
  assert.deepEqual(guide.screen, { role: 'dialog', name: 'Host locally' });
  const start = { localProfileId: fixtureIds.primary, branchHash: 'A'.repeat(64), requestId: null, acceptSplitWarning: true };
  const prepare = { allowReceiving: true, acceptSplitWarning: true, requirePeerConnection: true, serverFile: null, gamePassword: null };
  for (const stage of ['not-allowed', 'permission-unknown', 'permission-review']) {
    const blocked = createFixtureState({ id: 'friend-offline', simpleHosting: stage });
    assert.equal(routeFixture(blocked, { method: 'GET', path: `${base}/access` }).body.allowed, false);
    assert.equal(routeFixture(blocked, { method: 'POST', path: `${base}/start`, body: start }), null);
    const result = routeFixture(blocked, { method: 'POST', path: `${base}/prepare`, body: prepare }).body;
    assert.equal(result.allowed, false);
    assert.equal(result.readyForManualStart, false);
    assert.equal(blocked.simpleHostingPrepared, false);
    assert.equal(routeFixture(blocked, { method: 'POST', path: `${base}/start`, body: start }), null);
    assert.equal(blocked.simpleHostingStarted, false);
  }
});

test('hosting access fixtures keep passive reads separate from strict scoped explicit checks without preparing or starting', () => {
  const path = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting/access`;
  for (const stage of ['not-allowed', 'permission-unknown', 'permission-review', 'prepare']) {
    const state = createFixtureState({ id: 'friend-offline', simpleHosting: stage });
    const before = JSON.stringify({ host: state.host, friend: state.friend });
    const passive = routeFixture(state, { method: 'GET', path });
    assert.equal(passive.status, 200);
    const checked = routeFixture(state, { method: 'POST', path: `${path}/check`, body: {} });
    assert.equal(checked.status, 200);
    assert.deepEqual(checked.body, passive.body, 'an explicit check preserves the fixture permission instead of granting access');
    for (let poll = 0; poll < 3; poll++)
      assert.deepEqual(routeFixture(state, { method: 'GET', path }).body, passive.body);
    assert.equal(state.requests.filter(request => request.method === 'POST').length, 1);
    assert.equal(state.simpleHostingPrepared, false);
    assert.equal(state.simpleHostingStarted, false);
    assert.equal(JSON.stringify({ host: state.host, friend: state.friend }), before);
    assert.equal(routeFixture(state, { method: 'POST', path: `${path}/check`, body: '{}' }).status, 200);
  }
  const state = createFixtureState({ id: 'friend-offline', simpleHosting: 'permission-unknown' });
  const rejected = request => {
    const response = routeFixture(state, request);
    assert.ok(response === null || response.status >= 400, `must reject ${request.method} ${request.path} with ${JSON.stringify(request.body)}`);
    assert.equal(state.simpleHostingPrepared, false);
    assert.equal(state.simpleHostingStarted, false);
  };
  for (const body of [undefined, null, [], true, 0, 'null', '[]', 'true', '"text"', '{', '{"force":true}',
    '{"force":true,"force":false}', `${' '.repeat(64)}{}`])
    rejected({ method: 'POST', path: `${path}/check`, body });
  for (const body of [{}, [], { force: true }, '{}', '[]']) rejected({ method: 'GET', path, body });
  for (const method of ['GET', 'PUT', 'DELETE', 'PATCH']) rejected({ method, path: `${path}/check`, body: {} });
  for (const method of ['POST', 'PUT', 'DELETE', 'PATCH']) rejected({ method, path, body: {} });
  for (const invalid of [
    `${path}/check?force=true`, `${path}/check/extra`,
    `/api/local/profiles/${fixtureIds.primary}/shared-world/hosting/access/check`,
    '/api/local/friend/not-a-profile/shared-world/hosting/access/check',
    '/api/local/friend/eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee/shared-world/hosting/access/check'
  ]) rejected({ method: 'POST', path: invalid, body: {} });
  rejected({ method: 'GET', path: `${path}?force=true` });
  state.friend.profiles = [];
  rejected({ method: 'GET', path });
  rejected({ method: 'POST', path: `${path}/check`, body: {} });
});

test('hidden hosting launcher evidence requires one clicked access check followed by passive GET polling across both presentations', async () => {
  const { assertSimpleHostingUi } = await import('./simple-shared-hosting-review.mjs');
  const hidden = simpleHostingCases.filter(item => item.hostingEntry === 'hidden');
  assert.equal(hidden.length, 6);
  for (const stage of ['not-allowed', 'permission-unknown', 'permission-review']) {
    assert.deepEqual(hidden.filter(item => item.fixture.simpleHosting === stage).map(item => item.section[1]).sort(), ['play', 'shared-saves']);
  }
  const base = `/api/local/friend/${fixtureIds.primary}/shared-world/hosting`;
  for (const testCase of hidden) {
    const state = createFixtureState({ id: testCase.scenario, ...testCase.fixture });
    const permission = routeFixture(state, { method: 'GET', path: `${base}/access` }).body;
    assert.equal(permission.allowed, false);
    assert.deepEqual(testCase.steps, [], 'a hidden-entry case must never attempt to open hosting');
    const retryExpected = permission.code !== 'HostingPermissionRequired';
    const counts = { launcher: 0, region: 0, dialog: 0, list: 0, status: 1, retry: Number(retryExpected) };
    let retryEnabled = true;
    let completeResponse;
    let clicks = 0;
    let polls = 0;
    let pollReplaysCheck = false;
    const respond = (method, path, body) => {
      const response = routeFixture(state, { method, path, body });
      completeResponse({ url: () => `http://synthetic.invalid${path}`,
        request: () => ({ method: () => method, postDataJSON: () => body }),
        ok: () => response.status === 200, json: async () => response.body, finished: async () => null });
    };
    const access = {
      waitFor: async options => assert.equal(options.state, 'visible'), count: async () => counts.status,
      isVisible: async () => counts.status === 1,
      getByText: (text, options) => {
        assert.equal(permission.message.includes(text), true, 'the status must explain this scoped permission result');
        assert.deepEqual(options, { exact: false });
        return { waitFor: async settings => assert.equal(settings.state, 'visible') };
      }
    };
    const retry = { count: async () => counts.retry, isEnabled: async () => retryEnabled,
      click: async () => {
        clicks++;
        respond('POST', `${base}/access/check`, {});
      } };
    const scope = { getByRole: (role, options) => {
      assert.equal(options.exact, true);
      if (role === 'status' && options.name === 'Hosting access') return access;
      assert.equal(role, 'button'); assert.equal(options.name, 'Check hosting access again');
      return retry;
    } };
    const page = {
      evaluate: async action => assert.match(action.toString(), /requestAnimationFrame\(\(\) => requestAnimationFrame/u,
        'the next timer advance waits for the previous passive result to finish painting'),
      clock: { fastForward: async ticks => {
        assert.equal(ticks, 10001);
        polls++;
        if (pollReplaysCheck) routeFixture(state, { method: 'POST', path: `${base}/access/check`, body: {} });
        respond('GET', `${base}/access`);
      } },
      getByRole: (role, options) => {
        if (role === 'region' && options.name === 'Play') return scope;
        const key = role === 'button' ? 'launcher' : role;
        assert.equal(options.name, role === 'list' ? 'Local hosting checks' : 'Host locally');
        return { waitFor: async settings => assert.equal(settings.state, 'hidden'), count: async () => counts[key] };
      },
      locator: selector => { assert.equal(selector, '.friend-primary', 'styling classes do not identify the hidden launcher'); return scope; },
      waitForResponse: (predicate, options) => {
        assert.deepEqual(options, { timeout: 8000 });
        return new Promise(resolve => { completeResponse = response => {
          assert.equal(predicate(response), true, 'each wait binds the explicit POST or passive scoped GET'); resolve(response);
        }; });
      }
    };
    const record = { assertions: [] };
    await assertSimpleHostingUi(page, testCase, record, state);
    assert.equal(record.assertions[0].hostingEntryHidden, true);
    assert.equal(record.assertions[0].hostingAccessStatusVisible, true);
    assert.equal(record.assertions[0].hostingAccessRetry, retryExpected);
    assert.equal(record.assertions[0].passiveAccessPolling, true);
    assert.equal(record.assertions[0].explicitAccessChecks, Number(retryExpected));
    assert.equal(record.assertions[0].prepareRequests, 0);
    assert.equal(record.assertions[0].startRequests, 0);
    assert.equal(record.assertions[0].presentation, testCase.section[1] === 'shared-saves' ? 'card' : 'action');
    assert.equal(clicks, Number(retryExpected), 'only unresolved access causes one explicit button click');
    assert.equal(polls, 2, 'two later polls must leave explicit checking disabled');
    assert.deepEqual(state.requests, [
      { method: 'GET', path: `${base}/access` },
      ...(retryExpected ? [{ method: 'POST', path: `${base}/access/check` }] : []),
      { method: 'GET', path: `${base}/access` }, { method: 'GET', path: `${base}/access` }
    ]);
    assert.equal(state.simpleHostingPrepared, false);
    assert.equal(state.simpleHostingStarted, false);
    state.requests = [{ method: 'GET', path: `${base}/access` }];
    for (const surface of ['launcher', 'region', 'dialog', 'list']) {
      counts[surface] = 1;
      await assert.rejects(assertSimpleHostingUi(page, testCase, { assertions: [] }, state), /Denied or unknown permission must hide/u);
      counts[surface] = 0;
    }
    counts.status = 0;
    await assert.rejects(assertSimpleHostingUi(page, testCase, { assertions: [] }, state), /must show one scoped Hosting access status/u);
    counts.status = 1;
    counts.retry = Number(!retryExpected);
    await assert.rejects(assertSimpleHostingUi(page, testCase, { assertions: [] }, state), /needs an enabled explicit access check/u);
    counts.retry = Number(retryExpected);
    if (retryExpected) {
      retryEnabled = false;
      await assert.rejects(assertSimpleHostingUi(page, testCase, { assertions: [] }, state), /needs an enabled explicit access check/u);
      retryEnabled = true;
    }
    await assert.rejects(assertSimpleHostingUi(page, testCase, { assertions: [] }, { ...state, requests: [] }),
      /Denied or unknown permission must hide/u, 'an unchecked initial render is not hidden-permission evidence');
    for (const request of [{ method: 'GET', path: base }, { method: 'POST', path: `${base}/prepare` },
      { method: 'POST', path: `${base}/start` }, { method: 'GET', path: `${base}/access/check` },
      { method: 'POST', path: `${base}/access/check` },
      { method: 'GET', path: `/api/local/friend/${fixtureIds.secondary}/shared-world/hosting/access` }]) {
      await assert.rejects(assertSimpleHostingUi(page, testCase, { assertions: [] }, { ...state, requests: [...state.requests, request] }),
        /Denied or unknown permission must hide/u, 'hidden entries must not run setup checks or write hosting state');
    }
    pollReplaysCheck = true;
    await assert.rejects(assertSimpleHostingUi(page, testCase, { assertions: [] }, state),
      /Denied or unknown permission must hide/u, 'a later poll cannot repeat the explicit access check');
  }
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
