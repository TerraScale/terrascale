import assert from 'node:assert/strict'
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { auditTree } from './public-audit.mjs'

test('licensed font filenames pass while unexpected binary assets fail', (t) => {
  const dir = mkdtempSync(join(tmpdir(), 'ts-docs-fonts-'))
  t.after(() => rmSync(dir, { recursive: true, force: true }))
  mkdirSync(join(dir, 'fonts'))
  for (const name of ['geist-variable.woff2', 'geist-mono-variable.woff2', 'oxanium-latin-wght-normal.woff2', 'Geist-Variable.woff2', 'GeistMono-Variable.woff2']) {
    writeFileSync(join(dir, 'fonts', name), Buffer.from([0, 1, 2]))
  }
  assert.deepEqual(auditTree(dir), [])

  writeFileSync(join(dir, 'fonts', 'Unexpected.woff2'), Buffer.from([0, 1, 2]))
  writeFileSync(join(dir, 'payload.bin'), Buffer.from([0, 1, 2]))
  assert.deepEqual(auditTree(dir), [
    { file: 'fonts/Unexpected.woff2', line: 1, category: 'unexpected binary asset' },
    { file: 'payload.bin', line: 1, category: 'unexpected binary asset' },
  ])
})
