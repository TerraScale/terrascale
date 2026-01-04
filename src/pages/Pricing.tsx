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
      href: '/contact'
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
      href: '/contact'
    },
    {
      name: 'Enterprise',
      basePrice: 499,
      popular: false,
      description: 'For mission-critical applications',
      features: [
        'Everything in Pro',
        'Dedicated support team',
        'Custom SLA agreements',
        'On-premise deployment options',
        'Advanced security features',
        'Custom integrations',
        'Usage-based pricing: $1/GB storage',
        'Usage-based pricing: $1M operations'
      ],
      cta: 'Contact Us',
      href: null
    }
  ]

  const faqData = [
    {
      question: 'What is a Database Operation?',
      answer: (
        <div className="space-y-3">
          <p>A database operation refers to any interaction with your database. This includes:</p>
          <ul className="list-none space-y-2 ml-4">
            <li className="flex items-start space-x-2">
              <span className="text-[#10B981]">➜</span>
              <span><strong>Read Operations:</strong> When you retrieve data from database, it counts as one read operation.</span>
            </li>
            <li className="flex items-start space-x-2">
              <span className="text-[#10B981]">➜</span>
              <span><strong>Write Operations:</strong> When you save or modify data in database, it counts as one write operation.</span>
            </li>
            <li className="flex items-start space-x-2">
              <span className="text-[#10B981]">➜</span>
              <span><strong>Replication Operations:</strong> If your database is configured to replicate data across multiple regions, each time an object is written or updated, it incurs an operation for the initial write plus one operation for each region it replicates to.</span>
            </li>
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
          <ul className="list-none space-y-2 ml-4">
            <li className="flex items-start space-x-2">
              <span className="text-[#10B981]">➜</span>
              <span><strong>Upgrading:</strong> When you upgrade, your new plan's features will be available immediately. We'll adjust the cost for your current billing cycle on a prorated basis.</span>
            </li>
            <li className="flex items-start space-x-2">
              <span className="text-[#10B981]">➜</span>
              <span><strong>Downgrading:</strong> If you choose to downgrade, your current plan will stay active until the end of your billing cycle. Your new, lower-cost plan will then start at the beginning of the next cycle.</span>
            </li>
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
      <section className="py-24 bg-[#050508]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center max-w-4xl mx-auto">
            <ScrollReveal>
              <h1 className="text-4xl md:text-6xl font-bold text-[#F8FAFC] mb-6 tracking-tight">
                <span className="terminal-prefix">///</span>
                Simple Pricing
              </h1>
            </ScrollReveal>
            <ScrollReveal delay={0.1}>
              <p className="text-xl md:text-2xl text-[#94A3B8] mb-8 leading-relaxed">
                30-day free trial on all plans. No hidden fees, no surprises.
              </p>
            </ScrollReveal>

            <ScrollReveal delay={0.2}>
              <div className="inline-flex items-center ts-glass-frosted ts-rounded-lg p-1">
                <motion.button
                  onClick={() => setBillingPeriod('monthly')}
                  className={`px-6 py-3 ts-rounded-sm font-medium transition-colors duration-200 relative ${
                    billingPeriod === 'monthly'
                      ? 'text-[#050508]'
                      : 'text-[#94A3B8] hover:text-[#F8FAFC]'
                  }`}
                  whileTap={{ scale: 0.98 }}
                >
                  {billingPeriod === 'monthly' && (
                    <motion.div
                      layoutId="billingToggle"
                      className="absolute inset-0 bg-[#2563EB] ts-rounded-sm shadow-offset-blue"
                      transition={{ type: "spring", bounce: 0.2, duration: 0.6 }}
                    />
                  )}
                  <span className="relative z-10">Monthly</span>
                </motion.button>
                <motion.button
                  onClick={() => setBillingPeriod('annual')}
                  className={`px-6 py-3 ts-rounded-sm font-medium transition-colors duration-200 relative ${
                    billingPeriod === 'annual'
                      ? 'text-[#050508]'
                      : 'text-[#94A3B8] hover:text-[#F8FAFC]'
                  }`}
                  whileTap={{ scale: 0.98 }}
                >
                  {billingPeriod === 'annual' && (
                    <motion.div
                      layoutId="billingToggle"
                      className="absolute inset-0 bg-[#2563EB] ts-rounded-sm shadow-offset-blue"
                      transition={{ type: "spring", bounce: 0.2, duration: 0.6 }}
                    />
                  )}
                  <span className="relative z-10">Annual</span>
                  {getSavings() && billingPeriod !== 'annual' && (
                    <motion.span
                      initial={{ scale: 0 }}
                      animate={{ scale: 1 }}
                      className="absolute -top-2 -right-2 bg-[#10B981] text-[#050508] text-xs px-2 py-1 ts-rounded-sm font-bold"
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

      <section className="py-24 bg-[#0A0A0E]">
        <div className="max-w-[1440px] mx-auto px-4 sm:px-6 lg:px-8">
          <StaggerContainer className="grid grid-cols-1 md:grid-cols-3 gap-6">
            {plans.map((plan) => (
              <StaggerItem key={plan.name}>
                <motion.div
                  className={`relative ts-glass ts-rounded-lg ${
                    plan.popular
                      ? 'border-2 border-[#0891B2]'
                      : ''
                  }`}
                  whileHover={{
                    y: -8,
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
                      <div className="bg-[#0891B2] text-[#050508] px-4 py-2 ts-rounded-sm text-sm font-bold flex items-center ts-glow-accent">
                        <Star className="h-4 w-4 mr-1 fill-current" />
                        MOST POPULAR
                      </div>
                    </motion.div>
                  )}

                  <div className="p-8">
                    <div className="text-center mb-6">
                      <h3 className="text-2xl font-bold text-[#F8FAFC] mb-2">{plan.name}</h3>
                      <p className="text-[#94A3B8] text-sm">{plan.description}</p>
                      {plan.trial && (
                        <span className="inline-block mt-2 text-xs font-bold text-[#10B981] bg-[#10B981]/10 px-3 py-1 ts-rounded-sm">
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
                          <span className="text-4xl md:text-5xl font-bold ts-text-gradient">
                            {formatPrice(plan.price[billingPeriod])}
                          </span>
                          {plan.price.monthly > 0 && (
                            <span className="text-[#94A3B8] ml-2 text-sm">
                              {billingPeriod === 'annual' ? '/year' : '/month'}
                            </span>
                          )}
                        </motion.div>
                      ) : plan.basePrice ? (
                        <div className="text-center">
                          <span className="text-2xl font-bold ts-text-gradient">$499/mo</span>
                          <p className="text-sm text-[#94A3B8] mt-1">+ Usage: $1/GB, $1/M ops</p>
                        </div>
                      ) : (
                        <div className="text-center">
                          <span className="text-2xl font-bold text-[#F8FAFC]">Custom Pricing</span>
                          <p className="text-sm text-[#94A3B8] mt-1">Contact us for details</p>
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
                          <div className="w-5 h-5 bg-[#10B981] ts-rounded-sm flex items-center justify-center flex-shrink-0 mt-0.5">
                            <Check className="h-3 w-3 text-[#050508]" />
                          </div>
                          <span className="text-[#94A3B8] text-sm">{feature}</span>
                        </motion.li>
                      ))}
                    </ul>

                    {plan.href ? (
                      <motion.div whileHover={{ scale: 1.02 }} whileTap={{ scale: 0.98 }}>
                        <Link
                          to={plan.href}
                          className={`w-full block text-center py-4 px-6 ts-rounded-md font-bold transition-all duration-200 ${
                            plan.popular
                              ? 'bg-[#0891B2] hover:bg-[#0E7490] text-[#050508] ts-glow-accent'
                              : 'ts-btn-primary'
                          }`}
                        >
                          {plan.cta}
                        </Link>
                      </motion.div>
                    ) : (
                      <motion.button
                        onClick={() => setIsContactModalOpen(true)}
                        className={`w-full py-4 px-6 ts-rounded-md font-bold transition-all duration-200 ${
                          plan.popular
                            ? 'bg-[#0891B2] hover:bg-[#0E7490] text-[#050508] ts-glow-accent'
                            : 'ts-btn-primary'
                        }`}
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

          <StaggerContainer className="mt-16 grid grid-cols-1 md:grid-cols-3 gap-6">
            {additionalFeatures.map((feature) => (
              <StaggerItem key={feature.title}>
                <motion.div
                  className="text-center p-6 ts-glass ts-rounded-lg hover:border-[#2563EB] transition-colors duration-200"
                  whileHover={{ y: -4 }}
                >
                  <motion.div
                    whileHover={{ scale: 1.1, rotate: 5 }}
                    transition={{ type: "spring", stiffness: 300 }}
                  >
                    <feature.icon className="h-12 w-12 text-[#2563EB] mx-auto mb-4" />
                  </motion.div>
                  <h3 className="font-bold text-[#F8FAFC] mb-2">{feature.title}</h3>
                  <p className="text-[#94A3B8] text-sm">{feature.description}</p>
                </motion.div>
              </StaggerItem>
            ))}
          </StaggerContainer>

          <ScrollReveal delay={0.2}>
            <motion.div
              className="mt-16 text-center ts-glass-frosted ts-rounded-lg p-8 hover:border-[#2563EB] transition-colors duration-200"
              whileHover={{ scale: 1.01 }}
              transition={{ type: "spring", stiffness: 300 }}
            >
              <h3 className="text-2xl font-bold text-[#F8FAFC] mb-4">
                Need more detailed pricing information?
              </h3>
              <p className="text-[#94A3B8] mb-6">
                Get comprehensive details about our pricing structure, including usage-based pricing,
                enterprise discounts, and detailed cost breakdowns.
              </p>
              <motion.a
                href="https://docs.terrascale.tech/pricing"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-2 ts-btn-primary px-6 py-3 ts-rounded-md font-bold"
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

      <section className="py-24 bg-[#050508]">
        <div className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8">
          <ScrollReveal>
            <div className="text-center mb-16">
              <h2 className="text-3xl md:text-4xl font-bold text-[#F8FAFC] mb-4 tracking-tight">
                <span className="terminal-prefix">///</span>
                Frequently Asked Questions
              </h2>
              <p className="text-xl text-[#94A3B8]">
                Get answers to common questions about our pricing and plans.
              </p>
            </div>
          </ScrollReveal>

          <div className="space-y-4">
            {faqData.map((faq, index) => (
              <ScrollReveal key={index} delay={index * 0.05}>
                <motion.div
                  className="ts-glass ts-rounded-lg overflow-hidden hover:border-[#2563EB] transition-colors duration-200"
                  whileHover={{ y: -2 }}
                >
                  <button
                    onClick={() => toggleFaq(index)}
                    className="w-full px-6 py-6 text-left flex justify-between items-center hover:bg-[#0A0A0E] transition-all duration-200"
                  >
                    <h3 className="font-bold text-[#F8FAFC] pr-4 flex items-center">
                      <span className="terminal-prefix mr-2">///</span>
                      {faq.question}
                    </h3>
                    <motion.div
                      animate={{ rotate: openFaq === index ? 180 : 0 }}
                      transition={{ duration: 0.3 }}
                    >
                      <ChevronDown className="h-5 w-5 text-[#94A3B8]" />
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
                        <div className="px-6 pb-6 text-[#94A3B8] border-t border-[#1A1A24] pt-4">
                          {faq.answer}
                        </div>
                      </motion.div>
                    )}
                  </AnimatePresence>
                </motion.div>
              </ScrollReveal>
            ))}
          </div>

          <ScrollReveal delay={0.3}>
            <motion.div
              className="mt-12 text-center p-8 ts-glass-frosted ts-rounded-lg hover:border-[#2563EB] transition-colors duration-200"
              whileHover={{ scale: 1.01 }}
            >
              <h3 className="text-xl font-bold text-[#F8FAFC] mb-2">Still have questions?</h3>
              <p className="text-[#94A3B8] mb-6">Our team is here to help you find the perfect plan for your needs.</p>
              <div className="flex flex-col sm:flex-row gap-4 justify-center">
                <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
                  <Link
                    to="/contact"
                    className="ts-btn-primary px-6 py-3 ts-rounded-md font-bold inline-flex items-center justify-center"
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
                    className="ts-btn-glass px-6 py-3 ts-rounded-md font-bold inline-flex items-center justify-center"
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

      <section className="py-24 bg-[#050508] bg-grid-pattern relative overflow-hidden">
        <ScrollReveal>
          <div className="relative z-10 max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 text-center">
            <h2 className="text-3xl md:text-4xl font-bold text-[#F8FAFC] mb-4 tracking-tight">
              <span className="terminal-prefix">///</span>
              Ready to get started?
            </h2>
            <p className="text-xl text-[#94A3B8] mb-8">
              Join thousands of developers building the future with TerraScale.
            </p>
            <motion.div whileHover={{ scale: 1.03 }} whileTap={{ scale: 0.98 }}>
              <Link
                to="/contact"
                className="ts-btn-primary font-bold py-4 px-8 ts-rounded-md inline-flex items-center shadow-offset-blue hover-lift"
              >
                Start Building Today
                <ArrowRight className="ml-2 h-5 w-5" />
              </Link>
            </motion.div>
          </div>
        </ScrollReveal>
      </section>

      <ContactModal
        isOpen={isContactModalOpen}
        onClose={() => setIsContactModalOpen(false)}
      />
    </>
  )
}

export default Pricing
