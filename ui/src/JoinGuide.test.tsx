import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { JoinGuide } from './JoinGuide'

describe('JoinGuide', () => {
  it('shows game-specific steps without exposing connection secrets', () => {
    const { rerender } = render(<JoinGuide kind="MinecraftBedrock" />)
    expect(screen.getByText(/Server Address/)).toBeInTheDocument()
    expect(screen.getByText(/Outdated Client or Outdated Server/)).toBeInTheDocument()
    rerender(<JoinGuide kind="Valheim" />)
    expect(screen.getByText(/password shared separately/)).toBeInTheDocument()
    expect(screen.queryByText(/server code/i)).not.toBeInTheDocument()
  })
  it('uses separate Copy fields for Bedrock and Terraria rather than ambiguous colon splitting', () => {
    const view = render(<JoinGuide kind="MinecraftBedrock" />)
    expect(screen.getByText(/Copy beside Server address/)).toHaveTextContent('Copy beside Port')
    expect(view.container.textContent).not.toContain('final colon')
    view.rerender(<JoinGuide kind="Terraria" />)
    expect(screen.getByText(/Copy beside Server address/)).toHaveTextContent('Copy beside Port')
    expect(view.container.textContent).not.toContain('port after')
  })
})
