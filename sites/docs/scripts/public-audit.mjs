import { readdirSync, readFileSync } from 'node:fs'
import { join, relative } from 'node:path'

// Findings report locations and categories only, never the matched value.
const rules = [
  ['credential', /-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\b(?:ghp_|gho_|github_pat_|sk_live_|sk_test_|AKIA)[A-Za-z0-9_]{16,}|\btb_[A-Za-z0-9_-]{43}\b|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b/g],
  ['private topology', /\b(?:NetBird|OCI\s+Vault)\b|private[ -]network|\.internal\b|\.ts\.net\b|\/home\/[a-z0-9_-]+|\b(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b/gi],
  ['personal address', /[A-Z0-9._%+-]+@(?:gmail|hotmail|outlook|icloud)\.[A-Z]{2,}/gi],
  ['internal planning reference', /\b(?:TS|D|O|P)-\d+\b|\[PLACEHOLDER\b/g],
  ['obsolete source link', /https:\/\/(?:git\.terrascale\.tech|github\.com\/TerraScale\/[^\s"<)]+\/src\/branch\/)/g],
]

export function findings(text, file) {
  const out = []
  for (const [category, pattern] of rules) {
    pattern.lastIndex = 0
    for (const match of text.matchAll(pattern)) {
      const line = text.slice(0, match.index).split('\n').length
      out.push({ file, line, category })
    }
  }
  return out
}

export function filesIn(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name)
    if (entry.isSymbolicLink()) throw new Error(`Public tree contains a symlink: ${path}`)
    return entry.isDirectory() ? filesIn(path) : [path]
  })
}

export function auditTree(dir) {
  const out = []
  for (const file of filesIn(dir)) {
    const rel = relative(dir, file)
    if (/(?:^|\/)(?:node_modules|\.env[^/]*|\.git)(?:\/|$)|\.map$/.test(rel))
      out.push({ file: rel, line: 1, category: 'unexpected bundled file' })
    // Committed, licensed font assets are the only binary public files.
    if (/^fonts\/(?:[a-z-]+|Geist(?:Mono)?-Variable)\.woff2$/.test(rel)) continue
    const bytes = readFileSync(file)
    if (bytes.includes(0)) {
      out.push({ file: rel, line: 1, category: 'unexpected binary asset' })
      continue
    }
    out.push(...findings(bytes.toString('utf8'), rel))
  }
  return out
}
