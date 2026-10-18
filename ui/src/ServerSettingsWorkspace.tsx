import { useEffect, useId, useState, type ReactNode } from 'react'
import { Button } from './Controls'
import { Icon } from './Icon'

/** Keep both editors mounted so changing sections never loses an unfinished draft. */
export type EditorRecoveryRequest = { section: 'settings' | 'files'; source: string; revision: number }

export function ServerSettingsWorkspace({ settings, files, recoveryRequest }: {
  settings: ReactNode; files: ReactNode; recoveryRequest?: EditorRecoveryRequest | null
}) {
  const id = useId()
  const [section, setSection] = useState<'settings' | 'files'>('settings')
  useEffect(() => {
    if (!recoveryRequest) return
    setSection(recoveryRequest.section)
    document.getElementById(`${id}-${recoveryRequest.section}-tab`)?.focus({ preventScroll: true })
  }, [id, recoveryRequest])
  const sections = [
    { id: 'settings' as const, label: 'Game settings', icon: 'settings' as const },
    { id: 'files' as const, label: 'Server files', icon: 'folder' as const }
  ]
  return <div className="server-settings-workspace">
    {recoveryRequest && <p className="settings-hidden-feedback" role="status"><Icon name="warning" />
      Review the open {recoveryRequest.section === 'files' ? 'file' : 'game settings'} draft before leaving.
      {section !== recoveryRequest.section && <Button className="secondary" onClick={() => setSection(recoveryRequest.section)}>Show draft</Button>}
    </p>}
    <div className="workspace-tabs" role="tablist" aria-label="Settings & files sections">
      {sections.map((item, index) => <Button key={item.id} id={`${id}-${item.id}-tab`} role="tab"
        className={section === item.id ? 'secondary selected' : 'secondary'} aria-selected={section === item.id}
        aria-controls={`${id}-${item.id}-panel`} tabIndex={section === item.id ? 0 : -1}
        onClick={() => setSection(item.id)} onKeyDown={event => {
          const next = event.key === 'Home' ? 0 : event.key === 'End' ? sections.length - 1
            : event.key === 'ArrowRight' ? (index + 1) % sections.length
              : event.key === 'ArrowLeft' ? (index + sections.length - 1) % sections.length : null
          if (next === null) return
          event.preventDefault()
          setSection(sections[next].id)
          document.getElementById(`${id}-${sections[next].id}-tab`)?.focus()
        }}><Icon name={item.icon} />{item.label}</Button>)}
    </div>
    <div id={`${id}-settings-panel`} role="tabpanel" aria-labelledby={`${id}-settings-tab`} hidden={section !== 'settings'}>{settings}</div>
    <div id={`${id}-files-panel`} role="tabpanel" aria-labelledby={`${id}-files-tab`} hidden={section !== 'files'}>{files}</div>
  </div>
}
