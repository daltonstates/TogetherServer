import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react'
import { Button, Input } from './Controls'
import { Icon, type IconName } from './Icon'

export type WorkspacePage = 'host' | 'join' | 'attention' | 'settings'

export type WorkspaceCommand = {
  id: string
  label: string
  detail: string
  icon: IconName
  keywords?: string
  disabled?: boolean
  run: () => void
}

const navigation: Array<{ id: WorkspacePage; label: string; icon: IconName; shortcut: string }> = [
  { id: 'host', label: 'Host', icon: 'server', shortcut: 'Alt+1' },
  { id: 'join', label: 'Join', icon: 'link', shortcut: 'Alt+2' },
  { id: 'attention', label: 'Attention', icon: 'bell', shortcut: 'Alt+3' },
  { id: 'settings', label: 'Settings', icon: 'settings', shortcut: 'Alt+4' }
]

/** Header disclosures share dismissal, focus recovery and the available viewport height. */
export function HeaderTools({ children }: { children: ReactNode }) {
  const toolsRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    const tools = toolsRef.current
    if (!tools) return
    const menus = () => Array.from(tools.querySelectorAll<HTMLDetailsElement>(':scope > details'))
    const closeOutside = (event: Event) => {
      if (!(event.target instanceof Node)) return
      const target = event.target
      menus().forEach(menu => { if (!menu.contains(target)) menu.open = false })
    }
    const updateHeight = () => {
      tools.style.setProperty('--header-menu-height', `${Math.max(0, window.innerHeight - tools.getBoundingClientRect().bottom - 22)}px`)
    }
    const keepOneOpen = (event: Event) => {
      const opened = event.target
      if (!(opened instanceof HTMLDetailsElement) || !opened.open) return
      menus().forEach(menu => { if (menu !== opened) menu.open = false })
      updateHeight()
    }
    const dismiss = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      const opened = menus().find(menu => menu.open)
      if (!opened) return
      event.preventDefault()
      event.stopPropagation()
      menus().forEach(menu => { menu.open = false })
      opened.querySelector('summary')?.focus()
    }
    tools.addEventListener('toggle', keepOneOpen, true)
    tools.addEventListener('click', closeOutside, true)
    document.addEventListener('pointerdown', closeOutside)
    document.addEventListener('focusin', closeOutside)
    document.addEventListener('keydown', dismiss, true)
    window.addEventListener('resize', updateHeight)
    const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(updateHeight) : null
    observer?.observe(tools.closest('header') ?? tools)
    updateHeight()
    return () => {
      tools.removeEventListener('toggle', keepOneOpen, true)
      tools.removeEventListener('click', closeOutside, true)
      document.removeEventListener('pointerdown', closeOutside)
      document.removeEventListener('focusin', closeOutside)
      document.removeEventListener('keydown', dismiss, true)
      window.removeEventListener('resize', updateHeight)
      observer?.disconnect()
    }
  }, [])

  return <div ref={toolsRef} className="header-tools">{children}</div>
}

export function WorkspaceNavigation({ page, activeRuns, unread, onNavigate }: {
  page: WorkspacePage
  activeRuns: number
  unread: boolean
  onNavigate: (page: WorkspacePage) => void
}) {
  const statusId = useId()
  return <nav className="workspace-nav" aria-label="TogetherServer workspaces">
    {navigation.map(item => <Button key={item.id} className={page === item.id ? 'workspace-nav-item selected' : 'workspace-nav-item'}
      aria-label={item.label} aria-current={page === item.id ? 'page' : undefined}
      aria-describedby={item.id === 'host' && activeRuns > 0 ? `${statusId}-running` : item.id === 'attention' && unread ? `${statusId}-unread` : undefined}
      title={`${item.label} (${item.shortcut})`} onClick={() => onNavigate(item.id)}>
      <span className="workspace-nav-icon"><Icon name={item.icon} /></span>
      <span className="workspace-nav-label">{item.label}</span>
      {item.id === 'host' && activeRuns > 0 && <span className="workspace-nav-count" aria-label={`${activeRuns} running`}>{activeRuns}<span className="sr-only" id={`${statusId}-running`}>{activeRuns} servers running</span></span>}
      {item.id === 'attention' && unread && <span className="workspace-nav-unread"><span className="sr-only" id={`${statusId}-unread`}>New activity</span></span>}
    </Button>)}
  </nav>
}

export function CommandPalette({ open, commands, onClose }: {
  open: boolean
  commands: WorkspaceCommand[]
  onClose: () => void
}) {
  const dialogRef = useRef<HTMLDialogElement | null>(null)
  const inputRef = useRef<HTMLInputElement | null>(null)
  const listRef = useRef<HTMLDivElement | null>(null)
  const [query, setQuery] = useState('')
  const [activeIndex, setActiveIndex] = useState(0)
  const visible = useMemo(() => {
    const words = query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean)
    return words.length ? commands.filter(command => {
      const description = `${command.label} ${command.detail} ${command.keywords ?? ''}`.toLocaleLowerCase()
      return words.every(word => description.includes(word))
    }) : commands
  }, [commands, query])

  useEffect(() => {
    const dialog = dialogRef.current
    if (!dialog) return
    if (open && !dialog.open) {
      if (typeof dialog.showModal === 'function') dialog.showModal()
      else dialog.setAttribute('open', '')
      setQuery('')
      setActiveIndex(0)
      window.setTimeout(() => inputRef.current?.focus(), 0)
    } else if (!open && dialog.open) {
      if (typeof dialog.close === 'function') dialog.close()
      else dialog.removeAttribute('open')
    }
  }, [open])

  useEffect(() => setActiveIndex(index => Math.min(index, Math.max(visible.length - 1, 0))), [visible.length])
  const activeCommandId = visible[activeIndex]?.id
  useEffect(() => {
    if (open) listRef.current?.querySelector<HTMLElement>('[aria-selected="true"]')?.scrollIntoView?.({ block: 'nearest' })
  }, [open, activeCommandId, activeIndex])

  const run = (command: WorkspaceCommand | undefined) => {
    if (!command || command.disabled) return
    command.run()
    onClose()
  }

  return <dialog ref={dialogRef} className="command-palette" aria-labelledby="command-palette-title"
    onCancel={event => { event.preventDefault(); onClose() }} onClose={onClose}>
    <div className="command-palette-search">
      <Icon name="search" size={18} />
      <Input ref={inputRef} value={query} onChange={event => { setQuery(event.target.value); setActiveIndex(0) }}
        aria-label="Search commands" placeholder="Type a server, command, or workspace"
        role="combobox" aria-autocomplete="list" aria-expanded={open} aria-controls="workspace-command-options"
        aria-activedescendant={visible[activeIndex] ? `workspace-command-${visible[activeIndex].id}` : undefined}
        onKeyDown={event => {
          if (event.key === 'ArrowDown') { event.preventDefault(); setActiveIndex(index => Math.min(index + 1, Math.max(0, visible.length - 1))) }
          if (event.key === 'ArrowUp') { event.preventDefault(); setActiveIndex(index => Math.max(index - 1, 0)) }
          if (event.key === 'Enter') { event.preventDefault(); run(visible[activeIndex]) }
        }} />
      <Button className="secondary command-close" aria-label="Close commands" onClick={onClose}>Close</Button>
    </div>
    <h2 id="command-palette-title" className="sr-only">Command palette</h2>
    <div ref={listRef} className="command-list" id="workspace-command-options" role="listbox" aria-label="Commands">
      {visible.map((command, index) => <Button key={command.id} id={`workspace-command-${command.id}`} role="option" aria-selected={index === activeIndex}
        className={index === activeIndex ? 'command-item selected' : 'command-item'} aria-disabled={command.disabled} tabIndex={-1}
        onMouseEnter={() => setActiveIndex(index)} onClick={() => run(command)}>
        <span className="command-icon"><Icon name={command.icon} /></span>
        <span><strong>{command.label}</strong><small>{command.detail}</small></span>
      </Button>)}
      {visible.length === 0 && <p className="command-empty">No commands match “{query}”.</p>}
    </div>
    <div className="command-palette-footer"><span><kbd>↑</kbd><kbd>↓</kbd> move</span><span><kbd>Enter</kbd> open</span><span>Ctrl+K from anywhere</span></div>
  </dialog>
}

export function StatusStrip({ hostText, friendText, pending, version }: {
  hostText: string
  friendText: string
  pending: string
  version: string
}) {
  return <footer className="status-strip" aria-label="Application status">
    <span><span className="status-strip-dot" />{hostText}</span>
    <span><Icon name="link" size={13} />{friendText}</span>
    <span className="status-strip-spacer" />
    {pending && <span className="status-strip-busy" role="status" aria-live="polite"><Icon name="loader" size={13} />{pending}</span>}
    <span>v{version}</span>
  </footer>
}
