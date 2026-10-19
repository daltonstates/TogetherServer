import type { Device } from './contracts'
import { Button, Input } from './Controls'
import { Icon } from './Icon'
import './HostSharing.css'

export type Grants = { receive: boolean; eligibleHost: boolean; recoveryVoter: boolean; manageSharing: boolean; reshare: boolean }
type Member = { deviceId: string; grants: Grants; revoked: boolean; accessExpiresUtc: string | null }

const noGrants: Grants = { receive: false, eligibleHost: false, recoveryVoter: false, manageSharing: false, reshare: false }
const permissions: { key: keyof Grants; label: string; title: string; description: string }[] = [
  { key: 'receive', label: 'Receive', title: 'Receive saves',
    description: 'Keep verified copies of this world. This PC must also choose to receive them locally.' },
  { key: 'reshare', label: 'Forward verified saves', title: 'Forward verified saves',
    description: 'Send verified copies to approved world members who have Receive permission and local consent.' },
  { key: 'recoveryVoter', label: 'Recovery voter', title: 'Vote on legacy recovery',
    description: 'Vote in the guarded hosting-decision flow. This does not choose the group default or start a server.' },
  { key: 'manageSharing', label: 'Manage sharing', title: 'Manage sharing',
    description: 'Edit permitted member grants. Only the owner can grant forwarding or sharing management.' },
]

export function HostSharingPermissions({ profileId, devices, members, busy, enabled, canManage, blocked, onChange, onResetKey }:
  { profileId: string; devices: Device[]; members: Member[]; busy: boolean; enabled: boolean;
    canManage: boolean; blocked: boolean;
    onChange: (deviceId: string, changes: Partial<Grants>) => Promise<void>;
    onResetKey: (deviceId: string) => Promise<void> }) {
  const accessEnded = (device: Device) => {
    const expiry = members.find(item => item.deviceId === device.id)?.accessExpiresUtc
    return device.accessExpired || expiry != null && Date.parse(expiry) <= Date.now()
  }
  const orderedDevices = [...devices].sort((first, second) => Number(accessEnded(first)) - Number(accessEnded(second)) ||
    Number(!first.sharedWorldKeyEnrolled) - Number(!second.sharedWorldKeyEnrolled))
  return <div className="host-sharing-people">
    {devices.length === 0 && <div className="host-sharing-empty"><Icon name="users" size={28} />
      <h4>No approved PCs for this world yet</h4><p>Approve and assign a Friend PC first. New permissions start off.</p>
    </div>}
    {orderedDevices.map(device => {
      const member = members.find(item => item.deviceId === device.id)
      const grants = { ...noGrants, ...(member?.grants ?? device.sharedWorldGrants?.[profileId]) }
      const expired = accessEnded(device)
      const revoked = device.revoked || member?.revoked === true
      const disabled = busy || !enabled || !canManage || blocked || expired || revoked || device.approvalPending
      const canHost = grants.receive && grants.eligibleHost
      const initials = device.name.trim().split(/\s+/).slice(0, 2).map(part => part[0] ?? '').join('').toUpperCase() || 'PC'
      const permission = (item: typeof permissions[number]) => <label className="host-sharing-permission" key={item.key}>
        <span><strong>{item.title}</strong><small>{item.description}</small></span>
        <Input type="checkbox" aria-label={`${item.label} for ${device.name}`} checked={grants[item.key]}
          disabled={disabled} onChange={event => void onChange(device.id,
            item.key === 'receive' ? { receive: event.target.checked, eligibleHost: false } :
              { [item.key]: event.target.checked })} />
      </label>
      return <article className="host-sharing-person" key={device.id} aria-label={`${device.name} sharing permissions`}>
        <header><span className="host-sharing-avatar" aria-hidden="true">{initials}</span>
          <div><h4>{device.name}</h4></div>
          <span className="host-sharing-badge">{revoked ? 'Access removed' : expired ? 'Access ended' : device.approvalPending ? 'Approval pending' :
            !device.sharedWorldKeyEnrolled && (grants.receive || grants.eligibleHost) ? 'Waiting for friend' :
            canHost ? 'Hosting allowed' : grants.receive ? 'Copies only' : 'Not shared'}</span>
        </header>
        <label className="host-sharing-permission host-sharing-host-permission">
          <span><strong>Can host this save</strong><small>Receive verified saves and prepare a server on this PC. Your friend approves storage and finishes setup before Start.</small></span>
          <Input type="checkbox" aria-label={`Can host this save for ${device.name}`} checked={canHost}
            disabled={disabled} onChange={event => void onChange(device.id, event.target.checked ?
              { receive: true, eligibleHost: true } : { eligibleHost: false })} />
        </label>
        {expired && <p className="host-sharing-hint">Access ended for this PC. Restore access before changing its grants.</p>}
        {!device.sharedWorldKeyEnrolled && <p className="host-sharing-hint">Ask your friend to open TogetherServer and connect to this Host with their approved PC to finish enrollment. Receiving also needs their local consent before saves can arrive.</p>}
        <details className="host-sharing-details"><summary>Advanced permissions for {device.name}</summary>
          <div className="host-sharing-advanced-permissions">{permissions.map(permission)}</div>
          <p className="host-sharing-hint">Turn off Can host this save to keep receiving copies without hosting. Turning off Receive saves also removes hosting permission. Copies already received stay on that PC.</p>
          <details className="host-sharing-details"><summary>Signing identity for {device.name}</summary>
            <p>PC code {device.id.slice(-6).toUpperCase()}. Device identity: <code>{device.id}</code>. Signing identity {device.sharedWorldKeyEnrolled ? 'enrolled' : 'pending'}.</p>
            {device.sharedWorldKeyEnrolled && <Button className="text-button" disabled={busy || !canManage || blocked}
              onClick={() => void onResetKey(device.id)}>Reset {device.name}'s signing identity and grants</Button>}
          </details>
        </details>
      </article>
    })}
  </div>
}
