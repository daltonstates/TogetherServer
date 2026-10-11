import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { PaneErrorBoundary } from './PaneErrorBoundary'

function BrokenView(): never {
  throw new Error('pane failed')
}

describe('PaneErrorBoundary', () => {
  it('allows dismissal while retaining Retry and shows a new failure after Retry', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    render(<PaneErrorBoundary title="Players"><BrokenView /></PaneErrorBoundary>)
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss section error' }))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Try section again' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Players could not be shown')
  })

  it('contains a pane failure without replacing the rest of the interface', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)

    render(<main><span>Host remains available</span><PaneErrorBoundary title="World protection">
      <BrokenView />
    </PaneErrorBoundary></main>)

    expect(screen.getByText('Host remains available')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('World protection could not be shown')
    expect(screen.getByRole('button', { name: 'Try section again' })).toBeInTheDocument()
  })

  it('recovers when the pane identity changes', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const view = render(<PaneErrorBoundary title="Players" resetKey="one"><BrokenView /></PaneErrorBoundary>)

    view.rerender(<PaneErrorBoundary title="Players" resetKey="two"><p>Fresh pane</p></PaneErrorBoundary>)

    expect(screen.getByText('Fresh pane')).toBeInTheDocument()
  })
})
