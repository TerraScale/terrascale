<script lang="ts">
  import { onMount } from 'svelte'
  import { resolveIconPath, type AppIconName } from '@/lib/icons'

  export let name: AppIconName
  export let className = ''
  export let label: string | undefined = undefined

  const iconCache: Record<string, Promise<string> | undefined> = {}

  const loadIconMarkup = (path: string) => {
    if (!iconCache[path]) {
      iconCache[path] =
        fetch(path)
          .then(async (response) => {
            if (!response.ok) {
              throw new Error(`Failed to load icon at ${path}: ${response.status}`)
            }

            return response.text()
          })
          .catch((error) => {
            if (import.meta.env.DEV) {
              console.warn(`[Icon] ${error instanceof Error ? error.message : String(error)}`)
            }

            return ''
          })
    }

    return iconCache[path]!
  }

  let iconMarkup = ''
  let isMounted = false
  let loadVersion = 0

  const syncIcon = async () => {
    const currentVersion = ++loadVersion
    const path = resolveIconPath(name)
    const markup = await loadIconMarkup(path)

    if (currentVersion === loadVersion) {
      iconMarkup = markup
    }
  }

  onMount(() => {
    isMounted = true
    void syncIcon()
  })

  $: if (isMounted) {
    void syncIcon()
  }
</script>

<span
  class={`ts-icon ${className}`.trim()}
  role={label ? 'img' : undefined}
  aria-label={label}
  aria-hidden={label ? undefined : true}
>
  <!-- eslint-disable-next-line svelte/no-at-html-tags -->
  {@html iconMarkup}
</span>

<style>
  .ts-icon {
    display: inline-block;
    flex-shrink: 0;
    line-height: 0;
  }

  .ts-icon :global(svg) {
    display: block;
    height: 100%;
    width: 100%;
  }
</style>
