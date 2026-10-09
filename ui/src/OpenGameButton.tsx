import { useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage } from './api'
import { Button } from './Controls'
import { parseBasicResult } from './contracts'

export type OpenGameButtonProps = {
  profileId: string
  kind: string
  available: boolean
  connectionId?: string
  identityKey?: string | number
  disabledReason?: string
}

function ScopedOpenGameButton({ profileId, kind, available, disabledReason }: OpenGameButtonProps) {
  const [busy, setBusy] = useState(false), [message, setMessage] = useState(''), [failed, setFailed] = useState(false)
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])
  if (!['Valheim', 'Factorio', 'Terraria'].includes(kind)) return <p className="helper-text">Open {kind === 'MinecraftJava' || kind === 'MinecraftBedrock' ? 'Minecraft from Windows Start or your own launcher, then choose Multiplayer or Servers' : 'your game yourself'}. {kind === 'MinecraftBedrock' ? 'Copy Server address and Port into their separate fields.' : 'Use Copy beside Server IP to enter the address.'}</p>
  async function open() {
    if (pending.current || busy || !available) return
    const controller = new AbortController(); pending.current = controller
    setBusy(true)
    try {
      const result = await changeJson(`/api/local/friend/${encodeURIComponent(profileId)}/open-game`, 'POST', parseBasicResult, undefined, controller.signal)
      if (controller.signal.aborted) return
      setMessage(result.message); setFailed(!result.ok)
    } catch (failure) { if (!controller.signal.aborted) { setMessage(errorMessage(failure)); setFailed(true) } }
    finally { if (pending.current === controller) pending.current = null; if (!controller.signal.aborted) setBusy(false) }
  }
  return <div className="open-game-action"><Button className="secondary" disabled={busy || !available} onClick={() => void open()}>{busy ? 'Opening game…' : 'Open game'}</Button>
    <small>Opens an installed game through Steam. Join in the game; opening it does not join the server.</small>
    {!available && <small>{disabledReason ?? 'Reconnect for fresh access and wait for the server before opening the game.'}</small>}
    {message && <p className={failed ? 'error-text' : 'helper-text'} role={failed ? 'alert' : 'status'}>{message}</p>}
  </div>
}

export function OpenGameButton(props: OpenGameButtonProps) {
  const scope = JSON.stringify([props.connectionId, props.profileId, props.kind, props.identityKey, props.available])
  return <ScopedOpenGameButton key={scope} {...props} />
}
