import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input, Select } from './Controls'
import { accessListIssue, gameModes, maximumAccessEntries, parseGameAccessList, parseGameSettings,
  parseGameSettingsChange, parseGameSettingsPreview, settingsIssue, type GameAccessEntry,
  type GameAccessListRequest, type GameAccessListSummary, type GameAccessListView,
  type GameSettingsPreview, type GameSettingsRequest, type GameSettingsValues, type GameSettingsView } from './gameSettings'

export type GameSettingsPanelProps = {
  profileId: string
  state: string
  maintenance: boolean
  busy: boolean
  recoveryBlocked: boolean
  onPrepareMaintenance: () => void
}
type Notice = { bad: boolean; text: string }
type Review<T> = { request: T; result: GameSettingsPreview }

// Replacing a selected profile always replaces its drafts and outstanding reviews.
export function GameSettingsPanel(props: GameSettingsPanelProps) {
  return <GameSettingsContent key={props.profileId} {...props} />
}

function GameSettingsContent({ profileId, state, maintenance, busy, recoveryBlocked,
  onPrepareMaintenance }: GameSettingsPanelProps) {
  const base = `/api/local/profiles/${profileId}/game-settings`
  const [view, setView] = useState<GameSettingsView | null>(null)
  const [draft, setDraft] = useState<GameSettingsValues | null>(null)
  const [maximumPlayers, setMaximumPlayers] = useState('')
  const [pending, setPending] = useState('')
  const [notice, setNotice] = useState<Notice | null>(null)
  const [review, setReview] = useState<Review<GameSettingsRequest> | null>(null)
  const [listRevision, setListRevision] = useState(0)
  const allowed = maintenance && state === 'Offline' && !busy && !recoveryBlocked && !pending
  const next = draft ? { ...draft, maximumPlayers: Number(maximumPlayers) } : null
  const issue = next && view ? settingsIssue(view.kind, next) : null
  const changed = next !== null && view?.settings !== null && JSON.stringify(next) !== JSON.stringify(view?.settings)

  const accept = (result: GameSettingsView) => {
    setView(result)
    setDraft(result.settings)
    setMaximumPlayers(result.settings ? String(result.settings.maximumPlayers) : '')
    setReview(null)
  }
  useEffect(() => {
    let active = true
    const abort = new AbortController()
    void getLocalJson(base, parseGameSettings, abort.signal).then(result => {
      if (!active) return
      setView(result)
      setDraft(result.settings)
      setMaximumPlayers(result.settings ? String(result.settings.maximumPlayers) : '')
    }).catch(error => { if (active) setNotice({ bad: true, text: errorMessage(error) }) })
    return () => { active = false; abort.abort() }
  }, [base])
  const reload = async () => {
    if (pending || busy) return
    setPending('reload')
    setNotice(null)
    try {
      accept(await getLocalJson(base, parseGameSettings))
      setListRevision(value => value + 1)
    } catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const change = (values: Partial<GameSettingsValues>) => {
    setDraft(current => current ? { ...current, ...values } : current)
    setReview(null)
    setNotice(null)
  }
  const preview = async () => {
    if (!allowed || !view?.sha256 || !next || !changed || issue) return
    const request = { expectedSha256: view.sha256, settings: next }
    setPending('preview')
    setNotice(null)
    setReview(null)
    try {
      const result = await changeJson(`${base}/preview`, 'POST', parseGameSettingsPreview, request)
      if (result.ok && result.key === 'server-properties' && result.expectedSha256 === view.sha256 &&
          result.changes.every(item => item.key !== (view.kind === 'MinecraftJava' ? 'allow-list' : 'white-list')))
        setReview({ request, result })
      else setNotice({ bad: true, text: result.ok ? 'The preview does not match these settings. Reload and try again.' : result.message })
    } catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const save = async () => {
    if (!allowed || !review || review.result.changes.length === 0) return
    setPending('save')
    setNotice(null)
    try {
      const result = await changeJson(base, 'PUT', parseGameSettingsChange, review.request)
      if (result.key !== 'server-properties') throw new Error('The saved result does not match this settings file.')
      setNotice({ bad: !result.ok, text: result.message })
      setReview(null)
      if (result.ok) accept(await getLocalJson(base, parseGameSettings))
    } catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const undo = async () => {
    if (!allowed || !view?.sha256 || !view.canUndo || changed) return
    setPending('undo')
    setNotice(null)
    try {
      const result = await changeJson(`${base}/undo`, 'POST', parseGameSettingsChange, { expectedSha256: view.sha256 })
      if (result.key !== 'server-properties') throw new Error('The Undo result does not match this settings file.')
      setNotice({ bad: !result.ok, text: result.message })
      if (result.ok) accept(await getLocalJson(base, parseGameSettings))
    } catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  return <section className="game-settings-panel" aria-label="Simple game settings">
    <div className="section-heading"><div><h3>Game settings</h3>
      <p>Choose common settings and manage who can join. Changes apply after the next Start.</p></div>
      <Button className="secondary" disabled={!!pending || busy} onClick={() => void reload()}>Reload game settings</Button></div>
    {notice && <div className={`notice${notice.bad ? ' bad' : ''}`} role="status">{notice.text}</div>}
    {!view && !notice && <p className="helper-text">Loading game settings…</p>}
    {view && !view.ok && <p className="helper-text">{view.message}</p>}
    {view?.settings && draft && <>
      <div className="game-settings-fields">
        <label>Difficulty<Select value={draft.difficulty} disabled={!allowed} onChange={event => change({ difficulty: event.target.value })}>
          {['peaceful', 'easy', 'normal', 'hard'].map(value => <option key={value} value={value}>{title(value)}</option>)}</Select></label>
        <label>Maximum players<Input type="number" min={1} max={200} step={1} value={maximumPlayers}
          disabled={!allowed} onChange={event => { setMaximumPlayers(event.target.value); setReview(null); setNotice(null) }} /></label>
        <label>Game mode<Select value={draft.gameMode} disabled={!allowed} onChange={event => change({ gameMode: event.target.value })}>
          {gameModes(view.kind).map(value => <option key={value} value={value}>{title(value)}</option>)}</Select></label>
        <label className="check"><Input type="checkbox" checked={draft.forceGameMode} disabled={!allowed}
          onChange={event => change({ forceGameMode: event.target.checked })} />Apply game mode when players join</label>
        <label className="check"><Input type="checkbox" checked={draft.allowListEnabled} disabled={!allowed}
          onChange={event => change({ allowListEnabled: event.target.checked })} />Require allowed players</label>
      </div>
      <p className="helper-text">Game mode normally applies to new players; the saved player or world mode can remain unless you apply it when players join.
        Requiring allowed players uses this edition's list below.</p>
      {issue && changed && <p className="helper-text" role="status">{issue}</p>}
      <div className="actions"><Button disabled={!allowed || !changed || !!issue} onClick={() => void preview()}>
        {pending === 'preview' ? 'Reviewing…' : 'Review settings changes'}</Button>
        <Button className="secondary" disabled={!allowed || !view.canUndo || changed} onClick={() => void undo()}>
          {pending === 'undo' ? 'Undoing…' : 'Undo last settings change'}</Button></div>
      {changed && <small>Unsaved settings. Review them before saving.</small>}
      {review && <PreviewChanges review={review.result} disabled={!allowed || review.result.changes.length === 0}
        pending={pending === 'save'} onSave={() => void save()} />}
    </>}
    {view?.lists.map(list => <GameAccessListEditor key={`${listRevision}-${list.key}`} base={`${base}/lists/${list.key}`}
      summary={list} kind={view.kind} editAllowed={allowed} blocked={busy || !!pending || recoveryBlocked} />)}
    {view && (view.settings !== null || view.lists.length > 0) && <>
      <p className="helper-text">Each save and Undo makes an offline setup checkpoint first. Undo restores the previous version of that file.</p>
      {recoveryBlocked ? <p className="helper-text">Resolve local recovery before changing game settings.</p> :
        (!maintenance || state !== 'Offline') && <div className="next-action"><span>{!maintenance ?
          'Prepare maintenance before editing settings or access lists.' : 'Stop or resolve this server before editing settings or access lists.'}</span>
          <Button className="secondary" disabled={busy || !!pending} onClick={onPrepareMaintenance}>Open maintenance guide</Button></div>}
    </>}
  </section>
}

function GameAccessListEditor({ base, summary, kind, editAllowed, blocked }: {
  base: string; summary: GameAccessListSummary; kind: string; editAllowed: boolean; blocked: boolean
}) {
  const [open, setOpen] = useState(false)
  const [view, setView] = useState<GameAccessListView | null>(null)
  const [entries, setEntries] = useState<GameAccessEntry[]>([])
  const [identity, setIdentity] = useState('')
  const [name, setName] = useState('')
  const [ignoresPlayerLimit, setIgnoresPlayerLimit] = useState(false)
  const [pending, setPending] = useState('')
  const [notice, setNotice] = useState<Notice | null>(null)
  const [review, setReview] = useState<Review<GameAccessListRequest> | null>(null)
  const allowed = editAllowed && !pending && !blocked
  const changed = view !== null && JSON.stringify(entries) !== JSON.stringify(view.entries)
  const issue = accessListIssue(kind, entries)
  const accept = (result: GameAccessListView) => {
    if (result.key !== summary.key || result.kind !== kind) throw new Error('The list response does not match this saved game.')
    setView(result)
    setEntries(result.entries)
    setReview(null)
  }
  useEffect(() => {
    if (!open || !summary.available || view !== null) return
    let active = true
    const abort = new AbortController()
    void getLocalJson(base, parseGameAccessList, abort.signal).then(result => {
      if (!active) return
      if (result.key !== summary.key || result.kind !== kind) throw new Error('The list response does not match this saved game.')
      setView(result)
      setEntries(result.entries)
    }).catch(error => { if (active) setNotice({ bad: true, text: errorMessage(error) }) })
    return () => { active = false; abort.abort() }
  }, [base, kind, open, summary.available, summary.key, view])
  const reload = async () => {
    setPending('reload')
    setNotice(null)
    try { accept(await getLocalJson(base, parseGameAccessList)) }
    catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const update = (next: GameAccessEntry[]) => { setEntries(next); setReview(null); setNotice(null) }
  const add = () => {
    if (!allowed || !view?.ok) return
    const entry: GameAccessEntry = { identity: kind === 'MinecraftBedrock' ? (identity || null) : identity,
      name: kind === 'Valheim' ? null : name, ignoresPlayerLimit: kind === 'MinecraftBedrock' ? ignoresPlayerLimit : null }
    const next = [...entries, entry]
    const problem = accessListIssue(kind, next)
    if (problem) { setNotice({ bad: true, text: problem }); return }
    update(next)
    setIdentity('')
    setName('')
    setIgnoresPlayerLimit(false)
  }
  const preview = async () => {
    if (!allowed || !view?.sha256 || !changed || issue) return
    const request = { expectedSha256: view.sha256, entries: entries.map(entry => ({ ...entry })) }
    setPending('preview')
    setNotice(null)
    setReview(null)
    try {
      const result = await changeJson(`${base}/preview`, 'POST', parseGameSettingsPreview, request)
      if (result.ok && result.key === summary.key && result.expectedSha256 === view.sha256) setReview({ request, result })
      else setNotice({ bad: true, text: result.ok ? 'The review does not match this list. Reload and try again.' : result.message })
    } catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const save = async () => {
    if (!allowed || !review || review.result.changes.length === 0) return
    setPending('save')
    setNotice(null)
    try {
      const result = await changeJson(base, 'PUT', parseGameSettingsChange, review.request)
      if (result.key !== summary.key) throw new Error('The saved result does not match this access list.')
      setNotice({ bad: !result.ok, text: result.message })
      setReview(null)
      if (result.ok) accept(await getLocalJson(base, parseGameAccessList))
    } catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const undo = async () => {
    if (!allowed || !view?.sha256 || !view.canUndo || changed) return
    setPending('undo')
    setNotice(null)
    try {
      const result = await changeJson(`${base}/undo`, 'POST', parseGameSettingsChange, { expectedSha256: view.sha256 })
      if (result.key !== summary.key) throw new Error('The Undo result does not match this access list.')
      setNotice({ bad: !result.ok, text: result.message })
      if (result.ok) accept(await getLocalJson(base, parseGameAccessList))
    } catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  return <section className="game-settings-list" aria-label={summary.label}>
    <div className="section-heading"><h4>{summary.label}</h4>
      <Button className="secondary" aria-expanded={open} disabled={blocked || !!pending}
        onClick={() => setOpen(value => !value)}>{open ? 'Close list' : `Manage ${summary.label.toLowerCase()}`}</Button></div>
    {!summary.available && <p className="helper-text">The game has not created this list yet. Start it once, then stop and reload to manage the list.</p>}
    {open && summary.available && <>
      {notice && <div className={`notice${notice.bad ? ' bad' : ''}`} role="status">{notice.text}</div>}
      {!view && !notice && <p className="helper-text">Loading players…</p>}
      {view && !view.ok && <p className="helper-text">{view.message}</p>}
      <Button className="text-button" disabled={blocked || !!pending} onClick={() => void reload()}>Reload this list</Button>
      {view?.ok && <>
        {kind === 'Valheim' && summary.key === 'permit-list' && <p className="helper-text">
          A non-empty permitted list restricts joining to these IDs. An empty list allows other players subject to bans and the game password.</p>}
        {entries.length === 0 ? <p className="helper-text">No players in this list.</p> : entries.map((entry, index) =>
          <div className="game-settings-entry" key={entry.identity ?? entry.name ?? index}>
            <div><strong>{entry.name ?? entry.identity}</strong>{entry.name && entry.identity && <code>{entry.identity}</code>}</div>
            {kind === 'MinecraftBedrock' && <label className="check"><Input type="checkbox" checked={entry.ignoresPlayerLimit ?? false}
              disabled={!allowed} onChange={event => update(entries.map((item, position) => position === index ?
                { ...item, ignoresPlayerLimit: event.target.checked } : item))} />Bypass player limit for {entry.name}</label>}
            <Button className="text-button" disabled={!allowed} aria-label={`Remove ${entry.name ?? entry.identity}`}
              onClick={() => update(entries.filter((_, position) => position !== index))}>Remove</Button></div>)}
        <div className="game-settings-fields">
          {kind !== 'Valheim' && <label>{kind === 'MinecraftJava' ? 'Java player name' : 'Xbox gamertag'}<Input
            value={name} maxLength={kind === 'MinecraftJava' ? 16 : 32} disabled={!allowed} onChange={event => setName(event.target.value)} /></label>}
          <label>{kind === 'Valheim' ? 'Platform user ID' : kind === 'MinecraftJava' ? 'Player UUID' : 'XUID (optional)'}<Input
            value={identity} maxLength={kind === 'Valheim' ? 128 : kind === 'MinecraftJava' ? 36 : 20}
            disabled={!allowed} onChange={event => setIdentity(event.target.value)} /></label>
          {kind === 'MinecraftBedrock' && <label className="check"><Input type="checkbox" checked={ignoresPlayerLimit}
            disabled={!allowed} onChange={event => setIgnoresPlayerLimit(event.target.checked)} />Bypass player limit for new player</label>}
          <Button className="secondary" disabled={!allowed || entries.length >= maximumAccessEntries} onClick={add}>Add player</Button>
        </div>
        {kind === 'Valheim' && <p className="helper-text">Use the case-sensitive Platform_UserID from the game's F2 panel or server log.</p>}
        {kind === 'MinecraftJava' && <p className="helper-text">Use this player's verified Java UUID and username. This editor does not look up identities.</p>}
        <small>{entries.length} of 128 player entries</small>
        {changed && <p className="helper-text">Unsaved list changes.</p>}
        <div className="actions"><Button disabled={!allowed || !changed || !!issue} onClick={() => void preview()}>
          {pending === 'preview' ? 'Reviewing…' : 'Review list changes'}</Button>
          <Button className="secondary" disabled={!allowed || !view.canUndo || changed} onClick={() => void undo()}>
            {pending === 'undo' ? 'Undoing…' : 'Undo last list change'}</Button></div>
        {review && <PreviewChanges review={review.result} disabled={!allowed || review.result.changes.length === 0}
          pending={pending === 'save'} onSave={() => void save()} />}
      </>}
    </>}
  </section>
}

function PreviewChanges({ review, disabled, pending, onSave }: {
  review: GameSettingsPreview; disabled: boolean; pending: boolean; onSave: () => void
}) {
  return <section className="game-settings-preview" aria-label="Review exact changes">
    <h4>Review changes</h4>
    {review.changes.length === 0 ? <p>There are no changes to save.</p> : <dl>{review.changes.map((change, index) =>
      <div key={`${change.key}-${index}`}><dt>{change.label}</dt><dd>
        <span>Before: </span><code>{change.before ?? '(absent)'}</code><br />
        <span>After: </span><code>{change.after ?? '(removed)'}</code></dd></div>)}</dl>}
    <p className="helper-text">Saving first checkpoints the offline world and reviewed setup. Start afterward and check the changed behavior in the game.</p>
    <Button disabled={disabled} onClick={onSave}>{pending ? 'Saving…' : 'Save with checkpoint'}</Button>
  </section>
}

function title(value: string): string { return value.charAt(0).toUpperCase() + value.slice(1) }
