import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { CommandPalette, HeaderTools, type WorkspaceCommand } from './WorkspaceChrome'
import { Button } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
import workspaceChromeCss from './workspace-chrome.css?inline'
import companionCss from './companion.css?inline'
import qolCss from './qol.css?inline'
import redesignCss from './redesign.css?inline'

describe('HeaderTools', () => {
  it('keeps notification headings, actions and messages inset through the app CSS cascade', () => {
    render(<><style>{[workspaceChromeCss, companionCss, qolCss, redesignCss].join('\n')}</style>
      <div className="shell app-shell"><header className="topbar"><HeaderTools>
        <details open className="notification-menu"><summary>Notifications</summary>
          <div className="notification-panel">
            <div className="notification-panel-heading"><div><strong>Notifications</strong><small>Recent app and connection activity</small></div><Button>Clear activity</Button></div>
            <Button className="secondary notification-center-link">Open Attention Center</Button>
            <p className="notification-empty">No recent notifications.</p>
            <DismissibleNotice className="notification-item"><span>!</span><div>Connection issue</div></DismissibleNotice>
          </div>
        </details>
      </HeaderTools></header></div></>)
    const panel = screen.getByText('No recent notifications.').parentElement!
    const style = getComputedStyle(panel)
    expect(style.padding).toBe('14px')
    expect(style.gap).toBe('12px')
    expect(style.flexDirection).toBe('column')
    const heading = getComputedStyle(screen.getByText('Recent app and connection activity').closest('.notification-panel-heading')!)
    expect(heading.padding).toBe('0px 0px 12px')
    expect(heading.margin).toBe('0px')
    expect(getComputedStyle(screen.getByRole('button', { name: 'Open Attention Center' })).margin).toBe('0px')
    expect(getComputedStyle(screen.getByText('No recent notifications.')).padding).toBe('8px 0px')
    expect(getComputedStyle(screen.getByText('Connection issue').closest('.notification-item')!).paddingInlineEnd).toBe('3.5rem')
  })

  function openTools() {
    render(<><header><HeaderTools>
      <details><summary>Notifications</summary><button>Review activity</button></details>
      <details><summary>App settings</summary><button>Quit development</button></details>
      <button>Commands</button>
    </HeaderTools></header><button>Workspace action</button></>)
    return {
      notifications: screen.getByText('Notifications').closest('details')!,
      settings: screen.getByText('App settings').closest('details')!
    }
  }

  it('keeps only the chosen header menu open', async () => {
    const { notifications, settings } = openTools()
    fireEvent.click(screen.getByText('Notifications'))
    expect(screen.getByRole('navigation', { name: 'TogetherServer navigation' })).toHaveClass('header-tools')
    expect(notifications).toHaveAttribute('open')
    fireEvent.click(screen.getByText('App settings'))
    await waitFor(() => expect(notifications).not.toHaveAttribute('open'))
    expect(settings).toHaveAttribute('open')
  })

  it('dismisses with Escape before workspace shortcuts and returns focus to its summary', () => {
    const { notifications } = openTools()
    const workspaceShortcut = vi.fn()
    window.addEventListener('keydown', workspaceShortcut)
    try {
      fireEvent.click(screen.getByText('Notifications'))
      screen.getByRole('button', { name: 'Review activity' }).focus()
      fireEvent.keyDown(document.activeElement!, { key: 'Escape' })
      expect(notifications).not.toHaveAttribute('open')
      expect(screen.getByText('Notifications')).toHaveFocus()
      expect(workspaceShortcut).not.toHaveBeenCalled()
    } finally {
      window.removeEventListener('keydown', workspaceShortcut)
    }
  })

  it('closes when clicking outside or tabbing to another action without redirecting focus', () => {
    const { notifications } = openTools()
    fireEvent.click(screen.getByText('Notifications'))
    fireEvent.pointerDown(screen.getByRole('button', { name: 'Workspace action' }))
    expect(notifications).not.toHaveAttribute('open')
    fireEvent.click(screen.getByText('Notifications'))
    screen.getByRole('button', { name: 'Commands' }).focus()
    expect(notifications).not.toHaveAttribute('open')
    expect(screen.getByRole('button', { name: 'Commands' })).toHaveFocus()
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

  it('keeps keyboard selection in view for long command lists and runs the selected command', () => {
    const scrolled: Array<{ selected: string | null; options: ScrollIntoViewOptions }> = []
    const previousScroll = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollIntoView')
    const scroll = vi.fn(function (this: HTMLElement, options: ScrollIntoViewOptions) {
      scrolled.push({ selected: this.getAttribute('aria-selected'), options: options as ScrollIntoViewOptions })
    })
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', { configurable: true, value: scroll })
    const runLast = vi.fn()
    const commands: WorkspaceCommand[] = Array.from({ length: 32 }, (_, index) => ({
      id: `server-${index}`, label: `Server ${index} · Settings and files`, detail: 'Long saved server label', icon: 'server',
      run: index === 31 ? runLast : vi.fn()
    }))
    try {
      render(<CommandPalette open commands={commands} onClose={vi.fn()} />)
      const search = screen.getByLabelText('Search commands')
      for (let index = 0; index < 31; index++) fireEvent.keyDown(search, { key: 'ArrowDown' })
      expect(search).toHaveAttribute('aria-activedescendant', 'workspace-command-server-31')
      expect(scrolled.at(-1)).toEqual({ selected: 'true', options: { block: 'nearest' } })
      expect(scroll).toHaveBeenCalledTimes(32)
      fireEvent.keyDown(search, { key: 'Enter' })
      expect(runLast).toHaveBeenCalledOnce()
    } finally {
      if (previousScroll) Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', previousScroll)
      else Reflect.deleteProperty(HTMLElement.prototype, 'scrollIntoView')
    }
  })

  it('has a visible Close action and recovers keyboard selection after an empty search', () => {
    const close = vi.fn()
    const run = vi.fn()
    render(<CommandPalette open commands={[{ id: 'host', label: 'Open Host', detail: 'Servers', icon: 'server', run }]} onClose={close} />)
    const search = screen.getByLabelText('Search commands')
    fireEvent.change(search, { target: { value: 'missing' } })
    fireEvent.keyDown(search, { key: 'ArrowDown' })
    fireEvent.keyDown(search, { key: 'Enter' })
    expect(run).not.toHaveBeenCalled()
    fireEvent.change(search, { target: { value: 'host' } })
    fireEvent.keyDown(search, { key: 'Enter' })
    expect(run).toHaveBeenCalledOnce()
    fireEvent.click(screen.getByRole('button', { name: 'Close commands' }))
    expect(close).toHaveBeenCalledTimes(2)
  })
})
