import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { CommandPalette, WorkspaceNavigation, type WorkspaceCommand } from './WorkspaceChrome'

describe('WorkspaceNavigation', () => {
  it('exposes concurrent Host and Join workspaces plus attention and settings', () => {
    const navigate = vi.fn()
    render(<WorkspaceNavigation page="host" activeRuns={2} unread onNavigate={navigate} />)
    expect(screen.getByRole('button', { name: /Host/ })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('button', { name: 'Join' })).toBeEnabled()
    expect(screen.getByText('2')).toHaveAccessibleName('2 running')
    fireEvent.click(screen.getByRole('button', { name: 'Attention' }))
    expect(navigate).toHaveBeenCalledWith('attention')
  })
})

describe('CommandPalette', () => {
  it('filters and runs safe commands while leaving guarded lifecycle commands disabled', () => {
    const runSettings = vi.fn()
    const commands: WorkspaceCommand[] = [
      { id: 'settings', label: 'Open Settings', detail: 'Application preferences', icon: 'settings', run: runSettings },
      { id: 'stop', label: 'Stop selected server', detail: 'Use the guarded control in Overview', icon: 'stop', disabled: true, run: vi.fn() }
    ]
    render(<CommandPalette open commands={commands} onClose={vi.fn()} />)
    fireEvent.change(screen.getByLabelText('Search commands'), { target: { value: 'settings' } })
    fireEvent.keyDown(screen.getByLabelText('Search commands'), { key: 'Enter' })
    expect(runSettings).toHaveBeenCalledOnce()
  })
})
