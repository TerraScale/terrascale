<script lang="ts">
  import { resolve } from '$app/paths'
  import { page } from '$app/state'
  import type { RouteId } from '$app/types'
  import type { Snippet } from 'svelte'
  import type { HTMLAnchorAttributes } from 'svelte/elements'

  type Props = Omit<HTMLAnchorAttributes, 'href'> & {
    href?: string
    activeClass?: string
    inactiveClass?: string
    match?: 'exact' | 'prefix'
    afterClick?: () => void
    children?: Snippet
  }

  let {
    class: className = '', href = '', activeClass = '', inactiveClass = '',
    match = 'exact', afterClick, children, ...rest
  }: Props = $props()

  const toResolvedHref = (value: string) => {
    if (value.startsWith('#')) return value
    const [pathname, hash = ''] = value.split('#')
    const resolvedPath = resolve((pathname || '/') as RouteId)
    return hash ? `${resolvedPath}#${hash}` : resolvedPath
  }

  const resolvedHref = $derived(toResolvedHref(href))
  const targetPath = $derived(href.startsWith('#') ? page.url.pathname : href.split('#')[0] || '/')
  const normalizedTargetPath = $derived(
    targetPath.length > 1 && targetPath.endsWith('/') ? targetPath.slice(0, -1) : targetPath,
  )
  const isActive = $derived(
    match === 'prefix'
      ? normalizedTargetPath === '/'
        ? page.url.pathname === '/'
        : page.url.pathname.startsWith(normalizedTargetPath)
      : page.url.pathname === normalizedTargetPath,
  )
</script>

<a
  href={resolvedHref}
  class={`${className} ${isActive ? activeClass : inactiveClass}`.trim()}
  onclick={() => afterClick?.()}
  {...rest}
>
  {@render children?.()}
</a>
