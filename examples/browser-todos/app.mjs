import { mountTodos } from './todos.mjs'

// App integration only: call after your own sign-in flow supplies a session.
// This function is not a TerraScale authentication API.
export function showAuthenticatedTodos(session) {
  return mountTodos(document.querySelector('#todos'), session)
}

// Call the returned dispose() before logout, or before changing accounts.
// Mounting again on the same root also disposes the previous session.
