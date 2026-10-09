import { useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { SharedWorldRouteDetails } from './SharedWorldRouteDetails'
import { SharedWorldServerFile } from './SharedWorldServerFile'
import { createSharedRouteDetails, futureHostFields, nextHostingStep, normalizedDirectIpHttpsEndpoint,
  type HostingStepDestination, type SharedGame } from './sharedWorldUx'

type Readiness = { ready: boolean; reasons: string[]; version: number | null;
  versionHash: string | null; rehearsalPassed: boolean; managedProcessRehearsalPassed?: boolean }
type RouteCheck = { controlRouteObserved: boolean; code: string; message: string }
type RequiredAddOn = { name: string; version: string; requiredGameVersion: string;
  type: string; id: string | null }
type RestoreStatus = { staged: boolean; restored: boolean; recordHash: string | null;
  message: string; pendingChecks: string[]; preparedServerRoot: string | null;
  readyForManualStart: boolean; requiredAddOns: RequiredAddOn[];
  controlRouteFingerprint: string | null; controlRouteAddress: string | null }
type RestoreResult = { ok: boolean; code: string; message: string;
  pendingChecks: string[] | null }

function parseRestoreStatus(value: unknown): RestoreStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Handoff status is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.staged !== 'boolean' || typeof item.restored !== 'boolean' ||
    typeof item.readyForManualStart !== 'boolean' ||
    (item.recordHash !== null && (typeof item.recordHash !== 'string' || !/^[0-9A-F]{64}$/.test(item.recordHash))) ||
    typeof item.message !== 'string' || !Array.isArray(item.pendingChecks) ||
    item.pendingChecks.some(check => typeof check !== 'string') ||
    (item.requiredAddOns !== null && (!Array.isArray(item.requiredAddOns) ||
      item.requiredAddOns.length > 128 ||
      item.requiredAddOns.some(addOn => !addOn || typeof addOn !== 'object' ||
      typeof addOn.name !== 'string' || typeof addOn.version !== 'string' ||
      typeof addOn.requiredGameVersion !== 'string' || typeof addOn.type !== 'string' ||
      (addOn.id !== null && typeof addOn.id !== 'string')))) ||
    (item.preparedServerRoot !== null && item.preparedServerRoot !== undefined &&
      typeof item.preparedServerRoot !== 'string') ||
    (item.controlRouteFingerprint !== null && item.controlRouteFingerprint !== undefined &&
      (typeof item.controlRouteFingerprint !== 'string' ||
        !/^[0-9A-F]{64}$/.test(item.controlRouteFingerprint))) ||
    (item.controlRouteAddress != null && (typeof item.controlRouteAddress !== 'string' ||
      normalizedDirectIpHttpsEndpoint(item.controlRouteAddress) === null))) throw new Error('Handoff status is invalid.')
  return { ...item, requiredAddOns: item.requiredAddOns ?? [], controlRouteAddress: item.controlRouteAddress ?? null } as RestoreStatus
}

function parseRestoreResult(value: unknown): RestoreResult {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Restore result is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.ok !== 'boolean' || typeof item.code !== 'string' ||
    typeof item.message !== 'string' ||
    (item.pendingChecks !== null && item.pendingChecks !== undefined &&
      (!Array.isArray(item.pendingChecks) || item.pendingChecks.some(check => typeof check !== 'string'))))
    throw new Error('Restore result is invalid.')
  return item as RestoreResult
}

function parseStageResult(value: unknown): { ok: boolean; message: string } {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Handoff response is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.ok !== 'boolean' || typeof item.message !== 'string')
    throw new Error('Handoff response is invalid.')
  return item as { ok: boolean; message: string }
}

export function parseTakeoverReadiness(value: unknown): Readiness {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Readiness is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.ready !== 'boolean' || !Array.isArray(item.reasons) ||
    item.reasons.some(reason => typeof reason !== 'string' || reason.length > 300) ||
    (item.version !== null && (typeof item.version !== 'number' || !Number.isSafeInteger(item.version))) ||
    (item.versionHash !== null && (typeof item.versionHash !== 'string' || !/^[0-9A-F]{64}$/.test(item.versionHash))) ||
    typeof item.rehearsalPassed !== 'boolean' ||
    (item.managedProcessRehearsalPassed !== undefined && typeof item.managedProcessRehearsalPassed !== 'boolean'))
    throw new Error('Readiness is invalid.')
  return item as Readiness
}

function parseRouteCheck(value: unknown): RouteCheck {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Route check is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.controlRouteObserved !== 'boolean' || typeof item.code !== 'string' ||
    typeof item.message !== 'string' || item.message.length > 500) throw new Error('Route check is invalid.')
  return item as RouteCheck
}

export function SharedWorldReadinessPanel({ profileId, onHostingSetupChange, game, onBrowseServerFile, onOpenHostSetup }:
  { profileId: string; onHostingSetupChange?: (ready: boolean) => void; game?: SharedGame;
    onBrowseServerFile?: (game: SharedGame) => Promise<string | null>; onOpenHostSetup?: () => void }) {
  const fields = futureHostFields(game)
  const knownGame = ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(game ?? '')
  const scope = useRef(profileId)
  scope.current = profileId
  const panel = useRef<HTMLDetailsElement>(null)
  const routePanel = useRef<HTMLDetailsElement>(null)
  const [serverFile, setServerFile] = useState('')
  const [gameVersion, setGameVersion] = useState('')
  const [passwordSet, setPasswordSet] = useState(false)
  const [controlPort, setControlPort] = useState('5131')
  const [gamePort, setGamePort] = useState(fields.gamePort)
  const [serverName, setServerName] = useState('Recovered world')
  const [executable, setExecutable] = useState('')
  const [preparedServerRoot, setPreparedServerRoot] = useState('')
  const [factorioRconPort, setFactorioRconPort] = useState('27015')
  const [gamePassword, setGamePassword] = useState('')
  const [handoff, setHandoff] = useState<RestoreStatus | null>(null)
  const [restoreResult, setRestoreResult] = useState<RestoreResult | null>(null)
  const [result, setResult] = useState<Readiness | null>(null)
  const [routeRecordHash, setRouteRecordHash] = useState('')
  const [routeFingerprint, setRouteFingerprint] = useState('')
  const [routeResult, setRouteResult] = useState<RouteCheck | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const firstMissing = nextHostingStep(result?.reasons ?? handoff?.pendingChecks ?? [])
  const goToStep = (destination: HostingStepDestination) => {
    if (destination === 'route') {
      if (routePanel.current) { routePanel.current.open = true; routePanel.current.scrollIntoView?.({ block: 'nearest' }) }
      return
    }
    if (destination === 'game' || destination === 'receive') {
      const surface = panel.current?.closest('[aria-label="Host on this PC"]')?.parentElement
      const target = destination === 'game' ? surface?.querySelector<HTMLDetailsElement>('.connection-doctor') :
        surface?.querySelector<HTMLElement>('[aria-label="Receive save copies"]')
      if (target) {
        if (target instanceof HTMLDetailsElement) target.open = true
        target.scrollIntoView?.({ block: 'nearest' })
      }
      return
    }
    const id = destination === 'server' ? 'server' : destination === 'version' ? 'version' :
      destination === 'ports' ? 'control-port' : destination === 'access' ? 'access' :
      destination === 'folder' ? 'folder' : destination === 'permissions' ? 'permission-help' : 'local-setup'
    const target = panel.current?.querySelector<HTMLElement>(`[data-hosting-step="${id}"]`)
    target?.scrollIntoView?.({ block: 'nearest' })
    target?.focus()
  }
  useEffect(() => { onHostingSetupChange?.(handoff?.readyForManualStart === true) },
    [handoff?.readyForManualStart, onHostingSetupChange])
  useEffect(() => {
    let active = true
    setHandoff(null); setResult(null); setRestoreResult(null); setRouteResult(null); setError('')
    setServerFile(''); setGameVersion(''); setPasswordSet(false); setControlPort('5131'); setGamePort(futureHostFields(game).gamePort)
    setServerName('Recovered world'); setExecutable(''); setPreparedServerRoot(''); setFactorioRconPort('27015'); setGamePassword('')
    setRouteRecordHash(''); setRouteFingerprint(''); setBusy(false)
    void getLocalJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`, parseRestoreStatus)
      .then(status => { if (active) {
        setHandoff(status); setPreparedServerRoot(status.preparedServerRoot ?? '')
        setRouteRecordHash(status.recordHash ?? '')
        setRouteFingerprint(status.controlRouteFingerprint ?? '')
      } })
      .catch(() => {})
    return () => { active = false }
  }, [profileId, game])
  const setup = () => ({ serverFile: serverFile || null, gameVersion: gameVersion || null,
    enabledAddOns: handoff?.requiredAddOns ?? [], newPasswordConfigured: passwordSet,
    controlPort: Number(controlPort), gamePort: Number(gamePort) })
  const run = async (action: 'readiness' | 'rehearse') => {
    setBusy(true); setError('')
    try {
      const value = await changeJson(`/api/local/friend/${profileId}/shared-world/${action}`, 'POST',
        parseTakeoverReadiness, setup())
      if (scope.current === profileId) setResult(value)
    } catch (cause) { if (scope.current === profileId) setError(errorMessage(cause)) }
    finally { if (scope.current === profileId) setBusy(false) }
  }
  const stage = async () => {
    setBusy(true); setError('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/handoff/stage`,
        'POST', parseStageResult)
      if (!result.ok) throw new Error(result.message)
      const status = await getLocalJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`,
        parseRestoreStatus)
      if (scope.current !== profileId) return
      setHandoff(status); setPreparedServerRoot(status.preparedServerRoot ?? '')
      setRouteRecordHash(status.recordHash ?? '')
      setRouteFingerprint(status.controlRouteFingerprint ?? '')
    } catch (cause) { if (scope.current === profileId) setError(errorMessage(cause)) }
    finally { if (scope.current === profileId) setBusy(false) }
  }
  const restore = async () => {
    if (!handoff?.recordHash) return
    setBusy(true); setError('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`,
        'POST', parseRestoreResult, { recordHash: handoff.recordHash, setup: setup(),
          name: serverName, serverName, gamePassword: gamePassword || null,
          executablePath: executable || null, preparedServerRoot: preparedServerRoot || null,
          factorioRconPort: Number(factorioRconPort) })
      const next = result.ok ? await getLocalJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`, parseRestoreStatus) : null
      if (scope.current === profileId) { setRestoreResult(result); if (next) setHandoff(next) }
    } catch (cause) { if (scope.current === profileId) setError(errorMessage(cause)) }
    finally { if (scope.current === profileId) setBusy(false) }
  }
  const finish = async () => {
    if (!handoff?.recordHash) return
    setBusy(true); setError('')
    try {
      const value = await changeJson(`/api/local/friend/${profileId}/shared-world/handoff/finish`,
        'POST', parseRestoreResult, { recordHash: handoff.recordHash, setup: setup(),
          executablePath: executable || null, preparedServerRoot: preparedServerRoot || null })
      const next = await getLocalJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`, parseRestoreStatus)
      if (scope.current === profileId) { setRestoreResult(value); setHandoff(next) }
    } catch (cause) { if (scope.current === profileId) setError(errorMessage(cause)) }
    finally { if (scope.current === profileId) setBusy(false) }
  }
  const probeRoute = async () => {
    setBusy(true); setError(''); setRouteResult(null)
    try {
      const next = await changeJson(`/api/local/friend/${profileId}/shared-world/route-check`,
        'POST', parseRouteCheck, { recordHash: routeRecordHash.trim(),
          tlsFingerprint: routeFingerprint.trim() })
      if (scope.current === profileId) setRouteResult(next)
    } catch (cause) { if (scope.current === profileId) setError(errorMessage(cause)) }
    finally { if (scope.current === profileId) setBusy(false) }
  }
  return <details ref={panel}><summary>Check this PC for future hosting</summary>
    <p>Restore the exact signed planned handoff or recovery majority into a fresh managed world. Finish local and control route checks, then switch to Host for manual Start.</p>
    <p className="helper-text" data-hosting-step="permission-help" tabIndex={-1}>Eligible host is a separate grant from Receive and Start. Ask the original owner to review this PC's grant; a verified copy alone does not allow hosting.</p>
    <ol aria-label="This PC handoff checklist">
      <li aria-current={!handoff?.staged ? 'step' : undefined}>{handoff?.staged ? 'Checked' : 'Next'} · This PC: check the signed decision and exact received copy.</li>
      <li aria-current={handoff?.staged && !handoff.restored ? 'step' : undefined}>{handoff?.restored ? 'Done' : 'Later'} · This PC: restore into a fresh managed world.</li>
      <li aria-current={handoff?.restored && !handoff.readyForManualStart ? 'step' : undefined}>{handoff?.readyForManualStart ? 'Done' : 'Later'} · This PC and another approved Friend PC: finish local setup and check the pinned control route.</li>
      <li aria-current={handoff?.readyForManualStart ? 'step' : undefined}>Manual choice · This PC: open Host, review Start, then test a real game join and saved restart.</li>
    </ol>
    {firstMissing && <section aria-label="Next hosting step"><strong>Next: {firstMissing.reason}</strong>
      <div className="actions"><Button className="text-button" onClick={() => goToStep(firstMissing.destination)}>{firstMissing.action}</Button></div>
    </section>}
    <div data-hosting-step="local-setup" tabIndex={-1}>
    <div data-hosting-step="server" tabIndex={-1}><SharedWorldServerFile profileId={profileId} game={game} value={serverFile} disabled={busy}
      onChange={path => { setServerFile(path); setResult(null) }} onBrowseServerFile={onBrowseServerFile} /></div>
    <label>Installed game version<Input data-hosting-step="version" value={gameVersion} onChange={event => setGameVersion(event.target.value)} /></label>
    <label>Future Friend control port<Input data-hosting-step="control-port" inputMode="numeric" value={controlPort}
      onChange={event => setControlPort(event.target.value)} /></label>
    <label>Future game port<Input inputMode="numeric" value={gamePort}
      onChange={event => setGamePort(event.target.value)} /></label>
    <label><Input data-hosting-step="access" type="checkbox" checked={passwordSet} onChange={event => setPasswordSet(event.target.checked)} />
      {fields.password ? 'I have set a new game password locally' : 'I reviewed local game access and set a new password where this game supports one'}</label>
    {handoff?.requiredAddOns.length ? <details><summary>Required add-ons</summary>
      <ul>{handoff.requiredAddOns.map(addOn => <li key={`${addOn.type}:${addOn.id ?? addOn.name}`}>
        {addOn.name} {addOn.version} ({addOn.type})</li>)}</ul>
      <p className="helper-text">Install these reviewed packages in this managed server folder before restoring. The app checks the installed set.</p>
    </details> : null}
    <p className="helper-text">Check matching add-ons, a fresh managed location, and the direct-IP Friend control route before hosting. Test the real game load and join after Start.</p>
    <div className="actions"><Button className="secondary" disabled={busy} onClick={() => void run('readiness')}>
      Check this PC</Button><Button className="secondary" disabled={busy} onClick={() => void run('rehearse')}>
      Rehearse file restore</Button></div>
    </div>
    {onOpenHostSetup && <Button className="text-button" onClick={onOpenHostSetup}>Open this PC's Host setup</Button>}
    <details ref={routePanel}><summary>Test successor's control route from another Friend PC</summary>
      <p>On another Friend PC, use the signed authority hash and successor certificate fingerprint. This checks pinned HTTPS. Test the game route after Start.</p>
      {handoff?.recordHash && <p className="helper-text">Current signed authority: <code style={{ overflowWrap: 'anywhere' }}>{handoff.recordHash}</code></p>}
      {handoff?.controlRouteFingerprint && <p className="helper-text">This PC's TLS fingerprint: <code style={{ overflowWrap: 'anywhere' }}>{handoff.controlRouteFingerprint}</code></p>}
      {handoff?.controlRouteAddress && <p className="helper-text">Signed successor route: {handoff.controlRouteAddress}</p>}
      {handoff?.recordHash && handoff.controlRouteFingerprint && <Button className="text-button"
        onClick={() => void navigator.clipboard.writeText(
          createSharedRouteDetails(profileId, { recordHash: handoff.recordHash!, tlsFingerprint: handoff.controlRouteFingerprint!, endpoint: handoff.controlRouteAddress }))
          .then(() => setError('')).catch(() => setError('Could not copy route details. Select the signed hash and fingerprint above to copy them manually.'))}>
        Copy route details</Button>}
      <SharedWorldRouteDetails key={profileId} profileId={profileId} onReviewed={details => {
        setRouteRecordHash(details?.recordHash ?? ''); setRouteFingerprint(details?.tlsFingerprint ?? ''); setRouteResult(null)
      }} />
      <label>Signed handoff hash<Input value={routeRecordHash}
        onChange={event => setRouteRecordHash(event.target.value)} /></label>
      <label>Successor certificate fingerprint<Input value={routeFingerprint}
        onChange={event => setRouteFingerprint(event.target.value)} /></label>
      <Button className="secondary" disabled={busy || !/^[0-9A-Fa-f]{64}$/.test(routeRecordHash.trim()) ||
        !/^[0-9A-Fa-f]{64}$/.test(routeFingerprint.trim())} onClick={() => void probeRoute()}>
        Check direct-IP control route</Button>
      {routeResult && <p role="status">{routeResult.message}</p>}
    </details>
    {handoff?.staged && <div data-hosting-step="folder" tabIndex={-1}><p role="status">{handoff.message}</p>
      {!handoff.restored && <><label>New server name<Input value={serverName}
        onChange={event => setServerName(event.target.value)} /></label>
        {(game === 'MinecraftJava' || !knownGame) && <label>{game === 'MinecraftJava' ? 'Java runtime executable (java.exe)' : 'Installed game executable, if different from the server file'}<Input value={executable}
          onChange={event => setExecutable(event.target.value)} /></label>}
        {(fields.minecraft || !knownGame) && <label>Prepared Minecraft server folder, if using Minecraft<Input data-hosting-step="folder" value={preparedServerRoot}
          onChange={event => setPreparedServerRoot(event.target.value)} /></label>
        }
        {handoff.preparedServerRoot && <small>Install the matching Minecraft server in this separate folder. Review its terms, server.properties settings, and player allowlist against the signed source. A mismatch blocks restore and Start. The world folder must be empty.</small>}
        {(fields.factorio || !knownGame) && <label>Factorio local RCON port, if using Factorio<Input inputMode="numeric" value={factorioRconPort}
          onChange={event => setFactorioRconPort(event.target.value)} /></label>}
        {fields.password && <label>New game password, if this game uses one<Input type="password" value={gamePassword}
          onChange={event => setGamePassword(event.target.value)} /></label>}
        {fields.factorio && <p className="helper-text">RCON is local administration only. Never forward its port.</p>}
        <Button disabled={busy} onClick={() => void restore()}>Restore verified copy</Button></>}
      {handoff.restored && !handoff.readyForManualStart && <Button disabled={busy}
        onClick={() => void finish()}>Finish setup and checks</Button>}
      {handoff.readyForManualStart && <div className="start-connection-notice" role="note">
        <strong>Before the first Start on this PC</strong>
        <p>The signed Friend control route was checked from another PC and the local game ports were available. Incoming game traffic is still unverified while the server is stopped. Switch to Host and start, then test the game connection and a real join from another network before relying on this Host.</p>
      </div>}
      <ul>{handoff.pendingChecks.map(check => <li key={check}>{check}</li>)}</ul></div>}
    {!handoff?.staged && <Button data-hosting-step="folder" className="secondary" disabled={busy} onClick={() => void stage()}>
      Check planned handoff</Button>}
    {restoreResult && <div role="status"><p>{restoreResult.message}</p>
      {restoreResult.pendingChecks && <ul>{restoreResult.pendingChecks.map(check =>
        <li key={check}>{check}</li>)}</ul>}</div>}
    {error && <p role="alert">{error}</p>}
    {result && <div role="status"><p>{result.managedProcessRehearsalPassed ?
      'Disposable fixture process checked and removed.' : result.rehearsalPassed ? 'Disposable file restore checked and removed.' :
      result.ready ? 'Local checks passed.' : 'Hosting still needs setup.'}</p>
      {result.reasons.length > 0 && <ul>{result.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>}
      {result.rehearsalPassed && <details><summary>Technical details</summary>
        <p>Version: {result.version} · Hash: {result.versionHash}</p></details>}</div>}
  </details>
}
