import { useState } from 'preact/hooks';

export function Pricing() {
  const [annual, setAnnual] = useState(false);
  const plans = [
    {
      name: 'Trial',
      icon: '🚀',
      price: 'Free',
      annualPrice: 'Free',
      features: [
        { label: 'Databases', value: 'Unlimited', note: '**' },
        {
          label: 'Database Operations',
          value: '1 million operations',
          note: '*',
        },
        { label: 'Storage', value: '1 GB', note: '*' },
        { label: 'Egress Bandwidth', value: '1 GB', note: '*' },
        { label: 'Support', value: 'Low Priority' },
      ],
      cta: 'Create account',
      ctaIcon: 'icon-[mdi--account-plus]',
      color: 'bg-base-100',
      border: 'border-base-200',
    },
    {
      name: 'Hobby',
      icon: '🛠️',
      price: '$4.99',
      annualPrice: '$49',
      features: [
        { label: 'Databases', value: 'Unlimited', note: '**' },
        { label: 'Database Operations', value: '10 million included' },
        { label: 'Storage', value: '10 GB' },
        { label: 'Egress Bandwidth', value: '15 GB' },
        { label: 'Support', value: 'Customer-only Discord' },
      ],
      cta: 'Get Hobby',
      ctaIcon: 'icon-[mdi--rocket-launch-outline]',
      color: 'bg-base-100',
      border: 'border-base-200',
    },
    {
      name: 'Pro',
      icon: '💼',
      price: '$24.99',
      annualPrice: '$249',
      features: [
        { label: 'Databases', value: 'Unlimited', note: '**' },
        { label: 'Database Operations', value: '1 billion included' },
        { label: 'Storage', value: '50 GB' },
        { label: 'Egress Bandwidth', value: '100 GB' },
        { label: 'Support', value: 'Customer-only Discord' },
      ],
      cta: 'Get Pro',
      ctaIcon: 'icon-[mdi--star-outline]',
      color: 'bg-gradient-to-br from-primary to-secondary text-white',
      border: 'border-4 border-primary',
      highlight: true,
    },
    {
      name: 'Enterprise',
      icon: '🏢',
      price: '$99.99',
      annualPrice: '$999',
      features: [
        { label: 'Databases', value: 'Unlimited', note: '**' },
        { label: 'Support', value: 'Premium' },
        { label: 'Storage', value: '$0.25 per GB' },
        { label: 'Egress Bandwidth', value: '$0.05 per GB', note: '***' },
        {
          label: 'Database Operations',
          value: '$1.00 per 1 billion operations',
        },
      ],
      cta: 'Get enterprise',
      ctaIcon: 'icon-[mdi--email-outline]',
      color: 'bg-base-100',
      border: 'border-base-200',
    },
  ];

  // Collect all unique features for table rows
  const allFeatures = Array.from(
    new Set(plans.flatMap((plan) => plan.features.map((f) => f.label)))
  );

  return (
    <div className="mx-auto mt-10 max-w-6xl animate-fade-in rounded-3xl bg-gradient-to-br from-primary/10 to-secondary/10 px-4 py-20 shadow-2xl">
      <h1 className="mb-6 animate-fade-in-down bg-gradient-to-r from-primary to-secondary bg-clip-text text-center font-extrabold text-3xl text-transparent sm:mb-10 sm:text-4xl lg:text-5xl">
        Pricing Plans
      </h1>
      <div className="mb-8 flex animate-fade-in-up justify-center sm:mb-12">
        <button
          className={`btn btn-outline mr-2 rounded-full px-4 text-xs sm:mr-4 sm:px-8 sm:text-base ${annual ? '' : 'btn-primary'}`}
          onClick={() => setAnnual(false)}
        >
          Monthly
        </button>
        <button
          className={`btn btn-outline rounded-full px-4 text-xs sm:px-8 sm:text-base ${annual ? 'btn-primary' : ''}`}
          onClick={() => setAnnual(true)}
        >
          Annual{' '}
          <span className="ml-1 text-success text-xs sm:ml-2">(Save 20%)</span>
        </button>
      </div>

      {/* Mobile Card Layout */}
      <div className="mb-8 block animate-fade-in-up lg:hidden">
        <div className="grid gap-6">
          {plans.map((plan, idx) => (
            <div
              className={`card ${plan.color} ${plan.border} shadow-xl ${plan.highlight ? 'ring-2 ring-primary' : ''}`}
              key={plan.name}
            >
              <div className="card-body p-6">
                {/* Plan Header */}
                <div className="mb-4 text-center">
                  <div className="mb-2 text-4xl">{plan.icon}</div>
                  <h3 className="font-bold text-2xl">{plan.name}</h3>
                  {plan.highlight && (
                    <div className="badge badge-primary badge-sm mt-1">
                      POPULAR
                    </div>
                  )}
                </div>

                {/* Price */}
                <div className="mb-6 text-center">
                  <div className="font-extrabold text-3xl">
                    {annual ? plan.annualPrice : plan.price}
                    {plan.price !== 'Free' && (
                      <span className="ml-1 font-normal text-base">
                        {annual ? '/yr' : '/mo'}
                      </span>
                    )}
                  </div>
                </div>

                {/* Features */}
                <div className="mb-6 space-y-3">
                  {plan.features.map((feature, i) => (
                    <div
                      className="flex items-center justify-between border-base-200/50 border-b py-1 last:border-b-0"
                      key={i}
                    >
                      <span className="font-medium text-sm">
                        {feature.label}
                      </span>
                      <span className="text-base-content/80 text-sm">
                        {feature.value}{' '}
                        {feature.note && (
                          <span className="text-base-content/60 text-xs">
                            {feature.note}
                          </span>
                        )}
                      </span>
                    </div>
                  ))}
                </div>

                {/* CTA Button */}
                <a
                  className={`btn btn-lg flex w-full items-center justify-center gap-2 rounded-full ${plan.name === 'Pro' ? 'btn-primary' : plan.name === 'Trial' ? 'btn-outline font-semibold' : 'btn-outline'}`}
                  href="#"
                >
                  <span className={plan.ctaIcon} /> {plan.cta}
                </a>
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Desktop Table Layout */}
      <div className="mb-8 hidden animate-fade-in-up overflow-x-auto lg:block">
        <table className="table-zebra table-lg table w-full rounded-2xl bg-base-100 shadow-xl">
          <thead>
            <tr>
              <th className="bg-base-200" />
              {plans.map((plan, idx) => (
                <th
                  className={`text-center font-bold text-xl ${plan.highlight ? 'bg-gradient-to-br from-primary to-secondary text-white' : 'bg-base-200'}`}
                  key={plan.name}
                  style={
                    plan.highlight
                      ? { borderTop: '4px solid var(--fallback-bc, #570df8)' }
                      : {}
                  }
                >
                  <div className="flex flex-col items-center gap-2">
                    <span className="text-4xl">{plan.icon}</span>
                    <span>{plan.name}</span>
                  </div>
                </th>
              ))}
            </tr>
            <tr>
              <th className="bg-base-200">Price</th>
              {plans.map((plan, idx) => (
                <td
                  className="text-center font-extrabold text-2xl"
                  key={plan.name}
                >
                  {annual ? plan.annualPrice : plan.price}
                  {plan.price !== 'Free' && (
                    <span className="ml-1 font-normal text-base">
                      {annual ? '/yr' : '/mo'}
                    </span>
                  )}
                </td>
              ))}
            </tr>
          </thead>
          <tbody>
            {allFeatures.map((feature, i) => (
              <tr key={feature}>
                <th className="bg-base-200 text-left font-semibold text-base">
                  {feature}
                </th>
                {plans.map((plan, idx) => {
                  const f = plan.features.find((f) => f.label === feature);
                  return (
                    <td
                      className="text-center text-base-content/80"
                      key={plan.name}
                    >
                      {f ? (
                        <span>
                          {f.value}{' '}
                          {f.note && (
                            <span className="text-base-content/60 text-xs">
                              {f.note}
                            </span>
                          )}
                        </span>
                      ) : (
                        <span className="opacity-30">—</span>
                      )}
                    </td>
                  );
                })}
              </tr>
            ))}
            <tr>
              <th className="bg-base-200" />
              {plans.map((plan, idx) => (
                <td className="text-center" key={plan.name}>
                  <a
                    className={`btn btn-lg mt-2 flex w-full items-center justify-center gap-2 rounded-full ${plan.name === 'Pro' ? 'btn-primary' : plan.name === 'Trial' ? 'btn-outline px-8 py-3 font-semibold' : 'btn-outline'}`}
                    href="#"
                  >
                    <span className={plan.ctaIcon} /> {plan.cta}
                  </a>
                </td>
              ))}
            </tr>
          </tbody>
        </table>
      </div>
      <div className="animate-fade-in-up rounded-xl bg-base-100 p-4 text-center text-base-content/70 text-xs shadow-md sm:p-8 sm:text-sm">
        <ul className="list-inside list-disc space-y-1">
          <li>* Usage limits reset monthly.</li>
          <li>** Unlimited databases per account.</li>
          <li>*** Egress overages billed at $0.05/GB.</li>
        </ul>
      </div>

      {/* FAQ Section */}
      <div className="mx-auto mt-12 mb-8 max-w-4xl animate-fade-in-up sm:mt-16">
        <h2 className="mb-4 text-center font-bold text-2xl sm:mb-6 sm:text-3xl">
          Frequently Asked Questions
        </h2>
        <div className="join join-vertical w-full">
          <div className="collapse-arrow join-item collapse border border-base-200 bg-base-100">
            <input type="checkbox" />
            <div className="collapse-title font-medium text-lg sm:text-xl">
              What is a Database Operation?
            </div>
            <div className="collapse-content text-base-content/80 text-sm sm:text-base">
              <p>
                A database operation refers to any interaction with your
                database. This includes:
              </p>
              <ul className="my-2 ml-4 list-disc sm:ml-6">
                <li>
                  <b>Read Operations:</b> When you retrieve data from the
                  database, it counts as one read operation.
                </li>
                <li>
                  <b>Write Operations:</b> When you save or modify data in the
                  database, it counts as one write operation.
                </li>
                <li>
                  <b>Replication Operations:</b> If your database is configured
                  to replicate data across multiple regions, each time an object
                  is written or updated, it incurs an operation for the initial
                  write plus one operation for each region it replicates to. For
                  example, if you have a database that replicates to 10 regions,
                  a single write to the database will cost 10 operations (1 for
                  the initial write + 9 for replication).
                </li>
              </ul>
            </div>
          </div>
          <div className="collapse-arrow join-item collapse border border-base-200 bg-base-100">
            <input type="checkbox" />
            <div className="collapse-title font-medium text-lg sm:text-xl">
              What if I need more operations or storage?
            </div>
            <div className="collapse-content text-base-content/80 text-sm sm:text-base">
              <p>
                You can easily <b>upgrade</b> your plan at any time from your
                account dashboard. For instance, if you're on the Pro plan and
                need more than 50 GB of storage or 1 billion operations, you can
                upgrade to the <b>Pay-as-you-go Enterprise</b> plan. This lets
                you scale your resources to fit your needs as your project
                grows.
              </p>
            </div>
          </div>
          <div className="collapse-arrow join-item collapse border border-base-200 bg-base-100">
            <input type="checkbox" />
            <div className="collapse-title font-medium text-lg sm:text-xl">
              When can I change my subscription?
            </div>
            <div className="collapse-content text-base-content/80 text-sm sm:text-base">
              <p>You can change your subscription anytime you want.</p>
              <ul className="my-2 ml-4 list-disc sm:ml-6">
                <li>
                  <b>Upgrading:</b> When you upgrade, your new plan's features
                  will be available immediately. We'll adjust the cost for your
                  current billing cycle on a prorated basis.
                </li>
                <li>
                  <b>Downgrading:</b> If you choose to downgrade, your current
                  plan will stay active until the end of your billing cycle.
                  Your new, lower-cost plan will then start at the beginning of
                  the next cycle.
                </li>
              </ul>
              <p className="mt-2">
                Please note that we generally don't offer partial refunds for
                downgrades. However, we're happy to review special cases, so
                feel free to contact our support team to discuss your specific
                situation.
              </p>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
