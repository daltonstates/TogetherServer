import { useId } from 'react'
import { Input, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { Icon, type IconName } from './Icon'
import type { Device, ServerPermission } from './contracts'
import { devicePermission, type PermissionValues } from './permissionState'
import './friend-access.css'

export type FriendAccessServer = { id: string; name: string; kind?: string }
export type FriendAccessState = 'Available' | 'Revoked' | 'Unpaired' | 'ApprovalPending' |
  'AccessExpired' | 'CredentialExpired' | 'Unknown' | 'RefreshNeeded'
export type EffectiveServerAccess = {
  profileId: string
  name: string
  observedPermissions: PermissionValues
  effectivePermissions: PermissionValues | null
  exception: boolean
}
export type FriendAccessView = {
  state: FriendAccessState
  label: string
  helper: 'Active' | 'Inactive' | 'RefreshNeeded'
  connected: boolean
  accessDeadline: string
  credentialDeadline: string
  rows: EffectiveServerAccess[]
}

const actions = ['canStart', 'canStop', 'canExtendTimer', 'canViewLogs'] as const
const noActions: PermissionValues = { canStart: false, canStop: false, canExtendTimer: false, canViewLogs: false }
function permissionValues(source: PermissionValues): PermissionValues {
  return { canStart: source.canStart, canStop: source.canStop,
    canExtendTimer: source.canExtendTimer, canViewLogs: source.canViewLogs }
}
function deadlineMs(value: string | null | undefined) {
  return value ? Date.parse(value) : NaN
}
function dateLabel(value: string | null | undefined) {
  const timestamp = deadlineMs(value)
  return Number.isFinite(timestamp) ? new Date(timestamp).toLocaleString() : 'Time unavailable'
}

export function permissionSummary(permissions: PermissionValues): string {
  const allowed = [permissions.canStart && 'Start', permissions.canStop && 'guarded Stop',
    permissions.canStart && permissions.canStop && 'Restart',
    permissions.canExtendTimer && 'add shutdown time', permissions.canViewLogs && 'view logs'].filter(Boolean)
  return allowed.length ? allowed.join(' · ') : 'Status and connection details only'
}

export function effectiveDeviceAccess(device: Device, servers: readonly FriendAccessServer[], nowMs: number): FriendAccessView {
  const accessUntil = deadlineMs(device.accessExpiresUtc)
  const credentialUntil = deadlineMs(device.credentialExpiresUtc)
  const helperUntil = deadlineMs(device.temporaryHelperUntilUtc)
  // DeviceView already projects the helper overlay into all four permissions.
  // Once that projection expires, the saved grants cannot be reconstructed here.
  const helper = device.temporaryHelperActive
    ? Number.isFinite(nowMs) && Number.isFinite(helperUntil) && helperUntil > nowMs ? 'Active' : 'RefreshNeeded'
    : 'Inactive'
  let state: FriendAccessState = 'Available'
  let label = 'Access active'
  if (device.revoked) { state = 'Revoked'; label = 'Access removed' }
  else if (!device.paired) { state = 'Unpaired'; label = 'Not paired' }
  else if (device.approvalPending) { state = 'ApprovalPending'; label = 'Waiting for owner approval' }
  else if (device.accessExpired || (Number.isFinite(accessUntil) && accessUntil <= nowMs)) {
    state = 'AccessExpired'; label = 'Owner access deadline passed'
  } else if (Number.isFinite(credentialUntil) && credentialUntil <= nowMs) {
    state = 'CredentialExpired'; label = 'Renewable credential expired'
  } else if (!Number.isFinite(nowMs) || (device.accessExpiresUtc !== null && !Number.isFinite(accessUntil)) ||
      (device.credentialExpiresUtc !== null && !Number.isFinite(credentialUntil))) {
    state = 'Unknown'; label = 'Access expiry could not be verified'
  } else if (helper === 'RefreshNeeded') {
    state = 'RefreshNeeded'; label = 'Refresh needed after temporary helper access'
  }
  const heartbeat = deadlineMs(device.lastHeartbeatUtc)
  const rows = [...new Set(device.assignedProfileIds)].map(profileId => {
    const observedPermissions = permissionValues(devicePermission(device, profileId))
    return {
      profileId, name: servers.find(server => server.id === profileId)?.name ?? 'Unavailable saved server',
      observedPermissions,
      effectivePermissions: state === 'Available' ? observedPermissions :
        state === 'RefreshNeeded' || state === 'Unknown' ? null : { ...noActions },
      exception: helper === 'Inactive' && actions.some(action => observedPermissions[action] !== device[action])
    }
  })
  return {
    state, label, helper, rows,
    connected: Number.isFinite(heartbeat) && heartbeat <= nowMs && nowMs - heartbeat <= 45_000,
    accessDeadline: device.accessExpiresUtc === null
      ? device.accessExpired ? 'Owner access ended; deadline time unavailable' : 'No owner access deadline' :
      `${device.accessExpired || accessUntil <= nowMs ? 'Owner access ended' : 'Owner access ends'}: ${dateLabel(device.accessExpiresUtc)}`,
    credentialDeadline: device.credentialExpiresUtc === null ? 'Renewable credential expiry unavailable' :
      `${credentialUntil <= nowMs ? 'Renewable credential expired' : 'Renewable credential expires'}: ${dateLabel(device.credentialExpiresUtc)}`
  }
}

export type FriendAccessSummaryProps = { device: Device; servers: readonly FriendAccessServer[]; nowMs: number }

export function FriendAccessSummary({ device, servers, nowMs }: FriendAccessSummaryProps) {
  const headingId = useId()
  const view = effectiveDeviceAccess(device, servers, nowMs)
  const exceptions = view.rows.filter(row => row.exception).length
  const grants = view.state === 'Available' && view.rows.length > 0
    ? `Start: ${view.rows.filter(row => row.effectivePermissions?.canStart).length}/${view.rows.length} · Guarded Stop: ${view.rows.filter(row => row.effectivePermissions?.canStop).length}/${view.rows.length} · Restart: ${view.rows.filter(row => row.effectivePermissions?.canStart && row.effectivePermissions.canStop).length}/${view.rows.length} · Add time: ${view.rows.filter(row => row.effectivePermissions?.canExtendTimer).length}/${view.rows.length} · Logs: ${view.rows.filter(row => row.effectivePermissions?.canViewLogs).length}/${view.rows.length}`
    : ''
  return <section className="players-workspace friend-access-summary" aria-labelledby={headingId}>
    <div className="players-heading"><div><h3 id={headingId}>{device.name} · Access summary</h3>
      <p><strong>{view.label}</strong> · {view.connected ? 'Recent authenticated heartbeat' : 'Connection Unknown'}</p></div>
      <span>{view.rows.length} assigned {view.rows.length === 1 ? 'server' : 'servers'}</span></div>
    <p className="helper-text">{view.accessDeadline}. {view.credentialDeadline}.</p>
    {grants && <p className="helper-text">{grants}</p>}
    {view.helper === 'Active' && <p className="helper-text">{view.state === 'Available'
      ? `Temporary helper access is active until ${dateLabel(device.temporaryHelperUntilUtc)} on existing assignments.`
      : `A temporary helper overlay is recorded until ${dateLabel(device.temporaryHelperUntilUtc)}; device access currently blocks its use.`} These are the Host’s projected grants; saved permissions and exceptions are unavailable during the overlay.</p>}
    {view.helper === 'RefreshNeeded' && <DismissibleNotice as="p" scope={device.id} noticeKey={device.temporaryHelperUntilUtc ?? undefined} className="warning-text">The last snapshot included temporary helper grants. Refresh from the Host to see the saved permissions now; this view cannot infer them.</DismissibleNotice>}
    {view.helper === 'Inactive' && exceptions > 0 && <small>{exceptions} assigned {exceptions === 1 ? 'server has' : 'servers have'} permission exceptions.</small>}
    {view.rows.length === 0 ? <p className="helper-text">No servers assigned. Device-wide permissions do not grant access to an unassigned server.</p> :
      <details className="advanced-block"><summary>Effective access by server</summary>
        <ul>{view.rows.map(row => <li key={row.profileId}><strong>{row.name}</strong>
          <p>{view.state === 'Available' && row.effectivePermissions ? permissionSummary(row.effectivePermissions) : 'Actions and logs unavailable for this PC.'}</p>
          {row.exception && <small>Per-server exception to the device defaults.</small>}
          {view.state !== 'Available' && view.helper === 'Inactive' && <small>Configured grants: {permissionSummary(row.observedPermissions)}.</small>}
        </li>)}</ul>
      </details>}
    <small className="evidence-boundary">Stop and Restart still need a fresh zero-player check. Maintenance and paused remote controls block lifecycle actions; an independent log grant remains read-only. Add time uses the Host’s fixed increment and run limit.</small>
    <span className="sr-only" role="status" aria-live="polite" aria-atomic="true">{view.label}. Connection {view.connected ? 'has a recent authenticated heartbeat' : 'Unknown'}. {view.rows.length} servers assigned. {grants}</span>
  </section>
}

export type FriendAccessConfiguration = PermissionValues & {
  id?: string
  assignedProfileIds: readonly string[]
  serverPermissions: readonly ServerPermission[]
  temporaryHelperActive?: boolean
}
export type FriendAccessReviewRow = {
  profileId: string; name: string; beforeAssigned: boolean; afterAssigned: boolean
  beforePermissions: PermissionValues | null; afterPermissions: PermissionValues | null
  change: 'Added' | 'Removed' | 'Changed' | 'Unchanged' | 'ReviewNeeded'
}
export type FriendAccessReviewView = {
  rows: FriendAccessReviewRow[]
  changedCount: number
  beforeDefaults: PermissionValues | null
  afterDefaults: PermissionValues | null
  helperProjection: boolean
}

function configurationPermission(configuration: FriendAccessConfiguration, profileId: string): PermissionValues | null {
  if (configuration.temporaryHelperActive) return null
  return permissionValues(configuration.serverPermissions.find(item => item.profileId === profileId) ?? configuration)
}

export function reviewFriendAccess(before: FriendAccessConfiguration, after: FriendAccessConfiguration,
  servers: readonly FriendAccessServer[]): FriendAccessReviewView {
  const rows = [...new Set([...before.assignedProfileIds, ...after.assignedProfileIds])].map(profileId => {
    const beforeAssigned = before.assignedProfileIds.includes(profileId)
    const afterAssigned = after.assignedProfileIds.includes(profileId)
    const beforePermissions = beforeAssigned ? configurationPermission(before, profileId) : null
    const afterPermissions = afterAssigned ? configurationPermission(after, profileId) : null
    const change: FriendAccessReviewRow['change'] = !beforeAssigned ? 'Added' : !afterAssigned ? 'Removed' :
      beforePermissions === null || afterPermissions === null ? 'ReviewNeeded' :
      actions.some(action => beforePermissions[action] !== afterPermissions[action]) ? 'Changed' : 'Unchanged'
    return { profileId, name: servers.find(server => server.id === profileId)?.name ?? 'Unavailable saved server',
      beforeAssigned, afterAssigned, beforePermissions, afterPermissions, change }
  })
  return { rows, changedCount: rows.filter(row => row.change !== 'Unchanged').length,
    beforeDefaults: before.temporaryHelperActive ? null : permissionValues(before),
    afterDefaults: after.temporaryHelperActive ? null : permissionValues(after),
    helperProjection: !!before.temporaryHelperActive || !!after.temporaryHelperActive }
}

export type FriendAccessReviewProps = {
  before: FriendAccessConfiguration
  after: FriendAccessConfiguration
  servers: readonly FriendAccessServer[]
  clearsExceptions?: boolean
}

const reviewActions: { label: string; icon: IconName; allowed: (permissions: PermissionValues) => boolean }[] = [
  { label: 'Start', icon: 'play', allowed: permissions => permissions.canStart },
  { label: 'Guarded Stop', icon: 'stop', allowed: permissions => permissions.canStop },
  { label: 'Restart', icon: 'refresh', allowed: permissions => permissions.canStart && permissions.canStop },
  { label: 'Add time', icon: 'clock', allowed: permissions => permissions.canExtendTimer },
  { label: 'View logs', icon: 'logs', allowed: permissions => permissions.canViewLogs }
]

function ReviewPermissionChips({ permissions }: { permissions: PermissionValues }) {
  const allowed = reviewActions.filter(action => action.allowed(permissions))
  return allowed.length ? <ul className="friend-grant-chips" aria-label="Allowed permissions">
    {allowed.map(action => <li key={action.label}><Icon name={action.icon} size={14} />{action.label}</li>)}
  </ul> : <p className="friend-review-status-only"><Icon name="eye" size={15} />Status and connection details only</p>
}

function PermissionChanges({ before, after, label }: {
  before: PermissionValues; after: PermissionValues; label: string
}) {
  const changed = reviewActions.filter(action => action.allowed(before) !== action.allowed(after))
  if (changed.length === 0) return <div className="friend-review-same">
    <span className="friend-access-badge">Unchanged</span><ReviewPermissionChips permissions={after} />
  </div>
  return <div className="friend-permission-changes">
    <div className="friend-permission-column-labels" aria-hidden="true"><span>Permission</span><span>Before</span><span /><span>After</span></div>
    <ul className="friend-permission-change-list" aria-label={label}>
      {changed.map(action => <li key={action.label} aria-label={`${action.label} permission change`}>
        <span className="friend-permission-label"><Icon name={action.icon} size={15} />{action.label}</span>
        <span className={action.allowed(before) ? 'friend-permission-on' : 'friend-permission-off'} aria-label={`Before: ${action.allowed(before) ? 'Allowed' : 'Off'}`}>
          {action.allowed(before) ? 'Allowed' : 'Off'}</span>
        <Icon name="chevronRight" size={14} />
        <span className={action.allowed(after) ? 'friend-permission-on' : 'friend-permission-off'} aria-label={`After: ${action.allowed(after) ? 'Allowed' : 'Off'}`}>
          {action.allowed(after) ? 'Allowed' : 'Off'}</span>
      </li>)}
    </ul>
    {changed.length < reviewActions.length && <p className="friend-review-quiet">Other permissions unchanged.</p>}
  </div>
}

function ReviewPermissionSide({ assigned, permissions, label, defaults = false }: {
  assigned: boolean; permissions: PermissionValues | null; label: string; defaults?: boolean
}) {
  return <div className="friend-review-side" role="group" aria-label={`${label} permissions`}>
    <strong>{label}</strong>
    {!assigned ? <p>Not assigned</p> : permissions ? <ReviewPermissionChips permissions={permissions} /> :
      <p className="friend-review-unavailable">{defaults ? 'Saved defaults unavailable' : 'Saved permissions unavailable during helper overlay'}</p>}
  </div>
}

function ReviewServerChange({ row }: { row: FriendAccessReviewRow }) {
  return <li className="friend-access-review-row" data-change={row.change}>
    <div role="group" aria-label={`${row.name} access change`}>
      <div className="friend-review-row-heading"><strong><Icon name="server" size={16} />{row.name}</strong>
        <span className="friend-access-badge">{row.change === 'ReviewNeeded' ? 'Refresh needed' : row.change}</span></div>
      {row.beforeAssigned && row.afterAssigned && row.beforePermissions && row.afterPermissions ?
        <PermissionChanges before={row.beforePermissions} after={row.afterPermissions} label={`${row.name} permission changes`} /> :
        <div className="friend-permission-comparison">
          <ReviewPermissionSide assigned={row.beforeAssigned} permissions={row.beforePermissions} label="Before" />
          <ReviewPermissionSide assigned={row.afterAssigned} permissions={row.afterPermissions} label="After" />
        </div>}
    </div>
  </li>
}

export function FriendAccessReview({ before, after, servers, clearsExceptions = false }: FriendAccessReviewProps) {
  const headingId = useId()
  const review = reviewFriendAccess(before, after, servers)
  const changed = review.rows.filter(row => row.change !== 'Unchanged')
  const unchanged = review.rows.filter(row => row.change === 'Unchanged')
  return <section className="players-workspace friend-access-review" aria-labelledby={headingId}>
    <h3 id={headingId}><Icon name="shield" />Review access changes</h3>
    <p className="friend-review-intro">{review.changedCount} assigned-server {review.changedCount === 1 ? 'change' : 'changes'}. Changes apply only when you save.</p>
    <div className="friend-default-review" role="group" aria-label="Device defaults">
      <strong>Device defaults</strong>
      {review.beforeDefaults && review.afterDefaults ?
        <PermissionChanges before={review.beforeDefaults} after={review.afterDefaults} label="Device default permission changes" /> :
        <div className="friend-permission-comparison">
          <ReviewPermissionSide assigned permissions={review.beforeDefaults} label="Before" defaults />
          <ReviewPermissionSide assigned permissions={review.afterDefaults} label="After" defaults />
        </div>}
    </div>
    {review.helperProjection && <DismissibleNotice as="p" scope={before.id} className="warning-text friend-review-note"><Icon name="warning" />
      <span>End temporary helper access and refresh before editing saved permissions. The current Host view includes the helper overlay.</span></DismissibleNotice>}
    {clearsExceptions && <DismissibleNotice as="p" scope={before.id} className="friend-review-note"><Icon name="refresh" />
      <span>This preset replaces all four device defaults and clears per-server exceptions on every assigned server.</span></DismissibleNotice>}
    {changed.length > 0 && <ul className="friend-access-change-list" aria-label="Server access before and after">
      {changed.map(row => <ReviewServerChange key={row.profileId} row={row} />)}
    </ul>}
    {unchanged.length > 0 && <details className="friend-access-unchanged">
      <summary>{unchanged.length} unchanged {unchanged.length === 1 ? 'server' : 'servers'}</summary>
      <ul aria-label="Unchanged server access">{unchanged.map(row => <li key={row.profileId}>
        <strong><Icon name="server" size={15} />{row.name}</strong>
        {row.afterPermissions && <ReviewPermissionChips permissions={row.afterPermissions} />}
      </li>)}</ul>
    </details>}
    {review.rows.length === 0 && <p className="friend-review-quiet">No servers are assigned before or after. Defaults alone give no server access.</p>}
    <small className="friend-review-boundary">Restart requires both Start and Stop. Log and timer grants remain separate; owner access deadlines stay unchanged.</small>
  </section>
}

export type FriendPcFiltersValue = {
  search: string
  status: 'all' | 'connected' | 'unknown' | 'approval' | 'expired' | 'helper' | 'revoked' | 'unassigned'
  serverId: string
}
export const defaultFriendPcFilters: FriendPcFiltersValue = { search: '', status: 'all', serverId: 'all' }

export function filterFriendPcs(devices: readonly Device[], servers: readonly FriendAccessServer[],
  filters: FriendPcFiltersValue, nowMs: number): Device[] {
  const search = filters.search.trim().toLocaleLowerCase()
  return devices.filter(device => {
    if (device.revoked && filters.status !== 'revoked') return false
    const view = effectiveDeviceAccess(device, servers, nowMs)
    if (filters.serverId !== 'all' && !device.assignedProfileIds.includes(filters.serverId)) return false
    const statusMatches = filters.status === 'all' ||
      (filters.status === 'connected' && view.connected && view.state === 'Available') ||
      (filters.status === 'unknown' && (!view.connected || view.state === 'Unknown' || view.state === 'RefreshNeeded')) ||
      (filters.status === 'approval' && view.state === 'ApprovalPending') ||
      (filters.status === 'expired' && ['AccessExpired', 'CredentialExpired'].includes(view.state)) ||
      (filters.status === 'helper' && view.helper === 'Active' && view.state === 'Available') ||
      (filters.status === 'revoked' && view.state === 'Revoked') ||
      (filters.status === 'unassigned' && view.rows.length === 0)
    if (!statusMatches) return false
    const searchable = [device.profileName ?? '', device.name, view.label, ...view.rows.map(row => row.name)].join(' ').toLocaleLowerCase()
    return !search || searchable.includes(search)
  }).sort((left, right) => {
    const rank = (device: Device) => {
      const view = effectiveDeviceAccess(device, servers, nowMs)
      return view.state === 'Available' ? view.connected ? 0 : 1 : 2
    }
    return rank(left) - rank(right)
  })
}

export type FriendPcFiltersProps = {
  value: FriendPcFiltersValue
  servers: readonly FriendAccessServer[]
  resultCount: number
  totalCount: number
  onChange: (value: FriendPcFiltersValue) => void
}

export function FriendPcFilters({ value, servers, resultCount, totalCount, onChange }: FriendPcFiltersProps) {
  return <div className="friend-pc-filters" role="group" aria-label="Filter Friend PCs">
    <label><span className="friend-filter-label"><Icon name="search" size={15} />Search Friend PCs</span><Input type="search" maxLength={200} value={value.search} placeholder="Person, PC or assigned server name" onChange={event => onChange({ ...value, search: event.target.value })} /></label>
    <label><span className="friend-filter-label"><Icon name="monitor" size={15} />PC status</span><Select value={value.status} onChange={event => onChange({ ...value, status: event.target.value as FriendPcFiltersValue['status'] })}>
      <option value="all">Current PCs</option><option value="connected">Connected</option><option value="unknown">Connection Unknown or refresh needed</option>
      <option value="approval">Waiting for approval</option><option value="expired">Access expired</option><option value="helper">Temporary helper</option>
      <option value="revoked">Show removed PCs</option><option value="unassigned">No assigned servers</option>
    </Select></label>
    <label><span className="friend-filter-label"><Icon name="server" size={15} />Assigned server</span><Select value={value.serverId} onChange={event => onChange({ ...value, serverId: event.target.value })}>
      <option value="all">All servers</option>{servers.map(server => <option key={server.id} value={server.id}>{server.name}</option>)}
    </Select></label>
    <span className="friend-filter-count" role="status" aria-live="polite" aria-atomic="true">{resultCount} of {totalCount} Friend PCs shown.</span>
  </div>
}
