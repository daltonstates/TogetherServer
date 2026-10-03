import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { parseTakeoverReadiness, SharedWorldReadinessPanel } from './SharedWorldReadinessPanel'

it('shows rehearsal limits and keeps takeover unavailable', async () => {
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    calls.push(url)
    return new Response(JSON.stringify({ ready: false,
      reasons: ['The owner has not granted this PC takeover permission.',
        'Disposable file copy passed hash checks. Fixture process rehearsal is unavailable because this app cannot verify the selected executable\'s provenance. A real game load, join, and save still need testing.'],
      version: 2, versionHash: 'A'.repeat(64), rehearsalPassed: true,
      managedProcessRehearsalPassed: false }),
    { status: 200, headers: { 'Content-Type': 'application/json' } })
  }))
  render(<SharedWorldReadinessPanel profileId="11111111-1111-4111-8111-111111111111" />)
  fireEvent.click(screen.getByText('Check this PC for future hosting'))
  fireEvent.click(screen.getByRole('button', { name: 'Rehearse disposable copy' }))
  await waitFor(() => expect(calls.some(url => url.endsWith('/rehearse'))).toBe(true))
  expect(await screen.findByText('Disposable copy checked and removed.')).toBeInTheDocument()
  expect(screen.getByText(/real game load, join, and save still need testing/)).toBeInTheDocument()
  expect(screen.getByText(/Fixture process rehearsal is unavailable/)).toBeInTheDocument()
  expect(screen.queryByText('Ready to host')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Check direct-IP control route' })).toBeDisabled()
})

it('rejects malformed readiness data', () => {
  expect(() => parseTakeoverReadiness({ ready: true, reasons: [], version: 1,
    versionHash: 'A'.repeat(64), rehearsalPassed: 'yes' })).toThrow()
})

it('finishes a verified majority with signed add-ons before showing manual Start', async () => {
  const hash = 'A'.repeat(64)
  const fingerprint = 'B'.repeat(64)
  const addOn = { name: 'Reviewed mod', version: '1.0', requiredGameVersion: '2.0',
    type: 'Factorio mod', id: null }
  let ready = false
  let finishBody: Record<string, unknown> | null = null
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith('/handoff/finish')) {
      finishBody = JSON.parse(String(init?.body)) as Record<string, unknown>
      ready = true
      return new Response(JSON.stringify({ ok: true, code: 'ReadyForManualStart',
        message: 'Pre-Start checks passed.', pendingChecks: null }), { status: 200 })
    }
    return new Response(JSON.stringify({ staged: true, restored: true,
      recordHash: hash, message: 'Verified copy restored.', pendingChecks: [],
      preparedServerRoot: null, readyForManualStart: ready, requiredAddOns: [addOn],
      controlRouteFingerprint: fingerprint }),
    { status: 200 })
  }))
  render(<SharedWorldReadinessPanel profileId="11111111-1111-4111-8111-111111111111" />)
  fireEvent.click(screen.getByText('Check this PC for future hosting'))
  expect(await screen.findByRole('button', { name: 'Finish setup and checks' })).toBeInTheDocument()
  fireEvent.click(screen.getByText('Required add-ons'))
  expect(screen.getByText(/Reviewed mod 1.0/)).toBeInTheDocument()
  expect(screen.getByText(fingerprint)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Finish setup and checks' }))
  await waitFor(() => expect(finishBody).not.toBeNull())
  const sent = finishBody as Record<string, unknown> | null
  expect(sent && sent.recordHash).toBe(hash)
  expect((sent?.setup as { enabledAddOns: unknown[] }).enabledAddOns).toEqual([addOn])
  expect(await screen.findByText(/Ready for manual Start/)).toBeInTheDocument()
})
