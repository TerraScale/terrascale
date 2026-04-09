<script lang="ts">
  import { fly, scale, slide } from 'svelte/transition'
  import ContactModal from '@/components/ContactModal.svelte'
  import Icon from '@/components/Icon.svelte'
  import RouteLink from '@/components/RouteLink.svelte'
  import ScrollReveal from '@/components/ScrollReveal.svelte'
  import type { AppIconName } from '@/lib/icons'

  type BillingPeriod = 'monthly' | 'annual'
  type Plan = {
    name: string
    icon: AppIconName
    iconClass: string
    ctaIcon: AppIconName
    price?: { monthly: number; annual: number }
    basePrice?: number
    popular: boolean
    description: string
    trial?: string
    features: string[]
    cta: string
    href: string | null
    detailsHref?: string
    detailsLabel?: string
  }

  type EnterprisePricingRow = {
    item: string
    pricing: string
    details: string
  }

  let billingPeriod: BillingPeriod = 'monthly'
  let openFaq: number | null = null
  let isContactModalOpen = false

  const plans: Plan[] = [
    {
      name: 'Hobby',
      icon: 'zap',
      iconClass: 'text-[#10B981] bg-[#10B981]/10 border-[#10B981]/30',
      ctaIcon: 'arrowRight',
      price: { monthly: 4.99, annual: 49 },
      popular: false,
      description: 'Great for personal projects',
      trial: '30-day free trial',
      features: [
        'Databases: 3 included',
        'Database Operations: 10M included',
        'Storage: 10 GB',
        'Egress Bandwidth: 15 GB included + usage',
        'Support: Priority Discord',
      ],
      cta: 'Start Free Trial',
      href: '/contact',
      detailsHref: '#detailed-pricing',
      detailsLabel: 'See pricing details',
    },
    {
      name: 'Pro',
      icon: 'trendingUp',
      iconClass: 'text-[#0891B2] bg-[#0891B2]/10 border-[#0891B2]/30',
      ctaIcon: 'arrowRight',
      price: { monthly: 24.99, annual: 249 },
      popular: true,
      description: 'Best for growing teams',
      trial: '30-day free trial',
      features: [
        'Databases: 15 included',
        'Database Operations: 1B included',
        'Storage: 50 GB',
        'Egress Bandwidth: 100 GB included + usage',
        'Support: Email + Discord',
      ],
      cta: 'Start Free Trial',
      href: '/contact',
      detailsHref: '#detailed-pricing',
      detailsLabel: 'See pricing details',
    },
    {
      name: 'Enterprise',
      icon: 'shieldCheck',
      iconClass: 'text-[#8B5CF6] bg-[#8B5CF6]/10 border-[#8B5CF6]/30',
      ctaIcon: 'messageSquare',
      basePrice: 499,
      popular: false,
      description: 'For mission-critical applications',
      features: [
        'Databases: Unlimited',
        'Everything in Pro',
        'Dedicated support team',
        'Custom SLA agreements',
        'On-premise deployment options',
        'Advanced security features',
        'Custom integrations',
      ],
      cta: 'Contact Us',
      href: null,
      detailsHref: '#detailed-pricing',
      detailsLabel: 'See pricing details',
    },
  ]

  const enterprisePricingRows: EnterprisePricingRow[] = [
    {
      item: 'Enterprise Level Support',
      pricing: '499/month',
      details: 'Base platform access for Enterprise deployments and advanced support.',
    },
    {
      item: 'Storage Usage',
      pricing: '1/GB',
      details: 'Usage-based storage pricing on top of the base platform fee.',
    },
    {
      item: 'Database Operations',
      pricing: '1 / 1M ops',
      details: 'Scales with workload volume for read, write, and replication activity.',
    },
  ]

  const faqData = [
    {
      question: 'What is a Database Operation?',
      intro: ['A database operation refers to any interaction with your database. This includes:'],
      bullets: [
        {
          label: 'Read Operations',
          text: 'When you retrieve data from database, it counts as one read operation.',
        },
        {
          label: 'Write Operations',
          text: 'When you save or modify data in database, it counts as one write operation.',
        },
        {
          label: 'Replication Operations',
          text: 'If your database is configured to replicate data across multiple regions, each time an object is written or updated, it incurs an operation for the initial write plus one operation for each region it replicates to.',
        },
      ],
    },
    {
      question: 'What if I need more operations or storage?',
      intro: [
        "You can easily upgrade your plan at any time from your account dashboard. For instance, if you're on the Pro plan and need more than 50 GB of storage or 1 billion operations, you can upgrade to the Pay-as-you-go Enterprise plan. This lets you scale your resources to fit your needs as your project grows.",
      ],
    },
    {
      question: 'When can I change my subscription?',
      intro: ['You can change your subscription anytime you want.'],
      bullets: [
        {
          label: 'Upgrading',
          text: "When you upgrade, your new plan's features will be available immediately. We'll adjust the cost for your current billing cycle on a prorated basis.",
        },
        {
          label: 'Downgrading',
          text: 'If you choose to downgrade, your current plan will stay active until the end of your billing cycle. Your new, lower-cost plan will then start at the beginning of the next cycle.',
        },
      ],
      outro: [
        "Please note that we generally don't offer partial refunds for downgrades. However, we're happy to review special cases, so feel free to contact our support team to discuss your specific situation.",
      ],
    },
    {
      question: 'Do you offer custom enterprise solutions?',
      intro: [
        'Yes! We offer custom enterprise solutions including dedicated instances, private cloud deployments, custom SLAs, and specialized support. Contact our sales team to discuss your specific requirements.',
      ],
    },
    {
      question: 'Why do you require a credit card for the free trial?',
      intro: [
        "We require a credit card to prevent spam and abuse of our platform. This helps us maintain high-quality service for all users. You won't be charged during the 30-day trial period, and you can cancel anytime before the trial ends.",
      ],
    },
  ]

  const additionalFeatures: Array<{ icon: AppIconName; title: string; description: string }> = [
    {
      icon: 'shield',
      title: 'Enterprise Security',
      description: 'SOC 2 compliant with end-to-end encryption',
    },
    {
      icon: 'zap',
      title: 'Lightning Fast',
      description: 'Sub-10ms query latency worldwide',
    },
    {
      icon: 'trendingUp',
      title: 'Auto-scaling',
      description: 'Scales automatically with your traffic',
    },
  ]

  const toggleFaq = (index: number) => {
    openFaq = openFaq === index ? null : index
  }

  const formatPrice = (price: number) =>
    billingPeriod === 'monthly' ? `$${price}/mo` : `$${price}/yr`
</script>

<section class="bg-[#050508] pb-12 pt-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="mx-auto max-w-4xl text-center">
      <ScrollReveal>
        <h1 class="mb-6 text-4xl font-bold tracking-tight text-[#F8FAFC] md:text-6xl">
          Simple Pricing
        </h1>
      </ScrollReveal>
      <ScrollReveal delay={0.1}>
        <p class="mb-8 text-xl leading-relaxed text-[#94A3B8] md:text-2xl">
          30-day free trial on all plans. No hidden fees, no surprises.
        </p>
      </ScrollReveal>

      <ScrollReveal delay={0.2}>
        <div class="inline-flex items-center p-1.5 ts-glass-frosted ts-rounded-lg">
          <div class="relative grid grid-cols-2">
            <div
              class="pointer-events-none absolute inset-y-0 left-0 m-1.5 w-[calc(50%-0.375rem)] bg-[#2563EB] shadow-offset-blue ts-rounded-sm transition-transform duration-300 ease-out"
              style:transform={billingPeriod === 'annual' ? 'translateX(100%)' : 'translateX(0)'}
            ></div>

            <button
              type="button"
              class={`relative z-10 px-8 py-4 text-lg font-bold transition-colors duration-200 ts-rounded-sm ${
                billingPeriod === 'monthly'
                  ? 'text-[#F8FAFC]'
                  : 'text-[#94A3B8] hover:text-[#F8FAFC]'
              }`}
              on:click={() => {
                billingPeriod = 'monthly'
              }}
            >
              Monthly
            </button>

            <button
              type="button"
              class={`relative z-10 px-8 py-4 text-base font-bold transition-colors duration-200 ts-rounded-sm ${
                billingPeriod === 'annual'
                  ? 'text-[#F8FAFC]'
                  : 'text-[#94A3B8] hover:text-[#F8FAFC]'
              }`}
              on:click={() => {
                billingPeriod = 'annual'
              }}
            >
              Annual
              {#if billingPeriod !== 'annual'}
                <span
                  class="absolute -right-2 -top-2 bg-[#047857] px-2.5 py-1 text-xs font-bold text-[#F8FAFC] ts-rounded-sm"
                  transition:scale={{ duration: 150, start: 0.9 }}
                >
                  Save 20%
                </span>
              {/if}
            </button>
          </div>
        </div>
      </ScrollReveal>
    </div>
  </div>
</section>

<section class="bg-[#050508] pb-8 pt-24">
  <div class="mx-auto max-w-[1440px] px-4 sm:px-6 lg:px-8">
    <div class="grid grid-cols-1 gap-6 md:grid-cols-3">
      {#each plans as plan, index (plan.name)}
        <ScrollReveal delay={index * 0.08}>
          <div
            class={`relative flex h-full ts-glass ts-rounded-lg ${plan.popular ? 'border-2 border-[#0891B2]' : ''}`}
          >
            {#if plan.popular}
              <div
                class="absolute -top-4 left-1/2 -translate-x-1/2 bg-[#0F3D66] px-4 py-2 text-sm font-bold text-[#F8FAFC] ts-glow-accent ts-rounded-sm"
                transition:fly={{ y: -10, duration: 180 }}
              >
                <div class="flex items-center">
                  <Icon name="star" className="popular-badge-star mr-1 h-4 w-4" />
                  MOST POPULAR
                </div>
              </div>
            {/if}

            <div class="flex h-full w-full flex-col p-8">
              <div class="mb-6 text-center">
                <div class="mb-4 grid grid-cols-[3.5rem_1fr_3.5rem] items-center gap-3">
                  <div
                    class={`flex h-14 w-14 items-center justify-center border ts-rounded-sm ${plan.iconClass}`}
                  >
                    <Icon name={plan.icon} className="h-7 w-7" />
                  </div>
                  <h3 class="text-[3.15rem] font-bold leading-none tracking-tight text-[#F8FAFC]">
                    {plan.name}
                  </h3>
                  <div class="h-14 w-14" aria-hidden="true"></div>
                </div>
                <p class="text-sm text-[#94A3B8]">{plan.description}</p>
                {#if 'trial' in plan}
                  <span class="mt-2 inline-block bg-[#10B981]/10 px-3 py-1 text-xs font-bold text-[#10B981] ts-rounded-sm">
                    {plan.trial}
                  </span>
                {/if}
              </div>

              <div class="mb-6 text-center">
                {#if 'price' in plan}
                  <div transition:fly={{ y: 10, duration: 180 }}>
                    <span class="text-4xl font-bold ts-text-gradient md:text-5xl">
                      {formatPrice(plan.price![billingPeriod])}
                    </span>
                    <span class="ml-2 text-sm text-[#94A3B8]">
                      {billingPeriod === 'annual' ? '/year' : '/month'}
                    </span>
                    {#if plan.detailsHref}
                      <div class="mt-3">
                        <a
                          href={plan.detailsHref}
                          class="inline-flex items-center gap-2 text-sm font-semibold text-[#2563EB] transition-colors duration-200 hover:text-[#60A5FA]"
                        >
                          <span>{plan.detailsLabel}</span>
                          <Icon name="chevronRight" className="h-4 w-4" />
                        </a>
                      </div>
                    {/if}
                  </div>
                {:else}
                  <div class="text-center">
                    <span class="text-3xl font-bold ts-text-gradient">Custom</span>
                    <p class="mt-1 text-sm text-[#94A3B8]">
                      Tailored pricing for scale, compliance, and dedicated support.
                    </p>
                    {#if plan.detailsHref}
                      <a
                        href={plan.detailsHref}
                        class="mt-3 inline-flex items-center gap-2 text-sm font-semibold text-[#8B5CF6] transition-colors duration-200 hover:text-[#A78BFA]"
                      >
                        <span>{plan.detailsLabel}</span>
                        <Icon name="chevronRight" className="h-4 w-4" />
                      </a>
                    {/if}
                  </div>
                {/if}
              </div>

              <ul class="mb-8 flex-1 space-y-4">
                {#each plan.features as feature, featureIndex (`${plan.name}-${feature}`)}
                  <li
                    class="flex items-start space-x-3"
                    transition:fly={{ x: -10, duration: 180, delay: featureIndex * 30 }}
                  >
                    <div class="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center bg-[#10B981] ts-rounded-sm">
                      <Icon name="check" className="h-3 w-3 text-[#050508]" />
                    </div>
                    <span class="text-sm text-[#94A3B8]">{feature}</span>
                  </li>
                {/each}
              </ul>

              {#if plan.href}
                <RouteLink
                  href={plan.href}
                  class={`mt-auto flex min-h-[4.25rem] w-full items-center justify-center px-6 py-4 text-center font-bold transition-all duration-200 ts-rounded-md ts-pressable ${
                    plan.popular
                      ? 'bg-[#0891B2] text-[#050508] ts-glow-accent hover:bg-[#0E7490]'
                      : 'ts-btn-primary'
                  }`}
                >
                  <span class="inline-flex items-center justify-center gap-2">
                    <span>{plan.cta}</span>
                    <Icon name={plan.ctaIcon} className="h-4 w-4" />
                  </span>
                </RouteLink>
              {:else}
                <button
                  type="button"
                  class={`mt-auto flex min-h-[4.25rem] w-full items-center justify-center px-6 py-4 font-bold transition-all duration-200 ts-rounded-md ts-pressable ${
                    plan.popular
                      ? 'bg-[#0891B2] text-[#050508] ts-glow-accent hover:bg-[#0E7490]'
                      : 'ts-btn-primary'
                  }`}
                  on:click={() => {
                    isContactModalOpen = true
                  }}
                >
                  <span class="inline-flex items-center justify-center gap-2">
                    <span>{plan.cta}</span>
                    <Icon name={plan.ctaIcon} className="h-4 w-4" />
                  </span>
                </button>
              {/if}
            </div>
          </div>
        </ScrollReveal>
      {/each}
    </div>

    <div class="mt-16 grid grid-cols-1 gap-6 md:grid-cols-3">
      {#each additionalFeatures as feature, index (feature.title)}
        <ScrollReveal delay={0.15 + index * 0.08}>
          <div class="p-6 text-center transition-colors duration-200 hover:border-[#2563EB] ts-glass ts-rounded-lg">
            <div class="mb-4 flex justify-center">
              <Icon name={feature.icon} className="h-12 w-12 text-[#2563EB]" />
            </div>
            <h3 class="mb-2 font-bold text-[#F8FAFC]">{feature.title}</h3>
            <p class="text-sm text-[#94A3B8]">{feature.description}</p>
          </div>
        </ScrollReveal>
      {/each}
    </div>

    <ScrollReveal delay={0.2}>
      <div
        id="detailed-pricing"
        class="mt-16 scroll-mt-24 overflow-hidden transition-colors duration-200 hover:border-[#8B5CF6] ts-glass-frosted ts-rounded-lg"
      >
        <div class="border-b border-[#1A1A24] px-6 py-6 md:px-8">
          <div class="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
            <div>
              <div class="mb-3 inline-flex items-center gap-2 bg-[#8B5CF6]/10 px-3 py-1 text-xs font-bold uppercase tracking-[0.18em] text-[#C4B5FD] ts-rounded-sm">
                <Icon name="dollarSign" className="h-4 w-4" />
                Detailed Pricing
              </div>
              <h3 class="text-2xl font-bold text-[#F8FAFC] md:text-3xl">
                Clear pricing for custom deployments
              </h3>
              <p class="mt-2 max-w-3xl text-sm text-[#94A3B8] md:text-base">
                Usage-based costs, support scopes, and deployment requirements are broken out below so enterprise teams can estimate spend before talking to sales.
              </p>
            </div>
            <a
              href="https://docs.terrascale.tech/pricing"
              target="_blank"
              rel="noopener noreferrer"
              class="ts-btn-glass ts-rounded-md inline-flex items-center justify-center gap-2 px-4 py-3 font-bold ts-pressable"
            >
              <Icon name="bookOpen" className="h-4 w-4" />
              <span>Full Pricing Docs</span>
              <Icon name="externalLink" className="h-4 w-4" />
            </a>
          </div>
        </div>

        <div class="grid gap-4 p-4 sm:p-6 md:hidden">
          {#each enterprisePricingRows as row (row.item)}
            <div class="border border-[#1A1A24] bg-[#0F0F16]/70 p-4 ts-rounded-lg">
              <div class="mb-3 flex items-start justify-between gap-3">
                <div>
                  <div class="text-[11px] font-bold uppercase tracking-[0.16em] text-[#64748B]">
                    Item
                  </div>
                  <div class="mt-1 font-semibold text-[#F8FAFC]">{row.item}</div>
                </div>
                <span class="inline-flex shrink-0 items-center gap-2 bg-[#8B5CF6]/10 px-3 py-1.5 font-semibold text-[#C4B5FD] ts-rounded-sm">
                  <Icon name="dollarSign" className="h-4 w-4" />
                  {row.pricing}
                </span>
              </div>
              <div class="text-[11px] font-bold uppercase tracking-[0.16em] text-[#64748B]">
                Details
              </div>
              <p class="mt-1 text-sm leading-relaxed text-[#94A3B8]">{row.details}</p>
            </div>
          {/each}
        </div>

        <div class="hidden overflow-x-auto md:block">
          <table class="min-w-full border-collapse">
            <thead>
              <tr class="border-b border-[#1A1A24] bg-[#0F0F16]/80">
                <th class="px-6 py-4 text-left text-xs font-bold uppercase tracking-[0.16em] text-[#64748B] md:px-8">
                  Item
                </th>
                <th class="px-6 py-4 text-left text-xs font-bold uppercase tracking-[0.16em] text-[#64748B] md:px-8">
                  Pricing
                </th>
                <th class="px-6 py-4 text-left text-xs font-bold uppercase tracking-[0.16em] text-[#64748B] md:px-8">
                  Details
                </th>
              </tr>
            </thead>
            <tbody>
              {#each enterprisePricingRows as row, index (row.item)}
                <tr
                  class={`${index % 2 === 0 ? 'bg-[#0B0B12]' : 'bg-[#11111A]/80'} ${
                    index < enterprisePricingRows.length - 1 ? 'border-b border-[#1A1A24]' : ''
                  }`}
                >
                  <td class="px-6 py-5 align-top md:px-8">
                    <div class="font-semibold text-[#F8FAFC]">{row.item}</div>
                  </td>
                  <td class="px-6 py-5 align-top md:px-8">
                    <span class="inline-flex items-center gap-2 font-semibold text-[#8B5CF6]">
                      <Icon name="dollarSign" className="h-4 w-4" />
                      {row.pricing}
                    </span>
                  </td>
                  <td class="px-6 py-5 align-top text-sm leading-relaxed text-[#94A3B8] md:px-8">
                    {row.details}
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
        </div>
      </div>
    </ScrollReveal>
  </div>
</section>

<section class="bg-[#050508] pb-24 pt-8">
  <div class="mx-auto max-w-4xl px-4 sm:px-6 lg:px-8">
    <ScrollReveal>
      <div class="mb-16 text-center">
        <h2 class="mb-4 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
          Frequently Asked Questions
        </h2>
        <p class="text-xl text-[#94A3B8]">Get answers to common questions about our pricing and plans.</p>
      </div>
    </ScrollReveal>

    <div class="space-y-4">
      {#each faqData as faq, index (faq.question)}
        <ScrollReveal delay={index * 0.05}>
          <div class="overflow-hidden transition-colors duration-200 hover:border-[#2563EB] ts-glass ts-rounded-lg">
            <button
              type="button"
              class="flex w-full items-center justify-between px-6 py-6 text-left transition-colors duration-200 hover:bg-[#0A0A0E]"
              aria-expanded={openFaq === index}
              on:click={() => toggleFaq(index)}
            >
              <h3 class="flex items-center pr-4 font-bold text-[#F8FAFC]">
                {faq.question}
              </h3>
              <Icon
                name="chevronDown"
                className={`h-5 w-5 text-[#94A3B8] transition-transform duration-300 ${
                  openFaq === index ? 'rotate-180' : ''
                }`}
              />
            </button>

            {#if openFaq === index}
              <div
                class="space-y-3 border-t border-[#1A1A24] px-6 pb-6 pt-4 text-[#94A3B8]"
                transition:slide={{ duration: 220 }}
              >
                {#if faq.intro}
                  {#each faq.intro as paragraph (`${faq.question}-${paragraph}`)}
                    <p>{paragraph}</p>
                  {/each}
                {/if}

                {#if faq.bullets}
                  <ul class="ml-4 list-none space-y-2">
                    {#each faq.bullets as bullet (bullet.label)}
                      <li class="flex items-start space-x-2">
                        <span class="text-[#10B981]">➜</span>
                        <span>
                          <strong>{bullet.label}:</strong>
                          {bullet.text}
                        </span>
                      </li>
                    {/each}
                  </ul>
                {/if}

                {#if faq.outro}
                  {#each faq.outro as paragraph (`${faq.question}-outro-${paragraph}`)}
                    <p>{paragraph}</p>
                  {/each}
                {/if}
              </div>
            {/if}
          </div>
        </ScrollReveal>
      {/each}
    </div>

    <ScrollReveal delay={0.3}>
      <div class="mt-12 p-8 text-center transition-colors duration-200 hover:border-[#2563EB] ts-glass-frosted ts-rounded-lg">
        <h3 class="mb-2 text-xl font-bold text-[#F8FAFC]">Still have questions?</h3>
        <p class="mb-6 text-[#94A3B8]">
          Our team is here to help you find the perfect plan for your needs.
        </p>
        <div class="flex flex-col justify-center gap-4 sm:flex-row">
          <RouteLink
            href="/contact"
            class="ts-btn-primary ts-rounded-md inline-flex items-center justify-center px-6 py-3 font-bold ts-pressable"
          >
            <Icon name="messageSquare" className="mr-2 h-4 w-4" />
            <span>Contact Sales</span>
          </RouteLink>

          <a
            href="https://docs.terrascale.tech"
            target="_blank"
            rel="noopener noreferrer"
            class="ts-btn-glass ts-rounded-md inline-flex items-center justify-center px-6 py-3 font-bold ts-pressable"
          >
            <Icon name="bookOpen" className="mr-2 h-4 w-4" />
            <span>Read Documentation</span>
          </a>
        </div>
      </div>
    </ScrollReveal>
  </div>
</section>

<section class="relative overflow-hidden bg-[#050508] pb-24 pt-6">
  <ScrollReveal>
    <div class="relative z-10 mx-auto max-w-4xl px-4 text-center sm:px-6 lg:px-8">
      <h2 class="mb-4 text-3xl font-bold tracking-tight text-[#F8FAFC] md:text-4xl">
        Ready to get started?
      </h2>
      <p class="mb-8 text-xl text-[#94A3B8]">
        Join thousands of developers building the future with TerraScale.
      </p>
      <RouteLink
        href="/contact"
        class="ts-btn-primary ts-rounded-md inline-flex items-center px-8 py-4 font-bold shadow-offset-blue ts-pressable"
      >
        <span>Start Building Today</span>
        <Icon name="arrowRight" className="ml-2 h-5 w-5" />
      </RouteLink>
    </div>
  </ScrollReveal>
</section>

<ContactModal open={isContactModalOpen} on:close={() => {
  isContactModalOpen = false
}} />

<style>
  .popular-badge-star :global(path) {
    fill: currentColor;
  }
</style>
