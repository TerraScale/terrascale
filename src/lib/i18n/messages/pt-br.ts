import type { Messages } from './en'

export const ptBr: Messages = {
  meta: {
    title: 'TerraScale Public Alpha | Banco de Documentos Regional',
    description:
      'O TerraScale Public Alpha oferece um banco de documentos tipado com replicação assíncrona em São Paulo, San Francisco Bay Area e Frankfurt.',
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
    getStarted: 'Comece a construir',
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
          terms: 'Termos',
          privacy: 'Privacidade',
          dpa: 'DPA',
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
    eyebrow: 'Public Alpha',
    headlineLead: 'Banco de dados de documentos tipado,',
    headlineAccent: 'replicado entre regiões.',
    subtitle:
      'Um banco de documentos tipado com replicação assíncrona em São Paulo, San Francisco Bay Area e Frankfurt. O endpoint global encaminha para a região selecionada saudável mais próxima. O Public Alpha não tem percentual de disponibilidade nem SLA.',
    ctaPrelaunch: 'Public Alpha',
    highlightsAria: 'Principais recursos',
    highlights: ['Roteamento por região selecionada', 'Replicação assíncrona', 'SDKs tipados', 'Pronto para agentes de IA'],
  },

  features: {
    eyebrow: 'Plataforma',
    titleLead: 'Public Alpha,',
    titleAccent: 'com limites explícitos',
    subtitle:
      'O contrato de lançamento cobre documentos, três regiões ativas, clientes tipados e saúde visível sem compromisso de disponibilidade.',
    items: [
      {
        title: 'Dados',
        text: 'Bancos de dados guardam coleções de documentos JSON, cada uma com um esquema opcional. Consulte com filtros JSON ou SQL somente leitura: SELECT, WHERE, ORDER BY e até 1.000 linhas por instrução.',
        bullets: ['Coleções', 'Filtros JSON', 'SQL somente leitura'],
      },
      {
        title: 'Regiões',
        text: 'São Paulo, San Francisco Bay Area e Frankfurt já atendem tráfego. Cada banco vive em uma região principal e é replicado por completo nas outras regiões que você selecionar.',
        bullets: ['gru', 'sjc', 'fra'],
      },
      {
        title: 'Roteamento',
        text: 'O endpoint global responde da região saudável mais próxima que tem o seu banco. Endpoints regionais estritos nunca atendem de uma região que você não selecionou.',
        bullets: ['Endpoint global', 'Endpoints regionais estritos'],
      },
      {
        title: 'Saúde',
        text: 'A replicação é assíncrona. Atraso, convergência, conflitos e latência da API são publicados como valores medidos separados, com o horário da observação. Não há promessa de atraso limitado, porcentagem de disponibilidade nem SLA.',
        bullets: ['Replicação assíncrona', 'Horário da observação', 'Sem SLA'],
      },
      {
        title: 'Segurança',
        text: 'O tráfego do cliente exige TLS 1.2 ou superior, os dados em repouso usam criptografia qualificada e toda requisição exige autorização. O conteúdo do cliente permanece nas regiões que você selecionar.',
        bullets: ['TLS 1.2+', 'Criptografia em repouso', 'Autorização por requisição'],
      },
    ],
  },

  code: {
    eyebrow: 'SDK para desenvolvedores',
    headlineLead: 'Consulte seus dados',
    headlineAccent: 'na linguagem que você ama',
    text: 'SDKs tipados em C# e TypeScript chamam a mesma API HTTP pública usada pelo painel, com modelos gerados a partir do contrato OpenAPI.',
    bullets: ['Clientes tipados em C# e TypeScript', 'Modelos OpenAPI gerados', 'Filtros JSON ou SQL somente leitura'],
    agent: {
      title: 'Ou peça ao seu agente de IA favorito para fazer isso por você',
      text: 'Suportamos CLI e MCP. Conecte o Claude Code, o Cursor ou qualquer cliente MCP ao servidor MCP da TerraScale com um token de acesso pessoal. O agente age com os seus papéis e cada chamada é verificada novamente. Agentes também podem chamar a API HTTP pelo terminal.',
      promptsLabel: 'Experimente pedir',
      prompts: [
        'Liste meus bancos de dados e as regiões que cada um atende.',
        'Encontre os clientes com 10 ou mais assentos no banco app.',
        'Quanto da cota de unidades de operação deste mês já usamos?',
      ],
      toolsLabel: 'Algumas ferramentas MCP',
    },
    copy: 'Copiar',
    copied: 'Copiado',
    copyAria: 'Copiar código',
    tablistLabel: 'Exemplos de código',
  },

  cta: {
    headlineLead: 'Cadastre-se no',
    headlineAccent: 'TerraScale Public Alpha.',
    text: 'Starter e Pro oferecem um teste elegível de 30 dias por método de pagamento verificado e organização.',
    ctaPrimary: 'Cadastre-se no Public Alpha',
    ctaSecondary: 'Fale com vendas',
    perksAria: 'Detalhes dos planos',
    perks: ['Planos pagos mensais', 'Sem excedentes pagos'],
  },

  pricingTeaser: {
    eyebrow: 'Preços',
    title: 'Comece grátis. Limites rígidos, sem excedentes.',
    cta: 'Comparar planos',
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
    usage: { eyebrow: 'Como o uso é medido', title: 'Armazenamento e unidades de operação.' },
    triad: [
      { title: 'Armazenamento entre regiões', text: 'Os bytes contam uma vez em cada região selecionada' },
      { title: 'Unidades de operação', text: 'Cada 4 KiB iniciados lidos ou gravados contam como uma unidade' },
      { title: 'Limites rígidos', text: 'Leituras, exportações, exclusão e redução de uso continuam disponíveis' },
    ],
    compare: {
      included: 'Incluído',
      hardLimit: 'Limite rígido',
      eyebrow: 'Comparar',
      title: 'Limites do plano.',
      head: 'Limite',
      projects: 'Projetos',
      databases: 'Bancos de dados',
      members: 'Membros',
      storage: 'Armazenamento entre regiões',
      operationUnits: 'Unidades de operação / mês',
      trial: 'Teste de 30 dias',
      trialIncluded: 'Um por método de pagamento verificado e organização',
      support: 'Meta de suporte',
      supportStarter: '2 dias úteis',
      supportPro: '1 dia útil',
    },
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
      eyebrow: 'Perguntas frequentes',
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
    channelsTitle: 'Fale conosco',
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
      'O TerraScale Public Alpha oferece um banco de documentos em São Paulo, San Francisco Bay Area e Frankfurt com replicação assíncrona.',
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
