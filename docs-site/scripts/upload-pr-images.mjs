import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { basename, join } from 'node:path'

const [pr, ...files] = process.argv.slice(2)
if (!/^[1-9]\d*$/.test(pr ?? '') || files.length === 0) {
  throw new Error('Usage: node scripts/upload-pr-images.mjs <PR-number> <file.png> [more.png ...]')
}
const git = (args, options = {}) => execFileSync('git', args, { encoding: 'utf8', ...options }).trim()
const repo = 'TerraScale/ts-docs'
const remote = `https://github.com/${repo}.git`
execFileSync('gh', ['pr', 'view', pr, '--repo', repo, '--json', 'number'], { stdio: 'ignore' })

const images = files.map((file) => {
  const name = basename(file)
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]*\.png$/.test(name)) {
    throw new Error(`Use a simple PNG filename: ${name}`)
  }
  const bytes = readFileSync(file)
  if (!bytes.subarray(0, 8).equals(Buffer.from('89504e470d0a1a0a', 'hex'))) {
    throw new Error(`Not a PNG: ${name}`)
  }
  return { name, bytes, path: `${pr}/${name}` }
})
if (new Set(images.map(({ name }) => name)).size !== images.length) {
  throw new Error('Each image needs a distinct filename')
}

// A separate index leaves the checkout and its staged files untouched.
const temporary = mkdtempSync(join(tmpdir(), 'docs-pr-images-'))
try {
  const env = { ...process.env, GIT_INDEX_FILE: join(temporary, 'index') }
  const assetGit = (args, options = {}) => git(args, { env, ...options })
  const exists = git(['ls-remote', '--heads', remote, 'refs/heads/pr-assets'])
  let parent
  if (exists) {
    git(['fetch', '--no-tags', remote, 'refs/heads/pr-assets'])
    parent = git(['rev-parse', 'FETCH_HEAD'])
    assetGit(['read-tree', parent])
  } else {
    assetGit(['read-tree', '--empty'])
  }
  for (const image of images) {
    if (assetGit(['ls-files', '--', image.path])) {
      throw new Error(`Already uploaded: ${image.path}; use a new filename`)
    }
    const blob = git(['hash-object', '-w', '--stdin'], { input: image.bytes })
    assetGit(['update-index', '--add', '--cacheinfo', `100644,${blob},${image.path}`])
  }
  const tree = assetGit(['write-tree'])
  // The first commit has no parent: site history never enters the asset branch.
  const commit = git(['commit-tree', tree, ...(parent ? ['-p', parent] : [])], {
    input: `Add preview images for PR #${pr}\n`,
  })
  // A normal push rejects concurrent updates instead of overwriting them.
  git(['push', remote, `${commit}:refs/heads/pr-assets`])
  for (const { name, path } of images) {
    console.log(`![${name.replace(/\.png$/, '').replace(/[-_]/g, ' ')}](https://raw.githubusercontent.com/${repo}/pr-assets/${path})`)
  }
} finally {
  rmSync(temporary, { recursive: true, force: true })
}
