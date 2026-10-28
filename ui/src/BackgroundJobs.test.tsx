import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { BackgroundJobs, parseBackgroundJobs, type BackgroundJob } from './BackgroundJobs'

const stamp = '2026-10-28T12:00:00Z'
const job: BackgroundJob = { id: '11111111-1111-4111-8111-111111111111', profileId: '22222222-2222-4222-8222-222222222222',
  serverName: 'Weekend server', worldName: 'Weekend world', operation: 'ReceiveSave', phase: 'Receiving', state: 'Running',
  receivedBytes: 128, totalBytes: 1024, startedUtc: stamp, updatedUtc: stamp, completedUtc: null, resultCode: null,
  message: 'Receiving the completed save.', attempts: 1 }
const response = (value: unknown) => new Response(JSON.stringify(value))
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

it('keeps one backend job and measured progress while the menu closes and workspace navigation changes', async () => {
  vi.useFakeTimers()
  let receivedBytes = 128
  const fetcher = vi.fn(async () => response({ jobs: [{ ...job, receivedBytes }], activeCount: 1 }))
  vi.stubGlobal('fetch', fetcher)
  const Workspace = ({ page }: { page: string }) => <><BackgroundJobs /><main>{page}</main></>
  const view = render(<Workspace page="Server one" />)
  await act(async () => {})
  const summary = screen.getByText('Background jobs', { selector: 'summary span' })
  fireEvent.click(summary)
  expect(screen.getByRole('progressbar')).toHaveAttribute('value', '128')
  fireEvent.click(summary)
  view.rerender(<Workspace page="Server two" />)
  receivedBytes = 640
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  fireEvent.click(summary)
  expect(screen.getByRole('progressbar')).toHaveAttribute('value', '640')
  expect(screen.getByText(/62% · 640 B of 1 KiB/)).toBeInTheDocument()
  expect(screen.getAllByText('Weekend server')).toHaveLength(1)
  expect(fetcher).toHaveBeenCalledWith('/api/local/background-jobs', expect.objectContaining({
    cache: 'no-store', headers: { 'X-TogetherServer-Local': '1' }
  }))
  expect(screen.getByText('Server two')).toBeInTheDocument()
})

it('uses indeterminate progress for unknown totals and retains interruption/failure details', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => response({ jobs: [{ ...job, totalBytes: null, state: 'Stalled', attempts: 3,
    resultCode: 'NoProgress', message: 'No new bytes were observed. Waiting for an approved source.' }], activeCount: 1 })))
  render(<BackgroundJobs />)
  await act(async () => {})
  fireEvent.click(screen.getByText('Background jobs', { selector: 'summary span' }))
  expect(screen.getByRole('progressbar')).not.toHaveAttribute('value')
  expect(screen.getByText('128 B received · total unknown')).toBeInTheDocument()
  expect(screen.getByText(/3 attempts/)).toBeInTheDocument()
  expect(screen.getByText('Result: NoProgress')).toBeInTheDocument()
})

it('closes review with Escape and restores the jobs trigger without stopping observation', async () => {
  vi.useFakeTimers()
  const fetcher = vi.fn(async () => response({ jobs: [job], activeCount: 1 }))
  vi.stubGlobal('fetch', fetcher)
  render(<BackgroundJobs />)
  await act(async () => {})
  const summary = screen.getByText('Background jobs', { selector: 'summary span' }).closest('summary')!
  fireEvent.click(summary)
  expect(summary.parentElement).toHaveAttribute('open')
  fireEvent.keyDown(screen.getByRole('region', { name: 'Background jobs' }), { key: 'Escape' })
  expect(summary.parentElement).not.toHaveAttribute('open')
  expect(summary).toHaveFocus()
  await act(async () => { await vi.advanceTimersByTimeAsync(1000) })
  expect(fetcher).toHaveBeenCalledTimes(2)
})

it.each([{ ...job, receivedBytes: 2048 }, { ...job, totalBytes: -1 }, { ...job, attempts: 0 },
  { ...job, state: 'Ready' }, { ...job, id: '../path' }])('rejects invalid backend progress %j', value => {
  expect(() => parseBackgroundJobs({ jobs: [value], activeCount: 1 })).toThrow()
})

it('rejects duplicate jobs and contradictory active counts', () => {
  expect(() => parseBackgroundJobs({ jobs: [job, job], activeCount: 2 })).toThrow()
  expect(() => parseBackgroundJobs({ jobs: [job], activeCount: 0 })).toThrow()
})

it('accepts a newly queued application job with no message yet', () => {
  expect(parseBackgroundJobs({ jobs: [{ ...job, message: null, totalBytes: null }], activeCount: 1 }).jobs[0].message).toBeNull()
})

it('retains concurrent active jobs alongside bounded history and rejects an oversized projection', () => {
  const jobs = Array.from({ length: 512 }, (_, index) => ({ ...job,
    id: `11111111-1111-4111-8111-${index.toString(16).padStart(12, '0')}`,
    state: index < 412 ? 'Running' : 'Succeeded' }))
  expect(parseBackgroundJobs({ jobs, activeCount: 412 }).jobs).toHaveLength(512)
  expect(() => parseBackgroundJobs({ jobs: [...jobs, { ...job, id: '22222222-2222-4222-8222-222222222222' }], activeCount: 413 })).toThrow()
})
