import { useCallback, useEffect, useId, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { ContractError } from './contracts'
import { parseGameCompatibility, parseGameRequirements, validManualGameVersion,
  type GameCompatibilityResult, type GameRequirementsResult } from './gameCompatibilityWire'

export function GameCompatibilityPanel({ profileId, host }: { profileId: string; host: boolean }) {
  const titleId = useId(), inputId = useId()
  const [result, setResult] = useState<GameRequirementsResult | GameCompatibilityResult | null>(null)
  const [manual, setManual] = useState('')
  const [busy, setBusy] = useState(false), [error, setError] = useState('')
  const path = host ? `/api/local/profiles/${profileId}/requirements` : `/api/local/friend/${profileId}/compatibility`
  const decode = useCallback((value: unknown, context?: string) => {
    const next = host ? parseGameRequirements(value, context) : parseGameCompatibility(value, context)
    if (next.requirements && next.requirements.profileId !== profileId) throw new ContractError('Requirements returned a different saved server.')
    return next
  }, [host, profileId])
  const refresh = useCallback(async (signal?: AbortSignal) => {
    setBusy(true)
    try {
      const next = await getLocalJson(path, decode, signal)
      if (signal?.aborted) return
      setResult(next); setError(next.ok ? '' : next.message)
    } catch (failure) { if (!signal?.aborted) { setResult(null); setError(errorMessage(failure)) } }
    finally { if (!signal?.aborted) setBusy(false) }
  }, [path, decode])
  useEffect(() => {
    const controller = new AbortController(); void refresh(controller.signal)
    return () => controller.abort()
  }, [refresh])
  async function save(clear = false) {
    if (busy || !clear && !validManualGameVersion(manual)) return
    setBusy(true)
    try {
      const next = await changeJson(host ? path : `/api/local/friend/${profileId}/client-version`, 'PUT', decode, { version: clear ? null : manual })
      setResult(next); setError(next.ok ? '' : next.message)
      if (next.ok) setManual('')
    } catch (failure) { setError(errorMessage(failure)) }
    finally { setBusy(false) }
  }
  const requirements = result?.requirements
  const comparison = result && 'clientVersion' in result ? result : null
  return <section className="game-compatibility" aria-labelledby={titleId} aria-busy={busy}>
    <div className="panel-heading"><div><h4 id={titleId}>{host ? 'Before friends join' : 'Game requirements'}</h4>
      <p>{requirements ? `${requirements.gameName} · ${requirements.requiredVersion ?? 'Version unknown'}` : busy ? 'Reading saved game requirements…' : 'Requirements unavailable'}</p></div>
      <Button className="text-button" disabled={busy} onClick={() => void refresh()}>Refresh requirements</Button></div>
    {error && <p className="error-text" role="alert">{error}</p>}
    {requirements && <>
      <p className="helper-text">Required version: {requirements.requiredVersion ?? 'Unknown'} ({requirements.versionSource === 'OwnerReported' ? 'owner-reported' : requirements.versionSource === 'Observed' ? 'observed installation metadata' : 'not detected'}).</p>
      <small>Checked {new Date(requirements.checkedUtc).toLocaleString()}. Refresh after the Host changes the game or add-ons.</small>
      {comparison && <p className={comparison.versionComparison === 'Mismatch' ? 'error-text' : 'helper-text'}>
        This PC: {comparison.clientVersion ?? 'Unknown'} ({comparison.clientVersionSource === 'Manual' ? 'manual' : comparison.clientVersionSource === 'Observed' ? 'observed installation metadata' : 'not detected'}).
        {' '}{comparison.versionComparison === 'Match' ? 'Version text matches.' : comparison.versionComparison === 'Mismatch' ? 'Version mismatch; check with the Host before joining.' : 'Version compatibility unknown.'}
      </p>}
      <details className="advanced-block"><summary>Add-ons: {requirements.addOnState === 'Known' ? requirements.addOns.length ? `${requirements.addOns.length} enabled` : 'none enabled' : requirements.addOnState === 'NotReviewed' ? 'mod support not reviewed' : 'Unknown'}</summary>
        {requirements.addOns.length > 0 && <ul>{requirements.addOns.map(item => <li key={`${item.type}:${item.id}`}>{item.name} · {item.version}</li>)}</ul>}
        <p>{requirements.guidance}</p>
        {comparison && <p>{comparison.addOnComparison === 'Match' ? 'The default Factorio mod-folder names and versions match. Custom launch options are not inspected.' : comparison.addOnComparison === 'Mismatch' ? 'The default Factorio mod set differs on this PC. Review the Host’s list.' : 'Installed add-on compatibility is Unknown. Review it in the game.'}</p>}
      </details>
    </>}
      {(host ? requirements?.versionSource !== 'Observed' : !!requirements && comparison?.clientVersionSource !== 'Observed') && <details className="advanced-block"><summary>{host ? 'State the required version' : 'Enter the version shown in your game'}</summary>
        <label htmlFor={inputId}>{host ? 'Required game version (owner-reported)' : 'Installed game version (manual)'}<Input id={inputId} value={manual} maxLength={48} placeholder="For example, 1.21.1" disabled={busy} onChange={event => setManual(event.target.value)} /></label>
        <div className="actions"><Button className="secondary" disabled={busy || !validManualGameVersion(manual)} onClick={() => void save()}>Save version</Button><Button className="text-button" disabled={busy} onClick={() => void save(true)}>Clear manual version</Button></div>
      </details>}
    <small>These checks never install add-ons or prove readiness, a player count, a successful join or a saved world.</small>
  </section>
}
