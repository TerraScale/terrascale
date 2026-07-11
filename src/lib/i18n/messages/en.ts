// Canonical English message dictionary.
//
// This file is the source of truth for the i18n message SHAPE — the exported
// `Messages` type is what `pt-br.ts` and `es.ts` are checked against, so a
// missing or malformed key in another locale is a compile error (surfaced by
// `npm run check`). Keep the structure flat and grouping-by-section so it is
// easy to mirror across locales.
//
// Deliberately NOT translated:
//   - brand/product names (TerraScale, TerraBase, JetStream, NATS, Stripe),
//   - plan names (Hobby / Pro / Enterprise),
//   - code samples in src/lib/highlight.ts (real source code),
//   - city / region proper nouns (San Francisco, Frankfurt, Guarulhos),
//   - currency amounts and API field identifiers.
// Those stay identical across locales; only natural-language copy moves here.

export const en = {
  meta: {
    title: 'TerraScale | Developer Infrastructure For Data Workflows',
    description:
      'TerraScale is developer-first infrastructure for typed database, auth, queue, and payment workflows across the current launch-region model.',
  },

  chrome: {
    brandHome: 'TerraScale home',
    nav: {
      home: 'Home',
      about: 'About',
      pricing: 'Pricing',
      terms: 'Terms',
      contact: 'Contact',
    },
    docs: 'Docs',
    status: 'Status',
    getStarted: 'Get Started',
    toggleNav: 'Toggle navigation menu',
    language: {
      // `current` is the screen-reader label for the selector trigger,
      // e.g. "Language: English". `menu` labels the popup listbox.
      trigger: 'Language',
      currentSuffix: 'Language: {label}',
      menu: 'Select a language',
    },
  },

  footer: {
    tagline:
      'Empowering teams with secure, scalable database infrastructure. Scale your tech, effortlessly.',
    rightsReserved: 'All rights reserved.',
    columns: {
      product: {
        title: 'Product',
        links: {
          features: 'Features',
          pricing: 'Pricing',
          changelog: 'Changelog',
          roadmap: 'Roadmap',
          documentation: 'Documentation',
        },
      },
      company: {
        title: 'Company',
        links: {
          about: 'About',
          blog: 'Blog',
          contact: 'Contact',
        },
      },
      resources: {
        title: 'Resources',
        links: {
          docs: 'Docs',
          apiReference: 'API Reference',
          status: 'Status',
          community: 'Community',
          support: 'Support',
        },
      },
      legal: {
        title: 'Legal',
        links: {
          privacy: 'Privacy',
          terms: 'Terms',
          sla: 'SLA',
          compliance: 'Compliance',
        },
      },
    },
    // Social aria-labels are brand names; identical across locales.
    social: {
      github: 'GitHub',
      discord: 'Discord',
      x: 'X',
      linkedin: 'LinkedIn',
      email: 'Email',
    },
  },

  hero: {
    brandAlt: 'TerraScale',
    headlineLead: 'Scale your data,',
    headlineAccent: 'effortlessly.',
    subtitle:
      'Typed database, auth, queue, and payment infrastructure for teams building on the TerraScale API.',
    ctaPrimary: 'Start Free',
    ctaSecondary: 'View Docs',
    highlightsAria: 'Key capabilities',
    highlights: ['AI Ready', '99.99% Uptime', '20ms Latency'],
  },

  features: {
    eyebrow: 'Platform',
    titleLead: 'Built for scale,',
    titleAccent: 'designed for developers',
    subtitle:
      'The implemented surface spans data, auth, queues, payments, SDKs, and dashboard workflows.',
    items: [
      {
        title: 'Typed Data API',
        text: 'Create databases, collections, schemas, entities, and SQL queries through one public API.',
        bullets: ['Schema plans', 'SQL explain', 'SDK queries'],
      },
      {
        title: 'Auth And Access',
        text: 'Account, session, passkey, MFA, organization, invitation, and scoped API-key flows share the same platform surface.',
        bullets: ['Passkeys', 'MFA', 'Scoped keys'],
      },
      {
        title: 'Launch Regions',
        text: 'Use the product launch regions San Francisco, Frankfurt, and Guarulhos through dashboard and SDK flows.',
        bullets: ['sfo', 'fra', 'gru'],
      },
      {
        title: 'Queue And Payments',
        text: 'Queue workflows run on JetStream, and Stripe billing flows are processed through TerraScale Payments.',
        bullets: ['NATS JetStream', 'Stripe', 'Webhooks'],
      },
    ],
  },

  code: {
    eyebrow: 'Developer SDK',
    headlineLead: 'Query your data',
    headlineAccent: 'in the language you love',
    text: 'First-class C# and TypeScript SDKs target the same public HTTP routes used by the dashboard, with generated OpenAPI models underneath.',
    bullets: [
      'Generated OpenAPI contract catalog',
      'Typed C# and TypeScript clients',
      'Dashboard uses the public SDK boundary',
      'Launch-region inputs for sfo, fra, gru',
    ],
    copy: 'Copy',
    copied: 'Copied',
    copyAria: 'Copy code',
    tablistLabel: 'Language examples',
  },

  cta: {
    headlineLead: 'Try TerraScale',
    headlineAccent: 'free for 30 days.',
    text: 'Set it up in minutes and get full access to all features of the Hobby and Pro plans. You won’t be charged anything until you decide to keep the service.',
    ctaPrimary: 'Start 30-Day Free Trial',
    ctaSecondary: 'Talk to Sales',
    perksAria: 'Trial details',
    perks: ['Full feature access', 'Cancel anytime', 'No lock-in'],
  },

  pricing: {
    eyebrow: 'Pricing',
    titleLead: 'Simple pricing',
    titleAccent: 'That scales with YOU',
    subtitle: 'Start free, scale on demand. No hidden fees, no surprises.',
    toggleLabel: 'Billing cycle',
    monthly: 'Monthly',
    annual: 'Annual',
    save: 'Save 20%',
    badge: 'Most Popular',
    custom: 'Custom',
    suffixMonthly: '/month',
    suffixAnnual: '/year',
    baseNoteLead: 'starts at',
    baseNoteTrail: '/mo + usage',
    plans: [
      {
        description: 'Great for personal projects and prototypes',
        features: ['3 databases', '1M operations', '10 GB storage', '15 GB egress', 'Priority Discord'],
        cta: 'Try for 30 days',
      },
      {
        description: 'Best for growing teams and production',
        features: [
          '15 databases',
          '10M operations',
          '50 GB storage',
          '100 GB egress',
          'Email + Discord',
          'Priority support',
        ],
        cta: 'Try for 30 days',
      },
      {
        description: 'For mission-critical applications',
        features: ['Unlimited databases', 'Custom SLAs', 'Dedicated support', 'Advanced security', 'Custom integrations'],
        cta: 'Contact Sales',
      },
    ],
    triad: [
      { title: 'Launch Region Controls', text: 'Create databases in sfo, fra, or gru' },
      { title: 'Observable By Default', text: 'OpenTelemetry traces and logs across core workflows' },
      { title: 'Usage Guardrails', text: 'Metered plans with platform-level billing controls' },
    ],
    detailed: {
      eyebrow: 'Detailed Pricing',
      titleLead: 'Clear pricing for custom',
      titleAccent: 'deployments',
      text: 'Usage-based costs, support scopes, and deployment requirements are broken out below so enterprise teams can estimate spend before talking to sales.',
      headItem: 'Item',
      headPricing: 'Pricing',
      headDetails: 'Details',
      rows: [
        {
          item: 'Enterprise Level Support',
          unit: '/month',
          details: 'Base platform access for Enterprise deployments and advanced support.',
        },
        {
          item: 'Storage Usage',
          unit: '/GB',
          details: 'Usage-based storage pricing on top of the base platform fee.',
        },
        {
          item: 'Database Operations',
          unit: '/ 1M ops',
          details: 'Scales with workload volume for read, write, and replication activity.',
        },
      ],
      docsLink: 'Full Pricing Docs',
    },
    faq: {
      titleLead: 'Frequently Asked',
      titleAccent: 'Questions',
      subtitle: 'Get answers to common questions about our pricing and plans.',
      items: [
        {
          q: 'What is a Database Operation?',
          a: 'One Database Operation unit is each started 4 KiB (4,096 bytes) of a logical object successfully read or written. Lists, queries, and batches count each item independently. Writes count once per selected region; reads count only in the serving region. Failed, denied, invalid, throttled, and not-found requests are not billed.',
        },
        {
          q: 'What if I need more operations or storage?',
          a: 'All plans support overage billing at usage rates ($1 per additional GB of storage, $1 per million additional operations). You’ll never be cut off mid-request — we’ll notify you well before you approach limits so you can upgrade or adjust.',
        },
        {
          q: 'When can I change my subscription?',
          a: 'Anytime. Upgrades take effect immediately with prorated billing; downgrades apply at the start of your next billing cycle. No lock-in, no cancellation fees.',
        },
        {
          q: 'Can I export my data and leave anytime?',
          a: 'Yes — your data is yours. Full exports are available anytime through the dashboard or API, and we never charge a cent to get it out. The only constraint is a per-request rate limit so a runaway export job can’t destabilize the cluster for other tenants; there’s no cap on total volume and no exit fee. If TerraScale isn’t working for you, you leave with everything you put in.',
        },
        {
          q: 'Do you offer custom enterprise solutions?',
          a: 'Yes. For teams with specific compliance requirements, custom SLAs, or dedicated infrastructure, our Enterprise plan is fully tailored. Contact sales to scope a deployment that fits your needs.',
        },
        {
          q: 'Why do you require a credit card for the free trial?',
          a: 'We require a valid card to prevent abuse and ensure serious usage of our infrastructure. You won’t be charged during the 30-day trial, and you can cancel anytime. This also makes the transition to a paid plan seamless if you choose to continue.',
        },
      ],
      footerTitle: 'Still have questions?',
      footerText: 'Our team is here to help you find the perfect plan for your needs.',
      contactSales: 'Contact Sales',
      readDocs: 'Read Documentation',
    },
    finalCta: {
      titleLead: 'Ready to',
      titleAccent: 'get started?',
      text: 'Join thousands of developers building the future with TerraScale.',
      cta: 'Start Building Today',
    },
  },

  contact: {
    eyebrow: 'Get in Touch',
    titleLead: 'Let’s build',
    titleAccent: 'something together.',
    subtitle:
      'Have questions? Need a custom plan? Want to integrate with TerraScale? We’d love to hear from you.',
    formTitle: 'Send us a message',
    labels: {
      name: 'Name',
      email: 'Email',
      company: 'Company',
      message: 'Message',
    },
    placeholders: {
      name: 'Mario Belchior',
      email: 'mario@acme.com',
      company: 'Acme Inc.',
      message: 'Tell us about your project, team size, and what you’re hoping to achieve...',
    },
    submit: {
      idle: 'Send Message',
      submitting: 'Sending…',
      success: 'Message Sent!',
      error: 'Try Again',
      badge: 'Message sent successfully',
    },
    success: 'Thanks! Your message has been recorded — we’ll reply within 24h.',
    errorNoForm: 'The form could not be submitted. Please email us directly.',
    errorSend: 'Something went wrong sending your message. Please try again, or email us directly.',
    note: 'Your information is never shared. Replies within 24h.',
    channelsTitle: 'Other ways to reach us',
    channels: [
      { value: 'support@terrascale.tech' },
      { value: 'Join our community' },
      { value: 'Open issues & PRs' },
    ],
    slaHeading: 'Support SLA',
    slaBodyLead: 'Pro customers get',
    slaBodyStrong: 'priority routing',
    slaBodyTrail: 'with 1-hour first response during business hours.',
  },

  about: {
    eyebrow: 'About TerraScale',
    titleLead: 'Globally distributed',
    titleAccent: 'database infrastructure.',
    subtitle:
      'TerraScale exists to make globally distributed DBaaS feel like one coherent product: database, auth, and payments built together on TerraBase with usage-based billing and flat pricing across regions.',
    ctaPricing: 'View Pricing',
    ctaContact: 'Talk to Us',
    missionEyebrow: 'Mission',
    missionTitle: 'One platform surface for teams that ship across regions.',
    missionText:
      'Teams should not have to stitch together a database, identity layer, billing system, and regional deployment plan before they can serve customers. TerraScale brings those concerns into a single developer-facing platform so product teams can start small, pick the regions that matter, and scale without changing their application model.',
    pillars: [
      {
        title: 'TerraBase foundation',
        text: 'TerraScale is built on TerraBase so teams can run durable document, query, and operational data workflows behind one public platform surface.',
      },
      {
        title: 'Auth included',
        text: 'Database access, users, sessions, invitations, and machine credentials are planned as one product boundary instead of separate glue code.',
      },
      {
        title: 'Usage-aware billing',
        text: 'Plans stay predictable while metered usage lets teams grow by workload, not by negotiating region-by-region pricing.',
      },
    ],
    pricingPrincipleEyebrow: 'Pricing principle',
    bandTitle: 'Flat regional pricing, metered by real use.',
    bandText:
      'Region choice should be an architecture decision, not a surprise line item. TerraScale’s public pricing is designed around the same plan prices across launch regions, with storage, operations, and transfer growing with workload.',
    regions: ['San Francisco', 'Frankfurt', 'Guarulhos'],
  },

  notFound: {
    eyebrow: 'Page Not Found',
    title: 'We can’t find that page.',
    text: 'The page you’re looking for has moved, or never existed. Let’s get you back on track.',
    backHome: 'Back to Home',
    contactSupport: 'Contact Support',
    linksLabel: 'Popular pages:',
    links: {
      home: 'Home',
      pricing: 'Pricing',
      contact: 'Contact',
    },
  },
}

export type Messages = typeof en
