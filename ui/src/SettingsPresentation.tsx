import { useEffect, useId, useRef, type ReactNode } from 'react'
import { Button } from './Controls'
import { Icon, type IconName } from './Icon'

const focusableSelector = 'button:not(:disabled), input:not(:disabled):not([type="hidden"]), select:not(:disabled), textarea:not(:disabled), a[href], summary, [tabindex]:not([tabindex="-1"])'

function availableFocusTarget(element: HTMLElement, root: HTMLElement) {
  if (!root.contains(element) || element.matches(':disabled') || element.closest('[hidden], [inert], [aria-hidden="true"]')) return false
  for (let ancestor: HTMLElement | null = element; ancestor && ancestor !== root; ancestor = ancestor.parentElement) {
    if (ancestor instanceof HTMLDetailsElement && !ancestor.open && !ancestor.querySelector(':scope > summary')?.contains(element)) return false
    const style = getComputedStyle(ancestor)
    if (style.display === 'none' || style.visibility === 'hidden') return false
  }
  return true
}

function focusableElements(root: HTMLElement) {
  return Array.from(root.querySelectorAll<HTMLElement>(focusableSelector))
    .filter(element => element.tabIndex >= 0 && availableFocusTarget(element, root))
    .sort((first, second) => first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1)
}

function focusMeaningfulAction(dialog: HTMLDialogElement) {
  const body = dialog.querySelector<HTMLElement>('.settings-dialog-body')
  const footer = dialog.querySelector<HTMLElement>('.settings-dialog-footer')
  const bodyControls = body ? focusableElements(body) : []
  const target = bodyControls.find(element => element.tagName !== 'SUMMARY' && !element.classList.contains('notice-dismiss'))
    || (footer && focusableElements(footer)[0]) || bodyControls[0] || dialog
  target.focus({ preventScroll: true })
}

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

  return <Button className="settings-card settings-card-compact" disabled={disabled} onClick={onOpen} title={actionLabel}
    aria-labelledby={`${id}-title ${id}-action`} aria-describedby={descriptions || undefined}>
    <span className="settings-card-icon"><Icon name={icon} size={21} /></span>
    <span className="settings-card-content">
      <span className="settings-card-title" id={`${id}-title`}>{title}</span>
      {description && <span className={hasSummary ? 'sr-only' : 'settings-card-description'} id={`${id}-description`}>{description}</span>}
      {hasSummary && <span className="settings-card-summary" id={`${id}-summary`}>{summary}</span>}
      <span className="sr-only" id={`${id}-action`}>{actionLabel}</span>
    </span>
    <span className="settings-card-chevron"><Icon name="chevronRight" size={17} /></span>
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
    }
    focusMeaningfulAction(dialog)

    // Native dialog is not available in some embedded/test environments.
    const containFocus = () => {
      const topDialog = Array.from(document.querySelectorAll('dialog[open]')).at(-1)
      if (topDialog !== dialog) return
      const active = document.activeElement
      if (active !== dialog && (!(active instanceof HTMLElement) || !availableFocusTarget(active, dialog)))
        focusMeaningfulAction(dialog)
    }
    document.addEventListener('focusin', containFocus)
    // Disabling or removing a focused Save can leave focus on BODY without a
    // focusin event. Cover leaf-editor updates as well as parent busy changes.
    const focusObserver = new MutationObserver(containFocus)
    focusObserver.observe(dialog, { childList: true, subtree: true, attributes: true,
      attributeFilter: ['disabled', 'hidden', 'inert', 'aria-hidden', 'open', 'class', 'style'] })

    return () => {
      document.removeEventListener('focusin', containFocus)
      focusObserver.disconnect()
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
        event.preventDefault()
        event.stopPropagation()
        if (!busy) onClose()
      }
      if (event.key === 'Tab') {
        const controls = focusableElements(event.currentTarget)
        const first = controls[0], last = controls.at(-1)
        if (!first) { event.preventDefault(); event.currentTarget.focus(); return }
        const active = document.activeElement
        if (event.shiftKey && (active === first || !controls.includes(active as HTMLElement))) {
          event.preventDefault(); last?.focus()
        } else if (!event.shiftKey && (active === last || !controls.includes(active as HTMLElement))) {
          event.preventDefault(); first.focus()
        }
      }
    }}>
    <header className="settings-dialog-header">
      <div className="settings-dialog-heading">
        <h2 id={`${id}-title`}>{title}</h2>
        {description && <p id={`${id}-description`}>{description}</p>}
      </div>
      <Button className="secondary icon-button settings-dialog-close" aria-label={`Close ${title}`} title={`Close ${title}`} disabled={busy} onClick={onClose}>
        <Icon name="close" size={17} />
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
