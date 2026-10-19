import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useState } from 'react'
import { ProfileAccess, ProfileSettings, profileNameError, useLocalProfile } from './ProfileSettings'
import { ContractError, parseCompanionInfo, parseFriendSnapshot, parseLocalProfile, parseLocalProfileResult } from './contracts'

function Harness() {
  const profile = useLocalProfile()
  const [visible, show] = useState(true)
  return <><ProfileAccess profile={profile} onOpen={() => show(true)} />
    <button onClick={() => show(current => !current)}>Switch page</button>
    {visible && <ProfileSettings profile={profile} />}</>
}
const response = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
afterEach(() => vi.unstubAllGlobals())

describe('shared profile editor', () => {
  it('loads with the local header, keeps a draft across pages, trims save and requires a nonempty name', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(response({ displayName: '' }))
      .mockResolvedValueOnce(response({ ok: true, code: 'Saved', message: 'Saved', displayName: 'Alex' }))
    vi.stubGlobal('fetch', fetch)
    render(<Harness />)
    await waitFor(() => expect(screen.getByLabelText('Your name')).toBeEnabled())
    expect(fetch.mock.calls[0][0]).toBe('/api/local/profile')
    expect(fetch.mock.calls[0][1].headers).toEqual({ 'X-TogetherServer-Local': '1' })
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: '  Alex  ' } })
    fireEvent.click(screen.getByText('Switch page'))
    fireEvent.click(screen.getByText('Set your name'))
    expect(screen.getByLabelText('Your name')).toHaveValue('  Alex  ')
    fireEvent.click(screen.getByText('Save name'))
    await screen.findByText('Name saved and shared with connected PCs.')
    expect(fetch.mock.calls[1][1]).toMatchObject({ method: 'PUT', body: '{"displayName":"Alex"}', headers: { 'X-TogetherServer-Local': '1' } })
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: '' } })
    expect(screen.getByText('Save name')).toBeDisabled()
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a name your friends recognize.')
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('preserves failed drafts, retries load and shows pending state until canonical save completes', async () => {
    let complete!: (response: Response) => void
    const fetch = vi.fn().mockResolvedValueOnce(response({ message: 'Unavailable' }, 503))
      .mockResolvedValueOnce(response({ displayName: 'Old name' }))
      .mockResolvedValueOnce(response({ message: 'Please retry' }, 500))
      .mockImplementationOnce(() => new Promise<Response>(resolve => { complete = resolve }))
    vi.stubGlobal('fetch', fetch)
    render(<Harness />)
    fireEvent.click(await screen.findByText('Try again'))
    await waitFor(() => expect(screen.getByLabelText('Your name')).toHaveValue('Old name'))
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'New name' } })
    fireEvent.click(screen.getByText('Save name'))
    await screen.findByRole('alert')
    expect(screen.getByLabelText('Your name')).toHaveValue('New name')
    fireEvent.click(screen.getByText('Save name'))
    expect(screen.getByText('Saving…')).toBeDisabled()
    expect(screen.getByLabelText('Your name')).toBeDisabled()
    complete(response({ ok: true, code: 'Saved', message: 'Saved', displayName: 'New name' }))
    await screen.findByText('Name saved and shared with connected PCs.')
  })

  it('bounds UTF-16 length and rejects formatting, controls and line separators', () => {
    for (const value of ['a\nb', 'a\rb', 'a\tb', 'a\u200db', 'a\u2028b', 'a\u2029b', '\ud800', '\udc00', '', ' '.repeat(90), 'x'.repeat(81), '😀'.repeat(41)]) expect(profileNameError(value)).not.toBe('')
    for (const value of ['a'.repeat(80), '😀'.repeat(40), ' Renée ']) expect(profileNameError(value)).toBe('')
  })

  it('prevents invalid submissions and keeps the draft when a typed save is denied', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(response({ displayName: 'Alex' }))
      .mockResolvedValueOnce(response({ ok: false, code: 'ProfileUnavailable', message: 'Try saving again.', displayName: 'Alex' }))
    vi.stubGlobal('fetch', fetch)
    render(<Harness />)
    await waitFor(() => expect(screen.getByLabelText('Your name')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'x'.repeat(81) } })
    expect(screen.getByRole('alert')).toHaveTextContent('Use 80 characters or fewer.')
    expect(screen.getByText('Save name')).toBeDisabled()
    expect(fetch).toHaveBeenCalledTimes(1)
    fireEvent.change(screen.getByLabelText('Your name'), { target: { value: 'Sam' } })
    fireEvent.click(screen.getByText('Save name'))
    await screen.findByText('Try saving again.')
    expect(screen.getByLabelText('Your name')).toHaveValue('Sam')
    fireEvent.click(screen.getByText('Discard changes'))
    expect(screen.getByLabelText('Your name')).toHaveValue('Alex')
  })
})

describe('profile wire contracts', () => {
  const friend = { mode: 'Friend', state: 'Connected', detail: '', endpoint: '', lastConnectedUtc: null, remoteControlsEnabled: false,
    canStart: false, canStop: false, profiles: [], connectionId: 'one', connections: null }
  const device = { id: 'pc', profileId: '', assignedProfileIds: [], name: 'PC label', canStart: false, canStop: false, canExtendTimer: false,
    canViewLogs: false, revoked: false, paired: true, approvalPending: false, credentialExpiresUtc: null, lastHeartbeatUtc: null,
    serverPermissions: [], accessExpiresUtc: null, accessExpired: false }
  const companion = { listenerActive: false, listenerState: 'Off', listenerWarning: null, endpoint: '', fingerprint: null,
    certificates: null, route: { mode: 'DirectInternet', address: '' }, devices: [device], stopSafety: {} }
  it('decodes shared names on the active and every saved Host while preserving local aliases and older peers', () => {
    expect(parseFriendSnapshot({ ...friend, connectionName: 'My alias', hostProfileName: 'Alex', connections: [{ ...friend, hostProfileName: 'Sam' }] }))
      .toMatchObject({ hostProfileName: 'Alex', connectionName: 'My alias', connections: [{ hostProfileName: 'Sam' }] })
    expect(parseFriendSnapshot(friend).hostProfileName).toBeUndefined()
    expect(parseFriendSnapshot({ ...friend, hostProfileName: null }).hostProfileName).toBeNull()
    expect(() => parseFriendSnapshot({ ...friend, hostProfileName: 123 })).toThrow(ContractError)
    expect(parseCompanionInfo({ ...companion, devices: [{ ...device, profileName: 'Sam' }] }).devices[0]).toMatchObject({ name: 'PC label', profileName: 'Sam' })
    expect(parseCompanionInfo(companion).devices[0].profileName).toBeUndefined()
    expect(() => parseCompanionInfo({ ...companion, devices: [{ ...device, profileName: false }] })).toThrow(ContractError)
  })
  it('requires an explicit name and a typed mutation result', () => {
    expect(parseLocalProfile({ displayName: '' })).toEqual({ displayName: '' })
    expect(parseLocalProfileResult({ ok: true, code: 'Saved', message: 'Saved', displayName: 'Alex' }).displayName).toBe('Alex')
    for (const value of [null, {}, { displayName: false }, { displayName: '\ud800' }, { displayName: 'x'.repeat(81) }]) expect(() => parseLocalProfile(value)).toThrow(ContractError)
    expect(parseLocalProfile({ displayName: '😀'.repeat(40) }).displayName).toBe('😀'.repeat(40))
    for (const hostProfileName of ['\udc00', 'x'.repeat(81), 'a\u200db'])
      expect(() => parseFriendSnapshot({ ...friend, hostProfileName })).toThrow(ContractError)
    expect(() => parseLocalProfileResult({ displayName: 'Alex' })).toThrow(ContractError)
  })
})
