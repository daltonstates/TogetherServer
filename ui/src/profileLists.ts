import type { FriendSnapshot } from './contracts'

export function isActiveProfile(state: string | undefined): boolean {
  return ['Ready', 'Starting', 'Stopping', 'Listening', 'Process running', 'World copy in progress'].includes(state ?? '')
}

// Ordering is a view preference. It never changes settings, selection or authorization.
export function orderProfiles<T extends { id: string; state?: string }>(profiles: readonly T[],
  runs: readonly { profileId: string; state: string }[] = [],
  favorites: readonly string[] = [], preferredOrder: readonly string[] = []): T[] {
  const states = new Map(runs.map(run => [run.profileId, run.state]))
  return profiles.map((profile, index) => ({ profile, index })).sort((a, b) =>
    Number(isActiveProfile(states.get(b.profile.id) ?? b.profile.state)) - Number(isActiveProfile(states.get(a.profile.id) ?? a.profile.state)) ||
    Number(favorites.includes(b.profile.id)) - Number(favorites.includes(a.profile.id)) ||
    (preferredOrder.includes(a.profile.id) ? preferredOrder.indexOf(a.profile.id) : preferredOrder.length + a.index) -
    (preferredOrder.includes(b.profile.id) ? preferredOrder.indexOf(b.profile.id) : preferredOrder.length + b.index) ||
    a.index - b.index).map(item => item.profile)
}

export function isRemovedHost(connection: Pick<FriendSnapshot, 'state' | 'connectionCode'>): boolean {
  return connection.state === 'Revoked' || connection.connectionCode === 'Revoked'
}

export function orderHostConnections(connections: readonly FriendSnapshot[], showRemoved = false,
  favorites: readonly string[] = []): FriendSnapshot[] {
  const rank = (connection: FriendSnapshot) => isRemovedHost(connection) ? 2 :
    ['Connected', 'Disabled'].includes(connection.state) ? 0 : 1
  return connections.filter(connection => showRemoved || !isRemovedHost(connection))
    .sort((a, b) => rank(a) - rank(b) ||
      Number(favorites.includes(b.connectionId)) - Number(favorites.includes(a.connectionId)))
}
