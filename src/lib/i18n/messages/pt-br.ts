import type { Messages } from './en'

// Português (Brasil). Annotated against `Messages` so the compiler enforces
// parity with the English dictionary — a missing key or array entry here is a
// build-time error, not a silent English fallback at runtime.

export const ptBr: Messages = {
  meta: {
    title: 'TerraScale | Infraestrutura para Desenvolvedores de Fluxos de Dados',
    description:
      'A TerraScale é uma infraestrutura focada em desenvolvedores para fluxos de banco de dados tipado, autenticação, filas e pagamentos no modelo atual de regiões de lançamento.',
  },

  chrome: {
    brandHome: 'Página inicial da TerraScale',
    nav: {
      home: 'Início',
      about: 'Sobre',
      pricing: 'Preços',
      terms: 'Termos',
      contact: 'Contato',
    },
    docs: 'Docs',
    status: 'Status',
    getStarted: 'Começar',
    toggleNav: 'Abrir menu de navegação',
    language: {
      trigger: 'Idioma',
      currentSuffix: 'Idioma: {label}',
      menu: 'Selecione um idioma',
    },
  },

  footer: {
    tagline:
      'Capacitando equipes com infraestrutura de banco de dados segura e escalável. Escale sua tecnologia sem esforço.',
    rightsReserved: 'Todos os direitos reservados.',
    columns: {
      product: {
        title: 'Produto',
        links: {
          features: 'Recursos',
          pricing: 'Preços',
          changelog: 'Changelog',
          roadmap: 'Roadmap',
          documentation: 'Documentação',
        },
      },
      company: {
        title: 'Empresa',
        links: {
          about: 'Sobre',
          blog: 'Blog',
          contact: 'Contato',
        },
      },
      resources: {
        title: 'Recursos',
        links: {
          docs: 'Docs',
          apiReference: 'Referência da API',
          status: 'Status',
          community: 'Comunidade',
          support: 'Suporte',
        },
      },
      legal: {
        title: 'Jurídico',
        links: {
          terms: 'Termos',
          support: 'Contato',
        },
      },
    },
    social: {
      github: 'GitHub',
      discord: 'Discord',
      x: 'X',
      linkedin: 'LinkedIn',
      email: 'E-mail',
    },
  },

  hero: {
    brandAlt: 'TerraScale',
    headlineLead: 'Escale seus dados,',
    headlineAccent: 'sem esforço.',
    subtitle:
      'Infraestrutura de banco de dados tipado, autenticação, filas e pagamentos para equipes que constroem sobre a API da TerraScale.',
    ctaPrimary: 'Começar grátis',
    ctaSecondary: 'Ver documentação',
    highlightsAria: 'Principais recursos',
    highlights: ['Consultas TQL', 'Filas de projeto', 'Regiões de lançamento'],
  },

  features: {
    eyebrow: 'Plataforma',
    titleLead: 'Feito para escalar,',
    titleAccent: 'desenhado para desenvolvedores',
    subtitle:
      'A superfície implementada abrange dados, autenticação, filas, pagamentos, SDKs e fluxos do painel.',
    items: [
      {
        title: 'API de dados tipados',
        text: 'Crie bancos de dados, repositórios, esquemas e documentos e consulte com TQL por meio de uma única API pública.',
        bullets: ['Repositórios', 'TQL', 'Clientes SDK'],
      },
      {
        title: 'Autenticação e acesso',
        text: 'Fluxos de conta, sessão, passkey, MFA, organização, convite e chaves de API com escopo compartilham a mesma superfície da plataforma.',
        bullets: ['Passkeys', 'MFA', 'Chaves com escopo'],
      },
      {
        title: 'Regiões de lançamento',
        text: 'Use as regiões de lançamento do produto San Francisco, Frankfurt e Guarulhos por meio de fluxos do painel e do SDK.',
        bullets: ['sfo', 'fra', 'gru'],
      },
      {
        title: 'Filas e pagamentos',
        text: 'Os fluxos de fila rodam no JetStream e os fluxos de cobrança do Stripe são processados pelo TerraScale Payments.',
        bullets: ['NATS JetStream', 'Stripe', 'Webhooks'],
      },
    ],
  },

  code: {
    eyebrow: 'SDK para desenvolvedores',
    headlineLead: 'Consulte seus dados',
    headlineAccent: 'na linguagem que você ama',
    text: 'SDKs de primeira classe em C# e TypeScript usam as mesmas rotas HTTP públicas do painel, com modelos OpenAPI gerados por baixo.',
    bullets: [
      'Catálogo de contratos OpenAPI gerado',
      'Clientes tipados em C# e TypeScript',
      'O painel usa o limite público do SDK',
      'Entradas de região para sfo, fra, gru',
    ],
    copy: 'Copiar',
    copied: 'Copiado',
    copyAria: 'Copiar código',
    tablistLabel: 'Exemplos de linguagem',
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
    eyebrow: 'Preços',
    titleLead: 'Preços simples',
    titleAccent: 'que escalam com VOCÊ',
    subtitle: 'Comece grátis, escale sob demanda. Sem taxas ocultas, sem surpresas.',
    toggleLabel: 'Ciclo de cobrança',
    monthly: 'Mensal',
    annual: 'Anual',
    save: 'Economize 20%',
    badge: 'Mais popular',
    custom: 'Personalizado',
    suffixMonthly: '/mês',
    suffixAnnual: '/ano',
    baseNoteLead: 'a partir de',
    baseNoteTrail: '/mês + uso',
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
      { title: 'Controles de região', text: 'Crie bancos de dados em sfo, fra ou gru' },
      { title: 'Observável por padrão', text: 'Traces e logs do OpenTelemetry nos fluxos principais' },
      { title: 'Proteções de uso', text: 'Planos medidos com controles de cobrança na plataforma' },
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
      titleLead: 'Pronto para',
      titleAccent: 'começar?',
      text: 'Junte-se a milhares de desenvolvedores construindo o futuro com a TerraScale.',
      cta: 'Comece a construir hoje',
    },
  },

  contact: {
    eyebrow: 'Entre em contato',
    titleLead: 'Vamos construir',
    titleAccent: 'algo juntos.',
    subtitle:
      'Tem dúvidas? Precisa de um plano personalizado? Quer integrar com a TerraScale? Adoraríamos ouvir você.',
    formTitle: 'Envie uma mensagem',
    labels: {
      name: 'Nome',
      email: 'E-mail',
      company: 'Empresa',
      message: 'Mensagem',
    },
    placeholders: {
      name: 'Mario Belchior',
      email: 'mario@acme.com',
      company: 'Acme Inc.',
      message: 'Conte sobre seu projeto, tamanho da equipe e o que você espera alcançar...',
    },
    submit: {
      idle: 'Enviar mensagem',
      submitting: 'Enviando…',
      success: 'Mensagem enviada!',
      error: 'Tentar novamente',
      badge: 'Mensagem enviada com sucesso',
    },
    success: 'Obrigado! Sua mensagem foi registrada — responderemos em até 24h.',
    errorNoForm: 'Não foi possível enviar o formulário. Envie um e-mail diretamente para nós.',
    errorSend: 'Algo deu errado ao enviar sua mensagem. Tente novamente ou nos envie um e-mail diretamente.',
    note: 'Suas informações nunca são compartilhadas. Resposta em até 24h.',
    channelsTitle: 'Outras formas de nos contatar',
    channels: [
      { value: 'support@terrascale.tech' },
      { value: 'Entre na nossa comunidade' },
      { value: 'Abrir issues e PRs' },
    ],
    slaHeading: 'SLA de suporte',
    slaBodyLead: 'Clientes Pro têm',
    slaBodyStrong: 'roteamento prioritário',
    slaBodyTrail: 'com primeira resposta em 1 hora durante o horário comercial.',
  },

  about: {
    eyebrow: 'Sobre a TerraScale',
    titleLead: 'Globalmente distribuída,',
    titleAccent: 'infraestrutura de banco de dados.',
    subtitle:
      'A TerraScale existe para fazer com que o DBaaS globalmente distribuído pareça um produto coerente: banco de dados, autenticação e pagamentos construídos juntos sobre a TerraBase, com cobrança baseada em uso e preços fixos entre regiões.',
    ctaPricing: 'Ver preços',
    ctaContact: 'Fale conosco',
    missionEyebrow: 'Missão',
    missionTitle: 'Uma única superfície de plataforma para equipes que entregam entre regiões.',
    missionText:
      'As equipes não deveriam precisar costurar um banco de dados, uma camada de identidade, um sistema de cobrança e um plano de implantação regional antes de conseguir atender clientes. A TerraScale reúne essas preocupações em uma única plataforma voltada para desenvolvedores, para que as equipes de produto comecem pequeno, escolham as regiões que importam e escalem sem mudar seu modelo de aplicação.',
    pillars: [
      {
        title: 'Fundação TerraBase',
        text: 'A TerraScale é construída sobre a TerraBase para que as equipes executem fluxos de dados duráveis de documentos, consultas e operações por trás de uma única superfície pública da plataforma.',
      },
      {
        title: 'Autenticação inclusa',
        text: 'Acesso ao banco de dados, usuários, sessões, convites e credenciais de máquina são planejados como um único limite de produto, em vez de código de cola separado.',
      },
      {
        title: 'Cobrança consciente do uso',
        text: 'Os planos continuam previsíveis enquanto o uso medido permite que as equipes cresçam por carga de trabalho, não negociando preço região por região.',
      },
    ],
    pricingPrincipleEyebrow: 'Princípio de preços',
    bandTitle: 'Preços regionais fixos, medidos pelo uso real.',
    bandText:
      'A escolha de região deve ser uma decisão de arquitetura, não um item surpresa na fatura. O preço público da TerraScale é desenhado em torno dos mesmos valores de plano entre as regiões de lançamento, com armazenamento, operações e transferência crescendo conforme a carga de trabalho.',
    regions: ['San Francisco', 'Frankfurt', 'Guarulhos'],
  },

  notFound: {
    eyebrow: 'Página não encontrada',
    title: 'Não conseguimos encontrar essa página.',
    text: 'A página que você procura foi movida ou nunca existiu. Vamos colocá-lo de volta no caminho.',
    backHome: 'Voltar ao início',
    contactSupport: 'Contatar suporte',
    linksLabel: 'Páginas populares:',
    links: {
      home: 'Início',
      pricing: 'Preços',
      contact: 'Contato',
    },
  },
}
