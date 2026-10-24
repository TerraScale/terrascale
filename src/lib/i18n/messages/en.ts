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
          support: 'Support',
          supportEmail: 'Contact: support@terrascale.tech',
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
    text: 'Free requires no card. Paid allowances and usage rates are approved; usage charges are not yet collected.',
    ctaPrimary: 'Register for Public Alpha',
    ctaSecondary: 'Talk to Sales',
    perksAria: 'Plan details',
    perks: ['Free without a card', 'Starter yearly; Pro and Team monthly'],
  },

  pricingTeaser: {
    eyebrow: 'Pricing',
    title: 'Start Free. Compare subscription plans.',
    cta: 'Compare plans',
  },

  pricing: {
    eyebrow: 'Pricing',
    titleLead: 'Simple pricing',
    titleAccent: 'That scales with YOU',
    subtitle: 'Free, Starter, Pro, and Team for Public Alpha. Enterprise requires sales contact and is unavailable for self-service purchase.',
    badge: 'Most Popular',
    suffixMonthly: '/month',
    suffixAnnual: '/year',
    plans: {
      free: {
        description: 'Build and evaluate without a payment card',
        cta: 'Start Free',
      },
      starter: {
        description: 'Annual-only subscription for small projects',
        cta: 'Choose Starter',
      },
      pro: {
        description: 'Monthly subscription for growing projects',
        cta: 'Choose Pro',
      },
      team: {
        description: 'Monthly subscription for teams',
        cta: 'Choose Team',
      },
      enterprise: {
        description: 'Support plus usage, arranged through sales',
        features: ['Contact sales for availability and terms', 'No self-service purchase during Public Alpha'],
        cta: 'Contact sales',
      },
    },
    limits: {
      projects: ['{n} project', '{n} projects'],
      databases: ['{n} database', '{n} databases'],
      members: ['{n} member', '{n} members'],
      storage: '{n} across regions (monthly average)',
      operationUnits: '{n} operation units/month',
      egress: '{n} client egress/month',
    },
    usage: {
      eyebrow: 'How usage is measured',
      title: 'Storage, operations, and client egress.',
    },
    triad: [
      {
        title: 'Storage across regions',
        text: 'Decimal GB in each selected region, averaged over the month',
      },
      {
        title: 'Operation units',
        text: 'Each started 4 KiB read or written; writes count once and replication is free',
      },
      {
        title: 'Allowances then usage',
        text: 'Paid plans include allowances then pay-as-you-go; usage charges are not yet collected',
      },
    ],
    compare: {
      eyebrow: 'Compare',
      title: 'Plan allowances.',
      head: 'Allowance',
      projects: 'Projects',
      databases: 'Databases',
      members: 'Members',
      storage: 'Storage across regions (monthly average)',
      operationUnits: 'Operation units / month',
      egress: 'Client egress / month',
      billing: 'Billing cadence',
    },
    detailed: {
      eyebrow: 'Detailed Pricing',
      titleLead: 'Approved',
      titleAccent: 'usage rates',
      text: 'Paid plans include the allowances, then pay-as-you-go at the rates below. Free has no automatic overages. Usage charges are not yet collected.',
      headItem: 'Item',
      headPricing: 'Pricing',
      headDetails: 'Details',
      rows: [
        {
          item: 'Operation units',
          unit: '/million units',
          details: 'Each started 4 KiB of each successful read or write counts separately. Writes count once regardless of selected regions; reads count in the serving region. Replication is free.',
        },
        {
          item: 'Storage across regions',
          unit: '/GB-region-month',
          details: 'Decimal stored GB in each selected region, averaged over time during the month.',
        },
        {
          item: 'Client egress',
          unit: '/GB',
          details: 'Decimal outbound application response bytes to clients; replication traffic is excluded.',
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
          a: 'One operation unit is each started 4 KiB (4,096 bytes) of a logical object successfully read or written. Lists, queries, and batches count each item independently. Writes count once regardless of the number of selected regions; reads count only in the serving region. Replication between regions is not billed. Failed, denied, invalid, throttled, and not-found requests are not billed.',
        },
        {
          q: 'What if I need more operations, storage, or egress?',
          a: 'Paid plans include monthly allowances followed by pay-as-you-go usage. Allowances and rates are approved; usage charges are not yet collected. Free has no automatic overages.',
        },
        {
          q: 'How are subscriptions billed?',
          a: 'Free requires no card. Starter is billed yearly only; its monthly amount is an equivalent, not a monthly purchase. Pro and Team are monthly. No trials or annual Pro/Team discounts are offered.',
        },
        {
          q: 'Can I export my data and leave anytime?',
          a: 'Yes. Your data is yours. Export through the dashboard or API. Rate limits protect cluster stability, but there is no exit fee for leaving TerraScale.',
        },
        {
          q: 'What does Enterprise include?',
          a: 'Enterprise has a ${enterprisePriceUsd} monthly support base plus usage. Contact sales for availability and terms; self-service purchase is unavailable during Public Alpha.',
        },
      ],
      footerTitle: 'Still have questions?',
      footerText: 'Contact us to confirm availability and enrollment terms.',
      contactSales: 'Contact Sales',
      readDocs: 'Read Documentation',
    },
    finalCta: {
      titleLead: 'Ready to',
      titleAccent: 'get started?',
      text: 'Start with Free, or confirm paid plan enrollment.',
      cta: 'Register for Public Alpha',
    },
    status: 'Subscription prices, allowances, and overage rates are approved. Usage charges are not yet collected.',
    cadence: {
      free: 'No card required',
      year: 'Billed {annual}/year only · {monthly}/month equivalent',
      month: 'Billed monthly',
      contact: 'Support + usage · contact sales',
    },
    suffixUsage: '/month + usage',
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
        text: 'Free requires no card. Paid allowances and usage rates are approved; usage charges are not yet collected.',
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
