import type { PublicProfile, Snapshot } from './contracts'

export const successFeedbackMs = 8_000
export type ActionNotice = {
  good: boolean
  text: string
  receivedAtMs?: number
  server?: { profileId: string; action: string; signature: string }
}

function serverSignature(snapshot: Snapshot | null, profileId: string): { signature: string; state: string } | null {
  if (!snapshot) return null
  const server = snapshot.mode === 'Host' ? snapshot.runs.find(run => run.profileId === profileId)
    : snapshot.profiles.find(profile => profile.id === profileId)
  if (!server) return null
  return { state: server.state, signature: JSON.stringify([
    snapshot.mode, snapshot.mode === 'Friend' ? snapshot.connectionId : null,
    snapshot.mode === 'Friend' ? snapshot.state : null, profileId,
    server.runOperationId, server.state, server.onlinePlayers, server.autoShutdownAtUtc,
    'playerCountTrusted' in server ? server.playerCountTrusted : null,
    'playerObservationSource' in server ? server.playerObservationSource : null,
    snapshot.mode === 'Host' && 'addedShutdownMinutes' in server ? server.addedShutdownMinutes : null,
    snapshot.mode === 'Friend' && 'operation' in server ? server.operation?.id : null
  ]) }
}

export function serverActionNotice(good: boolean, text: string, snapshot: Snapshot, profileId: string, action: string): ActionNotice {
  const current = serverSignature(snapshot, profileId)
  return { good, text, receivedAtMs: Date.now(), ...(current ? { server: { profileId, action, signature: current.signature } } : {}) }
}

// A receipt is temporary feedback, never the current server-status authority.
export function currentActionNotice(notice: ActionNotice | null | undefined, snapshot: Snapshot | null, nowMs: number): ActionNotice | null {
  if (!notice) return null
  if (!notice.good) return notice
  if (notice.receivedAtMs !== undefined) {
    const age = nowMs - notice.receivedAtMs
    if (!Number.isFinite(age) || age < 0 || age >= successFeedbackMs) return null
  }
  if (notice.server) {
    const current = serverSignature(snapshot, notice.server.profileId)
    if (!current || current.signature !== notice.server.signature) return null
    if (['start', 'restart', 'safe-restart', 'replace', 'resume-hosting'].includes(notice.server.action)
      && current.state === 'Ready') return null
    const operation = snapshot?.mode === 'Friend'
      ? snapshot.profiles.find(profile => profile.id === notice.server!.profileId)?.operation : null
    if (operation?.action === notice.server.action) return null // The canonical operation owns its progress/result.
  }
  return notice
}

export function currentRemoteOperation(profile: PublicProfile, nowMs: number): boolean {
  const operation = profile.operation
  if (!operation) return false
  if (operation.state !== 'Succeeded') return true
  const completed = operation.completedUtc ? Date.parse(operation.completedUtc) : NaN
  const age = nowMs - completed
  if (!Number.isFinite(age) || age < 0 || age >= successFeedbackMs) return false
  if (['start', 'restart', 'replace'].includes(operation.action)
    && !['Starting', 'Process running', 'Listening'].includes(profile.state)) return false
  if (operation.action === 'stop' && profile.state !== 'Offline') return false
  if (operation.action === 'extend' && profile.state !== 'Ready') return false
  return true
}
