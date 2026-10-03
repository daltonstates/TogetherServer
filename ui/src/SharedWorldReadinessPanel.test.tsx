import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { parseTakeoverReadiness, SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'

it('shows rehearsal limits and keeps takeover unavailable', async () => {
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    calls.push(url)
    return new Response(JSON.stringify({ ready: false,
      reasons: ['The owner has not granted this PC takeover permission.',
        'Disposable file copy passed hash checks. A real game load, join, and save still need testing.'],
      version: 2, versionHash: 'A'.repeat(64), rehearsalPassed: true }),
    { status: 200, headers: { 'Content-Type': 'application/json' } })
  }))
  render(<SharedWorldReadinessPanel profileId="11111111-1111-4111-8111-111111111111" />)
  fireEvent.click(screen.getByText('Check this PC for future hosting'))
  fireEvent.click(screen.getByRole('button', { name: 'Make disposable test copy' }))
  await waitFor(() => expect(calls.some(url => url.endsWith('/rehearse'))).toBe(true))
  expect(await screen.findByText('Disposable copy checked and removed.')).toBeInTheDocument()
  expect(screen.getByText(/real game load, join, and save still need testing/)).toBeInTheDocument()
  expect(screen.queryByText('Ready to host')).not.toBeInTheDocument()
})

it('rejects malformed readiness data', () => {
  expect(() => parseTakeoverReadiness({ ready: true, reasons: [], version: 1,
    versionHash: 'A'.repeat(64), rehearsalPassed: 'yes' })).toThrow()
})
