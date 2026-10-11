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
    const dismissOutside = (event: PointerEvent) => {
      if (event.target instanceof Node && ref.current?.open && !ref.current.contains(event.target)) ref.current.open = false
    }
    document.addEventListener('pointerdown', dismissOutside)
    return () => document.removeEventListener('pointerdown', dismissOutside)
  }, [])
  const selected = items.find(item => item.id === selectedId) ?? items[0]
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
