// Scroll reveal: a section below the fold rises into place once as it enters the viewport.
// The prerendered HTML stays fully visible; the hidden start state is only applied in the
// browser, and never under reduced motion or for content already on screen.
export function reveal(node: HTMLElement) {
  if (
    typeof IntersectionObserver === 'undefined' ||
    window.matchMedia('(prefers-reduced-motion: reduce)').matches ||
    node.getBoundingClientRect().top < window.innerHeight
  ) {
    return {}
  }

  node.classList.add('ts-reveal')
  const observer = new IntersectionObserver(
    (entries) => {
      if (!entries.some((entry) => entry.isIntersecting)) return
      node.classList.add('is-visible')
      observer.disconnect()
    },
    { rootMargin: '0px 0px -12% 0px' },
  )
  observer.observe(node)

  return {
    destroy() {
      observer.disconnect()
    },
  }
}
