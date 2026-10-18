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
  fireEvent.click(screen.getByText('Recovery copies on this PC'))
  expect(screen.getByText(/does not settle the split/)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Show recovery copies on this PC' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Prepare this recovery copy' }))
  fireEvent.click(screen.getByText('Signed recovery proof'))
  expect(screen.getByLabelText('Signed separate-copy proof code')).toHaveValue(JSON.stringify(proof))
  expect(screen.queryByRole('button', { name: 'Start recovery copy' })).not.toBeInTheDocument()
  fireEvent.click(screen.getByText('Optional check from another PC'))
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

it('keeps solo Start available without a current second-PC route while requiring the warning', async () => {
  let expired = false
  const localProof = { ...proof, offer: { ...proof.offer,
    proposal: { ...proof.offer.proposal, candidateAddress: 'https://127.0.0.1:5131' } } }
  vi.stubGlobal('fetch', vi.fn(async (url: string) =>
    url.includes('/hosting/') ? reply({ recorded: true, restored: true,
      readyForManualStart: true, controlRouteVerified: !expired, reviewRequired: false, running: false,
      message: expired ? 'The optional second-PC route observation expired; solo recovery remains available.' :
        'Manual separate-copy Start is available.', localProfileId: profile,
      branchHash: proof.branchHash, preparedServerRoot: null, requiredAddOns: [] }) :
      reply([localProof])))
  render(<SharedWorldSeparateRoutePanel profileId={profile} separateCopies={1} />)
  fireEvent.click(screen.getByText('Recovery copies on this PC'))
  fireEvent.click(screen.getByRole('button', { name: 'Show recovery copies on this PC' }))
  fireEvent.click(await screen.findByRole('button', {
    name: 'Prepare this recovery copy' }))
  expect(await screen.findByRole('button', { name: 'Start recovery copy' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Start recovery copy' })).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
  expect(screen.queryByText('Before starting this recovery copy')).not.toBeInTheDocument()
  expect(screen.getByLabelText('I understand another server may still be running')).not.toBeChecked()
  expect(screen.getByRole('button', { name: 'Start recovery copy' })).toBeDisabled()
  expired = true
  fireEvent.click(screen.getByRole('button', { name: 'Refresh separate-copy status' }))
  expect(await screen.findByText('The optional second-PC route observation expired; solo recovery remains available.')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Start recovery copy' })).toBeDisabled()
  fireEvent.click(screen.getByLabelText('I understand another server may still be running'))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Start recovery copy' })).toBeEnabled())
  vi.unstubAllGlobals()
})

it('finishes a reopened restored copy using newly reviewed server inputs without restoring or starting it again', async () => {
  let ready = false
  const base = `/api/local/friend/${profile}/shared-world/recovery/separate`
  const serverFile = 'C:\\Games\\Valheim\\valheim_server.exe'
  const localProfileId = '22222222-2222-4222-8222-222222222222'
  const savedCopy = { ...proof, offer: { ...proof.offer, version: { number: 3, game: 'Valheim' } } }
  const requiredAddOns = [{ name: 'Reviewed package', version: '1', requiredGameVersion: '0.221.4',
    type: 'Plugin', id: null }]
  const mutations: { path: string; method: string; body: unknown }[] = []
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    if (init?.method) {
      mutations.push({ path: url, method: init.method, body: JSON.parse(String(init.body)) })
      ready = true
      return reply({ ok: true, code: 'SeparateSetupReady', message: 'Local setup checks passed.' })
    }
    if (url.includes('/hosting/')) return reply({ recorded: true, restored: true,
      readyForManualStart: ready, reviewRequired: false, running: false,
      message: ready ? 'Manual Start is available.' : 'The restored copy still needs local setup checks.',
      localProfileId, branchHash: proof.branchHash, preparedServerRoot: null, requiredAddOns })
    return reply([savedCopy])
  }))
  render(<SharedWorldSeparateRoutePanel profileId={profile} separateCopies={1} expanded />)
  fireEvent.click(screen.getByRole('button', { name: 'Show recovery copies on this PC' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Prepare this recovery copy' }))
  await screen.findByText('The restored copy still needs local setup checks.')
  expect(screen.getByLabelText('Valheim dedicated server')).toHaveValue('')
  expect(screen.getByLabelText('Installed game version')).toHaveValue('')
  expect(screen.queryByRole('button', { name: 'Restore into a fresh managed world' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Start recovery copy' })).not.toBeInTheDocument()
  expect(mutations).toEqual([])
  fireEvent.change(screen.getByLabelText('Valheim dedicated server'), { target: { value: serverFile } })
  fireEvent.change(screen.getByLabelText('Installed game version'), { target: { value: '0.221.4' } })
  fireEvent.change(screen.getByLabelText('Future Friend control port'), { target: { value: '5141' } })
  fireEvent.change(screen.getByLabelText('Future game port'), { target: { value: '2466' } })
  fireEvent.click(screen.getByLabelText('I set a new game password on this PC'))
  fireEvent.click(screen.getByText('Recovery steps and route checks'))
  fireEvent.click(screen.getByText('Recovery steps and route checks'))
  expect(screen.getByLabelText('Valheim dedicated server')).toHaveValue(serverFile)
  expect(screen.getByLabelText('Installed game version')).toHaveValue('0.221.4')
  expect(screen.getByLabelText('Future Friend control port')).toHaveValue('5141')
  expect(screen.getByLabelText('I set a new game password on this PC')).toBeChecked()
  expect(mutations).toEqual([])
  fireEvent.click(screen.getByRole('button', { name: 'Finish local setup checks' }))
  await screen.findByText('Manual Start is available.')
  expect(mutations).toEqual([{ path: `${base}/finish`, method: 'POST', body: {
    branchHash: proof.branchHash,
    setup: { serverFile, gameVersion: '0.221.4', enabledAddOns: requiredAddOns,
      newPasswordConfigured: true, controlPort: 5141, gamePort: 2466 },
    executablePath: null, preparedServerRoot: null,
  } }])
  expect(screen.getByRole('button', { name: 'Start recovery copy' })).toBeDisabled()
  vi.unstubAllGlobals()
})
