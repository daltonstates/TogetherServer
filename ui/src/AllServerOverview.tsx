import { Button } from './Controls'
import type { HostSnapshot } from './contracts'
import { activityDestination, type ActivityDestination } from './notificationState'

type OverviewRow = {
  id: string; name: string; state: string; players: string; source: string; freshness: string
  deadline: string | null; backup: string; warning: string; priority: number; destination: ActivityDestination
}
const sourceLabels: Record<string, string> = {
  FixtureReady: 'Fixture count', ValheimLogReady: 'Valheim query or owned log', ValheimReady: 'Valheim query',
  MinecraftLocalStatus: 'Minecraft status query', FactorioReady: 'Factorio RCON count',
  CustomCertifiedReady: 'Owner-certified Custom count', CustomScriptReady: 'Custom display only',
  ObservationStale: 'Stale observation', ObservationPending: 'Observation pending'
}
function age(value: string | null | undefined, nowMs: number) {
  const timestamp = value ? Date.parse(value) : NaN
  return Number.isFinite(timestamp) && nowMs >= timestamp ? nowMs - timestamp : null
}
function elapsed(milliseconds: number) {
  if (milliseconds < 60_000) return 'just now'
  if (milliseconds < 3_600_000) return `${Math.floor(milliseconds / 60_000)} min ago`
  if (milliseconds < 86_400_000) return `${Math.floor(milliseconds / 3_600_000)} hr ago`
  return `${Math.floor(milliseconds / 86_400_000)} days ago`
}

export function allServerRows(snapshot: HostSnapshot, nowMs: number): OverviewRow[] {
  return snapshot.settings.profiles.map((profile, index) => {
    const run = snapshot.runs.find(item => item.profileId === profile.id)
    const observedAge = age(run?.playerCountObservedUtc, nowMs)
    const trusted = run?.state === 'Ready' && run.playerCountTrusted && run.onlinePlayers !== null &&
      observedAge !== null && observedAge <= 10_000
    const stale = observedAge !== null && observedAge > 10_000
    const backup = snapshot.backups?.[profile.id]
    const backupAge = age(backup?.lastSuccessfulUtc, nowMs)
    const backupFailed = !!backup?.lastFailureUtc &&
      (!backup.lastSuccessfulUtc || Date.parse(backup.lastFailureUtc) >= Date.parse(backup.lastSuccessfulUtc))
    const state = run?.state ?? 'Unknown'
    // The Host projects an expired player observation as Unknown. An Unknown
    // run without that observation still needs the lifecycle review action.
    const stalePlayerObservation = state === 'Unknown' && stale
    let warning = ''
    let priority = 0
    let destination: ActivityDestination = { workspace: 'host', section: 'overview', profileId: profile.id, label: 'Open server' }
    const problem = (text: string, rank: number, section: 'overview' | 'players' | 'backups', label: string) => {
      warning = text; priority = rank; destination = { workspace: 'host', section, profileId: profile.id, label }
    }
    if (snapshot.recovery?.lifecycleBlocked) {
      warning = 'Local data needs review'; priority = 100
      destination = { workspace: 'settings', section: 'diagnostics', profileId: profile.id, label: 'Review recovery' }
    } else if (state === 'Failed' || (state === 'Unknown' && !stalePlayerObservation)) problem('Server state needs review', 90, 'overview', 'Review server')
    else if (snapshot.crashRecovery?.[profile.id]?.state === 'Suspended') problem('Crash recovery is suspended', 85, 'overview', 'Review recovery')
    else if (profile.maintenance?.enabled) {
      warning = 'Maintenance is active'; priority = 70
      destination = { workspace: 'host', section: 'setup', profileId: profile.id, label: 'Continue maintenance' }
    }
    else if (backupFailed) problem('Latest backup attempt failed', 60, 'backups', 'Review backups')
    else if ((state === 'Ready' || stalePlayerObservation) && !trusted) problem(stale ? 'Player observation is stale' : 'Player count unavailable', 50, 'players', 'Review players')
    else if (profile.kind !== 'Custom' && !backup) problem('Backup status unavailable', 35, 'backups', 'Review backups')
    else if (profile.kind !== 'Custom' && backup?.completedCount === 0) problem('No completed backup', 30, 'backups', 'Protect world')
    else {
      const recent = snapshot.activity?.filter(item => item.profileId === profile.id && item.severity === 'Warning' &&
        item.visibility !== 'Device' && age(item.occurredUtc, nowMs) !== null && age(item.occurredUtc, nowMs)! < 300_000 &&
        ['Network', 'Connections', 'Recovery', 'Backup'].includes(item.category))
        .sort((a, b) => Date.parse(b.occurredUtc) - Date.parse(a.occurredUtc))[0]
      const routed = recent ? activityDestination(recent) : null
      if (routed) {
        warning = recent!.category === 'Backup' ? 'World protection needs review' : recent!.category === 'Recovery' ?
          'Recovery needs review' : 'Connection check needs review'
        priority = 20; destination = routed
      }
    }
    return {
      id: profile.id, name: profile.name, state, index, priority, destination, warning,
      players: trusted ? `${run!.onlinePlayers} online` : 'Unknown',
      source: sourceLabels[run?.playerObservationSource ?? ''] ?? (trusted ? 'Game server report' : 'No trusted source'),
      freshness: observedAge === null ? 'Observation unavailable' : stale ? `Stale · ${elapsed(observedAge)}` : `Observed ${elapsed(observedAge)}`,
      deadline: trusted && run!.onlinePlayers === 0 && run!.autoShutdownAtUtc ? run!.autoShutdownAtUtc : null,
      backup: profile.kind === 'Custom' ? 'Backup unsupported' : !backup ? 'Status unavailable' :
        backup.completedCount === 0 ? 'No completed backup' : backupAge === null ? 'Completed · age unavailable' : `Completed ${elapsed(backupAge)}`
    }
  }).sort((a, b) => b.priority - a.priority || a.index - b.index)
}

export function AllServerOverview({ snapshot, selectedProfileId, nowMs, error = '', onOpen }: {
  snapshot: HostSnapshot | null; selectedProfileId: string; nowMs: number; error?: string
  onOpen: (destination: ActivityDestination) => void
}) {
  const rows = snapshot ? allServerRows(snapshot, nowMs) : []
  return <section className="all-server-overview" aria-label="All servers">
    <div className="all-server-heading"><h2>All servers</h2><span>{rows.filter(row => row.priority > 0).length} need attention</span></div>
    {error && <p role="alert">Overview could not refresh. Showing the last available snapshot; stale counts are Unknown.</p>}
    {!snapshot ? <p role="status">Loading saved servers…</p> : rows.length === 0 ? <p>No saved servers yet. Add a server to begin.</p> :
      <div className="all-server-rows">{rows.map(row => <article key={row.id} className={row.id === selectedProfileId ? 'all-server-row selected' : 'all-server-row'}>
        <div className="all-server-identity"><strong>{row.name}</strong><span>{row.state}</span>{row.warning && <small>{row.warning}</small>}</div>
        <div><small>Players</small><strong>{row.players}</strong><span>{row.source}</span><small>{row.freshness}</small></div>
        <div><small>Shutdown deadline</small>{row.deadline ? <time dateTime={row.deadline}>{new Date(row.deadline).toLocaleString()}</time> : <span>No current deadline</span>}</div>
        <div><small>Latest completed backup</small><span>{row.backup}</span></div>
        <Button className="secondary" onClick={() => onOpen(row.destination)} aria-label={`${row.destination.label}: ${row.name}`}>{row.destination.label}</Button>
      </article>)}</div>}
    <small className="evidence-boundary">Counts and backup history are observations. Server actions use their existing Host safety checks.</small>
  </section>
}
