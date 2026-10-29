import { createTerraBaseClient } from '@terrascale/sdk'

const mounted = new WeakMap()

/**
 * App integration, not a sign-in API. getAccessToken returns a current end-user
 * access JWT (string or Promise<string>); it must never return a project key.
 */
export function mountTodos(root, { userId, getAccessToken, project, database }) {
  mounted.get(root)?.()
  root.replaceChildren()
  if (typeof userId !== 'string' || !userId || typeof getAccessToken !== 'function')
    throw new TypeError('An authenticated end-user session is required.')

  const lifetime = new AbortController()
  const db = createTerraBaseClient({
    project, baseUrl: 'https://api.terrascale.tech', token: () => getAccessToken(),
  }).database(database)
  const todos = db.collection('todos')
  let stopped = false, busy = false, writeBlocked = false, reading, timer, stream
  let refreshAgain = false
  let readEpoch = 0
  let renderedVersions
  const element = (tag, text = '') => {
    const node = root.ownerDocument.createElement(tag)
    node.textContent = text
    return node
  }
  const form = element('form')
  const input = element('input')
  input.required = true
  input.maxLength = 200
  input.setAttribute('aria-label', 'New todo')
  const add = element('button', 'Add todo')
  add.type = 'submit'
  const reload = element('button', 'Refresh and reconnect')
  reload.type = 'button'
  const status = element('p')
  status.setAttribute('role', 'status')
  const list = element('ul')
  form.append(input, add)
  root.append(form, reload, status, list)

  const requestSignal = () => AbortSignal.any([lifetime.signal, AbortSignal.timeout(10_000)])
  function dispose() {
    if (stopped) return
    stopped = true
    lifetime.abort()
    clearTimeout(timer)
    void stream?.return().catch(() => {})
    renderedVersions = undefined
    root.replaceChildren()
    if (mounted.get(root) === dispose) mounted.delete(root)
  }
  mounted.set(root, dispose)

  function failure(error, mutation = false) {
    if (stopped) return
    if (denied(error)) {
      dispose()
      root.append(element('p', 'Access denied. Sign in again or contact the app owner.'))
      return
    }
    status.textContent = error.status === 412
      ? 'Todo changed elsewhere. Refresh before editing again.'
      : mutation
        ? 'Write outcome may be unknown. Refresh and inspect before repeating it.'
        : 'Refresh failed. Use Refresh and reconnect to try reading again.'
  }

  function denied(error) {
    return error.status === 401 || error.status === 403 ||
      ['unauthorized', 'forbidden', 'forbidden_scope', 'access_denied', 'rules_changed'].includes(error.code)
  }

  async function mutate(write) {
    if (stopped || busy || writeBlocked) return
    busy = true
    form.inert = true
    list.inert = true
    try {
      await write(requestSignal()) // Send once; never retry a mutation.
      if (!stopped) {
        input.value = ''
        await refresh()
      }
    } catch (error) {
      readEpoch++
      writeBlocked = true
      failure(error, true)
    }
    finally {
      busy = false
      form.inert = writeBlocked
      list.inert = writeBlocked
    }
  }

  function render(items) {
    // Producer rules enforce ownership; this also checks every returned snapshot.
    // An unchanged cursor refresh must preserve controls, focus and drafts.
    for (const { document: todo } of items)
      if (todo.userId !== userId) throw new Error('Unexpected owner')
    const versions = JSON.stringify(items.map(({ id, version }) => [id, version]))
    if (versions === renderedVersions && !writeBlocked) return
    renderedVersions = versions
    list.replaceChildren()
    for (const { id, version, document: todo } of items) {
      const row = element('li')
      const title = element('input')
      title.value = todo.title
      title.maxLength = 200
      title.setAttribute('aria-label', 'Todo title')
      const done = element('button', todo.done ? 'Mark incomplete' : 'Complete')
      const save = element('button', 'Save title')
      const remove = element('button', 'Delete')
      done.addEventListener('click', () => mutate(signal => todos.patch(id,
        { merge: { done: !todo.done } }, { ifMatch: version, signal })))
      save.addEventListener('click', () => {
        const value = title.value.trim()
        if (value && value.length <= 200) return mutate(signal => todos.patch(id,
          { merge: { title: value } }, { ifMatch: version, signal }))
      })
      remove.addEventListener('click', () => mutate(signal => todos.delete(id, { ifMatch: version, signal })))
      row.append(title, done, save, remove)
      list.append(row)
    }
    status.textContent = `${items.length} todo(s).`
    writeBlocked = false
    if (!busy) { form.inert = false; list.inert = false }
  }

  function scheduleRefresh() {
    if (stopped || timer) return
    timer = setTimeout(() => { timer = undefined; void refresh() }, 250)
  }

  function refresh() {
    if (stopped) return Promise.resolve()
    if (reading) { refreshAgain = true; return reading }
    const epoch = readEpoch
    reading = (async () => {
      const items = []
      let cursor = null
      const signal = requestSignal()
      try {
        // shortcut: cap the demo at 500 todos; add paginated UI for larger lists.
        for (let page = 0; page < 10; page++) {
          const result = await todos.query({
            filter: { eq: ['/userId', userId] }, limit: 50, cursor,
          }, { signal })
          if (stopped || epoch !== readEpoch) return
          items.push(...result.items)
          cursor = result.nextCursor
          if (cursor === null) break
        }
        if (cursor !== null) {
          renderedVersions = undefined
          list.replaceChildren()
          status.textContent = 'This example supports 500 todos. Add pagination for larger lists.'
          return
        }
        render(items)
      } catch (error) {
        if (denied(error)) failure(error)
        else if (!stopped && epoch === readEpoch) {
          renderedVersions = undefined
          list.replaceChildren()
          failure(error)
        }
      }
    })().finally(() => {
      reading = undefined
      if (refreshAgain) { refreshAgain = false; scheduleRefresh() }
    })
    return reading
  }

  async function watch() {
    if (stopped || stream) return
    // A finite stream lifetime renews the token on the next user-initiated read.
    const signal = AbortSignal.any([lifetime.signal, AbortSignal.timeout(60_000)])
    stream = db.changes({ collections: ['todos'], cursor: 'now' }, { signal })
    try {
      for await (const event of stream) {
        // Cursor refresh also closes the snapshot/stream startup race.
        if (event.type === 'change' || event.type === 'cursor') scheduleRefresh()
      }
    } catch (error) {
      if (denied(error)) failure(error)
      else if (!stopped && ['cursor_expired', 'reset', 'reset_required'].includes(error.code)) {
        readEpoch++
        renderedVersions = undefined
        list.replaceChildren()
        await reading
        await refresh()
        if (!stopped) status.textContent += ' Live updates paused. Use Refresh and reconnect.'
      }
      else if (!stopped) status.textContent = 'Live updates paused. Use Refresh and reconnect.'
    } finally { stream = undefined }
  }

  form.addEventListener('submit', event => {
    event.preventDefault()
    const title = input.value.trim()
    if (title && title.length <= 200)
      return mutate(signal => todos.create({ userId, title, done: false }, { signal }))
  })
  reload.addEventListener('click', () => { void refresh(); void watch() })
  const ready = refresh()
  void watch()
  return { dispose, refresh, ready }
}
