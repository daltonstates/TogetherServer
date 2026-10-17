import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { getLocalJson } from './api'
import { Button, Input, Select } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import { Icon } from './Icon'
import { SettingsSectionHeading } from './SettingsPresentation'
import { BackupBookmarkEditor, updateBackupBookmark, type BackupBookmarkUpdater } from './BackupBookmarkEditor'
import type { BackupBookmark, BackupBookmarksResult } from './backupBookmarksWire'
import { parseBackupCatalog } from './backupCatalogWire'
import { backupBytes, backupKindName, compareBackups, emptyBackupFilters, filterBackups, latestBackupEvidence,
  protectionSummary, restoreReview, retentionPreview, type BackupCatalogResult, type BackupEvidenceKind,
  type BackupRestoreReview, type BackupSummary } from './backupCatalogModel'
import './BackupCatalogPresentation.css'

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
  showOverview?: boolean
}

export const loadBackupCatalog: BackupCatalogLoader = (profileId, signal) =>
  getLocalJson(`/api/local/profiles/${encodeURIComponent(profileId)}/backup-catalog`, parseBackupCatalog, signal)

const evidenceNames: Record<BackupEvidenceKind, string> = {
  Integrity: 'Local integrity', Vault: 'Vault copy', HashRehearsal: 'Hash restore test', OwnerGameRehearsal: 'Game load, change and restart'
}
function BackupEvidenceDetails({ backup, evidenceAvailable }: { backup: BackupSummary; evidenceAvailable: boolean }) {
  return <>
    <h4>Protection results</h4>
    <div className="backup-library-evidence-grid" aria-label="Recorded protection results">{(Object.keys(evidenceNames) as BackupEvidenceKind[]).map(kind => {
      const fact = latestBackupEvidence(backup, kind)
      return <div className={`backup-library-evidence-result${fact?.outcome === 'Failed' ? ' has-issue' : ''}`} key={kind}>
        <Icon name={kind === 'OwnerGameRehearsal' ? 'game' : kind === 'Vault' ? 'archive' : 'shield'} size={18} />
        <small><strong>{evidenceNames[kind]}: </strong><span>{fact ?
        `${kind === 'OwnerGameRehearsal' ? 'Owner reported: ' : ''}${fact.outcome === 'Passed' ? 'Passed' : fact.outcome === 'Failed' ? 'Failed' : 'Incomplete'} · ${new Date(fact.checkedUtc).toLocaleString()}` :
        evidenceAvailable ? 'Unknown — no retained result for this backup' : 'Unknown — saved evidence unavailable'}</span></small>
      </div>
    })}</div>
    <p>Each dated result applies to this exact backup. A vault copy confirms transferred hashes at that time; its location and continued availability are unknown here.</p>
    <p>A hash restore test checks bytes in a disposable copy. Game load, a saved change and restart require separate owner confirmations. A passed hash check does not establish game save health.</p>
  </>
}

function BackupReviewTools({ backup, evidenceAvailable, children }: { backup: BackupSummary; evidenceAvailable: boolean; children: ReactNode }) {
  const [open, setOpen] = useState(false)
  const panelId = useId()
  return <div className={`backup-library-review-tools${open ? ' is-open' : ''}`}>
    <Button className="secondary backup-library-review-toggle" aria-expanded={open} aria-controls={panelId} onClick={() => setOpen(value => !value)}>
      <Icon name="eye" />Review backup<Icon name={open ? 'chevronDown' : 'chevronRight'} size={15} />
    </Button>
    <div id={panelId} className="backup-library-review-panel" hidden={!open}>
      <BackupEvidenceDetails backup={backup} evidenceAvailable={evidenceAvailable} />
      <div className="actions backup-library-check-actions">{children}</div>
    </div>
  </div>
}

function BackupEvidenceCue({ backup, evidenceAvailable }: { backup: BackupSummary; evidenceAvailable: boolean }) {
  const failed = (Object.keys(evidenceNames) as BackupEvidenceKind[]).filter(kind => latestBackupEvidence(backup, kind)?.outcome === 'Failed')
  const incomplete = (Object.keys(evidenceNames) as BackupEvidenceKind[]).filter(kind => kind !== 'Integrity' && latestBackupEvidence(backup, kind)?.outcome === 'Incomplete')
  const integrity = latestBackupEvidence(backup, 'Integrity')
  const unknown = !evidenceAvailable ? 'Saved evidence unavailable' : !integrity ? 'Integrity not checked' :
    integrity.outcome === 'Incomplete' ? 'Integrity check incomplete' : null
  return <div className="backup-library-evidence-cue">
    <div className="backup-library-badges">
      {integrity?.outcome === 'Passed' && evidenceAvailable && <span className="backup-library-badge is-measured"><Icon name="shield" size={14} />Integrity passed</span>}
      {failed.length > 0 && <span className="backup-library-badge has-issue"><Icon name="warning" size={14} />Recorded check failed: {failed.map(kind => evidenceNames[kind]).join(', ')}.</span>}
      {unknown && <span className="backup-library-badge"><Icon name="shield" size={14} />{unknown}.</span>}
      {incomplete.length > 0 && <span className="backup-library-badge has-issue"><Icon name="warning" size={14} />Incomplete: {incomplete.map(kind => evidenceNames[kind]).join(', ')}.</span>}
      {!backup.metadataAvailable && <span className="backup-library-badge">Completion details unavailable</span>}
    </div>
    <small>{integrity?.outcome === 'Passed' && evidenceAvailable ? 'Recorded integrity passed; game save health unverified.' : 'Game save health unverified.'}</small>
  </div>
}

function BackupComparison({ left, right }: { left: BackupSummary; right: BackupSummary }) {
  const comparison = compareBackups(left, right)
  return <section aria-label="Backup comparison" className="recent-session-card backup-library-comparison">
    <h4><Icon name="copy" size={19} />Compare backup summaries</h4>
    <p>Recorded manifest and setup fingerprints only. This is not a file-content diff or a fresh integrity check.</p>
    <p>Protected setup hashes compare stored checkpoint bytes. Different hashes can reflect protection encoding and do not by themselves prove different settings.</p>
    <table><caption>{left.label || 'Unnamed backup'} → {right.label || 'Unnamed backup'}</caption>
      <thead><tr><th scope="col">Summary</th><th scope="col">First backup</th><th scope="col">Second backup</th></tr></thead>
      <tbody>
        <tr><th scope="row">Backup identity</th><td>{left.backupId}</td><td>{right.backupId}</td></tr>
        <tr><th scope="row">Created</th><td>{new Date(left.createdUtc).toLocaleString()}</td><td>{new Date(right.createdUtc).toLocaleString()}</td></tr>
        <tr><th scope="row">Kind</th><td>{backupKindName(left.backupKind)}</td><td>{backupKindName(right.backupKind)}</td></tr>
        <tr><th scope="row">World</th><td>{left.worldId ?? 'Unknown'}</td><td>{right.worldId ?? 'Unknown'}</td></tr>
        <tr><th scope="row">Game</th><td>{left.gameKind ?? 'Unknown'}</td><td>{right.gameKind ?? 'Unknown'}</td></tr>
        <tr><th scope="row">Size</th><td>{backupBytes(left.sizeBytes)}</td><td>{backupBytes(right.sizeBytes)}</td></tr>
        <tr><th scope="row">Files</th><td>{left.fileCount ?? 'Unknown'}</td><td>{right.fileCount ?? 'Unknown'}</td></tr>
        <tr><th scope="row">Recorded payload fingerprint</th><td>{left.payloadSha256 ?? 'Unknown'}</td><td>{right.payloadSha256 ?? 'Unknown'}</td></tr>
        <tr><th scope="row">Recorded setup fingerprint</th><td>{left.setupSha256 ?? (left.setupIncluded === false ? 'World only' : 'Unknown')}</td><td>{right.setupSha256 ?? (right.setupIncluded === false ? 'World only' : 'Unknown')}</td></tr>
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
  return <section aria-label="Review Restore" className="recent-session-card backup-library-restore" aria-busy={working}>
    <h4><Icon name="warning" size={19} />Review Restore</h4>
    <dl>
      <dt>Name</dt><dd>{backup.label || 'Unnamed backup'}</dd>
      <dt>Backup identity</dt><dd>{backup.backupId}</dd>
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
  bookmarkLoader, bookmarkAdapter, legacyLabels = false, showOverview = true, onChanged, onLoaded, currentWorld = null, currentGame = null,
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
  return <section hidden={!visible} className="backup-bookmarks backup-catalog backup-library" aria-label={legacyLabels ? 'Named and pinned backups' : 'Backup catalog'} aria-busy={loading}>
    <header className="backup-library-header">
      <SettingsSectionHeading icon="archive" title={legacyLabels ? 'Named and pinned backups' : 'Backup library'}
        description="Completed copies on this Host. Find a backup, keep the ones you need, and review its protection." />
      <Button className="secondary" disabled={loading || !!working} onClick={() => { setNotice(''); void load() }}>
        <Icon name={loading ? 'loader' : 'refresh'} />{loading ? 'Loading…' : legacyLabels ? 'Refresh names and pins' : 'Refresh backups'}
      </Button>
    </header>
    {loading && !catalog && <div className="backup-library-state" role="status"><Icon name="loader" size={22} />
      <p>{legacyLabels ? 'Loading backup names and pins…' : 'Loading backups…'}</p></div>}
    {error && <DismissibleNotice scope={profileId} className="error" role="alert"><strong>{legacyLabels ? 'Backup names and pins unavailable' : 'Backups unavailable'}</strong>
      <p>Could not read completed backups on this Host. Refresh before using previous results.</p><Button className="secondary" onClick={() => void load()}>Try again</Button></DismissibleNotice>}
    {!error && catalog && <>
      {summary && showOverview && <div className="backup-library-overview" aria-label="Protection summary">
        <dl className="backup-library-summary-cards">
          <div className="backup-library-summary-card">
            <dt><Icon name="clock" size={17} />{catalog.moreBackupsAvailable ? 'Latest loaded copy' : 'Latest completed'}</dt>
            <dd className="backup-library-summary-value is-date">{summary.newest ?
              <time dateTime={summary.newest.createdUtc}>{new Date(summary.newest.createdUtc).toLocaleDateString()}</time> : 'None yet'}</dd>
            <dd className="backup-library-summary-detail">{summary.newest ?
              `${new Date(summary.newest.createdUtc).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })} · ${backupKindName(summary.newest.backupKind)}` : 'No completed backup recorded.'}</dd>
          </div>
          <div className="backup-library-summary-card">
            <dt><Icon name="archive" size={17} />{catalog.moreBackupsAvailable ? 'Loaded copies' : 'Completed copies'}</dt>
            <dd className="backup-library-summary-value">{catalog.backups.length}</dd>
            <dd className="backup-library-summary-detail">{catalog.moreBackupsAvailable ? 'More retained copies are available' : 'In this local library'}</dd>
          </div>
          <div className="backup-library-summary-card">
            <dt><Icon name="server" size={17} />Backup storage</dt>
            <dd className="backup-library-summary-value">{backupBytes(catalog.retainedSizeBytes)}</dd>
            <dd className="backup-library-summary-detail">{catalog.availableSpaceBytes === null ? 'Backup drive free space unknown' : `${backupBytes(catalog.availableSpaceBytes)} free on backup drive`}</dd>
          </div>
          <div className="backup-library-summary-card">
            <dt><Icon name="shield" size={17} />Pinned copies</dt>
            <dd className="backup-library-summary-value">{catalog.pinnedCount}<span> / {catalog.maximumPinnedCount}</span></dd>
            <dd className="backup-library-summary-detail">{backupBytes(catalog.pinnedSizeBytes)} of {backupBytes(catalog.maximumPinnedSizeBytes)} pinned</dd>
          </div>
        </dl>
        {summary.incomplete && <p className="backup-library-coverage"><Icon name="shield" size={16} />Evidence coverage is incomplete. Missing results remain unknown.</p>}
      </div>}
      {notice && <DismissibleNotice as="p" scope={profileId} role="status">{notice}</DismissibleNotice>}
      {working && <p className="backup-library-progress" role="status"><Icon name="loader" />{working.action} is in progress for the selected backup.</p>}
      <div className="backup-library-toolbar" role="search" aria-label="Find backups">
        <label className="backup-library-search" htmlFor={`${titleId}-query`}>
          <span>Search backups</span>
          <span className="backup-library-search-field"><Icon name="search" size={18} />
            <Input id={`${titleId}-query`} value={filters.query} maxLength={128} placeholder="Name, pin, kind or date"
              onChange={event => { setFilters({ ...filters, query: event.target.value }); setExpanded(true) }} />
          </span>
        </label>
        <div className="backup-library-filter-actions">
          <Button className="secondary" aria-expanded={filtersOpen} aria-controls={`${titleId}-filters`} onClick={() => setFiltersOpen(value => !value)}>
            <Icon name="settings" size={16} />Filter backups{activeFilters ? ` (${activeFilters} active)` : ''}<Icon name={filtersOpen ? 'chevronDown' : 'chevronRight'} size={14} />
          </Button>
          <Button className="text-button" disabled={!filters.query && !activeFilters} onClick={() => setFilters(emptyBackupFilters)}>Clear filters</Button>
        </div>
        <div id={`${titleId}-filters`} className="backup-library-filter-options" hidden={!filtersOpen}>
          <label htmlFor={`${titleId}-pin`}>Pins<Select id={`${titleId}-pin`} value={filters.pin}
            onChange={event => { setFilters({ ...filters, pin: event.target.value as typeof filters.pin }); setExpanded(true) }}>
            <option value="all">All backups</option><option value="pinned">Pinned only</option><option value="unpinned">Unpinned only</option></Select></label>
          <label htmlFor={`${titleId}-kind`}>Backup kind<Select id={`${titleId}-kind`} value={filters.kind}
            onChange={event => { setFilters({ ...filters, kind: event.target.value }); setExpanded(true) }}>
            <option value="all">All kinds</option><option value="Rolling">After Stop</option><option value="Manual">Manual</option><option value="PreRestore">Before restore</option><option value="Unavailable">Unknown</option></Select></label>
          <label htmlFor={`${titleId}-after`}>From date (local)<Input id={`${titleId}-after`} type="date" value={filters.after}
            onChange={event => { setFilters({ ...filters, after: event.target.value }); setExpanded(true) }} /></label>
          <label htmlFor={`${titleId}-before`}>Through date (local)<Input id={`${titleId}-before`} type="date" value={filters.before}
            onChange={event => { setFilters({ ...filters, before: event.target.value }); setExpanded(true) }} /></label>
        </div>
      </div>
      <div className="backup-library-list-heading">
        <p>{catalog.backups.length === 0 ? 'No completed backups yet.' : filtered.length === 0 ? 'No backups match these filters.' : `${filtered.length} of ${catalog.backups.length} loaded backups match.`}</p>
        {catalog.backups.length > 0 && <small>Pinned first · newest in each group</small>}
      </div>
      {catalog.backups.length === 0 && <div className="backup-library-state"><Icon name="archive" size={28} /><p>Make an offline backup to name, pin or review a completed copy here.</p></div>}
      {catalog.backups.length > 0 && filtered.length === 0 && <div className="backup-library-state"><Icon name="search" size={26} /><p>Try another search or clear the filters to see your completed copies.</p></div>}
      {comparisonIds.length > 0 && <div className="backup-library-comparison-selection"><span><Icon name="copy" />{compare.length} of 2 backups selected for comparison.</span>
        <Button className="text-button" onClick={() => setComparisonIds([])}>Clear comparison</Button></div>}
      <div className="backup-library-list" aria-label="Completed backup copies">{shown.map(backup => <BackupBookmarkEditor
        key={JSON.stringify([profileId, backup.backupId, backup.label, backup.pinned])} backup={backup} updater={updater} capacity={catalog} onSaved={saved}
        actions={<div className="backup-library-row-controls">
          <BackupReviewTools backup={backup} evidenceAvailable={catalog.evidenceAvailable}>{actions(backup)}</BackupReviewTools>
          <label className="check backup-library-compare"><Input type="checkbox" checked={comparisonIds.includes(backup.backupId)}
            disabled={comparisonIds.length === 2 && !comparisonIds.includes(backup.backupId)} onChange={event => setComparisonIds(current =>
              event.target.checked ? [...current, backup.backupId].slice(0, 2) : current.filter(id => id !== backup.backupId))} />
            <span aria-hidden="true">Compare</span><span className="backup-library-sr-only">Compare {backup.label || new Date(backup.createdUtc).toLocaleString()}</span>
          </label>
        </div>}>
        <span className="backup-library-copy-icon"><Icon name="archive" size={21} /></span>
        <div className="backup-library-copy-context"><span>World: {backup.worldId ?? 'Unknown'}</span>
          <span>{backup.setupIncluded === true ? 'Includes protected setup' : backup.setupIncluded === false ? 'World only' : 'Setup coverage unknown'} · {backup.fileCount === null ? 'File count unknown' : `${backup.fileCount} ${backup.fileCount === 1 ? 'file' : 'files'}`}</span></div>
        <BackupEvidenceCue backup={backup} evidenceAvailable={catalog.evidenceAvailable} />
      </BackupBookmarkEditor>)}</div>
      {!expanded && filtered.length > shown.length && <Button className="secondary backup-library-show-more" onClick={() => setExpanded(true)}><Icon name="chevronDown" />Show {filtered.length - shown.length} more backups</Button>}
      {compare.length === 2 && <BackupComparison left={compare[0]} right={compare[1]} />}
      {review && onRestore && <RestoreBackupReview key={JSON.stringify([profileId, review.backup.backupId, review.backup.createdUtc, review.backup.payloadSha256, review.currentWorld, currentGame, offline])}
        review={review} unavailableReason={unavailableReasons.restore} working={!!working} onCancel={() => setRestoreId(null)} onConfirm={() => {
          if (!review.canRestore || unavailableReasons.restore) return
          void run(review.backup, 'Restore', (_backup, signal) => onRestore(review, signal))
        }} />}
      {restoreId && !review && <DismissibleNotice as="p" scope={profileId} noticeKey={restoreId}>The reviewed backup is no longer in this catalog. Choose a completed copy again.</DismissibleNotice>}
      {catalog.moreBackupsAvailable && <p className="backup-library-coverage"><Icon name="warning" size={17} />Only 100 retained backups are loaded, with pins first. Filters and comparisons cover these rows; exact retention candidates require the complete catalog.</p>}
      <div className="backup-library-secondary">
        <details className="backup-library-policy"><summary><Icon name="shield" size={17} />Protection and retention details</summary>
          <div className="backup-library-policy-body">
            {summary && <p>Latest recorded passes across {catalog.backups.length} shown copies: {summary.integrity} local integrity, {summary.vault} vault transfers, {summary.hashRehearsal} hash restore tests, {summary.ownerGame} owner-reported game rehearsals.</p>}
            <p>A pin protects local retention. Each check measures a separate part of protection; no overall healthy-world claim is inferred.</p>
            <p>{catalog.pinnedCount} of {catalog.maximumPinnedCount} pins · {backupBytes(catalog.pinnedSizeBytes)} of {backupBytes(catalog.maximumPinnedSizeBytes)} pinned. {Math.max(0, catalog.maximumPinnedCount - catalog.pinnedCount)} pins and {backupBytes(Math.max(0, catalog.maximumPinnedSizeBytes - catalog.pinnedSizeBytes))} remain.</p>
            <small>Pins have separate count and byte limits. Available drive space is a separate measurement.</small>
          </div>
        </details>
        <details className="backup-library-policy"><summary><Icon name="clock" size={17} />Retention preview</summary>
          <div className="backup-library-policy-body">
            {catalog.retention && <><p>Saved policy: {catalog.retention.retentionCount} unpinned copies. Rolling backup is {catalog.retention.rollingEnabled ? 'on' : 'off'}. Free-space reserve: {backupBytes(catalog.retention.minimumFreeSpaceBytes)}.</p>
              <label htmlFor={`${titleId}-retention`}>Preview unpinned retention count<Input id={`${titleId}-retention`} type="number" min={1} max={50}
                value={previewCount ?? catalog.retention.retentionCount} onChange={event => setPreviewCount(Number(event.target.value))} /></label>
              <small>This previews a count; change the saved policy in protection settings.</small></>}
            <p>{retention?.message}</p>
            {retention?.known && <p>{retention.candidates.length} current copies could become eligible after the next ordinary backup: {retention.candidates.map(item => item.label || `${backupKindName(item.backupKind)} ${new Date(item.createdUtc).toLocaleString()}`).join('; ') || 'None'}.</p>}
          </div>
        </details>
      </div>
    </>}
    <p className="backup-library-boundary"><Icon name="shield" size={17} />Pins protect from automatic retention on this PC. Use Copy to vault for another location, and Verify for a separate integrity check.</p>
  </section>
}
