import { useCallback, useEffect, useState } from 'react'
import { changeJson, errorMessage, getLocalJson } from './api'
import { parseLocalProfile, parseLocalProfileResult } from './contracts'
import { Button, Input } from './Controls'

export function profileNameError(value: string): string {
  if (/[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(value)) return 'Use a single line without invalid, control or formatting characters.'
  if (value.trim().length > 80) return 'Use 80 characters or fewer.'
  return ''
}

export function useLocalProfile() {
  const [displayName, setDisplayName] = useState<string | null>(null)
  const [draft, setDraft] = useState('')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [message, setMessage] = useState('')
  const reload = useCallback(async (signal?: AbortSignal) => {
    setLoading(true); setError('')
    try {
      const result = await getLocalJson('/api/local/profile', parseLocalProfile, signal)
      if (signal?.aborted) return
      setDisplayName(result.displayName); setDraft(result.displayName)
    } catch (failure) { if (!signal?.aborted) setError(errorMessage(failure)) }
    finally { if (!signal?.aborted) setLoading(false) }
  }, [])
  useEffect(() => {
    const controller = new AbortController()
    void reload(controller.signal)
    return () => controller.abort()
  }, [reload])
  const save = async () => {
    if (saving || loading || displayName === null) return
    const validation = profileNameError(draft)
    if (validation) { setError(validation); return }
    setSaving(true); setError(''); setMessage('')
    try {
      const result = await changeJson('/api/local/profile', 'PUT', parseLocalProfileResult, { displayName: draft.trim() })
      if (!result.ok) { setError(result.message); return }
      setDisplayName(result.displayName); setDraft(result.displayName)
      setMessage(result.displayName ? 'Profile saved. Your name is shared with connected PCs.' : 'Profile cleared. Your shared name is empty.')
    } catch (failure) { setError(errorMessage(failure)) }
    finally { setSaving(false) }
  }
  return { displayName, draft, loading, saving, error, message, reload, save,
    edit: (value: string) => { setDraft(value); setError(''); setMessage('') },
    reset: () => { setDraft(displayName ?? ''); setError(''); setMessage('') } }
}

export type ProfileEditorState = ReturnType<typeof useLocalProfile>

export function ProfileSettings({ profile }: { profile: ProfileEditorState }) {
  const validation = profileNameError(profile.draft)
  const dirty = profile.displayName !== null && profile.draft !== profile.displayName
  return <form className="settings-dialog-stack" aria-label="Shared profile" aria-busy={profile.loading || profile.saving}
    onSubmit={event => { event.preventDefault(); void profile.save() }}>
    <p>Your name is automatically shared with Hosts you join and Friends who connect to you. Choose a name they recognize.</p>
    <label>Your name<Input value={profile.draft} disabled={profile.loading || profile.saving || profile.displayName === null}
      autoComplete="off" aria-describedby="profile-name-help" aria-invalid={!!validation}
      onChange={event => profile.edit(event.target.value)} /></label>
    <p id="profile-name-help" className="helper-text">Up to 80 characters. Leave blank to clear your name. This is a self-reported name, not verified identity. PC labels and saved Host names stay separate.</p>
    {profile.loading && <p role="status">Loading profile…</p>}
    {(validation || profile.error) && <p role="alert" className="warning-text">{validation || profile.error}</p>}
    {profile.message && <p role="status">{profile.message}</p>}
    {profile.displayName === null && !profile.loading ? <Button className="secondary" onClick={() => void profile.reload()}>Retry profile</Button> :
      <div className="actions"><Button type="submit" disabled={!dirty || !!validation || profile.loading || profile.saving}>{profile.saving ? 'Saving…' : 'Save name'}</Button>
        <Button className="secondary" disabled={!dirty || profile.saving} onClick={profile.reset}>Discard changes</Button></div>}
    {dirty && <p className="helper-text">Unsaved name. Your draft stays here while you move between pages.</p>}
  </form>
}

export function ProfileAccess({ profile, onOpen }: { profile: ProfileEditorState; onOpen: () => void }) {
  return <div className="profile-access"><span>{profile.displayName ? <>Sharing as <strong>{profile.displayName}</strong></> : 'Help friends recognize you'}</span>
    <Button className="text-button" onClick={onOpen}>{profile.displayName ? 'Edit profile' : 'Set your name'}</Button></div>
}
