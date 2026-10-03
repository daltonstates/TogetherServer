import { useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button } from './Controls'

type Branch = { branchHash: string; offer: { version: { number: number };
  proposal: { profileId: string; candidateAddress: string } } }
type RouteCheck = { controlRouteObserved: boolean; code: string; message: string }

function branch(value: unknown, profileId: string): Branch {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Separate-copy proof is invalid.')
  const item = value as Record<string, unknown>
  const offer = item.offer as Record<string, unknown> | undefined
  const proposal = offer?.proposal as Record<string, unknown> | undefined
  const version = offer?.version as Record<string, unknown> | undefined
  if (typeof item.branchHash !== 'string' || !/^[0-9A-F]{64}$/.test(item.branchHash) ||
    proposal?.profileId !== profileId || typeof proposal.candidateAddress !== 'string' ||
    typeof version?.number !== 'number' || !Number.isSafeInteger(version.number) ||
    version.number < 1) throw new Error('Separate-copy proof is invalid.')
  return value as Branch
}

function branches(value: unknown, profileId: string): Branch[] {
  if (!Array.isArray(value) || value.length > 20) throw new Error('Separate-copy list is invalid.')
  return value.map(item => branch(item, profileId))
}

function routeCheck(value: unknown): RouteCheck {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Route response is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.controlRouteObserved !== 'boolean' || typeof item.code !== 'string' ||
    typeof item.message !== 'string' || item.message.length > 500)
    throw new Error('Route response is invalid.')
  return item as RouteCheck
}

export function SharedWorldSeparateRoutePanel({ profileId, separateCopies }:
  { profileId: string; separateCopies: number }) {
  const [ownBranches, setOwnBranches] = useState<Branch[]>([])
  const [shown, setShown] = useState<Branch | null>(null)
  const [pasted, setPasted] = useState('')
  const [result, setResult] = useState<RouteCheck | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const load = async () => {
    setBusy(true); setError('')
    try {
      setOwnBranches(await getLocalJson(`/api/local/friend/${profileId}/shared-world/recovery/separate`,
        value => branches(value, profileId)))
    } catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  const check = async () => {
    setBusy(true); setError(''); setResult(null)
    try {
      let parsed: unknown
      try { parsed = JSON.parse(pasted) as unknown }
      catch { throw new Error('Separate-copy proof code is invalid. Ask the candidate PC to copy it again.') }
      const proof = branch(parsed, profileId)
      setResult(await changeJson(
        `/api/local/friend/${profileId}/shared-world/recovery/separate/route-check`,
        'POST', routeCheck, { branch: proof }))
    } catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  return <details><summary>Separate-copy route check</summary>
    <p>A second approved Friend PC must check the candidate’s direct HTTPS address. This checks the control route only. It does not settle the split or prove the game route.</p>
    {separateCopies > 0 && <><Button className="secondary" disabled={busy}
      onClick={() => void load()}>Show this PC’s signed separate-copy proofs</Button>
      {ownBranches.map(item => <div key={item.branchHash}>
        <p>Separate save version {item.offer.version.number} · {item.offer.proposal.candidateAddress}</p>
        <Button className="text-button" onClick={() => setShown(item)}>Show proof code</Button>
      </div>)}
      {shown && <textarea className="ui-textarea" aria-label="Signed separate-copy proof code"
        rows={3} readOnly value={JSON.stringify(shown)} />}</>}
    <label>Proof code from candidate PC<textarea className="ui-textarea" rows={3}
      maxLength={512 * 1024} value={pasted} onChange={event => setPasted(event.target.value)} /></label>
    <Button className="secondary" disabled={busy || !pasted.trim()}
      onClick={() => void check()}>Check candidate control route from this PC</Button>
    {result && <p role={result.controlRouteObserved ? 'status' : 'alert'}>{result.message}</p>}
    {error && <p role="alert">{error}</p>}
  </details>
}
