import { accessEntryIssue, accessListIssue, maximumAccessEntries, type GameAccessEntry } from './gameSettings'

export type EditorDiffLine = { type: 'removed' | 'added' | 'unchanged'; before: number | null; after: number | null; text: string }
export function editorFileDiff(before: string, after: string, maximum = 240): {
  lines: EditorDiffLine[]; omitted: number; unchangedStart: number; unchangedEnd: number
} {
  const left = before.split('\n'), right = after.split('\n')
  let start = 0
  while (start < left.length && start < right.length && left[start] === right[start]) start++
  let end = 0
  while (end < left.length - start && end < right.length - start && left[left.length - end - 1] === right[right.length - end - 1]) end++
  const a = left.slice(start, left.length - end), b = right.slice(start, right.length - end)
  const lines: EditorDiffLine[] = []
  // Bound work for large raw files. Large edits still show exact removed/added lines in order.
  if (a.length * b.length <= 160_000) {
    const lengths = Array.from({ length: a.length + 1 }, () => new Uint16Array(b.length + 1))
    for (let i = a.length - 1; i >= 0; i--) for (let j = b.length - 1; j >= 0; j--)
      lengths[i][j] = a[i] === b[j] ? lengths[i + 1][j + 1] + 1 : Math.max(lengths[i + 1][j], lengths[i][j + 1])
    let i = 0, j = 0
    while (i < a.length || j < b.length) {
      if (i < a.length && j < b.length && a[i] === b[j]) { lines.push({ type: 'unchanged', before: start + i + 1, after: start + j + 1, text: a[i] }); i++; j++ }
      else if (i < a.length && (j === b.length || lengths[i + 1][j] >= lengths[i][j + 1])) { lines.push({ type: 'removed', before: start + i + 1, after: null, text: a[i] }); i++ }
      else { lines.push({ type: 'added', before: null, after: start + j + 1, text: b[j] }); j++ }
    }
  } else {
    a.forEach((text, i) => lines.push({ type: 'removed', before: start + i + 1, after: null, text }))
    b.forEach((text, i) => lines.push({ type: 'added', before: null, after: start + i + 1, text }))
  }
  return { lines: lines.slice(0, maximum), omitted: Math.max(0, lines.length - maximum), unchangedStart: start, unchangedEnd: end }
}

export function editorBulkPlayers(kind: string, text: string, existing: GameAccessEntry[]): { entries: GameAccessEntry[]; issue: string | null } {
  if (text.length > 24_000) return { entries: [], issue: 'Paste at most 24,000 characters.' }
  const rows = text.split(/\r?\n/u).map(row => row.trim()).filter(Boolean)
  if (rows.length === 0) return { entries: [], issue: 'Paste one player per line.' }
  if (existing.length + rows.length > maximumAccessEntries) return { entries: [], issue: 'Use at most 128 players in this editor.' }
  const entries: GameAccessEntry[] = []
  for (let index = 0; index < rows.length; index++) {
    const fields = rows[index].split('\t').map(value => value.trim())
    let item: GameAccessEntry
    if (kind === 'Valheim' && fields.length === 1) item = { identity: fields[0], name: null, ignoresPlayerLimit: null }
    else if (kind === 'MinecraftJava' && fields.length === 2) item = { name: fields[0], identity: fields[1], ignoresPlayerLimit: null }
    else if (kind === 'MinecraftBedrock' && fields.length <= 2) item = { name: fields[0], identity: fields[1] || null, ignoresPlayerLimit: false }
    else return { entries: [], issue: `Line ${index + 1}: use ${kind === 'Valheim' ? 'one Platform_UserID' : kind === 'MinecraftJava' ? 'a username and UUID separated by a tab' : 'a gamertag and optional XUID separated by a tab'}.` }
    const problem = accessEntryIssue(kind, item)
    if (problem) return { entries: [], issue: `Line ${index + 1}: ${problem}` }
    entries.push(item)
  }
  return { entries, issue: accessListIssue(kind, [...existing, ...entries]) }
}
