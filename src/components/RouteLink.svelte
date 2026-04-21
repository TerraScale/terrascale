<script lang="ts">
  import { resolve } from '$app/paths'
  import { page } from '$app/stores'

  let className = ''

  export { className as class }

  export let href = ''
  export let activeClass = ''
  export let inactiveClass = ''
  export let match: 'exact' | 'prefix' = 'exact'
  export let afterClick: (() => void) | undefined = undefined

  const isInternalHref = (value: string) => value.startsWith('/') || value.startsWith('#')
  const toResolvedHref = (value: string) => {
    if (value.startsWith('#')) {
      return value
    }

    const [pathname, hash = ''] = value.split('#')
    const resolvedPath = resolve((pathname || '/') as Parameters<typeof resolve>[0])
    return hash ? `${resolvedPath}#${hash}` : resolvedPath
  }

  $: internal = isInternalHref(href)
  $: resolvedHref = internal ? toResolvedHref(href) : href
  $: targetPath = href.startsWith('#') ? $page.url.pathname : href.split('#')[0] || '/'
  $: normalizedTargetPath =
    targetPath.length > 1 && targetPath.endsWith('/') ? targetPath.slice(0, -1) : targetPath
  $: isActive =
    internal &&
    (match === 'prefix'
      ? normalizedTargetPath === '/'
        ? $page.url.pathname === '/'
        : $page.url.pathname.startsWith(normalizedTargetPath)
      : $page.url.pathname === normalizedTargetPath)

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
