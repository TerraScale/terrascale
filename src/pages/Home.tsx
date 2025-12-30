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
      <section className="relative min-h-screen flex items-center justify-center overflow-hidden">
        {/* Animated Background */}
        <div className="absolute inset-0 bg-gradient-to-br from-background via-background to-accent/20">
          <div className="absolute inset-0 bg-[radial-gradient(circle_at_30%_40%,rgba(67,121,242,0.1),transparent_50%)]" />
          <div className="absolute inset-0 bg-[radial-gradient(circle_at_70%_60%,rgba(110,194,7,0.1),transparent_50%)]" />
          <div className="absolute inset-0 bg-[radial-gradient(circle_at_50%_50%,rgba(67,121,242,0.05),transparent_50%)]" />
        </div>

        {/* Subtle Floating Elements */}
        <motion.div
          className="absolute top-1/4 left-[15%] w-64 h-64 rounded-full bg-gradient-to-r from-[#4379F2]/5 to-transparent blur-3xl"
          animate={{
            y: [0, -20, 0],
            opacity: [0.5, 0.8, 0.5]
          }}
          transition={{ duration: 8, repeat: Infinity, ease: "easeInOut" }}
        />
        <motion.div
          className="absolute bottom-1/4 right-[15%] w-80 h-80 rounded-full bg-gradient-to-l from-[#6EC207]/5 to-transparent blur-3xl"
          animate={{
            y: [0, 20, 0],
            opacity: [0.5, 0.7, 0.5]
          }}
          transition={{ duration: 10, repeat: Infinity, ease: "easeInOut" }}
        />

        {/* Grid pattern overlay */}
        <div className="absolute inset-0 bg-[linear-gradient(rgba(67,121,242,0.03)_1px,transparent_1px),linear-gradient(90deg,rgba(67,121,242,0.03)_1px,transparent_1px)] bg-[size:64px_64px] [mask-image:radial-gradient(ellipse_50%_50%_at_50%_50%,black,transparent)]" />

        <div className="relative z-10 max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8 py-20">
          <div className="text-center max-w-4xl mx-auto">
            {/* Badge */}
            <ScrollReveal delay={0.1}>
              <motion.div
                className="inline-flex items-center px-4 py-2 bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 border border-border rounded-full text-sm font-medium text-foreground mb-8"
                whileHover={{ scale: 1.02 }}
              >
                <motion.span
                  className="w-2 h-2 bg-[#6EC207] rounded-full mr-3"
                  animate={{ scale: [1, 1.2, 1] }}
                  transition={{ duration: 2, repeat: Infinity }}
                />
                <Database className="h-4 w-4 mr-2 text-primary" />
                Database as a Service Platform
              </motion.div>
            </ScrollReveal>

            {/* Main Heading */}
            <ScrollReveal delay={0.2}>
              <h1 className="text-5xl md:text-7xl lg:text-8xl font-bold text-foreground mb-8 tracking-tight">
                Scale Your{' '}
                <span className="bg-gradient-to-r from-[#4379F2] via-[#6EC207] to-[#4379F2] bg-clip-text text-transparent animate-gradient bg-[length:200%_auto]">
                  Database
                </span>
                <br />
                Effortlessly
              </h1>
            </ScrollReveal>

            {/* Subtitle */}
            <ScrollReveal delay={0.3}>
              <p className="text-xl md:text-2xl text-muted-foreground mb-12 max-w-3xl mx-auto leading-relaxed">
                The most advanced database platform for modern applications. Deploy, scale, and manage your data with enterprise-grade security and performance.
              </p>
            </ScrollReveal>

            {/* CTA Buttons */}
            <ScrollReveal delay={0.4}>
              <div className="flex flex-col sm:flex-row gap-4 justify-center items-center mb-16">
                <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                  <Link
                    to="/contact"
                    className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-8 py-4 rounded-xl text-lg font-semibold transition-all duration-300 inline-flex items-center shadow-lg hover:shadow-xl"
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
                    className="border border-border hover:bg-accent text-foreground px-8 py-4 rounded-xl text-lg font-semibold transition-all duration-300 inline-flex items-center"
                  >
                    <BookOpen className="mr-2 h-5 w-5" />
                    View Documentation
                  </a>
                </motion.div>
              </div>
            </ScrollReveal>

            {/* Stats */}
            <ScrollReveal delay={0.5}>
              <div className="grid grid-cols-2 md:grid-cols-4 gap-8">
                {stats.map((stat, index) => (
                  <motion.div
                    key={index}
                    className="text-center"
                    whileHover={{ y: -2 }}
                  >
                    <div className="text-2xl md:text-3xl font-bold text-foreground">
                      <AnimatedCounter value={stat.value} />
                    </div>
                    <div className="text-sm text-muted-foreground">{stat.label}</div>
                  </motion.div>
                ))}
              </div>
            </ScrollReveal>
          </div>
        </div>

        {/* Scroll Indicator */}
        <motion.div
          className="absolute bottom-8 left-1/2 transform -translate-x-1/2"
          animate={{ y: [0, 8, 0] }}
          transition={{ duration: 2, repeat: Infinity, ease: "easeInOut" }}
        >
          <div className="w-6 h-10 border-2 border-border rounded-full flex justify-center">
            <motion.div
              className="w-1 h-3 bg-foreground/50 rounded-full mt-2"
              animate={{ opacity: [0.3, 1, 0.3] }}
              transition={{ duration: 2, repeat: Infinity }}
            />
          </div>
        </motion.div>
      </section>

      {/* Features Grid */}
      <section className="py-24 bg-gradient-to-b from-background to-accent/10">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-5xl font-bold text-foreground mb-6 tracking-tight">
                Why Choose <span className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] bg-clip-text text-transparent">TerraScale</span>
              </h2>
              <p className="text-xl text-muted-foreground max-w-3xl mx-auto">
                Built for developers, trusted by enterprises. Experience the next generation of database infrastructure.
              </p>
            </div>
          </ScrollReveal>

          <StaggerContainer className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-8">
            {features.map((feature) => (
              <StaggerItem key={feature.title}>
                <motion.div
                  className="bg-card border border-border p-8 rounded-2xl h-full transition-colors duration-300 group"
                  whileHover={{
                    y: -8,
                    boxShadow: "0 20px 40px -12px rgba(67, 121, 242, 0.15)"
                  }}
                >
                  <motion.div
                    className="w-14 h-14 bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 rounded-xl flex items-center justify-center mb-6"
                    whileHover={{ scale: 1.1, rotate: 5 }}
                    transition={{ type: "spring", stiffness: 300 }}
                  >
                    <feature.icon className="h-7 w-7 text-primary" />
                  </motion.div>
                  <h3 className="text-xl font-bold text-foreground mb-3">
                    {feature.title}
                  </h3>
                  <p className="text-muted-foreground leading-relaxed">
                    {feature.description}
                  </p>
                </motion.div>
              </StaggerItem>
            ))}
          </StaggerContainer>
        </div>
      </section>

      {/* Code Example Section */}
      <section className="py-24">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16 items-center">
            <ScrollReveal direction="left">
              <div>
                <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-6 tracking-tight">
                  Get started in <span className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] bg-clip-text text-transparent">seconds</span>
                </h2>
                <p className="text-xl text-muted-foreground mb-8 leading-relaxed">
                  Simple APIs, powerful features. Deploy your database with a single command and start building immediately.
                </p>
                <div className="flex items-center space-x-4">
                  <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                    <Link
                      to="/contact"
                      className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-6 py-3 rounded-lg font-semibold transition-all duration-300 inline-flex items-center"
                    >
                      Start Free Trial
                      <ArrowRight className="ml-2 h-4 w-4" />
                    </Link>
                  </motion.div>
                  <Link
                    to="/pricing"
                    className="text-primary hover:text-primary/80 font-semibold inline-flex items-center transition-colors"
                  >
                    View Pricing
                    <ChevronRight className="ml-1 h-4 w-4" />
                  </Link>
                </div>
              </div>
            </ScrollReveal>

            <ScrollReveal direction="right">
              <motion.div
                className="bg-card border border-border rounded-2xl p-6 overflow-hidden"
                whileHover={{
                  boxShadow: "0 20px 40px -12px rgba(67, 121, 242, 0.12)"
                }}
              >
                <div className="flex items-center space-x-2 mb-4">
                  <div className="w-3 h-3 bg-red-500 rounded-full" />
                  <div className="w-3 h-3 bg-yellow-500 rounded-full" />
                  <div className="w-3 h-3 bg-green-500 rounded-full" />
                  <Code2 className="h-4 w-4 ml-auto text-muted-foreground" />
                </div>
                <pre className="text-sm text-foreground overflow-x-auto">
                  <code>{`import { TerraScale } from '@terrascale/sdk'

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
              </motion.div>
            </ScrollReveal>
          </div>
        </div>
      </section>

      {/* Testimonials Section */}
      <section className="py-24 bg-gradient-to-r from-accent/5 to-accent/10">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-4 tracking-tight">
                Trusted by developers worldwide
              </h2>
              <div className="flex items-center justify-center space-x-1 text-yellow-500 mb-2">
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
              <p className="text-muted-foreground">Loved by 10,000+ developers and growing</p>
            </div>
          </ScrollReveal>

          <StaggerContainer className="grid grid-cols-1 md:grid-cols-3 gap-8">
            {testimonials.map((testimonial) => (
              <StaggerItem key={testimonial.name}>
                <motion.div
                  className="bg-card border border-border p-8 rounded-2xl h-full"
                  whileHover={{
                    y: -4,
                    boxShadow: "0 16px 32px -8px rgba(67, 121, 242, 0.1)"
                  }}
                >
                  <div className="flex items-center space-x-4 mb-6">
                    <div className="w-12 h-12 bg-gradient-to-r from-[#4379F2] to-[#6EC207] rounded-full flex items-center justify-center text-white font-bold">
                      {testimonial.avatar}
                    </div>
                    <div>
                      <h4 className="font-bold text-foreground">{testimonial.name}</h4>
                      <p className="text-muted-foreground text-sm">{testimonial.role}</p>
                    </div>
                  </div>
                  <p className="text-foreground italic leading-relaxed">"{testimonial.content}"</p>
                </motion.div>
              </StaggerItem>
            ))}
          </StaggerContainer>
        </div>
      </section>

      {/* Bottom CTA Section */}
      <section className="py-24 bg-gradient-to-r from-[#4379F2] to-[#6EC207] relative overflow-hidden">
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_30%_40%,rgba(255,255,255,0.1),transparent_50%)]" />
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_70%_60%,rgba(255,255,255,0.05),transparent_50%)]" />

        <ScrollReveal>
          <div className="relative z-10 max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 text-center">
            <h2 className="text-3xl md:text-5xl font-bold text-white mb-6 tracking-tight">
              Ready to build the future?
            </h2>
            <p className="text-xl text-white/80 mb-10 max-w-2xl mx-auto">
              Join thousands of developers already using TerraScale to build scalable, reliable applications.
            </p>
            <div className="flex flex-col sm:flex-row gap-4 justify-center items-center">
              <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                <Link
                  to="/contact"
                  className="bg-white text-[#4379F2] hover:bg-gray-50 font-bold py-4 px-8 rounded-xl transition-all duration-300 inline-flex items-center shadow-lg"
                >
                  Start Building Today
                  <ArrowRight className="ml-2 h-5 w-5" />
                </Link>
              </motion.div>
              <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                <Link
                  to="/pricing"
                  className="border-2 border-white text-white hover:bg-white hover:text-[#4379F2] font-bold py-4 px-8 rounded-xl transition-all duration-300"
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
