import { useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button, TextArea } from './Controls'
import { ContractError, parseBasicResult, type BasicResult, type Decoder } from './contracts'

type Location = { key: string; label: string; path: string; available: boolean }
type FileEntry = { key: string; label: string; path: string; available: boolean }
type FilesView = { ok: boolean; code: string; message: string; locations: Location[]; files: FileEntry[] }
type FileContent = { ok: boolean; code: string; message: string; key: string; content: string | null;
  sha256: string | null; canUndo: boolean }
type ChangeResult = BasicResult & { key: string; sha256: string | null; canUndo: boolean }

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

export function ServerFilesPanel({ profileId, state, maintenance, busy, recoveryBlocked,
  onPrepareMaintenance, onStart, onOpenDoctor }: {
  profileId: string
  state: string
  maintenance: boolean
  busy: boolean
  recoveryBlocked: boolean
  onPrepareMaintenance: () => void
  onStart: () => void
  onOpenDoctor: () => void
}) {
  const [view, setView] = useState<FilesView | null>(null)
  const [loaded, setLoaded] = useState<FileContent | null>(null)
  const [draft, setDraft] = useState('')
  const [pending, setPending] = useState('')
  const [notice, setNotice] = useState<{ good: boolean; text: string } | null>(null)
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
    return () => { active = false }
  }, [base])
  const editAllowed = maintenance && state === 'Offline' && !busy && !pending && !recoveryBlocked
  const changed = loaded?.content !== null && loaded?.content !== undefined && draft !== loaded.content

  const loadFile = async (key: string) => {
    if (changed && !window.confirm('Discard your unsaved file changes?')) return
    setPending(`read-${key}`)
    setNotice(null)
    try {
      const result = await getLocalJson(`${base}/files/${key}`, parseFileContent)
      if (!result.ok || result.content === null || result.sha256 === null) {
        setLoaded(null)
        setNotice({ good: false, text: result.message })
        return
      }
      setLoaded(result)
      setDraft(result.content)
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const refreshFile = async (key: string) => {
    const [result, listing] = await Promise.all([
      getLocalJson(`${base}/files/${key}`, parseFileContent),
      getLocalJson(`${base}/files`, parseFilesView)
    ])
    if (listing.ok) setView(listing)
    if (result.ok && result.content !== null && result.sha256 !== null) {
      setLoaded(result)
      setDraft(result.content)
    } else setLoaded(null)
  }

  const save = async () => {
    if (!loaded?.sha256 || !changed || !editAllowed) return
    if (!window.confirm('Save this file? TogetherServer will first make an offline world checkpoint and keep the previous file version for Undo.')) return
    setPending('save')
    setNotice(null)
    try {
      const result = await changeJson(`${base}/files/${loaded.key}`, 'PUT', parseChangeResult,
        { expectedSha256: loaded.sha256, content: draft })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok) await refreshFile(loaded.key)
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

  return <section className="server-files-panel" aria-label="Server files and settings">
    <div className="section-heading"><div><h3>Files &amp; settings</h3><p>Open the folders and edit the files this server actually uses.</p></div></div>
    {notice && <div className={`notice ${notice.good ? 'good' : 'bad'}`} role="status">{notice.text}</div>}
    {!view && !notice && <p className="helper-text">Loading server files…</p>}
    {view && <>
      <div className="server-file-locations">{view.locations.map(location =>
        <div className="server-file-row" key={location.key}><div><strong>{location.label}</strong><code>{location.path}</code></div>
          <Button className="secondary" disabled={!location.available || !!pending || busy}
            onClick={() => void openFolder(location.key)}>Open folder</Button></div>)}</div>
      {!view.locations.some(location => location.key === 'behavior-packs') &&
        <p className="helper-text">This saved server has no reviewed mod or add-on folder assigned by its current driver.</p>}
      {view.files.length === 0 ? <p className="helper-text">This driver has no reviewed text configuration file in use. Edit its saved setup in TogetherServer.</p> :
        <div className="server-file-list"><h4>Editable files</h4>{view.files.map(file =>
          <div className="server-file-row" key={file.key}><div><strong>{file.label}</strong><code>{file.path}</code></div>
            <Button className="secondary" disabled={!file.available || !!pending || busy}
              onClick={() => void loadFile(file.key)}>{loaded?.key === file.key ? 'Reload file' : 'Edit file'}</Button></div>)}</div>}
      {loaded && <div className="server-file-editor"><h4>{view.files.find(file => file.key === loaded.key)?.label ?? 'Server file'}</h4>
        <p className="helper-text">Changes are saved only while maintenance is on and the server is offline. A matching on-disk version is required.</p>
        <label>File contents<TextArea rows={15} spellCheck={false} value={draft} onChange={event => setDraft(event.target.value)} /></label>
        <div className="actions"><Button disabled={!editAllowed || !changed} onClick={() => void save()}>{pending === 'save' ? 'Saving…' : 'Save with checkpoint'}</Button>
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
