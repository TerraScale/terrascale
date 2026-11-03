import type { Messages } from './en'

export const ptBr: Messages = {
  meta: {
    title: 'TerraScale Public Alpha | Banco de dados SQL regional',
    description:
      'TerraScale Public Alpha oferece bancos de dados SQL, cada um armazenado e servido em uma região principal: São Paulo, San Francisco Bay Area ou Frankfurt.',
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
          support: 'Suporte',
          supportEmail: 'Contato: support@terrascale.tech',
        },
      },
    },
    social: {
      github: 'GitHub',
      linkedin: 'LinkedIn',
      email: 'E-mail',
    },
  },

  hero: {
    headlineLead: 'Banco de dados SQL,',
    headlineAccent: 'na região que você escolher.',
    cta: 'Comece grátis',
  },

  features: {
    titleLead: 'O que o Public Alpha cobre,',
    titleAccent: 'e onde ele para.',
    items: [
      {
        headline: 'SQL com tabelas, índices e chaves estrangeiras',
        text: 'Crie tabelas, índices, views e triggers no dialeto do SQLite. Chaves estrangeiras são sempre aplicadas, colunas JSON funcionam com json_extract e até 256 instruções são confirmadas como uma transação.',
      },
      {
        headline: 'Conecte com psql, HTTP ou a CLI',
        text: 'Clientes Postgres conectam pelo protocolo de rede com uma chave de API como senha. A API HTTP, a CLI e os SDKs executam o mesmo SQL; clientes MCP executam consultas somente leitura.',
      },
      {
        headline: 'Uma região principal por banco de dados',
        text: 'Escolha São Paulo, San Francisco Bay Area ou Frankfurt ao criar um banco de dados. Ele é armazenado e servido nessa região.',
      },
      {
        headline: 'Status publicado, não prometido',
        text: 'A disponibilidade atual e a latência da API são publicadas na página de status. Não há porcentagem de disponibilidade, promessa de latência nem SLA.',
      },
      {
        headline: 'Criptografia em trânsito e em repouso',
        text: 'O tráfego do cliente exige TLS 1.2 ou superior, os dados em repouso usam criptografia qualificada e toda requisição exige autorização.',
      },
    ],
  },

  howItWorks: {
    titleLead: 'Do cadastro à primeira consulta',
    titleAccent: 'em três passos.',
    steps: [
      {
        title: 'Crie um banco de dados',
        text: 'Cadastre-se sem cartão, crie um projeto e adicione um banco de dados na região principal: São Paulo, San Francisco Bay Area ou Frankfurt.',
      },
      {
        title: 'Crie uma chave de API',
        text: 'Limite a chave ao banco de dados. Use-a como senha do Postgres ou troque-a por um token de acesso para HTTP e os SDKs.',
      },
      {
        title: 'Execute SQL',
        text: 'Crie tabelas e consulte-as pelo psql, pela CLI, pelo SDK de C# ou TypeScript ou pela API HTTP.',
      },
    ],
  },


  homeFaq: {
    titleLead: 'Perguntas antes',
    titleAccent: 'de começar.',
    items: [
      {
        q: 'O que é a TerraScale?',
        a: 'Um banco de dados SQL. Cada banco contém tabelas, índices, views e triggers no dialeto do SQLite, com chaves estrangeiras sempre aplicadas. O acesso é pelo protocolo de rede do Postgres, HTTP, a CLI, os SDKs ou MCP.',
      },
      {
        q: 'Quais regiões atendem hoje?',
        a: 'São Paulo (gru), San Francisco Bay Area (sjc) e Frankfurt (fra). Cada banco de dados fica em uma região principal, escolhida ao criá-lo.',
      },
      {
        q: 'Meu banco de dados é copiado para outras regiões?',
        a: 'Hoje não. Um banco de dados é armazenado e servido apenas na região principal. Os backups o protegem contra perda de dados.',
      },
      {
        q: 'Posso usar minhas ferramentas do Postgres?',
        a: 'Sim, pelo protocolo de rede. psql e drivers Postgres conectam a pg.terrascale.tech com uma chave de API como senha. As instruções usam o dialeto do SQLite, então sintaxe exclusiva do Postgres é recusada.',
      },
      {
        q: 'Existe compromisso de disponibilidade?',
        a: 'Ainda não. A Public Alpha não tem porcentagem de disponibilidade, promessa de latência nem SLA. A disponibilidade atual é publicada na página de status.',
      },
      {
        q: 'Quais linguagens e ferramentas posso usar?',
        a: 'Use o psql ou qualquer driver Postgres, os SDKs tipados de C# e TypeScript, a API HTTP ou a CLI. Você também pode conectar Claude Code, Cursor ou qualquer cliente MCP ao servidor MCP da TerraScale com um token de acesso pessoal.',
      },
      {
        q: 'Como meus dados são protegidos?',
        a: 'O tráfego do cliente exige TLS 1.2 ou superior, os dados em repouso usam criptografia qualificada e toda requisição exige autorização.',
      },
      {
        q: 'Quanto custa para começar?',
        a: 'Free não exige cartão. Starter, Pro e Team são cobrados mensal ou anualmente. Unidades de operação acima de uma franquia paga vão para a próxima fatura; excedentes de armazenamento e saída para clientes ainda não são cobrados.',
      },
      {
        q: 'Posso exportar meus dados e sair?',
        a: 'Sim. Exporte pelo painel ou pela API a qualquer momento. Não há taxa de saída para deixar a TerraScale.',
      },
    ],
    contactSales: 'Fale conosco',
  },

  code: {
    headlineLead: 'Consulte seus dados',
    headlineAccent: 'na linguagem que você ama',
    bullets: ['Clientes tipados em C# e TypeScript', 'Protocolo de rede do Postgres para psql e drivers', 'Até 256 instruções em uma transação'],
    agent: {
      title: 'Ou peça ao seu agente de IA favorito para fazer isso por você',
      text: 'Suportamos CLI e MCP. Conecte o Claude Code, o Cursor ou qualquer cliente MCP ao servidor MCP da TerraScale com um token de acesso pessoal. O agente age com os seus papéis e cada chamada é verificada novamente. Agentes também podem chamar a API HTTP pelo terminal.',
      prompts: [
        'Liste meus bancos de dados e suas regiões principais.',
        'Encontre os clientes com 10 ou mais assentos no banco app.',
        'Quanto da cota de unidades de operação deste mês já usamos?',
      ],
    },
    copy: 'Copiar',
    copied: 'Copiado',
    copyAria: 'Copiar código',
    tablistLabel: 'Exemplos de código',
  },

  cta: {
    headlineLead: 'Cadastre-se no',
    headlineAccent: 'TerraScale Public Alpha.',
    ctaPrimary: 'Cadastre-se no Public Alpha',
  },

  pricingTeaser: {
    title: 'Comece com Free. Compare as assinaturas.',
    cta: 'Comparar planos',
  },

  pricing: {
    titleLead: 'Preços simples',
    titleAccent: 'que escalam com VOCÊ',
    suffixMonthly: '/mês',
    suffixAnnual: '/ano',
    intervals: {
      label: 'Intervalo de cobrança',
      month: 'Mensal',
      year: 'Anual',
    },
    plans: {
      free: {
        cta: 'Começar com Free',
      },
      starter: {
        cta: 'Escolher Starter',
      },
      pro: {
        cta: 'Escolher Pro',
      },
      team: {
        cta: 'Escolher Team',
      },
      enterprise: {
        features: ['Consulte condições com vendas', 'Limites e meta de resposta de suporte sob medida'],
        cta: 'Falar com vendas',
      },
    },
    limits: {
      projects: ['{n} projeto', '{n} projetos'],
      databases: ['{n} banco de dados', '{n} bancos de dados'],
      members: ['{n} membro', '{n} membros'],
      storage: '{n} de armazenamento (média mensal)',
      operationUnits: '{n} unidades de operação/mês',
      egress: '{n} de saída para clientes/mês',
    },
    usage: {
      title: 'Armazenamento, operações e saída para clientes.',
    },
    triad: [
      {
        title: 'Armazenamento',
        text: 'GB decimais armazenados, em média ao longo do mês',
      },
      {
        title: 'Unidades de operação',
        text: 'Cada 4 KiB iniciados lidos ou gravados',
      },
      {
        title: 'Franquias e uso adicional',
        text: 'Unidades de operação acima da franquia paga vão para a próxima fatura; excedentes de armazenamento e saída para clientes ainda não são cobrados',
      },
    ],
    compare: {
      title: 'Franquias por plano.',
      head: 'Franquia',
      projects: 'Projetos',
      databases: 'Bancos de dados',
      members: 'Membros',
      storage: 'Armazenamento (média mensal)',
      operationUnits: 'Unidades de operação / mês',
      egress: 'Saída para clientes / mês',
      billing: 'Ciclo de cobrança',
      entitlementsHead: 'Direitos do plano',
      requestsPerSecond: 'Requisições / segundo',
      burstRequests: 'Requisições em rajada',
      concurrentRequests: 'Requisições simultâneas',
      queryTimeout: 'Tempo limite de consulta',
      storageLimit: 'Limite de armazenamento',
      pitr: 'Recuperação para um ponto no tempo',
      backupRetention: 'Retenção de backups',
      regionsPerDatabase: 'Regiões por banco de dados',
      support: 'Meta de resposta do suporte',
      noCeiling: 'Sem limite do plano',
      seconds: '{n} s',
      days: ['{n} dia', '{n} dias'],
      supportTargets: {
        Community: 'Comunidade',
        '2 business days': '2 dias úteis',
        '1 business day': '1 dia útil',
        '8 hours': '8 horas',
        Custom: 'Sob medida',
      },
      entitlementsNote: 'As taxas de requisições e a simultaneidade valem por organização; requisições além delas recebem HTTP 429 com um cabeçalho Retry-After. O limite de armazenamento é um teto rígido, separado da franquia incluída. As metas de resposta do suporte não são um SLA.',
    },
    detailed: {
      titleLead: 'Tarifas de uso',
      titleAccent: 'aprovadas',
      text: 'Planos pagos incluem as franquias. Unidades de operação acima da franquia são adicionadas à próxima fatura da assinatura pela tarifa abaixo; excedentes de armazenamento e saída para clientes ainda não são cobrados. O Free interrompe novas gravações ao atingir sua franquia de operações e não tem cobranças de excedentes.',
      headItem: 'Item',
      headPricing: 'Preço',
      headDetails: 'Detalhes',
      rows: [
        {
          item: 'Unidades de operação',
          unit: '/milhão de unidades',
          details: 'Cada 4 KiB iniciados de cada leitura ou gravação bem-sucedida contam separadamente.',
        },
        {
          item: 'Armazenamento',
          unit: '/GB-mês',
          details: 'GB decimais armazenados, em média ao longo do mês.',
        },
        {
          item: 'Saída para clientes',
          unit: '/GB',
          details: 'Bytes decimais de respostas da aplicação enviados aos clientes.',
        },
      ],
      docsLink: 'Documentação completa de preços',
    },
    faq: {
      titleLead: 'Perguntas',
      titleAccent: 'frequentes',
      items: [
        {
          q: 'O que é uma unidade de operação?',
          a: 'Uma unidade de operação é cada 4 KiB (4.096 bytes) iniciados de um objeto lógico lido ou gravado com sucesso. Listas, consultas e lotes contam cada item separadamente. Solicitações com falha, negadas, inválidas, limitadas e não encontradas não são cobradas.',
        },
        {
          q: 'E se eu precisar de mais operações, armazenamento ou saída?',
          a: 'Planos pagos incluem franquias mensais. Unidades de operação acima da franquia são adicionadas à próxima fatura da assinatura pela tarifa de excedentes; excedentes de armazenamento e saída para clientes ainda não são cobrados. O Free interrompe novas gravações ao atingir sua franquia de operações e não tem cobranças de excedentes.',
        },
        {
          q: 'Como as assinaturas são cobradas?',
          a: 'Free não exige cartão. Starter, Pro e Team são cobrados por mês ou por ano; um ano custa dez mensalidades, então a cobrança anual economiza dois meses. Não há testes gratuitos.',
        },
        {
          q: 'Posso exportar meus dados?',
          a: 'Sim. Leituras, exportação, exclusão e redução de uso permanecem disponíveis durante limites e períodos de leitura.',
        },
        {
          q: 'O que Enterprise inclui?',
          a: 'Enterprise tem uma base mensal de suporte de ${enterprisePriceUsd} mais uso. Consulte as condições com vendas; o Enterprise não é vendido pelo checkout.',
        },
      ],
      contactSales: 'Falar com vendas',
    },
    finalCta: {
      titleLead: 'Pronto para',
      titleAccent: 'começar?',
      cta: 'Cadastre-se no Public Alpha',
    },
    cadence: {
      free: 'Sem cartão de pagamento',
      year: 'Cobrança anual · equivalente a {monthly}/mês · {months} meses grátis',
      month: 'Cobrança mensal',
      both: '{monthly}/mês ou {annual}/ano',
      contact: 'Suporte + uso · fale com vendas',
    },
    suffixUsage: '/mês + uso',
  },

  contact: {
    titleLead: 'Vamos construir',
    titleAccent: 'algo juntos.',
  },

  about: {
    titleLead: 'Bancos de dados SQL',
    titleAccent: 'em três regiões.',
    ctaPricing: 'Ver preços',
    regionsAria: 'Regiões em operação',
    missionTitle: 'Uma plataforma para bancos de dados, chaves e cobrança.',
    pillars: [
      {
        title: 'Fundação TerraBase',
        text: 'A TerraScale é construída sobre o TerraBase, que executa SQL sobre o Turso por trás de uma única superfície pública da plataforma.',
      },
      {
        title: 'Uma região principal',
        text: 'Cada banco de dados é armazenado e servido na região escolhida ao criá-lo.',
      },
      {
        title: 'Cobrança consciente do uso',
        text: 'Free não exige cartão. Planos pagos cobram na próxima fatura as unidades de operação acima da franquia; excedentes de armazenamento e saída para clientes ainda não são cobrados.',
      },
    ],
    bandTitle: 'Preços regionais fixos, medidos pelo uso real.',
    bandText:
      'O armazenamento conta os bytes que cada banco de dados guarda na região principal. Distância de rede estimada e latência medida da API continuam separadas.',
  },

  notFound: {
    title: 'Não conseguimos encontrar essa página.',
    backHome: 'Voltar ao início',
  },
}
