import { defineConfig } from 'vite'
import { syntheticDemoContractPlugin } from './demoFixturePlugin.ts'

export default defineConfig({
  base: '/',
  plugins: [syntheticDemoContractPlugin()],
  build: { outDir: 'dist', emptyOutDir: true }
})
