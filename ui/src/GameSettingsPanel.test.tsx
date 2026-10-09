import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { GameSettingsPanel } from './GameSettingsPanel'

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })
const beforeSha = 'A'.repeat(64)
const afterSha = 'B'.repeat(64)
const values = { difficulty: 'normal', maximumPlayers: 20, gameMode: 'survival', allowListEnabled: false, forceGameMode: false }
const baseView = { ok: true, code: 'GameSettingsReady', message: 'Next Start', kind: 'MinecraftJava',
  sha256: beforeSha, settings: values, canUndo: false, lists: [] }
const props = { profileId: '11111111-1111-4111-8111-111111111111', state: 'Offline', maintenance: true,
  busy: false, recoveryBlocked: false, onPrepareMaintenance: vi.fn() }
const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } })
const preview = { ok: true, code: 'SettingsPreviewReady', message: 'Review', key: 'server-properties',
  expectedSha256: beforeSha, proposedSha256: afterSha,
  changes: [{ key: 'difficulty', label: 'Difficulty', before: 'difficulty=normal', after: 'difficulty=hard' }] }

it('requires Offline maintenance, keeps difficulty visible, and exposes the maintenance guide', async () => {
  const fetcher = vi.fn(async () => json(baseView))
  vi.stubGlobal('fetch', fetcher)
  const guide = vi.fn()
  const rendered = render(<GameSettingsPanel {...props} maintenance={false} state="Ready" onPrepareMaintenance={guide} />)
  expect(await screen.findByLabelText('Difficulty')).toBeDisabled()
  expect(screen.getByRole('button', { name: 'Review settings changes' })).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Open maintenance guide' }))
  expect(guide).toHaveBeenCalledOnce()
  rendered.rerender(<GameSettingsPanel {...props} onPrepareMaintenance={guide} />)
  expect(screen.getByLabelText('Difficulty')).toBeEnabled()
  rendered.rerender(<GameSettingsPanel {...props} recoveryBlocked />)
  expect(screen.getByLabelText('Difficulty')).toBeDisabled()
  expect(fetcher).toHaveBeenCalledOnce()
})

it('previews exact lines, sends only reviewed values, refreshes canonical state, and offers one-step Undo', async () => {
  let saved = false
  const fetcher = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input)
    if (path.endsWith('/preview')) return json(preview)
    if (path.endsWith('/undo')) {
      saved = false
      return json({ ok: true, code: 'FileRestored', message: 'Restored after checkpoint.', key: 'server-properties', sha256: beforeSha, canUndo: false })
    }
    if (init?.method === 'PUT') {
      saved = true
      return json({ ok: true, code: 'FileSaved', message: 'Saved after checkpoint. Check the game.', key: 'server-properties', sha256: afterSha, canUndo: true })
    }
    return json(saved ? { ...baseView, sha256: afterSha, settings: { ...values, difficulty: 'hard' }, canUndo: true } : baseView)
  })
  vi.stubGlobal('fetch', fetcher)
  const confirm = vi.spyOn(window, 'confirm')
  render(<GameSettingsPanel {...props} />)
  fireEvent.change(await screen.findByLabelText('Difficulty'), { target: { value: 'hard' } })
  expect(screen.queryByRole('button', { name: 'Save with checkpoint' })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Review settings changes' }))
  expect(await screen.findByText('difficulty=normal')).toBeInTheDocument()
  expect(screen.getByText('difficulty=hard')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Save with checkpoint' }))
  await waitFor(() => expect(screen.getByRole('button', { name: 'Undo last settings change' })).toBeEnabled())
  const put = fetcher.mock.calls.find(([, init]) => init?.method === 'PUT')
  expect(JSON.parse(String(put?.[1]?.body))).toEqual({ expectedSha256: beforeSha, settings: { ...values, difficulty: 'hard' } })
  expect(put?.[1]?.headers).toMatchObject({ 'X-TogetherServer-Local': '1' })
  expect(screen.getByText(/Saved after checkpoint/).classList).not.toContain('good')
  fireEvent.click(screen.getByRole('button', { name: 'Undo last settings change' }))
  await waitFor(() => expect(screen.getByLabelText('Difficulty')).toHaveValue('normal'))
  expect(fetcher).toHaveBeenCalledWith(expect.stringContaining('/game-settings/undo'), expect.objectContaining({
    method: 'POST', body: JSON.stringify({ expectedSha256: afterSha }) }))
  expect(confirm).not.toHaveBeenCalled()
})

it('invalidates a review after another edit and blocks Save if the server resumes', async () => {
  const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/preview') ? preview : baseView))
  vi.stubGlobal('fetch', fetcher)
  const rendered = render(<GameSettingsPanel {...props} />)
  fireEvent.change(await screen.findByLabelText('Difficulty'), { target: { value: 'hard' } })
  fireEvent.click(screen.getByRole('button', { name: 'Review settings changes' }))
  await screen.findByRole('button', { name: 'Save with checkpoint' })
  fireEvent.change(screen.getByLabelText('Maximum players'), { target: { value: '21' } })
  expect(screen.queryByRole('button', { name: 'Save with checkpoint' })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Review settings changes' }))
  await screen.findByRole('button', { name: 'Save with checkpoint' })
  rendered.rerender(<GameSettingsPanel {...props} state="Ready" />)
  expect(screen.getByRole('button', { name: 'Save with checkpoint' })).toBeDisabled()
  expect(fetcher).not.toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ method: 'PUT' }))
})

it('rejects an invalid maximum and keeps a stale-file refusal visible without saving', async () => {
  const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/preview') ? {
    ok: false, code: 'FileChanged', message: 'The file changed. Reload before saving.', key: 'server-properties',
    expectedSha256: null, proposedSha256: null, changes: [] } : baseView))
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  fireEvent.change(await screen.findByLabelText('Maximum players'), { target: { value: '201' } })
  expect(screen.getByRole('button', { name: 'Review settings changes' })).toBeDisabled()
  fireEvent.change(screen.getByLabelText('Maximum players'), { target: { value: '10' } })
  fireEvent.click(screen.getByRole('button', { name: 'Review settings changes' }))
  expect(await screen.findByText('The file changed. Reload before saving.')).toBeInTheDocument()
  expect(screen.getByLabelText('Maximum players')).toHaveValue(10)
  expect(screen.queryByRole('button', { name: 'Save with checkpoint' })).not.toBeInTheDocument()
})

it('limits Bedrock modes and requires allowed players through the edition-specific switch', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => json({ ...baseView, kind: 'MinecraftBedrock' })))
  render(<GameSettingsPanel {...props} />)
  await screen.findByLabelText('Game mode')
  expect(screen.queryByRole('option', { name: 'Spectator' })).not.toBeInTheDocument()
  expect(screen.getByLabelText('Require allowed players')).toBeEnabled()
  expect(screen.getByLabelText('Apply game mode when players join')).toBeEnabled()
})

it('adds and removes real typed Valheim IDs with the non-empty permitted-list restriction visible', async () => {
  const entry = { identity: 'Steam_111', name: null, ignoresPlayerLimit: null }
  const listView = { ok: true, code: 'AccessListReady', message: 'Next Start', kind: 'Valheim', key: 'permit-list',
    label: 'Permitted players', sha256: beforeSha, entries: [entry], canUndo: false }
  const fetcher = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input)
    if (path.endsWith('/preview')) return json({ ...preview, key: 'permit-list', changes: [
      { key: 'permit-list', label: 'Remove player', before: 'Steam_111', after: null },
      { key: 'permit-list', label: 'Add or update player', before: null, after: 'Steam_222' }] })
    if (init?.method === 'PUT') return json({ ok: true, code: 'FileSaved', message: 'List saved after checkpoint.',
      key: 'permit-list', sha256: afterSha, canUndo: true })
    if (path.endsWith('/permit-list')) return json(listView)
    return json({ ...baseView, kind: 'Valheim', settings: null, sha256: null,
      lists: [{ key: 'permit-list', label: 'Permitted players', available: true }] })
  })
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Manage permitted players' }))
  await screen.findByText('Steam_111')
  expect(screen.getByText(/non-empty permitted list restricts joining/)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Remove Steam_111' }))
  fireEvent.change(screen.getByLabelText('Platform user ID'), { target: { value: 'Steam_222' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add player' }))
  fireEvent.click(screen.getByRole('button', { name: 'Review list changes' }))
  await screen.findByRole('region', { name: 'Review exact changes' })
  fireEvent.click(screen.getByRole('button', { name: 'Save with checkpoint' }))
  await screen.findByText('List saved after checkpoint.')
  const put = fetcher.mock.calls.find(([, init]) => init?.method === 'PUT')
  expect(JSON.parse(String(put?.[1]?.body))).toEqual({ expectedSha256: beforeSha,
    entries: [{ identity: 'Steam_222', name: null, ignoresPlayerLimit: null }] })
})

it('preserves a collapsed list draft and rejects a duplicate Bedrock name without a request', async () => {
  const listView = { ok: true, code: 'AccessListReady', message: 'Next Start', kind: 'MinecraftBedrock', key: 'allow-list',
    label: 'Allowed players', sha256: beforeSha, entries: [{ identity: null, name: 'Test Gamer', ignoresPlayerLimit: false }], canUndo: false }
  const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/allow-list') ? listView : {
    ...baseView, kind: 'MinecraftBedrock', lists: [{ key: 'allow-list', label: 'Allowed players', available: true }] }))
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Manage allowed players' }))
  fireEvent.change(await screen.findByLabelText('Xbox gamertag'), { target: { value: 'test gamer' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add player' }))
  expect(screen.getByText('Remove the repeated player name.')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Remove Test Gamer' }))
  fireEvent.click(screen.getByRole('button', { name: 'Close list' }))
  fireEvent.click(screen.getByRole('button', { name: 'Manage allowed players' }))
  expect(await screen.findByText('No players in this list.')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Review list changes' })).toBeEnabled()
  expect(fetcher).toHaveBeenCalledTimes(2)
})

it('shows malformed contracts as retryable errors and keeps unsupported games out of the editor', async () => {
  let malformed = true
  vi.stubGlobal('fetch', vi.fn(async () => json(malformed ? { ...baseView, settings: { ...values, maximumPlayers: null } } : {
    ...baseView, ok: false, kind: 'Custom', code: 'GameSettingsUnsupported', message: 'Use its saved setup or reviewed file editor.',
    settings: null, sha256: null, lists: [] })))
  render(<GameSettingsPanel {...props} />)
  expect(await screen.findByRole('status')).toHaveTextContent('InvalidResponse')
  expect(screen.queryByLabelText('Difficulty')).not.toBeInTheDocument()
  malformed = false
  fireEvent.click(screen.getByRole('button', { name: 'Reload game settings' }))
  expect(await screen.findByText('Use its saved setup or reviewed file editor.')).toBeInTheDocument()
  expect(within(screen.getByRole('region', { name: 'Simple game settings' })).queryByRole('button', { name: 'Save with checkpoint' })).not.toBeInTheDocument()
})
