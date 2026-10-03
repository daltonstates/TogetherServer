import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBasicResult, type BasicResult, type Device } from './contracts'

type HostStatus = { enabled: boolean; latest: { number: number; versionHash: string; createdUtc: string } | null; error: string | null }
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
  let latest: HostStatus['latest'] = null
  if (source.latest !== null) {
    const item = record(source.latest, 'Published version')
    const number = numberOrNull(item.number, 'Version number')
    if (number === null || number < 1 || typeof item.versionHash !== 'string' || !/^[0-9A-F]{64}$/.test(item.versionHash) ||
      typeof item.createdUtc !== 'string' || !Number.isFinite(Date.parse(item.createdUtc)))
      throw new Error('Published version is invalid.')
    latest = { number, versionHash: item.versionHash, createdUtc: item.createdUtc }
  }
  return { enabled: boolean(source.enabled, 'Sharing switch'), latest, error: textOrNull(source.error, 'Shared save error') }
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
    <label><Input type="checkbox" checked={status?.enabled ?? false} disabled={busy || !rollingBackupEnabled}
      onChange={event => void changeSharing(event.target.checked)} /> Share completed saves from this server</label>
    {!rollingBackupEnabled && <p className="helper-text">Enable rolling backup after Stop in protection settings first.</p>}
    {status?.latest ? <p>Latest Host version: {status.latest.number} · {new Date(status.latest.createdUtc).toLocaleString()}</p> :
      <p>No post-Stop save has been published yet.</p>}
    {status?.enabled && <label><Input type="checkbox" disabled={busy || !roster}
      checked={roster?.ownerOverride ?? true} onChange={event => void changeOverride(event.target.checked)} />
      Owner recovery override (future recovery only)</label>}
    {status?.enabled && <p className="helper-text">Signed roster revision: {roster?.revision ?? 'pending'}.
      These grants are separate from Start, Stop, and logs. Eligibility and voting do not start a Host transfer.</p>}
    {status?.enabled && eligible.map(device => {
      const grants = device.sharedWorldGrants?.[profileId] ?? { receive: false, eligibleHost: false,
        recoveryVoter: false, manageSharing: false }
      const fields: { key: keyof Grants; label: string }[] = [
        { key: 'receive', label: 'Receive' }, { key: 'eligibleHost', label: 'Eligible host' },
        { key: 'recoveryVoter', label: 'Recovery voter' }, { key: 'manageSharing', label: 'Manage sharing' }]
      return <div key={device.id}><p>{device.name} · signing identity {device.sharedWorldKeyEnrolled ? 'enrolled' : 'pending'}</p>
        {fields.map(field => <label key={field.key}><Input type="checkbox" disabled={busy || device.accessExpired}
          checked={grants[field.key]} onChange={event => void changeGrant(device.id,
            { ...grants, [field.key]: event.target.checked })} /> {field.label} for {device.name}</label>)}
        {device.sharedWorldKeyEnrolled && <Button className="text-button" disabled={busy}
          onClick={() => void resetKey(device.id)}>Reset {device.name}'s signing identity and grants</Button>}</div>
    })}
    {status?.enabled && eligible.length === 0 && <p className="helper-text">Approve and assign a Friend PC first.</p>}
    {status?.error && <p role="alert">{status.error}</p>}
    {message && <p role="status">{message}</p>}
    <Button className="text-button" disabled={busy} onClick={() => void getLocalJson(
      `/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus).then(setStatus).catch(error => setMessage(errorMessage(error)))}>Refresh shared save</Button>
    <details><summary>Technical details</summary><p>Only immutable, hash checked post-Stop backup files are sent over the existing paired HTTPS connection. Previous downloaded copies cannot be recalled.</p></details>
  </details>
}

export function FriendSharedWorlds({ profileId, available }:
  { profileId: string; available: boolean }) {
  const [status, setStatus] = useState<FriendStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  useEffect(() => {
    if (!busy) return
    const timer = window.setInterval(() => {
      void getLocalJson(`/api/local/friend/${profileId}/shared-world`, parseFriendSharedWorldStatus)
        .then(setStatus).catch(() => {})
    }, 1000)
    return () => window.clearInterval(timer)
  }, [busy, profileId])
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
  return <details className="advanced-block"><summary>Shared worlds</summary>
    <p>Receive approved completed saves into this PC's private vault. Live save sharing and takeover are not available yet.</p>
    {!available && <p>Update the Host app before receiving shared saves.</p>}
    <label><Input type="checkbox" checked={status?.consented ?? false} disabled={!available || (busy && !status?.consented)}
      onChange={event => void run('consent', event.target.checked)} /> Allow saves on this PC</label>
    <p>Last checked Host version: {status?.hostVersion ?? 'unknown'} · This PC: {status?.thisPcVersion ?? 'none'}.
      {!status?.state.startsWith('Host save source changed') && status?.hostVersion != null &&
        status.thisPcVersion != null && status.hostVersion > status.thisPcVersion &&
        ` ${status.hostVersion - status.thisPcVersion} version(s) behind.`}</p>
    {status && <p className="helper-text" role={status.state.startsWith('Host save source changed') ? 'alert' : undefined}>{status.state}</p>}
    {status && <p className="helper-text">Trust: {status.trust}.
      Roster revision {status.rosterRevision ?? 'not checked'}. This is a verified copy status, not takeover readiness.</p>}
    {status?.state === 'Receiving' && <p role="status">Receiving {status.receivedBytes} of {status.totalBytes} bytes.</p>}
    <div className="actions"><Button className="secondary" disabled={busy || !available || !status?.consented}
      onClick={() => void run('check')}>Check latest</Button>
    <Button className="secondary" disabled={busy || !available || !status?.consented}
      onClick={() => void run('pull')}>{busy ? 'Working…' : 'Receive latest save'}</Button></div>
    {message && <p role="status">{message}</p>}{status?.error && <p role="alert">{status.error}</p>}
    <details><summary>Technical details</summary><p>Transfers resume in bounded chunks. Each file is checked before an atomic vault receipt. This never replaces a live game save.</p></details>
  </details>
}
