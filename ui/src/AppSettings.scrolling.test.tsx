import type { ComponentProps } from 'react'
import { act, fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { AppSettings } from './AppSettings'
import { defaultUiPreferences } from './qolPreferences'

vi.mock('./DesktopPreferencesPanel', () => ({
  DesktopPreferencesPanel: () => <p>Notification choices</p>,
  UpdateDetailsPanel: () => null
}))

function input(): ComponentProps<typeof AppSettings> {
  return { preferences: { ...defaultUiPreferences }, onPreferencesChange: vi.fn(),
    desktop: { available: true, launchAtLogin: false, closeToTray: false, startupAvailable: true },
    instance: null, update: null, busy: false, desktopBusy: false, updateBusy: false,
    onDesktopChange: vi.fn(), notice: null, onClearNotice: vi.fn(), onCheckUpdate: vi.fn(async () => false),
    onReviewUpdate: vi.fn(), onReloadUpdate: vi.fn(), onQuit: vi.fn() }
}

function scrollingPage(settings: ComponentProps<typeof AppSettings>) {
  let frameId = 0
  const frames = new Map<number, FrameRequestCallback>()
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => {
    frames.set(++frameId, callback)
    return frameId
  })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(id => { frames.delete(id) })
  const flush = () => act(() => {
    const current = [...frames.values()]
    frames.clear()
    for (const callback of current) callback(0)
  })
  const page = (next: ComponentProps<typeof AppSettings>) => <div data-testid="outer-scroll" style={{ overflowY: 'visible' }}>
    <div data-testid="content-scroll" style={{ overflowY: 'auto' }}><AppSettings {...next} /></div>
  </div>
  const view = render(page(settings))
  const outer = screen.getByTestId('outer-scroll')
  const content = screen.getByTestId('content-scroll')
  let activeRoot = content
  for (const root of [content, outer]) {
    Object.defineProperties(root, { clientHeight: { configurable: true, value: 500 }, scrollHeight: { configurable: true, value: 2350 } })
    vi.spyOn(root, 'getBoundingClientRect').mockImplementation(() => ({ top: root === content ? 80 : 0 } as DOMRect))
  }
  const offsets = [0, 300, 700, 1000, 1300, 1700, 2050]
  const blocks = screen.getAllByRole('region').filter(block => block.hasAttribute('data-settings-section'))
  blocks.forEach((block, index) => vi.spyOn(block, 'getBoundingClientRect').mockImplementation(() => ({
    top: activeRoot.getBoundingClientRect().top + 16 + offsets[index] - activeRoot.scrollTop,
    height: index === offsets.length - 1 ? 220 : 300
  } as DOMRect)))
  const contentScroll = vi.fn()
  const outerScroll = vi.fn()
  Object.defineProperty(content, 'scrollTo', { configurable: true, value: contentScroll })
  Object.defineProperty(outer, 'scrollTo', { configurable: true, value: outerScroll })
  const scroll = (top: number) => {
    activeRoot.scrollTop = top
    fireEvent.scroll(activeRoot)
    flush()
  }
  const narrow = () => {
    content.style.overflowY = 'visible'
    outer.style.overflowY = 'auto'
    activeRoot = outer
    fireEvent(window, new Event('resize'))
    flush()
  }
  return { ...view, flush, scroll, narrow, content, outer, contentScroll, outerScroll,
    rerenderSettings: (next: ComponentProps<typeof AppSettings>) => view.rerender(page(next)) }
}

describe('App Settings section scrolling', () => {
  it('tracks the content pane and the short final section without scrolling on passive reports or polling', () => {
    const settings = input()
    const onSectionChange = vi.fn()
    const view = scrollingPage({ ...settings, section: 'profile', onSectionChange })
    view.flush()
    expect(onSectionChange).not.toHaveBeenCalled()
    view.scroll(720)
    expect(onSectionChange).toHaveBeenLastCalledWith('notifications')
    view.rerenderSettings({ ...settings, section: 'notifications', onSectionChange,
      desktop: { ...settings.desktop!, closeToTray: true } })
    view.flush()
    expect(view.content.scrollTop).toBe(720)
    expect(view.contentScroll).not.toHaveBeenCalled()
    view.scroll(1850)
    expect(onSectionChange).toHaveBeenLastCalledWith('shortcuts')
    expect(view.outer.scrollTop).toBe(0)
    expect(view.outerScroll).not.toHaveBeenCalled()
  })

  it('smoothly jumps only for explicit requests and allows requesting the same section again', () => {
    const settings = input()
    const view = scrollingPage({ ...settings, section: 'profile' })
    view.flush()
    view.rerenderSettings({ ...settings, section: 'updates', scrollRequest: { section: 'updates', requestId: 1 } })
    view.flush(); view.flush()
    expect(view.contentScroll).toHaveBeenCalledExactlyOnceWith({ top: 1700, behavior: 'smooth' })
    expect(screen.getByRole('region', { name: 'App updates' })).toHaveFocus()
    view.rerenderSettings({ ...settings, section: 'windows', scrollRequest: { section: 'updates', requestId: 1 }, updateBusy: true })
    view.flush(); view.flush()
    expect(view.contentScroll).toHaveBeenCalledOnce()
    view.rerenderSettings({ ...settings, section: 'updates', scrollRequest: { section: 'updates', requestId: 2 } })
    view.flush(); view.flush()
    expect(view.contentScroll).toHaveBeenCalledTimes(2)
    expect(view.outerScroll).not.toHaveBeenCalled()
  })

  it('uses instant jumps for reduced motion and clamps the short last section to the scroll limit', () => {
    vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: true })))
    const view = scrollingPage(input())
    view.flush()
    fireEvent.click(screen.getByRole('button', { name: 'Keyboard shortcuts' }))
    expect(view.contentScroll).toHaveBeenCalledExactlyOnceWith({ top: 1850, behavior: 'instant' })
    expect(screen.getByRole('region', { name: 'Keyboard shortcuts' })).toHaveFocus()
  })

  it('switches tracking and jumps to the outer workspace when a narrow layout changes the scroll owner', () => {
    const onSectionChange = vi.fn()
    const view = scrollingPage({ ...input(), onSectionChange })
    view.flush()
    view.narrow()
    view.scroll(1320)
    expect(onSectionChange).toHaveBeenLastCalledWith('windows')
    expect(screen.getByRole('button', { name: 'Windows behavior' })).toHaveAttribute('aria-current', 'location')
    fireEvent.click(screen.getByRole('button', { name: 'App updates' }))
    expect(view.outerScroll).toHaveBeenCalledExactlyOnceWith({ top: 1700, behavior: 'smooth' })
    expect(view.contentScroll).not.toHaveBeenCalled()
    view.unmount()
    fireEvent.scroll(view.outer)
    view.flush()
    expect(onSectionChange).toHaveBeenCalledTimes(1)
  })
})
