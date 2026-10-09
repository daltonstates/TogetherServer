export type UiTheme = 'system' | 'light' | 'dark'
export type UiDensity = 'comfortable' | 'compact'
export type UiPreferences = {
  theme: UiTheme
  density: UiDensity
  textScale: 100 | 115 | 130 | 150
  highContrast: boolean
  quietMode: boolean
  selectedServer: string
  favoriteServers: string[]
  favoriteHosts: string[]
  serverOrder: string[]
  serverTabs: Record<string, string>
}

export const uiPreferencesKey = 'togetherserver.ui-preferences.v1'
export const defaultUiPreferences: UiPreferences = {
  theme: 'dark', density: 'comfortable', textScale: 100, highContrast: false,
  quietMode: false, selectedServer: '', favoriteServers: [], favoriteHosts: [],
  serverTabs: {}, serverOrder: []
}

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const tabs = new Set(['overview', 'chat', 'players', 'logs', 'sessions', 'backups', 'files', 'setup'])
const identifiers = (value: unknown): string[] => Array.isArray(value)
  ? [...new Set(value.filter((id): id is string => typeof id === 'string' && guid.test(id)))].slice(0, 100) : []

export function readUiPreferences(storage: Pick<Storage, 'getItem'> | null): UiPreferences {
  try {
    const saved = storage?.getItem(uiPreferencesKey) ?? 'null'
    if (saved.length > 100_000) return { ...defaultUiPreferences }
    const value: unknown = JSON.parse(saved)
    if (!value || typeof value !== 'object' || Array.isArray(value)) return { ...defaultUiPreferences }
    const source = value as Record<string, unknown>
    const rawTabs = source.serverTabs && typeof source.serverTabs === 'object' && !Array.isArray(source.serverTabs)
      ? source.serverTabs as Record<string, unknown> : {}
    return {
      theme: ['system', 'light', 'dark'].includes(String(source.theme)) ? source.theme as UiTheme : 'dark',
      density: source.density === 'compact' ? 'compact' : 'comfortable',
      textScale: typeof source.textScale === 'number' && [100, 115, 130, 150].includes(source.textScale) ? source.textScale as UiPreferences['textScale'] : 100,
      highContrast: source.highContrast === true, quietMode: source.quietMode === true,
      selectedServer: typeof source.selectedServer === 'string' && guid.test(source.selectedServer) ? source.selectedServer : '',
      favoriteServers: identifiers(source.favoriteServers), favoriteHosts: identifiers(source.favoriteHosts),
      serverOrder: identifiers(source.serverOrder),
      serverTabs: Object.fromEntries(Object.entries(rawTabs).filter(([id, tab]) => guid.test(id) && typeof tab === 'string' && tabs.has(tab)).slice(0, 100)) as Record<string, string>
    }
  } catch { return { ...defaultUiPreferences } }
}

export function writeUiPreferences(storage: Pick<Storage, 'setItem'> | null, preferences: UiPreferences): void {
  try {
    const projected = readUiPreferences({ getItem: () => JSON.stringify(preferences) })
    storage?.setItem(uiPreferencesKey, JSON.stringify(projected))
  }
  catch { /* View preferences are optional and never block hosting. */ }
}
