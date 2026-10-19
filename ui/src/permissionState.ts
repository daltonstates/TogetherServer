import type { Device, ServerPermission } from './contracts'

export type PermissionAction = 'canStart' | 'canStop' | 'canExtendTimer' | 'canViewLogs'
export type PermissionScope = 'start' | 'stop' | 'extend' | 'logs'
export type PermissionValues = Pick<ServerPermission,
  'canStart' | 'canStop' | 'canExtendTimer' | 'canViewLogs'>
export type PermissionPreset = 'status' | 'start' | 'helper' | 'custom'

export const permissionPresets: Record<Exclude<PermissionPreset, 'custom'>,
  PermissionValues & { label: string; detail: string }> = {
  status: {
    label: 'Status only', detail: 'View shared servers and connection details.',
    canStart: false, canStop: false, canExtendTimer: false, canViewLogs: false
  },
  start: {
    label: 'Can start', detail: 'Start shared servers. No Stop, extra time, or logs.',
    canStart: true, canStop: false, canExtendTimer: false, canViewLogs: false
  },
  helper: {
    label: 'Trusted helper', detail: 'Start servers, request Stop when empty, add time, and view logs with private details removed.',
    canStart: true, canStop: true, canExtendTimer: true, canViewLogs: true
  }
}

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

export function permissionPresetRequest(preset: Exclude<PermissionPreset, 'custom'>) {
  const permissions = permissionPresets[preset]
  return {
    canStart: permissions.canStart,
    canStop: permissions.canStop,
    canExtendTimer: permissions.canExtendTimer,
    canViewLogs: permissions.canViewLogs,
    scope: null
  }
}

export function matchingPermissionPreset(device: Device): PermissionPreset {
  if ((['canStart', 'canStop', 'canExtendTimer', 'canViewLogs'] as PermissionAction[])
      .some(action => permissionMix(device, action).mixed)) return 'custom'
  const match = (Object.entries(permissionPresets) as [Exclude<PermissionPreset, 'custom'>,
    PermissionValues][]).find(([, preset]) =>
      preset.canStart === device.canStart && preset.canStop === device.canStop &&
      preset.canExtendTimer === device.canExtendTimer && preset.canViewLogs === device.canViewLogs)
  return match?.[0] ?? 'custom'
}
