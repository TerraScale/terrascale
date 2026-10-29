import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import { dirname, join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { auditTree, filesIn, findings } from './public-audit.mjs'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const dist = join(root, 'docs/.vitepress/dist')
assert.ok(existsSync(dist), 'Build the docs before checking public output')
const problems = auditTree(dist)
const sources = filesIn(join(root, 'docs')).filter((file) =>
  !file.includes('/.vitepress/') && !file.includes('/public/') && /\.(?:md|ts)$/.test(file))
for (const source of sources) problems.push(...findings(readFileSync(source, 'utf8'), relative(root, source)))
if (problems.length) {
  for (const { file, line, category } of problems.slice(0, 30))
    console.error(`${file}:${line}: ${category}`)
  throw new Error(`Public privacy audit failed: ${problems.length} finding(s); values withheld`)
}

const htmlFiles = filesIn(dist).filter((file) => file.endsWith('.html'))
for (const source of sources.filter((file) => file.endsWith('.md'))) {
  const page = join(dist, relative(join(root, 'docs'), source).replace(/\.md$/, '.html'))
  assert.ok(existsSync(page), `Missing generated page: ${relative(root, source)}`)
}
assert.ok(htmlFiles.length > 0, 'The output contains no rendered pages')
console.log(`Public audit passed: ${sources.length} source files, ${filesIn(dist).length} bundled files, ${htmlFiles.length} pages.`)
