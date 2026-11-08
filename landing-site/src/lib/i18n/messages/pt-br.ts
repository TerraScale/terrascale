import type { Messages } from './en'

export const ptBr: Messages = {
  meta: {
    title: 'TerraScale Public Alpha | Banco de dados SQL regional',
    description:
      'TerraScale Public Alpha: bancos SQL com uma região de escrita e réplicas de leitura criptografadas em São Paulo, San Francisco Bay Area e Frankfurt.',
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
    headline: 'Banco de dados multirregião',
    cta: 'Comece grátis',
  },


  features: {
    titleLead: 'Bancos de dados SQL',
    titleAccent: 'e réplicas de leitura.',
    items: [
      { headline: 'Tabelas SQL, índices e chaves estrangeiras' },
      { headline: 'Conexões com psql, HTTP e WebSocket' },
      { headline: 'Réplicas de leitura criptografadas em outras regiões' },
      { headline: 'Shapes filtradas com permissões de leitura por tabela' },
      { headline: 'Bancos para agentes com TTL, forks e chaves limitadas' },
      { headline: 'A exclusão destrói as chaves dos backups' },
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
        a: 'São Paulo (gru), San Francisco Bay Area (sjc) e Frankfurt (fra). Escolha uma região principal para escritas e outras regiões para réplicas de leitura criptografadas.',
      },
      {
        q: 'Meu banco de dados é copiado para outras regiões?',
        a: 'Sim. Escolha regiões de réplica para manter cópias criptografadas somente leitura fora da região principal. REST, psql e WebSocket podem ler uma réplica local pronta. As réplicas têm consistência eventual. As escritas vão para a região principal a partir de qualquer região em operação.',
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
        a: 'O tráfego do cliente usa TLS e arquivos de banco de dados, réplicas e backups são criptografados em repouso. A exclusão destrói as chaves dos backups e impede sua restauração. A limpeza física é uma etapa separada.',
      },
      {
        q: 'Quanto custa para começar?',
        a: 'Free não exige cartão. Starter, Pro e Team são cobrados mensal ou anualmente. Unidades de operação acima de uma franquia paga vão para a próxima fatura e o armazenamento acima dela é cobrado por GB-mês; o excedente de saída para clientes ainda não é cobrado.',
      },
      {
        q: 'Posso exportar meus dados e sair?',
        a: 'Sim. Exporte pelo painel ou pela API a qualquer momento. Não há taxa de saída para deixar a TerraScale.',
      },
      { q: 'Os clientes podem assinar parte de uma tabela?', a: 'Sim. Shapes ao vivo aceitam filtros e seleção de colunas. Permissões de leitura por tabela permitem que tokens de usuário final leiam as tabelas autorizadas; um filtro não substitui a autorização.' },
      { q: 'Posso dar a um agente seu próprio banco de dados?', a: 'Sim. Crie um banco com TTL, faça um fork e emita uma chave limitada a um banco com acesso de leitura ou leitura e escrita. Ao expirar, o banco é excluído. A restauração para um ponto no tempo em um banco novo está disponível; a restauração no mesmo banco está chegando.' },
      { q: 'Posso limitar o uso?', a: 'Sim. Os planos aplicam tetos mensais de escrita e uso justo de leituras SQL. O teto de escrita interrompe novas escritas; a franquia de leitura interrompe leituras SQL, mas as shapes continuam disponíveis. Tetos de gastos definidos pelo cliente estão chegando.' },
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
    recommended: 'Recomendado',
    titleLead: 'Preços',
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
      { title: 'Armazenamento na região principal' },
      { title: 'Escritas contadas em unidades de 4 KiB' },
      { title: 'Tetos de escrita por plano' },
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
      regionsPerDatabase: 'Regiões principais por banco de dados',
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
      text: 'Planos pagos incluem as franquias. Unidades de operação acima da franquia são adicionadas à próxima fatura da assinatura pela tarifa abaixo e o armazenamento acima dela é cobrado por GB-mês; o excedente de saída para clientes ainda não é cobrado. O Free interrompe novas gravações ao atingir sua franquia de operações e não tem cobranças de excedentes.',
      headItem: 'Item',
      headPricing: 'Preço',
      headDetails: 'Detalhes',
      rows: [
        {
          item: 'Unidades de operação',
          unit: '/milhão de unidades',
          details: 'Cada 4 KiB escritos com sucesso iniciados são contados separadamente; cada linha alterada conta pelo menos uma unidade.',
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
          a: 'Uma unidade de escrita é cada 4 KiB (4.096 bytes) escritos com sucesso, iniciados. Cada linha alterada conta pelo menos uma unidade. Leituras SQL não são cobradas e contam para o uso justo mensal; leituras de shapes pelo edge são gratuitas. Requisições com falha, negadas, inválidas, limitadas ou não encontradas não são cobradas.',
        },
        {
          q: 'E se eu precisar de mais operações, armazenamento ou saída?',
          a: 'Planos pagos incluem franquias mensais. Unidades de operação acima da franquia são adicionadas à próxima fatura da assinatura pela tarifa de excedentes e o armazenamento acima dela é cobrado por GB-mês; o excedente de saída para clientes ainda não é cobrado. O Free interrompe novas gravações ao atingir sua franquia de operações e não tem cobranças de excedentes.',
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
        { q: 'Como as réplicas são cobradas?', a: 'O catálogo inclui uma região principal por banco de dados em todos os planos. Escritas e bytes armazenados são contados na região principal; o armazenamento de réplicas não tem cobrança separada. Os tetos de escrita e o uso justo de leituras SQL se aplicam.' },
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
    eyebrow: 'Contato TerraScale',
    titleLead: 'Vamos construir',
    titleAccent: 'algo juntos.',
    subtitle:
      'Tem uma dúvida sobre seu projeto, precisa de ajuda técnica ou quer conversar sobre uma parceria? Fale com a nossa equipe.',
    emailCta: 'Envie um e-mail',
    topicsTitle: 'Como podemos ajudar?',
    topics: [
      {
        title: 'Suporte técnico',
        text: 'Dúvidas sobre seu banco de dados, integração com o SDK ou um erro inesperado.',
      },
      {
        title: 'Planos e cobrança',
        text: 'Ajuda para entender planos, faturas ou qual opção atende melhor ao seu projeto.',
      },
      {
        title: 'Trabalhe com a gente',
        text: 'Parcerias, integrações e outras formas de construir com a TerraScale.',
      },
    ],
    github: {
      title: 'Encontre a gente no GitHub',
      text: 'Explore nossos repositórios e SDKs.',
    },
    resourcesTitle: 'Procurando uma resposta rápida?',
    resourcesSubtitle: 'Estes recursos são um bom ponto de partida.',
    resources: {
      documentation: {
        title: 'Documentação',
        text: 'Guias e exemplos para ajudar você a construir sua aplicação.',
      },
      status: {
        title: 'Status da plataforma',
        text: 'Confira a disponibilidade e as atualizações sobre incidentes em andamento.',
      },
      api: {
        title: 'Referência da API',
        text: 'Explore endpoints e formatos de requisição para sua integração.',
      },
    },
    note: 'Para pedidos de suporte técnico, inclua a mensagem de erro e os passos para reproduzir o problema. Nunca compartilhe senhas ou chaves de API.',
  },

  about: {
    titleLead: 'Bancos de dados SQL',
    titleAccent: 'em três regiões.',
    ctaPricing: 'Ver preços',
    regionsAria: 'Regiões em operação',
    missionTitle: 'Uma plataforma para bancos de dados, chaves e cobrança.',
    pillars: [
      { title: 'Bancos de dados SQL' },
      { title: 'Uma região de escrita e réplicas criptografadas' },
      { title: 'Tetos de escrita por plano' },
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
