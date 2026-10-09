import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { ContractError } from './contracts'
import { friendCheckFreshness } from './FriendConnectionDoctor'
import { parseGameCompatibility, parseGameRequirements, validManualGameVersion,
  type GameCompatibilityResult, type GameRequirementsResult } from './gameCompatibilityWire'

export type GameCompatibilityPanelProps = {
  profileId: string
  host: boolean
  connectionId?: string
  identityKey?: string | number
  available?: boolean
  nowMs?: number
}

function ScopedCompatibilityPanel({ profileId, host, available = true, nowMs }: GameCompatibilityPanelProps) {
  const titleId = useId(), inputId = useId()
  const [result, setResult] = useState<GameRequirementsResult | GameCompatibilityResult | null>(null)
  const [manual, setManual] = useState('')
  const [busy, setBusy] = useState(false), [error, setError] = useState('')
  const [clock, setClock] = useState(() => Date.now())
  const pending = useRef<AbortController | null>(null)
  const encodedId = encodeURIComponent(profileId)
  const path = host ? `/api/local/profiles/${encodedId}/requirements` : `/api/local/friend/${encodedId}/compatibility`
  const decode = useCallback((value: unknown, context?: string) => {
    const next = host ? parseGameRequirements(value, context) : parseGameCompatibility(value, context)
    if (next.requirements && next.requirements.profileId !== profileId) throw new ContractError('Requirements returned a different saved server.')
    return next
  }, [host, profileId])
  const refresh = useCallback(async () => {
    if (!available) return
    pending.current?.abort()
    const controller = new AbortController(); pending.current = controller
    setBusy(true); setError('')
    try {
      const next = await getLocalJson(path, decode, controller.signal)
      if (controller.signal.aborted || pending.current !== controller) return
      setResult(next); setError(next.ok ? '' : next.message)
    } catch (failure) {
      if (!controller.signal.aborted && pending.current === controller) { setResult(null); setError(errorMessage(failure)) }
    } finally {
      if (!controller.signal.aborted && pending.current === controller) { pending.current = null; setBusy(false) }
    }
  }, [available, path, decode])
  useEffect(() => {
    void refresh()
    return () => pending.current?.abort()
  }, [refresh])
  useEffect(() => {
    if (nowMs !== undefined) return
    const timer = window.setInterval(() => setClock(Date.now()), 15_000)
    return () => window.clearInterval(timer)
  }, [nowMs])
  async function save(clear = false) {
    if (!available || busy || pending.current || !clear && !validManualGameVersion(manual)) return
    const controller = new AbortController(); pending.current = controller
    setBusy(true); setError('')
    try {
      const next = await changeJson(host ? path : `/api/local/friend/${encodedId}/client-version`, 'PUT', decode,
        { version: clear ? null : manual }, controller.signal)
      if (controller.signal.aborted || pending.current !== controller) return
      setResult(next); setError(next.ok ? '' : next.message)
      if (next.ok) setManual('')
    } catch (failure) { if (!controller.signal.aborted && pending.current === controller) { setResult(null); setError(errorMessage(failure)) } }
    finally { if (!controller.signal.aborted && pending.current === controller) { pending.current = null; setBusy(false) } }
  }
  const requirements = result?.ok ? result.requirements : null
  const comparison = result?.ok && 'clientVersion' in result ? result : null
  const freshness = friendCheckFreshness(requirements?.checkedUtc, nowMs ?? clock, 5 * 60_000)
  const stale = busy || freshness !== 'current'
  const versionState = stale ? 'Unknown' : comparison?.versionComparison ?? 'Unknown'
  const addOnState = stale ? 'Unknown' : comparison?.addOnComparison ?? 'Unknown'
  const source = requirements?.versionSource === 'OwnerReported' ? 'owner-reported'
    : requirements?.versionSource === 'Observed' ? 'observed installation metadata' : 'not detected'
  const clientSource = comparison?.clientVersionSource === 'Manual' ? 'manual'
    : comparison?.clientVersionSource === 'Observed' ? 'observed installation metadata' : 'not detected'
  return <section className="game-compatibility" aria-labelledby={titleId} aria-busy={busy}>
    <div className="panel-heading"><div><h4 id={titleId}>{host ? 'Before friends join' : 'Game requirements'}</h4>
      <p>{requirements ? `${requirements.gameName} · ${requirements.requiredVersion ?? 'Version unknown'}`
        : !available ? 'Reconnect for current requirements' : busy ? 'Reading saved game requirements…' : 'Requirements unavailable'}</p></div>
      <Button className="text-button" disabled={busy || !available} onClick={() => void refresh()}>Refresh requirements</Button></div>
    {error && <p className="error-text" role="alert">{error}</p>}
    {requirements && <>
      <div className="compatibility-summary" role="status">
        <p>Required version: <strong>{requirements.requiredVersion ?? 'Unknown'}</strong> ({source}).</p>
        {comparison && <>
          <p>Current on this PC: <strong>{comparison.clientVersion ?? 'Unknown'}</strong> ({clientSource}).</p>
          <p className={versionState === 'Mismatch' ? 'error-text' : 'helper-text'}>Version: <strong>{versionState}</strong> · Add-ons: <strong>{addOnState}</strong></p>
          {versionState === 'Mismatch' && <small>Version mismatch; check with the Host before joining.</small>}
          {versionState === 'Match' && <small>Version text matches. Review add-ons separately.</small>}
          {versionState === 'Unknown' && <small>Version compatibility unknown. Refresh or compare the version shown in the game.</small>}
        </>}
      </div>
      <small>{busy ? 'Refreshing… Previous checks are informational.' : freshness === 'stale' ? 'Stale requirements. Refresh before comparing.'
        : freshness === 'unknown' ? 'Check time cannot be verified. Refresh before comparing.' : 'Checked'}
        {' '}<time dateTime={requirements.checkedUtc}>{new Date(requirements.checkedUtc).toLocaleString()}</time>. Refresh after the Host changes the game or add-ons.</small>
      <details className="advanced-block"><summary>Add-on inventory: {requirements.addOnState === 'Known'
        ? requirements.addOns.length ? `${requirements.addOns.length} enabled` : 'none enabled'
        : requirements.addOnState === 'NotReviewed' ? 'mod support not reviewed' : 'Unknown'}</summary>
        {requirements.addOns.length > 0 && <ul>{requirements.addOns.map(item => <li key={`${item.type}:${item.id}`}>{item.name} · {item.version}</li>)}</ul>}
        <p>{requirements.guidance}</p>
        {comparison && <p>{addOnState === 'Match' ? 'The default Factorio mod-folder names and versions match. Custom launch options are not inspected.'
          : addOnState === 'Mismatch' ? 'The default Factorio mod set differs on this PC. Review the Host’s list.'
            : 'Installed add-on compatibility is Unknown. Review it in the game.'}</p>}
      </details>
    </>}
    {available && (host ? requirements?.versionSource !== 'Observed' : !!requirements && comparison?.clientVersionSource !== 'Observed')
      && <details className="advanced-block"><summary>{host ? 'State the required version' : 'Enter the version shown in your game'}</summary>
        <label htmlFor={inputId}>{host ? 'Required game version (owner-reported)' : 'Installed game version (manual)'}<Input id={inputId} value={manual} maxLength={48}
          placeholder="For example, 1.21.1" disabled={busy} onChange={event => setManual(event.target.value)} /></label>
        <div className="actions"><Button className="secondary" disabled={busy || !validManualGameVersion(manual)} onClick={() => void save()}>Save version</Button>
          <Button className="text-button" disabled={busy} onClick={() => void save(true)}>Clear manual version</Button></div>
      </details>}
    <small>These checks never install add-ons or prove readiness, a player count, a successful join or a saved world.</small>
  </section>
}

export function GameCompatibilityPanel(props: GameCompatibilityPanelProps) {
  const scope = JSON.stringify([props.host, props.profileId, props.connectionId, props.identityKey, props.available ?? true])
  return <ScopedCompatibilityPanel key={scope} {...props} />
}
