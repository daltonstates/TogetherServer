import { fireEvent, render, screen } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { SharedWorldRouteDetails } from './SharedWorldRouteDetails'
import { createSharedRouteDetails } from './sharedWorldUx'

const profileId = '11111111-1111-4111-8111-111111111111'
const code = createSharedRouteDetails(profileId, { recordHash: 'A'.repeat(64), tlsFingerprint: 'B'.repeat(64),
  endpoint: 'https://192.0.2.10:5131' })

it('reviews combined details locally, then withdraws that review when the pasted proof changes', () => {
  const reviewed = vi.fn()
  render(<SharedWorldRouteDetails profileId={profileId} onReviewed={reviewed} />)
  const input = screen.getByLabelText('Connection details from the new Host')
  fireEvent.change(input, { target: { value: code } })
  expect(reviewed).toHaveBeenLastCalledWith(null)
  expect(screen.queryByText('Proposed address: https://192.0.2.10:5131')).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Review connection details' }))
  expect(reviewed).toHaveBeenLastCalledWith({ recordHash: 'A'.repeat(64), tlsFingerprint: 'B'.repeat(64),
    endpoint: 'https://192.0.2.10:5131' })
  expect(screen.getByText(/Pasting details does not change your saved connection or give access/)).toBeInTheDocument()
  fireEvent.change(input, { target: { value: '{bad' } })
  expect(reviewed).toHaveBeenLastCalledWith(null)
  expect(screen.queryByText('Proposed address: https://192.0.2.10:5131')).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Review connection details' }))
  expect(screen.getByRole('alert')).toHaveTextContent('Could not read these connection details')
})

it('refuses another world and clears displayed proof details when the selected world changes', () => {
  const reviewed = vi.fn()
  const view = render(<SharedWorldRouteDetails profileId={profileId} onReviewed={reviewed} />)
  fireEvent.change(screen.getByLabelText('Connection details from the new Host'), { target: { value: code } })
  fireEvent.click(screen.getByRole('button', { name: 'Review connection details' }))
  view.rerender(<SharedWorldRouteDetails profileId="22222222-2222-4222-8222-222222222222" onReviewed={reviewed} />)
  expect(reviewed).toHaveBeenLastCalledWith(null)
  expect(screen.getByLabelText('Connection details from the new Host')).toHaveValue('')
  expect(screen.queryByText('Proposed address: https://192.0.2.10:5131')).not.toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Connection details from the new Host'), { target: { value: code } })
  fireEvent.click(screen.getByRole('button', { name: 'Review connection details' }))
  expect(screen.getByRole('alert')).toHaveTextContent('are for a different shared world')
  expect(reviewed).toHaveBeenLastCalledWith(null)
})
