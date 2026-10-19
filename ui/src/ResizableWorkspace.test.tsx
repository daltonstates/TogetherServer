import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ResizableWorkspace } from './ResizableWorkspace'

const serverKey = 'togetherserver.sidebar-width.servers.v1'
const settingsKey = 'togetherserver.sidebar-width.settings.v1'
const pane = (area: 'servers' | 'settings' = 'servers') => <ResizableWorkspace area={area} sidebarLabel="Test navigation"
  sidebar={<button>A long server name</button>}><button>Start server</button></ResizableWorkspace>
const divider = () => screen.getByRole('separator')

beforeEach(() => window.localStorage.clear())
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('ResizableWorkspace', () => {
  it('supports keyboard bounds, larger steps, reset and separate remembered page widths', () => {
    const first = render(pane())
    expect(divider()).toHaveAttribute('aria-controls', screen.getByRole('complementary').id)
    expect(divider()).toHaveAttribute('aria-valuenow', '268')
    fireEvent.keyDown(divider(), { key: 'ArrowRight' })
    fireEvent.keyDown(divider(), { key: 'ArrowRight', shiftKey: true })
    expect(divider()).toHaveAttribute('aria-valuenow', '318')
    first.unmount()
    const settings = render(pane('settings'))
    expect(divider()).toHaveAccessibleName('App settings sidebar width')
    expect(divider()).toHaveAttribute('aria-valuenow', '268')
    fireEvent.keyDown(divider(), { key: 'End' })
    fireEvent.keyDown(divider(), { key: 'ArrowRight' })
    expect(divider()).toHaveAttribute('aria-valuenow', '520')
    expect(window.localStorage.getItem(settingsKey)).toBe('520')
    settings.unmount()
    render(pane()) // Join reuses the Host sidebar preference.
    expect(divider()).toHaveAccessibleName('Server sidebar width')
    expect(divider()).toHaveAttribute('aria-valuenow', '318')
    fireEvent.keyDown(divider(), { key: 'Home' })
    fireEvent.keyDown(divider(), { key: 'ArrowLeft' })
    expect(divider()).toHaveAttribute('aria-valuenow', '200')
    fireEvent.doubleClick(divider())
    expect(divider()).toHaveAttribute('aria-valuenow', '268')
    expect(window.localStorage.getItem(serverKey)).toBe('268')
  })

  it('drags with pointer capture and ignores secondary buttons, other pointers and movement after cancellation', () => {
    vi.stubGlobal('PointerEvent', class extends MouseEvent {
      pointerId: number
      constructor(type: string, init: PointerEventInit = {}) { super(type, init); this.pointerId = init.pointerId ?? 1 }
    })
    render(pane())
    const capture = vi.fn()
    divider().setPointerCapture = capture
    fireEvent.pointerDown(divider(), { button: 2, clientX: 268 })
    fireEvent.pointerMove(divider(), { clientX: 500 })
    expect(divider()).toHaveAttribute('aria-valuenow', '268')
    fireEvent.pointerDown(divider(), { button: 0, clientX: 268 })
    expect(capture).toHaveBeenCalledOnce()
    fireEvent.pointerMove(divider(), { pointerId: 2, clientX: 500 })
    expect(divider()).toHaveAttribute('aria-valuenow', '268')
    fireEvent.pointerMove(divider(), { clientX: 410 })
    expect(divider()).toHaveAttribute('aria-valuenow', '410')
    fireEvent.pointerMove(divider(), { clientX: 900 })
    expect(divider()).toHaveAttribute('aria-valuenow', '520')
    fireEvent.pointerCancel(divider())
    fireEvent.pointerMove(divider(), { clientX: 250 })
    expect(divider()).toHaveAttribute('aria-valuenow', '520')
    fireEvent.pointerDown(divider(), { button: 0, clientX: 520 })
    fireEvent.pointerUp(divider(), { pointerId: 2 })
    fireEvent.pointerMove(divider(), { clientX: 260 })
    expect(divider()).toHaveAttribute('aria-valuenow', '260')
    fireEvent.pointerUp(divider())
    fireEvent.pointerMove(divider(), { clientX: 400 })
    expect(divider()).toHaveAttribute('aria-valuenow', '260')
    expect(screen.getByRole('button', { name: 'Start server' })).toBeEnabled()
  })

  it('protects content when the window shrinks and restores the preferred width when it grows', () => {
    let containerWidth = 1100
    let measure = () => {}
    const disconnect = vi.fn()
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(() => ({ width: containerWidth } as DOMRect))
    vi.stubGlobal('ResizeObserver', class {
      constructor(callback: () => void) { measure = callback }
      observe() {}
      disconnect = disconnect
    })
    window.localStorage.setItem(serverKey, '480')
    const view = render(pane())
    expect(divider()).toHaveAttribute('aria-valuenow', '480')
    containerWidth = 720
    act(() => measure())
    expect(divider()).toHaveAttribute('aria-valuemax', '348')
    expect(divider()).toHaveAttribute('aria-valuenow', '348')
    expect(window.localStorage.getItem(serverKey)).toBe('480')
    containerWidth = 1100
    act(() => measure())
    expect(divider()).toHaveAttribute('aria-valuenow', '480')
    view.unmount()
    expect(disconnect).toHaveBeenCalledOnce()
  })

  it.each(['', '-1', 'NaN', 'Infinity', '521', '{bad'] )('ignores an invalid saved width: %s', saved => {
    window.localStorage.setItem(serverKey, saved)
    render(pane())
    expect(divider()).toHaveAttribute('aria-valuenow', '268')
  })

  it('keeps resizing usable when storage is unavailable', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('unavailable') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('unavailable') })
    render(pane())
    fireEvent.keyDown(divider(), { key: 'ArrowLeft' })
    expect(divider()).toHaveAttribute('aria-valuenow', '258')
  })
})
