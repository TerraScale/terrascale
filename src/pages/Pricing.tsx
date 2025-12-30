import { useState } from 'react'
import { Check, Star, ChevronDown, Zap, Shield, TrendingUp, BookOpen, MessageSquare, ArrowRight, ExternalLink } from 'lucide-react'
import { Link } from 'react-router-dom'
import { motion, AnimatePresence } from 'framer-motion'
import { ScrollReveal, StaggerContainer, StaggerItem } from '@/components/motion'
import ContactModal from '../components/ContactModal'

const Pricing = () => {
  const [billingPeriod, setBillingPeriod] = useState<'monthly' | 'annual'>('monthly')
  const [openFaq, setOpenFaq] = useState<number | null>(null)
  const [isContactModalOpen, setIsContactModalOpen] = useState(false)

  const plans = [
    {
      name: 'Hobby',
      price: { monthly: 4.99, annual: 49 },
      popular: false,
      description: 'Great for personal projects',
      trial: '30-day free trial',
      features: [
        'Databases: Unlimited',
        'Database Operations: 10M included',
        'Storage: 10 GB',
        'Egress Bandwidth: 15 GB',
        'Support: Priority Discord'
      ],
      cta: 'Start Free Trial',
      href: '/contact',
      gradient: 'from-[#4379F2] to-[#4379F2]'
    },
    {
      name: 'Pro',
      price: { monthly: 24.99, annual: 249 },
      popular: true,
      description: 'Best for growing teams',
      trial: '30-day free trial',
      features: [
        'Databases: Unlimited',
        'Database Operations: 1B included',
        'Storage: 50 GB',
        'Egress Bandwidth: 100 GB',
        'Support: Email + Discord'
      ],
      cta: 'Start Free Trial',
      href: '/contact',
      gradient: 'from-[#4379F2] to-[#6EC207]'
    },
    {
      name: 'Enterprise',
      price: null,
      popular: false,
      description: 'For mission-critical applications',
      features: [
        'Everything in Pro',
        'Dedicated support team',
        'Custom SLA agreements',
        'On-premise deployment options',
        'Advanced security features',
        'Custom integrations'
      ],
      cta: 'Contact Us',
      href: null,
      gradient: 'from-[#6EC207] to-[#4379F2]'
    }
  ]

  const faqData = [
    {
      question: 'What is a Database Operation?',
      answer: (
        <div className="space-y-3">
          <p>A database operation refers to any interaction with your database. This includes:</p>
          <ul className="list-disc list-inside space-y-2 ml-4">
            <li><strong>Read Operations:</strong> When you retrieve data from the database, it counts as one read operation.</li>
            <li><strong>Write Operations:</strong> When you save or modify data in the database, it counts as one write operation.</li>
            <li><strong>Replication Operations:</strong> If your database is configured to replicate data across multiple regions, each time an object is written or updated, it incurs an operation for the initial write plus one operation for each region it replicates to.</li>
          </ul>
        </div>
      )
    },
    {
      question: 'What if I need more operations or storage?',
      answer: (
        <p>You can easily upgrade your plan at any time from your account dashboard. For instance, if you're on the Pro plan and need more than 50 GB of storage or 1 billion operations, you can upgrade to the Pay-as-you-go Enterprise plan. This lets you scale your resources to fit your needs as your project grows.</p>
      )
    },
    {
      question: 'When can I change my subscription?',
      answer: (
        <div className="space-y-3">
          <p>You can change your subscription anytime you want.</p>
          <ul className="list-disc list-inside space-y-2 ml-4">
            <li><strong>Upgrading:</strong> When you upgrade, your new plan's features will be available immediately. We'll adjust the cost for your current billing cycle on a prorated basis.</li>
            <li><strong>Downgrading:</strong> If you choose to downgrade, your current plan will stay active until the end of your billing cycle. Your new, lower-cost plan will then start at the beginning of the next cycle.</li>
          </ul>
          <p>Please note that we generally don't offer partial refunds for downgrades. However, we're happy to review special cases, so feel free to contact our support team to discuss your specific situation.</p>
        </div>
      )
    },
    {
      question: 'Do you offer custom enterprise solutions?',
      answer: (
        <p>Yes! We offer custom enterprise solutions including dedicated instances, private cloud deployments, custom SLAs, and specialized support. Contact our sales team to discuss your specific requirements.</p>
      )
    },
    {
      question: 'Why do you require a credit card for the free trial?',
      answer: (
        <p>We require a credit card to prevent spam and abuse of our platform. This helps us maintain high-quality service for all users. You won't be charged during the 30-day trial period, and you can cancel anytime before the trial ends.</p>
      )
    }
  ]

  const toggleFaq = (index: number) => {
    setOpenFaq(openFaq === index ? null : index)
  }

  const formatPrice = (price: number) => {
    if (price === 0) return 'Free'
    return billingPeriod === 'monthly' ? `$${price}/mo` : `$${price}/yr`
  }

  const getSavings = () => {
    if (billingPeriod === 'annual') {
      return 'Save 20%'
    }
    return null
  }

  const additionalFeatures = [
    { icon: Shield, title: 'Enterprise Security', description: 'SOC 2 compliant with end-to-end encryption' },
    { icon: Zap, title: 'Lightning Fast', description: 'Sub-10ms query latency worldwide' },
    { icon: TrendingUp, title: 'Auto-scaling', description: 'Scales automatically with your traffic' }
  ]

  return (
    <>
      {/* Hero Section */}
      <section className="py-24 bg-gradient-to-br from-background via-accent/10 to-background">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center max-w-4xl mx-auto">
            <ScrollReveal>
              <h1 className="text-4xl md:text-6xl font-bold text-foreground mb-6 tracking-tight">
                Simple <span className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] bg-clip-text text-transparent">Pricing</span>
              </h1>
            </ScrollReveal>
            <ScrollReveal delay={0.1}>
              <p className="text-xl md:text-2xl text-muted-foreground mb-8 leading-relaxed">
                30-day free trial on all plans. No hidden fees, no surprises.
              </p>
            </ScrollReveal>

            {/* Billing Toggle */}
            <ScrollReveal delay={0.2}>
              <div className="inline-flex items-center bg-card border border-border rounded-xl p-1">
                <motion.button
                  onClick={() => setBillingPeriod('monthly')}
                  className={`px-6 py-3 rounded-lg font-medium transition-colors duration-300 relative ${
                    billingPeriod === 'monthly'
                      ? 'text-white'
                      : 'text-muted-foreground hover:text-foreground'
                  }`}
                  whileTap={{ scale: 0.98 }}
                >
                  {billingPeriod === 'monthly' && (
                    <motion.div
                      layoutId="billingToggle"
                      className="absolute inset-0 bg-gradient-to-r from-[#4379F2] to-[#6EC207] rounded-lg shadow-lg"
                      transition={{ type: "spring", bounce: 0.2, duration: 0.6 }}
                    />
                  )}
                  <span className="relative z-10">Monthly</span>
                </motion.button>
                <motion.button
                  onClick={() => setBillingPeriod('annual')}
                  className={`px-6 py-3 rounded-lg font-medium transition-colors duration-300 relative ${
                    billingPeriod === 'annual'
                      ? 'text-white'
                      : 'text-muted-foreground hover:text-foreground'
                  }`}
                  whileTap={{ scale: 0.98 }}
                >
                  {billingPeriod === 'annual' && (
                    <motion.div
                      layoutId="billingToggle"
                      className="absolute inset-0 bg-gradient-to-r from-[#4379F2] to-[#6EC207] rounded-lg shadow-lg"
                      transition={{ type: "spring", bounce: 0.2, duration: 0.6 }}
                    />
                  )}
                  <span className="relative z-10">Annual</span>
                  {getSavings() && billingPeriod !== 'annual' && (
                    <motion.span
                      initial={{ scale: 0 }}
                      animate={{ scale: 1 }}
                      className="absolute -top-2 -right-2 bg-[#6EC207] text-white text-xs px-2 py-1 rounded-full"
                    >
                      {getSavings()}
                    </motion.span>
                  )}
                </motion.button>
              </div>
            </ScrollReveal>
          </div>
        </div>
      </section>

      {/* Pricing Plans */}
      <section className="py-24">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <StaggerContainer className="grid grid-cols-1 md:grid-cols-3 gap-8">
            {plans.map((plan) => (
              <StaggerItem key={plan.name}>
                <motion.div
                  className={`relative bg-card border rounded-2xl h-full ${
                    plan.popular
                      ? 'border-primary shadow-lg scale-105 lg:scale-110'
                      : 'border-border'
                  }`}
                  whileHover={{
                    y: -8,
                    boxShadow: "0 24px 48px -12px rgba(67, 121, 242, 0.15)"
                  }}
                  transition={{ type: "spring", stiffness: 300 }}
                >
                  {plan.popular && (
                    <motion.div
                      className="absolute -top-4 left-1/2 transform -translate-x-1/2"
                      initial={{ y: -10, opacity: 0 }}
                      animate={{ y: 0, opacity: 1 }}
                      transition={{ delay: 0.3 }}
                    >
                      <div className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] text-white px-4 py-2 rounded-full text-sm font-semibold flex items-center shadow-lg">
                        <Star className="h-4 w-4 mr-1 fill-current" />
                        MOST POPULAR
                      </div>
                    </motion.div>
                  )}

                  <div className="p-8">
                    <div className="text-center mb-6">
                      <h3 className="text-2xl font-bold text-foreground mb-2">{plan.name}</h3>
                      <p className="text-muted-foreground text-sm">{plan.description}</p>
                      {plan.trial && (
                        <span className="inline-block mt-2 text-xs font-medium text-[#6EC207] bg-[#6EC207]/10 px-3 py-1 rounded-full">
                          {plan.trial}
                        </span>
                      )}
                    </div>

                    <div className="text-center mb-6">
                      {plan.price ? (
                        <motion.div
                          key={billingPeriod}
                          initial={{ opacity: 0, y: 10 }}
                          animate={{ opacity: 1, y: 0 }}
                          transition={{ duration: 0.3 }}
                        >
                          <span className="text-4xl md:text-5xl font-bold text-foreground">
                            {formatPrice(plan.price[billingPeriod])}
                          </span>
                          {plan.price.monthly > 0 && (
                            <span className="text-muted-foreground ml-2">
                              {billingPeriod === 'annual' ? '/year' : '/month'}
                            </span>
                          )}
                        </motion.div>
                      ) : (
                        <div className="text-center">
                          <span className="text-2xl font-bold text-foreground">Custom Pricing</span>
                          <p className="text-sm text-muted-foreground mt-1">Contact us for details</p>
                        </div>
                      )}
                    </div>

                    <ul className="space-y-4 mb-8">
                      {plan.features.map((feature, featureIndex) => (
                        <motion.li
                          key={featureIndex}
                          className="flex items-start space-x-3"
                          initial={{ opacity: 0, x: -10 }}
                          animate={{ opacity: 1, x: 0 }}
                          transition={{ delay: featureIndex * 0.05 }}
                        >
                          <div className="w-5 h-5 rounded-full bg-gradient-to-r from-[#6EC207] to-[#6EC207] flex items-center justify-center flex-shrink-0 mt-0.5">
                            <Check className="h-3 w-3 text-white" />
                          </div>
                          <span className="text-muted-foreground text-sm">{feature}</span>
                        </motion.li>
                      ))}
                    </ul>

                    {plan.href ? (
                      <motion.div whileHover={{ scale: 1.02 }} whileTap={{ scale: 0.98 }}>
                        <Link
                          to={plan.href}
                          className={`w-full block text-center py-4 px-6 rounded-xl font-semibold transition-all duration-300 bg-gradient-to-r ${plan.gradient} text-white shadow-lg hover:opacity-90`}
                        >
                          {plan.cta}
                        </Link>
                      </motion.div>
                    ) : (
                      <motion.button
                        onClick={() => setIsContactModalOpen(true)}
                        className={`w-full py-4 px-6 rounded-xl font-semibold transition-all duration-300 bg-gradient-to-r ${plan.gradient} text-white shadow-lg hover:opacity-90`}
                        whileHover={{ scale: 1.02 }}
                        whileTap={{ scale: 0.98 }}
                      >
                        {plan.cta}
                      </motion.button>
                    )}
                  </div>
                </motion.div>
              </StaggerItem>
            ))}
          </StaggerContainer>

          {/* Additional Features */}
          <StaggerContainer className="mt-16 grid grid-cols-1 md:grid-cols-3 gap-8">
            {additionalFeatures.map((feature) => (
              <StaggerItem key={feature.title}>
                <motion.div
                  className="text-center p-6 bg-card border border-border rounded-xl"
                  whileHover={{ y: -4, boxShadow: "0 12px 24px -6px rgba(67, 121, 242, 0.1)" }}
                >
                  <motion.div
                    whileHover={{ scale: 1.1, rotate: 5 }}
                    transition={{ type: "spring", stiffness: 300 }}
                  >
                    <feature.icon className="h-12 w-12 text-primary mx-auto mb-4" />
                  </motion.div>
                  <h3 className="font-semibold text-foreground mb-2">{feature.title}</h3>
                  <p className="text-muted-foreground text-sm">{feature.description}</p>
                </motion.div>
              </StaggerItem>
            ))}
          </StaggerContainer>

          {/* Detailed Pricing Information */}
          <ScrollReveal delay={0.2}>
            <motion.div
              className="mt-16 text-center bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 border border-border rounded-2xl p-8"
              whileHover={{ scale: 1.01 }}
              transition={{ type: "spring", stiffness: 300 }}
            >
              <h3 className="text-2xl font-bold text-foreground mb-4">
                Need more detailed pricing information?
              </h3>
              <p className="text-muted-foreground mb-6">
                Get comprehensive details about our pricing structure, including usage-based pricing,
                enterprise discounts, and detailed cost breakdowns.
              </p>
              <motion.a
                href="https://docs.terrascale.tech/pricing"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-2 bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-6 py-3 rounded-lg font-semibold transition-all duration-300"
                whileHover={{ scale: 1.03 }}
                whileTap={{ scale: 0.98 }}
              >
                <BookOpen className="h-5 w-5" />
                View Detailed Pricing
                <ExternalLink className="h-4 w-4" />
              </motion.a>
            </motion.div>
          </ScrollReveal>
        </div>
      </section>

      {/* FAQ Section */}
      <section className="py-24 bg-gradient-to-b from-background to-accent/5">
        <div className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8">
          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-4 tracking-tight">
                Frequently Asked Questions
              </h2>
              <p className="text-xl text-muted-foreground">
                Get answers to common questions about our pricing and plans.
              </p>
            </div>
          </ScrollReveal>

          <div className="space-y-4">
            {faqData.map((faq, index) => (
              <ScrollReveal key={index} delay={index * 0.05}>
                <motion.div
                  className="bg-card border border-border rounded-xl overflow-hidden"
                  whileHover={{ boxShadow: "0 8px 24px -6px rgba(67, 121, 242, 0.1)" }}
                >
                  <button
                    onClick={() => toggleFaq(index)}
                    className="w-full px-6 py-6 text-left flex justify-between items-center hover:bg-accent/50 transition-all duration-300"
                  >
                    <h3 className="font-semibold text-foreground pr-4">{faq.question}</h3>
                    <motion.div
                      animate={{ rotate: openFaq === index ? 180 : 0 }}
                      transition={{ duration: 0.3 }}
                    >
                      <ChevronDown className="h-5 w-5 text-muted-foreground" />
                    </motion.div>
                  </button>

                  <AnimatePresence>
                    {openFaq === index && (
                      <motion.div
                        initial={{ height: 0, opacity: 0 }}
                        animate={{ height: "auto", opacity: 1 }}
                        exit={{ height: 0, opacity: 0 }}
                        transition={{ duration: 0.3, ease: "easeInOut" }}
                        className="overflow-hidden"
                      >
                        <div className="px-6 pb-6 text-muted-foreground">
                          {faq.answer}
                        </div>
                      </motion.div>
                    )}
                  </AnimatePresence>
                </motion.div>
              </ScrollReveal>
            ))}
          </div>

          {/* Help CTA */}
          <ScrollReveal delay={0.3}>
            <motion.div
              className="mt-12 text-center p-8 bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 rounded-xl border border-border"
              whileHover={{ scale: 1.01 }}
            >
              <h3 className="text-xl font-semibold text-foreground mb-2">Still have questions?</h3>
              <p className="text-muted-foreground mb-6">Our team is here to help you find the perfect plan for your needs.</p>
              <div className="flex flex-col sm:flex-row gap-4 justify-center">
                <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                  <Link
                    to="/contact"
                    className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-6 py-3 rounded-lg font-semibold transition-all duration-300 inline-flex items-center justify-center"
                  >
                    <MessageSquare className="h-4 w-4 mr-2" />
                    Contact Sales
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
                    Read Documentation
                  </a>
                </motion.div>
              </div>
            </motion.div>
          </ScrollReveal>
        </div>
      </section>

      {/* CTA Section */}
      <section className="py-24 bg-gradient-to-r from-[#4379F2] to-[#6EC207] relative overflow-hidden">
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_30%_40%,rgba(255,255,255,0.1),transparent_50%)]" />
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_70%_60%,rgba(255,255,255,0.05),transparent_50%)]" />

        <ScrollReveal>
          <div className="relative z-10 max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 text-center">
            <h2 className="text-3xl md:text-4xl font-bold text-white mb-4 tracking-tight">
              Ready to get started?
            </h2>
            <p className="text-xl text-white/80 mb-8">
              Join thousands of developers building the future with TerraScale.
            </p>
            <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
              <Link
                to="/contact"
                className="bg-white text-[#4379F2] hover:bg-gray-50 font-bold py-4 px-8 rounded-xl transition-all duration-300 inline-flex items-center shadow-lg"
              >
                Start Building Today
                <ArrowRight className="ml-2 h-5 w-5" />
              </Link>
            </motion.div>
          </div>
        </ScrollReveal>
      </section>

      {/* Contact Modal */}
      <ContactModal
        isOpen={isContactModalOpen}
        onClose={() => setIsContactModalOpen(false)}
      />
    </>
  )
}

export default Pricing
