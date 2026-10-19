import { useEffect, useRef, useState } from 'react'
import { Button, TextArea } from './Controls'
import { DismissibleNotice } from './DismissibleNotice'
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
  return <section aria-label="Paste connection details">
    <label>Connection details from the new Host<TextArea rows={3} maxLength={4096} value={pasted}
      onChange={event => { setPasted(event.target.value); setReviewed(null); setError(''); onReviewed(null) }} /></label>
    <Button className="secondary" disabled={!pasted.trim()} onClick={review}>Review connection details</Button>
    {reviewed && <div role="note"><p>Check the world and Host with your group before testing this connection.</p>
      {reviewed.endpoint && <p>Proposed address: {reviewed.endpoint}</p>}
      <p>Hosting decision ID: <code style={{ overflowWrap: 'anywhere' }}>{reviewed.recordHash}</code></p>
      <p>Certificate fingerprint: <code style={{ overflowWrap: 'anywhere' }}>{reviewed.tlsFingerprint}</code></p>
      <p className="helper-text">The app tests the address in the verified hosting decision. The pasted address is shown for you to review.</p>
      <p className="helper-text">The app checks the hosting decision and the Host's saved identity. Pasting details does not change your saved connection or give access.</p>
    </div>}
    {error && <DismissibleNotice as="p" scope={profileId} role="alert">{error}</DismissibleNotice>}
  </section>
}
