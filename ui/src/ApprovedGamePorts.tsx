import { useCallback, useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input, Select } from './Controls'
import { parseBasicResult, type BasicResult } from './contracts'
import { Icon } from './Icon'
import './ApprovedGamePorts.css'

const games = ['Valheim', 'MinecraftJava', 'MinecraftBedrock', 'Factorio', 'Terraria', 'Fixture'] as const
export type ApprovedPortGame = typeof games[number]
export type ApprovedGamePort = { id: string; game: ApprovedPortGame; gamePort: number; factorioRconPort: number | null }
export type ApprovedGamePortInput = Omit<ApprovedGamePort, 'id'>
export type ApprovedGamePortsResult = BasicResult & { approvedPorts: ApprovedGamePort[] }
const endpoint = '/api/local/approved-game-ports'
const guid = /^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const gameLabels: Record<ApprovedPortGame, string> = {
  Valheim: 'Valheim', MinecraftJava: 'Minecraft Java', MinecraftBedrock: 'Minecraft Bedrock', Factorio: 'Factorio', Terraria: 'Terraria', Fixture: 'Test fixture'
}
export function validGamePorts(game: string, gamePort: number, factorioRconPort: number | null): boolean {
  const port = (value: number | null) => value !== null && Number.isInteger(value) && value >= 1024 && value <= 65535
  return games.includes(game as ApprovedPortGame) && port(gamePort) && (game !== 'Valheim' || gamePort < 65535) &&
    (game === 'Factorio' ? port(factorioRconPort) && factorioRconPort !== gamePort : factorioRconPort === null)
}
export function parseApprovedGamePorts(value: unknown): ApprovedGamePort[] {
  if (!Array.isArray(value) || value.length > 128) throw new Error('Approved game ports could not be read.')
  const plans = value.map(item => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new Error('Approved game ports could not be read.')
    const plan = item as Record<string, unknown>
    if (typeof plan.id !== 'string' || !guid.test(plan.id) || typeof plan.game !== 'string' ||
      typeof plan.gamePort !== 'number' || plan.factorioRconPort !== null && typeof plan.factorioRconPort !== 'number' ||
      !validGamePorts(plan.game, plan.gamePort, plan.factorioRconPort as number | null)) throw new Error('Approved game ports could not be read.')
    return plan as ApprovedGamePort
  })
  if (new Set(plans.map(plan => plan.id.toLowerCase())).size !== plans.length ||
    new Set(plans.map(plan => JSON.stringify([plan.game, plan.gamePort, plan.factorioRconPort]))).size !== plans.length)
    throw new Error('Approved game ports could not be read.')
  return plans
}
export function parseApprovedGamePortsView(value: unknown): { approvedPorts: ApprovedGamePort[] } {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Approved game ports could not be read.')
  return { approvedPorts: parseApprovedGamePorts((value as Record<string, unknown>).approvedPorts) }
}
export function parseApprovedGamePortsResult(value: unknown): ApprovedGamePortsResult {
  return { ...parseBasicResult(value), ...parseApprovedGamePortsView(value) }
}
export function approvedGamePortLabel(plan: ApprovedGamePort): string {
  return `${gameLabels[plan.game]} · ${plan.gamePort}${plan.game === 'Valheim' ? `–${plan.gamePort + 1}` : ''}${
    plan.factorioRconPort === null ? '' : ` · private RCON ${plan.factorioRconPort}`}`
}
function portNumber(value: string): number | null { return /^\d{1,5}$/.test(value) ? Number(value) : null }

export type LocalHostingPortChoice = { approvedPortId: string } | { gamePort: number; factorioRconPort: number | null; approvePorts: true }
export function localHostingPortChoice({ game, choice, plans, gamePort, rconPort, approved }: {
  game: string; choice: string; plans: ApprovedGamePort[]; gamePort: string; rconPort: string; approved: boolean
}): LocalHostingPortChoice | null {
  if (choice !== 'new') return plans.some(plan => plan.id === choice && plan.game === game) ? { approvedPortId: choice } : null
  const port = portNumber(gamePort)
  const rcon = game === 'Factorio' ? portNumber(rconPort) : null
  return approved && port !== null && validGamePorts(game, port, rcon) ? { gamePort: port, factorioRconPort: rcon, approvePorts: true } : null
}

export function ApprovedGamePortsPanel({ disabled = false, active = true, onBusyChange }: {
  disabled?: boolean; active?: boolean; onBusyChange?: (busy: boolean) => void
}) {
  const [plans, setPlans] = useState<ApprovedGamePort[] | null>(null)
  const [pending, setPending] = useState(false)
  const [reading, setReading] = useState(false)
  const [needsRefresh, setNeedsRefresh] = useState(false)
  const [notice, setNotice] = useState<{ error: boolean; text: string } | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [game, setGame] = useState<ApprovedPortGame>('Valheim')
  const [gamePort, setGamePort] = useState('')
  const [rconPort, setRconPort] = useState('')
  const controller = useRef<AbortController | null>(null)
  const loaded = useRef(false)
  const mutation = useRef(false)
  const readingRequest = useRef(false)
  const verified = useRef(false)
  const requestTimeout = useRef<number | undefined>(undefined)
  const clearDraft = () => { setEditing(null); setGamePort(''); setRconPort('') }
  useEffect(() => { onBusyChange?.(pending); return () => onBusyChange?.(false) }, [pending, onBusyChange])
  useEffect(() => () => { controller.current?.abort(); window.clearTimeout(requestTimeout.current) }, [])
  const read = useCallback(async () => {
    if (mutation.current || readingRequest.current) return
    const abort = new AbortController()
    controller.current = abort
    readingRequest.current = true; setReading(true); setNotice(null)
    const timeout = window.setTimeout(() => {
      if (abort.signal.aborted) return
      abort.abort(); readingRequest.current = false; verified.current = false
      setReading(false); setNeedsRefresh(true)
      setNotice({ error: true, text: 'Loading approved game ports timed out after 20 seconds. Refresh approved ports to try again.' })
    }, 20000)
    requestTimeout.current = timeout
    try {
      const next = await getLocalJson(endpoint, parseApprovedGamePortsView, abort.signal)
      if (!abort.signal.aborted) { verified.current = true; setNeedsRefresh(false); setPlans(next.approvedPorts) }
    } catch (cause) {
      if (!abort.signal.aborted) { verified.current = false; setNeedsRefresh(true); setNotice({ error: true, text: errorMessage(cause) }) }
    } finally {
      window.clearTimeout(timeout)
      if (!abort.signal.aborted) { readingRequest.current = false; setReading(false) }
    }
  }, [])
  useEffect(() => { if (active && !loaded.current) { loaded.current = true; void read() } }, [active, read])
  const refresh = () => { if (!disabled && !pending) void read() }
  const save = async (method: 'POST' | 'PUT' | 'DELETE', id?: string) => {
    if (disabled || mutation.current || readingRequest.current || !verified.current || !plans) return
    const port = portNumber(gamePort)
    const rcon = game === 'Factorio' ? portNumber(rconPort) : null
    if (method !== 'DELETE' && (port === null || !validGamePorts(game, port, rcon))) return
    mutation.current = true
    const abort = new AbortController()
    controller.current = abort; setPending(true); setNotice(null)
    const timeout = window.setTimeout(() => {
      if (abort.signal.aborted) return
      abort.abort(); mutation.current = false; verified.current = false
      setPending(false); setNeedsRefresh(true)
      setNotice({ error: true, text: 'The game-port change could not be confirmed after 20 seconds. It may have been saved. Refresh approved ports before making another change.' })
    }, 20000)
    requestTimeout.current = timeout
    try {
      const next = await changeJson(`${endpoint}${id ? `/${id}` : ''}`, method, parseApprovedGamePortsResult,
        method === 'DELETE' ? undefined : { game, gamePort: port, factorioRconPort: rcon }, abort.signal)
      if (abort.signal.aborted) return
      if (next.ok) { setPlans(next.approvedPorts); if (method !== 'DELETE' || id === editing) clearDraft() }
      setNotice({ error: !next.ok, text: next.message })
    } catch (cause) {
      if (!abort.signal.aborted) {
        verified.current = false; setNeedsRefresh(true)
        setNotice({ error: true, text: `${errorMessage(cause)} The change may have been saved. Refresh approved ports before making another change.` })
      }
    } finally {
      window.clearTimeout(timeout)
      if (!abort.signal.aborted) { mutation.current = false; setPending(false) }
    }
  }
  const port = portNumber(gamePort)
  const valid = port !== null && validGamePorts(game, port, game === 'Factorio' ? portNumber(rconPort) : null)
  const busy = disabled || pending || reading || needsRefresh
  return <div className="approved-game-ports" aria-busy={pending || reading}>
    <p>Approve the game ports you want to reuse when hosting on this PC. Host locally uses these choices for future shared servers.</p>
    <p className="helper-text">Changing this list keeps existing server setup and running servers as they are. Change an existing server’s ports in its local Setup. Network settings still need your own configuration.</p>
    {!active && plans === null && !reading && !notice && <p className="helper-text">Open Game ports to review this PC’s saved approvals.</p>}
    {reading && <p role="status">Loading approved game ports…</p>}
    {plans && (plans.length === 0 ? <p>No game ports have been approved on this PC.</p> :
      <ul className="approved-game-port-list" aria-label="Approved game ports">{plans.map(plan => <li key={plan.id}>
        <strong>{approvedGamePortLabel(plan)}</strong><div className="actions">
          <Button className="text-button" disabled={busy} aria-label={`Edit ${approvedGamePortLabel(plan)}`} onClick={() => {
            setEditing(plan.id); setGame(plan.game); setGamePort(String(plan.gamePort)); setRconPort(plan.factorioRconPort === null ? '' : String(plan.factorioRconPort)); setNotice(null)
          }}><Icon name="edit" />Edit</Button>
          <Button className="text-button" disabled={busy} aria-label={`Remove ${approvedGamePortLabel(plan)}`} onClick={() => void save('DELETE', plan.id)}><Icon name="trash" />Remove</Button>
        </div></li>)}</ul>)}
    {plans && <fieldset className="approved-game-port-form" disabled={busy}><legend>{editing ? 'Edit approved game ports' : 'Approve game ports'}</legend>
      <label>Game<Select aria-label="Game for approved ports" value={game} onChange={event => { setGame(event.target.value as ApprovedPortGame); setGamePort(''); setRconPort('') }}>
        {games.filter(item => item !== 'Fixture' || game === 'Fixture').map(item => <option key={item} value={item}>{gameLabels[item]}</option>)}</Select></label>
      <label>Game port<Input type="number" aria-label="Approved game port" min={1024} max={game === 'Valheim' ? 65534 : 65535} value={gamePort} onChange={event => setGamePort(event.target.value)} /></label>
      {game === 'Valheim' && <p className="helper-text">Valheim also uses the next UDP port. Approving 2456 includes 2457.</p>}
      {game === 'Factorio' && <label>Private RCON port<Input type="number" aria-label="Approved private RCON port" min={1024} max={65535} value={rconPort} onChange={event => setRconPort(event.target.value)} /></label>}
      {game === 'Factorio' && <p className="helper-text">Use a different port for RCON and keep it private. Do not forward the RCON port.</p>}
      <div className="actions"><Button disabled={!valid} onClick={() => void save(editing ? 'PUT' : 'POST', editing ?? undefined)}><Icon name={pending ? 'loader' : 'check'} />{editing ? 'Save approved ports' : 'Approve ports'}</Button>
        {editing && <Button className="secondary" onClick={clearDraft}>Cancel edit</Button>}</div>
    </fieldset>}
    {notice && <p role={notice.error ? 'alert' : 'status'}>{notice.text}</p>}
    {(active || plans) && <Button className="text-button" disabled={disabled || pending || reading} onClick={refresh}><Icon name="refresh" />Refresh approved ports</Button>}
  </div>
}
