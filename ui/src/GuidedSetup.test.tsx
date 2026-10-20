import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { GuideWelcome, GuidedWalkthrough, SetupStageTip, type GuideStep } from './GuidedSetup'

afterEach(() => vi.restoreAllMocks())
const steps: GuideStep[] = [
  { id: 'first', title: 'Choose your game', detail: 'Use the real setup button.', target: '#real-action', tip: 'You stay in control.' },
  { id: 'second', title: 'Test with a Friend', detail: 'Check the actual game.', target: '#second-action', tip: 'A port response does not prove a join.' }
]
const callbacks = () => ({ onStep: vi.fn(), onLocate: vi.fn(), onClose: vi.fn(), onFinish: vi.fn() })

describe('guided setup', () => {
  it('makes both installer paths actionable and lets the user defer setup', () => {
    const onStart = vi.fn(), onClose = vi.fn()
    render(<GuideWelcome intent="host" busy={false} onStart={onStart} onClose={onClose} />)
    fireEvent.click(screen.getByRole('button', { name: /Host on this PC/ }))
    expect(onStart).toHaveBeenLastCalledWith('host')
    fireEvent.click(screen.getByRole('button', { name: /Join a friend/ }))
    expect(onStart).toHaveBeenLastCalledWith('join')
    fireEvent.click(screen.getByRole('button', { name: 'Set up later' }))
    expect(onClose).toHaveBeenCalledOnce()
    expect(screen.getByText('Your installer choice')).toBeInTheDocument()
  })

  it('highlights an actual action without invoking it, and restores it when paused', () => {
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ left: 30, right: 130, top: 80, bottom: 124, width: 100, height: 44 } as DOMRect)
    const action = vi.fn(), props = callbacks()
    const view = render(<><button id="real-action" onClick={action}>Real app action</button>
      <GuidedWalkthrough kind="host" steps={steps} stepIndex={0} suspended={false} busy={false} {...props} /></>)
    const target = screen.getByRole('button', { name: 'Real app action' })
    expect(target).toHaveAttribute('data-guide-highlight', 'true')
    fireEvent.click(screen.getByRole('button', { name: 'Show me' }))
    fireEvent.click(screen.getByRole('button', { name: 'Next tip' }))
    expect(props.onLocate).toHaveBeenCalledWith(0)
    expect(props.onStep).toHaveBeenCalledWith(1)
    expect(action).not.toHaveBeenCalled()
    fireEvent.click(target)
    expect(action).toHaveBeenCalledOnce()
    view.unmount()
    expect(target).not.toHaveAttribute('data-guide-highlight')
  })

  it('suspends the coach and spotlight while a native workflow dialog is open', async () => {
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ left: 20, right: 120, top: 80, bottom: 124, width: 100, height: 44 } as DOMRect)
    const props = callbacks()
    const view = render(<><button id="real-action">Setup</button><GuidedWalkthrough kind="host" steps={steps} stepIndex={0} suspended={false} busy={false} {...props} /></>)
    view.rerender(<><button id="real-action">Setup</button><dialog open aria-label="Game setup" /><GuidedWalkthrough kind="host" steps={steps} stepIndex={0} suspended={false} busy={false} {...props} /></>)
    await waitFor(() => expect(screen.queryByRole('complementary')).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Setup' })).not.toHaveAttribute('data-guide-highlight')
  })

  it('keeps a narrow-screen action exposed when the coach needs its own scroll space', () => {
    vi.stubGlobal('innerWidth', 390); vi.stubGlobal('innerHeight', 500)
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      return (this.id === 'real-action'
        ? { left: 20, right: 370, top: 240, bottom: 284, width: 350, height: 44 }
        : { left: 14, right: 374, top: 14, bottom: 414, width: 360, height: 400 }) as DOMRect
    })
    render(<><button id="real-action">Real action</button><GuidedWalkthrough kind="host" steps={steps} stepIndex={0} suspended={false} busy={false} {...callbacks()} /></>)
    const coach = screen.getByRole('complementary')
    expect(parseFloat(coach.style.maxHeight)).toBeGreaterThan(0)
    expect(parseFloat(coach.style.top) + parseFloat(coach.style.maxHeight)).toBeLessThan(240)
  })

  it('keeps pause separate from finishing and dismisses Escape before workspace shortcuts', () => {
    const props = callbacks(), shortcut = vi.fn()
    render(<GuidedWalkthrough kind="join" steps={steps} stepIndex={1} suspended={false} busy={false} {...props} />)
    window.addEventListener('keydown', shortcut)
    try {
      fireEvent.keyDown(document.body, { key: 'Escape' })
      expect(props.onClose).toHaveBeenCalledOnce()
      expect(shortcut).not.toHaveBeenCalled()
      expect(props.onFinish).not.toHaveBeenCalled()
      fireEvent.click(screen.getByRole('button', { name: 'Finish guide' }))
      expect(props.onFinish).toHaveBeenCalledOnce()
    } finally { window.removeEventListener('keydown', shortcut) }
  })

  it('leaves an active guide in place when Escape has already dismissed a header menu', () => {
    const dismissMenu = (event: KeyboardEvent) => event.preventDefault()
    document.addEventListener('keydown', dismissMenu, true)
    const props = callbacks()
    try {
      render(<GuidedWalkthrough kind="host" steps={steps} stepIndex={0} suspended={false} busy={false} {...props} />)
      fireEvent.keyDown(document.body, { key: 'Escape' })
      expect(props.onClose).not.toHaveBeenCalled()
    } finally { document.removeEventListener('keydown', dismissMenu, true) }
  })

  it('explains the correct game dependencies without treating SteamCMD as a prerequisite', () => {
    const view = render(<SetupStageTip kind="Valheim" stage="server" />)
    expect(screen.getByLabelText('Setup tip')).toHaveTextContent('SteamCMD is an optional way to install it')
    view.rerender(<SetupStageTip kind="MinecraftJava" stage="server" />)
    expect(screen.getByLabelText('Setup tip')).toHaveTextContent('matching Java runtime')
    view.rerender(<SetupStageTip kind="MinecraftBedrock" stage="server" />)
    expect(screen.getByLabelText('Setup tip')).toHaveTextContent('Java and SteamCMD are not needed')
  })
})
