import { useCallback, useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBasicResult, type BasicResult, type Device } from './contracts'
import { SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'

type HostStatus = { enabled: boolean; latest: { number: number; versionHash: string; createdUtc: string } | null; error: string | null; confirmedCopies: number;
  liveSave: { available: boolean; message: string }; canManageSharing: boolean; authority: AuthorityStatus | null }
type AuthorityHead = { groupId: string; epoch: number; recordHash: string; versionHash: string;
  hostDeviceId: string; hostPublicKey: string; hostAddress: string }
type AuthorityStatus = { state: 'NoTakeover' | 'OldHostFenced' | 'ThisPcHost' | 'CompetingHistories' | 'ReviewRequired';
  message: string; head: AuthorityHead | null; competingHeads: AuthorityHead[]; exactManagedProcessRunning: boolean }
type FriendStatus = { consented: boolean; hostVersion: number | null; thisPcVersion: number | null; state: string; error: string | null }
  & { receivedBytes: number; totalBytes: number; rosterRevision: number | null; trust: string }
type Grants = { receive: boolean; eligibleHost: boolean; recoveryVoter: boolean; manageSharing: boolean }
type Roster = { revision: number; ownerOverride: boolean }
type HandoffStatus = { pending: boolean; code: string; message: string; successorDeviceId: string | null;
  finalVersion: number | null; receiptConfirmed: boolean; canComplete: boolean; canCancel: boolean }
type RecoveryOffer = { proposal: { profileId: string; candidateAddress: string; candidatePublicKey: string };
  version: { number: number; versionHash: string; captureKind: string }; candidateReceipt: { deviceId: string } }
type RecoveryStatus = { state: 'NoOffer' | 'OfferArmed' | 'OfferClosed' | 'MajorityRecorded' |
  'ObservedMajority' | 'HistoricalRecovery' | 'HistoryReviewRequired'; votes: number;
  required: number; version: number | null; versionHash: string | null; candidateAddress: string | null;
  candidateDeviceId: string | null; majorityReached: boolean; separateCopies: number }
  & { proposalHash: string | null; authorityHeadHash: string | null }
type ResolutionChoice = { recordHash: string; version: number; availableHere: boolean }
type PendingResolution = { proposalHash: string; selectedVersion: number; competingBranches: number }

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
export function normalizedDirectIpHttpsEndpoint(value: string): string | null {
  try {
    const url = new URL(value)
    if (url.protocol !== 'https:' || !url.port || url.pathname !== '/' || url.search || url.hash ||
      url.username || url.password) return null
    const host = url.hostname.toLowerCase()
    const ipv4 = /^(\d{1,3}\.){3}\d{1,3}$/.test(host)
    const ipv6 = /^\[[0-9a-f:.]+\]$/.test(host) && host.includes(':')
    if (!ipv4 && !ipv6 || host === '0.0.0.0' || host.startsWith('127.') ||
      host === '[::]' || host === '[::1]' || host.includes('ffff:')) return null
    return url.origin
  } catch { return null }
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
  if (!['NoOffer', 'OfferArmed', 'OfferClosed', 'MajorityRecorded', 'ObservedMajority',
    'HistoricalRecovery', 'HistoryReviewRequired'].includes(String(source.state)))
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
  const proposalHash = source.proposalHash === null ? null : hash(source.proposalHash, 'Recovery proposal hash')
  const authorityHeadHash = source.authorityHeadHash === null ? null : hash(source.authorityHeadHash, 'Authority head hash')
  if (source.state === 'OfferArmed' && (version === null || versionHash === null ||
    candidateAddress === null || candidateDeviceId === null || proposalHash === null))
    throw new Error('Armed offer status is incomplete.')
  if ((source.state === 'MajorityRecorded' || source.state === 'ObservedMajority') !== majorityReached)
    throw new Error('Majority state is invalid.')
  return { state: source.state as RecoveryStatus['state'], votes, required, separateCopies,
    version, versionHash, candidateAddress, candidateDeviceId, majorityReached,
    proposalHash, authorityHeadHash }
}
function parseCurrentOfferCode(value: unknown, profileId: string) {
  const source = record(value, 'Current offer code')
  return { proposalHash: hash(source.proposalHash, 'Recovery proposal hash'),
    offer: parseRecoveryOffer(source.offer, profileId) }
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
  const authority = source.authority == null ? null : parseAuthorityStatus(source.authority)
  return { enabled: boolean(source.enabled, 'Sharing switch'), latest, error: textOrNull(source.error, 'Shared save error'),
    confirmedCopies: numberOrNull(source.confirmedCopies ?? 0, 'Confirmed copies') ?? 0, liveSave,
    canManageSharing: boolean(source.canManageSharing ?? true, 'Sharing management'), authority }
}
function parseAuthorityHead(value: unknown): AuthorityHead {
  const head = record(value, 'Hosting decision')
  const epoch = numberOrNull(head.epoch, 'Hosting epoch')
  if (epoch === null || epoch < 1) throw new Error('Hosting epoch is invalid.')
  return { groupId: guid(head.groupId, 'Group ID'), epoch, recordHash: hash(head.recordHash, 'Decision hash'),
    versionHash: hash(head.versionHash, 'Save hash'), hostDeviceId: guid(head.hostDeviceId, 'Host PC ID'),
    hostPublicKey: shortText(head.hostPublicKey, 'Hosting key', 500),
    hostAddress: shortText(head.hostAddress, 'Host address', 255) }
}
function parseAuthorityStatus(value: unknown): AuthorityStatus {
  const source = record(value, 'Hosting authority')
  const state = source.state
  if (state !== 'NoTakeover' && state !== 'OldHostFenced' && state !== 'ThisPcHost' &&
    state !== 'CompetingHistories' && state !== 'ReviewRequired') throw new Error('Hosting authority state is invalid.')
  const competingHeads = source.competingHeads == null ? [] : source.competingHeads
  if (!Array.isArray(competingHeads) || competingHeads.length > 128) throw new Error('Hosting histories are invalid.')
  const head = source.head == null ? null : parseAuthorityHead(source.head)
  if ((state === 'OldHostFenced' || state === 'ThisPcHost') !== (head !== null) ||
    (state === 'CompetingHistories') !== (competingHeads.length > 1))
    throw new Error('Hosting authority decision is invalid.')
  return { state, message: shortText(source.message, 'Hosting message'), head,
    competingHeads: competingHeads.map(parseAuthorityHead),
    exactManagedProcessRunning: boolean(source.exactManagedProcessRunning, 'Running game') }
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
function resolutionHash(value: unknown): string {
  if (typeof value !== 'string' || !/^[0-9A-F]{64}$/.test(value)) throw new Error('Signed decision ID is invalid.')
  return value
}
function parseResolutionChoices(value: unknown): ResolutionChoice[] {
  if (!Array.isArray(value) || value.length > 128) throw new Error('Competing copies are invalid.')
  return value.map(item => { const source = record(item, 'Competing copy')
    const version = numberOrNull(source.version, 'Copy version')
    if (version === null || version < 1) throw new Error('Copy version is invalid.')
    return { recordHash: resolutionHash(source.recordHash), version,
      availableHere: boolean(source.availableHere, 'Copy availability') } })
}
function parsePendingResolutions(value: unknown): PendingResolution[] {
  if (!Array.isArray(value) || value.length > 16) throw new Error('Pending decisions are invalid.')
  return value.map(item => { const source = record(item, 'Pending decision')
    const selectedVersion = numberOrNull(source.selectedVersion, 'Selected version')
    const competingBranches = numberOrNull(source.competingBranches, 'Competing branches')
    if (selectedVersion === null || competingBranches === null || competingBranches < 2)
      throw new Error('Pending decision is invalid.')
    return { proposalHash: resolutionHash(source.proposalHash), selectedVersion, competingBranches } })
}
function parseResolutionOfferResult(value: unknown): BasicResult & { offer: unknown } {
  const source = record(value, 'Decision offer')
  return { ...parseBasicResult(value), offer: source.offer }
}
function parseSignedOffer(value: unknown): { offer: unknown; selectedVersion: number; branches: number } {
  const offer = record(value, 'Signed invitation')
  const proposal = record(offer.proposal, 'Signed proposal')
  const version = record(offer.version, 'Selected copy')
  const selectedVersion = numberOrNull(version.number, 'Selected version')
  if (selectedVersion === null || selectedVersion < 1 || proposal.kind !== 'ResolutionQuorum' ||
      !Array.isArray(proposal.competingHeadHashes) || proposal.competingHeadHashes.length < 2 ||
      proposal.competingHeadHashes.length > 128 ||
      proposal.competingHeadHashes.some(item => typeof item !== 'string' || !/^[0-9A-F]{64}$/.test(item)))
    throw new Error('This is not a bounded signed voting invitation.')
  return { offer: value, selectedVersion, branches: proposal.competingHeadHashes.length }
}
function parseReviewedInvitation(value: unknown): BasicResult & { proposalHash: string | null } {
  const source = record(value, 'Reviewed invitation')
  return { ...parseBasicResult(value), proposalHash: source.proposalHash === null ? null : resolutionHash(source.proposalHash) }
}

export function HostSharedSaves({ profileId, devices, rollingBackupEnabled, currentAddress, onGrantChanged }:
  { profileId: string; devices: Device[]; rollingBackupEnabled: boolean; currentAddress?: string;
    onGrantChanged: () => Promise<void> }) {
  const [status, setStatus] = useState<HostStatus | null>(null)
  const [open, setOpen] = useState(false)
  const [roster, setRoster] = useState<Roster | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [handoff, setHandoff] = useState<HandoffStatus | null>(null)
  const [successorId, setSuccessorId] = useState('')
  const [successorAddress, setSuccessorAddress] = useState('')
  const [handoffMessage, setHandoffMessage] = useState('')
  const shareAddress = normalizedDirectIpHttpsEndpoint(currentAddress ?? '')
  const copyAddress = async () => {
    if (!shareAddress) return
    try {
      await navigator.clipboard.writeText(shareAddress)
      setMessage('Current Host address copied. Send it privately; Friends will check the saved Host identity before changing their address.')
    } catch { setMessage('Could not copy the Host address. Check the saved direct IP address in Host settings under Friend route.') }
  }
  const refreshHandoff = async () => setHandoff(await getLocalJson(
    `/api/local/profiles/${profileId}/shared-world/handoff`, parseHandoffStatus))
  const refreshHost = async () => {
    const [nextStatus, nextHandoff] = await Promise.all([
      getLocalJson(`/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus),
      getLocalJson(`/api/local/profiles/${profileId}/shared-world/handoff`, parseHandoffStatus)])
    setStatus(nextStatus); setHandoff(nextHandoff)
  }
  const [pendingResolutions, setPendingResolutions] = useState<PendingResolution[]>([])
  const [resolutionMessage, setResolutionMessage] = useState('')
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
  useEffect(() => {
    if (!open) return
    let active = true
    const refresh = async () => {
      try {
        const next = await getLocalJson(`/api/local/profiles/${profileId}/shared-world`, parseHostSharedWorldStatus)
        if (active) { setStatus(next); setMessage('') }
      } catch (error) {
        if (active) { setStatus(null); setMessage(errorMessage(error)) }
      }
    }
    void refresh()
    const timer = window.setInterval(() => void refresh(), 2000)
    return () => { active = false; window.clearInterval(timer) }
  }, [open, profileId])
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
  const loadPendingResolutions = async () => {
    try { setPendingResolutions(await getLocalJson(
      `/api/local/profiles/${profileId}/shared-world/resolution/offers`, parsePendingResolutions)) }
    catch (error) { setResolutionMessage(errorMessage(error)) }
  }
  const approveResolution = async (proposalHash: string) => {
    setBusy(true); setResolutionMessage('')
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/shared-world/resolution/approve/${proposalHash}`,
        'POST', value => record(value, 'Owner decision'))
      setResolutionMessage(result.code === 'OwnerOverrideRecorded' ?
        'Signed owner decision recorded. This PC is fenced from hosting the selected world.' : 'Decision pending.')
      await loadPendingResolutions()
    } catch (error) { setResolutionMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const authority = status?.authority
  const fenced = authority?.state === 'OldHostFenced'
  const review = authority?.state === 'CompetingHistories' || authority?.state === 'ReviewRequired'
  return <details className="advanced-block" onToggle={event => setOpen(event.currentTarget.open)}><summary>Shared saves</summary>
    {(fenced || review) && <section role="alert" aria-label="Hosting authority">
      <h4>{fenced ? 'Another PC now hosts this world' :
        authority?.state === 'CompetingHistories' ? 'Competing hosting histories need review' : 'Hosting decision needs review'}</h4>
      <p>{authority?.message}</p>
      <p>This PC keeps its preserved local copy. Do not start or share this world here.</p>
      {authority?.exactManagedProcessRunning && <p>The exact game process managed by this PC is still running. Stop it gracefully, then review the saved histories.</p>}
    </section>}
    {authority?.state === 'ThisPcHost' && <p role="status">This PC holds the verified current hosting decision.</p>}
    {!fenced && !review && <><p>After a graceful Stop, send hash-verified world files to approved PCs. Game load and playability have not been checked. Live save capture and automatic takeover are unavailable.</p>
      <p className="helper-text">{status?.liveSave.message ?? 'Live save sharing is unavailable. Use a hash-verified post-Stop file copy. Game load has not been checked.'}</p></>}
    <label><Input type="checkbox" checked={status?.enabled ?? false} disabled={busy || !rollingBackupEnabled || status?.canManageSharing === false || fenced || review}
      onChange={event => void changeSharing(event.target.checked)} /> Share completed saves from this server</label>
    {status?.canManageSharing === false && !fenced && !review && <p className="helper-text">This PC may host and share hash-verified post-Stop file copies after its local setup and route checks pass. Only the original owner can change sharing permissions; successor management is not available yet.</p>}
    {!rollingBackupEnabled && <p className="helper-text">Enable rolling backup after Stop in protection settings first.</p>}
    {status?.enabled && !fenced && !review && <div className="actions"><Button className="secondary" disabled={!shareAddress}
      onClick={() => void copyAddress()}>Share current address</Button></div>}
    {status?.enabled && !shareAddress && !fenced && !review && <p className="helper-text">Save a direct IP HTTPS address in Host settings under Friend route before sharing an address change.</p>}
    {status?.latest ? <p>{fenced || review ?
      `Preserved version ${status.latest.number} on this PC` :
      `Copied to ${status.confirmedCopies} PCs · latest post-Stop file copy ${status.latest.number}`} · {new Date(status.latest.createdUtc).toLocaleString()}</p> :
      <p>{fenced || review ? 'Preserved local copy on this PC. No published save can be verified here.' : 'No post-Stop save has been published yet.'}</p>}
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
      <p>Choose an approved PC. Preparing stops this server and publishes a hash-verified post-Stop file copy. Game load has not been checked. Keep this PC offline until that PC confirms the exact copy.</p>
      {handoff?.pending && <p role={handoff.code === 'HandoffReviewRequired' ? 'alert' : 'status'}>{handoff.message}
        {handoff.finalVersion != null && ` Final save version ${handoff.finalVersion}.`}
        {handoff.receiptConfirmed && ' Signed receipt confirmed.'}</p>}
      {!handoff?.pending && handoff !== null && <p className="helper-text">No pending handoff. If this PC was fenced, review its shared-world status before hosting.</p>}
      {!handoff?.pending && <><label>Next host PC <select value={successorId} disabled={busy || handoff === null}
        onChange={event => setSuccessorId(event.target.value)}><option value="">Choose a PC</option>
        {successors.map(device => <option key={device.id} value={device.id}>{device.name}</option>)}</select></label>
        {successors.length === 0 && <p className="helper-text">Give an approved PC Receive and Eligible host access, then enroll its signing identity.</p>}
        <details><summary>Direct route details</summary>
          <label>Next PC direct HTTPS IP address and port<Input value={successorAddress}
            disabled={busy || handoff === null} onChange={event => setSuccessorAddress(event.target.value)}
            placeholder="https://192.0.2.10:5131" /></label>
          <p>This address is recorded in the signed offer. Confirm the next PC's direct route with its owner.</p>
        </details>
        <div className="actions"><Button className="secondary" disabled={busy || handoff === null || !successorId || !successorAddress.trim()}
          onClick={() => void runHandoff('prepare')}>Stop and prepare file copy</Button></div></>}
      {handoff?.pending && <><p className="helper-text">After the chosen PC confirms this exact copy, complete the handoff. The app checks its signed receipt before changing who may host. These actions remain available after reopening the app.</p>
        <div className="actions"><Button className="secondary" disabled={busy || !handoff.canComplete} onClick={() => void runHandoff('complete')}>Complete pending handoff</Button>
          <Button className="text-button" disabled={busy || !handoff.canCancel} onClick={() => void runHandoff('cancel')}>Cancel pending handoff</Button></div></>}
      {handoffMessage && <p role="status">{handoffMessage}</p>}
    </section>}
    {status?.enabled && !status.canManageSharing && !fenced && !review && <p className="helper-text">Manage sharing grants are recorded for future delegated updates. Only the original owner can change signed membership today.</p>}
    {status?.error && <p role="alert">{status.error}</p>}
    <section aria-label="Resolve competing copies"><h4>Competing copies</h4>
      <p>Review signed offers before choosing a saved branch. The selected copy stays on its PC.</p>
      <Button className="secondary" disabled={busy} onClick={() => void loadPendingResolutions()}>Check owner decisions</Button>
      {pendingResolutions.map(offer => <div key={offer.proposalHash}>
        <p>{offer.competingBranches} signed branches · selected saved version {offer.selectedVersion}</p>
        <Button className="secondary" disabled={busy || !roster?.ownerOverride}
          onClick={() => void approveResolution(offer.proposalHash)}>Approve selected copy</Button>
        <details><summary>Technical details</summary><code>{offer.proposalHash}</code></details>
      </div>)}
      {resolutionMessage && <p role="status">{resolutionMessage}</p>}
    </section>
    {message && <p role="status">{message}</p>}
    <Button className="text-button" disabled={busy} onClick={() => void refreshHost().catch(error => setMessage(errorMessage(error)))}>Refresh shared save</Button>
    <details><summary>Technical details</summary><p>Copy count includes PCs that signed a confirmation for this exact version after checking every file. It was last confirmed when that PC connected; the app cannot prove its current availability. Only immutable, hash-verified post-Stop backup files are sent over the existing paired HTTPS connection. A hash check does not prove the game can load or play this world. Previous downloaded copies cannot be recalled.</p>
      {authority?.head && <p>Verified head: epoch {authority.head.epoch} · PC {authority.head.hostDeviceId} · address {authority.head.hostAddress} · decision hash {authority.head.recordHash} · save hash {authority.head.versionHash}</p>}
      {authority?.competingHeads.map(head => <p key={head.recordHash}>Competing head: epoch {head.epoch} · PC {head.hostDeviceId} · address {head.hostAddress} · decision hash {head.recordHash} · save hash {head.versionHash}</p>)}
      {status?.canManageSharing && <div className="actions"><Button className="secondary" disabled={busy || !status.enabled}
        onClick={() => void repairRoster(false)}>Retry signed permissions</Button>
        <Button className="secondary" disabled={busy}
          onClick={() => void repairRoster(true)}>Review changed world source</Button></div>}
      {status?.canManageSharing && <p className="helper-text">Review a source change only after checking the selected world and save folder. Friends will approve its new signed group on their PCs.</p>}
    </details>
  </details>
}

export function FriendSharedWorlds({ profileId, available, onAddressChange }:
  { profileId: string; available: boolean; onAddressChange?: () => void }) {
  const [status, setStatus] = useState<FriendStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [open, setOpen] = useState(false)
  const [message, setMessage] = useState('')
  const [stageMessage, setStageMessage] = useState('')
  const [staged, setStaged] = useState(false)
  const [recovery, setRecovery] = useState<RecoveryStatus | null>(null)
  const [recoveryMessage, setRecoveryMessage] = useState('')
  const [offerCode, setOfferCode] = useState('')
  const [candidateCode, setCandidateCode] = useState<{ proposalHash: string; offer: RecoveryOffer } | null>(null)
  const [reviewedOffer, setReviewedOffer] = useState<RecoveryOffer | null>(null)
  const [splitAccepted, setSplitAccepted] = useState(false)
  const [voteCount, setVoteCount] = useState<{ votes: number; required: number; majorityReached: boolean } | null>(null)
  const recoveryFlight = useRef<{ profileId: string; request: Promise<RecoveryStatus> } | null>(null)
  const recoveryIdentity = useRef<string | null>(null)
  const currentRecovery = useRef<RecoveryStatus | null>(null)
  const refreshRecovery = useCallback(async (active: () => boolean = () => true) => {
    const flight = recoveryFlight.current?.profileId === profileId ? recoveryFlight.current.request :
      getLocalJson(`/api/local/friend/${profileId}/shared-world/recovery`, parseRecoveryStatus)
    recoveryFlight.current = { profileId, request: flight }
    let next: RecoveryStatus
    try { next = await flight }
    finally { if (recoveryFlight.current?.request === flight) recoveryFlight.current = null }
    if (!active()) return
    const identity = `${next.proposalHash ?? ''}/${next.authorityHeadHash ?? ''}`
    if (recoveryIdentity.current !== null && recoveryIdentity.current !== identity) {
      setCandidateCode(null); setVoteCount(null); setReviewedOffer(null); setOfferCode('')
      setSplitAccepted(false); setRecoveryMessage('')
    }
    recoveryIdentity.current = identity
    currentRecovery.current = next
    setRecovery(next)
    if (next.state !== 'OfferArmed') setCandidateCode(null)
    if (next.state !== 'OfferArmed' && next.state !== 'MajorityRecorded' &&
      next.state !== 'ObservedMajority') setVoteCount(null)
  }, [profileId])
  const showCandidateCode = async () => {
    setRecoveryMessage(''); setCandidateCode(null)
    const identity = recoveryIdentity.current
    try {
      const result = await getLocalJson(`/api/local/friend/${profileId}/shared-world/recovery/offer-code`,
        value => parseCurrentOfferCode(value, profileId))
      if (identity === recoveryIdentity.current && currentRecovery.current?.state === 'OfferArmed' &&
        result.proposalHash === currentRecovery.current.proposalHash) setCandidateCode(result)
    }
    catch (error) { if (identity === recoveryIdentity.current) setRecoveryMessage(errorMessage(error)) }
  }
  useEffect(() => {
    if (!open) return
    let active = true
    let timer: number | undefined
    let delay = 2000
    const poll = async () => {
      try {
        await refreshRecovery(() => active)
        delay = 2000
      } catch (error) {
        if (active) {
          recoveryIdentity.current = null; currentRecovery.current = null
          setRecovery(null); setCandidateCode(null); setVoteCount(null)
          setReviewedOffer(null); setOfferCode(''); setSplitAccepted(false)
          setRecoveryMessage(errorMessage(error))
        }
        delay = Math.min(delay * 2, 30000)
      }
      if (active) timer = window.setTimeout(() => void poll(), delay)
    }
    void poll()
    return () => { active = false; window.clearTimeout(timer) }
  }, [open, refreshRecovery])
  const [choices, setChoices] = useState<ResolutionChoice[]>([])
  const [resolutionMessage, setResolutionMessage] = useState('')
  const [offerText, setOfferText] = useState('')
  const [invitation, setInvitation] = useState('')
  const [reviewed, setReviewed] = useState<(ReturnType<typeof parseSignedOffer> & { proposalHash: string }) | null>(null)
  useEffect(() => {
    if (!open && (!available || !status?.consented)) return
    const timer = window.setInterval(() => {
      void getLocalJson(`/api/local/friend/${profileId}/shared-world`, parseFriendSharedWorldStatus)
        .then(setStatus).catch(() => {})
    }, open ? 2000 : 5000)
    return () => window.clearInterval(timer)
  }, [available, open, profileId, status?.consented])
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
    const identity = recoveryIdentity.current
    setBusy(true); setRecoveryMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/recovery/offer`,
        'POST', value => parseOfferResult(value, profileId))
      if (identity === recoveryIdentity.current) setRecoveryMessage(result.message)
      if (result.ok) await refreshRecovery()
    } catch (error) { if (identity === recoveryIdentity.current) setRecoveryMessage(errorMessage(error)) }
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
    const identity = recoveryIdentity.current
    setBusy(true); setRecoveryMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/recovery/vote`,
        'POST', parseVoteResult, reviewedOffer)
      if (identity === recoveryIdentity.current) {
        setRecoveryMessage(result.message)
        if (result.ok) setVoteCount(result)
      }
      await refreshRecovery()
    } catch (error) { if (identity === recoveryIdentity.current) setRecoveryMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const separate = async () => {
    if (!splitAccepted) return
    const identity = recoveryIdentity.current
    setBusy(true); setRecoveryMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/recovery/separate`,
        'POST', parseBasicResult, { acceptSplitWarning: true })
      if (identity === recoveryIdentity.current) setRecoveryMessage(result.message)
      if (result.ok) { setSplitAccepted(false); await refreshRecovery() }
    } catch (error) { if (identity === recoveryIdentity.current) setRecoveryMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const loadChoices = async () => {
    try { setChoices(await getLocalJson(
      `/api/local/friend/${profileId}/shared-world/resolution/heads`, parseResolutionChoices)) }
    catch (error) { setResolutionMessage(errorMessage(error)) }
  }
  const offerResolution = async (choice: ResolutionChoice, owner: boolean) => {
    setBusy(true); setResolutionMessage('')
    try {
      const route = owner ? 'owner-offer' : 'offer'
      const result = await changeJson(
        `/api/local/friend/${profileId}/shared-world/resolution/${route}/${choice.recordHash}`,
        'POST', parseResolutionOfferResult)
      setResolutionMessage(result.message)
      if (result.ok && result.offer) setOfferText(JSON.stringify(result.offer))
    } catch (error) { setResolutionMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const voteResolution = async () => {
    if (!reviewed) return
    setBusy(true); setResolutionMessage('')
    try {
      const result = await changeJson(
        `/api/local/friend/${profileId}/shared-world/resolution/vote/${reviewed.proposalHash}`,
        'POST', parseBasicResult)
      setResolutionMessage(result.message)
      await loadChoices()
    } catch (error) { setResolutionMessage(errorMessage(error)) }
    finally { setBusy(false) }
  }
  const reviewInvitation = async () => {
    setBusy(true); setResolutionMessage('')
    try {
      const parsed = parseSignedOffer(JSON.parse(invitation))
      const result = await changeJson(
        `/api/local/friend/${profileId}/shared-world/resolution/invitations`,
        'POST', parseReviewedInvitation, parsed.offer)
      if (result.ok && result.proposalHash) setReviewed({ ...parsed, proposalHash: result.proposalHash })
      else setResolutionMessage(result.message)
    } catch (error) { setReviewed(null); setResolutionMessage(errorMessage(error)) }
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
  const transferAlert = status?.state === 'Low space' || status?.state === 'Stalled'
  return <details className="advanced-block" onToggle={event => setOpen(event.currentTarget.open)}><summary>Shared worlds
    {transferAlert && <span className="warning-text" role="alert"> · {headline}</span>}</summary>
    <p>Receive approved post-Stop file copies into this PC's private vault. Files are hash-verified; game load and playability have not been checked. Live save sharing and automatic takeover are unavailable.</p>
    {onAddressChange && <div className="actions"><Button className="text-button" onClick={onAddressChange}>Host address changed?</Button></div>}
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
    {status?.consented && status.thisPcVersion != null && <details aria-label="Shared world recovery">
      <summary>Recover after Host loss</summary>
      <p>A candidate can offer this PC's completed save after two minutes without the Host. Each approved PC checks the signed offer before voting. No game starts here.</p>
      <Button className="secondary" disabled={busy || !available} onClick={() => void prepareOffer()}>Prepare signed offer</Button>
      {recovery?.candidateAddress && <p>{recovery.state === 'HistoricalRecovery' || recovery.state === 'OfferClosed' ?
        'Earlier candidate PC' : 'Current candidate PC'} {recovery.candidateDeviceId} · save version {recovery.version} · {recovery.candidateAddress}</p>}
      {recovery?.required ? <p role="status">Votes {recovery.votes}/{recovery.required}. {recovery.state === 'MajorityRecorded' ?
        'This PC’s current majority decision is recorded. Game setup and route checks remain.' :
        recovery.state === 'ObservedMajority' ?
          'Another PC’s current majority decision is recorded here. Game setup and route checks remain.' :
          'Majority decision pending.'}</p> :
        recovery?.state === 'HistoryReviewRequired' ? <p role="alert">Competing authority histories need review. No recovery offer is active.</p> :
          recovery?.state === 'HistoricalRecovery' ? <p role="status">Earlier recovery history is preserved. It is no longer current.</p> :
            recovery?.state === 'OfferClosed' ? <p role="status">The earlier offer is closed. No recovery offer is active.</p> :
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
        {candidateCode?.proposalHash === recovery.proposalHash &&
          <textarea className="ui-textarea" rows={3} aria-label="Signed offer code" readOnly value={JSON.stringify(candidateCode.offer)} />}
        <p>Save hash: {recovery.versionHash}</p>
      </details>}
    </details>}
    {status?.thisPcVersion != null && <SharedWorldReadinessPanel profileId={profileId} />}
    {status?.consented && <section aria-label="Resolve competing copies"><h4>Competing copies</h4>
      <p>A decision needs the complete signed branch set and a majority of recovery voters, or an enabled owner override.</p>
      <Button className="secondary" disabled={busy} onClick={() => void loadChoices()}>Check signed branches</Button>
      {choices.map(choice => <div key={choice.recordHash}>
        <p>Saved version {choice.version} {choice.availableHere ? '· verified on this PC' : '· on another PC'}</p>
        {choice.availableHere && <div className="actions">
          <Button className="secondary" disabled={busy} onClick={() => void offerResolution(choice, false)}>Ask recovery voters</Button>
          <Button className="secondary" disabled={busy} onClick={() => void offerResolution(choice, true)}>Ask owner</Button>
        </div>}
        <details><summary>Technical details</summary><code>{choice.recordHash}</code></details>
      </div>)}
      {offerText && <details><summary>Share signed invitation</summary>
        <p>Send this invitation only to designated recovery voters. The app checks the signed proposal and exact branches before a vote.</p>
        <Button className="secondary" onClick={() => void navigator.clipboard.writeText(offerText)}>Copy invitation</Button>
        <textarea readOnly value={offerText} aria-label="Signed invitation" /></details>}
      <details><summary>Vote on an invitation</summary>
        <textarea value={invitation} aria-label="Paste signed invitation" onChange={event => {
          setInvitation(event.target.value); setReviewed(null) }} />
        <Button className="secondary" disabled={!invitation || busy}
          onClick={() => void reviewInvitation()}>Review invitation</Button>
        {reviewed && <><p>{reviewed.branches} signed branches · proposed saved version {reviewed.selectedVersion}</p>
          <Button className="secondary" disabled={busy} onClick={() => void voteResolution()}>Approve this copy</Button></>}
      </details>
      {resolutionMessage && <p role="status">{resolutionMessage}</p>}
    </section>}
    <details><summary>Technical details</summary><p>Last checked Host version: {status?.hostVersion ?? 'unknown'} · This PC: {status?.thisPcVersion ?? 'none'}.</p>
      {status?.error && <p role="alert">{status.error}</p>}
      <p>Transfers resume in bounded chunks. Each file is checked before an atomic vault receipt. This never replaces a live game save.</p></details>
  </details>
}
