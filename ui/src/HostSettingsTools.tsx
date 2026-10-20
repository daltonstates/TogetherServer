import { useEffect, useState } from 'react'
import { Button, Input, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { ConnectionDoctor } from './ConnectionDoctor'
import type { CompanionInfo, HostSnapshot, HostMoveKitResult, RouteDiscovery, Settings } from './contracts'
import { gameLabel } from './GameProfile'
import { orderProfiles } from './profileLists'
import { Icon } from './Icon'
import { PaneErrorBoundary } from './PaneErrorBoundary'
import { HostRemoteRehearsal } from './RemoteRehearsal'
import type { InternetRouteCheck, PortDiagnostics } from './ServerReadiness'
import { SettingsCard, SettingsDialog, SettingsSectionHeading } from './SettingsPresentation'
import { ServerNotificationPreferences } from './DesktopPreferencesPanel'
import './TaskFirstPages.css'

export type HostSettingsPatch = Partial<Pick<Settings, 'idleMinutes' | 'friendTimerExtensionMinutes' |
  'friendTimerExtensionMaximumMinutes' | 'maxConcurrentServers' | 'companionPort' |
  'companionEndpoint' | 'companionBindAddress' | 'connectionRoute'>>
type Tool = 'countdown' | 'doctor' | 'route' | 'limits' | 'identity' | 'move' | 'rehearsal' | 'notifications'
const titles: Record<Tool, string> = {
  countdown: 'Empty-server countdown', doctor: 'Troubleshoot connection', route: 'Address and ports',
  limits: 'Hosting limits', identity: 'Secure Host identity',
  move: 'Move hosting to another PC', rehearsal: 'Test with another owned PC', notifications: 'Server notifications'
}
const routeLabels: Record<Settings['connectionRoute']['mode'], string> = {
  DirectInternet: 'Direct Internet', PrivateMesh: 'Private mesh', AdvancedAddress: 'Advanced address'
}

export function HostSettingsTools({ section, snapshot, companion, ports, routeCheck, previousRouteVerdict,
  routeDiscovery, selectedProfileId, busy, dirty, checkingPorts, checkingRoute, detectingAddress, friendAppStatus, addressError,
  isStaging, notice, moveKit, onClearNotice, onDraftStateChange, onSave, onAutoShutdown, onRefresh, onTestRoute, onDetectAddress,
  onOpenAccess, onOpenDiagnostics, onCertificate, onInspectMoveKit, onNewHost, keepAwake, guideChecks = false }: {
  section: 'stop' | 'network' | 'advanced'
  guideChecks?: boolean
  snapshot: HostSnapshot
  companion: CompanionInfo | null
  ports: PortDiagnostics | null
  routeCheck: InternetRouteCheck | null
  previousRouteVerdict: boolean
  routeDiscovery: RouteDiscovery | null
  selectedProfileId: string
  busy: boolean
  dirty: boolean
  checkingPorts: boolean
  checkingRoute: boolean
  detectingAddress: boolean
  friendAppStatus: string
  addressError: string | null
  isStaging: boolean
  notice: { good: boolean; text: string } | null
  onClearNotice: () => void
  onDraftStateChange: (dirty: boolean) => void
  moveKit: HostMoveKitResult | null
  onSave: (patch: HostSettingsPatch) => Promise<boolean>
  onAutoShutdown: (enabled: boolean) => void
  onRefresh: () => void
  onTestRoute: () => void
  onDetectAddress: () => void
  onOpenAccess: () => void
  onOpenDiagnostics: () => void
  onCertificate: (action: 'stage' | 'activate' | 'retire-previous') => void
  onInspectMoveKit: () => void
  onNewHost: () => void
  keepAwake?: { enabled: boolean; detail?: string; onChange: (enabled: boolean) => void }
}) {
  const [tool, setTool] = useState<Tool | null>(null)
  const [childBusy, setChildBusy] = useState(false)
  const [troubleshootingOpen, setTroubleshootingOpen] = useState(false)
  const [networkChecksOpen, setNetworkChecksOpen] = useState(false)
  const [networkDetailsOpen, setNetworkDetailsOpen] = useState(false)
  const [extensionsOpen, setExtensionsOpen] = useState(false)
  const [draft, setDraft] = useState(snapshot.settings)
  const [editingBaseline, setEditingBaseline] = useState(snapshot.settings)
  const settings = snapshot.settings
  const open = (next: Tool) => {
    onClearNotice(); setDraft(structuredClone(settings)); setEditingBaseline(structuredClone(settings)); setNetworkChecksOpen(false)
    setNetworkDetailsOpen(false); setExtensionsOpen(false); setTool(next)
  }
  const editing = tool === 'countdown' || tool === 'route' || tool === 'limits'
  const timerPatch = Object.fromEntries((['idleMinutes', 'friendTimerExtensionMinutes', 'friendTimerExtensionMaximumMinutes'] as const)
    .filter(key => draft[key] !== editingBaseline[key]).map(key => [key, draft[key]]))
  const networkPatch = Object.fromEntries((['connectionRoute', 'companionPort', 'companionEndpoint', 'companionBindAddress'] as const)
    .filter(key => JSON.stringify(draft[key]) !== JSON.stringify(editingBaseline[key])).map(key => [key, draft[key]]))
  const patch: HostSettingsPatch = tool === 'countdown' ? timerPatch :
    tool === 'limits' ? { maxConcurrentServers: draft.maxConcurrentServers } :
    tool === 'route' ? networkPatch : {}
  const changed = Object.entries(patch).some(([key, value]) => JSON.stringify(value) !== JSON.stringify(settings[key as keyof Settings]))
  useEffect(() => { onDraftStateChange(editing && changed) }, [editing, changed, onDraftStateChange])
  useEffect(() => () => { onDraftStateChange(false) }, [onDraftStateChange])
  const wholeMinutes = (value: number, maximum: number) => Number.isSafeInteger(value) && value >= 1 && value <= maximum
  const valid = tool === 'countdown' ? wholeMinutes(draft.idleMinutes, 1440) && wholeMinutes(draft.friendTimerExtensionMinutes, 120)
      && wholeMinutes(draft.friendTimerExtensionMaximumMinutes, 1440) && draft.friendTimerExtensionMaximumMinutes >= draft.friendTimerExtensionMinutes :
    tool === 'limits' ? wholeMinutes(draft.maxConcurrentServers, 16) :
    tool === 'route' ? wholeMinutes(draft.companionPort, 65535) && draft.companionPort >= 1024 : true
  const selectedProfile = settings.profiles.find(profile => profile.id === selectedProfileId) ?? settings.profiles[0]
  const modalBusy = busy || checkingPorts || checkingRoute || detectingAddress || childBusy
  const save = async () => { if (valid && changed && await onSave(patch)) setTool(null) }
  const changeRoute = (mode: Settings['connectionRoute']['mode'], address = '') => {
    const selected = address.trim()
    const endpoint = selected ? `https://${selected}:${draft.companionPort}` :
      mode === 'DirectInternet' && settings.publicGameIp ? `https://${settings.publicGameIp}:${draft.companionPort}` : draft.companionEndpoint
    setDraft(current => ({ ...current, connectionRoute: { mode, address: selected }, companionEndpoint: endpoint,
      companionBindAddress: mode === 'DirectInternet' ? '0.0.0.0' : selected || current.companionBindAddress }))
  }
  const changePort = (port: number) => {
    let endpoint = draft.companionEndpoint
    if (endpoint) try { const url = new URL(endpoint); url.port = String(port); endpoint = url.origin } catch { /* Saved validation reports an invalid address. */ }
    setDraft(current => ({ ...current, companionPort: port, companionEndpoint: endpoint }))
  }
  return <section className="settings-section host-settings-tools task-first-page">
    {section === 'stop' && <>
      <SettingsSectionHeading icon="clock" title="Shutdown" description="Choose when empty servers stop." />
      <label className="setting-toggle"><span><strong>Stop empty servers automatically</strong>
        <small>The timer starts when a ready server reports 0 players. Players joining or an unknown count cancel it.</small></span>
        <Input type="checkbox" checked={settings.autoShutdownEnabled} disabled={busy} onChange={event => onAutoShutdown(event.target.checked)} /></label>
      {keepAwake && <label className="setting-toggle"><span><strong>Keep Windows awake while hosting</strong>
        <small>Prevents idle sleep while your server runs. Putting the PC to sleep yourself can still interrupt hosting.</small>
        {keepAwake.detail && <small>{keepAwake.detail}</small>}</span><Input type="checkbox" checked={keepAwake.enabled} disabled={busy || dirty}
          onChange={event => keepAwake.onChange(event.target.checked)} /></label>}
      <div className="settings-card-grid">
        <SettingsCard icon="clock" title="Empty-server countdown" description="Wait before gracefully stopping an empty server."
          summary={`${settings.idleMinutes} minutes · ${settings.autoShutdownEnabled ? 'Enabled' : 'Automatic shutdown is off'}`} actionLabel="Edit countdown" disabled={busy || dirty} onOpen={() => open('countdown')} />
      </div>
      <details className="advanced-block"><summary>How safe shutdown works</summary>
        <p className="helper-text">Friends and the timer can stop a server only when it is ready and reports 0 players. TogetherServer checks again just before stopping. An unknown count blocks Stop. You can still stop it yourself.</p>
        <p>Remote controls are <strong>{settings.remoteControlsEnabled ? 'on' : 'paused'}</strong>.</p>
        {orderProfiles(settings.profiles, snapshot.runs).map(profile => <div className="settings-detail-row" key={profile.id}><strong>{profile.name}</strong><span>{companion?.stopSafety[profile.id]?.reason ?? 'Player safety check unavailable.'}</span></div>)}
      </details>
    </>}
    {section === 'network' && <>
      <SettingsSectionHeading icon="link" title="Network" description="The address and ports friends use to reach this PC." />
      <div className="settings-controls"><p><strong>Friend app:</strong> {friendAppStatus}.</p>
        {addressError && <DismissibleNotice as="p" scope={settings.companionEndpoint} className="warning-text" role="status">{addressError}</DismissibleNotice>}
        {companion?.listenerWarning && <DismissibleNotice as="p" scope={settings.companionEndpoint} className="warning-text" role="status">{companion.listenerWarning}</DismissibleNotice>}</div>
      <div className="settings-card-grid">
        <SettingsCard icon="link" title="Address and ports" description="Choose a network route and Friend app port. Game ports are set on each server."
          summary={`${routeLabels[settings.connectionRoute.mode]} · TCP ${settings.companionPort}`} actionLabel="Edit network" disabled={busy || dirty} onOpen={() => open('route')} />
      </div>
      <div className="next-action"><span>Friend app connections are <strong>{settings.companionListeningEnabled ? 'on' : 'off'}</strong>.</span>
        <Button className="text-button" onClick={onOpenAccess}>Manage access</Button></div>
      <details className="advanced-block" open={troubleshootingOpen || guideChecks} onToggle={event => setTroubleshootingOpen(event.currentTarget.open)}>
        <summary>Troubleshooting</summary>
        {(troubleshootingOpen || guideChecks) && <><p className="helper-text">Use these checks if a friend cannot connect. App access and joining the game are checked separately.</p>
          <div className="actions"><Button className="secondary" data-guide="host-checks" disabled={modalBusy} onClick={() => open('doctor')}><Icon name="search" />Troubleshoot connection</Button>
            <Button className="text-button" disabled={modalBusy} onClick={() => open('identity')}><Icon name="shield" />Secure Host identity</Button></div>
          {isStaging && <SettingsCard icon="monitor" title="Test with another owned PC" description="Synthetic connection, chat, and save-transfer rehearsal."
            actionLabel="Open rehearsal" onOpen={() => open('rehearsal')} />}</>}
      </details>
    </>}
    {section === 'advanced' && <>
      <SettingsSectionHeading icon="settings" title="Advanced" description="Hosting limits, server alerts, and moving to another PC." />
      <div className="settings-card-grid">
        {selectedProfile && <SettingsCard icon="bell" title="Server notifications" description={`Windows alerts for ${selectedProfile.name} on this PC.`}
          actionLabel="Edit server notifications" disabled={modalBusy} onOpen={() => open('notifications')} />}
        <SettingsCard icon="server" title="Hosting limits" description="How many servers TogetherServer can run at once."
          summary={`${settings.maxConcurrentServers} ${settings.maxConcurrentServers === 1 ? 'server' : 'servers'} at once`} actionLabel="Edit hosting limits" disabled={busy || dirty} onOpen={() => open('limits')} />
        <SettingsCard icon="archive" title="Move hosting to another PC" description="Inspect a verified world move kit and set up the new Host."
          actionLabel="Open move guide" onOpen={() => open('move')} />
      </div>
    </>}
    {tool && <SettingsDialog title={titles[tool]} busy={modalBusy} onClose={() => setTool(null)} wide={tool === 'doctor' || tool === 'rehearsal'}
      description={editing ? 'Review your changes, then save.' : undefined}
      footer={<><Button className="secondary" disabled={modalBusy} onClick={() => setTool(null)}>{editing ? 'Cancel' : 'Done'}</Button>
        {editing && <Button disabled={modalBusy || !changed || !valid} onClick={() => void save()}><Icon name={busy ? 'loader' : 'check'} />{busy ? 'Saving…' : tool === 'countdown' ? 'Save timer' : 'Save settings'}</Button>}</>}>
      {notice && <DismissibleNotice as="p" scope={tool} className={`action-feedback ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</DismissibleNotice>}
      {tool === 'countdown' && <div className="settings-dialog-fields"><label>Wait after the server reaches 0 players<Input type="number" min="1" max="1440" step="1" value={draft.idleMinutes} disabled={busy} onChange={event => setDraft(current => ({ ...current, idleMinutes: Number(event.target.value) }))} /><small>Minutes, from 1 to 1440.</small></label>
        <p className="helper-text">At the end, the Host checks the server again and stops only at exactly zero players. Added time stays saved when a count change cancels the countdown.</p>
        <details className="advanced-block" open={extensionsOpen} onToggle={event => setExtensionsOpen(event.currentTarget.open)}><summary>Allow friends to add time</summary>
          {extensionsOpen && <div className="settings-dialog-fields"><label>Minutes added per request<Input type="number" min="1" max="120" step="1" value={draft.friendTimerExtensionMinutes} disabled={busy} onChange={event => setDraft(current => ({ ...current, friendTimerExtensionMinutes: Number(event.target.value) }))} /><small>From 1 to 120 minutes.</small></label>
            <label>Maximum minutes friends can add<Input type="number" min={draft.friendTimerExtensionMinutes} max="1440" step="1" value={draft.friendTimerExtensionMaximumMinutes} disabled={busy} onChange={event => setDraft(current => ({ ...current, friendTimerExtensionMaximumMinutes: Number(event.target.value) }))} /><small>Total per server session, up to 1440. Allow enough for at least one request.</small></label>
            <p className="helper-text">Give each PC Add shutdown time permission in Access. Friends add the set number of minutes, even while players are online.</p></div>}
        </details></div>}
      {tool === 'route' && <div className="settings-dialog-fields"><label>Network route<Select value={draft.connectionRoute.mode} disabled={busy} onChange={event => changeRoute(event.target.value as Settings['connectionRoute']['mode'])}>
        <option value="DirectInternet">Direct Internet</option><option value="PrivateMesh">Private mesh</option><option value="AdvancedAddress">Advanced address</option></Select><small>Set up the network yourself. TogetherServer verifies the Host and checks each PC’s permissions on every route.</small></label>
        {draft.connectionRoute.mode !== 'DirectInternet' && <label>Network IPv4 address<Input value={draft.connectionRoute.address} disabled={busy} onChange={event => changeRoute(draft.connectionRoute.mode, event.target.value)} placeholder="100.64.0.2" /></label>}
        {draft.connectionRoute.mode === 'PrivateMesh' && <label>Detected private-network adapter<Select value="" disabled={busy} onChange={event => event.target.value && changeRoute('PrivateMesh', event.target.value)}><option value="">Choose a detected address</option>
          {routeDiscovery?.privateMeshCandidates.map(candidate => <option key={`${candidate.interfaceName}-${candidate.address}`} value={candidate.address}>{candidate.provider} · {candidate.address} · {candidate.interfaceName}</option>)}</Select></label>}
        <label>Friend app TCP port<Input type="number" min="1024" max="65535" step="1" value={draft.companionPort} disabled={busy} onChange={event => changePort(Number(event.target.value))} /><small>Game ports belong to each server's setup.</small></label>
        <details className="advanced-block" open={networkDetailsOpen} onToggle={event => setNetworkDetailsOpen(event.currentTarget.open)}><summary>Advanced address settings</summary>
          {networkDetailsOpen && <div className="settings-dialog-fields"><label>Custom secure address<Input value={draft.companionEndpoint} disabled={busy} onChange={event => setDraft(current => ({ ...current, companionEndpoint: event.target.value.trim() }))} placeholder="https://127.0.0.1:5131" /></label>
            <label>Bind IP<Input value={draft.companionBindAddress} disabled={busy} onChange={event => setDraft(current => ({ ...current, companionBindAddress: event.target.value }))} placeholder="127.0.0.1" /></label></div>}
        </details><p className="helper-text">TogetherServer reads adapters and saves your selected route. It does not install clients or change firewall, router, DNS, or network policies.</p></div>}
      {tool === 'doctor' && <><PaneErrorBoundary title="Connection help" resetKey={selectedProfileId}><ConnectionDoctor profile={selectedProfile}
        run={snapshot.runs.find(run => run.profileId === selectedProfile?.id)} ports={ports} routeCheck={routeCheck} devices={companion?.devices ?? []}
        busy={modalBusy} routeMode={settings.connectionRoute.mode} onRefresh={onRefresh} onTestRoute={onTestRoute}
        onOpenAccess={() => { setTool(null); onOpenAccess() }} onOpenDiagnostics={() => { setTool(null); onOpenDiagnostics() }} /></PaneErrorBoundary>
        <details className="advanced-block" open={networkChecksOpen} onToggle={event => setNetworkChecksOpen(event.currentTarget.open)}><summary>Address and port check details</summary>
          {networkChecksOpen && <div className="settings-dialog-stack"><div className="settings-detail-row"><strong>Public game address</strong><span>{settings.publicGameIp && settings.publicGameIpCheckedUtc && Date.now() - Date.parse(settings.publicGameIpCheckedUtc) < 3600000 ? `${settings.publicGameIp} · address hint; Friend join untested` : 'Address unavailable or stale'}</span></div>
            {addressError && <DismissibleNotice as="p" scope={settings.companionEndpoint} className="warning-text" role="status">{addressError}</DismissibleNotice>}
            <div className="actions"><Button className="secondary" aria-label="Refresh public address" disabled={modalBusy} onClick={onDetectAddress}><Icon name={detectingAddress ? 'loader' : 'refresh'} />{detectingAddress ? 'Refreshing…' : 'Refresh address'}</Button>
              <Button className="secondary" aria-label="Test Friend app port from internet" disabled={modalBusy || dirty} onClick={onTestRoute}><Icon name={checkingRoute ? 'loader' : 'search'} />{checkingRoute ? 'Testing…' : 'Test internet access'}</Button></div>
            <p className="helper-text">The outside TCP check uses portchecker.io, which sees this PC's public IP and port. No code or saved access is sent.</p>
            {routeCheck && <DismissibleNotice scope={settings.companionEndpoint} noticeKey={`${routeCheck.checkedUtc}:${routeCheck.state}:${previousRouteVerdict}`} trackMessage={false} className="settings-detail-row" role="status"><strong>{previousRouteVerdict ? 'Previous port test' : routeCheck.state} · TCP {routeCheck.port}</strong><span>{routeCheck.detail}</span><small>Checked {new Date(routeCheck.checkedUtc).toLocaleString()}.{previousRouteVerdict && ' This result is old; test again.'} An open port does not prove a secure app connection or game join.</small></DismissibleNotice>}
            {ports?.control.lanForwardDetail && <div className="settings-detail-row"><strong>Router forwarding target on this PC</strong><span>{ports.control.lanForwardDetail}</span>{ports.control.lanAddresses?.map(item => <small key={`${item.interfaceName}-${item.address}`}>{item.address} · {item.interfaceName} · gateway {item.gateway}</small>)}</div>}
            <p className="helper-text">Friend app HTTPS uses TCP {settings.companionPort}. Game ports are separate; Friends connect outbound. Test app connection and game join from another network separately.</p></div>}
        </details></>}
      {tool === 'limits' && <div className="settings-dialog-fields"><label>Maximum servers running at once<Input type="number" min="1" max="16" step="1" value={draft.maxConcurrentServers} disabled={busy} onChange={event => setDraft(current => ({ ...current, maxConcurrentServers: Number(event.target.value) }))} /><small>Most homes should leave this at 1. Each running world still needs distinct ports and save storage.</small></label></div>}
      {tool === 'identity' && <div className="settings-dialog-stack">{companion?.fingerprint && <p className="helper-text">Pinned Host identity: <code>{companion.fingerprint}</code></p>}
        {companion?.certificates ? <><p>Current identity is valid until {new Date(companion.certificates.activeExpiresUtc).toLocaleString()}.</p>
          <p className="helper-text">{companion.certificates.nextFingerprint ? 'The next identity is ready and connected Friends are receiving it.' : 'TogetherServer prepares the next identity automatically within 30 days of expiry.'}</p>
          {companion.certificates.previousAcceptedUntilUtc && <p className="helper-text">The old identity will be accepted until {new Date(companion.certificates.previousAcceptedUntilUtc).toLocaleString()}.</p>}
          <div className="actions"><Button className="secondary" disabled={busy || !!companion.certificates.nextFingerprint} onClick={() => onCertificate('stage')}><Icon name="plus" />Prepare next identity</Button>
            <Button className="secondary" disabled={busy || !companion.certificates.nextFingerprint} onClick={() => onCertificate('activate')}><Icon name="check" />Use prepared identity</Button>
            {companion.certificates.previousFingerprint && <Button className="danger-outline" disabled={busy} onClick={() => onCertificate('retire-previous')}><Icon name="shield" />Stop accepting old identity</Button>}</div></> : <p className="helper-text">Secure identity details are unavailable.</p>}</div>}
      {tool === 'move' && <div className="settings-dialog-stack"><p>On the old Host, stop the server, make a fresh backup, and choose Prepare move kit beside it. Take the verified .backup folder to this PC.</p>
        <div className="actions"><Button className="secondary" aria-label="Inspect move kit on this PC" disabled={busy} onClick={onInspectMoveKit}><Icon name="search" />Inspect move kit</Button></div>
        {moveKit?.kit && <><div className="settings-detail-row"><strong>{moveKit.kit.name} · {gameLabel(moveKit.kit.kind)}</strong><span>World {moveKit.kit.worldId} · port {moveKit.kit.gamePort} · {moveKit.fileCount} verified files</span></div>
          <ol><li>Install the same game server version and create a fresh server on this PC.</li><li>While it is offline, copy the kit's payload into the new save location. Keep existing saves separate.</li><li>Choose the locally installed executable, recreate settings and passwords, and test a real join and saved change.</li><li>Pair Friend PCs again. Retire the old Host after the new one passes those checks.</li></ol>
          <div className="actions"><Button className="secondary" disabled={busy || dirty} onClick={() => { setTool(null); onNewHost() }}>Set up new Host</Button></div></>}
        <p className="helper-text">The kit contains world files and basic setup, with no game binaries, Friend credentials, private keys, or passwords.</p></div>}
      {tool === 'rehearsal' && <HostRemoteRehearsal />}
      {tool === 'notifications' && selectedProfile && <ServerNotificationPreferences
        server={{ id: selectedProfile.id, name: selectedProfile.name }} onBusyChange={setChildBusy} />}
      {editing && !valid && <DismissibleNotice as="p" scope={tool} className="warning-text" role="status">{tool === 'countdown' && draft.friendTimerExtensionMaximumMinutes < draft.friendTimerExtensionMinutes ? 'The maximum must allow at least one request.' : 'Enter a whole number within the range shown above.'}</DismissibleNotice>}
    </SettingsDialog>}
  </section>
}
