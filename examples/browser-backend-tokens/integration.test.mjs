import assert from 'node:assert/strict'
import test from 'node:test'
import { mountTodos } from '../browser-todos/todos.mjs'
import { connectTodos } from './connect-todos.mjs'

const project = '01ARZ3NDEKTSV4RRFFQ69G5FAV'
const userA = '01ARZ3NDEKTSV4RRFFQ69G5FAW'
const userB = '01ARZ3NDEKTSV4RRFFQ69G5FAX'
const token = user_id => ({ user_id, access_token: 'header.payload.signature', token_type: 'Bearer', expires_in: 900 })
const deferred = () => {
  let resolve
  const promise = new Promise(done => { resolve = done })
  return { promise, resolve }
}
const until = async predicate => {
  for (let i = 0; i < 100; i++) {
    if (predicate()) return
    await new Promise(resolve => setTimeout(resolve, 5))
  }
  assert.fail('Expected integration state was not reached')
}

class Element {
  constructor(ownerDocument) {
    this.ownerDocument = ownerDocument
    this.children = []
    this.events = {}
    this.value = ''
    this.textContent = ''
  }
  append(...nodes) { this.children.push(...nodes) }
  replaceChildren(...nodes) { this.children = nodes }
  setAttribute() {}
  addEventListener(type, handler) { this.events[type] = handler }
  fire(type) { return this.events[type]?.({ preventDefault() {} }) }
  set innerHTML(_) { assert.fail('Unexpected HTML assignment') }
}

function fixture(t) {
  const document = { createElement: () => new Element(document) }
  const root = document.createElement('div')
  const streams = [], connections = []
  let identity = userA, denial, tokenHold, queryHold, mutations = 0, view, active
  t.mock.method(globalThis, 'fetch', async (url, options) => {
    if (url === '/api/terrabase-token') {
      const response = denial === 'mismatch' ? Response.json(token(userB))
        : denial ? new Response(null, { status: denial }) : Response.json(token(identity))
      if (tokenHold) {
        const held = tokenHold
        tokenHold = undefined
        held.started = true
        await held.promise
      }
      return response
    }
    const path = new URL(url).pathname
    if (path.endsWith('/changes')) {
      const stream = { signal: options.signal }
      streams.push(stream)
      return new Response(new ReadableStream({
        start(controller) {
          options.signal.addEventListener('abort', () => controller.error(options.signal.reason), { once: true })
        },
      }), { headers: { 'content-type': 'text/event-stream' } })
    }
    if (path.endsWith('/query')) {
      const query = await new Response(options.body).json()
      const owner = query.filter.eq[1]
      if (queryHold) {
        const held = queryHold
        queryHold = undefined
        held.signal = options.signal
        held.started = true
        await held.promise
      }
      return Response.json({
        items: [{ id: userA, version: 'version-1', state: 'value',
          document: { userId: owner, title: owner === userA ? 'A private record' : 'B private record', done: false } }],
        next_cursor: null,
      })
    }
    mutations++
    assert.fail('A mutation must not reach TerraBase after app session invalidation')
  })
  function connect(owner = userA) {
    active?.dispose()
    identity = owner
    denial = undefined
    active = connectTodos(session => {
      view = mountTodos(root, session)
      return view
    }, { userId: owner, project, database: 'demo' })
    connections.push(active)
    return active
  }
  t.after(() => connections.forEach(connection => connection.dispose()))
  return {
    root, streams, connect,
    get view() { return view },
    get mutations() { return mutations },
    deny(mode) { denial = mode },
    holdToken() { tokenHold = { ...deferred(), started: false }; return tokenHold },
    holdQuery() { queryHold = { ...deferred(), started: false }; return queryHold },
  }
}
const rows = f => f.root.children[3].children

for (const mode of [401, 403, 'mismatch']) {
  test(`real SDK: app ${mode} clears private rows and aborts the stream and pending read`, async t => {
    const f = fixture(t)
    await f.connect().ready
    await until(() => f.streams.length === 1)
    assert.equal(rows(f)[0].children[0].value, 'A private record')
    const complete = rows(f)[0].children[1]
    const held = f.holdQuery()
    const reading = f.view.refresh()
    await until(() => held.started)
    f.deny(mode)
    await complete.fire('click')
    assert.equal(f.root.children.length, 0)
    assert.equal(f.streams[0].signal.aborted, true)
    assert.equal(held.signal.aborted, true)
    assert.equal(f.mutations, 0)
    held.resolve()
    await reading
    assert.equal(f.root.children.length, 0, 'A late read cannot restore cleared private data')
  })
}

for (const mode of [401, 403, 'mismatch']) {
  test(`real SDK: late A ${mode} after B mounts cannot invalidate B`, async t => {
    const f = fixture(t)
    await f.connect().ready
    await until(() => f.streams.length === 1)
    f.deny(mode)
    const held = f.holdToken()
    const completing = rows(f)[0].children[1].fire('click')
    await until(() => held.started)
    await f.connect(userB).ready
    await until(() => f.streams.length === 2)
    held.resolve()
    await completing
    assert.equal(rows(f)[0].children[0].value, 'B private record')
    assert.equal(f.streams[1].signal.aborted, false)
    assert.equal(f.mutations, 0)
  })
}

test('real SDK: initial app denial clears the mounted view without relying on error.status', async t => {
  const f = fixture(t)
  const app = f.connect()
  f.deny(401)
  await app.ready
  assert.equal(f.root.children.length, 0)
  assert.equal(f.streams.length, 0)
  assert.equal(f.mutations, 0)
})

test('synchronous invalidation before view assignment disposes the eventual view once', async t => {
  let disposed = 0, provider
  t.mock.method(globalThis, 'fetch', () => assert.fail('Disposed provider must not fetch'))
  const connection = connectTodos((session, invalidate) => {
    provider = session
    invalidate()
    invalidate()
    return { dispose() { disposed++ }, ready: Promise.resolve() }
  }, { userId: userA, project, database: 'demo' })
  connection.dispose()
  assert.equal(disposed, 1)
  await assert.rejects(provider.getAccessToken(), { name: 'AbortError' })
})
