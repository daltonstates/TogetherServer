import { useCallback, useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input, TextArea } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { Icon } from './Icon'
import { parseBasicResult } from './contracts'
import { sharedSaveAge } from './sharedWorldUx'
import { PeerLiveTestCopies } from './PeerLiveTestCopies'
import './SharedWorld.css'

type Route = { address: string; certificateSha256: string }
type Peer = { deviceId: string; publicKey: string; revision: number; routes: Route[];
  hasConflict: boolean; authorized: boolean; availableVersionHashes: string[]; announcedUtc: string;
  contact: { lastAttemptUtc: string; lastSuccessUtc: string | null; lastAttemptSucceeded: boolean } | null }
type BranchView = { deviceId: string; publicKey: string; hasConflict: boolean; authorized: boolean;
  branch: { branchHash: string; baseVersionHash: string; headVersionHash: string;
    parentVersionHash: string | null; versionNumber: number; authorityHash: string | null } }
type SessionView = { deviceId: string; publicKey: string; hasConflict: boolean; authorized: boolean;
  session: { sessionId: string; branchHash: string; revision: number;
    state: 'Preparing' | 'Hosting' | 'Stopped' | 'Interrupted'; baseVersionHash: string;
    headVersionHash: string | null; startedUtc: string | null; endedUtc: string | null } }
type GroupSelection = { canChoose: boolean; needsChoice: boolean;
  heads: { selectionHash: string; deviceId: string; publicKey: string; sessionId: string;
    branchHash: string; authorized: boolean; targetKnown: boolean }[] }
type DefaultReview = { groupKey: string; deviceId: string; publicKey: string; sessionId: string; branchHash: string }
export type WorldPeerGroup = { groupKey: string; name: string; profileId: string; selfDeviceId: string;
  sharingEnabled: boolean; receiveConsented: boolean; canReceive: boolean; canReshare: boolean; canHost: boolean;
  warning: string | null; selection: GroupSelection | null;
  state: { groupKey: string; peers: Peer[]; branches: BranchView[]; sessions: SessionView[];
    defaultChoices: { authorityHash: string; versionHash: string }[]; needsChoice: boolean;
    knownSessions: number; announcementCount: number;
    replication: { knownPeers: number; peersWithRoutes: number; peersLastSyncSucceeded: number;
      claimedCopyPeers: number; lastSuccessfulSyncUtc: string | null; hasConflicts: boolean;
      capacityReached: boolean; state: string } } }

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const hashPattern = /^[0-9a-f]{64}$/i
const isDocumentHidden = () => document.visibilityState === 'hidden'
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('World group information is invalid.')
  return value as Record<string, unknown>
}
function text(value: unknown, limit = 500): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > limit ||
    [...value].some(character => character.charCodeAt(0) < 32))
    throw new Error('World group text is invalid.')
  return value
}
function flag(value: unknown): boolean {
  if (typeof value !== 'boolean') throw new Error('World group permission is invalid.')
  return value
}
function count(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0)
    throw new Error('World group count is invalid.')
  return value
}
function identity(value: unknown, pattern = guidPattern): string {
  const result = text(value)
  if (!pattern.test(result)) throw new Error('World group identity is invalid.')
  return result
}
function date(value: unknown): string | null {
  if (value === null || value === undefined) return null
  const result = text(value, 64)
  if (!/^\d{4}-\d\d-\d\dT.*(?:Z|\+00:00)$/.test(result) || !Number.isFinite(Date.parse(result)))
    throw new Error('World group date is invalid.')
  return result
}
function list<T>(value: unknown, maximum: number, decode: (item: unknown) => T): T[] {
  if (!Array.isArray(value) || value.length > maximum) throw new Error('World group list is invalid.')
  return value.map(decode)
}
function parsePeer(value: unknown): Peer {
  const source = object(value)
  const contact = source.contact == null ? null : object(source.contact)
  const announcedUtc = date(source.announcedUtc)
  if (announcedUtc === null) throw new Error('World group announcement date is missing.')
  return { deviceId: identity(source.deviceId), publicKey: text(source.publicKey), revision: count(source.revision),
    routes: list(source.routes, 4, item => {
      const route = object(item)
      return { address: text(route.address, 255), certificateSha256: identity(route.certificateSha256, hashPattern) }
    }), hasConflict: flag(source.hasConflict), authorized: flag(source.authorized),
    availableVersionHashes: list(source.availableVersionHashes, 128, item => identity(item, hashPattern)), announcedUtc,
    contact: contact === null ? null : { lastAttemptUtc: date(contact.lastAttemptUtc) ?? '',
      lastSuccessUtc: date(contact.lastSuccessUtc), lastAttemptSucceeded: flag(contact.lastAttemptSucceeded) } }
}
function parseBranch(value: unknown): BranchView {
  const source = object(value)
  const branch = object(source.branch)
  return { deviceId: identity(source.deviceId), publicKey: text(source.publicKey),
    hasConflict: flag(source.hasConflict), authorized: flag(source.authorized), branch: {
      branchHash: identity(branch.branchHash, hashPattern), baseVersionHash: identity(branch.baseVersionHash, hashPattern),
      headVersionHash: identity(branch.headVersionHash, hashPattern), versionNumber: count(branch.versionNumber),
      parentVersionHash: branch.parentVersionHash == null ? null : identity(branch.parentVersionHash, hashPattern),
      authorityHash: branch.authorityHash == null ? null : identity(branch.authorityHash, hashPattern) } }
}
function parseSession(value: unknown): SessionView {
  const source = object(value)
  const session = object(source.session)
  if (!['Preparing', 'Hosting', 'Stopped', 'Interrupted'].includes(String(session.state)))
    throw new Error('World session state is invalid.')
  return { deviceId: identity(source.deviceId), publicKey: text(source.publicKey),
    hasConflict: flag(source.hasConflict), authorized: flag(source.authorized), session: {
      sessionId: identity(session.sessionId), branchHash: identity(session.branchHash, hashPattern),
      revision: count(session.revision), state: session.state as SessionView['session']['state'],
      baseVersionHash: identity(session.baseVersionHash, hashPattern),
      headVersionHash: session.headVersionHash == null ? null : identity(session.headVersionHash, hashPattern),
      startedUtc: date(session.startedUtc), endedUtc: date(session.endedUtc) } }
}
function parseSelection(value: unknown): GroupSelection | null {
  if (value === undefined || value === null) return null
  const source = object(value)
  return { canChoose: flag(source.canChoose), needsChoice: flag(source.needsChoice),
    heads: list(source.heads, 128, value => {
      const head = object(value)
      return { selectionHash: identity(head.selectionHash, hashPattern), deviceId: identity(head.deviceId),
        publicKey: text(head.publicKey), sessionId: identity(head.sessionId), branchHash: identity(head.branchHash, hashPattern),
        authorized: flag(head.authorized), targetKnown: flag(head.targetKnown) }
    }) }
}
export function parseWorldPeerGroups(value: unknown): WorldPeerGroup[] {
  const result = list(value, 128, item => {
    const source = object(item)
    const state = object(source.state)
    const root = object(state.root)
    const replication = object(state.replication)
    const groupKey = identity(source.groupKey, hashPattern)
    const profileId = identity(source.profileId)
    text(root.ownerPublicKey)
    identity(root.groupId)
    if (identity(root.profileId) !== profileId || identity(state.groupKey, hashPattern) !== groupKey)
      throw new Error('The peer information names another world.')
    return { groupKey, profileId, name: text(source.name, 100), selfDeviceId: identity(source.selfDeviceId),
      sharingEnabled: flag(source.sharingEnabled), receiveConsented: flag(source.receiveConsented),
      canReceive: flag(source.canReceive), canReshare: flag(source.canReshare), canHost: flag(source.canHost),
      warning: source.warning == null ? null : text(source.warning, 1000), selection: parseSelection(source.selection), state: { groupKey,
        peers: list(state.peers, 256, parsePeer), branches: list(state.branches, 8192, parseBranch),
        sessions: list(state.sessions, 128, parseSession), needsChoice: flag(state.needsChoice),
        knownSessions: count(state.knownSessions), announcementCount: count(state.announcementCount),
        defaultChoices: list(state.defaultChoices, 128, choice => {
          const entry = object(choice)
          return { authorityHash: identity(entry.authorityHash, hashPattern), versionHash: identity(entry.versionHash, hashPattern) }
        }), replication: { knownPeers: count(replication.knownPeers), peersWithRoutes: count(replication.peersWithRoutes),
          peersLastSyncSucceeded: count(replication.peersLastSyncSucceeded), claimedCopyPeers: count(replication.claimedCopyPeers),
          lastSuccessfulSyncUtc: date(replication.lastSuccessfulSyncUtc), hasConflicts: flag(replication.hasConflicts),
          capacityReached: flag(replication.capacityReached), state: text(replication.state) } } }
  })
  if (new Set(result.map(group => group.groupKey)).size !== result.length) throw new Error('A world group was repeated.')
  return result
}

export type WorldPeerPanelProps = { visible?: boolean; profileId?: string;
  onConnected?: () => void | Promise<void>; onChanged?: () => void | Promise<void>;
  onRecoverOwnerBranch?: (groupKey: string, profileId: string, branchHash: string) => void;
  onRecoverBranch?: (profileId: string, branchHash: string) => void }

export function WorldPeerPanel({ visible = true, profileId, onConnected, onChanged, onRecoverBranch, onRecoverOwnerBranch }: WorldPeerPanelProps) {
  const [groups, setGroups] = useState<WorldPeerGroup[]>([])
  const [loaded, setLoaded] = useState(false)
  const [checkedUtc, setCheckedUtc] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [messages, setMessages] = useState<Record<string, string>>({})
  const [contactCodes, setContactCodes] = useState<Record<string, string>>({})
  const [incomingCodes, setIncomingCodes] = useState<Record<string, string>>({})
  const [defaultReview, setDefaultReview] = useState<DefaultReview | null>(null)
  const flight = useRef<Promise<void> | null>(null)
  const controller = useRef<AbortController | null>(null)
  const mounted = useRef(true)
  const refresh = useCallback((): Promise<void> => {
    if (flight.current) return flight.current
    const requestController = new AbortController()
    controller.current = requestController
    const request = (async () => {
      try {
        const next = await getLocalJson('/api/local/world-peers', parseWorldPeerGroups, requestController.signal)
        if (!requestController.signal.aborted && mounted.current) {
          setGroups(next); setLoaded(true); setCheckedUtc(new Date().toISOString()); setError('')
        }
      } catch (cause) {
        if (!requestController.signal.aborted && mounted.current) setError(errorMessage(cause))
      } finally {
        if (controller.current === requestController) controller.current = null
      }
    })()
    flight.current = request
    void request.finally(() => { if (flight.current === request) flight.current = null })
    return request
  }, [])
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; controller.current?.abort() } }, [])
  useEffect(() => {
    if (!visible) return
    let active = true
    let timer: number | undefined
    let sequence = 0
    const poll = async () => {
      const current = ++sequence
      if (!active || document.visibilityState === 'hidden') return
      await refresh()
      if (active && current === sequence && !isDocumentHidden()) timer = window.setTimeout(() => void poll(), 5000)
    }
    const visibility = () => {
      window.clearTimeout(timer)
      if (document.visibilityState === 'hidden') { ++sequence; controller.current?.abort() }
      else void poll()
    }
    void poll()
    document.addEventListener('visibilitychange', visibility)
    return () => { active = false; ++sequence; window.clearTimeout(timer); controller.current?.abort()
      document.removeEventListener('visibilitychange', visibility) }
  }, [visible, refresh])

  const perform = async (group: WorldPeerGroup,
    operation: 'sharing' | 'receiving' | 'sync' | 'contact' | 'connect' | 'default' | 'recovery-source', body: unknown): Promise<boolean> => {
    if (busy) return false
    setBusy(group.groupKey); setMessages(current => ({ ...current, [group.groupKey]: '' }))
    try {
      const result = await changeJson(`/api/local/world-peers/${group.groupKey}/${operation}`,
        operation === 'sharing' || operation === 'receiving' ? 'PUT' : 'POST', parseBasicResult, body)
      if (!mounted.current) return false
      setMessages(current => ({ ...current, [group.groupKey]: result.message }))
      if (result.ok) {
        if (operation === 'default') setDefaultReview(null)
        if (operation === 'connect') await onConnected?.()
        else await onChanged?.()
        await refresh()
      }
      return result.ok
    } catch (cause) { if (mounted.current) setMessages(current => ({ ...current, [group.groupKey]: errorMessage(cause) })) }
    finally { if (mounted.current) setBusy(null) }
    return false
  }
  const recoverBranch = async (group: WorldPeerGroup, branchHash: string) => {
    if (await perform(group, 'recovery-source', {}) && mounted.current) onRecoverBranch?.(group.profileId, branchHash)
  }
  const contactCode = async (group: WorldPeerGroup) => {
    setBusy(group.groupKey)
    try {
      const value = await getLocalJson(`/api/local/world-peers/${group.groupKey}/contact-code`, source =>
        text(object(source).code, 32768))
      if (mounted.current) setContactCodes(current => ({ ...current, [group.groupKey]: value }))
    } catch (cause) { if (mounted.current) setMessages(current => ({ ...current, [group.groupKey]: errorMessage(cause) })) }
    finally { if (mounted.current) setBusy(null) }
  }
  if (!visible) return null
  const shown = groups.filter(group => !profileId || group.profileId === profileId)
  const pcName = (deviceId: string, selfDeviceId: string) => deviceId === selfDeviceId ? 'This PC' : `PC ending ${deviceId.slice(-6).toUpperCase()}`
  return <section className="world-peer-panel" aria-label="World groups">
    <header><div><h3>World groups</h3><p>Follow the world across its approved PCs and sessions.</p></div>
      <Button className="secondary" disabled={busy !== null} onClick={() => void refresh()}><Icon name="refresh" />Refresh groups</Button></header>
    <p className="helper-text">Following signed information does not allow save receiving. Background updates keep your current game connection and never start or stop a server.</p>
    {!loaded && !error && <p role="status">Checking this PC's saved world groups…</p>}
    {error && <DismissibleNotice scope="world-peers" noticeKey={error} role="alert"><p>{error}</p>
      <p>{loaded ? `Showing cached information checked ${sharedSaveAge(checkedUtc)}. Sessions and routes may have changed.` : 'World groups are unavailable. Retry when the local app responds.'}</p></DismissibleNotice>}
    {loaded && shown.length === 0 && <p>No saved world groups here yet. Connect to an approved Host and verify this PC's world membership first.</p>}
    {shown.map(group => {
      const activeSessions = group.state.sessions.filter(item => item.session.state === 'Hosting' || item.session.state === 'Preparing')
      const histories = [...new Set(group.state.branches.map(item => item.branch.branchHash))]
      const selection = group.selection
      const selected = selection?.heads.length === 1 && !selection.needsChoice &&
        selection.heads[0].authorized && selection.heads[0].targetKnown ? selection.heads[0] : null
      const reviewing = defaultReview?.groupKey === group.groupKey ? defaultReview : null
      const reviewTarget = reviewing && group.selection?.canChoose ? activeSessions.find(item => item.authorized && !item.hasConflict &&
        item.deviceId === reviewing.deviceId && item.publicKey === reviewing.publicKey &&
        item.session.sessionId === reviewing.sessionId && item.session.branchHash === reviewing.branchHash) : undefined
      return <article className="world-peer-group" key={group.groupKey} aria-label={group.name}>
        <header><div><h4>{group.name}</h4><p>{group.state.needsChoice ? 'Multiple histories: choose explicitly' : 'Saved world membership'}</p></div>
          <span className="world-peer-badge">{activeSessions.length} reported {activeSessions.length === 1 ? 'session' : 'sessions'}</span></header>
        {group.warning && <DismissibleNotice scope={group.groupKey} noticeKey={group.warning} role="alert">{group.warning}</DismissibleNotice>}
        {group.state.needsChoice && <p role="note">Competing histories are preserved. A newer save number does not choose a winner or move your connection.</p>}
        {selection && (selection.needsChoice || selection.heads.length > 1) && <p role="note"><strong>Conflicting group default choices.</strong> All choices stay visible until an authorized member explicitly chooses a default. Running sessions and your game connection continue.</p>}
        {selection?.heads.some(head => !head.authorized || !head.targetKnown) && <p role="note"><strong>Default choice awaiting review or session details.</strong> The chosen session is not confirmed in this PC's current signed information.</p>}
        <div className="world-peer-summary">
          <p><strong>Information sync</strong><span>{group.state.replication.lastSuccessfulSyncUtc ?
            `Last completed ${sharedSaveAge(group.state.replication.lastSuccessfulSyncUtc)}` : 'No completed peer sync recorded'}</span></p>
          <p><strong>Save receiving</strong><span>{!group.canReceive ? 'Owner permission required' :
            !group.receiveConsented ? 'Off on this PC — local consent required' : 'Allowed on this PC'}</span></p>
          <p><strong>Save forwarding</strong><span>{group.canReshare ? 'Owner permission granted' : 'Owner permission required'}</span></p>
        </div>
        <p className="helper-text">Offline PCs catch up when a permitted contact is reachable. Old signed information stays visible; its age does not erase a history.</p>
        <label><Input type="checkbox" checked={group.receiveConsented}
          disabled={busy !== null || (!group.receiveConsented && !group.canReceive)}
          onChange={event => void perform(group, 'receiving', { enabled: event.target.checked })} />Allow saves on this PC</label>
        <p className="helper-text">This consent stays with the world when its Host changes. Incoming peer contact is a separate choice.</p>
        <label><Input type="checkbox" checked={group.sharingEnabled} disabled={busy !== null}
          onChange={event => void perform(group, 'sharing', { enabled: event.target.checked })} />Allow world peers to contact this PC</label>
        <Button className="secondary" disabled={busy !== null} onClick={() => void perform(group, 'sync', {})}>
          <Icon name="refresh" />Sync world information</Button>
        <section className="world-peer-sessions" aria-label={`Sessions for ${group.name}`}>
          <h5>Hosts and sessions</h5>
          {activeSessions.length === 0 && <p>No active session is reported in this cached information. The game connection is unverified.</p>}
          {group.state.sessions.map(item => {
            const peer = group.state.peers.find(peer => peer.deviceId === item.deviceId)
            const historyNumber = histories.indexOf(item.session.branchHash) + 1
            const active = item.session.state === 'Hosting' || item.session.state === 'Preparing'
            const canConnect = item.authorized && !item.hasConflict && item.deviceId !== group.selfDeviceId &&
              active && peer?.authorized && !peer.hasConflict && !!peer.routes.length
            const isDefault = active && item.authorized && !item.hasConflict && selected?.deviceId === item.deviceId &&
              selected.publicKey === item.publicKey && selected.sessionId === item.session.sessionId && selected.branchHash === item.session.branchHash
            const canChoose = selection?.canChoose && item.authorized && !item.hasConflict && active
            return <div className="world-peer-session" key={`${item.deviceId}/${item.session.sessionId}/${item.session.revision}/${item.session.branchHash}/${item.session.state}/${item.session.headVersionHash ?? item.session.baseVersionHash}`}>
              <div><strong>{pcName(item.deviceId, group.selfDeviceId)}</strong><p>{item.session.state === 'Hosting' ? 'Hosting reported' :
                item.session.state === 'Preparing' ? 'Preparing to host' : item.session.state === 'Stopped' ? 'Stopped session' : 'Interrupted session'}
                {historyNumber > 0 ? ` · History ${historyNumber}` : ''}</p>
                <p className="helper-text">{peer ? `Signed update ${sharedSaveAge(peer.announcedUtc)}. ` : ''}Game connection and real join unverified.</p></div>
              {isDefault && <span className="world-peer-badge">Group default</span>}
              {canConnect && <Button className="secondary" disabled={busy !== null}
                onClick={() => void perform(group, 'connect', { deviceId: item.deviceId, sessionId: item.session.sessionId })}>
                <Icon name="link" />Connect to this session</Button>}
              {canChoose && !isDefault && <Button className="secondary" disabled={busy !== null}
                onClick={() => setDefaultReview({ groupKey: group.groupKey, deviceId: item.deviceId,
                  publicKey: item.publicKey, sessionId: item.session.sessionId, branchHash: item.session.branchHash })}>
                Use as group default</Button>}
              {item.hasConflict && <p role="note">This PC reported conflicting session information. Review before connecting.</p>}
              {!item.authorized && <p>Current world membership needs review.</p>}
            </div>
          })}
          {reviewing && <section className="world-peer-default-review" aria-label="Review group default">
            <h5>Use {pcName(reviewing.deviceId, group.selfDeviceId)} as the group default?</h5>
            <p>This publishes a signed choice for the group. All histories and running sessions stay preserved. Your current game connection stays selected; this action does not start, stop, or merge a server.</p>
            {!reviewTarget && <p role="alert">This session or your permission changed. Choose a current session again.</p>}
            <div><Button className="secondary" disabled={busy !== null} onClick={() => setDefaultReview(null)}>Cancel default choice</Button>
              <Button disabled={busy !== null || !reviewTarget} onClick={() => {
                if (reviewTarget) void perform(group, 'default', { deviceId: reviewTarget.deviceId, sessionId: reviewTarget.session.sessionId })
              }}>Confirm group default</Button></div>
          </section>}
        </section>
        <details><summary>Save histories and copy claims</summary>
          <p>{group.state.replication.claimedCopyPeers} PCs report having save copies. Signed claims are not completed transfers or confirmed receipts.</p>
          <p>Confirmed transfer receipts are shown with the received save after its bytes and signature are verified.</p>
          {histories.map((branchHash, index) => <section key={branchHash} aria-label={`History ${index + 1}`}>
            <h5>History {index + 1}</h5>
            {group.state.branches.filter(item => item.branch.branchHash === branchHash).map(item =>
              <p key={`${item.deviceId}/${item.branch.headVersionHash}`}>
                {pcName(item.deviceId, group.selfDeviceId)} reports saved version {item.branch.versionNumber}
                {item.hasConflict ? ' · conflicting signed claims' : ' · availability claimed'}</p>)}
            {onRecoverBranch && group.canHost && group.receiveConsented && <Button className="secondary" disabled={busy !== null}
              onClick={() => void recoverBranch(group, branchHash)}>Recover this history on this PC</Button>}
            {onRecoverOwnerBranch && group.selfDeviceId === '00000000-0000-0000-0000-000000000000' &&
              group.canHost && group.receiveConsented && <Button className="secondary" disabled={busy !== null}
                onClick={() => onRecoverOwnerBranch(group.groupKey, group.profileId, branchHash)}>Recover this history on the owner PC</Button>}
          </section>)}
          {!histories.length && <p>No save history has been announced yet.</p>}
          {group.canHost && <p>Manual recovery rechecks the newest complete verified copy on this PC. Other sessions may continue; no vote or waiting period is required for a separate recovery branch.</p>}
        </details>
        <PeerLiveTestCopies groupKey={group.groupKey} />
        <details><summary>Contact another group PC</summary>
          <p>Exchange a signed contact code with an already approved world member. It contains contact and identity information, with no device credential or game password.</p>
          <Button className="secondary" disabled={busy !== null} onClick={() => void contactCode(group)}>Show this PC's contact code</Button>
          {contactCodes[group.groupKey] && <><TextArea aria-label={`Contact code for ${group.name}`} readOnly rows={3} value={contactCodes[group.groupKey]} />
            <Button className="secondary" onClick={() => void navigator.clipboard.writeText(contactCodes[group.groupKey])
              .then(() => setMessages(current => ({ ...current, [group.groupKey]: 'Contact code copied.' })))
              .catch(() => setMessages(current => ({ ...current, [group.groupKey]: 'Select the contact code to copy it manually.' })))}>
              <Icon name="copy" />Copy contact code</Button></>}
          <label>Contact code from another PC<TextArea rows={3} maxLength={32768} value={incomingCodes[group.groupKey] ?? ''}
            onChange={event => setIncomingCodes(current => ({ ...current, [group.groupKey]: event.target.value }))} /></label>
          <Button className="secondary" disabled={busy !== null || !incomingCodes[group.groupKey]?.trim()}
            onClick={() => void perform(group, 'contact', { code: incomingCodes[group.groupKey].trim() })}>Add approved contact</Button>
        </details>
        <details><summary>Technical details</summary>
          <p>{group.state.replication.state}. {group.state.announcementCount} signed updates; {group.state.knownSessions} known sessions.</p>
          {group.state.replication.capacityReached && <p role="alert">Metadata capacity needs review. Existing histories are retained.</p>}
          <p>Group: <code>{group.groupKey}</code></p>
          {group.state.peers.map(peer => <div key={peer.deviceId}><p>{pcName(peer.deviceId, group.selfDeviceId)} · announcement revision {peer.revision}</p>
            <p>Signing identity: <code>{peer.publicKey}</code></p>
            {peer.routes.map(route => <p key={route.address}>{route.address}<br /><code>{route.certificateSha256}</code></p>)}</div>)}
        </details>
        {messages[group.groupKey] && <DismissibleNotice scope={group.groupKey} noticeKey={messages[group.groupKey]} role="status">{messages[group.groupKey]}</DismissibleNotice>}
      </article>
    })}
  </section>
}
