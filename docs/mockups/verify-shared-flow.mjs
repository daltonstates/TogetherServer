import assert from 'node:assert/strict';
import { createSharedFlow } from './shared-flow.mjs';

export function verifySharedModel(passed) {
  const flow = createSharedFlow({icon:() => '',escape:value => String(value)});
  assert.equal(flow.run('receive'),false,'Receiving bypassed explicit local consent/trust review.');
  flow.state.grants.successor.receive = false;
  assert.equal(flow.run('receive',true),false,'Local consent bypassed Receive permission.');
  flow.setScene('chosen');
  assert.equal(flow.state.status,'Offline','A hosting decision was promoted to Ready.');
  flow.state.grants.successor.eligible = false;
  assert.equal(flow.can('restore'),false,'Receive permission bypassed host eligibility.');
  flow.setScene('chosen'); flow.state.consent.successor = false;
  assert.equal(flow.can('restore'),false,'Restoration bypassed local consent.');
  flow.setScene('chosen'); flow.state.trusted.successor = false;
  assert.equal(flow.can('restore'),false,'Restoration bypassed group trust.');
  flow.setScene('ready-to-start'); flow.state.copies.successor = 12;
  assert.equal(flow.can('start'),false,'An older copy bypassed the exact current save requirement.');
  flow.setScene('route-needed');
  assert.equal(flow.can('route'),false,'A self-check satisfied peer route proof.');
  flow.state.client = 'friend'; assert.equal(flow.can('route'),true);
  flow.state.grants.friend.approved = false; assert.equal(flow.can('route'),false,'An unapproved peer checked the route.');
  flow.state.grants.friend.approved = true; flow.state.pins.friend = false; assert.equal(flow.can('route'),false,'A mismatched peer pin passed.');
  flow.state.pins.friend = true; flow.state.trusted.friend = false; assert.equal(flow.can('route'),false,'An untrusted peer checked the route.');
  flow.setScene('recovery'); flow.state.client = 'successor'; flow.state.lossObservedMs = 119999;
  assert.equal(flow.can('offer'),false,'Recovery bypassed continuous Host-loss evidence.');
  flow.state.lossObservedMs = 120000; assert(flow.run('offer',true)); assert(flow.run('vote',true));
  assert.equal(flow.state.authority,'original','One vote transferred authority.');
  flow.state.client = 'friend'; assert(flow.landing().includes('Review recovery vote'));
  flow.state.grants.friend.voter = false; assert.equal(flow.can('vote'),false,'Receive bypassed the separate voter permission.');
  flow.state.grants.friend.voter = true; assert(flow.run('vote',true));
  assert.equal(flow.state.authority,'successor'); assert.equal(flow.state.status,'Offline');
  for (const scene of ['expired','revoked','conflicts','unknown','loading','blocked']) {
    flow.setScene(scene);
    assert(!flow.hostBanner().includes('· Ready'),`${scene} retained fresh readiness.`);
    for (const name of ['receive','restore','route','start','offer','vote','connect']) assert.equal(flow.can(name),false,`${scene} allowed ${name}`);
  }
  flow.setScene('recovery'); flow.state.client = 'original'; assert.equal(flow.localState(),'Unknown','Host loss was treated as proof that the old server stopped.');
  passed('Independent sharing permissions, trust and authority guards');
}

export async function verifySharedInteractions({root,document,dom,change,click,choose,passed}) {
  const wait = () => new Promise(resolve => setTimeout(resolve,720));
  const acknowledge = () => { for (const box of root.querySelectorAll('.ts-dialog-body [data-ack]')) { box.checked = true; box.dispatchEvent(new dom.window.Event('change',{bubbles:true})); } };
  const perform = async name => {
    const control = root.querySelector(`.ts-dialog-body [data-shared-action="${name}"]`); assert(control,`Missing guided ${name}`);
    if (control.hasAttribute('data-requires-check')) { assert(control.disabled,`${name} initially bypassed review`); acknowledge(); }
    assert.equal(control.disabled,false,`Acknowledged ${name} stayed unavailable`); control.click();
    assert([...root.querySelectorAll('.ts-overlay button,.ts-overlay input')].every(control => control.disabled),'Pending guide escaped busy guard.');
    await wait();
  };
  choose('join-shared'); change('[data-review="state"]','normal'); change('[data-review="client"]','successor');
  assert(root.querySelector('[data-shared-landing]').textContent.includes('Get latest save'));
  assert.equal(root.querySelectorAll('[data-shared-landing] button').length,2,'Secondary sharing actions leaked onto the landing page.');
  assert(!root.querySelector('[data-shared-landing]').textContent.includes('Recovery votes'));
  for (const control of root.querySelectorAll('[data-shared-landing] [data-primary-action]')) assert(control.textContent.trim(),'A primary sharing label disappeared.');
  click('[data-shared-action="open-guide"]'); await perform('receive');
  assert(root.querySelector('.ts-dialog-body').textContent.includes('Arrange the hosting transfer'));
  change('[data-review="client"]','original'); await perform('transfer');
  assert(root.querySelector('.ts-dialog-body').textContent.includes('Waiting for Friend PC A'));
  change('[data-review="client"]','successor'); await perform('receive');
  assert(root.querySelector('.ts-dialog-body').textContent.includes('Waiting for the hosting decision'));
  change('[data-review="client"]','original'); await perform('complete');
  assert(root.querySelector('[data-shared-host]').dataset.sharedHost === 'successor');
  assert(!root.querySelector('[data-shared-host]').textContent.includes('· Ready'),'Decision alone displayed Ready.');
  change('[data-review="client"]','successor'); await perform('restore');
  assert(root.querySelector('.ts-dialog-body [data-shared-action="route"]').disabled,'Successor self-check enabled.');
  change('[data-review="client"]','friend'); await perform('route');
  change('[data-review="client"]','successor'); await perform('start');
  assert(root.querySelector('[data-shared-host]').textContent.includes('Starting'));
  assert(!root.querySelector('[data-shared-host]').textContent.includes('· Ready'),'Start request alone displayed Ready.');
  await perform('observe');
  for (const client of ['original','successor','friend']) {
    change('[data-review="client"]',client);
    choose('join-play');
    if (client !== 'successor') {
      assert(root.querySelector('.ts-server-command [data-action="open-game"]').disabled,'Unreviewed new Host connection allowed Open game.');
      assert.equal(root.querySelector('.ts-server-command .ts-badge').textContent,'Unknown','An unconnected client claimed fresh readiness.');
      click('[data-shared-action="join-current"]'); await perform('connect'); click('.ts-dialog-body [data-page="join-play"]');
    }
    assert.equal(root.querySelector('.ts-server-command [data-action="open-game"]').disabled,false,`${client} lost fresh play readiness after observing or connecting to the new Host.`);
    for (const page of ['join-shared','join-play','host-overview']) {
      choose(page);
      const banner = root.querySelector('[data-shared-host]');
      assert.equal(banner.dataset.sharedHost,'successor',`${client}/${page} lost current Host`);
      assert(banner.textContent.includes('Friend PC A') && banner.textContent.includes('Ready'),`${client}/${page} changed replicated readiness`);
    }
  }
  choose('join-play');
  assert.equal(root.querySelector('.ts-server-command [data-action="open-game"]').disabled,false);
  click('.ts-content [data-action="reveal"]');
  assert(root.querySelector('[data-revealed]').textContent.includes('192.0.2.20'),'New Host retained the old game address.');
  passed('Complete guided hosting transfer across three clients');

  choose('join-shared'); click('[data-dialog="shared-manage"]');
  const destinations = new Set();
  for (const tab of ['permissions','copies','history','checks']) {
    click(`[data-sharing-tab="${tab}"]`);
    assert.equal(root.querySelector(`[data-sharing-tab="${tab}"]`).getAttribute('aria-pressed'),'true');
    for (const button of root.querySelectorAll('.ts-dialog-body [data-dialog]')) destinations.add(button.dataset.dialog);
  }
  for (const id of ['shared-host-grants','shared-consent','shared-history','shared-vote','shared-conflicts','shared-receipt','shared-route','shared-rehearsal','shared-live-save']) assert(destinations.has(id),`Hidden secondary feature ${id}`);
  click('.ts-dialog-body [data-dialog="shared-route"]'); click('.ts-dialog-heading [data-close]');
  assert.equal(root.querySelector('[data-current-dialog]').dataset.currentDialog,'shared-manage','Nested detail did not return to management.');
  assert.equal(document.activeElement.dataset.dialog,'shared-route','Nested detail lost its opener focus.');
  passed('Named secondary sharing sections and focus return');

  choose('join-shared'); change('[data-review="state"]','normal'); change('[data-review="client"]','successor');
  click('[data-shared-action="open-guide"]'); acknowledge(); click('.ts-dialog-body [data-shared-action="receive"]');
  change('[data-review="client"]','friend'); await wait(); change('[data-review="client"]','successor');
  assert(root.querySelector('.ts-dialog-body').textContent.includes('Get latest save'),'Canceled receive changed the new client.');
  assert(!root.querySelector('[data-shared-landing]').textContent.includes('version 12Your'));
  choose('join-shared'); change('[data-room-selector]','MapleCraft');
  assert(root.querySelector('[data-shared-host]').textContent.includes('MapleCraft'));
  assert(root.querySelector('[data-shared-landing]').textContent.includes('No verified copy'));
  change('[data-room-selector]','Alderheim');
  assert(root.querySelector('[data-shared-landing]').textContent.includes('No verified copy'),'A different world overwrote the selected save.');
  passed('Client and world isolation during pending work');

  choose('join-shared'); change('[data-review="state"]','hosting'); change('[data-review="client"]','successor');
  choose('host-overview'); click('[aria-label="Stop server"]'); acknowledge(); click('.ts-dialog-footer [data-confirm]');
  choose('join-shared'); assert(root.querySelector('[data-shared-host]').textContent.includes('Stopping'),'Ordinary Stop left shared readiness stale.');
  change('[data-review="state"]','unknown');
  assert(root.querySelector('[data-shared-host]').textContent.includes('Last known Host'));
  assert(!root.querySelector('[data-shared-host]').textContent.includes('· Ready'));
  for (const state of ['loading','error','expired','revoked','conflicts']) {
    change('[data-review="state"]',state);
    assert(!root.querySelector('[data-shared-host]').textContent.includes('· Ready'),`${state} displayed fresh readiness.`);
    choose('join-play'); assert(!root.querySelector('.ts-server-command').textContent.includes('Auto-stop in'));
    choose('join-shared');
  }
  change('[data-review="state"]','chosen'); change('[data-review="client"]','successor'); choose('host-overview');
  click('[data-dialog="server-actions"]'); click('.ts-dialog-body [data-dialog="confirm-restart"]'); acknowledge();
  assert(root.querySelector('.ts-dialog-footer [data-confirm]').disabled,'More bypassed required hosting setup.');
  passed('Common lifecycle status and explicit stale Host identity');
}

export function verifyTooltip({root,dom,passed}) {
  const tooltip = root.querySelector('.ts-tooltip');
  assert.equal(root.querySelectorAll('[role="tooltip"]').length,1,'Duplicate tooltip layers.');
  assert.equal(root.querySelectorAll('[data-tooltip]').length,0,'Host tooltip triggers remain active.');
  const target = root.querySelector('[data-ts-tooltip]');
  const bounds = (left,top,width,height) => ({left,top,width,height,right:left+width,bottom:top+height});
  root.getBoundingClientRect = () => bounds(0,0,390,700);
  tooltip.getBoundingClientRect = () => bounds(0,0,170,36);
  target.getBoundingClientRect = () => bounds(348,650,36,36);
  target.focus();
  assert.equal(tooltip.hidden,false); assert.equal(tooltip.dataset.side,'top','Bottom edge tooltip did not flip.');
  assert.equal(tooltip.textContent,target.dataset.tsTooltip);
  assert(target.getAttribute('aria-describedby').includes(tooltip.id));
  const left = parseFloat(tooltip.style.left), tail = parseFloat(tooltip.style.getPropertyValue('--ts-tail'));
  assert(left >= 8 && left+170 <= 382,'Tooltip escaped the narrow root.');
  assert(Math.abs(left+tail+4-366) <= 10,'Tail points away from its triggering control.');
  root.dispatchEvent(new dom.window.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
  assert(tooltip.hidden); assert(!target.getAttribute('aria-describedby')?.includes(tooltip.id));
  target.getBoundingClientRect = () => bounds(70,40,36,36);
  target.dispatchEvent(new dom.window.Event('pointerover',{bubbles:true}));
  assert.equal(tooltip.dataset.side,'bottom');
  target.dispatchEvent(new dom.window.Event('pointerout',{bubbles:true})); assert(tooltip.hidden);
  passed('One tailed tooltip with edge positioning, focus and Escape');
}
