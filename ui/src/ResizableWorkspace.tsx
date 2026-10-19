import { useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'

const minimumWidth = 200
const maximumWidth = 520
const defaultWidth = 268
const contentReserve = 372 // A 12px divider and at least 360px of content.

function savedWidth(key: string): number {
  try {
    const saved = window.localStorage.getItem(key)
    const width = saved === null ? NaN : Number(saved)
    if (Number.isFinite(width) && width >= minimumWidth && width <= maximumWidth) return Math.round(width)
  } catch { /* Optional layout preferences must not block the app. */ }
  return defaultWidth
}

/** A shared split pane; Host and Join deliberately share the same saved width. */
export function ResizableWorkspace({ area, label, sidebarLabel, sidebar, children, className = '', contentClassName = '' }: {
  area: 'servers' | 'settings'
  label?: string
  sidebarLabel: string
  sidebar: ReactNode
  children: ReactNode
  className?: string
  contentClassName?: string
}) {
  const storageKey = `togetherserver.sidebar-width.${area}.v1`
  const [preferredWidth, setPreferredWidth] = useState(() => savedWidth(storageKey))
  const [availableWidth, setAvailableWidth] = useState(maximumWidth)
  const [dragging, setDragging] = useState(false)
  const workspaceRef = useRef<HTMLElement>(null)
  const drag = useRef<{ pointerId: number; x: number; width: number } | null>(null)
  const sidebarId = useId()
  const width = Math.min(preferredWidth, availableWidth)
  const resizeLabel = area === 'servers' ? 'Server sidebar width' : 'App settings sidebar width'

  useLayoutEffect(() => {
    const workspace = workspaceRef.current
    if (!workspace) return
    const measure = () => {
      const measured = workspace.getBoundingClientRect().width
      if (measured > 0) setAvailableWidth(Math.max(minimumWidth, Math.min(maximumWidth, Math.floor(measured - contentReserve))))
    }
    const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(measure) : null
    observer?.observe(workspace)
    window.addEventListener('resize', measure)
    measure()
    return () => { observer?.disconnect(); window.removeEventListener('resize', measure) }
  }, [])

  useEffect(() => {
    try { window.localStorage.setItem(storageKey, String(preferredWidth)) }
    catch { /* Resizing still works when storage is unavailable. */ }
  }, [storageKey, preferredWidth])

  const resize = (next: number) => setPreferredWidth(Math.round(Math.max(minimumWidth, Math.min(availableWidth, next))))
  const endDrag = () => { drag.current = null; setDragging(false) }

  return <section ref={workspaceRef} className={`context-workspace resizable-workspace ${className}`} aria-label={label}
    style={{ '--sidebar-width': `${width}px` } as CSSProperties} data-resizing={dragging || undefined}>
    <aside id={sidebarId} className="context-sidebar" aria-label={sidebarLabel} tabIndex={0}>{sidebar}</aside>
    <div className="sidebar-resizer" role="separator" tabIndex={0} aria-label={resizeLabel} aria-orientation="vertical"
      aria-controls={sidebarId} aria-valuemin={minimumWidth} aria-valuemax={availableWidth} aria-valuenow={width} aria-valuetext={`${width} pixels`}
      title="Drag to resize. Use arrow keys, or double-click to reset."
      onDoubleClick={() => setPreferredWidth(defaultWidth)}
      onKeyDown={event => {
        const step = event.shiftKey ? 40 : 10
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
        event.preventDefault()
        resize(event.key === 'Home' ? minimumWidth : event.key === 'End' ? availableWidth : width + (event.key === 'ArrowRight' ? step : -step))
      }}
      onPointerDown={event => {
        if (event.button !== 0 || drag.current) return
        event.preventDefault()
        event.currentTarget.focus()
        drag.current = { pointerId: event.pointerId, x: event.clientX, width }
        event.currentTarget.setPointerCapture?.(event.pointerId)
        setDragging(true)
      }}
      onPointerMove={event => {
        const current = drag.current
        if (current && current.pointerId === event.pointerId) resize(current.width + event.clientX - current.x)
      }}
      onPointerUp={event => { if (drag.current?.pointerId === event.pointerId) endDrag() }}
      onPointerCancel={event => { if (drag.current?.pointerId === event.pointerId) endDrag() }} onLostPointerCapture={endDrag}>
      <span aria-hidden="true" />
    </div>
    <div className={`context-content ${contentClassName}`}>{children}</div>
  </section>
}
