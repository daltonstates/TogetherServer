import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { changeJson, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
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
  game: 'game', world: 'world', serverApp: 'server app', ports: 'ports',
  route: 'Friend connection', listing: 'network or public listing', serverName: 'server name'
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
    ? 'The selected game server app no longer matches the one used for the earlier tests.'
    : 'The game server files cannot be read, so the app cannot compare them with the earlier tests. This does not confirm a game update.')
  const fields = [...new Set(scoped?.changedFields ?? [])].filter(field => Object.hasOwn(identityLabels, field))
  if (view.stale && fields.length > 0) explanations.push('Changed settings: ' + fields.map(field => identityLabels[field]).join(', ') + '.')
  else if (view.stale && !view.gameFilesChanged) explanations.push('The server or Friend connection settings differ from the earlier tests. This record does not show which setting changed.')
  else if (view.stale) explanations.push('The saved server and connection settings also differ. This record does not show which other setting changed.')
  const recorded = version(scoped?.recordedGameVersion)
  const current = version(scoped?.currentGameVersion)
  if (recorded && current && (scoped?.versionSource === 'observed' || scoped?.versionSource === 'ownerReported')) {
    explanations.push((scoped.versionSource === 'observed' ? 'Observed' : 'Owner-reported') +
      ' game versions: recorded ' + recorded + '; current ' + current +
      (recorded === current ? '. The same version number does not guarantee the same files.' : '.'))
  } else explanations.push('The earlier and current game versions are not both available, so a version change cannot be confirmed.')
  return explanations
}

// Copy only reviewed booleans, field names and numeric versions. Backend labels,
// evidence text, IDs, fingerprints and error messages never enter this report.
export function acceptanceExplanationReport(view: AcceptanceView, evidence?: AcceptanceIdentityEvidence) {
  const count = reviewedCheckIds.filter(id => view.checks.some(check => check.id === id && check.confirmed)).length
  return ['TogetherServer owner checks',
    'Current setup: ' + (view.stale ? 'repeat the earlier tests' : 'no settings change reported') + '.',
    'Selected game files: ' + (!view.gameFilesAvailable ? 'unavailable' : view.gameFilesChanged ? 'changed' : 'readable') + '.',
    'Current owner confirmations: ' + (view.stale ? 0 : count) + ' of ' + reviewedCheckIds.length + '.',
    ...staleAcceptanceExplanation(view, evidence),
    'Next: review the setup, repeat the affected tests with a real Friend and game, then record the results.',
    'These results record what the owner confirmed. They do not permit Stop or verify app access, player count, a game join or saved files.'].join('\n')
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
  const [copyResult, setCopyResult] = useState(0)
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
      if (scope.current === currentScope) {
        setCopyStatus('Explanation copied. Private details omitted.')
        setCopyResult(result => result + 1)
      }
    } catch {
      if (scope.current === currentScope) {
        setCopyStatus('Could not copy the explanation. Try again.')
        setCopyResult(result => result + 1)
      }
    }
  }

  const currentView = view?.profileId.toLowerCase() === profileId.toLowerCase() ? view : null
  const explanations = currentView ? staleAcceptanceExplanation(currentView, configurationEvidence) : []
  return <section hidden={!visible} className="acceptance-recorder" aria-labelledby={titleId} aria-busy={!!busy}>
    <div className="section-heading"><div><h3 id={titleId}>Record your test results</h3>
      <p>Record tests you completed with a real Friend and game. Results apply only to this server and connection setup.</p></div>
      <Button className="text-button" disabled={!!busy || !visible} onClick={() => void load()}>Refresh</Button></div>
    {error && <DismissibleNotice as="p" scope={profileId} className="warning-text" role="alert">{error}</DismissibleNotice>}
    {!currentView && !error && <p className="helper-text" role="status">Loading recorded checks…</p>}
    {currentView && explanations.length > 0 && <DismissibleNotice scope={profileId} className="notice bad" role="status">
      <strong>{currentView.gameFilesChanged ? 'Game server files changed or unavailable' : 'Server setup changed'}</strong>
      <ul>{explanations.map(explanation => <li key={explanation}>{explanation}</li>)}</ul>
      <p>Earlier results no longer apply to this setup. Review the change and repeat the affected tests. Stop the server before changing files.</p>
      {currentView.updatedUtc && <small>Earlier checks last recorded {new Date(currentView.updatedUtc).toLocaleString()}.</small>}
    </DismissibleNotice>}
    {currentView && !currentView.gameFilesAvailable && <DismissibleNotice as="p" scope={profileId} className="warning-text" role="status">The selected game server files could not be read. Check the saved server app before relying on earlier tests.</DismissibleNotice>}
    {currentView && <>
      <div className="acceptance-checks">{currentView.checks.map(check => <label className="acceptance-check" key={check.id}>
        <Input type="checkbox" checked={check.confirmed} disabled={!!busy}
          onChange={event => void change(check.id, event.target.checked)} />
        <span><strong>{check.label}</strong><small>{check.evidence}</small>
          {check.confirmedUtc && <small>Owner confirmed {new Date(check.confirmedUtc).toLocaleString()}.</small>}</span>
      </label>)}</div>
      <Button className="secondary" onClick={() => void copyExplanation()}>Copy explanation</Button>
      {copyStatus && <DismissibleNotice as="output" scope={profileId} noticeKey={copyResult} role="status">{copyStatus}</DismissibleNotice>}
      <small className="evidence-boundary">{currentView.evidenceBoundary}</small>
    </>}
  </section>
}
