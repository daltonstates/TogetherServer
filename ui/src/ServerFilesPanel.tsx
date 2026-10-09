import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, TextArea } from './Controls'
import { ContractError, parseBasicResult, type BasicResult, type Decoder } from './contracts'
import { ServerAddOnsPanel } from './ServerAddOnsPanel'
import { editorFileDiff } from './editorChanges'
import { EditorDraftRecovery, useEditorDirtyGuard, useEditorDraftGuard, useEditorProtectedDraft, type EditorDraftStateChange, type EditorDraftGuardChange } from './editorProtectedDraft'

type Location = { key: string; label: string; path: string; available: boolean }
type FileEntry = { key: string; label: string; path: string; available: boolean }
type FilesView = { ok: boolean; code: string; message: string; locations: Location[]; files: FileEntry[] }
type FileContent = { ok: boolean; code: string; message: string; key: string; content: string | null;
  sha256: string | null; canUndo: boolean }
type ChangeResult = BasicResult & { key: string; sha256: string | null; canUndo: boolean }
type SetupBackup = { id: string; createdUtc: string; setupIncluded: boolean; backupKind: string }
type SetupBackups = { backups: SetupBackup[] }

function entry(value: unknown, context: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError(`${context} must be an object.`)
  return value as Record<string, unknown>
}
function field(value: unknown, context: string): string {
  if (typeof value !== 'string') throw new ContractError(`${context} must be text.`)
  return value
}
function flag(value: unknown, context: string): boolean {
  if (typeof value !== 'boolean') throw new ContractError(`${context} must be true or false.`)
  return value
}
const parseFilesView: Decoder<FilesView> = (value, context = 'server files') => {
  const source = entry(value, context)
  if (!Array.isArray(source.locations) || !Array.isArray(source.files) ||
      source.locations.length > 12 || source.files.length > 8) throw new ContractError(`${context} has invalid entries.`)
  const decodeEntry = (item: unknown, label: string) => {
    const source = entry(item, label)
    return { key: field(source.key, `${label}.key`), label: field(source.label, `${label}.label`),
      path: field(source.path, `${label}.path`), available: flag(source.available, `${label}.available`) }
  }
  return { ok: flag(source.ok, `${context}.ok`), code: field(source.code, `${context}.code`),
    message: field(source.message, `${context}.message`),
    locations: source.locations.map((item, index) => decodeEntry(item, `location ${index}`)),
    files: source.files.map((item, index) => decodeEntry(item, `file ${index}`)) }
}
const parseFileContent: Decoder<FileContent> = (value, context = 'server file') => {
  const source = entry(value, context)
  const nullable = (item: unknown, name: string) => item === null ? null : field(item, name)
  return { ok: flag(source.ok, `${context}.ok`), code: field(source.code, `${context}.code`),
    message: field(source.message, `${context}.message`), key: field(source.key, `${context}.key`),
    content: nullable(source.content, `${context}.content`), sha256: nullable(source.sha256, `${context}.sha256`),
    canUndo: flag(source.canUndo, `${context}.canUndo`) }
}
const parseChangeResult: Decoder<ChangeResult> = (value, context = 'file change') => {
  const source = entry(value, context)
  return { ...parseBasicResult(value, context), key: field(source.key, `${context}.key`),
    sha256: source.sha256 === null ? null : field(source.sha256, `${context}.sha256`),
    canUndo: flag(source.canUndo, `${context}.canUndo`) }
}
const parseSetupBackups: Decoder<SetupBackups> = (value, context = 'setup backups') => {
  const source = entry(value, context)
  if (!Array.isArray(source.backups) || source.backups.length > 100)
    throw new ContractError(`${context} has invalid entries.`)
  return { backups: source.backups.map((item, index) => {
    const backup = entry(item, `backup ${index}`)
    return { id: field(backup.id, 'backup.id'), createdUtc: field(backup.createdUtc, 'backup.createdUtc'),
      backupKind: field(backup.backupKind, 'backup.backupKind'),
      setupIncluded: flag(backup.setupIncluded, 'backup.setupIncluded') }
  }) }
}

export type ServerFilesPanelProps = {
  profileId: string
  state: string
  maintenance: boolean
  busy: boolean
  recoveryBlocked: boolean
  onPrepareMaintenance: () => void
  onStart: () => void
  onOpenDoctor: () => void
  onDraftStateChange?: EditorDraftStateChange
  onDraftGuardChange?: EditorDraftGuardChange
}

export function ServerFilesPanel(props: ServerFilesPanelProps) {
  return <ServerFilesContent key={props.profileId} {...props} />
}

function ServerFilesContent({ profileId, state, maintenance, busy, recoveryBlocked,
  onPrepareMaintenance, onStart, onOpenDoctor, onDraftStateChange, onDraftGuardChange }: ServerFilesPanelProps) {
  const [view, setView] = useState<FilesView | null>(null)
  const [loaded, setLoaded] = useState<FileContent | null>(null)
  const [draft, setDraft] = useState('')
  const [pending, setPending] = useState('')
  const [notice, setNotice] = useState<{ good: boolean; text: string } | null>(null)
  const [backups, setBackups] = useState<SetupBackup[]>([])
  const [revision, setRevision] = useState(0)
  const [reviewed, setReviewed] = useState<{ key: string; sha256: string; content: string } | null>(null)
  const base = `/api/local/profiles/${profileId}`
  useEffect(() => {
    let active = true
    setView(null)
    setLoaded(null)
    setNotice(null)
    void getLocalJson(`${base}/files`, parseFilesView).then(result => {
      if (!active) return
      if (result.ok) setView(result)
      else setNotice({ good: false, text: result.message })
    }).catch(error => { if (active) setNotice({ good: false, text: errorMessage(error) }) })
    void getLocalJson(`${base}/backups`, parseSetupBackups).then(result => {
      if (active) setBackups(result.backups)
    }).catch(() => { /* The Files tab still works when backup history is unavailable. */ })
    return () => { active = false }
  }, [base])
  const editAllowed = maintenance && state === 'Offline' && !busy && !pending && !recoveryBlocked
  const changed = loaded?.content !== null && loaded?.content !== undefined && draft !== loaded.content
  const protectedDraft = useEditorProtectedDraft(loaded ? { purpose: 'file', profileId, key: `file:${loaded.key}` } : null,
    changed && loaded ? JSON.stringify({ version: 1, key: loaded.key, sha256: loaded.sha256, content: draft }) : null)
  useEditorDirtyGuard(`file:${profileId}`, changed, onDraftStateChange)
  useEditorDraftGuard(`file:${profileId}`, changed, protectedDraft, onDraftGuardChange)
  const diff = changed && loaded?.content !== null && loaded?.content !== undefined ? editorFileDiff(loaded.content, draft) : null
  const reviewMatches = reviewed !== null && loaded !== null && reviewed.key === loaded.key && reviewed.sha256 === loaded.sha256 && reviewed.content === draft
  const recoverDraft = () => {
    try {
      const value: unknown = JSON.parse(protectedDraft.recovered ?? '')
      if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('This draft has an unsupported format.')
      const saved = value as Record<string, unknown>
      if (saved.version !== 1 || saved.key !== loaded?.key || typeof saved.content !== 'string' || saved.content.length > 256 * 1024)
        throw new Error('This draft does not match the selected reviewed file.')
      setDraft(saved.content)
      setReviewed(null)
      protectedDraft.acceptRecovery()
      if (saved.sha256 !== loaded?.sha256) setNotice({ good: false, text: 'The file changed since this draft was kept. Review the draft against the current file before saving.' })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
  }

  const loadFile = async (key: string) => {
    if (pending || busy) return
    if (changed && !window.confirm('Reloading replaces the editor contents. Keep your protected draft and reload this file?')) return
    setPending(`read-${key}`)
    setNotice(null)
    try {
      if (changed && !(await protectedDraft.persistNow())) return
      const result = await getLocalJson(`${base}/files/${key}`, parseFileContent)
      if (result.key !== key) throw new Error('The file response does not match the selected reviewed file.')
      if (!result.ok || result.content === null || result.sha256 === null) {
        setLoaded(null)
        setNotice({ good: false, text: result.message })
        return
      }
      setLoaded(result)
      setDraft(result.content)
      setReviewed(null)
      if (loaded?.key === key && changed) await protectedDraft.reloadRecovery()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const refreshFile = async (key: string) => {
    const [result, listing] = await Promise.all([
      getLocalJson(`${base}/files/${key}`, parseFileContent),
      getLocalJson(`${base}/files`, parseFilesView)
    ])
    if (result.key !== key) throw new Error('The refreshed file does not match the reviewed file.')
    if (listing.ok) setView(listing)
    if (result.ok && result.content !== null && result.sha256 !== null) {
      setLoaded(result)
      setDraft(result.content)
      setReviewed(null)
    } else setLoaded(null)
  }

  const save = async () => {
    if (!loaded?.sha256 || !changed || !editAllowed || !reviewMatches || protectedDraft.recovered !== null) return
    setPending('save')
    setNotice(null)
    try {
      const result = await changeJson(`${base}/files/${loaded.key}`, 'PUT', parseChangeResult,
        { expectedSha256: loaded.sha256, content: draft })
      if (result.key !== loaded.key) throw new Error('The saved result does not match the reviewed file.')
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) { await protectedDraft.clear(); await refreshFile(loaded.key) }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const undo = async () => {
    if (!loaded?.sha256 || !loaded.canUndo || !editAllowed || changed) return
    if (!window.confirm('Restore the previous file version? TogetherServer will make another offline world checkpoint first.')) return
    setPending('undo')
    setNotice(null)
    try {
      const result = await changeJson(`${base}/files/${loaded.key}/undo`, 'POST', parseChangeResult,
        { expectedSha256: loaded.sha256 })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) await refreshFile(loaded.key)
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const openFolder = async (key: string) => {
    setPending(`open-${key}`)
    setNotice(null)
    try {
      const result = await changeJson(`${base}/folders/${key}/open`, 'POST', parseBasicResult)
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const openTextFile = async (key: string) => {
    if (!editAllowed || changed) return
    setPending(`open-file-${key}`)
    setNotice(null)
    try {
      const result = await changeJson(`${base}/files/${key}/open`, 'POST', parseBasicResult)
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const createConfiguration = async (key: string) => {
    if (!editAllowed) return
    if (!window.confirm('Create this game configuration? TogetherServer will make an offline world checkpoint first.')) return
    setPending(`create-${key}`)
    setNotice(null)
    try {
      const result = await changeJson(`${base}/files/${key}/create`, 'POST', parseChangeResult)
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) await refreshFile(key)
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const refreshBackups = async () => {
    const result = await getLocalJson(`${base}/backups`, parseSetupBackups)
    setBackups(result.backups)
  }
  const createSetupCheckpoint = async () => {
    if (!editAllowed) return
    setPending('checkpoint')
    try {
      const result = await changeJson(`${base}/backups/setup`, 'POST', parseBasicResult)
      setNotice({ good: result.ok, text: result.message })
      await refreshBackups()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const restoreSetup = async (id: string) => {
    if (!editAllowed || changed ||
        !window.confirm('Restore this complete setup checkpoint? The world, reviewed configuration, and managed add-on state will change while the server stays offline. A pre-restore checkpoint will be kept.')) return
    setPending('restore-setup')
    try {
      const result = await changeJson(`${base}/backups/${id}/restore-setup`, 'POST', parseBasicResult)
      setNotice({ good: result.ok, text: result.message })
      await refreshBackups()
      if (result.ok) {
        setLoaded(null)
        setView(await getLocalJson(`${base}/files`, parseFilesView))
        setRevision(current => current + 1)
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const prepareChanges = async () => {
    if (busy || pending || recoveryBlocked ||
        !window.confirm('Prepare to change this server? TogetherServer will pause Friend controls, require a fresh zero-player count before any automatic Stop, and make a complete offline setup checkpoint.')) return
    setPending('prepare')
    try {
      const result = await changeJson(`${base}/prepare-change`, 'POST', parseBasicResult)
      setNotice({ good: result.ok, text: result.message })
      await refreshBackups()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const finishChanges = async () => {
    if (busy || pending || recoveryBlocked || changed || state !== 'Ready' || !maintenance ||
        !window.confirm('Have you joined this server from a real game client and checked the changed setup? Confirm to end maintenance and allow Friend controls again.')) return
    setPending('finish')
    try {
      const result = await changeJson(`${base}/finish-change`, 'POST', parseBasicResult,
        { confirmedGameJoin: true })
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  return <section className="server-files-panel" aria-label="Server files and settings">
    <div className="section-heading"><div><h3>Files &amp; settings</h3><p>Open the folders and edit the files this server actually uses.</p></div></div>
    {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
    <section aria-label="Prepare for editing"><ol>
      <li>{maintenance ? 'Friend lifecycle controls are paused.' : 'Pause Friend lifecycle controls with Prepare changes.'}</li>
      <li>{state === 'Offline' ? 'Server is offline. Prepare changes still verifies the setup checkpoint.' : 'Prepare changes needs a fresh exact zero-player count before graceful Stop. Positive or Unknown counts block it.'}</li>
      <li>Create a complete setup checkpoint, review edits, and save with the matching file version.</li>
      <li>Start, join from a real game client, then confirm Finish maintenance.</li>
    </ol></section>
    <div className="next-action"><span>{editAllowed ? 'Ready to review and save guarded edits.' : recoveryBlocked ? 'Resolve local recovery before preparing changes.' : 'Prepare changes creates the offline checkpoint required for safe editing.'}</span>
      <div className="actions"><Button className="secondary" disabled={busy || !!pending || recoveryBlocked}
        onClick={() => void prepareChanges()}>Prepare changes</Button>
        {maintenance && state === 'Ready' && <Button className="secondary" disabled={busy || !!pending || !!changed || recoveryBlocked}
          onClick={() => void finishChanges()}>Finish maintenance</Button>}</div></div>
    {!view && !notice && <p className="helper-text">Loading server files…</p>}
    {view && <>
      <div className="server-file-locations">{view.locations.map(location =>
        <div className="server-file-row" key={location.key}><div><strong>{location.label}</strong><code>{location.path}</code>
          {!location.available && <small>This folder is unavailable at the saved location. Check the server setup; Open folder does not create it.</small>}</div>
          <Button className="secondary" disabled={!location.available || !!pending || busy}
            onClick={() => void openFolder(location.key)}>Open folder</Button></div>)}</div>
      {!view.locations.some(location => location.key === 'behavior-packs' || location.key === 'mods') &&
        <p className="helper-text">This saved server has no reviewed mod or add-on folder assigned by its current driver.</p>}
      {view.files.length === 0 ? <p className="helper-text">This driver has no reviewed text configuration file in use. Edit its saved setup in TogetherServer.</p> :
        <div className="server-file-list"><h4>Editable files</h4>{view.files.map(file =>
          <div className="server-file-row" key={file.key}><div><strong>{file.label}</strong><code>{file.path}</code>
            {!file.available && <small>{file.key === 'factorio-settings' || file.key === 'terraria-config'
              ? 'This configuration is not available. Prepare changes, then use Create config to create the reviewed file.'
              : 'This reviewed file is unavailable. Check the saved server folder or start the game once, then stop and reload.'}</small>}</div>
            <div className="actions">
              {!file.available && (file.key === 'factorio-settings' || file.key === 'terraria-config') &&
                <Button className="secondary" disabled={!editAllowed}
                  onClick={() => void createConfiguration(file.key)}>Create config</Button>}
              <Button className="secondary" disabled={!file.available || !!pending || busy}
                onClick={() => void loadFile(file.key)}>{loaded?.key === file.key ? 'Reload file' : 'Edit file'}</Button>
              <Button className="text-button" disabled={!file.available || !editAllowed || !!changed}
                onClick={() => void openTextFile(file.key)}>Open in Notepad</Button>
            </div></div>)}</div>}
      <ServerAddOnsPanel key={revision} profileId={profileId} state={state} maintenance={maintenance}
        busy={busy} recoveryBlocked={recoveryBlocked} />
      <section className="server-setup-checkpoints" aria-label="Complete setup checkpoints">
        <div className="section-heading"><div><h4>Setup checkpoints</h4>
          <p>Complete setup checkpoints include the world, reviewed configuration, and managed add-on files.</p></div>
          <Button className="secondary" disabled={!editAllowed} onClick={() => void createSetupCheckpoint()}>
            Create checkpoint</Button></div>
        <details className="advanced-block"><summary>What a setup checkpoint covers</summary>
          <p>Included: this server's world files, reviewed configuration files shown above when present, managed add-ons, game ports and listing settings.</p>
          <p>Excluded: game executables and runtimes, Friend credentials, Windows app preferences, custom scripts, and packs outside the managed world.</p>
          <p>Active shared Bedrock packs outside this world block a complete setup checkpoint. Use the reviewed world-local pack flow before creating one.</p>
          <p>Undo last change restores one file. Restore setup restores the complete saved world and supported setup, with a pre-restore checkpoint.</p>
        </details>
        {backups.filter(backup => backup.setupIncluded).slice(0, 5).map(backup =>
          <div className="server-file-row" key={backup.id}><div><strong>{new Date(backup.createdUtc).toLocaleString()}</strong>
            <small>{backup.backupKind} · Complete setup</small></div>
            <Button className="secondary" disabled={!editAllowed || !!changed}
              onClick={() => void restoreSetup(backup.id)}>Restore setup</Button></div>)}
        {!backups.some(backup => backup.setupIncluded) &&
          <p className="helper-text">No complete setup checkpoint yet. Older world-only backups remain in Maintenance.</p>}
      </section>
      {loaded && <div className="server-file-editor"><h4>{view.files.find(file => file.key === loaded.key)?.label ?? 'Server file'}</h4>
        <p className="helper-text">Changes are saved only while maintenance is on and the server is offline. A matching on-disk version is required.</p>
        <EditorDraftRecovery recovered={protectedDraft.recovered} message={protectedDraft.message} disabled={!!pending}
          onRecover={recoverDraft} onDiscard={() => void protectedDraft.clear({ keepCurrent: true })} />
        <label>File contents<TextArea rows={15} spellCheck={false} value={draft} disabled={!!pending || protectedDraft.recovered !== null} onChange={event => { setDraft(event.target.value); setReviewed(null) }} /></label>
        {changed && <Button className="secondary" disabled={!!pending || protectedDraft.recovered !== null} onClick={() => loaded.sha256 && setReviewed({ key: loaded.key, sha256: loaded.sha256, content: draft })}>Review file changes</Button>}
        {diff && reviewMatches && <section aria-label="Review raw file changes"><h4>Before and after</h4>
          <p>{diff.unchangedStart} unchanged leading lines · {diff.unchangedEnd} unchanged trailing lines</p>
          <p>Line endings before: {(loaded.content?.match(/\r\n/gu) ?? []).length} CRLF, {(loaded.content?.match(/(?<!\r)\n/gu) ?? []).length} LF. After: {(draft.match(/\r\n/gu) ?? []).length} CRLF, {(draft.match(/(?<!\r)\n/gu) ?? []).length} LF.</p>
          <div style={{ overflowX: 'auto' }}><table><thead><tr><th>Change</th><th>Before line</th><th>After line</th><th>Exact text</th></tr></thead>
            <tbody>{diff.lines.map((line, index) => <tr key={index}><td>{line.type}</td><td>{line.before ?? '—'}</td><td>{line.after ?? '—'}</td><td><pre>{line.text || '(empty line)'}</pre></td></tr>)}</tbody></table></div>
          {diff.omitted > 0 && <p>{diff.omitted} more lines changed. The editor above contains the complete proposed file; only this comparison is shortened.</p>}
          <p>Save first checkpoints the offline world and supported setup, then keeps the previous file version for Undo.</p>
        </section>}
        <div className="actions"><Button disabled={!editAllowed || !changed || !reviewMatches || protectedDraft.recovered !== null} onClick={() => void save()}>{pending === 'save' ? 'Saving…' : 'Save with checkpoint'}</Button>
          <Button className="text-button" disabled={!changed || !!pending} onClick={() => {
            if (!window.confirm('Discard the unsaved contents of this editor? The current saved file stays unchanged.')) return
            setDraft(loaded.content ?? ''); setReviewed(null); void protectedDraft.clear()
          }}>Discard unsaved file edits</Button>
          <Button className="secondary" disabled={!editAllowed || !loaded.canUndo || !!changed} onClick={() => void undo()}>Undo last change</Button></div>
        {changed && <small>Unsaved changes in this editor.</small>}</div>}
      {!maintenance || state !== 'Offline' ? <div className="next-action"><span>{!maintenance ? 'Begin maintenance before changing files.' : 'Stop or resolve this server before changing files.'}</span>
        <Button className="secondary" onClick={onPrepareMaintenance}>Open maintenance guide</Button></div> :
        <div className="next-action"><span>After saving, start the server and check a real game join and saved change.</span>
          <div className="actions"><Button className="secondary" disabled={busy || recoveryBlocked || !!changed} onClick={onStart}>Start server</Button>
            <Button className="text-button" onClick={onOpenDoctor}>Connection Doctor</Button></div></div>}
    </>}
  </section>
}
