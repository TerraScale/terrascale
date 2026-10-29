import { readTokenJson } from './read-token-json.mjs'

// The project key and token-handler.mjs must never enter the browser bundle.
export function createBrowserSession({ userId, project, database, onInvalidated }) {
  if (typeof onInvalidated !== 'function')
    throw new TypeError('An app session invalidation callback is required.')
  const lifetime = new AbortController()
  function invalidate() {
    if (lifetime.signal.aborted) return
    lifetime.abort()
    onInvalidated()
  }
  return {
    userId, project, database,
    dispose() { lifetime.abort() },
    async getAccessToken() {
      const signal = AbortSignal.any([lifetime.signal, AbortSignal.timeout(5_000)])
      signal.throwIfAborted()
      const response = await fetch('/api/terrabase-token', {
        method: 'POST', credentials: 'same-origin', cache: 'no-store',
        redirect: 'error', signal,
      })
      signal.throwIfAborted()
      if (!response.ok) {
        const error = new Error('App token request failed.')
        error.status = response.status
        if (response.status === 401 || response.status === 403) invalidate()
        await response.body?.cancel()
        throw error
      }
      const token = await readTokenJson(response, signal)
      signal.throwIfAborted()
      if (token?.user_id !== userId) {
        const error = new Error('App account changed. Sign in again.')
        error.status = 401
        invalidate()
        throw error
      }
      if (token.token_type !== 'Bearer' || typeof token.access_token !== 'string' ||
          token.access_token.length > 8192 ||
          !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(token.access_token) ||
          !Number.isInteger(token.expires_in) || token.expires_in <= 0 || token.expires_in > 900)
        throw new Error('Invalid app token response.')
      return token.access_token
    },
  }
}
