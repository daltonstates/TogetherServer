import { useEffect, useId, useRef, type ReactNode } from 'react'
import { Button } from './Controls'
import { Icon, type IconName } from './Icon'

export function SettingsCard({ icon, title, description, summary, actionLabel, onOpen, disabled = false }: {
  icon: IconName
  title: string
  description?: string
  summary?: ReactNode
  actionLabel: string
  onOpen: () => void
  disabled?: boolean
}) {
  const id = useId()
  const hasSummary = summary !== undefined && summary !== null && summary !== false
  const descriptions = [description && `${id}-description`, hasSummary && `${id}-summary`].filter(Boolean).join(' ')

  return <Button className="settings-card" disabled={disabled} onClick={onOpen}
    aria-labelledby={`${id}-title ${id}-action`} aria-describedby={descriptions || undefined}>
    <span className="settings-card-icon"><Icon name={icon} size={21} /></span>
    <span className="settings-card-content">
      <span className="settings-card-title" id={`${id}-title`}>{title}</span>
      {description && <span className="settings-card-description" id={`${id}-description`}>{description}</span>}
      {hasSummary && <span className="settings-card-summary" id={`${id}-summary`}>{summary}</span>}
      <span className="settings-card-action" id={`${id}-action`}>{actionLabel}<Icon name="chevronRight" size={16} /></span>
    </span>
  </Button>
}

// Mount only while open. Keeping the native dialog open is independent of changing
// callbacks and pending actions, so refreshing a settings snapshot preserves focus.
export function SettingsDialog({ title, description, onClose, busy = false, children, footer, wide = false }: {
  title: string
  description?: string
  onClose: () => void
  busy?: boolean
  children: ReactNode
  footer?: ReactNode
  wide?: boolean
}) {
  const id = useId()
  const dialogRef = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = dialogRef.current
    if (!dialog) return
    const activeElement = document.activeElement
    const opener = activeElement instanceof HTMLElement && activeElement !== document.body ? activeElement : null

    if (typeof dialog.showModal === 'function') dialog.showModal()
    else {
      dialog.setAttribute('open', '')
      dialog.querySelector<HTMLButtonElement>('.settings-dialog-close:not(:disabled)')?.focus({ preventScroll: true })
    }

    return () => {
      if (dialog.open) {
        if (typeof dialog.close === 'function') dialog.close()
        else dialog.removeAttribute('open')
      }
      if (opener?.isConnected) opener.focus({ preventScroll: true })
    }
  }, [])

  return <dialog ref={dialogRef} className={`settings-dialog${wide ? ' settings-dialog-wide' : ''}`}
    aria-labelledby={`${id}-title`} aria-describedby={description ? `${id}-description` : undefined}
    aria-modal="true" aria-busy={busy || undefined} tabIndex={-1}
    onCancel={event => { event.preventDefault(); if (!busy) onClose() }}
    onKeyDown={event => {
      if (event.key === 'Escape') {
        event.stopPropagation()
        if (busy) event.preventDefault()
      }
    }}>
    <header className="settings-dialog-header">
      <div className="settings-dialog-heading">
        <h2 id={`${id}-title`}>{title}</h2>
        {description && <p id={`${id}-description`}>{description}</p>}
      </div>
      <Button className="secondary settings-dialog-close" aria-label={`Close ${title}`} disabled={busy} onClick={onClose}>
        <Icon name="close" size={17} />Close
      </Button>
    </header>
    <div className="settings-dialog-body">{children}</div>
    {footer !== undefined && footer !== null && <footer className="settings-dialog-footer"><div className="actions">{footer}</div></footer>}
  </dialog>
}

export function SettingsSectionHeading({ icon, title, description }: {
  icon: IconName
  title: string
  description?: string
}) {
  return <div className="settings-section-heading">
    <span className="settings-section-icon"><Icon name={icon} size={21} /></span>
    <div><h2>{title}</h2>{description && <p>{description}</p>}</div>
  </div>
}
