import assert from 'node:assert/strict'
import test from 'node:test'
import { mountTodos } from './todos.mjs'

// The tiny DOM fake records safe text/property assignments and event handlers.
class Element {
  constructor(tag, ownerDocument) {
    this.tag = tag
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
  set innerHTML(_) { assert.fail('Untrusted text must not become HTML') }
}
const document = { createElement: tag => new Element(tag, document) }
const root = () => document.createElement('div')
const project = '01ARZ3NDEKTSV4RRFFQ69G5FAV'
const id = '01ARZ3NDEKTSV4RRFFQ69G5FAW'
const json = (status, body) => new Response(JSON.stringify(body), {
  status, headers: { 'content-type': 'application/json' },
})
const until = async predicate => {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (predicate()) return
    await new Promise(resolve => setTimeout(resolve, 10))
  }
  assert.fail('Expected asynchronous state was not reached')
}

function fixture(t) {
  let owner = 'user-a', token = 'header.payload.signature', version = 'version-1'
  let todo = { userId: owner, title: '<img src=x onerror=alert(1)>', done: false }
  let denial = false, conflict = false, lostWrite = false, endless = false
  let heldQuery
  const calls = [], streams = []
  t.mock.method(globalThis, 'fetch', async (url, options) => {
    const path = new URL(url)
    const headers = new Headers(options.headers)
    const body = options.body === undefined ? undefined : await new Response(options.body).text()
    calls.push({ path, options: { ...options, body }, token: headers.get('authorization') })
    assert.ok(path.pathname.startsWith(`/v1/projects/${project}/databases/demo/`))
    assert.equal(headers.get('idempotency-key'), null)
    if (denial) return json(403, { error: { code: 'forbidden_scope' } })
    if (path.pathname.endsWith('/changes')) {
      assert.equal(path.searchParams.get('collections'), 'todos')
      assert.equal(path.searchParams.get('cursor'), 'now')
      let controller
      const body = new ReadableStream({ start(c) { controller = c } })
      const entry = {
        signal: options.signal,
        emit(text) { controller.enqueue(new TextEncoder().encode(text)) },
      }
      streams.push(entry)
      options.signal.addEventListener('abort', () => controller.error(options.signal.reason), { once: true })
      return new Response(body, { headers: { 'content-type': 'text/event-stream' } })
    }
    if (path.pathname.endsWith('/query')) {
      const query = JSON.parse(body)
      assert.deepEqual(query.filter, { eq: ['/userId', owner] })
      assert.equal(query.limit, 50)
      if (heldQuery) {
        const held = heldQuery
        heldQuery = undefined
        const oldTodo = { ...todo }
        held.started = true
        await held.wait
        return json(200, { items: [{ id, version, state: 'value', document: oldTodo }], next_cursor: null })
      }
      if (endless) return json(200, { items: [], next_cursor: 'next-page' })
      // Prove that an empty cursor page does not terminate the list.
      if (query.cursor === null) return json(200, { items: [], next_cursor: 'next-page' })
      assert.equal(query.cursor, 'next-page')
      return json(200, {
        items: todo ? [{ id, version, state: 'value', document: todo }] : [], next_cursor: null,
      })
    }
    assert.ok(path.pathname.endsWith('/documents') || path.pathname.endsWith('/documents/by-key'))
    if (lostWrite) throw new TypeError('Synthetic lost response')
    if (options.method === 'POST') {
      todo = JSON.parse(body)
      assert.deepEqual(todo, { userId: owner, title: 'New todo', done: false })
      return json(201, { id, version, offset: '1', durability: 'local_durable' })
    }
    assert.equal(path.searchParams.get('id'), id)
    assert.equal(headers.get('if-match'), `"${version}"`)
    if (conflict) return json(412, { error: { code: 'precondition_failed' } })
    if (options.method === 'PATCH') {
      const patch = JSON.parse(body)
      assert.equal(Object.hasOwn(patch, 'userId'), false)
      todo = { ...todo, ...patch }
    } else {
      assert.equal(options.method, 'DELETE')
      todo = null
    }
    version = 'version-2'
    return json(200, { id, version, offset: '2', durability: 'local_durable' })
  })
  const session = () => ({ userId: owner, getAccessToken: async () => token, project, database: 'demo' })
  const node = root()
  let app = mountTodos(node, session())
  t.after(() => app.dispose())
  return {
    node, calls, streams, app,
    setToken(value) { token = value },
    deny() { denial = true },
    conflict() { conflict = true },
    loseWrite() { lostWrite = true },
    endless() { endless = true },
    delayNextQuery() {
      const held = { started: false }
      held.wait = new Promise(resolve => { held.release = resolve })
      heldQuery = held
      return held
    },
    switchAccount() {
      owner = 'user-b'
      todo = { userId: owner, title: 'B only', done: false }
      app = mountTodos(node, session())
      return app
    },
  }
}
const list = f => f.node.children[3]
const status = f => f.node.children[2].textContent
const writes = f => f.calls.filter(call => /\/documents(?:\/by-key)?$/.test(call.path.pathname))

test('safe per-user CRUD uses fresh tokens, opaque versions and never provisions', async t => {
  const f = fixture(t)
  await f.app.ready
  await until(() => f.streams.length === 1)
  assert.equal(list(f).children[0].children[0].value, '<img src=x onerror=alert(1)>')
  f.setToken('newheader.newpayload.newsignature')
  await list(f).children[0].children[1].fire('click')
  assert.equal(JSON.parse(writes(f)[0].options.body).done, true)
  assert.equal(writes(f)[0].token, 'Bearer newheader.newpayload.newsignature')
  const row = list(f).children[0]
  row.children[0].value = 'Edited'
  await row.children[2].fire('click')
  assert.equal(list(f).children[0].children[0].value, 'Edited')
  await list(f).children[0].children[3].fire('click')
  assert.equal(list(f).children.length, 0)
  f.node.children[0].children[0].value = 'New todo'
  await f.node.children[0].fire('submit')
  assert.equal(list(f).children[0].children[0].value, 'New todo')
  assert.equal(writes(f).length, 4)
  f.app.dispose()
  assert.equal(f.streams[0].signal.aborted, true)
  assert.equal(f.node.children.length, 0)
})

test('conflicts and lost write responses send once and require explicit reconciliation', async t => {
  for (const mode of ['conflict', 'loseWrite']) {
    const f = fixture(t)
    await f.app.ready
    f[mode]()
    const count = f.calls.length
    await list(f).children[0].children[1].fire('click')
    assert.equal(f.calls.length, count + 1)
    assert.equal(writes(f).length, 1)
    assert.match(status(f), mode === 'conflict' ? /Refresh before editing/ : /outcome may be unknown/)
    await list(f).children[0].children[1].fire('click')
    assert.equal(writes(f).length, 1, 'A failed write stays blocked until a successful refresh')
    f.app.dispose()
    t.mock.restoreAll()
  }
})

test('a query started before a failed create cannot unblock another submission', async t => {
  const f = fixture(t)
  await f.app.ready
  const held = f.delayNextQuery()
  const oldRead = f.app.refresh()
  await until(() => held.started)
  const form = f.node.children[0]
  form.children[0].value = 'New todo'
  f.loseWrite()
  await form.fire('submit')
  held.release()
  await oldRead
  assert.match(status(f), /outcome may be unknown/)
  assert.equal(form.inert, true)
  assert.equal(form.children[0].value, 'New todo')
  await form.fire('submit')
  assert.equal(writes(f).length, 1, 'A stale read cannot authorize repeating an uncertain create')
  await f.app.refresh()
  assert.equal(form.inert, false, 'Only a subsequent authorized read can unblock writes')
})

test('unauthenticated mount performs no request and clears the prior root', t => {
  const node = root()
  node.append(document.createElement('p'))
  const fetch = t.mock.method(globalThis, 'fetch', () => assert.fail('Anonymous network request'))
  assert.throws(() => mountTodos(node, { userId: '', getAccessToken: undefined }), /authenticated/)
  assert.equal(node.children.length, 0)
  assert.equal(fetch.mock.callCount(), 0)
})

test('read and mutation denials clear prior records and abort the stream', async t => {
  for (const operation of ['read', 'write']) {
    const f = fixture(t)
    await f.app.ready
    await until(() => f.streams.length === 1)
    const row = list(f).children[0]
    f.deny()
    if (operation === 'read') await f.app.refresh()
    else await row.children[1].fire('click')
    assert.equal(f.node.children.length, 1)
    assert.match(f.node.children[0].textContent, /^Access denied/)
    assert.equal(f.streams[0].signal.aborted, true)
    const count = f.calls.length
    await f.app.refresh()
    assert.equal(f.calls.length, count)
    f.app.dispose()
    t.mock.restoreAll()
  }
})

test('SSE rule denial clears the view, including HTTP-200 error frames', async t => {
  const f = fixture(t)
  await f.app.ready
  await until(() => f.streams.length === 1)
  f.streams[0].emit('event: error\ndata: {"error":{"code":"forbidden_scope"}}\n\n')
  await until(() => f.node.children.length === 1)
  assert.match(f.node.children[0].textContent, /^Access denied/)
  assert.equal(f.streams[0].signal.aborted, true)
})

test('rules_changed disposes the view instead of keeping previously authorized data', async t => {
  const f = fixture(t)
  await f.app.ready
  await until(() => f.streams.length === 1)
  f.streams[0].emit('event: error\ndata: {"error":{"code":"rules_changed"}}\n\n')
  await until(() => f.node.children.length === 1)
  assert.match(f.node.children[0].textContent, /^Access denied/)
  assert.equal(f.streams[0].signal.aborted, true)
  assert.equal(writes(f).length, 0)
})

test('expired cursors and reset codes clear first, then query fresh without replaying writes', async t => {
  for (const code of ['cursor_expired', 'reset', 'reset_required']) {
    const f = fixture(t)
    await f.app.ready
    await until(() => f.streams.length === 1)
    const held = f.delayNextQuery()
    f.streams[0].emit(`event: error\ndata: {"error":{"code":"${code}"}}\n\n`)
    await until(() => held.started)
    assert.equal(list(f).children.length, 0, 'Discard old data before the recovery read completes')
    held.release()
    await until(() => status(f).includes('Live updates paused'))
    assert.equal(list(f).children.length, 1)
    assert.equal(writes(f).length, 0)
    assert.equal(f.streams.length, 1, 'Recovery must not open an unbounded reconnect loop')
    f.app.dispose()
    t.mock.restoreAll()
  }
})

test('reset discards a pending older query before starting its fresh query', async t => {
  const f = fixture(t)
  await f.app.ready
  await until(() => f.streams.length === 1)
  const held = f.delayNextQuery()
  const oldRead = f.app.refresh()
  await until(() => held.started)
  f.streams[0].emit('event: error\ndata: {"error":{"code":"cursor_expired"}}\n\n')
  await until(() => list(f).children.length === 0)
  const fresh = f.delayNextQuery()
  held.release()
  await oldRead
  await until(() => fresh.started)
  assert.equal(list(f).children.length, 0, 'The old response must not restore the discarded view')
  fresh.release()
  await until(() => status(f).includes('Live updates paused'))
  assert.equal(list(f).children.length, 1)
})

test('switching accounts clears synchronously; late old disposal cannot clear the new view', async t => {
  const f = fixture(t)
  await f.app.ready
  await until(() => f.streams.length === 1)
  const second = f.switchAccount()
  assert.equal(f.streams[0].signal.aborted, true)
  assert.equal(list(f).children.length, 0)
  await second.ready
  f.app.dispose()
  assert.equal(list(f).children[0].children[0].value, 'B only')
})

test('a delayed response from the old account cannot repopulate the new view', async t => {
  const f = fixture(t)
  await f.app.ready
  const held = f.delayNextQuery()
  const oldRead = f.app.refresh()
  await until(() => held.started)
  const second = f.switchAccount()
  await second.ready
  held.release()
  await oldRead
  assert.equal(list(f).children[0].children[0].value, 'B only')
})

test('change bursts coalesce; pagination has a hard request bound', async t => {
  const f = fixture(t)
  await f.app.ready
  await until(() => f.streams.length === 1)
  const count = f.calls.length
  for (let index = 0; index < 20; index++)
    f.streams[0].emit(`event: cursor\nid: cursor-${index}\ndata: {"cursor":"cursor-${index}"}\n\n`)
  await until(() => f.calls.length === count + 2)
  await new Promise(resolve => setTimeout(resolve, 300))
  assert.equal(f.calls.length, count + 2)
  f.endless()
  const before = f.calls.length
  await f.app.refresh()
  assert.equal(f.calls.length, before + 10)
  assert.equal(list(f).children.length, 0)
  assert.match(status(f), /supports 500 todos/)
})

test('an unchanged snapshot preserves an unsaved title and its input control', async t => {
  const f = fixture(t)
  await f.app.ready
  const row = list(f).children[0]
  const title = row.children[0]
  title.value = 'Unsaved draft'
  await f.app.refresh()
  assert.equal(list(f).children[0], row)
  assert.equal(list(f).children[0].children[0], title)
  assert.equal(title.value, 'Unsaved draft')
  await row.children[2].fire('click')
  assert.equal(JSON.parse(writes(f)[0].options.body).title, 'Unsaved draft')
})
