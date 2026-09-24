import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { Profile } from './GameProfile'
import { MinecraftServerSetup } from './MinecraftSetup'

const profile: Profile = {
  id: 'minecraft-profile',
  kind: 'MinecraftJava',
  name: 'Java server',
  serverName: '',
  crossplay: false,
  publicListing: false,
  worldId: 'world',
  worldSource: 'New',
  worldDirectory: '',
  gamePort: 25565,
  executablePath: '',
  minecraft: { serverJarPath: '' }
}

describe('MinecraftServerSetup', () => {
  it('associates the terms checkbox without nesting legal links in its label', () => {
    render(<MinecraftServerSetup profile={profile} busy={false} onChange={vi.fn()} mode="install"
      onBrowse={vi.fn()} discovery={null} onSelect={vi.fn()} onScan={vi.fn()} onInstall={vi.fn()}
      acceptedTerms={false} onTermsChange={vi.fn()} installBusy={false} />)

    expect(screen.getByRole('checkbox', { name: /I have read and accept the terms/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Minecraft EULA' }).closest('label')).toBeNull()
    expect(screen.getByRole('link', { name: 'Microsoft Privacy Statement' }).closest('label')).toBeNull()
  })
})
