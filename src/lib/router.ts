import { writable } from 'svelte/store'

export interface LocationState {
  pathname: string
  hash: string
}

const normalizePathname = (pathname: string) => {
  if (!pathname || pathname === '/') {
    return '/'
  }

  return pathname.endsWith('/') ? pathname.slice(0, -1) : pathname
}

const readLocation = (): LocationState => {
  if (typeof window === 'undefined') {
    return { pathname: '/', hash: '' }
  }

  return {
    pathname: normalizePathname(window.location.pathname),
    hash: window.location.hash,
  }
}

const locationStore = writable<LocationState>(readLocation())

export const currentLocation = {
  subscribe: locationStore.subscribe,
}

export const isInternalHref = (href: string) =>
  href.startsWith('/') || href.startsWith('#')

export const navigate = (href: string, options?: { replace?: boolean }) => {
  if (typeof window === 'undefined') {
    return
  }

  const nextUrl = new URL(href, window.location.href)

  if (nextUrl.origin !== window.location.origin) {
    window.location.assign(nextUrl.toString())
    return
  }

  const nextPathname = normalizePathname(nextUrl.pathname)
  const target = `${nextPathname}${nextUrl.search}${nextUrl.hash}`
  const method = options?.replace ? 'replaceState' : 'pushState'

  window.history[method]({}, '', target)

  locationStore.set({
    pathname: nextPathname,
    hash: nextUrl.hash,
  })
}

export const startRouter = () => {
  if (typeof window === 'undefined') {
    return () => {}
  }

  const sync = () => {
    locationStore.set(readLocation())
  }

  sync()

  window.addEventListener('popstate', sync)
  window.addEventListener('hashchange', sync)

  return () => {
    window.removeEventListener('popstate', sync)
    window.removeEventListener('hashchange', sync)
  }
}
