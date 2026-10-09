import { useEffect, useMemo, useRef, useState } from 'react'
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

export function WorkspaceNavigation({ page, activeRuns, unread, onNavigate }: {
  page: WorkspacePage
  activeRuns: number
  unread: boolean
  onNavigate: (page: WorkspacePage) => void
}) {
  return <nav className="workspace-nav" aria-label="TogetherServer workspaces">
    {navigation.map(item => <Button key={item.id} className={page === item.id ? 'workspace-nav-item selected' : 'workspace-nav-item'}
      aria-label={item.label} aria-current={page === item.id ? 'page' : undefined} title={`${item.label} (${item.shortcut})`} onClick={() => onNavigate(item.id)}>
      <span className="workspace-nav-icon"><Icon name={item.icon} /></span>
      <span className="workspace-nav-label">{item.label}</span>
      {item.id === 'host' && activeRuns > 0 && <span className="workspace-nav-count" aria-label={`${activeRuns} running`}>{activeRuns}</span>}
      {item.id === 'attention' && unread && <span className="workspace-nav-unread"><span className="sr-only">New activity</span></span>}
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
  const [query, setQuery] = useState('')
  const [activeIndex, setActiveIndex] = useState(0)
  const visible = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase()
    return needle ? commands.filter(command => `${command.label} ${command.detail} ${command.keywords ?? ''}`.toLocaleLowerCase().includes(needle)) : commands
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
          if (event.key === 'ArrowDown') { event.preventDefault(); setActiveIndex(index => Math.min(index + 1, visible.length - 1)) }
          if (event.key === 'ArrowUp') { event.preventDefault(); setActiveIndex(index => Math.max(index - 1, 0)) }
          if (event.key === 'Enter') { event.preventDefault(); run(visible[activeIndex]) }
        }} />
      <kbd>Esc</kbd>
    </div>
    <h2 id="command-palette-title" className="sr-only">Command palette</h2>
    <div className="command-list" id="workspace-command-options" role="listbox" aria-label="Commands">
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
