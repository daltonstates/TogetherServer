import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { ApiError, changeJson, getLocalJson } from './api'
import { Button, Input } from './Controls'
import { parseBackupBookmarkResult, parseBackupBookmarks, validBackupLabel,
  type BackupBookmark, type BackupBookmarkChange, type BackupBookmarkResult, type BackupBookmarksResult } from './backupBookmarksWire'

export type BackupBookmarksLoader = (profileId: string, signal?: AbortSignal) => Promise<BackupBookmarksResult>
export type BackupBookmarkUpdater = (profileId: string, backupId: string, change: BackupBookmarkChange,
  signal?: AbortSignal) => Promise<BackupBookmarkResult>

const defaultLoader: BackupBookmarksLoader = (profileId, signal) =>
  getLocalJson(`/api/local/profiles/${encodeURIComponent(profileId)}/backup-bookmarks`, parseBackupBookmarks, signal)
const defaultUpdater: BackupBookmarkUpdater = (profileId, backupId, change, signal) =>
  changeJson(`/api/local/profiles/${encodeURIComponent(profileId)}/backups/${encodeURIComponent(backupId)}/bookmark`,
    'PUT', parseBackupBookmarkResult, change, signal)

function failure(code: string) {
  switch (code) {
    case 'PinnedBackupLimitReached': return 'This server has 20 pinned backups. Unpin one before pinning another.'
    case 'PinnedBackupCapacityReached': return 'Pinned backups can total at most 50 GB per server. Unpin one or copy it to a vault first.'
    case 'PinnedBackupCapacityUnavailable': return 'Pinned backup sizes could not be verified. Review the backup catalog first.'
    case 'InvalidBackupBookmark': return 'Use up to 64 English letters, numbers, spaces or simple punctuation for the name.'
    case 'BackupNotFound': return 'That backup is no longer in the completed list. Refresh to choose another.'
    case 'BackupCompletionInvalid': return 'That backup’s completed metadata could not be verified. Check its integrity before trying again.'
    default: return 'The backup name and pin could not be saved. Try again.'
  }
}

function BackupBookmarkEditor({ backup, updater, onSaved }: {
  backup: BackupBookmark
  updater: BackupBookmarkUpdater
  onSaved: (backup: BackupBookmark) => void
}) {
  const inputId = useId()
  const [label, setLabel] = useState(backup.label)
  const [pinned, setPinned] = useState(backup.pinned)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])
  const changed = label.trim() !== backup.label || pinned !== backup.pinned
  const valid = validBackupLabel(label)

  async function save() {
    if (saving || !valid || !changed) return
    const controller = new AbortController()
    pending.current = controller
    setSaving(true)
    setError('')
    try {
      const result = await updater(backup.profileId, backup.backupId, { label: label.trim(), pinned }, controller.signal)
      if (controller.signal.aborted) return
      if (!result.ok || !result.backup) { setError(failure(result.code)); return }
      if (result.profileId.toLowerCase() !== backup.profileId.toLowerCase() ||
          result.backupId.toLowerCase() !== backup.backupId.toLowerCase() ||
          result.backup.profileId.toLowerCase() !== backup.profileId.toLowerCase() ||
          result.backup.backupId.toLowerCase() !== backup.backupId.toLowerCase()) {
        setError('The local app returned another backup. Refresh and try again.')
        return
      }
      setLabel(result.backup.label)
      setPinned(result.backup.pinned)
      onSaved(result.backup)
    } catch (caught) {
      if (!controller.signal.aborted) setError(failure(caught instanceof ApiError ? caught.code : ''))
    } finally {
      if (!controller.signal.aborted) setSaving(false)
      if (pending.current === controller) pending.current = null
    }
  }

  const date = new Date(backup.createdUtc).toLocaleString()
  return <article className="recent-session-card backup-bookmark" aria-busy={saving}>
    <div className="recent-session-heading"><div><strong>{backup.label || 'Unnamed backup'}</strong>
      <small>{date} · {backup.backupKind === 'PreRestore' ? 'Before restore' : backup.backupKind} · {(backup.sizeBytes / 1024 ** 2).toLocaleString(undefined, { maximumFractionDigits: 1 })} MB</small></div>
      <span>{backup.pinned ? 'Pinned' : 'Follows retention'}</span></div>
    <details><summary>Name and retention</summary>
      <form onSubmit={event => { event.preventDefault(); void save() }}>
        <label htmlFor={inputId}>Backup name</label>
        <Input id={inputId} value={label} maxLength={64} disabled={saving} onChange={event => setLabel(event.target.value)}
          placeholder="Before a game update" aria-describedby={`${inputId}-help`} />
        <small id={`${inputId}-help`}>Up to 64 English letters, numbers, spaces or simple punctuation. Leave blank to clear the name.</small>
        <label className="check"><input type="checkbox" checked={pinned} disabled={saving}
          onChange={event => setPinned(event.target.checked)} />Pin this backup</label>
        {!valid && <p role="alert">Use English letters, numbers, spaces or simple punctuation in the name.</p>}
        {backup.pinned && !pinned && <p>After you save, a later backup can remove this one through automatic retention.</p>}
        <Button className="secondary" type="submit" disabled={saving || !changed || !valid}>{saving ? 'Saving…' : 'Save name and pin'}</Button>
        {error && <p className="error" role="alert">{error}</p>}
      </form>
    </details>
  </article>
}

export function BackupBookmarks({ profileId, visible, refreshKey = 0, loader = defaultLoader, updater = defaultUpdater, onChanged }: {
  profileId: string
  visible: boolean
  refreshKey?: number
  loader?: BackupBookmarksLoader
  updater?: BackupBookmarkUpdater
  onChanged?: () => void
}) {
  const titleId = useId()
  const [result, setResult] = useState<BackupBookmarksResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(false)
  const [notice, setNotice] = useState('')
  const [expanded, setExpanded] = useState(false)
  const pending = useRef<AbortController | null>(null)
  const load = useCallback(async () => {
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    setLoading(true)
    setError(false)
    setNotice('')
    try {
      const next = await loader(profileId, controller.signal)
      if (!controller.signal.aborted) {
        if (!next.ok || next.profileId.toLowerCase() !== profileId.toLowerCase()) setError(true)
        else setResult(next)
      }
    } catch {
      if (!controller.signal.aborted) setError(true)
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [loader, profileId])
  useEffect(() => {
    setResult(null)
    setExpanded(false)
    setNotice('')
    if (visible) void load()
    return () => pending.current?.abort()
  }, [load, refreshKey, visible])

  function saved(backup: BackupBookmark) {
    setNotice(backup.pinned ? 'Saved. This backup is protected from automatic retention.' : 'Saved. This backup follows automatic retention.')
    setResult(current => {
      if (!current) return current
      const old = current.backups.find(item => item.backupId === backup.backupId)
      if (!old) return current
      return { ...current, pinnedCount: current.pinnedCount + Number(backup.pinned) - Number(old.pinned),
        pinnedSizeBytes: current.pinnedSizeBytes + (backup.pinned ? backup.sizeBytes : 0) - (old.pinned ? old.sizeBytes : 0),
        backups: current.backups.map(item => item.backupId === backup.backupId ? backup : item) }
    })
    onChanged?.()
  }

  const shown = result?.backups.slice(0, expanded ? undefined : 5) ?? []
  return <section hidden={!visible} className="recent-sessions backup-bookmarks" aria-labelledby={titleId} aria-busy={loading}>
    <div className="recent-sessions-toolbar"><div><h4 id={titleId}>Named and pinned backups</h4>
      <p>A pin keeps a completed backup when automatic retention removes older copies.</p></div>
      <Button className="secondary" disabled={loading} onClick={() => void load()}>{loading ? 'Loading…' : 'Refresh names and pins'}</Button></div>
    {loading && !result && <p role="status">Loading backup names and pins…</p>}
    {error && <div className="error" role="alert"><strong>Backup names and pins unavailable</strong>
      <p>Could not read completed backups on this Host.</p><Button className="secondary" onClick={() => void load()}>Try again</Button></div>}
    {!error && result && <>
      <p>{result.pinnedCount} of {result.maximumPinnedCount} pins · {(result.pinnedSizeBytes / 1024 ** 3).toLocaleString(undefined, { maximumFractionDigits: 1 })} of 50 GB</p>
      {notice && <p role="status">{notice}</p>}
      {shown.length === 0 && <p>No completed backups yet. Make an offline backup to name or pin it.</p>}
      <div className="recent-session-list">{shown.map(backup => <BackupBookmarkEditor
        key={JSON.stringify([profileId, backup.backupId, backup.label, backup.pinned])}
        backup={backup} updater={updater} onSaved={saved} />)}</div>
      {!expanded && result.backups.length > shown.length && <Button className="secondary" onClick={() => setExpanded(true)}>Show {result.backups.length - shown.length} more backups</Button>}
      {result.moreBackupsAvailable && <p>The newest 100 completed backups are shown. More retained catalog entries need local review.</p>}
    </>}
    <p className="recent-sessions-boundary">Pins protect from automatic retention on this PC. Use Copy to vault for another location, and Verify for a separate integrity check.</p>
  </section>
}
