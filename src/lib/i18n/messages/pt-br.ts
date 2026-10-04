import type { Messages } from './en'

export const ptBr: Messages = {
  meta: {
    title: 'TerraScale Public Alpha | Banco de Documentos Regional',
    description:
      'O TerraScale Public Alpha oferece um banco de documentos tipado com replicação assíncrona em São Paulo, San Jose e Frankfurt.',
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
    login: 'Entrar',
    getStarted: 'Começar',
    prelaunch: 'Public Alpha',
    toggleNav: 'Abrir menu de navegação',
    language: {
      trigger: 'Idioma',
      currentSuffix: 'Idioma: {label}',
      menu: 'Selecione um idioma',
    },
  },

  footer: {
    tagline:
      'O TerraScale Public Alpha oferece um banco de documentos regional sem percentual de disponibilidade nem SLA. A disponibilidade atual é publicada na página de status.',
    rightsReserved: 'Todos os direitos reservados.',
    columns: {
      product: {
        title: 'Produto',
        links: {
          features: 'Recursos',
          pricing: 'Preços',
          changelog: 'Changelog',
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
    headlineLead: 'TerraScale',
    headlineAccent: 'Public Alpha',
    subtitle:
      'Um banco de documentos tipado com replicação assíncrona em São Paulo, San Jose e Frankfurt. O endpoint global encaminha para a região selecionada saudável mais próxima. O Public Alpha não tem percentual de disponibilidade nem SLA.',
    ctaPrimary: 'Cadastre-se no Public Alpha',
    ctaPrelaunch: 'Public Alpha',
    ctaSecondary: 'Ver documentação',
    highlightsAria: 'Principais recursos',
    highlights: ['Roteamento por região selecionada', 'Replicação assíncrona', 'SDKs tipados'],
  },

  features: {
    eyebrow: 'Plataforma',
    titleLead: 'Public Alpha,',
    titleAccent: 'com limites explícitos',
    subtitle:
      'O contrato de lançamento cobre documentos, três regiões selecionáveis, clientes tipados e saúde visível sem compromisso de disponibilidade.',
    items: [
      {
        title: 'API de dados tipados',
        text: 'Crie bancos de dados, repositórios, esquemas e documentos e consulte com TQL por meio de uma única API pública.',
        bullets: ['Repositórios', 'TQL', 'Clientes SDK'],
      },
      {
        title: 'Roteamento regional',
        text: 'O endpoint global escolhe a região selecionada saudável mais próxima. Endpoints regionais estritos nunca atendem de outra região.',
        bullets: ['Endpoint global', 'Endpoints regionais estritos', 'Regiões selecionadas'],
      },
      {
        title: 'Regiões de lançamento',
        text: 'Escolha São Paulo, San Jose ou Frankfurt durante o Public Alpha.',
        bullets: ['sfo', 'fra', 'gru'],
      },
      {
        title: 'Sinais medidos',
        text: 'Saúde da replicação, convergência, conflitos, latência da API e estimativas geográficas continuam sendo valores separados. O conteúdo do cliente permanece nas regiões selecionadas. O tráfego do cliente exige TLS 1.2 ou mais recente, os dados em repouso usam criptografia qualificada e cada solicitação ainda exige autorização.',
        bullets: ['Hora da observação', 'Rótulos de evidência', 'Sem promessa de atraso limitado'],
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
      'Entradas de região para gru, sfo, fra',
    ],
    copy: 'Copiar',
    copied: 'Copiado',
    copyAria: 'Copiar código',
    tablistLabel: 'Exemplos de linguagem',
  },

  cta: {
    headlineLead: 'Cadastre-se no',
    headlineAccent: 'TerraScale Public Alpha.',
    text: 'Starter e Pro oferecem um teste elegível de 30 dias por método de pagamento verificado e organização.',
    ctaPrimary: 'Cadastre-se no Public Alpha',
    ctaPrelaunch: 'Public Alpha',
    ctaSecondary: 'Fale com vendas',
    perksAria: 'Detalhes dos planos',
    perks: ['Planos pagos mensais', 'Sem excedentes pagos'],
  },

  pricing: {
    eyebrow: 'Preços',
    titleLead: 'Preços simples',
    titleAccent: 'que escalam com VOCÊ',
    subtitle: 'Hobby, Starter e Pro para o Public Alpha. Enterprise continua indisponível.',
    badge: 'Mais popular',
    toggleLabel: 'Ciclo de cobrança',
    monthly: 'Mensal',
    annual: 'Anual',
    save: 'Dois meses grátis',
    suffixMonthly: '/mês',
    suffixAnnual: '/ano',
    plans: {
      hobby: {
        description: 'Plano gratuito para construir e avaliar',
        cta: 'Começar com Hobby',
      },
      starter: {
        description: 'Plano mensal com um teste elegível de 30 dias',
        cta: 'Escolher Starter',
      },
      pro: {
        description: 'Plano mensal com um teste elegível de 30 dias',
        cta: 'Escolher Pro',
      },
      enterprise: {
        description: 'Plano de contrato futuro',
        features: ['Base mensal de suporte mais uso medido', 'Indisponível durante o Public Alpha'],
        cta: 'Contato para acesso futuro',
      },
    },
    limits: {
      projects: ['{n} projeto', '{n} projetos'],
      databases: ['{n} banco de dados', '{n} bancos de dados'],
      members: ['{n} membro', '{n} membros'],
      storage: '{n} entre regiões',
      operationUnits: '{n} unidades de operação/mês',
    },
    triad: [
      { title: 'Armazenamento entre regiões', text: 'Os bytes contam uma vez em cada região selecionada' },
      { title: 'Unidades de operação', text: 'Cada 4 KiB iniciados lidos ou gravados contam como uma unidade' },
      { title: 'Limites rígidos', text: 'Leituras, exportações, exclusão e redução de uso continuam disponíveis' },
    ],
    detailed: {
      eyebrow: 'Regras de cobrança',
      titleLead: 'Public Alpha',
      titleAccent: 'sem excedentes pagos',
      text: 'O armazenamento soma os bytes em cada região selecionada. As unidades de operação arredondam cada documento separadamente.',
      headItem: 'Item',
      headPricing: 'Preço',
      headDetails: 'Detalhes',
      rows: [
        {
          item: 'Franquias dos planos',
          unit: '',
          details: 'Cada plano inclui as franquias listadas de armazenamento entre regiões e unidades mensais de operação.',
        },
        {
          item: 'Comportamento no limite',
          unit: '',
          details: 'Novos trabalhos cobrados param. Leituras, exportações, exclusão e redução de uso continuam disponíveis.',
        },
        {
          item: 'Base de suporte Enterprise',
          unit: '/mês + uso',
          details: 'Enterprise está indisponível durante o Public Alpha e não tem ação de compra.',
        },
      ],
      docsLink: 'Documentação completa de preços',
    },
    faq: {
      titleLead: 'Perguntas',
      titleAccent: 'frequentes',
      subtitle: 'Respostas diretas sobre os planos do Public Alpha.',
      items: [
        {
          q: 'O que é uma unidade de operação?',
          a: 'Uma unidade de operação é cada 4 KiB (4.096 bytes) iniciados de um objeto lógico lido ou gravado com sucesso. Listas, consultas e lotes contam cada item separadamente. Gravações contam uma vez por região selecionada; leituras contam apenas na região que as atende. Solicitações com falha, negadas, inválidas, limitadas e não encontradas não são cobradas.',
        },
        {
          q: 'O que acontece ao atingir um limite?',
          a: 'O Public Alpha usa limites rígidos, não excedentes pagos. Leituras, exportações, exclusão e redução de uso continuam disponíveis.',
        },
        {
          q: 'Quando posso trocar de plano?',
          a: 'Proprietários da organização podem trocar planos mensais pelo Dashboard. Planos pagos renovam mensalmente. O cancelamento inicia 30 dias de leitura quando o uso excede o plano.',
        },
        {
          q: 'Posso exportar meus dados?',
          a: 'Sim. Leituras, exportação, exclusão e redução de uso permanecem disponíveis durante limites e períodos de leitura.',
        },
        {
          q: 'O que inclui o plano Enterprise?',
          a: 'Enterprise tem uma base mensal de suporte de US$ {enterprisePriceUsd} mais uso medido, mas está indisponível durante o Public Alpha.',
        },
      ],
      footerTitle: 'Ainda tem dúvidas?',
      footerText: 'Starter responde em até dois dias úteis. Pro responde em até um dia útil.',
      contactSales: 'Falar com vendas',
      readDocs: 'Ler documentação',
    },
    finalCta: {
      titleLead: 'Pronto para',
      titleAccent: 'começar?',
      text: 'Cadastre-se pelo fluxo canônico do Dashboard TerraScale.',
      cta: 'Cadastre-se no Public Alpha',
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
    success: 'Obrigado! Sua mensagem foi enviada com segurança para nossa equipe.',
    errorNoForm: 'Não foi possível enviar o formulário. Envie um e-mail diretamente para nós.',
    errorSend: 'Algo deu errado ao enviar sua mensagem. Tente novamente ou nos envie um e-mail diretamente.',
    note: 'O tráfego do cliente exige TLS 1.2 ou mais recente. Dados em repouso usam criptografia qualificada pelo serviço.',
    channelsTitle: 'Outras formas de nos contatar',
    channels: [
      { value: 'support@terrascale.tech' },
      { value: 'Entre na nossa comunidade' },
      { value: 'Abrir issues e PRs' },
    ],
  },

  about: {
    eyebrow: 'Sobre a TerraScale',
    titleLead: 'Globalmente distribuída,',
    titleAccent: 'infraestrutura de banco de dados.',
    subtitle:
      'O TerraScale Public Alpha oferece um banco de documentos em São Paulo, San Jose e Frankfurt com replicação assíncrona.',
    ctaPricing: 'Ver preços',
    ctaContact: 'Fale conosco',
    missionEyebrow: 'Missão',
    missionTitle: 'Uma única superfície de plataforma para equipes que entregam entre regiões.',
    missionText:
      'Os projetos escolhem suas regiões. O endpoint global usa a região selecionada saudável mais próxima. Endpoints regionais estritos não atendem de outra região.',
    pillars: [
      {
        title: 'Fundação TerraBase',
        text: 'A TerraScale é construída sobre a TerraBase para que as equipes executem fluxos de dados duráveis de documentos, consultas e operações por trás de uma única superfície pública da plataforma.',
      },
      {
        title: 'Replicação assíncrona',
        text: 'Uma escrita bem-sucedida confirma aceitação local. A replicação para outras regiões selecionadas acontece depois.',
      },
      {
        title: 'Cobrança consciente do uso',
        text: 'Os planos usam franquias explícitas de armazenamento entre regiões e unidades de operação, sem excedentes pagos durante o Public Alpha.',
      },
    ],
    pricingPrincipleEyebrow: 'Princípio de preços',
    bandTitle: 'Preços regionais fixos, medidos pelo uso real.',
    bandText:
      'O armazenamento soma os bytes em cada região selecionada. Distância de rede estimada, latência medida da API e saúde da replicação continuam separadas.',
  },

  notFound: {
    eyebrow: 'Página não encontrada',
    title: 'Não conseguimos encontrar essa página.',
    text: 'A página que você procura foi movida ou nunca existiu. Vamos colocá-lo de volta no caminho.',
    backHome: 'Voltar ao início',
    contactSupport: 'Contatar suporte',
  },
}
