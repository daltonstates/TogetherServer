import { getLocalJson } from './api'
import { parseAppInstance, type AppInstanceView } from './contracts'

type DemoModule = Pick<typeof import('./developmentDemo'), 'installDevelopmentDemo' | 'useDevelopmentDemoRevision' | 'DevelopmentDemoControls'>

// Verify the real loopback instance before loading or installing the fake transport.
// A query string on an ordinary production or staging app cannot enable demo mode.
export async function prepareDevelopmentDemo(search: string,
  readInstance: () => Promise<AppInstanceView> = () => getLocalJson('/api/local/instance', parseAppInstance),
  loadDemo: () => Promise<DemoModule> = () => import('./developmentDemo')): Promise<DemoModule | null> {
  if (new URLSearchParams(search).get('demo') !== 'sharing') return null
  const instance = await readInstance()
  if (!instance.isStaging || instance.isDemo !== true) return null
  const demo = await loadDemo()
  demo.installDevelopmentDemo(instance)
  return demo
}
