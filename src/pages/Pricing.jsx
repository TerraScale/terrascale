import { useState } from 'react'
import { Check, Star, ChevronDown, ChevronUp } from 'lucide-react'
import { Link } from 'react-router-dom'

const Pricing = () => {
  const [billingPeriod, setBillingPeriod] = useState('monthly') // 'monthly' or 'annual'
  const [openFaq, setOpenFaq] = useState(null)

  const plans = [
    {
      name: 'Trial',
      price: { monthly: 0, annual: 0 },
      popular: false,
      features: [
        'Databases: Unlimited (**)',
        'Database Operations: 1 million operations (*)',
        'Storage: 1 GB (*)',
        'Egress Bandwidth: 1 GB (*)',
        'Support: Low Priority'
      ],
      cta: 'Create account',
      href: '/contact'
    },
    {
      name: 'Hobby',
      price: { monthly: 4.99, annual: 49 },
      popular: false,
      features: [
        'Databases: Unlimited (**)',
        'Database Operations: 10 million included',
        'Storage: 10 GB',
        'Egress Bandwidth: 15 GB',
        'Support: Customer-only Discord'
      ],
      cta: 'Get Hobby',
      href: '/contact'
    },
    {
      name: 'Pro',
      price: { monthly: 24.99, annual: 249 },
      popular: true,
      features: [
        'Databases: Unlimited (**)',
        'Database Operations: 1 billion included',
        'Storage: 50 GB',
        'Egress Bandwidth: 100 GB',
        'Support: Customer-only Discord'
      ],
      cta: 'Get Pro',
      href: '/contact'
    },
    {
      name: 'Enterprise',
      price: { monthly: 99.99, annual: 999 },
      popular: false,
      features: [
        'Databases: Unlimited (**)',
        'Support: Premium',
        'Storage: $0.25 per GB',
        'Egress Bandwidth: $0.05 per GB (***)',
        'Database Operations: $1.00 per 1 billion operations'
      ],
      cta: 'Get Enterprise',
      href: '/contact'
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
            <li><strong>Replication Operations:</strong> If your database is configured to replicate data across multiple regions, each time an object is written or updated, it incurs an operation for the initial write plus one operation for each region it replicates to. For example, if you have a database that replicates to 10 regions, a single write to the database will cost 10 operations (1 for the initial write + 9 for replication).</li>
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
      <section className="section-padding bg-gradient-to-b from-gray-50 to-white">
        <div className="max-w-7xl mx-auto container-padding">
          <div className="text-center max-w-4xl mx-auto">
            <h1 className="text-4xl md:text-6xl font-bold text-gray-900 mb-6">
              Pricing <span className="gradient-text">Plans</span>
            </h1>
            <p className="text-xl md:text-2xl text-gray-600 mb-8">
              Choose the plan that fits your needs. Scale up as you grow.
            </p>
            
            {/* Billing Toggle */}
            <div className="inline-flex items-center bg-gray-100 rounded-lg p-1">
              <button
                onClick={() => setBillingPeriod('monthly')}
                className={`px-6 py-2 rounded-lg font-medium transition-all duration-200 ${
                  billingPeriod === 'monthly'
                    ? 'bg-white text-gray-900 shadow-sm'
                    : 'text-gray-600 hover:text-gray-900'
                }`}
              >
                Monthly
              </button>
              <button
                onClick={() => setBillingPeriod('annual')}
                className={`px-6 py-2 rounded-lg font-medium transition-all duration-200 relative ${
                  billingPeriod === 'annual'
                    ? 'bg-white text-gray-900 shadow-sm'
                    : 'text-gray-600 hover:text-gray-900'
                }`}
              >
                Annual
                {getSavings() && (
                  <span className="absolute -top-2 -right-2 bg-primary-600 text-white text-xs px-2 py-1 rounded-full">
                    {getSavings()}
                  </span>
                )}
              </button>
            </div>
          </div>
        </div>
      </section>

      {/* Pricing Plans */}
      <section className="section-padding">
        <div className="max-w-7xl mx-auto container-padding">
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-8">
            {plans.map((plan, index) => (
              <div
                key={plan.name}
                className={`relative bg-white rounded-2xl shadow-lg hover:shadow-xl transition-shadow duration-300 ${
                  plan.popular ? 'ring-2 ring-primary-500' : 'border border-gray-200'
                }`}
                style={{ animationDelay: `${index * 0.1}s` }}
              >
                {plan.popular && (
                  <div className="absolute -top-4 left-1/2 transform -translate-x-1/2">
                    <div className="bg-primary-600 text-white px-4 py-2 rounded-full text-sm font-semibold flex items-center">
                      <Star className="h-4 w-4 mr-1 fill-current" />
                      POPULAR
                    </div>
                  </div>
                )}
                
                <div className="p-8">
                  <h3 className="text-2xl font-bold text-gray-900 mb-2">{plan.name}</h3>
                  <div className="mb-6">
                    <span className="text-4xl font-bold text-gray-900">
                      {formatPrice(plan.price[billingPeriod])}
                    </span>
                    {plan.price.monthly > 0 && (
                      <span className="text-gray-600 ml-2">
                        {billingPeriod === 'annual' ? '/year' : '/month'}
                      </span>
                    )}
                  </div>
                  
                  <ul className="space-y-3 mb-8">
                    {plan.features.map((feature, featureIndex) => (
                      <li key={featureIndex} className="flex items-start space-x-3">
                        <Check className="h-5 w-5 text-primary-600 flex-shrink-0 mt-0.5" />
                        <span className="text-gray-600 text-sm">{feature}</span>
                      </li>
                    ))}
                  </ul>
                  
                  <Link
                    to={plan.href}
                    className={`w-full block text-center py-3 px-6 rounded-lg font-semibold transition-colors duration-200 ${
                      plan.popular
                        ? 'btn-primary'
                        : 'btn-outline'
                    }`}
                  >
                    {plan.cta}
                  </Link>
                </div>
              </div>
            ))}
          </div>
          
          {/* Plan Notes */}
          <div className="mt-12 text-center">
            <div className="inline-block bg-gray-50 rounded-lg p-6 text-left">
              <h4 className="font-semibold text-gray-900 mb-3">Plan Notes:</h4>
              <ul className="text-sm text-gray-600 space-y-1">
                <li>* Usage limits reset monthly.</li>
                <li>** Unlimited databases per account.</li>
                <li>*** Egress overages billed at $0.05/GB.</li>
              </ul>
            </div>
          </div>
        </div>
      </section>

      {/* FAQ Section */}
      <section className="section-padding bg-gray-50">
        <div className="max-w-4xl mx-auto container-padding">
          <div className="text-center mb-12">
            <h2 className="text-3xl md:text-4xl font-bold text-gray-900 mb-4">
              Frequently Asked Questions
            </h2>
            <p className="text-xl text-gray-600">
              Get answers to common questions about our pricing and plans.
            </p>
          </div>
          
          <div className="space-y-4">
            {faqData.map((faq, index) => (
              <div
                key={index}
                className="bg-white rounded-lg shadow-sm border border-gray-200"
              >
                <button
                  onClick={() => toggleFaq(index)}
                  className="w-full px-6 py-4 text-left flex justify-between items-center hover:bg-gray-50 transition-colors duration-200"
                >
                  <h3 className="font-semibold text-gray-900 pr-4">{faq.question}</h3>
                  {openFaq === index ? (
                    <ChevronUp className="h-5 w-5 text-gray-500 flex-shrink-0" />
                  ) : (
                    <ChevronDown className="h-5 w-5 text-gray-500 flex-shrink-0" />
                  )}
                </button>
                {openFaq === index && (
                  <div className="px-6 pb-4 text-gray-600">
                    {faq.answer}
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* CTA Section */}
      <section className="section-padding bg-gradient-to-r from-primary-600 to-primary-700">
        <div className="max-w-4xl mx-auto container-padding text-center">
          <h2 className="text-3xl md:text-4xl font-bold text-white mb-4">
            Still have questions?
          </h2>
          <p className="text-xl text-primary-100 mb-8">
            Our team is here to help you find the perfect plan for your needs.
          </p>
          <Link
            to="/contact"
            className="bg-white text-primary-600 hover:bg-gray-50 font-semibold py-3 px-8 rounded-lg transition-colors duration-200"
          >
            Contact Sales
          </Link>
        </div>
      </section>
    </>
  )
}

export default Pricing