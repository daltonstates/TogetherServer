import { Button, Input } from './Controls'
import type { FriendSnapshot } from './contracts'
import { Icon } from './Icon'
import { isRemovedHost, orderHostConnections, orderProfiles } from './profileLists'
import { SearchablePicker } from './SearchablePicker'

export function FriendHostPicker({ connections, selectedId, showRemoved, favorites, busy,
  onShowRemoved, onSelect, onFavorite, onForget }: {
  connections: readonly FriendSnapshot[]; selectedId: string; showRemoved: boolean; favorites: readonly string[]; busy: boolean
  onShowRemoved: (show: boolean) => void; onSelect: (id: string) => void
  onFavorite: (id: string) => void; onForget: (id: string) => void
}) {
  const visible = orderHostConnections(connections, showRemoved, favorites)
  return <SearchablePicker label="Selected Host" selectedId={selectedId}
    items={visible.map(connection => ({ id: connection.connectionId,
      name: connection.hostProfileName?.trim() || connection.connectionName || connection.profiles[0]?.name || 'Saved Host',
      detail: [connection.hostProfileName?.trim() && connection.connectionName ? `Saved as: ${connection.connectionName}` : '',
        isRemovedHost(connection) ? '' : orderProfiles(connection.profiles).map(profile => profile.name).join(', ')].filter(Boolean).join(' / '),
      state: isRemovedHost(connection) ? 'Access removed' : connection.state }))}
    onSelect={onSelect}
    tools={<label><Input type="checkbox" checked={showRemoved} onChange={event => onShowRemoved(event.target.checked)} />Show removed Hosts</label>}
    actions={item => isRemovedHost(visible.find(connection => connection.connectionId === item.id)!)
      ? <Button className="danger-outline icon-button" disabled={busy} aria-label={`Delete saved Host ${item.name}`}
        title="Delete saved Host" onClick={() => onForget(item.id)}><Icon name="trash" /></Button>
      : <Button className="secondary icon-button" aria-pressed={favorites.includes(item.id)}
        aria-label={`${favorites.includes(item.id) ? 'Unpin' : 'Pin'} saved Host`} title={`${favorites.includes(item.id) ? 'Unpin' : 'Pin'} saved Host`}
        onClick={() => onFavorite(item.id)}>{favorites.includes(item.id) ? '★' : '☆'}</Button>} />
}
