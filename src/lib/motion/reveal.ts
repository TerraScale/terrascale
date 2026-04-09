export type RevealDirection = 'up' | 'down' | 'left' | 'right' | 'none'

export interface RevealOptions {
  direction?: RevealDirection
  delay?: number
  duration?: number
  once?: boolean
  margin?: string
}

const directionClasses: Record<RevealDirection, string> = {
  up: 'ts-reveal-up',
  down: 'ts-reveal-down',
  left: 'ts-reveal-left',
  right: 'ts-reveal-right',
  none: 'ts-reveal-none',
}

const cleanupDirectionClasses = (node: HTMLElement) => {
  for (const className of Object.values(directionClasses)) {
    node.classList.remove(className)
  }
}

export const reveal = (node: HTMLElement, options: RevealOptions = {}) => {
  let observer: IntersectionObserver | null = null
  let currentOptions = options

  const apply = () => {
    const resolved = {
      direction: currentOptions.direction ?? 'up',
      delay: currentOptions.delay ?? 0,
      duration: currentOptions.duration ?? 0.38,
      once: currentOptions.once ?? true,
      margin: currentOptions.margin ?? '0px 0px 120px 0px',
    }

    cleanupDirectionClasses(node)
    node.classList.add('ts-reveal', directionClasses[resolved.direction])
    node.style.setProperty('--ts-reveal-delay', `${resolved.delay}s`)
    node.style.setProperty('--ts-reveal-duration', `${resolved.duration}s`)

    observer?.disconnect()
    observer = null

    if (
      typeof window === 'undefined' ||
      window.matchMedia('(prefers-reduced-motion: reduce)').matches ||
      typeof IntersectionObserver === 'undefined'
    ) {
      node.classList.add('is-visible')
      return
    }

    observer = new IntersectionObserver(
      ([entry]) => {
        if (!entry) {
          return
        }

        if (entry.isIntersecting) {
          node.classList.add('is-visible')

          if (resolved.once) {
            observer?.disconnect()
            observer = null
          }
        } else if (!resolved.once) {
          node.classList.remove('is-visible')
        }
      },
      { rootMargin: resolved.margin },
    )

    observer.observe(node)
  }

  apply()

  return {
    update(nextOptions: RevealOptions) {
      currentOptions = nextOptions
      apply()
    },
    destroy() {
      observer?.disconnect()
      cleanupDirectionClasses(node)
      node.classList.remove('ts-reveal', 'is-visible')
    },
  }
}
