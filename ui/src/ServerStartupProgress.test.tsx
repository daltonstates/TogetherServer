import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ServerStartupProgress } from './ServerStartupProgress'

const startupRun = '22222222-2222-4222-8222-222222222222'
const logPage = (runId: string, message: string) => ({ ok: true, code: 'LogAvailable', message: 'Recent console lines.',
  sourceState: 'Active', runId: runId.replaceAll('-', ''), cursor: 'current-cursor', hasMore: false,
  records: [{ timestampUtc: null, severity: 'Info', category: 'Server', stream: 'Server', message }] })

describe('server startup progress', () => {
  it('keeps launch indeterminate, then shows the game check until canonical readiness arrives', () => {
    const view = render(<ServerStartupProgress state="Offline" requestPending detail="No managed process" />)
    const launch = screen.getByRole('progressbar', { name: 'Checking setup and starting' })
    expect(launch).not.toHaveAttribute('aria-valuenow')
    expect(launch).toHaveAccessibleDescription(/Checking the saved setup and available ports/)
    expect(screen.queryByText('No managed process')).not.toBeInTheDocument()
    expect(screen.getByText('Launch server').closest('li')).toHaveAttribute('aria-current', 'step')

    view.rerender(<ServerStartupProgress state="Unknown" playerObservationSource="ObservationPending"
      runOperationId="new-run" detail="Waiting for the next server check." />)
    expect(screen.getByRole('progressbar', { name: 'Waiting for the game' })).not.toHaveAttribute('aria-valuenow')
    expect(screen.getByText('Check game').closest('li')).toHaveAttribute('aria-current', 'step')
    expect(screen.getByText('Ready').closest('li')).not.toHaveAttribute('aria-current')
    expect(screen.getByRole('status')).toHaveTextContent('Waiting for the next server check.')

    view.rerender(<ServerStartupProgress state="Starting" detail="Waiting for a local game status reply." />)
    expect(screen.getByRole('progressbar')).toHaveAccessibleDescription('Waiting for a local game status reply.')
    view.rerender(<ServerStartupProgress state="Ready" />)
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  it.each(['Ready', 'Listening', 'Stopping', 'Failed', 'Unknown', 'World copy in progress'])(
    'does not let an unfinished Start request obscure %s', state => {
      render(<ServerStartupProgress state={state} requestPending />)
      expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    })

  it.each(['ObservationStale', 'HealthProbeFailed', null])('does not relabel %s uncertainty as startup', source => {
    render(<ServerStartupProgress state="Unknown" playerObservationSource={source} runOperationId="existing-run" />)
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('requires an identified run before treating the first pending observation as launched', () => {
    render(<ServerStartupProgress state="Unknown" playerObservationSource="ObservationPending" />)
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('opens logs only on an explicit click without moving focus when details update', () => {
    const openLogs = vi.fn()
    const view = render(<ServerStartupProgress state="Starting" detail="Checking the game." onOpenLogs={openLogs} />)
    const logs = screen.getByRole('button', { name: 'Open full logs' })
    logs.focus()
    view.rerender(<ServerStartupProgress state="Starting" detail="Still waiting for a game reply." onOpenLogs={openLogs} />)
    expect(logs).toHaveFocus()
    expect(openLogs).not.toHaveBeenCalled()
    expect(within(screen.getByRole('region', { name: 'Server startup' })).getByRole('status')).toHaveTextContent('Still waiting for a game reply.')
    fireEvent.click(logs)
    expect(openLogs).toHaveBeenCalledOnce()
  })

  it('starts the live console only for the launched run and removes it at Ready', async () => {
    const fetcher = vi.fn(async () => new Response(JSON.stringify(logPage(startupRun, 'Loading the world…'))))
    vi.stubGlobal('fetch', fetcher)
    const endpoint = '/api/local/profiles/server/logs'
    const view = render(<ServerStartupProgress state="Offline" requestPending logEndpoint={endpoint} runOperationId={startupRun} />)
    expect(screen.getByText('Console output will appear once the server app starts.')).toBeInTheDocument()
    expect(fetcher).not.toHaveBeenCalled()
    view.rerender(<ServerStartupProgress state="Starting" logEndpoint={endpoint} runOperationId={startupRun} />)
    expect(await screen.findByText('Loading the world…')).toBeVisible()
    expect(fetcher).toHaveBeenCalledWith(`${endpoint}?limit=60`, expect.objectContaining({ headers: { 'X-TogetherServer-Local': '1' } }))
    expect(screen.getByRole('log', { name: 'Startup console output' })).toHaveAttribute('aria-live', 'off')
    expect(screen.getByRole('button', { name: 'Pause' })).toBeEnabled()
    expect(screen.queryByRole('button', { name: 'Filters' })).not.toBeInTheDocument()
    view.rerender(<ServerStartupProgress state="Ready" logEndpoint={endpoint} runOperationId={startupRun} />)
    expect(screen.queryByRole('log')).not.toBeInTheDocument()
    expect(fetcher).toHaveBeenCalledTimes(1)
  })

  it('aborts an old run read and ignores its late result when a new startup uses the same endpoint', async () => {
    let finishOld!: (response: Response) => void
    let oldSignal!: AbortSignal
    const nextRun = '33333333-3333-4333-8333-333333333333'
    const fetcher = vi.fn((_path: string, init: RequestInit) => {
      if (!finishOld) {
        oldSignal = init.signal as AbortSignal
        return new Promise<Response>(resolve => { finishOld = resolve })
      }
      return Promise.resolve(new Response(JSON.stringify(logPage(nextRun, 'Current server start'))))
    })
    vi.stubGlobal('fetch', fetcher)
    const view = render(<ServerStartupProgress state="Starting" logEndpoint="/logs" runOperationId={startupRun} />)
    await waitFor(() => expect(fetcher).toHaveBeenCalledOnce())
    view.rerender(<ServerStartupProgress state="Starting" logEndpoint="/logs" runOperationId={nextRun} />)
    expect(await screen.findByText('Current server start')).toBeVisible()
    expect(oldSignal.aborted).toBe(true)
    await act(async () => { finishOld(new Response(JSON.stringify(logPage(startupRun, 'Previous server start')))) })
    expect(screen.queryByText('Previous server start')).not.toBeInTheDocument()
    expect(screen.getByText('Current server start')).toBeInTheDocument()
  })

  it('does not start a duplicate log reader while the full Logs tab is open', () => {
    const fetcher = vi.fn()
    vi.stubGlobal('fetch', fetcher)
    render(<ServerStartupProgress state="Starting" logEndpoint="/logs" runOperationId={startupRun} showConsole={false} />)
    expect(screen.getByRole('progressbar')).toBeInTheDocument()
    expect(screen.queryByRole('log')).not.toBeInTheDocument()
    expect(fetcher).not.toHaveBeenCalled()
  })
})
