import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { Button } from './Controls'
import { Icon } from './Icon'
import { SettingsDialog } from './SettingsPresentation'

export type ConfirmationOptions = {
  title: string
  description: string
  confirmLabel: string
  destructive?: boolean
  cancelLabel?: string
}

type Request = { id: number; options: ConfirmationOptions; resolve: (answer: boolean) => void }

/** Render confirmationDialog once. Each request gets its own decision, in arrival order. */
export function useConfirmation(): {
  confirm: (options: ConfirmationOptions) => Promise<boolean>
  confirmationDialog: ReactNode
} {
  const queue = useRef<Request[]>([])
  const sequence = useRef(0)
  const mounted = useRef(true)
  const [current, setCurrent] = useState<Request | null>(null)

  useEffect(() => {
    mounted.current = true
    const pending = queue.current
    return () => {
      mounted.current = false
      pending.splice(0).forEach(request => request.resolve(false))
    }
  }, [])

  const confirm = useCallback((options: ConfirmationOptions) => new Promise<boolean>(resolve => {
    if (!mounted.current) { resolve(false); return }
    const request = { id: ++sequence.current, options, resolve }
    queue.current.push(request)
    if (queue.current.length === 1) setCurrent(request)
  }), [])

  const settle = (id: number, answer: boolean) => {
    const request = queue.current[0]
    if (!request || request.id !== id) return
    queue.current.shift()
    request.resolve(answer)
    setCurrent(queue.current[0] ?? null)
  }

  const options = current?.options
  const confirmationDialog = current && options ? <SettingsDialog key={current.id}
    title={options.title} description={options.description} onClose={() => settle(current.id, false)}
    footer={<>
      <Button className="secondary" onClick={() => settle(current.id, false)}>{options.cancelLabel ?? 'Cancel'}</Button>
      <Button className={options.destructive ? 'danger' : 'primary'} onClick={() => settle(current.id, true)}>
        <Icon name={options.destructive ? 'warning' : 'check'} />{options.confirmLabel}
      </Button>
    </>}><div className={`confirmation-summary${options.destructive ? ' confirmation-destructive' : ''}`}>
      <Icon name={options.destructive ? 'warning' : 'check'} size={22} />
      <p>{options.destructive ? 'Review the consequences above before continuing.' : 'Continue when you are ready.'}</p>
    </div></SettingsDialog> : null

  return { confirm, confirmationDialog }
}
