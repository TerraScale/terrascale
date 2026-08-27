<script lang="ts">
  let className = ''

  export { className as class }

  export let href = ''
  export let target: '_blank' | '_self' | '_parent' | '_top' | null | undefined = undefined
</script>

<a href={href} target={target} rel="external noopener noreferrer" class={className} {...$$restProps}>
  <slot />
</a>
