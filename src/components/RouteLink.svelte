<script lang="ts">
  import { resolve } from '$app/paths'
  import { page } from '$app/stores'
  import type { ResolvedPathname } from '$app/types'

  let className = ''

  export { className as class }

  export let href = ''
  export let activeClass = ''
  export let inactiveClass = ''
  export let match: 'exact' | 'prefix' = 'exact'
  export let afterClick: (() => void) | undefined = undefined

  const toResolvedHref = (value: string): ResolvedPathname => {
    if (value.startsWith('#')) {
      return value as ResolvedPathname
    }

    const [pathname, hash = ''] = value.split('#')
    const resolvedPath = resolve((pathname || '/') as Parameters<typeof resolve>[0])
    return (hash ? `${resolvedPath}#${hash}` : resolvedPath) as ResolvedPathname
  }

  $: resolvedHref = toResolvedHref(href)
  $: targetPath = href.startsWith('#') ? $page.url.pathname : href.split('#')[0] || '/'
  $: normalizedTargetPath =
    targetPath.length > 1 && targetPath.endsWith('/') ? targetPath.slice(0, -1) : targetPath
  $: isActive =
    match === 'prefix'
      ? normalizedTargetPath === '/'
        ? $page.url.pathname === '/'
        : $page.url.pathname.startsWith(normalizedTargetPath)
      : $page.url.pathname === normalizedTargetPath

  function handleClick() {
    afterClick?.()
  }
</script>

<a
  href={resolvedHref}
  class={`${className} ${isActive ? activeClass : inactiveClass}`.trim()}
  on:click={handleClick}
  {...$$restProps}
>
  <slot />
</a>
