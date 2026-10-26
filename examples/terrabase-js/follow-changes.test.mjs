import test from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { createTerraBaseV1Client, TerraBaseV1Error } from '@terrascale/sdk'
import { followChanges } from './follow-changes.mjs'
const project = '01ARZ3NDEKTSV4RRFFQ69G5FA1'
const change = { collection: 'posts', id: 'post', op: 'upsert', version: '1', origin: '0123456789abcdef', document: { title: 'hello' } }
const frame = (kind, data, id) => `event: ${kind}\n${id === undefined ? '' : `id: ${id}\n`}data: ${JSON.stringify(data)}\n\n`
const cursor = id => frame('cursor', { cursor: id }, id)
const sse = (res, data, end = false) => { res.writeHead(200, { 'content-type': 'text/event-stream' }); end ? res.end(data) : res.write(data) }
const fail = (res, status, code, headers = {}) => { res.writeHead(status, { 'content-type': 'application/json', ...headers }); res.end(JSON.stringify({ error: { code } })) }
async function fixture(t, handle, token = () => 'fixture.payload.signature') {
  const requests = []
  const server = createServer((req, res) => { requests.push({ url: new URL(req.url, 'http://localhost'), headers: req.headers, time: Date.now() }); handle(req, res, requests.length) })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)) })
  const db = createTerraBaseV1Client({ project, token, baseUrl: `http://127.0.0.1:${server.address().port}` }).database('app')
  const controller = new AbortController(), stop = new Error('fixture stopped')
  const run = options => followChanges({ db, signal: AbortSignal.any([controller.signal, AbortSignal.timeout(6000)]), onChange: () => {}, onReset: () => { throw new Error('unexpected reset') }, ...options })
  return { db, requests, controller, stop, run }
}
test('normal EOF refreshes token, preserves filter and resumes only an applied checkpoint', async t => {
  let token = 'first.payload.signature'
  const f = await fixture(t, (_req, res, n) => sse(res, n === 1 ? cursor('before') + frame('change', change) : frame('change', change, 'after'), n === 1), () => token)
  const order = []
  await assert.rejects(f.run({ onChange: async event => { order.push(event.id); token = 'next.payload.signature' }, onCheckpoint: id => { order.push(id); if (id === 'after') f.controller.abort(f.stop) } }), error => error === f.stop)
  assert.deepEqual(order, ['before', 'post', 'post', 'after'])
  assert.deepEqual(f.requests.map(r => r.headers['last-event-id']), [undefined, 'before'])
  assert.deepEqual(f.requests.map(r => r.headers.authorization), ['Bearer first.payload.signature', 'Bearer next.payload.signature'])
  assert.ok(f.requests.every(r => r.url.searchParams.get('collections') === 'posts'))
})
for (const [status, code] of [[401, 'invalid_key'], [403, 'forbidden_scope'], [400, 'invalid_cursor']]) {
  test(`HTTP ${status} is terminal without replay`, async t => {
    const f = await fixture(t, (_req, res) => fail(res, status, code))
    await assert.rejects(f.run({}), error => error instanceof TerraBaseV1Error && error.status === status)
    assert.equal(f.requests.length, 1)
  })
}
test('HTTP-200 terminal denial is surfaced', async t => {
  const f = await fixture(t, (_req, res) => sse(res, frame('error', { error: { code: 'forbidden_scope' } }), true))
  await assert.rejects(f.run({}), error => error instanceof TerraBaseV1Error && error.status === 200 && error.code === 'forbidden_scope')
  assert.equal(f.requests.length, 1)
})
for (const expiredStatus of [200, 410]) {
  test(`expired ${expiredStatus}: capture before rebuilding, persist after, then replay concurrent changes`, async t => {
    const order = []
    const f = await fixture(t, (_req, res, n) => {
      if (n === 1) expiredStatus === 410 ? fail(res, 410, 'cursor_expired') : sse(res, frame('error', { error: { code: 'cursor_expired' } }), true)
      else if (n === 2) sse(res, cursor('fresh'))
      else sse(res, frame('change', change, 'applied'))
    })
    await assert.rejects(f.run({ lastEventId: 'old', onReset: async () => { assert.equal(f.requests.length, 2); order.push('rebuild') }, onChange: () => order.push('change'), onCheckpoint: id => { order.push(id); if (id === 'applied') f.controller.abort(f.stop) } }), error => error === f.stop)
    assert.deepEqual(order, ['rebuild', 'fresh', 'change', 'applied'])
    assert.deepEqual(f.requests.map(r => r.headers['last-event-id']), ['old', undefined, 'fresh'])
  })
}
for (const status of [429, 503]) {
  test(`${status}: Retry-After seconds bounds the safe read retry`, async t => {
    const f = await fixture(t, (_req, res, n) => n === 1 ? fail(res, status, 'changes_busy', { 'retry-after': '1' }) : sse(res, cursor('ready')))
    await assert.rejects(f.run({ onCheckpoint: () => f.controller.abort(f.stop) }), error => error === f.stop)
    assert.equal(f.requests.length, 2)
    assert.ok(f.requests[1].time - f.requests[0].time >= 990)
  })
}
test('Retry-After HTTP date is also respected', async t => {
  let retryAt
  const f = await fixture(t, (_req, res, n) => { if (n === 1) { retryAt = new Date(Date.now() + 2000).toUTCString(); fail(res, 429, 'changes_busy', { 'retry-after': retryAt }) } else sse(res, cursor('ready')) })
  await assert.rejects(f.run({ onCheckpoint: () => f.controller.abort(f.stop) }), error => error === f.stop)
  assert.ok(f.requests[1].time >= Date.parse(retryAt) - 10)
})
test('cancellation interrupts blocked reads and backoff without a new request', async t => {
  for (const overloaded of [false, true]) {
    const f = await fixture(t, (_req, res) => { overloaded ? fail(res, 429, 'changes_busy', { 'retry-after': '60' }) : sse(res, cursor('waiting')); setTimeout(() => f.controller.abort(f.stop), 30) })
    await assert.rejects(f.run({}), error => error === f.stop)
    assert.equal(f.requests.length, 1)
  }
})
test('application/persistence failures are terminal, including errors shaped like overload', async t => {
  for (const callback of ['onChange', 'onCheckpoint']) {
    const f = await fixture(t, (_req, res) => sse(res, frame('change', change, 'next')))
    const error = new TerraBaseV1Error({ status: 503, code: 'application_failed' })
    await assert.rejects(f.run({ [callback]: () => { throw error } }), value => value === error)
    assert.equal(f.requests.length, 1)
  }
})
test('malformed stream and lost connection reach the caller without automatic replay', async t => {
  for (const malformed of [true, false]) {
    const f = await fixture(t, (_req, res) => { sse(res, malformed ? 'data: incomplete' : cursor('before')); setTimeout(() => malformed ? res.end() : res.destroy(), 10) })
    await assert.rejects(f.run({}))
    assert.equal(f.requests.length, 1)
  }
})
test('failed rebuild does not persist the new checkpoint or start replay', async t => {
  const f = await fixture(t, (_req, res, n) => n === 1 ? fail(res, 410, 'cursor_expired') : sse(res, cursor('fresh')))
  const checkpoints = [], failure = new Error('snapshot failed')
  await assert.rejects(f.run({ onReset: () => { throw failure }, onCheckpoint: id => checkpoints.push(id) }), error => error === failure)
  assert.deepEqual(checkpoints, [])
  assert.equal(f.requests.length, 2)
})
