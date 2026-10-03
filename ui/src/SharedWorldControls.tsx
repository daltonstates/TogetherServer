import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBasicResult, type BasicResult, type Device } from './contracts'
import { SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'

type HostStatus = { enabled: boolean; latest: { number: number; versionHash: string; createdUtc: string } | null; error: string | null; confirmedCopies: number; canManageSharing: boolean }
type FriendStatus = { consented: boolean; hostVersion: number | null; thisPcVersion: number | null; state: string; error: string | null }
  & { receivedBytes: number; totalBytes: number; rosterRevision: number | null; trust: string }
type Grants = { receive: boolean; eligibleHost: boolean; recoveryVoter: boolean; manageSharing: boolean }
type RosterMember = { deviceId: string; grants: Grants; revoked: boolean; accessExpiresUtc: string | null }
type Roster = { revision: number; ownerOverride: boolean; members: RosterMember[] }
type SharingMember = RosterMember & { isSelf: boolean }
type SharingView = { available: boolean; canManage: boolean; selfDeviceId: string;
  revision: number | null; code: string; message: string; members: SharingMember[]; checkedUtc: string | null }

const emptyGrants: Grants = { receive: false, eligibleHost: false, recoveryVoter: false, manageSharing: false }
const grantFields: { key: keyof Grants; label: string }[] = [
  { key: 'receive', label: 'Receive' }, { key: 'eligibleHost', label: 'Eligible host' },
  { key: 'recoveryVoter', label: 'Recovery voter' }, { key: 'manageSharing', label: 'Manage sharing' }]
const deviceIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

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
function parseGrants(value: unknown): Grants {
  const source = record(value, 'Sharing grants')
  return { receive: boolean(source.receive, 'Receive'),
    eligibleHost: boolean(source.eligibleHost, 'Eligible host'),
    recoveryVoter: boolean(source.recoveryVoter, 'Recovery voter'),
    manageSharing: boolean(source.manageSharing, 'Manage sharing') }
}
function parseRosterMember(value: unknown): RosterMember {
  const source = record(value, 'Sharing member')
  if (typeof source.deviceId !== 'string' || !deviceIdPattern.test(source.deviceId))
    throw new Error('Sharing member identity is invalid.')
  const accessExpiresUtc = textOrNull(source.accessExpiresUtc ?? null, 'Access expiry')
  if (accessExpiresUtc !== null && !Number.isFinite(Date.parse(accessExpiresUtc)))
    throw new Error('Sharing member expiry is invalid.')
  return { deviceId: source.deviceId, grants: parseGrants(source.grants),
    revoked: boolean(source.revoked, 'Sharing revocation'), accessExpiresUtc }
}
export function parseHostSharedWorldStatus(value: unknown): HostStatus {
  const source = record(value, 'Shared save status')
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
    confirmedCopies: numberOrNull(source.confirmedCopies ?? 0, 'Confirmed copies') ?? 0,
    canManageSharing: boolean(source.canManageSharing ?? true, 'Sharing management') }
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
export function parseRoster(value: unknown): Roster | null {
  if (value === null) return null
  const source = record(value, 'Shared roster')
  const revision = numberOrNull(source.revision, 'Roster revision')
  if (revision === null || revision < 1) throw new Error('Roster revision is invalid.')
  if (source.members !== undefined && (!Array.isArray(source.members) || source.members.length > 128))
    throw new Error('Sharing members are invalid.')
  return { revision, ownerOverride: boolean(source.ownerOverride, 'Owner override'),
    members: (source.members ?? []).map(parseRosterMember) }
}
export function parseSharingView(value: unknown): SharingView {
  const source = record(value, 'Sharing access')
  if (typeof source.selfDeviceId !== 'string' || !deviceIdPattern.test(source.selfDeviceId) ||
    typeof source.code !== 'string' ||
    typeof source.message !== 'string' || source.code.length > 80 || source.message.length > 300 ||
    !Array.isArray(source.members) || source.members.length > 128)
    throw new Error('Sharing access is invalid.')
  const checkedUtc = textOrNull(source.checkedUtc, 'Sharing check time')
  if (checkedUtc !== null && !Number.isFinite(Date.parse(checkedUtc)))
    throw new Error('Sharing check time is invalid.')
  const available = boolean(source.available, 'Sharing availability')
  const canManage = boolean(source.canManage, 'Manage sharing access')
  const members = source.members.map(item => {
    const member = parseRosterMember(item)
    const isSelf = boolean(record(item, 'Sharing member').isSelf, 'Own PC')
    if (isSelf !== (member.deviceId === source.selfDeviceId))
      throw new Error('Sharing member identity is invalid.')
    return { ...member, isSelf }
  })
  if (canManage && (!available || !members.some(member => member.isSelf)) ||
    !canManage && members.length > 0 ||
    new Set(members.map(member => member.deviceId)).size !== members.length)
    throw new Error('Sharing access is inconsistent.')
  return { available, canManage,
    selfDeviceId: source.selfDeviceId, revision: numberOrNull(source.revision, 'Sharing revision'),
    code: source.code, message: source.message, checkedUtc, members }
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
  const changeGrant = async (deviceId: string, field: keyof Grants, enabled: boolean) => {
    setBusy(true); setMessage('')
    try {
      const current = await getLocalJson(`/api/local/profiles/${profileId}/shared-world/governance`, parseRoster)
      const signedGrants = current?.members.find(member => member.deviceId === deviceId)?.grants
      const pendingGrants = devices.find(device => device.id === deviceId)?.sharedWorldGrants?.[profileId]
      const grants = { ...(signedGrants ?? pendingGrants ?? emptyGrants), [field]: enabled }
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
  return <details className="advanced-block"><summary>Shared saves</summary>
    <p>Send verified backups after a graceful Stop to PCs you approve. Live save capture and takeover are not available yet.</p>
    <label><Input type="checkbox" checked={status?.enabled ?? false} disabled={busy || !rollingBackupEnabled || status?.canManageSharing === false}
      onChange={event => void changeSharing(event.target.checked)} /> Share completed saves from this server</label>
    {status?.canManageSharing === false && <p className="helper-text">This PC can host and share verified saves with current members. Owner controls stay with the original owner; a Friend with Manage sharing can edit permitted member grants from Join.</p>}
    {!rollingBackupEnabled && <p className="helper-text">Enable rolling backup after Stop in protection settings first.</p>}
    {status?.latest ? <p>Copied to {status.confirmedCopies} PCs · latest saved version {status.latest.number} · {new Date(status.latest.createdUtc).toLocaleString()}</p> :
      <p>No post-Stop save has been published yet.</p>}
    {status?.enabled && <label><Input type="checkbox" disabled={busy || !roster || !status.canManageSharing}
      checked={roster?.ownerOverride ?? true} onChange={event => void changeOverride(event.target.checked)} />
      Owner has final say on save conflicts</label>}
    {status?.enabled && <p className="helper-text">Verified sharing list: {roster ? `${roster.members.filter(member => !member.revoked && (member.accessExpiresUtc === null || Date.parse(member.accessExpiresUtc) > Date.now())).length} active PCs, revision ${roster.revision}` : 'pending'}.
      These grants are separate from Start, Stop, and logs.</p>}
    {status?.enabled && eligible.map(device => {
      const signedMember = roster?.members.find(member => member.deviceId === device.id)
      const grants = signedMember?.grants ?? device.sharedWorldGrants?.[profileId] ?? emptyGrants
      return <div key={device.id}><p>{device.name} · code {device.id.slice(-6).toUpperCase()} · signing identity {device.sharedWorldKeyEnrolled ? 'enrolled' : 'pending'}</p>
        {grantFields.map(field => <label key={field.key}><Input type="checkbox" disabled={busy || device.accessExpired || !status.canManageSharing}
          checked={grants[field.key]} onChange={event => void changeGrant(device.id,
            field.key, event.target.checked)} /> {field.label} for {device.name}</label>)}
        {device.accessExpired && <p className="helper-text">Access ended for this PC. Restore access before changing its grants.</p>}
        {device.sharedWorldKeyEnrolled && <Button className="text-button" disabled={busy || !status.canManageSharing}
          onClick={() => void resetKey(device.id)}>Reset {device.name}'s signing identity and grants</Button>}</div>
    })}
    {status?.enabled && eligible.length === 0 && <p className="helper-text">Approve and assign a Friend PC first.</p>}
    {status?.enabled && <p className="helper-text">Manage sharing lets an approved Friend change another member's Receive, host eligibility, and recovery vote. Only the owner can grant Manage sharing or change the owner override.</p>}
    {status?.error && <p role="alert">{status.error}</p>}
    {message && <p role="status">{message}</p>}
    <Button className="text-button" disabled={busy} onClick={() => void Promise.all([
      getLocalJson(`/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus),
      getLocalJson(`/api/local/profiles/${profileId}/shared-world/governance`, parseRoster)
    ]).then(([nextStatus, nextRoster]) => { setStatus(nextStatus); setRoster(nextRoster) })
      .catch(error => setMessage(errorMessage(error)))}>Refresh sharing</Button>
    <details><summary>Technical details</summary><p>Copy count includes PCs that signed a confirmation for this exact version after checking every file. It was last confirmed when that PC connected; the app cannot prove its current availability. Only immutable, hash checked post-Stop backup files are sent over the existing paired HTTPS connection. Previous downloaded copies cannot be recalled.</p>
      {roster?.members.map(member => <p key={member.deviceId}>PC {member.deviceId} · {member.revoked ? 'revoked' : 'active'} · Receive {member.grants.receive ? 'yes' : 'no'} · host {member.grants.eligibleHost ? 'yes' : 'no'} · vote {member.grants.recoveryVoter ? 'yes' : 'no'} · manage {member.grants.manageSharing ? 'yes' : 'no'}</p>)}
      {status?.enabled && status.canManageSharing && <div className="actions"><Button className="secondary" disabled={busy}
        onClick={() => void repairRoster(false)}>Retry signed permissions</Button>
        <Button className="secondary" disabled={busy}
          onClick={() => void repairRoster(true)}>Review changed world source</Button></div>}
      {status?.enabled && status.canManageSharing && <p className="helper-text">Review a source change only after checking the selected world and save folder. Friends will approve its new signed group on their PCs.</p>}
    </details>
  </details>
}

export function FriendSharedWorlds({ profileId, available }:
  { profileId: string; available: boolean }) {
  const [status, setStatus] = useState<FriendStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState(false)
  const [message, setMessage] = useState('')
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
  const behind = status?.hostVersion != null && status.thisPcVersion != null &&
    status.hostVersion > status.thisPcVersion && !status.state.startsWith('Host save source changed')
  const headline = status?.state === 'Receiving' ? 'Receiving completed save' :
    status?.state === 'Low space' ? 'Low space — receiving paused' :
    status?.state === 'Stalled' ? 'Receiving stalled — retrying' :
    status?.state.startsWith('Host save source changed') ? status.state :
    behind && status?.hostVersion != null && status.thisPcVersion != null ?
      `${status.hostVersion - status.thisPcVersion} version(s) behind` : status?.state
  return <details className="advanced-block" onToggle={event => setOpen(event.currentTarget.open)}><summary>Shared worlds</summary>
    <p>Receive approved completed saves into this PC's private vault. Live save sharing and takeover are not available yet.</p>
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
    <FriendSharingManager profileId={profileId} available={available} />
    {status?.thisPcVersion != null && <SharedWorldReadinessPanel profileId={profileId} />}
    <details><summary>Technical details</summary><p>Last checked Host version: {status?.hostVersion ?? 'unknown'} · This PC: {status?.thisPcVersion ?? 'none'}.</p>
      {status?.error && <p role="alert">{status.error}</p>}
      <p>Transfers resume in bounded chunks. Each file is checked before an atomic vault receipt. This never replaces a live game save.</p></details>
  </details>
}

function FriendSharingManager({ profileId, available }: { profileId: string; available: boolean }) {
  const [view, setView] = useState<SharingView | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const check = async (): Promise<SharingView> => {
    const next = await changeJson(`/api/local/friend/${profileId}/shared-world/sharing/check`,
      'POST', parseSharingView)
    setView(next)
    return next
  }
  const open = async () => {
    if (!available) return
    setBusy(true); setMessage('')
    try { await check() }
    catch (error) { setView(null); setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const change = async (deviceId: string, field: 'receive' | 'eligibleHost' | 'recoveryVoter', enabled: boolean) => {
    setBusy(true); setMessage('')
    try {
      const current = await check()
      const target = current.members.find(member => member.deviceId === deviceId && !member.isSelf)
      if (!current.canManage || !target) {
        setMessage(current.message || 'This PC cannot change sharing for that member.')
        return
      }
      if (target.revoked || target.accessExpiresUtc !== null &&
        Date.parse(target.accessExpiresUtc) <= Date.now()) {
        setMessage('The owner must restore this PC\'s access before its sharing grants can change.')
        return
      }
      const changeRequest = { deviceId,
        receive: field === 'receive' ? enabled : target.grants.receive,
        eligibleHost: field === 'eligibleHost' ? enabled : target.grants.eligibleHost,
        recoveryVoter: field === 'recoveryVoter' ? enabled : target.grants.recoveryVoter,
        revoked: target.revoked }
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/sharing`,
        'POST', parseFriendResult, changeRequest)
      setMessage(result.message)
      await check()
    } catch (error) { setMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  return <details className="advanced-block" onToggle={event => {
    if (event.target === event.currentTarget && event.currentTarget.open) void open()
  }}><summary>Manage sharing</summary>
    {!available ? <p>Update the Host app to manage sharing.</p> :
      <p>Only PCs the owner approves can change another member's save access. This does not grant Start, Stop, or logs.</p>}
    {busy && <p role="status">Checking signed sharing permissions…</p>}
    {view && !view.canManage && <p role="alert">{view.message}</p>}
    {view?.canManage && <>
      <p>Sharing permissions verified for this PC. Check the PC code with the owner before changing another member.</p>
      {view.members.filter(member => !member.isSelf).map(member => {
        const label = `PC ending ${member.deviceId.slice(-6).toUpperCase()}`
        const expired = member.accessExpiresUtc !== null && Date.parse(member.accessExpiresUtc) <= Date.now()
        return <div key={member.deviceId}><p><strong>{label}</strong> · {member.revoked ? 'Removed from sharing' :
          expired ? 'Access ended' : 'Sharing member'}</p>
          {expired && <p className="helper-text">The owner must extend this PC's access before it can use sharing.</p>}
          {grantFields.filter(field => field.key !== 'manageSharing').map(field => <label key={field.key}>
            <Input type="checkbox" checked={member.grants[field.key]} disabled={busy || member.revoked || expired}
              onChange={event => void change(member.deviceId,
                field.key as 'receive' | 'eligibleHost' | 'recoveryVoter', event.target.checked)} />
            {field.label} for {label}</label>)}
          {member.revoked && <p className="helper-text">Ask the owner to restore this PC's access.</p>}
          {member.grants.manageSharing && <p className="helper-text">Only the owner can change this PC's Manage sharing grant.</p>}
        </div>
      })}
      {view.members.filter(member => !member.isSelf).length === 0 &&
        <p>No other approved PCs are in this world yet.</p>}
      <p className="helper-text">Only the owner can grant Manage sharing or change the owner's conflict override.</p>
    </>}
    {message && <p role="status">{message}</p>}
    <Button className="text-button" disabled={!available || busy} onClick={() => void open()}>Refresh sharing list</Button>
    <details><summary>Technical details</summary><p>Verified roster revision: {view?.revision ?? 'not checked'}.
      The signed list identifies PCs by device ID; a changed or expired grant is checked again by the Host.</p>
      {view?.members.map(member => <p key={member.deviceId}>{member.deviceId} · {member.isSelf ? 'this PC' : 'another PC'}</p>)}
    </details>
  </details>
}
