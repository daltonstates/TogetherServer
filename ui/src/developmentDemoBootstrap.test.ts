import { describe, expect, it, vi } from 'vitest'
import type { AppInstanceView } from './contracts'
import { prepareDevelopmentDemo } from './developmentDemoBootstrap'

const instance: AppInstanceView = {
  kind: 'Staging', displayName: 'TogetherServer DEVELOPMENT DEMO', isStaging: true, isDemo: true,
  freshWorldsOnly: true, startupAvailable: false, updatesAvailable: false,
  localPort: 5129, companionPort: 5133, valheimPort: 2460, minecraftJavaPort: 25567,
  minecraftBedrockPort: 19136, dataRoot: 'C:\\Synthetic\\TogetherServer-Demo', dataIsolation: 'Demo only.'
}
const makeModule = () => ({
  installDevelopmentDemo: vi.fn(() => ({ reset: vi.fn(), uninstall: vi.fn() })),
  useDevelopmentDemoRevision: () => 0,
  DevelopmentDemoControls: () => null
})

describe('native development demo bootstrap', () => {
  it('leaves ordinary application startup alone', async () => {
    const read = vi.fn(async () => instance)
    const load = vi.fn(async () => makeModule())
    expect(await prepareDevelopmentDemo('', read, load)).toBeNull()
    expect(read).not.toHaveBeenCalled()
    expect(load).not.toHaveBeenCalled()
  })

  it('does not load the fake transport from a query string on production or regular staging', async () => {
    const load = vi.fn(async () => makeModule())
    for (const untrusted of [
      { ...instance, kind: 'Production' as const, isStaging: false, isDemo: false },
      { ...instance, isStaging: false, isDemo: true },
      { ...instance, isDemo: false },
      { ...instance, isDemo: undefined }
    ]) {
      expect(await prepareDevelopmentDemo('?demo=sharing', async () => untrusted, load)).toBeNull()
    }
    expect(load).not.toHaveBeenCalled()
  })

  it('waits for trusted metadata before loading and installing demo data', async () => {
    const demo = makeModule()
    const order: string[] = []
    const prepared = await prepareDevelopmentDemo('?demo=sharing', async () => {
      order.push('real-instance')
      return instance
    }, async () => {
      order.push('demo-module')
      return demo
    })
    expect(prepared).toBe(demo)
    expect(order).toEqual(['real-instance', 'demo-module'])
    expect(demo.installDevelopmentDemo).toHaveBeenCalledExactlyOnceWith(instance)
  })

  it('fails closed when the real instance cannot be verified', async () => {
    const load = vi.fn(async () => makeModule())
    await expect(prepareDevelopmentDemo('?demo=sharing', async () => {
      throw new Error('Unavailable')
    }, load)).rejects.toThrow('Unavailable')
    expect(load).not.toHaveBeenCalled()
  })
})
