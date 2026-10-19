import { useCallback, useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, TextArea } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { useConfirmation, type ConfirmationOptions } from './Confirmation'
import { ContractError, parseBasicResult, type BasicResult, type Decoder } from './contracts'
import { ServerAddOnsPanel } from './ServerAddOnsPanel'
import { Icon, type IconName } from './Icon'
import { editorFileDiff } from './editorChanges'
import { EditorDraftRecovery, useEditorDirtyGuard, useEditorDraftGuard, useEditorProtectedDraft, type EditorDraftStateChange, type EditorDraftGuardChange } from './editorProtectedDraft'
import './ServerFilesPresentation.css'

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
  const [section, setSection] = useState<'files' | 'addons' | 'checkpoints'>('files')
  const registerDraftGuard = useCallback<EditorDraftGuardChange>((source, guard) => {
    onDraftGuardChange?.(source, guard ? { ...guard, flush: async () => {
      const saved = await guard.flush()
      if (!saved) setSection('files')
      return saved
    } } : null)
  }, [onDraftGuardChange])
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
  const { confirm: requestConfirmation, confirmationDialog } = useConfirmation()
  const confirmationInFlight = useRef(false)
  const currentConfirmationTarget = useRef({ signature: '', revision: 0 })
  const confirmationSignature = JSON.stringify({ profileId, state, maintenance, busy, pending, recoveryBlocked,
    view, loaded, draft, backups, recovered: protectedDraft.recovered })
  if (currentConfirmationTarget.current.signature !== confirmationSignature) {
    currentConfirmationTarget.current = { signature: confirmationSignature, revision: currentConfirmationTarget.current.revision + 1 }
  }
  const confirmCurrent = async (options: ConfirmationOptions) => {
    if (confirmationInFlight.current) return false
    confirmationInFlight.current = true
    const revision = currentConfirmationTarget.current.revision
    try { return await requestConfirmation(options) && revision === currentConfirmationTarget.current.revision }
    finally { confirmationInFlight.current = false }
  }
  useEditorDirtyGuard(`file:${profileId}`, changed, onDraftStateChange)
  useEditorDraftGuard(`file:${profileId}`, changed, {
    status: loaded ? protectedDraft.status : 'ready', persistNow: protectedDraft.persistNow
  }, registerDraftGuard)
  const diff = changed && loaded?.content !== null && loaded?.content !== undefined ? editorFileDiff(loaded.content, draft) : null
  const reviewMatches = reviewed !== null && loaded !== null && reviewed.key === loaded.key && reviewed.sha256 === loaded.sha256 && reviewed.content === draft
  const recoverDraft = () => {
    try {
      const value: unknown = JSON.parse(protectedDraft.recovered ?? '')
      if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('This draft has an unsupported format.')
      const saved = value as Record<string, unknown>
      if (saved.version !== 1 || saved.key !== loaded?.key || typeof saved.content !== 'string' || saved.content.length > 256 * 1024)
        throw new Error('This draft belongs to a different file.')
      setDraft(saved.content)
      setReviewed(null)
      protectedDraft.acceptRecovery()
      if (saved.sha256 !== loaded?.sha256) setNotice({ good: false, text: 'The file changed since this draft was kept. Review the draft against the current file before saving.' })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
  }

  const loadFile = async (key: string) => {
    if (pending || busy) return
    if (changed && !(await confirmCurrent({ title: 'Reload server file?',
      description: 'Save a draft of your edits, then reload this file?', confirmLabel: 'Keep draft and reload' }))) return
    setPending(`read-${key}`)
    setNotice(null)
    try {
      if (changed && !(await protectedDraft.persistNow())) return
      const result = await getLocalJson(`${base}/files/${key}`, parseFileContent)
      if (result.key !== key) throw new Error('Could not load the selected file. Try again.')
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
    if (result.key !== key) throw new Error('The selected file changed. Reload and try again.')
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
      if (result.key !== loaded.key) throw new Error('Could not confirm that this file was saved. Reload to check.')
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) { await protectedDraft.clear(); await refreshFile(loaded.key) }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const undo = async () => {
    if (!loaded?.sha256 || !loaded.canUndo || !editAllowed || changed) return
    if (!(await confirmCurrent({ title: 'Restore previous file version?',
      description: 'Restore the previous file version? The stopped world will be backed up first.', confirmLabel: 'Restore previous version', destructive: true }))) return
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
    if (!(await confirmCurrent({ title: 'Create game configuration?',
      description: 'Create this game configuration? The stopped world will be backed up first.', confirmLabel: 'Create configuration' }))) return
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
    if (!editAllowed || changed) return
    if (!(await confirmCurrent({ title: 'Restore complete setup?',
      description: 'Restore this backup? It replaces the world, supported settings, and add-ons while the server is stopped. A backup of the current setup will be kept first.',
      confirmLabel: 'Restore setup', destructive: true }))) return
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
    if (busy || pending || recoveryBlocked) return
    if (!(await confirmCurrent({ title: 'Prepare server changes?',
      description: 'Prepare to edit this server? Friend controls will pause. TogetherServer will check for 0 players before stopping, then back up the world and supported setup.',
      confirmLabel: 'Prepare changes' }))) return
    setPending('prepare')
    try {
      const result = await changeJson(`${base}/prepare-change`, 'POST', parseBasicResult)
      setNotice({ good: result.ok, text: result.message })
      await refreshBackups()
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const finishChanges = async () => {
    if (busy || pending || recoveryBlocked || changed || state !== 'Ready' || !maintenance) return
    if (!(await confirmCurrent({ title: 'Finish maintenance?',
      description: 'Have you joined this server from a real game client and checked the changed setup? Confirm to end maintenance and allow Friend controls again.',
      confirmLabel: 'Confirm game join and finish' }))) return
    setPending('finish')
    try {
      const result = await changeJson(`${base}/finish-change`, 'POST', parseBasicResult,
        { confirmedGameJoin: true })
      setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }
  const discard = async () => {
    if (!loaded || !changed || pending) return
    if (!(await confirmCurrent({ title: 'Discard file edits?',
      description: 'Discard the unsaved contents of this editor? The current saved file stays unchanged.', confirmLabel: 'Discard file edits', destructive: true }))) return
    setDraft(loaded.content ?? ''); setReviewed(null); await protectedDraft.clear()
  }

  const sections: { id: typeof section; label: string; icon: IconName }[] = [
    { id: 'files', label: 'Files', icon: 'logs' },
    { id: 'addons', label: 'Add-ons', icon: 'game' },
    { id: 'checkpoints', label: 'Setup backups', icon: 'archive' }
  ]
  return <section className="server-files-panel server-files-workspace" aria-label="Server files and settings">
    <div className="server-files-heading"><div><h3><Icon name="logs" />Server files</h3><p>Settings files, add-ons, and setup backups.</p></div>
      <span className="server-files-state"><Icon name={maintenance ? 'shield' : 'server'} size={15} />{maintenance ? 'Maintenance' : state}</span></div>
    {notice && <DismissibleNotice scope={profileId} className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</DismissibleNotice>}
    <div className="next-action server-files-maintenance"><span>{editAllowed ? 'Stopped and ready to edit.' : recoveryBlocked ? 'Review the local data warning before editing.' : 'Prepare changes pauses Friend controls and backs up the stopped world and setup.'}</span>
      <div className="actions"><Button className="secondary" disabled={busy || !!pending || recoveryBlocked}
        onClick={() => void prepareChanges()}><Icon name={pending === 'prepare' ? 'loader' : 'shield'} />Prepare changes</Button>
        {maintenance && state === 'Ready' && <Button className="secondary" disabled={busy || !!pending || !!changed || recoveryBlocked}
          onClick={() => void finishChanges()}><Icon name="check" />Finish maintenance</Button>}</div></div>
    {!editAllowed && state !== 'Offline' && <DismissibleNotice as="p" scope={profileId} className="warning-text">The server must report 0 players before Prepare changes can stop it. Players online or an unknown count block Stop.</DismissibleNotice>}
    <details className="server-support-disclosure server-files-disclosure"><summary><Icon name="shield" />Editing safely<Icon name="chevronDown" /></summary><section aria-label="Prepare for editing"><ol>
      <li>{maintenance ? 'Friend server controls are paused.' : 'Pause Friend server controls with Prepare changes.'}</li>
      <li>{state === 'Offline' ? 'The server is stopped. Prepare changes still checks the setup backup.' : 'Prepare changes checks for 0 players before stopping. Players online or an unknown count block Stop.'}</li>
      <li>Back up the world and setup, review your edits, and save.</li>
      <li>Start, join from a real game client, then confirm Finish maintenance.</li>
    </ol></section></details>
    {!view && !notice && <p className="helper-text">Loading server files…</p>}
    {view && <>
      <nav className="server-files-navigation" aria-label="Server file sections">{sections.map(item =>
        <Button key={item.id} className="secondary" aria-current={section === item.id ? 'page' : undefined}
          onClick={() => setSection(item.id)}><Icon name={item.icon} />{item.label}
          {item.id === 'files' && changed && <span className="server-files-dirty">Unsaved</span>}</Button>)}</nav>
      <div className="server-files-content" hidden={section !== 'files'}>
      <details className="server-files-locations server-files-disclosure"><summary><Icon name="folder" />Folders on this PC<span>{view.locations.length}</span><Icon name="chevronDown" /></summary>
      <div className="server-file-locations">{view.locations.map(location =>
        <div className="server-file-row" key={location.key}><div><strong>{location.label}</strong><code>{location.path}</code>
          {!location.available && <small>This folder is unavailable at the saved location. Check the server setup; Open folder does not create it.</small>}</div>
          <Button className="secondary" disabled={!location.available || !!pending || busy}
            onClick={() => void openFolder(location.key)}><Icon name="folder" />Open folder</Button></div>)}</div>
      {!view.locations.some(location => location.key === 'behavior-packs' || location.key === 'mods') &&
        <p className="helper-text">An add-on folder is not available for this game.</p>}
      </details>
      {view.files.length === 0 ? <p className="helper-text">No supported settings file is available for this game. Edit its server settings in TogetherServer.</p> :
        <div className="server-file-list"><div className="server-files-list-heading"><h4>Settings files</h4><small>{view.files.length} {view.files.length === 1 ? 'file' : 'files'}</small></div>{view.files.map(file =>
          <div className={`server-file-row${loaded?.key === file.key ? ' is-selected' : ''}`} key={file.key}><div><strong>{file.label}</strong><code title={file.path}>{file.path}</code>
            {!file.available && <small>{file.key === 'factorio-settings' || file.key === 'terraria-config'
              ? 'No settings file yet. Choose Prepare changes, then Create config.'
              : 'File unavailable. Check the server folder, or start the game once, then stop and reload.'}</small>}</div>
            <div className="actions">
              {!file.available && (file.key === 'factorio-settings' || file.key === 'terraria-config') &&
                <Button className="secondary" disabled={!editAllowed}
                  onClick={() => void createConfiguration(file.key)}><Icon name="plus" />Create config</Button>}
              <Button className="secondary" disabled={!file.available || !!pending || busy}
                onClick={() => void loadFile(file.key)}><Icon name={pending === `read-${file.key}` ? 'loader' : loaded?.key === file.key ? 'refresh' : 'edit'} />{loaded?.key === file.key ? 'Reload file' : 'Edit file'}</Button>
              <Button className="text-button" disabled={!file.available || !editAllowed || !!changed}
                onClick={() => void openTextFile(file.key)}><Icon name="logs" />Open in Notepad</Button>
            </div></div>)}</div>}
      {loaded && <div className="server-file-editor"><div className="server-files-editor-heading"><h4><Icon name="logs" />{view.files.find(file => file.key === loaded.key)?.label ?? 'Server file'}</h4>
        {changed && <span className="server-files-dirty">Unsaved changes</span>}</div>
        <p className="helper-text">To save, turn on maintenance and stop the server. If the file changes elsewhere, reload it first.</p>
        <EditorDraftRecovery recovered={protectedDraft.recovered} message={protectedDraft.message} status={protectedDraft.status} scope={`${profileId}:file:${loaded.key}`} disabled={!!pending}
          onRecover={recoverDraft} onDiscard={() => void protectedDraft.clear({ keepCurrent: true })} />
        <label>File contents<TextArea rows={15} spellCheck={false} value={draft} disabled={!!pending || protectedDraft.recovered !== null} onChange={event => { setDraft(event.target.value); setReviewed(null) }} /></label>
        {changed && <Button className="secondary server-files-review-action" disabled={!!pending || protectedDraft.recovered !== null} onClick={() => loaded.sha256 && setReviewed({ key: loaded.key, sha256: loaded.sha256, content: draft })}><Icon name="eye" />Review file changes</Button>}
        {diff && reviewMatches && <section aria-label="Review raw file changes"><h4>Before and after</h4>
          <p>{diff.unchangedStart} unchanged leading lines · {diff.unchangedEnd} unchanged trailing lines</p>
          <p>Line endings before: {(loaded.content?.match(/\r\n/gu) ?? []).length} CRLF, {(loaded.content?.match(/(?<!\r)\n/gu) ?? []).length} LF. After: {(draft.match(/\r\n/gu) ?? []).length} CRLF, {(draft.match(/(?<!\r)\n/gu) ?? []).length} LF.</p>
          <div className="server-file-diff-scroll" role="region" aria-label="File change comparison" tabIndex={0}><table><thead><tr><th>Change</th><th>Before line</th><th>After line</th><th>Exact text</th></tr></thead>
            <tbody>{diff.lines.map((line, index) => <tr key={index}><td>{line.type}</td><td>{line.before ?? '—'}</td><td>{line.after ?? '—'}</td><td><pre>{line.text || '(empty line)'}</pre></td></tr>)}</tbody></table></div>
          {diff.omitted > 0 && <p>{diff.omitted} more lines changed. The editor above contains the complete proposed file; only this comparison is shortened.</p>}
          <p>Saving backs up the stopped world and supported setup, and keeps the previous file for Undo.</p>
        </section>}
        <div className="actions server-files-editor-actions"><Button disabled={!editAllowed || !changed || !reviewMatches || protectedDraft.recovered !== null} onClick={() => void save()}><Icon name={pending === 'save' ? 'loader' : 'check'} />{pending === 'save' ? 'Saving…' : 'Back up and save'}</Button>
          <Button className="secondary" disabled={!editAllowed || !loaded.canUndo || !!changed} onClick={() => void undo()}><Icon name="undo" />Undo last change</Button>
          <Button className="text-button" disabled={!changed || !!pending} onClick={() => void discard()}><Icon name="close" />Discard unsaved file edits</Button></div>
      </div>}
      </div>
      <div className="server-files-content" hidden={section !== 'addons'}>
        <ServerAddOnsPanel key={revision} profileId={profileId} state={state} maintenance={maintenance}
          busy={busy || !!pending || !!changed} recoveryBlocked={recoveryBlocked} />
        {changed && <p className="server-files-section-note"><Icon name="logs" />Save or discard your file edits before changing add-ons.</p>}
      </div>
      <section className="server-setup-checkpoints server-files-content" aria-label="Complete setup backups" hidden={section !== 'checkpoints'}>
        <div className="server-files-list-heading"><div><h4><Icon name="archive" />Setup backups</h4>
          <p>World, supported settings, and add-ons.</p></div>
          <Button className="secondary" disabled={!editAllowed} onClick={() => void createSetupCheckpoint()}>
            <Icon name={pending === 'checkpoint' ? 'loader' : 'plus'} />Back up setup</Button></div>
        <details className="server-files-disclosure"><summary><Icon name="shield" />What a setup backup includes<Icon name="chevronDown" /></summary>
          <div className="server-files-disclosure-body"><p>Includes this server’s world, supported settings files, managed add-ons, game ports, and listing settings.</p>
          <p>Excludes game executables, runtimes, Friend credentials, Windows app preferences, custom scripts and packs outside the managed world.</p>
          <p>To back up the complete setup, review external Bedrock packs under Add-ons first.</p>
          <p>Undo restores one file. Restore setup replaces the world and supported setup, after backing up the current setup.</p></div>
        </details>
        {backups.filter(backup => backup.setupIncluded).slice(0, 5).map(backup =>
          <div className="server-file-row" key={backup.id}><div><strong>{new Date(backup.createdUtc).toLocaleString()}</strong>
            <small>{backup.backupKind} · Complete setup</small></div>
            <Button className="secondary" disabled={!editAllowed || !!changed}
              onClick={() => void restoreSetup(backup.id)}><Icon name="undo" />Restore setup</Button></div>)}
        {!backups.some(backup => backup.setupIncluded) &&
          <p className="server-files-empty"><Icon name="archive" />No setup backups yet. Find world-only backups in Backups.</p>}
        {changed && <p className="server-files-section-note"><Icon name="logs" />Save or discard your file edits before restoring a setup.</p>}
      </section>
      {!maintenance || state !== 'Offline' ? <div className="next-action"><span>{!maintenance ? 'Begin maintenance before changing files.' : 'Stop or resolve this server before changing files.'}</span>
        <Button className="secondary" onClick={onPrepareMaintenance}>Open maintenance guide</Button></div> :
        <div className="next-action"><span>After saving, start the server and check a real game join and saved change.</span>
          <div className="actions"><Button className="secondary" disabled={busy || recoveryBlocked || !!changed} onClick={onStart}><Icon name="play" />Start server</Button>
            <Button className="text-button" onClick={onOpenDoctor}><Icon name="plug" />Connection help</Button></div></div>}
    </>}
    {confirmationDialog}
  </section>
}
