import { cloneElement, useCallback, useId, useLayoutEffect, useRef, useState, type CSSProperties, type HTMLAttributes, type ReactElement, type Ref } from 'react'
import { createPortal } from 'react-dom'

type TriggerProps = HTMLAttributes<HTMLElement> & { ref?: Ref<HTMLElement> }

let dismissActiveTooltip: (() => void) | null = null

/** A single trigger keeps its layout, events and ref; the tip stays in a modal's top layer. */
export function Tooltip({ content, children }: { content: string; children: ReactElement<TriggerProps> }) {
  const id = useId()
  const triggerRef = useRef<HTMLElement | null>(null)
  const tipRef = useRef<HTMLDivElement | null>(null)
  const hovered = useRef(false)
  const focused = useRef(false)
  const touched = useRef(false)
  const [open, setOpen] = useState(false)
  const [position, setPosition] = useState({ left: 0, top: 0, tail: 12, side: 'bottom' })
  const childRef = children.props.ref
  const ref = useCallback((node: HTMLElement | null) => {
    triggerRef.current = node
    if (typeof childRef === 'function') return childRef(node)
    if (childRef) childRef.current = node
  }, [childRef])
  const dismiss = useCallback(() => {
    hovered.current = focused.current = touched.current = false
    setOpen(false)
  }, [])

  useLayoutEffect(() => {
    if (!open) return
    if (dismissActiveTooltip !== dismiss) dismissActiveTooltip?.()
    dismissActiveTooltip = dismiss
    const positionTip = () => {
      const trigger = triggerRef.current
      const tip = tipRef.current
      if (!trigger?.isConnected || !tip) return dismiss()
      const anchor = trigger.getBoundingClientRect()
      const bounds = tip.getBoundingClientRect()
      const margin = 8, gap = 9
      const width = bounds.width, height = bounds.height
      const center = anchor.left + anchor.width / 2
      const left = Math.max(margin, Math.min(center - width / 2, window.innerWidth - width - margin))
      const above = anchor.top - gap - height
      const below = anchor.bottom + gap
      const side = below + height > window.innerHeight - margin && above >= margin ? 'top' : 'bottom'
      const top = Math.max(margin, Math.min(side === 'top' ? above : below, window.innerHeight - height - margin))
      setPosition({ left, top, side, tail: Math.max(10, Math.min(center - left, width - 10)) })
    }
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      event.preventDefault()
      event.stopPropagation()
      dismiss()
    }
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !triggerRef.current?.contains(event.target)) dismiss()
    }
    positionTip()
    const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(positionTip) : null
    if (triggerRef.current) observer?.observe(triggerRef.current)
    if (tipRef.current) observer?.observe(tipRef.current)
    document.addEventListener('keydown', escape, true)
    document.addEventListener('pointerdown', outside)
    window.addEventListener('resize', positionTip)
    window.addEventListener('scroll', dismiss, true)
    window.addEventListener('blur', dismiss)
    return () => {
      if (dismissActiveTooltip === dismiss) dismissActiveTooltip = null
      observer?.disconnect()
      document.removeEventListener('keydown', escape, true)
      document.removeEventListener('pointerdown', outside)
      window.removeEventListener('resize', positionTip)
      window.removeEventListener('scroll', dismiss, true)
      window.removeEventListener('blur', dismiss)
    }
  }, [open, content, dismiss])

  const props = children.props
  const trigger = cloneElement(children, {
    ref,
    title: undefined,
    'aria-describedby': [props['aria-describedby'], open ? id : undefined].filter(Boolean).join(' ') || undefined,
    onPointerEnter: event => { props.onPointerEnter?.(event); if (event.pointerType !== 'touch') { hovered.current = true; setOpen(true) } },
    onPointerLeave: event => { props.onPointerLeave?.(event); hovered.current = false; if (!focused.current && !touched.current) setOpen(false) },
    onMouseEnter: event => { props.onMouseEnter?.(event); hovered.current = true; setOpen(true) },
    onMouseLeave: event => { props.onMouseLeave?.(event); hovered.current = false; if (!focused.current && !touched.current) setOpen(false) },
    onFocus: event => { props.onFocus?.(event); focused.current = true; setOpen(true) },
    onBlur: event => { props.onBlur?.(event); focused.current = false; touched.current = false; if (!hovered.current) setOpen(false) },
    onPointerDown: event => {
      props.onPointerDown?.(event)
      if (event.pointerType === 'touch') { touched.current = !touched.current; setOpen(touched.current) }
    }
  })
  return <>{trigger}{open && createPortal(<div ref={tipRef} id={id} role="tooltip" className="ui-tooltip"
    data-side={position.side} style={{ left: position.left, top: position.top, '--tooltip-tail': `${position.tail}px` } as CSSProperties}>
    {content}
  </div>, triggerRef.current?.closest('dialog[open]') ?? document.body)}</>
}
