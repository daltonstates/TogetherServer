import { useEffect, useState } from 'react'
import { getLocalJson } from './api'
import { DismissibleNotice } from './DismissibleNotice'
import { normalizedDirectIpHttpsEndpoint } from './sharedWorldUx'

type HostingIdentity = { label: string; address: string | null; local: boolean; warning: string | null; hidden?: boolean; recordHash?: string }

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Hosting identity is unavailable.')
  return value as Record<string, unknown>
}
function validHash(value: unknown): value is string { return typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value) }
function validDevice(value: unknown): value is string { return typeof value === 'string' && /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(value) }

function hasSharedSaveEvidence(value: unknown): boolean {
  const source = record(value)
  for (const key of ['hostVersion', 'thisPcVersion', 'rosterRevision']) {
    if (source[key] != null && (typeof source[key] !== 'number' || !Number.isSafeInteger(source[key]) ||
      (source[key] as number) < 0)) throw new Error('Shared save evidence is invalid.')
  }
  if (typeof source.consented !== 'boolean' || typeof source.state !== 'string' ||
    source.error != null && typeof source.error !== 'string') throw new Error('Shared save evidence is invalid.')
  // Consent and app capability alone do not establish a shared world. A copy,
  // published version, verified roster or signed-history problem makes it relevant.
  return source.hostVersion != null || source.thisPcVersion != null || source.rosterRevision != null ||
    source.error != null || source.state.startsWith('Host save source changed') ||
    source.state.startsWith('Competing save histories')
}

export function parseSharedHostingIdentity(value: unknown, host: boolean): HostingIdentity {
  const source = record(value)
  if (host) {
    const authority = source.authority == null ? null : record(source.authority)
    if (source.enabled === false && source.latest === null &&
      (authority === null || authority.state === 'NoTakeover')) return {
      label: 'Sharing is off', address: null, local: false, warning: null, hidden: true }
    if (!authority) return { label: 'Host decision not checked', address: null, local: false, warning: null }
    if (authority.state === 'NoTakeover') return {
      label: 'Original Host PC · hosting has not moved', address: null, local: true, warning: null }
    if (authority.state === 'CompetingHistories' || authority.state === 'ReviewRequired') return {
      label: 'Host decision needs review', address: null, local: false,
      warning: 'Keep this world stopped until the hosting decisions are resolved.' }
    if (authority.state !== 'ThisPcHost' && authority.state !== 'OldHostFenced') throw new Error('Hosting identity is invalid.')
    const head = record(authority.head)
    if (!validDevice(head.hostDeviceId) || !validHash(head.recordHash) ||
      typeof head.hostAddress !== 'string') throw new Error('Signed hosting identity is invalid.')
    const address = normalizedDirectIpHttpsEndpoint(head.hostAddress)
    if (!address) throw new Error('Signed hosting address is invalid.')
    return { label: authority.state === 'ThisPcHost' ? 'This PC · approved Host' :
      `Approved Host · PC ending ${head.hostDeviceId.slice(-6).toUpperCase()}`,
      address, local: authority.state === 'ThisPcHost', warning: authority.state === 'OldHostFenced' ?
        'Hosting this shared world is blocked on this PC.' : null, recordHash: head.recordHash }
  }
  // The local recovery projection names the current verified quorum head only
  // in these two states. Armed candidates and historical offers are not Hosts.
  if (source.state === 'HistoryReviewRequired') return { label: 'Host decision needs review',
    address: null, local: false, warning: 'The group needs to review conflicting hosting decisions.' }
  if (source.state !== 'MajorityRecorded' && source.state !== 'ObservedMajority') return {
    label: 'Host decision not checked', address: null, local: false, warning: null,
    recordHash: validHash(source.authorityHeadHash) ? source.authorityHeadHash : undefined }
  if (source.majorityReached !== true || !validHash(source.authorityHeadHash) ||
    !validDevice(source.candidateDeviceId) || typeof source.candidateAddress !== 'string')
    throw new Error('Signed hosting identity is invalid.')
  const address = normalizedDirectIpHttpsEndpoint(source.candidateAddress)
  if (!address) throw new Error('Signed hosting address is invalid.')
  return { label: `Approved Host · PC ending ${source.candidateDeviceId.slice(-6).toUpperCase()}`,
    address, local: source.state === 'MajorityRecorded', warning: null }
}

export function parseSharedSuccessorIdentity(value: unknown): HostingIdentity | null {
  const source = record(value)
  if (source.staged !== true || !validHash(source.recordHash) ||
    !validHash(source.controlRouteFingerprint) || typeof source.controlRouteAddress !== 'string') return null
  const address = normalizedDirectIpHttpsEndpoint(source.controlRouteAddress)
  if (!address) return null
  // This projection comes from the backend's verified unique authority and
  // exact staged copy. Its route fields exist only for this PC's configured
  // route matching that signed candidate. It is preparation, never run Ready.
  return { label: 'This PC · next Host finishing setup', address, local: false, warning: null, recordHash: source.recordHash }
}

export function SharedHostingIdentity({ profileId, host, available = true, worldName,
  serverState, serverStatusFresh = false, observedHostAddress }: {
  profileId: string; host: boolean; available?: boolean; worldName?: string;
  serverState?: string; serverStatusFresh?: boolean; observedHostAddress?: string | null
}) {
  const [identitySnapshot, setIdentity] = useState<{ scope: string; value: HostingIdentity } | null>(null)
  const identityScope = `${host ? 'host' : 'friend'}/${profileId}`
  const identity = identitySnapshot?.scope === identityScope ? identitySnapshot.value : null
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    let active = true
    const controller = new AbortController()
    let timer: number | undefined
    setIdentity(current => current?.scope === identityScope ? current : null); setFailed(false)
    const check = async () => {
      try {
        let next = await getLocalJson(host ? `/api/local/profiles/${profileId}/shared-world` :
          `/api/local/friend/${profileId}/shared-world/recovery`, value => parseSharedHostingIdentity(value, host), controller.signal)
        if (!host && next.label === 'Host decision not checked' && next.recordHash) {
          const successor = await getLocalJson(`/api/local/friend/${profileId}/shared-world/handoff/restore`,
            parseSharedSuccessorIdentity, controller.signal).catch(() => null)
          if (successor && successor.recordHash === next.recordHash) next = successor
        }
        if (!host && next.label === 'Host decision not checked' && !next.recordHash) {
          const relevant = await getLocalJson(`/api/local/friend/${profileId}/shared-world`,
            hasSharedSaveEvidence, controller.signal)
          next = { ...next, hidden: !relevant }
        }
        if (active) { setIdentity({ scope: `${host ? 'host' : 'friend'}/${profileId}`, value: next }); setFailed(false) }
      } catch { if (active) setFailed(true) }
      if (active) timer = window.setTimeout(() => void check(), 5000)
    }
    void check()
    return () => { active = false; controller.abort(); window.clearTimeout(timer) }
  }, [profileId, host, available, identityScope])
  if (!host && identity === null) return null
  return <SharedHostingIdentityView identity={identity} failed={failed} host={host} worldName={worldName} scope={identityScope}
    serverState={serverState} serverStatusFresh={serverStatusFresh} observedHostAddress={observedHostAddress} />
}

export function SharedHostingIdentityView({ identity, failed = false, host, scope,
  serverState, serverStatusFresh = false }: {
  identity: HostingIdentity | null; failed?: boolean; host: boolean; worldName?: string; scope?: string;
  serverState?: string; serverStatusFresh?: boolean; observedHostAddress?: string | null
}) {
  if (identity?.hidden || host && identity === null) return null
  // A matching address alone does not bind a Friend snapshot's TLS identity to
  // this signed authority record. Only local Host authority can bind its run.
  const matches = !failed && identity != null && host && identity.local
  const observed = serverStatusFresh && matches && serverState != null ?
    serverState === 'Ready' ? 'Ready · game join not tested' : serverState : 'Unknown'
  return <section className="shared-hosting-identity" aria-label="Shared world Host">
    <p className="shared-hosting-identity-row"><span>{failed && identity ? `Last known: ${identity.label}` : identity?.label ?? 'Host decision not checked'}</span>
      <small>Current Host status: {observed}{failed ? ' · latest Host check failed' : ''}</small>
      {!host && serverState && identity?.label === 'Host decision not checked' && <small>Connected server: {serverStatusFresh ?
        serverState === 'Ready' ? 'Ready · game join not tested' : serverState : 'Unknown'}</small>}
    </p>
    {identity?.warning && <DismissibleNotice as="p" scope={scope} noticeKey={identity.recordHash ?? identity.label} role="alert">{identity.warning}</DismissibleNotice>}
  </section>
}
