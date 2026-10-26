import { TerraBaseV1Error } from '@terrascale/sdk'

function sleep(ms, signal) {
  signal.throwIfAborted()
  return new Promise((resolve, reject) => {
    const abort = () => { clearTimeout(timer); reject(signal.reason) }
    const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve() }, ms)
    signal.addEventListener('abort', abort, { once: true })
  })
}

// db uses the current-token provider; every reconnect resolves it again.
// onChange applies idempotently; onCheckpoint durably saves only applied positions.
// onReset clears and fully re-queries the view. No writes are replayed here.
export async function followChanges({ db, collections = ['posts'], lastEventId,
  onChange, onReset, onCheckpoint = () => {}, signal }) {
  signal.throwIfAborted()
  const filter = [...collections]
  let checkpoint = lastEventId
  let delay = 1000
  while (!signal.aborted) {
    let applying = false
    try {
      for await (const event of db.changes({ cursor: 'now', collections: filter,
        lastEventId: checkpoint }, { signal })) {
        applying = true
        if (event.type === 'change') await onChange(event)
        if (event.cursor !== undefined) {
          await onCheckpoint(event.cursor)
          checkpoint = event.cursor
        }
        applying = false
        delay = 1000
      }
      return
    } catch (error) {
      if (applying || signal.aborted || !(error instanceof TerraBaseV1Error)) throw error
      if (error.code === 'cursor_expired' && [200, 410].includes(error.status)) {
        // Capture a position BEFORE rebuilding, so concurrent writes are replayed.
        const fresh = db.changes({ cursor: 'now', collections: filter }, { signal })
        let start
        try {
          const first = await fresh.next()
          if (first.done || first.value.type !== 'cursor') throw new Error('Missing initial changes cursor.')
          start = first.value.cursor
        } finally {
          await fresh.return()
        }
        await onReset()
        await onCheckpoint(start)
        checkpoint = start
        continue
      }
      // Retry only documented safe read overload responses. Auth, malformed data,
      // lost connections and all other terminal errors reach the application.
      if (error.status !== 429 && error.status !== 503) throw error
      await sleep(Math.max(delay, (error.retryAfter ?? 0) * 1000), signal)
      delay = Math.min(delay * 2, 30_000)
    }
  }
  signal.throwIfAborted()
}
