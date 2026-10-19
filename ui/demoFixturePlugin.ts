import type { Plugin } from 'vite'
import hostSnapshot from '../contracts/host-snapshot.v1.json' with { type: 'json' }

// Keep the checked-in synthetic contract available in both the bundled demo and
// JSDOM. No browser filesystem shim or real app data is involved.
export function syntheticDemoContractPlugin(): Plugin {
  return {
    name: 'inline-synthetic-demo-contract',
    enforce: 'pre',
    transform(source, id) {
      if (!id.replaceAll('\\', '/').endsWith('/scripts/ui-redesign-fixtures.mjs')) return
      const importLine = "import { readFileSync } from 'node:fs';"
      const contractLine = "const contract = JSON.parse(readFileSync(new URL('../contracts/host-snapshot.v1.json', import.meta.url), 'utf8'));"
      if (!source.includes(importLine) || !source.includes(contractLine))
        throw new Error('Synthetic demo contract changed; review its browser-only transform.')
      return { code: source.replace(importLine, '').replace(contractLine, `const contract = ${JSON.stringify(hostSnapshot)};`), map: null }
    }
  }
}
