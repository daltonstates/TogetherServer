import { describe, expect, it } from 'vitest'
import type { Device } from './contracts'
import { devicePermission, globalPermissionRequest, permissionMix } from './permissionState'

const device: Device = {
  id: 'device', profileId: 'one', assignedProfileIds: ['one', 'two'], name: 'Friend PC',
  canStart: true, canStop: false, canExtendTimer: true, canViewLogs: false,
  revoked: false, paired: true, approvalPending: false, credentialExpiresUtc: null,
  lastHeartbeatUtc: null,
  serverPermissions: [
    { profileId: 'one', canStart: true, canStop: false, canExtendTimer: true, canViewLogs: false },
    { profileId: 'two', canStart: true, canStop: false, canExtendTimer: true, canViewLogs: true }
  ]
}

describe('Friend permission wiring', () => {
  it('keeps View logs independent and reports a mixed exception state', () => {
    expect(permissionMix(device, 'canViewLogs')).toEqual({ mixed: true, all: false })
    expect(devicePermission(device, 'one').canViewLogs).toBe(false)
    expect(devicePermission(device, 'two').canViewLogs).toBe(true)
  })

  it('changes only the global log permission field and uses the logs scope', () => {
    expect(globalPermissionRequest(device, 'canViewLogs', true)).toEqual({
      canStart: true,
      canStop: false,
      canExtendTimer: true,
      canViewLogs: true,
      scope: 'logs'
    })
  })

  it('defaults an absent per-server exception to the global false grant', () => {
    expect(devicePermission({ ...device, serverPermissions: [] }, 'one')).toMatchObject({
      canStart: true, canStop: false, canExtendTimer: true, canViewLogs: false
    })
  })
})
