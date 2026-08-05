import assert from 'node:assert/strict'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import test from 'node:test'
import { runDeploy } from './cloudflare-pages-deploy.mjs'
import { runProjectCreate } from './cloudflare-pages-project-create.mjs'
import { verifyPagesReadOnly } from './cloudflare-pages-verify.mjs'
import { stagePagesCandidate } from './stage-pages-candidate.mjs'

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
  const workflow = readFileSync(new URL('../.forgejo/workflows/ci.yml', import.meta.url), 'utf8')
  const readme = readFileSync(new URL('../README.md', import.meta.url), 'utf8')
  const promotionWorkflow = workflow.slice(workflow.search(/\n {2}promote:/))
  const staticProfile = deployConfig.prProfiles.find((profile) => profile.name === 'static')
  const releaseProfile = deployConfig.releaseProfiles.find((profile) => profile.name === 'release-e2e')

  assert.match(packageJson.scripts.check, /node --test scripts\/cloudflare-pages-deploy\.test\.mjs/)
  assert.ok(staticProfile.commands.some((command) => command.arguments.join(' ') === 'run check'))
  assert.ok(!staticProfile.commands.some((command) => command.arguments.join(' ') === 'test'))
  assert.ok(releaseProfile.commands.some((command) => command.arguments.join(' ') === 'test'))
  assert.ok(releaseProfile.commands.some((command) => command.arguments.join(' ') === 'run check'))
  assert.ok(releaseProfile.commands.some((command) => command.arguments.join(' ') === 'run test:e2e'))
  assert.match(packageJson.scripts['candidate:stage'], /stage-pages-candidate\.mjs/)
  assert.match(workflow, /ci release-test --repository-root \. --profile "\$\{\{ matrix\.profile \}\}" --result artifacts\/candidate\/test-results\/release-e2e\.test-result\.json/)
  assert.match(workflow, /name: release-test-result-release-e2e, path: artifacts\/candidate\/test-results\/release-e2e\.test-result\.json, if-no-files-found: error/)
  assert.match(workflow, /name: candidate-pages, path: artifacts\/candidate, if-no-files-found: error/)
  assert.match(workflow, /npm run build\n {10}\.tools\/terrascale-deploy ci build-candidate/)
  assert.match(workflow, /npm run candidate:stage/)
  assert.match(workflow, /name: candidate-pages, path: artifacts\/candidate/)
  assert.match(workflow, /name: release-test-result-release-e2e, path: artifacts\/candidate\/test-results/)
  assert.match(workflow, /test -f "\$\{test_results\}"/)
  assert.match(workflow, /find artifacts\/candidate\/test-results -type f \| wc -l\)" -eq 1/)
  assert.match(workflow, /candidate-manifest .*--test-results "\$\{test_results\}"/)
  assert.match(workflow, /bundle_entry="test-results\/release-e2e\.test-result\.json"/)
  assert.match(workflow, /unzip -Z1 artifacts\/candidate\.zip \| grep -Fx "\$\{bundle_entry\}"/)
  assert.match(workflow, /unzip -p artifacts\/candidate\.zip "\$\{bundle_entry\}" \| sha256sum/)
  assert.doesNotMatch(workflow, /with: \{ path: artifacts\/candidate \}/)
  assert.match(workflow, /name: deploy-cli, path: \.tools/)
  assert.ok(!('promotion' in deployConfig))
  assert.match(promotionWorkflow, /Promotion blocked: Git-integrated Pages migration is required and external provisioning is not authorized\./)
  assert.match(promotionWorkflow, /exit 1/)
  assert.doesNotMatch(promotionWorkflow, /candidate-store|ci promote|download-artifact|checkout@/)
  assert.match(readme, /Release candidates remain verified artifacts.*not a deployment/s)
  assert.match(readme, /Activation requires explicit authorization to provision a new Git-integrated Pages project/)
})

test('candidate staging copies the static site into the deterministic payload tree', () => {
  const directory = mkdtempSync(join(tmpdir(), 'terrascale-candidate-'))
  const buildDirectory = join(directory, 'build')
  const candidateDirectory = join(directory, 'artifacts', 'candidate')
  mkdirSync(join(buildDirectory, '_app'), { recursive: true })
  writeFileSync(join(buildDirectory, 'index.html'), '<!doctype html>')
  writeFileSync(join(buildDirectory, '_app', 'entry.js'), 'export {}')

  try {
    assert.equal(stagePagesCandidate({ buildDirectory, candidateDirectory }), join(candidateDirectory, 'site'))
    assert.ok(existsSync(join(candidateDirectory, 'site', 'index.html')))
    assert.ok(existsSync(join(candidateDirectory, 'site', '_app', 'entry.js')))
  } finally {
    rmSync(directory, { force: true, recursive: true })
  }
})
