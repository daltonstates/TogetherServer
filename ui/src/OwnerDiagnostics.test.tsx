import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { OwnerDiagnostics } from './OwnerDiagnostics'
import type { OwnerDiagnosticsView, SupportReportExport } from './contracts'

const diagnostics: OwnerDiagnosticsView = {
  generatedUtc: '2026-09-28T12:00:00Z',
  evidenceBoundary: 'These checks do not prove that a Friend connected, joined the game, or that a world saved correctly.',
  servers: [{
    profileId: '11111111-1111-4111-8111-111111111111', label: 'Weekend world', kind: 'Valheim',
    checks: [
      { id: 'configuration', label: 'Saved setup', state: 'Saved', detail: 'The existing driver accepts the saved configuration.',
        nextAction: 'Review Setup before changing it.', location: 'Host > Setup', tone: 'Neutral', observedUtc: null },
      { id: 'local-game-ports', label: 'Declared game ports', state: 'Open on PC',
        detail: 'Seeing a port open on this PC does not prove that a Friend can reach it or join the game.',
        nextAction: 'Verify the game separately from a real Friend PC.', location: 'Settings > Connection help', tone: 'Attention', observedUtc: null }
    ]
  }],
  sharedChecks: [{ id: 'update-state', label: 'Application update', state: 'Current', detail: 'TogetherServer is up to date.',
    nextAction: 'Check again when needed.', location: 'Settings > App', tone: 'Neutral', observedUtc: null }],
  truncated: false
}

const report: SupportReportExport = {
  fileName: 'TogetherServer-support-report.json',
  contentType: 'application/json; charset=utf-8',
  content: '{"reportSchemaVersion":1}\n',
  sizeBytes: 26
}

afterEach(() => vi.restoreAllMocks())

describe('OwnerDiagnostics', () => {
  it('shows honest local evidence with neutral, attention, and error styling only', async () => {
    const { container } = render(<OwnerDiagnostics selectedProfileId={diagnostics.servers[0].profileId}
      diagnosticsLoader={async () => diagnostics} supportReportLoader={async () => report} />)

    expect(await screen.findByText('Seeing a port open on this PC does not prove that a Friend can reach it or join the game.')).toBeInTheDocument()
    expect(screen.getByText(/do not prove that a Friend connected/)).toBeInTheDocument()
    expect(container.querySelector('.good')).not.toBeInTheDocument()
    expect(container.querySelector('.tone-neutral')).toBeInTheDocument()
    expect(container.querySelector('.tone-attention')).toBeInTheDocument()
  })

  it('copies and downloads only the fixed support-report payload', async () => {
    const writeText = vi.fn(async () => {})
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    const createObjectURL = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:support-report')
    const revokeObjectURL = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    const supportReportLoader = vi.fn(async () => report)
    render(<OwnerDiagnostics selectedProfileId={diagnostics.servers[0].profileId}
      diagnosticsLoader={async () => diagnostics} supportReportLoader={supportReportLoader} />)
    await screen.findByText('Saved setup')

    fireEvent.click(screen.getByRole('button', { name: 'Copy report' }))
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(report.content))
    expect(await screen.findByText(/Redacted support report copied/)).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Download JSON' }))
    await waitFor(() => expect(click).toHaveBeenCalledOnce())
    expect(createObjectURL).toHaveBeenCalledOnce()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:support-report')
    const anchor = click.mock.instances[0] as HTMLAnchorElement
    expect(anchor.download).toBe('TogetherServer-support-report.json')
    expect(supportReportLoader).toHaveBeenCalledTimes(2)
  })

  it('shows a bounded generic export error without reflecting private details', async () => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: vi.fn() } })
    render(<OwnerDiagnostics selectedProfileId={diagnostics.servers[0].profileId}
      diagnosticsLoader={async () => diagnostics}
      supportReportLoader={async () => { throw new Error('password=hunter2 C:\\Users\\Owner\\world') }} />)
    await screen.findByText('Saved setup')

    fireEvent.click(screen.getByRole('button', { name: 'Copy report' }))
    expect(await screen.findByText(/No private error details were added/)).toBeInTheDocument()
    expect(screen.queryByText(/hunter2/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Users\\Owner/)).not.toBeInTheDocument()
  })
})
