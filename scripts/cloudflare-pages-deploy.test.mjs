import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
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

test('PR static CI runs retirement guards through the approved check command', () => {
  const packageJson = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))
  const deployConfig = JSON.parse(readFileSync(new URL('../.terrascale/deploy.json', import.meta.url), 'utf8'))
  const staticProfile = deployConfig.prProfiles.find((profile) => profile.name === 'static')
  const releaseProfile = deployConfig.releaseProfiles.find((profile) => profile.name === 'release-e2e')

  assert.match(packageJson.scripts.check, /node --test scripts\/cloudflare-pages-deploy\.test\.mjs/)
  assert.ok(staticProfile.commands.some((command) => command.arguments.join(' ') === 'run check'))
  assert.ok(!staticProfile.commands.some((command) => command.arguments.join(' ') === 'test'))
  assert.ok(releaseProfile.commands.some((command) => command.arguments.join(' ') === 'test'))
})
