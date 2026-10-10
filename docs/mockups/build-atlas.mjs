import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import globalCatalog from './catalog-global.mjs';
import nativeCatalog from './catalog-native.mjs';
import { sharedCatalog, sharedStates, createSharedFlow } from './shared-flow.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const repository = path.resolve(directory, '../..');
const globalOnly = process.argv.includes('--global-only');
const extras = {
  pages: [],
  dialogs: [
    {
      id:'join-host-picker',title:'Saved Hosts',area:'join',description:'Find a saved Host, choose its assigned server or manage this connection.',states:['normal','empty','loading','error'],emptyTitle:'No saved Hosts',emptyText:'Use a private server code to connect to a Host.',
      html:`<label class="ts-field"><span>Find Host</span><input type="search" placeholder="Host name, server or game" data-filter="hosts"></label><div class="ts-stack"><section class="ts-panel" data-filter-item><div class="ts-row"><div><strong>Friend Host A</strong><small>Alderheim · Valheim</small></div><div class="ts-actions"><button type="button" class="ts-iconbutton" data-action="pin" data-icon="star" aria-label="Pin Friend Host A" data-tooltip="Pin Host"></button><button type="button" class="ts-button ts-primary" data-primary-action data-select-host="Friend Host A" data-assigned-server="Alderheim" data-icon="chevronRight">Select Host</button></div></div></section><section class="ts-panel" data-filter-item><div class="ts-row"><div><strong>Friend Host B</strong><small>MapleCraft · Minecraft Java</small></div><div class="ts-actions"><button type="button" class="ts-iconbutton" data-action="pin" data-icon="star" aria-label="Pin Friend Host B" data-tooltip="Pin Host"></button><button type="button" class="ts-button" data-primary-action data-select-host="Friend Host B" data-assigned-server="MapleCraft" data-icon="chevronRight">Select Host</button></div></div></section></div>`,
      footer:'<button type="button" class="ts-button" data-page="join-pairing">Add Host</button><button type="button" class="ts-button" data-page="join-settings">Manage selected Host</button><button type="button" class="ts-button" data-close>Done</button>'
    },
    {
      id:'server-picker', title:'Saved servers', area:'host', description:'Find, pin or reorder a saved server.', states:['normal','empty','loading','error'],
      html:`<label class="ts-field"><span>Find server</span><input type="search" placeholder="Name, game, or world" data-filter="servers"></label><div class="ts-stack"><section class="ts-panel" data-filter-item><div class="ts-row"><div><strong>Alderheim</strong><small>Valheim · Ready · 0 players</small></div><div class="ts-actions"><button type="button" class="ts-iconbutton" data-action="pin" data-icon="star" aria-label="Pin Alderheim" data-tooltip="Pin server"></button><button type="button" class="ts-iconbutton" data-select-server="Alderheim" data-icon="chevronRight" aria-label="Open Alderheim" data-tooltip="Open server"></button></div></div></section><section class="ts-panel" data-filter-item><div class="ts-row"><div><strong>MapleCraft</strong><small>Minecraft Java · Offline</small></div><div class="ts-actions"><button type="button" class="ts-iconbutton" data-action="pin" data-icon="star" aria-label="Pin MapleCraft" data-tooltip="Pin server"></button><button type="button" class="ts-iconbutton" data-select-server="MapleCraft" data-icon="chevronRight" aria-label="Open MapleCraft" data-tooltip="Open server"></button></div></div></section></div><details class="ts-details"><summary>Reorder saved servers</summary><div class="ts-stack"><label class="ts-field"><span>First server</span><select><option>Alderheim</option><option>MapleCraft</option></select></label><button type="button" class="ts-button" data-action="save">Save order</button></div></details>`,
      footer:`<button type="button" class="ts-button" data-page="host-all-servers">Compare all servers</button><button type="button" class="ts-button" data-close>Done</button>`
    },
    {
      id:'server-actions', title:'More server actions', area:'host', description:'Actions for the selected Host server.', states:['normal','blocked','loading'],
      html:`<div class="ts-command-list"><button type="button" class="ts-button" data-dialog="confirm-restart" data-icon="refresh">Restart</button><button type="button" class="ts-button" data-dialog="confirm-safe-restart" data-icon="archive">Safe restart with checkpoint</button><button type="button" class="ts-button" data-dialog="setup-game" data-icon="settings">Edit server setup</button><button type="button" class="ts-button" data-action="refresh" data-icon="check">Check server health</button><button type="button" class="ts-button" data-page="host-pc-access" data-icon="users">Friend access</button><button type="button" class="ts-button" data-dialog="server-notifications" data-icon="bell">Server notifications</button></div>`,
      footer:`<button type="button" class="ts-button" data-close>Done</button>`
    }
  ],
  coverage: [
    {source:'ui/src/main.tsx:1980',label:'Saved server search, pins and ordering',kind:'flow',target:'server-picker',decision:'reframed',note:'Searchable picker replaces the permanent saved-server column.'},
    {source:'ui/src/main.tsx:2170',label:'More Host server actions',kind:'flow',target:'server-actions',decision:'reframed',note:'Supporting actions use one compact secondary menu.'},
    {source:'ui/src/main.tsx:1835',label:'Friend server tools',kind:'flow',target:'join-tools',decision:'reframed',note:'Operational tools stay inside Join.'}
  ]
};

const catalogs = [globalCatalog, nativeCatalog, extras, sharedCatalog];
extras.dialogs.find(dialog => dialog.id === 'server-actions').html += '<button type="button" class="ts-button" data-primary-action data-dialog="host-overview-details" data-icon="check">Server details and checks</button>';
if (!globalOnly) {
  for (const name of ['catalog-host.json','catalog-join.json','catalog-access.json']) {
    catalogs.push(JSON.parse(await readFile(path.join(directory,name),'utf8')));
  }
}
const catalog = { schema:1, pages:catalogs.flatMap(part => part.pages || []), dialogs:catalogs.flatMap(part => part.dialogs || []), coverage:catalogs.flatMap(part => part.coverage || []) };
const sharedPage = catalog.pages.find(page => page.id === 'join-shared');
if (sharedPage) {
  sharedPage.description = 'Get the save, prepare hosting, then choose Start.';
  sharedPage.html = '<section data-shared-landing><h3>Get save → Prepare hosting → Start server</h3></section>';
  sharedPage.states = [...new Set([...sharedPage.states.map(state => state === 'default' ? 'normal' : state),...sharedStates])];
  sharedPage.variants = {};
}
const coverageMoves = {
  'ui/src/main.tsx:1811':['join-host-picker','Saved Host search and pins live in the searchable picker above the section sidebar.'],
  'ui/src/ServerChat.tsx:644':['join-chat','The selected room and full notice are available in Chat; Play keeps only notices requiring immediate action.'],
  'ui/src/main.tsx:2166':['host-backup-tools','Protection evidence and supporting tools open from Backups; create/restore remain primary.'],
  'ui/src/BackupCatalog.tsx:257':['host-backups','Name, pins, kind and local dates remain reachable in the collapsed backup filters.'],
  'ui/src/ServerFilesPanel.tsx:297':['host-file-folders','Saved folders remain available from the named Server folders dialog.'],
  'ui/src/main.tsx:2180':['server-actions','Supporting actions stay in More; detailed server evidence opens a separate view.']
};
for (const row of catalog.coverage) if (coverageMoves[row.source] && ['join-play','host-backups','host-files','host-overview'].includes(row.target)) { [row.target,row.note] = coverageMoves[row.source]; row.decision = 'reframed'; }
const aliases = {
  'global-draft':'global-draft-recovery', 'join-server-actions':'join-tools',
  'confirm-archive-run':'confirm-archive-session', 'confirm-file-reload':'confirm-reload-file',
  'confirm-file-discard':'confirm-discard-file', 'confirm-config-create':'confirm-create-configuration',
  'confirm-file-undo':'confirm-restore-file-version', 'confirm-setup-restore':'confirm-restore-setup',
  'confirm-settings-reload':'confirm-reload-game-settings', 'confirm-settings-discard':'confirm-discard-game-settings',
  'confirm-list-reload':'confirm-reload-player-list', 'confirm-list-discard':'confirm-discard-player-list',
  'confirm-addons-disable':'confirm-addon-disable', 'confirm-addons-enable':'confirm-addon-enable',
  'confirm-addons-import':'confirm-addon-import-factorio', 'confirm-addons-undo':'confirm-addon-undo',
  'confirm-addons-review':'confirm-addon-version-review'
};
const rewriteTargets = html => String(html || '').replace(/data-(dialog|page)=(["'])([^"']+)\2/g,(match,kind,quote,target) => aliases[target] ? `data-${kind}=${quote}${aliases[target]}${quote}` : match);
for (const entry of [...catalog.pages,...catalog.dialogs]) {
  entry.html = rewriteTargets(entry.html); entry.footer = rewriteTargets(entry.footer);
  if (entry.variants) entry.variants = Object.fromEntries(Object.entries(entry.variants).map(([state,html]) => [state,rewriteTargets(html)]));
  if (entry.id === 'tool-addons' && entry.variants?.bedrock) entry.variants.bedrock = entry.variants.bedrock.replaceAll('confirm-addon-import-factorio','confirm-addon-import-bedrock');
  if (entry.id === 'join-tools') { entry.html = entry.html.replace('12:40','08:42'); if (entry.variants?.default) entry.variants.default = entry.variants.default.replace('12:40','08:42'); }
  if (['host-overview','join-play'].includes(entry.id)) { entry.states.push('staging'); entry.variants.staging = entry.html; }
}
if (!globalOnly) catalog.coverage.push({source:'ui/src/main.tsx:1740',label:'Development/staging identity banner',kind:'flow',target:'host-overview',decision:'retained',note:'Staging state keeps unmistakable isolated-instance identity across the shared shell.'});

const iconSource = await readFile(path.join(repository,'ui/src/Icon.tsx'),'utf8');
catalog.icons = {};
for (const match of iconSource.matchAll(/^  (\w+): (.+),?\r?$/gm)) {
  catalog.icons[match[1]] = match[2].replace(/,$/,'').replaceAll('<>','').replaceAll('</>','');
}
Object.assign(catalog.icons, {
  star:'<path d="m10 2 2.5 5 5.5.8-4 4 .9 5.5-4.9-2.6-4.9 2.6.9-5.5-4-4L7.5 7Z"/>',
  more:'<circle cx="4" cy="10" r="1"/><circle cx="10" cy="10" r="1"/><circle cx="16" cy="10" r="1"/>',
  folder:'<path d="M2 6V4h6l2 2h8v10H2Z"/>',
  download:'<path d="M10 2v10m-4-4 4 4 4-4M3 13v4h14v-4"/>',
  upload:'<path d="M10 13V3m-4 4 4-4 4 4M3 13v4h14v-4"/>',
  trash:'<path d="M3 5h14M7 5V3h6v2M5 5l1 12h8l1-12M8 8v6M12 8v6"/>',
  pin:'<path d="m6 3 8 0-1 5 3 3H4l3-3-1-5ZM10 11v7"/>',
  arrowLeft:'<path d="m8 4-6 6 6 6M2 10h16"/>',
  arrowRight:'<path d="m12 4 6 6-6 6M2 10h16"/>'
});

const ids = new Set();
for (const entry of [...catalog.pages,...catalog.dialogs]) {
  if (!entry.id || ids.has(entry.id)) throw new Error(`Duplicate or missing atlas ID: ${entry.id}`);
  ids.add(entry.id);
  if (!entry.states?.length) entry.states = ['normal','loading','error','blocked','success'];
  if (!entry.area) throw new Error(`Missing area: ${entry.id}`);
  if (!entry.html) throw new Error(`Missing visual body: ${entry.id}`);
  entry.states = entry.states.map(state => state === 'default' ? 'normal' : state);
  if (entry.variants?.default) { entry.variants.normal = entry.variants.default; delete entry.variants.default; }
  if (entry.variants?.normal === entry.html) delete entry.variants.normal;
}
if (globalOnly) catalog.coverage = catalog.coverage.filter(row => ids.has(row.target));
for (const row of catalog.coverage) if (!ids.has(row.target)) throw new Error(`Coverage points to missing view: ${row.target}`);

const template = (await readFile(path.join(directory,'atlas-template.html'),'utf8')).replace('@@SHARED_FLOW@@',createSharedFlow.toString());
const inlineData = value => JSON.stringify(value).replaceAll('<','\\u003c').replaceAll('\u2028','\\u2028').replaceAll('\u2029','\\u2029');
const packedData = offline => inlineData({format:'gzip-base64',data:gzipSync(JSON.stringify({...catalog,offline}),{level:9}).toString('base64')});
const fragment = template.replace('@@ATLAS_DATA@@',packedData(false));
const offlineFragment = template.replace('@@ATLAS_DATA@@',packedData(true));
const offline = `<!doctype html>\n<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="referrer" content="no-referrer"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'"><title>TogetherServer UI review</title><style>html{color-scheme:light dark}body{margin:0;padding:16px;background:light-dark(#faf8f5,#181716)}#ts-ui-atlas{margin-inline:auto}</style></head><body>\n${offlineFragment}\n</body></html>\n`;
await writeFile(path.join(directory,'together-ui-review.html'),fragment,'utf8');
await writeFile(path.join(directory,'index.html'),offline,'utf8');
await writeFile(path.join(directory,'coverage.json'),JSON.stringify(catalog.coverage,null,2)+'\n','utf8');
const markdownEscape = value => String(value || '').replaceAll('|','\\|').replaceAll('\n',' ');
const mapping = catalog.coverage.map(row => `| ${markdownEscape(row.source)} | ${markdownEscape(row.label)} | ${markdownEscape(row.target)} | ${markdownEscape(row.decision)} | ${markdownEscape(row.note)} |`).join('\n');
const viewRows = [...catalog.pages.map(entry => ({...entry,kind:'Page'})),...catalog.dialogs.map(entry => ({...entry,kind:entry.id.startsWith('confirm-') ? 'Confirmation' : 'Dialog'}))].map(entry => `| ${entry.id} | ${entry.kind} | ${entry.area} | ${markdownEscape(entry.title)} | ${entry.states.join(', ')} |`).join('\n');
await writeFile(path.join(directory,'COVERAGE.md'),`# UI review coverage\n\n${catalog.pages.length} pages, ${catalog.dialogs.length} dialogs and confirmations, ${catalog.coverage.length} source mappings. All examples are synthetic. ${globalOnly ? 'INCOMPLETE GLOBAL-ONLY BUILD.' : 'Application code has not been changed.'}\n\n## Review views\n\n| ID | Kind | Context | Title | States and variants |\n| --- | --- | --- | --- | --- |\n${viewRows}\n\n## Current-to-proposed mapping\n\nApp preference dialogs explicitly merged into direct App Settings pages remain listed. Inline editors and reviews are included as flows. Native Windows file pickers retain system presentation.\n\n| Current source | Current view or decision | Proposed ID | Treatment | Notes |\n| --- | --- | --- | --- | --- |\n${mapping}\n`,'utf8');
console.log(`Built ${catalog.pages.length} pages, ${catalog.dialogs.length} dialogs and confirmations, ${catalog.coverage.length} source mappings. Fragment ${Buffer.byteLength(fragment)} bytes.`);
