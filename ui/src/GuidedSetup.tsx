import { useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { Button } from './Controls'
import { Icon } from './Icon'
import { SettingsDialog } from './SettingsPresentation'
import type { GameKind } from './GameProfile'
import './GuidedSetup.css'

export type GuideKind = 'host' | 'join'
export type GuideStep = { id: string; title: string; detail: string; target: string; tip: string; status?: ReactNode }

export function GuideWelcome({ intent, busy, error, onStart, onClose }: {
  intent: GuideKind | null; busy: boolean; error?: string; onStart: (kind: GuideKind) => void; onClose: () => void
}) {
  return <SettingsDialog title="Make room for your next game night" description="A little help, right where you need it." busy={busy} onClose={onClose}
    footer={<Button className="text-button" disabled={busy} onClick={onClose}>Set up later</Button>}>
    <div className="guide-welcome">
      <div className="guide-welcome-art" aria-hidden="true"><Icon name="game" size={40} /><span /><span /></div>
      <p>Choose what you want to do. We’ll walk through the app together, from the first click to checking the connection.</p>
      <div className="guide-paths">
        {(['host', 'join'] as const).map(kind => <Button key={kind} className={`secondary guide-path${intent === kind ? ' guide-path-preferred' : ''}`}
          disabled={busy} onClick={() => onStart(kind)}>
          <Icon name={kind === 'host' ? 'server' : 'link'} size={25} />
          <span><strong>{kind === 'host' ? 'Host on this PC' : 'Join a friend'}</strong>
            <small>{kind === 'host' ? 'Choose a game, prepare its server app, and invite friends.' : 'Use a server code, check your access, and get into the game.'}</small>
            {intent === kind && <small className="guide-recommended">Your installer choice</small>}</span><Icon name="chevronRight" />
        </Button>)}
      </div>
      <p className="guide-footnote"><Icon name="shield" />You choose when to install game files, start a server, or allow Friend connections.</p>
      <p className="guide-footnote">Open Guides in the top bar whenever you want to return.</p>
      {error && <p className="warning-text" role="alert">{error}</p>}
    </div>
  </SettingsDialog>
}

/** The real app stays interactive. A guide can navigate, but never clicks an action for the owner. */
export function GuidedWalkthrough({ kind, steps, stepIndex, suspended, busy, onStep, onLocate, onClose, onFinish }: {
  kind: GuideKind; steps: GuideStep[]; stepIndex: number; suspended: boolean; busy: boolean
  onStep: (index: number) => void; onLocate: (index: number) => void; onClose: () => void; onFinish: () => void
}) {
  const id = useId()
  const panelRef = useRef<HTMLElement>(null)
  const [position, setPosition] = useState<CSSProperties>({ right: 20, bottom: 36 })
  const [modalOpen, setModalOpen] = useState(false)
  const step = steps[stepIndex] ?? steps[0]
  const stepId = step?.id
  const targetSelector = step?.target

  useEffect(() => {
    const update = () => setModalOpen(!!document.querySelector('dialog[open]'))
    const observer = new MutationObserver(update)
    observer.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['open'] })
    update()
    return () => observer.disconnect()
  }, [])
  const hidden = suspended || modalOpen

  useLayoutEffect(() => {
    if (hidden || !targetSelector) return
    let highlighted: HTMLElement | null = null
    const update = () => {
      const target = document.querySelector<HTMLElement>(targetSelector)
      const bounds = target?.getBoundingClientRect()
      const usable = target && bounds && bounds.width > 0 && bounds.height > 0 && bounds.bottom > 14 && bounds.top < window.innerHeight - 28 && !target.closest('[hidden], [inert], dialog:not([open])')
      if (highlighted !== target || !usable) highlighted?.removeAttribute('data-guide-highlight')
      highlighted = usable ? target : null
      highlighted?.setAttribute('data-guide-highlight', 'true')
      const margin = 14, gap = 16
      const header = document.querySelector<HTMLElement>('.app-shell > .topbar')
      const topEdge = Math.max(margin, (header?.getBoundingClientRect().bottom ?? 0) + margin)
      const availableHeight = Math.max(0, window.innerHeight - topEdge - 36)
      const width = Math.min(360, window.innerWidth - margin * 2)
      const panel = panelRef.current
      const content = panel?.querySelector<HTMLElement>('.guide-step-content')
      const hiddenContent = content ? Math.max(0, content.scrollHeight - content.clientHeight) : 0
      const height = panel ? Math.max(panel.scrollHeight + hiddenContent + 2, panel.getBoundingClientRect().height) : 310
      const left = Math.max(margin, Math.min(bounds?.left ?? window.innerWidth - width - margin, window.innerWidth - width - margin))
      let top = window.innerHeight - Math.min(height, availableHeight) - 36
      let maxHeight = availableHeight
      if (usable && bounds) {
        if (bounds.bottom + gap + height < window.innerHeight - 28) top = bounds.bottom + gap
        else if (bounds.top - gap - height > topEdge) top = bounds.top - gap - height
        else if (bounds.right + gap + width < window.innerWidth - margin) {
          setPosition({ left: bounds.right + gap, top: Math.max(topEdge, Math.min(bounds.top, window.innerHeight - Math.min(height, availableHeight) - 36)), width, maxHeight }); return
        } else if (bounds.left - gap - width > margin) {
          setPosition({ left: bounds.left - gap - width, top: Math.max(topEdge, Math.min(bounds.top, window.innerHeight - Math.min(height, availableHeight) - 36)), width, maxHeight }); return
        } else {
          // A short/narrow viewport may not fit the full coach beside its action.
          // Keep the action exposed and scroll the coach within the available space.
          const above = bounds.top - gap - topEdge
          const below = window.innerHeight - bounds.bottom - gap - 36
          if (above >= below) { top = topEdge; maxHeight = Math.max(0, above) }
          else { top = bounds.bottom + gap; maxHeight = Math.max(0, below) }
        }
      }
      setPosition({ left, top: Math.max(topEdge, top), width, maxHeight })
    }
    const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(update) : null
    if (panelRef.current) observer?.observe(panelRef.current)
    const header = document.querySelector<HTMLElement>('.app-shell > .topbar')
    if (header) observer?.observe(header)
    const target = document.querySelector<HTMLElement>(targetSelector)
    if (target) observer?.observe(target)
    window.addEventListener('resize', update)
    window.addEventListener('scroll', update, true)
    update()
    return () => {
      highlighted?.removeAttribute('data-guide-highlight')
      observer?.disconnect()
      window.removeEventListener('resize', update)
      window.removeEventListener('scroll', update, true)
    }
  }, [hidden, stepId, targetSelector])

  const closeRef = useRef(onClose)
  useEffect(() => { closeRef.current = onClose }, [onClose])
  useEffect(() => {
    if (hidden) return
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented) return
      event.preventDefault(); event.stopImmediatePropagation(); closeRef.current()
    }
    document.addEventListener('keydown', escape, true)
    return () => document.removeEventListener('keydown', escape, true)
  }, [hidden])

  if (hidden || !step) return null
  return <aside ref={panelRef} className="guide-coach" style={position} aria-labelledby={`${id}-title`}>
    <div className="guide-coach-top"><span><Icon name={kind === 'host' ? 'server' : 'link'} />{kind === 'host' ? 'Host setup' : 'Join a friend'}</span>
      <Button className="text-button icon-button" aria-label="Pause walkthrough" onClick={onClose}><Icon name="close" /></Button></div>
    <div className="guide-step-dots" aria-label={`Step ${stepIndex + 1} of ${steps.length}`}>
      {steps.map((item, index) => <span key={item.id} className={index <= stepIndex ? 'visited' : ''} />)}</div>
    <div key={step.id} className="guide-step-content">
      <h2 id={`${id}-title`} tabIndex={-1}>{step.title}</h2><p>{step.detail}</p>
      {step.status && <div className="guide-live-checks">{step.status}</div>}
      <p className="guide-coach-tip"><Icon name="plug" size={15} />{step.tip}</p>
    </div>
    <footer><Button className="text-button" disabled={busy} onClick={() => onLocate(stepIndex)}>Show me</Button>
      <div className="actions">{stepIndex > 0 && <Button className="secondary" disabled={busy} onClick={() => onStep(stepIndex - 1)}>Back</Button>}
        <Button disabled={busy} onClick={() => stepIndex + 1 === steps.length ? onFinish() : onStep(stepIndex + 1)}>
          {stepIndex + 1 === steps.length ? 'Finish guide' : 'Next tip'}<Icon name="chevronRight" size={15} /></Button></div></footer>
    <span className="sr-only" aria-live="polite" aria-atomic="true">Step {stepIndex + 1} of {steps.length}: {step.title}</span>
  </aside>
}

export function SetupStageTip({ kind, stage }: { kind: GameKind; stage: string }) {
  const text = stage === 'game' ? 'Pick the game your friends will play. Java and Bedrock are separate editions; everyone needs the matching edition and version.'
    : stage === 'world' ? 'Choose a new world or copy an existing save. Keep the original safe. Give friends the game password separately from the app’s server code.'
      : stage === 'server' ? kind === 'Valheim'
        ? 'Install Valheim Dedicated Server from Steam’s Tools library, then use Search this PC. SteamCMD is an optional way to install it, not a requirement to run it here.'
        : kind === 'MinecraftJava' ? 'The guided install prepares the official server JAR and a matching Java runtime. Review the terms yourself before installing; an arbitrary JAR or Java on PATH is not enough.'
          : kind === 'MinecraftBedrock' ? 'Choose a prepared Bedrock server or use the official install. Review the terms before installing. Java and SteamCMD are not needed for Bedrock.'
            : kind === 'Factorio' ? 'Choose your installed Factorio server and an existing save. Keep its RCON port private; friends use the game port.'
              : kind === 'Terraria' ? 'Choose your installed Terraria server and copy a world. Player counts are unverified, so Friend and automatic Stop stay unavailable.'
                : 'Choose the server app this PC will run. Advanced custom servers need their own reviewed controls and checks.'
        : 'Review the files, save location, and ports before saving. Start is your choice. After it starts, use Network to check app access, then test a real game join and saved change.'
  return <aside className="setup-stage-tip" aria-label="Setup tip"><Icon name="plug" /><p>{text}</p></aside>
}
