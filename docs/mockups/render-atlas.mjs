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
  const choose = async (id,theme,width,scale=100) => {
    await page.setViewportSize({width:width==='narrow'?422:1440,height:1000});
    await page.locator('[data-review="theme"]').selectOption(theme);
    await page.locator('[data-review="width"]').selectOption(width);
    await page.locator('[data-review="scale"]').selectOption(String(scale));
    await page.locator('[data-review="screen"]').selectOption(id);
    await page.locator('[data-review="client"]').selectOption(['join-shared','shared-host-guide'].includes(id)?'successor':'original');
    await page.locator('[data-review="state"]').selectOption('normal');
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
      if(id==='server-actions' && document.querySelector('.ts-action-menu .ts-iconbutton')) issues.push('Icon-only action in tools menu');
      return issues.map(message=>({id,width,message}));
    },{id,width});
    failures.push(...findings);
  };
  const capture = async (entry,theme,width,scale=100) => {
    await choose(entry.id,theme,width,scale);
    await inspect(entry.id,width);
    const file=`${entry.id}-${theme}-${width}-${scale}.png`;
    await page.locator(entry.kind==='dialog'?'.ts-dialog':'.ts-product').screenshot({path:path.join(output,file)});
    screenshots.push({id:entry.id,title:entry.title,kind:entry.kind,theme,width,scale,file});
  };
  const entries=[...catalog.pages.map(entry=>({...entry,kind:'page'})),...catalog.dialogs.map(entry=>({...entry,kind:'dialog'}))];
  for (const [theme,width] of [['dark','desktop'],['light','narrow']]) for (const entry of entries) await capture(entry,theme,width);
  for (const id of ['host-overview','join-play','join-shared','app-appearance','server-actions','tool-move','shared-host-guide']) {
    const entry=entries.find(entry=>entry.id===id);
    await capture(entry,'light','desktop'); await capture(entry,'dark','narrow'); await capture(entry,'dark','narrow',150);
  }
  // Contact sheets are rendered from the actual PNGs, without changing the screenshots.
  const escape=value=>String(value).replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
  for (const kind of ['page','dialog']) {
    const images=screenshots.filter(item=>item.kind===kind&&item.theme==='dark'&&item.width==='desktop'&&item.scale===100);
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
} finally { await browser.close(); }
