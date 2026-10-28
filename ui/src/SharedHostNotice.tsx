import { useEffect, useRef, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { Button } from './Controls'
import { Icon } from './Icon'
import { parseBasicResult } from './contracts'
import { sharedSaveAge } from './sharedWorldUx'
import { parseWorldPeerGroups, type WorldPeerGroup } from './WorldPeerPanel'
import './SharedHostNotice.css'

export type SharedHostNoticeProps = {
  profileId: string
  connectionId: string | null
  currentHostAddress?: string | null
  currentHostSessionId?: string | null
  visible?: boolean
  disabled?: boolean
  onConnected?: () => void | Promise<void>
}

type HostChoice = {
  key: string
  group: WorldPeerGroup
  deviceId: string
  sessionId: string
  addresses: string[]
  announcedUtc: string
  current: boolean
  savedAddress: boolean
  preparing: boolean
}
type StoppedReport = { deviceId: string; endedUtc: string }
type HostGroup = { group: WorldPeerGroup; choices: HostChoice[]; stopped: StoppedReport | null; competing: boolean; needsReview: boolean }
type View = { state: 'checking' | 'ready' | 'unknown'; groups: WorldPeerGroup[]; error?: string }
const hidden = () => document.visibilityState === 'hidden'

function hostAddress(value: string | null | undefined): string | null {
  if (!value) return null
  try {
    const address = new URL(value)
    return address.protocol === 'https:' && !address.username && !address.password && !address.search && !address.hash
      && (address.pathname === '' || address.pathname === '/') ? address.origin : null
  } catch { return null }
}

function hostingGroups(groups: WorldPeerGroup[], profileId: string, currentHostAddress?: string | null,
  currentHostSessionId?: string | null): HostGroup[] {
  const currentAddress = hostAddress(currentHostAddress)
  return groups.filter(group => group.profileId === profileId).map(group => {
    const remoteReports = group.state.sessions.filter(item => item.authorized && !item.hasConflict && item.deviceId !== group.selfDeviceId)
      .filter(item => {
        const peers = group.state.peers.filter(peer => peer.deviceId === item.deviceId && peer.publicKey === item.publicKey)
        return peers.length === 1 && peers[0].authorized && !peers[0].hasConflict
      })
    const hosting = group.state.sessions.filter(item => item.authorized && !item.hasConflict
      && (item.session.state === 'Hosting' || item.session.state === 'Preparing'))
    const choices = hosting.flatMap(item => {
      if (item.deviceId === group.selfDeviceId) return []
      const peers = group.state.peers.filter(peer => peer.deviceId === item.deviceId && peer.publicKey === item.publicKey)
      const peer = peers.length === 1 ? peers[0] : null
      if (!peer?.authorized || peer.hasConflict) return []
      const addresses = [...new Set(peer.routes.map(route => hostAddress(route.address)).filter((address): address is string => !!address))]
      if (!addresses.length) return []
      return [{ key: JSON.stringify([group.groupKey, item.deviceId, item.publicKey, item.session.sessionId, item.session.branchHash]),
        group, deviceId: item.deviceId, sessionId: item.session.sessionId, addresses, announcedUtc: peer.announcedUtc,
        current: !!currentHostSessionId && item.session.sessionId === currentHostSessionId && currentAddress !== null && addresses.includes(currentAddress),
        savedAddress: currentAddress !== null && addresses.includes(currentAddress), preparing: item.session.state === 'Preparing' }]
    })
    const datedReports = remoteReports.map(report => ({ report,
      reportedAt: Date.parse(report.session.endedUtc ?? report.session.startedUtc ?? '') }))
    const latestReport = datedReports.every(item => Number.isFinite(item.reportedAt))
      ? datedReports.sort((left, right) => right.reportedAt - left.reportedAt)[0]?.report : undefined
    const stopped = choices.length === 0 && latestReport?.session.state === 'Stopped' && latestReport.session.endedUtc
      ? { deviceId: latestReport.deviceId, endedUtc: latestReport.session.endedUtc } : null
    return { group, choices, stopped, competing: new Set(hosting.map(item => item.deviceId)).size > 1,
      needsReview: group.state.peers.some(peer => peer.authorized && peer.hasConflict) ||
        group.state.sessions.some(item => item.authorized && item.hasConflict) }
  }).filter(group => group.choices.length > 0 || group.stopped !== null)
}

const reportTitle = ({ stopped, competing, choices }: HostGroup) => stopped ? 'Another PC reported a stopped session' : competing ? 'Multiple PCs report hosting' :
  choices[0].preparing ? choices[0].current ? 'Your saved Host is starting' : 'Another PC is starting the server' :
  choices[0].current ? 'Your saved Host reports hosting' : 'Another PC reports hosting'

/** A saved route identifies the current connection; it does not establish live reachability. */
export function SharedHostNotice(props: SharedHostNoticeProps) {
  return <SharedHostNoticeScope key={JSON.stringify([props.profileId, props.connectionId, props.currentHostAddress,
    props.currentHostSessionId, props.visible ?? true])} {...props} />
}

function SharedHostNoticeScope({ profileId, currentHostAddress, currentHostSessionId, visible = true, disabled = false,
  onConnected }: SharedHostNoticeProps) {
  const [view, setView] = useState<View>({ state: 'checking', groups: [] })
  const [pageVisible, setPageVisible] = useState(!hidden())
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null)
  const groups = useRef<WorldPeerGroup[]>([])
  const epoch = useRef(0)
  const read = useRef<AbortController | null>(null)
  const write = useRef<AbortController | null>(null)
  const reload = useRef<(() => void) | null>(null)

  useEffect(() => {
    if (!visible) return
    let active = true
    let timer: number | undefined
    const cancel = () => {
      ++epoch.current
      read.current?.abort(); read.current = null
      write.current?.abort(); write.current = null
      groups.current = []
      window.clearTimeout(timer)
    }
    const poll = async () => {
      window.clearTimeout(timer)
      if (!active || hidden()) return
      if (read.current || write.current) {
        timer = window.setTimeout(() => void poll(), 5000)
        return
      }
      const request = new AbortController()
      const version = epoch.current
      read.current = request
      const timeout = window.setTimeout(() => {
        if (read.current !== request) return
        request.abort()
        read.current = null
        groups.current = []
        setView({ state: 'unknown', groups: [], error: 'Host updates took too long. Retry to check again.' })
        if (active && !hidden()) timer = window.setTimeout(() => void poll(), 5000)
      }, 12000)
      try {
        const next = await getLocalJson('/api/local/world-peers', parseWorldPeerGroups, request.signal)
        if (!active || request.signal.aborted || epoch.current !== version || read.current !== request || hidden()) return
        groups.current = next
        setView({ state: 'ready', groups: next })
      } catch (cause) {
        if (!active || request.signal.aborted || epoch.current !== version || read.current !== request || hidden()) return
        groups.current = []
        setView({ state: 'unknown', groups: [], error: errorMessage(cause) })
      } finally {
        window.clearTimeout(timeout)
        if (read.current === request) {
          read.current = null
          if (active && !hidden()) timer = window.setTimeout(() => void poll(), 5000)
        }
      }
    }
    reload.current = () => {
      read.current?.abort(); read.current = null
      window.clearTimeout(timer)
      void poll()
    }
    const visibility = () => {
      cancel()
      setPageVisible(!hidden())
      setBusy(null); setMessage(null)
      setView({ state: 'checking', groups: [] })
      if (!hidden()) void poll()
    }
    setPageVisible(!hidden())
    void poll()
    document.addEventListener('visibilitychange', visibility)
    return () => {
      active = false
      cancel()
      reload.current = null
      document.removeEventListener('visibilitychange', visibility)
    }
  }, [visible])

  const connect = async (choice: HostChoice) => {
    if (disabled || !visible || hidden() || write.current) return
    const current = hostingGroups(groups.current, profileId, currentHostAddress, currentHostSessionId)
      .flatMap(group => group.choices).find(item => item.key === choice.key)
    if (!current || current.current) return
    const request = new AbortController()
    const version = epoch.current
    write.current = request
    setBusy(current.key); setMessage(null)
    const stillCurrent = () => !request.signal.aborted && epoch.current === version && write.current === request && !hidden()
    const timeout = window.setTimeout(() => {
      if (!stillCurrent()) return
      request.abort(); write.current = null
      groups.current = []
      setBusy(null)
      const text = 'The connection check took too long. Check your saved Host before trying again.'
      setMessage({ text, error: true })
      setView({ state: 'unknown', groups: [], error: text })
    }, 30000)
    try {
      const result = await changeJson(`/api/local/world-peers/${current.group.groupKey}/connect`, 'POST', parseBasicResult,
        { deviceId: current.deviceId, sessionId: current.sessionId }, request.signal)
      if (!stillCurrent()) return
      setMessage({ text: result.message, error: !result.ok })
      if (result.ok) await onConnected?.()
    } catch (cause) {
      if (stillCurrent()) setMessage({ text: errorMessage(cause), error: true })
    } finally {
      window.clearTimeout(timeout)
      if (stillCurrent()) {
        write.current = null
        setBusy(null)
        reload.current?.()
      }
    }
  }

  if (!visible || !pageVisible) return null
  const shown = hostingGroups(view.groups, profileId, currentHostAddress, currentHostSessionId)
  const announcement = shown.map(item => `${item.group.name}: ${reportTitle(item)}.`).join(' ')
  return <><span className="shared-host-announcement" aria-live="polite" aria-atomic="true">{announcement}</span>
    {(shown.length > 0 || view.state === 'unknown') && <section className="shared-host-notice" aria-label="Shared Host updates">
    {view.state === 'unknown' ? <div className="shared-host-unknown" role="status">
      <Icon name="plug" /><div><strong>Shared Host updates unavailable</strong><p>{view.error}</p>
        <small>Saved reports cannot confirm who is hosting now.</small></div>
      <Button className="secondary" disabled={disabled || busy !== null} onClick={() => reload.current?.()}>
        <Icon name="refresh" />Retry Host updates</Button>
    </div> : shown.map(item => { const { group, choices, stopped, competing, needsReview } = item; return <article key={group.groupKey}
      className="shared-host-group" aria-label={`Hosting reports for ${group.name}`}>
      <header><Icon name={competing ? 'warning' : 'bell'} size={21} /><div>
        <h4>{reportTitle(item)}</h4>
        <p>{group.name}</p></div></header>
      {needsReview && <p className="shared-host-caution">Some PCs reported different server or connection details. Conflicting reports are not offered as connections.</p>}
      {group.warning && <p className="shared-host-caution">{group.warning}</p>}
      {stopped ? <div className="shared-host-details">
        <p>PC ending {stopped.deviceId.slice(-6).toUpperCase()} reported that this hosting session stopped.</p>
        <small>Signed stopped report · Reported stop: <time dateTime={stopped.endedUtc}>{new Date(stopped.endedUtc).toLocaleString()}</time> · {sharedSaveAge(stopped.endedUtc)}.</small>
        <small>Other PCs may still be hosting. Save delivery is checked separately.</small>
      </div> : <>
      {competing && <p className="shared-host-caution">Choose the exact Host you want. These servers may have separate progress.</p>}
      <ul className="shared-host-options">{choices.map(choice => <li key={choice.key}>
        <div className="shared-host-details"><strong>PC ending {choice.deviceId.slice(-6).toUpperCase()}</strong>
          <div className="shared-host-badges">{choice.preparing && <span>Starting</span>}{choice.savedAddress && <span>{choice.current ? 'Current saved Host' : 'Saved Host address'}</span>}</div>
          <div className="shared-host-addresses"><small>TogetherServer HTTPS</small>{choice.addresses.map(address => <code key={address}>{address}</code>)}</div>
          <small>Signed {choice.preparing ? 'starting' : 'hosting'} report · Updated {sharedSaveAge(choice.announcedUtc)}. Reachability is checked when you connect.</small>
          {choice.preparing && <small>Game readiness has not been confirmed. Wait for the server to be ready before joining the game.</small>}</div>
        <Button className="secondary" disabled={disabled || busy !== null || choice.current}
          aria-label={choice.current ? `Current saved Host: PC ending ${choice.deviceId.slice(-6).toUpperCase()}` :
            `Connect to PC ending ${choice.deviceId.slice(-6).toUpperCase()}`}
          onClick={() => void connect(choice)}><Icon name={busy === choice.key ? 'loader' : choice.current ? 'check' : 'link'} />
          {busy === choice.key ? 'Checking Host...' : choice.current ? 'Saved Host' : 'Connect to this Host'}</Button>
      </li>)}</ul>
      <p className="shared-host-footnote">These HTTPS addresses connect the TogetherServer apps. After connecting, open Join for the current game's address and port. Your game and running servers stay open.</p>
      </>}
    </article> })}
    {message && <p className="shared-host-result" role={message.error ? 'alert' : 'status'}>{message.text}</p>}
  </section>}</>
}
