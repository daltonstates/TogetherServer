import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button } from './Controls'
import { ContractError, type Decoder } from './contracts'

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
  const change = async (path: string, payload: Record<string, unknown>, confirm: string) => {
    if (!allowed || !view || !window.confirm(confirm)) return
    setPending(true)
    setNotice(null)
    try {
      const result = await changeJson(`${base}/${path}`, 'POST', parseResult, payload)
      setNotice({ good: result.ok, text: result.message })
      if (result.view.ok) setView(result.view)
      else await refresh()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending(false) }
  }
  return <section className="server-addons-panel" aria-label="Server add-ons">
    <div className="section-heading"><div><h4>Add-ons &amp; game version</h4>
      <p>Game version: <strong>{view?.gameVersion ?? 'Loading…'}</strong></p></div>
      <Button className="secondary" disabled={pending || busy} onClick={() => void refresh()}>Refresh</Button></div>
    {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
    {view?.warning && <div className="notice bad" role="status">{view.warning}</div>}
    {view && <>
      <p className="helper-text">{view.requiredOnFriendPc}</p>
      {view.items.length > 0 ? <div className="server-file-list">{view.items.map(item =>
        <div className="server-file-row" key={item.key}><div><strong>{item.name} {item.version}</strong>
          <small>{item.type} · {item.enabled ? 'Enabled' : 'Disabled'} · {item.compatibility}</small></div>
          {(item.type === 'Factorio mod' || item.type === 'behavior pack' || item.type === 'resource pack') &&
            <Button className="secondary" disabled={!allowed}
            onClick={() => void change('state', { expectedStateToken: view.stateToken, key: item.key,
              enabled: !item.enabled }, `${item.enabled ? 'Disable' : 'Enable'} ${item.name}? An offline complete setup checkpoint will be made first.`)}>
            {item.enabled ? 'Disable' : 'Enable'}</Button>}</div>)}</div> :
        <p className="helper-text">No managed add-ons found for this server.</p>}
      <div className="actions">
        {view.canImport && <Button disabled={!allowed}
          onClick={() => void change('import', { expectedStateToken: view.stateToken },
            `Choose a local ${view.importType === 'BedrockPack' ? 'Bedrock .mcpack' : 'Factorio mod ZIP'}? TogetherServer will checkpoint the offline setup before copying it.`)}>
            {view.importType === 'BedrockPack' ? 'Import world pack' : 'Import mod ZIP'}</Button>}
        {view.canUndo && <Button className="secondary" disabled={!allowed}
          onClick={() => void change('undo', { expectedStateToken: view.stateToken },
            'Undo the last mod change? TogetherServer will checkpoint the offline world first.')}>Undo last add-on change</Button>}
        {view.warning && <Button className="secondary" disabled={!allowed}
          onClick={() => void change('review-version', { expectedStateToken: view.stateToken },
            'Confirm you reviewed this game version and each installed add-on? This does not prove a real game join.')}>Mark version reviewed</Button>}
      </div>
      {(view.canImport || view.canUndo) && !allowed &&
        <p className="helper-text">Begin maintenance and stop this server before changing add-ons.</p>}
    </>}
  </section>
}
