import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBasicResult, type BasicResult, type Device } from './contracts'
import { SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'

type HostStatus = { enabled: boolean; latest: { number: number; versionHash: string; createdUtc: string } | null; error: string | null; confirmedCopies: number;
  liveSave: { available: boolean; message: string } }
type FriendStatus = { consented: boolean; hostVersion: number | null; thisPcVersion: number | null; state: string; error: string | null }
  & { receivedBytes: number; totalBytes: number; rosterRevision: number | null; trust: string }
type Grants = { receive: boolean; eligibleHost: boolean; recoveryVoter: boolean; manageSharing: boolean }
type Roster = { revision: number; ownerOverride: boolean }
type HandoffStatus = { pending: boolean; code: string; message: string; successorDeviceId: string | null;
  finalVersion: number | null; receiptConfirmed: boolean; canComplete: boolean; canCancel: boolean }
type RecoveryOffer = { proposal: { profileId: string; candidateAddress: string; candidatePublicKey: string };
  version: { number: number; versionHash: string; captureKind: string }; candidateReceipt: { deviceId: string } }
type RecoveryStatus = { state: 'NoOffer' | 'OfferArmed' | 'OfferClosed' | 'MajorityRecorded'; votes: number;
  required: number; version: number | null; versionHash: string | null; candidateAddress: string | null;
  candidateDeviceId: string | null; majorityReached: boolean; separateCopies: number }

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
function shortText(value: unknown, where: string, max = 300): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > max) throw new Error(`${where} is invalid.`)
  return value
}
function guid(value: unknown, where: string): string {
  const result = shortText(value, where, 36)
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(result))
    throw new Error(`${where} is invalid.`)
  return result
}
function hash(value: unknown, where: string): string {
  const result = shortText(value, where, 64)
  if (!/^[0-9a-f]{64}$/i.test(result)) throw new Error(`${where} is invalid.`)
  return result
}
export function parseRecoveryOffer(value: unknown, profileId: string): RecoveryOffer {
  const source = record(value, 'Recovery offer')
  const proposal = record(source.proposal, 'Recovery proposal')
  const version = record(source.version, 'Recovery save')
  const receipt = record(source.candidateReceipt, 'Candidate receipt')
  if (guid(proposal.profileId, 'World ID').toLowerCase() !== profileId.toLowerCase())
    throw new Error('The offer names another world.')
  const address = shortText(proposal.candidateAddress, 'Candidate address', 255)
  let parsed: URL
  try { parsed = new URL(address) } catch { throw new Error('Candidate address is invalid.') }
  if (parsed.protocol !== 'https:' || parsed.username || parsed.password || parsed.search || parsed.hash ||
    parsed.pathname !== '/' || parsed.origin !== address.replace(/\/$/, ''))
    throw new Error('Candidate address is invalid.')
  const number = numberOrNull(version.number, 'Save version')
  if (number === null || number < 1 || version.captureKind !== 'PostStopBackup')
    throw new Error('Only a completed post-Stop save can be offered.')
  guid(receipt.deviceId, 'Candidate PC')
  shortText(proposal.candidatePublicKey, 'Candidate identity', 500)
  hash(version.versionHash, 'Save hash')
  return value as RecoveryOffer
}
function parseRecoveryStatus(value: unknown): RecoveryStatus {
  const source = record(value, 'Recovery status')
  if (!['NoOffer', 'OfferArmed', 'OfferClosed', 'MajorityRecorded'].includes(String(source.state)))
    throw new Error('Recovery state is invalid.')
  const votes = numberOrNull(source.votes, 'Votes')
  const required = numberOrNull(source.required, 'Required votes')
  const separateCopies = numberOrNull(source.separateCopies, 'Separate copies')
  if (votes === null || votes > 128 || required === null || required > 129 || separateCopies === null || separateCopies > 20)
    throw new Error('Recovery counts are invalid.')
  const version = numberOrNull(source.version, 'Save version')
  const versionHash = source.versionHash === null ? null : hash(source.versionHash, 'Save hash')
  const candidateAddress = source.candidateAddress === null ? null : shortText(source.candidateAddress, 'Candidate address', 255)
  const candidateDeviceId = source.candidateDeviceId === null ? null : guid(source.candidateDeviceId, 'Candidate PC')
  const majorityReached = boolean(source.majorityReached, 'Majority decision')
  if (source.state === 'OfferArmed' && (version === null || versionHash === null ||
    candidateAddress === null || candidateDeviceId === null)) throw new Error('Armed offer status is incomplete.')
  return { state: source.state as RecoveryStatus['state'], votes, required, separateCopies,
    version, versionHash, candidateAddress, candidateDeviceId, majorityReached }
}
function parseHandoffStatus(value: unknown): HandoffStatus {
  const source = record(value, 'Planned handoff')
  const code = shortText(source.code, 'Handoff state')
  return { pending: boolean(source.pending, 'Pending handoff'), code,
    message: shortText(source.message, 'Handoff message'),
    successorDeviceId: source.successorDeviceId === null || source.successorDeviceId === undefined ? null :
      guid(source.successorDeviceId, 'Next PC'), finalVersion: numberOrNull(source.finalVersion ?? null, 'Final version'),
    receiptConfirmed: boolean(source.receiptConfirmed, 'Receipt'),
    canComplete: boolean(source.canComplete, 'Complete permission'), canCancel: boolean(source.canCancel, 'Cancel permission') }
}
export function parseHostSharedWorldStatus(value: unknown): HostStatus {
  const source = record(value, 'Shared save status')
  const live = source.liveSave === undefined || source.liveSave === null ? null : record(source.liveSave, 'Live save status')
  const liveSave = live === null ? { available: false, message: 'Live save sharing is unavailable. Use a verified post-Stop copy.' } :
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
    confirmedCopies: numberOrNull(source.confirmedCopies ?? 0, 'Confirmed copies') ?? 0, liveSave }
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
function parseOfferResult(value: unknown, profileId: string): BasicResult & { offer: RecoveryOffer | null } {
  const source = record(value, 'Recovery offer result')
  const result = parseBasicResult(value)
  return { ...result, offer: source.offer === null || source.offer === undefined ? null :
    parseRecoveryOffer(source.offer, profileId) }
}
function parseVoteResult(value: unknown): BasicResult & { votes: number; required: number; majorityReached: boolean } {
  const source = record(value, 'Recovery vote result')
  const votes = numberOrNull(source.votes, 'Votes')
  const required = numberOrNull(source.required, 'Required votes')
  if (votes === null || votes > 128 || required === null || required > 129) throw new Error('Vote count is invalid.')
  return { ...parseBasicResult(value), votes, required, majorityReached: source.decision != null }
}

export function HostSharedSaves({ profileId, devices, rollingBackupEnabled, onGrantChanged }:
  { profileId: string; devices: Device[]; rollingBackupEnabled: boolean; onGrantChanged: () => Promise<void> }) {
  const [status, setStatus] = useState<HostStatus | null>(null)
  const [roster, setRoster] = useState<Roster | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [handoff, setHandoff] = useState<HandoffStatus | null>(null)
  const [successorId, setSuccessorId] = useState('')
  const [successorAddress, setSuccessorAddress] = useState('')
  const [handoffMessage, setHandoffMessage] = useState('')
  const refreshHandoff = async () => setHandoff(await getLocalJson(
    `/api/local/profiles/${profileId}/shared-world/handoff`, parseHandoffStatus))
  const refreshHost = async () => {
    const [nextStatus, nextHandoff] = await Promise.all([
      getLocalJson(`/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus),
      getLocalJson(`/api/local/profiles/${profileId}/shared-world/handoff`, parseHandoffStatus)])
    setStatus(nextStatus); setHandoff(nextHandoff)
  }
  useEffect(() => {
    let active = true
    void getLocalJson(`/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus)
      .then(value => { if (active) setStatus(value) })
      .catch(error => { if (active) setMessage(errorMessage(error)) })
    void getLocalJson(`/api/local/profiles/${profileId}/shared-world/governance`, parseRoster)
      .then(value => { if (active) setRoster(value) })
      .catch(error => { if (active) setMessage(errorMessage(error)) })
    void getLocalJson(`/api/local/profiles/${profileId}/shared-world/handoff`, parseHandoffStatus)
      .then(value => { if (active) setHandoff(value) })
      .catch(error => { if (active) setHandoffMessage(errorMessage(error)) })
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
      if (result.ok) await refreshHost()
      else await refreshHandoff()
    } catch (error) { setHandoffMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  return <details className="advanced-block"><summary>Shared saves</summary>
    <p>Send verified backups after a graceful Stop to PCs you approve. Live save capture and automatic takeover are not available yet.</p>
    <p className="helper-text">{status?.liveSave.message ?? 'Live save sharing is unavailable. Use a verified post-Stop copy.'}</p>
    <label><Input type="checkbox" checked={status?.enabled ?? false} disabled={busy || !rollingBackupEnabled}
      onChange={event => void changeSharing(event.target.checked)} /> Share completed saves from this server</label>
    {!rollingBackupEnabled && <p className="helper-text">Enable rolling backup after Stop in protection settings first.</p>}
    {status?.latest ? <p>Copied to {status.confirmedCopies} PCs · latest saved version {status.latest.number} · {new Date(status.latest.createdUtc).toLocaleString()}</p> :
      <p>No post-Stop save has been published yet.</p>}
    {status?.enabled && <details><summary>Technical details and PC permissions</summary>
    <label><Input type="checkbox" disabled={busy || !roster}
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
        {fields.map(field => <label key={field.key}><Input type="checkbox" disabled={busy || device.accessExpired}
          checked={grants[field.key]} onChange={event => void changeGrant(device.id,
            { ...grants, [field.key]: event.target.checked })} /> {field.label} for {device.name}</label>)}
        {device.sharedWorldKeyEnrolled && <Button className="text-button" disabled={busy}
          onClick={() => void resetKey(device.id)}>Reset {device.name}'s signing identity and grants</Button>}</div>
    })}
    {eligible.length === 0 && <p className="helper-text">Approve and assign a Friend PC first.</p>}
    </details>}
    {status?.enabled && <section aria-label="Planned handoff">
      <h4>Move hosting to another PC</h4>
      <p>Choose an approved PC. Preparing stops this server and publishes its final verified save. Keep this PC offline until that PC confirms the exact copy.</p>
      {handoff?.pending && <p role={handoff.code === 'HandoffReviewRequired' ? 'alert' : 'status'}>{handoff.message}
        {handoff.finalVersion != null && ` Final save version ${handoff.finalVersion}.`}
        {handoff.receiptConfirmed && ' Signed receipt confirmed.'}</p>}
      {!handoff?.pending && handoff !== null && <p className="helper-text">No pending handoff. If this PC was fenced, review its shared-world status before hosting.</p>}
      {!handoff?.pending && <><label>Next host PC <select value={successorId} disabled={busy || handoff === null}
        onChange={event => setSuccessorId(event.target.value)}><option value="">Choose a PC</option>
        {successors.map(device => <option key={device.id} value={device.id}>{device.name}</option>)}</select></label>
        {successors.length === 0 && <p className="helper-text">Give an approved PC Receive and Eligible host access, then enroll its signing identity.</p>}
        <details><summary>Technical details</summary>
          <label>Next PC direct HTTPS IP address and port<Input value={successorAddress}
            disabled={busy || handoff === null} onChange={event => setSuccessorAddress(event.target.value)}
            placeholder="https://192.0.2.10:5131" /></label>
          <p>This address is recorded in the signed offer. Confirm the next PC's direct route with its owner.</p>
        </details>
        <div className="actions"><Button className="secondary" disabled={busy || handoff === null || !successorId || !successorAddress.trim()}
          onClick={() => void runHandoff('prepare')}>Stop and prepare final save</Button></div></>}
      {handoff?.pending && <><p className="helper-text">Complete only after the exact signed receipt is confirmed.</p>
        <div className="actions"><Button className="secondary" disabled={busy || !handoff.canComplete} onClick={() => void runHandoff('complete')}>Complete pending handoff</Button>
          <Button className="text-button" disabled={busy || !handoff.canCancel} onClick={() => void runHandoff('cancel')}>Cancel pending handoff</Button></div></>}
      {handoffMessage && <p role="status">{handoffMessage}</p>}
    </section>}
    {status?.error && <p role="alert">{status.error}</p>}
    {message && <p role="status">{message}</p>}
    <Button className="text-button" disabled={busy} onClick={() => void refreshHost().catch(error => setMessage(errorMessage(error)))}>Refresh shared save</Button>
    <details><summary>Technical details</summary><p>Copy count includes PCs that signed a confirmation for this exact version after checking every file. It was last confirmed when that PC connected; the app cannot prove its current availability. Only immutable, hash checked post-Stop backup files are sent over the existing paired HTTPS connection. Previous downloaded copies cannot be recalled.</p>
      <div className="actions">{status?.enabled && <Button className="secondary" disabled={busy}
        onClick={() => void repairRoster(false)}>Retry signed permissions</Button>}
        <Button className="secondary" disabled={busy}
          onClick={() => void repairRoster(true)}>Review changed world source</Button></div>
      <p className="helper-text">Review a source change only after checking the selected world and save folder. Friends will approve its new signed group on their PCs.</p>
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
  const [recovery, setRecovery] = useState<RecoveryStatus | null>(null)
  const [recoveryMessage, setRecoveryMessage] = useState('')
  const [offerCode, setOfferCode] = useState('')
  const [candidateCode, setCandidateCode] = useState<RecoveryOffer | null>(null)
  const [reviewedOffer, setReviewedOffer] = useState<RecoveryOffer | null>(null)
  const [splitAccepted, setSplitAccepted] = useState(false)
  const [voteCount, setVoteCount] = useState<{ votes: number; required: number; majorityReached: boolean } | null>(null)
  const refreshRecovery = async () => setRecovery(await getLocalJson(
    `/api/local/friend/${profileId}/shared-world/recovery`, parseRecoveryStatus))
  const showCandidateCode = async () => {
    setRecoveryMessage('')
    try { setCandidateCode(await getLocalJson(
      `/api/local/friend/${profileId}/shared-world/recovery/offer-code`, value => parseRecoveryOffer(value, profileId))) }
    catch (error) { setRecoveryMessage(errorMessage(error)) }
  }
  useEffect(() => {
    let active = true
    void getLocalJson(`/api/local/friend/${profileId}/shared-world/recovery`, parseRecoveryStatus)
      .then(value => { if (active) setRecovery(value) })
      .catch(error => { if (active) setRecoveryMessage(errorMessage(error)) })
    return () => { active = false }
  }, [profileId])
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
  const prepareOffer = async () => {
    setBusy(true); setRecoveryMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/recovery/offer`,
        'POST', value => parseOfferResult(value, profileId))
      setRecoveryMessage(result.message)
      if (result.ok) { setCandidateCode(result.offer); await refreshRecovery() }
    } catch (error) { setRecoveryMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const reviewOffer = () => {
    setReviewedOffer(null); setRecoveryMessage('')
    try {
      if (offerCode.length < 2 || offerCode.length > 512 * 1024) throw new Error('Offer code is invalid or too large.')
      setReviewedOffer(parseRecoveryOffer(JSON.parse(offerCode) as unknown, profileId))
    } catch (error) { setRecoveryMessage(`Invalid offer code: ${errorMessage(error)}`) }
  }
  const vote = async () => {
    if (!reviewedOffer) return
    setBusy(true); setRecoveryMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/recovery/vote`,
        'POST', parseVoteResult, reviewedOffer)
      setRecoveryMessage(result.message)
      if (result.ok) setVoteCount(result)
      await refreshRecovery()
    } catch (error) { setRecoveryMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const separate = async () => {
    if (!splitAccepted) return
    setBusy(true); setRecoveryMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/recovery/separate`,
        'POST', parseBasicResult, { acceptSplitWarning: true })
      setRecoveryMessage(result.message)
      if (result.ok) { setSplitAccepted(false); await refreshRecovery() }
    } catch (error) { setRecoveryMessage(errorMessage(error)) }
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
    <p>Receive approved completed saves into this PC's private vault. Live save sharing and automatic takeover are not available yet.</p>
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
      <p>If the current host signs an offer for this PC, check and stage its exact final save here.</p>
      <Button className="secondary" disabled={busy || !available} onClick={() => void stage()}>Check signed offer and stage copy</Button>
      {stageMessage && <p role="status">{stageMessage}</p>}
      {staged && <p>Verified copy staged on this PC. Local server setup, save signing, and direct routes still need checking before hosting.</p>}
    </section>}
    {status?.consented && status.thisPcVersion != null && <details aria-label="Shared world recovery">
      <summary>Recover after Host loss</summary>
      <p>A candidate can offer this PC's completed save after two minutes without the Host. Each approved PC checks the signed offer before voting. No game starts here.</p>
      <Button className="secondary" disabled={busy || !available} onClick={() => void prepareOffer()}>Prepare signed offer</Button>
      {recovery?.candidateAddress && <p>Candidate PC {recovery.candidateDeviceId} · save version {recovery.version} · {recovery.candidateAddress}</p>}
      {recovery?.required ? <p role="status">Votes {recovery.votes}/{recovery.required}. {recovery.majorityReached ?
        'Majority decision recorded. Game setup and route checks remain.' : 'Majority decision pending.'}</p> :
        <p className="helper-text">No candidate offer is armed on this PC.</p>}
      {recovery?.separateCopies ? <p role="status">Separate history recorded on this PC. It has not started a game server.</p> : null}
      <label>Offer code from candidate PC<textarea className="ui-textarea" rows={3} value={offerCode} maxLength={512 * 1024}
        onChange={event => { setOfferCode(event.target.value); setReviewedOffer(null); setVoteCount(null) }} /></label>
      <Button className="secondary" disabled={busy || !offerCode.trim()} onClick={reviewOffer}>Review offer code</Button>
      {reviewedOffer && <div><p>Candidate PC {reviewedOffer.candidateReceipt.deviceId} · save version {reviewedOffer.version.number} · {reviewedOffer.proposal.candidateAddress}</p>
        <p role="alert">Another Host may still be running. Voting for a different save history could split this world. Check the candidate PC, address, and save version with the group.</p>
        <Button className="secondary" disabled={busy || !available} onClick={() => void vote()}>Check and vote for this offer</Button></div>}
      {voteCount && !recovery?.required && <p role="status">Votes {voteCount.votes}/{voteCount.required}. {voteCount.majorityReached ?
        'Majority decision recorded.' : 'Majority decision pending.'}</p>}
      {recovery?.state === 'OfferArmed' && <div><p role="alert">Separate copy: another game server may still be running. This creates a separate history that will need group review. It does not start a game server.</p>
        <label><Input type="checkbox" checked={splitAccepted} onChange={event => setSplitAccepted(event.target.checked)} /> I understand this world may split into separate histories</label>
        <Button className="secondary" disabled={busy || !splitAccepted} onClick={() => void separate()}>Record separate history</Button></div>}
      {recoveryMessage && <p role="status">{recoveryMessage}</p>}
      <Button className="text-button" disabled={busy} onClick={() => void refreshRecovery().catch(error => setRecoveryMessage(errorMessage(error)))}>Refresh recovery</Button>
      {recovery?.state === 'OfferArmed' && <details><summary>Technical details</summary>
        <p>Share this signed offer code with approved voters. It names the candidate address and exact completed save. It does not authorize game Start.</p>
        <Button className="secondary" disabled={busy} onClick={() => void showCandidateCode()}>Show signed offer code</Button>
        {candidateCode && <textarea className="ui-textarea" rows={3} aria-label="Signed offer code" readOnly value={JSON.stringify(candidateCode)} />}
        <p>Save hash: {recovery.versionHash}</p>
      </details>}
    </details>}
    {status?.thisPcVersion != null && <SharedWorldReadinessPanel profileId={profileId} />}
    <details><summary>Technical details</summary><p>Last checked Host version: {status?.hostVersion ?? 'unknown'} · This PC: {status?.thisPcVersion ?? 'none'}.</p>
      {status?.error && <p role="alert">{status.error}</p>}
      <p>Transfers resume in bounded chunks. Each file is checked before an atomic vault receipt. This never replaces a live game save.</p></details>
  </details>
}
