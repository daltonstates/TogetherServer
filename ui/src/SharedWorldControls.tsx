import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input, TextArea } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { Icon } from './Icon'
import { SettingsDialog } from './SettingsPresentation'
import { HostSharingPermissions } from './HostSharingPermissions'
import { parseSharedHostingIdentity, parseSharedSuccessorIdentity, SharedHostingIdentityView } from './SharedHostingIdentity'
import './SharedWorld.css'
import './SharedWorldFriendPresentation.css'
import { parseBasicResult, type BasicResult, type Device } from './contracts'
import { SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'
import { SharedWorldSeparateRoutePanel } from './SharedWorldSeparateRoutePanel'
import { WorldLoadRehearsalPanel } from './WorldLoadRehearsal'
import { parseLiveSaveStatus, type LiveSaveStatus } from './LiveSaveControls'
import { SharedWorldRouteDetails } from './SharedWorldRouteDetails'
import { formatSharedBytes, normalizedDirectIpHttpsEndpoint,
  sharedSaveAge, sharedTransferPhase, sharedHostObservation, type SharedSignedHost, type SharedGame, type TransferPhase } from './sharedWorldUx'
export { normalizedDirectIpHttpsEndpoint } from './sharedWorldUx'

type SharedWorkspaceProps = { worldName?: string; showWorldHeading?: boolean; currentSignedHost?: SharedSignedHost;
  onOpenHostControls?: () => void; serverState?: string; serverStatusFresh?: boolean;
  onOpenSeparateHostControls?: (localProfileId: string) => void;
  observedHostAddress?: string | null; onOpenPlay?: () => void; connectedHostName?: string;
  onPairHost?: () => void; connectionId?: string | null; sourceBranchHash?: string | null }

function sharedHeadingIdentity(identitySource: unknown, host: boolean,
  successorIdentity?: ReturnType<typeof parseSharedSuccessorIdentity>) {
  let identity: ReturnType<typeof parseSharedHostingIdentity> | null = null
  try { if (identitySource) identity = parseSharedHostingIdentity(identitySource, host) } catch { /* Unknown is explicit. */ }
  if (!host && successorIdentity && identity?.label === 'Host decision not checked' &&
    identity.recordHash === successorIdentity.recordHash) identity = successorIdentity
  return identity
}

function SharedWorldHeading({ worldName, showWorldHeading = true, currentSignedHost, identitySource, successorIdentity, host, serverState, noticeScope,
  serverStatusFresh, observedHostAddress, connectedHostName, onOpenPlay }: SharedWorkspaceProps & { identitySource?: unknown; host: boolean;
    successorIdentity?: ReturnType<typeof parseSharedSuccessorIdentity>; noticeScope?: string }) {
  const identity = sharedHeadingIdentity(identitySource, host, successorIdentity)
  return <header className="shared-world-heading">
    <div className="shared-world-title">{showWorldHeading ? <><h3>{worldName || 'Selected world'}</h3><p>Shared saves</p></> :
      <h3>Shared saves</h3>}</div>
    <div className="shared-world-identity" aria-label="Approved Host">{currentSignedHost ? <><strong>
      Approved Host: {currentSignedHost.pcName}</strong><p>{sharedHostObservation(currentSignedHost)}</p></> :
      <SharedHostingIdentityView identity={identity} host={host} serverState={serverState} scope={noticeScope}
        serverStatusFresh={serverStatusFresh} observedHostAddress={observedHostAddress} />}
      {!host && connectedHostName && <p className="shared-connected-host">{serverStatusFresh ? 'Connected Host' : 'Saved Host'}: {connectedHostName}</p>}
      {!host && onOpenPlay && serverStatusFresh && serverState === 'Ready' && <div className="actions">
        <Button className="secondary" onClick={onOpenPlay}><Icon name="play" />Join connected Host</Button>
      </div>}
    </div>
  </header>
}

function SharedHostingSteps({ received, prepared }: { received: boolean; prepared: boolean }) {
  return <ol className="shared-hosting-steps" aria-label="Hosting steps">
    <li aria-current={!received ? 'step' : undefined}><Icon name={received ? 'check' : 'archive'} />Get save</li>
    <li aria-current={received && !prepared ? 'step' : undefined}><Icon name={prepared ? 'check' : 'settings'} />Prepare hosting</li>
    <li aria-current={received && prepared ? 'step' : undefined}><Icon name="play" />Start server</li>
  </ol>
}

type CaptureKind = 'PostStopBackup' | 'LiveSave'
type HostStatus = { enabled: boolean; latest: { number: number; versionHash: string; createdUtc: string; captureKind: CaptureKind | null } | null; error: string | null; confirmedCopies: number;
  liveSave: LiveSaveStatus; canManageSharing: boolean; authority: AuthorityStatus | null }
type LiveOrphanReview = { code: 'None' | 'Verified' | 'ReviewRequired'; versionHash: string | null; message: string }
type AuthorityHead = { groupId: string; epoch: number; recordHash: string; versionHash: string;
  hostDeviceId: string; hostPublicKey: string; hostAddress: string }
type AuthorityStatus = { state: 'NoTakeover' | 'OldHostFenced' | 'ThisPcHost' | 'CompetingHistories' | 'ReviewRequired';
  message: string; head: AuthorityHead | null; competingHeads: AuthorityHead[]; exactManagedProcessRunning: boolean }
type FriendStatus = { consented: boolean; hostVersion: number | null; thisPcVersion: number | null; state: string; error: string | null;
  capacityNotice: string | null; capacityState: 'Low space' | null;
  completedUtc: string | null; receivedUtc: string | null; transferPhase: TransferPhase | null; receiptConfirmed: boolean | null }
  & { receivedBytes: number; totalBytes: number; rosterRevision: number | null; trust: string }
type Grants = { receive: boolean; eligibleHost: boolean; recoveryVoter: boolean; manageSharing: boolean; reshare: boolean }
type RosterMember = { deviceId: string; grants: Grants; revoked: boolean; accessExpiresUtc: string | null }
type Roster = { revision: number; ownerOverride: boolean; members: RosterMember[] }
type SharingMember = RosterMember & { isSelf: boolean }
type SharingView = { available: boolean; canManage: boolean; selfDeviceId: string;
  revision: number | null; code: string; message: string; members: SharingMember[]; checkedUtc: string | null }

const grantFields: { key: keyof Grants; label: string }[] = [
  { key: 'receive', label: 'Receive' }, { key: 'eligibleHost', label: 'Can host' },
  { key: 'recoveryVoter', label: 'Recovery voter' }, { key: 'manageSharing', label: 'Manage sharing' },
  { key: 'reshare', label: 'Forward verified saves' }]
const deviceIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
type RecoveryOffer = { proposal: { profileId: string; candidateAddress: string; candidatePublicKey: string };
  version: { number: number; versionHash: string; captureKind: CaptureKind; createdUtc?: string }; candidateReceipt: { deviceId: string } }
type RecoveryStatus = { state: 'NoOffer' | 'OfferArmed' | 'OfferClosed' | 'MajorityRecorded' |
  'ObservedMajority' | 'HistoricalRecovery' | 'HistoryReviewRequired'; votes: number;
  required: number; version: number | null; versionHash: string | null; candidateAddress: string | null;
  candidateDeviceId: string | null; majorityReached: boolean; separateCopies: number }
  & { proposalHash: string | null; authorityHeadHash: string | null }
type ResolutionChoice = { recordHash: string; version: number; availableHere: boolean }
type HistoryReviewResult = BasicResult & { groupId: string | null; ownerPublicKey: string | null;
  recordCount: number; competingHeads: number }

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
function timestampOrNull(value: unknown, where: string): string | null {
  if (value == null) return null
  const result = shortText(value, where, 64)
  if (!/^\d{4}-\d{2}-\d{2}T.*(?:Z|\+00:00)$/.test(result) || !Number.isFinite(Date.parse(result)))
    throw new Error(`${where} is invalid.`)
  return result
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
function captureKind(value: unknown): CaptureKind {
  if (value !== 'PostStopBackup' && value !== 'LiveSave') throw new Error('Save capture kind is invalid.')
  return value
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
  if (number === null || number < 1) throw new Error('Save version is invalid.')
  captureKind(version.captureKind)
  guid(receipt.deviceId, 'Candidate PC')
  shortText(proposal.candidatePublicKey, 'Candidate identity', 500)
  hash(version.versionHash, 'Save hash')
  if (version.createdUtc !== undefined) timestampOrNull(version.createdUtc, 'Save capture time')
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
function parseGrants(value: unknown): Grants {
  const source = record(value, 'Sharing grants')
  return { receive: boolean(source.receive, 'Receive'),
    eligibleHost: boolean(source.eligibleHost, 'Eligible host'),
    recoveryVoter: boolean(source.recoveryVoter, 'Recovery voter'),
    manageSharing: boolean(source.manageSharing, 'Manage sharing'),
    reshare: source.reshare === undefined ? false : boolean(source.reshare, 'Forward verified saves') }
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
  const live = source.liveSave === undefined || source.liveSave === null ? null : record(source.liveSave, 'Live save status')
  const liveSave = parseLiveSaveStatus(live ?? { available: false, message: 'Sharing while running is unavailable. Stop the server safely to share a verified save. Loading it in the game still needs testing.' })
  let latest: HostStatus['latest'] = null
  if (source.latest !== null) {
    const item = record(source.latest, 'Published version')
    const number = numberOrNull(item.number, 'Version number')
    if (number === null || number < 1 || typeof item.versionHash !== 'string' || !/^[0-9A-F]{64}$/.test(item.versionHash) ||
      typeof item.createdUtc !== 'string' || !Number.isFinite(Date.parse(item.createdUtc)))
      throw new Error('Published version is invalid.')
    latest = { number, versionHash: item.versionHash, createdUtc: item.createdUtc,
      captureKind: item.captureKind == null ? null : captureKind(item.captureKind) }
  }
  const authority = source.authority == null ? null : parseAuthorityStatus(source.authority)
  return { enabled: boolean(source.enabled, 'Sharing switch'), latest, error: textOrNull(source.error, 'Shared save error'),
    confirmedCopies: numberOrNull(source.confirmedCopies ?? 0, 'Confirmed copies') ?? 0, liveSave,
    canManageSharing: boolean(source.canManageSharing ?? true, 'Sharing management'), authority }
}
export function parseLiveOrphanReview(value: unknown): LiveOrphanReview {
  const source = record(value, 'Interrupted file copy')
  if (Object.keys(source).sort().join(',') !== 'code,message,versionHash' ||
    source.code !== 'None' && source.code !== 'Verified' && source.code !== 'ReviewRequired')
    throw new Error('Interrupted file copy status is invalid.')
  const versionHash = source.versionHash === null ? null : hash(source.versionHash, 'Interrupted copy hash')
  if ((source.code === 'Verified') !== (versionHash !== null) ||
    versionHash !== null && !/^[0-9A-F]{64}$/.test(versionHash))
    throw new Error('Interrupted file copy hash is invalid.')
  return { code: source.code, versionHash, message: shortText(source.message, 'Interrupted file copy guidance') }
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
  const source = record(value, 'Hosting decision')
  const state = source.state
  if (state !== 'NoTakeover' && state !== 'OldHostFenced' && state !== 'ThisPcHost' &&
    state !== 'CompetingHistories' && state !== 'ReviewRequired') throw new Error('Hosting decision state is invalid.')
  const competingHeads = source.competingHeads == null ? [] : source.competingHeads
  if (!Array.isArray(competingHeads) || competingHeads.length > 128) throw new Error('Hosting histories are invalid.')
  const head = source.head == null ? null : parseAuthorityHead(source.head)
  if ((state === 'OldHostFenced' || state === 'ThisPcHost') !== (head !== null) ||
    (state === 'CompetingHistories') !== (competingHeads.length > 1))
    throw new Error('Hosting decision decision is invalid.')
  return { state, message: shortText(source.message, 'Hosting message'), head,
    competingHeads: competingHeads.map(parseAuthorityHead),
    exactManagedProcessRunning: boolean(source.exactManagedProcessRunning, 'Running game') }
}
export function parseFriendSharedWorldStatus(value: unknown): FriendStatus {
  const source = record(value, 'Received save status')
  if (typeof source.state !== 'string' || source.state.length > 300) throw new Error('Received save state is invalid.')
  const receivedBytes = numberOrNull(source.receivedBytes ?? 0, 'Received bytes') ?? 0
  const totalBytes = numberOrNull(source.totalBytes ?? 0, 'Total bytes') ?? 0
  if (receivedBytes > totalBytes) throw new Error('Received byte progress is invalid.')
  const transferPhase = source.transferPhase ?? null
  if (transferPhase !== null && transferPhase !== 'Receiving' && transferPhase !== 'Verifying' && transferPhase !== 'Receipt')
    throw new Error('Transfer phase is invalid.')
  return { consented: boolean(source.consented, 'This PC consent'),
    hostVersion: numberOrNull(source.hostVersion, 'Host version'),
    thisPcVersion: numberOrNull(source.thisPcVersion, 'This PC version'),
    state: source.state, error: textOrNull(source.error, 'Received save error'),
    capacityNotice: textOrNull(source.capacityNotice ?? null, 'Signed history notice'),
    capacityState: source.capacityState === 'Low space' ? source.capacityState : null,
    receivedBytes, totalBytes, transferPhase,
    completedUtc: timestampOrNull(source.completedUtc, 'Completed save time'),
    receivedUtc: timestampOrNull(source.receivedUtc, 'Received save time'),
    receiptConfirmed: source.receiptConfirmed == null ? null : boolean(source.receiptConfirmed, 'Receipt confirmation'),
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
function parseResolutionOfferResult(value: unknown): BasicResult & { offer: unknown } {
  const source = record(value, 'Decision offer')
  return { ...parseBasicResult(value), offer: source.offer }
}
function parseSignedOffer(value: unknown): { offer: unknown; selectedVersion: number; branches: number } {
  const offer = record(value, 'Voting invitation')
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
function parseHistoryReviewResult(value: unknown): HistoryReviewResult {
  const source = record(value, 'Signed history review')
  const recordCount = numberOrNull(source.recordCount, 'Decision count')
  const competingHeads = numberOrNull(source.competingHeads, 'History count')
  if (recordCount === null || competingHeads === null || competingHeads > 128)
    throw new Error('Signed history review is invalid.')
  return { ...parseBasicResult(value), recordCount, competingHeads,
    groupId: source.groupId == null ? null : guid(source.groupId, 'Signed group'),
    ownerPublicKey: source.ownerPublicKey == null ? null : shortText(source.ownerPublicKey, 'Owner identity', 256) }
}

export function HostSharedSaves({ profileId, devices, currentAddress, onGrantChanged, onOpenConnectionHelp }:
  { profileId: string; devices: Device[]; rollingBackupEnabled: boolean; currentAddress?: string;
    onGrantChanged: () => Promise<void>; onOpenProtection?: () => void; onOpenConnectionHelp?: () => void;
    renderAdvancedProof?: () => ReactNode } & SharedWorkspaceProps) {
  const base = `/api/local/profiles/${profileId}/shared-world`
  const noticeScope = `host/${profileId}`
  const [status, setStatus] = useState<HostStatus | null>(null)
  const [roster, setRoster] = useState<Roster | null>(null)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  const sequence = useRef(0)
  const mutation = useRef(false)
  const controller = useRef<AbortController | null>(null)
  const currentScope = useRef(base)
  currentScope.current = base

  const read = useCallback(async (signal: AbortSignal) => {
    const current = ++sequence.current
    const [nextStatus, nextRoster] = await Promise.allSettled([
      getLocalJson(base, parseHostSharedWorldStatus, signal),
      getLocalJson(`${base}/governance`, parseRoster, signal)
    ])
    if (signal.aborted || current !== sequence.current) return
    if (nextStatus.status === 'fulfilled') setStatus(nextStatus.value)
    else { setStatus(null); setMessage(errorMessage(nextStatus.reason)) }
    setRoster(nextRoster.status === 'fulfilled' ? nextRoster.value : null)
  }, [base])

  useEffect(() => {
    setStatus(null); setRoster(null); setBusy(false); setMessage('')
    mutation.current = false
    const abort = new AbortController()
    const sequences = sequence
    let active = true
    let timer: number | undefined
    const poll = async () => {
      if (!mutation.current) await read(abort.signal)
      if (active) timer = window.setTimeout(() => void poll(), 10000)
    }
    void poll()
    return () => {
      active = false; ++sequences.current; abort.abort(); controller.current?.abort(); window.clearTimeout(timer)
    }
  }, [read])

  const changeSharing = async (enabled: boolean) => {
    if (mutation.current || !status?.canManageSharing) return
    mutation.current = true
    ++sequence.current
    const scope = base
    const abort = new AbortController()
    controller.current = abort
    setBusy(true); setMessage('')
    const timeout = window.setTimeout(() => abort.abort(), 30000)
    try {
      const result = await changeJson(`${base}/hosting`, 'PUT', parseBasicResult, { enabled }, abort.signal)
      if (abort.signal.aborted || currentScope.current !== scope) return
      setMessage(result.message)
      if (result.ok) await onGrantChanged()
      if (!abort.signal.aborted && currentScope.current === scope) await read(abort.signal)
    } catch (error) {
      if (currentScope.current === scope) setMessage(abort.signal.aborted ? 'The change could not be checked. Refresh sharing and try again.' : errorMessage(error))
    } finally {
      window.clearTimeout(timeout)
      if (currentScope.current === scope) { mutation.current = false; setBusy(false) }
    }
  }
  const refresh = async () => {
    if (mutation.current) return
    const abort = new AbortController()
    controller.current?.abort(); controller.current = abort
    setMessage('')
    await read(abort.signal)
  }
  const authority = status?.authority
  const blocked = authority?.state === 'OldHostFenced' || authority?.state === 'CompetingHistories' || authority?.state === 'ReviewRequired'
  const eligible = devices.filter(device => device.paired && !device.revoked && !device.approvalPending &&
    !device.accessExpired && device.assignedProfileIds.includes(profileId))
  const enabled = status?.enabled === true
  return <section className="shared-world-workspace" aria-label="Shared saves">
    <section className="host-sharing-master" aria-label="Shared hosting">
      <label className="host-sharing-master-toggle"><span>Allow friends to host this server</span><Input type="checkbox"
        checked={enabled} disabled={busy || !status || !status.canManageSharing || blocked}
        onChange={event => void changeSharing(event.target.checked)} /></label>
      <p className="host-sharing-hint">Approved friends assigned to this server automatically keep the latest available save. They can choose Host locally beside Start server.</p>
      {status?.canManageSharing === false && <p className="host-sharing-hint">Change this setting on the original owner's PC.</p>}
    </section>
    {blocked && <p role="alert">This server has conflicting hosting histories. Existing saves are safe. Ask the owner to resolve the conflict before hosting here.</p>}
    {status?.error && <DismissibleNotice as="p" scope={noticeScope} role="alert">{status.error}</DismissibleNotice>}
    {message && <DismissibleNotice as="p" scope={noticeScope} role="status">{message}</DismissibleNotice>}
    <p role="status">{!status ? 'Checking shared hosting...' : !enabled ? 'Friends cannot host this server.' :
      status.latest ? `Latest save available. ${status.confirmedCopies} ${status.confirmedCopies === 1 ? 'PC has' : 'PCs have'} confirmed its copy.` :
        'Sharing is on. The latest save will be sent when a completed copy is available.'}</p>
    <details className="host-sharing-details"><summary>Sharing details</summary>
      <p>{eligible.length} approved {eligible.length === 1 ? 'PC is' : 'PCs are'} assigned to this server. Access changes apply automatically.</p>
      <HostSharingPermissions profileId={profileId} devices={eligible} members={roster?.members ?? []} enabled={enabled} />
      {status?.latest && <p>Latest completed copy: {sharedSaveAge(status.latest.createdUtc)}. Changes after that copy are not included.</p>}
      <p>The app checks each save and prepares the game automatically. Copies already received stay on those PCs.</p>
      {!normalizedDirectIpHttpsEndpoint(currentAddress ?? '') && enabled && onOpenConnectionHelp && <Button className="text-button"
        disabled={busy} onClick={onOpenConnectionHelp}><Icon name="plug" />Connection help</Button>}
      <Button className="text-button" disabled={busy} onClick={() => void refresh()}><Icon name="refresh" />Refresh sharing</Button>
    </details>
  </section>
}

export function FriendSharedWorlds({ profileId, available, onAddressChange, game, onBrowseServerFile, onOpenHostSetup, worldName, showWorldHeading, currentSignedHost, onOpenHostControls, onOpenSeparateHostControls, serverState, serverStatusFresh, observedHostAddress, onOpenPlay, connectedHostName, onPairHost, connectionId, sourceBranchHash = null }:
  { profileId: string; available: boolean; onAddressChange?: () => void; game?: SharedGame;
    onBrowseServerFile?: (game: SharedGame) => Promise<string | null>; onOpenHostSetup?: () => void } & SharedWorkspaceProps) {
  const noticeScope = JSON.stringify([connectionId, profileId, game])
  const [status, setStatus] = useState<FriendStatus | null>(null)
  const [observedAt, setObservedAt] = useState<number | null>(null)
  const [observationFailure, setObservationFailure] = useState('')
  const statusRequest = useRef(0)
  const acceptStatus = useCallback((value: FriendStatus) => {
    setStatus(value); setObservedAt(Date.now()); setObservationFailure('')
  }, [])
  const currentProfile = useRef(profileId)
  currentProfile.current = profileId
  const [showHosting, setShowHosting] = useState(false)
  const [hostingSetupScope, setHostingSetupScope] = useState<string | null>(null)
  const [successorIdentity, setSuccessorIdentity] = useState<ReturnType<typeof parseSharedSuccessorIdentity>>(null)
  const receiveSuccessorIdentity = useCallback((source: unknown) => {
    try { setSuccessorIdentity(source == null ? null : parseSharedSuccessorIdentity(source)) }
    catch { setSuccessorIdentity(null) }
  }, [])
  const [busy, setBusy] = useState(false)
  const [successorHash, setSuccessorHash] = useState('')
  const [successorPin, setSuccessorPin] = useState('')
  const [successorMessage, setSuccessorMessage] = useState('')
  const [manageOpen, setManageOpen] = useState(false)
  const [managePane, setManagePane] = useState<'copy' | 'people' | 'recovery' | 'details'>('copy')
  const open = true
  const [message, setMessage] = useState('')
  const [stageMessage, setStageMessage] = useState('')
  const [staged, setStaged] = useState(false)
  const [recovery, setRecovery] = useState<RecoveryStatus | null>(null)
  const hostingScope = JSON.stringify([profileId, game, recovery?.authorityHeadHash ?? null, status?.rosterRevision ?? null])
  const hostingSetupReady = hostingSetupScope === hostingScope
  const setHostingSetupReady = useCallback((ready: boolean) => {
    setHostingSetupScope(ready ? hostingScope : null)
  }, [hostingScope])
  const [recoveryMessage, setRecoveryMessage] = useState('')
  const [offerCode, setOfferCode] = useState('')
  const [candidateCode, setCandidateCode] = useState<{ proposalHash: string; offer: RecoveryOffer } | null>(null)
  const [reviewedOffer, setReviewedOffer] = useState<RecoveryOffer | null>(null)
  const [soloOffer, setSoloOffer] = useState<RecoveryOffer | null>(null)
  const [soloMessage, setSoloMessage] = useState('')
  const [soloRecorded, setSoloRecorded] = useState(0)
  const soloScope = JSON.stringify([noticeScope, sourceBranchHash])
  const currentSoloScope = useRef(soloScope)
  currentSoloScope.current = soloScope
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
    if (!active() || currentProfile.current !== profileId) return
    const identity = `${next.proposalHash ?? ''}/${next.authorityHeadHash ?? ''}`
    if (recoveryIdentity.current !== null && recoveryIdentity.current !== identity) {
      setCandidateCode(null); setVoteCount(null); setReviewedOffer(null); setOfferCode('')
      setRecoveryMessage('')
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
      if (currentProfile.current === profileId && identity === recoveryIdentity.current && currentRecovery.current?.state === 'OfferArmed' &&
        result.proposalHash === currentRecovery.current.proposalHash) setCandidateCode(result)
      return result
    }
    catch (error) { if (identity === recoveryIdentity.current) setRecoveryMessage(errorMessage(error)) }
  }
  const copyCandidateCode = async () => {
    const identity = recoveryIdentity.current
    const result = await showCandidateCode()
    if (!result || currentProfile.current !== profileId || identity !== recoveryIdentity.current ||
      currentRecovery.current?.state !== 'OfferArmed' || result.proposalHash !== currentRecovery.current.proposalHash) return
    try {
      await navigator.clipboard.writeText(JSON.stringify(result.offer))
      if (currentProfile.current === profileId && identity === recoveryIdentity.current)
        setRecoveryMessage('Recovery offer code copied. Share it privately with the approved recovery voters.')
    } catch {
      if (currentProfile.current === profileId && identity === recoveryIdentity.current)
        setRecoveryMessage('Could not copy the offer code. Open Technical details to select and copy it manually.')
    }
  }
  useEffect(() => {
    if (!manageOpen && !showHosting) return
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
          setReviewedOffer(null); setOfferCode('')
          setRecoveryMessage(errorMessage(error))
        }
        delay = Math.min(delay * 2, 30000)
      }
      if (active) timer = window.setTimeout(() => void poll(), delay)
    }
    void poll()
    return () => { active = false; window.clearTimeout(timer) }
  }, [manageOpen, showHosting, refreshRecovery])
  const [choices, setChoices] = useState<ResolutionChoice[]>([])
  const [resolutionMessage, setResolutionMessage] = useState('')
  const [offerText, setOfferText] = useState('')
  const [invitation, setInvitation] = useState('')
  const [reviewed, setReviewed] = useState<(ReturnType<typeof parseSignedOffer> & { proposalHash: string }) | null>(null)
  const [historyMessage, setHistoryMessage] = useState('')
  const [pendingHistoryGroup, setPendingHistoryGroup] = useState<{ groupId: string; ownerPublicKey: string } | null>(null)
  useEffect(() => { setHistoryMessage(''); setPendingHistoryGroup(null) }, [profileId])
  useEffect(() => { setSoloOffer(null); setSoloMessage(''); setSplitAccepted(false); setSoloRecorded(0); setBusy(false) }, [soloScope])
  useEffect(() => {
    setStatus(null); setObservedAt(null); setObservationFailure(''); ++statusRequest.current
    setBusy(false); setShowHosting(false); setManageOpen(false); setManagePane('copy'); setHostingSetupScope(null); setSuccessorIdentity(null)
    setSuccessorHash(''); setSuccessorPin(''); setSuccessorMessage(''); setMessage('')
    setStageMessage(''); setStaged(false); setRecovery(null); setRecoveryMessage('')
    setOfferCode(''); setCandidateCode(null); setReviewedOffer(null); setSplitAccepted(false); setVoteCount(null)
    setChoices([]); setResolutionMessage(''); setOfferText(''); setInvitation(''); setReviewed(null)
    recoveryIdentity.current = null; currentRecovery.current = null; recoveryFlight.current = null
  }, [profileId])
  useEffect(() => {
    if (sourceBranchHash) setShowHosting(true)
  }, [sourceBranchHash])
  useEffect(() => {
    let active = true
    let timer: number | undefined
    const check = async () => {
      await Promise.allSettled([
        refreshRecovery(() => active).catch(() => { if (active) setRecovery(null) }),
        getLocalJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`, parseSharedSuccessorIdentity)
          .then(value => { if (active) setSuccessorIdentity(value) }).catch(() => { if (active) setSuccessorIdentity(null) })
      ])
      if (active) timer = window.setTimeout(() => void check(), 5000)
    }
    void check()
    return () => { active = false; window.clearTimeout(timer) }
  }, [profileId, refreshRecovery])
  useEffect(() => {
    let active = true
    let timer: number | undefined
    const controller = new AbortController()
    const requests = statusRequest
    const poll = async () => {
      const sequence = ++statusRequest.current
      try {
        const value = await getLocalJson(`/api/local/friend/${profileId}/shared-world`, parseFriendSharedWorldStatus, controller.signal)
        if (active && sequence === statusRequest.current) acceptStatus(value)
      } catch (error) {
        if (active && sequence === statusRequest.current) {
          setObservedAt(null); setObservationFailure(`Save status refresh failed. ${errorMessage(error)}`)
        }
      }
      if (active) timer = window.setTimeout(() => void poll(), open ? 2000 : 5000)
    }
    void poll()
    return () => { active = false; controller.abort(); window.clearTimeout(timer); ++requests.current }
  }, [open, profileId, acceptStatus])
  useEffect(() => {
    if (observedAt === null) return
    const timer = window.setTimeout(() => {
      setObservedAt(null); setObservationFailure('Save status observation is stale. Waiting for a fresh read.')
    }, Math.max(0, observedAt + 6000 - Date.now()))
    return () => window.clearTimeout(timer)
  }, [observedAt])
  const run = async (action: 'consent' | 'pull' | 'check', enabled?: boolean) => {
    const sequence = ++statusRequest.current
    setBusy(true); setMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/${action}`, action === 'consent' ? 'PUT' : 'POST',
        parseFriendResult, action === 'consent' ? { enabled } : undefined)
      const next = result.status ?? await getLocalJson(`/api/local/friend/${profileId}/shared-world`, parseFriendSharedWorldStatus)
      if (currentProfile.current === profileId && sequence === statusRequest.current) { setMessage(result.message); acceptStatus(next) }
    } catch (error) { if (currentProfile.current === profileId && sequence === statusRequest.current) {
      setMessage(errorMessage(error)); setObservedAt(null)
      setObservationFailure(`Save status refresh failed. ${errorMessage(error)}`)
    } }
    finally { if (currentProfile.current === profileId) setBusy(false) }
  }
  const joinSuccessor = async () => {
    setBusy(true); setSuccessorMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/successor-enrollment`,
        'POST', parseBasicResult, { recordHash: successorHash.trim(), tlsFingerprint: successorPin.trim() })
      if (currentProfile.current === profileId) setSuccessorMessage(result.message)
    } catch (error) { if (currentProfile.current === profileId) setSuccessorMessage(errorMessage(error)) }
    finally { if (currentProfile.current === profileId) setBusy(false) }
  }
  const stage = async () => {
    setBusy(true); setStageMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/handoff/stage`,
        'POST', parseBasicResult)
      if (currentProfile.current === profileId) {
        setStageMessage(result.message); setStaged(result.ok && result.code === 'StagedForSetup')
      }
    } catch (error) { if (currentProfile.current === profileId) { setStageMessage(errorMessage(error)); setStaged(false) } }
    finally { if (currentProfile.current === profileId) setBusy(false) }
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
  const prepareSolo = async () => {
    setBusy(true); setSoloMessage(''); setSoloOffer(null); setSplitAccepted(false)
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/recovery/solo/prepare`,
        'POST', value => parseOfferResult(value, profileId), { sourceBranchHash })
      if (currentSoloScope.current !== soloScope) return
      setSoloMessage(result.message)
      if (result.ok && result.offer) setSoloOffer(result.offer)
    } catch (error) { if (currentSoloScope.current === soloScope) setSoloMessage(errorMessage(error)) }
    finally { if (currentSoloScope.current === soloScope) setBusy(false) }
  }
  const separate = async () => {
    if (!splitAccepted || !soloOffer) return
    setBusy(true); setSoloMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/recovery/separate`,
        'POST', parseBasicResult, { acceptSplitWarning: true })
      if (currentSoloScope.current !== soloScope) return
      setSoloMessage(result.message)
      if (result.ok) {
        setSplitAccepted(false); setSoloOffer(null); setSoloRecorded(current => current + 1)
        await refreshRecovery().catch(() => {})
      }
    } catch (error) { if (currentSoloScope.current === soloScope) setSoloMessage(errorMessage(error)) }
    finally { if (currentSoloScope.current === soloScope) setBusy(false) }
  }
  const loadChoices = async () => {
    try {
      const value = await getLocalJson(`/api/local/friend/${profileId}/shared-world/resolution/heads`, parseResolutionChoices)
      if (currentProfile.current === profileId) setChoices(value)
    }
    catch (error) { if (currentProfile.current === profileId) setResolutionMessage(errorMessage(error)) }
  }
  const reviewHistory = async (confirm = false) => {
    setBusy(true); setHistoryMessage('')
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/shared-world/history/review`,
        'POST', parseHistoryReviewResult, confirm && pendingHistoryGroup ?
          { confirmGroupId: pendingHistoryGroup.groupId,
            confirmOwnerPublicKey: pendingHistoryGroup.ownerPublicKey } : {})
      setHistoryMessage(result.message)
      if (result.code === 'GroupReviewRequired' && result.groupId && result.ownerPublicKey)
        setPendingHistoryGroup({ groupId: result.groupId, ownerPublicKey: result.ownerPublicKey })
      else setPendingHistoryGroup(null)
      if (result.ok) await loadChoices()
    } catch (error) { setPendingHistoryGroup(null); setHistoryMessage(errorMessage(error)) }
    finally { setBusy(false) }
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
  const statusProblem = status?.state === 'Error' ||
    status?.state.startsWith('Host save source changed') ||
    status?.state.startsWith('Competing save histories')
  const observationFresh = observedAt !== null
  const transferPhase = status && observationFresh ? sharedTransferPhase(status.state, status.transferPhase) : null
  const behind = observationFresh && status?.consented && !statusProblem && status.error == null && status.hostVersion != null &&
    status.thisPcVersion != null && status.hostVersion > status.thisPcVersion
  const headline = !status ? 'Checking save status' :
    !observationFresh ? 'Current save status unknown — no recent check' :
    !status.consented ? 'Save receiving is off' :
    statusProblem ? status.error ?? status.state :
    status?.state === 'Low space' ? 'Low space — receiving paused' :
    status?.state === 'Stalled' ? 'Transfer paused — retrying' :
    status.error != null ? status.error :
    transferPhase === 'Verifying' ? 'Verifying received save' :
    transferPhase === 'Receipt' ? 'Confirming receipt' :
    transferPhase === 'Receiving' ? 'Receiving completed save' :
    behind && status?.hostVersion != null && status.thisPcVersion != null ?
      `This PC is ${status.hostVersion - status.thisPcVersion === 1 ? 'one save' :
        `${status.hostVersion - status.thisPcVersion} saves`} behind` :
      hostingSetupReady && status.thisPcVersion != null && status.error == null &&
        (status.state === 'Ready' || status.state === 'Up to date when last checked') ?
        'Ready for manual Start; game load untested' :
        status.state === 'Ready' ? 'Verified copy on this PC' : status.state
  const transferAlert = !observationFresh && !!observationFailure || statusProblem || status?.error != null || status?.state === 'Low space' ||
    status?.state === 'Stalled'
  const capacityAlert = observationFresh && status?.capacityState === 'Low space' && !transferAlert ?
    'Low space — receiving paused' : null
  const hasVerifiedLocalCopy = status?.consented === true && status.thisPcVersion != null &&
    !statusProblem && status.error == null
  const verifiedCopy = hasVerifiedLocalCopy && transferPhase === null
  const identity = sharedHeadingIdentity(recovery, false, successorIdentity)
  const needsPairing = identity?.address != null && !identity.local &&
    identity.address !== normalizedDirectIpHttpsEndpoint(observedHostAddress ?? '') && identity !== successorIdentity
  const canJoin = !needsPairing && !successorIdentity && !!onOpenPlay && serverStatusFresh === true && serverState === 'Ready'
  const manualStart = observationFresh && hostingSetupReady && verifiedCopy && !!onOpenHostControls
  const canCheck = available && status?.consented === true && !busy
  const canReceive = canCheck && transferPhase === null
  const currentCopy = observationFresh && verifiedCopy && !behind
  const nextTask = manualStart ? 'start' : transferPhase ? 'transfer' : needsPairing ? 'pair' : status && !status.consented ? 'consent' :
    canReceive && observationFresh && !statusProblem && status?.error == null &&
      transferPhase === null && (status?.thisPcVersion == null || behind) ? 'receive' :
      currentCopy ? 'prepare' : 'check'
  const soloCaptureUtc = soloOffer?.version.createdUtc ?? (!sourceBranchHash ? status?.completedUtc : null)
  const soloRecovery = <section className="shared-solo-recovery" aria-label="Manual solo recovery">
    <h4>Play a recovery copy on this PC</h4>
    <p>If you have permission to host, use the newest complete verified copy here when you cannot play on the Host. You can recover alone, even if the Host app responds. No vote, waiting period or second PC is needed.</p>
    <p>Another server may still be running. Both copies keep their own progress. Your game connection stays selected. If the old Host returns, your recovery server keeps running.</p>
    {sourceBranchHash && <p>Source: the recovery history you selected in World groups. Its exact local copy is checked before preparation.</p>}
    {(soloOffer || !sourceBranchHash && status?.thisPcVersion != null) && <p className="shared-save-date">
      Save copied: {sharedSaveAge(soloCaptureUtc)}
      {soloCaptureUtc && <> · <time dateTime={soloCaptureUtc}>
        {new Date(soloCaptureUtc).toLocaleString()}</time></>}.
      Older complete copies can still be used. Later changes may be missing.</p>}
    <Button className="secondary" disabled={busy || status?.consented !== true ||
      status?.thisPcVersion == null && !sourceBranchHash || transferPhase !== null}
      onClick={() => void prepareSolo()}><Icon name="archive" />Prepare solo recovery</Button>
    {status?.consented !== true && <p>Allow saves on this PC before preparing a recovery copy.</p>}
    {soloOffer && <div className="shared-recovery-review">
      <p>Verified save {soloOffer.version.number} · {soloOffer.version.captureKind === 'LiveSave' ? 'save copied while running' : 'backup after Stop'}.</p>
      <DismissibleNotice scope={soloScope} noticeKey={soloOffer.version.versionHash} role="note">Keep a separate recovery history, restore it into a new world folder, then choose Start. This does not change the group's default.</DismissibleNotice>
      <label><Input type="checkbox" checked={splitAccepted} onChange={event => setSplitAccepted(event.target.checked)} /> I understand another server may still be running and both histories will be kept</label>
      <Button disabled={busy || !splitAccepted} onClick={() => void separate()}>Keep recovery history</Button>
    </div>}
    {soloMessage && <DismissibleNotice as="p" scope={soloScope} noticeKey={soloMessage} role="status">{soloMessage}</DismissibleNotice>}
    <SharedWorldSeparateRoutePanel key={`solo/${soloScope}`} profileId={profileId}
      separateCopies={Math.max(recovery?.separateCopies ?? 0, soloRecorded)} game={game}
      onBrowseServerFile={onBrowseServerFile} onOpenHostControls={onOpenHostControls}
      onOpenSeparateHostControls={onOpenSeparateHostControls} expanded={soloRecorded > 0} />
  </section>
  return <section className="shared-world-workspace" aria-label="Shared worlds">
    <SharedWorldHeading worldName={worldName} showWorldHeading={showWorldHeading} currentSignedHost={currentSignedHost} identitySource={recovery}
      successorIdentity={successorIdentity} host={false} serverState={serverState} serverStatusFresh={serverStatusFresh} observedHostAddress={observedHostAddress} noticeScope={noticeScope}
      connectedHostName={connectedHostName} onOpenPlay={canJoin ? onOpenPlay : undefined} />
    {transferAlert ? <DismissibleNotice as="p" scope={noticeScope} className="warning-text" role="alert">{headline}</DismissibleNotice> :
      !transferPhase && <p role="status">{headline}</p>}
    {observationFailure && <DismissibleNotice as="p" scope={noticeScope} role="alert">{observationFailure} Your last verified local copy and consent are retained; the current Host version and transfer progress are unavailable.</DismissibleNotice>}
    {capacityAlert && <DismissibleNotice as="p" scope={noticeScope} className="warning-text" role="alert">{capacityAlert}</DismissibleNotice>}
    {status?.thisPcVersion != null && <p>Verified save {status.thisPcVersion} is on this PC. You still need hosting permission and setup checks.</p>}
    {currentSignedHost && recovery?.state === 'HistoryReviewRequired' && <DismissibleNotice as="p" scope={noticeScope} role="alert">Hosting decisions conflict. Review them with the group. You can still recover a separate copy.</DismissibleNotice>}
    {recovery?.state === 'HistoricalRecovery' && <p role="status">This earlier recovery is kept as history. It is not the current hosting decision.</p>}
    <p className="helper-text">Changes since the Host's last completed copy may be missing. Hosting never takes over automatically.</p>
    <section className="shared-next-action" aria-label="Next shared-save action">
      <h4 aria-live="polite" aria-atomic="true">{nextTask === 'start' ? 'Setup complete; choose Start when ready' :
        nextTask === 'transfer' ? transferPhase === 'Receiving' ? 'Receiving the save copy' :
          transferPhase === 'Verifying' ? 'Verifying the save copy' : 'Confirming receipt' :
        nextTask === 'pair' ? 'Connect to the new approved Host' :
        nextTask === 'receive' ? 'Get the save before preparing hosting' : nextTask === 'prepare' ? 'Prepare hosting on this PC' :
        nextTask === 'consent' ? 'Allow receiving before preparing hosting' : 'Check the latest save status'}</h4>
      <p>{nextTask === 'start' ? 'Review Start in Host. The server must be confirmed stopped, and the hosting decision must still allow this PC.' :
        nextTask === 'transfer' ? 'Keep the app open. Wait for file checks and receipt confirmation before getting another copy or preparing this save.' :
        nextTask === 'pair' ? 'Use the new Host’s invite to pair before joining. Your saved connection stays preserved.' :
        nextTask === 'receive' ? 'Receive the latest completed copy with your consent, then review hosting requirements.' :
        nextTask === 'prepare' ? 'Review the hosting move, setup and connection checks. You choose when to Start.' :
        nextTask === 'consent' ? 'Turn on Allow saves on this PC to receive a copy. Host this save opens the guided steps.' :
        'Current save and hosting checks are incomplete. Review the status before preparing hosting.'}</p>
      <div className="actions">
        {manualStart && <Button onClick={onOpenHostControls}><Icon name="play" />Review Start server</Button>}
        {needsPairing && (onPairHost ? <Button className={nextTask === 'pair' ? undefined : 'secondary'}
          onClick={onPairHost}><Icon name="link" />Pair with the new Host</Button> :
          <p>Pair with the new Host using its invite before joining.</p>)}
        {nextTask === 'receive' && <Button disabled={!canReceive}
          onClick={() => void run('pull')}><Icon name="archive" />Get latest save</Button>}
        {nextTask === 'check' && <Button className={canCheck ? undefined : 'secondary'} disabled={!canCheck}
          onClick={() => void run('check')}><Icon name="refresh" />Check latest</Button>}
        <Button className={nextTask === 'prepare' || nextTask === 'consent' ? undefined : 'secondary'}
          onClick={() => setShowHosting(true)}><Icon name="server" />Host this save</Button>
        <Button className="secondary" onClick={() => setManageOpen(true)}><Icon name="settings" />Manage sharing</Button>
      </div>
    </section>
    <section className="shared-receive-save" aria-label="Receive save copies"><h4>Receive a private copy</h4>
    <p>Keep this app open to receive completed saves. Receiving does not make this PC a Host.</p>
    {onAddressChange && <div className="actions"><Button className="text-button" onClick={onAddressChange}>Host address changed?</Button></div>}
    {!available && <p>{status?.thisPcVersion != null ?
      'Save sharing could not be checked on this Host. You can still recover your verified copy below.' :
      'Connect to a Host with shared-save support before receiving saves.'}</p>}
    <label><Input type="checkbox" checked={status?.consented ?? false} disabled={!available || !status || (busy && !status.consented)}
      onChange={event => void run('consent', event.target.checked)} /> Allow saves on this PC</label>
    {status && !status.consented && <DismissibleNotice as="p" scope={noticeScope} role="note">Turn on Allow saves on this PC before choosing Get latest save.</DismissibleNotice>}
    {transferPhase && <p role="note">Get latest save is unavailable while this copy is being received, verified or acknowledged.</p>}
    <div className="actions">{nextTask !== 'check' && <Button className="secondary" disabled={!canCheck}
      onClick={() => void run('check')}>Check latest</Button>}
      {nextTask !== 'receive' && <Button className="secondary" disabled={!canReceive}
        onClick={() => void run('pull')}><Icon name="archive" />{busy ? 'Working…' : 'Get latest save'}</Button>}</div>
    {status && transferPhase && <section aria-label="Save transfer progress" aria-live="polite">
      <p>{formatSharedBytes(status.receivedBytes)} of {formatSharedBytes(status.totalBytes)}</p>
      {status.totalBytes > 0 && <progress max={status.totalBytes} value={status.receivedBytes}
        aria-label="Save bytes received" aria-valuetext={`${formatSharedBytes(status.receivedBytes)} of ${formatSharedBytes(status.totalBytes)}`} />}
      <ol aria-label="Transfer phases">
        <li aria-current={transferPhase === 'Receiving' ? 'step' : undefined}>Receive the copy</li>
        <li aria-current={transferPhase === 'Verifying' ? 'step' : undefined}>Check files and permission</li>
        <li aria-current={transferPhase === 'Receipt' ? 'step' : undefined}>Confirm receipt</li>
      </ol>
      <p className="helper-text">All bytes may arrive before file checks and receipt confirmation finish. Keep the app open. Interrupted transfers resume.</p>
    </section>}
    {observationFresh && status?.capacityNotice && <DismissibleNotice as="p" scope={noticeScope} role="alert">{status.capacityNotice}</DismissibleNotice>}
    {message && <DismissibleNotice as="p" scope={noticeScope} role="status">{message}</DismissibleNotice>}
    </section>
    <p className="helper-text">File checks do not prove the game can load or play this save.</p>
    <p className="helper-text">Recovery keeps a separate save history. Moving hosting and choosing the group's default have separate checks.</p>
    {showHosting && <SettingsDialog title="Host this save" description="Get the save, finish setup, then choose Start server. The app rechecks permission at each step."
      busy={busy} onClose={() => setShowHosting(false)} wide>
      <div aria-label="Host on this PC">
      {soloRecovery}
      <details className="advanced-block"><summary>Planned handoff and older recovery flow</summary>
      <SharedHostingSteps received={observationFresh && verifiedCopy && !behind} prepared={hostingSetupReady && verifiedCopy} />
      <section aria-label="Get save for hosting" tabIndex={-1}><h4>1. This PC: consent and verify the save</h4>
        <p>{status?.consented && status.thisPcVersion != null ? `Verified copy ${status.thisPcVersion} is on this PC.` : 'Allow saves on this PC and get the latest completed copy from the current Host.'}</p>
        <p>For a planned handoff, the current Host must stop, share the final save, confirm this PC received it and finish the handoff. Recovery above creates a separate history instead.</p>
        <label><Input type="checkbox" checked={status?.consented ?? false}
          disabled={!available || busy} onChange={event => void run('consent', event.target.checked)} />Allow this PC to receive the hosting save</label>
        <Button className={canReceive ? undefined : 'secondary'} disabled={!canReceive} onClick={() => void run('pull')}>
          <Icon name="archive" />Get hosting save</Button>
      </section>
      <h4>2. This PC: prepare hosting</h4>
    {status?.consented && status.thisPcVersion != null && <section aria-label="Planned handoff offer">
      <h4>Planned hosting handoff</h4>
      <p>If the current Host offers a handoff to this PC, check and prepare its final save here. Loading it in the game still needs testing.</p>
      <Button className="secondary" disabled={busy || !available || !verifiedCopy} onClick={() => void stage()}>Check handoff and prepare save</Button>
      {stageMessage && <DismissibleNotice as="p" scope={noticeScope} role="status">{stageMessage}</DismissibleNotice>}
      {staged && <p>Verified save prepared on this PC. Server setup, sharing identity and connections still need checking.</p>}
    </section>}
    {hasVerifiedLocalCopy && <SharedWorldReadinessPanel key={`readiness/${noticeScope}`} profileId={profileId}
      authorityHeadHash={recovery?.authorityHeadHash ?? null} authorityRevision={status?.rosterRevision ?? null}
      onHostingSetupChange={setHostingSetupReady} game={game} onBrowseServerFile={onBrowseServerFile}
      onOpenHostSetup={onOpenHostSetup} onOpenHostControls={onOpenHostControls}
      onSignedSuccessorChange={receiveSuccessorIdentity} expanded includeRehearsal={false} />}
      <h4>3. This PC: choose Start server in Host</h4>
      <p>Restore and connection checks do not start a server. Choose Start after the server is confirmed stopped and the hosting decision allows this PC. Ready appears only after a new game check.</p>
      {(!status?.consented || status.thisPcVersion == null) && <p>Get a verified copy first. Preparation and Start remain unavailable.</p>}
      <Button className="secondary" onClick={() => { setShowHosting(false); setManagePane('details'); setManageOpen(true) }}>
        <Icon name="shield" />Sharing checks and history</Button>
      </details>
      </div>
    </SettingsDialog>}
    {manageOpen && <SettingsDialog title="Manage sharing" description="Copies, approved PCs and recovery for this world."
      busy={busy} onClose={() => setManageOpen(false)} wide footer={<Button className="secondary" disabled={busy} onClick={() => setManageOpen(false)}>Done</Button>}>
      <div className="friend-sharing-manager">
      <div className="friend-sharing-status" data-warning={transferAlert || capacityAlert ? 'true' : undefined} aria-live="polite">
        <Icon name={transferAlert || capacityAlert ? 'warning' : transferPhase ? 'refresh' : 'archive'} size={20} />
        <div><strong>{!observationFresh ? 'Current save status unknown' : status?.thisPcVersion != null ? `Verified local save ${status.thisPcVersion}` :
          status?.consented ? 'Waiting for a completed copy' : 'Save receiving off'}</strong>
          <span>{transferPhase ? `${transferPhase === 'Receiving' ? 'Receiving bytes' : transferPhase === 'Verifying' ? 'Verifying files and access' : 'Confirming receipt'} · ${formatSharedBytes(status?.receivedBytes ?? 0)} of ${formatSharedBytes(status?.totalBytes ?? 0)}` :
            transferAlert ? headline : !available ? 'Host support not checked. Verified copies on this PC are kept.' : 'Receiving, hosting and choosing a default are separate decisions.'}</span>
          {transferPhase && status && status.totalBytes > 0 && <progress max={status.totalBytes} value={status.receivedBytes} aria-label="Management save bytes received" />}
        </div>
      </div>
      {observationFailure && <DismissibleNotice as="p" scope={noticeScope} className="warning-text" role="alert">Current Host progress is unavailable. Your verified copy and consent are retained.</DismissibleNotice>}
      {observationFresh && status?.capacityNotice && <DismissibleNotice as="p" scope={noticeScope} role="alert">{status.capacityNotice}</DismissibleNotice>}
      {recovery?.state === 'HistoryReviewRequired' && <DismissibleNotice as="p" scope={noticeScope} role="alert">Conflicting hosting decisions need review. You can still recover a separate copy.</DismissibleNotice>}
      <nav className="friend-sharing-navigation" aria-label="Shared-save management sections">
        <Button className="secondary" aria-current={managePane === 'copy' ? 'page' : undefined} onClick={() => setManagePane('copy')}><Icon name="archive" />Copy status</Button>
        <Button className="secondary" aria-current={managePane === 'people' ? 'page' : undefined} onClick={() => setManagePane('people')}><Icon name="users" />People</Button>
        <Button className="secondary" aria-current={managePane === 'recovery' ? 'page' : undefined} onClick={() => setManagePane('recovery')}><Icon name="shield" />Recovery</Button>
        <Button className="secondary" aria-current={managePane === 'details' ? 'page' : undefined} onClick={() => setManagePane('details')}><Icon name="logs" />Details</Button>
      </nav>
      <section className="friend-sharing-pane" aria-label="Copy status" hidden={managePane !== 'copy'}>
      <div className="friend-sharing-pane-heading"><h3><Icon name="archive" />Copy status</h3><p>Dates and receipt confirmations apply to this completed save.</p></div>
      <section className="friend-sharing-card" aria-label="Copy dates and receipt confirmation"><h4>Copy dates and receipt confirmation</h4>
    {status?.thisPcVersion != null && <dl aria-label="Shared save age">
      <dt>Completed by the Host</dt><dd>{sharedSaveAge(status.completedUtc)}
        {status.completedUtc && <> · <time dateTime={status.completedUtc}>{new Date(status.completedUtc).toLocaleString()}</time></>}</dd>
      <dt>Verified on this PC</dt><dd>{sharedSaveAge(status.receivedUtc)}
        {status.receivedUtc && <> · <time dateTime={status.receivedUtc}>{new Date(status.receivedUtc).toLocaleString()}</time></>}</dd>
      <dt>Host confirmed receipt</dt><dd>{status.receiptConfirmed === true ? 'Confirmed when last checked' :
        status.receiptConfirmed === false ? 'Not confirmed yet' : 'Unavailable'}</dd>
    </dl>}
    {status?.thisPcVersion == null && <p>No verified local copy yet. Allow saves on this PC and get a completed copy before preparing recovery.</p>}
    <p className="friend-sharing-hint">Changes after the completed copy may be missing. Hash checks do not prove game load or a saved restart.</p>
    {status && <details><summary>Technical details</summary><p>Trust: {status.trust}.
      Roster revision {status.rosterRevision ?? 'not checked'}. This confirms the copy, not permission or setup to host.</p></details>}
      </section>
      </section>
      <div className="friend-sharing-pane" hidden={managePane !== 'details'}>
      <div className="friend-sharing-pane-heading"><h3><Icon name="logs" />Details</h3><p>Review hosting decisions and checks on this PC.</p></div>
    {available && <section aria-label="Verified hosting history"><h4>Verified hosting history</h4>
      <p>If this PC can vote, review the group and its hosting decisions here. Receiving saves is a separate choice.</p>
      <Button className="secondary" disabled={busy} onClick={() => void reviewHistory()}>Review hosting history</Button>
      {pendingHistoryGroup && <div><DismissibleNotice as="p" scope={noticeScope} noticeKey={JSON.stringify([pendingHistoryGroup.groupId, pendingHistoryGroup.ownerPublicKey])} role="alert">Confirm this group and owner identity before trusting its hosting history.</DismissibleNotice>
        <p>Group {pendingHistoryGroup.groupId} · owner identity {pendingHistoryGroup.ownerPublicKey.slice(0, 16)}…</p>
        <Button className="secondary" disabled={busy} onClick={() => void reviewHistory(true)}>Confirm group and owner</Button>
        <details><summary>Technical details</summary><p>Full owner signing key: <code>{pendingHistoryGroup.ownerPublicKey}</code></p></details>
      </div>}
      {historyMessage && <DismissibleNotice as="p" scope={noticeScope} role="status">{historyMessage}</DismissibleNotice>}
    </section>}
      </div>
      <div className="friend-sharing-pane" hidden={managePane !== 'recovery'}>
      <div className="friend-sharing-pane-heading"><h3><Icon name="shield" />Recovery</h3><p>Prepare a separate continuation or review the group's default.</p></div>
      {soloRecovery}
    {status?.consented && <details><summary>Join a new Host after takeover</summary>
      <p>Paste the connection details from the new Host. The app checks the hosting decision and Host identity. Your old connection and save history are kept.</p>
      <SharedWorldRouteDetails key={profileId} profileId={profileId} onReviewed={details => {
        setSuccessorHash(details?.recordHash ?? ''); setSuccessorPin(details?.tlsFingerprint ?? '')
        setSuccessorMessage('')
      }} />
      <label>Hosting decision ID<Input value={successorHash} onChange={event => setSuccessorHash(event.target.value)} /></label>
      <label>New Host certificate fingerprint<Input value={successorPin} onChange={event => setSuccessorPin(event.target.value)} /></label>
      <Button className="secondary" disabled={busy || !/^[0-9A-Fa-f]{64}$/.test(successorHash.trim()) ||
        !/^[0-9A-Fa-f]{64}$/.test(successorPin.trim())} onClick={() => void joinSuccessor()}>
        Check and join new Host</Button>
      {successorMessage && <DismissibleNotice as="p" scope={noticeScope} role="status">{successorMessage}</DismissibleNotice>}
    </details>}
    {status?.consented && status.thisPcVersion != null && <details aria-label="Shared world recovery">
      <summary>Older recovery flow: ask voters to move hosting</summary>
      <p>This older flow needs at least two minutes of failed Host checks and approval from recovery voters. A stopped game alone is not enough. To recover alone without waiting, use Play a recovery copy on this PC above.</p>
      <ol>
        <li>In Host connection settings, set this PC's HTTPS address and TogetherServer port.</li>
        <li>Prepare an offer and share its code with the approved recovery voters.</li>
        <li>After a majority agrees, open Check this PC for hosting below to restore the copy and finish setup.</li>
      </ol>
      <p className="helper-text">A network outage can leave the original Host running. Confirm with the group before continuing. Preparing or voting does not start a game server.</p>
      <Button className="secondary" disabled={busy} onClick={() => void prepareOffer()}>Prepare recovery offer</Button>
      {recovery?.state === 'OfferArmed' && <div className="actions"><Button className="secondary" disabled={busy}
        onClick={() => void copyCandidateCode()}>Copy recovery offer code</Button></div>}
      {recovery?.candidateAddress && <p>{recovery.state === 'HistoricalRecovery' || recovery.state === 'OfferClosed' ?
        'Earlier candidate PC' : 'Current candidate PC'} {recovery.candidateDeviceId} · save version {recovery.version} · {recovery.candidateAddress}</p>}
      {recovery?.required ? <p role="status">Votes {recovery.votes}/{recovery.required}. {recovery.state === 'MajorityRecorded' ?
        'This PC’s current majority decision is recorded. Game setup and route checks remain.' :
        recovery.state === 'ObservedMajority' ?
          'Another PC’s current majority decision is recorded here. Game setup and route checks remain.' :
          'Majority decision pending.'}</p> :
        recovery?.state === 'HistoryReviewRequired' ? null :
          recovery?.state === 'HistoricalRecovery' ? <p role="status">Earlier recovery history is preserved. It is no longer current.</p> :
            recovery?.state === 'OfferClosed' ? <p role="status">The earlier offer is closed. No recovery offer is active.</p> :
              <p className="helper-text">No recovery offer is active on this PC.</p>}
      {recovery?.separateCopies ? <p role="status">Separate history recorded on this PC. It has not started a game server.</p> : null}
      <label>Offer code from candidate PC<TextArea rows={3} value={offerCode} maxLength={512 * 1024}
        onChange={event => { setOfferCode(event.target.value); setReviewedOffer(null); setVoteCount(null) }} /></label>
      <Button className="secondary" disabled={busy || !offerCode.trim()} onClick={reviewOffer}>Review offer code</Button>
      {reviewedOffer && <div><p>Candidate PC {reviewedOffer.candidateReceipt.deviceId} · save version {reviewedOffer.version.number} ({reviewedOffer.version.captureKind === 'LiveSave' ? 'save copied while running' : 'backup after Stop'}) · {reviewedOffer.proposal.candidateAddress}</p>
        <DismissibleNotice as="p" scope={noticeScope} noticeKey={JSON.stringify([reviewedOffer.candidateReceipt.deviceId, reviewedOffer.version.versionHash])} role="alert">Another Host may still be running. Voting for a different save history could split this world. Check the candidate PC, address, and save version with the group.</DismissibleNotice>
        <Button className="secondary" disabled={busy} onClick={() => void vote()}>Check and vote for this offer</Button></div>}
      {voteCount && !recovery?.required && <p role="status">Votes {voteCount.votes}/{voteCount.required}. {voteCount.majorityReached ?
        'Majority decision recorded.' : 'Majority decision pending.'}</p>}
      {recoveryMessage && <DismissibleNotice as="p" scope={noticeScope} role="status">{recoveryMessage}</DismissibleNotice>}
      <Button className="text-button" disabled={busy} onClick={() => void refreshRecovery().catch(error => setRecoveryMessage(errorMessage(error)))}>Refresh recovery</Button>
      {recovery?.state === 'OfferArmed' && <details><summary>Technical details</summary>
        <p>Share this recovery offer code with approved voters. It names the proposed Host and completed save. You must still choose Start separately.</p>
        <Button className="secondary" disabled={busy} onClick={() => void showCandidateCode()}>Show recovery offer code</Button>
        {candidateCode?.proposalHash === recovery.proposalHash &&
          <TextArea rows={3} aria-label="Recovery offer code" readOnly value={JSON.stringify(candidateCode.offer)} />}
        <p>Save hash: {recovery.versionHash}</p>
      </details>}
    </details>}
      </div>
      <section className="friend-sharing-pane" aria-label="Sharing people" hidden={managePane !== 'people'}>
      <div className="friend-sharing-pane-heading"><h3><Icon name="users" />People</h3><p>Review which approved PCs may receive, host or vote.</p></div>
      <FriendSharingManager key={noticeScope} profileId={profileId} available={available} noticeScope={noticeScope} />
      </section>
      <div className="friend-sharing-pane" hidden={managePane !== 'details'}>
    {status?.consented && status.thisPcVersion != null && <WorldLoadRehearsalPanel profileId={profileId} received />}
    {hasVerifiedLocalCopy && <details><summary>Hosting checks and file restore test</summary>
      <SharedWorldReadinessPanel key={`sharing-checks/${noticeScope}`} profileId={profileId}
        authorityHeadHash={recovery?.authorityHeadHash ?? null} authorityRevision={status?.rosterRevision ?? null}
        game={game} onBrowseServerFile={onBrowseServerFile} onOpenHostSetup={onOpenHostSetup} expanded />
    </details>}
      </div>
      <div className="friend-sharing-pane" hidden={managePane !== 'recovery'}>
    {available && <section aria-label="Resolve competing copies"><h4>Competing copies</h4>
      <p>This older recovery flow checks every save history and needs a majority of recovery voters or the owner's enabled final say. Both histories are kept. A higher save number does not choose one.</p>
      <Button className="secondary" disabled={busy} onClick={() => void loadChoices()}>Check verified save histories</Button>
      {choices.length > 0 && <div style={{ overflowX: 'auto' }}><table aria-label="Compare competing save histories">
        <caption>Compare every verified save history. No copy is selected automatically.</caption>
        <thead><tr><th scope="col">Save</th><th scope="col">Signed history</th><th scope="col">Propose this copy</th></tr></thead>
        <tbody>{choices.map(choice => <tr key={choice.recordHash}>
        <td><p>Saved version {choice.version} {choice.availableHere ? '· verified on this PC' : '· on another PC'}</p></td>
        <td><code>{choice.recordHash.slice(0, 12)}…</code>
          <details><summary>Full signed decision</summary><code style={{ overflowWrap: 'anywhere' }}>{choice.recordHash}</code></details></td>
        <td>{choice.availableHere ? <div className="actions">
          <Button className="secondary" disabled={busy} onClick={() => void offerResolution(choice, false)}>Ask recovery voters</Button>
          <Button className="secondary" disabled={busy} onClick={() => void offerResolution(choice, true)}>Ask owner</Button>
        </div> : <span>Review on the PC holding this copy.</span>}</td>
      </tr>)}</tbody></table></div>}
      {offerText && <section aria-label="Recovery vote invitation"><h4>Share voting invitation</h4>
        <p>Send this invitation only to approved recovery voters. The app checks the proposal and each save history before accepting a vote.</p>
        <Button className="secondary" onClick={() => void navigator.clipboard.writeText(offerText)
          .then(() => setResolutionMessage('Voting invitation copied. Share it privately with approved voters.'))
          .catch(() => setResolutionMessage('Could not copy the invitation. Select the code below to copy it.'))}>Copy invitation</Button>
        <details><summary>Voting invitation code</summary><TextArea readOnly value={offerText} aria-label="Voting invitation" /></details></section>}
      <details><summary>Vote on an invitation</summary>
        <TextArea value={invitation} aria-label="Paste voting invitation" onChange={event => {
          setInvitation(event.target.value); setReviewed(null) }} />
        <Button className="secondary" disabled={!invitation || busy}
          onClick={() => void reviewInvitation()}>Review invitation</Button>
        {reviewed && <><p>{reviewed.branches} verified save histories · proposed saved version {reviewed.selectedVersion}</p>
          <Button className="secondary" disabled={busy} onClick={() => void voteResolution()}>Approve this copy</Button></>}
      </details>
      {resolutionMessage && <DismissibleNotice as="p" scope={noticeScope} role="status">{resolutionMessage}</DismissibleNotice>}
    </section>}
      </div>
      <div className="friend-sharing-pane" hidden={managePane !== 'details'}>
    <details><summary>Diagnostics</summary><p>Last checked Host version: {observationFresh ? status?.hostVersion ?? 'unknown' : 'unknown (no recent check)'} · This PC: {status?.thisPcVersion ?? 'none'}.</p>
      {status?.error && <DismissibleNotice as="p" scope={noticeScope} role="alert">{status.error}</DismissibleNotice>}
      <p>Save copies are stored privately on this PC after checking the files and permission. Copies come from a stopped server or a running server with a passed installation test. Loading and playing the save need separate game tests.</p>
      <p>Interrupted transfers resume. Every file is checked before receipt is confirmed. A transfer never replaces a live game save.</p></details>
      </div>
      </div>
    </SettingsDialog>}
  </section>
}

function FriendSharingManager({ profileId, available, noticeScope }: { profileId: string; available: boolean; noticeScope: string }) {
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
        setMessage('The owner must restore this PC\'s access before its sharing permissions can change.')
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
  }}><summary>Member permissions</summary>
    {!available ? <p>Update the Host app to manage sharing.</p> :
      <p>Only sharing managers can change another member's save access. These permissions are separate from Start, Stop and logs.</p>}
    {busy && <p role="status">Checking sharing permissions…</p>}
    {view && !view.canManage && <DismissibleNotice as="p" scope={noticeScope} noticeKey={JSON.stringify([view.code, view.message])} role="alert">{view.message}</DismissibleNotice>}
    {view?.canManage && <>
      <p>Sharing permissions verified for this PC. Check the PC code with the owner before changing another member.</p>
      {view.members.filter(member => !member.isSelf && !member.revoked).sort((a, b) =>
        Number(a.accessExpiresUtc !== null && Date.parse(a.accessExpiresUtc) <= Date.now()) -
        Number(b.accessExpiresUtc !== null && Date.parse(b.accessExpiresUtc) <= Date.now())).map(member => {
        const label = `PC ending ${member.deviceId.slice(-6).toUpperCase()}`
        const expired = member.accessExpiresUtc !== null && Date.parse(member.accessExpiresUtc) <= Date.now()
        return <div key={member.deviceId}><p><strong>{label}</strong> · {member.revoked ? 'Removed from sharing' :
          expired ? 'Access ended' : 'Sharing member'}</p>
          {expired && <p className="helper-text">The owner must extend this PC's access before it can use sharing.</p>}
          {grantFields.filter(field => field.key !== 'manageSharing' && field.key !== 'reshare').map(field => <label key={field.key}>
            <Input type="checkbox" checked={member.grants[field.key]} disabled={busy || member.revoked || expired}
              onChange={event => void change(member.deviceId,
                field.key as 'receive' | 'eligibleHost' | 'recoveryVoter', event.target.checked)} />
            {field.label} for {label}</label>)}
          {member.revoked && <p className="helper-text">Ask the owner to restore this PC's access.</p>}
          {member.grants.manageSharing && <p className="helper-text">Only the owner can change Manage sharing for this PC.</p>}
        </div>
      })}
      {view.members.filter(member => !member.isSelf && !member.revoked).length === 0 &&
        <p>No other approved PCs are in this world yet.</p>}
      <p className="helper-text">Only the owner can allow Manage sharing or change the owner's final say on conflicts.</p>
    </>}
    {message && <DismissibleNotice as="p" scope={noticeScope} role="status">{message}</DismissibleNotice>}
    <Button className="text-button" disabled={!available || busy} onClick={() => void open()}>Refresh sharing list</Button>
    <details><summary>Technical details</summary><p>Verified roster revision: {view?.revision ?? 'not checked'}.
      The verified list identifies each PC. The Host rechecks changed or expired permissions.</p>
      {view?.members.filter(member => !member.revoked).map(member => <p key={member.deviceId}>{member.deviceId} · {member.isSelf ? 'this PC' : 'another PC'}</p>)}
    </details>
  </details>
}
