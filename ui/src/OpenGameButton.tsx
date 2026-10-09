import { useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage } from './api'
import { Button } from './Controls'
import { parseBasicResult } from './contracts'

export function OpenGameButton({ profileId, kind, available }: { profileId: string; kind: string; available: boolean }) {
  const [busy, setBusy] = useState(false), [message, setMessage] = useState(''), [failed, setFailed] = useState(false)
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])
  if (!['Valheim', 'Factorio', 'Terraria'].includes(kind)) return <p className="helper-text">Open {kind === 'MinecraftJava' || kind === 'MinecraftBedrock' ? 'Minecraft from Windows Start or your own launcher, then choose Multiplayer or Servers' : 'your game yourself'}. Use Copy beside Server IP to enter the address.</p>
  async function open() {
    if (busy || !available) return
    const controller = new AbortController(); pending.current = controller
    setBusy(true)
    try {
      const result = await changeJson(`/api/local/friend/${profileId}/open-game`, 'POST', parseBasicResult, undefined, controller.signal)
      if (controller.signal.aborted) return
      setMessage(result.message); setFailed(!result.ok)
    } catch (failure) { if (!controller.signal.aborted) { setMessage(errorMessage(failure)); setFailed(true) } }
    finally { if (!controller.signal.aborted) setBusy(false) }
  }
  return <div className="open-game-action"><Button className="secondary" disabled={busy || !available} onClick={() => void open()}>{busy ? 'Opening game…' : 'Open game'}</Button>
    <small>Opens the game through Steam. Copy the server address and join in the game.</small>
    {message && <p className={failed ? 'error-text' : 'helper-text'} role={failed ? 'alert' : 'status'}>{message}</p>}
  </div>
}
