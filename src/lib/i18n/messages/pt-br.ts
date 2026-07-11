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
      security: 'Segurança',
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
          privacy: 'Privacidade',
          terms: 'Termos',
          security: 'Segurança',
          sla: 'SLA',
          compliance: 'Conformidade',
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
    highlights: ['Pronto para IA', '99,99% de Disponibilidade', '20ms de Latência'],
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
        text: 'Crie bancos de dados, coleções, esquemas, entidades e consultas SQL por meio de uma única API pública.',
        bullets: ['Planos de esquema', 'Explicar SQL', 'Consultas SDK'],
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
    headlineLead: 'Experimente a TerraScale',
    headlineAccent: 'grátis por 30 dias.',
    text: 'Configure em minutos e tenha acesso total a todos os recursos dos planos Hobby e Pro. Você não será cobrado até decidir manter o serviço.',
    ctaPrimary: 'Iniciar teste grátis de 30 dias',
    ctaSecondary: 'Falar com vendas',
    perksAria: 'Detalhes do teste',
    perks: ['Acesso a todos os recursos', 'Cancele quando quiser', 'Sem fidelidade'],
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
        description: 'Ideal para projetos pessoais e protótipos',
        features: ['3 bancos de dados', '1M de operações', '10 GB de armazenamento', '15 GB de saída', 'Discord prioritário'],
        cta: 'Experimentar por 30 dias',
      },
      {
        description: 'Melhor para equipes em crescimento e produção',
        features: [
          '15 bancos de dados',
          '10M de operações',
          '50 GB de armazenamento',
          '100 GB de saída',
          'E-mail + Discord',
          'Suporte prioritário',
        ],
        cta: 'Experimentar por 30 dias',
      },
      {
        description: 'Para aplicações de missão crítica',
        features: ['Bancos de dados ilimitados', 'SLAs personalizados', 'Suporte dedicado', 'Segurança avançada', 'Integrações personalizadas'],
        cta: 'Falar com vendas',
      },
    ],
    triad: [
      { title: 'Controles de região', text: 'Crie bancos de dados em sfo, fra ou gru' },
      { title: 'Observável por padrão', text: 'Traces e logs do OpenTelemetry nos fluxos principais' },
      { title: 'Proteções de uso', text: 'Planos medidos com controles de cobrança na plataforma' },
    ],
    detailed: {
      eyebrow: 'Preços detalhados',
      titleLead: 'Preços claros para',
      titleAccent: 'implantações personalizadas',
      text: 'Custos por uso, escopos de suporte e requisitos de implantação estão detalhados abaixo para que equipes corporativas possam estimar gastos antes de falar com vendas.',
      headItem: 'Item',
      headPricing: 'Preço',
      headDetails: 'Detalhes',
      rows: [
        {
          item: 'Suporte nível Enterprise',
          unit: '/mês',
          details: 'Acesso base à plataforma para implantações Enterprise e suporte avançado.',
        },
        {
          item: 'Uso de armazenamento',
          unit: '/GB',
          details: 'Preço de armazenamento por uso além da taxa base da plataforma.',
        },
        {
          item: 'Operações de banco de dados',
          unit: '/ 1M ops',
          details: 'Escala com o volume de trabalho para atividade de leitura, gravação e replicação.',
        },
      ],
      docsLink: 'Documentação completa de preços',
    },
    faq: {
      titleLead: 'Perguntas',
      titleAccent: 'frequentes',
      subtitle: 'Respostas para dúvidas comuns sobre nossos preços e planos.',
      items: [
        {
          q: 'O que é uma Operação de Banco de Dados?',
          a: 'Uma unidade de Operação de Banco de Dados é cada bloco iniciado de 4 KiB (4.096 bytes) de um objeto lógico lido ou gravado com sucesso. Listagens, consultas e lotes contam cada item de forma independente. Gravações contam uma vez por região selecionada; leituras contam apenas na região que atendeu o resultado. Solicitações com falha, negadas, inválidas, limitadas ou não encontradas não são cobradas.',
        },
        {
          q: 'E se eu precisar de mais operações ou armazenamento?',
          a: 'Todos os planos suportam cobrança de excedente nas tarifas de uso (US$ 1 por GB adicional de armazenamento, US$ 1 por milhão de operações adicionais). Você nunca será interrompido no meio de uma requisição — avisamos com antecedência antes de você se aproximar dos limites, para que possa fazer upgrade ou ajustar.',
        },
        {
          q: 'Quando posso alterar minha assinatura?',
          a: 'A qualquer momento. Upgrades entram em vigor imediatamente com cobrança rateada; downgrades valem no início do próximo ciclo de cobrança. Sem fidelidade, sem taxas de cancelamento.',
        },
        {
          q: 'Posso exportar meus dados e sair a qualquer momento?',
          a: 'Sim — seus dados são seus. Exportações completas estão disponíveis a qualquer momento pelo painel ou API, e nunca cobramos um centavo para retirá-los. A única restrição é um limite de taxa por requisição, para que um job de exportação descontrolado não desestabilize o cluster para outros tenants; não há limite de volume total e nenhuma taxa de saída. Se a TerraScale não funcionar para você, você sai com tudo o que colocou.',
        },
        {
          q: 'Vocês oferecem soluções corporativas personalizadas?',
          a: 'Sim. Para equipes com requisitos específicos de conformidade, SLAs personalizados ou infraestrutura dedicada, nosso plano Enterprise é totalmente sob medida. Fale com vendas para dimensionar uma implantação que atenda às suas necessidades.',
        },
        {
          q: 'Por que vocês exigem um cartão de crédito para o teste grátis?',
          a: 'Exigimos um cartão válido para evitar abuso e garantir o uso sério da nossa infraestrutura. Você não será cobrado durante o teste de 30 dias e pode cancelar a qualquer momento. Isso também torna a transição para um plano pago tranquila, caso decida continuar.',
        },
      ],
      footerTitle: 'Ainda tem dúvidas?',
      footerText: 'Nossa equipe está aqui para ajudar você a encontrar o plano ideal para suas necessidades.',
      contactSales: 'Falar com vendas',
      readDocs: 'Ler documentação',
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
