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
      <section className="py-24 bg-[#050508]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center max-w-4xl mx-auto">
            <ScrollReveal>
              <h1 className="text-4xl md:text-6xl font-bold text-[#F8FAFC] mb-6 tracking-tight">
                <span className="terminal-prefix">///</span>
                About TerraScale
              </h1>
            </ScrollReveal>
            <ScrollReveal delay={0.1}>
              <p className="text-xl md:text-2xl text-[#94A3B8] leading-relaxed">
                Our mission is to empower businesses with scalable, secure, and innovative
                database solutions. We believe in transparency, customer-centricity,
                and continuous improvement.
              </p>
            </ScrollReveal>
          </div>
        </div>
      </section>

      <section className="py-24 bg-[#0A0A0E]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16 items-center mb-24">
            <ScrollReveal direction="left">
              <div>
                <h2 className="text-3xl md:text-4xl font-bold text-[#F8FAFC] mb-6 tracking-tight">
                  <span className="terminal-prefix">///</span>
                  Our Mission
                </h2>
                <p className="text-lg text-[#94A3B8] mb-6 leading-relaxed">
                  At TerraScale, we're on a mission to democratize enterprise-grade database
                  infrastructure. We believe that every business, regardless of size, should
                  have access to powerful, scalable, and secure database solutions.
                </p>
                <p className="text-lg text-[#94A3B8] mb-6 leading-relaxed">
                  Founded on principles of innovation and reliability, we've built a
                  platform that grows with your business, ensuring you're never held back
                  by technical limitations or database performance issues.
                </p>
                <div className="ts-glass-primary ts-rounded-lg border-l-4 border-[#2563EB] p-6">
                  <p className="text-[#F8FAFC] font-medium italic text-sm">
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
                <div className="ts-glass-frosted ts-rounded-lg p-8 shadow-offset hover-lift">
                  <div className="bg-[#050508] border border-[#1A1A24] ts-rounded-sm p-6 h-64 flex items-center justify-center">
                    <div className="text-center">
                      <motion.div
                        className="w-16 h-16 bg-[#2563EB] ts-rounded-sm mx-auto mb-4 flex items-center justify-center ts-hover-glow"
                        animate={{ scale: [1, 1.05, 1] }}
                        transition={{ duration: 3, repeat: Infinity }}
                      >
                        <Target className="h-8 w-8 text-[#050508]" />
                      </motion.div>
                      <p className="text-[#94A3B8] font-medium">Our Vision</p>
                    </div>
                  </div>
                </div>
              </motion.div>
            </ScrollReveal>
          </div>

          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-4xl font-bold text-[#F8FAFC] mb-4 tracking-tight">
                <span className="terminal-prefix">///</span>
                Our Core Values
              </h2>
              <p className="text-xl text-[#94A3B8] max-w-2xl mx-auto">
                The principles that guide every decision we make and every solution we build.
              </p>
            </div>
          </ScrollReveal>

          <StaggerContainer className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6">
            {values.map((value) => (
              <StaggerItem key={value.title}>
                <motion.div
                  className="text-center p-6 ts-glass ts-rounded-lg h-full hover:border-[#2563EB] transition-colors duration-200 hover-lift"
                  whileHover={{
                    y: -4,
                  }}
                >
                  <motion.div
                    className="w-12 h-12 bg-[#050508] border-2 border-[#1A1A24] ts-rounded-sm flex items-center justify-center mx-auto mb-4 hover:border-[#2563EB] transition-colors duration-200"
                    whileHover={{ scale: 1.1, rotate: 2 }}
                    transition={{ type: "spring", stiffness: 300 }}
                  >
                    <value.icon className="h-6 w-6 text-[#2563EB]" />
                  </motion.div>
                  <h3 className="text-xl font-bold text-[#F8FAFC] mb-3">
                    {value.title}
                  </h3>
                  <p className="text-[#94A3B8] text-sm">
                    {value.description}
                  </p>
                </motion.div>
              </StaggerItem>
            ))}
          </StaggerContainer>
        </div>
      </section>

      <section className="py-24 bg-[#050508]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-4xl font-bold text-[#F8FAFC] mb-4 tracking-tight">
                <span className="terminal-prefix">///</span>
                Our Team
              </h2>
              <p className="text-xl text-[#94A3B8] max-w-2xl mx-auto">
                Meet the passionate individual building the future of scalable database technology.
              </p>
            </div>
          </ScrollReveal>

          <ScrollReveal>
            <div className="flex justify-center">
              <motion.div
                className="ts-glass ts-rounded-lg p-8 max-w-md hover:border-[#2563EB] transition-colors duration-200 hover-lift"
                whileHover={{
                  y: -8,
                }}
              >
                <div className="text-center">
                  <motion.div
                    className="w-24 h-24 bg-[#2563EB] ts-rounded-sm mx-auto mb-6 flex items-center justify-center text-[#050508] text-2xl font-bold ts-hover-glow"
                    whileHover={{ scale: 1.05 }}
                  >
                    MG
                  </motion.div>
                  <h3 className="text-2xl font-bold text-[#F8FAFC] mb-2">
                    Mario Gabriell Karaziaki Belchior
                  </h3>
                  <p className="text-[#2563EB] font-bold mb-4 text-sm">CEO & Founder</p>
                  <p className="text-[#94A3B8] leading-relaxed mb-4">
                    Building the future of scalable database technology with passion and vision.
                    Mario combines deep technical expertise with strategic leadership to
                    drive TerraScale's mission forward.
                  </p>
                  <a
                    href="mailto:mariogk@terrascale.tech"
                    className="inline-flex items-center text-sm text-[#94A3B8] hover:text-[#2563EB] transition-colors duration-200"
                  >
                    <Mail className="h-4 w-4 mr-2" />
                    mariogk@terrascale.tech
                  </a>
                </div>
              </motion.div>
            </div>
          </ScrollReveal>

          <ScrollReveal delay={0.2}>
            <div className="mt-16 text-center">
              <motion.div
                className="ts-glass-frosted ts-rounded-lg p-8 hover:border-[#2563EB] transition-colors duration-200"
                whileHover={{ scale: 1.01 }}
                transition={{ type: "spring", stiffness: 300 }}
              >
                <h3 className="text-2xl font-bold text-[#F8FAFC] mb-4">Join Our Team</h3>
                <p className="text-[#94A3B8] mb-6 max-w-2xl mx-auto">
                  We're always looking for talented individuals who share our passion for building
                  innovative database solutions. Join us in shaping the future of data infrastructure.
                </p>
                <div className="flex flex-col sm:flex-row gap-4 justify-center">
                  <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                    <Link
                      to="/contact"
                      className="ts-btn-primary px-6 py-3 ts-rounded-md font-bold inline-flex items-center justify-center shadow-offset-blue hover-lift"
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
                      className="ts-btn-glass px-6 py-3 ts-rounded-md font-bold inline-flex items-center justify-center shadow-offset hover-lift"
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

      <section className="py-24 bg-[#050508] bg-grid-pattern relative overflow-hidden">
        <ScrollReveal>
          <div className="relative z-10 max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
            <div className="text-center mb-12">
              <h2 className="text-3xl md:text-4xl font-bold text-[#F8FAFC] mb-4 tracking-tight">
                <span className="terminal-prefix">///</span>
                TerraScale by Numbers
              </h2>
              <p className="text-xl text-[#94A3B8]">
                Trusted by developers and businesses worldwide
              </p>
            </div>

            <StaggerContainer className="grid grid-cols-1 md:grid-cols-4 gap-6 text-center">
              {stats.map((stat) => (
                <StaggerItem key={stat.label}>
                  <motion.div
                    className="ts-stats-card"
                    whileHover={{ y: -4 }}
                  >
                    <div className="text-4xl md:text-5xl font-bold ts-text-gradient mb-2">
                      {stat.noCounter ? stat.value : <AnimatedCounter value={stat.value} />}
                    </div>
                    <div className="text-[#94A3B8] text-sm terminal-line">
                      {stat.label}
                    </div>
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
