import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import {
  parseAcceptanceResult,
  parseAcceptanceView,
  type AcceptanceView
} from './contracts'

export function AcceptanceRecorder({ profileId, visible = true }: { profileId: string; visible?: boolean }) {
  const [view, setView] = useState<AcceptanceView | null>(null)
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!visible || !profileId) return
    const controller = new AbortController()
    setError('')
    void getLocalJson(`/api/local/profiles/${profileId}/acceptance`, parseAcceptanceView, controller.signal)
      .then(setView)
      .catch(issue => { if (!(issue instanceof DOMException && issue.name === 'AbortError')) setError(errorMessage(issue)) })
    return () => controller.abort()
  }, [profileId, visible])

  const change = async (checkId: string, confirmed: boolean) => {
    setBusy(checkId)
    setError('')
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/acceptance`, 'PUT',
        parseAcceptanceResult, { checkId, confirmed })
      setView(result.view)
      if (!result.ok) setError(result.message)
    } catch (issue) { setError(errorMessage(issue)) }
    finally { setBusy('') }
  }

  return <section className="acceptance-recorder" aria-labelledby="acceptance-recorder-title">
    <div className="section-heading"><div><h3 id="acceptance-recorder-title">Acceptance Recorder</h3>
      <p>Keep owner-confirmed real-world checks tied to this exact server and route configuration.</p></div>
      <Button className="text-button" disabled={!!busy} onClick={() => {
        setView(null); setError('')
        void getLocalJson(`/api/local/profiles/${profileId}/acceptance`, parseAcceptanceView)
          .then(setView).catch(issue => setError(errorMessage(issue)))
      }}>Refresh</Button></div>
    {error && <p className="warning-text" role="alert">{error}</p>}
    {!view && !error && <p className="helper-text" role="status">Loading recorded checks…</p>}
    {view?.stale && <div className="notice bad" role="status"><strong>Configuration changed</strong>
      <p>Earlier confirmations no longer count for this setup. Confirm the checks again.</p></div>}
    {view && <div className="acceptance-checks">{view.checks.map(check => <label className="acceptance-check" key={check.id}>
      <Input type="checkbox" checked={check.confirmed} disabled={!!busy}
        onChange={event => void change(check.id, event.target.checked)} />
      <span><strong>{check.label}</strong><small>{check.evidence}</small>
        {check.confirmedUtc && <small>Owner confirmed {new Date(check.confirmedUtc).toLocaleString()}.</small>}</span>
    </label>)}</div>}
    {view && <small className="evidence-boundary">{view.evidenceBoundary}</small>}
  </section>
}
