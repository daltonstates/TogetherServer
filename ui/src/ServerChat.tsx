import { useCallback, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, TextArea } from './Controls'
import { parseChatRoomView, type ChatRoomView } from './contracts'
import { useSingleFlightPolling } from './hooks/useSingleFlightPolling'

export function ServerChat({ profileId, host, visible, supported = true }: {
  profileId: string; host: boolean; visible: boolean; supported?: boolean
}) {
  const base = host ? `/api/local/profiles/${profileId}/chat` : `/api/local/friend/${profileId}/chat`
  const [room, setRoom] = useState<ChatRoomView | null>(null)
  const [text, setText] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const refresh = useCallback(async (signal?: AbortSignal) => {
    const next = host ? await getLocalJson(base, parseChatRoomView, signal) :
      await changeJson(`${base}/sync`, 'POST', parseChatRoomView, undefined, signal)
    setRoom(next)
    setError('')
  }, [base, host])
  useSingleFlightPolling(refresh, 5000, failure => setError(errorMessage(failure)),
    visible && supported, base)

  async function send(event: React.FormEvent) {
    event.preventDefault()
    if (busy || !text.trim()) return
    setBusy(true)
    try {
      const next = await changeJson(`${base}/messages`, 'POST', parseChatRoomView, { text: text.trim() })
      setRoom(next)
      if (next.ok) setText('')
      setError(next.ok ? '' : next.message)
    } catch (failure) { setError(errorMessage(failure)) }
    finally { setBusy(false) }
  }

  async function changeMember(deviceId: string, allowed: boolean) {
    setBusy(true)
    try {
      const next = await changeJson(`${base}/members/${deviceId}`, 'PUT',
        parseChatRoomView, { allowed })
      setRoom(next)
      setError('')
    } catch (failure) { setError(errorMessage(failure)) }
    finally { setBusy(false) }
  }

  if (!supported) return <p className="helper-text">Update the Host app to use server chat.</p>
  return <section className="server-chat" aria-label="Server chat">
    <div className="server-chat-heading"><div><h3>Server chat</h3>
      <p>Talk about this server with the people who have access to its room.</p></div>
      <Button className="secondary" disabled={busy} onClick={() => void refresh()}>Sync now</Button></div>
    {room && !room.ok && <p className="warning-text" role="status">{room.message}</p>}
    {error && <p className="warning-text" role="alert">{error}</p>}
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
