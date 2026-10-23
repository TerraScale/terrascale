import { createBrowserSession } from './browser-session.mjs'

// Bind cleanup to this exact session, including invalidation during mounting.
export function connectTodos(mount, identity) {
  let session, view, disposed = false
  function dispose() {
    if (disposed) return
    disposed = true
    session?.dispose()
    view?.dispose()
  }
  try {
    session = createBrowserSession({ ...identity, onInvalidated: dispose })
    view = mount(session, dispose)
    if (disposed) view.dispose()
    return { dispose, ready: view.ready }
  } catch (error) {
    dispose()
    throw error
  }
}
