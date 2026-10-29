import assert from 'node:assert/strict'
import { basename } from 'node:path'
import test from 'node:test'
import { filesIn } from './public-audit.mjs'

test('generated bundle hashes cannot resemble internal issue IDs', () => {
  const bundles = filesIn('docs/.vitepress/dist/assets').filter((file) => /\.(?:js|css)$/.test(file))
  assert.ok(bundles.length > 0, 'Build the docs before checking bundle hashes')
  for (const file of bundles) {
    assert.match(basename(file), /\.[a-f0-9]{8,}(?:\.lean)?\.(?:js|css)$/, file)
  }
})
