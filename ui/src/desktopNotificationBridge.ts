export type DesktopNotificationTarget = { workspace: 'host' | 'join' | 'settings'; section: string;
  profileId: string | null; connectionId: string | null }

type NativeMessage = { data: unknown }
type NativeWebView = { addEventListener(type: 'message', callback: (event: NativeMessage) => void): void;
  removeEventListener(type: 'message', callback: (event: NativeMessage) => void): void }
export type DesktopDraftFlushReply = { type: 'together-drafts-flushed'; requestId: string; ok: boolean }
export type NativeDraftWebView = NativeWebView & { postMessage(message: DesktopDraftFlushReply): void }

const guid = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/i
const sections = { host: ['overview', 'backups', 'players', 'sessions', 'files', 'chat', 'logs'],
  join: ['overview', 'network', 'shared-saves', 'chat', 'logs'], settings: ['access', 'network', 'diagnostics', 'app', 'stop'] }

export function parseDesktopNotification(value: unknown): DesktopNotificationTarget | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null
  const source = value as Record<string, unknown>
  if (source.type !== 'together-notification' || typeof source.destination !== 'object' ||
    source.destination === null || Array.isArray(source.destination)) return null
  const target = source.destination as Record<string, unknown>
  if (target.workspace !== 'host' && target.workspace !== 'join' && target.workspace !== 'settings') return null
  if (typeof target.section !== 'string' || !sections[target.workspace].includes(target.section)) return null
  const id = (value: unknown): string | null | undefined => value == null ? null :
    typeof value === 'string' && guid.test(value) && value !== '00000000-0000-0000-0000-000000000000' ? value : undefined
  const profileId = id(target.profileId)
  const connectionId = id(target.connectionId)
  if (profileId === undefined || connectionId === undefined || (target.workspace !== 'join' && connectionId !== null)) return null
  return { workspace: target.workspace, section: target.section, profileId, connectionId }
}

// Only the native bridge dispatches these events. DesktopWindow posts one after an explicit notification click.
export function subscribeToDesktopNotifications(onNavigate: (destination: DesktopNotificationTarget) => void,
  native?: NativeWebView): () => void {
  const bridge = native ?? (window as unknown as { chrome?: { webview?: NativeWebView } }).chrome?.webview
  if (!bridge) return () => {}
  const receive = (event: NativeMessage) => {
    const destination = parseDesktopNotification(event.data)
    if (destination) onNavigate(destination)
  }
  bridge.addEventListener('message', receive)
  return () => bridge.removeEventListener('message', receive)
}

export function parseDesktopDraftFlushRequest(value: unknown): { requestId: string } | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null
  const source = value as Record<string, unknown>
  if (Object.keys(source).length !== 2 || !Object.hasOwn(source, 'type') || !Object.hasOwn(source, 'requestId') ||
    source.type !== 'together-flush-drafts' || typeof source.requestId !== 'string' ||
    !guid.test(source.requestId) || source.requestId === '00000000-0000-0000-0000-000000000000') return null
  return { requestId: source.requestId }
}

// Native Quit waits for this acknowledgement before taking the local API gate.
// This callback only keeps drafts; it does not navigate, send chat or quit the app.
export function subscribeToDesktopDraftFlush(onFlush: () => Promise<boolean>, native?: NativeDraftWebView): () => void {
  const bridge = native ?? (window as unknown as { chrome?: { webview?: NativeDraftWebView } }).chrome?.webview
  if (!bridge || typeof bridge.postMessage !== 'function') return () => {}
  let active = true
  let pending: string | null = null
  let completed: string | null = null
  const reply = (requestId: string, ok: boolean) => {
    if (!active) return
    try { bridge.postMessage({ type: 'together-drafts-flushed', requestId, ok }) }
    catch { /* Native timeout keeps the app open when no receipt can be delivered. */ }
  }
  const receive = (event: NativeMessage) => {
    if (!active) return
    const request = parseDesktopDraftFlushRequest(event.data)
    if (!request) return
    const key = request.requestId.toLowerCase()
    if (key === pending || key === completed) return
    if (pending !== null) { reply(request.requestId, false); return }
    pending = key
    void Promise.resolve().then(() => active ? onFlush() : false).then(result => reply(request.requestId, result === true),
      () => reply(request.requestId, false)).finally(() => {
      if (pending === key) { pending = null; completed = key }
    })
  }
  bridge.addEventListener('message', receive)
  return () => { active = false; bridge.removeEventListener('message', receive) }
}
