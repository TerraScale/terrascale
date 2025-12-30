import { useState } from 'react'
import { Mail, Phone, MapPin, Clock, Send, CheckCircle, AlertCircle, MessageSquare } from 'lucide-react'

const Contact = () => {
  const [formData, setFormData] = useState({
    name: '',
    email: '',
    message: ''
  })
  const [status, setStatus] = useState({ type: '', message: '' })
  const [isSubmitting, setIsSubmitting] = useState(false)

  const handleInputChange = (e) => {
    const { name, value } = e.target
    setFormData(prev => ({
      ...prev,
      [name]: value
    }))
    // Clear status when user starts typing
    if (status.message) {
      setStatus({ type: '', message: '' })
    }
  }

  const handleSubmit = async (e) => {
    e.preventDefault()
    
    // Basic validation
    if (!formData.name.trim() || !formData.email.trim() || !formData.message.trim()) {
      setStatus({
        type: 'error',
        message: 'Please fill in all fields.'
      })
      return
    }

    // Email validation
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
      // For now, we'll simulate form submission
      // In production, this would connect to Google Forms or your backend
      await new Promise(resolve => setTimeout(resolve, 1000))
      
      setStatus({
        type: 'success',
        message: 'Message sent! We\'ll get back to you soon.'
      })
      
      // Reset form
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
      icon: Phone,
      label: 'Phone',
      value: '+1 (555) 123-4567',
      href: 'tel:+15551234567'
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
      <section className="py-20 bg-gradient-to-br from-background via-accent/20 to-background">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center max-w-4xl mx-auto">
            <h1 className="text-4xl md:text-6xl font-bold text-foreground mb-6 animate-fade-in">
              Contact <span className="bg-gradient-to-r from-blue-600 to-purple-600 bg-clip-text text-transparent">Us</span>
            </h1>
            <p className="text-xl md:text-2xl text-muted-foreground mb-8 animate-slide-up">
              We'd love to hear from you! Fill out the form or reach us through the info below.
            </p>
          </div>
        </div>
      </section>

      {/* Contact Form & Info Section */}
      <section className="py-20">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-16">
            {/* Contact Form */}
            <div className="bg-card border border-border rounded-xl p-8">
              <h2 className="text-3xl font-bold text-foreground mb-6">
                Send us a message
              </h2>
              <form onSubmit={handleSubmit} className="space-y-6">
                <div>
                  <label htmlFor="name" className="block text-sm font-medium text-foreground mb-2">
                    Name *
                  </label>
                  <input
                    type="text"
                    id="name"
                    name="name"
                    value={formData.name}
                    onChange={handleInputChange}
                    className="w-full px-4 py-3 bg-background border border-border rounded-lg focus:ring-2 focus:ring-primary focus:border-transparent transition-all duration-200"
                    placeholder="Your full name"
                    required
                  />
                </div>
                
                <div>
                  <label htmlFor="email" className="block text-sm font-medium text-foreground mb-2">
                    Email *
                  </label>
                  <input
                    type="email"
                    id="email"
                    name="email"
                    value={formData.email}
                    onChange={handleInputChange}
                    className="w-full px-4 py-3 bg-background border border-border rounded-lg focus:ring-2 focus:ring-primary focus:border-transparent transition-all duration-200"
                    placeholder="your.email@example.com"
                    required
                  />
                </div>
                
                <div>
                  <label htmlFor="message" className="block text-sm font-medium text-foreground mb-2">
                    Message *
                  </label>
                  <textarea
                    id="message"
                    name="message"
                    rows={6}
                    value={formData.message}
                    onChange={handleInputChange}
                    className="w-full px-4 py-3 bg-background border border-border rounded-lg focus:ring-2 focus:ring-primary focus:border-transparent transition-all duration-200 resize-none"
                    placeholder="Tell us about your project or question..."
                    required
                  />
                </div>

                {/* Status Message */}
                {status.message && (
                  <div className={`p-4 rounded-lg flex items-center space-x-2 ${
                    status.type === 'success' 
                      ? 'bg-green-500/10 text-green-600 border border-green-500/20' 
                      : 'bg-red-500/10 text-red-600 border border-red-500/20'
                  }`}>
                    {status.type === 'success' ? (
                      <CheckCircle className="h-5 w-5" />
                    ) : (
                      <AlertCircle className="h-5 w-5" />
                    )}
                    <span>{status.message}</span>
                  </div>
                )}
                
                <button
                  type="submit"
                  disabled={isSubmitting}
                  className="bg-gradient-to-r from-blue-600 to-purple-600 hover:from-blue-700 hover:to-purple-700 text-white px-6 py-3 rounded-lg font-semibold transition-all duration-200 w-full inline-flex items-center justify-center disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {isSubmitting ? (
                    <>
                      <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white mr-2"></div>
                      Sending...
                    </>
                  ) : (
                    <>
                      <Send className="mr-2 h-4 w-4" />
                      Send Message
                    </>
                  )}
                </button>
              </form>
            </div>

            {/* Contact Information */}
            <div>
              <h2 className="text-3xl font-bold text-foreground mb-6">
                Get in touch
              </h2>
              <p className="text-muted-foreground mb-8">
                Have questions about our platform? Need help with your account? 
                Our team is here to help you succeed.
              </p>
              
              <div className="space-y-6">
                {contactInfo.map((info, index) => (
                  <div key={index} className="flex items-start space-x-4">
                    <div className="w-10 h-10 bg-gradient-to-r from-blue-600/10 to-purple-600/10 rounded-lg flex items-center justify-center flex-shrink-0 border border-border">
                      <info.icon className="h-5 w-5 text-primary" />
                    </div>
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
                  </div>
                ))}
              </div>

              {/* Discord CTA */}
              <div className="mt-8 bg-gradient-to-r from-blue-600/10 to-purple-600/10 rounded-xl p-6 border border-border">
                <h3 className="font-semibold text-foreground mb-2 flex items-center">
                  <MessageSquare className="h-5 w-5 mr-2 text-primary" />
                  Join our Discord community
                </h3>
                <p className="text-muted-foreground text-sm mb-4">
                  Connect with other developers, get help, and stay updated with the latest news.
                </p>
                <a
                  href="https://discord.gg/terrascale"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="bg-[#5865F2] hover:bg-[#4752C4] text-white px-4 py-2 rounded-lg text-sm font-medium transition-colors duration-200 inline-flex items-center"
                >
                  <MessageSquare className="h-4 w-4 mr-2" />
                  Join Discord
                </a>
              </div>
            </div>
          </div>
        </div>
      </section>
    </>
  )
}

export default Contact