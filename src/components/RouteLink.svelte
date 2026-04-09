<script lang="ts">
  import { currentLocation, isInternalHref, navigate } from '@/lib/router'

  let className = ''

  export { className as class }

  export let href = ''
  export let activeClass = ''
  export let inactiveClass = ''
  export let match: 'exact' | 'prefix' = 'exact'
  export let afterClick: (() => void) | undefined = undefined

  const isModifiedEvent = (event: MouseEvent) =>
    event.metaKey ||
    event.ctrlKey ||
    event.shiftKey ||
    event.altKey ||
    event.button !== 0

  $: internal = isInternalHref(href)
  $: targetPath = href.startsWith('#') ? $currentLocation.pathname : href.split('#')[0] || '/'
  $: normalizedTargetPath =
    targetPath.length > 1 && targetPath.endsWith('/') ? targetPath.slice(0, -1) : targetPath
  $: isActive =
    internal &&
    (match === 'prefix'
      ? normalizedTargetPath === '/'
        ? $currentLocation.pathname === '/'
        : $currentLocation.pathname.startsWith(normalizedTargetPath)
      : $currentLocation.pathname === normalizedTargetPath)

  function handleClick(event: MouseEvent) {
    afterClick?.()

    if (!internal || isModifiedEvent(event) || event.defaultPrevented) {
      return
    }

    const anchor = event.currentTarget as HTMLAnchorElement

    if (anchor.target && anchor.target !== '_self') {
      return
    }

    event.preventDefault()
    navigate(href)
  }
</script>

<a
  href={href}
  class={`${className} ${isActive ? activeClass : inactiveClass}`.trim()}
  on:click={handleClick}
  {...$$restProps}
>
  <slot />
</a>
