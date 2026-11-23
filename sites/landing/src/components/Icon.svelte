<script lang="ts">
  import { resolveIconPath, type AppIconName } from '@/lib/icons'

  export let name: AppIconName
  export let className = ''
  export let label: string | undefined = undefined

  $: iconUrl = `url("${resolveIconPath(name)}")`
</script>

<span
  class={`ts-icon ${className}`.trim()}
  role={label ? 'img' : undefined}
  aria-label={label}
  aria-hidden={label ? undefined : true}
  style:--ts-icon-url={iconUrl}
>
</span>

<style>
  .ts-icon {
    display: inline-block;
    flex-shrink: 0;
    background-color: currentColor;
    -webkit-mask: var(--ts-icon-url) center / contain no-repeat;
    mask: var(--ts-icon-url) center / contain no-repeat;
  }
</style>
