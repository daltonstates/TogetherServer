import { useRef, useState } from 'react'
import { changeJson, errorMessage } from './api'
import { Button, Select } from './Controls'
import { ContractError, type Decoder, type FriendSnapshot } from './contracts'

export type RehearsalReport = {
  schema: 1; generatedUtc: string
  networkContext: 'Loopback' | 'OwnerReportedSameLan' | 'OwnerReportedSeparateNetwork' | 'Unspecified'
  stages: { id: string; state: 'Passed' | 'Failed' | 'Unverified' | 'Unavailable'; detail: string }[]
}
const labels: Record<string, string> = {
  listener: 'Host local listener', outsideTcp: 'Independent outside TCP', connection: 'Pinned and authenticated connection',
  chat: 'Two-way server chat', transfer: 'Synthetic transfer, hashes and receipt', gameEndpoint: 'Game endpoint query',
  humanJoinLoad: 'Human game join, load and restart'
}
function record(value: unknown, context: string): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ContractError(`${context}: expected an object`)
  return value as Record<string, unknown>
}
export const parseRehearsalReport: Decoder<RehearsalReport> = (value, context = 'rehearsal') => {
  const source = record(value, context)
  if (source.schema !== 1 || typeof source.generatedUtc !== 'string' || source.generatedUtc.length > 50 || !Number.isFinite(Date.parse(source.generatedUtc)) ||
    !['Loopback', 'OwnerReportedSameLan', 'OwnerReportedSeparateNetwork', 'Unspecified'].includes(String(source.networkContext)) ||
    !Array.isArray(source.stages) || source.stages.length !== 7) throw new ContractError(`${context}: invalid rehearsal report`)
  const stages = source.stages.map((value, index) => {
    const item = record(value, context)
    if (item.id !== Object.keys(labels)[index] || !['Passed', 'Failed', 'Unverified', 'Unavailable'].includes(String(item.state)) ||
      typeof item.detail !== 'string' || item.detail.length > 500) throw new ContractError(`${context}: invalid rehearsal stage`)
    return { id: String(item.id), state: item.state as RehearsalReport['stages'][number]['state'], detail: item.detail }
  })
  if (stages[1].state === 'Passed' || stages[6].state === 'Passed') throw new ContractError(`${context}: unobserved external evidence`)
  return { schema: 1, generatedUtc: source.generatedUtc, networkContext: source.networkContext as RehearsalReport['networkContext'], stages }
}
const parseSetup: Decoder<{ ok: boolean; message: string }> = (value, context = 'rehearsal setup') => {
  const source = record(value, context)
  if (typeof source.ok !== 'boolean' || typeof source.message !== 'string' || source.message.length > 500)
    throw new ContractError(`${context}: invalid setup result`)
  return { ok: source.ok, message: source.message }
}

export function HostRemoteRehearsal() {
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const prepare = async () => {
    setBusy(true)
    try { setMessage((await changeJson('/api/local/rehearsal/prepare', 'POST', parseSetup)).message) }
    catch (error) { setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  return <details className="connection-doctor"><summary>Test with another owned PC</summary>
    <p>Prepare a disposable server with synthetic save data. Pair the development app on the test PC with this server's code, grant Receive, then allow saves on that PC. Production access and game worlds are separate.</p>
    <Button className="secondary" disabled={busy} onClick={() => void prepare()}>{busy ? 'Preparing…' : 'Prepare rehearsal'}</Button>
    {message && <p role="status">{message}</p>}
  </details>
}

export function FriendRemoteRehearsal({ snapshot }: { snapshot: FriendSnapshot }) {
  const candidates = snapshot.profiles.filter(profile => profile.kind === 'Fixture')
  const [profileId, setProfileId] = useState('')
  const [context, setContext] = useState('Unspecified')
  const [busy, setBusy] = useState(false)
  const [report, setReport] = useState<RehearsalReport | null>(null)
  const [message, setMessage] = useState('')
  const retry = useRef<{ profileId: string; requestId: string } | null>(null)
  const selected = candidates.some(profile => profile.id === profileId) ? profileId : candidates[0]?.id
  const run = async () => {
    if (!selected) return
    setBusy(true); setMessage(''); setReport(null)
    if (retry.current?.profileId !== selected) retry.current = { profileId: selected, requestId: crypto.randomUUID() }
    const controller = new AbortController()
    const timer = window.setTimeout(() => controller.abort(), 100_000)
    try {
      const next = await changeJson(`/api/local/friend/${selected}/rehearsal`, 'POST', parseRehearsalReport,
        { requestId: retry.current.requestId, networkContext: context }, controller.signal)
      setReport(next)
      if (next.stages[4].state === 'Passed') retry.current = null
    } catch (error) { setMessage(errorMessage(error)) }
    finally { window.clearTimeout(timer); setBusy(false) }
  }
  return <details className="connection-doctor"><summary>Run rehearsal</summary>
    <p>Use a separately paired disposable staging server. Your network choice is recorded as owner reported; the app cannot prove the physical network.</p>
    {candidates.length > 0 ? <>
      <label>Disposable server<Select value={selected} disabled={busy} onChange={event => { setProfileId(event.target.value); setReport(null) }}>
        {candidates.map(profile => <option key={profile.id} value={profile.id}>{profile.name}</option>)}
      </Select></label>
      <label>Test PC network<Select value={context} disabled={busy} onChange={event => { setContext(event.target.value); setReport(null) }}>
        <option value="Unspecified">Not recorded</option><option value="SameLan">Same LAN as Host</option><option value="SeparateNetwork">Another network, owner confirmed</option>
      </Select></label>
      <Button disabled={busy} onClick={() => void run()}>{busy ? 'Running rehearsal…' : 'Run rehearsal'}</Button>
    </> : <p>Prepare Connection rehearsal on the development Host and pair this development app to its code.</p>}
    {report && <><p><strong>Environment: {report.networkContext === 'Loopback' ? 'Loopback' : report.networkContext === 'OwnerReportedSameLan' ? 'Same LAN, owner reported' : report.networkContext === 'OwnerReportedSeparateNetwork' ? 'Separate network, owner reported' : 'Not recorded'}</strong></p>
      <div className="doctor-stages">{report.stages.map(stage => <article className={`doctor-stage ${stage.state === 'Failed' ? 'error' : 'neutral'}`} key={stage.id}>
        <div><small>{labels[stage.id]}</small><strong>{stage.state}</strong><p>{stage.detail}</p></div>
      </article>)}</div>
      <Button className="text-button" onClick={() => void navigator.clipboard.writeText(JSON.stringify(report, null, 2)).then(
        () => setMessage('Redacted report copied.'), () => setMessage('Clipboard is unavailable.'))}>Copy redacted report</Button>
    </>}
    {message && <p role="status">{message}</p>}
  </details>
}
