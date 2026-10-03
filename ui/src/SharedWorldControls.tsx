import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBasicResult, type BasicResult, type Device } from './contracts'
import { SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'

type HostStatus = { enabled: boolean; latest: { number: number; versionHash: string; createdUtc: string } | null; error: string | null; confirmedCopies: number;
  liveSave: { available: boolean; message: string }; canManageSharing: boolean }
type FriendStatus = { consented: boolean; hostVersion: number | null; thisPcVersion: number | null; state: string; error: string | null }
  & { receivedBytes: number; totalBytes: number; rosterRevision: number | null; trust: string }
type Grants = { receive: boolean; eligibleHost: boolean; recoveryVoter: boolean; manageSharing: boolean }
type Roster = { revision: number; ownerOverride: boolean }

function record(value: unknown, where: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new Error(`${where} is invalid.`)
  return value as Record<string, unknown>
}
function boolean(value: unknown, where: string): boolean {
  if (typeof value !== 'boolean') throw new Error(`${where} is invalid.`)
  return value
}
function numberOrNull(value: unknown, where: string): number | null {
  if (value === null) return null
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) throw new Error(`${where} is invalid.`)
  return value
}
function textOrNull(value: unknown, where: string): string | null {
  if (value === null) return null
  if (typeof value !== 'string' || value.length > 300) throw new Error(`${where} is invalid.`)
  return value
}
export function parseHostSharedWorldStatus(value: unknown): HostStatus {
  const source = record(value, 'Shared save status')
  const live = source.liveSave === undefined || source.liveSave === null ? null : record(source.liveSave, 'Live save status')
  const liveSave = live === null ? { available: false, message: 'Live save sharing is unavailable. Use a hash-verified post-Stop file copy. Game load has not been checked.' } :
    { available: boolean(live.available, 'Live save availability'), message: textOrNull(live.message, 'Live save message') ?? '' }
  if (liveSave.available || liveSave.message.length === 0) throw new Error('Live save status is invalid.')
  let latest: HostStatus['latest'] = null
  if (source.latest !== null) {
    const item = record(source.latest, 'Published version')
    const number = numberOrNull(item.number, 'Version number')
    if (number === null || number < 1 || typeof item.versionHash !== 'string' || !/^[0-9A-F]{64}$/.test(item.versionHash) ||
      typeof item.createdUtc !== 'string' || !Number.isFinite(Date.parse(item.createdUtc)))
      throw new Error('Published version is invalid.')
    latest = { number, versionHash: item.versionHash, createdUtc: item.createdUtc }
  }
  return { enabled: boolean(source.enabled, 'Sharing switch'), latest, error: textOrNull(source.error, 'Shared save error'),
    confirmedCopies: numberOrNull(source.confirmedCopies ?? 0, 'Confirmed copies') ?? 0, liveSave, canManageSharing: boolean(source.canManageSharing ?? true, 'Sharing management') }
}
export function parseFriendSharedWorldStatus(value: unknown): FriendStatus {
  const source = record(value, 'Received save status')
  if (typeof source.state !== 'string' || source.state.length > 300) throw new Error('Received save state is invalid.')
  return { consented: boolean(source.consented, 'This PC consent'),
    hostVersion: numberOrNull(source.hostVersion, 'Host version'),
    thisPcVersion: numberOrNull(source.thisPcVersion, 'This PC version'),
    state: source.state, error: textOrNull(source.error, 'Received save error'),
    receivedBytes: numberOrNull(source.receivedBytes ?? 0, 'Received bytes') ?? 0,
    totalBytes: numberOrNull(source.totalBytes ?? 0, 'Total bytes') ?? 0,
    rosterRevision: numberOrNull(source.rosterRevision ?? null, 'Roster revision'),
    trust: source.trust === undefined ? 'Roster not verified' :
      textOrNull(source.trust, 'Trust status') ?? 'Roster not verified' }
}
function parseRoster(value: unknown): Roster | null {
  if (value === null) return null
  const source = record(value, 'Shared roster')
  const revision = numberOrNull(source.revision, 'Roster revision')
  if (revision === null || revision < 1) throw new Error('Roster revision is invalid.')
  return { revision, ownerOverride: boolean(source.ownerOverride, 'Owner override') }
}
function parseFriendResult(value: unknown): BasicResult & { status?: FriendStatus } {
  const source = record(value, 'Shared save result')
  return { ...parseBasicResult(value), status: source.status === undefined || source.status === null ? undefined :
    parseFriendSharedWorldStatus(source.status) }
}

export function HostSharedSaves({ profileId, devices, rollingBackupEnabled, onGrantChanged }:
  { profileId: string; devices: Device[]; rollingBackupEnabled: boolean; onGrantChanged: () => Promise<void> }) {
  const [status, setStatus] = useState<HostStatus | null>(null)
  const [roster, setRoster] = useState<Roster | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [handoff, setHandoff] = useState<'idle' | 'waiting' | 'fenced'>('idle')
  const [successorId, setSuccessorId] = useState('')
  const [successorAddress, setSuccessorAddress] = useState('')
  const [handoffMessage, setHandoffMessage] = useState('')
  useEffect(() => {
    let active = true
    void getLocalJson(`/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus)
      .then(value => { if (active) setStatus(value) })
      .catch(error => { if (active) setMessage(errorMessage(error)) })
    void getLocalJson(`/api/local/profiles/${profileId}/shared-world/governance`, parseRoster)
      .then(value => { if (active) setRoster(value) })
      .catch(error => { if (active) setMessage(errorMessage(error)) })
    return () => { active = false }
  }, [profileId])
  const changeSharing = async (enabled: boolean) => {
    setBusy(true); setMessage('')
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/shared-world`, 'PUT', parseBasicResult, { enabled })
      setMessage(result.message)
      setStatus(await getLocalJson(`/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus))
      setRoster(await getLocalJson(`/api/local/profiles/${profileId}/shared-world/governance`, parseRoster))
    } catch (error) { setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const changeGrant = async (deviceId: string, grants: Grants) => {
    setBusy(true); setMessage('')
    try {
      const result = await changeJson(`/api/local/devices/${deviceId}/shared-world/${profileId}/grants`,
        'PUT', parseBasicResult, { grants })
      setMessage(result.message)
      await onGrantChanged()
      setRoster(await getLocalJson(`/api/local/profiles/${profileId}/shared-world/governance`, parseRoster))
    } catch (error) { setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const changeOverride = async (ownerOverride: boolean) => {
    setBusy(true); setMessage('')
    try {
      setRoster(await changeJson(`/api/local/profiles/${profileId}/shared-world/governance`,
        'PUT', parseRoster, { ownerOverride }))
      setMessage('Owner override saved in the signed roster.')
    } catch (error) { setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const repairRoster = async (reviewSourceChange: boolean) => {
    setBusy(true); setMessage('')
    try {
      if (reviewSourceChange) {
        setRoster(await changeJson(`/api/local/profiles/${profileId}/shared-world/governance`,
          'PUT', parseRoster, { reviewSourceChange: true }))
      } else {
        await changeJson('/api/local/shared-world/repair-rosters', 'POST', parseBasicResult)
        setRoster(await getLocalJson(`/api/local/profiles/${profileId}/shared-world/governance`, parseRoster))
      }
      setStatus(await getLocalJson(`/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus))
      setMessage(reviewSourceChange ? 'The changed world source has a new signed group. Friends must review it.' :
        'Signed sharing permissions are available again.')
    } catch (error) { setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const resetKey = async (deviceId: string) => {
    setBusy(true); setMessage('')
    try {
      const result = await changeJson(`/api/local/devices/${deviceId}/shared-world/re-enroll`,
        'POST', parseBasicResult)
      setMessage(result.message)
      await onGrantChanged()
      setRoster(await getLocalJson(`/api/local/profiles/${profileId}/shared-world/governance`, parseRoster))
    } catch (error) { setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const eligible = devices.filter(device => device.paired && !device.revoked && !device.approvalPending &&
    device.assignedProfileIds.includes(profileId))
  const successors = eligible.filter(device => !device.accessExpired && device.sharedWorldKeyEnrolled &&
    device.sharedWorldGrants?.[profileId]?.receive && device.sharedWorldGrants[profileId].eligibleHost)
  const runHandoff = async (action: 'prepare' | 'complete' | 'cancel') => {
    setBusy(true); setHandoffMessage('')
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/shared-world/handoff/${action}`,
        'POST', parseBasicResult, action === 'prepare' ?
          { successorDeviceId: successorId, successorAddress: successorAddress.trim() } : undefined)
      setHandoffMessage(result.message)
      if (result.ok && action === 'prepare') setHandoff('waiting')
      if (result.ok && action === 'complete') setHandoff('fenced')
      if (result.ok && action === 'cancel') setHandoff('idle')
      if (result.ok) setStatus(await getLocalJson(`/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus))
    } catch (error) { setHandoffMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  return <details className="advanced-block"><summary>Shared saves</summary>
    <p>After a graceful Stop, send hash-verified world files to approved PCs. Game load and playability have not been checked. Live save capture and automatic takeover are unavailable.</p>
    <p className="helper-text">{status?.liveSave.message ?? 'Live save sharing is unavailable. Use a hash-verified post-Stop file copy. Game load has not been checked.'}</p>
    <label><Input type="checkbox" checked={status?.enabled ?? false} disabled={busy || !rollingBackupEnabled || status?.canManageSharing === false}
      onChange={event => void changeSharing(event.target.checked)} /> Share completed saves from this server</label>
    {status?.canManageSharing === false && <p className="helper-text">This PC may host and share hash-verified post-Stop file copies after its local setup and route checks pass. Only the original owner can change sharing permissions; successor management is not available yet.</p>}
    {!rollingBackupEnabled && <p className="helper-text">Enable rolling backup after Stop in protection settings first.</p>}
    {status?.latest ? <p>Copied to {status.confirmedCopies} PCs · latest post-Stop file copy {status.latest.number} · {new Date(status.latest.createdUtc).toLocaleString()}</p> :
      <p>No post-Stop save has been published yet.</p>}
    {status?.enabled && <details><summary>Technical details and PC permissions</summary>
    <label><Input type="checkbox" disabled={busy || !roster || !status.canManageSharing}
      checked={roster?.ownerOverride ?? true} onChange={event => void changeOverride(event.target.checked)} />
      Owner recovery override (future recovery only)</label>
    <p className="helper-text">Signed roster revision: {roster?.revision ?? 'pending'}.
      These grants are separate from Start, Stop, and logs. Eligibility and voting do not start a Host transfer.</p>
    {eligible.map(device => {
      const grants = device.sharedWorldGrants?.[profileId] ?? { receive: false, eligibleHost: false,
        recoveryVoter: false, manageSharing: false }
      const fields: { key: keyof Grants; label: string }[] = [
        { key: 'receive', label: 'Receive' }, { key: 'eligibleHost', label: 'Eligible host' },
        { key: 'recoveryVoter', label: 'Recovery voter' }, { key: 'manageSharing', label: 'Manage sharing' }]
      return <div key={device.id}><p>{device.name} · signing identity {device.sharedWorldKeyEnrolled ? 'enrolled' : 'pending'}</p>
        {fields.map(field => <label key={field.key}><Input type="checkbox" disabled={busy || device.accessExpired || !status.canManageSharing}
          checked={grants[field.key]} onChange={event => void changeGrant(device.id,
            { ...grants, [field.key]: event.target.checked })} /> {field.label} for {device.name}</label>)}
        {device.sharedWorldKeyEnrolled && <Button className="text-button" disabled={busy || !status.canManageSharing}
          onClick={() => void resetKey(device.id)}>Reset {device.name}'s signing identity and grants</Button>}</div>
    })}
    {eligible.length === 0 && <p className="helper-text">Approve and assign a Friend PC first.</p>}
    </details>}
    {status?.enabled && status.canManageSharing && <section aria-label="Planned handoff">
      <h4>Move hosting to another PC</h4>
      <p>Choose an approved PC. Preparing stops this server and publishes a hash-verified file copy. Game load has not been checked. Keep this PC offline until that PC confirms the exact copy.</p>
      {handoff === 'waiting' && <p role="status">Post-Stop file copy published. Signed receipt pending from the chosen PC for this exact copy.</p>}
      {handoff === 'fenced' && <p role="status">Handoff signed. This PC can no longer host this world. The new PC still needs local setup and direct route checks.</p>}
      {handoff !== 'fenced' && <><label>Next host PC <select value={successorId} disabled={busy || handoff === 'waiting'}
        onChange={event => setSuccessorId(event.target.value)}><option value="">Choose a PC</option>
        {successors.map(device => <option key={device.id} value={device.id}>{device.name}</option>)}</select></label>
        {successors.length === 0 && <p className="helper-text">Give an approved PC Receive and Eligible host access, then enroll its signing identity.</p>}
        <details><summary>Direct route details</summary>
          <label>Next PC direct HTTPS IP address and port<Input value={successorAddress}
            disabled={busy || handoff === 'waiting'} onChange={event => setSuccessorAddress(event.target.value)}
            placeholder="https://192.0.2.10:5131" /></label>
          <p>This address is recorded in the signed offer. Confirm the next PC's direct route with its owner.</p>
        </details>
        <div className="actions"><Button className="secondary" disabled={busy || handoff === 'waiting' || !successorId || !successorAddress.trim()}
          onClick={() => void runHandoff('prepare')}>Stop and prepare file copy</Button></div></>}
      {handoff !== 'fenced' && <><p className="helper-text">After that PC confirms the exact save, complete the handoff. The app checks its signed receipt before changing who may host. If you reopen the app, use these actions to check or cancel an existing handoff; the app will confirm its state.</p>
        <div className="actions"><Button className="secondary" disabled={busy} onClick={() => void runHandoff('complete')}>Complete pending handoff</Button>
          <Button className="text-button" disabled={busy} onClick={() => void runHandoff('cancel')}>Cancel pending handoff</Button></div></>}
      {handoffMessage && <p role="status">{handoffMessage}</p>}
    </section>}
    {status?.enabled && !status.canManageSharing && <p className="helper-text">Manage sharing grants are recorded for future delegated updates. Only the original owner can change signed membership today.</p>}
    {status?.error && <p role="alert">{status.error}</p>}
    {message && <p role="status">{message}</p>}
    <Button className="text-button" disabled={busy} onClick={() => void getLocalJson(
      `/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus).then(setStatus).catch(error => setMessage(errorMessage(error)))}>Refresh shared save</Button>
    <details><summary>Technical details</summary><p>Copy count includes PCs that signed a confirmation for this exact version after checking every file. It was last confirmed when that PC connected; the app cannot prove its current availability. Only immutable, hash-verified post-Stop backup files are sent over the existing paired HTTPS connection. A hash check does not prove the game can load or play this world. Previous downloaded copies cannot be recalled.</p>
      {status?.canManageSharing && <div className="actions"><Button className="secondary" disabled={busy || !status.enabled}
        onClick={() => void repairRoster(false)}>Retry signed permissions</Button>
        <Button className="secondary" disabled={busy}
          onClick={() => void repairRoster(true)}>Review changed world source</Button></div>}
      {status?.canManageSharing && <p className="helper-text">Review a source change only after checking the selected world and save folder. Friends will approve its new signed group on their PCs.</p>}
    </details>
  </details>
}

export function FriendSharedWorlds({ profileId, available }:
  { profileId: string; available: boolean }) {
  const [status, setStatus] = useState<FriendStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState(false)
  const [message, setMessage] = useState('')
  const [stageMessage, setStageMessage] = useState('')
  const [staged, setStaged] = useState(false)
  useEffect(() => {
    if (!open) return
    const timer = window.setInterval(() => {
      void getLocalJson(`/api/local/friend/${profileId}/shared-world`, parseFriendSharedWorldStatus)
        .then(setStatus).catch(() => {})
    }, 2000)
    return () => window.clearInterval(timer)
  }, [open, profileId])
  useEffect(() => {
    let active = true
    void getLocalJson(`/api/local/friend/${profileId}/shared-world`, parseFriendSharedWorldStatus)
      .then(value => { if (active) setStatus(value) })
      .catch(error => { if (active) setMessage(errorMessage(error)) })
    return () => { active = false }
  }, [profileId])
  const run = async (action: 'consent' | 'pull' | 'check', enabled?: boolean) => {
    setBusy(true); setMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/${action}`, action === 'consent' ? 'PUT' : 'POST',
        parseFriendResult, action === 'consent' ? { enabled } : undefined)
      setMessage(result.message)
      setStatus(result.status ?? await getLocalJson(`/api/local/friend/${profileId}/shared-world`, parseFriendSharedWorldStatus))
    } catch (error) { setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const stage = async () => {
    setBusy(true); setStageMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/handoff/stage`,
        'POST', parseBasicResult)
      setStageMessage(result.message)
      setStaged(result.ok && result.code === 'StagedForSetup')
    } catch (error) { setStageMessage(errorMessage(error)); setStaged(false) }
    finally { setBusy(false) }
  }
  const behind = status?.hostVersion != null && status.thisPcVersion != null &&
    status.hostVersion > status.thisPcVersion && !status.state.startsWith('Host save source changed')
  const headline = status?.state === 'Ready' ? 'Verified copy on this PC' :
    status?.state === 'Receiving' ? 'Receiving completed save' :
    status?.state === 'Low space' ? 'Low space — receiving paused' :
    status?.state === 'Stalled' ? 'Receiving stalled — retrying' :
    status?.state.startsWith('Host save source changed') ? status.state :
    behind && status?.hostVersion != null && status.thisPcVersion != null ?
      `${status.hostVersion - status.thisPcVersion} version(s) behind` : status?.state
  return <details className="advanced-block" onToggle={event => setOpen(event.currentTarget.open)}><summary>Shared worlds</summary>
    <p>Receive approved post-Stop file copies into this PC's private vault. Files are hash-verified; game load and playability have not been checked. Live save sharing and automatic takeover are unavailable.</p>
    {!available && <p>Update the Host app before receiving shared saves.</p>}
    <label><Input type="checkbox" checked={status?.consented ?? false} disabled={!available || (busy && !status?.consented)}
      onChange={event => void run('consent', event.target.checked)} /> Allow saves on this PC</label>
    {status && <p className="helper-text" role={status.state.startsWith('Host save source changed') ? 'alert' : 'status'}>{headline}</p>}
    {status && <p className="helper-text">Trust: {status.trust}.
      Roster revision {status.rosterRevision ?? 'not checked'}. This is a verified copy status, not takeover readiness.</p>}
    {status?.state === 'Receiving' && <p role="status">Receiving {status.receivedBytes} of {status.totalBytes} bytes.</p>}
    <div className="actions"><Button className="secondary" disabled={busy || !available || !status?.consented}
      onClick={() => void run('check')}>Check latest</Button>
    <Button className="secondary" disabled={busy || !available || !status?.consented}
      onClick={() => void run('pull')}>{busy ? 'Working…' : 'Receive latest save'}</Button></div>
    {message && <p role="status">{message}</p>}
    {status?.consented && status.thisPcVersion != null && <section aria-label="Planned handoff offer">
      <h4>Planned hosting handoff</h4>
      <p>If the current host signs an offer for this PC, hash-check and stage its exact post-Stop file copy here. Game load has not been checked.</p>
      <Button className="secondary" disabled={busy || !available} onClick={() => void stage()}>Check signed offer and stage copy</Button>
      {stageMessage && <p role="status">{stageMessage}</p>}
      {staged && <p>Verified copy staged on this PC. Local server setup, save signing, and direct routes still need checking before hosting.</p>}
    </section>}
    {status?.thisPcVersion != null && <SharedWorldReadinessPanel profileId={profileId} />}
    <details><summary>Technical details</summary><p>Last checked Host version: {status?.hostVersion ?? 'unknown'} · This PC: {status?.thisPcVersion ?? 'none'}.</p>
      {status?.error && <p role="alert">{status.error}</p>}
      <p>Transfers resume in bounded chunks. Each file is checked before an atomic vault receipt. This never replaces a live game save.</p></details>
  </details>
}
