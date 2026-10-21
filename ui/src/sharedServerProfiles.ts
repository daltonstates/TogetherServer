import type { HostSnapshot, Run } from './contracts'

type SharedProfile = { id: string; separateCopySourceProfileId?: string | null }

/** Only an explicit source ID that names a local original establishes ownership. */
export function originalLocalServer<T extends SharedProfile>(profile: T, profiles: readonly T[]): T | null {
  if (!profile.separateCopySourceProfileId) return profile
  return profiles.find(source => source.id === profile.separateCopySourceProfileId && !source.separateCopySourceProfileId) ?? null
}

function running(run: Run | undefined): run is Run {
  return !!run && (['Starting', 'Process running', 'Listening', 'Ready'].includes(run.state) ||
    run.state === 'Unknown' && (run.processId !== null || run.playerObservationSource === 'ObservationPending' && !!run.runOperationId))
}

/** Select the actual run returned by Start, without treating a retained Unknown copy as the started server. */
export function startedSharedServer(next: HostSnapshot, previous: HostSnapshot | null, requestedProfileId: string) {
  const requested = next.settings.profiles.find(profile => profile.id === requestedProfileId) ??
    previous?.settings.profiles.find(profile => profile.id === requestedProfileId)
  const rootId = requested?.separateCopySourceProfileId ?? requestedProfileId
  const candidates = next.settings.profiles.filter(profile => profile.id === rootId || profile.separateCopySourceProfileId === rootId)
    .filter(profile => running(next.runs.find(run => run.profileId === profile.id)))
  const started = candidates.filter(profile => {
    const current = next.runs.find(run => run.profileId === profile.id)!
    const prior = previous?.runs.find(run => run.profileId === profile.id)
    return !running(prior) || !!current.runOperationId && current.runOperationId !== prior.runOperationId ||
      current.processId !== null && current.processId !== prior.processId
  })
  return started.length === 1 ? started[0] : candidates.length === 1 ? candidates[0] : undefined
}
