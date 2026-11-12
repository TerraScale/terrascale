export const en = {
  meta: {
    title: 'TerraScale Public Alpha | Regional SQL Database',
    description:
      'TerraScale Public Alpha: SQL databases in three regions, São Paulo, the San Francisco Bay Area and Frankfurt. Every region you choose accepts reads and writes.',
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
    getStarted: 'Create account',
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
          documentation: 'Documentation',
          apiReference: 'API Reference',
          status: 'Status',
        },
      },
      support: {
        title: 'Support',
        links: {
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
    headline: 'Multi-region database',
    cta: 'Create account',
  },

  homeStats: {
    regions: 'Serving regions',
    writeRegion: 'Regions that accept writes',
    statements: 'Statements per transaction',
  },


  features: {
    titleLead: 'SQL databases',
    titleAccent: 'in the regions you choose.',
    items: [
      { headline: 'SQL tables, indexes and foreign keys' },
      { headline: 'psql, HTTP and WebSocket connections' },
      { headline: 'Every region you choose accepts reads and writes' },
      { headline: 'Filtered live shapes with table read grants' },
      { headline: 'Agent databases with TTL, forks and scoped keys' },
      { headline: 'Database deletion destroys backup keys' },
    ],
  },

  howItWorks: {
    titleLead: 'From sign up to your first query',
    titleAccent: 'in three steps.',
    steps: [
      {
        title: 'Create a database',
        text: 'Register without a card, create a project, and add a database in the regions you choose: São Paulo, the San Francisco Bay Area, or Frankfurt.',
      },
      {
        title: 'Create an API key',
        text: 'Limit the key to the database. Use it as the Postgres password, or exchange it for an access token for HTTP and the SDKs.',
      },
      {
        title: 'Run SQL',
        text: 'Create tables and query them from psql, the CLI, the C# or TypeScript SDK, or the HTTP API.',
      },
    ],
  },


  homeFaq: {
    titleLead: 'Questions before',
    titleAccent: 'you start.',
    items: [
      {
        q: 'What is TerraScale?',
        a: 'A SQL database. Each database holds tables, indexes, views, and triggers in the SQLite dialect, with foreign keys always enforced. You reach it over the Postgres wire protocol, HTTP, the CLI, the SDKs, or MCP.',
      },
      {
        q: 'Which regions serve today?',
        a: 'São Paulo (gru), the San Francisco Bay Area (sjc), and Frankfurt (fra). A database lives in the regions you choose, and every one of them accepts reads and writes.',
      },
      {
        q: 'Does every region I choose accept writes?',
        a: 'Yes. Every region of the set accepts reads and writes. Reads are served in the region you call. A write sent to another region is forwarded, or committed locally and confirmed.',
      },
      {
        q: 'Can I use my Postgres tools?',
        a: 'Yes, through the Postgres wire protocol. psql and Postgres drivers connect to pg.terrascale.tech with an API key as the password. Statements use the SQLite dialect, so Postgres-only syntax is refused.',
      },
      {
        q: 'Is there an uptime commitment?',
        a: 'Not yet. Public Alpha has no uptime percentage, latency promise, or SLA. Current availability is published on the status page.',
      },
      {
        q: 'Which languages and tools can I use?',
        a: 'Use psql or any Postgres driver, the typed C# and TypeScript SDKs, the HTTP API, or the CLI. You can also connect Claude Code, Cursor, or any MCP client to the TerraScale MCP server with a personal access token.',
      },
      {
        q: 'How is my data protected?',
        a: 'Customer traffic uses TLS. Database files in every region of the set, and backups, are encrypted at rest. Database deletion destroys its backup data keys so retained encrypted backups cannot be restored. Physical cleanup is a separate step.',
      },
      {
        q: 'What does it cost to start?',
        a: 'Free requires no card. Starter, Pro, and Team are billed monthly or yearly. Operation units over a paid allowance go on the next invoice and storage over it is billed per GB-month; client egress overage is not billed yet.',
      },
      {
        q: 'Can I export my data and leave?',
        a: 'Yes. Export through the dashboard or API at any time. There is no exit fee for leaving TerraScale.',
      },
      { q: 'Can clients subscribe to part of a table?', a: 'Yes. Live shapes support filters and selected columns. Table read grants let end-user tokens read allowed tables; a shape filter does not replace authorization.' },
      { q: 'Can I give an agent its own database?', a: 'Yes. Create a database with a TTL, fork a database, and issue a key scoped to one database with read or read-write access. Expiry deletes the database. Point-in-time restore to a new database is available; in-place restore is coming.' },
      { q: 'Can I cap usage?', a: 'Yes. Plans enforce monthly write caps and SQL read fair use. The write cap stops new writes; the read allowance stops SQL reads while shape reads stay available. Customer-set hard spending caps are coming.' },
    ],
    contactSales: 'Contact us',
  },

  code: {
    headlineLead: 'Query your data',
    headlineAccent: 'in the language you love',
    bullets: ['Typed C# and TypeScript clients', 'Postgres wire protocol for psql and drivers', 'Up to 256 statements in one transaction'],
    agent: {
      title: 'Or ask your favorite AI agent to do it for you',
      text: 'We support CLI and MCP. Connect Claude Code, Cursor, or any MCP client to the TerraScale MCP server with a personal access token. The agent acts with your roles, and every call is checked again. Agents can also call the HTTP API from a shell.',
      prompts: [
        'List my databases and their regions.',
        'Find customers with 10 or more seats in the app database.',
        'How much of this month’s operation-unit allowance have we used?',
      ],
    },
    copy: 'Copy',
    copied: 'Copied',
    copyAria: 'Copy code',
    tablistLabel: 'Code examples',
    sourcePending: '(source)',
  },

  cta: {
    title: 'Ready to get started?',
    ctaPrimary: 'Create account',
  },

  pricingTeaser: {
    title: 'Start Free. Compare subscription plans.',
    cta: 'Compare plans',
  },

  pricing: {
    recommended: 'Recommended',
    titleLead: 'Pricing',
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
      storage: '{n} storage (monthly average)',
      operationUnits: '{n} operation units/month',
      egress: '{n} client egress/month',
    },
    usage: {
      title: 'Storage, operations, and client egress.',
    },
    triad: [
      { title: 'Storage metered once per database' },
      { title: 'Writes counted in 4 KiB units' },
      { title: 'Plan write caps' },
    ],
    compare: {
      title: 'Plan allowances.',
      head: 'Allowance',
      projects: 'Projects',
      databases: 'Databases',
      members: 'Members',
      storage: 'Storage (monthly average)',
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
      regionsPerDatabase: 'Metered regions per database',
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
      text: 'Paid plans include the allowances. Operation units over the allowance are added to the next subscription invoice at the rate below and storage over it is billed per GB-month; client egress overage is not billed yet. Free stops new writes at its operation allowance and has no overage charges.',
      headItem: 'Item',
      headPricing: 'Pricing',
      headDetails: 'Details',
      rows: [
        {
          item: 'Operation units',
          unit: '/million units',
          details: 'Each started 4 KiB successfully written counts separately; every changed row counts at least one unit.',
        },
        {
          item: 'Storage',
          unit: '/GB-month',
          details: 'Decimal stored GB, averaged over time during the month.',
        },
        {
          item: 'Client egress',
          unit: '/GB',
          details: 'Decimal outbound application response bytes to clients.',
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
          a: 'A write unit is each started 4 KiB (4,096 bytes) successfully written. Every changed row counts at least one unit. SQL reads are not billed and count toward monthly fair use; shape reads served from the edge are free. Failed, denied, invalid, throttled and not-found requests are not billed.',
        },
        {
          q: 'What if I need more operations, storage, or egress?',
          a: 'Paid plans include monthly allowances. Operation units over the allowance are added to the next subscription invoice at the overage rate and storage over it is billed per GB-month; client egress overage is not billed yet. Free stops new writes at its operation allowance and has no overage charges.',
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
        { q: 'How are extra regions billed?', a: 'Writes and stored bytes are metered once per database, in the region that orders its writes. Copies in the other regions you choose have no separate charge. Plan write caps and SQL read fair use apply.' },
      ],
      contactSales: 'Contact Sales',
    },
    finalCta: {
      title: 'Ready to get started?',
      cta: 'Create account',
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
    eyebrow: 'Contact TerraScale',
    titleLead: 'Let’s build',
    titleAccent: 'something together.',
    subtitle:
      'Have a question about your project, need technical help, or want to talk about working together? Get in touch with the team.',
    emailCta: 'Email our team',
    topicsTitle: 'What can we help you with?',
    topics: [
      {
        title: 'Technical support',
        text: 'Questions about your database, SDK integration, or an unexpected error.',
      },
      {
        title: 'Plans & billing',
        text: 'Help understanding plans, invoices, or which option fits your project.',
      },
      {
        title: 'Work with us',
        text: 'Partnerships, integrations, and other ways to build with TerraScale.',
      },
    ],
    github: {
      title: 'Find us on GitHub',
      text: 'Explore our repositories and SDKs.',
    },
    resourcesTitle: 'Looking for a quick answer?',
    resourcesSubtitle: 'These resources are a good place to start.',
    resources: {
      documentation: {
        title: 'Documentation',
        text: 'Guides and examples to help you build your application.',
      },
      status: {
        title: 'System status',
        text: 'Check availability and updates on ongoing incidents.',
      },
      api: {
        title: 'API reference',
        text: 'Explore endpoints and request formats for your integration.',
      },
    },
    note: 'For technical requests, include the error message and steps to reproduce. Never share passwords or API keys.',
  },

  about: {
    title: 'About TerraScale',
    introduction:
      'TerraScale builds TerraBase, a SQL database service. The dashboard brings together databases, access management and usage.',
    databaseTitle: 'TerraBase',
    databaseText:
      'The service has three regions. A database lives in the regions you choose, and every one of them accepts reads and writes.',
    integrationText:
      'Use the SQLite SQL dialect through PostgreSQL-compatible clients, the HTTP API or the SDKs. Manage databases and API keys in the dashboard.',
    alphaTitle: 'Public Alpha',
    alphaText:
      'TerraScale is in Public Alpha. Features, limits and APIs may change. No SLA applies during Alpha.',
    ctaDocs: 'Read the documentation',
  },

  notFound: {
    title: 'We can’t find that page.',
    backHome: 'Back to Home',
  },
}

export type Messages = typeof en
