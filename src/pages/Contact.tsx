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
      <section className="py-24 bg-[#050508]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center max-w-4xl mx-auto">
            <ScrollReveal>
              <h1 className="text-4xl md:text-6xl font-bold text-[#F8FAFC] mb-6 tracking-tight">
                <span className="terminal-prefix">///</span>
                Contact Us
              </h1>
            </ScrollReveal>
            <ScrollReveal delay={0.1}>
              <p className="text-xl md:text-2xl text-[#94A3B8] leading-relaxed">
                We'd love to hear from you! Fill out the form or reach us through the info below.
              </p>
            </ScrollReveal>
          </div>
        </div>
      </section>

      <section className="py-24 bg-[#0A0A0E]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16">
            <ScrollReveal direction="left">
              <motion.div
                className="ts-glass ts-rounded-lg p-8 hover-lift"
                whileHover={{
                  y: -4,
                }}
              >
                <h2 className="text-3xl font-bold text-[#F8FAFC] mb-6">
                  <span className="terminal-prefix">///</span>
                  Send us a message
                </h2>
                <form onSubmit={handleSubmit} className="space-y-6">
                  <div>
                    <label htmlFor="name" className="block text-sm font-bold text-[#F8FAFC] mb-2">
                      <span className="terminal-prefix">///</span>
                      Name *
                    </label>
                    <motion.input
                      type="text"
                      id="name"
                      name="name"
                      value={formData.name}
                      onChange={handleInputChange}
                      className="ts-input w-full shadow-offset"
                      placeholder="Your full name"
                      required
                      whileFocus={{ scale: 1.01 }}
                    />
                  </div>

                  <div>
                    <label htmlFor="email" className="block text-sm font-bold text-[#F8FAFC] mb-2">
                      <span className="terminal-prefix">///</span>
                      Email *
                    </label>
                    <motion.input
                      type="email"
                      id="email"
                      name="email"
                      value={formData.email}
                      onChange={handleInputChange}
                      className="ts-input w-full shadow-offset"
                      placeholder="your.email@example.com"
                      required
                      whileFocus={{ scale: 1.01 }}
                    />
                  </div>

                  <div>
                    <label htmlFor="message" className="block text-sm font-bold text-[#F8FAFC] mb-2">
                      <span className="terminal-prefix">///</span>
                      Message *
                    </label>
                    <motion.textarea
                      id="message"
                      name="message"
                      rows={6}
                      value={formData.message}
                      onChange={handleInputChange}
                      className="ts-input w-full resize-none shadow-offset"
                      placeholder="Tell us about your project or question..."
                      required
                      whileFocus={{ scale: 1.01 }}
                    />
                  </div>

                  <AnimatePresence>
                    {status.message && (
                      <motion.div
                        initial={{ opacity: 0, y: -10 }}
                        animate={{ opacity: 1, y: 0 }}
                        exit={{ opacity: 0, y: -10 }}
                        transition={{ duration: 0.3 }}
                        className={`p-4 ts-rounded-sm border flex items-center space-x-2 text-sm ${
                          status.type === 'success'
                            ? 'bg-[#10B981]/10 text-[#10B981] border-[#10B981]'
                            : 'bg-[#F43F5E]/10 text-[#F43F5E] border-[#F43F5E]'
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
                    className="ts-btn-primary px-6 py-3 ts-rounded-md font-bold w-full inline-flex items-center justify-center disabled:opacity-50 disabled:cursor-not-allowed shadow-offset-blue hover-lift"
                    whileHover={{ scale: isSubmitting ? 1 : 1.02 }}
                    whileTap={{ scale: isSubmitting ? 1 : 0.98 }}
                  >
                    {isSubmitting ? (
                      <>
                        <motion.div
                          className="rounded-full h-4 w-4 border-2 border-[#050508] border-t-transparent mr-2"
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

            <ScrollReveal direction="right">
              <div>
                <h2 className="text-3xl font-bold text-[#F8FAFC] mb-6">
                  <span className="terminal-prefix">///</span>
                  Get in touch
                </h2>
                <p className="text-[#94A3B8] mb-8 leading-relaxed">
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
                          className="w-10 h-10 bg-[#050508] border-2 border-[#1A1A24] ts-rounded-sm flex items-center justify-center flex-shrink-0 hover:border-[#2563EB] transition-colors duration-200"
                          whileHover={{ scale: 1.1, rotate: 2 }}
                        >
                          <info.icon className="h-5 w-5 text-[#2563EB]" />
                        </motion.div>
                        <div>
                          <h3 className="font-bold text-[#F8FAFC] mb-1">{info.label}</h3>
                          {info.href ? (
                            <a
                              href={info.href}
                              className="text-[#94A3B8] hover:text-[#2563EB] transition-colors duration-200 text-sm"
                              target={info.href.startsWith('http') ? '_blank' : undefined}
                              rel={info.href.startsWith('http') ? 'noopener noreferrer' : undefined}
                            >
                              {info.value}
                            </a>
                          ) : (
                            <span className="text-[#94A3B8] text-sm">{info.value}</span>
                          )}
                        </div>
                      </motion.div>
                    </StaggerItem>
                  ))}
                </StaggerContainer>

                <motion.div
                  className="mt-8 ts-glass-frosted ts-rounded-lg p-6 hover:border-[#5865F2] transition-colors duration-200 hover-lift"
                  whileHover={{ scale: 1.01 }}
                >
                  <h3 className="font-bold text-[#F8FAFC] mb-2 flex items-center">
                    <MessageSquare className="h-5 w-5 mr-2 text-[#5865F2]" />
                    Join our Discord community
                  </h3>
                  <p className="text-[#94A3B8] text-sm mb-4">
                    Connect with other developers, get help, and stay updated with the latest news.
                  </p>
                  <motion.a
                    href="https://discord.gg/terrascale"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="bg-[#5865F2] hover:bg-[#4752C4] text-[#F8FAFC] px-4 py-2 ts-rounded-md text-sm font-bold transition-colors duration-200 inline-flex items-center shadow-offset hover-lift"
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
