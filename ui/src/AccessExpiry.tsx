import { useId, useMemo, useState } from 'react'
import { changeJson, errorMessage } from './api'
import { Button, Input } from './Controls'
import {
  parseDeviceAccessExpiryResult,
  type Device,
  type DeviceAccessDuration,
  type DeviceAccessExpiryRequest,
  type DeviceAccessExpiryResult
} from './contracts'

export const accessDurationChoices: ReadonlyArray<{ value: DeviceAccessDuration; label: string }> = [
  { value: 'OneHour', label: '1 hour' },
  { value: 'EightHours', label: '8 hours' },
  { value: 'OneDay', label: '1 day' },
  { value: 'SevenDays', label: '7 days' },
  { value: 'ThirtyDays', label: '30 days' },
  { value: 'NinetyDays', label: '90 days' }
]

type AccessChoice = DeviceAccessDuration | 'Clear' | 'Custom'
type CustomUtcValidation =
  | { ok: true; utc: string; date: Date }
  | { ok: false; message: string }

const maximumAccessMilliseconds = 365 * 24 * 60 * 60 * 1000

export function putDeviceAccessExpiry(deviceId: string, request: DeviceAccessExpiryRequest): Promise<DeviceAccessExpiryResult> {
  return changeJson(`/api/local/devices/${encodeURIComponent(deviceId)}/access-expiry`, 'PUT', parseDeviceAccessExpiryResult, request)
}

export function validateCustomUtcDateTime(value: string, nowMs: number): CustomUtcValidation {
  if (!value) return { ok: false, message: 'Enter a UTC date and time.' }
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value))
    return { ok: false, message: 'Use the UTC date and time fields shown.' }

  const timestamp = Date.parse(`${value}:00.000Z`)
  if (!Number.isFinite(timestamp) || new Date(timestamp).toISOString().slice(0, 16) !== value)
    return { ok: false, message: 'Enter a real UTC calendar date and time.' }
  if (timestamp <= nowMs) return { ok: false, message: 'Choose a UTC time in the future.' }
  if (timestamp > nowMs + maximumAccessMilliseconds)
    return { ok: false, message: 'Choose a UTC time no more than 365 days away.' }

  const date = new Date(timestamp)
  return { ok: true, utc: date.toISOString(), date }
}

function defaultLocalDateTime(date: Date): string {
  return date.toLocaleString(undefined, {
    year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', timeZoneName: 'short'
  })
}

export function accessDeadlineRelativeText(deadlineMs: number, nowMs: number): string {
  const delta = deadlineMs - nowMs
  const absolute = Math.abs(delta)
  if (absolute < 60_000) return delta >= 0 ? 'in less than a minute' : 'less than a minute ago'

  const units: Array<{ milliseconds: number; singular: string; plural: string }> = [
    { milliseconds: 24 * 60 * 60 * 1000, singular: 'day', plural: 'days' },
    { milliseconds: 60 * 60 * 1000, singular: 'hour', plural: 'hours' },
    { milliseconds: 60 * 1000, singular: 'minute', plural: 'minutes' }
  ]
  const unit = units.find(candidate => absolute >= candidate.milliseconds) ?? units[2]
  const amount = Math.max(1, Math.round(absolute / unit.milliseconds))
  const label = amount === 1 ? unit.singular : unit.plural
  return delta >= 0 ? `in ${amount} ${label}` : `${amount} ${label} ago`
}

export function OwnerAccessDeadlineEditor({
  device,
  disabled = false,
  now = () => new Date(),
  formatLocal = defaultLocalDateTime,
  onSave,
  onRefresh
}: {
  device: Device
  disabled?: boolean
  now?: () => Date
  formatLocal?: (date: Date) => string
  onSave: (request: DeviceAccessExpiryRequest) => Promise<DeviceAccessExpiryResult>
  onRefresh: () => Promise<void>
}) {
  const id = useId()
  const [choice, setChoice] = useState<AccessChoice | null>(null)
  const [customUtc, setCustomUtc] = useState('')
  const [saving, setSaving] = useState(false)
  const [feedback, setFeedback] = useState<{ kind: 'success' | 'error'; text: string } | null>(null)
  const nowMs = now().getTime()
  const deadlineMs = device.accessExpiresUtc ? Date.parse(device.accessExpiresUtc) : null
  const expired = device.accessExpired || (deadlineMs !== null && deadlineMs <= nowMs)
  const customValidation = useMemo(
    () => validateCustomUtcDateTime(customUtc, nowMs),
    [customUtc, nowMs]
  )

  const choose = (next: AccessChoice) => {
    setChoice(next)
    setFeedback(null)
  }

  const requestForChoice = (): DeviceAccessExpiryRequest | null => {
    if (choice === 'Clear') return { clear: true }
    if (choice === 'Custom') return customValidation.ok ? { accessExpiresUtc: customValidation.utc } : null
    if (choice) return { duration: choice }
    return null
  }

  const save = async () => {
    const request = requestForChoice()
    if (!request) {
      const message = choice === 'Custom' && !customValidation.ok
        ? customValidation.message
        : 'Choose an access deadline first.'
      setFeedback({ kind: 'error', text: message })
      return
    }

    setSaving(true)
    setFeedback(null)
    try {
      const result = await onSave(request)
      if (!result.ok) {
        setFeedback({ kind: 'error', text: result.message })
        return
      }
      try {
        await onRefresh()
      } catch (error) {
        setFeedback({ kind: 'error', text: `The deadline was saved, but the latest Host state could not be refreshed. ${errorMessage(error)}` })
        return
      }
      setChoice(null)
      setCustomUtc('')
      setFeedback({ kind: 'success', text: result.message })
    } catch (error) {
      setFeedback({ kind: 'error', text: errorMessage(error) })
    } finally {
      setSaving(false)
    }
  }

  return <section className={`access-deadline ${expired ? 'expired' : ''}`} aria-labelledby={`${id}-title`}>
    <div className="access-deadline-current">
      <div>
        <span id={`${id}-title`}>Owner access</span>
        {!device.accessExpiresUtc && <><strong>No deadline</strong><small>Separate from credential expiry, approval, revocation, assignments, and permissions.</small></>}
        {device.accessExpiresUtc && !expired && deadlineMs !== null && <>
          <strong>Access ends {formatLocal(new Date(deadlineMs))}</strong>
          <small>{accessDeadlineRelativeText(deadlineMs, nowMs)} · The saved credential and assignments stay unchanged.</small>
        </>}
        {device.accessExpiresUtc && expired && deadlineMs !== null && <>
          <strong>Access expired</strong>
          <small>Ended {formatLocal(new Date(deadlineMs))} · {accessDeadlineRelativeText(deadlineMs, nowMs)}. Extend or clear it below.</small>
        </>}
      </div>
    </div>

    <details className="access-deadline-controls">
      <summary>Change deadline</summary>
      <fieldset disabled={disabled || saving}>
        <legend>Choose an owner access deadline</legend>
        <div className="access-deadline-choices">
          {accessDurationChoices.map(option => <label key={option.value} className={choice === option.value ? 'selected' : ''}>
            <Input type="radio" name={`${id}-access-deadline`} checked={choice === option.value} onChange={() => choose(option.value)} />
            <span>{option.label}</span>
          </label>)}
          <label className={choice === 'Clear' ? 'selected' : ''}>
            <Input type="radio" name={`${id}-access-deadline`} checked={choice === 'Clear'} onChange={() => choose('Clear')} />
            <span>Clear deadline</span>
          </label>
        </div>

        <details className="access-deadline-advanced">
          <summary>Advanced: custom UTC date and time</summary>
          <label className="access-custom-choice">
            <Input type="radio" name={`${id}-access-deadline`} checked={choice === 'Custom'} onChange={() => choose('Custom')} />
            <span>Use a custom UTC deadline</span>
          </label>
          <div className="access-custom-field">
            <label htmlFor={`${id}-custom-utc`}>UTC date and time</label>
            <Input id={`${id}-custom-utc`} type="datetime-local" step="60" value={customUtc}
              aria-describedby={`${id}-custom-utc-help`}
              onFocus={() => choose('Custom')}
              onChange={event => { setCustomUtc(event.target.value); choose('Custom') }} />
            <small id={`${id}-custom-utc-help`}>Enter UTC explicitly. TogetherServer converts it for display; it does not interpret this field as local time.</small>
          </div>
          {choice === 'Custom' && customValidation.ok && <div className="access-deadline-preview" role="status">
            <span>Exact UTC</span><strong>{customValidation.utc}</strong>
            <span>On this PC</span><strong>{formatLocal(customValidation.date)}</strong>
          </div>}
          {choice === 'Custom' && !customValidation.ok && customUtc && <p className="access-deadline-validation" role="alert">{customValidation.message}</p>}
        </details>
      </fieldset>

      <div className="access-deadline-save">
        <small>{choice === 'Clear' ? 'Clearing only removes the owner deadline.' : choice && choice !== 'Custom' ? 'The duration starts when the Host saves it.' : 'Assignments and permissions will not change.'}</small>
        <Button disabled={disabled || saving || !choice || (choice === 'Custom' && !customValidation.ok)} onClick={() => void save()}>
          {saving ? 'Saving deadline…' : 'Save deadline'}
        </Button>
      </div>
      {feedback && <div className={`access-deadline-feedback ${feedback.kind}`} role={feedback.kind === 'error' ? 'alert' : 'status'}>{feedback.text}</div>}
    </details>
  </section>
}

export function FriendAccessExpiredNotice({ connectionCode }: { connectionCode?: string | null }) {
  if (connectionCode !== 'AccessExpired') return null
  return <div className="friend-access-expired" role="status">
    <strong>Access expired</strong>
    <p>The Host owner ended access for this PC at its saved deadline.</p>
    <small>Ask the Host to extend or clear the deadline. This saved Host connection remains here, so you do not need a new invite.</small>
  </div>
}
