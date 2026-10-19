import { useState } from 'react'
import type { Decoder, Snapshot } from './contracts'
import { Button, Input } from './Controls'
import { Icon } from './Icon'
import { gameLabel } from './GameProfile'
import './ServerDirectory.css'

export type DirectoryServer = {
  id: string; name: string; kind: string; state: string; onlinePlayers: number | null; maxPlayers: number | null
  playerCountTrusted: boolean; playerObservationSource: string | null; playerCountObservedUtc: string | null
  playerCountRefreshing: boolean; playerCountFailureCount: number
  separateCopySourceProfileId: string | null; sourceName: string | null; sourceHostName: string | null
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
  return { id: identifier(item.id)!, name: text(item.name)!, kind: text(item.kind)!, state: text(item.state)!,
    onlinePlayers: count(item.onlinePlayers), maxPlayers: count(item.maxPlayers), playerCountTrusted: flag(item.playerCountTrusted),
    playerObservationSource: text(item.playerObservationSource, true), playerCountObservedUtc: timestamp(item.playerCountObservedUtc),
    playerCountRefreshing: flag(item.playerCountRefreshing), playerCountFailureCount: count(item.playerCountFailureCount) ?? 0,
    separateCopySourceProfileId: identifier(item.separateCopySourceProfileId, true), sourceName: text(item.sourceName, true),
    sourceHostName: text(item.sourceHostName, true) }
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
    return { ...blankCount, id: profile.id, name: profile.name, kind: profile.kind, state: run?.state ?? 'Unknown',
      onlinePlayers: run?.onlinePlayers ?? null, maxPlayers: run?.maxPlayers ?? null, playerCountTrusted: run?.playerCountTrusted ?? false,
      playerObservationSource: run?.playerObservationSource ?? null, playerCountObservedUtc: run?.playerCountObservedUtc ?? null,
      playerCountRefreshing: run?.playerCountRefreshing ?? false, playerCountFailureCount: run?.playerCountFailureCount ?? 0,
      separateCopySourceProfileId: profile.separateCopySourceProfileId ?? null,
      sourceName: prior?.sourceName ?? source?.name ?? null,
      sourceHostName: prior?.sourceHostName ?? sourceHost?.hostProfileName ?? sourceHost?.connectionName ?? null }
  }) }
  const connections = snapshot.connections?.length ? snapshot.connections : snapshot.connectionId ? [snapshot] : []
  const hosts = connections.map(host => ({ connectionId: host.connectionId, hostId: host.hostId || null,
    connectionName: host.connectionName || 'Saved friend', hostProfileName: host.hostProfileName ?? null,
    state: host.state, connectionCode: host.connectionCode ?? null, lastConnectedUtc: host.lastConnectedUtc,
    servers: !host.profiles.length && ['Disconnected', 'Disconnected/Unknown'].includes(host.state)
      ? (view.friendHosts.find(prior => prior.connectionId === host.connectionId)?.servers ?? []).map(server => ({ ...server,
        state: 'Unknown', onlinePlayers: null, maxPlayers: null, playerCountTrusted: false, playerCountObservedUtc: null }))
      : host.profiles.map(profile => ({ ...blankCount, id: profile.id, name: profile.name, kind: profile.kind, state: profile.state,
      onlinePlayers: profile.onlinePlayers, maxPlayers: profile.maxPlayers, playerCountTrusted: profile.playerCountTrusted ?? false,
      playerObservationSource: profile.playerObservationSource ?? null, playerCountObservedUtc: profile.playerCountObservedUtc ?? null,
      playerCountRefreshing: profile.playerCountRefreshing ?? false, playerCountFailureCount: profile.playerCountFailureCount ?? 0 })) }))
  return { ...view, friendHosts: snapshot.connections !== null ? hosts
    : [...view.friendHosts.filter(host => !hosts.some(next => next.connectionId === host.connectionId)), ...hosts] }
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
  return server.playerCountFailureCount >= 2 ? 'Player count unavailable' : 'Checking players…'
}

export function ServerDirectory({ view, selection, nowMs, busy, error, onRetry, onSelect, onAddLocal, onConnect }: {
  view: ServerDirectoryView; selection: ServerSelection | null; nowMs: number; busy: boolean; error?: string
  onRetry: () => void; onSelect: (selection: ServerSelection) => void; onAddLocal: () => void; onConnect: () => void
}) {
  const [search, setSearch] = useState('')
  const [showRemoved, setShowRemoved] = useState(false)
  const query = search.trim().toLocaleLowerCase()
  const matches = (row: DirectoryServer, owner = '') => `${row.name} ${gameLabel(row.kind)} ${row.sourceName ?? ''} ${row.sourceHostName ?? ''} ${owner}`.toLocaleLowerCase().includes(query)
  const local = view.localServers.filter(row => matches(row))
  const removed = (host: DirectoryHost) => host.connectionCode === 'Revoked' || host.state === 'Revoked'
  const friends = view.friendHosts.filter(host => showRemoved || !removed(host)).map(host => ({ ...host,
    servers: host.servers.filter(row => matches(row, `${host.connectionName} ${host.hostProfileName ?? ''}`)) }))
    .filter(host => host.servers.length || !query || `${host.connectionName} ${host.hostProfileName ?? ''}`.toLocaleLowerCase().includes(query))
  const row = (server: DirectoryServer, target: ServerSelection, host?: DirectoryHost) => {
    const selected = selection?.kind === target.kind && selection.profileId === server.id &&
      (target.kind === 'local' || selection.kind === 'friend' && selection.connectionId === target.connectionId)
    const owner = host ? host.hostProfileName || host.connectionName : 'this PC'
    const connected = !host || ['Connected', 'Disabled'].includes(host.state) && !!host.lastConnectedUtc && nowMs - Date.parse(host.lastConnectedUtc) <= 15_000
    return <Button className={`server-directory-row${selected ? ' selected' : ''}`} key={`${target.kind}-${host?.connectionId ?? ''}-${server.id}`}
      aria-label={`Open ${server.name} on ${owner}`} aria-current={selected ? 'true' : undefined} disabled={busy}
      onClick={() => onSelect(target)}>
      <Icon name={server.separateCopySourceProfileId ? 'users' : host ? 'link' : 'server'} size={17} />
      <span><strong>{server.name}</strong><small>{gameLabel(server.kind)} · {connected ? playerLabel(server, nowMs) : 'Host offline · status unavailable'}</small>
        {server.separateCopySourceProfileId && <small className="guest-hosting-badge">Hosted for {server.sourceHostName || 'a friend'}</small>}</span>
    </Button>
  }
  return <section className="server-directory" aria-label="Servers">
    <div className="server-directory-heading"><h2>Servers</h2><Button className="text-button icon-button" aria-label="Refresh server list" disabled={busy} onClick={onRetry}><Icon name="refresh" size={16} /></Button></div>
    <label className="server-directory-search"><span className="sr-only">Find a server or friend</span><Input type="search" placeholder="Find a server or friend" value={search} onChange={event => setSearch(event.target.value)} /></label>
    <div className="server-directory-actions"><Button className="secondary" disabled={busy} onClick={onAddLocal}><Icon name="plus" size={15} />New server</Button>
      <Button className="secondary" disabled={busy} onClick={onConnect}><Icon name="link" size={15} />Connect</Button></div>
    {error && <p className="helper-text" role="status">The full server list is unavailable. <button className="text-button" onClick={onRetry}>Retry</button></p>}
    <div className="server-directory-group"><h3>Your servers</h3>{local.filter(server => !server.separateCopySourceProfileId).map(server => row(server, { kind: 'local', profileId: server.id }))}
      {!local.some(server => !server.separateCopySourceProfileId) && <p className="server-directory-empty">{query ? 'No matching servers.' : 'Create a server on this PC.'}</p>}</div>
    {local.some(server => !!server.separateCopySourceProfileId) && <div className="server-directory-group"><h3>Hosting for friends</h3>
      {local.filter(server => !!server.separateCopySourceProfileId).map(server => row(server, { kind: 'local', profileId: server.id }))}</div>}
    <div className="server-directory-group"><h3>Friend servers</h3>{friends.map(host => <div className="server-directory-host" key={host.connectionId}>
      <h4>{host.hostProfileName || host.connectionName}{removed(host) && <small>Access removed</small>}</h4>
      {host.servers.map(server => row(server, { kind: 'friend', connectionId: host.connectionId, profileId: server.id }, host))}
      {!host.servers.length && <Button className="server-directory-row" disabled={busy} onClick={() => onSelect({ kind: 'friend', connectionId: host.connectionId, profileId: '' })}>
        Open {host.connectionName}<small>{host.state} · No assigned servers</small></Button>}</div>)}
      {!friends.length && <p className="server-directory-empty">{query ? 'No matching friends.' : 'Connect using a friend’s server code.'}</p>}</div>
    {view.friendHosts.some(removed) && <label className="server-directory-removed"><input type="checkbox" checked={showRemoved} onChange={event => setShowRemoved(event.target.checked)} />Show removed access</label>}
  </section>
}
