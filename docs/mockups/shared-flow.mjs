// Synthetic shared-world state. This module never reads application data or performs I/O.
export const sharedStates = ['normal','empty','loading','error','blocked','recovery','success','consent-off','untrusted','low-space','stalled','verifying','receipt-pending','expired','revoked','fenced','conflicts','received','handoff-needed','handoff-waiting','chosen','setup','route-needed','ready-to-start','starting','hosting','offline','unknown'];

export const sharedCatalog = {
  pages: [],
  dialogs: [
    { id:'shared-host-guide', title:'Host this shared save', area:'shared', description:'Get the save, prepare this PC, then choose Start.', states:sharedStates, html:'<section data-shared-guide><h3>Get save → Prepare hosting → Start server</h3></section>', footer:'<button type="button" class="ts-button" data-close>Close</button>' },
    { id:'shared-manage', title:'Manage sharing', area:'shared', description:'Permissions, saves, history and checks for the selected world.', states:['normal','loading','error'], html:'<section data-shared-management><h3>Sharing details</h3></section>', footer:'<button type="button" class="ts-button" data-close>Done</button>' }
  ],
  coverage: []
};

// Kept self-contained so the builder can embed the same implementation in both previews.
export function createSharedFlow({icon,escape}) {
  const clients = { original:'Original Host PC', successor:'Friend PC A', friend:'Friend PC B' };
  const state = { world:'Alderheim', client:'successor', tab:'permissions', scene:'normal', phase:'get', authority:'original', status:'Ready', version:12, copies:{original:12,successor:null,friend:null}, consent:{original:true,successor:false,friend:false}, trusted:{original:true,successor:false,friend:true}, connected:{original:'original',successor:'original',friend:'original'}, receipt:false, restored:false, route:false, joinMode:false, busy:false, recovery:false, voters:[], available:true, lossObservedMs:0 };
  const grants = () => state.grants[state.client];
  const button = (label,name,primary=false,extra='') => `<button type="button" class="ts-button ${primary ? 'ts-primary' : 'ts-ghost'}" data-primary-action data-shared-action="${name}" ${extra}>${icon(({receive:'download',transfer:'archive',complete:'check',restore:'folder',route:'link',start:'play',observe:'refresh',stop:'stop',connect:'link',offer:'users',vote:'check','open-guide':'server','join-current':'game',retry:'refresh'}[name] || 'arrowRight'))}<span>${escape(label)}</span></button>`;
  const link = (label,id,name='settings') => `<button type="button" class="ts-button ts-ghost" data-primary-action data-dialog="${id}">${icon(name)}<span>${escape(label)}</span></button>`;
  const note = (text,tone='warning') => `<div class="ts-alert" data-tone="${tone}" role="${tone === 'error' ? 'alert' : 'status'}">${text}</div>`;
  const ack = text => `<label class="ts-checkrow"><input type="checkbox" data-ack><span>${escape(text)}</span></label>`;
  const address = () => state.authority === 'original' ? '192.0.2.10' : '192.0.2.20';
  const problem = () => ({empty:'The Host has not published a completed save yet.',loading:'Checking current access, save and hosting status…',error:'The latest check failed. Your previous verified copy is preserved.',blocked:'This PC does not have current Receive and hosting access. Ask the owner to review its grants.','low-space':'Receiving is paused until this PC has enough free space. Your completed copy is preserved.',stalled:'Receiving stalled. Your previous completed copy is preserved.',verifying:'The received files are being verified. The previous completed copy remains usable.',expired:'The owner’s access deadline has ended. New copies and hosting actions are unavailable.',revoked:'The owner removed this PC. Previously received copies remain private, but do not grant hosting authority.',conflicts:'Competing hosting histories need a group decision. Neither branch is selected automatically.',unknown:'Current server status is Unknown. Start is blocked until the recorded process and current Host are checked.'}[state.scene] || '');
  const denied = () => ['empty','loading','blocked','low-space','verifying','expired','revoked','conflicts','unknown'].includes(state.scene);
  const owner = () => state.client === state.authority;
  const currentStatus = () => ['loading','error','expired','revoked','conflicts','blocked'].includes(state.scene) ? 'Unknown' : state.status;
  const can = name => {
    if (state.busy || !grants().approved || !state.pins[state.client]) return false;
    if (name === 'connect') return currentStatus() === 'Ready';
    if (denied()) return false;
    if (name === 'receive') return state.available && grants().receive;
    if (name === 'remote-start') return grants().remoteStart && state.connected[state.client] === state.authority && state.status === 'Offline' && state.restored && state.route && state.copies[state.authority] === state.version;
    if (name === 'remote-stop') return grants().remoteStop && state.connected[state.client] === state.authority && currentStatus() === 'Ready';
    if (name === 'transfer') return state.client === 'original' && state.authority === 'original' && state.phase === 'handoff';
    if (name === 'complete') return state.client === 'original' && state.phase === 'complete' && state.copies.successor === state.version && state.receipt;
    if (name === 'restore') return state.client === 'successor' && grants().eligible && state.consent.successor && state.trusted.successor && state.authority === 'successor' && state.phase === 'setup' && state.copies.successor === state.version;
    if (name === 'route') return state.client !== 'successor' && state.trusted[state.client] && state.authority === 'successor' && state.phase === 'route';
    if (name === 'start') return owner() && grants().eligible && state.consent[state.client] && state.trusted[state.client] && state.restored && state.route && state.copies[state.client] === state.version && state.phase === 'start' && state.status === 'Offline';
    if (name === 'observe') return owner() && ['starting','stopping'].includes(state.phase);
    if (name === 'stop') return owner() && state.status === 'Ready';
    if (name === 'offer') return state.client === 'successor' && grants().eligible && state.consent.successor && state.trusted.successor && state.recovery && state.lossObservedMs >= 120000 && state.phase === 'recover' && state.copies.successor === state.version;
    if (name === 'vote') return grants().voter && state.trusted[state.client] && ['successor','friend'].includes(state.client) && state.phase === 'vote' && !state.voters.includes(state.client);
    return true;
  };
  const setScene = scene => {
    const client = state.client;
    Object.assign(state,{scene,phase:'get',authority:'original',status:'Ready',version:12,copies:{original:12,successor:null,friend:null},consent:{original:true,successor:false,friend:false},trusted:{original:true,successor:false,friend:true},connected:{original:'original',successor:'original',friend:'original'},processes:{original:'Ready',successor:'Offline',friend:'Offline'},receipt:false,restored:true,route:true,joinMode:false,busy:false,recovery:false,voters:[],available:scene !== 'empty',lossObservedMs:0,grants:{original:{approved:true,receive:true,eligible:true,voter:true,remoteStart:true,remoteStop:true},successor:{approved:true,receive:true,eligible:true,voter:true,remoteStart:true,remoteStop:true},friend:{approved:true,receive:true,eligible:false,voter:true,remoteStart:true,remoteStop:false}},pins:{original:true,successor:true,friend:true}});
    state.client = client;
    if (['received','handoff-needed','success','stalled','error'].includes(scene)) { state.copies.successor = 12; state.consent.successor = true; state.trusted.successor = true; state.phase = 'handoff'; }
    if (['handoff-waiting','receipt-pending'].includes(scene)) { state.status = 'Offline'; state.processes.original = 'Offline'; state.version = 13; state.copies.original = 13; state.phase = 'final-copy'; state.consent.successor = true; state.trusted.successor = true; if (scene === 'receipt-pending') state.copies.successor = 13; }
    if (['chosen','setup','route-needed','ready-to-start','starting','hosting','offline','unknown','fenced'].includes(scene)) {
      state.authority = 'successor'; state.version = 13; state.copies.original = 13; state.copies.successor = 13; state.consent.successor = true; state.trusted.successor = true; state.receipt = true; state.status = 'Offline'; state.processes.original = scene === 'fenced' ? 'Unknown' : 'Offline'; state.phase = 'setup'; state.restored = false; state.route = false;
      state.connected.successor = 'successor';
      if (['route-needed','ready-to-start','starting','hosting','offline','unknown','fenced'].includes(scene)) { state.restored = true; state.phase = 'route'; }
      if (['ready-to-start','starting','hosting','offline','unknown','fenced'].includes(scene)) { state.route = true; state.phase = 'start'; }
      if (scene === 'starting') { state.phase = 'starting'; state.status = 'Starting'; state.processes.successor = 'Starting'; }
      if (['hosting','fenced'].includes(scene)) { state.phase = 'hosting'; state.status = 'Ready'; state.processes.successor = 'Ready'; }
      if (scene === 'unknown') { state.status = 'Unknown'; state.processes.successor = 'Unknown'; }
    }
    if (scene === 'blocked') { state.grants.successor.receive = false; state.grants.successor.eligible = false; }
    if (scene === 'untrusted') state.trusted.successor = false;
    if (scene === 'recovery') { state.status = 'Unknown'; state.processes.original = 'Unknown'; state.copies.successor = 12; state.consent.successor = true; state.trusted.successor = true; state.recovery = true; state.lossObservedMs = 120000; state.phase = 'recover'; }
  };
  const run = (name,acknowledged=false) => {
    if (!can(name)) return false;
    if (['receive','transfer','complete','restore','route','start','stop','connect','offer','vote','remote-start','remote-stop'].includes(name) && !acknowledged) return false;
    if (name === 'receive') {
      state.consent[state.client] = true; state.trusted[state.client] = true; state.copies[state.client] = state.version;
      if (state.client === 'successor') {
        if (state.phase === 'final-copy') { state.receipt = true; state.phase = 'complete'; }
        else if (state.phase === 'get') state.phase = 'handoff';
      }
      if (['consent-off','untrusted','stalled','error'].includes(state.scene)) state.scene = 'normal';
    }
    if (name === 'transfer') { state.status = 'Offline'; state.processes.original = 'Offline'; state.version = 13; state.copies.original = 13; state.phase = 'final-copy'; }
    if (name === 'complete') { state.authority = 'successor'; state.connected.successor = 'successor'; state.phase = 'setup'; state.restored = false; state.route = false; }
    if (name === 'restore') { state.restored = true; state.phase = 'route'; }
    if (name === 'route') { state.route = true; state.phase = 'start'; }
    if (name === 'start') { state.status = 'Starting'; state.phase = 'starting'; }
    if (name === 'remote-start') { state.status = 'Starting'; state.phase = 'starting'; }
    if (name === 'remote-stop') { state.status = 'Stopping'; state.phase = 'stopping'; }
    if (name === 'observe') { if (state.phase === 'stopping') { state.status = 'Offline'; state.phase = 'start'; state.version++; state.copies[state.authority] = state.version; } else { state.status = 'Ready'; state.phase = 'hosting'; } }
    if (name === 'stop') { state.status = 'Offline'; state.phase = 'start'; state.version++; state.copies[state.authority] = state.version; }
    if (name === 'connect') { state.connected[state.client] = state.authority; state.joinMode = 'connected'; }
    if (name === 'offer') state.phase = 'vote';
    if (name === 'vote') {
      state.voters.push(state.client);
      if (state.voters.length === 2) { state.authority = 'successor'; state.connected.successor = 'successor'; state.status = 'Offline'; state.phase = 'setup'; state.restored = false; state.route = false; }
    }
    if (['start','remote-start','remote-stop','observe','stop'].includes(name)) state.processes[state.authority] = state.status;
    return true;
  };
  const hostStatus = () => state.scene === 'conflicts' ? 'Hosting decision disputed · game status Unknown' : currentStatus() === 'Unknown' ? `Last known Host: ${clients[state.authority]} · Unknown` : state.status === 'Ready' ? owner() || state.connected[state.client] === state.authority ? `Hosted by ${clients[state.authority]} · Ready` : `Current Host: ${clients[state.authority]} · status not yet checked on this PC` : `${clients[state.authority]} · ${state.status === 'Starting' ? 'Starting' : state.phase === 'start' ? 'Offline' : 'Preparing'} · server ${state.status}`;
  const hostBanner = () => `<section class="ts-world-host" data-shared-host="${state.scene === 'conflicts' ? 'disputed' : state.authority}" aria-label="Current shared-world Host"><div><strong>Alderheim · ${escape(hostStatus())}</strong><small>${state.scene === 'conflicts' ? 'Current Host disputed; resolve the conflicting histories.' : `${currentStatus() === 'Unknown' ? 'Last known' : 'Latest'} completed save: version ${state.version}${currentStatus() === 'Ready' ? ' · current gameplay can include newer changes' : ''}`}</small></div>${button('Join current Host','join-current',false,`${!can('connect') ? 'disabled' : ''}`)}</section>`;
  const progress = () => {
    const received = state.copies[state.client] === state.version;
    const prepared = state.client === 'successor' && state.restored && state.route && owner();
    const running = state.client === 'successor' && owner() && state.status === 'Ready';
    const active = !received ? 0 : !prepared ? 1 : 2;
    return `<ol class="ts-flow-steps" aria-label="Hosting steps">${['Get save','Prepare hosting','Start server'].map((label,index) => `<li data-step="${index}" ${index === active && !running ? 'aria-current="step"' : ''} data-complete="${index === 0 ? received : index === 1 ? prepared : running}"><span class="ts-step-number">${[received,prepared,running][index] ? icon('check') : index + 1}</span><span>${label}</span></li>`).join('')}</ol>`;
  };
  const next = () => {
    if (problem()) {
      if (state.scene === 'conflicts') return {title:'Resolve the hosting conflict',text:problem(),html:link('Review competing copies','shared-conflicts','warning')};
      if (state.scene === 'error' || state.scene === 'stalled') return {title:'Resume getting the save',text:problem(),html:button('Retry getting save','open-guide',true)};
      return {title:state.scene === 'verifying' ? 'Verifying the save' : state.scene === 'loading' ? 'Checking shared saves' : 'Hosting is waiting',text:problem(),html:link('Review details','shared-manage')};
    }
    if (state.phase === 'route' && state.client !== 'successor') return {title:'Check Friend PC A’s new connection',text:'The next Host needs this check from another approved PC before Start.',html:button('Check new Host connection','open-guide',true)};
    if (state.recovery && ['recover','vote'].includes(state.phase)) return {title:state.phase === 'recover' ? 'Recover from the last completed save' : 'Group approval is needed',text:'The original Host may still be running. Recovery is a deliberate group decision and can miss newer gameplay.',html:button(state.phase === 'vote' ? 'Review recovery vote' : 'Review recovery','open-guide',true)};
    if (state.client === 'friend') return {title:state.copies.friend === state.version ? 'Your saved copy is current' : 'Keep a save on this PC',text:'This PC can receive copies and play. Hosting here needs the owner’s Eligible host grant.',html:state.copies.friend === state.version ? button('Join current Host','join-current',true,`${state.status !== 'Ready' ? 'disabled' : ''}`) : button('Get latest save','open-guide',true)};
    if (state.scene === 'fenced' || state.authority === 'successor' && state.client === 'original') return {title:'This world moved to Friend PC A',text:'This PC retains its copy. Starting or publishing the shared world here is blocked by the current hosting decision.',html:button('Join current Host','join-current',true,`${state.status !== 'Ready' ? 'disabled' : ''}`)};
    if (state.client === 'original' && state.authority === 'original') return {title:state.phase === 'complete' ? 'The next PC confirmed the final save' : state.phase === 'final-copy' ? 'Waiting for Friend PC A to get the final save' : 'Move hosting to Friend PC A',text:state.phase === 'final-copy' ? 'Keep this server Offline. Friend PC A must get version 13 and confirm its exact copy.' : 'Prepare the final save, then complete the transfer after the selected PC confirms it.',html:button(state.phase === 'complete' ? 'Complete hosting transfer' : 'Review hosting transfer','open-guide',true)};
    if (state.copies.successor !== state.version || state.phase === 'final-copy') return {title:state.phase === 'final-copy' ? 'Get the final save' : 'Get a save to host',text:`Completed version ${state.version} is available. Your private copy is preserved while receiving.`,html:button(state.phase === 'final-copy' ? 'Get final save' : 'Get latest save','open-guide',true)};
    if (state.phase === 'complete') return {title:'Your final save is confirmed',text:'Original Host PC must complete the hosting transfer. Getting a save does not grant hosting authority.',html:button('View hosting progress','open-guide',true)};
    if (state.phase === 'handoff') return {title:'Prepare to host this save',text:'Original Host PC must Stop and transfer hosting to this PC before local setup can proceed.',html:button('Host this save','open-guide',true)};
    if (state.phase === 'setup') return {title:'Prepare hosting on this PC',text:'Hosting is assigned to this PC. Restore a fresh managed world and finish its setup.',html:button('Prepare hosting','open-guide',true)};
    if (state.phase === 'route') return {title:'One connection check remains',text:'Another approved PC must check the new Host’s signed control connection before Start.',html:button('Continue hosting setup','open-guide',true)};
    if (state.phase === 'start') return {title:'Ready to start on this PC',text:'The verified save, fresh setup and another PC’s control check are complete. Start remains your choice.',html:button('Start server','open-guide',true)};
    if (state.phase === 'starting') return {title:'Starting the server',text:'The Start request is accepted. A current status observation is still needed.',html:button('Check server status','open-guide',true)};
    return {title:'You are hosting Alderheim',text:`Started from completed save version ${state.version}. Other clients now identify this PC as the current Host.`,html:button('View hosting controls','open-guide',true)};
  };
  const landing = () => {
    const step = next();
    const hostingSteps = state.client === 'successor' && grants().eligible ? progress() : '';
    return `${hostBanner()}<section class="ts-panel ts-flow-panel" data-shared-landing>${hostingSteps}<div class="ts-next-action"><h3>${escape(step.title)}</h3><p>${escape(step.text)}</p>${step.html}</div><div class="ts-sharing-summary"><span>${state.copies[state.client] ? `This PC’s verified copy: version ${state.copies[state.client]}` : 'No verified copy on this PC'}</span>${link('Manage sharing','shared-manage','settings')}</div></section>`;
  };
  const guide = () => {
    let content = '';
    if (state.busy) return `${progress()}${note('Working on the selected step… Previous verified copies remain preserved.','info')}<progress aria-label="Shared-save operation"></progress>`;
    if (state.phase === 'stopping') return `${progress()}<h3>Waiting for graceful Stop</h3><p>The accepted Stop request does not yet prove exit or a completed save.</p>${button('Check server status','observe',true,`${!can('observe') ? 'disabled' : ''}`)}`;
    if (state.joinMode) {
      if (state.joinMode === 'connected') return `${hostBanner()}${note('Connected to the current signed Host in this preview. Joining the game is a separate action.','success')}<button type="button" class="ts-button ts-primary" data-primary-action data-page="join-play">${icon('game')}<span>Open Play</span></button>`;
      return `${hostBanner()}<h3>Connect to ${escape(clients[state.authority])}</h3><p>Check the current signed Host and its pinned connection. Keep the previous saved Host and received copies.</p><dl class="ts-kv"><dt>World</dt><dd>Alderheim</dd><dt>Current Host</dt><dd>${escape(clients[state.authority])}</dd><dt>Signed connection</dt><dd><code>${address()}:5131 · synthetic</code></dd></dl>${ack('I reviewed this world’s current Host and pinned connection')}${button('Connect to current Host','connect',true,`data-requires-check ${!can('connect') ? 'disabled' : ''}`)}`;
    }
    if (problem()) return `${progress()}${note(escape(problem()),['error','revoked'].includes(state.scene) ? 'error' : 'warning')}${['error','stalled'].includes(state.scene) ? `${ack('Allow the verified completed save from the reviewed group on this PC')}${button('Retry getting save','receive',true,'data-requires-check')}` : link('Manage sharing','shared-manage')}`;
    if (state.phase === 'route' && state.client !== 'successor') return `${progress()}<h3>Check the new Host connection</h3><p>This approved PC checks Friend PC A’s current signed, pinned control connection. Game traffic is a separate check.</p>${ack('I reviewed the signed connection for Alderheim from this other approved PC')}${button('Check new Host connection','route',true,'data-requires-check')}${link('Connection check details','shared-route','link')}`;
    if (state.scene === 'fenced' || state.client === 'original' && state.authority === 'successor') return `${progress()}${note('This PC is fenced by the current hosting decision. Do not Start or publish the shared world here.')}${button('Join current Host','join-current',true,`${state.status !== 'Ready' ? 'disabled' : ''}`)}${link('Review hosting history','shared-history')}`;
    if (state.recovery && ['recover','vote'].includes(state.phase)) {
      if (state.phase === 'recover') content = `<h3>Review recovery on Friend PC A</h3>${note('The original Host may still be running. This example has two minutes of continuously observed failed checks. A stopped game alone never establishes Host loss.')}<p>Use the last verified completed save, version ${state.version}. Later gameplay can be missing.</p>${ack('I understand another Host may still be running')}${ack('I accept recovery from the last completed save and the group decision requirements')}${button('Prepare recovery offer','offer',true,`data-requires-check ${!can('offer') ? 'disabled' : ''}`)}`;
      else content = `<h3>Review and vote for Friend PC A</h3>${note('Recovery uses the exact reviewed version and current group membership. A majority does not prove the old game process stopped.')}<p>Version ${state.version} · ${state.voters.length} of 3 voters approved · 2 required</p>${ack('I reviewed this candidate, exact save and possibility of another running Host')}${button(state.voters.includes(state.client) ? 'Your vote is recorded' : 'Approve this recovery','vote',true,`data-requires-check ${!can('vote') ? 'disabled' : ''}`)}<p class="ts-muted">${state.voters.includes(state.client) ? 'Friend PC B must review and vote from its own client.' : 'This vote belongs only to the selected PC.'}</p>`;
      return `${progress()}${content}`;
    }
    if (state.client === 'friend') content = state.copies.friend === state.version ? `<h3>Your verified copy is current</h3><p>Hosting needs a separate owner grant. You can join the current Host.</p>${button('Join current Host','join-current',true,`${state.status !== 'Ready' ? 'disabled' : ''}`)}` : `<h3>Get latest save on this PC</h3><p>Version ${state.version} · a completed save, separate from current gameplay.</p>${ack('I checked the signed group and owner identity')}${ack('Allow completed saves from this group on this PC')}${button('Allow and get save','receive',true,'data-requires-check')}`;
    else if (state.client === 'original') {
      if (state.phase === 'complete') content = `<h3>Complete transfer to Friend PC A</h3><p>The chosen PC’s signed receipt confirms exact version ${state.version}. Completing the transfer fences this PC. It does not Start the new server.</p>${ack('Transfer this world to Friend PC A and block hosting it on this PC')}${button('Complete hosting transfer','complete',true,`data-requires-check ${!can('complete') ? 'disabled' : ''}`)}`;
      else if (state.phase === 'final-copy') content = `<h3>Waiting for Friend PC A</h3><p>This server is Offline. Friend PC A must receive and confirm exact version ${state.version} before you complete the transfer.</p>${link('Review final-copy receipt','shared-receipt','archive')}`;
      else content = `<h3>Stop and prepare the final save</h3><p>Next Host: Friend PC A · Receive and Eligible host granted.</p>${note('Review the owner Stop decision. The exact recorded process must exit gracefully before its final save is published.')}<p>This synthetic run reports zero players. Current process identity and signed authority are resolved.</p>${ack('Stop this server and prepare its final completed save for Friend PC A')}${button('Stop and prepare final save','transfer',true,`data-requires-check ${!can('transfer') ? 'disabled' : ''}`)}${state.phase === 'get' ? '<p class="ts-muted">Friend PC A first needs to get a verified save and review receiving consent.</p>' : ''}`;
    }
    else if (state.copies.successor !== state.version || state.phase === 'final-copy') content = `<h3>${state.phase === 'final-copy' ? 'Get the final save' : 'Get latest save'}</h3><p>Alderheim · completed version ${state.version}. Receiving keeps a private copy; it does not transfer hosting authority.</p>${ack('I checked the signed group and owner identity')}${ack('Allow completed saves from this group on this PC')}${button(state.phase === 'final-copy' ? 'Get and confirm final save' : 'Allow and get save','receive',true,'data-requires-check')}`;
    else if (state.phase === 'handoff' || state.phase === 'complete') content = `<h3>${state.phase === 'complete' ? 'Waiting for the hosting decision' : 'Arrange the hosting transfer'}</h3><p>${state.phase === 'complete' ? 'Your final save is confirmed. Original Host PC must complete the transfer before you restore or Start here.' : 'Original Host PC must Stop and prepare the final save for this PC. The owner completes the transfer after your final-copy receipt.'}</p>${note('Getting a save or being eligible to host does not make this PC the current Host.','info')}<p class="ts-muted">Next action belongs to Original Host PC.</p>`;
    else if (state.phase === 'setup') content = `<h3>Prepare hosting on Friend PC A</h3><p>Version ${state.version} is verified and the current signed hosting decision names this PC.</p><label class="ts-field"><span>Server name</span><input value="Alderheim"></label><label class="ts-field"><span>Installed server file on this PC</span><input value="Synthetic reviewed server file" readonly></label><p class="ts-muted">The signed game version, settings and add-ons must match. Native file selection retains Windows presentation.</p>${ack('Restore into a fresh empty managed world; preserve received and existing worlds')}${ack('I reviewed matching game setup and fresh local access/password')}${button('Restore and prepare hosting','restore',true,'data-requires-check')}${link('Review game setup','shared-successor','settings')}`;
    else if (state.phase === 'route') content = `<h3>Check the new Host connection</h3><p>Fresh world restored. Another approved PC must check Friend PC A’s current signed, pinned control connection.</p>${state.client === 'successor' ? note('Next action belongs to Original Host PC or Friend PC B. A local self-check cannot satisfy this step.','info') : ''}${ack('I reviewed Friend PC A’s signed connection for Alderheim from this other approved PC')}${button('Check new Host connection','route',true,`data-requires-check ${!can('route') ? 'disabled' : ''}`)}${link('Connection check details','shared-route','link')}`;
    else if (state.phase === 'start') content = `<h3>Start Alderheim on this PC</h3><p>Exact save restored · local setup reviewed · another PC’s pinned control check completed.</p>${note('Start is manual. Game traffic, a real join and saved restart still need their own evidence.','info')}${ack('Start the prepared shared world on Friend PC A')}${button('Start server','start',true,'data-requires-check')}`;
    else if (state.phase === 'starting') content = `<h3>Start request accepted</h3><p>The game is Starting. A current status observation is needed before it is shown as Ready.</p>${button('Check server status','observe',true)}`;
    else content = `<h3>You are hosting Alderheim</h3><p>Friend PC A · Ready · started from version ${state.version}. Every simulated client identifies this PC as the Host.</p>${ack('Stop this exact server gracefully and keep its saved world')}${button('Stop server','stop',true,'data-requires-check')}`;
    return `${progress()}${content}`;
  };
  const management = () => {
    const tabs = {permissions:['Sharing', [['Sharing permissions','shared-host-grants'],['Local save consent','shared-consent'],['Signed group and owner','shared-group-trust'],['Delegated sharing management','shared-friend-grants'],['PC signing identity','shared-signing']]],copies:['Saves & receipts',[['Transfer and capacity','shared-transfer'],['Exact copy and receipt','shared-receipt'],['Source and roster repair','shared-source-review'],['Live save availability','shared-live-save'],['Interrupted live copy','shared-live-copy']]],history:['History & recovery',[['Signed hosting history','shared-history'],['Planned hosting transfer','shared-handoff'],['Host-loss recovery','shared-recovery'],['Review a recovery vote','shared-vote'],['Competing histories','shared-conflicts'],['Separate history and route','shared-separate']]],checks:['Setup & checks',[['This PC’s hosting checks','shared-successor'],['Fresh save restore','shared-restore'],['New Host connection check','shared-route'],['Connect to the new Host','shared-join-successor'],['Test a disposable game copy','shared-rehearsal'],['Staging connection rehearsal','shared-remote-rehearsal']]]};
    const active = tabs[state.tab] || tabs.permissions;
    return `<div class="ts-sharing-tabs" aria-label="Sharing detail sections">${Object.entries(tabs).map(([id,[label]]) => `<button type="button" class="ts-button" data-primary-action data-sharing-tab="${id}" aria-pressed="${state.tab === id}">${escape(label)}</button>`).join('')}</div><h3>${escape(active[0])}</h3><div class="ts-command-list">${active[1].map(([label,id]) => link(label,id)).join('')}</div>`;
  };
  setScene('normal');
  const scoped = render => () => render().replaceAll('Alderheim',escape(state.world));
  return {state,clients,can,run,setScene,landing:scoped(landing),guide:scoped(guide),management:scoped(management),hostBanner:scoped(hostBanner),hostStatus,address,owner,currentStatus,cancel:() => {state.busy=false;},localState:() => state.client === state.authority ? currentStatus() : state.processes[state.client]};
}
