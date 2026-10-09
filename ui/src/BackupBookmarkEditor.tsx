import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { ApiError, changeJson } from './api'
import { Button, Input, Select } from './Controls'
import { parseBackupBookmarkResult, validBackupLabel, type BackupBookmark, type BackupBookmarkChange,
  type BackupBookmarkResult, type BackupBookmarksResult } from './backupBookmarksWire'
import { backupBytes, backupKindName, pinPreview, suggestedBackupNames } from './backupCatalogModel'

export type BackupBookmarkUpdater = (profileId: string, backupId: string, change: BackupBookmarkChange,
  signal?: AbortSignal) => Promise<BackupBookmarkResult>
export const updateBackupBookmark: BackupBookmarkUpdater = (profileId, backupId, change, signal) =>
  changeJson(`/api/local/profiles/${encodeURIComponent(profileId)}/backups/${encodeURIComponent(backupId)}/bookmark`,
    'PUT', parseBackupBookmarkResult, change, signal)

function failure(code: string) {
  switch (code) {
    case 'PinnedBackupLimitReached': return 'This server has 20 pinned backups. Unpin one before pinning another.'
    case 'PinnedBackupCapacityReached': return 'Pinned backups can total at most 50 GiB per server. Unpin one before pinning another.'
    case 'PinnedBackupCapacityUnavailable': return 'Pinned backup sizes could not be verified. Review the backup catalog first.'
    case 'InvalidBackupBookmark': return 'Use up to 64 English letters, numbers, spaces or simple punctuation for the name.'
    case 'BackupNotFound': return 'That backup is no longer in the completed list. Refresh to choose another.'
    case 'BackupCompletionInvalid': return 'That backup’s completed metadata could not be verified. Check its integrity before trying again.'
    default: return 'The backup name and pin could not be saved. Try again.'
  }
}

export function BackupBookmarkEditor({ backup, updater = updateBackupBookmark, capacity, onSaved, children, actions }: {
  backup: BackupBookmark
  updater?: BackupBookmarkUpdater
  capacity: Pick<BackupBookmarksResult, 'pinnedCount' | 'pinnedSizeBytes' | 'maximumPinnedCount' | 'maximumPinnedSizeBytes'>
  onSaved: (backup: BackupBookmark) => void
  children?: ReactNode
  actions?: ReactNode
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
  const preview = pinPreview(backup, capacity, pinned)

  async function save() {
    if (pending.current || !valid || !changed || preview.blocked) return
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
      if (result.backup.createdUtc !== backup.createdUtc || result.backup.backupKind !== backup.backupKind ||
          result.backup.sizeBytes !== backup.sizeBytes) {
        setError('The completed backup changed. Refresh before saving a name or pin.')
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

  return <article className="recent-session-card backup-bookmark" aria-busy={saving}>
    <div className="recent-session-heading"><div><strong>{backup.label || 'Unnamed backup'}</strong>
      <small>{new Date(backup.createdUtc).toLocaleString()} · {backupKindName(backup.backupKind)} · {backupBytes(backup.sizeBytes)}</small></div>
      <span>{backup.pinned ? 'Pinned' : 'Follows retention'}</span></div>
    {children}
    {actions && <div className="actions">{actions}</div>}
    <details><summary>Name and retention</summary>
      <form onSubmit={event => { event.preventDefault(); void save() }}>
        <label htmlFor={inputId}>Backup name</label>
        <Input id={inputId} value={label} maxLength={64} disabled={saving} onChange={event => setLabel(event.target.value)}
          placeholder="Before a game update" aria-describedby={`${inputId}-help`} />
        <small id={`${inputId}-help`}>Up to 64 English letters, numbers, spaces or simple punctuation. Leave blank to clear the name.</small>
        <label htmlFor={`${inputId}-suggestion`}>Suggested name</label>
        <Select id={`${inputId}-suggestion`} value="" disabled={saving} onChange={event => { if (event.target.value) setLabel(event.target.value) }}>
          <option value="">Choose a suggestion</option>
          {suggestedBackupNames(backup).map(name => <option key={name} value={name}>{name}</option>)}
        </Select>
        <label className="check"><Input type="checkbox" checked={pinned} disabled={saving}
          onChange={event => setPinned(event.target.checked)} />Pin this backup</label>
        <small>{preview.message}</small>
        {!valid && <p role="alert">Use English letters, numbers, spaces or simple punctuation in the name.</p>}
        {backup.pinned && !pinned && <p>After you save, a later backup can remove this one through automatic retention. Unpinning removes nothing now.</p>}
        <Button className="secondary" type="submit" disabled={saving || !changed || !valid || preview.blocked}>{saving ? 'Saving…' : 'Save name and pin'}</Button>
        {error && <p className="error" role="alert">{error}</p>}
      </form>
    </details>
  </article>
}
