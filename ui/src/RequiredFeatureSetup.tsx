import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { ContractError, type Decoder } from './contracts'
import { Button } from './Controls'
import './RequiredFeatureSetup.css'

export type RequiredFeature = { id: string; revision: number; title: string; description: string; completed: boolean }
export type RequiredSetupView = { features: RequiredFeature[] }
export type RequiredSetupResult = RequiredSetupView & { ok: boolean; code: string; message: string }
export type RequiredFeatureRegistry = Readonly<Record<string, { revision: number }>>
export const appRequiredFeatures: RequiredFeatureRegistry = { 'profile-name': { revision: 1 } }

export const parseRequiredSetup: Decoder<RequiredSetupView> = (value, context = 'required setup') => {
  if (!value || typeof value !== 'object' || !('features' in value) || !Array.isArray(value.features) || value.features.length > 128)
    throw new ContractError(`${context}.features must be a bounded list.`)
  const ids = new Set<string>()
  const features = value.features.map((feature: unknown) => {
    if (!feature || typeof feature !== 'object') throw new ContractError(`${context} has an invalid feature.`)
    const item = feature as Record<string, unknown>
    if (typeof item.id !== 'string' || !/^[a-z0-9-]{1,64}$/.test(item.id) || ids.has(item.id) ||
      typeof item.revision !== 'number' || !Number.isSafeInteger(item.revision) || item.revision < 1 || item.revision > 10000 ||
      typeof item.title !== 'string' || !item.title.trim() || item.title.length > 80 ||
      typeof item.description !== 'string' || !item.description.trim() || item.description.length > 320 || typeof item.completed !== 'boolean')
      throw new ContractError(`${context} has an invalid or duplicate feature.`)
    ids.add(item.id)
    return { id: item.id, revision: item.revision, title: item.title, description: item.description, completed: item.completed }
  })
  return { features }
}

export const parseRequiredSetupResult: Decoder<RequiredSetupResult> = (value, context = 'required setup result') => {
  const view = parseRequiredSetup(value, context)
  const item = value as Record<string, unknown>
  if (typeof item.ok !== 'boolean' || typeof item.code !== 'string' || typeof item.message !== 'string')
    throw new ContractError(`${context} must include the completion result.`)
  return { ...view, ok: item.ok, code: item.code, message: item.message }
}

function validateRegistry(view: RequiredSetupView, registry: RequiredFeatureRegistry) {
  if (Object.keys(registry).some(id => !view.features.some(feature => feature.id === id)))
    throw new Error('Could not load setup. Try again.')
  if (view.features.some(feature => registry[feature.id]?.revision !== feature.revision))
    throw new Error('This setup step needs a newer app version. Try again or update TogetherServer.')
  return view
}

export function useRequiredFeatureSetup(registry: RequiredFeatureRegistry = appRequiredFeatures) {
  const [view, setView] = useState<RequiredSetupView | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const inFlight = useRef(false)
  const reload = useCallback(async (signal?: AbortSignal) => {
    setLoading(true); setError('')
    try {
      const result = validateRegistry(await getLocalJson('/api/local/required-setup', parseRequiredSetup, signal), registry)
      if (signal?.aborted) return false
      setView(result)
      return true
    } catch (failure) {
      if (!signal?.aborted) setError(errorMessage(failure))
      return false
    } finally { if (!signal?.aborted) setLoading(false) }
  }, [registry])
  useEffect(() => {
    const controller = new AbortController()
    void reload(controller.signal)
    return () => controller.abort()
  }, [reload])
  const complete = async (feature: RequiredFeature) => {
    if (inFlight.current || loading || !view?.features.some(item => item.id === feature.id && item.revision === feature.revision && !item.completed)) return false
    inFlight.current = true; setBusy(true); setError('')
    try {
      const result = await changeJson(`/api/local/required-setup/${encodeURIComponent(feature.id)}/complete`, 'POST', parseRequiredSetupResult, { revision: feature.revision })
      if (!result.ok) { setError(result.message); return false }
      validateRegistry(result, registry)
      // A mutation response never reveals the app. Recheck the authoritative settings.
      return await reload()
    } catch (failure) { setError(errorMessage(failure)); return false }
    finally { inFlight.current = false; setBusy(false) }
  }
  return { view, loading, busy, error, reload, complete,
    ready: !!view && !loading && !error && view.features.every(feature => feature.completed) }
}

export type RequiredSetupController = ReturnType<typeof useRequiredFeatureSetup>
export type RequiredStepRenderer = (feature: RequiredFeature, complete: () => Promise<boolean>, busy: boolean) => ReactNode

export function RequiredFeatureSetup({ setup, renderStep, safety, appName = 'TogetherServer' }: {
  setup: RequiredSetupController; renderStep: RequiredStepRenderer; safety?: ReactNode; appName?: string
}) {
  const pending = setup.view?.features.find(feature => !feature.completed)
  const step = pending && renderStep(pending, () => setup.complete(pending), setup.busy || setup.loading)
  const error = setup.error || (pending && !step ? 'This setup step is unavailable. Try again or update TogetherServer.' : '')
  return <div className="shell required-setup-shell">
    <header className="topbar"><div className="brand"><span className="brand-mark">T</span><strong>{appName}</strong></div></header>
    <main className="required-setup-main"><section className="panel required-setup-panel" aria-label="Required app setup" aria-busy={setup.loading || setup.busy}>
      <h1>Let’s get you ready</h1>
      <p>Finish setup to get started. Your choices are saved on this PC.</p>
      {setup.view && setup.view.features.length > 1 && <p role="status">{setup.view.features.filter(feature => feature.completed).length} of {setup.view.features.length} steps complete</p>}
      {setup.loading ? <p role="status">Checking setup…</p> : <>
        {pending && <><h2>{pending.title}</h2><p>{pending.description}</p>{step}</>}
        {error && <p className="warning-text" role="alert">{error}</p>}
        {error && <Button className="secondary" disabled={setup.busy} onClick={() => void setup.reload()}>Retry setup</Button>}
      </>}
    </section>{safety}</main>
  </div>
}
