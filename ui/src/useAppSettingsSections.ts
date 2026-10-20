import { useCallback, useEffect, useRef, useState, type RefObject } from 'react'
import type { AppSettingsSection } from './AppSettings'

export type AppSettingsScrollRequest = { section: AppSettingsSection; requestId: number }

const sectionInset = 16

/** Settings use the content pane on desktop and the outer workspace on narrow windows. */
function scrollContainer(page: HTMLElement): HTMLElement {
  for (let parent = page.parentElement; parent; parent = parent.parentElement) {
    if (/(auto|scroll)/.test(getComputedStyle(parent).overflowY)) return parent
  }
  return (document.scrollingElement ?? document.documentElement) as HTMLElement
}

function viewport(root: HTMLElement) {
  const documentRoot = root === document.scrollingElement || root === document.documentElement
  return {
    top: documentRoot ? 0 : root.getBoundingClientRect().top + root.clientTop,
    height: documentRoot ? window.innerHeight : root.clientHeight,
    inset: Math.max(sectionInset, parseFloat(getComputedStyle(root).paddingTop) || 0)
  }
}

export function useAppSettingsSections(pageRef: RefObject<HTMLElement | null>, initialSection: AppSettingsSection,
  onSectionChange?: (section: AppSettingsSection) => void, scrollRequest?: AppSettingsScrollRequest) {
  const [visibleSection, setVisibleSection] = useState(initialSection)
  const reportedSection = useRef(initialSection)
  const report = useRef(onSectionChange)
  report.current = onSectionChange
  const request = useRef(scrollRequest)
  request.current = scrollRequest

  const showSection = useCallback((section: AppSettingsSection, behavior: ScrollBehavior, focus: boolean) => {
    const page = pageRef.current
    const target = page?.querySelector<HTMLElement>(`[data-settings-section="${section}"]`)
    if (!page || !target) return
    const root = scrollContainer(page)
    const bounds = viewport(root)
    const top = Math.max(0, Math.min(root.scrollHeight - bounds.height,
      root.scrollTop + target.getBoundingClientRect().top - bounds.top - bounds.inset))
    // Focusing the section makes the next Tab continue through the requested controls.
    // preventScroll keeps the chosen pane as the only scroll owner.
    if (focus) target.focus({ preventScroll: true })
    if (typeof root.scrollTo === 'function') root.scrollTo({ top, behavior })
    else root.scrollTop = top
  }, [pageRef])

  const jumpToSection = useCallback((section: AppSettingsSection) => {
    showSection(section, window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth', true)
  }, [showSection])

  useEffect(() => {
    const page = pageRef.current
    if (!page) return
    let root = scrollContainer(page)
    let frame = 0
    const measure = () => {
      frame = 0
      const bounds = viewport(root)
      if (bounds.height <= 0) return
      const blocks = Array.from(page.querySelectorAll<HTMLElement>('[data-settings-section]'))
      if (!blocks.length || blocks.every(block => block.getBoundingClientRect().height === 0)) return
      // A short final section cannot reach the top. At the scroll limit it owns the location.
      const atEnd = root.scrollHeight > bounds.height + 2 && root.scrollTop + bounds.height >= root.scrollHeight - 2
      let current = blocks[0]
      if (atEnd) current = blocks[blocks.length - 1]
      else for (const block of blocks) {
        if (block.getBoundingClientRect().top <= bounds.top + bounds.inset + 12) current = block
        else break
      }
      const next = current.dataset.settingsSection as AppSettingsSection
      if (next === reportedSection.current) return
      reportedSection.current = next
      setVisibleSection(next)
      report.current?.(next)
    }
    const schedule = () => {
      if (!frame) frame = window.requestAnimationFrame(measure)
    }
    const rebind = () => {
      const next = scrollContainer(page)
      if (root !== next) {
        root.removeEventListener('scroll', schedule)
        root = next
        root.addEventListener('scroll', schedule, { passive: true })
      }
      schedule()
    }
    root.addEventListener('scroll', schedule, { passive: true })
    // Document scrolling dispatches on document/window rather than the scrolling element.
    window.addEventListener('scroll', schedule, { passive: true })
    window.addEventListener('resize', rebind)
    const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(rebind) : null
    observer?.observe(page)
    observer?.observe(root)
    schedule()
    return () => {
      root.removeEventListener('scroll', schedule)
      window.removeEventListener('scroll', schedule)
      window.removeEventListener('resize', rebind)
      observer?.disconnect()
      if (frame) window.cancelAnimationFrame(frame)
    }
  }, [pageRef])

  const requestId = scrollRequest?.requestId
  useEffect(() => {
    if (requestId === undefined) return
    let secondFrame = 0
    // Parent workspace restoration runs on its first frame. Explicit navigation follows it.
    const firstFrame = window.requestAnimationFrame(() => {
      secondFrame = window.requestAnimationFrame(() => {
        if (request.current?.requestId === requestId) jumpToSection(request.current.section)
      })
    })
    return () => {
      window.cancelAnimationFrame(firstFrame)
      window.cancelAnimationFrame(secondFrame)
    }
  }, [requestId, jumpToSection])

  return { visibleSection, jumpToSection }
}
