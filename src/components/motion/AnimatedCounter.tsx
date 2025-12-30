import { useInView, useReducedMotion, motion, useSpring, useTransform, MotionValue } from 'framer-motion'
import { useRef, useEffect } from 'react'

interface AnimatedCounterProps {
  value: string
  className?: string
  duration?: number
}

function AnimatedNumber({ value, className }: { value: MotionValue<string>, className?: string }) {
  return (
    <motion.span className={className}>
      {value}
    </motion.span>
  )
}

export const AnimatedCounter = ({
  value,
  className,
  duration = 1.5,
}: AnimatedCounterProps) => {
  const ref = useRef<HTMLSpanElement>(null)
  const isInView = useInView(ref, { once: true, margin: '-50px' })
  const prefersReducedMotion = useReducedMotion()

  // Parse the value to extract numeric part and suffix
  const numericMatch = value.match(/^([<>]?)(\d+(?:\.\d+)?)(.*?)$/)
  const prefix = numericMatch?.[1] || ''
  const numericValue = numericMatch ? parseFloat(numericMatch[2]) : 0
  const suffix = numericMatch?.[3] || ''
  const hasDecimal = value.includes('.')

  const spring = useSpring(0, {
    duration: duration * 1000,
    bounce: 0,
  })

  const display = useTransform(spring, (current) => {
    if (hasDecimal) {
      return `${prefix}${current.toFixed(2)}${suffix}`
    }
    return `${prefix}${Math.round(current)}${suffix}`
  })

  useEffect(() => {
    if (isInView && !prefersReducedMotion) {
      spring.set(numericValue)
    }
  }, [isInView, numericValue, spring, prefersReducedMotion])

  if (prefersReducedMotion) {
    return <span className={className}>{value}</span>
  }

  return (
    <span ref={ref} className={className}>
      <AnimatedNumber value={display} />
    </span>
  )
}

export default AnimatedCounter
