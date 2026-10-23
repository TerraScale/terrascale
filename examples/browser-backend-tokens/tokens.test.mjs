import assert from 'node:assert/strict'
import test from 'node:test'
import { createTokenHandler } from './token-handler.mjs'
import { createBrowserSession } from './browser-session.mjs'

const origin = 'https://app.example'
const project = '01ARZ3NDEKTSV4RRFFQ69G5FAV'
const userId = '01ARZ3NDEKTSV4RRFFQ69G5FAW'
const otherUser = '01ARZ3NDEKTSV4RRFFQ69G5FAX'
const projectKey = 'synthetic-project-key'
const jwt = 'header.payload.signature' // HTTP fake, not a signed credential.
const token = { access_token: jwt, token_type: 'Bearer', expires_in: 900 }
const browserSession = (options = {}) => createBrowserSession({
  userId, project, database: 'demo', onInvalidated() {}, ...options,
})
const json = body => Response.json(body)
const request = (options = {}) => new Request(`${origin}/api/terrabase-token`, {
  method: 'POST', headers: { origin }, ...options,
})

function fixture(t, session = { userId }) {
  const calls = []
  const upstream = t.mock.method(globalThis, 'fetch', async (url, options) => {
    calls.push({ url, options })
    return json(token)
  })
  const readSession = t.mock.fn(async () => session)
  const handle = createTokenHandler({ origin, project, projectKey, readSession })
  return { handle, calls, upstream, readSession }
}

test('mints only server session identity and roles using the whole project key', async t => {
  const f = fixture(t, { userId, roles: ['reader'], ignoredCustomClaim: 'never copied' })
  const response = await f.handle(request({ headers: { origin, cookie: 'app=session' } }))
  assert.equal(response.status, 200)
  assert.equal(response.headers.get('cache-control'), 'no-store')
  assert.equal(response.headers.get('set-cookie'), null)
  assert.equal(response.headers.get('access-control-allow-origin'), null)
  assert.deepEqual(await response.json(), { ...token, user_id: userId })
  const [{ url, options }] = f.calls
  assert.equal(url, `https://auth.terrascale.tech/v1/terrascale/projects/${project}/end-user-tokens`)
  assert.equal(options.method, 'POST')
  assert.deepEqual(options.headers, {
    authorization: `Bearer ${projectKey}`, 'content-type': 'application/json',
  })
  assert.deepEqual(JSON.parse(options.body), { user_id: userId, claims: { roles: ['reader'] } })
  assert.equal(options.redirect, 'error')
  assert.equal(options.credentials, 'omit')
  assert.equal(options.cache, 'no-store')
  assert.ok(options.signal instanceof AbortSignal)
  assert.equal(f.readSession.mock.calls[0].arguments[0].headers.get('cookie'), 'app=session')
})

test('body, query, method and exact Origin checks precede session access and minting', async t => {
  const f = fixture(t)
  const denied = [
    [request({ headers: {} }), 403],
    [request({ headers: { origin: 'null' } }), 403],
    [request({ headers: { origin: 'https://app.example.evil' } }), 403],
    [request({ headers: { origin: `${origin}/` } }), 403],
    [request({ headers: { origin: 'http://app.example', host: 'app.example' } }), 403],
    [request({ method: 'GET' }), 405],
    [request({ method: 'OPTIONS' }), 405],
    [request({ body: JSON.stringify({ user_id: otherUser, claims: { roles: ['admin'] } }) }), 400],
    [request({ body: '{}' }), 400],
    [new Request(`${origin}/api/terrabase-token?user_id=${otherUser}`, {
      method: 'POST', headers: { origin },
    }), 404],
  ]
  for (const [input, status] of denied) {
    const response = await f.handle(input)
    assert.equal(response.status, status)
    assert.equal(response.headers.get('cache-control'), 'no-store')
    assert.equal(response.headers.get('access-control-allow-origin'), null)
  }
  assert.equal(f.calls.length, 0)
  assert.equal(f.readSession.mock.callCount(), 0)
})

test('missing or malformed trusted sessions cannot mint', async t => {
  const f = fixture(t)
  for (const session of [null, { userId: 'not-a-ulid' }, { userId: `8${userId.slice(1)}` },
    { userId, roles: 'admin' }, { userId, roles: [42] }]) {
    f.readSession.mock.mockImplementation(async () => session)
    const response = await f.handle(request())
    assert.equal(response.status, session === null ? 401 : 503)
    assert.equal(f.calls.length, 0)
  }
  f.readSession.mock.mockImplementation(async () => { throw new Error(projectKey) })
  const response = await f.handle(request())
  assert.equal(response.status, 503)
  assert.equal((await response.text()).includes(projectKey), false)
})

test('optional roles stay omitted and short key-bounded expiry is preserved', async t => {
  const f = fixture(t)
  f.upstream.mock.mockImplementation(async (url, options) => {
    assert.deepEqual(JSON.parse(options.body), { user_id: userId })
    return json({ ...token, expires_in: 12, ignored: projectKey })
  })
  const response = await f.handle(request())
  assert.deepEqual(await response.json(), { ...token, expires_in: 12, user_id: userId })
})

test('session mutation during the mint cannot relabel an already requested identity', async t => {
  const session = { userId }
  const f = fixture(t, session)
  f.upstream.mock.mockImplementation(async (_, options) => {
    assert.deepEqual(JSON.parse(options.body), { user_id: userId })
    session.userId = otherUser
    return json(token)
  })
  const response = await f.handle(request())
  assert.equal((await response.json()).user_id, userId)
})

test('upstream failures and invalid token responses are safe and never retried', async t => {
  const f = fixture(t)
  const cases = [
    () => new Response(projectKey, { status: 401, headers: { 'set-cookie': projectKey } }),
    () => new Response(projectKey, { status: 403 }),
    () => new Response(projectKey, { status: 404 }),
    () => new Response(null, { status: 302, headers: { location: 'https://elsewhere.example' } }),
    () => new Response('not JSON'),
    () => json(null),
    () => json({ ...token, token_type: 'Other' }),
    () => json({ ...token, expires_in: 901 }),
    () => json({ ...token, expires_in: 0 }),
    () => json({ ...token, access_token: projectKey }),
    () => { throw new Error(projectKey) },
  ]
  for (const upstream of cases) {
    f.upstream.mock.mockImplementation(upstream)
    const before = f.upstream.mock.callCount()
    const response = await f.handle(request())
    assert.ok([502, 503].includes(response.status))
    assert.deepEqual(await response.json(), { error: 'Token service unavailable.' })
    assert.equal(response.headers.get('cache-control'), 'no-store')
    assert.equal(response.headers.get('set-cookie'), null)
    assert.equal(f.upstream.mock.callCount(), before + 1)
  }
})

test('request cancellation prevents mint after a delayed session validation', async t => {
  const f = fixture(t)
  const lifetime = new AbortController()
  f.readSession.mock.mockImplementation(async (_, { signal }) => {
    lifetime.abort()
    assert.equal(signal.aborted, true)
    return { userId }
  })
  assert.equal((await f.handle(request({ signal: lifetime.signal }))).status, 503)
  assert.equal(f.calls.length, 0)
})

test('mint fetch is canceled on disconnect and on the fixed five-second deadline', async t => {
  const f = fixture(t)
  const timeout = AbortSignal.timeout.bind(AbortSignal)
  t.mock.method(AbortSignal, 'timeout', ms => {
    assert.equal(ms, 5_000)
    return timeout(20)
  })
  for (const disconnect of [true, false]) {
    const lifetime = new AbortController()
    let fetchSignal
    f.upstream.mock.mockImplementation(async (_, { signal }) => {
      fetchSignal = signal
      if (disconnect) lifetime.abort()
      await new Promise((resolve, reject) => {
        // Keep the test alive while the real AbortSignal deadline expires.
        const keepAlive = setTimeout(resolve, 1_000)
        const aborted = () => { clearTimeout(keepAlive); reject(signal.reason) }
        if (signal.aborted) aborted()
        else signal.addEventListener('abort', aborted, { once: true })
      })
    })
    const response = await f.handle(request({ signal: lifetime.signal }))
    assert.equal(response.status, 503)
    assert.equal(fetchSignal.aborted, true)
  }
})

test('browser provider uses same-origin POST, supplies no identity, and renews in memory', async t => {
  let calls = 0
  t.mock.method(globalThis, 'fetch', async (url, options) => {
    assert.equal(url, '/api/terrabase-token')
    assert.equal(options.method, 'POST')
    assert.equal(options.credentials, 'same-origin')
    assert.equal(options.cache, 'no-store')
    assert.equal(options.redirect, 'error')
    assert.equal(options.headers, undefined)
    assert.equal(options.body, undefined)
    return json({ ...token, access_token: `header.payload.signature${++calls}`, user_id: userId })
  })
  const session = browserSession()
  assert.equal(session.userId, userId)
  assert.equal(session.project, project)
  assert.equal(session.database, 'demo')
  assert.equal(await session.getAccessToken(), 'header.payload.signature1')
  assert.equal(await session.getAccessToken(), 'header.payload.signature2')
  session.dispose()
  await assert.rejects(session.getAccessToken(), { name: 'AbortError' })
  assert.equal(calls, 2)
})

test('browser -> app backend -> fake producer keeps the project key on the server', async t => {
  const handle = createTokenHandler({
    origin, project, projectKey,
    readSession: async input => input.headers.get('cookie') === 'app=valid' ? { userId } : null,
  })
  let mints = 0
  t.mock.method(globalThis, 'fetch', async (url, options) => {
    if (url === '/api/terrabase-token') {
      assert.equal(options.body, undefined)
      assert.equal(options.headers, undefined)
      // Simulate the browser attaching its same-origin session and Origin.
      return handle(request({ signal: options.signal, headers: { origin, cookie: 'app=valid' } }))
    }
    assert.equal(url, `https://auth.terrascale.tech/v1/terrascale/projects/${project}/end-user-tokens`)
    assert.equal(options.headers.authorization, `Bearer ${projectKey}`)
    assert.equal(options.headers.cookie, undefined)
    assert.deepEqual(JSON.parse(options.body), { user_id: userId })
    mints++
    return json(token)
  })
  const session = browserSession()
  assert.equal(await session.getAccessToken(), jwt)
  assert.equal(mints, 1)
})

test('browser rejects account mismatch, denials and malformed responses without exposing errors', async t => {
  const fetch = t.mock.method(globalThis, 'fetch')
  fetch.mock.mockImplementation(async () => json({ ...token, user_id: otherUser }))
  await assert.rejects(browserSession().getAccessToken(), { status: 401 })
  for (const status of [401, 403, 502, 503]) {
    fetch.mock.mockImplementation(async () => new Response(projectKey, { status }))
    await assert.rejects(browserSession().getAccessToken(), { message: 'App token request failed.', status })
  }
  for (const invalid of [null, { ...token, expires_in: 901 },
    { ...token, token_type: 'Other' }, { ...token, access_token: projectKey }]) {
    fetch.mock.mockImplementation(async () => json(invalid && { ...invalid, user_id: userId }))
    await assert.rejects(browserSession().getAccessToken())
  }
})

test('invalidation fires once before rejection and never for an already disposed provider', async t => {
  let callbacks = 0, release
  const fetch = t.mock.method(globalThis, 'fetch', () => new Promise(resolve => { release = resolve }))
  const session = browserSession({ onInvalidated() { callbacks++; session.dispose() } })
  const late = session.getAccessToken()
  fetch.mock.mockImplementation(async () => new Response(null, { status: 401 }))
  await assert.rejects(session.getAccessToken(), { status: 401 })
  assert.equal(callbacks, 1)
  release(new Response(null, { status: 403 }))
  await assert.rejects(late, { name: 'AbortError' })
  assert.equal(callbacks, 1)
})

for (const side of ['backend', 'browser']) {
  test(`${side} accepts the producer's exact 8192-byte signed JWT cap and rejects overflow`, async t => {
    const f = fixture(t)
    for (const length of [8191, 8192, 8193]) {
      const access_token = `a.${'b'.repeat(length - 4)}.c`
      assert.equal(new TextEncoder().encode(access_token).byteLength, length)
      f.upstream.mock.mockImplementation(async () => json({ ...token, user_id: userId, access_token }))
      if (side === 'backend') {
        const response = await f.handle(request())
        assert.equal(response.status, length <= 8192 ? 200 : 502)
        if (length <= 8192) assert.equal((await response.json()).access_token, access_token)
        else assert.deepEqual(await response.json(), { error: 'Token service unavailable.' })
      } else if (length <= 8192) {
        assert.equal(await browserSession().getAccessToken(), access_token)
      } else {
        await assert.rejects(browserSession().getAccessToken(), { message: 'Invalid app token response.' })
      }
    }
  })

  test(`${side} bounds chunked token JSON to exactly 16384 bytes and cancels overflow`, async t => {
    const f = fixture(t)
    const base = JSON.stringify({ ...token, user_id: userId, ignored: '' })
    for (const size of [16_384, 16_385, 1_048_576]) {
      const text = JSON.stringify({ ...token, user_id: userId, ignored: 'x'.repeat(size - base.length) })
      const bytes = new TextEncoder().encode(text)
      let offset = 0, canceled = false
      f.upstream.mock.mockImplementation(async () => new Response(new ReadableStream({
        pull(controller) {
          if (offset === bytes.length) return controller.close()
          controller.enqueue(bytes.subarray(offset, offset += Math.min(1024, bytes.length - offset)))
        },
        cancel() { canceled = true },
      }, { highWaterMark: 0 })))
      if (side === 'backend') {
        const response = await f.handle(request())
        assert.equal(response.status, size === 16_384 ? 200 : 503)
        if (size > 16_384) assert.deepEqual(await response.json(), { error: 'Token service unavailable.' })
      } else if (size === 16_384) {
        assert.equal(await browserSession().getAccessToken(), jwt)
      } else {
        await assert.rejects(browserSession().getAccessToken(), { message: 'Invalid token response.' })
      }
      if (size > 16_384) {
        assert.equal(canceled, true)
        assert.ok(offset <= 17_408, 'Stop pulling immediately after the first overflowing chunk')
      }
    }
  })
}

test('account disposal cancels token fetch and rejects a late successful response', async t => {
  let release, started, signal
  const ready = new Promise(resolve => { started = resolve })
  t.mock.method(globalThis, 'fetch', async (_, options) => {
    signal = options.signal
    started()
    await new Promise(resolve => { release = resolve })
    return json({ ...token, user_id: userId })
  })
  const session = browserSession()
  const pending = session.getAccessToken()
  await ready
  session.dispose()
  assert.equal(signal.aborted, true)
  release()
  await assert.rejects(pending, { name: 'AbortError' })
})
