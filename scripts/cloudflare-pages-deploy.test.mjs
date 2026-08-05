import assert from 'node:assert/strict'
import { chmodSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'

const deployScript = new URL('./cloudflare-pages-deploy.mjs', import.meta.url)

const runDeploy = (args, env) => {
  const binDirectory = mkdtempSync(join(tmpdir(), 'terrascale-wrangler-'))
  const wrangler = join(binDirectory, 'wrangler')
  writeFileSync(wrangler, '#!/bin/sh\nprintf "%s\\n" "$@"\n')
  chmodSync(wrangler, 0o755)

  try {
    return spawnSync(process.execPath, [deployScript.pathname, ...args], {
      encoding: 'utf8',
      env: { ...process.env, ...env, PATH: `${binDirectory}:${process.env.PATH}` },
    })
  } finally {
    rmSync(binDirectory, { force: true, recursive: true })
  }
}

test('production deploy uses the configured production branch', () => {
  const result = runDeploy(['--production'], { CLOUDFLARE_PAGES_PRODUCTION_BRANCH: 'release' })

  assert.equal(result.status, 0)
  assert.match(result.stdout, /^pages\ndeploy\n--branch\nrelease\n/m)
})

test('preview deploy rejects the configured production branch', () => {
  const result = runDeploy(['--preview'], {
    CLOUDFLARE_PAGES_BRANCH: 'release',
    CLOUDFLARE_PAGES_PRODUCTION_BRANCH: 'release',
  })

  assert.equal(result.status, 1)
  assert.match(result.stderr, /Preview deploys cannot target the production branch "release"/)
  assert.equal(result.stdout, '')
})
