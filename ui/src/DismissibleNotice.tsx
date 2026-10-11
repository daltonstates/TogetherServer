import { isValidElement, useRef, useState, type HTMLAttributes, type ReactNode } from 'react'
import { Button } from './Controls'
import { Icon } from './Icon'
import './DismissibleNotice.css'

function messageText(node: ReactNode): string {
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(messageText).join('|')
  return isValidElement<{ children?: ReactNode }>(node) ? messageText(node.props.children) : ''
}

type Props = HTMLAttributes<HTMLElement> & {
  as?: 'div' | 'p' | 'section' | 'aside' | 'article' | 'output' | 'small' | 'span'
  children: ReactNode
  noticeKey?: string | number | null
  trackMessage?: boolean
  scope?: string
  dismissLabel?: string
  onDismiss?: () => void
}

/** Dismisses presentation only. Source state and pending operations keep their own authority. */
export function DismissibleNotice({ as: Tag = 'div', children, className, noticeKey, scope,
  trackMessage = true, dismissLabel = 'Dismiss notification', onDismiss, ...attributes }: Props) {
  const rootRef = useRef<HTMLElement | null>(null)
  const [dismissed, setDismissed] = useState<string | null>(null)
  const signature = JSON.stringify([scope, noticeKey, trackMessage ? messageText(children) : null])
  if (dismissed === signature) return null
  const dismiss = () => {
    const notice = rootRef.current
    if (notice?.contains(document.activeElement)) {
      const boundary = notice.closest<HTMLElement>('dialog, main, .notification-panel') ?? notice.parentElement
      const controls = Array.from(boundary?.querySelectorAll<HTMLElement>('button, input, select, textarea, a[href], summary, [tabindex]') ?? [])
        .filter(element => {
          if (element.tabIndex < 0 || element.matches(':disabled') || notice.contains(element)
            || element.closest('[hidden], [inert], [aria-hidden="true"]')) return false
          for (let ancestor: HTMLElement | null = element; ancestor && ancestor !== boundary; ancestor = ancestor.parentElement) {
            if (ancestor instanceof HTMLDetailsElement && !ancestor.open
              && !ancestor.querySelector(':scope > summary')?.contains(element)) return false
            const style = getComputedStyle(ancestor)
            if (style.display === 'none' || style.visibility === 'hidden') return false
          }
          return true
        })
      const next = controls.find(element => notice.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING)
      const target = next ?? controls.at(-1) ?? boundary
      target?.focus({ preventScroll: true })
    }
    setDismissed(signature)
    onDismiss?.()
  }
  return <Tag {...attributes} ref={(element: HTMLElement | null) => { rootRef.current = element }} className={`dismissible-notice${className ? ` ${className}` : ''}`}>
    {children}
    <Button className="secondary icon-button notice-dismiss" aria-label={dismissLabel}
      onClick={dismiss}><Icon name="close" /></Button>
  </Tag>
}
