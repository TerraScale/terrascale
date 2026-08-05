import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { chmodSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:http'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'

const deployScript = new URL('./cloudflare-pages-deploy.mjs', import.meta.url)
const projectCreateScript = new URL('./cloudflare-pages-project-create.mjs', import.meta.url)

const startApi = async (t, { result, status = 200 }) => {
  let calls = 0
  const server = createServer((_request, response) => {
    calls += 1
    response.writeHead(status, { 'Content-Type': 'application/json' })
    response.end(JSON.stringify({ result }))
  })
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve))
  t.after(() => server.close())
  const address = server.address()
  return { apiBaseUrl: `http://127.0.0.1:${address.port}`, calls: () => calls }
}

const runScript = (script, args, env) => {
  const binDirectory = mkdtempSync(join(tmpdir(), 'terrascale-wrangler-'))
  const wrangler = join(binDirectory, 'wrangler')
  writeFileSync(wrangler, '#!/bin/sh\nprintf "%s\\n" "$@"\n')
  chmodSync(wrangler, 0o755)

  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [script.pathname, ...args], {
      env: { ...process.env, ...env, PATH: `${binDirectory}:${process.env.PATH}` },
    })
    let stderr = ''
    let stdout = ''
    child.stderr.on('data', (chunk) => (stderr += chunk))
    child.stdout.on('data', (chunk) => (stdout += chunk))
    child.on('error', reject)
    child.on('close', (status) => {
      rmSync(binDirectory, { force: true, recursive: true })
      resolve({ status, stderr, stdout })
    })
  })
}

const verifiedEnv = (apiBaseUrl, productionBranch) => ({
  CLOUDFLARE_ACCOUNT_ID: 'account',
  CLOUDFLARE_API_TOKEN: 'token',
  CLOUDFLARE_PAGES_API_BASE_URL: apiBaseUrl,
  CLOUDFLARE_PAGES_PRODUCTION_BRANCH: productionBranch,
})

test('production deploy uses a verified configured non-main branch', async (t) => {
  const api = await startApi(t, { result: { production_branch: 'release' } })
  const result = await runScript(deployScript, ['--production'], verifiedEnv(api.apiBaseUrl, 'release'))

  assert.equal(result.status, 0)
  assert.match(result.stdout, /^pages\ndeploy\n--branch\nrelease\n/m)
  assert.equal(api.calls(), 1)
})

test('preview deploy fails closed on remote production-branch drift without spawning Wrangler', async (t) => {
  const api = await startApi(t, { result: { production_branch: 'main' } })
  const result = await runScript(deployScript, ['--preview'], {
    ...verifiedEnv(api.apiBaseUrl, 'release'),
    CLOUDFLARE_PAGES_BRANCH: 'main',
  })

  assert.equal(result.status, 1)
  assert.match(result.stderr, /production branch mismatch: remote "main", configured "release"/)
  assert.equal(result.stdout, '')
  assert.equal(api.calls(), 1)
})

test('existing project creation fails closed on a production-branch mismatch', async (t) => {
  const api = await startApi(t, { result: { production_branch: 'main' } })
  const result = await runScript(projectCreateScript, [], verifiedEnv(api.apiBaseUrl, 'release'))

  assert.equal(result.status, 1)
  assert.match(result.stderr, /production branch mismatch: remote "main", configured "release"/)
  assert.equal(result.stdout, '')
  assert.equal(api.calls(), 1)
})
