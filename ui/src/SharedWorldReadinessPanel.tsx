import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'

type Readiness = { ready: boolean; reasons: string[]; version: number | null;
  versionHash: string | null; rehearsalPassed: boolean; managedProcessRehearsalPassed?: boolean }
type RouteCheck = { controlRouteObserved: boolean; code: string; message: string }
type RequiredAddOn = { name: string; version: string; requiredGameVersion: string;
  type: string; id: string | null }
type RestoreStatus = { staged: boolean; restored: boolean; recordHash: string | null;
  message: string; pendingChecks: string[]; preparedServerRoot: string | null;
  readyForManualStart: boolean; requiredAddOns: RequiredAddOn[];
  controlRouteFingerprint: string | null }
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
        !/^[0-9A-F]{64}$/.test(item.controlRouteFingerprint)))) throw new Error('Handoff status is invalid.')
  return { ...item, requiredAddOns: item.requiredAddOns ?? [] } as RestoreStatus
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

export function SharedWorldReadinessPanel({ profileId, onHostingSetupChange }:
  { profileId: string; onHostingSetupChange?: (ready: boolean) => void }) {
  const [serverFile, setServerFile] = useState('')
  const [gameVersion, setGameVersion] = useState('')
  const [passwordSet, setPasswordSet] = useState(false)
  const [controlPort, setControlPort] = useState('5131')
  const [gamePort, setGamePort] = useState('')
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
  useEffect(() => { onHostingSetupChange?.(handoff?.readyForManualStart === true) },
    [handoff?.readyForManualStart, onHostingSetupChange])
  useEffect(() => {
    let active = true
    setHandoff(null)
    void getLocalJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`, parseRestoreStatus)
      .then(status => { if (active) {
        setHandoff(status); setPreparedServerRoot(status.preparedServerRoot ?? '')
        setRouteRecordHash(status.recordHash ?? '')
        setRouteFingerprint(status.controlRouteFingerprint ?? '')
      } })
      .catch(() => {})
    return () => { active = false }
  }, [profileId])
  const setup = () => ({ serverFile: serverFile || null, gameVersion: gameVersion || null,
    enabledAddOns: handoff?.requiredAddOns ?? [], newPasswordConfigured: passwordSet,
    controlPort: Number(controlPort), gamePort: Number(gamePort) })
  const run = async (action: 'readiness' | 'rehearse') => {
    setBusy(true); setError('')
    try {
      setResult(await changeJson(`/api/local/friend/${profileId}/shared-world/${action}`, 'POST',
        parseTakeoverReadiness, setup()))
    } catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  const stage = async () => {
    setBusy(true); setError('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/handoff/stage`,
        'POST', parseStageResult)
      if (!result.ok) throw new Error(result.message)
      const status = await getLocalJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`,
        parseRestoreStatus)
      setHandoff(status); setPreparedServerRoot(status.preparedServerRoot ?? '')
      setRouteRecordHash(status.recordHash ?? '')
      setRouteFingerprint(status.controlRouteFingerprint ?? '')
    } catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
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
      setRestoreResult(result)
      if (result.ok) setHandoff(await getLocalJson(
        `/api/local/friend/${profileId}/shared-world/handoff/restore`, parseRestoreStatus))
    } catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  const finish = async () => {
    if (!handoff?.recordHash) return
    setBusy(true); setError('')
    try {
      const value = await changeJson(`/api/local/friend/${profileId}/shared-world/handoff/finish`,
        'POST', parseRestoreResult, { recordHash: handoff.recordHash, setup: setup(),
          executablePath: executable || null, preparedServerRoot: preparedServerRoot || null })
      setRestoreResult(value)
      setHandoff(await getLocalJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`,
        parseRestoreStatus))
    } catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  const probeRoute = async () => {
    setBusy(true); setError(''); setRouteResult(null)
    try {
      setRouteResult(await changeJson(`/api/local/friend/${profileId}/shared-world/route-check`,
        'POST', parseRouteCheck, { recordHash: routeRecordHash.trim(),
          tlsFingerprint: routeFingerprint.trim() }))
    } catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  return <details><summary>Check this PC for future hosting</summary>
    <p>Restore the exact signed planned handoff or recovery majority into a fresh managed world. Finish local and control route checks, then switch to Host for manual Start.</p>
    <label>Installed game server file<Input value={serverFile} onChange={event => setServerFile(event.target.value)}
      placeholder="Path to installed server file" /></label>
    <label>Installed game version<Input value={gameVersion} onChange={event => setGameVersion(event.target.value)} /></label>
    <label>Future Friend control port<Input inputMode="numeric" value={controlPort}
      onChange={event => setControlPort(event.target.value)} /></label>
    <label>Future game port<Input inputMode="numeric" value={gamePort}
      onChange={event => setGamePort(event.target.value)} /></label>
    <label><Input type="checkbox" checked={passwordSet} onChange={event => setPasswordSet(event.target.checked)} />
      I have set a new game password locally</label>
    {handoff?.requiredAddOns.length ? <details><summary>Required add-ons</summary>
      <ul>{handoff.requiredAddOns.map(addOn => <li key={`${addOn.type}:${addOn.id ?? addOn.name}`}>
        {addOn.name} {addOn.version} ({addOn.type})</li>)}</ul>
      <p className="helper-text">Install these reviewed packages in this managed server folder before restoring. The app checks the installed set.</p>
    </details> : null}
    <p className="helper-text">Matching add-ons, a fresh managed location, real game load, and direct-IP routes still need confirmation before hosting.</p>
    <div className="actions"><Button className="secondary" disabled={busy} onClick={() => void run('readiness')}>
      Check this PC</Button><Button className="secondary" disabled={busy} onClick={() => void run('rehearse')}>
      Rehearse file restore</Button></div>
    <details><summary>Test successor's control route from another Friend PC</summary>
      <p>On another Friend PC, use the signed authority hash and successor certificate fingerprint. This checks pinned HTTPS. Test the game route after Start.</p>
      {handoff?.recordHash && <p className="helper-text">Current signed authority: <code style={{ overflowWrap: 'anywhere' }}>{handoff.recordHash}</code></p>}
      {handoff?.controlRouteFingerprint && <p className="helper-text">This PC's TLS fingerprint: <code style={{ overflowWrap: 'anywhere' }}>{handoff.controlRouteFingerprint}</code></p>}
      {handoff?.recordHash && handoff.controlRouteFingerprint && <Button className="text-button"
        onClick={() => void navigator.clipboard.writeText(
          `${handoff.recordHash}\n${handoff.controlRouteFingerprint}`).catch(cause => setError(errorMessage(cause)))}>
        Copy route details</Button>}
      <label>Signed handoff hash<Input value={routeRecordHash}
        onChange={event => setRouteRecordHash(event.target.value)} /></label>
      <label>Successor certificate fingerprint<Input value={routeFingerprint}
        onChange={event => setRouteFingerprint(event.target.value)} /></label>
      <Button className="secondary" disabled={busy || !/^[0-9A-Fa-f]{64}$/.test(routeRecordHash.trim()) ||
        !/^[0-9A-Fa-f]{64}$/.test(routeFingerprint.trim())} onClick={() => void probeRoute()}>
        Check direct-IP control route</Button>
      {routeResult && <p role="status">{routeResult.message}</p>}
    </details>
    {handoff?.staged && <div><p role="status">{handoff.message}</p>
      {!handoff.restored && <><label>New server name<Input value={serverName}
        onChange={event => setServerName(event.target.value)} /></label>
        <label>Installed game executable, if different from the server file<Input value={executable}
          onChange={event => setExecutable(event.target.value)} /></label>
        <label>Prepared Minecraft server folder, if using Minecraft<Input value={preparedServerRoot}
          onChange={event => setPreparedServerRoot(event.target.value)} /></label>
        {handoff.preparedServerRoot && <small>Install the matching Minecraft server in this separate folder. Review its terms, server.properties settings, and player allowlist against the signed source. A mismatch blocks restore and Start. The world folder must be empty.</small>}
        <label>Factorio local RCON port, if using Factorio<Input inputMode="numeric" value={factorioRconPort}
          onChange={event => setFactorioRconPort(event.target.value)} /></label>
        <label>New game password, if this game uses one<Input type="password" value={gamePassword}
          onChange={event => setGamePassword(event.target.value)} /></label>
        <Button disabled={busy} onClick={() => void restore()}>Restore verified copy</Button></>}
      {handoff.restored && !handoff.readyForManualStart && <Button disabled={busy}
        onClick={() => void finish()}>Finish setup and checks</Button>}
      {handoff.readyForManualStart && <p role="status">Ready for manual Start. Switch to Host, start this server, then test a real game join and saved Stop.</p>}
      <ul>{handoff.pendingChecks.map(check => <li key={check}>{check}</li>)}</ul></div>}
    {!handoff?.staged && <Button className="secondary" disabled={busy} onClick={() => void stage()}>
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
