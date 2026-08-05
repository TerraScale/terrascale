import assert from 'node:assert/strict'
import test from 'node:test'
import { runDeploy } from './cloudflare-pages-deploy.mjs'
import { runProjectCreate } from './cloudflare-pages-project-create.mjs'
import { verifyPagesReadOnly } from './cloudflare-pages-verify.mjs'

const writeEnvironment = {
  CLOUDFLARE_ACCOUNT_ID: 'account',
  CLOUDFLARE_API_TOKEN: 'write-token',
  CLOUDFLARE_PAGES_READ_TOKEN: 'read-token',
}

for (const [name, command] of [
  ['production direct upload', () => runDeploy({ argv: ['node', 'script', '--production'], environment: writeEnvironment, fetchImpl: () => assert.fail('fetch must not run'), spawnImpl: () => assert.fail('Wrangler must not run') })],
  ['preview direct upload', () => runDeploy({ argv: ['node', 'script', '--preview'], environment: writeEnvironment, fetchImpl: () => assert.fail('fetch must not run'), spawnImpl: () => assert.fail('Wrangler must not run') })],
  ['project creation', () => runProjectCreate({ environment: writeEnvironment, fetchImpl: () => assert.fail('fetch must not run'), spawnImpl: () => assert.fail('Wrangler must not run') })],
]) {
  test(`${name} refuses before any fetch or Wrangler spawn`, () => {
    assert.throws(command, /Direct Upload is retired.*Pages Write/)
  })
}

test('read-only diagnostic uses only the separate Pages Read token', async () => {
  let request
  const result = await verifyPagesReadOnly({
    environment: writeEnvironment,
    fetchImpl: async (url, options) => {
      request = { options, url }
      return new Response(
        JSON.stringify({
          success: true,
          result: { name: 'terrascale-landing-page', production_branch: 'main' },
        }),
      )
    },
  })

  assert.equal(result.production_branch, 'main')
  assert.match(request.url, /^https:\/\/api\.cloudflare\.com\/client\/v4\//)
  assert.equal(request.options.headers.Authorization, 'Bearer read-token')
  assert.doesNotMatch(request.options.headers.Authorization, /write-token/)
})

test('read-only diagnostic refuses a write token without a separate read token before fetch', async () => {
  await assert.rejects(
    verifyPagesReadOnly({
      environment: { CLOUDFLARE_ACCOUNT_ID: 'account', CLOUDFLARE_API_TOKEN: 'write-token' },
      fetchImpl: async () => assert.fail('fetch must not run without a Pages Read token'),
    }),
    /CLOUDFLARE_PAGES_READ_TOKEN/,
  )
})
