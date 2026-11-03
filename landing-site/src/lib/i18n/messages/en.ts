export const en = {
  meta: {
    title: 'TerraScale Public Alpha | Regional SQL Database',
    description:
      'TerraScale Public Alpha provides SQL databases, each stored and served in one home region: São Paulo, the San Francisco Bay Area, or Frankfurt.',
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
    headlineLead: 'SQL database,',
    headlineAccent: 'in the region you choose.',
    cta: 'Start free',
  },

  features: {
    titleLead: 'What Public Alpha covers,',
    titleAccent: 'and where it stops.',
    items: [
      {
        headline: 'SQL with tables, indexes, and foreign keys',
        text: 'Create tables, indexes, views, and triggers in the SQLite dialect. Foreign keys are always enforced, JSON columns work with json_extract, and up to 256 statements commit as one transaction.',
      },
      {
        headline: 'Connect with psql, HTTP, or the CLI',
        text: 'Postgres clients connect over the wire protocol with an API key as the password. The HTTP API, the CLI, and the SDKs run the same SQL; MCP clients run read-only queries.',
      },
      {
        headline: 'One home region per database',
        text: 'Pick São Paulo, the San Francisco Bay Area, or Frankfurt when you create a database. It is stored and served in that region.',
      },
      {
        headline: 'Status published, not promised',
        text: 'Current availability and API latency are published on the status page. There is no uptime percentage, latency promise, or SLA.',
      },
      {
        headline: 'Encrypted in transit and at rest',
        text: 'Customer traffic requires TLS 1.2 or newer, data at rest uses qualified encryption, and every request requires authorization.',
      },
    ],
  },

  howItWorks: {
    titleLead: 'From sign up to your first query',
    titleAccent: 'in three steps.',
    steps: [
      {
        title: 'Create a database',
        text: 'Register without a card, create a project, and add a database in its home region: São Paulo, the San Francisco Bay Area, or Frankfurt.',
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
        a: 'São Paulo (gru), the San Francisco Bay Area (sjc), and Frankfurt (fra). Each database lives in one home region, chosen when you create it.',
      },
      {
        q: 'Is my database copied to other regions?',
        a: 'Not today. A database is stored and served only in its home region. Backups protect it against data loss.',
      },
      {
        q: 'Can I use my Postgres tools?',
        a: 'Yes, through the wire protocol. psql and Postgres drivers connect to pg.terrascale.tech with an API key as the password. Statements use the SQLite dialect, so Postgres-only syntax is refused.',
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
        a: 'Customer traffic requires TLS 1.2 or newer, data at rest uses qualified encryption, and every request requires authorization.',
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
    bullets: ['Typed C# and TypeScript clients', 'Postgres wire protocol for psql and drivers', 'Up to 256 statements in one transaction'],
    agent: {
      title: 'Or ask your favorite AI agent to do it for you',
      text: 'We support CLI and MCP. Connect Claude Code, Cursor, or any MCP client to the TerraScale MCP server with a personal access token. The agent acts with your roles, and every call is checked again. Agents can also call the HTTP API from a shell.',
      prompts: [
        'List my databases and their home regions.',
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
      storage: '{n} storage (monthly average)',
      operationUnits: '{n} operation units/month',
      egress: '{n} client egress/month',
    },
    usage: {
      title: 'Storage, operations, and client egress.',
    },
    triad: [
      {
        title: 'Storage',
        text: 'Decimal GB stored, averaged over the month',
      },
      {
        title: 'Operation units',
        text: 'Each started 4 KiB read or written',
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
          details: 'Each started 4 KiB of each successful read or write counts separately.',
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
          a: 'One operation unit is each started 4 KiB (4,096 bytes) of a logical object successfully read or written. Lists, queries, and batches count each item independently. Failed, denied, invalid, throttled, and not-found requests are not billed.',
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
    titleLead: 'SQL databases',
    titleAccent: 'in three regions.',
    ctaPricing: 'View Pricing',
    regionsAria: 'Serving regions',
    missionTitle: 'One platform for databases, keys, and billing.',
    pillars: [
      {
        title: 'TerraBase foundation',
        text: 'TerraScale is built on TerraBase, which runs SQL on Turso behind one public platform surface.',
      },
      {
        title: 'One home region',
        text: 'Each database is stored and served in the region you pick when you create it.',
      },
      {
        title: 'Usage-aware billing',
        text: 'Free requires no card. Paid plans bill operation units over the allowance on the next invoice; storage and client egress overages are not billed yet.',
      },
    ],
    bandTitle: 'Flat regional pricing, metered by real use.',
    bandText:
      'Storage usage counts the bytes each database stores in its home region. Estimated network distance and measured API latency remain separate values.',
  },

  notFound: {
    title: 'We can’t find that page.',
    backHome: 'Back to Home',
  },
}

export type Messages = typeof en
