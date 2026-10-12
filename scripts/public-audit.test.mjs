import assert from 'node:assert/strict'
import test from 'node:test'
import { findings } from './public-audit.mjs'

test('privacy findings redact credentials, personal data and private addresses', () => {
  const secret = ['ghp_', 's'.repeat(36)].join('')
  const input = `first line\n${secret}\n${['person', 'gmail.com'].join('@')}\n${['10', '8', '9', '2'].join('.')}`
  const result = findings(input, 'example.txt')
  assert.deepEqual(result.map(({ category, line }) => [category, line]), [
    ['credential', 2], ['private topology', 4], ['personal address', 3],
  ])
  assert.equal(JSON.stringify(result).includes(secret), false)
})

test('public API origins, bearer placeholders and synthetic examples stay allowed', () => {
  assert.deepEqual(findings('https://api.terrascale.tech https://gru.api.terrascale.tech YOUR_ACCESS_TOKEN https://api.example.invalid', 'example.md'), [])
})

test('old Forgejo-shaped GitHub URLs and internal issue labels fail the public gate', () => {
  assert.deepEqual(findings('https://github.com/TerraScale/ts-docs/src/branch/main/example', 'links.md').map((x) => x.category), ['obsolete source link'])
  const result = findings(['TS', '1234'].join('-'), 'page.md')
  assert.equal(result[0].category, 'internal planning reference')
})
