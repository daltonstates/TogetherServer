import { useEffect, useRef, useState } from 'react'
import { Button, TextArea } from './Controls'
import { errorMessage } from './api'
import { parseSharedRouteDetails, type SharedRouteDetails } from './sharedWorldUx'

export function SharedWorldRouteDetails({ profileId, onReviewed }:
  { profileId: string; onReviewed: (details: SharedRouteDetails | null) => void }) {
  const [pasted, setPasted] = useState('')
  const [reviewed, setReviewed] = useState<SharedRouteDetails | null>(null)
  const [error, setError] = useState('')
  const previousProfile = useRef(profileId)
  useEffect(() => {
    if (previousProfile.current === profileId) return
    previousProfile.current = profileId
    setPasted(''); setReviewed(null); setError(''); onReviewed(null)
  }, [profileId, onReviewed])
  const review = () => {
    setReviewed(null); setError(''); onReviewed(null)
    try {
      const details = parseSharedRouteDetails(pasted, profileId)
      setReviewed(details); onReviewed(details)
    } catch (cause) { setError(errorMessage(cause)) }
  }
  return <section aria-label="Paste route details together">
    <label>Route details from the new Host<TextArea rows={3} maxLength={4096} value={pasted}
      onChange={event => { setPasted(event.target.value); setReviewed(null); setError(''); onReviewed(null) }} /></label>
    <Button className="secondary" disabled={!pasted.trim()} onClick={review}>Review pasted route details</Button>
    {reviewed && <div role="note"><p>Review this shared world with the group before checking its signed connection.</p>
      {reviewed.endpoint && <p>Proposed direct route: {reviewed.endpoint}</p>}
      <p>Signed decision: <code style={{ overflowWrap: 'anywhere' }}>{reviewed.recordHash}</code></p>
      <p>Certificate fingerprint: <code style={{ overflowWrap: 'anywhere' }}>{reviewed.tlsFingerprint}</code></p>
      <p className="helper-text">The signed decision supplies the address used for the check. A pasted address is a review hint.</p>
      <p className="helper-text">The app still checks signed authority and pinned TLS. Pasting does not change a saved address or grant access.</p>
    </div>}
    {error && <p role="alert">{error}</p>}
  </section>
}
