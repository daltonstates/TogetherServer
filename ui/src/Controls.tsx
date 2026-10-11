import type {
  ButtonHTMLAttributes,
  InputHTMLAttributes,
  SelectHTMLAttributes,
  TextareaHTMLAttributes
} from 'react'
import { forwardRef } from 'react'
import { Tooltip } from './Tooltip'

function classes(base: string, className?: string) {
  return className ? `${base} ${className}` : base
}

export const Button = forwardRef<HTMLButtonElement, ButtonHTMLAttributes<HTMLButtonElement>>(function Button(
  { className, type = 'button', title, ...props }, ref
) {
  const button = <button ref={ref} type={type} className={classes('ui-button', className)} {...props} />
  return title ? <Tooltip content={title}>{button}</Tooltip> : button
})

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(function Input(
  { className, ...props }, ref
) {
  return <input ref={ref} className={classes('ui-input', className)} {...props} />
})

export function Select({ className, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select className={classes('ui-select', className)} {...props} />
}

export function TextArea({ className, ...props }: TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea className={classes('ui-textarea', className)} {...props} />
}
