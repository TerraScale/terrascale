import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import path from 'node:path'
import test from 'node:test'

import {
  checkDeployedRedirects,
  validateRedirectRules,
} from '../scripts/validate-redirect-contract.mjs'
import { probeExternalLinks } from '../scripts/validate-links.mjs'
import { validateRenderedLinks } from '../scripts/validate-links.mjs'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const claimsValidator = path.join(root, 'scripts/validate-claims.mjs')
const claimsFixtureRoot = path.join(root, 'tests/fixtures/claims-prohibited')
const linksFixture = path.join(root, 'tests/fixtures/links-prohibited.html')

test('redirect rules reject insecure or non-permanent migrations', () => {
  const failures = validateRedirectRules(`
    /signup http://dash.terrascale.tech/register 302
    https://dashboard.terrascale.tech/* https://dash.terrascale.tech/:splat 308
  `)

  assert.ok(failures.some((failure) => /HTTPS/i.test(failure)))
  assert.ok(failures.some((failure) => /308/i.test(failure)))
})

test('deployed redirect checks verify the complete safe chain', async () => {
  const requests = []
  const responses = new Map([
    [
      'https://terrascale.tech/signup?plan=pro&return=%2Fpricing',
      new Response(null, {
        status: 308,
        headers: { location: 'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing' },
      }),
    ],
    [
      'https://dash.terrascale.tech/register?plan=pro&return=%2Fpricing',
      new Response('ok', { status: 200 }),
    ],
    [
      'https://dashboard.terrascale.tech/login?next=%2Fprojects',
      new Response(null, {
        status: 308,
        headers: { location: 'https://dash.terrascale.tech/login?next=%2Fprojects' },
      }),
    ],
    [
      'https://dash.terrascale.tech/login?next=%2Fprojects',
      new Response('ok', { status: 200 }),
    ],
  ])

  const failures = await checkDeployedRedirects({
    fetchImpl: async (url) => {
      requests.push(url)
      return responses.get(url) ?? new Response('not found', { status: 404 })
    },
  })

  assert.deepEqual(failures, [])
  assert.deepEqual(requests, [...responses.keys()])
})

test('rendered claim scans inspect metadata and structured data', () => {
  const result = spawnSync(process.execPath, [claimsValidator, '--rendered'], {
    cwd: root,
    env: { ...process.env, TERRASCALE_VALIDATOR_ROOT: claimsFixtureRoot },
    encoding: 'utf8',
  })

  assert.notEqual(result.status, 0)
  assert.match(`${result.stdout}\n${result.stderr}`, /99\.9|zero[- ]latency|instant replication/i)
})

test('release link probes fail closed on an unavailable required endpoint', async () => {
  const failures = await probeExternalLinks(['https://status.terrascale.tech'], {
    fetchImpl: async () => new Response('unavailable', { status: 503 }),
  })

  assert.match(failures.join('\n'), /returned 503/)
})

test('rendered link scans inspect metadata and structured-data URLs', () => {
  const result = validateRenderedLinks({ buildRoot: root, files: [linksFixture] })

  assert.match(result.failures.join('\n'), /must use HTTPS|preview or non-canonical external host/i)
})

test('the release script is explicit and separate from offline checks', () => {
  const packageJson = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8'))
  assert.match(packageJson.scripts['check:release'], /--release/)
  assert.doesNotMatch(packageJson.scripts.check, /--release/)
})
