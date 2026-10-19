import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { getLocalJson } from './api'
import { Button, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { Icon } from './Icon'
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
    meaning: 'A valid setup does not prove the game started or the world works.', next: 'Review the saved setup before Start.' },
  'managed-process': { label: 'Server process', states: ['Offline', 'Starting', 'Process running', 'Listening', 'Ready', 'Stopping', 'Unknown', 'Failed'],
    meaning: 'The app checks the exact server process it recorded when starting.', next: 'Open Overview. Resolve any unknown process before starting again.' },
  'driver-observation': { label: 'Readiness and players', states: ['Not active', 'Player count unavailable', 'Offline', 'Starting', 'Process running', 'Listening', 'Ready', 'Stopping', 'Unknown', 'Failed'],
    meaning: 'A missing or old player count is Unknown. Friend and automatic Stop need a current count of zero, checked again before stopping.', next: 'Refresh the count in Players.' },
  'local-game-ports': { label: 'Local game ports', states: ['Open on PC', 'Closed on PC', 'Opening', 'Waiting', 'Loopback only', 'Unknown', 'Unavailable', 'Relay ready', 'Starting', 'Stopping', 'Failed'],
    meaning: 'An open port or ready relay does not prove an outside player can join.', next: 'Review Connection help, then try joining the game from a real Friend PC.' },
  'friend-evidence': { label: 'Friend contact', states: ['None recorded', 'Recent', 'Old'],
    meaning: 'A secure app connection does not tell us the Friend’s network or prove they joined the game.', next: 'Check the saved Friend connection, then test the game join separately.' },
  'companion-listener': { label: 'Friend app port', states: ['Off', 'Idle', 'Listening', 'Open on PC', 'Not listening', 'Closed on PC', 'Error', 'Unknown'],
    meaning: 'An open port on this PC does not prove Friends can connect securely from outside.', next: 'Review Friend access and Connection help.' },
  'route-diagnostic': { label: 'App port from internet', states: ['Not checked', 'Reachable', 'Not reachable', 'Inconclusive', 'Unavailable', 'Previous result expired'],
    meaning: 'A TCP response does not verify the Host identity, a Friend’s access or a game join.', next: 'Check the network setup and ask a Friend on another network to connect.' },
  'data-recovery': { label: 'Local data recovery', states: ['Server controls blocked', 'Review needed', 'No active notice'],
    meaning: 'App recovery checks saved settings and server records. It does not check world files.', next: 'Open Attention and review any recorded servers before confirming recovery.' },
  'update-state': { label: 'Application update', states: ['Current', 'Available', 'Unavailable', 'Checking', 'Unsupported', 'NoRelease'],
    meaning: 'An app update check does not verify game files or world compatibility.', next: 'Review App settings and choose when to update.' },
  'server-list-bound': { label: 'Servers included', states: ['Partial'],
    meaning: 'This report includes only some saved servers.', next: 'Select the affected saved server in the app.' }
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
  if (!fact) return 'Check unavailable. Refresh the diagnostics.'
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
    ...(view.truncated ? ['Only some saved servers are included.'] : []),
    'These checks do not change the server. They do not verify a game join, saved world or continuous availability, or grant permission to stop.'].join('\n\n')
}

function DiagnosticCard({ check, onCopy, copyStatus, scope }: {
  check: OwnerDiagnosticCheck
  onCopy: (check: OwnerDiagnosticCheck) => void
  copyStatus?: { text: string; result: number }
  scope: string
}) {
  const fact = reviewedFact(check)
  return <article className={`owner-diagnostic-check tone-${check.tone.toLocaleLowerCase()}`}>
    <div className="owner-diagnostic-check-heading"><span>{check.label}</span><strong>{check.state}</strong></div>
    <p>{check.detail}</p>
    <div className="owner-diagnostic-next"><span>Next</span><p>{check.nextAction}</p><small>{check.location}</small></div>
    {fact && <Button className="text-button" aria-label={`Copy explanation for ${fact.label}`} onClick={() => onCopy(check)}>Copy explanation</Button>}
    {copyStatus && <DismissibleNotice as="output" scope={`${scope}:${check.id}`} noticeKey={copyStatus.result} role="status">{copyStatus.text}</DismissibleNotice>}
  </article>
}

export function OwnerDiagnostics({ selectedProfileId = '', onSelectedProfileIdChange, profileOrder = [],
  diagnosticsLoader = defaultDiagnosticsLoader, supportReportLoader = defaultSupportReportLoader }: {
  selectedProfileId?: string
  onSelectedProfileIdChange?: (profileId: string) => void
  profileOrder?: readonly string[]
  diagnosticsLoader?: OwnerDiagnosticsLoader
  supportReportLoader?: SupportReportLoader
}) {
  const [diagnostics, setDiagnostics] = useState<OwnerDiagnosticsView | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState(false)
  const [exportBusy, setExportBusy] = useState<'copy' | 'download' | 'summary' | ''>('')
  const [exportStatus, setExportStatus] = useState<{ tone: 'neutral' | 'error'; text: string; result: number } | null>(null)
  const [explanationStatus, setExplanationStatus] = useState<{ check: OwnerDiagnosticCheck; text: string; result: number } | null>(null)
  const pending = useRef<AbortController | null>(null)
  const generation = useRef(0)
  const resultSequence = useRef(0)

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
      if (generation.current === current) setExplanationStatus({ check, text: 'Explanation copied. Private details omitted.', result: ++resultSequence.current })
    } catch {
      if (generation.current === current) setExplanationStatus({ check, text: 'Could not copy the explanation. Try again.', result: ++resultSequence.current })
    }
  }

  const copySummary = async () => {
    if (!diagnostics || exportBusy) return
    const current = generation.current
    setExportBusy('summary')
    setExportStatus(null)
    try {
      await navigator.clipboard.writeText(ownerTroubleshootingReport(diagnostics, selected?.profileId ?? ''))
      if (generation.current === current) setExportStatus({ tone: 'neutral', text: 'Summary copied. Private details omitted.', result: ++resultSequence.current })
    } catch {
      if (generation.current === current) setExportStatus({ tone: 'error', text: 'Could not copy the troubleshooting summary. Try again.', result: ++resultSequence.current })
    } finally { setExportBusy('') }
  }

  const exportReport = async (action: 'copy' | 'download') => {
    setExportBusy(action)
    setExportStatus(null)
    try {
      const report = await supportReportLoader()
      if (action === 'copy') {
        await navigator.clipboard.writeText(report.content)
        setExportStatus({ tone: 'neutral', text: `Support report copied (${report.sizeBytes} bytes). Private details omitted.`, result: ++resultSequence.current })
      } else {
        const url = URL.createObjectURL(new Blob([report.content], { type: report.contentType }))
        try {
          const link = document.createElement('a')
          link.href = url
          link.download = report.fileName
          link.click()
        } finally { URL.revokeObjectURL(url) }
        setExportStatus({ tone: 'neutral', text: `Downloaded ${report.fileName} (${report.sizeBytes} bytes).`, result: ++resultSequence.current })
      }
    } catch {
      setExportStatus({ tone: 'error', text: 'Could not create the support report. Try again.', result: ++resultSequence.current })
    } finally { setExportBusy('') }
  }

  return <section className="owner-diagnostics" aria-labelledby="owner-diagnostics-title" aria-busy={loading}>
    <div className="owner-diagnostics-toolbar">
      <div><h3 id="owner-diagnostics-title">Server diagnostics</h3>
        <p>Review setup, server status and connection checks. These checks do not change the server.</p></div>
      <Button className="secondary" disabled={loading} onClick={() => void load()}>{loading ? 'Refreshing…' : 'Refresh checks'}</Button>
    </div>

    {loadError && <DismissibleNotice scope={selectedProfileId} className="owner-diagnostics-error" role="alert"><strong>Diagnostics unavailable</strong>
      <p>Could not load the local checks. Try again.</p>
      <Button className="secondary" onClick={() => void load()}>Try again</Button></DismissibleNotice>}

    {!loadError && diagnostics && <>
      <DismissibleNotice scope={selected?.profileId ?? selectedProfileId} className="owner-diagnostics-boundary" role="note">{diagnostics.evidenceBoundary}</DismissibleNotice>
      {diagnostics.servers.length > 0 ? <>
        <label className="owner-diagnostics-server">Selected server<Select aria-label="Diagnostics server"
          value={selected?.profileId ?? ''} onChange={event => onSelectedProfileIdChange?.(event.target.value)}>
          {[...diagnostics.servers].sort((a, b) =>
            (profileOrder.includes(a.profileId) ? profileOrder.indexOf(a.profileId) : profileOrder.length) -
            (profileOrder.includes(b.profileId) ? profileOrder.indexOf(b.profileId) : profileOrder.length))
            .map(server => <option key={server.profileId} value={server.profileId}>{server.label} · {server.kind}</option>)}
        </Select></label>
        {selected && <div className="owner-diagnostics-grid" aria-label={`${selected.label} diagnostic checks`}>
          {selected.checks.map(check => <DiagnosticCard key={check.id} check={check} onCopy={check => void copyExplanation(check)}
            scope={selected.profileId} copyStatus={explanationStatus?.check === check ? explanationStatus : undefined} />)}
        </div>}
      </> : <div className="owner-diagnostics-empty"><strong>No saved server yet</strong>
        <p>Save a server setup to see its process, player count, and local port checks. App checks remain available below.</p></div>}

      <div className="owner-diagnostics-shared"><h4>App, recovery, and Friend access</h4>
        <div className="owner-diagnostics-grid">{diagnostics.sharedChecks.map(check =>
          <DiagnosticCard key={check.id} check={check} onCopy={check => void copyExplanation(check)}
            scope={selected?.profileId ?? selectedProfileId} copyStatus={explanationStatus?.check === check ? explanationStatus : undefined} />)}</div></div>
      <section className="support-export" aria-labelledby="support-export-title">
        <div><h4 id="support-export-title">Support report</h4>
          <p>Copy a short summary or save the full report. Both omit private details.</p></div>
        <div className="actions"><Button className="secondary" disabled={!!exportBusy} onClick={() => void copySummary()}>
          <Icon name="copy" />{exportBusy === 'summary' ? 'Copying…' : 'Copy summary'}</Button>
          <Button disabled={!!exportBusy} onClick={() => void exportReport('copy')}>
          <Icon name="copy" />{exportBusy === 'copy' ? 'Copying…' : 'Copy report'}</Button>
          <Button className="secondary" disabled={!!exportBusy} onClick={() => void exportReport('download')}>
            <Icon name="download" />{exportBusy === 'download' ? 'Preparing…' : 'Download JSON'}</Button></div>
        <details className="support-export-privacy"><summary>Report contents & privacy</summary>
          <p>Copy summary includes check results and next steps. Copy report and Download JSON include more diagnostic details.</p>
          <p>The report leaves out saved access, server codes, passwords, scripts, private paths, addresses, secure identity data, player and chat details, raw logs, worlds and environment variables. Copying creates no file; Download JSON saves the report on this PC.</p>
        </details>
        {exportStatus && <DismissibleNotice as="output" scope={selected?.profileId ?? selectedProfileId} noticeKey={exportStatus.result} className={`support-export-status ${exportStatus.tone}`} role="status">{exportStatus.text}</DismissibleNotice>}
      </section>
    </>}
  </section>
}
