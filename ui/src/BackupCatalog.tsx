import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { getLocalJson } from './api'
import { Button, Input, Select } from './Controls'
import { BackupBookmarkEditor, updateBackupBookmark, type BackupBookmarkUpdater } from './BackupBookmarkEditor'
import type { BackupBookmark, BackupBookmarksResult } from './backupBookmarksWire'
import { parseBackupCatalog } from './backupCatalogWire'
import { backupBytes, backupKindName, compareBackups, emptyBackupFilters, filterBackups, latestBackupEvidence,
  protectionSummary, restoreReview, retentionPreview, type BackupCatalogResult, type BackupEvidenceKind,
  type BackupRestoreReview, type BackupSummary } from './backupCatalogModel'

export type BackupCatalogLoader = (profileId: string, signal?: AbortSignal) => Promise<BackupCatalogResult>
export type BackupCatalogAction = (backup: BackupSummary, signal?: AbortSignal) => void | Promise<void>
export type BackupCatalogProps = {
  profileId: string
  visible: boolean
  refreshKey?: number
  loader?: BackupCatalogLoader
  updater?: BackupBookmarkUpdater
  onChanged?: () => void
  onLoaded?: (catalog: BackupCatalogResult) => void
  currentWorld?: string | null
  currentGame?: string | null
  offline?: boolean
  onVerify?: BackupCatalogAction
  onVault?: BackupCatalogAction
  onHashRehearsal?: BackupCatalogAction
  onOwnerGameRehearsal?: BackupCatalogAction
  onRestore?: (review: BackupRestoreReview, signal?: AbortSignal) => void | Promise<void>
  renderActions?: (backup: BackupSummary) => ReactNode
  unavailableReasons?: Partial<Record<'verify' | 'vault' | 'hashRehearsal' | 'ownerGameRehearsal' | 'restore', string>>
  // Used only by the backwards-compatible bookmark wrapper.
  bookmarkLoader?: (profileId: string, signal?: AbortSignal) => Promise<BackupBookmarksResult>
  bookmarkAdapter?: (bookmarks: BackupBookmarksResult) => BackupCatalogResult
  legacyLabels?: boolean
}

export const loadBackupCatalog: BackupCatalogLoader = (profileId, signal) =>
  getLocalJson(`/api/local/profiles/${encodeURIComponent(profileId)}/backup-catalog`, parseBackupCatalog, signal)

const evidenceNames: Record<BackupEvidenceKind, string> = {
  Integrity: 'Local integrity', Vault: 'Vault copy', HashRehearsal: 'Hash restore test', OwnerGameRehearsal: 'Game load, change and restart'
}
function BackupEvidenceDetails({ backup, evidenceAvailable }: { backup: BackupSummary; evidenceAvailable: boolean }) {
  return <>
    <div aria-label="Recorded protection results">{(Object.keys(evidenceNames) as BackupEvidenceKind[]).map(kind => {
      const fact = latestBackupEvidence(backup, kind)
      return <div key={kind}><small className={fact?.outcome === 'Failed' ? 'warning-text' : undefined}><strong>{evidenceNames[kind]}: </strong><span>{fact ?
        `${kind === 'OwnerGameRehearsal' ? 'Owner reported: ' : ''}${fact.outcome === 'Passed' ? 'Passed' : fact.outcome === 'Failed' ? 'Failed' : 'Incomplete'} · ${new Date(fact.checkedUtc).toLocaleString()}` :
        evidenceAvailable ? 'Unknown — no retained result for this backup' : 'Unknown — saved evidence unavailable'}</span></small></div>
    })}</div>
    <strong>Protection results</strong>
    <p>Each dated result applies to this exact backup. A vault copy confirms transferred hashes at that time; its location and continued availability are unknown here.</p>
    <p>A hash restore test checks bytes in a disposable copy. Game load, a saved change and restart require separate owner confirmations. A passed hash check does not establish game save health.</p>
  </>
}

function BackupReviewTools({ backup, evidenceAvailable, children }: { backup: BackupSummary; evidenceAvailable: boolean; children: ReactNode }) {
  const [open, setOpen] = useState(false)
  const panelId = useId()
  return <div>
    <Button className="secondary" aria-expanded={open} aria-controls={panelId} onClick={() => setOpen(value => !value)}>Review backup</Button>
    <div id={panelId} hidden={!open}>
      <BackupEvidenceDetails backup={backup} evidenceAvailable={evidenceAvailable} />
      <div className="actions">{children}</div>
    </div>
  </div>
}

function BackupEvidenceCue({ backup, evidenceAvailable }: { backup: BackupSummary; evidenceAvailable: boolean }) {
  const failed = (Object.keys(evidenceNames) as BackupEvidenceKind[]).filter(kind => latestBackupEvidence(backup, kind)?.outcome === 'Failed')
  const integrity = latestBackupEvidence(backup, 'Integrity')
  const unknown = !evidenceAvailable ? 'Saved evidence unavailable' : !integrity ? 'Integrity not checked' :
    integrity.outcome === 'Incomplete' ? 'Integrity check incomplete' : null
  return <small className={failed.length ? 'warning-text' : undefined}>
    {failed.length ? `Recorded check failed: ${failed.map(kind => evidenceNames[kind]).join(', ')}.` :
      integrity?.outcome === 'Passed' && evidenceAvailable ? 'Recorded integrity passed; game save health unverified.' : ''}
    {unknown && `${failed.length ? ' ' : ''}${unknown}.`}
  </small>
}

function BackupComparison({ left, right }: { left: BackupSummary; right: BackupSummary }) {
  const comparison = compareBackups(left, right)
  return <section aria-label="Backup comparison" className="recent-session-card">
    <h4>Compare backup summaries</h4>
    <p>Recorded manifest and setup fingerprints only. This is not a file-content diff or a fresh integrity check.</p>
    <p>Protected setup hashes compare stored checkpoint bytes. Different hashes can reflect protection encoding and do not by themselves prove different settings.</p>
    <table style={{ width: '100%', tableLayout: 'fixed', overflowWrap: 'anywhere' }}><caption>{left.label || 'Unnamed backup'} → {right.label || 'Unnamed backup'}</caption>
      <thead><tr><th scope="col">Summary</th><th scope="col">First backup</th><th scope="col">Second backup</th></tr></thead>
      <tbody>
        <tr><th scope="row">Backup identity</th><td style={{ overflowWrap: 'anywhere' }}>{left.backupId}</td><td style={{ overflowWrap: 'anywhere' }}>{right.backupId}</td></tr>
        <tr><th scope="row">Created</th><td>{new Date(left.createdUtc).toLocaleString()}</td><td>{new Date(right.createdUtc).toLocaleString()}</td></tr>
        <tr><th scope="row">Kind</th><td>{backupKindName(left.backupKind)}</td><td>{backupKindName(right.backupKind)}</td></tr>
        <tr><th scope="row">World</th><td>{left.worldId ?? 'Unknown'}</td><td>{right.worldId ?? 'Unknown'}</td></tr>
        <tr><th scope="row">Game</th><td>{left.gameKind ?? 'Unknown'}</td><td>{right.gameKind ?? 'Unknown'}</td></tr>
        <tr><th scope="row">Size</th><td>{backupBytes(left.sizeBytes)}</td><td>{backupBytes(right.sizeBytes)}</td></tr>
        <tr><th scope="row">Files</th><td>{left.fileCount ?? 'Unknown'}</td><td>{right.fileCount ?? 'Unknown'}</td></tr>
        <tr><th scope="row">Recorded payload fingerprint</th><td style={{ overflowWrap: 'anywhere' }}>{left.payloadSha256 ?? 'Unknown'}</td><td style={{ overflowWrap: 'anywhere' }}>{right.payloadSha256 ?? 'Unknown'}</td></tr>
        <tr><th scope="row">Recorded setup fingerprint</th><td style={{ overflowWrap: 'anywhere' }}>{left.setupSha256 ?? (left.setupIncluded === false ? 'World only' : 'Unknown')}</td><td style={{ overflowWrap: 'anywhere' }}>{right.setupSha256 ?? (right.setupIncluded === false ? 'World only' : 'Unknown')}</td></tr>
      </tbody>
    </table>
    <p>World: {comparison.world}. Game: {comparison.game}. Payload: {comparison.payload}. Setup: {comparison.setup}.</p>
    <p>Size change: {comparison.sizeChange < 0 ? '−' : '+'}{backupBytes(Math.abs(comparison.sizeChange))}. File count change: {comparison.filesChange ?? 'Unknown'}.</p>
  </section>
}

function RestoreBackupReview({ review, unavailableReason, working, onConfirm, onCancel }: {
  review: BackupRestoreReview
  unavailableReason?: string
  working: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  const [accepted, setAccepted] = useState(false)
  const backup = review.backup
  return <section aria-label="Review Restore" className="recent-session-card" aria-busy={working}>
    <h4>Review Restore</h4>
    <dl style={{ overflowWrap: 'anywhere' }}>
      <dt>Name</dt><dd>{backup.label || 'Unnamed backup'}</dd>
      <dt>Backup identity</dt><dd style={{ overflowWrap: 'anywhere' }}>{backup.backupId}</dd>
      <dt>Created</dt><dd>{new Date(backup.createdUtc).toLocaleString()}</dd>
      <dt>Kind</dt><dd>{backupKindName(backup.backupKind)}</dd>
      <dt>Size</dt><dd>{backupBytes(backup.sizeBytes)} · {backup.fileCount ?? 'Unknown'} files</dd>
      <dt>Backup world</dt><dd>{backup.worldId ?? 'Unknown'}</dd>
      <dt>Current world</dt><dd>{review.currentWorld ?? 'Unknown'}</dd>
      <dt>Restore scope</dt><dd>World only. This action does not restore protected setup or game binaries.</dd>
    </dl>
    <p>{review.checkpoint}</p><p>{unavailableReason || review.reason}</p>
    <p>The selected backup is verified again. After Restore, test the world in the game and check a recognizable saved change after restart.</p>
    <label className="check"><Input type="checkbox" checked={accepted} disabled={working}
      onChange={event => setAccepted(event.target.checked)} />I reviewed this backup and the current world replacement.</label>
    <div className="actions"><Button className="secondary" disabled={working || !accepted || !review.canRestore || !!unavailableReason}
      onClick={onConfirm}>{working ? 'Restoring…' : 'Restore reviewed backup'}</Button>
      <Button className="text-button" disabled={working} onClick={onCancel}>Cancel Restore</Button></div>
  </section>
}

export function BackupCatalog({ profileId, visible, refreshKey = 0, loader = loadBackupCatalog, updater = updateBackupBookmark,
  bookmarkLoader, bookmarkAdapter, legacyLabels = false, onChanged, onLoaded, currentWorld = null, currentGame = null,
  offline = false, onVerify, onVault, onHashRehearsal, onOwnerGameRehearsal, onRestore, renderActions, unavailableReasons = {} }: BackupCatalogProps) {
  const titleId = useId()
  const [result, setResult] = useState<BackupCatalogResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(false)
  const [notice, setNotice] = useState('')
  const [filters, setFilters] = useState(emptyBackupFilters)
  const [expanded, setExpanded] = useState(false)
  const [filtersOpen, setFiltersOpen] = useState(false)
  const [comparisonIds, setComparisonIds] = useState<string[]>([])
  const [restoreId, setRestoreId] = useState<string | null>(null)
  const [previewCount, setPreviewCount] = useState<number | undefined>()
  const [working, setWorking] = useState<{ backupId: string; action: string } | null>(null)
  const pending = useRef<AbortController | null>(null)
  const actionPending = useRef<AbortController | null>(null)
  const onLoadedRef = useRef(onLoaded)
  onLoadedRef.current = onLoaded
  const load = useCallback(async () => {
    pending.current?.abort()
    const controller = new AbortController()
    pending.current = controller
    setLoading(true)
    setError(false)
    try {
      const next = bookmarkLoader && bookmarkAdapter ? bookmarkAdapter(await bookmarkLoader(profileId, controller.signal)) : await loader(profileId, controller.signal)
      if (controller.signal.aborted) return
      if (!next.ok || next.profileId.toLowerCase() !== profileId.toLowerCase()) { setError(true); return }
      setResult(next)
      onLoadedRef.current?.(next)
    } catch { if (!controller.signal.aborted) setError(true) }
    finally { if (!controller.signal.aborted) setLoading(false) }
  }, [loader, profileId, bookmarkLoader, bookmarkAdapter])
  useEffect(() => {
    setResult(null)
    setFilters(emptyBackupFilters)
    setExpanded(false)
    setFiltersOpen(false)
    setComparisonIds([])
    setRestoreId(null)
    setNotice('')
    setPreviewCount(undefined)
    setWorking(null)
    if (visible) void load()
    return () => { pending.current?.abort(); actionPending.current?.abort(); actionPending.current = null }
  }, [load, refreshKey, visible])

  function saved(backup: BackupBookmark) {
    setNotice(backup.pinned ? 'Saved. This backup is protected from automatic retention.' : 'Saved. This backup follows automatic retention.')
    setResult(current => {
      if (!current || current.profileId.toLowerCase() !== backup.profileId.toLowerCase()) return current
      const old = current.backups.find(item => item.backupId.toLowerCase() === backup.backupId.toLowerCase())
      if (!old) return current
      return { ...current, pinnedCount: current.pinnedCount + Number(backup.pinned) - Number(old.pinned),
        pinnedSizeBytes: current.pinnedSizeBytes + (backup.pinned ? backup.sizeBytes : 0) - (old.pinned ? old.sizeBytes : 0),
        backups: current.backups.map(item => item === old ? { ...item, label: backup.label, pinned: backup.pinned } : item) }
    })
    onChanged?.()
  }
  async function run(backup: BackupSummary, action: string, callback: BackupCatalogAction) {
    if (actionPending.current || !visible || backup.profileId.toLowerCase() !== profileId.toLowerCase()) return
    const controller = new AbortController()
    actionPending.current = controller
    setWorking({ backupId: backup.backupId, action })
    setNotice('')
    try {
      await callback(backup, controller.signal)
      if (controller.signal.aborted) return
      if (action === 'Restore') setRestoreId(null)
      await load()
      if (!controller.signal.aborted) onChanged?.()
    } catch { if (!controller.signal.aborted) setNotice(`${action} could not complete. Review this backup and try again.`) }
    finally {
      if (!controller.signal.aborted) setWorking(null)
      if (actionPending.current === controller) actionPending.current = null
    }
  }
  const catalog = result?.profileId.toLowerCase() === profileId.toLowerCase() ? result : null
  const filtered = catalog ? filterBackups(catalog.backups, filters) : []
  const activeFilters = Number(filters.pin !== 'all') + Number(filters.kind !== 'all') + Number(!!filters.after) + Number(!!filters.before)
  const shown = filtered.slice(0, expanded ? undefined : 5)
  const summary = catalog ? protectionSummary(catalog) : null
  const retention = catalog ? retentionPreview(catalog, previewCount) : null
  const compare = comparisonIds.map(id => catalog?.backups.find(backup => backup.backupId === id)).filter((item): item is BackupSummary => !!item)
  const restoreBackup = catalog?.backups.find(backup => backup.backupId === restoreId)
  const review = restoreBackup ? restoreReview(restoreBackup, currentWorld, currentGame, offline) : null
  const actions = (backup: BackupSummary) => {
    const callbacks: [string, keyof NonNullable<BackupCatalogProps['unavailableReasons']>, BackupCatalogAction | undefined][] = [
      ['Verify', 'verify', onVerify], ['Copy to vault', 'vault', onVault], ['Test restore hashes', 'hashRehearsal', onHashRehearsal],
      ['Test a copy in the game', 'ownerGameRehearsal', onOwnerGameRehearsal]
    ]
    return <>{callbacks.map(([label, key, callback]) => callback && <span key={key}><Button className="secondary"
      disabled={!!working || !!unavailableReasons[key]} onClick={() => void run(backup, label, callback)}>
      {working?.backupId === backup.backupId && working.action === label ? `${label}…` : label}</Button>
      {unavailableReasons[key] && <small>{unavailableReasons[key]}</small>}</span>)}
      {onRestore && <Button className="secondary" disabled={!!working} onClick={() => setRestoreId(backup.backupId)}>Review Restore</Button>}
      {renderActions?.(backup)}</>
  }
  return <section hidden={!visible} className="recent-sessions backup-bookmarks backup-catalog" aria-labelledby={titleId} aria-busy={loading}>
    <div className="recent-sessions-toolbar"><div><h4 id={titleId}>{legacyLabels ? 'Named and pinned backups' : 'Backups'}</h4>
      <p>Name, pin and review each completed copy here.</p></div>
      <Button className="secondary" disabled={loading || !!working} onClick={() => { setNotice(''); void load() }}>
        {loading ? 'Loading…' : legacyLabels ? 'Refresh names and pins' : 'Refresh backups'}</Button></div>
    {loading && !catalog && <p role="status">{legacyLabels ? 'Loading backup names and pins…' : 'Loading backups…'}</p>}
    {error && <div className="error" role="alert"><strong>{legacyLabels ? 'Backup names and pins unavailable' : 'Backups unavailable'}</strong>
      <p>Could not read completed backups on this Host. Refresh before using previous results.</p><Button className="secondary" onClick={() => void load()}>Try again</Button></div>}
    {!error && catalog && <>
      {summary && <div className="world-protection-summary" aria-label="Protection summary"><strong>Protection summary</strong>
        <p>{summary.newest ? `Newest catalog entry: ${new Date(summary.newest.createdUtc).toLocaleString()}.` : 'No completed backup recorded.'} {catalog.pinnedCount} pinned · {backupBytes(catalog.retainedSizeBytes)} retained · {backupBytes(catalog.availableSpaceBytes)} free.</p>
        {summary.incomplete && <p>Evidence coverage is incomplete. Missing results remain unknown.</p>}
        <details><summary>Protection and retention details</summary>
        <p>Latest recorded passes across {catalog.backups.length} shown copies: {summary.integrity} local integrity, {summary.vault} vault transfers, {summary.hashRehearsal} hash restore tests, {summary.ownerGame} owner-reported game rehearsals.</p>
        <p>A pin protects local retention. Each check measures a separate part of protection; no overall healthy-world claim is inferred.</p>
        <p>{catalog.pinnedCount} of {catalog.maximumPinnedCount} pins · {backupBytes(catalog.pinnedSizeBytes)} of {backupBytes(catalog.maximumPinnedSizeBytes)} pinned. {Math.max(0, catalog.maximumPinnedCount - catalog.pinnedCount)} pins and {backupBytes(Math.max(0, catalog.maximumPinnedSizeBytes - catalog.pinnedSizeBytes))} remain.</p>
        <small>Pins have separate count and byte limits. Available drive space is a separate measurement.</small>
        </details>
      </div>}
      {notice && <p role="status">{notice}</p>}
      <div className="backup-catalog-filters">
        <label htmlFor={`${titleId}-query`}>Search backups</label><Input id={`${titleId}-query`} value={filters.query} maxLength={128} placeholder="Name, pin, kind or date"
          onChange={event => { setFilters({ ...filters, query: event.target.value }); setExpanded(true) }} />
        <Button className="secondary" aria-expanded={filtersOpen} aria-controls={`${titleId}-filters`} onClick={() => setFiltersOpen(value => !value)}>Filter backups{activeFilters ? ` (${activeFilters} active)` : ''}</Button>
        <Button className="text-button" onClick={() => setFilters(emptyBackupFilters)}>Clear filters</Button>
      </div>
      <div id={`${titleId}-filters`} className="backup-catalog-filters" hidden={!filtersOpen}>
        <label htmlFor={`${titleId}-pin`}>Pins</label><Select id={`${titleId}-pin`} value={filters.pin}
          onChange={event => { setFilters({ ...filters, pin: event.target.value as typeof filters.pin }); setExpanded(true) }}>
          <option value="all">All backups</option><option value="pinned">Pinned only</option><option value="unpinned">Unpinned only</option></Select>
        <label htmlFor={`${titleId}-kind`}>Backup kind</label><Select id={`${titleId}-kind`} value={filters.kind}
          onChange={event => { setFilters({ ...filters, kind: event.target.value }); setExpanded(true) }}>
          <option value="all">All kinds</option><option value="Rolling">After Stop</option><option value="Manual">Manual</option><option value="PreRestore">Before restore</option><option value="Unavailable">Unknown</option></Select>
        <label htmlFor={`${titleId}-after`}>From date (local)</label><Input id={`${titleId}-after`} type="date" value={filters.after}
          onChange={event => { setFilters({ ...filters, after: event.target.value }); setExpanded(true) }} />
        <label htmlFor={`${titleId}-before`}>Through date (local)</label><Input id={`${titleId}-before`} type="date" value={filters.before}
          onChange={event => { setFilters({ ...filters, before: event.target.value }); setExpanded(true) }} />
      </div>
      <details><summary>Retention preview</summary>
        {catalog.retention && <><p>Saved policy: {catalog.retention.retentionCount} unpinned copies. Rolling backup is {catalog.retention.rollingEnabled ? 'on' : 'off'}. Free-space reserve: {backupBytes(catalog.retention.minimumFreeSpaceBytes)}.</p>
          <label htmlFor={`${titleId}-retention`}>Preview unpinned retention count</label><Input id={`${titleId}-retention`} type="number" min={1} max={50}
            value={previewCount ?? catalog.retention.retentionCount} onChange={event => setPreviewCount(Number(event.target.value))} />
          <small>This previews a count; change the saved policy in protection settings.</small></>}
        <p>{retention?.message}</p>
        {retention?.known && <p>{retention.candidates.length} current copies could become eligible after the next ordinary backup: {retention.candidates.map(item => item.label || `${backupKindName(item.backupKind)} ${new Date(item.createdUtc).toLocaleString()}`).join('; ') || 'None'}.</p>}
      </details>
      {catalog.backups.length === 0 ? <p>No completed backups yet. Make an offline backup to name or pin it.</p> :
        filtered.length === 0 ? <p>No backups match these filters.</p> : <p>{filtered.length} of {catalog.backups.length} loaded backups match.</p>}
      <div className="recent-session-list">{shown.map(backup => <BackupBookmarkEditor
        key={JSON.stringify([profileId, backup.backupId, backup.label, backup.pinned])} backup={backup} updater={updater} capacity={catalog} onSaved={saved}
        actions={<BackupReviewTools backup={backup} evidenceAvailable={catalog.evidenceAvailable}>{actions(backup)}</BackupReviewTools>}>
        <small>World: {backup.worldId ?? 'Unknown'} · {backup.setupIncluded === true ? 'Includes protected setup' : backup.setupIncluded === false ? 'World only' : 'Setup coverage unknown'}.</small>
        <BackupEvidenceCue backup={backup} evidenceAvailable={catalog.evidenceAvailable} />
        <label className="check"><Input type="checkbox" checked={comparisonIds.includes(backup.backupId)}
          disabled={comparisonIds.length === 2 && !comparisonIds.includes(backup.backupId)} onChange={event => setComparisonIds(current =>
            event.target.checked ? [...current, backup.backupId].slice(0, 2) : current.filter(id => id !== backup.backupId))} />Compare {backup.label || new Date(backup.createdUtc).toLocaleString()}</label>
      </BackupBookmarkEditor>)}</div>
      {!expanded && filtered.length > shown.length && <Button className="secondary" onClick={() => setExpanded(true)}>Show {filtered.length - shown.length} more backups</Button>}
      {comparisonIds.length > 0 && <div className="actions"><span>{compare.length} of 2 backups selected for comparison.</span><Button className="text-button" onClick={() => setComparisonIds([])}>Clear comparison</Button></div>}
      {compare.length === 2 && <BackupComparison left={compare[0]} right={compare[1]} />}
      {review && onRestore && <RestoreBackupReview key={JSON.stringify([profileId, review.backup.backupId, review.backup.createdUtc, review.backup.payloadSha256, review.currentWorld, currentGame, offline])}
        review={review} unavailableReason={unavailableReasons.restore} working={!!working} onCancel={() => setRestoreId(null)} onConfirm={() => {
          if (!review.canRestore || unavailableReasons.restore) return
          void run(review.backup, 'Restore', (_backup, signal) => onRestore(review, signal))
        }} />}
      {restoreId && !review && <p>The reviewed backup is no longer in this catalog. Choose a completed copy again.</p>}
      {catalog.moreBackupsAvailable && <p>Only 100 retained backups are loaded, with pins first. Filters and comparisons cover these rows; exact retention candidates require the complete catalog.</p>}
    </>}
    <p className="recent-sessions-boundary">Pins protect from automatic retention on this PC. Use Copy to vault for another location, and Verify for a separate integrity check.</p>
  </section>
}
