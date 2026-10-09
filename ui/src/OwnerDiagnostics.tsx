import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { getLocalJson } from './api'
import { Button, Select } from './Controls'
import {
  parseOwnerDiagnostics, parseSupportReportExport,
  type OwnerDiagnosticCheck, type OwnerDiagnosticsView, type SupportReportExport
} from './contracts'

export type OwnerDiagnosticsLoader = (signal?: AbortSignal) => Promise<OwnerDiagnosticsView>
export type SupportReportLoader = () => Promise<SupportReportExport>

const defaultDiagnosticsLoader: OwnerDiagnosticsLoader = signal =>
  getLocalJson('/api/local/diagnostics', parseOwnerDiagnostics, signal)
const defaultSupportReportLoader: SupportReportLoader = () =>
  getLocalJson('/api/local/support-report', parseSupportReportExport)

const diagnosticFacts = {
  configuration: { label: 'Saved setup', states: ['Saved', 'Needs attention', 'Driver unavailable', 'Check unavailable'],
    meaning: 'Saved setup validation does not prove that the game started or a world is healthy.', next: 'Review the saved setup before Start.' },
  'managed-process': { label: 'Managed process', states: ['Offline', 'Starting', 'Process running', 'Listening', 'Ready', 'Stopping', 'Unknown', 'Failed'],
    meaning: 'Only the recorded process identity can establish which process is managed.', next: 'Review Overview; resolve uncertain identity before another Start.' },
  'driver-observation': { label: 'Readiness and players', states: ['Not active', 'Player count unavailable', 'Offline', 'Starting', 'Process running', 'Listening', 'Ready', 'Stopping', 'Unknown', 'Failed'],
    meaning: 'A missing or stale count is Unknown. Remote and automatic Stop require a fresh trusted zero and a final recheck.', next: 'Refresh the player observation in Players.' },
  'local-game-ports': { label: 'Local game ports', states: ['Open on PC', 'Closed on PC', 'Opening', 'Waiting', 'Loopback only', 'Unknown', 'Unavailable', 'Relay ready', 'Starting', 'Stopping', 'Failed'],
    meaning: 'Local socket or relay evidence does not prove an outside game route or a game join.', next: 'Review Connection help, then test the game separately from a real Friend PC.' },
  'friend-evidence': { label: 'Friend contact', states: ['None recorded', 'Recent', 'Old'],
    meaning: 'Authenticated app contact does not establish the Friend network location or prove a game join.', next: 'Check the saved Friend connection; test the game join separately.' },
  'companion-listener': { label: 'Friend app listener', states: ['Off', 'Idle', 'Listening', 'Open on PC', 'Not listening', 'Closed on PC', 'Error', 'Unknown'],
    meaning: 'A listener on this PC does not establish outside reachability or pinned authentication.', next: 'Review Friend access and Connection help.' },
  'route-diagnostic': { label: 'Outside TCP test', states: ['Not checked', 'Reachable', 'Not reachable', 'Inconclusive', 'Unavailable', 'Previous result expired'],
    meaning: 'TCP-only evidence does not prove pinned TLS, device access or a game join.', next: 'Review the intended route and ask a Friend on another network to connect.' },
  'data-recovery': { label: 'Local data recovery', states: ['Server controls blocked', 'Review needed', 'No active notice'],
    meaning: 'Data recovery notices concern app control state; they do not verify a world save.', next: 'Review the Attention Center and resolve recorded processes before acknowledging recovery.' },
  'update-state': { label: 'Application update', states: ['Current', 'Available', 'Unavailable', 'Checking', 'Unsupported', 'NoRelease'],
    meaning: 'An update result does not certify game files or world compatibility.', next: 'Review App settings; installation remains owner initiated.' },
  'server-list-bound': { label: 'Saved server coverage', states: ['Partial'],
    meaning: 'The diagnostic projection is bounded; additional saved servers are not included.', next: 'Select the affected saved server in the app.' }
} as const

type DiagnosticFactId = keyof typeof diagnosticFacts
function reviewedFact(check: OwnerDiagnosticCheck) {
  return Object.hasOwn(diagnosticFacts, check.id) ? diagnosticFacts[check.id as DiagnosticFactId] : null
}

function diagnosticState(check: OwnerDiagnosticCheck) {
  const fact = reviewedFact(check)
  if (!fact) return 'Unavailable'
  if (fact.states.some(state => state === check.state)) return check.state
  if (check.id === 'driver-observation') {
    const count = /^Ready · (\d{1,7})(?: of (\d{1,7}))? online$/u.exec(check.state)
    if (count && Number(count[1]) <= 1_000_000 && (count[2] === undefined ||
        Number(count[2]) >= Number(count[1]) && Number(count[2]) <= 1_000_000))
      return `Ready · ${Number(count[1])}${count[2] ? ` of ${Number(count[2])}` : ''} online`
  }
  return 'Unavailable'
}

export function ownerDiagnosticExplanation(check: OwnerDiagnosticCheck) {
  const fact = reviewedFact(check)
  if (!fact) return 'Diagnostic unavailable. Refresh the fixed owner checks.'
  return `${fact.label}: ${diagnosticState(check)}.\n${fact.meaning}\nNext: ${fact.next}`
}

// This concise copy is an allowlisted projection, independent of displayed
// detail/labels/locations and the larger backend support export.
export function ownerTroubleshootingReport(view: OwnerDiagnosticsView, selectedProfileId: string) {
  const selected = view.servers.find(server => server.profileId === selectedProfileId) ?? view.servers[0]
  const checks = [...(selected?.checks ?? []), ...view.sharedChecks]
  const seen = new Set<string>()
  const facts = checks.filter(check => {
    if (!reviewedFact(check) || seen.has(check.id)) return false
    seen.add(check.id)
    return true
  }).slice(0, 10).map(ownerDiagnosticExplanation)
  return ['TogetherServer troubleshooting summary', ...facts,
    ...(view.truncated ? ['Saved-server coverage is partial.'] : []),
    'Read-only evidence. No join, saved-world, continuous availability or permission to stop a server is inferred.'].join('\n\n')
}

function DiagnosticCard({ check, onCopy, copyStatus }: {
  check: OwnerDiagnosticCheck
  onCopy: (check: OwnerDiagnosticCheck) => void
  copyStatus?: string
}) {
  const fact = reviewedFact(check)
  return <article className={`owner-diagnostic-check tone-${check.tone.toLocaleLowerCase()}`}>
    <div className="owner-diagnostic-check-heading"><span>{check.label}</span><strong>{check.state}</strong></div>
    <p>{check.detail}</p>
    <div className="owner-diagnostic-next"><span>Next</span><p>{check.nextAction}</p><small>{check.location}</small></div>
    {fact && <Button className="text-button" aria-label={`Copy explanation for ${fact.label}`} onClick={() => onCopy(check)}>Copy explanation</Button>}
    {copyStatus && <output role="status">{copyStatus}</output>}
  </article>
}

export function OwnerDiagnostics({ selectedProfileId = '', onSelectedProfileIdChange,
  diagnosticsLoader = defaultDiagnosticsLoader, supportReportLoader = defaultSupportReportLoader }: {
  selectedProfileId?: string
  onSelectedProfileIdChange?: (profileId: string) => void
  diagnosticsLoader?: OwnerDiagnosticsLoader
  supportReportLoader?: SupportReportLoader
}) {
  const [diagnostics, setDiagnostics] = useState<OwnerDiagnosticsView | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [exportBusy, setExportBusy] = useState<'copy' | 'download' | 'summary' | ''>('')
  const [exportStatus, setExportStatus] = useState<{ tone: 'neutral' | 'error'; text: string } | null>(null)
  const [explanationStatus, setExplanationStatus] = useState<{ check: OwnerDiagnosticCheck; text: string } | null>(null)
  const pending = useRef<AbortController | null>(null)
  const generation = useRef(0)

  const load = useCallback(async () => {
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    generation.current++
    setLoading(true)
    setLoadError(false)
    setExplanationStatus(null)
    try {
      const result = await diagnosticsLoader(controller.signal)
      if (!controller.signal.aborted) setDiagnostics(result)
    } catch {
      if (!controller.signal.aborted) setLoadError(true)
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [diagnosticsLoader])

  useEffect(() => {
    const operationGeneration = generation
    setDiagnostics(null)
    void load()
    return () => { pending.current?.abort(); operationGeneration.current++ }
  }, [load])

  useEffect(() => {
    if (!diagnostics || diagnostics.servers.length === 0 ||
        diagnostics.servers.some(server => server.profileId === selectedProfileId)) return
    onSelectedProfileIdChange?.(diagnostics.servers[0].profileId)
  }, [diagnostics, onSelectedProfileIdChange, selectedProfileId])

  const selected = useMemo(() => diagnostics?.servers.find(server => server.profileId === selectedProfileId) ??
    diagnostics?.servers[0] ?? null, [diagnostics, selectedProfileId])

  useEffect(() => { setExplanationStatus(null); setExportStatus(null); generation.current++ }, [selectedProfileId])

  const copyExplanation = async (check: OwnerDiagnosticCheck) => {
    const current = generation.current
    try {
      await navigator.clipboard.writeText(ownerDiagnosticExplanation(check))
      if (generation.current === current) setExplanationStatus({ check, text: 'Redacted explanation copied.' })
    } catch {
      if (generation.current === current) setExplanationStatus({ check, text: 'Could not copy the explanation. Try again.' })
    }
  }

  const copySummary = async () => {
    if (!diagnostics || exportBusy) return
    const current = generation.current
    setExportBusy('summary')
    setExportStatus(null)
    try {
      await navigator.clipboard.writeText(ownerTroubleshootingReport(diagnostics, selected?.profileId ?? ''))
      if (generation.current === current) setExportStatus({ tone: 'neutral', text: 'Concise redacted troubleshooting summary copied.' })
    } catch {
      if (generation.current === current) setExportStatus({ tone: 'error', text: 'Could not copy the troubleshooting summary. Try again.' })
    } finally { setExportBusy('') }
  }

  const exportReport = async (action: 'copy' | 'download') => {
    setExportBusy(action)
    setExportStatus(null)
    try {
      const report = await supportReportLoader()
      if (action === 'copy') {
        await navigator.clipboard.writeText(report.content)
        setExportStatus({ tone: 'neutral', text: `Redacted support report copied (${report.sizeBytes} bytes).` })
      } else {
        const url = URL.createObjectURL(new Blob([report.content], { type: report.contentType }))
        try {
          const link = document.createElement('a')
          link.href = url
          link.download = report.fileName
          link.click()
        } finally { URL.revokeObjectURL(url) }
        setExportStatus({ tone: 'neutral', text: `Downloaded ${report.fileName} (${report.sizeBytes} bytes).` })
      }
    } catch {
      setExportStatus({ tone: 'error', text: 'The redacted support report could not be created. No private error details were added.' })
    } finally { setExportBusy('') }
  }

  return <section className="owner-diagnostics" aria-labelledby="owner-diagnostics-title" aria-busy={loading}>
    <div className="owner-diagnostics-toolbar">
      <div><h3 id="owner-diagnostics-title">Preflight & diagnostics</h3>
        <p>Read-only owner checks. Opening this view never starts, stops, restarts, or repairs a server.</p></div>
      <Button className="secondary" disabled={loading} onClick={() => void load()}>{loading ? 'Refreshing…' : 'Refresh checks'}</Button>
    </div>

    {loadError && <div className="owner-diagnostics-error" role="alert"><strong>Diagnostics unavailable</strong>
      <p>TogetherServer could not assemble the fixed local checks. No server action was started.</p>
      <Button className="secondary" onClick={() => void load()}>Try again</Button></div>}

    {!loadError && diagnostics && <>
      <div className="owner-diagnostics-boundary" role="note">{diagnostics.evidenceBoundary}</div>
      {diagnostics.servers.length > 0 ? <>
        <label className="owner-diagnostics-server">Selected server<Select aria-label="Diagnostics server"
          value={selected?.profileId ?? ''} onChange={event => onSelectedProfileIdChange?.(event.target.value)}>
          {diagnostics.servers.map(server => <option key={server.profileId} value={server.profileId}>{server.label} · {server.kind}</option>)}
        </Select></label>
        {selected && <div className="owner-diagnostics-grid" aria-label={`${selected.label} diagnostic checks`}>
          {selected.checks.map(check => <DiagnosticCard key={check.id} check={check} onCopy={check => void copyExplanation(check)}
            copyStatus={explanationStatus?.check === check ? explanationStatus.text : undefined} />)}
        </div>}
      </> : <div className="owner-diagnostics-empty"><strong>No saved server yet</strong>
        <p>Save a server setup to see its process, player count, and local port checks. App checks remain available below.</p></div>}

      <div className="owner-diagnostics-shared"><h4>App, recovery, and Friend access</h4>
        <div className="owner-diagnostics-grid">{diagnostics.sharedChecks.map(check =>
          <DiagnosticCard key={check.id} check={check} onCopy={check => void copyExplanation(check)}
            copyStatus={explanationStatus?.check === check ? explanationStatus.text : undefined} />)}</div></div>
      <section className="support-export" aria-labelledby="support-export-title">
        <div><h4 id="support-export-title">Export support report</h4>
          <p>Copy summary keeps only reviewed check states and next steps. Copy report and Download JSON include the larger redacted report.</p>
          <p>Creates a size-limited JSON report without saving anything on the Host. It leaves out saved access, server codes, passwords, scripts, private paths, addresses, secure identity data, player/chat details, raw logs, worlds, and environment variables.</p></div>
        <div className="actions"><Button className="secondary" disabled={!!exportBusy} onClick={() => void copySummary()}>
          {exportBusy === 'summary' ? 'Copying…' : 'Copy summary'}</Button>
          <Button disabled={!!exportBusy} onClick={() => void exportReport('copy')}>
          {exportBusy === 'copy' ? 'Copying…' : 'Copy report'}</Button>
          <Button className="secondary" disabled={!!exportBusy} onClick={() => void exportReport('download')}>
            {exportBusy === 'download' ? 'Preparing…' : 'Download JSON'}</Button></div>
        {exportStatus && <output className={`support-export-status ${exportStatus.tone}`} role="status">{exportStatus.text}</output>}
      </section>
    </>}
  </section>
}
