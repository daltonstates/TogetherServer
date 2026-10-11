import { fireEvent, render, screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { StartupRecoveryView } from './contracts'
import { StartupRecoveryNotice } from './StartupRecoveryNotice'

const profileId = '11111111-1111-1111-1111-111111111111'
const recovery: StartupRecoveryView = {
  previousSessionInterrupted: true, previousStartedUtc: '2026-10-10T12:00:00Z', items: [],
  message: 'The previous app session was interrupted, but no previously active saved server needs action.',
  windowsRestartRegistered: true
}
const profiles = [{ id: profileId, name: 'Saved fixture' }]

beforeEach(() => { sessionStorage.clear() })

describe('recovered app session notice', () => {
  it('dismisses the informational notice without an empty choices box or any server action', () => {
    const onResume = vi.fn()
    const view = render(<StartupRecoveryNotice recovery={recovery} profiles={profiles} busy={false} onResume={onResume} />)
    expect(screen.getByRole('region', { name: 'Recovered app session' }).querySelector('.choices')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Resume hosting' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss recovered app session' }))
    expect(screen.queryByRole('region', { name: 'Recovered app session' })).not.toBeInTheDocument()
    view.rerender(<StartupRecoveryNotice recovery={{ ...recovery, message: 'A newer display summary.' }} profiles={profiles} busy={false} onResume={onResume} />)
    expect(screen.queryByRole('region')).not.toBeInTheDocument()
    view.unmount()
    render(<StartupRecoveryNotice recovery={{ ...recovery }} profiles={profiles} busy={false} onResume={onResume} />)
    expect(screen.queryByRole('region')).not.toBeInTheDocument()
    expect(onResume).not.toHaveBeenCalled()
  })

  it('shows a different interrupted session or a changed recovery state after dismissal', () => {
    const onResume = vi.fn()
    const view = render(<StartupRecoveryNotice recovery={recovery} profiles={profiles} busy={false} onResume={onResume} />)
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss recovered app session' }))
    view.rerender(<StartupRecoveryNotice recovery={{ ...recovery, previousStartedUtc: '2026-10-11T12:00:00Z' }} profiles={profiles} busy={false} onResume={onResume} />)
    expect(screen.getByRole('region', { name: 'Recovered app session' })).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss recovered app session' }))
    view.rerender(<StartupRecoveryNotice recovery={{ ...recovery, previousStartedUtc: '2026-10-11T12:00:00Z',
      items: [{ profileId, state: 'Needs review', detail: 'The exact process identity is unknown.', canResume: false }] }} profiles={profiles} busy={false} onResume={onResume} />)
    expect(screen.getByText('Saved fixture · Needs review')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Resume hosting' })).not.toBeInTheDocument()
    expect(onResume).not.toHaveBeenCalled()
  })

  it('keeps dismissal across polling order changes', () => {
    const items = [profileId, '22222222-2222-2222-2222-222222222222'].map(id => ({
      profileId: id, state: 'Needs review', detail: 'Review the recorded process.', canResume: false
    }))
    const view = render(<StartupRecoveryNotice recovery={{ ...recovery, items }} profiles={profiles} busy={false} onResume={vi.fn()} />)
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss recovered app session' }))
    view.rerender(<StartupRecoveryNotice recovery={{ ...recovery, items: [...items].reverse() }} profiles={profiles} busy={false} onResume={vi.fn()} />)
    expect(screen.queryByRole('region')).not.toBeInTheDocument()
  })

  it('allows explicit review of a dismissed notice and resumes only on a permitted click', () => {
    const onResume = vi.fn()
    const resumable = { ...recovery, items: [{ profileId, state: 'Ready to resume', detail: 'Normal Start checks still apply.', canResume: true }] }
    const view = render(<StartupRecoveryNotice recovery={resumable} profiles={profiles} busy onResume={onResume} />)
    expect(screen.getByRole('button', { name: 'Resume hosting' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Dismiss recovered app session' })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss recovered app session' }))
    view.rerender(<StartupRecoveryNotice review recovery={resumable} profiles={profiles} busy={false} onResume={onResume} />)
    fireEvent.click(screen.getByText('Session recovery'))
    const notice = screen.getByRole('region', { name: 'Recovered app session' })
    expect(notice).toBeVisible()
    expect(within(notice).queryByRole('button', { name: 'Dismiss recovered app session' })).not.toBeInTheDocument()
    expect(onResume).not.toHaveBeenCalled()
    fireEvent.click(within(notice).getByRole('button', { name: 'Resume hosting' }))
    expect(onResume).toHaveBeenCalledExactlyOnceWith(profileId)
  })

  it('still dismisses when optional session storage is unavailable', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('Storage unavailable') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage unavailable') })
    render(<StartupRecoveryNotice recovery={recovery} profiles={profiles} busy={false} onResume={vi.fn()} />)
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss recovered app session' }))
    expect(screen.queryByRole('region')).not.toBeInTheDocument()
  })

  it('omits recovery for a clean previous exit, including the Attention review', () => {
    const view = render(<StartupRecoveryNotice recovery={{ ...recovery, previousSessionInterrupted: false }} profiles={profiles} busy={false} onResume={vi.fn()} />)
    expect(view.container).toBeEmptyDOMElement()
    view.rerender(<StartupRecoveryNotice review recovery={{ ...recovery, previousSessionInterrupted: false }} profiles={profiles} busy={false} onResume={vi.fn()} />)
    expect(view.container).toBeEmptyDOMElement()
  })
})
