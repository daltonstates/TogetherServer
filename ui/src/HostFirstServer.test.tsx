import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { HostFirstServer } from './HostFirstServer'
import { EditorDraftRecovery } from './editorProtectedDraft'

function actions() {
  return { onCreate: vi.fn(), onResume: vi.fn(), onJoin: vi.fn(), onMove: vi.fn() }
}

describe('First Host task', () => {
  it('opens setup or Join only on the corresponding explicit action', () => {
    const callbacks = actions()
    render(<HostFirstServer {...callbacks} busy={false} resume={false} recovered={false} recovery={null} />)
    expect(callbacks.onCreate).not.toHaveBeenCalled()
    expect(callbacks.onJoin).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Host a server' }))
    expect(callbacks.onCreate).toHaveBeenCalledOnce()
    fireEvent.click(screen.getByRole('button', { name: 'Join a server' }))
    expect(callbacks.onJoin).toHaveBeenCalledOnce()
    fireEvent.click(screen.getByRole('button', { name: 'Move hosting from another PC' }))
    expect(callbacks.onMove).toHaveBeenCalledOnce()
  })

  it('makes recovery a distinct reviewed action and preserves its error and discard path', () => {
    const recover = vi.fn(), discard = vi.fn()
    render(<HostFirstServer {...actions()} busy={false} resume={false} recovered
      recovery={<EditorDraftRecovery purpose="setup" recovered="protected nonsecret setup" message="Draft could not be saved. Retry before leaving."
        onRecover={recover} onDiscard={discard} />} />)
    const entry = screen.getByRole('region', { name: 'Host or join a server' })
    const recovery = within(entry).getByRole('region', { name: 'Recovered server setup' })
    expect(within(recovery).getByText(/Enter the game password again/)).toBeInTheDocument()
    expect(within(recovery).getByText('Draft could not be saved. Retry before leaving.')).toBeInTheDocument()
    expect(screen.queryByText(/Your saved file is unchanged/)).not.toBeInTheDocument()
    expect(recover).not.toHaveBeenCalled()
    fireEvent.click(within(recovery).getByRole('button', { name: 'Review saved setup' }))
    expect(recover).toHaveBeenCalledOnce()
    fireEvent.click(within(recovery).getByRole('button', { name: 'Discard saved setup' }))
    expect(discard).toHaveBeenCalledOnce()
    expect(within(entry).getByRole('button', { name: 'Host a server' })).toBeEnabled()
  })

  it('continues a paused setup rather than creating a duplicate server', () => {
    const callbacks = actions()
    render(<HostFirstServer {...callbacks} busy={false} resume recovered={false} recovery={null} />)
    expect(screen.queryByRole('button', { name: 'Host a server' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Continue setup' }))
    expect(callbacks.onResume).toHaveBeenCalledOnce()
    expect(callbacks.onCreate).not.toHaveBeenCalled()
  })

  it('keeps actions blocked while a current operation is pending', () => {
    render(<HostFirstServer {...actions()} busy resume recovered={false} recovery={null} />)
    for (const button of screen.getAllByRole('button')) expect(button).toBeDisabled()
  })

  it('retains the existing file-draft language for file editors', () => {
    render(<EditorDraftRecovery recovered="file edit" message="" onRecover={vi.fn()} onDiscard={vi.fn()} />)
    expect(screen.getByRole('region', { name: 'Recovered editor draft' })).toBeInTheDocument()
    expect(screen.getByText(/Your saved file is unchanged/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Review recovered draft' })).toBeInTheDocument()
  })
})
