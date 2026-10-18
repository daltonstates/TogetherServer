import type { DesktopNotificationTarget } from './desktopNotificationBridge'
import type { IconName } from './Icon'

export type AppSection = 'profile' | 'appearance' | 'notifications' | 'windows' | 'updates' | 'shortcuts'
export type HostPcSection = 'access' | 'stop' | 'network' | 'diagnostics' | 'advanced'
export type JoinSection = 'pairing' | 'play' | 'chat' | 'logs' | 'shared-saves' | 'network' | 'connection'
export type SectionItem<T extends string> = { id: T; label: string; icon: IconName }

export const appSections: SectionItem<AppSection>[] = [
  { id: 'profile', label: 'Profile', icon: 'users' },
  { id: 'appearance', label: 'Appearance', icon: 'palette' },
  { id: 'notifications', label: 'Notifications', icon: 'bell' },
  { id: 'windows', label: 'Windows behavior', icon: 'monitor' },
  { id: 'updates', label: 'Updates', icon: 'refresh' },
  { id: 'shortcuts', label: 'Keyboard shortcuts', icon: 'keyboard' }
]

export const hostPcSections: SectionItem<HostPcSection>[] = [
  { id: 'access', label: 'Friend access', icon: 'users' },
  { id: 'stop', label: 'Stop & timer', icon: 'clock' },
  { id: 'network', label: 'Connection help', icon: 'link' },
  { id: 'diagnostics', label: 'Diagnostics', icon: 'logs' },
  { id: 'advanced', label: 'Advanced', icon: 'settings' }
]

export const joinSections: SectionItem<JoinSection>[] = [
  { id: 'play', label: 'Play', icon: 'play' },
  { id: 'chat', label: 'Chat', icon: 'chat' },
  { id: 'logs', label: 'Logs', icon: 'logs' },
  { id: 'shared-saves', label: 'Shared saves', icon: 'archive' },
  { id: 'network', label: 'Connection help', icon: 'link' },
  { id: 'connection', label: 'Connection settings', icon: 'settings' },
  { id: 'pairing', label: 'Add a Host', icon: 'plus' }
]

export type WorkspaceDestination =
  | { workspace: 'settings'; section: AppSection }
  | { workspace: 'host'; section: string; scope: 'server' | 'pc'; profileId: string | null }
  | { workspace: 'join'; section: JoinSection; profileId: string | null; connectionId: string | null }

/** Native notification destinations from earlier versions remain valid aliases. */
export function workspaceDestination(target: DesktopNotificationTarget): WorkspaceDestination {
  if (target.workspace === 'settings') {
    if (target.section === 'app' || appSections.some(section => section.id === target.section)) {
      return { workspace: 'settings', section: target.section === 'app' ? 'appearance' : target.section as AppSection }
    }
    return { workspace: 'host', scope: 'pc', section: target.section, profileId: target.profileId }
  }
  if (target.workspace === 'join') {
    return { workspace: 'join', section: target.section === 'overview' ? 'play' : target.section as JoinSection,
      profileId: target.profileId, connectionId: target.connectionId }
  }
  return { workspace: 'host', scope: hostPcSections.some(section => section.id === target.section) ? 'pc' : 'server',
    section: target.section, profileId: target.profileId }
}
