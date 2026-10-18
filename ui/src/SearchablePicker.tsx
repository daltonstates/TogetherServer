import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Button, Input } from './Controls'
import { Icon } from './Icon'

export type PickerItem = { id: string; name: string; detail?: string; state?: string; keywords?: string }

export function SearchablePicker({ label, items, selectedId, onSelect, actions, tools }: {
  label: string; items: PickerItem[]; selectedId: string; onSelect: (id: string) => void;
  actions?: (item: PickerItem) => ReactNode; tools?: ReactNode
}) {
  const ref = useRef<HTMLDetailsElement>(null)
  const [query, setQuery] = useState('')
  useEffect(() => {
    const picker = ref.current
    if (!picker) return
    const position = () => {
      if (!picker.open) return
      const trigger = picker.querySelector('summary')
      const popover = picker.querySelector<HTMLElement>('.context-picker-popover')
      if (!trigger || !popover) return
      const anchor = trigger.getBoundingClientRect()
      const margin = 12, gap = 7
      const width = Math.min(Math.max(anchor.width, 320), window.innerWidth - margin * 2)
      const below = window.innerHeight - anchor.bottom - margin - gap
      const above = anchor.top - margin - gap
      const opensAbove = below < Math.min(popover.scrollHeight, 220) && above > below
      const height = Math.max(0, Math.min(420, opensAbove ? above : below))
      popover.style.setProperty('--picker-left', `${Math.max(margin, Math.min(anchor.left, window.innerWidth - width - margin))}px`)
      popover.style.setProperty('--picker-top', `${opensAbove ? Math.max(margin, anchor.top - gap - Math.min(popover.scrollHeight, height)) : anchor.bottom + gap}px`)
      popover.style.setProperty('--picker-width', `${width}px`)
      popover.style.setProperty('--picker-height', `${height}px`)
    }
    const dismissOutside = (event: PointerEvent) => {
      if (event.target instanceof Node && picker.open && !picker.contains(event.target)) picker.open = false
    }
    const dismissOnScroll = (event: Event) => {
      if (picker.open && event.target instanceof Element && !picker.contains(event.target)) picker.open = false
    }
    picker.addEventListener('toggle', position)
    const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(position) : null
    const trigger = picker.querySelector('summary')
    const popover = picker.querySelector('.context-picker-popover')
    if (trigger) observer?.observe(trigger)
    if (popover) observer?.observe(popover)
    document.addEventListener('pointerdown', dismissOutside)
    document.addEventListener('scroll', dismissOnScroll, true)
    window.addEventListener('resize', position)
    return () => {
      picker.removeEventListener('toggle', position)
      observer?.disconnect()
      document.removeEventListener('pointerdown', dismissOutside)
      document.removeEventListener('scroll', dismissOnScroll, true)
      window.removeEventListener('resize', position)
    }
  }, [])
  const selected = items.find(item => item.id === selectedId)
  const filtered = items.filter(item => `${item.name} ${item.detail ?? ''} ${item.keywords ?? ''}`
    .toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()))
  return <details ref={ref} className="context-picker" onKeyDown={event => {
    if (event.key === 'Escape' && ref.current?.open) {
      event.preventDefault(); event.stopPropagation(); ref.current.open = false
      ref.current.querySelector('summary')?.focus()
    }
  }}>
    <summary className="context-picker-trigger" aria-label={`${label}: ${selected?.name ?? 'None selected'}`}>
      <span><small>{label}</small><strong>{selected?.name ?? 'Choose a Host'}</strong></span><Icon name="chevronDown" />
    </summary>
    <div className="context-picker-popover">
      <label className="context-picker-search"><span className="sr-only">Find {label.toLocaleLowerCase()}</span>
        <Icon name="search" /><Input value={query} onChange={event => setQuery(event.target.value)} placeholder={`Find ${label.toLocaleLowerCase()}…`} />
      </label>
      <div className="context-picker-options" aria-label={`${label} choices`}>
        {filtered.map(item => <div className="context-picker-option" key={item.id}>
          <Button className={item.id === selected?.id ? 'selected secondary' : 'secondary'}
            aria-current={item.id === selected?.id ? 'true' : undefined} onClick={() => {
              onSelect(item.id)
              if (ref.current) { ref.current.open = false; ref.current.querySelector('summary')?.focus() }
            }}>
            <span><strong>{item.name}</strong>{item.detail && <small>{item.detail}</small>}</span>
            {item.state && <small>{item.state}</small>}
          </Button>{actions?.(item)}
        </div>)}
        {filtered.length === 0 && <p className="helper-text">No matches. Try a different name or game.</p>}
      </div>
      {tools && <div className="context-picker-tools">{tools}</div>}
    </div>
  </details>
}
