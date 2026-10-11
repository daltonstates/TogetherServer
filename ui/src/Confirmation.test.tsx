import { act, fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { Button } from './Controls'
import { useConfirmation } from './Confirmation'

describe('useConfirmation', () => {
  function harness() {
    let api: ReturnType<typeof useConfirmation>
    function Harness() {
      api = useConfirmation()
      return <><Button onClick={() => { void api.confirm({ title: 'Stop server?', description: 'Players will disconnect.', confirmLabel: 'Stop', destructive: true }) }}>Review stop</Button>{api.confirmationDialog}</>
    }
    const view = render(<Harness />)
    return { view, getApi: () => api! }
  }

  it('focuses the safe action and resolves a deliberate confirmation while restoring the opener', async () => {
    const { getApi } = harness()
    const opener = screen.getByRole('button', { name: 'Review stop' })
    opener.focus()
    let decision!: Promise<boolean>
    act(() => { decision = getApi().confirm({ title: 'Stop server?', description: 'Players will disconnect.', confirmLabel: 'Stop', destructive: true }) })
    expect(screen.getByRole('dialog', { name: 'Stop server?' })).toHaveAccessibleDescription('Players will disconnect.')
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus()
    const stop = screen.getByRole('button', { name: 'Stop' })
    expect(stop).toHaveClass('danger')
    fireEvent.click(stop)
    await expect(decision).resolves.toBe(true)
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(opener).toHaveFocus()
  })

  it('queues overlapping reviews and keeps each answer separate', async () => {
    const { getApi } = harness()
    let first!: Promise<boolean>, second!: Promise<boolean>
    act(() => {
      first = getApi().confirm({ title: 'First review', description: 'First consequence', confirmLabel: 'Continue' })
      second = getApi().confirm({ title: 'Second review', description: 'Second consequence', confirmLabel: 'Remove', cancelLabel: 'Keep' })
    })
    expect(screen.getAllByRole('dialog')).toHaveLength(1)
    expect(screen.getByRole('dialog')).toHaveAccessibleName('First review')
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await expect(first).resolves.toBe(false)
    expect(screen.getByRole('dialog')).toHaveAccessibleName('Second review')
    expect(screen.getByRole('button', { name: 'Keep' })).toHaveFocus()
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }))
    await expect(second).resolves.toBe(true)
  })

  it('cancels with Escape, native cancel and the Close action without window.confirm', async () => {
    const native = vi.spyOn(window, 'confirm').mockImplementation(() => { throw new Error('Native confirmation used') })
    const { getApi } = harness()
    for (const mode of ['escape', 'native', 'close']) {
      let decision!: Promise<boolean>
      act(() => { decision = getApi().confirm({ title: 'Review action', description: 'Consequences', confirmLabel: 'Continue' }) })
      if (mode === 'escape') fireEvent.keyDown(screen.getByRole('button', { name: 'Cancel' }), { key: 'Escape' })
      if (mode === 'native') fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true }))
      if (mode === 'close') fireEvent.click(screen.getByRole('button', { name: 'Close Review action' }))
      await expect(decision).resolves.toBe(false)
    }
    expect(native).not.toHaveBeenCalled()
  })

  it('cancels every unresolved request on unmount and safely rejects later requests', async () => {
    const { getApi, view } = harness()
    const api = getApi()
    let first!: Promise<boolean>, second!: Promise<boolean>
    act(() => {
      first = api.confirm({ title: 'First', description: 'First', confirmLabel: 'Continue' })
      second = api.confirm({ title: 'Second', description: 'Second', confirmLabel: 'Continue' })
    })
    view.unmount()
    await expect(first).resolves.toBe(false)
    await expect(second).resolves.toBe(false)
    await expect(api.confirm({ title: 'Gone', description: 'Gone', confirmLabel: 'Continue' })).resolves.toBe(false)
  })
})
