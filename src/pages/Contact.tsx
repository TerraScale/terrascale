import { useState, type ChangeEvent, type FormEvent } from 'react'
import { Mail, Clock, Send, CheckCircle, AlertCircle, MessageSquare } from 'lucide-react'
import { motion, AnimatePresence } from 'framer-motion'
import { ScrollReveal, StaggerContainer, StaggerItem } from '@/components/motion'

interface FormData {
  name: string
  email: string
  message: string
}

interface Status {
  type: 'success' | 'error' | ''
  message: string
}

const Contact = () => {
  const [formData, setFormData] = useState<FormData>({
    name: '',
    email: '',
    message: ''
  })
  const [status, setStatus] = useState<Status>({ type: '', message: '' })
  const [isSubmitting, setIsSubmitting] = useState(false)

  const handleInputChange = (e: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    const { name, value } = e.target
    setFormData(prev => ({
      ...prev,
      [name]: value
    }))
    if (status.message) {
      setStatus({ type: '', message: '' })
    }
  }

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault()

    if (!formData.name.trim() || !formData.email.trim() || !formData.message.trim()) {
      setStatus({
        type: 'error',
        message: 'Please fill in all fields.'
      })
      return
    }

    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/
    if (!emailRegex.test(formData.email)) {
      setStatus({
        type: 'error',
        message: 'Please enter a valid email address.'
      })
      return
    }

    setIsSubmitting(true)

    try {
      await new Promise(resolve => setTimeout(resolve, 1000))

      setStatus({
        type: 'success',
        message: 'Message sent! We\'ll get back to you soon.'
      })

      setFormData({ name: '', email: '', message: '' })
    } catch {
      setStatus({
        type: 'error',
        message: 'Something went wrong. Please try again later.'
      })
    } finally {
      setIsSubmitting(false)
    }
  }

  const contactInfo = [
    {
      icon: Mail,
      label: 'Email',
      value: 'support@terrascale.tech',
      href: 'mailto:support@terrascale.tech'
    },
    {
      icon: MessageSquare,
      label: 'Discord',
      value: 'Join our community',
      href: 'https://discord.gg/terrascale'
    },
    {
      icon: Clock,
      label: 'Business Hours',
      value: 'Mon-Fri: 9am - 6pm PST',
      href: null
    }
  ]

  return (
    <>
      {/* Hero Section */}
      <section className="py-24 bg-gradient-to-br from-background via-accent/20 to-background">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center max-w-4xl mx-auto">
            <ScrollReveal>
              <h1 className="text-4xl md:text-6xl font-bold text-foreground mb-6 tracking-tight">
                Contact <span className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] bg-clip-text text-transparent">Us</span>
              </h1>
            </ScrollReveal>
            <ScrollReveal delay={0.1}>
              <p className="text-xl md:text-2xl text-muted-foreground leading-relaxed">
                We'd love to hear from you! Fill out the form or reach us through the info below.
              </p>
            </ScrollReveal>
          </div>
        </div>
      </section>

      {/* Contact Form & Info Section */}
      <section className="py-24">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16">
            {/* Contact Form */}
            <ScrollReveal direction="left">
              <motion.div
                className="bg-card border border-border rounded-xl p-8"
                whileHover={{ boxShadow: "0 20px 40px -12px rgba(67, 121, 242, 0.1)" }}
              >
                <h2 className="text-3xl font-bold text-foreground mb-6">
                  Send us a message
                </h2>
                <form onSubmit={handleSubmit} className="space-y-6">
                  <div>
                    <label htmlFor="name" className="block text-sm font-medium text-foreground mb-2">
                      Name *
                    </label>
                    <motion.input
                      type="text"
                      id="name"
                      name="name"
                      value={formData.name}
                      onChange={handleInputChange}
                      className="w-full px-4 py-3 bg-background border border-border rounded-lg focus:ring-2 focus:ring-primary focus:border-transparent transition-all duration-200 outline-none"
                      placeholder="Your full name"
                      required
                      whileFocus={{ scale: 1.01, boxShadow: "0 0 0 2px rgba(67, 121, 242, 0.3)" }}
                    />
                  </div>

                  <div>
                    <label htmlFor="email" className="block text-sm font-medium text-foreground mb-2">
                      Email *
                    </label>
                    <motion.input
                      type="email"
                      id="email"
                      name="email"
                      value={formData.email}
                      onChange={handleInputChange}
                      className="w-full px-4 py-3 bg-background border border-border rounded-lg focus:ring-2 focus:ring-primary focus:border-transparent transition-all duration-200 outline-none"
                      placeholder="your.email@example.com"
                      required
                      whileFocus={{ scale: 1.01, boxShadow: "0 0 0 2px rgba(67, 121, 242, 0.3)" }}
                    />
                  </div>

                  <div>
                    <label htmlFor="message" className="block text-sm font-medium text-foreground mb-2">
                      Message *
                    </label>
                    <motion.textarea
                      id="message"
                      name="message"
                      rows={6}
                      value={formData.message}
                      onChange={handleInputChange}
                      className="w-full px-4 py-3 bg-background border border-border rounded-lg focus:ring-2 focus:ring-primary focus:border-transparent transition-all duration-200 resize-none outline-none"
                      placeholder="Tell us about your project or question..."
                      required
                      whileFocus={{ scale: 1.01, boxShadow: "0 0 0 2px rgba(67, 121, 242, 0.3)" }}
                    />
                  </div>

                  {/* Status Message */}
                  <AnimatePresence>
                    {status.message && (
                      <motion.div
                        initial={{ opacity: 0, y: -10 }}
                        animate={{ opacity: 1, y: 0 }}
                        exit={{ opacity: 0, y: -10 }}
                        transition={{ duration: 0.3 }}
                        className={`p-4 rounded-lg flex items-center space-x-2 ${
                          status.type === 'success'
                            ? 'bg-green-500/10 text-green-600 border border-green-500/20'
                            : 'bg-red-500/10 text-red-600 border border-red-500/20'
                        }`}
                      >
                        {status.type === 'success' ? (
                          <motion.div
                            initial={{ scale: 0 }}
                            animate={{ scale: 1 }}
                            transition={{ type: "spring", stiffness: 300 }}
                          >
                            <CheckCircle className="h-5 w-5" />
                          </motion.div>
                        ) : (
                          <AlertCircle className="h-5 w-5" />
                        )}
                        <span>{status.message}</span>
                      </motion.div>
                    )}
                  </AnimatePresence>

                  <motion.button
                    type="submit"
                    disabled={isSubmitting}
                    className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-6 py-3 rounded-lg font-semibold transition-all duration-200 w-full inline-flex items-center justify-center disabled:opacity-50 disabled:cursor-not-allowed"
                    whileHover={{ scale: isSubmitting ? 1 : 1.02 }}
                    whileTap={{ scale: isSubmitting ? 1 : 0.98 }}
                  >
                    {isSubmitting ? (
                      <>
                        <motion.div
                          className="rounded-full h-4 w-4 border-2 border-white border-t-transparent mr-2"
                          animate={{ rotate: 360 }}
                          transition={{ duration: 1, repeat: Infinity, ease: "linear" }}
                        />
                        Sending...
                      </>
                    ) : (
                      <>
                        <Send className="mr-2 h-4 w-4" />
                        Send Message
                      </>
                    )}
                  </motion.button>
                </form>
              </motion.div>
            </ScrollReveal>

            {/* Contact Information */}
            <ScrollReveal direction="right">
              <div>
                <h2 className="text-3xl font-bold text-foreground mb-6">
                  Get in touch
                </h2>
                <p className="text-muted-foreground mb-8 leading-relaxed">
                  Have questions about our platform? Need help with your account?
                  Our team is here to help you succeed.
                </p>

                <StaggerContainer className="space-y-6">
                  {contactInfo.map((info) => (
                    <StaggerItem key={info.label}>
                      <motion.div
                        className="flex items-start space-x-4"
                        whileHover={{ x: 4 }}
                        transition={{ type: "spring", stiffness: 300 }}
                      >
                        <motion.div
                          className="w-10 h-10 bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 rounded-lg flex items-center justify-center flex-shrink-0 border border-border"
                          whileHover={{ scale: 1.1, rotate: 5 }}
                        >
                          <info.icon className="h-5 w-5 text-primary" />
                        </motion.div>
                        <div>
                          <h3 className="font-semibold text-foreground mb-1">{info.label}</h3>
                          {info.href ? (
                            <a
                              href={info.href}
                              className="text-muted-foreground hover:text-primary transition-colors duration-200"
                              target={info.href.startsWith('http') ? '_blank' : undefined}
                              rel={info.href.startsWith('http') ? 'noopener noreferrer' : undefined}
                            >
                              {info.value}
                            </a>
                          ) : (
                            <span className="text-muted-foreground">{info.value}</span>
                          )}
                        </div>
                      </motion.div>
                    </StaggerItem>
                  ))}
                </StaggerContainer>

                {/* Discord CTA */}
                <motion.div
                  className="mt-8 bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 rounded-xl p-6 border border-border"
                  whileHover={{ scale: 1.01, boxShadow: "0 12px 24px -6px rgba(67, 121, 242, 0.1)" }}
                >
                  <h3 className="font-semibold text-foreground mb-2 flex items-center">
                    <MessageSquare className="h-5 w-5 mr-2 text-primary" />
                    Join our Discord community
                  </h3>
                  <p className="text-muted-foreground text-sm mb-4">
                    Connect with other developers, get help, and stay updated with the latest news.
                  </p>
                  <motion.a
                    href="https://discord.gg/terrascale"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="bg-[#5865F2] hover:bg-[#4752C4] text-white px-4 py-2 rounded-lg text-sm font-medium transition-colors duration-200 inline-flex items-center"
                    whileHover={{ scale: 1.03 }}
                    whileTap={{ scale: 0.98 }}
                  >
                    <MessageSquare className="h-4 w-4 mr-2" />
                    Join Discord
                  </motion.a>
                </motion.div>
              </div>
            </ScrollReveal>
          </div>
        </div>
      </section>
    </>
  )
}

export default Contact
