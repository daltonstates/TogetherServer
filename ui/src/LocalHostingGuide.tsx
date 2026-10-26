import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Button } from './Controls'
import type { LocalHostingStatus } from './FriendLocalHosting'
import './LocalHostingGuide.css'

type StepKey = LocalHostingStatus['steps'][number]['key']
export type LocalHostingGuideProps = {
  status: LocalHostingStatus | null
  selectedStep?: StepKey | null
  recommendedStep?: StepKey
  busy: boolean
  checking?: boolean
  checksCurrent?: boolean
  feedback?: string
  onRetry: () => void
  onPrepare?: () => void
  onChooseServer?: () => void
  onConnectionHelp?: () => void
  onGameSetup?: () => void
  onOpenLocalServer?: () => void
  onReviewSharedSave?: () => void
}

const guideSteps: { key: StepKey; label: string }[] = [
  { key: 'permission', label: 'Permission' }, { key: 'server', label: 'Server app' },
  { key: 'save', label: 'Save' }, { key: 'ports', label: 'Ports' }, { key: 'connection', label: 'Connection' }
]
const historyCodes = ['HostingHistoryChoiceRequired', 'WorldHistoryReviewRequired',
  'VersionConflict', 'VersionChainInvalid', 'GroupMismatch', 'SourceReviewRequired', 'SeparateCopyReviewRequired']
const integrityCodes = ['SharedSaveHistoryNeedsReview', 'RosterRollback', 'VersionRollback', 'RosterRejected', 'AuthorityRejected', 'InvalidManifest',
  'InvalidSave', 'SharedSaveInvalid', 'SharedSaveHashMismatch', 'HashMismatch', 'ChunkHashMismatch',
  'SharingReviewRequired', 'LocalHostingReviewRequired', 'HandoffReviewRequired', 'DataRecoveryRequired']
const permissionUnknownCodes = ['SharingPermissionUnknown', 'RosterUnavailable', 'RosterCatchUpPending',
  'AuthorityUnavailable', 'AuthorityCatchUpPending', 'ConnectionClosed', 'ConnectionChanged',
  'HostingPermissionChanged', 'HostingRequestChanged', 'HostUnavailable', 'HostUnreachable', 'CheckFailed', 'Unknown', 'Unavailable']
const permissionDeniedCodes = ['HostingPermissionRequired', 'HostingPermissionDenied', 'EnrollmentDenied',
  'PermissionDenied', 'SharingOff', 'SharedHostingDisabled', 'HostingDisabled']
const missingSaveCodes = ['SharedSaveRequired', 'WaitingForSuccessorCopy', 'SharedSaveUnavailable',
  'NoPublishedSave', 'SharedVersionUnavailable', 'SharedChunkUnavailable']

export function localHostingAccessBlocker(status: LocalHostingStatus | null): 'history' | 'permission' | 'unavailable' | null {
  if (!status || status.allowed) return null
  if (historyCodes.includes(status.code) || integrityCodes.includes(status.code) || status.code === 'SigningIdentityChanged'
    || status.remedy?.kind === 'ChooseHistory') return 'history'
  return [...permissionDeniedCodes, 'ApprovalPending', 'AccessExpired', 'Revoked', 'NotPaired'].includes(status.code)
    ? 'permission' : 'unavailable'
}

type Action = { label: string; run?: () => void }
type Help = { title: string; instructions: ReactNode[]; note?: ReactNode; actions?: Action[] }

function permissionHelp(status: LocalHostingStatus, props: LocalHostingGuideProps): Help {
  const retry: Action = { label: 'Check hosting access again', run: props.onRetry }
  if (historyCodes.includes(status.code) || integrityCodes.includes(status.code) || status.code === 'SigningIdentityChanged'
    || status.remedy?.kind === 'ChooseHistory') return saveHelp(status, props)
  if (status.steps[0].state === 'Ready' && status.allowed) return {
    title: 'This PC is allowed to host', instructions: [
      'The owner allows hosting for this server and this PC has access.',
      'Continue through the server app, save, ports and connection checks below.'
    ], actions: [retry]
  }
  if (status.code === 'ApprovalPending') return {
    title: 'Ask the owner to approve this PC', instructions: [
      'On the owner\'s PC, open Host, then Friend access.',
      'Choose this PC and approve its waiting connection. The owner should also check that this server is assigned to it.',
      'Keep the saved connection on this PC, then check hosting access again.'
    ], actions: [retry]
  }
  if (status.code === 'AccessExpired') return {
    title: 'Ask the owner to extend this PC\'s access', instructions: [
      'On the owner\'s PC, open Host, then Friend access and choose this PC.',
      'Under Access time, extend or clear the access deadline.',
      'Keep this saved connection and check hosting access again after the owner saves the change.'
    ], actions: [retry]
  }
  if (status.code === 'Revoked' || status.code === 'NotPaired') return {
    title: status.code === 'Revoked' ? 'Connect again with the owner\'s approval' : 'Connect to the owner first',
    instructions: [
      'Ask the owner to open Host, select this server and choose Invite friends.',
      'On this PC, open Join and connect with the code the owner shares privately.',
      'If approval is required, the owner approves this PC under Friend access. Return here and check hosting access again.'
    ], actions: [retry]
  }
  if (status.code === 'HostingIdentityRequired') return {
    title: 'Check this PC\'s sharing identity', instructions: [
      'Keep TogetherServer open on this PC and on the owner\'s PC.',
      'Connect to the saved Host in Join so this PC can refresh its sharing identity and current membership.',
      'Check hosting access again. If it still fails, ask the owner to review this PC under Friend access and this server\'s Backups & sharing.'
    ], actions: [retry, { label: 'Review shared-save setup', run: props.onReviewSharedSave }]
  }
  if (permissionUnknownCodes.includes(status.code) || status.steps[0].state === 'Unknown'
    || !status.allowed && (!permissionDeniedCodes.includes(status.code) || status.remedy?.kind === 'Review')) return {
    title: 'Check the current hosting permission', instructions: [
      'Keep TogetherServer open on the owner\'s PC and connect to that saved Host in Join.',
      'Check hosting access again to read the current sharing permission. An unavailable check does not mean the owner turned hosting off.',
      'If the check still fails, ask the owner to open this server\'s Backups & sharing, choose Sharing & hosting and review any sharing notice.'
    ], actions: [retry, { label: 'Review shared-save setup', run: props.onReviewSharedSave }]
  }
  return {
    title: 'Ask the owner to allow shared hosting', instructions: [
      <>On the original owner&apos;s PC, open <strong>Host</strong>, select <strong>{status.name}</strong>, then open <strong>Backups &amp; sharing</strong> and choose <strong>Sharing &amp; hosting</strong>.</>,
      <>Turn on <strong>Allow friends to host this server</strong>. Approved PCs assigned to this server get hosting access from this setting.</>,
      'If this PC is not approved or assigned, the owner can choose it under Friend access and approve it or add this server. Then check hosting access again.'
    ], note: 'The owner controls this setting. You cannot turn it on from a Friend PC.', actions: [retry]
  }
}

function serverHelp(status: LocalHostingStatus, props: LocalHostingGuideProps): Help {
  const gameActions: Action[] = [
    { label: 'Choose the installed server file', run: props.onChooseServer },
    { label: 'Review game setup', run: props.onGameSetup }
  ]
  const match = status.gameVersion ? `Use version ${status.gameVersion} to match this save.` : 'Check the game version with the current Host and use the matching dedicated server.'
  if (status.code === 'LocalServerSetupRequired' || status.remedy?.kind === 'GameSetup') return {
    title: 'Match this save\'s game setup', instructions: [
      'Open game setup for this local server and review the required game version and add-ons.',
      `${match} Install and enable the same required add-ons yourself.`,
      'Return here after saving the setup and retry the checks. An installed server file alone does not confirm that add-ons match.'
    ], actions: [{ label: 'Review game setup', run: props.onGameSetup },
      { label: 'Open this PC\'s server setup', run: props.onOpenLocalServer }]
  }
  const install: Record<string, ReactNode[]> = {
    Valheim: [
      'In Steam, open Library and include Tools. Find Valheim Dedicated Server and install it on this PC if it is missing.',
      'In its Steam properties, choose Installed Files, then Browse. Select valheim_server.exe in the Installed server file field here.',
      `${match} No command-line tool is needed when Valheim Dedicated Server is already installed.`
    ],
    MinecraftJava: [
      props.onGameSetup ? 'Open game setup for Minecraft Java. Choose a TogetherServer-managed official server installation, or deliberately install a fresh one there.' :
        'On this PC, open Host, then New server. Choose Minecraft Java and open the Server app step. Search this PC for a TogetherServer-managed official server installation, or choose Install a new official Java server.',
      'Read the Minecraft EULA and Microsoft Privacy Statement and decide whether to accept them on this PC. A game client or an arbitrary server.jar is not a verified server install.',
      `${match} Choose the managed server.jar here. Game setup prepares its matching Java runtime; return here and retry the checks.`
    ],
    MinecraftBedrock: [
      props.onGameSetup ? 'Open game setup for Minecraft Bedrock. Find an installed official Bedrock dedicated server, or deliberately install a fresh one there.' :
        'On this PC, open Host, then New server. Choose Minecraft Bedrock and open the Server app step. Search this PC for an installed official Bedrock dedicated server, or choose Install a new official Bedrock server.',
      'Read the Minecraft EULA and Microsoft Privacy Statement and decide whether to accept them on this PC.',
      `${match} Choose bedrock_server.exe in the Installed server file field, then retry the checks.`
    ],
    Factorio: [
      'Install the matching official Factorio server yourself using your game account or existing installation.',
      'Choose factorio.exe from that installation in the Installed server file field.',
      `${match} Review required add-ons in game setup before continuing.`
    ],
    Terraria: [
      'Install the matching official Terraria dedicated server yourself using your game installation.',
      'Choose TerrariaServer.exe in the Installed server file field.',
      `${match} Terraria hosting is a preview; Friend Stop and automatic Stop remain unavailable without a trusted player count.`
    ]
  }
  if (status.steps[1].state === 'Ready' && !status.needsServerFile && status.code !== 'MinecraftTermsRequired') return {
    title: 'The server app is available on this PC', instructions: [
      match,
      'If the game or its add-ons changed, review game setup before continuing. Start checks the selected installation again.'
    ], actions: [{ label: 'Review game setup', run: props.onGameSetup }]
  }
  return {
    title: status.code === 'MinecraftTermsRequired' || status.remedy?.kind === 'Terms' ? 'Review Minecraft\'s terms on this PC' : 'Install or select the dedicated server',
    instructions: [...(install[status.game ?? ''] ?? [
      'Ask the owner which dedicated server and version this save needs.',
      'Install that server yourself, then choose its installed server file here.',
      'Use game setup to review any required tools or add-ons, then retry the checks.'
    ]), ...(!props.onGameSetup && ['MinecraftJava', 'MinecraftBedrock'].includes(status.game ?? '') ? [
      'After finding or installing the server, note its Server file under Install location. Close that setup without Save and start. Return to Join, select this shared server, open Host locally and choose the installed file here.'
    ] : [])],
    note: status.needsPassword ? 'Enter a new game password in this dialog before continuing.' : 'Game terms and installation stay your choice. Choosing a file does not start the server.',
    actions: gameActions
  }
}

function saveHelp(status: LocalHostingStatus, props: LocalHostingGuideProps): Help {
  const review: Action = { label: 'Review the shared saves', run: props.onReviewSharedSave }
  const retry: Action = { label: 'Recheck the save', run: props.onRetry }
  if (historyCodes.includes(status.code) || status.remedy?.kind === 'ChooseHistory') return {
    title: 'Ask the owner to review different progress', instructions: [
      'Keep every existing save. Two histories have different progress, so the app cannot choose a continuation automatically.',
      'Ask the owner to open Host, select this server, then Backups & sharing and Sharing & hosting to review the different shared saves.',
      'After the owner resolves which history to continue, return here and recheck the save. The guide cannot merge worlds or pick the newest by date.'
    ], actions: [review, retry]
  }
  if (status.consented && (status.code === 'LocalStoppedProgressNeedsSharing' || status.remedy?.kind === 'ShareStoppedProgress')) return {
    title: 'Share this PC\'s stopped progress', instructions: [
      status.message,
      'Keep this server stopped. Choose Share stopped progress in this dialog to verify the stopped save and share it within its existing history. Existing worlds and backups are kept.',
      'If the save or setup cannot be verified, keep every copy and review the completed backup and setup before trying again.',
      'After sharing succeeds, wait for current hosting checks. Choose Start hosting separately when this PC is ready.'
    ], note: 'Rechecking does not repeat sharing or Start. Sharing does not confirm another PC received the save or that the game is ready.',
    actions: [review, retry]
  }
  if (status.code === 'SharedSaveHistoryNeedsReview') return {
    title: 'Review the available shared save', instructions: [
      status.message,
      'Keep every existing save. Selecting and verifying a complete hosting copy still needs review.',
      'Keep TogetherServer open on this PC and on an approved PC with the completed save. Ask the source Host to open Backups & sharing, choose Sharing & hosting and review its sharing notice and completed copy.',
      'Recheck the save to refresh the available information. After reviewing the result, choose Host locally only when you want to retry preparation.'
    ], note: 'Rechecking reads the current checks. It does not retry preparation or select a different save automatically.',
    actions: [review, retry]
  }
  if (status.code === 'RosterRollback') return {
    title: 'Refresh the saved sharing history', instructions: [
      'Keep TogetherServer open on this PC and on the original owner\'s PC. Make sure both apps are up to date.',
      'Choose Check hosting access again to sync missing signed sharing history. Existing saves and the saved connection are kept.',
      'If the same message returns, ask the owner to open this server\'s Backups & sharing, choose Sharing & hosting and review any sharing notice. Do not delete or reset saved history to get past this check.'
    ], note: 'A retry can catch up missing history. Changed, conflicting or damaged history stays blocked for review.',
    actions: [{ label: 'Check hosting access again', run: props.onRetry }, review]
  }
  if (integrityCodes.includes(status.code) || status.code === 'SigningIdentityChanged' || status.remedy?.kind === 'Review') return {
    title: 'Review the save verification problem', instructions: [
      status.message,
      'Keep the existing local save and leave hosting paused. The app could not trust the saved history, identity or file checks.',
      'Ask the owner to open this server\'s Backups & sharing, choose Sharing & hosting and check the sharing notice and completed source copy. An older history or a failed hash check cannot be bypassed.',
      'Keep an approved PC with the verified completed save reachable, then retry. If the same failure returns, review the shared-save details with the owner before continuing.'
    ], actions: [review, retry]
  }
  const invitation = status.handoffRequest
  if (invitation?.recipientState === 'Requested') return {
    title: 'Review the requested hosting move', instructions: [
      'Review who is moving this server and the game setup required on this PC.',
      'Use Host locally below only when you want to accept this move and its network confirmation.',
      'The current Host must finish its final save before this PC can host it. Preparing this PC does not prove that final save has arrived.'
    ], actions: [retry]
  }
  if (invitation && ['Accepted', 'Prepared'].includes(invitation.recipientState ?? '') && !status.readyForManualStart) return {
    title: 'Wait for the current Host\'s final save', instructions: [
      'Keep TogetherServer open on both PCs while this move is prepared.',
      'The current Host finishes the graceful Stop and final completed save. This PC must receive and verify that exact final copy.',
      'Retry the checks if progress pauses. If the current Host cannot finish, ask its owner to review the hosting move in Backups & sharing.'
    ], note: invitation.receiptConfirmed ? 'A copy was acknowledged. The final handoff checks still need to finish.' : 'The final save receipt is not confirmed yet.',
    actions: [review, retry]
  }
  if (status.code === 'LocalHostingPreparationRequired') return {
    title: 'Prepare this PC\'s hosting copy', instructions: [
      status.message,
      'Choose Host locally below after reviewing the network confirmation. The app checks for the latest completed save, receives it if needed and prepares a separate local copy.',
      'Preparation verifies the save files and local setup before Start. Existing saves are kept.'
    ], actions: [review, retry]
  }
  if (missingSaveCodes.includes(status.code) || status.remedy?.kind === 'GetSave') return {
    title: 'Get a completed verified save', instructions: [
      status.message,
      'Keep TogetherServer open on this PC and on an approved PC that has the latest completed save. At least one source PC must be reachable.',
      'On the original Host, open Backups & sharing, choose Sharing & hosting and select Share latest backup to retry an existing completed backup.',
      'If no completed backup exists, ask the current Host to stop the game server safely so a completed save can be shared.',
      'Return here and choose Host locally below after reviewing the network confirmation. The app receives the copy, checks its files and prepares fresh local storage before starting.'
    ], note: 'This PC must receive and verify a complete save before Start.', actions: [review, retry]
  }
  if (status.steps[2].state === 'Ready') return {
    title: 'The local save passed its checks', instructions: [
      'The received copy and its recorded history passed the checks shown above. Existing local progress stays protected.',
      'Start checks for newer completed progress again. A real game load, recognizable saved change and restart remain separate checks.'
    ], actions: [review, retry]
  }
  if (status.steps[2].state === 'Unknown') return {
    title: 'Check the save before preparing it', instructions: [
      'Keep TogetherServer open on this PC and on an approved PC with the completed save.',
      'Recheck the save and wait for its receipt, history and file checks to finish.',
      'If the result stays unavailable, open the shared-save details and ask the source Host to review any sharing notice.'
    ], note: 'An unknown check is not a verified save. Keep the existing copies.', actions: [review, retry]
  }
  return {
    title: 'Prepare and verify this PC\'s copy', instructions: [
      'Keep TogetherServer open on a reachable approved PC with the latest completed save.',
      'Choose Host locally below after reviewing the network confirmation. The app gets the latest available copy and verifies it in fresh local storage before starting.',
      'If preparation stops, follow its specific check here and retry. Existing saves stay available for review.'
    ], actions: [review, ...(props.onPrepare ? [{ label: 'Prepare a checked copy', run: props.onPrepare }] : []), retry]
  }
}

function portsHelp(status: LocalHostingStatus, props: LocalHostingGuideProps): Help {
  return { title: status.steps[3].state === 'Ready' ? 'Check the local port plan' : 'Free the game ports on this PC',
    instructions: [
      `Open this PC's local servers and inspect the server using game port ${status.gamePort}. Some games also need a second game port or a separate local control port.`,
      'If another server you own uses a required port, stop it normally when everyone is finished. Otherwise, open this server\'s game setup and choose a free game port. Leave unrelated apps running.',
      'Save the port change and return here to retry the checks. A free or open port on this PC does not prove another PC can connect.'
    ], note: status.game === 'Factorio' ? 'Keep Factorio\'s RCON port private. Share only its game port and TogetherServer\'s Friend connection.' : undefined,
    actions: [{ label: 'Inspect this PC\'s local server', run: props.onOpenLocalServer },
      { label: 'Review game setup', run: props.onGameSetup }, { label: 'Recheck the local ports', run: props.onRetry }]
  }
}

function connectionHelp(status: LocalHostingStatus, props: LocalHostingGuideProps): Help {
  return { title: status.steps[4].state === 'Ready' ? 'Check Friend access and the game separately' : 'Set up connections to this PC',
    instructions: [
      `Open this PC's Connection help. Choose the connection you intend to use and review the Friend app address and TCP port ${status.controlPort}. The game connection uses its separate game ports.`,
      'For a private network, both PCs need access to the same owner-managed network. For direct internet access, follow Connection help to review Windows Firewall, router forwarding and any shared-address ISP limits. Make only the network changes you choose.',
      'After hosting starts, ask an approved friend on the intended network to connect with TogetherServer, then join in the game. Test from another network before relying on internet access.'
    ], note: status.steps[4].state === 'Ready' ?
      'An outside TCP check verifies only that port. Pinned Friend access, a real game join and a saved restart need separate checks.' :
      'A local-only address or open local listener is not outside-network proof. The app connection and a real game join must be checked separately.',
    actions: [{ label: 'Check this PC\'s connection', run: props.onConnectionHelp },
      { label: 'Open this PC\'s connection details', run: props.onOpenLocalServer },
      { label: 'Recheck connection setup', run: props.onRetry }]
  }
}

function waitingForAccess(key: StepKey, status: LocalHostingStatus, props: LocalHostingGuideProps, showPermission: () => void): Help {
  const history = localHostingAccessBlocker(status) === 'history'
  const check = guideSteps.find(step => step.key === key)!.label.toLowerCase()
  return {
    title: history ? `Review sharing history before checking ${check}` : `Check hosting access before checking ${check}`,
    instructions: [
      `The ${check} check has not run in this attempt because ${history ? 'sharing history needs review' : 'hosting access has not been verified'}.`,
      history ? 'Keep every existing save. Follow the permission step to review the sharing notice with the owner, then check again.' :
        'Follow the permission step first. After this PC has current hosting access, check again to see what it needs next.',
      'This waiting result does not show a problem with the server app, save files, ports or network.'
    ], actions: [
      { label: 'Show the permission steps', run: showPermission },
      ...(history ? [{ label: 'Review the shared saves', run: props.onReviewSharedSave }] : []),
      { label: 'Check hosting access again', run: props.onRetry }
    ]
  }
}

export function LocalHostingGuide(props: LocalHostingGuideProps) {
  const { status, selectedStep = null, recommendedStep, busy, checking = false, checksCurrent = true, feedback, onRetry } = props
  const guideHeading = useRef<HTMLHeadingElement>(null)
  useEffect(() => {
    if (!selectedStep) return
    guideHeading.current?.focus({ preventScroll: true })
    guideHeading.current?.scrollIntoView?.({ block: 'start', inline: 'nearest' })
  }, [selectedStep])
  const firstUnresolved = recommendedStep ?? (status ? status.steps.find(step => step.state !== 'Ready')?.key ?? 'connection' : 'permission')
  const revision = status ? JSON.stringify([status.code, status.allowed, status.readyForManualStart,
    status.handoffRequest?.recipientState, status.steps.map(step => step.state), recommendedStep]) : 'unavailable'
  const [choice, setChoice] = useState<{ key: StepKey; revision: string; requested: StepKey | null } | null>(null)
  const key = choice?.revision === revision && choice.requested === selectedStep ? choice.key : selectedStep ?? firstUnresolved
  const index = guideSteps.findIndex(step => step.key === key)
  const select = (next: StepKey) => setChoice({ key: next, revision, requested: selectedStep })
  const help = status ? localHostingAccessBlocker(status) && key !== 'permission'
    ? waitingForAccess(key, status, props, () => select('permission'))
    : ({ permission: permissionHelp, server: serverHelp, save: saveHelp,
      ports: portsHelp, connection: connectionHelp })[key](status, props) : {
    title: 'Retry the hosting checks', instructions: [
      'Keep TogetherServer open and choose the saved Host connection for this server in Join.',
      'Retry the hosting checks. Wait for current permission, game and save results before continuing.'
    ], note: 'Unavailable checks stay unverified. Retrying here only reads the checks.', actions: [{ label: 'Retry hosting checks', run: onRetry }]
  }
  const selectedCheck = status?.steps.find(step => step.key === key)
  const waiting = status && key !== 'permission' && localHostingAccessBlocker(status)
  const resultLabel = checking ? 'Checking' : !checksCurrent && status ? 'Previous result' : waiting || !selectedCheck ? 'Not checked' :
    selectedCheck.state === 'Ready' ? 'Checked' : selectedCheck.state === 'ActionRequired' ? 'Needs attention' :
      key === 'save' && !status?.localProfileId ? 'Not checked' : 'Unverified'
  const resultMessage = checking ? 'Wait for the current check to finish. The steps below remain available to read.' :
    waiting ? `This check is waiting for ${waiting === 'history' ? 'sharing history review' : 'hosting access to be verified'}. Follow the permission step first, then retry.` :
    !checksCurrent && status ? 'This is the last available result. Retry the checks before starting.' :
    key === 'save' && selectedCheck?.state === 'Unknown' && !status?.localProfileId ?
      'Receiving and file verification run when you choose Host locally after reviewing the confirmation.' :
    selectedCheck?.message ?? 'Current hosting access and setup results are not available yet. Retry the checks.'
  return <section className="local-hosting-guide" aria-label="Local hosting setup guide" aria-busy={busy}>
    <div className="local-hosting-guide-heading"><h3>Set up hosting on this PC</h3><span>Step {index + 1} of {guideSteps.length}</span></div>
    <nav aria-label="Hosting setup steps"><ol>{guideSteps.map((step, stepIndex) => <li key={step.key}>
      <Button className="text-button" aria-current={key === step.key ? 'step' : undefined}
        onClick={() => select(step.key)}>{stepIndex + 1}. {step.label}</Button>
    </li>)}</ol></nav>
    <div className="local-hosting-guide-content" data-step={key}>
      <p className="local-hosting-guide-result"><strong>{resultLabel}:</strong> {resultMessage}</p>
      <h4 ref={guideHeading} tabIndex={-1}>{help.title}</h4>
      <p className="local-hosting-guide-next-label">What to do next</p>
      <ol>{help.instructions.map((instruction, instructionIndex) => <li key={instructionIndex}>{instruction}</li>)}</ol>
      {help.note && <p className="local-hosting-guide-note">{help.note}</p>}
      <div className="local-hosting-guide-actions">{help.actions?.filter(action => action.run).map(action =>
        <Button key={action.label} className="secondary" disabled={busy} onClick={action.run}>{action.label}</Button>)}</div>
      {feedback && <p className="local-hosting-guide-feedback" role="status" aria-live="polite" aria-atomic="true">{feedback}</p>}
    </div>
    <div className="local-hosting-guide-navigation">
      <Button className="text-button" disabled={index === 0} onClick={() => select(guideSteps[index - 1].key)}>Previous step</Button>
      <Button className="text-button" disabled={index === guideSteps.length - 1} onClick={() => select(guideSteps[index + 1].key)}>Next step</Button>
    </div>
  </section>
}
