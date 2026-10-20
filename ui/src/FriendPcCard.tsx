import { useId, type ReactNode } from 'react'
import { Button } from './Controls'
import { Icon, type IconName } from './Icon'
import type { Device } from './contracts'
import { effectiveDeviceAccess, type FriendAccessServer } from './FriendAccessSummary'
import { devicePasswordPermission, matchingPermissionPreset, permissionPresets, type PermissionValues } from './permissionState'
import './friend-access.css'

export type FriendPcCardProps = {
  device: Device
  servers: readonly FriendAccessServer[]
  nowMs: number
  busy: boolean
  onManage: () => void
  onApprove: () => void
  onDelete?: () => void
  feedback?: ReactNode
}

const grants: { label: string; icon: IconName; password?: boolean; allowed: (permissions: PermissionValues) => boolean }[] = [
  { label: 'Start', icon: 'play', allowed: permissions => permissions.canStart },
  { label: 'Stop when empty', icon: 'stop', allowed: permissions => permissions.canStop },
  { label: 'Restart', icon: 'refresh', allowed: permissions => permissions.canStart && permissions.canStop },
  { label: 'Add time', icon: 'clock', allowed: permissions => permissions.canExtendTimer },
  { label: 'View logs', icon: 'logs', allowed: permissions => permissions.canViewLogs },
  { label: 'Game password', icon: 'shield', password: true, allowed: () => false }
]

function localTime(value: string | null | undefined) {
  const timestamp = value ? Date.parse(value) : NaN
  return Number.isFinite(timestamp) ? new Date(timestamp).toLocaleString() : 'Time unavailable'
}

function heartbeatAge(value: string | null, nowMs: number) {
  if (!value) return 'Not connected yet'
  const heartbeat = Date.parse(value)
  if (!Number.isFinite(heartbeat) || !Number.isFinite(nowMs) || heartbeat > nowMs) return 'Last connection time unavailable'
  const seconds = Math.floor((nowMs - heartbeat) / 1000)
  if (seconds < 60) return `Last connected ${seconds}s ago`
  if (seconds < 3600) return `Last connected ${Math.floor(seconds / 60)}m ago`
  if (seconds < 86_400) return `Last connected ${Math.floor(seconds / 3600)}h ago`
  return `Last connected ${Math.floor(seconds / 86_400)}d ago`
}

export function FriendPcCard({ device, servers, nowMs, busy, onManage, onApprove, onDelete, feedback }: FriendPcCardProps) {
  const headingId = useId()
  const view = effectiveDeviceAccess(device, servers, nowMs)
  if (device.revoked) return <article className="friend-pc-card" data-access-state="Revoked" aria-labelledby={headingId}>
    <header className="friend-pc-card-heading">
      <div className="friend-pc-identity"><span className="friend-pc-icon"><Icon name="monitor" size={22} /></span>
        <div><h3 id={headingId}>{device.profileName?.trim() || device.name}</h3><span className="friend-pc-kind">{device.profileName?.trim() ? `PC: ${device.name}` : 'Friend PC'}</span></div></div>
      <span className="friend-access-badge friend-pc-access-state">Access removed</span>
    </header>
    <p className="friend-pc-quiet">This PC no longer has access. Delete its saved record to remove it from this list.</p>
    <ul className="friend-grant-chips" aria-label="Unavailable permissions">
      {grants.map(grant => <li key={grant.label} data-permission-state="unavailable" aria-label={`${grant.label}: unavailable, access removed`}>
        <Icon name={grant.icon} size={14} />{grant.label}
      </li>)}
    </ul>
    {onDelete && <footer className="friend-pc-card-actions"><Button className="danger-outline" disabled={busy} onClick={onDelete}><Icon name="trash" />Delete PC</Button></footer>}
    {feedback && <div className="friend-pc-feedback">{feedback}</div>}
  </article>
  const preset = matchingPermissionPreset(device)
  const configured = view.state !== 'Available' && view.helper === 'Inactive'
  const permissionsKnown = view.state !== 'Unknown' && view.state !== 'RefreshNeeded' && view.helper !== 'RefreshNeeded' &&
    (view.helper !== 'Active' || view.state === 'Available')
  const counts = grants.map(grant => ({ ...grant, count: view.rows.filter(row => {
    if (grant.password) return servers.find(server => server.id === row.profileId)?.kind === 'Valheim' &&
      devicePasswordPermission(device, row.profileId) && (configured || view.state === 'Available')
    const permissions = configured ? row.observedPermissions : row.effectivePermissions
    return permissions !== null && grant.allowed(permissions)
  }).length }))
  const permissionLabel = view.helper === 'Active' ? 'Temporary helper' :
    preset === 'custom' ? 'Custom permissions' : permissionPresets[preset].label
  const hasOwnerDeadline = device.accessExpiresUtc !== null || device.accessExpired
  const exceptions = view.rows.filter(row => row.exception).length
  const shownServers = view.rows.slice(0, 2)

  return <article className="friend-pc-card" data-access-state={view.state} aria-labelledby={headingId}>
    <header className="friend-pc-card-heading">
      <div className="friend-pc-identity"><span className="friend-pc-icon"><Icon name="monitor" size={22} /></span>
        <div><h3 id={headingId}>{device.profileName?.trim() || device.name}</h3><span className="friend-pc-kind">{device.profileName?.trim() ? `PC: ${device.name}` : 'Friend PC'}</span></div></div>
      <span className="friend-access-badge friend-pc-access-state"><Icon name={view.state === 'Available' ? 'shield' : 'warning'} size={14} />{view.label}</span>
    </header>

    <div className="friend-pc-heartbeat"><span><Icon name={view.connected ? 'plug' : 'warning'} size={15} />
      {view.connected ? 'Recently connected' : 'Connection unavailable'}</span>
      <small title={device.lastHeartbeatUtc ? localTime(device.lastHeartbeatUtc) : undefined}>{heartbeatAge(device.lastHeartbeatUtc, nowMs)}</small></div>

    <div className="friend-pc-servers" role="group" aria-label="Assigned servers">
      <span className="friend-pc-field-label"><Icon name="server" size={15} />{view.rows.length} assigned {view.rows.length === 1 ? 'server' : 'servers'}</span>
      {view.rows.length > 0 ? <ul className="friend-server-tags">
        {shownServers.map(server => <li key={server.profileId} title={server.name}>{server.name}</li>)}
        {view.rows.length > shownServers.length && <li className="friend-more-servers" aria-label={`${view.rows.length - shownServers.length} more assigned servers. Manage access to see all.`}>
          +{view.rows.length - shownServers.length} more</li>}
      </ul> : <p className="friend-pc-quiet">No server access. Assign a server in Manage access.</p>}
    </div>

    <div className="friend-pc-permissions" role="group" aria-label={view.rows.length === 0 ? 'Permissions with no assigned servers' : configured ? 'Configured permissions' : 'Current permissions'}>
      <div className="friend-pc-permission-heading"><span className="friend-pc-field-label">{configured ? 'Configured access' : 'Permissions'}</span>
        {permissionsKnown && view.rows.length > 0 && <strong>{permissionLabel}</strong>}</div>
      {permissionsKnown ? <>
        <ul className="friend-grant-chips" aria-label={configured ? 'Saved permissions by server' : 'Permissions by server'}>
          {counts.map(grant => <li key={grant.label}
            data-permission-state={grant.count === 0 ? 'unassigned' : configured ? 'configured' : 'granted'}
            aria-label={view.rows.length === 0 ? `${grant.label}: no assigned servers` : `${grant.label}: ${grant.count} of ${view.rows.length} assigned servers${configured ? ', configured' : ''}`}>
            <Icon name={grant.icon} size={14} />{grant.label}{grant.count < view.rows.length && <span className="friend-grant-count">{grant.count}/{view.rows.length}</span>}
          </li>)}
        </ul>
        {view.rows.length > 0 && counts.every(grant => grant.count === 0) && <p className="friend-pc-quiet">Status and connection details only.</p>}
        {exceptions > 0 && <small className="friend-pc-quiet">{exceptions} {exceptions === 1 ? 'server has' : 'servers have'} custom permissions.</small>}
      </> : <><ul className="friend-grant-chips" aria-label="Permissions need refresh">
        {grants.map(grant => <li key={grant.label} data-permission-state="unknown" aria-label={`${grant.label}: unavailable until access is refreshed`}>
          <Icon name={grant.icon} size={14} />{grant.label}
        </li>)}
      </ul><p className="friend-pc-refresh-note"><Icon name="refresh" size={15} />
        <span>{view.helper === 'RefreshNeeded' ? 'Refresh to see saved permissions after temporary access ends.' :
          view.helper === 'Active' ? 'Temporary access is blocked. Saved permissions are hidden until it ends.' : 'Refresh to check current access.'}</span></p></>}
      {configured && <p className="friend-pc-quiet">Actions and logs are currently blocked.</p>}
    </div>

    {view.helper === 'Active' && <p className="friend-pc-helper"><Icon name="clock" size={15} />
      <span>Temporary helper {view.state === 'Available' ? 'ends' : 'recorded until'} {localTime(device.temporaryHelperUntilUtc)}.
        {view.state === 'Available' && ' Saved permissions are hidden while it is active.'}</span></p>}
    {hasOwnerDeadline && <p className="friend-pc-deadline"><Icon name="clock" size={15} /><span>{view.accessDeadline}</span></p>}

    <footer className="friend-pc-card-actions">
      <Button className="secondary" disabled={busy || device.revoked} onClick={onManage}><Icon name="settings" />Manage access<Icon name="chevronRight" size={15} /></Button>
      {view.state === 'ApprovalPending' && <Button disabled={busy} onClick={onApprove}><Icon name="check" />Approve this PC</Button>}
    </footer>
    {feedback && <div className="friend-pc-feedback">{feedback}</div>}
    <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">
      {view.label}. Connection {view.connected ? 'recently active' : 'unavailable'}. {view.rows.length} servers assigned.
      {view.rows.length > 0 && permissionsKnown ? ` ${configured ? 'Configured' : 'Granted'} permissions. ${counts.map(grant => `${grant.label}: ${grant.count}/${view.rows.length}`).join('. ')}.` : ''}
      {view.helper === 'RefreshNeeded' ? ' Refresh to see saved permissions.' : ''}
      {hasOwnerDeadline ? ` ${view.accessDeadline}.` : ''}
    </span>
  </article>
}
