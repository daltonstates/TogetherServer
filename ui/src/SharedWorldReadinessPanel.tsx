import { useState } from 'react'
import { changeJson, errorMessage } from './api'
import { Button, Input } from './Controls'

type Readiness = { ready: boolean; reasons: string[]; version: number | null;
  versionHash: string | null; rehearsalPassed: boolean }

export function parseTakeoverReadiness(value: unknown): Readiness {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Readiness is invalid.')
  const item = value as Record<string, unknown>
  if (typeof item.ready !== 'boolean' || !Array.isArray(item.reasons) ||
    item.reasons.some(reason => typeof reason !== 'string' || reason.length > 300) ||
    (item.version !== null && (typeof item.version !== 'number' || !Number.isSafeInteger(item.version))) ||
    (item.versionHash !== null && (typeof item.versionHash !== 'string' || !/^[0-9A-F]{64}$/.test(item.versionHash))) ||
    typeof item.rehearsalPassed !== 'boolean')
    throw new Error('Readiness is invalid.')
  return item as Readiness
}

export function SharedWorldReadinessPanel({ profileId }: { profileId: string }) {
  const [serverFile, setServerFile] = useState('')
  const [gameVersion, setGameVersion] = useState('')
  const [passwordSet, setPasswordSet] = useState(false)
  const [controlPort, setControlPort] = useState('5131')
  const [gamePort, setGamePort] = useState('')
  const [result, setResult] = useState<Readiness | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const run = async (action: 'readiness' | 'rehearse') => {
    setBusy(true); setError('')
    try {
      setResult(await changeJson(`/api/local/friend/${profileId}/shared-world/${action}`, 'POST',
        parseTakeoverReadiness, { serverFile: serverFile || null, gameVersion: gameVersion || null,
          enabledAddOns: [], newPasswordConfigured: passwordSet,
          controlPort: Number(controlPort), gamePort: Number(gamePort) }))
    } catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  return <details><summary>Check this PC for future hosting</summary>
    <p>Check local files and make a disposable copy of a verified save. Taking over a world is not available yet.</p>
    <label>Installed game server file<Input value={serverFile} onChange={event => setServerFile(event.target.value)}
      placeholder="Path to installed server file" /></label>
    <label>Installed game version<Input value={gameVersion} onChange={event => setGameVersion(event.target.value)} /></label>
    <label>Future Friend control port<Input inputMode="numeric" value={controlPort}
      onChange={event => setControlPort(event.target.value)} /></label>
    <label>Future game port<Input inputMode="numeric" value={gamePort}
      onChange={event => setGamePort(event.target.value)} /></label>
    <label><Input type="checkbox" checked={passwordSet} onChange={event => setPasswordSet(event.target.checked)} />
      I have set a new game password locally</label>
    <p className="helper-text">Matching add-ons, a fresh managed location, real game load, and direct-IP routes still need confirmation before hosting.</p>
    <div className="actions"><Button className="secondary" disabled={busy} onClick={() => void run('readiness')}>
      Check this PC</Button><Button className="secondary" disabled={busy} onClick={() => void run('rehearse')}>
      Make disposable test copy</Button></div>
    {error && <p role="alert">{error}</p>}
    {result && <div role="status"><p>{result.rehearsalPassed ? 'Disposable copy checked and removed.' :
      result.ready ? 'Local checks passed.' : 'Hosting still needs setup.'}</p>
      {result.reasons.length > 0 && <ul>{result.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>}
      {result.rehearsalPassed && <details><summary>Technical details</summary>
        <p>Version: {result.version} · Hash: {result.versionHash}</p></details>}</div>}
  </details>
}
