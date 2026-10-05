import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import fixture from '../../contracts/host-snapshot.v1.json'
import { AllServerOverview, allServerRows } from './AllServerOverview'
import { parseSnapshot, type HostSnapshot } from './contracts'

const now = Date.parse('2026-09-29T12:00:04Z')
function snapshot(): HostSnapshot {
  const result = parseSnapshot(structuredClone(fixture))
  if (result.mode !== 'Host') throw new Error('Host fixture required')
  result.runs[0].playerCountObservedUtc = '2026-09-29T12:00:00Z'
  result.runs[0].playerCountTrusted = true
  result.runs[0].onlinePlayers = 0
  result.runs[0].autoShutdownAtUtc = '2026-09-29T12:15:00Z'
  result.backups = { [result.settings.profiles[0].id]: { profileId: result.settings.profiles[0].id,
    lastSuccessfulUtc: '2026-09-29T11:00:00Z', lastFailureUtc: null, lastFailure: null,
    completedCount: 1, retainedSizeBytes: 10, availableSpaceBytes: null } }
  result.recovery = null; result.activity = []; result.crashRecovery = {}
  return result
}
describe('all-server overview', () => {
  it('handles loading, zero servers, error and partial backup/count data', () => {
    const open = vi.fn()
    const { rerender } = render(<AllServerOverview snapshot={null} selectedProfileId="" nowMs={now} onOpen={open} />)
    expect(screen.getByRole('status')).toHaveTextContent('Loading saved servers')
    const data = snapshot(); data.settings.profiles = []; data.runs = []
    rerender(<AllServerOverview snapshot={data} selectedProfileId="" nowMs={now} onOpen={open} />)
    expect(screen.getByText(/No saved servers yet/)).toBeInTheDocument()
    data.settings.profiles = snapshot().settings.profiles; data.backups = undefined
    rerender(<AllServerOverview snapshot={data} selectedProfileId="" nowMs={now} error="private error detail" onOpen={open} />)
    expect(screen.getByRole('alert')).toHaveTextContent('last available snapshot')
    expect(screen.getByText('Unknown', { selector: 'strong' })).toBeInTheDocument()
    expect(screen.getByText('Status unavailable')).toBeInTheDocument()
    expect(screen.queryByText('private error detail')).not.toBeInTheDocument()
  })
  it('displays zero as a fresh reported count and its canonical deadline', () => {
    const rows = allServerRows(snapshot(), now)
    expect(rows[0]).toMatchObject({ players: '0 online', freshness: 'Observed just now',
      deadline: '2026-09-29T12:15:00Z', backup: 'Completed 1 hr ago' })
    const stale = allServerRows(snapshot(), now + 11_000)[0]
    expect(stale.players).toBe('Unknown')
    expect(stale.deadline).toBeNull()
    expect(stale.warning).toBe('Player observation is stale')
  })
  it('distinguishes unavailable count, missing backup and failed latest attempt', () => {
    const data = snapshot(); const id = data.settings.profiles[0].id
    data.runs[0].onlinePlayers = null
    expect(allServerRows(data, now)[0].warning).toBe('Player count unavailable')
    data.runs[0].onlinePlayers = 0
    data.backups![id].completedCount = 0; data.backups![id].lastSuccessfulUtc = null
    expect(allServerRows(data, now)[0]).toMatchObject({ warning: 'No completed backup', backup: 'No completed backup' })
    data.backups![id].lastFailureUtc = '2026-09-29T12:00:00Z'
    expect(allServerRows(data, now)[0].warning).toBe('Latest backup attempt failed')
  })
  it('sorts attention first, retains selection and navigates only to reviewed actions', () => {
    const data = snapshot(); const selected = data.settings.profiles[0].id
    data.settings.profiles.push({ ...data.settings.profiles[0], id: 'server-2', name: 'Needs review' })
    data.runs.push({ ...data.runs[0], profileId: 'server-2', state: 'Unknown', detail: 'C:\\private\\world' })
    const open = vi.fn()
    const { container, rerender } = render(<AllServerOverview snapshot={data} selectedProfileId={selected} nowMs={now} onOpen={open} />)
    expect(allServerRows(data, now).map(row => row.id)).toEqual(['server-2', selected])
    expect(container.querySelector('.selected')).toHaveTextContent('Contract fixture')
    fireEvent.click(screen.getByRole('button', { name: 'Review server: Needs review' }))
    expect(open).toHaveBeenCalledWith({ workspace: 'host', section: 'overview', profileId: 'server-2', label: 'Review server' })
    data.runs[1].state = 'Ready'; data.backups!['server-2'] = { ...data.backups![selected], profileId: 'server-2' }
    rerender(<AllServerOverview snapshot={data} selectedProfileId={selected} nowMs={now} onOpen={open} />)
    expect(container.querySelector('.selected')).toHaveTextContent('Contract fixture')
    expect(container).not.toHaveTextContent('C:\\private\\world')
    expect(container).not.toHaveTextContent(data.settings.profiles[0].executablePath)
  })
  it('excludes device-only and unreviewed Attention text from the summary', () => {
    const data = snapshot(); const id = data.settings.profiles[0].id
    data.activity = [{ id: 'event', category: 'Network', action: 'RouteFailure', severity: 'Warning', profileId: id,
      deviceId: 'private-device', visibility: 'Device', message: 'secret path and Friend identity', occurredUtc: '2026-09-29T12:00:00Z' }]
    expect(allServerRows(data, now)[0].warning).toBe('')
    data.activity[0].visibility = 'LocalOwner'
    expect(allServerRows(data, now)[0]).toMatchObject({ warning: 'Connection check needs review',
      destination: { workspace: 'settings', section: 'network', profileId: id } })
    expect(JSON.stringify(allServerRows(data, now))).not.toContain('secret path')
  })
})
