import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import test from 'node:test'

const terms = join(dirname(fileURLToPath(import.meta.url)), '../docs/legal/terms.md')

test('english terms do not quote dollar amounts', () => {
  assert.equal(readFileSync(terms, 'utf8').includes('$'), false)
})
