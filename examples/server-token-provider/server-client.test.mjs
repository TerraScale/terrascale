import test from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { createServerClient } from './server-client.mjs'
const project = '01ARZ3NDEKTSV4RRFFQ69G5FA1', keyId = '01ARZ3NDEKTSV4RRFFQ6000002'
const key = `${keyId}_synthetic_fixture`
async function fixture(t, handler) {
  const requests = []
  const server = createServer(async (req, res) => {
    let body = ''; for await (const chunk of req) body += chunk
    requests.push({ url: req.url, headers: req.headers, form: new URLSearchParams(body) })
    const result = handler(requests.length)
    res.writeHead(result.status ?? 200, { 'content-type': 'application/json' }); res.end(JSON.stringify(result.body))
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)) })
  const origin = `http://127.0.0.1:${server.address().port}`, nativeFetch = globalThis.fetch
  // Native HTTP fixture routing only; no production-origin network request.
  t.mock.method(globalThis, 'fetch', (input, init) => {
    const request = new Request(input, init), url = new URL(request.url)
    return nativeFetch(url.origin === 'https://auth.terrascale.tech' ? new Request(origin + url.pathname, request) : request)
  })
  process.env.TERRASCALE_API_KEY = key; process.env.TERRASCALE_PROJECT_ID = project; process.env.TERRASCALE_API_URL = origin
  return { requests, ...createServerClient() }
}
const token = (value = 'fixture.payload.signature', expires_in = 60) => ({ access_token: value, token_type: 'Bearer', expires_in })
test('coalesces and caches; exact project grant without resource', async t => {
  const f = await fixture(t, () => ({ body: token() }))
  const values = await Promise.all(Array.from({ length: 8 }, () => f.getAccessToken()))
  assert.ok(values.every(value => value === 'fixture.payload.signature'))
  assert.equal(await f.getAccessToken(), values[0]); assert.equal(f.requests.length, 1)
  const r = f.requests[0]
  assert.equal(r.url, '/terrascale/oauth/token')
  assert.deepEqual(Object.fromEntries(r.form), { client_id: keyId, client_secret: key, grant_type: 'client_credentials', project, scope: 'documents:read documents:write' })
  assert.equal(r.headers.authorization, undefined)
})
test('renews early, denial never falls back to stale cache, explicit later retry coalesces', async t => {
  const f = await fixture(t, n => n === 1 ? { body: token('old.payload.signature', 6) } : n === 2 ? { status: 401, body: { error: 'invalid_client' } } : { body: token('new.payload.signature') })
  assert.equal(await f.getAccessToken(), 'old.payload.signature')
  await new Promise(resolve => setTimeout(resolve, 1100))
  await assert.rejects(f.getAccessToken(), /Project token exchange failed/)
  assert.equal(f.requests.length, 2)
  assert.deepEqual(await Promise.all([f.getAccessToken(), f.getAccessToken()]), ['new.payload.signature', 'new.payload.signature'])
  assert.equal(f.requests.length, 3)
})
for (const body of [{ ...token(), token_type: 'wrong' }, token('fixture.payload.signature', 1)]) {
  test('rejects malformed/too-short grants without credentials in errors', async t => {
    const f = await fixture(t, () => ({ body }))
    await assert.rejects(f.getAccessToken(), error => !String(error).includes(key)); assert.equal(f.requests.length, 1)
  })
}
test('data requests send only exchanged bearer token', async t => {
  const f = await fixture(t, n => n === 1 ? { body: token() } : { status: 404, body: { error: { code: 'not_found' } } })
  await assert.rejects(f.client.database('app').collection('posts').get('01ARZ3NDEKTSV4RRFFQ6000003'), error => error.status === 404)
  assert.equal(f.requests.length, 2); assert.equal(f.requests[1].headers.authorization, 'Bearer fixture.payload.signature')
  assert.ok(f.requests[1].url.startsWith(`/v1/projects/${project}/databases/app/collections/posts/`))
})
