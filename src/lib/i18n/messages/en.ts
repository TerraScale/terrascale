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
//   - plan names (Developer / Starter / Pro / Scale / Business),
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
          terms: 'Terms',
          support: 'Contact',
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
    highlights: ['TQL Queries', 'Project Queues', 'Launch Regions'],
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
        text: 'Create databases, repositories, schemas, and documents, then query with TQL through one public API.',
        bullets: ['Repositories', 'TQL', 'SDK clients'],
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
    headlineAccent: 'on the free Developer plan.',
    text: 'Set it up in minutes on the free Developer plan and grow into Starter, Pro, Scale, or Business when you need more capacity. You will not be charged until you choose a paid plan.',
    ctaPrimary: 'Start free',
    ctaSecondary: 'Talk to Sales',
    perksAria: 'Plan details',
    perks: ['Free Developer tier', 'Upgrade anytime', 'No lock-in'],
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
        description: 'Free tier for prototypes and early evaluation',
        features: ['3 databases', '100K operations/month', '10 GB-region storage', '3 team members', 'Auth APIs'],
        cta: 'Start free',
      },
      {
        description: 'Small production workloads with Stripe billing',
        features: ['10 databases', '2M operations/month', '1 GB-region storage', '5 team members', 'Stripe billing'],
        cta: 'Choose Starter',
      },
      {
        description: 'More databases and team capacity',
        features: ['50 databases', '20M operations/month', '5 GB-region storage', '25 team members', '10 regions'],
        cta: 'Choose Pro',
      },
      {
        description: 'Default checkout plan with storage headroom',
        features: ['25 databases', '5M operations/month', '500 GB-region storage', '25 team members', '10 regions'],
        cta: 'Choose Scale',
      },
      {
        description: 'Highest seeded limits for larger organizations',
        features: ['100 databases', '50M operations/month', '5 TB-region storage', '100 team members', '10 regions'],
        cta: 'Choose Business',
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
          item: 'Business plan base',
          unit: '/month',
          details: 'Seeded Business plan starts at $99/month ($990/year) before usage overage.',
        },
        {
          item: 'Storage overage',
          unit: '/GB-region',
          details: 'Paid plans use seed storage overage ($0.70 per GB-region by default).',
        },
        {
          item: 'Operation overage',
          unit: '/ 1M ops',
          details: 'Paid plans use seed operation overage ($1.00 per million operation units).',
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
          a: 'Paid plans support overage at seed rates ($0.70 per GB-region of storage and $1.00 per million operation units). Prefer upgrading when you consistently approach included limits.',
        },
        {
          q: 'When can I change my subscription?',
          a: 'Anytime through Stripe checkout and billing portal sessions on the organization billing routes. Upgrades take effect immediately with Stripe-prorated billing; downgrades follow Stripe period rules.',
        },
        {
          q: 'Can I export my data and leave anytime?',
          a: 'Yes. Your data is yours. Export through the dashboard or API. Rate limits protect cluster stability, but there is no exit fee for leaving TerraScale.',
        },
        {
          q: 'Do you offer larger custom deployments?',
          a: 'Contact sales when you need limits beyond the seeded Business plan, dedicated support, or custom commercial terms.',
        },
        {
          q: 'Do I need a credit card for the free Developer plan?',
          a: 'No. The Developer plan is free. Add a payment method only when you upgrade to a paid plan through Stripe checkout.',
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
