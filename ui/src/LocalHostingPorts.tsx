import { useState } from 'react'
import { Input, Select } from './Controls'
import { approvedGamePortLabel, localHostingPortChoice, type LocalHostingPortChoice } from './ApprovedGamePorts'
import type { LocalHostingStatus } from './FriendLocalHosting'

type PortDraft = { scope: string; source: string; game: string | null; profileId: string | null; choice: string; gamePort: string; rconPort: string; approved: boolean }
export function useLocalHostingPorts(status: LocalHostingStatus | null, scope: string) {
  const [draft, setDraft] = useState<PortDraft | null>(null)
  const plans = status?.approvedPorts ?? []
  const known = status?.needsPortApproval !== undefined && status.approvedPorts !== undefined && status.selectedApprovedPortId !== undefined
  const handoff = status?.handoffRequest
  const planned = !!handoff?.requestId && ['Requested', 'Accepted', 'Prepared', 'Completed'].includes(handoff.recipientState ?? '')
  const plannedPrepared = planned && handoff?.recipientState === 'Prepared'
  const canChangeExisting = status?.game === 'Valheim' && !!status.branchHash && !planned
  // A prepared move binds its ports before its destination profile exists.
  const preferred = canChangeExisting || !status?.localProfileId && !planned ? plans.at(-1) : null
  const source = JSON.stringify([status?.gamePort, status?.factorioRconPort, status?.selectedApprovedPortId, plans,
    planned ? [handoff?.requestId, plannedPrepared, plannedPrepared ? handoff?.preparedVersionHash : null] : null])
  const initial: PortDraft = { scope, source, game: status?.game ?? null, profileId: status?.localProfileId ?? null,
    choice: preferred?.id ?? status?.selectedApprovedPortId ?? 'new', gamePort: String(status?.gamePort ?? ''),
    rconPort: status?.factorioRconPort == null ? '' : String(status.factorioRconPort), approved: false }
  const value = draft?.scope === scope && draft.source === source && draft.game === initial.game && draft.profileId === initial.profileId ? draft : initial
  const selection: LocalHostingPortChoice | null = value.game ? localHostingPortChoice({ game: value.game, choice: value.choice,
    plans, gamePort: value.gamePort, rconPort: value.rconPort, approved: value.approved }) : null
  const selected = value.choice === 'new' ? selection && 'gamePort' in selection ? selection : null : plans.find(plan => plan.id === value.choice)
  const changed = !!status?.localProfileId && !!selected && (selected.gamePort !== status.gamePort ||
    selected.factorioRconPort !== (status.factorioRconPort ?? null))
  return { value, known, selection, ready: !known || selection !== null, plans, canChangeExisting, plannedPrepared, changed, savedPort: status?.gamePort,
    change: (patch: Partial<Pick<PortDraft, 'choice' | 'gamePort' | 'rconPort' | 'approved'>>) => setDraft({ ...value, ...patch }) }
}

export function LocalHostingPorts({ ports, existing, busy, onChange }: {
  ports: ReturnType<typeof useLocalHostingPorts>; existing: boolean; busy: boolean; onChange: () => void
}) {
  if (!ports.known || !ports.value.game) return null
  const fixed = existing && !ports.canChangeExisting || ports.plannedPrepared
  const change = (patch: Parameters<typeof ports.change>[0]) => { onChange(); ports.change(patch) }
  return <section className="local-hosting-fix" aria-label="Game ports on this PC"><h3>Game ports on this PC</h3>
    <p>Choose ports you have approved on this PC, or approve a new choice below. The other Host’s game port is a suggestion.</p>
    <label>Approved game ports<Select aria-label="Approved local game ports" value={ports.value.choice} disabled={busy || fixed}
      onChange={event => change({ choice: event.target.value, approved: false })}>
      <option value="new">Approve a game port choice</option>
      {ports.plans.map(plan => <option key={plan.id} value={plan.id}>{approvedGamePortLabel(plan)}</option>)}
    </Select></label>
    {ports.value.choice === 'new' && <>
      <label>Game port<Input type="number" aria-label="Local hosting game port" min={1024} max={ports.value.game === 'Valheim' ? 65534 : 65535}
        value={ports.value.gamePort} disabled={busy || fixed} onChange={event => change({ gamePort: event.target.value, approved: false })} /></label>
      {ports.value.game === 'Valheim' && <p className="helper-text">Valheim also uses the next UDP port. Both ports must be available.</p>}
      {ports.value.game === 'Factorio' && <label>Private RCON port<Input type="number" aria-label="Local hosting private RCON port" min={1024} max={65535}
        value={ports.value.rconPort} disabled={busy || fixed} onChange={event => change({ rconPort: event.target.value, approved: false })} /></label>}
      {ports.value.game === 'Factorio' && <p className="helper-text">Use a separate private RCON port. Do not forward it.</p>}
      <label className="local-hosting-consent"><Input type="checkbox" checked={ports.value.approved} disabled={busy}
        onChange={event => change({ approved: event.target.checked })} /><span>Approve these game ports for reuse on this PC.</span></label>
    </>}
    <p className="helper-text">Manage reusable choices in App Settings → Game ports. Approval does not change Windows or router settings.</p>
    {ports.changed && <p className="helper-text">This server currently uses port {ports.savedPort}. Your selected ports will be checked and saved before hosting.</p>}
    {ports.plannedPrepared ? <p className="helper-text">This hosting move keeps the ports already prepared for it.</p> :
      fixed && <p className="helper-text">This prepared server keeps its saved ports. Open its local Setup to change them.</p>}
  </section>
}
