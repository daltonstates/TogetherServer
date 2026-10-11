// Browser automation is authorized ONLY on the explicitly selected hosted Windows runner.
// Keep this guard before imports, filesystem reads, servers, or browser side effects.
const args = process.argv.slice(2);
if (!args.includes('--allow-isolated-browser-tests') ||
    args.indexOf('--hosted-runner') < 0 || args[args.indexOf('--hosted-runner') + 1] !== 'github-actions' ||
    process.env.GITHUB_ACTIONS !== 'true' || process.env.RUNNER_OS !== 'Windows' ||
    process.env.RUNNER_ENVIRONMENT !== 'github-hosted' || process.platform !== 'win32') {
  throw new Error('Refusing browser execution: requires --allow-isolated-browser-tests --hosted-runner github-actions on an actual github-hosted Windows runner (GITHUB_ACTIONS=true, RUNNER_OS=Windows, RUNNER_ENVIRONMENT=github-hosted). Never run on the owner desktop.');
}

// Implementation below intentionally uses dynamic imports after authorization.
const { readFile, writeFile, mkdir, realpath, stat } = await import('node:fs/promises');
const { resolve, dirname, extname, relative, isAbsolute } = await import('node:path');
const { fileURLToPath } = await import('node:url');
const { createServer } = await import('node:http');
const { createRequire } = await import('node:module');
const { createHash } = await import('node:crypto');
const { createFixtureState, routeFixture, fixtureNow } = await import('./ui-redesign-fixtures.mjs');
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const output = resolve(root, 'local-data/ui-redesign-visual-review');
await mkdir(output, { recursive: true });
const manifest = {
  version: 1, evidence: 'Actual bundled React UI; synthetic responses only. No game, .NET, production storage, network or real-world acceptance.',
  authorization: { explicitFlags: args, hostedRunner: 'github-actions', runnerOS: process.env.RUNNER_OS, runId: process.env.GITHUB_RUN_ID ?? null },
  sourceAuthority: 'Current TogetherServer ui/src integrated by root before this hosted build; never the stale isolated UI.',
  build: {}, cases: [], fatalErrors: [], status: 'running',
};
const saveManifest = () => writeFile(resolve(output, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
await saveManifest();

// The inventory is explicit so a disappeared screen cannot silently reduce coverage.
const heading = name => ({ role: 'heading', name });
const region = name => ({ role: 'region', name });
const hostTabs = [
  ['overview', 'Overview', region('Server controls')], ['chat', 'Chat', heading('Server chat')],
  ['players', 'Players', heading('Players')], ['logs', 'Logs', region('Server logs')],
  ['sessions', 'Sessions', heading('Recent sessions')], ['backups', 'Backups', heading('Backups')],
  ['files', 'Settings & files', heading('Files & settings')],
  ['setup', 'Maintenance & setup', { css: '.maintenance-guide > summary', text: 'Prepare maintenance' }],
];
// Server controls is an aria-labelled div (a group screen, not an ARIA region).
hostTabs[0][2] = { css: '[aria-label="Server controls"]', text: 'Stop server' };
const pcSections = [
  ['access', 'Friend access', 'Friend access'], ['stop', 'Stop & timer', 'Stop & timer'],
  ['network', 'Connection help', 'Connection help'], ['diagnostics', 'Diagnostics', 'Preflight & diagnostics'],
  ['advanced', 'Advanced', 'Advanced'],
];
const joinSections = [
  ['play', 'Play', heading('Play')], ['chat', 'Chat', heading('Server chat')],
  ['logs', 'Logs', region('Server logs')], ['shared-saves', 'Shared saves', heading('Get a private save on this PC')],
  ['network', 'Connection help', heading('Connection help')],
  ['connection', 'Connection settings', heading('Connection settings')],
  ['pairing', 'Add a Host', heading('Add another Host')],
];
const appSections = [
  ['appearance', 'Appearance', 'Appearance'], ['notifications', 'Notifications', 'Notifications'],
  ['windows', 'Windows behavior', 'Windows behavior'], ['updates', 'Updates', 'App updates'],
  ['shortcuts', 'Keyboard shortcuts', 'Keyboard shortcuts'],
];
const cases = [
  ...hostTabs.map(([id, label, screen]) => ({ id: `host-${id}`, scenario: 'host-ready', workspace: 'Host', section: ['Selected server sections', id, label], screen })),
  ...pcSections.map(([id, label, title]) => ({ id: `host-pc-${id}`, scenario: 'host-ready', workspace: 'Host', section: ['This Host PC sections', id, label], screen: heading(title) })),
  { id: 'host-first-use', scenario: 'host-first-use', workspace: 'Host', screen: heading('Host or join a server') },
  { id: 'host-compare-all', scenario: 'host-multi', workspace: 'Host', action: 'compare', screen: { role: 'dialog', name: 'All servers' } },
  ...joinSections.map(([id, label, screen]) => ({ id: `join-${id}`, scenario: 'friend-connected', workspace: 'Join', section: ['Join sections', id, label], screen })),
  { id: 'join-first-pairing', scenario: 'friend-pairing', workspace: 'Join', screen: heading('Join a server') },
  { id: 'join-permitted-controls', scenario: 'friend-permitted', workspace: 'Join', section: ['Join sections', 'connection', 'Connection settings'], screen: heading('Connection settings'), action: 'permitted-controls' },
  { id: 'join-permitted-start', scenario: 'friend-offline', workspace: 'Join', section: ['Join sections', 'play', 'Play'], screen: heading('Play'), action: 'permitted-start' },
  { id: 'attention-issues', scenario: 'host-attention', workspace: 'Attention', screen: heading('Attention Center') },
  { id: 'attention-history', scenario: 'host-attention', workspace: 'Attention', screen: { css: '[aria-label="Activity history"] h3', text: 'Activity history' }, action: 'history' },
  ...appSections.map(([id, label, title]) => ({ id: `settings-${id}`, scenario: 'host-ready', workspace: 'App Settings', section: ['App settings sections', id, label], screen: heading(title) })),
];
const dialogs = [
  { id: 'dialog-server-tools', scenario: 'host-ready', workspace: 'Host', action: 'server-tools', screen: { role: 'dialog', name: 'Server tools' } },
  ...[
    ['capacity', 'advanced', 'Advanced', 'Edit hosting limits', 'Hosting limits'],
    ['network', 'advanced', 'Advanced', 'Edit network settings', 'Advanced network settings'],
    ['power-timer', 'stop', 'Stop & timer', 'Edit countdown', 'Empty-server countdown'],
    ['stop-safety', 'stop', 'Stop & timer', 'Review Stop safety', 'Remote Stop safety'],
    ['friend-route', 'network', 'Connection help', 'Edit connection route', 'Friend connection route'],
    ['connection-doctor', 'network', 'Connection help', 'Connection Doctor', 'Connection Doctor'],
  ].map(([id, section, label, button, title]) => ({ id: `dialog-${id}`, scenario: 'host-ready', workspace: 'Host',
    section: ['This Host PC sections', section, label], button, screen: { role: 'dialog', name: title } })),
  { id: 'dialog-setup-game-chooser', scenario: 'host-first-use', workspace: 'Host', action: 'setup', screen: heading('Choose a game') },
  ...['Valheim', 'Minecraft Java', 'Minecraft Bedrock', 'Factorio', 'Terraria', 'Custom game'].map(game => ({
    id: `dialog-setup-${game.toLowerCase().replaceAll(' ', '-')}`, scenario: 'host-first-use', workspace: 'Host', action: 'setup-game', game,
    screen: heading(game === 'Valheim' ? 'Choose a world' : 'Name this server'),
  })),
  { id: 'dialog-managed-sharing-host', scenario: 'host-shared', workspace: 'Host', section: ['Selected server sections', 'backups', 'Backups'], action: 'manage-sharing', screen: { role: 'dialog', name: 'Manage sharing' } },
  { id: 'join-consent', scenario: 'friend-shared', workspace: 'Join', section: ['Join sections', 'shared-saves', 'Shared saves'], action: 'consent', screen: heading('Get a private save on this PC') },
  { id: 'dialog-save-consent', scenario: 'friend-shared', workspace: 'Join', section: ['Join sections', 'shared-saves', 'Shared saves'], button: 'Host this save', screen: { role: 'dialog', name: 'Host this save' } },
  { id: 'dialog-managed-sharing-friend', scenario: 'friend-shared', workspace: 'Join', section: ['Join sections', 'shared-saves', 'Shared saves'], action: 'manage-sharing', screen: { role: 'dialog', name: 'Manage sharing' } },
  { id: 'tooltip-hover', scenario: 'host-ready', workspace: 'Host', action: 'tooltip-hover', screen: { role: 'tooltip', name: /.+/ } },
  { id: 'tooltip-keyboard', scenario: 'host-ready', workspace: 'Host', action: 'tooltip-keyboard', screen: { role: 'tooltip', name: /.+/ } },
];
const variants = [
  { id: 'darkdesktop', viewport: { width: 1280, height: 900 }, theme: 'dark', scale: 100 },
  { id: 'lightnarrow', viewport: { width: 390, height: 844 }, theme: 'light', scale: 100 },
];
const largeTextPages = new Set(['host-overview', 'host-players', 'host-backups', 'host-files', 'host-pc-access', 'host-pc-stop',
  'host-first-use', 'join-play', 'join-shared-saves', 'join-first-pairing', 'attention-issues', 'settings-appearance']);
const plans = [...cases, ...dialogs].flatMap(testCase => [
  ...variants.map(variant => ({ testCase, variant })),
  ...(largeTextPages.has(testCase.id) ? [{ testCase, variant: { id: 'lightnarrow150', viewport: { width: 390, height: 844 }, theme: 'light', scale: 150 } }] : []),
]);
manifest.coverage = { mainPages: cases.length, dialogAndTooltipCases: dialogs.length, variants: variants.map(item => item.id),
  text150Pages: [...largeTextPages], plannedCaptures: plans.length };
manifest.inventory = plans.map(({ testCase, variant }) => ({ id: `${testCase.id}--${variant.id}`, scenario: testCase.scenario, workspace: testCase.workspace, section: testCase.section ?? null, expectedScreen: { ...testCase.screen, name: String(testCase.screen.name ?? '') }, ...variant }));
await saveManifest();

function locatorFor(page, screen) {
  return screen.css ? page.locator(screen.css).filter({ hasText: screen.text }) : page.getByRole(screen.role, { name: screen.name, exact: typeof screen.name === 'string' });
}
async function visible(locator) { await locator.first().waitFor({ state: 'visible', timeout: 8000 }); return locator.first(); }
async function section(page, [label, value, button], record) {
  record.actions.push({ action: 'select-section', label, value, button });
  const select = page.getByRole('combobox', { name: label, exact: true });
  if (await select.isVisible()) await select.selectOption(value);
  else await page.getByRole('navigation', { name: label, exact: true }).getByRole('button', { name: button, exact: true }).click();
  // Assert the selected navigation state, separately from the screen heading.
  if (await select.isVisible()) {
    await page.waitForFunction(({ label, value }) => [...document.querySelectorAll('select')].some(el => el.getAttribute('aria-label') === label && el.value === value), { label, value });
  } else await visible(page.getByRole('navigation', { name: label, exact: true }).locator('[aria-current="page"]').filter({ hasText: button }));
}
async function click(page, name, record, exact = true) {
  record.actions.push({ action: 'click', role: 'button', name: String(name) });
  await page.getByRole('button', { name, exact }).click({ timeout: 8000 });
}
async function performActions(page, testCase, record) {
  if (testCase.section) await section(page, testCase.section, record);
  if (testCase.button) await click(page, new RegExp(testCase.button), record, false);
  switch (testCase.action) {
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
        record.actions.push({ action: 'open-disclosure', name: 'Protection & sharing' });
        await page.locator('.backups-support > summary').click();
      }
      await click(page, 'Manage sharing', record); break;
    case 'consent': {
      const consent = await visible(page.getByRole('checkbox', { name: 'Allow saves on this PC', exact: true }));
      record.actions.push({ action: 'check', name: 'Allow saves on this PC', fixtureOnly: true });
      await consent.check();
      await page.waitForFunction(() => [...document.querySelectorAll('input[type="checkbox"]')].some(el => el.checked && el.closest('label')?.textContent.includes('Allow saves on this PC')));
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
    case 'history': await page.locator('[aria-label="Activity history"]').scrollIntoViewIfNeeded(); break;
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
    const visible = el => {
      const r = el.getBoundingClientRect(), style = getComputedStyle(el);
      return r.width > 0 && r.height > 0 && style.visibility !== 'hidden' && style.display !== 'none' && !el.closest('[hidden], [inert]');
    };
    const findings = [];
    if (document.documentElement.scrollWidth > innerWidth + 2) findings.push({ kind: 'document-horizontal-overflow', width: document.documentElement.scrollWidth, viewport: innerWidth });
    for (const el of document.querySelectorAll('button,input,select,textarea,[role="tooltip"],dialog[open],h1,h2,h3,h4')) {
      if (!visible(el) || el.closest('.sr-only')) continue;
      const r = el.getBoundingClientRect(), label = (el.getAttribute('aria-label') || el.textContent || el.tagName).trim().slice(0, 120);
      if (r.left < -2 || r.right > innerWidth + 2) findings.push({ kind: 'element-outside-horizontal-viewport', tag: el.tagName, label, bounds: r.toJSON() });
      if ((el.matches('dialog[open],[role="tooltip"]')) && (r.top < -2 || r.bottom > innerHeight + 2)) findings.push({ kind: 'overlay-outside-viewport', label, bounds: r.toJSON() });
      if (el.matches('button') && el.scrollWidth > el.clientWidth + 3) findings.push({ kind: 'possible-clipped-button-text', label, scrollWidth: el.scrollWidth, clientWidth: el.clientWidth });
    }
    return findings;
  });
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
  for (const { testCase, variant } of plans) {
    const record = { id: `${testCase.id}--${variant.id}`, scenario: testCase.scenario, workspace: testCase.workspace,
      viewport: variant.viewport, theme: variant.theme, scale: variant.scale, status: 'running',
      actions: [], assertions: [], skippedSelectors: [], failedSelectors: [], jsErrors: [], consoleErrors: [],
      unhandledFixtures: [], deliberateFailures: [], blockedNetwork: [], failedRequests: [], httpFailures: [], layoutFindings: [], screenshots: [], apiRequests: [] };
    manifest.cases.push(record);
    let context;
    try {
      const state = createFixtureState(testCase.scenario);
      context = await browser.newContext({ viewport: variant.viewport, colorScheme: variant.theme, deviceScaleFactor: 1, serviceWorkers: 'block', acceptDownloads: false, locale: 'en-US', timezoneId: 'UTC', reducedMotion: 'reduce' });
      const pending = new Set(), deliberatelyFailedURLs = new Set();
      await context.route('**/*', async route => {
        const request = route.request(), url = new URL(request.url());
        if (url.origin !== origin) {
          record.blockedNetwork.push({ url: request.url(), method: request.method() });
          await route.abort('blockedbyclient'); return;
        }
        if (url.pathname.startsWith('/api/local/')) {
          const token = {}; pending.add(token);
          try {
            let body;
            try { body = request.postDataJSON(); } catch { body = request.postData(); }
            const fixture = routeFixture(state, { method: request.method(), path: `${url.pathname}${url.search}`, body });
            record.apiRequests.push({ method: request.method(), path: `${url.pathname}${url.search}`, status: fixture?.status ?? 501 });
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
          } finally { pending.delete(token); }
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
      page.on('requestfailed', request => record.failedRequests.push({ url: request.url(), error: request.failure()?.errorText }));
      page.on('response', response => {
        if (response.status() >= 400 && !deliberatelyFailedURLs.has(response.url())) record.httpFailures.push({ url: response.url(), status: response.status() });
      });
      await page.addInitScript(({ theme, scale, now }) => {
        // Fixed synthetic observation time; keep real timers for visible-state waits.
        const RealDate = Date, fixed = RealDate.parse(now);
        globalThis.Date = class extends RealDate { constructor(...values) { super(...(values.length ? values : [fixed])); } static now() { return fixed; } };
        let fakeId = 0;
        Object.defineProperty(globalThis.crypto, 'randomUUID', { value: () => `aaaaaaaa-aaaa-4aaa-8aaa-${String(++fakeId).padStart(12, '0')}` });
        localStorage.setItem('togetherserver.ui-preferences.v1', JSON.stringify({ theme, density: 'comfortable', textScale: scale, highContrast: false, quietMode: false, selectedServer: '', favoriteServers: [], favoriteHosts: [], serverOrder: [], serverTabs: {} }));
      }, { ...variant, now: fixtureNow });
      await page.goto(origin, { waitUntil: 'domcontentloaded' });
      await visible(page.getByRole('navigation', { name: 'TogetherServer workspaces', exact: true }));
      // Navigation exists before the snapshot loads. Wait for a real mode workspace before clicking it.
      await visible(page.locator('section.context-workspace'));
      record.actions.push({ action: 'navigate-workspace', name: testCase.workspace });
      await page.getByRole('navigation', { name: 'TogetherServer workspaces', exact: true }).getByRole('button', { name: testCase.workspace, exact: true }).click();
      await visible(page.getByRole('navigation', { name: 'TogetherServer workspaces', exact: true }).getByRole('button', { name: testCase.workspace, exact: true }).and(page.locator('[aria-current="page"]')));
      await performActions(page, testCase, record);
      const screen = await visible(locatorFor(page, testCase.screen));
      record.assertions.push({ correctNamedScreen: { ...testCase.screen, name: String(testCase.screen.name ?? '') } });
      const hasDialog = await page.locator('dialog[open]').count() > 0;
      if (hasDialog && !record.dialogInitialFocus) record.dialogInitialFocus = await focusDetails(page);
      if (!testCase.action?.startsWith('tooltip')) {
        record.actions.push({ action: 'scroll-asserted-screen-into-view' });
        await screen.scrollIntoViewIfNeeded();
      }
      // Wait for UI fetches/fonts/paint using signals, never arbitrary sleep.
      await page.waitForLoadState('networkidle', { timeout: 8000 });
      if (pending.size) throw new Error('Synthetic API responses still pending at capture');
      await page.evaluate(async () => { await document.fonts.ready; await new Promise(resolveFrame => requestAnimationFrame(() => requestAnimationFrame(resolveFrame))); });
      await visible(screen);
      record.screenBounds = await screen.boundingBox();
      if (!record.screenBounds || record.screenBounds.y >= variant.viewport.height || record.screenBounds.y + record.screenBounds.height <= 0) {
        throw new Error('The named screen is outside the capture viewport');
      }
      if (hasDialog && !record.dialogInitialFocus.insideDialog) record.layoutFindings.push({ kind: 'dialog-initial-focus-outside', ...record.dialogInitialFocus });
      if (hasDialog && record.dialogInitialFocus.closeControl) record.layoutFindings.push({ kind: 'dialog-initial-focus-on-close', ...record.dialogInitialFocus });
      record.layoutFindings.push(...await layoutFindings(page));
      const filename = `${record.id}.png`;
      await page.screenshot({ path: resolve(output, filename), animations: 'disabled' });
      record.screenshots.push({ file: filename, kind: 'viewport' });
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
      await writeFile(resolve(output, `${record.id}.json`), `${JSON.stringify(record, null, 2)}\n`);
      await saveManifest();
      console.log(`${record.status}: ${record.id}`);
    }
  }
} catch (error) {
  manifest.fatalErrors.push(error.stack ?? String(error));
} finally {
  if (browser) await browser.close();
  if (server) await new Promise(resolveClose => server.close(resolveClose));
  manifest.summary = { planned: plans.length, attempted: manifest.cases.length, screenshots: manifest.cases.reduce((count, entry) => count + entry.screenshots.length, 0),
    failed: manifest.cases.filter(entry => entry.status === 'failed').length, layoutFindings: manifest.cases.reduce((count, entry) => count + entry.layoutFindings.length, 0),
    intentionalErrorCases: manifest.cases.filter(entry => entry.deliberateFailures.length).length, skipped: plans.length - manifest.cases.length };
  const attempted = new Set(manifest.cases.map(entry => entry.id));
  manifest.skippedCases = manifest.inventory.filter(entry => !attempted.has(entry.id)).map(entry => ({ ...entry, status: 'skipped', reason: 'Run ended before this case; see fatalErrors. No screen assertion or capture performed.' }));
  manifest.status = manifest.fatalErrors.length || manifest.summary.failed || manifest.summary.skipped ? 'failed' : manifest.summary.layoutFindings ? 'captured-with-layout-findings' : 'captured-awaiting-visual-inspection';
  await saveManifest();
  if (manifest.status === 'failed') process.exitCode = 1;
}
