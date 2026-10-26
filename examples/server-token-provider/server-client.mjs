// SERVER ONLY. Uses published @terrascale/sdk 0.0.10 or newer.
import { env } from 'node:process'
import { createTerraBaseV1Client, requireId } from '@terrascale/sdk'
import { createTsAuthClient } from '@terrascale/sdk/ts-auth'

function required(name) {
  const value = env[name]
  if (!value) throw new Error(`Missing ${name}.`)
  return value
}

export function createServerClient() {
  const project = requireId(required('TERRASCALE_PROJECT_ID'), 'project')
  const apiKey = required('TERRASCALE_API_KEY')
  const separator = apiKey.indexOf('_')
  if (separator < 1) throw new Error('Expected a dashboard project API key.')
  const keyId = requireId(apiKey.slice(0, separator), 'key ID')
  const auth = createTsAuthClient({
    apiBaseUrl: 'https://auth.terrascale.tech',
    instanceSlug: 'terrascale',
  })
  let cached
  let expiresAt = 0
  let pending
  async function exchange() {
    const started = performance.now()
    const result = await auth.oidc.token(keyId, apiKey, {
      grant_type: 'client_credentials',
      project,
      scope: 'documents:read documents:write',
    })
    if (!result.ok) throw new Error('Project token exchange failed; check server key permissions.')
    const value = result.value
    const deadline = started + value.expires_in * 1000
    if (deadline - performance.now() <= 5000) throw new Error('Project token lifetime is too short.')
    cached = value.access_token
    expiresAt = deadline
    return cached
  }
  async function getAccessToken() {
    if (cached !== undefined && expiresAt - performance.now() > 5000) return cached
    if (pending === undefined) pending = exchange().finally(() => { pending = undefined })
    return pending
  }
  const client = createTerraBaseV1Client({
    baseUrl: required('TERRASCALE_API_URL'),
    project,
    token: getAccessToken,
  })
  return { client, getAccessToken }
}
