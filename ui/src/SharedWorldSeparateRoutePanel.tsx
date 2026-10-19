import { useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input, TextArea } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { SharedWorldServerFile } from './SharedWorldServerFile'
import { futureHostFields, sharedSaveAge, type SharedGame } from './sharedWorldUx'
import './SharedWorldSeparateRoute.css'

type Branch = { branchHash: string; offer: { version: { number: number; createdUtc?: string; game?: string };
  proposal: { profileId: string; candidateAddress: string } } }
type RouteCheck = { controlRouteObserved: boolean; code: string; message: string }
type RequiredAddOn = { name: string; version: string; requiredGameVersion: string;
  type: string; id: string | null }
type HostStatus = { recorded: boolean; restored: boolean; readyForManualStart: boolean;
  reviewRequired: boolean; running: boolean; message: string; localProfileId: string | null;
  branchHash: string | null; preparedServerRoot: string | null;
  requiredAddOns: RequiredAddOn[] | null; controlRouteVerified?: boolean }
type Action = { ok: boolean; code: string; message: string; pendingChecks?: string[] | null }

function literalHttpsOrigin(value: string): boolean {
  try {
    const url = new URL(value)
    const ipv4 = /^(\d{1,3}\.){3}\d{1,3}$/.test(url.hostname) && url.hostname.split('.').every(part => Number(part) <= 255)
    const ipv6 = /^\[[0-9a-f:]+\]$/i.test(url.hostname)
    return url.protocol === 'https:' && (ipv4 || ipv6) && !url.username && !url.password &&
      !url.search && !url.hash && url.pathname === '/' && url.origin === value.replace(/\/$/, '')
  } catch { return false }
}

function branch(value: unknown, profileId: string): Branch {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Separate-copy proof is invalid.')
  const item = value as Record<string, unknown>
  const offer = item.offer as Record<string, unknown> | undefined
  const proposal = offer?.proposal as Record<string, unknown> | undefined
  const version = offer?.version as Record<string, unknown> | undefined
  if (typeof item.branchHash !== 'string' || !/^[0-9A-F]{64}$/.test(item.branchHash) ||
    proposal?.profileId !== profileId || typeof proposal.candidateAddress !== 'string' ||
    !literalHttpsOrigin(proposal.candidateAddress) ||
    typeof version?.number !== 'number' || !Number.isSafeInteger(version.number) ||
    version.number < 1 || version.createdUtc != null && (typeof version.createdUtc !== 'string' ||
      !Number.isFinite(Date.parse(version.createdUtc)))) throw new Error('Separate-copy proof is invalid.')
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
    item.controlRouteVerified !== undefined && typeof item.controlRouteVerified !== 'boolean' ||
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

export function SharedWorldSeparateRoutePanel({ profileId, separateCopies, game, onBrowseServerFile, expanded = false, onOpenHostControls, onOpenSeparateHostControls, apiBase,
  selectedBranchHash, suggestedSetup, allowStart = true, onPrepared, onBusyChange }:
  { profileId: string; separateCopies: number; game?: SharedGame;
    onBrowseServerFile?: (game: SharedGame) => Promise<string | null>;
    expanded?: boolean; onOpenHostControls?: () => void; onOpenSeparateHostControls?: (localProfileId: string) => void; apiBase?: string;
    selectedBranchHash?: string; suggestedSetup?: { serverFile: string | null; gameVersion: string | null; gamePort: number; controlPort: number };
    allowStart?: boolean; onPrepared?: () => void; onBusyChange?: (busy: boolean) => void }) {
  const base = apiBase ?? `/api/local/friend/${profileId}/shared-world/recovery/separate`
  const scope = useRef(profileId)
  scope.current = profileId
  const [ownBranches, setOwnBranches] = useState<Branch[]>([])
  const [shown, setShown] = useState<Branch | null>(null)
  const [hosting, setHosting] = useState<HostStatus | null>(null)
  const [serverFile, setServerFile] = useState('')
  const [gameVersion, setGameVersion] = useState('')
  const [controlPort, setControlPort] = useState('5131')
  const [gamePort, setGamePort] = useState(futureHostFields(game).gamePort)
  const [newPasswordSet, setNewPasswordSet] = useState(false)
  const [gamePassword, setGamePassword] = useState('')
  const [serverName, setServerName] = useState('Recovery world')
  const [executable, setExecutable] = useState('')
  const [factorioRconPort, setFactorioRconPort] = useState('27015')
  const [splitAccepted, setSplitAccepted] = useState(false)
  const [hostAction, setHostAction] = useState<Action | null>(null)
  const [pasted, setPasted] = useState('')
  const [result, setResult] = useState<RouteCheck | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  useEffect(() => { onBusyChange?.(busy); return () => onBusyChange?.(false) }, [busy, onBusyChange])
  const effectiveGame = game ?? shown?.offer.version.game
  const fields = futureHostFields(effectiveGame)
  const knownGame = ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria'].includes(effectiveGame ?? '')
  useEffect(() => { if (!game) setGamePort(futureHostFields(effectiveGame).gamePort) }, [effectiveGame, game])
  const noticeScope = JSON.stringify([profileId, game, shown?.branchHash])
  useEffect(() => {
    setOwnBranches([]); setShown(null); setHosting(null); setServerFile(suggestedSetup?.serverFile ?? ''); setGameVersion(suggestedSetup?.gameVersion ?? '')
    setControlPort(String(suggestedSetup?.controlPort ?? 5131)); setGamePort(String(suggestedSetup?.gamePort ?? futureHostFields(game).gamePort)); setNewPasswordSet(false); setGamePassword('')
    setExecutable(''); setFactorioRconPort('27015'); setSplitAccepted(false); setHostAction(null)
    setPasted(''); setResult(null); setError(''); setBusy(false)
  // Suggested values seed a new world only; background checks must not replace a draft.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [profileId, game])
  useEffect(() => {
    if (!selectedBranchHash) return
    let active = true
    const abort = new AbortController()
    setBusy(true); setError('')
    void (async () => {
      try {
        const list = await getLocalJson(base, value => branches(value, profileId), abort.signal)
        const selected = list.find(item => item.branchHash === selectedBranchHash)
        if (!selected) throw new Error('The exact prepared save is unavailable. Retry local hosting checks.')
        const current = await getLocalJson(`${base}/hosting/${selected.branchHash}`, hostStatus, abort.signal)
        if (active) { setOwnBranches(list); setShown(selected); setHosting(current) }
      } catch (cause) { if (active) setError(errorMessage(cause)) }
      finally { if (active) setBusy(false) }
    })()
    return () => { active = false; abort.abort() }
  }, [base, profileId, selectedBranchHash])
  const load = async () => {
    setBusy(true); setError('')
    try {
      const value = await getLocalJson(base,
        value => branches(value, profileId))
      if (scope.current === profileId) setOwnBranches(value)
    } catch (cause) { if (scope.current === profileId) setError(errorMessage(cause)) }
    finally { if (scope.current === profileId) setBusy(false) }
  }
  const refreshHost = async (selected: Branch) => {
    const next = await getLocalJson(
      `${base}/hosting/${selected.branchHash}`,
      hostStatus)
    if (scope.current === profileId) setHosting(next)
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
        `${base}/${kind}`,
        'POST', action, body)
      setHostAction(next)
      await refreshHost(shown)
      if (next.ok && kind !== 'start') onPrepared?.()
    } catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  const check = async () => {
    setBusy(true); setError(''); setResult(null)
    try {
      if (pasted.length < 2 || pasted.length > 512 * 1024) throw new Error('Separate-copy proof code is invalid or too large.')
      let parsed: unknown
      try { parsed = JSON.parse(pasted) as unknown }
      catch { throw new Error('Separate-copy proof code is invalid. Ask the candidate PC to copy it again.') }
      const proof = branch(parsed, profileId)
      const value = await changeJson(
        `/api/local/friend/${profileId}/shared-world/recovery/separate/route-check`,
        'POST', routeCheck, { branch: proof })
      if (scope.current === profileId) setResult(value)
    } catch (cause) { if (scope.current === profileId) setError(errorMessage(cause)) }
    finally { if (scope.current === profileId) setBusy(false) }
  }
  return <details className="shared-separate-copy" open={expanded || undefined}><summary>{selectedBranchHash ? 'Finish the matching server setup' : 'Recovery copies on this PC'}</summary>
    <p>Recovery keeps a separate history. Restore locally, finish setup, then Start manually.</p>
    <details className="separate-copy-guidance"><summary>Recovery steps and route checks</summary>
      <p>Restore a signed recovery branch into a fresh local world, finish local setup, then choose Start manually. A second-PC route check is optional evidence; it does not settle the split or prove the game route.</p></details>
    {separateCopies > 0 && !selectedBranchHash && <><Button className="secondary" disabled={busy}
      onClick={() => void load()}>Show recovery copies on this PC</Button>
      {ownBranches.map(item => <div key={item.branchHash}>
        <p>Recovery save {item.offer.version.number} · captured {sharedSaveAge(item.offer.version.createdUtc)}. No age cutoff applies to a complete verified copy.</p>
        <Button className="text-button" disabled={busy}
          onClick={() => void select(item)}>Prepare this recovery copy</Button>
      </div>)}
      {shown && <details><summary>Signed recovery proof</summary><p>Reviewed local HTTPS identity: {shown.offer.proposal.candidateAddress}</p><Button className="secondary" disabled={busy} onClick={() => void navigator.clipboard.writeText(JSON.stringify(shown))
        .catch(() => setError('Could not copy this proof. Select the proof code below to copy it manually.'))}>Copy separate-copy proof</Button>
        <TextArea aria-label="Signed separate-copy proof code" rows={3} readOnly value={JSON.stringify(shown)} /></details>}</>}
    {shown && hosting && <section aria-label="Host this separate copy">
      {hosting.reviewRequired ? <DismissibleNotice as="p" scope={noticeScope} role="alert">{hosting.message}</DismissibleNotice> :
        <p role="status">{hosting.message}</p>}
      {!hosting.reviewRequired && <>
        {!hosting.readyForManualStart && <p>Another game server may still be running. This copy stays separate until the group reviews both save histories.</p>}
        {hosting.requiredAddOns?.length ? <details><summary>Required add-ons</summary>
          <ul>{hosting.requiredAddOns.map(item => <li key={`${item.type}:${item.name}`}>
            {item.name} {item.version} ({item.type})</li>)}</ul>
          <p>Install these packages in this PC’s managed server folder before restoring.</p>
        </details> : null}
        <div className="separate-copy-fields">
        {!hosting.readyForManualStart && !hosting.running && <>
          {hosting.restored && <p>Select the installed server file and matching game version to finish checking this restored copy.</p>}
          <SharedWorldServerFile key={profileId} profileId={profileId} game={effectiveGame}
          value={serverFile} onChange={setServerFile} disabled={busy} onBrowseServerFile={onBrowseServerFile} />
          <label>Installed game version<Input value={gameVersion}
            onChange={event => setGameVersion(event.target.value)} /></label></>}
        {!hosting.restored && <>
          <label>New server name<Input value={serverName}
            onChange={event => setServerName(event.target.value)} /></label>
          {(effectiveGame === 'MinecraftJava' || !knownGame) && <label>{effectiveGame === 'MinecraftJava' ? 'Java runtime executable (java.exe)' : 'Installed game executable, if different'}<Input value={executable}
            onChange={event => setExecutable(event.target.value)} /></label>}
          {hosting.preparedServerRoot && <details className="separate-copy-guidance"><summary>Managed server folder and source settings</summary>
            <p>Prepare the matching server files and required add-ons in this managed folder: <code>{hosting.preparedServerRoot}</code>.</p>
            <p>Match the server settings and player allowlist to the signed source; a mismatch blocks restore and Start.</p></details>}
          {(fields.factorio || !knownGame) && <label>Factorio local RCON port, if used<Input inputMode="numeric"
            value={factorioRconPort} onChange={event => setFactorioRconPort(event.target.value)} /></label>}
          {fields.factorio && <p className="helper-text">Keep the RCON port local. Never forward it.</p>}
          {fields.password && <label>New game password, if used<Input type="password" value={gamePassword}
            onChange={event => setGamePassword(event.target.value)} /></label>}</>}
        {!hosting.readyForManualStart && !hosting.running && <><label>Future Friend control port<Input inputMode="numeric"
          value={controlPort} onChange={event => setControlPort(event.target.value)} /></label>
          <label>Future game port<Input inputMode="numeric" value={gamePort}
            onChange={event => setGamePort(event.target.value)} /></label>
          <label className="separate-copy-consent"><Input type="checkbox" checked={newPasswordSet}
            onChange={event => setNewPasswordSet(event.target.checked)} /> {fields.password ?
              'I set a new game password on this PC' : 'I reviewed local game access and set a new password where this game supports one'}</label>
          <Button className="secondary" disabled={busy} onClick={() => void hostChange(
            hosting.restored ? 'finish' : 'restore')}>
            {hosting.restored ? 'Finish local setup checks' : 'Restore into a fresh managed world'}</Button></>}
        </div>
        {hosting.readyForManualStart && allowStart && <div className="separate-copy-start-review"><DismissibleNotice scope={noticeScope} className="start-connection-notice" role="note"><strong>Before starting this recovery copy</strong><p>Local setup passed. Game load, a real game join and saved restart remain unverified. Other sessions may still run; this copy does not change the group's default history.</p></DismissibleNotice><label><Input type="checkbox" checked={splitAccepted}
          onChange={event => setSplitAccepted(event.target.checked)} /> I understand another server may still be running</label>
          <Button disabled={busy || !splitAccepted} onClick={() => void hostChange('start')}>
            Start recovery copy</Button></div>}
        {hosting.running && <DismissibleNotice scope={noticeScope} role="note"><p>This recovery session is running. A returning Host does not stop it or replace its history.</p>
          {(onOpenHostControls || onOpenSeparateHostControls) && <Button className="secondary" onClick={() =>
            hosting.localProfileId && onOpenSeparateHostControls ? onOpenSeparateHostControls(hosting.localProfileId) : onOpenHostControls?.()
          }>Open this PC's Host controls</Button>}</DismissibleNotice>}
        <p className="helper-text">{hosting.controlRouteVerified ? 'Another approved PC observed the pinned control route. A real game join remains unverified.' :
          'No second-PC control-route observation is recorded. It is optional for solo recovery.'}</p>
      </>}
      {hostAction && <DismissibleNotice as="p" scope={noticeScope} role={hostAction.ok ? 'status' : 'alert'}>{hostAction.message}</DismissibleNotice>}
      {hostAction?.pendingChecks?.length ? <ul>{hostAction.pendingChecks.map(reason =>
        <li key={reason}>{reason}</li>)}</ul> : null}
      <Button className="text-button" disabled={busy} onClick={() => void select(shown)}>
        Refresh separate-copy status</Button>
    </section>}
    <details hidden={!!apiBase || !!selectedBranchHash}><summary>Optional check from another PC</summary>
    <p>This checks a deliberately enabled pinned control listener. It is separate from solo recovery and game reachability.</p>
    <label>Proof code from candidate PC<TextArea rows={3}
      maxLength={512 * 1024} value={pasted} onChange={event => setPasted(event.target.value)} /></label>
    <Button className="secondary" disabled={busy || !pasted.trim()}
      onClick={() => void check()}>Check candidate control route from this PC</Button>
    {result && <DismissibleNotice as="p" scope={noticeScope} role={result.controlRouteObserved ? 'status' : 'alert'}>{result.message}</DismissibleNotice>}
    </details>
    {error && <DismissibleNotice as="p" scope={noticeScope} role="alert">{error}</DismissibleNotice>}
  </details>
}
