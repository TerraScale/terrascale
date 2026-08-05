import assert from 'node:assert/strict'
import test from 'node:test'
import { runDeploy } from './cloudflare-pages-deploy.mjs'
import { verifyExistingProject } from './cloudflare-pages-project-create.mjs'

const environment = (overrides = {}) => ({
  CLOUDFLARE_ACCOUNT_ID: 'account',
  CLOUDFLARE_API_TOKEN: 'token',
  CLOUDFLARE_PAGES_PRODUCTION_BRANCH: 'release',
  ...overrides,
})

const response = (body, status = 200) => new Response(body, { status })
const project = (overrides = {}) => ({ name: 'terrascale-landing-page', production_branch: 'release', ...overrides })
const success = (result = project()) => response(JSON.stringify({ success: true, result }))

const run = (argv, env, fetchImpl, spawnCalls = []) =>
  runDeploy({
    argv,
    environment: env,
    fetchImpl,
    spawnImpl: (...args) => {
      spawnCalls.push(args)
      return {}
    },
  })

test('production deploy uses a verified configured non-main branch', async () => {
  const spawnCalls = []
  await run(['node', 'script', '--production'], environment(), async () => success(), spawnCalls)

  assert.deepEqual(spawnCalls[0][1].slice(0, 4), ['pages', 'deploy', '--branch', 'release'])
})

test('preview deploy checks verified remote truth before rejecting its production branch', async () => {
  const spawnCalls = []
  await assert.rejects(
    run(
      ['node', 'script', '--preview'],
      environment({ CLOUDFLARE_PAGES_BRANCH: 'release' }),
      async () => success(),
      spawnCalls,
    ),
    /verified production branch "release"/,
  )
  assert.equal(spawnCalls.length, 0)
})

test('deploy fails closed on remote branch drift without spawning Wrangler', async () => {
  const spawnCalls = []
  await assert.rejects(
    run(['node', 'script', '--preview'], environment({ CLOUDFLARE_PAGES_BRANCH: 'main' }), async () => success(project({ production_branch: 'main' })), spawnCalls),
    /production branch mismatch: remote "main", configured "release"/,
  )
  assert.equal(spawnCalls.length, 0)
})

for (const [name, env, fetchImpl, message] of [
  ['missing credentials', environment({ CLOUDFLARE_API_TOKEN: '' }), async () => success(), /needs CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN/],
  ['404', environment(), async () => response('', 404), /was not found/],
  ['HTTP API error', environment(), async () => response('', 500), /Failed to read Cloudflare Pages project/],
  ['branch absent', environment(), async () => success({ name: 'terrascale-landing-page' }), /invalid metadata/],
  ['envelope failure', environment(), async () => response(JSON.stringify({ success: false, result: project() })), /invalid metadata/],
  ['malformed JSON', environment(), async () => response('{'), /invalid metadata/],
]) {
  test(`deploy ${name} fails without spawning Wrangler`, async () => {
    const spawnCalls = []
    await assert.rejects(run(['node', 'script', '--production'], env, fetchImpl, spawnCalls), message)
    assert.equal(spawnCalls.length, 0)
  })
}

test('runtime API-base overrides cannot send a token to a hostile origin', async () => {
  const spawnCalls = []
  await assert.rejects(
    run(
      ['node', 'script', '--production'],
      environment({ CLOUDFLARE_PAGES_API_BASE_URL: 'https://hostile.example' }),
      async () => assert.fail('fetch must not run when an API-origin override is set'),
      spawnCalls,
    ),
    /CLOUDFLARE_PAGES_API_BASE_URL is not supported/,
  )
  assert.equal(spawnCalls.length, 0)
})

test('project creation requires verified remote metadata and never spawns Wrangler', async () => {
  await assert.rejects(
    verifyExistingProject({ environment: environment(), fetchImpl: async () => response('', 404) }),
    /was not found/,
  )
  await assert.rejects(
    verifyExistingProject({ environment: environment({ CLOUDFLARE_API_TOKEN: '' }), fetchImpl: async () => success() }),
    /needs CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN/,
  )
  await assert.rejects(
    verifyExistingProject({ environment: environment(), fetchImpl: async () => success(project({ production_branch: 'main' })) }),
    /production branch mismatch/,
  )
})
