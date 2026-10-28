import type { DesktopNotificationTarget } from './desktopNotificationBridge'
import type { IconName } from './Icon'

export type AppSection = 'profile' | 'appearance' | 'notifications' | 'game-ports' | 'windows' | 'updates' | 'shortcuts'
export type HostPcSection = 'access' | 'stop' | 'network' | 'diagnostics' | 'advanced'
export type JoinSection = 'pairing' | 'play' | 'chat' | 'logs' | 'shared-saves' | 'network' | 'connection'
export type SectionItem<T extends string> = { id: T; label: string; icon: IconName }

export const appSections: SectionItem<AppSection>[] = [
  { id: 'profile', label: 'Profile', icon: 'users' },
  { id: 'appearance', label: 'Appearance', icon: 'palette' },
  { id: 'notifications', label: 'Notifications', icon: 'bell' },
  { id: 'game-ports', label: 'Game ports', icon: 'plug' },
  { id: 'windows', label: 'Windows behavior', icon: 'monitor' },
  { id: 'updates', label: 'Updates', icon: 'refresh' },
  { id: 'shortcuts', label: 'Keyboard shortcuts', icon: 'keyboard' }
]

export const hostPcSections: SectionItem<HostPcSection>[] = [
  { id: 'access', label: 'Access', icon: 'users' },
  { id: 'stop', label: 'Shutdown', icon: 'clock' },
  { id: 'network', label: 'Network', icon: 'link' },
  { id: 'advanced', label: 'Advanced', icon: 'settings' }
]

export const joinSections: SectionItem<JoinSection>[] = [
  { id: 'play', label: 'Play', icon: 'play' },
  { id: 'chat', label: 'Chat', icon: 'chat' },
  { id: 'logs', label: 'Logs', icon: 'logs' },
  { id: 'shared-saves', label: 'Shared saves', icon: 'archive' },
  { id: 'connection', label: 'Settings', icon: 'settings' }
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
    return { workspace: 'join', section: target.section === 'overview' ? 'play' : target.section === 'network' ? 'connection' : target.section as JoinSection,
      profileId: target.profileId, connectionId: target.connectionId }
  }
  return { workspace: 'host', scope: target.section === 'diagnostics' || hostPcSections.some(section => section.id === target.section) ? 'pc' : 'server',
    section: target.section, profileId: target.profileId }
}
