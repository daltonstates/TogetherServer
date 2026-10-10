import assert from 'node:assert/strict';
import { readFile, readdir, stat, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import vm from 'node:vm';
import { gunzipSync } from 'node:zlib';
import { verifySharedModel, verifySharedInteractions, verifyTooltip } from './verify-shared-flow.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const repository = path.resolve(directory,'../..');
const globalOnly = process.argv.includes('--global-only');
const require = createRequire(path.join(repository,'ui/package.json'));
const { JSDOM, VirtualConsole } = require('jsdom');
const fragment = await readFile(path.join(directory,'together-ui-review.html'),'utf8');
const offline = await readFile(path.join(directory,'index.html'),'utf8');
const packed = JSON.parse(fragment.match(/<script type="application\/json" id="ts-ui-atlas-data">([\s\S]*?)<\/script>/)[1]);
assert.equal(packed.format,'gzip-base64');
const catalog = JSON.parse(gunzipSync(Buffer.from(packed.data,'base64')).toString('utf8'));
const entries = [...catalog.pages,...catalog.dialogs];
const ids = new Set(entries.map(entry => entry.id));
const results = [];
const passed = (name, detail = '') => results.push({name,status:'PASS',detail});

assert(!/<(?:!doctype|html|head|body)[\s>]/i.test(fragment),'Inline output must be a fragment.');
assert(Buffer.byteLength(fragment) < 1_000_000,'Inline review must remain under 1 MB.');
assert(!fragment.includes('@@ATLAS_DATA@@'),'Unexpanded catalog marker.');
assert(offline.includes("connect-src 'none'"),'Offline preview must block network connections.');
const script = fragment.match(/<script>\s*([\s\S]*?)<\/script>/)[1];
new vm.Script(script);
assert(!/\b(?:fetch|XMLHttpRequest|WebSocket|EventSource)\s*\(/.test(script),'Mockups must not call a network API.');
assert(!/\b(?:localStorage|sessionStorage)\b|navigator\.clipboard|window\.open\s*\(|https?:\/\//.test(script),'Mockups must stay memory-only and cannot dispatch external actions.');
passed('Isolated self-contained output',`${Buffer.byteLength(fragment)} bytes; no network, app, storage or clipboard action.`);

let links = 0;
for (const entry of entries) {
  for (const html of [entry.html,entry.footer || '',...Object.values(entry.variants || {})]) {
    assert(!/<script\b|\bon\w+\s*=|\b(?:src|href)\s*=\s*["'](?:https?:|\/api\/)/i.test(html),`Side effect in ${entry.id}`);
    const document = new JSDOM(`<div>${html}</div>`).window.document;
    for (const control of document.querySelectorAll('[data-dialog],[data-page]')) {
      const target = control.dataset.dialog || control.dataset.page;
      if (!globalOnly) assert(ids.has(target),`${entry.id} references missing view ${target}`);
      links++;
    }
    for (const button of document.querySelectorAll('.ts-iconbutton')) {
      assert(button.getAttribute('aria-label')?.trim(),`Missing accessible name in ${entry.id}`);
      assert(button.getAttribute('data-tooltip')?.trim(),`Missing icon tooltip in ${entry.id}`);
    }
    assert(!document.querySelector('input[type="file"]'),`Native file input is active in ${entry.id}`);
    for (const node of document.querySelectorAll('[data-icon]')) assert(catalog.icons[node.dataset.icon],`Unknown icon ${node.dataset.icon} in ${entry.id}`);
    document.defaultView.close();
  }
  for (const key of Object.keys(entry.variants || {})) assert(entry.states.includes(key),`Unreachable variant ${entry.id}:${key}`);
}
passed('Navigation and icon vocabulary',`${links} cross-view links; named icon controls and selectable variants.`);

const expectedPages = ['host-first-use','host-all-servers','host-overview','host-chat','host-players','host-logs','host-sessions','host-backups','host-files','host-maintenance','host-pc-access','host-pc-stop','host-pc-connection','host-pc-diagnostics','host-pc-advanced','join-pairing','join-play','join-chat','join-logs','join-shared','join-help','join-settings','attention-current','attention-history','app-appearance','app-notifications','app-windows','app-updates','app-shortcuts'];
if (!globalOnly) for (const id of expectedPages) assert(catalog.pages.some(page => page.id === id),`Missing planned page ${id}`);
const sourceTexts = new Map();
for (const row of catalog.coverage) {
  assert(ids.has(row.target),`Missing coverage target ${row.target}`);
  const match = /^((?:ui\/src|src\/TogetherServer)\/.+):(\d+)$/.exec(row.source);
  assert(match,`Malformed source location ${row.source}`);
  const [,relative,line] = match;
  if (!sourceTexts.has(relative)) sourceTexts.set(relative,(await readFile(path.join(repository,relative),'utf8')).split(/\r?\n/));
  assert(Number(line) <= sourceTexts.get(relative).length,`Source line past EOF: ${row.source}`);
  assert(['retained','reframed','merged'].includes(row.decision),`Missing treatment ${row.source}`);
}
const walk = async directoryPath => {
  const output = [];
  for (const entry of await readdir(directoryPath,{withFileTypes:true})) {
    const target = path.join(directoryPath,entry.name);
    if (entry.isDirectory()) output.push(...await walk(target));
    else if (/\.tsx?$/.test(entry.name) && !/\.test\./.test(entry.name)) output.push(target);
  }
  return output;
};
const confirmations = [];
for (const filename of await walk(path.join(repository,'ui/src'))) {
  const lines = (await readFile(filename,'utf8')).split(/\r?\n/);
  lines.forEach((line,index) => { if (/window\.confirm\s*\(/.test(line)) confirmations.push(`${path.relative(repository,filename).replaceAll('\\','/')}:${index + 1}`); });
}
if (!globalOnly) for (const source of confirmations) assert(catalog.coverage.some(row => row.source === source && row.kind === 'confirmation'),`No designed confirmation for ${source}`);
passed('Source coverage',`${catalog.pages.length} pages, ${catalog.dialogs.length} dialogs/confirmations, ${catalog.coverage.length} mappings; ${confirmations.length} browser-confirmation call sites inventoried.`);

const errors = [];
const virtualConsole = new VirtualConsole();
virtualConsole.on('jsdomError',error => errors.push(error));
const dom = new JSDOM(fragment,{runScripts:'dangerously',pretendToBeVisual:true,url:'https://ui-review.invalid/',virtualConsole,beforeParse(window) { window.Blob = globalThis.Blob; window.Response = globalThis.Response; window.DecompressionStream = globalThis.DecompressionStream; }});
const document = dom.window.document;
const root = document.getElementById('ts-ui-atlas');
for (let attempt = 0; !root.dataset.ready && attempt < 300; attempt++) await new Promise(resolve => setTimeout(resolve,10));
assert.equal(root.dataset.ready,'true','Compressed inline catalog did not initialize.');
const change = (selector,value) => { const control = root.querySelector(selector); assert(control,`Missing control ${selector}`); control.value = value; control.dispatchEvent(new dom.window.Event('change',{bubbles:true})); };
const click = selector => { const control = root.querySelector(selector); assert(control,`Missing click control ${selector}`); control.click(); return control; };
const choose = id => change('[data-review="screen"]',id);
let renderedStates = 0;
for (const entry of entries) {
  choose(entry.id);
  const marker = catalog.pages.some(page => page.id === entry.id) ? '[data-current-page]' : '[data-current-dialog]';
  assert.equal(root.querySelector(marker)?.dataset[marker.includes('page') ? 'currentPage' : 'currentDialog'],entry.id,`Wrong selected view ${entry.id}`);
  for (const state of entry.states) {
    change('[data-review="state"]',state);
    assert.equal(root.querySelector('[data-review="state"]').value,state);
    assert(root.querySelector(marker)?.textContent.trim(),`Blank view ${entry.id}:${state}`);
    for (const element of root.querySelectorAll(`${marker} .ts-iconbutton`)) assert(element.getAttribute('aria-label'),`Unnamed runtime action ${entry.id}:${state}`);
    for (const element of root.querySelectorAll(`${marker} [data-primary-action]`)) assert(element.textContent.trim(),`Hidden primary action label ${entry.id}:${state}`);
    if (marker.includes('dialog') && state === 'loading') assert([...root.querySelectorAll('.ts-overlay button,.ts-overlay input,.ts-overlay select,.ts-overlay textarea')].every(control => control.disabled),`Busy action escaped its guard in ${entry.id}`);
    renderedStates++;
  }
}
assert.equal(errors.length,0,errors.map(error => error.message).join('\n'));
passed('Every view and state renders',`${renderedStates} states; zero JSDOM script/style errors.`);

if (!globalOnly) {
  choose('join-shared'); change('[data-review="state"]','normal'); change('[data-review="client"]','original');
  choose('host-overview'); click('[data-workspace="app"]');
  const fromHost = root.querySelector('.ts-main').innerHTML;
  choose('join-play'); click('[data-workspace="app"]');
  assert.equal(root.querySelector('.ts-main').innerHTML,fromHost,'Global Settings differs by entry mode.');
  assert(!root.querySelector('.ts-main').textContent.includes('Keep Windows awake'),'Hosting power leaked into global Settings.');
  passed('Mode-independent App Settings');
  choose('host-overview'); change('[data-review="state"]','staging');
  assert(root.querySelector('.ts-brand-name').textContent.includes('DEVELOPMENT'));
  assert(root.querySelector('.ts-staging-banner').textContent.includes('Isolated test data'));
  passed('Unmistakable staging identity');

  choose('host-overview'); change('[data-review="state"]','unknown');
  assert.equal(root.querySelector('[aria-label="Stop server"]').disabled,false,'Owner Stop must remain reviewable for Unknown occupancy.');
  assert(!root.querySelector('.ts-server-command').textContent.includes('Auto-stop in'),'Unknown occupancy retained a countdown.');
  click('[aria-label="Stop server"]');
  assert.equal(root.querySelector('[data-current-dialog]').dataset.currentDialog,'confirm-stop');
  click('[data-close]');
  assert.equal(document.activeElement.getAttribute('aria-label'),'Stop server','Dialog did not return focus.');
  choose('join-play'); change('[data-review="state"]','unknown');
  assert(root.querySelector('[aria-label="Stop server"]').disabled,'Friend Stop allowed Unknown occupancy.');
  change('[data-review="state"]','positive');
  assert(root.querySelector('[aria-label="Stop server"]').disabled,'Friend Stop allowed online players.');
  choose('host-overview'); change('[data-review="state"]','blocked');
  assert(root.querySelector('[aria-label="Stop server"]').disabled,'Unresolved Host action was not blocked.');
  passed('Occupancy and process-review boundaries');

  choose('invite-code');
  const mask = root.querySelector('[data-secret]').textContent;
  click('[data-action="copy"]');
  assert.equal(root.querySelector('[data-secret]').textContent,mask,'Copy revealed the secret.');
  click('[data-action="reveal"]');
  assert.notEqual(root.querySelector('[data-secret]').textContent,mask,'Reveal did not show the synthetic value.');
  dom.window.dispatchEvent(new dom.window.Event('blur'));
  assert.equal(root.querySelector('[data-secret]').textContent,mask,'Focus loss retained the revealed value.');
  passed('Independent copy/reveal and focus-loss masking');

  choose('host-overview');
  const ip = root.querySelector('[data-action="reveal"][data-value="192.0.2.40:2456"]').closest('dd').querySelector('[data-secret]');
  const password = root.querySelector('[data-action="reveal"][data-value="demo-game-password"]').closest('dd').querySelector('[data-secret]');
  const ipMask = ip.textContent, passwordMask = password.textContent;
  click('[data-action="copy"][data-value="demo-game-password"]');
  assert.equal(password.textContent,passwordMask,'Copy exposed the separate password.');
  click('[data-action="reveal"][data-value="demo-game-password"]');
  assert.equal(password.textContent,'demo-game-password'); assert.equal(ip.textContent,ipMask,'Password reveal exposed the IP.');
  click('[data-action="reveal"][data-value="192.0.2.40:2456"]');
  assert.equal(ip.textContent,'192.0.2.40:2456'); assert.equal(password.textContent,passwordMask);
  choose('join-play'); click('[data-action="reveal"][data-value="192.0.2.10:2456"]');
  assert(root.querySelector('[data-revealed]')?.textContent.includes('192.0.2.10'),'Join address reveal did not honor its data-value.');
  passed('Correct independent Host and Join reveal targets');

  choose('host-overview'); change('[data-server-selector]','Preview server');
  assert(root.querySelector('.ts-server-command').textContent.includes('Listening'));
  assert(root.querySelector('.ts-server-command').textContent.includes('Unknown players'));
  assert(!root.querySelector('.ts-server-command').textContent.includes('Auto-stop in'));
  choose('host-players'); assert(root.querySelector('.ts-server-command').textContent.includes('Unknown players'));
  change('[data-server-selector]','Alderheim');
  choose('join-play'); change('[data-review="state"]','expired');
  assert(root.querySelector('.ts-server-command [data-action="open-game"]').disabled,'Expired access retained an Open game action.');
  change('[data-review="state"]','starting');
  assert(root.querySelector('.ts-server-command [data-action="open-game"]').disabled,'Starting was promoted to a game-open opportunity.');
  passed('Terraria selection and game-open gating');

  choose('invite-code'); click('[data-action="copy"]');
  assert(root.querySelector('.ts-dialog-body [role="status"]')?.textContent.startsWith('Preview:'),'Modal feedback was hidden in the inert background.');
  choose('tool-backup-restore');
  const guarded = root.querySelector('[data-requires-check]');
  assert(guarded,'Restore review lacks acknowledgement.'); assert(guarded.disabled,'Restore initially bypassed acknowledgement.');
  const acknowledgement = root.querySelector('[data-ack]'); assert(acknowledgement,'Restore lacks the acknowledgement control.');
  acknowledgement.checked = true; acknowledgement.dispatchEvent(new dom.window.Event('change',{bubbles:true}));
  assert.equal(guarded.disabled,false,'Acknowledged Restore could not proceed in preview.');
  passed('Visible modal receipts and required acknowledgements');

  choose('app-notifications');
  assert.equal(root.querySelectorAll('.ts-content [data-event-kind]').length,13,'PC notification event choices were lost.');
  choose('server-notifications');
  assert.equal(root.querySelectorAll('.ts-dialog-body [data-event-kind]').length,12,'Server override choices were lost.');
  assert(!root.querySelector('.ts-dialog-body [data-event-kind="Update"]'),'App updates leaked into server overrides.');
  passed('Complete PC and contextual notification choices');

  choose('setup-game');
  const game = root.querySelector('[data-dialog="setup-world"][data-variant="java"]');
  assert(game,'Missing Java game selection.'); game.click();
  assert.equal(root.querySelector('[data-review="state"]').value,'java','Game selection did not choose its World variant.');
  const next = root.querySelector('[data-dialog="setup-app"]');
  assert(next,'World step has no next action.'); next.click();
  assert.equal(root.querySelector('[data-review="state"]').value,'java','World-to-app step lost the selected game.');
  passed('Game-specific setup navigation');
  verifySharedModel(passed);
  await verifySharedInteractions({root,document,dom,change,click,choose,passed});
  choose('app-appearance'); verifyTooltip({root,dom,passed});
}

choose('app-appearance');
const editable = root.querySelector('.ts-content select');
editable.value = 'light'; editable.dispatchEvent(new dom.window.Event('change',{bubbles:true}));
assert.equal(root.dataset.theme,'light','Appearance change did not affect preview.');
change('[data-review="scale"]','150'); assert.equal(root.style.getPropertyValue('--ts-scale'),'1.5');
change('[data-review="width"]','narrow'); assert.equal(root.querySelector('.ts-preview-stage').dataset.width,'narrow');
click('[data-dialog="command-palette"]');
const search = root.querySelector('[data-command-search]'); search.value = 'appearance'; search.dispatchEvent(new dom.window.Event('input',{bubbles:true}));
const visibleCommands = [...root.querySelectorAll('[data-command-list] button')].filter(button => !button.hidden);
assert.equal(visibleCommands.length,1,'Command filtering did not narrow the selection.');
choose('update-review');
change('[data-review="state"]','loading'); root.dispatchEvent(new dom.window.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
assert(root.querySelector('[data-current-dialog]'),'Busy dialog was dismissed.');
change('[data-review="state"]','normal'); root.dispatchEvent(new dom.window.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
assert(!root.querySelector('[data-current-dialog]'),'Escape did not close the dialog.');
root.dispatchEvent(new dom.window.KeyboardEvent('keydown',{key:'k',ctrlKey:true,bubbles:true}));
assert.equal(root.querySelector('[data-current-dialog]').dataset.currentDialog,'command-palette');
passed('Review controls, command filtering, shortcuts and busy dismissal');

choose('native-runtime-fallback');
const modalRefresh = root.querySelector('.ts-dialog-footer [data-action="refresh"]'); modalRefresh.focus(); modalRefresh.click();
root.dispatchEvent(new dom.window.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
assert(root.querySelector('[data-current-dialog]'),'Pending modal action was dismissed.');
await new Promise(resolve => setTimeout(resolve,750));
assert.equal(document.activeElement.dataset.action,'refresh','Pending modal update lost focus.');
assert(root.querySelector('.ts-dialog-body [role="status"]'),'Pending modal result did not stay in the dialog.');
passed('Modal pending focus and feedback');

choose('app-updates'); click('.ts-content [data-action="refresh"]');
assert(root.querySelector('.ts-content [data-action="refresh"]').disabled,'Refresh lacks immediate pending feedback.');
choose('app-appearance');
await new Promise(resolve => setTimeout(resolve,750));
assert(!root.querySelector('.ts-toast'),'A late result overwrote the newer page.');
passed('Pending feedback and stale-result cancellation');

choose('app-appearance');
const density = root.querySelector('[data-appearance="density"]');
density.value = 'comfortable'; density.dispatchEvent(new dom.window.Event('change',{bubbles:true}));
choose('app-windows'); choose('app-appearance');
assert.equal(root.querySelector('[data-appearance="density"]').value,'comfortable','Local draft did not survive navigation.');
passed('Preview edits survive local navigation');

assert.equal(errors.length,0,errors.map(error => error.message).join('\n'));
dom.window.close();
const offlineErrors = [];
const offlineConsole = new VirtualConsole(); offlineConsole.on('jsdomError',error => offlineErrors.push(error));
const offlineDom = new JSDOM(offline,{runScripts:'dangerously',pretendToBeVisual:true,url:'file:///together-ui-review.html',virtualConsole:offlineConsole,beforeParse(window) { window.Blob = globalThis.Blob; window.Response = globalThis.Response; window.DecompressionStream = globalThis.DecompressionStream; }});
const offlineRoot = offlineDom.window.document.getElementById('ts-ui-atlas');
for (let attempt = 0; !offlineRoot.dataset.ready && attempt < 300; attempt++) await new Promise(resolve => setTimeout(resolve,10));
assert.equal(offlineRoot.dataset.ready,'true','Offline export did not initialize.');
const hoverTarget = offlineRoot.querySelector('[data-ts-tooltip]'); hoverTarget.dispatchEvent(new offlineDom.window.Event('pointerover',{bubbles:true}));
assert.equal(offlineRoot.querySelector('.ts-tooltip').hidden,false,'Offline tooltip did not open.');
assert.equal(offlineRoot.querySelector('.ts-tooltip').textContent,hoverTarget.dataset.tsTooltip);
const offlinePicker = offlineRoot.querySelector('[data-review="screen"]'); offlinePicker.value = 'command-palette'; offlinePicker.dispatchEvent(new offlineDom.window.Event('change',{bubbles:true}));
assert.equal(offlineRoot.querySelector('[data-current-dialog]').dataset.currentDialog,'command-palette');
assert.equal(offlineErrors.length,0,offlineErrors.map(error => error.message).join('\n'));
offlineDom.window.close();
passed('Offline boot, tooltips and dialog navigation');
const report = {scope:'Synthetic mockup atlas only',generatedAt:new Date().toISOString(),globalOnly,pages:catalog.pages.length,dialogs:catalog.dialogs.length,sourceMappings:catalog.coverage.length,confirmationCallSites:confirmations.length,renderedStates,results,skipped:['Actual browser pixel/layout review on an approved separate session','Native application and game acceptance; application code has not changed']};
await writeFile(path.join(directory,'VALIDATION.json'),JSON.stringify(report,null,2)+'\n','utf8');
console.log(`PASS ${results.length} atlas checks; ${renderedStates} rendered states. Browser/native checks unrun.`);
