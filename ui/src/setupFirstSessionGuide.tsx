import { Button } from './Controls'
import { profileGameLabel, type Profile } from './GameProfile'

export type FirstSessionTool = 'start' | 'invite' | 'connection' | 'acceptance' | 'backups'
export function SetupFirstSessionGuide({ profile, state, onOpenTool, onDismiss }: {
  profile: Profile; state: string; onOpenTool: (tool: FirstSessionTool) => void; onDismiss?: () => void
}) {
  return <section className="panel" aria-label="First session guide"><div className="section-heading"><div>
    <h3>Try your first session</h3><p>{profile.name || profileGameLabel(profile)} is saved. Try joining and saving before using automatic features.</p>
  </div>{onDismiss && <Button className="text-button" onClick={onDismiss}>Close guide</Button>}</div>
    <ol>
      <li><strong>{state === 'Ready' ? 'Server reports Ready.' : 'Start and wait for Ready.'}</strong> Then check that you can join in the game.
        <Button className="secondary" onClick={() => onOpenTool(state === 'Ready' ? 'connection' : 'start')}>{state === 'Ready' ? 'Check connections' : 'Open server controls'}</Button></li>
      <li><strong>Invite one Friend.</strong> Connect their TogetherServer app, then have them join in the game.
        <Button className="secondary" onClick={() => onOpenTool('invite')}>Invite a Friend</Button></li>
      <li><strong>Make and save a recognizable change.</strong> Use Test with a Friend to check join, graceful Stop, and restart with that change still present.
        <Button className="secondary" onClick={() => onOpenTool('acceptance')}>Test with a Friend</Button></li>
      <li><strong>Review your protection.</strong> Check a completed backup and test restoring a copy before relying on recovery.
        <Button className="secondary" onClick={() => onOpenTool('backups')}>Open backups</Button></li>
    </ol>
  </section>
}
