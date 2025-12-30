import { Link } from 'react-router-dom'
import { Home, Search, ArrowLeft } from 'lucide-react'
import { motion } from 'framer-motion'
import { ScrollReveal } from '@/components/motion'

const NotFound = () => {
  return (
    <section className="py-24 bg-gradient-to-b from-background to-accent/5 min-h-screen flex items-center">
      <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
        <div className="text-center max-w-2xl mx-auto">
          {/* Animated Illustration */}
          <ScrollReveal>
            <div className="mb-8">
              <motion.div
                animate={{ y: [0, -10, 0] }}
                transition={{ duration: 3, repeat: Infinity, ease: "easeInOut" }}
              >
                <svg
                  className="w-64 h-64 mx-auto text-muted-foreground/30"
                  fill="none"
                  stroke="currentColor"
                  viewBox="0 0 24 24"
                  xmlns="http://www.w3.org/2000/svg"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={1.5}
                    d="M9.172 16.172a4 4 0 015.656 0M9 12h6m-3-4v.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
                  />
                </svg>
              </motion.div>
            </div>
          </ScrollReveal>

          {/* 404 Number */}
          <ScrollReveal delay={0.1}>
            <div className="mb-6">
              <h1 className="text-8xl md:text-9xl font-bold bg-gradient-to-r from-[#4379F2] to-[#6EC207] bg-clip-text text-transparent mb-4">
                404
              </h1>
              <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-4">
                Not Found
              </h2>
            </div>
          </ScrollReveal>

          {/* Message */}
          <ScrollReveal delay={0.2}>
            <p className="text-xl text-muted-foreground mb-2">
              It's gone :(
            </p>
            <p className="text-lg text-muted-foreground mb-8">
              The page you're looking for seems to have vanished into the digital void.
              Don't worry, even the best explorers sometimes take a wrong turn!
            </p>
          </ScrollReveal>

          {/* Action Buttons */}
          <ScrollReveal delay={0.3}>
            <div className="flex flex-col sm:flex-row gap-4 justify-center items-center">
              <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                <Link
                  to="/"
                  className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-6 py-3 rounded-lg font-semibold transition-all duration-200 inline-flex items-center"
                >
                  <Home className="mr-2 h-4 w-4" />
                  Back to Home
                </Link>
              </motion.div>
              <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                <Link
                  to="/contact"
                  className="border border-border hover:bg-accent text-foreground px-6 py-3 rounded-lg font-semibold transition-all duration-200 inline-flex items-center"
                >
                  <Search className="mr-2 h-4 w-4" />
                  Contact Support
                </Link>
              </motion.div>
            </div>
          </ScrollReveal>

          {/* Helpful Links */}
          <ScrollReveal delay={0.4}>
            <div className="mt-12 pt-8 border-t border-border">
              <p className="text-muted-foreground mb-4">Or try one of these popular pages:</p>
              <div className="flex flex-wrap justify-center gap-6 text-sm">
                <Link
                  to="/about"
                  className="text-primary hover:text-primary/80 transition-colors duration-200"
                >
                  About Us
                </Link>
                <Link
                  to="/pricing"
                  className="text-primary hover:text-primary/80 transition-colors duration-200"
                >
                  Pricing
                </Link>
                <Link
                  to="/contact"
                  className="text-primary hover:text-primary/80 transition-colors duration-200"
                >
                  Contact
                </Link>
              </div>
            </div>
          </ScrollReveal>

          {/* Fun Animation */}
          <ScrollReveal delay={0.5}>
            <div className="mt-8">
              <motion.div
                className="inline-flex items-center space-x-2 text-muted-foreground"
                animate={{ x: [-5, 0, -5] }}
                transition={{ duration: 1.5, repeat: Infinity }}
              >
                <ArrowLeft className="h-4 w-4" />
                <span className="text-sm">This way back to safety</span>
              </motion.div>
            </div>
          </ScrollReveal>
        </div>
      </div>
    </section>
  )
}

export default NotFound