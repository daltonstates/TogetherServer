import { useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { ContractError, type Decoder } from './contracts'
import type { GuideKind } from './GuidedSetup'

export type GettingStartedView = { offered: boolean; intent: 'Choose' | 'Host' | 'Join'; dismissed: boolean }
export const parseGettingStarted: Decoder<GettingStartedView> = value => {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new ContractError('Invalid getting started state.')
  const item = value as Record<string, unknown>
  if (typeof item.offered !== 'boolean' || typeof item.dismissed !== 'boolean' || !['Choose', 'Host', 'Join'].includes(item.intent as string))
    throw new ContractError('Invalid getting started choice.')
  return { offered: item.offered, dismissed: item.dismissed, intent: item.intent as GettingStartedView['intent'] }
}

type Progress = { kind: GuideKind; step: number }
const progressKey = 'togetherserver.guide-progress.v1'
function savedProgress(): Progress | null {
  try {
    const value = JSON.parse(window.localStorage.getItem(progressKey) ?? 'null') as unknown
    if (!value || typeof value !== 'object') return null
    const item = value as Record<string, unknown>
    return (item.kind === 'host' || item.kind === 'join') && Number.isSafeInteger(item.step) && (item.step as number) >= 0 && (item.step as number) < (item.kind === 'host' ? 5 : 3)
      ? { kind: item.kind, step: item.step as number } : null
  } catch { return null }
}

export function useGettingStarted(enabled: boolean) {
  const [view, setView] = useState<GettingStartedView | null>(null)
  const [welcome, setWelcome] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [active, setActive] = useState<GuideKind | null>(null)
  const [progress, setProgress] = useState<Progress | null>(savedProgress)
  const inFlight = useRef(false)
  const preferenceEpoch = useRef(0)
  useEffect(() => {
    if (!enabled) return
    const controller = new AbortController()
    const epoch = preferenceEpoch.current
    void getLocalJson('/api/local/getting-started', parseGettingStarted, controller.signal).then(result => {
      if (controller.signal.aborted || epoch !== preferenceEpoch.current) return
      setView(result)
      if (result.offered && !result.dismissed) setWelcome(true)
    }).catch(() => { /* Optional help remains available from Guides if its preference cannot load. */ })
    return () => controller.abort()
  }, [enabled])
  const remember = (next: Progress | null) => {
    setProgress(next)
    try {
      if (next) window.localStorage.setItem(progressKey, JSON.stringify(next))
      else window.localStorage.removeItem(progressKey)
    } catch { /* Walkthroughs still work without optional progress storage. */ }
  }
  const save = async (kind: GuideKind | null) => {
    if (inFlight.current) return false
    preferenceEpoch.current += 1
    inFlight.current = true; setBusy(true); setError('')
    try {
      const result = await changeJson('/api/local/getting-started', 'PUT', parseGettingStarted,
        { intent: kind === 'host' ? 'Host' : kind === 'join' ? 'Join' : view?.intent ?? 'Choose', dismissed: true })
      setView(result)
      return true
    } catch (failure) { setError(errorMessage(failure)); return false }
    finally { inFlight.current = false; setBusy(false) }
  }
  return { view, welcome, busy, error, active, step: progress?.kind === active ? progress.step : 0,
    resumeStep: (kind: GuideKind) => progress?.kind === kind ? progress.step : 0,
    intent: view?.intent === 'Host' ? 'host' as const : view?.intent === 'Join' ? 'join' as const : null,
    open: () => { preferenceEpoch.current += 1; setActive(null); setError(''); setWelcome(true) },
    dismiss: async () => { setWelcome(false); await save(null) },
    start: async (kind: GuideKind) => {
      if (!await save(kind)) return false
      setWelcome(false); setActive(kind)
      if (progress?.kind !== kind) remember({ kind, step: 0 })
      return true
    },
    stepTo: (step: number) => { if (active) remember({ kind: active, step: Math.max(0, Math.min(active === 'host' ? 4 : 2, step)) }) },
    pause: () => setActive(null),
    finish: () => { remember(null); setActive(null) }
  }
}
