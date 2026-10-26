import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { LocalHostingGuide, type LocalHostingGuideProps } from './LocalHostingGuide'
import type { LocalHostingStatus } from './FriendLocalHosting'

type StepKey = LocalHostingStatus['steps'][number]['key']
const prepared: LocalHostingStatus = {
  ok: true, code: 'LocalHostingPrepared', message: 'The checked copy is ready.', allowed: true, consented: true,
  game: 'Valheim', name: 'Our world', needsServerFile: false, needsPassword: false,
  serverFile: 'C:/Games/valheim_server.exe', gameVersion: '0.222.1', gamePort: 2456, controlPort: 5131,
  localProfileId: '22222222-2222-4222-8222-222222222222', branchHash: 'a'.repeat(64),
  readyForManualStart: true, running: false,
  steps: ['permission', 'server', 'save', 'ports', 'connection'].map(key => ({
    key: key as StepKey, state: 'Ready', message: 'This check passed.'
  }))
}
function withCheck(key: StepKey, state: LocalHostingStatus['steps'][number]['state'], patch: Partial<LocalHostingStatus> = {}): LocalHostingStatus {
  return { ...prepared, readyForManualStart: false, ...patch,
    steps: prepared.steps.map(step => step.key === key ? { ...step, state } : step) }
}
function handlers() {
  return { onRetry: vi.fn(), onPrepare: vi.fn(), onChooseServer: vi.fn(), onConnectionHelp: vi.fn(),
    onGameSetup: vi.fn(), onOpenLocalServer: vi.fn(), onReviewSharedSave: vi.fn() }
}
function setup(status: LocalHostingStatus | null, extra: Partial<LocalHostingGuideProps> = {}) {
  const actions = handlers()
  const props = { status, busy: false, ...actions, ...extra }
  return { ...render(<LocalHostingGuide {...props} />), actions, props }
}
function currentStep(name: RegExp) {
  return within(screen.getByRole('navigation', { name: 'Hosting setup steps' })).getByRole('button', { name })
}

describe('Local hosting setup guide', () => {
  it('explains explicit stopped-save sharing without offering a sharing mutation in the guide', () => {
    const status = withCheck('save', 'ActionRequired', { code: 'LocalStoppedProgressNeedsSharing',
      message: 'The last stopped save is still local.',
      remedy: { kind: 'ShareStoppedProgress', title: 'Share stopped progress', message: 'Review the stopped save before sharing.' } })
    const { actions } = setup(status)
    expect(screen.getByRole('heading', { name: 'Share this PC\'s stopped progress' })).toBeInTheDocument()
    expect(screen.getByText(/If the save or setup cannot be verified, keep every copy/)).toBeInTheDocument()
    expect(screen.getByText(/Choose Start hosting separately/)).toBeInTheDocument()
    expect(screen.getByText(/Sharing does not confirm another PC received the save/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Share stopped progress' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Recheck the save' }))
    expect(actions.onRetry).toHaveBeenCalledOnce()
    expect(actions.onPrepare).not.toHaveBeenCalled()
    expect(actions.onOpenLocalServer).not.toHaveBeenCalled()
  })

  it('keeps the selected repair readable through a refresh and labels its last result after a failure', () => {
    const status = withCheck('save', 'ActionRequired', { code: 'SharedChunkUnavailable',
      message: 'Ask the source PC to share its completed backup again.' })
    const { props, rerender, actions } = setup(status)
    const heading = screen.getByRole('heading', { name: 'Get a completed verified save' })
    expect(screen.getByText('What to do next')).toBeInTheDocument()
    expect(screen.getByText('Needs attention:')).toBeInTheDocument()
    rerender(<LocalHostingGuide {...props} busy checking checksCurrent={false} />)
    expect(heading).toBeInTheDocument()
    expect(screen.getByText('Checking:')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Recheck the save' })).toBeDisabled()
    rerender(<LocalHostingGuide {...props} checksCurrent={false} />)
    expect(heading).toBeInTheDocument()
    expect(screen.getByText('Previous result:')).toBeInTheDocument()
    expect(screen.getByText(/This is the last available result/)).toBeInTheDocument()
    for (const action of Object.values(actions)) expect(action).not.toHaveBeenCalled()
  })

  it('opens the first unresolved check and keeps manual navigation available without running actions', () => {
    const status: LocalHostingStatus = { ...withCheck('server', 'ActionRequired', { needsServerFile: true }),
      steps: prepared.steps.map(step => ['server', 'save', 'ports'].includes(step.key) ? { ...step, state: 'ActionRequired' } : step) }
    const { actions } = setup(status)
    expect(currentStep(/2\. Server app/)).toHaveAttribute('aria-current', 'step')
    expect(screen.getByText('Step 2 of 5')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Install or select the dedicated server' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Next step' }))
    expect(currentStep(/3\. Save/)).toHaveAttribute('aria-current', 'step')
    fireEvent.click(screen.getByRole('button', { name: 'Previous step' }))
    expect(currentStep(/2\. Server app/)).toHaveAttribute('aria-current', 'step')
    fireEvent.click(currentStep(/1\. Permission/))
    expect(screen.getByRole('button', { name: 'Previous step' })).toBeDisabled()
    fireEvent.click(currentStep(/5\. Connection/))
    expect(screen.getByRole('button', { name: 'Next step' })).toBeDisabled()
    for (const action of Object.values(actions)) expect(action).not.toHaveBeenCalled()
  })

  it('preserves the selected guide page on equivalent polling results and follows changed checks', () => {
    const status = withCheck('server', 'ActionRequired', { needsServerFile: true })
    const { props, rerender } = setup(status)
    fireEvent.click(currentStep(/4\. Ports/))
    rerender(<LocalHostingGuide {...props} status={{ ...status, message: 'New display text.' }} />)
    expect(currentStep(/4\. Ports/)).toHaveAttribute('aria-current', 'step')
    rerender(<LocalHostingGuide {...props} status={withCheck('save', 'ActionRequired')} />)
    expect(currentStep(/3\. Save/)).toHaveAttribute('aria-current', 'step')
  })

  it('opens a requested check and still permits guide navigation', () => {
    const { props, rerender } = setup(withCheck('save', 'ActionRequired'), { selectedStep: 'ports' })
    expect(currentStep(/4\. Ports/)).toHaveAttribute('aria-current', 'step')
    fireEvent.click(currentStep(/2\. Server app/))
    expect(currentStep(/2\. Server app/)).toHaveAttribute('aria-current', 'step')
    rerender(<LocalHostingGuide {...props} selectedStep="connection" />)
    expect(currentStep(/5\. Connection/)).toHaveAttribute('aria-current', 'step')
  })

  it('focuses and reveals an explicitly requested step without moving focus on automatic checks', () => {
    render(<button>Keep current focus</button>)
    const previousControl = screen.getByRole('button', { name: 'Keep current focus' })
    previousControl.focus()
    const status = withCheck('save', 'ActionRequired')
    const { props, rerender, actions } = setup(status)
    expect(previousControl).toHaveFocus()
    const heading = screen.getByRole('heading', { name: 'Prepare and verify this PC\'s copy' })
    const scroll = vi.fn()
    Object.defineProperty(heading, 'scrollIntoView', { value: scroll, configurable: true })

    rerender(<LocalHostingGuide {...props} status={{ ...status, message: 'Automatic refresh finished.' }} />)
    expect(previousControl).toHaveFocus()
    expect(scroll).not.toHaveBeenCalled()

    rerender(<LocalHostingGuide {...props} recommendedStep="save" />)
    expect(previousControl).toHaveFocus()
    expect(scroll).not.toHaveBeenCalled()

    rerender(<LocalHostingGuide {...props} selectedStep="ports" />)
    expect(screen.getByRole('heading', { name: 'Check the local port plan' })).toHaveFocus()
    expect(heading).toHaveAttribute('tabindex', '-1')
    expect(scroll).toHaveBeenCalledExactlyOnceWith({ block: 'start', inline: 'nearest' })

    currentStep(/1\. Permission/).focus()
    rerender(<LocalHostingGuide {...props} selectedStep="ports" status={{ ...status, code: 'LatestSaveChanged' }} />)
    expect(currentStep(/1\. Permission/)).toHaveFocus()
    expect(scroll).toHaveBeenCalledOnce()

    previousControl.focus()
    rerender(<LocalHostingGuide key="repeat-row-request" {...props} selectedStep="ports" />)
    expect(screen.getByRole('heading', { name: 'Check the local port plan' })).toHaveFocus()
    for (const action of Object.values(actions)) expect(action).not.toHaveBeenCalled()
  })

  it('keeps unavailable checks unverified and offers only a read retry', () => {
    const { actions, props, rerender } = setup(null)
    expect(currentStep(/1\. Permission/)).toHaveAttribute('aria-current', 'step')
    expect(screen.getByText(/Unavailable checks stay unverified/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Retry hosting checks' }))
    expect(actions.onRetry).toHaveBeenCalledOnce()
    for (const [name, action] of Object.entries(actions)) if (name !== 'onRetry') expect(action).not.toHaveBeenCalled()
    rerender(<LocalHostingGuide {...props} busy />)
    expect(screen.getByRole('button', { name: 'Retry hosting checks' })).toBeDisabled()
    expect(screen.getByRole('region', { name: 'Local hosting setup guide' })).toHaveAttribute('aria-busy', 'true')
  })

  it.each(['SharingOff', 'SharedHostingDisabled', 'HostingPermissionRequired', 'HostingPermissionDenied'])(
    'gives the original owner the exact single-setting path for %s', code => {
      const { container, actions } = setup(withCheck('permission', 'ActionRequired', { allowed: false, code }))
      expect(screen.getByRole('heading', { name: 'Ask the owner to allow shared hosting' })).toBeInTheDocument()
      expect(screen.getByText('Allow friends to host this server')).toBeInTheDocument()
      expect(screen.getByText('Backups & sharing')).toBeInTheDocument()
      expect(container).toHaveTextContent('original owner\'s PC')
      expect(container).toHaveTextContent('Approved PCs assigned to this server get hosting access from this setting.')
      expect(container.textContent).not.toMatch(/Can host this save|grant Receive|grant EligibleHost|separate host grant/i)
      expect(screen.queryByRole('button', { name: /enable|allow|start|prepare/i })).not.toBeInTheDocument()
      expect(actions.onPrepare).not.toHaveBeenCalled()
    })

  it.each([
    ['ApprovalPending', 'Ask the owner to approve this PC', /approve its waiting connection/],
    ['AccessExpired', 'Ask the owner to extend this PC\'s access', /extend or clear the access deadline/],
    ['Revoked', 'Connect again with the owner\'s approval', /Invite friends/],
    ['NotPaired', 'Connect to the owner first', /Invite friends/],
    ['HostingIdentityRequired', 'Check this PC\'s sharing identity', /refresh its sharing identity and current membership/]
  ] as const)('explains the owner or local action for %s', (code, title, instruction) => {
    setup(withCheck('permission', 'ActionRequired', { allowed: false, code }))
    expect(screen.getByRole('heading', { name: title as string })).toBeInTheDocument()
    expect(screen.getByText(instruction as RegExp)).toBeInTheDocument()
  })

  it.each(['SharingPermissionUnknown', 'RosterUnavailable', 'RosterCatchUpPending', 'ConnectionChanged', 'NewReadFailure'])(
    'does not turn an unavailable permission result %s into an owner denial', code => {
      setup(withCheck('permission', 'ActionRequired', { allowed: false, code,
        remedy: { kind: 'Permission', title: 'Ask the owner', message: 'Server-supplied text.' } }))
      expect(screen.getByRole('heading', { name: 'Check the current hosting permission' })).toBeInTheDocument()
      expect(screen.getByText(/An unavailable check does not mean the owner turned hosting off/)).toBeInTheDocument()
      expect(screen.queryByText(/Turn on/)).not.toBeInTheDocument()
    })

  it.each(['HostingHistoryChoiceRequired', 'WorldHistoryReviewRequired', 'VersionConflict'])(
    'preserves different progress for owner review for %s', code => {
      const { actions } = setup(withCheck('save', 'ActionRequired', { code }))
      expect(screen.getByRole('heading', { name: 'Ask the owner to review different progress' })).toBeInTheDocument()
      expect(screen.getByText(/Keep every existing save/)).toBeInTheDocument()
      expect(screen.getByText(/cannot merge worlds or pick the newest by date/)).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Prepare a checked copy' })).not.toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: 'Review the shared saves' }))
      expect(actions.onReviewSharedSave).toHaveBeenCalledOnce()
      expect(actions.onPrepare).not.toHaveBeenCalled()
    })

  it('keeps a general shared-save review distinct from confirmed different progress or damaged bytes', () => {
    const status = withCheck('save', 'ActionRequired', { code: 'SharedSaveHistoryNeedsReview',
      message: 'The available shared save could not be selected and verified. Keep its source PC reachable and retry.' })
    const { actions, container } = setup(status)
    expect(screen.getByRole('heading', { name: 'Review the available shared save' })).toBeInTheDocument()
    expect(screen.getByText(status.message)).toBeInTheDocument()
    expect(container).toHaveTextContent('Selecting and verifying a complete hosting copy still needs review.')
    expect(container.textContent).not.toMatch(/two histories|different progress|conflicting saves|damaged|corrupt|failed hash/i)
    expect(screen.queryByRole('button', { name: 'Prepare a checked copy' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Recheck the save' }))
    expect(actions.onRetry).toHaveBeenCalledOnce()
    expect(actions.onPrepare).not.toHaveBeenCalled()
  })

  it.each(['VersionRollback', 'InvalidManifest', 'SharedSaveHashMismatch', 'SigningIdentityChanged', 'SharingReviewRequired'])(
    'keeps %s blocked for trusted history or file verification without a bypass action', code => {
      const { actions } = setup(withCheck('save', 'ActionRequired', { code }))
      expect(screen.getByRole('heading', { name: 'Review the save verification problem' })).toBeInTheDocument()
      expect(screen.getByText(/older history or a failed hash check cannot be bypassed/)).toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /prepare|start|ignore|replace/i })).not.toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: 'Recheck the save' }))
      expect(actions.onRetry).toHaveBeenCalledOnce()
      expect(actions.onPrepare).not.toHaveBeenCalled()
    })

  it('explains a history failure from the permission check without suggesting a permission change', () => {
    const { actions } = setup(withCheck('permission', 'ActionRequired', { allowed: false, code: 'RosterRollback' }))
    expect(screen.getByRole('heading', { name: 'Refresh the saved sharing history' })).toBeInTheDocument()
    expect(screen.getByText(/Make sure both apps are up to date/)).toBeInTheDocument()
    expect(screen.getByText(/sync missing signed sharing history/)).toBeInTheDocument()
    expect(screen.getByText(/Do not delete or reset saved history/)).toBeInTheDocument()
    expect(screen.getByText(/Changed, conflicting or damaged history stays blocked for review/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Check hosting access again' }))
    expect(actions.onRetry).toHaveBeenCalledOnce()
    expect(actions.onPrepare).not.toHaveBeenCalled()
    expect(screen.queryByText(/Turn on/)).not.toBeInTheDocument()
  })

  it.each([
    ['RosterRollback', 'Review sharing history before checking ports', 'Refresh the saved sharing history'],
    ['HostingPermissionRequired', 'Check hosting access before checking ports', 'Ask the owner to allow shared hosting'],
    ['RosterUnavailable', 'Check hosting access before checking ports', 'Check the current hosting permission']
  ])('explains the prerequisite for a port check blocked by %s', (code, title, permissionTitle) => {
    const { actions } = setup(withCheck('permission', 'ActionRequired', { allowed: false, code }), { selectedStep: 'ports' })
    expect(screen.getByRole('heading', { name: title })).toBeInTheDocument()
    expect(screen.getByText(/The ports check has not run in this attempt/)).toBeInTheDocument()
    expect(screen.getByText(/does not show a problem with the server app, save files, ports or network/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Recheck the local ports' })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Free the game ports on this PC' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Show the permission steps' }))
    expect(currentStep(/1\. Permission/)).toHaveAttribute('aria-current', 'step')
    expect(screen.getByRole('heading', { name: permissionTitle })).toBeInTheDocument()
    for (const action of Object.values(actions)) expect(action).not.toHaveBeenCalled()
  })

  it('announces an action in progress and its result beside the guide actions', () => {
    const { props, rerender } = setup(withCheck('permission', 'Unknown', { allowed: false, code: 'RosterUnavailable' }), {
      busy: true, feedback: 'Checking hosting access. This check does not start the server.'
    })
    expect(screen.getByRole('status')).toHaveTextContent('Checking hosting access.')
    expect(screen.getByRole('button', { name: 'Check hosting access again' })).toBeDisabled()
    rerender(<LocalHostingGuide {...props} busy={false} status={prepared}
      feedback="Check finished: this PC is prepared. Choose Host locally when you want to start." />)
    expect(screen.getByRole('status')).toHaveTextContent('Check finished: this PC is prepared.')
    expect(screen.queryByText(/Checking hosting access\./)).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Check Friend access and the game separately' })).toBeInTheDocument()
  })

  it.each([
    ['Valheim', /Library and include Tools/, /valheim_server\.exe/, /No command-line tool is needed/],
    ['MinecraftJava', /TogetherServer-managed official server installation/, /managed server\.jar/, /matching Java runtime/],
    ['MinecraftBedrock', /official Bedrock dedicated server/, /bedrock_server\.exe/, /Minecraft EULA/],
    ['Factorio', /official Factorio server/, /factorio\.exe/, /required add-ons/],
    ['Terraria', /official Terraria dedicated server/, /TerrariaServer\.exe/, /trusted player count/]
  ] as const)('gives concrete installation and selection instructions for %s', (game, install, file, detail) => {
    const { actions } = setup(withCheck('server', 'ActionRequired', { game: game as string, needsServerFile: true }))
    expect(screen.getByText(install as RegExp)).toBeInTheDocument()
    expect(screen.getByText(file as RegExp)).toBeInTheDocument()
    expect(screen.getByText(detail as RegExp)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Choose the installed server file' }))
    expect(actions.onChooseServer).toHaveBeenCalledOnce()
    expect(actions.onGameSetup).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Review game setup' }))
    expect(actions.onGameSetup).toHaveBeenCalledOnce()
    expect(actions.onPrepare).not.toHaveBeenCalled()
  })

  it('keeps Minecraft terms a deliberate local choice and gives the matching tools path', () => {
    const { container } = setup(withCheck('server', 'ActionRequired', {
      game: 'MinecraftJava', code: 'MinecraftTermsRequired', needsServerFile: true, needsPassword: true
    }))
    expect(screen.getByRole('heading', { name: 'Review Minecraft\'s terms on this PC' })).toBeInTheDocument()
    expect(container).toHaveTextContent('decide whether to accept them on this PC')
    expect(container).toHaveTextContent('Enter a new game password')
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it.each(['MinecraftJava', 'MinecraftBedrock'])('gives %s a concrete install route before a local profile exists', game => {
    const { container } = setup(withCheck('server', 'ActionRequired', { game, localProfileId: null, needsServerFile: true }),
      { onGameSetup: undefined })
    expect(container).toHaveTextContent('open Host, then New server')
    expect(container).toHaveTextContent('Server app step')
    expect(container).toHaveTextContent('Close that setup without Save and start.')
    expect(container).toHaveTextContent('Return to Join, select this shared server, open Host locally')
    expect(screen.queryByRole('button', { name: 'Review game setup' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Choose the installed server file' })).toBeInTheDocument()
  })

  it('routes a matching add-on problem to saved setup instead of another install', () => {
    const { actions } = setup(withCheck('server', 'ActionRequired', { code: 'LocalServerSetupRequired' }))
    expect(screen.getByRole('heading', { name: 'Match this save\'s game setup' })).toBeInTheDocument()
    expect(screen.getByText(/Install and enable the same required add-ons yourself/)).toHaveTextContent('0.222.1')
    expect(screen.queryByRole('button', { name: 'Choose the installed server file' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Open this PC\'s server setup' }))
    expect(actions.onOpenLocalServer).toHaveBeenCalledOnce()
  })

  it.each(['NoPublishedSave', 'SharedSaveRequired', 'SharedChunkUnavailable', 'WaitingForSuccessorCopy'])(
    'gives a completed-source and safe-stop path for %s without starting from a read', code => {
      const { actions } = setup(withCheck('save', 'ActionRequired', { code }))
      expect(screen.getByRole('heading', { name: 'Get a completed verified save' })).toBeInTheDocument()
      expect(screen.getByText(/Share latest backup to retry an existing completed backup/)).toBeInTheDocument()
      expect(screen.getByText(/If no completed backup exists, ask the current Host to stop the game server safely/)).toBeInTheDocument()
      expect(screen.getByText('This PC must receive and verify a complete save before Start.')).toBeInTheDocument()
      expect(screen.queryByText(/interrupted transfer/)).not.toBeInTheDocument()
      expect(screen.getByText(/choose Host locally below after reviewing the network confirmation/)).toBeInTheDocument()
      for (const action of Object.values(actions)) expect(action).not.toHaveBeenCalled()
    })

  it.each(['Unknown', 'ActionRequired'] as const)('explains the unprepared hosting copy before a generic %s save result', state => {
    const status = withCheck('save', state, { code: 'LocalHostingPreparationRequired',
      message: 'Choose Host locally to get and verify the latest completed save.', localProfileId: null, branchHash: null })
    const { actions } = setup(status)
    expect(currentStep(/3\. Save/)).toHaveAttribute('aria-current', 'step')
    expect(screen.getByRole('heading', { name: 'Prepare this PC\'s hosting copy' })).toBeInTheDocument()
    expect(screen.getByText(status.message)).toBeInTheDocument()
    expect(screen.getByText(/receives it if needed and prepares a separate local copy/)).toBeInTheDocument()
    expect(screen.getByText(/Preparation verifies the save files and local setup before Start/)).toBeInTheDocument()
    expect(screen.queryByText(/interrupted transfer|stop the game server safely/i)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Prepare a checked copy' })).not.toBeInTheDocument()
    for (const action of Object.values(actions)) expect(action).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Recheck the save' }))
    expect(actions.onRetry).toHaveBeenCalledOnce()
    expect(actions.onPrepare).not.toHaveBeenCalled()
  })

  it.each(['SharedSaveUnavailable', 'SharedVersionUnavailable', 'SharedChunkUnavailable', 'ChunkHashMismatch'])(
    'keeps the actual %s failure visible with review and read-only retry actions', code => {
      const status = withCheck('save', 'ActionRequired', { ok: false, code,
        message: 'The exact shared copy could not be verified. Keep its source reachable and retry.' })
      const { actions } = setup(status)
      expect(screen.getByText(status.message)).toBeInTheDocument()
      fireEvent.click(screen.getByRole('button', { name: 'Review the shared saves' }))
      fireEvent.click(screen.getByRole('button', { name: 'Recheck the save' }))
      expect(actions.onReviewSharedSave).toHaveBeenCalledOnce()
      expect(actions.onRetry).toHaveBeenCalledOnce()
      expect(actions.onPrepare).not.toHaveBeenCalled()
    })

  it('keeps an unknown save check unverified instead of offering preparation', () => {
    setup(withCheck('save', 'Unknown'))
    expect(screen.getByRole('heading', { name: 'Check the save before preparing it' })).toBeInTheDocument()
    expect(screen.getByText(/An unknown check is not a verified save/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Prepare a checked copy' })).not.toBeInTheDocument()
  })

  it.each(['Requested', 'Accepted', 'Prepared'] as const)('keeps the exact final save distinct for a %s hosting move', recipientState => {
    const { actions } = setup(withCheck('save', 'ActionRequired', { handoffRequest: {
      pending: true, code: 'WaitingForFinalSave', message: 'Waiting.',
      successorDeviceId: '33333333-3333-4333-8333-333333333333', requestId: '44444444-4444-4444-8444-444444444444',
      recipientState, preparedVersionHash: 'b'.repeat(64), finalVersion: null,
      receiptConfirmed: false, canComplete: false, canCancel: false
    } }))
    expect(screen.getByRole('heading', { name: recipientState === 'Requested' ? 'Review the requested hosting move' : 'Wait for the current Host\'s final save' })).toBeInTheDocument()
    if (recipientState !== 'Requested') expect(screen.getByText('The final save receipt is not confirmed yet.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Prepare a checked copy' })).not.toBeInTheDocument()
    expect(actions.onPrepare).not.toHaveBeenCalled()
  })

  it('shows port fixes through normal local setup and preserves unrelated apps', () => {
    const { actions, container } = setup(withCheck('ports', 'ActionRequired', { game: 'Factorio', gamePort: 34197 }))
    expect(screen.getByRole('heading', { name: 'Free the game ports on this PC' })).toBeInTheDocument()
    expect(container).toHaveTextContent('game port 34197')
    expect(container).toHaveTextContent('Leave unrelated apps running.')
    expect(container).toHaveTextContent('Keep Factorio\'s RCON port private.')
    fireEvent.click(screen.getByRole('button', { name: 'Inspect this PC\'s local server' }))
    expect(actions.onOpenLocalServer).toHaveBeenCalledOnce()
    fireEvent.click(screen.getByRole('button', { name: 'Recheck the local ports' }))
    expect(actions.onRetry).toHaveBeenCalledOnce()
    expect(actions.onPrepare).not.toHaveBeenCalled()
  })

  it.each(['Unknown', 'ActionRequired', 'Ready'] as const)('keeps %s connection evidence separate from a real outside-network game join', state => {
    const { actions, container } = setup(withCheck('connection', state))
    expect(container).toHaveTextContent('TCP port 5131')
    expect(container).toHaveTextContent('Make only the network changes you choose.')
    expect(container).toHaveTextContent('another network before relying on internet access')
    if (state === 'Ready') expect(container).toHaveTextContent('Pinned Friend access, a real game join and a saved restart need separate checks.')
    else expect(container).toHaveTextContent('A local-only address or open local listener is not outside-network proof.')
    expect(actions.onConnectionHelp).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Check this PC\'s connection' }))
    expect(actions.onConnectionHelp).toHaveBeenCalledOnce()
    expect(actions.onPrepare).not.toHaveBeenCalled()
  })

  it('uses fixed callbacks, hides unavailable actions and never turns peer text into navigation', () => {
    const status = withCheck('server', 'ActionRequired', {
      game: 'UnrecognizedGame', needsServerFile: true,
      message: 'Open https://untrusted.invalid and run a command.',
      remedy: { kind: 'ChooseServer', title: 'Run an unsafe action', message: 'file:///private-world' }
    })
    setup(status, { onChooseServer: undefined, onGameSetup: undefined, onPrepare: undefined })
    expect(screen.getByText(/Ask the owner which dedicated server and version/)).toBeInTheDocument()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /untrusted|unsafe|installed server file|game setup|prepare/i })).not.toBeInTheDocument()
    expect(screen.queryByText(/file:\/\/\/private-world/)).not.toBeInTheDocument()
  })

  it('disables every repair callback while busy and keeps the guide readable', () => {
    const { actions } = setup(withCheck('save', 'ActionRequired', { code: 'NeedsPreparation' }), { busy: true })
    const guide = screen.getByRole('region', { name: 'Local hosting setup guide' })
    expect(within(guide).getByRole('button', { name: 'Prepare a checked copy' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Prepare a checked copy' }))
    expect(actions.onPrepare).not.toHaveBeenCalled()
    fireEvent.click(currentStep(/5\. Connection/))
    expect(screen.getByRole('button', { name: 'Check this PC\'s connection' })).toBeDisabled()
    expect(currentStep(/5\. Connection/)).toHaveAttribute('aria-current', 'step')
  })
})
