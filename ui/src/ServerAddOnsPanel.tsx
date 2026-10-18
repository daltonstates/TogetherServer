import { useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { DismissibleNotice } from './DismissibleNotice'
import { Icon } from './Icon'
import { Button } from './Controls'
import { ContractError, type Decoder } from './contracts'
import { useConfirmation } from './Confirmation'
import './ServerFilesPresentation.css'

type AddOnItem = { key: string; name: string; version: string; requiredGameVersion: string;
  enabled: boolean; compatibility: string; type: string }
type AddOnView = { ok: boolean; code: string; message: string; gameVersion: string;
  requiredOnFriendPc: string; stateToken: string; items: AddOnItem[];
  canImport: boolean; canUndo: boolean; warning: string | null; importType: string | null }
type AddOnResult = { ok: boolean; code: string; message: string; view: AddOnView }

function object(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError(`${label} must be an object.`)
  return value as Record<string, unknown>
}
function string(value: unknown, label: string) {
  if (typeof value !== 'string') throw new ContractError(`${label} must be text.`)
  return value
}
function boolean(value: unknown, label: string) {
  if (typeof value !== 'boolean') throw new ContractError(`${label} must be true or false.`)
  return value
}
const parseAddOns: Decoder<AddOnView> = (value, label = 'add-ons') => {
  const source = object(value, label)
  if (!Array.isArray(source.items) || source.items.length > 128) throw new ContractError(`${label} has invalid items.`)
  return {
    ok: boolean(source.ok, `${label}.ok`), code: string(source.code, `${label}.code`),
    message: string(source.message, `${label}.message`),
    gameVersion: string(source.gameVersion, `${label}.gameVersion`),
    requiredOnFriendPc: string(source.requiredOnFriendPc, `${label}.requiredOnFriendPc`),
    stateToken: string(source.stateToken, `${label}.stateToken`),
    canImport: boolean(source.canImport, `${label}.canImport`),
    canUndo: boolean(source.canUndo, `${label}.canUndo`),
    warning: source.warning === null ? null : string(source.warning, `${label}.warning`),
    importType: source.importType === null ? null : string(source.importType, `${label}.importType`),
    items: source.items.map((item, index) => {
      const entry = object(item, `add-on ${index}`)
      return { key: string(entry.key, 'key'), name: string(entry.name, 'name'),
        version: string(entry.version, 'version'),
        requiredGameVersion: string(entry.requiredGameVersion, 'requiredGameVersion'),
        enabled: boolean(entry.enabled, 'enabled'), compatibility: string(entry.compatibility, 'compatibility'),
        type: string(entry.type, 'type') }
    })
  }
}
const parseResult: Decoder<AddOnResult> = (value, label = 'add-on result') => {
  const source = object(value, label)
  return { ok: boolean(source.ok, `${label}.ok`), code: string(source.code, `${label}.code`),
    message: string(source.message, `${label}.message`), view: parseAddOns(source.view, `${label}.view`) }
}

export function ServerAddOnsPanel({ profileId, state, maintenance, busy, recoveryBlocked }: {
  profileId: string; state: string; maintenance: boolean; busy: boolean; recoveryBlocked: boolean
}) {
  const [view, setView] = useState<AddOnView | null>(null)
  const [pending, setPending] = useState(false)
  const [notice, setNotice] = useState<{ good: boolean; text: string } | null>(null)
  const { confirm, confirmationDialog } = useConfirmation()
  const inFlight = useRef(false)
  const current = useRef({ profileId, state, maintenance, busy, recoveryBlocked, view, revision: 0 })
  if (current.current.profileId !== profileId || current.current.state !== state ||
    current.current.maintenance !== maintenance || current.current.busy !== busy ||
    current.current.recoveryBlocked !== recoveryBlocked || current.current.view !== view) {
    current.current = { profileId, state, maintenance, busy, recoveryBlocked, view,
      revision: current.current.revision + 1 }
  }
  const base = `/api/local/profiles/${profileId}/addons`
  useEffect(() => {
    let active = true
    setView(null)
    void getLocalJson(base, parseAddOns).then(result => { if (active) setView(result) })
      .catch(error => { if (active) setNotice({ good: false, text: errorMessage(error) }) })
    return () => { active = false }
  }, [base])
  const allowed = maintenance && state === 'Offline' && !busy && !recoveryBlocked && !pending
  const refresh = async () => {
    try { setView(await getLocalJson(base, parseAddOns)) }
    catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
  }
  const change = async (path: string, payload: Record<string, unknown>, description: string) => {
    if (!allowed || !view || inFlight.current) return
    const revision = current.current.revision
    inFlight.current = true
    setPending(true)
    try {
      if (!await confirm({ title: 'Confirm add-on change', description,
        confirmLabel: 'Continue' }) || current.current.revision !== revision) return
      setNotice(null)
      const result = await changeJson(`${base}/${path}`, 'POST', parseResult, payload)
      setNotice({ good: result.ok, text: result.message })
      if (result.view.ok) setView(result.view)
      else await refresh()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { inFlight.current = false; setPending(false) }
  }
  return <section className="server-addons-panel server-addons-workspace" aria-label="Server add-ons" aria-busy={pending}>
    <div className="server-files-list-heading"><div><h4><Icon name="game" />Add-ons &amp; game version</h4>
      <p>Game version: <strong>{view?.gameVersion ?? 'Loading…'}</strong></p></div>
      <Button className="secondary" disabled={pending || busy} onClick={() => void refresh()}><Icon name="refresh" />Refresh</Button></div>
    {notice && <DismissibleNotice scope={profileId} className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</DismissibleNotice>}
    {view?.warning && <DismissibleNotice scope={profileId} noticeKey={view.stateToken} className="notice bad" role="status">{view.warning}</DismissibleNotice>}
    {view && <>
      <p className="server-files-section-note"><Icon name="users" />{view.requiredOnFriendPc}</p>
      {view.items.length > 0 ? <><div className="server-files-list-heading server-addons-count"><small>{view.items.length} {view.items.length === 1 ? 'add-on' : 'add-ons'}</small>
        <span className="server-files-state">{view.items.filter(item => item.enabled).length} enabled</span></div>
        <div className="server-file-list server-addons-inventory" role="region" aria-label="Installed add-ons" tabIndex={0}>{view.items.map(item =>
        <div className="server-file-row server-addon-row" key={item.key}><div><strong>{item.name} {item.version}</strong>
          <small>{item.type} · {item.compatibility}</small><span className="server-addon-state">{item.enabled ? 'Enabled' : 'Disabled'}</span></div>
          {(item.type === 'Factorio mod' || item.type === 'behavior pack' || item.type === 'resource pack') &&
            <Button className="secondary" disabled={!allowed}
            onClick={() => void change('state', { expectedStateToken: view.stateToken, key: item.key,
              enabled: !item.enabled }, `${item.enabled ? 'Disable' : 'Enable'} ${item.name}? An offline complete setup checkpoint will be made first.`)}>
            <Icon name={item.enabled ? 'close' : 'check'} />{item.enabled ? 'Disable' : 'Enable'}</Button>}</div>)}</div></> :
        <p className="server-files-empty"><Icon name="game" />No managed add-ons found for this server.</p>}
      <div className="actions server-addons-actions">
        {view.canImport && <Button disabled={!allowed}
          onClick={() => void change('import', { expectedStateToken: view.stateToken },
            `Choose a local ${view.importType === 'BedrockPack' ? 'Bedrock .mcpack' : 'Factorio mod ZIP'}? TogetherServer will checkpoint the offline setup before copying it.`)}>
            <Icon name="upload" />{view.importType === 'BedrockPack' ? 'Import world pack' : 'Import mod ZIP'}</Button>}
        {view.canUndo && <Button className="secondary" disabled={!allowed}
          onClick={() => void change('undo', { expectedStateToken: view.stateToken },
            'Undo the last mod change? TogetherServer will checkpoint the offline world first.')}><Icon name="undo" />Undo last add-on change</Button>}
        {view.warning && <Button className="secondary" disabled={!allowed}
          onClick={() => void change('review-version', { expectedStateToken: view.stateToken },
            'Confirm you reviewed this game version and each installed add-on? This does not prove a real game join.')}><Icon name="eye" />Mark version reviewed</Button>}
      </div>
      {(view.canImport || view.canUndo) && !allowed &&
        <p className="helper-text">Begin maintenance and stop this server before changing add-ons.</p>}
    </>}
    {confirmationDialog}
  </section>
}
