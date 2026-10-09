import { useCallback, useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, Input, Select, TextArea } from './Controls'
import { editorBulkPlayers } from './editorChanges'
import { EditorDraftRecovery, useEditorDirtyGuard, useEditorDraftGuard, useEditorProtectedDraft, type EditorDraftStateChange, type EditorDraftGuard, type EditorDraftGuardChange } from './editorProtectedDraft'
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
  onDraftStateChange?: EditorDraftStateChange
  onDraftGuardChange?: EditorDraftGuardChange
}
type Notice = { bad: boolean; text: string }
type Review<T> = { request: T; result: GameSettingsPreview }

// Replacing a selected profile always replaces its drafts and outstanding reviews.
export function GameSettingsPanel(props: GameSettingsPanelProps) {
  return <GameSettingsContent key={props.profileId} {...props} />
}

function GameSettingsContent({ profileId, state, maintenance, busy, recoveryBlocked,
  onPrepareMaintenance, onDraftStateChange, onDraftGuardChange }: GameSettingsPanelProps) {
  const base = `/api/local/profiles/${profileId}/game-settings`
  const [view, setView] = useState<GameSettingsView | null>(null)
  const [draft, setDraft] = useState<GameSettingsValues | null>(null)
  const [maximumPlayers, setMaximumPlayers] = useState('')
  const [pending, setPending] = useState('')
  const [notice, setNotice] = useState<Notice | null>(null)
  const [review, setReview] = useState<Review<GameSettingsRequest> | null>(null)
  const [listRevision, setListRevision] = useState(0)
  const [search, setSearch] = useState('')
  const listGuards = useRef(new Map<string, EditorDraftGuard>())
  const updateListGuard = useCallback<EditorDraftGuardChange>((source, guard) => {
    if (guard) listGuards.current.set(source, guard)
    else listGuards.current.delete(source)
    onDraftGuardChange?.(source, guard)
  }, [onDraftGuardChange])
  const allowed = maintenance && state === 'Offline' && !busy && !recoveryBlocked && !pending
  const next = draft ? { ...draft, maximumPlayers: Number(maximumPlayers) } : null
  const issue = next && view ? settingsIssue(view.kind, next) : null
  const changed = next !== null && view?.settings !== null && JSON.stringify(next) !== JSON.stringify(view?.settings)
  const protectedDraft = useEditorProtectedDraft(view?.settings ? { purpose: 'settings', profileId, key: 'settings:properties' } : null,
    changed && draft && view ? JSON.stringify({ version: 1, kind: view.kind, sha256: view.sha256, settings: draft, maximumPlayers }) : null)
  useEditorDirtyGuard(`settings:${profileId}`, changed, onDraftStateChange)
  useEditorDraftGuard(`settings:${profileId}`, changed, protectedDraft, onDraftGuardChange)
  const matches = (label: string) => label.toLowerCase().includes(search.trim().toLowerCase())
  const labels = ['Difficulty peaceful easy normal hard combat enemy damage', 'Maximum players capacity simultaneous joins 1 200',
    'Game mode survival creative adventure spectator new players', 'Apply game mode when players join saved player world mode',
    'Require allowed players allow list whitelist restrict joining']
  const recoverDraft = () => {
    try {
      const value: unknown = JSON.parse(protectedDraft.recovered ?? '')
      if (!value || typeof value !== 'object' || Array.isArray(value) || !view?.settings)
        throw new Error('This settings draft has an unsupported format.')
      const saved = value as Record<string, unknown>
      if (saved.version !== 1 || saved.kind !== view.kind || typeof saved.maximumPlayers !== 'string' || saved.maximumPlayers.length > 12)
        throw new Error('This draft does not match this game settings editor.')
      const parsed = parseGameSettings({ ...view, settings: saved.settings })
      setDraft(parsed.settings)
      setMaximumPlayers(saved.maximumPlayers)
      setReview(null)
      protectedDraft.acceptRecovery()
      if (saved.sha256 !== view.sha256) setNotice({ bad: true, text: 'The saved file changed since this draft was kept. Review it against the current settings before saving.' })
    } catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
  }

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
    const dirtyLists = [...listGuards.current.values()].filter(guard => guard.dirty)
    if ((changed || dirtyLists.length > 0) && !window.confirm('Reloading replaces unsaved settings and player lists. Keep their protected drafts and reload?')) return
    setPending('reload')
    setNotice(null)
    try {
      if ((changed && !(await protectedDraft.persistNow())) || (await Promise.all(dirtyLists.map(guard => guard.flush()))).some(saved => !saved)) {
        setNotice({ bad: true, text: 'The drafts could not all be kept. Your editors remain unchanged; review the draft message or discard edits deliberately.' }); return
      }
      accept(await getLocalJson(base, parseGameSettings))
      if (changed) await protectedDraft.reloadRecovery()
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
    if (!allowed || !view?.sha256 || !next || !changed || issue || protectedDraft.recovered !== null) return
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
    if (!allowed || !review || review.result.changes.length === 0 || protectedDraft.recovered !== null) return
    setPending('save')
    setNotice(null)
    try {
      const result = await changeJson(base, 'PUT', parseGameSettingsChange, review.request)
      if (result.key !== 'server-properties') throw new Error('The saved result does not match this settings file.')
      setNotice({ bad: !result.ok, text: result.message })
      setReview(null)
      if (result.ok) { await protectedDraft.clear(); accept(await getLocalJson(base, parseGameSettings)) }
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
      <EditorDraftRecovery recovered={protectedDraft.recovered} message={protectedDraft.message} disabled={!!pending}
        onRecover={recoverDraft} onDiscard={() => void protectedDraft.clear({ keepCurrent: true })} />
      <label>Search game settings<Input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Difficulty, players, joining…" /></label>
      {search.trim() && <p className="helper-text" role="status">{labels.filter(matches).length} of {labels.length} settings match.</p>}
      <div className="game-settings-fields">
        {matches(labels[0]) && <div><label>Difficulty<Select aria-describedby={`difficulty-${profileId}`} value={draft.difficulty} disabled={!allowed || protectedDraft.recovered !== null} onChange={event => change({ difficulty: event.target.value })}>
          {['peaceful', 'easy', 'normal', 'hard'].map(value => <option key={value} value={value}>{title(value)}</option>)}</Select></label>
          <small id={`difficulty-${profileId}`}>Peaceful through Hard changes the game's challenge and enemy behavior after the next Start.</small></div>}
        {matches(labels[1]) && <div><label>Maximum players<Input type="number" min={1} max={200} step={1} value={maximumPlayers}
          aria-describedby={`players-${profileId}`} disabled={!allowed || protectedDraft.recovered !== null} onChange={event => { setMaximumPlayers(event.target.value); setReview(null); setNotice(null) }} /></label>
          <small id={`players-${profileId}`}>The game's simultaneous player limit, from 1 to 200. This does not grant Friend access.</small></div>}
        {matches(labels[2]) && <div><label>Game mode<Select aria-describedby={`mode-${profileId}`} value={draft.gameMode} disabled={!allowed || protectedDraft.recovered !== null} onChange={event => change({ gameMode: event.target.value })}>
          {gameModes(view.kind).map(value => <option key={value} value={value}>{title(value)}</option>)}</Select></label>
          <small id={`mode-${profileId}`}>The default for new players. {view.kind === 'MinecraftBedrock' ? 'Bedrock has no Spectator choice in this reviewed editor.' : 'Existing player modes can remain unchanged.'}</small></div>}
        {matches(labels[3]) && <div><label className="check"><Input type="checkbox" checked={draft.forceGameMode} disabled={!allowed || protectedDraft.recovered !== null}
          aria-describedby={`force-mode-${profileId}`} onChange={event => change({ forceGameMode: event.target.checked })} />Apply game mode when players join</label>
          <small id={`force-mode-${profileId}`}>On: apply the selected mode each time a player joins. Off: retain the game's normal saved-mode behavior.</small></div>}
        {matches(labels[4]) && <div><label className="check"><Input type="checkbox" checked={draft.allowListEnabled} disabled={!allowed || protectedDraft.recovered !== null}
          aria-describedby={`allowed-${profileId}`} onChange={event => change({ allowListEnabled: event.target.checked })} />Require allowed players</label>
          <small id={`allowed-${profileId}`}>On: joining requires an entry in the {view.kind === 'MinecraftJava' ? 'Java' : 'Bedrock'} list below, independently of TogetherServer's Friend permissions.</small></div>}
      </div>
      <p className="helper-text">Game mode normally applies to new players; the saved player or world mode can remain unless you apply it when players join.
        Requiring allowed players uses this edition's list below.</p>
      {issue && changed && <p className="helper-text" role="status">{issue}</p>}
      <div className="actions"><Button disabled={!allowed || !changed || !!issue} onClick={() => void preview()}>
        {pending === 'preview' ? 'Reviewing…' : 'Review settings changes'}</Button>
        <Button className="secondary" disabled={!allowed || !view.canUndo || changed} onClick={() => void undo()}>
          {pending === 'undo' ? 'Undoing…' : 'Undo last settings change'}</Button></div>
      {changed && <Button className="text-button" disabled={!!pending} onClick={() => {
        if (!window.confirm('Discard these unsaved settings? The current saved settings stay unchanged.')) return
        accept(view); void protectedDraft.clear()
      }}>Discard unsaved settings</Button>}
      {changed && <small>Unsaved settings. Review them before saving.</small>}
      {review && <PreviewChanges review={review.result} disabled={!allowed || review.result.changes.length === 0}
        pending={pending === 'save'} onSave={() => void save()} />}
    </>}
    {view?.lists.map(list => <GameAccessListEditor key={`${listRevision}-${list.key}`} base={`${base}/lists/${list.key}`}
      profileId={profileId} onDraftStateChange={onDraftStateChange} onDraftGuardChange={updateListGuard} summary={list} kind={view.kind} editAllowed={allowed} blocked={busy || !!pending || recoveryBlocked} />)}
    {view && (view.settings !== null || view.lists.length > 0) && <>
      <p className="helper-text">Each save and Undo makes an offline setup checkpoint first. Undo restores the previous version of that file.</p>
      {recoveryBlocked ? <p className="helper-text">Resolve local recovery before changing game settings.</p> :
        (!maintenance || state !== 'Offline') && <div className="next-action"><span>{!maintenance ?
          'Prepare maintenance before editing settings or access lists.' : 'Stop or resolve this server before editing settings or access lists.'}</span>
          <Button className="secondary" disabled={busy || !!pending} onClick={onPrepareMaintenance}>Open maintenance guide</Button></div>}
    </>}
  </section>
}

function GameAccessListEditor({ base, summary, kind, editAllowed, blocked, profileId, onDraftStateChange, onDraftGuardChange }: {
  base: string; summary: GameAccessListSummary; kind: string; editAllowed: boolean; blocked: boolean;
  profileId: string; onDraftStateChange?: EditorDraftStateChange
  onDraftGuardChange?: EditorDraftGuardChange
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
  const [search, setSearch] = useState('')
  const [bulkText, setBulkText] = useState('')
  const [bulkReview, setBulkReview] = useState<GameAccessEntry[] | null>(null)
  const allowed = editAllowed && !pending && !blocked
  const changed = view !== null && JSON.stringify(entries) !== JSON.stringify(view.entries)
  const issue = accessListIssue(kind, entries)
  const dirty = changed || !!identity || !!name || !!bulkText
  const protectedDraft = useEditorProtectedDraft(view ? { purpose: 'list', profileId, key: `list:${summary.key}` } : null,
    dirty && view ? JSON.stringify({ version: 1, kind, key: summary.key, sha256: view.sha256, entries, identity, name, ignoresPlayerLimit, bulkText }) : null)
  useEditorDirtyGuard(`list:${profileId}:${summary.key}`, dirty, onDraftStateChange)
  useEditorDraftGuard(`list:${profileId}:${summary.key}`, dirty, protectedDraft, onDraftGuardChange)
  const recoverDraft = () => {
    try {
      const value: unknown = JSON.parse(protectedDraft.recovered ?? '')
      if (!value || typeof value !== 'object' || Array.isArray(value) || !view) throw new Error('This player-list draft has an unsupported format.')
      const saved = value as Record<string, unknown>
      if (saved.version !== 1 || saved.kind !== kind || saved.key !== summary.key || typeof saved.identity !== 'string' || saved.identity.length > 128 ||
          typeof saved.name !== 'string' || saved.name.length > 32 || typeof saved.ignoresPlayerLimit !== 'boolean' || typeof saved.bulkText !== 'string' || saved.bulkText.length > 24_000)
        throw new Error('This draft does not match the selected player list.')
      const parsed = parseGameAccessList({ ...view, entries: saved.entries })
      setEntries(parsed.entries)
      setIdentity(saved.identity); setName(saved.name); setIgnoresPlayerLimit(saved.ignoresPlayerLimit); setBulkText(saved.bulkText)
      setReview(null); setBulkReview(null)
      protectedDraft.acceptRecovery()
      if (saved.sha256 !== view.sha256) setNotice({ bad: true, text: 'This list changed since the draft was kept. Review the draft against the current list before saving.' })
    } catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
  }
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
    if (pending || blocked) return
    if (dirty && !window.confirm('Reloading replaces this unsaved player list. Keep the protected draft and reload?')) return
    setPending('reload')
    setNotice(null)
    try {
      if (dirty && !(await protectedDraft.persistNow())) return
      accept(await getLocalJson(base, parseGameAccessList)); setIdentity(''); setName(''); setBulkText(''); setBulkReview(null); if (dirty) await protectedDraft.reloadRecovery()
    }
    catch (error) { setNotice({ bad: true, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const update = (next: GameAccessEntry[]) => { setEntries(next); setReview(null); setBulkReview(null); setNotice(null) }
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
    if (!allowed || !view?.sha256 || !changed || issue || protectedDraft.recovered !== null) return
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
    if (!allowed || !review || review.result.changes.length === 0 || protectedDraft.recovered !== null) return
    setPending('save')
    setNotice(null)
    try {
      const result = await changeJson(base, 'PUT', parseGameSettingsChange, review.request)
      if (result.key !== summary.key) throw new Error('The saved result does not match this access list.')
      setNotice({ bad: !result.ok, text: result.message })
      setReview(null)
      if (result.ok) { await protectedDraft.clear(); accept(await getLocalJson(base, parseGameAccessList)); setIdentity(''); setName(''); setBulkText(''); setBulkReview(null) }
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
        <EditorDraftRecovery recovered={protectedDraft.recovered} message={protectedDraft.message} disabled={!!pending}
          onRecover={recoverDraft} onDiscard={() => void protectedDraft.clear({ keepCurrent: true })} />
        {kind === 'Valheim' && summary.key === 'permit-list' && <p className="helper-text">
          A non-empty permitted list restricts joining to these IDs. An empty list allows other players subject to bans and the game password.</p>}
        <label>Search {summary.label.toLowerCase()}<Input type="search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Name or player ID" /></label>
        {search.trim() && <p className="helper-text" role="status">{entries.filter(entry => `${entry.name ?? ''} ${entry.identity ?? ''}`.toLowerCase().includes(search.trim().toLowerCase())).length} of {entries.length} players match.</p>}
        {entries.length === 0 ? <p className="helper-text">No players in this list.</p> : entries.map((entry, index) => ({ entry, index })).filter(({ entry }) =>
          `${entry.name ?? ''} ${entry.identity ?? ''}`.toLowerCase().includes(search.trim().toLowerCase())).map(({ entry, index }) =>
          <div className="game-settings-entry" key={entry.identity ?? entry.name ?? index}>
            <div><strong>{entry.name ?? entry.identity}</strong>{entry.name && entry.identity && <code>{entry.identity}</code>}</div>
            {kind === 'MinecraftBedrock' && <label className="check"><Input type="checkbox" checked={entry.ignoresPlayerLimit ?? false}
              disabled={!allowed || protectedDraft.recovered !== null} onChange={event => update(entries.map((item, position) => position === index ?
                { ...item, ignoresPlayerLimit: event.target.checked } : item))} />Bypass player limit for {entry.name}</label>}
            <Button className="text-button" disabled={!allowed || protectedDraft.recovered !== null} aria-label={`Remove ${entry.name ?? entry.identity}`}
              onClick={() => update(entries.filter((_, position) => position !== index))}>Remove</Button></div>)}
        <div className="game-settings-fields">
          {kind !== 'Valheim' && <label>{kind === 'MinecraftJava' ? 'Java player name' : 'Xbox gamertag'}<Input
            value={name} maxLength={kind === 'MinecraftJava' ? 16 : 32} disabled={!allowed || protectedDraft.recovered !== null} onChange={event => setName(event.target.value)} /></label>}
          <label>{kind === 'Valheim' ? 'Platform user ID' : kind === 'MinecraftJava' ? 'Player UUID' : 'XUID (optional)'}<Input
            value={identity} maxLength={kind === 'Valheim' ? 128 : kind === 'MinecraftJava' ? 36 : 20}
            disabled={!allowed || protectedDraft.recovered !== null} onChange={event => setIdentity(event.target.value)} /></label>
          {kind === 'MinecraftBedrock' && <label className="check"><Input type="checkbox" checked={ignoresPlayerLimit}
            disabled={!allowed || protectedDraft.recovered !== null} onChange={event => setIgnoresPlayerLimit(event.target.checked)} />Bypass player limit for new player</label>}
          <Button className="secondary" disabled={!allowed || entries.length >= maximumAccessEntries} onClick={add}>Add player</Button>
        </div>
        {kind === 'Valheim' && <p className="helper-text">Use the case-sensitive Platform_UserID from the game's F2 panel or server log.</p>}
        {kind === 'MinecraftJava' && <p className="helper-text">Use this player's verified Java UUID and username. This editor does not look up identities.</p>}
        <details className="advanced-block"><summary>Paste several players</summary>
          <p>{kind === 'Valheim' ? 'Paste one case-sensitive Platform_UserID per line.' : kind === 'MinecraftJava' ? 'Paste one Java username and verified UUID per line, separated by a tab.' : 'Paste one Xbox gamertag per line, optionally followed by a tab and numeric XUID. New entries do not bypass the player limit.'}</p>
          <label>Players to paste<TextArea rows={5} maxLength={24_000} value={bulkText} disabled={!allowed || protectedDraft.recovered !== null} onChange={event => { setBulkText(event.target.value); setBulkReview(null) }} /></label>
          <Button className="secondary" disabled={!allowed || !bulkText.trim()} onClick={() => {
            const result = editorBulkPlayers(kind, bulkText, entries)
            if (result.issue) { setNotice({ bad: true, text: result.issue }); setBulkReview(null) }
            else { setBulkReview(result.entries); setNotice(null) }
          }}>Review pasted players</Button>
          {bulkReview && <section aria-label="Review pasted players"><h5>{bulkReview.length} players to add</h5>
            <ul>{bulkReview.map((entry, index) => <li key={index}>{entry.name ?? entry.identity}{entry.name && entry.identity ? ` · ${entry.identity}` : ''}</li>)}</ul>
            <p>These entries join the unsaved list. Review the exact list changes before saving to the game.</p>
            <Button className="secondary" disabled={!allowed || protectedDraft.recovered !== null} onClick={() => { if (!accessListIssue(kind, [...entries, ...bulkReview])) { update([...entries, ...bulkReview]); setBulkText('') } }}>Add reviewed players to draft</Button>
          </section>}
        </details>
        <small>{entries.length} of {maximumAccessEntries} player entries</small>
        {changed && <p className="helper-text">Unsaved list changes.</p>}
        {dirty && <Button className="text-button" disabled={!!pending} onClick={() => {
          if (!window.confirm('Discard this unsaved player list and unfinished entries? The saved list stays unchanged.')) return
          accept(view); setIdentity(''); setName(''); setBulkText(''); setIgnoresPlayerLimit(false); setBulkReview(null); void protectedDraft.clear()
        }}>Discard unsaved list edits</Button>}
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
