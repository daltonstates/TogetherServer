import { act, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { parseSharedHostingIdentity, parseSharedSuccessorIdentity, SharedHostingIdentity } from './SharedHostingIdentity'

const deviceId = '11111111-1111-4111-8111-111111111111'
const head = { hostDeviceId: deviceId, recordHash: 'A'.repeat(64), hostAddress: 'https://192.0.2.20:5131' }
const quorum = { state: 'ObservedMajority', majorityReached: true, authorityHeadHash: 'A'.repeat(64),
  candidateDeviceId: deviceId, candidateAddress: head.hostAddress }
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

describe('Shared hosting identity', () => {
  it('keeps an ordinary unshared Host free of hosting authority details', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply({ enabled: false, latest: null, authority: { state: 'NoTakeover' } })))
    render(<SharedHostingIdentity profileId={deviceId} host />)
    await act(async () => {})
    expect(screen.queryByRole('region', { name: 'Shared world hosting identity' })).not.toBeInTheDocument()
  })
  it('identifies a prepared local signed successor only from the verified stage and matching route projection', () => {
    const source = { staged: true, recordHash: head.recordHash, controlRouteAddress: head.hostAddress,
      controlRouteFingerprint: 'B'.repeat(64) }
    expect(parseSharedSuccessorIdentity(source)?.label).toBe('This PC · signed successor preparing hosting')
    for (const patch of [{ staged: false }, { recordHash: null }, { controlRouteFingerprint: null }, { controlRouteAddress: null }]) {
      expect(parseSharedSuccessorIdentity({ ...source, ...patch })).toBeNull()
    }
    expect(parseSharedSuccessorIdentity({ thisPcVersion: 3, state: 'Ready' })).toBeNull()
  })
  it('does not promote armed, historical, or planned-only recovery metadata into a current Host', () => {
    for (const state of ['OfferArmed', 'HistoricalRecovery', 'OfferClosed', 'NoOffer']) {
      expect(parseSharedHostingIdentity({ ...quorum, state }, false).label).toBe('Signed Host not checked')
    }
    expect(() => parseSharedHostingIdentity({ ...quorum, majorityReached: false }, false)).toThrow()
  })
  it('keeps fenced and conflicting Host authority explicit', () => {
    expect(parseSharedHostingIdentity({ authority: { state: 'OldHostFenced', head } }, true)).toMatchObject({ local: false })
    expect(parseSharedHostingIdentity({ authority: { state: 'CompetingHistories' } }, true).warning).toMatch(/offline/)
  })
  it('does not attach an unrelated connected server Ready observation to the signed Host', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply(quorum)))
    render(<SharedHostingIdentity profileId={deviceId} host={false} worldName="Alderheim" serverState="Ready" serverStatusFresh
      observedHostAddress="https://192.0.2.10:5131" />)
    expect(await screen.findByText(/Signed Host · PC ending/)).toBeInTheDocument()
    expect(screen.getByText('Current Host server: Unknown')).toBeInTheDocument()
    expect(screen.queryByText('Alderheim')).not.toBeInTheDocument()
    expect(screen.queryByText(/192\.0\.2\./)).not.toBeInTheDocument()
  })
  it('separates Friend connection readiness even when the address matches the signed Host', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply(quorum)))
    const { rerender } = render(<SharedHostingIdentity profileId={deviceId} host={false} serverState="Ready"
      observedHostAddress={head.hostAddress} />)
    await screen.findByText(/Signed Host · PC ending/)
    expect(screen.getByText('Current Host server: Unknown')).toBeInTheDocument()
    rerender(<SharedHostingIdentity profileId={deviceId} host={false} serverState="Ready" serverStatusFresh
      observedHostAddress={head.hostAddress} />)
    expect(screen.getByText('Current Host server: Unknown')).toBeInTheDocument()
    expect(screen.queryByText(/Connected server: Ready observed/)).not.toBeInTheDocument()
  })
  it('preserves last known identity but clears current readiness after a failed identity check', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const fetch = vi.fn().mockResolvedValueOnce(reply({ authority: { state: 'ThisPcHost', head } })).mockRejectedValue(new Error('offline'))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostingIdentity profileId={deviceId} host serverState="Ready" serverStatusFresh
      observedHostAddress={head.hostAddress} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(screen.getByText('Current Host server: Ready observed · game join unverified')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(screen.getByText(/Last known: This PC · signed Host/)).toBeInTheDocument()
    expect(screen.getByText('Current Host server: Unknown · latest identity check failed')).toBeInTheDocument()
  })
})
