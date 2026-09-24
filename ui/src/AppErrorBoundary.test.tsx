import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { AppErrorBoundary } from './AppErrorBoundary'

function BrokenView(): never {
  throw new Error('render failed')
}

describe('AppErrorBoundary', () => {
  it('offers a recoverable fallback after an unexpected render failure', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)

    render(<AppErrorBoundary><BrokenView /></AppErrorBoundary>)

    expect(screen.getByRole('alert')).toHaveTextContent('The interface needs to reload')
    expect(screen.getByRole('button', { name: 'Reload interface' })).toBeInTheDocument()
  })
})
