import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { SharedWorldSeparateRoutePanel } from './SharedWorldSeparateRoutePanel'

const profile = '11111111-1111-4111-8111-111111111111'
const proof = { branchHash: 'A'.repeat(64), offer: {
  proposal: { profileId: profile, candidateAddress: 'https://192.0.2.10:5131' },
  version: { number: 3 } }, signature: 'signed candidate proof' }
const reply = (value: unknown) => new Response(JSON.stringify(value), { status: 200 })

it('shares a signed fork and sends an exact proof only after local review', async () => {
  const sent: unknown[] = []
  vi.stubGlobal('fetch', vi.fn(async (_url: string, init?: RequestInit) => {
    if (_url.includes('/hosting/')) return reply({ recorded: true, restored: false,
      readyForManualStart: false, reviewRequired: false, running: false,
      message: 'The signed separate copy is recorded.', localProfileId: profile,
      branchHash: proof.branchHash, preparedServerRoot: null, requiredAddOns: [] })
    if (init?.method === 'POST') {
      sent.push(JSON.parse(String(init.body)) as unknown)
      return reply({ controlRouteObserved: true, code: 'SeparateRouteObserved',
        message: 'A second approved PC reached the pinned direct address.' })
    }
    return reply([proof])
  }))
  render(<SharedWorldSeparateRoutePanel profileId={profile} separateCopies={1} />)
  fireEvent.click(screen.getByText('Separate-copy route check'))
  expect(screen.getByText(/does not settle the split/)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Show this PC’s signed separate-copy proofs' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Review separate copy and show proof code' }))
  expect(screen.getByLabelText('Signed separate-copy proof code')).toHaveValue(JSON.stringify(proof))
  expect(screen.queryByRole('button', { name: 'Start warned separate copy' })).not.toBeInTheDocument()
  const checkRoute = screen.getByRole('button', { name: 'Check candidate control route from this PC' })
  await waitFor(() => expect(screen.getByText('The signed separate copy is recorded.')).toBeInTheDocument())
  fireEvent.change(screen.getByLabelText('Proof code from candidate PC'),
    { target: { value: '{bad' } })
  await waitFor(() => expect(checkRoute).not.toBeDisabled())
  fireEvent.click(checkRoute)
  expect(await screen.findByRole('alert')).toHaveTextContent('Separate-copy proof')
  expect(sent).toHaveLength(0)
  fireEvent.change(screen.getByLabelText('Proof code from candidate PC'),
    { target: { value: JSON.stringify(proof) } })
  fireEvent.click(checkRoute)
  await waitFor(() => expect(sent).toEqual([{ branch: proof }]))
  expect(await screen.findByText(/second approved PC reached/)).toBeInTheDocument()
  vi.unstubAllGlobals()
})

it('removes manual Start when a refreshed route check has expired', async () => {
  let expired = false
  vi.stubGlobal('fetch', vi.fn(async (url: string) =>
    url.includes('/hosting/') ? reply({ recorded: true, restored: true,
      readyForManualStart: !expired, reviewRequired: false, running: false,
      message: expired ? 'A recent signed control-route check is required.' :
        'Manual separate-copy Start is available.', localProfileId: profile,
      branchHash: proof.branchHash, preparedServerRoot: null, requiredAddOns: [] }) :
      reply([proof])))
  render(<SharedWorldSeparateRoutePanel profileId={profile} separateCopies={1} />)
  fireEvent.click(screen.getByText('Separate-copy route check'))
  fireEvent.click(screen.getByRole('button', { name: 'Show this PC’s signed separate-copy proofs' }))
  fireEvent.click(await screen.findByRole('button', {
    name: 'Review separate copy and show proof code' }))
  expect(await screen.findByRole('button', { name: 'Start warned separate copy' })).toBeInTheDocument()
  expired = true
  fireEvent.click(screen.getByRole('button', { name: 'Refresh separate-copy status' }))
  await waitFor(() => expect(screen.queryByRole('button', {
    name: 'Start warned separate copy' })).not.toBeInTheDocument())
  expect(screen.getByText('A recent signed control-route check is required.')).toBeInTheDocument()
  vi.unstubAllGlobals()
})
