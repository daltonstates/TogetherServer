import { useCallback, useEffect, useRef, useState } from 'react'
import { Button } from './Controls'
import { errorMessage } from './api'
import { clearProtectedDraft, readProtectedDraft, saveProtectedDraft, type DraftIdentity } from './protectedUiDrafts'

export type EditorDraftStateChange = (source: string, dirty: boolean) => void
export type EditorDraftGuard = { dirty: boolean; saving: boolean; flush: () => Promise<boolean> }
export type EditorDraftGuardChange = (source: string, guard: EditorDraftGuard | null) => void
type DraftState = { recovered: string | null; status: 'loading' | 'ready' | 'saving' | 'saved' | 'error' | 'conflict'; message: string }
type Scope = {
  identity: DraftIdentity; revision: number; loaded: boolean; blocked: boolean; disposed: boolean;
  latest: string | null; lastSaved: string | null; clearedValue: string | null; chain: Promise<unknown>; reading: Promise<unknown>
}

/** A draft never replaces the live file, and a recovered draft always needs owner review. */
export function useEditorProtectedDraft(identity: DraftIdentity | null, value: string | null) {
  const [state, setState] = useState<DraftState>({ recovered: null, status: 'loading', message: '' })
  const scopeRef = useRef<Scope | null>(null)
  const valueRef = useRef(value)
  valueRef.current = value
  const purpose = identity?.purpose
  const profileId = identity?.profileId
  const connectionId = identity?.connectionId ?? null
  const key = identity?.key
  const scopeKey = identity ? JSON.stringify([purpose, profileId, connectionId, key]) : ''

  const persist = (scope: Scope, text: string): Promise<boolean> => {
    const operation = scope.chain.then(async () => {
      await scope.reading
      if (!scope.loaded || scope.blocked) return false
      if (scope.lastSaved === text || scope.clearedValue === text) return true
      if (!scope.disposed) setState(current => ({ ...current, status: 'saving', message: 'Keeping a protected draft on this PC…' }))
      try {
        const result = await saveProtectedDraft(scope.identity, text, scope.revision)
        scope.revision = result.revision
        if (!result.ok) {
          scope.blocked = true
          if (!scope.disposed) setState({ recovered: result.text, status: 'conflict', message: result.message })
          return false
        }
        scope.lastSaved = text
        if (!scope.disposed) setState({ recovered: null, status: 'saved', message: 'Protected draft kept on this PC. Changes are not applied to the server.' })
        return true
      } catch (error) {
        if (!scope.disposed) setState(current => ({ ...current, status: 'error', message: errorMessage(error) }))
        return false
      }
    })
    scope.chain = operation
    return operation
  }
  const persistRef = useRef(persist)
  persistRef.current = persist

  useEffect(() => {
    if (!purpose || !profileId || !key) { scopeRef.current = null; return }
    const scope: Scope = { identity: { purpose, profileId, connectionId, key }, revision: 0, loaded: false,
      blocked: false, disposed: false, latest: valueRef.current, lastSaved: null, clearedValue: null, chain: Promise.resolve(), reading: Promise.resolve() }
    scopeRef.current = scope
    setState({ recovered: null, status: 'loading', message: '' })
    const abort = new AbortController()
    scope.reading = readProtectedDraft(scope.identity, abort.signal).then(result => {
      if (scope.disposed) return
      scope.revision = result.revision
      scope.loaded = result.ok
      scope.blocked = result.text !== null || !result.ok
      scope.lastSaved = result.text
      setState({ recovered: result.text, status: result.ok ? 'ready' : 'error', message: result.ok ? '' : result.message })
      if (result.ok && result.text === null && scope.latest !== null)
        void persistRef.current(scope, scope.latest)
    }).catch(error => {
      scope.blocked = true
      if (!scope.disposed) setState({ recovered: null, status: 'error', message: errorMessage(error) })
    })
    return () => {
      abort.abort()
      // Preserve the last edit even when navigation happens inside the debounce window.
      // The captured scope and CAS revision are retained; no write can adopt a new profile identity.
      if (scope.latest !== null && scope.loaded && !scope.blocked)
        void persistRef.current(scope, scope.latest)
      scope.disposed = true
      if (scopeRef.current === scope) scopeRef.current = null
    }
  }, [scopeKey, purpose, profileId, connectionId, key])

  useEffect(() => {
    const scope = scopeRef.current
    if (!scope) return
    scope.latest = value
    if (value === null || !scope.loaded || scope.blocked || scope.lastSaved === value || scope.clearedValue === value) return
    const timer = window.setTimeout(() => { void persistRef.current(scope, value) }, 650)
    return () => window.clearTimeout(timer)
  }, [scopeKey, value, state.status, state.recovered])

  const clear = async (options: { keepCurrent?: boolean } = {}) => {
    const scope = scopeRef.current
    if (!scope) return false
    await scope.reading
    if (!scope.loaded) return false
    // Freeze autosave while all earlier writes finish, then clear their exact resulting revision.
    scope.blocked = true
    scope.latest = null
    const operation = scope.chain.then(async () => {
      if (!scope.disposed) setState(current => ({ ...current, status: 'saving', message: 'Clearing this protected draft…' }))
      try {
        const result = await clearProtectedDraft(scope.identity, scope.revision)
        scope.revision = result.revision
        scope.blocked = !result.ok
        if (result.ok) { scope.lastSaved = null; scope.clearedValue = options.keepCurrent ? null : valueRef.current }
        if (!scope.disposed) setState({ recovered: result.ok ? null : result.text,
          status: result.ok ? 'ready' : 'conflict', message: result.ok ? '' : result.message })
        return result.ok
      } catch (error) {
        if (!scope.disposed) setState(current => ({ ...current, status: 'error', message: errorMessage(error) }))
        return false
      }
    })
    scope.chain = operation
    return operation
  }
  const reloadRecovery = async () => {
    const scope = scopeRef.current
    if (!scope) return
    await scope.chain
    try {
      const result = await readProtectedDraft(scope.identity)
      if (scope.disposed) return
      scope.revision = result.revision
      scope.loaded = result.ok
      scope.blocked = result.text !== null || !result.ok
      scope.lastSaved = result.text
      setState({ recovered: result.text, status: result.ok ? 'ready' : 'error', message: result.ok ? '' : result.message })
    } catch (error) {
      if (!scope.disposed) setState(current => ({ ...current, status: 'error', message: errorMessage(error) }))
    }
  }
  return { ...state, clear, reloadRecovery, persistNow: async () => {
    const scope = scopeRef.current
    const currentValue = valueRef.current
    if (currentValue === null) return true
    if (!scope) return false
    const saved = await persistRef.current(scope, currentValue)
    // An earlier value being durable does not authorize leaving a newer edit or another scope.
    return saved && scopeRef.current === scope && !scope.disposed && valueRef.current === currentValue
  }, acceptRecovery: () => {
    const scope = scopeRef.current
    if (!scope?.loaded) return
    scope.blocked = false
    setState({ recovered: null, status: 'ready', message: 'Recovered draft is in the editor. Review it before saving.' })
  } }
}

export function useEditorDraftGuard(source: string, dirty: boolean,
  draft: Pick<ReturnType<typeof useEditorProtectedDraft>, 'persistNow' | 'status'>, onChange?: EditorDraftGuardChange) {
  const draftRef = useRef(draft)
  draftRef.current = draft
  const flush = useCallback(() => draftRef.current.persistNow(), [])
  const saving = draft.status === 'saving' || draft.status === 'loading'
  useEffect(() => { onChange?.(source, { dirty, saving, flush }) }, [source, dirty, saving, flush, onChange])
  useEffect(() => () => onChange?.(source, null), [source, onChange])
}

export function useEditorDirtyGuard(source: string, dirty: boolean, onChange?: EditorDraftStateChange) {
  useEffect(() => {
    onChange?.(source, dirty)
    if (!dirty) return
    const beforeUnload = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', beforeUnload)
    return () => window.removeEventListener('beforeunload', beforeUnload)
  }, [source, dirty, onChange])
  useEffect(() => () => onChange?.(source, false), [source, onChange])
}

export function EditorDraftRecovery({ recovered, message, onRecover, onDiscard, disabled = false }: {
  recovered: string | null; message: string; onRecover: () => void; onDiscard: () => void; disabled?: boolean
}) {
  if (recovered === null) return message ? <p className="helper-text" role="status">{message}</p> : null
  return <section className="notice" aria-label="Recovered editor draft">
    <strong>A protected draft is available on this PC.</strong>
    <p>Your saved file is unchanged. Put the draft into the editor to review it against the current file, or discard the draft.</p>
    {message && <p>{message}</p>}
    <div className="actions"><Button className="secondary" disabled={disabled} onClick={onRecover}>Review recovered draft</Button>
      <Button className="text-button" disabled={disabled} onClick={onDiscard}>Discard recovered draft</Button></div>
  </section>
}
