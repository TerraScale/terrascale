export const en = {
  meta: {
    title: 'TerraScale Public Alpha | Regional Document Database',
    description:
      'TerraScale Public Alpha provides a typed document database with asynchronous replication across São Paulo, the San Francisco Bay Area, and Frankfurt.',
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
    getStarted: 'Start building',
    toggleNav: 'Toggle navigation menu',
    language: {
      trigger: 'Language',
      currentSuffix: 'Language: {label}',
      menu: 'Select a language',
    },
  },

  footer: {
    tagline:
      'TerraScale Public Alpha offers a regional document database without an uptime percentage or SLA. Current availability is published on the status page.',
    rightsReserved: 'All rights reserved.',
    columns: {
      product: {
        title: 'Product',
        links: {
          features: 'Features',
          pricing: 'Pricing',
          changelog: 'Changelog',
          documentation: 'Documentation',
        },
      },
      company: {
        title: 'Company',
        links: {
          about: 'About',
          blog: 'Blog',
          contact: 'Contact',
          terms: 'Terms',
          privacy: 'Privacy',
          dpa: 'DPA',
        },
      },
      resources: {
        title: 'Resources',
        links: {
          docs: 'Docs',
          apiReference: 'API Reference',
          status: 'Status',
          community: 'Community',
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
    eyebrow: 'Public Alpha',
    headlineLead: 'Typed document database,',
    headlineAccent: 'replicated across regions.',
    subtitle:
      'A typed document database with asynchronous replication across São Paulo, the San Francisco Bay Area, and Frankfurt. The global endpoint routes to the nearest healthy region selected by your project. Public Alpha has no uptime percentage or SLA.',
    ctaPrelaunch: 'Public Alpha',
    highlightsAria: 'Key capabilities',
    highlights: ['Selected-region routing', 'Asynchronous replication', 'Typed SDKs', 'AI agents ready'],
  },

  features: {
    eyebrow: 'Platform',
    titleLead: 'Public Alpha,',
    titleAccent: 'with explicit boundaries',
    subtitle:
      'The launch contract covers document data, three serving regions, typed clients, and customer-visible health without an uptime commitment.',
    items: [
      {
        title: 'Data',
        text: 'Databases hold collections of JSON documents, each with an optional schema. Query with JSON filters or read-only SQL: SELECT, WHERE, ORDER BY, and up to 1,000 rows per statement.',
        bullets: ['Collections', 'JSON filters', 'Read-only SQL'],
      },
      {
        title: 'Regions',
        text: 'São Paulo, the San Francisco Bay Area, and Frankfurt serve today. Each database lives in a home region and replicates in full to the other regions you select.',
        bullets: ['gru', 'sjc', 'fra'],
      },
      {
        title: 'Routing',
        text: 'The global endpoint answers from the nearest healthy region that holds your database. Strict regional endpoints never serve from a region you did not select.',
        bullets: ['Global endpoint', 'Strict regional endpoints'],
      },
      {
        title: 'Health',
        text: 'Replication is asynchronous. Lag, convergence, conflicts, and API latency are published as separate measured values with their observation time. There is no bounded-lag promise, uptime percentage, or SLA.',
        bullets: ['Asynchronous replication', 'Observation time', 'No SLA'],
      },
      {
        title: 'Security',
        text: 'Customer traffic requires TLS 1.2 or newer, data at rest uses qualified encryption, and every request requires authorization. Customer content stays in the regions you select.',
        bullets: ['TLS 1.2+', 'Encryption at rest', 'Per-request authorization'],
      },
    ],
  },

  code: {
    eyebrow: 'Developer SDK',
    headlineLead: 'Query your data',
    headlineAccent: 'in the language you love',
    text: 'Typed C# and TypeScript SDKs call the same public HTTP API the dashboard uses, with models generated from its OpenAPI contract.',
    bullets: ['Typed C# and TypeScript clients', 'Generated OpenAPI models', 'JSON filters or read-only SQL'],
    agent: {
      title: 'Or ask your favorite AI agent to do it for you',
      text: 'We support CLI and MCP. Connect Claude Code, Cursor, or any MCP client to the TerraScale MCP server with a personal access token. The agent acts with your roles, and every call is checked again. Agents can also call the HTTP API from a shell.',
      promptsLabel: 'Try asking',
      prompts: [
        'List my databases and the regions each one serves.',
        'Find customers with 10 or more seats in the app database.',
        'How much of this month’s operation-unit allowance have we used?',
      ],
      toolsLabel: 'Some MCP tools',
    },
    copy: 'Copy',
    copied: 'Copied',
    copyAria: 'Copy code',
    tablistLabel: 'Code examples',
  },

  cta: {
    headlineLead: 'Register for the',
    headlineAccent: 'TerraScale Public Alpha.',
    text: 'Starter and Pro have one eligible 30-day trial per verified payment method and organization.',
    ctaPrimary: 'Register for Public Alpha',
    ctaSecondary: 'Talk to Sales',
    perksAria: 'Plan details',
    perks: ['Monthly paid plans', 'No paid overages'],
  },

  pricingTeaser: {
    eyebrow: 'Pricing',
    title: 'Start free. Hard limits, no overages.',
    cta: 'Compare plans',
  },

  pricing: {
    eyebrow: 'Pricing',
    titleLead: 'Simple pricing',
    titleAccent: 'That scales with YOU',
    subtitle: 'Hobby, Starter, and Pro for Public Alpha. Enterprise remains unavailable.',
    badge: 'Most Popular',
    toggleLabel: 'Billing cycle',
    monthly: 'Monthly',
    annual: 'Annual',
    save: 'Two months free',
    suffixMonthly: '/month',
    suffixAnnual: '/year',
    plans: {
      hobby: {
        description: 'Free plan to build and evaluate',
        cta: 'Start with Hobby',
      },
      starter: {
        description: 'Monthly plan with one eligible 30-day trial',
        cta: 'Choose Starter',
      },
      pro: {
        description: 'Monthly plan with one eligible 30-day trial',
        cta: 'Choose Pro',
      },
      enterprise: {
        description: 'Future contract plan',
        features: ['Monthly support base plus metered usage', 'Unavailable during Public Alpha'],
        cta: 'Contact for future access',
      },
    },
    limits: {
      projects: ['{n} project', '{n} projects'],
      databases: ['{n} database', '{n} databases'],
      members: ['{n} member', '{n} members'],
      storage: '{n} across regions',
      operationUnits: '{n} operation units/month',
    },
    usage: { eyebrow: 'How usage is measured', title: 'Storage and operation units.' },
    triad: [
      { title: 'Storage Across Regions', text: 'Stored bytes count once in every selected region' },
      { title: 'Operation Units', text: 'Each started 4 KiB read or written counts as one unit' },
      { title: 'Hard Allowances', text: 'Reads, exports, deletion, and usage reduction remain available' },
    ],
    compare: {
      included: 'Included',
      hardLimit: 'Hard limit',
      eyebrow: 'Compare',
      title: 'Plan limits.',
      head: 'Limit',
      projects: 'Projects',
      databases: 'Databases',
      members: 'Members',
      storage: 'Storage across regions',
      operationUnits: 'Operation units / month',
      trial: '30-day trial',
      trialIncluded: 'One per verified payment method and organization',
      support: 'Support target',
      supportStarter: '2 business days',
      supportPro: '1 business day',
    },
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
      eyebrow: 'FAQ',
      titleLead: 'Frequently Asked',
      titleAccent: 'Questions',
      subtitle: 'Get answers to common questions about our pricing and plans.',
      items: [
        {
          q: 'What is an operation unit?',
          a: 'One operation unit is each started 4 KiB (4,096 bytes) of a logical object successfully read or written. Lists, queries, and batches count each item independently. Writes count once per selected region; reads count only in the serving region. Failed, denied, invalid, throttled, and not-found requests are not billed.',
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
          a: 'Enterprise has a ${enterprisePriceUsd} monthly support base plus metered usage, but it is unavailable during Public Alpha.',
        },
      ],
      footerTitle: 'Still have questions?',
      footerText: 'Starter targets two business days. Pro targets one business day.',
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
    channelsTitle: 'Reach us',
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
      'TerraScale Public Alpha provides a document database across São Paulo, the San Francisco Bay Area, and Frankfurt with asynchronous replication.',
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
  },

  notFound: {
    eyebrow: 'Page Not Found',
    title: 'We can’t find that page.',
    text: 'The page you’re looking for has moved, or never existed. Let’s get you back on track.',
    backHome: 'Back to Home',
    contactSupport: 'Contact Support',
  },
}

export type Messages = typeof en
