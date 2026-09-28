import type { Device, ServerPermission } from './contracts'

export type PermissionAction = 'canStart' | 'canStop' | 'canExtendTimer' | 'canViewLogs'
export type PermissionScope = 'start' | 'stop' | 'extend' | 'logs'
export type PermissionValues = Pick<ServerPermission,
  'canStart' | 'canStop' | 'canExtendTimer' | 'canViewLogs'>

const scopes: Record<PermissionAction, PermissionScope> = {
  canStart: 'start',
  canStop: 'stop',
  canExtendTimer: 'extend',
  canViewLogs: 'logs'
}

export function devicePermission(device: Device, profileId: string): ServerPermission {
  return device.serverPermissions?.find(permission => permission.profileId === profileId) ?? {
    profileId,
    canStart: device.canStart,
    canStop: device.canStop,
    canExtendTimer: device.canExtendTimer,
    canViewLogs: device.canViewLogs
  }
}

export function permissionMix(device: Device, action: PermissionAction) {
  const values = device.assignedProfileIds.map(profileId => devicePermission(device, profileId)[action])
  const global = device[action]
  return { mixed: values.some(value => value !== global), all: global }
}

export function globalPermissionRequest(device: Device, action: PermissionAction, value: boolean) {
  const permissions: PermissionValues = {
    canStart: device.canStart,
    canStop: device.canStop,
    canExtendTimer: device.canExtendTimer,
    canViewLogs: device.canViewLogs,
    [action]: value
  }
  return { ...permissions, scope: scopes[action] }
}
