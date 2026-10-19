import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { changeJson, getJson, getLocalJson } from './api'
import { parseActionResult, parseAppInstance, parseBasicResult, parseCompanionInfo, parseFriendSnapshot, parseSnapshot, type AppInstanceView } from './contracts'
import { DevelopmentDemoControls, installDevelopmentDemo, useDevelopmentDemoRevision, type DevelopmentDemoController } from './developmentDemo'
import { FriendLocalHosting, parseLocalHostingStatus } from './FriendLocalHosting'

const primary = '11111111-1111-4111-8111-111111111111'
const local = '33333333-3333-4333-8333-333333333333'
const device = '55555555-5555-4555-8555-555555555555'
const base = `/api/local/friend/${primary}/shared-world`
const grantsPath = `/api/local/devices/${device}/shared-world/${primary}/grants`
const grants = { receive: true, eligibleHost: true, recoveryVoter: false, manageSharing: false, reshare: false }
const instance: AppInstanceView = {
  kind: 'Staging', displayName: 'TogetherServer DEMO', isStaging: true, isDemo: true,
  freshWorldsOnly: true, startupAvailable: false, updatesAvailable: false,
  localPort: 5129, companionPort: 5133, valheimPort: 2460, minecraftJavaPort: 25567, minecraftBedrockPort: 19136,
  dataRoot: 'C:\\Synthetic\\TogetherServer-Demo', dataIsolation: 'Isolated demo storage.'
}
let controller: DevelopmentDemoController | undefined
let realFetch: ReturnType<typeof vi.fn>
function install() {
  realFetch = vi.fn(() => { throw new Error('A real request must never be dispatched.') })
  vi.stubGlobal('fetch', realFetch)
  controller = installDevelopmentDemo(instance)
  return controller
}
async function snapshot() { return getJson('/api/local/snapshot', parseSnapshot) }
async function hosting() { return getLocalJson(`${base}/hosting`, parseLocalHostingStatus) }
async function mode(value: 'host' | 'friend') { await changeJson(`/api/local/mode/${value}`, 'POST', parseBasicResult) }
async function prepare() {
  return changeJson(`${base}/hosting/prepare`, 'POST', parseLocalHostingStatus, {
    allowReceiving: true, acceptSplitWarning: true, serverFile: null, gamePassword: null
  })
}
afterEach(() => { cleanup(); controller?.uninstall(); controller = undefined; vi.useRealTimers() })

describe('isolated development demo transport', () => {
  it.each([{ isDemo: false }, { isDemo: undefined }, { isStaging: false }, { kind: 'Production' as const }])(
    'refuses fake data without trusted staging/demo metadata: %j', patch => {
      const original = vi.fn()
      vi.stubGlobal('fetch', original)
      expect(() => installDevelopmentDemo({ ...instance, ...patch })).toThrow(/verified isolated DEMO/)
      expect(fetch).toBe(original)
    })

  it('preserves the exact trusted instance metadata and fails closed for unsupported, external and production-port requests', async () => {
    install()
    expect(await getJson('/api/local/instance', parseAppInstance)).toEqual(instance)
    const blocked = [
      '/api/local/unknown-demo-feature', '/api/companion/start', 'https://example.com/api/local/snapshot',
      'http://127.0.0.1:5127/api/local/snapshot', '//127.0.0.1:5127/api/local/snapshot',
      '/api/local/../../companion/start'
    ]
    for (const path of blocked) expect((await fetch(path)).status).toBe(503)
    expect((await fetch('/api/local/quit', { method: 'POST' })).status).toBe(503)
    expect((await fetch('/api/local/minecraft/install', { method: 'POST' })).status).toBe(503)
    expect(realFetch).not.toHaveBeenCalled()
  })

  it('keeps existing required setup, fake profiles and the shared fixture draft transport usable without persistent writes', async () => {
    install()
    const required = await (await fetch('/api/local/required-setup')).json()
    expect(required.features).toMatchObject([{ id: 'profile-name', completed: true }])
    expect(await (await fetch('/api/local/profile')).json()).toEqual({ displayName: 'Riley Demo' })
    const view = await snapshot()
    expect(view.mode).toBe('Host')
    if (view.mode !== 'Host') throw new Error('Expected a demo Host.')
    expect(view.settings.profiles.map(profile => profile.name)).toEqual(['Meadow friends · DEMO', 'Lantern village · DEMO'])
    const draft = { purpose: 'chat', profileId: primary, connectionId: null, key: 'composer' }
    const saved = await (await fetch('/api/local/ui-drafts', { method: 'PUT', body: JSON.stringify({ ...draft, expectedRevision: 0, text: 'Demo draft' }) })).json()
    expect(saved).toMatchObject({ ok: true, revision: 1, text: 'Demo draft' })
    controller?.reset()
    const reset = await (await fetch('/api/local/ui-drafts/read', { method: 'POST', body: JSON.stringify(draft) })).json()
    expect(reset).toMatchObject({ text: null, revision: 0 })
    expect(realFetch).not.toHaveBeenCalled()
  })

  it('applies owner hosting permission atomically across Host and Join and refuses stale or partial grants', async () => {
    install()
    await changeJson(`/api/local/profiles/${primary}/shared-world`, 'PUT', parseBasicResult, { enabled: false })
    await changeJson(grantsPath, 'PUT', parseBasicResult, { grants: { ...grants, receive: false, eligibleHost: false } })
    await mode('friend')
    expect((await hosting()).allowed).toBe(false)
    expect((await fetch(`${base}/hosting/prepare`, { method: 'POST', body: JSON.stringify({ allowReceiving: true, acceptSplitWarning: true }) })).status).toBe(409)
    await mode('host')
    expect((await fetch(grantsPath, { method: 'PUT', body: JSON.stringify({ grants: { receive: true, eligibleHost: true } }) })).status).toBe(409)
    await changeJson(grantsPath, 'PUT', parseBasicResult, { grants })
    const shared = await (await fetch(`/api/local/profiles/${primary}/shared-world`)).json()
    expect(shared.enabled).toBe(true)
    const companion = await getJson('/api/local/companion', parseCompanionInfo)
    expect(companion.devices[0].saveReceiveProfileIds).toEqual([primary])
    await mode('friend')
    expect((await hosting()).allowed).toBe(true)
    expect((await fetch(grantsPath, { method: 'PUT', body: JSON.stringify({ grants: { ...grants, eligibleHost: false } }) })).status).toBe(409)
    expect((await hosting()).allowed).toBe(true)
    expect(realFetch).not.toHaveBeenCalled()
  })

  it('prepares without starting and requires current grants plus the exact branch for both guarded Start routes', async () => {
    install().reset('friend-hosting')
    const prepared = await prepare()
    expect(prepared).toMatchObject({ readyForManualStart: true, running: false, localProfileId: local })
    await mode('host')
    let view = await snapshot()
    expect(view.mode === 'Host' && view.runs.find(run => run.profileId === local)?.state).toBe('Offline')
    expect((await fetch(`/api/local/profiles/${local}/start`, { method: 'POST' })).status).toBe(409)
    expect((await fetch(`/api/local/profiles/${local}/shared-world/hosting/start`, {
      method: 'POST', body: JSON.stringify({ branchHash: 'E'.repeat(64), acceptSplitWarning: true })
    })).status).toBe(409)
    await changeJson(grantsPath, 'PUT', parseBasicResult, { grants: { ...grants, eligibleHost: false } })
    expect((await fetch(`/api/local/profiles/${local}/shared-world/hosting/start`, {
      method: 'POST', body: JSON.stringify({ branchHash: prepared.branchHash, acceptSplitWarning: true })
    })).status).toBe(409)
    await changeJson(grantsPath, 'PUT', parseBasicResult, { grants })
    const started = await changeJson(`/api/local/profiles/${local}/shared-world/hosting/start`, 'POST', parseActionResult,
      { branchHash: prepared.branchHash, acceptSplitWarning: true })
    expect(started.snapshot.runs.find(run => run.profileId === local)?.state).toBe('Ready')
    await changeJson(`/api/local/profiles/${local}/stop`, 'POST', parseActionResult)
    await mode('friend')
    const accepted = await changeJson(`${base}/recovery/separate/start`, 'POST', parseBasicResult,
      { branchHash: prepared.branchHash, localProfileId: local, acceptSplitWarning: true })
    expect(accepted.ok).toBe(true)
    expect((await hosting()).running).toBe(true)
    await mode('host')
    view = await snapshot()
    expect(view.mode === 'Host' && view.runs.find(run => run.profileId === local)?.state).toBe('Ready')
    expect(realFetch).not.toHaveBeenCalled()
  })

  it('keeps assigned Friend status and player evidence fresh during a long demo session', async () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-10-19T16:00:00Z'))
    install()
    await changeJson(`/api/local/profiles/${primary}/start`, 'POST', parseActionResult)
    vi.setSystemTime(new Date('2026-10-19T18:42:00Z'))
    const view = await snapshot()
    expect(view.mode === 'Host' && view.runs[0].playerCountObservedUtc).toBe(new Date().toISOString())
    const companion = await getJson('/api/local/companion', parseCompanionInfo)
    expect(companion.devices[0].lastHeartbeatUtc).toBe(new Date().toISOString())
    await mode('friend')
    const friend = await getJson('/api/local/snapshot', parseFriendSnapshot)
    expect(friend.lastConnectedUtc).toBe(new Date().toISOString())
    expect(realFetch).not.toHaveBeenCalled()
  })

  it('keeps an offline owner separate from verified-copy hosting readiness', async () => {
    install().reset('friend-offline')
    expect(await snapshot()).toMatchObject({ mode: 'Friend', state: 'Disconnected', lastConnectedUtc: null })
    expect(await hosting()).toMatchObject({ allowed: true, consented: true, readyForManualStart: false })
    expect(await prepare()).toMatchObject({ readyForManualStart: true, running: false })
    expect(realFetch).not.toHaveBeenCalled()
  })

  it('respects cancellation before changing fake state and rejects malformed bodies', async () => {
    install()
    const abort = new AbortController(); abort.abort()
    await expect(fetch('/api/local/mode/friend', { method: 'POST', signal: abort.signal })).rejects.toMatchObject({ name: 'AbortError' })
    expect((await snapshot()).mode).toBe('Host')
    for (const body of ['{', '[]', 'null', 'x'.repeat(65537)])
      expect((await fetch('/api/local/mode/friend', { method: 'POST', body })).status).toBe(400)
    expect((await snapshot()).mode).toBe('Host')
    expect(realFetch).not.toHaveBeenCalled()
  })
})

it('runs the actual Join preparation and explicit Start controls entirely through fake data', async () => {
  install().reset('friend-hosting')
  const openHost = vi.fn()
  render(<FriendLocalHosting profileId={primary} worldName="Meadow friends · DEMO" available onOpenHost={openHost} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const dialog = screen.getByRole('dialog', { name: 'Host on this PC' })
  expect(within(dialog).getByRole('button', { name: 'Prepare this PC' })).toBeDisabled()
  fireEvent.click(within(dialog).getByRole('checkbox', { name: /Keep this save on my PC/ }))
  fireEvent.click(within(dialog).getByRole('button', { name: 'Prepare this PC' }))
  const start = await within(dialog).findByRole('button', { name: 'Start server on this PC' })
  expect(await hosting()).toMatchObject({ readyForManualStart: true, running: false })
  expect(openHost).not.toHaveBeenCalled()
  fireEvent.click(start)
  await waitFor(() => expect(openHost).toHaveBeenCalledExactlyOnceWith(local))
  expect((await hosting()).running).toBe(true)
  expect(realFetch).not.toHaveBeenCalled()
})

it('lets the actual missing-install flow select only a fake server file and prepare without installation', async () => {
  install().reset('friend-missing-server')
  render(<FriendLocalHosting profileId={primary} worldName="Meadow friends · DEMO" available onOpenHost={vi.fn()}
    onBrowseServerFile={async game => {
      const response = await changeJson('/api/local/shared-worlds/browse-server-file', 'POST', value => value as { path: string }, { game })
      return response.path
    }} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Host on this PC' }))
  const dialog = screen.getByRole('dialog', { name: 'Host on this PC' })
  fireEvent.click(within(dialog).getByRole('checkbox', { name: /Keep this save on my PC/ }))
  expect(within(dialog).getByRole('button', { name: 'Prepare this PC' })).toBeDisabled()
  fireEvent.click(within(dialog).getByRole('button', { name: 'Browse' }))
  await waitFor(() => expect(within(dialog).getByRole('textbox', { name: 'Installed server file' })).toHaveValue('C:\\TogetherServer-DEMO-FAKE-DATA\\Valheim\\valheim_server.exe'))
  fireEvent.change(within(dialog).getByLabelText('New local game password'), { target: { value: 'demo-only' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Prepare this PC' }))
  expect(await within(dialog).findByRole('button', { name: 'Start server on this PC' })).toBeEnabled()
  expect((await hosting()).running).toBe(false)
  expect(realFetch).not.toHaveBeenCalled()
})

it('keeps its fake-data banner visible, updates the scene, and remounts consumers only on explicit reset', async () => {
  install()
  const revisions: number[] = []
  function Consumer() { const revision = useDevelopmentDemoRevision(); revisions.push(revision); return <DevelopmentDemoControls /> }
  render(<Consumer />)
  expect(screen.getByText('DEMO · FAKE DATA')).toBeVisible()
  expect(screen.queryByRole('button', { name: /dismiss|close/i })).not.toBeInTheDocument()
  const initial = revisions.at(-1)
  await snapshot()
  expect(revisions.at(-1)).toBe(initial)
  fireEvent.change(screen.getByRole('combobox', { name: 'Demo scene' }), { target: { value: 'friend-offline' } })
  expect((await snapshot()).mode).toBe('Friend')
  expect(revisions.at(-1)).toBeGreaterThan(initial!)
  const beforeReset = revisions.at(-1)
  fireEvent.click(screen.getByRole('button', { name: 'Reset fake data' }))
  expect(revisions.at(-1)).toBeGreaterThan(beforeReset!)
  act(() => controller?.uninstall())
  expect(screen.queryByText('DEMO · FAKE DATA')).not.toBeInTheDocument()
  expect(fetch).toBe(realFetch)
})
