import { useCallback, useEffect, useRef, useState, type Dispatch, type SetStateAction } from 'react'
import { changeJson, errorMessage, getJson } from '../../api'
import {
  parseActionResult,
  parseBrowseResult,
  parseCustomScriptResult,
  parseDiscovery,
  parseImportResult,
  parseMinecraftBrowseResult,
  parseMinecraftDiscovery,
  parseMinecraftInstallResult,
  parseServerBrowseResult,
  parseWorldBrowseResult,
  type ActionResult,
  type CustomScriptBundle,
  type Discovery,
  type HostSnapshot,
  type Settings,
  type Snapshot
} from '../../contracts'
import type { CustomPort, Profile } from '../../GameProfile'
import type { MinecraftDiscovery, MinecraftInstallation } from '../../MinecraftSetup'
import {
  hasSensitiveSetupDraft,
  readSetupDraftFrom,
  reconcileProfileRemoval,
  removeSetupDraftFrom,
  writeSetupDraftTo
} from '../../setupDraft'
import { getSetupIssues, getStepIssues, isValidGamePassword, type SetupStep } from './HostSetupDialog'

export type SetupNotice = { good: boolean; text: string }

type UseHostSetupOptions = {
  snapshot: Snapshot | null
  pending: string
  setPending: Dispatch<SetStateAction<string>>
  setNotice: Dispatch<SetStateAction<SetupNotice | null>>
  applySnapshot: (snapshot: Snapshot) => void
  dataRecoveryBlocked: boolean
}

const setupDraftKey = 'togetherserver-first-server-draft-v2'

function changeAction(path: string, method: 'POST' | 'PUT', body?: unknown): Promise<ActionResult> {
  return changeJson(path, method, parseActionResult, body)
}

function minecraftProfile(profile: Profile, item: MinecraftInstallation): Profile {
  return {
    ...profile,
    name: profile.name || `${item.kind === 'MinecraftJava' ? 'Java' : 'Bedrock'} server`,
    worldId: item.worldName,
    worldDirectory: item.serverDirectory,
    gamePort: item.gamePort,
    executablePath: item.executablePath || profile.executablePath,
    minecraft: item.kind === 'MinecraftJava' ? { serverJarPath: item.artifactPath } : null
  }
}

function useModalDialog(open: boolean) {
  const ref = useRef<HTMLDialogElement | null>(null)
  useEffect(() => {
    if (!open || !ref.current) return
    const dialog = ref.current
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    dialog.showModal()
    return () => {
      if (dialog.open) dialog.close()
      previousFocus?.focus()
    }
  }, [open])
  return ref
}

export function useHostSetup({ snapshot, pending, setPending, setNotice, applySnapshot,
  dataRecoveryBlocked }: UseHostSetupOptions) {
  const [draft, setDraft] = useState<Settings | null>(null)
  const [dirty, setDirty] = useState(false)
  const [passwords, setPasswords] = useState<Record<string, string>>({})
  const [customScripts, setCustomScripts] = useState<Record<string, CustomScriptBundle>>({})
  const [customScriptsSaved, setCustomScriptsSaved] = useState<Record<string, boolean>>({})
  const [customScriptsLoading, setCustomScriptsLoading] = useState<Record<string, boolean>>({})
  const [discovery, setDiscovery] = useState<Discovery | null>(null)
  const [minecraftDiscovery, setMinecraftDiscovery] = useState<MinecraftDiscovery | null>(null)
  const [minecraftTerms, setMinecraftTerms] = useState<Record<string, boolean>>({})
  const [sourceRoots, setSourceRoots] = useState<Record<string, string>>({})
  const [showSetup, setShowSetup] = useState(false)
  const [setupStep, setSetupStep] = useState<SetupStep>('game')
  const [minecraftSetupMode, setMinecraftSetupMode] = useState<Record<string, 'existing' | 'install'>>({})
  const [showPasswords, setShowPasswords] = useState<Record<string, boolean>>({})
  const [activeProfileId, setActiveProfileId] = useState('')
  const initialDraftSet = useRef(false)
  const dirtyRef = useRef(false)
  const customScriptEditVersionRef = useRef<Record<string, number>>({})
  const customScriptLoadRequestRef = useRef<Record<string, number>>({})
  const setupRef = useModalDialog(showSetup)

  const currentMode = snapshot?.mode
  const hasMinecraftDraft = draft?.profiles.some(profile =>
    profile.kind === 'MinecraftJava' || profile.kind === 'MinecraftBedrock') ?? false
  const hostProfileCount = snapshot?.mode === 'Host' ? snapshot.settings.profiles.length : -1

  useEffect(() => {
    if (currentMode !== 'Host' || !showSetup || !draft || !discovery) return
    const serverPath = discovery.installations.length === 1 ? discovery.installations[0].executablePath : ''
    const profiles = draft.profiles.map(profile => profile.kind === 'Valheim' && !profile.executablePath && serverPath
      ? { ...profile, executablePath: serverPath } : profile)
    if (profiles.some((profile, index) => profile !== draft.profiles[index])) {
      setDraft({ ...draft, profiles })
      dirtyRef.current = true
      setDirty(true)
    }
  }, [currentMode, showSetup, discovery, draft])

  useEffect(() => {
    if (currentMode !== 'Host' || !hasMinecraftDraft || minecraftDiscovery) return
    const controller = new AbortController()
    void getJson('/api/local/minecraft/discover', parseMinecraftDiscovery, controller.signal)
      .then(setMinecraftDiscovery)
      .catch(() => { /* Manual browsing and installation remain available. */ })
    return () => controller.abort()
  }, [currentMode, hasMinecraftDraft, minecraftDiscovery])

  useEffect(() => {
    if (currentMode !== 'Host' || !showSetup || !draft || !minecraftDiscovery) return
    const profiles = draft.profiles.map(profile => {
      if (profile.kind !== 'MinecraftJava' && profile.kind !== 'MinecraftBedrock') return profile
      if (profile.worldDirectory || profile.minecraft?.serverJarPath || profile.executablePath) return profile
      const found = minecraftDiscovery.installations.filter(item => item.kind === profile.kind)
      return found.length === 1 ? minecraftProfile(profile, found[0]) : profile
    })
    if (profiles.some((profile, index) => profile !== draft.profiles[index])) {
      setDraft({ ...draft, profiles })
      dirtyRef.current = true
      setDirty(true)
    }
  }, [currentMode, showSetup, draft, minecraftDiscovery])

  useEffect(() => {
    if (currentMode !== 'Host' || hostProfileCount !== 0 || !dirty || !draft) return
    writeSetupDraftTo(() => window.localStorage, setupDraftKey, draft.profiles)
  }, [currentMode, hostProfileCount, dirty, draft])

  const edit = (next: Settings) => {
    setDraft(next)
    dirtyRef.current = true
    setDirty(true)
  }

  const acceptSavedSettings = (settings: Settings) => {
    setDraft(settings)
    dirtyRef.current = false
    setDirty(false)
  }

  const setDraftIfClean = useCallback((settings: Settings) => {
    if (!dirtyRef.current) setDraft(settings)
  }, [])

  const syncControlPolicy = (settings: Settings) => {
    setDraft(current => current ? {
      ...current,
      companionListeningEnabled: settings.companionListeningEnabled,
      remoteControlsEnabled: settings.remoteControlsEnabled,
      autoShutdownEnabled: settings.autoShutdownEnabled
    } : current)
  }

  const syncDetectedPublicIp = useCallback((address: string, settings: Settings) => {
    setDraft(current => current ? { ...current, publicGameIp: address,
      publicGameIpCheckedUtc: settings.publicGameIpCheckedUtc } : current)
  }, [])

  const synchronizeHostSnapshot = useCallback((next: HostSnapshot) => {
    if (!initialDraftSet.current) {
      initialDraftSet.current = true
      if (!dirtyRef.current) {
        const restoredProfiles = next.settings.profiles.length === 0
          ? readSetupDraftFrom(() => window.localStorage, setupDraftKey) : null
        if (restoredProfiles?.length) {
          setDraft({ ...next.settings, profiles: restoredProfiles })
          setActiveProfileId(restoredProfiles[0].id)
          dirtyRef.current = true
          setDirty(true)
        } else setDraft(next.settings)
      }
    } else if (!dirtyRef.current) setDraft(next.settings)
  }, [])

  const resetForMode = (next: Snapshot) => {
    initialDraftSet.current = next.mode === 'Host'
    setDraft(next.mode === 'Host' ? next.settings : null)
    clearSetupSecrets()
    setShowSetup(false)
    dirtyRef.current = false
    setDirty(false)
  }

  const invalidateCustomScriptLoads = () => {
    for (const profileId of Object.keys(customScriptLoadRequestRef.current))
      customScriptLoadRequestRef.current[profileId] = (customScriptLoadRequestRef.current[profileId] ?? 0) + 1
  }

  const clearSetupSecrets = () => {
    invalidateCustomScriptLoads()
    setPasswords({})
    setShowPasswords({})
    setCustomScripts({})
    setCustomScriptsSaved({})
    setCustomScriptsLoading({})
    customScriptEditVersionRef.current = {}
  }

  const saveSetup = async (startAfterSave = false) => {
    if (!draft) return
    const profile = draft.profiles.find(item => item.id === activeProfileId) ?? draft.profiles[0]
    if (!profile) return
    const unmet = getSetupIssues(profile,
      snapshot?.mode === 'Host' && !!snapshot.passwordConfigured[profile.id], passwords[profile.id] ?? '',
      customScripts[profile.id], !!customScriptsSaved[profile.id])
      .map(message => ({ id: profile.id, message }))
    if (unmet.length) {
      setActiveProfileId(unmet[0].id)
      setSetupStep('review')
      setNotice({ good: false, text: unmet[0].message })
      return
    }
    const password = passwords[profile.id] ?? ''
    if (password && !isValidGamePassword(password)) {
      setNotice({ good: false, text: 'Use a server password of 5 to 64 characters without control characters.' })
      return
    }
    setPending('save')
    setNotice(null)
    try {
      let result: ActionResult | null = null
      if (dirty) {
        result = await changeAction('/api/local/settings', 'PUT', draft)
        applySnapshot(result.snapshot)
        if (!result.ok) { setNotice({ good: false, text: result.message }); return }
        acceptSavedSettings(result.snapshot.settings)
      }
      if (profile.kind === 'Valheim' && password) {
        result = await changeAction(`/api/local/profiles/${profile.id}/password`, 'POST', { password })
        applySnapshot(result.snapshot)
        if (!result.ok) { setNotice({ good: false, text: result.message }); return }
        setPasswords(current => ({ ...current, [profile.id]: '' }))
      }
      if (profile.kind === 'Custom' && !customScriptsSaved[profile.id]) {
        const scripts = customScripts[profile.id] ?? { start: '', status: '', stop: '' }
        result = await changeAction(`/api/local/profiles/${profile.id}/custom-scripts`, 'PUT', scripts)
        applySnapshot(result.snapshot)
        if (!result.ok) { setNotice({ good: false, text: result.message }); return }
        setCustomScriptsSaved(current => ({ ...current, [profile.id]: true }))
      }
      const passwordWasConfigured = snapshot?.mode === 'Host' && snapshot.passwordConfigured[profile.id]
      const needsPassword = profile.kind === 'Valheim' && !password && !passwordWasConfigured
      if (startAfterSave && !needsPassword) {
        if (dataRecoveryBlocked) {
          setNotice({ good: false, text: 'Resolve the local data recovery warning before starting a server.' })
          return
        }
        const started = await changeAction(`/api/local/profiles/${profile.id}/start`, 'POST')
        applySnapshot(started.snapshot)
        setNotice({ good: started.ok, text: started.message })
        if (!started.ok) return
      } else setNotice({ good: true, text: needsPassword ? 'Add a game password before starting.' : 'Server setup saved.' })
      if (!needsPassword) {
        removeSetupDraftFrom(() => window.localStorage, setupDraftKey)
        clearSetupSecrets()
        setShowSetup(false)
        window.scrollTo({ top: 0, behavior: 'smooth' })
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const updateProfile = (id: string, patch: Partial<Profile>) => {
    if (!draft) return
    edit({ ...draft, profiles: draft.profiles.map(profile => profile.id === id ? { ...profile, ...patch } : profile) })
  }

  const editCustomScripts = (id: string, patch: Partial<CustomScriptBundle>) => {
    customScriptEditVersionRef.current[id] = (customScriptEditVersionRef.current[id] ?? 0) + 1
    setCustomScripts(current => {
      const existing = current[id] ?? { start: '', status: '', stop: '' }
      return { ...current, [id]: { ...existing, ...patch } }
    })
    setCustomScriptsSaved(current => ({ ...current, [id]: false }))
  }

  const updateCustomPort = (profile: Profile, index: number, patch: Partial<CustomPort>) => {
    const custom = profile.custom ?? { gameName: 'Custom game', primaryProtocol: 'UDP' as const, shareJoinAddress: true, additionalPorts: [] }
    updateProfile(profile.id, { custom: { ...custom, additionalPorts: custom.additionalPorts.map((port, portIndex) =>
      portIndex === index ? { ...port, ...patch } : port) } })
  }

  const addCustomPort = (profile: Profile) => {
    const custom = profile.custom ?? { gameName: 'Custom game', primaryProtocol: 'UDP' as const, shareJoinAddress: true, additionalPorts: [] }
    updateProfile(profile.id, { custom: { ...custom, additionalPorts: [...custom.additionalPorts,
      { protocol: 'UDP', port: Math.min(65535, profile.gamePort + custom.additionalPorts.length + 1), label: 'Additional', family: 'Any' }] } })
  }

  const removeCustomPort = (profile: Profile, index: number) => {
    const custom = profile.custom
    if (!custom) return
    updateProfile(profile.id, { custom: { ...custom, additionalPorts: custom.additionalPorts.filter((_, portIndex) => portIndex !== index) } })
  }

  const loadCustomScripts = async (profileId: string) => {
    const request = (customScriptLoadRequestRef.current[profileId] ?? 0) + 1
    const editVersion = customScriptEditVersionRef.current[profileId] ?? 0
    customScriptLoadRequestRef.current[profileId] = request
    setCustomScriptsLoading(current => ({ ...current, [profileId]: true }))
    try {
      const result = await changeJson(`/api/local/profiles/${profileId}/custom-scripts/reveal`, 'POST', parseCustomScriptResult)
      if (!result.ok) { setNotice({ good: false, text: result.message }); return }
      if (customScriptLoadRequestRef.current[profileId] !== request ||
          (customScriptEditVersionRef.current[profileId] ?? 0) !== editVersion) return
      setCustomScripts(current => ({ ...current, [profileId]: result.scripts }))
      setCustomScriptsSaved(current => ({ ...current, [profileId]: result.code === 'CustomScriptsLoaded' }))
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally {
      if (customScriptLoadRequestRef.current[profileId] === request)
        setCustomScriptsLoading(current => ({ ...current, [profileId]: false }))
    }
  }

  const browseCustomDirectory = async (profile: Profile) => {
    setPending(profile.id)
    try {
      const result = await changeJson('/api/local/custom/browse-working-directory', 'POST', parseBrowseResult)
      if (result.ok && result.path) updateProfile(profile.id, { worldDirectory: result.path })
      if (result.code !== 'Canceled') setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const applyMinecraftInstallation = (profile: Profile, item: MinecraftInstallation, announce = true) => {
    setDraft(current => current ? { ...current, profiles: current.profiles.map(saved =>
      saved.id === profile.id ? minecraftProfile(saved, item) : saved) } : current)
    dirtyRef.current = true
    setDirty(true)
    if (announce) setNotice({ good: true, text: `${item.kind === 'MinecraftJava' ? 'Java' : 'Bedrock'} server selected. Continue to review.` })
  }

  const scanMinecraft = async (folder?: string) => {
    try {
      const path = '/api/local/minecraft/discover' + (folder ? `?folder=${encodeURIComponent(folder)}` : '')
      setMinecraftDiscovery(await getJson(path, parseMinecraftDiscovery))
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
  }

  const installMinecraft = async (profile: Profile) => {
    setPending('install-minecraft')
    setNotice(null)
    try {
      const result = await changeJson('/api/local/minecraft/install', 'POST', parseMinecraftInstallResult, {
        kind: profile.kind, worldName: profile.worldId.trim() || 'world', gamePort: profile.gamePort,
        acceptedTerms: !!minecraftTerms[profile.id]
      })
      setNotice({ good: result.ok, text: result.message })
      if (result.ok && result.installation) {
        applyMinecraftInstallation(profile, result.installation, false)
        setMinecraftTerms(current => ({ ...current, [profile.id]: false }))
        void scanMinecraft(result.installation.serverDirectory)
      }
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const changeGameKind = (profile: Profile, kind: Profile['kind']) => {
    if (profile.kind === kind || snapshot?.mode !== 'Host') return
    setPasswords(current => ({ ...current, [profile.id]: '' }))
    setShowPasswords(current => ({ ...current, [profile.id]: false }))
    setMinecraftTerms(current => ({ ...current, [profile.id]: false }))
    setMinecraftSetupMode(current => ({ ...current, [profile.id]: 'existing' }))
    customScriptLoadRequestRef.current[profile.id] = (customScriptLoadRequestRef.current[profile.id] ?? 0) + 1
    if (kind === 'Custom') {
      setCustomScripts(current => ({ ...current, [profile.id]: current[profile.id] ?? { start: '', status: '', stop: '' } }))
      setCustomScriptsSaved(current => ({ ...current, [profile.id]: false }))
    } else {
      setCustomScripts(current => { const next = { ...current }; delete next[profile.id]; return next })
      setCustomScriptsSaved(current => { const next = { ...current }; delete next[profile.id]; return next })
      setCustomScriptsLoading(current => { const next = { ...current }; delete next[profile.id]; return next })
    }
    updateProfile(profile.id, { kind, name: '', serverName: '', crossplay: false, publicListing: false,
      worldId: '', worldSource: kind === 'Valheim' ? 'New' : 'Existing',
      worldDirectory: kind === 'Valheim' ? `${snapshot.managedWorldsRoot}\\${profile.id.replaceAll('-', '')}` : '',
      gamePort: kind === 'Valheim' ? 2456 : kind === 'MinecraftJava' ? 25565 : kind === 'MinecraftBedrock' ? 19132 : 2456,
      executablePath: '', minecraft: kind === 'MinecraftJava' ? { serverJarPath: '' } : null,
      custom: kind === 'Custom' ? { gameName: '', primaryProtocol: 'UDP', shareJoinAddress: true, additionalPorts: [] } : null,
      crashRecovery: { enabled: false }, backups: { enabled: false, retentionCount: 5, minimumFreeSpaceMb: 1024 },
      maintenance: { enabled: false, message: '' } })
  }

  const addProfile = () => {
    if (!draft || snapshot?.mode !== 'Host') return
    setNotice(null)
    const id = crypto.randomUUID()
    edit({ ...draft, profiles: [...draft.profiles, { id, kind: 'Valheim', name: '', serverName: '', crossplay: false,
      publicListing: false, worldId: '', worldSource: 'New',
      worldDirectory: `${snapshot.managedWorldsRoot}\\${id.replaceAll('-', '')}`, gamePort: 2456, executablePath: '', custom: null,
      crashRecovery: { enabled: false }, backups: { enabled: false, retentionCount: 5, minimumFreeSpaceMb: 1024 },
      maintenance: { enabled: false, message: '' } }] })
    setActiveProfileId(id)
    setSetupStep('game')
    setMinecraftSetupMode(current => ({ ...current, [id]: 'existing' }))
    setShowSetup(true)
    if (!discovery) void scanValheim()
  }

  const removeProfile = async (profile: Profile) => {
    if (!draft || !window.confirm(`Remove ${profile.name || profile.serverName || 'this server'} from TogetherServer? Its world files are left in place.`)) return
    setPending('remove-profile')
    try {
      const profiles = draft.profiles.filter(item => item.id !== profile.id)
      const result = await changeAction('/api/local/settings', 'PUT', { ...draft, profiles,
        companionListeningEnabled: profiles.length > 0 && draft.companionListeningEnabled,
        remoteControlsEnabled: profiles.length > 0 && draft.remoteControlsEnabled })
      const reconciled = reconcileProfileRemoval(draft, result)
      applySnapshot(result.snapshot)
      if (!reconciled.committed) {
        setNotice({ good: false, text: result.message })
        return
      }
      acceptSavedSettings(reconciled.settings)
      clearSetupSecrets()
      setShowSetup(false)
      setNotice({ good: true, text: 'Server removed from TogetherServer. Its world files were left in place.' })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const cancelSetup = () => {
    if (snapshot?.mode !== 'Host' || pending) return
    if ((dirty || hasSensitiveSetupDraft(passwords, customScripts, customScriptsSaved)) &&
        !window.confirm('Discard these setup changes, including any entered password or unsaved custom scripts?')) return
    acceptSavedSettings(snapshot.settings)
    setActiveProfileId(snapshot.settings.profiles[0]?.id ?? '')
    clearSetupSecrets()
    setMinecraftTerms({})
    setSourceRoots({})
    removeSetupDraftFrom(() => window.localStorage, setupDraftKey)
    setShowSetup(false)
    setNotice(null)
  }

  const finishSetupLater = () => {
    if (pending) return
    if (hasSensitiveSetupDraft(passwords, customScripts, customScriptsSaved) &&
        !window.confirm('Finish later clears the entered game password and unsaved custom scripts from this window. Continue?')) return
    clearSetupSecrets()
    setShowSetup(false)
    setNotice({ good: true, text: 'Setup is paused. Non-secret setup fields stay on this PC; re-enter passwords and custom scripts when you continue.' })
  }

  const openSetup = (id: string) => {
    setNotice(null)
    setActiveProfileId(id)
    setSetupStep('review')
    setShowSetup(true)
    if (snapshot?.mode === 'Host' && snapshot.settings.profiles.some(profile => profile.id === id && profile.kind === 'Custom'))
      void loadCustomScripts(id)
  }

  const continueSetup = () => {
    if (!draft?.profiles[0]) return
    setActiveProfileId(draft.profiles[0].id)
    setSetupStep('world')
    setShowSetup(true)
  }

  const scanValheim = async () => {
    setPending('scan')
    try {
      setDiscovery(await getJson('/api/local/valheim/discover', parseDiscovery))
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const importWorld = async (profile: Profile, sourceSaveRoot: string, worldId: string, sourceFolder = 'worlds_local') => {
    if (sourceFolder === 'worlds' && !window.confirm('Close Valheim and wait for Steam Cloud to finish syncing before copying this cached world folder. Continue?')) return
    setPending(profile.id)
    try {
      const result = await changeJson('/api/local/valheim/import', 'POST', parseImportResult, {
        profileId: profile.id, sourceSaveRoot, worldId, sourceFolder
      })
      setNotice({ good: result.ok, text: `${result.code}: ${result.message}${result.ok ? ' Save the profile next.' : ''}` })
      if (result.ok && result.worldDirectory) updateProfile(profile.id, {
        name: profile.name || worldId, serverName: profile.serverName || worldId,
        worldId, worldSource: 'Existing', worldDirectory: result.worldDirectory
      })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const browseServer = async (profile: Profile) => {
    setPending(profile.id)
    try {
      const result = await changeJson('/api/local/valheim/browse-server', 'POST', parseServerBrowseResult)
      if (result.ok && result.executablePath) updateProfile(profile.id, { executablePath: result.executablePath })
      if (result.code !== 'Canceled') setNotice({ good: result.ok, text: `${result.code}: ${result.message}` })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const browseMinecraft = async (profile: Profile, target: 'folder' | 'executable' | 'jar') => {
    setPending(profile.id)
    try {
      const result = await changeJson('/api/local/minecraft/browse', 'POST', parseMinecraftBrowseResult, { kind: profile.kind, target })
      if (result.ok && result.path) {
        if (target === 'folder') { updateProfile(profile.id, { worldDirectory: result.path }); void scanMinecraft(result.path) }
        if (target === 'executable') updateProfile(profile.id, { executablePath: result.path,
          worldDirectory: profile.kind === 'MinecraftBedrock' && !profile.worldDirectory
            ? result.path.slice(0, result.path.lastIndexOf('\\')) : profile.worldDirectory })
        if (target === 'jar') updateProfile(profile.id, { minecraft: { serverJarPath: result.path },
          worldDirectory: profile.worldDirectory || result.path.slice(0, result.path.lastIndexOf('\\')) })
        if (target === 'jar') void scanMinecraft(result.path.slice(0, result.path.lastIndexOf('\\')))
      }
      if (result.code !== 'Canceled') setNotice({ good: result.ok, text: result.message })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const browseWorld = async (profile: Profile, folder = false) => {
    setPending(profile.id)
    try {
      const result = await changeJson(folder ? '/api/local/valheim/browse-world-folder' : '/api/local/valheim/browse-world',
        'POST', parseWorldBrowseResult)
      if (result.ok && result.sourceSaveRoot && result.worldId) {
        if (result.sourceFolder === 'worlds_local')
          setSourceRoots(current => ({ ...current, [profile.id]: result.sourceSaveRoot! }))
        await importWorld(profile, result.sourceSaveRoot, result.worldId, result.sourceFolder)
      } else if (result.code !== 'Canceled') setNotice({ good: false, text: `${result.code}: ${result.message}` })
    } catch (error) { setNotice({ good: false, text: errorMessage(error) }) }
    finally { setPending('') }
  }

  const editedProfile = draft?.profiles.find(profile => profile.id === activeProfileId) ?? draft?.profiles[0]
  const setupIssues = editedProfile ? getSetupIssues(editedProfile,
    snapshot?.mode === 'Host' && !!snapshot.passwordConfigured[editedProfile.id], passwords[editedProfile.id] ?? '',
    customScripts[editedProfile.id], !!customScriptsSaved[editedProfile.id]) : []
  const stepIssues = editedProfile ? getStepIssues(setupStep, editedProfile,
    snapshot?.mode === 'Host' && !!snapshot.passwordConfigured[editedProfile.id], passwords[editedProfile.id] ?? '',
    customScripts[editedProfile.id], !!customScriptsSaved[editedProfile.id]) : []
  const customScriptsChanged = !!editedProfile && editedProfile.kind === 'Custom' && !customScriptsSaved[editedProfile.id] &&
    !!customScripts[editedProfile.id] && Object.values(customScripts[editedProfile.id]).some(value => value.trim().length > 0)

  return {
    draft,
    dirty,
    passwords,
    customScripts,
    customScriptsSaved,
    customScriptsLoading,
    discovery,
    minecraftDiscovery,
    minecraftTerms,
    sourceRoots,
    showSetup,
    setupStep,
    minecraftSetupMode,
    showPasswords,
    editedProfile,
    setupIssues,
    stepIssues,
    customScriptsChanged,
    setupRef,
    sensitiveDraft: hasSensitiveSetupDraft(passwords, customScripts, customScriptsSaved),
    edit,
    acceptSavedSettings,
    setDraftIfClean,
    syncControlPolicy,
    syncDetectedPublicIp,
    synchronizeHostSnapshot,
    resetForMode,
    saveSetup,
    updateProfile,
    editCustomScripts,
    updateCustomPort,
    addCustomPort,
    removeCustomPort,
    browseCustomDirectory,
    applyMinecraftInstallation,
    scanMinecraft,
    installMinecraft,
    changeGameKind,
    addProfile,
    removeProfile,
    cancelSetup,
    finishSetupLater,
    openSetup,
    continueSetup,
    scanValheim,
    importWorld,
    browseServer,
    browseMinecraft,
    browseWorld,
    setSetupStep,
    setSourceRoot: (profileId: string, value: string) => setSourceRoots(current => ({ ...current, [profileId]: value })),
    setPassword: (profileId: string, value: string) => setPasswords(current => ({ ...current, [profileId]: value })),
    setShowPassword: (profileId: string, value: boolean) => setShowPasswords(current => ({ ...current, [profileId]: value })),
    setMinecraftSetupModeFor: (profileId: string, mode: 'existing' | 'install') =>
      setMinecraftSetupMode(current => ({ ...current, [profileId]: mode })),
    setMinecraftTermsFor: (profileId: string, accepted: boolean) =>
      setMinecraftTerms(current => ({ ...current, [profileId]: accepted }))
  }
}
