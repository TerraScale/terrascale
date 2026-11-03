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
    headlineLead: 'Banco de dados de documentos tipado,',
    headlineAccent: 'replicado entre regiões.',
    cta: 'Comece grátis',
  },

  features: {
    titleLead: 'O que o Public Alpha cobre,',
    titleAccent: 'e onde ele para.',
    items: [
      {
        headline: 'Consulte documentos JSON com filtros ou SQL',
        text: 'Bancos de dados guardam coleções de documentos JSON, cada uma com um esquema opcional. Consulte com filtros JSON ou SQL somente leitura: SELECT, WHERE, ORDER BY e até 1.000 linhas por instrução.',
      },
      {
        headline: 'Mantenha cada banco nas regiões que você escolher',
        text: 'São Paulo, San Francisco Bay Area e Frankfurt já atendem tráfego. Cada banco vive em uma região principal e é replicado por completo nas outras regiões que você selecionar.',
      },
      {
        headline: 'Atenda leituras pela região saudável mais próxima',
        text: 'O endpoint global responde da região saudável mais próxima que tem o seu banco. Endpoints regionais estritos nunca atendem de uma região que você não selecionou.',
      },
      {
        headline: 'Veja a saúde da replicação como valores medidos',
        text: 'A replicação é assíncrona. Atraso, convergência, conflitos e latência da API são publicados como valores medidos separados, com o horário da observação. Não há promessa de atraso limitado, porcentagem de disponibilidade nem SLA.',
      },
      {
        headline: 'Criptografia em trânsito e em repouso',
        text: 'O tráfego do cliente exige TLS 1.2 ou superior, os dados em repouso usam criptografia qualificada e toda requisição exige autorização. O conteúdo do cliente permanece nas regiões que você selecionar.',
      },
    ],
  },

  howItWorks: {
    titleLead: 'Do cadastro a leituras globais',
    titleAccent: 'em três passos.',
    steps: [
      {
        title: 'Crie um banco de dados',
        text: 'Cadastre-se sem cartão, crie um projeto e adicione um banco. Escolha a região principal: São Paulo, San Francisco Bay Area ou Frankfurt.',
      },
      {
        title: 'Selecione regiões de réplica',
        text: 'Adicione as outras regiões que devem guardar uma cópia completa. Uma gravação confirma o aceite local e depois é replicada de forma assíncrona para cada região selecionada.',
      },
      {
        title: 'Leia e grave',
        text: 'Use o endpoint global para a região selecionada saudável mais próxima, ou um endpoint regional estrito que nunca atende de outra região. Chame pelo SDK de C# ou TypeScript, pela API HTTP ou por um cliente MCP.',
      },
    ],
  },


  homeFaq: {
    titleLead: 'Perguntas antes',
    titleAccent: 'de começar.',
    items: [
      {
        q: 'O que é a TerraScale?',
        a: 'Um banco de dados de documentos tipado. Bancos guardam coleções de documentos JSON, cada uma com um esquema opcional. Você consulta com filtros JSON ou com SQL somente leitura: SELECT, WHERE, ORDER BY e até 1.000 linhas por instrução.',
      },
      {
        q: 'Quais regiões atendem hoje?',
        a: 'São Paulo (gru), San Francisco Bay Area (sjc) e Frankfurt (fra). Cada banco vive em uma região principal e é replicado por completo nas outras regiões que você selecionar.',
      },
      {
        q: 'Como funciona a replicação?',
        a: 'A replicação é assíncrona. Uma gravação bem sucedida confirma o aceite local, e as outras regiões selecionadas a recebem depois. Atraso, convergência, conflitos e latência da API são publicados como valores medidos separados, com o horário da observação.',
      },
      {
        q: 'Qual a diferença entre o endpoint global e os regionais?',
        a: 'O endpoint global responde da região saudável mais próxima que tem o seu banco. Um endpoint regional estrito nunca atende de uma região que você não selecionou.',
      },
      {
        q: 'Existe compromisso de disponibilidade?',
        a: 'Ainda não. O Public Alpha não tem percentual de disponibilidade, promessa de atraso limitado nem SLA. A disponibilidade atual é publicada na página de status.',
      },
      {
        q: 'Quais linguagens e ferramentas posso usar?',
        a: 'SDKs tipados em C# e TypeScript chamam a mesma API HTTP pública usada pelo painel. Você também pode usar a CLI, ou conectar o Claude Code, o Cursor ou qualquer cliente MCP ao servidor MCP da TerraScale com um token de acesso pessoal.',
      },
      {
        q: 'Como meus dados são protegidos?',
        a: 'O tráfego do cliente exige TLS 1.2 ou superior, os dados em repouso usam criptografia qualificada e toda requisição exige autorização. O conteúdo do cliente permanece nas regiões que você selecionar.',
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
    bullets: ['Clientes tipados em C# e TypeScript', 'Modelos OpenAPI gerados', 'Filtros JSON ou SQL somente leitura'],
    agent: {
      title: 'Ou peça ao seu agente de IA favorito para fazer isso por você',
      text: 'Suportamos CLI e MCP. Conecte o Claude Code, o Cursor ou qualquer cliente MCP ao servidor MCP da TerraScale com um token de acesso pessoal. O agente age com os seus papéis e cada chamada é verificada novamente. Agentes também podem chamar a API HTTP pelo terminal.',
      prompts: [
        'Liste meus bancos de dados e as regiões que cada um atende.',
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
      storage: '{n} entre regiões (média mensal)',
      operationUnits: '{n} unidades de operação/mês',
      egress: '{n} de saída para clientes/mês',
    },
    usage: {
      title: 'Armazenamento, operações e saída para clientes.',
    },
    triad: [
      {
        title: 'Armazenamento entre regiões',
        text: 'GB decimais em cada região selecionada, com média ao longo do mês',
      },
      {
        title: 'Unidades de operação',
        text: 'Cada 4 KiB iniciados lidos ou escritos; escritas contam uma vez e a replicação é grátis',
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
      storage: 'Armazenamento entre regiões (média mensal)',
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
          details: 'Cada 4 KiB iniciados de cada leitura ou escrita bem-sucedida contam separadamente. Escritas contam uma vez, independentemente das regiões selecionadas; leituras contam na região que atende. A replicação é grátis.',
        },
        {
          item: 'Armazenamento entre regiões',
          unit: '/GB-região-mês',
          details: 'GB decimais armazenados em cada região selecionada, com média ao longo do mês.',
        },
        {
          item: 'Saída para clientes',
          unit: '/GB',
          details: 'Bytes decimais de respostas da aplicação enviados aos clientes; exclui tráfego de replicação.',
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
          a: 'Uma unidade de operação é cada 4 KiB (4.096 bytes) iniciados de um objeto lógico lido ou gravado com sucesso. Listas, consultas e lotes contam cada item separadamente. Gravações contam uma vez, independentemente do número de regiões selecionadas; leituras contam apenas na região que as atende. A replicação entre regiões não é cobrada. Solicitações com falha, negadas, inválidas, limitadas e não encontradas não são cobradas.',
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
    titleLead: 'Globalmente distribuída,',
    titleAccent: 'infraestrutura de banco de dados.',
    ctaPricing: 'Ver preços',
    regionsAria: 'Regiões em operação',
    missionTitle: 'Uma única superfície de plataforma para equipes que entregam entre regiões.',
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
        text: 'Free não exige cartão. Planos pagos cobram na próxima fatura as unidades de operação acima da franquia; excedentes de armazenamento e saída para clientes ainda não são cobrados.',
      },
    ],
    bandTitle: 'Preços regionais fixos, medidos pelo uso real.',
    bandText:
      'O armazenamento soma os bytes em cada região selecionada. Distância de rede estimada, latência medida da API e saúde da replicação continuam separadas.',
  },

  notFound: {
    title: 'Não conseguimos encontrar essa página.',
    backHome: 'Voltar ao início',
  },
}
