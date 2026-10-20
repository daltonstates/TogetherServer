import { useCallback, useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { ApiError, changeJson, errorMessage, getLocalJson } from './api'
import { Button, TextArea } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { ContractError, type ChatDraft } from './contracts'
import { maximumNoticeTextLength, parseChatRoomWithNotice, validPinnedNoticeText,
  parsePinnedNoticeFields, type ChatRoomWithNotice, type PinnedNoticeFields } from './pinnedNoticeContracts'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'
import { clearProtectedDraft, readProtectedDraft, saveProtectedDraft, type DraftIdentity } from './protectedUiDrafts'
import { Icon } from './Icon'
import { successFeedbackMs } from './actionFeedback'
import { useConfirmation } from './Confirmation'
import { chatAuthorId, createChatSoundPlayer, createIncomingChatTracker,
  readChatSoundPreference, saveChatSoundPreference } from './chatSound'
import './ServerChat.css'

const messageLimit = 500
const messageIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const readEvent = 'togetherserver:chat-read'
type QueuedDraft = ChatDraft & { submitted: boolean }
export type ManagedChatRoom = Omit<ChatRoomWithNotice, 'pending'> & { pending: QueuedDraft[]; selfAuthorId?: string | null }
export type ChatRoomSummary = PinnedNoticeFields & {
  ok: boolean; code: string; message: string; hostId: string; profileId: string; messageIds: string[]
}
export type ChatDraftState = {
  dirty: boolean; saving: boolean; recovering: boolean; flush: () => Promise<boolean>
}

export function parseManagedChatRoom(value: unknown, context?: string): ManagedChatRoom {
  const room = parseChatRoomWithNotice(value, context)
  const source = value as { pending: Array<{ submitted?: unknown }>; selfAuthorId?: unknown }
  if (source.selfAuthorId !== undefined && source.selfAuthorId !== null &&
      (typeof source.selfAuthorId !== 'string' || !messageIdPattern.test(source.selfAuthorId)))
    throw new ContractError('The chat room returned an invalid local author.')
  return { ...room, selfAuthorId: source.selfAuthorId as string | null | undefined, pending: room.pending.map((draft, index) => {
    const submitted = source.pending[index].submitted
    if (submitted !== undefined && submitted !== null && typeof submitted !== 'boolean')
      throw new ContractError('The chat queue returned an invalid delivery state.')
    // A legacy response gives no proof that a message was never dispatched.
    return { ...draft, submitted: submitted !== false }
  }) }
}

export function parseChatRoomSummary(value: unknown): ChatRoomSummary {
  if (typeof value !== 'object' || value === null || Array.isArray(value))
    throw new ContractError('The chat summary must be an object.')
  const source = value as Record<string, unknown>
  if (typeof source.ok !== 'boolean' || typeof source.code !== 'string' || source.code.length > 80 ||
      typeof source.message !== 'string' || source.message.length > 500 ||
      typeof source.hostId !== 'string' || !messageIdPattern.test(source.hostId) ||
      typeof source.profileId !== 'string' || !messageIdPattern.test(source.profileId) ||
      !Array.isArray(source.messageIds) || source.messageIds.length > 200 ||
      source.messageIds.some(id => typeof id !== 'string' || !messageIdPattern.test(id)) ||
      new Set(source.messageIds).size !== source.messageIds.length)
    throw new ContractError('The chat summary exceeded room limits.')
  if (!source.ok && (source.messageIds.length > 0 || source.notice != null))
    throw new ContractError('An unavailable room must not return chat content.')
  return { ok: source.ok, code: source.code, message: source.message,
    hostId: source.hostId, profileId: source.profileId, messageIds: source.messageIds as string[],
    ...parsePinnedNoticeFields(value, source.hostId, source.profileId) }
}

export function unreadMessageCount(ids: readonly string[], lastReadId: string | null): number {
  const index = lastReadId ? ids.findIndex(id => id.toLowerCase() === lastReadId.toLowerCase()) : -1
  return index < 0 ? ids.length : ids.length - index - 1
}

function roomScope(profileId: string, host: boolean, connectionId?: string | null) {
  return `${host ? 'host' : `friend-${connectionId ?? 'selected'}`}:${profileId}`.toLowerCase()
}
function chatBase(profileId: string, host: boolean, connectionId?: string | null) {
  const profile = encodeURIComponent(profileId)
  return host ? `/api/local/profiles/${profile}/chat` : connectionId
    ? `/api/local/friend/connections/${encodeURIComponent(connectionId)}/servers/${profile}/chat`
    : `/api/local/friend/${profile}/chat`
}
function readLastMessage(scope: string): string | null {
  try {
    const value = localStorage.getItem(`together-server.chat.last-read.${scope}`)
    return value && messageIdPattern.test(value) ? value : null
  } catch { return null }
}
function rememberLastMessage(scope: string, id: string) {
  try { localStorage.setItem(`together-server.chat.last-read.${scope}`, id) } catch { /* In-memory reading still works. */ }
  window.dispatchEvent(new CustomEvent(readEvent, { detail: { scope, id } }))
}

function useChatLifetime() {
  const lifetime = useRef(new AbortController())
  useEffect(() => {
    // StrictMode replays setup/cleanup in development. Each setup needs a live
    // controller while the real scope cleanup still aborts its own requests.
    if (lifetime.current.signal.aborted) lifetime.current = new AbortController()
    const active = lifetime.current
    return () => active.abort()
  }, [])
  return lifetime
}

function useProtectedChatDraft(identity: DraftIdentity, enabled: boolean) {
  const [text, setText] = useState('')
  const [ready, setReady] = useState(false)
  const [recovered, setRecovered] = useState<string | null>(null)
  const [feedback, setFeedback] = useState('')
  const [failed, setFailed] = useState(false)
  const [working, setWorking] = useState(false)
  const [, setReceiptRevision] = useState(0)
  const [readAttempt, setReadAttempt] = useState(0)
  const revision = useRef(0)
  const desired = useRef<string | null>(null)
  const saved = useRef<string | null>(null)
  const latestText = useRef('')
  // This is tied to a successful post from this composer, never inferred from
  // matching historical chat text. Keep it until the exact snapshot is cleared.
  const acceptedSnapshot = useRef<string | null>(null)
  const writes = useRef<Promise<boolean>>(Promise.resolve(true))
  const controller = useChatLifetime()

  const write = useCallback((value: string | null) => {
    const clearingAcceptedSnapshot = value === null ? acceptedSnapshot.current : null
    const next = writes.current.then(async () => {
      const signal = controller.current.signal
      if (signal.aborted) return false
      // Superseded debounced saves must never write an old editor value.
      if (value !== null && desired.current !== value) return false
      if (value !== null && saved.current === value) return true
      if (clearingAcceptedSnapshot !== null && saved.current !== null && saved.current !== clearingAcceptedSnapshot) {
        // A retry read may have observed another edit while this clear waited.
        // Its revision cannot be used to discard that different protected value.
        acceptedSnapshot.current = null; desired.current = null
        setReady(false); setRecovered(saved.current); setFailed(true)
        setFeedback('Message sent. A newer draft was kept on this PC for review.')
        return false
      }
      setWorking(true)
      try {
        let result = value === null
          ? await clearProtectedDraft(identity, revision.current, signal)
          : await saveProtectedDraft(identity, value, revision.current, signal)
        if (signal.aborted) return false
        if (clearingAcceptedSnapshot !== null && !result.ok && result.text === clearingAcceptedSnapshot) {
          // A lost save receipt can leave a newer revision of the same sent
          // snapshot. Retry only that exact value using its canonical revision.
          result = await clearProtectedDraft(identity, result.revision, signal)
          if (signal.aborted) return false
        }
        revision.current = result.revision
        if (!result.ok) {
          // A CAS response supplies the canonical nonempty/empty string at this
          // revision. Do not retain a previous value as the "already saved" cache.
          if (result.text !== null) saved.current = result.text
          desired.current = null
          setReady(false)
          if (clearingAcceptedSnapshot !== null && result.text === clearingAcceptedSnapshot) {
            setRecovered(null)
            setFeedback('Message sent. Its saved draft still needs clearing. Retry before writing another message.')
          } else {
            // A confirmed different value belongs to a newer edit; preserve it.
            if (result.text !== null) acceptedSnapshot.current = null
            setRecovered(result.text)
            setFeedback(result.message)
          }
          setFailed(true)
          return false
        }
        saved.current = result.text
        // Fast queued writes can batch their working states. Publish the new
        // receipt so the navigation guard also renders the confirmed save.
        setReceiptRevision(result.revision)
        setFailed(false)
        setFeedback(value === null ? '' : 'Draft saved on this PC.')
        if (value === null) { acceptedSnapshot.current = null; setRecovered(null); setReady(true) }
        return true
      } catch (failure) {
        if (!signal.aborted) {
          if (acceptedSnapshot.current !== null) {
            setReady(false); setRecovered(null)
            setFeedback('Message sent. Could not clear its saved draft. Retry before writing another message.')
          } else setFeedback(errorMessage(failure))
          setFailed(true)
        }
        return false
      } finally { if (!signal.aborted) setWorking(false) }
    })
    writes.current = next
    return next
  }, [controller, identity])

  useEffect(() => {
    if (!enabled) return
    let active = true
    const signal = controller.current.signal
    void Promise.resolve().then(() => readProtectedDraft(identity, signal)).then(async result => {
      if (!active || signal.aborted) return
      revision.current = result.revision
      if (!result.ok) { setFeedback(result.message); setFailed(true); return }
      saved.current = result.text
      if (acceptedSnapshot.current !== null && result.text === acceptedSnapshot.current) {
        // Retry cleanup of this known accepted snapshot, not a recovery offer.
        await write(null)
        return
      }
      acceptedSnapshot.current = null
      setFailed(false)
      if (result.text) setRecovered(result.text)
      else {
        if (latestText.current) desired.current = latestText.current
        setReady(true)
      }
    }).catch(failure => {
      if (active && !signal.aborted) { setFeedback(errorMessage(failure)); setFailed(true) }
    })
    return () => { active = false }
  }, [controller, enabled, identity, readAttempt, write])

  useEffect(() => {
    if (!enabled || !ready || desired.current === null) return
    const timer = window.setTimeout(() => void write(text), 700)
    return () => window.clearTimeout(timer)
  }, [enabled, ready, text, write])

  const change = (value: string) => { desired.current = value; latestText.current = value; setText(value) }
  const resume = () => {
    if (recovered === null) return
    change(recovered); setRecovered(null); setReady(true); setFailed(false); setFeedback('Saved draft ready for review.')
  }
  const discard = async () => {
    desired.current = null
    await write(null)
    if (controller.current.signal.aborted || saved.current !== null || !latestText.current) return
    desired.current = latestText.current
    await write(latestText.current)
  }
  const accepted = (snapshot: string) => {
    acceptedSnapshot.current = enabled ? snapshot : null
    desired.current = null; latestText.current = ''; setText('')
    if (enabled) setReady(false)
    return enabled ? write(null) : Promise.resolve(true)
  }
  const flush = useCallback(async () => {
    if (!enabled) return true
    if (acceptedSnapshot.current !== null) {
      const confirmed = await write(null)
      return confirmed && !controller.current.signal.aborted && acceptedSnapshot.current === null
    }
    const value = desired.current
    if (value === null) return !working && (!latestText.current || latestText.current === saved.current)
    const confirmed = await write(value)
    // Navigation leaves the composer enabled while storage is pending. A later
    // edit must stay open until that newer value, not just this snapshot, saves.
    return confirmed && !controller.current.signal.aborted && saved.current === value &&
      desired.current === value && latestText.current === value
  }, [controller, enabled, working, write])
  return { text, change, recovered, resume, discard, accepted, feedback, failed, working,
    dirty: enabled && (acceptedSnapshot.current !== null || saved.current !== text && (desired.current !== null || text.length > 0)), flush,
    retry: () => { setFeedback(''); setReadAttempt(current => current + 1) },
    ready: !enabled || ready, recovering: recovered !== null }
}

type ServerChatProps = {
  profileId: string; host: boolean; visible: boolean; supported?: boolean
  quietMode?: boolean
  connectionId?: string | null; hostId?: string | null
  onUnreadChange?: (count: number) => void
  onRoomChange?: (room: ManagedChatRoom) => void
  onDraftStateChange?: (state: ChatDraftState) => void
}

export function ServerChat(props: ServerChatProps) {
  return <ServerChatRoom key={roomScope(props.profileId, props.host, props.connectionId)} {...props} />
}

function ServerChatRoom({ profileId, host, visible, supported = true, quietMode = false, connectionId, hostId,
  onUnreadChange, onRoomChange, onDraftStateChange }: ServerChatProps) {
  const base = chatBase(profileId, host, connectionId)
  const scope = roomScope(profileId, host, connectionId)
  const [soundEnabled, setSoundEnabled] = useState(() => readChatSoundPreference(scope))
  const [soundPlayer] = useState(createChatSoundPlayer)
  const [incomingTracker] = useState(() => createIncomingChatTracker(host))
  const [arrivingMessages, setArrivingMessages] = useState<string[]>([])
  const soundOptions = useRef({ enabled: soundEnabled, quiet: quietMode, visible, supported })
  useLayoutEffect(() => { soundOptions.current = { enabled: soundEnabled, quiet: quietMode, visible, supported } })
  useEffect(() => {
    if (!visible || !supported) incomingTracker.reset()
    if (!soundEnabled || quietMode) soundPlayer.dispose()
  }, [incomingTracker, soundPlayer, visible, supported, soundEnabled, quietMode])
  useEffect(() => () => soundPlayer.dispose(), [soundPlayer])
  const [roomState, setRoomState] = useState<{ base: string; room: ManagedChatRoom } | null>(null)
  const room = roomState?.base === base ? roomState.room : null
  const draftIdentity = useMemo<DraftIdentity>(() => ({ purpose: 'chat', profileId,
    connectionId: host ? null : connectionId ?? null, key: 'compose' }), [profileId, host, connectionId])
  const draft = useProtectedChatDraft(draftIdentity, supported && (host || !!connectionId))
  const draftGuardCallback = useRef(onDraftStateChange)
  draftGuardCallback.current = onDraftStateChange
  const text = draft.text
  const lifetime = useChatLifetime()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const actionPending = useRef(false)
  const syncScope = useMemo(() => ({ base, hostId, visible, supported }), [base, hostId, visible, supported])
  const [syncState, setSyncState] = useState<{
    scope: typeof syncScope; status: 'pending' | 'success' | 'error'; message: string
  } | null>(null)
  const syncRequest = useRef<AbortController | null>(null)
  const syncFeedbackId = useId()
  const currentSync = syncState?.scope === syncScope ? syncState : null
  const syncing = currentSync?.status === 'pending'
  useEffect(() => {
    if (currentSync?.status !== 'success') return
    const timer = window.setTimeout(() => setSyncState(current => current === currentSync ? null : current), successFeedbackMs)
    return () => window.clearTimeout(timer)
  }, [currentSync])
  const [queueEdit, setQueueEdit] = useState<{ id: string; text: string; expectedText: string } | null>(null)
  const [queueError, setQueueError] = useState('')
  const [following, setFollowing] = useState(true)
  const follow = useRef(true)
  const messages = useRef<HTMLDivElement>(null)
  const [lastReadId, setLastReadId] = useState(() => readLastMessage(scope))
  const [noticeEdit, setNoticeEdit] = useState<{ base: string; text: string; expectedText: string; revision: number } | null>(null)
  const [noticeError, setNoticeError] = useState('')
  const editingNotice = noticeEdit?.base === base ? noticeEdit : null
  const { confirm, confirmationDialog } = useConfirmation()
  const noticeDirty = !!editingNotice && editingNotice.text !== editingNotice.expectedText
  const queueDirty = !!queueEdit && queueEdit.text !== queueEdit.expectedText
  const roomIdentity = useMemo(() => ({ base, hostId, roomHostId: room?.hostId }), [base, hostId, room?.hostId])
  // Each edit object is an exact revision, including reopening or changing back
  // to the same text. A review of one object never authorizes a later edit.
  const guardSnapshot = { roomIdentity, roomOk: room?.ok,
    noticeRevision: room?.notice?.revision, supported, visible,
    noticeEdit, queueEdit, busy, syncing, text, working: draft.working, recovering: draft.recovering,
    queueTarget: room?.pending.find(queued => queued.id === queueEdit?.id) }
  const latestGuard = useRef(guardSnapshot)
  useLayoutEffect(() => { latestGuard.current = guardSnapshot })
  const reviewingEdits = useRef(false)
  const flushComposer = draft.flush
  const flushForNavigation = useCallback(async () => {
    const target = latestGuard.current
    const sameTarget = () => {
      const actual = latestGuard.current
      return !lifetime.current.signal.aborted && actual.roomIdentity === target.roomIdentity &&
        actual.roomOk === target.roomOk && actual.noticeRevision === target.noticeRevision &&
        actual.supported === target.supported && actual.visible === target.visible &&
        actual.noticeEdit === target.noticeEdit && actual.queueEdit === target.queueEdit &&
        actual.text === target.text && actual.recovering === target.recovering &&
        !actual.busy && !actionPending.current && !actual.syncing && !syncRequest.current &&
        actual.queueTarget?.text === target.queueTarget?.text &&
        actual.queueTarget?.submitted === target.queueTarget?.submitted
    }
    if (!target.supported || reviewingEdits.current || !sameTarget()) return false
    reviewingEdits.current = true
    try {
      // Persist only the composer. Never publish a notice, send a message, or
      // save/cancel a queued message as a side effect of navigation.
      if (!await flushComposer() || !sameTarget()) return false
      const discardNotice = !!target.noticeEdit && target.noticeEdit.text !== target.noticeEdit.expectedText
      const discardQueue = !!target.queueEdit && target.queueEdit.text !== target.queueEdit.expectedText
      if (!discardNotice && !discardQueue) return true
      const label = discardNotice && discardQueue ? 'notice and queued message' : discardNotice ? 'notice' : 'queued message'
      const accepted = await confirm({ title: 'Discard unsaved chat edits?',
        description: `Discard the unsaved ${label} edits to leave this chat? The published notice and queued messages stay as they are. Your message draft is kept on this PC.`,
        confirmLabel: `Discard ${label} edits`, destructive: true })
      if (!accepted || !sameTarget() || latestGuard.current.working) return false
      if (discardNotice) { setNoticeEdit(null); setNoticeError('') }
      if (discardQueue) { setQueueEdit(null); setQueueError('') }
      // The caller may immediately recheck the registered guard before React
      // commits these state updates. Publish the exact discarded revisions now.
      latestGuard.current = { ...latestGuard.current,
        noticeEdit: discardNotice ? null : target.noticeEdit,
        queueEdit: discardQueue ? null : target.queueEdit }
      return true
    } finally { reviewingEdits.current = false }
  }, [confirm, flushComposer, lifetime])
  const decodeRoom = useCallback((value: unknown, context?: string) => {
    const decoded = parseManagedChatRoom(value, context)
    if (decoded.profileId.toLowerCase() !== profileId.toLowerCase())
      throw new ContractError('Chat returned a different server room.')
    if (hostId && decoded.hostId.toLowerCase() !== hostId.toLowerCase())
      throw new ContractError('Chat returned a different Host identity.')
    return decoded
  }, [profileId, hostId])
  const keepRoom = useCallback((next: ManagedChatRoom, notify = false) => {
    if (lifetime.current.signal.aborted) return
    const arriving = incomingTracker.observe(next, Date.now())
    const options = soundOptions.current
    setArrivingMessages(notify && options.visible ? arriving : [])
    if (notify && arriving.length && options.enabled && !options.quiet && options.visible && options.supported) soundPlayer.play()
    setRoomState({ base, room: next }); onRoomChange?.(next)
  }, [base, lifetime, incomingTracker, onRoomChange, soundPlayer])
  const readRoom = useCallback((signal: AbortSignal) => host ? getLocalJson(base, decodeRoom, signal) :
    changeJson(`${base}/sync`, 'POST', decodeRoom, undefined, signal), [base, host, decodeRoom])
  const refresh = useCallback(async (signal?: AbortSignal) => {
    const next = await readRoom(signal ?? lifetime.current.signal)
    if (signal?.aborted || lifetime.current.signal.aborted) return
    keepRoom(next, true)
    setError('')
  }, [readRoom, keepRoom, lifetime])
  const markNoticeCached = useCallback(() => {
    if (!host) setRoomState(current => current?.base === base ?
      { ...current, room: { ...current.room, noticeCached: true } } : current)
  }, [base, host])
  const refreshFailure = useCallback((failure: unknown) => {
    if (lifetime.current.signal.aborted) return
    incomingTracker.reset()
    setError(errorMessage(failure))
    markNoticeCached()
  }, [lifetime, markNoticeCached, incomingTracker])
  useSingleFlightPolling(refresh, 5000, refreshFailure,
    visible && supported, base)
  useLayoutEffect(() => () => {
    syncRequest.current?.abort()
    syncRequest.current = null
  }, [syncScope])
  useEffect(() => {
    onDraftStateChange?.({ dirty: draft.dirty || noticeDirty || queueDirty,
      saving: draft.working || busy || syncing, recovering: draft.recovering, flush: flushForNavigation })
  }, [draft.dirty, draft.working, draft.recovering, noticeDirty, queueDirty, busy, syncing, flushForNavigation, onDraftStateChange])
  useEffect(() => () => {
    // A guarded transition can unmount before the final clean-state effect.
    // Remove this room's guard before the next keyed room registers its own.
    draftGuardCallback.current?.({ dirty: false, saving: false, recovering: false, flush: async () => true })
  }, [])

  const messageIds = useMemo(() => room?.ok ? room.entries.map(entry => entry.id) : [], [room])
  const unread = unreadMessageCount(messageIds, lastReadId)
  const markRead = useCallback(() => {
    const latest = messageIds.at(-1)
    if (!latest || latest === lastReadId) return
    setLastReadId(latest); rememberLastMessage(scope, latest)
  }, [messageIds, lastReadId, scope])
  useEffect(() => { onUnreadChange?.(unread) }, [unread, onUnreadChange])
  useLayoutEffect(() => {
    if (!visible || !follow.current || !messages.current) return
    messages.current.scrollTop = messages.current.scrollHeight
    markRead()
  }, [visible, messageIds, markRead])

  function onScroll() {
    const element = messages.current
    if (!element) return
    const atBottom = element.scrollHeight - element.clientHeight - element.scrollTop <= 48
    follow.current = atBottom; setFollowing(atBottom)
    if (atBottom && visible) markRead()
  }

  function jumpLatest() {
    if (messages.current) messages.current.scrollTop = messages.current.scrollHeight
    follow.current = true; setFollowing(true); markRead()
  }

  async function syncNow() {
    if (busy || actionPending.current || !visible || !supported || syncRequest.current || lifetime.current.signal.aborted) return
    const request = new AbortController()
    syncRequest.current = request
    setSyncState({ scope: syncScope, status: 'pending', message: 'Syncing chat\u2026' })
    setError('')
    try {
      const next = await readRoom(request.signal)
      if (request.signal.aborted || lifetime.current.signal.aborted || syncRequest.current !== request) return
      keepRoom(next, true)
      setError('')
      // An offline Friend copy can be ok for reading without a completed Host
      // exchange. Only ChatSynced confirms that this request actually synced.
      const succeeded = next.ok && (host || next.code === 'ChatSynced')
      setSyncState({ scope: syncScope, status: succeeded ? 'success' : 'error',
        message: succeeded ? (host ? 'Chat refreshed.' : 'Chat synced.') :
          next.message || 'Chat could not be synced. Try again.' })
    } catch (failure) {
      if (request.signal.aborted || lifetime.current.signal.aborted || syncRequest.current !== request) return
      incomingTracker.reset()
      markNoticeCached()
      setSyncState({ scope: syncScope, status: 'error', message: `Sync failed. ${errorMessage(failure)}` })
    } finally {
      if (syncRequest.current === request) syncRequest.current = null
    }
  }

  async function send(event: React.FormEvent) {
    event.preventDefault()
    if (busy || actionPending.current || !draft.ready || !text.trim() || text.length > messageLimit) return
    actionPending.current = true; setBusy(true); setError('')
    try {
      if (!await draft.flush()) {
        if (!lifetime.current.signal.aborted)
          setError('Could not confirm that your draft was saved. Review the draft status before sending.')
        return
      }
      if (lifetime.current.signal.aborted) return
      const next = await changeJson(`${base}/messages`, 'POST', decodeRoom, { text: text.trim() }, lifetime.current.signal)
      if (lifetime.current.signal.aborted) return
      keepRoom(next)
      if (next.ok) await draft.accepted(text)
      setError(next.ok ? '' : next.message)
    } catch (failure) { if (!lifetime.current.signal.aborted) setError(errorMessage(failure)) }
    finally { actionPending.current = false; if (!lifetime.current.signal.aborted) setBusy(false) }
  }

  async function changeQueued(id: string, expectedText: string, nextText: string | null) {
    if (busy || actionPending.current || host || (nextText !== null && (!nextText.trim() || nextText.length > messageLimit))) return
    actionPending.current = true; setBusy(true); setQueueError('')
    try {
      const next = await changeJson(`${base}/queue/${encodeURIComponent(id)}${nextText === null ? '/cancel' : ''}`,
        nextText === null ? 'POST' : 'PUT', decodeRoom,
        nextText === null ? { expectedText } : { text: nextText.trim(), expectedText }, lifetime.current.signal)
      if (lifetime.current.signal.aborted) return
      if (next.ok) { keepRoom(next); setQueueEdit(null) }
      else setQueueError(next.message)
    } catch (failure) { if (!lifetime.current.signal.aborted) setQueueError(errorMessage(failure)) }
    finally { actionPending.current = false; if (!lifetime.current.signal.aborted) setBusy(false) }
  }

  async function changeMember(deviceId: string, allowed: boolean) {
    if (busy || actionPending.current || !host) return
    actionPending.current = true; setBusy(true); setError('')
    try {
      const next = await changeJson(`${base}/members/${deviceId}`, 'PUT',
        decodeRoom, { allowed }, lifetime.current.signal)
      if (lifetime.current.signal.aborted) return
      keepRoom(next)
      setError('')
    } catch (failure) { if (!lifetime.current.signal.aborted) setError(errorMessage(failure)) }
    finally { actionPending.current = false; if (!lifetime.current.signal.aborted) setBusy(false) }
  }

  function editNotice() {
    if (!host || !room?.ok || !room.noticeSupported) return
    setNoticeEdit({ base, text: room.notice?.text ?? '', expectedText: room.notice?.text ?? '', revision: room.notice?.revision ?? 0 })
    setNoticeError('')
  }

  async function saveNotice(event: React.FormEvent) {
    event.preventDefault()
    if (busy || !host || !editingNotice || !validPinnedNoticeText(editingNotice.text.trim())) return
    await changeNotice(editingNotice.text.trim(), editingNotice.revision)
  }

  async function changeNotice(nextText: string | null, expectedRevision: number) {
    if (busy || actionPending.current || !host || !room?.ok || !room.noticeSupported) return
    actionPending.current = true; setBusy(true); setNoticeError('')
    try {
      const next = await changeJson(`${base}/notice`, 'PUT', decodeRoom,
        { text: nextText, expectedRevision }, lifetime.current.signal)
      if (lifetime.current.signal.aborted) return
      keepRoom(next)
      if (next.ok) { setNoticeEdit(null); setNoticeError('') }
      else setNoticeError(next.message)
    } catch (failure) { if (!lifetime.current.signal.aborted) setNoticeError(errorMessage(failure)) }
    finally { actionPending.current = false; if (!lifetime.current.signal.aborted) setBusy(false) }
  }

  if (!supported) return <>{confirmationDialog}<p className="helper-text">Update the Host app to use server chat.</p></>
  const selfAuthorId = room ? chatAuthorId(room, host) : null
  const unlockSound = () => { if (soundEnabled && !quietMode) void soundPlayer.unlock() }
  const toggleSound = () => {
    const enabled = !soundEnabled
    setSoundEnabled(enabled); saveChatSoundPreference(scope, enabled)
    if (enabled && !quietMode) void soundPlayer.unlock()
    else soundPlayer.dispose()
  }
  return <section className="server-chat" aria-label="Server chat" onPointerDownCapture={unlockSound} onKeyDownCapture={unlockSound}>
    {confirmationDialog}
    <div className="server-chat-heading"><div className="server-chat-room-title"><span className="server-chat-room-icon"><Icon name="chat" size={22} /></span><div><h3>Server chat</h3>
      <p>Your room for plans, questions, and the next game.</p></div></div>
      <div className="server-chat-sync">
        <Button className="secondary server-chat-sound" aria-pressed={soundEnabled}
          aria-label={soundEnabled ? 'Mute chat sounds' : 'Unmute chat sounds'}
          title={quietMode ? 'Quiet mode pauses chat sounds.' : 'Play a soft sound for new messages in this room.'} onClick={toggleSound}>
          <Icon name="bell" />{soundEnabled ? quietMode ? 'Sound paused' : 'Sound on' : 'Sound off'}</Button>
        <Button className="secondary" disabled={busy || syncing || !visible} aria-busy={syncing} aria-label={syncing ? 'Syncing\u2026' : 'Sync now'}
          aria-describedby={syncFeedbackId} onClick={() => void syncNow()}>
          <Icon name={syncing ? 'loader' : 'refresh'} />{syncing ? 'Syncing\u2026' : 'Sync'}</Button>
        {currentSync && currentSync.status !== 'pending' ? <DismissibleNotice as="span" scope={scope} id={syncFeedbackId} className={`server-chat-sync-feedback${currentSync.status === 'error' ? ' warning-text' : ''}`}
          role={currentSync?.status === 'error' ? 'alert' : 'status'} aria-label="Chat sync" aria-atomic="true">
          {currentSync?.status === 'success' && <Icon name="check" />}
          {currentSync?.status === 'error' && <Icon name="warning" />}{currentSync?.message}
        </DismissibleNotice> : <span id={syncFeedbackId} className="server-chat-sync-feedback" role="status" aria-label="Chat sync" aria-atomic="true">{currentSync?.message}</span>}
      </div></div>
    {room && !room.ok && <DismissibleNotice as="p" scope={scope} className="warning-text" role="status">{room.message}</DismissibleNotice>}
    {error && <DismissibleNotice as="p" scope={scope} className="warning-text" role="alert">{error}</DismissibleNotice>}
    {room && <section className="server-chat-notice" aria-label="Pinned notice">
      <div className="server-chat-notice-heading"><strong>Pinned notice</strong>
        {host && room.ok && room.noticeSupported && !editingNotice &&
          <Button className="secondary" disabled={busy} onClick={editNotice}>
            {room.notice?.text ? 'Edit notice' : 'Add notice'}</Button>}
      </div>
      {room.notice?.text ? <><p>{room.notice.text}</p>
        <small>{!host && room.noticeCached ? 'Saved on this PC · ' : ''}Updated{' '}
          <time dateTime={room.notice.updatedUtc}>{new Date(room.notice.updatedUtc).toLocaleString()}</time></small>
      </> : <p className="helper-text">{room.notice?.text === null ?
        `${!host && room.noticeCached ? 'Saved on this PC: ' : ''}The Host cleared the notice.` : 'No pinned notice.'}</p>}
      {!host && !room.noticeSupported && room.code === 'ChatSynced' &&
        <p className="helper-text">Update the Host app to see current pinned notices.</p>}
      {editingNotice && <form className="server-chat-notice-editor" onSubmit={event => void saveNotice(event)}>
        <label htmlFor={`notice-${profileId}`}>Notice text</label>
        <TextArea id={`notice-${profileId}`} value={editingNotice.text} maxLength={maximumNoticeTextLength} rows={4}
          placeholder="Rules, maintenance, or what changed" disabled={busy}
          onChange={event => setNoticeEdit({ ...editingNotice, text: event.target.value })} />
        <div className="server-chat-edit-actions"><small>{editingNotice.text.length}/{maximumNoticeTextLength}</small>
          <Button type="submit" disabled={busy || !validPinnedNoticeText(editingNotice.text.trim())}>Save notice</Button>
          <Button className="secondary" disabled={busy} onClick={() => { setNoticeEdit(null); setNoticeError('') }}>Cancel</Button>
          {room.notice?.text && <Button className="secondary" disabled={busy}
            onClick={() => void changeNotice(null, editingNotice.revision)}>Clear notice</Button>}
        </div>
      </form>}
      {noticeError && <DismissibleNotice as="p" scope={scope} className="warning-text" role="alert">{noticeError}</DismissibleNotice>}
    </section>}
    <div ref={messages} className="server-chat-messages" role="log" aria-live={following ? 'polite' : 'off'}
      aria-label="Messages" tabIndex={0} onScroll={onScroll}>
      {!room && !error && <div className="server-chat-empty" role="status"><Icon name="loader" size={25} /><strong>Opening your chat…</strong><p>Checking this room and its saved messages.</p></div>}
      {room?.ok && !room.entries.length && !room.pending.length && <div className="server-chat-empty"><Icon name="chat" size={30} /><strong>No messages yet.</strong><p>Plan a session, ask a question, or say hello.</p></div>}
      {room?.entries.map((entry, index, entries) => {
        const previous = entries[index - 1]
        const date = new Date(entry.sentUtc)
        const previousDate = previous ? new Date(previous.sentUtc) : null
        const sameDay = previousDate?.toDateString() === date.toDateString()
        const grouped = sameDay && previous?.authorId.toLowerCase() === entry.authorId.toLowerCase() &&
          date.getTime() - previousDate!.getTime() >= 0 && date.getTime() - previousDate!.getTime() < 300_000
        const own = selfAuthorId === entry.authorId.toLowerCase()
        return <div className="server-chat-entry" key={entry.id}>
          {!sameDay && <div className="server-chat-day"><span>{date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })}</span></div>}
          <div className="server-chat-row" data-own={own} data-grouped={grouped}>
            <span className="server-chat-avatar" aria-hidden="true">{own ? 'Y' : Array.from(entry.author.trim())[0]?.toLocaleUpperCase() || '?'}</span>
            <div className="server-chat-message" data-own={own} data-arriving={arrivingMessages.includes(entry.id)}>
              <div className="server-chat-message-meta"><strong className={grouped ? 'sr-only' : undefined}>{own ? 'You' : entry.author}</strong>
                <time dateTime={entry.sentUtc} title={date.toLocaleString()}>{date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })}</time></div>
              <p>{entry.text}</p>
            </div>
          </div>
        </div>
      })}
      {room?.pending.map((queued, index) => <div className="server-chat-message pending" data-own="true" key={queued.id}>
        <div><strong>You</strong><small>{queued.submitted ? 'Sending; waiting for Host confirmation' : 'Not sent; waiting to connect'}</small></div><p>{queued.text}</p>
        {!host && room.ok && !queued.submitted && <div>
          <Button className="secondary" disabled={busy} aria-label={`Edit queued message ${index + 1}`}
            onClick={() => { setQueueEdit({ id: queued.id, text: queued.text, expectedText: queued.text }); setQueueError('') }}>Edit</Button>
          <Button className="secondary" disabled={busy} aria-label={`Cancel queued message ${index + 1}`}
            onClick={() => void changeQueued(queued.id, queued.text, null)}>Cancel queued message</Button>
        </div>}
      </div>)}
    </div>
    {!following && <div className="server-chat-latest" role="status">
      <Button className="secondary" onClick={jumpLatest}>{unread > 0 ? `New messages (${unread}) · Jump to latest` : 'Jump to latest'}</Button>
    </div>}
    {!host && room?.pending.length ? <small>Queue: {room.pending.length}/20. Only unsent messages can be edited or canceled. Sent messages await confirmation and keep their original text.</small> : null}
    {queueEdit && <form className="server-chat-compose" aria-label="Edit queued message" onSubmit={event => {
      event.preventDefault(); void changeQueued(queueEdit.id, queueEdit.expectedText, queueEdit.text)
    }}>
      <label htmlFor={`queued-${profileId}`}>Queued message</label>
      <TextArea id={`queued-${profileId}`} value={queueEdit.text} maxLength={messageLimit} rows={3}
        disabled={busy || !room?.pending.some(queued => queued.id === queueEdit.id && !queued.submitted)}
        onChange={event => setQueueEdit({ ...queueEdit, text: event.target.value })} />
      {!room?.pending.some(queued => queued.id === queueEdit.id && !queued.submitted) &&
        <p className="helper-text">This message was submitted and can no longer be edited. Sync to check delivery.</p>}
      <Button type="submit" disabled={busy || !queueEdit.text.trim() || queueEdit.text.length > messageLimit ||
        !room?.pending.some(queued => queued.id === queueEdit.id && !queued.submitted)}>Save queued message</Button>
      <Button className="secondary" disabled={busy} onClick={() => setQueueEdit(null)}>Keep queued message</Button>
    </form>}
    {queueError && <DismissibleNotice as="p" scope={scope} className="warning-text" role="alert">{queueError}</DismissibleNotice>}
    {room?.ok && draft.recovering && <DismissibleNotice as="section" scope={scope} noticeKey={draft.recovered ?? undefined} aria-label="Saved draft">
      <strong>Review your saved draft</strong><p>{draft.recovered}</p>
      <Button className="secondary" disabled={busy || draft.working} onClick={draft.resume}>Use saved draft</Button>
      <Button className="secondary" disabled={busy || draft.working} onClick={() => void draft.discard()}>Discard saved draft</Button>
    </DismissibleNotice>}
    {room?.ok && <form className="server-chat-compose" onSubmit={event => void send(event)}>
      <label htmlFor={`chat-${profileId}`}>Message</label>
      <TextArea id={`chat-${profileId}`} value={text} maxLength={500} rows={3}
        placeholder="Ask about an issue or share an update" disabled={busy || !draft.ready}
        onChange={event => draft.change(event.target.value)} />
      <div className="server-chat-compose-footer"><small>{text.length}/500<span className="server-chat-compose-help"> · For this server’s room.</span></small><Button type="submit" disabled={busy || !draft.ready || !text.trim() || text.length > messageLimit}><Icon name={busy ? 'loader' : 'chat'} />Send</Button></div>
      {!draft.ready && !draft.recovering && !draft.feedback && <small role="status">Checking for a saved draft…</small>}
      {(draft.dirty || draft.feedback) && (draft.dirty && !draft.failed ? <small role="status">Saving draft…</small> :
        <DismissibleNotice as="small" scope={scope} role={draft.failed ? 'alert' : 'status'}>{draft.feedback}</DismissibleNotice>)}
      {!draft.ready && !draft.recovering && draft.feedback && <Button className="secondary" disabled={draft.working}
        onClick={draft.retry}>Retry saving draft</Button>}
    </form>}
    {host && room?.members && <details className="server-chat-members"><summary>People in this room ({room.members.filter(member => member.allowed).length})</summary>
      {room.members.length === 0 ? <p className="helper-text">Invite a friend and assign this server to add them.</p> :
        <ul>{room.members.map(member => <li key={member.deviceId}><span>{member.name}</span>
          <Button className="secondary" disabled={busy} onClick={() => void changeMember(member.deviceId, !member.allowed)}>
            {member.allowed ? 'Remove' : 'Add'}</Button></li>)}</ul>}
      <small>Removing someone stops new messages reaching their PC. Messages already copied there cannot be recalled.</small>
    </details>}
  </section>
}

export function PinnedNoticePreview({ room, authorized, onOpenChat }: {
  room: ChatRoomSummary | ChatRoomWithNotice | null
  authorized: boolean
  onOpenChat?: () => void
}) {
  if (!authorized || !room?.ok || !room.notice?.text) return null
  const text = room.notice.text.replace(/\s+/g, ' ').trim()
  return <section className="server-chat-notice-preview" aria-label="Pinned notice preview">
    <strong>Pinned notice</strong><p>{text.length > 180 ? `${text.slice(0, 180)}…` : text}</p>
    <small>{room.noticeCached ? 'Saved on this PC · ' : ''}Updated{' '}
      <time dateTime={room.notice.updatedUtc}>{new Date(room.notice.updatedUtc).toLocaleString()}</time></small>
    {onOpenChat && <Button className="secondary" onClick={onOpenChat}>Read notice in chat</Button>}
  </section>
}

type ChatCardProps = {
  profileId: string; host: boolean; connectionId?: string | null; hostId?: string | null
  authorized: boolean; supported?: boolean; visible?: boolean; roomOpen?: boolean
  pollWhenClosed?: boolean; room?: ManagedChatRoom | null
  onUnreadChange?: (count: number) => void; onOpenChat?: () => void
}

export function ServerChatCardSummary(props: ChatCardProps) {
  return <ChatCardSummary key={roomScope(props.profileId, props.host, props.connectionId)} {...props} />
}

function ChatCardSummary({ profileId, host, connectionId, hostId, authorized, supported = true,
  visible = true, roomOpen = false, pollWhenClosed = false, room: suppliedRoom,
  onUnreadChange, onOpenChat }: ChatCardProps) {
  const base = chatBase(profileId, host, connectionId)
  const scope = roomScope(profileId, host, connectionId)
  const [summary, setSummary] = useState<ChatRoomSummary | null>(null)
  const [lastReadId, setLastReadId] = useState(() => readLastMessage(scope))
  const [failed, setFailed] = useState(false)
  const [blocked, setBlocked] = useState(false)
  const readSummary = useCallback(async (signal: AbortSignal) => {
    const next = await getLocalJson(`${base}/summary`, parseChatRoomSummary, signal)
    if (signal.aborted) return
    if (next.profileId.toLowerCase() !== profileId.toLowerCase() ||
        hostId && next.hostId.toLowerCase() !== hostId.toLowerCase())
      throw new ContractError('The chat summary returned a different room.')
    setSummary(next); setFailed(false); setBlocked(!next.ok)
  }, [base, profileId, hostId])
  const onFailure = useCallback((failure: unknown) => {
    setFailed(true)
    if (failure instanceof ApiError && (failure.status === 403 ||
        ['ConnectionChanged', 'NotPaired', 'ConnectionClosed', 'UnknownConnection'].includes(failure.code))) {
      setSummary(null); setBlocked(true)
    }
  }, [])
  useSingleFlightPolling(readSummary, 15000, onFailure,
    pollWhenClosed && !roomOpen && visible && authorized && supported && (host || !!connectionId), base)
  useEffect(() => {
    const changed = (event: Event) => {
      const detail = (event as CustomEvent<{ scope: string; id: string }>).detail
      if (detail?.scope === scope && messageIdPattern.test(detail.id)) setLastReadId(detail.id)
    }
    window.addEventListener(readEvent, changed)
    return () => window.removeEventListener(readEvent, changed)
  }, [scope])
  const matchingRoom = suppliedRoom && suppliedRoom.profileId.toLowerCase() === profileId.toLowerCase() &&
    (!hostId || suppliedRoom.hostId.toLowerCase() === hostId.toLowerCase()) ? suppliedRoom : null
  const current = matchingRoom ?? summary
  const unread = authorized && supported && !blocked && current?.ok
    ? unreadMessageCount(matchingRoom?.entries.map(entry => entry.id) ?? summary?.messageIds ?? [], lastReadId) : 0
  useEffect(() => { onUnreadChange?.(unread) }, [onUnreadChange, unread])
  if (!authorized || !supported || blocked) return null
  const displayed = current && failed && !host ? { ...current, noticeCached: true } : current
  return <div className="server-chat-card-summary">
    {unread > 0 && <Button className="secondary" onClick={onOpenChat} disabled={!onOpenChat} aria-label={`${unread} unread chat messages`}>
      Chat · {unread} unread</Button>}
    <PinnedNoticePreview room={displayed} authorized={authorized} onOpenChat={onOpenChat} />
    {failed && <DismissibleNotice as="small" scope={scope} role="status">Could not refresh chat preview. Open chat to check access and new messages.</DismissibleNotice>}
  </div>
}
