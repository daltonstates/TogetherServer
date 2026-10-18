import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { GameSettingsPanel } from './GameSettingsPanel'
import type { EditorDraftGuard, EditorDraftGuardChange } from './editorProtectedDraft'
import { clearProtectedDraft, readProtectedDraft, saveProtectedDraft } from './protectedUiDrafts'

vi.mock('./protectedUiDrafts', () => ({ readProtectedDraft: vi.fn(), saveProtectedDraft: vi.fn(), clearProtectedDraft: vi.fn() }))
beforeEach(() => {
  vi.mocked(readProtectedDraft).mockReset().mockResolvedValue({ ok: true, text: null, revision: 4, message: '' })
  vi.mocked(saveProtectedDraft).mockReset().mockImplementation(async (_identity, text, revision) => ({ ok: true, text, revision: revision + 1, message: '' }))
  vi.mocked(clearProtectedDraft).mockReset().mockImplementation(async (_identity, revision) => ({ ok: true, text: null, revision: revision + 1, message: '' }))
})

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

async function selectPlayers(key = 'allow-list') {
  const navigation = await screen.findByRole('navigation', { name: 'Game settings sections' })
  const players = within(navigation).getByRole('button', { name: /^Players/ })
  if (!players.hasAttribute('aria-current')) fireEvent.click(players)
  const picker = await screen.findByRole('combobox', { name: 'Player list' })
  await waitFor(() => expect(picker).toBeEnabled())
  if ((picker as HTMLSelectElement).value !== key) fireEvent.change(picker, { target: { value: key } })
  await waitFor(() => expect(picker).toBeEnabled())
}

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
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
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
  fireEvent.click(within(screen.getByText('Maximum players must be a whole number from 1 to 200.')).getByRole('button', { name: 'Dismiss notification' }))
  expect(screen.queryByText('Maximum players must be a whole number from 1 to 200.')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Review settings changes' })).toBeDisabled()
  fireEvent.change(screen.getByLabelText('Maximum players'), { target: { value: '10' } })
  fireEvent.click(screen.getByRole('button', { name: 'Review settings changes' }))
  expect(await screen.findByText('The file changed. Reload before saving.')).toBeInTheDocument()
  expect(screen.getByLabelText('Maximum players')).toHaveValue(10)
  expect(screen.queryByRole('button', { name: 'Save with checkpoint' })).not.toBeInTheDocument()
  fireEvent.click(within(screen.getByText('The file changed. Reload before saving.')).getByRole('button', { name: 'Dismiss notification' }))
  expect(screen.getByLabelText('Maximum players')).toHaveValue(10)
  expect(screen.queryByRole('button', { name: 'Save with checkpoint' })).not.toBeInTheDocument()
  expect(fetcher).not.toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ method: 'PUT' }))
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
  await selectPlayers('permit-list')
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

it('preserves a player draft across tabs and rejects a duplicate Bedrock name without a request', async () => {
  const listView = { ok: true, code: 'AccessListReady', message: 'Next Start', kind: 'MinecraftBedrock', key: 'allow-list',
    label: 'Allowed players', sha256: beforeSha, entries: [{ identity: null, name: 'Test Gamer', ignoresPlayerLimit: false }], canUndo: false }
  const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/allow-list') ? listView : {
    ...baseView, kind: 'MinecraftBedrock', lists: [{ key: 'allow-list', label: 'Allowed players', available: true }] }))
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  await selectPlayers()
  fireEvent.change(await screen.findByLabelText('Xbox gamertag'), { target: { value: 'test gamer' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add player' }))
  expect(screen.getByText('Remove the repeated player name.')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Remove Test Gamer' }))
  fireEvent.click(screen.getByRole('button', { name: 'General' }))
  expect(screen.queryByRole('button', { name: 'Review list changes' })).not.toBeInTheDocument()
  await selectPlayers()
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

it('searches descriptions without losing hidden edits and allows canceling a dirty reload', async () => {
  const fetcher = vi.fn(async () => json(baseView))
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  fireEvent.change(await screen.findByLabelText('Difficulty'), { target: { value: 'hard' } })
  fireEvent.change(screen.getByLabelText('Search game settings'), { target: { value: 'capacity' } })
  expect(screen.queryByLabelText('Difficulty')).not.toBeInTheDocument()
  expect(screen.getByLabelText('Maximum players')).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Search game settings'), { target: { value: '' } })
  expect(screen.getByLabelText('Difficulty')).toHaveValue('hard')
  fireEvent.click(screen.getByRole('button', { name: 'Reload game settings' }))
  const dialog = await screen.findByRole('dialog', { name: 'Reload game settings?' })
  expect(dialog).toHaveTextContent('Reloading replaces unsaved settings and player lists. Keep their protected drafts and reload?')
  expect(fetcher).toHaveBeenCalledOnce()
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(screen.getByLabelText('Difficulty')).toHaveValue('hard')
  expect(fetcher).toHaveBeenCalledOnce()
})

it('keeps the protected settings draft before an accepted reload', async () => {
  const fetcher = vi.fn(async () => json(baseView))
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  fireEvent.change(await screen.findByLabelText('Difficulty'), { target: { value: 'hard' } })
  fireEvent.click(screen.getByRole('button', { name: 'Reload game settings' }))
  const dialog = await screen.findByRole('dialog', { name: 'Reload game settings?' })
  expect(fetcher).toHaveBeenCalledOnce()
  fireEvent.click(screen.getByRole('button', { name: 'Reload game settings' }))
  fireEvent.click(within(dialog).getByRole('button', { name: 'Keep drafts and reload' }))
  await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2))
  expect(saveProtectedDraft).toHaveBeenCalledWith(expect.objectContaining({ purpose: 'settings', profileId: props.profileId }),
    expect.stringContaining('"difficulty":"hard"'), expect.any(Number))
  expect(screen.getByLabelText('Difficulty')).toHaveValue('normal')
  expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
})

it('declines settings discard without changing the draft, then discards only after acceptance', async () => {
  const fetcher = vi.fn(async () => json(baseView))
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  fireEvent.change(await screen.findByLabelText('Difficulty'), { target: { value: 'hard' } })
  fireEvent.click(screen.getByRole('button', { name: 'Discard unsaved settings' }))
  let dialog = await screen.findByRole('dialog', { name: 'Discard unsaved settings?' })
  expect(dialog).toHaveTextContent('Discard these unsaved settings? The current saved settings stay unchanged.')
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(screen.getByLabelText('Difficulty')).toHaveValue('hard')
  expect(clearProtectedDraft).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: 'Discard unsaved settings' }))
  dialog = await screen.findByRole('dialog', { name: 'Discard unsaved settings?' })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Discard settings' }))
  await waitFor(() => expect(screen.getByLabelText('Difficulty')).toHaveValue('normal'))
  await waitFor(() => expect(clearProtectedDraft).toHaveBeenCalledOnce())
  expect(fetcher).toHaveBeenCalledOnce()
})

it('rejects an accepted settings reload when recovery becomes blocked during confirmation', async () => {
  const fetcher = vi.fn(async () => json(baseView))
  vi.stubGlobal('fetch', fetcher)
  const rendered = render(<GameSettingsPanel {...props} />)
  fireEvent.change(await screen.findByLabelText('Difficulty'), { target: { value: 'hard' } })
  fireEvent.click(screen.getByRole('button', { name: 'Reload game settings' }))
  const dialog = await screen.findByRole('dialog', { name: 'Reload game settings?' })
  rendered.rerender(<GameSettingsPanel {...props} recoveryBlocked />)
  expect(screen.getByRole('dialog')).toBe(dialog)
  rendered.rerender(<GameSettingsPanel {...props} />)
  fireEvent.click(within(dialog).getByRole('button', { name: 'Keep drafts and reload' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(screen.getByLabelText('Difficulty')).toHaveValue('hard')
  expect(fetcher).toHaveBeenCalledOnce()
})

it.each([false, true])('does not reload captured lists after child content changes during the parent decision (already dirty: %s)', async alreadyDirty => {
  const listView = { ok: true, code: 'AccessListReady', message: 'Ready', kind: 'MinecraftJava', key: 'allow-list',
    label: 'Allowed players', sha256: beforeSha, entries: [], canUndo: false }
  const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/allow-list') ? listView : {
    ...baseView, lists: [{ key: 'allow-list', label: 'Allowed players', available: true }] }))
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  await selectPlayers()
  await screen.findByLabelText('Java player name')
  if (alreadyDirty) fireEvent.change(screen.getByLabelText('Java player name'), { target: { value: 'OldPlayer' } })
  fireEvent.change(screen.getByLabelText('Difficulty'), { target: { value: 'hard' } })
  fireEvent.click(screen.getByRole('button', { name: 'Reload game settings' }))
  const dialog = await screen.findByRole('dialog', { name: 'Reload game settings?' })
  fireEvent.change(screen.getByLabelText('Java player name'), { target: { value: 'NewPlayer' } })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Keep drafts and reload' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(screen.getByLabelText('Difficulty')).toHaveValue('hard')
  expect(screen.getByLabelText('Java player name')).toHaveValue('NewPlayer')
  expect(fetcher).toHaveBeenCalledTimes(2)
})

it('keeps an unchanged reload decision valid while a player-list draft finishes autosaving', async () => {
  const listView = { ok: true, code: 'AccessListReady', message: 'Ready', kind: 'MinecraftJava', key: 'allow-list',
    label: 'Allowed players', sha256: beforeSha, entries: [], canUndo: false }
  const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/allow-list') ? listView : {
    ...baseView, lists: [{ key: 'allow-list', label: 'Allowed players', available: true }] }))
  vi.stubGlobal('fetch', fetcher)
  let finishListSave: (() => void) | undefined
  vi.mocked(saveProtectedDraft).mockImplementation((identity, text, revision) => identity.purpose === 'list'
    ? new Promise(resolve => { finishListSave = () => resolve({ ok: true, text, revision: revision + 1, message: '' }) })
    : Promise.resolve({ ok: true, text, revision: revision + 1, message: '' }))
  render(<GameSettingsPanel {...props} />)
  await selectPlayers()
  fireEvent.change(await screen.findByLabelText('Java player name'), { target: { value: 'KeptPlayer' } })
  fireEvent.change(screen.getByLabelText('Difficulty'), { target: { value: 'hard' } })
  fireEvent.click(screen.getByRole('button', { name: 'Reload game settings' }))
  const dialog = await screen.findByRole('dialog', { name: 'Reload game settings?' })
  await waitFor(() => expect(finishListSave).toBeTypeOf('function'))
  await act(async () => { finishListSave!() })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Keep drafts and reload' }))
  await waitFor(() => expect(screen.getByLabelText('Difficulty')).toHaveValue('normal'))
  expect(fetcher.mock.calls.filter(([input]) => String(input).endsWith('/game-settings'))).toHaveLength(2)
  expect(vi.mocked(saveProtectedDraft).mock.calls.some(([identity, text]) => identity.purpose === 'list' && text.includes('KeptPlayer'))).toBe(true)
})

it('owns a persistent list confirmation and refuses stale decisions after choosing another list', async () => {
  const listView = { ok: true, code: 'AccessListReady', message: 'Ready', kind: 'Valheim', key: 'permit-list',
    label: 'Permitted players', sha256: beforeSha, entries: [], canUndo: false }
  const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/permit-list') ? listView : String(input).endsWith('/ban-list') ? { ...listView, key: 'ban-list', label: 'Banned players' } : {
    ...baseView, kind: 'Valheim', settings: null, sha256: null,
    lists: [{ key: 'permit-list', label: 'Permitted players', available: true }, { key: 'ban-list', label: 'Banned players', available: true }] }))
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  await selectPlayers('permit-list')
  fireEvent.change(within(screen.getByRole('region', { name: 'Permitted players' })).getByLabelText('Platform user ID'), { target: { value: 'Steam_222' } })
  fireEvent.click(screen.getByRole('button', { name: 'Reload this list' }))
  let dialog = await screen.findByRole('dialog', { name: 'Reload player list?' })
  expect(dialog).toHaveTextContent('Reloading replaces this unsaved player list. Keep the protected draft and reload?')
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(fetcher).toHaveBeenCalledTimes(2)
  expect(within(screen.getByRole('region', { name: 'Permitted players' })).getByLabelText('Platform user ID')).toHaveValue('Steam_222')
  fireEvent.click(screen.getByRole('button', { name: 'Discard unsaved list edits' }))
  dialog = await screen.findByRole('dialog', { name: 'Discard player list edits?' })
  expect(dialog).toHaveTextContent('Discard this unsaved player list and unfinished entries? The saved list stays unchanged.')
  fireEvent.change(screen.getByRole('combobox', { name: 'Player list' }), { target: { value: 'ban-list' } })
  expect(screen.getByRole('dialog')).toBe(dialog)
  fireEvent.click(within(dialog).getByRole('button', { name: 'Discard list edits' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  await selectPlayers('permit-list')
  expect(within(screen.getByRole('region', { name: 'Permitted players' })).getByLabelText('Platform user ID')).toHaveValue('Steam_222')
  expect(clearProtectedDraft).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: 'Discard unsaved list edits' }))
  fireEvent.click(within(await screen.findByRole('dialog', { name: 'Discard player list edits?' })).getByRole('button', { name: 'Discard list edits' }))
  await waitFor(() => expect(within(screen.getByRole('region', { name: 'Permitted players' })).getByLabelText('Platform user ID')).toHaveValue(''))
  await waitFor(() => expect(clearProtectedDraft).toHaveBeenCalledOnce())
  expect(fetcher).toHaveBeenCalledTimes(3)
})

it('keeps bulk paste in a reviewed local draft until the exact game-list preview is accepted', async () => {
  let entries: { identity: string; name: null; ignoresPlayerLimit: null }[] = []
  const listView = () => ({ ok: true, code: 'AccessListReady', message: 'Ready', kind: 'Valheim', key: 'permit-list', label: 'Permitted players',
    sha256: beforeSha, entries, canUndo: false })
  const fetcher = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input)
    if (path.endsWith('/preview')) return json({ ...preview, key: 'permit-list', changes: [{ key: 'permit-list', label: 'Add player', before: null, after: 'Steam_222' }] })
    if (init?.method === 'PUT') { entries = JSON.parse(String(init.body)).entries; return json({ ok: true, code: 'FileSaved', message: 'List saved after checkpoint.', key: 'permit-list', sha256: afterSha, canUndo: true }) }
    if (path.endsWith('/permit-list')) return json(listView())
    return json({ ...baseView, kind: 'Valheim', settings: null, sha256: null, lists: [{ key: 'permit-list', label: 'Permitted players', available: true }] })
  })
  vi.stubGlobal('fetch', fetcher)
  const drafts = vi.fn()
  render(<GameSettingsPanel {...props} onDraftStateChange={drafts} />)
  await selectPlayers('permit-list')
  const paste = await screen.findByLabelText('Players to paste')
  paste.closest('details')!.open = true
  fireEvent.change(paste, { target: { value: 'Steam_222' } })
  fireEvent.click(screen.getByRole('button', { name: 'Review pasted players' }))
  expect(screen.queryByRole('button', { name: 'Remove Steam_222' })).not.toBeInTheDocument()
  expect(fetcher.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  fireEvent.click(screen.getByRole('button', { name: 'Add reviewed players to draft' }))
  expect(screen.getByRole('button', { name: 'Remove Steam_222' })).toBeInTheDocument()
  expect(drafts).toHaveBeenCalledWith(`list:${props.profileId}:permit-list`, true)
  fireEvent.change(screen.getByLabelText('Search permitted players'), { target: { value: 'not-present' } })
  expect(screen.queryByRole('button', { name: 'Remove Steam_222' })).not.toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Search permitted players'), { target: { value: '' } })
  fireEvent.click(screen.getByRole('button', { name: 'Review list changes' }))
  await screen.findByRole('region', { name: 'Review exact changes' })
  expect(fetcher.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  fireEvent.click(screen.getByRole('button', { name: 'Save with checkpoint' }))
  await waitFor(() => expect(fetcher).toHaveBeenCalledWith(expect.stringContaining('/permit-list'), expect.objectContaining({ method: 'PUT',
    body: JSON.stringify({ expectedSha256: beforeSha, entries: [{ identity: 'Steam_222', name: null, ignoresPlayerLimit: null }] }) })))
})

it('requires explicit draft recovery and leaves live settings unchanged until the review and save', async () => {
  vi.mocked(readProtectedDraft).mockResolvedValue({ ok: true, revision: 10, message: '', text: JSON.stringify({ version: 1, kind: 'MinecraftJava', sha256: 'old',
    settings: { ...values, difficulty: 'hard' }, maximumPlayers: '20' }) })
  vi.stubGlobal('fetch', vi.fn(async () => json(baseView)))
  render(<GameSettingsPanel {...props} />)
  await screen.findByRole('button', { name: 'Review recovered draft' })
  expect(screen.getByLabelText('Difficulty')).toHaveValue('normal')
  expect(screen.getByLabelText('Difficulty')).toBeDisabled()
  fireEvent.click(screen.getByRole('button', { name: 'Review recovered draft' }))
  expect(screen.getByLabelText('Difficulty')).toHaveValue('hard')
  expect(screen.getByText(/saved file changed since this draft/)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Save with checkpoint' })).not.toBeInTheDocument()
})

it('keeps general edits and unfinished player entries while changing sections', async () => {
  const listView = { ok: true, code: 'AccessListReady', message: 'Ready', kind: 'MinecraftJava', key: 'allow-list',
    label: 'Allowed players', sha256: beforeSha, entries: [], canUndo: false }
  const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/allow-list') ? listView : {
    ...baseView, lists: [{ key: 'allow-list', label: 'Allowed players', available: true }] }))
  vi.stubGlobal('fetch', fetcher)
  const drafts = vi.fn()
  render(<GameSettingsPanel {...props} onDraftStateChange={drafts} />)
  fireEvent.change(await screen.findByLabelText('Difficulty'), { target: { value: 'hard' } })
  expect(screen.queryByRole('region', { name: 'Allowed players' })).not.toBeInTheDocument()
  await selectPlayers()
  fireEvent.change(await screen.findByLabelText('Java player name'), { target: { value: 'KeptPlayer' } })
  expect(screen.getByRole('button', { name: /^Players/ })).toHaveAttribute('aria-current', 'page')
  fireEvent.click(screen.getByRole('button', { name: /^General/ }))
  expect(screen.getByLabelText('Difficulty')).toHaveValue('hard')
  expect(screen.queryByRole('region', { name: 'Allowed players' })).not.toBeInTheDocument()
  await selectPlayers()
  expect(screen.getByLabelText('Java player name')).toHaveValue('KeptPlayer')
  expect(drafts).toHaveBeenCalledWith(`settings:${props.profileId}`, true)
  expect(drafts).toHaveBeenCalledWith(`list:${props.profileId}:allow-list`, true)
  expect(fetcher).toHaveBeenCalledTimes(2)
})

it('shows one player list, keeps other-list errors visible, and blocks navigation while loading', async () => {
  const permit = { ok: true, code: 'AccessListReady', message: 'Ready', kind: 'Valheim', key: 'permit-list',
    label: 'Permitted players', sha256: beforeSha, entries: [], canUndo: false }
  let finishBanned: ((value: Response) => void) | undefined
  const fetcher = vi.fn(async (input: RequestInfo | URL) => {
    if (String(input).endsWith('/permit-list')) return json(permit)
    if (String(input).endsWith('/ban-list')) return new Promise<Response>(resolve => { finishBanned = resolve })
    return json({ ...baseView, kind: 'Valheim', settings: null, sha256: null, lists: [
      { key: 'permit-list', label: 'Permitted players', available: true }, { key: 'ban-list', label: 'Banned players', available: true }] })
  })
  vi.stubGlobal('fetch', fetcher)
  render(<GameSettingsPanel {...props} />)
  await selectPlayers('permit-list')
  fireEvent.change(await screen.findByLabelText('Platform user ID'), { target: { value: 'invalid' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add player' }))
  expect(screen.getByRole('status')).toHaveTextContent('Enter the case-sensitive Platform_UserID')
  fireEvent.change(screen.getByRole('combobox', { name: 'Player list' }), { target: { value: 'ban-list' } })
  await waitFor(() => expect(finishBanned).toBeTypeOf('function'))
  expect(screen.getByRole('combobox', { name: 'Player list' })).toBeDisabled()
  expect(screen.getByRole('button', { name: /^Players/ })).toBeDisabled()
  expect(screen.getByRole('button', { name: 'Reload game settings' })).toBeDisabled()
  expect(screen.queryByRole('region', { name: 'Permitted players' })).not.toBeInTheDocument()
  expect(screen.getByRole('region', { name: 'Banned players' })).toBeVisible()
  expect(screen.getByRole('status')).toHaveTextContent('Enter the case-sensitive Platform_UserID')
  await act(async () => finishBanned?.(json({ ...permit, key: 'ban-list', label: 'Banned players' })))
  fireEvent.click(screen.getByRole('button', { name: 'Review' }))
  expect(screen.getByRole('region', { name: 'Permitted players' })).toBeVisible()
  expect(within(screen.getByRole('region', { name: 'Permitted players' })).getByLabelText('Platform user ID')).toHaveValue('invalid')
  expect(screen.queryByRole('region', { name: 'Banned players' })).not.toBeInTheDocument()
  expect(fetcher).toHaveBeenCalledTimes(3)
})

it('reveals General when its hidden draft cannot be kept before leaving', async () => {
  const listView = { ok: true, code: 'AccessListReady', message: 'Ready', kind: 'MinecraftJava', key: 'allow-list',
    label: 'Allowed players', sha256: beforeSha, entries: [], canUndo: false }
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/allow-list') ? listView : {
    ...baseView, lists: [{ key: 'allow-list', label: 'Allowed players', available: true }] })))
  vi.mocked(saveProtectedDraft).mockResolvedValue({ ok: false, text: null, revision: 5, message: 'Cannot keep this settings draft.' })
  const guards = new Map<string, EditorDraftGuard>()
  const keepGuard: EditorDraftGuardChange = (source, guard) => { if (guard) guards.set(source, guard); else guards.delete(source) }
  render(<GameSettingsPanel {...props} onDraftGuardChange={keepGuard} />)
  const difficulty = await screen.findByLabelText('Difficulty')
  fireEvent.change(difficulty, { target: { value: 'hard' } })
  await selectPlayers()
  expect(difficulty).not.toBeVisible()
  let saved = true
  await act(async () => { saved = await guards.get(`settings:${props.profileId}`)!.flush() })
  expect(saved).toBe(false)
  expect(screen.getByRole('button', { name: /^General/ })).toHaveAttribute('aria-current', 'page')
  expect(difficulty).toBeVisible()
  expect(difficulty).toHaveValue('hard')
  expect(screen.getByText('Cannot keep this settings draft.')).toBeVisible()
})

it('reveals the exact player list when its hidden draft cannot be kept before leaving', async () => {
  const permit = { ok: true, code: 'AccessListReady', message: 'Ready', kind: 'Valheim', key: 'permit-list',
    label: 'Permitted players', sha256: beforeSha, entries: [], canUndo: false }
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/permit-list') ? permit :
    String(input).endsWith('/ban-list') ? { ...permit, key: 'ban-list', label: 'Banned players' } : {
      ...baseView, kind: 'Valheim', settings: null, sha256: null, lists: [
        { key: 'permit-list', label: 'Permitted players', available: true }, { key: 'ban-list', label: 'Banned players', available: true }] })))
  vi.mocked(saveProtectedDraft).mockResolvedValue({ ok: false, text: null, revision: 5, message: 'Cannot keep this player draft.' })
  const guards = new Map<string, EditorDraftGuard>()
  const keepGuard: EditorDraftGuardChange = (source, guard) => { if (guard) guards.set(source, guard); else guards.delete(source) }
  render(<GameSettingsPanel {...props} onDraftGuardChange={keepGuard} />)
  await selectPlayers('permit-list')
  const identity = within(screen.getByRole('region', { name: 'Permitted players' })).getByLabelText('Platform user ID')
  fireEvent.change(identity, { target: { value: 'Steam_222' } })
  await selectPlayers('ban-list')
  expect(identity).not.toBeVisible()
  let saved = true
  await act(async () => { saved = await guards.get(`list:${props.profileId}:permit-list`)!.flush() })
  expect(saved).toBe(false)
  expect(screen.getByRole('combobox', { name: 'Player list' })).toHaveValue('permit-list')
  expect(identity).toBeVisible()
  expect(identity).toHaveValue('Steam_222')
  expect(screen.getByText('Cannot keep this player draft.')).toBeVisible()
})

it.each(['unvisited players', 'unselected Valheim lists', 'unsupported game'])('keeps unchanged %s out of the Start-blocking draft guards', async scenario => {
  const valheim = scenario === 'unselected Valheim lists'
  const unsupported = scenario === 'unsupported game'
  const view = unsupported ? { ...baseView, ok: false, kind: 'Custom', code: 'GameSettingsUnsupported',
    message: 'Use its saved setup or reviewed file editor.', settings: null, sha256: null, lists: [] } : valheim ? {
      ...baseView, kind: 'Valheim', settings: null, sha256: null, lists: [
        { key: 'permit-list', label: 'Permitted players', available: true }, { key: 'admin-list', label: 'Administrators', available: true }] } : {
      ...baseView, lists: [{ key: 'allow-list', label: 'Allowed players', available: true }] }
  const fetcher = vi.fn(async (input: RequestInfo | URL) => json(String(input).endsWith('/permit-list') ? {
    ok: true, code: 'AccessListReady', message: 'Ready', kind: 'Valheim', key: 'permit-list',
    label: 'Permitted players', sha256: beforeSha, entries: [], canUndo: false } : view))
  vi.stubGlobal('fetch', fetcher)
  const guards = new Map<string, EditorDraftGuard>()
  const keepGuard: EditorDraftGuardChange = (source, guard) => { if (guard) guards.set(source, guard); else guards.delete(source) }
  render(<GameSettingsPanel {...props} onDraftGuardChange={keepGuard} />)
  if (unsupported) await screen.findByText('Use its saved setup or reviewed file editor.')
  else if (valheim) await screen.findByLabelText('Platform user ID')
  else await screen.findByLabelText('Difficulty')
  await waitFor(() => {
    expect(guards.size).toBe(unsupported ? 1 : valheim ? 3 : 2)
    expect([...guards.values()].every(guard => !guard.dirty && !guard.saving)).toBe(true)
  })
  const results = await Promise.all([...guards.values()].map(guard => guard.flush()))
  expect(results.every(Boolean)).toBe(true)
  expect(fetcher).toHaveBeenCalledTimes(valheim ? 2 : 1)
  expect(saveProtectedDraft).not.toHaveBeenCalled()
})
