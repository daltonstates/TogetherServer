import { changeJson } from './api'
import { ContractError, parseUpdateView, type UpdateView } from './contracts'

export const notificationEventKinds = ['Lifecycle', 'Backup', 'Access', 'Connections', 'Network', 'Recovery',
  'Countdown', 'Players', 'Maintenance', 'Configuration', 'AddOns', 'Remote', 'Update'] as const
export type NotificationEventKind = typeof notificationEventKinds[number]
export type NotificationServerPreference = { profileId: string; connectionId: string | null; allowedEvents: NotificationEventKind[] }
export type NotificationPreferences = { quietMode: boolean; allowedEvents: NotificationEventKind[];
  servers: NotificationServerPreference[]; systemState: string; systemAllowsNotifications: boolean }
export type NotificationPreferenceChange = { quietMode: boolean } | { allowedEvents: NotificationEventKind[] } |
  { profileId: string; connectionId?: string | null; allowedEvents: NotificationEventKind[] | null }
export type UpdatePreparation = { stage: 'Idle' | 'Checking' | 'Downloading' | 'Verifying' | 'Ready' |
  'Checkpoint' | 'Restarting' | 'Blocked' | 'Failed'; message: string; downloadedBytes: number;
  totalBytes: number | null; blocker: string | null }
export type DesktopUpdateDetails = UpdateView & { releaseNotes?: string; releaseNotesUrl?: string | null;
  snoozedUntilUtc?: string | null; promptSnoozed?: boolean; versionSkipped?: boolean; preparation?: UpdatePreparation | null }
export type UpdateReminderDuration = 'OneHour' | 'Tomorrow' | 'ThreeDays' | 'SevenDays' | 'SkipVersion' | 'Clear'

const guid = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i
const emptyGuid = '00000000-0000-0000-0000-000000000000'
function object(value: unknown, context: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new ContractError(`${context} must be an object.`)
  return value as Record<string, unknown>
}
function events(value: unknown): NotificationEventKind[] {
  if (!Array.isArray(value) || value.length > notificationEventKinds.length || new Set(value).size !== value.length ||
    value.some(item => typeof item !== 'string' || !notificationEventKinds.includes(item as NotificationEventKind)))
    throw new ContractError('Notification events must be fixed reviewed choices.')
  return value as NotificationEventKind[]
}
function serverId(value: unknown): string {
  if (typeof value !== 'string' || !guid.test(value) || value === emptyGuid)
    throw new ContractError('Notification preference must belong to a saved server.')
  return value
}

export function parseNotificationPreferences(value: unknown): NotificationPreferences {
  const source = object(value, 'Notification preferences')
  if (typeof source.quietMode !== 'boolean' || typeof source.systemAllowsNotifications !== 'boolean' ||
    typeof source.systemState !== 'string' || !['Unknown', 'NotPresent', 'Busy', 'RunningD3DFullScreen',
      'PresentationMode', 'AcceptsNotifications', 'QuietTime', 'App'].includes(source.systemState) ||
    source.systemAllowsNotifications !== (source.systemState === 'AcceptsNotifications') ||
    !Array.isArray(source.servers) || source.servers.length > 128)
    throw new ContractError('Notification preferences contain invalid local state.')
  const servers = source.servers.map(item => {
    const server = object(item, 'Server notification preferences')
    return { profileId: serverId(server.profileId), connectionId: server.connectionId == null ? null : serverId(server.connectionId),
      allowedEvents: events(server.allowedEvents) }
  })
  if (new Set(servers.map(item => `${item.connectionId ?? ''}:${item.profileId}`)).size !== servers.length)
    throw new ContractError('Notification preferences repeat a server scope.')
  return { quietMode: source.quietMode, systemAllowsNotifications: source.systemAllowsNotifications,
    systemState: source.systemState, allowedEvents: events(source.allowedEvents), servers }
}

export function saveNotificationPreferences(change: NotificationPreferenceChange,
  signal?: AbortSignal): Promise<NotificationPreferences> {
  return changeJson('/api/local/desktop/notifications', 'PUT', parseNotificationPreferences, change, signal)
}
export const syncDesktopQuietMode = (quietMode: boolean, signal?: AbortSignal) =>
  saveNotificationPreferences({ quietMode }, signal)

export function parseUpdatePreparation(value: unknown): UpdatePreparation {
  const source = object(value, 'Update preparation')
  const stages = ['Idle', 'Checking', 'Downloading', 'Verifying', 'Ready', 'Checkpoint', 'Restarting', 'Blocked', 'Failed']
  if (typeof source.stage !== 'string' || !stages.includes(source.stage) || typeof source.message !== 'string' ||
    source.message.length > 600 || typeof source.downloadedBytes !== 'number' ||
    !Number.isSafeInteger(source.downloadedBytes) || source.downloadedBytes < 0 || source.downloadedBytes > 200 * 1024 * 1024 ||
    (source.totalBytes !== null && (typeof source.totalBytes !== 'number' || !Number.isSafeInteger(source.totalBytes) ||
      source.totalBytes < source.downloadedBytes || source.totalBytes > 200 * 1024 * 1024 || source.totalBytes < 1)) ||
    (source.blocker !== null && (typeof source.blocker !== 'string' || source.blocker.length > 600)))
    throw new ContractError('Update preparation contains invalid bounded progress.')
  return { stage: source.stage as UpdatePreparation['stage'], message: source.message,
    downloadedBytes: source.downloadedBytes, totalBytes: source.totalBytes as number | null,
    blocker: source.blocker as string | null }
}

export function trustedReleaseNotesLink(value: string | null | undefined, version: string | null): boolean {
  return !!version && /^\d+\.\d+\.\d+$/.test(version) &&
    value === `https://github.com/daltonstates/TogetherServer/releases/tag/v${version}`
}

export function parseDesktopUpdateDetails(value: unknown, context = 'Update details'): DesktopUpdateDetails {
  const source = object(value, context)
  const update = parseUpdateView(value, context)
  const releaseNotes = source.releaseNotes ?? ''
  const releaseNotesUrl = source.releaseNotesUrl ?? null
  const snoozedUntilUtc = source.snoozedUntilUtc ?? null
  const promptSnoozed = source.promptSnoozed ?? false
  const versionSkipped = source.versionSkipped ?? false
  if (typeof releaseNotes !== 'string' || releaseNotes.length > 4000 ||
    (releaseNotesUrl !== null && (typeof releaseNotesUrl !== 'string' || !trustedReleaseNotesLink(releaseNotesUrl, update.latestVersion))) ||
    (snoozedUntilUtc !== null && (typeof snoozedUntilUtc !== 'string' ||
      !/(?:Z|\+00:00)$/.test(snoozedUntilUtc) || !Number.isFinite(Date.parse(snoozedUntilUtc)))) ||
    typeof promptSnoozed !== 'boolean' || typeof versionSkipped !== 'boolean' ||
    (promptSnoozed && snoozedUntilUtc === null) || (versionSkipped && !promptSnoozed))
    throw new ContractError('Update details contain invalid notes or reminder state.')
  return { ...update, releaseNotes, releaseNotesUrl, snoozedUntilUtc, promptSnoozed, versionSkipped,
    preparation: source.preparation == null ? null : parseUpdatePreparation(source.preparation) }
}

export function updatePreparationReport(update: DesktopUpdateDetails, preparation: UpdatePreparation | null | undefined): string {
  return JSON.stringify({ currentVersion: update.currentVersion, targetVersion: update.latestVersion,
    publisherTrust: update.publisherTrust, snoozedUntilUtc: update.snoozedUntilUtc ?? null,
    versionSkipped: update.versionSkipped ?? false,
    preparation: preparation ? parseUpdatePreparation(preparation) : null }, null, 2)
}
