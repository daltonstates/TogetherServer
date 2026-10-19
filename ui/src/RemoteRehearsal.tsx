import { useRef, useState } from 'react'
import { changeJson, errorMessage } from './api'
import { Button, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { ContractError, type Decoder, type FriendSnapshot } from './contracts'
import { orderProfiles } from './profileLists'

export type RehearsalReport = {
  schema: 1; generatedUtc: string
  networkContext: 'Loopback' | 'OwnerReportedSameLan' | 'OwnerReportedSeparateNetwork' | 'Unspecified'
  stages: { id: string; state: 'Passed' | 'Failed' | 'Unverified' | 'Unavailable'; detail: string; diagnosticCode?: string | null }[]
}
const labels: Record<string, string> = {
  listener: 'Host app port on its PC', outsideTcp: 'App port from outside the network', connection: 'Secure app connection',
  chat: 'Two-way server chat', transfer: 'Test file transfer and confirmation', gameEndpoint: 'Game connection check',
  humanJoinLoad: 'Game join, load and restart confirmed by a person'
}
function record(value: unknown, context: string): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ContractError(`${context}: expected an object`)
  return value as Record<string, unknown>
}
export const parseRehearsalReport: Decoder<RehearsalReport> = (value, context = 'rehearsal') => {
  const source = record(value, context)
  if (source.schema !== 1 || typeof source.generatedUtc !== 'string' || source.generatedUtc.length > 50 || !Number.isFinite(Date.parse(source.generatedUtc)) ||
    typeof source.networkContext !== 'string' || !['Loopback', 'OwnerReportedSameLan', 'OwnerReportedSeparateNetwork', 'Unspecified'].includes(source.networkContext) ||
    !Array.isArray(source.stages) || source.stages.length !== 7) throw new ContractError(`${context}: invalid rehearsal report`)
  const stages = source.stages.map((value, index) => {
    const item = record(value, context)
    if (item.id !== Object.keys(labels)[index] || typeof item.state !== 'string' || !['Passed', 'Failed', 'Unverified', 'Unavailable'].includes(item.state) ||
      typeof item.detail !== 'string' || item.detail.length > 500) throw new ContractError(`${context}: invalid rehearsal stage`)
    if (item.diagnosticCode !== undefined && item.diagnosticCode !== null &&
      (typeof item.diagnosticCode !== 'string' || !/^[A-Za-z][A-Za-z0-9]{0,63}$/.test(item.diagnosticCode)))
      throw new ContractError(`${context}: invalid rehearsal diagnostic code`)
    return { id: String(item.id), state: item.state as RehearsalReport['stages'][number]['state'], detail: item.detail,
      ...(item.diagnosticCode !== undefined ? { diagnosticCode: item.diagnosticCode as string | null } : {}) }
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
  const [messageResult, setMessageResult] = useState(0)
  const showMessage = (text: string) => { setMessage(text); setMessageResult(result => result + 1) }
  const prepare = async () => {
    setBusy(true)
    try { showMessage((await changeJson('/api/local/rehearsal/prepare', 'POST', parseSetup)).message) }
    catch (error) { showMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  return <details className="connection-doctor"><summary>Test with another owned PC</summary>
    <p>Prepare a temporary server with test files. On the other PC, open TogetherServer DEVELOPMENT and connect with this server's code. Grant Receive on the Host, then turn on Allow saves on this PC on the other PC. This test uses separate development data and no real world.</p>
    <Button className="secondary" disabled={busy} onClick={() => void prepare()}>{busy ? 'Preparing…' : 'Prepare connection test'}</Button>
    {message && <DismissibleNotice as="p" noticeKey={messageResult} role="status">{message}</DismissibleNotice>}
  </details>
}

export function FriendRemoteRehearsal({ snapshot }: { snapshot: FriendSnapshot }) {
  const candidates = orderProfiles(snapshot.profiles.filter(profile => profile.kind === 'Fixture'))
  const [profileId, setProfileId] = useState('')
  const [context, setContext] = useState('Unspecified')
  const [busy, setBusy] = useState(false)
  const [report, setReport] = useState<RehearsalReport | null>(null)
  const [message, setMessage] = useState('')
  const [messageResult, setMessageResult] = useState(0)
  const showMessage = (text: string) => { setMessage(text); setMessageResult(result => result + 1) }
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
    } catch (error) { showMessage(errorMessage(error)) }
    finally { window.clearTimeout(timer); setBusy(false) }
  }
  return <details className="connection-doctor"><summary>Run connection test</summary>
    <p>Use the temporary server prepared in TogetherServer DEVELOPMENT and connect with its code. You report which network this PC uses; the app cannot verify that location.</p>
    {candidates.length > 0 ? <>
      <label>Test server<Select value={selected} disabled={busy} onChange={event => { setProfileId(event.target.value); setReport(null) }}>
        {candidates.map(profile => <option key={profile.id} value={profile.id}>{profile.name}</option>)}
      </Select></label>
      <label>Test PC network<Select value={context} disabled={busy} onChange={event => { setContext(event.target.value); setReport(null) }}>
        <option value="Unspecified">Not recorded</option><option value="SameLan">Same local network as Host</option><option value="SeparateNetwork">Another network, owner confirmed</option>
      </Select></label>
      <Button disabled={busy} onClick={() => void run()}>{busy ? 'Running test…' : 'Run connection test'}</Button>
    </> : <p>Prepare a connection test on the development Host, then connect this development app with that server's code.</p>}
    {report && <><p><strong>Network: {report.networkContext === 'Loopback' ? 'This PC only' : report.networkContext === 'OwnerReportedSameLan' ? 'Same local network, owner reported' : report.networkContext === 'OwnerReportedSeparateNetwork' ? 'Separate network, owner reported' : 'Not recorded'}</strong></p>
      <div className="doctor-stages">{report.stages.map(stage => <article className={`doctor-stage ${stage.state === 'Failed' ? 'error' : 'neutral'}`} key={stage.id}>
        <div><small>{labels[stage.id]}</small><strong>{stage.state}</strong><p>{stage.detail}</p></div>
      </article>)}</div>
      <Button className="text-button" onClick={() => void navigator.clipboard.writeText(JSON.stringify(report, null, 2)).then(
        () => showMessage('Report copied. Private details omitted.'), () => showMessage('Clipboard is unavailable.'))}>Copy report</Button>
    </>}
    {message && <DismissibleNotice as="p" scope={`${snapshot.connectionId}:${selected ?? ''}`} noticeKey={messageResult} role="status">{message}</DismissibleNotice>}
  </details>
}
