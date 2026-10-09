import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { changeJson, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseAcceptanceResult, parseAcceptanceView, type AcceptanceResult, type AcceptanceView } from './contracts'

export type AcceptanceIdentityField = 'game' | 'world' | 'serverApp' | 'ports' | 'route' | 'listing' | 'serverName'
export type AcceptanceIdentityEvidence = {
  profileId: string
  changedFields?: readonly AcceptanceIdentityField[]
  recordedGameVersion?: string | null
  currentGameVersion?: string | null
  versionSource?: 'observed' | 'ownerReported'
}
export type AcceptanceLoader = (profileId: string, signal?: AbortSignal) => Promise<AcceptanceView>
export type AcceptanceSaver = (profileId: string, checkId: string, confirmed: boolean, signal?: AbortSignal) => Promise<AcceptanceResult>
const defaultLoader: AcceptanceLoader = (profileId, signal) =>
  getLocalJson('/api/local/profiles/' + encodeURIComponent(profileId) + '/acceptance', parseAcceptanceView, signal)
const defaultSaver: AcceptanceSaver = (profileId, checkId, confirmed, signal) =>
  changeJson('/api/local/profiles/' + encodeURIComponent(profileId) + '/acceptance', 'PUT', parseAcceptanceResult,
    { checkId, confirmed }, signal)

const identityLabels: Record<AcceptanceIdentityField, string> = {
  game: 'game choice', world: 'world identity', serverApp: 'selected server app', ports: 'configured ports',
  route: 'Friend route', listing: 'network/listing mode', serverName: 'server-name setting'
}
const reviewedCheckIds = ['FriendRoute', 'RealJoin', 'PlayerTransition', 'GracefulStop', 'SavedRestart', 'RestoreDrill', 'SleepResume']
function version(value: string | null | undefined) {
  return value && value.length <= 48 && /^\d{1,6}(?:\.\d{1,6}){1,4}$/u.test(value) ? value : null
}

export function staleAcceptanceExplanation(view: AcceptanceView, evidence?: AcceptanceIdentityEvidence): string[] {
  if (!view.stale && !view.gameFilesChanged) return []
  const scoped = evidence?.profileId.toLowerCase() === view.profileId.toLowerCase() ? evidence : undefined
  const explanations: string[] = []
  if (view.gameFilesChanged) explanations.push(view.gameFilesAvailable
    ? 'The selected game server file identity differs from the identity saved with the earlier confirmations.'
    : 'The selected game server files are unavailable, so their current identity cannot match the saved confirmations. A game update has not been established.')
  const fields = [...new Set(scoped?.changedFields ?? [])].filter(field => Object.hasOwn(identityLabels, field))
  if (view.stale && fields.length > 0) explanations.push('Compared configuration changed: ' + fields.map(field => identityLabels[field]).join(', ') + '.')
  else if (view.stale && !view.gameFilesChanged) explanations.push('The current server and Friend-route configuration identity differs from the identity saved with these confirmations. The exact changed setting is not available in this record.')
  else if (view.stale) explanations.push('The combined server and route identity also differs. This record does not identify any other changed setting.')
  const recorded = version(scoped?.recordedGameVersion)
  const current = version(scoped?.currentGameVersion)
  if (recorded && current && (scoped?.versionSource === 'observed' || scoped?.versionSource === 'ownerReported')) {
    explanations.push((scoped.versionSource === 'observed' ? 'Observed' : 'Owner-reported') +
      ' game versions: recorded ' + recorded + '; current ' + current +
      (recorded === current ? '. Matching version labels do not establish matching files.' : '.'))
  } else explanations.push('Recorded and current game versions are not both available here; a version change cannot be named.')
  return explanations
}

// Copy only reviewed booleans, field names and numeric versions. Backend labels,
// evidence text, IDs, fingerprints and error messages never enter this report.
export function acceptanceExplanationReport(view: AcceptanceView, evidence?: AcceptanceIdentityEvidence) {
  const count = reviewedCheckIds.filter(id => view.checks.some(check => check.id === id && check.confirmed)).length
  return ['TogetherServer owner checks',
    'Current configuration: ' + (view.stale ? 'earlier confirmations are stale' : 'no configuration mismatch reported') + '.',
    'Selected game files: ' + (!view.gameFilesAvailable ? 'unavailable' : view.gameFilesChanged ? 'identity differs' : 'readable') + '.',
    'Current owner confirmations: ' + (view.stale ? 0 : count) + ' of ' + reviewedCheckIds.length + '.',
    ...staleAcceptanceExplanation(view, evidence),
    'Next: review the saved setup, then repeat the affected real Friend/game/save checks and record them explicitly.',
    'Owner confirmations do not authorize Stop or prove authentication, occupancy, a join or save integrity.'].join('\n')
}

export function AcceptanceRecorder({ profileId, visible = true, configurationEvidence, loader = defaultLoader, saver = defaultSaver }: {
  profileId: string
  visible?: boolean
  configurationEvidence?: AcceptanceIdentityEvidence
  loader?: AcceptanceLoader
  saver?: AcceptanceSaver
}) {
  const titleId = useId()
  const [view, setView] = useState<AcceptanceView | null>(null)
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [copyStatus, setCopyStatus] = useState('')
  const pending = useRef<AbortController | null>(null)
  const scope = useRef(0)

  const load = useCallback(async () => {
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    setBusy('load'); setError(''); setCopyStatus('')
    try {
      const next = await loader(profileId, controller.signal)
      if (!controller.signal.aborted) {
        if (next.profileId.toLowerCase() !== profileId.toLowerCase()) setError('Recorded checks returned for a different server. Refresh and try again.')
        else setView(next)
      }
    } catch {
      if (!controller.signal.aborted) setError('Could not read recorded owner checks. Refresh and try again.')
    } finally { if (!controller.signal.aborted) setBusy('') }
  }, [loader, profileId])

  useEffect(() => {
    const operationScope = scope
    operationScope.current++
    setView(null); setBusy(''); setError(''); setCopyStatus('')
    if (visible && profileId) void load()
    return () => { pending.current?.abort(); operationScope.current++ }
  }, [load, profileId, visible])

  const change = async (checkId: string, confirmed: boolean) => {
    if (busy || !view || view.profileId.toLowerCase() !== profileId.toLowerCase()) return
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    setBusy(checkId); setError(''); setCopyStatus('')
    try {
      const result = await saver(profileId, checkId, confirmed, controller.signal)
      if (controller.signal.aborted) return
      if (!result.ok || result.view.profileId.toLowerCase() !== profileId.toLowerCase())
        setError('The owner confirmation could not be saved for this server. Refresh and try again.')
      else setView(result.view)
    } catch {
      if (!controller.signal.aborted) setError('The owner confirmation could not be saved. Refresh and try again.')
    } finally { if (!controller.signal.aborted) setBusy('') }
  }
  const copyExplanation = async () => {
    if (!view) return
    const currentScope = scope.current
    try {
      await navigator.clipboard.writeText(acceptanceExplanationReport(view, configurationEvidence))
      if (scope.current === currentScope) setCopyStatus('Redacted owner-check explanation copied.')
    } catch {
      if (scope.current === currentScope) setCopyStatus('Could not copy the explanation. Try again.')
    }
  }

  const currentView = view?.profileId.toLowerCase() === profileId.toLowerCase() ? view : null
  const explanations = currentView ? staleAcceptanceExplanation(currentView, configurationEvidence) : []
  return <section hidden={!visible} className="acceptance-recorder" aria-labelledby={titleId} aria-busy={!!busy}>
    <div className="section-heading"><div><h3 id={titleId}>Acceptance Recorder</h3>
      <p>Keep owner-confirmed real-world checks tied to this exact server and route configuration.</p></div>
      <Button className="text-button" disabled={!!busy || !visible} onClick={() => void load()}>Refresh</Button></div>
    {error && <p className="warning-text" role="alert">{error}</p>}
    {!currentView && !error && <p className="helper-text" role="status">Loading recorded checks…</p>}
    {currentView && explanations.length > 0 && <div className="notice bad" role="status">
      <strong>{currentView.gameFilesChanged ? 'Game server file identity changed' : 'Configuration changed'}</strong>
      <ul>{explanations.map(explanation => <li key={explanation}>{explanation}</li>)}</ul>
      <p>Earlier confirmations no longer count for this setup. Review the change, keep the server stopped for any file work, and repeat the affected real checks.</p>
      {currentView.updatedUtc && <small>Earlier checks last recorded {new Date(currentView.updatedUtc).toLocaleString()}.</small>}
    </div>}
    {currentView && !currentView.gameFilesAvailable && <p className="warning-text" role="status">The selected game server files could not be read. Check the saved server app before relying on earlier tests.</p>}
    {currentView && <>
      <div className="acceptance-checks">{currentView.checks.map(check => <label className="acceptance-check" key={check.id}>
        <Input type="checkbox" checked={check.confirmed} disabled={!!busy}
          onChange={event => void change(check.id, event.target.checked)} />
        <span><strong>{check.label}</strong><small>{check.evidence}</small>
          {check.confirmedUtc && <small>Owner confirmed {new Date(check.confirmedUtc).toLocaleString()}.</small>}</span>
      </label>)}</div>
      <Button className="secondary" onClick={() => void copyExplanation()}>Copy explanation</Button>
      {copyStatus && <output role="status">{copyStatus}</output>}
      <small className="evidence-boundary">{currentView.evidenceBoundary}</small>
    </>}
  </section>
}
