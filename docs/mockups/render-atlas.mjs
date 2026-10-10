// Run only on an owner-approved separate runner/session. Headless alone is not focus safety.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';
import { gunzipSync } from 'node:zlib';

if (!process.argv.includes('--allow-isolated-browser-tests')) throw new Error('ForegroundSafety: browser rendering requires an explicitly approved separate runner/session.');
const directory = path.dirname(fileURLToPath(import.meta.url));
const repository = path.resolve(directory,'../..');
const output = path.resolve(repository,process.argv.find(arg => arg.startsWith('--output='))?.slice(9) || 'local-data/mockup-visual-review');
assert(output.startsWith(path.join(repository,'local-data')+path.sep),'Visual evidence must remain in task-owned local-data.');
await mkdir(output,{recursive:true});
const source = await readFile(path.join(directory,'index.html'),'utf8');
const packed = JSON.parse(source.match(/<script type="application\/json" id="ts-ui-atlas-data">([\s\S]*?)<\/script>/)[1]);
const catalog = JSON.parse(gunzipSync(Buffer.from(packed.data,'base64')).toString('utf8'));
const require = createRequire(path.join(repository,'ui/package.json'));
const {chromium} = require('playwright');
const browser = await chromium.launch({headless:true});
const screenshots = [], failures = [];
try {
  const context = await browser.newContext({viewport:{width:1440,height:1000},colorScheme:'dark',deviceScaleFactor:1});
  const page = await context.newPage();
  page.on('pageerror',error => failures.push({kind:'script',message:error.message}));
  await page.goto(pathToFileURL(path.join(directory,'index.html')).href);
  await page.locator('#ts-ui-atlas[data-ready="true"]').waitFor();
  const choose = async (id,theme,width,scale=100,state='normal',client) => {
    await page.setViewportSize({width:width==='narrow'?422:1440,height:1000});
    await page.locator('[data-review="theme"]').selectOption(theme);
    await page.locator('[data-review="width"]').selectOption(width);
    await page.locator('[data-review="scale"]').selectOption(String(scale));
    await page.locator('[data-review="screen"]').selectOption(id);
    await page.locator('[data-review="client"]').selectOption(client || (['join-shared','shared-host-guide'].includes(id)?'successor':'original'));
    await page.locator('[data-review="state"]').selectOption(state);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  };
  const inspect = async (id,width) => {
    const findings = await page.evaluate(({id,width}) => {
      const product=document.querySelector('.ts-product'), box=product.getBoundingClientRect();
      const issues=[];
      const picker=document.querySelector('.ts-section-picker'), nav=document.querySelector('.ts-sidebar-nav');
      if (picker && nav && getComputedStyle(picker).display!=='none' && getComputedStyle(nav).display!=='none') issues.push('Duplicate section navigation');
      for(const control of product.querySelectorAll('button,input,select,textarea,table,h2,h3')) {
        if(control.closest('[hidden],details:not([open])') && !control.closest('summary')) continue;
        const rect=control.getBoundingClientRect();
        if(!rect.width || !rect.height) continue;
        if(rect.left<box.left-1 || rect.right>box.right+1) issues.push(`Horizontal overflow: ${control.getAttribute('aria-label') || control.textContent.trim().slice(0,70) || control.tagName}`);
      }
      for(const control of document.querySelectorAll('.ts-dialog-body .ts-button')) if(!control.textContent.trim()) issues.push('Unlabelled dialog action');
      for(const control of product.querySelectorAll('button')) if(control.querySelectorAll('.ts-icon').length>1) issues.push(`Duplicate action icons: ${control.getAttribute('aria-label') || control.textContent.trim()}`);
      if(id==='server-actions' && document.querySelector('.ts-action-menu .ts-iconbutton')) issues.push('Icon-only action in tools menu');
      return issues.map(message=>({id,width,message}));
    },{id,width});
    failures.push(...findings);
  };
  const saveImage = async (entry,theme,width,scale,state='normal',suffix='') => {
    await inspect(entry.id,width);
    const file=`${entry.id}-${theme}-${width}-${scale}${state==='normal'?'':`-${state}`}${suffix}.png`;
    await page.locator(entry.kind==='dialog'?'.ts-dialog':'.ts-product').screenshot({path:path.join(output,file)});
    screenshots.push({id:entry.id,title:entry.title,kind:entry.kind,theme,width,scale,state,file});
  };
  const capture = async (entry,theme,width,scale=100,state='normal',client) => {
    await choose(entry.id,theme,width,scale,state,client);
    await saveImage(entry,theme,width,scale,state);
  };
  const entries=[...catalog.pages.map(entry=>({...entry,kind:'page'})),...catalog.dialogs.map(entry=>({...entry,kind:'dialog'}))];
  for (const [theme,width] of [['dark','desktop'],['light','narrow']]) for (const entry of entries) await capture(entry,theme,width);
  for (const id of ['host-overview','join-play','join-shared','app-appearance','server-actions','tool-move','shared-host-guide','setup-game','setup-world','setup-app','tool-custom-certification','join-chat']) {
    const entry=entries.find(entry=>entry.id===id);
    await capture(entry,'light','desktop'); await capture(entry,'dark','narrow'); await capture(entry,'dark','narrow',150);
  }
  for (const state of ['verifying','consent-off','handoff-waiting','chosen','route-needed','ready-to-start','hosting','unknown','blocked','recovery','conflicts']) {
    const entry=entries.find(entry=>entry.id==='join-shared');
    await capture(entry,'dark','desktop',100,state);
    await capture(entry,'dark','narrow',150,state);
  }
  for (const [theme,width,scale,selector] of [
    ['dark','desktop',100,'[aria-label="Show server IP"]'],
    ['light','narrow',100,'[aria-label="Copy game password"]'],
    ['dark','narrow',150,'[aria-label="App preferences"]'],
    ['dark','narrow',150,'[aria-label="Commands (Ctrl K)"]']
  ]) {
    await choose('host-overview',theme,width,scale);
    const anchor=page.locator(selector);
    await anchor.hover(); await anchor.focus();
    const tip=page.locator('.ts-tooltip:not([hidden])'); await tip.waitFor();
    assert.equal(await tip.count(),1,'Only one tooltip may be visible.');
    const aligned=await page.evaluate(selector=>{
      const anchor=document.querySelector(selector).getBoundingClientRect();
      const tip=document.querySelector('.ts-tooltip'),box=tip.getBoundingClientRect(),style=getComputedStyle(tip);
      const tailX=box.left+parseFloat(style.getPropertyValue('--ts-tail'))+4.5;
      return Math.abs(tailX-(anchor.left+anchor.width/2))<=12 && parseFloat(getComputedStyle(tip,'::after').width)>=8;
    },selector);
    assert(aligned,`Tooltip tail missed its trigger: ${selector}`);
    await saveImage(entries.find(entry=>entry.id==='host-overview'),theme,width,scale,'normal',`-tooltip-${screenshots.length}`);
    await page.keyboard.press('Escape');
    assert(await page.locator('.ts-tooltip').isHidden(),'Escape must dismiss the tooltip.');
    assert(await anchor.evaluate(element=>element===document.activeElement),'Tooltip dismissal must retain trigger focus.');
  }
  // Observe the same hosting decision in each client without resetting the scenario.
  await choose('join-shared','dark','desktop',100,'hosting','successor');
  for (const client of ['successor','friend','original']) {
    await page.locator('[data-review="client"]').selectOption(client);
    await page.locator('[data-review="screen"]').selectOption('join-shared');
    await saveImage(entries.find(entry=>entry.id==='join-shared'),'dark','desktop',100,'hosting',`-${client}`);
    if(client!=='successor') {
      await page.locator('.ts-world-host [data-shared-action="join-current"]').click();
      await page.locator('[data-ack]').check();
      await page.locator('[data-shared-action="connect"]').click();
      await page.locator('.ts-dialog-body [data-page="join-play"]').waitFor();
      await page.locator('.ts-dialog-body [data-page="join-play"]').click();
    } else await page.locator('[data-review="screen"]').selectOption('join-play');
    await saveImage(entries.find(entry=>entry.id==='join-play'),'dark','desktop',100,'hosting',`-${client}`);
    assert.equal(await page.locator('[aria-label="Open game"]').isEnabled(),true,'A connected client should see the next play action.');
    assert((await page.locator('.ts-server-identity').textContent()).includes('Friend PC A'),'Play must identify the new Host.');
    await page.locator('[data-review="screen"]').selectOption('host-overview');
    await saveImage(entries.find(entry=>entry.id==='host-overview'),'dark','desktop',100,'hosting',`-${client}`);
  }
  await choose('host-overview','dark','desktop');
  await page.locator('[aria-label="More server actions"]').click();
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('.ts-dialog').count(),0,'Escape must close the tools dialog.');
  assert(await page.locator('[aria-label="More server actions"]').evaluate(element=>element===document.activeElement),'Dialog dismissal must return focus.');
  // Contact sheets are rendered from the actual PNGs, without changing the screenshots.
  const escape=value=>String(value).replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
  for (const kind of ['page','dialog']) {
    const images=screenshots.filter(item=>item.kind===kind&&item.theme==='dark'&&item.width==='desktop'&&item.scale===100&&item.state==='normal'&&!item.file.includes('tooltip'));
    for(let offset=0;offset<images.length;offset+=8) {
      const cells=await Promise.all(images.slice(offset,offset+8).map(async item=>`<figure><figcaption>${escape(item.title)}</figcaption><img src="data:image/png;base64,${(await readFile(path.join(output,item.file))).toString('base64')}"></figure>`));
      await page.setViewportSize({width:1280,height:1000});
      await page.setContent(`<!doctype html><html><head><style>body{margin:0;padding:20px;background:#101113;color:#f2f3f5;font:14px 'Segoe UI',sans-serif;display:grid;grid-template-columns:1fr 1fr;gap:20px}figure{margin:0;min-width:0}figcaption{margin-bottom:8px}img{display:block;width:100%;height:auto}</style></head><body>${cells.join('')}</body></html>`);
      await page.screenshot({path:path.join(output,`contact-${kind}-${Math.floor(offset/8)+1}.png`),fullPage:true});
    }
  }
  const report={sourceSha256:createHash('sha256').update(source).digest('hex'),pages:catalog.pages.length,dialogs:catalog.dialogs.length,screenshots:screenshots.length,failures,images:screenshots,scope:'Isolated synthetic atlas rendering; no application/game/native acceptance'};
  await writeFile(path.join(output,'VISUAL-REPORT.json'),JSON.stringify(report,null,2)+'\n');
  console.log(`Rendered ${screenshots.length} screenshots. ${failures.length} findings. Manual visual inspection still required.`);
  if(failures.length) process.exitCode=1;
} catch (error) {
  failures.push({kind:'runner',message:error.message});
  await writeFile(path.join(output,'VISUAL-REPORT.json'),JSON.stringify({sourceSha256:createHash('sha256').update(source).digest('hex'),pages:catalog.pages.length,dialogs:catalog.dialogs.length,screenshots:screenshots.length,complete:false,failures,images:screenshots,scope:'Partial isolated synthetic atlas rendering; no application/game/native acceptance'},null,2)+'\n');
  throw error;
} finally { await browser.close(); }
