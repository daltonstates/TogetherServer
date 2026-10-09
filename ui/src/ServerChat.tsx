import { useCallback, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, TextArea } from './Controls'
import { ContractError } from './contracts'
import { maximumNoticeTextLength, parseChatRoomWithNotice, validPinnedNoticeText,
  type ChatRoomWithNotice } from './pinnedNoticeContracts'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'

export function ServerChat({ profileId, host, visible, supported = true }: {
  profileId: string; host: boolean; visible: boolean; supported?: boolean
}) {
  const base = host ? `/api/local/profiles/${profileId}/chat` : `/api/local/friend/${profileId}/chat`
  const [roomState, setRoomState] = useState<{ base: string; room: ChatRoomWithNotice } | null>(null)
  const room = roomState?.base === base ? roomState.room : null
  const [text, setText] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [noticeEdit, setNoticeEdit] = useState<{ base: string; text: string; revision: number } | null>(null)
  const [noticeError, setNoticeError] = useState('')
  const editingNotice = noticeEdit?.base === base ? noticeEdit : null
  const decodeRoom = useCallback((value: unknown, context?: string) => {
    const decoded = parseChatRoomWithNotice(value, context)
    if (decoded.profileId.toLowerCase() !== profileId.toLowerCase())
      throw new ContractError('Chat returned a different server room.')
    return decoded
  }, [profileId])
  const keepRoom = useCallback((next: ChatRoomWithNotice) => setRoomState({ base, room: next }), [base])
  const refresh = useCallback(async (signal?: AbortSignal) => {
    const next = host ? await getLocalJson(base, decodeRoom, signal) :
      await changeJson(`${base}/sync`, 'POST', decodeRoom, undefined, signal)
    keepRoom(next)
    setError('')
  }, [base, host, decodeRoom, keepRoom])
  const refreshFailure = useCallback((failure: unknown) => {
    setError(errorMessage(failure))
    if (!host) setRoomState(current => current?.base === base ?
      { ...current, room: { ...current.room, noticeCached: true } } : current)
  }, [base, host])
  useSingleFlightPolling(refresh, 5000, refreshFailure,
    visible && supported, base)

  async function send(event: React.FormEvent) {
    event.preventDefault()
    if (busy || !text.trim()) return
    setBusy(true)
    try {
      const next = await changeJson(`${base}/messages`, 'POST', decodeRoom, { text: text.trim() })
      keepRoom(next)
      if (next.ok) setText('')
      setError(next.ok ? '' : next.message)
    } catch (failure) { setError(errorMessage(failure)) }
    finally { setBusy(false) }
  }

  async function changeMember(deviceId: string, allowed: boolean) {
    setBusy(true)
    try {
      const next = await changeJson(`${base}/members/${deviceId}`, 'PUT',
        decodeRoom, { allowed })
      keepRoom(next)
      setError('')
    } catch (failure) { setError(errorMessage(failure)) }
    finally { setBusy(false) }
  }

  function editNotice() {
    if (!host || !room?.ok || !room.noticeSupported) return
    setNoticeEdit({ base, text: room.notice?.text ?? '', revision: room.notice?.revision ?? 0 })
    setNoticeError('')
  }

  async function saveNotice(event: React.FormEvent) {
    event.preventDefault()
    if (busy || !host || !editingNotice || !validPinnedNoticeText(editingNotice.text.trim())) return
    await changeNotice(editingNotice.text.trim(), editingNotice.revision)
  }

  async function changeNotice(nextText: string | null, expectedRevision: number) {
    if (busy || !host || !room?.ok || !room.noticeSupported) return
    setBusy(true)
    try {
      const next = await changeJson(`${base}/notice`, 'PUT', decodeRoom,
        { text: nextText, expectedRevision })
      keepRoom(next)
      if (next.ok) { setNoticeEdit(null); setNoticeError('') }
      else setNoticeError(next.message)
    } catch (failure) { setNoticeError(errorMessage(failure)) }
    finally { setBusy(false) }
  }

  if (!supported) return <p className="helper-text">Update the Host app to use server chat.</p>
  return <section className="server-chat" aria-label="Server chat">
    <div className="server-chat-heading"><div><h3>Server chat</h3>
      <p>Talk about this server with the people who have access to its room.</p></div>
      <Button className="secondary" disabled={busy} onClick={() => void refresh().catch(refreshFailure)}>Sync now</Button></div>
    {room && !room.ok && <p className="warning-text" role="status">{room.message}</p>}
    {error && <p className="warning-text" role="alert">{error}</p>}
    {room && <section className="server-chat-notice" aria-label="Pinned notice">
      <div className="server-chat-notice-heading"><strong>Pinned notice</strong>
        {host && room.ok && room.noticeSupported && !editingNotice &&
          <Button className="secondary" disabled={busy} onClick={editNotice}>
            {room.notice?.text ? 'Edit notice' : 'Add notice'}</Button>}
      </div>
      {room.notice?.text ? <><p>{room.notice.text}</p>
        <small>{!host && room.noticeCached ? 'Cached on this PC · ' : ''}Updated{' '}
          <time dateTime={room.notice.updatedUtc}>{new Date(room.notice.updatedUtc).toLocaleString()}</time></small>
      </> : <p className="helper-text">{room.notice?.text === null ?
        `${!host && room.noticeCached ? 'Cached on this PC: ' : ''}The Host cleared the notice.` : 'No pinned notice.'}</p>}
      {!host && !room.noticeSupported && room.code === 'ChatSynced' &&
        <p className="helper-text">Update the Host app to see current pinned notices.</p>}
      {editingNotice && <form className="server-chat-notice-editor" onSubmit={event => void saveNotice(event)}>
        <label htmlFor={`notice-${profileId}`}>Notice text</label>
        <TextArea id={`notice-${profileId}`} value={editingNotice.text} maxLength={maximumNoticeTextLength} rows={4}
          placeholder="Rules, maintenance, or what changed" disabled={busy}
          onChange={event => setNoticeEdit({ ...editingNotice, text: event.target.value })} />
        <div><small>{editingNotice.text.length}/{maximumNoticeTextLength}</small>
          <Button type="submit" disabled={busy || !validPinnedNoticeText(editingNotice.text.trim())}>Save notice</Button>
          <Button className="secondary" disabled={busy} onClick={() => { setNoticeEdit(null); setNoticeError('') }}>Cancel</Button>
          {room.notice?.text && <Button className="secondary" disabled={busy}
            onClick={() => void changeNotice(null, editingNotice.revision)}>Clear notice</Button>}
        </div>
      </form>}
      {noticeError && <p className="warning-text" role="alert">{noticeError}</p>}
    </section>}
    <div className="server-chat-messages" role="log" aria-live="polite" aria-label="Messages">
      {!room?.entries.length && <p className="helper-text">No messages yet.</p>}
      {room?.entries.map(entry => <div className="server-chat-message" key={entry.id}>
        <div><strong>{entry.author}</strong><time dateTime={entry.sentUtc}>{new Date(entry.sentUtc).toLocaleString()}</time></div>
        <p>{entry.text}</p>
      </div>)}
      {room?.pending.map(draft => <div className="server-chat-message pending" key={draft.id}>
        <div><strong>You</strong><small>Waiting to sync</small></div><p>{draft.text}</p>
      </div>)}
    </div>
    {room?.ok && <form className="server-chat-compose" onSubmit={event => void send(event)}>
      <label htmlFor={`chat-${profileId}`}>Message</label>
      <TextArea id={`chat-${profileId}`} value={text} maxLength={500} rows={3}
        placeholder="Ask about an issue or share an update"
        onChange={event => setText(event.target.value)} />
      <div><small>{text.length}/500</small><Button type="submit" disabled={busy || !text.trim()}>Send</Button></div>
    </form>}
    {host && room?.members && <details className="server-chat-members"><summary>People in this room ({room.members.filter(member => member.allowed).length})</summary>
      {room.members.length === 0 ? <p className="helper-text">Invite a friend and assign this server to add them.</p> :
        <ul>{room.members.map(member => <li key={member.deviceId}><span>{member.name}</span>
          <Button className="secondary" disabled={busy} onClick={() => void changeMember(member.deviceId, !member.allowed)}>
            {member.allowed ? 'Remove' : 'Add'}</Button></li>)}</ul>}
      <small>Removing someone stops new chat sync. Messages already copied to their PC cannot be recalled.</small>
    </details>}
  </section>
}
