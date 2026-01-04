import { Link } from 'react-router-dom'
import { motion } from 'framer-motion'
import {
  ArrowRight,
  Zap,
  BarChart3,
  Shield,
  Star,
  ChevronRight,
  Database,
  Globe,
  Code2,
  BookOpen
} from 'lucide-react'
import { ScrollReveal, StaggerContainer, StaggerItem, AnimatedCounter } from '@/components/motion'

const Home = () => {
  const features = [
    {
      icon: Zap,
      title: 'Lightning Fast',
      description: 'Blazing fast queries with global edge caching and optimized indexing.',
    },
    {
      icon: BarChart3,
      title: 'Real-Time Analytics',
      description: 'Monitor your data and performance with real-time insights and dashboards.',
    },
    {
      icon: Shield,
      title: 'Enterprise Security',
      description: 'SOC 2 compliant with end-to-end encryption and advanced access controls.',
    },
    {
      icon: Globe,
      title: 'Global Scale',
      description: 'Deploy worldwide with automatic failover and geographic distribution.',
    },
  ]

  const testimonials = [
    {
      name: 'Sam Carter',
      role: 'CTO, ExampleCorp',
      content: 'TerraScale transformed our data infrastructure. The performance gains are incredible.',
      avatar: 'SC',
    },
    {
      name: 'Priya Patel',
      role: 'Founder, Startly',
      content: 'The developer experience is outstanding. We shipped faster than ever before.',
      avatar: 'PP',
    },
    {
      name: 'Liam Nguyen',
      role: 'Product Manager',
      content: 'Reliable, secure, and scales effortlessly. TerraScale is our backbone.',
      avatar: 'LN',
    },
  ]

  const stats = [
    { value: '99.99%', label: 'Uptime SLA' },
    { value: '10ms', label: 'Query Latency' },
    { value: '1M+', label: 'Queries/Second' },
    { value: '20+', label: 'Global Regions' },
  ]

  return (
    <>
      {/* Hero Section */}
      <section className="relative min-h-screen flex items-center justify-center overflow-hidden bg-[#050508]">
        <div className="absolute inset-0 bg-grid-pattern opacity-40" />

        {/* Floating decorative elements */}
        <div className="absolute top-1/4 left-[15%] w-96 h-96 border border-[#1A1A24] ts-rounded-md opacity-20">
          <div className="absolute inset-0 bg-gradient-to-br from-[#2563EB]/5 to-transparent" />
        </div>
        <motion.div
          className="absolute top-1/4 left-[15%] w-96 h-96 border border-[#1A1A24] ts-rounded-md opacity-20"
          animate={{
            y: [0, -10, 0],
          }}
          transition={{ duration: 8, repeat: Infinity, ease: "easeInOut" }}
        />

        <div className="absolute bottom-1/4 right-[15%] w-80 h-80 border border-[#1A1A24] ts-rounded-md opacity-20">
          <div className="absolute inset-0 bg-gradient-to-br from-[#10B981]/5 to-transparent" />
        </div>
        <motion.div
          className="absolute bottom-1/4 right-[15%] w-80 h-80 border border-[#1A1A24] ts-rounded-md opacity-20"
          animate={{
            y: [0, 10, 0],
          }}
          transition={{ duration: 10, repeat: Infinity, ease: "easeInOut" }}
        />

        <div className="relative z-10 max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8 py-20">
          <div className="text-center max-w-4xl mx-auto">
            <ScrollReveal delay={0.1}>
              <motion.div
                className="inline-flex items-center px-4 py-2 ts-glass ts-rounded-sm text-sm font-medium text-[#F8FAFC] mb-8 hover-lift"
                whileHover={{ scale: 1.02 }}
              >
                <motion.span
                  className="w-2 h-2 bg-[#10B981] ts-rounded-sm mr-3 ts-glow-secondary"
                  animate={{ scale: [1, 1.2, 1] }}
                  transition={{ duration: 2, repeat: Infinity }}
                />
                <Database className="h-4 w-4 mr-2 text-[#2563EB]" />
                Database as a Service Platform
              </motion.div>
            </ScrollReveal>

            <ScrollReveal delay={0.2}>
              <h1 className="text-5xl md:text-7xl lg:text-8xl font-bold text-[#F8FAFC] mb-8 tracking-tight">
                <span className="terminal-prefix">///</span>
                Scale Your Database
                <br />
                <span className="ts-text-gradient">Effortlessly</span>
              </h1>
            </ScrollReveal>

            <ScrollReveal delay={0.3}>
              <p className="text-xl md:text-2xl text-[#94A3B8] mb-12 max-w-3xl mx-auto leading-relaxed">
                The most advanced database platform for modern applications. Deploy, scale, and manage your data with enterprise-grade security and performance.
              </p>
            </ScrollReveal>

            <ScrollReveal delay={0.4}>
              <div className="flex flex-col sm:flex-row gap-4 justify-center items-center mb-16">
                <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                  <Link
                    to="/contact"
                    className="ts-btn-primary px-8 py-4 ts-rounded-md text-lg font-bold inline-flex items-center shadow-offset-blue hover-lift"
                  >
                    Start Building Free
                    <ArrowRight className="ml-2 h-5 w-5" />
                  </Link>
                </motion.div>
                <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                  <a
                    href="https://docs.terrascale.tech"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="ts-btn-glass px-8 py-4 ts-rounded-md text-lg font-bold inline-flex items-center shadow-offset hover-lift"
                  >
                    <BookOpen className="mr-2 h-5 w-5" />
                    View Documentation
                  </a>
                </motion.div>
              </div>
            </ScrollReveal>

            <ScrollReveal delay={0.5}>
              <div className="grid grid-cols-2 md:grid-cols-4 gap-6">
                {stats.map((stat, index) => (
                  <motion.div
                    key={index}
                    className="ts-stats-card text-center"
                    whileHover={{ y: -2 }}
                  >
                    <div className="text-2xl md:text-3xl font-bold ts-text-gradient">
                      <AnimatedCounter value={stat.value} />
                    </div>
                    <div className="text-sm text-[#94A3B8] mt-2 terminal-line">
                      {stat.label}
                    </div>
                  </motion.div>
                ))}
              </div>
            </ScrollReveal>
          </div>
        </div>

        {/* Scroll indicator */}
        <motion.div
          className="absolute bottom-8 left-1/2 transform -translate-x-1/2"
          animate={{ y: [0, 8, 0] }}
          transition={{ duration: 2, repeat: Infinity, ease: "easeInOut" }}
        >
          <div className="w-6 h-10 border-2 border-[#1A1A24] ts-rounded-sm flex justify-center">
            <motion.div
              className="w-1 h-3 bg-[#94A3B8]/50 ts-rounded-sm mt-2"
              animate={{ opacity: [0.3, 1, 0.3] }}
              transition={{ duration: 2, repeat: Infinity }}
            />
          </div>
        </motion.div>
      </section>

      {/* Features Section */}
      <section className="py-24 bg-[#0A0A0E]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-5xl font-bold text-[#F8FAFC] mb-6 tracking-tight">
                <span className="terminal-prefix">///</span>
                Why Choose TerraScale
              </h2>
              <p className="text-xl text-[#94A3B8] max-w-3xl mx-auto">
                Built for developers, trusted by enterprises. Experience the next generation of database infrastructure.
              </p>
            </div>
          </ScrollReveal>

          <StaggerContainer className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6">
            {features.map((feature) => (
              <StaggerItem key={feature.title}>
                <motion.div
                  className="ts-glass ts-rounded-lg p-6 h-full transition-colors duration-200 hover:border-[#2563EB] group hover-lift"
                  whileHover={{
                    y: -4,
                  }}
                >
                  <motion.div
                    className="w-14 h-14 bg-[#050508] border-2 border-[#1A1A24] group-hover:border-[#2563EB] ts-rounded-sm flex items-center justify-center mb-6 transition-colors duration-200"
                    whileHover={{ scale: 1.1, rotate: 2 }}
                    transition={{ type: "spring", stiffness: 300 }}
                  >
                    <feature.icon className="h-7 w-7 text-[#2563EB]" />
                  </motion.div>
                  <h3 className="text-xl font-bold text-[#F8FAFC] mb-3">
                    {feature.title}
                  </h3>
                  <p className="text-[#94A3B8] leading-relaxed text-sm">
                    {feature.description}
                  </p>
                </motion.div>
              </StaggerItem>
            ))}
          </StaggerContainer>
        </div>
      </section>

      {/* Code Example Section */}
      <section className="py-24 bg-[#050508]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16 items-center">
            <ScrollReveal direction="left">
              <div>
                <h2 className="text-3xl md:text-4xl font-bold text-[#F8FAFC] mb-6 tracking-tight">
                  <span className="terminal-prefix">///</span>
                  Get started in seconds
                </h2>
                <p className="text-xl text-[#94A3B8] mb-8 leading-relaxed">
                  Simple APIs, powerful features. Deploy your database with a single command and start building immediately.
                </p>
                <div className="flex items-center space-x-4">
                  <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                    <Link
                      to="/contact"
                      className="ts-btn-primary px-6 py-3 ts-rounded-md font-bold inline-flex items-center shadow-offset-blue hover-lift"
                    >
                      Start Free Trial
                      <ArrowRight className="ml-2 h-4 w-4" />
                    </Link>
                  </motion.div>
                  <Link
                    to="/pricing"
                    className="text-[#2563EB] hover:text-[#60A5FA] font-bold inline-flex items-center transition-colors"
                  >
                    View Pricing
                    <ChevronRight className="ml-1 h-4 w-4" />
                  </Link>
                </div>
              </div>
            </ScrollReveal>

            <ScrollReveal direction="right">
              <motion.div
                className="ts-glass-frosted ts-rounded-lg p-6 overflow-hidden shadow-offset"
                whileHover={{
                  y: -4,
                }}
              >
                <div className="flex items-center space-x-2 mb-4 border-b border-[#1A1A24] pb-4">
                  <div className="w-3 h-3 bg-[#F43F5E] ts-rounded-sm" />
                  <div className="w-3 h-3 bg-[#FBBF24] ts-rounded-sm" />
                  <div className="w-3 h-3 bg-[#10B981] ts-rounded-sm" />
                  <div className="flex-1" />
                  <span className="text-xs text-[#475569]">sdk.ts</span>
                  <Code2 className="h-4 w-4 ml-2 text-[#475569]" />
                </div>
                <div className="bg-[#050508] ts-rounded-sm p-4">
                  <pre className="text-sm overflow-x-auto">
                    <code className="text-[#F8FAFC]">{`import { TerraScale } from '@terrascale/sdk'

const db = new TerraScale({
  apiKey: process.env.TERRASCALE_API_KEY,
  region: 'us-west-2'
})

// Create a new document
await db.collection('users').create({
  name: 'John Doe',
  email: 'john@example.com'
})

// Query with real-time filters
const users = await db.collection('users')
  .where('status', '==', 'active')
  .orderBy('created_at')
  .limit(10)
  .get()`}</code>
                  </pre>
                </div>
              </motion.div>
            </ScrollReveal>
          </div>
        </div>
      </section>

      {/* Testimonials Section */}
      <section className="py-24 bg-[#0A0A0E]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-4xl font-bold text-[#F8FAFC] mb-4 tracking-tight">
                <span className="terminal-prefix">///</span>
                Trusted by developers worldwide
              </h2>
              <div className="flex items-center justify-center space-x-1 text-[#FBBF24] mb-2">
                {[...Array(5)].map((_, i) => (
                  <motion.div
                    key={i}
                    initial={{ opacity: 0, scale: 0 }}
                    animate={{ opacity: 1, scale: 1 }}
                    transition={{ delay: i * 0.1 }}
                  >
                    <Star className="h-5 w-5 fill-current" />
                  </motion.div>
                ))}
              </div>
              <p className="text-[#94A3B8] terminal-line">Loved by 10,000+ developers and growing</p>
            </div>
          </ScrollReveal>

          <StaggerContainer className="grid grid-cols-1 md:grid-cols-3 gap-6">
            {testimonials.map((testimonial) => (
              <StaggerItem key={testimonial.name}>
                <motion.div
                  className="ts-glass ts-rounded-lg p-6 h-full hover:border-[#2563EB] transition-colors duration-200 hover-lift"
                  whileHover={{
                    y: -4,
                  }}
                >
                  <div className="flex items-center space-x-4 mb-6">
                    <div className="w-12 h-12 bg-[#2563EB] ts-rounded-sm flex items-center justify-center text-[#050508] font-bold">
                      {testimonial.avatar}
                    </div>
                    <div>
                      <h4 className="font-bold text-[#F8FAFC]">{testimonial.name}</h4>
                      <p className="text-[#94A3B8] text-sm">{testimonial.role}</p>
                    </div>
                  </div>
                  <p className="text-[#F8FAFC] italic leading-relaxed">"{testimonial.content}"</p>
                </motion.div>
              </StaggerItem>
            ))}
          </StaggerContainer>
        </div>
      </section>

      {/* CTA Section */}
      <section className="py-24 bg-[#050508] bg-grid-pattern relative overflow-hidden">
        <ScrollReveal>
          <div className="relative z-10 max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 text-center">
            <h2 className="text-3xl md:text-5xl font-bold text-[#F8FAFC] mb-6 tracking-tight">
              <span className="terminal-prefix">///</span>
              Ready to build the future?
            </h2>
            <p className="text-xl text-[#94A3B8] mb-10 max-w-2xl mx-auto">
              Join thousands of developers already using TerraScale to build scalable, reliable applications.
            </p>
            <div className="flex flex-col sm:flex-row gap-4 justify-center items-center">
              <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                <Link
                  to="/contact"
                  className="ts-btn-primary font-bold py-4 px-8 ts-rounded-md inline-flex items-center shadow-offset-blue hover-lift"
                >
                  Start Building Today
                  <ArrowRight className="ml-2 h-5 w-5" />
                </Link>
              </motion.div>
              <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                <Link
                  to="/pricing"
                  className="ts-btn-glass font-bold py-4 px-8 ts-rounded-md shadow-offset hover-lift"
                >
                  Explore Pricing
                </Link>
              </motion.div>
            </div>
          </div>
        </ScrollReveal>
      </section>
    </>
  )
}

export default Home
