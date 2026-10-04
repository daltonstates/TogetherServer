import { useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'

type Branch = { branchHash: string; offer: { version: { number: number };
  proposal: { profileId: string; candidateAddress: string } } }
type RouteCheck = { controlRouteObserved: boolean; code: string; message: string }
type RequiredAddOn = { name: string; version: string; requiredGameVersion: string;
  type: string; id: string | null }
type HostStatus = { recorded: boolean; restored: boolean; readyForManualStart: boolean;
  reviewRequired: boolean; running: boolean; message: string; localProfileId: string | null;
  branchHash: string | null; preparedServerRoot: string | null;
  requiredAddOns: RequiredAddOn[] | null }
type Action = { ok: boolean; code: string; message: string; pendingChecks?: string[] | null }

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

function hostStatus(value: unknown): HostStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Separate-copy status is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.recorded !== 'boolean' || typeof item.restored !== 'boolean' ||
    typeof item.readyForManualStart !== 'boolean' || typeof item.reviewRequired !== 'boolean' ||
    typeof item.running !== 'boolean' ||
    typeof item.message !== 'string' ||
    (item.localProfileId !== null && typeof item.localProfileId !== 'string') ||
    (item.branchHash !== null && typeof item.branchHash !== 'string') ||
    (item.preparedServerRoot !== null && typeof item.preparedServerRoot !== 'string') ||
    (item.requiredAddOns !== null && (!Array.isArray(item.requiredAddOns) ||
      item.requiredAddOns.length > 128 || item.requiredAddOns.some(addOn =>
        !addOn || typeof addOn !== 'object' || typeof addOn.name !== 'string' ||
        typeof addOn.version !== 'string' ||
        typeof addOn.requiredGameVersion !== 'string' ||
        typeof addOn.type !== 'string' ||
        (addOn.id !== null && typeof addOn.id !== 'string')))))
    throw new Error('Separate-copy status is invalid.')
  return item as HostStatus
}

function action(value: unknown): Action {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Separate-copy response is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.ok !== 'boolean' || typeof item.code !== 'string' ||
    typeof item.message !== 'string' ||
    (item.pendingChecks !== null && item.pendingChecks !== undefined &&
      (!Array.isArray(item.pendingChecks) ||
        item.pendingChecks.some(reason => typeof reason !== 'string'))))
    throw new Error('Separate-copy response is invalid.')
  return item as Action
}

export function SharedWorldSeparateRoutePanel({ profileId, separateCopies }:
  { profileId: string; separateCopies: number }) {
  const [ownBranches, setOwnBranches] = useState<Branch[]>([])
  const [shown, setShown] = useState<Branch | null>(null)
  const [hosting, setHosting] = useState<HostStatus | null>(null)
  const [serverFile, setServerFile] = useState('')
  const [gameVersion, setGameVersion] = useState('')
  const [controlPort, setControlPort] = useState('5131')
  const [gamePort, setGamePort] = useState('')
  const [newPasswordSet, setNewPasswordSet] = useState(false)
  const [gamePassword, setGamePassword] = useState('')
  const [serverName, setServerName] = useState('Separate world')
  const [executable, setExecutable] = useState('')
  const [factorioRconPort, setFactorioRconPort] = useState('27015')
  const [splitAccepted, setSplitAccepted] = useState(false)
  const [hostAction, setHostAction] = useState<Action | null>(null)
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
  const refreshHost = async (selected: Branch) => {
    const next = await getLocalJson(
      `/api/local/friend/${profileId}/shared-world/recovery/separate/hosting/${selected.branchHash}`,
      hostStatus)
    setHosting(next)
  }
  const select = async (selected: Branch) => {
    setShown(selected); setHosting(null); setHostAction(null); setBusy(true); setError('')
    try { await refreshHost(selected) }
    catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  const setup = () => ({ serverFile: serverFile || null, gameVersion: gameVersion || null,
    enabledAddOns: hosting?.requiredAddOns ?? [], newPasswordConfigured: newPasswordSet,
    controlPort: Number(controlPort), gamePort: Number(gamePort) })
  const hostChange = async (kind: 'restore' | 'finish' | 'start') => {
    if (!shown || !hosting) return
    setBusy(true); setError(''); setHostAction(null)
    try {
      const body = kind === 'restore' ? { branchHash: shown.branchHash, setup: setup(),
        name: serverName, serverName, gamePassword: gamePassword || null,
        executablePath: executable || null,
        preparedServerRoot: hosting.preparedServerRoot || null,
        factorioRconPort: Number(factorioRconPort) } : kind === 'finish' ?
        { branchHash: shown.branchHash, setup: setup(), executablePath: executable || null,
          preparedServerRoot: hosting.preparedServerRoot || null } :
        { branchHash: shown.branchHash, localProfileId: hosting.localProfileId,
          acceptSplitWarning: splitAccepted }
      const next = await changeJson(
        `/api/local/friend/${profileId}/shared-world/recovery/separate/${kind}`,
        'POST', action, body)
      setHostAction(next)
      await refreshHost(shown)
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
        <Button className="text-button" disabled={busy}
          onClick={() => void select(item)}>Review separate copy and show proof code</Button>
      </div>)}
      {shown && <textarea className="ui-textarea" aria-label="Signed separate-copy proof code"
        rows={3} readOnly value={JSON.stringify(shown)} />}</>}
    {shown && hosting && <section aria-label="Host this separate copy">
      <p role={hosting.reviewRequired ? 'alert' : 'status'}>{hosting.message}</p>
      {!hosting.reviewRequired && <>
        <p>Another game server may still be running. This copy stays separate until the group reviews both save histories.</p>
        {hosting.requiredAddOns?.length ? <details><summary>Required add-ons</summary>
          <ul>{hosting.requiredAddOns.map(item => <li key={`${item.type}:${item.name}`}>
            {item.name} {item.version} ({item.type})</li>)}</ul>
          <p>Install these packages in this PC’s managed server folder before restoring.</p>
        </details> : null}
        {!hosting.restored && <><label>Installed game server file<Input value={serverFile}
          onChange={event => setServerFile(event.target.value)} /></label>
          <label>Installed game version<Input value={gameVersion}
            onChange={event => setGameVersion(event.target.value)} /></label>
          <label>New server name<Input value={serverName}
            onChange={event => setServerName(event.target.value)} /></label>
          <label>Installed game executable, if different<Input value={executable}
            onChange={event => setExecutable(event.target.value)} /></label>
          {hosting.preparedServerRoot && <p>Prepare the matching Minecraft server and add-ons in this managed folder: {hosting.preparedServerRoot}. Match its server.properties settings and player allowlist to the signed source; a mismatch blocks restore and Start.</p>}
          <label>Factorio local RCON port, if used<Input inputMode="numeric"
            value={factorioRconPort} onChange={event => setFactorioRconPort(event.target.value)} /></label>
          <label>New game password, if used<Input type="password" value={gamePassword}
            onChange={event => setGamePassword(event.target.value)} /></label></>}
        {!hosting.readyForManualStart && !hosting.running && <><label>Future Friend control port<Input inputMode="numeric"
          value={controlPort} onChange={event => setControlPort(event.target.value)} /></label>
          <label>Future game port<Input inputMode="numeric" value={gamePort}
            onChange={event => setGamePort(event.target.value)} /></label>
          <label><Input type="checkbox" checked={newPasswordSet}
            onChange={event => setNewPasswordSet(event.target.checked)} /> I set a new game password on this PC</label>
          <Button className="secondary" disabled={busy} onClick={() => void hostChange(
            hosting.restored ? 'finish' : 'restore')}>
            {hosting.restored ? 'Finish local and route checks' : 'Restore into a fresh managed world'}</Button></>}
        {hosting.readyForManualStart && <><label><Input type="checkbox" checked={splitAccepted}
          onChange={event => setSplitAccepted(event.target.checked)} /> I understand another server may still be running</label>
          <Button disabled={busy || !splitAccepted} onClick={() => void hostChange('start')}>
            Start warned separate copy</Button></>}
        {hosting.running && <p role="alert">This separate server is running. Use Host mode to stop the exact managed game process before group review.</p>}
      </>}
      {hostAction && <p role={hostAction.ok ? 'status' : 'alert'}>{hostAction.message}</p>}
      {hostAction?.pendingChecks?.length ? <ul>{hostAction.pendingChecks.map(reason =>
        <li key={reason}>{reason}</li>)}</ul> : null}
      <Button className="text-button" disabled={busy} onClick={() => void select(shown)}>
        Refresh separate-copy status</Button>
    </section>}
    <label>Proof code from candidate PC<textarea className="ui-textarea" rows={3}
      maxLength={512 * 1024} value={pasted} onChange={event => setPasted(event.target.value)} /></label>
    <Button className="secondary" disabled={busy || !pasted.trim()}
      onClick={() => void check()}>Check candidate control route from this PC</Button>
    {result && <p role={result.controlRouteObserved ? 'status' : 'alert'}>{result.message}</p>}
    {error && <p role="alert">{error}</p>}
  </details>
}
