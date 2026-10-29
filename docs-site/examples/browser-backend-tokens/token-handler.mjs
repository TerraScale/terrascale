import { readTokenJson } from './read-token-json.mjs'

const ulid = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/

// Mount in your existing app backend. readSession must validate the app session,
// returning null or { userId, roles? } from trusted server-side identity data.
export function createTokenHandler({ origin, project, projectKey, readSession }) {
  if (new URL(origin).origin !== origin || !origin.startsWith('https://') ||
      !ulid.test(project) || typeof projectKey !== 'string' || !projectKey ||
      /\s/.test(projectKey) || typeof readSession !== 'function')
    throw new TypeError('Invalid server token integration configuration.')

  const endpoint = `https://auth.terrascale.tech/v1/terrascale/projects/${project}/end-user-tokens`
  const reply = (status, body, headers = {}) => Response.json(body, {
    status, headers: { 'cache-control': 'no-store', ...headers },
  })

  return async function handleToken(request) {
    const url = new URL(request.url)
    if (url.pathname !== '/api/terrabase-token' || url.search)
      return reply(404, { error: 'Not found.' })
    if (request.method !== 'POST')
      return reply(405, { error: 'POST required.' }, { allow: 'POST' })
    // Use a fixed trusted origin, never Host or forwarded request headers.
    if (request.headers.get('origin') !== origin)
      return reply(403, { error: 'Origin denied.' })
    // There are no browser-supplied claims, user IDs, or routing parameters.
    if (request.body !== null)
      return reply(400, { error: 'Request body must be empty.' })

    const signal = AbortSignal.any([request.signal, AbortSignal.timeout(5_000)])
    try {
      signal.throwIfAborted()
      const session = await readSession(request, { signal })
      signal.throwIfAborted()
      if (!session) return reply(401, { error: 'App sign-in required.' })
      const { userId, roles } = session
      if (!ulid.test(userId) || (roles !== undefined &&
          (!Array.isArray(roles) || !roles.every(role => typeof role === 'string'))))
        return reply(503, { error: 'Token service unavailable.' })

      const body = { user_id: userId }
      if (roles !== undefined) body.claims = { roles }
      const response = await fetch(endpoint, {
        method: 'POST', redirect: 'error', credentials: 'omit', cache: 'no-store', signal,
        headers: { authorization: `Bearer ${projectKey}`, 'content-type': 'application/json' },
        body: JSON.stringify(body),
      })
      if (!response.ok) {
        await response.body?.cancel()
        return reply(502, { error: 'Token service unavailable.' })
      }
      const token = await readTokenJson(response, signal)
      signal.throwIfAborted()
      if (!validToken(token)) return reply(502, { error: 'Token service unavailable.' })
      // Allowlist the response; never forward upstream headers or error bodies.
      return reply(200, {
        user_id: userId, access_token: token.access_token,
        token_type: 'Bearer', expires_in: token.expires_in,
      })
    } catch {
      return reply(503, { error: 'Token service unavailable.' })
    }
  }
}

function validToken(token) {
  return token !== null && typeof token === 'object' && token.token_type === 'Bearer' &&
    Number.isInteger(token.expires_in) && token.expires_in > 0 && token.expires_in <= 900 &&
    typeof token.access_token === 'string' && token.access_token.length <= 8192 &&
    /^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(token.access_token)
}
