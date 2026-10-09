import { describe, expect, it } from 'vitest'
import { parseSetupImportPreview, setupSourceFiles } from './setupImportPreview'

const preview = { ok: true, code: 'ImportPreviewReady', message: 'Review the source before copying.', preview: {
  selectionId: '11111111-1111-4111-8111-111111111111', profileId: '22222222-2222-4222-8222-222222222222',
  kind: 'Factorio', worldId: 'new-factory', sourceFiles: [{ name: 'new-factory.zip', bytes: 1200, modifiedUtc: '2026-10-08T22:00:00Z' }],
  totalBytes: 1200, modifiedUtc: '2026-10-08T22:00:00Z', expiresUtc: '2026-10-08T22:10:00Z' } }
describe('source preview contract', () => {
  it('retains known source facts and represents unavailable metadata explicitly', () => {
    expect(parseSetupImportPreview(preview).preview?.sourceFiles[0]).toEqual(preview.preview.sourceFiles[0])
    expect(parseSetupImportPreview({ ...preview, preview: { ...preview.preview, expiresUtc: '2026-10-08T22:10:00+00:00' } }).preview?.expiresUtc).toBe('2026-10-08T22:10:00+00:00')
    expect(parseSetupImportPreview({ ...preview, preview: { ...preview.preview, totalBytes: null, modifiedUtc: null,
      sourceFiles: [{ name: 'new-factory.zip', bytes: null, modifiedUtc: null }] } }).preview?.totalBytes).toBeNull()
    expect(parseSetupImportPreview({ ok: false, code: 'Canceled', message: 'No file selected.' }).preview).toBeNull()
  })
  it('refuses arbitrary paths, invalid identities, unknown kinds and malformed dates', () => {
    expect(() => setupSourceFiles([{ name: 'C:\\private\\save.zip', bytes: 2, modifiedUtc: null }])).toThrow()
    expect(() => parseSetupImportPreview({ ...preview, preview: { ...preview.preview, sourcePath: 'C:\\private' } })).toThrow()
    expect(() => parseSetupImportPreview({ ...preview, preview: { ...preview.preview, selectionId: 'not-a-token' } })).toThrow()
    expect(() => parseSetupImportPreview({ ...preview, preview: { ...preview.preview, kind: 'Custom' } })).toThrow()
    expect(() => parseSetupImportPreview({ ...preview, preview: { ...preview.preview, expiresUtc: '2026-10-08 22:00' } })).toThrow()
    expect(() => parseSetupImportPreview({ ...preview, preview: { ...preview.preview, totalBytes: -1 } })).toThrow()
    expect(() => parseSetupImportPreview({ ...preview, ok: false })).toThrow()
  })
})
