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

beforeEach(() => {
  window.localStorage.clear()
  realFetch = vi.fn(async () => { throw new Error('Unexpected real request') })
  vi.stubGlobal('fetch', realFetch)
  controller = installDevelopmentDemo(instance)
})
afterEach(() => { cleanup(); controller?.uninstall(); controller = null })

it('renders the native demo sample servers and completes Join preparation and explicit local Start', async () => {
  render(<DemoApp />)
  expect(await screen.findByRole('heading', { name: 'Meadow friends · DEMO' })).toBeInTheDocument()
  expect(screen.getByRole('complementary', { name: 'Demo controls' })).toHaveTextContent('FAKE DATA')
  fireEvent.click(screen.getByRole('button', { name: 'Backups' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Sharing & hosting' }))
  const permission = await screen.findByRole('checkbox', { name: 'Can host this save for Riley’s desktop · DEMO' })
  await waitFor(() => expect(permission).toBeEnabled())
  expect(permission).toBeChecked()
  fireEvent.click(permission)
  await waitFor(() => { expect(permission).not.toBeChecked(); expect(permission).toBeEnabled() })
  fireEvent.click(permission)
  await waitFor(() => { expect(permission).toBeChecked(); expect(permission).toBeEnabled() })
  fireEvent.click(screen.getByRole('button', { name: 'Join' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const dialog = await screen.findByRole('dialog', { name: 'Host on this PC' })
  fireEvent.click(within(dialog).getByRole('checkbox', { name: /Keep this save on my PC/ }))
  fireEvent.click(within(dialog).getByRole('button', { name: 'Prepare this PC' }))
  const start = await within(dialog).findByRole('button', { name: 'Start server on this PC' })
  expect(start).toBeEnabled()
  fireEvent.click(start)
  expect(await screen.findByRole('heading', { name: 'Meadow friends · local DEMO copy' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Stop server' })).toBeEnabled()
  expect(realFetch).not.toHaveBeenCalled()
})

it('switches to a missing-server scene and resets open dialogs without using a native picker', async () => {
  render(<DemoApp />)
  await screen.findByRole('heading', { name: 'Meadow friends · DEMO' })
  await act(async () => controller?.reset('friend-missing-server'))
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const dialog = await screen.findByRole('dialog', { name: 'Host on this PC' })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Browse' }))
  await waitFor(() => expect(within(dialog).getByRole('textbox', { name: 'Installed server file' })).toHaveValue(
    'C:\\TogetherServer-DEMO-FAKE-DATA\\Valheim\\valheim_server.exe'))
  fireEvent.click(screen.getByRole('button', { name: 'Reset fake data' }))
  await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Host on this PC' })).not.toBeInTheDocument())
  expect(screen.getByRole('combobox', { name: 'Demo scene' })).toHaveValue('friend-missing-server')
  expect(realFetch).not.toHaveBeenCalled()
})
