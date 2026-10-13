import { useId, type ReactNode } from 'react'
import { Button } from './Controls'
import { Icon, type IconName } from './Icon'
import type { Device } from './contracts'
import { effectiveDeviceAccess, type FriendAccessServer } from './FriendAccessSummary'
import { matchingPermissionPreset, permissionPresets, type PermissionValues } from './permissionState'
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

const grants: { label: string; icon: IconName; allowed: (permissions: PermissionValues) => boolean }[] = [
  { label: 'Start', icon: 'play', allowed: permissions => permissions.canStart },
  { label: 'Guarded Stop', icon: 'stop', allowed: permissions => permissions.canStop },
  { label: 'Restart', icon: 'refresh', allowed: permissions => permissions.canStart && permissions.canStop },
  { label: 'Add time', icon: 'clock', allowed: permissions => permissions.canExtendTimer },
  { label: 'View logs', icon: 'logs', allowed: permissions => permissions.canViewLogs }
]

function localTime(value: string | null | undefined) {
  const timestamp = value ? Date.parse(value) : NaN
  return Number.isFinite(timestamp) ? new Date(timestamp).toLocaleString() : 'Time unavailable'
}

function heartbeatAge(value: string | null, nowMs: number) {
  if (!value) return 'No heartbeat recorded'
  const heartbeat = Date.parse(value)
  if (!Number.isFinite(heartbeat) || !Number.isFinite(nowMs) || heartbeat > nowMs) return 'Heartbeat time unavailable'
  const seconds = Math.floor((nowMs - heartbeat) / 1000)
  if (seconds < 60) return `Last heartbeat ${seconds}s ago`
  if (seconds < 3600) return `Last heartbeat ${Math.floor(seconds / 60)}m ago`
  if (seconds < 86_400) return `Last heartbeat ${Math.floor(seconds / 3600)}h ago`
  return `Last heartbeat ${Math.floor(seconds / 86_400)}d ago`
}

export function FriendPcCard({ device, servers, nowMs, busy, onManage, onApprove, onDelete, feedback }: FriendPcCardProps) {
  const headingId = useId()
  const view = effectiveDeviceAccess(device, servers, nowMs)
  if (device.revoked) return <article className="friend-pc-card" data-access-state="Revoked" aria-labelledby={headingId}>
    <header className="friend-pc-card-heading">
      <div className="friend-pc-identity"><span className="friend-pc-icon"><Icon name="monitor" size={22} /></span>
        <div><h3 id={headingId}>{device.name}</h3><span className="friend-pc-kind">Friend PC</span></div></div>
      <span className="friend-access-badge friend-pc-access-state">Access removed</span>
    </header>
    <p className="friend-pc-quiet">This PC no longer has access. Delete its saved record to remove it from this list.</p>
    {onDelete && <footer className="friend-pc-card-actions"><Button className="danger-outline" disabled={busy} onClick={onDelete}><Icon name="trash" />Delete PC</Button></footer>}
    {feedback && <div className="friend-pc-feedback">{feedback}</div>}
  </article>
  const preset = matchingPermissionPreset(device)
  const configured = view.state !== 'Available' && view.helper === 'Inactive'
  const permissionsKnown = view.state !== 'Unknown' && view.state !== 'RefreshNeeded' && view.helper !== 'RefreshNeeded' &&
    (view.helper !== 'Active' || view.state === 'Available')
  const counts = grants.map(grant => ({ ...grant, count: view.rows.filter(row => {
    const permissions = configured ? row.observedPermissions : row.effectivePermissions
    return permissions !== null && grant.allowed(permissions)
  }).length }))
  const allowed = counts.filter(grant => grant.count > 0)
  const permissionLabel = view.helper === 'Active' ? 'Temporary helper' :
    preset === 'custom' ? 'Custom permissions' : permissionPresets[preset].label
  const hasOwnerDeadline = device.accessExpiresUtc !== null || device.accessExpired
  const exceptions = view.rows.filter(row => row.exception).length
  const shownServers = view.rows.slice(0, 2)

  return <article className="friend-pc-card" data-access-state={view.state} aria-labelledby={headingId}>
    <header className="friend-pc-card-heading">
      <div className="friend-pc-identity"><span className="friend-pc-icon"><Icon name="monitor" size={22} /></span>
        <div><h3 id={headingId}>{device.name}</h3><span className="friend-pc-kind">Friend PC</span></div></div>
      <span className="friend-access-badge friend-pc-access-state"><Icon name={view.state === 'Available' ? 'shield' : 'warning'} size={14} />{view.label}</span>
    </header>

    <div className="friend-pc-heartbeat"><span><Icon name={view.connected ? 'plug' : 'warning'} size={15} />
      {view.connected ? 'Recent authenticated heartbeat' : 'Connection Unknown'}</span>
      <small title={device.lastHeartbeatUtc ? localTime(device.lastHeartbeatUtc) : undefined}>{heartbeatAge(device.lastHeartbeatUtc, nowMs)}</small></div>

    <div className="friend-pc-servers" role="group" aria-label="Assigned servers">
      <span className="friend-pc-field-label"><Icon name="server" size={15} />{view.rows.length} assigned {view.rows.length === 1 ? 'server' : 'servers'}</span>
      {view.rows.length > 0 ? <ul className="friend-server-tags">
        {shownServers.map(server => <li key={server.profileId} title={server.name}>{server.name}</li>)}
        {view.rows.length > shownServers.length && <li className="friend-more-servers" aria-label={`${view.rows.length - shownServers.length} more assigned servers. Manage access to see all.`}>
          +{view.rows.length - shownServers.length} more</li>}
      </ul> : <p className="friend-pc-quiet">No server access. Assign a server in Manage access.</p>}
    </div>

    {view.rows.length > 0 && <div className="friend-pc-permissions" role="group" aria-label={configured ? 'Configured permissions' : 'Current permissions'}>
      <div className="friend-pc-permission-heading"><span className="friend-pc-field-label">{configured ? 'Configured access' : 'Permissions'}</span>
        {permissionsKnown && <strong>{permissionLabel}</strong>}</div>
      {permissionsKnown ? <>
        {allowed.length > 0 ? <ul className="friend-grant-chips" aria-label={configured ? 'Configured grants by assigned server' : 'Grants by assigned server'}>
          {allowed.map(grant => <li key={grant.label} aria-label={`${grant.label}: ${grant.count} of ${view.rows.length} assigned servers${configured ? ', configured' : ''}`}>
            <Icon name={grant.icon} size={14} />{grant.label}{grant.count < view.rows.length && <span className="friend-grant-count">{grant.count}/{view.rows.length}</span>}
          </li>)}
        </ul> : preset !== 'status' && <p className="friend-pc-quiet">Status and connection details only.</p>}
        {exceptions > 0 && <small className="friend-pc-quiet">{exceptions} {exceptions === 1 ? 'server has' : 'servers have'} custom permissions.</small>}
      </> : <p className="friend-pc-refresh-note"><Icon name="refresh" size={15} />
        <span>{view.helper === 'RefreshNeeded' ? 'Refresh from the Host to see saved permissions. Expired helper grants cannot reveal them.' :
          view.helper === 'Active' ? 'Helper grants are blocked for this PC. Saved permissions are unavailable during the overlay.' : 'Refresh from the Host to verify current access.'}</span></p>}
      {configured && <p className="friend-pc-quiet">Actions and logs are currently blocked.</p>}
    </div>}

    {view.helper === 'Active' && <p className="friend-pc-helper"><Icon name="clock" size={15} />
      <span>Temporary helper {view.state === 'Available' ? 'ends' : 'recorded until'} {localTime(device.temporaryHelperUntilUtc)}.
        {view.state === 'Available' && ' Saved permissions are hidden while it is active.'}</span></p>}
    {view.helper === 'RefreshNeeded' && view.rows.length === 0 && <p className="friend-pc-refresh-note"><Icon name="refresh" size={15} />
      <span>Refresh from the Host after temporary helper access. Saved permissions cannot be inferred.</span></p>}
    {hasOwnerDeadline && <p className="friend-pc-deadline"><Icon name="clock" size={15} /><span>{view.accessDeadline}</span></p>}

    <footer className="friend-pc-card-actions">
      <Button className="secondary" disabled={busy || device.revoked} onClick={onManage}><Icon name="settings" />Manage access<Icon name="chevronRight" size={15} /></Button>
      {view.state === 'ApprovalPending' && <Button disabled={busy} onClick={onApprove}><Icon name="check" />Approve this PC</Button>}
    </footer>
    {feedback && <div className="friend-pc-feedback">{feedback}</div>}
    <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">
      {view.label}. Connection {view.connected ? 'has a recent authenticated heartbeat' : 'Unknown'}. {view.rows.length} servers assigned.
      {view.rows.length > 0 && permissionsKnown ? ` ${configured ? 'Configured' : 'Granted'} permissions. ${counts.map(grant => `${grant.label}: ${grant.count}/${view.rows.length}`).join('. ')}.` : ''}
      {view.helper === 'RefreshNeeded' ? ' Refresh needed; saved permissions cannot be inferred.' : ''}
      {hasOwnerDeadline ? ` ${view.accessDeadline}.` : ''}
    </span>
  </article>
}
