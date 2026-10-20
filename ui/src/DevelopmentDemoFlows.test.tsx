import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import type { AppInstanceView } from './contracts'
import { DevelopmentDemoControls, installDevelopmentDemo, useDevelopmentDemoRevision, type DevelopmentDemoController } from './developmentDemo'
import { App } from './main'

const instance: AppInstanceView = {
  kind: 'Staging', displayName: 'TogetherServer DEVELOPMENT DEMO', isStaging: true, isDemo: true,
  freshWorldsOnly: true, startupAvailable: false, updatesAvailable: false,
  localPort: 5129, companionPort: 5133, valheimPort: 2458, minecraftJavaPort: 25566,
  minecraftBedrockPort: 19134, dataRoot: 'C:\\Synthetic\\TogetherServer-Demo', dataIsolation: 'Demo only.'
}
let controller: DevelopmentDemoController | null = null
let realFetch: ReturnType<typeof vi.fn>

function DemoApp() {
  const revision = useDevelopmentDemoRevision()
  return <App key={revision} developmentDemoControls={<DevelopmentDemoControls />} />
}

async function selectServerSection(name: string) {
  const sections = await screen.findByRole('navigation', { name: 'Selected server sections' })
  const button = within(sections).getByRole('button', { name })
  // The app awaits its scope/draft guard before applying a section change.
  // Flush that action and its effects before checking the destination surface.
  await act(async () => { fireEvent.click(button) })
  await waitFor(() => expect(button).toHaveAttribute('aria-current', 'page'))
}

beforeEach(() => {
  window.localStorage.clear()
  realFetch = vi.fn(async () => { throw new Error('Unexpected real request') })
  vi.stubGlobal('fetch', realFetch)
  controller = installDevelopmentDemo(instance)
})
afterEach(() => { cleanup(); controller?.uninstall(); controller = null })

it('shows one grouped workspace and hosts a consented guest copy with one Continue and start', async () => {
  render(<DemoApp />)
  expect(await screen.findByRole('heading', { name: 'Meadow friends · DEMO' })).toBeInTheDocument()
  expect(screen.getByRole('complementary', { name: 'Demo controls' })).toHaveTextContent('FAKE DATA')
  expect(screen.queryByRole('button', { name: 'Join' })).not.toBeInTheDocument()
  expect(await screen.findByRole('button', { name: 'Open Morgan’s Meadow · DEMO on Morgan' })).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Backups' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Sharing & hosting' }))
  const permission = await screen.findByRole('checkbox', { name: 'Can host this save for Riley’s desktop · DEMO' })
  await waitFor(() => expect(permission).toBeEnabled())
  expect(permission).toBeChecked()
  fireEvent.click(permission)
  await waitFor(() => { expect(permission).not.toBeChecked(); expect(permission).toBeEnabled() })
  fireEvent.click(permission)
  await waitFor(() => { expect(permission).toBeChecked(); expect(permission).toBeEnabled() })
  fireEvent.click(screen.getByRole('button', { name: 'Open Morgan’s Meadow · DEMO on Morgan' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const dialog = await screen.findByRole('dialog', { name: 'Host on this PC' })
  fireEvent.click(within(dialog).getByRole('checkbox', { name: /Keep this save on my PC/ }))
  const start = within(dialog).getByRole('button', { name: 'Continue and start' })
  expect(start).toBeEnabled()
  fireEvent.click(start)
  expect(await screen.findByRole('heading', { name: 'Morgan’s Meadow · local DEMO copy' })).toBeInTheDocument()
  expect(await screen.findByRole('heading', { name: 'Hosting for friends' })).toBeInTheDocument()
  const localRow = await screen.findByRole('button', { name: 'Open Morgan’s Meadow · local DEMO copy on this PC' })
  await waitFor(() => expect(localRow).toHaveTextContent('Hosted for Morgan'))
  expect(await screen.findByText('Hosting for Morgan')).toBeInTheDocument()
  expect(screen.getByText('Shared save: Morgan’s Meadow · DEMO')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Stop server' })).toBeEnabled()
  fireEvent.click(screen.getByRole('button', { name: 'Open Meadow friends · DEMO on this PC' }))
  expect(await screen.findByRole('heading', { name: 'Meadow friends · DEMO' })).toBeInTheDocument()
  expect(localRow).toHaveTextContent('Hosted for Morgan')
  fireEvent.click(localRow)
  expect(await screen.findByRole('heading', { name: 'Morgan’s Meadow · local DEMO copy' })).toBeInTheDocument()
  expect(screen.getByText('Hosting for Morgan')).toBeInTheDocument()
  expect(realFetch).not.toHaveBeenCalled()
}, 15_000)

it('switches to a missing-server scene and resets open dialogs without using a native picker', async () => {
  render(<DemoApp />)
  await screen.findByRole('heading', { name: 'Meadow friends · DEMO' })
  await act(async () => controller?.reset('friend-missing-server'))
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const dialog = await screen.findByRole('dialog', { name: 'Host on this PC' })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Browse' }))
  await waitFor(() => expect(within(dialog).queryByRole('textbox', { name: 'Installed server file' })).not.toBeInTheDocument())
  expect(within(dialog).getByRole('heading', { name: 'Review before hosting your own copy' })).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Reset fake data' }))
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Host on this PC' })).not.toBeInTheDocument())
  expect(screen.getByRole('combobox', { name: 'Demo scene' })).toHaveValue('friend-missing-server')
  expect(realFetch).not.toHaveBeenCalled()
})

it('keeps a player lookup quiet, shows a repeated problem, and clears it after a successful refresh', async () => {
  controller?.reset('players-refreshing')
  render(<DemoApp />)
  await screen.findByRole('heading', { name: 'Meadow friends · DEMO' })
  expect(screen.queryByText('Player count has been unavailable repeatedly.')).not.toBeInTheDocument()
  expect(document.querySelector('.server-progress')).toBeNull()
  expect(screen.queryByText('Checking players…')).not.toBeInTheDocument()
  await selectServerSection('Players')
  let players = await screen.findByRole('region', { name: 'Players' })
  expect(within(players).queryByRole('note')).not.toBeInTheDocument()
  expect(within(players).getByRole('button', { name: 'Refresh count' })).toBeDisabled()
  expect(within(players).queryByRole('status')).not.toBeInTheDocument()
  expect(within(players).getByLabelText('Player count unavailable')).toHaveTextContent('—')
  await act(async () => controller?.reset('players-repeated-issue'))
  await screen.findByRole('heading', { name: 'Meadow friends · DEMO' })
  await selectServerSection('Players')
  players = await screen.findByRole('region', { name: 'Players' })
  expect(within(players).getByText('Player count unavailable after repeated checks')).toBeInTheDocument()
  fireEvent.click(within(players).getByRole('button', { name: 'Refresh count' }))
  await waitFor(() => expect(within(players).getByText('0 / 10 online')).toBeInTheDocument())
  expect(within(players).queryByRole('note')).not.toBeInTheDocument()
  expect(realFetch).not.toHaveBeenCalled()
}, 15_000)

it.each(['friend-players-online', 'friend-players-checking'] as const)('keeps %s quiet with disabled Stop and a readable count', async scene => {
  controller?.reset(scene)
  render(<DemoApp />)
  const play = await screen.findByRole('region', { name: 'Play' })
  expect(within(play).getByRole('button', { name: 'Stop server' })).toBeDisabled()
  expect(within(play).queryByRole('status')).not.toBeInTheDocument()
  expect(within(play).queryByRole('alert')).not.toBeInTheDocument()
  expect(within(play).queryByText('Checking players. Stop waits for a fresh count of zero.')).not.toBeInTheDocument()
  expect(play.querySelector('.warning-text')).toBeNull()
  const row = await screen.findByRole('button', { name: 'Open Morgan’s Meadow · DEMO on Morgan' })
  expect(row).toHaveTextContent(scene === 'friend-players-online' ? '2 / 10 online' : 'Players —')
  await selectServerSection('Settings')
  expect(screen.queryByText(/Restart unavailable:/)).not.toBeInTheDocument()
  expect(screen.queryByText(/Players are online/, { selector: '.inline-blocker' })).not.toBeInTheDocument()
  expect(realFetch).not.toHaveBeenCalled()
}, 15_000)

it('keeps earlier count warnings out of the notification badge and menu while preserving history', async () => {
  controller?.reset('players-refreshing')
  render(<DemoApp />)
  await screen.findByRole('heading', { name: 'Meadow friends · DEMO' })
  expect(await screen.findByLabelText('Notifications')).toBeInTheDocument()
  expect(screen.queryByLabelText('Notifications, new activity')).not.toBeInTheDocument()
  fireEvent.click(screen.getByLabelText('Notifications'))
  expect(screen.getByText('No recent notifications.')).toBeVisible()
  expect(screen.queryByText('DEMO: earlier count check unavailable.')).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Open Attention Center' }))
  expect(await screen.findByText('DEMO: earlier count check unavailable.')).toBeInTheDocument()
  expect(realFetch).not.toHaveBeenCalled()
}, 15_000)

it('retains unrelated explicit refresh failures while the latest count observation is unavailable', async () => {
  controller?.reset('players-repeated-issue')
  const demoFetch = globalThis.fetch
  vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
    if (String(input).endsWith('/players/refresh')) {
      const snapshot = await (await demoFetch('/api/local/snapshot')).json()
      return new Response(JSON.stringify({ ok: false, code: 'DataRecoveryRequired',
        message: 'Synthetic recovery requires review.', snapshot }), { headers: { 'Content-Type': 'application/json' } })
    }
    return demoFetch(input, init)
  })
  render(<DemoApp />)
  await screen.findByRole('heading', { name: 'Meadow friends · DEMO' })
  await selectServerSection('Players')
  const players = await screen.findByRole('region', { name: 'Players' })
  fireEvent.click(within(players).getByRole('button', { name: 'Refresh count' }))
  await waitFor(() => expect(screen.getAllByText('Synthetic recovery requires review.').some(element => element.closest('[role=status], [role=alert]'))).toBe(true))
  expect(realFetch).not.toHaveBeenCalled()
}, 15_000)
