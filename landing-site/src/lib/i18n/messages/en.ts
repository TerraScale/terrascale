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
          support: 'Support',
          supportEmail: 'Contact: support@terrascale.tech',
        },
      },
    },
    social: {
      github: 'GitHub',
      linkedin: 'LinkedIn',
      email: 'Email',
    },
  },

  hero: {
    headlineLead: 'Typed document database,',
    headlineAccent: 'replicated across regions.',
    cta: 'Start free',
  },

  features: {
    titleLead: 'What Public Alpha covers,',
    titleAccent: 'and where it stops.',
    items: [
      {
        headline: 'Query JSON documents with filters or SQL',
        text: 'Databases hold collections of JSON documents, each with an optional schema. Query with JSON filters or read-only SQL: SELECT, WHERE, ORDER BY, and up to 1,000 rows per statement.',
      },
      {
        headline: 'Keep each database in the regions you pick',
        text: 'São Paulo, the San Francisco Bay Area, and Frankfurt serve today. Each database lives in a home region and replicates in full to the other regions you select.',
      },
      {
        headline: 'Serve reads from the nearest healthy region',
        text: 'The global endpoint answers from the nearest healthy region that holds your database. Strict regional endpoints never serve from a region you did not select.',
      },
      {
        headline: 'See replication health as measured values',
        text: 'Replication is asynchronous. Lag, convergence, conflicts, and API latency are published as separate measured values with their observation time. There is no bounded-lag promise, uptime percentage, or SLA.',
      },
      {
        headline: 'Encrypted in transit and at rest',
        text: 'Customer traffic requires TLS 1.2 or newer, data at rest uses qualified encryption, and every request requires authorization. Customer content stays in the regions you select.',
      },
    ],
  },

  howItWorks: {
    titleLead: 'From sign up to global reads',
    titleAccent: 'in three steps.',
    steps: [
      {
        title: 'Create a database',
        text: 'Register without a card, create a project, and add a database. Choose its home region: São Paulo, the San Francisco Bay Area, or Frankfurt.',
      },
      {
        title: 'Select replica regions',
        text: 'Add the other regions that should hold a full copy. A write confirms local acceptance, then replicates asynchronously to every region you selected.',
      },
      {
        title: 'Read and write',
        text: 'Use the global endpoint for the nearest healthy selected region, or a strict regional endpoint that never serves from another region. Call it from the C# or TypeScript SDK, the HTTP API, or an MCP client.',
      },
    ],
  },


  homeFaq: {
    titleLead: 'Questions before',
    titleAccent: 'you start.',
    items: [
      {
        q: 'What is TerraScale?',
        a: 'A typed document database. Databases hold collections of JSON documents, each with an optional schema. You query them with JSON filters or with SQL that only reads: SELECT, WHERE, ORDER BY, and up to 1,000 rows per statement.',
      },
      {
        q: 'Which regions serve today?',
        a: 'São Paulo (gru), the San Francisco Bay Area (sjc), and Frankfurt (fra). Each database lives in a home region and replicates in full to the other regions you select.',
      },
      {
        q: 'How does replication work?',
        a: 'Replication is asynchronous. A successful write confirms local acceptance, and the other selected regions receive it later. Lag, convergence, conflicts, and API latency are published as separate measured values with their observation time.',
      },
      {
        q: 'What is the difference between the global and regional endpoints?',
        a: 'The global endpoint answers from the nearest healthy region that holds your database. A strict regional endpoint never serves from a region you did not select.',
      },
      {
        q: 'Is there an uptime commitment?',
        a: 'Not yet. Public Alpha has no uptime percentage, bounded lag promise, or SLA. Current availability is published on the status page.',
      },
      {
        q: 'Which languages and tools can I use?',
        a: 'Typed C# and TypeScript SDKs call the same public HTTP API the dashboard uses. You can also use the CLI, or connect Claude Code, Cursor, or any MCP client to the TerraScale MCP server with a personal access token.',
      },
      {
        q: 'How is my data protected?',
        a: 'Customer traffic requires TLS 1.2 or newer, data at rest uses qualified encryption, and every request requires authorization. Customer content stays in the regions you select.',
      },
      {
        q: 'What does it cost to start?',
        a: 'Free requires no card. Starter, Pro, and Team are billed monthly or yearly. Operation units over a paid allowance go on the next invoice; storage and client egress overages are not billed yet.',
      },
      {
        q: 'Can I export my data and leave?',
        a: 'Yes. Export through the dashboard or API at any time. There is no exit fee for leaving TerraScale.',
      },
    ],
    contactSales: 'Contact us',
  },

  code: {
    headlineLead: 'Query your data',
    headlineAccent: 'in the language you love',
    bullets: ['Typed C# and TypeScript clients', 'Generated OpenAPI models', 'JSON filters or read-only SQL'],
    agent: {
      title: 'Or ask your favorite AI agent to do it for you',
      text: 'We support CLI and MCP. Connect Claude Code, Cursor, or any MCP client to the TerraScale MCP server with a personal access token. The agent acts with your roles, and every call is checked again. Agents can also call the HTTP API from a shell.',
      prompts: [
        'List my databases and the regions each one serves.',
        'Find customers with 10 or more seats in the app database.',
        'How much of this month’s operation-unit allowance have we used?',
      ],
    },
    copy: 'Copy',
    copied: 'Copied',
    copyAria: 'Copy code',
    tablistLabel: 'Code examples',
  },

  cta: {
    headlineLead: 'Register for the',
    headlineAccent: 'TerraScale Public Alpha.',
    ctaPrimary: 'Register for Public Alpha',
  },

  pricingTeaser: {
    title: 'Start Free. Compare subscription plans.',
    cta: 'Compare plans',
  },

  pricing: {
    titleLead: 'Simple pricing',
    titleAccent: 'That scales with YOU',
    suffixMonthly: '/month',
    suffixAnnual: '/year',
    intervals: {
      label: 'Billing interval',
      month: 'Monthly',
      year: 'Yearly',
    },
    plans: {
      free: {
        cta: 'Start Free',
      },
      starter: {
        cta: 'Choose Starter',
      },
      pro: {
        cta: 'Choose Pro',
      },
      team: {
        cta: 'Choose Team',
      },
      enterprise: {
        features: ['Contact sales for terms', 'Custom limits and support response target'],
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
        text: 'Operation units over a paid allowance go on the next invoice; storage and client egress overages are not billed yet',
      },
    ],
    compare: {
      title: 'Plan allowances.',
      head: 'Allowance',
      projects: 'Projects',
      databases: 'Databases',
      members: 'Members',
      storage: 'Storage across regions (monthly average)',
      operationUnits: 'Operation units / month',
      egress: 'Client egress / month',
      billing: 'Billing cadence',
      entitlementsHead: 'Entitlements',
      requestsPerSecond: 'Requests / second',
      burstRequests: 'Burst requests',
      concurrentRequests: 'Concurrent requests',
      queryTimeout: 'Query timeout',
      storageLimit: 'Storage limit',
      pitr: 'Point-in-time recovery',
      backupRetention: 'Backup retention',
      regionsPerDatabase: 'Regions per database',
      support: 'Support response target',
      noCeiling: 'No plan ceiling',
      seconds: '{n} s',
      days: ['{n} day', '{n} days'],
      supportTargets: {
        Community: 'Community',
        '2 business days': '2 business days',
        '1 business day': '1 business day',
        '8 hours': '8 hours',
        Custom: 'Custom',
      },
      entitlementsNote: 'Request rates and concurrency apply per organization; requests beyond them receive HTTP 429 with a Retry-After header. The storage limit is a hard ceiling, separate from the included allowance. Support response targets are not an SLA.',
    },
    detailed: {
      titleLead: 'Approved',
      titleAccent: 'usage rates',
      text: 'Paid plans include the allowances. Operation units over the allowance are added to the next subscription invoice at the rate below; storage and client egress overages are not billed yet. Free stops new writes at its operation allowance and has no overage charges.',
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
      titleLead: 'Frequently Asked',
      titleAccent: 'Questions',
      items: [
        {
          q: 'What is an operation unit?',
          a: 'One operation unit is each started 4 KiB (4,096 bytes) of a logical object successfully read or written. Lists, queries, and batches count each item independently. Writes count once regardless of the number of selected regions; reads count only in the serving region. Replication between regions is not billed. Failed, denied, invalid, throttled, and not-found requests are not billed.',
        },
        {
          q: 'What if I need more operations, storage, or egress?',
          a: 'Paid plans include monthly allowances. Operation units over the allowance are added to the next subscription invoice at the overage rate; storage and client egress overages are not billed yet. Free stops new writes at its operation allowance and has no overage charges.',
        },
        {
          q: 'How are subscriptions billed?',
          a: 'Free requires no card. Starter, Pro, and Team are billed monthly or yearly; a year costs ten monthly payments, so yearly billing saves two months. No trials are offered.',
        },
        {
          q: 'Can I export my data and leave anytime?',
          a: 'Yes. Your data is yours. Export through the dashboard or API. Rate limits protect cluster stability, but there is no exit fee for leaving TerraScale.',
        },
        {
          q: 'What does Enterprise include?',
          a: 'Enterprise has a ${enterprisePriceUsd} monthly support base plus usage. Contact sales for terms; Enterprise is not sold through checkout.',
        },
      ],
      contactSales: 'Contact Sales',
    },
    finalCta: {
      titleLead: 'Ready to',
      titleAccent: 'get started?',
      cta: 'Register for Public Alpha',
    },
    cadence: {
      free: 'No card required',
      year: 'Billed yearly · {monthly}/month equivalent · {months} months free',
      month: 'Billed monthly',
      both: '{monthly}/month or {annual}/year',
      contact: 'Support + usage · contact sales',
    },
    suffixUsage: '/month + usage',
  },

  contact: {
    titleLead: 'Let’s build',
    titleAccent: 'something together.',
  },

  about: {
    titleLead: 'Globally distributed',
    titleAccent: 'database infrastructure.',
    ctaPricing: 'View Pricing',
    regionsAria: 'Serving regions',
    missionTitle: 'One platform surface for teams that ship across regions.',
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
        text: 'Free requires no card. Paid plans bill operation units over the allowance on the next invoice; storage and client egress overages are not billed yet.',
      },
    ],
    bandTitle: 'Flat regional pricing, metered by real use.',
    bandText:
      'Storage usage adds the bytes stored in every selected region. Estimated network distance, measured API latency, and replication health remain separate values.',
  },

  notFound: {
    title: 'We can’t find that page.',
    backHome: 'Back to Home',
  },
}

export type Messages = typeof en
