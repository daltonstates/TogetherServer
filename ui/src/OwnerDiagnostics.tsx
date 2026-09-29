import { useCallback, useEffect, useMemo, useState } from 'react'
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

function DiagnosticCard({ check }: { check: OwnerDiagnosticCheck }) {
  return <article className={`owner-diagnostic-check tone-${check.tone.toLocaleLowerCase()}`}>
    <div className="owner-diagnostic-check-heading"><span>{check.label}</span><strong>{check.state}</strong></div>
    <p>{check.detail}</p>
    <div className="owner-diagnostic-next"><span>Next</span><p>{check.nextAction}</p><small>{check.location}</small></div>
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
  const [exportBusy, setExportBusy] = useState<'copy' | 'download' | ''>('')
  const [exportStatus, setExportStatus] = useState<{ tone: 'neutral' | 'error'; text: string } | null>(null)

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true)
    setLoadError(false)
    try {
      const result = await diagnosticsLoader(signal)
      if (!signal?.aborted) setDiagnostics(result)
    } catch {
      if (!signal?.aborted) setLoadError(true)
    } finally {
      if (!signal?.aborted) setLoading(false)
    }
  }, [diagnosticsLoader])

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load])

  useEffect(() => {
    if (!diagnostics || diagnostics.servers.length === 0 ||
        diagnostics.servers.some(server => server.profileId === selectedProfileId)) return
    onSelectedProfileIdChange?.(diagnostics.servers[0].profileId)
  }, [diagnostics, onSelectedProfileIdChange, selectedProfileId])

  const selected = useMemo(() => diagnostics?.servers.find(server => server.profileId === selectedProfileId) ??
    diagnostics?.servers[0] ?? null, [diagnostics, selectedProfileId])

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
          {selected.checks.map(check => <DiagnosticCard key={check.id} check={check} />)}
        </div>}
      </> : <div className="owner-diagnostics-empty"><strong>No saved server yet</strong>
        <p>Save a server setup to see its process, player count, and local port checks. App checks remain available below.</p></div>}

      <div className="owner-diagnostics-shared"><h4>App, recovery, and Friend access</h4>
        <div className="owner-diagnostics-grid">{diagnostics.sharedChecks.map(check =>
          <DiagnosticCard key={check.id} check={check} />)}</div></div>
      <section className="support-export" aria-labelledby="support-export-title">
        <div><h4 id="support-export-title">Export support report</h4>
          <p>Creates a size-limited JSON report without saving anything on the Host. It leaves out saved access, server codes, passwords, scripts, private paths, addresses, secure identity data, player/chat details, raw logs, worlds, and environment variables.</p></div>
        <div className="actions"><Button disabled={!!exportBusy} onClick={() => void exportReport('copy')}>
          {exportBusy === 'copy' ? 'Copying…' : 'Copy report'}</Button>
          <Button className="secondary" disabled={!!exportBusy} onClick={() => void exportReport('download')}>
            {exportBusy === 'download' ? 'Preparing…' : 'Download JSON'}</Button></div>
        {exportStatus && <output className={`support-export-status ${exportStatus.tone}`} role="status">{exportStatus.text}</output>}
      </section>
    </>}
  </section>
}
