import { defineConfig } from 'vitest/config'
import { syntheticDemoContractPlugin } from './demoFixturePlugin.ts'

export default defineConfig({
  plugins: [syntheticDemoContractPlugin()],
  test: {
    environment: 'jsdom',
    css: { include: [/\?inline(?:&|$)/] },
    setupFiles: ['./src/test/setup.ts'],
    clearMocks: true,
    restoreMocks: true,
    unstubGlobals: true
  }
})
