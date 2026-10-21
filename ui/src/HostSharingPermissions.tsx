import type { Device } from './contracts'
import { Icon } from './Icon'
import './HostSharing.css'

export type Grants = { receive: boolean; eligibleHost: boolean; recoveryVoter: boolean; manageSharing: boolean; reshare: boolean }
type Member = { deviceId: string; grants: Grants; revoked: boolean; accessExpiresUtc: string | null }

// The server switch owns access. This list is a read-only explanation, never a
// second place to configure receiving, forwarding or hosting permissions.
export function HostSharingPermissions({ devices, enabled }: {
  profileId: string; devices: Device[]; members: Member[]; enabled: boolean;
  busy?: boolean; canManage?: boolean; blocked?: boolean;
  onChange?: (deviceId: string, changes: Partial<Grants>) => Promise<void>;
  onResetKey?: (deviceId: string) => Promise<void>
}) {
  const approved = devices.filter(device => device.paired && !device.revoked && !device.approvalPending && !device.accessExpired)
  return <div className="host-sharing-people" aria-label="Friends with hosting access">
    {approved.length === 0 ? <p className="host-sharing-hint">Approve a Friend PC and assign it to this server to share hosting.</p> :
      approved.map(device => <div className="host-sharing-access-row" key={device.id}>
        <Icon name="monitor" /><span>{device.name}</span><small>{enabled ? 'Hosting allowed' : 'Hosting off'}</small>
      </div>)}
  </div>
}
