import { createRef } from 'react'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { Button } from './Controls'
import { Tooltip } from './Tooltip'

describe('Tooltip', () => {
  it('keeps one active tooltip when pointer and keyboard target different controls', () => {
    render(<><Button title="First help">First</Button><Button title="Second help">Second</Button></>)
    const first = screen.getByRole('button', { name: 'First' })
    fireEvent.focus(first)
    fireEvent.mouseEnter(screen.getByRole('button', { name: 'Second' }))
    expect(screen.getAllByRole('tooltip')).toHaveLength(1)
    expect(screen.getByRole('tooltip')).toHaveTextContent('Second help')
    expect(first).not.toHaveAttribute('aria-describedby')
  })
  it('migrates Button title without a native duplicate and preserves refs, events and descriptions', () => {
    const ref = createRef<HTMLButtonElement>()
    const hover = vi.fn(), click = vi.fn()
    render(<><p id="existing">Existing help</p><Button ref={ref} title="Copy address" aria-describedby="existing"
      onMouseEnter={hover} onClick={click}>Copy</Button></>)
    const button = screen.getByRole('button', { name: 'Copy' })
    expect(ref.current).toBe(button)
    expect(button).not.toHaveAttribute('title')
    expect(screen.queryByRole('tooltip')).toBeNull()
    fireEvent.mouseEnter(button)
    expect(hover).toHaveBeenCalledOnce()
    expect(screen.getByRole('tooltip')).toHaveTextContent('Copy address')
    expect(button).toHaveAccessibleDescription('Existing help Copy address')
    fireEvent.click(button)
    expect(click).toHaveBeenCalledOnce()
    fireEvent.mouseLeave(button)
    expect(screen.queryByRole('tooltip')).toBeNull()
    expect(button).toHaveAccessibleDescription('Existing help')
  })

  it('opens for focus, remains on pointer exit and dismisses Escape before workspace shortcuts', () => {
    render(<Button title="Application preferences" aria-label="App Settings">Settings</Button>)
    const button = screen.getByRole('button', { name: 'App Settings' })
    fireEvent.focus(button)
    fireEvent.mouseEnter(button)
    fireEvent.mouseLeave(button)
    expect(screen.getByRole('tooltip')).toBeVisible()
    const shortcut = vi.fn()
    window.addEventListener('keydown', shortcut)
    try {
      fireEvent.keyDown(button, { key: 'Escape' })
      expect(screen.queryByRole('tooltip')).toBeNull()
      expect(shortcut).not.toHaveBeenCalled()
      fireEvent.blur(button)
      fireEvent.focus(button)
      expect(screen.getByRole('tooltip')).toBeVisible()
    } finally { window.removeEventListener('keydown', shortcut) }
  })

  it('opens by touch and closes on an outside touch', () => {
    render(<><Tooltip content="More information"><button>Help</button></Tooltip><button>Outside</button></>)
    const event = new Event('pointerdown', { bubbles: true })
    Object.defineProperty(event, 'pointerType', { value: 'touch' })
    fireEvent(screen.getByRole('button', { name: 'Help' }), event)
    expect(screen.getByRole('tooltip')).toHaveTextContent('More information')
    fireEvent.pointerDown(screen.getByRole('button', { name: 'Outside' }))
    expect(screen.queryByRole('tooltip')).toBeNull()
  })

  it('explains a disabled action on pointer hover without enabling the action', () => {
    const click = vi.fn()
    render(<Button title="Wait for the current request" disabled onClick={click}>Start</Button>)
    const button = screen.getByRole('button', { name: 'Start' })
    fireEvent.pointerEnter(button)
    expect(screen.getByRole('tooltip')).toHaveTextContent('Wait for the current request')
    fireEvent.click(button)
    expect(click).not.toHaveBeenCalled()
    expect(button).toBeDisabled()
    fireEvent.pointerLeave(button)
    expect(screen.queryByRole('tooltip')).toBeNull()
  })

  it('preserves ordinary Button attributes and callback ref cleanup without a tooltip', () => {
    const cleanup = vi.fn(), ref = vi.fn(() => cleanup)
    const view = render(<Button ref={ref} type="submit" name="choice" value="save" className="secondary">Save</Button>)
    const button = screen.getByRole('button', { name: 'Save' })
    expect(button).toHaveAttribute('type', 'submit')
    expect(button).toHaveAttribute('name', 'choice')
    expect(button).toHaveAttribute('value', 'save')
    expect(button).toHaveClass('ui-button', 'secondary')
    expect(ref).toHaveBeenCalledWith(button)
    view.unmount()
    expect(cleanup).toHaveBeenCalledOnce()
  })

  it('flips above a bottom-edge trigger and keeps the tail anchored after edge adjustment', () => {
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      return (this.getAttribute('role') === 'tooltip'
        ? { left: 0, top: 0, width: 160, height: 32, right: 160, bottom: 32 }
        : { left: window.innerWidth - 30, top: window.innerHeight - 35, width: 24, height: 24,
          right: window.innerWidth - 6, bottom: window.innerHeight - 11 }) as DOMRect
    })
    render(<Button title="Near the viewport edge">Help</Button>)
    fireEvent.mouseEnter(screen.getByRole('button', { name: 'Help' }))
    const tip = screen.getByRole('tooltip')
    expect(tip).toHaveAttribute('data-side', 'top')
    expect(tip.style.left).toBe(`${window.innerWidth - 168}px`)
    expect(tip.style.getPropertyValue('--tooltip-tail')).toBe('150px')
  })

  it('keeps a modal tooltip in the native top layer and removes it on unmount', () => {
    const view = render(<dialog open><Button title="Dialog help">Help</Button></dialog>)
    fireEvent.mouseEnter(screen.getByRole('button', { name: 'Help' }))
    expect(screen.getByRole('tooltip').closest('dialog')).not.toBeNull()
    view.unmount()
    expect(screen.queryByRole('tooltip')).toBeNull()
  })
})
