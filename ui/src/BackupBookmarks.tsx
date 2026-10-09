import { getLocalJson } from './api'
import { BackupCatalog, type BackupCatalogProps } from './BackupCatalog'
import { bookmarksAsCatalog } from './backupCatalogModel'
import { parseBackupBookmarks, type BackupBookmarksResult } from './backupBookmarksWire'

export type BackupBookmarksLoader = (profileId: string, signal?: AbortSignal) => Promise<BackupBookmarksResult>
export type { BackupBookmarkUpdater } from './BackupBookmarkEditor'
export { BackupBookmarkEditor } from './BackupBookmarkEditor'
const defaultLoader: BackupBookmarksLoader = (profileId, signal) =>
  getLocalJson(`/api/local/profiles/${encodeURIComponent(profileId)}/backup-bookmarks`, parseBackupBookmarks, signal)

export type BackupBookmarksProps = Omit<BackupCatalogProps, 'loader' | 'legacyLabels' | 'bookmarkLoader' | 'bookmarkAdapter'> & {
  loader?: BackupBookmarksLoader
  catalogLoader?: BackupCatalogProps['loader']
}

// Existing callers retain their bookmark loader and exports. The coordinator
// can supply catalogLoader (or use BackupCatalog) when wiring the richer route.
export function BackupBookmarks({ loader = defaultLoader, catalogLoader, ...props }: BackupBookmarksProps) {
  return <BackupCatalog {...props} loader={catalogLoader} bookmarkLoader={catalogLoader ? undefined : loader}
    bookmarkAdapter={bookmarksAsCatalog} legacyLabels={!catalogLoader} />
}
