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
  it.each([true, false])('omits ordinary unshared Play details when capability is %s and no sharing evidence exists', async available => {
    const requests: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      requests.push(url)
      return reply(url.endsWith('/recovery') ? { state: 'NoOffer', authorityHeadHash: null } :
        { consented: true, hostVersion: null, thisPcVersion: null, rosterRevision: null, state: 'Host version not checked', error: null })
    }))
    render(<SharedHostingIdentity profileId={deviceId} host={false} available={available} serverState="Ready" serverStatusFresh />)
    await act(async () => {})
    expect(screen.queryByRole('region', { name: 'Shared world Host' })).not.toBeInTheDocument()
    expect(requests.some(url => url.endsWith('/handoff/restore'))).toBe(false)
  })
  it.each([{ thisPcVersion: 2 }, { hostVersion: 3 }, { rosterRevision: 1 },
    { state: 'Competing save histories. Review before receiving another save.' }])(
    'retains Unknown signed authority when shared-world evidence exists: %j', async evidence => {
      vi.stubGlobal('fetch', vi.fn(async (url: string) => reply(url.endsWith('/recovery') ?
        { state: 'NoOffer', authorityHeadHash: null } : { consented: false, hostVersion: null,
          thisPcVersion: null, rosterRevision: null, state: 'Consent off', error: null, ...evidence })))
      render(<SharedHostingIdentity profileId={deviceId} host={false} available={false} serverState="Ready" serverStatusFresh />)
      expect(await screen.findByText('Host decision not checked')).toBeInTheDocument()
      expect(screen.getByText('Current Host status: Unknown')).toBeInTheDocument()
      expect(screen.getByText('Connected server: Ready · game join not tested')).toBeInTheDocument()
    })
  it('retains last known shared evidence as Unknown after a relevance read fails and clears it on a valid unshared read', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    let evidenceRead = 0
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.endsWith('/recovery')) return reply({ state: 'NoOffer', authorityHeadHash: null })
      if (++evidenceRead === 2) throw new Error('offline')
      return reply({ consented: false, hostVersion: null, thisPcVersion: evidenceRead === 1 ? 2 : null,
        rosterRevision: null, state: 'Consent off', error: null })
    }))
    render(<SharedHostingIdentity profileId={deviceId} host={false} serverState="Ready" serverStatusFresh />)
    await act(async () => {})
    expect(screen.getByText('Host decision not checked')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(screen.getByText('Last known: Host decision not checked')).toBeInTheDocument()
    expect(screen.getByText('Current Host status: Unknown · latest Host check failed')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(screen.queryByRole('region', { name: 'Shared world Host' })).not.toBeInTheDocument()
  })

  it('keeps an ordinary unshared Host free of hosting authority details', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply({ enabled: false, latest: null, authority: { state: 'NoTakeover' } })))
    render(<SharedHostingIdentity profileId={deviceId} host />)
    await act(async () => {})
    expect(screen.queryByRole('region', { name: 'Shared world Host' })).not.toBeInTheDocument()
  })
  it('labels the original Host as this PC without claiming that shared hosting has not moved', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply({ enabled: true, latest: {}, authority: { state: 'NoTakeover' } })))
    render(<SharedHostingIdentity profileId={deviceId} host serverState="Offline" serverStatusFresh />)
    expect(await screen.findByText('Original Host PC · this PC’s server')).toBeInTheDocument()
    expect(screen.getByText('This PC’s server status: Offline')).toBeInTheDocument()
    expect(screen.queryByText(/hosting has not moved/)).not.toBeInTheDocument()
  })
  it('retains a known shared identity as Unknown when capability disappears and the local read fails', async () => {
    let fail = false
    vi.stubGlobal('fetch', vi.fn(async () => { if (fail) throw new Error('offline'); return reply(quorum) }))
    const view = render(<SharedHostingIdentity profileId={deviceId} host={false} available serverState="Ready" serverStatusFresh />)
    await screen.findByText(/Approved Host · PC ending/)
    fail = true
    view.rerender(<SharedHostingIdentity profileId={deviceId} host={false} available={false} serverState="Ready" serverStatusFresh />)
    await act(async () => {})
    expect(screen.getByText(/Last known: Approved Host · PC ending/)).toBeInTheDocument()
    expect(screen.getByText('Current Host status: Unknown · latest Host check failed')).toBeInTheDocument()
  })
  it('identifies a prepared local signed successor only from the verified stage and matching route projection', () => {
    const source = { staged: true, recordHash: head.recordHash, controlRouteAddress: head.hostAddress,
      controlRouteFingerprint: 'B'.repeat(64) }
    expect(parseSharedSuccessorIdentity(source)?.label).toBe('This PC · next Host finishing setup')
    for (const patch of [{ staged: false }, { recordHash: null }, { controlRouteFingerprint: null }, { controlRouteAddress: null }]) {
      expect(parseSharedSuccessorIdentity({ ...source, ...patch })).toBeNull()
    }
    expect(parseSharedSuccessorIdentity({ thisPcVersion: 3, state: 'Ready' })).toBeNull()
  })
  it('does not promote armed, historical, or planned-only recovery metadata into a current Host', () => {
    for (const state of ['OfferArmed', 'HistoricalRecovery', 'OfferClosed', 'NoOffer']) {
      expect(parseSharedHostingIdentity({ ...quorum, state }, false).label).toBe('Host decision not checked')
    }
    expect(() => parseSharedHostingIdentity({ ...quorum, majorityReached: false }, false)).toThrow()
  })
  it('keeps fenced and conflicting Host authority explicit', () => {
    expect(parseSharedHostingIdentity({ authority: { state: 'OldHostFenced', head } }, true)).toMatchObject({ local: false })
    expect(parseSharedHostingIdentity({ authority: { state: 'CompetingHistories' } }, true).warning).toMatch(/stopped/)
  })
  it('does not attach an unrelated connected server Ready observation to the signed Host', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply(quorum)))
    render(<SharedHostingIdentity profileId={deviceId} host={false} worldName="Alderheim" serverState="Ready" serverStatusFresh
      observedHostAddress="https://192.0.2.10:5131" />)
    expect(await screen.findByText(/Approved Host · PC ending/)).toBeInTheDocument()
    expect(screen.getByText('Current Host status: Unknown')).toBeInTheDocument()
    expect(screen.queryByText('Alderheim')).not.toBeInTheDocument()
    expect(screen.queryByText(/192\.0\.2\./)).not.toBeInTheDocument()
  })
  it('separates Friend connection readiness even when the address matches the signed Host', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => reply(quorum)))
    const { rerender } = render(<SharedHostingIdentity profileId={deviceId} host={false} serverState="Ready"
      observedHostAddress={head.hostAddress} />)
    await screen.findByText(/Approved Host · PC ending/)
    expect(screen.getByText('Current Host status: Unknown')).toBeInTheDocument()
    rerender(<SharedHostingIdentity profileId={deviceId} host={false} serverState="Ready" serverStatusFresh
      observedHostAddress={head.hostAddress} />)
    expect(screen.getByText('Current Host status: Unknown')).toBeInTheDocument()
    expect(screen.queryByText(/Connected server: Ready/)).not.toBeInTheDocument()
  })
  it('preserves last known identity but clears current readiness after a failed identity check', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    const fetch = vi.fn().mockResolvedValueOnce(reply({ authority: { state: 'ThisPcHost', head } })).mockRejectedValue(new Error('offline'))
    vi.stubGlobal('fetch', fetch)
    render(<SharedHostingIdentity profileId={deviceId} host serverState="Ready" serverStatusFresh
      observedHostAddress={head.hostAddress} />)
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(screen.getByText('This PC’s server status: Ready · game join not tested')).toBeInTheDocument()
    await act(async () => { await vi.advanceTimersByTimeAsync(5000) })
    expect(screen.getByText(/Last known: This PC · approved Host/)).toBeInTheDocument()
    expect(screen.getByText('This PC’s server status: Unknown · latest Host check failed')).toBeInTheDocument()
  })
})
