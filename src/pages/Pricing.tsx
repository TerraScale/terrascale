import { useState } from 'react'
import { Check, Star, ChevronDown, Zap, Shield, TrendingUp, BookOpen, MessageSquare, ArrowRight, ExternalLink } from 'lucide-react'
import { Link } from 'react-router-dom'
import ContactModal from '../components/ContactModal'

const Pricing = () => {
  const [billingPeriod, setBillingPeriod] = useState('monthly') // 'monthly' or 'annual'
  const [openFaq, setOpenFaq] = useState(null)
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
      price: null, // No pricing shown
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
      href: null, // Will open modal instead
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

  const toggleFaq = (index) => {
    setOpenFaq(openFaq === index ? null : index)
  }

  const formatPrice = (price) => {
    if (price === 0) return 'Free'
    return billingPeriod === 'monthly' ? `$${price}/mo` : `$${price}/yr`
  }

  const getSavings = () => {
    if (billingPeriod === 'annual') {
      return 'Save 20%'
    }
    return null
  }

  return (
    <>
      {/* Hero Section */}
      <section className="py-20 bg-gradient-to-br from-background via-accent/10 to-background">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center max-w-4xl mx-auto">
            <h1 className="text-4xl md:text-6xl font-bold text-foreground mb-6 animate-fade-in">
              Simple <span className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] bg-clip-text text-transparent">Pricing</span>
            </h1>
            <p className="text-xl md:text-2xl text-muted-foreground mb-8 animate-slide-up">
              30-day free trial on all plans. No hidden fees, no surprises.
            </p>
            
            {/* Billing Toggle */}
            <div className="inline-flex items-center bg-card border border-border rounded-xl p-1 animate-slide-up">
              <button
                onClick={() => setBillingPeriod('monthly')}
                className={`px-6 py-3 rounded-lg font-medium transition-all duration-300 ${
                  billingPeriod === 'monthly'
                    ? 'bg-gradient-to-r from-[#4379F2] to-[#6EC207] text-white shadow-lg'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
              >
                Monthly
              </button>
              <button
                onClick={() => setBillingPeriod('annual')}
                className={`px-6 py-3 rounded-lg font-medium transition-all duration-300 relative ${
                  billingPeriod === 'annual'
                    ? 'bg-gradient-to-r from-[#4379F2] to-[#6EC207] text-white shadow-lg'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
              >
                Annual
                {getSavings() && (
                  <span className="absolute -top-2 -right-2 bg-[#6EC207] text-white text-xs px-2 py-1 rounded-full">
                    {getSavings()}
                  </span>
                )}
              </button>
            </div>
          </div>
        </div>
      </section>

      {/* Pricing Plans */}
      <section className="py-20">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            {plans.map((plan, index) => (
              <div
                key={plan.name}
                className={`relative bg-card border rounded-2xl hover:shadow-2xl transition-all duration-500 group transform hover:-translate-y-2 ${
                  plan.popular 
                    ? 'border-primary shadow-lg scale-105 lg:scale-110' 
                    : 'border-border hover:border-primary/50'
                }`}
                style={{ animationDelay: `${index * 0.1}s` }}
              >
                {plan.popular && (
                  <div className="absolute -top-4 left-1/2 transform -translate-x-1/2">
                    <div className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] text-white px-4 py-2 rounded-full text-sm font-semibold flex items-center shadow-lg">
                      <Star className="h-4 w-4 mr-1 fill-current" />
                      MOST POPULAR
                    </div>
                  </div>
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
                      <>
                        <span className="text-4xl md:text-5xl font-bold text-foreground">
                          {formatPrice(plan.price[billingPeriod])}
                        </span>
                        {plan.price.monthly > 0 && (
                          <span className="text-muted-foreground ml-2">
                            {billingPeriod === 'annual' ? '/year' : '/month'}
                          </span>
                        )}
                      </>
                    ) : (
                      <div className="text-center">
                        <span className="text-2xl font-bold text-foreground">Custom Pricing</span>
                        <p className="text-sm text-muted-foreground mt-1">Contact us for details</p>
                      </div>
                    )}
                  </div>
                  
                  <ul className="space-y-4 mb-8">
                    {plan.features.map((feature, featureIndex) => (
                      <li key={featureIndex} className="flex items-start space-x-3">
                        <div className="w-5 h-5 rounded-full bg-gradient-to-r from-[#6EC207] to-[#6EC207] flex items-center justify-center flex-shrink-0 mt-0.5">
                          <Check className="h-3 w-3 text-white" />
                        </div>
                        <span className="text-muted-foreground text-sm">{feature}</span>
                      </li>
                    ))}
                  </ul>
                  
                  {plan.href ? (
                    <Link
                      to={plan.href}
                      className={`w-full block text-center py-4 px-6 rounded-xl font-semibold transition-all duration-300 transform hover:scale-105 ${
                        plan.popular
                          ? `bg-gradient-to-r ${plan.gradient} hover:from-[#3366E6] hover:to-[#5CAE06] text-white shadow-lg`
                          : `bg-gradient-to-r ${plan.gradient} hover:opacity-90 text-white shadow-lg`
                      }`}
                    >
                      {plan.cta}
                    </Link>
                  ) : (
                    <button
                      onClick={() => setIsContactModalOpen(true)}
                      className={`w-full py-4 px-6 rounded-xl font-semibold transition-all duration-300 transform hover:scale-105 bg-gradient-to-r ${plan.gradient} hover:from-[#5CAE06] hover:to-[#3366E6] text-white shadow-lg`}
                    >
                      {plan.cta}
                    </button>
                  )}
                </div>
              </div>
            ))}
          </div>
          
          {/* Additional Features */}
          <div className="mt-16 grid grid-cols-1 md:grid-cols-3 gap-8">
            <div className="text-center p-6 bg-card border border-border rounded-xl">
              <Shield className="h-12 w-12 text-primary mx-auto mb-4" />
              <h3 className="font-semibold text-foreground mb-2">Enterprise Security</h3>
              <p className="text-muted-foreground text-sm">SOC 2 compliant with end-to-end encryption</p>
            </div>
            <div className="text-center p-6 bg-card border border-border rounded-xl">
              <Zap className="h-12 w-12 text-primary mx-auto mb-4" />
              <h3 className="font-semibold text-foreground mb-2">Lightning Fast</h3>
              <p className="text-muted-foreground text-sm">Sub-10ms query latency worldwide</p>
            </div>
            <div className="text-center p-6 bg-card border border-border rounded-xl">
              <TrendingUp className="h-12 w-12 text-primary mx-auto mb-4" />
              <h3 className="font-semibold text-foreground mb-2">Auto-scaling</h3>
              <p className="text-muted-foreground text-sm">Scales automatically with your traffic</p>
            </div>
          </div>
          
          {/* Detailed Pricing Information */}
          <div className="mt-16 text-center">
            <div className="bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 border border-border rounded-2xl p-8">
              <h3 className="text-2xl font-bold text-foreground mb-4">
                Need more detailed pricing information?
              </h3>
              <p className="text-muted-foreground mb-6">
                Get comprehensive details about our pricing structure, including usage-based pricing, 
                enterprise discounts, and detailed cost breakdowns.
              </p>
              <a
                href="https://docs.terrascale.tech/pricing"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-2 bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-6 py-3 rounded-lg font-semibold transition-all duration-300 transform hover:scale-105"
              >
                <BookOpen className="h-5 w-5" />
                View Detailed Pricing
                <ExternalLink className="h-4 w-4" />
              </a>
            </div>
          </div>
        </div>
      </section>

      {/* FAQ Section */}
      <section className="py-20 bg-gradient-to-b from-background to-accent/5">
        <div className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center mb-16">
            <h2 className="text-3xl md:text-4xl font-bold text-foreground mb-4">
              Frequently Asked Questions
            </h2>
            <p className="text-xl text-muted-foreground">
              Get answers to common questions about our pricing and plans.
            </p>
          </div>
          
          <div className="space-y-4">
            {faqData.map((faq, index) => (
              <div
                key={index}
                className="bg-card border border-border rounded-xl overflow-hidden transition-all duration-300 hover:shadow-lg"
              >
                <button
                  onClick={() => toggleFaq(index)}
                  className="w-full px-6 py-6 text-left flex justify-between items-center hover:bg-accent/50 transition-all duration-300"
                >
                  <h3 className="font-semibold text-foreground pr-4">{faq.question}</h3>
                  <div className={`transition-transform duration-300 ${openFaq === index ? 'rotate-180' : ''}`}>
                    <ChevronDown className="h-5 w-5 text-muted-foreground" />
                  </div>
                </button>
                
                <div className={`transition-all duration-300 ease-in-out overflow-hidden ${
                  openFaq === index 
                    ? 'max-h-[500px] opacity-100 pb-6' 
                    : 'max-h-0 opacity-0'
                }`}>
                  <div className="px-6 text-muted-foreground">
                    {faq.answer}
                  </div>
                </div>
              </div>
            ))}
          </div>
          
          {/* Help CTA */}
          <div className="mt-12 text-center p-8 bg-gradient-to-r from-[#4379F2]/10 to-[#6EC207]/10 rounded-xl border border-border">
            <h3 className="text-xl font-semibold text-foreground mb-2">Still have questions?</h3>
            <p className="text-muted-foreground mb-6">Our team is here to help you find the perfect plan for your needs.</p>
            <div className="flex flex-col sm:flex-row gap-4 justify-center">
              <Link
                to="/contact"
                className="bg-gradient-to-r from-[#4379F2] to-[#6EC207] hover:from-[#3366E6] hover:to-[#5CAE06] text-white px-6 py-3 rounded-lg font-semibold transition-all duration-300 inline-flex items-center justify-center"
              >
                <MessageSquare className="h-4 w-4 mr-2" />
                Contact Sales
              </Link>
              <a
                href="https://docs.terrascale.tech"
                target="_blank"
                rel="noopener noreferrer"
                className="border border-border hover:bg-accent text-foreground px-6 py-3 rounded-lg font-semibold transition-all duration-300 inline-flex items-center justify-center"
              >
                <BookOpen className="h-4 w-4 mr-2" />
                Read Documentation
              </a>
            </div>
          </div>
        </div>
      </section>

      {/* CTA Section */}
      <section className="py-20 bg-gradient-to-r from-[#4379F2] to-[#6EC207] relative overflow-hidden">
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_30%_40%,rgba(255,255,255,0.1),transparent_50%)]" />
        <div className="absolute inset-0 bg-[radial-gradient(circle_at_70%_60%,rgba(255,255,255,0.05),transparent_50%)]" />
        
        <div className="relative z-10 max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 text-center">
          <h2 className="text-3xl md:text-4xl font-bold text-white mb-4">
            Ready to get started?
          </h2>
          <p className="text-xl text-white/80 mb-8">
            Join thousands of developers building the future with TerraScale.
          </p>
          <Link
            to="/contact"
            className="bg-white text-[#4379F2] hover:bg-gray-50 font-bold py-4 px-8 rounded-xl transition-all duration-300 transform hover:scale-105 inline-flex items-center shadow-lg"
          >
            Start Building Today
            <ArrowRight className="ml-2 h-5 w-5" />
          </Link>
        </div>
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