import type { PublicProfile, Run } from './contracts'

type CountRefreshEvidence = {
  playerCountRefreshing?: boolean
  playerCountFailureCount?: number
}

export function hasRepeatedPlayerCountFailure(value: CountRefreshEvidence): boolean {
  return Number.isSafeInteger(value.playerCountFailureCount) && (value.playerCountFailureCount ?? 0) >= 2
}

export function playerCountAvailability(run: Run, nowMs: number): {
  count: number | null; reason: string; state: 'Fresh' | 'Stale' | 'Unavailable' | 'DisplayOnly'
} {
  const observed = run.playerCountObservedUtc ? Date.parse(run.playerCountObservedUtc) : NaN
  if (run.playerObservationSource === 'CustomScriptReady') return {
    count: null, state: 'DisplayOnly', reason: 'This Custom count cannot allow Friend Stop or automatic shutdown until the owner tests the setup.'
  }
  if (run.playerObservationSource === 'ObservationStale' ||
      (Number.isFinite(observed) && Number.isFinite(nowMs) && nowMs - observed > 10_000)) return {
    count: null, state: 'Stale', reason: 'This count is out of date. Refresh to check again.'
  }
  if (run.state !== 'Ready' || !run.playerCountTrusted || run.onlinePlayers === null ||
      !Number.isSafeInteger(run.onlinePlayers) || run.onlinePlayers < 0 ||
      !Number.isFinite(observed) || !Number.isFinite(nowMs) || observed > nowMs ||
      run.playerObservationSource === 'ObservationPending') return {
    count: null, state: 'Unavailable', reason: 'The game has not supplied a current player count.'
  }
  return { count: run.onlinePlayers, state: 'Fresh', reason: 'Current count from the game.' }
}

export function playerCountPresentation(run: Run, nowMs = Date.now(), refreshing = false) {
  const availability = playerCountAvailability(run, nowMs)
  const missing = availability.count === null
  const inProgress = refreshing || run.playerCountRefreshing === true
  return {
    ...availability,
    refreshing: inProgress,
    checking: missing && !hasRepeatedPlayerCountFailure(run),
    showWarning: missing && hasRepeatedPlayerCountFailure(run)
  }
}

export function isTransientPlayerObservation(run: (CountRefreshEvidence & {
  state: string; playerObservationSource?: string | null
}) | undefined): boolean {
  return !!run && isPlayerCountObservationUnavailable(run) && !hasRepeatedPlayerCountFailure(run)
}

// A count lookup cannot establish readiness, but it also does not call for a
// global warning. Keep identity and actual server failures outside this list.
export function isPlayerCountObservationUnavailable(run: {
  state: string; playerObservationSource?: string | null
} | undefined): boolean {
  return !!run && run.state === 'Unknown' &&
    ['ObservationPending', 'ObservationStale', 'HealthProbeFailed', 'CustomStatusTimedOut']
      .includes(run.playerObservationSource ?? '')
}

export function publicPlayerCount(profile: PublicProfile, nowMs = Date.now()): number | null {
  if (profile.state !== 'Ready' || !Number.isSafeInteger(profile.onlinePlayers) ||
      profile.onlinePlayers === null || profile.onlinePlayers < 0) return null
  // Older Hosts do not send count metadata. Preserve their existing display and
  // Host-issued canStopNow guard; absence never fabricates repeated failures.
  if (profile.playerCountTrusted === undefined || profile.playerCountTrusted === null) return profile.onlinePlayers
  const observed = profile.playerCountObservedUtc ? Date.parse(profile.playerCountObservedUtc) : NaN
  return profile.playerCountTrusted && Number.isFinite(observed) && Number.isFinite(nowMs) &&
    nowMs >= observed && nowMs - observed <= 10_000 &&
    !['ObservationPending', 'ObservationStale'].includes(profile.playerObservationSource ?? '')
    ? profile.onlinePlayers : null
}
