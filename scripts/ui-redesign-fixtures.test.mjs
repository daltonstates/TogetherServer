import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { createRequire, registerHooks, stripTypeScriptTypes } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { createFixtureState, routeFixture, fixtureScenarios, fixtureIds, FIXED_NOW } from './ui-redesign-fixtures.mjs';

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
const { parseChatRoomWithNotice } = await import(new URL('pinnedNoticeContracts.ts', sourceRoot).href);
const { parseNotificationPreferences, parseDesktopUpdateDetails, parseUpdatePreparation } = await import(new URL('desktopPreferencesWire.ts', sourceRoot).href);
const { parseProtectedDraftResult } = await import(new URL('protectedUiDrafts.ts', sourceRoot).href);
const { normalizedDirectIpHttpsEndpoint } = await import(new URL('sharedWorldUx.ts', sourceRoot).href);
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
const chat = pureDecoders('ServerChat.tsx', ['parseManagedChatRoom'], {
  parseChatRoomWithNotice, ContractError: contracts.ContractError
});
const live = pureDecoders('LiveSaveControls.tsx', ['record', 'text', 'parseAttempt', 'parseLiveSaveStatus'], {
  ContractError: contracts.ContractError, stageLabels: Object.fromEntries(['completion', 'snapshot', 'transfer', 'load', 'change', 'restart'].map(id => [id, id]))
});
const shared = pureDecoders('SharedWorldControls.tsx', ['record', 'boolean', 'numberOrNull', 'textOrNull', 'timestampOrNull',
  'shortText', 'guid', 'hash', 'captureKind', 'parseRecoveryStatus', 'parseHandoffStatus', 'parseGrants', 'parseRosterMember',
  'parseHostSharedWorldStatus', 'parseAuthorityHead', 'parseAuthorityStatus', 'parseFriendSharedWorldStatus', 'parseFriendResult', 'parseRoster', 'parseSharingView'], {
  deviceIdPattern: /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
  parseLiveSaveStatus: live.parseLiveSaveStatus, parseBasicResult: contracts.parseBasicResult
});
const files = pureDecoders('ServerFilesPanel.tsx', ['entry', 'field', 'flag', 'parseFilesView', 'parseFileContent', 'parseSetupBackups'],
  { ContractError: contracts.ContractError });
const addons = pureDecoders('ServerAddOnsPanel.tsx', ['object', 'string', 'boolean', 'parseAddOns'], { ContractError: contracts.ContractError });
const readiness = pureDecoders('SharedWorldReadinessPanel.tsx', ['parseRestoreStatus'], { normalizedDirectIpHttpsEndpoint });
const successor = pureDecoders('SharedHostingIdentity.tsx', ['record', 'validHash', 'parseSharedSuccessorIdentity'], { normalizedDirectIpHttpsEndpoint });
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

test('all presets decode Host and Friend snapshots with current strict decoders', () => {
  for (const scenario of fixtureScenarios) {
    const state = createFixtureState(scenario);
    assert.doesNotThrow(() => contracts.parseSnapshot(state.host), `${scenario} host`);
    assert.doesNotThrow(() => contracts.parseFriendSnapshot(state.friend), `${scenario} friend`);
    assert.doesNotThrow(() => contracts.parseSnapshot(get(state, '/api/local/snapshot')), scenario);
  }
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
  shared.parseHandoffStatus(get(host, `${hostBase}/handoff`));
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
  for (const [argv, env, platform] of [
    [[], hosted, 'win32'], [flags, {}, 'win32'],
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
  const { plans } = runInNewContext(source.slice(start, end) + '\n;({plans})', { manifest: {} });
  const captures = new Set(plans.map(plan => plan.testCase.id));
  for (const expected of [
    ...['overview', 'chat', 'players', 'logs', 'sessions', 'backups', 'files', 'setup'].map(id => `host-${id}`),
    ...['access', 'stop', 'network', 'diagnostics', 'advanced'].map(id => `host-pc-${id}`),
    'host-first-use', 'host-compare-all',
    ...['play', 'chat', 'logs', 'shared-saves', 'network', 'connection', 'pairing'].map(id => `join-${id}`),
    'join-shared-verified-copy', 'join-shared-receiving',
    'join-first-pairing', 'join-permitted-controls', 'join-permitted-start', 'attention-issues', 'attention-history',
    ...['appearance', 'notifications', 'windows', 'updates', 'shortcuts'].map(id => `settings-${id}`),
    'dialog-server-tools', 'dialog-managed-sharing-host', 'dialog-managed-sharing-friend', 'join-consent', 'dialog-save-consent',
    ...['valheim', 'minecraft-java', 'minecraft-bedrock', 'factorio', 'terraria', 'custom-game'].map(id => `dialog-setup-${id}`),
    'tooltip-hover', 'tooltip-keyboard'
  ]) assert.ok(captures.has(expected), `capture inventory retains ${expected}`);
  const unique = new Set(plans.map(plan => `${plan.testCase.id}--${plan.variant.id}`));
  assert.equal(unique.size, plans.length, 'capture keys are unique');
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
    'npm run build', 'npx --no-install playwright install chromium', '--allow-isolated-browser-tests --hosted-runner github-actions'])
    assert.ok(source.includes(command), `workflow retains ${command}`);
  assert.ok(source.includes('if: always()'), 'failure evidence is uploaded');
  assert.ok(source.indexOf('node --test') < source.indexOf('run: node scripts/render-ui-redesign.mjs'), 'Node checks precede renderer');
});
