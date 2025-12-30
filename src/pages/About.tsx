import { Users, Target, Shield, Award, BookOpen, MessageSquare, Mail } from 'lucide-react'
import { Link } from 'react-router-dom'
import { motion } from 'framer-motion'
import { ScrollReveal, StaggerContainer, StaggerItem, AnimatedCounter } from '@/components/motion'

const About = () => {
  const values = [
    {
      icon: Target,
      title: 'Transparency',
      description: 'We believe in open communication and honest relationships with our users.',
    },
    {
      icon: Users,
      title: 'Customer-Centricity',
      description: 'Every decision we make is guided by what\'s best for our customers.',
    },
    {
      icon: Shield,
      title: 'Innovation',
      description: 'We continuously push boundaries to deliver cutting-edge solutions.',
    },
    {
      icon: Award,
      title: 'Continuous Improvement',
      description: 'We never stop learning, growing, and improving our platform.',
    },
  ]

  const stats = [
    { value: '10000+', label: 'Active Developers' },
    { value: '99.99%', label: 'Uptime SLA' },
    { value: '24/7', label: 'Support', noCounter: true },
    { value: '20+', label: 'Global Regions' },
  ]

  return (
    <>
      {/* Hero Section */}
      <section className="py-24 bg-gradient-to-br from-background via-accent/10 to-background">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center max-w-4xl mx-auto">
            <ScrollReveal>
              <h1 className="text-4xl md:text-6xl font-bold text-foreground mb-6 tracking-tight">
                About <span className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] bg-clip-text text-transparent">TerraScale</span>
              </h1>
            </ScrollReveal>
            <ScrollReveal delay={0.1}>
              <p className="text-xl md:text-2xl text-muted-foreground leading-relaxed">
                Our mission is to empower businesses with scalable, secure, and innovative
                database solutions. We believe in transparency, customer-centricity,
                and continuous improvement.
              </p>
            </ScrollReveal>
          </div>
        </div>
      </section>

      {/* Mission & Values Section */}
      <section className="py-24">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16 items-center mb-24">
            <ScrollReveal direction="left">
              <div>
                <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-6 tracking-tight">
                  Our Mission
                </h2>
                <p className="text-lg text-muted-foreground mb-6 leading-relaxed">
                  At TerraScale, we're on a mission to democratize enterprise-grade database
                  infrastructure. We believe that every business, regardless of size, should
                  have access to powerful, scalable, and secure database solutions.
                </p>
                <p className="text-lg text-muted-foreground mb-6 leading-relaxed">
                  Founded on the principles of innovation and reliability, we've built a
                  platform that grows with your business, ensuring you're never held back
                  by technical limitations or database performance issues.
                </p>
                <div className="bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 border-l-4 border-primary p-6 rounded-r-xl">
                  <p className="text-foreground font-medium italic">
                    "Database technology should empower, not complicate. That's the driving force
                    behind everything we build at TerraScale."
                  </p>
                </div>
              </div>
            </ScrollReveal>

            <ScrollReveal direction="right">
              <motion.div
                className="relative"
                whileHover={{ scale: 1.02 }}
                transition={{ type: "spring", stiffness: 200 }}
              >
                <div className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] rounded-2xl p-8 shadow-2xl">
                  <div className="bg-card rounded-lg p-6 h-64 flex items-center justify-center">
                    <div className="text-center">
                      <motion.div
                        className="w-16 h-16 bg-gradient-to-r from-[#4379F2] to-[#6EC207] rounded-full mx-auto mb-4 flex items-center justify-center"
                        animate={{ scale: [1, 1.05, 1] }}
                        transition={{ duration: 3, repeat: Infinity }}
                      >
                        <Target className="h-8 w-8 text-white" />
                      </motion.div>
                      <p className="text-muted-foreground font-medium">Our Vision</p>
                    </div>
                  </div>
                </div>
              </motion.div>
            </ScrollReveal>
          </div>

          {/* Core Values */}
          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-4 tracking-tight">
                Our Core Values
              </h2>
              <p className="text-xl text-muted-foreground max-w-2xl mx-auto">
                The principles that guide every decision we make and every solution we build.
              </p>
            </div>
          </ScrollReveal>

          <StaggerContainer className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-8">
            {values.map((value) => (
              <StaggerItem key={value.title}>
                <motion.div
                  className="text-center p-6 bg-card border border-border rounded-xl h-full"
                  whileHover={{
                    y: -4,
                    boxShadow: "0 16px 32px -8px rgba(67, 121, 242, 0.1)"
                  }}
                >
                  <motion.div
                    className="w-12 h-12 bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 rounded-lg flex items-center justify-center mx-auto mb-4"
                    whileHover={{ scale: 1.1, rotate: 5 }}
                    transition={{ type: "spring", stiffness: 300 }}
                  >
                    <value.icon className="h-6 w-6 text-primary" />
                  </motion.div>
                  <h3 className="text-xl font-semibold text-foreground mb-3">
                    {value.title}
                  </h3>
                  <p className="text-muted-foreground">
                    {value.description}
                  </p>
                </motion.div>
              </StaggerItem>
            ))}
          </StaggerContainer>
        </div>
      </section>

      {/* Team Section */}
      <section className="py-24 bg-gradient-to-b from-background to-accent/5">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-4 tracking-tight">
                Our Team
              </h2>
              <p className="text-xl text-muted-foreground max-w-2xl mx-auto">
                Meet the passionate individuals building the future of scalable database technology.
              </p>
            </div>
          </ScrollReveal>

          <ScrollReveal>
            <div className="flex justify-center">
              <motion.div
                className="bg-card border border-border p-8 rounded-2xl max-w-md"
                whileHover={{
                  y: -8,
                  boxShadow: "0 24px 48px -12px rgba(67, 121, 242, 0.15)"
                }}
              >
                <div className="text-center">
                  <motion.div
                    className="w-24 h-24 bg-gradient-to-r from-[#4379F2] to-[#6EC207] rounded-full mx-auto mb-6 flex items-center justify-center text-white text-2xl font-bold"
                    whileHover={{ scale: 1.05 }}
                  >
                    MG
                  </motion.div>
                  <h3 className="text-2xl font-bold text-foreground mb-2">
                    Mario Gabriell Karaziaki Belchior
                  </h3>
                  <p className="text-primary font-semibold mb-4">CEO & Founder</p>
                  <p className="text-muted-foreground leading-relaxed mb-4">
                    Building the future of scalable database technology with passion and vision.
                    Mario combines deep technical expertise with strategic leadership to
                    drive TerraScale's mission forward.
                  </p>
                  <a
                    href="mailto:mariogk@terrascale.tech"
                    className="inline-flex items-center text-sm text-muted-foreground hover:text-primary transition-colors duration-200"
                  >
                    <Mail className="h-4 w-4 mr-2" />
                    mariogk@terrascale.tech
                  </a>
                </div>
              </motion.div>
            </div>
          </ScrollReveal>

          {/* Join Team CTA */}
          <ScrollReveal delay={0.2}>
            <div className="mt-16 text-center">
              <motion.div
                className="bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 rounded-xl p-8 border border-border"
                whileHover={{ scale: 1.01 }}
                transition={{ type: "spring", stiffness: 300 }}
              >
                <h3 className="text-2xl font-bold text-foreground mb-4">Join Our Team</h3>
                <p className="text-muted-foreground mb-6 max-w-2xl mx-auto">
                  We're always looking for talented individuals who share our passion for building
                  innovative database solutions. Join us in shaping the future of data infrastructure.
                </p>
                <div className="flex flex-col sm:flex-row gap-4 justify-center">
                  <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                    <Link
                      to="/contact"
                      className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-6 py-3 rounded-lg font-semibold transition-all duration-300 inline-flex items-center justify-center"
                    >
                      <MessageSquare className="h-4 w-4 mr-2" />
                      Get in Touch
                    </Link>
                  </motion.div>
                  <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                    <a
                      href="https://docs.terrascale.tech"
                      target="_blank"
                      rel="noopener noreferrer"
                      className="border border-border hover:bg-accent text-foreground px-6 py-3 rounded-lg font-semibold transition-all duration-300 inline-flex items-center justify-center"
                    >
                      <BookOpen className="h-4 w-4 mr-2" />
                      Learn More
                    </a>
                  </motion.div>
                </div>
              </motion.div>
            </div>
          </ScrollReveal>
        </div>
      </section>

      {/* Stats Section */}
      <section className="py-24 bg-gradient-to-r from-[#4379F2] to-[#6EC207] relative overflow-hidden">
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_30%_40%,rgba(255,255,255,0.1),transparent_50%)]" />
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_70%_60%,rgba(255,255,255,0.05),transparent_50%)]" />

        <ScrollReveal>
          <div className="relative z-10 max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
            <div className="text-center mb-12">
              <h2 className="text-3xl md:text-4xl font-bold text-white mb-4 tracking-tight">
                TerraScale by the Numbers
              </h2>
              <p className="text-xl text-white/80">
                Trusted by developers and businesses worldwide
              </p>
            </div>

            <StaggerContainer className="grid grid-cols-1 md:grid-cols-4 gap-8 text-center text-white">
              {stats.map((stat) => (
                <StaggerItem key={stat.label}>
                  <motion.div
                    className="p-6 bg-white/10 rounded-xl backdrop-blur-sm"
                    whileHover={{ scale: 1.02, backgroundColor: "rgba(255,255,255,0.15)" }}
                  >
                    <div className="text-4xl md:text-5xl font-bold mb-2">
                      {stat.noCounter ? stat.value : <AnimatedCounter value={stat.value} />}
                    </div>
                    <div className="text-white/80">{stat.label}</div>
                  </motion.div>
                </StaggerItem>
              ))}
            </StaggerContainer>
          </div>
        </ScrollReveal>
      </section>
    </>
  )
}

export default About
