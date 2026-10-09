import { useEffect, useRef, useState } from 'react'
import { Button, Input } from './Controls'
import { futureHostFields, type SharedGame } from './sharedWorldUx'

export function SharedWorldServerFile({ profileId, game, value, disabled, onChange, onBrowseServerFile, id }:
  { profileId: string; game?: SharedGame; value: string; disabled?: boolean; id?: string;
    onChange: (path: string) => void; onBrowseServerFile?: (game: SharedGame) => Promise<string | null> }) {
  const [choosing, setChoosing] = useState(false)
  const [message, setMessage] = useState('')
  const scope = useRef({ profileId, game })
  if (scope.current.profileId !== profileId || scope.current.game !== game) scope.current = { profileId, game }
  const mounted = useRef(true)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  useEffect(() => { setChoosing(false); setMessage('') }, [profileId, game])
  const fields = futureHostFields(game)
  const browse = async () => {
    if (!onBrowseServerFile) return
    const requestScope = scope.current
    setChoosing(true); setMessage('')
    try {
      const chosen = await onBrowseServerFile(game ?? 'Unknown')
      if (mounted.current && scope.current === requestScope && chosen !== null) {
        onChange(chosen); setMessage('Review this local path and matching game version before checking this PC.')
      }
    } catch {
      if (mounted.current && scope.current === requestScope)
        setMessage('The server file could not be selected. Enter its local path or try Browse again.')
    } finally {
      if (mounted.current && scope.current === requestScope) setChoosing(false)
    }
  }
  return <div>
    <label>{fields.serverFileLabel}<Input id={id} value={value} disabled={disabled || choosing}
      onChange={event => onChange(event.target.value)} placeholder="Path to installed server file" /></label>
    <p className="helper-text">{fields.serverFileHint}</p>
    {onBrowseServerFile && <Button className="secondary" disabled={disabled || choosing} onClick={() => void browse()}>
      {choosing ? 'Choosing file…' : 'Browse this PC'}</Button>}
    {message && <p role="status">{message}</p>}
  </div>
}
