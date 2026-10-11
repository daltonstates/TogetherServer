import { useEffect, useState } from 'react'
import { Button, Input, Select } from './Controls'
import { ConnectionDoctor } from './ConnectionDoctor'
import type { CompanionInfo, HostSnapshot, HostMoveKitResult, RouteDiscovery, Settings } from './contracts'
import { gameLabel } from './GameProfile'
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
type Tool = 'countdown' | 'extensions' | 'safety' | 'doctor' | 'route' | 'tcp' |
  'limits' | 'listener' | 'identity' | 'move' | 'revoked' | 'rehearsal' | 'notifications'
const titles: Record<Tool, string> = {
  countdown: 'Empty-server countdown', extensions: 'Friend timer extensions', safety: 'Remote Stop safety',
  doctor: 'Connection Doctor', route: 'Friend connection route', tcp: 'Internet and local connection checks',
  limits: 'Hosting limits', listener: 'Advanced network settings', identity: 'Secure Host identity',
  move: 'Move hosting to another PC', revoked: 'Revoked Friend PCs', rehearsal: 'Test with another owned PC', notifications: 'Server notifications'
}
const routeLabels: Record<Settings['connectionRoute']['mode'], string> = {
  DirectInternet: 'Direct Internet', PrivateMesh: 'Private mesh', AdvancedAddress: 'Advanced address'
}

export function HostSettingsTools({ section, snapshot, companion, ports, routeCheck, previousRouteVerdict,
  routeDiscovery, selectedProfileId, busy, dirty, checkingPorts, checkingRoute, detectingAddress, friendAppStatus, addressError,
  isStaging, notice, moveKit, onClearNotice, onDraftStateChange, onSave, onAutoShutdown, onRefresh, onTestRoute, onDetectAddress,
  onOpenAccess, onOpenDiagnostics, onCertificate, onInspectMoveKit, onNewHost, keepAwake }: {
  section: 'stop' | 'network' | 'advanced'
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
  const [draft, setDraft] = useState(snapshot.settings)
  const settings = snapshot.settings
  const open = (next: Tool) => { onClearNotice(); setDraft(structuredClone(settings)); setTool(next) }
  const editing = tool === 'countdown' || tool === 'extensions' || tool === 'route' || tool === 'listener' || tool === 'limits'
  const patch: HostSettingsPatch = tool === 'countdown' ? { idleMinutes: draft.idleMinutes } :
    tool === 'extensions' ? { friendTimerExtensionMinutes: draft.friendTimerExtensionMinutes, friendTimerExtensionMaximumMinutes: draft.friendTimerExtensionMaximumMinutes } :
    tool === 'limits' ? { maxConcurrentServers: draft.maxConcurrentServers } :
    tool === 'listener' ? { companionPort: draft.companionPort, companionBindAddress: draft.companionBindAddress, companionEndpoint: draft.companionEndpoint } :
    tool === 'route' ? { connectionRoute: draft.connectionRoute, companionEndpoint: draft.companionEndpoint, companionBindAddress: draft.companionBindAddress } : {}
  const changed = Object.entries(patch).some(([key, value]) => JSON.stringify(value) !== JSON.stringify(settings[key as keyof Settings]))
  useEffect(() => { onDraftStateChange(editing && changed) }, [editing, changed, onDraftStateChange])
  useEffect(() => () => { onDraftStateChange(false) }, [onDraftStateChange])
  const wholeMinutes = (value: number, maximum: number) => Number.isSafeInteger(value) && value >= 1 && value <= maximum
  const valid = tool === 'countdown' ? wholeMinutes(draft.idleMinutes, 1440) :
    tool === 'extensions' ? wholeMinutes(draft.friendTimerExtensionMinutes, 120) && wholeMinutes(draft.friendTimerExtensionMaximumMinutes, 1440) && draft.friendTimerExtensionMaximumMinutes >= draft.friendTimerExtensionMinutes :
    tool === 'limits' ? wholeMinutes(draft.maxConcurrentServers, 16) :
    tool === 'listener' ? wholeMinutes(draft.companionPort, 65535) && draft.companionPort >= 1024 : true
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
  return <section className="settings-section task-first-page">
    {section === 'stop' && <>
      <SettingsSectionHeading icon="clock" title="Stop & timer" description="Automatic shutdown and safe Friend Stop requests." />
      <div className="settings-controls"><label className="setting-toggle"><span><strong>Stop empty servers automatically</strong>
        <small>Starts after a Ready server reports exactly zero players. A positive or Unknown count cancels the countdown.</small></span>
        <Input type="checkbox" checked={settings.autoShutdownEnabled} disabled={busy} onChange={event => onAutoShutdown(event.target.checked)} /></label></div>
      {keepAwake && <div className="settings-controls"><label className="setting-toggle"><span><strong>Keep Windows awake while hosting</strong>
        <small>Prevents idle sleep while an exact managed server is running. Manual sleep can still interrupt hosting.</small>
        {keepAwake.detail && <small>{keepAwake.detail}</small>}</span><Input type="checkbox" checked={keepAwake.enabled} disabled={busy || dirty}
          onChange={event => keepAwake.onChange(event.target.checked)} /></label></div>}
      <div className="settings-card-grid">
        <SettingsCard icon="clock" title="Empty-server countdown" description="Wait before gracefully stopping an empty server."
          summary={`${settings.idleMinutes} minutes · ${settings.autoShutdownEnabled ? 'Enabled' : 'Automatic shutdown is off'}`} actionLabel="Edit countdown" disabled={busy || dirty} onOpen={() => open('countdown')} />
        <SettingsCard icon="plus" title="Friend timer extensions" description="Fixed increment and maximum for permitted PCs."
          summary={`+${settings.friendTimerExtensionMinutes} min · ${settings.friendTimerExtensionMaximumMinutes} min maximum per run`} actionLabel="Edit extensions" disabled={busy || dirty} onOpen={() => open('extensions')} />
        <SettingsCard icon="shield" title="Remote Stop safety" description="Review the rules and each server's current Stop checks."
          summary="Ready + fresh zero-player check" actionLabel="Review Stop safety" onOpen={() => open('safety')} />
      </div>
    </>}
    {section === 'network' && <>
      <SettingsSectionHeading icon="link" title="Connection help" description="Check the app connection and game route separately." />
      <div className="settings-controls"><p><strong>Friend app:</strong> {friendAppStatus}.</p>
        {addressError && <p className="warning-text" role="status">{addressError}</p>}
        {companion?.listenerWarning && <p className="warning-text" role="status">{companion.listenerWarning}</p>}</div>
      <div className="actions"><Button disabled={modalBusy} onClick={() => open('doctor')}><Icon name="plug" />Connection Doctor</Button></div>
      <div className="settings-card-grid">
        <SettingsCard icon="link" title="Friend connection route" description="Choose the networking you use to reach this Host."
          summary={routeLabels[settings.connectionRoute.mode]} actionLabel="Edit connection route" disabled={busy || dirty} onOpen={() => open('route')} />
        <SettingsCard icon="search" title="Internet and local checks" description="Public address, outside TCP test, and router target."
          summary={routeCheck ? `${previousRouteVerdict ? 'Previous result' : 'Last TCP check'}: ${routeCheck.state}` : 'Outside TCP has not been tested'}
          actionLabel="Open connection checks" onOpen={() => open('tcp')} />
        {isStaging && <SettingsCard icon="monitor" title="Test with another owned PC" description="Synthetic connection, chat, and save-transfer rehearsal."
          actionLabel="Open rehearsal" onOpen={() => open('rehearsal')} />}
      </div>
      <div className="next-action"><span>Friend app connections are <strong>{settings.companionListeningEnabled ? 'on' : 'off'}</strong>.</span>
        <Button className="text-button" onClick={onOpenAccess}>Manage access</Button></div>
    </>}
    {section === 'advanced' && <>
      <SettingsSectionHeading icon="settings" title="Advanced" description="Host limits, secure connection details, and moving PCs." />
      <div className="settings-card-grid">
        {selectedProfile && <SettingsCard icon="bell" title="Server notifications" description={`Windows alerts for ${selectedProfile.name} on this PC.`}
          actionLabel="Edit server notifications" disabled={modalBusy} onOpen={() => open('notifications')} />}
        <SettingsCard icon="server" title="Hosting limits" description="Maximum number of managed servers running at once."
          summary={`${settings.maxConcurrentServers} ${settings.maxConcurrentServers === 1 ? 'server' : 'servers'} at once`} actionLabel="Edit hosting limits" disabled={busy || dirty} onOpen={() => open('limits')} />
        <SettingsCard icon="plug" title="Advanced network settings" description="Friend app TCP port, bind IP, and secure address."
          summary={`TCP ${settings.companionPort}`} actionLabel="Edit network settings" disabled={busy || dirty} onOpen={() => open('listener')} />
        <SettingsCard icon="shield" title="Secure Host identity" description="Pinned identity and reviewed certificate rotation."
          summary={companion?.certificates ? `Valid until ${new Date(companion.certificates.activeExpiresUtc).toLocaleDateString()}` : 'Identity details unavailable'}
          actionLabel="Review Host identity" onOpen={() => open('identity')} />
        <SettingsCard icon="archive" title="Move hosting to another PC" description="Inspect a verified world move kit and set up the new Host."
          actionLabel="Open move guide" onOpen={() => open('move')} />
        {!!companion?.devices.some(device => device.revoked) && <SettingsCard icon="users" title="Revoked Friend PCs" description="PCs whose saved access has been removed."
          summary={`${companion.devices.filter(device => device.revoked).length} removed`} actionLabel="View removed PCs" onOpen={() => open('revoked')} />}
      </div>
    </>}
    {tool && <SettingsDialog title={titles[tool]} busy={modalBusy} onClose={() => setTool(null)} wide={tool === 'doctor' || tool === 'rehearsal'}
      description={editing ? 'Review your changes, then save. Cancel leaves the saved settings in place.' : undefined}
      footer={<><Button className="secondary" disabled={modalBusy} onClick={() => setTool(null)}>{editing ? 'Cancel' : 'Done'}</Button>
        {editing && <Button disabled={modalBusy || !changed || !valid} onClick={() => void save()}><Icon name={busy ? 'loader' : 'check'} />{busy ? 'Saving…' : tool === 'countdown' || tool === 'extensions' ? 'Save timer' : 'Save settings'}</Button>}</>}>
      {notice && <p className={`action-feedback ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</p>}
      {tool === 'countdown' && <div className="settings-dialog-fields"><label>Wait after the server reaches 0 players<Input type="number" min="1" max="1440" step="1" value={draft.idleMinutes} disabled={busy} onChange={event => setDraft(current => ({ ...current, idleMinutes: Number(event.target.value) }))} /><small>Minutes, from 1 to 1440.</small></label>
        <p className="helper-text">At the end, the Host checks the server again and stops only at exactly zero players. Friend app presence does not gate the timer. Added time stays saved when a count change cancels the countdown.</p></div>}
      {tool === 'extensions' && <div className="settings-dialog-fields"><label>Friend extension increment<Input type="number" min="1" max="120" step="1" value={draft.friendTimerExtensionMinutes} disabled={busy} onChange={event => setDraft(current => ({ ...current, friendTimerExtensionMinutes: Number(event.target.value) }))} /><small>Fixed minutes per approved Friend request, from 1 to 120.</small></label>
        <label>Friend extension maximum<Input type="number" min={draft.friendTimerExtensionMinutes} max="1440" step="1" value={draft.friendTimerExtensionMaximumMinutes} disabled={busy} onChange={event => setDraft(current => ({ ...current, friendTimerExtensionMaximumMinutes: Number(event.target.value) }))} /><small>Total minutes per run, up to 1440. The maximum must cover at least one increment.</small></label>
        <p className="helper-text">Grant Add shutdown time separately for each PC. Friends can add only the fixed increment, including while players are online.</p></div>}
      {tool === 'safety' && <div className="settings-dialog-stack"><p className="helper-text">A Friend Stop requires Ready, exactly zero reported players, and a second fresh count immediately before graceful Stop. Unknown or positive counts block the request. The Host owner's local Stop remains available.</p>
        <p>Remote controls are <strong>{settings.remoteControlsEnabled ? 'on' : 'paused'}</strong>.</p>
        {settings.profiles.map(profile => <div className="settings-detail-row" key={profile.id}><strong>{profile.name}</strong><span>{companion?.stopSafety[profile.id]?.reason ?? 'Player safety check unavailable.'}</span></div>)}</div>}
      {tool === 'doctor' && <PaneErrorBoundary title="Connection Doctor" resetKey={selectedProfileId}><ConnectionDoctor profile={selectedProfile}
        run={snapshot.runs.find(run => run.profileId === selectedProfile?.id)} ports={ports} routeCheck={routeCheck} devices={companion?.devices ?? []}
        busy={modalBusy} routeMode={settings.connectionRoute.mode} onRefresh={onRefresh} onTestRoute={onTestRoute}
        onOpenAccess={() => { setTool(null); onOpenAccess() }} onOpenDiagnostics={() => { setTool(null); onOpenDiagnostics() }} /></PaneErrorBoundary>}
      {tool === 'route' && <div className="settings-dialog-fields"><label>Friend route<Select value={draft.connectionRoute.mode} disabled={busy} onChange={event => changeRoute(event.target.value as Settings['connectionRoute']['mode'])}>
        <option value="DirectInternet">Direct Internet</option><option value="PrivateMesh">Private mesh</option><option value="AdvancedAddress">Advanced address</option></Select><small>Use networking you install and manage. Every route still uses pinned Host identity and per-PC permissions.</small></label>
        {draft.connectionRoute.mode !== 'DirectInternet' && <label>Selected route IPv4<Input value={draft.connectionRoute.address} disabled={busy} onChange={event => changeRoute(draft.connectionRoute.mode, event.target.value)} placeholder="100.64.0.2" /></label>}
        {draft.connectionRoute.mode === 'PrivateMesh' && <label>Detected private-network adapter<Select value="" disabled={busy} onChange={event => event.target.value && changeRoute('PrivateMesh', event.target.value)}><option value="">Choose a detected address</option>
          {routeDiscovery?.privateMeshCandidates.map(candidate => <option key={`${candidate.interfaceName}-${candidate.address}`} value={candidate.address}>{candidate.provider} · {candidate.address} · {candidate.interfaceName}</option>)}</Select></label>}
        <p className="helper-text">TogetherServer reads adapters and saves your selected route. It does not install clients or change firewall, router, DNS, or network policies.</p></div>}
      {tool === 'tcp' && <div className="settings-dialog-stack"><div className="settings-detail-row"><strong>Public game address</strong><span>{settings.publicGameIp && settings.publicGameIpCheckedUtc && Date.now() - Date.parse(settings.publicGameIpCheckedUtc) < 3600000 ? `${settings.publicGameIp} · address hint; Friend join untested` : 'Address unavailable or stale'}</span></div>
        {addressError && <p className="warning-text" role="status">{addressError}</p>}
        <div className="actions"><Button className="secondary" disabled={modalBusy} onClick={onDetectAddress}><Icon name={detectingAddress ? 'loader' : 'refresh'} />{detectingAddress ? 'Refreshing…' : 'Refresh public address'}</Button>
          <Button className="secondary" disabled={modalBusy || dirty} onClick={onTestRoute}><Icon name={checkingRoute ? 'loader' : 'search'} />{checkingRoute ? 'Testing TCP port…' : 'Test Friend app port from internet'}</Button></div>
        <p className="helper-text">The outside TCP check uses portchecker.io, which sees this PC's public IP and port. No code or saved access is sent.</p>
        {routeCheck && <div className="settings-detail-row" role="status"><strong>{previousRouteVerdict ? 'Previous TCP result' : routeCheck.state} · TCP {routeCheck.port}</strong><span>{routeCheck.detail}</span><small>Checked {new Date(routeCheck.checkedUtc).toLocaleString()}.{previousRouteVerdict && ' This result is stale; test again.'} TCP reachability does not prove pinned pairing or a game join.</small></div>}
        {ports?.control.lanForwardDetail && <div className="settings-detail-row"><strong>Router forwarding target on this PC</strong><span>{ports.control.lanForwardDetail}</span>{ports.control.lanAddresses?.map(item => <small key={`${item.interfaceName}-${item.address}`}>{item.address} · {item.interfaceName} · gateway {item.gateway}</small>)}</div>}
        <p className="helper-text">Friend app HTTPS uses TCP {settings.companionPort}. Game ports are separate; Friends connect outbound. Test app connection and game join from another network separately.</p></div>}
      {tool === 'limits' && <div className="settings-dialog-fields"><label>Maximum servers running at once<Input type="number" min="1" max="16" step="1" value={draft.maxConcurrentServers} disabled={busy} onChange={event => setDraft(current => ({ ...current, maxConcurrentServers: Number(event.target.value) }))} /><small>Most homes should leave this at 1. Each running world still needs distinct ports and save storage.</small></label></div>}
      {tool === 'listener' && <div className="settings-dialog-fields"><label>Friend app TCP port<Input type="number" min="1024" max="65535" step="1" value={draft.companionPort} disabled={busy} onChange={event => {
        const port = Number(event.target.value)
        let endpoint = draft.companionEndpoint
        if (endpoint) try { const url = new URL(endpoint); url.port = String(port); endpoint = url.origin } catch { /* Saved validation reports an invalid address. */ }
        setDraft(current => ({ ...current, companionPort: port, companionEndpoint: endpoint }))
      }} /></label><label>Custom secure address<Input value={draft.companionEndpoint} disabled={busy} onChange={event => setDraft(current => ({ ...current, companionEndpoint: event.target.value.trim() }))} placeholder="https://127.0.0.1:5131" /></label>
        <label>Bind IP<Input value={draft.companionBindAddress} disabled={busy} onChange={event => setDraft(current => ({ ...current, companionBindAddress: event.target.value }))} placeholder="127.0.0.1" /></label></div>}
      {tool === 'identity' && <div className="settings-dialog-stack">{companion?.fingerprint && <p className="helper-text">Pinned Host identity: <code>{companion.fingerprint}</code></p>}
        {companion?.certificates ? <><p>Current identity is valid until {new Date(companion.certificates.activeExpiresUtc).toLocaleString()}.</p>
          <p className="helper-text">{companion.certificates.nextFingerprint ? 'The next identity is ready and connected Friends are receiving it.' : 'TogetherServer prepares the next identity automatically within 30 days of expiry.'}</p>
          {companion.certificates.previousAcceptedUntilUtc && <p className="helper-text">The old identity will be accepted until {new Date(companion.certificates.previousAcceptedUntilUtc).toLocaleString()}.</p>}
          <div className="actions"><Button className="secondary" disabled={busy || !!companion.certificates.nextFingerprint} onClick={() => onCertificate('stage')}>Prepare next identity</Button>
            <Button className="secondary" disabled={busy || !companion.certificates.nextFingerprint} onClick={() => onCertificate('activate')}>Use prepared identity</Button>
            {companion.certificates.previousFingerprint && <Button className="danger-outline" disabled={busy} onClick={() => onCertificate('retire-previous')}>Stop accepting old identity</Button>}</div></> : <p className="helper-text">Secure identity details are unavailable.</p>}</div>}
      {tool === 'move' && <div className="settings-dialog-stack"><p>On the old Host, stop the server, make a fresh backup, and choose Prepare move kit beside it. Take the verified .backup folder to this PC.</p>
        <div className="actions"><Button className="secondary" disabled={busy} onClick={onInspectMoveKit}><Icon name="search" />Inspect move kit on this PC</Button></div>
        {moveKit?.kit && <><div className="settings-detail-row"><strong>{moveKit.kit.name} · {gameLabel(moveKit.kit.kind)}</strong><span>World {moveKit.kit.worldId} · port {moveKit.kit.gamePort} · {moveKit.fileCount} verified files</span></div>
          <ol><li>Install the same game server version and create a fresh server on this PC.</li><li>While it is offline, copy the kit's payload into the new save location. Keep existing saves separate.</li><li>Choose the locally installed executable, recreate settings and passwords, and test a real join and saved change.</li><li>Pair Friend PCs again. Retire the old Host after the new one passes those checks.</li></ol>
          <div className="actions"><Button className="secondary" disabled={busy || dirty} onClick={() => { setTool(null); onNewHost() }}>Set up new Host</Button></div></>}
        <p className="helper-text">The kit contains world files and basic setup, with no game binaries, Friend credentials, private keys, or passwords.</p></div>}
      {tool === 'revoked' && <div className="settings-dialog-stack">{companion?.devices.filter(device => device.revoked).map(device => <div className="settings-detail-row" key={device.id}><strong>{device.name}</strong><span>Access removed</span></div>)}</div>}
      {tool === 'rehearsal' && <HostRemoteRehearsal />}
      {tool === 'notifications' && selectedProfile && <ServerNotificationPreferences
        server={{ id: selectedProfile.id, name: selectedProfile.name }} onBusyChange={setChildBusy} />}
      {editing && !valid && <p className="warning-text" role="status">{tool === 'extensions' && draft.friendTimerExtensionMaximumMinutes < draft.friendTimerExtensionMinutes ? 'The maximum must cover at least one extension increment.' : 'Enter a whole number within the range shown above.'}</p>}
    </SettingsDialog>}
  </section>
}
