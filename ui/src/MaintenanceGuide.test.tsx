import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { MaintenanceGuide } from './MaintenanceGuide'

describe('MaintenanceGuide', () => {
  it('requires maintenance and a trusted zero count before guided Stop', () => {
    const onToggle = vi.fn()
    const onStop = vi.fn()
    const props = { enabled: false, message: '', state: 'Ready', onlinePlayers: null,
      countTrusted: false, lastBackupUtc: null, busy: false, onMessage: vi.fn(), onToggle,
      onStop, onBackup: vi.fn(), onStart: vi.fn(), onOpenDoctor: vi.fn() }
    const { rerender } = render(<MaintenanceGuide {...props} />)
    fireEvent.click(screen.getByText('Prepare maintenance'))
    expect(screen.getByRole('button', { name: 'Stop empty server' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Begin maintenance' }))
    expect(onToggle).toHaveBeenCalledWith(true)
    rerender(<MaintenanceGuide {...props} enabled onlinePlayers={0} countTrusted />)
    fireEvent.click(screen.getByRole('button', { name: 'Stop empty server' }))
    expect(onStop).toHaveBeenCalledOnce()
  })
})
