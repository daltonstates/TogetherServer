import { useState } from 'react'
import type { Decoder, Snapshot } from './contracts'
import { Button, Input } from './Controls'
import { Icon } from './Icon'
import { gameLabel } from './GameProfile'
import { originalLocalServer } from './sharedServerProfiles'
import './ServerDirectory.css'

export type DirectoryServer = {
  id: string; name: string; kind: string; state: string; onlinePlayers: number | null; maxPlayers: number | null
  playerCountTrusted: boolean; playerObservationSource: string | null; playerCountObservedUtc: string | null
  playerCountRefreshing: boolean; playerCountFailureCount: number
  separateCopySourceProfileId: string | null; sourceName: string | null; sourceHostName: string | null
  sharedWorldId?: string | null; originalProfileId?: string | null; isSuccessor?: boolean
  requiresReview?: boolean
}
export type DirectoryHost = { connectionId: string; hostId: string | null; connectionName: string; hostProfileName: string | null
  state: string; connectionCode: string | null; lastConnectedUtc: string | null; servers: DirectoryServer[] }
export type ServerDirectoryView = { localServers: DirectoryServer[]; friendHosts: DirectoryHost[] }
export type ServerSelection = { kind: 'local'; profileId: string } | { kind: 'friend'; connectionId: string; profileId: string }

const guid = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('The server list is invalid.')
  return value as Record<string, unknown>
}
function text(value: unknown, nullable = false): string | null {
  if (nullable && value === null) return null
  if (typeof value !== 'string' || value.length > 512) throw new Error('The server list contains invalid text.')
  return value
}
function identifier(value: unknown, nullable = false): string | null {
  if (nullable && value === null) return null
  const result = text(value)
  if (!result || !guid.test(result)) throw new Error('The server list contains an invalid identity.')
  return result
}
function count(value: unknown): number | null {
  if (value === null) return null
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) throw new Error('The server list contains an invalid player count.')
  return value
}
function flag(value: unknown): boolean {
  if (typeof value !== 'boolean') throw new Error('The server list contains an invalid state.')
  return value
}
function timestamp(value: unknown): string | null {
  const result = text(value, true)
  if (result !== null && !Number.isFinite(Date.parse(result))) throw new Error('The server list contains an invalid observation time.')
  return result
}
function list<T>(value: unknown, decode: (item: unknown) => T, key: (item: T) => string): T[] {
  if (!Array.isArray(value) || value.length > 100) throw new Error('The server list is too large or invalid.')
  const values = value.map(decode)
  if (new Set(values.map(key)).size !== values.length) throw new Error('The server list contains duplicate identities.')
  return values
}
function server(value: unknown): DirectoryServer {
  const item = object(value)
  const sharedWorldId = item.sharedWorldId == null ? null : text(item.sharedWorldId)
  const originalProfileId = item.originalProfileId == null ? null : identifier(item.originalProfileId)
  const isSuccessor = item.isSuccessor === undefined ? false : flag(item.isSuccessor)
  if (sharedWorldId !== null && !/^[a-f\d]{64}$/i.test(sharedWorldId) ||
    (sharedWorldId === null) !== (originalProfileId === null) || isSuccessor && sharedWorldId === null)
    throw new Error('The server list contains an invalid shared-world identity.')
  return { id: identifier(item.id)!, name: text(item.name)!, kind: text(item.kind)!, state: text(item.state)!,
    onlinePlayers: count(item.onlinePlayers), maxPlayers: count(item.maxPlayers), playerCountTrusted: flag(item.playerCountTrusted),
    playerObservationSource: text(item.playerObservationSource, true), playerCountObservedUtc: timestamp(item.playerCountObservedUtc),
    playerCountRefreshing: flag(item.playerCountRefreshing), playerCountFailureCount: count(item.playerCountFailureCount) ?? 0,
    separateCopySourceProfileId: identifier(item.separateCopySourceProfileId, true), sourceName: text(item.sourceName, true),
    sourceHostName: text(item.sourceHostName, true),
    ...(item.sharedWorldId !== undefined || item.originalProfileId !== undefined || item.isSuccessor !== undefined
      ? { sharedWorldId, originalProfileId, isSuccessor } : {}) }
}
export const parseServerDirectory: Decoder<ServerDirectoryView> = value => {
  const root = object(value)
  return { localServers: list(root.localServers, server, row => row.id),
    friendHosts: list(root.friendHosts, value => {
      const item = object(value)
      return { connectionId: identifier(item.connectionId)!, hostId: identifier(item.hostId, true),
        connectionName: text(item.connectionName)!, hostProfileName: text(item.hostProfileName, true),
        state: text(item.state)!, connectionCode: text(item.connectionCode, true), lastConnectedUtc: timestamp(item.lastConnectedUtc),
        servers: list(item.servers, server, row => row.id) }
    }, host => host.connectionId) }
}

const blankCount = { playerCountRefreshing: false, playerCountFailureCount: 0,
  separateCopySourceProfileId: null, sourceName: null, sourceHostName: null }
/** Overlay current canonical details immediately while the passive combined list catches up. */
export function directoryWithSnapshot(directory: ServerDirectoryView | null, snapshot: Snapshot | null): ServerDirectoryView {
  const view = directory ?? { localServers: [], friendHosts: [] }
  if (!snapshot) return view
  if (snapshot.mode === 'Host') return { ...view, localServers: snapshot.settings.profiles.map(profile => {
    const run = snapshot.runs.find(item => item.profileId === profile.id)
    const prior = view.localServers.find(item => item.id === profile.id)
    const sourceHost = view.friendHosts.find(host => host.servers.some(server => server.id === profile.separateCopySourceProfileId))
    const source = sourceHost?.servers.find(server => server.id === profile.separateCopySourceProfileId)
    const localSource = profile.separateCopySourceProfileId ? originalLocalServer(profile, snapshot.settings.profiles) : null
    const recovery = snapshot.crashRecovery?.[profile.id]
    return { ...blankCount, id: profile.id, name: profile.name, kind: profile.kind, state: run?.state ?? 'Unknown',
      onlinePlayers: run?.onlinePlayers ?? null, maxPlayers: run?.maxPlayers ?? null, playerCountTrusted: run?.playerCountTrusted ?? false,
      playerObservationSource: run?.playerObservationSource ?? null, playerCountObservedUtc: run?.playerCountObservedUtc ?? null,
      playerCountRefreshing: run?.playerCountRefreshing ?? false, playerCountFailureCount: run?.playerCountFailureCount ?? 0,
      separateCopySourceProfileId: profile.separateCopySourceProfileId ?? null,
      ...(prior?.sharedWorldId ? { sharedWorldId: prior.sharedWorldId, originalProfileId: prior.originalProfileId,
        isSuccessor: prior.isSuccessor } : {}),
      requiresReview: run?.processId != null && run.state === 'Offline' ||
        !!snapshot.startupRecovery?.items.some(item => item.profileId === profile.id && ['Ready to resume', 'Needs review'].includes(item.state)) ||
        !!recovery && recovery.state !== 'Recovered',
      sourceName: localSource?.name ?? prior?.sourceName ?? source?.name ?? null,
      sourceHostName: localSource ? null : prior?.sourceHostName ?? sourceHost?.hostProfileName ?? sourceHost?.connectionName ?? null }
  }) }
  const connections = snapshot.connections?.length ? snapshot.connections : snapshot.connectionId ? [snapshot] : []
  const hosts = connections.map(host => ({ connectionId: host.connectionId, hostId: host.hostId || null,
    connectionName: host.connectionName || 'Saved friend', hostProfileName: host.hostProfileName ?? null,
    state: host.state, connectionCode: host.connectionCode ?? null, lastConnectedUtc: host.lastConnectedUtc,
    servers: !host.profiles.length && ['Disconnected', 'Disconnected/Unknown'].includes(host.state)
      ? (view.friendHosts.find(prior => prior.connectionId === host.connectionId)?.servers ?? []).map(server => ({ ...server,
        state: 'Unknown', onlinePlayers: null, maxPlayers: null, playerCountTrusted: false, playerCountObservedUtc: null }))
      : host.profiles.map(profile => ({ ...blankCount,
      ...view.friendHosts.find(prior => prior.connectionId === host.connectionId)?.servers.find(server => server.id === profile.id),
      id: profile.id, name: profile.name, kind: profile.kind, state: profile.state,
      onlinePlayers: profile.onlinePlayers, maxPlayers: profile.maxPlayers, playerCountTrusted: profile.playerCountTrusted ?? false,
      playerObservationSource: profile.playerObservationSource ?? null, playerCountObservedUtc: profile.playerCountObservedUtc ?? null,
      playerCountRefreshing: profile.playerCountRefreshing ?? false, playerCountFailureCount: profile.playerCountFailureCount ?? 0 })) }))
  return { ...view, friendHosts: snapshot.connections !== null ? hosts
    : [...view.friendHosts.filter(host => !hosts.some(next => next.connectionId === host.connectionId)), ...hosts] }
}

/** Retained storage stays in settings; only inactive duplicates leave the ordinary list. */
export function ordinaryLocalServers(view: ServerDirectoryView): DirectoryServer[] {
  const roots = new Map<string, DirectoryServer[]>()
  for (const server of view.localServers) {
    const rootId = server.separateCopySourceProfileId ?? server.id
    roots.set(rootId, [...(roots.get(rootId) ?? []), server])
  }
  return [...roots.entries()].flatMap(([rootId, copies]) => {
    const unresolved = copies.filter(server => server.state !== 'Offline' || server.requiresReview)
    if (unresolved.length) return unresolved
    const original = copies.find(server => server.id === rootId && !server.separateCopySourceProfileId)
    if (original) return [original]
    const listedByFriend = view.friendHosts.some(host => host.state !== 'Revoked' && host.connectionCode !== 'Revoked' &&
      host.servers.some(server => server.id === rootId))
    return listedByFriend ? [] : copies.slice(0, 1)
  })
}

type DirectoryLocation = { server: DirectoryServer; target: ServerSelection; host?: DirectoryHost }
type SidebarServer = {
  anchor: DirectoryLocation; route: DirectoryLocation; locations: DirectoryLocation[]
  name: string; hostedBy: string | null; multipleHosts: boolean
}

function sameSelection(a: ServerSelection | null, b: ServerSelection): boolean {
  return a?.kind === b.kind && a.profileId === b.profileId &&
    (a.kind === 'local' || b.kind === 'friend' && a.connectionId === b.connectionId)
}

function connectedHost(host: DirectoryHost | undefined, nowMs: number): boolean {
  if (!host) return true
  const lastConnected = host.lastConnectedUtc ? Date.parse(host.lastConnectedUtc) : NaN
  return ['Connected', 'Disabled'].includes(host.state) && Number.isFinite(lastConnected) &&
    lastConnected <= nowMs && nowMs - lastConnected <= 15_000
}

/** Signed world identity controls grouping; the selected action still names the actual hosting PC. */
function sidebarServers(view: ServerDirectoryView, selection: ServerSelection | null, nowMs: number): SidebarServer[] {
  const ordinary = ordinaryLocalServers(view)
  const locations: DirectoryLocation[] = [
    ...view.localServers.filter(server => server.sharedWorldId || ordinary.includes(server))
      .map(server => ({ server, target: { kind: 'local' as const, profileId: server.id } })),
    ...view.friendHosts.flatMap(host => host.servers.map(server => ({ server, host,
      target: { kind: 'friend' as const, connectionId: host.connectionId, profileId: server.id } })))
  ]
  const worlds = new Map<string, DirectoryLocation[]>()
  for (const location of locations) {
    const { server, target } = location
    const key = server.sharedWorldId && server.originalProfileId
      ? `shared:${server.sharedWorldId.toUpperCase()}:${server.originalProfileId.toLowerCase()}` : JSON.stringify(target)
    worlds.set(key, [...worlds.get(key) ?? [], location])
  }
  return [...worlds.values()].map(family => {
    const anchor = family.find(({ server }) => !server.isSuccessor && server.id === server.originalProfileId) ?? family[0]
    const active = family.filter(({ server, host }) => connectedHost(host, nowMs) &&
      (['Ready', 'Starting', 'Stopping', 'Process running', 'Listening', 'Unknown'].includes(server.state) || server.requiresReview))
    const route = active.length === 1 ? active[0] : active.find(location => sameSelection(selection, location.target)) ?? anchor
    const successor = route.server.isSuccessor === true && active.includes(route)
    return { anchor, route, locations: family,
      name: anchor.server.isSuccessor ? anchor.server.sourceName || anchor.server.name : anchor.server.name,
      hostedBy: successor ? route.host ? route.host.hostProfileName || route.host.connectionName : 'this PC' : null,
      multipleHosts: active.length > 1 }
  })
}

export function GuestHostingLabel({ server }: { server: Pick<DirectoryServer, 'separateCopySourceProfileId' | 'sourceName' | 'sourceHostName'> }) {
  if (!server.separateCopySourceProfileId) return null
  return <p className="guest-hosting-label"><Icon name="users" size={16} /><span><strong>Hosting for {server.sourceHostName || 'a friend'}</strong>
    {server.sourceName && <small>Shared save: {server.sourceName}</small>}</span></p>
}

function playerLabel(server: DirectoryServer, nowMs: number): string {
  const observed = server.playerCountObservedUtc ? Date.parse(server.playerCountObservedUtc) : NaN
  const fresh = server.state === 'Ready' && server.playerCountTrusted && server.onlinePlayers !== null &&
    Number.isFinite(observed) && observed <= nowMs && nowMs - observed <= 10_000
  if (fresh) return `${server.onlinePlayers}${server.maxPlayers === null ? '' : ` / ${server.maxPlayers}`} online`
  if (server.state === 'Offline') return 'Offline'
  return 'Players —'
}

export function ServerDirectory({ view, selection, nowMs, busy, error, onRetry, onSelect, onAddLocal, onConnect }: {
  view: ServerDirectoryView; selection: ServerSelection | null; nowMs: number; busy: boolean; error?: string
  onRetry: () => void; onSelect: (selection: ServerSelection) => void; onAddLocal: () => void; onConnect: () => void
}) {
  const [search, setSearch] = useState('')
  const [showRemoved, setShowRemoved] = useState(false)
  const query = search.trim().toLocaleLowerCase()
  const removed = (host: DirectoryHost) => host.connectionCode === 'Revoked' || host.state === 'Revoked'
  const visibleHosts = view.friendHosts.filter(host => showRemoved || !removed(host))
  const entries = sidebarServers({ ...view, friendHosts: visibleHosts }, selection, nowMs)
  const matches = (entry: SidebarServer) => [entry.name, entry.hostedBy ?? '', ...entry.locations.map(({ server, host }) =>
    `${server.name} ${gameLabel(server.kind)} ${server.sourceName ?? ''} ${server.sourceHostName ?? ''} ${host?.connectionName ?? ''} ${host?.hostProfileName ?? ''}`)]
    .join(' ').toLocaleLowerCase().includes(query)
  const ordinaryLocal = ordinaryLocalServers(view)
  const selectedLocal = selection?.kind === 'local' ? view.localServers.find(server => server.id === selection.profileId) : undefined
  const selectedLocalId = ordinaryLocal.some(server => server.id === selectedLocal?.id) ? selectedLocal?.id : selectedLocal &&
    ordinaryLocal.find(server => (server.separateCopySourceProfileId ?? server.id) ===
      (selectedLocal.separateCopySourceProfileId ?? selectedLocal.id))?.id
  const owned = (server: DirectoryServer) => !!originalLocalServer(server, view.localServers)
  const local = entries.filter(entry => !entry.anchor.host && matches(entry))
  const own = local.filter(entry => owned(entry.anchor.server))
  const hostedForFriends = local.filter(entry => !owned(entry.anchor.server))
  const friends = visibleHosts.map(host => ({ ...host,
    entries: entries.filter(entry => entry.anchor.host?.connectionId === host.connectionId && matches(entry)) }))
    .filter(host => host.entries.length || !host.servers.length && (!query || `${host.connectionName} ${host.hostProfileName ?? ''}`.toLocaleLowerCase().includes(query)))
  const row = (entry: SidebarServer) => {
    const { server, target, host } = entry.route
    const selected = entry.locations.some(location => sameSelection(selection, location.target)) ||
      target.kind === 'local' && selection?.kind === 'local' && selectedLocalId === server.id
    const guest = !!server.separateCopySourceProfileId && !host && !owned(server)
    const originalHost = entry.anchor.host
    const owner = originalHost ? originalHost.hostProfileName || originalHost.connectionName : 'this PC'
    const connected = connectedHost(host, nowMs)
    return <Button className={`server-directory-row${selected ? ' selected' : ''}`} key={`${entry.anchor.target.kind}-${originalHost?.connectionId ?? ''}-${entry.anchor.server.id}`}
      aria-label={`Open ${entry.name} on ${owner}`} aria-current={selected ? 'true' : undefined} disabled={busy}
      onClick={() => onSelect(target)}>
      <Icon name={entry.hostedBy ? 'monitor' : guest ? 'users' : host ? 'link' : 'server'} size={17} />
      <span><strong>{entry.name}</strong><small>{gameLabel(server.kind)} · {connected ? playerLabel(server, nowMs) : 'Host offline · status unavailable'}</small>
        {entry.hostedBy && <small className="remote-hosting-badge"><Icon name="monitor" size={13} />
          {entry.hostedBy === 'this PC' ? 'Hosting on this PC' : `Hosted by ${entry.hostedBy}`}</small>}
        {entry.multipleHosts && <small className="remote-hosting-badge">Multiple hosting PCs · review shared saves</small>}
        {guest && !entry.hostedBy && <small className="guest-hosting-badge">Hosted for {server.sourceHostName || 'a friend'}</small>}</span>
    </Button>
  }
  return <section className="server-directory" aria-label="Servers">
    <div className="server-directory-heading"><h2>Servers</h2><Button className="text-button icon-button" aria-label="Refresh server list" disabled={busy} onClick={onRetry}><Icon name="refresh" size={16} /></Button></div>
    <label className="server-directory-search"><span className="sr-only">Find a server or friend</span><Input type="search" placeholder="Find a server or friend" value={search} onChange={event => setSearch(event.target.value)} /></label>
    <div className="server-directory-actions"><Button className="secondary" data-guide="host-create" disabled={busy} onClick={onAddLocal}><Icon name="plus" size={15} />New server</Button>
      <Button className="secondary" disabled={busy} onClick={onConnect}><Icon name="link" size={15} />Connect</Button></div>
    {error && <p className="helper-text" role="status">The full server list is unavailable. <Button className="text-button" onClick={onRetry}>Retry</Button></p>}
    <div className="server-directory-group"><h3>Your servers</h3>{own.map(row)}
      {!own.length && <p className="server-directory-empty">{query ? 'No matching servers.' : 'Create a server on this PC.'}</p>}</div>
    {hostedForFriends.length > 0 && <div className="server-directory-group"><h3>Hosting for friends</h3>
      {hostedForFriends.map(row)}</div>}
    <div className="server-directory-group"><h3>Friend servers</h3>{friends.map(host => <div className="server-directory-host" key={host.connectionId}>
      <h4>{host.hostProfileName || host.connectionName}{removed(host) && <small>Access removed</small>}</h4>
      {host.entries.map(row)}
      {!host.entries.length && <Button className="server-directory-row" disabled={busy} onClick={() => onSelect({ kind: 'friend', connectionId: host.connectionId, profileId: '' })}>
        Open {host.connectionName}<small>{host.state} · No assigned servers</small></Button>}</div>)}
      {!friends.length && <p className="server-directory-empty">{query ? 'No matching friends.' : 'Connect using a friend’s server code.'}</p>}</div>
    {view.friendHosts.some(removed) && <label className="server-directory-removed"><Input type="checkbox" checked={showRemoved} onChange={event => setShowRemoved(event.target.checked)} /><span>Show removed access</span></label>}
  </section>
}
