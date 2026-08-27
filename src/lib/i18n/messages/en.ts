export const en = {
  meta: {
    title: 'TerraScale Public Alpha | Regional Document Database',
    description:
      'TerraScale Public Alpha provides a typed document database with asynchronous replication across São Paulo, San Francisco, and Falkenstein.',
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
    login: 'Log in',
    getStarted: 'Get Started',
    prelaunch: 'Public Alpha Sep 7',
    toggleNav: 'Toggle navigation menu',
    language: {
      trigger: 'Language',
      currentSuffix: 'Language: {label}',
      menu: 'Select a language',
    },
  },

  footer: {
    tagline:
      'TerraScale Public Alpha offers a regional document database without an uptime percentage or SLA.',
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
          security: 'Security',
          support: 'Support',
        },
      },
      legal: {
        title: 'Legal',
        links: {
          terms: 'Terms',
          privacy: 'Privacy',
          acceptableUse: 'Acceptable Use',
          dataProcessingTerms: 'Data Processing Terms',
          support: 'Support',
        },
      },
    },
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
    headlineLead: 'TerraScale',
    headlineAccent: 'Public Alpha',
    subtitle:
      'A typed document database with asynchronous replication across São Paulo, San Francisco, and Falkenstein. The global endpoint routes to the nearest healthy region selected by your project. Public Alpha has no uptime percentage or SLA.',
    ctaPrimary: 'Register for Public Alpha',
    ctaPrelaunch: 'Public Alpha opens Sep 7',
    ctaSecondary: 'View Docs',
    highlightsAria: 'Key capabilities',
    highlights: ['Selected-region routing', 'Asynchronous replication', 'Typed SDKs'],
  },

  features: {
    eyebrow: 'Platform',
    titleLead: 'Public Alpha,',
    titleAccent: 'with explicit boundaries',
    subtitle:
      'The launch contract covers document data, three selectable regions, typed clients, and customer-visible health without an uptime commitment.',
    items: [
      {
        title: 'Typed Data API',
        text: 'Create databases, repositories, schemas, and documents, then query with TQL through one public API.',
        bullets: ['Repositories', 'TQL', 'SDK clients'],
      },
      {
        title: 'Regional Routing',
        text: 'The global endpoint chooses the nearest healthy selected region. Strict regional endpoints never serve from an unselected region.',
        bullets: ['Global endpoint', 'Strict regional endpoints', 'Selected regions'],
      },
      {
        title: 'Launch Regions',
        text: 'Choose São Paulo, San Francisco, or Falkenstein during Public Alpha.',
        bullets: ['sfo', 'fra', 'gru'],
      },
      {
        title: 'Measured Signals',
        text: 'Replication health, convergence, conflicts, API latency, and geographic latency estimates remain separate values. Customer content stays in selected regions. Customer traffic requires TLS 1.2 or newer, data at rest uses qualified encryption, and every request still requires authorization.',
        bullets: ['Observation time', 'Evidence labels', 'No bounded-lag promise'],
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
      'Launch-region inputs for gru, sfo, fra',
    ],
    copy: 'Copy',
    copied: 'Copied',
    copyAria: 'Copy code',
    tablistLabel: 'Language examples',
  },

  cta: {
    headlineLead: 'Register for the',
    headlineAccent: 'TerraScale Public Alpha.',
    text: 'Developer is non-expiring. Starter and Pro have one eligible 30-day trial per verified payment method and organization.',
    ctaPrimary: 'Register for Public Alpha',
    ctaPrelaunch: 'Public Alpha opens Sep 7',
    ctaSecondary: 'Talk to Sales',
    perksAria: 'Plan details',
    perks: ['Non-expiring Developer plan', 'Monthly paid plans', 'No paid overages'],
    policyLead: 'Review before payment verification:',
  },

  pricing: {
    eyebrow: 'Pricing',
    titleLead: 'Simple pricing',
    titleAccent: 'That scales with YOU',
    subtitle: 'Four monthly plans for Public Alpha. Enterprise remains unavailable.',
    badge: 'Most Popular',
    suffixMonthly: '/month',
    plans: {
      developer: {
        description: 'Non-expiring free plan with a verified payment method',
        features: ['1 project', '3 databases', '3 members', '1 GB across regions', '10,000 operation units/month'],
        cta: 'Choose Developer',
      },
      starter: {
        description: 'Monthly plan with one eligible 30-day trial',
        features: ['5 projects', '10 databases', '5 members', '5 GB across regions', '1,000,000 operation units/month'],
        cta: 'Choose Starter',
      },
      pro: {
        description: 'Monthly plan with one eligible 30-day trial',
        features: ['25 projects', '50 databases', '25 members', '50 GB across regions', '10,000,000 operation units/month'],
        cta: 'Choose Pro',
      },
      enterprise: {
        description: 'Future contract plan',
        features: ['Monthly support base plus metered usage', 'Unavailable during Public Alpha'],
        cta: 'Contact for future access',
      },
    },
    triad: [
      { title: 'Storage Across Regions', text: 'Stored bytes count once in every selected region' },
      { title: 'Operation Units', text: 'Each started 4 KiB of document data counts as one unit' },
      { title: 'Hard Allowances', text: 'Reads, exports, deletion, and usage reduction remain available' },
    ],
    detailed: {
      eyebrow: 'Detailed Pricing',
      titleLead: 'Public Alpha',
      titleAccent: 'billing rules',
      text: 'Storage adds bytes in every selected region. Operation units round each document separately. Public Alpha does not offer paid overages.',
      headItem: 'Item',
      headPricing: 'Pricing',
      headDetails: 'Details',
      rows: [
        {
          item: 'Plan allowances',
          unit: '',
          details: 'Each plan includes its listed storage-across-regions and monthly operation-unit allowances.',
        },
        {
          item: 'Allowance behavior',
          unit: '',
          details: 'New billable work stops at the allowance. Reads, exports, deletion, and usage reduction remain available.',
        },
        {
          item: 'Enterprise support base',
          unit: '/month + usage',
          details: 'Enterprise is unavailable during Public Alpha and has no purchase action.',
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
          a: 'Public Alpha uses hard allowances rather than paid overages. Reads, exports, deletion, and usage reduction remain available after new billable work stops.',
        },
        {
          q: 'When can I change my subscription?',
          a: 'Organization Owners can change monthly plans through the Dashboard. Paid plans renew monthly. Cancellation moves excess usage into a 30-day read-only grace period.',
        },
        {
          q: 'Can I export my data and leave anytime?',
          a: 'Yes. Your data is yours. Export through the dashboard or API. Rate limits protect cluster stability, but there is no exit fee for leaving TerraScale.',
        },
        {
          q: 'What does the Enterprise plan include?',
          a: 'Enterprise has a $500 monthly support base plus metered usage, but it is unavailable during Public Alpha.',
        },
        {
          q: 'Does the Developer plan expire?',
          a: 'No. Developer is non-expiring and requires a verified payment method without creating a paid subscription.',
        },
      ],
      footerTitle: 'Still have questions?',
      footerText: 'Developer has best-effort email support. Starter targets two business days. Pro targets one business day.',
      contactSales: 'Contact Sales',
      readDocs: 'Read Documentation',
    },
    finalCta: {
      titleLead: 'Ready to',
      titleAccent: 'get started?',
      text: 'Register through the canonical TerraScale Dashboard flow.',
      cta: 'Register for Public Alpha',
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
    success: 'Thanks! Your message has been sent securely to our team.',
    errorNoForm: 'The form could not be submitted. Please email us directly.',
    errorSend: 'Something went wrong sending your message. Please try again, or email us directly.',
    note: 'Customer traffic requires TLS 1.2 or newer. Data at rest uses service-qualified encryption.',
    channelsTitle: 'Other ways to reach us',
    channels: [
      { value: 'support@terrascale.tech' },
      { value: 'Join our community' },
      { value: 'Open issues & PRs' },
    ],
  },

  about: {
    eyebrow: 'About TerraScale',
    titleLead: 'Globally distributed',
    titleAccent: 'database infrastructure.',
    subtitle:
      'TerraScale Public Alpha provides a document database across São Paulo, San Francisco, and Falkenstein with asynchronous replication.',
    ctaPricing: 'View Pricing',
    ctaContact: 'Talk to Us',
    missionEyebrow: 'Mission',
    missionTitle: 'One platform surface for teams that ship across regions.',
    missionText:
      'Projects select their regions. The global endpoint uses the nearest healthy selected region, while strict regional endpoints never serve from another region.',
    pillars: [
      {
        title: 'TerraBase foundation',
        text: 'TerraScale is built on TerraBase so teams can run durable document, query, and operational data workflows behind one public platform surface.',
      },
      {
        title: 'Asynchronous replication',
        text: 'A successful write confirms local acceptance. Replication to other selected regions happens later.',
      },
      {
        title: 'Usage-aware billing',
        text: 'Plans use explicit storage-across-regions and operation-unit allowances without paid overages during Public Alpha.',
      },
    ],
    pricingPrincipleEyebrow: 'Pricing principle',
    bandTitle: 'Flat regional pricing, metered by real use.',
    bandText:
      'Storage usage adds the bytes stored in every selected region. Estimated network distance, measured API latency, and replication health remain separate values.',
    regions: ['São Paulo', 'San Francisco', 'Falkenstein'],
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
